using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal sealed class EmbeddedTailnetPeer
    {
        internal string Id = "";
        internal string HostName = "";
        internal string DnsName = "";
        internal string OS = "";
        internal string Ip4 = "";
        internal string Ip6 = "";
        internal bool Online;
        internal bool Active;
        internal string CurAddr = "";
        internal string Relay = "";
        internal string PeerRelay = "";
        internal string LastSeen = "";
        internal readonly List<string> Addrs = new List<string>();

        internal string PreferredIp
        {
            get { return !string.IsNullOrEmpty(Ip4) ? Ip4 : Ip6; }
        }
    }

    internal sealed class EmbeddedTailnetSnapshot
    {
        internal string BackendState = "Stopped";
        internal string AuthUrl = "";
        internal string SelfIp4 = "";
        internal string SelfIp6 = "";
        internal string SelfDnsName = "";
        internal string SelfHostName = "";
        internal string LoginName = "";
        internal string DisplayName = "";
        internal string TailnetName = "";
        internal int ControlPort;
        internal int SocksPort;
        internal bool Listening49810;
        internal bool Listening49811;
        internal string Error = "";
        internal readonly List<EmbeddedTailnetPeer> Peers = new List<EmbeddedTailnetPeer>();
    }

    /// <summary>
    /// 内置免安装用户态 Tailscale 引擎管理（基于嵌入的 pure-Go BoBoTsNet.exe.gz）：
    /// - 无需用户下载官方 Tailscale 客户端、不安装虚拟网卡、不干扰本机已有代理/VPN；
    /// - 通过随会话随机密钥鉴权的本地 HTTP 控制口 + 受限 Tailnet-only SOCKS5 代理互通；
    /// - 自动把 Tailnet 49810 入站连接无感转发给本机 BoBoHostServer，保留完整 TLS 1.2 + RSA-2048 TOFU 端到端加密。
    /// </summary>
    internal sealed class BoBoEmbeddedTailnet : IDisposable
    {
        internal const string EmbeddedResourceName = "DualRemote.BoBoTsNet.exe.gz";

        internal static readonly BoBoEmbeddedTailnet Default = new BoBoEmbeddedTailnet();

        internal event Action<EmbeddedTailnetSnapshot> StateChanged;

        private readonly object gate = new object();
        private Process childProcess;
        private IntPtr jobHandle = IntPtr.Zero;
        private string sessionToken = "";
        private int controlPort;
        private int socksPort;
        private bool disposed;
        private CancellationTokenSource pollCts;
        private Task startTask;
        private EmbeddedTailnetSnapshot latestSnapshot = new EmbeddedTailnetSnapshot();

        private BoBoEmbeddedTailnet() { }

        internal static string TsNetRootDir
        {
            get { return Path.Combine(RecentStore.DirPath, "tsnet"); }
        }

        internal static string StateDir
        {
            get { return Path.Combine(TsNetRootDir, "state"); }
        }

        internal static bool HasSavedState
        {
            get
            {
                try
                {
                    if (!Directory.Exists(StateDir)) return false;
                    string primary = Path.Combine(StateDir, "tailscaled.state");
                    if (File.Exists(primary) && new FileInfo(primary).Length > 16) return true;
                    string[] files = Directory.GetFiles(StateDir, "*.state", SearchOption.AllDirectories);
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (new FileInfo(files[i]).Length > 16) return true;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static bool IsEmbeddedResourceAvailable
        {
            get
            {
                try
                {
                    return Assembly.GetExecutingAssembly().GetManifestResourceInfo(EmbeddedResourceName) != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    if (disposed || childProcess == null || controlPort <= 0 || socksPort <= 0) return false;
                    try { return !childProcess.HasExited; }
                    catch { return false; }
                }
            }
        }

        internal int ControlPort { get { lock (gate) { return controlPort; } } }
        internal int SocksPort { get { lock (gate) { return socksPort; } } }
        internal string SessionToken { get { lock (gate) { return sessionToken; } } }

        internal EmbeddedTailnetSnapshot LatestSnapshot
        {
            get
            {
                lock (gate)
                {
                    return latestSnapshot ?? new EmbeddedTailnetSnapshot();
                }
            }
        }

        internal async Task<bool> EnsureStartedAsync(CancellationToken ct)
        {
            if (disposed) return false;
            if (IsRunning) return true;
            if (!IsEmbeddedResourceAvailable)
            {
                PublishErrorSnapshot("当前程序包未内嵌 BoBoTsNet 内核资源。");
                return false;
            }

            Task pending;
            lock (gate)
            {
                if (IsRunning) return true;
                if (startTask == null || startTask.IsCompleted)
                    startTask = Task.Run(delegate { return StartInternalAsync(ct); });
                pending = startTask;
            }
            await pending.ConfigureAwait(false);
            return IsRunning;
        }

        private async Task StartInternalAsync(CancellationToken ct)
        {
            string exePath;
            string extractErr;
            if (!TryExtractBinary(out exePath, out extractErr))
            {
                PublishErrorSnapshot(extractErr);
                return;
            }

            Directory.CreateDirectory(StateDir);
            string tok = GenerateSessionTokenHex();
            string hostName = Environment.MachineName;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--state-dir " + Engines.Quote(StateDir)
                    + " --hostname " + Engines.Quote(hostName)
                    + " --forward-target 127.0.0.1:" + BoBoProtocol.DefaultTcpPort.ToString(CultureInfo.InvariantCulture)
                    + " --parent-pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                    + (HasSavedState ? " --auto-start" : ""),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["BOBO_TSNET_TOKEN"] = tok;

            Process proc = null;
            IntPtr job = IntPtr.Zero;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!proc.Start())
                {
                    PublishErrorSnapshot("无法启动内置跨网引擎进程。");
                    return;
                }

                job = TryAttachKillOnCloseJob(proc);

                // Read first stdout ready line within 10s
                string readyLine = null;
                Task<string> readLineTask = proc.StandardOutput.ReadLineAsync();
                Task timeoutTask = Task.Delay(10000, ct);
                Task finished = await Task.WhenAny(readLineTask, timeoutTask).ConfigureAwait(false);
                if (finished != readLineTask)
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    proc.Dispose();
                    if (job != IntPtr.Zero) CloseHandle(job);
                    PublishErrorSnapshot("启动内置跨网引擎超时。");
                    return;
                }
                readyLine = await readLineTask.ConfigureAwait(false);

                int cPort, sPort;
                if (!TryParseReadyLine(readyLine, out cPort, out sPort))
                {
                    string errTail = "";
                    try { errTail = proc.StandardError.ReadToEnd(); } catch { }
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    proc.Dispose();
                    if (job != IntPtr.Zero) CloseHandle(job);
                    PublishErrorSnapshot("内置跨网引擎握手异常" + (string.IsNullOrWhiteSpace(errTail) ? "。" : ("：" + errTail.Trim())));
                    return;
                }

                // Drain remaining stdout/stderr asynchronously so buffers never stall
                BeginDrainStreams(proc);

                CancellationTokenSource cts;
                lock (gate)
                {
                    if (disposed)
                    {
                        try { if (!proc.HasExited) proc.Kill(); } catch { }
                        proc.Dispose();
                        if (job != IntPtr.Zero) CloseHandle(job);
                        return;
                    }
                    CleanupProcessHandlesLocked();
                    childProcess = proc;
                    jobHandle = job;
                    sessionToken = tok;
                    controlPort = cPort;
                    socksPort = sPort;
                    if (pollCts != null) { try { pollCts.Cancel(); pollCts.Dispose(); } catch { } }
                    cts = new CancellationTokenSource();
                    pollCts = cts;
                }

                proc.Exited += delegate
                {
                    lock (gate)
                    {
                        if (childProcess == proc)
                        {
                            controlPort = 0;
                            socksPort = 0;
                            sessionToken = "";
                        }
                    }
                };

                await RefreshStatusAsync(ct).ConfigureAwait(false);
                StartPollLoop(cts.Token);
            }
            catch (Exception ex)
            {
                if (proc != null)
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                }
                if (job != IntPtr.Zero) CloseHandle(job);
                PublishErrorSnapshot("启动内置跨网引擎失败：" + ex.Message);
            }
        }

        private void StartPollLoop(CancellationToken token)
        {
            Task t = Task.Run(delegate { return PollLoopAsync(token); });
            t.ContinueWith(delegate { }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !disposed)
            {
                EmbeddedTailnetSnapshot snap = LatestSnapshot;
                string st = snap != null ? (snap.BackendState ?? "") : "";
                int delayMs = string.Equals(st, "Running", StringComparison.OrdinalIgnoreCase) ? 12000 : 1500;
                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch { break; }
                if (!IsRunning) break;
                try { await RefreshStatusAsync(ct).ConfigureAwait(false); }
                catch { }
            }
        }

        internal async Task<EmbeddedTailnetSnapshot> RefreshStatusAsync(CancellationToken ct)
        {
            if (!IsRunning)
            {
                return LatestSnapshot;
            }
            string json = await SendHttpAsync("GET", "/status", 3500, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
                return LatestSnapshot;

            EmbeddedTailnetSnapshot parsed = ParseStatusJson(json);
            PublishSnapshot(parsed);
            return parsed;
        }

        internal async Task<EmbeddedTailnetSnapshot> BeginLoginAsync(CancellationToken ct)
        {
            if (!await EnsureStartedAsync(ct).ConfigureAwait(false))
                return LatestSnapshot;

            string json = await SendHttpAsync("POST", "/login", 12000, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 };
                    var root = serializer.DeserializeObject(json) as IDictionary<string, object>;
                    if (root != null)
                    {
                        var nested = root.ContainsKey("status") ? root["status"] as IDictionary<string, object> : null;
                        EmbeddedTailnetSnapshot snap = nested != null
                            ? ParseStatusDict(nested)
                            : ParseStatusDict(root);
                        if (string.IsNullOrEmpty(snap.AuthUrl))
                            snap.AuthUrl = GetStr(root, "authUrl", "");
                        if (string.IsNullOrEmpty(snap.Error) && !GetBool(root, "ok", true))
                            snap.Error = GetStr(root, "error", "未能获取登录授权链接。");
                        PublishSnapshot(snap);
                        return snap;
                    }
                }
                catch { }
            }
            return await RefreshStatusAsync(ct).ConfigureAwait(false);
        }

        internal async Task<bool> LogoutAsync(CancellationToken ct)
        {
            if (!IsRunning)
            {
                try
                {
                    if (Directory.Exists(StateDir))
                        Directory.Delete(StateDir, true);
                }
                catch { }
                PublishSnapshot(new EmbeddedTailnetSnapshot { BackendState = "NeedsLogin" });
                return true;
            }

            string json = await SendHttpAsync("POST", "/logout", 6000, ct).ConfigureAwait(false);
            await RefreshStatusAsync(ct).ConfigureAwait(false);
            return !string.IsNullOrEmpty(json);
        }

        internal LinkProbeResult PingPeerSync(string ip, int timeoutMs)
        {
            if (!IsRunning)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Unknown, "内置跨网引擎未启动。");
            }
            try
            {
                int httpTimeout = Math.Max(2000, Math.Min(8000, timeoutMs));
                string json = SendHttpSync("POST", "/ping?ip=" + Uri.EscapeDataString(ip ?? ""), httpTimeout);
                if (string.IsNullOrEmpty(json))
                    return LinkProbeResult.Of(LinkProbeStatus.Unknown, "内置跨网探测无响应，这不代表不可达。");

                var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 };
                var dict = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (dict == null)
                    return LinkProbeResult.Of(LinkProbeStatus.Unknown, "内置跨网探测响应解析失败。");

                bool ok = GetBool(dict, "ok", false);
                bool direct = GetBool(dict, "direct", false);
                string endpoint = GetStr(dict, "endpoint", "");
                string derp = GetStr(dict, "derpRegionCode", "");
                string peerRelay = GetStr(dict, "peerRelay", "");
                double latencyMs = GetDouble(dict, "latencyMs", 0.0);
                string err = GetStr(dict, "error", "");

                if (!ok)
                {
                    return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                        string.IsNullOrEmpty(err) ? "内置跨网暂未收到探测回包。" : ("内置跨网探测提示：" + err));
                }

                string routeStr = direct
                    ? endpoint
                    : (!string.IsNullOrEmpty(peerRelay)
                        ? ("peer-relay(" + peerRelay + ")")
                        : (!string.IsNullOrEmpty(derp) ? ("DERP(" + derp + ")") : ""));
                var pong = new PingPong
                {
                    Direct = direct,
                    Route = routeStr,
                    LatencyMs = latencyMs,
                    Latency = Math.Round(latencyMs, 1).ToString(CultureInfo.InvariantCulture) + "ms"
                };

                string text;
                LinkProbeStatus status;
                if (direct)
                {
                    status = LinkProbeStatus.Reachable;
                    text = "内置跨网已 P2P 直连，往返约 " + Math.Max(1, (int)Math.Round(latencyMs)) + " 毫秒。";
                }
                else if (!string.IsNullOrEmpty(peerRelay))
                {
                    status = LinkProbeStatus.Relay;
                    text = "内置跨网经节点中继 (" + peerRelay + ") 转发，往返约 " + Math.Max(1, (int)Math.Round(latencyMs)) + " 毫秒。";
                }
                else
                {
                    status = LinkProbeStatus.Relay;
                    text = "内置跨网经 DERP(" + (string.IsNullOrEmpty(derp) ? "中继" : derp) + ") 转发，往返约 " + Math.Max(1, (int)Math.Round(latencyMs)) + " 毫秒。";
                }

                return new LinkProbeResult
                {
                    Status = status,
                    Text = text,
                    LastPong = pong
                };
            }
            catch (Exception ex)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Unknown, "内置跨网探测异常：" + ex.Message);
            }
        }

        internal bool ShouldRouteHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || !IsRunning) return false;
            if (!Tailscale.IsTailnetAddress(host)) return false;

            EmbeddedTailnetSnapshot snap = LatestSnapshot;
            if (snap == null || !string.Equals(snap.BackendState, "Running", StringComparison.OrdinalIgnoreCase))
                return false;

            bool inEmbeddedPeers = false;
            for (int i = 0; i < snap.Peers.Count; i++)
            {
                EmbeddedTailnetPeer p = snap.Peers[i];
                if (string.Equals(p.Ip4, host, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Ip6, host, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.DnsName, host, StringComparison.OrdinalIgnoreCase))
                {
                    inEmbeddedPeers = true;
                    break;
                }
            }
            if (inEmbeddedPeers) return true;
            return !Tailscale.IsSystemDaemonRunningCached();
        }

        internal Task ConnectTargetOverSocks5Async(TcpClient client, string targetHost, int targetPort, CancellationToken ct)
        {
            int port;
            string tok;
            lock (gate)
            {
                port = socksPort;
                tok = sessionToken;
            }
            if (port <= 0 || string.IsNullOrEmpty(tok))
                throw new SocketException((int)SocketError.NotConnected);
            return ConnectViaSocks5Async(client, "127.0.0.1", port, "bobo", tok, targetHost, targetPort, ct);
        }

        /// <summary>
        /// RFC 1928 + RFC 1929 SOCKS5 CONNECT 异步握手实现。
        /// </summary>
        internal static async Task ConnectViaSocks5Async(
            TcpClient client,
            string proxyHost,
            int proxyPort,
            string username,
            string password,
            string targetHost,
            int targetPort,
            CancellationToken ct)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (string.IsNullOrWhiteSpace(targetHost)) throw new ArgumentException("targetHost");

            Task connectTask = client.ConnectAsync(proxyHost, proxyPort);
            using (ct.Register(delegate { try { client.Close(); } catch { } }))
            {
                await connectTask.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();

            NetworkStream stream = client.GetStream();
            using (ct.Register(delegate { try { client.Close(); } catch { } }))
            {
                // 1. Greeting: VER=5, NMETHODS=1, METHOD=0x02 (Username/Password)
                byte[] greeting = new byte[] { 0x05, 0x01, 0x02 };
                await stream.WriteAsync(greeting, 0, greeting.Length, ct).ConfigureAwait(false);
                byte[] methodRep = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
                if (methodRep[0] != 0x05 || methodRep[1] != 0x02)
                    throw new IOException("内置跨网 SOCKS5 代理未接受认证方式。");

                // 2. RFC 1929 Username/Password Auth
                byte[] uBytes = Encoding.UTF8.GetBytes(username ?? "bobo");
                byte[] pBytes = Encoding.UTF8.GetBytes(password ?? "");
                if (uBytes.Length > 255 || pBytes.Length > 255)
                    throw new ArgumentException("SOCKS5 credentials too long");
                byte[] authBuf = new byte[3 + uBytes.Length + pBytes.Length];
                authBuf[0] = 0x01;
                authBuf[1] = (byte)uBytes.Length;
                Buffer.BlockCopy(uBytes, 0, authBuf, 2, uBytes.Length);
                authBuf[2 + uBytes.Length] = (byte)pBytes.Length;
                Buffer.BlockCopy(pBytes, 0, authBuf, 3 + uBytes.Length, pBytes.Length);
                await stream.WriteAsync(authBuf, 0, authBuf.Length, ct).ConfigureAwait(false);

                byte[] authRep = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
                if (authRep[1] != 0x00)
                    throw new UnauthorizedAccessException("内置跨网本地 SOCKS5 会话认证失败。");

                // 3. SOCKS5 CONNECT Request
                string cleanTarget = targetHost.Trim().Trim('[', ']');
                byte[] addrPayload;
                byte atyp;
                IPAddress ipAddr;
                if (IPAddress.TryParse(cleanTarget, out ipAddr))
                {
                    if (ipAddr.IsIPv4MappedToIPv6) ipAddr = ipAddr.MapToIPv4();
                    byte[] rawIp = ipAddr.GetAddressBytes();
                    atyp = rawIp.Length == 4 ? (byte)0x01 : (byte)0x04;
                    addrPayload = rawIp;
                }
                else
                {
                    byte[] domainBytes = Encoding.UTF8.GetBytes(cleanTarget);
                    if (domainBytes.Length == 0 || domainBytes.Length > 255)
                        throw new ArgumentException("Invalid domain length for SOCKS5");
                    atyp = 0x03;
                    addrPayload = new byte[1 + domainBytes.Length];
                    addrPayload[0] = (byte)domainBytes.Length;
                    Buffer.BlockCopy(domainBytes, 0, addrPayload, 1, domainBytes.Length);
                }

                byte[] reqBuf = new byte[4 + addrPayload.Length + 2];
                reqBuf[0] = 0x05;
                reqBuf[1] = 0x01; // CONNECT
                reqBuf[2] = 0x00;
                reqBuf[3] = atyp;
                Buffer.BlockCopy(addrPayload, 0, reqBuf, 4, addrPayload.Length);
                reqBuf[4 + addrPayload.Length] = (byte)((targetPort >> 8) & 0xFF);
                reqBuf[5 + addrPayload.Length] = (byte)(targetPort & 0xFF);
                await stream.WriteAsync(reqBuf, 0, reqBuf.Length, ct).ConfigureAwait(false);

                // 4. Read SOCKS5 Reply + drain bound address completely
                byte[] repHdr = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
                if (repHdr[0] != 0x05)
                    throw new IOException("内置跨网代理返回无效响应版本。");
                if (repHdr[1] != 0x00)
                    throw new SocketException((int)SocketError.HostUnreachable);

                int drainBytes;
                if (repHdr[3] == 0x01) drainBytes = 4 + 2;
                else if (repHdr[3] == 0x04) drainBytes = 16 + 2;
                else if (repHdr[3] == 0x03)
                {
                    byte[] lenByte = await ReadExactAsync(stream, 1, ct).ConfigureAwait(false);
                    drainBytes = lenByte[0] + 2;
                }
                else throw new IOException("Unknown SOCKS5 ATYP");
                await ReadExactAsync(stream, drainBytes, ct).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = await stream.ReadAsync(buf, offset, count - offset, ct).ConfigureAwait(false);
                if (n <= 0) throw new EndOfStreamException("SOCKS5 stream closed prematurely");
                offset += n;
            }
            return buf;
        }

        internal static EmbeddedTailnetSnapshot ParseStatusJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new EmbeddedTailnetSnapshot();
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 512 * 1024 };
                var dict = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (dict == null) return new EmbeddedTailnetSnapshot();
                return ParseStatusDict(dict);
            }
            catch
            {
                return new EmbeddedTailnetSnapshot();
            }
        }

        private static EmbeddedTailnetSnapshot ParseStatusDict(IDictionary<string, object> dict)
        {
            var snap = new EmbeddedTailnetSnapshot
            {
                BackendState = GetStr(dict, "backendState", "Stopped"),
                AuthUrl = GetStr(dict, "authUrl", ""),
                SelfIp4 = GetStr(dict, "selfIp4", ""),
                SelfIp6 = GetStr(dict, "selfIp6", ""),
                SelfDnsName = GetStr(dict, "selfDnsName", ""),
                SelfHostName = GetStr(dict, "selfHostName", ""),
                LoginName = GetStr(dict, "loginName", ""),
                DisplayName = GetStr(dict, "displayName", ""),
                TailnetName = GetStr(dict, "tailnetName", ""),
                ControlPort = GetInt(dict, "controlPort", 0),
                SocksPort = GetInt(dict, "socksPort", 0),
                Listening49810 = GetBool(dict, "listening49810", false),
                Listening49811 = GetBool(dict, "listening49811", false),
                Error = GetStr(dict, "error", "")
            };
            if (dict.ContainsKey("peers"))
            {
                var list = dict["peers"] as IEnumerable;
                if (list != null)
                {
                    foreach (object item in list)
                    {
                        var pd = item as IDictionary<string, object>;
                        if (pd == null) continue;
                        var peer = new EmbeddedTailnetPeer
                        {
                            Id = GetStr(pd, "id", ""),
                            HostName = GetStr(pd, "hostName", ""),
                            DnsName = GetStr(pd, "dnsName", ""),
                            OS = GetStr(pd, "os", ""),
                            Ip4 = GetStr(pd, "ip4", ""),
                            Ip6 = GetStr(pd, "ip6", ""),
                            Online = GetBool(pd, "online", false),
                            Active = GetBool(pd, "active", false),
                            CurAddr = GetStr(pd, "curAddr", ""),
                            Relay = GetStr(pd, "relay", ""),
                            PeerRelay = GetStr(pd, "peerRelay", ""),
                            LastSeen = GetStr(pd, "lastSeen", "")
                        };
                        if (pd.ContainsKey("addrs"))
                        {
                            var addrList = pd["addrs"] as IEnumerable;
                            if (addrList != null)
                            {
                                foreach (object a in addrList)
                                {
                                    string addrStr = Convert.ToString(a, CultureInfo.InvariantCulture);
                                    if (!string.IsNullOrWhiteSpace(addrStr)) peer.Addrs.Add(addrStr.Trim());
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(peer.PreferredIp))
                            snap.Peers.Add(peer);
                    }
                }
            }
            return snap;
        }

        internal static List<TsPeer> ConvertPeersToTsPeers(EmbeddedTailnetSnapshot snap)
        {
            var list = new List<TsPeer>();
            if (snap == null || snap.Peers == null) return list;
            for (int i = 0; i < snap.Peers.Count; i++)
            {
                EmbeddedTailnetPeer p = snap.Peers[i];
                string ip = p.PreferredIp;
                if (string.IsNullOrEmpty(ip)) continue;
                string osTag = string.IsNullOrEmpty(p.OS) ? "" : ("[" + p.OS + "] ");
                list.Add(new TsPeer
                {
                    HostName = p.HostName,
                    DnsName = p.DnsName,
                    Ip = ip,
                    Os = p.OS ?? "",
                    Online = p.Online,
                    CurAddr = p.CurAddr,
                    Relay = p.Relay,
                    PeerRelay = p.PeerRelay,
                    LinkText = p.Online
                        ? (osTag + (!string.IsNullOrEmpty(p.CurAddr) ? "内置跨网 · 直连就绪"
                            : !string.IsNullOrEmpty(p.PeerRelay) ? ("内置跨网 · 节点中继(" + p.PeerRelay + ")")
                            : !string.IsNullOrEmpty(p.Relay) ? ("内置跨网 · DERP(" + p.Relay + ")")
                            : "内置跨网在线"))
                        : (osTag + "内置跨网 · 离线")
                });
            }
            return list;
        }

        private async Task<string> SendHttpAsync(string method, string pathAndQuery, int timeoutMs, CancellationToken ct)
        {
            int port;
            string tok;
            lock (gate)
            {
                port = controlPort;
                tok = sessionToken;
            }
            if (port <= 0 || string.IsNullOrEmpty(tok)) return "";
            return await Task.Run(delegate { return SendHttpSync(method, pathAndQuery, timeoutMs, port, tok); }, ct).ConfigureAwait(false);
        }

        private string SendHttpSync(string method, string pathAndQuery, int timeoutMs)
        {
            int port;
            string tok;
            lock (gate)
            {
                port = controlPort;
                tok = sessionToken;
            }
            return SendHttpSync(method, pathAndQuery, timeoutMs, port, tok);
        }

        internal async Task<int> OpenUdpBridgeAsync(
            string peerIp,
            int remotePort,
            int localClientUdpPort,
            int timeoutMs = 4000,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(peerIp) || remotePort <= 0 || localClientUdpPort <= 0) return 0;
            string path = "/udp-bridge?peerIp=" + Uri.EscapeDataString(peerIp.Trim()) +
                "&remotePort=" + remotePort.ToString(CultureInfo.InvariantCulture) +
                "&localPort=" + localClientUdpPort.ToString(CultureInfo.InvariantCulture);
            string json = await SendHttpAsync("POST", path, Math.Max(1500, timeoutMs), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return 0;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 };
                var dict = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (dict == null || !GetBool(dict, "ok", false)) return 0;
                return GetInt(dict, "bridgePort", 0);
            }
            catch
            {
                return 0;
            }
        }

        internal async Task CloseUdpBridgeAsync(int bridgePort)
        {
            if (bridgePort <= 0) return;
            try
            {
                await SendHttpAsync(
                    "DELETE",
                    "/udp-bridge?port=" + bridgePort.ToString(CultureInfo.InvariantCulture),
                    1500,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        internal static string GetExpectedBinaryPath()
        {
            string exePath;
            string err;
            return TryExtractBinary(out exePath, out err) ? exePath : "";
        }

        private static bool TryExtractEndpointHostIp(string endpoint, out IPAddress ip)
        {
            ip = null;
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            string raw = endpoint.Trim();
            string ipText = raw;
            if (raw.StartsWith("[", StringComparison.Ordinal))
            {
                int close = raw.IndexOf(']');
                if (close > 1) ipText = raw.Substring(1, close - 1);
            }
            else
            {
                int firstColon = raw.IndexOf(':');
                int lastColon = raw.LastIndexOf(':');
                if (firstColon > 0 && firstColon == lastColon)
                {
                    ipText = raw.Substring(0, lastColon);
                }
            }
            if (!IPAddress.TryParse(ipText, out ip)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return BoBoNatTraversal.IsAllowedRemoteCandidateIp(ip);
        }

        internal List<IPAddress> GetPeerDirectCandidateIps(string peerIpOrHost)
        {
            return ExtractPeerDirectCandidateIps(LatestSnapshot, peerIpOrHost);
        }

        internal static List<IPAddress> ExtractPeerDirectCandidateIps(EmbeddedTailnetSnapshot snap, string peerIpOrHost)
        {
            var result = new List<IPAddress>();
            if (snap == null || snap.Peers == null || string.IsNullOrWhiteSpace(peerIpOrHost)) return result;
            string needle = peerIpOrHost.Trim().TrimEnd('.');

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawIps = new List<IPAddress>();
            for (int i = 0; i < snap.Peers.Count; i++)
            {
                EmbeddedTailnetPeer p = snap.Peers[i];
                if (p == null) continue;
                bool matched = string.Equals(p.Ip4, needle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Ip6, needle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.HostName, needle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.DnsName != null ? p.DnsName.TrimEnd('.') : "", needle, StringComparison.OrdinalIgnoreCase);
                if (!matched) continue;

                IPAddress parsedCur;
                if (TryExtractEndpointHostIp(p.CurAddr, out parsedCur) && seen.Add(parsedCur.ToString()))
                {
                    rawIps.Add(parsedCur);
                }
                for (int j = 0; j < p.Addrs.Count; j++)
                {
                    IPAddress parsedAddr;
                    if (TryExtractEndpointHostIp(p.Addrs[j], out parsedAddr) && seen.Add(parsedAddr.ToString()))
                    {
                        rawIps.Add(parsedAddr);
                    }
                }
            }

            // Order: Global IPv6 -> Private LAN IPv4 -> Public IPv4
            for (int i = 0; i < rawIps.Count; i++)
                if (rawIps[i].AddressFamily == AddressFamily.InterNetworkV6 && BoBoNatTraversal.IsUsableGlobalIPv6(rawIps[i]))
                    result.Add(rawIps[i]);
            for (int i = 0; i < rawIps.Count; i++)
                if (rawIps[i].AddressFamily == AddressFamily.InterNetwork && BoBoNatTraversal.IsPrivateLanIPv4(rawIps[i]))
                    result.Add(rawIps[i]);
            for (int i = 0; i < rawIps.Count; i++)
                if (rawIps[i].AddressFamily == AddressFamily.InterNetwork && !BoBoNatTraversal.IsPrivateLanIPv4(rawIps[i]))
                    result.Add(rawIps[i]);
            return result;
        }

        internal IPAddress TryGetPeerDirectPublicIp(string peerIpOrHost)
        {
            List<IPAddress> list = GetPeerDirectCandidateIps(peerIpOrHost);
            return list.Count > 0 ? list[0] : null;
        }

        private static string SendHttpSync(string method, string pathAndQuery, int timeoutMs, int port, string tok)
        {
            if (port <= 0 || string.IsNullOrEmpty(tok)) return "";
            string url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + pathAndQuery;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Proxy = null;
                req.KeepAlive = false;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Headers["X-BoBo-Token"] = tok;
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                    req.ContentLength = 0;

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                    return sr.ReadToEnd();
                }
            }
            catch
            {
                return "";
            }
        }

        private void PublishSnapshot(EmbeddedTailnetSnapshot snap)
        {
            if (snap == null) return;
            lock (gate)
            {
                latestSnapshot = snap;
            }
            Action<EmbeddedTailnetSnapshot> ev = StateChanged;
            if (ev != null)
            {
                try { ev(snap); } catch { }
            }
        }

        private void PublishErrorSnapshot(string errorText)
        {
            EmbeddedTailnetSnapshot prev = LatestSnapshot;
            var snap = new EmbeddedTailnetSnapshot
            {
                BackendState = prev != null && !string.IsNullOrEmpty(prev.BackendState) ? prev.BackendState : "Stopped",
                Error = errorText ?? ""
            };
            PublishSnapshot(snap);
        }

        private static bool TryExtractBinary(out string exePath, out string error)
        {
            exePath = null;
            error = null;
            if (BoBoSystemService.IsRunningAsSystem())
            {
                error = "安全策略禁止在 SYSTEM 身份下释放或运行 BoBoTsNet。";
                return false;
            }
            try
            {
                using (Stream resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (resStream == null)
                    {
                        error = "未找到内置跨网内核资源 BoBoTsNet.exe.gz。";
                        return false;
                    }
                    byte[] plainBytes;
                    using (var gz = new GZipStream(resStream, CompressionMode.Decompress, false))
                    using (var ms = new MemoryStream())
                    {
                        gz.CopyTo(ms);
                        plainBytes = ms.ToArray();
                    }
                    if (plainBytes.Length <= 1024 * 1024)
                    {
                        error = "内置跨网内核资源体积异常。";
                        return false;
                    }
                    string expectedSha = BoBoIdentityStore.ComputeCertSha256Hex(plainBytes);
                    string hashTag = expectedSha.Substring(0, 16);
                    string binRoot = Path.Combine(TsNetRootDir, "bin");
                    string binDir = Path.Combine(binRoot, hashTag);
                    exePath = Path.Combine(binDir, "BoBoTsNet.exe");

                    bool needWrite = true;
                    if (File.Exists(exePath))
                    {
                        try
                        {
                            var fi = new FileInfo(exePath);
                            if (fi.Length == plainBytes.Length)
                            {
                                byte[] diskBytes = File.ReadAllBytes(exePath);
                                string diskSha = BoBoIdentityStore.ComputeCertSha256Hex(diskBytes);
                                if (BoBoIdentityStore.FixedTimeEqualsHex(diskSha, expectedSha))
                                {
                                    needWrite = false;
                                }
                            }
                        }
                        catch { }
                    }

                    if (needWrite)
                    {
                        Directory.CreateDirectory(binDir);
                        string tmp = exePath + "." + Process.GetCurrentProcess().Id + ".tmp";
                        try
                        {
                            File.WriteAllBytes(tmp, plainBytes);
                            if (File.Exists(exePath)) File.Replace(tmp, exePath, null);
                            else File.Move(tmp, exePath);
                        }
                        finally
                        {
                            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        }
                    }

                    // Clean up outdated cached hash directories best-effort
                    try
                    {
                        if (Directory.Exists(binRoot))
                        {
                            foreach (string dir in Directory.GetDirectories(binRoot))
                            {
                                if (!string.Equals(Path.GetFileName(dir), hashTag, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { Directory.Delete(dir, true); } catch { }
                                }
                            }
                        }
                    }
                    catch { }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "解压内置跨网内核失败：" + ex.Message;
                return false;
            }
        }

        private static bool TryParseReadyLine(string line, out int controlPort, out int socksPort)
        {
            controlPort = 0;
            socksPort = 0;
            if (string.IsNullOrWhiteSpace(line)) return false;
            try
            {
                var ser = new JavaScriptSerializer();
                var d = ser.DeserializeObject(line) as IDictionary<string, object>;
                if (d == null || !GetBool(d, "ready", false)) return false;
                controlPort = GetInt(d, "controlPort", 0);
                socksPort = GetInt(d, "socksPort", 0);
                return controlPort > 0 && socksPort > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void BeginDrainStreams(Process proc)
        {
            DrainReaderAsync(proc.StandardOutput).ContinueWith(delegate { }, TaskContinuationOptions.OnlyOnFaulted);
            DrainReaderAsync(proc.StandardError).ContinueWith(delegate { }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static async Task DrainReaderAsync(StreamReader reader)
        {
            if (reader == null) return;
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) != null)
                {
                }
            }
            catch { }
        }

        private static string GenerateSessionTokenHex()
        {
            byte[] buf = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(buf);
            var sb = new StringBuilder(32);
            for (int i = 0; i < buf.Length; i++) sb.Append(buf[i].ToString("x2"));
            return sb.ToString();
        }

        private void CleanupProcessHandlesLocked()
        {
            if (pollCts != null)
            {
                try { pollCts.Cancel(); pollCts.Dispose(); } catch { }
                pollCts = null;
            }
            if (childProcess != null)
            {
                try { if (!childProcess.HasExited) childProcess.Kill(); } catch { }
                try { childProcess.Dispose(); } catch { }
                childProcess = null;
            }
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
                jobHandle = IntPtr.Zero;
            }
            controlPort = 0;
            socksPort = 0;
            sessionToken = "";
        }

        public void Dispose()
        {
            int port;
            string tok;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                port = controlPort;
                tok = sessionToken;
                if (pollCts != null) { try { pollCts.Cancel(); } catch { } }
            }

            if (port > 0 && !string.IsNullOrEmpty(tok))
            {
                try { SendHttpSync("POST", "/shutdown", 800, port, tok); } catch { }
            }

            lock (gate)
            {
                if (childProcess != null)
                {
                    try { childProcess.WaitForExit(700); } catch { }
                }
                CleanupProcessHandlesLocked();
            }
        }

        private static string GetStr(IDictionary<string, object> d, string key, string fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            return Convert.ToString(d[key], CultureInfo.InvariantCulture) ?? fallback;
        }

        private static int GetInt(IDictionary<string, object> d, string key, int fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            try { return Convert.ToInt32(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double GetDouble(IDictionary<string, object> d, string key, double fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            try { return Convert.ToDouble(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool GetBool(IDictionary<string, object> d, string key, bool fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            try { return Convert.ToBoolean(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        // Win32 Job Object so BoBoTsNet.exe terminates automatically even if BoBo远程.exe is killed
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInfo
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo
        {
            public JobObjectBasicLimitInfo BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static IntPtr TryAttachKillOnCloseJob(Process proc)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var info = new JobObjectExtendedLimitInfo
                {
                    BasicLimitInformation = new JobObjectBasicLimitInfo
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                int sz = Marshal.SizeOf(typeof(JobObjectExtendedLimitInfo));
                IntPtr ptr = Marshal.AllocHGlobal(sz);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)sz)
                        || !AssignProcessToJobObject(job, proc.Handle))
                    {
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                return job;
            }
            catch
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
    }
}
