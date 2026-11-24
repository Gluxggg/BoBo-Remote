using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private const int AllDevicesPage = 0;
        private const int DeviceDetailPage = 1;
        private const int AssistPage = 2;
        private const int FavoritesPage = 3;
        private const int PrivacyPage = 4;
        private const int SettingsPage = 5;

        private readonly Button[] navigation = new Button[6];
        private readonly Control[] pages = new Control[6];
        private readonly Theme.StatusBanner settingsBanner = new Theme.StatusBanner("设置变更会自动保存到本机。");
        private readonly Theme.Badge diagStatusBadge = new Theme.Badge("检测本机宿主中…", Theme.PillTone.Neutral);
        private readonly Label diagVersionLabel = Theme.Text("正在读取 Foundation Sunshine 版本与编码参数…", Theme.TextSize.Small, Theme.Muted);
        private readonly Label diagDetailsLabel = Theme.Text("", Theme.TextSize.Small, Theme.BodyInk);
        private Theme.Badge sunshineComponentBadge;

        private Button navQuickDevice;
        private int navQuickDeviceRow = -1;
        private TableLayoutPanel navRail;
        private BorderFramePanel borderFrame;
        private Button topGuideBtn;
        private Button topPrivacyBtn;
        private Button topStatusBtn;
        private Button topMenuBtn;
        private Button maxRestoreBtn;
        private Control perfDiagCard;
        private OnboardingOverlayForm activeOnboardingTour;
        private bool forceOnboardingOnLaunch;
        private bool onboardingScheduled;

        private bool navDevicesExpanded = true;
        private bool navAssistExpanded = true;
        private bool navPrivacyExpanded = true;
        private int navAllDevicesRow = -1;
        private int navStartAssistRow = -1;
        private int navFavoritesRow = -1;
        private int navPrivacyRow = -1;

        private TableLayoutPanel settingsDesk;
        private Panel settingsTabHost;
        private int settingsTabRow = -1;
        private Control[] settingsTabPanels;
        private Button[] settingsTabButtons;
        private int activeSettingsTab = 0;

        private int defaultQuality = 0;
        private bool fillLastTarget = true;
        private bool showStreamStats = false;
        private bool playAudioOnHost = false;
        private bool windowedStream = false;
        private bool preferHevc = true;
        private bool useFoundationClient = true;
        private bool lowLatencyNoVsync = true;
        private bool enableFramePacing = false;
        private bool minimizeToTray = false;
        private bool keepThisComputerAwake = false;

        private Control BuildChrome()
        {
            var outer = new BorderFramePanel();
            borderFrame = outer;
            var frame = Theme.Grid(1);
            Theme.FillColumn(frame);
            frame.BackColor = Theme.ChromeBg;

            Theme.Row(frame, 48, BuildTopBar());

            var body = Theme.Grid(2);
            Theme.FixedColumn(body, 224);
            Theme.FillColumn(body);
            Theme.FillRow(body);
            body.BackColor = Theme.ChromeBg;
            body.Controls.Add(BuildNavigation(), 0, 0);

            var workspace = new Theme.WorkspaceFrame();
            pages[AllDevicesPage] = BuildDevicesPage();
            pages[DeviceDetailPage] = BuildDeviceDetailPage();
            pages[AssistPage] = BuildWorkspace();
            pages[FavoritesPage] = BuildFavoritesPage();
            pages[PrivacyPage] = BuildPrivacyAndDisplayPage();
            pages[SettingsPage] = BuildSettingsPage();
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = false;
                workspace.Controls.Add(pages[i]);
            }
            ShowPage(AllDevicesPage);
            body.Controls.Add(workspace, 1, 0);

            Theme.FillRow(frame, body);
            outer.Controls.Add(frame);
            return outer;
        }

        private sealed class BorderFramePanel : Panel
        {
            internal BorderFramePanel()
            {
                Dock = DockStyle.Fill;
                Padding = new Padding(1);
                BackColor = Theme.WindowBorder;
                DoubleBuffered = true;
            }
        }

        private Control BuildTopBar()
        {
            var bar = Theme.Grid(13);
            Theme.FixedColumn(bar, 224);
            Theme.FillColumn(bar);
            Theme.FixedColumn(bar, 36);
            Theme.FixedColumn(bar, 4);
            Theme.FixedColumn(bar, 36);
            Theme.FixedColumn(bar, 4);
            Theme.FixedColumn(bar, 36);
            Theme.FixedColumn(bar, 4);
            Theme.FixedColumn(bar, 36);
            Theme.FixedColumn(bar, 17);
            Theme.FixedColumn(bar, 40);
            Theme.FixedColumn(bar, 40);
            Theme.FixedColumn(bar, 42);
            bar.BackColor = Theme.ChromeBg;

            var brand = new BrandHeaderControl();
            AttachDragHandlers(brand);

            var dragSpace = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ChromeBg, Margin = Padding.Empty };
            AttachDragHandlers(dragSpace);

            topGuideBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Help);
            topGuideBtn.AccessibleName = "新手使用引导";
            topGuideBtn.Margin = new Padding(0, 7, 0, 7);
            topGuideBtn.Click += delegate { StartOnboardingTour(false); };

            topPrivacyBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Shield);
            topPrivacyBtn.AccessibleName = "防窥壁纸快捷开关";
            topPrivacyBtn.Margin = new Padding(0, 7, 0, 7);
            topPrivacyBtn.Click += delegate { TogglePrivacyCurtain(false); };

            topStatusBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Bell);
            topStatusBtn.AccessibleName = "本机状态与网络地址";
            topStatusBtn.Margin = new Padding(0, 7, 0, 7);
            topStatusBtn.Click += delegate { ShowHostStatusPopover(); };

            topMenuBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Menu);
            topMenuBtn.AccessibleName = "更多功能菜单";
            topMenuBtn.Margin = new Padding(0, 7, 0, 7);
            topMenuBtn.Click += delegate { ShowTopMenuPopover(); };

            var sepWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.ChromeBg, Padding = new Padding(8, 15, 8, 15), Margin = Padding.Empty };
            sepWrap.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.StrokeStrong });
            AttachDragHandlers(sepWrap);

            var minBtn = Theme.Action("", Theme.ButtonStyle.Caption, Theme.Glyph.Minus);
            minBtn.AccessibleName = "最小化";
            minBtn.Click += delegate { WindowState = FormWindowState.Minimized; };

            maxRestoreBtn = Theme.Action("", Theme.ButtonStyle.Caption, Theme.Glyph.Maximize);
            maxRestoreBtn.AccessibleName = "最大化";
            maxRestoreBtn.Click += delegate { ToggleWindowMaximize(); };

            var closeBtn = Theme.Action("", Theme.ButtonStyle.Close, Theme.Glyph.Close);
            closeBtn.AccessibleName = "关闭";
            closeBtn.Click += delegate { Close(); };

            Theme.Row(bar, 48, brand, dragSpace, topGuideBtn, null, topPrivacyBtn, null, topStatusBtn, null, topMenuBtn, sepWrap, minBtn, maxRestoreBtn, closeBtn);
            return bar;
        }

        private void AttachDragHandlers(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (e.Clicks == 2) ToggleWindowMaximize();
                    else BeginDragWindow();
                }
            };
        }

        private sealed class BrandHeaderControl : Control
        {
            internal BrandHeaderControl()
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.ChromeBg;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.ChromeBg);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                float scale = g.DpiX / 96f;
                float iconSize = (float)Math.Round(26f * scale);
                float left = (float)Math.Round(16f * scale);
                float top = (float)Math.Round((Height - iconSize) / 2f);
                Theme.DrawBrandMark(g, new RectangleF(left, top, iconSize, iconSize));

                int textLeft = (int)Math.Round(left + iconSize + 9f * scale);
                var textRect = new Rectangle(textLeft, 0, Math.Max(10, Width - textLeft - 8), Height);
                using (var titleFont = new Font("Microsoft YaHei UI", 10.25f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, "BoBo远程", titleFont, textRect, Theme.Ink,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
            }
        }

        private Control BuildNavigation()
        {
            var rail = Theme.Grid(1);
            Theme.FillColumn(rail);
            rail.BackColor = Theme.Rail;
            rail.Padding = new Padding(10, 8, 10, 10);
            navRail = rail;

            // Group 1: 我的设备
            var devicesGroup = Theme.Action("我的设备", Theme.ButtonStyle.NavSection, Theme.Glyph.Devices);
            Theme.Highlight(devicesGroup, true);
            devicesGroup.Click += delegate
            {
                navDevicesExpanded = !navDevicesExpanded;
                Theme.Highlight(devicesGroup, navDevicesExpanded);
                SyncNavigationSectionVisibility();
            };
            Theme.Row(rail, 36, devicesGroup);

            // Quick device shortcut (dynamically shown when a peer/favorite exists)
            navQuickDevice = MakePageButton(DeviceDetailPage, Theme.Glyph.WindowsTile, "暂无远端设备");
            Theme.SetNavIndent(navQuickDevice, 14);
            Theme.SetGlyphTint(navQuickDevice, Theme.Muted);
            navQuickDevice.Visible = false;
            navQuickDeviceRow = rail.RowCount;
            Theme.Row(rail, 0, navQuickDevice);

            var allDevicesBtn = MakePageButton(AllDevicesPage, Theme.Glyph.AllDevices, "全部设备");
            Theme.SetNavIndent(allDevicesBtn, 14);
            Theme.SetGlyphTint(allDevicesBtn, Theme.Accent);
            navAllDevicesRow = rail.RowCount;
            Theme.Row(rail, 38, allDevicesBtn);

            Theme.Row(rail, 10);

            // Group 2: 远程协助
            var assistGroup = Theme.Action("远程协助", Theme.ButtonStyle.NavSection, Theme.Glyph.Link);
            Theme.Highlight(assistGroup, true);
            assistGroup.Click += delegate
            {
                navAssistExpanded = !navAssistExpanded;
                Theme.Highlight(assistGroup, navAssistExpanded);
                SyncNavigationSectionVisibility();
            };
            Theme.Row(rail, 36, assistGroup);

            var startAssistBtn = MakePageButton(AssistPage, Theme.Glyph.Assist, "开始协助");
            Theme.SetNavIndent(startAssistBtn, 14);
            Theme.SetGlyphTint(startAssistBtn, Theme.Accent);
            navStartAssistRow = rail.RowCount;
            Theme.Row(rail, 38, startAssistBtn);

            var favoritesBtn = MakePageButton(FavoritesPage, Theme.Glyph.Star, "收藏设备");
            Theme.SetNavIndent(favoritesBtn, 14);
            Theme.SetGlyphTint(favoritesBtn, Theme.Accent);
            navFavoritesRow = rail.RowCount;
            Theme.Row(rail, 38, favoritesBtn);

            Theme.Row(rail, 10);

            // Group 3: 显示与隐私
            var privacyGroup = Theme.Action("防窥与显示", Theme.ButtonStyle.NavSection, Theme.Glyph.Shield);
            Theme.Highlight(privacyGroup, true);
            privacyGroup.Click += delegate
            {
                navPrivacyExpanded = !navPrivacyExpanded;
                Theme.Highlight(privacyGroup, navPrivacyExpanded);
                SyncNavigationSectionVisibility();
            };
            Theme.Row(rail, 36, privacyGroup);

            var privacyBtn = MakePageButton(PrivacyPage, Theme.Glyph.Eye, "防窥壁纸与虚拟屏");
            Theme.SetNavIndent(privacyBtn, 14);
            Theme.SetGlyphTint(privacyBtn, Theme.Accent);
            navPrivacyRow = rail.RowCount;
            Theme.Row(rail, 38, privacyBtn);

            Theme.FillRow(rail);

            // Bottom pinned divider + Settings (exact UU layout)
            Theme.Row(rail, 1, Theme.Rule());
            Theme.Row(rail, 8);
            var settingsBtn = MakePageButton(SettingsPage, Theme.Glyph.Gear, "设置");
            Theme.SetNavIndent(settingsBtn, 0);
            Theme.SetGlyphTint(settingsBtn, Theme.BodyInk);
            Theme.Row(rail, 38, settingsBtn);
            return rail;
        }

        private void SyncNavigationSectionVisibility()
        {
            if (navRail == null) return;
            float scale = navRail.DeviceDpi / 96f;
            float itemH = 38f * scale;
            bool showQuick = navDevicesExpanded && !string.IsNullOrEmpty(quickDeviceAddress);

            navRail.SuspendLayout();
            if (navQuickDeviceRow >= 0)
            {
                navRail.RowStyles[navQuickDeviceRow].Height = showQuick ? itemH : 0;
                navQuickDevice.Visible = showQuick;
            }
            if (navAllDevicesRow >= 0)
            {
                navRail.RowStyles[navAllDevicesRow].Height = navDevicesExpanded ? itemH : 0;
                navigation[AllDevicesPage].Visible = navDevicesExpanded;
            }
            if (navStartAssistRow >= 0)
            {
                navRail.RowStyles[navStartAssistRow].Height = navAssistExpanded ? itemH : 0;
                navigation[AssistPage].Visible = navAssistExpanded;
            }
            if (navFavoritesRow >= 0)
            {
                navRail.RowStyles[navFavoritesRow].Height = navAssistExpanded ? itemH : 0;
                navigation[FavoritesPage].Visible = navAssistExpanded;
            }
            if (navPrivacyRow >= 0)
            {
                navRail.RowStyles[navPrivacyRow].Height = navPrivacyExpanded ? itemH : 0;
                navigation[PrivacyPage].Visible = navPrivacyExpanded;
            }
            navRail.ResumeLayout(true);
        }

        private Button MakePageButton(int page, Theme.Glyph glyph, string title)
        {
            var button = Theme.Action(title, Theme.ButtonStyle.Navigation, glyph);
            button.Click += delegate
            {
                if (page == DeviceDetailPage && string.IsNullOrEmpty(detailDeviceAddress) && !string.IsNullOrEmpty(quickDeviceAddress))
                    OpenDeviceDetail(quickDeviceName, quickDeviceAddress, quickDeviceOnline, quickDeviceLink, quickDeviceIsStream);
                else
                    ShowPage(page);
            };
            navigation[page] = button;
            return button;
        }

        private void ShowPage(int selected)
        {
            if (selected < 0 || selected >= pages.Length) return;
            for (int i = 0; i < pages.Length; i++)
            {
                bool active = i == selected;
                if (pages[i] != null) pages[i].Visible = active;
                if (navigation[i] != null) Theme.Highlight(navigation[i], active);
            }
            if (pages[selected] != null)
            {
                pages[selected].BringToFront();
                var vp = pages[selected] as Theme.SlimScrollViewport;
                if (vp != null) vp.RefreshLayout();
            }
            if (selected == AllDevicesPage || selected == FavoritesPage) RefreshSavedDevices();
        }

        private static Theme.SlimScrollViewport CreatePage(int initialHeight, out TableLayoutPanel desk)
        {
            var viewport = new Theme.SlimScrollViewport();
            desk = Theme.Grid(1);
            Theme.FillColumn(desk);
            desk.Dock = DockStyle.None;
            desk.Height = initialHeight;
            desk.Padding = new Padding(36, 26, 36, 28);
            desk.BackColor = Theme.Workspace;
            viewport.Controls.Add(desk);
            return viewport;
        }

        private static void AddPageHeader(TableLayoutPanel grid, string title, string subtitle, Control trailing = null, int actionWidth = 118)
        {
            bool hasSub = !string.IsNullOrEmpty(subtitle);
            int headerH = hasSub ? 56 : 38;
            var titles = Theme.Grid(1);
            Theme.FillColumn(titles);
            Theme.Row(titles, 36, Theme.Text(title, Theme.TextSize.Title, Theme.Ink));
            if (hasSub) Theme.Row(titles, 20, Theme.Text(subtitle, Theme.TextSize.Small, Theme.Muted));

            if (trailing == null)
            {
                Theme.Row(grid, headerH, titles);
                return;
            }
            var heading = Theme.Grid(3);
            Theme.FillColumn(heading);
            Theme.FixedColumn(heading, 12);
            Theme.FixedColumn(heading, actionWidth);
            int vPad = hasSub ? 11 : 3;
            trailing.Margin = new Padding(0, vPad, 0, vPad);
            Theme.Row(heading, headerH, titles, null, trailing);
            Theme.Row(grid, headerH, heading);
        }

        // ==================== SETTINGS PAGE (UU EXACT SINGLE-COLUMN STYLE) ====================

        private Control BuildSettingsPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(700, out desk);
            settingsDesk = desk;

            AddPageHeader(desk, "设置", null);
            Theme.Row(desk, 14);

            // UU-style clean horizontal text tabs with short blue underline indicator
            var tabBar = Theme.Grid(9);
            Theme.FixedColumn(tabBar, 58);
            Theme.FixedColumn(tabBar, 24);
            Theme.FixedColumn(tabBar, 102);
            Theme.FixedColumn(tabBar, 24);
            Theme.FixedColumn(tabBar, 128);
            Theme.FixedColumn(tabBar, 24);
            Theme.FixedColumn(tabBar, 102);
            Theme.FillColumn(tabBar);
            Theme.FixedColumn(tabBar, 1);

            var tabGeneral = Theme.Action("常规", Theme.ButtonStyle.UnderlineTab);
            var tabStream = Theme.Action("串流与画质", Theme.ButtonStyle.UnderlineTab);
            var tabPerf = Theme.Action("低延迟与抗丢包", Theme.ButtonStyle.UnderlineTab);
            var tabNetwork = Theme.Action("引擎与网络", Theme.ButtonStyle.UnderlineTab);

            Theme.Row(tabBar, 34, tabGeneral, null, tabStream, null, tabPerf, null, tabNetwork, null, null);
            Theme.Row(desk, 34, tabBar);
            Theme.Row(desk, 18);

            settingsTabHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Workspace };
            settingsTabButtons = new[] { tabGeneral, tabStream, tabPerf, tabNetwork };
            settingsTabPanels = new Control[]
            {
                BuildGeneralSettingsTab(),
                BuildStreamSettingsTab(),
                BuildPerformanceTab(),
                BuildNetworkAndEnginesTab()
            };

            for (int i = 0; i < settingsTabPanels.Length; i++)
            {
                int tabIndex = i;
                settingsTabPanels[i].Dock = DockStyle.Fill;
                settingsTabPanels[i].Visible = false;
                settingsTabHost.Controls.Add(settingsTabPanels[i]);
                settingsTabButtons[i].Click += delegate { SelectSettingsTab(tabIndex); };
            }

            settingsTabRow = desk.RowCount;
            Theme.Row(desk, 460, settingsTabHost);
            Theme.Row(desk, 16);
            Theme.Row(desk, 40, settingsBanner);

            SelectSettingsTab(0);
            return viewport;
        }

        private void SelectSettingsTab(int selected)
        {
            if (settingsTabPanels == null || selected < 0 || selected >= settingsTabPanels.Length) return;
            activeSettingsTab = selected;
            for (int i = 0; i < settingsTabPanels.Length; i++)
            {
                bool active = i == selected;
                settingsTabPanels[i].Visible = active;
                Theme.Highlight(settingsTabButtons[i], active);
            }
            settingsTabPanels[selected].BringToFront();
            RecalculateSettingsDeskHeight();
        }

        private void RecalculateSettingsDeskHeight()
        {
            if (settingsDesk == null || settingsTabPanels == null || settingsTabRow < 0) return;
            int tabH = SumRows(settingsTabPanels[activeSettingsTab]);
            if (tabH < 200) tabH = DevicePixels(420);
            settingsDesk.RowStyles[settingsTabRow].Height = tabH;
            settingsDesk.Height = SumRows(settingsDesk);
            settingsDesk.PerformLayout();
        }

        private Control BuildGeneralSettingsTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            AddStandaloneSettingCard(page, Theme.Glyph.Desktop, "开机自动启动",
                "登录 Windows 后自动在后台启动 BoBo远程，随时准备连接",
                SystemToggle("开机自动启动", WindowsSession.StartsAtSignIn(), WindowsSession.TrySetStartAtSignIn));

            AddStandaloneSettingCard(page, Theme.Glyph.Bolt, "防止电脑休眠",
                "休眠将导致电脑无法远程控制（被控端强烈推荐开启）",
                SystemToggle("防止电脑休眠", false, ChangeKeepAwake));

            AddStandaloneSettingCard(page, Theme.Glyph.Pin, "关闭窗口时最小化到托盘",
                "保持防窥守护在后台运行，可从右下角托盘随时恢复或彻底退出",
                PreferenceToggle("最小化到托盘", minimizeToTray, value => minimizeToTray = value));

            AddStandaloneSettingCard(page, Theme.Glyph.Refresh, "启动时自动填入上次设备",
                "自动回填最近连接过的主机地址与协助设备码，省去重复输入",
                PreferenceToggle("启动时填入上次设备", fillLastTarget, value => fillLastTarget = value));

            var defaultProfile = Theme.Choice("默认串流画质", false);
            defaultProfile.DropDownWidth = 340;
            foreach (var item in quality.Items) defaultProfile.Items.Add(item);
            defaultProfile.SelectedIndex = defaultQuality;
            defaultProfile.SelectedIndexChanged += delegate
            {
                defaultQuality = defaultProfile.SelectedIndex;
                quality.SelectedIndex = defaultQuality;
                SavePreferences();
            };
            AddStandaloneSettingCard(page, Theme.Glyph.Image, "默认串流画质档位",
                "发起低延迟桌面串流时默认采用的分辨率、帧率与码率档位",
                Theme.WrapInput(defaultProfile), 280);

            var replayGuide = Theme.Action("重新查看引导", Theme.ButtonStyle.Secondary, Theme.Glyph.Help);
            replayGuide.Click += delegate { StartOnboardingTour(false); };
            AddStandaloneSettingCard(page, Theme.Glyph.Help, "新手引导使用教程",
                "以灰白框选高亮方式分步回顾设备连接、远程协助、防窥壁纸与低延迟调优",
                replayGuide, 134);

            var clear = Theme.Action("清空记录", Theme.ButtonStyle.Secondary);
            clear.Click += delegate { ClearRecents(); };
            AddStandaloneSettingCard(page, Theme.Glyph.Trash, "清除最近连接历史",
                "清除本机保存的设备码与主机地址历史记录（不影响收藏与配对）",
                clear, 112);

            return page;
        }

        private Control BuildStreamSettingsTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            var enhanced = PreferenceToggle("Foundation 自适应码率", useFoundationClient,
                value => { useFoundationClient = value; UpdateStreamHint(); });
            enhanced.Enabled = FoundationClient.IsAvailable;
            AddStandaloneSettingCard(page, Theme.Glyph.Bolt, "Foundation 自适应码率（ABR · 抗丢包推荐）",
                enhanced.Enabled ? "使用 Moonlight V+ 根据实时丢包与 RTT 动态调优码率，跨网弱网防卡死" : "当前目录缺少 engines\\MoonlightVPlus 组件",
                enhanced);

            AddStandaloneSettingCard(page, Theme.Glyph.Desktop, "优先使用 HEVC (H.265) 硬件编解码",
                "同等高清画质节省约 35%–40% 带宽，外网与高刷场景文字更锐利（默认开启）",
                PreferenceToggle("优先使用 HEVC 编码", preferHevc, value => { preferHevc = value; UpdateStreamHint(); }));

            AddStandaloneSettingCard(page, Theme.Glyph.Bolt, "极速低延迟模式（关闭垂直同步 VSync）",
                "向串流引擎传入 --no-vsync，减少约 1 帧显示缓冲延迟，键鼠更跟手（默认开启）",
                PreferenceToggle("极速低延迟", lowLatencyNoVsync, value => lowLatencyNoVsync = value));

            AddStandaloneSettingCard(page, Theme.Glyph.Refresh, "帧节奏平滑（Frame Pacing）",
                "关闭时端到端延迟最低；若高刷屏拖动窗口有微抖动可开启平滑",
                PreferenceToggle("帧节奏平滑", enableFramePacing, value => enableFramePacing = value));

            AddStandaloneSettingCard(page, Theme.Glyph.Info, "串流实时性能浮层（OSD）",
                "连接后实时显示帧率、网络丢帧、RTT 延迟与解码耗时（会话中可按 Ctrl+Alt+Shift+S）",
                PreferenceToggle("串流统计", showStreamStats, value => showStreamStats = value));

            AddStandaloneSettingCard(page, Theme.Glyph.Bell, "在被控端同步播放声音",
                "关闭时音频仅传输至主控端设备，避免被控端外放打扰他人",
                PreferenceToggle("在被控端同时播放声音", playAudioOnHost, value => playAudioOnHost = value));

            AddStandaloneSettingCard(page, Theme.Glyph.Maximize, "使用窗口模式打开串流",
                "开启后以可自由拖拽拉伸的窗口显示远程桌面；关闭时默认全屏",
                PreferenceToggle("使用窗口模式", windowedStream, value => windowedStream = value));

            return page;
        }

        private Control BuildPerformanceTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            // Card 1: Host Sunshine Diagnostic + One-click Anti-Loss Tuning
            var hostCard = new Theme.Card { Padding = new Padding(22, 18, 22, 18) };
            perfDiagCard = hostCard;
            var h = Theme.Grid(1);
            Theme.FillColumn(h);

            diagStatusBadge.AlignRight = true;
            var head = Theme.Grid(3);
            Theme.FillColumn(head);
            Theme.FixedColumn(head, 12);
            Theme.FixedColumn(head, 176);
            Theme.Row(head, 28, Theme.Text("被控端抗丢包诊断与一键调优", Theme.TextSize.Section, Theme.Ink), null, diagStatusBadge);
            Theme.Row(h, 28, head);
            Theme.Row(h, 4);
            Theme.Row(h, 22, diagVersionLabel);
            Theme.Row(h, 8);
            diagDetailsLabel.TextAlign = ContentAlignment.TopLeft;
            diagDetailsLabel.AutoEllipsis = false;
            Theme.Row(h, 66, diagDetailsLabel);
            Theme.Row(h, 12);

            var btnBar = Theme.Grid(7);
            Theme.FixedColumn(btnBar, 152);
            Theme.FixedColumn(btnBar, 8);
            Theme.FixedColumn(btnBar, 124);
            Theme.FixedColumn(btnBar, 8);
            Theme.FixedColumn(btnBar, 110);
            Theme.FixedColumn(btnBar, 8);
            Theme.FillColumn(btnBar);

            var applyWan = Theme.Action("外网抗丢包调优", Theme.ButtonStyle.Primary, Theme.Glyph.Bolt);
            var applyLan = Theme.Action("局域网高刷档", Theme.ButtonStyle.Secondary);
            var restoreCfg = Theme.Action("还原原配置", Theme.ButtonStyle.Secondary);
            var refreshDiag = Theme.Action("刷新诊断", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            applyWan.Click += delegate { OpenOptimizeTool("Apply", "WanAntiLoss"); };
            applyLan.Click += delegate { OpenOptimizeTool("Apply", "LanUltraLow"); };
            restoreCfg.Click += delegate { OpenOptimizeTool("Restore", "WanAntiLoss"); };
            refreshDiag.Click += async delegate
            {
                refreshDiag.Enabled = false;
                try { await RefreshHostDiagnosticsAsync(); }
                finally { if (!IsDisposed) refreshDiag.Enabled = true; }
            };
            Theme.Row(btnBar, 36, applyWan, null, applyLan, null, restoreCfg, null, refreshDiag);
            Theme.Row(h, 36, btnBar);
            hostCard.Controls.Add(h);

            Theme.Row(page, 212, hostCard);
            Theme.Row(page, 12);

            // Card 2: Architecture checklist learned from UU / Moonlight / Parsec
            var guideCard = new Theme.Card { Padding = new Padding(22, 16, 22, 16) };
            var g = Theme.Grid(1);
            Theme.FillColumn(g);
            Theme.Row(g, 26, Theme.Text("高清高帧率 · 抗丢包架构要点", Theme.TextSize.Section, Theme.Ink));
            Theme.Row(g, 8);
            Theme.Row(g, 24, Theme.Text("• 编码层：硬编零拷贝 + CBR & HRD 硬码率约束 + LTR 参考帧 + 按需 GOP，杜绝突发大帧拖垮 Wi-Fi 缓冲。", Theme.TextSize.Small, Theme.BodyInk));
            Theme.Row(g, 24, Theme.Text("• 传输层：UDP 动态 Auto-FEC（0 额外 RTT 本地纠错）+ Moonlight V+ ABR 实时降码率防花屏断流。", Theme.TextSize.Small, Theme.BodyInk));
            Theme.Row(g, 24, Theme.Text("• 网络层：同局域网直连或双端公网 IPv6 / Tailscale 直连优先；高跳数中继时可搭配 EasyTier。", Theme.TextSize.Small, Theme.BodyInk));
            Theme.Row(g, 12);

            var links = Theme.Grid(7);
            Theme.FixedColumn(links, 144);
            Theme.FixedColumn(links, 10);
            Theme.FixedColumn(links, 156);
            Theme.FixedColumn(links, 10);
            Theme.FixedColumn(links, 160);
            Theme.FixedColumn(links, 10);
            Theme.FillColumn(links);

            var openGuide = Theme.Action("打开调优指南", Theme.ButtonStyle.Secondary, Theme.Glyph.Help);
            var openFs = Theme.Action("Sunshine Releases", Theme.ButtonStyle.Secondary);
            var openEt = Theme.Action("EasyTier 组网加速", Theme.ButtonStyle.Secondary);
            openGuide.Click += delegate { OpenGuide(); };
            openFs.Click += delegate { OpenExternalUrl(HostNetworkInfo.FoundationReleasesUrl, "已打开 Foundation Sunshine Releases 页。"); };
            openEt.Click += delegate { OpenExternalUrl(HostNetworkInfo.EasyTierReleasesUrl, "已打开 EasyTier 开源异地组网 Releases 页。"); };
            Theme.Row(links, 34, openGuide, null, openFs, null, openEt, null, null);
            Theme.Row(g, 34, links);
            guideCard.Controls.Add(g);

            Theme.Row(page, 182, guideCard);
            return page;
        }

        private Control BuildNetworkAndEnginesTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            AddStandaloneSettingCard(page, Theme.Glyph.Bolt, "Moonlight V+ 6.5.6",
                "主力低延迟串流引擎，原生支持 Foundation ABR 自适应抗丢包",
                BuildEngineActionRow(FoundationClient.IsAvailable, "打开 V+", delegate { OpenMoonlightGui(); }), 176);

            AddStandaloneSettingCard(page, Theme.Glyph.Desktop, "Moonlight 6.2.0",
                "官方标准便携串流引擎，可作为独立备用客户端",
                EngineAvailability(Engines.MoonlightPresent), 92);

            AddStandaloneSettingCard(page, Theme.Glyph.Assist, "RustDesk 1.5.3",
                "通用远程协助、9 位设备码接入与双向文件互传引擎",
                BuildEngineActionRow(Engines.RustDeskPresent, "打开窗口", async delegate { await OpenLocalRustDeskOnSettings(); }), 176);

            sunshineComponentBadge = new Theme.Badge("检测中…", Theme.PillTone.Neutral);
            AddStandaloneSettingCard(page, Theme.Glyph.Network, "Foundation Sunshine 被控宿主",
                "支持便携版内置宿主与系统安装版，负责硬编采集与 Zako 虚拟屏",
                sunshineComponentBadge, 108);

            AddStandaloneSettingCard(page, Theme.Glyph.Link, "Tailscale 异地组网 CLI",
                "自动发现同账号 Windows 设备并区分直连与 DERP 中继路径",
                EngineAvailability(Engines.TailscalePresent), 92);

            return page;
        }

        private async System.Threading.Tasks.Task OpenLocalRustDeskOnSettings()
        {
            ShowSettingsBanner("正在启动本机 RustDesk…", Theme.PillTone.Info);
            string error = await System.Threading.Tasks.Task.Run(() =>
            {
                string detail;
                return Engines.LaunchRustDesk(out detail) ? null : detail;
            });
            if (!IsDisposed)
                ShowSettingsBanner(error ?? "已打开本机 RustDesk 窗口。", error == null ? Theme.PillTone.Success : Theme.PillTone.Alert);
        }

        private static Control BuildEngineActionRow(bool ready, string buttonText, EventHandler onClick)
        {
            var box = Theme.Grid(3);
            Theme.FixedColumn(box, 74);
            Theme.FixedColumn(box, 8);
            Theme.FixedColumn(box, 92);
            var btn = Theme.Action(buttonText, Theme.ButtonStyle.Secondary);
            btn.Enabled = ready;
            btn.Click += onClick;
            Theme.Row(box, 32, EngineAvailability(ready), null, btn);
            return box;
        }

        /// <summary>UU 设置页标准单列卡片行（左侧线性图标 + 标题/说明 + 右侧开关或下拉控件）。</summary>
        private static void AddStandaloneSettingCard(TableLayoutPanel parent, Theme.Glyph glyph, string title, string description, Control action, int actionWidth = 104)
        {
            bool hasDesc = !string.IsNullOrEmpty(description);
            int cardH = hasDesc ? 70 : 58;

            var card = new Theme.Card { Padding = new Padding(18, 10, 18, 10) };
            var row = Theme.Grid(5);
            Theme.FixedColumn(row, 26);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 14);
            Theme.FixedColumn(row, actionWidth);

            var icon = new Theme.IconTile(glyph, true, Color.Transparent, Theme.BodyInk, 0f);
            Control textBlock;
            if (hasDesc)
            {
                var stack = Theme.Grid(1);
                Theme.FillColumn(stack);
                stack.Padding = new Padding(0, 3, 0, 3);
                Theme.Row(stack, 24, Theme.Text(title, Theme.TextSize.Label, Theme.Ink));
                Theme.Row(stack, 20, Theme.Text(description, Theme.TextSize.Small, Theme.Muted));
                textBlock = stack;
            }
            else
            {
                textBlock = Theme.Text(title, Theme.TextSize.Label, Theme.Ink);
            }

            int innerH = cardH - 20;
            int actionPad = Math.Max(0, (innerH - 34) / 2);
            action.Margin = new Padding(0, actionPad, 0, actionPad);
            Theme.Row(row, innerH, icon, null, textBlock, null, action);
            card.Controls.Add(row);

            Theme.Row(parent, cardH, card);
            Theme.Row(parent, 10);
        }

        private void UpdateDiagnosticsUi()
        {
            if (hostSnapshot == null) return;
            if (!hostSnapshot.SunshineInstalled)
            {
                diagStatusBadge.UpdateStatus("未检测到宿主", Theme.PillTone.Neutral);
                diagVersionLabel.Text = "本机未安装或未准备 Foundation Sunshine（仅控制他人时无需安装，被控端生成配对码时会自动准备内置宿主）。";
                diagDetailsLabel.Text = "当前串流客户端：" + StreamAvailabilityText();
            }
            else
            {
                bool hostRunning = hostSnapshot.SunshineRunning;
                string hostState = hostRunning ? "宿主运行中" : "宿主未运行";
                if (hostSnapshot.AntiLossTuned)
                    diagStatusBadge.UpdateStatus(hostState + " · 已优化", hostRunning ? Theme.PillTone.Success : Theme.PillTone.Warning);
                else
                    diagStatusBadge.UpdateStatus(hostState + " · 可调优", Theme.PillTone.Warning);

                string enc = string.IsNullOrEmpty(hostSnapshot.Encoder) ? "自动" : hostSnapshot.Encoder;
                diagVersionLabel.Text = "版本：" + hostSnapshot.SunshineVersion + "  ·  编码器：" + enc + "  ·  来源：" + hostSnapshot.HostSourceLabel;
                if (hostSnapshot.Recommendations.Count == 0)
                    diagDetailsLabel.Text = (hostRunning ? "所有抗丢包关键项均已就绪。" : "宿主服务当前未运行；") + "点击「外网抗丢包调优」可按当前显卡自动写入 CBR + HRD + Auto-FEC + LTR 参数。";
                else
                    diagDetailsLabel.Text = "• " + string.Join("\r\n• ", hostSnapshot.Recommendations.ToArray());
            }
            if (sunshineComponentBadge != null)
            {
                sunshineComponentBadge.UpdateStatus(
                    hostSnapshot.SunshineInstalled ? (hostSnapshot.SunshineRunning ? "运行中" : "已就绪") : "未就绪",
                    hostSnapshot.SunshineRunning ? Theme.PillTone.Success : (hostSnapshot.SunshineInstalled ? Theme.PillTone.Info : Theme.PillTone.Neutral));
            }
        }

        // ==================== TOP BAR POPOVERS (UU IMAGE 9 & 10 STYLE) ====================

        private void ShowTopMenuPopover()
        {
            var menu = CreateCleanPopupMenu();
            AddPopupItem(menu, "刷新全部设备与本机状态", delegate { var _ = RefreshDashboardAsync(); });
            AddPopupItem(menu, "切换防窥屏壁纸 (Ctrl+Alt+Shift+P)", delegate { TogglePrivacyCurtain(false); });
            AddPopupItem(menu, "新手引导使用教程", delegate { StartOnboardingTour(false); });
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "打开 Moonlight V+ 管理端", delegate { OpenMoonlightGui(); });
            AddPopupItem(menu, "打开本机 RustDesk 窗口", delegate { var _ = OpenLocalRustDeskOnSettings(); });
            AddPopupItem(menu, "低延迟调优与防窥指南", delegate { OpenGuide(); });
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "关于 BoBo远程", delegate { ShowAboutDialog(); });

            Point pt = topMenuBtn.PointToScreen(new Point(topMenuBtn.Width - 208, topMenuBtn.Height + 6));
            menu.Show(pt);
        }

        private void ShowHostStatusPopover()
        {
            var menu = CreateCleanPopupMenu();
            string hostState = hostSnapshot.SunshineRunning ? "被控宿主运行中" : (hostSnapshot.SunshineInstalled ? "被控宿主已就绪" : "内置宿主待准备");
            var header = new ToolStripMenuItem("本机：" + Environment.MachineName + "  (" + hostState + ")") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            string lan = string.IsNullOrEmpty(hostSnapshot.LanIpv4) ? "未连接" : hostSnapshot.LanIpv4;
            string ts = string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4) ? "未登录" : hostSnapshot.TailscaleIpv4;
            string v6 = string.IsNullOrEmpty(hostSnapshot.PublicIpv6) ? "无公网 IPv6" : ShortenIpv6(hostSnapshot.PublicIpv6);

            AddPopupItem(menu, "复制局域网 IP：" + lan, delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); });
            AddPopupItem(menu, "复制 Tailscale IP：" + ts, delegate { CopyHostAddress("Tailscale IP", hostSnapshot.TailscaleIpv4); });
            AddPopupItem(menu, "复制公网 IPv6：" + v6, delegate { CopyHostAddress("公网 IPv6", hostSnapshot.PublicIpv6); });
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "前往「开始协助」生成 4 位配对码", delegate { ShowPage(AssistPage); });

            Point pt = topStatusBtn.PointToScreen(new Point(topStatusBtn.Width - 250, topStatusBtn.Height + 6));
            menu.Show(pt);
        }

        private static ContextMenuStrip CreateCleanPopupMenu()
        {
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = Theme.Paper,
                ForeColor = Theme.Ink,
                Font = new Font("Microsoft YaHei UI", 9.25f),
                Padding = new Padding(4, 6, 4, 6),
                Renderer = new CleanMenuRenderer()
            };
            return menu;
        }

        private static void AddPopupItem(ContextMenuStrip menu, string text, EventHandler click)
        {
            var item = new ToolStripMenuItem(text)
            {
                Padding = new Padding(8, 6, 12, 6),
                ForeColor = Theme.Ink
            };
            item.Click += click;
            menu.Items.Add(item);
        }

        private sealed class CleanMenuRenderer : ToolStripProfessionalRenderer
        {
            internal CleanMenuRenderer() : base(new CleanMenuColors()) { }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(Theme.StrokeStrong, 1f))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                if (e.Item.Selected && e.Item.Enabled)
                    Theme.Surface(e.Graphics, rect, 5f, Theme.SurfaceSubtle, Color.Transparent);
                else
                    using (var b = new SolidBrush(Theme.Paper)) e.Graphics.FillRectangle(b, e.Item.ContentRectangle);
            }
        }

        private sealed class CleanMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Paper; } }
            public override Color MenuBorder { get { return Theme.StrokeStrong; } }
            public override Color SeparatorDark { get { return Theme.Divider; } }
            public override Color SeparatorLight { get { return Theme.Paper; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Paper; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Paper; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Paper; } }
        }

        private void ShowAboutDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "关于 BoBo远程";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Theme.Workspace;
                dlg.ClientSize = new Size(460, 268);
                dlg.Icon = appIcon ?? Icon;

                var root = Theme.Grid(1);
                Theme.FillColumn(root);
                root.Padding = new Padding(24, 20, 24, 18);
                root.BackColor = Theme.Workspace;

                var hero = Theme.Grid(3);
                Theme.FixedColumn(hero, 56);
                Theme.FixedColumn(hero, 14);
                Theme.FillColumn(hero);

                var logoBox = new BrandLogoPreviewBox();
                var titleStack = Theme.Grid(1);
                Theme.FillColumn(titleStack);
                titleStack.Padding = new Padding(0, 4, 0, 0);
                Theme.Row(titleStack, 28, Theme.Text("BoBo远程", Theme.TextSize.Title, Theme.Ink));
                Theme.Row(titleStack, 20, Theme.Text("低延迟高帧率双端远程桌面 · 隐私防窥便携版", Theme.TextSize.Small, Theme.Muted));
                Theme.Row(hero, 56, logoBox, null, titleStack);
                Theme.Row(root, 56, hero);
                Theme.Row(root, 14);

                var infoCard = new Theme.Card { Padding = new Padding(16, 12, 16, 12) };
                var ic = Theme.Grid(1);
                Theme.FillColumn(ic);
                Theme.Row(ic, 22, Theme.Text("• 串流引擎：Foundation Sunshine + Moonlight V+ (ABR + Auto-FEC + HEVC)", Theme.TextSize.Small, Theme.BodyInk));
                Theme.Row(ic, 22, Theme.Text("• 通用协助与文件传输：RustDesk 1.5.3", Theme.TextSize.Small, Theme.BodyInk));
                Theme.Row(ic, 22, Theme.Text("• 隐私保护：WDA_EXCLUDEFROMCAPTURE 物理屏防窥壁纸 + Zako 虚拟屏", Theme.TextSize.Small, Theme.BodyInk));
                Theme.Row(ic, 22, Theme.Text("• 配置目录：" + RecentStore.DirPath, Theme.TextSize.Small, Theme.Muted));
                infoCard.Controls.Add(ic);
                Theme.Row(root, 114, infoCard);
                Theme.Row(root, 14);

                var footer = Theme.Grid(4);
                Theme.FillColumn(footer);
                Theme.FixedColumn(footer, 122);
                Theme.FixedColumn(footer, 10);
                Theme.FixedColumn(footer, 84);

                var tourBtn = Theme.Action("新手使用引导", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Help);
                tourBtn.Click += delegate
                {
                    dlg.DialogResult = DialogResult.Retry;
                    dlg.Close();
                };
                var okBtn = Theme.Action("确定", Theme.ButtonStyle.Primary);
                okBtn.Click += delegate { dlg.Close(); };

                Theme.Row(footer, 34, null, tourBtn, null, okBtn);
                Theme.Row(root, 34, footer);
                dlg.Controls.Add(root);

                if (dlg.ShowDialog(this) == DialogResult.Retry)
                    StartOnboardingTour(false);
            }
        }

        private sealed class BrandLogoPreviewBox : Control
        {
            internal BrandLogoPreviewBox()
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.Workspace;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Workspace);
                float side = Math.Min(Width, Height) - 2f;
                if (side < 8f) return;
                Theme.DrawBrandMark(e.Graphics, new RectangleF((Width - side) * 0.5f, (Height - side) * 0.5f, side, side));
            }
        }

        internal void StartOnboardingTour(bool markCompleteOnExit)
        {
            if (IsDisposed || !Visible) return;
            if (activeOnboardingTour != null && !activeOnboardingTour.IsDisposed)
            {
                try { activeOnboardingTour.Activate(); }
                catch { }
                return;
            }

            var steps = BuildOnboardingSteps();
            activeOnboardingTour = new OnboardingOverlayForm(this, steps, markCompleteOnExit, delegate
            {
                activeOnboardingTour = null;
                if (!IsDisposed)
                {
                    ShowPage(AllDevicesPage);
                    ResetPageScroll(AllDevicesPage);
                }
            });
            activeOnboardingTour.StartTour();
        }

        private void ResetPageScroll(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= pages.Length) return;
            var vp = pages[pageIndex] as Theme.SlimScrollViewport;
            if (vp != null) vp.ResetScroll();
        }

        private Rectangle GetClientRectForControl(Control control)
        {
            if (control == null || control.IsDisposed || !control.Visible || !control.IsHandleCreated)
                return Rectangle.Empty;
            Point topLeft = PointToClient(control.PointToScreen(Point.Empty));
            return new Rectangle(topLeft.X, topLeft.Y, control.Width, control.Height);
        }

        private Rectangle GetClientRectForSpan(Control first, Control last)
        {
            Rectangle r1 = GetClientRectForControl(first);
            Rectangle r2 = GetClientRectForControl(last);
            if (r1.IsEmpty) return r2;
            if (r2.IsEmpty) return r1;
            return Rectangle.Union(r1, r2);
        }

        private System.Collections.Generic.List<OnboardingStep> BuildOnboardingSteps()
        {
            return new System.Collections.Generic.List<OnboardingStep>
            {
                new OnboardingStep
                {
                    Title = "欢迎使用 BoBo远程 · 功能导航栏",
                    Description = "左侧导航栏将全部核心功能分为四大区：「我的设备」直连常用电脑，「远程协助」发码/输码跨网互助，「防窥与显示」保护本地隐私并管理虚拟屏，底部「设置」用于画质与低延迟调优。",
                    Tip = "支持键盘方向键 ← / → 或 Enter 快速翻页，按 Esc 随时跳过。",
                    PaddingPx = 4,
                    Prepare = delegate
                    {
                        ShowPage(AllDevicesPage);
                        ResetPageScroll(AllDevicesPage);
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForControl(navRail); }
                },
                new OnboardingStep
                {
                    Title = "本机状态与一键直连入口",
                    Description = "右上角标签实时展示本机被控与防窥状态。点击「手动连接设备」输入对方 IP 即可高清低延迟串流；点击「允许连接本机」生成 4 位配对码；点「查看本机地址」可一键复制局域网 IP、Tailscale IP 或公网 IPv6。",
                    Tip = "两台电脑登录同一个 Tailscale 账号后，外网也能免配置直连。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(AllDevicesPage);
                        ResetPageScroll(AllDevicesPage);
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForControl(hostCardControl); }
                },
                new OnboardingStep
                {
                    Title = "自动发现同网电脑与收藏秒连",
                    Description = "局域网或 Tailscale 同网设备会自动列在这里，显示在线状态与直连/中继链路。点击任意在线设备即可发起 60~144Hz 硬件编码串流；收藏常用设备后，它还会出现在左侧快捷栏，实现一键直达。",
                    Tip = "如果暂时未发现设备，可点右上角「刷新状态」或直接手动输入地址。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(AllDevicesPage);
                        ResetPageScroll(AllDevicesPage);
                    },
                    ResolveTargetClientRect = delegate
                    {
                        Rectangle listRect = GetClientRectForControl(peerListHost);
                        Rectangle groupRect = GetClientRectForControl(computersGroupBtn);
                        if (!groupRect.IsEmpty && !listRect.IsEmpty)
                            return Rectangle.Union(groupRect, listRect);
                        return listRect;
                    }
                },
                new OnboardingStep
                {
                    Title = "远程协助 · 配对码与 9 位设备码互助",
                    Description = "需要临时帮朋友排查问题或跨网授权连接时切到这里：上方「本设备」可一键生成 120 秒临时 4 位串流配对码；下方「远控伙伴设备」支持高清串流与内置 RustDesk 9 位设备码双通道切换，还可直接传文件。",
                    Tip = "无需提前注册账号，对方发来 9 位设备码或 IP 即可开连。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(AssistPage);
                        SelectAssistMode(0);
                        ResetPageScroll(AssistPage);
                    },
                    ResolveTargetClientRect = delegate
                    {
                        return GetClientRectForControl(assistPartnerCard ?? assistLocalCard);
                    }
                },
                new OnboardingStep
                {
                    Title = "物理屏隐私防窥壁纸与虚拟显示器",
                    Description = "开启防窥后，本机物理显示器会立刻覆盖全屏安全壁纸，而远程画面依然清晰可见。支持勾选「有远控连入时自动防窥」与自定义本地图片；下方还可配置虚拟显示器，自动匹配手机/平板/笔记本分辨率。",
                    Tip = "紧急解除快捷键：Ctrl+Alt+Shift+P（系统锁屏输入密码解锁后不会误重拉）。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(PrivacyPage);
                        ResetPageScroll(PrivacyPage);
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForControl(wallpaperCardControl); }
                },
                new OnboardingStep
                {
                    Title = "低延迟抗丢包一键调优",
                    Description = "针对 Wi-Fi 波动、跨省弱网与高刷画面，点击「外网抗丢包调优」可自动检测显卡编码器并写入 CBR + HRD 硬码率约束 + LTR 参考帧 + Auto-FEC 动态纠错，显著减少花屏与操作延迟。",
                    Tip = "所有参数修改前均会自动备份，点「还原原配置」随时一键恢复。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(SettingsPage);
                        SelectSettingsTab(2);
                        ResetPageScroll(SettingsPage);
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForControl(perfDiagCard); }
                },
                new OnboardingStep
                {
                    Title = "顶部快捷工具栏 · 随时重看引导",
                    Description = "右上角快捷栏整合了四个高频入口：依次是「新手引导 (?)」、「一键开关防窥壁纸」、「本机 IP 速查面板」与「更多工具菜单」，无需切页即可随时操作。",
                    Tip = "引导完成后将不再自动弹出；以后随时点右上角「?」图标都能重新查看本教程！",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(AllDevicesPage);
                        ResetPageScroll(AllDevicesPage);
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForSpan(topGuideBtn, topMenuBtn); }
                }
            };
        }

        private void OpenExternalUrl(string url, string statusText)
        {
            string err;
            if (Engines.TryOpenUrl(url, out err)) ShowSettingsBanner(statusText, Theme.PillTone.Success);
            else ShowSettingsBanner("无法打开浏览器：" + err, Theme.PillTone.Alert);
        }

        private delegate bool ChangeSystemToggle(bool enabled, out string error);

        private CheckBox SystemToggle(string name, bool current, ChangeSystemToggle change)
        {
            var field = BuildToggle(name, current);
            bool restoring = false;
            field.CheckedChanged += delegate
            {
                if (restoring) return;
                string error;
                if (change(field.Checked, out error))
                    ShowSettingsBanner(field.Checked ? (name + "已开启。") : (name + "已关闭。"), Theme.PillTone.Success);
                else
                {
                    restoring = true;
                    field.Checked = !field.Checked;
                    restoring = false;
                    ShowSettingsBanner(error, Theme.PillTone.Alert);
                }
            };
            return field;
        }

        private bool ChangeKeepAwake(bool enabled, out string error)
        {
            if (!WindowsSession.TryKeepAwake(enabled, out error)) return false;
            keepThisComputerAwake = enabled;
            return true;
        }

        private static CheckBox BuildToggle(string name, bool current)
        {
            return new Theme.Switch
            {
                AccessibleName = name,
                Checked = current,
                Text = name,
                Dock = DockStyle.Fill
            };
        }

        private CheckBox PreferenceToggle(string name, bool current, Action<bool> toggle)
        {
            var field = BuildToggle(name, current);
            field.CheckedChanged += delegate { toggle(field.Checked); SavePreferences(); };
            return field;
        }

        private static Label EngineAvailability(bool present)
        {
            return new Theme.Badge(present ? "已就绪" : "未找到", present);
        }

        private static void AddSettingRow(TableLayoutPanel parent, string title, string description, Control action, int actionWidth = 112)
        {
            var row = Theme.Grid(3);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 14);
            Theme.FixedColumn(row, actionWidth);

            var text = Theme.Grid(1);
            Theme.FillColumn(text);
            text.Padding = new Padding(0, 6, 0, 6);
            Theme.Row(text, 22, Theme.Text(title, Theme.TextSize.Label, Theme.Ink));
            Theme.Row(text, 20, Theme.Text(description, Theme.TextSize.Small, Theme.Muted));

            action.Margin = new Padding(0, 10, 0, 10);
            Theme.Row(row, 54, text, null, action);
            Theme.Row(parent, 1, Theme.Rule());
            Theme.Row(parent, 54, row);
        }

        private static string PreferencePath { get { return Path.Combine(RecentStore.DirPath, "preferences.txt"); } }

        private void LoadPreferences()
        {
            useFoundationClient = FoundationClient.IsAvailable;
            preferHevc = true;
            lowLatencyNoVsync = true;
            enableFramePacing = false;
            minimizeToTray = false;
            defaultQuality = 0;

            try
            {
                if (!File.Exists(PreferencePath)) return;
                string[] lines = File.ReadAllLines(PreferencePath);
                if (lines.Length > 0 && lines[0] == "v2")
                {
                    int profile;
                    if (lines.Length > 1 && int.TryParse(lines[1], out profile) && profile >= 0 && profile < QualityOptions.Length)
                        defaultQuality = profile;
                    if (lines.Length > 2) fillLastTarget = lines[2] != "0";
                    showStreamStats = ReadFlag(lines, 3, false);
                    playAudioOnHost = ReadFlag(lines, 4, false);
                    windowedStream = ReadFlag(lines, 5, false);
                    preferHevc = ReadFlag(lines, 6, true);
                    useFoundationClient = FoundationClient.IsAvailable && ReadFlag(lines, 7, true);
                    lowLatencyNoVsync = ReadFlag(lines, 8, true);
                    enableFramePacing = ReadFlag(lines, 9, false);
                    minimizeToTray = ReadFlag(lines, 10, false);
                }
                else
                {
                    if (lines.Length > 1) fillLastTarget = lines[1] != "0";
                    showStreamStats = ReadFlag(lines, 2, false);
                    playAudioOnHost = ReadFlag(lines, 3, false);
                    windowedStream = ReadFlag(lines, 4, false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool ReadFlag(string[] lines, int index, bool fallback)
        {
            if (lines.Length <= index) return fallback;
            return lines[index] == "1";
        }

        private static string WriteFlag(bool value) { return value ? "1" : "0"; }

        private void SavePreferences()
        {
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                File.WriteAllLines(PreferencePath, new[]
                {
                    "v2",
                    defaultQuality.ToString(),
                    WriteFlag(fillLastTarget),
                    WriteFlag(showStreamStats),
                    WriteFlag(playAudioOnHost),
                    WriteFlag(windowedStream),
                    WriteFlag(preferHevc),
                    WriteFlag(useFoundationClient),
                    WriteFlag(lowLatencyNoVsync),
                    WriteFlag(enableFramePacing),
                    WriteFlag(minimizeToTray)
                });
                ShowSettingsBanner("设置已自动保存到本机。", Theme.PillTone.Success);
            }
            catch (IOException) { PreferenceError(); }
            catch (UnauthorizedAccessException) { PreferenceError(); }
        }

        private void PreferenceError()
        {
            ShowSettingsBanner("当前 Windows 用户无法写入配置文件，本次修改仅在窗口打开期间生效。", Theme.PillTone.Warning);
        }

        private void ShowSettingsBanner(string text, Theme.PillTone tone)
        {
            settingsBanner.Set(text, tone);
        }

        private void ClearRecents()
        {
            recent.RustDeskIds.Clear();
            recent.MoonlightHosts.Clear();
            remoteId.Items.Clear();
            streamHost.Items.Clear();
            remoteId.Text = "";
            streamHost.Text = "";
            try
            {
                recent.Save();
                RefreshSavedDevices();
                ShowSettingsBanner("已清空本机最近连接历史。", Theme.PillTone.Success);
            }
            catch (IOException) { PreferenceError(); }
            catch (UnauthorizedAccessException) { PreferenceError(); }
        }
    }
}
