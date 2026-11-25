using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DualRemote
{
    // 登录自启与防待机的小封装：只在用户明确点击时生效，不启动后台进程/引擎，不提权，不改电源配置。
    internal static class WindowsSession
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "BoBoRemote";
        private const string LegacyRunValueName = "DualRemote.Windows";
        private const uint ES_CONTINUOUS = 0x80000000u;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001u;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        internal static bool TryLockWorkstation()
        {
            try { return LockWorkStation(); }
            catch { return false; }
        }

        // 只读：当前用户启动项里是否已有指向本程序的有效项。
        internal static bool StartsAtSignIn()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    return IsOurLaunchCommand(key.GetValue(RunValueName) as string)
                        || IsOurLaunchCommand(key.GetValue(LegacyRunValueName) as string);
                }
            }
            catch
            {
                return false;
            }
        }

        // 明确点击才增删；值只含固定的当前 EXE 绝对路径（带引号），仅写当前用户，并自动清理旧版重复启动键。
        internal static bool TrySetStartAtSignIn(bool enabled, out string error)
        {
            error = null;
            try
            {
                string exe = ExecutablePath();
                if (!File.Exists(exe))
                {
                    error = "找不到当前程序路径：" + exe;
                    return false;
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        error = "无法打开当前用户的启动项注册表键，请检查权限。";
                        return false;
                    }
                    if (key.GetValue(LegacyRunValueName) != null)
                        key.DeleteValue(LegacyRunValueName, false);
                    if (enabled) key.SetValue(RunValueName, Quote(exe), RegistryValueKind.String);
                    else if (key.GetValue(RunValueName) != null) key.DeleteValue(RunValueName, false);
                }
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "没有权限修改当前用户的启动项：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "设置登录时启动失败：" + ex.Message;
                return false;
            }
        }

        // 防待机：开启 ES_CONTINUOUS|ES_SYSTEM_REQUIRED，关闭 ES_CONTINUOUS。
        // 只在调用线程（应为 UI 线程）有效；不阻止合盖或手动睡眠，也不修改电源配置。
        internal static bool TryKeepAwake(bool enabled, out string error)
        {
            error = null;
            try
            {
                uint flags = enabled ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS;
                if (SetThreadExecutionState(flags) == 0)
                {
                    error = "系统未接受防待机请求，请确认在界面线程调用。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "设置防待机失败：" + ex.Message;
                return false;
            }
        }

        private static bool IsOurLaunchCommand(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string text = value.Trim();
            if (text.Length < 2 || text[0] != '"') return false;
            int close = text.IndexOf('"', 1);
            if (close < 0) return false;
            string path = text.Substring(1, close - 1);
            return string.Equals(path, ExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }

        private static string ExecutablePath()
        {
            return Path.GetFullPath(Application.ExecutablePath);
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }
    }
}
