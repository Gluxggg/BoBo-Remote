using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace DualRemote
{
    /// <summary>本机地址与 Foundation Sunshine 只读健康快照。</summary>
    internal sealed class HostSnapshot
    {
        internal string LanIpv4 = "";
        internal string TailscaleIpv4 = "";
        internal string PublicIpv6 = "";
        internal bool SunshineInstalled;
        internal bool SunshineRunning;
        internal string SunshineVersion = "";
        internal string Encoder = "";
        internal string DisplayModeLabel = "默认实体屏";
        internal bool AntiLossTuned;
        internal string TuningSummary = "未检测到本机 Foundation Sunshine";
        internal readonly List<string> Recommendations = new List<string>();
    }

    internal static class HostNetworkInfo
    {
        internal const string DefaultSunshineRoot = @"C:\Program Files\Sunshine";
        internal const string FoundationReleasesUrl = "https://github.com/AlkaidLab/foundation-sunshine/releases";
        internal const string EasyTierReleasesUrl = "https://github.com/EasyTier/EasyTier/releases";

        private static readonly Regex SessionsCountRegex = new Regex("\"total_sessions\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);

        /// <summary>收集本机 IP 与 Foundation Sunshine 配置诊断（只读，无副作用，建议在后台线程调用）。</summary>
        internal static HostSnapshot Collect()
        {
            var snap = new HostSnapshot();
            CollectAddresses(snap);
            CollectSunshineStatus(snap, DefaultSunshineRoot);
            return snap;
        }

        private static void CollectAddresses(HostSnapshot snap)
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string name = nic.Name ?? "";
                    string desc = nic.Description ?? "";
                    if (name.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }
                    if (props == null) continue;

                    bool isTailscale = name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0
                        || desc.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0;

                    foreach (UnicastIPAddressInformation uni in props.UnicastAddresses)
                    {
                        IPAddress addr = uni.Address;
                        if (addr == null) continue;
                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] b = addr.GetAddressBytes();
                            if (b[0] == 169 && b[1] == 254) continue; // APIPA
                            if (isTailscale || (b[0] == 100 && b[1] >= 64 && b[1] <= 127))
                            {
                                if (snap.TailscaleIpv4.Length == 0) snap.TailscaleIpv4 = addr.ToString();
                            }
                            else if (b[0] == 192 && b[1] == 168 && b[2] == 137)
                            {
                                // Windows 移动热点虚拟网卡，优先让位给真实局域网 IP
                                if (snap.LanIpv4.Length == 0) snap.LanIpv4 = addr.ToString();
                            }
                            else
                            {
                                if (snap.LanIpv4.Length == 0 || snap.LanIpv4.StartsWith("192.168.137.", StringComparison.Ordinal))
                                    snap.LanIpv4 = addr.ToString();
                            }
                        }
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && !isTailscale)
                        {
                            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast || addr.IsIPv6Teredo) continue;
                            byte[] b = addr.GetAddressBytes();
                            // 2000::/3 全球单播公网 IPv6（跳过 fd00::/8 ULA）
                            if ((b[0] & 0xE0) == 0x20)
                            {
                                string text = addr.ToString();
                                int zone = text.IndexOf('%');
                                if (zone > 0) text = text.Substring(0, zone);
                                if (snap.PublicIpv6.Length == 0) snap.PublicIpv6 = text;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        internal static void CollectSunshineStatus(HostSnapshot snap, string root)
        {
            string exe = Path.Combine(root, "sunshine.exe");
            string conf = Path.Combine(root, "config", "sunshine.conf");
            if (!File.Exists(exe))
            {
                snap.SunshineInstalled = false;
                snap.TuningSummary = "本机未安装 Foundation Sunshine（如本机为控制端笔记本可忽略）。";
                return;
            }
            snap.SunshineInstalled = true;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                snap.SunshineVersion = RepairSunshineVersion(info.FileVersion);
            }
            catch
            {
                snap.SunshineVersion = "已安装";
            }

            try
            {
                Process[] procs = Process.GetProcessesByName("sunshine");
                snap.SunshineRunning = procs.Length > 0;
                foreach (Process p in procs) { try { p.Dispose(); } catch { } }
            }
            catch { }

            if (!File.Exists(conf))
            {
                snap.TuningSummary = "未找到 sunshine.conf。";
                return;
            }

            Dictionary<string, string> map = ParseKeyValues(conf);
            AnalyzeConfig(snap, map);
        }

        internal static void AnalyzeConfig(HostSnapshot snap, Dictionary<string, string> map)
        {
            string outputName = GetVal(map, "output_name");
            string prep = GetVal(map, "display_device_prep");
            if (string.Equals(outputName, "ZakoHDR", StringComparison.OrdinalIgnoreCase)
                && string.Equals(prep, "ensure_only_display", StringComparison.OrdinalIgnoreCase))
                snap.DisplayModeLabel = "熄屏防窥（仅虚拟屏）";
            else if (string.Equals(outputName, "ZakoHDR", StringComparison.OrdinalIgnoreCase)
                && string.Equals(prep, "ensure_secondary", StringComparison.OrdinalIgnoreCase))
                snap.DisplayModeLabel = "虚拟屏扩展（保留实体屏）";
            else
                snap.DisplayModeLabel = "默认实体屏";

            string encoder = GetVal(map, "encoder").ToLowerInvariant();
            string adapter = GetVal(map, "adapter_name").ToLowerInvariant();
            bool isAmd = encoder == "amdvce" || adapter.IndexOf("amd", StringComparison.OrdinalIgnoreCase) >= 0
                || adapter.IndexOf("radeon", StringComparison.OrdinalIgnoreCase) >= 0;
            snap.Encoder = isAmd ? "AMD AMF" : (encoder.Length > 0 ? encoder : "自动");

            snap.Recommendations.Clear();
            bool configGood = true;
            bool amdChecked = false;
            if (isAmd)
            {
                amdChecked = true;
                if (!string.Equals(GetVal(map, "amd_rc"), "cbr", StringComparison.OrdinalIgnoreCase))
                {
                    configGood = false;
                    snap.Recommendations.Add("AMD 码控未设为 CBR（该配置与历史 Skipping FEC / 大帧异常相关，建议结合本次会话的帧统计核对）");
                }
                if (!string.Equals(GetVal(map, "amd_enforce_hrd"), "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    configGood = false;
                    snap.Recommendations.Add("未开启 HRD 硬码率约束（amd_enforce_hrd）");
                }
                int ltr;
                if (!int.TryParse(GetVal(map, "amd_ltr_frames"), out ltr) || ltr < 1)
                {
                    configGood = false;
                    snap.Recommendations.Add("未显式启用 LTR 弱网参考帧恢复（amd_ltr_frames 建议设为 2，当前未启用；恢复效果需会话验证）");
                }
            }
            if (!string.Equals(GetVal(map, "fec_auto"), "enabled", StringComparison.OrdinalIgnoreCase))
            {
                configGood = false;
                snap.Recommendations.Add("未开启自动 FEC 动态前向纠错（fec_auto）");
            }
            snap.AntiLossTuned = configGood;
            string readyItems = amdChecked ? "AMD CBR + HRD + LTR + Auto-FEC" : "Auto-FEC";
            if (configGood)
                snap.TuningSummary = "配置项已就绪（" + readyItems + "）；实际串流丢包与延迟尚未实测";
            else
                snap.TuningSummary = "检测到 " + snap.Recommendations.Count + " 项丢包/延迟隐患，建议点击一键调优";
        }

        internal static string RepairSunshineVersion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            foreach (char c in raw)
            {
                if (c >= '\u4e00' && c <= '\u9fff') return raw;
            }
            try
            {
                byte[] bytes = Encoding.GetEncoding(1252).GetBytes(raw);
                string utf8 = Encoding.UTF8.GetString(bytes);
                foreach (char c in utf8)
                {
                    if (c >= '\u4e00' && c <= '\u9fff') return utf8;
                }
            }
            catch { }
            return raw;
        }

        internal static Dictionary<string, string> ParseKeyValues(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    map[key] = val;
                }
            }
            catch { }
            return map;
        }

        private static string GetVal(Dictionary<string, string> map, string key)
        {
            string val;
            return map.TryGetValue(key, out val) ? val : "";
        }

        /// <summary>
        /// 检测本机当前是否正处于 Sunshine/Moonlight 远控会话中（只读，供防窥壁纸自动守护使用）。
        /// 优先通过本机 CA 证书指纹校验的 /api/runtime/sessions 接口查询；若接口暂不可用则检查活动串流连接。
        /// </summary>
        internal static bool IsHostBeingControlled()
        {
            int sessions;
            if (TryQuerySunshineSessionCount(DefaultSunshineRoot, out sessions))
            {
                return sessions > 0;
            }
            return HasActiveRemoteTcpConnection();
        }

        private static bool TryQuerySunshineSessionCount(string root, out int count)
        {
            count = 0;
            string pemPath = Path.Combine(root, "config", "credentials", "cacert.pem");
            if (!File.Exists(pemPath)) return false;

            string expectedThumbprint;
            try
            {
                using (var cert = new X509Certificate2(pemPath))
                {
                    expectedThumbprint = cert.Thumbprint;
                }
            }
            catch { return false; }
            if (string.IsNullOrEmpty(expectedThumbprint)) return false;

            int webPort = 47990;
            string conf = Path.Combine(root, "config", "sunshine.conf");
            if (File.Exists(conf))
            {
                Dictionary<string, string> map = ParseKeyValues(conf);
                int basePort;
                if (int.TryParse(GetVal(map, "port"), out basePort) && basePort > 0 && basePort < 65534)
                    webPort = basePort + 1;
            }

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create("https://localhost:" + webPort + "/api/runtime/sessions");
                req.Proxy = null;
                req.Timeout = 1800;
                req.ReadWriteTimeout = 1800;
                req.Method = "GET";
                req.ServerCertificateValidationCallback = delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
                {
                    if (certificate == null) return false;
                    return string.Equals(certificate.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);
                };
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    Match m = SessionsCountRegex.Match(body);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out count))
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool HasActiveRemoteTcpConnection()
        {
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                foreach (TcpConnectionInformation conn in props.GetActiveTcpConnections())
                {
                    if (conn.State != TcpState.Established) continue;
                    int localPort = conn.LocalEndPoint.Port;
                    // Sunshine RTSP/Control (48010) 仅在活跃串流期间保持连接
                    if (localPort == 48010 && !IPAddress.IsLoopback(conn.RemoteEndPoint.Address))
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
