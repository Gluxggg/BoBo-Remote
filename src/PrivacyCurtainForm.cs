using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace DualRemote
{
    /// <summary>
    /// 在当前 Windows 桌面上遮挡屏幕、系统指针和候选窗，保留远程画面与交互。
    /// </summary>
    internal sealed class PrivacyCurtainManager : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_SWITCHEND = 0x0015;
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_REORDER = 0x8004;
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint EVENT_OBJECT_SELECTION = 0x8006;
        private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
        private const int OBJID_CARET = -8;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint GA_ROOT = 2;
        private const uint GW_HWNDPREV = 3;
        private const int DWMWA_CLOAKED = 14;
        private const int PreviewRefreshMs = 1000;
        private const int CursorRestoreRetryMs = 250;
        private const int EdgeGuardMarginPx = 96;
        private const int InputBurstDurationMs = 360;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        private readonly Control owner;
        private readonly List<PrivacyCurtainWindow> windows = new List<PrivacyCurtainWindow>();
        private readonly Timer cursorRestoreTimer;
        private Timer previewTimer;
        private CurtainWindowThread windowThread;
        private PrivacyWallpaperConfig currentConfig;
        private Bitmap cachedImage;
        private volatile bool active;
        private volatile bool previewMode;
        private volatile Control callbackAnchor;
        private volatile Rectangle[] cachedScreenBounds = new Rectangle[0];
        private Action<BoBoInputPacket> remoteInputHandler;
        private Action remoteTextInputHandler;
        private bool displayEventsHooked;
        private bool reassertingCurtain;
        private volatile int activationEpoch;
        private int lastClockTick;
        private IntPtr winEventHookSys;
        private IntPtr winEventHookObj;
        private WinEventDelegate winEventCallback;
        private volatile bool disposed;

        internal event Action StateChanged;
        internal event Action LocalEscapeRequested;

        internal bool IsActive { get { return active; } }
        internal bool IsPreview { get { return active && previewMode; } }

        internal PrivacyCurtainManager(Control owner)
        {
            this.owner = owner;
            cursorRestoreTimer = new Timer { Interval = CursorRestoreRetryMs };
            cursorRestoreTimer.Tick += delegate { RestoreCursorMask(); };
        }

        internal bool Activate(PrivacyWallpaperConfig config, bool preview, out string statusMessage)
        {
            Deactivate(false);
            if (disposed)
            {
                statusMessage = "防窥壁纸已关闭。";
                return false;
            }

            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0)
            {
                statusMessage = "未检测到可用显示器。";
                return false;
            }

            currentConfig = config ?? new PrivacyWallpaperConfig();
            previewMode = preview;
            cursorRestoreTimer.Stop();
            cachedScreenBounds = ExtractScreenBounds(screens);
            bool cursorMasked = false;
            bool inputBlocked = false;
            bool allExcluded = false;
            int screenCount = 0;
            try
            {
                // Keep Magnification cursor masking on the persistent UI thread while
                // keeping the live curtain windows, high-res Z-order guard, and WinEvent hooks on their own STA thread.
                if (!preview) cursorMasked = BoBoCoreInterop.SetPrivacyCursorMask(true);
                if (!preview) windowThread = new CurtainWindowThread();
                InvokeOnWindowThread(delegate
                {
                    ReloadImage();
                    windows.AddRange(CreateCurtainWindows(screens, true));
                    callbackAnchor = windows[0];
                    active = true;
                    lastClockTick = Environment.TickCount;
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    displayEventsHooked = true;
                    if (!preview)
                    {
                        InstallWinEventHooks();
                        EnsureAllWindowsTopMostNoActivate();
                        if (windowThread != null)
                        {
                            windowThread.SetPulseHandler(KeepOnTopAndRefreshClock);
                            windowThread.SignalBurst(InputBurstDurationMs);
                        }
                    }
                    else
                    {
                        previewTimer = new Timer { Interval = PreviewRefreshMs };
                        previewTimer.Tick += delegate { KeepOnTopAndRefreshClock(); };
                        previewTimer.Start();
                    }
                    allExcluded = windows.TrueForAll(win => win.CaptureExcluded);
                    screenCount = windows.Count;
                });
                if (!preview)
                {
                    remoteInputHandler = NotifyRemoteInput;
                    remoteTextInputHandler = NotifyRemoteTextInput;
                    BoBoCoreInterop.RemoteInputHook = remoteInputHandler;
                    BoBoCoreInterop.RemoteTextInputHook = remoteTextInputHandler;
                }
                if (!preview && currentConfig.BlockLocalInput)
                    inputBlocked = BoBoCoreInterop.SetPrivacyInputBlock(true, OnNativeLocalEscapeTriggered);
                if (!preview && currentConfig.HardwareBlackout)
                    PrivacyDisplayDimmer.DimAllMonitorsAsync(null);
            }
            catch (Exception ex)
            {
                Deactivate(false);
                statusMessage = "无法开启防窥壁纸：" + ex.Message;
                return false;
            }

            if (preview)
            {
                statusMessage = "正在全屏预览防窥壁纸，点击任意处或按 Esc 退出。";
            }
            else
            {
                var status = new List<string> { "防窥壁纸已覆盖 " + screenCount + " 块屏幕" };
                if (!allExcluded) status.Add("部分屏幕的捕获排除不可用，远端可能看到壁纸");
                status.Add(cursorMasked ? "已请求隐藏主机指针" : "主机指针遮挡不可用");
                if (currentConfig.BlockLocalInput)
                    status.Add(inputBlocked ? "已锁定物理键鼠" : "物理键鼠锁定不可用");
                if (currentConfig.HardwareBlackout) status.Add("已请求背光降亮，取决于显示器支持");
                statusMessage = string.Join(" · ", status) + "。按 Ctrl+Alt+Shift+P 退出。";
            }
            RaiseStateChanged();
            return true;
        }

        internal void NotifyRemoteInput(BoBoInputPacket pkt)
        {
            if (!active || previewMode || disposed) return;
            bool trigger = IsEdgeOrPopupTriggerInput(pkt);
            if (!trigger && pkt.Kind == 2)
            {
                NativePoint pt;
                if (GetCursorPos(out pt))
                    trigger = IsPointNearCurtainEdge(pt.X, pt.Y, cachedScreenBounds, EdgeGuardMarginPx);
            }
            if (trigger)
            {
                CurtainWindowThread wt = windowThread;
                if (wt != null) wt.SignalBurst(InputBurstDurationMs);
            }
        }

        internal void NotifyRemoteTextInput()
        {
            if (!active || previewMode || disposed) return;
            CurtainWindowThread wt = windowThread;
            if (wt != null) wt.SignalBurst(InputBurstDurationMs);
        }

        internal static bool IsEdgeOrPopupTriggerInput(BoBoInputPacket pkt)
        {
            // Mouse clicks, wheels, keys/IME typing, or touch can raise shell/IME topmost overlays.
            if (pkt.Kind == 3 || pkt.Kind == 4 || pkt.Kind == 5 || pkt.Kind == 7) return true;
            // Absolute normalized mouse move (0..65535): guard top/bottom/left/right edge trigger zones.
            if (pkt.Kind == 1)
            {
                ushort nx = unchecked((ushort)pkt.A);
                ushort ny = unchecked((ushort)pkt.B);
                return ny >= 58500 || ny <= 4500 || nx <= 4500 || nx >= 61000;
            }
            return false;
        }

        internal static bool IsPointNearCurtainEdge(int x, int y, Rectangle[] screenBounds, int marginPx)
        {
            if (screenBounds == null || screenBounds.Length == 0) return false;
            int m = Math.Max(16, marginPx);
            for (int i = 0; i < screenBounds.Length; i++)
            {
                Rectangle r = screenBounds[i];
                if (!r.Contains(x, y)) continue;
                if (x - r.Left <= m || r.Right - x <= m || y - r.Top <= m || r.Bottom - y <= m)
                    return true;
            }
            return false;
        }

        internal static bool IsShellOrImeClassName(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            return className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                || className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
                || className.Equals("TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase)
                || className.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase)
                || className.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase)
                || className.Equals("#32768", StringComparison.Ordinal)
                || className.Equals("MSTaskListWClass", StringComparison.OrdinalIgnoreCase)
                || className.Equals("DV2ControlHost", StringComparison.OrdinalIgnoreCase)
                || className.Equals("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase)
                || className.IndexOf("IME", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Sogou", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("WeType", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Candidate", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Weasel", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("QQPinyin", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("Baidu", StringComparison.OrdinalIgnoreCase) >= 0
                || className.IndexOf("InputMethod", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Rectangle[] ExtractScreenBounds(Screen[] screens)
        {
            var result = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) result[i] = screens[i].Bounds;
            return result;
        }

        private List<PrivacyCurtainWindow> CreateCurtainWindows(Screen[] screens, bool showEntryReminder)
        {
            var created = new List<PrivacyCurtainWindow>(screens.Length);
            try
            {
                foreach (Screen screen in screens)
                {
                    var win = new PrivacyCurtainWindow(screen.Bounds, currentConfig, cachedImage, previewMode, showEntryReminder);
                    created.Add(win);
                    if (previewMode) win.ExitRequested += delegate { Deactivate(false); };
                    win.Show();
                }
                return created;
            }
            catch
            {
                CloseCurtainWindows(created);
                throw;
            }
        }

        private static void CloseCurtainWindows(List<PrivacyCurtainWindow> curtainWindows)
        {
            foreach (var win in curtainWindows)
            {
                try { win.PrepareForClose(); } catch { }
                try { win.Hide(); } catch { }
                try { win.Close(); } catch { }
                try { win.Dispose(); } catch { }
            }
        }

        internal void Deactivate(bool triggerLockIfConfigured)
        {
            bool shouldLock = triggerLockIfConfigured && active && !previewMode && currentConfig != null && currentConfig.LockOnLocalExit;
            activationEpoch++;
            active = false;
            previewMode = false;
            cursorRestoreTimer.Stop();
            UnhookRemoteInput();

            try
            {
                InvokeOnWindowThread(StopCurtainWindows);
            }
            catch { }
            finally
            {
                CurtainWindowThread thread = windowThread;
                windowThread = null;
                try { if (thread != null) thread.Dispose(); } catch { }
                try { RestoreCursorMask(); } catch { }
                try { BoBoCoreInterop.SetPrivacyInputBlock(false, null); } catch { }
                try { PrivacyDisplayDimmer.RestoreAllAsync(); } catch { }
            }
            RaiseStateChanged();

            if (shouldLock)
            {
                try { LockWorkStation(); } catch { }
            }
        }

        private void UnhookRemoteInput()
        {
            Action<BoBoInputPacket> hIn = remoteInputHandler;
            if (hIn != null)
            {
                if (BoBoCoreInterop.RemoteInputHook == hIn)
                    BoBoCoreInterop.RemoteInputHook = null;
                remoteInputHandler = null;
            }
            Action hTxt = remoteTextInputHandler;
            if (hTxt != null)
            {
                if (BoBoCoreInterop.RemoteTextInputHook == hTxt)
                    BoBoCoreInterop.RemoteTextInputHook = null;
                remoteTextInputHandler = null;
            }
        }

        internal bool SetLocalInputBlock(bool enable)
        {
            if (currentConfig != null) currentConfig.BlockLocalInput = enable;
            if (!active || previewMode || disposed) return false;
            return BoBoCoreInterop.SetPrivacyInputBlock(enable, enable ? (Action)OnNativeLocalEscapeTriggered : null);
        }

        private void InvokeOnWindowThread(Action action)
        {
            if (windowThread == null) action();
            else windowThread.Invoke(action);
        }

        private void StopCurtainWindows()
        {
            callbackAnchor = null;
            UnhookRemoteInput();
            if (windowThread != null) windowThread.SetPulseHandler(null);
            if (previewTimer != null)
            {
                previewTimer.Dispose();
                previewTimer = null;
            }
            UninstallWinEventHooks();
            if (displayEventsHooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                displayEventsHooked = false;
            }
            CloseCurtainWindows(windows);
            windows.Clear();
            cachedScreenBounds = new Rectangle[0];
            DisposeImage();
        }

        private void RestoreCursorMask()
        {
            cursorRestoreTimer.Enabled = !BoBoCoreInterop.SetPrivacyCursorMask(false) && !disposed;
        }

        private void OnNativeLocalEscapeTriggered()
        {
            if (disposed || !active || previewMode) return;
            int capturedEpoch = activationEpoch;
            Action run = delegate
            {
                if (disposed || !active || previewMode || capturedEpoch != activationEpoch) return;
                Action escapeHandler = LocalEscapeRequested;
                if (escapeHandler != null) escapeHandler();
                else Deactivate(true);
            };
            if (TryPost(owner, run)) return;
            if (capturedEpoch != activationEpoch) return;
            // Emergency fallback for a host whose UI window has already closed.
            try { BoBoCoreInterop.SetPrivacyCursorMask(false); } catch { }
            try { BoBoCoreInterop.SetPrivacyInputBlock(false, null); } catch { }
            try { PrivacyDisplayDimmer.RestoreAllAsync(); } catch { }
        }

        private static bool TryPost(Control anchor, Action action)
        {
            try
            {
                if (anchor != null && !anchor.IsDisposed && anchor.IsHandleCreated)
                {
                    anchor.BeginInvoke(action);
                    return true;
                }
            }
            catch (InvalidOperationException) { }
            return false;
        }

        private void InstallWinEventHooks()
        {
            UninstallWinEventHooks();
            try
            {
                winEventCallback = OnWinEventReceived;
                winEventHookSys = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_SWITCHEND,
                    IntPtr.Zero,
                    winEventCallback,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT);
                winEventHookObj = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_VALUECHANGE,
                    IntPtr.Zero,
                    winEventCallback,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT);
            }
            catch { }
        }

        private void UninstallWinEventHooks()
        {
            if (winEventHookSys != IntPtr.Zero)
            {
                try { UnhookWinEvent(winEventHookSys); } catch { }
                winEventHookSys = IntPtr.Zero;
            }
            if (winEventHookObj != IntPtr.Zero)
            {
                try { UnhookWinEvent(winEventHookObj); } catch { }
                winEventHookObj = IntPtr.Zero;
            }
            winEventCallback = null;
        }

        private void OnWinEventReceived(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (!active || previewMode || disposed || reassertingCurtain) return;

            IntPtr root = hwnd != IntPtr.Zero ? GetAncestor(hwnd, GA_ROOT) : IntPtr.Zero;
            if (root == IntPtr.Zero) root = hwnd;
            if (root != IntPtr.Zero && IsCurtainWindow(root)) return;

            bool rootTopMost = root != IntPtr.Zero && (GetWindowLong(root, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            IntPtr pendingWindow = IntPtr.Zero;

            if (hWinEventHook == winEventHookSys)
            {
                if (rootTopMost) pendingWindow = root;
                CurtainWindowThread wt = windowThread;
                if (wt != null) wt.SignalBurst(300);
            }
            else if (hWinEventHook == winEventHookObj)
            {
                if (eventType != EVENT_OBJECT_CREATE &&
                    eventType != EVENT_OBJECT_SHOW &&
                    eventType != EVENT_OBJECT_REORDER &&
                    eventType != EVENT_OBJECT_FOCUS &&
                    eventType != EVENT_OBJECT_SELECTION &&
                    eventType != EVENT_OBJECT_STATECHANGE &&
                    eventType != EVENT_OBJECT_LOCATIONCHANGE &&
                    eventType != EVENT_OBJECT_NAMECHANGE &&
                    eventType != EVENT_OBJECT_VALUECHANGE)
                {
                    return;
                }

                if (rootTopMost)
                {
                    pendingWindow = root;
                    CurtainWindowThread wt = windowThread;
                    if (wt != null) wt.SignalBurst(320);
                }
                else if (eventType == EVENT_OBJECT_SHOW ||
                    eventType == EVENT_OBJECT_REORDER ||
                    eventType == EVENT_OBJECT_FOCUS ||
                    idObject == OBJID_CARET)
                {
                    if (idObject == OBJID_CARET)
                    {
                        CurtainWindowThread wt = windowThread;
                        if (wt != null) wt.SignalBurst(240);
                    }
                }
                else
                {
                    return;
                }
            }
            else
            {
                return;
            }

            EnsureAllWindowsTopMostNoActivate(pendingWindow);
        }

        private bool IsCurtainWindow(IntPtr hwnd)
        {
            foreach (PrivacyCurtainWindow win in windows)
            {
                if (win.IsHandleCreated && win.Handle == hwnd) return true;
            }
            return false;
        }

        private void EnsureAllWindowsTopMostNoActivate(IntPtr pendingWindow = default(IntPtr))
        {
            if (reassertingCurtain) return;
            reassertingCurtain = true;
            try
            {
                foreach (PrivacyCurtainWindow win in windows)
                {
                    if (win.IsDisposed || !win.IsHandleCreated) continue;
                    bool hidden = !IsWindowVisible(win.Handle);
                    if (hidden || HasWindowAboveCurtain(win, pendingWindow))
                    {
                        win.EnsureTopMostNoActivate(hidden, false);
                        if (HasWindowAboveCurtain(win, pendingWindow))
                            win.EnsureTopMostNoActivate(hidden, true);
                    }
                }
            }
            finally { reassertingCurtain = false; }
        }

        private bool HasWindowAboveCurtain(PrivacyCurtainWindow curtain, IntPtr pendingWindow)
        {
            Rectangle bounds = curtain.Bounds;
            uint curtainBand;
            if (!TryGetWindowBand(curtain.Handle, out curtainBand)) curtainBand = 1;
            IntPtr hwnd = GetWindow(curtain.Handle, GW_HWNDPREV);
            // Bound link traversal because cross-process windows can exit mid-enumeration.
            for (int visited = 0; hwnd != IntPtr.Zero && visited < 128; visited++)
            {
                if (!IsCurtainWindow(hwnd))
                {
                    uint otherBand;
                    if (!TryGetWindowBand(hwnd, out otherBand) || otherBand <= curtainBand)
                    {
                        NativeRect rect;
                        if (hwnd == pendingWindow)
                        {
                            if (!GetWindowRect(hwnd, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top ||
                                bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)))
                                return true;
                        }
                        else if (IsWindowVisible(hwnd) && GetWindowRect(hwnd, out rect) &&
                            bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)))
                        {
                            int cloaked;
                            bool uncloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0 || cloaked == 0;
                            if (uncloaked || ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0 && IsShellOrImeWindow(hwnd)))
                                return true;
                        }
                    }
                }
                hwnd = GetWindow(hwnd, GW_HWNDPREV);
            }
            return hwnd != IntPtr.Zero;
        }

        private static bool IsShellOrImeWindow(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(128);
                int len = GetClassName(hwnd, sb, sb.Capacity);
                if (len <= 0) return false;
                return IsShellOrImeClassName(sb.ToString());
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetWindowBand(IntPtr hwnd, out uint band)
        {
            band = 1;
            try
            {
                return GetWindowBand(hwnd, out band);
            }
            catch (EntryPointNotFoundException)
            {
                band = 1;
                return false;
            }
            catch
            {
                band = 1;
                return false;
            }
        }

        private void ReloadImage()
        {
            DisposeImage();
            if (currentConfig != null && currentConfig.Preset == PrivacyWallpaperConfig.PresetCustomImage)
            {
                cachedImage = PrivacyWallpaper.LoadImageUnlocked(currentConfig.CustomImagePath);
            }
        }

        private void DisposeImage()
        {
            if (cachedImage != null)
            {
                try { cachedImage.Dispose(); } catch { }
                cachedImage = null;
            }
        }

        private void KeepOnTopAndRefreshClock()
        {
            if (!active) return;
            if (!previewMode) EnsureAllWindowsTopMostNoActivate();
            int now = Environment.TickCount;
            if (unchecked(now - lastClockTick) < 1000) return;
            lastClockTick = now;
            foreach (PrivacyCurtainWindow win in windows)
            {
                if (!win.IsDisposed) win.Invalidate();
            }
        }

        private void RebuildCurtainWindowsForDisplayChange()
        {
            if (!active || disposed) return;
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return;

            List<PrivacyCurtainWindow> replacement;
            try { replacement = CreateCurtainWindows(screens, false); }
            catch { return; }
            var previous = new List<PrivacyCurtainWindow>(windows);
            windows.Clear();
            windows.AddRange(replacement);
            cachedScreenBounds = ExtractScreenBounds(screens);
            callbackAnchor = windows[0];
            CloseCurtainWindows(previous);
            if (!previewMode)
            {
                EnsureAllWindowsTopMostNoActivate();
                ReassertCursorMaskOnUiThread();
            }
        }

        private void ReassertCursorMaskOnUiThread()
        {
            if (previewMode || !active || disposed) return;
            if (windowThread == null)
            {
                BoBoCoreInterop.SetPrivacyCursorMask(true);
                return;
            }
            TryPost(owner, delegate
            {
                if (active && !previewMode && !disposed) BoBoCoreInterop.SetPrivacyCursorMask(true);
            });
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!active || disposed) return;
            int capturedEpoch = activationEpoch;
            TryPost(callbackAnchor, delegate
            {
                if (capturedEpoch == activationEpoch) RebuildCurtainWindowsForDisplayChange();
            });
        }

        private void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null) handler();
        }

        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Deactivate(false);
            cursorRestoreTimer.Dispose();
        }

        private sealed class CurtainWindowThread : IDisposable
        {
            private const uint PM_REMOVE = 0x0001;
            private const uint WM_QUIT = 0x0012;
            private const uint QS_ALLINPUT = 0x04FF;
            private const uint MWMO_INPUTAVAILABLE = 0x0004;
            private const uint WAIT_OBJECT_0 = 0x00000000;

            private readonly Thread thread;
            private readonly Control dispatcher;
            private readonly IntPtr wakeEvent;
            private volatile bool exitRequested;
            private volatile bool exited;
            private volatile Action pulseHandler;
            private long burstUntilTicks;

            internal CurtainWindowThread()
            {
                wakeEvent = CreateEvent(IntPtr.Zero, false, false, null);
                var started = new TaskCompletionSource<Control>();
                thread = new Thread(delegate()
                {
                    bool highResPeriod = timeBeginPeriod(1) == 0;
                    try
                    {
                        using (var control = new Control())
                        {
                            if (control.Handle == IntPtr.Zero) throw new InvalidOperationException();
                            started.SetResult(control);
                            RunMessageAndGuardLoop();
                        }
                    }
                    catch (Exception error)
                    {
                        started.TrySetException(error);
                    }
                    finally
                    {
                        if (highResPeriod) timeEndPeriod(1);
                        exited = true;
                    }
                }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "BoBo Privacy Curtain" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                dispatcher = started.Task.GetAwaiter().GetResult();
            }

            internal void SetPulseHandler(Action handler)
            {
                pulseHandler = handler;
                if (wakeEvent != IntPtr.Zero) SetEvent(wakeEvent);
            }

            internal void SignalBurst(int durationMs)
            {
                if (exitRequested || exited) return;
                long until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * Math.Max(20, durationMs)) / 1000;
                long cur = Interlocked.Read(ref burstUntilTicks);
                while (until > cur)
                {
                    long prev = Interlocked.CompareExchange(ref burstUntilTicks, until, cur);
                    if (prev == cur) break;
                    cur = prev;
                }
                if (wakeEvent != IntPtr.Zero) SetEvent(wakeEvent);
            }

            internal void Invoke(Action action)
            {
                if (action == null || exited || dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
                try
                {
                    dispatcher.Invoke(action);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }

            private void RunMessageAndGuardLoop()
            {
                IntPtr[] waitHandles = wakeEvent != IntPtr.Zero ? new[] { wakeEvent } : null;
                long lastPulseTicks = 0;
                bool wokeFromEvent = false;
                NativeMsg msg;

                while (!exitRequested)
                {
                    while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        if (msg.message == WM_QUIT)
                        {
                            exitRequested = true;
                            break;
                        }
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                    if (exitRequested) break;

                    long now = Stopwatch.GetTimestamp();
                    bool inBurst = now < Interlocked.Read(ref burstUntilTicks);
                    int cadenceMs = inBurst ? 2 : 8;
                    long minGapTicks = (Stopwatch.Frequency * cadenceMs) / 1000;

                    Action handler = pulseHandler;
                    if (handler != null && (wokeFromEvent || (now - lastPulseTicks) >= minGapTicks))
                    {
                        wokeFromEvent = false;
                        lastPulseTicks = now;
                        try { handler(); } catch { }
                        now = Stopwatch.GetTimestamp();
                        inBurst = now < Interlocked.Read(ref burstUntilTicks);
                        cadenceMs = inBurst ? 2 : 8;
                        minGapTicks = (Stopwatch.Frequency * cadenceMs) / 1000;
                    }
                    else
                    {
                        wokeFromEvent = false;
                    }

                    uint waitMs = 50;
                    if (handler != null)
                    {
                        long elapsed = Math.Max(0, now - lastPulseTicks);
                        long remMs = Math.Max(1, ((minGapTicks - elapsed) * 1000) / Math.Max(1L, Stopwatch.Frequency));
                        waitMs = (uint)Math.Max(1L, Math.Min((long)cadenceMs, remMs));
                    }

                    if (waitHandles != null)
                    {
                        uint wr = MsgWaitForMultipleObjectsEx(1, waitHandles, waitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                        wokeFromEvent = (wr == WAIT_OBJECT_0);
                    }
                    else
                    {
                        Thread.Sleep((int)waitMs);
                    }
                }
            }

            public void Dispose()
            {
                exitRequested = true;
                pulseHandler = null;
                if (wakeEvent != IntPtr.Zero) SetEvent(wakeEvent);
                try
                {
                    if (!exited && !dispatcher.IsDisposed && dispatcher.IsHandleCreated)
                    {
                        dispatcher.BeginInvoke(new Action(delegate
                        {
                            exitRequested = true;
                            PostQuitMessage(0);
                        }));
                    }
                }
                catch { }
                try
                {
                    if (thread.IsAlive && Thread.CurrentThread != thread)
                        thread.Join(2000);
                }
                catch { }
                if (wakeEvent != IntPtr.Zero)
                {
                    try { CloseHandle(wakeEvent); } catch { }
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeMsg
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam;
                public IntPtr lParam;
                public uint time;
                public int ptX;
                public int ptY;
            }

            [DllImport("winmm.dll")]
            private static extern uint timeBeginPeriod(uint period);

            [DllImport("winmm.dll")]
            private static extern uint timeEndPeriod(uint period);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetEvent(IntPtr hEvent);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hObject);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

            [DllImport("user32.dll")]
            private static extern bool TranslateMessage(ref NativeMsg lpMsg);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr DispatchMessage(ref NativeMsg lpMsg);

            [DllImport("user32.dll")]
            private static extern void PostQuitMessage(int nExitCode);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
        }
    }

    internal sealed class PrivacyCurtainWindow : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private const double EntryToastHoldMs = 3600.0;
        private const double EntryToastTotalMs = 4400.0;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private readonly Rectangle monitorBounds;
        private readonly PrivacyWallpaperConfig config;
        private readonly Image customImage;
        private readonly bool previewMode;
        private readonly bool showEntryReminder;
        private readonly DateTime shownAtUtc;
        private Timer entryToastTimer;
        private float entryToastOpacity;
        private volatile bool allowCloseOrDispose;
        internal bool CaptureExcluded { get; private set; }
        internal event Action ExitRequested;

        internal PrivacyCurtainWindow(Rectangle monitorBounds, PrivacyWallpaperConfig config, Image customImage, bool previewMode, bool showEntryReminder)
        {
            this.monitorBounds = monitorBounds;
            this.config = config;
            this.customImage = customImage;
            this.previewMode = previewMode;
            this.showEntryReminder = showEntryReminder && !previewMode;
            shownAtUtc = DateTime.UtcNow;
            entryToastOpacity = this.showEntryReminder ? 1f : 0f;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = monitorBounds;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            if (previewMode)
            {
                Cursor = Cursors.Hand;
                MouseDown += delegate { RequestExit(); };
                KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
                        RequestExit();
                };
            }
            else if (this.showEntryReminder)
            {
                entryToastTimer = new Timer { Interval = 33 };
                entryToastTimer.Tick += delegate { UpdateEntryToastFade(); };
                entryToastTimer.Start();
            }
        }

        internal void PrepareForClose()
        {
            allowCloseOrDispose = true;
        }

        private void UpdateEntryToastFade()
        {
            if (IsDisposed)
            {
                StopEntryToastTimer();
                return;
            }
            double elapsed = (DateTime.UtcNow - shownAtUtc).TotalMilliseconds;
            if (elapsed <= EntryToastHoldMs)
            {
                entryToastOpacity = 1f;
                return;
            }
            if (elapsed >= EntryToastTotalMs)
            {
                entryToastOpacity = 0f;
                StopEntryToastTimer();
                Invalidate();
                return;
            }
            entryToastOpacity = (float)Math.Max(0.0, 1.0 - (elapsed - EntryToastHoldMs) / (EntryToastTotalMs - EntryToastHoldMs));
            Invalidate();
        }

        private void StopEntryToastTimer()
        {
            if (entryToastTimer != null)
            {
                try { entryToastTimer.Stop(); entryToastTimer.Dispose(); } catch { }
                entryToastTimer = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            allowCloseOrDispose = true;
            if (disposing) StopEntryToastTimer();
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation
        {
            get { return !previewMode; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                if (!previewMode)
                {
                    cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                }
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (!previewMode && !allowCloseOrDispose && m.Msg == WM_WINDOWPOSCHANGING && m.LParam != IntPtr.Zero)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                if ((wp.flags & SWP_NOZORDER) == 0 && wp.hwndInsertAfter != HWND_TOPMOST)
                {
                    wp.hwndInsertAfter = HWND_TOPMOST;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!previewMode)
            {
                SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
                ApplyCaptureExclusion();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!previewMode)
            {
                ApplyCaptureExclusion();
                EnsureTopMostNoActivate();
            }
            else
            {
                Activate();
            }
        }

        private void ApplyCaptureExclusion()
        {
            // Before build 19041 Windows interprets 0x11 as WDA_MONITOR.
            uint affinity;
            CaptureExcluded = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE) &&
                Environment.OSVersion.Version.Build >= 19041 &&
                GetWindowDisplayAffinity(Handle, out affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
        }

        internal void EnsureTopMostNoActivate(bool show = false, bool forceNoSendChangingBypass = false)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!forceNoSendChangingBypass)
            {
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING;
                if (show) flags |= SWP_SHOWWINDOW;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
            }
            else
            {
                uint flags = SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0u);
                SetWindowPos(Handle, HWND_TOPMOST,
                    monitorBounds.X, monitorBounds.Y, monitorBounds.Width, monitorBounds.Height, flags);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 全部在 OnPaint 中双缓冲绘制，避免闪烁
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PrivacyWallpaper.Render(e.Graphics, ClientRectangle, config, customImage, previewMode);
            if (!previewMode && entryToastOpacity > 0.01f)
            {
                PrivacyWallpaper.RenderEntryToast(e.Graphics, ClientRectangle, entryToastOpacity,
                    config != null && config.LockOnLocalExit);
            }
        }

        private void RequestExit()
        {
            var handler = ExitRequested;
            if (handler != null) handler();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
