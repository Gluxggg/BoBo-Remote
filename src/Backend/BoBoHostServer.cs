using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DualRemote
{
    /// <summary>
    /// BoBo V6 全内置被控端服务（仅随 BoBo远程.exe 进程内运行）：
    /// - 绑定专属端口 49810（TLS 1.2），绝不复用外部 Sunshine/Moonlight/RustDesk；
    /// - 关闭 BoBo远程.exe 立即停服断口，物理杜绝软件未开仍被外部内核连控的漏洞；
    /// - 本机 4 位临时配对码全内存零毫秒生成，台式机/笔记本 100% 即点即出；
    /// - 严格双向鉴权：必须双方均打开 BoBo远程 且完成 BoBo 自身配对授权后方可建流；
    /// - 建流前同步触发防窥回调（&lt;5ms），并原生承载实时切屏、改码率/帧率、被控静音、锁屏、剪贴板与文件接收。
    /// </summary>
    internal sealed class BoBoHostServer : IDisposable
    {
        private readonly BoBoIdentityStore identity;
        private readonly int requestedPort;
        private readonly bool enableUdpDiscovery;
        private readonly object stateGate = new object();
        private readonly object pairGate = new object();

        private TcpListener listener;
        private UdpClient discoveryUdp;
        private UdpClient mediaUdp;
        private int boundUdpPort;
        private CancellationTokenSource cts;
        private bool running;
        private int boundPort;
        private volatile bool allowIncomingControl = true;
        private volatile bool activeStreamRunning;
        private long currentStreamSessionId;
        private string activePeerName = "";
        private int activeMonitorIndex;
        private BoBoLocalPairSession activePairSession;

        private readonly ConcurrentDictionary<string, BoBoDiscoveredPeer> lanPeers =
            new ConcurrentDictionary<string, BoBoDiscoveredPeer>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<uint, HostUdpSession> udpSessions =
            new ConcurrentDictionary<uint, HostUdpSession>();

        private readonly List<ActiveClientConnection> activeConnections = new List<ActiveClientConnection>();

        internal event Action<bool, string, bool> SessionStateChanged;
        internal event Action<bool> PrivacyCommandReceived;
        internal event Action<BoBoAuthorizedPeer> PeerPaired;
        internal event Action<string> ClipboardTextReceived;
        internal event Action<string, string> FileReceived;

        internal BoBoHostServer(BoBoIdentityStore identityStore, int port, bool enableDiscovery)
        {
            identity = identityStore ?? BoBoIdentityStore.Default;
            requestedPort = port;
            enableUdpDiscovery = enableDiscovery;
        }

        internal bool IsRunning { get { return running; } }
        internal int ListeningPort { get { return boundPort; } }
        internal int ListeningUdpPort { get { return boundUdpPort; } }
        internal bool HasActiveRemoteSession { get { return activeStreamRunning; } }
        internal string ActivePeerName { get { return activePeerName; } }

        internal bool AllowIncomingControl
        {
            get { return allowIncomingControl; }
            set
            {
                allowIncomingControl = value;
                if (!value)
                {
                    DisconnectAllActiveStreams("主机已关闭「允许本机被控」");
                }
            }
        }

        internal bool Start(out string error)
        {
            error = null;
            lock (stateGate)
            {
                if (running) return true;
                try
                {
                    identity.EnsureLoaded();
                    cts = new CancellationTokenSource();

                    TcpListener tcp = null;
                    try
                    {
                        tcp = new TcpListener(IPAddress.IPv6Any, requestedPort);
                        tcp.Server.DualMode = true;
                        tcp.Start();
                    }
                    catch
                    {
                        tcp = new TcpListener(IPAddress.Any, requestedPort);
                        tcp.Start();
                    }

                    listener = tcp;
                    boundPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    running = true;

                    StartMediaUdpLocked(cts.Token);
                    Task _accept = Task.Run(delegate { return AcceptLoopAsync(tcp, cts.Token); });

                    if (enableUdpDiscovery)
                    {
                        StartDiscoveryLocked(cts.Token);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = "启动 BoBo V6 内置被控服务失败：" + ex.Message;
                    StopInternalLocked();
                    return false;
                }
            }
        }

        internal void Stop()
        {
            lock (stateGate)
            {
                StopInternalLocked();
            }
        }

        private void StopInternalLocked()
        {
            running = false;
            CancelPairing();

            if (enableUdpDiscovery && discoveryUdp != null)
            {
                try { BroadcastUdpPacket("BOBO6_BYE"); } catch { }
            }

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            if (listener != null)
            {
                try { listener.Stop(); } catch { }
                listener = null;
            }

            if (discoveryUdp != null)
            {
                try { discoveryUdp.Close(); } catch { }
                discoveryUdp = null;
            }

            if (mediaUdp != null)
            {
                try { mediaUdp.Close(); } catch { }
                mediaUdp = null;
                boundUdpPort = 0;
            }
            udpSessions.Clear();

            DisconnectAllActiveStreams("被控端已关闭 BoBo远程");
            try { BoBoCoreInterop.RestoreSuperDisplay(); } catch { }

            if (cts != null)
            {
                try { cts.Dispose(); } catch { }
                cts = null;
            }
        }

        internal void DisconnectAllActiveStreams(string reason)
        {
            List<ActiveClientConnection> copy;
            lock (activeConnections)
            {
                copy = new List<ActiveClientConnection>(activeConnections);
            }
            for (int i = 0; i < copy.Count; i++)
            {
                copy[i].ForceClose(reason);
            }
        }

        internal BoBoLocalPairSession BeginPairing(int lifetimeSeconds)
        {
            identity.EnsureLoaded();
            int ttl = lifetimeSeconds >= 30 && lifetimeSeconds <= 600 ? lifetimeSeconds : 120;
            lock (pairGate)
            {
                DateTime now = DateTime.UtcNow;
                activePairSession = new BoBoLocalPairSession
                {
                    Pin = BoBoIdentityStore.GenerateFourDigitPin(),
                    CreatedUtc = now,
                    ExpiresUtc = now.AddSeconds(ttl)
                };
                return activePairSession;
            }
        }

        internal BoBoLocalPairSession GetActivePairing()
        {
            lock (pairGate)
            {
                if (activePairSession != null && activePairSession.IsExpired)
                {
                    activePairSession = null;
                }
                return activePairSession;
            }
        }

        internal void CancelPairing()
        {
            lock (pairGate)
            {
                activePairSession = null;
            }
        }

        internal List<BoBoDiscoveredPeer> GetDiscoveredLanPeers()
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-18);
            var list = new List<BoBoDiscoveredPeer>();
            foreach (KeyValuePair<string, BoBoDiscoveredPeer> kv in lanPeers)
            {
                if (kv.Value.LastSeenUtc >= cutoff)
                {
                    list.Add(kv.Value);
                }
                else
                {
                    BoBoDiscoveredPeer ignored;
                    lanPeers.TryRemove(kv.Key, out ignored);
                }
            }
            return list;
        }

        internal void SendDiscoveryProbeNow()
        {
            if (!enableUdpDiscovery || !running) return;
            try { BroadcastUdpPacket("BOBO6_PROBE"); } catch { }
        }

        internal string ResolveTargetHost(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string code = BoBoIdentityStore.NormalizeDeviceCode(input);
            if (code.Length == 9)
            {
                // 1. Check live LAN discovery table first
                foreach (BoBoDiscoveredPeer p in GetDiscoveredLanPeers())
                {
                    if (string.Equals(BoBoIdentityStore.NormalizeDeviceCode(p.DeviceCode), code, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(p.Host))
                    {
                        return p.Host;
                    }
                }
                // 2. Check saved authorized peers last known IP
                BoBoAuthorizedPeer saved = identity.FindPeerByCodeOrHost(code);
                if (saved != null && !string.IsNullOrWhiteSpace(saved.LastHost))
                {
                    return saved.LastHost;
                }
            }
            return input.Trim();
        }

        private void StartDiscoveryLocked(CancellationToken ct)
        {
            try
            {
                var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.EnableBroadcast = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, BoBoProtocol.DiscoveryUdpPort));
                discoveryUdp = udp;

                Task _rx = Task.Run(delegate { return UdpReceiveLoopAsync(udp, ct); });
                Task _tx = Task.Run(delegate { return UdpBeaconLoopAsync(ct); });
            }
            catch
            {
                // Non-fatal if another local instance already holds UDP discovery port
            }
        }

        private async Task UdpBeaconLoopAsync(CancellationToken ct)
        {
            try
            {
                BroadcastUdpPacket("BOBO6_PROBE");
                while (!ct.IsCancellationRequested)
                {
                    BroadcastUdpPacket("BOBO6_HELLO");
                    await Task.Delay(3500, ct).ConfigureAwait(false);
                }
            }
            catch { }
        }

        private async Task UdpReceiveLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool backoff = false;
                try
                {
                    UdpReceiveResult res = await udp.ReceiveAsync().ConfigureAwait(false);
                    if (res.Buffer == null || res.Buffer.Length == 0) continue;
                    string text = Encoding.UTF8.GetString(res.Buffer);
                    var ser = new JavaScriptSerializer();
                    var map = ser.DeserializeObject(text) as Dictionary<string, object>;
                    if (map == null) continue;
                    string kind = BoBoProtocol.GetStr(map, "kind", "");
                    string peerNodeId = BoBoProtocol.GetStr(map, "nodeId", "");
                    if (string.IsNullOrEmpty(peerNodeId) || string.Equals(peerNodeId, identity.NodeId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string senderIp = res.RemoteEndPoint.Address.ToString();
                    if (senderIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                        senderIp = senderIp.Substring(7);

                    if (kind == "BOBO6_BYE")
                    {
                        BoBoDiscoveredPeer removed;
                        lanPeers.TryRemove(peerNodeId, out removed);
                        continue;
                    }

                    if (kind == "BOBO6_HELLO" || kind == "BOBO6_PROBE")
                    {
                        lanPeers[peerNodeId] = new BoBoDiscoveredPeer
                        {
                            NodeId = peerNodeId,
                            DeviceCode = BoBoProtocol.GetStr(map, "deviceCode", ""),
                            DeviceName = BoBoProtocol.GetStr(map, "deviceName", senderIp),
                            Host = senderIp,
                            Port = BoBoProtocol.GetInt(map, "port", BoBoProtocol.DefaultTcpPort),
                            Version = BoBoProtocol.GetStr(map, "version", "6.0.0"),
                            Platform = BoBoProtocol.GetStr(map, "platform", BoBoProtocol.PlatformWindows),
                            HostControlMode = BoBoProtocol.GetStr(map, "hostControlMode", BoBoProtocol.ControlModeFull),
                            AllowIncoming = BoBoProtocol.GetBool(map, "allowIncoming", true),
                            LastSeenUtc = DateTime.UtcNow
                        };
                        if (kind == "BOBO6_PROBE")
                        {
                            SendUdpPacketTo("BOBO6_HELLO", new IPEndPoint(res.RemoteEndPoint.Address, BoBoProtocol.DiscoveryUdpPort));
                        }
                    }
                }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    backoff = true;
                }
                if (backoff)
                {
                    try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private void StartMediaUdpLocked(CancellationToken ct)
        {
            boundUdpPort = 0;
            int desiredUdp = requestedPort == 0 ? 0 : BoBoProtocol.DefaultUdpPort;
            UdpClient udp = null;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.DualMode = true;
                BoBoUdpSocketUtil.ConfigureLowLatencySocket(udp.Client, 512 * 1024, 256 * 1024);
                udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, desiredUdp));
            }
            catch
            {
                if (udp != null) { try { udp.Close(); } catch { } udp = null; }
                try
                {
                    udp = new UdpClient(AddressFamily.InterNetwork);
                    BoBoUdpSocketUtil.ConfigureLowLatencySocket(udp.Client, 512 * 1024, 256 * 1024);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, desiredUdp));
                }
                catch
                {
                    if (udp != null) { try { udp.Close(); } catch { } udp = null; }
                    if (desiredUdp != 0)
                    {
                        try
                        {
                            udp = new UdpClient(AddressFamily.InterNetwork);
                            BoBoUdpSocketUtil.ConfigureLowLatencySocket(udp.Client, 512 * 1024, 256 * 1024);
                            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                        }
                        catch
                        {
                            if (udp != null) { try { udp.Close(); } catch { } udp = null; }
                        }
                    }
                }
            }

            if (udp == null) return;
            mediaUdp = udp;
            boundUdpPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            Task _rx = Task.Run(delegate { return MediaUdpReceiveLoopAsync(udp, ct); });
        }

        private async Task MediaUdpReceiveLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && running)
            {
                try
                {
                    UdpReceiveResult res = await udp.ReceiveAsync().ConfigureAwait(false);
                    byte[] buf = res.Buffer;
                    if (buf == null || buf.Length < BoBoUdpCrypto.OverheadBytes) continue;
                    uint sid = BoBoUdpCrypto.ReadUInt32LE(buf, 0);
                    HostUdpSession sess;
                    if (udpSessions.TryGetValue(sid, out sess) && sess != null)
                    {
                        sess.HandleIncomingDatagram(buf, buf.Length, res.RemoteEndPoint);
                    }
                }
                catch
                {
                    if (ct.IsCancellationRequested || !running) break;
                }
            }
        }

        private void BroadcastUdpPacket(string kind)
        {
            List<IPAddress> targets = BoBoProtocol.GetDirectedBroadcastAddresses();
            for (int i = 0; i < targets.Count; i++)
            {
                SendUdpPacketTo(kind, new IPEndPoint(targets[i], BoBoProtocol.DiscoveryUdpPort));
            }
        }

        private void SendUdpPacketTo(string kind, IPEndPoint target)
        {
            UdpClient udp = discoveryUdp;
            if (udp == null) return;
            try
            {
                var map = new Dictionary<string, object>();
                map["kind"] = kind;
                map["protocol"] = BoBoProtocol.ProtocolMagic;
                map["protocolVersion"] = BoBoProtocol.ProtocolVersion;
                map["platform"] = BoBoProtocol.PlatformWindows;
                map["hostControlMode"] = BoBoProtocol.ControlModeFull;
                map["nodeId"] = identity.NodeId;
                map["deviceCode"] = identity.DeviceCode;
                map["deviceName"] = identity.DeviceName;
                map["port"] = boundPort;
                map["version"] = AppUpdater.CurrentVersion;
                map["allowIncoming"] = allowIncomingControl;
                byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(map));
                udp.Send(bytes, bytes.Length, target);
            }
            catch { }
        }

        private static void ConfigureTcpKeepAlive(TcpClient client)
        {
            if (client == null || client.Client == null) return;
            try
            {
                // Windows SIO_KEEPALIVE_VALS: enable=1, idle=5000ms, interval=1000ms
                byte[] ka = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(ka, 0);
                BitConverter.GetBytes(5000u).CopyTo(ka, 4);
                BitConverter.GetBytes(1000u).CopyTo(ka, 8);
                client.Client.IOControl(IOControlCode.KeepAliveValues, ka, null);
            }
            catch { }
        }

        private async Task AcceptLoopAsync(TcpListener tcp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && running)
            {
                TcpClient client = null;
                try
                {
                    client = await tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    // Right-sized TCP buffers (128KB instead of 1MB) prevent multi-second kernel bufferbloat on congested Wi-Fi/campus networks
                    client.SendBufferSize = 131072;
                    client.ReceiveBufferSize = 131072;
                    BoBoUdpSocketUtil.TrySetTcpDscp(client.Client);
                    ConfigureTcpKeepAlive(client);
                    TcpClient accepted = client;
                    Task _h = Task.Run(delegate { return HandleClientAsync(accepted, ct); });
                }
                catch
                {
                    if (client != null) { try { client.Close(); } catch { } }
                    if (ct.IsCancellationRequested || !running) break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcp, CancellationToken serverCt)
        {
            var conn = new ActiveClientConnection(tcp);
            lock (activeConnections) { activeConnections.Add(conn); }

            string remoteIp = "";
            try
            {
                var ep = tcp.Client.RemoteEndPoint as IPEndPoint;
                if (ep != null)
                {
                    remoteIp = ep.Address.ToString();
                    if (remoteIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                        remoteIp = remoteIp.Substring(7);
                }
            }
            catch { }

            BoBoCoreInterop.BoBoHostNativeHandle nativeHost = null;
            bool streamNotifiedStarted = false;
            long myStreamSessionId = 0;
            int lastRecoveryKeyTick = 0;
            string clientNodeId = "";
            string clientDeviceCode = "";
            string clientName = remoteIp;
            string clientThumb = "";
            string sessionNonce = BoBoIdentityStore.GenerateRandomNonceHex(16);
            string activeFileId = "";
            string activeFileName = "";
            string activeFileTmpPath = "";
            string activeFileExpectedSha = "";
            long activeFileTotalBytes = 0;
            FileStream activeFileStream = null;
            int videoQueueDepth = 0;
            int audioQueueDepth = 0;
            int waitingForRecoveryKeyframe = 0;
            HostUdpSession activeUdpSession = null;
            CancellationTokenSource cursorPollCts = null;
            BoBoAdaptiveBitrateController abrController = null;

            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt, conn.Token))
                using (var ssl = new SslStream(tcp.GetStream(), false))
                {
                    CancellationToken ct = linked.Token;
                    BoBoFrame? firstFrame;
                    using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (handshakeCts.Token.Register(delegate { try { tcp.Close(); } catch { } }))
                    {
                        handshakeCts.CancelAfter(8000);
                        await ssl.AuthenticateAsServerAsync(identity.Certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                        conn.AttachSsl(ssl);

                        // 1. Expect HELLO within 8 seconds
                        firstFrame = await BoBoProtocol.ReadOneFrameAsync(ssl, handshakeCts.Token).ConfigureAwait(false);
                    }
                    if (!firstFrame.HasValue || firstFrame.Value.Type != BoBoPacketType.ControlJson) return;
                    var hello = BoBoProtocol.ParseJsonPayload(firstFrame.Value.Payload);
                    if (hello == null || BoBoProtocol.GetStr(hello, "type", "") != "HELLO" ||
                        BoBoProtocol.GetStr(hello, "protocol", "") != BoBoProtocol.ProtocolMagic)
                    {
                        return;
                    }

                    clientNodeId = BoBoProtocol.GetStr(hello, "nodeId", "");
                    clientDeviceCode = BoBoProtocol.GetStr(hello, "deviceCode", "");
                    clientName = BoBoProtocol.GetStr(hello, "deviceName", remoteIp);
                    clientThumb = BoBoProtocol.GetStr(hello, "certThumbprint", "");

                    bool authorized = identity.IsPeerAuthorized(clientNodeId, clientThumb);
                    List<BoBoMonitorDescriptor> monitors = BoBoCoreInterop.EnumerateMonitors();

                    var ack = new Dictionary<string, object>();
                    ack["type"] = "HELLO_ACK";
                    ack["protocol"] = BoBoProtocol.ProtocolMagic;
                    ack["protocolVersion"] = BoBoProtocol.ProtocolVersion;
                    ack["platform"] = BoBoProtocol.PlatformWindows;
                    ack["hostControlMode"] = BoBoProtocol.ControlModeFull;
                    ack["screenOrientation"] = 0;
                    ack["nodeId"] = identity.NodeId;
                    ack["deviceCode"] = identity.DeviceCode;
                    ack["deviceName"] = identity.DeviceName;
                    ack["certThumbprint"] = identity.CertThumbprint;
                    ack["version"] = AppUpdater.CurrentVersion;
                    ack["allowIncoming"] = allowIncomingControl;
                    ack["authorized"] = authorized;
                    ack["nonce"] = sessionNonce;
                    ack["monitors"] = BoBoProtocol.SerializeMonitors(monitors);
                    ack["lanIps"] = BoBoProtocol.GetLocalLanIpv4Addresses().ToArray();
                    ack["udpSupported"] = (boundUdpPort > 0);
                    await conn.SendJsonSafeAsync(ack, ct).ConfigureAwait(false);

                    bool superDisplayActive = false;
                    bool superDisplayNativeMatch = false;
                    int activeFps = 60;
                    int activeBitrateKbps = 14000;

                    // 2. Main command & input loop
                    while (!ct.IsCancellationRequested)
                    {
                        BoBoFrame? frameOpt = await BoBoProtocol.ReadOneFrameAsync(ssl, ct).ConfigureAwait(false);
                        if (!frameOpt.HasValue) break;
                        BoBoFrame frame = frameOpt.Value;

                        if (frame.Type == BoBoPacketType.InputEvent)
                        {
                            if (authorized && nativeHost != null)
                            {
                                BoBoInputPacket inp;
                                if (BoBoProtocol.ParseInputPayload(frame.Payload, out inp))
                                {
                                    BoBoCoreInterop.InjectInput(activeMonitorIndex, inp);
                                }
                            }
                            continue;
                        }

                        if (frame.Type == BoBoPacketType.FileChunk)
                        {
                            if (authorized && activeFileStream != null)
                            {
                                string tid;
                                long offset;
                                byte[] chunk;
                                if (BoBoProtocol.ParseFileChunkPayload(frame.Payload, out tid, out offset, out chunk) &&
                                    string.Equals(tid, activeFileId, StringComparison.Ordinal) &&
                                    offset >= 0 &&
                                    chunk != null && chunk.Length > 0)
                                {
                                    if (activeFileStream.Position != offset) activeFileStream.Seek(offset, SeekOrigin.Begin);
                                    activeFileStream.Write(chunk, 0, chunk.Length);
                                }
                            }
                            continue;
                        }

                        if (frame.Type != BoBoPacketType.ControlJson) continue;
                        var msg = BoBoProtocol.ParseJsonPayload(frame.Payload);
                        if (msg == null) continue;
                        string msgType = BoBoProtocol.GetStr(msg, "type", "");

                        if (msgType == "PING")
                        {
                            // CRITICAL: NEVER await PONG on the read loop! Enqueue to high-priority send queue so
                            // incoming mouse/keyboard InputEvents are NEVER blocked behind video frames!
                            var pong = new Dictionary<string, object>();
                            pong["type"] = "PONG";
                            pong["ts"] = BoBoProtocol.GetLong(msg, "ts", 0);
                            conn.SendJsonHighPriorityFireAndForget(pong);
                            continue;
                        }

                        if (msgType == "PAIR_REQUEST")
                        {
                            var pairResp = new Dictionary<string, object>();
                            pairResp["type"] = "PAIR_RESULT";
                            if (!allowIncomingControl)
                            {
                                pairResp["ok"] = false;
                                pairResp["error"] = "对方当前关闭了「允许本机被控」，请在被控端 BoBo远程 中开启。";
                                await conn.SendJsonSafeAsync(pairResp, ct).ConfigureAwait(false);
                                continue;
                            }

                            string proof = BoBoProtocol.GetStr(msg, "proof", "");
                            bool matched = false;
                            lock (pairGate)
                            {
                                if (activePairSession != null && !activePairSession.IsExpired)
                                {
                                    string expected = BoBoIdentityStore.ComputePinProof(
                                        activePairSession.Pin, sessionNonce, clientThumb, identity.CertThumbprint);
                                    if (BoBoIdentityStore.FixedTimeEqualsHex(proof, expected))
                                    {
                                        matched = true;
                                        activePairSession = null; // One-time PIN consumed immediately
                                    }
                                }
                            }

                            if (!matched)
                            {
                                pairResp["ok"] = false;
                                pairResp["error"] = "4 位配对码不正确或已失效。请确认被控端 BoBo远程 已生成最新配对码。";
                                await conn.SendJsonSafeAsync(pairResp, ct).ConfigureAwait(false);
                                continue;
                            }

                            var newPeer = new BoBoAuthorizedPeer
                            {
                                NodeId = clientNodeId,
                                DeviceCode = clientDeviceCode,
                                DeviceName = clientName,
                                CertThumbprint = clientThumb,
                                LastHost = remoteIp
                            };
                            identity.AuthorizeOrUpdatePeer(newPeer);
                            authorized = true;

                            pairResp["ok"] = true;
                            pairResp["hostName"] = identity.DeviceName;
                            pairResp["hostDeviceCode"] = identity.DeviceCode;
                            await conn.SendJsonSafeAsync(pairResp, ct).ConfigureAwait(false);

                            Action<BoBoAuthorizedPeer> pairedEv = PeerPaired;
                            if (pairedEv != null) { try { pairedEv(newPeer); } catch { } }
                            continue;
                        }

                        if (msgType == "START_STREAM")
                        {
                            if (!allowIncomingControl)
                            {
                                await SendErrorAsync(conn, "HOST_DISABLED", "对方当前已关闭「允许本机被控」。", ct).ConfigureAwait(false);
                                continue;
                            }
                            if (!authorized)
                            {
                                await SendErrorAsync(conn, "PAIR_REQUIRED", "尚未完成 BoBo 双端配对授权，请先输入被控端 4 位临时配对码。", ct).ConfigureAwait(false);
                                continue;
                            }

                            // Update last known host IP for this authorized peer
                            if (!string.IsNullOrEmpty(clientNodeId) && !string.IsNullOrEmpty(clientThumb))
                            {
                                identity.AuthorizeOrUpdatePeer(new BoBoAuthorizedPeer
                                {
                                    NodeId = clientNodeId,
                                    DeviceCode = clientDeviceCode,
                                    DeviceName = clientName,
                                    CertThumbprint = clientThumb,
                                    LastHost = remoteIp
                                });
                            }

                            int reqMon = BoBoProtocol.GetInt(msg, "monitorIndex", 0);
                            int reqW = BoBoProtocol.GetInt(msg, "width", 1920);
                            int reqH = BoBoProtocol.GetInt(msg, "height", 1080);
                            int reqFps = BoBoProtocol.GetInt(msg, "fps", 60);
                            int reqBps = BoBoProtocol.GetInt(msg, "bitrateKbps", 10000);
                            bool muteHost = BoBoProtocol.GetBool(msg, "muteHost", false);
                            bool requestPrivacy = BoBoProtocol.GetBool(msg, "requestPrivacy", false);
                            bool reqSuperDisplay = BoBoProtocol.GetBool(msg, "superDisplay", false);
                            int clientW = BoBoProtocol.GetInt(msg, "clientWidth", 0);
                            int clientH = BoBoProtocol.GetInt(msg, "clientHeight", 0);
                            bool reqUdp = BoBoProtocol.GetBool(msg, "udpTransport", false);
                            bool clientNativeCursor = BoBoProtocol.GetBool(msg, "nativeLocalCursor", false);
                            bool clientFreeMouse = BoBoProtocol.GetBool(msg, "freeMouse", true);
                            bool reqAutoBitrate = BoBoProtocol.GetBool(msg, "autoBitrate", true);
                            double initRttMs = 0.0;
                            object initRttObj;
                            if (msg.TryGetValue("initialRttMs", out initRttObj) && initRttObj != null)
                            {
                                try { initRttMs = Convert.ToDouble(initRttObj, CultureInfo.InvariantCulture); } catch { }
                            }

                            long profilePixelBudget = Math.Max(1920L * 1200L, ((long)reqW * (long)reqH * 115L) / 100L);
                            activeFps = reqFps;
                            activeBitrateKbps = reqBps;
                            superDisplayActive = reqSuperDisplay;
                            superDisplayNativeMatch = false;
                            if (reqSuperDisplay && clientW >= 640 && clientH >= 480)
                            {
                                int actualW, actualH;
                                if (BoBoCoreInterop.ApplySuperDisplay(reqMon, clientW, clientH, out actualW, out actualH) &&
                                    actualW >= 640 && actualH >= 480)
                                {
                                    superDisplayNativeMatch = true;
                                    int capW = Math.Min(actualW, 3840);
                                    int capH = Math.Min(actualH, 2160);
                                    // Preserve exact laptop aspect ratio (zero black bars!) while capping encode resolution
                                    // to the chosen profile's pixel budget (so 1080p never silently explodes to 2880x1800 over Wi-Fi/campus WAN!)
                                    long actualPixels = (long)capW * (long)capH;
                                    if (actualPixels > profilePixelBudget)
                                    {
                                        double scale = Math.Sqrt((double)profilePixelBudget / (double)actualPixels);
                                        capW = Math.Max(640, ((int)Math.Round(capW * scale)) & ~1);
                                        capH = Math.Max(480, ((int)Math.Round(capH * scale)) & ~1);
                                    }
                                    reqW = capW;
                                    reqH = capH;
                                }
                            }

                            // CRITICAL: notify privacy curtain synchronously BEFORE creating capture engine (<5ms)!
                            myStreamSessionId = Interlocked.Increment(ref currentStreamSessionId);
                            activeStreamRunning = true;
                            activePeerName = clientName;
                            activeMonitorIndex = reqMon;
                            streamNotifiedStarted = true;
                            Action<bool, string, bool> sessEv = SessionStateChanged;
                            if (sessEv != null) { try { sessEv(true, clientName, requestPrivacy); } catch { } }

                            if (cursorPollCts != null) { try { cursorPollCts.Cancel(); cursorPollCts.Dispose(); } catch { } cursorPollCts = null; }
                            if (activeUdpSession != null)
                            {
                                HostUdpSession oldU;
                                udpSessions.TryRemove(activeUdpSession.SessionId, out oldU);
                                activeUdpSession.Dispose();
                                activeUdpSession = null;
                            }
                            if (nativeHost != null)
                            {
                                nativeHost.Dispose();
                                nativeHost = null;
                            }

                            abrController = new BoBoAdaptiveBitrateController(reqAutoBitrate, reqBps, reqFps, initRttMs);
                            int startBps = abrController.CurrentBitrateKbps;
                            int startFps = abrController.CurrentFps;
                            bool hostCompositeCursor = !clientNativeCursor || !clientFreeMouse;
                            Interlocked.Exchange(ref videoQueueDepth, 0);
                            Interlocked.Exchange(ref audioQueueDepth, 0);
                            Interlocked.Exchange(ref waitingForRecoveryKeyframe, 0);

                            // Set up optional encrypted UDP+FEC fast-path if both host and client support it
                            uint udpSid = 0;
                            byte[] udpKeyBytes = null;
                            if (reqUdp && mediaUdp != null && boundUdpPort > 0)
                            {
                                udpSid = BoBoUdpCrypto.GenerateRandomSessionId();
                                udpKeyBytes = BoBoUdpCrypto.GenerateRandomKey32();
                                var uSess = new HostUdpSession(
                                    udpSid,
                                    udpKeyBytes,
                                    mediaUdp,
                                    delegate(BoBoInputPacket inp)
                                    {
                                        BoBoCoreInterop.InjectInput(activeMonitorIndex, inp);
                                    },
                                    delegate
                                    {
                                        BoBoCoreInterop.BoBoHostNativeHandle h = nativeHost;
                                        if (h != null) { try { h.RequestKeyframe(); } catch { } }
                                    },
                                    delegate(double rttMs, double lossPct)
                                    {
                                        BoBoAdaptiveBitrateController abr = abrController;
                                        BoBoCoreInterop.BoBoHostNativeHandle h = nativeHost;
                                        if (abr == null || h == null) return;
                                        int nextBps, nextFps;
                                        if (abr.OnTelemetry(rttMs, lossPct, out nextBps, out nextFps))
                                        {
                                            h.SetBitrate(nextBps);
                                            h.SetFps(nextFps);
                                            var note = new Dictionary<string, object>();
                                            note["type"] = "BITRATE_UPDATED";
                                            note["bitrateKbps"] = nextBps;
                                            note["fps"] = nextFps;
                                            conn.SendJsonHighPriorityFireAndForget(note);
                                        }
                                    });
                                activeUdpSession = uSess;
                                udpSessions[udpSid] = uSess;
                            }

                            string hostErr;
                            nativeHost = BoBoCoreInterop.CreateHostSession(
                                reqMon, reqW, reqH, startFps, startBps, muteHost,
                                delegate(byte[] h264, bool isKey, int w, int h, uint tsMs)
                                {
                                    if (ct.IsCancellationRequested) return;

                                    // Fast-path: encrypted UDP + adaptive XOR FEC (zero TCP Head-of-Line blocking!)
                                    HostUdpSession uSess = activeUdpSession;
                                    if (uSess != null && uSess.IsActivated)
                                    {
                                        uSess.SendVideoFrame(h264, isKey, w, h, tsMs);
                                        return;
                                    }

                                    // Optimized TCP+TLS fallback: never let stale P-frames pile up or cause IDR storms!
                                    int depth = Interlocked.Increment(ref videoQueueDepth);
                                    if (isKey)
                                    {
                                        if (depth > 4)
                                        {
                                            Interlocked.Decrement(ref videoQueueDepth);
                                            Interlocked.Exchange(ref waitingForRecoveryKeyframe, 1);
                                            NotifyAbrBackpressure(abrController, nativeHost, conn);
                                            return;
                                        }
                                        Interlocked.Exchange(ref waitingForRecoveryKeyframe, 0);
                                    }
                                    else
                                    {
                                        if (Volatile.Read(ref waitingForRecoveryKeyframe) != 0)
                                        {
                                            Interlocked.Decrement(ref videoQueueDepth);
                                            if (depth <= 2)
                                            {
                                                TryRequestDebouncedRecoveryKeyframe(ref lastRecoveryKeyTick, nativeHost, 1500);
                                            }
                                            return;
                                        }
                                        if (depth > 2)
                                        {
                                            Interlocked.Decrement(ref videoQueueDepth);
                                            Interlocked.Exchange(ref waitingForRecoveryKeyframe, 1);
                                            NotifyAbrBackpressure(abrController, nativeHost, conn);
                                            return;
                                        }
                                    }

                                    byte[] payload = BoBoProtocol.BuildVideoPayload(w, h, isKey, h264);
                                    conn.SendFrameFireAndForget(BoBoPacketType.VideoH264, tsMs, payload, false, delegate
                                    {
                                        int rem = Interlocked.Decrement(ref videoQueueDepth);
                                        if (rem <= 1 && Volatile.Read(ref waitingForRecoveryKeyframe) != 0)
                                        {
                                            TryRequestDebouncedRecoveryKeyframe(ref lastRecoveryKeyTick, nativeHost, 1500);
                                        }
                                    });
                                },
                                delegate(byte[] adpcm, int sr, int ch, int samples)
                                {
                                    if (ct.IsCancellationRequested) return;
                                    HostUdpSession uSess = activeUdpSession;
                                    if (uSess != null && uSess.IsActivated)
                                    {
                                        uSess.SendAudioFrame(sr, ch, samples, adpcm);
                                        return;
                                    }
                                    // Bound TCP audio queue to <= 4 packets (40ms) so audio never clogs video/control
                                    int aDepth = Interlocked.Increment(ref audioQueueDepth);
                                    if (aDepth > 4)
                                    {
                                        Interlocked.Decrement(ref audioQueueDepth);
                                        return;
                                    }
                                    byte[] payload = BoBoProtocol.BuildAudioPayload(sr, ch, samples, adpcm);
                                    conn.SendFrameFireAndForget(BoBoPacketType.AudioAdpcm, 0, payload, false, delegate
                                    {
                                        Interlocked.Decrement(ref audioQueueDepth);
                                    });
                                },
                                out hostErr);

                            if (nativeHost == null)
                            {
                                if (activeUdpSession != null)
                                {
                                    HostUdpSession rmU;
                                    udpSessions.TryRemove(activeUdpSession.SessionId, out rmU);
                                    activeUdpSession.Dispose();
                                    activeUdpSession = null;
                                }
                                await SendErrorAsync(conn, "ENGINE_FAILED", hostErr ?? "主机采编引擎启动失败。", ct).ConfigureAwait(false);
                                continue;
                            }

                            nativeHost.SetCompositeCursor(hostCompositeCursor);

                            // Lightweight cursor shape sync task (35ms poll, sends tiny 5B packet ONLY when shape changes)
                            if (clientNativeCursor)
                            {
                                cursorPollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                CancellationToken cpCt = cursorPollCts.Token;
                                Task _cp = Task.Run(async delegate
                                {
                                    int lastShape = -1;
                                    uint seq = 0;
                                    while (!cpCt.IsCancellationRequested)
                                    {
                                        try
                                        {
                                            int shape = BoBoCoreInterop.GetHostCursorShape(activeMonitorIndex);
                                            if (shape != lastShape)
                                            {
                                                lastShape = shape;
                                                HostUdpSession uSess = activeUdpSession;
                                                if (uSess != null && uSess.IsActivated)
                                                {
                                                    uSess.SendCursorShape(shape);
                                                }
                                                else
                                                {
                                                    byte[] cpPayload = new byte[] { (byte)(shape & 0xFF) };
                                                    conn.SendFrameFireAndForget(BoBoPacketType.CursorShape, ++seq, cpPayload, true, null);
                                                }
                                            }
                                            await Task.Delay(35, cpCt).ConfigureAwait(false);
                                        }
                                        catch { break; }
                                    }
                                });
                            }

                            List<BoBoMonitorDescriptor> curMons = BoBoCoreInterop.EnumerateMonitors();
                            var started = new Dictionary<string, object>();
                            started["type"] = "STREAM_STARTED";
                            started["width"] = reqW;
                            started["height"] = reqH;
                            started["fps"] = startFps;
                            started["bitrateKbps"] = startBps;
                            started["monitorIndex"] = reqMon;
                            started["superDisplayActive"] = superDisplayActive;
                            started["superDisplayNativeMatch"] = superDisplayNativeMatch;
                            started["platform"] = BoBoProtocol.PlatformWindows;
                            started["hostControlMode"] = BoBoProtocol.ControlModeFull;
                            started["hostCursorComposited"] = hostCompositeCursor;
                            started["monitors"] = BoBoProtocol.SerializeMonitors(curMons);
                            if (activeUdpSession != null && udpKeyBytes != null)
                            {
                                started["udpPort"] = boundUdpPort;
                                started["udpSessionId"] = (long)udpSid;
                                started["udpKey"] = Convert.ToBase64String(udpKeyBytes);
                            }
                            await conn.SendJsonSafeAsync(started, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (!authorized)
                        {
                            await SendErrorAsync(conn, "PAIR_REQUIRED", "未授权会话，拒绝执行控制命令。", ct).ConfigureAwait(false);
                            continue;
                        }

                        if (msgType == "RECONFIGURE" && nativeHost != null)
                        {
                            int reqMon = BoBoProtocol.GetInt(msg, "monitorIndex", activeMonitorIndex);
                            int reqW = BoBoProtocol.GetInt(msg, "width", 1920);
                            int reqH = BoBoProtocol.GetInt(msg, "height", 1080);
                            int reqFps = BoBoProtocol.GetInt(msg, "fps", 60);
                            int reqBps = BoBoProtocol.GetInt(msg, "bitrateKbps", 10000);
                            bool reqAuto = BoBoProtocol.GetBool(msg, "autoBitrate", abrController != null ? abrController.AutoEnabled : true);
                            activeMonitorIndex = reqMon;
                            activeFps = reqFps;
                            activeBitrateKbps = reqBps;
                            if (abrController != null) abrController.UpdateTarget(reqAuto, reqBps, reqFps);
                            nativeHost.Reconfigure(reqMon, reqW, reqH, reqFps, reqBps);
                            var ackCfg = new Dictionary<string, object>();
                            ackCfg["type"] = "RECONFIGURED";
                            ackCfg["monitorIndex"] = reqMon;
                            ackCfg["width"] = reqW;
                            ackCfg["height"] = reqH;
                            ackCfg["fps"] = reqFps;
                            ackCfg["bitrateKbps"] = reqBps;
                            conn.SendJsonHighPriorityFireAndForget(ackCfg);
                            continue;
                        }

                        if (msgType == "SET_CURSOR_MODE" && nativeHost != null)
                        {
                            bool composite = BoBoProtocol.GetBool(msg, "composite", true);
                            nativeHost.SetCompositeCursor(composite);
                            var ackCm = new Dictionary<string, object>();
                            ackCm["type"] = "CURSOR_MODE_STATE";
                            ackCm["hostCursorComposited"] = composite;
                            conn.SendJsonHighPriorityFireAndForget(ackCm);
                            continue;
                        }

                        if (msgType == "TELEMETRY" && nativeHost != null)
                        {
                            double rttMs = 0.0;
                            double lossPct = 0.0;
                            object rv;
                            if (msg.TryGetValue("rttMs", out rv) && rv != null)
                                try { rttMs = Convert.ToDouble(rv, CultureInfo.InvariantCulture); } catch { }
                            object lv;
                            if (msg.TryGetValue("lossPct", out lv) && lv != null)
                                try { lossPct = Convert.ToDouble(lv, CultureInfo.InvariantCulture); } catch { }

                            if (abrController != null)
                            {
                                int nextBps, nextFps;
                                if (abrController.OnTelemetry(rttMs, lossPct, out nextBps, out nextFps))
                                {
                                    nativeHost.SetBitrate(nextBps);
                                    nativeHost.SetFps(nextFps);
                                    var note = new Dictionary<string, object>();
                                    note["type"] = "BITRATE_UPDATED";
                                    note["bitrateKbps"] = nextBps;
                                    note["fps"] = nextFps;
                                    conn.SendJsonHighPriorityFireAndForget(note);
                                }
                            }
                            continue;
                        }

                        if (msgType == "TRANSPORT_FALLBACK")
                        {
                            if (activeUdpSession != null) activeUdpSession.Deactivate();
                            if (nativeHost != null) nativeHost.RequestKeyframe();
                            continue;
                        }

                        if (msgType == "SET_SUPER_DISPLAY")
                        {
                            bool enable = BoBoProtocol.GetBool(msg, "enable", false);
                            int clientW = BoBoProtocol.GetInt(msg, "clientWidth", 0);
                            int clientH = BoBoProtocol.GetInt(msg, "clientHeight", 0);
                            int outW = 0;
                            int outH = 0;

                            if (enable && clientW >= 640 && clientH >= 480)
                            {
                                int actualW, actualH;
                                bool matched = BoBoCoreInterop.ApplySuperDisplay(activeMonitorIndex, clientW, clientH, out actualW, out actualH);
                                superDisplayActive = true;
                                superDisplayNativeMatch = matched;
                                outW = actualW;
                                outH = actualH;
                                if (matched && actualW >= 640 && actualH >= 480 && nativeHost != null)
                                {
                                    nativeHost.Reconfigure(activeMonitorIndex, Math.Min(actualW, 3840), Math.Min(actualH, 2160), activeFps, activeBitrateKbps);
                                }
                            }
                            else
                            {
                                BoBoCoreInterop.RestoreSuperDisplay();
                                superDisplayActive = false;
                                superDisplayNativeMatch = false;
                                List<BoBoMonitorDescriptor> monsNow = BoBoCoreInterop.EnumerateMonitors();
                                if (monsNow.Count > 0)
                                {
                                    int idx = Math.Max(0, Math.Min(activeMonitorIndex, monsNow.Count - 1));
                                    outW = monsNow[idx].Width;
                                    outH = monsNow[idx].Height;
                                    if (nativeHost != null && outW >= 640 && outH >= 480)
                                    {
                                        nativeHost.Reconfigure(activeMonitorIndex, Math.Min(outW, 3840), Math.Min(outH, 2160), activeFps, activeBitrateKbps);
                                    }
                                }
                            }

                            var ackSd = new Dictionary<string, object>();
                            ackSd["type"] = "SUPER_DISPLAY_STATE";
                            ackSd["active"] = superDisplayActive;
                            ackSd["nativeMatch"] = superDisplayNativeMatch;
                            ackSd["width"] = outW;
                            ackSd["height"] = outH;
                            await conn.SendJsonSafeAsync(ackSd, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (msgType == "REQUEST_KEYFRAME" && nativeHost != null)
                        {
                            nativeHost.RequestKeyframe();
                            continue;
                        }

                        if (msgType == "SET_MUTE_HOST" && nativeHost != null)
                        {
                            nativeHost.SetMuteHost(BoBoProtocol.GetBool(msg, "mute", false));
                            continue;
                        }

                        if (msgType == "SET_PRIVACY")
                        {
                            bool enable = BoBoProtocol.GetBool(msg, "enable", false);
                            Action<bool> privEv = PrivacyCommandReceived;
                            if (privEv != null) { try { privEv(enable); } catch { } }
                            var ackP = new Dictionary<string, object>();
                            ackP["type"] = "PRIVACY_STATE";
                            ackP["active"] = enable;
                            await conn.SendJsonSafeAsync(ackP, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (msgType == "LOCK_WORKSTATION")
                        {
                            WindowsSession.TryLockWorkstation();
                            continue;
                        }

                        if (msgType == "CLIPBOARD_TEXT")
                        {
                            string text = BoBoProtocol.GetStr(msg, "text", "");
                            bool typeText = BoBoProtocol.GetBool(msg, "injectUnicode", true);
                            if (!string.IsNullOrEmpty(text))
                            {
                                Action<string> cbEv = ClipboardTextReceived;
                                if (cbEv != null) { try { cbEv(text); } catch { } }
                                if (typeText) BoBoCoreInterop.InjectUnicodeText(text);
                            }
                            continue;
                        }

                        if (msgType == "FILE_OFFER")
                        {
                            if (activeFileStream != null) { try { activeFileStream.Dispose(); } catch { } activeFileStream = null; }
                            activeFileId = BoBoProtocol.GetStr(msg, "transferId", "");
                            string rawName = Path.GetFileName(BoBoProtocol.GetStr(msg, "fileName", "received.bin"));
                            if (string.IsNullOrWhiteSpace(rawName)) rawName = "received.bin";
                            activeFileName = rawName;
                            activeFileTotalBytes = BoBoProtocol.GetLong(msg, "totalBytes", 0);
                            activeFileExpectedSha = BoBoProtocol.GetStr(msg, "sha256", "");

                            string recvDir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                "Downloads", "BoBoRemote");
                            Directory.CreateDirectory(recvDir);
                            activeFileTmpPath = Path.Combine(recvDir, ".bobo-" + Guid.NewGuid().ToString("N") + ".part");
                            activeFileStream = new FileStream(activeFileTmpPath, FileMode.Create, FileAccess.Write, FileShare.None);

                            var accept = new Dictionary<string, object>();
                            accept["type"] = "FILE_ACCEPT";
                            accept["transferId"] = activeFileId;
                            await conn.SendJsonSafeAsync(accept, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (msgType == "FILE_DONE")
                        {
                            string tid = BoBoProtocol.GetStr(msg, "transferId", "");
                            var fAck = new Dictionary<string, object>();
                            fAck["type"] = "FILE_ACK";
                            fAck["transferId"] = tid;
                            if (activeFileStream != null && string.Equals(tid, activeFileId, StringComparison.Ordinal))
                            {
                                activeFileStream.Flush();
                                activeFileStream.Dispose();
                                activeFileStream = null;

                                string actualSha = ComputeFileSha256Hex(activeFileTmpPath);
                                bool hashOk = string.IsNullOrEmpty(activeFileExpectedSha) ||
                                              string.Equals(actualSha, activeFileExpectedSha, StringComparison.OrdinalIgnoreCase);
                                if (hashOk)
                                {
                                    string recvDir = Path.GetDirectoryName(activeFileTmpPath);
                                    string finalPath = AllocateUniqueFilePath(recvDir, activeFileName);
                                    File.Move(activeFileTmpPath, finalPath);
                                    activeFileTmpPath = "";
                                    fAck["ok"] = true;
                                    fAck["savedPath"] = finalPath;
                                    fAck["sha256"] = actualSha;
                                    Action<string, string> fEv = FileReceived;
                                    if (fEv != null) { try { fEv(clientName, finalPath); } catch { } }
                                }
                                else
                                {
                                    try { File.Delete(activeFileTmpPath); } catch { }
                                    activeFileTmpPath = "";
                                    fAck["ok"] = false;
                                    fAck["error"] = "文件 SHA-256 校验不一致，已丢弃损坏文件。";
                                }
                            }
                            else
                            {
                                fAck["ok"] = false;
                                fAck["error"] = "未找到对应的文件接收上下文。";
                            }
                            await conn.SendJsonSafeAsync(fAck, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (msgType == "DISCONNECT")
                        {
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Normal disconnect / TLS shutdown
            }
            finally
            {
                if (cursorPollCts != null) { try { cursorPollCts.Cancel(); cursorPollCts.Dispose(); } catch { } }
                if (activeUdpSession != null)
                {
                    HostUdpSession rmU;
                    udpSessions.TryRemove(activeUdpSession.SessionId, out rmU);
                    activeUdpSession.Dispose();
                }
                if (activeFileStream != null) { try { activeFileStream.Dispose(); } catch { } }
                if (!string.IsNullOrEmpty(activeFileTmpPath) && File.Exists(activeFileTmpPath))
                {
                    try { File.Delete(activeFileTmpPath); } catch { }
                }
                if (nativeHost != null)
                {
                    nativeHost.Dispose();
                }
                if (streamNotifiedStarted)
                {
                    // 断开瞬间强制释放所有远端按下的修饰键与鼠标键，防止主机卡键/卡鼠标
                    try
                    {
                        BoBoCoreInterop.InjectInput(activeMonitorIndex, new BoBoInputPacket { Kind = 6, Flags = 0, A = 0, B = 0 });
                    }
                    catch { }

                    // 仅当前断开的会话仍是最新活跃流时，才还原超级屏并通知关闭防窥，防止旧连接清理误伤新连接
                    if (myStreamSessionId == Interlocked.Read(ref currentStreamSessionId))
                    {
                        try { BoBoCoreInterop.RestoreSuperDisplay(); } catch { }
                        activeStreamRunning = false;
                        activePeerName = "";
                        Action<bool, string, bool> sessEv = SessionStateChanged;
                        if (sessEv != null) { try { sessEv(false, clientName, false); } catch { } }
                    }
                }
                lock (activeConnections) { activeConnections.Remove(conn); }
                conn.Dispose();
            }
        }

        private static Task SendErrorAsync(ActiveClientConnection conn, string code, string message, CancellationToken ct)
        {
            var err = new Dictionary<string, object>();
            err["type"] = "ERROR";
            err["code"] = code;
            err["message"] = message;
            return conn.SendJsonSafeAsync(err, ct);
        }

        private static string ComputeFileSha256Hex(string path)
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

        private static string AllocateUniqueFilePath(string dir, string fileName)
        {
            string target = Path.Combine(dir, fileName);
            if (!File.Exists(target)) return target;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int n = 1; n < 1000; n++)
            {
                string candidate = Path.Combine(dir, stem + " (" + n + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, stem + "_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ext);
        }

        private static void TryRequestDebouncedRecoveryKeyframe(ref int lastTick, BoBoCoreInterop.BoBoHostNativeHandle host, int cooldownMs)
        {
            if (host == null) return;
            int nowTick = Environment.TickCount;
            int prevTick = Volatile.Read(ref lastTick);
            if (unchecked(nowTick - prevTick) >= cooldownMs &&
                Interlocked.CompareExchange(ref lastTick, nowTick, prevTick) == prevTick)
            {
                try { host.RequestKeyframe(); } catch { }
            }
        }

        private static void NotifyAbrBackpressure(
            BoBoAdaptiveBitrateController abr,
            BoBoCoreInterop.BoBoHostNativeHandle host,
            ActiveClientConnection conn)
        {
            if (abr == null || host == null) return;
            int nextBps, nextFps;
            if (abr.OnCongestionBackpressure(out nextBps, out nextFps))
            {
                host.SetBitrate(nextBps);
                host.SetFps(nextFps);
                var note = new Dictionary<string, object>();
                note["type"] = "BITRATE_UPDATED";
                note["bitrateKbps"] = nextBps;
                note["fps"] = nextFps;
                conn.SendJsonHighPriorityFireAndForget(note);
            }
        }

        private sealed class HostUdpSession : IDisposable
        {
            private readonly BoBoUdpCrypto crypto;
            private readonly UdpClient udp;
            private readonly BoBoUdpInputCodec inputCodec = new BoBoUdpInputCodec();
            private readonly Action<BoBoInputPacket> onInput;
            private readonly Action onRequestKeyframe;
            private readonly Action<double, double> onTelemetry;
            private readonly object sendGate = new object();

            private IPEndPoint remoteEp;
            private volatile bool activated;
            private volatile bool disposed;
            private int nextFrameId;
            private int nextCursorSeq;
            private long lastPliTicks;
            private double recentLossPct;

            internal HostUdpSession(
                uint sessionId,
                byte[] key32,
                UdpClient sharedUdp,
                Action<BoBoInputPacket> inputHandler,
                Action keyframeHandler,
                Action<double, double> telemetryHandler)
            {
                crypto = new BoBoUdpCrypto(sessionId, key32);
                udp = sharedUdp;
                onInput = inputHandler;
                onRequestKeyframe = keyframeHandler;
                onTelemetry = telemetryHandler;
            }

            internal uint SessionId { get { return crypto.SessionId; } }
            internal bool IsActivated { get { return activated && !disposed && remoteEp != null; } }

            internal void Deactivate()
            {
                activated = false;
            }

            internal void HandleIncomingDatagram(byte[] buf, int length, IPEndPoint sender)
            {
                if (disposed || sender == null) return;
                BoBoUdpPacketType ptype;
                uint seq;
                byte[] plain;
                if (!crypto.TryOpen(buf, length, out ptype, out seq, out plain)) return;

                remoteEp = sender;
                if (ptype == BoBoUdpPacketType.Hello)
                {
                    bool first = !activated;
                    activated = true;
                    SendSealed(BoBoUdpPacketType.HelloAck, plain, 0, plain.Length, sender);
                    if (first && onRequestKeyframe != null)
                    {
                        try { onRequestKeyframe(); } catch { }
                    }
                    return;
                }

                if (ptype == BoBoUdpPacketType.Input)
                {
                    List<BoBoInputPacket> packets = inputCodec.ParseAndDeduplicate(plain);
                    if (onInput != null)
                    {
                        for (int i = 0; i < packets.Count; i++)
                        {
                            try { onInput(packets[i]); } catch { }
                        }
                    }
                    return;
                }

                if (ptype == BoBoUdpPacketType.Ping)
                {
                    SendSealed(BoBoUdpPacketType.Pong, plain, 0, plain.Length, sender);
                    return;
                }

                if (ptype == BoBoUdpPacketType.Feedback && plain != null && plain.Length >= 11)
                {
                    double lossPct = BoBoUdpCrypto.ReadUInt16LE(plain, 0) / 100.0;
                    double rttMs = BoBoUdpCrypto.ReadUInt16LE(plain, 2) / 10.0;
                    byte flags = plain[10];
                    recentLossPct = lossPct;
                    if (onTelemetry != null)
                    {
                        try { onTelemetry(rttMs, lossPct); } catch { }
                    }
                    if ((flags & 1) != 0 && onRequestKeyframe != null)
                    {
                        long now = Stopwatch.GetTimestamp();
                        long prev = Interlocked.Read(ref lastPliTicks);
                        double elapsedMs = prev == 0 ? 1000.0 : ((now - prev) * 1000.0 / Stopwatch.Frequency);
                        if (elapsedMs >= 400.0 && Interlocked.CompareExchange(ref lastPliTicks, now, prev) == prev)
                        {
                            try { onRequestKeyframe(); } catch { }
                        }
                    }
                }
            }

            internal void SendVideoFrame(byte[] h264, bool isKey, int w, int h, uint tsMs)
            {
                IPEndPoint ep = remoteEp;
                if (!activated || disposed || ep == null || h264 == null || h264.Length == 0) return;

                uint fid = unchecked((uint)Interlocked.Increment(ref nextFrameId));
                List<byte[]> shards = BoBoUdpFec.BuildVideoShards(fid, tsMs, w, h, isKey, h264, recentLossPct);
                if (shards.Count == 0) return;

                // Smooth micro-pacing on multi-shard frames prevents Wi-Fi / WireGuard router tail-drop bursts
                bool pace = shards.Count > 6;
                long paceStart = pace ? Stopwatch.GetTimestamp() : 0;
                long maxPaceTicks = (Stopwatch.Frequency * 5L) / 1000L; // Cap total pacing <= 5ms
                long stepTicks = (Stopwatch.Frequency * 250L) / 1000000L; // ~0.25ms per 4-shard micro-burst

                for (int i = 0; i < shards.Count; i++)
                {
                    if (disposed || !activated) break;
                    SendSealed(BoBoUdpPacketType.VideoShard, shards[i], 0, shards[i].Length, ep);
                    if (pace && ((i + 1) & 3) == 0 && (i + 1) < shards.Count)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if ((now - paceStart) < maxPaceTicks)
                        {
                            long target = now + stepTicks;
                            while (Stopwatch.GetTimestamp() < target) Thread.SpinWait(16);
                        }
                    }
                }
            }

            internal void SendAudioFrame(int sr, int ch, int samples, byte[] adpcm)
            {
                IPEndPoint ep = remoteEp;
                if (!activated || disposed || ep == null || adpcm == null || adpcm.Length == 0) return;
                byte[] payload = BoBoProtocol.BuildAudioPayload(sr, ch, samples, adpcm);
                SendSealed(BoBoUdpPacketType.Audio, payload, 0, payload.Length, ep);
            }

            internal void SendCursorShape(int shapeId)
            {
                IPEndPoint ep = remoteEp;
                if (!activated || disposed || ep == null) return;
                byte[] buf = new byte[5];
                uint seq = unchecked((uint)Interlocked.Increment(ref nextCursorSeq));
                BoBoUdpCrypto.WriteUInt32LE(buf, 0, seq);
                buf[4] = (byte)(shapeId & 0xFF);
                SendSealed(BoBoUdpPacketType.CursorShape, buf, 0, 5, ep);
                SendSealed(BoBoUdpPacketType.CursorShape, buf, 0, 5, ep);
            }

            private void SendSealed(BoBoUdpPacketType type, byte[] plain, int off, int len, IPEndPoint ep)
            {
                if (disposed || ep == null) return;
                byte[] sealedPkt = crypto.Seal(type, plain, off, len);
                if (sealedPkt == null) return;
                lock (sendGate)
                {
                    if (disposed) return;
                    try { udp.Send(sealedPkt, sealedPkt.Length, ep); } catch { }
                }
            }

            public void Dispose()
            {
                disposed = true;
                activated = false;
                crypto.Dispose();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private sealed class ActiveClientConnection : IDisposable
        {
            private sealed class PendingWrite
            {
                internal BoBoPacketType Type;
                internal uint FlagsOrTs;
                internal byte[] Payload;
                internal Action OnDone;
                internal TaskCompletionSource<bool> Completion;
            }

            private readonly TcpClient tcp;
            private readonly CancellationTokenSource connCts = new CancellationTokenSource();
            private readonly ConcurrentQueue<PendingWrite> highPriorityQueue = new ConcurrentQueue<PendingWrite>();
            private readonly ConcurrentQueue<PendingWrite> mediaQueue = new ConcurrentQueue<PendingWrite>();
            private readonly object sendLoopGate = new object();
            private bool sendLoopRunning;
            private volatile bool disposed;
            private SslStream ssl;

            internal ActiveClientConnection(TcpClient client)
            {
                tcp = client;
            }

            internal CancellationToken Token { get { return connCts.Token; } }

            internal void AttachSsl(SslStream stream)
            {
                ssl = stream;
            }

            internal Task SendJsonSafeAsync(Dictionary<string, object> obj, CancellationToken ct)
            {
                if (ssl == null || disposed || connCts.IsCancellationRequested)
                    return Task.FromResult(true);

                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = BoBoProtocol.MaxControlPayloadBytes;
                byte[] bytes = Encoding.UTF8.GetBytes(ser.Serialize(obj));
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (ct.CanBeCanceled)
                {
                    ct.Register(delegate { tcs.TrySetCanceled(); });
                }
                EnqueueWrite(new PendingWrite
                {
                    Type = BoBoPacketType.ControlJson,
                    FlagsOrTs = 0,
                    Payload = bytes,
                    Completion = tcs
                }, true);
                return tcs.Task;
            }

            internal void SendJsonHighPriorityFireAndForget(Dictionary<string, object> obj)
            {
                if (ssl == null || disposed || connCts.IsCancellationRequested) return;
                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = BoBoProtocol.MaxControlPayloadBytes;
                byte[] bytes = Encoding.UTF8.GetBytes(ser.Serialize(obj));
                EnqueueWrite(new PendingWrite
                {
                    Type = BoBoPacketType.ControlJson,
                    FlagsOrTs = 0,
                    Payload = bytes
                }, true);
            }

            internal void SendFrameFireAndForget(BoBoPacketType type, uint flagsOrTs, byte[] payload, bool highPriority, Action onDone)
            {
                if (ssl == null || disposed || connCts.IsCancellationRequested)
                {
                    if (onDone != null) { try { onDone(); } catch { } }
                    return;
                }
                EnqueueWrite(new PendingWrite
                {
                    Type = type,
                    FlagsOrTs = flagsOrTs,
                    Payload = payload ?? new byte[0],
                    OnDone = onDone
                }, highPriority);
            }

            private void EnqueueWrite(PendingWrite item, bool highPriority)
            {
                if (highPriority) highPriorityQueue.Enqueue(item);
                else mediaQueue.Enqueue(item);

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
                    PendingWrite item;
                    // Always drain control/PONG/cursor packets BEFORE video/audio frames!
                    if (!highPriorityQueue.TryDequeue(out item) && !mediaQueue.TryDequeue(out item))
                    {
                        lock (sendLoopGate)
                        {
                            if (highPriorityQueue.IsEmpty && mediaQueue.IsEmpty)
                            {
                                sendLoopRunning = false;
                                return;
                            }
                        }
                        continue;
                    }

                    SslStream s = ssl;
                    if (s == null || disposed || connCts.IsCancellationRequested)
                    {
                        CompleteDroppedItem(item);
                        continue;
                    }

                    try
                    {
                        await BoBoProtocol.WriteFrameAsync(s, item.Type, item.FlagsOrTs, item.Payload, 0, item.Payload.Length, connCts.Token).ConfigureAwait(false);
                        if (item.Completion != null) item.Completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        if (item.Completion != null) item.Completion.TrySetException(ex);
                        ForceClose("send failed");
                    }
                    finally
                    {
                        if (item.OnDone != null) { try { item.OnDone(); } catch { } }
                    }
                }
            }

            private static void CompleteDroppedItem(PendingWrite item)
            {
                if (item.Completion != null) item.Completion.TrySetResult(false);
                if (item.OnDone != null) { try { item.OnDone(); } catch { } }
            }

            private void DrainRemainingOnClose()
            {
                PendingWrite dropped;
                while (highPriorityQueue.TryDequeue(out dropped))
                {
                    CompleteDroppedItem(dropped);
                }
                while (mediaQueue.TryDequeue(out dropped))
                {
                    CompleteDroppedItem(dropped);
                }
            }

            internal void ForceClose(string reason)
            {
                try { connCts.Cancel(); } catch { }
                try { tcp.Close(); } catch { }
            }

            public void Dispose()
            {
                disposed = true;
                try { connCts.Cancel(); } catch { }
                try { tcp.Close(); } catch { }
                DrainRemainingOnClose();
                try { connCts.Dispose(); } catch { }
            }
        }
    }
}
