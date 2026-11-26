using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DualRemote
{
    internal enum BoBoRelaySelectionMode
    {
        Manual = 0,
        LowestLatency = 1,
        DynamicLoadBalanced = 2
    }

    internal sealed class BoBoCustomRelay
    {
        internal string Id = "";
        internal string Name = "";
        internal string Host = "";
        internal int Port = BoBoProtocol.DefaultTcpPort;
        internal string HealthUrl = "";
        internal int Weight = 100;

        // Runtime scheduling & telemetry state (not serialized)
        internal long LastProbeUtcTicks;
        internal bool LastReachable;
        internal double LastRttMs;
        internal bool HasServerLoad;
        internal double LastServerLoad;
        internal double LastCpuPct;
        internal int LastActiveSessions;
        internal int LastMaxSessions;
        internal double LastBandwidthMbps;
        internal double LastMaxBandwidthMbps;
        internal bool LastAccepting = true;
        internal string LastRegion = "";
        internal double LastScore = double.PositiveInfinity;
        internal int ConsecutiveFailures;
        internal long LastFailureUtcTicks;

        internal string EndpointText
        {
            get
            {
                string safeHost = (Host ?? "").Trim();
                int safePort = Port > 0 && Port <= 65535 ? Port : BoBoProtocol.DefaultTcpPort;
                if (safeHost.IndexOf(':') >= 0 && !safeHost.StartsWith("[", StringComparison.Ordinal))
                    return "[" + safeHost + "]:" + safePort;
                return safeHost + ":" + safePort;
            }
        }

        internal string DisplayLabel
        {
            get
            {
                string ep = EndpointText;
                string safeName = (Name ?? "").Trim();
                if (safeName.Length == 0 || string.Equals(safeName, Host, StringComparison.OrdinalIgnoreCase))
                    return ep;
                return safeName + " (" + ep + ")";
            }
        }

        internal string DisplayLabelWithTelemetry
        {
            get
            {
                string baseLabel = DisplayLabel;
                if (LastProbeUtcTicks <= 0) return baseLabel;
                if (!LastReachable) return baseLabel + " · 不可达";
                string rttTag = Math.Max(1, (int)Math.Round(LastRttMs)).ToString(CultureInfo.InvariantCulture) + "ms";
                if (!LastAccepting) return baseLabel + " · " + rttTag + " · 维护中";
                if (HasServerLoad)
                {
                    int pct = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, LastServerLoad)) * 100.0);
                    string loadTag = pct >= 85 ? ("过载" + pct + "%") : ("负载" + pct + "%");
                    return baseLabel + " · " + rttTag + " · " + loadTag;
                }
                return baseLabel + " · " + rttTag;
            }
        }

        public override string ToString()
        {
            return DisplayLabelWithTelemetry;
        }
    }

    /// <summary>
    /// 用户自定义兜底中转（自建 VPS / 转发入口）本地存储与动态调度配置：
    /// 位置：%LOCALAPPDATA%\双端远程\relays.txt
    /// </summary>
    internal sealed class BoBoRelayStore
    {
        internal const int MaxRelays = 12;

        internal static string FilePath
        {
            get { return Path.Combine(RecentStore.DirPath, "relays.txt"); }
        }

        internal readonly List<BoBoCustomRelay> Relays = new List<BoBoCustomRelay>();
        internal string ActiveRelayId = "";
        internal BoBoRelaySelectionMode SelectionMode = BoBoRelaySelectionMode.DynamicLoadBalanced;

        internal BoBoCustomRelay ActiveRelay
        {
            get
            {
                if (Relays.Count == 0) return null;
                if (!string.IsNullOrEmpty(ActiveRelayId))
                {
                    for (int i = 0; i < Relays.Count; i++)
                    {
                        if (string.Equals(Relays[i].Id, ActiveRelayId, StringComparison.OrdinalIgnoreCase))
                            return Relays[i];
                    }
                }
                return Relays[0];
            }
        }

        internal static string FormatHostForUrl(string host)
        {
            string h = (host ?? "").Trim();
            if (h.IndexOf(':') >= 0 && !h.StartsWith("[", StringComparison.Ordinal))
                return "[" + h + "]";
            return h;
        }

        internal static bool TryParseHealthUrl(string raw, string relayHost, out string normalized, out string error)
        {
            normalized = "";
            error = null;
            string text = (raw ?? "").Trim();
            if (text.Length == 0) return true;
            if (text.Length > 300)
            {
                error = "负载监控接口地址过长。";
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                {
                    error = "负载监控接口地址不能包含空格或控制字符。";
                    return false;
                }
            }

            if (text.StartsWith(":", StringComparison.Ordinal))
            {
                string portText = text.Substring(1);
                int hp;
                if (portText.Length == 0 || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out hp)
                    || hp < 1 || hp > 65535)
                {
                    error = "负载监控简写端口必须在 1–65535 之间（如 :49811）。";
                    return false;
                }
                string safeHost = (relayHost ?? "").Trim();
                if (safeHost.Length == 0)
                {
                    error = "使用 :端口 简写时需先填写有效的中转节点地址。";
                    return false;
                }
                normalized = "http://" + FormatHostForUrl(safeHost) + ":" + hp.ToString(CultureInfo.InvariantCulture) + "/bobo-relay/health";
                return true;
            }

            int purePort;
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out purePort)
                && purePort >= 1 && purePort <= 65535 && !string.IsNullOrEmpty(relayHost))
            {
                normalized = "http://" + FormatHostForUrl(relayHost) + ":" + purePort.ToString(CultureInfo.InvariantCulture) + "/bobo-relay/health";
                return true;
            }

            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                error = "负载监控地址需使用 http:// 或 https:// 开头，或简写为 :端口（如 :49811）。";
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
            {
                error = "负载监控 URL 格式不正确。";
                return false;
            }

            normalized = uri.AbsoluteUri;
            return true;
        }

        internal static bool TryParseEndpoint(string raw, out string host, out int port, out string error)
        {
            host = null;
            port = BoBoProtocol.DefaultTcpPort;
            error = null;

            string text = (raw ?? "").Trim();
            if (text.Length == 0)
            {
                error = "请填写兜底中转服务器地址（如 VPS 公网 IP 或域名:端口）。";
                return false;
            }
            if (text.Length > 260)
            {
                error = "中转服务器地址过长。";
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                {
                    error = "中转地址不能包含空格或换行符。";
                    return false;
                }
            }
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0 || text.IndexOf('/') >= 0
                || text.IndexOf('\\') >= 0 || text.IndexOf('?') >= 0 || text.IndexOf('#') >= 0)
            {
                error = "请直接输入 IP 或域名[:端口]，无需包含协议头或路径。";
                return false;
            }

            string hostPart;
            string portPart = null;

            if (text[0] == '[')
            {
                int closeIdx = text.IndexOf(']');
                if (closeIdx <= 1)
                {
                    error = "IPv6 中转地址格式不正确。";
                    return false;
                }
                hostPart = text.Substring(1, closeIdx - 1);
                if (closeIdx + 1 < text.Length)
                {
                    if (text[closeIdx + 1] != ':')
                    {
                        error = "IPv6 地址后若指定端口请使用 [IPv6]:端口。";
                        return false;
                    }
                    portPart = text.Substring(closeIdx + 2);
                }
            }
            else
            {
                int firstColon = text.IndexOf(':');
                int lastColon = text.LastIndexOf(':');
                if (firstColon >= 0 && firstColon == lastColon)
                {
                    hostPart = text.Substring(0, firstColon);
                    portPart = text.Substring(firstColon + 1);
                }
                else
                {
                    // Zero colons (IPv4 / hostname) or multiple colons (unbracketed IPv6 without explicit port)
                    hostPart = text;
                }
            }

            if (portPart != null)
            {
                int parsedPort;
                if (portPart.Length == 0 || !int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort)
                    || parsedPort < 1 || parsedPort > 65535)
                {
                    error = "中转端口必须在 1–65535 之间（留空则默认 49810）。";
                    return false;
                }
                port = parsedPort;
            }

            string normErr;
            string cleanHost = Engines.NormalizeHost(hostPart, out normErr);
            if (cleanHost == null)
            {
                error = string.IsNullOrEmpty(normErr) ? "中转主机地址格式不正确。" : normErr;
                return false;
            }

            host = cleanHost;
            return true;
        }

        internal bool TryAddOrUpdate(string rawName, string rawEndpoint, out BoBoCustomRelay saved, out string error)
        {
            return TryAddOrUpdate(rawName, rawEndpoint, "", 100, out saved, out error);
        }

        internal bool TryAddOrUpdate(string rawName, string rawEndpoint, string rawHealthUrl, int weight, out BoBoCustomRelay saved, out string error)
        {
            saved = null;
            string host;
            int port;
            if (!TryParseEndpoint(rawEndpoint, out host, out port, out error))
                return false;

            string cleanHealthUrl;
            if (!TryParseHealthUrl(rawHealthUrl, host, out cleanHealthUrl, out error))
                return false;

            int safeWeight = Math.Max(1, Math.Min(100, weight));
            string safeName = SanitizeName(rawName, host);
            for (int i = 0; i < Relays.Count; i++)
            {
                BoBoCustomRelay existing = Relays[i];
                if (string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) && existing.Port == port)
                {
                    existing.Name = safeName;
                    existing.HealthUrl = cleanHealthUrl;
                    existing.Weight = safeWeight;
                    existing.ConsecutiveFailures = 0;
                    existing.LastFailureUtcTicks = 0;
                    ActiveRelayId = existing.Id;
                    saved = existing;
                    return true;
                }
            }

            if (Relays.Count >= MaxRelays)
            {
                error = "最多可保存 " + MaxRelays + " 个自定义兜底中转节点，请先删除旧节点。";
                return false;
            }

            saved = new BoBoCustomRelay
            {
                Id = "relay_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = safeName,
                Host = host,
                Port = port,
                HealthUrl = cleanHealthUrl,
                Weight = safeWeight
            };
            Relays.Insert(0, saved);
            ActiveRelayId = saved.Id;
            return true;
        }

        internal void RecordConnectOutcome(string relayId, bool succeeded)
        {
            if (string.IsNullOrEmpty(relayId)) return;
            for (int i = 0; i < Relays.Count; i++)
            {
                BoBoCustomRelay r = Relays[i];
                if (!string.Equals(r.Id, relayId, StringComparison.OrdinalIgnoreCase)) continue;
                if (succeeded)
                {
                    r.ConsecutiveFailures = 0;
                    r.LastFailureUtcTicks = 0;
                    r.LastReachable = true;
                }
                else
                {
                    r.ConsecutiveFailures = Math.Min(20, r.ConsecutiveFailures + 1);
                    r.LastFailureUtcTicks = DateTime.UtcNow.Ticks;
                    r.LastReachable = false;
                }
                return;
            }
        }

        internal bool RemoveById(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            int removed = Relays.RemoveAll(delegate(BoBoCustomRelay item)
            {
                return string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase);
            });
            if (removed <= 0) return false;
            if (string.Equals(ActiveRelayId, id, StringComparison.OrdinalIgnoreCase))
                ActiveRelayId = Relays.Count > 0 ? Relays[0].Id : "";
            return true;
        }

        internal void SelectById(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                ActiveRelayId = "";
                return;
            }
            for (int i = 0; i < Relays.Count; i++)
            {
                if (string.Equals(Relays[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveRelayId = Relays[i].Id;
                    return;
                }
            }
        }

        internal string Serialize()
        {
            var sb = new StringBuilder();
            sb.AppendLine("v2");
            sb.AppendLine("mode=" + ((int)SelectionMode).ToString(CultureInfo.InvariantCulture));
            BoBoCustomRelay active = ActiveRelay;
            sb.AppendLine("active=" + (active != null ? active.Id : ""));
            for (int i = 0; i < Relays.Count; i++)
            {
                BoBoCustomRelay r = Relays[i];
                string safeHealth = (r.HealthUrl ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
                int safeWeight = Math.Max(1, Math.Min(100, r.Weight));
                sb.AppendLine("relay=" + r.Id
                    + "\t" + SanitizeName(r.Name, r.Host)
                    + "\t" + r.Host
                    + "\t" + r.Port.ToString(CultureInfo.InvariantCulture)
                    + "\t" + safeHealth
                    + "\t" + safeWeight.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        internal static BoBoRelayStore Parse(string content)
        {
            var store = new BoBoRelayStore();
            if (string.IsNullOrEmpty(content)) return store;

            string[] lines = content.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string requestedActive = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line == "v1" || line == "v2") continue;
                if (line.StartsWith("mode=", StringComparison.Ordinal))
                {
                    int modeInt;
                    if (int.TryParse(line.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out modeInt)
                        && modeInt >= (int)BoBoRelaySelectionMode.Manual
                        && modeInt <= (int)BoBoRelaySelectionMode.DynamicLoadBalanced)
                    {
                        store.SelectionMode = (BoBoRelaySelectionMode)modeInt;
                    }
                    continue;
                }
                if (line.StartsWith("active=", StringComparison.Ordinal))
                {
                    requestedActive = line.Substring(7).Trim();
                    continue;
                }
                if (!line.StartsWith("relay=", StringComparison.Ordinal)) continue;
                if (store.Relays.Count >= MaxRelays) continue;

                string[] parts = line.Substring(6).Split('\t');
                if (parts.Length < 4) continue;
                string id = parts[0].Trim();
                string name = parts[1].Trim();
                string hostRaw = parts[2].Trim();
                int port;
                if (!int.TryParse(parts[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    || port < 1 || port > 65535)
                    continue;
                string err;
                string cleanHost = Engines.NormalizeHost(hostRaw, out err);
                if (cleanHost == null) continue;
                if (string.IsNullOrEmpty(id)) id = "relay_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                string healthUrl = "";
                if (parts.Length >= 5)
                {
                    string hNorm;
                    string hErr;
                    if (TryParseHealthUrl(parts[4].Trim(), cleanHost, out hNorm, out hErr))
                        healthUrl = hNorm;
                }

                int weight = 100;
                if (parts.Length >= 6)
                {
                    int parsedWeight;
                    if (int.TryParse(parts[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedWeight))
                        weight = Math.Max(1, Math.Min(100, parsedWeight));
                }

                bool dup = false;
                for (int k = 0; k < store.Relays.Count; k++)
                {
                    if (string.Equals(store.Relays[k].Host, cleanHost, StringComparison.OrdinalIgnoreCase) && store.Relays[k].Port == port)
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup) continue;

                store.Relays.Add(new BoBoCustomRelay
                {
                    Id = id,
                    Name = SanitizeName(name, cleanHost),
                    Host = cleanHost,
                    Port = port,
                    HealthUrl = healthUrl,
                    Weight = weight
                });
            }

            if (!string.IsNullOrEmpty(requestedActive))
                store.SelectById(requestedActive);
            if (string.IsNullOrEmpty(store.ActiveRelayId) && store.Relays.Count > 0)
                store.ActiveRelayId = store.Relays[0].Id;

            return store;
        }

        internal static BoBoRelayStore Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch { }
            return new BoBoRelayStore();
        }

        internal void Save()
        {
            Directory.CreateDirectory(RecentStore.DirPath);
            File.WriteAllText(FilePath, Serialize(), new UTF8Encoding(false));
        }

        internal static async Task<Tuple<bool, double, string>> ProbeRelayAsync(string host, int port, int timeoutMs, CancellationToken ct)
        {
            int targetPort = port > 0 && port <= 65535 ? port : BoBoProtocol.DefaultTcpPort;
            int waitMs = Math.Max(500, Math.Min(10000, timeoutMs));
            var sw = Stopwatch.StartNew();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(waitMs);
                using (var client = new TcpClient())
                {
                    client.NoDelay = true;
                    try
                    {
                        using (cts.Token.Register(delegate
                        {
                            try { client.Close(); } catch { }
                        }))
                        {
                            await client.ConnectAsync(host, targetPort).ConfigureAwait(false);
                        }
                        sw.Stop();
                        double rttMs = Math.Max(0.5, sw.Elapsed.TotalMilliseconds);
                        return Tuple.Create(true, rttMs,
                            "中转入口可达（" + host + ":" + targetPort + " · TCP 延迟 " + Math.Round(rttMs) + " ms）");
                    }
                    catch (OperationCanceledException)
                    {
                        return Tuple.Create(false, 0.0, "探测超时（" + host + ":" + targetPort + "），请检查 VPS 防火墙与端口转发规则。");
                    }
                    catch (ObjectDisposedException)
                    {
                        return Tuple.Create(false, 0.0, "探测超时（" + host + ":" + targetPort + "），请检查 VPS 防火墙与端口转发规则。");
                    }
                    catch (SocketException ex)
                    {
                        return Tuple.Create(false, 0.0, "中转节点暂不可达：" + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        return Tuple.Create(false, 0.0, "中转探测失败：" + ex.Message);
                    }
                }
            }
        }

        private static string SanitizeName(string rawName, string fallbackHost)
        {
            string s = (rawName ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) return fallbackHost ?? "";
            if (s.Length > 28) s = s.Substring(0, 28).Trim();
            return s;
        }
    }
}
