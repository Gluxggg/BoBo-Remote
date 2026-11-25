using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace DualRemote
{
    internal static class Engines
    {
        internal static string AppDir
        {
            get { return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); }
        }

        internal static string TailscaleExe
        {
            get
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(Path.Combine(programFiles, "Tailscale"), "tailscale.exe");
            }
        }

        internal static bool TailscalePresent
        {
            get { return File.Exists(TailscaleExe); }
        }

        // 兼容旧 favorites.txt 中保存的协助设备 ID。
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
                error = "历史设备码长度应在 3–64 个字符之间。";
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

        internal static string Quote(string value)
        {
            return "\"" + value + "\"";
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

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        internal static void ReleaseCursorClip()
        {
            try { ClipCursor(IntPtr.Zero); }
            catch { }
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
    }
}
