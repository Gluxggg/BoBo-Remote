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
        private readonly Button discover = Theme.Action("发现设备", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
        private readonly Theme.StatusBanner assistBanner = new Theme.StatusBanner("输入伙伴设备地址或 9 位设备码即可连接；首次高速串流会在此展开 4 位配对码授权。");
        private readonly Theme.Badge streamModeBadge = new Theme.Badge("检测串流组件", Theme.PillTone.Neutral);
        private readonly Label streamHint = Theme.Text("", Theme.TextSize.Small, Theme.Muted);
        private readonly Timer localPairTimer = new Timer();

        private Label localAssistAddressLabel;
        private Label localPairCodeLabel;
        private Label localPairStatusLabel;
        private Button localPairGenerateButton;
        private Button localPairCancelButton;
        private Button localPairEyeButton;
        private bool localPairPinVisible = true;
        private string localPairPin;
        private bool localPairPollBusy;
        private bool localPairPreparing;
        private int localPairGeneration;
        private LocalPairSession localPairSession;

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
        private const int Lan2K90WideProfile = 4;
        private const int Lan2K90TallProfile = 5;

        private const int HotkeyPrivacyId = 0x51A0;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkP = 0x50;
        private const int WmHotkey = 0x0312;
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

        private const string MissingStreamEngineMessage = "当前目录缺少串流组件。请完整解压 BoBo远程 便携版后再试。";
        private const int AssistPageHeight = 640;
        private const int LocalPairLifetimeSeconds = 120;

        private static readonly QualityOption[] QualityOptions =
        {
            new QualityOption("外网流畅自适应 · 1080p · 60 帧 · 10 Mbps（推荐）", 1920, 1080, 60, 10000),
            new QualityOption("外网 16:10 · 1920×1200 · 60 帧 · 10 Mbps", 1920, 1200, 60, 10000),
            new QualityOption("外网高清 · 1440p · 60 帧 · 14 Mbps", 2560, 1440, 60, 14000),
            new QualityOption("弱网抗丢包 · 720p · 60 帧 · 6 Mbps", 1280, 720, 60, 6000),
            new QualityOption("高速 / 局域网 · 2560×1440 · 90 帧 · 22 Mbps", 2560, 1440, 90, 22000),
            new QualityOption("高速 / 局域网 16:10 · 2560×1600 · 90 帧 · 24 Mbps", 2560, 1600, 90, 24000),
            new QualityOption("局域网高刷 · 2560×1440 · 120 帧 · 30 Mbps", 2560, 1440, 120, 30000),
            new QualityOption("极速电竞高刷 · 2560×1440 · 144 帧 · 36 Mbps", 2560, 1440, 144, 36000),
            new QualityOption("4K 超清办公 · 3840×2160 · 60 帧 · 35 Mbps", 3840, 2160, 60, 35000)
        };

        private readonly RecentStore recent;
        private readonly PrivacyWallpaperConfig privacyConfig;
        private readonly PrivacyCurtainManager privacyCurtain = new PrivacyCurtainManager();
        private readonly PrivacyGuardState privacyGuard = new PrivacyGuardState();
        private readonly Timer remoteGuardTimer = new Timer();
        private HostSnapshot hostSnapshot = new HostSnapshot();
        private NotifyIcon trayIcon;
        private Icon appIcon;
        private bool forceExit;
        private bool guardPollBusy;
        private bool hotkeyRegistered;
        private bool sessionEventsHooked;
        private TableLayoutPanel assistDesk;
        private Control assistLocalCard;
        private Control assistPartnerCard;
        private Timer onboardingDelayTimer;
        private WindowMessageFilter windowFilter;

        internal MainForm() : this(false) { }

        internal MainForm(bool forceTour)
        {
            forceOnboardingOnLaunch = forceTour;
            SuspendLayout();
            Text = "BoBo远程";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MinimizeBox = true;
            MaximizeBox = true;
            ClientSize = new Size(1060, 700);
            MinimumSize = new Size(860, 560);
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
            privacyConfig = PrivacyWallpaperConfig.Load();
            streamModeBadge.AlignRight = true;
            remoteId.MaxLength = 64;
            streamHost.MaxLength = 253;
            Theme.Cue(remoteId, "请输入对方的 9 位设备码");
            Theme.Cue(streamHost, "请输入对方局域网 IP、Tailscale IP 或公网 IPv6");
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
            SystemEvents.SessionSwitch += OnSessionSwitch;
            sessionEventsHooked = true;
            remoteGuardTimer.Interval = 2500;
            remoteGuardTimer.Tick += async delegate { await PollRemoteGuardTick(); };
            localPairTimer.Interval = 1200;
            localPairTimer.Tick += async delegate { await PollLocalPairingAsync(); };

            Controls.Add(BuildChrome());
            SetupTrayIcon();
            windowFilter = new WindowMessageFilter(this);
            Application.AddMessageFilter(windowFilter);

            AutoScaleDimensions = new SizeF(96f, 96f);
            ResumeLayout(false);
            PerformAutoScale();
            PerformLayout();
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

            // Windows 11 crisp rounded window corners + native OS drop shadow (gracefully ignored on Win10)
            try
            {
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyPrivacyId)
            {
                TogglePrivacyCurtain(true);
                return;
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
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(Width, area.Width - 20);
            int height = Math.Min(Height, area.Height - 20);
            if (width != Width || height != Height)
                SetBounds(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
            SyncRemoteGuardTimer();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ResizeToWorkingArea(false);

            // First launch (or --tour): run spotlight onboarding guide once initial layout & DPI settle
            if (!onboardingScheduled && (forceOnboardingOnLaunch || !OnboardingStore.IsCompleted()))
            {
                onboardingScheduled = true;
                onboardingDelayTimer = new Timer { Interval = 280 };
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
            float scale = DeviceDpi / 96f;
            int preferredW = (int)Math.Round(1060 * scale);
            int preferredH = (int)Math.Round(700 * scale);
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
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("彻底退出 BoBo远程", null, delegate { forceExit = true; Close(); });

            trayIcon = new NotifyIcon
            {
                Icon = appIcon ?? SystemIcons.Application,
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
                    trayIcon.ShowBalloonTip(2000, "BoBo远程 仍在后台运行",
                        privacyConfig.AutoOnRemote ? "防窥屏壁纸守护运行中，双击托盘图标可打开主界面。" : "双击右下角托盘图标可随时恢复窗口。",
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
            if (activeOnboardingTour != null && !activeOnboardingTour.IsDisposed)
            {
                try { activeOnboardingTour.Close(); } catch { }
                activeOnboardingTour = null;
            }
            localPairPreparing = false;
            Process pendingPairProcess = TakeClientPairProcess();
            if (pendingPairProcess != null)
            {
                try { if (!pendingPairProcess.HasExited) pendingPairProcess.Kill(); } catch { }
                try { pendingPairProcess.Dispose(); } catch { }
            }
            if (hotkeyRegistered && IsHandleCreated)
            {
                try { UnregisterHotKey(Handle, HotkeyPrivacyId); } catch { }
                hotkeyRegistered = false;
            }
            privacyCurtain.Dispose();
            DisposePreviewImage();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
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
            Theme.Row(desk, 198, localCard);
            Theme.Row(desk, 16);

            // Card 2: 远控伙伴设备 (exact UU Image 3 structure with clean Stream / RustDesk toggle!)
            var partnerCard = BuildPartnerDeviceCard();
            assistPartnerCard = partnerCard;
            assistStreamCardRow = desk.RowCount;
            Theme.Row(desk, ClientStreamCardHeight(), partnerCard);
            Theme.Row(desk, 16);
            Theme.Row(desk, 40, assistBanner);

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
            Theme.FixedColumn(header, 118);
            Theme.FixedColumn(header, 8);
            Theme.FixedColumn(header, 76);

            localPairGenerateButton = Theme.Action("生成配对码", Theme.ButtonStyle.Primary);
            localPairGenerateButton.Click += async delegate { await LocalPairPrimaryActionAsync(); };
            localPairCancelButton = Theme.Action("取消", Theme.ButtonStyle.Secondary);
            localPairCancelButton.Enabled = false;
            localPairCancelButton.Click += delegate { CancelLocalPairing(); };

            Theme.Row(header, 34, Theme.Text("本设备", Theme.TextSize.Section, Theme.Ink), null, localPairGenerateButton, null, localPairCancelButton);
            Theme.Row(root, 58, header);
            Theme.Row(root, 1, Theme.Rule());

            // Card body: Left 本机地址, Middle 一次性 4 位配对码 + eye button, Right stacked actions (never squishes IP at 860px)
            var body = Theme.Grid(5);
            body.Padding = new Padding(22, 16, 22, 14);
            Theme.FillColumn(body);
            Theme.FixedColumn(body, 16);
            Theme.FixedColumn(body, 176);
            Theme.FixedColumn(body, 16);
            Theme.FixedColumn(body, 116);

            var addrCol = Theme.Grid(1);
            Theme.FillColumn(addrCol);
            Theme.Row(addrCol, 20, Theme.Text("本机连接地址（推荐直连）", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(addrCol, 6);
            localAssistAddressLabel = Theme.Text("正在检测地址…", Theme.TextSize.BigCode, Theme.Ink);
            localAssistAddressLabel.AutoEllipsis = true;
            Theme.Row(addrCol, 34, localAssistAddressLabel);
            localPairStatusLabel = Theme.Text("点击右上角「生成配对码」允许新电脑首次连接本机", Theme.TextSize.Small, Theme.Muted);
            localPairStatusLabel.AutoEllipsis = true;
            Theme.Row(addrCol, 22, localPairStatusLabel);
            Theme.FillRow(addrCol);

            var pinCol = Theme.Grid(1);
            Theme.FillColumn(pinCol);
            Theme.Row(pinCol, 20, Theme.Text("临时配对码（120 秒有效）", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(pinCol, 6);

            var pinLine = Theme.Grid(3);
            Theme.FillColumn(pinLine);
            Theme.FixedColumn(pinLine, 6);
            Theme.FixedColumn(pinLine, 32);
            localPairCodeLabel = Theme.Text("• • • •", Theme.TextSize.BigCode, Theme.Ink);
            localPairCodeLabel.AccessibleName = "本机 4 位配对码";
            localPairEyeButton = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Eye);
            localPairEyeButton.AccessibleName = "显示或隐藏配对码";
            localPairEyeButton.Click += delegate
            {
                localPairPinVisible = !localPairPinVisible;
                Theme.ChangeGlyph(localPairEyeButton, localPairPinVisible ? Theme.Glyph.Eye : Theme.Glyph.EyeOff);
                RefreshLocalPairPinDisplay();
            };
            Theme.Row(pinLine, 34, localPairCodeLabel, null, localPairEyeButton);
            Theme.Row(pinCol, 34, pinLine);
            Theme.Row(pinCol, 22, Theme.Text("每次生成自动刷新", Theme.TextSize.Small, Theme.Muted));
            Theme.FillRow(pinCol);

            var actionCol = Theme.Grid(1);
            Theme.FillColumn(actionCol);
            var copyShareBtn = Theme.Action("复制并分享", Theme.ButtonStyle.Secondary);
            copyShareBtn.Click += delegate { CopyLocalShareInfo(); };
            var openRdBtn = Theme.Action("RustDesk 码", Theme.ButtonStyle.Secondary);
            openRdBtn.Click += async delegate { await OpenLocalRustDeskOnAssist(openRdBtn); };
            Theme.FillRow(actionCol);
            Theme.Row(actionCol, 34, copyShareBtn);
            Theme.Row(actionCol, 8);
            Theme.Row(actionCol, 34, openRdBtn);
            Theme.FillRow(actionCol);

            Theme.Row(body, 96, addrCol, null, pinCol, null, actionCol);
            Theme.FillRow(body);
            Theme.Row(root, 128, body);
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
            localAssistAddressLabel.Text = preferred;
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
            string addr = !string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4)
                ? hostSnapshot.TailscaleIpv4
                : !string.IsNullOrEmpty(hostSnapshot.LanIpv4)
                    ? hostSnapshot.LanIpv4
                    : hostSnapshot.PublicIpv6;
            if (string.IsNullOrEmpty(addr))
            {
                SetStatus("暂未检测到本机可用网络地址，请先刷新或使用 RustDesk 设备码。", Theme.PillTone.Warning);
                return;
            }
            string share = "BoBo远程连接地址：" + addr
                + (string.IsNullOrEmpty(localPairPin) ? "" : "  配对码：" + localPairPin);
            try
            {
                Clipboard.SetText(share);
                SetStatus("已复制到剪贴板：" + share, Theme.PillTone.Success);
            }
            catch
            {
                SetStatus("无法写入剪贴板，请手动记录：" + share, Theme.PillTone.Warning);
            }
        }

        private Control BuildPartnerDeviceCard()
        {
            var card = new Theme.Card { Padding = new Padding(0) };
            var layout = Theme.Grid(1);
            Theme.FillColumn(layout);
            clientPairCard = card;
            clientPairCardLayout = layout;

            // Header section matching UU Image 3 ("远控伙伴设备" + subtitle)
            var header = Theme.Grid(1);
            header.Padding = new Padding(22, 16, 22, 14);
            Theme.FillColumn(header);
            Theme.Row(header, 24, Theme.Text("远控伙伴设备", Theme.TextSize.Section, Theme.Ink));
            Theme.Row(header, 4);
            Theme.Row(header, 20, Theme.Text("输入伙伴的局域网/组网地址（高清高帧率串流），或 9 位设备码（临时协助与文件互传）", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(layout, 78, header);
            Theme.Row(layout, 1, Theme.Rule());

            // Clean underline mode switcher inside card body
            var modeStrip = Theme.Grid(5);
            modeStrip.Padding = new Padding(22, 12, 22, 0);
            Theme.FixedColumn(modeStrip, 136);
            Theme.FixedColumn(modeStrip, 18);
            Theme.FixedColumn(modeStrip, 174);
            Theme.FillColumn(modeStrip);
            Theme.FixedColumn(modeStrip, 184);

            assistModeStreamTab = Theme.Action("低延迟高清串流", Theme.ButtonStyle.UnderlineTab);
            assistModeRustDeskTab = Theme.Action("临时协助 / 文件传输", Theme.ButtonStyle.UnderlineTab);
            assistModeStreamTab.Click += delegate { SelectAssistMode(0); };
            assistModeRustDeskTab.Click += delegate { SelectAssistMode(1); };

            Theme.Row(modeStrip, 32, assistModeStreamTab, null, assistModeRustDeskTab, null, streamModeBadge);
            Theme.Row(layout, 44, modeStrip);

            // Mode content host
            assistModeHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Paper };
            assistStreamModePanel = BuildStreamModePanel();
            assistRustDeskModePanel = BuildRustDeskModePanel();
            assistModeHost.Controls.Add(assistStreamModePanel);
            assistModeHost.Controls.Add(assistRustDeskModePanel);
            assistModeHostRow = layout.RowCount;
            Theme.Row(layout, 146, assistModeHost);

            // Expandable inline 4-digit PIN pairing panel (for first-time Moonlight/Sunshine pairing)
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

            Theme.Row(panel, 20, Theme.Text("伙伴设备地址", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(panel, 8);

            var row = Theme.Grid(5);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 118);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 116);

            streamConnectBtn = Theme.Action("连接", Theme.ButtonStyle.Primary);
            streamConnectBtn.Click += async delegate { await ConnectToDeviceAsync(SelectedHost(), null); };
            discover.Click += async delegate { await DiscoverPeers(); };
            streamHost.KeyDown += delegate(object sender, KeyEventArgs args) { RunOnEnter(streamHost, streamConnectBtn, args); };
            streamHost.TextChanged += delegate { UpdateAssistConnectButtons(); };

            Theme.Row(row, 38, Theme.WrapInput(streamHost), null, streamConnectBtn, null, discover);
            Theme.Row(panel, 38, row);
            Theme.Row(panel, 12);

            // Compact quality selector + hint row
            var qRow = Theme.Grid(5);
            Theme.FixedColumn(qRow, 64);
            Theme.FixedColumn(qRow, 8);
            Theme.FixedColumn(qRow, 330);
            Theme.FixedColumn(qRow, 12);
            Theme.FillColumn(qRow);

            var probeBtn = Theme.Action("链路测速", Theme.ButtonStyle.Link);
            probeBtn.Click += async delegate { await ProbeLink(); };

            Theme.Row(qRow, 36, Theme.Text("画质档位", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(quality), null, probeBtn);
            Theme.Row(panel, 36, qRow);
            Theme.Row(panel, 6);
            Theme.Row(panel, 20, streamHint);

            UpdateAssistConnectButtons();
            return panel;
        }

        private Control BuildRustDeskModePanel()
        {
            var panel = Theme.Grid(1);
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(22, 14, 22, 8);
            Theme.FillColumn(panel);

            Theme.Row(panel, 20, Theme.Text("伙伴的 9 位设备码", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(panel, 8);

            var row = Theme.Grid(5);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 118);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 116);

            rustDeskConnectBtn = Theme.Action("连接", Theme.ButtonStyle.Primary);
            var transferBtn = Theme.Action("文件传输", Theme.ButtonStyle.Secondary, Theme.Glyph.Folder);
            rustDeskConnectBtn.Click += delegate { StartRemote(false); };
            transferBtn.Click += delegate { StartRemote(true); };
            remoteId.KeyDown += delegate(object sender, KeyEventArgs args) { RunOnEnter(remoteId, rustDeskConnectBtn, args); };
            remoteId.TextChanged += delegate { UpdateAssistConnectButtons(); };

            Theme.Row(row, 38, Theme.WrapInput(remoteId), null, rustDeskConnectBtn, null, transferBtn);
            Theme.Row(panel, 38, row);
            Theme.Row(panel, 12);
            Theme.Row(panel, 24, Theme.Text("通过内置 RustDesk 1.5.3 发起跨网通用桌面协助或直接传输文件，权限由被控端确认。", Theme.TextSize.Small, Theme.Muted));

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
                clientPairCardLayout.RowStyles[assistModeHostRow].Height = DevicePixels(isStream ? 156 : 118);
                if (assistDesk != null && assistStreamCardRow >= 0)
                    assistDesk.RowStyles[assistStreamCardRow].Height = ClientStreamCardHeight();
                LayoutAssistDesk();
            }
            UpdateAssistConnectButtons();
        }

        private void UpdateAssistConnectButtons()
        {
            if (streamConnectBtn != null)
                streamConnectBtn.Enabled = !string.IsNullOrWhiteSpace(streamHost.Text);
            if (rustDeskConnectBtn != null)
                rustDeskConnectBtn.Enabled = !string.IsNullOrWhiteSpace(remoteId.Text);
        }

        // ==================== LOCAL PAIRING FLOW (PRESERVED 100%) ====================

        private async Task BeginLocalPairingAsync()
        {
            if (localPairPreparing || localPairPollBusy) return;
            if (localPairPin != null)
            {
                CancelLocalPairing();
                return;
            }

            localPairPreparing = true;
            int prepareGeneration = ++localPairGeneration;
            localPairGenerateButton.Enabled = false;
            localPairCancelButton.Enabled = true;
            localPairGenerateButton.Text = "准备中…";
            localPairGenerateButton.AccessibleName = "准备本机被控服务";
            localPairStatusLabel.Text = "正在准备本机被控服务…";
            SetStatus("正在准备本机被控服务，首次启动可能需要数秒。", Theme.PillTone.Info);

            LocalPairPrepareResult result;
            try
            {
                result = await Task.Run(delegate
                {
                    var prepared = new LocalPairPrepareResult();
                    string prepareMessage = null;
                    bool started = BundledHost.TryPrepareAndStart(out prepareMessage);
                    string availabilityReason = null;
                    bool available = SunshinePairing.TryGetAvailability(out availabilityReason);
                    if (!available)
                    {
                        prepared.Message = JoinMessages(prepareMessage, availabilityReason);
                        return prepared;
                    }

                    List<PairedClientInfo> paired = null;
                    string pairedError = null;
                    if (!SunshinePairing.TryGetPairedClients(out paired, out pairedError))
                    {
                        prepared.Message = JoinMessages("无法读取本机已有授权记录。", pairedError);
                        return prepared;
                    }
                    List<PendingPairRequest> pending = null;
                    string pendingError = null;
                    if (!SunshinePairing.TryGetPending(out pending, out pendingError))
                    {
                        prepared.Message = JoinMessages("无法读取本机配对请求。", pendingError);
                        return prepared;
                    }

                    var session = new LocalPairSession();
                    session.Pin = SunshinePairing.GeneratePin();
                    session.Clock = Stopwatch.StartNew();
                    session.BaselinePaired = BuildPairedIdSet(paired);
                    session.BaselinePending = BuildPendingIdSet(pending);
                    prepared.Session = session;
                    prepared.Message = string.IsNullOrEmpty(prepareMessage)
                        ? (started ? "本机被控服务已准备就绪。" : "复用已有宿主，本机被控服务已就绪。")
                        : prepareMessage;
                    return prepared;
                });
            }
            catch (Exception ex)
            {
                result = new LocalPairPrepareResult();
                result.Message = "准备本机被控服务失败：" + ex.Message;
            }

            localPairPreparing = false;
            if (IsDisposed) return;
            if (prepareGeneration != localPairGeneration)
            {
                localPairGenerateButton.Enabled = true;
                return;
            }
            if (result.Session == null)
            {
                localPairGenerateButton.Enabled = true;
                localPairGenerateButton.Text = "生成配对码";
                localPairStatusLabel.Text = result.Message ?? "本机被控服务暂不可用。";
                SetStatus(localPairStatusLabel.Text, Theme.PillTone.Warning);
                return;
            }

            LocalPairSession current = result.Session;
            current.Generation = ++localPairGeneration;
            localPairSession = current;
            localPairPin = current.Pin;
            RefreshLocalPairPinDisplay();
            localPairCancelButton.Enabled = true;
            localPairGenerateButton.Enabled = false;
            localPairStatusLabel.Text = "等待另一台电脑输入配对码…";
            localPairGenerateButton.Text = "等待配对…";
            localPairGenerateButton.AccessibleName = "等待另一台电脑输入配对码";
            localPairTimer.Start();
            SetStatus(result.Message + " 4 位配对码有效期 120 秒；对方输入后请点「确认允许」。", Theme.PillTone.Info);
            await RefreshHostDiagnosticsAsync();
        }

        private async Task LocalPairPrimaryActionAsync()
        {
            if (localPairPreparing) return;
            if (localPairPin == null)
            {
                await BeginLocalPairingAsync();
                return;
            }
            LocalPairSession session = localPairSession;
            if (session == null || session.PendingRequest == null) return;
            await ConfirmLocalPendingAsync(session);
        }

        private async Task ConfirmLocalPendingAsync(LocalPairSession session)
        {
            if (session == null || !EnsureLocalPairCurrent(session)) return;
            if (session.SubmitStarted) return;
            PendingPairRequest request = session.PendingRequest;
            if (request == null || string.IsNullOrEmpty(request.Id))
            {
                localPairStatusLabel.Text = "待确认请求已变化，请等待列表刷新后重试。";
                return;
            }

            session.SubmitStarted = true;
            session.SubmitCompleted = false;
            session.SubmitSucceeded = false;
            localPairGenerateButton.Enabled = false;
            localPairGenerateButton.Text = "正在确认…";
            localPairStatusLabel.Text = "正在提交本机授权确认…";
            string pin = session.Pin;
            string requestId = request.Id;
            string deviceName = string.IsNullOrEmpty(request.DeviceName) ? "另一台电脑" : request.DeviceName;

            string error = null;
            bool confirmed = false;
            try
            {
                confirmed = await Task.Run(delegate
                {
                    if (session.Cancelled
                        || session.Clock == null
                        || session.Clock.Elapsed >= TimeSpan.FromSeconds(LocalPairLifetimeSeconds))
                        return false;

                    List<PendingPairRequest> stillPending = null;
                    string pendingError = null;
                    if (!SunshinePairing.TryGetPending(out stillPending, out pendingError)) return false;
                    bool found = false;
                    if (stillPending != null)
                    {
                        foreach (PendingPairRequest candidate in stillPending)
                        {
                            if (string.Equals(candidate.Id, requestId, StringComparison.Ordinal))
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                    if (!found) return false;
                    if (session.Cancelled
                        || session.Clock == null
                        || session.Clock.Elapsed >= TimeSpan.FromSeconds(LocalPairLifetimeSeconds))
                        return false;
                    return SunshinePairing.TryConfirm(pin, requestId, deviceName, out error);
                });
            }
            catch (Exception ex)
            {
                error = "提交本机授权确认失败：" + ex.Message;
            }

            session.SubmitCompleted = true;
            session.SubmitSucceeded = confirmed;
            if (!IsCurrentLocalPairing(session)) return;
            if (confirmed)
            {
                session.ConfirmationSubmitted = true;
                localPairGenerateButton.Text = "已提交确认";
                localPairGenerateButton.Enabled = false;
                localPairStatusLabel.Text = "已提交确认，正在等待本机授权记录生效…";
                SetStatus("已提交本机授权确认，正在等待授权列表更新。", Theme.PillTone.Info);
            }
            else
            {
                session.SubmitStarted = false;
                localPairGenerateButton.Text = "确认允许";
                localPairGenerateButton.Enabled = true;
                localPairStatusLabel.Text = string.IsNullOrEmpty(error)
                    ? "请求已失效或配对码已过期，本次未提交授权。" : error;
                SetStatus(string.IsNullOrEmpty(error)
                    ? "待确认请求已失效，本次未提交授权。" : error, Theme.PillTone.Warning);
            }
        }

        private void CancelLocalPairing()
        {
            CancelLocalPairing("已取消等待，配对码已失效。");
        }

        private void CancelLocalPairing(string status)
        {
            LocalPairSession cancelled = localPairSession;
            if (cancelled != null) cancelled.Cancelled = true;
            if (cancelled != null && cancelled.PendingRequestCount > 1)
                status = "已取消本机配对。已显示 " + cancelled.PendingRequestCount + " 个待确认请求，均未提交授权。";
            else if (cancelled != null && cancelled.SubmitStarted)
                status = "已取消本机配对。确认已发出，最终是否授权以本机授权列表为准。";
            localPairTimer.Stop();
            localPairSession = null;
            localPairPin = null;
            localPairGeneration++;
            RefreshLocalPairPinDisplay();
            if (localPairStatusLabel != null) localPairStatusLabel.Text = status;
            if (localPairGenerateButton != null)
            {
                localPairGenerateButton.Text = "生成配对码";
                localPairGenerateButton.AccessibleName = "生成配对码";
                localPairGenerateButton.Enabled = !localPairPollBusy;
            }
            if (localPairCancelButton != null) localPairCancelButton.Enabled = false;
        }

        private async Task PollLocalPairingAsync()
        {
            LocalPairSession session = localPairSession;
            if (localPairPollBusy || session == null || IsDisposed) return;
            if (session.Clock == null || session.Clock.Elapsed >= TimeSpan.FromSeconds(LocalPairLifetimeSeconds))
            {
                session.Expired = true;
                CancelLocalPairing(session.SubmitStarted
                    ? "配对码已过期；确认已发出，最终是否授权以本机授权列表为准。"
                    : "配对码已过期，请重新生成。");
                SetStatus(session.SubmitStarted
                    ? "本次配对码已过期；确认已发出，最终是否授权以本机授权列表为准。"
                    : "本次配对码已过期，没有提交任何授权确认。", Theme.PillTone.Warning);
                return;
            }

            localPairPollBusy = true;
            try
            {
                if (!session.SubmitStarted)
                {
                    List<PendingPairRequest> requests = null;
                    string error = null;
                    bool loaded = await Task.Run(() => SunshinePairing.TryGetPending(out requests, out error));
                    if (!EnsureLocalPairCurrent(session)) return;
                    if (!loaded)
                    {
                        localPairStatusLabel.Text = error ?? "暂时无法读取待确认请求，正在重试…";
                        return;
                    }

                    PendingPairRequest request = FindNewPendingRequest(requests, session.BaselinePending);
                    if (request == null)
                    {
                        session.PendingRequestCount = 0;
                        session.PendingRequest = null;
                        ShowLocalPairCountdown(session);
                        return;
                    }
                    string name = string.IsNullOrEmpty(request.DeviceName) ? "另一台电脑" : request.DeviceName;
                    if (session.PendingRequest == null || !string.Equals(session.PendingRequest.Id, request.Id, StringComparison.Ordinal))
                    {
                        int count = CountNewPendingRequests(requests, session.BaselinePending);
                        session.PendingRequestCount = count;
                        session.PendingRequest = request;
                        localPairStatusLabel.Text = count > 1
                            ? name + " 请求连接（共 " + count + " 个待确认）。点「确认允许」仅通过此设备。"
                            : name + " 请求连接本机，请点右上角「确认允许」。";
                        localPairGenerateButton.Text = "确认允许";
                        localPairGenerateButton.AccessibleName = "确认允许 " + name + " 连接";
                        localPairGenerateButton.Enabled = true;
                    }
                    else
                    {
                        localPairGenerateButton.Text = "确认允许";
                        localPairGenerateButton.Enabled = true;
                    }
                    return;
                }

                if (!session.SubmitCompleted)
                {
                    ShowLocalPairCountdown(session);
                    return;
                }

                List<PairedClientInfo> clients = null;
                string clientsError = null;
                bool loadedClients = await Task.Run(() => SunshinePairing.TryGetPairedClients(out clients, out clientsError));
                if (!EnsureLocalPairCurrent(session)) return;
                if (!loadedClients)
                {
                    localPairStatusLabel.Text = clientsError ?? "已提交确认，暂时无法读取授权记录，正在重试…";
                    return;
                }
                PairedClientInfo added = FindNewPairedClient(clients, session.BaselinePaired);
                if (added != null)
                {
                    string name = string.IsNullOrEmpty(added.Name) ? "另一台电脑" : added.Name;
                    CancelLocalPairing("已允许 " + name + " 连接这台电脑。");
                    SetStatus("完成本机授权，之后对方可直接一键连接本机。", Theme.PillTone.Success);
                    return;
                }
                localPairStatusLabel.Text = "已提交确认，等待授权生效… 还剩 " + LocalPairSecondsLeft(session) + " 秒";
            }
            finally
            {
                localPairPollBusy = false;
                if (!IsDisposed && localPairSession == null && localPairGenerateButton != null)
                    localPairGenerateButton.Enabled = true;
            }
        }

        private bool IsCurrentLocalPairing(LocalPairSession session)
        {
            return !IsDisposed && session != null && localPairSession == session
                && session.Generation == localPairGeneration
                && string.Equals(localPairPin, session.Pin, StringComparison.Ordinal);
        }

        private bool EnsureLocalPairCurrent(LocalPairSession session)
        {
            if (!IsCurrentLocalPairing(session)) return false;
            if (session.Clock != null && session.Clock.Elapsed >= TimeSpan.FromSeconds(LocalPairLifetimeSeconds))
            {
                CancelLocalPairing("配对码已过期，请重新生成。");
                SetStatus("本次配对码已过期，没有授权任何设备。", Theme.PillTone.Warning);
                return false;
            }
            return true;
        }

        private void ShowLocalPairCountdown(LocalPairSession session)
        {
            if (!IsCurrentLocalPairing(session)) return;
            localPairStatusLabel.Text = "正在等待另一台电脑输入配对码… 还剩 " + LocalPairSecondsLeft(session) + " 秒";
        }

        private static int LocalPairSecondsLeft(LocalPairSession session)
        {
            if (session == null || session.Clock == null) return 0;
            return Math.Max(0, LocalPairLifetimeSeconds - (int)Math.Ceiling(session.Clock.Elapsed.TotalSeconds));
        }

        private static HashSet<string> BuildPendingIdSet(List<PendingPairRequest> requests)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (requests == null) return set;
            foreach (PendingPairRequest request in requests)
                if (!string.IsNullOrEmpty(request.Id)) set.Add(request.Id);
            return set;
        }

        private static HashSet<string> BuildPairedIdSet(List<PairedClientInfo> clients)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (clients == null) return set;
            foreach (PairedClientInfo client in clients)
                if (!string.IsNullOrEmpty(client.Id)) set.Add(client.Id.Trim());
            return set;
        }

        private static PendingPairRequest FindNewPendingRequest(List<PendingPairRequest> requests, HashSet<string> baseline)
        {
            if (requests == null) return null;
            foreach (PendingPairRequest request in requests)
            {
                if (string.IsNullOrEmpty(request.Id)) continue;
                if (baseline != null && baseline.Contains(request.Id)) continue;
                return request;
            }
            return null;
        }

        private static int CountNewPendingRequests(List<PendingPairRequest> requests, HashSet<string> baseline)
        {
            if (requests == null) return 0;
            int count = 0;
            foreach (PendingPairRequest request in requests)
            {
                if (string.IsNullOrEmpty(request.Id)) continue;
                if (baseline != null && baseline.Contains(request.Id)) continue;
                count++;
            }
            return count;
        }

        private static PairedClientInfo FindNewPairedClient(List<PairedClientInfo> clients, HashSet<string> baseline)
        {
            if (clients == null) return null;
            foreach (PairedClientInfo client in clients)
            {
                if (string.IsNullOrEmpty(client.Id)) continue;
                if (baseline != null && baseline.Contains(client.Id.Trim())) continue;
                return client;
            }
            return null;
        }

        private static string JoinMessages(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second ?? "";
            if (string.IsNullOrEmpty(second)) return first;
            return first + " " + second;
        }

        private async Task OpenLocalRustDeskOnAssist(Button btn)
        {
            if (btn != null) btn.Enabled = false;
            SetStatus("正在启动本机 RustDesk…", Theme.PillTone.Info);
            try
            {
                string error = await Task.Run(() =>
                {
                    string detail;
                    return Engines.LaunchRustDesk(out detail) ? null : detail;
                });
                if (!IsDisposed) SetStatus(error ?? "已打开本机 RustDesk 窗口，可查看 9 位设备码并确认连接请求。", error == null ? Theme.PillTone.Success : Theme.PillTone.Alert);
            }
            finally { if (btn != null && !IsDisposed) btn.Enabled = true; }
        }

        private void OpenMoonlightGui()
        {
            switch (FoundationClient.SelectEngine(true))
            {
                case StreamEngine.Enhanced:
                    string message;
                    if (!FoundationClient.ApplyAbrSetting(useFoundationClient, out message))
                    {
                        SetStatus(message, Theme.PillTone.Alert);
                        return;
                    }
                    Launch(FoundationClient.Open, "已打开 Moonlight V+，可在主机列表中管理配对或调整参数。");
                    break;
                case StreamEngine.Official:
                    Launch(Engines.LaunchMoonlight, "已打开原版 Moonlight，可在其中管理已配对主机。");
                    break;
                default:
                    SetStatus(MissingStreamEngineMessage, Theme.PillTone.Alert);
                    break;
            }
        }

        private delegate bool OpenApplication(out string error);

        private void Launch(OpenApplication open, string instruction)
        {
            string error;
            bool started = open(out error);
            SetStatus(started ? instruction : error, started ? Theme.PillTone.Success : Theme.PillTone.Alert);
        }

        private void StartRemote(bool transfer)
        {
            if (string.IsNullOrWhiteSpace(remoteId.Text))
            {
                SetStatus("请先输入对方的 9 位远程设备码。", Theme.PillTone.Warning);
                remoteId.Focus();
                return;
            }
            string message;
            string id = Engines.NormalizeRustDeskId(remoteId.Text, out message);
            if (id == null)
            {
                SetStatus(message, Theme.PillTone.Alert);
                remoteId.Focus();
                return;
            }
            string pending = null;
            Action<string> onStatus = delegate(string update)
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(new Action(delegate
                {
                    if (assistBanner.Message == pending) SetStatus(update, Theme.PillTone.Success);
                }));
            };
            bool launched = transfer ? Engines.TransferRustDesk(id, out message, onStatus)
                : Engines.ConnectRustDesk(id, out message, onStatus);
            if (!launched)
            {
                SetStatus(message, Theme.PillTone.Alert);
                return;
            }
            pending = message;
            recent.RememberRustDeskId(id);
            Remember(remoteId, recent.RustDeskIds, message);
        }

        private void StartStream(string confirmedHost)
        {
            string message;
            string host = Engines.NormalizeHost(confirmedHost, out message);
            if (host == null)
            {
                SetStatus(message, Theme.PillTone.Warning);
                streamHost.Focus();
                return;
            }
            var profile = quality.SelectedItem as QualityOption ?? QualityOptions[0];
            string note;
            bool started;
            switch (FoundationClient.SelectEngine(true))
            {
                case StreamEngine.Enhanced:
                    started = FoundationClient.Launch(host, profile.Width, profile.Height, profile.Fps, profile.BitrateKbps,
                        showStreamStats, playAudioOnHost, windowedStream, UsesHevc(profile), useFoundationClient,
                        lowLatencyNoVsync, enableFramePacing, false, out note);
                    break;
                case StreamEngine.Official:
                    started = Engines.StreamMoonlight(host, profile.Width, profile.Height, profile.Fps, profile.BitrateKbps,
                        showStreamStats, playAudioOnHost, windowedStream, UsesHevc(profile),
                        lowLatencyNoVsync, enableFramePacing, out note);
                    break;
                default:
                    SetStatus(MissingStreamEngineMessage, Theme.PillTone.Alert);
                    ConnectionTelemetry.RecordFailed("unavailable", host, profile.Width, profile.Height, profile.Fps,
                        profile.BitrateKbps, UsesHevc(profile), useFoundationClient, MissingStreamEngineMessage);
                    return;
            }
            if (!started)
            {
                SetStatus(note, Theme.PillTone.Alert);
                ConnectionTelemetry.RecordFailed(FoundationClient.SelectEngine(true).ToString(), host, profile.Width,
                    profile.Height, profile.Fps, profile.BitrateKbps, UsesHevc(profile), useFoundationClient, note);
                return;
            }
            ConnectionTelemetry.RecordSubmitted(FoundationClient.SelectEngine(true).ToString(), host, profile.Width,
                profile.Height, profile.Fps, profile.BitrateKbps, UsesHevc(profile), useFoundationClient, note);
            recent.RememberMoonlightHost(host);
            Remember(streamHost, recent.MoonlightHosts, note, Theme.PillTone.Info);
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
            SetStatus("正在读取 Tailscale 组网设备列表…", Theme.PillTone.Info);
            try
            {
                TailnetLoadResult result = await Task.Run(() => Tailscale.LoadPeers(5200));
                if (IsDisposed) return;
                if (!result.Ok)
                {
                    SetStatus(result.Error + " 也可以直接填写局域网 IP 或公网 IPv6。", Theme.PillTone.Warning);
                    return;
                }
                string kept = SelectedHost();
                streamHost.BeginUpdate();
                try
                {
                    streamHost.Items.Clear();
                    RestoreRecents(streamHost, recent.MoonlightHosts);
                    foreach (var peer in result.Peers)
                    {
                        if (!string.IsNullOrWhiteSpace(peer.Ip)) streamHost.Items.Add(new PeerOption(peer));
                    }
                    streamHost.SelectedIndex = -1;
                    streamHost.Text = kept;
                }
                finally { streamHost.EndUpdate(); }
                if (result.Peers.Count == 0)
                {
                    SetStatus("当前 Tailscale 网络里暂无其他 Windows 设备。", Theme.PillTone.Info);
                    return;
                }
                SetStatus("已刷新设备列表，请选择主机连接。", Theme.PillTone.Success);
                streamHost.Focus();
                streamHost.DroppedDown = true;
            }
            catch
            {
                if (!IsDisposed) SetStatus("暂时读不到 Tailscale 设备，可直接手动输入 IP。", Theme.PillTone.Warning);
            }
            finally
            {
                if (!IsDisposed) discover.Enabled = true;
            }
        }

        private async Task ProbeLink()
        {
            string error;
            string target = Engines.NormalizeHost(SelectedHost(), out error);
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
                "通用协助：在「开始协助」使用 RustDesk 设备码连接或互传文件。\n低延迟高清串流：输入主机地址直接连接，首次将引导 4 位配对码授权。\n防窥保护：在「防窥与显示」开启防窥壁纸，快捷键 Ctrl+Alt+Shift+P 随时开关。",
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
            switch (FoundationClient.SelectEngine(true))
            {
                case StreamEngine.Enhanced:
                    return useFoundationClient ? "Moonlight V+（已请求 ABR 自适应抗丢包）" : "Moonlight V+（固定码率）";
                case StreamEngine.Official:
                    return "Moonlight 6.2.0（固定码率）";
                default:
                    return "未找到串流组件，请完整解压便携版";
            }
        }

        private void UpdateStreamHint()
        {
            var selected = quality.SelectedItem as QualityOption;
            if (selected == null) return;
            var engine = FoundationClient.SelectEngine(true);
            if (engine == StreamEngine.Unavailable)
            {
                streamModeBadge.UpdateStatus("缺少串流组件", Theme.PillTone.Alert);
                streamHint.Text = MissingStreamEngineMessage;
                return;
            }
            string codec = UsesHevc(selected) ? "HEVC" : "H.264";
            string badgeText = codec;
            if (engine == StreamEngine.Enhanced && useFoundationClient) badgeText += " · ABR";
            if (lowLatencyNoVsync) badgeText += " · 低延迟";
            streamModeBadge.UpdateStatus(badgeText, engine == StreamEngine.Enhanced ? Theme.PillTone.Info : Theme.PillTone.Neutral);

            string abrNote = engine == StreamEngine.Enhanced && useFoundationClient
                ? "已启用 Foundation ABR 自适应抗丢包。"
                : engine == StreamEngine.Enhanced ? "ABR 未开启（固定码率）。" : "官方客户端固定码率模式。";
            if (selected.Width >= 3840)
                streamHint.Text = "4K 超清模式，建议 HEVC 硬解 + 高带宽直连。" + abrNote;
            else if (selected.Width * 10 == selected.Height * 16)
                streamHint.Text = "16:10 满屏比例，建议被控端启用虚拟屏。" + abrNote;
            else if (selected.Fps >= 120)
                streamHint.Text = "电竞高刷档位（" + selected.Fps + "fps），建议局域网或高上行直连。" + abrNote;
            else if (selected.Fps >= 90)
                streamHint.Text = "高刷档位，建议同局域网或低延迟直连。" + abrNote;
            else
                streamHint.Text = "跨网抗丢包友好档位。" + abrNote;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (IsDisposed) return;
            Action apply = delegate
            {
                if (IsDisposed) return;
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    privacyGuard.OnSessionLock();
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    privacyGuard.OnSessionUnlock(DateTime.UtcNow);
                    // 若在解锁瞬间发现有残留防窥层且用户已主动解除，强制确保其关闭
                    if (privacyGuard.ManualDismissSuppressed && privacyCurtain.IsActive && !privacyCurtain.IsPreview)
                    {
                        privacyCurtain.Deactivate(false);
                    }
                    UpdatePrivacyIndicators();
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

        private void SyncRemoteGuardTimer()
        {
            if (privacyConfig.AutoOnRemote)
            {
                if (!remoteGuardTimer.Enabled) remoteGuardTimer.Start();
            }
            else
            {
                remoteGuardTimer.Stop();
                bool wasAuto = privacyGuard.AutoActivated;
                privacyGuard.OnAutoGuardDisabled();
                if (wasAuto && privacyCurtain.IsActive && !privacyCurtain.IsPreview)
                {
                    privacyCurtain.Deactivate(false);
                }
            }
            UpdatePrivacyIndicators();
        }

        private async Task PollRemoteGuardTick()
        {
            if (guardPollBusy || !privacyConfig.AutoOnRemote || privacyGuard.SessionLocked || IsDisposed) return;
            guardPollBusy = true;
            long pollGeneration = privacyGuard.Generation;
            try
            {
                bool activeSession = await Task.Run(() => HostNetworkInfo.IsHostBeingControlled());
                if (IsDisposed || !privacyConfig.AutoOnRemote) return;

                PrivacyGuardAction action = privacyGuard.EvaluateAutoGuardPoll(
                    pollGeneration, activeSession, privacyCurtain.IsActive, privacyCurtain.IsPreview,
                    privacyConfig.AutoOnRemote, DateTime.UtcNow);

                if (action == PrivacyGuardAction.ActivateAuto)
                {
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
                    privacyCurtain.Deactivate(privacyConfig.LockOnLocalExit);
                    ShowDisplayBanner("远控会话已结束，已自动收起防窥壁纸。", Theme.PillTone.Info);
                }
            }
            catch { }
            finally { guardPollBusy = false; }
        }

        internal void TogglePrivacyCurtain(bool fromLocalHotkey)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (privacyCurtain.IsActive)
            {
                bool triggerLock = fromLocalHotkey && !privacyCurtain.IsPreview;
                privacyGuard.OnManualExit(nowUtc);
                privacyCurtain.Deactivate(triggerLock);
                ShowDisplayBanner(triggerLock && privacyConfig.LockOnLocalExit
                    ? "已退出防窥屏并触发锁屏，解锁返回桌面后不会自动再次遮挡。"
                    : "已退出防窥屏壁纸。", Theme.PillTone.Info);
                return;
            }
            if (fromLocalHotkey && !privacyGuard.CanActivateFromHotkey(nowUtc))
            {
                return;
            }
            privacyGuard.OnManualActivate();
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
                float scale = owner.DeviceDpi / 96f;
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

        private sealed class LocalPairSession
        {
            internal int Generation;
            internal string Pin;
            internal Stopwatch Clock;
            internal HashSet<string> BaselinePending;
            internal HashSet<string> BaselinePaired;
            internal bool ConfirmationSubmitted;
            internal PendingPairRequest PendingRequest;
            internal int PendingRequestCount;
            internal bool SubmitStarted;
            internal bool SubmitCompleted;
            internal bool SubmitSucceeded;
            internal bool Expired;
            internal bool Cancelled;
        }

        private sealed class LocalPairPrepareResult
        {
            internal LocalPairSession Session;
            internal string Message;
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
