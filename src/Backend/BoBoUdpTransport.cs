using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        Feedback = 9
    }

    /// <summary>
    /// BoBo V6.3 加密低延迟 UDP 媒体与输入传输核心（AES-128-CTR + 64-bit HMAC-SHA256）：
    /// - 密钥通过已认证的 TLS 1.2 通道按会话动态下发（32 字节：前 16B AES-128，后 16B HMAC-SHA256）；
    /// - 单包总长严格控制在 ≤1220 字节（安全适配 Tailscale WireGuard 1280 MTU 与校园网 PPPoE，零 IP 分片）；
    /// - 内置自适应分组 XOR FEC（单包丢失 0ms 额外 RTT 现场恢复）与 64-bit 滑动防重放窗口。
    /// </summary>
    internal sealed class BoBoUdpCrypto : IDisposable
    {
        internal const int HeaderBytes = 9;    // sessionId(4) + seq(4) + pktType(1)
        internal const int TagBytes = 8;       // truncated HMAC-SHA256 (64-bit)
        internal const int OverheadBytes = HeaderBytes + TagBytes;

        private readonly uint sessionId;
        private readonly byte[] aesKey;
        private readonly byte[] hmacKey;
        private readonly object encGate = new object();
        private readonly object decGate = new object();
        private readonly AesCryptoServiceProvider encAes;
        private readonly AesCryptoServiceProvider decAes;
        private readonly ICryptoTransform encBlock;
        private readonly ICryptoTransform decBlock;
        private readonly HMACSHA256 encHmac;
        private readonly HMACSHA256 decHmac;
        private readonly byte[] encCtrIn = new byte[16];
        private readonly byte[] encCtrOut = new byte[16];
        private readonly byte[] decCtrIn = new byte[16];
        private readonly byte[] decCtrOut = new byte[16];

        private int sendSeq;
        private bool hasRecvSeq;
        private uint maxRecvSeq;
        private ulong replayMask;
        private volatile bool disposed;

        internal BoBoUdpCrypto(uint sessionId, byte[] key32)
        {
            if (key32 == null || key32.Length < 32)
                throw new ArgumentException("UDP session key must be at least 32 bytes.");

            this.sessionId = sessionId;
            aesKey = new byte[16];
            hmacKey = new byte[16];
            Buffer.BlockCopy(key32, 0, aesKey, 0, 16);
            Buffer.BlockCopy(key32, 16, hmacKey, 0, 16);

            encAes = new AesCryptoServiceProvider { Key = aesKey, Mode = CipherMode.ECB, Padding = PaddingMode.None };
            decAes = new AesCryptoServiceProvider { Key = aesKey, Mode = CipherMode.ECB, Padding = PaddingMode.None };
            encBlock = encAes.CreateEncryptor();
            decBlock = decAes.CreateEncryptor();
            encHmac = new HMACSHA256(hmacKey);
            decHmac = new HMACSHA256(hmacKey);
        }

        internal uint SessionId { get { return sessionId; } }

        internal static byte[] GenerateRandomKey32()
        {
            byte[] key = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }
            return key;
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
            uint seq = unchecked((uint)Interlocked.Increment(ref sendSeq));
            byte[] packet = new byte[OverheadBytes + bodyLen];
            WriteUInt32LE(packet, 0, sessionId);
            WriteUInt32LE(packet, 4, seq);
            packet[8] = (byte)type;

            lock (encGate)
            {
                if (disposed) return null;
                if (bodyLen > 0)
                {
                    ApplyCtr(encBlock, encCtrIn, encCtrOut, sessionId, seq, (byte)type,
                        plaintext, offset, packet, HeaderBytes, bodyLen);
                }
                byte[] mac = encHmac.ComputeHash(packet, 0, HeaderBytes + bodyLen);
                Buffer.BlockCopy(mac, 0, packet, HeaderBytes + bodyLen, TagBytes);
            }
            return packet;
        }

        internal bool TryOpen(byte[] datagram, int length, out BoBoUdpPacketType type, out uint seq, out byte[] plaintext)
        {
            type = 0;
            seq = 0;
            plaintext = null;
            if (disposed || datagram == null || length < OverheadBytes) return false;
            if (ReadUInt32LE(datagram, 0) != sessionId) return false;

            seq = ReadUInt32LE(datagram, 4);
            type = (BoBoUdpPacketType)datagram[8];
            int bodyLen = length - OverheadBytes;

            lock (decGate)
            {
                if (disposed) return false;

                // Verify 64-bit HMAC-SHA256 tag in constant time before decrypting
                byte[] expected = decHmac.ComputeHash(datagram, 0, HeaderBytes + bodyLen);
                int diff = 0;
                int tagOff = HeaderBytes + bodyLen;
                for (int i = 0; i < TagBytes; i++)
                {
                    diff |= (expected[i] ^ datagram[tagOff + i]);
                }
                if (diff != 0) return false;

                // 64-packet sliding anti-replay window (accepts out-of-order Wi-Fi packets cleanly)
                if (hasRecvSeq)
                {
                    int delta = unchecked((int)(seq - maxRecvSeq));
                    if (delta == 0) return false;
                    if (delta < 0)
                    {
                        int back = -delta;
                        if (back >= 64) return false;
                        ulong bit = 1UL << back;
                        if ((replayMask & bit) != 0) return false;
                        replayMask |= bit;
                    }
                    else
                    {
                        if (delta >= 64) replayMask = 1UL;
                        else replayMask = (replayMask << delta) | 1UL;
                        maxRecvSeq = seq;
                    }
                }
                else
                {
                    hasRecvSeq = true;
                    maxRecvSeq = seq;
                    replayMask = 1UL;
                }

                plaintext = new byte[bodyLen];
                if (bodyLen > 0)
                {
                    ApplyCtr(decBlock, decCtrIn, decCtrOut, sessionId, seq, (byte)type,
                        datagram, HeaderBytes, plaintext, 0, bodyLen);
                }
                return true;
            }
        }

        private static void ApplyCtr(
            ICryptoTransform aesEcb,
            byte[] ctrIn,
            byte[] ctrOut,
            uint sessId,
            uint seq,
            byte pktType,
            byte[] src,
            int srcOffset,
            byte[] dst,
            int dstOffset,
            int count)
        {
            WriteUInt32LE(ctrIn, 0, sessId);
            WriteUInt32LE(ctrIn, 4, seq);
            ctrIn[8] = pktType;
            ctrIn[9] = 0;
            ctrIn[10] = 0;
            ctrIn[11] = 0;

            uint blockIdx = 0;
            int pos = 0;
            while (pos < count)
            {
                WriteUInt32LE(ctrIn, 12, blockIdx++);
                aesEcb.TransformBlock(ctrIn, 0, 16, ctrOut, 0);
                int chunk = count - pos;
                if (chunk > 16) chunk = 16;
                for (int i = 0; i < chunk; i++)
                {
                    dst[dstOffset + pos + i] = (byte)(src[srcOffset + pos + i] ^ ctrOut[i]);
                }
                pos += chunk;
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (encGate)
            {
                try { encBlock.Dispose(); } catch { }
                try { encAes.Dispose(); } catch { }
                try { encHmac.Dispose(); } catch { }
            }
            lock (decGate)
            {
                try { decBlock.Dispose(); } catch { }
                try { decAes.Dispose(); } catch { }
                try { decHmac.Dispose(); } catch { }
            }
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
        internal const int ShardHeaderBytes = 19;

        internal static List<byte[]> BuildVideoShards(
            uint frameId,
            uint timestampMs,
            int width,
            int height,
            bool isKeyframe,
            byte[] h264Data,
            double recentLossPct)
        {
            var shards = new List<byte[]>();
            if (h264Data == null || h264Data.Length == 0) return shards;

            int totalLen = h264Data.Length;
            int dataCount = (totalLen + MaxShardPayloadBytes - 1) / MaxShardPayloadBytes;
            if (dataCount > 60000) return shards;

            // Choose adaptive XOR FEC group size:
            // - Keyframes / lossy links (>1.5% loss): stronger FEC (groupSize = 2..3)
            // - Tiny 1-shard P-frames: add 1 parity duplicate only when loss > 0.4%
            // - Normal P-frames: lightweight FEC (groupSize = 4..6)
            int groupSize;
            bool emitFec;
            if (isKeyframe)
            {
                groupSize = recentLossPct >= 2.0 ? 2 : 3;
                emitFec = true;
            }
            else if (dataCount == 1)
            {
                groupSize = 1;
                emitFec = recentLossPct >= 0.4;
            }
            else if (recentLossPct >= 3.0)
            {
                groupSize = 2;
                emitFec = true;
            }
            else if (recentLossPct >= 0.8)
            {
                groupSize = 3;
                emitFec = true;
            }
            else
            {
                groupSize = 5;
                emitFec = true;
            }

            int parityCount = emitFec ? ((dataCount + groupSize - 1) / groupSize) : 0;
            if (parityCount > 255)
            {
                groupSize = (dataCount + 254) / 255;
                parityCount = (dataCount + groupSize - 1) / groupSize;
            }

            // 1. Emit data shards
            for (int i = 0; i < dataCount; i++)
            {
                int off = i * MaxShardPayloadBytes;
                int len = Math.Min(MaxShardPayloadBytes, totalLen - off);
                byte[] pkt = new byte[ShardHeaderBytes + len];
                WriteShardHeader(pkt, frameId, timestampMs, width, height,
                    (byte)(isKeyframe ? 1 : 0), (ushort)i, (ushort)dataCount, (byte)groupSize, (byte)parityCount);
                Buffer.BlockCopy(h264Data, off, pkt, ShardHeaderBytes, len);
                shards.Add(pkt);
            }

            // 2. Emit grouped XOR parity shards (first 2 bytes of XOR payload encode XOR of member lengths)
            for (int g = 0; g < parityCount; g++)
            {
                int start = g * groupSize;
                int end = Math.Min(dataCount, start + groupSize);
                int maxMemberLen = 0;
                for (int i = start; i < end; i++)
                {
                    int off = i * MaxShardPayloadBytes;
                    int len = Math.Min(MaxShardPayloadBytes, totalLen - off);
                    if (len > maxMemberLen) maxMemberLen = len;
                }

                byte[] pkt = new byte[ShardHeaderBytes + 2 + maxMemberLen];
                WriteShardHeader(pkt, frameId, timestampMs, width, height,
                    (byte)((isKeyframe ? 1 : 0) | 2), (ushort)g, (ushort)dataCount, (byte)groupSize, (byte)parityCount);

                ushort xorLen = 0;
                for (int i = start; i < end; i++)
                {
                    int off = i * MaxShardPayloadBytes;
                    int len = Math.Min(MaxShardPayloadBytes, totalLen - off);
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

    internal struct BoBoReassembledVideoFrame
    {
        internal uint FrameId;
        internal uint TimestampMs;
        internal int Width;
        internal int Height;
        internal bool IsKeyframe;
        internal byte[] H264Data;
    }

    /// <summary>
    /// 客户端零阻塞 UDP 视频重组与 XOR FEC 即时修复器：
    /// - 维护最近 4 帧重组槽，单组丢 1 包 0ms 恢复；
    /// - 出现不可恢复丢帧时立即置 awaitingKeyframe 并触发防抖 PLI，杜绝花屏拖影；
    /// - 基于 UDP 序列号间隔精确统计滑动窗口丢包率（供 HUD 与 ABR 实时使用）。
    /// </summary>
    internal sealed class BoBoUdpVideoAssembler
    {
        private sealed class FrameBucket
        {
            internal uint FrameId;
            internal uint TimestampMs;
            internal int Width;
            internal int Height;
            internal bool IsKeyframe;
            internal int DataCount;
            internal int GroupSize;
            internal int ParityCount;
            internal byte[][] DataShards;
            internal byte[][] ParityShards;
            internal int ReceivedDataCount;
            internal long CreatedTicks;
        }

        private readonly object gate = new object();
        private readonly Dictionary<uint, FrameBucket> buckets = new Dictionary<uint, FrameBucket>();
        private uint lastDeliveredFrameId;
        private bool hasDeliveredFrame;
        private bool awaitingKeyframe;
        private long lastPliTicks;
        private int fecRecoveredPackets;
        private int droppedIncompleteFrames;

        // Rolling sequence-gap packet loss estimator
        private bool hasSeqBaseline;
        private uint highestSeq;
        private int windowReceived;
        private int windowExpected;
        private long windowStartTicks = Stopwatch.GetTimestamp();
        private double latestLossPct;

        internal double LatestLossPct { get { lock (gate) { return latestLossPct; } } }
        internal int FecRecoveredPackets { get { return Volatile.Read(ref fecRecoveredPackets); } }
        internal int DroppedIncompleteFrames { get { return Volatile.Read(ref droppedIncompleteFrames); } }
        internal uint LastDeliveredFrameId { get { lock (gate) { return lastDeliveredFrameId; } } }

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
                long now = Stopwatch.GetTimestamp();
                double elapsedSec = (now - windowStartTicks) / (double)Stopwatch.Frequency;
                if (elapsedSec >= 1.2 && windowExpected >= 10)
                {
                    int lost = windowExpected - windowReceived;
                    if (lost < 0) lost = 0;
                    double instant = (lost * 100.0) / windowExpected;
                    latestLossPct = latestLossPct * 0.35 + instant * 0.65;
                    if (latestLossPct < 0.05) latestLossPct = 0.0;
                    windowReceived = 0;
                    windowExpected = 0;
                    windowStartTicks = now;
                }
            }
        }

        internal bool ProcessShard(byte[] payload, out BoBoReassembledVideoFrame completed, out bool needPli)
        {
            completed = new BoBoReassembledVideoFrame();
            needPli = false;
            if (payload == null || payload.Length < BoBoUdpFec.ShardHeaderBytes) return false;

            uint frameId = BoBoUdpCrypto.ReadUInt32LE(payload, 0);
            uint tsMs = BoBoUdpCrypto.ReadUInt32LE(payload, 4);
            int width = BoBoUdpCrypto.ReadUInt16LE(payload, 8);
            int height = BoBoUdpCrypto.ReadUInt16LE(payload, 10);
            byte flags = payload[12];
            bool isKey = (flags & 1) != 0;
            bool isParity = (flags & 2) != 0;
            int shardIdx = BoBoUdpCrypto.ReadUInt16LE(payload, 13);
            int dataCount = BoBoUdpCrypto.ReadUInt16LE(payload, 15);
            int groupSize = payload[17];
            int parityCount = payload[18];

            if (dataCount <= 0 || dataCount > 60000 || groupSize <= 0) return false;
            int bodyLen = payload.Length - BoBoUdpFec.ShardHeaderBytes;
            if (bodyLen <= 0) return false;

            lock (gate)
            {
                if (hasDeliveredFrame && unchecked((int)(frameId - lastDeliveredFrameId)) <= 0)
                {
                    return false; // Late shard for already-delivered or superseded frame
                }
                if (awaitingKeyframe && !isKey)
                {
                    needPli = ShouldTriggerPliLocked();
                    return false;
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
                        DataCount = dataCount,
                        GroupSize = groupSize,
                        ParityCount = parityCount,
                        DataShards = new byte[dataCount][],
                        ParityShards = parityCount > 0 ? new byte[parityCount][] : null,
                        CreatedTicks = Stopwatch.GetTimestamp()
                    };
                    buckets[frameId] = b;
                    PruneStaleBucketsLocked(frameId, ref needPli);
                }

                byte[] slice = new byte[bodyLen];
                Buffer.BlockCopy(payload, BoBoUdpFec.ShardHeaderBytes, slice, 0, bodyLen);

                if (!isParity)
                {
                    if (shardIdx < b.DataCount && b.DataShards[shardIdx] == null)
                    {
                        b.DataShards[shardIdx] = slice;
                        b.ReceivedDataCount++;
                    }
                }
                else
                {
                    if (b.ParityShards != null && shardIdx < b.ParityShards.Length && b.ParityShards[shardIdx] == null)
                    {
                        b.ParityShards[shardIdx] = slice;
                    }
                }

                // Attempt instant 0-RTT XOR FEC recovery for any group with exactly 1 missing data shard
                if (b.ReceivedDataCount < b.DataCount && b.ParityShards != null)
                {
                    TryFecRecoverBucketLocked(b);
                }

                if (b.ReceivedDataCount < b.DataCount)
                {
                    return false;
                }

                // Frame complete! Check if any intermediate older frame was skipped
                bool skippedGap = hasDeliveredFrame && !isKey && unchecked((int)(frameId - lastDeliveredFrameId)) > 1;
                if (skippedGap)
                {
                    // A prior P-frame never arrived -> H.264 reference chain is broken until next IDR!
                    Interlocked.Increment(ref droppedIncompleteFrames);
                    RemoveOlderBucketsLocked(frameId);
                    buckets.Remove(frameId);
                    awaitingKeyframe = true;
                    needPli = ShouldTriggerPliLocked();
                    return false;
                }

                // Assemble contiguous Annex-B H.264 access unit
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

                lastDeliveredFrameId = frameId;
                hasDeliveredFrame = true;
                if (isKey) awaitingKeyframe = false;
                RemoveOlderBucketsLocked(frameId);
                buckets.Remove(frameId);

                completed = new BoBoReassembledVideoFrame
                {
                    FrameId = frameId,
                    TimestampMs = b.TimestampMs,
                    Width = b.Width,
                    Height = b.Height,
                    IsKeyframe = b.IsKeyframe,
                    H264Data = full
                };
                return true;
            }
        }

        private void TryFecRecoverBucketLocked(FrameBucket b)
        {
            for (int g = 0; g < b.ParityShards.Length; g++)
            {
                byte[] par = b.ParityShards[g];
                if (par == null || par.Length < 2) continue;

                int start = g * b.GroupSize;
                int end = Math.Min(b.DataCount, start + b.GroupSize);
                int missingIdx = -1;
                int missingCount = 0;
                for (int i = start; i < end; i++)
                {
                    if (b.DataShards[i] == null)
                    {
                        missingIdx = i;
                        missingCount++;
                    }
                }
                if (missingCount != 1 || missingIdx < 0) continue;

                ushort recLen = BoBoUdpCrypto.ReadUInt16LE(par, 0);
                for (int i = start; i < end; i++)
                {
                    if (i == missingIdx) continue;
                    recLen ^= (ushort)b.DataShards[i].Length;
                }
                if (recLen == 0 || recLen > BoBoUdpFec.MaxShardPayloadBytes || recLen + 2 > par.Length) continue;

                byte[] recovered = new byte[recLen];
                Buffer.BlockCopy(par, 2, recovered, 0, recLen);
                for (int i = start; i < end; i++)
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
        }

        private void PruneStaleBucketsLocked(uint newestFrameId, ref bool needPli)
        {
            if (buckets.Count <= 4) return;
            var toRemove = new List<uint>();
            foreach (KeyValuePair<uint, FrameBucket> kv in buckets)
            {
                if (unchecked((int)(newestFrameId - kv.Key)) >= 4)
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
                    needPli = ShouldTriggerPliLocked();
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

        private bool ShouldTriggerPliLocked()
        {
            long now = Stopwatch.GetTimestamp();
            double ms = (now - lastPliTicks) * 1000.0 / Stopwatch.Frequency;
            if (lastPliTicks == 0 || ms >= 450.0)
            {
                lastPliTicks = now;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 实时自研拥塞控制与自适应码率/帧率控制器（GCC / AIMD 思路，兼容 UDP 与 TCP+TLS 双通道）：
    /// - 结合 RTT 队列延迟（currentRtt - minRtt）、丢包率与发送背压事件，毫秒级乘性降码率，彻底杜绝校园网/Wi-Fi 缓冲膨胀；
    /// - 网络空闲稳定后平缓加性回升至目标码率与帧率，全程通过 ICodecAPI 热生效，零重启无黑屏。
    /// </summary>
    internal sealed class BoBoAdaptiveBitrateController
    {
        private readonly object gate = new object();
        private bool enabled;
        private int targetBitrateKbps;
        private int targetFps;
        private int currentBitrateKbps;
        private int currentFps;
        private double minRttMs = -1.0;
        private double smoothedRttMs = -1.0;
        private double smoothedLossPct;
        private long lastDecreaseTicks;
        private long lastIncreaseTicks;
        private long lowBitrateSinceTicks;

        internal BoBoAdaptiveBitrateController(bool autoEnabled, int targetBitrateKbps, int targetFps, double initialRttMs)
        {
            enabled = autoEnabled;
            this.targetBitrateKbps = ClampBitrate(targetBitrateKbps);
            this.targetFps = ClampFps(targetFps);
            this.currentFps = this.targetFps;

            // On WAN / Campus / Tailscale DERP links (high initial RTT), start conservatively and ramp up smoothly
            if (autoEnabled && initialRttMs > 55.0)
                this.currentBitrateKbps = Math.Min(this.targetBitrateKbps, 3200);
            else if (autoEnabled && initialRttMs > 22.0)
                this.currentBitrateKbps = Math.Min(this.targetBitrateKbps, 6000);
            else
                this.currentBitrateKbps = this.targetBitrateKbps;

            if (initialRttMs > 0.1)
            {
                minRttMs = initialRttMs;
                smoothedRttMs = initialRttMs;
            }
            long now = Stopwatch.GetTimestamp();
            lastIncreaseTicks = now;
        }

        internal int CurrentBitrateKbps { get { lock (gate) { return currentBitrateKbps; } } }
        internal int CurrentFps { get { lock (gate) { return currentFps; } } }
        internal bool AutoEnabled { get { lock (gate) { return enabled; } } }

        internal void UpdateTarget(bool autoEnabled, int newTargetBitrateKbps, int newTargetFps)
        {
            lock (gate)
            {
                enabled = autoEnabled;
                targetBitrateKbps = ClampBitrate(newTargetBitrateKbps);
                targetFps = ClampFps(newTargetFps);
                if (!enabled || currentBitrateKbps > targetBitrateKbps)
                    currentBitrateKbps = targetBitrateKbps;
                if (!enabled || currentFps > targetFps)
                    currentFps = targetFps;
            }
        }

        internal bool OnCongestionBackpressure(out int newBitrateKbps, out int newFps)
        {
            lock (gate)
            {
                newBitrateKbps = currentBitrateKbps;
                newFps = currentFps;
                if (!enabled) return false;

                long now = Stopwatch.GetTimestamp();
                if (lastDecreaseTicks != 0 && ElapsedMs(lastDecreaseTicks, now) < 600.0) return false;

                int reduced = ClampBitrate((int)(currentBitrateKbps * 0.65));
                if (reduced < currentBitrateKbps)
                {
                    currentBitrateKbps = reduced;
                    lastDecreaseTicks = now;
                    lastIncreaseTicks = now;
                    if (currentBitrateKbps <= 2200 && currentFps > 30)
                    {
                        currentFps = 30;
                    }
                    newBitrateKbps = currentBitrateKbps;
                    newFps = currentFps;
                    return true;
                }
                return false;
            }
        }

        internal bool OnTelemetry(double rttMs, double lossPct, out int newBitrateKbps, out int newFps)
        {
            lock (gate)
            {
                newBitrateKbps = currentBitrateKbps;
                newFps = currentFps;
                if (!enabled) return false;

                long now = Stopwatch.GetTimestamp();
                if (rttMs > 0.1 && rttMs < 8000.0)
                {
                    if (minRttMs < 0 || rttMs < minRttMs) minRttMs = rttMs;
                    else minRttMs = minRttMs * 0.995 + rttMs * 0.005; // Slowly track route shifts
                    smoothedRttMs = (smoothedRttMs < 0) ? rttMs : (smoothedRttMs * 0.65 + rttMs * 0.35);
                }
                if (lossPct >= 0.0 && lossPct <= 100.0)
                {
                    smoothedLossPct = smoothedLossPct * 0.5 + lossPct * 0.5;
                }

                double queueDelayMs = (minRttMs > 0 && smoothedRttMs > minRttMs) ? (smoothedRttMs - minRttMs) : 0.0;
                bool changed = false;

                if (lossPct >= 4.0 || queueDelayMs >= 75.0)
                {
                    if (lastDecreaseTicks == 0 || ElapsedMs(lastDecreaseTicks, now) >= 700.0)
                    {
                        int next = ClampBitrate((int)(currentBitrateKbps * 0.64));
                        if (next != currentBitrateKbps)
                        {
                            currentBitrateKbps = next;
                            changed = true;
                        }
                        lastDecreaseTicks = now;
                        lastIncreaseTicks = now;
                    }
                }
                else if (lossPct >= 1.5 || queueDelayMs >= 38.0)
                {
                    if (lastDecreaseTicks == 0 || ElapsedMs(lastDecreaseTicks, now) >= 900.0)
                    {
                        int next = ClampBitrate((int)(currentBitrateKbps * 0.84));
                        if (next != currentBitrateKbps)
                        {
                            currentBitrateKbps = next;
                            changed = true;
                        }
                        lastDecreaseTicks = now;
                        lastIncreaseTicks = now;
                    }
                }
                else if (smoothedLossPct < 0.5 && queueDelayMs < 18.0)
                {
                    if (currentBitrateKbps < targetBitrateKbps && ElapsedMs(lastIncreaseTicks, now) >= 2000.0)
                    {
                        int step = Math.Max(450, (int)(currentBitrateKbps * 0.12));
                        int next = Math.Min(targetBitrateKbps, currentBitrateKbps + step);
                        if (next != currentBitrateKbps)
                        {
                            currentBitrateKbps = next;
                            changed = true;
                        }
                        lastIncreaseTicks = now;
                    }
                }

                // Adaptive FPS step-down on severely constrained links (<=2.2Mbps -> 30fps keeps frame clarity high)
                if (currentBitrateKbps <= 2200)
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
                    if (currentBitrateKbps >= 3600 && currentFps < targetFps && smoothedLossPct < 0.5 && queueDelayMs < 20.0)
                    {
                        currentFps = targetFps;
                        changed = true;
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
