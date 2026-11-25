using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DualRemote
{
    /// <summary>
    /// 物理显示器硬件级深度熄黑器（基于 Windows 内置 dxva2.dll DDC/CI VCP 协议）：
    /// - 仅调节物理显示器硬件面板的亮度（0x10）、对比度（0x12）与 RGB 增益（0x16/0x18/0x1A）至 0；
    /// - 完全在显示器硬件端生效，不中断 DisplayPort/HDMI 信号，对 DXGI 屏幕采集 100% 透明（主控端画面仍为全亮原色）；
    /// - 即使 Windows 11 系统级开始菜单、现代 XAML 右键菜单、中文输入法候选框越过桌面 Z 序，物理屏仍彻底漆黑不可窥视；
    /// - 熄黑前将每块屏幕的原始寄存器值落盘到 %LOCALAPPDATA%\双端远程\privacy-display-restore.txt，
    ///   支持退出防窥、锁屏、进程退出以及异常断电/强杀后下次启动自动恢复，绝不留下黑屏隐患。
    /// </summary>
    internal static class PrivacyDisplayDimmer
    {
        private const byte VcpBrightness = 0x10;
        private const byte VcpContrast = 0x12;
        private const byte VcpRedGain = 0x16;
        private const byte VcpGreenGain = 0x18;
        private const byte VcpBlueGain = 0x1A;
        private const int DdcDelayMs = 45;

        private static readonly byte[] VcpCodesDimOrder = new byte[]
        {
            VcpBrightness,
            VcpContrast,
            VcpRedGain,
            VcpGreenGain,
            VcpBlueGain
        };

        private static readonly byte[] VcpCodesRestoreOrder = new byte[]
        {
            VcpRedGain,
            VcpGreenGain,
            VcpBlueGain,
            VcpContrast,
            VcpBrightness
        };

        private static readonly object Gate = new object();
        private static readonly object HardwareIoLock = new object();
        private static int operationEpoch;
        private static bool isDimmed;
        private static bool processExitHooked;
        private static List<MonitorSavedState> activeSavedStates = new List<MonitorSavedState>();

        internal sealed class MonitorSavedState
        {
            internal int MonitorOrdinal;
            internal string Description = "";
            internal Dictionary<byte, uint> VcpValues = new Dictionary<byte, uint>();
        }

        internal static string RestoreFilePath
        {
            get { return Path.Combine(RecentStore.DirPath, "privacy-display-restore.txt"); }
        }

        internal static bool IsDimmed
        {
            get { lock (Gate) { return isDimmed; } }
        }

        private static void EnsureProcessExitHook()
        {
            lock (Gate)
            {
                if (processExitHooked) return;
                processExitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { RestoreAllSync(); } catch { }
                };
            }
        }

        /// <summary>
        /// 异步执行物理屏硬件级深度熄黑；若在执行过程中被调用 RestoreAll，将通过代次令牌立即中止并还原。
        /// </summary>
        internal static void DimAllMonitorsAsync(Action<int> onCompleted)
        {
            EnsureProcessExitHook();
            int myEpoch;
            lock (Gate)
            {
                operationEpoch++;
                myEpoch = operationEpoch;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                int dimmedCount = 0;
                try
                {
                    dimmedCount = DimAllMonitorsWorker(myEpoch);
                }
                catch
                {
                    dimmedCount = 0;
                }
                if (onCompleted != null)
                {
                    try { onCompleted(dimmedCount); } catch { }
                }
            });
        }

        private static int DimAllMonitorsWorker(int myEpoch)
        {
            lock (HardwareIoLock)
            {
                lock (Gate)
                {
                    if (myEpoch != operationEpoch) return 0;
                    if (isDimmed) return activeSavedStates.Count;
                }

                List<IntPtr> hMonitors = EnumerateHMonitors();
                if (hMonitors.Count == 0) return 0;

                var savedList = new List<MonitorSavedState>();
                int ordinal = 0;
                int dimmedMonitors = 0;

                foreach (IntPtr hMon in hMonitors)
                {
                    if (Volatile.Read(ref operationEpoch) != myEpoch) return 0;

                    PHYSICAL_MONITOR[] phys;
                    if (!TryGetPhysicalMonitors(hMon, out phys) || phys == null || phys.Length == 0)
                        continue;

                    try
                    {
                        for (int i = 0; i < phys.Length; i++)
                        {
                            if (Volatile.Read(ref operationEpoch) != myEpoch) break;

                            IntPtr hPhys = phys[i].hPhysicalMonitor;
                            string desc = (phys[i].szPhysicalMonitorDescription ?? "").Trim();
                            var state = new MonitorSavedState
                            {
                                MonitorOrdinal = ordinal,
                                Description = desc
                            };

                            // 仅读取并保存显示器真实支持且当前值 > 0 的寄存器，避免写入不支持或只读属性
                            for (int c = 0; c < VcpCodesDimOrder.Length; c++)
                            {
                                byte code = VcpCodesDimOrder[c];
                                uint curVal, maxVal;
                                int vcpType;
                                if (GetVCPFeatureAndVCPFeatureReply(hPhys, code, out vcpType, out curVal, out maxVal))
                                {
                                    if (maxVal > 0 && curVal > 0 && curVal <= maxVal)
                                    {
                                        state.VcpValues[code] = curVal;
                                    }
                                }
                                Thread.Sleep(25);
                            }

                            if (state.VcpValues.Count > 0)
                            {
                                savedList.Add(state);
                            }
                            ordinal++;
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors((uint)phys.Length, phys);
                    }
                }

                if (savedList.Count == 0 || Volatile.Read(ref operationEpoch) != myEpoch)
                    return 0;

                lock (Gate)
                {
                    if (myEpoch != operationEpoch)
                    {
                        return 0;
                    }
                    // 先落盘备份原始亮度/对比度/RGB 增益，再执行硬件熄黑（断电/崩溃保护）
                    SaveRestoreFile(savedList);
                    activeSavedStates = savedList;
                    isDimmed = true;
                }

                // 第二阶段：逐台写入 0，确保每条 DDC/CI I2C 指令间隔 >= 45ms
                ordinal = 0;
                foreach (IntPtr hMon in hMonitors)
                {
                    if (Volatile.Read(ref operationEpoch) != myEpoch) break;

                    PHYSICAL_MONITOR[] phys;
                    if (!TryGetPhysicalMonitors(hMon, out phys) || phys == null || phys.Length == 0)
                        continue;

                    try
                    {
                        for (int i = 0; i < phys.Length; i++)
                        {
                            if (Volatile.Read(ref operationEpoch) != myEpoch) break;

                            MonitorSavedState match = FindSavedState(savedList, ordinal, (phys[i].szPhysicalMonitorDescription ?? "").Trim());
                            if (match != null)
                            {
                                bool anyWritten = false;
                                for (int c = 0; c < VcpCodesDimOrder.Length; c++)
                                {
                                    if (Volatile.Read(ref operationEpoch) != myEpoch) break;
                                    byte code = VcpCodesDimOrder[c];
                                    if (match.VcpValues.ContainsKey(code))
                                    {
                                        if (SetVCPFeature(phys[i].hPhysicalMonitor, code, 0))
                                        {
                                            anyWritten = true;
                                        }
                                        Thread.Sleep(DdcDelayMs);
                                    }
                                }
                                if (anyWritten) dimmedMonitors++;
                            }
                            ordinal++;
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors((uint)phys.Length, phys);
                    }
                }

                // 如果在写 0 过程中用户已经取消防窥，立刻执行还原，绝不残留黑屏
                if (Volatile.Read(ref operationEpoch) != myEpoch)
                {
                    RestoreStatesToHardware(savedList);
                    bool shouldDeleteFile = false;
                    lock (Gate)
                    {
                        if (!isDimmed)
                        {
                            activeSavedStates.Clear();
                            shouldDeleteFile = true;
                        }
                    }
                    if (shouldDeleteFile) DeleteRestoreFileQuietly();
                    return 0;
                }

                return dimmedMonitors;
            }
        }

        /// <summary>异步恢复全部物理显示器亮度/对比度/RGB 增益（供防窥退出/锁屏调用）。</summary>
        internal static void RestoreAllAsync()
        {
            List<MonitorSavedState> toRestore;
            int restoreEpoch;
            lock (Gate)
            {
                operationEpoch++;
                restoreEpoch = operationEpoch;
                if (!isDimmed && activeSavedStates.Count == 0 && !File.Exists(RestoreFilePath))
                    return;
                toRestore = activeSavedStates.Count > 0
                    ? new List<MonitorSavedState>(activeSavedStates)
                    : LoadRestoreFile();
                isDimmed = false;
                activeSavedStates.Clear();
            }

            if (toRestore == null || toRestore.Count == 0)
            {
                lock (Gate)
                {
                    if (operationEpoch == restoreEpoch && !isDimmed) DeleteRestoreFileQuietly();
                }
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (HardwareIoLock)
                {
                    try
                    {
                        RestoreStatesToHardware(toRestore);
                    }
                    catch { }
                    finally
                    {
                        lock (Gate)
                        {
                            if (operationEpoch == restoreEpoch && !isDimmed)
                            {
                                DeleteRestoreFileQuietly();
                            }
                        }
                    }
                }
            });
        }

        /// <summary>同步恢复物理显示器（供进程退出前最后一道防线使用）。</summary>
        internal static void RestoreAllSync()
        {
            List<MonitorSavedState> toRestore;
            int restoreEpoch;
            lock (Gate)
            {
                operationEpoch++;
                restoreEpoch = operationEpoch;
                if (!isDimmed && activeSavedStates.Count == 0 && !File.Exists(RestoreFilePath))
                    return;
                toRestore = activeSavedStates.Count > 0
                    ? new List<MonitorSavedState>(activeSavedStates)
                    : LoadRestoreFile();
                isDimmed = false;
                activeSavedStates.Clear();
            }

            lock (HardwareIoLock)
            {
                if (toRestore != null && toRestore.Count > 0)
                {
                    try { RestoreStatesToHardware(toRestore); } catch { }
                }
                lock (Gate)
                {
                    if (operationEpoch == restoreEpoch && !isDimmed)
                    {
                        DeleteRestoreFileQuietly();
                    }
                }
            }
        }

        /// <summary>
        /// 启动崩溃自愈：若上次防窥期间进程异常终止残留了 restore 文件，自动恢复物理显示器原始亮度。
        /// </summary>
        internal static void RecoverIfNeeded()
        {
            EnsureProcessExitHook();
            try
            {
                if (!File.Exists(RestoreFilePath)) return;
                List<MonitorSavedState> pending = LoadRestoreFile();
                if (pending == null || pending.Count == 0)
                {
                    DeleteRestoreFileQuietly();
                    return;
                }
                int recoverEpoch;
                lock (Gate) { recoverEpoch = operationEpoch; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    lock (HardwareIoLock)
                    {
                        try { RestoreStatesToHardware(pending); } catch { }
                        finally
                        {
                            lock (Gate)
                            {
                                if (operationEpoch == recoverEpoch && !isDimmed)
                                    DeleteRestoreFileQuietly();
                            }
                        }
                    }
                });
            }
            catch { }
        }

        private static void RestoreStatesToHardware(List<MonitorSavedState> savedList)
        {
            if (savedList == null || savedList.Count == 0) return;
            List<IntPtr> hMonitors = EnumerateHMonitors();
            int ordinal = 0;

            foreach (IntPtr hMon in hMonitors)
            {
                PHYSICAL_MONITOR[] phys;
                if (!TryGetPhysicalMonitors(hMon, out phys) || phys == null || phys.Length == 0)
                    continue;

                try
                {
                    for (int i = 0; i < phys.Length; i++)
                    {
                        MonitorSavedState match = FindSavedState(savedList, ordinal, (phys[i].szPhysicalMonitorDescription ?? "").Trim());
                        if (match != null)
                        {
                            for (int c = 0; c < VcpCodesRestoreOrder.Length; c++)
                            {
                                byte code = VcpCodesRestoreOrder[c];
                                uint origVal;
                                if (match.VcpValues.TryGetValue(code, out origVal))
                                {
                                    SetVCPFeature(phys[i].hPhysicalMonitor, code, origVal);
                                    Thread.Sleep(DdcDelayMs);
                                }
                            }
                        }
                        ordinal++;
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors((uint)phys.Length, phys);
                }
            }
        }

        private static MonitorSavedState FindSavedState(List<MonitorSavedState> list, int ordinal, string desc)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].MonitorOrdinal == ordinal) return list[i];
            }
            if (!string.IsNullOrEmpty(desc))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].Description, desc, StringComparison.OrdinalIgnoreCase))
                        return list[i];
                }
            }
            return null;
        }

        internal static string SerializeRestoreRecords(List<MonitorSavedState> states)
        {
            var sb = new StringBuilder();
            if (states == null) return "";
            for (int i = 0; i < states.Count; i++)
            {
                MonitorSavedState s = states[i];
                if (s == null || s.VcpValues == null || s.VcpValues.Count == 0) continue;
                string safeDesc = (s.Description ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", " ");
                sb.Append(s.MonitorOrdinal.ToString(CultureInfo.InvariantCulture));
                sb.Append('|');
                sb.Append(safeDesc);
                sb.Append('|');
                bool first = true;
                foreach (KeyValuePair<byte, uint> kv in s.VcpValues)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(kv.Key.ToString("X2", CultureInfo.InvariantCulture));
                    sb.Append('=');
                    sb.Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        internal static List<MonitorSavedState> ParseRestoreRecords(string text)
        {
            var result = new List<MonitorSavedState>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('|');
                if (parts.Length < 3) continue;
                int ord;
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ord) || ord < 0)
                    continue;
                var st = new MonitorSavedState
                {
                    MonitorOrdinal = ord,
                    Description = parts[1].Trim()
                };
                string[] pairs = parts[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int j = 0; j < pairs.Length; j++)
                {
                    int eq = pairs[j].IndexOf('=');
                    if (eq <= 0) continue;
                    byte code;
                    uint val;
                    if (byte.TryParse(pairs[j].Substring(0, eq).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) &&
                        uint.TryParse(pairs[j].Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                    {
                        st.VcpValues[code] = val;
                    }
                }
                if (st.VcpValues.Count > 0) result.Add(st);
            }
            return result;
        }

        private static void SaveRestoreFile(List<MonitorSavedState> states)
        {
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                File.WriteAllText(RestoreFilePath, SerializeRestoreRecords(states), new UTF8Encoding(false));
            }
            catch { }
        }

        private static List<MonitorSavedState> LoadRestoreFile()
        {
            try
            {
                if (!File.Exists(RestoreFilePath)) return new List<MonitorSavedState>();
                return ParseRestoreRecords(File.ReadAllText(RestoreFilePath, Encoding.UTF8));
            }
            catch
            {
                return new List<MonitorSavedState>();
            }
        }

        private static void DeleteRestoreFileQuietly()
        {
            try
            {
                if (File.Exists(RestoreFilePath)) File.Delete(RestoreFilePath);
            }
            catch { }
        }

        private static List<IntPtr> EnumerateHMonitors()
        {
            var list = new List<IntPtr>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    delegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
                    {
                        list.Add(hMonitor);
                        return true;
                    }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        private static bool TryGetPhysicalMonitors(IntPtr hMonitor, out PHYSICAL_MONITOR[] monitors)
        {
            monitors = null;
            try
            {
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0 || count > 16)
                    return false;
                var arr = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, arr))
                    return false;
                monitors = arr;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, out int pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);
    }
}
