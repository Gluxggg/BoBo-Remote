using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        internal const string PortableMarker = "portable.dat";
        internal const string SettingsFolder = "Moonlight Game Streaming Project";
        internal const string SettingsFile = "Moonlight.ini";
        internal const string AbrKey = "sunshineabr";

        internal static string AppDir
        {
            get { return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
        }

        internal static string EngineDir
        {
            get { return Path.Combine(AppDir, EngineRelativeDir); }
        }

        internal static string EngineExe
        {
            get { return Path.Combine(AppDir, EngineRelativeExe); }
        }

        internal static string BaseSettingsPath
        {
            get
            {
                return Path.Combine(Path.Combine(AppDir, BaseMoonlightRelativeDir), SettingsFolder, SettingsFile);
            }
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
            get { return File.Exists(EngineExe) && File.Exists(Path.Combine(EngineDir, PortableMarker)); }
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
            message = null;
            if (!IsAvailable)
            {
                message = "未找到可用的 Moonlight V+ 组件（需要 Moonlight.exe 与同目录 portable.dat）：\r\n"
                        + EngineExe + "\r\n请确认发行包包含完整的 engines\\MoonlightVPlus 目录。";
                return false;
            }
            string cleanHost = Engines.NormalizeHost(host, out message);
            if (cleanHost == null) return false;

            string args;
            if (pair)
            {
                if (!TryPrepareSettings(out message)) return false;
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
                if (!TryPrepareSettings(out message)) return false;
                if (!ApplyAbrSetting(useAbr, out message)) return false;
                args = Engines.BuildMoonlightStreamArgs(cleanHost, width, height, fps, bitrateKbps,
                    showStats, playOnHost, windowed, useHevc, disableVsync, framePacing);
            }

            try
            {
                Engines.StartEngine(EngineExe, args);
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
                message = "已启动 Moonlight V+（" + width + "×" + height + " · " + fps + "fps · "
                        + (useHevc ? "HEVC" : "H.264") + " · " + (bitrateKbps / 1000) + " Mbps · "
                        + (useAbr ? "ABR 自适应开" : "固定码率") + (disableVsync ? " · 极速低延迟" : "") + "）。";
            }
            return true;
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
            if (!TryPrepareSettings(out message)) return false;
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
            if (!Directory.Exists(EngineDir))
            {
                message = "未找到 Moonlight V+ 目录：" + EngineDir;
                return false;
            }
            if (!File.Exists(Path.Combine(EngineDir, PortableMarker)))
            {
                message = "Moonlight V+ 缺少 portable.dat；为避免写到非便携设置，已取消本次 ABR 配置。";
                return false;
            }

            try
            {
                if (!TryPrepareSettings(out message)) return false;
                bool exists = File.Exists(SettingsPath);
                bool hasBom = false;
                string text = exists ? ReadSettingsText(out hasBom) : "";
                bool current;
                if (exists && TryGetAbrValue(text, out current) && current == enabled)
                {
                    message = "Moonlight V+ 的 Sunshine ABR 已是" + (enabled ? "开启" : "关闭") + "状态，未改写设置。";
                    return true;
                }
                if (IsEngineRunning(out message)) return false;
                if (!exists && !enabled)
                {
                    message = "Moonlight V+ 是全新便携目录；Sunshine ABR 默认关闭，无需写入设置。";
                    return true;
                }
                string updated = SetAbrValue(text, enabled);
                if (!exists || !string.Equals(updated, text, StringComparison.Ordinal))
                {
                    // 防竞争：写回前再确认没有 V+ 进程正在使用同一份便携设置。
                    if (IsEngineRunning(out message)) return false;
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
        /// 首启迁移：仅当 V+ 自己的便携 INI 缺失且存在标准版便携 INI 时复制一次。
        /// 复制前校验 portable.dat 并确认 V+ 未运行；显式 Open、pair 与 ABR 路径共用。
        /// 已存在或无源文件时直接放行；普通错误给出可操作中文。
        /// </summary>
        internal static bool TryPrepareSettings(out string message)
        {
            message = null;
            if (File.Exists(SettingsPath)) return true;
            if (!File.Exists(BaseSettingsPath)) return true; // 全新用户：两边都空，正常继续
            if (!File.Exists(Path.Combine(EngineDir, PortableMarker)))
            {
                message = "Moonlight V+ 缺少 portable.dat，无法安全迁移原版设置；请使用发行包中的完整目录。";
                return false;
            }
            if (IsEngineRunning(out message)) return false;
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.Copy(BaseSettingsPath, SettingsPath, false);
                if (!File.Exists(SettingsPath))
                {
                    message = "迁移原版 Moonlight 设置失败：目标文件未生成。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                message = "迁移原版 Moonlight 设置失败：" + ex.Message;
                return false;
            }
        }

        private static string ReadSettingsText(out bool hasBom)
        {
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

        private static bool IsEngineRunning(out string message)
        {
            message = null;
            Process[] processes;
            try { processes = Process.GetProcessesByName("Moonlight"); }
            catch { return false; }
            try
            {
                foreach (Process process in processes)
                {
                    string path = null;
                    try { path = process.MainModule != null ? process.MainModule.FileName : null; }
                    catch { path = null; }
                    if (path == null)
                    {
                        message = "检测到 Moonlight 进程但无法确认它是否为 V+；请先退出 Moonlight 后重试。";
                        return true;
                    }
                    if (string.Equals(Path.GetFullPath(path), EngineExe, StringComparison.OrdinalIgnoreCase))
                    {
                        message = "Moonlight V+ 正在运行，无法安全修改 ABR 设置；请先退出 Moonlight V+ 后重试。";
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
    }
}
