using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm : Form
    {
        private readonly ComboBox remoteId = Theme.Choice("远程设备码", true);
        private readonly ComboBox streamHost = Theme.Choice("串流主机地址", true);
        private readonly ComboBox quality = Theme.Choice("串流画质", false);
        private readonly Button discover = Theme.Action("查找设备", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
        private readonly Button probe = Theme.Action("测试链路", Theme.ButtonStyle.Secondary, Theme.Glyph.Bolt);
        private readonly Theme.StatusBanner assistBanner = new Theme.StatusBanner("远程协助连接发起后，结果会显示在这里。");
        private readonly Theme.Badge streamModeBadge = new Theme.Badge("检测串流组件", Theme.PillTone.Neutral);
        private readonly Label streamHint = Theme.Text("", Theme.TextSize.Small, Theme.Muted);

        private const int WanWideProfile = 0;
        private const int WanTallProfile = 1;
        private const int WeakNetProfile = 3;
        private const int Lan2K90WideProfile = 4;
        private const int Lan2K90TallProfile = 5;
        private const int HotkeyPrivacyId = 0x51A0;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint VkP = 0x50;
        private const int WmHotkey = 0x0312;

        private const string MissingStreamEngineMessage = "当前目录缺少串流组件。请完整解压便携版后再试。";

        private const int AssistCardHeight = 430;
        private const int AssistCardGap = 14;
        private const int AssistPageHeight = 640;
        private const int AssistLaneMinWidth = 780;

        private static readonly QualityOption[] QualityOptions =
        {
            new QualityOption("外网流畅自适应 · 1080p · 60 帧 · 10 Mbps（推荐）", 1920, 1080, 60, 10000),
            new QualityOption("外网 16:10 · 1920×1200 · 60 帧 · 10 Mbps", 1920, 1200, 60, 10000),
            new QualityOption("外网高清 · 1440p · 60 帧 · 14 Mbps", 2560, 1440, 60, 14000),
            new QualityOption("弱网抗丢包 · 720p · 60 帧 · 6 Mbps", 1280, 720, 60, 6000),
            new QualityOption("高速 / 局域网 · 2560×1440 · 90 帧 · 22 Mbps", 2560, 1440, 90, 22000),
            new QualityOption("高速 / 局域网 16:10 · 2560×1600 · 90 帧 · 24 Mbps", 2560, 1600, 90, 24000)
        };

        private readonly RecentStore recent;
        private readonly PrivacyWallpaperConfig privacyConfig;
        private readonly PrivacyCurtainManager privacyCurtain = new PrivacyCurtainManager();
        private readonly Timer remoteGuardTimer = new Timer();
        private HostSnapshot hostSnapshot = new HostSnapshot();
        private NotifyIcon trayIcon;
        private Icon appIcon;
        private bool forceExit;
        private bool guardPollBusy;
        private bool autoActivatedCurtain;
        private bool hotkeyRegistered;
        private TableLayoutPanel assistDesk;
        private TableLayoutPanel assistLanes;
        private Control assistRemoteCard;
        private Control assistStreamCard;
        private bool assistStacked;
        private bool assistRelayout;
        private bool chromeReady;

        internal MainForm()
        {
            SuspendLayout();
            Text = "双端远程";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(1120, 740);
            MinimumSize = new Size(860, 540);
            Padding = new Padding(1);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.StrokeStrong;
            DoubleBuffered = true;

            try
            {
                appIcon = Theme.CreateAppIcon();
                Icon = appIcon;
            }
            catch { }

            recent = RecentStore.Load();
            privacyConfig = PrivacyWallpaperConfig.Load();
            remoteId.MaxLength = 64;
            streamHost.MaxLength = 253;
            Theme.Cue(remoteId, "输入对方的 9 位 RustDesk 设备码");
            Theme.Cue(streamHost, "输入局域网 IP、Tailscale IP 或公网 IPv6");
            streamHint.AutoEllipsis = false;
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
            remoteGuardTimer.Interval = 2500;
            remoteGuardTimer.Tick += async delegate { await PollRemoteGuardTick(); };

            Controls.Add(BuildChrome());
            SetupTrayIcon();
            AutoScaleDimensions = new SizeF(96f, 96f);
            ResumeLayout(false);
            PerformAutoScale();
            PerformLayout();
            chromeReady = true;
            if (assistLanes != null) assistLanes.Resize += delegate { ApplyAssistLaneLayout(); };
            ApplyAssistLaneLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyPrivacyId, ModControl | ModAlt | ModShift, VkP);
            }
            catch { hotkeyRegistered = false; }
        }

        protected override void OnLoad(EventArgs args)
        {
            base.OnLoad(args);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(Width, area.Width - 20);
            int height = Math.Min(Height, area.Height - 20);
            if (width != Width || height != Height)
                SetBounds(area.Left + 10, area.Top + 10, width, height);
            SyncRemoteGuardTimer();
        }

        private void SetupTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主界面", null, delegate { RestoreFromTray(); });
            menu.Items.Add("切换防窥屏壁纸 (Ctrl+Alt+Shift+P)", null, delegate { TogglePrivacyCurtain(false); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("彻底退出双端远程", null, delegate { forceExit = true; Close(); });

            trayIcon = new NotifyIcon
            {
                Text = "双端远程 · 低延迟远控与防窥守护",
                Icon = appIcon ?? SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
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
            if (!forceExit && e.CloseReason == CloseReason.UserClosing && (minimizeToTray || privacyConfig.AutoOnRemote || privacyCurtain.IsActive))
            {
                e.Cancel = true;
                Hide();
                if (trayIcon != null)
                {
                    trayIcon.ShowBalloonTip(2000, "双端远程仍在后台运行",
                        privacyConfig.AutoOnRemote ? "防窥屏壁纸守护运行中，双击托盘图标可打开主界面。" : "双击右下角托盘图标可随时恢复窗口。",
                        ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs args)
        {
            remoteGuardTimer.Stop();
            remoteGuardTimer.Dispose();
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

        private Control BuildWorkspace()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(AssistPageHeight, out desk);
            AddPageHeader(desk, "远程协助", "通用控制走 RustDesk，高帧低延迟与自适应抗丢包走 Moonlight V+");
            Theme.Row(desk, 16);

            var lanes = Theme.Grid(3);
            Theme.FillColumn(lanes);
            Theme.FixedColumn(lanes, 18);
            Theme.FillColumn(lanes);
            var remoteCard = BuildRemoteCard();
            var streamCard = BuildStreamCard();
            Theme.Row(lanes, AssistCardHeight, remoteCard, null, streamCard);
            Theme.Row(desk, AssistCardHeight, lanes);
            Theme.Row(desk, 14);
            Theme.Row(desk, 46, assistBanner);
            assistDesk = desk;
            assistLanes = lanes;
            assistRemoteCard = remoteCard;
            assistStreamCard = streamCard;
            return viewport;
        }

        private void ApplyAssistLaneLayout()
        {
            if (!chromeReady || assistLanes == null || assistDesk == null || assistRelayout) return;
            int laneWidth = assistLanes.Width;
            if (laneWidth <= 0) return;
            float scale = CurrentAutoScaleDimensions.Width / 96f;
            if (scale <= 0f) scale = 1f;
            bool stack = laneWidth < (int)(AssistLaneMinWidth * scale);
            if (stack == assistStacked && assistLanes.RowCount == (stack ? 3 : 1)) return;
            int cardHeight = (int)Math.Round(AssistCardHeight * scale);
            int gap = (int)Math.Round(AssistCardGap * scale);
            int pageHeight = (int)Math.Round(AssistPageHeight * scale);
            int laneGap = (int)Math.Round(18 * scale);
            assistRelayout = true;
            try
            {
                assistLanes.SuspendLayout();
                assistDesk.SuspendLayout();
                assistStacked = stack;
                if (stack)
                {
                    assistLanes.ColumnCount = 1;
                    assistLanes.RowCount = 3;
                    assistLanes.ColumnStyles.Clear();
                    assistLanes.RowStyles.Clear();
                    assistLanes.RowCount = 3;
                    assistLanes.RowStyles.Add(new RowStyle(SizeType.Absolute, cardHeight));
                    assistLanes.RowStyles.Add(new RowStyle(SizeType.Absolute, gap));
                    assistLanes.RowStyles.Add(new RowStyle(SizeType.Absolute, cardHeight));
                    assistLanes.SetCellPosition(assistRemoteCard, new TableLayoutPanelCellPosition(0, 0));
                    assistLanes.SetCellPosition(assistStreamCard, new TableLayoutPanelCellPosition(0, 2));
                    Theme.FillColumn(assistLanes);
                    assistDesk.RowStyles[2].Height = cardHeight * 2 + gap;
                    assistDesk.Height = pageHeight + cardHeight + gap;
                }
                else
                {
                    assistLanes.ColumnCount = 3;
                    assistLanes.RowCount = 1;
                    assistLanes.ColumnStyles.Clear();
                    assistLanes.RowStyles.Clear();
                    Theme.FillColumn(assistLanes);
                    Theme.FixedColumn(assistLanes, laneGap);
                    Theme.FillColumn(assistLanes);
                    assistLanes.RowStyles.Add(new RowStyle(SizeType.Absolute, cardHeight));
                    assistLanes.SetCellPosition(assistRemoteCard, new TableLayoutPanelCellPosition(0, 0));
                    assistLanes.SetCellPosition(assistStreamCard, new TableLayoutPanelCellPosition(2, 0));
                    assistDesk.RowStyles[2].Height = cardHeight;
                    assistDesk.Height = pageHeight;
                }
                assistLanes.ResumeLayout(true);
                assistDesk.ResumeLayout(true);
                assistLanes.PerformLayout();
                assistDesk.PerformLayout();
            }
            finally
            {
                assistRelayout = false;
            }
        }

        private static Control CardHeader(Theme.Glyph glyph, string title, string subtitle, Color tileBg, Color tileFg)
        {
            var heading = Theme.Grid(3);
            Theme.FixedColumn(heading, 42);
            Theme.FixedColumn(heading, 12);
            Theme.FillColumn(heading);
            var text = Theme.Grid(1);
            Theme.FillColumn(text);
            Theme.Row(text, 24, Theme.Text(title, Theme.TextSize.Section, Theme.Ink));
            Theme.Row(text, 20, Theme.Text(subtitle, Theme.TextSize.Small, Theme.Muted));
            Theme.Row(heading, 44, new Theme.IconTile(glyph, false, tileBg, tileFg), null, text);
            return heading;
        }

        private Control BuildRemoteCard()
        {
            var card = new Theme.Card();
            var layout = Theme.Grid(1);
            Theme.FillColumn(layout);
            Theme.Row(layout, 44, CardHeader(Theme.Glyph.Link, "通用远控与文件", "基于 RustDesk 1.5.3，适合临时协助与大文件互传", Theme.AccentLight, Theme.Accent));
            Theme.Row(layout, 18);
            Theme.Row(layout, 22, Theme.Text("远程设备码", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(layout, 6);
            Theme.Row(layout, 38, Theme.WrapInput(remoteId));
            Theme.Row(layout, 14);

            var actions = Theme.Grid(3);
            Theme.FillColumn(actions);
            Theme.FixedColumn(actions, 10);
            Theme.FillColumn(actions);
            var connect = Theme.Action("发起远控", Theme.ButtonStyle.Primary, Theme.Glyph.Arrow);
            var transfer = Theme.Action("文件传输", Theme.ButtonStyle.Secondary, Theme.Glyph.Folder);
            connect.Click += delegate { StartRemote(false); };
            transfer.Click += delegate { StartRemote(true); };
            remoteId.KeyDown += delegate(object sender, KeyEventArgs args) { RunOnEnter(remoteId, connect, args); };
            Theme.Row(actions, 40, connect, null, transfer);
            Theme.Row(layout, 40, actions);

            Theme.Row(layout, 22);
            Theme.Row(layout, 1, Theme.Rule());
            Theme.Row(layout, 14);
            Theme.Row(layout, 22, Theme.Text("安全授权与本机入口", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(layout, 40, Theme.Text("首次远控或文件传输均需被控端在 RustDesk 窗口中手动同意，不会绕过系统权限。", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(layout, 10);
            var openLocalRow = Theme.Grid(2);
            Theme.FixedColumn(openLocalRow, 152);
            Theme.FillColumn(openLocalRow);
            var openLocal = Theme.Action("打开本机 RustDesk", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Desktop);
            openLocal.Click += async delegate { await OpenLocalRustDeskOnAssist(openLocal); };
            Theme.Row(openLocalRow, 36, openLocal);
            Theme.Row(layout, 36, openLocalRow);
            Theme.FillRow(layout);
            card.Controls.Add(layout);
            return card;
        }

        private async Task OpenLocalRustDeskOnAssist(Button btn)
        {
            btn.Enabled = false;
            SetStatus("正在启动本机 RustDesk…", Theme.PillTone.Info);
            try
            {
                string error = await Task.Run(() =>
                {
                    string detail;
                    return Engines.LaunchRustDesk(out detail) ? null : detail;
                });
                if (!IsDisposed) SetStatus(error ?? "已打开本机 RustDesk 窗口，可查看设备码并确认连接请求。", error == null ? Theme.PillTone.Success : Theme.PillTone.Alert);
            }
            finally { if (!IsDisposed) btn.Enabled = true; }
        }

        private Control BuildStreamCard()
        {
            var card = new Theme.Card();
            var layout = Theme.Grid(1);
            Theme.FillColumn(layout);
            Theme.Row(layout, 44, CardHeader(Theme.Glyph.Desktop, "低延迟桌面串流", "Moonlight 桌面串流；开启 V+ 自适应后使用 ABR", Theme.SuccessLight, Theme.Success));
            Theme.Row(layout, 14);
            Theme.Row(layout, 22, Theme.Text("主机地址（优先局域网 IP 或公网 IPv6，跨网可用组网 IP）", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(layout, 6);

            var target = Theme.Grid(3);
            Theme.FillColumn(target);
            Theme.FixedColumn(target, 8);
            Theme.FixedColumn(target, 98);
            discover.Click += async delegate { await DiscoverPeers(); };
            Theme.Row(target, 38, Theme.WrapInput(streamHost), null, discover);
            Theme.Row(layout, 38, target);
            Theme.Row(layout, 10);

            Theme.Row(layout, 22, Theme.Text("画质与网络档位", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(layout, 6);
            var settings = Theme.Grid(3);
            Theme.FillColumn(settings);
            Theme.FixedColumn(settings, 8);
            Theme.FixedColumn(settings, 98);
            probe.Click += async delegate { await ProbeLink(); };
            Theme.Row(settings, 38, Theme.WrapInput(quality), null, probe);
            Theme.Row(layout, 38, settings);
            Theme.Row(layout, 10);

            // Quick preset chips
            var chips = Theme.Grid(6);
            Theme.FixedColumn(chips, 88);
            Theme.FixedColumn(chips, 6);
            Theme.FixedColumn(chips, 88);
            Theme.FixedColumn(chips, 6);
            Theme.FixedColumn(chips, 94);
            Theme.FillColumn(chips);
            var chipWan = Theme.Action("外网抗丢包", Theme.ButtonStyle.Chip);
            var chipWeak = Theme.Action("6M 弱网", Theme.ButtonStyle.Chip);
            var chip2k90 = Theme.Action("局域网 2K90", Theme.ButtonStyle.Chip);
            chipWan.Click += delegate
            {
                bool tall = IsTallDisplay();
                quality.SelectedIndex = tall ? WanTallProfile : WanWideProfile;
                SetStatus("已切到 10 Mbps 外网流畅档，适合跨网稳定低延迟。", Theme.PillTone.Success);
            };
            chipWeak.Click += delegate
            {
                quality.SelectedIndex = WeakNetProfile;
                SetStatus("已切到 6 Mbps 弱网抗丢包档，重连后生效。", Theme.PillTone.Info);
            };
            chip2k90.Click += delegate
            {
                bool tall = IsTallDisplay();
                quality.SelectedIndex = tall ? Lan2K90TallProfile : Lan2K90WideProfile;
                SetStatus("已切到高速 2K 90 帧档（建议同局域网或高上行宽带直连使用）。", Theme.PillTone.Info);
            };
            Theme.Row(chips, 28, chipWan, null, chipWeak, null, chip2k90);
            Theme.Row(layout, 28, chips);
            Theme.Row(layout, 10);

            var modeRow = Theme.Grid(1);
            Theme.FillColumn(modeRow);
            Theme.Row(modeRow, 26, streamModeBadge);
            Theme.Row(layout, 26, modeRow);
            Theme.Row(layout, 6);
            Theme.Row(layout, 36, streamHint);
            Theme.Row(layout, 8);

            var actions = Theme.Grid(5);
            Theme.FillColumn(actions);
            Theme.FixedColumn(actions, 8);
            Theme.FixedColumn(actions, 102);
            Theme.FixedColumn(actions, 8);
            Theme.FixedColumn(actions, 106);
            var connect = Theme.Action("连接桌面", Theme.ButtonStyle.Primary, Theme.Glyph.Arrow);
            var pair = Theme.Action("首次配对", Theme.ButtonStyle.Secondary, Theme.Glyph.Pin);
            var manage = Theme.Action("打开客户端", Theme.ButtonStyle.AccentSoft);
            pair.Click += delegate { StartStream(true); };
            connect.Click += delegate { StartStream(false); };
            streamHost.KeyDown += delegate(object sender, KeyEventArgs args) { RunOnEnter(streamHost, connect, args); };
            manage.Click += delegate { OpenMoonlightGui(); };
            Theme.Row(actions, 40, connect, null, pair, null, manage);
            Theme.Row(layout, 40, actions);
            Theme.FillRow(layout);
            card.Controls.Add(layout);
            return card;
        }

        private bool IsTallDisplay()
        {
            var bounds = Screen.FromControl(this).Bounds;
            return bounds.Width > bounds.Height && (double)bounds.Width / bounds.Height <= 1.68;
        }

        private void OpenMoonlightGui()
        {
            switch (FoundationClient.SelectEngine(useFoundationClient))
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
                SetStatus("请先输入对方的远程设备码。", Theme.PillTone.Warning);
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

        private void StartStream(bool pair)
        {
            string message;
            string host = Engines.NormalizeHost(SelectedHost(), out message);
            if (host == null)
            {
                SetStatus(message, Theme.PillTone.Warning);
                streamHost.Focus();
                return;
            }
            var profile = quality.SelectedItem as QualityOption ?? QualityOptions[0];
            string note;
            bool started;
            switch (FoundationClient.SelectEngine(useFoundationClient))
            {
                case StreamEngine.Enhanced:
                    started = FoundationClient.Launch(host, profile.Width, profile.Height, profile.Fps, profile.BitrateKbps,
                        showStreamStats, playAudioOnHost, windowedStream, UsesHevc(profile), useFoundationClient,
                        lowLatencyNoVsync, enableFramePacing, pair, out note);
                    break;
                case StreamEngine.Official:
                    started = pair
                        ? Engines.PairMoonlight(host, out note)
                        : Engines.StreamMoonlight(host, profile.Width, profile.Height, profile.Fps, profile.BitrateKbps,
                            showStreamStats, playAudioOnHost, windowedStream, UsesHevc(profile),
                            lowLatencyNoVsync, enableFramePacing, out note);
                    break;
                default:
                    SetStatus(MissingStreamEngineMessage, Theme.PillTone.Alert);
                    return;
            }
            if (!started)
            {
                SetStatus(note, Theme.PillTone.Alert);
                return;
            }
            recent.RememberMoonlightHost(host);
            Remember(streamHost, recent.MoonlightHosts, note);
        }

        private static void RestoreRecents(ComboBox combo, List<string> entries)
        {
            foreach (var entry in entries) combo.Items.Add(entry);
        }

        private void Remember(ComboBox combo, List<string> entries, string note)
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
                SetStatus(note, Theme.PillTone.Success);
            }
            catch (IOException) { SetStatus(note + " 本次地址暂未保存。", Theme.PillTone.Warning); }
            catch (UnauthorizedAccessException) { SetStatus(note + " 本次地址暂未保存。", Theme.PillTone.Warning); }
        }

        private string SelectedHost()
        {
            var item = streamHost.SelectedItem as PeerOption;
            return item != null && streamHost.Text == item.ToString() ? item.Address : streamHost.Text;
        }

        private async Task DiscoverPeers()
        {
            discover.Enabled = probe.Enabled = false;
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
            catch (Exception)
            {
                if (!IsDisposed) SetStatus("暂时读不到 Tailscale 设备，可直接手动输入 IP。", Theme.PillTone.Warning);
            }
            finally
            {
                if (!IsDisposed) discover.Enabled = probe.Enabled = true;
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
            discover.Enabled = probe.Enabled = false;
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
            catch (Exception)
            {
                if (!IsDisposed) SetStatus("链路探测失败，请检查双方网络或 Tailscale 登录状态。", Theme.PillTone.Alert);
            }
            finally
            {
                if (!IsDisposed) discover.Enabled = probe.Enabled = true;
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
                "通用远控：打开本机 RustDesk 复制设备码。\n低延迟桌面：输入主机 IP 后点「首次配对」→「连接桌面」。\n防窥壁纸：在「防窥与显示」页自定义壁纸，快捷键 Ctrl+Alt+Shift+P 随时退出。",
                "使用与调优指南", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void RunOnEnter(ComboBox combo, Button button, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.Enter || combo.DroppedDown) return;
            args.SuppressKeyPress = true;
            button.PerformClick();
        }

        private bool UsesHevc(QualityOption profile)
        {
            return profile.Fps >= 90 || preferHevc;
        }

        /// <summary>按实际可用引擎描述串流能力；ABR 只在 V+ 且已启用时成立。</summary>
        private string StreamAvailabilityText()
        {
            switch (FoundationClient.SelectEngine(useFoundationClient))
            {
                case StreamEngine.Enhanced:
                    return useFoundationClient ? "将使用 Moonlight V+（启动时请求 ABR，实际效果待串流实测）。" : "将使用 Moonlight V+（ABR 已关闭，固定码率）。";
                case StreamEngine.Official:
                    return "将使用 Moonlight 官方客户端（固定码率，无 ABR）。";
                default:
                    return "未找到串流组件，请完整解压便携版。";
            }
        }

        private void UpdateStreamHint()
        {
            var selected = quality.SelectedItem as QualityOption;
            if (selected == null) return;
            var engine = FoundationClient.SelectEngine(useFoundationClient);
            if (engine == StreamEngine.Unavailable)
            {
                streamModeBadge.UpdateStatus("缺少串流组件", Theme.PillTone.Alert);
                streamHint.Text = MissingStreamEngineMessage;
                return;
            }
            string codec = UsesHevc(selected) ? "HEVC" : "H.264";
            string badgeText = (engine == StreamEngine.Enhanced ? "Moonlight V+ · " : "Moonlight · ") + codec;
            if (engine == StreamEngine.Enhanced && useFoundationClient) badgeText += " · ABR";
            if (lowLatencyNoVsync) badgeText += " · 低延迟";
            streamModeBadge.UpdateStatus(badgeText, engine == StreamEngine.Enhanced ? Theme.PillTone.Success : Theme.PillTone.Info);

            string abrNote = engine == StreamEngine.Enhanced && useFoundationClient
                ? "ABR 已请求；实际自动码率效果待串流实测。"
                : engine == StreamEngine.Enhanced ? "ABR 未启用，固定码率。" : "官方客户端无 ABR，固定码率。";
            if (selected.Width * 10 == selected.Height * 16)
                streamHint.Text = "16:10 满屏比例，建议被控端启用虚拟屏。" + abrNote;
            else if (selected.Fps >= 90)
                streamHint.Text = "高刷档位，建议同局域网或高上行直连。" + abrNote;
            else
                streamHint.Text = "已选用抗丢包友好档位。" + abrNote;
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
                if (autoActivatedCurtain)
                {
                    autoActivatedCurtain = false;
                    privacyCurtain.Deactivate(false);
                }
            }
            UpdatePrivacyIndicators();
        }

        private async Task PollRemoteGuardTick()
        {
            if (guardPollBusy || !privacyConfig.AutoOnRemote || IsDisposed) return;
            guardPollBusy = true;
            try
            {
                bool activeSession = await Task.Run(() => HostNetworkInfo.IsHostBeingControlled());
                if (IsDisposed || !privacyConfig.AutoOnRemote) return;
                if (activeSession)
                {
                    if (!privacyCurtain.IsActive)
                    {
                        string msg;
                        if (privacyCurtain.Activate(privacyConfig, false, out msg))
                        {
                            autoActivatedCurtain = true;
                            ShowDisplayBanner(msg, Theme.PillTone.Success);
                        }
                    }
                }
                else if (autoActivatedCurtain && privacyCurtain.IsActive && !privacyCurtain.IsPreview)
                {
                    autoActivatedCurtain = false;
                    privacyCurtain.Deactivate(privacyConfig.LockOnLocalExit);
                    ShowDisplayBanner("远控会话已结束，已自动收起防窥壁纸。", Theme.PillTone.Info);
                }
            }
            catch { }
            finally { guardPollBusy = false; }
        }

        internal void TogglePrivacyCurtain(bool fromLocalHotkey)
        {
            if (privacyCurtain.IsActive)
            {
                autoActivatedCurtain = false;
                privacyCurtain.Deactivate(fromLocalHotkey && !privacyCurtain.IsPreview);
                ShowDisplayBanner("已退出防窥屏壁纸。", Theme.PillTone.Info);
                return;
            }
            string msg;
            bool ok = privacyCurtain.Activate(privacyConfig, false, out msg);
            autoActivatedCurtain = false;
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
