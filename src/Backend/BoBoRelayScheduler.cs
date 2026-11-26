using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DualRemote
{
    internal sealed class BoBoRelayProbeSnapshot
    {
        internal BoBoCustomRelay Relay;
        internal bool Reachable;
        internal double RttMs;
        internal bool HasServerLoad;
        internal double ServerLoad;
        internal double CpuPct;
        internal int ActiveSessions;
        internal int MaxSessions;
        internal double BandwidthMbps;
        internal double MaxBandwidthMbps;
        internal bool Accepting = true;
        internal string Region = "";
        internal double Score = double.PositiveInfinity;
        internal string Summary = "";
    }

    /// <summary>
    /// 多中转节点延迟与服务器负载动态调度器：
    /// 1. 并发探测全部自建中转节点的 TCP 往返延迟（RTT）；
    /// 2. 并发拉取各节点负载健康接口（支持自定义 HealthUrl 或默认 端口+1 /bobo-relay/health）；
    /// 3. 按调度模式（动态负载+延迟 / 最低延迟 / 手动固定）、熔断退避与权重综合打分排序；
    /// 4. 支持会话期间过载或高延迟无感迁移决策（带滞后防抖）。
    /// </summary>
    internal static class BoBoRelayScheduler
    {
        internal static string ResolveHealthUrl(BoBoCustomRelay relay)
        {
            if (relay == null) return "";
            string explicitUrl = (relay.HealthUrl ?? "").Trim();
            if (explicitUrl.Length > 0) return explicitUrl;
            if (relay.Port > 0 && relay.Port < 65535 && !string.IsNullOrWhiteSpace(relay.Host))
            {
                return "http://" + BoBoRelayStore.FormatHostForUrl(relay.Host)
                    + ":" + (relay.Port + 1).ToString(CultureInfo.InvariantCulture)
                    + "/bobo-relay/health";
            }
            return "";
        }

        internal static bool ParseHealthJson(
            string json,
            out bool ok,
            out bool accepting,
            out double load,
            out double cpuPct,
            out int activeSessions,
            out int maxSessions,
            out double bwMbps,
            out double maxBwMbps,
            out string region)
        {
            ok = false;
            accepting = true;
            load = 0.0;
            cpuPct = 0.0;
            activeSessions = 0;
            maxSessions = 0;
            bwMbps = 0.0;
            maxBwMbps = 0.0;
            region = "";

            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 };
                var dict = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (dict == null) return false;

                ok = ReadBool(dict, "ok", true);
                accepting = ReadBool(dict, "accepting", ok) && ok;
                cpuPct = Math.Max(0.0, ReadDouble(dict, "cpuPct", 0.0));
                activeSessions = Math.Max(0, ReadInt(dict, "activeSessions", 0));
                maxSessions = Math.Max(0, ReadInt(dict, "maxSessions", 0));
                bwMbps = Math.Max(0.0, ReadDouble(dict, "bandwidthMbps", 0.0));
                maxBwMbps = Math.Max(0.0, ReadDouble(dict, "maxBandwidthMbps", 0.0));
                region = ReadString(dict, "region", "");

                double rawLoad;
                if (TryReadDouble(dict, "load", out rawLoad))
                {
                    if (rawLoad > 1.0 && rawLoad <= 100.0) rawLoad /= 100.0;
                    load = Clamp01(rawLoad);
                }
                else
                {
                    double derived = 0.0;
                    if (cpuPct > 0.0)
                        derived = Math.Max(derived, cpuPct / 100.0);
                    if (maxSessions > 0)
                        derived = Math.Max(derived, (double)activeSessions / (double)maxSessions);
                    if (maxBwMbps > 0.0)
                        derived = Math.Max(derived, bwMbps / maxBwMbps);
                    load = Clamp01(derived);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static double ComputeScore(
            BoBoCustomRelay relay,
            BoBoRelayProbeSnapshot snap,
            BoBoRelaySelectionMode mode,
            long nowUtcTicks)
        {
            if (snap == null || !snap.Reachable)
                return double.PositiveInfinity;

            double rtt = Math.Max(0.5, snap.RttMs);
            double drainPenalty = snap.Accepting ? 0.0 : 2000.0;
            double backoff = 0.0;
            if (relay != null && relay.ConsecutiveFailures > 0)
            {
                long elapsedTicks = nowUtcTicks - relay.LastFailureUtcTicks;
                if (elapsedTicks >= 0 && elapsedTicks < TimeSpan.FromSeconds(60).Ticks)
                    backoff = 120.0 * Math.Min(5, relay.ConsecutiveFailures);
            }

            if (mode == BoBoRelaySelectionMode.Manual)
                return rtt + drainPenalty;

            if (mode == BoBoRelaySelectionMode.LowestLatency)
                return rtt + drainPenalty + backoff;

            double l = snap.HasServerLoad ? Clamp01(snap.ServerLoad) : 0.25;
            double loadPenalty = (l * 60.0) + (l > 0.70 ? (l - 0.70) * 400.0 : 0.0);
            bool saturated = !snap.Accepting
                || (snap.HasServerLoad && l >= 0.98)
                || (snap.MaxSessions > 0 && snap.ActiveSessions >= snap.MaxSessions);
            double saturationPenalty = saturated ? 2000.0 : 0.0;
            int w = relay != null ? Math.Max(1, Math.Min(100, relay.Weight)) : 100;
            double weightPenalty = (100 - w) * 0.5;

            return rtt + loadPenalty + saturationPenalty + backoff + weightPenalty;
        }

        internal static async Task<BoBoRelayProbeSnapshot> ProbeRelayWithTelemetryAsync(
            BoBoCustomRelay relay,
            int tcpTimeoutMs,
            int healthTimeoutMs,
            BoBoRelaySelectionMode mode,
            CancellationToken ct)
        {
            var snap = new BoBoRelayProbeSnapshot { Relay = relay };
            if (relay == null) return snap;

            int effectiveTcpMs = Math.Max(400, Math.Min(10000, tcpTimeoutMs));
            int effectiveHealthMs = Math.Max(400, Math.Min(900, healthTimeoutMs));
            string healthUrl = ResolveHealthUrl(relay);

            Task<Tuple<bool, double, string>> tcpTask = BoBoRelayStore.ProbeRelayAsync(
                relay.Host, relay.Port, effectiveTcpMs, ct);
            Task<string> healthTask = string.IsNullOrEmpty(healthUrl)
                ? Task.FromResult("")
                : TryFetchHealthJsonAsync(healthUrl, effectiveHealthMs, ct);

            await Task.WhenAll(tcpTask, healthTask).ConfigureAwait(false);

            Tuple<bool, double, string> tcpRes = await tcpTask.ConfigureAwait(false);
            string healthJson = await healthTask.ConfigureAwait(false);

            snap.Reachable = tcpRes.Item1;
            snap.RttMs = tcpRes.Item2;

            bool ok, accepting;
            double load, cpuPct, bwMbps, maxBwMbps;
            int activeSessions, maxSessions;
            string region;
            if (ParseHealthJson(healthJson, out ok, out accepting, out load, out cpuPct, out activeSessions, out maxSessions, out bwMbps, out maxBwMbps, out region))
            {
                snap.HasServerLoad = true;
                snap.ServerLoad = load;
                snap.CpuPct = cpuPct;
                snap.ActiveSessions = activeSessions;
                snap.MaxSessions = maxSessions;
                snap.BandwidthMbps = bwMbps;
                snap.MaxBandwidthMbps = maxBwMbps;
                snap.Accepting = accepting;
                snap.Region = region;
            }
            else
            {
                snap.HasServerLoad = false;
                snap.Accepting = true;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            snap.Score = ComputeScore(relay, snap, mode, nowTicks);

            relay.LastProbeUtcTicks = nowTicks;
            relay.LastReachable = snap.Reachable;
            relay.LastRttMs = snap.RttMs;
            relay.HasServerLoad = snap.HasServerLoad;
            relay.LastServerLoad = snap.ServerLoad;
            relay.LastCpuPct = snap.CpuPct;
            relay.LastActiveSessions = snap.ActiveSessions;
            relay.LastMaxSessions = snap.MaxSessions;
            relay.LastBandwidthMbps = snap.BandwidthMbps;
            relay.LastMaxBandwidthMbps = snap.MaxBandwidthMbps;
            relay.LastAccepting = snap.Accepting;
            relay.LastRegion = snap.Region;
            relay.LastScore = snap.Score;
            if (snap.Reachable && snap.Accepting)
            {
                relay.ConsecutiveFailures = 0;
                relay.LastFailureUtcTicks = 0;
            }

            if (!snap.Reachable)
            {
                snap.Summary = tcpRes.Item3;
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append(relay.DisplayLabel);
                sb.Append(" · 延迟 ");
                sb.Append(Math.Max(1, (int)Math.Round(snap.RttMs)).ToString(CultureInfo.InvariantCulture));
                sb.Append(" ms");
                if (snap.HasServerLoad)
                {
                    int pct = (int)Math.Round(snap.ServerLoad * 100.0);
                    if (!snap.Accepting)
                        sb.Append(" · 节点维护/拒收中");
                    else
                        sb.Append(" · 负载 ").Append(pct.ToString(CultureInfo.InvariantCulture)).Append('%');

                    if (snap.MaxSessions > 0 || snap.CpuPct > 0.5)
                    {
                        sb.Append(" (");
                        bool needComma = false;
                        if (snap.CpuPct > 0.5)
                        {
                            sb.Append("CPU ").Append(Math.Round(snap.CpuPct).ToString(CultureInfo.InvariantCulture)).Append('%');
                            needComma = true;
                        }
                        if (snap.MaxSessions > 0)
                        {
                            if (needComma) sb.Append(", ");
                            sb.Append(snap.ActiveSessions.ToString(CultureInfo.InvariantCulture))
                              .Append('/')
                              .Append(snap.MaxSessions.ToString(CultureInfo.InvariantCulture))
                              .Append(" 会话");
                        }
                        sb.Append(')');
                    }
                }
                else
                {
                    sb.Append(" · 仅 TCP 测速（未部署负载接口）");
                }
                snap.Summary = sb.ToString();
            }

            return snap;
        }

        internal static async Task<List<BoBoRelayProbeSnapshot>> ProbeAndRankAllAsync(
            BoBoRelayStore store,
            int tcpTimeoutMs,
            CancellationToken ct)
        {
            var results = new List<BoBoRelayProbeSnapshot>();
            if (store == null || store.Relays == null || store.Relays.Count == 0)
                return results;

            var tasks = new List<Task<BoBoRelayProbeSnapshot>>(store.Relays.Count);
            int healthTimeoutMs = Math.Min(900, Math.Max(450, tcpTimeoutMs / 2));
            for (int i = 0; i < store.Relays.Count; i++)
            {
                tasks.Add(ProbeRelayWithTelemetryAsync(
                    store.Relays[i], tcpTimeoutMs, healthTimeoutMs, store.SelectionMode, ct));
            }

            BoBoRelayProbeSnapshot[] snaps = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (int i = 0; i < snaps.Length; i++)
                results.Add(snaps[i]);

            string activeId = store.ActiveRelayId ?? "";
            BoBoRelaySelectionMode mode = store.SelectionMode;
            results.Sort(delegate(BoBoRelayProbeSnapshot a, BoBoRelayProbeSnapshot b)
            {
                if (a.Reachable != b.Reachable)
                    return a.Reachable ? -1 : 1;

                if (mode == BoBoRelaySelectionMode.Manual && a.Reachable && b.Reachable)
                {
                    bool aActive = a.Relay != null && string.Equals(a.Relay.Id, activeId, StringComparison.OrdinalIgnoreCase);
                    bool bActive = b.Relay != null && string.Equals(b.Relay.Id, activeId, StringComparison.OrdinalIgnoreCase);
                    if (aActive != bActive) return aActive ? -1 : 1;
                }

                if (a.Accepting != b.Accepting)
                    return a.Accepting ? -1 : 1;

                int sc = a.Score.CompareTo(b.Score);
                if (sc != 0) return sc;
                return a.RttMs.CompareTo(b.RttMs);
            });

            return results;
        }

        internal static bool ShouldMigrateActiveRelay(
            BoBoRelayProbeSnapshot current,
            BoBoRelayProbeSnapshot candidate,
            BoBoRelaySelectionMode mode)
        {
            if (mode == BoBoRelaySelectionMode.Manual) return false;
            if (candidate == null || !candidate.Reachable || !candidate.Accepting) return false;
            if (current == null || !current.Reachable || !current.Accepting) return true;

            bool highLoad = current.HasServerLoad && current.ServerLoad >= 0.85;
            bool highRtt = current.RttMs >= 130.0;
            if (!highLoad && !highRtt) return false;

            return candidate.Score + 35.0 < current.Score;
        }

        private static async Task<string> TryFetchHealthJsonAsync(string url, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                return await Task.Run(delegate
                {
                    if (ct.IsCancellationRequested) return "";
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Proxy = null;
                    req.KeepAlive = false;
                    req.Timeout = timeoutMs;
                    req.ReadWriteTimeout = timeoutMs;
                    req.AllowAutoRedirect = false;
                    using (ct.Register(delegate { try { req.Abort(); } catch { } }))
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode != HttpStatusCode.OK) return "";
                        using (Stream s = resp.GetResponseStream())
                        using (var sr = new StreamReader(s, Encoding.UTF8))
                        {
                            char[] buf = new char[16 * 1024];
                            int n = sr.Read(buf, 0, buf.Length);
                            return n > 0 ? new string(buf, 0, n) : "";
                        }
                    }
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                return "";
            }
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v) || v <= 0.0) return 0.0;
            if (v >= 1.0) return 1.0;
            return v;
        }

        private static bool ReadBool(IDictionary<string, object> d, string key, bool fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            try { return Convert.ToBoolean(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static int ReadInt(IDictionary<string, object> d, string key, int fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            try { return Convert.ToInt32(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double ReadDouble(IDictionary<string, object> d, string key, double fallback)
        {
            double v;
            return TryReadDouble(d, key, out v) ? v : fallback;
        }

        private static bool TryReadDouble(IDictionary<string, object> d, string key, out double val)
        {
            val = 0.0;
            if (d == null || !d.ContainsKey(key) || d[key] == null) return false;
            try
            {
                val = Convert.ToDouble(d[key], CultureInfo.InvariantCulture);
                return !double.IsNaN(val) && !double.IsInfinity(val);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadString(IDictionary<string, object> d, string key, string fallback)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return fallback;
            return Convert.ToString(d[key], CultureInfo.InvariantCulture) ?? fallback;
        }
    }
}
