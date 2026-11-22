using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DualRemote
{
    /// <summary>
    /// 本地记忆：只保存最近的目标设备 ID 与主机地址，不含密码、PIN、令牌或服务器账号。
    /// 位置：%LOCALAPPDATA%\双端远程\recent.txt
    /// </summary>
    internal sealed class RecentStore
    {
        internal const int MaxItems = 5;
        internal readonly List<string> RustDeskIds = new List<string>();
        internal readonly List<string> MoonlightHosts = new List<string>();

        internal static string DirPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "双端远程");
            }
        }

        internal static string FilePath
        {
            get { return Path.Combine(DirPath, "recent.txt"); }
        }

        internal static RecentStore Load()
        {
            RecentStore store = new RecentStore();
            try
            {
                if (File.Exists(FilePath))
                {
                    Parse(File.ReadAllText(FilePath, Encoding.UTF8), store);
                }
            }
            catch
            {
                // 记录损坏时静默从空列表开始，不影响连接功能。
            }
            return store;
        }

        internal void Save()
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePath, Serialize(this), new UTF8Encoding(false));
        }

        internal void RememberRustDeskId(string id)
        {
            AddFirst(RustDeskIds, id);
        }

        internal void RememberMoonlightHost(string host)
        {
            AddFirst(MoonlightHosts, host);
        }

        private static void AddFirst(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            list.RemoveAll(delegate(string item)
            {
                return string.Equals(item, value, StringComparison.OrdinalIgnoreCase);
            });
            list.Insert(0, value);
            if (list.Count > MaxItems)
            {
                list.RemoveRange(MaxItems, list.Count - MaxItems);
            }
        }

        internal static string Serialize(RecentStore store)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string id in store.RustDeskIds)
            {
                builder.AppendLine("rustdesk=" + id);
            }
            foreach (string host in store.MoonlightHosts)
            {
                builder.AppendLine("moonlight=" + host);
            }
            return builder.ToString();
        }

        internal static void Parse(string text, RecentStore store)
        {
            if (text == null) return;
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // 从后往前读，配合 AddFirst 可还原“最新的在最前”。
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (value.Length == 0 || value.Length > 253) continue;
                if (key == "rustdesk")
                {
                    AddFirst(store.RustDeskIds, value);
                }
                else if (key == "moonlight")
                {
                    AddFirst(store.MoonlightHosts, value);
                }
            }
        }
    }
}
