using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm : Form
    {
        private readonly ComboBox remoteId = Theme.Choice("远程设备码", true);
        private readonly ComboBox streamHost = Theme.Choice("串流主机地址", true);
        private readonly ComboBox quality = Theme.Choice("串流画质", false);
        private readonly Button discover = Theme.Action("发现设备", Theme.ButtonStyle.Secondary, Theme.Glyph.Search);
        private readonly Theme.StatusBanner assistBanner = new Theme.StatusBanner("输入伙伴设备地址或 9 位设备码即可连接；首次高速串流会在此展开 4 位配对码授权。");
        private readonly Theme.Badge streamModeBadge = new Theme.Badge("检测串流组件", Theme.PillTone.Neutral);
        private readonly Label streamHint = Theme.Text("", Theme.TextSize.Small, Theme.Muted);
        private readonly Timer localPairTimer = new Timer();

        private Label localAssistAddressLabel;
        private Button localAssistAddressEyeButton;
        private bool localAssistAddressVisible = false;
        private Label localPairCodeLabel;
        private Label localPairStatusLabel;
        private Button localPairGenerateButton;
        private Button localPairCancelButton;
        private Button localPairEyeButton;
        private bool localPairPinVisible = true;
        private string localPairPin;
        private BoBoLocalPairSession localPairSession;
        private BoBoHostServer boboHostServer;
        private bool allowIncomingControl = true;
        private bool fileTransferBusy;

        private Button assistModeStreamTab;
        private Button assistModeRustDeskTab;
        private Control assistStreamModePanel;
        private Control assistRustDeskModePanel;
        private Panel assistModeHost;
        private int assistModeHostRow = -1;
        private int activeAssistMode = 0;
        private Button streamConnectBtn;
        private Button rustDeskConnectBtn;

        private const int WanWideProfile = 0;
        private const int WanTallProfile = 1;
        private const int WeakNetProfile = 3;
        private const int AutoQualityProfile = 9;
        private const int Lan2K90WideProfile = 4;
        private const int Lan2K90TallProfile = 5;

        private const int HotkeyPrivacyId = 0x51A0;
        private const int HotkeyPrivacyEnableId = 0x51A1;
        private const int HotkeyPrivacyDisableId = 0x51A2;
        private const int HotkeyRemoteLockId = 0x51A3;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkP = 0x50;
        private const uint VkF9 = 0x78;
        private const uint VkF11 = 0x7A;
        private const uint VkF12 = 0x7B;
        private const int WmHotkey = 0x0312;
        private const int WmWtsSessionChange = 0x02B1;
        private const int WtsSessionLock = 0x7;
        private const int WtsSessionUnlock = 0x8;
        private const uint NotifyForThisSession = 0;
        private const int WmGetMinMaxInfo = 0x0024;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 2;
        private const int WsBorder = 0x00800000;
        private const int WsDlgFrame = 0x00400000;
        private const int WsCaption = 0x00C00000;
        private const int WsThickFrame = 0x00040000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const int WsSysMenu = 0x00080000;
        private const int CsDropShadow = 0x00020000;
        private const uint MonitorDefaultToNearest = 2;

        private const string MissingStreamEngineMessage = "内置 BoBoCore 原生内核加载异常，请重新安装或更新 BoBo远程。";
        private const int AssistPageHeight = 640;
        private const int LocalPairLifetimeSeconds = 120;

        private static readonly QualityOption[] QualityOptions =
        {
            new QualityOption("低延迟自适应 · 1080p · 60 帧 · 10 Mbps", 1920, 1080, 60, 10000),
            new QualityOption("外网 16:10 · 1920×1200 · 60 帧 · 10 Mbps", 1920, 1200, 60, 10000),
            new QualityOption("外网高清 · 1440p · 60 帧 · 14 Mbps", 2560, 1440, 60, 14000),
            new QualityOption("弱网抗丢包 · 720p · 30 帧 · 3.5 Mbps", 1280, 720, 30, 3500),
            new QualityOption("高速 / 局域网 · 2560×1440 · 90 帧 · 22 Mbps", 2560, 1440, 90, 22000),
            new QualityOption("高速 / 局域网 16:10 · 2560×1600 · 90 帧 · 24 Mbps", 2560, 1600, 90, 24000),
            new QualityOption("局域网高刷 · 2560×1440 · 120 帧 · 30 Mbps", 2560, 1440, 120, 30000),
            new QualityOption("极速电竞高刷 · 2560×1440 · 144 帧 · 36 Mbps", 2560, 1440, 144, 36000),
            new QualityOption("4K 超清办公 · 3840×2160 · 60 帧 · 35 Mbps", 3840, 2160, 60, 35000),
            new QualityOption("自动推荐 · 根据线路选择画质", 1920, 1080, 60, 10000)
        };

        private readonly RecentStore recent;
        private readonly BoBoRelayStore customRelayStore;
        private readonly PrivacyWallpaperConfig privacyConfig;
        private readonly PrivacyCurtainManager privacyCurtain;
        private readonly PrivacyGuardState privacyGuard = new PrivacyGuardState();
        private readonly Timer remoteGuardTimer = new Timer();
        private HostSnapshot hostSnapshot = new HostSnapshot();
        private NotifyIcon trayIcon;
        private Icon trayAppIcon;
        private Icon appIcon;
        private bool forceExit;
        private bool remoteSessionsTornDown;
        private bool guardPollBusy;
        private bool hotkeyRegistered;
        private bool remoteHotkeysRegistered;
        private bool wtsRegistered;
        private bool sessionEventsHooked;
        private DateTime lastSessionUnlockUtc = DateTime.MinValue;
        private DateTime lastPrivacyPointerDownUtc = DateTime.MinValue;
        private Control lastPrivacyPointerControl;
        private TableLayoutPanel assistDesk;
        private Control assistLocalCard;
        private Control assistPartnerCard;
        private Timer onboardingDelayTimer;
        private Timer updateCheckTimer;
        private WindowMessageFilter windowFilter;
        private NativeStreamForm activeNativeStreamForm;
        private StreamToolbarOverlay activeStreamToolbar;
        private string activeStreamHost = "";
        private int activeStreamPid;
        private bool hostCurrentlyControlled;
        private bool initialScaleApplied;
        private float appliedDpiScale = 1f;
        private string updatedFromVersion;
        private bool updateBusy;
        private UpdateCheckResult pendingUpdate;
        private System.Threading.CancellationTokenSource updateCts;
        private ComboBox settingsDefaultProfileCombo;

        internal MainForm() : this(false, null) { }

        internal MainForm(bool forceTour, string updatedVersion = null)
        {
            privacyCurtain = new PrivacyCurtainManager(this);
            forceOnboardingOnLaunch = forceTour;
            updatedFromVersion = updatedVersion;
            initialScaleApplied = false;
            SuspendLayout();
            Text = "BoBo远程";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MinimizeBox = true;
            MaximizeBox = true;
            ClientSize = new Size(1160, 730);
            MinimumSize = new Size(880, 560);
            Padding = Padding.Empty;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.ChromeBg;
            DoubleBuffered = true;

            try
            {
                appIcon = Theme.CreateAppIcon();
                Icon = appIcon;
            }
            catch { }

            recent = RecentStore.Load();
            customRelayStore = BoBoRelayStore.Load();
            privacyConfig = PrivacyWallpaperConfig.Load();
            streamModeBadge.AlignRight = true;
            remoteId.MaxLength = 64;
            streamHost.MaxLength = 253;
            Theme.Cue(remoteId, "请输入对方的 9 位 BoBo 设备码或局域网/Tailscale IP");
            Theme.Cue(streamHost, "请输入对方 9 位 BoBo 设备码、局域网 IP 或 Tailscale IP");
            streamHint.AutoEllipsis = true;
            streamHint.UseMnemonic = false;
            LoadPreferences();
            RestoreRecents(remoteId, recent.RustDeskIds);
            RestoreRecents(streamHost, recent.MoonlightHosts);
            if (fillLastTarget)
            {
                if (remoteId.Items.Count > 0) remoteId.SelectedIndex = 0;
                if (streamHost.Items.Count > 0) streamHost.SelectedIndex = 0;
            }
            quality.Items.AddRange(QualityOptions);
            quality.DropDownWidth = 370;
            quality.SelectedIndex = defaultQuality;
            quality.SelectedIndexChanged += delegate { UpdateStreamHint(); };
            UpdateStreamHint();

            privacyCurtain.StateChanged += OnPrivacyCurtainStateChanged;
            privacyCurtain.LocalEscapeRequested += delegate { TogglePrivacyCurtain(true, false); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            sessionEventsHooked = true;
            remoteGuardTimer.Interval = 350;
            remoteGuardTimer.Tick += async delegate { await PollRemoteGuardTick(); };
            localPairTimer.Interval = 1000;
            localPairTimer.Tick += delegate { PollLocalPairingTick(); };

            Controls.Add(BuildChrome());
            SetupTrayIcon();
            windowFilter = new WindowMessageFilter(this);
            Application.AddMessageFilter(windowFilter);

            AutoScaleDimensions = new SizeF(96f, 96f);
            ResumeLayout(false);
            PerformAutoScale();
            float autoScale = AutoScaleDimensions.Width >= 72f ? (AutoScaleDimensions.Width / 96f) : 1f;
            appliedDpiScale = Math.Max(1f, Math.Max(autoScale, Theme.GetDpiScale(this)));
            initialScaleApplied = true;
            SyncAllPagesAfterScale();
            PerformLayout();
        }

        internal float CurrentDpiScale()
        {
            if (!initialScaleApplied) return 1f;
            return Math.Max(1f, appliedDpiScale);
        }

        private void SyncAllPagesAfterScale()
        {
            float scale = CurrentDpiScale();
            quality.DropDownWidth = (int)Math.Round(400f * scale);
            if (connectionRouteChoice != null)
                connectionRouteChoice.DropDownWidth = (int)Math.Round(300f * scale);
            if (customRelayChoice != null)
                customRelayChoice.DropDownWidth = (int)Math.Round(340f * scale);
            if (customRelayModeChoice != null)
                customRelayModeChoice.DropDownWidth = (int)Math.Round(290f * scale);
            if (settingsDefaultProfileCombo != null)
                settingsDefaultProfileCombo.DropDownWidth = (int)Math.Round(380f * scale);
            if (settingsHostDisconnectActionCombo != null) settingsHostDisconnectActionCombo.DropDownWidth = DevicePixels(180);
            if (privacyHostDisconnectActionCombo != null) privacyHostDisconnectActionCombo.DropDownWidth = DevicePixels(180);

            for (int i = 0; i < pages.Length; i++)
            {
                var vp = pages[i] as Theme.SlimScrollViewport;
                if (vp != null) vp.SuspendResponsiveLayout = false;
            }
            SyncNavigationSectionVisibility();
            SetCustomRelayPanelExpanded(customRelayPanelExpanded);
            SelectAssistMode(activeAssistMode);
            RecalculateDevicesDeskHeight();
            RecalculateFavoritesDeskHeight();
            RecalculateSettingsDeskHeight();
            if (privacyDesk != null)
            {
                privacyDesk.Height = SumRows(privacyDesk);
                privacyDesk.PerformLayout();
            }
            RecalculateDetailDeskHeight();
            for (int i = 0; i < pages.Length; i++)
            {
                var vp = pages[i] as Theme.SlimScrollViewport;
                if (vp != null) vp.RefreshLayout();
            }
        }

        private void NotifyOnboardingLayoutChanged()
        {
            if (activeOnboardingTour != null && !activeOnboardingTour.IsDisposed)
                activeOnboardingTour.NotifyHostContentChanged();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 纯无边框模式：绝不附加 WS_CAPTION / WS_THICKFRAME / WS_SYSMENU，彻底杜绝经典 Windows 蓝色标题栏重绘遮挡；
                // 保留 WS_MINIMIZEBOX / WS_MAXIMIZEBOX 支持任务栏点击最小化/还原，并用 CS_DROPSHADOW + Win11 DWMWCP_ROUND 呈现阴影圆角。
                cp.Style |= WsMinimizeBox | WsMaximizeBox;
                cp.Style &= ~(WsCaption | WsThickFrame | WsSysMenu | WsBorder | WsDlgFrame);
                cp.ClassStyle |= CsDropShadow;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyPrivacyId, ModControl | ModAlt | ModShift | ModNoRepeat, VkP);
            }
            catch { hotkeyRegistered = false; }

            try
            {
                uint mods = ModControl | ModAlt | ModShift | ModNoRepeat;
                bool ok1 = RegisterHotKey(Handle, HotkeyPrivacyEnableId, mods, VkF11);
                bool ok2 = RegisterHotKey(Handle, HotkeyPrivacyDisableId, mods, VkF12);
                bool ok3 = RegisterHotKey(Handle, HotkeyRemoteLockId, mods, VkF9);
                remoteHotkeysRegistered = ok1 || ok2 || ok3;
            }
            catch { remoteHotkeysRegistered = false; }

            try
            {
                wtsRegistered = WTSRegisterSessionNotification(Handle, NotifyForThisSession);
            }
            catch { wtsRegistered = false; }

            // Windows 11 crisp rounded window corners + native OS drop shadow (gracefully ignored on Win10)
            try
            {
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            }
            catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (wtsRegistered && IsHandleCreated)
            {
                try { WTSUnRegisterSessionNotification(Handle); } catch { }
                wtsRegistered = false;
            }
            if (hotkeyRegistered && IsHandleCreated)
            {
                try { UnregisterHotKey(Handle, HotkeyPrivacyId); } catch { }
                hotkeyRegistered = false;
            }
            if (remoteHotkeysRegistered && IsHandleCreated)
            {
                try { UnregisterHotKey(Handle, HotkeyPrivacyEnableId); } catch { }
                try { UnregisterHotKey(Handle, HotkeyPrivacyDisableId); } catch { }
                try { UnregisterHotKey(Handle, HotkeyRemoteLockId); } catch { }
                remoteHotkeysRegistered = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (Program.WmShowExistingInstance != 0 && m.Msg == Program.WmShowExistingInstance)
            {
                RestoreFromTray();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WmWtsSessionChange)
            {
                int reason = m.WParam.ToInt32();
                if (reason == WtsSessionLock) HandleSessionLocked();
                else if (reason == WtsSessionUnlock) HandleSessionUnlocked();
            }
            if (m.Msg == WmHotkey)
            {
                int hkId = m.WParam.ToInt32();
                if (hkId == HotkeyPrivacyId)
                {
                    TogglePrivacyCurtain(true, false);
                    return;
                }
                if (hkId == HotkeyPrivacyEnableId)
                {
                    ActivatePrivacyFromRemoteCommand();
                    return;
                }
                if (hkId == HotkeyPrivacyDisableId)
                {
                    DeactivatePrivacyFromRemoteCommand();
                    return;
                }
                if (hkId == HotkeyRemoteLockId)
                {
                    WindowsSession.TryLockWorkstation();
                    return;
                }
            }
            if (m.Msg == WmGetMinMaxInfo)
            {
                ApplyMonitorWorkAreaMinMax(m.LParam);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private void ApplyMonitorWorkAreaMinMax(IntPtr lParam)
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(Handle, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return;
                var info = new MonitorInfoStruct { CbSize = Marshal.SizeOf(typeof(MonitorInfoStruct)) };
                if (!GetMonitorInfo(monitor, ref info)) return;
                var mmi = (MinMaxInfoStruct)Marshal.PtrToStructure(lParam, typeof(MinMaxInfoStruct));
                mmi.MaxPosition.X = Math.Abs(info.WorkArea.Left - info.MonitorArea.Left);
                mmi.MaxPosition.Y = Math.Abs(info.WorkArea.Top - info.MonitorArea.Top);
                mmi.MaxSize.X = Math.Abs(info.WorkArea.Right - info.WorkArea.Left);
                mmi.MaxSize.Y = Math.Abs(info.WorkArea.Bottom - info.WorkArea.Top);
                mmi.MinTrackSize.X = Math.Max(mmi.MinTrackSize.X, MinimumSize.Width);
                mmi.MinTrackSize.Y = Math.Max(mmi.MinTrackSize.Y, MinimumSize.Height);
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            catch { }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            bool maxed = WindowState == FormWindowState.Maximized;
            if (borderFrame != null)
            {
                borderFrame.Padding = maxed ? Padding.Empty : new Padding(1);
            }
            if (maxRestoreBtn != null)
            {
                Theme.ChangeGlyph(maxRestoreBtn, maxed ? Theme.Glyph.Restore : Theme.Glyph.Maximize);
                maxRestoreBtn.AccessibleName = maxed ? "还原" : "最大化";
            }
        }

        private void BeginDragWindow()
        {
            try
            {
                if (WindowState == FormWindowState.Maximized)
                {
                    Point cursor = Control.MousePosition;
                    double ratioX = Width > 0 ? Math.Max(0.15, Math.Min(0.85, (double)(cursor.X - Left) / Width)) : 0.5;
                    WindowState = FormWindowState.Normal;
                    int newX = cursor.X - (int)Math.Round(Width * ratioX);
                    int newY = Math.Max(Screen.FromPoint(cursor).WorkingArea.Top, cursor.Y - 22);
                    Location = new Point(newX, newY);
                }
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
            }
            catch { }
        }

        private void ToggleWindowMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void OnLoad(EventArgs args)
        {
            base.OnLoad(args);
            ClampMinimumSizeToWorkArea();
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(Width, area.Width - 20);
            int height = Math.Min(Height, area.Height - 20);
            if (width != Width || height != Height)
                SetBounds(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
            PrivacyDisplayDimmer.RecoverIfNeeded();
            EnsureBoBoHostServerStarted();
            SyncRemoteGuardTimer();
            HookAndAutoStartEmbeddedTailnet();
        }

        private void ClampMinimumSizeToWorkArea()
        {
            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                float scale = CurrentDpiScale();
                int targetMinW = Math.Min((int)Math.Round(880 * scale), Math.Max(640, area.Width - 40));
                int targetMinH = Math.Min((int)Math.Round(560 * scale), Math.Max(460, area.Height - 40));
                MinimumSize = new Size(targetMinW, targetMinH);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ClampMinimumSizeToWorkArea();
            ResizeToWorkingArea(false);

            if (!string.IsNullOrEmpty(updatedFromVersion))
            {
                string msg = "已自动更新到 BoBo远程 v" + AppUpdater.CurrentVersion + "！";
                deviceBanner.Set(msg, Theme.PillTone.Success);
                SetStatus(msg, Theme.PillTone.Success);
                ShowSettingsBanner(msg, Theme.PillTone.Success);
                updatedFromVersion = null;
            }

            if (autoCheckUpdates && updateCheckTimer == null)
            {
                updateCheckTimer = new Timer { Interval = 2600 };
                updateCheckTimer.Tick += async delegate
                {
                    if (updateCheckTimer != null)
                    {
                        updateCheckTimer.Stop();
                        updateCheckTimer.Dispose();
                        updateCheckTimer = null;
                    }
                    if (!IsDisposed) await CheckForUpdatesAsync(true);
                };
                updateCheckTimer.Start();
            }

            // First launch (or --tour): run spotlight onboarding guide once initial layout & DPI settle
            if (!onboardingScheduled && (forceOnboardingOnLaunch || !OnboardingStore.IsCompleted()))
            {
                onboardingScheduled = true;
                onboardingDelayTimer = new Timer { Interval = 320 };
                onboardingDelayTimer.Tick += delegate
                {
                    if (onboardingDelayTimer != null)
                    {
                        onboardingDelayTimer.Stop();
                        onboardingDelayTimer.Dispose();
                        onboardingDelayTimer = null;
                    }
                    if (!IsDisposed && Visible && WindowState != FormWindowState.Minimized)
                        StartOnboardingTour(true);
                };
                onboardingDelayTimer.Start();
            }
        }

        private void ResizeToWorkingArea(bool forceMaximum)
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            float scale = CurrentDpiScale();
            int preferredW = (int)Math.Round(1160 * scale);
            int preferredH = (int)Math.Round(730 * scale);
            int width = forceMaximum ? area.Width : Math.Min(preferredW, area.Width - 24);
            int height = forceMaximum ? area.Height : Math.Min(preferredH, area.Height - 24);
            if (forceMaximum || Width > area.Width || Height > area.Height)
            {
                Bounds = new Rectangle(area.Left + (area.Width - width) / 2,
                    area.Top + (area.Height - height) / 2, width, height);
            }
        }

        private void SetupTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开 BoBo远程", null, delegate { RestoreFromTray(); });
            menu.Items.Add("开启/关闭防窥壁纸 (Ctrl+Alt+Shift+P)", null, delegate { TogglePrivacyCurtain(false); });
            menu.Items.Add("断开当前远控会话", null, delegate { DisconnectAllRemoteSessionsFromUi(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("彻底退出 BoBo远程（断开远控）", null, delegate { forceExit = true; Close(); });

            trayAppIcon = Theme.CreateSmallAppIcon();
            trayIcon = new NotifyIcon
            {
                Icon = trayAppIcon ?? appIcon ?? SystemIcons.Application,
                Text = "BoBo远程 · 低延迟远控与防窥守护",
                Visible = true,
                ContextMenuStrip = menu
            };
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CancelClientPairing("窗口关闭，已取消配对。");
            CancelLocalPairing("窗口关闭，本机配对码已失效。");
            if (!forceExit && e.CloseReason == CloseReason.UserClosing && (minimizeToTray || privacyConfig.AutoOnRemote || privacyCurtain.IsActive))
            {
                e.Cancel = true;
                Hide();
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(2200, "BoBo远程 仍在后台运行",
                        privacyConfig.AutoOnRemote
                            ? "防窥屏守护运行中；双击托盘恢复窗口，右键「彻底退出」将同时断开远控。"
                            : "双击托盘可恢复窗口；右键「彻底退出」将同时断开远控。",
                        ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs args)
        {
            if (sessionEventsHooked)
            {
                try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
                sessionEventsHooked = false;
            }
            if (windowFilter != null)
            {
                Application.RemoveMessageFilter(windowFilter);
                windowFilter = null;
            }
            remoteGuardTimer.Stop();
            remoteGuardTimer.Dispose();
            localPairTimer.Stop();
            localPairTimer.Dispose();
            if (onboardingDelayTimer != null)
            {
                onboardingDelayTimer.Stop();
                onboardingDelayTimer.Dispose();
                onboardingDelayTimer = null;
            }
            if (updateCheckTimer != null)
            {
                updateCheckTimer.Stop();
                updateCheckTimer.Dispose();
                updateCheckTimer = null;
            }
            if (updateCts != null)
            {
                try { updateCts.Cancel(); } catch { }
                try { updateCts.Dispose(); } catch { }
                updateCts = null;
            }
            if (activeOnboardingTour != null && !activeOnboardingTour.IsDisposed)
            {
                try { activeOnboardingTour.Close(); } catch { }
                activeOnboardingTour = null;
            }
            TeardownRemoteSessionsOnExit();
            if (wtsRegistered && IsHandleCreated)
            {
                try { WTSUnRegisterSessionNotification(Handle); } catch { }
                wtsRegistered = false;
            }
            if (hotkeyRegistered && IsHandleCreated)
            {
                try { UnregisterHotKey(Handle, HotkeyPrivacyId); } catch { }
                hotkeyRegistered = false;
            }
            if (remoteHotkeysRegistered && IsHandleCreated)
            {
                try { UnregisterHotKey(Handle, HotkeyPrivacyEnableId); } catch { }
                try { UnregisterHotKey(Handle, HotkeyPrivacyDisableId); } catch { }
                try { UnregisterHotKey(Handle, HotkeyRemoteLockId); } catch { }
                remoteHotkeysRegistered = false;
            }
            privacyCurtain.Dispose();
            DisposePreviewImage();
            DeviceWallpaperGallery.DisposeCache();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (trayAppIcon != null)
            {
                trayAppIcon.Dispose();
                trayAppIcon = null;
            }
            if (appIcon != null)
            {
                try { appIcon.Dispose(); } catch { }
                appIcon = null;
            }
            if (keepThisComputerAwake)
            {
                string error;
                WindowsSession.TryKeepAwake(false, out error);
            }
            base.OnFormClosed(args);
        }

        // ==================== ASSIST PAGE (UU IMAGE 3 EXACT TWO-CARD LAYOUT) ====================

        private Control BuildWorkspace()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(AssistPageHeight, out desk);
            assistDesk = desk;

            AddPageHeader(desk, "远程协助", null);
            Theme.Row(desk, 18);

            // Card 1: 本设备 (exact UU Image 3 structure!)
            var localCard = BuildLocalDeviceAssistCard();
            assistLocalCard = localCard;
            Theme.Row(desk, 208, localCard);
            Theme.Row(desk, 16);

            // Card 2: 远控伙伴设备 (exact UU Image 3 structure with clean Stream / RustDesk toggle!)
            var partnerCard = BuildPartnerDeviceCard();
            assistPartnerCard = partnerCard;
            assistStreamCardRow = desk.RowCount;
            Theme.Row(desk, ClientStreamCardHeight(), partnerCard);
            Theme.Row(desk, 16);
            Theme.Row(desk, 42, assistBanner);

            SelectAssistMode(0);
            LayoutAssistDesk();
            return viewport;
        }

        private Control BuildLocalDeviceAssistCard()
        {
            var card = new Theme.Card { Padding = new Padding(0) };
            var root = Theme.Grid(1);
            Theme.FillColumn(root);

            // Card top bar: "本设备" on left, primary pairing controls on right
            var header = Theme.Grid(5);
            header.Padding = new Padding(22, 12, 22, 12);
            Theme.FillColumn(header);
            Theme.FixedColumn(header, 12);
            Theme.FixedColumn(header, 128);
            Theme.FixedColumn(header, 8);
            Theme.FixedColumn(header, 84);

            localPairGenerateButton = Theme.Action("生成配对码", Theme.ButtonStyle.Primary);
            localPairGenerateButton.Click += async delegate { await LocalPairPrimaryActionAsync(); };
            localPairCancelButton = Theme.Action("取消", Theme.ButtonStyle.Secondary);
            localPairCancelButton.Enabled = false;
            localPairCancelButton.Click += delegate { CancelLocalPairing(); };

            Theme.Row(header, 36, Theme.Text("本设备", Theme.TextSize.Section, Theme.Ink), null, localPairGenerateButton, null, localPairCancelButton);
            Theme.Row(root, 60, header);
            Theme.Row(root, 1, Theme.Rule());

            // Card body: Left 本机地址, Middle 一次性 4 位配对码 + eye button, Right stacked actions (never squishes IP at 880px)
            var body = Theme.Grid(5);
            body.Padding = new Padding(22, 16, 22, 14);
            Theme.FillColumn(body);
            Theme.FixedColumn(body, 16);
            Theme.FixedColumn(body, 198);
            Theme.FixedColumn(body, 16);
            Theme.FixedColumn(body, 126);

            var addrCol = Theme.Grid(1);
            Theme.FillColumn(addrCol);
            Theme.Row(addrCol, 22, Theme.Text("本机连接地址（推荐直连）", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(addrCol, 6);

            var addrLine = Theme.Grid(3);
            Theme.FillColumn(addrLine);
            Theme.FixedColumn(addrLine, 6);
            Theme.FixedColumn(addrLine, 34);
            localAssistAddressLabel = Theme.Text("正在检测地址…", Theme.TextSize.BigCode, Theme.Ink);
            localAssistAddressLabel.AutoEllipsis = true;
            localAssistAddressLabel.AccessibleName = "本机连接地址";
            localAssistAddressEyeButton = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.EyeOff);
            localAssistAddressEyeButton.AccessibleName = "显示或隐藏本机 IP 地址";
            localAssistAddressEyeButton.Click += delegate
            {
                localAssistAddressVisible = !localAssistAddressVisible;
                Theme.ChangeGlyph(localAssistAddressEyeButton, localAssistAddressVisible ? Theme.Glyph.Eye : Theme.Glyph.EyeOff);
                UpdateLocalAssistAddressLabel();
            };
            Theme.Row(addrLine, 38, localAssistAddressLabel, null, localAssistAddressEyeButton);
            Theme.Row(addrCol, 38, addrLine);

            localPairStatusLabel = Theme.Text("点击右上角「生成配对码」允许新电脑首次连接本机", Theme.TextSize.Small, Theme.Muted);
            localPairStatusLabel.AutoEllipsis = true;
            Theme.Row(addrCol, 24, localPairStatusLabel);
            Theme.FillRow(addrCol);

            var pinCol = Theme.Grid(1);
            Theme.FillColumn(pinCol);
            Theme.Row(pinCol, 22, Theme.Text("临时配对码（120 秒有效）", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(pinCol, 6);

            var pinLine = Theme.Grid(3);
            Theme.FillColumn(pinLine);
            Theme.FixedColumn(pinLine, 6);
            Theme.FixedColumn(pinLine, 34);
            localPairCodeLabel = Theme.Text("• • • •", Theme.TextSize.BigCode, Theme.Ink);
            localPairCodeLabel.AutoEllipsis = false;
            localPairCodeLabel.AccessibleName = "本机 4 位配对码";
            localPairEyeButton = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Eye);
            localPairEyeButton.AccessibleName = "显示或隐藏配对码";
            localPairEyeButton.Click += delegate
            {
                localPairPinVisible = !localPairPinVisible;
                Theme.ChangeGlyph(localPairEyeButton, localPairPinVisible ? Theme.Glyph.Eye : Theme.Glyph.EyeOff);
                RefreshLocalPairPinDisplay();
            };
            Theme.Row(pinLine, 38, localPairCodeLabel, null, localPairEyeButton);
            Theme.Row(pinCol, 38, pinLine);
            Theme.Row(pinCol, 24, Theme.Text("每次生成自动刷新", Theme.TextSize.Small, Theme.Muted));
            Theme.FillRow(pinCol);

            var actionCol = Theme.Grid(1);
            Theme.FillColumn(actionCol);
            var copyShareBtn = Theme.Action("复制并分享", Theme.ButtonStyle.Secondary);
            copyShareBtn.Click += delegate { CopyLocalShareInfo(); };
            var manageAuthBtn = Theme.Action("管理授权", Theme.ButtonStyle.Secondary);
            manageAuthBtn.Click += delegate { ShowAuthorizedPeersDialog(); };
            Theme.FillRow(actionCol);
            Theme.Row(actionCol, 36, copyShareBtn);
            Theme.Row(actionCol, 8);
            Theme.Row(actionCol, 36, manageAuthBtn);
            Theme.FillRow(actionCol);

            Theme.Row(body, 104, addrCol, null, pinCol, null, actionCol);
            Theme.FillRow(body);
            Theme.Row(root, 138, body);
            Theme.FillRow(root);

            card.Controls.Add(root);
            return card;
        }

        private void UpdateLocalAssistAddressLabel()
        {
            if (localAssistAddressLabel == null) return;
            string preferred = !string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4)
                ? hostSnapshot.TailscaleIpv4
                : !string.IsNullOrEmpty(hostSnapshot.LanIpv4)
                    ? hostSnapshot.LanIpv4
                    : !string.IsNullOrEmpty(hostSnapshot.PublicIpv6)
                        ? ShortenIpv6(hostSnapshot.PublicIpv6)
                        : "未获取到网络地址";
            if (!localAssistAddressVisible && preferred != "未获取到网络地址")
            {
                localAssistAddressLabel.Text = MaskAddressForDisplay(
                    !string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4) ? hostSnapshot.TailscaleIpv4
                    : !string.IsNullOrEmpty(hostSnapshot.LanIpv4) ? hostSnapshot.LanIpv4
                    : hostSnapshot.PublicIpv6);
            }
            else
            {
                localAssistAddressLabel.Text = preferred;
            }
        }

        internal static string MaskAddressForDisplay(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "未获取到网络地址";
            string trimmed = address.Trim();
            if (trimmed.IndexOf(':') >= 0)
                return "IPv6 ••••:••••";
            string[] parts = trimmed.Split('.');
            if (parts.Length == 4)
                return parts[0] + "." + parts[1] + ".•••.•••";
            return "••••••••";
        }

        private void RefreshLocalPairPinDisplay()
        {
            if (localPairCodeLabel == null) return;
            if (string.IsNullOrEmpty(localPairPin))
                localPairCodeLabel.Text = "• • • •";
            else
                localPairCodeLabel.Text = localPairPinVisible ? localPairPin : "• • • •";
        }

        private void CopyLocalShareInfo()
        {
            string code = BoBoIdentityStore.Default.FormattedDeviceCode;
            string addr = !string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4)
                ? hostSnapshot.TailscaleIpv4
                : !string.IsNullOrEmpty(hostSnapshot.LanIpv4)
                    ? hostSnapshot.LanIpv4
                    : hostSnapshot.PublicIpv6;
            bool hasPin = !string.IsNullOrEmpty(localPairPin);
            string share = "BoBo设备码：" + code
                + (string.IsNullOrEmpty(addr) ? "" : ("  连接地址：" + addr))
                + (hasPin ? ("  4位配对码：" + localPairPin) : "");
            try
            {
                Clipboard.SetText(share);
                SetStatus(hasPin
                    ? "已将本机 BoBo 设备码、地址与 4 位临时配对码复制到剪贴板。"
                    : "已将本机 BoBo 设备码与连接地址复制到剪贴板。",
                    Theme.PillTone.Success);
            }
            catch
            {
                SetStatus("无法写入剪贴板，请点击眼睛图标查看并手动复制。", Theme.PillTone.Warning);
            }
        }

        private Control BuildPartnerDeviceCard()
        {
            var card = new Theme.Card { Padding = new Padding(0) };
            var layout = Theme.Grid(1);
            Theme.FillColumn(layout);
            clientPairCard = card;
            clientPairCardLayout = layout;

            var header = Theme.Grid(1);
            header.Padding = new Padding(22, 16, 22, 14);
            Theme.FillColumn(header);
            Theme.Row(header, 28, Theme.Text("远控伙伴设备", Theme.TextSize.Section, Theme.Ink));
            Theme.Row(header, 4);
            Theme.Row(header, 24, Theme.Text("双端同时打开 BoBo远程 后，输入对方 9 位 BoBo 设备码或局域网/Tailscale IP 即可安全连控与传文件", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(layout, 86, header);
            Theme.Row(layout, 1, Theme.Rule());

            var modeStrip = Theme.Grid(5);
            modeStrip.Padding = new Padding(22, 12, 22, 0);
            Theme.FixedColumn(modeStrip, 148);
            Theme.FixedColumn(modeStrip, 18);
            Theme.FixedColumn(modeStrip, 188);
            Theme.FillColumn(modeStrip);
            Theme.FixedColumn(modeStrip, 198);

            assistModeStreamTab = Theme.Action("远程桌面控制", Theme.ButtonStyle.UnderlineTab);
            assistModeRustDeskTab = Theme.Action("内置极速传文件", Theme.ButtonStyle.UnderlineTab);
            assistModeStreamTab.Click += delegate { SelectAssistMode(0); };
            assistModeRustDeskTab.Click += delegate { SelectAssistMode(1); };

            Theme.Row(modeStrip, 36, assistModeStreamTab, null, assistModeRustDeskTab, null, streamModeBadge);
            Theme.Row(layout, 48, modeStrip);

            assistModeHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Paper };
            assistStreamModePanel = BuildStreamModePanel();
            assistRustDeskModePanel = BuildRustDeskModePanel();
            assistModeHost.Controls.Add(assistStreamModePanel);
            assistModeHost.Controls.Add(assistRustDeskModePanel);
            assistModeHostRow = layout.RowCount;
            Theme.Row(layout, 178, assistModeHost);

            clientPairPanel = BuildClientPairPanel();
            clientPairPanel.Visible = false;
            clientPairPanelRow = layout.RowCount;
            Theme.Row(layout, 0, clientPairPanel);
            Theme.Row(layout, 10);

            card.Controls.Add(layout);
            return card;
        }

        private Control BuildStreamModePanel()
        {
            var panel = Theme.Grid(1);
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(22, 14, 22, 8);
            Theme.FillColumn(panel);

            Theme.Row(panel, 22, Theme.Text("伙伴设备地址", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(panel, 8);

            var row = Theme.Grid(5);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 124);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 124);

            streamConnectBtn = Theme.Action("连接", Theme.ButtonStyle.Primary, Theme.Glyph.Arrow);
            streamConnectBtn.Click += async delegate
            {
                if (IsActivelyControlling())
                {
                    DisconnectActiveStreamSession();
                    return;
                }
                await ConnectToDeviceAsync(SelectedHost(), null);
            };
            discover.Click += async delegate { await DiscoverPeers(); };
            streamHost.KeyDown += delegate(object sender, KeyEventArgs args) { RunOnEnter(streamHost, streamConnectBtn, args); };
            streamHost.TextChanged += delegate { UpdateAssistConnectButtons(); };

            Theme.Row(row, 40, Theme.WrapInput(streamHost), null, streamConnectBtn, null, discover);
            Theme.Row(panel, 40, row);
            Theme.Row(panel, 12);

            assistStreamPanelLayout = panel;
            var routeRow = Theme.Grid(5);
            Theme.FixedColumn(routeRow, 72);
            Theme.FixedColumn(routeRow, 8);
            Theme.FillColumn(routeRow);
            Theme.FixedColumn(routeRow, 14);
            Theme.FixedColumn(routeRow, 92);

            connectionRouteChoice = Theme.Choice("连接线路", false);
            connectionRouteChoice.Items.AddRange(new object[]
            {
                "自动选择（局域网 / P2P 优先）",
                "局域网直连",
                "跨网 · Tailscale（优先 P2P）",
                "直连优先 + 自建中转兜底"
            });
            connectionRouteChoice.DropDownWidth = 300;
            suppressRouteUiEvents = true;
            int initialRouteIdx = Math.Max(0, Math.Min(connectionRouteChoice.Items.Count - 1, (int)preferredConnectionRoute));
            connectionRouteChoice.SelectedIndex = initialRouteIdx;
            suppressRouteUiEvents = false;

            connectionRouteHint = Theme.Text("", Theme.TextSize.Small, Theme.Muted);
            connectionRouteHint.AutoEllipsis = true;
            customRelayToggleBtn = Theme.Action("自建中转", Theme.ButtonStyle.Link, Theme.Glyph.Sliders);
            customRelayToggleBtn.Click += delegate { ToggleCustomRelayPanel(); };

            connectionRouteChoice.SelectedIndexChanged += delegate { HandleConnectionRouteSelectionChanged(); };
            streamHost.TextChanged += delegate { ShowConnectionRouteHint(activeConnectionRoute); };
            Theme.Row(routeRow, 38, Theme.Text("连接线路", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(connectionRouteChoice), null, customRelayToggleBtn);
            Theme.Row(panel, 38, routeRow);
            Theme.Row(panel, 4);
            Theme.Row(panel, 22, connectionRouteHint);

            customRelaySpacerRowIndex = panel.RowCount;
            customRelayPanelExpanded = preferredConnectionRoute == BoBoRoutePreference.CustomRelayFallback;
            Theme.Row(panel, customRelayPanelExpanded ? 8 : 0);
            customRelayPanel = BuildCustomRelayPanel();
            customRelayPanel.Visible = customRelayPanelExpanded;
            customRelayRowIndex = panel.RowCount;
            Theme.Row(panel, customRelayPanelExpanded ? MeasureCustomRelayPanelHeight() : 0, customRelayPanel);
            Theme.Row(panel, 8);
            SyncCustomRelayUi();
            ShowConnectionRouteHint(null);

            // Responsive quality selector + hint row
            var qRow = Theme.Grid(5);
            Theme.FixedColumn(qRow, 72);
            Theme.FixedColumn(qRow, 8);
            Theme.FillColumn(qRow);
            Theme.FixedColumn(qRow, 14);
            Theme.FixedColumn(qRow, 92);

            var probeBtn = Theme.Action("链路测速", Theme.ButtonStyle.Link, Theme.Glyph.Activity);
            probeBtn.Click += async delegate { await ProbeLink(); };

            Theme.Row(qRow, 38, Theme.Text("画质档位", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(quality), null, probeBtn);
            Theme.Row(panel, 38, qRow);
            Theme.Row(panel, 8);
            Theme.Row(panel, 24, streamHint);

            UpdateAssistConnectButtons();
            return panel;
        }

        private Control BuildRustDeskModePanel()
        {
            var panel = Theme.Grid(1);
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(22, 14, 22, 8);
            Theme.FillColumn(panel);

            Theme.Row(panel, 22, Theme.Text("目标电脑的 9 位 BoBo 设备码或 IP 地址", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(panel, 8);

            var row = Theme.Grid(3);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 160);

            rustDeskConnectBtn = Theme.Action("选择文件发送", Theme.ButtonStyle.Primary, Theme.Glyph.Folder);
            rustDeskConnectBtn.Click += async delegate { await StartBuiltInFileTransferAsync(); };
            remoteId.KeyDown += delegate(object sender, KeyEventArgs args) { RunOnEnter(remoteId, rustDeskConnectBtn, args); };
            remoteId.TextChanged += delegate { UpdateAssistConnectButtons(); };

            Theme.Row(row, 40, Theme.WrapInput(remoteId), null, rustDeskConnectBtn);
            Theme.Row(panel, 40, row);
            Theme.Row(panel, 12);
            Theme.Row(panel, 26, Theme.Text("通过 BoBo V6 内置 TLS 1.2 通道点对点传输文件并校验 SHA-256，接收文件自动保存到对方「下载\\BoBoRemote」。", Theme.TextSize.Small, Theme.Muted));

            UpdateAssistConnectButtons();
            return panel;
        }

        private void SelectAssistMode(int mode)
        {
            activeAssistMode = mode == 1 ? 1 : 0;
            bool isStream = activeAssistMode == 0;
            if (assistStreamModePanel != null) assistStreamModePanel.Visible = isStream;
            if (assistRustDeskModePanel != null) assistRustDeskModePanel.Visible = !isStream;
            if (assistModeStreamTab != null) Theme.Highlight(assistModeStreamTab, isStream);
            if (assistModeRustDeskTab != null) Theme.Highlight(assistModeRustDeskTab, !isStream);
            if (streamModeBadge != null) streamModeBadge.Visible = isStream;
            if (!isStream) SetClientPairPanelVisible(false);

            if (clientPairCardLayout != null && assistModeHostRow >= 0)
            {
                Control activePanel = isStream ? assistStreamModePanel : assistRustDeskModePanel;
                int panelH = activePanel != null ? SumRows(activePanel) : 0;
                if (panelH <= 0) panelH = DevicePixels(isStream ? 178 : 138);
                clientPairCardLayout.RowStyles[assistModeHostRow].Height = panelH;
                if (assistDesk != null && assistStreamCardRow >= 0)
                    assistDesk.RowStyles[assistStreamCardRow].Height = ClientStreamCardHeight();
                LayoutAssistDesk();
            }
            UpdateAssistConnectButtons();
        }

        private bool IsActivelyControlling()
        {
            return (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                || (activeStreamPid > 0 && Engines.IsProcessAlive(activeStreamPid));
        }

        private void UpdateAssistConnectButtons()
        {
            if (streamConnectBtn != null)
            {
                bool controlling = IsActivelyControlling();
                if (controlling)
                {
                    streamConnectBtn.Text = "退出控制";
                    Theme.ChangeStyle(streamConnectBtn, Theme.ButtonStyle.DangerPill);
                    Theme.ChangeGlyph(streamConnectBtn, Theme.Glyph.Exit);
                    streamConnectBtn.Enabled = true;
                }
                else
                {
                    streamConnectBtn.Text = "连接";
                    Theme.ChangeStyle(streamConnectBtn, Theme.ButtonStyle.Primary);
                    Theme.ChangeGlyph(streamConnectBtn, Theme.Glyph.Arrow);
                    bool canConnectWithRelayFallback = SelectedConnectionRoute == BoBoRoutePreference.CustomRelayFallback
                        && customRelayStore != null && customRelayStore.ActiveRelay != null;
                    streamConnectBtn.Enabled = !string.IsNullOrWhiteSpace(streamHost.Text) || canConnectWithRelayFallback;
                }
            }
            if (rustDeskConnectBtn != null)
                rustDeskConnectBtn.Enabled = !string.IsNullOrWhiteSpace(remoteId.Text);
        }

        // ==================== V6 IN-PROCESS HOST SERVER & INSTANT LOCAL PAIRING ====================

        private void EnsureBoBoHostServerStarted()
        {
            if (boboHostServer != null && boboHostServer.IsRunning) return;
            try
            {
                if (boboHostServer == null)
                {
                    boboHostServer = new BoBoHostServer(BoBoIdentityStore.Default, BoBoProtocol.DefaultTcpPort, true);
                    boboHostServer.AllowIncomingControl = allowIncomingControl;
                    boboHostServer.SessionStateChanged += OnBoBoHostSessionStateChanged;
                    boboHostServer.PrivacyCommandReceived += delegate(bool enable)
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                if (IsDisposed) return;
                                if (enable) ActivatePrivacyFromRemoteCommand();
                                else DeactivatePrivacyFromRemoteCommand();
                            }));
                        }
                        catch { }
                    };
                    boboHostServer.PeerPaired += delegate(BoBoAuthorizedPeer peer)
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                if (IsDisposed) return;
                                string name = string.IsNullOrEmpty(peer.DeviceName) ? peer.LastHost : peer.DeviceName;
                                CancelLocalPairing("已双向授权：" + name + "，对方现在可直接连控本机。");
                                SetStatus("双向授权完成：" + name + " 已加入本机 BoBo 可信名单。", Theme.PillTone.Success);
                                UpdateDiagnosticsUi();
                            }));
                        }
                        catch { }
                    };
                    boboHostServer.ClipboardTextReceived += delegate(string text)
                    {
                        if (IsDisposed || !IsHandleCreated || string.IsNullOrEmpty(text)) return;
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                try { Clipboard.SetText(text); } catch { }
                            }));
                        }
                        catch { }
                    };
                    boboHostServer.FileReceived += delegate(string senderName, string savedPath)
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                if (IsDisposed) return;
                                string fn = Path.GetFileName(savedPath ?? "");
                                string note = "已接收来自 " + senderName + " 的文件：" + fn + "（已存至 下载\\BoBoRemote）";
                                SetStatus(note, Theme.PillTone.Success);
                                deviceBanner.Set(note, Theme.PillTone.Success);
                            }));
                        }
                        catch { }
                    };
                }

                string err;
                if (!boboHostServer.Start(out err))
                {
                    SetStatus(err ?? "本机 BoBo V6 被控端口监听失败，请检查端口 49810 是否被占用。", Theme.PillTone.Warning);
                }
                ApplyHostSnapshotToUi();
            }
            catch (Exception ex)
            {
                SetStatus("启动本机 BoBo V6 服务异常：" + ex.Message, Theme.PillTone.Warning);
            }
        }

        private void OnBoBoHostSessionStateChanged(bool active, string peerName, bool requestPrivacy)
        {
            if (IsDisposed || !IsHandleCreated) return;
            Action apply = delegate
            {
                if (IsDisposed || (!active && boboHostServer != null && boboHostServer.HasActiveRemoteSession)) return;
                hostCurrentlyControlled = active;
                SyncActiveControlIndicators();
                if (active)
                {
                    if ((privacyConfig.AutoOnRemote || requestPrivacy) && !privacyGuard.SessionLocked)
                    {
                        if (!privacyCurtain.IsActive || privacyCurtain.IsPreview)
                        {
                            ClearPrivacyButtonFocus();
                            string msg;
                            if (privacyCurtain.Activate(privacyConfig, false, out msg))
                            {
                                privacyGuard.OnManualActivate();
                                ShowDisplayBanner(msg, Theme.PillTone.Info);
                            }
                        }
                    }
                    else
                    {
                        deviceBanner.Set("正在被 " + peerName + " 远程控制中（BoBo V6 全内置加密通道）。", Theme.PillTone.Info);
                    }
                }
                else
                {
                    bool hadCurtain = privacyCurtain.IsActive && !privacyCurtain.IsPreview;
                    if (hadCurtain)
                    {
                        ClearPrivacyButtonFocus();
                        privacyCurtain.Deactivate(false);
                    }

                    string message = hadCurtain
                        ? "远控会话已结束，已收起防窥壁纸并回到桌面。"
                        : "远控会话已结束，本机恢复就绪状态。";
                    Theme.PillTone tone = Theme.PillTone.Info;
                    if (privacyGuard.SessionLocked)
                    {
                        message = "远控会话已结束，Windows 处于锁定状态。";
                    }
                    else if (privacyConfig.LockOnRemoteDisconnect)
                    {
                        if (WindowsSession.TryLockWorkstation())
                        {
                            message = "远控会话已结束，已请求 Windows 自动锁屏。";
                        }
                        else
                        {
                            message = "远控会话已结束，Windows 未接受自动锁屏请求。";
                            tone = Theme.PillTone.Warning;
                        }
                    }
                    ShowDisplayBanner(message, tone);
                    deviceBanner.Set(message, tone);
                }
            };
            try
            {
                if (InvokeRequired)
                {
                    // Synchronous invoke on session start so physical curtain drops BEFORE frame 1 (<5ms)!
                    if (active && (privacyConfig.AutoOnRemote || requestPrivacy))
                        Invoke(apply);
                    else
                        BeginInvoke(apply);
                }
                else
                {
                    apply();
                }
            }
            catch { }
        }

        private Task BeginLocalPairingAsync()
        {
            if (!allowIncomingControl)
            {
                allowIncomingControl = true;
                if (boboHostServer != null) boboHostServer.AllowIncomingControl = true;
                SavePreferences();
            }
            EnsureBoBoHostServerStarted();
            if (boboHostServer == null || !boboHostServer.IsRunning)
            {
                localPairStatusLabel.Text = "本机 49810 端口未能启动，请检查防火墙或重启 BoBo远程。";
                SetStatus(localPairStatusLabel.Text, Theme.PillTone.Alert);
                return Task.FromResult(0);
            }

            BoBoLocalPairSession session = boboHostServer.BeginPairing(LocalPairLifetimeSeconds);
            localPairSession = session;
            localPairPin = session.Pin;
            localPairPinVisible = true;
            if (localPairEyeButton != null)
                Theme.ChangeGlyph(localPairEyeButton, Theme.Glyph.Eye);
            RefreshLocalPairPinDisplay();

            localPairCancelButton.Enabled = true;
            localPairGenerateButton.Enabled = true;
            localPairGenerateButton.Text = "刷新配对码";
            localPairGenerateButton.AccessibleName = "刷新本机 4 位临时配对码";
            localPairStatusLabel.Text = "已生成临时码 · 对方在 BoBo远程 输入此 4 位码即可完成双向授权（还剩 " + session.RemainingSeconds + " 秒）";
            localPairTimer.Start();
            SetStatus("已即时生成 4 位临时配对码（有效期 120 秒），台式机与笔记本双端均需打开 BoBo远程 完成匹配。", Theme.PillTone.Success);
            ApplyHostSnapshotToUi();
            return Task.FromResult(0);
        }

        private Task LocalPairPrimaryActionAsync()
        {
            return BeginLocalPairingAsync();
        }

        private void CancelLocalPairing()
        {
            CancelLocalPairing("已取消等待，临时配对码已失效。");
        }

        private void CancelLocalPairing(string status)
        {
            localPairTimer.Stop();
            if (boboHostServer != null) boboHostServer.CancelPairing();
            localPairSession = null;
            localPairPin = null;
            RefreshLocalPairPinDisplay();
            if (localPairStatusLabel != null) localPairStatusLabel.Text = status;
            if (localPairGenerateButton != null)
            {
                localPairGenerateButton.Text = "生成配对码";
                localPairGenerateButton.AccessibleName = "生成配对码";
                localPairGenerateButton.Enabled = true;
            }
            if (localPairCancelButton != null) localPairCancelButton.Enabled = false;
        }

        private void PollLocalPairingTick()
        {
            BoBoLocalPairSession session = localPairSession;
            if (session == null || IsDisposed)
            {
                localPairTimer.Stop();
                return;
            }
            int rem = session.RemainingSeconds;
            if (rem <= 0 || session.IsExpired)
            {
                CancelLocalPairing("临时配对码已过期，请重新点击「生成配对码」。");
                SetStatus("本次 4 位临时配对码已过期，未授权新设备。", Theme.PillTone.Warning);
                return;
            }
            if (localPairStatusLabel != null)
            {
                localPairStatusLabel.Text = "等待对方 BoBo远程 输入此 4 位码… 还剩 " + rem + " 秒";
            }
        }

        private void ShowAuthorizedPeersDialog()
        {
            List<BoBoAuthorizedPeer> list = BoBoIdentityStore.Default.ListAuthorizedPeers();
            using (var dlg = new Form())
            {
                dlg.Text = "BoBo远程 · 已授权设备管理";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(520, 380);
                dlg.BackColor = Theme.Workspace;
                if (appIcon != null) dlg.Icon = appIcon;

                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
                var title = Theme.Text("本机 BoBo 设备码：" + BoBoIdentityStore.Default.FormattedDeviceCode, Theme.TextSize.Section, Theme.Ink);
                title.Dock = DockStyle.Top;
                title.Height = 32;

                var sub = Theme.Text("以下设备已完成双向 4 位配对码授权，双方同时打开 BoBo远程 时可直接连控；移除后需重新配对。", Theme.TextSize.Small, Theme.Muted);
                sub.Dock = DockStyle.Top;
                sub.Height = 36;

                var listBox = new ListBox
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Microsoft YaHei UI", 9.5f),
                    BorderStyle = BorderStyle.FixedSingle,
                    IntegralHeight = false
                };

                Action reload = delegate
                {
                    list = BoBoIdentityStore.Default.ListAuthorizedPeers();
                    listBox.Items.Clear();
                    if (list.Count == 0)
                    {
                        listBox.Items.Add("（暂无已授权设备，生成 4 位配对码即可完成首次绑定）");
                    }
                    else
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            BoBoAuthorizedPeer p = list[i];
                            listBox.Items.Add(p.DeviceName + "  |  码 " + BoBoIdentityStore.FormatDeviceCode(p.DeviceCode) + "  |  " + p.LastHost);
                        }
                    }
                };
                reload();

                var btnBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 46,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(0, 8, 0, 0)
                };
                var closeBtn = Theme.Action("关闭", Theme.ButtonStyle.Primary);
                closeBtn.Width = 88;
                closeBtn.Height = 34;
                closeBtn.Click += delegate { dlg.Close(); };

                var revokeBtn = Theme.Action("撤销选中授权", Theme.ButtonStyle.Secondary);
                revokeBtn.Width = 128;
                revokeBtn.Height = 34;
                revokeBtn.Click += delegate
                {
                    int idx = listBox.SelectedIndex;
                    if (idx < 0 || idx >= list.Count) return;
                    BoBoAuthorizedPeer target = list[idx];
                    BoBoIdentityStore.Default.RevokePeer(target.NodeId);
                    if (boboHostServer != null) boboHostServer.DisconnectAllActiveStreams("授权已撤销");
                    reload();
                    UpdateDiagnosticsUi();
                    SetStatus("已撤销 " + target.DeviceName + " 的远程控制授权。", Theme.PillTone.Info);
                };

                btnBar.Controls.Add(closeBtn);
                btnBar.Controls.Add(revokeBtn);

                root.Controls.Add(listBox);
                root.Controls.Add(sub);
                root.Controls.Add(title);
                root.Controls.Add(btnBar);
                dlg.Controls.Add(root);
                dlg.ShowDialog(this);
            }
        }

        private async Task StartBuiltInFileTransferAsync()
        {
            if (fileTransferBusy) return;
            string raw = (remoteId.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                SetStatus("请先输入对方的 9 位 BoBo 设备码或 IP 地址。", Theme.PillTone.Warning);
                remoteId.Focus();
                return;
            }
            string resolved = ResolveBoBoHostTarget(raw);
            string err;
            string cleanHost = Engines.NormalizeHost(resolved, out err);
            if (cleanHost == null)
            {
                SetStatus(err ?? "无法解析目标主机地址，请输入有效 IP 或同网已发现的 9 位 BoBo 设备码。", Theme.PillTone.Warning);
                remoteId.Focus();
                return;
            }

            string selectedFile = null;
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择要发送到 " + cleanHost + " 的文件";
                ofd.CheckFileExists = true;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                selectedFile = ofd.FileName;
            }

            fileTransferBusy = true;
            if (rustDeskConnectBtn != null) rustDeskConnectBtn.Enabled = false;
            SetStatus("正在通过 BoBo V6 TLS 1.2 通道发送 " + Path.GetFileName(selectedFile) + "…", Theme.PillTone.Info);
            try
            {
                BoBoFileTransferProgress res = await BoBoStreamClient.SendFileAsync(
                    cleanHost,
                    BoBoProtocol.DefaultTcpPort,
                    selectedFile,
                    BoBoIdentityStore.Default,
                    delegate(BoBoFileTransferProgress p)
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                if (IsDisposed) return;
                                int pct = p.TotalBytes > 0 ? (int)Math.Min(100, (p.BytesTransferred * 100L) / p.TotalBytes) : 100;
                                SetStatus("正在发送 " + p.FileName + "（" + pct + "%）…", Theme.PillTone.Info);
                            }));
                        }
                        catch { }
                    },
                    System.Threading.CancellationToken.None);

                if (IsDisposed) return;
                if (res.Completed)
                {
                    recent.RememberRustDeskId(raw);
                    Remember(remoteId, recent.RustDeskIds,
                        "文件 " + res.FileName + " 已通过 SHA-256 校验并安全送达对方「下载\\BoBoRemote」。",
                        Theme.PillTone.Success);
                }
                else
                {
                    SetStatus(string.IsNullOrEmpty(res.Error) ? "文件发送失败。" : res.Error, Theme.PillTone.Alert);
                }
            }
            finally
            {
                fileTransferBusy = false;
                if (!IsDisposed && rustDeskConnectBtn != null)
                    rustDeskConnectBtn.Enabled = !string.IsNullOrWhiteSpace(remoteId.Text);
            }
        }

        private QualityOption ResolveStreamQuality(BoBoConnectionRoute route)
        {
            if (quality.SelectedIndex == AutoQualityProfile)
                return QualityOptions[route != null && route.PreferWeakNetworkProfile ? WeakNetProfile : WanWideProfile];
            return quality.SelectedItem as QualityOption ?? QualityOptions[WanWideProfile];
        }

        private Task StartStreamAsync(string confirmedHost, BoBoConnectionRoute route = null)
        {
            string message;
            string host = Engines.NormalizeHost(confirmedHost, out message);
            if (host == null)
            {
                SetStatus(message, Theme.PillTone.Warning);
                streamHost.Focus();
                return Task.FromResult(0);
            }

            StartNativeIntegratedStream(host, ResolveStreamQuality(route), route);
            return Task.FromResult(0);
        }

        private void StartNativeIntegratedStream(string host, QualityOption profile, BoBoConnectionRoute route)
        {
            string displayName = string.IsNullOrEmpty(clientPairHostName) ? host : clientPairHostName;
            string modeTag = profile.Height + "p" + profile.Fps + " · D3D11VA";
            int targetPort = (route != null && route.TargetPort > 0 && route.TargetPort <= 65535)
                ? route.TargetPort
                : BoBoProtocol.DefaultTcpPort;

            if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
            {
                if (string.Equals(activeNativeStreamForm.Host, host, StringComparison.OrdinalIgnoreCase)
                    && activeNativeStreamForm.TargetPort == targetPort)
                {
                    if (activeNativeStreamForm.WindowState == FormWindowState.Minimized)
                        activeNativeStreamForm.WindowState = FormWindowState.Normal;
                    activeNativeStreamForm.Activate();
                    return;
                }
                NativeStreamForm previous = activeNativeStreamForm;
                activeNativeStreamForm = null;
                previous.DisconnectAndClose(true);
            }

            var nativeForm = new NativeStreamForm(
                host,
                displayName,
                profile.Width,
                profile.Height,
                profile.Fps,
                profile.BitrateKbps,
                modeTag,
                windowedStream,
                useAbsoluteMouse,
                showStreamStats,
                playAudioOnHost,
                UsesHevc(profile),
                appIcon ?? Icon,
                autoRemotePrivacyOnConnect,
                lockHostOnDisconnect,
                hideFloatBallInFullscreen,
                showFloatBallInWindowed,
                sendSystemShortcuts,
                miniWindowOpacity,
                true,
                targetPort);

            nativeForm.LocalPrivacyActiveProvider = delegate { return privacyCurtain.IsActive; };

            nativeForm.PlayerProcessStarted += delegate(int pid)
            {
                if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                    activeStreamPid = pid;
                    if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                    {
                        activeStreamToolbar.UpdateSession(pid);
                    }
                    SyncActiveControlIndicators();
                }));
            };

            nativeForm.MouseModeChanged += delegate(bool free)
            {
                if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                    useAbsoluteMouse = free;
                    if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                        activeStreamToolbar.RefreshVisualState();
                }));
            };

            nativeForm.ToolbarHotkeyPressed += delegate
            {
                if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                BeginInvoke(new Action(delegate
                {
                    if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                        activeStreamToolbar.RevealFromHotkey();
                }));
            };

            nativeForm.BoundsMoved += delegate
            {
                if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                {
                    activeStreamToolbar.SetVisibleByMode(nativeForm.ShouldShowFloatingSquare);
                    activeStreamToolbar.RepositionToOwnerScreen();
                }
            };

            nativeForm.RestoreMainRequested += delegate
            {
                if (!IsDisposed) RestoreFromTray();
            };

            nativeForm.ToggleWindowedRequested += delegate
            {
                if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                windowedStream = !windowedStream;
                SavePreferences();
                nativeForm.SetWindowedMode(windowedStream);
                if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                {
                    activeStreamToolbar.SetVisibleByMode(nativeForm.ShouldShowFloatingSquare);
                    activeStreamToolbar.RefreshVisualState();
                }
            };

            nativeForm.ToggleHudRequested += delegate
            {
                if (IsDisposed || activeNativeStreamForm != nativeForm) return;
                showStreamStats = !showStreamStats;
                SavePreferences();
                nativeForm.SetHudVisible(showStreamStats);
                if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                    activeStreamToolbar.RefreshVisualState();
            };

            nativeForm.ToggleLocalPrivacyRequested += delegate
            {
                if (!IsDisposed) TogglePrivacyCurtain(false);
            };

            nativeForm.HostAudioPreferenceChanged += delegate(bool onHost)
            {
                playAudioOnHost = onHost;
                SavePreferences();
            };

            nativeForm.AutoRemotePrivacyPreferenceChanged += delegate(bool on)
            {
                autoRemotePrivacyOnConnect = on;
                SavePreferences();
            };

            nativeForm.LockHostOnDisconnectPreferenceChanged += delegate(bool on)
            {
                lockHostOnDisconnect = on;
                SavePreferences();
            };

            nativeForm.HideFloatBallPreferenceChanged += delegate(bool hide)
            {
                hideFloatBallInFullscreen = hide;
                SavePreferences();
                if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                    activeStreamToolbar.SetVisibleByMode(nativeForm.ShouldShowFloatingSquare);
            };

            nativeForm.ShowFloatBallWindowedPreferenceChanged += delegate(bool show)
            {
                showFloatBallInWindowed = show;
                SavePreferences();
                if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                    activeStreamToolbar.SetVisibleByMode(nativeForm.ShouldShowFloatingSquare);
            };

            nativeForm.SendSysKeysPreferenceChanged += delegate(bool on)
            {
                sendSystemShortcuts = on;
                SavePreferences();
            };

            nativeForm.MiniOpacityPreferenceChanged += delegate(int pct)
            {
                miniWindowOpacity = pct;
                SavePreferences();
            };

            nativeForm.StatusUpdated += delegate(string statusText)
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(delegate
                {
                    if (!IsDisposed) SetStatus(statusText, Theme.PillTone.Info);
                }));
            };

            nativeForm.PairRequired += delegate(string targetHost)
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(delegate
                {
                    if (!IsDisposed)
                    {
                        RestoreFromTray();
                        ShowClientPairPrompt(targetHost, displayName, true, "该主机需要 4 位配对码授权。", route);
                    }
                }));
            };

            nativeForm.SessionEnded += delegate
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(delegate
                {
                    if (activeNativeStreamForm == nativeForm)
                    {
                        activeNativeStreamForm = null;
                        activeConnectionRoute = null;
                        ShowConnectionRouteHint(null);
                        activeStreamPid = 0;
                        activeStreamHost = "";
                        if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                        {
                            activeStreamToolbar.CloseSafely();
                            activeStreamToolbar = null;
                        }
                        SyncActiveControlIndicators();
                    }
                }));
            };

            activeNativeStreamForm = nativeForm;
            activeConnectionRoute = route;
            TrackSessionRoute(nativeForm, route);
            activeStreamHost = host;
            activeStreamPid = 0;

            string routeLabel = route == null ? "指定地址" : route.Label;
            string note = "正在连接 " + displayName + " · " + routeLabel + " · " + profile.Height + "p / " + profile.Fps + " 帧";
            ConnectionTelemetry.RecordSubmitted("NativeV5", host, profile.Width,
                profile.Height, profile.Fps, profile.BitrateKbps, false, true, note);
            string rememberHost = (route != null && route.Kind == BoBoRouteKind.CustomRelay && !string.IsNullOrEmpty(clientPairDisplayInput))
                ? clientPairDisplayInput
                : host;
            recent.RememberMoonlightHost(rememberHost);
            Remember(streamHost, recent.MoonlightHosts, note, Theme.PillTone.Info);

            nativeForm.Show();
            ShowOrUpdateStreamToolbar(host, profile, 0, nativeForm);
            if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
            {
                activeStreamToolbar.SetNativeSessionAliveCallback(
                    delegate { return activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed; });
                activeStreamToolbar.SetVisibleByMode(nativeForm.ShouldShowFloatingSquare);
            }
            SyncActiveControlIndicators();
        }

        private StreamControlCenterModel BuildActiveControlCenterModel()
        {
            if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                return activeNativeStreamForm.BuildControlCenterModel();

            QualityOption p = quality.SelectedItem as QualityOption ?? QualityOptions[0];
            Screen scr = Screen.FromControl(this) ?? Screen.PrimaryScreen;
            return new StreamControlCenterModel
            {
                HostDisplayName = string.IsNullOrEmpty(clientPairHostName) ? activeStreamHost : clientPairHostName,
                DisplayDeviceName = "显示屏 1 (" + (string.IsNullOrEmpty(clientPairHostName) ? activeStreamHost : clientPairHostName) + ")",
                LocalDisplayWidth = scr.Bounds.Width,
                LocalDisplayHeight = scr.Bounds.Height,
                HostNativeWidth = p.Width,
                HostNativeHeight = p.Height,
                CurrentWidth = p.Width,
                CurrentHeight = p.Height,
                BitrateKbps = p.BitrateKbps,
                Fps = p.Fps,
                IsFullscreen = !windowedStream,
                MuteHostAudio = !playAudioOnHost,
                LocalPrivacyActive = privacyCurtain.IsActive,
                AutoRemotePrivacyOnConnect = autoRemotePrivacyOnConnect,
                LockHostOnDisconnect = lockHostOnDisconnect,
                MouseMode = useAbsoluteMouse ? ControlMouseMode.Free : ControlMouseMode.Locked,
                ShowNetworkStats = showStreamStats,
                HideFloatBallInFullscreen = hideFloatBallInFullscreen,
                ShowFloatBallInWindowed = showFloatBallInWindowed,
                OnSelectResolution = delegate(int w, int h, ControlResolutionMode mode) { RestartActiveStreamSession(); },
                OnSelectBitrate = delegate(int kbps, bool isAuto)
                {
                    int idx = kbps >= 28000 ? 6 : (kbps >= 20000 ? 4 : (kbps >= 14000 ? 2 : (kbps <= 6000 ? 3 : 0)));
                    SwitchActiveStreamQuality(idx);
                },
                OnSelectFps = delegate(int fps)
                {
                    int idx = fps >= 144 ? 7 : (fps >= 90 ? 4 : 0);
                    SwitchActiveStreamQuality(idx);
                },
                OnToggleMuteHostAudio = delegate(bool mute)
                {
                    playAudioOnHost = !mute;
                    SavePreferences();
                    RestartActiveStreamSession();
                },
                OnToggleLocalPrivacy = delegate { TogglePrivacyCurtain(false); },
                OnSelectMouseMode = delegate(ControlMouseMode m)
                {
                    useAbsoluteMouse = m != ControlMouseMode.Locked;
                    SavePreferences();
                    if (useAbsoluteMouse) Engines.ReleaseCursorClip();
                    RestartActiveStreamSession();
                },
                OnRestoreMainWindow = delegate { RestoreFromTray(); },
                OnToggleNetworkStats = delegate(bool on)
                {
                    showStreamStats = on;
                    SavePreferences();
                    RestartActiveStreamSession();
                },
                OnToggleHideFloatBallInFullscreen = delegate(bool hide)
                {
                    hideFloatBallInFullscreen = hide;
                    SavePreferences();
                    if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                        activeStreamToolbar.SetVisibleByMode(!hide || windowedStream);
                },
                OnToggleFullscreen = delegate
                {
                    windowedStream = !windowedStream;
                    SavePreferences();
                    RestartActiveStreamSession();
                },
                OnDisconnect = delegate { DisconnectActiveStreamSession(); }
            };
        }

        private void ShowOrUpdateStreamToolbar(string host, QualityOption profile, int pid, Form ownerForm = null)
        {
            activeStreamHost = host ?? "";
            activeStreamPid = pid;
            SyncActiveControlIndicators();

            if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
            {
                if (ownerForm != null) activeStreamToolbar.SetOwnerForm(ownerForm);
                activeStreamToolbar.UpdateSession(pid);
                if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                    activeStreamToolbar.SetVisibleByMode(activeNativeStreamForm.ShouldShowFloatingSquare);
                return;
            }

            activeStreamToolbar = new StreamToolbarOverlay(
                pid,
                string.IsNullOrEmpty(clientPairHostName) ? host : clientPairHostName,
                delegate { return useAbsoluteMouse; },
                delegate { return BuildActiveControlCenterModel(); },
                delegate
                {
                    useAbsoluteMouse = !useAbsoluteMouse;
                    SavePreferences();
                    if (useAbsoluteMouse) Engines.ReleaseCursorClip();
                    if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                        activeNativeStreamForm.SetFreeMouse(useAbsoluteMouse);
                    else
                        RestartActiveStreamSession();
                },
                delegate
                {
                    windowedStream = !windowedStream;
                    SavePreferences();
                    if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                    {
                        activeNativeStreamForm.SetWindowedMode(windowedStream);
                        if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                            activeStreamToolbar.SetVisibleByMode(activeNativeStreamForm.ShouldShowFloatingSquare);
                    }
                    else
                    {
                        RestartActiveStreamSession();
                    }
                },
                delegate(bool hide)
                {
                    hideFloatBallInFullscreen = hide;
                    SavePreferences();
                    if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                    {
                        bool show = activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed
                            ? activeNativeStreamForm.ShouldShowFloatingSquare
                            : (!hide || windowedStream);
                        activeStreamToolbar.SetVisibleByMode(show);
                    }
                },
                delegate { DisconnectActiveStreamSession(); },
                delegate
                {
                    activeStreamToolbar = null;
                    if (activeNativeStreamForm == null || activeNativeStreamForm.IsDisposed)
                    {
                        activeStreamPid = 0;
                        activeStreamHost = "";
                    }
                    SyncActiveControlIndicators();
                });
            if (ownerForm != null) activeStreamToolbar.SetOwnerForm(ownerForm);
            if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                activeStreamToolbar.SetVisibleByMode(activeNativeStreamForm.ShouldShowFloatingSquare);
            activeStreamToolbar.StartWatching();
        }

        private void SyncActiveControlIndicators()
        {
            if (IsDisposed) return;
            bool nativeActive = activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed;
            bool controlling = nativeActive || (activeStreamPid > 0 && Engines.IsProcessAlive(activeStreamPid));
            if (!controlling && activeStreamPid > 0)
            {
                activeStreamPid = 0;
                activeStreamHost = "";
            }
            bool anyActive = controlling || hostCurrentlyControlled;

            if (topDisconnectBtn != null && topBarGrid != null && topDisconnectCol >= 0 && topDisconnectGapCol >= 0)
            {
                string label = controlling ? "退出控制" : "断开被控";
                if (topDisconnectBtn.Text != label) topDisconnectBtn.Text = label;
                if (topDisconnectBtn.Visible != anyActive)
                {
                    topDisconnectBtn.Visible = anyActive;
                    float scale = CurrentDpiScale();
                    topBarGrid.SuspendLayout();
                    topBarGrid.ColumnStyles[topDisconnectCol].Width = anyActive ? (float)Math.Round(102f * scale) : 0f;
                    topBarGrid.ColumnStyles[topDisconnectGapCol].Width = anyActive ? (float)Math.Round(6f * scale) : 0f;
                    topBarGrid.ResumeLayout(true);
                }
            }

            if (detailPreviewCanvas != null)
            {
                bool controllingDetail = controlling
                    && !string.IsNullOrEmpty(detailDeviceAddress)
                    && string.Equals(activeStreamHost, detailDeviceAddress, StringComparison.OrdinalIgnoreCase);
                detailPreviewCanvas.SetControlling(controllingDetail);
            }
            UpdateAssistConnectButtons();
        }

        private bool streamQualityOrRestartBusy;

        private async void SwitchActiveStreamQuality(int newIndex)
        {
            if (newIndex < 0 || newIndex >= QualityOptions.Length) return;
            if (streamQualityOrRestartBusy) return;
            streamQualityOrRestartBusy = true;
            try
            {
                quality.SelectedIndex = newIndex;
                SavePreferences();
                if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                {
                    QualityOption p = ResolveStreamQuality(activeConnectionRoute);
                    string modeTag = p.Height + "p" + p.Fps + " · D3D11VA";
                    if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                        activeStreamToolbar.UpdateSession(activeNativeStreamForm.PlayerPid);
                    await activeNativeStreamForm.SwitchQualityAsync(p.Width, p.Height, p.Fps, p.BitrateKbps, modeTag);
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) SetStatus("切换画质失败：" + ex.Message, Theme.PillTone.Warning);
                return;
            }
            finally
            {
                streamQualityOrRestartBusy = false;
            }
            RestartActiveStreamSession();
        }

        private async void RestartActiveStreamSession()
        {
            if (streamQualityOrRestartBusy) return;
            string target = activeStreamHost;
            if (string.IsNullOrEmpty(target)) return;
            streamQualityOrRestartBusy = true;
            try
            {
                if (activeNativeStreamForm != null && !activeNativeStreamForm.IsDisposed)
                {
                    var p = ResolveStreamQuality(activeConnectionRoute);
                    await activeNativeStreamForm.SwitchQualityAsync(p.Width, p.Height, p.Fps, p.BitrateKbps, p.Height + "p" + p.Fps + " · D3D11VA");
                    return;
                }
                activeStreamPid = 0;
                if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
                    activeStreamToolbar.PauseWatchingForRestart();
                if (!IsDisposed)
                {
                    await StartStreamAsync(target, activeConnectionRoute);
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) SetStatus("重连远程会话失败：" + ex.Message, Theme.PillTone.Warning);
            }
            finally
            {
                streamQualityOrRestartBusy = false;
            }
        }

        private void DisconnectActiveStreamSession()
        {
            NativeStreamForm nativeForm = activeNativeStreamForm;
            activeNativeStreamForm = null;
            activeConnectionRoute = null;
            ShowConnectionRouteHint(null);
            activeStreamPid = 0;
            activeStreamHost = "";
            Engines.ReleaseCursorClip();
            if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
            {
                activeStreamToolbar.CloseSafely();
                activeStreamToolbar = null;
            }
            if (nativeForm != null && !nativeForm.IsDisposed)
            {
                nativeForm.DisconnectAndClose(true);
            }
            SyncActiveControlIndicators();
            SetStatus("已退出远程控制，BoBo V6 加密会话已断开。", Theme.PillTone.Info);
            deviceBanner.Set("已退出远程控制，BoBo V6 加密会话已断开。", Theme.PillTone.Info);
            RestoreFromTray();
        }

        private void DisconnectAllRemoteSessionsFromUi()
        {
            NativeStreamForm nativeForm = activeNativeStreamForm;
            bool hadNative = nativeForm != null && !nativeForm.IsDisposed;
            activeNativeStreamForm = null;
            activeConnectionRoute = null;
            ShowConnectionRouteHint(null);
            activeStreamPid = 0;
            activeStreamHost = "";
            Engines.ReleaseCursorClip();
            if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
            {
                activeStreamToolbar.CloseSafely();
                activeStreamToolbar = null;
            }
            if (hadNative)
            {
                nativeForm.DisconnectAndClose(true);
            }
            bool hadHostSession = boboHostServer != null && boboHostServer.HasActiveRemoteSession;
            if (hadHostSession)
            {
                boboHostServer.DisconnectAllActiveStreams("本机用户主动断开远控");
                hostCurrentlyControlled = false;
            }
            SyncActiveControlIndicators();
            string msg = (hadNative || hadHostSession) ? "已断开所有进行中的 BoBo 远控会话。" : "当前没有进行中的远控会话。";
            Theme.PillTone tone = (hadNative || hadHostSession) ? Theme.PillTone.Success : Theme.PillTone.Info;
            SetStatus(msg, tone);
            deviceBanner.Set(msg, tone);
        }

        /// <summary>
        /// 退出 BoBo远程 时，双端同步收尾远控会话并立即关闭内置 49810 监听端口，
        /// 从物理层面杜绝软件未打开时被任何设备连控。
        /// </summary>
        private void TeardownRemoteSessionsOnExit()
        {
            if (remoteSessionsTornDown) return;
            remoteSessionsTornDown = true;

            NativeStreamForm nativeForm = activeNativeStreamForm;
            activeNativeStreamForm = null;
            activeStreamPid = 0;
            activeStreamHost = "";
            if (activeStreamToolbar != null && !activeStreamToolbar.IsDisposed)
            {
                activeStreamToolbar.CloseSafely();
                activeStreamToolbar = null;
            }
            if (nativeForm != null && !nativeForm.IsDisposed)
            {
                try { nativeForm.DisconnectAndClose(true); } catch { }
            }

            BoBoHostServer server = boboHostServer;
            boboHostServer = null;
            if (server != null)
            {
                try { server.Dispose(); } catch { }
            }
            try
            {
                BoBoEmbeddedTailnet.Default.StateChanged -= OnEmbeddedTailnetStateChanged;
                BoBoEmbeddedTailnet.Default.Dispose();
            }
            catch { }
            try { BoBoCoreInterop.SetPrivacyInputBlock(false, null); } catch { }
            try { BoBoCoreInterop.RestoreSuperDisplay(); } catch { }
            try { PrivacyDisplayDimmer.RestoreAllSync(); } catch { }
        }

        private static void RestoreRecents(ComboBox combo, List<string> entries)
        {
            foreach (var entry in entries) combo.Items.Add(entry);
        }

        private void Remember(ComboBox combo, List<string> entries, string note, Theme.PillTone tone = Theme.PillTone.Success)
        {
            for (int i = combo.Items.Count - 1; i >= 0; i--)
            {
                if (combo.Items[i] is string) combo.Items.RemoveAt(i);
            }
            for (int i = 0; i < entries.Count; i++) combo.Items.Insert(i, entries[i]);
            if (entries.Count > 0) combo.SelectedIndex = 0;
            try
            {
                recent.Save();
                SetStatus(note, tone);
            }
            catch (IOException) { SetStatus(note + " 本次地址暂未保存。", tone == Theme.PillTone.Success ? Theme.PillTone.Warning : tone); }
            catch (UnauthorizedAccessException) { SetStatus(note + " 本次地址暂未保存。", tone == Theme.PillTone.Success ? Theme.PillTone.Warning : tone); }
        }

        private string SelectedHost()
        {
            var item = streamHost.SelectedItem as PeerOption;
            return item != null && streamHost.Text == item.ToString() ? item.Address : streamHost.Text;
        }

        private async Task DiscoverPeers()
        {
            discover.Enabled = false;
            if (boboHostServer != null) boboHostServer.SendDiscoveryProbeNow();
            SetStatus("正在扫描局域网在线 BoBo远程 设备与 Tailscale 组网设备…", Theme.PillTone.Info);
            try
            {
                await Task.Delay(350);
                List<BoBoDiscoveredPeer> lanList = boboHostServer != null
                    ? boboHostServer.GetDiscoveredLanPeers()
                    : new List<BoBoDiscoveredPeer>();
                TailnetLoadResult result = await Task.Run(() => Tailscale.LoadPeers(4200));
                if (IsDisposed) return;

                string kept = SelectedHost();
                int foundCount = 0;
                var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                streamHost.BeginUpdate();
                try
                {
                    streamHost.Items.Clear();
                    foreach (BoBoDiscoveredPeer lp in lanList)
                    {
                        if (string.IsNullOrWhiteSpace(lp.Host)) continue;
                        seenHosts.Add(lp.Host);
                        var synth = new TsPeer
                        {
                            HostName = lp.DeviceName + " (BoBo V6 · " + BoBoIdentityStore.FormatDeviceCode(lp.DeviceCode) + ")",
                            Ip = lp.Host,
                            Online = lp.AllowIncoming,
                            LinkText = lp.AllowIncoming ? "局域网直连 · BoBo V6 在线" : "局域网 · 已关闭被控"
                        };
                        streamHost.Items.Add(new PeerOption(synth));
                        foundCount++;
                    }
                    if (result.Ok && result.Peers != null)
                    {
                        foreach (var peer in result.Peers)
                        {
                            if (!string.IsNullOrWhiteSpace(peer.Ip) && seenHosts.Add(peer.Ip))
                            {
                                streamHost.Items.Add(new PeerOption(peer));
                                foundCount++;
                            }
                        }
                    }
                    foreach (string entry in recent.MoonlightHosts)
                    {
                        if (!string.IsNullOrWhiteSpace(entry) && seenHosts.Add(entry.Trim()))
                        {
                            streamHost.Items.Add(entry);
                        }
                    }
                    streamHost.SelectedIndex = -1;
                    streamHost.Text = kept;
                }
                finally { streamHost.EndUpdate(); }

                if (foundCount == 0)
                {
                    SetStatus("暂未发现其他已打开 BoBo远程 的同网设备，可直接输入对方 9 位 BoBo 设备码或 IP。", Theme.PillTone.Info);
                    return;
                }
                SetStatus("已发现 " + foundCount + " 台同网设备，请选择并连接。", Theme.PillTone.Success);
                if (Visible && !IsDisposed && Form.ActiveForm == this && streamHost.Visible && streamHost.Enabled)
                {
                    streamHost.Focus();
                    streamHost.DroppedDown = true;
                }
            }
            catch
            {
                if (!IsDisposed) SetStatus("扫描设备时出现异常，可直接手动输入对方 IP 或设备码。", Theme.PillTone.Warning);
            }
            finally
            {
                if (!IsDisposed) discover.Enabled = true;
            }
        }

        private async Task ProbeLink()
        {
            string rawTarget = SelectedHost();
            if (string.IsNullOrWhiteSpace(rawTarget)
                && SelectedConnectionRoute == BoBoRoutePreference.CustomRelayFallback
                && customRelayStore != null && customRelayStore.Relays.Count > 0)
            {
                await ProbeAndSelectBestCustomRelayAsync();
                return;
            }

            string error;
            string target = Engines.NormalizeHost(rawTarget, out error);
            if (target == null)
            {
                SetStatus(error, Theme.PillTone.Warning);
                streamHost.Focus();
                return;
            }
            discover.Enabled = false;
            SetStatus("正在探测到 " + target + " 的链路路径与往返延迟…", Theme.PillTone.Info);
            try
            {
                LinkProbeResult result = await Task.Run(() => Tailscale.Probe(target, 8000));
                if (!IsDisposed)
                {
                    Theme.PillTone tone;
                    if (result.Status == LinkProbeStatus.Reachable) tone = Theme.PillTone.Success;
                    else if (result.Status == LinkProbeStatus.Failed) tone = Theme.PillTone.Alert;
                    else tone = Theme.PillTone.Warning;
                    SetStatus(result.Text, tone);
                }
            }
            catch
            {
                if (!IsDisposed) SetStatus("链路探测失败，请检查双方网络或 Tailscale 登录状态。", Theme.PillTone.Alert);
            }
            finally
            {
                if (!IsDisposed) discover.Enabled = true;
            }
        }

        private void SetStatus(string text, Theme.PillTone tone)
        {
            assistBanner.Set(text, tone);
        }

        private void OpenGuide(string preferredFile = "低延迟调优与防窥指南.md")
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, preferredFile);
            if (!File.Exists(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
            if (File.Exists(path))
            {
                try
                {
                    var editor = Path.Combine(Environment.SystemDirectory, "notepad.exe");
                    var start = new ProcessStartInfo(editor, "\"" + path + "\"") { UseShellExecute = false };
                    using (Process reader = Process.Start(start)) { }
                    return;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            MessageBox.Show(this,
                "远程连接与文件互传：在「远程控制」输入对方 9 位 BoBo 设备码或局域网/Tailscale IP，首次将通过 4 位临时配对码完成 TLS 1.2 双端授权。\n低延迟串流：默认启用加密 UDP+FEC 与 D3D11VA 硬解，并支持超级屏自适应无黑边。\n防窥保护：在「防窥与显示」开启防窥壁纸与物理键鼠锁，快捷键 Ctrl+Alt+Shift+P 随时紧急退出。",
                "BoBo远程 使用与调优指南", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void RunOnEnter(ComboBox combo, Button button, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.Enter || combo.DroppedDown) return;
            args.SuppressKeyPress = true;
            if (button.Enabled) button.PerformClick();
        }

        private bool UsesHevc(QualityOption profile)
        {
            return profile.Fps >= 90 || profile.Width >= 3840 || preferHevc;
        }

        private string StreamAvailabilityText()
        {
            return BoBoCoreInterop.IsLoaded
                ? "BoBo V6 全内置原生内核（DXGI + MF H.264 + D3D11VA + WASAPI + TLS 1.2）"
                : "正在初始化内置 BoBoCore 原生内核";
        }

        private void UpdateStreamHint()
        {
            var selected = quality.SelectedItem as QualityOption;
            if (selected == null) return;
            streamModeBadge.UpdateStatus("V6 全内置内核 · 端口 49810", Theme.PillTone.Success);
            streamHint.Text = quality.SelectedIndex == AutoQualityProfile
                ? "稳定线路使用低延迟档，高 RTT 或中继时使用弱网档。"
                : "优先使用 UDP+FEC，连接后可在性能浮层查看传输协议。";
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (IsDisposed) return;
            Action apply = delegate
            {
                if (IsDisposed) return;
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    HandleSessionLocked();
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    HandleSessionUnlocked();
                }
            };
            if (InvokeRequired)
            {
                try { BeginInvoke(apply); } catch { }
            }
            else
            {
                apply();
            }
        }

        private void HandleSessionLocked()
        {
            if (IsDisposed) return;
            bool curtainWasActive = privacyCurtain.IsActive && !privacyCurtain.IsPreview;
            if (!privacyGuard.SessionLocked || curtainWasActive)
            {
                privacyGuard.OnSessionLock(curtainWasActive);
            }
            ClearPrivacyButtonFocus();
            // 任何锁屏动作（快捷键自动锁屏、Win+L 或系统锁屏）均立刻撤销物理屏防窥幕布，确保解锁回到桌面时不残留遮挡
            if (privacyCurtain.IsActive)
            {
                privacyCurtain.Deactivate(false);
            }
            UpdatePrivacyIndicators();
        }

        private void HandleSessionUnlocked()
        {
            if (IsDisposed) return;
            DateTime nowUtc = DateTime.UtcNow;
            bool hadCurtain = privacyCurtain.IsActive && !privacyCurtain.IsPreview;
            bool duplicateUnlock = !privacyGuard.SessionLocked
                && lastSessionUnlockUtc != DateTime.MinValue
                && (nowUtc - lastSessionUnlockUtc).TotalMilliseconds < 1000.0;
            if (duplicateUnlock && !privacyCurtain.IsActive)
            {
                return;
            }

            lastSessionUnlockUtc = nowUtc;
            privacyGuard.OnSessionUnlock(nowUtc);
            ClearPrivacyButtonFocus();
            // 解锁进入桌面后无条件保证防窥屏关闭，防止防窥层残留或被锁屏前后竞态重新拉起
            if (privacyCurtain.IsActive)
            {
                privacyCurtain.Deactivate(false);
            }
            UpdatePrivacyIndicators();
            if (hadCurtain)
            {
                ShowDisplayBanner("已解锁返回桌面，防窥屏壁纸已自动关闭。", Theme.PillTone.Info);
            }
        }

        private void BindPrivacyToggleButton(Button button)
        {
            if (button == null) return;
            button.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    lastPrivacyPointerControl = button;
                    lastPrivacyPointerDownUtc = DateTime.UtcNow;
                }
            };
            button.Click += delegate
            {
                bool pointerTriggered = false;
                if (lastPrivacyPointerControl == button
                    && lastPrivacyPointerDownUtc != DateTime.MinValue
                    && (DateTime.UtcNow - lastPrivacyPointerDownUtc).TotalMilliseconds <= 2000.0)
                {
                    try
                    {
                        pointerTriggered = button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
                    }
                    catch { pointerTriggered = true; }
                }
                lastPrivacyPointerControl = null;
                TogglePrivacyCurtain(false, pointerTriggered);
            };
        }

        private void ClearPrivacyButtonFocus()
        {
            try
            {
                Control active = ActiveControl;
                if (active != null && (active == toggleCurtainBtn || active == heroPrivacyToggle || active == topPrivacyBtn))
                {
                    ActiveControl = null;
                }
            }
            catch { }
        }

        private void SyncRemoteGuardTimer()
        {
            if (!remoteGuardTimer.Enabled) remoteGuardTimer.Start();
            if (!privacyConfig.AutoOnRemote)
            {
                bool wasAuto = privacyGuard.AutoActivated;
                privacyGuard.OnAutoGuardDisabled();
                if (wasAuto && privacyCurtain.IsActive && !privacyCurtain.IsPreview)
                {
                    privacyCurtain.Deactivate(false);
                }
            }
            UpdatePrivacyIndicators();
            SyncActiveControlIndicators();
        }

        /// <summary>主控端发起的幂等「立即开启被控端防窥」指令：已开启时保持不闪烁。</summary>
        private void ActivatePrivacyFromRemoteCommand()
        {
            if (IsDisposed || privacyGuard.SessionLocked) return;
            if (privacyCurtain.IsActive && !privacyCurtain.IsPreview) return;
            privacyGuard.OnManualActivate();
            ClearPrivacyButtonFocus();
            string msg;
            bool ok = privacyCurtain.Activate(privacyConfig, false, out msg);
            if (!ok) privacyGuard.OnManualExit(DateTime.UtcNow);
            ShowDisplayBanner(ok ? "主控端已触发防窥模式，本机物理屏已瞬间遮挡。" : (msg ?? "开启防窥壁纸失败。"),
                ok ? Theme.PillTone.Success : Theme.PillTone.Alert);
        }

        /// <summary>主控端发起的幂等「立即关闭被控端防窥」指令：不触发本机锁屏。</summary>
        private void DeactivatePrivacyFromRemoteCommand()
        {
            if (IsDisposed || !privacyCurtain.IsActive) return;
            privacyGuard.OnManualExit(DateTime.UtcNow);
            ClearPrivacyButtonFocus();
            privacyCurtain.Deactivate(false);
            ShowDisplayBanner("主控端已关闭防窥模式。", Theme.PillTone.Info);
        }

        private Task PollRemoteGuardTick()
        {
            if (guardPollBusy || IsDisposed) return Task.FromResult(0);
            guardPollBusy = true;
            long pollGeneration = privacyGuard.Generation;
            try
            {
                bool activeSession = boboHostServer != null && boboHostServer.HasActiveRemoteSession;
                if (hostCurrentlyControlled != activeSession)
                {
                    hostCurrentlyControlled = activeSession;
                }
                SyncActiveControlIndicators();

                if (!privacyConfig.AutoOnRemote || privacyGuard.SessionLocked) return Task.FromResult(0);

                PrivacyGuardAction action = privacyGuard.EvaluateAutoGuardPoll(
                    pollGeneration, activeSession, privacyCurtain.IsActive, privacyCurtain.IsPreview,
                    privacyConfig.AutoOnRemote, DateTime.UtcNow);

                if (action == PrivacyGuardAction.ActivateAuto)
                {
                    ClearPrivacyButtonFocus();
                    string msg;
                    if (privacyCurtain.Activate(privacyConfig, false, out msg))
                    {
                        ShowDisplayBanner(msg, Theme.PillTone.Success);
                    }
                    else
                    {
                        privacyGuard.OnManualExit(DateTime.UtcNow);
                    }
                }
                else if (action == PrivacyGuardAction.DeactivateAuto)
                {
                    ClearPrivacyButtonFocus();
                    privacyCurtain.Deactivate(false);
                    ShowDisplayBanner("远控会话已结束，已自动收起防窥壁纸。", Theme.PillTone.Info);
                }
            }
            catch { }
            finally { guardPollBusy = false; }
            return Task.FromResult(0);
        }

        internal void TogglePrivacyCurtain(bool fromLocalHotkey)
        {
            TogglePrivacyCurtain(fromLocalHotkey, !fromLocalHotkey);
        }

        internal void TogglePrivacyCurtain(bool fromLocalHotkey, bool pointerTriggered)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (privacyCurtain.IsActive)
            {
                bool triggerLock = fromLocalHotkey && !privacyCurtain.IsPreview;
                privacyGuard.OnManualExit(nowUtc);
                ClearPrivacyButtonFocus();
                privacyCurtain.Deactivate(triggerLock);
                ShowDisplayBanner(triggerLock && privacyConfig.LockOnLocalExit
                    ? "已退出防窥屏并触发锁屏，解锁返回桌面后防窥将保持关闭。"
                    : "已退出防窥屏壁纸。", Theme.PillTone.Info);
                return;
            }
            if (fromLocalHotkey && !privacyGuard.CanActivateFromHotkey(nowUtc))
            {
                return;
            }
            if (!fromLocalHotkey && !privacyGuard.CanActivateFromUi(nowUtc, pointerTriggered))
            {
                return;
            }
            privacyGuard.OnManualActivate();
            ClearPrivacyButtonFocus();
            string msg;
            bool ok = privacyCurtain.Activate(privacyConfig, false, out msg);
            if (!ok) privacyGuard.OnManualExit(nowUtc);
            ShowDisplayBanner(msg, ok ? Theme.PillTone.Success : Theme.PillTone.Alert);
        }

        private void OnPrivacyCurtainStateChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(UpdatePrivacyIndicators)); } catch { }
                return;
            }
            UpdatePrivacyIndicators();
        }

        /// <summary>
        /// 统一窗口消息过滤器：
        /// 1. 鼠标滚轮全局路由到当前 SlimScrollViewport；
        /// 2. 纯无边框窗体的 8 方向边缘/四角平滑拉伸与光标反馈（零经典标题栏残留）。
        /// </summary>
        private sealed class WindowMessageFilter : IMessageFilter
        {
            private const int WmMouseWheel = 0x020A;
            private const int WmMouseMove = 0x0200;
            private const int WmLButtonDown = 0x0201;
            private const int WmLButtonUp = 0x0202;
            private const int WmCaptureChanged = 0x0215;

            private enum ResizeEdge { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

            private readonly MainForm owner;
            private ResizeEdge activeEdge = ResizeEdge.None;
            private Point dragStartScreen;
            private Rectangle dragStartBounds;
            private bool cursorOverrideActive;

            internal WindowMessageFilter(MainForm owner) { this.owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                if (owner.IsDisposed || !owner.Visible) return false;

                if (m.Msg == WmMouseWheel)
                {
                    return HandleWheel(ref m);
                }

                if (activeEdge != ResizeEdge.None)
                {
                    if (m.Msg == WmMouseMove)
                    {
                        ApplyResizeDrag(Control.MousePosition);
                        return true;
                    }
                    if (m.Msg == WmLButtonUp || m.Msg == WmCaptureChanged)
                    {
                        EndResizeDrag();
                        return m.Msg == WmLButtonUp;
                    }
                }

                if (owner.WindowState != FormWindowState.Normal) return false;
                if (Form.ActiveForm != owner && m.Msg == WmLButtonDown)
                {
                    // Allow click-through focus unless strictly on outer border
                }

                if (m.Msg == WmMouseMove)
                {
                    if (!IsMessageForOwnerTree(m.HWnd))
                    {
                        ClearResizeCursor();
                        return false;
                    }
                    ResizeEdge hover = HitTestEdge(Control.MousePosition);
                    if (hover != ResizeEdge.None)
                    {
                        Cursor.Current = CursorForEdge(hover);
                        cursorOverrideActive = true;
                        return false;
                    }
                    ClearResizeCursor();
                    return false;
                }

                if (m.Msg == WmLButtonDown)
                {
                    if (!IsMessageForOwnerTree(m.HWnd)) return false;
                    ResizeEdge edge = HitTestEdge(Control.MousePosition);
                    if (edge != ResizeEdge.None)
                    {
                        activeEdge = edge;
                        dragStartScreen = Control.MousePosition;
                        dragStartBounds = owner.Bounds;
                        owner.Capture = true;
                        Cursor.Current = CursorForEdge(edge);
                        return true;
                    }
                }

                return false;
            }

            private bool IsMessageForOwnerTree(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero) return false;
                if (hwnd == owner.Handle) return true;
                Control c = Control.FromHandle(hwnd);
                return c != null && c.FindForm() == owner;
            }

            private ResizeEdge HitTestEdge(Point screenPt)
            {
                Rectangle bounds = owner.Bounds;
                if (!bounds.Contains(screenPt)) return ResizeEdge.None;
                float scale = Theme.GetDpiScale(owner);
                int grip = Math.Max(4, (int)Math.Round(5 * scale));
                int corner = Math.Max(9, (int)Math.Round(10 * scale));
                int dxL = screenPt.X - bounds.Left;
                int dxR = bounds.Right - 1 - screenPt.X;
                int dyT = screenPt.Y - bounds.Top;
                int dyB = bounds.Bottom - 1 - screenPt.Y;

                // Keep top-right window caption buttons easy to click (only outermost 3px triggers top resize there)
                int captionZoneW = (int)Math.Round(132 * scale);
                bool inTopCaptionButtons = dxR < captionZoneW && dyT >= 3 && dyT <= grip && dxR >= 3;
                if (inTopCaptionButtons) return ResizeEdge.None;

                bool left = dxL <= grip;
                bool right = dxR <= grip;
                bool top = dyT <= grip;
                bool bottom = dyB <= grip;

                if (dxL <= corner && dyT <= corner && (left || top)) return ResizeEdge.TopLeft;
                if (dxR <= corner && dyT <= corner && (right || top)) return ResizeEdge.TopRight;
                if (dxL <= corner && dyB <= corner && (left || bottom)) return ResizeEdge.BottomLeft;
                if (dxR <= corner && dyB <= corner && (right || bottom)) return ResizeEdge.BottomRight;
                if (left) return ResizeEdge.Left;
                if (right) return ResizeEdge.Right;
                if (top) return ResizeEdge.Top;
                if (bottom) return ResizeEdge.Bottom;
                return ResizeEdge.None;
            }

            private static Cursor CursorForEdge(ResizeEdge edge)
            {
                switch (edge)
                {
                    case ResizeEdge.Left:
                    case ResizeEdge.Right: return Cursors.SizeWE;
                    case ResizeEdge.Top:
                    case ResizeEdge.Bottom: return Cursors.SizeNS;
                    case ResizeEdge.TopLeft:
                    case ResizeEdge.BottomRight: return Cursors.SizeNWSE;
                    case ResizeEdge.TopRight:
                    case ResizeEdge.BottomLeft: return Cursors.SizeNESW;
                    default: return Cursors.Default;
                }
            }

            private void ApplyResizeDrag(Point currentScreen)
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    EndResizeDrag();
                    return;
                }
                int dx = currentScreen.X - dragStartScreen.X;
                int dy = currentScreen.Y - dragStartScreen.Y;
                int minW = Math.Max(640, owner.MinimumSize.Width);
                int minH = Math.Max(480, owner.MinimumSize.Height);

                int l = dragStartBounds.Left;
                int t = dragStartBounds.Top;
                int r = dragStartBounds.Right;
                int b = dragStartBounds.Bottom;

                if (activeEdge == ResizeEdge.Left || activeEdge == ResizeEdge.TopLeft || activeEdge == ResizeEdge.BottomLeft)
                    l = Math.Min(r - minW, dragStartBounds.Left + dx);
                if (activeEdge == ResizeEdge.Right || activeEdge == ResizeEdge.TopRight || activeEdge == ResizeEdge.BottomRight)
                    r = Math.Max(l + minW, dragStartBounds.Right + dx);
                if (activeEdge == ResizeEdge.Top || activeEdge == ResizeEdge.TopLeft || activeEdge == ResizeEdge.TopRight)
                    t = Math.Min(b - minH, dragStartBounds.Top + dy);
                if (activeEdge == ResizeEdge.Bottom || activeEdge == ResizeEdge.BottomLeft || activeEdge == ResizeEdge.BottomRight)
                    b = Math.Max(t + minH, dragStartBounds.Bottom + dy);

                Cursor.Current = CursorForEdge(activeEdge);
                owner.SetBounds(l, t, r - l, b - t);
            }

            private void EndResizeDrag()
            {
                activeEdge = ResizeEdge.None;
                if (owner.Capture) owner.Capture = false;
                ClearResizeCursor();
            }

            private void ClearResizeCursor()
            {
                if (cursorOverrideActive)
                {
                    cursorOverrideActive = false;
                    Cursor.Current = Cursors.Default;
                }
            }

            private bool HandleWheel(ref Message m)
            {
                Control target = Control.FromHandle(m.HWnd);
                if (target != null)
                {
                    var combo = target as ComboBox;
                    if (combo != null && combo.DroppedDown) return false;
                }
                Point cursor = Control.MousePosition;
                if (!owner.Bounds.Contains(cursor)) return false;

                foreach (Control page in owner.pages)
                {
                    var vp = page as Theme.SlimScrollViewport;
                    if (vp == null || !vp.Visible || !vp.IsHandleCreated) continue;
                    if (vp.RectangleToScreen(vp.ClientRectangle).Contains(cursor))
                    {
                        int delta = (short)((long)m.WParam >> 16);
                        vp.ScrollByWheel(delta);
                        return true;
                    }
                }
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointStruct { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectStruct { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfoStruct
        {
            public PointStruct Reserved;
            public PointStruct MaxSize;
            public PointStruct MaxPosition;
            public PointStruct MinTrackSize;
            public PointStruct MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfoStruct
        {
            public int CbSize;
            public RectStruct MonitorArea;
            public RectStruct WorkArea;
            public int Flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoStruct lpmi);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private sealed class QualityOption
        {
            private readonly string label;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int Fps;
            internal readonly int BitrateKbps;

            internal QualityOption(string label, int width, int height, int fps, int bitrateKbps)
            {
                this.label = label;
                Width = width;
                Height = height;
                Fps = fps;
                BitrateKbps = bitrateKbps;
            }

            public override string ToString() { return label; }
        }

        private sealed class PeerOption
        {
            private readonly TsPeer peer;
            internal string Address { get { return peer.Ip; } }
            internal PeerOption(TsPeer peer) { this.peer = peer; }
            public override string ToString() { return peer.DisplayText(); }
        }
    }
}
