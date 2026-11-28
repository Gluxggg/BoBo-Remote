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
        private readonly Label diagVersionLabel = Theme.Text("正在检查本机 BoBo V6 状态…", Theme.TextSize.Small, Theme.Muted);
        private readonly Label diagDetailsLabel = Theme.Text("", Theme.TextSize.Small, Theme.BodyInk);

        private Button navQuickDevice;
        private int navQuickDeviceRow = -1;
        private TableLayoutPanel navRail;
        private TableLayoutPanel topBarGrid;
        private BorderFramePanel borderFrame;
        private Theme.WorkspaceFrame workspaceFrame;
        private Button topDisconnectBtn;
        private int topDisconnectCol = -1;
        private int topDisconnectGapCol = -1;
        private Button topGuideBtn;
        private Button topPrivacyBtn;
        private Button topStatusBtn;
        private Button topMenuBtn;
        private Button maxRestoreBtn;
        private Control perfDiagCard;
        private Theme.TwoLineText settingsCrossNetText;
        private Button settingsCrossNetBtn;
        private Theme.TwoLineText updateStatusCardText;
        private Button updateActionButton;
        private OnboardingOverlayForm activeOnboardingTour;
        private bool forceOnboardingOnLaunch;
        private bool onboardingScheduled;

        private bool navDevicesExpanded = true;
        private bool navAssistExpanded = true;
        private int navAllDevicesRow = -1;
        private int navStartAssistRow = -1;
        private int navFavoritesRow = -1;

        private Theme.SlimScrollViewport settingsViewport;
        private TableLayoutPanel settingsDesk;
        private Panel settingsTabHost;
        private int settingsTabRow = -1;
        private Control[] settingsTabPanels;
        private Theme.SlidingTabStrip settingsTabs;
        private int activeSettingsTab = 0;

        private int defaultQuality = AutoQualityProfile;
        private bool fillLastTarget = true;
        private bool showStreamStats = false;
        private bool playAudioOnHost = false;
        private bool windowedStream = false;
        private bool preferHevc = true;
        private bool preferCompressedAudio = true;
        private bool useAbsoluteMouse = true;
        private bool minimizeToTray = false;
        private bool keepThisComputerAwake = false;
        private bool autoCheckUpdates = true;
        private bool autoRemotePrivacyOnConnect = false;
        private bool lockHostOnDisconnect = false;
        private bool hideFloatBallInFullscreen = false;
        private bool showFloatBallInWindowed = false;
        private bool sendSystemShortcuts = false;
        private int miniWindowOpacity = 100;

        private Control BuildChrome()
        {
            var outer = new BorderFramePanel();
            borderFrame = outer;
            var frame = Theme.Grid(1);
            Theme.FillColumn(frame);
            frame.BackColor = Theme.ChromeBg;

            Theme.Row(frame, 48, BuildTopBar());

            var body = Theme.Grid(2);
            Theme.FixedColumn(body, 212);
            Theme.FillColumn(body);
            Theme.FillRow(body);
            body.BackColor = Theme.ChromeBg;
            body.Controls.Add(BuildNavigation(), 0, 0);

            var workspace = new Theme.WorkspaceFrame();
            workspaceFrame = workspace;
            pages[AllDevicesPage] = BuildDevicesPage();
            pages[DeviceDetailPage] = BuildDeviceDetailPage();
            pages[AssistPage] = BuildWorkspace();
            pages[FavoritesPage] = BuildFavoritesPage();
            pages[SettingsPage] = BuildSettingsPage();
            pages[PrivacyPage] = null;
            for (int i = 0; i < pages.Length; i++)
            {
                if (pages[i] == null) continue;
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
            var bar = Theme.Grid(15);
            topBarGrid = bar;
            Theme.FixedColumn(bar, 212);
            Theme.FillColumn(bar);
            topDisconnectCol = bar.ColumnStyles.Count;
            Theme.FixedColumn(bar, 0);
            topDisconnectGapCol = bar.ColumnStyles.Count;
            Theme.FixedColumn(bar, 0);
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

            topDisconnectBtn = Theme.Action("退出控制", Theme.ButtonStyle.DangerPill, Theme.Glyph.Close);
            topDisconnectBtn.AccessibleName = "退出当前远控控制";
            topDisconnectBtn.Margin = new Padding(0, 9, 0, 9);
            topDisconnectBtn.Visible = false;
            topDisconnectBtn.Click += delegate { DisconnectAllRemoteSessionsFromUi(); };

            topGuideBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Compass);
            topGuideBtn.AccessibleName = "新手使用引导";
            topGuideBtn.Margin = new Padding(0, 7, 0, 7);
            topGuideBtn.Click += delegate { StartOnboardingTour(false); };

            topPrivacyBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Privacy);
            topPrivacyBtn.AccessibleName = "防窥壁纸快捷开关";
            topPrivacyBtn.Margin = new Padding(0, 7, 0, 7);
            BindPrivacyToggleButton(topPrivacyBtn);

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

            Theme.Row(bar, 48, brand, dragSpace, topDisconnectBtn, null, topGuideBtn, null, topPrivacyBtn, null, topStatusBtn, null, topMenuBtn, sepWrap, minBtn, maxRestoreBtn, closeBtn);
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

            var allDevicesBtn = MakePageButton(AllDevicesPage, Theme.Glyph.BentoGrid, "全部设备");
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
            Theme.Row(rail, 36, startAssistBtn);

            var favoritesBtn = MakePageButton(FavoritesPage, Theme.Glyph.Star, "收藏设备");
            Theme.SetNavIndent(favoritesBtn, 14);
            Theme.SetGlyphTint(favoritesBtn, Theme.Accent);
            navFavoritesRow = rail.RowCount;
            Theme.Row(rail, 36, favoritesBtn);

            Theme.FillRow(rail);

            // Bottom pinned divider + clean Settings entry (matches UU Remote Image 4)
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
            float scale = CurrentDpiScale();
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
            navRail.ResumeLayout(true);
            NotifyOnboardingLayoutChanged();
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

        internal void OpenPrivacySettings()
        {
            ShowPage(SettingsPage);
            SelectSettingsTab(1);
            ResetPageScroll(SettingsPage);
        }

        private void ShowPage(int selected)
        {
            if (selected == PrivacyPage)
            {
                OpenPrivacySettings();
                return;
            }
            if (selected < 0 || selected >= pages.Length) return;
            bool switched = pages[selected] != null && !pages[selected].Visible;
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
                if (vp != null)
                {
                    vp.RefreshLayout();
                    if (switched) vp.AnimateEntrance();
                }
            }
            if (selected == AllDevicesPage || selected == FavoritesPage) RefreshSavedDevices();
        }

        private Theme.SlimScrollViewport CreatePage(int initialHeight, out TableLayoutPanel desk)
        {
            var viewport = new Theme.SlimScrollViewport { SuspendResponsiveLayout = !initialScaleApplied };
            var pageDesk = Theme.Grid(1);
            Theme.FillColumn(pageDesk);
            pageDesk.Dock = DockStyle.None;
            pageDesk.Height = initialHeight;
            pageDesk.Padding = new Padding(32, 26, 32, 28);
            pageDesk.BackColor = Theme.Workspace;
            viewport.ContentWidthChanged += delegate(int contentW)
            {
                if (!initialScaleApplied) return;
                float scale = CurrentDpiScale();
                float logicalW = contentW / Math.Max(1f, scale);
                int hPadLogical = logicalW < 720f ? 22 : (logicalW < 1100f ? 30 : 38);
                int hPad = (int)Math.Round(hPadLogical * scale);
                int topPad = (int)Math.Round(26 * scale);
                int botPad = (int)Math.Round(28 * scale);
                Padding next = new Padding(hPad, topPad, hPad, botPad);
                if (pageDesk.Padding != next)
                    pageDesk.Padding = next;
            };
            viewport.Controls.Add(pageDesk);
            desk = pageDesk;
            return viewport;
        }

        private static void AddPageHeader(TableLayoutPanel grid, string title, string subtitle, Control trailing = null, int actionWidth = 122)
        {
            bool hasSub = !string.IsNullOrEmpty(subtitle);
            int headerH = hasSub ? 64 : 44;
            var titles = Theme.Grid(1);
            Theme.FillColumn(titles);
            Theme.Row(titles, 42, Theme.Text(title, Theme.TextSize.Title, Theme.Ink));
            if (hasSub) Theme.Row(titles, 22, Theme.Text(subtitle, Theme.TextSize.Small, Theme.Muted));

            if (trailing == null)
            {
                Theme.Row(grid, headerH, titles);
                return;
            }
            var heading = Theme.Grid(3);
            Theme.FillColumn(heading);
            Theme.FixedColumn(heading, 12);
            Theme.FixedColumn(heading, actionWidth);
            int vPad = hasSub ? 14 : 4;
            trailing.Margin = new Padding(0, vPad, 0, vPad);
            Theme.Row(heading, headerH, titles, null, trailing);
            Theme.Row(grid, headerH, heading);
        }

        // ==================== SETTINGS PAGE (UU EXACT SINGLE-COLUMN STYLE) ====================

        private Control BuildSettingsPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(760, out desk);
            settingsViewport = viewport;
            settingsDesk = desk;

            AddPageHeader(desk, "设置", null);
            Theme.Row(desk, 12);

            settingsTabs = new Theme.SlidingTabStrip(
                new Theme.TabEntry("常规设置", Theme.Glyph.Sliders),
                new Theme.TabEntry("防窥与显示", Theme.Glyph.Privacy),
                new Theme.TabEntry("画面与操控", Theme.Glyph.QualityHd),
                new Theme.TabEntry("网络与服务", Theme.Glyph.Globe));
            settingsTabs.SelectedIndexChanged += delegate
            {
                SelectSettingsTab(settingsTabs.SelectedIndex);
            };
            Theme.Row(desk, 40, settingsTabs);
            Theme.Row(desk, 16);

            settingsTabHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Workspace };
            settingsTabPanels = new Control[]
            {
                BuildGeneralSettingsTab(),
                BuildPrivacySettingsTab(viewport),
                BuildStreamSettingsTab(),
                BuildNetworkAndServiceTab()
            };

            for (int i = 0; i < settingsTabPanels.Length; i++)
            {
                settingsTabPanels[i].Dock = DockStyle.Fill;
                settingsTabPanels[i].Visible = false;
                settingsTabHost.Controls.Add(settingsTabPanels[i]);
            }

            settingsTabRow = desk.RowCount;
            Theme.Row(desk, 460, settingsTabHost);
            Theme.Row(desk, 16);
            Theme.Row(desk, 42, settingsBanner);

            SelectSettingsTab(0);
            return viewport;
        }

        private void SelectSettingsTab(int selected)
        {
            if (settingsTabPanels == null || selected < 0 || selected >= settingsTabPanels.Length) return;
            bool tabChanged = activeSettingsTab != selected;
            activeSettingsTab = selected;
            for (int i = 0; i < settingsTabPanels.Length; i++)
            {
                bool active = i == selected;
                settingsTabPanels[i].Visible = active;
            }
            if (settingsTabs != null && settingsTabs.SelectedIndex != selected)
                settingsTabs.SelectTab(selected, true);
            settingsTabPanels[selected].BringToFront();
            RecalculateSettingsDeskHeight();
            if (tabChanged && settingsViewport != null && settingsViewport.Visible)
                settingsViewport.AnimateEntrance();
        }

        private void RecalculateSettingsDeskHeight()
        {
            if (settingsDesk == null || settingsTabPanels == null || settingsTabRow < 0) return;
            int tabH = SumRows(settingsTabPanels[activeSettingsTab]);
            if (tabH < 200) tabH = DevicePixels(420);
            settingsDesk.RowStyles[settingsTabRow].Height = tabH;
            settingsDesk.Height = SumRows(settingsDesk);
            settingsDesk.PerformLayout();
            if (settingsViewport != null) settingsViewport.RefreshLayout();
            NotifyOnboardingLayoutChanged();
        }

        private Control BuildGeneralSettingsTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            Theme.Row(page, 22, Theme.Text("常用", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(page, 8);

            var basicCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var basicList = Theme.Grid(1);
            Theme.FillColumn(basicList);

            AddGroupedSettingRow(basicList, false, Theme.Glyph.Power, "开机自动启动",
                "登录后自动在后台待命",
                SystemToggle("开机自动启动", WindowsSession.StartsAtSignIn(), WindowsSession.TrySetStartAtSignIn));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.Sun, "防止电脑休眠",
                "保持被控端屏幕与网络在线",
                SystemToggle("防止电脑休眠", false, ChangeKeepAwake));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.Tray, "最小化到托盘",
                "关闭窗口时转入系统托盘",
                PreferenceToggle("最小化到托盘", minimizeToTray, value => minimizeToTray = value));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.History, "自动填入上次设备",
                "启动时回填最近连接目标",
                PreferenceToggle("启动时填入上次设备", fillLastTarget, value => fillLastTarget = value));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.CloudDownload, "自动检查更新",
                "静默检查 GitHub 最新版本",
                PreferenceToggle("自动检查更新", autoCheckUpdates, value => autoCheckUpdates = value));

            var defaultProfile = Theme.Choice("默认串流画质", false);
            settingsDefaultProfileCombo = defaultProfile;
            defaultProfile.DropDownWidth = 340;
            foreach (var item in quality.Items) defaultProfile.Items.Add(item);
            defaultProfile.SelectedIndex = defaultQuality;
            defaultProfile.SelectedIndexChanged += delegate
            {
                defaultQuality = defaultProfile.SelectedIndex;
                quality.SelectedIndex = defaultQuality;
                SavePreferences();
            };
            AddGroupedSettingRow(basicList, true, Theme.Glyph.QualityHd, "默认串流画质",
                "发起桌面串流时的默认画质档位",
                Theme.WrapInput(defaultProfile), 372);

            settingsHostDisconnectActionCombo = CreateHostDisconnectActionCombo();
            AddGroupedSettingRow(basicList, true, Theme.Glyph.ScreenLock, "被控断开后动作",
                "远控结束时回到桌面或锁屏",
                Theme.WrapInput(settingsHostDisconnectActionCombo), 160);

            basicCard.Controls.Add(basicList);
            Theme.Row(page, SumRows(basicList) + basicCard.Padding.Vertical, basicCard);
            Theme.Row(page, 16);

            Theme.Row(page, 22, Theme.Text("版本与引导", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(page, 8);

            var miscCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var miscList = Theme.Grid(1);
            Theme.FillColumn(miscList);

            updateActionButton = Theme.Action("检查更新", Theme.ButtonStyle.AccentSoft, Theme.Glyph.CloudDownload);
            updateActionButton.Click += async delegate { await HandleUpdateButtonClickAsync(); };
            updateStatusCardText = AddGroupedSettingRowWithDesc(miscList, false, Theme.Glyph.Layers,
                "软件版本 (v" + AppUpdater.CurrentVersion + ")",
                "支持在线校验升级与增量覆盖",
                updateActionButton, 146);

            var replayGuide = Theme.Action("重新查看", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            replayGuide.Click += delegate { StartOnboardingTour(false); };
            AddGroupedSettingRow(miscList, true, Theme.Glyph.Compass, "新手引导",
                "分步熟悉设备连接与防窥保护",
                replayGuide, 128);

            var clear = Theme.Action("清空记录", Theme.ButtonStyle.Secondary);
            clear.Click += delegate { ClearRecents(); };
            AddGroupedSettingRow(miscList, true, Theme.Glyph.Trash, "清除连接历史",
                "清空最近地址与设备码记录",
                clear, 114);

            miscCard.Controls.Add(miscList);
            Theme.Row(page, SumRows(miscList) + miscCard.Padding.Vertical, miscCard);
            return page;
        }

        private Control BuildStreamSettingsTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            Theme.Row(page, 22, Theme.Text("画面与音频", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(page, 8);

            var mediaCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var mediaList = Theme.Grid(1);
            Theme.FillColumn(mediaList);

            AddGroupedSettingRow(mediaList, false, Theme.Glyph.Cpu, "优先使用 HEVC (H.265)",
                "高清场景节省约 35% 带宽",
                PreferenceToggle("优先使用 HEVC 编码", preferHevc, value => { preferHevc = value; UpdateStreamHint(); }));

            AddGroupedSettingRow(mediaList, true, Theme.Glyph.Volume, "高效 AAC-LC 音频编码",
                "96 kbps 压缩传输 · 不支持时回落 ADPCM",
                PreferenceToggle("高效 AAC 音频", preferCompressedAudio, value => preferCompressedAudio = value));

            AddGroupedSettingRow(mediaList, true, Theme.Glyph.Speaker, "在被控端播放声音",
                "关闭时声音仅传至主控端",
                PreferenceToggle("在被控端同时播放声音", playAudioOnHost, value => playAudioOnHost = value));

            AddGroupedSettingRow(mediaList, true, Theme.Glyph.Activity, "实时性能浮层 (OSD)",
                "Ctrl+Alt+Shift+S 切换显示",
                PreferenceToggle("串流统计", showStreamStats, value => showStreamStats = value));

            mediaCard.Controls.Add(mediaList);
            Theme.Row(page, SumRows(mediaList) + mediaCard.Padding.Vertical, mediaCard);
            Theme.Row(page, 16);

            Theme.Row(page, 22, Theme.Text("操控与隐私", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(page, 8);

            var controlCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var controlList = Theme.Grid(1);
            Theme.FillColumn(controlList);

            AddGroupedSettingRow(controlList, false, Theme.Glyph.Mouse, "自由出框鼠标（绝对坐标）",
                "精准点击 · 3D 游戏可切锁鼠模式",
                PreferenceToggle("绝对坐标鼠标", useAbsoluteMouse, value => useAbsoluteMouse = value));

            AddGroupedSettingRow(controlList, true, Theme.Glyph.Window, "窗口模式打开串流",
                "关闭时默认无边框全屏",
                PreferenceToggle("使用窗口模式", windowedStream, value => windowedStream = value));

            AddGroupedSettingRow(controlList, true, Theme.Glyph.Privacy, "连接后自动开启防窥",
                "接通瞬间自动遮挡被控物理屏",
                PreferenceToggle("自动开启被控端防窥", autoRemotePrivacyOnConnect, value => autoRemotePrivacyOnConnect = value));

            AddGroupedSettingRow(controlList, true, Theme.Glyph.FloatBall, "全屏隐藏悬浮球",
                "Ctrl+Alt+G 显示或隐藏",
                PreferenceToggle("全屏隐藏悬浮球", hideFloatBallInFullscreen, value => hideFloatBallInFullscreen = value));

            controlCard.Controls.Add(controlList);
            Theme.Row(page, SumRows(controlList) + controlCard.Padding.Vertical, controlCard);
            return page;
        }

        private Control BuildNetworkAndServiceTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            var hostCard = new Theme.Card { Padding = new Padding(20, 16, 20, 16) };
            perfDiagCard = hostCard;
            var h = Theme.Grid(1);
            Theme.FillColumn(h);

            diagStatusBadge.AlignRight = true;
            var head = Theme.Grid(3);
            Theme.FillColumn(head);
            Theme.FixedColumn(head, 12);
            Theme.FixedColumn(head, 206);
            Theme.Row(head, 28, Theme.Text("本机被控诊断", Theme.TextSize.Section, Theme.Ink), null, diagStatusBadge);
            Theme.Row(h, 28, head);
            Theme.Row(h, 4);
            Theme.Row(h, 22, diagVersionLabel);
            Theme.Row(h, 6);
            diagDetailsLabel.TextAlign = ContentAlignment.TopLeft;
            diagDetailsLabel.AutoEllipsis = false;
            Theme.Row(h, 52, diagDetailsLabel);
            Theme.Row(h, 10);

            var btnBar = Theme.Grid(4);
            Theme.FixedColumn(btnBar, 114);
            Theme.FixedColumn(btnBar, 10);
            Theme.FixedColumn(btnBar, 128);
            Theme.FillColumn(btnBar);
            var refreshDiag = Theme.Action("刷新诊断", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            refreshDiag.Click += async delegate
            {
                refreshDiag.Enabled = false;
                try { await RefreshHostDiagnosticsAsync(); }
                finally { if (!IsDisposed) refreshDiag.Enabled = true; }
            };
            var openGuide = Theme.Action("查看使用指南", Theme.ButtonStyle.Secondary, Theme.Glyph.Help);
            openGuide.Click += delegate { OpenGuide("README.md"); };
            Theme.Row(btnBar, 36, refreshDiag, null, openGuide, null);
            Theme.Row(h, 36, btnBar);
            hostCard.Controls.Add(h);

            Theme.Row(page, 192, hostCard);
            Theme.Row(page, 16);

            Theme.Row(page, 22, Theme.Text("跨网与服务", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(page, 8);

            var card = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var list = Theme.Grid(1);
            Theme.FillColumn(list);

            settingsCrossNetBtn = Theme.Action("一键登录", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Globe);
            settingsCrossNetBtn.Click += async delegate { await HandleCrossNetCardClickAsync(); };
            settingsCrossNetText = AddGroupedSettingRowWithDesc(list, false, Theme.Glyph.Globe,
                "内置跨网账号 (Tailscale)",
                "免装虚拟网卡 · 双端同账号即可异地直连",
                settingsCrossNetBtn, 120);
            SyncSettingsCrossNetRow(BoBoEmbeddedTailnet.Default.LatestSnapshot);

            AddGroupedSettingRow(list, true, Theme.Glyph.Cpu, "BoBoCore 原生内核",
                "DXGI / MF / D3D11 / WASAPI 单文件全内置",
                BuildEngineActionRow(BoBoCoreInterop.IsLoaded, "运行自检", delegate { RunNativeCoreSelfTestDialog(); }), 186);

            AddGroupedSettingRow(list, true, Theme.Glyph.Shield, "允许本机被控 (:49810)",
                "TLS 1.2 + AES-128-GCM 实时加密传输",
                PreferenceToggle("允许本机被控", allowIncomingControl, delegate(bool value)
                {
                    allowIncomingControl = value;
                    if (boboHostServer != null) boboHostServer.AllowIncomingControl = value;
                    ApplyHostSnapshotToUi();
                }));

            AddGroupedSettingRow(list, true, Theme.Glyph.Key, "本机设备码：" + BoBoIdentityStore.Default.FormattedDeviceCode,
                "管理本机已授权的远控设备名单",
                BuildEngineActionRow(true, "管理授权", delegate { ShowAuthorizedPeersDialog(); }), 186);

            AddGroupedSettingRow(list, true, Theme.Glyph.Terminal, "外部 Tailscale CLI",
                "可选命令行组网补充（优先使用内置通道）",
                EngineAvailability(Engines.TailscalePresent), 98);

            bool svcInstalled, svcRunning, svcSecure;
            string svcExe, svcVer, svcUpReason;
            BoBoSystemService.InspectInstalledService(out svcInstalled, out svcRunning, out svcSecure, out svcExe, out svcVer);
            bool svcNeedsUpgrade = svcRunning && BoBoSystemService.NeedsServiceUpgradeOrMigration(out svcUpReason);
            string initBadgeText = !svcInstalled ? "未配置" : (svcNeedsUpgrade ? "需升级" : (svcRunning ? "已就绪" : "未启动"));
            Theme.PillTone initBadgeTone = (!svcInstalled || !svcRunning)
                ? Theme.PillTone.Neutral
                : (svcNeedsUpgrade ? Theme.PillTone.Warning : Theme.PillTone.Success);
            string initBtnText = !svcInstalled ? "安装/修复" : (svcNeedsUpgrade ? "升级/迁移" : (svcRunning ? "重新配置" : "安装/修复"));
            var lockSvcBadge = new Theme.Badge(initBadgeText, initBadgeTone);
            var lockSvcBtn = Theme.Action(initBtnText, Theme.ButtonStyle.Secondary);
            var lockSvcBox = Theme.Grid(3);
            Theme.FixedColumn(lockSvcBox, 78);
            Theme.FixedColumn(lockSvcBox, 8);
            Theme.FixedColumn(lockSvcBox, 98);
            Theme.Row(lockSvcBox, 34, lockSvcBadge, null, lockSvcBtn);
            Theme.TwoLineText lockSvcText = AddGroupedSettingRowWithDesc(
                list, true, Theme.Glyph.Server,
                "锁屏远控与穿透服务（SYSTEM / UAC）",
                BoBoSystemService.GetServiceStatusLabel(),
                lockSvcBox, 184);
            lockSvcBtn.Click += delegate
            {
                lockSvcBtn.Enabled = false;
                lockSvcBtn.Text = "配置中…";
                System.Threading.Tasks.Task.Run(delegate
                {
                    string svcMsg;
                    bool ok = BoBoSystemService.TryInstallOrRepairElevated(out svcMsg);
                    if (IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            bool nowInst, nowRun, nowSec;
                            string nowExe, nowVer, nowUpReason;
                            BoBoSystemService.InspectInstalledService(out nowInst, out nowRun, out nowSec, out nowExe, out nowVer);
                            bool nowNeedsUp = nowRun && BoBoSystemService.NeedsServiceUpgradeOrMigration(out nowUpReason);
                            string nowBadgeText = !nowInst ? "未配置" : (nowNeedsUp ? "需升级" : (nowRun ? "已就绪" : "未启动"));
                            Theme.PillTone nowTone = (!nowInst || !nowRun)
                                ? Theme.PillTone.Neutral
                                : (nowNeedsUp ? Theme.PillTone.Warning : Theme.PillTone.Success);
                            lockSvcBadge.UpdateStatus(nowBadgeText, nowTone);
                            lockSvcBtn.Text = !nowInst ? "安装/修复" : (nowNeedsUp ? "升级/迁移" : (nowRun ? "重新配置" : "安装/修复"));
                            lockSvcBtn.Enabled = true;
                            if (lockSvcText != null) lockSvcText.SubtitleText = BoBoSystemService.GetServiceStatusLabel();
                            ShowSettingsBanner(svcMsg, ok ? Theme.PillTone.Success : Theme.PillTone.Alert);
                        });
                    }
                    catch { }
                });
            };

            card.Controls.Add(list);
            Theme.Row(page, SumRows(list) + card.Padding.Vertical, card);
            return page;
        }

        private static void AddGroupedSettingRow(TableLayoutPanel list, bool addTopRule, Theme.Glyph glyph, string title, string description, Control action, int actionWidth = 108)
        {
            AddGroupedSettingRowWithDesc(list, addTopRule, glyph, title, description, action, actionWidth);
        }

        private static Theme.TwoLineText AddGroupedSettingRowWithDesc(TableLayoutPanel list, bool addTopRule, Theme.Glyph glyph, string title, string description, Control action, int actionWidth = 108)
        {
            if (addTopRule) Theme.Row(list, 1, Theme.Rule());

            var row = Theme.Grid(5);
            Theme.FixedColumn(row, 32);
            Theme.FixedColumn(row, 12);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 12);
            Theme.FixedColumn(row, actionWidth);

            var icon = new Theme.IconTile(glyph, true, Theme.SurfaceSubtle, Theme.BodyInk, 8f);
            icon.Margin = new Padding(0, 5, 0, 5);
            var textBlock = new Theme.TwoLineText(title, description, Theme.TextSize.Label, Theme.Ink, Theme.Muted);

            action.Margin = new Padding(0, 4, 0, 4);
            Theme.Row(row, 42, icon, null, textBlock, null, action);

            var rowWrap = new Theme.SettingRow(icon, textBlock) { Padding = new Padding(18, 10, 18, 10) };
            rowWrap.Controls.Add(row);
            rowWrap.HookHoverChildren(rowWrap);
            Theme.Row(list, 62, rowWrap);
            return textBlock;
        }

        private void RunNativeCoreSelfTestDialog()
        {
            string report;
            bool ok = BoBoCoreInterop.RunNativeSelfTest(out report);
            ShowSettingsBanner(ok ? "BoBo V6 原生内核自检全部通过！" : "BoBo V6 原生内核自检发现异常。",
                ok ? Theme.PillTone.Success : Theme.PillTone.Alert);
            MessageBox.Show(this,
                string.IsNullOrEmpty(report) ? (ok ? "BoBoCore 自检通过。" : "无法加载 BoBoCore。") : report,
                "BoBo V6 全内置原生内核自检",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static Control BuildEngineActionRow(bool ready, string buttonText, EventHandler onClick)
        {
            var box = Theme.Grid(3);
            Theme.FixedColumn(box, 78);
            Theme.FixedColumn(box, 8);
            Theme.FixedColumn(box, 98);
            var btn = Theme.Action(buttonText, Theme.ButtonStyle.Secondary);
            btn.Enabled = ready;
            btn.Click += onClick;
            Theme.Row(box, 34, EngineAvailability(ready), null, btn);
            return box;
        }

        /// <summary>单列设置卡片行，左侧为图标和说明，右侧为操作控件。</summary>
        private static void AddStandaloneSettingCard(TableLayoutPanel parent, Theme.Glyph glyph, string title, string description, Control action, int actionWidth = 108)
        {
            AddStandaloneSettingCardWithDesc(parent, glyph, title, description, action, actionWidth);
        }

        private static Theme.TwoLineText AddStandaloneSettingCardWithDesc(TableLayoutPanel parent, Theme.Glyph glyph, string title, string description, Control action, int actionWidth = 108)
        {
            bool hasDesc = !string.IsNullOrEmpty(description);
            int cardH = hasDesc ? 76 : 62;

            var card = new Theme.Card { Padding = new Padding(20, 12, 20, 12) };
            var row = Theme.Grid(5);
            Theme.FixedColumn(row, 32);
            Theme.FixedColumn(row, 12);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 14);
            Theme.FixedColumn(row, actionWidth);

            var icon = new Theme.IconTile(glyph, true, Theme.SurfaceSubtle, Theme.BodyInk, 8f);
            var textBlock = new Theme.TwoLineText(title, description, Theme.TextSize.Label, Theme.Ink, Theme.Muted);

            int innerH = cardH - 24;
            int actionPad = Math.Max(0, (innerH - 36) / 2);
            action.Margin = new Padding(0, actionPad, 0, actionPad);
            Theme.Row(row, innerH, icon, null, textBlock, null, action);
            card.Controls.Add(row);

            Theme.Row(parent, cardH, card);
            Theme.Row(parent, 10);
            return textBlock;
        }

        private void UpdateDiagnosticsUi()
        {
            bool serverUp = boboHostServer != null && boboHostServer.IsRunning;
            bool allowing = serverUp && boboHostServer.AllowIncomingControl;
            int actualPort = serverUp ? boboHostServer.ListeningPort : BoBoProtocol.DefaultTcpPort;
            int peerCount = BoBoIdentityStore.Default.ListAuthorizedPeers().Count;
            if (allowing)
            {
                string portLabel = boboHostServer.IsUsingFallbackPort
                    ? ("V6 运行中 · 备用端口 " + actualPort)
                    : ("V6 内置运行中 · 端口 " + actualPort);
                diagStatusBadge.UpdateStatus(portLabel, Theme.PillTone.Success);
            }
            else if (serverUp)
                diagStatusBadge.UpdateStatus("已暂停入站被控", Theme.PillTone.Warning);
            else
                diagStatusBadge.UpdateStatus("被控服务未启动", Theme.PillTone.Alert);

            diagVersionLabel.Text = "全内置 BoBoCore v" + AppUpdater.CurrentVersion + " · 本机设备码：" + BoBoIdentityStore.Default.FormattedDeviceCode;
            diagDetailsLabel.Text = "已授权设备 " + peerCount + " 台 · 控制口 TCP " + actualPort + " (TLS 1.2)"
                + (serverUp && boboHostServer.IsUsingFallbackPort ? "（49810 被旧进程占用，已自动切换）" : "") + "\r\n"
                + "媒体流 UDP+AES-128-GCM+FEC/NACK · 局域网发现 UDP " + BoBoProtocol.DiscoveryUdpPort;
        }

        private void SyncSettingsCrossNetRow(EmbeddedTailnetSnapshot snap)
        {
            if (settingsCrossNetText == null || settingsCrossNetBtn == null) return;
            string st = snap != null ? (snap.BackendState ?? "Stopped") : "Stopped";
            bool running = string.Equals(st, "Running", StringComparison.OrdinalIgnoreCase);
            bool waiting = !running && snap != null && !string.IsNullOrEmpty(snap.AuthUrl);
            if (running)
            {
                string acct = !string.IsNullOrEmpty(snap.DisplayName) ? snap.DisplayName
                    : (!string.IsNullOrEmpty(snap.LoginName) ? snap.LoginName : "已登录");
                settingsCrossNetText.TitleText = "跨网账号 · " + acct;
                settingsCrossNetText.SubtitleText = string.IsNullOrEmpty(snap.SelfIp4)
                    ? "跨网通道在线 · 已自动对接本地被控端口"
                    : ("跨网 IP：" + snap.SelfIp4 + " · 发现 " + (snap.Peers != null ? snap.Peers.Count : 0) + " 台设备");
                settingsCrossNetBtn.Text = "退出账号";
            }
            else if (waiting)
            {
                settingsCrossNetText.TitleText = "跨网账号 · 等待浏览器授权";
                settingsCrossNetText.SubtitleText = "点击右侧按钮继续在浏览器完成 Tailscale 登录";
                settingsCrossNetBtn.Text = "打开登录页";
            }
            else
            {
                settingsCrossNetText.TitleText = "内置跨网账号 (Tailscale)";
                settingsCrossNetText.SubtitleText = "免装虚拟网卡 · 双端同账号即可异地直连";
                settingsCrossNetBtn.Text = "一键登录";
            }
        }

        // ==================== TOP BAR POPOVERS (UU IMAGE 9 & 10 STYLE) ====================

        private void ShowTopMenuPopover()
        {
            var menu = CreateCleanPopupMenu();
            if (pendingUpdate != null && pendingUpdate.HasUpdate)
            {
                AddPopupItem(menu, "发现新版本 " + pendingUpdate.ReleaseTag + "（立即更新）", delegate
                {
                    ShowPage(SettingsPage);
                    SelectSettingsTab(0);
                    var _ = HandleUpdateButtonClickAsync();
                }, Theme.Glyph.CloudDownload);
                menu.Items.Add(new ToolStripSeparator());
            }
            AddPopupItem(menu, "刷新全部设备与本机状态", delegate { var _ = RefreshDashboardAsync(); }, Theme.Glyph.Refresh);
            AddPopupItem(menu, "切换防窥屏壁纸 (Ctrl+Alt+Shift+P)", delegate { TogglePrivacyCurtain(false); }, Theme.Glyph.Privacy);
            AddPopupItem(menu, "新手引导使用教程", delegate { StartOnboardingTour(false); }, Theme.Glyph.Compass);
            AddPopupItem(menu, "检查软件更新（当前 v" + AppUpdater.CurrentVersion + "）", delegate
            {
                ShowPage(SettingsPage);
                SelectSettingsTab(0);
                var _ = CheckForUpdatesAsync(false);
            }, Theme.Glyph.CloudDownload);
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "管理已授权设备（本机码 " + BoBoIdentityStore.Default.FormattedDeviceCode + "）", delegate { ShowAuthorizedPeersDialog(); }, Theme.Glyph.Shield);
            AddPopupItem(menu, "运行 V6 全内置内核自检", delegate { RunNativeCoreSelfTestDialog(); }, Theme.Glyph.Activity);
            AddPopupItem(menu, "BoBo远程使用指南", delegate { OpenGuide("README.md"); }, Theme.Glyph.Help);
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "断开当前远控会话", delegate { DisconnectAllRemoteSessionsFromUi(); }, Theme.Glyph.Power);
            AddPopupItem(menu, "关于 BoBo远程", delegate { ShowAboutDialog(); }, Theme.Glyph.Info);
            AddPopupItem(menu, "退出 BoBo远程（断开远控）", delegate { forceExit = true; Close(); }, Theme.Glyph.Exit, true);

            int menuW = Math.Max(DevicePixels(236), menu.PreferredSize.Width);
            Point pt = topMenuBtn.PointToScreen(new Point(topMenuBtn.Width - menuW, topMenuBtn.Height + DevicePixels(6)));
            menu.Show(pt);
        }

        private void ShowHostStatusPopover()
        {
            var menu = CreateCleanPopupMenu();
            bool serverUp = boboHostServer != null && boboHostServer.IsRunning;
            string hostState = (serverUp && boboHostServer.AllowIncomingControl)
                ? ("被控就绪 · 端口 " + boboHostServer.ListeningPort)
                : (serverUp ? "已暂停被控" : "被控服务未启动");
            var header = new ToolStripMenuItem("本机：" + Environment.MachineName + "  (" + hostState + ")") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            string code = BoBoIdentityStore.Default.FormattedDeviceCode;
            string lan = string.IsNullOrEmpty(hostSnapshot.LanIpv4) ? "未连接" : hostSnapshot.LanIpv4;
            string ts = string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4) ? "未登录" : hostSnapshot.TailscaleIpv4;
            string v6 = string.IsNullOrEmpty(hostSnapshot.PublicIpv6) ? "无公网 IPv6" : ShortenIpv6(hostSnapshot.PublicIpv6);

            AddPopupItem(menu, "复制本机 BoBo 设备码：" + code, delegate { CopyHostAddress("本机 BoBo 设备码", code); }, Theme.Glyph.Copy);
            AddPopupItem(menu, "复制局域网 IP：" + lan, delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); }, Theme.Glyph.Copy);
            AddPopupItem(menu, "复制 Tailscale IP：" + ts, delegate { CopyHostAddress("Tailscale IP", hostSnapshot.TailscaleIpv4); }, Theme.Glyph.Copy);
            AddPopupItem(menu, "复制公网 IPv6：" + v6, delegate { CopyHostAddress("公网 IPv6", hostSnapshot.PublicIpv6); }, Theme.Glyph.Copy);
            menu.Items.Add(new ToolStripSeparator());

            EmbeddedTailnetSnapshot emb = BoBoEmbeddedTailnet.Default.LatestSnapshot;
            bool embRunning = emb != null && string.Equals(emb.BackendState, "Running", StringComparison.OrdinalIgnoreCase);
            if (embRunning)
            {
                AddPopupItem(menu, "退出内置跨网账号（" + (string.IsNullOrEmpty(emb.DisplayName) ? emb.SelfIp4 : emb.DisplayName) + "）",
                    delegate { var _ = CancelOrLogoutEmbeddedTailnetAsync(); }, Theme.Glyph.Exit);
            }
            else
            {
                AddPopupItem(menu, "登录内置跨网账号（Tailscale）",
                    delegate { var _ = HandleCrossNetCardClickAsync(); }, Theme.Glyph.Globe);
            }
            AddPopupItem(menu, "前往「开始协助」生成 4 位配对码", delegate { ShowPage(AssistPage); }, Theme.Glyph.Key);

            int menuW = Math.Max(DevicePixels(270), menu.PreferredSize.Width);
            Point pt = topStatusBtn.PointToScreen(new Point(topStatusBtn.Width - menuW, topStatusBtn.Height + DevicePixels(6)));
            menu.Show(pt);
        }

        private static readonly Font PopupMenuFont = Theme.GetFont(Theme.TextSize.Label);

        private ContextMenuStrip CreateCleanPopupMenu()
        {
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = Theme.Paper,
                ForeColor = Theme.Ink,
                Font = PopupMenuFont,
                Padding = new Padding(4, 6, 4, 6),
                Renderer = new CleanMenuRenderer()
            };
            menu.Closed += delegate
            {
                if (IsDisposed || !IsHandleCreated)
                {
                    try { menu.Dispose(); } catch { }
                    return;
                }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        try { menu.Dispose(); } catch { }
                    }));
                }
                catch { }
            };
            return menu;
        }

        private static void AddPopupItem(ContextMenuStrip menu, string text, EventHandler click,
            Theme.Glyph glyph = Theme.Glyph.None, bool danger = false)
        {
            int leftPad = (int)Math.Round(30 * Theme.GetDpiScale());
            int vertPad = (int)Math.Round(6 * Theme.GetDpiScale());
            int rightPad = (int)Math.Round(14 * Theme.GetDpiScale());
            var item = new ToolStripMenuItem(text)
            {
                Padding = new Padding(leftPad, vertPad, rightPad, vertPad),
                ForeColor = danger ? Theme.Alert : Theme.Ink,
                Tag = glyph
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

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                Color rowBg = (e.Item.Selected && e.Item.Enabled) ? Theme.SurfaceSubtle : Theme.Paper;
                Color textColor = !e.Item.Enabled ? Theme.Muted : e.Item.ForeColor;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                TextRenderer.DrawText(e.Graphics, e.Item.Text, e.TextFont, e.TextRectangle, textColor, rowBg,
                    e.TextFormat | TextFormatFlags.NoPrefix);

                if (e.Item.Tag is Theme.Glyph)
                {
                    var glyph = (Theme.Glyph)e.Item.Tag;
                    if (glyph != Theme.Glyph.None)
                    {
                        float scale = Math.Max(1f, e.Graphics.DpiX / 96f);
                        float icoSize = Math.Max(13f, 14.5f * scale);
                        float ix = Math.Max(10f * scale, e.TextRectangle.Left - icoSize - 8f * scale);
                        float iy = (e.Item.Height - icoSize) * 0.5f;
                        Color iconColor = (e.Item.ForeColor == Theme.Alert)
                            ? Theme.Alert
                            : (e.Item.Selected && e.Item.Enabled ? Theme.Accent : Theme.BodyInk);
                        Theme.DrawGlyph(e.Graphics, glyph, new RectangleF(ix, iy, icoSize, icoSize), iconColor);
                    }
                }
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
                dlg.SuspendLayout();
                dlg.Text = "关于 BoBo远程";
                dlg.AutoScaleMode = AutoScaleMode.Dpi;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = Theme.Workspace;
                dlg.ClientSize = new Size(524, 316);
                dlg.Icon = appIcon ?? Icon;

                var root = Theme.Grid(1);
                Theme.FillColumn(root);
                root.Padding = new Padding(24, 20, 24, 18);
                root.BackColor = Theme.Workspace;

                var hero = Theme.Grid(3);
                Theme.FixedColumn(hero, 58);
                Theme.FixedColumn(hero, 14);
                Theme.FillColumn(hero);

                var logoBox = new BrandLogoPreviewBox();
                var titleStack = Theme.Grid(1);
                Theme.FillColumn(titleStack);
                titleStack.Padding = new Padding(0, 2, 0, 0);
                Theme.Row(titleStack, 40, Theme.Text("BoBo远程 v" + AppUpdater.CurrentVersion, Theme.TextSize.Title, Theme.Ink));
                Theme.Row(titleStack, 22, Theme.Text("低延迟高帧率双端远程桌面 · 隐私防窥便携版", Theme.TextSize.Small, Theme.Muted));
                Theme.Row(hero, 64, logoBox, null, titleStack);
                Theme.Row(root, 64, hero);
                Theme.Row(root, 14);

                var infoCard = new Theme.Card { Padding = new Padding(16, 12, 16, 12) };
                var ic = Theme.Grid(1);
                Theme.FillColumn(ic);
                Theme.Row(ic, 25, Theme.Text("• 全内置核心：BoBo V6 原生单文件内核（DXGI/GDI + MF H.264 + D3D11VA + WASAPI + TLS 1.2）", Theme.TextSize.Small, Theme.BodyInk));
                Theme.Row(ic, 25, Theme.Text("• 安全机制：双端必开 BoBo远程 + 4 位临时配对码 HMAC-SHA256 双向授权（零外置独立内核）", Theme.TextSize.Small, Theme.BodyInk));
                Theme.Row(ic, 25, Theme.Text("• 隐私与互传：WDA_EXCLUDEFROMCAPTURE 毫秒级防窥壁纸 + 内置 SHA-256 极速文件传输", Theme.TextSize.Small, Theme.BodyInk));
                Theme.Row(ic, 25, Theme.Text("• 配置目录：" + RecentStore.DirPath, Theme.TextSize.Small, Theme.Muted));
                infoCard.Controls.Add(ic);
                Theme.Row(root, 130, infoCard);
                Theme.Row(root, 16);

                var footer = Theme.Grid(6);
                Theme.FillColumn(footer);
                Theme.FixedColumn(footer, 116);
                Theme.FixedColumn(footer, 10);
                Theme.FixedColumn(footer, 132);
                Theme.FixedColumn(footer, 10);
                Theme.FixedColumn(footer, 88);

                var checkUpdBtn = Theme.Action("检查更新", Theme.ButtonStyle.Secondary, Theme.Glyph.CloudDownload);
                checkUpdBtn.Click += delegate
                {
                    dlg.DialogResult = DialogResult.Yes;
                    dlg.Close();
                };
                var tourBtn = Theme.Action("新手使用引导", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Compass);
                tourBtn.Click += delegate
                {
                    dlg.DialogResult = DialogResult.Retry;
                    dlg.Close();
                };
                var okBtn = Theme.Action("确定", Theme.ButtonStyle.Primary);
                okBtn.Click += delegate { dlg.Close(); };

                Theme.Row(footer, 36, null, checkUpdBtn, null, tourBtn, null, okBtn);
                Theme.Row(root, 36, footer);
                dlg.Controls.Add(root);

                dlg.AutoScaleDimensions = new SizeF(96f, 96f);
                dlg.ResumeLayout(false);
                dlg.PerformAutoScale();
                dlg.PerformLayout();

                DialogResult res = dlg.ShowDialog(this);
                if (res == DialogResult.Retry)
                    StartOnboardingTour(false);
                else if (res == DialogResult.Yes)
                {
                    ShowPage(SettingsPage);
                    SelectSettingsTab(0);
                    var _ = CheckForUpdatesAsync(false);
                }
            }
        }

        private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool silent)
        {
            if (updateBusy || IsDisposed) return;
            updateBusy = true;
            if (!silent)
            {
                if (updateActionButton != null)
                {
                    updateActionButton.Enabled = false;
                    updateActionButton.Text = "检查中…";
                }
                if (updateStatusCardText != null)
                    updateStatusCardText.SubtitleText = "正在连接 GitHub Releases 检查最新版本…";
                ShowSettingsBanner("正在检查最新版本…", Theme.PillTone.Info);
            }

            UpdateCheckResult res = null;
            try
            {
                res = await AppUpdater.CheckForUpdateAsync();
            }
            catch (Exception ex)
            {
                res = new UpdateCheckResult { CurrentVersion = AppUpdater.CurrentVersion, Error = ex.Message };
            }
            finally
            {
                updateBusy = false;
            }

            if (IsDisposed) return;
            if (res == null || !res.Checked)
            {
                if (updateActionButton != null)
                {
                    updateActionButton.Enabled = true;
                    updateActionButton.Text = "检查更新";
                }
                if (!silent)
                {
                    string err = (res != null && !string.IsNullOrEmpty(res.Error)) ? res.Error : "检查更新失败，请稍后重试。";
                    if (updateStatusCardText != null) updateStatusCardText.SubtitleText = err;
                    ShowSettingsBanner(err, Theme.PillTone.Warning);
                }
                return;
            }

            if (res.HasUpdate)
            {
                pendingUpdate = res;
                if (topMenuBtn != null)
                    Theme.SetIndicatorDot(topMenuBtn, true, Theme.Accent);
                string kindText = res.PackageKind == UpdatePackageKind.LightZip
                    ? "轻量增量包，秒级更新"
                    : (res.PackageKind == UpdatePackageKind.FullSetup ? "完整覆盖包，保留本地配对" : "需前往 Releases 页面下载");
                string desc = "发现新版本 " + res.ReleaseTag + "（" + kindText + "）"
                    + (string.IsNullOrEmpty(res.ReleaseNotesSummary) ? "" : "：" + res.ReleaseNotesSummary);
                if (updateStatusCardText != null) updateStatusCardText.SubtitleText = desc;
                if (updateActionButton != null)
                {
                    updateActionButton.Enabled = true;
                    updateActionButton.Text = res.PackageKind != UpdatePackageKind.None ? ("更新 " + res.ReleaseTag) : "打开发布页";
                }
                string banner = "发现新版本 " + res.ReleaseTag + "（当前 v" + res.CurrentVersion + "），可在右上角菜单或「设置 → 常规」一键自动更新。";
                ShowSettingsBanner(banner, Theme.PillTone.Info);
                if (silent)
                {
                    deviceBanner.Set(banner, Theme.PillTone.Info);
                }
            }
            else
            {
                pendingUpdate = null;
                if (topMenuBtn != null)
                    Theme.SetIndicatorDot(topMenuBtn, false, Theme.Accent);
                if (updateActionButton != null)
                {
                    updateActionButton.Enabled = true;
                    updateActionButton.Text = "检查更新";
                }
                if (!silent)
                {
                    string upToDate = "当前已是最新版本 v" + res.CurrentVersion + "（最新发行版 " + res.ReleaseTag + "）。";
                    if (updateStatusCardText != null) updateStatusCardText.SubtitleText = upToDate;
                    ShowSettingsBanner(upToDate, Theme.PillTone.Success);
                }
            }
        }

        private async System.Threading.Tasks.Task HandleUpdateButtonClickAsync()
        {
            if (updateBusy || IsDisposed) return;
            if (pendingUpdate == null || !pendingUpdate.HasUpdate)
            {
                await CheckForUpdatesAsync(false);
                if (pendingUpdate == null || !pendingUpdate.HasUpdate || IsDisposed) return;
            }
            if (pendingUpdate.PackageKind == UpdatePackageKind.None)
            {
                OpenExternalUrl(pendingUpdate.ReleasePageUrl ?? AppUpdater.ReleasesWebUrl, "已打开 GitHub Releases 下载页面。");
                return;
            }
            await StartDownloadAndInstallUpdateAsync(pendingUpdate);
        }

        private async System.Threading.Tasks.Task StartDownloadAndInstallUpdateAsync(UpdateCheckResult update)
        {
            if (updateBusy || update == null || IsDisposed) return;
            updateBusy = true;
            if (updateCts != null)
            {
                try { updateCts.Cancel(); updateCts.Dispose(); } catch { }
            }
            updateCts = new System.Threading.CancellationTokenSource();

            if (updateActionButton != null)
            {
                updateActionButton.Enabled = false;
                updateActionButton.Text = "下载中 0%";
            }
            if (updateStatusCardText != null)
                updateStatusCardText.SubtitleText = "正在下载 " + update.ReleaseTag + "（将自动校验 SHA-256）…";
            ShowSettingsBanner("正在下载新版本 " + update.ReleaseTag + "…", Theme.PillTone.Info);

            try
            {
                string downloadedPath = await AppUpdater.DownloadAndVerifyAsync(update, delegate(int pct, string msg)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            if (updateActionButton != null)
                                updateActionButton.Text = pct >= 100 ? "正在安装…" : ("下载中 " + pct + "%");
                            if (updateStatusCardText != null && !string.IsNullOrEmpty(msg))
                                updateStatusCardText.SubtitleText = msg;
                        });
                    }
                    catch { }
                }, updateCts.Token);

                if (IsDisposed) return;
                // 更新前先彻底释放监听端口与内置子进程，防止句柄继承锁死 49810
                TeardownRemoteSessionsOnExit();
                string launchErr;
                if (!AppUpdater.TryLaunchDownloadedUpdate(update, downloadedPath, out launchErr))
                {
                    remoteSessionsTornDown = false;
                    EnsureBoBoHostServerStarted();
                    updateBusy = false;
                    if (updateActionButton != null)
                    {
                        updateActionButton.Enabled = true;
                        updateActionButton.Text = "重试更新";
                    }
                    if (updateStatusCardText != null) updateStatusCardText.SubtitleText = launchErr;
                    ShowSettingsBanner(launchErr, Theme.PillTone.Alert);
                    return;
                }

                // 退出当前旧实例，让新版自动覆盖并重新拉起（不触发最小化到托盘）
                ShowSettingsBanner("校验通过，正在重启并完成自动更新…", Theme.PillTone.Success);
                forceExit = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                updateBusy = false;
                if (!IsDisposed && updateActionButton != null)
                {
                    updateActionButton.Enabled = true;
                    updateActionButton.Text = "更新 " + update.ReleaseTag;
                }
            }
            catch (Exception ex)
            {
                updateBusy = false;
                if (IsDisposed) return;
                string err = "自动更新失败：" + ex.Message;
                if (updateActionButton != null)
                {
                    updateActionButton.Enabled = true;
                    updateActionButton.Text = "重试更新";
                }
                if (updateStatusCardText != null) updateStatusCardText.SubtitleText = err;
                ShowSettingsBanner(err, Theme.PillTone.Alert);
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
            if (control == null || control.IsDisposed || !control.Visible)
                return Rectangle.Empty;
            if (!control.IsHandleCreated)
            {
                try { control.CreateControl(); }
                catch { return Rectangle.Empty; }
            }
            if (!control.IsHandleCreated) return Rectangle.Empty;
            Point topLeft = PointToClient(control.PointToScreen(Point.Empty));
            var rect = new Rectangle(topLeft.X, topLeft.Y, control.Width, control.Height);
            // 若目标控件位于可滚动工作区内，裁剪到工作区可见视口内，避免超长卡片撑爆新手引导框选区
            if (workspaceFrame != null && !workspaceFrame.IsDisposed && workspaceFrame.IsHandleCreated && IsDescendantOf(control, workspaceFrame))
            {
                Point wsTopLeft = PointToClient(workspaceFrame.PointToScreen(Point.Empty));
                var wsRect = new Rectangle(wsTopLeft.X, wsTopLeft.Y, workspaceFrame.Width, workspaceFrame.Height);
                rect = Rectangle.Intersect(rect, wsRect);
            }
            return rect;
        }

        private static bool IsDescendantOf(Control child, Control ancestor)
        {
            for (Control cur = child; cur != null; cur = cur.Parent)
                if (cur == ancestor) return true;
            return false;
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
                    Title = "欢迎使用 BoBo远程 · 极简导航栏",
                    Description = "左侧导航已精简为「我的设备」「远程协助」与底部「设置」三大区（防窥与显示设置已收纳至设置页）。",
                    Tip = "支持键盘 ←/→ 翻页，Esc 跳过引导。",
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
                    Title = "设备列表 · 添加与精简管理",
                    Description = "自动发现同局域网或同账号电脑，按「电脑」「手机/平板」分组卡片展示。支持右上角「+ 添加设备」、重命名备注或点击设备右侧 (i) 隐藏不需要的冗余设备。",
                    Tip = "同机多网卡/多通道节点已自动智能去重，不会反复占满列表。",
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
                        return !listRect.IsEmpty ? listRect : GetClientRectForControl(devicesGuideCard);
                    }
                },
                new OnboardingStep
                {
                    Title = "远程协助 · 即点即出 4 位配对码",
                    Description = "在「本设备」点击「生成配对码」（或直接点击 • • • • 区域）立即生成 120 秒有效的 4 位临时码，下方可连接伙伴设备或加密传文件。",
                    Tip = "默认仅保留最常用输入与连接按钮，点击「高级线路与画质选项」可按需展开路由与中转配置。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(AssistPage);
                        SelectAssistMode(0);
                        ResetPageScroll(AssistPage);
                    },
                    ResolveTargetClientRect = delegate
                    {
                        return GetClientRectForControl(assistLocalCard ?? assistPartnerCard);
                    }
                },
                new OnboardingStep
                {
                    Title = "防窥壁纸与显示保护（设置页）",
                    Description = "防窥与显示已集成至「设置 → 防窥与显示」：开启后物理屏立刻覆盖安全壁纸，远程画面清晰可见，支持 Ctrl+Alt+Shift+P 紧急解除。",
                    Tip = "也可以随时点击右上角小盾牌图标一键开关防窥壁纸。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        OpenPrivacySettings();
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForControl(wallpaperCardControl); }
                },
                new OnboardingStep
                {
                    Title = "检查本机被控状态与跨网服务",
                    Description = "在「设置 → 网络与服务」查看监听端口、跨网账号、已授权设备与锁屏穿透服务状态。",
                    Tip = "如默认 49810 端口被占，系统会自动无缝启用备用端口并同步给跨网与局域网广播。",
                    PaddingPx = 6,
                    Prepare = delegate
                    {
                        ShowPage(SettingsPage);
                        SelectSettingsTab(3);
                        ResetPageScroll(SettingsPage);
                    },
                    ResolveTargetClientRect = delegate { return GetClientRectForControl(perfDiagCard); }
                },
                new OnboardingStep
                {
                    Title = "顶部快捷工具栏 · 随时重看引导",
                    Description = "右上角快捷栏整合了「新手引导 (?)」、「一键防窥」、「本机状态与跨网账号」和「更多功能菜单」。",
                    Tip = "引导完成后不再自动弹出；以后点右上角「?」随时可重新查看！",
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
            preferHevc = true;
            preferCompressedAudio = true;
            useAbsoluteMouse = true;
            minimizeToTray = false;
            autoCheckUpdates = true;
            defaultQuality = AutoQualityProfile;
            preferredConnectionRoute = BoBoRoutePreference.Auto;

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
                    minimizeToTray = ReadFlag(lines, 10, false);
                    autoCheckUpdates = ReadFlag(lines, 11, true);
                    useAbsoluteMouse = ReadFlag(lines, 12, true);
                    autoRemotePrivacyOnConnect = ReadFlag(lines, 14, false);
                    lockHostOnDisconnect = ReadFlag(lines, 15, false);
                    hideFloatBallInFullscreen = ReadFlag(lines, 16, false);
                    showFloatBallInWindowed = ReadFlag(lines, 17, false);
                    sendSystemShortcuts = ReadFlag(lines, 18, false);
                    int op;
                    if (lines.Length > 19 && int.TryParse(lines[19], out op) && op >= 50 && op <= 100)
                        miniWindowOpacity = op;
                    allowIncomingControl = ReadFlag(lines, 20, true);
                    int routePref;
                    if (lines.Length > 21 && int.TryParse(lines[21], out routePref)
                        && routePref >= (int)BoBoRoutePreference.Auto
                        && routePref <= (int)BoBoRoutePreference.CustomRelayFallback)
                        preferredConnectionRoute = (BoBoRoutePreference)routePref;
                    preferCompressedAudio = ReadFlag(lines, 22, true);
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
                    "0", // 保留 v2 的旧外置客户端设置位置
                    "1", // 保留 v2 的旧垂直同步设置位置
                    "0", // 保留 v2 的旧帧节奏设置位置
                    WriteFlag(minimizeToTray),
                    WriteFlag(autoCheckUpdates),
                    WriteFlag(useAbsoluteMouse),
                    "1", // 保留 v2 的旧内核选择设置位置
                    WriteFlag(autoRemotePrivacyOnConnect),
                    WriteFlag(lockHostOnDisconnect),
                    WriteFlag(hideFloatBallInFullscreen),
                    WriteFlag(showFloatBallInWindowed),
                    WriteFlag(sendSystemShortcuts),
                    miniWindowOpacity.ToString(),
                    WriteFlag(allowIncomingControl),
                    ((int)preferredConnectionRoute).ToString(),
                    WriteFlag(preferCompressedAudio)
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

        private static string FormatSpacedPin(string pin)
        {
            if (string.IsNullOrEmpty(pin)) return "• • • •";
            var chars = new char[pin.Length * 2 - 1];
            for (int i = 0; i < pin.Length; i++)
            {
                chars[i * 2] = pin[i];
                if (i + 1 < pin.Length) chars[i * 2 + 1] = ' ';
            }
            return new string(chars);
        }
    }
}
