using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal enum BoBoPacketType : byte
    {
        ControlJson = 0x01,
        VideoH264 = 0x02,
        AudioAdpcm = 0x03,
        InputEvent = 0x04,
        FileChunk = 0x05
    }

    internal sealed class BoBoMonitorDescriptor
    {
        internal int Index;
        internal string Name = "DISPLAY1";
        internal int Width = 1920;
        internal int Height = 1080;
        internal bool IsPrimary = true;
    }

    internal sealed class BoBoDiscoveredPeer
    {
        internal string NodeId = "";
        internal string DeviceCode = "";
        internal string DeviceName = "";
        internal string Host = "";
        internal int Port = BoBoProtocol.DefaultTcpPort;
        internal string Version = "6.2.1";
        internal string Platform = BoBoProtocol.PlatformWindows;
        internal string HostControlMode = BoBoProtocol.ControlModeFull;
        internal bool AllowIncoming = true;
        internal DateTime LastSeenUtc = DateTime.UtcNow;
    }

    internal sealed class BoBoLocalPairSession
    {
        internal string Pin = "";
        internal DateTime CreatedUtc;
        internal DateTime ExpiresUtc;

        internal int RemainingSeconds
        {
            get
            {
                int rem = (int)Math.Ceiling((ExpiresUtc - DateTime.UtcNow).TotalSeconds);
                return rem > 0 ? rem : 0;
            }
        }

        internal bool IsExpired
        {
            get { return DateTime.UtcNow >= ExpiresUtc; }
        }
    }

    internal sealed class BoBoFileTransferProgress
    {
        internal string TransferId = "";
        internal string FileName = "";
        internal long BytesTransferred;
        internal long TotalBytes;
        internal bool Completed;
        internal string SavedPath = "";
        internal string Error = "";
    }

    internal struct BoBoFrame
    {
        internal BoBoPacketType Type;
        internal uint FlagsOrTimestamp;
        internal byte[] Payload;
    }

    /// <summary>
    /// BoBo V6 专属二进制帧协议与序列化工具（TCP+TLS 1.2 端口 49810，LAN UDP 发现端口 49812）。
    /// 彻底与第三方 Sunshine 47989 / Moonlight / RustDesk 端口隔离，仅 BoBo远程 双端可互通。
    /// </summary>
    internal static class BoBoProtocol
    {
        internal const string ProtocolMagic = "BOBO6";
        internal const string ProtocolVersion = "6.2";
        internal const string PlatformWindows = "windows";
        internal const string PlatformMacOS = "macos";
        internal const string PlatformAndroid = "android";
        internal const string PlatformIOS = "ios";
        internal const string ControlModeFull = "full";
        internal const string ControlModeViewOnly = "view_only";

        internal const byte InputKindTouch = 7;
        internal const byte InputKindMobileNav = 8;

        internal const int MobileNavBack = 1;
        internal const int MobileNavHome = 2;
        internal const int MobileNavRecents = 3;
        internal const int MobileNavNotifications = 4;
        internal const int MobileNavLock = 5;
        internal const int MobileNavVolumeUp = 6;
        internal const int MobileNavVolumeDown = 7;
        internal const int MobileNavPowerDialog = 8;

        internal const int DefaultTcpPort = 49810;
        internal const int DiscoveryUdpPort = 49812;
        internal const int HeaderBytes = 9;
        internal const int MaxControlPayloadBytes = 2 * 1024 * 1024;
        internal const int MaxVideoPayloadBytes = 16 * 1024 * 1024;
        internal const int MaxFileChunkPayloadBytes = 512 * 1024;

        internal static async Task WriteFrameAsync(
            Stream stream,
            BoBoPacketType type,
            uint flagsOrTs,
            byte[] payload,
            int offset,
            int count,
            CancellationToken ct)
        {
            int actualCount = (payload != null && count > 0) ? count : 0;
            if (actualCount == 0)
            {
                byte[] header = new byte[HeaderBytes];
                header[0] = (byte)type;
                WriteInt32LE(header, 1, 0);
                WriteUInt32LE(header, 5, flagsOrTs);
                await stream.WriteAsync(header, 0, HeaderBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }
            if (actualCount <= 4096)
            {
                // Combine header + small payload (input events, control JSON, audio packets) into a single TLS record write.
                byte[] packet = new byte[HeaderBytes + actualCount];
                packet[0] = (byte)type;
                WriteInt32LE(packet, 1, actualCount);
                WriteUInt32LE(packet, 5, flagsOrTs);
                Buffer.BlockCopy(payload, offset, packet, HeaderBytes, actualCount);
                await stream.WriteAsync(packet, 0, packet.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return;
            }
            byte[] hdr = new byte[HeaderBytes];
            hdr[0] = (byte)type;
            WriteInt32LE(hdr, 1, actualCount);
            WriteUInt32LE(hdr, 5, flagsOrTs);
            await stream.WriteAsync(hdr, 0, HeaderBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, offset, actualCount, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        internal static Task WriteJsonAsync(Stream stream, Dictionary<string, object> obj, CancellationToken ct)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = MaxControlPayloadBytes;
            byte[] bytes = Encoding.UTF8.GetBytes(ser.Serialize(obj));
            return WriteFrameAsync(stream, BoBoPacketType.ControlJson, 0, bytes, 0, bytes.Length, ct);
        }

        internal static async Task<bool> ReadFrameAsync(Stream stream, CancellationToken ct, Action<BoBoFrame> onFrame)
        {
            byte[] header = new byte[HeaderBytes];
            if (!await ReadExactAsync(stream, header, 0, HeaderBytes, ct).ConfigureAwait(false))
            {
                return false;
            }

            BoBoPacketType type = (BoBoPacketType)header[0];
            int length = ReadInt32LE(header, 1);
            uint flags = ReadUInt32LE(header, 5);

            int maxAllowed = type == BoBoPacketType.VideoH264 ? MaxVideoPayloadBytes : MaxControlPayloadBytes;
            if (length < 0 || length > maxAllowed)
            {
                throw new InvalidDataException("BoBo6 frame payload length out of bounds: " + length);
            }

            byte[] payload = length > 0 ? new byte[length] : new byte[0];
            if (length > 0 && !await ReadExactAsync(stream, payload, 0, length, ct).ConfigureAwait(false))
            {
                return false;
            }

            if (onFrame != null)
            {
                onFrame(new BoBoFrame
                {
                    Type = type,
                    FlagsOrTimestamp = flags,
                    Payload = payload
                });
            }
            return true;
        }

        internal static async Task<BoBoFrame?> ReadOneFrameAsync(Stream stream, CancellationToken ct)
        {
            BoBoFrame? result = null;
            bool ok = await ReadFrameAsync(stream, ct, delegate(BoBoFrame f) { result = f; }).ConfigureAwait(false);
            return ok ? result : null;
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int n = await stream.ReadAsync(buffer, offset + readTotal, count - readTotal, ct).ConfigureAwait(false);
                if (n <= 0) return false;
                readTotal += n;
            }
            return true;
        }

        internal static byte[] BuildVideoPayload(int width, int height, bool isKeyframe, byte[] h264Data)
        {
            int h264Len = h264Data != null ? h264Data.Length : 0;
            byte[] buf = new byte[5 + h264Len];
            WriteUInt16LE(buf, 0, (ushort)(width & 0xFFFF));
            WriteUInt16LE(buf, 2, (ushort)(height & 0xFFFF));
            buf[4] = isKeyframe ? (byte)1 : (byte)0;
            if (h264Len > 0) Buffer.BlockCopy(h264Data, 0, buf, 5, h264Len);
            return buf;
        }

        internal static bool ParseVideoPayload(byte[] payload, out int width, out int height, out bool isKeyframe, out byte[] h264Data)
        {
            width = 0;
            height = 0;
            isKeyframe = false;
            h264Data = null;
            if (payload == null || payload.Length <= 5) return false;
            width = ReadUInt16LE(payload, 0);
            height = ReadUInt16LE(payload, 2);
            isKeyframe = (payload[4] & 1) != 0;
            int len = payload.Length - 5;
            h264Data = new byte[len];
            Buffer.BlockCopy(payload, 5, h264Data, 0, len);
            return true;
        }

        internal static byte[] BuildAudioPayload(int sampleRate, int channels, int sampleCount, byte[] adpcmData)
        {
            int len = adpcmData != null ? adpcmData.Length : 0;
            byte[] buf = new byte[7 + len];
            WriteInt32LE(buf, 0, sampleRate);
            buf[4] = (byte)(channels & 0xFF);
            WriteUInt16LE(buf, 5, (ushort)(sampleCount & 0xFFFF));
            if (len > 0) Buffer.BlockCopy(adpcmData, 0, buf, 7, len);
            return buf;
        }

        internal static bool ParseAudioPayload(byte[] payload, out int sampleRate, out int channels, out int sampleCount, out byte[] adpcmData)
        {
            sampleRate = 48000;
            channels = 2;
            sampleCount = 0;
            adpcmData = null;
            if (payload == null || payload.Length <= 7) return false;
            sampleRate = ReadInt32LE(payload, 0);
            channels = payload[4];
            sampleCount = ReadUInt16LE(payload, 5);
            int len = payload.Length - 7;
            adpcmData = new byte[len];
            Buffer.BlockCopy(payload, 7, adpcmData, 0, len);
            return true;
        }

        internal static byte[] BuildInputPayload(BoBoInputPacket pkt)
        {
            byte[] buf = new byte[6];
            buf[0] = pkt.Kind;
            buf[1] = pkt.Flags;
            WriteInt16LE(buf, 2, pkt.A);
            WriteInt16LE(buf, 4, pkt.B);
            return buf;
        }

        internal static bool ParseInputPayload(byte[] payload, out BoBoInputPacket pkt)
        {
            pkt = new BoBoInputPacket();
            if (payload == null || payload.Length < 6) return false;
            pkt.Kind = payload[0];
            pkt.Flags = payload[1];
            pkt.A = ReadInt16LE(payload, 2);
            pkt.B = ReadInt16LE(payload, 4);
            return true;
        }

        internal static BoBoInputPacket BuildTouchInput(int pointerId, int action, ushort nx, ushort ny)
        {
            byte flags = (byte)(((pointerId & 0x0F) << 4) | (action & 0x0F));
            return new BoBoInputPacket
            {
                Kind = InputKindTouch,
                Flags = flags,
                A = unchecked((short)nx),
                B = unchecked((short)ny)
            };
        }

        internal static BoBoInputPacket BuildMobileNavInput(int navAction)
        {
            return new BoBoInputPacket
            {
                Kind = InputKindMobileNav,
                Flags = 0,
                A = (short)navAction,
                B = 0
            };
        }

        internal static byte[] BuildFileChunkPayload(string transferId16, long offset, byte[] data, int count)
        {
            byte[] idBytes = new byte[16];
            byte[] rawId = Encoding.ASCII.GetBytes(transferId16 ?? "");
            Buffer.BlockCopy(rawId, 0, idBytes, 0, Math.Min(16, rawId.Length));
            byte[] buf = new byte[24 + count];
            Buffer.BlockCopy(idBytes, 0, buf, 0, 16);
            WriteInt64LE(buf, 16, offset);
            if (count > 0 && data != null) Buffer.BlockCopy(data, 0, buf, 24, count);
            return buf;
        }

        internal static bool ParseFileChunkPayload(byte[] payload, out string transferId16, out long offset, out byte[] chunkData)
        {
            transferId16 = "";
            offset = 0;
            chunkData = null;
            if (payload == null || payload.Length < 24) return false;
            int idLen = 0;
            while (idLen < 16 && payload[idLen] != 0) idLen++;
            transferId16 = Encoding.ASCII.GetString(payload, 0, idLen);
            offset = ReadInt64LE(payload, 16);
            int len = payload.Length - 24;
            chunkData = new byte[len];
            if (len > 0) Buffer.BlockCopy(payload, 24, chunkData, 0, len);
            return true;
        }

        internal static Dictionary<string, object> ParseJsonPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                string json = Encoding.UTF8.GetString(payload);
                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = MaxControlPayloadBytes;
                return ser.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        internal static string GetStr(Dictionary<string, object> map, string key, string fallback)
        {
            object val;
            if (map != null && map.TryGetValue(key, out val) && val != null) return val.ToString();
            return fallback;
        }

        internal static int GetInt(Dictionary<string, object> map, string key, int fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            try { return Convert.ToInt32(val); } catch { return fallback; }
        }

        internal static long GetLong(Dictionary<string, object> map, string key, long fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            try { return Convert.ToInt64(val); } catch { return fallback; }
        }

        internal static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            try { return Convert.ToBoolean(val); } catch { return fallback; }
        }

        internal static List<Dictionary<string, object>> SerializeMonitors(List<BoBoMonitorDescriptor> monitors)
        {
            var list = new List<Dictionary<string, object>>();
            if (monitors == null) return list;
            for (int i = 0; i < monitors.Count; i++)
            {
                var d = new Dictionary<string, object>();
                d["index"] = monitors[i].Index;
                d["name"] = monitors[i].Name ?? ("DISPLAY" + (i + 1));
                d["width"] = monitors[i].Width;
                d["height"] = monitors[i].Height;
                d["primary"] = monitors[i].IsPrimary;
                list.Add(d);
            }
            return list;
        }

        internal static List<BoBoMonitorDescriptor> ParseMonitors(Dictionary<string, object> map, string key)
        {
            var list = new List<BoBoMonitorDescriptor>();
            object val;
            if (map == null || !map.TryGetValue(key, out val)) return list;
            object[] arr = val as object[];
            if (arr == null) return list;
            for (int i = 0; i < arr.Length; i++)
            {
                var item = arr[i] as Dictionary<string, object>;
                if (item == null) continue;
                list.Add(new BoBoMonitorDescriptor
                {
                    Index = GetInt(item, "index", i),
                    Name = GetStr(item, "name", "DISPLAY" + (i + 1)),
                    Width = GetInt(item, "width", 1920),
                    Height = GetInt(item, "height", 1080),
                    IsPrimary = GetBool(item, "primary", i == 0)
                });
            }
            return list;
        }

        private static void WriteInt16LE(byte[] b, int o, short v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static short ReadInt16LE(byte[] b, int o)
        {
            return (short)(b[o] | (b[o + 1] << 8));
        }

        private static void WriteUInt16LE(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static ushort ReadUInt16LE(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        private static void WriteInt32LE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static int ReadInt32LE(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }

        private static void WriteUInt32LE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
            b[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static uint ReadUInt32LE(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void WriteInt64LE(byte[] b, int o, long v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)((v >> (i * 8)) & 0xFF);
        }

        private static long ReadInt64LE(byte[] b, int o)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= ((long)b[o + i]) << (i * 8);
            return v;
        }
    }
}
