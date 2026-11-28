using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DualRemote
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\BoBoRemote.Launcher.SingleInstance";
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xFFFF);
        internal static readonly int WmShowExistingInstance = RegisterWindowMessage("BoBoRemote.Launcher.ShowExistingInstance");

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const uint AttachParentProcess = 0xFFFFFFFFu;

        [STAThread]
        internal static int Main(string[] args)
        {
            bool selfTest = false;
            bool forceTour = false;
            bool applyUpdate = false;
            bool runAsService = false;
            bool runDesktopHelper = false;
            bool installService = false;
            bool upgradeService = false;
            bool uninstallService = false;
            int helperSessionId = -1;
            string updatedVersion = null;
            string firewallExe = null;
            foreach (string rawArg in args)
            {
                string arg = rawArg;
                if (string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    selfTest = true;
                }
                else if (string.Equals(arg, "--apply-update", StringComparison.OrdinalIgnoreCase))
                {
                    applyUpdate = true;
                }
                else if (string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase))
                {
                    runAsService = true;
                }
                else if (string.Equals(arg, "--desktop-helper", StringComparison.OrdinalIgnoreCase))
                {
                    runDesktopHelper = true;
                }
                else if (string.Equals(arg, "--install-service", StringComparison.OrdinalIgnoreCase))
                {
                    installService = true;
                }
                else if (string.Equals(arg, "--upgrade-service", StringComparison.OrdinalIgnoreCase))
                {
                    upgradeService = true;
                }
                else if (string.Equals(arg, "--uninstall-service", StringComparison.OrdinalIgnoreCase))
                {
                    uninstallService = true;
                }
                else if (arg != null && arg.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
                {
                    int parsedSid;
                    if (int.TryParse(arg.Substring(10), out parsedSid)) helperSessionId = parsedSid;
                }
                else if (arg != null && arg.StartsWith("--firewall-exe=", StringComparison.OrdinalIgnoreCase))
                {
                    firewallExe = arg.Substring(15).Trim().Trim('"');
                }
                else if (arg != null && arg.StartsWith("--updated-version=", StringComparison.OrdinalIgnoreCase))
                {
                    updatedVersion = arg.Substring(18).Trim().Trim('"');
                }
                else if (string.Equals(arg, "--tour", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "--reset-tour", StringComparison.OrdinalIgnoreCase))
                {
                    forceTour = true;
                    if (string.Equals(arg, "--reset-tour", StringComparison.OrdinalIgnoreCase))
                        OnboardingStore.Reset();
                }
            }
            if (runAsService)
            {
                return BoBoSystemService.RunServiceHost();
            }
            if (runDesktopHelper)
            {
                return BoBoSystemService.RunDesktopHelper(helperSessionId);
            }
            if (installService)
            {
                string svcMsg;
                bool ok = BoBoSystemService.InstallAndStartService(out svcMsg, firewallExe);
                try { Console.WriteLine(svcMsg); } catch { }
                return ok ? 0 : 1;
            }
            if (upgradeService)
            {
                string svcMsg;
                bool ok = BoBoSystemService.UpgradeService(out svcMsg, firewallExe);
                try { Console.WriteLine(svcMsg); } catch { }
                return ok ? 0 : 1;
            }
            if (uninstallService)
            {
                string svcMsg;
                bool ok = BoBoSystemService.UninstallService(out svcMsg);
                try { Console.WriteLine(svcMsg); } catch { }
                return ok ? 0 : 1;
            }
            if (applyUpdate)
            {
                return AppUpdater.RunApplyUpdate(args);
            }
            if (selfTest)
            {
                // 无 UI：让 self-test 输出尽量出现在调用方控制台，同时写临时报告文件。
                AttachConsole(AttachParentProcess);
                try
                {
                    return SelfTest.Run();
                }
                catch (Exception ex)
                {
                    string path = Path.Combine(Path.GetTempPath(), "双端远程-self-test.log");
                    try
                    {
                        File.WriteAllText(path, "self-test 崩溃：" + ex + "\r\n", new System.Text.UTF8Encoding(false));
                    }
                    catch
                    {
                    }
                    try
                    {
                        Console.WriteLine("self-test 崩溃：" + ex);
                    }
                    catch
                    {
                    }
                    return 2;
                }
            }

            bool createdNew = true;
            Mutex instanceMutex = null;
            int maxAttempts = string.IsNullOrEmpty(updatedVersion) ? 1 : 12;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
                }
                catch
                {
                    createdNew = true;
                }
                if (createdNew) break;
                if (instanceMutex != null)
                {
                    try { instanceMutex.Dispose(); } catch { }
                    instanceMutex = null;
                }
                if (attempt + 1 < maxAttempts) Thread.Sleep(250);
            }
            if (!createdNew)
            {
                if (WmShowExistingInstance != 0)
                {
                    try { PostMessage(HwndBroadcast, WmShowExistingInstance, IntPtr.Zero, IntPtr.Zero); } catch { }
                }
                return 0;
            }

            AppUpdater.CleanupOldUpdateCacheAsync();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                EmergencyRestoreHardwareState();
                try
                {
                    MessageBox.Show("程序发生未处理界面异常，已自动解除防窥锁与分辨率保护：\r\n" + (e.Exception != null ? e.Exception.Message : "未知错误"),
                        "BoBo远程", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate
            {
                EmergencyRestoreHardwareState();
            };
            try
            {
                Application.Run(new MainForm(forceTour, updatedVersion));
                return 0;
            }
            catch (Exception ex)
            {
                EmergencyRestoreHardwareState();
                MessageBox.Show("程序异常：" + ex.Message, "BoBo远程", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                EmergencyRestoreHardwareState();
                if (instanceMutex != null)
                {
                    try { instanceMutex.ReleaseMutex(); } catch { }
                    try { instanceMutex.Dispose(); } catch { }
                }
            }
        }

        private static void EmergencyRestoreHardwareState()
        {
            try { BoBoCoreInterop.SetPrivacyInputBlock(false, null); } catch { }
            try { BoBoCoreInterop.RestoreSuperDisplay(); } catch { }
            try { PrivacyDisplayDimmer.RestoreAllSync(); } catch { }
        }
    }
}
