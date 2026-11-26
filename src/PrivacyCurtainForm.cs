using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
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
        private const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0007;
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_REORDER = 0x8004;
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint GA_ROOT = 2;
        private const uint GW_HWNDPREV = 3;
        private const int DWMWA_CLOAKED = 14;
        private const int CurtainRefreshMs = 16;
        private const int CursorRestoreRetryMs = 250;

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
        private Timer curtainTimer;
        private CurtainWindowThread windowThread;
        private PrivacyWallpaperConfig currentConfig;
        private Bitmap cachedImage;
        private volatile bool active;
        private volatile bool previewMode;
        private volatile Control callbackAnchor;
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
            bool cursorMasked = false;
            bool inputBlocked = false;
            bool allExcluded = false;
            int screenCount = 0;
            try
            {
                // Keep Magnification cursor masking on the persistent UI thread while
                // keeping the live curtain windows and WinEvent hooks on their own STA thread.
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
                    }
                    curtainTimer = new Timer { Interval = preview ? 1000 : CurtainRefreshMs };
                    curtainTimer.Tick += delegate { KeepOnTopAndRefreshClock(); };
                    curtainTimer.Start();
                    allExcluded = windows.TrueForAll(win => win.CaptureExcluded);
                    screenCount = windows.Count;
                });
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
                statusMessage = string.Join(" · ", status) + "。系统弹窗和指针遮挡取决于 Windows 与显示驱动。按 Ctrl+Alt+Shift+P 退出。";
            }
            RaiseStateChanged();
            return true;
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
            if (curtainTimer != null)
            {
                curtainTimer.Dispose();
                curtainTimer = null;
            }
            UninstallWinEventHooks();
            if (displayEventsHooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                displayEventsHooked = false;
            }
            CloseCurtainWindows(windows);
            windows.Clear();
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
                    EVENT_SYSTEM_MENUPOPUPEND,
                    IntPtr.Zero,
                    winEventCallback,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT);
                winEventHookObj = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_LOCATIONCHANGE,
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
            IntPtr pendingWindow = IntPtr.Zero;
            if (hWinEventHook == winEventHookObj)
            {
                // Accept client-object show/focus events as some IMEs reuse an existing HWND.
                // A new or repositioned topmost window may already be in the Z order while hidden.
                if (eventType == EVENT_OBJECT_CREATE || eventType == EVENT_OBJECT_LOCATIONCHANGE)
                {
                    if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0 ||
                        GetAncestor(hwnd, GA_ROOT) != hwnd || (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) return;
                    pendingWindow = hwnd;
                }
                else if (eventType != EVENT_OBJECT_SHOW && eventType != EVENT_OBJECT_REORDER &&
                    eventType != EVENT_OBJECT_FOCUS) return;
            }
            else if (hWinEventHook != winEventHookSys) return;

            if (hwnd != IntPtr.Zero && IsCurtainWindow(GetAncestor(hwnd, GA_ROOT))) return;
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
                        win.EnsureTopMostNoActivate(hidden);
                }
            }
            finally { reassertingCurtain = false; }
        }

        private bool HasWindowAboveCurtain(PrivacyCurtainWindow curtain, IntPtr pendingWindow)
        {
            Rectangle bounds = curtain.Bounds;
            IntPtr hwnd = GetWindow(curtain.Handle, GW_HWNDPREV);
            // Bound the link traversal because windows in other processes can disappear during enumeration.
            for (int visited = 0; hwnd != IntPtr.Zero && visited < 128; visited++)
            {
                NativeRect rect;
                if (hwnd == pendingWindow)
                {
                    // The candidate can receive its first bounds after the creation notification.
                    if (!GetWindowRect(hwnd, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top ||
                        bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom))) return true;
                }
                else if (IsWindowVisible(hwnd) && !IsCurtainWindow(hwnd) && GetWindowRect(hwnd, out rect) &&
                    bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)))
                {
                    int cloaked;
                    if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0 || cloaked == 0)
                        return true;
                }
                hwnd = GetWindow(hwnd, GW_HWNDPREV);
            }
            return hwnd != IntPtr.Zero;
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

            // Install the new coverage before releasing the previous windows.
            List<PrivacyCurtainWindow> replacement;
            try { replacement = CreateCurtainWindows(screens, false); }
            catch { return; }
            var previous = new List<PrivacyCurtainWindow>(windows);
            windows.Clear();
            windows.AddRange(replacement);
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
            private readonly Thread thread;
            private readonly Control dispatcher;
            private volatile bool exited;

            internal CurtainWindowThread()
            {
                var started = new TaskCompletionSource<Control>();
                thread = new Thread(delegate()
                {
                    try
                    {
                        using (var control = new Control())
                        {
                            if (control.Handle == IntPtr.Zero) throw new InvalidOperationException();
                            started.SetResult(control);
                            Application.Run();
                        }
                    }
                    catch (Exception error)
                    {
                        started.TrySetException(error);
                    }
                    finally
                    {
                        exited = true;
                    }
                }) { IsBackground = true, Name = "BoBo Privacy Curtain" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                dispatcher = started.Task.GetAwaiter().GetResult();
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

            public void Dispose()
            {
                try
                {
                    if (!exited && !dispatcher.IsDisposed && dispatcher.IsHandleCreated)
                        dispatcher.BeginInvoke(new Action(Application.ExitThread));
                }
                catch { }
                try
                {
                    if (thread.IsAlive && Thread.CurrentThread != thread)
                        thread.Join(2000);
                }
                catch { }
            }
        }
    }

    internal sealed class PrivacyCurtainWindow : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private const double EntryToastHoldMs = 3600.0;
        private const double EntryToastTotalMs = 4400.0;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private readonly PrivacyWallpaperConfig config;
        private readonly Image customImage;
        private readonly bool previewMode;
        private readonly bool showEntryReminder;
        private readonly DateTime shownAtUtc;
        private Timer entryToastTimer;
        private float entryToastOpacity;
        internal bool CaptureExcluded { get; private set; }
        internal event Action ExitRequested;

        internal PrivacyCurtainWindow(Rectangle monitorBounds, PrivacyWallpaperConfig config, Image customImage, bool previewMode, bool showEntryReminder)
        {
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

        internal void EnsureTopMostNoActivate(bool show = false)
        {
            if (IsDisposed || !IsHandleCreated) return;
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING;
            if (show) flags |= SWP_SHOWWINDOW;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
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
