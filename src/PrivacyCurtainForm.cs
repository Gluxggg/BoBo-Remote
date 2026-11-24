using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DualRemote
{
    /// <summary>
    /// 管理全屏防窥屏壁纸幕布（覆盖所有物理显示器）：
    /// - 正常防窥模式：使用 WDA_EXCLUDEFROMCAPTURE（0x11）使幕布仅在被控端物理屏幕上完全不透明显示，
    ///   对 Sunshine / Moonlight / RustDesk 等 DXGI/WGC 捕获完全透明，并启用点击穿透，远控零干扰。
    /// - 预览模式：允许点击任意位置或按 Esc 立即关闭，方便本机直观查看自定义壁纸效果。
    /// </summary>
    internal sealed class PrivacyCurtainManager : IDisposable
    {
        private readonly List<PrivacyCurtainWindow> windows = new List<PrivacyCurtainWindow>();
        private readonly Timer refreshTimer = new Timer();
        private PrivacyWallpaperConfig currentConfig;
        private Bitmap cachedImage;
        private bool active;
        private bool previewMode;
        private bool displayEventsHooked;
        private bool disposed;

        internal event Action StateChanged;

        internal bool IsActive { get { return active; } }
        internal bool IsPreview { get { return active && previewMode; } }

        internal PrivacyCurtainManager()
        {
            refreshTimer.Interval = 1500;
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
                win.Show();
                if (!preview && !win.CaptureExcluded) allExcluded = false;
                windows.Add(win);
            }

            active = windows.Count > 0;
            if (active)
            {
                if (!displayEventsHooked)
                {
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    displayEventsHooked = true;
                }
                refreshTimer.Start();
                if (preview)
                    statusMessage = "正在全屏预览自定义防窥壁纸（点击任意处或按 Esc 退出）。";
                else if (allExcluded)
                    statusMessage = "防窥屏壁纸已覆盖 " + windows.Count + " 块屏幕（物理屏遮挡，远控端透明穿透；随时按 Ctrl+Alt+Shift+P 退出）。";
                else
                    statusMessage = "防窥壁纸已开启（当前系统未接受捕获排除标记，建议搭配 Zako 虚拟屏使用）。";
            }
            RaiseStateChanged();
            return active;
        }

        internal void Deactivate(bool triggerLockIfConfigured)
        {
            bool wasActiveNonPreview = active && !previewMode;
            bool shouldLock = triggerLockIfConfigured && wasActiveNonPreview && currentConfig != null && currentConfig.LockOnLocalExit;

            // 先切换状态、停定时器并解绑显示事件，杜绝锁屏/解锁切换桌面时残留回调重新拉起防窥屏
            active = false;
            previewMode = false;
            refreshTimer.Stop();
            if (displayEventsHooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                displayEventsHooked = false;
            }

            foreach (var win in windows)
            {
                try { win.Hide(); } catch { }
                try { win.Close(); } catch { }
                try { win.Dispose(); } catch { }
            }
            windows.Clear();
            DisposeImage();
            RaiseStateChanged();

            if (shouldLock)
            {
                try { NativeLockWorkStation(); } catch { }
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
            foreach (var win in windows)
            {
                if (win.IsDisposed) continue;
                if (!previewMode) win.EnsureTopMostNoActivate();
                win.Invalidate();
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!active || disposed || windows.Count == 0) return;
            try
            {
                Control anchor = windows[0];
                if (!anchor.IsDisposed && anchor.IsHandleCreated)
                {
                    anchor.BeginInvoke(new Action(delegate
                    {
                        if (!active || disposed) return;
                        string ignored;
                        ActivateInternal(currentConfig, previewMode, false, out ignored);
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
        private const double EntryToastHoldMs = 3200.0;
        private const double EntryToastTotalMs = 4000.0;
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
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
