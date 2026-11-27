using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BoBoSetup
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        private const uint AttachParentProcess = 0xFFFFFFFFu;

        [STAThread]
        internal static int Main(string[] args)
        {
            bool selfTest = false;
            bool silent = false;
            bool uninstall = false;
            bool purgeData = false;
            bool createDesktop = true;
            bool createStartMenu = true;
            bool launchAfter = true;
            bool autoStart = false;
            bool preserveAutoStart = false;
            int waitPid = 0;
            string customDir = null;

            foreach (string raw in args)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string arg = raw.Trim();
                if (string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                else if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (string.Equals(arg, "--purge-data", StringComparison.OrdinalIgnoreCase)) purgeData = true;
                else if (string.Equals(arg, "--no-desktop", StringComparison.OrdinalIgnoreCase)) createDesktop = false;
                else if (string.Equals(arg, "--no-startmenu", StringComparison.OrdinalIgnoreCase)) createStartMenu = false;
                else if (string.Equals(arg, "--no-launch", StringComparison.OrdinalIgnoreCase)) launchAfter = false;
                else if (string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase)) autoStart = true;
                else if (string.Equals(arg, "--preserve-autostart", StringComparison.OrdinalIgnoreCase)) preserveAutoStart = true;
                else if (arg.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(arg.Substring(11).Trim().Trim('"'), out waitPid);
                }
                else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                {
                    customDir = arg.Substring(6).Trim().Trim('"');
                }
            }

            if (selfTest)
            {
                AttachConsole(AttachParentProcess);
                return SetupSelfTest.Run();
            }

            string selfExe = Assembly.GetExecutingAssembly().Location;
            PayloadTrailer trailer;
            bool hasPayload = PayloadArchive.TryReadTrailer(selfExe, out trailer);
            if (!hasPayload)
            {
                uninstall = true;
            }

            var options = new InstallOptions
            {
                InstallDir = string.IsNullOrWhiteSpace(customDir)
                    ? (uninstall ? InstallerEngine.DetectInstalledDir(selfExe) : InstallerEngine.DefaultInstallDir)
                    : customDir,
                CreateDesktopShortcut = createDesktop,
                CreateStartMenuShortcut = createStartMenu,
                LaunchAfterInstall = launchAfter,
                AutoStart = autoStart,
                PreserveAutoStart = preserveAutoStart,
                WaitPid = waitPid,
                PurgeUserData = purgeData
            };

            if (silent)
            {
                AttachConsole(AttachParentProcess);
                try
                {
                    if (uninstall)
                    {
                        InstallerEngine.RunUninstall(options.InstallDir, options.PurgeUserData, false,
                            delegate(int pct, string msg) { SafeWriteLine("[" + pct + "%] " + msg); });
                    }
                    else
                    {
                        InstallerEngine.RunInstall(selfExe, trailer, options,
                            delegate(int pct, string msg) { SafeWriteLine("[" + pct + "%] " + msg); });
                        if (options.LaunchAfterInstall)
                        {
                            string launchArgs = options.PreserveAutoStart
                                ? ("--updated-version=" + InstallerEngine.GetAppVersion())
                                : null;
                            InstallerEngine.LaunchMainApp(options.InstallDir, launchArgs);
                        }
                    }
                    SafeWriteLine("OK");
                    return 0;
                }
                catch (Exception ex)
                {
                    SafeWriteLine("ERROR: " + ex.Message);
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (uninstall)
                {
                    Application.Run(new UninstallForm(options.InstallDir));
                }
                else
                {
                    Application.Run(new SetupForm(selfExe, trailer, options));
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装程序运行异常：" + ex.Message, "BoBo Remote 安装向导", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void SafeWriteLine(string line)
        {
            try { Console.WriteLine(line); }
            catch { }
        }
    }

    internal sealed class InstallOptions
    {
        internal string InstallDir;
        internal bool CreateDesktopShortcut = true;
        internal bool CreateStartMenuShortcut = true;
        internal bool LaunchAfterInstall = true;
        internal bool AutoStart;
        internal bool PreserveAutoStart;
        internal int WaitPid;
        internal bool PurgeUserData;
    }

    internal sealed class PayloadTrailer
    {
        internal const int TrailerSize = 64;
        internal static readonly byte[] MagicStart = Encoding.ASCII.GetBytes("BOBOSET1");
        internal static readonly byte[] MagicEnd = Encoding.ASCII.GetBytes("END1");

        internal long PayloadOffset;
        internal long PayloadLength;
        internal byte[] Sha256;
        internal int Flags;
    }

    internal static class PayloadArchive
    {
        internal static bool TryReadTrailer(string exePath, out PayloadTrailer trailer)
        {
            trailer = null;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < PayloadTrailer.TrailerSize + 1024) return false;
                    fs.Seek(-PayloadTrailer.TrailerSize, SeekOrigin.End);
                    byte[] buf = new byte[PayloadTrailer.TrailerSize];
                    if (ReadExact(fs, buf, 0, buf.Length) != buf.Length) return false;
                    return TryParseTrailer(buf, fs.Length, out trailer);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryParseTrailer(byte[] buf, long totalFileLength, out PayloadTrailer trailer)
        {
            trailer = null;
            if (buf == null || buf.Length != PayloadTrailer.TrailerSize) return false;
            for (int i = 0; i < 8; i++)
            {
                if (buf[i] != PayloadTrailer.MagicStart[i]) return false;
            }
            for (int i = 0; i < 4; i++)
            {
                if (buf[60 + i] != PayloadTrailer.MagicEnd[i]) return false;
            }

            long offset = BitConverter.ToInt64(buf, 8);
            long length = BitConverter.ToInt64(buf, 16);
            int flags = BitConverter.ToInt32(buf, 56);
            if (offset <= 0 || length <= 0 || offset + length + PayloadTrailer.TrailerSize != totalFileLength)
                return false;

            byte[] hash = new byte[32];
            Buffer.BlockCopy(buf, 24, hash, 0, 32);

            trailer = new PayloadTrailer
            {
                PayloadOffset = offset,
                PayloadLength = length,
                Sha256 = hash,
                Flags = flags
            };
            return true;
        }

        internal static byte[] FormatTrailer(long payloadOffset, long payloadLength, byte[] sha256, int flags)
        {
            if (sha256 == null || sha256.Length != 32) throw new ArgumentException("sha256 must be 32 bytes");
            byte[] buf = new byte[PayloadTrailer.TrailerSize];
            Buffer.BlockCopy(PayloadTrailer.MagicStart, 0, buf, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(payloadOffset), 0, buf, 8, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(payloadLength), 0, buf, 16, 8);
            Buffer.BlockCopy(sha256, 0, buf, 24, 32);
            Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, buf, 56, 4);
            Buffer.BlockCopy(PayloadTrailer.MagicEnd, 0, buf, 60, 4);
            return buf;
        }

        internal static bool VerifyPayloadSha256(string exePath, PayloadTrailer trailer, Action<int> progressPercent)
        {
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sub = new BoundedSubStream(fs, trailer.PayloadOffset, trailer.PayloadLength))
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024];
                long readTotal = 0;
                int n;
                while ((n = sub.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    readTotal += n;
                    if (progressPercent != null && trailer.PayloadLength > 0)
                    {
                        int pct = (int)Math.Min(100, (readTotal * 100L) / trailer.PayloadLength);
                        progressPercent(pct);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                byte[] actual = sha.Hash;
                if (actual == null || actual.Length != 32) return false;
                for (int i = 0; i < 32; i++)
                {
                    if (actual[i] != trailer.Sha256[i]) return false;
                }
                return true;
            }
        }

        private static int ReadExact(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    internal sealed class BoundedSubStream : Stream
    {
        private readonly Stream inner;
        private readonly long baseOffset;
        private readonly long length;
        private long position;

        internal BoundedSubStream(Stream inner, long baseOffset, long length)
        {
            this.inner = inner;
            this.baseOffset = baseOffset;
            this.length = length;
            this.position = 0;
            this.inner.Seek(baseOffset, SeekOrigin.Begin);
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }

        public override long Position
        {
            get { return position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = length - position;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)remaining;
            if (inner.Position != baseOffset + position)
                inner.Seek(baseOffset + position, SeekOrigin.Begin);
            int read = inner.Read(buffer, offset, count);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            if (origin == SeekOrigin.Begin) target = offset;
            else if (origin == SeekOrigin.Current) target = position + offset;
            else target = length + offset;

            if (target < 0) target = 0;
            if (target > length) target = length;
            position = target;
            inner.Seek(baseOffset + position, SeekOrigin.Begin);
            return position;
        }

        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    internal static class InstallerEngine
    {
        internal const string AppDisplayName = "BoBo Remote";
        internal const string MainExeName = "BoBo远程.exe";
        internal const string UninstallExeName = "BoBoRemote-Uninstall.exe";
        internal const string UninstallRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BoBoRemote";
        internal const string RunRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunValueName = "BoBoRemote";
        internal const string LegacyRunValueName = "DualRemote.Windows";
        internal const string ShortcutFileName = "BoBo Remote.lnk";

        internal static string DefaultInstallDir
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "BoBoRemote");
            }
        }

        internal static string UserDataDir
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "双端远程");
            }
        }

        internal static string GetAppVersion()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DualRemote.setup-version.txt"))
                {
                    if (s != null)
                    {
                        using (var sr = new StreamReader(s, Encoding.UTF8))
                        {
                            string v = sr.ReadToEnd().Trim();
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                }
            }
            catch { }
            return "7.5.0";
        }

        internal static Bitmap LoadBrandLogo()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DualRemote.bobo-logo.png"))
                {
                    if (s != null)
                    {
                        using (var img = Image.FromStream(s))
                        {
                            return new Bitmap(img);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        internal static string DetectInstalledDir(string selfExe)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallRegSubKey))
                {
                    if (key != null)
                    {
                        string loc = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                            return loc;
                    }
                }
            }
            catch { }
            string dir = Path.GetDirectoryName(selfExe);
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MainExeName)))
                return dir;
            return DefaultInstallDir;
        }

        internal static bool IsSafeInstallDir(string dir, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(dir))
            {
                reason = "安装目录不能为空。";
                return false;
            }
            string full;
            try
            {
                full = Path.GetFullPath(dir).TrimEnd('\\');
            }
            catch
            {
                reason = "安装目录路径格式无效。";
                return false;
            }
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || string.Equals(full, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                reason = "不能直接安装到磁盘根目录，请选择单独子目录（例如 BoBoRemote）。";
                return false;
            }
            string[] forbidden = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };
            foreach (string f in forbidden)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (string.Equals(full, f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "为防止误删其它文件，请为 BoBo Remote 指定独立文件夹（例如 " + Path.Combine(full, "BoBoRemote") + "）。";
                    return false;
                }
            }
            return true;
        }

        internal static string NormalizeZipEntryRelativePath(string entryFullName)
        {
            if (string.IsNullOrEmpty(entryFullName)) return string.Empty;
            string s = entryFullName.Replace('/', '\\').TrimStart('\\');
            if (s.StartsWith("BoBo远程\\", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("BoBo远程\\".Length);
            else if (string.Equals(s, "BoBo远程", StringComparison.OrdinalIgnoreCase))
                s = string.Empty;
            return s;
        }

        internal static bool TryResolveSafeExtractPath(string installDir, string relativePath, out string fullDestPath)
        {
            fullDestPath = null;
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string normRoot = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(normRoot, relativePath));
            }
            catch
            {
                return false;
            }
            if (!candidate.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            fullDestPath = candidate;
            return true;
        }

        internal static void RunInstall(string selfExe, PayloadTrailer trailer, InstallOptions options, Action<int, string> report)
        {
            string reason;
            if (!IsSafeInstallDir(options.InstallDir, out reason))
                throw new InvalidOperationException(reason);

            string installDir = Path.GetFullPath(options.InstallDir).TrimEnd('\\');
            string version = GetAppVersion();

            Report(report, 2, "正在校验安装包完整性（SHA-256）…");
            if (!PayloadArchive.VerifyPayloadSha256(selfExe, trailer, delegate(int p)
            {
                Report(report, 2 + (p * 13) / 100, "正在校验安装包完整性（" + p + "%）…");
            }))
            {
                throw new InvalidOperationException("安装包 SHA-256 完整性校验失败，文件可能已损坏或下载不完整，请重新下载。");
            }

            Report(report, 16, "正在检查并关闭旧版本运行进程…");
            if (options.WaitPid > 0)
            {
                WaitForProcessExit(options.WaitPid, 10000);
                Thread.Sleep(300);
            }
            StopRunningProcessesInDir(installDir);

            // V6 全内置架构升级：彻底移除旧版 V5 遗留的独立外置引擎目录与独立播放核
            RemoveLegacyExternalEngines(installDir);

            Directory.CreateDirectory(installDir);
            Report(report, 20, "正在释放 BoBo V6 全内置核心程序…");

            var installedRelPaths = new List<string>();
            long totalExtractedBytes = 0;
            using (var fs = new FileStream(selfExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sub = new BoundedSubStream(fs, trailer.PayloadOffset, trailer.PayloadLength))
            using (var zip = new ZipArchive(sub, ZipArchiveMode.Read, true))
            {
                int totalEntries = Math.Max(1, zip.Entries.Count);
                int done = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    done++;
                    string rel = NormalizeZipEntryRelativePath(entry.FullName);
                    if (string.IsNullOrEmpty(rel)) continue;

                    bool isDir = entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\");
                    string destPath;
                    if (!TryResolveSafeExtractPath(installDir, rel.TrimEnd('\\'), out destPath))
                        throw new InvalidOperationException("安装包内检测到非法路径：" + entry.FullName);

                    if (isDir)
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        string parent = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        using (Stream src = entry.Open())
                        using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            src.CopyTo(dst);
                        }
                        try { File.SetLastWriteTime(destPath, entry.LastWriteTime.DateTime); }
                        catch { }
                        installedRelPaths.Add(rel.TrimEnd('\\'));
                        totalExtractedBytes += entry.Length;
                    }

                    if (done == 1 || done == totalEntries || (done % 18 == 0))
                    {
                        int pct = 20 + (done * 68) / totalEntries;
                        string leaf = Path.GetFileName(rel.TrimEnd('\\'));
                        Report(report, pct, "正在释放文件（" + done + "/" + totalEntries + "）：" + leaf);
                    }
                }
            }

            Report(report, 90, "正在写入卸载程序…");
            string uninstallExe = Path.Combine(installDir, UninstallExeName);
            WriteStubExe(selfExe, trailer.PayloadOffset, uninstallExe);
            installedRelPaths.Add(UninstallExeName);
            WriteInstallManifest(installDir, installedRelPaths);

            string mainExe = Path.Combine(installDir, MainExeName);
            if (!File.Exists(mainExe))
                throw new InvalidOperationException("安装目录未找到主程序：" + mainExe);

            Report(report, 93, "正在创建快捷方式…");
            if (options.CreateDesktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktop, ShortcutFileName), mainExe, "", installDir, mainExe + ",0", "BoBo Remote · 低延迟远程桌面与高清串流");
            }
            if (options.CreateStartMenuShortcut)
            {
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDisplayName);
                Directory.CreateDirectory(startMenu);
                CreateShortcut(Path.Combine(startMenu, ShortcutFileName), mainExe, "", installDir, mainExe + ",0", "BoBo Remote · 低延迟远程桌面与高清串流");
                CreateShortcut(Path.Combine(startMenu, "卸载 BoBo Remote.lnk"), uninstallExe, "--uninstall", installDir, mainExe + ",0", "卸载 BoBo Remote");
            }

            Report(report, 96, "正在注册 Windows 应用与卸载信息…");
            RegisterUninstallEntry(installDir, mainExe, uninstallExe, version, totalExtractedBytes);
            if (options.PreserveAutoStart)
            {
                if (IsAutoStartEnabled()) SetAutoStart(mainExe, true);
            }
            else
            {
                SetAutoStart(mainExe, options.AutoStart);
            }
            Report(report, 98, "正在配置低延迟直连防火墙规则…");
            ConfigureWindowsFirewallRules(mainExe, true);

            Report(report, 100, "安装完成！BoBo Remote 已准备就绪。");
        }

        internal static void RunUninstall(string installDir, bool purgeUserData, bool scheduleSelfDelete, Action<int, string> report)
        {
            string reason;
            if (!IsSafeInstallDir(installDir, out reason))
                throw new InvalidOperationException(reason);

            string fullDir = Path.GetFullPath(installDir).TrimEnd('\\');
            Report(report, 10, "正在关闭运行中的 BoBo Remote 进程…");
            StopRunningProcessesInDir(fullDir);

            Report(report, 30, "正在移除桌面与开始菜单快捷方式…");
            TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName));
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDisplayName);
            TryDeleteDirectory(startMenu);

            Report(report, 50, "正在清理注册表项…");
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegSubKey, false); }
            catch { }
            SetAutoStart(null, false);
            ConfigureWindowsFirewallRules(Path.Combine(fullDir, MainExeName), false);

            if (purgeUserData)
            {
                Report(report, 65, "正在清理用户配置与本地宿主凭据…");
                TryDeleteDirectory(UserDataDir);
            }

            Report(report, 80, "正在删除程序文件…");
            string selfExe = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (Directory.Exists(fullDir))
            {
                DeleteOwnedBoBoFilesExcept(fullDir, selfExe);
                if (!scheduleSelfDelete)
                {
                    TryDeleteDirectoryIfEmpty(fullDir);
                }
                else
                {
                    ScheduleSelfExeAndEmptyDirRemoval(selfExe, fullDir);
                }
            }

            Report(report, 100, "BoBo Remote 已卸载完成。");
        }

        internal static void LaunchMainApp(string installDir, string arguments = null)
        {
            string mainExe = Path.Combine(installDir, MainExeName);
            if (!File.Exists(mainExe)) return;
            var psi = new ProcessStartInfo(mainExe, arguments ?? "")
            {
                WorkingDirectory = installDir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        private static void WriteStubExe(string selfExe, long stubLength, string targetPath)
        {
            string tmp = targetPath + ".tmp";
            using (var src = new FileStream(selfExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[64 * 1024];
                long remaining = stubLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int n = src.Read(buf, 0, toRead);
                    if (n <= 0) break;
                    dst.Write(buf, 0, n);
                    remaining -= n;
                }
            }
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tmp, targetPath);
        }

        private static void ConfigureWindowsFirewallRules(string exePath, bool add)
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    if (id == null) return;
                    var principal = new WindowsPrincipal(id);
                    if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return;
                }
                RunHiddenNetsh("advfirewall firewall delete rule name=\"BoBo Remote (UDP-In)\"");
                RunHiddenNetsh("advfirewall firewall delete rule name=\"BoBo Remote (TCP-In)\"");
                if (add && !string.IsNullOrWhiteSpace(exePath))
                {
                    string quoted = "\"" + exePath + "\"";
                    RunHiddenNetsh("advfirewall firewall add rule name=\"BoBo Remote (UDP-In)\" dir=in action=allow program=" + quoted + " enable=yes profile=any protocol=UDP localport=49811,49812");
                    RunHiddenNetsh("advfirewall firewall add rule name=\"BoBo Remote (TCP-In)\" dir=in action=allow program=" + quoted + " enable=yes profile=any protocol=TCP localport=49810");
                }
            }
            catch { }
        }

        private static void RunHiddenNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(2000);
                }
            }
            catch { }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDir, string iconLocation, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("系统缺少 WScript.Shell 组件，无法创建快捷方式。");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                try
                {
                    Type st = shortcut.GetType();
                    st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                    st.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? "" });
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                    st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconLocation });
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description ?? "" });
                    st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                }
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        private static void RegisterUninstallEntry(string installDir, string mainExe, string uninstallExe, string version, long totalBytes)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallRegSubKey))
            {
                if (key == null) return;
                key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", version, RegistryValueKind.String);
                key.SetValue("Publisher", "Gluxggg", RegistryValueKind.String);
                key.SetValue("DisplayIcon", mainExe + ",0", RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + uninstallExe + "\" --uninstall", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", "\"" + uninstallExe + "\" --uninstall --silent", RegistryValueKind.String);
                key.SetValue("URLInfoAbout", "https://github.com/Gluxggg/BoBo-Remote", RegistryValueKind.String);
                key.SetValue("EstimatedSize", (int)Math.Max(1024L, totalBytes / 1024L), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegSubKey, false))
                {
                    if (key == null) return false;
                    object cur = key.GetValue(RunValueName);
                    if (cur != null && !string.IsNullOrWhiteSpace(Convert.ToString(cur))) return true;
                    object legacy = key.GetValue(LegacyRunValueName);
                    return legacy != null && !string.IsNullOrWhiteSpace(Convert.ToString(legacy));
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WaitForProcessExit(int pid, int timeoutMs)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.CloseMainWindow(); }
                        catch { }
                        p.WaitForExit(2000);
                    }
                }
            }
            catch { }
        }

        private static void SetAutoStart(string mainExe, bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegSubKey, true))
                {
                    if (key == null) return;
                    key.DeleteValue(LegacyRunValueName, false);
                    if (enabled && !string.IsNullOrEmpty(mainExe))
                        key.SetValue(RunValueName, "\"" + mainExe + "\"", RegistryValueKind.String);
                    else
                        key.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        private static void StopRunningProcessesInDir(string installDir)
        {
            string normDir = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";
            int selfPid = Process.GetCurrentProcess().Id;
            foreach (string procName in new[] { "BoBo远程", "BoBoStreamPlayer", "Moonlight", "rustdesk", "sunshine" })
            {
                Process[] list;
                try { list = Process.GetProcessesByName(procName); }
                catch { continue; }
                foreach (Process p in list)
                {
                    try
                    {
                        if (p.Id == selfPid) continue;
                        string path = p.MainModule != null ? p.MainModule.FileName : null;
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!path.StartsWith(normDir, StringComparison.OrdinalIgnoreCase)) continue;
                        try { p.CloseMainWindow(); }
                        catch { }
                        if (!p.WaitForExit(1500))
                        {
                            try { p.Kill(); p.WaitForExit(2000); }
                            catch { }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        private static void RemoveLegacyExternalEngines(string installDir)
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return;
            try
            {
                string enginesDir = Path.Combine(installDir, "engines");
                if (Directory.Exists(enginesDir))
                {
                    Directory.Delete(enginesDir, true);
                }
            }
            catch { }
            foreach (string obsoleteFile in new[] { "BoBoStreamPlayer.exe", "opus.dll" })
            {
                try
                {
                    string path = Path.Combine(installDir, obsoleteFile);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }
        }

        private const string InstallManifestFileName = ".bobo-install-manifest.txt";

        internal static void WriteInstallManifest(string installDir, List<string> relativePaths)
        {
            try
            {
                string manifestPath = Path.Combine(installDir, InstallManifestFileName);
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lines = new List<string>();
                if (relativePaths != null)
                {
                    foreach (string raw in relativePaths)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        string clean = raw.Replace('/', '\\').TrimStart('\\');
                        if (unique.Add(clean)) lines.Add(clean);
                    }
                }
                File.WriteAllLines(manifestPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        internal static void DeleteOwnedBoBoFilesExcept(string dir, string keepFileFullPath)
        {
            string normRoot = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            string manifestPath = Path.Combine(dir, InstallManifestFileName);
            bool usedManifest = false;

            if (File.Exists(manifestPath))
            {
                try
                {
                    string[] relLines = File.ReadAllLines(manifestPath, Encoding.UTF8);
                    usedManifest = true;
                    foreach (string rel in relLines)
                    {
                        string dest;
                        if (!TryResolveSafeExtractPath(dir, rel.Trim(), out dest)) continue;
                        if (string.Equals(Path.GetFullPath(dest), keepFileFullPath, StringComparison.OrdinalIgnoreCase)) continue;
                        TryDeleteFile(dest);
                    }
                    TryDeleteFile(manifestPath);
                }
                catch { usedManifest = false; }
            }

            if (!usedManifest)
            {
                // Legacy fallback (pre-manifest installs): remove ONLY known BoBo-owned files & subdirs, never user data!
                string[] knownFiles = new[]
                {
                    MainExeName, UninstallExeName, "BoBoStreamPlayer.exe", "opus.dll",
                    "README.md", "README.en.md", "LICENSE", "NOTICE", "NOTICE.md",
                    "THIRD-PARTY.md", "低延迟调优与防窥指南.md"
                };
                foreach (string name in knownFiles)
                {
                    string p = Path.Combine(dir, name);
                    if (string.Equals(Path.GetFullPath(p), keepFileFullPath, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteFile(p);
                }
                string enginesSub = Path.Combine(dir, "engines");
                if (Directory.Exists(enginesSub)) TryDeleteDirectory(enginesSub);
            }

            // Clean up any empty subdirectories created under installDir
            try
            {
                string[] subDirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
                Array.Sort(subDirs, delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
                foreach (string sub in subDirs)
                {
                    if (!Path.GetFullPath(sub).StartsWith(normRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteDirectoryIfEmpty(sub);
                }
            }
            catch { }
        }

        internal static string BuildSafeSelfCleanupEncodedCommand(string selfExe, string fullDir)
        {
            // Base64 UTF-16LE PowerShell -EncodedCommand eliminates ALL cmd.exe metacharacter (&, %, ^, ") injection risks
            // and removes ONLY selfExe + empty installDir (never recursively wiping unrelated sibling user files).
            string escExe = (selfExe ?? "").Replace("'", "''");
            string escDir = (fullDir ?? "").Replace("'", "''");
            string script =
                "Start-Sleep -Seconds 2; " +
                "$e = '" + escExe + "'; " +
                "$d = '" + escDir + "'; " +
                "for ($i=0; $i -lt 10; $i++) { " +
                "  if (Test-Path -LiteralPath $e) { try { Remove-Item -LiteralPath $e -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 300; continue } } " +
                "  break " +
                "}; " +
                "if (Test-Path -LiteralPath $d) { " +
                "  $rem = @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue); " +
                "  if ($rem.Count -eq 0) { try { [System.IO.Directory]::Delete($d, $false) } catch {} } " +
                "}";
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        private static void ScheduleSelfExeAndEmptyDirRemoval(string selfExe, string fullDir)
        {
            try
            {
                string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                string encoded = BuildSafeSelfCleanupEncodedCommand(selfExe, fullDir);
                var psi = new ProcessStartInfo(ps, "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static void TryDeleteDirectoryIfEmpty(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                if (Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path, false);
                }
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }

        private static void Report(Action<int, string> report, int pct, string msg)
        {
            if (report != null) report(pct, msg);
        }
    }

    internal static class SetupPalette
    {
        internal static readonly Color Chrome = Color.FromArgb(240, 245, 248);       // #F0F5F8
        internal static readonly Color Workspace = Color.FromArgb(247, 251, 254);    // #F7FBFE
        internal static readonly Color Card = Color.White;
        internal static readonly Color Border = Color.FromArgb(218, 229, 240);       // #DAE5F0
        internal static readonly Color Ink = Color.FromArgb(22, 34, 52);
        internal static readonly Color Muted = Color.FromArgb(95, 113, 136);
        internal static readonly Color Accent = Color.FromArgb(51, 122, 255);        // #337AFF
        internal static readonly Color AccentHover = Color.FromArgb(35, 104, 240);
        internal static readonly Color Success = Color.FromArgb(18, 155, 96);
        internal static readonly Color SoftBlue = Color.FromArgb(234, 243, 255);
    }

    internal sealed class RoundedButton : Control
    {
        private bool hovered;
        private bool primary;

        internal RoundedButton(string text, bool isPrimary)
        {
            Text = text;
            primary = isPrimary;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = new Font("Microsoft YaHei UI", isPrimary ? 10.5f : 9f, isPrimary ? FontStyle.Bold : FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : SetupPalette.Workspace);

            Rectangle rect = new Rectangle(1, 1, Math.Max(4, Width - 3), Math.Max(4, Height - 3));
            float scale = Math.Max(1f, g.DpiX / 96f);
            using (GraphicsPath path = RoundedRect(rect, Math.Max(2, (int)Math.Round(8f * scale))))
            {
                Color fill = !Enabled
                    ? Color.FromArgb(214, 224, 236)
                    : primary
                        ? (hovered ? SetupPalette.AccentHover : SetupPalette.Accent)
                        : (hovered ? Color.FromArgb(236, 243, 251) : Color.White);

                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                if (!primary)
                {
                    using (var pen = new Pen(SetupPalette.Border, 1f)) g.DrawPath(pen, path);
                }
            }

            Color fg = !Enabled ? SetupPalette.Muted : (primary ? Color.White : SetupPalette.Ink);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        internal static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int maxR = Math.Min(r.Width, r.Height) / 2;
            int rad = Math.Min(radius, maxR);
            if (rad <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SmoothProgressBar : Control
    {
        private int value;
        internal int Value
        {
            get { return value; }
            set
            {
                int clamped = Math.Max(0, Math.Min(100, value));
                if (this.value != clamped)
                {
                    this.value = clamped;
                    Invalidate();
                }
            }
        }

        internal SmoothProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : SetupPalette.Card);

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath trackPath = RoundedButton.RoundedRect(track, Height / 2))
            using (var trackBrush = new SolidBrush(Color.FromArgb(228, 237, 247)))
            {
                g.FillPath(trackBrush, trackPath);
            }

            if (value > 0)
            {
                int fillW = Math.Max(Height, (int)Math.Round((Width - 1) * (value / 100.0)));
                Rectangle fill = new Rectangle(0, 0, Math.Min(Width - 1, fillW), Height - 1);
                using (GraphicsPath fillPath = RoundedButton.RoundedRect(fill, Height / 2))
                using (var fillBrush = new LinearGradientBrush(fill, Color.FromArgb(51, 122, 255), Color.FromArgb(38, 152, 255), 0f))
                {
                    g.FillPath(fillBrush, fillPath);
                }
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private static int S(int px, float scale)
        {
            return (int)Math.Round(px * scale);
        }

        private readonly string selfExe;
        private readonly PayloadTrailer trailer;
        private readonly InstallOptions options;
        private readonly Bitmap logoBitmap;
        private readonly string appVersion;

        private Panel welcomePage;
        private Panel progressPage;
        private Panel donePage;

        private TextBox pathBox;
        private CheckBox desktopChk;
        private CheckBox startMenuChk;
        private CheckBox launchChk;
        private CheckBox autoStartChk;
        private RoundedButton installBtn;

        private SmoothProgressBar progressBar;
        private Label progressTitle;
        private Label progressDetail;
        private Label progressPercent;
        private Label doneSummary;

        private bool installing;

        internal SetupForm(string selfExe, PayloadTrailer trailer, InstallOptions options)
        {
            this.selfExe = selfExe;
            this.trailer = trailer;
            this.options = options;
            this.logoBitmap = InstallerEngine.LoadBrandLogo();
            this.appVersion = InstallerEngine.GetAppVersion();

            SuspendLayout();
            Text = "BoBo Remote 安装向导";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 424);
            BackColor = SetupPalette.Workspace;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(selfExe); }
            catch { }

            BuildLayout();
            ShowPanel(welcomePage);

            AutoScaleDimensions = new SizeF(96f, 96f);
            ResumeLayout(false);
            PerformAutoScale();
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; // Win11 DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && logoBitmap != null) logoBitmap.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(SetupPalette.Border, 1f))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }

        private void BuildLayout()
        {
            // Top glacier ice-blue bar
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = SetupPalette.Chrome
            };
            topBar.MouseDown += DragWindow;
            topBar.Paint += delegate(object s, PaintEventArgs e)
            {
                float sc = Math.Max(1f, e.Graphics.DpiX / 96f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (logoBitmap != null)
                {
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int iconSz = S(24, sc);
                    e.Graphics.DrawImage(logoBitmap, new Rectangle(S(16, sc), (topBar.Height - iconSz) / 2, iconSz, iconSz));
                }
                using (var titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, "BoBo Remote 安装向导", titleFont,
                        new Rectangle(S(48, sc), 0, Math.Max(120, topBar.Width - S(150, sc)), topBar.Height),
                        SetupPalette.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                using (var pen = new Pen(SetupPalette.Border, 1f))
                    e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            var closeBtn = new Label
            {
                Text = "✕",
                Size = new Size(44, 45),
                Location = new Point(ClientSize.Width - 45, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                ForeColor = SetupPalette.Muted,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent
            };
            closeBtn.MouseEnter += delegate { closeBtn.BackColor = Color.FromArgb(232, 17, 35); closeBtn.ForeColor = Color.White; };
            closeBtn.MouseLeave += delegate { closeBtn.BackColor = Color.Transparent; closeBtn.ForeColor = SetupPalette.Muted; };
            closeBtn.Click += delegate
            {
                if (installing) return;
                Close();
            };

            var minBtn = new Label
            {
                Text = "—",
                Size = new Size(44, 45),
                Location = new Point(ClientSize.Width - 89, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = SetupPalette.Muted,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent
            };
            minBtn.MouseEnter += delegate { minBtn.BackColor = Color.FromArgb(224, 234, 244); };
            minBtn.MouseLeave += delegate { minBtn.BackColor = Color.Transparent; };
            minBtn.Click += delegate { WindowState = FormWindowState.Minimized; };

            topBar.Controls.Add(minBtn);
            topBar.Controls.Add(closeBtn);
            Controls.Add(topBar);

            welcomePage = BuildWelcomePage();
            progressPage = BuildProgressPage();
            donePage = BuildDonePage();

            foreach (Panel p in new[] { welcomePage, progressPage, donePage })
            {
                p.Bounds = new Rectangle(1, 46, ClientSize.Width - 2, ClientSize.Height - 47);
                p.BackColor = SetupPalette.Workspace;
                Controls.Add(p);
            }
        }

        private Panel BuildWelcomePage()
        {
            var page = new Panel();

            var hero = new Panel
            {
                Bounds = new Rectangle(24, 16, 510, 76),
                BackColor = Color.Transparent
            };
            hero.Paint += delegate(object s, PaintEventArgs e)
            {
                float sc = Math.Max(1f, e.Graphics.DpiX / 96f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (logoBitmap != null)
                {
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(logoBitmap, new Rectangle(S(4, sc), S(8, sc), S(58, sc), S(58, sc)));
                }
                using (var brandFont = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold))
                using (var pillFont = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold))
                using (var subFont = new Font("Microsoft YaHei UI", 9f))
                {
                    string brandText = "BoBo Remote";
                    int titleX = S(76, sc);
                    int titleY = S(8, sc);
                    int titleH = S(32, sc);
                    Size brandSz = TextRenderer.MeasureText(e.Graphics, brandText, brandFont, new Size(int.MaxValue, titleH),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(e.Graphics, brandText, brandFont,
                        new Rectangle(titleX, titleY, brandSz.Width + S(8, sc), titleH), SetupPalette.Ink,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                    // Version pill placed dynamically after title width
                    string pillText = "v" + appVersion + " · x64";
                    Size pillSz = TextRenderer.MeasureText(e.Graphics, pillText, pillFont, new Size(int.MaxValue, S(22, sc)),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    int pillH = S(22, sc);
                    int pillW = Math.Max(S(96, sc), pillSz.Width + S(20, sc));
                    Rectangle pill = new Rectangle(titleX + brandSz.Width + S(12, sc), titleY + (titleH - pillH) / 2, pillW, pillH);
                    using (GraphicsPath pp = RoundedButton.RoundedRect(pill, pillH / 2))
                    using (var pb = new SolidBrush(SetupPalette.SoftBlue))
                        e.Graphics.FillPath(pb, pp);
                    TextRenderer.DrawText(e.Graphics, pillText, pillFont,
                        pill, SetupPalette.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

                    TextRenderer.DrawText(e.Graphics, "双端低延迟远程桌面与高清串流 · 一键安装、免账号直连",
                        subFont, new Rectangle(titleX, S(42, sc), Math.Max(120, hero.Width - titleX - S(4, sc)), S(26, sc)),
                        SetupPalette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            };
            page.Controls.Add(hero);

            // White card for path + checkboxes
            var card = new Panel
            {
                Bounds = new Rectangle(24, 102, 510, 178),
                BackColor = SetupPalette.Card
            };
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                float sc = Math.Max(1f, e.Graphics.DpiX / 96f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedButton.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), S(10, sc)))
                using (var pen = new Pen(SetupPalette.Border, 1f))
                    e.Graphics.DrawPath(pen, path);
            };

            var dirLabel = new Label
            {
                Text = "安装目录",
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = SetupPalette.Ink,
                Location = new Point(18, 16),
                AutoSize = true
            };
            pathBox = new TextBox
            {
                Text = options.InstallDir,
                Font = new Font("Microsoft YaHei UI", 9f),
                Location = new Point(18, 40),
                Size = new Size(374, 28),
                BorderStyle = BorderStyle.FixedSingle
            };
            var browseBtn = new RoundedButton("更改目录…", false)
            {
                Location = new Point(402, 37),
                Size = new Size(90, 30)
            };
            browseBtn.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择 BoBo Remote 的安装目录";
                    dlg.SelectedPath = Directory.Exists(pathBox.Text) ? pathBox.Text : InstallerEngine.DefaultInstallDir;
                    if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    {
                        string chosen = dlg.SelectedPath.TrimEnd('\\');
                        if (!chosen.EndsWith("BoBoRemote", StringComparison.OrdinalIgnoreCase))
                            chosen = Path.Combine(chosen, "BoBoRemote");
                        pathBox.Text = chosen;
                    }
                }
            };

            desktopChk = MakeCheck("创建桌面快捷方式（BoBo Remote）", options.CreateDesktopShortcut, 18, 82);
            startMenuChk = MakeCheck("创建开始菜单快捷方式", options.CreateStartMenuShortcut, 266, 82);
            launchChk = MakeCheck("安装完成后立即启动 BoBo Remote", options.LaunchAfterInstall, 18, 112);
            autoStartChk = MakeCheck("开机自动启动（需软件内手动授权才会允许连入）", options.AutoStart, 18, 142);

            card.Controls.Add(dirLabel);
            card.Controls.Add(pathBox);
            card.Controls.Add(browseBtn);
            card.Controls.Add(desktopChk);
            card.Controls.Add(startMenuChk);
            card.Controls.Add(launchChk);
            card.Controls.Add(autoStartChk);
            page.Controls.Add(card);

            installBtn = new RoundedButton("立即安装", true)
            {
                Bounds = new Rectangle(170, 298, 218, 42)
            };
            installBtn.Click += delegate { BeginInstall(); };
            page.Controls.Add(installBtn);

            var foot = new Label
            {
                Text = "MIT 开源许可 · V6 全内置零外挂原生内核（DXGI + D3D11VA + TLS 1.2 + UDP+FEC）· 随时可在系统应用列表卸载",
                Font = new Font("Microsoft YaHei UI", 7.8f),
                ForeColor = SetupPalette.Muted,
                Bounds = new Rectangle(24, 348, 510, 22),
                TextAlign = ContentAlignment.MiddleCenter
            };
            page.Controls.Add(foot);

            return page;
        }

        private Panel BuildProgressPage()
        {
            var page = new Panel();

            var card = new Panel
            {
                Bounds = new Rectangle(32, 64, 494, 226),
                BackColor = SetupPalette.Card
            };
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                float sc = Math.Max(1f, e.Graphics.DpiX / 96f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedButton.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), S(12, sc)))
                using (var pen = new Pen(SetupPalette.Border, 1f))
                    e.Graphics.DrawPath(pen, path);
                if (logoBitmap != null)
                {
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int iconSz = S(56, sc);
                    e.Graphics.DrawImage(logoBitmap, new Rectangle((card.Width - iconSz) / 2, S(22, sc), iconSz, iconSz));
                }
            };

            progressTitle = new Label
            {
                Text = "正在安装 BoBo Remote…",
                Font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold),
                ForeColor = SetupPalette.Ink,
                Bounds = new Rectangle(20, 90, 454, 28),
                TextAlign = ContentAlignment.MiddleCenter
            };
            progressBar = new SmoothProgressBar
            {
                Bounds = new Rectangle(36, 134, 422, 12)
            };
            progressPercent = new Label
            {
                Text = "0%",
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                ForeColor = SetupPalette.Accent,
                Bounds = new Rectangle(36, 154, 422, 22),
                TextAlign = ContentAlignment.MiddleRight
            };
            progressDetail = new Label
            {
                Text = "正在准备安装环境…",
                Font = new Font("Microsoft YaHei UI", 8.8f),
                ForeColor = SetupPalette.Muted,
                Bounds = new Rectangle(36, 154, 355, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };

            card.Controls.Add(progressTitle);
            card.Controls.Add(progressBar);
            card.Controls.Add(progressDetail);
            card.Controls.Add(progressPercent);
            page.Controls.Add(card);

            return page;
        }

        private Panel BuildDonePage()
        {
            var page = new Panel();

            var card = new Panel
            {
                Bounds = new Rectangle(32, 40, 494, 230),
                BackColor = SetupPalette.Card
            };
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                float sc = Math.Max(1f, e.Graphics.DpiX / 96f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedButton.RoundedRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), S(12, sc)))
                using (var pen = new Pen(SetupPalette.Border, 1f))
                    e.Graphics.DrawPath(pen, path);

                // Clean green checkmark circle (centered horizontally at any DPI)
                int d = S(60, sc);
                int cx = (card.Width - d) / 2;
                int cy = S(22, sc);
                using (var br = new SolidBrush(Color.FromArgb(232, 249, 240)))
                    e.Graphics.FillEllipse(br, cx, cy, d, d);
                using (var pen = new Pen(SetupPalette.Success, Math.Max(2.5f, 4f * sc)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(cx + S(17, sc), cy + S(31, sc)),
                        new Point(cx + S(27, sc), cy + S(41, sc)),
                        new Point(cx + S(45, sc), cy + S(21, sc))
                    });
                }
            };

            var doneTitle = new Label
            {
                Text = "安装完成！BoBo Remote 已就绪",
                Font = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold),
                ForeColor = SetupPalette.Ink,
                Bounds = new Rectangle(20, 96, 454, 30),
                TextAlign = ContentAlignment.MiddleCenter
            };
            doneSummary = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = SetupPalette.Muted,
                Bounds = new Rectangle(28, 134, 438, 76),
                TextAlign = ContentAlignment.TopCenter
            };

            card.Controls.Add(doneTitle);
            card.Controls.Add(doneSummary);
            page.Controls.Add(card);

            var launchNowBtn = new RoundedButton("立即体验 BoBo Remote", true)
            {
                Bounds = new Rectangle(116, 294, 210, 42)
            };
            launchNowBtn.Click += delegate
            {
                try { InstallerEngine.LaunchMainApp(options.InstallDir); }
                catch (Exception ex) { MessageBox.Show(this, "启动失败：" + ex.Message, "BoBo Remote", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                Close();
            };

            var finishBtn = new RoundedButton("完成", false)
            {
                Bounds = new Rectangle(340, 294, 102, 42)
            };
            finishBtn.Click += delegate { Close(); };

            page.Controls.Add(launchNowBtn);
            page.Controls.Add(finishBtn);
            return page;
        }

        private static CheckBox MakeCheck(string text, bool checkedState, int x, int y)
        {
            return new CheckBox
            {
                Text = text,
                Checked = checkedState,
                Font = new Font("Microsoft YaHei UI", 8.8f),
                ForeColor = SetupPalette.Ink,
                Location = new Point(x, y),
                AutoSize = true,
                Cursor = Cursors.Hand
            };
        }

        private void ShowPanel(Panel target)
        {
            welcomePage.Visible = (target == welcomePage);
            progressPage.Visible = (target == progressPage);
            donePage.Visible = (target == donePage);
        }

        private void BeginInstall()
        {
            options.InstallDir = pathBox.Text.Trim();
            options.CreateDesktopShortcut = desktopChk.Checked;
            options.CreateStartMenuShortcut = startMenuChk.Checked;
            options.LaunchAfterInstall = launchChk.Checked;
            options.AutoStart = autoStartChk.Checked;

            string reason;
            if (!InstallerEngine.IsSafeInstallDir(options.InstallDir, out reason))
            {
                MessageBox.Show(this, reason, "目录校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            installing = true;
            ShowPanel(progressPage);
            progressBar.Value = 2;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallerEngine.RunInstall(selfExe, trailer, options, delegate(int pct, string msg)
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke((MethodInvoker)delegate
                        {
                            progressBar.Value = pct;
                            progressPercent.Text = pct + "%";
                            progressDetail.Text = msg;
                        });
                    });

                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        installing = false;
                        doneSummary.Text =
                            "安装路径：" + options.InstallDir + "\r\n" +
                            (options.CreateDesktopShortcut ? "已自动创建桌面快捷方式「BoBo Remote」\r\n" : "") +
                            "首次打开软件时会自动弹出 7 步灰白框选新手引导，带你快速上手。";
                        ShowPanel(donePage);
                        if (options.LaunchAfterInstall)
                        {
                            try { InstallerEngine.LaunchMainApp(options.InstallDir); }
                            catch { }
                        }
                    });
                }
                catch (Exception ex)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        installing = false;
                        MessageBox.Show(this, "安装未能完成：\r\n" + ex.Message, "BoBo Remote 安装向导", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        ShowPanel(welcomePage);
                    });
                }
            });
        }
    }

    internal sealed class UninstallForm : Form
    {
        private readonly string installDir;
        private CheckBox purgeChk;
        private Label statusLabel;
        private RoundedButton uninstallBtn;
        private RoundedButton cancelBtn;

        internal UninstallForm(string installDir)
        {
            this.installDir = installDir;
            SuspendLayout();
            Text = "卸载 BoBo Remote";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 224);
            BackColor = SetupPalette.Workspace;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
            catch { }

            var title = new Label
            {
                Text = "确定要卸载 BoBo Remote 吗？",
                Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold),
                ForeColor = SetupPalette.Ink,
                Location = new Point(22, 20),
                AutoSize = true
            };
            statusLabel = new Label
            {
                Text = "将移除程序文件、桌面与开始菜单快捷方式及应用卸载项：\r\n" + installDir,
                Font = new Font("Microsoft YaHei UI", 8.8f),
                ForeColor = SetupPalette.Muted,
                Bounds = new Rectangle(22, 54, 396, 48)
            };
            purgeChk = new CheckBox
            {
                Text = "同时删除用户偏好与本地宿主配对缓存（%LOCALAPPDATA%\\双端远程）",
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = SetupPalette.Ink,
                Location = new Point(22, 112),
                AutoSize = true,
                Checked = false
            };

            uninstallBtn = new RoundedButton("确认卸载", true)
            {
                Bounds = new Rectangle(196, 158, 110, 36)
            };
            cancelBtn = new RoundedButton("取消", false)
            {
                Bounds = new Rectangle(318, 158, 96, 36)
            };
            cancelBtn.Click += delegate { Close(); };
            uninstallBtn.Click += delegate { DoUninstall(); };

            Controls.Add(title);
            Controls.Add(statusLabel);
            Controls.Add(purgeChk);
            Controls.Add(uninstallBtn);
            Controls.Add(cancelBtn);

            AutoScaleDimensions = new SizeF(96f, 96f);
            ResumeLayout(false);
            PerformAutoScale();
            PerformLayout();
        }

        private void DoUninstall()
        {
            uninstallBtn.Enabled = false;
            cancelBtn.Enabled = false;
            bool purge = purgeChk.Checked;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallerEngine.RunUninstall(installDir, purge, true, delegate(int pct, string msg)
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke((MethodInvoker)delegate { statusLabel.Text = msg; });
                    });
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show(this, "BoBo Remote 已成功卸载。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Close();
                    });
                }
                catch (Exception ex)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        MessageBox.Show(this, "卸载过程出错：" + ex.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        uninstallBtn.Enabled = true;
                        cancelBtn.Enabled = true;
                    });
                }
            });
        }
    }

    internal static class SetupSelfTest
    {
        private static int passed;
        private static int failed;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                passed++;
                Console.WriteLine("[通过] " + name);
            }
            else
            {
                failed++;
                Console.WriteLine("[失败] " + name + " (" + detail + ")");
            }
        }

        internal static int Run()
        {
            passed = 0;
            failed = 0;

            // 1. Trailer format & parse round-trip + tamper detection
            byte[] fakeStub = new byte[2048];
            byte[] fakeZip = Encoding.UTF8.GetBytes("PK-fake-payload-content-for-trailer-test");
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(fakeZip);
            byte[] trailerBytes = PayloadArchive.FormatTrailer(fakeStub.Length, fakeZip.Length, hash, 1);
            long totalLen = fakeStub.Length + fakeZip.Length + trailerBytes.Length;

            PayloadTrailer parsed;
            Check("安装包 Trailer 编码与解析",
                PayloadArchive.TryParseTrailer(trailerBytes, totalLen, out parsed)
                && parsed.PayloadOffset == fakeStub.Length
                && parsed.PayloadLength == fakeZip.Length,
                "parse");

            byte[] badTrailer = (byte[])trailerBytes.Clone();
            badTrailer[0] = (byte)'X';
            PayloadTrailer badParsed;
            Check("安装包 Trailer 魔数损坏拒绝",
                !PayloadArchive.TryParseTrailer(badTrailer, totalLen, out badParsed),
                "bad-magic");

            // 2. Zip-Slip protection & prefix normalization
            string norm = InstallerEngine.NormalizeZipEntryRelativePath("BoBo远程/engines/RustDesk/rustdesk.exe");
            Check("ZIP 根目录前缀剥离",
                string.Equals(norm, @"engines\RustDesk\rustdesk.exe", StringComparison.Ordinal),
                norm);

            string safePath;
            Check("Zip-Slip 路径穿越拦截",
                !InstallerEngine.TryResolveSafeExtractPath(@"C:\Users\Demo\AppData\Local\BoBoRemote", @"..\..\evil.exe", out safePath)
                && InstallerEngine.TryResolveSafeExtractPath(@"C:\Users\Demo\AppData\Local\BoBoRemote", norm, out safePath),
                safePath ?? "null");

            // 3. Install dir safety guard
            string why;
            Check("安装目录安全防误删校验",
                !InstallerEngine.IsSafeInstallDir(@"C:\", out why)
                && !InstallerEngine.IsSafeInstallDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out why)
                && InstallerEngine.IsSafeInstallDir(InstallerEngine.DefaultInstallDir, out why),
                why ?? "ok");

            // 4. Embedded brand logo
            using (Bitmap logo = InstallerEngine.LoadBrandLogo())
            {
                Check("安装向导内嵌圆角方框奶牛猫 Logo 可加载",
                    logo != null && logo.Width >= 32 && logo.Height >= 32,
                    logo == null ? "null" : (logo.Width + "x" + logo.Height));
            }

            // 5. Manifest-based uninstall safety (preserves non-BoBo sibling user files & safe encoded self-delete)
            string testDir = Path.Combine(Path.GetTempPath(), "BoBoSetup-SafeUninstall-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(testDir);
                string boboFile = Path.Combine(testDir, InstallerEngine.MainExeName);
                string userFile = Path.Combine(testDir, "user-important-notes.txt");
                File.WriteAllText(boboFile, "bobo");
                File.WriteAllText(userFile, "keep-me");
                InstallerEngine.WriteInstallManifest(testDir, new List<string> { InstallerEngine.MainExeName });
                InstallerEngine.DeleteOwnedBoBoFilesExcept(testDir, Path.Combine(testDir, InstallerEngine.UninstallExeName));
                string encoded = InstallerEngine.BuildSafeSelfCleanupEncodedCommand(Path.Combine(testDir, "a & b.exe"), testDir);
                Check("卸载清单安全校验（仅删 BoBo 自身文件、绝不误删同目录用户文件）",
                    !File.Exists(boboFile) && File.Exists(userFile) && !string.IsNullOrEmpty(encoded),
                    "boboDeleted=" + (!File.Exists(boboFile)) + " userKept=" + File.Exists(userFile));
            }
            finally
            {
                try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
            }

            Console.WriteLine("BoBoSetup self-test: 通过 " + passed + "，失败 " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
