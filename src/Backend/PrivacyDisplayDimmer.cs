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
    /// 物理显示器硬件级背光降亮器（基于 Windows 内置 dxva2.dll DDC/CI VCP 协议）：
    /// - 严格仅调节物理显示器背光亮度（VCP 0x10），绝不修改对比度（0x12）、色温预设（0x14）、
    ///   RGB 增益（0x16/0x18/0x1A）或画面模式（0xDC）；
    ///   （部分 MStar 等显示器固件对 RGB 增益存在读写缩放不一致或切出 sRGB 预设缺陷，写入 0x16/0x18/0x1A
    ///   会导致退出防窥后画面持续过曝、偏色甚至重启电脑仍无法恢复。）
    /// - 调亮前对每块非 HDR 物理屏执行双读一致性校验（间隔 100ms），避免 I2C 读数漂移污染基线；
    /// - 将原始亮度落盘到 %LOCALAPPDATA%\双端远程\privacy-display-restore.txt，退出/锁屏/异常重启时
    ///   采用「写回 + 回读校验 + 自动重试」确保 100% 精确还原；
    /// - 若检测到旧版本残留的含 RGB 增益记录的恢复文件，绝不回写 0x16/0x18/0x1A，而是通过 VCP 0x08
    ///   安全恢复显示器出厂色彩默认，再精确还原原始亮度。
    /// </summary>
    internal static class PrivacyDisplayDimmer
    {
        internal const byte VcpRestoreFactoryColor = 0x08;
        internal const byte VcpBrightness = 0x10;
        internal const byte VcpContrast = 0x12;
        internal const byte VcpRedGain = 0x16;
        internal const byte VcpGreenGain = 0x18;
        internal const byte VcpBlueGain = 0x1A;

        // MStar 等显示器 DDC/CI I2C 总线响应较慢，间隔过短会导致回包错位或读到 0
        private const int DdcReadDelayMs = 100;
        private const int DdcWriteDelayMs = 120;
        private const int DdcVerifyDelayMs = 140;
        private const int DdcFactoryColorWaitMs = 1100;
        private const int MaxVerifyRetries = 3;

        // 仅允许调节背光亮度（VCP 0x10），彻底禁止写入对比度与 RGB 增益
        private static readonly byte[] VcpCodesDimOrder = new byte[]
        {
            VcpBrightness
        };

        private static readonly byte[] VcpCodesRestoreOrder = new byte[]
        {
            VcpBrightness
        };

        private static readonly object Gate = new object();
        private static readonly object HardwareIoLock = new object();
        private static readonly Queue<Action> WorkerQueue = new Queue<Action>();
        private static bool workerRunning;
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

        /// <summary>安全不变量：仅允许 VCP 0x10（背光亮度）参与防窥调暗。</summary>
        internal static bool IsSafeDimmingVcpCode(byte code)
        {
            for (int i = 0; i < VcpCodesDimOrder.Length; i++)
            {
                if (VcpCodesDimOrder[i] == code) return true;
            }
            return false;
        }

        /// <summary>检测恢复记录是否包含旧版本（v6.1.0）写入的 RGB 增益（0x16/0x18/0x1A）。</summary>
        internal static bool HasLegacyColorVcpCodes(MonitorSavedState state)
        {
            if (state == null || state.VcpValues == null) return false;
            return state.VcpValues.ContainsKey(VcpRedGain)
                || state.VcpValues.ContainsKey(VcpGreenGain)
                || state.VcpValues.ContainsKey(VcpBlueGain);
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

        /// <summary>单线程有序串行调度 DDC/CI 硬件 I/O，彻底消除启停竞态。</summary>
        private static void EnqueueHardwareWork(Action work)
        {
            if (work == null) return;
            bool startWorker = false;
            lock (WorkerQueue)
            {
                WorkerQueue.Enqueue(work);
                if (!workerRunning)
                {
                    workerRunning = true;
                    startWorker = true;
                }
            }
            if (startWorker)
            {
                ThreadPool.QueueUserWorkItem(delegate { DrainHardwareQueue(); });
            }
        }

        private static void DrainHardwareQueue()
        {
            while (true)
            {
                Action next = null;
                lock (WorkerQueue)
                {
                    if (WorkerQueue.Count == 0)
                    {
                        workerRunning = false;
                        return;
                    }
                    next = WorkerQueue.Dequeue();
                }
                try { next(); } catch { }
            }
        }

        /// <summary>
        /// 异步执行物理屏背光降亮（仅 VCP 0x10）；若执行期间调用 RestoreAll，将通过代次令牌立即中止并还原。
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

            EnqueueHardwareWork(delegate
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
                List<MonitorSavedState> existingBaseline = null;
                lock (Gate)
                {
                    if (myEpoch != operationEpoch) return 0;
                    if (isDimmed && activeSavedStates.Count > 0) return activeSavedStates.Count;
                    if (activeSavedStates.Count > 0)
                    {
                        existingBaseline = CloneStateList(activeSavedStates);
                    }
                }

                // 若磁盘上存在尚未恢复的旧基线，优先沿用（避免二次调暗时把 0 误当成原始亮度）
                if ((existingBaseline == null || existingBaseline.Count == 0) && File.Exists(RestoreFilePath))
                {
                    List<MonitorSavedState> diskBaseline = LoadRestoreFile();
                    if (diskBaseline != null && diskBaseline.Count > 0)
                    {
                        // 若检测到旧版含 RGB 增益的残留文件，先安全自愈再按新逻辑采集亮度基线
                        bool hasLegacy = false;
                        for (int k = 0; k < diskBaseline.Count; k++)
                        {
                            if (HasLegacyColorVcpCodes(diskBaseline[k])) { hasLegacy = true; break; }
                        }
                        if (hasLegacy)
                        {
                            RestoreStatesToHardware(diskBaseline);
                            DeleteRestoreFileQuietly();
                        }
                        else
                        {
                            existingBaseline = diskBaseline;
                        }
                    }
                }

                List<IntPtr> hMonitors = EnumerateHMonitors();
                if (hMonitors.Count == 0) return 0;

                // 若系统存在开启 Windows HDR 的显示器，跳过 DDC/CI 背光干预，避免破坏 HDR 亮度映射
                HashSet<string> hdrDevices = QueryHdrEnabledDeviceNames();

                List<MonitorSavedState> savedList = existingBaseline != null && existingBaseline.Count > 0
                    ? existingBaseline
                    : ReadBaselineBrightnessStates(hMonitors, hdrDevices, myEpoch);

                if (savedList == null || savedList.Count == 0 || Volatile.Read(ref operationEpoch) != myEpoch)
                    return 0;

                lock (Gate)
                {
                    if (myEpoch != operationEpoch)
                    {
                        return 0;
                    }
                    SaveRestoreFile(savedList);
                    activeSavedStates = CloneStateList(savedList);
                    isDimmed = true;
                }

                // 第二阶段：仅将背光亮度（VCP 0x10）调至 0，绝不修改对比度与 RGB 增益
                int ordinal = 0;
                int dimmedMonitors = 0;
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
                            if (match != null && match.VcpValues.ContainsKey(VcpBrightness))
                            {
                                if (SetVCPFeature(phys[i].hPhysicalMonitor, VcpBrightness, 0))
                                {
                                    dimmedMonitors++;
                                }
                                Thread.Sleep(DdcWriteDelayMs);
                            }
                            ordinal++;
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors((uint)phys.Length, phys);
                    }
                }

                // 若写入过程中用户已退出防窥，立刻还原并校验
                if (Volatile.Read(ref operationEpoch) != myEpoch)
                {
                    bool restoredAll = RestoreStatesToHardware(savedList);
                    bool shouldDeleteFile = false;
                    lock (Gate)
                    {
                        if (!isDimmed)
                        {
                            activeSavedStates.Clear();
                            shouldDeleteFile = restoredAll;
                        }
                    }
                    if (shouldDeleteFile) DeleteRestoreFileQuietly();
                    return 0;
                }

                return dimmedMonitors;
            }
        }

        private static List<MonitorSavedState> ReadBaselineBrightnessStates(
            List<IntPtr> hMonitors,
            HashSet<string> hdrDevices,
            int myEpoch)
        {
            var savedList = new List<MonitorSavedState>();
            int ordinal = 0;

            foreach (IntPtr hMon in hMonitors)
            {
                if (Volatile.Read(ref operationEpoch) != myEpoch) return savedList;

                string gdiDeviceName = GetMonitorDeviceName(hMon);
                bool isHdr = !string.IsNullOrEmpty(gdiDeviceName) && hdrDevices != null && hdrDevices.Contains(gdiDeviceName);

                PHYSICAL_MONITOR[] phys;
                if (!TryGetPhysicalMonitors(hMon, out phys) || phys == null || phys.Length == 0)
                    continue;

                try
                {
                    for (int i = 0; i < phys.Length; i++)
                    {
                        if (Volatile.Read(ref operationEpoch) != myEpoch) break;

                        if (!isHdr)
                        {
                            IntPtr hPhys = phys[i].hPhysicalMonitor;
                            string desc = (phys[i].szPhysicalMonitorDescription ?? "").Trim();
                            uint stableBrightness;
                            if (TryReadStableVcpValue(hPhys, VcpBrightness, out stableBrightness) && stableBrightness > 0)
                            {
                                var state = new MonitorSavedState
                                {
                                    MonitorOrdinal = ordinal,
                                    Description = desc
                                };
                                state.VcpValues[VcpBrightness] = stableBrightness;
                                savedList.Add(state);
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

            return savedList;
        }

        /// <summary>
        /// 双读一致性校验：相隔 100ms 连续读取两次同一 VCP 寄存器，仅在两次读数完全一致且合法时返回 true，
        /// 防止 MStar 等显示器 I2C 总线回包错位污染原始亮度基线。
        /// </summary>
        private static bool TryReadStableVcpValue(IntPtr hPhys, byte vcpCode, out uint stableValue)
        {
            stableValue = 0;
            uint cur1, max1;
            int type1;
            if (!GetVCPFeatureAndVCPFeatureReply(hPhys, vcpCode, out type1, out cur1, out max1))
                return false;
            if (max1 == 0 || cur1 > max1)
                return false;

            Thread.Sleep(DdcReadDelayMs);

            uint cur2, max2;
            int type2;
            if (!GetVCPFeatureAndVCPFeatureReply(hPhys, vcpCode, out type2, out cur2, out max2))
                return false;
            if (max2 == 0 || cur2 > max2 || cur1 != cur2)
                return false;

            Thread.Sleep(DdcReadDelayMs);
            stableValue = cur1;
            return true;
        }

        /// <summary>异步恢复全部物理显示器背光亮度（供防窥退出/锁屏调用）。</summary>
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
                    ? CloneStateList(activeSavedStates)
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

            EnqueueHardwareWork(delegate
            {
                lock (HardwareIoLock)
                {
                    bool restoredAll = false;
                    try
                    {
                        restoredAll = RestoreStatesToHardware(toRestore);
                    }
                    catch { }
                    finally
                    {
                        lock (Gate)
                        {
                            if (operationEpoch == restoreEpoch && !isDimmed && restoredAll)
                            {
                                DeleteRestoreFileQuietly();
                            }
                        }
                    }
                }
            });
        }

        /// <summary>同步恢复全部物理显示器背光亮度（供进程退出/关机调用，确保不残留低亮）。</summary>
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
                    ? CloneStateList(activeSavedStates)
                    : LoadRestoreFile();
                isDimmed = false;
                activeSavedStates.Clear();
            }

            lock (HardwareIoLock)
            {
                bool restoredAll = true;
                if (toRestore != null && toRestore.Count > 0)
                {
                    try { restoredAll = RestoreStatesToHardware(toRestore); } catch { restoredAll = false; }
                }
                lock (Gate)
                {
                    if (operationEpoch == restoreEpoch && !isDimmed && restoredAll)
                    {
                        DeleteRestoreFileQuietly();
                    }
                }
            }
        }

        /// <summary>
        /// 启动崩溃自愈：若上次防窥期间进程异常终止残留了 restore 文件，自动恢复物理显示器原始亮度；
        /// 若检测到旧版本含 RGB 增益的残留文件，则通过 VCP 0x08 安全还原出厂色彩，绝不回写 0x16/0x18/0x1A。
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
                EnqueueHardwareWork(delegate
                {
                    lock (HardwareIoLock)
                    {
                        bool restoredAll = false;
                        try { restoredAll = RestoreStatesToHardware(pending); } catch { }
                        finally
                        {
                            lock (Gate)
                            {
                                if (operationEpoch == recoverEpoch && !isDimmed && restoredAll)
                                    DeleteRestoreFileQuietly();
                            }
                        }
                    }
                });
            }
            catch { }
        }

        /// <summary>
        /// 一键安全恢复物理显示器出厂色彩默认（VCP 0x08）并保留用户当前亮度（VCP 0x10），
        /// 专用于修复旧版 DDC/CI 误改 RGB 增益导致的画面过曝或偏色。
        /// </summary>
        internal static void RestoreFactoryColorDefaultsAsync(Action<int> onCompleted)
        {
            EnqueueHardwareWork(delegate
            {
                int fixedCount = 0;
                lock (HardwareIoLock)
                {
                    List<IntPtr> hMonitors = EnumerateHMonitors();
                    foreach (IntPtr hMon in hMonitors)
                    {
                        PHYSICAL_MONITOR[] phys;
                        if (!TryGetPhysicalMonitors(hMon, out phys) || phys == null || phys.Length == 0)
                            continue;
                        try
                        {
                            for (int i = 0; i < phys.Length; i++)
                            {
                                IntPtr hPhys = phys[i].hPhysicalMonitor;
                                uint savedBrightness;
                                bool hasBrightness = TryReadStableVcpValue(hPhys, VcpBrightness, out savedBrightness);
                                if (SetVCPFeature(hPhys, VcpRestoreFactoryColor, 1))
                                {
                                    fixedCount++;
                                    Thread.Sleep(DdcFactoryColorWaitMs);
                                    if (hasBrightness)
                                    {
                                        SetVcpWithVerify(hPhys, VcpBrightness, savedBrightness);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            DestroyPhysicalMonitors((uint)phys.Length, phys);
                        }
                    }
                }
                if (onCompleted != null)
                {
                    try { onCompleted(fixedCount); } catch { }
                }
            });
        }

        private static bool RestoreStatesToHardware(List<MonitorSavedState> savedList)
        {
            if (savedList == null || savedList.Count == 0) return true;
            List<IntPtr> hMonitors = EnumerateHMonitors();
            if (hMonitors.Count == 0) return true;

            int ordinal = 0;
            bool allVerified = true;

            foreach (IntPtr hMon in hMonitors)
            {
                PHYSICAL_MONITOR[] phys;
                if (!TryGetPhysicalMonitors(hMon, out phys) || phys == null || phys.Length == 0)
                    continue;

                try
                {
                    for (int i = 0; i < phys.Length; i++)
                    {
                        IntPtr hPhys = phys[i].hPhysicalMonitor;
                        MonitorSavedState match = FindSavedState(savedList, ordinal, (phys[i].szPhysicalMonitorDescription ?? "").Trim());
                        if (match != null)
                        {
                            // 兼容旧版残留文件：若旧版曾经修改过 RGB 增益（0x16/0x18/0x1A），
                            // 绝不回写 RGB 增益数值（防止 MStar 缩放跳变导致过曝），改为发送 VCP 0x08 恢复出厂色彩
                            if (HasLegacyColorVcpCodes(match))
                            {
                                SetVCPFeature(hPhys, VcpRestoreFactoryColor, 1);
                                Thread.Sleep(DdcFactoryColorWaitMs);
                            }

                            // 仅在旧版确实将对比度归零时才恢复对比度
                            uint origContrast;
                            if (match.VcpValues.TryGetValue(VcpContrast, out origContrast) && origContrast > 0)
                            {
                                uint curContrast;
                                if (TryReadStableVcpValue(hPhys, VcpContrast, out curContrast) && curContrast == 0)
                                {
                                    if (!SetVcpWithVerify(hPhys, VcpContrast, origContrast))
                                    {
                                        allVerified = false;
                                    }
                                }
                            }

                            for (int c = 0; c < VcpCodesRestoreOrder.Length; c++)
                            {
                                byte code = VcpCodesRestoreOrder[c];
                                uint origVal;
                                if (match.VcpValues.TryGetValue(code, out origVal))
                                {
                                    if (!SetVcpWithVerify(hPhys, code, origVal))
                                    {
                                        allVerified = false;
                                    }
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

            return allVerified;
        }

        private static bool SetVcpWithVerify(IntPtr hPhys, byte code, uint targetVal)
        {
            for (int attempt = 0; attempt < MaxVerifyRetries; attempt++)
            {
                bool setOk = SetVCPFeature(hPhys, code, targetVal);
                Thread.Sleep(DdcVerifyDelayMs);

                uint actual, maxVal;
                int pvct;
                if (GetVCPFeatureAndVCPFeatureReply(hPhys, code, out pvct, out actual, out maxVal))
                {
                    if (actual == targetVal)
                    {
                        Thread.Sleep(DdcReadDelayMs);
                        return true;
                    }
                }
                else if (setOk && attempt == MaxVerifyRetries - 1)
                {
                    return true;
                }
                Thread.Sleep(DdcWriteDelayMs);
            }
            return false;
        }

        private static List<MonitorSavedState> CloneStateList(List<MonitorSavedState> src)
        {
            var copy = new List<MonitorSavedState>();
            if (src == null) return copy;
            for (int i = 0; i < src.Count; i++)
            {
                MonitorSavedState s = src[i];
                if (s == null) continue;
                var clone = new MonitorSavedState
                {
                    MonitorOrdinal = s.MonitorOrdinal,
                    Description = s.Description
                };
                if (s.VcpValues != null)
                {
                    foreach (KeyValuePair<byte, uint> kv in s.VcpValues)
                    {
                        clone.VcpValues[kv.Key] = kv.Value;
                    }
                }
                copy.Add(clone);
            }
            return copy;
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

        private static string GetMonitorDeviceName(IntPtr hMonitor)
        {
            try
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfoW(hMonitor, ref info))
                {
                    return (info.szDevice ?? "").Trim();
                }
            }
            catch { }
            return "";
        }

        private static HashSet<string> QueryHdrEnabledDeviceNames()
        {
            var hdrSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint numPaths, numModes;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0 || numPaths == 0)
                    return hdrSet;
                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
                    return hdrSet;

                for (int i = 0; i < numPaths; i++)
                {
                    var adv = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                    adv.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                    adv.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
                    adv.header.adapterId = paths[i].targetInfo.adapterId;
                    adv.header.id = paths[i].targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref adv) == 0 && (adv.value & 2u) != 0)
                    {
                        var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                        srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                        srcName.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                        srcName.header.adapterId = paths[i].sourceInfo.adapterId;
                        srcName.header.id = paths[i].sourceInfo.id;
                        if (DisplayConfigGetDeviceInfo(ref srcName) == 0 && !string.IsNullOrEmpty(srcName.viewGdiDeviceName))
                        {
                            hdrSet.Add(srcName.viewGdiDeviceName.Trim());
                        }
                    }
                }
            }
            catch { }
            return hdrSet;
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

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

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
