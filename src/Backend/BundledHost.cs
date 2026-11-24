using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DualRemote
{
    /// <summary>
    /// 本机被控组件的固定运行位置。随包组件只读自 engines\Sunshine，首次准备时复制到
    /// %LOCALAPPDATA%\双端远程\host；运行配置和配对凭据始终保留在该稳定目录。
    /// 已运行的真实 Sunshine 优先复用，避免双开；未运行时才启动包内固定版本。
    /// </summary>
    internal static class BundledHost
    {
        internal const string ExecutableName = "sunshine.exe";
        internal const string ConfigName = "config";
        internal const string CredentialsName = "credentials";
        internal const string TokenName = "local-auth.token";
        internal const string CertificateName = "cacert.pem";
        internal const string ConfigFileName = "sunshine.conf";
        internal const string AppsFileName = "apps.json";
        internal const string StateFileName = "sunshine_state.json";

        internal const string BundleVersion = "v2026.1123.185102";
        internal const string BundleReleaseTag = "v2026.1123.185102.杂鱼";
        internal const string BundleSourceFileName = "Sunshine.v2026.1123.185102.Portable-x64.zip";
        internal const long BundleExecutableSize = 53252608L;
        internal const string BundleExecutableSha256 =
            "87B4D685E23D843AD8D6CD19678EB468159CB3C45479E2F997B358220FE359C7";
        internal const string BundleZipSha256 =
            "610788D951FE9BA0BA74F370C4BB83D535A61531F17937CD0A4644F00EABBA49";

        private const string RuntimeStampFileName = ".bundle-version";
        private const int StartProbeMilliseconds = 2500;
        private const int ReadinessTimeoutMilliseconds = 15000;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const string ProcessName = "sunshine";
        private static readonly object LaunchLock = new object();
        private static int ownedProcessId;
        private static long ownedProcessStartTicks;
        private static string ownedExecutablePath;
        private static string ownedConfigDirectory;
        private static int ownedBasePort;
        private static int ownedWebPort;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process, int flags, StringBuilder executablePath, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        internal static string AppDir
        {
            get { return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
        }

        internal static string LocalRoot
        {
            get { return Path.Combine(RecentStore.DirPath, "host"); }
        }

        internal static string LocalConfigDirectory
        {
            get { return Path.Combine(LocalRoot, ConfigName); }
        }

        internal static string DefaultInstalledRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Sunshine");
            }
        }

        /// <summary>随包宿主的唯一固定来源；不接受工作目录外的候选或用户配置路径。</summary>
        internal static string BundledSourceRoot
        {
            get
            {
                string root = Path.Combine(AppDir, "engines", "Sunshine");
                if (File.Exists(Path.Combine(root, ExecutableName))) return root;
                return null;
            }
        }

        internal static string BundledSourceExecutable
        {
            get
            {
                string root = BundledSourceRoot;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, ExecutableName);
            }
        }

        internal static bool BundledAvailable
        {
            get
            {
                string root = BundledSourceRoot;
                if (string.IsNullOrEmpty(root)) return false;
                if (!File.Exists(Path.Combine(root, ExecutableName))) return false;
                if (!File.Exists(Path.Combine(root, "assets", AppsFileName))) return false;
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(
                        Path.Combine(root, ExecutableName));
                    return !string.IsNullOrEmpty(info.FileVersion)
                        && info.FileVersion.StartsWith(BundleVersion, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static string BundledVersion
        {
            get { return BundleVersion; }
        }

        internal static bool LocalRuntimePresent
        {
            get { return File.Exists(Path.Combine(LocalRoot, ExecutableName)); }
        }

        internal static bool LocalPrepared
        {
            get
            {
                return File.Exists(Path.Combine(LocalRoot, ExecutableName))
                    && File.Exists(Path.Combine(LocalConfigDirectory, AppsFileName));
            }
        }

        internal static int RunningProcessCount
        {
            get
            {
                Process[] processes = null;
                try
                {
                    processes = Process.GetProcessesByName("sunshine");
                    return processes.Length;
                }
                catch
                {
                    return 0;
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
        }

        internal static bool AnySunshineProcessRunning
        {
            get { return RunningProcessCount > 0; }
        }

        /// <summary>已运行 Sunshine 的真实可执行路径；无法从系统确认时返回 null。</summary>
        internal static string RunningExecutablePath
        {
            get
            {
                Process[] processes = null;
                try
                {
                    processes = Process.GetProcessesByName("sunshine");
                    foreach (Process process in processes)
                    {
                        string executable = TryGetProcessExecutablePath(process);
                        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) continue;
                        return Path.GetFullPath(executable);
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
                return null;
            }
        }

        /// <summary>已运行 Sunshine 的真实根目录；无法确认进程路径时返回 null。</summary>
        internal static string RunningInstalledRoot
        {
            get
            {
                string executable = RunningExecutablePath;
                return string.IsNullOrEmpty(executable) ? null : Path.GetDirectoryName(executable);
            }
        }

        internal static bool RunningFromLocalRoot
        {
            get { return IsPathUnder(RunningInstalledRoot, LocalRoot); }
        }

        internal static bool IsRootRunning(string root)
        {
            string running = RunningInstalledRoot;
            return !string.IsNullOrEmpty(root) && SamePath(root, running);
        }

        internal static string ResolveActiveRoot()
        {
            string running = RunningInstalledRoot;
            if (!string.IsNullOrEmpty(running)) return running;
            if (LocalRuntimePresent) return LocalRoot;
            if (BundledAvailable) return LocalRoot;
            if (File.Exists(Path.Combine(DefaultInstalledRoot, ExecutableName))) return DefaultInstalledRoot;
            return LocalRoot;
        }

        internal static string ActiveRoot
        {
            get { return ResolveActiveRoot(); }
        }

        internal static string ActiveConfigDirectory
        {
            get
            {
                string path;
                string error;
                return TryGetActiveConfigDirectory(out path, out error) ? path : null;
            }
        }

        internal static string ActiveConfigDirectoryError
        {
            get
            {
                string path;
                string error;
                TryGetActiveConfigDirectory(out path, out error);
                return error ?? "";
            }
        }

        internal static bool TryGetActiveConfigDirectory(out string path, out string error)
        {
            path = null;
            error = null;
            string running = RunningExecutablePath;
            if (!string.IsNullOrEmpty(running))
            {
                if (IsPathUnder(running, LocalRoot)) { path = LocalConfigDirectory; return true; }
                string explicitConf;
                if (TryGetExplicitConfigPath(running, out explicitConf)) { path = Path.GetDirectoryName(explicitConf); return true; }
                // 系统安装版 Sunshine（例如 Windows 服务启动的 C:\Program Files\Sunshine\sunshine.exe）
                // 默认使用同级 config 目录，不带 .conf 命令行参数（且非管理员无法读 SYSTEM 进程 CommandLine）。
                string runningDir = Path.GetDirectoryName(running);
                if (!string.IsNullOrEmpty(runningDir))
                {
                    string siblingConfig = Path.Combine(runningDir, ConfigName);
                    if (Directory.Exists(siblingConfig)
                        && (File.Exists(Path.Combine(siblingConfig, TokenName))
                            || File.Exists(Path.Combine(siblingConfig, ConfigFileName))))
                    {
                        path = siblingConfig;
                        return true;
                    }
                }
                error = "检测到本机已运行的 Sunshine，但无法确认其配置目录；请确认其 config 目录存在。";
                return false;
            }
            if (LocalPrepared) { path = LocalConfigDirectory; return true; }
            path = LocalConfigDirectory;
            error = "包内宿主尚未准备，配置目录将在首次准备时创建。";
            return false;
        }

        internal static string ActiveExecutable
        {
            get
            {
                string running = RunningExecutablePath;
                if (!string.IsNullOrEmpty(running)) return running;
                string local = Path.Combine(LocalRoot, ExecutableName);
                if (File.Exists(local)) return local;
                string bundled = BundledSourceExecutable;
                if (!string.IsNullOrEmpty(bundled) && File.Exists(bundled)) return bundled;
                return Path.Combine(DefaultInstalledRoot, ExecutableName);
            }
        }

        internal static string ActiveTokenPath
        {
            get
            {
                string config;
                string error;
                return TryGetActiveConfigDirectory(out config, out error) ? Path.Combine(config, TokenName) : null;
            }
        }

        internal static string ActiveCertificatePath
        {
            get
            {
                string config;
                string error;
                return TryGetActiveConfigDirectory(out config, out error)
                    ? Path.Combine(config, CredentialsName, CertificateName) : null;
            }
        }

        internal static int ActiveBasePort
        {
            get
            {
                if (ownedBasePort > 0) return ownedBasePort;
                string config;
                string error;
                int port;
                if (TryGetActiveConfigDirectory(out config, out error)
                    && IsPathUnder(config, LocalRoot)
                    && TryReadPort(Path.Combine(config, ConfigFileName), out port))
                    return port;
                return 0;
            }
        }

        internal static int ActiveWebUiPort
        {
            get
            {
                if (ownedWebPort > 0) return ownedWebPort;
                int port = ActiveBasePort;
                return port > 0 ? port + 1 : 0;
            }
        }

        internal static bool ActiveUsesBundledRuntime
        {
            get
            {
                if (RunningFromLocalRoot) return true;
                return string.IsNullOrEmpty(RunningInstalledRoot) && LocalRuntimePresent;
            }
        }

        /// <summary>现有调优脚本只支持 Program Files\Sunshine；内置宿主不作为改写目标。</summary>
        internal static bool TuningSupported
        {
            get
            {
                string root = ActiveRoot;
                if (!SamePath(root, DefaultInstalledRoot)) return false;
                if (!File.Exists(Path.Combine(root, ExecutableName))) return false;
                string config;
                string error;
                return TryGetActiveConfigDirectory(out config, out error)
                    && File.Exists(Path.Combine(config, ConfigFileName));
            }
        }

        internal static string TuningUnsupportedReason
        {
            get
            {
                if (TuningSupported) return "";
                if (ActiveUsesBundledRuntime || BundledAvailable)
                    return "当前使用包内稳定宿主；现有一键调优脚本只支持 C:\\Program Files\\Sunshine 安装，"
                        + "不会改写包内配置或用户安装。";
                return "当前宿主不在 C:\\Program Files\\Sunshine，一键调优与显示脚本没有适配该路径；"
                    + "为避免误改，本次不提供一键调优。";
            }
        }

        internal static string ActiveHostStateText
        {
            get
            {
                string running = RunningInstalledRoot;
                if (!string.IsNullOrEmpty(running))
                {
                    if (IsPathUnder(running, LocalRoot))
                        return "正在使用已准备的内置 Foundation Sunshine " + BundleVersion + "。";
                    return "正在复用本机已运行的 Sunshine：" + running + "（未启动第二个实例）。";
                }
                if (AnySunshineProcessRunning)
                    return "检测到 Sunshine 进程，但当前用户无法确认其真实路径；为避免双开，不自动启动包内宿主。";
                if (LocalRuntimePresent)
                    return "包内 Foundation Sunshine 已解压到稳定目录，当前未运行。";
                if (BundledAvailable)
                    return "包内 Foundation Sunshine " + BundleVersion + " 可用，首次使用需在本机点击准备。";
                if (File.Exists(Path.Combine(DefaultInstalledRoot, ExecutableName)))
                    return "检测到 C:\\Program Files\\Sunshine 安装但当前未运行；不会自动启动或改写其配置。";
                return "未检测到可用的本机 Sunshine 宿主。";
            }
        }

        /// <summary>
        /// 用户在本机明确点击“准备/允许”后调用。已有 Sunshine 正在运行时只复核其
        /// local API、令牌与证书并复用；否则把固定版本程序文件准备到稳定目录，启动后
        /// 等待上述三项真实就绪。仅进程存活 2.5 秒不会报告成功。
        /// </summary>
        internal static bool TryPrepareAndStart(out string message)
        {
            lock (LaunchLock)
            {
                return TryPrepareAndStartCore(out message);
            }
        }

        private static bool TryPrepareAndStartCore(out string message)
        {
            message = null;
            string running = RunningInstalledRoot;
            if (!string.IsNullOrEmpty(running)) return TryReportRunningHost(running, out message);
            if (AnySunshineProcessRunning)
            {
                message = "检测到 Sunshine 进程，但无法确认其真实可执行路径。为避免启动第二个实例，"
                    + "本次没有启动包内宿主；请检查该进程或改用当前运行实例后重试。";
                return false;
            }
            if (!BundledAvailable)
            {
                string installed = Path.Combine(DefaultInstalledRoot, ExecutableName);
                message = File.Exists(installed)
                    ? "包内 Foundation Sunshine 不可用；检测到 C:\\Program Files\\Sunshine，但本工具不会自动启动或改写用户安装。"
                    : "包内 Foundation Sunshine 不可用，无法准备本机被控服务。";
                return false;
            }

            try
            {
                string verifyError;
                if (!TryVerifyBundledExecutable(out verifyError))
                {
                    message = "包内 Foundation Sunshine 校验失败：" + verifyError;
                    return false;
                }

                // 检查与复制之间仍可能有宿主启动；再次检查，宁可复用也不双开。
                running = RunningInstalledRoot;
                if (!string.IsNullOrEmpty(running)) return TryReportRunningHost(running, out message);
                if (AnySunshineProcessRunning)
                {
                    message = "准备过程中检测到其他 Sunshine 进程，已停止启动包内宿主以避免双开。";
                    return false;
                }

                string updateMessage;
                if (!TryEnsureRuntimeFiles(out updateMessage))
                {
                    message = updateMessage;
                    return false;
                }
                int basePort;
                int webPort;
                string portError;
                if (!TryReadPort(Path.Combine(LocalConfigDirectory, ConfigFileName), out basePort))
                {
                    if (!TryFindFreePortPair(out basePort, out webPort, out portError))
                    {
                        message = "无法为包内宿主分配独立端口：" + portError;
                        return false;
                    }
                }
                else
                {
                    webPort = basePort + 1;
                }
                PrepareLocalConfig(basePort, webPort);

                string executable = Path.Combine(LocalRoot, ExecutableName);
                if (!File.Exists(executable))
                {
                    message = "稳定运行目录缺少 sunshine.exe，无法启动包内宿主。";
                    return false;
                }

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = executable;
                info.WorkingDirectory = LocalRoot;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.Arguments = "\"" + Path.Combine(LocalConfigDirectory, ConfigFileName) + "\"";

                Process process = Process.Start(info);
                if (process == null)
                {
                    message = "启动包内 Foundation Sunshine 失败；请检查稳定目录权限和杀毒软件拦截。";
                    return false;
                }

                try
                {
                    ownedProcessId = process.Id;
                    ownedProcessStartTicks = process.StartTime.ToUniversalTime().Ticks;
                    ownedExecutablePath = executable;
                    ownedConfigDirectory = LocalConfigDirectory;
                    ownedBasePort = basePort;
                    ownedWebPort = webPort;
                    if (process.WaitForExit(StartProbeMilliseconds))
                    {
                        int exitCode = -1;
                        try { exitCode = process.ExitCode; } catch { }
                        message = exitCode == 0
                            ? "包内 Foundation Sunshine 已退出（退出码 0），未进入服务就绪状态；请查看 config\\sunshine.log。"
                            : "包内 Foundation Sunshine 启动后退出（退出码 " + exitCode
                                + "）；请查看 config\\sunshine.log，并确认未被安全软件拦截。";
                        return false;
                    }

                    Stopwatch clock = Stopwatch.StartNew();
                    string readinessError = "";
                    while (clock.ElapsedMilliseconds < ReadinessTimeoutMilliseconds)
                    {
                        if (TryCheckReadiness(LocalRoot, LocalConfigDirectory, out readinessError))
                        {
                            message = "已准备并启动包内 Foundation Sunshine " + BundleVersion
                                + "；独立基础端口 " + basePort + "、Web UI 端口 " + webPort
                                + "；本地 API、local-auth.token 与 CA 证书均已就绪，未修改系统服务、"
                                + "防火墙、注册表或驱动。请在设备页完成本机确认后配对。";
                            return true;
                        }
                        if (process.HasExited)
                        {
                            int exitCode = -1;
                            try { exitCode = process.ExitCode; } catch { }
                            message = "包内 Foundation Sunshine 在初始化期间退出（退出码 " + exitCode
                                + "）；请查看 config\\sunshine.log。";
                            return false;
                        }
                        System.Threading.Thread.Sleep(500);
                    }

                    message = "包内 Foundation Sunshine 进程仍存活，但在 "
                        + (ReadinessTimeoutMilliseconds / 1000) + " 秒内未确认 local API/令牌/证书就绪："
                        + (string.IsNullOrEmpty(readinessError) ? "未返回具体原因。" : readinessError)
                        + " 首次启动或安全软件拦截时可能更慢；可稍后刷新，期间不会重复启动第二个实例。";
                    return false;
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                message = "准备包内 Foundation Sunshine 失败：" + SafeText(ex.Message);
                return false;
            }
        }

        internal static bool TryStopBundled(out string message)
        {
            lock (LaunchLock)
            {
                if (ownedProcessId <= 0) { message = "本程序当前没有记录到自己启动的包内宿主，未执行停止。"; return false; }
                Process process = null;
                try
                {
                    process = Process.GetProcessById(ownedProcessId);
                    string executable = TryGetProcessExecutablePath(process);
                    bool sameStart = process.StartTime.ToUniversalTime().Ticks == ownedProcessStartTicks;
                    if (!sameStart || !SamePath(executable, ownedExecutablePath) || !IsPathUnder(executable, LocalRoot))
                    {
                        message = "记录的身份与当前进程不一致，未停止任何进程。";
                        return false;
                    }
                    if (!process.HasExited)
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000)) { message = "已请求停止包内宿主 PID " + ownedProcessId + "，但 5 秒内未退出。"; return false; }
                    }
                    int stopped = ownedProcessId;
                    ClearOwnedProcess();
                    message = "已停止本程序启动的包内宿主 PID " + stopped + "。";
                    return true;
                }
                catch (Exception ex)
                {
                    ClearOwnedProcess();
                    message = "包内宿主当前不可核查或已退出，未停止其他进程：" + SafeText(ex.Message);
                    return false;
                }
                finally { if (process != null) { try { process.Dispose(); } catch { } } }
            }
        }

        private static void ClearOwnedProcess()
        {
            ownedProcessId = 0;
            ownedProcessStartTicks = 0;
            ownedExecutablePath = null;
            ownedConfigDirectory = null;
            ownedBasePort = 0;
            ownedWebPort = 0;
        }

        private static bool TryReportRunningHost(string root, out string message)
        {
            bool bundledRuntime = IsPathUnder(root, LocalRoot);
            if (!bundledRuntime)
            {
                message = "已有 Sunshine 正在运行，使用其配置/证书；未重复启动。该实例路径：" + root
                    + "。其配置/证书由现有实例持有，具体授权可用性请在配对页检查。";
                return true;
            }
            string readinessError;
            if (TryCheckReadiness(root, LocalConfigDirectory, out readinessError))
            {
                message = "已复用已准备的内置宿主（" + root + "），未启动第二个实例；"
                    + "local API、local-auth.token 与 CA 证书均已就绪；基础端口 "
                    + ActiveBasePort + "、Web UI 端口 " + ActiveWebUiPort + "。";
                if (!RuntimeProgramFilesCurrent())
                    message += " 包内程序文件有新版本，但宿主正在运行，本次不会覆盖；待其停止后的下次准备再更新。";
                return true;
            }

            message = "已检测到已准备的内置宿主（" + root + "），为避免双开没有启动第二个实例；"
                + "但应用内配对接口尚未就绪："
                + (string.IsNullOrEmpty(readinessError) ? "未返回具体原因。" : readinessError);
            return false;
        }

        private static bool TryCheckReadiness(string root, string config, out string error)
        {
            error = null;
            try
            {
                string reason;
                if (SunshinePairing.TryGetAvailability(root, config, out reason)) return true;
                error = reason;
                return false;
            }
            catch (Exception ex)
            {
                error = "检查本机配对接口失败：" + SafeText(ex.Message);
                return false;
            }
        }

        internal static bool TryVerifyBundledExecutable(out string error)
        {
            error = null;
            try
            {
                string exe = BundledSourceExecutable;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    error = "engines\\Sunshine\\sunshine.exe 不存在。";
                    return false;
                }
                FileInfo item = new FileInfo(exe);
                if (item.Length != BundleExecutableSize)
                {
                    error = "sunshine.exe 大小为 " + item.Length + " 字节，应为 "
                        + BundleExecutableSize + " 字节。";
                    return false;
                }
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(exe);
                if (string.IsNullOrEmpty(version.FileVersion)
                    || !version.FileVersion.StartsWith(BundleVersion, StringComparison.OrdinalIgnoreCase))
                {
                    error = "sunshine.exe 文件版本为 " + (version.FileVersion ?? "空")
                        + "，应为 " + BundleVersion + "。";
                    return false;
                }
                string hash = ComputeSha256(exe);
                if (!string.Equals(hash, BundleExecutableSha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = "sunshine.exe SHA256 为 " + hash + "，应为 "
                        + BundleExecutableSha256 + "。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = SafeText(ex.Message);
                return false;
            }
        }

        private static bool RuntimeProgramFilesCurrent()
        {
            try
            {
                string localExe = Path.Combine(LocalRoot, ExecutableName);
                if (!File.Exists(localExe)) return false;
                if (!File.Exists(Path.Combine(LocalRoot, "assets", AppsFileName))) return false;
                if (!File.Exists(Path.Combine(LocalRoot, "assets", "desktop.png"))) return false;
                if ((new FileInfo(localExe)).Length != BundleExecutableSize) return false;
                string localHash = ComputeSha256(localExe);
                if (!string.Equals(localHash, BundleExecutableSha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                string stampPath = Path.Combine(LocalRoot, RuntimeStampFileName);
                if (!File.Exists(stampPath)) return false;
                string stamp = File.ReadAllText(stampPath, Encoding.UTF8).Trim();
                return string.Equals(stamp, RuntimeStamp(), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryEnsureRuntimeFiles(out string message)
        {
            message = null;
            string source = BundledSourceRoot;
            if (RuntimeProgramFilesCurrent())
            {
                message = "包内程序文件已是当前固定版本，保留现有 config 与配对凭据。";
                return true;
            }
            if (AnySunshineProcessRunning)
            {
                message = "包内程序文件需要更新，但当前有 Sunshine 在运行；为避免覆盖正在使用的程序，本次不更新。";
                return false;
            }

            try
            {
                CopyRuntimeFiles(source, LocalRoot);
                Directory.CreateDirectory(LocalRoot);
                File.WriteAllText(Path.Combine(LocalRoot, RuntimeStampFileName),
                    RuntimeStamp(), new UTF8Encoding(false));
                message = "已将包内固定版本程序文件更新到稳定目录；config、令牌与证书未被覆盖。";
                return true;
            }
            catch (Exception ex)
            {
                message = "更新包内宿主程序文件失败：" + SafeText(ex.Message);
                return false;
            }
        }

        private static string RuntimeStamp()
        {
            return BundleVersion + "|" + BundleExecutableSha256;
        }

        private static bool TryReadPort(string conf, out int port)
        {
            port = 0;
            try
            {
                if (!File.Exists(conf)) return false;
                foreach (string line in File.ReadAllLines(conf, Encoding.UTF8))
                {
                    string value = line.Trim();
                    if (!value.StartsWith("port", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = value.IndexOf('=');
                    if (eq < 0) continue;
                    return int.TryParse(value.Substring(eq + 1).Trim(), out port) && port > 0 && port < 65534;
                }
            }
            catch { }
            return false;
        }

        internal const int StandardBasePort = 47989;

        internal static bool TryFindFreePortPair(out int basePort, out int webPort, out string error)
        {
            error = null;
            basePort = 0;
            webPort = 0;
            // 优先使用 Sunshine/Moonlight 标准端口 47989（对应 HTTPS 47984、WebUI 47990、RTSP 48010），
            // 确保开箱即用时对端 Moonlight 直接输入 IP 即可免端口直连配对与串流；仅在标准端口被占用时才回退至独立端口段。
            if (IsSunshinePortGroupFree(StandardBasePort))
            {
                basePort = StandardBasePort;
                webPort = StandardBasePort + 1;
                return true;
            }
            for (int candidate = 48989; candidate <= 59989; candidate += 2)
            {
                if (!IsSunshinePortGroupFree(candidate)) continue;
                basePort = candidate;
                webPort = candidate + 1;
                return true;
            }
            error = "未找到可用的本机端口组（标准端口 47989 与备选范围 48989-59989 均被占用）。";
            return false;
        }

        private static bool IsSunshinePortGroupFree(int basePort)
        {
            return IsTcpPortFree(basePort - 5)
                && IsTcpPortFree(basePort)
                && IsTcpPortFree(basePort + 1)
                && IsTcpPortFree(basePort + 21);
        }

        private static bool IsTcpPortFree(int port)
        {
            if (port <= 0 || port > 65535) return false;
            try
            {
                var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                foreach (IPEndPoint ep in props.GetActiveTcpListeners())
                {
                    if (ep.Port == port) return false;
                }
            }
            catch { }
            return CanBindPort(port);
        }

        private static bool CanBindPort(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch { return false; }
            finally { if (listener != null) { try { listener.Stop(); } catch { } } }
        }

        private static void PrepareLocalConfig(int basePort, int webPort)
        {
            Directory.CreateDirectory(LocalConfigDirectory);
            string conf = Path.Combine(LocalConfigDirectory, ConfigFileName);
            if (!File.Exists(conf))
            {
                string text = "address_family = both\r\n"
                    + "port = " + basePort.ToString() + "\r\n"
                    + "fec_auto = enabled\r\n"
                    + "fec_percentage = 20\r\n"
                    + "fec_auto_max_percentage = 50\r\n"
                    + "ping_timeout = 20000\r\n";
                File.WriteAllText(conf, text, new UTF8Encoding(false));
            }
            string localApps = Path.Combine(LocalConfigDirectory, AppsFileName);
            if (File.Exists(localApps)) return;
            string sourceRoot = BundledSourceRoot;
            string sourceApps = string.IsNullOrEmpty(sourceRoot) ? null : Path.Combine(sourceRoot, "assets", AppsFileName);
            if (!string.IsNullOrEmpty(sourceApps) && File.Exists(sourceApps)) File.Copy(sourceApps, localApps, false);
        }

        private static void CopyRuntimeFiles(string source, string target)
        {
            string sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            string targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(targetFull);

            foreach (string path in Directory.GetFiles(sourceFull, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(sourceFull.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (ShouldSkip(relative)) continue;
                string destination = Path.Combine(targetFull, relative);
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(path, destination, true);
            }
        }

        private static bool ShouldSkip(string relative)
        {
            string normalized = (relative ?? "").Replace('/', '\\').TrimStart('\\');
            if (normalized.Length == 0) return true;
            string first = normalized.Split('\\')[0];
            if (string.Equals(first, ConfigName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, CredentialsName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "logs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "logs_archive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "crashes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "cache", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "dumps", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "telemetry", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(first, RuntimeStampFileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(first, StateFileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(first, "sunshine.log", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("X2"));
                return text.ToString();
            }
        }

        private static string TryGetProcessExecutablePath(Process process)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle != IntPtr.Zero)
                {
                    StringBuilder buffer = new StringBuilder(32768);
                    int size = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                        return buffer.ToString(0, size);
                }
            }
            catch
            {
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try { CloseHandle(handle); } catch { }
                }
            }

            try
            {
                return process.MainModule == null ? null : process.MainModule.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static Process[] GetSunshineProcesses()
        {
            try { return Process.GetProcessesByName(ProcessName); }
            catch { return new Process[0]; }
        }

        private static bool TryGetExplicitConfigPath(string executable, out string configPath)
        {
            configPath = null;
            Process[] list = null;
            try
            {
                list = GetSunshineProcesses();
                foreach (Process process in list)
                {
                    string path = TryGetProcessExecutablePath(process);
                    if (string.IsNullOrEmpty(path) || !SamePath(path, executable)) continue;
                    string commandLine = TryGetWmiCommandLine(process.Id);
                    if (string.IsNullOrEmpty(commandLine)) return false;
                    string candidate = ExtractConfigArgument(commandLine);
                    if (string.IsNullOrEmpty(candidate) || !Path.IsPathRooted(candidate) || !File.Exists(candidate)) return false;
                    configPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
            catch { }
            finally
            {
                if (list != null)
                {
                    foreach (Process process in list)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            return false;
        }

        private static string TryGetWmiCommandLine(int processId)
        {
            object locator = null;
            object service = null;
            object results = null;
            object item = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (type == null) return null;
                locator = Activator.CreateInstance(type);
                service = type.InvokeMember("ConnectServer", BindingFlags.InvokeMethod, null, locator,
                    new object[] { ".", "root\\cimv2" });
                if (service == null) return null;
                results = service.GetType().InvokeMember("ExecQuery", BindingFlags.InvokeMethod, null, service,
                    new object[] { "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + processId });
                if (results == null) return null;
                foreach (object value in (IEnumerable)results) { item = value; break; }
                if (item == null) return null;
                object command = item.GetType().InvokeMember("CommandLine", BindingFlags.GetProperty, null, item, null);
                return command == null ? null : Convert.ToString(command);
            }
            catch { return null; }
            finally
            {
                ReleaseCom(item);
                ReleaseCom(results);
                ReleaseCom(service);
                ReleaseCom(locator);
            }
        }

        private static void ReleaseCom(object value)
        {
            if (value == null) return;
            try { Marshal.ReleaseComObject(value); } catch { }
        }

        private static string ExtractConfigArgument(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return null;
            string best = null;
            int i = 0;
            while (i < commandLine.Length)
            {
                while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i])) i++;
                if (i >= commandLine.Length) break;
                string token;
                if (commandLine[i] == '"')
                {
                    i++;
                    int start = i;
                    while (i < commandLine.Length && commandLine[i] != '"') i++;
                    token = commandLine.Substring(start, i - start);
                    if (i < commandLine.Length) i++;
                }
                else
                {
                    int start = i;
                    while (i < commandLine.Length && !char.IsWhiteSpace(commandLine[i])) i++;
                    token = commandLine.Substring(start, i - start);
                }
                if (token.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) best = token;
            }
            return best;
        }

        private static bool IsPathUnder(string path, string parent)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parent)) return false;
            try
            {
                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase)) return true;
                return fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string SafeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder builder = new StringBuilder();
            foreach (char c in text)
            {
                char value = (c == '\r' || c == '\n' || char.IsControl(c)) ? ' ' : c;
                builder.Append(value);
                if (builder.Length >= 200) break;
            }
            return builder.ToString().Trim();
        }
    }
}
