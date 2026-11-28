using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DualRemote
{
    /// <summary>
    /// V7.6 Windows SYSTEM 服务与 Winlogon 安全桌面命名管道桥接：
    /// 1) BoBoRemoteService (--service) 运行在 NT AUTHORITY\SYSTEM，监听 Console 会话切换并拉起同会话 SYSTEM Helper；
    /// 2) --desktop-helper (--session=&lt;sid&gt;) 在活动桌面会话中运行采编引擎与独立无 HWND 输入线程，自动跟随 Default &lt;-&gt; Winlogon &lt;-&gt; UAC 切换并支持 sas.dll!SendSAS(FALSE)；
    /// 3) BoBoSecureDesktopBridge 自动优先连接本地 SYSTEM Helper（可用时全程零切换中断），不可用时无缝回退至进程内直连。
    /// </summary>
    internal static class BoBoSystemService
    {
        internal const string ServiceName = "BoBoRemoteService";
        internal const string ServiceDisplayName = "BoBo 远程桌面安全桌面与锁屏穿透服务";
        internal const string ServiceDescription = "允许在 Windows 锁屏界面 (Winlogon)、Ctrl+Alt+Del 凭证界面及 UAC 提权窗口下持续采集画面、注入键鼠输入并发送安全注意序列 (SAS)。";
        internal const string PipeNamePrefix = "BoBoRemote.DesktopHelper.";

        private const int ConnectTimeoutMs = 950;
        private const int RpcTimeoutMs = 4500;
        private const int MaxFrameBytes = 16 * 1024 * 1024;

        internal static string GetPipeNameForSession(int sessionId)
        {
            return PipeNamePrefix + sessionId;
        }

        internal static bool IsRunningAsSystem()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    if (id != null && id.User != null)
                    {
                        var sys = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                        return id.User.Equals(sys);
                    }
                }
            }
            catch { }
            return false;
        }

        internal static bool IsProcessElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    if (id == null) return false;
                    var principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsServiceInstalled()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    return (int)sc.Status >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsServiceRunning()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static string GetProgramFilesRoot64()
        {
            try
            {
                string pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
                if (!string.IsNullOrWhiteSpace(pf64)) return pf64.Trim();
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        internal static string GetProtectedRootDirectory()
        {
            return Path.Combine(GetProgramFilesRoot64(), "BoBoRemote");
        }

        internal static string GetProtectedServiceDirectory()
        {
            return Path.Combine(GetProtectedRootDirectory(), "Service");
        }

        internal static string GetProtectedServiceExePath()
        {
            return Path.Combine(GetProtectedServiceDirectory(), "BoBo远程.exe");
        }

        internal static string GetProtectedServiceCoreDllPath()
        {
            return Path.Combine(GetProtectedServiceDirectory(), "BoBoCore.dll");
        }

        internal static bool IsPathUnderProtectedProgramFiles(string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath)) return false;
            try
            {
                string trimmed = candidatePath.Trim().Trim('"');
                if (trimmed.Contains("..")) return false;
                string full = Path.GetFullPath(trimmed).TrimEnd('\\') + "\\";
                string pf = GetProgramFilesRoot64();
                if (string.IsNullOrWhiteSpace(pf)) return false;
                string normPf = Path.GetFullPath(pf).TrimEnd('\\') + "\\";
                return full.StartsWith(normPf, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static DirectorySecurity BuildProtectedDirectorySecurity()
        {
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            var sys = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            ds.AddAccessRule(new FileSystemAccessRule(
                sys,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(
                admins,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            return ds;
        }

        internal static void EnsureProtectedDirectoryAcl(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return;
            Directory.CreateDirectory(dirPath);
            DirectorySecurity ds = BuildProtectedDirectorySecurity();
            Directory.SetAccessControl(dirPath, ds);
        }

        internal static string ExtractExePathFromCommandLine(string cmdLine)
        {
            if (string.IsNullOrWhiteSpace(cmdLine)) return "";
            string s = cmdLine.Trim();
            string rawPath = s;
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = s.IndexOf('"', 1);
                rawPath = endQuote > 1 ? s.Substring(1, endQuote - 1) : s.Trim('"');
            }
            else
            {
                int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx >= 0)
                {
                    int after = exeIdx + 4;
                    if (after == s.Length || char.IsWhiteSpace(s[after]))
                        rawPath = s.Substring(0, after);
                }
                else
                {
                    int sp = s.IndexOf(' ');
                    if (sp > 0) rawPath = s.Substring(0, sp);
                }
            }
            rawPath = (rawPath ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(rawPath)) return "";
            try { return Path.GetFullPath(rawPath); }
            catch { return rawPath; }
        }

        internal static string QueryRegisteredServiceBinaryPath()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return "";
            IntPtr svc = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                svc = OpenServiceW(scm, ServiceName, SERVICE_QUERY_CONFIG);
                if (svc == IntPtr.Zero) return "";
                int needed = 0;
                QueryServiceConfigW(svc, IntPtr.Zero, 0, out needed);
                if (needed <= 0) needed = 4096;
                buf = Marshal.AllocHGlobal(needed);
                if (!QueryServiceConfigW(svc, buf, needed, out needed)) return "";
                var cfg = (QUERY_SERVICE_CONFIG)Marshal.PtrToStructure(buf, typeof(QUERY_SERVICE_CONFIG));
                return ExtractExePathFromCommandLine(cfg.lpBinaryPathName);
            }
            catch
            {
                return "";
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                CloseServiceHandle(scm);
            }
        }

        internal static void InspectInstalledService(
            out bool installed,
            out bool running,
            out bool securePath,
            out string exePath,
            out string binaryVersion)
        {
            installed = false;
            running = false;
            securePath = false;
            exePath = "";
            binaryVersion = "";

            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    ServiceControllerStatus st = sc.Status;
                    installed = (int)st >= 0;
                    running = (st == ServiceControllerStatus.Running);
                }
            }
            catch
            {
                installed = false;
                running = false;
            }
            if (!installed) return;

            exePath = QueryRegisteredServiceBinaryPath();
            securePath = IsPathUnderProtectedProgramFiles(exePath);
            if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    string svcDir = Path.GetDirectoryName(exePath);
                    string verStamp = string.IsNullOrEmpty(svcDir) ? null : Path.Combine(svcDir, "service-version.txt");
                    if (!string.IsNullOrEmpty(verStamp) && File.Exists(verStamp))
                    {
                        string textVer = (File.ReadAllText(verStamp, Encoding.UTF8) ?? "").Trim();
                        if (!string.IsNullOrEmpty(textVer)) binaryVersion = textVer;
                    }
                    if (string.IsNullOrEmpty(binaryVersion) && File.Exists(exePath))
                    {
                        FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exePath);
                        string raw = !string.IsNullOrWhiteSpace(fvi.ProductVersion) ? fvi.ProductVersion : fvi.FileVersion;
                        if (!string.IsNullOrWhiteSpace(raw) && !raw.StartsWith("0.0.0", StringComparison.Ordinal))
                        {
                            string[] parts = raw.Trim().Split('.');
                            if (parts.Length >= 3) binaryVersion = parts[0] + "." + parts[1] + "." + parts[2];
                            else binaryVersion = raw.Trim();
                        }
                    }
                }
                catch { }
            }
        }

        internal static bool NeedsServiceUpgradeOrMigration(out string reason)
        {
            reason = "";
            bool installed, running, securePath;
            string exePath, binVer;
            InspectInstalledService(out installed, out running, out securePath, out exePath, out binVer);
            if (!installed) return false;
            if (!securePath)
            {
                reason = "当前锁屏服务仍在用户目录运行，需迁移至受保护系统目录。";
                return true;
            }
            if (!File.Exists(GetProtectedServiceCoreDllPath()))
            {
                reason = "受保护服务目录缺少核心模块，需同步修复。";
                return true;
            }
            if (string.IsNullOrEmpty(binVer) || AppUpdater.CompareVersions(binVer, AppUpdater.CurrentVersion) < 0)
            {
                reason = "锁屏服务版本（" + (string.IsNullOrEmpty(binVer) ? "未知" : "v" + binVer) + "）落后于当前主程序 v" + AppUpdater.CurrentVersion + "。";
                return true;
            }
            return false;
        }

        internal static string GetServiceStatusLabel()
        {
            bool installed, running, securePath;
            string exePath, binVer;
            InspectInstalledService(out installed, out running, out securePath, out exePath, out binVer);
            if (!installed)
            {
                return "未安装（登录后锁屏 / UAC 时系统将拦截画面与输入）";
            }
            if (!securePath)
            {
                return "安全边界待迁移（仍在用户目录运行，请点击升级/迁移至 Program Files）";
            }
            if (!running)
            {
                return "已安装但未启动（受保护系统目录，点击修复启动）";
            }
            string upgReason;
            if (NeedsServiceUpgradeOrMigration(out upgReason))
            {
                return "服务运行中（" + (string.IsNullOrEmpty(binVer) ? "旧版" : "v" + binVer) + "，建议同步升级至 v" + AppUpdater.CurrentVersion + "）";
            }
            return "服务运行中 v" + binVer + "（受保护系统目录 | 支持登录后锁屏/UAC，开机未登录前不监听网络）";
        }

        internal static int RunServiceHost()
        {
            if (IsRunningAsSystem())
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    if (!IsPathUnderProtectedProgramFiles(exe)) return 5;
                }
                catch
                {
                    return 5;
                }
            }
            try
            {
                ServiceBase.Run(new ServiceBase[] { new BoBoWindowsService() });
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        internal static int RunDesktopHelper(int requestedSessionId)
        {
            if (IsRunningAsSystem())
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    if (!IsPathUnderProtectedProgramFiles(exe)) return 5;
                }
                catch
                {
                    return 5;
                }
            }
            int sid = requestedSessionId >= 0 ? requestedSessionId : GetActiveConsoleSessionIdSafe();
            if (sid < 0) sid = Process.GetCurrentProcess().SessionId;
            try
            {
                string err;
                if (!BoBoCoreInterop.EnsureLoaded(out err))
                {
                    return 6;
                }
            }
            catch
            {
                return 6;
            }

            try
            {
                using (var helper = new DesktopHelperServer(GetPipeNameForSession(sid), sid, true))
                {
                    helper.RunBlocking();
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static void StopProcessesMatchingPath(string targetExePath, int excludePid)
        {
            if (string.IsNullOrWhiteSpace(targetExePath)) return;
            string fullTarget;
            try { fullTarget = Path.GetFullPath(targetExePath); }
            catch { return; }
            string procName = Path.GetFileNameWithoutExtension(fullTarget);
            Process[] procs = null;
            try { procs = Process.GetProcessesByName(procName); }
            catch { return; }
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id == excludePid) continue;
                    string pPath = "";
                    try { pPath = Path.GetFullPath(p.MainModule.FileName); } catch { }
                    if (!string.Equals(pPath, fullTarget, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        private static void RollbackProtectedServiceFiles(string targetExe, string targetCoreDll, string targetVerFile, string prevRegisteredExe)
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(6));
                    }
                }
            }
            catch { }

            StopProcessesMatchingPath(targetExe, Process.GetCurrentProcess().Id);

            bool hasBackup = File.Exists(targetExe + ".bak") && File.Exists(targetCoreDll + ".bak");
            if (hasBackup)
            {
                try { File.Copy(targetExe + ".bak", targetExe, true); } catch { }
                try { File.Copy(targetCoreDll + ".bak", targetCoreDll, true); } catch { }
                if (File.Exists(targetVerFile + ".bak"))
                {
                    try { File.Copy(targetVerFile + ".bak", targetVerFile, true); } catch { }
                }
                if (!string.IsNullOrWhiteSpace(prevRegisteredExe) &&
                    IsPathUnderProtectedProgramFiles(prevRegisteredExe) &&
                    File.Exists(prevRegisteredExe))
                {
                    IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
                    if (scm != IntPtr.Zero)
                    {
                        IntPtr svc = IntPtr.Zero;
                        try
                        {
                            svc = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
                            if (svc != IntPtr.Zero)
                            {
                                ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                                    "\"" + prevRegisteredExe + "\" --service", null, IntPtr.Zero, null, null, null, ServiceDisplayName);
                            }
                        }
                        finally
                        {
                            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                            CloseServiceHandle(scm);
                        }
                    }
                }
                try
                {
                    using (var sc = new ServiceController(ServiceName))
                    {
                        if (sc.Status != ServiceControllerStatus.Running)
                        {
                            if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(6));
                        }
                    }
                }
                catch { }
            }
        }

        private static void CleanupBackupFiles(params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                try { if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i])) File.Delete(paths[i]); } catch { }
            }
        }

        internal static bool UpgradeService(out string message, string firewallAppExeOverride = null)
        {
            return InstallAndStartService(out message, firewallAppExeOverride);
        }

        internal static bool InstallAndStartService(out string message, string firewallAppExeOverride = null)
        {
            if (!IsProcessElevated() && !IsRunningAsSystem())
            {
                message = "需要管理员权限才能安装或升级锁屏穿透服务。";
                return false;
            }

            string sourceExe;
            try
            {
                sourceExe = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch (Exception ex)
            {
                message = "无法定位主程序路径：" + ex.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceExe) || !File.Exists(sourceExe))
            {
                message = "主程序文件不存在：" + sourceExe;
                return false;
            }

            string coreErr;
            byte[] coreBytes = BoBoCoreInterop.GetEmbeddedCoreDllBytes(out coreErr);
            if (coreBytes == null)
            {
                message = coreErr ?? "无法提取内嵌 BoBoCore.dll。";
                return false;
            }

            string rootDir = GetProtectedRootDirectory();
            string svcDir = GetProtectedServiceDirectory();
            string targetExe = GetProtectedServiceExePath();
            string targetCoreDll = GetProtectedServiceCoreDllPath();
            string targetVerFile = Path.Combine(svcDir, "service-version.txt");
            string prevRegisteredExe = QueryRegisteredServiceBinaryPath();

            try
            {
                EnsureProtectedDirectoryAcl(rootDir);
                EnsureProtectedDirectoryAcl(svcDir);
            }
            catch (Exception ex)
            {
                message = "创建受保护系统服务目录失败：" + ex.Message;
                return false;
            }

            // Stop active service & helper before replacing files in %ProgramFiles%\BoBoRemote\Service
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                    else if (sc.Status == ServiceControllerStatus.StopPending)
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                }
            }
            catch { }

            int curPid = Process.GetCurrentProcess().Id;
            StopProcessesMatchingPath(targetExe, curPid);
            Thread.Sleep(150);

            bool createdBackup = false;
            try
            {
                CleanupBackupFiles(targetExe + ".bak", targetCoreDll + ".bak", targetVerFile + ".bak");
                if (File.Exists(targetExe))
                {
                    File.Copy(targetExe, targetExe + ".bak", true);
                    createdBackup = true;
                }
                if (File.Exists(targetCoreDll))
                {
                    File.Copy(targetCoreDll, targetCoreDll + ".bak", true);
                    createdBackup = true;
                }
                if (File.Exists(targetVerFile))
                {
                    File.Copy(targetVerFile, targetVerFile + ".bak", true);
                }

                bool sameBinary = false;
                try { sameBinary = string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase); }
                catch { }
                if (!sameBinary)
                {
                    string exeTmp = targetExe + ".tmp";
                    File.Copy(sourceExe, exeTmp, true);
                    if (File.Exists(targetExe)) File.Delete(targetExe);
                    File.Move(exeTmp, targetExe);
                }

                string dllTmp = targetCoreDll + ".tmp";
                File.WriteAllBytes(dllTmp, coreBytes);
                if (File.Exists(targetCoreDll)) File.Delete(targetCoreDll);
                File.Move(dllTmp, targetCoreDll);

                File.WriteAllText(targetVerFile, AppUpdater.CurrentVersion ?? "", new UTF8Encoding(false));
                EnsureProtectedDirectoryAcl(svcDir);
            }
            catch (Exception ex)
            {
                if (createdBackup) RollbackProtectedServiceFiles(targetExe, targetCoreDll, targetVerFile, prevRegisteredExe);
                message = "复制服务程序与核心模块至受保护目录失败：" + ex.Message;
                return false;
            }

            string binPath = "\"" + targetExe + "\" --service";
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (createdBackup) RollbackProtectedServiceFiles(targetExe, targetCoreDll, targetVerFile, prevRegisteredExe);
                message = "打开 Windows 服务管理器失败（错误码 " + err + "）。";
                return false;
            }

            IntPtr svc = IntPtr.Zero;
            try
            {
                svc = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    svc = CreateServiceW(
                        scm,
                        ServiceName,
                        ServiceDisplayName,
                        SERVICE_ALL_ACCESS,
                        SERVICE_WIN32_OWN_PROCESS,
                        SERVICE_AUTO_START,
                        SERVICE_ERROR_NORMAL,
                        binPath,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null);
                    if (svc == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (createdBackup) RollbackProtectedServiceFiles(targetExe, targetCoreDll, targetVerFile, prevRegisteredExe);
                        message = "创建系统服务失败（错误码 " + err + "）。";
                        return false;
                    }
                }
                else
                {
                    if (!ChangeServiceConfigW(
                        svc,
                        SERVICE_NO_CHANGE,
                        SERVICE_AUTO_START,
                        SERVICE_ERROR_NORMAL,
                        binPath,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null,
                        ServiceDisplayName))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (createdBackup) RollbackProtectedServiceFiles(targetExe, targetCoreDll, targetVerFile, prevRegisteredExe);
                        message = "更新系统服务配置失败（错误码 " + err + "）。";
                        return false;
                    }
                }

                ConfigureServiceMetadataAndRecovery(svc);
            }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                CloseServiceHandle(scm);
            }

            EnsureSoftwareSasPolicy();
            try
            {
                string tsnetExe = BoBoEmbeddedTailnet.GetExpectedBinaryPath();
                string fwAppExe = !string.IsNullOrWhiteSpace(firewallAppExeOverride) ? firewallAppExeOverride : sourceExe;
                BoBoNatTraversal.ConfigureFirewallRulesForPaths(fwAppExe, tsnetExe, true);
            }
            catch { }

            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }

                int sid = GetActiveConsoleSessionIdSafe();
                if (sid >= 0)
                {
                    bool pipeVerified = false;
                    string pipeVer = "";
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        using (DesktopHelperClient probe = DesktopHelperClient.TryConnect(GetPipeNameForSession(sid), 400))
                        {
                            if (probe != null)
                            {
                                pipeVer = probe.ServerVersion;
                                pipeVerified = string.IsNullOrEmpty(pipeVer) ||
                                    string.Equals(pipeVer, AppUpdater.CurrentVersion, StringComparison.OrdinalIgnoreCase);
                                if (pipeVerified) break;
                            }
                        }
                        Thread.Sleep(250);
                    }
                    if (!pipeVerified)
                    {
                        if (createdBackup) RollbackProtectedServiceFiles(targetExe, targetCoreDll, targetVerFile, prevRegisteredExe);
                        message = "服务已启动但未能验证会话采编管道（版本：" + (pipeVer ?? "无响应") + "），已自动回滚。";
                        return false;
                    }
                }

                CleanupBackupFiles(targetExe + ".bak", targetCoreDll + ".bak", targetVerFile + ".bak");
                message = "锁屏穿透服务已部署至受保护目录并启动校验通过（v" + AppUpdater.CurrentVersion + "）。";
                return true;
            }
            catch (Exception ex)
            {
                if (createdBackup) RollbackProtectedServiceFiles(targetExe, targetCoreDll, targetVerFile, prevRegisteredExe);
                message = "服务启动校验失败：" + ex.Message;
                return false;
            }
        }

        internal static bool UninstallService(out string message)
        {
            if (!IsProcessElevated() && !IsRunningAsSystem())
            {
                message = "需要管理员权限才能卸载系统服务。";
                return false;
            }

            string prevExe = QueryRegisteredServiceBinaryPath();
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                }
            }
            catch { }

            int curPid = Process.GetCurrentProcess().Id;
            StopProcessesMatchingPath(GetProtectedServiceExePath(), curPid);
            if (!string.IsNullOrEmpty(prevExe) && IsPathUnderProtectedProgramFiles(prevExe))
            {
                StopProcessesMatchingPath(prevExe, curPid);
            }

            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                message = "无法打开 Windows 服务管理器。";
                return false;
            }
            try
            {
                IntPtr svc = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
                if (svc != IntPtr.Zero)
                {
                    try
                    {
                        bool deleted = DeleteService(svc);
                        int err = deleted ? 0 : Marshal.GetLastWin32Error();
                        if (!deleted && err != ERROR_SERVICE_MARKED_FOR_DELETE)
                        {
                            message = "删除服务失败（错误码 " + err + "）。";
                            return false;
                        }
                    }
                    finally
                    {
                        CloseServiceHandle(svc);
                    }
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }

            try
            {
                BoBoNatTraversal.ConfigureFirewallRulesForPaths(null, null, false);
            }
            catch { }

            string svcDir = GetProtectedServiceDirectory();
            string rootDir = GetProtectedRootDirectory();
            try { if (Directory.Exists(svcDir)) Directory.Delete(svcDir, true); } catch { }
            try { if (Directory.Exists(rootDir) && Directory.GetFileSystemEntries(rootDir).Length == 0) Directory.Delete(rootDir, false); } catch { }

            message = "锁屏穿透服务与受保护服务目录已卸载。";
            return true;
        }

        internal static bool TryInstallOrRepairElevated(out string message)
        {
            if (IsProcessElevated() || IsRunningAsSystem())
            {
                return InstallAndStartService(out message);
            }
            try
            {
                string mainExe = Process.GetCurrentProcess().MainModule.FileName;
                string switchArg = IsServiceInstalled() ? "--upgrade-service" : "--install-service";
                var psi = new ProcessStartInfo(mainExe, switchArg)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        message = "未能启动提权安装进程。";
                        return false;
                    }
                    if (!p.WaitForExit(25000))
                    {
                        message = "提权服务配置超时（25s）。";
                        return false;
                    }
                    string upgReason;
                    bool healthy = IsServiceRunning() && !NeedsServiceUpgradeOrMigration(out upgReason);
                    if (p.ExitCode == 0 && healthy)
                    {
                        message = "锁屏穿透服务已部署至受保护系统目录并校验通过。";
                        return true;
                    }
                    if (p.ExitCode == 0 && IsServiceRunning())
                    {
                        message = "锁屏穿透服务与防火墙规则已就绪。";
                        return true;
                    }
                    message = "服务配置未通过（退出码 " + p.ExitCode + "），详情可在设置页查看服务状态。";
                    return false;
                }
            }
            catch (Win32Exception ex)
            {
                message = ex.NativeErrorCode == 1223
                    ? "已取消管理员授权。"
                    : "提权安装失败：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                message = "提权安装失败：" + ex.Message;
                return false;
            }
        }

        internal static void EnsureSoftwareSasPolicy()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (key == null) return;
                    object curObj = key.GetValue("SoftwareSASGeneration");
                    int cur = 0;
                    if (curObj is int) cur = (int)curObj;
                    // 1 = Services only, 3 = Services and Ease of Access
                    if (cur != 1 && cur != 3)
                    {
                        key.SetValue("SoftwareSASGeneration", cur == 2 ? 3 : 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private static void ConfigureServiceMetadataAndRecovery(IntPtr svc)
        {
            IntPtr descStrPtr = IntPtr.Zero;
            IntPtr descStructPtr = IntPtr.Zero;
            IntPtr actionsPtr = IntPtr.Zero;
            IntPtr sfaPtr = IntPtr.Zero;
            try
            {
                var desc = new SERVICE_DESCRIPTION { lpDescription = ServiceDescription };
                descStructPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_DESCRIPTION)));
                Marshal.StructureToPtr(desc, descStructPtr, false);
                ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, descStructPtr);

                var action = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 2000 };
                int actionSize = Marshal.SizeOf(typeof(SC_ACTION));
                actionsPtr = Marshal.AllocHGlobal(actionSize * 3);
                for (int i = 0; i < 3; i++)
                {
                    Marshal.StructureToPtr(action, IntPtr.Add(actionsPtr, i * actionSize), false);
                }
                var sfa = new SERVICE_FAILURE_ACTIONS
                {
                    dwResetPeriod = 86400,
                    lpRebootMsg = null,
                    lpCommand = null,
                    cActions = 3,
                    lpsaActions = actionsPtr
                };
                sfaPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_FAILURE_ACTIONS)));
                Marshal.StructureToPtr(sfa, sfaPtr, false);
                ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, sfaPtr);
            }
            catch { }
            finally
            {
                if (descStrPtr != IntPtr.Zero) Marshal.FreeHGlobal(descStrPtr);
                if (descStructPtr != IntPtr.Zero) Marshal.FreeHGlobal(descStructPtr);
                if (actionsPtr != IntPtr.Zero) Marshal.FreeHGlobal(actionsPtr);
                if (sfaPtr != IntPtr.Zero) Marshal.FreeHGlobal(sfaPtr);
            }
        }

        // ==================== Windows Service Runtime ====================

        private sealed class BoBoWindowsService : ServiceBase
        {
            private readonly object sync = new object();
            private volatile bool stopping;
            private Thread watchdogThread;
            private Process helperProcess;
            private int helperSessionId = -1;

            internal BoBoWindowsService()
            {
                ServiceName = BoBoSystemService.ServiceName;
                CanStop = true;
                CanShutdown = true;
                CanHandleSessionChangeEvent = true;
                AutoLog = false;
            }

            protected override void OnStart(string[] args)
            {
                stopping = false;
                EnsureSoftwareSasPolicy();
                watchdogThread = new Thread(WatchdogLoop)
                {
                    IsBackground = true,
                    Name = "BoBoServiceWatchdog"
                };
                watchdogThread.Start();
            }

            protected override void OnStop()
            {
                stopping = true;
                KillHelper();
                Thread t = watchdogThread;
                if (t != null)
                {
                    try { t.Join(2000); } catch { }
                }
            }

            protected override void OnShutdown()
            {
                OnStop();
            }

            protected override void OnSessionChange(SessionChangeDescription changeDescription)
            {
                if (!stopping)
                {
                    ThreadPool.QueueUserWorkItem(delegate { EnsureHelperMatchesConsoleSession(); });
                }
            }

            private void WatchdogLoop()
            {
                while (!stopping)
                {
                    EnsureHelperMatchesConsoleSession();
                    for (int i = 0; i < 15 && !stopping; i++) Thread.Sleep(150);
                }
            }

            private void EnsureHelperMatchesConsoleSession()
            {
                int consoleSid = GetActiveConsoleSessionIdSafe();
                if (consoleSid < 0) return;

                lock (sync)
                {
                    if (stopping) return;
                    bool alive = false;
                    if (helperProcess != null)
                    {
                        try { alive = !helperProcess.HasExited; }
                        catch { alive = false; }
                    }
                    if (alive && helperSessionId == consoleSid) return;

                    if (helperProcess != null)
                    {
                        KillHelperLocked();
                    }
                    Process spawned = LaunchSystemHelperInSession(consoleSid);
                    if (spawned != null)
                    {
                        helperProcess = spawned;
                        helperSessionId = consoleSid;
                    }
                }
            }

            private void KillHelper()
            {
                lock (sync) { KillHelperLocked(); }
            }

            private void KillHelperLocked()
            {
                Process p = helperProcess;
                helperProcess = null;
                helperSessionId = -1;
                if (p == null) return;
                try
                {
                    if (!p.HasExited) p.Kill();
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
        }

        private static int GetActiveConsoleSessionIdSafe()
        {
            try
            {
                uint sid = WTSGetActiveConsoleSessionId();
                return sid == 0xFFFFFFFFu ? -1 : unchecked((int)sid);
            }
            catch
            {
                return -1;
            }
        }

        private static Process LaunchSystemHelperInSession(int sessionId)
        {
            string mainExe;
            try
            {
                mainExe = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(mainExe) || !File.Exists(mainExe)) return null;
            if (!IsPathUnderProtectedProgramFiles(mainExe)) return null;

            IntPtr curToken = IntPtr.Zero;
            IntPtr dupToken = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out curToken)) return null;
                if (!DuplicateTokenEx(curToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dupToken)) return null;
                uint sid = unchecked((uint)sessionId);
                if (!SetTokenInformation(dupToken, TokenSessionId, ref sid, sizeof(uint))) return null;

                CreateEnvironmentBlock(out envBlock, dupToken, false);
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = "winsta0\\default";
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;

                PROCESS_INFORMATION pi;
                string cmdLine = "\"" + mainExe + "\" --desktop-helper --session=" + sessionId;
                uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
                string workDir = Path.GetDirectoryName(mainExe);
                if (!CreateProcessAsUserW(dupToken, mainExe, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags, envBlock, workDir, ref si, out pi))
                {
                    return null;
                }
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                try { return Process.GetProcessById(pi.dwProcessId); }
                catch { return null; }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
                if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
                if (curToken != IntPtr.Zero) CloseHandle(curToken);
            }
        }

        // ==================== Binary Pipe Framing ====================

        private const byte OpCreateSession = 1;
        private const byte OpDestroySession = 2;
        private const byte OpReconfigure = 3;
        private const byte OpSetBitrate = 4;
        private const byte OpSetFps = 5;
        private const byte OpSetCompositeCursor = 6;
        private const byte OpRequestKeyframe = 7;
        private const byte OpSetMuteHost = 8;
        private const byte OpInjectInput = 9;
        private const byte OpInjectUnicode = 10;
        private const byte OpSendSas = 11;
        private const byte OpPing = 12;
        private const byte OpApplySuperDisplay = 13;
        private const byte OpRestoreSuperDisplay = 14;
        private const byte OpEnumerateMonitors = 15;

        private const byte OpVideoFrame = 100;
        private const byte OpAudioFrame = 101;
        private const byte OpCreateReply = 102;
        private const byte OpAck = 104;
        private const byte OpPong = 105;
        private const byte OpSuperDisplayReply = 106;
        private const byte OpMonitorsReply = 107;

        private static bool WritePipeMessage(Stream stream, object writeLock, byte op, byte[] payload)
        {
            if (stream == null) return false;
            int plen = payload != null ? payload.Length : 0;
            byte[] packet = new byte[5 + plen];
            WriteInt32LE(packet, 0, 1 + plen);
            packet[4] = op;
            if (plen > 0) Buffer.BlockCopy(payload, 0, packet, 5, plen);
            lock (writeLock)
            {
                try
                {
                    stream.Write(packet, 0, packet.Length);
                    stream.Flush();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool ReadPipeMessage(Stream stream, out byte op, out byte[] payload)
        {
            op = 0;
            payload = null;
            byte[] lenBuf = new byte[4];
            if (!ReadExact(stream, lenBuf, 0, 4)) return false;
            int len = ReadInt32LE(lenBuf, 0);
            if (len < 1 || len > MaxFrameBytes) return false;
            byte[] body = new byte[len];
            if (!ReadExact(stream, body, 0, len)) return false;
            op = body[0];
            int plen = len - 1;
            if (plen > 0)
            {
                payload = new byte[plen];
                Buffer.BlockCopy(body, 1, payload, 0, plen);
            }
            else
            {
                payload = new byte[0];
            }
            return true;
        }

        private static bool ReadExact(Stream stream, byte[] buf, int off, int count)
        {
            int pos = 0;
            while (pos < count)
            {
                int n = stream.Read(buf, off + pos, count - pos);
                if (n <= 0) return false;
                pos += n;
            }
            return true;
        }

        private static void WriteInt32LE(byte[] buf, int off, int v)
        {
            buf[off] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
            buf[off + 2] = (byte)((v >> 16) & 0xFF);
            buf[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static int ReadInt32LE(byte[] buf, int off)
        {
            return buf[off]
                | (buf[off + 1] << 8)
                | (buf[off + 2] << 16)
                | (buf[off + 3] << 24);
        }

        private static void WriteUInt32LE(byte[] buf, int off, uint v)
        {
            WriteInt32LE(buf, off, unchecked((int)v));
        }

        private static uint ReadUInt32LE(byte[] buf, int off)
        {
            return unchecked((uint)ReadInt32LE(buf, off));
        }

        private static void WriteInt16LE(byte[] buf, int off, short v)
        {
            buf[off] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static short ReadInt16LE(byte[] buf, int off)
        {
            return unchecked((short)(buf[off] | (buf[off + 1] << 8)));
        }

        // ==================== DesktopHelperServer (SYSTEM / Console Session) ====================

        internal sealed class DesktopHelperServer : IDisposable
        {
            private readonly string pipeName;
            private readonly int targetSessionId;
            private readonly bool restrictToConsoleUser;
            private readonly ManualResetEventSlim stopEvent = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim listenerReady = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim handlersDrained = new ManualResetEventSlim(true);
            private readonly object inputQueueLock = new object();
            private readonly Queue<Action> inputQueue = new Queue<Action>();
            private readonly AutoResetEvent inputWakeEvent = new AutoResetEvent(false);
            private readonly object hostGate = new object();
            private readonly object clientsGate = new object();
            private readonly List<NamedPipeServerStream> activeClientPipes = new List<NamedPipeServerStream>();
            private readonly NamedPipeServerStream[] activeAcceptPipes = new NamedPipeServerStream[2];
            private readonly Thread inputThread;
            private readonly Thread serverThread;
            private Thread auxServerThread;

            private volatile bool disposed;
            private int injectedPackets;
            private int sasCalls;
            private int activeClientHandlers;
            private BoBoCoreInterop.BoBoHostNativeHandle activeHostHandle;
            private long sessionToken;

            internal int InjectedPacketCount { get { return Volatile.Read(ref injectedPackets); } }
            internal int SasCallCount { get { return Volatile.Read(ref sasCalls); } }

            internal DesktopHelperServer(string customPipeName, int sessionId, bool restrictAclToSession)
            {
                pipeName = customPipeName;
                targetSessionId = sessionId;
                restrictToConsoleUser = restrictAclToSession;
                inputThread = new Thread(InputWorkerLoop)
                {
                    IsBackground = true,
                    Name = "BoBoHelperInputThread"
                };
                inputThread.Start();
            }

            internal DesktopHelperServer(string customPipeName, int sessionId)
                : this(customPipeName, sessionId, false)
            {
                serverThread = new Thread(RunBlocking)
                {
                    IsBackground = true,
                    Name = "BoBoHelperPipeServer"
                };
                serverThread.Start();
                listenerReady.Wait(1500);
            }

            internal void RunBlocking()
            {
                auxServerThread = new Thread((ThreadStart)delegate { RunAcceptorLoop(1); })
                {
                    IsBackground = true,
                    Name = "BoBoHelperPipeAux"
                };
                auxServerThread.Start();
                try
                {
                    RunAcceptorLoop(0);
                }
                finally
                {
                    Thread aux = auxServerThread;
                    if (aux != null && aux.IsAlive)
                    {
                        try { aux.Join(1200); } catch { }
                    }
                }
            }

            private void RunAcceptorLoop(int slot)
            {
                while (!disposed && !stopEvent.IsSet)
                {
                    NamedPipeServerStream pipe = null;
                    try
                    {
                        SecurityIdentifier grantedUserSid;
                        pipe = CreateSecuredPipeServer(pipeName, targetSessionId, restrictToConsoleUser, out grantedUserSid);
                        activeAcceptPipes[slot] = pipe;
                        IAsyncResult ar = pipe.BeginWaitForConnection(null, null);
                        try { listenerReady.Set(); } catch { }
                        WaitHandle[] waits = new WaitHandle[] { ar.AsyncWaitHandle, stopEvent.WaitHandle };
                        bool recreateForUserLogon = false;
                        while (!disposed && !stopEvent.IsSet)
                        {
                            int idx = WaitHandle.WaitAny(waits, 1500);
                            if (idx == 1)
                            {
                                try { pipe.Dispose(); } catch { }
                                return;
                            }
                            if (idx == 0) break;
                            // WaitTimeout: if DesktopHelper started before user sign-in, recreate pipe ACL once console user SID appears
                            if (restrictToConsoleUser && targetSessionId >= 0)
                            {
                                SecurityIdentifier nowSid = TryGetSessionUserSid(targetSessionId);
                                if (nowSid != null && !nowSid.Equals(grantedUserSid))
                                {
                                    recreateForUserLogon = true;
                                    break;
                                }
                            }
                        }
                        if (recreateForUserLogon || disposed || stopEvent.IsSet)
                        {
                            try { pipe.Dispose(); } catch { }
                            continue;
                        }

                        pipe.EndWaitForConnection(ar);
                        if (!VerifyPipeClientProcess(pipe, targetSessionId, restrictToConsoleUser))
                        {
                            try { pipe.Dispose(); } catch { }
                            continue;
                        }

                        NamedPipeServerStream connectedPipe = pipe;
                        pipe = null;
                        activeAcceptPipes[slot] = null;
                        bool queued = false;
                        lock (clientsGate)
                        {
                            if (disposed || stopEvent.IsSet)
                            {
                                try { connectedPipe.Dispose(); } catch { }
                                break;
                            }
                            activeClientPipes.Add(connectedPipe);
                            activeClientHandlers++;
                            try { handlersDrained.Reset(); } catch { }
                            queued = true;
                        }
                        if (queued)
                        {
                            ThreadPool.QueueUserWorkItem(delegate { HandleClientConnection(connectedPipe); });
                        }
                    }
                    catch
                    {
                        if (!disposed && !stopEvent.IsSet) Thread.Sleep(120);
                    }
                    finally
                    {
                        activeAcceptPipes[slot] = null;
                        if (pipe != null)
                        {
                            try { pipe.Dispose(); } catch { }
                        }
                    }
                }
            }

            private void HandleClientConnection(NamedPipeServerStream pipe)
            {
                var writeLock = new object();
                long connSessionToken = 0;

                try
                {
                    while (!disposed && !stopEvent.IsSet && pipe.IsConnected)
                    {
                        byte op;
                        byte[] payload;
                        if (!ReadPipeMessage(pipe, out op, out payload)) break;

                        if (op == OpPing)
                        {
                            byte acc = BoBoCoreInterop.IsInputDesktopAccessible() ? (byte)1 : (byte)0;
                            byte uipi = BoBoCoreInterop.IsForegroundUipiBlocked() ? (byte)1 : (byte)0;
                            byte[] verBytes = Encoding.UTF8.GetBytes(AppUpdater.CurrentVersion ?? "");
                            byte[] rep = new byte[2 + verBytes.Length];
                            rep[0] = acc;
                            rep[1] = uipi;
                            if (verBytes.Length > 0) Buffer.BlockCopy(verBytes, 0, rep, 2, verBytes.Length);
                            WritePipeMessage(pipe, writeLock, OpPong, rep);
                        }
                        else if (op == OpCreateSession)
                        {
                            if (payload == null || payload.Length < 24)
                            {
                                SendCreateReply(pipe, writeLock, false, 0, 0, "请求参数不完整。");
                                continue;
                            }
                            int monIdx = ReadInt32LE(payload, 0);
                            int w = ReadInt32LE(payload, 4);
                            int h = ReadInt32LE(payload, 8);
                            int fps = ReadInt32LE(payload, 12);
                            int bitrate = ReadInt32LE(payload, 16);
                            bool mute = payload[20] != 0;
                            bool compCursor = payload[21] != 0;
                            bool prefAac = payload[22] != 0;
                            bool prefHevc = payload[23] != 0;

                            // Wake idle lock-screen display on input thread before initializing capture
                            EnqueueInputAction(delegate
                            {
                                BoBoCoreInterop.SyncInputDesktop();
                                BoBoCoreInterop.InjectInput(monIdx, new BoBoInputPacket { Kind = 2, Flags = 0, A = 0, B = 0 });
                            });

                            BoBoCoreInterop.BoBoHostNativeHandle createdRef = null;
                            string err = "";
                            lock (hostGate)
                            {
                                Interlocked.Increment(ref sessionToken);
                                if (activeHostHandle != null)
                                {
                                    try { activeHostHandle.Dispose(); } catch { }
                                    activeHostHandle = null;
                                }

                                long myToken = Interlocked.Increment(ref sessionToken);
                                connSessionToken = myToken;
                                createdRef = BoBoCoreInterop.CreateHostSession(
                                    monIdx, w, h, fps, bitrate, mute,
                                    delegate(byte[] data, bool isKey, int vw, int vh, uint ts)
                                    {
                                        if (disposed || Volatile.Read(ref sessionToken) != myToken || data == null || data.Length == 0) return;
                                        BoBoCoreInterop.BoBoHostNativeHandle hRef = createdRef;
                                        byte vCodec = hRef != null ? (byte)hRef.VideoCodec : (byte)(prefHevc ? BoBoCoreInterop.VideoCodecHevc : BoBoCoreInterop.VideoCodecH264);
                                        byte[] pkt = new byte[14 + data.Length];
                                        pkt[0] = isKey ? (byte)1 : (byte)0;
                                        WriteInt32LE(pkt, 1, vw);
                                        WriteInt32LE(pkt, 5, vh);
                                        WriteUInt32LE(pkt, 9, ts);
                                        pkt[13] = vCodec;
                                        Buffer.BlockCopy(data, 0, pkt, 14, data.Length);
                                        WritePipeMessage(pipe, writeLock, OpVideoFrame, pkt);
                                    },
                                    delegate(byte[] data, int sr, int ch, int samples)
                                    {
                                        if (disposed || Volatile.Read(ref sessionToken) != myToken || data == null || data.Length == 0) return;
                                        BoBoCoreInterop.BoBoHostNativeHandle hRef = createdRef;
                                        byte aCodec = hRef != null ? (byte)hRef.AudioCodec : (byte)BoBoCoreInterop.AudioCodecAdpcm;
                                        byte[] pkt = new byte[13 + data.Length];
                                        WriteInt32LE(pkt, 0, sr);
                                        WriteInt32LE(pkt, 4, ch);
                                        WriteInt32LE(pkt, 8, samples);
                                        pkt[12] = aCodec;
                                        Buffer.BlockCopy(data, 0, pkt, 13, data.Length);
                                        WritePipeMessage(pipe, writeLock, OpAudioFrame, pkt);
                                    },
                                    out err,
                                    compCursor,
                                    prefAac,
                                    prefHevc);
                                activeHostHandle = createdRef;
                            }
                            if (createdRef != null)
                            {
                                SendCreateReply(pipe, writeLock, true, (byte)createdRef.VideoCodec, (byte)createdRef.AudioCodec, "");
                            }
                            else
                            {
                                connSessionToken = 0;
                                SendCreateReply(pipe, writeLock, false, 0, 0, err ?? "CreateHostSession failed");
                            }
                        }
                        else if (op == OpDestroySession)
                        {
                            lock (hostGate)
                            {
                                if (connSessionToken != 0 && Volatile.Read(ref sessionToken) == connSessionToken)
                                {
                                    Interlocked.Increment(ref sessionToken);
                                    if (activeHostHandle != null)
                                    {
                                        try { activeHostHandle.Dispose(); } catch { }
                                        activeHostHandle = null;
                                    }
                                    connSessionToken = 0;
                                }
                            }
                            WritePipeMessage(pipe, writeLock, OpAck, new byte[] { 1 });
                        }
                        else if (op == OpReconfigure && payload != null && payload.Length >= 20)
                        {
                            int monIdx = ReadInt32LE(payload, 0);
                            int w = ReadInt32LE(payload, 4);
                            int h = ReadInt32LE(payload, 8);
                            int fps = ReadInt32LE(payload, 12);
                            int bitrate = ReadInt32LE(payload, 16);
                            bool ok = false;
                            lock (hostGate)
                            {
                                ok = activeHostHandle != null && activeHostHandle.Reconfigure(monIdx, w, h, fps, bitrate);
                            }
                            WritePipeMessage(pipe, writeLock, OpAck, new byte[] { ok ? (byte)1 : (byte)0 });
                        }
                        else if (op == OpSetBitrate && payload != null && payload.Length >= 4)
                        {
                            int bitrate = ReadInt32LE(payload, 0);
                            lock (hostGate) { if (activeHostHandle != null) activeHostHandle.SetBitrate(bitrate); }
                        }
                        else if (op == OpSetFps && payload != null && payload.Length >= 4)
                        {
                            int fps = ReadInt32LE(payload, 0);
                            lock (hostGate) { if (activeHostHandle != null) activeHostHandle.SetFps(fps); }
                        }
                        else if (op == OpSetCompositeCursor && payload != null && payload.Length >= 1)
                        {
                            lock (hostGate) { if (activeHostHandle != null) activeHostHandle.SetCompositeCursor(payload[0] != 0); }
                        }
                        else if (op == OpRequestKeyframe)
                        {
                            lock (hostGate) { if (activeHostHandle != null) activeHostHandle.RequestKeyframe(); }
                        }
                        else if (op == OpSetMuteHost && payload != null && payload.Length >= 1)
                        {
                            lock (hostGate) { if (activeHostHandle != null) activeHostHandle.SetMuteHost(payload[0] != 0); }
                        }
                        else if (op == OpInjectInput && payload != null && payload.Length >= 10)
                        {
                            var pkt = new BoBoInputPacket
                            {
                                Kind = payload[0],
                                Flags = payload[1],
                                A = ReadInt16LE(payload, 2),
                                B = ReadInt16LE(payload, 4)
                            };
                            int monIdx = ReadInt32LE(payload, 6);
                            EnqueueInputAction(delegate
                            {
                                BoBoCoreInterop.SyncInputDesktop();
                                BoBoCoreInterop.InjectInput(monIdx, pkt);
                                Interlocked.Increment(ref injectedPackets);
                            });
                        }
                        else if (op == OpInjectUnicode)
                        {
                            string text = payload != null && payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "";
                            if (!string.IsNullOrEmpty(text))
                            {
                                EnqueueInputAction(delegate
                                {
                                    BoBoCoreInterop.SyncInputDesktop();
                                    BoBoCoreInterop.InjectUnicodeText(text);
                                    Interlocked.Increment(ref injectedPackets);
                                });
                            }
                        }
                        else if (op == OpSendSas)
                        {
                            EnqueueInputAction(delegate
                            {
                                BoBoCoreInterop.SyncInputDesktop();
                                BoBoCoreInterop.SendSAS();
                                Interlocked.Increment(ref sasCalls);
                            });
                        }
                        else if (op == OpApplySuperDisplay && payload != null && payload.Length >= 12)
                        {
                            int monIdx = ReadInt32LE(payload, 0);
                            int cw = ReadInt32LE(payload, 4);
                            int ch = ReadInt32LE(payload, 8);
                            int aw, ah;
                            bool ok = BoBoCoreInterop.ApplySuperDisplay(monIdx, cw, ch, out aw, out ah);
                            byte[] rep = new byte[9];
                            rep[0] = ok ? (byte)1 : (byte)0;
                            WriteInt32LE(rep, 1, aw);
                            WriteInt32LE(rep, 5, ah);
                            WritePipeMessage(pipe, writeLock, OpSuperDisplayReply, rep);
                        }
                        else if (op == OpRestoreSuperDisplay)
                        {
                            BoBoCoreInterop.RestoreSuperDisplay();
                        }
                        else if (op == OpEnumerateMonitors)
                        {
                            List<BoBoMonitorDescriptor> mons = null;
                            using (var done = new ManualResetEventSlim(false))
                            {
                                EnqueueInputAction(delegate
                                {
                                    try
                                    {
                                        BoBoCoreInterop.SyncInputDesktop();
                                        mons = BoBoCoreInterop.EnumerateMonitors();
                                    }
                                    finally
                                    {
                                        try { done.Set(); } catch { }
                                    }
                                });
                                done.Wait(1200);
                            }
                            WritePipeMessage(pipe, writeLock, OpMonitorsReply, SerializeMonitorListReply(mons));
                        }
                    }
                }
                finally
                {
                    if (connSessionToken != 0)
                    {
                        lock (hostGate)
                        {
                            if (Volatile.Read(ref sessionToken) == connSessionToken)
                            {
                                Interlocked.Increment(ref sessionToken);
                                if (activeHostHandle != null)
                                {
                                    try { activeHostHandle.Dispose(); } catch { }
                                    activeHostHandle = null;
                                }
                            }
                        }
                    }
                    try { pipe.Dispose(); } catch { }
                    lock (clientsGate)
                    {
                        activeClientPipes.Remove(pipe);
                        activeClientHandlers--;
                        if (activeClientHandlers <= 0)
                        {
                            activeClientHandlers = 0;
                            try { handlersDrained.Set(); } catch { }
                        }
                    }
                }
            }

            private static byte[] SerializeMonitorListReply(List<BoBoMonitorDescriptor> mons)
            {
                if (mons == null || mons.Count == 0) return new byte[] { 0, 0, 0, 0 };
                using (var ms = new MemoryStream())
                {
                    byte[] tmp = new byte[4];
                    WriteInt32LE(tmp, 0, mons.Count);
                    ms.Write(tmp, 0, 4);
                    for (int i = 0; i < mons.Count; i++)
                    {
                        BoBoMonitorDescriptor m = mons[i];
                        WriteInt32LE(tmp, 0, m.Index); ms.Write(tmp, 0, 4);
                        ms.WriteByte(m.IsPrimary ? (byte)1 : (byte)0);
                        WriteInt32LE(tmp, 0, m.Width); ms.Write(tmp, 0, 4);
                        WriteInt32LE(tmp, 0, m.Height); ms.Write(tmp, 0, 4);
                        byte[] nameBytes = Encoding.UTF8.GetBytes(m.Name ?? "");
                        int nLen = Math.Min(1024, nameBytes.Length);
                        WriteInt16LE(tmp, 0, (short)nLen); ms.Write(tmp, 0, 2);
                        if (nLen > 0) ms.Write(nameBytes, 0, nLen);
                    }
                    return ms.ToArray();
                }
            }

            private static void SendCreateReply(NamedPipeServerStream pipe, object writeLock, bool ok, byte vCodec, byte aCodec, string err)
            {
                byte[] errBytes = string.IsNullOrEmpty(err) ? new byte[0] : Encoding.UTF8.GetBytes(err);
                byte[] rep = new byte[3 + errBytes.Length];
                rep[0] = ok ? (byte)1 : (byte)0;
                rep[1] = vCodec;
                rep[2] = aCodec;
                if (errBytes.Length > 0) Buffer.BlockCopy(errBytes, 0, rep, 3, errBytes.Length);
                WritePipeMessage(pipe, writeLock, OpCreateReply, rep);
            }

            private void EnqueueInputAction(Action action)
            {
                if (disposed || action == null) return;
                lock (inputQueueLock)
                {
                    if (inputQueue.Count > 512) return;
                    inputQueue.Enqueue(action);
                }
                inputWakeEvent.Set();
            }

            private void InputWorkerLoop()
            {
                // Dedicated HWND-free thread so SetThreadDesktop(OpenInputDesktop) can switch freely into Winlogon/UAC.
                while (!disposed && !stopEvent.IsSet)
                {
                    Action next = null;
                    lock (inputQueueLock)
                    {
                        if (inputQueue.Count > 0) next = inputQueue.Dequeue();
                    }
                    if (next != null)
                    {
                        try { next(); } catch { }
                        continue;
                    }
                    inputWakeEvent.WaitOne(120);
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { stopEvent.Set(); } catch { }
                try { listenerReady.Set(); } catch { }
                try { inputWakeEvent.Set(); } catch { }
                for (int i = 0; i < activeAcceptPipes.Length; i++)
                {
                    NamedPipeServerStream p = activeAcceptPipes[i];
                    if (p != null)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                List<NamedPipeServerStream> clientsCopy;
                lock (clientsGate)
                {
                    clientsCopy = new List<NamedPipeServerStream>(activeClientPipes);
                    activeClientPipes.Clear();
                }
                for (int i = 0; i < clientsCopy.Count; i++)
                {
                    try { clientsCopy[i].Dispose(); } catch { }
                }
                lock (hostGate)
                {
                    Interlocked.Increment(ref sessionToken);
                    if (activeHostHandle != null)
                    {
                        try { activeHostHandle.Dispose(); } catch { }
                        activeHostHandle = null;
                    }
                }
                try { handlersDrained.Wait(1500); } catch { }
                if (serverThread != null && serverThread.IsAlive)
                {
                    try { serverThread.Join(1200); } catch { }
                }
                if (auxServerThread != null && auxServerThread.IsAlive)
                {
                    try { auxServerThread.Join(1200); } catch { }
                }
                if (inputThread != null && inputThread.IsAlive)
                {
                    try { inputThread.Join(1200); } catch { }
                }
                try { stopEvent.Dispose(); } catch { }
                try { listenerReady.Dispose(); } catch { }
                try { handlersDrained.Dispose(); } catch { }
                try { inputWakeEvent.Dispose(); } catch { }
            }
        }

        internal static bool IsAuthorizedHelperClientImage(string imgPath, uint clientSid, int targetSessionId, string protectedSvcExe)
        {
            if (string.IsNullOrWhiteSpace(imgPath)) return false;
            if (clientSid == 0 && !string.IsNullOrEmpty(protectedSvcExe) &&
                string.Equals(imgPath, protectedSvcExe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (targetSessionId < 0 || clientSid != (uint)targetSessionId) return false;
            return string.Equals(Path.GetFileName(imgPath), "BoBo远程.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool VerifyPipeClientProcess(NamedPipeServerStream pipe, int targetSessionId, bool restrictToConsoleUser)
        {
            if (pipe == null) return false;
            bool addedRef = false;
            uint clientPid = 0;
            try
            {
                pipe.SafePipeHandle.DangerousAddRef(ref addedRef);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out clientPid) || clientPid == 0)
                    return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (addedRef) { try { pipe.SafePipeHandle.DangerousRelease(); } catch { } }
            }

            if (!restrictToConsoleUser)
            {
                return clientPid == unchecked((uint)Process.GetCurrentProcess().Id);
            }

            uint clientSid;
            if (!ProcessIdToSessionId(clientPid, out clientSid)) return false;

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, clientPid);
            if (hProc == IntPtr.Zero) return false;
            try
            {
                var sb = new StringBuilder(1024);
                uint cap = (uint)sb.Capacity;
                if (!QueryFullProcessImageNameW(hProc, 0, sb, ref cap) || cap == 0) return false;
                string imgPath = sb.ToString();
                if (!IsAuthorizedHelperClientImage(imgPath, clientSid, targetSessionId, GetProtectedServiceExePath()))
                    return false;
                return File.Exists(imgPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private static NamedPipeServerStream CreateSecuredPipeServer(
            string pipeName,
            int sessionId,
            bool includeConsoleUser,
            out SecurityIdentifier grantedUserSid)
        {
            grantedUserSid = null;
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            bool addedUser = false;
            if (includeConsoleUser && sessionId >= 0)
            {
                SecurityIdentifier consoleSid = TryGetSessionUserSid(sessionId);
                if (consoleSid != null)
                {
                    grantedUserSid = consoleSid;
                    ps.AddAccessRule(new PipeAccessRule(consoleSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    addedUser = true;
                }
            }
            if (!addedUser)
            {
                try
                {
                    using (WindowsIdentity cur = WindowsIdentity.GetCurrent())
                    {
                        if (cur != null && cur.User != null)
                        {
                            ps.AddAccessRule(new PipeAccessRule(cur.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                        }
                    }
                }
                catch { }
            }

            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                16,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                65536,
                262144,
                ps);
        }

        private static SecurityIdentifier TryGetSessionUserSid(int sessionId)
        {
            IntPtr hToken = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!WTSQueryUserToken(unchecked((uint)sessionId), out hToken) || hToken == IntPtr.Zero) return null;
                int len = 0;
                GetTokenInformation(hToken, TokenUser, IntPtr.Zero, 0, out len);
                if (len <= 0) return null;
                buffer = Marshal.AllocHGlobal(len);
                if (!GetTokenInformation(hToken, TokenUser, buffer, len, out len)) return null;
                IntPtr pSid = Marshal.ReadIntPtr(buffer);
                if (pSid == IntPtr.Zero) return null;
                return new SecurityIdentifier(pSid);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        // ==================== DesktopHelperClient ====================

        internal sealed class DesktopHelperClient : IDisposable
        {
            private readonly NamedPipeClientStream pipe;
            private readonly object writeLock = new object();
            private readonly object rpcLock = new object();
            private readonly object rpcStateLock = new object();
            private readonly AutoResetEvent rpcEvent = new AutoResetEvent(false);
            private readonly Thread readerThread;

            private volatile bool disposed;
            private volatile bool disconnected;
            private Action<byte[], bool, int, int, uint> videoHandler;
            private Action<byte[], int, int, int> audioHandler;
            private volatile int lastVideoCodec = BoBoCoreInterop.VideoCodecH264;
            private volatile int lastAudioCodec = BoBoCoreInterop.AudioCodecAdpcm;
            private volatile string serverVersion = "";

            private byte rpcExpectedOp;
            private byte rpcResultOp;
            private byte[] rpcResultPayload;

            internal event Action Disconnected;

            internal bool IsConnected { get { return !disposed && !disconnected && pipe.IsConnected; } }
            internal int VideoCodec { get { return lastVideoCodec; } }
            internal int AudioCodec { get { return lastAudioCodec; } }
            internal string ServerVersion { get { return serverVersion ?? ""; } }

            private DesktopHelperClient(NamedPipeClientStream connectedPipe)
            {
                pipe = connectedPipe;
                readerThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "BoBoHelperPipeClient"
                };
                readerThread.Start();
            }

            internal static DesktopHelperClient TryConnect(string pipeName, int timeoutMs)
            {
                int budgetMs = Math.Max(150, timeoutMs);
                Stopwatch sw = Stopwatch.StartNew();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int remainingMs = budgetMs - (int)sw.ElapsedMilliseconds;
                    if (attempt > 0 && remainingMs < 60) break;

                    NamedPipeClientStream p = null;
                    try
                    {
                        p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        p.Connect(Math.Max(60, remainingMs));
                        var c = new DesktopHelperClient(p);
                        p = null;
                        if (c.Ping()) return c;
                        c.Dispose();
                    }
                    catch (System.TimeoutException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch
                    {
                        break;
                    }
                    finally
                    {
                        if (p != null) { try { p.Dispose(); } catch { } }
                    }

                    if (attempt < 2 && (budgetMs - (int)sw.ElapsedMilliseconds) >= 60)
                    {
                        Thread.Sleep(25);
                    }
                }
                return null;
            }

            internal bool Ping()
            {
                byte[] rep = SendRpc(OpPing, null, OpPong, 1200);
                if (rep == null) return false;
                if (rep.Length > 2)
                {
                    try { serverVersion = Encoding.UTF8.GetString(rep, 2, rep.Length - 2).Trim(); }
                    catch { }
                }
                return true;
            }

            internal bool CreateHostSession(
                int monitorIndex,
                int targetWidth,
                int targetHeight,
                int fps,
                int bitrateKbps,
                bool muteHostSpeakers,
                bool compositeCursor,
                bool preferAacAudio,
                bool preferHevcVideo,
                Action<byte[], bool, int, int, uint> onVideo,
                Action<byte[], int, int, int> onAudio,
                out string error)
            {
                videoHandler = onVideo;
                audioHandler = onAudio;
                byte[] req = new byte[24];
                WriteInt32LE(req, 0, monitorIndex);
                WriteInt32LE(req, 4, targetWidth);
                WriteInt32LE(req, 8, targetHeight);
                WriteInt32LE(req, 12, fps);
                WriteInt32LE(req, 16, bitrateKbps);
                req[20] = muteHostSpeakers ? (byte)1 : (byte)0;
                req[21] = compositeCursor ? (byte)1 : (byte)0;
                req[22] = preferAacAudio ? (byte)1 : (byte)0;
                req[23] = preferHevcVideo ? (byte)1 : (byte)0;

                byte[] rep = SendRpc(OpCreateSession, req, OpCreateReply, RpcTimeoutMs);
                if (rep == null || rep.Length < 3)
                {
                    error = "连接锁屏服务采集通道超时。";
                    return false;
                }
                if (rep[0] == 0)
                {
                    error = rep.Length > 3 ? Encoding.UTF8.GetString(rep, 3, rep.Length - 3) : "服务采编引擎初始化失败。";
                    return false;
                }
                lastVideoCodec = rep[1];
                lastAudioCodec = rep[2];
                error = "";
                return true;
            }

            internal void DestroyHostSession()
            {
                SendRpc(OpDestroySession, null, OpAck, 1500);
                videoHandler = null;
                audioHandler = null;
            }

            internal bool Reconfigure(int monitorIndex, int width, int height, int fps, int bitrateKbps)
            {
                byte[] req = new byte[20];
                WriteInt32LE(req, 0, monitorIndex);
                WriteInt32LE(req, 4, width);
                WriteInt32LE(req, 8, height);
                WriteInt32LE(req, 12, fps);
                WriteInt32LE(req, 16, bitrateKbps);
                byte[] rep = SendRpc(OpReconfigure, req, OpAck, RpcTimeoutMs);
                return rep != null && rep.Length >= 1 && rep[0] != 0;
            }

            internal void SetBitrate(int bitrateKbps)
            {
                byte[] req = new byte[4];
                WriteInt32LE(req, 0, bitrateKbps);
                WritePipeMessage(pipe, writeLock, OpSetBitrate, req);
            }

            internal void SetFps(int fps)
            {
                byte[] req = new byte[4];
                WriteInt32LE(req, 0, fps);
                WritePipeMessage(pipe, writeLock, OpSetFps, req);
            }

            internal void SetCompositeCursor(bool enable)
            {
                WritePipeMessage(pipe, writeLock, OpSetCompositeCursor, new byte[] { enable ? (byte)1 : (byte)0 });
            }

            internal void RequestKeyframe()
            {
                WritePipeMessage(pipe, writeLock, OpRequestKeyframe, null);
            }

            internal void SetMuteHost(bool mute)
            {
                WritePipeMessage(pipe, writeLock, OpSetMuteHost, new byte[] { mute ? (byte)1 : (byte)0 });
            }

            internal void InjectInput(int monitorIndex, BoBoInputPacket pkt)
            {
                byte[] req = new byte[10];
                req[0] = pkt.Kind;
                req[1] = pkt.Flags;
                WriteInt16LE(req, 2, pkt.A);
                WriteInt16LE(req, 4, pkt.B);
                WriteInt32LE(req, 6, monitorIndex);
                WritePipeMessage(pipe, writeLock, OpInjectInput, req);
            }

            internal void InjectUnicodeText(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                WritePipeMessage(pipe, writeLock, OpInjectUnicode, Encoding.UTF8.GetBytes(text));
            }

            internal void SendSAS()
            {
                WritePipeMessage(pipe, writeLock, OpSendSas, null);
            }

            internal bool ApplySuperDisplay(int monitorIndex, int clientW, int clientH, out int actualW, out int actualH)
            {
                actualW = 0;
                actualH = 0;
                byte[] req = new byte[12];
                WriteInt32LE(req, 0, monitorIndex);
                WriteInt32LE(req, 4, clientW);
                WriteInt32LE(req, 8, clientH);
                byte[] rep = SendRpc(OpApplySuperDisplay, req, OpSuperDisplayReply, RpcTimeoutMs);
                if (rep == null || rep.Length < 9) return false;
                actualW = ReadInt32LE(rep, 1);
                actualH = ReadInt32LE(rep, 5);
                return rep[0] != 0;
            }

            internal void RestoreSuperDisplay()
            {
                WritePipeMessage(pipe, writeLock, OpRestoreSuperDisplay, null);
            }

            internal List<BoBoMonitorDescriptor> EnumerateMonitors()
            {
                byte[] rep = SendRpc(OpEnumerateMonitors, null, OpMonitorsReply, 1500);
                var list = new List<BoBoMonitorDescriptor>();
                if (rep == null || rep.Length < 4) return list;
                int count = ReadInt32LE(rep, 0);
                int off = 4;
                for (int i = 0; i < count && off + 15 <= rep.Length; i++)
                {
                    int idx = ReadInt32LE(rep, off); off += 4;
                    bool pri = rep[off++] != 0;
                    int w = ReadInt32LE(rep, off); off += 4;
                    int h = ReadInt32LE(rep, off); off += 4;
                    int nLen = ReadInt16LE(rep, off); off += 2;
                    if (nLen < 0 || off + nLen > rep.Length) break;
                    string name = nLen > 0 ? Encoding.UTF8.GetString(rep, off, nLen) : ("DISPLAY" + (idx + 1));
                    off += nLen;
                    list.Add(new BoBoMonitorDescriptor
                    {
                        Index = idx,
                        IsPrimary = pri,
                        Width = w,
                        Height = h,
                        Name = name
                    });
                }
                return list;
            }

            private byte[] SendRpc(byte reqOp, byte[] payload, byte expectedReplyOp, int timeoutMs)
            {
                if (disposed || disconnected) return null;
                lock (rpcLock)
                {
                    if (disposed || disconnected) return null;
                    lock (rpcStateLock)
                    {
                        rpcExpectedOp = expectedReplyOp;
                        rpcResultOp = 0;
                        rpcResultPayload = null;
                        rpcEvent.Reset();
                    }
                    if (!WritePipeMessage(pipe, writeLock, reqOp, payload))
                    {
                        lock (rpcStateLock) { rpcExpectedOp = 0; }
                        return null;
                    }
                    bool signaled = rpcEvent.WaitOne(timeoutMs);
                    lock (rpcStateLock)
                    {
                        byte gotOp = rpcResultOp;
                        byte[] gotPayload = rpcResultPayload;
                        rpcExpectedOp = 0;
                        rpcResultPayload = null;
                        return (signaled && gotOp == expectedReplyOp) ? gotPayload : null;
                    }
                }
            }

            private void ReadLoop()
            {
                try
                {
                    while (!disposed && pipe.IsConnected)
                    {
                        byte op;
                        byte[] payload;
                        if (!ReadPipeMessage(pipe, out op, out payload)) break;

                        if (op == OpVideoFrame && payload != null && payload.Length > 14)
                        {
                            bool isKey = payload[0] != 0;
                            int w = ReadInt32LE(payload, 1);
                            int h = ReadInt32LE(payload, 5);
                            uint ts = ReadUInt32LE(payload, 9);
                            lastVideoCodec = payload[13];
                            int dataLen = payload.Length - 14;
                            byte[] frame = new byte[dataLen];
                            Buffer.BlockCopy(payload, 14, frame, 0, dataLen);
                            Action<byte[], bool, int, int, uint> cb = videoHandler;
                            if (cb != null)
                            {
                                try { cb(frame, isKey, w, h, ts); } catch { }
                            }
                        }
                        else if (op == OpAudioFrame && payload != null && payload.Length > 13)
                        {
                            int sr = ReadInt32LE(payload, 0);
                            int ch = ReadInt32LE(payload, 4);
                            int samples = ReadInt32LE(payload, 8);
                            lastAudioCodec = payload[12];
                            int dataLen = payload.Length - 13;
                            byte[] audio = new byte[dataLen];
                            Buffer.BlockCopy(payload, 13, audio, 0, dataLen);
                            Action<byte[], int, int, int> cb = audioHandler;
                            if (cb != null)
                            {
                                try { cb(audio, sr, ch, samples); } catch { }
                            }
                        }
                        else if (op == OpCreateReply || op == OpAck || op == OpPong || op == OpSuperDisplayReply || op == OpMonitorsReply)
                        {
                            lock (rpcStateLock)
                            {
                                if (op == rpcExpectedOp || rpcExpectedOp == 0)
                                {
                                    rpcResultOp = op;
                                    rpcResultPayload = payload ?? new byte[0];
                                    try { rpcEvent.Set(); } catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    disconnected = true;
                    lock (rpcStateLock)
                    {
                        rpcExpectedOp = 0;
                        try { rpcEvent.Set(); } catch { }
                    }
                    if (!disposed)
                    {
                        Action disc = Disconnected;
                        if (disc != null)
                        {
                            ThreadPool.QueueUserWorkItem(delegate { try { disc(); } catch { } });
                        }
                    }
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                disconnected = true;
                try { pipe.Dispose(); } catch { }
                lock (rpcStateLock)
                {
                    rpcExpectedOp = 0;
                    try { rpcEvent.Set(); } catch { }
                }
                if (readerThread != null && readerThread.IsAlive)
                {
                    try { readerThread.Join(800); } catch { }
                }
                try { rpcEvent.Dispose(); } catch { }
            }
        }

        // ==================== BoBoSecureDesktopBridge ====================

        /// <summary>
        /// 宿主采编/输入统一桥接：优先连接同主机 Session 的 SYSTEM Helper（全程不断流穿透 Winlogon 与 UAC），
        /// 不可用时无缝回退至进程内 BoBoCoreInterop，并在 Helper 重启后自动重连重建采集并请求关键帧。
        /// </summary>
        internal sealed class BoBoSecureDesktopBridge : IDisposable
        {
            private readonly object gate = new object();
            private readonly Func<string> customPipeNameProvider;
            private volatile DesktopHelperClient helperClient;
            private volatile BoBoCoreInterop.BoBoHostNativeHandle localHandle;
            private volatile int cachedVideoCodec = BoBoCoreInterop.VideoCodecH264;
            private volatile int cachedAudioCodec = BoBoCoreInterop.AudioCodecAdpcm;
            private long lastHelperProbeTicks;
            private volatile bool disposed;

            private bool sessionActive;
            private bool recovering;
            private int recoveryCount;
            private int cfgMonitor;
            private int cfgWidth;
            private int cfgHeight;
            private int cfgFps;
            private int cfgBitrateKbps;
            private bool cfgMuteHost;
            private bool cfgCompositeCursor;
            private bool cfgPreferAac;
            private bool cfgPreferHevc;
            private Action<byte[], bool, int, int, uint> userOnVideo;
            private Action<byte[], int, int, int> userOnAudio;

            internal BoBoSecureDesktopBridge(Func<string> pipeNameProvider = null)
            {
                customPipeNameProvider = pipeNameProvider;
            }

            private string ResolveTargetPipeName()
            {
                if (customPipeNameProvider != null) return customPipeNameProvider();
                int sid = GetActiveConsoleSessionIdSafe();
                if (sid < 0) sid = Process.GetCurrentProcess().SessionId;
                return GetPipeNameForSession(sid);
            }

            private DesktopHelperClient TryConnectHelper(bool bypassCooldown)
            {
                if (disposed) return null;
                if (customPipeNameProvider == null && IsRunningAsSystem()) return null;

                DesktopHelperClient cur = helperClient;
                if (cur != null && cur.IsConnected) return cur;

                lock (gate)
                {
                    cur = helperClient;
                    if (cur != null && cur.IsConnected) return cur;
                    if (cur != null)
                    {
                        helperClient = null;
                        try { cur.Dispose(); } catch { }
                    }
                    long now = Stopwatch.GetTimestamp();
                    if (!bypassCooldown && lastHelperProbeTicks != 0 &&
                        ((now - lastHelperProbeTicks) * 1000.0 / Stopwatch.Frequency) < 2500.0)
                    {
                        return null;
                    }
                    lastHelperProbeTicks = now;
                }

                string targetPipe = ResolveTargetPipeName();
                if (string.IsNullOrEmpty(targetPipe)) return null;
                DesktopHelperClient client = DesktopHelperClient.TryConnect(targetPipe, ConnectTimeoutMs);
                if (client != null)
                {
                    lock (gate) { lastHelperProbeTicks = 0; }
                }
                return client;
            }

            internal bool IsUsingSystemHelper
            {
                get
                {
                    DesktopHelperClient hc = helperClient;
                    return hc != null && hc.IsConnected;
                }
            }

            internal int RecoveryCount { get { return Volatile.Read(ref recoveryCount); } }

            internal int VideoCodec
            {
                get
                {
                    DesktopHelperClient hc = helperClient;
                    if (hc != null) return hc.VideoCodec;
                    BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                    if (lh != null) return lh.VideoCodec;
                    return cachedVideoCodec;
                }
            }

            internal int AudioCodec
            {
                get
                {
                    DesktopHelperClient hc = helperClient;
                    if (hc != null) return hc.AudioCodec;
                    BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                    if (lh != null) return lh.AudioCodec;
                    return cachedAudioCodec;
                }
            }

            private void AttachHelperClient(DesktopHelperClient client)
            {
                if (client == null) return;
                client.Disconnected += delegate { OnHelperClientDisconnected(client); };
                cachedVideoCodec = client.VideoCodec;
                cachedAudioCodec = client.AudioCodec;
                helperClient = client;
            }

            private void OnHelperClientDisconnected(DesktopHelperClient deadClient)
            {
                bool shouldRecover = false;
                lock (gate)
                {
                    if (disposed || !sessionActive) return;
                    if (object.ReferenceEquals(helperClient, deadClient))
                    {
                        helperClient = null;
                    }
                    if (!recovering)
                    {
                        recovering = true;
                        shouldRecover = true;
                    }
                }
                try { deadClient.Dispose(); } catch { }
                if (shouldRecover)
                {
                    Task.Run(delegate { RecoverHelperSessionLoop(); });
                }
            }

            private void WaitForPendingRecoveryBriefly()
            {
                for (int i = 0; i < 16 && !disposed; i++)
                {
                    lock (gate)
                    {
                        if (!recovering || !sessionActive) return;
                    }
                    Thread.Sleep(40);
                }
            }

            private void RecoverHelperSessionLoop()
            {
                try
                {
                    int[] delays = customPipeNameProvider != null
                        ? new int[] { 40, 80, 120, 180, 260, 400 }
                        : new int[] { 80, 180, 320, 500, 800, 1000, 1200 };

                    for (int attempt = 0; attempt < delays.Length && !disposed; attempt++)
                    {
                        int mon, w, h, fps, bps;
                        bool mute, comp, pAac, pHevc;
                        Action<byte[], bool, int, int, uint> cbV;
                        Action<byte[], int, int, int> cbA;
                        lock (gate)
                        {
                            if (disposed || !sessionActive) return;
                            mon = cfgMonitor;
                            w = cfgWidth;
                            h = cfgHeight;
                            fps = cfgFps;
                            bps = cfgBitrateKbps;
                            mute = cfgMuteHost;
                            comp = cfgCompositeCursor;
                            pAac = cfgPreferAac;
                            pHevc = cfgPreferHevc;
                            cbV = userOnVideo;
                            cbA = userOnAudio;
                        }

                        DesktopHelperClient nextClient = TryConnectHelper(true);
                        if (nextClient != null)
                        {
                            string helperErr;
                            if (nextClient.CreateHostSession(
                                mon, w, h, fps, bps, mute, comp, pAac, pHevc,
                                cbV, cbA, out helperErr))
                            {
                                bool attached = false;
                                lock (gate)
                                {
                                    if (!disposed && sessionActive)
                                    {
                                        DesktopHelperClient prev = helperClient;
                                        BoBoCoreInterop.BoBoHostNativeHandle prevLocal = localHandle;
                                        localHandle = null;
                                        AttachHelperClient(nextClient);
                                        attached = true;
                                        if (prev != null && !object.ReferenceEquals(prev, nextClient))
                                        {
                                            ThreadPool.QueueUserWorkItem(delegate { try { prev.Dispose(); } catch { } });
                                        }
                                        if (prevLocal != null)
                                        {
                                            ThreadPool.QueueUserWorkItem(delegate { try { prevLocal.Dispose(); } catch { } });
                                        }
                                    }
                                }
                                if (attached)
                                {
                                    Interlocked.Increment(ref recoveryCount);
                                    try { nextClient.RequestKeyframe(); } catch { }
                                    return;
                                }
                                try { nextClient.DestroyHostSession(); } catch { }
                            }
                            try { nextClient.Dispose(); } catch { }
                        }

                        try { Thread.Sleep(delays[attempt]); } catch { }
                    }

                    // Helper stayed offline; fall back to in-process capture when interactive desktop is accessible
                    if (!disposed && customPipeNameProvider == null && !IsSecureDesktopBlockedWithoutService())
                    {
                        int mon, w, h, fps, bps;
                        bool mute, comp, pAac, pHevc;
                        Action<byte[], bool, int, int, uint> cbV;
                        Action<byte[], int, int, int> cbA;
                        lock (gate)
                        {
                            if (disposed || !sessionActive || localHandle != null || helperClient != null) return;
                            mon = cfgMonitor;
                            w = cfgWidth;
                            h = cfgHeight;
                            fps = cfgFps;
                            bps = cfgBitrateKbps;
                            mute = cfgMuteHost;
                            comp = cfgCompositeCursor;
                            pAac = cfgPreferAac;
                            pHevc = cfgPreferHevc;
                            cbV = userOnVideo;
                            cbA = userOnAudio;
                        }
                        string localErr;
                        BoBoCoreInterop.BoBoHostNativeHandle fallbackLocal = BoBoCoreInterop.CreateHostSession(
                            mon, w, h, fps, bps, mute, cbV, cbA, out localErr, comp, pAac, pHevc);
                        if (fallbackLocal != null)
                        {
                            lock (gate)
                            {
                                if (!disposed && sessionActive && helperClient == null && localHandle == null)
                                {
                                    cachedVideoCodec = fallbackLocal.VideoCodec;
                                    cachedAudioCodec = fallbackLocal.AudioCodec;
                                    localHandle = fallbackLocal;
                                    fallbackLocal = null;
                                }
                            }
                            if (fallbackLocal != null)
                            {
                                try { fallbackLocal.Dispose(); } catch { }
                            }
                            else
                            {
                                Interlocked.Increment(ref recoveryCount);
                                try { localHandle.RequestKeyframe(); } catch { }
                            }
                        }
                    }
                }
                finally
                {
                    lock (gate) { recovering = false; }
                }
            }

            internal bool StartHostSession(
                int monitorIndex,
                int targetWidth,
                int targetHeight,
                int fps,
                int bitrateKbps,
                bool muteHostSpeakers,
                bool compositeCursor,
                bool preferAacAudio,
                bool preferHevcVideo,
                Action<byte[], bool, int, int, uint> onVideo,
                Action<byte[], int, int, int> onAudio,
                out string error)
            {
                StopHostSession();
                lock (gate)
                {
                    if (disposed) { error = "Bridge disposed"; return false; }
                    sessionActive = true;
                    recovering = false;
                    cfgMonitor = monitorIndex;
                    cfgWidth = targetWidth;
                    cfgHeight = targetHeight;
                    cfgFps = fps;
                    cfgBitrateKbps = bitrateKbps;
                    cfgMuteHost = muteHostSpeakers;
                    cfgCompositeCursor = compositeCursor;
                    cfgPreferAac = preferAacAudio;
                    cfgPreferHevc = preferHevcVideo;
                    userOnVideo = onVideo;
                    userOnAudio = onAudio;
                    lastHelperProbeTicks = 0;
                }

                // Prefer SYSTEM Helper when available so capture survives Winlogon/UAC switches seamlessly
                DesktopHelperClient client = TryConnectHelper(true);
                if (client == null && customPipeNameProvider == null && IsServiceRunning())
                {
                    try { Thread.Sleep(150); } catch { }
                    client = TryConnectHelper(true);
                }
                if (client != null)
                {
                    string helperErr;
                    if (client.CreateHostSession(
                        monitorIndex, targetWidth, targetHeight, fps, bitrateKbps,
                        muteHostSpeakers, compositeCursor, preferAacAudio, preferHevcVideo,
                        onVideo, onAudio, out helperErr))
                    {
                        bool keep = false;
                        lock (gate)
                        {
                            if (!disposed && sessionActive)
                            {
                                AttachHelperClient(client);
                                keep = true;
                            }
                        }
                        if (keep)
                        {
                            error = "";
                            return true;
                        }
                        try { client.DestroyHostSession(); } catch { }
                    }
                    try { client.Dispose(); } catch { }
                }

                if (customPipeNameProvider != null)
                {
                    lock (gate) { sessionActive = false; }
                    error = "未能连接到测试管道服务。";
                    return false;
                }

                BoBoCoreInterop.BoBoHostNativeHandle local = BoBoCoreInterop.CreateHostSession(
                    monitorIndex, targetWidth, targetHeight, fps, bitrateKbps, muteHostSpeakers,
                    onVideo, onAudio, out error, compositeCursor, preferAacAudio, preferHevcVideo);
                if (local == null)
                {
                    lock (gate) { sessionActive = false; }
                    return false;
                }
                bool keepLocal = false;
                lock (gate)
                {
                    if (!disposed && sessionActive)
                    {
                        cachedVideoCodec = local.VideoCodec;
                        cachedAudioCodec = local.AudioCodec;
                        localHandle = local;
                        keepLocal = true;
                    }
                }
                if (!keepLocal)
                {
                    try { local.Dispose(); } catch { }
                    error = "会话已关闭。";
                    return false;
                }
                return true;
            }

            internal bool Reconfigure(int monitorIndex, int width, int height, int fps, int bitrateKbps)
            {
                if (disposed) return false;
                lock (gate)
                {
                    if (disposed || !sessionActive) return false;
                    cfgMonitor = monitorIndex;
                    cfgWidth = width;
                    cfgHeight = height;
                    cfgFps = fps;
                    cfgBitrateKbps = bitrateKbps;
                }

                DesktopHelperClient hc = helperClient;
                BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                if (hc == null && lh == null && recovering)
                {
                    WaitForPendingRecoveryBriefly();
                    hc = helperClient;
                    lh = localHandle;
                }

                if (hc != null && hc.IsConnected)
                {
                    return hc.Reconfigure(monitorIndex, width, height, fps, bitrateKbps);
                }
                if (lh != null)
                {
                    return lh.Reconfigure(monitorIndex, width, height, fps, bitrateKbps);
                }
                return false;
            }

            internal bool SetBitrate(int bitrateKbps)
            {
                if (disposed) return false;
                lock (gate)
                {
                    if (disposed) return false;
                    cfgBitrateKbps = bitrateKbps;
                }
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected) { hc.SetBitrate(bitrateKbps); return true; }
                BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                if (lh != null) return lh.SetBitrate(bitrateKbps);
                return false;
            }

            internal bool SetFps(int fps)
            {
                if (disposed) return false;
                lock (gate)
                {
                    if (disposed) return false;
                    cfgFps = fps;
                }
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected) { hc.SetFps(fps); return true; }
                BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                if (lh != null) return lh.SetFps(fps);
                return false;
            }

            internal void SetCompositeCursor(bool enable)
            {
                if (disposed) return;
                lock (gate) { cfgCompositeCursor = enable; }
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected) hc.SetCompositeCursor(enable);
                else
                {
                    BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                    if (lh != null) lh.SetCompositeCursor(enable);
                }
            }

            internal void RequestKeyframe()
            {
                if (disposed) return;
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected) hc.RequestKeyframe();
                else
                {
                    BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                    if (lh != null) lh.RequestKeyframe();
                }
            }

            internal void SetMuteHost(bool mute)
            {
                if (disposed) return;
                lock (gate) { cfgMuteHost = mute; }
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected) hc.SetMuteHost(mute);
                else
                {
                    BoBoCoreInterop.BoBoHostNativeHandle lh = localHandle;
                    if (lh != null) lh.SetMuteHost(mute);
                }
            }

            internal void InjectInput(int monitorIndex, BoBoInputPacket pkt)
            {
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected)
                {
                    hc.InjectInput(monitorIndex, pkt);
                    Action<BoBoInputPacket> hook = BoBoCoreInterop.RemoteInputHook;
                    if (hook != null) hook(pkt);
                    return;
                }
                BoBoCoreInterop.InjectInput(monitorIndex, pkt);
            }

            internal void InjectUnicodeText(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected)
                {
                    hc.InjectUnicodeText(text);
                    Action textHook = BoBoCoreInterop.RemoteTextInputHook;
                    if (textHook != null) textHook();
                    return;
                }
                BoBoCoreInterop.InjectUnicodeText(text);
            }

            internal bool SendSAS()
            {
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected)
                {
                    hc.SendSAS();
                    return true;
                }
                using (DesktopHelperClient temp = TryConnectHelper(false))
                {
                    if (temp != null && temp.IsConnected)
                    {
                        temp.SendSAS();
                        return true;
                    }
                }
                return BoBoCoreInterop.SendSAS();
            }

            internal bool ApplySuperDisplay(int monitorIndex, int clientW, int clientH, out int actualW, out int actualH)
            {
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected)
                {
                    return hc.ApplySuperDisplay(monitorIndex, clientW, clientH, out actualW, out actualH);
                }
                using (DesktopHelperClient temp = TryConnectHelper(false))
                {
                    if (temp != null && temp.IsConnected)
                    {
                        return temp.ApplySuperDisplay(monitorIndex, clientW, clientH, out actualW, out actualH);
                    }
                }
                return BoBoCoreInterop.ApplySuperDisplay(monitorIndex, clientW, clientH, out actualW, out actualH);
            }

            internal void RestoreSuperDisplay()
            {
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected)
                {
                    hc.RestoreSuperDisplay();
                    return;
                }
                using (DesktopHelperClient temp = TryConnectHelper(false))
                {
                    if (temp != null && temp.IsConnected)
                    {
                        temp.RestoreSuperDisplay();
                        return;
                    }
                }
                BoBoCoreInterop.RestoreSuperDisplay();
            }

            internal List<BoBoMonitorDescriptor> EnumerateMonitors()
            {
                DesktopHelperClient hc = helperClient;
                if (hc != null && hc.IsConnected)
                {
                    List<BoBoMonitorDescriptor> remoteMons = hc.EnumerateMonitors();
                    if (remoteMons != null && remoteMons.Count > 0) return remoteMons;
                }
                using (DesktopHelperClient temp = TryConnectHelper(false))
                {
                    if (temp != null && temp.IsConnected)
                    {
                        List<BoBoMonitorDescriptor> remoteMons = temp.EnumerateMonitors();
                        if (remoteMons != null && remoteMons.Count > 0) return remoteMons;
                    }
                }
                return BoBoCoreInterop.EnumerateMonitors();
            }

            internal bool IsSecureDesktopBlockedWithoutService()
            {
                if (IsUsingSystemHelper || IsRunningAsSystem()) return false;
                return !BoBoCoreInterop.IsInputDesktopAccessible() || BoBoCoreInterop.IsForegroundUipiBlocked();
            }

            internal void StopHostSession()
            {
                DesktopHelperClient hc;
                BoBoCoreInterop.BoBoHostNativeHandle lh;
                lock (gate)
                {
                    sessionActive = false;
                    recovering = false;
                    hc = helperClient;
                    lh = localHandle;
                    localHandle = null;
                    helperClient = null;
                    userOnVideo = null;
                    userOnAudio = null;
                }
                if (hc != null)
                {
                    try { hc.DestroyHostSession(); } catch { }
                    try { hc.Dispose(); } catch { }
                }
                if (lh != null)
                {
                    try { lh.Dispose(); } catch { }
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                StopHostSession();
            }
        }

        // ==================== Win32 SCM & Token P/Invokes ====================

        private const uint SC_MANAGER_CONNECT = 0x0001u;
        private const uint SC_MANAGER_ALL_ACCESS = 0xF003Fu;
        private const uint SERVICE_QUERY_CONFIG = 0x0001u;
        private const uint SERVICE_ALL_ACCESS = 0xF01FFu;
        private const uint SERVICE_NO_CHANGE = 0xFFFFFFFFu;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010u;
        private const uint SERVICE_AUTO_START = 0x00000002u;
        private const uint SERVICE_ERROR_NORMAL = 0x00000001u;
        private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
        private const int SERVICE_CONFIG_DESCRIPTION = 1;
        private const int SERVICE_CONFIG_FAILURE_ACTIONS = 2;
        private const int SC_ACTION_RESTART = 1;

        private const uint TOKEN_ALL_ACCESS = 0xF01FFu;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const int TokenUser = 1;
        private const int TokenSessionId = 12;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_HIDE = 0;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400u;
        private const uint CREATE_NO_WINDOW = 0x08000000u;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct QUERY_SERVICE_CONFIG
        {
            public uint dwServiceType;
            public uint dwStartType;
            public uint dwErrorControl;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpBinaryPathName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpLoadOrderGroup;
            public uint dwTagId;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpDependencies;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpServiceStartName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpDisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SC_ACTION
        {
            public int Type;
            public uint Delay;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_FAILURE_ACTIONS
        {
            public int dwResetPeriod;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpRebootMsg;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpCommand;
            public int cActions;
            public IntPtr lpsaActions;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_DESCRIPTION
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpDescription;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfigW(IntPtr hService, IntPtr lpServiceConfig, int cbBufSize, out int pcbBytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateServiceW(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lpServiceStartName,
            string lpPassword);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfigW(
            IntPtr hService,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lpServiceStartName,
            string lpPassword,
            string lpDisplayName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig2W(IntPtr hService, int dwInfoLevel, IntPtr lpInfo);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, ref uint TokenInformation, int TokenInformationLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessAsUserW(
            IntPtr hToken,
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
    }
}
