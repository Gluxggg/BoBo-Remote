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

    internal sealed class ManagedDeviceItem
    {
        internal string Address = "";
        internal string Alias = "";
        internal bool IsMobile;
    }

    /// <summary>
    /// 本地设备目录管理：持久化手动添加设备、隐藏/移除的冗余设备以及自定义备注名。
    /// 位置：%LOCALAPPDATA%\双端远程\devices.txt
    /// </summary>
    internal sealed class DeviceCatalogStore
    {
        internal readonly List<ManagedDeviceItem> ManualDevices = new List<ManagedDeviceItem>();
        internal readonly HashSet<string> HiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string FilePath
        {
            get { return Path.Combine(RecentStore.DirPath, "devices.txt"); }
        }

        internal static string NormalizePeerKey(string hostOrName)
        {
            if (string.IsNullOrWhiteSpace(hostOrName)) return "";
            string s = hostOrName.Trim().ToLowerInvariant();
            System.Net.IPAddress ip;
            if (System.Net.IPAddress.TryParse(s, out ip)) return s;
            int dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            if (s.StartsWith("bobo-", StringComparison.Ordinal)) s = s.Substring(5);
            return s.Trim();
        }

        internal static DeviceCatalogStore Load()
        {
            var store = new DeviceCatalogStore();
            try
            {
                if (File.Exists(FilePath))
                {
                    Parse(File.ReadAllText(FilePath, Encoding.UTF8), store);
                }
            }
            catch { }
            return store;
        }

        internal void Save()
        {
            Directory.CreateDirectory(RecentStore.DirPath);
            File.WriteAllText(FilePath, Serialize(this), new UTF8Encoding(false));
        }

        internal bool IsHidden(string address, string hostName)
        {
            string addr = (address ?? "").Trim();
            if (addr.Length > 0 && HiddenKeys.Contains(addr)) return true;
            string hostKey = NormalizePeerKey(hostName);
            if (hostKey.Length > 0 && HiddenKeys.Contains("host:" + hostKey)) return true;
            return false;
        }

        internal void HideDevice(string address, string hostName)
        {
            string addr = (address ?? "").Trim();
            if (addr.Length > 0)
            {
                ManualDevices.RemoveAll(delegate(ManagedDeviceItem m)
                {
                    return string.Equals(m.Address, addr, StringComparison.OrdinalIgnoreCase);
                });
                HiddenKeys.Add(addr);
            }
            string hostKey = NormalizePeerKey(hostName);
            if (hostKey.Length > 0)
            {
                HiddenKeys.Add("host:" + hostKey);
            }
        }

        internal void UnhideAll()
        {
            HiddenKeys.Clear();
        }

        internal void AddOrUpdateManual(string address, string alias, bool isMobile)
        {
            string addr = (address ?? "").Trim();
            if (addr.Length == 0) return;
            string cleanAlias = (alias ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

            HiddenKeys.Remove(addr);
            string aliasKey = NormalizePeerKey(cleanAlias);
            if (aliasKey.Length > 0) HiddenKeys.Remove("host:" + aliasKey);
            string addrKey = NormalizePeerKey(addr);
            if (addrKey.Length > 0) HiddenKeys.Remove("host:" + addrKey);

            ManagedDeviceItem existing = null;
            for (int i = 0; i < ManualDevices.Count; i++)
            {
                if (string.Equals(ManualDevices[i].Address, addr, StringComparison.OrdinalIgnoreCase))
                {
                    existing = ManualDevices[i];
                    break;
                }
            }
            if (existing != null)
            {
                existing.Alias = cleanAlias;
                existing.IsMobile = isMobile;
            }
            else
            {
                ManualDevices.Insert(0, new ManagedDeviceItem
                {
                    Address = addr,
                    Alias = cleanAlias,
                    IsMobile = isMobile
                });
            }
            if (cleanAlias.Length > 0)
            {
                Aliases[addr] = cleanAlias;
            }
        }

        internal void SetAlias(string address, string hostName, string alias)
        {
            string addr = (address ?? "").Trim();
            string cleanAlias = (alias ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            string hostKey = NormalizePeerKey(hostName);

            if (cleanAlias.Length == 0)
            {
                if (addr.Length > 0) Aliases.Remove(addr);
                if (hostKey.Length > 0) Aliases.Remove("host:" + hostKey);
            }
            else
            {
                if (addr.Length > 0) Aliases[addr] = cleanAlias;
                if (hostKey.Length > 0) Aliases["host:" + hostKey] = cleanAlias;
            }
            for (int i = 0; i < ManualDevices.Count; i++)
            {
                if (addr.Length > 0 && string.Equals(ManualDevices[i].Address, addr, StringComparison.OrdinalIgnoreCase))
                {
                    ManualDevices[i].Alias = cleanAlias;
                }
            }
        }

        internal string ResolveDisplayName(string address, string hostName, string fallback)
        {
            string addr = (address ?? "").Trim();
            string val;
            if (addr.Length > 0 && Aliases.TryGetValue(addr, out val) && !string.IsNullOrWhiteSpace(val))
                return val;
            string hostKey = NormalizePeerKey(hostName);
            if (hostKey.Length > 0 && Aliases.TryGetValue("host:" + hostKey, out val) && !string.IsNullOrWhiteSpace(val))
                return val;
            for (int i = 0; i < ManualDevices.Count; i++)
            {
                if (addr.Length > 0 && string.Equals(ManualDevices[i].Address, addr, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(ManualDevices[i].Alias))
                    return ManualDevices[i].Alias;
            }
            return fallback;
        }

        internal static string Serialize(DeviceCatalogStore store)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < store.ManualDevices.Count; i++)
            {
                ManagedDeviceItem item = store.ManualDevices[i];
                if (string.IsNullOrWhiteSpace(item.Address)) continue;
                sb.AppendLine("manual\t" + item.Address.Trim() + "\t" + (item.Alias ?? "").Trim() + "\t" + (item.IsMobile ? "1" : "0"));
            }
            foreach (string hidden in store.HiddenKeys)
            {
                if (!string.IsNullOrWhiteSpace(hidden))
                    sb.AppendLine("hidden\t" + hidden.Trim());
            }
            foreach (KeyValuePair<string, string> kv in store.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    sb.AppendLine("alias\t" + kv.Key.Trim() + "\t" + kv.Value.Trim());
            }
            return sb.ToString();
        }

        internal static void Parse(string text, DeviceCatalogStore store)
        {
            if (string.IsNullOrEmpty(text) || store == null) return;
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 2) continue;
                string kind = parts[0].Trim();
                if (kind == "manual")
                {
                    string addr = parts[1].Trim();
                    string alias = parts.Length >= 3 ? parts[2].Trim() : "";
                    bool isMob = parts.Length >= 4 && parts[3].Trim() == "1";
                    if (addr.Length > 0 && addr.Length <= 253)
                    {
                        store.ManualDevices.Add(new ManagedDeviceItem
                        {
                            Address = addr,
                            Alias = alias,
                            IsMobile = isMob
                        });
                    }
                }
                else if (kind == "hidden")
                {
                    string key = parts[1].Trim();
                    if (key.Length > 0 && key.Length <= 260)
                        store.HiddenKeys.Add(key);
                }
                else if (kind == "alias" && parts.Length >= 3)
                {
                    string key = parts[1].Trim();
                    string val = parts[2].Trim();
                    if (key.Length > 0 && val.Length > 0 && key.Length <= 260)
                        store.Aliases[key] = val;
                }
            }
        }
    }
}
