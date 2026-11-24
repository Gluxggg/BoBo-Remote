using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DualRemote
{
    /// <summary>
    /// 固定的组件位置与启动参数。组件路径只由程序自身目录推导，绝不从配置文件读取可执行文件路径。
    /// 不在这里写最近目标名单，持久化统一由 RecentStore 负责。
    /// </summary>
    internal static class Engines
    {
        internal const string RustDeskRelative = "engines\\RustDesk\\rustdesk.exe";
        internal const string MoonlightRelative = "engines\\Moonlight\\Moonlight.exe";
        internal const string FoundationPairingUrl = "https://localhost:47990/";
        internal const string MoonlightAppName = "Desktop";
        internal const int DefaultStreamHeight = 1080;
        internal const int DefaultStreamBitrateKbps = 15000;
        internal const int MinimalStreamBitrateKbps = 500;
        internal const int MaximalStreamBitrateKbps = 500000;
        internal const int MinimalStreamWidth = 640;
        internal const int MaximalStreamWidth = 4096;
        internal const int MinimalStreamHeight = 480;
        internal const int MaximalStreamHeight = 2560;
        internal const int MinimalStreamFps = 24;
        internal const int MaximalStreamFps = 240;
        internal const int RustDeskLaunchTimeoutMs = 6000;
        internal const int RustDeskWindowTimeoutMs = 20000;

        internal static string AppDir
        {
            get { return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
        }

        internal static string RustDeskExe
        {
            get { return Path.Combine(AppDir, RustDeskRelative); }
        }

        internal static string MoonlightExe
        {
            get { return Path.Combine(AppDir, MoonlightRelative); }
        }

        internal static string TailscaleExe
        {
            get
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(Path.Combine(programFiles, "Tailscale"), "tailscale.exe");
            }
        }

        internal static bool RustDeskPresent
        {
            get { return File.Exists(RustDeskExe); }
        }

        internal static bool MoonlightPresent
        {
            get { return File.Exists(MoonlightExe); }
        }

        internal static bool TailscalePresent
        {
            get { return File.Exists(TailscaleExe); }
        }

        /// <summary>
        /// 设备码：允许从 RustDesk 界面复制的空格；保留字母、数字与 . _ -，
        /// 兼容默认 9 位数字 ID 和自定义服务器 ID，交由 RustDesk 做最终校验。
        /// </summary>
        internal static string NormalizeRustDeskId(string raw, out string error)
        {
            error = null;
            StringBuilder id = new StringBuilder();
            if (raw != null)
            {
                foreach (char c in raw)
                {
                    if (char.IsWhiteSpace(c)) continue;
                    bool allowed = (c >= '0' && c <= '9')
                        || (c >= 'a' && c <= 'z')
                        || (c >= 'A' && c <= 'Z')
                        || c == '.' || c == '_' || c == '-';
                    if (!allowed)
                    {
                        error = "设备码只支持数字、字母和 . _ -（不含引号）。";
                        return null;
                    }
                    id.Append(c);
                }
            }
            string text = id.ToString();
            if (text.Length < 3 || text.Length > 64)
            {
                error = "设备码长度应在 3–64 个字符之间；RustDesk 默认是 9 位数字。";
                return null;
            }
            if (text[0] == '-')
            {
                error = "设备码不能以短横线 - 开头。";
                return null;
            }
            return text;
        }

        /// <summary>主机：严格 IPv4、IPv6（可选方括号）或合法主机名；不接受端口与选项形态。</summary>
        internal static string NormalizeHost(string raw, out string error)
        {
            error = null;
            string host = (raw == null) ? "" : raw.Trim();
            if (host.Length == 0)
            {
                error = "请先填写主机地址，或点“查找设备”。";
                return null;
            }
            if (host.Length > 253)
            {
                error = "主机地址过长。";
                return null;
            }
            if (host[0] == '-')
            {
                error = "主机地址不能以短横线 - 开头。";
                return null;
            }

            string candidate = host;
            if (candidate.Length > 2 && candidate[0] == '[' && candidate[candidate.Length - 1] == ']')
            {
                candidate = candidate.Substring(1, candidate.Length - 2);
            }

            // IPv6 优先交给 IPAddress 解析；含冒号但不是合法 IPv6 的（例如 host:port）直接拒绝。
            if (candidate.IndexOf(':') >= 0)
            {
                IPAddress address;
                if (IPAddress.TryParse(candidate, out address)
                    && address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return candidate;
                }
                error = "IPv6 地址格式不正确，且这里不需要端口。";
                return null;
            }
            if (TryStrictIPv4(candidate)) return candidate;

            // 其余按主机名（NetBIOS / Tailscale MagicDNS）逐段校验，端口、引号等一律拒绝。
            string[] labels = host.Split('.');
            foreach (string label in labels)
            {
                if (label.Length == 0 || label.Length > 63)
                {
                    error = "主机地址格式不正确。";
                    return null;
                }
                if (label[0] == '-' || label[label.Length - 1] == '-')
                {
                    error = "主机名每段不能以 - 开头或结尾。";
                    return null;
                }
                foreach (char c in label)
                {
                    bool allowed = (c >= '0' && c <= '9')
                        || (c >= 'a' && c <= 'z')
                        || (c >= 'A' && c <= 'Z')
                        || c == '-' || c == '_';
                    if (!allowed)
                    {
                        error = "主机地址只支持字母、数字、点、短横线和下划线（不含冒号与端口）。";
                        return null;
                    }
                }
            }
            // 形如 999.1.1.1 的全数字段按 IPv4 意图拒绝，避免被当成主机名放行。
            bool allNumeric = true;
            foreach (string label in labels)
            {
                foreach (char c in label)
                {
                    if (c < '0' || c > '9')
                    {
                        allNumeric = false;
                        break;
                    }
                }
                if (!allNumeric) break;
            }
            if (allNumeric)
            {
                error = "IPv4 地址格式不正确。";
                return null;
            }
            return host;
        }

        private static bool TryStrictIPv4(string value)
        {
            string[] parts = value.Split('.');
            if (parts.Length != 4) return false;
            foreach (string part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                if (part.Length > 1 && part[0] == '0') return false;
                int number;
                if (!int.TryParse(part, out number)) return false;
                if (number > 255) return false;
            }
            return true;
        }

        /// <summary>RustDesk 1.5.3：--connect 后跟设备 ID，由原生界面完成连接与授权。</summary>
        internal static string BuildRustDeskConnectArgs(string id)
        {
            return "--connect " + id;
        }

        /// <summary>RustDesk 1.5.3：--file-transfer 后跟设备 ID，由原生界面完成授权。</summary>
        internal static string BuildRustDeskFileTransferArgs(string id)
        {
            return "--file-transfer " + id;
        }

        /// <summary>
        /// Moonlight PC v6.2.0 已核对 stream 参数：--720/--1080/--1440、--fps、--bitrate(Kbps)、--video-codec H.264。
        /// 应用名固定为 Desktop；首次配对必须在 Moonlight 原生界面完成。
        /// </summary>
        internal static string BuildMoonlightStreamArgs(string host, int height, int bitrateKbps)
        {
            string heightFlag = height == 720 ? "--720" : (height == 1440 ? "--1440" : "--1080");
            return "stream " + Quote(host) + " " + Quote(MoonlightAppName) + " " + heightFlag
                 + " --fps 60 --bitrate " + bitrateKbps + " --video-codec H.264";
        }

        internal static string BuildMoonlightStreamArgs(string host)
        {
            return BuildMoonlightStreamArgs(host, DefaultStreamHeight, DefaultStreamBitrateKbps);
        }

        /// <summary>
        /// 精确宽高 + 帧率版本：--resolution <宽>x<高> --fps <n>，可配 HEVC、浮层、主机播放、窗口模式。
        /// Moonlight v6.2.0 的 --resolution 解析规则是 ^(\d+)x(\d+)$，只把宽高交给主机协商；
        /// 是否真的有该显示模式由 Foundation/Sunshine 侧决定，这里只做产品侧安全范围校验。
        /// 不显式传 --frame-pacing：上游默认关闭，保持低延迟与既有默认行为。
        /// </summary>
        internal static string NormalizeAppName(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return MoonlightAppName;
            StringBuilder clean = new StringBuilder();
            foreach (char c in appName.Trim())
            {
                if (c == '"' || c == '\r' || c == '\n' || char.IsControl(c)) continue;
                clean.Append(c);
            }
            return clean.Length == 0 ? MoonlightAppName : clean.ToString();
        }

        private static readonly string[] PreferredDesktopAppNames = new string[]
        {
            "Desktop",
            "桌面",
            "Windows 桌面",
            "Windows Desktop",
            "远程桌面",
            "遠端桌面",
            "Remote Desktop",
            "Full Desktop",
            "完整桌面"
        };

        /// <summary>
        /// 从目标 Sunshine 主机返回的应用名列表中智能选择桌面应用（自动兼容系统版中文「桌面」、随包英文「Desktop」等）。
        /// </summary>
        internal static string SelectBestDesktopAppName(IEnumerable<string> availableApps)
        {
            List<string> candidates = new List<string>();
            if (availableApps != null)
            {
                foreach (string raw in availableApps)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string clean = NormalizeAppName(raw);
                    if (!string.IsNullOrEmpty(clean)) candidates.Add(clean);
                }
            }
            if (candidates.Count == 0) return MoonlightAppName;

            // 1. 精确匹配主流中英文桌面名称（例如 Foundation Sunshine 中文安装版默认名「桌面」与英文版「Desktop」）
            foreach (string preferred in PreferredDesktopAppNames)
            {
                foreach (string candidate in candidates)
                {
                    if (string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }

            // 2. 包含「桌面」或「Desktop」且不属于大屏/游戏启动器的应用项
            foreach (string candidate in candidates)
            {
                if (IsNonDesktopLauncher(candidate)) continue;
                if (candidate.IndexOf("桌面", StringComparison.OrdinalIgnoreCase) >= 0
                    || candidate.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0)
                    return candidate;
            }

            // 3. 兜底取第一个非 Steam / Xbox / 大屏 的应用项，防止用户自定义改名后报「找不到应用程序 Desktop」
            foreach (string candidate in candidates)
            {
                if (!IsNonDesktopLauncher(candidate))
                    return candidate;
            }

            return candidates[0];
        }

        private static bool IsNonDesktopLauncher(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (string.Equals(name, "大屏", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Big Screen", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("bigscreen", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("big screen", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("playnite", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }

        internal static string DecodeCliOutputBytes(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return "";
            try
            {
                UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
                return strictUtf8.GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                try { return Encoding.Default.GetString(raw); }
                catch { return Encoding.UTF8.GetString(raw); }
            }
        }

        internal static List<string> ParseAppListOutput(string stdout)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(stdout)) return result;
            string[] lines = stdout.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Loading app list", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Establishing connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                string clean = NormalizeAppName(line);
                if (!string.IsNullOrEmpty(clean)) result.Add(clean);
            }
            return result;
        }

        internal static List<string> QueryMoonlightAppList(string exePath, string cleanHost, int timeoutMs)
        {
            List<string> empty = new List<string>();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath) || string.IsNullOrEmpty(cleanHost))
                return empty;

            Process proc = null;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = exePath;
                info.Arguments = "list " + Quote(cleanHost);
                info.WorkingDirectory = Path.GetDirectoryName(exePath);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                proc = Process.Start(info);
                if (proc == null) return empty;

                byte[] stdoutBytes = null;
                ManualResetEvent stdoutDone = new ManualResetEvent(false);
                Thread outReader = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] buf = new byte[4096];
                            int n;
                            while ((n = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length)) > 0)
                                ms.Write(buf, 0, n);
                            stdoutBytes = ms.ToArray();
                        }
                    }
                    catch { }
                    finally
                    {
                        try { stdoutDone.Set(); } catch { }
                    }
                }));
                outReader.IsBackground = true;
                outReader.Start();

                Thread errDrainer = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        byte[] buf = new byte[2048];
                        while (proc.StandardError.BaseStream.Read(buf, 0, buf.Length) > 0) { }
                    }
                    catch { }
                }));
                errDrainer.IsBackground = true;
                errDrainer.Start();

                bool exited = proc.WaitForExit(Math.Max(800, timeoutMs));
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    try { stdoutDone.WaitOne(300); } catch { }
                    return empty;
                }

                try { stdoutDone.WaitOne(800); } catch { }
                if (proc.ExitCode != 0 || stdoutBytes == null || stdoutBytes.Length == 0)
                    return empty;

                string text = DecodeCliOutputBytes(stdoutBytes);
                return ParseAppListOutput(text);
            }
            catch
            {
                return empty;
            }
            finally
            {
                if (proc != null)
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }

        internal static string BuildMoonlightStreamArgs(string host, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc)
        {
            return BuildMoonlightStreamArgsWithApp(host, MoonlightAppName, width, height, fps, bitrateKbps,
                showStats, playOnHost, windowed, useHevc);
        }

        internal static string BuildMoonlightStreamArgsWithApp(string host, string appName, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc)
        {
            string resolvedApp = NormalizeAppName(appName);
            return "stream " + Quote(host) + " " + Quote(resolvedApp)
                 + " --resolution " + width.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + "x" + height.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " --fps " + fps.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " --bitrate " + bitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + (useHevc ? " --video-codec HEVC" : " --video-codec H.264")
                 + (showStats ? " --performance-overlay" : " --no-performance-overlay")
                 + (playOnHost ? " --audio-on-host" : " --no-audio-on-host")
                 + (windowed ? " --display-mode windowed" : " --display-mode fullscreen");
        }

        /// <summary>
        /// 低延迟增强参数版本：在原有参数基础上显式控制 VSync 与 Frame Pacing。
        /// --no-vsync 可减少约 1 帧显示缓冲延迟；--no-frame-pacing 优先最低延迟。
        /// </summary>
        internal static string BuildMoonlightStreamArgs(string host, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool disableVsync, bool framePacing)
        {
            return BuildMoonlightStreamArgsWithApp(host, MoonlightAppName, width, height, fps, bitrateKbps,
                showStats, playOnHost, windowed, useHevc, disableVsync, framePacing);
        }

        internal static string BuildMoonlightStreamArgsWithApp(string host, string appName, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool disableVsync, bool framePacing)
        {
            return BuildMoonlightStreamArgsWithApp(host, appName, width, height, fps, bitrateKbps, showStats, playOnHost, windowed, useHevc)
                 + (disableVsync ? " --no-vsync" : " --vsync")
                 + (framePacing ? " --frame-pacing" : " --no-frame-pacing");
        }

        /// <summary>
        /// 显式设置版本：性能浮层、主机播放声音、窗口模式、HEVC。
        /// 这些开关均已用 Moonlight PC v6.2.0 的 CLI 帮助核实。
        /// </summary>
        internal static string BuildMoonlightStreamArgs(string host, int height, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc)
        {
            string heightFlag = height == 720 ? "--720" : (height == 1440 ? "--1440" : "--1080");
            return "stream " + Quote(host) + " " + Quote(MoonlightAppName) + " " + heightFlag
                 + " --fps 60 --bitrate " + bitrateKbps
                 + (useHevc ? " --video-codec HEVC" : " --video-codec H.264")
                 + (showStats ? " --performance-overlay" : " --no-performance-overlay")
                 + (playOnHost ? " --audio-on-host" : " --no-audio-on-host")
                 + (windowed ? " --display-mode windowed" : " --display-mode fullscreen");
        }

        internal static string BuildMoonlightPairArgs(string host)
        {
            string error;
            return BuildMoonlightPairArgs(host, out error);
        }

        internal static string BuildMoonlightPairArgs(string host, out string error)
        {
            error = null;
            string cleanHost = NormalizeHost(host, out error);
            if (cleanHost == null) return null;
            return "pair " + Quote(cleanHost);
        }

        /// <summary>配对码严格为 4 个 ASCII 数字；不接受空格、全角数字或其他字符。</summary>
        internal static bool TryValidatePairPin(string pin, out string error)
        {
            error = null;
            if (pin == null || pin.Length != 4)
            {
                error = "配对码必须是 4 位数字。";
                return false;
            }
            foreach (char c in pin)
            {
                if (c < '0' || c > '9')
                {
                    error = "配对码只能包含 0–9 的数字。";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Moonlight V+ 的带 PIN 配对参数；无效 PIN 返回 null。</summary>
        internal static string BuildMoonlightPairArgs(string host, string pin)
        {
            string error;
            return BuildMoonlightPairArgs(host, pin, out error);
        }

        internal static string BuildMoonlightPairArgs(string host, string pin, out string error)
        {
            string hostError;
            string cleanHost = NormalizeHost(host, out hostError);
            if (cleanHost == null)
            {
                error = hostError;
                return null;
            }
            if (!TryValidatePairPin(pin, out error)) return null;
            return "pair " + Quote(cleanHost) + " --pin " + pin;
        }

        internal static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        /// <summary>
        /// 打开 RustDesk 原生界面：查看/复制本机 ID、确认远程申请。
        /// 启动后短暂等待主窗口出现，避免自解压 wrapper 父进程退出后仍误报“已打开”。
        /// </summary>
        internal static bool LaunchRustDesk(out string message)
        {
            message = null;
            if (!RustDeskPresent)
            {
                message = MissingComponentMessage("RustDesk", RustDeskExe);
                return false;
            }
            try
            {
                StartEngine(RustDeskExe, "");
                if (WaitRustDeskWindow(RustDeskLaunchTimeoutMs))
                {
                    message = "已打开 RustDesk 原生界面：可在其中查看/复制本机 ID，并确认远程申请。";
                    return true;
                }
                message = "RustDesk 进程已启动，但约 " + (RustDeskLaunchTimeoutMs / 1000) + " 秒内没有出现窗口。\r\n"
                        + "若这里是便携自解压 wrapper，它需要写入用户目录才能解压；请确认发行包里的 engines\\RustDesk "
                        + "已是解压后的 RustDesk 1.5.3 文件。\r\n组件路径：" + RustDeskExe;
                return false;
            }
            catch (Exception ex)
            {
                message = "启动 RustDesk 失败：" + ex.Message + "\r\n组件路径：" + RustDeskExe;
                return false;
            }
        }

        /// <summary>
        /// 发起 RustDesk 原生授权连接。已在运行时立即派发 --connect；未运行时先开界面，
        /// 等主窗口出现后再派发（后台线程，调用本身不阻塞）。
        /// </summary>
        internal static bool ConnectRustDesk(string rawId, out string message)
        {
            return ConnectRustDesk(rawId, out message, null);
        }

        /// <param name="onBackgroundStatus">可选：冷启动补发连接后的状态文本回调，运行在后台线程。</param>
        internal static bool ConnectRustDesk(string rawId, out string message, Action<string> onBackgroundStatus)
        {
            return StartRustDeskAction("--connect", "连接", rawId, out message, onBackgroundStatus);
        }

        /// <summary>
        /// 打开 RustDesk 原生文件传输（--file-transfer）。和连接一样需要被控端原生授权；
        /// 已在运行时立即派发，未运行时先开界面、等主窗口出现后再派发。
        /// </summary>
        internal static bool TransferRustDesk(string rawId, out string message)
        {
            return TransferRustDesk(rawId, out message, null);
        }

        internal static bool TransferRustDesk(string rawId, out string message, Action<string> onBackgroundStatus)
        {
            return StartRustDeskAction("--file-transfer", "文件传输", rawId, out message, onBackgroundStatus);
        }

        /// <param name="onBackgroundStatus">可选：冷启动补发动作后的状态文本回调，运行在后台线程。</param>
        private static bool StartRustDeskAction(string action, string actionName, string rawId, out string message,
            Action<string> onBackgroundStatus)
        {
            message = null;
            string error;
            string id = NormalizeRustDeskId(rawId, out error);
            if (id == null)
            {
                message = error;
                return false;
            }
            if (!RustDeskPresent)
            {
                message = MissingComponentMessage("RustDesk", RustDeskExe);
                return false;
            }
            try
            {
                if (RustDeskWindowReady())
                {
                    using (StartEngine(RustDeskExe, action + " " + id)) { }
                    message = "已向 RustDesk 发送" + actionName + "请求：" + id + "。请在被控端原生界面确认授权。";
                    return true;
                }
                using (StartEngine(RustDeskExe, "")) { }
                string targetId = id;
                string targetAction = action;
                string targetActionName = actionName;
                Thread worker = new Thread(new ThreadStart(delegate
                {
                    string text = null;
                    try
                    {
                        if (WaitRustDeskWindow(RustDeskWindowTimeoutMs))
                        {
                            using (StartEngine(RustDeskExe, targetAction + " " + targetId)) { }
                            text = "RustDesk 窗口已就绪，已发起" + targetActionName + "：" + targetId + "。请在被控端确认授权。";
                        }
                        else
                        {
                            text = "RustDesk 启动较慢或未成功；请在 RustDesk 窗口中手动发起" + targetActionName
                                 + "（ID " + targetId + "）。";
                        }
                    }
                    catch (Exception ex)
                    {
                        text = "RustDesk 自动" + targetActionName + "失败：" + ex.Message
                             + "。请在 RustDesk 窗口中手动发起（ID " + targetId + "）。";
                    }
                    if (onBackgroundStatus != null && text != null)
                    {
                        try { onBackgroundStatus(text); } catch { }
                    }
                }));
                worker.IsBackground = true;
                worker.Start();
                message = "RustDesk 未在运行，正在启动；等其主窗口出现后会自动发起" + actionName + "：" + id + "。";
                return true;
            }
            catch (Exception ex)
            {
                message = "启动 RustDesk 失败：" + ex.Message + "\r\n组件路径：" + RustDeskExe;
                return false;
            }
        }

        /// <summary>打开 Moonlight 原生界面（首次配对、主机管理）。</summary>
        internal static bool LaunchMoonlight(out string message)
        {
            message = null;
            if (!MoonlightPresent)
            {
                message = MissingComponentMessage("Moonlight", MoonlightExe);
                return false;
            }
            try
            {
                using (StartEngine(MoonlightExe, "")) { }
                message = "已打开 Moonlight 原生界面：首次配对请在其中完成 PIN 确认。";
                return true;
            }
            catch (Exception ex)
            {
                message = "启动 Moonlight 失败：" + ex.Message + "\r\n组件路径：" + MoonlightExe;
                return false;
            }
        }

        /// <summary>
        /// Moonlight v6.2.0 的 pair CLI：Moonlight 窗口会显示它生成的 4 位 PIN，
        /// 需要在该主机侧（Foundation 的 47990 页面）输入，原生确认仍然保留。
        /// </summary>
        internal static bool PairMoonlight(string rawHost, out string message)
        {
            message = null;
            string host = NormalizeHost(rawHost, out message);
            if (host == null) return false;
            if (!MoonlightPresent)
            {
                message = MissingComponentMessage("Moonlight", MoonlightExe);
                return false;
            }
            try
            {
                using (StartEngine(MoonlightExe, BuildMoonlightPairArgs(host))) { }
                message = "已在 Moonlight 窗口发起配对：把窗口中显示的 4 位 PIN 输入 Foundation 的 47990 页面完成确认。"
                        + "若提示已配对，可直接连接 Desktop。";
                return true;
            }
            catch (Exception ex)
            {
                message = "启动 Moonlight 配对失败：" + ex.Message + "\r\n组件路径：" + MoonlightExe;
                return false;
            }
        }

        internal static bool StreamMoonlight(string rawHost, out string message)
        {
            return StreamMoonlight(rawHost, DefaultStreamHeight, DefaultStreamBitrateKbps, out message);
        }

        /// <summary>height 只接受 720 / 1080 / 1440；bitrate 为 Moonlight 支持的 Kbps 范围。</summary>
        internal static bool StreamMoonlight(string rawHost, int height, int bitrateKbps, out string message)
        {
            string host;
            if (!TryValidateStream(rawHost, height, bitrateKbps, out host, out message)) return false;
            return StartMoonlightStream(
                BuildMoonlightStreamArgs(host, height, bitrateKbps),
                "已请求 Moonlight 串流 Desktop（" + height + "p / 60fps / H.264 / " + bitrateKbps
                + " Kbps）。若提示未配对，请先完成首次配对。",
                out message);
        }

        /// <summary>
        /// 显式设置版本：性能浮层、主机播放声音、窗口模式、HEVC（本机默认 H.264）。
        /// </summary>
        internal static bool StreamMoonlight(string rawHost, int height, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, out string message)
        {
            string host;
            if (!TryValidateStream(rawHost, height, bitrateKbps, out host, out message)) return false;
            string detail = (useHevc ? "HEVC" : "H.264") + " / " + bitrateKbps + " Kbps；性能浮层"
                + (showStats ? "开" : "关") + "，声音"
                + (playOnHost ? "在主机播放" : "在本机播放") + "，" + (windowed ? "窗口" : "全屏") + "模式";
            return StartMoonlightStream(
                BuildMoonlightStreamArgs(host, height, bitrateKbps, showStats, playOnHost, windowed, useHevc),
                "已请求 Moonlight 串流 Desktop（" + height + "p / 60fps / " + detail
                + "）。若提示未配对，请先完成首次配对。",
                out message);
        }

        /// <summary>
        /// 精确宽高 + 帧率版本（2560x1440@90、2560x1600@90、1920x1200@60 等）。
        /// 宽高必须为偶数；拒绝过小/过大与边界值，避免 H.264/HEVC 编码失败或主机拒绝。
        /// 不显式传 --frame-pacing：上游默认关闭，保持低延迟与既有默认行为。
        /// </summary>
        internal static bool StreamMoonlight(string rawHost, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, out string message)
        {
            return StreamMoonlight(rawHost, width, height, fps, bitrateKbps, showStats, playOnHost, windowed,
                useHevc, false, false, out message);
        }

        internal static string ResolveOfficialDesktopAppName(string rawHost)
        {
            string err;
            string host = NormalizeHost(rawHost, out err);
            if (host == null) return MoonlightAppName;

            List<string> iniApps = FoundationClient.ReadAppsFromIniFile(FoundationClient.BaseSettingsPath, host);
            if (iniApps.Count > 0)
                return SelectBestDesktopAppName(iniApps);
            if (MoonlightPresent)
            {
                List<string> liveApps = QueryMoonlightAppList(MoonlightExe, host, 8500);
                if (liveApps.Count > 0)
                    return SelectBestDesktopAppName(liveApps);
                iniApps = FoundationClient.ReadAppsFromIniFile(FoundationClient.BaseSettingsPath, host);
                if (iniApps.Count > 0)
                    return SelectBestDesktopAppName(iniApps);
            }
            return MoonlightAppName;
        }

        internal static bool StreamMoonlight(string rawHost, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool disableVsync, bool framePacing,
            out string message)
        {
            return StreamMoonlightWithApp(rawHost, null, width, height, fps, bitrateKbps,
                showStats, playOnHost, windowed, useHevc, disableVsync, framePacing, out message);
        }

        internal static bool StreamMoonlightWithApp(string rawHost, string appName, int width, int height, int fps, int bitrateKbps,
            bool showStats, bool playOnHost, bool windowed, bool useHevc, bool disableVsync, bool framePacing,
            out string message)
        {
            message = null;
            string host = NormalizeHost(rawHost, out message);
            if (host == null) return false;
            if (!TryValidateStreamGeometry(width, height, fps, bitrateKbps, out message)) return false;
            if (!MoonlightPresent)
            {
                message = MissingComponentMessage("Moonlight", MoonlightExe);
                return false;
            }
            string resolvedApp = string.IsNullOrWhiteSpace(appName)
                ? ResolveOfficialDesktopAppName(host)
                : NormalizeAppName(appName);
            string detail = (useHevc ? "HEVC" : "H.264") + " · " + (bitrateKbps / 1000) + " Mbps"
                + (disableVsync ? " · 极速低延迟" : "")
                + (showStats ? " · 统计开" : "");
            return StartMoonlightStream(
                BuildMoonlightStreamArgsWithApp(host, resolvedApp, width, height, fps, bitrateKbps, showStats, playOnHost, windowed, useHevc, disableVsync, framePacing),
                "已提交 Moonlight 串流请求（" + resolvedApp + " · " + width + "×" + height + " · " + fps + "fps · " + detail
                + "）。等待客户端首帧，启动进程不代表串流已经稳定。",
                out message);
        }

        internal static bool TryValidateStreamGeometry(int width, int height, int fps, int bitrateKbps,
            out string message)
        {
            message = null;
            if (width % 2 != 0 || height % 2 != 0)
            {
                message = "流分辨率宽高必须是偶数（H.264/HEVC 的 4:2:0 编码要求）。";
                return false;
            }
            if (width <= MinimalStreamWidth || width >= MaximalStreamWidth)
            {
                message = "宽度需大于 " + MinimalStreamWidth + " 且小于 " + MaximalStreamWidth
                        + "（常用 1280 / 1920 / 2560 / 3840）。";
                return false;
            }
            if (height <= MinimalStreamHeight || height >= MaximalStreamHeight)
            {
                message = "高度需大于 " + MinimalStreamHeight + " 且小于 " + MaximalStreamHeight
                        + "（常用 720 / 1080 / 1200 / 1440 / 1600 / 2160）。";
                return false;
            }
            if (fps <= MinimalStreamFps || fps >= MaximalStreamFps)
            {
                message = "帧率需大于 " + MinimalStreamFps + " 且小于 " + MaximalStreamFps + "（常用 60 / 90 / 120 / 144）。";
                return false;
            }
            if (bitrateKbps <= MinimalStreamBitrateKbps || bitrateKbps >= MaximalStreamBitrateKbps)
            {
                message = "码率需大于 " + MinimalStreamBitrateKbps + " 且小于 " + MaximalStreamBitrateKbps
                        + " Kbps（常用 8000 / 15000 / 25000）。";
                return false;
            }
            return true;
        }

        private static bool TryValidateStream(string rawHost, int height, int bitrateKbps,
            out string host, out string message)
        {
            message = null;
            host = NormalizeHost(rawHost, out message);
            if (host == null) return false;
            if (height != 720 && height != 1080 && height != 1440)
            {
                message = "分辨率只支持 720 / 1080 / 1440。";
                return false;
            }
            if (bitrateKbps < MinimalStreamBitrateKbps || bitrateKbps > MaximalStreamBitrateKbps)
            {
                message = "码率需在 " + MinimalStreamBitrateKbps + "–" + MaximalStreamBitrateKbps
                        + " Kbps 之间（Moonlight 支持范围）。";
                return false;
            }
            if (!MoonlightPresent)
            {
                message = MissingComponentMessage("Moonlight", MoonlightExe);
                return false;
            }
            return true;
        }

        private static bool StartMoonlightStream(string arguments, string successMessage, out string message)
        {
            message = null;
            try
            {
                using (StartEngine(MoonlightExe, arguments)) { }
                message = successMessage;
                return true;
            }
            catch (Exception ex)
            {
                message = "启动 Moonlight 串流失败：" + ex.Message + "\r\n组件路径：" + MoonlightExe;
                return false;
            }
        }

        /// <summary>打开 Foundation 本机原生配对管理页。</summary>
        internal static bool OpenFoundationPairing(out string message)
        {
            string url = FoundationPairingUrl;
            string openError;
            if (TryOpenUrl(url, out openError))
            {
                message = "已打开本机的配对管理页：" + url + "。";
                return true;
            }
            message = "无法打开浏览器：" + openError + "\r\n请手动访问 " + url;
            return false;
        }

        internal static int LastStreamProcessId { get; private set; }

        internal static Process StartEngine(string exePath, string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = exePath;
            info.Arguments = arguments;
            info.WorkingDirectory = Path.GetDirectoryName(exePath);
            info.UseShellExecute = false;
            Process started = Process.Start(info);
            if (started != null && string.Equals(Path.GetFileName(exePath), "Moonlight.exe", StringComparison.OrdinalIgnoreCase))
            {
                LastStreamProcessId = started.Id;
            }
            return started;
        }

        internal static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static void StopStreamProcess(int pid)
        {
            if (pid <= 0) return;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return;
                    try { p.CloseMainWindow(); }
                    catch { }
                    if (!p.WaitForExit(1200))
                    {
                        try { p.Kill(); p.WaitForExit(1500); }
                        catch { }
                    }
                }
            }
            catch
            {
            }
        }

        internal static bool TryOpenUrl(string url, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(url);
                info.UseShellExecute = true;
                using (Process.Start(info)) { }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>RustDesk 主窗口是否已出现（用于冷启动后安全地发送 --connect）。</summary>
        internal static bool RustDeskWindowReady()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("rustdesk");
            }
            catch
            {
                return false;
            }
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero) return true;
                    }
                    catch
                    {
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

        private static bool WaitRustDeskWindow(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (RustDeskWindowReady()) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return false;
        }

        internal static string MissingComponentMessage(string name, string path)
        {
            return "未找到 " + name + " 组件：\r\n" + path + "\r\n\r\n"
                 + "请确认发行包已完整解压，且 engines 目录与“BoBo远程.exe”位于同一层，然后重试。";
        }
    }
}
