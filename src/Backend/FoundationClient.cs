using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DualRemote
{
    /// <summary>串流客户端选择结果：标准 Moonlight / 增强 V+ / 都不可用。</summary>
    internal enum StreamEngine
    {
        Unavailable,
        Official,
        Enhanced,
    }

    /// <summary>
    /// 可选的 Moonlight V+ 旁路客户端。固定使用 engines\MoonlightVPlus\Moonlight.exe，
    /// 不接受外部 exe/URL；只在用户明确点击时启动，并在启动前安全配置该便携目录的 Sunshine ABR。
    /// </summary>
    internal static class FoundationClient
    {
        internal const string EngineRelativeDir = "engines\\MoonlightVPlus";
        internal const string EngineRelativeExe = EngineRelativeDir + "\\Moonlight.exe";
        internal const string BaseMoonlightRelativeDir = "engines\\Moonlight";
        internal const string RuntimeRelativeDir = "runtime\\MoonlightVPlus";
        internal const string PortableMarker = "portable.dat";
        internal const string RuntimeVersionFile = ".dualremote-runtime";
        internal const string SettingsMigrationMarker = ".dualremote-settings-migrated";
        internal const string SettingsFolder = "Moonlight Game Streaming Project";
        internal const string SettingsFile = "Moonlight.ini";
        internal const string AbrKey = "sunshineabr";
        internal const int PairPollIntervalMs = 400;
        internal const int PairTimeoutMs = 20000;
        private const int MaxSettingsBytes = 2 * 1024 * 1024;
        private static readonly object RuntimeGate = new object();
        private static readonly object AppCacheGate = new object();
        private static readonly Dictionary<string, string> ResolvedAppCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string AppDir
        {
            get { return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
        }

        /// <summary>随包只读来源目录；绝不在其中保存用户设置或凭据。</summary>
        internal static string BundledEngineDir
        {
            get { return Path.Combine(AppDir, EngineRelativeDir); }
        }

        internal static string BundledEngineExe
        {
            get { return Path.Combine(BundledEngineDir, "Moonlight.exe"); }
        }

        /// <summary>用户稳定的 V+ 运行目录；升级解压到新目录时仍复用这里的身份和证书。</summary>
        internal static string EngineDir
        {
            get { return Path.Combine(RecentStore.DirPath, RuntimeRelativeDir); }
        }

        internal static string EngineExe
        {
            get { return Path.Combine(EngineDir, "Moonlight.exe"); }
        }

        internal static string BaseSettingsPath
        {
            get
            {
                return Path.Combine(Path.Combine(AppDir, BaseMoonlightRelativeDir), SettingsFolder, SettingsFile);
            }
        }

        internal static string BundledSettingsPath
        {
            get { return Path.Combine(BundledEngineDir, SettingsFolder, SettingsFile); }
        }

        internal static string SettingsDir
        {
            get { return Path.Combine(EngineDir, SettingsFolder); }
        }

        internal static string SettingsPath
        {
            get { return Path.Combine(SettingsDir, SettingsFile); }
        }

        /// <summary>V+ 可用：exe 存在且同目录有便携标记 portable.dat；只查固定路径，不读外部配置。</summary>
        internal static bool IsAvailable
        {
            get
            {
                return IsPortableLayout(EngineDir) || IsPortableLayout(BundledEngineDir);
            }
        }

        internal static bool ShouldRefreshRuntime(string bundledFingerprint, string installedFingerprint)
        {
            if (string.IsNullOrEmpty(bundledFingerprint)) return false;
            return !string.Equals(bundledFingerprint, installedFingerprint, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按本机实际文件选择引擎；只查固定路径，不搜索其他目录。</summary>
        internal static StreamEngine SelectEngine(bool preferEnhanced)
        {
            return SelectEngine(Engines.MoonlightPresent, IsAvailable, preferEnhanced);
        }

        /// <summary>
        /// 纯选择逻辑（GUI、配对、串流共用）：
        /// 明确优先 V+ 且 V+ 可用 → Enhanced；标准版存在 → Official；仅 V+ → Enhanced；否则 Unavailable。
        /// ABR 不在此决定，仍由 Launch 的显式 useAbr 控制。
        /// </summary>
        internal static StreamEngine SelectEngine(bool officialAvailable, bool enhancedAvailable, bool preferEnhanced)
        {
            if (preferEnhanced && enhancedAvailable) return StreamEngine.Enhanced;
            if (officialAvailable) return StreamEngine.Official;
            if (enhancedAvailable) return StreamEngine.Enhanced;
            return StreamEngine.Unavailable;
        }

        /// <summary>
        /// 启动 Moonlight V+：pair=true 时走原生配对（窗口显示 PIN，由主机侧确认）；
        /// 否则按精确宽高/帧率/码率发起串流。输入校验与 Engines 的同范围一致。
        /// </summary>
        internal static bool Launch(string host, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool useAbr, bool pair,
            out string message)
        {
            return Launch(host, width, height, fps, bitrateKbps, showStats, playOnHost, windowed,
                useHevc, useAbr, true, false, pair, out message);
        }

        internal static bool Launch(string host, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool useAbr,
            bool disableVsync, bool framePacing, bool pair, out string message)
        {
            return LaunchWithApp(host, null, width, height, fps, bitrateKbps, showStats, playOnHost,
                windowed, useHevc, useAbr, disableVsync, framePacing, pair, out message);
        }

        internal static bool LaunchWithApp(string host, string appName, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool useAbr,
            bool disableVsync, bool framePacing, bool pair, out string message)
        {
            return LaunchWithApp(host, appName, width, height, fps, bitrateKbps, showStats, playOnHost,
                windowed, useHevc, useAbr, disableVsync, framePacing, true, true, pair, out message);
        }

        internal static bool LaunchWithApp(string host, string appName, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool useAbr,
            bool disableVsync, bool framePacing, bool absoluteMouse, bool quitAfter, bool pair, out string message)
        {
            message = null;
            if (!IsAvailable)
            {
                message = "未找到可用的 Moonlight V+ 组件（需要 Moonlight.exe 与同目录 portable.dat）：\r\n"
                        + EngineExe + "\r\n请确认发行包包含完整的 engines\\MoonlightVPlus 目录。";
                return false;
            }
            if (!TryPrepareRuntime(out message)) return false;
            string cleanHost = Engines.NormalizeHost(host, out message);
            if (cleanHost == null) return false;

            string args;
            string resolvedApp = Engines.MoonlightAppName;
            if (pair)
            {
                args = Engines.BuildMoonlightPairArgs(cleanHost);
            }
            else
            {
                string geometryError;
                if (!TryValidateGeometry(width, height, fps, bitrateKbps, out geometryError))
                {
                    message = geometryError;
                    return false;
                }
                if (!ApplyAbrSetting(useAbr, out message)) return false;
                resolvedApp = string.IsNullOrWhiteSpace(appName)
                    ? ResolveDesktopAppName(cleanHost)
                    : Engines.NormalizeAppName(appName);
                args = Engines.BuildMoonlightStreamArgsWithApp(cleanHost, resolvedApp, width, height, fps, bitrateKbps,
                    showStats, playOnHost, windowed, useHevc, disableVsync, framePacing, absoluteMouse, quitAfter);
            }

            try
            {
                using (Engines.StartEngine(EngineExe, args)) { }
            }
            catch (Exception ex)
            {
                message = "启动 Moonlight V+ 失败：" + ex.Message + "\r\n组件路径：" + EngineExe;
                return false;
            }
            if (pair)
            {
                message = "已在 Moonlight V+ 中发起配对：请把窗口中显示的 4 位 PIN 输入到被控端配对页。";
            }
            else
            {
                message = "已提交 Moonlight V+ 串流请求（" + resolvedApp + " · " + width + "×" + height + " · " + fps + "fps · "
                        + (useHevc ? "HEVC" : "H.264") + " · " + (bitrateKbps / 1000) + " Mbps · "
                        + (useAbr ? "ABR 自适应开" : "固定码率") + (disableVsync ? " · 极速低延迟" : "")
                        + "）。等待客户端首帧，启动进程不代表串流已经稳定。";
            }
            return true;
        }

        /// <summary>
        /// 在内置 Moonlight V+ 中发起带 PIN 的配对，隐藏启动，不阻塞 UI。
        /// 配对成功以 INI 的 [hosts] 记录为准，而不是进程退出码；本方法只负责启动并立即返回。
        /// 调用方必须在配对码有效期内用 IsHostPaired 轮询，结束后关闭返回的进程。
        /// </summary>
        internal static bool StartPairWithPin(string host, string pin, out Process process, out string message)
        {
            process = null;
            message = null;
            string cleanHost = Engines.NormalizeHost(host, out message);
            if (cleanHost == null) return false;
            string args = Engines.BuildMoonlightPairArgs(cleanHost, pin, out message);
            if (args == null) return false;
            if (!IsAvailable)
            {
                message = "未找到可用的 Moonlight V+ 组件（需要 Moonlight.exe 与同目录 portable.dat）：\r\n"
                        + EngineExe + "\r\n请确认发行包包含完整的 engines\\MoonlightVPlus 目录。";
                return false;
            }
            if (!TryPrepareRuntime(out message)) return false;

            // V+ 的 QSettings 共享同一便携 INI。任何同路径 Moonlight 已在运行时，
            // 都不能再启动第二个配对实例，更不能结束用户进程。
            if (IsPathEngineRunning(out message)) return false;

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = EngineExe;
                info.Arguments = args;
                info.WorkingDirectory = EngineDir;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                process = Process.Start(info);
                if (process == null)
                {
                    message = "启动 Moonlight V+ 配对进程失败，请重试。";
                    return false;
                }
                message = "已在本机后台启动 V+ 配对进程；启动成功不代表授权完成，"
                        + "须等待主机确认并在稳定运行目录写入同编号 srvcert。";
                return true;
            }
            catch (Exception ex)
            {
                if (process != null)
                {
                    try { process.Dispose(); } catch { }
                    process = null;
                }
                message = "启动 Moonlight V+ 配对失败：" + ex.Message + "\r\n组件路径：" + EngineExe;
                return false;
            }
        }

        /// <summary>
        /// 只读稳定运行目录里的 Moonlight.ini。[hosts] 中同一编号必须有非空 srvcert，
        /// 且 manualaddress / remoteaddress / ipv6address / localaddress 之一与目标等价；
        /// 显示名称不构成配对身份。没有 INI 是正常首连状态，返回 readable=true、paired=false。
        /// </summary>
        internal static bool IsHostPaired(string host, out bool paired, out string message)
        {
            return IsHostPaired(host, "", out paired, out message);
        }

        internal static bool IsHostPaired(string host, string hostName, out bool paired, out string message)
        {
            paired = false;
            message = null;
            string cleanHost = Engines.NormalizeHost(host, out message);
            if (cleanHost == null) return false;

            string prepareMessage;
            if (!EnsureSettingsMigrated(out prepareMessage))
            {
                message = prepareMessage;
                return false;
            }
            bool settingsExists = File.Exists(SettingsPath);
            string iniText = null;
            try
            {
                if (settingsExists)
                {
                    bool hasBom;
                    iniText = ReadSettingsText(out hasBom);
                }
            }
            catch (Exception ex)
            {
                message = "无法读取 Moonlight 主机记录：" + ex.Message;
                return false;
            }
            return TryEvaluatePairedSettings(settingsExists, iniText, cleanHost, hostName,
                out paired, out message);
        }

        /// <summary>纯判定入口；供自检覆盖“无 INI 正常未配对”和“有记录但无证书不配对”。</summary>
        internal static bool TryEvaluatePairedSettings(bool settingsExists, string iniText,
            string host, string hostName, out bool paired, out string message)
        {
            paired = false;
            message = null;
            string cleanHost = Engines.NormalizeHost(host, out message);
            if (cleanHost == null) return false;
            if (!settingsExists)
            {
                message = "尚未找到 Moonlight 主机记录，目标电脑还没有完成首次配对。";
                return true;
            }
            if (string.IsNullOrEmpty(iniText))
            {
                message = "Moonlight 主机记录为空，目标电脑还没有完成首次配对。";
                return true;
            }
            paired = ContainsPairedHost(iniText, cleanHost, hostName);
            if (!paired) message = "未在 Moonlight 主机记录中找到带有效 srvcert 的目标地址，尚未完成配对。";
            return true;
        }

        /// <summary>解析 INI 的 [hosts] 节；只把同编号且有非空 srvcert 的地址记录视为授权实据。</summary>
        internal static bool ContainsPairedHost(string iniText, string host)
        {
            return ContainsPairedHost(iniText, host, "");
        }

        internal static bool ContainsPairedHost(string iniText, string host, string hostName)
        {
            if (string.IsNullOrEmpty(iniText)) return false;
            string error;
            string normalHost = Engines.NormalizeHost(host, out error);
            if (normalHost == null) return false;

            Dictionary<int, Dictionary<string, string>> records = ReadHostsSection(iniText);
            string[] addressFields = new string[] { "manualaddress", "remoteaddress", "ipv6address", "localaddress" };
            foreach (Dictionary<string, string> record in records.Values)
            {
                string serviceCertificate;
                if (!record.TryGetValue("srvcert", out serviceCertificate)
                    || !HasCertificateCandidate(serviceCertificate))
                    continue;
                foreach (string field in addressFields)
                {
                    string value;
                    if (record.TryGetValue(field, out value) && AddressMatches(value, normalHost))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 自动解析远程已配对 Sunshine 主机上的桌面应用名（兼容系统安装版中文「桌面」、随包版英文「Desktop」及自定义名称）。
        /// 优先读取 Moonlight.ini 已缓存的该主机应用列表（0 延迟）；若首次配对尚未缓存应用列表，则无窗口调用 `Moonlight.exe list` 同步拉取。
        /// </summary>
        internal static string ResolveDesktopAppName(string host)
        {
            string err;
            string cleanHost = Engines.NormalizeHost(host, out err);
            if (cleanHost == null) return Engines.MoonlightAppName;

            lock (AppCacheGate)
            {
                string cached;
                if (ResolvedAppCache.TryGetValue(cleanHost, out cached) && !string.IsNullOrEmpty(cached))
                    return cached;
            }

            string prepErr;
            TryPrepareRuntime(out prepErr);
            EnsureSettingsMigrated(out prepErr);

            List<string> iniApps = ReadAppsFromIniFile(SettingsPath, cleanHost);
            if (iniApps.Count > 0)
            {
                string pickedIni = Engines.SelectBestDesktopAppName(iniApps);
                lock (AppCacheGate) { ResolvedAppCache[cleanHost] = pickedIni; }
                return pickedIni;
            }

            string busyMsg;
            if (File.Exists(EngineExe) && !IsPathEngineRunning(out busyMsg))
            {
                // 首次配对后 INI 尚未写入 apps 列表时，留足 8.5s 兼容异地 DERP 中继与多网卡探测耗时
                List<string> liveApps = Engines.QueryMoonlightAppList(EngineExe, cleanHost, 8500);
                if (liveApps.Count > 0)
                {
                    string pickedLive = Engines.SelectBestDesktopAppName(liveApps);
                    lock (AppCacheGate) { ResolvedAppCache[cleanHost] = pickedLive; }
                    return pickedLive;
                }
                List<string> refreshedIni = ReadAppsFromIniFile(SettingsPath, cleanHost);
                if (refreshedIni.Count > 0)
                {
                    string pickedRefreshed = Engines.SelectBestDesktopAppName(refreshedIni);
                    lock (AppCacheGate) { ResolvedAppCache[cleanHost] = pickedRefreshed; }
                    return pickedRefreshed;
                }
            }

            return Engines.MoonlightAppName;
        }

        internal static List<string> ReadAppsFromIniFile(string iniPath, string host)
        {
            try
            {
                if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath))
                    return new List<string>();
                FileInfo fi = new FileInfo(iniPath);
                if (fi.Length <= 0 || fi.Length > MaxSettingsBytes)
                    return new List<string>();
                string text = File.ReadAllText(iniPath, Encoding.UTF8);
                return ReadHostAppsFromIniText(text, host);
            }
            catch
            {
                return new List<string>();
            }
        }

        internal static List<string> ReadHostAppsFromIniText(string iniText, string host)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(iniText)) return result;
            string error;
            string normalHost = Engines.NormalizeHost(host, out error);
            if (normalHost == null) return result;

            Dictionary<int, Dictionary<string, string>> records = ReadHostsSection(iniText);
            string[] addressFields = new string[] { "manualaddress", "remoteaddress", "ipv6address", "localaddress" };
            foreach (Dictionary<string, string> record in records.Values)
            {
                string serviceCertificate;
                if (!record.TryGetValue("srvcert", out serviceCertificate)
                    || !HasCertificateCandidate(serviceCertificate))
                    continue;

                bool matched = false;
                foreach (string field in addressFields)
                {
                    string value;
                    if (record.TryGetValue(field, out value) && AddressMatches(value, normalHost))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched) continue;

                SortedDictionary<int, string> indexedApps = new SortedDictionary<int, string>();
                foreach (KeyValuePair<string, string> kv in record)
                {
                    // key format inside [hosts]: apps\<appIndex>\name
                    string k = kv.Key.Replace('/', '\\');
                    if (!k.StartsWith("apps\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!k.EndsWith("\\name", StringComparison.OrdinalIgnoreCase)) continue;
                    string mid = k.Substring(5, k.Length - 10);
                    int appIdx;
                    if (!int.TryParse(mid, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out appIdx))
                        continue;
                    string decoded = UnescapeQtIniString(kv.Value);
                    if (!string.IsNullOrWhiteSpace(decoded))
                        indexedApps[appIdx] = decoded.Trim();
                }
                foreach (string appName in indexedApps.Values)
                {
                    result.Add(appName);
                }
                if (result.Count > 0) return result;
            }
            return result;
        }

        /// <summary>
        /// 解码 Qt QSettings::IniFormat 转义字符串（例如 `\x684c\x9762`、`"@String(\x684c\x9762)"` 或 UTF-8 原文 `桌面`）。
        /// </summary>
        internal static string UnescapeQtIniString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            const string qtStringPrefix = "@String(";
            if (s.StartsWith(qtStringPrefix, StringComparison.OrdinalIgnoreCase) && s.EndsWith(")", StringComparison.Ordinal))
                s = s.Substring(qtStringPrefix.Length, s.Length - qtStringPrefix.Length - 1);

            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                char next = s[i + 1];
                if (next == 'x' || next == 'X' || next == 'u' || next == 'U')
                {
                    int maxDigits = (next == 'U') ? 8 : 4;
                    int hexStart = i + 2;
                    int count = 0;
                    int code = 0;
                    while (count < maxDigits && hexStart + count < s.Length)
                    {
                        int hv = HexDigitValue(s[hexStart + count]);
                        if (hv < 0) break;
                        code = (code << 4) | hv;
                        count++;
                    }
                    if (count > 0)
                    {
                        if (code >= 0 && code <= 0xFFFF)
                            sb.Append((char)code);
                        else if (code <= 0x10FFFF)
                            sb.Append(char.ConvertFromUtf32(code));
                        i = hexStart + count;
                        continue;
                    }
                }
                switch (next)
                {
                    case '0': sb.Append('\0'); i += 2; break;
                    case 'a': sb.Append('\a'); i += 2; break;
                    case 'b': sb.Append('\b'); i += 2; break;
                    case 'f': sb.Append('\f'); i += 2; break;
                    case 'n': sb.Append('\n'); i += 2; break;
                    case 'r': sb.Append('\r'); i += 2; break;
                    case 't': sb.Append('\t'); i += 2; break;
                    case 'v': sb.Append('\v'); i += 2; break;
                    case '\\':
                    case '"':
                    case '\'':
                    case ';':
                    case '#':
                    case '=':
                        sb.Append(next);
                        i += 2;
                        break;
                    default:
                        sb.Append(next);
                        i += 2;
                        break;
                }
            }
            return sb.ToString();
        }

        private static int HexDigitValue(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return -1;
        }

        private static Dictionary<int, Dictionary<string, string>> ReadHostsSection(string iniText)
        {
            Dictionary<int, Dictionary<string, string>> records =
                new Dictionary<int, Dictionary<string, string>>();
            bool inHosts = false;
            string[] lines = iniText.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    inHosts = string.Equals(line, "[hosts]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inHosts || line[0] == '#' || line[0] == ';') continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                int slash = key.IndexOf('\\');
                if (slash <= 0 || slash == key.Length - 1) continue;
                int index;
                if (!int.TryParse(key.Substring(0, slash), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out index))
                    continue;
                string field = key.Substring(slash + 1).Trim();
                if (field.Length == 0) continue;
                Dictionary<string, string> record;
                if (!records.TryGetValue(index, out record))
                {
                    record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    records[index] = record;
                }
                record[field] = value;
            }
            return records;
        }

        private static bool AddressMatches(string rawValue, string normalHost)
        {
            string value = NormalizeHostForMatch(rawValue);
            string target = NormalizeHostForMatch(normalHost);
            if (value.Length == 0 || target.Length == 0) return false;

            IPAddress valueAddress;
            IPAddress targetAddress;
            bool valueIsAddress = IPAddress.TryParse(value, out valueAddress);
            bool targetIsAddress = IPAddress.TryParse(target, out targetAddress);
            if (valueIsAddress || targetIsAddress)
            {
                if (!valueIsAddress || !targetIsAddress) return false;
                valueAddress = AsComparableAddress(valueAddress);
                targetAddress = AsComparableAddress(targetAddress);
                if (valueAddress.AddressFamily != targetAddress.AddressFamily) return false;
                byte[] left = valueAddress.GetAddressBytes();
                byte[] right = targetAddress.GetAddressBytes();
                if (left.Length != right.Length) return false;
                for (int i = 0; i < left.Length; i++)
                {
                    if (left[i] != right[i]) return false;
                }
                return true;
            }

            if (value.EndsWith(".", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);
            if (target.EndsWith(".", StringComparison.Ordinal)) target = target.Substring(0, target.Length - 1);
            return string.Equals(value, target, StringComparison.OrdinalIgnoreCase);
        }

        private static IPAddress AsComparableAddress(IPAddress address)
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && address.IsIPv4MappedToIPv6)
                return address.MapToIPv4();
            return address;
        }

        private static string NormalizeHostForMatch(string value)
        {
            if (value == null) return "";
            string text = value.Trim();
            if (text.Length >= 2 && text[0] == '[' && text[text.Length - 1] == ']')
                text = text.Substring(1, text.Length - 2);
            return text.Trim();
        }

        private static bool HasCertificateCandidate(string value)
        {
            if (value == null) return false;
            string text = value.Trim();
            if (text.Length == 0) return false;
            const string prefix = "@ByteArray(";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.EndsWith(")", StringComparison.Ordinal))
            {
                text = text.Substring(prefix.Length, text.Length - prefix.Length - 1).Trim();
            }
            if (text.Length < 128) return false;
            return text.IndexOf("BEGIN CERTIFICATE", StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("END CERTIFICATE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>打开 Moonlight V+ 原生 GUI（不发起连接，也不修改设置）。</summary>
        internal static bool Open(out string message)
        {
            message = null;
            if (!IsAvailable)
            {
                message = "未找到可用的 Moonlight V+ 组件（需要 Moonlight.exe 与同目录 portable.dat）：\r\n"
                        + EngineExe + "\r\n请确认发行包包含完整的 engines\\MoonlightVPlus 目录。";
                return false;
            }
            if (!TryPrepareRuntime(out message)) return false;
            try
            {
                Engines.StartEngine(EngineExe, "");
            }
            catch (Exception ex)
            {
                message = "启动 Moonlight V+ 失败：" + ex.Message + "\r\n组件路径：" + EngineExe;
                return false;
            }
            message = "已打开 Moonlight V+：在应用列表中选择主机与「虚拟屏」，兼容范围由主机决定。";
            return true;
        }

        /// <summary>
        /// 只改 V+ 自己便携目录里的 sunshineabr（Qt6 INI 的 [General] 节），保留其他键。
        /// 引擎正在运行、或无法确认进程归属时不写设置并明确报错。
        /// </summary>
        internal static bool ApplyAbrSetting(bool enabled, out string message)
        {
            message = null;
            if (!IsPortableLayout(EngineDir))
            {
                if (!TryPrepareRuntime(out message)) return false;
            }
            if (!File.Exists(Path.Combine(EngineDir, PortableMarker)))
            {
                message = "Moonlight V+ 缺少 portable.dat；为避免写到非便携设置，已取消本次 ABR 配置。";
                return false;
            }

            try
            {
                bool exists = File.Exists(SettingsPath);
                bool hasBom = false;
                string text = exists ? ReadSettingsText(out hasBom) : "";
                bool current;
                if (exists && TryGetAbrValue(text, out current) && current == enabled)
                {
                    message = "Moonlight V+ 的 Sunshine ABR 已是" + (enabled ? "开启" : "关闭") + "状态，未改写设置。";
                    return true;
                }
                if (IsPathEngineRunning(out message)) return false;
                if (!exists && !enabled)
                {
                    message = "Moonlight V+ 是全新便携目录；Sunshine ABR 默认关闭，无需写入设置。";
                    return true;
                }
                string updated = SetAbrValue(text, enabled);
                if (!exists || !string.Equals(updated, text, StringComparison.Ordinal))
                {
                    // 防竞争：写回前再确认没有 V+ 进程正在使用同一份便携设置。
                    if (IsPathEngineRunning(out message)) return false;
                    WriteSettingsAtomic(updated, hasBom);
                }
                message = enabled ? "已开启 Moonlight V+ 的 Sunshine ABR。" : "已关闭 Moonlight V+ 的 Sunshine ABR。";
                return true;
            }
            catch (Exception ex)
            {
                message = "写入 Moonlight V+ 设置失败：" + ex.Message;
                return false;
            }
        }

        private static bool TryValidateGeometry(int width, int height, int fps, int bitrateKbps, out string message)
        {
            message = null;
            if (width % 2 != 0 || height % 2 != 0)
            {
                message = "串流宽高必须是偶数。";
                return false;
            }
            if (width <= Engines.MinimalStreamWidth || width >= Engines.MaximalStreamWidth)
            {
                message = "宽度需大于 " + Engines.MinimalStreamWidth + " 且小于 " + Engines.MaximalStreamWidth + "。";
                return false;
            }
            if (height <= Engines.MinimalStreamHeight || height >= Engines.MaximalStreamHeight)
            {
                message = "高度需大于 " + Engines.MinimalStreamHeight + " 且小于 " + Engines.MaximalStreamHeight + "。";
                return false;
            }
            if (fps <= Engines.MinimalStreamFps || fps >= Engines.MaximalStreamFps)
            {
                message = "帧率需大于 " + Engines.MinimalStreamFps + " 且小于 " + Engines.MaximalStreamFps + "。";
                return false;
            }
            if (bitrateKbps <= Engines.MinimalStreamBitrateKbps || bitrateKbps >= Engines.MaximalStreamBitrateKbps)
            {
                message = "码率需大于 " + Engines.MinimalStreamBitrateKbps + " 且小于 "
                        + Engines.MaximalStreamBitrateKbps + " Kbps。";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 把随包 V+ 程序镜像到稳定目录；只在首次或随包指纹变化时复制。
        /// 稳定目录不存在时才从旧 V+/official 便携目录迁移含有效 srvcert 的 INI；
        /// 已存在的 Moonlight.ini 与客户端身份永不覆盖。
        /// </summary>
        internal static bool TryPrepareRuntime(out string message)
        {
            message = null;
            lock (RuntimeGate)
            {
                try
                {
                    bool bundledReady = IsPortableLayout(BundledEngineDir);
                    bool localReady = IsPortableLayout(EngineDir);
                    if (!bundledReady && !localReady)
                    {
                        message = "未找到可用的 Moonlight V+ 组件，请确认完整发行包包含 engines\\MoonlightVPlus。";
                        return false;
                    }

                    if (bundledReady)
                    {
                        string fingerprint;
                        if (!TryComputeBundleFingerprint(out fingerprint, out message)) return false;
                        bool refresh = !localReady
                            || ShouldRefreshRuntime(fingerprint, ReadRuntimeVersion());
                        if (refresh)
                        {
                            if (localReady && IsPathEngineRunning(out message))
                            {
                                message = "检测到稳定运行目录中的 Moonlight V+ 正在使用，"
                                        + "程序版本更新将在退出后重试；本次未替换任何运行文件。";
                                return false;
                            }
                            CopyEngineRuntime(BundledEngineDir, EngineDir);
                            if (!IsPortableLayout(EngineDir))
                            {
                                message = "稳定运行目录缺少 Moonlight V+ 组件，请检查 "
                                        + EngineDir + " 的写入权限。";
                                return false;
                            }
                            if (!WriteRuntimeVersion(fingerprint, out message)) return false;
                        }
                    }

                    if (!IsPortableLayout(EngineDir))
                    {
                        message = "稳定运行目录缺少 Moonlight V+ 组件，请检查 "
                                + EngineDir + " 的写入权限。";
                        return false;
                    }
                    return EnsureSettingsMigrated(out message);
                }
                catch (Exception ex)
                {
                    message = "准备 Moonlight V+ 稳定运行目录失败：" + ex.Message;
                    return false;
                }
            }
        }

        private static bool EnsureSettingsMigrated(out string message)
        {
            message = null;
            if (File.Exists(SettingsPath))
            {
                string existing;
                if (TryReadTextBounded(SettingsPath, out existing) && HasAnyCertifiedHost(existing))
                    TryWriteSettingsMigrationMarker();
                return true;
            }
            if (File.Exists(Path.Combine(EngineDir, SettingsMigrationMarker))) return true;
            string source = FindLegacySettingsSource();
            if (source == null) return true;
            if (IsPathEngineRunning(out message))
            {
                message = "稳定运行目录中的 Moonlight V+ 正在使用，设置迁移将在退出后重试。";
                return false;
            }
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temp, true);
                    try
                    {
                        File.Move(temp, SettingsPath);
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(SettingsPath)) throw;
                    }
                    TryWriteSettingsMigrationMarker();
                    return true;
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch (Exception ex)
            {
                message = "迁移 Moonlight 便携设置失败：" + ex.Message;
                return false;
            }
        }

        private static void CopyEngineRuntime(string source, string target)
        {
            Directory.CreateDirectory(target);
            string sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            string targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase)) return;
            foreach (string path in Directory.GetFiles(sourceFull, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(sourceFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (ShouldSkipRuntimePath(relative)) continue;
                string destination = Path.Combine(targetFull, relative);
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                FileInfo sourceInfo = new FileInfo(path);
                if (File.Exists(destination))
                {
                    FileInfo targetInfo = new FileInfo(destination);
                    if (sourceInfo.Length == targetInfo.Length
                        && sourceInfo.LastWriteTimeUtc == targetInfo.LastWriteTimeUtc)
                        continue;
                }
                File.Copy(path, destination, true);
            }
        }

        private static bool IsPortableLayout(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            return File.Exists(Path.Combine(directory, "Moonlight.exe"))
                && File.Exists(Path.Combine(directory, PortableMarker));
        }

        private static bool TryComputeBundleFingerprint(out string fingerprint, out string message)
        {
            fingerprint = null;
            message = null;
            try
            {
                string sourceFull = Path.GetFullPath(BundledEngineDir).TrimEnd(Path.DirectorySeparatorChar);
                List<string> files = new List<string>(Directory.GetFiles(
                    sourceFull, "*", SearchOption.AllDirectories));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                StringBuilder manifest = new StringBuilder();
                foreach (string path in files)
                {
                    string relative = path.Substring(sourceFull.Length).TrimStart(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (ShouldSkipRuntimePath(relative)) continue;
                    FileInfo info = new FileInfo(path);
                    manifest.Append(relative.Replace('\\', '/')).Append('|')
                        .Append(info.Length).Append('|')
                        .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                manifest.Append("Moonlight.exe|sha256|").Append(ComputeFileSha256(BundledEngineExe));
                using (SHA256 hash = SHA256.Create())
                {
                    fingerprint = ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString())));
                }
                return true;
            }
            catch (Exception ex)
            {
                message = "计算随包 Moonlight V+ 版本失败：" + ex.Message;
                return false;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return ToHex(hash.ComputeHash(stream));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static string ReadRuntimeVersion()
        {
            try
            {
                string path = Path.Combine(EngineDir, RuntimeVersionFile);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.ASCII).Trim();
                return text.Length > 0 && text.Length <= 128 ? text : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool WriteRuntimeVersion(string fingerprint, out string message)
        {
            message = null;
            try
            {
                Directory.CreateDirectory(EngineDir);
                string path = Path.Combine(EngineDir, RuntimeVersionFile);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, fingerprint, new UTF8Encoding(false));
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                catch
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    throw;
                }
                return true;
            }
            catch (Exception ex)
            {
                message = "写入 Moonlight V+ 运行版本标记失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>只迁移含有效 srvcert 的旧便携 INI；未配对身份无需也无须从发行包搬运。</summary>
        private static string FindLegacySettingsSource()
        {
            List<string> candidates = new List<string>();
            AddLegacyCandidate(candidates, BundledSettingsPath);
            AddRunningEngineSettings(candidates);
            AddLegacyCandidate(candidates, BaseSettingsPath);
            AddLegacyCandidate(candidates, Path.Combine(AppDir, SettingsFolder, SettingsFile));

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                string text;
                if (TryReadTextBounded(candidate, out text) && HasAnyCertifiedHost(text))
                    return candidate;
            }
            return null;
        }

        private static void AddLegacyCandidate(List<string> candidates, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            foreach (string existing in candidates)
            {
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)) return;
            }
            candidates.Add(full);
        }

        private static void AddRunningEngineSettings(List<string> candidates)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("Moonlight");
                foreach (Process process in processes)
                {
                    string path = null;
                    try { path = process.MainModule == null ? null : process.MainModule.FileName; }
                    catch { path = null; }
                    if (string.IsNullOrEmpty(path)) continue;
                    string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (string.IsNullOrEmpty(directory)) continue;
                    if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(EngineDir),
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    AddLegacyCandidate(candidates,
                        Path.Combine(directory, SettingsFolder, SettingsFile));
                }
            }
            catch
            {
            }
            finally
            {
                if (processes != null)
                {
                    foreach (Process process in processes)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
        }

        private static bool TryReadTextBounded(string path, out string text)
        {
            text = null;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxSettingsBytes) return false;
                text = File.ReadAllText(path, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasAnyCertifiedHost(string iniText)
        {
            if (string.IsNullOrEmpty(iniText)) return false;
            Dictionary<int, Dictionary<string, string>> records = ReadHostsSection(iniText);
            foreach (Dictionary<string, string> record in records.Values)
            {
                string value;
                if (record.TryGetValue("srvcert", out value) && HasCertificateCandidate(value))
                    return true;
            }
            return false;
        }

        private static bool ShouldSkipRuntimePath(string relative)
        {
            string normalized = (relative ?? "").Replace('/', '\\').TrimStart('\\');
            if (normalized.Length == 0) return true;
            if (string.Equals(normalized, RuntimeVersionFile, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(normalized, SettingsMigrationMarker, StringComparison.OrdinalIgnoreCase)) return true;
            string first = normalized.Split('\\')[0];
            return string.Equals(first, SettingsFolder, StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "logs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "cache", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "crashes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "dumps", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "telemetry", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryWriteSettingsMigrationMarker()
        {
            try
            {
                string marker = Path.Combine(EngineDir, SettingsMigrationMarker);
                if (File.Exists(marker)) return;
                Directory.CreateDirectory(EngineDir);
                File.WriteAllText(marker, "1", new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        /// <summary>同路径 V+ 正在运行时拒绝新配对，避免共享 INI 被两个实例同时写。</summary>
        private static bool IsPathEngineRunning(out string message)
        {
            message = null;
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("Moonlight");
                bool unknownPath = false;
                foreach (Process process in processes)
                {
                    string path = null;
                    try { path = process.MainModule == null ? null : process.MainModule.FileName; }
                    catch { path = null; }
                    if (string.IsNullOrEmpty(path))
                    {
                        unknownPath = true;
                        continue;
                    }
                    if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(EngineExe),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        message = "同一稳定运行目录中的 Moonlight V+ 已在运行；"
                                + "为避免共享设置和凭据冲突，请先退出该窗口后重试。";
                        return true;
                    }
                }
                if (unknownPath)
                {
                    message = "检测到无法确认路径的 Moonlight 进程；为避免共享同一便携目录，"
                            + "请先退出该 Moonlight 后重试。";
                    return true;
                }
                return false;
            }
            catch
            {
                message = "无法检查 Moonlight 进程，已中止本次配对以避免共享设置冲突。";
                return true;
            }
            finally
            {
                if (processes != null)
                {
                    foreach (Process process in processes)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
        }

        private static string ReadSettingsText(out bool hasBom)
        {
            FileInfo info = new FileInfo(SettingsPath);
            if (!info.Exists || info.Length > MaxSettingsBytes)
                throw new InvalidDataException("Moonlight.ini 大小异常。");
            byte[] bytes = File.ReadAllBytes(SettingsPath);
            hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int offset = hasBom ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        private static void WriteSettingsAtomic(string text, bool withBom)
        {
            Directory.CreateDirectory(SettingsDir);
            string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(withBom));
            try
            {
                if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, null);
                else File.Move(temp, SettingsPath);
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private static bool TryGetAbrValue(string text, out bool enabled)
        {
            enabled = false;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                int separator = trimmed.IndexOf('=');
                if (separator <= 0) continue;
                if (!string.Equals(trimmed.Substring(0, separator).Trim(), AbrKey, StringComparison.OrdinalIgnoreCase)) continue;
                string value = trimmed.Substring(separator + 1).Trim();
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) { enabled = true; return true; }
                if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) { enabled = false; return true; }
                return false;
            }
            return false;
        }

        /// <summary>在 Qt6 INI 的 [General] 节里写 sunshineabr，保留其他节与键（其余内容原样）。</summary>
        private static string SetAbrValue(string text, bool enabled)
        {
            string newLine = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string valueLine = AbrKey + "=" + (enabled ? "true" : "false");
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));

            int general = -1;
            int end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']') continue;
                if (general >= 0) { end = i; break; }
                if (string.Equals(trimmed, "[General]", StringComparison.OrdinalIgnoreCase)) general = i;
            }

            if (general < 0)
            {
                int insertAt = lines.Count;
                if (insertAt > 0 && lines[insertAt - 1].Length == 0) insertAt--;
                lines.Insert(insertAt, "[General]");
                lines.Insert(insertAt + 1, valueLine);
            }
            else
            {
                int keyEnd = end;
                for (int i = general + 1; i < keyEnd; i++)
                {
                    string trimmed = lines[i].Trim();
                    int separator = trimmed.IndexOf('=');
                    if (separator > 0
                        && string.Equals(trimmed.Substring(0, separator).Trim(), AbrKey, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = valueLine;
                        return string.Join(newLine, lines.ToArray());
                    }
                }
                lines.Insert(general + 1, valueLine);
            }
            return string.Join(newLine, lines.ToArray());
        }

    }
}
