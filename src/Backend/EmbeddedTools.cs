using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace DualRemote
{
    /// <summary>
    /// 内嵌核心调优与显示脚本管理：将 Set-SunshineDisplay.ps1 与 Optimize-SunshineStream.ps1
    /// 直接编译进 BoBo远程.exe 资源段。优先使用同目录 tools\（便于开发调试），缺失时自动释放到
    /// %LOCALAPPDATA%\双端远程\runtime-tools\ 并做 SHA-256 一致性校验，确保主程序独立完整运行。
    /// </summary>
    internal static class EmbeddedTools
    {
        internal const string DisplayScriptName = "Set-SunshineDisplay.ps1";
        internal const string OptimizeScriptName = "Optimize-SunshineStream.ps1";
        internal const string DisplayResourceName = "DualRemote.tools.Set-SunshineDisplay.ps1";
        internal const string OptimizeResourceName = "DualRemote.tools.Optimize-SunshineStream.ps1";

        internal static string ResolveDisplayTool()
        {
            return ResolveScript(DisplayScriptName, DisplayResourceName);
        }

        internal static string ResolveOptimizeTool()
        {
            return ResolveScript(OptimizeScriptName, OptimizeResourceName);
        }

        internal static bool HasEmbeddedResource(string resourceName)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    return s != null && s.Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static string ResolveScript(string fileName, string resourceName)
        {
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", fileName);
            if (File.Exists(localPath))
                return localPath;

            try
            {
                byte[] embeddedBytes;
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (s == null || s.Length == 0)
                        return localPath;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        embeddedBytes = ms.ToArray();
                    }
                }

                string cacheDir = Path.Combine(RecentStore.DirPath, "runtime-tools");
                Directory.CreateDirectory(cacheDir);
                string targetPath = Path.Combine(cacheDir, fileName);

                if (File.Exists(targetPath) && BytesMatchSha256(targetPath, embeddedBytes))
                    return targetPath;

                string tmpPath = targetPath + ".tmp";
                File.WriteAllBytes(tmpPath, embeddedBytes);
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(tmpPath, targetPath);
                return targetPath;
            }
            catch
            {
                return localPath;
            }
        }

        private static bool BytesMatchSha256(string filePath, byte[] expectedBytes)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length != expectedBytes.Length)
                    return false;
                byte[] existing = File.ReadAllBytes(filePath);
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] h1 = sha.ComputeHash(existing);
                    byte[] h2 = sha.ComputeHash(expectedBytes);
                    if (h1.Length != h2.Length) return false;
                    for (int i = 0; i < h1.Length; i++)
                    {
                        if (h1[i] != h2[i]) return false;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
