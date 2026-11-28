using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
        internal List<string> LanIps = new List<string>();
        internal List<string> PublicIpv6s = new List<string>();
        internal bool UdpSupported;
        internal double HandshakeRttMs;
        internal IPAddress ConnectedAddress;
        internal IPAddress LocalAddress;
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
        internal int BitrateKbps = 10000;
        internal bool AutoBitrate = true;
        internal bool FreeMouse = true;
        internal bool MuteHostAudio = false;
        internal bool PreferCompressedAudio = true;
        internal bool PreferHevc = true;
        internal bool RequestPrivacy = false;
        internal bool SuperDisplay = true;
        internal int ClientWidth = 0;
        internal int ClientHeight = 0;
        internal bool ClientFullscreen;
        internal bool IsCustomRelay = false;
        internal int RelayUdpPort = 0;
        internal string CustomStunHost = "";
        internal int CustomStunPort = 0;
    }

    internal sealed class BoBoDisplayState
    {
        internal const int Version = 2;
        internal int RequestId;
        internal int MonitorIndex;
        internal int StreamWidth;
        internal int StreamHeight;
        internal int DesktopWidth;
        internal int DesktopHeight;
        internal int ViewportWidth;
        internal int ViewportHeight;
        internal bool Active;
        internal bool NativeMatch;
        internal bool DesktopModeChanged;

        internal static BoBoDisplayState Parse(Dictionary<string, object> message, bool isDisplayReply, bool supportsViewport)
        {
            return new BoBoDisplayState
            {
                RequestId = BoBoProtocol.GetInt(message, "displayRequestId", 0),
                MonitorIndex = BoBoProtocol.GetInt(message, "monitorIndex", -1),
                // Legacy SET_SUPER_DISPLAY replies describe the desktop, so they cannot update video geometry.
                StreamWidth = isDisplayReply && !supportsViewport ? 0 : BoBoProtocol.GetInt(message, "width", 0),
                StreamHeight = isDisplayReply && !supportsViewport ? 0 : BoBoProtocol.GetInt(message, "height", 0),
                DesktopWidth = BoBoProtocol.GetInt(message, "desktopWidth", 0),
                DesktopHeight = BoBoProtocol.GetInt(message, "desktopHeight", 0),
                ViewportWidth = BoBoProtocol.GetInt(message, "clientWidth", 0),
                ViewportHeight = BoBoProtocol.GetInt(message, "clientHeight", 0),
                Active = BoBoProtocol.GetBool(message, isDisplayReply ? "active" : "superDisplayActive", false),
                NativeMatch = supportsViewport && BoBoProtocol.GetBool(message, isDisplayReply ? "nativeMatch" : "superDisplayNativeMatch", false),
                DesktopModeChanged = supportsViewport && BoBoProtocol.GetBool(message, "desktopModeChanged", false)
            };
        }
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
        private int pendingMoveKind; // 0=none, 1=MouseAbs, 2=MouseRel
        private BoBoInputPacket pendingMoveAbs;
        private int pendingMoveDx;
        private int pendingMoveDy;

        private sealed class PendingSend
        {
            internal BoBoPacketType Type;
            internal uint Flags;
            internal byte[] Payload;
            internal TaskCompletionSource<bool> Completion;
            internal CancellationTokenRegistration CancelReg;
        }

        private TcpClient tcp;
        private SslStream ssl;
        private CancellationTokenSource cts;
        private BoBoCoreInterop.BoBoClientNativeHandle nativeClient;
        private volatile bool connected;
        private volatile string forcedDisconnectReason;
        private long lastRxTicks;
        private double latestRttMs;
        private double latestPacketLossPct;
        private int activeBitrateKbps;
        private long videoFramesReceived;
        private List<BoBoMonitorDescriptor> remoteMonitors = new List<BoBoMonitorDescriptor>();

        private sealed class UdpProbeCandidate
        {
            internal uint ProbeId;
            internal IPEndPoint EndPoint;
            internal string Kind;
            internal int Rank;
            internal byte[] HelloPayload;
            internal byte[] ForceHelloPayload;
        }

        // Optional V6.3 Encrypted UDP+FEC low-latency transport channel (auto-upgrades from TCP+TLS when reachable)
        private readonly object udpGate = new object();
        private UdpClient udpClient;
        private IPEndPoint udpRemoteEp;
        private BoBoUdpCrypto udpCrypto;
        private BoBoUdpVideoAssembler udpAssembler;
        private BoBoUdpInputCodec udpInputCodec;
        private volatile bool udpActive;
        private volatile bool udpNackNegotiated;
        private volatile bool routedViaEmbeddedTailnet;
        private BoBoStreamStartOptions lastStartOptions;
        private volatile int activeVideoCodecId = BoBoCoreInterop.VideoCodecH264;
        private volatile string activeUdpPathKind = "";
        private volatile int activeUdpPathRank;
        private int embeddedUdpBridgePort;
        private UdpClient preBoundUdp;
        private readonly BoBoNatTraversal.StunProbeSession clientStunSession = new BoBoNatTraversal.StunProbeSession();
        private readonly ConcurrentDictionary<uint, UdpProbeCandidate> udpProbeTable =
            new ConcurrentDictionary<uint, UdpProbeCandidate>();
        private int nextUdpProbeId;
        private volatile bool udpCandidatesUpdated;
        private long lastUdpRxTicks;
        private long udpFallbackSuppressUntilTicks;
        private long lastUdpMoveSentTicks;
        private bool hasDeferredUdpAbsMove;
        private BoBoInputPacket deferredUdpAbsMove;
        private int deferredUdpRelDx;
        private int deferredUdpRelDy;
        private int udpMoveFlushScheduled;
        private bool hasRedundantAbsMove;
        private BoBoInputPacket redundantAbsMove;
        private int udpDiscreteRedundantScheduled;
        private long udpDiscreteRedundantDueTicks;
        private double latestJitterMs;
        private double prevUdpRttMs;
        private double latestTlsRttMs;
        private BoBoNatTraversal.RouterPortMappingLease activeRouterLease;
        private int lastCursorSeq;
        private long lastDecodeQueueIdrReqTicks;
        private long decodeQueueIdrRequests;

        internal event Action<List<BoBoMonitorDescriptor>, int, int, int> StreamStarted;
        internal event Action<bool, bool, int, int> SuperDisplayStateChanged;
        internal event Action<BoBoDisplayState> DisplayStateChanged;
        internal event Action<string, string> StreamError;
        internal event Action<string, string> HostNoticeReceived;
        internal event Action<string> Disconnected;
        internal event Action<bool> RemotePrivacyStateChanged;
        internal event Action<double> RttUpdated;
        internal event Action<int, int> LiveBitrateUpdated;
        internal event Action<byte[], bool, int, int> RawVideoFrameReceived;

        internal bool SuperDisplayActive { get; private set; }
        internal bool SuperDisplayNativeMatch { get; private set; }
        internal bool SupportsSuperDisplayViewport { get; private set; }
        internal bool HostCursorComposited { get; private set; }
        internal string RemotePlatform { get; private set; }
        internal string RemoteHostControlMode { get; private set; }

        internal BoBoStreamClient(BoBoIdentityStore identityStore)
        {
            identity = identityStore ?? BoBoIdentityStore.Default;
        }

        internal bool IsConnected { get { return connected; } }
        internal bool IsUdpActive { get { return udpActive; } }
        internal bool IsDirectUdpPromoted { get { return udpActive && activeUdpPathRank >= 40; } }
        internal bool IsUdpNackActive { get { return udpActive && udpNackNegotiated; } }
        internal string ActiveVideoCodecLabel
        {
            get { return activeVideoCodecId == BoBoCoreInterop.VideoCodecHevc ? "H.265" : "H.264"; }
        }
        internal string ActiveUdpPathLabel
        {
            get
            {
                if (!udpActive || string.IsNullOrEmpty(activeUdpPathKind)) return "";
                return BoBoUdpCandidate.FormatDisplayLabel(activeUdpPathKind);
            }
        }
        internal string ActiveTransportLabel
        {
            get
            {
                if (routedViaEmbeddedTailnet)
                {
                    if (udpActive)
                    {
                        string prefix = activeUdpPathRank >= 40
                            ? BoBoUdpCandidate.FormatDisplayLabel(activeUdpPathKind)
                            : "内置跨网";
                        return prefix + " · " + (udpNackNegotiated ? "UDP+FEC/NACK" : "UDP+FEC");
                    }
                    return "内置跨网 · WireGuard";
                }
                return udpActive ? (udpNackNegotiated ? "UDP+FEC/NACK" : "UDP+FEC") : "TCP+TLS";
            }
        }
        internal double LatestRttMs { get { return latestRttMs; } }
        internal double LatestJitterMs { get { return latestJitterMs; } }
        internal double LatestPacketLossPct { get { return latestPacketLossPct; } }
        internal int UdpFecRecoveredPackets { get { BoBoUdpVideoAssembler a = udpAssembler; return a != null ? a.FecRecoveredPackets : 0; } }
        internal int UdpNackRecoveredPackets { get { BoBoUdpVideoAssembler a = udpAssembler; return a != null ? a.NackRecoveredPackets : 0; } }
        internal int UdpDroppedFrames { get { BoBoUdpVideoAssembler a = udpAssembler; return a != null ? a.DroppedIncompleteFrames : 0; } }
        internal int ActiveBitrateKbps { get { return activeBitrateKbps; } }
        internal long VideoFramesReceived { get { return Interlocked.Read(ref videoFramesReceived); } }
        internal long DecodeQueueIdrRequests { get { return Interlocked.Read(ref decodeQueueIdrRequests); } }
        internal List<BoBoMonitorDescriptor> RemoteMonitors { get { return remoteMonitors; } }

        private const string PinMismatchMessage = "目标设备的 TLS 证书指纹与本地配对记录不一致。请核对设备后移除旧授权并重新配对。";

        private static bool HasPinMismatch(BoBoIdentityStore store, string nodeId, string serverThumb)
        {
            BoBoAuthorizedPeer peer = store.FindPeerByNodeId(nodeId);
            return peer != null && !BoBoIdentityStore.FixedTimeEqualsHex(peer.CertThumbprint, serverThumb);
        }

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
                timeoutCts.CancelAfter(timeoutMs > 0 ? timeoutMs : 5000);
                using (var client = new TcpClient())
                using (timeoutCts.Token.Register(delegate { try { client.Close(); } catch { } }))
                {
                    try
                    {
                        client.NoDelay = true;
                        bool viaEmbedded = await ConnectWithCancelAsync(client, res.Host, res.Port, timeoutCts.Token).ConfigureAwait(false);
                        res.Reachable = true;
                        if (viaEmbedded)
                        {
                            IPAddress targetIp;
                            if (IPAddress.TryParse(res.Host, out targetIp))
                                res.ConnectedAddress = targetIp;
                            res.LocalAddress = null;
                        }
                        else
                        {
                            res.ConnectedAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                            res.LocalAddress = ((IPEndPoint)client.Client.LocalEndPoint).Address;
                        }

                        string serverThumb = "";
                        using (var sslStream = new SslStream(client.GetStream(), false,
                            delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                            {
                                if (cert != null) serverThumb = BoBoIdentityStore.ComputeCertSha256Hex(cert.GetRawCertData());
                                return cert != null;
                            }))
                        {
                            await sslStream.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                            long helloStart = Stopwatch.GetTimestamp();
                            await SendHelloAsync(sslStream, id, timeoutCts.Token).ConfigureAwait(false);
                            BoBoFrame? reply = await BoBoProtocol.ReadOneFrameAsync(sslStream, timeoutCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                            res.HandshakeRttMs = (Stopwatch.GetTimestamp() - helloStart) * 1000.0 / Stopwatch.Frequency;
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

                            if (string.IsNullOrEmpty(serverThumb))
                            {
                                res.Error = "未能从 TLS 握手提取目标主机证书指纹。";
                                return res;
                            }
                            res.HostNodeId = BoBoProtocol.GetStr(ack, "nodeId", "");
                            if (HasPinMismatch(id, res.HostNodeId, serverThumb))
                            {
                                res.Error = PinMismatchMessage;
                                return res;
                            }
                            res.IsBoBoHost = true;
                            res.HostDeviceCode = BoBoProtocol.GetStr(ack, "deviceCode", "");
                            res.HostName = BoBoProtocol.GetStr(ack, "deviceName", res.Host);
                            res.HostThumbprint = serverThumb;
                            res.AllowIncoming = BoBoProtocol.GetBool(ack, "allowIncoming", true);
                            res.Authorized = BoBoProtocol.GetBool(ack, "authorized", false)
                                && id.IsPeerAuthorized(res.HostNodeId, serverThumb);
                            res.Platform = BoBoProtocol.GetStr(ack, "platform", BoBoProtocol.PlatformWindows);
                            res.HostControlMode = BoBoProtocol.GetStr(ack, "hostControlMode", BoBoProtocol.ControlModeFull);
                            res.ScreenOrientation = BoBoProtocol.GetInt(ack, "screenOrientation", 0);
                            res.Nonce = BoBoProtocol.GetStr(ack, "nonce", "");
                            res.Monitors = BoBoProtocol.ParseMonitors(ack, "monitors");
                            res.LanIps = BoBoProtocol.ParseStringList(ack, "lanIps");
                            res.PublicIpv6s = BoBoProtocol.ParseStringList(ack, "publicIpv6s");
                            res.UdpSupported = BoBoProtocol.GetBool(ack, "udpSupported", false);
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

        // Only probe on-link addresses announced by an already-reached BoBo peer.
        // Changing transport still requires matching the node and its TLS certificate.
        internal static async Task<BoBoHostProbeResult> TryResolveDirectLanHostAsync(
            BoBoHostProbeResult initialProbe, BoBoIdentityStore identityStore, int timeoutMs, CancellationToken ct)
        {
            if (initialProbe == null || !initialProbe.IsBoBoHost || initialProbe.ConnectedAddress == null ||
                !Tailscale.IsTailnetAddress(initialProbe.ConnectedAddress.ToString()) ||
                string.IsNullOrEmpty(initialProbe.HostNodeId) || string.IsNullOrEmpty(initialProbe.HostThumbprint)) return null;

            using (var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var seen = new HashSet<IPAddress>();
                var orderedTargets = new List<IPAddress>(8);
                foreach (string value in initialProbe.LanIps)
                {
                    IPAddress candidate;
                    if (IPAddress.TryParse(value, out candidate) &&
                        BoBoConnectionRoute.IsOnLocalSubnet(candidate, null) &&
                        seen.Add(candidate))
                    {
                        orderedTargets.Add(candidate);
                    }
                }
                foreach (string value6 in initialProbe.PublicIpv6s)
                {
                    IPAddress cand6;
                    if (IPAddress.TryParse(value6, out cand6) &&
                        BoBoNatTraversal.IsUsableGlobalIPv6(cand6) &&
                        seen.Add(cand6))
                    {
                        orderedTargets.Add(cand6);
                    }
                }
                List<IPAddress> tsDirect = BoBoEmbeddedTailnet.Default.GetPeerDirectCandidateIps(
                    initialProbe.ConnectedAddress.ToString());
                for (int i = 0; i < tsDirect.Count; i++)
                {
                    IPAddress tip = tsDirect[i];
                    if (tip != null && !Tailscale.IsTailnetAddress(tip.ToString()) && seen.Add(tip))
                    {
                        orderedTargets.Add(tip);
                    }
                }
                if (orderedTargets.Count == 0) return null;

                var tasks = new List<Task<BoBoHostProbeResult>>(Math.Min(6, orderedTargets.Count));
                for (int i = 0; i < orderedTargets.Count && tasks.Count < 6; i++)
                {
                    tasks.Add(ProbeHostAsync(orderedTargets[i].ToString(), initialProbe.Port, identityStore, timeoutMs, directCts.Token));
                }

                while (tasks.Count > 0)
                {
                    Task<BoBoHostProbeResult> finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(finished);
                    BoBoHostProbeResult probe = await finished.ConfigureAwait(false);
                    if (probe == null || !probe.IsBoBoHost || !probe.AllowIncoming || probe.ConnectedAddress == null ||
                        Tailscale.IsTailnetAddress(probe.ConnectedAddress.ToString()) ||
                        !string.Equals(probe.HostNodeId, initialProbe.HostNodeId, StringComparison.OrdinalIgnoreCase) ||
                        !BoBoIdentityStore.FixedTimeEqualsHex(probe.HostThumbprint, initialProbe.HostThumbprint)) continue;
                    directCts.Cancel();
                    return probe;
                }
                return null;
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
                using (timeoutCts.Token.Register(delegate { try { client.Close(); } catch { } }))
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
                            BoBoFrame? ackFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, timeoutCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
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
                            if (string.IsNullOrEmpty(serverThumb))
                                return Tuple.Create(false, hostName, "未能从 TLS 握手提取对方真实证书指纹。");
                            if (HasPinMismatch(id, hostNodeId, serverThumb))
                                return Tuple.Create(false, hostName, PinMismatchMessage);
                            string hostThumb = serverThumb;
                            string nonce = BoBoProtocol.GetStr(ack, "nonce", "");
                            if (string.IsNullOrWhiteSpace(hostNodeId) || string.IsNullOrWhiteSpace(nonce))
                                return Tuple.Create(false, hostName, "对方的配对身份或会话挑战无效。");

                            string proof = BoBoIdentityStore.ComputePinProof(fourDigitPin, nonce, id.CertThumbprint, hostThumb);
                            var req = new Dictionary<string, object>();
                            req["type"] = "PAIR_REQUEST";
                            req["proof"] = proof;
                            req["authSig"] = id.SignAuthChallenge(nonce, hostThumb);
                            req["clientCertDer"] = id.CertDerBase64;
                            await BoBoProtocol.WriteJsonAsync(sslStream, req, timeoutCts.Token).ConfigureAwait(false);

                            BoBoFrame? resFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, timeoutCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                            if (!resFrame.HasValue || resFrame.Value.Type != BoBoPacketType.ControlJson)
                                return Tuple.Create(false, "", "被控端未返回配对结果。");

                            var result = BoBoProtocol.ParseJsonPayload(resFrame.Value.Payload);
                            bool ok = BoBoProtocol.GetStr(result, "type", "") == "PAIR_RESULT"
                                && BoBoProtocol.GetBool(result, "ok", false);
                            if (!ok)
                            {
                                string err = BoBoProtocol.GetStr(result, "error", "4 位配对码校验失败，请重试。");
                                return Tuple.Create(false, hostName, err);
                            }

                            if (!id.AuthorizeOrUpdatePeer(new BoBoAuthorizedPeer
                            {
                                NodeId = hostNodeId,
                                DeviceCode = hostCode,
                                DeviceName = hostName,
                                CertThumbprint = hostThumb,
                                LastHost = host
                            })) return Tuple.Create(false, hostName, PinMismatchMessage);
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
                if (prog.TotalBytes > BoBoProtocol.MaxFileTransferBytes)
                {
                    prog.Error = "单文件大小超出 2 GiB 上限。";
                    return prog;
                }
                string shaHex = ComputeFileSha256(localFilePath);

                using (var client = new TcpClient())
                {
                    client.NoDelay = true;
                    using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (hsCts.Token.Register(delegate { try { client.Close(); } catch { } }))
                    {
                        hsCts.CancelAfter(15000);
                        await ConnectWithCancelAsync(client, host, targetPort, hsCts.Token).ConfigureAwait(false);
                        string serverThumb = "";
                        using (var sslStream = new SslStream(client.GetStream(), false,
                            delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                            {
                                if (cert != null) serverThumb = BoBoIdentityStore.ComputeCertSha256Hex(cert.GetRawCertData());
                                return cert != null;
                            }))
                        {
                            await sslStream.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                            await SendHelloAsync(sslStream, id, hsCts.Token).ConfigureAwait(false);
                            BoBoFrame? ackFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, hsCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                            if (!ackFrame.HasValue || ackFrame.Value.Type != BoBoPacketType.ControlJson) { prog.Error = "握手失败。"; return prog; }
                            var ack = BoBoProtocol.ParseJsonPayload(ackFrame.Value.Payload);
                            if (BoBoProtocol.GetStr(ack, "type", "") != "HELLO_ACK" ||
                                BoBoProtocol.GetStr(ack, "protocol", "") != BoBoProtocol.ProtocolMagic ||
                                !BoBoProtocol.GetBool(ack, "authorized", false))
                            {
                                prog.Error = "尚未与该设备完成 BoBo 配对授权，请先配对后再传文件。";
                                return prog;
                            }
                            string hostNodeId = BoBoProtocol.GetStr(ack, "nodeId", "");
                            if (string.IsNullOrEmpty(serverThumb) || !id.IsPeerAuthorized(hostNodeId, serverThumb))
                            {
                                prog.Error = "目标主机 TLS 证书指纹未通过本地信任钉扎校验，已中止文件发送。";
                                return prog;
                            }
                            if (BoBoProtocol.GetBool(ack, "authChallenge", false))
                            {
                                string nonce = BoBoProtocol.GetStr(ack, "nonce", "");
                                var proofMsg = new Dictionary<string, object>();
                                proofMsg["type"] = "AUTH_PROOF";
                                proofMsg["signature"] = id.SignAuthChallenge(nonce, serverThumb);
                                proofMsg["clientCertDer"] = id.CertDerBase64;
                                await BoBoProtocol.WriteJsonAsync(sslStream, proofMsg, hsCts.Token).ConfigureAwait(false);
                                BoBoFrame? authFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, hsCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                                var authRes = authFrame.HasValue && authFrame.Value.Type == BoBoPacketType.ControlJson
                                    ? BoBoProtocol.ParseJsonPayload(authFrame.Value.Payload) : null;
                                if (BoBoProtocol.GetStr(authRes, "type", "") != "AUTH_RESULT" || !BoBoProtocol.GetBool(authRes, "ok", false))
                                {
                                    prog.Error = BoBoProtocol.GetStr(authRes, "error", "双向密钥身份校验失败。");
                                    return prog;
                                }
                            }

                            var offer = new Dictionary<string, object>();
                            offer["type"] = "FILE_OFFER";
                            offer["transferId"] = prog.TransferId;
                            offer["fileName"] = prog.FileName;
                            offer["totalBytes"] = prog.TotalBytes;
                            offer["sha256"] = shaHex;
                            await BoBoProtocol.WriteJsonAsync(sslStream, offer, hsCts.Token).ConfigureAwait(false);

                            BoBoFrame? accFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, hsCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                            if (!accFrame.HasValue) { prog.Error = "对方未接受文件传输。"; return prog; }
                            var acc = BoBoProtocol.ParseJsonPayload(accFrame.Value.Payload);
                            if (accFrame.Value.Type != BoBoPacketType.ControlJson ||
                                BoBoProtocol.GetStr(acc, "type", "") != "FILE_ACCEPT" ||
                                BoBoProtocol.GetStr(acc, "transferId", "") != prog.TransferId)
                            {
                                prog.Error = BoBoProtocol.GetStr(acc, "error", BoBoProtocol.GetStr(acc, "message", "对方拒绝了文件传输。"));
                                return prog;
                            }

                            // Disable handshake timer once FILE_ACCEPT succeeded; large files stream under caller's ct
                            hsCts.CancelAfter(Timeout.Infinite);

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
                            hsCts.CancelAfter(30000);
                            await BoBoProtocol.WriteJsonAsync(sslStream, done, hsCts.Token).ConfigureAwait(false);

                            BoBoFrame? finalFrame = await BoBoProtocol.ReadOneFrameAsync(sslStream, hsCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
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
            forcedDisconnectReason = null;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            CancellationToken ct = cts.Token;

            int targetPort = options.Port > 0 ? options.Port : BoBoProtocol.DefaultTcpPort;
            try
            {
                tcp = new TcpClient();
                tcp.NoDelay = true;
                // Balanced TCP buffers prevent micro-stalls on 2K 90Hz TCP fallback without multi-second bloat
                tcp.ReceiveBufferSize = 524288;
                tcp.SendBufferSize = 131072;
                BoBoUdpSocketUtil.TrySetTcpDscp(tcp.Client);
                ConfigureTcpKeepAlive(tcp);

                string serverThumb = "";
                BoBoFrame? ackFrame;
                double handshakeRttMs;
                using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (hsCts.Token.Register(delegate { try { if (tcp != null) tcp.Close(); } catch { } }))
                {
                    hsCts.CancelAfter(12000);
                    routedViaEmbeddedTailnet = await ConnectWithCancelAsync(tcp, options.Host, targetPort, hsCts.Token).ConfigureAwait(false);

                    ssl = new SslStream(tcp.GetStream(), false,
                        delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                        {
                            if (cert != null) serverThumb = BoBoIdentityStore.ComputeCertSha256Hex(cert.GetRawCertData());
                            return cert != null;
                        });
                    await ssl.AuthenticateAsClientAsync("BoBoRemote-V6-Node", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                    long helloStartTicks = Stopwatch.GetTimestamp();
                    await SendHelloAsync(ssl, identity, hsCts.Token).ConfigureAwait(false);

                    ackFrame = await BoBoProtocol.ReadOneFrameAsync(ssl, hsCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                    handshakeRttMs = (Stopwatch.GetTimestamp() - helloStartTicks) * 1000.0 / Stopwatch.Frequency;
                }
                if (handshakeRttMs > 0 && handshakeRttMs < 5000) latestRttMs = handshakeRttMs;
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

                remoteMonitors = BoBoProtocol.ParseMonitors(ack, "monitors");
                RemotePlatform = BoBoProtocol.GetStr(ack, "platform", BoBoProtocol.PlatformWindows);
                SupportsSuperDisplayViewport = string.Equals(RemotePlatform, BoBoProtocol.PlatformWindows, StringComparison.OrdinalIgnoreCase) &&
                    BoBoProtocol.GetInt(ack, "superDisplayVersion", 0) >= BoBoDisplayState.Version;
                RemoteHostControlMode = BoBoProtocol.GetStr(ack, "hostControlMode", BoBoProtocol.ControlModeFull);
                string hostNodeId = BoBoProtocol.GetStr(ack, "nodeId", "");
                string hostCode = BoBoProtocol.GetStr(ack, "deviceCode", "");
                string hostName = BoBoProtocol.GetStr(ack, "deviceName", options.Host);
                string nonce = BoBoProtocol.GetStr(ack, "nonce", "");

                // Strict Client TOFU Certificate Pinning: NEVER trust JSON certThumbprint or silently overwrite a pinned mismatch!
                if (string.IsNullOrEmpty(serverThumb))
                {
                    Disconnect();
                    return Tuple.Create(false, "CERT_MISSING", "未能从 TLS 握手获取目标主机证书指纹，已拒绝连接。");
                }
                if (HasPinMismatch(identity, hostNodeId, serverThumb))
                {
                    Disconnect();
                    return Tuple.Create(false, "CERT_MISMATCH", PinMismatchMessage);
                }
                if (!BoBoProtocol.GetBool(ack, "authorized", false) || !identity.IsPeerAuthorized(hostNodeId, serverThumb))
                {
                    Disconnect();
                    return Tuple.Create(false, "PAIR_REQUIRED", "请先输入被控端 BoBo远程 生成的 4 位临时配对码。");
                }

                // Prove private-key possession over host's fresh sessionNonce before issuing START_STREAM
                if (BoBoProtocol.GetBool(ack, "authChallenge", false))
                {
                    var proofMsg = new Dictionary<string, object>();
                    proofMsg["type"] = "AUTH_PROOF";
                    proofMsg["signature"] = identity.SignAuthChallenge(nonce, serverThumb);
                    proofMsg["clientCertDer"] = identity.CertDerBase64;
                    BoBoFrame? authResFrame;
                    using (var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (authCts.Token.Register(delegate { try { if (tcp != null) tcp.Close(); } catch { } }))
                    {
                        authCts.CancelAfter(8000);
                        await BoBoProtocol.WriteJsonAsync(ssl, proofMsg, authCts.Token).ConfigureAwait(false);
                        authResFrame = await BoBoProtocol.ReadOneFrameAsync(ssl, authCts.Token, BoBoProtocol.MaxPreAuthPayloadBytes).ConfigureAwait(false);
                    }
                    var authRes = authResFrame.HasValue && authResFrame.Value.Type == BoBoPacketType.ControlJson
                        ? BoBoProtocol.ParseJsonPayload(authResFrame.Value.Payload) : null;
                    if (BoBoProtocol.GetStr(authRes, "type", "") != "AUTH_RESULT" || !BoBoProtocol.GetBool(authRes, "ok", false))
                    {
                        Disconnect();
                        return Tuple.Create(false, "AUTH_FAILED", BoBoProtocol.GetStr(authRes, "error", "双向密钥身份校验未通过。"));
                    }
                }

                if (!string.IsNullOrEmpty(hostNodeId))
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

                lastStartOptions = options;
                activeVideoCodecId = BoBoCoreInterop.VideoCodecH264;
                activeBitrateKbps = options.BitrateKbps;
                var startReq = new Dictionary<string, object>();
                startReq["type"] = "START_STREAM";
                startReq["monitorIndex"] = options.MonitorIndex;
                startReq["width"] = options.Width;
                startReq["height"] = options.Height;
                startReq["fps"] = options.Fps;
                startReq["bitrateKbps"] = options.BitrateKbps;
                startReq["autoBitrate"] = options.AutoBitrate;
                startReq["freeMouse"] = options.FreeMouse;
                startReq["muteHost"] = options.MuteHostAudio;
                startReq["requestPrivacy"] = options.RequestPrivacy;
                startReq["superDisplay"] = options.SuperDisplay && SupportsSuperDisplayViewport;
                startReq["clientWidth"] = options.ClientWidth;
                startReq["clientHeight"] = options.ClientHeight;
                if (SupportsSuperDisplayViewport)
                {
                    startReq["superDisplayVersion"] = BoBoDisplayState.Version;
                    startReq["fullscreen"] = options.ClientFullscreen;
                }
                bool canHevcDec = options.PreferHevc && BoBoCoreInterop.IsHevcDecodeSupported();
                startReq["preferHevc"] = canHevcDec;
                startReq["videoCodecs"] = canHevcDec ? new string[] { "hevc", "h264" } : new string[] { "h264" };
                if (!string.IsNullOrWhiteSpace(options.CustomStunHost) && options.CustomStunPort > 0 && options.CustomStunPort <= 65535)
                {
                    startReq["stunHost"] = options.CustomStunHost.Trim();
                    startReq["stunPort"] = options.CustomStunPort;
                }
                startReq["udpTransport"] = true;
                startReq["udpCipher"] = "aes-128-gcm";
                startReq["udpCiphers"] = new string[] { "aes-128-gcm", "aes-128-ctr-hmac" };
                startReq["udpInterleavedFec"] = true;
                startReq["udpNack"] = true;
                startReq["audioCodecs"] = options.PreferCompressedAudio
                    ? new string[] { "aac", "adpcm" }
                    : new string[] { "adpcm" };
                startReq["nativeLocalCursor"] = true;
                startReq["cursorMetrics"] = true;
                startReq["initialRttMs"] = Math.Round(handshakeRttMs, 1);
                try
                {
                    UdpClient preUc = CreateDualStackMediaUdp();
                    if (preUc != null)
                    {
                        int prePort = ((IPEndPoint)preUc.Client.LocalEndPoint).Port;
                        lock (udpGate)
                        {
                            if (preBoundUdp != null) { try { preBoundUdp.Close(); } catch { } }
                            preBoundUdp = preUc;
                        }
                        List<BoBoUdpCandidate> initClientCands = BoBoNatTraversal.GatherLocalCandidates(prePort, null, false);
                        if (initClientCands.Count > 0)
                        {
                            startReq["udpCandidates"] = BoBoNatTraversal.ToProtocolObjects(initClientCands);
                        }
                    }
                }
                catch { }
                await SendJsonInternalAsync(startReq, ct).ConfigureAwait(false);

                Interlocked.Exchange(ref lastRxTicks, Stopwatch.GetTimestamp());
                connected = true;
                Task _rx = Task.Run(delegate { return ReceiveLoopAsync(ct); });
                Task _ping = Task.Run(delegate { return PingLoopAsync(ct); });
                return Tuple.Create(true, "OK", hostName);
            }
            catch (OperationCanceledException)
            {
                Disconnect();
                return Tuple.Create(false, "TIMEOUT", "连接被控端握手超时。请确认对方网络互通且已开启 BoBo远程。");
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
                    Interlocked.Exchange(ref lastRxTicks, Stopwatch.GetTimestamp());
                    BoBoFrame frame = frameOpt.Value;

                    if (frame.Type == BoBoPacketType.VideoH264 || frame.Type == BoBoPacketType.VideoHevc)
                    {
                        if (udpActive) continue; // Ignore straggling pre-switchover TCP video once UDP+FEC is active
                        int w, h;
                        bool isKey;
                        byte[] h264;
                        if (BoBoProtocol.ParseVideoPayload(frame.Payload, out w, out h, out isKey, out h264))
                        {
                            int vCodec = (frame.Type == BoBoPacketType.VideoHevc)
                                ? BoBoCoreInterop.VideoCodecHevc
                                : BoBoCoreInterop.VideoCodecH264;
                            activeVideoCodecId = vCodec;
                            Interlocked.Increment(ref videoFramesReceived);
                            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                            if (nc != null)
                            {
                                int pushRc = nc.PushVideoEx(h264, h264.Length, isKey, w, h, vCodec);
                                if (pushRc == BoBoCoreInterop.PushVideoNeedKeyframe && ShouldEmitDecodeQueueIdrRequest())
                                {
                                    SendRequestKeyframe();
                                }
                            }
                            Action<byte[], bool, int, int> rawEv = RawVideoFrameReceived;
                            if (rawEv != null) { try { rawEv(h264, isKey, w, h); } catch { } }
                        }
                        continue;
                    }

                    if (frame.Type == BoBoPacketType.AudioAdpcm || frame.Type == BoBoPacketType.AudioAac)
                    {
                        if (udpActive) continue;
                        int sr, ch, samples;
                        byte[] audioBytes;
                        if (BoBoProtocol.ParseAudioPayload(frame.Payload, out sr, out ch, out samples, out audioBytes))
                        {
                            int codecId = (frame.Type == BoBoPacketType.AudioAac)
                                ? BoBoCoreInterop.AudioCodecAac
                                : BoBoCoreInterop.AudioCodecAdpcm;
                            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                            if (nc != null) nc.PushAudioEx(audioBytes, audioBytes.Length, sr, ch, samples, codecId);
                        }
                        continue;
                    }

                    if (frame.Type == BoBoPacketType.CursorShape)
                    {
                        if (!udpActive) ApplyRemoteCursorShape(frame.Payload, 0);
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
                            int actualBps = BoBoProtocol.GetInt(msg, "bitrateKbps", activeBitrateKbps);
                            if (actualBps > 0) activeBitrateKbps = actualBps;
                            string startedPlat = BoBoProtocol.GetStr(msg, "platform", "");
                            if (!string.IsNullOrEmpty(startedPlat)) RemotePlatform = startedPlat;
                            string startedMode = BoBoProtocol.GetStr(msg, "hostControlMode", "");
                            if (!string.IsNullOrEmpty(startedMode)) RemoteHostControlMode = startedMode;
                            HostCursorComposited = BoBoProtocol.GetBool(msg, "hostCursorComposited", false);
                            string startedVideoCodec = BoBoProtocol.GetStr(msg, "videoCodec", "h264");
                            activeVideoCodecId = string.Equals(startedVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                                ? BoBoCoreInterop.VideoCodecHevc
                                : BoBoCoreInterop.VideoCodecH264;
                            BoBoCoreInterop.BoBoClientNativeHandle curNc = nativeClient;
                            if (curNc != null) curNc.SetHostCursorComposited(HostCursorComposited);

                            // Auto-activate encrypted UDP+FEC/NACK channel (including over embedded tsnet /udp-bridge and direct IPv6/STUN punching)
                            int udpPort = BoBoProtocol.GetInt(msg, "udpPort", 0);
                            long udpSidLong = BoBoProtocol.GetLong(msg, "udpSessionId", 0);
                            string udpKeyB64 = BoBoProtocol.GetStr(msg, "udpKey", "");
                            string udpCipherStr = BoBoProtocol.GetStr(msg, "udpCipher", "aes-128-ctr-hmac");
                            BoBoUdpCipherMode udpCipherMode = string.Equals(udpCipherStr, "aes-128-gcm", StringComparison.OrdinalIgnoreCase)
                                ? BoBoUdpCipherMode.Aes128Gcm
                                : BoBoUdpCipherMode.Aes128CtrHmac;
                            bool hostUdpNack = BoBoProtocol.GetBool(msg, "udpNack", false);
                            List<BoBoUdpCandidate> hostInitCands = BoBoNatTraversal.ParseCandidatesFromKey(msg, "udpCandidates");
                            if (udpPort > 0 && udpSidLong != 0 && !string.IsNullOrEmpty(udpKeyB64))
                            {
                                TryStartUdpChannel(udpPort, unchecked((uint)udpSidLong), udpKeyB64, hostUdpNack, udpCipherMode, hostInitCands, ct);
                            }

                            Action<List<BoBoMonitorDescriptor>, int, int, int> ev = StreamStarted;
                            if (ev != null) { try { ev(remoteMonitors, w, h, fps); } catch { } }
                            PublishDisplayState(msg, false, true);
                        }
                        else if (t == "RECONFIGURED")
                        {
                            int actualBps = BoBoProtocol.GetInt(msg, "bitrateKbps", activeBitrateKbps);
                            if (actualBps > 0) activeBitrateKbps = actualBps;
                            PublishDisplayState(msg, false, false);
                        }
                        else if (t == "UDP_CANDIDATES")
                        {
                            List<BoBoUdpCandidate> remoteCands = BoBoNatTraversal.ParseCandidatesFromMessage(msg);
                            if (remoteCands.Count > 0)
                            {
                                AddUdpCandidatesAndProbe(remoteCands);
                            }
                        }
                        else if (t == "CURSOR_MODE_STATE")
                        {
                            HostCursorComposited = BoBoProtocol.GetBool(msg, "hostCursorComposited", true);
                            BoBoCoreInterop.BoBoClientNativeHandle curNc = nativeClient;
                            if (curNc != null) curNc.SetHostCursorComposited(HostCursorComposited);
                        }
                        else if (t == "BITRATE_UPDATED")
                        {
                            int newBps = BoBoProtocol.GetInt(msg, "bitrateKbps", activeBitrateKbps);
                            int newFps = BoBoProtocol.GetInt(msg, "fps", 60);
                            if (newBps > 0) activeBitrateKbps = newBps;
                            Action<int, int> brEv = LiveBitrateUpdated;
                            if (brEv != null) { try { brEv(newBps, newFps); } catch { } }
                        }
                        else if (t == "SUPER_DISPLAY_STATE")
                        {
                            PublishDisplayState(msg, true, true);
                        }
                        else if (t == "PONG")
                        {
                            long sentTicks = BoBoProtocol.GetLong(msg, "ts", 0);
                            if (sentTicks > 0)
                            {
                                double rtt = (Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency;
                                if (rtt >= 0 && rtt < 10000)
                                {
                                    latestTlsRttMs = rtt;
                                    // P1-4: Never overwrite active UDP RTT with TLS control RTT
                                    if (!udpActive)
                                    {
                                        latestRttMs = rtt;
                                        Action<double> rttEv = RttUpdated;
                                        if (rttEv != null) { try { rttEv(rtt); } catch { } }
                                    }
                                }
                            }
                        }
                        else if (t == "PRIVACY_STATE")
                        {
                            bool act = BoBoProtocol.GetBool(msg, "active", false);
                            Action<bool> privEv = RemotePrivacyStateChanged;
                            if (privEv != null) { try { privEv(act); } catch { } }
                        }
                        else if (t == "HOST_NOTICE")
                        {
                            string ncode = BoBoProtocol.GetStr(msg, "code", "");
                            string nmsg = BoBoProtocol.GetStr(msg, "message", "");
                            Action<string, string> noticeEv = HostNoticeReceived;
                            if (noticeEv != null && !string.IsNullOrEmpty(nmsg))
                            {
                                try { noticeEv(ncode, nmsg); } catch { }
                            }
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
                string reason = !string.IsNullOrEmpty(forcedDisconnectReason) ? forcedDisconnectReason : disconnectReason;
                if (wasConnected)
                {
                    Action<string> discEv = Disconnected;
                    if (discEv != null) { try { discEv(reason); } catch { } }
                }
            }
        }

        private void PublishDisplayState(Dictionary<string, object> message, bool isDisplayReply, bool publishLegacyEvent)
        {
            BoBoDisplayState state = BoBoDisplayState.Parse(message, isDisplayReply, SupportsSuperDisplayViewport);
            if (publishLegacyEvent || SupportsSuperDisplayViewport)
            {
                SuperDisplayActive = state.Active;
                SuperDisplayNativeMatch = state.NativeMatch;
            }
            Action<BoBoDisplayState> displayChanged = DisplayStateChanged;
            if (displayChanged != null) { try { displayChanged(state); } catch { } }
            Action<bool, bool, int, int> legacyChanged = SuperDisplayStateChanged;
            if (publishLegacyEvent && legacyChanged != null)
            {
                try
                {
                    legacyChanged(state.Active, state.NativeMatch,
                        BoBoProtocol.GetInt(message, "width", 0), BoBoProtocol.GetInt(message, "height", 0));
                }
                catch { }
            }
        }

        private async Task PingLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && connected)
            {
                try
                {
                    long lastRx = Interlocked.Read(ref lastRxTicks);
                    if (lastRx != 0 && ((Stopwatch.GetTimestamp() - lastRx) / (double)Stopwatch.Frequency) >= 15.0)
                    {
                        forcedDisconnectReason = "连接超时（长时间未收到被控端响应）";
                        try { if (tcp != null) tcp.Close(); } catch { }
                        break;
                    }
                    if (!udpActive)
                    {
                        var ping = new Dictionary<string, object>();
                        ping["type"] = "PING";
                        ping["ts"] = Stopwatch.GetTimestamp();
                        await SendJsonInternalAsync(ping, ct).ConfigureAwait(false);

                        if (latestRttMs > 0.1)
                        {
                            var tel = new Dictionary<string, object>();
                            tel["type"] = "TELEMETRY";
                            tel["rttMs"] = Math.Round(latestRttMs, 1);
                            tel["lossPct"] = 0.0;
                            SendJsonFireAndForget(tel);
                        }
                    }
                    await Task.Delay(750, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }

        private static byte[] BuildBo74HelloPayload(uint probeId, byte rank, bool forceSelect)
        {
            byte[] p = new byte[14];
            p[0] = 0x42; // B
            p[1] = 0x4F; // O
            p[2] = 0x37; // 7
            p[3] = 0x34; // 4
            BoBoUdpCrypto.WriteUInt32LE(p, 4, probeId);
            p[8] = rank;
            p[9] = (byte)(forceSelect ? 1 : 0);
            return p;
        }

        private UdpProbeCandidate RegisterUdpCandidate(string kind, IPEndPoint ep)
        {
            if (ep == null) return null;
            IPEndPoint normEp = BoBoNatTraversal.NormalizeRemoteEndPoint(ep) ?? ep;
            foreach (UdpProbeCandidate existing in udpProbeTable.Values)
            {
                if (existing != null && existing.EndPoint != null &&
                    existing.EndPoint.Equals(normEp) &&
                    string.Equals(existing.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }
            uint pid = unchecked((uint)Interlocked.Increment(ref nextUdpProbeId));
            int rankInt = BoBoUdpCandidate.GetRankForKind(kind);
            byte rankByte = (byte)Math.Max(0, Math.Min(255, rankInt));
            var cand = new UdpProbeCandidate
            {
                ProbeId = pid,
                EndPoint = normEp,
                Kind = kind ?? BoBoUdpCandidate.KindDirectIPv4,
                Rank = rankInt,
                HelloPayload = BuildBo74HelloPayload(pid, rankByte, false),
                ForceHelloPayload = BuildBo74HelloPayload(pid, rankByte, true)
            };
            udpProbeTable[pid] = cand;
            udpCandidatesUpdated = true;
            return cand;
        }

        private static UdpClient CreateDualStackMediaUdp()
        {
            UdpClient uc = null;
            try
            {
                uc = new UdpClient(AddressFamily.InterNetworkV6);
                uc.Client.DualMode = true;
                BoBoUdpSocketUtil.ConfigureLowLatencySocket(uc.Client, 2 * 1024 * 1024, 4 * 1024 * 1024);
                BoBoNatTraversal.ConfigureLowLatencyUdpSocket(uc.Client);
                uc.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                return uc;
            }
            catch
            {
                if (uc != null) { try { uc.Close(); } catch { } }
                uc = new UdpClient(AddressFamily.InterNetwork);
                BoBoUdpSocketUtil.ConfigureLowLatencySocket(uc.Client, 2 * 1024 * 1024, 4 * 1024 * 1024);
                BoBoNatTraversal.ConfigureLowLatencyUdpSocket(uc.Client);
                uc.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                return uc;
            }
        }

        private List<UdpProbeCandidate> RegisterCandidateList(List<BoBoUdpCandidate> candidates)
        {
            var newlyAdded = new List<UdpProbeCandidate>();
            if (candidates == null || candidates.Count == 0) return newlyAdded;
            List<BoBoUdpCandidate> expanded = BoBoNatTraversal.ExpandCandidatesWithPortPrediction(candidates, 48);
            for (int i = 0; i < expanded.Count; i++)
            {
                BoBoUdpCandidate c = expanded[i];
                IPEndPoint ep;
                if (c == null || !c.TryGetIPEndPoint(out ep)) continue;
                UdpProbeCandidate reg = RegisterUdpCandidate(c.Kind, ep);
                if (reg != null) newlyAdded.Add(reg);
            }
            return newlyAdded;
        }

        private void AddUdpCandidatesAndProbe(List<BoBoUdpCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;
            UdpClient uc;
            BoBoUdpCrypto cr;
            lock (udpGate)
            {
                uc = udpClient;
                cr = udpCrypto;
            }
            if (uc == null || cr == null) return;

            List<UdpProbeCandidate> newlyAdded = RegisterCandidateList(candidates);
            for (int i = 0; i < newlyAdded.Count; i++)
            {
                UdpProbeCandidate p = newlyAdded[i];
                SendUdpRaw(uc, cr, BoBoUdpPacketType.Hello, p.HelloPayload, p.EndPoint);
            }
        }

        private void TryStartUdpChannel(
            int udpPort,
            uint udpSessionId,
            string udpKeyB64,
            bool hostUdpNack,
            BoBoUdpCipherMode cipherMode,
            List<BoBoUdpCandidate> hostInitCands,
            CancellationToken ct)
        {
            try
            {
                byte[] key = Convert.FromBase64String(udpKeyB64);
                if (key == null || key.Length < 32) return;
                TcpClient curTcp = tcp;
                if (curTcp == null || curTcp.Client == null) return;
                var tcpEp = curTcp.Client.RemoteEndPoint as IPEndPoint;
                if (tcpEp == null) return;

                IPAddress targetIp = tcpEp.Address;
                if (targetIp.IsIPv4MappedToIPv6) targetIp = targetIp.MapToIPv4();

                UdpClient uc = null;
                lock (udpGate)
                {
                    uc = preBoundUdp;
                    preBoundUdp = null;
                    DisposeUdpLocked();
                }
                if (uc == null)
                {
                    uc = CreateDualStackMediaUdp();
                }

                int localUdpPort = ((IPEndPoint)uc.Client.LocalEndPoint).Port;
                var crypto = new BoBoUdpCrypto(udpSessionId, key, false, cipherMode);
                Array.Clear(key, 0, key.Length);
                var assembler = new BoBoUdpVideoAssembler { NackEnabled = hostUdpNack };
                var inCodec = new BoBoUdpInputCodec();

                lock (udpGate)
                {
                    udpClient = uc;
                    udpRemoteEp = null;
                    udpCrypto = crypto;
                    udpAssembler = assembler;
                    udpInputCodec = inCodec;
                    udpActive = false;
                    udpNackNegotiated = hostUdpNack;
                    latestJitterMs = 0.0;
                    prevUdpRttMs = 0.0;
                    lastUdpRxTicks = 0;
                    Interlocked.Exchange(ref udpFallbackSuppressUntilTicks, 0);
                    lastCursorSeq = 0;
                }

                if (hostInitCands != null && hostInitCands.Count > 0)
                {
                    RegisterCandidateList(hostInitCands);
                }

                IPEndPoint fallbackEp = null;
                bool useTsNet = routedViaEmbeddedTailnet;
                if (!useTsNet)
                {
                    bool useRelay = lastStartOptions != null && lastStartOptions.IsCustomRelay && lastStartOptions.RelayUdpPort > 0;
                    string initKind;
                    int initPort;
                    if (useRelay)
                    {
                        initKind = BoBoUdpCandidate.KindRelay;
                        initPort = lastStartOptions.RelayUdpPort;
                    }
                    else
                    {
                        string ipStr = targetIp.ToString();
                        initKind = BoBoProtocol.IsPrivateLanIp(ipStr)
                            ? BoBoUdpCandidate.KindLan
                            : (targetIp.AddressFamily == AddressFamily.InterNetworkV6
                                ? BoBoUdpCandidate.KindDirectIPv6
                                : BoBoUdpCandidate.KindDirectIPv4);
                        initPort = udpPort;
                    }
                    UdpProbeCandidate initCand = RegisterUdpCandidate(initKind, new IPEndPoint(targetIp, initPort));
                    if (initCand != null)
                    {
                        fallbackEp = initCand.EndPoint;
                        udpRemoteEp = fallbackEp;
                    }
                }
                else
                {
                    BoBoStreamStartOptions optsSnap = lastStartOptions;
                    string peerHost = optsSnap != null ? optsSnap.Host : "";
                    List<IPAddress> tailnetDirectIps = BoBoEmbeddedTailnet.Default.GetPeerDirectCandidateIps(peerHost);
                    for (int ti = 0; ti < tailnetDirectIps.Count; ti++)
                    {
                        IPAddress tip = tailnetDirectIps[ti];
                        string directKind = BoBoProtocol.IsPrivateLanIp(tip.ToString())
                            ? BoBoUdpCandidate.KindLan
                            : (tip.AddressFamily == AddressFamily.InterNetworkV6
                                ? BoBoUdpCandidate.KindDirectIPv6
                                : BoBoUdpCandidate.KindDirectIPv4);
                        RegisterUdpCandidate(directKind, new IPEndPoint(tip, udpPort));
                    }

                    Task _bridgeTask = Task.Run(async delegate
                    {
                        int bPort = 0;
                        try
                        {
                            bPort = await BoBoEmbeddedTailnet.Default.OpenUdpBridgeAsync(
                                peerHost, udpPort, localUdpPort, 4000, ct).ConfigureAwait(false);
                        }
                        catch { }
                        if (bPort <= 0) return;

                        bool attached = false;
                        lock (udpGate)
                        {
                            if (!ct.IsCancellationRequested && connected && udpClient == uc)
                            {
                                embeddedUdpBridgePort = bPort;
                                attached = true;
                            }
                        }
                        if (!attached)
                        {
                            Task _cl = BoBoEmbeddedTailnet.Default.CloseUdpBridgeAsync(bPort);
                            return;
                        }
                        UdpProbeCandidate bridgeCand = RegisterUdpCandidate(
                            BoBoUdpCandidate.KindTsNet, new IPEndPoint(IPAddress.Loopback, bPort));
                        if (bridgeCand != null)
                        {
                            if (udpRemoteEp == null) udpRemoteEp = bridgeCand.EndPoint;
                            SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, bridgeCand.HelloPayload, bridgeCand.EndPoint);
                        }
                    });
                }

                // Concurrent Dual-Stack RFC 5389 STUN + UPnP/NAT-PMP router mapping on the SAME bound socket
                BoBoStreamStartOptions stunOpts = lastStartOptions;
                string stunHost = stunOpts != null ? stunOpts.CustomStunHost : "";
                int stunPort = stunOpts != null ? stunOpts.CustomStunPort : 0;
                Task _stunTask = Task.Run(async delegate
                {
                    try
                    {
                        Task<BoBoNatTraversal.StunDualProbeResult> stunTask = null;
                        try
                        {
                            var servers = BoBoNatTraversal.BuildDefaultStunServers(stunHost, stunPort);
                            stunTask = clientStunSession.ProbeDualStackAsync(uc.Client, servers, 900, ct);
                        }
                        catch { }
                        Task<BoBoNatTraversal.RouterPortMappingLease> mapTask = BoBoNatTraversal.TryAcquireRouterPortLeaseAsync(localUdpPort, 750, ct);
                        BoBoNatTraversal.StunDualProbeResult stunRes = null;
                        BoBoNatTraversal.RouterPortMappingLease routerLease = null;
                        if (stunTask != null)
                        {
                            try { stunRes = await stunTask.ConfigureAwait(false); } catch { }
                        }
                        try { routerLease = await mapTask.ConfigureAwait(false); } catch { }
                        bool keepLease = false;
                        if (routerLease != null)
                        {
                            lock (udpGate)
                            {
                                if (!ct.IsCancellationRequested && connected && udpClient == uc)
                                {
                                    if (activeRouterLease != null)
                                    {
                                        try { activeRouterLease.Dispose(); } catch { }
                                    }
                                    activeRouterLease = routerLease;
                                    keepLease = true;
                                }
                            }
                            if (!keepLease)
                            {
                                try { routerLease.Dispose(); } catch { }
                            }
                        }
                        if (ct.IsCancellationRequested || !connected || udpClient != uc) return;

                        var mappedList = stunRes != null ? stunRes.AllEndpoints() : new List<IPEndPoint>();
                        if (routerLease != null && routerLease.ExternalEndPoint != null) mappedList.Add(routerLease.ExternalEndPoint);
                        bool symHint = stunRes != null && stunRes.IsSymmetricIPv4;
                        int delta = stunRes != null ? stunRes.ObservedPortDelta : 0;
                        List<BoBoUdpCandidate> localCands = BoBoNatTraversal.GatherLocalCandidates(localUdpPort, mappedList, symHint, delta);
                        if (localCands.Count > 0)
                        {
                            var candMsg = new Dictionary<string, object>();
                            candMsg["type"] = "UDP_CANDIDATES";
                            candMsg["candidates"] = BoBoNatTraversal.ToProtocolObjects(localCands);
                            SendJsonFireAndForget(candMsg);
                        }
                    }
                    catch { }
                });

                var rxThread = new Thread(() => UdpReceiveLoopThread(uc, crypto, assembler, ct))
                {
                    IsBackground = true,
                    Name = "BoBoClientUdpRx",
                    Priority = ThreadPriority.AboveNormal
                };
                rxThread.Start();
                Task _hb = Task.Run(delegate { return UdpHeartbeatAndFeedbackLoopAsync(uc, crypto, assembler, fallbackEp, ct); });
            }
            catch { }
        }

        private void UdpReceiveLoopThread(UdpClient uc, BoBoUdpCrypto crypto, BoBoUdpVideoAssembler assembler, CancellationToken ct)
        {
            Socket sock = uc.Client;
            byte[] buf = new byte[2048];
            EndPoint remoteEp = new IPEndPoint(
                sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            var readyFrames = new List<BoBoReassembledVideoFrame>(4);
            var nackRequests = new List<BoBoUdpNackRequest>(16);
            long lastRecoveryPollTicks = 0;

            try
            {
                if (!sock.IsBound)
                {
                    sock.Bind(new IPEndPoint(
                        sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                }
            }
            catch { }

            while (!ct.IsCancellationRequested && connected)
            {
                try
                {
                    int pollUs = (udpActive && (assembler.HasPendingBuckets || assembler.IsAwaitingKeyframe)) ? 4000 : 80000;
                    if (!sock.Poll(pollUs, SelectMode.SelectRead))
                    {
                        if (udpActive && (assembler.HasPendingBuckets || assembler.IsAwaitingKeyframe))
                        {
                            long pollNow = Stopwatch.GetTimestamp();
                            lastRecoveryPollTicks = pollNow;
                            bool pollPli;
                            assembler.PollRecovery(pollNow, latestRttMs, nackRequests, out pollPli);
                            if (nackRequests.Count > 0) SendUdpNacks(uc, crypto, nackRequests);
                            if (pollPli) SendUdpFeedback(uc, crypto, assembler, true);
                        }
                        continue;
                    }

                    int n = sock.ReceiveFrom(buf, 0, buf.Length, SocketFlags.None, ref remoteEp);
                    if (clientStunSession.TryHandlePacket(buf, n)) continue;
                    if (n < crypto.ActiveOverheadBytes) continue;

                    long rxNow = Stopwatch.GetTimestamp();
                    IPEndPoint pktSender = BoBoNatTraversal.NormalizeRemoteEndPoint(remoteEp);
                    if (crypto.TryVerifyStatelessPunchProbe(buf, n))
                    {
                        if (pktSender != null && !IPAddress.IsLoopback(pktSender.Address) &&
                            BoBoNatTraversal.IsAllowedRemoteCandidateIp(pktSender.Address))
                        {
                            string dynKind = pktSender.AddressFamily == AddressFamily.InterNetworkV6
                                ? BoBoUdpCandidate.KindDirectIPv6
                                : BoBoUdpCandidate.KindDirectIPv4;
                            UdpProbeCandidate dynCand = RegisterUdpCandidate(dynKind, pktSender);
                            if (dynCand != null)
                            {
                                SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, dynCand.HelloPayload, pktSender);
                            }
                        }
                        continue;
                    }

                    BoBoUdpPacketType ptype;
                    uint seq;
                    byte[] plain;
                    if (!crypto.TryOpen(buf, n, out ptype, out seq, out plain)) continue;

                    Interlocked.Exchange(ref lastRxTicks, rxNow);

                    if (ptype == BoBoUdpPacketType.Ping)
                    {
                        if (pktSender != null && !IPAddress.IsLoopback(pktSender.Address) &&
                            BoBoNatTraversal.IsAllowedRemoteCandidateIp(pktSender.Address))
                        {
                            string dynKind = pktSender.AddressFamily == AddressFamily.InterNetworkV6
                                ? BoBoUdpCandidate.KindDirectIPv6
                                : BoBoUdpCandidate.KindDirectIPv4;
                            UdpProbeCandidate dynCand = RegisterUdpCandidate(dynKind, pktSender);
                            if (dynCand != null)
                            {
                                SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, dynCand.HelloPayload, pktSender);
                            }
                        }
                        if (plain != null && plain.Length >= 8)
                        {
                            IPEndPoint pongEp = pktSender ?? (remoteEp as IPEndPoint);
                            if (pongEp != null) SendUdpRaw(uc, crypto, BoBoUdpPacketType.Pong, plain, pongEp);
                        }
                        continue;
                    }

                    if (ptype == BoBoUdpPacketType.HelloAck)
                    {
                        if (rxNow >= Interlocked.Read(ref udpFallbackSuppressUntilTicks))
                        {
                            Interlocked.Exchange(ref lastUdpRxTicks, rxNow);
                            UdpProbeCandidate matchedCand = null;
                            if (plain != null && plain.Length >= 14 &&
                                plain[0] == 0x42 && plain[1] == 0x4F && plain[2] == 0x37 && plain[3] == 0x34)
                            {
                                uint pid = BoBoUdpCrypto.ReadUInt32LE(plain, 4);
                                udpProbeTable.TryGetValue(pid, out matchedCand);
                            }

                            if (matchedCand != null)
                            {
                                IPEndPoint effectiveEp = matchedCand.EndPoint;
                                if (!string.Equals(matchedCand.Kind, BoBoUdpCandidate.KindTsNet, StringComparison.OrdinalIgnoreCase) &&
                                    pktSender != null && !IPAddress.IsLoopback(pktSender.Address) &&
                                    BoBoNatTraversal.IsAllowedRemoteCandidateIp(pktSender.Address))
                                {
                                    effectiveEp = pktSender;
                                }
                                bool shouldPromote = !udpActive || matchedCand.Rank > activeUdpPathRank ||
                                    (matchedCand.Rank == activeUdpPathRank && udpRemoteEp != null && effectiveEp.Equals(udpRemoteEp));
                                if (shouldPromote)
                                {
                                    bool wasUdpActive = udpActive;
                                    bool switchedEp = !udpActive || udpRemoteEp == null || !effectiveEp.Equals(udpRemoteEp);
                                    udpRemoteEp = effectiveEp;
                                    activeUdpPathKind = matchedCand.Kind;
                                    activeUdpPathRank = matchedCand.Rank;
                                    udpActive = true;
                                    if (switchedEp)
                                    {
                                        latestRttMs = 0.0;
                                        prevUdpRttMs = 0.0;
                                        latestJitterMs = 0.0;
                                        if (!wasUdpActive)
                                        {
                                            assembler.ResetForRecovery(rxNow);
                                            lastRecoveryPollTicks = rxNow;
                                        }
                                        else
                                        {
                                            assembler.ResetLossBaseline(rxNow);
                                        }
                                        SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, matchedCand.ForceHelloPayload, effectiveEp);
                                        byte[] immediatePing = new byte[8];
                                        BoBoUdpCrypto.WriteInt64LE(immediatePing, 0, Stopwatch.GetTimestamp());
                                        SendUdpRaw(uc, crypto, BoBoUdpPacketType.Ping, immediatePing, effectiveEp);
                                    }
                                }
                            }
                            else
                            {
                                if (!udpActive)
                                {
                                    assembler.ResetForRecovery(rxNow);
                                    lastRecoveryPollTicks = rxNow;
                                }
                                udpActive = true;
                            }
                        }
                        continue;
                    }

                    // Do not let straggling VideoShard / Pong packets silently re-activate UDP after TRANSPORT_FALLBACK!
                    if (!udpActive) continue;

                    IPEndPoint curActiveEp = udpRemoteEp;
                    bool fromActivePath = string.Equals(activeUdpPathKind, BoBoUdpCandidate.KindTsNet, StringComparison.OrdinalIgnoreCase)
                        ? (pktSender != null && IPAddress.IsLoopback(pktSender.Address))
                        : (curActiveEp == null || (pktSender != null && pktSender.Equals(curActiveEp)));
                    if (!fromActivePath) continue;

                    Interlocked.Exchange(ref lastUdpRxTicks, rxNow);
                    assembler.RecordReceivedUdpSeq(seq);

                    if (ptype == BoBoUdpPacketType.VideoShard)
                    {
                        readyFrames.Clear();
                        bool needPli;
                        assembler.ProcessShardBatch(plain, latestRttMs, readyFrames, out needPli);
                        bool decodeQueueNeedPli = false;
                        for (int i = 0; i < readyFrames.Count; i++)
                        {
                            BoBoReassembledVideoFrame doneFrame = readyFrames[i];
                            int vCodec = doneFrame.IsHevc ? BoBoCoreInterop.VideoCodecHevc : BoBoCoreInterop.VideoCodecH264;
                            activeVideoCodecId = vCodec;
                            Interlocked.Increment(ref videoFramesReceived);
                            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                            if (nc != null)
                            {
                                int pushRc = nc.PushVideoEx(doneFrame.H264Data, doneFrame.H264Data.Length, doneFrame.IsKeyframe, doneFrame.Width, doneFrame.Height, vCodec);
                                if (pushRc == BoBoCoreInterop.PushVideoNeedKeyframe)
                                {
                                    decodeQueueNeedPli = true;
                                }
                            }
                            Action<byte[], bool, int, int> rawEv = RawVideoFrameReceived;
                            if (rawEv != null) { try { rawEv(doneFrame.H264Data, doneFrame.IsKeyframe, doneFrame.Width, doneFrame.Height); } catch { } }
                        }
                        if (decodeQueueNeedPli && ShouldEmitDecodeQueueIdrRequest())
                        {
                            needPli = true;
                            SendRequestKeyframe();
                        }
                        bool pollPli = false;
                        if (assembler.HasPendingBuckets)
                        {
                            bool isParityShard = plain != null && plain.Length > 12 && (plain[12] & BoBoUdpFec.FlagParity) != 0;
                            double sincePollMs = lastRecoveryPollTicks == 0
                                ? 100.0
                                : ((rxNow - lastRecoveryPollTicks) * 1000.0 / Stopwatch.Frequency);
                            if (isParityShard || sincePollMs >= 6.0)
                            {
                                lastRecoveryPollTicks = rxNow;
                                assembler.PollRecovery(rxNow, latestRttMs, nackRequests, out pollPli);
                                if (nackRequests.Count > 0) SendUdpNacks(uc, crypto, nackRequests);
                            }
                        }
                        if (needPli || pollPli)
                        {
                            SendUdpFeedback(uc, crypto, assembler, true);
                        }
                        continue;
                    }

                    if (ptype == BoBoUdpPacketType.Audio || ptype == BoBoUdpPacketType.AudioAac)
                    {
                        int sr, ch, samples;
                        byte[] audioBytes;
                        if (BoBoProtocol.ParseAudioPayload(plain, out sr, out ch, out samples, out audioBytes))
                        {
                            int codecId = (ptype == BoBoUdpPacketType.AudioAac)
                                ? BoBoCoreInterop.AudioCodecAac
                                : BoBoCoreInterop.AudioCodecAdpcm;
                            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                            if (nc != null) nc.PushAudioEx(audioBytes, audioBytes.Length, sr, ch, samples, codecId);
                        }
                        continue;
                    }

                    if (ptype == BoBoUdpPacketType.Pong && plain != null && plain.Length >= 8)
                    {
                        long sentTicks = BoBoUdpCrypto.ReadInt64LE(plain, 0);
                        if (sentTicks > 0)
                        {
                            double rtt = (Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency;
                            if (rtt >= 0 && rtt < 10000)
                            {
                                if (prevUdpRttMs > 0.1)
                                {
                                    double delta = Math.Abs(rtt - prevUdpRttMs);
                                    latestJitterMs = latestJitterMs * 0.75 + delta * 0.25;
                                }
                                prevUdpRttMs = rtt;
                                latestRttMs = rtt;
                                Action<double> rttEv = RttUpdated;
                                if (rttEv != null) { try { rttEv(rtt); } catch { } }
                            }
                        }
                        continue;
                    }

                    if (ptype == BoBoUdpPacketType.CursorShape && plain != null && plain.Length >= 5)
                    {
                        int cSeq = unchecked((int)BoBoUdpCrypto.ReadUInt32LE(plain, 0));
                        int prev = Volatile.Read(ref lastCursorSeq);
                        if (unchecked(cSeq - prev) > 0)
                        {
                            Volatile.Write(ref lastCursorSeq, cSeq);
                            ApplyRemoteCursorShape(plain, 4);
                        }
                        continue;
                    }
                }
                catch
                {
                    if (ct.IsCancellationRequested || !connected) break;
                }
            }
        }

        private async Task UdpHeartbeatAndFeedbackLoopAsync(
            UdpClient uc,
            BoBoUdpCrypto crypto,
            BoBoUdpVideoAssembler assembler,
            IPEndPoint fallbackEp,
            CancellationToken ct)
        {
            byte[] legacyHello = new byte[] { 0x42, 0x4F, 0x36, 0x33 };
            // 1. Rapid continuous bidirectional ICE check (~3.4s) until Direct LAN/IPv6/IPv4/STUN (Rank >= 40) activates
            for (int attempt = 0; attempt < 75 && !ct.IsCancellationRequested && connected; attempt++)
            {
                if (udpActive && activeUdpPathRank >= 40 && !udpCandidatesUpdated) break;
                udpCandidatesUpdated = false;
                bool sentAny = false;
                int minRank = udpActive ? activeUdpPathRank + 1 : 0;
                foreach (UdpProbeCandidate cand in udpProbeTable.Values)
                {
                    if (cand != null && cand.EndPoint != null && cand.Rank >= minRank)
                    {
                        SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, cand.HelloPayload, cand.EndPoint);
                        sentAny = true;
                    }
                }
                if (!udpActive && !sentAny && fallbackEp != null)
                {
                    SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, legacyHello, fallbackEp);
                }
                if (udpActive && (attempt % 4) == 0)
                {
                    IPEndPoint activeEp = udpRemoteEp ?? fallbackEp;
                    if (activeEp != null)
                    {
                        byte[] pingBuf = new byte[8];
                        BoBoUdpCrypto.WriteInt64LE(pingBuf, 0, Stopwatch.GetTimestamp());
                        SendUdpRaw(uc, crypto, BoBoUdpPacketType.Ping, pingBuf, activeEp);
                        assembler.RefreshLossWindow(Stopwatch.GetTimestamp());
                        latestPacketLossPct = assembler.LatestLossPct;
                        SendUdpFeedback(uc, crypto, assembler, false);
                    }
                }
                try { await Task.Delay(45, ct).ConfigureAwait(false); } catch { return; }
            }

            // 2. Steady-state 200ms RTT ping + loss/ABR feedback + opportunistic direct-path upgrade & UDP-silence fallback
            int tick = 0;
            int burstRemaining = 0;
            while (!ct.IsCancellationRequested && connected)
            {
                try
                {
                    if (udpCandidatesUpdated)
                    {
                        udpCandidatesUpdated = false;
                        if (activeUdpPathRank < 40) burstRemaining = 25;
                    }
                    if (udpActive)
                    {
                        long rxTicks = Interlocked.Read(ref lastUdpRxTicks);
                        double silenceMs = rxTicks == 0 ? 0.0 : ((Stopwatch.GetTimestamp() - rxTicks) * 1000.0 / Stopwatch.Frequency);
                        if (silenceMs > 3500.0)
                        {
                            // UDP path dropped (e.g. campus NAT rebind/block): gracefully fall back to TCP+TLS without disconnecting!
                            Interlocked.Exchange(ref udpFallbackSuppressUntilTicks,
                                Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.5));
                            udpActive = false;
                            activeUdpPathKind = "";
                            activeUdpPathRank = 0;
                            latestPacketLossPct = 0.0;
                            latestJitterMs = 0.0;
                            prevUdpRttMs = 0.0;
                            if (latestTlsRttMs > 0.1) latestRttMs = latestTlsRttMs;
                            tick = 1;
                            var fb = new Dictionary<string, object>();
                            fb["type"] = "TRANSPORT_FALLBACK";
                            SendJsonFireAndForget(fb);
                        }
                        else
                        {
                            bool pingTurn = (burstRemaining == 0) || ((tick % 4) == 0);
                            if (pingTurn)
                            {
                                IPEndPoint activeEp = udpRemoteEp ?? fallbackEp;
                                if (activeEp != null)
                                {
                                    byte[] pingBuf = new byte[8];
                                    BoBoUdpCrypto.WriteInt64LE(pingBuf, 0, Stopwatch.GetTimestamp());
                                    SendUdpRaw(uc, crypto, BoBoUdpPacketType.Ping, pingBuf, activeEp);

                                    assembler.RefreshLossWindow(Stopwatch.GetTimestamp());
                                    latestPacketLossPct = assembler.LatestLossPct;
                                    SendUdpFeedback(uc, crypto, assembler, false);
                                }
                            }

                            // While connected over relay/tsnet (<40 rank), race higher-rank direct candidates (burst or every ~1.4s)
                            if (activeUdpPathRank < 40 && (burstRemaining > 0 || (tick % 7) == 0))
                            {
                                foreach (UdpProbeCandidate cand in udpProbeTable.Values)
                                {
                                    if (cand != null && cand.Rank > activeUdpPathRank && cand.EndPoint != null)
                                    {
                                        SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, cand.HelloPayload, cand.EndPoint);
                                    }
                                }
                            }
                        }
                    }
                    else if (burstRemaining > 0 || (tick % 10) == 0)
                    {
                        bool sentAny = false;
                        foreach (UdpProbeCandidate cand in udpProbeTable.Values)
                        {
                            if (cand != null && cand.EndPoint != null)
                            {
                                SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, cand.HelloPayload, cand.EndPoint);
                                sentAny = true;
                            }
                        }
                        if (!sentAny && fallbackEp != null)
                        {
                            SendUdpRaw(uc, crypto, BoBoUdpPacketType.Hello, legacyHello, fallbackEp);
                        }
                    }
                    tick++;
                    int delayMs = burstRemaining > 0 ? 50 : 200;
                    if (burstRemaining > 0) burstRemaining--;
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch { break; }
            }
        }

        private void SendUdpNacks(UdpClient uc, BoBoUdpCrypto crypto, List<BoBoUdpNackRequest> requests)
        {
            IPEndPoint ep = udpRemoteEp;
            if (uc == null || crypto == null || ep == null || requests == null || requests.Count == 0) return;
            byte[] payload = BoBoUdpNackCodec.Encode(requests);
            if (payload != null) SendUdpRaw(uc, crypto, BoBoUdpPacketType.Nack, payload, ep);
        }

        private void SendUdpFeedback(UdpClient uc, BoBoUdpCrypto crypto, BoBoUdpVideoAssembler assembler, bool requestPli)
        {
            IPEndPoint ep = udpRemoteEp;
            if (uc == null || crypto == null || ep == null) return;
            double loss = assembler != null ? assembler.LatestLossPct : 0.0;
            latestPacketLossPct = loss;
            byte[] fb = new byte[11];
            ushort lossX100 = (ushort)Math.Max(0, Math.Min(10000, (int)Math.Round(loss * 100.0)));
            ushort rttX10 = (ushort)Math.Max(0, Math.Min(65000, (int)Math.Round(latestRttMs * 10.0)));
            ushort jitterX10 = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(latestJitterMs * 10.0)));
            BoBoUdpCrypto.WriteUInt16LE(fb, 0, lossX100);
            BoBoUdpCrypto.WriteUInt16LE(fb, 2, rttX10);
            BoBoUdpCrypto.WriteUInt16LE(fb, 4, jitterX10);
            BoBoUdpCrypto.WriteUInt32LE(fb, 6, assembler != null ? assembler.LastDeliveredFrameId : 0u);
            fb[10] = (byte)(requestPli ? 1 : 0);
            SendUdpRaw(uc, crypto, BoBoUdpPacketType.Feedback, fb, ep);
        }

        private void SendUdpRaw(UdpClient uc, BoBoUdpCrypto crypto, BoBoUdpPacketType type, byte[] plain, IPEndPoint ep)
        {
            if (uc == null || crypto == null || ep == null) return;
            lock (udpGate)
            {
                if (udpClient != uc) return;
                // Seal inside udpGate so UDP sequence numbers match exact wire transmission order
                byte[] sealedPkt = crypto.Seal(type, plain, 0, plain != null ? plain.Length : 0);
                if (sealedPkt == null) return;
                try { BoBoNatTraversal.SendDualStack(uc.Client, sealedPkt, sealedPkt.Length, ep); } catch { }
            }
        }

        private void DisposeUdpLocked()
        {
            udpActive = false;
            udpNackNegotiated = false;
            activeUdpPathKind = "";
            activeUdpPathRank = 0;
            udpCandidatesUpdated = false;
            udpProbeTable.Clear();
            int oldBridge = Interlocked.Exchange(ref embeddedUdpBridgePort, 0);
            if (oldBridge > 0)
            {
                Task _cl = BoBoEmbeddedTailnet.Default.CloseUdpBridgeAsync(oldBridge);
            }
            hasDeferredUdpAbsMove = false;
            deferredUdpRelDx = 0;
            deferredUdpRelDy = 0;
            hasRedundantAbsMove = false;
            udpDiscreteRedundantDueTicks = 0;
            latestJitterMs = 0.0;
            prevUdpRttMs = 0.0;
            latestTlsRttMs = 0.0;
            latestPacketLossPct = 0.0;
            Interlocked.Exchange(ref lastDecodeQueueIdrReqTicks, 0);
            if (activeRouterLease != null) { try { activeRouterLease.Dispose(); } catch { } activeRouterLease = null; }
            if (preBoundUdp != null) { try { preBoundUdp.Close(); } catch { } preBoundUdp = null; }
            if (udpClient != null) { try { udpClient.Close(); } catch { } udpClient = null; }
            if (udpCrypto != null) { try { udpCrypto.Dispose(); } catch { } udpCrypto = null; }
            udpRemoteEp = null;
            udpAssembler = null;
            udpInputCodec = null;
        }

        private void ScheduleUdpMoveTailFlush(UdpClient expectedUc)
        {
            if (expectedUc == null || Interlocked.CompareExchange(ref udpMoveFlushScheduled, 1, 0) != 0) return;
            Task.Run(async delegate
            {
                try
                {
                    try { await Task.Delay(5).ConfigureAwait(false); } catch { }
                    if (!connected || !udpActive) return;

                    bool sendAbs = false;
                    BoBoInputPacket absPkt = new BoBoInputPacket();
                    int relDx = 0;
                    int relDy = 0;
                    UdpClient uc;
                    BoBoUdpCrypto cr;
                    BoBoUdpInputCodec codec;
                    IPEndPoint ep;
                    lock (udpGate)
                    {
                        if (udpClient != expectedUc) return;
                        uc = udpClient;
                        cr = udpCrypto;
                        codec = udpInputCodec;
                        ep = udpRemoteEp;
                        if (hasDeferredUdpAbsMove)
                        {
                            sendAbs = true;
                            absPkt = deferredUdpAbsMove;
                            hasDeferredUdpAbsMove = false;
                        }
                        if (deferredUdpRelDx != 0 || deferredUdpRelDy != 0)
                        {
                            relDx = deferredUdpRelDx;
                            relDy = deferredUdpRelDy;
                            deferredUdpRelDx = 0;
                            deferredUdpRelDy = 0;
                        }
                    }
                    if ((sendAbs || relDx != 0 || relDy != 0) && uc != null && cr != null && codec != null && ep != null)
                    {
                        short cdx = (short)Math.Max(-32767, Math.Min(32767, relDx));
                        short cdy = (short)Math.Max(-32767, Math.Min(32767, relDy));
                        Interlocked.Exchange(ref lastUdpMoveSentTicks, Stopwatch.GetTimestamp());
                        byte[] p = codec.BuildPacket(sendAbs, absPkt, cdx != 0 || cdy != 0, cdx, cdy, null);
                        SendUdpRaw(uc, cr, BoBoUdpPacketType.Input, p, ep);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref udpMoveFlushScheduled, 0);
                }
            });
        }

        private void ScheduleUdpDiscreteRedundantSend(UdpClient expectedUc)
        {
            if (expectedUc == null || Interlocked.CompareExchange(ref udpDiscreteRedundantScheduled, 1, 0) != 0) return;
            Task.Run(async delegate
            {
                try
                {
                    while (connected && udpActive)
                    {
                        long due;
                        lock (udpGate)
                        {
                            if (udpClient != expectedUc) return;
                            due = udpDiscreteRedundantDueTicks;
                        }
                        if (due == 0) return;

                        double remMs = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                        if (remMs >= 1.0)
                        {
                            int delayMs = Math.Max(1, Math.Min(15, (int)Math.Ceiling(remMs)));
                            await Task.Delay(delayMs).ConfigureAwait(false);
                            continue;
                        }

                        bool sendAbs = false;
                        BoBoInputPacket absPkt = new BoBoInputPacket();
                        UdpClient uc;
                        BoBoUdpCrypto cr;
                        BoBoUdpInputCodec codec;
                        IPEndPoint ep;
                        bool readyToSend;
                        lock (udpGate)
                        {
                            if (udpClient != expectedUc) return;
                            if (udpDiscreteRedundantDueTicks > due) continue;

                            uc = udpClient;
                            cr = udpCrypto;
                            codec = udpInputCodec;
                            ep = udpRemoteEp;
                            if (hasRedundantAbsMove)
                            {
                                sendAbs = true;
                                absPkt = redundantAbsMove;
                                hasRedundantAbsMove = false;
                            }
                            udpDiscreteRedundantDueTicks = 0;
                            readyToSend = (uc != null && cr != null && codec != null && ep != null);
                        }

                        if (readyToSend && connected && udpActive)
                        {
                            byte[] redPkt = codec.BuildPacket(sendAbs, absPkt, false, 0, 0, null);
                            SendUdpRaw(uc, cr, BoBoUdpPacketType.Input, redPkt, ep);
                        }
                        return;
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref udpDiscreteRedundantScheduled, 0);
                    long pendingDue = 0;
                    UdpClient curUc = null;
                    lock (udpGate)
                    {
                        curUc = udpClient;
                        pendingDue = udpDiscreteRedundantDueTicks;
                    }
                    if (pendingDue != 0 && curUc == expectedUc && connected && udpActive)
                    {
                        ScheduleUdpDiscreteRedundantSend(expectedUc);
                    }
                }
            });
        }

        internal void SendInputPacket(BoBoInputPacket pkt)
        {
            if (!connected || ssl == null) return;

            // Zero-HoL encrypted UDP fast-path for mouse & keyboard input!
            if (udpActive)
            {
                UdpClient uc;
                BoBoUdpCrypto cr;
                BoBoUdpInputCodec codec;
                IPEndPoint ep;
                lock (udpGate)
                {
                    uc = udpClient;
                    cr = udpCrypto;
                    codec = udpInputCodec;
                    ep = udpRemoteEp;
                }
                if (uc != null && cr != null && codec != null && ep != null)
                {
                    if (pkt.Kind == 1)
                    {
                        long now = Stopwatch.GetTimestamp();
                        long prev = Interlocked.Read(ref lastUdpMoveSentTicks);
                        if (prev != 0 && ((now - prev) * 1000.0 / Stopwatch.Frequency) < 4.0)
                        {
                            lock (udpGate)
                            {
                                hasRedundantAbsMove = false;
                                hasDeferredUdpAbsMove = true;
                                deferredUdpAbsMove = pkt;
                            }
                            ScheduleUdpMoveTailFlush(uc);
                            return;
                        }
                        lock (udpGate) { hasRedundantAbsMove = false; hasDeferredUdpAbsMove = false; }
                        Interlocked.Exchange(ref lastUdpMoveSentTicks, now);
                        byte[] p = codec.BuildPacket(true, pkt, false, 0, 0, null);
                        SendUdpRaw(uc, cr, BoBoUdpPacketType.Input, p, ep);
                        return;
                    }
                    if (pkt.Kind == 2)
                    {
                        if (pkt.A == 0 && pkt.B == 0) return;
                        long now = Stopwatch.GetTimestamp();
                        long prev = Interlocked.Read(ref lastUdpMoveSentTicks);
                        if (prev != 0 && ((now - prev) * 1000.0 / Stopwatch.Frequency) < 4.0)
                        {
                            lock (udpGate)
                            {
                                hasRedundantAbsMove = false;
                                deferredUdpRelDx += pkt.A;
                                deferredUdpRelDy += pkt.B;
                            }
                            ScheduleUdpMoveTailFlush(uc);
                            return;
                        }
                        int totDx, totDy;
                        lock (udpGate)
                        {
                            hasRedundantAbsMove = false;
                            totDx = deferredUdpRelDx + pkt.A;
                            totDy = deferredUdpRelDy + pkt.B;
                            deferredUdpRelDx = 0;
                            deferredUdpRelDy = 0;
                        }
                        short cdx = (short)Math.Max(-32767, Math.Min(32767, totDx));
                        short cdy = (short)Math.Max(-32767, Math.Min(32767, totDy));
                        Interlocked.Exchange(ref lastUdpMoveSentTicks, now);
                        byte[] p = codec.BuildPacket(false, new BoBoInputPacket(), true, cdx, cdy, null);
                        SendUdpRaw(uc, cr, BoBoUdpPacketType.Input, p, ep);
                        return;
                    }
                    // Discrete input (clicks, keys, wheel, touch, releaseAll): flush any deferred move atomically
                    // in the exact same UDP packet so clicks NEVER land off-target!
                    bool flushAbs = false;
                    BoBoInputPacket absToFlush = new BoBoInputPacket();
                    int flushRelDx = 0, flushRelDy = 0;
                    lock (udpGate)
                    {
                        if (hasDeferredUdpAbsMove)
                        {
                            flushAbs = true;
                            absToFlush = deferredUdpAbsMove;
                            hasDeferredUdpAbsMove = false;
                        }
                        if (deferredUdpRelDx != 0 || deferredUdpRelDy != 0)
                        {
                            flushRelDx = deferredUdpRelDx;
                            flushRelDy = deferredUdpRelDy;
                            deferredUdpRelDx = 0;
                            deferredUdpRelDy = 0;
                        }
                    }
                    short fdx = (short)Math.Max(-32767, Math.Min(32767, flushRelDx));
                    short fdy = (short)Math.Max(-32767, Math.Min(32767, flushRelDy));
                    byte[] discPkt = codec.BuildPacket(flushAbs, absToFlush, fdx != 0 || fdy != 0, fdx, fdy, pkt);
                    SendUdpRaw(uc, cr, BoBoUdpPacketType.Input, discPkt, ep);
                    if (latestPacketLossPct >= 0.3 || latestRttMs >= 25.0)
                    {
                        // Spaced 10ms redundant copy for burst loss on Wi-Fi / WAN links:
                        // carries seq-deduplicated discrete events and only replays abs if no newer move intervened.
                        long dueTicks = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10L / 1000L);
                        lock (udpGate)
                        {
                            if (flushAbs)
                            {
                                hasRedundantAbsMove = true;
                                redundantAbsMove = absToFlush;
                            }
                            if (dueTicks > udpDiscreteRedundantDueTicks)
                                udpDiscreteRedundantDueTicks = dueTicks;
                        }
                        ScheduleUdpDiscreteRedundantSend(uc);
                    }
                    return;
                }
            }

            // TCP fallback: Coalesce high-frequency mouse moves while an earlier TLS write is in flight so 1000Hz mice
            // never accumulate multi-second lag over WAN/Tailscale, while preserving strict ordering before
            // clicks, keys, wheel events, or control commands.
            if (pkt.Kind == 1)
            {
                bool startLoop = false;
                lock (sendLoopGate)
                {
                    if (ssl == null) return;
                    if (pendingMoveKind == 2)
                    {
                        FlushPendingMoveLocked();
                    }
                    pendingMoveKind = 1;
                    pendingMoveAbs = pkt;
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
                return;
            }
            if (pkt.Kind == 2)
            {
                if (pkt.A == 0 && pkt.B == 0) return;
                bool startLoop = false;
                lock (sendLoopGate)
                {
                    if (ssl == null) return;
                    if (pendingMoveKind == 1)
                    {
                        FlushPendingMoveLocked();
                    }
                    if (pendingMoveKind == 2)
                    {
                        pendingMoveDx += pkt.A;
                        pendingMoveDy += pkt.B;
                    }
                    else
                    {
                        pendingMoveKind = 2;
                        pendingMoveDx = pkt.A;
                        pendingMoveDy = pkt.B;
                    }
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
                return;
            }
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

        internal void SendReconfigure(int monitorIndex, int width, int height, int fps, int bitrateKbps, bool autoBitrate = true)
        {
            activeBitrateKbps = bitrateKbps;
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "RECONFIGURE";
            cmd["monitorIndex"] = monitorIndex;
            cmd["width"] = width;
            cmd["height"] = height;
            cmd["fps"] = fps;
            cmd["bitrateKbps"] = bitrateKbps;
            cmd["autoBitrate"] = autoBitrate;
            SendJsonFireAndForget(cmd);
        }

        private void ApplyRemoteCursorShape(byte[] payload, int offset)
        {
            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
            if (nc == null || payload == null || offset >= payload.Length) return;
            bool hasSize = payload.Length - offset >= 3;
            nc.SetRemoteCursorInfo(payload[offset], hasSize ? payload[offset + 1] : 0, hasSize ? payload[offset + 2] : 0);
        }

        internal void SendSetCursorMode(bool hostComposite)
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "SET_CURSOR_MODE";
            cmd["composite"] = hostComposite;
            SendJsonFireAndForget(cmd);
        }

        private bool ShouldEmitDecodeQueueIdrRequest()
        {
            long now = Stopwatch.GetTimestamp();
            long prev = Interlocked.Read(ref lastDecodeQueueIdrReqTicks);
            long minInterval = Stopwatch.Frequency / 4; // 250ms guard to avoid IDR floods
            if (prev != 0 && (now - prev) < minInterval) return false;
            if (Interlocked.CompareExchange(ref lastDecodeQueueIdrReqTicks, now, prev) != prev) return false;
            Interlocked.Increment(ref decodeQueueIdrRequests);
            return true;
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

        internal void SendSetSuperDisplay(bool enable, int clientWidth, int clientHeight, bool fullscreen = false, int requestId = 0)
        {
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "SET_SUPER_DISPLAY";
            cmd["enable"] = enable;
            cmd["clientWidth"] = clientWidth;
            cmd["clientHeight"] = clientHeight;
            if (SupportsSuperDisplayViewport)
            {
                cmd["fullscreen"] = fullscreen;
                cmd["displayRequestId"] = requestId;
            }
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

        internal void SendSas()
        {
            if (!connected) return;
            var cmd = new Dictionary<string, object>();
            cmd["type"] = "SEND_SAS";
            SendJsonFireAndForget(cmd);
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
            CancellationTokenRegistration reg = default(CancellationTokenRegistration);
            if (ct.CanBeCanceled)
            {
                reg = ct.Register(delegate { tcs.TrySetCanceled(); });
            }
            EnqueueSend(BoBoPacketType.ControlJson, 0, SerializeJsonPayload(obj), tcs, reg);
            return tcs.Task;
        }

        private static byte[] SerializeJsonPayload(Dictionary<string, object> obj)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            ser.MaxJsonLength = BoBoProtocol.MaxControlPayloadBytes;
            return Encoding.UTF8.GetBytes(ser.Serialize(obj));
        }

        private void FlushPendingMoveLocked()
        {
            while (pendingMoveKind != 0)
            {
                PendingSend moveItem = TakePendingMoveLocked();
                if (moveItem != null)
                {
                    sendQueue.Enqueue(moveItem);
                }
            }
        }

        private PendingSend TakePendingMoveLocked()
        {
            if (pendingMoveKind == 1)
            {
                BoBoInputPacket absPkt = pendingMoveAbs;
                pendingMoveKind = 0;
                return new PendingSend
                {
                    Type = BoBoPacketType.InputEvent,
                    Flags = 0,
                    Payload = BoBoProtocol.BuildInputPayload(absPkt),
                    Completion = null
                };
            }
            if (pendingMoveKind == 2)
            {
                int dx = pendingMoveDx;
                int dy = pendingMoveDy;
                if (dx == 0 && dy == 0)
                {
                    pendingMoveKind = 0;
                    return null;
                }
                int emitX = dx > 32767 ? 32767 : (dx < -32767 ? -32767 : dx);
                int emitY = dy > 32767 ? 32767 : (dy < -32767 ? -32767 : dy);
                pendingMoveDx -= emitX;
                pendingMoveDy -= emitY;
                if (pendingMoveDx == 0 && pendingMoveDy == 0)
                {
                    pendingMoveKind = 0;
                }
                var relPkt = new BoBoInputPacket { Kind = 2, Flags = 0, A = (short)emitX, B = (short)emitY };
                return new PendingSend
                {
                    Type = BoBoPacketType.InputEvent,
                    Flags = 0,
                    Payload = BoBoProtocol.BuildInputPayload(relPkt),
                    Completion = null
                };
            }
            return null;
        }

        private void EnqueueSend(BoBoPacketType type, uint flags, byte[] payload, TaskCompletionSource<bool> tcs, CancellationTokenRegistration cancelReg = default(CancellationTokenRegistration))
        {
            bool rejected = false;
            bool startLoop = false;
            lock (sendLoopGate)
            {
                if (ssl == null)
                {
                    rejected = true;
                }
                else
                {
                    FlushPendingMoveLocked();
                    sendQueue.Enqueue(new PendingSend
                    {
                        Type = type,
                        Flags = flags,
                        Payload = payload ?? new byte[0],
                        Completion = tcs,
                        CancelReg = cancelReg
                    });
                    if (!sendLoopRunning)
                    {
                        sendLoopRunning = true;
                        startLoop = true;
                    }
                }
            }
            if (rejected)
            {
                try { cancelReg.Dispose(); } catch { }
                if (tcs != null) tcs.TrySetResult(false);
                return;
            }
            if (startLoop)
            {
                Task.Run(delegate { return DrainSendQueueAsync(); });
            }
        }

        private async Task DrainSendQueueAsync()
        {
            bool cleanExit = false;
            try
            {
                while (true)
                {
                    PendingSend item = null;
                    lock (sendLoopGate)
                    {
                        if (!sendQueue.TryDequeue(out item))
                        {
                            while (item == null && pendingMoveKind != 0)
                            {
                                item = TakePendingMoveLocked();
                            }
                            if (item == null)
                            {
                                sendLoopRunning = false;
                                cleanExit = true;
                                return;
                            }
                        }
                    }

                    SslStream s = ssl;
                    CancellationTokenSource curCts = cts;
                    CancellationToken ct = CancellationToken.None;
                    if (curCts != null)
                    {
                        try { ct = curCts.Token; }
                        catch (ObjectDisposedException) { ct = new CancellationToken(true); }
                    }
                    if (s == null || ct.IsCancellationRequested)
                    {
                        try { item.CancelReg.Dispose(); } catch { }
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
                    finally
                    {
                        try { item.CancelReg.Dispose(); } catch { }
                    }
                }
            }
            finally
            {
                if (!cleanExit)
                {
                    lock (sendLoopGate) { sendLoopRunning = false; }
                }
            }
        }

        private void DrainPendingSendQueueOnClose()
        {
            lock (sendLoopGate)
            {
                pendingMoveKind = 0;
                pendingMoveDx = 0;
                pendingMoveDy = 0;
            }
            PendingSend dropped;
            while (sendQueue.TryDequeue(out dropped))
            {
                try { dropped.CancelReg.Dispose(); } catch { }
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
            routedViaEmbeddedTailnet = false;
            HostCursorComposited = false;
            Interlocked.Exchange(ref lastDecodeQueueIdrReqTicks, 0);
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
            lock (udpGate)
            {
                DisposeUdpLocked();
            }
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
            hello["clientCertDer"] = id.CertDerBase64;
            hello["version"] = AppUpdater.CurrentVersion;
            return BoBoProtocol.WriteJsonAsync(sslStream, hello, ct);
        }

        private static async Task<bool> ConnectWithCancelAsync(TcpClient client, string host, int port, CancellationToken ct)
        {
            if (BoBoEmbeddedTailnet.Default.ShouldRouteHost(host))
            {
                await BoBoEmbeddedTailnet.Default.ConnectTargetOverSocks5Async(client, host, port, ct).ConfigureAwait(false);
                return true;
            }
            Task connectTask = client.ConnectAsync(host, port);
            using (ct.Register(delegate { try { client.Close(); } catch { } }))
            {
                await connectTask.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return false;
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
