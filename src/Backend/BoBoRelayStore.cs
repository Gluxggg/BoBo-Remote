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
    internal sealed class BoBoCustomRelay
    {
        internal string Id = "";
        internal string Name = "";
        internal string Host = "";
        internal int Port = BoBoProtocol.DefaultTcpPort;

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

        public override string ToString()
        {
            return DisplayLabel;
        }
    }

    /// <summary>
    /// 用户自定义兜底中转（自建 VPS / 转发入口）本地存储：
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
            saved = null;
            string host;
            int port;
            if (!TryParseEndpoint(rawEndpoint, out host, out port, out error))
                return false;

            string safeName = SanitizeName(rawName, host);
            for (int i = 0; i < Relays.Count; i++)
            {
                BoBoCustomRelay existing = Relays[i];
                if (string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) && existing.Port == port)
                {
                    existing.Name = safeName;
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
                Port = port
            };
            Relays.Insert(0, saved);
            ActiveRelayId = saved.Id;
            return true;
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
            sb.AppendLine("v1");
            BoBoCustomRelay active = ActiveRelay;
            sb.AppendLine("active=" + (active != null ? active.Id : ""));
            for (int i = 0; i < Relays.Count; i++)
            {
                BoBoCustomRelay r = Relays[i];
                sb.AppendLine("relay=" + r.Id + "\t" + SanitizeName(r.Name, r.Host) + "\t" + r.Host + "\t" + r.Port.ToString(CultureInfo.InvariantCulture));
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
                if (line.Length == 0 || line == "v1") continue;
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
                    Port = port
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
