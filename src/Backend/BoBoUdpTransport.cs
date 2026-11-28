using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DualRemote
{
    internal enum BoBoUdpPacketType : byte
    {
        Hello = 1,
        HelloAck = 2,
        VideoShard = 3,
        Audio = 4,
        Input = 5,
        Ping = 6,
        Pong = 7,
        CursorShape = 8,
        Feedback = 9,
        Nack = 10,
        AudioAac = 11
    }

    internal enum BoBoUdpCipherMode : byte
    {
        Aes128CtrHmac = 0,
        Aes128Gcm = 1
    }

    internal enum BoBoCongestionSignal : byte
    {
        Normal = 0,
        Hold = 1,
        Overuse = 2
    }

    /// <summary>
    /// BoBo 加密低延迟 UDP 媒体与输入传输核心：
    /// - 支持标准 AEAD `AES-128-GCM`（128-bit 认证标签 + Windows CNG 原生 bcrypt.dll，默认协商）并向后兼容 v7.2 `AES-128-CTR + HMAC-SHA256`；
    /// - 密钥通过已认证的 TLS 1.2 通道按会话动态下发（32 字节会话主密钥），按方向派生独立子密钥防反射；
    /// - 单包总长严格控制在 ≤1220 字节，严格执行序号上限（0xFFFFFF00）防 Nonce 复用、先验签后更新 512 包防重放窗口，并在 Dispose 时清零密钥材料。
    /// </summary>
    internal sealed class BoBoUdpCrypto : IDisposable
    {
        internal const int HeaderBytes = 9;         // sessionId(4) + seq(4) + pktType(1)
        internal const int TagBytes = 8;            // legacy truncated HMAC-SHA256 (64-bit)
        internal const int GcmTagBytes = 16;        // standard AEAD AES-128-GCM tag (128-bit)
        internal const int OverheadBytes = HeaderBytes + TagBytes;       // legacy min datagram overhead (17B)
        internal const int GcmOverheadBytes = HeaderBytes + GcmTagBytes; // AEAD GCM datagram overhead (25B)
        internal const uint MaxSafeSequence = 0xFFFFFF00u;               // fail-closed before 32-bit wrap / nonce reuse

        private static readonly object CngInitGate = new object();
        private static IntPtr sharedGcmAlgHandle;
        private static int sharedGcmKeyObjLength;

        [StructLayout(LayoutKind.Sequential)]
        private struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
        {
            public int cbSize;
            public int dwInfoVersion;
            public IntPtr pbNonce;
            public int cbNonce;
            public IntPtr pbAuthData;
            public int cbAuthData;
            public IntPtr pbTag;
            public int cbTag;
            public IntPtr pbMacContext;
            public int cbMacContext;
            public int cbAAD;
            public long cbData;
            public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, string pszAlgId, string pszImplementation, uint dwFlags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptSetProperty(IntPtr hObject, string pszProperty, byte[] pbInput, int cbInput, uint dwFlags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptGetProperty(IntPtr hObject, string pszProperty, byte[] pbOutput, int cbOutput, out int pcbResult, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptGenerateSymmetricKey(IntPtr hAlgorithm, out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject, byte[] pbSecret, int cbSecret, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyKey(IntPtr hKey);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptEncrypt(
            IntPtr hKey,
            byte[] pbInput,
            int cbInput,
            ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            byte[] pbIV,
            int cbIV,
            byte[] pbOutput,
            int cbOutput,
            out int pcbResult,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDecrypt(
            IntPtr hKey,
            byte[] pbInput,
            int cbInput,
            ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            byte[] pbIV,
            int cbIV,
            byte[] pbOutput,
            int cbOutput,
            out int pcbResult,
            uint dwFlags);

        private readonly uint sessionId;
        private readonly BoBoUdpCipherMode cipherMode;
        private readonly byte sendDir;
        private readonly byte recvDir;
        private readonly byte[] encAesKey;
        private readonly byte[] encHmacKey;
        private readonly byte[] decAesKey;
        private readonly byte[] decHmacKey;
        private readonly byte[] punchHmacKey;
        private readonly object encGate = new object();
        private readonly object decGate = new object();

        // Legacy CTR+HMAC primitives (when cipherMode == Aes128CtrHmac)
        private readonly AesCryptoServiceProvider encAes;
        private readonly AesCryptoServiceProvider decAes;
        private readonly ICryptoTransform encBlock;
        private readonly ICryptoTransform decBlock;
        private readonly HMACSHA256 encHmac;
        private readonly HMACSHA256 decHmac;
        private byte[] encCtrBatch;
        private byte[] encKsBatch;
        private byte[] decCtrBatch;
        private byte[] decKsBatch;

        // Standard AEAD AES-128-GCM primitives (when cipherMode == Aes128Gcm)
        private IntPtr encGcmKeyHandle;
        private IntPtr encGcmKeyObj;
        private IntPtr decGcmKeyHandle;
        private IntPtr decGcmKeyObj;
        private readonly byte[] encNonceScratch = new byte[12];
        private readonly byte[] decNonceScratch = new byte[12];
        private readonly byte[] encHeaderScratch = new byte[HeaderBytes];
        private readonly byte[] decHeaderScratch = new byte[HeaderBytes];
        private readonly byte[] encTagScratch = new byte[GcmTagBytes];
        private readonly byte[] decTagScratch = new byte[GcmTagBytes];
        private readonly byte[] emptyDummy = new byte[1];

        private uint sendSeq;
        private bool hasRecvSeq;
        private uint maxRecvSeq;
        private readonly ulong[] replayMask = new ulong[8]; // 512-packet sliding anti-replay window
        private volatile bool disposed;

        internal BoBoUdpCrypto(uint sessionId, byte[] key32, bool isHost)
            : this(sessionId, key32, isHost, BoBoUdpCipherMode.Aes128CtrHmac)
        {
        }

        internal BoBoUdpCrypto(uint sessionId, byte[] key32, bool isHost, BoBoUdpCipherMode cipherMode)
        {
            if (key32 == null || key32.Length < 32)
                throw new ArgumentException("UDP session key must be at least 32 bytes.");

            this.sessionId = sessionId;
            this.cipherMode = cipherMode;
            sendDir = isHost ? (byte)1 : (byte)0;
            recvDir = isHost ? (byte)0 : (byte)1;

            if (cipherMode == BoBoUdpCipherMode.Aes128Gcm)
            {
                byte[] h2c = DeriveDirectionKey(key32, sessionId, "BoBo7.3-UDP-GCM-H2C");
                byte[] c2h = DeriveDirectionKey(key32, sessionId, "BoBo7.3-UDP-GCM-C2H");
                byte[] txMat = isHost ? h2c : c2h;
                byte[] rxMat = isHost ? c2h : h2c;

                encAesKey = new byte[16];
                decAesKey = new byte[16];
                encHmacKey = null;
                decHmacKey = null;
                Buffer.BlockCopy(txMat, 0, encAesKey, 0, 16);
                Buffer.BlockCopy(rxMat, 0, decAesKey, 0, 16);
                Array.Clear(h2c, 0, h2c.Length);
                Array.Clear(c2h, 0, c2h.Length);

                CreateCngGcmKey(encAesKey, out encGcmKeyHandle, out encGcmKeyObj);
                try
                {
                    CreateCngGcmKey(decAesKey, out decGcmKeyHandle, out decGcmKeyObj);
                }
                catch
                {
                    DestroyCngGcmKey(ref encGcmKeyHandle, ref encGcmKeyObj);
                    throw;
                }
            }
            else
            {
                byte[] h2c = DeriveDirectionKey(key32, sessionId, "BoBo6.3-UDP-H2C");
                byte[] c2h = DeriveDirectionKey(key32, sessionId, "BoBo6.3-UDP-C2H");
                byte[] txMat = isHost ? h2c : c2h;
                byte[] rxMat = isHost ? c2h : h2c;

                encAesKey = new byte[16];
                encHmacKey = new byte[16];
                decAesKey = new byte[16];
                decHmacKey = new byte[16];
                Buffer.BlockCopy(txMat, 0, encAesKey, 0, 16);
                Buffer.BlockCopy(txMat, 16, encHmacKey, 0, 16);
                Buffer.BlockCopy(rxMat, 0, decAesKey, 0, 16);
                Buffer.BlockCopy(rxMat, 16, decHmacKey, 0, 16);
                Array.Clear(h2c, 0, h2c.Length);
                Array.Clear(c2h, 0, c2h.Length);

                encCtrBatch = new byte[1536];
                encKsBatch = new byte[1536];
                decCtrBatch = new byte[1536];
                decKsBatch = new byte[1536];
                encAes = new AesCryptoServiceProvider { Key = encAesKey, Mode = CipherMode.ECB, Padding = PaddingMode.None };
                decAes = new AesCryptoServiceProvider { Key = decAesKey, Mode = CipherMode.ECB, Padding = PaddingMode.None };
                encBlock = encAes.CreateEncryptor();
                decBlock = decAes.CreateEncryptor();
                encHmac = new HMACSHA256(encHmacKey);
                decHmac = new HMACSHA256(decHmacKey);
            }
            punchHmacKey = DeriveDirectionKey(key32, sessionId, "BoBo7.6-UDP-PUNCH");
        }

        private static void EnsureCngGcmProvider(out IntPtr hAlg, out int objLen)
        {
            lock (CngInitGate)
            {
                if (sharedGcmAlgHandle == IntPtr.Zero)
                {
                    IntPtr alg;
                    int st = BCryptOpenAlgorithmProvider(out alg, "AES", null, 0);
                    if (st != 0) throw new CryptographicException("BCryptOpenAlgorithmProvider(AES) failed: 0x" + st.ToString("X8"));
                    byte[] gcmMode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                    st = BCryptSetProperty(alg, "ChainingMode", gcmMode, gcmMode.Length, 0);
                    if (st != 0) throw new CryptographicException("BCryptSetProperty(ChainingModeGCM) failed: 0x" + st.ToString("X8"));
                    byte[] lenBuf = new byte[4];
                    int cbRes;
                    st = BCryptGetProperty(alg, "ObjectLength", lenBuf, 4, out cbRes, 0);
                    if (st != 0 || cbRes != 4) throw new CryptographicException("BCryptGetProperty(ObjectLength) failed: 0x" + st.ToString("X8"));
                    sharedGcmKeyObjLength = BitConverter.ToInt32(lenBuf, 0);
                    sharedGcmAlgHandle = alg;
                }
                hAlg = sharedGcmAlgHandle;
                objLen = sharedGcmKeyObjLength;
            }
        }

        private static void CreateCngGcmKey(byte[] key16, out IntPtr hKey, out IntPtr hKeyObj)
        {
            IntPtr hAlg;
            int objLen;
            EnsureCngGcmProvider(out hAlg, out objLen);
            hKeyObj = Marshal.AllocHGlobal(objLen);
            hKey = IntPtr.Zero;
            int st = BCryptGenerateSymmetricKey(hAlg, out hKey, hKeyObj, objLen, key16, key16.Length, 0);
            if (st != 0 || hKey == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(hKeyObj);
                hKeyObj = IntPtr.Zero;
                throw new CryptographicException("BCryptGenerateSymmetricKey(AES-GCM) failed: 0x" + st.ToString("X8"));
            }
        }

        private static void DestroyCngGcmKey(ref IntPtr hKey, ref IntPtr hKeyObj)
        {
            if (hKey != IntPtr.Zero)
            {
                try { BCryptDestroyKey(hKey); } catch { }
                hKey = IntPtr.Zero;
            }
            if (hKeyObj != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(hKeyObj);
                hKeyObj = IntPtr.Zero;
            }
        }

        internal static byte[] DeriveDirectionKey(byte[] masterKey32, uint sid, string label)
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            byte[] msg = new byte[labelBytes.Length + 4];
            Buffer.BlockCopy(labelBytes, 0, msg, 0, labelBytes.Length);
            WriteUInt32LE(msg, labelBytes.Length, sid);
            using (var h = new HMACSHA256(masterKey32))
            {
                return h.ComputeHash(msg);
            }
        }

        internal uint SessionId { get { return sessionId; } }
        internal BoBoUdpCipherMode CipherModeKind { get { return cipherMode; } }
        internal int ActiveOverheadBytes { get { return cipherMode == BoBoUdpCipherMode.Aes128Gcm ? GcmOverheadBytes : OverheadBytes; } }

        internal void ForceSendSeqForTest(uint nextBeforeIncrement)
        {
            lock (encGate)
            {
                sendSeq = nextBeforeIncrement;
            }
        }

        internal static byte[] GenerateRandomKey32()
        {
            byte[] key = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }
            return key;
        }

        internal const int StatelessPunchBytes = 32;

        internal byte[] BuildStatelessPunchProbe()
        {
            if (disposed || punchHmacKey == null) return null;
            byte[] pkt = new byte[StatelessPunchBytes];
            WriteUInt32LE(pkt, 0, sessionId);
            pkt[4] = 0x42; // B
            pkt[5] = 0x4F; // O
            pkt[6] = 0x50; // P
            pkt[7] = 0x37; // 7
            WriteInt64LE(pkt, 8, Stopwatch.GetTimestamp());
            using (var h = new HMACSHA256(punchHmacKey))
            {
                byte[] tag = h.ComputeHash(pkt, 0, 16);
                Buffer.BlockCopy(tag, 0, pkt, 16, 16);
            }
            return pkt;
        }

        internal static bool IsPossibleStatelessPunchProbe(byte[] buf, int length)
        {
            return buf != null && length == StatelessPunchBytes &&
                buf[4] == 0x42 && buf[5] == 0x4F && buf[6] == 0x50 && buf[7] == 0x37;
        }

        internal bool TryVerifyStatelessPunchProbe(byte[] buf, int length)
        {
            if (disposed || punchHmacKey == null || !IsPossibleStatelessPunchProbe(buf, length)) return false;
            if (ReadUInt32LE(buf, 0) != sessionId) return false;
            byte[] tag;
            using (var h = new HMACSHA256(punchHmacKey))
            {
                tag = h.ComputeHash(buf, 0, 16);
            }
            uint diff = 0;
            for (int i = 0; i < 16; i++)
            {
                diff |= (uint)(buf[16 + i] ^ tag[i]);
            }
            return diff == 0;
        }

        internal static uint GenerateRandomSessionId()
        {
            byte[] buf = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            uint id = ReadUInt32LE(buf, 0);
            return id == 0 ? 0x52423633u : id;
        }

        internal byte[] Seal(BoBoUdpPacketType type, byte[] plaintext, int offset, int count)
        {
            if (disposed) return null;
            int bodyLen = (plaintext != null && count > 0) ? count : 0;
            int overhead = ActiveOverheadBytes;
            byte[] packet = new byte[overhead + bodyLen];

            lock (encGate)
            {
                if (disposed) return null;
                if (sendSeq >= MaxSafeSequence)
                    throw new CryptographicException("UDP_NONCE_EXHAUSTED");

                uint seq = ++sendSeq;
                WriteUInt32LE(packet, 0, sessionId);
                WriteUInt32LE(packet, 4, seq);
                packet[8] = (byte)type;

                if (cipherMode == BoBoUdpCipherMode.Aes128Gcm)
                {
                    Buffer.BlockCopy(packet, 0, encHeaderScratch, 0, HeaderBytes);
                    Buffer.BlockCopy(packet, 0, encNonceScratch, 0, HeaderBytes);
                    encNonceScratch[9] = sendDir;
                    encNonceScratch[10] = 0;
                    encNonceScratch[11] = 0;

                    byte[] inSlice = bodyLen > 0 ? new byte[bodyLen] : emptyDummy;
                    if (bodyLen > 0) Buffer.BlockCopy(plaintext, offset, inSlice, 0, bodyLen);
                    byte[] outSlice = bodyLen > 0 ? new byte[bodyLen] : emptyDummy;

                    GCHandle hN = GCHandle.Alloc(encNonceScratch, GCHandleType.Pinned);
                    GCHandle hA = GCHandle.Alloc(encHeaderScratch, GCHandleType.Pinned);
                    GCHandle hT = GCHandle.Alloc(encTagScratch, GCHandleType.Pinned);
                    try
                    {
                        var info = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
                        {
                            cbSize = Marshal.SizeOf(typeof(BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO)),
                            dwInfoVersion = 1,
                            pbNonce = hN.AddrOfPinnedObject(),
                            cbNonce = 12,
                            pbAuthData = hA.AddrOfPinnedObject(),
                            cbAuthData = HeaderBytes,
                            pbTag = hT.AddrOfPinnedObject(),
                            cbTag = GcmTagBytes
                        };
                        int cbOut;
                        int st = BCryptEncrypt(encGcmKeyHandle, inSlice, bodyLen, ref info, null, 0, outSlice, bodyLen, out cbOut, 0);
                        if (st != 0) throw new CryptographicException("BCryptEncrypt(AES-GCM) failed: 0x" + st.ToString("X8"));
                    }
                    finally
                    {
                        hN.Free();
                        hA.Free();
                        hT.Free();
                    }
                    if (bodyLen > 0) Buffer.BlockCopy(outSlice, 0, packet, HeaderBytes, bodyLen);
                    Buffer.BlockCopy(encTagScratch, 0, packet, HeaderBytes + bodyLen, GcmTagBytes);
                }
                else
                {
                    if (bodyLen > 0)
                    {
                        ApplyCtr(encBlock, ref encCtrBatch, ref encKsBatch, sessionId, seq, (byte)type, sendDir,
                            plaintext, offset, packet, HeaderBytes, bodyLen);
                    }
                    byte[] mac = encHmac.ComputeHash(packet, 0, HeaderBytes + bodyLen);
                    Buffer.BlockCopy(mac, 0, packet, HeaderBytes + bodyLen, TagBytes);
                }
            }
            return packet;
        }

        internal bool TryOpen(byte[] datagram, int length, out BoBoUdpPacketType type, out uint seq, out byte[] plaintext)
        {
            type = 0;
            seq = 0;
            plaintext = null;
            int minLen = ActiveOverheadBytes;
            if (disposed || datagram == null || length < minLen) return false;
            if (ReadUInt32LE(datagram, 0) != sessionId) return false;

            seq = ReadUInt32LE(datagram, 4);
            type = (BoBoUdpPacketType)datagram[8];
            int bodyLen = length - minLen;

            lock (decGate)
            {
                if (disposed) return false;

                // Read-only replay pre-check (never mutate state until cryptographic tag verification passes!)
                if (!CheckReplayAcceptableLocked(seq)) return false;

                if (cipherMode == BoBoUdpCipherMode.Aes128Gcm)
                {
                    Buffer.BlockCopy(datagram, 0, decHeaderScratch, 0, HeaderBytes);
                    Buffer.BlockCopy(datagram, 0, decNonceScratch, 0, HeaderBytes);
                    decNonceScratch[9] = recvDir;
                    decNonceScratch[10] = 0;
                    decNonceScratch[11] = 0;
                    Buffer.BlockCopy(datagram, HeaderBytes + bodyLen, decTagScratch, 0, GcmTagBytes);

                    byte[] cipherSlice = bodyLen > 0 ? new byte[bodyLen] : emptyDummy;
                    if (bodyLen > 0) Buffer.BlockCopy(datagram, HeaderBytes, cipherSlice, 0, bodyLen);
                    byte[] plainSlice = bodyLen > 0 ? new byte[bodyLen] : emptyDummy;

                    GCHandle hN = GCHandle.Alloc(decNonceScratch, GCHandleType.Pinned);
                    GCHandle hA = GCHandle.Alloc(decHeaderScratch, GCHandleType.Pinned);
                    GCHandle hT = GCHandle.Alloc(decTagScratch, GCHandleType.Pinned);
                    int st;
                    try
                    {
                        var info = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
                        {
                            cbSize = Marshal.SizeOf(typeof(BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO)),
                            dwInfoVersion = 1,
                            pbNonce = hN.AddrOfPinnedObject(),
                            cbNonce = 12,
                            pbAuthData = hA.AddrOfPinnedObject(),
                            cbAuthData = HeaderBytes,
                            pbTag = hT.AddrOfPinnedObject(),
                            cbTag = GcmTagBytes
                        };
                        int cbOut;
                        st = BCryptDecrypt(decGcmKeyHandle, cipherSlice, bodyLen, ref info, null, 0, plainSlice, bodyLen, out cbOut, 0);
                    }
                    finally
                    {
                        hN.Free();
                        hA.Free();
                        hT.Free();
                    }
                    if (st != 0) return false;
                    if (!CommitReplayLocked(seq)) return false;
                    plaintext = bodyLen > 0 ? plainSlice : new byte[0];
                    return true;
                }
                else
                {
                    // Verify 64-bit directional HMAC-SHA256 tag in constant time before touching anti-replay state or decrypting
                    byte[] expected = decHmac.ComputeHash(datagram, 0, HeaderBytes + bodyLen);
                    int diff = 0;
                    int tagOff = HeaderBytes + bodyLen;
                    for (int i = 0; i < TagBytes; i++)
                    {
                        diff |= (expected[i] ^ datagram[tagOff + i]);
                    }
                    if (diff != 0) return false;

                    if (!CommitReplayLocked(seq)) return false;

                    plaintext = new byte[bodyLen];
                    if (bodyLen > 0)
                    {
                        ApplyCtr(decBlock, ref decCtrBatch, ref decKsBatch, sessionId, seq, (byte)type, recvDir,
                            datagram, HeaderBytes, plaintext, 0, bodyLen);
                    }
                    return true;
                }
            }
        }

        private bool CheckReplayAcceptableLocked(uint seq)
        {
            if (!hasRecvSeq) return true;
            int delta = unchecked((int)(seq - maxRecvSeq));
            if (delta == 0) return false;
            if (delta > 0) return true;
            int back = -delta;
            if (back >= 512) return false;
            int wordIdx = back >> 6;
            ulong bit = 1UL << (back & 63);
            return (replayMask[wordIdx] & bit) == 0;
        }

        private bool CommitReplayLocked(uint seq)
        {
            if (!hasRecvSeq)
            {
                hasRecvSeq = true;
                maxRecvSeq = seq;
                Array.Clear(replayMask, 0, replayMask.Length);
                replayMask[0] = 1UL;
                return true;
            }
            int delta = unchecked((int)(seq - maxRecvSeq));
            if (delta == 0) return false;
            if (delta < 0)
            {
                int back = -delta;
                if (back >= 512) return false;
                int wordIdx = back >> 6;
                ulong bit = 1UL << (back & 63);
                if ((replayMask[wordIdx] & bit) != 0) return false;
                replayMask[wordIdx] |= bit;
                return true;
            }
            ShiftReplayWindowLeft(replayMask, delta);
            replayMask[0] |= 1UL;
            maxRecvSeq = seq;
            return true;
        }

        private static void ShiftReplayWindowLeft(ulong[] words, int shift)
        {
            int n = words.Length;
            int totalBits = n * 64;
            if (shift >= totalBits)
            {
                Array.Clear(words, 0, n);
                return;
            }
            int wordShift = shift >> 6;
            int bitShift = shift & 63;
            for (int i = n - 1; i >= 0; i--)
            {
                int src = i - wordShift;
                ulong val = (src >= 0) ? words[src] : 0UL;
                if (bitShift != 0 && src >= 0)
                {
                    ulong carry = (src - 1 >= 0) ? (words[src - 1] >> (64 - bitShift)) : 0UL;
                    val = (val << bitShift) | carry;
                }
                words[i] = val;
            }
        }

        private static void ApplyCtr(
            ICryptoTransform aesEcb,
            ref byte[] ctrBatch,
            ref byte[] ksBatch,
            uint sessId,
            uint seq,
            byte pktType,
            byte dir,
            byte[] src,
            int srcOffset,
            byte[] dst,
            int dstOffset,
            int count)
        {
            int blocks = (count + 15) >> 4;
            int totalBlockBytes = blocks << 4;
            if (ctrBatch.Length < totalBlockBytes)
            {
                ctrBatch = new byte[totalBlockBytes];
                ksBatch = new byte[totalBlockBytes];
            }

            // Build all 16-byte counter blocks in one contiguous buffer -> SINGLE native CryptoAPI TransformBlock call (3x faster!)
            byte s0 = (byte)(sessId & 0xFF);
            byte s1 = (byte)((sessId >> 8) & 0xFF);
            byte s2 = (byte)((sessId >> 16) & 0xFF);
            byte s3 = (byte)((sessId >> 24) & 0xFF);
            byte q0 = (byte)(seq & 0xFF);
            byte q1 = (byte)((seq >> 8) & 0xFF);
            byte q2 = (byte)((seq >> 16) & 0xFF);
            byte q3 = (byte)((seq >> 24) & 0xFF);

            for (int b = 0; b < blocks; b++)
            {
                int o = b << 4;
                ctrBatch[o] = s0;
                ctrBatch[o + 1] = s1;
                ctrBatch[o + 2] = s2;
                ctrBatch[o + 3] = s3;
                ctrBatch[o + 4] = q0;
                ctrBatch[o + 5] = q1;
                ctrBatch[o + 6] = q2;
                ctrBatch[o + 7] = q3;
                ctrBatch[o + 8] = pktType;
                ctrBatch[o + 9] = dir;
                ctrBatch[o + 10] = 0;
                ctrBatch[o + 11] = 0;
                ctrBatch[o + 12] = (byte)(b & 0xFF);
                ctrBatch[o + 13] = (byte)((b >> 8) & 0xFF);
                ctrBatch[o + 14] = (byte)((b >> 16) & 0xFF);
                ctrBatch[o + 15] = (byte)((b >> 24) & 0xFF);
            }

            aesEcb.TransformBlock(ctrBatch, 0, totalBlockBytes, ksBatch, 0);
            for (int i = 0; i < count; i++)
            {
                dst[dstOffset + i] = (byte)(src[srcOffset + i] ^ ksBatch[i]);
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (encGate)
            {
                DestroyCngGcmKey(ref encGcmKeyHandle, ref encGcmKeyObj);
                if (encBlock != null) { try { encBlock.Dispose(); } catch { } }
                if (encAes != null) { try { encAes.Dispose(); } catch { } }
                if (encHmac != null) { try { encHmac.Dispose(); } catch { } }
                if (encAesKey != null) Array.Clear(encAesKey, 0, encAesKey.Length);
                if (encHmacKey != null) Array.Clear(encHmacKey, 0, encHmacKey.Length);
                Array.Clear(encNonceScratch, 0, encNonceScratch.Length);
                Array.Clear(encTagScratch, 0, encTagScratch.Length);
            }
            lock (decGate)
            {
                DestroyCngGcmKey(ref decGcmKeyHandle, ref decGcmKeyObj);
                if (decBlock != null) { try { decBlock.Dispose(); } catch { } }
                if (decAes != null) { try { decAes.Dispose(); } catch { } }
                if (decHmac != null) { try { decHmac.Dispose(); } catch { } }
                if (decAesKey != null) Array.Clear(decAesKey, 0, decAesKey.Length);
                if (decHmacKey != null) Array.Clear(decHmacKey, 0, decHmacKey.Length);
                Array.Clear(decNonceScratch, 0, decNonceScratch.Length);
                Array.Clear(decTagScratch, 0, decTagScratch.Length);
            }
            if (punchHmacKey != null) Array.Clear(punchHmacKey, 0, punchHmacKey.Length);
        }

        internal static void WriteUInt16LE(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        internal static ushort ReadUInt16LE(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        internal static void WriteInt16LE(byte[] b, int o, short v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        internal static short ReadInt16LE(byte[] b, int o)
        {
            return (short)(b[o] | (b[o + 1] << 8));
        }

        internal static void WriteUInt32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        internal static uint ReadUInt32LE(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        internal static void WriteInt64LE(byte[] b, int o, long v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)((v >> (i * 8)) & 0xFF);
        }

        internal static long ReadInt64LE(byte[] b, int o)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= ((long)b[o + i]) << (i * 8);
            return v;
        }
    }

    /// <summary>
    /// MTU 安全视频分片 + 分组 XOR FEC 编码器（主机端调用）。
    /// 每个数据分片负载 ≤ 1140B，每组 G 个分片生成 1 个 XOR 校验分片（含 2B 长度前缀异或），
    /// 可在 0ms 额外 RTT 下直接恢复任意单包丢失（包括末尾短分片）。
    /// </summary>
    internal static class BoBoUdpFec
    {
        internal const int MaxShardPayloadBytes = 1140;
        internal const int DefaultSendShardPayloadBytes = 1060;
        internal const int ShardHeaderBytes = 19;
        internal const int MaxDataShardsPerFrame = 4096;
        internal const byte FlagKeyframe = 1;
        internal const byte FlagParity = 2;
        internal const byte FlagInterleaved = 4;
        internal const byte FlagRetransmit = 8;
        internal const byte FlagHevc = 16;

        internal static void SelectFecParams(
            int dataCount,
            bool isKeyframe,
            double recentLossPct,
            bool nackEnabled,
            out int groupSize,
            out bool emitFec)
        {
            SelectFecParams(dataCount, isKeyframe, recentLossPct, nackEnabled, 0.0, 0.0, 0, out groupSize, out emitFec);
        }

        internal static void SelectFecParams(
            int dataCount,
            bool isKeyframe,
            double recentLossPct,
            bool nackEnabled,
            double rttMs,
            double jitterMs,
            int burstLossStreak,
            out int groupSize,
            out bool emitFec)
        {
            if (isKeyframe)
            {
                groupSize = (recentLossPct >= 2.0 || burstLossStreak >= 2 || jitterMs >= 25.0 || rttMs >= 110.0) ? 2 : 3;
                emitFec = true;
            }
            else if (dataCount == 1)
            {
                groupSize = 1;
                emitFec = (recentLossPct >= 0.4 || burstLossStreak >= 1 || jitterMs >= 20.0);
            }
            else if (recentLossPct >= 2.5 || burstLossStreak >= 2 || (rttMs >= 160.0 && recentLossPct >= 0.8))
            {
                groupSize = 2;
                emitFec = true;
            }
            else if (recentLossPct >= 0.8 || jitterMs >= 20.0 || rttMs >= 65.0)
            {
                // High RTT/jitter makes selective NACK costly (~1+ RTT repair), so proactively strengthen XOR FEC.
                groupSize = 3;
                emitFec = true;
            }
            else if (nackEnabled && recentLossPct < 0.3 && rttMs < 30.0 && jitterMs < 10.0)
            {
                groupSize = 8;
                emitFec = true;
            }
            else
            {
                groupSize = 5;
                emitFec = true;
            }
        }

        /// <summary>
        /// P1-5 线速总预算优先分配：先确定总网络发送预算，再按当前 FEC 组大小、协议头占比与重传预留扣除开销，
        /// 得到不会撑爆 Pacer 发送队列的编码目标码率，并保持 Pacer 排水能力高于总发送码率。
        /// </summary>
        internal static void ComputeWireBudgetAllocation(
            int totalWireBudgetKbps,
            double recentLossPct,
            bool nackEnabled,
            double rttMs,
            double jitterMs,
            int burstLossStreak,
            int cipherOverheadBytes,
            BoBoCongestionSignal signal,
            out int encoderBitrateKbps,
            out int pacerWireKbps,
            out double pacerDrainMargin,
            out int activeGroupSize)
        {
            bool emitFec;
            SelectFecParams(6, false, recentLossPct, nackEnabled, rttMs, jitterMs, burstLossStreak,
                out activeGroupSize, out emitFec);
            if (activeGroupSize < 1) activeGroupSize = 1;

            double fecFactor = emitFec ? ((activeGroupSize + 1.0) / activeGroupSize) : 1.0;
            double perPacketOverhead = ShardHeaderBytes + Math.Max(BoBoUdpCrypto.OverheadBytes, cipherOverheadBytes) + 48.0;
            double headerFactor = ((DefaultSendShardPayloadBytes + perPacketOverhead) / DefaultSendShardPayloadBytes) * 1.015;
            double rtxReserve = nackEnabled
                ? Math.Min(0.10, 0.02 + Math.Max(0.0, recentLossPct) * 0.012)
                : 0.015;
            double audioAndCtrlReserve = 0.02;
            double totalExpansion = fecFactor * headerFactor * (1.0 + rtxReserve + audioAndCtrlReserve);

            int safeWire = Math.Max(600, Math.Min(60000, totalWireBudgetKbps));
            encoderBitrateKbps = Math.Max(450, Math.Min(safeWire, (int)Math.Floor(safeWire / totalExpansion)));
            pacerWireKbps = safeWire;
            if (signal == BoBoCongestionSignal.Overuse) pacerDrainMargin = 1.08;
            else if (signal == BoBoCongestionSignal.Hold) pacerDrainMargin = 1.15;
            else pacerDrainMargin = 1.24;
        }

        internal static List<byte[]> BuildVideoShards(
            uint frameId,
            uint timestampMs,
            int width,
            int height,
            bool isKeyframe,
            byte[] h264Data,
            double recentLossPct,
            bool interleavedFec = false,
            bool nackEnabled = false)
        {
            return BuildVideoShards(frameId, timestampMs, width, height, isKeyframe, h264Data,
                recentLossPct, interleavedFec, nackEnabled, 0.0, 0.0, 0);
        }

        internal static List<byte[]> BuildVideoShards(
            uint frameId,
            uint timestampMs,
            int width,
            int height,
            bool isKeyframe,
            byte[] h264Data,
            double recentLossPct,
            bool interleavedFec,
            bool nackEnabled,
            double rttMs,
            double jitterMs,
            int burstLossStreak,
            bool isHevc = false)
        {
            var shards = new List<byte[]>();
            if (h264Data == null || h264Data.Length == 0) return shards;

            int totalLen = h264Data.Length;
            int sendShardBytes = DefaultSendShardPayloadBytes;
            int dataCount = (totalLen + sendShardBytes - 1) / sendShardBytes;
            if (dataCount > MaxDataShardsPerFrame) return shards;

            int groupSize;
            bool emitFec;
            SelectFecParams(dataCount, isKeyframe, recentLossPct, nackEnabled, rttMs, jitterMs, burstLossStreak,
                out groupSize, out emitFec);

            int parityCount = emitFec ? ((dataCount + groupSize - 1) / groupSize) : 0;
            if (parityCount > 255)
            {
                groupSize = (dataCount + 254) / 255;
                parityCount = (dataCount + groupSize - 1) / groupSize;
            }

            // Flag 0x04 selects stride-interleaved parity; Flag 0x10 signals H.265 (HEVC).
            bool useInterleavedFec = interleavedFec && parityCount > 0;
            byte dataFlags = (byte)((isKeyframe ? FlagKeyframe : 0) | (useInterleavedFec ? FlagInterleaved : 0) | (isHevc ? FlagHevc : 0));
            for (int i = 0; i < dataCount; i++)
            {
                int off = i * sendShardBytes;
                int len = Math.Min(sendShardBytes, totalLen - off);
                byte[] pkt = new byte[ShardHeaderBytes + len];
                WriteShardHeader(pkt, frameId, timestampMs, width, height,
                    dataFlags, (ushort)i, (ushort)dataCount, (byte)groupSize, (byte)parityCount);
                Buffer.BlockCopy(h264Data, off, pkt, ShardHeaderBytes, len);
                shards.Add(pkt);
            }

            byte parityFlags = (byte)(dataFlags | FlagParity);
            for (int g = 0; g < parityCount; g++)
            {
                int start = useInterleavedFec ? g : g * groupSize;
                int end = useInterleavedFec ? dataCount : Math.Min(dataCount, start + groupSize);
                int step = useInterleavedFec ? parityCount : 1;
                int maxMemberLen = Math.Min(sendShardBytes, totalLen - start * sendShardBytes);

                byte[] pkt = new byte[ShardHeaderBytes + 2 + maxMemberLen];
                WriteShardHeader(pkt, frameId, timestampMs, width, height,
                    parityFlags, (ushort)g, (ushort)dataCount, (byte)groupSize, (byte)parityCount);

                ushort xorLen = 0;
                for (int i = start; i < end; i += step)
                {
                    int off = i * sendShardBytes;
                    int len = Math.Min(sendShardBytes, totalLen - off);
                    xorLen ^= (ushort)len;
                    for (int k = 0; k < len; k++)
                    {
                        pkt[ShardHeaderBytes + 2 + k] ^= h264Data[off + k];
                    }
                }
                BoBoUdpCrypto.WriteUInt16LE(pkt, ShardHeaderBytes, xorLen);
                shards.Add(pkt);
            }

            return shards;
        }

        private static void WriteShardHeader(
            byte[] pkt,
            uint frameId,
            uint timestampMs,
            int width,
            int height,
            byte flags,
            ushort shardIdx,
            ushort dataCount,
            byte groupSize,
            byte parityCount)
        {
            BoBoUdpCrypto.WriteUInt32LE(pkt, 0, frameId);
            BoBoUdpCrypto.WriteUInt32LE(pkt, 4, timestampMs);
            BoBoUdpCrypto.WriteUInt16LE(pkt, 8, (ushort)(width & 0xFFFF));
            BoBoUdpCrypto.WriteUInt16LE(pkt, 10, (ushort)(height & 0xFFFF));
            pkt[12] = flags;
            BoBoUdpCrypto.WriteUInt16LE(pkt, 13, shardIdx);
            BoBoUdpCrypto.WriteUInt16LE(pkt, 15, dataCount);
            pkt[17] = groupSize;
            pkt[18] = parityCount;
        }
    }

    internal struct BoBoUdpNackRequest
    {
        internal uint FrameId;
        internal ushort ShardIdx;
    }

    internal static class BoBoUdpNackCodec
    {
        internal const int MaxEntriesPerPacket = 48;

        internal static byte[] Encode(List<BoBoUdpNackRequest> requests)
        {
            if (requests == null || requests.Count == 0) return null;
            int count = Math.Min(requests.Count, MaxEntriesPerPacket);
            byte[] buf = new byte[2 + count * 6];
            BoBoUdpCrypto.WriteUInt16LE(buf, 0, (ushort)count);
            int pos = 2;
            for (int i = 0; i < count; i++)
            {
                BoBoUdpNackRequest req = requests[i];
                BoBoUdpCrypto.WriteUInt32LE(buf, pos, req.FrameId);
                BoBoUdpCrypto.WriteUInt16LE(buf, pos + 4, req.ShardIdx);
                pos += 6;
            }
            return buf;
        }

        internal static List<BoBoUdpNackRequest> Decode(byte[] payload)
        {
            var result = new List<BoBoUdpNackRequest>();
            if (payload == null || payload.Length < 2) return result;
            int declared = BoBoUdpCrypto.ReadUInt16LE(payload, 0);
            if (declared <= 0 || 2L + (long)declared * 6L > payload.Length) return result;

            int count = Math.Min(declared, MaxEntriesPerPacket);
            result.Capacity = count;
            int pos = 2;
            for (int i = 0; i < count; i++)
            {
                result.Add(new BoBoUdpNackRequest
                {
                    FrameId = BoBoUdpCrypto.ReadUInt32LE(payload, pos),
                    ShardIdx = BoBoUdpCrypto.ReadUInt16LE(payload, pos + 4)
                });
                pos += 6;
            }
            return result;
        }
    }

    /// <summary>
    /// 主机端短时视频分片重传环形缓存（最多保留 36 帧 / 4 MiB，供选择性 NACK 在高刷高 RTT 下精准补发）。
    /// </summary>
    internal sealed class BoBoUdpRetransmitCache
    {
        internal const int MaxCachedFrames = 36;
        internal const int MaxCachedBytes = 4 * 1024 * 1024;

        private sealed class CachedFrame
        {
            internal uint FrameId;
            internal byte[][] Shards;
            internal long[] LastSentTicks;
            internal byte[] RetryCounts;
            internal int ByteCount;
        }

        private readonly object gate = new object();
        private readonly Queue<uint> order = new Queue<uint>(MaxCachedFrames + 2);
        private readonly Dictionary<uint, CachedFrame> frames = new Dictionary<uint, CachedFrame>(MaxCachedFrames + 2);
        private int totalBytes;

        internal void StoreFrame(uint frameId, List<byte[]> shards)
        {
            if (shards == null || shards.Count == 0) return;

            int dataCount = 0;
            for (int i = 0; i < shards.Count; i++)
            {
                byte[] s = shards[i];
                if (s != null && s.Length >= BoBoUdpFec.ShardHeaderBytes && (s[12] & BoBoUdpFec.FlagParity) == 0)
                {
                    dataCount = BoBoUdpCrypto.ReadUInt16LE(s, 15);
                    break;
                }
            }
            if (dataCount <= 0 || dataCount > BoBoUdpFec.MaxDataShardsPerFrame) return;

            var stored = new byte[dataCount][];
            int frameBytes = 0;
            for (int i = 0; i < shards.Count; i++)
            {
                byte[] s = shards[i];
                if (s == null || s.Length < BoBoUdpFec.ShardHeaderBytes) continue;
                if ((s[12] & BoBoUdpFec.FlagParity) != 0) continue;
                int idx = BoBoUdpCrypto.ReadUInt16LE(s, 13);
                if (idx >= 0 && idx < dataCount && stored[idx] == null)
                {
                    stored[idx] = s;
                    frameBytes += s.Length;
                }
            }
            if (frameBytes <= 0 || frameBytes > MaxCachedBytes) return;

            var entry = new CachedFrame
            {
                FrameId = frameId,
                Shards = stored,
                LastSentTicks = new long[dataCount],
                RetryCounts = new byte[dataCount],
                ByteCount = frameBytes
            };

            lock (gate)
            {
                CachedFrame existing;
                if (frames.TryGetValue(frameId, out existing))
                {
                    totalBytes -= existing.ByteCount;
                    frames.Remove(frameId);
                }

                frames[frameId] = entry;
                order.Enqueue(frameId);
                totalBytes += frameBytes;

                while (order.Count > 0 && (frames.Count > MaxCachedFrames || totalBytes > MaxCachedBytes))
                {
                    uint oldestId = order.Dequeue();
                    CachedFrame oldest;
                    if (frames.TryGetValue(oldestId, out oldest) && !object.ReferenceEquals(oldest, entry))
                    {
                        frames.Remove(oldestId);
                        totalBytes -= oldest.ByteCount;
                    }
                }
            }
        }

        internal byte[] TryGetForRetransmit(uint frameId, ushort shardIdx, long nowTicks, double minCooldownMs = 20.0, int maxRetriesPerShard = 2)
        {
            lock (gate)
            {
                CachedFrame entry;
                if (!frames.TryGetValue(frameId, out entry)) return null;
                if (shardIdx >= entry.Shards.Length) return null;
                byte[] src = entry.Shards[shardIdx];
                if (src == null || src.Length < BoBoUdpFec.ShardHeaderBytes) return null;
                if (entry.RetryCounts[shardIdx] >= maxRetriesPerShard) return null;

                long prevTicks = entry.LastSentTicks[shardIdx];
                if (prevTicks != 0)
                {
                    double elapsedMs = (nowTicks - prevTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs < minCooldownMs) return null;
                }

                entry.LastSentTicks[shardIdx] = nowTicks;
                entry.RetryCounts[shardIdx]++;

                byte[] copy = new byte[src.Length];
                Buffer.BlockCopy(src, 0, copy, 0, src.Length);
                copy[12] |= BoBoUdpFec.FlagRetransmit;
                return copy;
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                frames.Clear();
                order.Clear();
                totalBytes = 0;
            }
        }
    }

    internal struct BoBoReassembledVideoFrame
    {
        internal uint FrameId;
        internal uint TimestampMs;
        internal int Width;
        internal int Height;
        internal bool IsKeyframe;
        internal bool IsHevc;
        internal byte[] H264Data;
    }

    /// <summary>
    /// 客户端零阻塞 UDP 视频重组与 XOR FEC + 选择性 NACK 协同修复器：
    /// - 维护最近 8 帧重组槽，单组丢 1 包 0ms 恢复，突发多包丢帧通过精准 NACK 1-RTT 补发；
    /// - 不可恢复断链与静帧尾包超时时自动触发防抖 PLI，杜绝花屏与长久僵死；
    /// - 基于 UDP 序列号间隔精确统计滑动窗口丢包率（供 HUD 与 ABR 实时使用）。
    /// </summary>
    internal sealed class BoBoUdpVideoAssembler
    {
        internal static int ComputeMaxAheadFrames(bool nackEnabled, double baseRttMs)
        {
            int rttFrames = (int)Math.Ceiling(Math.Max(15.0, baseRttMs) / 11.0);
            return nackEnabled
                ? Math.Max(12, Math.Min(24, rttFrames + 6))
                : Math.Max(8, Math.Min(16, rttFrames + 4));
        }

        private sealed class FrameBucket
        {
            internal uint FrameId;
            internal uint TimestampMs;
            internal int Width;
            internal int Height;
            internal bool IsKeyframe;
            internal bool IsHevc;
            internal int DataCount;
            internal int GroupSize;
            internal int ParityCount;
            internal bool InterleavedFec;
            internal byte[][] DataShards;
            internal byte[][] ParityShards;
            internal int ReceivedDataCount;
            internal int HighestDataIdxSeen = -1;
            internal bool AnyParitySeen;
            internal long CreatedTicks;
            internal long LastShardTicks;
            internal long[] NackSentTicks;
            internal byte[] NackRetries;
            internal bool HasActiveNack;
            internal bool IsComplete;
            internal byte[] AssembledData;
        }

        private const double BaseOutOfOrderGraceMs = 48.0;
        private static readonly Comparison<FrameBucket> BucketFrameIdComparison = delegate(FrameBucket a, FrameBucket c)
        {
            return unchecked((int)(a.FrameId - c.FrameId));
        };
        private readonly object gate = new object();
        private readonly Dictionary<uint, FrameBucket> buckets = new Dictionary<uint, FrameBucket>();
        private readonly List<FrameBucket> scratchOrdered = new List<FrameBucket>(12);
        private uint lastDeliveredFrameId;
        private bool hasDeliveredFrame;
        private bool awaitingKeyframe;
        private long awaitingKeyframeSinceTicks;
        private long gapFirstSeenTicks;
        private long lastPliTicks;
        private int fecRecoveredPackets;
        private int nackRecoveredPackets;
        private int nackRequestsSent;
        private int droppedIncompleteFrames;

        // Rolling sequence-gap packet loss estimator
        private bool hasSeqBaseline;
        private uint highestSeq;
        private int windowReceived;
        private int windowExpected;
        private long windowStartTicks = Stopwatch.GetTimestamp();
        private double latestLossPct;

        internal bool NackEnabled { get; set; }
        internal double LatestLossPct { get { lock (gate) { return latestLossPct; } } }
        internal int FecRecoveredPackets { get { return Volatile.Read(ref fecRecoveredPackets); } }
        internal int NackRecoveredPackets { get { return Volatile.Read(ref nackRecoveredPackets); } }
        internal int NackRequestsSent { get { return Volatile.Read(ref nackRequestsSent); } }
        internal int DroppedIncompleteFrames { get { return Volatile.Read(ref droppedIncompleteFrames); } }
        internal bool HasPendingBuckets { get { lock (gate) { return buckets.Count > 0; } } }
        internal bool IsAwaitingKeyframe { get { lock (gate) { return awaitingKeyframe; } } }
        internal uint LastDeliveredFrameId { get { lock (gate) { return lastDeliveredFrameId; } } }

        internal void ResetForRecovery()
        {
            ResetForRecovery(Stopwatch.GetTimestamp());
        }

        internal void ResetForRecovery(long nowTicks)
        {
            lock (gate)
            {
                buckets.Clear();
                gapFirstSeenTicks = 0;
                awaitingKeyframe = true;
                awaitingKeyframeSinceTicks = nowTicks;
                lastPliTicks = 0;
                hasSeqBaseline = false;
                highestSeq = 0;
                windowReceived = 0;
                windowExpected = 0;
                windowStartTicks = nowTicks;
                latestLossPct = 0.0;
            }
        }

        /// <summary>切换活跃端点或过滤非活跃探测后，仅平滑重置丢包统计窗口而保留在途视频重组桶。</summary>
        internal void ResetLossBaseline(long nowTicks)
        {
            lock (gate)
            {
                hasSeqBaseline = false;
                highestSeq = 0;
                windowReceived = 0;
                windowExpected = 0;
                windowStartTicks = nowTicks;
                latestLossPct = 0.0;
            }
        }

        internal void RecordReceivedUdpSeq(uint seq)
        {
            lock (gate)
            {
                if (!hasSeqBaseline)
                {
                    hasSeqBaseline = true;
                    highestSeq = seq;
                    windowReceived = 1;
                    windowExpected = 1;
                    windowStartTicks = Stopwatch.GetTimestamp();
                    return;
                }
                windowReceived++;
                int diff = unchecked((int)(seq - highestSeq));
                if (diff > 0 && diff < 20000)
                {
                    windowExpected += diff;
                    highestSeq = seq;
                }
                EvaluateLossWindowLocked(Stopwatch.GetTimestamp());
            }
        }

        /// <summary>Periodic decay check so VFR idle desktops do not keep a stale historical loss spike frozen forever.</summary>
        internal void RefreshLossWindow(long nowTicks)
        {
            lock (gate)
            {
                if (!hasSeqBaseline) return;
                EvaluateLossWindowLocked(nowTicks);
            }
        }

        private void EvaluateLossWindowLocked(long now)
        {
            double elapsedSec = (now - windowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec >= 1.5 || (elapsedSec >= 0.75 && windowExpected >= 6))
            {
                int lost = windowExpected - windowReceived;
                if (lost < 0) lost = 0;
                double instant = windowExpected > 0 ? ((lost * 100.0) / windowExpected) : 0.0;
                latestLossPct = latestLossPct * 0.35 + instant * 0.65;
                if (latestLossPct < 0.05) latestLossPct = 0.0;
                windowReceived = 0;
                windowExpected = 0;
                windowStartTicks = now;
            }
        }

        internal bool ProcessShard(byte[] payload, out BoBoReassembledVideoFrame completed, out bool needPli)
        {
            var ready = new List<BoBoReassembledVideoFrame>(2);
            ProcessShardBatch(payload, 20.0, ready, out needPli);
            if (ready.Count > 0)
            {
                completed = ready[ready.Count - 1];
                return true;
            }
            completed = new BoBoReassembledVideoFrame();
            return false;
        }

        internal void ProcessShardBatch(byte[] payload, List<BoBoReassembledVideoFrame> outReadyFrames, out bool needPli)
        {
            ProcessShardBatch(payload, 20.0, outReadyFrames, out needPli);
        }

        internal void ProcessShardBatch(byte[] payload, double currentRttMs, List<BoBoReassembledVideoFrame> outReadyFrames, out bool needPli)
        {
            needPli = false;
            if (outReadyFrames == null || payload == null || payload.Length < BoBoUdpFec.ShardHeaderBytes) return;

            uint frameId = BoBoUdpCrypto.ReadUInt32LE(payload, 0);
            uint tsMs = BoBoUdpCrypto.ReadUInt32LE(payload, 4);
            int width = BoBoUdpCrypto.ReadUInt16LE(payload, 8);
            int height = BoBoUdpCrypto.ReadUInt16LE(payload, 10);
            byte flags = payload[12];
            bool isKey = (flags & BoBoUdpFec.FlagKeyframe) != 0;
            bool isParity = (flags & BoBoUdpFec.FlagParity) != 0;
            bool interleaved = (flags & BoBoUdpFec.FlagInterleaved) != 0;
            bool isRetransmit = (flags & BoBoUdpFec.FlagRetransmit) != 0;
            bool isHevc = (flags & BoBoUdpFec.FlagHevc) != 0;
            int shardIdx = BoBoUdpCrypto.ReadUInt16LE(payload, 13);
            int dataCount = BoBoUdpCrypto.ReadUInt16LE(payload, 15);
            int groupSize = payload[17];
            int parityCount = payload[18];

            if (dataCount <= 0 || dataCount > BoBoUdpFec.MaxDataShardsPerFrame || groupSize <= 0) return;
            int bodyLen = payload.Length - BoBoUdpFec.ShardHeaderBytes;
            if (bodyLen <= 0 || bodyLen > BoBoUdpFec.MaxShardPayloadBytes + 2) return;

            long nowTicks = Stopwatch.GetTimestamp();
            double baseRtt = (currentRttMs > 0.1 && currentRttMs < 8000.0) ? currentRttMs : 20.0;
            double jitterGraceMs = Math.Max(BaseOutOfOrderGraceMs, Math.Min(140.0, baseRtt * 0.9));
            double effectiveGraceMs = NackEnabled
                ? Math.Max(jitterGraceMs, Math.Min(380.0, baseRtt * 1.65 + 35.0))
                : jitterGraceMs;
            int maxAhead = ComputeMaxAheadFrames(NackEnabled, baseRtt);

            lock (gate)
            {
                if (hasDeliveredFrame && unchecked((int)(frameId - lastDeliveredFrameId)) <= 0)
                {
                    return; // Late shard for already-delivered or superseded frame
                }
                if (awaitingKeyframe && !isKey)
                {
                    if (!HasPendingKeyframeBucketLocked() && HasExceededKeyframeStartupGraceLocked(baseRtt, nowTicks))
                    {
                        needPli = ShouldTriggerPliLocked(baseRtt, nowTicks);
                    }
                    return;
                }

                FrameBucket b;
                if (!buckets.TryGetValue(frameId, out b))
                {
                    b = new FrameBucket
                    {
                        FrameId = frameId,
                        TimestampMs = tsMs,
                        Width = width,
                        Height = height,
                        IsKeyframe = isKey,
                        IsHevc = isHevc,
                        DataCount = dataCount,
                        GroupSize = groupSize,
                        ParityCount = parityCount,
                        InterleavedFec = interleaved,
                        DataShards = new byte[dataCount][],
                        ParityShards = parityCount > 0 ? new byte[parityCount][] : null,
                        CreatedTicks = nowTicks,
                        LastShardTicks = nowTicks
                    };
                    buckets[frameId] = b;
                    PruneStaleBucketsLocked(frameId, maxAhead, baseRtt, nowTicks, ref needPli);
                }

                if (!b.IsComplete)
                {
                    b.LastShardTicks = nowTicks;
                    byte[] slice = new byte[bodyLen];
                    Buffer.BlockCopy(payload, BoBoUdpFec.ShardHeaderBytes, slice, 0, bodyLen);

                    int affectedGroup = -1;
                    if (!isParity)
                    {
                        if (shardIdx < b.DataCount)
                        {
                            if (shardIdx > b.HighestDataIdxSeen) b.HighestDataIdxSeen = shardIdx;
                            if (b.DataShards[shardIdx] == null)
                            {
                                b.DataShards[shardIdx] = slice;
                                b.ReceivedDataCount++;
                                if (isRetransmit) Interlocked.Increment(ref nackRecoveredPackets);
                                if (b.ParityCount > 0)
                                {
                                    affectedGroup = b.InterleavedFec ? (shardIdx % b.ParityCount) : (shardIdx / b.GroupSize);
                                }
                            }
                        }
                    }
                    else
                    {
                        b.AnyParitySeen = true;
                        if (b.ParityShards != null && shardIdx < b.ParityShards.Length && b.ParityShards[shardIdx] == null)
                        {
                            b.ParityShards[shardIdx] = slice;
                            affectedGroup = shardIdx;
                        }
                    }

                    // O(1) instant 0-RTT XOR FEC recovery for the single affected parity group
                    if (b.ReceivedDataCount < b.DataCount && b.ParityShards != null && affectedGroup >= 0)
                    {
                        TryFecRecoverGroupLocked(b, affectedGroup);
                    }

                    if (b.ReceivedDataCount >= b.DataCount)
                    {
                        AssembleBucketLocked(b);
                    }
                }

                // 1. If a completed keyframe is ready, it cleanly resets any prior missing gap and delivers immediately
                if (b.IsComplete && b.IsKeyframe)
                {
                    DeliverBucketAndFollowingLocked(b, outReadyFrames);
                    return;
                }

                // 2. If this completed frame is the exact next frame in sequence (or the very first frame), deliver it and any buffered contiguous successors!
                if (b.IsComplete && (!hasDeliveredFrame || unchecked((int)(frameId - lastDeliveredFrameId)) == 1))
                {
                    DeliverBucketAndFollowingLocked(b, outReadyFrames);
                    return;
                }

                // 3. Out-of-order later frame completed while an older frame is still in flight:
                // hold briefly (adaptive jitterGraceMs / nackGraceMs) so jitter, FEC parity & NACKs can arrive before declaring a gap!
                if (hasDeliveredFrame && !awaitingKeyframe)
                {
                    bool hasCompletedFutureFrame = false;
                    uint maxCompleteFuture = lastDeliveredFrameId;
                    foreach (KeyValuePair<uint, FrameBucket> kv in buckets)
                    {
                        if (kv.Value.IsComplete && unchecked((int)(kv.Key - lastDeliveredFrameId)) > 1)
                        {
                            hasCompletedFutureFrame = true;
                            if (unchecked((int)(kv.Key - maxCompleteFuture)) > 0)
                                maxCompleteFuture = kv.Key;
                        }
                    }

                    if (hasCompletedFutureFrame)
                    {
                        if (gapFirstSeenTicks == 0) gapFirstSeenTicks = nowTicks;
                        double waitMs = (nowTicks - gapFirstSeenTicks) * 1000.0 / Stopwatch.Frequency;
                        int aheadFrames = unchecked((int)(maxCompleteFuture - lastDeliveredFrameId));
                        if (waitMs >= effectiveGraceMs || aheadFrames >= maxAhead)
                        {
                            // Prior P-frame truly vanished beyond grace -> drop broken chain & request single debounced PLI
                            Interlocked.Increment(ref droppedIncompleteFrames);
                            buckets.Clear();
                            gapFirstSeenTicks = 0;
                            awaitingKeyframe = true;
                            awaitingKeyframeSinceTicks = nowTicks;
                            needPli = ShouldTriggerPliLocked(baseRtt, nowTicks);
                        }
                    }
                }
            }
        }

        internal void PollRecovery(long nowTicks, double currentRttMs, List<BoBoUdpNackRequest> outNacks, out bool needPli)
        {
            needPli = false;
            if (outNacks != null) outNacks.Clear();

            double baseRtt = (currentRttMs > 0.1 && currentRttMs < 8000.0) ? currentRttMs : 20.0;
            double jitterGraceMs = Math.Max(BaseOutOfOrderGraceMs, Math.Min(140.0, baseRtt * 0.9));
            double nackGraceMs = NackEnabled
                ? Math.Max(jitterGraceMs, Math.Min(380.0, baseRtt * 1.65 + 35.0))
                : jitterGraceMs;
            int maxAhead = ComputeMaxAheadFrames(NackEnabled, baseRtt);

            lock (gate)
            {
                if (hasSeqBaseline) EvaluateLossWindowLocked(nowTicks);
                if (buckets.Count == 0)
                {
                    if (awaitingKeyframe && HasExceededKeyframeStartupGraceLocked(baseRtt, nowTicks))
                    {
                        needPli = ShouldTriggerPliLocked(baseRtt, nowTicks);
                    }
                    return;
                }

                scratchOrdered.Clear();
                try
                {
                    foreach (FrameBucket fb in buckets.Values) scratchOrdered.Add(fb);
                    scratchOrdered.Sort(BucketFrameIdComparison);
                    uint highestSeenFrameId = scratchOrdered[scratchOrdered.Count - 1].FrameId;

                    int addedNacks = 0;
                    if (NackEnabled && outNacks != null)
                    {
                        for (int bi = 0; bi < scratchOrdered.Count && outNacks.Count < BoBoUdpNackCodec.MaxEntriesPerPacket; bi++)
                        {
                            FrameBucket b = scratchOrdered[bi];
                            if (b.IsComplete) continue;
                            if (awaitingKeyframe && !b.IsKeyframe) continue;
                            bool rttEligible = b.IsKeyframe ? (baseRtt <= 450.0) : (baseRtt <= 320.0);
                            if (!rttEligible) continue;

                            double ageMs = (nowTicks - b.CreatedTicks) * 1000.0 / Stopwatch.Frequency;
                            if (!b.IsKeyframe && ageMs >= nackGraceMs * 0.92) continue;

                            double idleMs = (nowTicks - b.LastShardTicks) * 1000.0 / Stopwatch.Frequency;
                            bool hasNewerFrame = b.FrameId != highestSeenFrameId;
                            int maxRetries = b.IsKeyframe ? 3 : 2;
                            double retryIntervalMs = Math.Max(18.0, Math.Min(130.0, baseRtt * 0.95 + 6.0));

                            int groupCount = b.ParityCount > 0 ? b.ParityCount : 1;
                            for (int g = 0; g < groupCount && outNacks.Count < BoBoUdpNackCodec.MaxEntriesPerPacket; g++)
                            {
                                int start = (b.ParityCount > 0 && b.InterleavedFec) ? g : (b.ParityCount > 0 ? g * b.GroupSize : 0);
                                int step = (b.ParityCount > 0 && b.InterleavedFec) ? b.ParityCount : 1;
                                int end = (b.ParityCount > 0 && b.InterleavedFec)
                                    ? b.DataCount
                                    : (b.ParityCount > 0 ? Math.Min(b.DataCount, start + b.GroupSize) : b.DataCount);
                                if (step <= 0) continue;

                                int missingCount = 0;
                                int firstMissing = -1;
                                int lastMissing = -1;
                                for (int idx = start; idx < end; idx += step)
                                {
                                    if (b.DataShards[idx] == null)
                                    {
                                        if (firstMissing < 0) firstMissing = idx;
                                        lastMissing = idx;
                                        missingCount++;
                                    }
                                }
                                if (missingCount == 0) continue;

                                bool groupEligible;
                                if (missingCount >= 2)
                                {
                                    groupEligible = hasNewerFrame || b.AnyParitySeen || idleMs >= 8.0 || b.HighestDataIdxSeen >= lastMissing + 3;
                                }
                                else if (b.ParityCount == 0)
                                {
                                    groupEligible = hasNewerFrame || idleMs >= 10.0 || b.HighestDataIdxSeen >= firstMissing + 3;
                                }
                                else
                                {
                                    groupEligible = hasNewerFrame || idleMs >= 14.0;
                                }
                                if (!groupEligible) continue;

                                if (b.NackSentTicks == null)
                                {
                                    b.NackSentTicks = new long[b.DataCount];
                                    b.NackRetries = new byte[b.DataCount];
                                }

                                for (int idx = start; idx < end && outNacks.Count < BoBoUdpNackCodec.MaxEntriesPerPacket; idx += step)
                                {
                                    if (b.DataShards[idx] != null) continue;
                                    if (b.NackRetries[idx] >= maxRetries) continue;
                                    long prevSent = b.NackSentTicks[idx];
                                    double sinceSentMs = prevSent == 0 ? double.MaxValue : (nowTicks - prevSent) * 1000.0 / Stopwatch.Frequency;
                                    if (prevSent == 0 || sinceSentMs >= retryIntervalMs)
                                    {
                                        b.NackSentTicks[idx] = nowTicks;
                                        b.NackRetries[idx]++;
                                        b.HasActiveNack = true;
                                        outNacks.Add(new BoBoUdpNackRequest { FrameId = b.FrameId, ShardIdx = (ushort)idx });
                                        addedNacks++;
                                    }
                                }
                            }
                        }
                    }
                    if (addedNacks > 0) Interlocked.Add(ref nackRequestsSent, addedNacks);

                    if (hasDeliveredFrame && !awaitingKeyframe)
                    {
                        bool hasCompletedFutureFrame = false;
                        uint maxCompleteFuture = lastDeliveredFrameId;
                        foreach (KeyValuePair<uint, FrameBucket> kv in buckets)
                        {
                            if (kv.Value.IsComplete && unchecked((int)(kv.Key - lastDeliveredFrameId)) > 1)
                            {
                                hasCompletedFutureFrame = true;
                                if (unchecked((int)(kv.Key - maxCompleteFuture)) > 0)
                                    maxCompleteFuture = kv.Key;
                            }
                        }

                        if (hasCompletedFutureFrame)
                        {
                            if (gapFirstSeenTicks == 0) gapFirstSeenTicks = nowTicks;
                            double waitMs = (nowTicks - gapFirstSeenTicks) * 1000.0 / Stopwatch.Frequency;
                            int aheadFrames = unchecked((int)(maxCompleteFuture - lastDeliveredFrameId));
                            if (waitMs >= nackGraceMs || aheadFrames >= maxAhead)
                            {
                                Interlocked.Increment(ref droppedIncompleteFrames);
                                buckets.Clear();
                                gapFirstSeenTicks = 0;
                                awaitingKeyframe = true;
                                awaitingKeyframeSinceTicks = nowTicks;
                                needPli = ShouldTriggerPliLocked(baseRtt, nowTicks);
                                return;
                            }
                        }
                    }

                    FrameBucket head = null;
                    for (int i = 0; i < scratchOrdered.Count; i++)
                    {
                        if (!scratchOrdered[i].IsComplete)
                        {
                            head = scratchOrdered[i];
                            break;
                        }
                    }
                    if (head != null && (!awaitingKeyframe || head.IsKeyframe))
                    {
                        double headIdleMs = (nowTicks - head.LastShardTicks) * 1000.0 / Stopwatch.Frequency;
                        double tailLimitMs = head.IsKeyframe
                            ? Math.Max(260.0, Math.Min(450.0, baseRtt * 2.6 + 80.0))
                            : Math.Max(180.0, nackGraceMs + 40.0);
                        if (headIdleMs >= tailLimitMs)
                        {
                            Interlocked.Increment(ref droppedIncompleteFrames);
                            buckets.Clear();
                            gapFirstSeenTicks = 0;
                            awaitingKeyframe = true;
                            awaitingKeyframeSinceTicks = nowTicks;
                            needPli = ShouldTriggerPliLocked(baseRtt, nowTicks);
                        }
                    }
                }
                finally
                {
                    scratchOrdered.Clear();
                }
            }
        }

        private static void AssembleBucketLocked(FrameBucket b)
        {
            int totalBytes = 0;
            for (int i = 0; i < b.DataCount; i++) totalBytes += b.DataShards[i].Length;
            byte[] full = new byte[totalBytes];
            int writePos = 0;
            for (int i = 0; i < b.DataCount; i++)
            {
                byte[] s = b.DataShards[i];
                Buffer.BlockCopy(s, 0, full, writePos, s.Length);
                writePos += s.Length;
            }
            b.AssembledData = full;
            b.IsComplete = true;
            b.DataShards = null;
            b.ParityShards = null;
        }

        private void DeliverBucketAndFollowingLocked(FrameBucket first, List<BoBoReassembledVideoFrame> outReady)
        {
            FrameBucket cur = first;
            while (cur != null && cur.IsComplete)
            {
                uint fid = cur.FrameId;
                outReady.Add(new BoBoReassembledVideoFrame
                {
                    FrameId = fid,
                    TimestampMs = cur.TimestampMs,
                    Width = cur.Width,
                    Height = cur.Height,
                    IsKeyframe = cur.IsKeyframe,
                    IsHevc = cur.IsHevc,
                    H264Data = cur.AssembledData
                });
                lastDeliveredFrameId = fid;
                hasDeliveredFrame = true;
                if (cur.IsKeyframe)
                {
                    awaitingKeyframe = false;
                    awaitingKeyframeSinceTicks = 0;
                }
                RemoveOlderBucketsLocked(fid);
                buckets.Remove(fid);
                gapFirstSeenTicks = 0;

                FrameBucket nextB;
                if (buckets.TryGetValue(unchecked(fid + 1), out nextB) && nextB.IsComplete)
                {
                    cur = nextB;
                }
                else
                {
                    cur = null;
                }
            }
        }

        private void TryFecRecoverGroupLocked(FrameBucket b, int g)
        {
            if (b.ParityShards == null || g < 0 || g >= b.ParityShards.Length) return;
            byte[] par = b.ParityShards[g];
            if (par == null || par.Length < 2) return;

            int start = b.InterleavedFec ? g : (g * b.GroupSize);
            int step = b.InterleavedFec ? b.ParityCount : 1;
            int end = b.InterleavedFec ? b.DataCount : Math.Min(b.DataCount, start + b.GroupSize);
            if (step <= 0) return;

            int missingIdx = -1;
            int missingCount = 0;
            for (int i = start; i < end; i += step)
            {
                if (b.DataShards[i] == null)
                {
                    missingIdx = i;
                    missingCount++;
                    if (missingCount > 1) return;
                }
            }
            if (missingCount != 1 || missingIdx < 0) return;

            ushort recLen = BoBoUdpCrypto.ReadUInt16LE(par, 0);
            for (int i = start; i < end; i += step)
            {
                if (i == missingIdx) continue;
                recLen ^= (ushort)b.DataShards[i].Length;
            }
            if (recLen == 0 || recLen > BoBoUdpFec.MaxShardPayloadBytes || recLen + 2 > par.Length) return;

            byte[] recovered = new byte[recLen];
            Buffer.BlockCopy(par, 2, recovered, 0, recLen);
            for (int i = start; i < end; i += step)
            {
                if (i == missingIdx) continue;
                byte[] other = b.DataShards[i];
                int n = Math.Min(recLen, other.Length);
                for (int k = 0; k < n; k++) recovered[k] ^= other[k];
            }

            b.DataShards[missingIdx] = recovered;
            b.ReceivedDataCount++;
            Interlocked.Increment(ref fecRecoveredPackets);
        }

        private bool HasPendingKeyframeBucketLocked()
        {
            foreach (FrameBucket b in buckets.Values)
            {
                if (b.IsKeyframe && !b.IsComplete) return true;
            }
            return false;
        }

        private bool HasExceededKeyframeStartupGraceLocked(double baseRtt, long nowTicks)
        {
            if (awaitingKeyframeSinceTicks == 0) return true;
            double graceMs = Math.Max(260.0, Math.Min(900.0, baseRtt * 2.5 + 120.0));
            return ((nowTicks - awaitingKeyframeSinceTicks) * 1000.0 / Stopwatch.Frequency) >= graceMs;
        }

        private void PruneStaleBucketsLocked(uint newestFrameId, int maxAhead, double currentRttMs, long nowTicks, ref bool needPli)
        {
            int pruneLimit = Math.Max(12, maxAhead + 4);
            if (buckets.Count <= pruneLimit) return;
            var toRemove = new List<uint>();
            foreach (KeyValuePair<uint, FrameBucket> kv in buckets)
            {
                if (unchecked((int)(newestFrameId - kv.Key)) >= pruneLimit)
                {
                    toRemove.Add(kv.Key);
                }
            }
            if (toRemove.Count > 0)
            {
                for (int i = 0; i < toRemove.Count; i++) buckets.Remove(toRemove[i]);
                Interlocked.Add(ref droppedIncompleteFrames, toRemove.Count);
                if (!awaitingKeyframe)
                {
                    awaitingKeyframe = true;
                    awaitingKeyframeSinceTicks = nowTicks;
                    needPli = ShouldTriggerPliLocked(currentRttMs, nowTicks);
                }
            }
        }

        private void RemoveOlderBucketsLocked(uint upToFrameId)
        {
            if (buckets.Count <= 1) return;
            var toRemove = new List<uint>();
            foreach (KeyValuePair<uint, FrameBucket> kv in buckets)
            {
                if (unchecked((int)(kv.Key - upToFrameId)) < 0) toRemove.Add(kv.Key);
            }
            for (int i = 0; i < toRemove.Count; i++) buckets.Remove(toRemove[i]);
        }

        private bool ShouldTriggerPliLocked(double currentRttMs, long now)
        {
            double ms = (now - lastPliTicks) * 1000.0 / Stopwatch.Frequency;
            double cooldownMs = Math.Max(500.0, Math.Min(1200.0, currentRttMs * 4.0));
            if (lastPliTicks == 0 || ms >= cooldownMs)
            {
                lastPliTicks = now;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 实时自研拥塞控制与自适应码率/帧率控制器（GCC / AIMD 思路，兼容 UDP 与 TCP+TLS 双通道）：
    /// - 结合滚动 12 秒基线队列延迟（currentRtt - minRtt）、抖动（EWMA Jitter）、丢包率与发送背压事件，毫秒级乘性降码率；
    /// - 网络空闲稳定且低抖动后平缓加性回升至目标码率与帧率，全程通过 ICodecAPI 热生效，零重启无黑屏。
    /// </summary>
    internal sealed class BoBoAdaptiveBitrateController
    {
        private const double MinRttWindowSec = 6.0;
        private readonly object gate = new object();
        private readonly bool isLanLink;
        private bool enabled;
        private int targetBitrateKbps;
        private int targetFps;
        private int currentBitrateKbps;
        private int currentFps;
        private double minRttMs = -1.0;
        private double currentBucketMinRtt = -1.0;
        private double prevBucketMinRtt = -1.0;
        private long rttBucketStartTicks;
        private double smoothedRttMs = -1.0;
        private double prevRttMs = -1.0;
        private double rttJitterMs;
        private double clientJitterMs;
        private double delayTrendMs;
        private double overuseThresholdMs = 12.0;
        private int consecutiveOveruseTicks;
        private long overuseStartTicks;
        private BoBoCongestionSignal congestionSignal = BoBoCongestionSignal.Normal;
        private double smoothedLossPct;
        private long lastDecreaseTicks;
        private long lastIncreaseTicks;
        private long lowBitrateSinceTicks;

        internal static int ComputeTcpQueueLimit(int fps)
        {
            int safeFps = Math.Max(15, Math.Min(144, fps));
            return Math.Max(4, Math.Min(8, (int)Math.Ceiling(safeFps / 14.0)));
        }

        internal BoBoAdaptiveBitrateController(bool autoEnabled, int targetBitrateKbps, int targetFps, double initialRttMs, bool isLan = true)
        {
            enabled = autoEnabled;
            isLanLink = isLan && (initialRttMs <= 18.0);
            this.targetBitrateKbps = ClampBitrate(targetBitrateKbps);
            this.targetFps = ClampFps(targetFps);
            this.currentFps = this.targetFps;

            // On WAN / Campus / Cellular / Tailscale links, preserve high frame rate when RTT is moderate
            // and ramp smoothly toward 30 Mbps without unnecessarily throttling 90Hz down to 60fps.
            if (autoEnabled && initialRttMs > 95.0)
            {
                this.currentBitrateKbps = Math.Min(this.targetBitrateKbps, 3500);
                this.currentFps = Math.Min(this.targetFps, 45);
            }
            else if (autoEnabled && !isLanLink)
            {
                this.currentBitrateKbps = Math.Min(this.targetBitrateKbps, 6000);
                this.currentFps = Math.Min(this.targetFps, 90);
            }
            else if (!autoEnabled && !isLanLink && initialRttMs >= 95.0)
            {
                // Smoothly warm up fixed bitrate presets on high-RTT links so Frame 0 IDR bursts never spike packet loss.
                this.currentBitrateKbps = Math.Min(this.targetBitrateKbps, 3200);
            }
            else
            {
                this.currentBitrateKbps = this.targetBitrateKbps;
            }

            if (initialRttMs > 0.1)
            {
                minRttMs = initialRttMs;
                currentBucketMinRtt = initialRttMs;
                smoothedRttMs = initialRttMs;
                prevRttMs = initialRttMs;
            }
            long now = Stopwatch.GetTimestamp();
            lastIncreaseTicks = now;
            rttBucketStartTicks = now;
        }

        internal int CurrentBitrateKbps { get { lock (gate) { return currentBitrateKbps; } } }
        internal int CurrentFps { get { lock (gate) { return currentFps; } } }
        internal bool AutoEnabled { get { lock (gate) { return enabled; } } }
        internal double LatestJitterMs { get { lock (gate) { return Math.Max(rttJitterMs, clientJitterMs); } } }
        internal double CurrentBaselineRttMs { get { lock (gate) { return minRttMs; } } }
        internal BoBoCongestionSignal CurrentSignal { get { lock (gate) { return congestionSignal; } } }
        internal double CurrentDelayTrendMs { get { lock (gate) { return delayTrendMs; } } }
        internal double PacingHeadroomFactor
        {
            get
            {
                lock (gate)
                {
                    if (congestionSignal == BoBoCongestionSignal.Overuse) return 1.12;
                    if (congestionSignal == BoBoCongestionSignal.Hold) return 1.22;
                    return 1.35;
                }
            }
        }

        internal void UpdateTarget(bool autoEnabled, int newTargetBitrateKbps, int newTargetFps)
        {
            lock (gate)
            {
                bool enablingAuto = autoEnabled && !enabled;
                enabled = autoEnabled;
                targetBitrateKbps = ClampBitrate(newTargetBitrateKbps);
                targetFps = ClampFps(newTargetFps);
                lowBitrateSinceTicks = 0;
                if (!enabled)
                {
                    currentBitrateKbps = targetBitrateKbps;
                    currentFps = targetFps;
                    return;
                }
                int maxBitrate = isLanLink ? targetBitrateKbps : Math.Min(targetBitrateKbps, enablingAuto ? 18000 : 30000);
                int maxFps = isLanLink ? targetFps : Math.Min(targetFps, 90);
                currentBitrateKbps = Math.Min(currentBitrateKbps, maxBitrate);
                currentFps = Math.Min(currentFps, maxFps);
                if (enablingAuto) lastIncreaseTicks = Stopwatch.GetTimestamp();
            }
        }

        internal bool OnDirectPathPromoted(out int newBitrateKbps, out int newFps)
        {
            lock (gate)
            {
                int prevBps = currentBitrateKbps;
                int prevFps = currentFps;

                minRttMs = -1.0;
                currentBucketMinRtt = -1.0;
                prevBucketMinRtt = -1.0;
                smoothedRttMs = -1.0;
                prevRttMs = -1.0;
                rttJitterMs = 0.0;
                clientJitterMs = 0.0;
                delayTrendMs = 0.0;
                consecutiveOveruseTicks = 0;
                overuseStartTicks = 0;
                lowBitrateSinceTicks = 0;
                lastDecreaseTicks = 0;
                lastIncreaseTicks = Stopwatch.GetTimestamp();
                congestionSignal = BoBoCongestionSignal.Normal;

                int maxFps = isLanLink ? targetFps : Math.Min(targetFps, 90);
                currentFps = maxFps;
                if (enabled)
                {
                    int directStart = Math.Min(targetBitrateKbps, 6000);
                    currentBitrateKbps = Math.Max(currentBitrateKbps, directStart);
                }
                else
                {
                    currentBitrateKbps = targetBitrateKbps;
                }

                newBitrateKbps = currentBitrateKbps;
                newFps = currentFps;
                return currentBitrateKbps != prevBps || currentFps != prevFps;
            }
        }

        internal bool OnCongestionBackpressure(out int newBitrateKbps, out int newFps)
        {
            lock (gate)
            {
                congestionSignal = BoBoCongestionSignal.Overuse;
                long now = Stopwatch.GetTimestamp();
                if (lastDecreaseTicks != 0 && ElapsedMs(lastDecreaseTicks, now) < 600.0)
                {
                    newBitrateKbps = currentBitrateKbps;
                    newFps = currentFps;
                    return false;
                }

                int floorBps = enabled ? 600 : Math.Min(targetBitrateKbps, 1200);
                int reduced = Math.Max(floorBps, ClampBitrate((int)(currentBitrateKbps * 0.65)));
                if (reduced < currentBitrateKbps)
                {
                    currentBitrateKbps = reduced;
                    lastDecreaseTicks = now;
                    lastIncreaseTicks = now;
                    if (enabled && currentBitrateKbps <= 2200 && currentFps > 30)
                    {
                        currentFps = 30;
                    }
                    newBitrateKbps = currentBitrateKbps;
                    newFps = currentFps;
                    return true;
                }
                newBitrateKbps = currentBitrateKbps;
                newFps = currentFps;
                return false;
            }
        }

        internal bool OnTelemetry(double rttMs, double lossPct, out int newBitrateKbps, out int newFps)
        {
            return OnTelemetry(rttMs, lossPct, 0.0, Stopwatch.GetTimestamp(), out newBitrateKbps, out newFps);
        }

        internal bool OnTelemetry(double rttMs, double lossPct, long nowTicks, out int newBitrateKbps, out int newFps)
        {
            return OnTelemetry(rttMs, lossPct, 0.0, nowTicks, out newBitrateKbps, out newFps);
        }

        internal bool OnTelemetry(double rttMs, double lossPct, double reportedJitterMs, out int newBitrateKbps, out int newFps)
        {
            return OnTelemetry(rttMs, lossPct, reportedJitterMs, Stopwatch.GetTimestamp(), out newBitrateKbps, out newFps);
        }

        internal bool OnTelemetry(double rttMs, double lossPct, double reportedJitterMs, long nowTicks, out int newBitrateKbps, out int newFps)
        {
            lock (gate)
            {
                long now = nowTicks;
                if (rttBucketStartTicks == 0) rttBucketStartTicks = now;
                double bucketElapsedSec = (now - rttBucketStartTicks) / (double)Stopwatch.Frequency;
                if (bucketElapsedSec >= MinRttWindowSec)
                {
                    if (bucketElapsedSec >= MinRttWindowSec * 2.0)
                    {
                        prevBucketMinRtt = -1.0;
                    }
                    else
                    {
                        prevBucketMinRtt = currentBucketMinRtt;
                    }
                    currentBucketMinRtt = -1.0;
                    rttBucketStartTicks = now;
                }

                if (reportedJitterMs >= 0.0 && reportedJitterMs < 5000.0)
                {
                    clientJitterMs = reportedJitterMs;
                }

                if (rttMs > 0.1 && rttMs < 8000.0)
                {
                    if (prevRttMs > 0.1)
                    {
                        double signedDelta = rttMs - prevRttMs;
                        double absDelta = Math.Abs(signedDelta);
                        rttJitterMs = rttJitterMs * 0.75 + absDelta * 0.25;
                        // GCC-inspired smoothed delay gradient (+ = queue growing, <=0 = draining)
                        double clampedGrad = Math.Max(-80.0, Math.Min(80.0, signedDelta));
                        delayTrendMs = delayTrendMs * 0.75 + clampedGrad * 0.25;

                        // Require >=2 consecutive upward ticks to ignore one-off baseline steps or Wi-Fi spikes
                        if (signedDelta > 1.5 && delayTrendMs > overuseThresholdMs)
                        {
                            consecutiveOveruseTicks++;
                            if (overuseStartTicks == 0) overuseStartTicks = now;
                        }
                        else if (delayTrendMs > overuseThresholdMs && consecutiveOveruseTicks >= 1 && signedDelta >= -1.0)
                        {
                            consecutiveOveruseTicks++;
                        }
                        else
                        {
                            consecutiveOveruseTicks = 0;
                            overuseStartTicks = 0;
                        }

                        // Slowly adapt threshold toward observed noise floor (GCC adaptive gamma)
                        double targetGamma = Math.Max(6.0, Math.Min(28.0, Math.Abs(delayTrendMs) * 1.15 + 6.0));
                        overuseThresholdMs = overuseThresholdMs * 0.92 + targetGamma * 0.08;
                    }
                    prevRttMs = rttMs;
                    if (currentBucketMinRtt < 0 || rttMs < currentBucketMinRtt) currentBucketMinRtt = rttMs;

                    if (currentBucketMinRtt > 0 && prevBucketMinRtt > 0)
                        minRttMs = Math.Min(currentBucketMinRtt, prevBucketMinRtt);
                    else if (currentBucketMinRtt > 0)
                        minRttMs = currentBucketMinRtt;
                    else if (prevBucketMinRtt > 0)
                        minRttMs = prevBucketMinRtt;

                    smoothedRttMs = (smoothedRttMs < 0) ? rttMs : (smoothedRttMs * 0.65 + rttMs * 0.35);
                }
                if (lossPct >= 0.0 && lossPct <= 100.0)
                {
                    smoothedLossPct = smoothedLossPct * 0.5 + lossPct * 0.5;
                }

                double queueDelayMs = (minRttMs > 0 && smoothedRttMs > minRttMs) ? (smoothedRttMs - minRttMs) : 0.0;
                double effectiveJitterMs = Math.Max(rttJitterMs, clientJitterMs);
                bool trendOveruse = consecutiveOveruseTicks >= 2 &&
                    (overuseStartTicks == 0 || ElapsedMs(overuseStartTicks, now) >= 180.0 || delayTrendMs >= overuseThresholdMs * 1.35);

                if (lossPct >= 4.0 || queueDelayMs >= (enabled ? 110.0 : 95.0))
                {
                    congestionSignal = BoBoCongestionSignal.Overuse;
                }
                else if (enabled
                    ? (lossPct >= 1.5 || queueDelayMs >= 55.0 || trendOveruse)
                    : (lossPct >= 2.0 || queueDelayMs >= 45.0 || (trendOveruse && queueDelayMs >= 26.0)))
                {
                    congestionSignal = BoBoCongestionSignal.Overuse;
                }
                else if (delayTrendMs > overuseThresholdMs * 0.5 || effectiveJitterMs >= 18.0 || queueDelayMs >= 26.0)
                {
                    congestionSignal = BoBoCongestionSignal.Hold;
                }
                else
                {
                    congestionSignal = BoBoCongestionSignal.Normal;
                }

                bool changed = false;
                if (lossPct >= 4.0 || queueDelayMs >= (enabled ? 110.0 : 95.0))
                {
                    if (lastDecreaseTicks == 0 || ElapsedMs(lastDecreaseTicks, now) >= (enabled ? 750.0 : 700.0))
                    {
                        int floorBps = enabled ? 600 : Math.Min(targetBitrateKbps, 1200);
                        int next = Math.Max(floorBps, ClampBitrate((int)(currentBitrateKbps * (enabled ? 0.65 : 0.68))));
                        if (next < currentBitrateKbps)
                        {
                            currentBitrateKbps = next;
                            changed = true;
                        }
                        lastDecreaseTicks = now;
                        lastIncreaseTicks = now;
                    }
                }
                else if (congestionSignal == BoBoCongestionSignal.Overuse)
                {
                    if (lastDecreaseTicks == 0 || ElapsedMs(lastDecreaseTicks, now) >= (enabled ? 950.0 : 900.0))
                    {
                        int floorBps = enabled ? 600 : Math.Min(targetBitrateKbps, 1500);
                        int next = Math.Max(floorBps, ClampBitrate((int)(currentBitrateKbps * 0.84)));
                        if (next < currentBitrateKbps)
                        {
                            currentBitrateKbps = next;
                            changed = true;
                        }
                        lastDecreaseTicks = now;
                        lastIncreaseTicks = now;
                        consecutiveOveruseTicks = 0;
                        overuseStartTicks = 0;
                    }
                }
                else if (congestionSignal == BoBoCongestionSignal.Normal &&
                         smoothedLossPct < (enabled ? 0.5 : 1.0) &&
                         queueDelayMs < 30.0 &&
                         effectiveJitterMs < 35.0)
                {
                    int maxBps = (enabled && !isLanLink) ? Math.Min(targetBitrateKbps, 30000) : targetBitrateKbps;
                    double incInterval = enabled ? 2000.0 : 900.0;
                    if (currentBitrateKbps < maxBps && ElapsedMs(lastIncreaseTicks, now) >= incInterval)
                    {
                        int step = enabled
                            ? Math.Max(400, (int)(currentBitrateKbps * 0.12))
                            : Math.Max(900, (int)(currentBitrateKbps * 0.24));
                        int next = Math.Min(maxBps, currentBitrateKbps + step);
                        if (next != currentBitrateKbps)
                        {
                            currentBitrateKbps = next;
                            changed = true;
                        }
                        lastIncreaseTicks = now;
                    }
                }

                // Always prioritize high FPS ("保帧率、降码率"); only reduce FPS under sustained extreme bottleneck.
                if (enabled)
                {
                    int maxFps = isLanLink ? targetFps : Math.Min(targetFps, 90);
                    if (currentBitrateKbps <= 2000)
                    {
                        if (lowBitrateSinceTicks == 0) lowBitrateSinceTicks = now;
                        else if (ElapsedMs(lowBitrateSinceTicks, now) >= 1800.0 && currentFps > 30)
                        {
                            currentFps = 30;
                            changed = true;
                        }
                    }
                    else
                    {
                        lowBitrateSinceTicks = 0;
                        if (currentBitrateKbps >= 3600 && currentFps < maxFps && congestionSignal == BoBoCongestionSignal.Normal &&
                            smoothedLossPct < 0.5 && queueDelayMs < 30.0 && effectiveJitterMs < 35.0)
                        {
                            currentFps = maxFps;
                            changed = true;
                        }
                    }
                }
                else
                {
                    if (currentBitrateKbps <= 1500 && targetBitrateKbps > 2000)
                    {
                        if (lowBitrateSinceTicks == 0) lowBitrateSinceTicks = now;
                        else if (ElapsedMs(lowBitrateSinceTicks, now) >= 2500.0 && currentFps > Math.Min(targetFps, 45))
                        {
                            currentFps = Math.Min(targetFps, 45);
                            changed = true;
                        }
                    }
                    else
                    {
                        lowBitrateSinceTicks = 0;
                        if (currentBitrateKbps >= 2500 && currentFps != targetFps)
                        {
                            currentFps = targetFps;
                            changed = true;
                        }
                    }
                }

                newBitrateKbps = currentBitrateKbps;
                newFps = currentFps;
                return changed;
            }
        }

        private static int ClampBitrate(int kbps)
        {
            if (kbps < 600) return 600;
            if (kbps > 60000) return 60000;
            return kbps;
        }

        private static int ClampFps(int fps)
        {
            if (fps < 15) return 15;
            if (fps > 144) return 144;
            return fps;
        }

        private static double ElapsedMs(long fromTicks, long toTicks)
        {
            return (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// UDP 输入打包与可靠去重工具（零 HoL 鼠标移动 + 最近 3 条离散键鼠事件冗余附带）：
    /// 即使校园网出现 10% 随机丢包，鼠标移动零卡顿，点击/按键也绝不丢失不重放。
    /// </summary>
    internal sealed class BoBoUdpInputCodec
    {
        private readonly object txGate = new object();
        private uint nextDiscreteSeq;
        private readonly List<KeyValuePair<uint, BoBoInputPacket>> recentDiscrete = new List<KeyValuePair<uint, BoBoInputPacket>>(4);

        private readonly object rxGate = new object();
        private uint maxAppliedDiscreteSeq;
        private ulong appliedDiscreteMask;
        private bool hasAppliedDiscrete;

        internal byte[] BuildPacket(bool hasAbs, BoBoInputPacket absPkt, bool hasRel, short relDx, short relDy, BoBoInputPacket? newDiscrete)
        {
            lock (txGate)
            {
                if (newDiscrete.HasValue)
                {
                    uint seq = ++nextDiscreteSeq;
                    recentDiscrete.Add(new KeyValuePair<uint, BoBoInputPacket>(seq, newDiscrete.Value));
                    if (recentDiscrete.Count > 3) recentDiscrete.RemoveAt(0);
                }

                int dCount = recentDiscrete.Count;
                byte flags = (byte)((hasAbs ? 1 : 0) | (hasRel ? 2 : 0));
                int len = 2 + (hasAbs ? 4 : 0) + (hasRel ? 4 : 0) + (dCount * 10);
                byte[] buf = new byte[len];
                int pos = 0;
                buf[pos++] = flags;
                buf[pos++] = (byte)dCount;
                if (hasAbs)
                {
                    BoBoUdpCrypto.WriteInt16LE(buf, pos, absPkt.A); pos += 2;
                    BoBoUdpCrypto.WriteInt16LE(buf, pos, absPkt.B); pos += 2;
                }
                if (hasRel)
                {
                    BoBoUdpCrypto.WriteInt16LE(buf, pos, relDx); pos += 2;
                    BoBoUdpCrypto.WriteInt16LE(buf, pos, relDy); pos += 2;
                }
                for (int i = 0; i < dCount; i++)
                {
                    BoBoUdpCrypto.WriteUInt32LE(buf, pos, recentDiscrete[i].Key); pos += 4;
                    BoBoInputPacket p = recentDiscrete[i].Value;
                    buf[pos++] = p.Kind;
                    buf[pos++] = p.Flags;
                    BoBoUdpCrypto.WriteInt16LE(buf, pos, p.A); pos += 2;
                    BoBoUdpCrypto.WriteInt16LE(buf, pos, p.B); pos += 2;
                }
                return buf;
            }
        }

        internal List<BoBoInputPacket> ParseAndDeduplicate(byte[] payload)
        {
            var result = new List<BoBoInputPacket>(4);
            if (payload == null || payload.Length < 2) return result;
            byte flags = payload[0];
            int dCount = payload[1];
            int pos = 2;

            if ((flags & 1) != 0)
            {
                if (pos + 4 > payload.Length) return result;
                short ax = BoBoUdpCrypto.ReadInt16LE(payload, pos); pos += 2;
                short ay = BoBoUdpCrypto.ReadInt16LE(payload, pos); pos += 2;
                result.Add(new BoBoInputPacket { Kind = 1, Flags = 0, A = ax, B = ay });
            }
            if ((flags & 2) != 0)
            {
                if (pos + 4 > payload.Length) return result;
                short dx = BoBoUdpCrypto.ReadInt16LE(payload, pos); pos += 2;
                short dy = BoBoUdpCrypto.ReadInt16LE(payload, pos); pos += 2;
                if (dx != 0 || dy != 0)
                {
                    result.Add(new BoBoInputPacket { Kind = 2, Flags = 0, A = dx, B = dy });
                }
            }

            if (dCount > 0)
            {
                lock (rxGate)
                {
                    for (int i = 0; i < dCount && pos + 10 <= payload.Length; i++)
                    {
                        uint seq = BoBoUdpCrypto.ReadUInt32LE(payload, pos); pos += 4;
                        var p = new BoBoInputPacket
                        {
                            Kind = payload[pos++],
                            Flags = payload[pos++],
                            A = BoBoUdpCrypto.ReadInt16LE(payload, pos),
                            B = BoBoUdpCrypto.ReadInt16LE(payload, pos + 2)
                        };
                        pos += 4;

                        if (MarkDiscreteSeqIfNewLocked(seq))
                        {
                            result.Add(p);
                        }
                    }
                }
            }
            return result;
        }

        private bool MarkDiscreteSeqIfNewLocked(uint seq)
        {
            if (!hasAppliedDiscrete)
            {
                hasAppliedDiscrete = true;
                maxAppliedDiscreteSeq = seq;
                appliedDiscreteMask = 1UL;
                return true;
            }
            int delta = unchecked((int)(seq - maxAppliedDiscreteSeq));
            if (delta == 0) return false;
            if (delta < 0)
            {
                int back = -delta;
                if (back >= 64) return false;
                ulong bit = 1UL << back;
                if ((appliedDiscreteMask & bit) != 0) return false;
                appliedDiscreteMask |= bit;
                return true;
            }
            if (delta >= 64) appliedDiscreteMask = 1UL;
            else appliedDiscreteMask = (appliedDiscreteMask << delta) | 1UL;
            maxAppliedDiscreteSeq = seq;
            return true;
        }
    }

    internal static class BoBoUdpSocketUtil
    {
        internal static void ConfigureLowLatencySocket(Socket sock, int sendBufBytes, int recvBufBytes)
        {
            if (sock == null) return;
            try { sock.SendBufferSize = sendBufBytes; } catch { }
            try { sock.ReceiveBufferSize = recvBufBytes; } catch { }
            try { sock.SendTimeout = 50; } catch { }
            try
            {
                // Disable Windows SIO_UDP_CONNRESET (prevents ICMP Port Unreachable from killing shared UdpClient!)
                const int SIO_UDP_CONNRESET = -1744830452;
                sock.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }
            try
            {
                // DSCP AF41 / WMM Video priority on Wi-Fi routers (best effort)
                if (sock.AddressFamily == AddressFamily.InterNetwork)
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x88);
            }
            catch { }
        }

        internal static void TrySetTcpDscp(Socket sock)
        {
            if (sock == null) return;
            try
            {
                if (sock.AddressFamily == AddressFamily.InterNetwork)
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x88);
            }
            catch { }
        }
    }
}
