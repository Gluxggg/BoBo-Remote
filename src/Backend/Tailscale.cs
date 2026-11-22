using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DualRemote
{
    /// <summary>一个 Tailscale 节点的只读快照。</summary>
    internal sealed class TsPeer
    {
        internal string HostName = "";
        internal string DnsName = "";
        internal string Ip = "";
        internal bool Online;
        internal string CurAddr = "";
        internal string Relay = "";
        internal string PeerRelay = "";

        /// <summary>最近一次记录到的连接路径；这里是 status 的已记录状态，不是本次探测结论。</summary>
        internal string LinkText
        {
            get
            {
                if (CurAddr.Length > 0) return "直连（最近记录）";
                if (PeerRelay.Length > 0) return "peer 中继(" + PeerRelay + ")（最近记录）";
                if (Relay.Length > 0) return "DERP(" + Relay + ")（最近记录）";
                return "路径未知";
            }
        }

        internal string DisplayText()
        {
            string name = HostName.Length > 0 ? HostName : DnsName;
            return name + "  ·  " + Ip + "  ·  " + (Online ? "在线" : "离线") + "  ·  " + LinkText;
        }
    }

    /// <summary>一次 tailscale ping 回包。</summary>
    internal sealed class PingPong
    {
        internal string Route = "";
        internal string Latency = "";
        internal bool Direct;
    }

    /// <summary>tailscale status 的读取结果。</summary>
    internal sealed class TailnetLoadResult
    {
        internal bool Ok;
        internal string BackendState = "";
        internal string Error = "";
        internal readonly List<TsPeer> Peers = new List<TsPeer>();
    }

    internal enum LinkProbeStatus
    {
        Reachable,
        Relay,
        Unknown,
        Failed
    }

    internal sealed class LinkProbeResult
    {
        internal LinkProbeStatus Status;
        internal string Text = "";

        internal static LinkProbeResult Of(LinkProbeStatus status, string text)
        {
            LinkProbeResult result = new LinkProbeResult();
            result.Status = status;
            result.Text = text == null ? "" : text;
            return result;
        }
    }

    internal static class Tailscale
    {
        internal const int DefaultStatusTimeoutMs = 5000;
        internal const int DefaultProbeTimeoutMs = 8000;

        private static readonly Regex PongRegex = new Regex(
            @"pong from .*?\(([^)]*)\)(?:, [^)]*)? via (.+?) in ([0-9.]+)(ms|s)",
            RegexOptions.CultureInvariant);

        internal static string ExePath
        {
            get
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(Path.Combine(programFiles, "Tailscale"), "tailscale.exe");
            }
        }

        internal static bool Present
        {
            get { return File.Exists(ExePath); }
        }

        /// <summary>读取已认证节点（只保留 Windows），阻塞，最多 timeoutMs；请在后台线程调用。</summary>
        internal static TailnetLoadResult LoadPeers(int timeoutMs)
        {
            TailnetLoadResult result = new TailnetLoadResult();
            if (!Present)
            {
                result.Error = "未找到 Tailscale：" + ExePath;
                return result;
            }
            int exitCode;
            string stderrText;
            bool timedOut;
            string json = RunCli("status --json", timeoutMs, out exitCode, out stderrText, out timedOut);
            if (timedOut)
            {
                result.Error = "读取 Tailscale 状态超时（约 " + Math.Max(1, timeoutMs / 1000) + " 秒），暂不可读；不代表没有设备。";
                return result;
            }
            if (exitCode != 0)
            {
                result.Error = DescribeStatusFailure(exitCode, stderrText);
                return result;
            }
            List<TsPeer> peers;
            string backendState;
            string parseError;
            if (!TryParseStatusJson(json, out peers, out backendState, out parseError))
            {
                result.Error = parseError;
                return result;
            }
            result.BackendState = backendState;
            if (!BackendReady(backendState))
            {
                result.Error = "Tailscale 当前未运行或未登录（状态：" + DescribeBackendState(backendState)
                             + "）。请在 Tailscale 中登录后重试。";
                return result;
            }
            result.Ok = true;
            result.Peers.AddRange(peers);
            return result;
        }

        internal static bool BackendReady(string backendState)
        {
            return string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase);
        }

        internal static string DescribeBackendState(string backendState)
        {
            if (string.IsNullOrEmpty(backendState)) return "未知";
            if (string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase)) return "运行中";
            if (string.Equals(backendState, "NeedsLogin", StringComparison.OrdinalIgnoreCase)) return "需要登录";
            if (string.Equals(backendState, "NeedsMachineAuth", StringComparison.OrdinalIgnoreCase)) return "需要设备授权";
            if (string.Equals(backendState, "Stopped", StringComparison.OrdinalIgnoreCase)) return "已停止";
            if (string.Equals(backendState, "Starting", StringComparison.OrdinalIgnoreCase)) return "正在启动";
            if (string.Equals(backendState, "NoState", StringComparison.OrdinalIgnoreCase)) return "未启动";
            if (string.Equals(backendState, "InUseOtherUser", StringComparison.OrdinalIgnoreCase)) return "被其他用户占用";
            return backendState;
        }

        internal static LinkProbeResult Probe(string hostOrIp, int timeoutMs)
        {
            string parameterError;
            string host = Engines.NormalizeHost(hostOrIp, out parameterError);
            if (host == null)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Failed, "参数无效：" + parameterError);
            }

            Stopwatch clock = Stopwatch.StartNew();
            System.Net.IPAddress address;
            if (!System.Net.IPAddress.TryParse(host, out address))
            {
                if (IsTailnetAddress(host))
                {
                    return ProbeTailnetHost(host, Remaining(timeoutMs, clock));
                }
                LinkProbeResult resolveFailure = ResolveHost(host, timeoutMs, clock, out address);
                if (resolveFailure != null) return resolveFailure;
                if (address == null)
                {
                    return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                        "主机名 " + host + " 没有可用地址；未判定远控不可达。");
                }
            }

            if (IsTailnetAddress(address.ToString()))
            {
                return ProbeTailnetHost(address.ToString(), Remaining(timeoutMs, clock));
            }
            return ProbeAddress(host, address, Remaining(timeoutMs, clock));
        }

        internal static bool IsTailnetAddress(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return true;
            System.Net.IPAddress address;
            if (!System.Net.IPAddress.TryParse(host, out address)) return false;
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return bytes.Length == 16 && bytes[0] == 0xfd && bytes[1] == 0x7a
                    && bytes[2] == 0x11 && bytes[3] == 0x5c
                    && bytes[4] == 0xa1 && bytes[5] == 0xe0;
            return false;
        }

        private static int Remaining(int timeoutMs, Stopwatch clock)
        {
            return timeoutMs - (int)clock.ElapsedMilliseconds;
        }

        private static LinkProbeResult ResolveHost(string host, int timeoutMs, Stopwatch clock,
            out System.Net.IPAddress address)
        {
            address = null;
            int left = Remaining(timeoutMs, clock);
            if (left <= 0)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                    "主机名 " + host + " 未解析：探测时间预算已耗尽，未作出可达或不可达判断。");
            }
            System.Threading.Tasks.Task<System.Net.IPAddress[]> lookup = System.Net.Dns.GetHostAddressesAsync(host);
            bool completed;
            try
            {
                completed = lookup.Wait(left);
            }
            catch (AggregateException)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Failed,
                    "主机名 " + host + " 解析失败；未判定远控不可达。");
            }
            if (!completed)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                    "主机名 " + host + " 解析超时（探测预算约 " + Math.Max(1, timeoutMs / 1000)
                    + " 秒）；未判定远控不可达。");
            }
            System.Net.IPAddress[] addresses;
            try
            {
                addresses = lookup.Result;
            }
            catch (AggregateException)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Failed,
                    "主机名 " + host + " 解析失败；未判定远控不可达。");
            }
            address = PickAddress(addresses);
            return null;
        }

        private static System.Net.IPAddress PickAddress(System.Net.IPAddress[] addresses)
        {
            if (addresses == null) return null;
            System.Net.IPAddress fallback = null;
            foreach (System.Net.IPAddress address in addresses)
            {
                if (address == null) continue;
                if (fallback == null) fallback = address;
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return address;
                if (!address.IsIPv6LinkLocal) return address;
            }
            return fallback;
        }

        private static LinkProbeResult ProbeTailnetHost(string host, int timeoutMs)
        {
            if (!Present)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                    "未找到 Tailscale，未执行 Tailscale 节点路径探测；这不代表节点不可达，可直接用局域网/公网地址连接。");
            }
            int left = Math.Max(1, timeoutMs);
            int exitCode;
            string stderrText;
            bool timedOut;
            string arguments = "ping --c 2 --timeout 2s --until-direct=true " + Engines.Quote(host);
            string output = RunCli(arguments, left, out exitCode, out stderrText, out timedOut);
            return DescribeProbeResult(output, stderrText, exitCode, timedOut,
                Math.Max(1, left / 1000));
        }

        private static LinkProbeResult ProbeAddress(string label, System.Net.IPAddress address, int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                    "普通地址 " + label + " 未探测：主机名解析已用完时间预算，未判定远控不可达。");
            }
            string shown = string.Equals(label, address.ToString(), StringComparison.OrdinalIgnoreCase)
                ? label
                : label + "（本次使用 " + address + "）";
            try
            {
                using (System.Net.NetworkInformation.Ping ping = new System.Net.NetworkInformation.Ping())
                {
                    System.Net.NetworkInformation.PingReply reply = ping.Send(address, timeoutMs);
                    if (reply != null && reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        return LinkProbeResult.Of(LinkProbeStatus.Reachable,
                            "普通地址 " + shown + " 收到 ICMP 回包（RTT " + reply.RoundtripTime
                            + "ms）。仅说明网络层可达，未测视频帧丢失与端到端延迟。");
                    }
                    string reason = reply == null ? "无响应" : reply.Status.ToString();
                    return LinkProbeResult.Of(LinkProbeStatus.Unknown,
                        "普通地址 " + shown + " 未收到 ICMP 回包（" + reason
                        + "）；可能被防火墙禁用 ICMP，不能据此判定远控不可达，也不代表视频会丢帧。");
                }
            }
            catch (Exception ex)
            {
                return LinkProbeResult.Of(LinkProbeStatus.Failed,
                    "普通地址 " + shown + " 探测失败：" + ex.Message + "；未判定远控不可达。");
            }
        }

        internal static List<PingPong> ParsePongs(string text)
        {
            List<PingPong> pongs = new List<PingPong>();
            if (string.IsNullOrEmpty(text)) return pongs;
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                Match match = PongRegex.Match(line);
                if (!match.Success) continue;
                PingPong pong = new PingPong();
                pong.Route = match.Groups[2].Value.Trim();
                string number = match.Groups[3].Value;
                string unit = match.Groups[4].Value;
                if (unit == "s")
                {
                    double milliseconds = Convert.ToDouble(number, System.Globalization.CultureInfo.InvariantCulture) * 1000.0;
                    pong.Latency = Math.Round(milliseconds) + "ms";
                }
                else
                {
                    pong.Latency = number + "ms";
                }
                pong.Direct = IsDirectRoute(pong.Route);
                pongs.Add(pong);
            }
            return pongs;
        }

        internal static LinkProbeResult DescribeProbeResult(string output, string stderrText, int exitCode, bool timedOut, int timeoutSeconds)
        {
            LinkProbeResult probe = LinkProbeResult.Of(LinkProbeStatus.Unknown,
                DescribeProbeOutput(output, stderrText, exitCode, timedOut, timeoutSeconds));
            string combined = (output == null ? "" : output) + "\n" + (stderrText == null ? "" : stderrText);
            List<PingPong> pongs = ParsePongs(combined);
            if (pongs.Count > 0)
            {
                PingPong last = pongs[pongs.Count - 1];
                if (last.Direct) probe.Status = LinkProbeStatus.Reachable;
                else if (last.Route.StartsWith("DERP(", StringComparison.Ordinal)
                    || last.Route.StartsWith("peer-relay(", StringComparison.Ordinal)) probe.Status = LinkProbeStatus.Relay;
                return probe;
            }
            if (timedOut) probe.Status = LinkProbeStatus.Unknown;
            else if (IsBusyError(combined) || IsPermissionError(combined)
                || combined.IndexOf("no matching peer", StringComparison.OrdinalIgnoreCase) >= 0) probe.Status = LinkProbeStatus.Unknown;
            else if (exitCode != 0) probe.Status = LinkProbeStatus.Failed;
            return probe;
        }

        internal static string DescribeProbeOutput(string output, string stderrText, int exitCode, bool timedOut, int timeoutSeconds)
        {
            string combined = (output == null ? "" : output) + "\n" + (stderrText == null ? "" : stderrText);
            List<PingPong> pongs = ParsePongs(combined);
            if (pongs.Count > 0)
            {
                PingPong last = pongs[pongs.Count - 1];
                string routePart;
                if (last.Route.StartsWith("DERP(", StringComparison.Ordinal))
                {
                    routePart = "经 DERP 中继，本次未建立直连";
                }
                else if (last.Route.StartsWith("peer-relay(", StringComparison.Ordinal))
                {
                    routePart = "经 peer 中继，本次未建立直连";
                }
                else if (last.Direct)
                {
                    routePart = "直连";
                }
                else
                {
                    routePart = "路径未确认，未判定直连";
                }
                return "探测结果：" + routePart + "；RTT " + last.Latency + "。这是 tailscale ping 探测，不代表串流帧率。";
            }
            if (timedOut)
            {
                return "探测超时（约 " + timeoutSeconds + " 秒内没有可用回包，未判定直连）。";
            }
            if (combined.IndexOf("is local Tailscale IP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "这是本机 Tailscale IP。";
            }
            if (IsBusyError(combined))
            {
                return "Tailscale 正被系统占用，未判定直连。稍后重试或点「手动连接」直接试连。";
            }
            if (IsPermissionError(combined))
            {
                return "当前 Windows 用户无法读取 Tailscale，未判定直连。可点「手动连接」输入地址直接试连。";
            }
            string reason = FirstLine(stderrText);
            if (reason.Length == 0) reason = FirstLine(output);
            if (reason.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("no reply", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "无回包/超时（未判定直连），请确认对方已开机并登录 Tailscale 后重试。";
            }
            return "未能探测（未判定直连），Tailscale 没有返回可用回包（退出码 " + exitCode + "）。";
        }

        internal static bool TryParseStatusJson(string json, out List<TsPeer> peers, out string backendState, out string error)
        {
            peers = new List<TsPeer>();
            backendState = "";
            error = null;
            if (string.IsNullOrEmpty(json))
            {
                error = "Tailscale 没有返回状态 JSON。";
                return false;
            }
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 8 * 1024 * 1024;
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
                if (root == null)
                {
                    error = "无法解析 Tailscale 状态。";
                    return false;
                }
                backendState = GetString(root, "BackendState");
                object peersValue;
                if (root.TryGetValue("Peer", out peersValue))
                {
                    Dictionary<string, object> peerMap = peersValue as Dictionary<string, object>;
                    if (peerMap != null)
                    {
                        foreach (KeyValuePair<string, object> item in peerMap)
                        {
                            Dictionary<string, object> data = item.Value as Dictionary<string, object>;
                            if (data == null) continue;
                            if (!string.Equals(GetString(data, "OS"), "windows", StringComparison.OrdinalIgnoreCase)) continue;
                            string[] ips = GetStringArray(data, "TailscaleIPs");
                            if (ips.Length == 0) continue;
                            TsPeer peer = new TsPeer();
                            peer.HostName = GetString(data, "HostName");
                            peer.DnsName = GetString(data, "DNSName");
                            peer.CurAddr = GetString(data, "CurAddr");
                            peer.Relay = GetString(data, "Relay");
                            peer.PeerRelay = GetString(data, "PeerRelay");
                            object onlineValue;
                            peer.Online = data.TryGetValue("Online", out onlineValue) && onlineValue is bool && (bool)onlineValue;
                            peer.Ip = ips[0];
                            foreach (string ip in ips)
                            {
                                if (ip.IndexOf(':') < 0)
                                {
                                    peer.Ip = ip;
                                    break;
                                }
                            }
                            peers.Add(peer);
                        }
                    }
                }
                peers.Sort(delegate(TsPeer a, TsPeer b)
                {
                    int byOnline = b.Online.CompareTo(a.Online);
                    if (byOnline != 0) return byOnline;
                    return string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase);
                });
                return true;
            }
            catch (Exception ex)
            {
                error = "无法解析 Tailscale 状态：" + ex.Message;
                return false;
            }
        }

        private static bool IsDirectRoute(string route)
        {
            if (route.Length == 0) return false;
            if (route.StartsWith("DERP(", StringComparison.Ordinal)) return false;
            if (route.StartsWith("peer-relay(", StringComparison.Ordinal)) return false;
            if (string.Equals(route, "disco", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(route, "ICMP", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(route, "TSMP", StringComparison.OrdinalIgnoreCase)) return false;
            return route.IndexOf(':') >= 0;
        }

        /// <summary>
        /// tailscale status 非零退出时的中文说明。权限拒绝与系统占用只给可操作的中文，
        /// 不回显内部管道名或英文原文；其余情况保持原有退出码与首行信息。
        /// </summary>
        internal static string DescribeStatusFailure(int exitCode, string stderrText)
        {
            if (IsBusyError(stderrText))
            {
                return "Tailscale 正被系统占用，暂时读不到设备。稍后重试或点「手动连接」输入地址。";
            }
            if (IsPermissionError(stderrText))
            {
                return "当前 Windows 用户无法读取 Tailscale 设备。可点「手动连接」输入主机地址。";
            }
            string line = FirstLine(stderrText);
            if (line.Length == 0)
            {
                return "tailscale status 退出码 " + exitCode + "，暂不可读。";
            }
            return "tailscale status 退出码 " + exitCode + "：" + line;
        }

        /// <summary>系统占用：只认明确的 already in use（常见于 401 Already in Use）。</summary>
        private static bool IsBusyError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("already in use", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>权限拒绝：本机 Tailscale 服务（常为受保护的命名管道）拒绝了当前进程；普通 401 也归此类。</summary>
        private static bool IsPermissionError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf("access is denied", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("permission denied", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("401", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("权限", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            object value;
            if (data.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return "";
        }

        private static string[] GetStringArray(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return new string[0];
            if (value is string) return new string[0];
            // JavaScriptSerializer 在泛型字典里把嵌套数组反序列化为 ArrayList（非 object[]），
            // 所以按 IEnumerable 遍历，兼容 ArrayList / object[]。
            System.Collections.IEnumerable items = value as System.Collections.IEnumerable;
            if (items == null) return new string[0];
            List<string> result = new List<string>();
            foreach (object item in items)
            {
                string text = Convert.ToString(item);
                if (!string.IsNullOrEmpty(text)) result.Add(text);
            }
            return result.ToArray();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return "";
        }

        internal static string RunCli(string arguments, int timeoutMs, out int exitCode, out string stderrText, out bool timedOut)
        {
            exitCode = -1;
            stderrText = "";
            timedOut = false;
            StringBuilder outBuffer = new StringBuilder();
            StringBuilder errBuffer = new StringBuilder();
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = ExePath;
                info.Arguments = arguments;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (outBuffer) { outBuffer.AppendLine(e.Data); }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (errBuffer) { errBuffer.AppendLine(e.Data); }
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        timedOut = true;
                        try { process.Kill(); } catch { }
                    }
                    try { process.WaitForExit(); } catch { }
                    if (!timedOut)
                    {
                        try { exitCode = process.ExitCode; } catch { exitCode = -1; }
                    }
                }
            }
            catch (Exception ex)
            {
                stderrText = "无法运行 tailscale：" + ex.Message;
                return "";
            }
            string outText;
            string errText;
            lock (outBuffer) { outText = outBuffer.ToString(); }
            lock (errBuffer) { errText = errBuffer.ToString(); }
            stderrText = errText;
            return outText;
        }
    }
}
