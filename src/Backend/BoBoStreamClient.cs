using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DualRemote
{
    internal sealed class BoBoHostProbeResult
    {
        internal bool Reachable;
        internal bool IsBoBoHost;
        internal bool AllowIncoming;
        internal bool Authorized;
        internal string Host = "";
        internal int Port = BoBoProtocol.DefaultTcpPort;
        internal string HostNodeId = "";
        internal string HostDeviceCode = "";
        internal string HostName = "";
        internal string HostThumbprint = "";
        internal string Platform = BoBoProtocol.PlatformWindows;
        internal string HostControlMode = BoBoProtocol.ControlModeFull;
        internal int ScreenOrientation = 0;
        internal string Nonce = "";
        internal List<BoBoMonitorDescriptor> Monitors = new List<BoBoMonitorDescriptor>();
        internal string Error = "";
    }

    internal sealed class BoBoStreamStartOptions
    {
        internal string Host = "";
        internal int Port = BoBoProtocol.DefaultTcpPort;
        internal int MonitorIndex = 0;
        internal int Width = 1920;
        internal int Height = 1080;
        internal int Fps = 60;
        internal int BitrateKbps = 14000;
        internal bool MuteHostAudio = false;
        internal bool RequestPrivacy = false;
        internal bool SuperDisplay = true;
        internal int ClientWidth = 0;
        internal int ClientHeight = 0;
    }

    /// <summary>
    /// BoBo V6 全内置主控端客户端（TLS 1.2 + 进程内 BoBoCore.dll 硬件解码渲染与文件传输）：
    /// - 仅连接运行 BoBo远程 的目标端口 49810，彻底杜绝误连外部 Sunshine / Moonlight / RustDesk；
    /// - 支持毫秒级探测、4 位临时配对码 HMAC 认证、实时推流/切屏/画质热切换、直接防窥控制与 SHA-256 文件传输。
    /// </summary>
    internal sealed class BoBoStreamClient : IDisposable
    {
        private readonly BoBoIdentityStore identity;
        private readonly ConcurrentQueue<PendingSend> sendQueue = new ConcurrentQueue<PendingSend>();
        private readonly object sendLoopGate = new object();
        private bool sendLoopRunning;

        private sealed class PendingSend
        {
            internal BoBoPacketType Type;
            internal uint Flags;
            internal byte[] Payload;
            internal TaskCompletionSource<bool> Completion;
        }

        private TcpClient tcp;
        private SslStream ssl;
        private CancellationTokenSource cts;
        private BoBoCoreInterop.BoBoClientNativeHandle nativeClient;
        private volatile bool connected;
        private double latestRttMs;
        private long videoFramesReceived;
        private List<BoBoMonitorDescriptor> remoteMonitors = new List<BoBoMonitorDescriptor>();

        internal event Action<List<BoBoMonitorDescriptor>, int, int, int> StreamStarted;
        internal event Action<bool, bool, int, int> SuperDisplayStateChanged;
        internal event Action<string, string> StreamError;
        internal event Action<string> Disconnected;
        internal event Action<bool> RemotePrivacyStateChanged;
        internal event Action<double> RttUpdated;
        internal event Action<byte[], bool, int, int> RawVideoFrameReceived;

        internal bool SuperDisplayActive { get; private set; }
        internal bool SuperDisplayNativeMatch { get; private set; }
        internal string RemotePlatform { get; private set; }
        internal string RemoteHostControlMode { get; private set; }

        internal BoBoStreamClient(BoBoIdentityStore identityStore)
        {
            identity = identityStore ?? BoBoIdentityStore.Default;
        }

        internal bool IsConnected { get { return connected; } }
        internal double LatestRttMs { get { return latestRttMs; } }
        internal long VideoFramesReceived { get { return Interlocked.Read(ref videoFramesReceived); } }
        internal List<BoBoMonitorDescriptor> RemoteMonitors { get { return remoteMonitors; } }

        internal static async Task<BoBoHostProbeResult> ProbeHostAsync(
            string host,
            int port,
            BoBoIdentityStore identityStore,
            int timeoutMs,
            CancellationToken ct)
        {
            var res = new BoBoHostProbeResult { Host = host ?? "", Port = port > 0 ? port : BoBoProtocol.DefaultTcpPort };
            BoBoIdentityStore id = identityStore ?? BoBoIdentityStore.Default;
            id.EnsureLoaded();

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(timeoutMs > 500 ? timeoutMs : 5000);
                using (var client = new TcpClient())
                {
                    try
                    {
                        client.NoDelay = true;
                        await ConnectWithCancelAsync(client, res.Host, res.Port, timeoutCts.Token).ConfigureAwait(false);
                        res.Reachable = true;

                        string serverThumb = "";
                        using (var sslStream = new SslStream(client.GetStream(), false,
                            delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                            {
                                if (cert != null) serverThumb = BoBoIdentityStore.ComputeCertSha256Hex(cert.GetRawCertData());
                                return cert != null;
                            }))
                        {
                            await sslStream.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                            await SendHelloAsync(sslStream, id, timeoutCts.Token).ConfigureAwait(false);
                            BoBoFrame? reply = await BoBoProtocol.ReadOneFrameAsync(sslStream, timeoutCts.Token).ConfigureAwait(false);
                            if (!reply.HasValue || reply.Value.Type != BoBoPacketType.ControlJson)
                            {
                                res.Error = "目标主机未返回有效的 BoBo V6 握手响应。";
                                return res;
                            }

                            var ack = BoBoProtocol.ParseJsonPayload(reply.Value.Payload);
                            if (ack == null || BoBoProtocol.GetStr(ack, "protocol", "") != BoBoProtocol.ProtocolMagic)
                            {
                                res.Error = "目标地址并非 BoBo远程 V6 客户端。";
                                return res;
                            }

                            res.IsBoBoHost = true;
                            res.HostNodeId = BoBoProtocol.GetStr(ack, "nodeId", "");
                            res.HostDeviceCode = BoBoProtocol.GetStr(ack, "deviceCode", "");
                            res.HostName = BoBoProtocol.GetStr(ack, "deviceName", res.Host);
                            res.HostThumbprint = string.IsNullOrEmpty(serverThumb)
                                ? BoBoProtocol.GetStr(ack, "certThumbprint", "")
                                : serverThumb;
                            res.AllowIncoming = BoBoProtocol.GetBool(ack, "allowIncoming", true);
                            res.Authorized = BoBoProtocol.GetBool(ack, "authorized", false);
                            res.Platform = BoBoProtocol.GetStr(ack, "platform", BoBoProtocol.PlatformWindows);
                            res.HostControlMode = BoBoProtocol.GetStr(ack, "hostControlMode", BoBoProtocol.ControlModeFull);
                            res.ScreenOrientation = BoBoProtocol.GetInt(ack, "screenOrientation", 0);
                            res.Nonce = BoBoProtocol.GetStr(ack, "nonce", "");
                            res.Monitors = BoBoProtocol.ParseMonitors(ack, "monitors");
                            return res;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        res.Error = "连接对方 BoBo远程 超时。请确认对方已打开 BoBo远程 且网络互通。";
                        return res;
                    }
                    catch (SocketException)
                    {
                        res.Error = "未检测到对方打开 BoBo远程（端口 " + res.Port + " 未开放）。请先在被控端启动 BoBo远程。";
                        return res;
                    }
                    catch (Exception ex)
                    {
                        res.Error = "连接对方 BoBo远程 失败：" + ex.Message;
                        return res;
                    }
                }
            }
        }

        internal static async Task<Tuple<bool, string, string>> PairWithPinAsync(
            string host,
            int port,
            string fourDigitPin,
            BoBoIdentityStore identityStore,
            CancellationToken ct)
        {
            BoBoIdentityStore id = identityStore ?? BoBoIdentityStore.Default;
            id.EnsureLoaded();
            int targetPort = port > 0 ? port : BoBoProtocol.DefaultTcpPort;

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(12000);
                using (var client = new TcpClient())
                {
                    try
                    {
                        client.NoDelay = true;
                        await ConnectWithCancelAsync(client, host, targetPort, timeoutCts.Token).ConfigureAwait(false);
                        string serverThumb = "";
                        using (var sslStream = new SslStream(client.GetStream(), false,
                            delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                            {
                                if (cert != null) serverThumb = BoBoIdentityStore.ComputeCertSha256Hex(cert.GetRawCertData());
                                return cert != null;
                            }))
                        {
                            await sslStream.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                            await SendHelloAsync(sslStream, id, timeoutCts.Token).ConfigureAwait(false);
                            BoBoFrame? ackFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, timeoutCts.Token).ConfigureAwait(false);
                            if (!ackFrame.HasValue || ackFrame.Value.Type != BoBoPacketType.ControlJson)
                                return Tuple.Create(false, "", "被控端未返回 BoBo 握手信息。");

                            var ack = BoBoProtocol.ParseJsonPayload(ackFrame.Value.Payload);
                            if (ack == null || BoBoProtocol.GetStr(ack, "protocol", "") != BoBoProtocol.ProtocolMagic)
                                return Tuple.Create(false, "", "目标不是 BoBo远程 V6 客户端。");

                            if (!BoBoProtocol.GetBool(ack, "allowIncoming", true))
                                return Tuple.Create(false, "", "对方已关闭「允许本机被控」，请在被控端 BoBo远程 中开启。");

                            string hostNodeId = BoBoProtocol.GetStr(ack, "nodeId", "");
                            string hostCode = BoBoProtocol.GetStr(ack, "deviceCode", "");
                            string hostName = BoBoProtocol.GetStr(ack, "deviceName", host);
                            string hostThumb = string.IsNullOrEmpty(serverThumb) ? BoBoProtocol.GetStr(ack, "certThumbprint", "") : serverThumb;
                            string nonce = BoBoProtocol.GetStr(ack, "nonce", "");

                            string proof = BoBoIdentityStore.ComputePinProof(fourDigitPin, nonce, id.CertThumbprint, hostThumb);
                            var req = new Dictionary<string, object>();
                            req["type"] = "PAIR_REQUEST";
                            req["proof"] = proof;
                            await BoBoProtocol.WriteJsonAsync(sslStream, req, timeoutCts.Token).ConfigureAwait(false);

                            BoBoFrame? resFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, timeoutCts.Token).ConfigureAwait(false);
                            if (!resFrame.HasValue || resFrame.Value.Type != BoBoPacketType.ControlJson)
                                return Tuple.Create(false, "", "被控端未返回配对结果。");

                            var result = BoBoProtocol.ParseJsonPayload(resFrame.Value.Payload);
                            bool ok = BoBoProtocol.GetBool(result, "ok", false);
                            if (!ok)
                            {
                                string err = BoBoProtocol.GetStr(result, "error", "4 位配对码校验失败，请重试。");
                                return Tuple.Create(false, hostName, err);
                            }

                            id.AuthorizeOrUpdatePeer(new BoBoAuthorizedPeer
                            {
                                NodeId = hostNodeId,
                                DeviceCode = hostCode,
                                DeviceName = hostName,
                                CertThumbprint = hostThumb,
                                LastHost = host
                            });
                            return Tuple.Create(true, hostName, "");
                        }
                    }
                    catch (SocketException)
                    {
                        return Tuple.Create(false, "", "未检测到对方打开 BoBo远程，请先启动被控端 BoBo远程 并生成 4 位配对码。");
                    }
                    catch (Exception ex)
                    {
                        return Tuple.Create(false, "", "配对通信失败：" + ex.Message);
                    }
                }
            }
        }

        internal static async Task<BoBoFileTransferProgress> SendFileAsync(
            string host,
            int port,
            string localFilePath,
            BoBoIdentityStore identityStore,
            Action<BoBoFileTransferProgress> onProgress,
            CancellationToken ct)
        {
            var prog = new BoBoFileTransferProgress
            {
                TransferId = Guid.NewGuid().ToString("N").Substring(0, 16),
                FileName = Path.GetFileName(localFilePath ?? "")
            };
            if (string.IsNullOrEmpty(localFilePath) || !File.Exists(localFilePath))
            {
                prog.Error = "待发送文件不存在。";
                return prog;
            }

            BoBoIdentityStore id = identityStore ?? BoBoIdentityStore.Default;
            id.EnsureLoaded();
            int targetPort = port > 0 ? port : BoBoProtocol.DefaultTcpPort;

            try
            {
                var fi = new FileInfo(localFilePath);
                prog.TotalBytes = fi.Length;
                string shaHex = ComputeFileSha256(localFilePath);

                using (var client = new TcpClient())
                {
                    client.NoDelay = true;
                    await ConnectWithCancelAsync(client, host, targetPort, ct).ConfigureAwait(false);
                    using (var sslStream = new SslStream(client.GetStream(), false, delegate { return true; }))
                    {
                        await sslStream.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                        await SendHelloAsync(sslStream, id, ct).ConfigureAwait(false);
                        BoBoFrame? ackFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, ct).ConfigureAwait(false);
                        if (!ackFrame.HasValue) { prog.Error = "握手失败。"; return prog; }
                        var ack = BoBoProtocol.ParseJsonPayload(ackFrame.Value.Payload);
                        if (!BoBoProtocol.GetBool(ack, "authorized", false))
                        {
                            prog.Error = "尚未与该设备完成 BoBo 配对授权，请先配对后再传文件。";
                            return prog;
                        }

                        var offer = new Dictionary<string, object>();
                        offer["type"] = "FILE_OFFER";
                        offer["transferId"] = prog.TransferId;
                        offer["fileName"] = prog.FileName;
                        offer["totalBytes"] = prog.TotalBytes;
                        offer["sha256"] = shaHex;
                        await BoBoProtocol.WriteJsonAsync(sslStream, offer, ct).ConfigureAwait(false);

                        BoBoFrame? accFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, ct).ConfigureAwait(false);
                        if (!accFrame.HasValue) { prog.Error = "对方未接受文件传输。"; return prog; }
                        var acc = BoBoProtocol.ParseJsonPayload(accFrame.Value.Payload);
                        if (BoBoProtocol.GetStr(acc, "type", "") != "FILE_ACCEPT")
                        {
                            prog.Error = BoBoProtocol.GetStr(acc, "message", "对方拒绝了文件传输。");
                            return prog;
                        }

                        byte[] chunkBuf = new byte[65536];
                        long offset = 0;
                        using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            int n;
                            while ((n = await fs.ReadAsync(chunkBuf, 0, chunkBuf.Length, ct).ConfigureAwait(false)) > 0)
                            {
                                byte[] framePayload = BoBoProtocol.BuildFileChunkPayload(prog.TransferId, offset, chunkBuf, n);
                                await BoBoProtocol.WriteFrameAsync(sslStream, BoBoPacketType.FileChunk, 0, framePayload, 0, framePayload.Length, ct).ConfigureAwait(false);
                                offset += n;
                                prog.BytesTransferred = offset;
                                if (onProgress != null) { try { onProgress(prog); } catch { } }
                            }
                        }

                        var done = new Dictionary<string, object>();
                        done["type"] = "FILE_DONE";
                        done["transferId"] = prog.TransferId;
                        await BoBoProtocol.WriteJsonAsync(sslStream, done, ct).ConfigureAwait(false);

                        BoBoFrame? finalFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, ct).ConfigureAwait(false);
                        if (!finalFrame.HasValue) { prog.Error = "等待对方文件校验回执超时。"; return prog; }
                        var finalObj = BoBoProtocol.ParseJsonPayload(finalFrame.Value.Payload);
                        if (BoBoProtocol.GetBool(finalObj, "ok", false))
                        {
                            prog.Completed = true;
                            prog.SavedPath = BoBoProtocol.GetStr(finalObj, "savedPath", "");
                            if (onProgress != null) { try { onProgress(prog); } catch { } }
                        }
                        else
                        {
                            prog.Error = BoBoProtocol.GetStr(finalObj, "error", "对方保存文件失败。");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                prog.Error = "文件传输失败：" + ex.Message;
            }
            return prog;
        }

        internal async Task<Tuple<bool, string, string>> ConnectAndStartStreamAsync(
            BoBoStreamStartOptions options,
            BoBoCoreInterop.BoBoClientNativeHandle nativeHandle,
            CancellationToken externalCt)
        {
            Disconnect();
            identity.EnsureLoaded();
            nativeClient = nativeHandle;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            CancellationToken ct = cts.Token;

            int targetPort = options.Port > 0 ? options.Port : BoBoProtocol.DefaultTcpPort;
            try
            {
                tcp = new TcpClient();
                tcp.NoDelay = true;
                tcp.ReceiveBufferSize = 1024 * 1024;
                tcp.SendBufferSize = 512 * 1024;
                ConfigureTcpKeepAlive(tcp);
                await ConnectWithCancelAsync(tcp, options.Host, targetPort, ct).ConfigureAwait(false);

                string serverThumb = "";
                ssl = new SslStream(tcp.GetStream(), false,
                    delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                    {
                        if (cert != null) serverThumb = BoBoIdentityStore.ComputeCertSha256Hex(cert.GetRawCertData());
                        return cert != null;
                    });
                await ssl.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                await SendHelloAsync(ssl, identity, ct).ConfigureAwait(false);

                BoBoFrame? ackFrame = await BoBoProtocol.ReadOneFrameAsync(ssl, ct).ConfigureAwait(false);
                if (!ackFrame.HasValue || ackFrame.Value.Type != BoBoPacketType.ControlJson)
                {
                    Disconnect();
                    return Tuple.Create(false, "HANDSHAKE_FAILED", "目标主机未返回有效 BoBo V6 握手。");
                }

                var ack = BoBoProtocol.ParseJsonPayload(ackFrame.Value.Payload);
                if (ack == null || BoBoProtocol.GetStr(ack, "protocol", "") != BoBoProtocol.ProtocolMagic)
                {
                    Disconnect();
                    return Tuple.Create(false, "NOT_BOBO", "目标主机未运行 BoBo远程 V6。");
                }

                if (!BoBoProtocol.GetBool(ack, "allowIncoming", true))
                {
                    Disconnect();
                    return Tuple.Create(false, "HOST_DISABLED", "对方已关闭「允许本机被控」，请在被控端 BoBo远程 中开启。");
                }

                if (!BoBoProtocol.GetBool(ack, "authorized", false))
                {
                    Disconnect();
                    return Tuple.Create(false, "PAIR_REQUIRED", "首次连接这台电脑，请先输入被控端 BoBo远程 生成的 4 位临时配对码。");
                }

                remoteMonitors = BoBoProtocol.ParseMonitors(ack, "monitors");
                RemotePlatform = BoBoProtocol.GetStr(ack, "platform", BoBoProtocol.PlatformWindows);
                RemoteHostControlMode = BoBoProtocol.GetStr(ack, "hostControlMode", BoBoProtocol.ControlModeFull);
                string hostNodeId = BoBoProtocol.GetStr(ack, "nodeId", "");
                string hostCode = BoBoProtocol.GetStr(ack, "deviceCode", "");
                string hostName = BoBoProtocol.GetStr(ack, "deviceName", options.Host);
                if (!string.IsNullOrEmpty(hostNodeId) && !string.IsNullOrEmpty(serverThumb))
                {
                    identity.AuthorizeOrUpdatePeer(new BoBoAuthorizedPeer
                    {
                        NodeId = hostNodeId,
                        DeviceCode = hostCode,
                        DeviceName = hostName,
                        CertThumbprint = serverThumb,
                        LastHost = options.Host
                    });
                }

                var startReq = new Dictionary<string, object>();
                startReq["type"] = "START_STREAM";
                startReq["monitorIndex"] = options.MonitorIndex;
                startReq["width"] = options.Width;
                startReq["height"] = options.Height;
                startReq["fps"] = options.Fps;
                startReq["bitrateKbps"] = options.BitrateKbps;
                startReq["muteHost"] = options.MuteHostAudio;
                startReq["requestPrivacy"] = options.RequestPrivacy;
                startReq["superDisplay"] = options.SuperDisplay;
                startReq["clientWidth"] = options.ClientWidth;
                startReq["clientHeight"] = options.ClientHeight;
                await SendJsonInternalAsync(startReq, ct).ConfigureAwait(false);

                connected = true;
                Task _rx = Task.Run(delegate { return ReceiveLoopAsync(ct); });
                Task _ping = Task.Run(delegate { return PingLoopAsync(ct); });
                return Tuple.Create(true, "OK", hostName);
            }
            catch (SocketException)
            {
                Disconnect();
                return Tuple.Create(false, "OFFLINE", "未检测到对方打开 BoBo远程（端口 " + targetPort + " 未响应）。请确认被控端已打开 BoBo远程。");
            }
            catch (Exception ex)
            {
                Disconnect();
                return Tuple.Create(false, "CONNECT_FAILED", "连接被控端失败：" + ex.Message);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            string disconnectReason = "连接已断开";
            try
            {
                while (!ct.IsCancellationRequested && connected && ssl != null)
                {
                    BoBoFrame? frameOpt = await BoBoProtocol.ReadOneFrameAsync(ssl, ct).ConfigureAwait(false);
                    if (!frameOpt.HasValue) break;
                    BoBoFrame frame = frameOpt.Value;

                    if (frame.Type == BoBoPacketType.VideoH264)
                    {
                        int w, h;
                        bool isKey;
                        byte[] h264;
                        if (BoBoProtocol.ParseVideoPayload(frame.Payload, out w, out h, out isKey, out h264))
                        {
                            Interlocked.Increment(ref videoFramesReceived);
                            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                            if (nc != null)
                            {
                                nc.PushVideo(h264, h264.Length, isKey, w, h);
                            }
                            Action<byte[], bool, int, int> rawEv = RawVideoFrameReceived;
                            if (rawEv != null) { try { rawEv(h264, isKey, w, h); } catch { } }
                        }
                        continue;
                    }

                    if (frame.Type == BoBoPacketType.AudioAdpcm)
                    {
                        int sr, ch, samples;
                        byte[] adpcm;
                        if (BoBoProtocol.ParseAudioPayload(frame.Payload, out sr, out ch, out samples, out adpcm))
                        {
                            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                            if (nc != null) nc.PushAudio(adpcm, adpcm.Length, sr, ch, samples);
                        }
                        continue;
                    }

                    if (frame.Type == BoBoPacketType.ControlJson)
                    {
                        var msg = BoBoProtocol.ParseJsonPayload(frame.Payload);
                        if (msg == null) continue;
                        string t = BoBoProtocol.GetStr(msg, "type", "");
                        if (t == "STREAM_STARTED")
                        {
                            List<BoBoMonitorDescriptor> mons = BoBoProtocol.ParseMonitors(msg, "monitors");
                            if (mons.Count > 0) remoteMonitors = mons;
                            int w = BoBoProtocol.GetInt(msg, "width", 1920);
                            int h = BoBoProtocol.GetInt(msg, "height", 1080);
                            int fps = BoBoProtocol.GetInt(msg, "fps", 60);
                            bool sdActive = BoBoProtocol.GetBool(msg, "superDisplayActive", false);
                            bool sdNative = BoBoProtocol.GetBool(msg, "superDisplayNativeMatch", false);
                            string startedPlat = BoBoProtocol.GetStr(msg, "platform", "");
                            if (!string.IsNullOrEmpty(startedPlat)) RemotePlatform = startedPlat;
                            string startedMode = BoBoProtocol.GetStr(msg, "hostControlMode", "");
                            if (!string.IsNullOrEmpty(startedMode)) RemoteHostControlMode = startedMode;
                            SuperDisplayActive = sdActive;
                            SuperDisplayNativeMatch = sdNative;
                            Action<List<BoBoMonitorDescriptor>, int, int, int> ev = StreamStarted;
                            if (ev != null) { try { ev(remoteMonitors, w, h, fps); } catch { } }
                            Action<bool, bool, int, int> sdEv = SuperDisplayStateChanged;
                            if (sdEv != null) { try { sdEv(sdActive, sdNative, w, h); } catch { } }
                        }
                        else if (t == "SUPER_DISPLAY_STATE")
                        {
                            bool sdActive = BoBoProtocol.GetBool(msg, "active", false);
                            bool sdNative = BoBoProtocol.GetBool(msg, "nativeMatch", false);
                            int w = BoBoProtocol.GetInt(msg, "width", 0);
                            int h = BoBoProtocol.GetInt(msg, "height", 0);
                            SuperDisplayActive = sdActive;
                            SuperDisplayNativeMatch = sdNative;
                            Action<bool, bool, int, int> sdEv = SuperDisplayStateChanged;
                            if (sdEv != null) { try { sdEv(sdActive, sdNative, w, h); } catch { } }
                        }
                        else if (t == "PONG")
                        {
                            long sentTicks = BoBoProtocol.GetLong(msg, "ts", 0);
                            if (sentTicks > 0)
                            {
                                double rtt = (Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency;
                                if (rtt >= 0 && rtt < 10000)
                                {
                                    latestRttMs = rtt;
                                    Action<double> rttEv = RttUpdated;
                                    if (rttEv != null) { try { rttEv(rtt); } catch { } }
                                }
                            }
                        }
                        else if (t == "PRIVACY_STATE")
                        {
                            bool act = BoBoProtocol.GetBool(msg, "active", false);
                            Action<bool> privEv = RemotePrivacyStateChanged;
                            if (privEv != null) { try { privEv(act); } catch { } }
                        }
                        else if (t == "ERROR")
                        {
                            string code = BoBoProtocol.GetStr(msg, "code", "ERROR");
                            string emsg = BoBoProtocol.GetStr(msg, "message", "远端返回错误。");
                            disconnectReason = emsg;
                            Action<string, string> errEv = StreamError;
                            if (errEv != null) { try { errEv(code, emsg); } catch { } }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) disconnectReason = ex.Message;
            }
            finally
            {
                bool wasConnected = connected;
                connected = false;
                if (wasConnected)
                {
                    Action<string> discEv = Disconnected;
                    if (discEv != null) { try { discEv(disconnectReason); } catch { } }
                }
            }
        }

        private async Task PingLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && connected)
            {
                try
                {
                    var ping = new Dictionary<string, object>();
                    ping["type"] = "PING";
                    ping["ts"] = Stopwatch.GetTimestamp();
                    await SendJsonInternalAsync(ping, ct).ConfigureAwait(false);
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }

        internal void SendInputPacket(BoBoInputPacket pkt)
        {
            if (!connected || ssl == null) return;
            byte[] payload = BoBoProtocol.BuildInputPayload(pkt);
            SendFrameFireAndForget(BoBoPacketType.InputEvent, 0, payload);
        }

        internal void SendTouch(int pointerId, int action, ushort nx, ushort ny)
        {
            SendInputPacket(BoBoProtocol.BuildTouchInput(pointerId, action, nx, ny));
        }

        internal void SendMobileNav(int navAction)
        {
            SendInputPacket(BoBoProtocol.BuildMobileNavInput(navAction));
        }

        internal void SendReconfigure(int monitorIndex, int width, int height, int fps, int bitrateKbps)
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "RECONFIGURE";
            cmd["monitorIndex"] = monitorIndex;
            cmd["width"] = width;
            cmd["height"] = height;
            cmd["fps"] = fps;
            cmd["bitrateKbps"] = bitrateKbps;
            SendJsonFireAndForget(cmd);
        }

        internal void SendRequestKeyframe()
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "REQUEST_KEYFRAME";
            SendJsonFireAndForget(cmd);
        }

        internal void SendSetMuteHost(bool mute)
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "SET_MUTE_HOST";
            cmd["mute"] = mute;
            SendJsonFireAndForget(cmd);
        }

        internal void SendSetPrivacy(bool enable)
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "SET_PRIVACY";
            cmd["enable"] = enable;
            SendJsonFireAndForget(cmd);
        }

        internal void SendSetSuperDisplay(bool enable, int clientWidth, int clientHeight)
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "SET_SUPER_DISPLAY";
            cmd["enable"] = enable;
            cmd["clientWidth"] = clientWidth;
            cmd["clientHeight"] = clientHeight;
            SendJsonFireAndForget(cmd);
        }

        internal void SendLockWorkstation()
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "LOCK_WORKSTATION";
            SendJsonFireAndForget(cmd);
        }

        internal void SendClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "CLIPBOARD_TEXT";
            cmd["text"] = text;
            cmd["injectUnicode"] = true;
            SendJsonFireAndForget(cmd);
        }

        internal void SendKeyCombo(int[] vks)
        {
            if (vks == null || vks.Length == 0 || !connected) return;
            for (int i = 0; i < vks.Length; i++)
            {
                SendInputPacket(new BoBoInputPacket { Kind = 5, Flags = 1, A = (short)vks[i], B = 0 });
            }
            for (int i = vks.Length - 1; i >= 0; i--)
            {
                SendInputPacket(new BoBoInputPacket { Kind = 5, Flags = 0, A = (short)vks[i], B = 0 });
            }
        }

        private void SendJsonFireAndForget(Dictionary<string, object> obj)
        {
            if (!connected || ssl == null) return;
            EnqueueSend(BoBoPacketType.ControlJson, 0, SerializeJsonPayload(obj), null);
        }

        private void SendFrameFireAndForget(BoBoPacketType type, uint flags, byte[] payload)
        {
            if (!connected || ssl == null) return;
            EnqueueSend(type, flags, payload ?? new byte[0], null);
        }

        private Task SendJsonInternalAsync(Dictionary<string, object> obj, CancellationToken ct)
        {
            if (ssl == null) return Task.FromResult(true);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.CanBeCanceled)
            {
                ct.Register(delegate { tcs.TrySetCanceled(); });
            }
            EnqueueSend(BoBoPacketType.ControlJson, 0, SerializeJsonPayload(obj), tcs);
            return tcs.Task;
        }

        private static byte[] SerializeJsonPayload(Dictionary<string, object> obj)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            ser.MaxJsonLength = BoBoProtocol.MaxControlPayloadBytes;
            return Encoding.UTF8.GetBytes(ser.Serialize(obj));
        }

        private void EnqueueSend(BoBoPacketType type, uint flags, byte[] payload, TaskCompletionSource<bool> tcs)
        {
            if (ssl == null)
            {
                if (tcs != null) tcs.TrySetResult(false);
                return;
            }
            sendQueue.Enqueue(new PendingSend
            {
                Type = type,
                Flags = flags,
                Payload = payload ?? new byte[0],
                Completion = tcs
            });
            bool startLoop = false;
            lock (sendLoopGate)
            {
                if (!sendLoopRunning)
                {
                    sendLoopRunning = true;
                    startLoop = true;
                }
            }
            if (startLoop)
            {
                Task.Run(delegate { return DrainSendQueueAsync(); });
            }
        }

        private async Task DrainSendQueueAsync()
        {
            while (true)
            {
                PendingSend item;
                if (!sendQueue.TryDequeue(out item))
                {
                    lock (sendLoopGate)
                    {
                        if (sendQueue.IsEmpty)
                        {
                            sendLoopRunning = false;
                            return;
                        }
                    }
                    continue;
                }

                SslStream s = ssl;
                CancellationTokenSource curCts = cts;
                CancellationToken ct = curCts != null ? curCts.Token : CancellationToken.None;
                if (s == null || ct.IsCancellationRequested)
                {
                    if (item.Completion != null) item.Completion.TrySetResult(false);
                    continue;
                }

                try
                {
                    await BoBoProtocol.WriteFrameAsync(s, item.Type, item.Flags, item.Payload, 0, item.Payload.Length, ct).ConfigureAwait(false);
                    if (item.Completion != null) item.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    if (item.Completion != null) item.Completion.TrySetException(ex);
                }
            }
        }

        private void DrainPendingSendQueueOnClose()
        {
            PendingSend dropped;
            while (sendQueue.TryDequeue(out dropped))
            {
                if (dropped.Completion != null) dropped.Completion.TrySetResult(false);
            }
        }

        private static void ConfigureTcpKeepAlive(TcpClient client)
        {
            if (client == null || client.Client == null) return;
            try
            {
                byte[] ka = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(ka, 0);
                BitConverter.GetBytes(5000u).CopyTo(ka, 4);
                BitConverter.GetBytes(1000u).CopyTo(ka, 8);
                client.Client.IOControl(IOControlCode.KeepAliveValues, ka, null);
            }
            catch { }
        }

        internal void Disconnect()
        {
            bool wasConnected = connected;
            connected = false;
            try
            {
                if (wasConnected && ssl != null)
                {
                    var disc = new Dictionary<string, object>();
                    disc["type"] = "DISCONNECT";
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EnqueueSend(BoBoPacketType.ControlJson, 0, SerializeJsonPayload(disc), tcs);
                    try { tcs.Task.Wait(250); } catch { }
                }
            }
            catch { }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                cts = null;
            }
            DrainPendingSendQueueOnClose();
            if (ssl != null)
            {
                try { ssl.Dispose(); } catch { }
                ssl = null;
            }
            if (tcp != null)
            {
                try { tcp.Close(); } catch { }
                tcp = null;
            }
            nativeClient = null;
        }

        public void Dispose()
        {
            Disconnect();
        }

        private static Task SendHelloAsync(SslStream sslStream, BoBoIdentityStore id, CancellationToken ct)
        {
            var hello = new Dictionary<string, object>();
            hello["type"] = "HELLO";
            hello["protocol"] = BoBoProtocol.ProtocolMagic;
            hello["protocolVersion"] = BoBoProtocol.ProtocolVersion;
            hello["platform"] = BoBoProtocol.PlatformWindows;
            hello["nodeId"] = id.NodeId;
            hello["deviceCode"] = id.DeviceCode;
            hello["deviceName"] = id.DeviceName;
            hello["certThumbprint"] = id.CertThumbprint;
            hello["version"] = AppUpdater.CurrentVersion;
            return BoBoProtocol.WriteJsonAsync(sslStream, hello, ct);
        }

        private static async Task ConnectWithCancelAsync(TcpClient client, string host, int port, CancellationToken ct)
        {
            Task connectTask = client.ConnectAsync(host, port);
            using (ct.Register(delegate { try { client.Close(); } catch { } }))
            {
                await connectTask.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
