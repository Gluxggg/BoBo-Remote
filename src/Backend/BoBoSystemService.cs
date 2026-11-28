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

        internal static string GetServiceStatusLabel()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    switch (sc.Status)
                    {
                        case ServiceControllerStatus.Running: return "服务运行中（支持锁屏/Win+L/UAC）";
                        case ServiceControllerStatus.StartPending: return "服务启动中…";
                        case ServiceControllerStatus.Stopped: return "已安装但未启动";
                        default: return "状态：" + sc.Status;
                    }
                }
            }
            catch
            {
                return "未安装（锁屏时系统将拦截画面与输入）";
            }
        }

        internal static int RunServiceHost()
        {
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
            int sid = requestedSessionId >= 0 ? requestedSessionId : GetActiveConsoleSessionIdSafe();
            if (sid < 0) sid = Process.GetCurrentProcess().SessionId;
            try
            {
                string err;
                BoBoCoreInterop.EnsureLoaded(out err);
            }
            catch { }

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

        internal static bool InstallAndStartService(out string message)
        {
            if (!IsProcessElevated() && !IsRunningAsSystem())
            {
                message = "需要管理员权限才能安装系统服务。";
                return false;
            }

            string mainExe;
            try
            {
                mainExe = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch (Exception ex)
            {
                message = "无法定位主程序路径：" + ex.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(mainExe) || !File.Exists(mainExe))
            {
                message = "主程序文件不存在：" + mainExe;
                return false;
            }

            string binPath = "\"" + mainExe + "\" --service";
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
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
                        message = "创建系统服务失败（错误码 " + err + "）。";
                        return false;
                    }
                }
                else
                {
                    ChangeServiceConfigW(
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
                        ServiceDisplayName);
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
                BoBoNatTraversal.ConfigureFirewallRulesForPaths(mainExe, BoBoEmbeddedTailnet.GetExpectedBinaryPath(), true);
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
                message = "锁屏穿透服务已安装并启动。";
                return true;
            }
            catch (Exception ex)
            {
                message = "服务已注册但启动超时：" + ex.Message;
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

            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                message = "无法打开 Windows 服务管理器。";
                return false;
            }
            try
            {
                IntPtr svc = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    message = "服务未安装。";
                    return true;
                }
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
            finally
            {
                CloseServiceHandle(scm);
            }

            try
            {
                BoBoNatTraversal.ConfigureFirewallRulesForPaths(null, null, false);
            }
            catch { }

            message = "锁屏穿透服务已卸载。";
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
                var psi = new ProcessStartInfo(mainExe, "--install-service")
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
                    p.WaitForExit(20000);
                    if (p.ExitCode == 0 || IsServiceRunning())
                    {
                        message = "锁屏穿透与防火墙规则已就绪。";
                        return true;
                    }
                    message = "安装进程返回退出码 " + p.ExitCode + "。";
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
                Task.Run(delegate
                {
                    try
                    {
                        string mainExe = Process.GetCurrentProcess().MainModule.FileName;
                        BoBoNatTraversal.ConfigureFirewallRulesForPaths(mainExe, BoBoEmbeddedTailnet.GetExpectedBinaryPath(), true);
                    }
                    catch { }
                });
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

        private const byte OpVideoFrame = 100;
        private const byte OpAudioFrame = 101;
        private const byte OpCreateReply = 102;
        private const byte OpAck = 104;
        private const byte OpPong = 105;
        private const byte OpSuperDisplayReply = 106;

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
            private readonly object inputQueueLock = new object();
            private readonly Queue<Action> inputQueue = new Queue<Action>();
            private readonly AutoResetEvent inputWakeEvent = new AutoResetEvent(false);
            private readonly Thread inputThread;
            private readonly Thread serverThread;

            private volatile bool disposed;
            private long lastDesktopSyncTicks;
            private int injectedPackets;
            private int sasCalls;
            private NamedPipeServerStream activeServerPipe;

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
            }

            internal void RunBlocking()
            {
                while (!disposed && !stopEvent.IsSet)
                {
                    NamedPipeServerStream pipe = null;
                    try
                    {
                        pipe = CreateSecuredPipeServer(pipeName, targetSessionId, restrictToConsoleUser);
                        activeServerPipe = pipe;
                        IAsyncResult ar = pipe.BeginWaitForConnection(null, null);
                        WaitHandle[] waits = new WaitHandle[] { ar.AsyncWaitHandle, stopEvent.WaitHandle };
                        int idx = WaitHandle.WaitAny(waits);
                        if (idx != 0)
                        {
                            try { pipe.Dispose(); } catch { }
                            break;
                        }
                        pipe.EndWaitForConnection(ar);
                        HandleClientConnection(pipe);
                    }
                    catch
                    {
                        if (!disposed && !stopEvent.IsSet) Thread.Sleep(120);
                    }
                    finally
                    {
                        activeServerPipe = null;
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
                BoBoCoreInterop.BoBoHostNativeHandle hostHandle = null;
                long sessionToken = 0;

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
                            WritePipeMessage(pipe, writeLock, OpPong, new byte[] { acc, uipi });
                        }
                        else if (op == OpCreateSession)
                        {
                            if (hostHandle != null)
                            {
                                try { hostHandle.Dispose(); } catch { }
                                hostHandle = null;
                            }
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

                            long myToken = Interlocked.Increment(ref sessionToken);
                            BoBoCoreInterop.BoBoHostNativeHandle createdRef = null;
                            string err;
                            createdRef = BoBoCoreInterop.CreateHostSession(
                                monIdx, w, h, fps, bitrate, mute,
                                delegate(byte[] data, bool isKey, int vw, int vh, uint ts)
                                {
                                    if (Volatile.Read(ref sessionToken) != myToken || data == null || data.Length == 0) return;
                                    BoBoCoreInterop.BoBoHostNativeHandle hRef = createdRef;
                                    byte vCodec = hRef != null ? (byte)hRef.VideoCodec : (byte)BoBoCoreInterop.VideoCodecH264;
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
                                    if (Volatile.Read(ref sessionToken) != myToken || data == null || data.Length == 0) return;
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
                            hostHandle = createdRef;
                            if (hostHandle != null)
                            {
                                SendCreateReply(pipe, writeLock, true, (byte)hostHandle.VideoCodec, (byte)hostHandle.AudioCodec, "");
                            }
                            else
                            {
                                SendCreateReply(pipe, writeLock, false, 0, 0, err ?? "CreateHostSession failed");
                            }
                        }
                        else if (op == OpDestroySession)
                        {
                            Interlocked.Increment(ref sessionToken);
                            if (hostHandle != null)
                            {
                                try { hostHandle.Dispose(); } catch { }
                                hostHandle = null;
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
                            bool ok = hostHandle != null && hostHandle.Reconfigure(monIdx, w, h, fps, bitrate);
                            WritePipeMessage(pipe, writeLock, OpAck, new byte[] { ok ? (byte)1 : (byte)0 });
                        }
                        else if (op == OpSetBitrate && payload != null && payload.Length >= 4)
                        {
                            int bitrate = ReadInt32LE(payload, 0);
                            if (hostHandle != null) hostHandle.SetBitrate(bitrate);
                        }
                        else if (op == OpSetFps && payload != null && payload.Length >= 4)
                        {
                            int fps = ReadInt32LE(payload, 0);
                            if (hostHandle != null) hostHandle.SetFps(fps);
                        }
                        else if (op == OpSetCompositeCursor && payload != null && payload.Length >= 1)
                        {
                            if (hostHandle != null) hostHandle.SetCompositeCursor(payload[0] != 0);
                        }
                        else if (op == OpRequestKeyframe)
                        {
                            if (hostHandle != null) hostHandle.RequestKeyframe();
                        }
                        else if (op == OpSetMuteHost && payload != null && payload.Length >= 1)
                        {
                            if (hostHandle != null) hostHandle.SetMuteHost(payload[0] != 0);
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
                                SyncInputDesktopThrottled();
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
                                    SyncInputDesktopThrottled();
                                    BoBoCoreInterop.InjectUnicodeText(text);
                                    Interlocked.Increment(ref injectedPackets);
                                });
                            }
                        }
                        else if (op == OpSendSas)
                        {
                            EnqueueInputAction(delegate
                            {
                                SyncInputDesktopThrottled();
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
                    }
                }
                finally
                {
                    Interlocked.Increment(ref sessionToken);
                    if (hostHandle != null)
                    {
                        try { hostHandle.Dispose(); } catch { }
                    }
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

            private void SyncInputDesktopThrottled()
            {
                long now = Stopwatch.GetTimestamp();
                long last = Interlocked.Read(ref lastDesktopSyncTicks);
                if (last == 0 || ((now - last) * 1000.0 / Stopwatch.Frequency) >= 25.0)
                {
                    BoBoCoreInterop.SyncInputDesktop();
                    Interlocked.Exchange(ref lastDesktopSyncTicks, now);
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                stopEvent.Set();
                inputWakeEvent.Set();
                NamedPipeServerStream p = activeServerPipe;
                if (p != null)
                {
                    try { p.Dispose(); } catch { }
                }
                if (serverThread != null && serverThread.IsAlive)
                {
                    try { serverThread.Join(1200); } catch { }
                }
                if (inputThread != null && inputThread.IsAlive)
                {
                    try { inputThread.Join(1200); } catch { }
                }
                stopEvent.Dispose();
                inputWakeEvent.Dispose();
            }
        }

        private static NamedPipeServerStream CreateSecuredPipeServer(string pipeName, int sessionId, bool includeConsoleUser)
        {
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
                1,
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
            private readonly AutoResetEvent rpcEvent = new AutoResetEvent(false);
            private readonly Thread readerThread;

            private volatile bool disposed;
            private volatile bool disconnected;
            private Action<byte[], bool, int, int, uint> videoHandler;
            private Action<byte[], int, int, int> audioHandler;
            private int lastVideoCodec = BoBoCoreInterop.VideoCodecH264;
            private int lastAudioCodec = BoBoCoreInterop.AudioCodecAdpcm;

            private byte rpcResultOp;
            private byte[] rpcResultPayload;

            internal bool IsConnected { get { return !disposed && !disconnected && pipe.IsConnected; } }
            internal int VideoCodec { get { return lastVideoCodec; } }
            internal int AudioCodec { get { return lastAudioCodec; } }

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
                NamedPipeClientStream p = null;
                try
                {
                    p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    p.Connect(Math.Max(100, timeoutMs));
                    var c = new DesktopHelperClient(p);
                    if (c.Ping()) return c;
                    c.Dispose();
                    return null;
                }
                catch
                {
                    if (p != null) { try { p.Dispose(); } catch { } }
                    return null;
                }
            }

            internal bool Ping()
            {
                byte[] rep = SendRpc(OpPing, null, OpPong, 1200);
                return rep != null;
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

            private byte[] SendRpc(byte reqOp, byte[] payload, byte expectedReplyOp, int timeoutMs)
            {
                if (disposed || disconnected) return null;
                lock (rpcLock)
                {
                    if (disposed || disconnected) return null;
                    rpcResultOp = 0;
                    rpcResultPayload = null;
                    rpcEvent.Reset();
                    if (!WritePipeMessage(pipe, writeLock, reqOp, payload)) return null;
                    if (!rpcEvent.WaitOne(timeoutMs)) return null;
                    return rpcResultOp == expectedReplyOp ? rpcResultPayload : null;
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
                        else if (op == OpCreateReply || op == OpAck || op == OpPong || op == OpSuperDisplayReply)
                        {
                            rpcResultOp = op;
                            rpcResultPayload = payload;
                            rpcEvent.Set();
                        }
                    }
                }
                catch { }
                finally
                {
                    disconnected = true;
                    try { rpcEvent.Set(); } catch { }
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                disconnected = true;
                try { pipe.Dispose(); } catch { }
                try { rpcEvent.Set(); } catch { }
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
        /// 不可用时无缝回退至进程内 BoBoCoreInterop。
        /// </summary>
        internal sealed class BoBoSecureDesktopBridge : IDisposable
        {
            private readonly object gate = new object();
            private DesktopHelperClient helperClient;
            private BoBoCoreInterop.BoBoHostNativeHandle localHandle;
            private long lastHelperProbeTicks;
            private bool disposed;

            private DesktopHelperClient EnsureHelperConnectedLocked()
            {
                if (disposed || IsRunningAsSystem()) return null;
                if (helperClient != null && helperClient.IsConnected) return helperClient;
                if (helperClient != null)
                {
                    try { helperClient.Dispose(); } catch { }
                    helperClient = null;
                }
                long now = Stopwatch.GetTimestamp();
                if (lastHelperProbeTicks != 0 && ((now - lastHelperProbeTicks) * 1000.0 / Stopwatch.Frequency) < 2500.0)
                {
                    return null;
                }
                int sid = GetActiveConsoleSessionIdSafe();
                if (sid < 0) sid = Process.GetCurrentProcess().SessionId;
                DesktopHelperClient client = DesktopHelperClient.TryConnect(GetPipeNameForSession(sid), ConnectTimeoutMs);
                if (client != null)
                {
                    helperClient = client;
                    lastHelperProbeTicks = 0;
                    return client;
                }
                lastHelperProbeTicks = now;
                return null;
            }

            internal bool IsUsingSystemHelper
            {
                get
                {
                    lock (gate) { return helperClient != null && helperClient.IsConnected; }
                }
            }

            internal int VideoCodec
            {
                get
                {
                    lock (gate)
                    {
                        if (helperClient != null) return helperClient.VideoCodec;
                        if (localHandle != null) return localHandle.VideoCodec;
                        return BoBoCoreInterop.VideoCodecH264;
                    }
                }
            }

            internal int AudioCodec
            {
                get
                {
                    lock (gate)
                    {
                        if (helperClient != null) return helperClient.AudioCodec;
                        if (localHandle != null) return localHandle.AudioCodec;
                        return BoBoCoreInterop.AudioCodecAdpcm;
                    }
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

                // 若当前并非 SYSTEM 进程且服务运行中，优先接入活跃控制台会话的 SYSTEM Helper
                DesktopHelperClient client = null;
                lock (gate)
                {
                    lastHelperProbeTicks = 0;
                    client = EnsureHelperConnectedLocked();
                }
                if (client != null)
                {
                    string helperErr;
                    if (client.CreateHostSession(
                        monitorIndex, targetWidth, targetHeight, fps, bitrateKbps,
                        muteHostSpeakers, compositeCursor, preferAacAudio, preferHevcVideo,
                        onVideo, onAudio, out helperErr))
                    {
                        error = "";
                        return true;
                    }
                    lock (gate)
                    {
                        if (object.ReferenceEquals(helperClient, client)) helperClient = null;
                    }
                    client.Dispose();
                }

                BoBoCoreInterop.BoBoHostNativeHandle local = BoBoCoreInterop.CreateHostSession(
                    monitorIndex, targetWidth, targetHeight, fps, bitrateKbps, muteHostSpeakers,
                    onVideo, onAudio, out error, compositeCursor, preferAacAudio, preferHevcVideo);
                if (local == null) return false;
                lock (gate) { localHandle = local; }
                return true;
            }

            internal bool Reconfigure(int monitorIndex, int width, int height, int fps, int bitrateKbps)
            {
                lock (gate)
                {
                    if (disposed) return false;
                    if (helperClient != null && helperClient.IsConnected)
                        return helperClient.Reconfigure(monitorIndex, width, height, fps, bitrateKbps);
                    if (localHandle != null)
                        return localHandle.Reconfigure(monitorIndex, width, height, fps, bitrateKbps);
                    return false;
                }
            }

            internal bool SetBitrate(int bitrateKbps)
            {
                lock (gate)
                {
                    if (disposed) return false;
                    if (helperClient != null && helperClient.IsConnected) { helperClient.SetBitrate(bitrateKbps); return true; }
                    if (localHandle != null) return localHandle.SetBitrate(bitrateKbps);
                    return false;
                }
            }

            internal bool SetFps(int fps)
            {
                lock (gate)
                {
                    if (disposed) return false;
                    if (helperClient != null && helperClient.IsConnected) { helperClient.SetFps(fps); return true; }
                    if (localHandle != null) return localHandle.SetFps(fps);
                    return false;
                }
            }

            internal void SetCompositeCursor(bool enable)
            {
                lock (gate)
                {
                    if (disposed) return;
                    if (helperClient != null && helperClient.IsConnected) helperClient.SetCompositeCursor(enable);
                    else if (localHandle != null) localHandle.SetCompositeCursor(enable);
                }
            }

            internal void RequestKeyframe()
            {
                lock (gate)
                {
                    if (disposed) return;
                    if (helperClient != null && helperClient.IsConnected) helperClient.RequestKeyframe();
                    else if (localHandle != null) localHandle.RequestKeyframe();
                }
            }

            internal void SetMuteHost(bool mute)
            {
                lock (gate)
                {
                    if (disposed) return;
                    if (helperClient != null && helperClient.IsConnected) helperClient.SetMuteHost(mute);
                    else if (localHandle != null) localHandle.SetMuteHost(mute);
                }
            }

            internal void InjectInput(int monitorIndex, BoBoInputPacket pkt)
            {
                DesktopHelperClient hc;
                lock (gate) { hc = helperClient; }
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
                DesktopHelperClient hc;
                lock (gate) { hc = helperClient; }
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
                DesktopHelperClient hc;
                lock (gate) { hc = EnsureHelperConnectedLocked(); }
                if (hc != null && hc.IsConnected)
                {
                    hc.SendSAS();
                    return true;
                }
                return BoBoCoreInterop.SendSAS();
            }

            internal bool ApplySuperDisplay(int monitorIndex, int clientW, int clientH, out int actualW, out int actualH)
            {
                DesktopHelperClient hc;
                lock (gate) { hc = EnsureHelperConnectedLocked(); }
                if (hc != null && hc.IsConnected)
                {
                    return hc.ApplySuperDisplay(monitorIndex, clientW, clientH, out actualW, out actualH);
                }
                return BoBoCoreInterop.ApplySuperDisplay(monitorIndex, clientW, clientH, out actualW, out actualH);
            }

            internal void RestoreSuperDisplay()
            {
                DesktopHelperClient hc;
                lock (gate) { hc = EnsureHelperConnectedLocked(); }
                if (hc != null && hc.IsConnected)
                {
                    hc.RestoreSuperDisplay();
                    return;
                }
                BoBoCoreInterop.RestoreSuperDisplay();
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
                    hc = helperClient;
                    lh = localHandle;
                    localHandle = null;
                    helperClient = null;
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

        private const uint SC_MANAGER_ALL_ACCESS = 0xF003Fu;
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
