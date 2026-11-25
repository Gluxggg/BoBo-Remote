using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DualRemote
{
    /// <summary>
    /// 管理多维硬件+系统级全屏防窥保护（覆盖所有物理显示器）：
    /// 1. 幕布层：使用 WDA_EXCLUDEFROMCAPTURE（0x11）覆盖所有屏幕，远控采集透明无干扰；
    /// 2. 0ms Z 序守护层：通过 SetWinEventHook 实时监听前台切换/右键菜单/输入法弹出并瞬间压回最顶层（配合 50ms 保底脉冲）；
    /// 3. 物理键鼠锁定层：开启原生 WH_MOUSE_LL + WH_KEYBOARD_LL 钩子，100% 屏蔽被控端物理鼠标点击任务栏/开始页/刷新及键盘按键，
    ///    仅放行 BoBo 远控专属签名事件及紧急退出组合键 Ctrl+Alt+Shift+P；
    /// 4. 可选显示器硬件背光降亮层：用户显式开启后，通过 DDC/CI（dxva2.dll）仅将非 HDR 物理显示器背光亮度（VCP 0x10）调至最低，
    ///    绝不修改对比度与 RGB 增益（避免退出后偏色或过曝），退出/锁屏/异常重启均经回读校验精确恢复。
    /// </summary>
    internal sealed class PrivacyCurtainManager : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0007;
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int OBJID_WINDOW = 0;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        private readonly List<PrivacyCurtainWindow> windows = new List<PrivacyCurtainWindow>();
        private readonly Timer refreshTimer = new Timer();
        private PrivacyWallpaperConfig currentConfig;
        private Bitmap cachedImage;
        private bool active;
        private bool previewMode;
        private bool displayEventsHooked;
        private int activationEpoch;
        private int clockTickDivider;
        private int lastZOrderTick;
        private IntPtr winEventHookSys;
        private IntPtr winEventHookObj;
        private WinEventDelegate winEventCallback;
        private bool disposed;

        internal event Action StateChanged;
        internal event Action LocalEscapeRequested;

        internal bool IsActive { get { return active; } }
        internal bool IsPreview { get { return active && previewMode; } }

        internal PrivacyCurtainManager()
        {
            refreshTimer.Interval = 50;
            refreshTimer.Tick += delegate { KeepOnTopAndRefreshClock(); };
        }

        internal bool Activate(PrivacyWallpaperConfig config, bool preview, out string statusMessage)
        {
            return ActivateInternal(config, preview, true, out statusMessage);
        }

        private bool ActivateInternal(PrivacyWallpaperConfig config, bool preview, bool showEntryReminder, out string statusMessage)
        {
            statusMessage = null;
            Deactivate(false);
            activationEpoch++;
            clockTickDivider = 0;
            currentConfig = config ?? new PrivacyWallpaperConfig();
            previewMode = preview;
            ReloadImage();

            Screen[] screens = Screen.AllScreens;
            if (screens == null || screens.Length == 0)
            {
                statusMessage = "未检测到可用显示器。";
                return false;
            }

            bool allExcluded = true;
            foreach (Screen screen in screens)
            {
                var win = new PrivacyCurtainWindow(screen.Bounds, currentConfig, cachedImage, preview, showEntryReminder);
                if (preview)
                {
                    win.ExitRequested += delegate { Deactivate(false); };
                }
                windows.Add(win);
                win.Show();
                if (!preview && !win.CaptureExcluded) allExcluded = false;
            }

            active = windows.Count > 0;
            if (active)
            {
                if (!displayEventsHooked)
                {
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    displayEventsHooked = true;
                }

                bool inputBlocked = false;
                bool hwBlackoutTriggered = false;
                if (!preview)
                {
                    InstallWinEventHooks();
                    if (currentConfig.BlockLocalInput)
                    {
                        inputBlocked = BoBoCoreInterop.SetPrivacyInputBlock(true, OnNativeLocalEscapeTriggered);
                    }
                    if (currentConfig.HardwareBlackout)
                    {
                        hwBlackoutTriggered = true;
                        PrivacyDisplayDimmer.DimAllMonitorsAsync(null);
                    }
                }

                refreshTimer.Start();
                if (preview)
                {
                    statusMessage = "正在全屏预览自定义防窥壁纸（点击任意处或按 Esc 退出）。";
                }
                else if (allExcluded)
                {
                    string extra = (inputBlocked ? " · 已锁物理键鼠" : "") + (hwBlackoutTriggered ? " · 背光降亮" : "");
                    statusMessage = "完全防窥保护已覆盖 " + windows.Count + " 块屏幕（物理屏彻底屏蔽，远控透明无干扰" + extra + "；按 Ctrl+Alt+Shift+P 退出）。";
                }
                else
                {
                    statusMessage = "防窥保护已开启（物理键鼠已锁定；建议升级 Windows 10 2004+ 以启用捕获排除标记）。";
                }
            }
            RaiseStateChanged();
            return active;
        }

        internal void Deactivate(bool triggerLockIfConfigured)
        {
            bool wasActiveNonPreview = active && !previewMode;
            bool shouldLock = triggerLockIfConfigured && wasActiveNonPreview && currentConfig != null && currentConfig.LockOnLocalExit;

            // 先递增代次、切换状态、解除物理输入锁定、恢复显示器硬件亮度、停定时器并解绑事件，绝不残留黑屏或锁鼠
            activationEpoch++;
            active = false;
            previewMode = false;
            refreshTimer.Stop();
            UninstallWinEventHooks();
            try { BoBoCoreInterop.SetPrivacyInputBlock(false, null); } catch { }
            try { PrivacyDisplayDimmer.RestoreAllAsync(); } catch { }

            if (displayEventsHooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                displayEventsHooked = false;
            }

            List<PrivacyCurtainWindow> snapshot = new List<PrivacyCurtainWindow>(windows);
            windows.Clear();
            foreach (var win in snapshot)
            {
                try { win.Hide(); } catch { }
                try { win.Close(); } catch { }
                try { win.Dispose(); } catch { }
            }
            DisposeImage();
            RaiseStateChanged();

            if (shouldLock)
            {
                try { NativeLockWorkStation(); } catch { }
            }
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
            try
            {
                if (windows.Count > 0 && !windows[0].IsDisposed && windows[0].IsHandleCreated)
                {
                    windows[0].BeginInvoke(run);
                    return;
                }
            }
            catch { }
            // Never manipulate WinForms UI controls directly on the native escape thread!
            // First release hardware input lock & display dimming immediately (thread-safe), then try marshaling UI teardown.
            try { BoBoCoreInterop.SetPrivacyInputBlock(false, null); } catch { }
            try { PrivacyDisplayDimmer.RestoreAllAsync(); } catch { }
            try
            {
                foreach (Form f in Application.OpenForms)
                {
                    if (f != null && !f.IsDisposed && f.IsHandleCreated)
                    {
                        f.BeginInvoke(run);
                        return;
                    }
                }
            }
            catch { }
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
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                winEventHookObj = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_FOCUS,
                    IntPtr.Zero,
                    winEventCallback,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
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
            if (!active || previewMode || disposed) return;
            if (idObject != OBJID_WINDOW) return;
            int now = Environment.TickCount;
            if (unchecked(now - lastZOrderTick) < 8) return;
            lastZOrderTick = now;
            EnsureAllWindowsTopMostNoActivate();
        }

        private void EnsureAllWindowsTopMostNoActivate()
        {
            for (int i = 0; i < windows.Count; i++)
            {
                PrivacyCurtainWindow win = windows[i];
                if (win != null && !win.IsDisposed)
                {
                    win.EnsureTopMostNoActivate();
                }
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
            clockTickDivider++;
            bool refreshClock = (clockTickDivider >= 20);
            if (refreshClock) clockTickDivider = 0;

            for (int i = 0; i < windows.Count; i++)
            {
                PrivacyCurtainWindow win = windows[i];
                if (win == null || win.IsDisposed) continue;
                if (!previewMode) win.EnsureTopMostNoActivate();
                if (refreshClock) win.Invalidate();
            }
        }

        private void RebuildCurtainWindowsForDisplayChange()
        {
            if (!active || disposed) return;
            Screen[] screens = Screen.AllScreens;
            if (screens == null || screens.Length == 0) return;

            List<PrivacyCurtainWindow> oldWindows = new List<PrivacyCurtainWindow>(windows);
            windows.Clear();
            foreach (Screen screen in screens)
            {
                var win = new PrivacyCurtainWindow(screen.Bounds, currentConfig, cachedImage, previewMode, false);
                if (previewMode)
                {
                    win.ExitRequested += delegate { Deactivate(false); };
                }
                windows.Add(win);
                win.Show();
            }
            foreach (var oldWin in oldWindows)
            {
                try { oldWin.Hide(); } catch { }
                try { oldWin.Close(); } catch { }
                try { oldWin.Dispose(); } catch { }
            }
            if (!previewMode)
            {
                EnsureAllWindowsTopMostNoActivate();
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!active || disposed || windows.Count == 0) return;
            int capturedEpoch = activationEpoch;
            try
            {
                Control anchor = windows[0];
                if (!anchor.IsDisposed && anchor.IsHandleCreated)
                {
                    anchor.BeginInvoke(new Action(delegate
                    {
                        if (!active || disposed || capturedEpoch != activationEpoch || windows.Count == 0) return;
                        RebuildCurtainWindowsForDisplayChange();
                    }));
                }
            }
            catch { }
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

        private static void NativeLockWorkStation()
        {
            LockWorkStation();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Deactivate(false);
            refreshTimer.Dispose();
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
                CaptureExcluded = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!previewMode)
            {
                CaptureExcluded = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
                EnsureTopMostNoActivate();
            }
            else
            {
                Activate();
            }
        }

        internal void EnsureTopMostNoActivate()
        {
            if (IsDisposed || !IsHandleCreated) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
