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
        private Button navExitBtn;
        private CrossNetSidebarCard sidebarCrossNetCard;
        private Control perfDiagCard;
        private Theme.TwoLineText updateStatusCardText;
        private Button updateActionButton;
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
            Theme.FixedColumn(body, 232);
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
            var bar = Theme.Grid(15);
            topBarGrid = bar;
            Theme.FixedColumn(bar, 232);
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

            topGuideBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Help);
            topGuideBtn.AccessibleName = "新手使用引导";
            topGuideBtn.Margin = new Padding(0, 7, 0, 7);
            topGuideBtn.Click += delegate { StartOnboardingTour(false); };

            topPrivacyBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Shield);
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
            Theme.Row(rail, 36, startAssistBtn);

            var favoritesBtn = MakePageButton(FavoritesPage, Theme.Glyph.Star, "收藏设备");
            Theme.SetNavIndent(favoritesBtn, 14);
            Theme.SetGlyphTint(favoritesBtn, Theme.Accent);
            navFavoritesRow = rail.RowCount;
            Theme.Row(rail, 36, favoritesBtn);

            Theme.Row(rail, 6);

            // Group 3: 显示与隐私
            var privacyGroup = Theme.Action("防窥与显示", Theme.ButtonStyle.NavSection, Theme.Glyph.Shield);
            Theme.Highlight(privacyGroup, true);
            privacyGroup.Click += delegate
            {
                navPrivacyExpanded = !navPrivacyExpanded;
                Theme.Highlight(privacyGroup, navPrivacyExpanded);
                SyncNavigationSectionVisibility();
            };
            Theme.Row(rail, 34, privacyGroup);

            var privacyBtn = MakePageButton(PrivacyPage, Theme.Glyph.Eye, "防窥与显示");
            Theme.SetNavIndent(privacyBtn, 14);
            Theme.SetGlyphTint(privacyBtn, Theme.Accent);
            navPrivacyRow = rail.RowCount;
            Theme.Row(rail, 36, privacyBtn);

            Theme.FillRow(rail);

            // Compact Glacier-Blue Embedded Tailscale Status Card
            sidebarCrossNetCard = new CrossNetSidebarCard();
            sidebarCrossNetCard.Click += async delegate { await HandleCrossNetCardClickAsync(); };
            Theme.Row(rail, 60, sidebarCrossNetCard);
            Theme.Row(rail, 8);

            // Bottom pinned divider + Settings + Direct Exit button (clear frontend exit!)
            Theme.Row(rail, 1, Theme.Rule());
            Theme.Row(rail, 8);
            var settingsBtn = MakePageButton(SettingsPage, Theme.Glyph.Gear, "设置");
            Theme.SetNavIndent(settingsBtn, 0);
            Theme.SetGlyphTint(settingsBtn, Theme.BodyInk);
            Theme.Row(rail, 38, settingsBtn);

            Theme.Row(rail, 4);
            navExitBtn = Theme.Action("退出 BoBo远程", Theme.ButtonStyle.Navigation, Theme.Glyph.Close);
            navExitBtn.AccessibleName = "退出 BoBo远程并断开远控";
            Theme.SetNavIndent(navExitBtn, 0);
            Theme.SetGlyphTint(navExitBtn, Theme.Alert);
            navExitBtn.Click += delegate { forceExit = true; Close(); };
            Theme.Row(rail, 38, navExitBtn);
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
            if (navPrivacyRow >= 0)
            {
                navRail.RowStyles[navPrivacyRow].Height = navPrivacyExpanded ? itemH : 0;
                navigation[PrivacyPage].Visible = navPrivacyExpanded;
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
            var viewport = CreatePage(700, out desk);
            settingsDesk = desk;

            AddPageHeader(desk, "设置", null);
            Theme.Row(desk, 14);

            // UU-style clean horizontal text tabs with short blue underline indicator
            var tabBar = Theme.Grid(9);
            Theme.FixedColumn(tabBar, 64);
            Theme.FixedColumn(tabBar, 24);
            Theme.FixedColumn(tabBar, 112);
            Theme.FixedColumn(tabBar, 24);
            Theme.FixedColumn(tabBar, 140);
            Theme.FixedColumn(tabBar, 24);
            Theme.FixedColumn(tabBar, 112);
            Theme.FillColumn(tabBar);
            Theme.FixedColumn(tabBar, 1);

            var tabGeneral = Theme.Action("常规", Theme.ButtonStyle.UnderlineTab);
            var tabStream = Theme.Action("串流与画质", Theme.ButtonStyle.UnderlineTab);
            var tabPerf = Theme.Action("低延迟与抗丢包", Theme.ButtonStyle.UnderlineTab);
            var tabNetwork = Theme.Action("引擎与网络", Theme.ButtonStyle.UnderlineTab);

            Theme.Row(tabBar, 38, tabGeneral, null, tabStream, null, tabPerf, null, tabNetwork, null, null);
            Theme.Row(desk, 38, tabBar);
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
            Theme.Row(desk, 42, settingsBanner);

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
            NotifyOnboardingLayoutChanged();
        }

        private Control BuildGeneralSettingsTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            Theme.Row(page, 22, Theme.Text("常用", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(page, 8);

            var basicCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var basicList = Theme.Grid(1);
            Theme.FillColumn(basicList);

            AddGroupedSettingRow(basicList, false, Theme.Glyph.Desktop, "开机自动启动",
                "登录后自动在后台待命",
                SystemToggle("开机自动启动", WindowsSession.StartsAtSignIn(), WindowsSession.TrySetStartAtSignIn));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.Bolt, "防止电脑休眠",
                "保持被控端屏幕与网络在线",
                SystemToggle("防止电脑休眠", false, ChangeKeepAwake));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.Pin, "最小化到托盘",
                "关闭窗口时转入系统托盘",
                PreferenceToggle("最小化到托盘", minimizeToTray, value => minimizeToTray = value));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.Refresh, "自动填入上次设备",
                "启动时回填最近连接目标",
                PreferenceToggle("启动时填入上次设备", fillLastTarget, value => fillLastTarget = value));

            AddGroupedSettingRow(basicList, true, Theme.Glyph.Refresh, "自动检查更新",
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
            AddGroupedSettingRow(basicList, true, Theme.Glyph.Image, "默认串流画质",
                "发起桌面串流时的默认画质档位",
                Theme.WrapInput(defaultProfile), 372);

            settingsHostDisconnectActionCombo = CreateHostDisconnectActionCombo();
            AddGroupedSettingRow(basicList, true, Theme.Glyph.Lock, "被控断开后动作",
                "远控结束时回到桌面或锁屏",
                Theme.WrapInput(settingsHostDisconnectActionCombo), 160);

            basicCard.Controls.Add(basicList);
            Theme.Row(page, SumRows(basicList) + basicCard.Padding.Vertical, basicCard);
            Theme.Row(page, 16);

            Theme.Row(page, 22, Theme.Text("版本与引导", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(page, 8);

            var miscCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var miscList = Theme.Grid(1);
            Theme.FillColumn(miscList);

            updateActionButton = Theme.Action("检查更新", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Refresh);
            updateActionButton.Click += async delegate { await HandleUpdateButtonClickAsync(); };
            updateStatusCardText = AddGroupedSettingRowWithDesc(miscList, false, Theme.Glyph.Refresh,
                "软件版本 (v" + AppUpdater.CurrentVersion + ")",
                "支持在线校验升级与增量覆盖",
                updateActionButton, 146);

            var replayGuide = Theme.Action("重新查看", Theme.ButtonStyle.Secondary, Theme.Glyph.Help);
            replayGuide.Click += delegate { StartOnboardingTour(false); };
            AddGroupedSettingRow(miscList, true, Theme.Glyph.Help, "新手引导",
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

            Theme.Row(page, 22, Theme.Text("画面与音频", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(page, 8);

            var mediaCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var mediaList = Theme.Grid(1);
            Theme.FillColumn(mediaList);

            AddGroupedSettingRow(mediaList, false, Theme.Glyph.Desktop, "优先使用 HEVC (H.265)",
                "高清场景节省约 35% 带宽",
                PreferenceToggle("优先使用 HEVC 编码", preferHevc, value => { preferHevc = value; UpdateStreamHint(); }));

            AddGroupedSettingRow(mediaList, true, Theme.Glyph.Volume, "高效 AAC-LC 音频编码",
                "96 kbps 压缩传输 · 不支持时回落 ADPCM",
                PreferenceToggle("高效 AAC 音频", preferCompressedAudio, value => preferCompressedAudio = value));

            AddGroupedSettingRow(mediaList, true, Theme.Glyph.Bell, "在被控端播放声音",
                "关闭时声音仅传至主控端",
                PreferenceToggle("在被控端同时播放声音", playAudioOnHost, value => playAudioOnHost = value));

            AddGroupedSettingRow(mediaList, true, Theme.Glyph.Info, "实时性能浮层 (OSD)",
                "Ctrl+Alt+Shift+S 切换显示",
                PreferenceToggle("串流统计", showStreamStats, value => showStreamStats = value));

            mediaCard.Controls.Add(mediaList);
            Theme.Row(page, SumRows(mediaList) + mediaCard.Padding.Vertical, mediaCard);
            Theme.Row(page, 16);

            Theme.Row(page, 22, Theme.Text("操控与隐私", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(page, 8);

            var controlCard = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var controlList = Theme.Grid(1);
            Theme.FillColumn(controlList);

            AddGroupedSettingRow(controlList, false, Theme.Glyph.Desktop, "自由出框鼠标（绝对坐标）",
                "精准点击 · 3D 游戏可切锁鼠模式",
                PreferenceToggle("绝对坐标鼠标", useAbsoluteMouse, value => useAbsoluteMouse = value));

            AddGroupedSettingRow(controlList, true, Theme.Glyph.Maximize, "窗口模式打开串流",
                "关闭时默认无边框全屏",
                PreferenceToggle("使用窗口模式", windowedStream, value => windowedStream = value));

            AddGroupedSettingRow(controlList, true, Theme.Glyph.Shield, "连接后自动开启防窥",
                "接通瞬间自动遮挡被控物理屏",
                PreferenceToggle("自动开启被控端防窥", autoRemotePrivacyOnConnect, value => autoRemotePrivacyOnConnect = value));

            AddGroupedSettingRow(controlList, true, Theme.Glyph.Eye, "全屏隐藏悬浮球",
                "Ctrl+Alt+G 显示或隐藏",
                PreferenceToggle("全屏隐藏悬浮球", hideFloatBallInFullscreen, value => hideFloatBallInFullscreen = value));

            controlCard.Controls.Add(controlList);
            Theme.Row(page, SumRows(controlList) + controlCard.Padding.Vertical, controlCard);
            return page;
        }

        private Control BuildPerformanceTab()
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
            Theme.FixedColumn(head, 188);
            Theme.Row(head, 28, Theme.Text("本机被控诊断", Theme.TextSize.Section, Theme.Ink), null, diagStatusBadge);
            Theme.Row(h, 28, head);
            Theme.Row(h, 4);
            Theme.Row(h, 22, diagVersionLabel);
            Theme.Row(h, 6);
            diagDetailsLabel.TextAlign = ContentAlignment.TopLeft;
            diagDetailsLabel.AutoEllipsis = false;
            Theme.Row(h, 54, diagDetailsLabel);
            Theme.Row(h, 10);

            var btnBar = Theme.Grid(2);
            Theme.FixedColumn(btnBar, 114);
            Theme.FillColumn(btnBar);
            var refreshDiag = Theme.Action("刷新诊断", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            refreshDiag.Click += async delegate
            {
                refreshDiag.Enabled = false;
                try { await RefreshHostDiagnosticsAsync(); }
                finally { if (!IsDisposed) refreshDiag.Enabled = true; }
            };
            Theme.Row(btnBar, 36, refreshDiag, null);
            Theme.Row(h, 36, btnBar);
            hostCard.Controls.Add(h);

            Theme.Row(page, 194, hostCard);
            Theme.Row(page, 14);

            var guideCard = new Theme.Card { Padding = new Padding(20, 16, 20, 16) };
            var g = Theme.Grid(1);
            Theme.FillColumn(g);
            Theme.Row(g, 26, Theme.Text("串流与网络建议", Theme.TextSize.Section, Theme.Ink));
            Theme.Row(g, 6);
            Theme.Row(g, 24, Theme.Text("优先局域网或内置 Tailscale 直连；跨网不可达可启用自建 VPS 兜底中转。", Theme.TextSize.Small, Theme.BodyInk));
            Theme.Row(g, 24, Theme.Text("弱网时 GCC 拥塞控制与动态 FEC 会自动平滑码率，也可手动调低画质。", Theme.TextSize.Small, Theme.BodyInk));
            Theme.Row(g, 10);

            var links = Theme.Grid(4);
            Theme.FixedColumn(links, 142);
            Theme.FixedColumn(links, 10);
            Theme.FixedColumn(links, 158);
            Theme.FillColumn(links);

            var openGuide = Theme.Action("打开使用指南", Theme.ButtonStyle.Secondary, Theme.Glyph.Help);
            var openEt = Theme.Action("EasyTier 组网加速", Theme.ButtonStyle.Secondary);
            openGuide.Click += delegate { OpenGuide("README.md"); };
            openEt.Click += delegate { OpenExternalUrl(HostNetworkInfo.EasyTierReleasesUrl, "已打开 EasyTier 开源异地组网 Releases 页。"); };
            Theme.Row(links, 36, openGuide, null, openEt, null);
            Theme.Row(g, 36, links);
            guideCard.Controls.Add(g);

            Theme.Row(page, 160, guideCard);
            return page;
        }

        private Control BuildNetworkAndEnginesTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            Theme.Row(page, 22, Theme.Text("内核与传输", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(page, 8);

            var card = new Theme.Card { Padding = new Padding(1, 2, 1, 2) };
            var list = Theme.Grid(1);
            Theme.FillColumn(list);

            AddGroupedSettingRow(list, false, Theme.Glyph.Bolt, "BoBoCore 原生内核",
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

            AddGroupedSettingRow(list, true, Theme.Glyph.Keypad, "本机设备码：" + BoBoIdentityStore.Default.FormattedDeviceCode,
                "管理本机已授权的远控设备名单",
                BuildEngineActionRow(true, "管理授权", delegate { ShowAuthorizedPeersDialog(); }), 186);

            AddGroupedSettingRow(list, true, Theme.Glyph.Network, "外部 Tailscale CLI",
                "可选命令行组网补充（优先使用内置通道）",
                EngineAvailability(Engines.TailscalePresent), 98);

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

            var rowWrap = Theme.Grid(1);
            rowWrap.Padding = new Padding(18, 9, 18, 9);
            Theme.FillColumn(rowWrap);

            var row = Theme.Grid(5);
            Theme.FixedColumn(row, 30);
            Theme.FixedColumn(row, 12);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 12);
            Theme.FixedColumn(row, actionWidth);

            var icon = new Theme.IconTile(glyph, true, Theme.SurfaceSubtle, Theme.BodyInk, 8f);
            icon.Margin = new Padding(0, 5, 0, 5);
            var textBlock = new Theme.TwoLineText(title, description, Theme.TextSize.Label, Theme.Ink, Theme.Muted);

            action.Margin = new Padding(0, 3, 0, 3);
            Theme.Row(row, 40, icon, null, textBlock, null, action);
            Theme.Row(rowWrap, 40, row);
            Theme.Row(list, 58, rowWrap);
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
            int peerCount = BoBoIdentityStore.Default.ListAuthorizedPeers().Count;
            if (allowing)
                diagStatusBadge.UpdateStatus("V6 内置运行中 · 端口 " + boboHostServer.ListeningPort, Theme.PillTone.Success);
            else if (serverUp)
                diagStatusBadge.UpdateStatus("已暂停入站被控", Theme.PillTone.Warning);
            else
                diagStatusBadge.UpdateStatus("被控服务未启动", Theme.PillTone.Alert);

            diagVersionLabel.Text = "全内置 BoBoCore v7.3 · 本机设备码：" + BoBoIdentityStore.Default.FormattedDeviceCode;
            diagDetailsLabel.Text = "已授权设备 " + peerCount + " 台 · 控制口 TCP " + BoBoProtocol.DefaultTcpPort + " (TLS 1.2)\r\n"
                + "媒体流 UDP+AES-128-GCM+FEC/NACK · 局域网发现 UDP " + BoBoProtocol.DiscoveryUdpPort;
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
                });
                menu.Items.Add(new ToolStripSeparator());
            }
            AddPopupItem(menu, "刷新全部设备与本机状态", delegate { var _ = RefreshDashboardAsync(); });
            AddPopupItem(menu, "切换防窥屏壁纸 (Ctrl+Alt+Shift+P)", delegate { TogglePrivacyCurtain(false); });
            AddPopupItem(menu, "新手引导使用教程", delegate { StartOnboardingTour(false); });
            AddPopupItem(menu, "检查软件更新（当前 v" + AppUpdater.CurrentVersion + "）", delegate
            {
                ShowPage(SettingsPage);
                SelectSettingsTab(0);
                var _ = CheckForUpdatesAsync(false);
            });
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "管理已授权设备（本机码 " + BoBoIdentityStore.Default.FormattedDeviceCode + "）", delegate { ShowAuthorizedPeersDialog(); });
            AddPopupItem(menu, "运行 V6 全内置内核自检", delegate { RunNativeCoreSelfTestDialog(); });
            AddPopupItem(menu, "BoBo远程使用指南", delegate { OpenGuide("README.md"); });
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "断开当前远控会话", delegate { DisconnectAllRemoteSessionsFromUi(); });
            AddPopupItem(menu, "关于 BoBo远程", delegate { ShowAboutDialog(); });
            AddPopupItem(menu, "退出 BoBo远程（断开远控）", delegate { forceExit = true; Close(); });

            int menuW = Math.Max(DevicePixels(228), menu.PreferredSize.Width);
            Point pt = topMenuBtn.PointToScreen(new Point(topMenuBtn.Width - menuW, topMenuBtn.Height + DevicePixels(6)));
            menu.Show(pt);
        }

        private void ShowHostStatusPopover()
        {
            var menu = CreateCleanPopupMenu();
            string hostState = (boboHostServer != null && boboHostServer.IsRunning && boboHostServer.AllowIncomingControl)
                ? ("V6 内置运行中 · 端口 " + boboHostServer.ListeningPort)
                : "本机被控已暂停";
            var header = new ToolStripMenuItem("本机：" + Environment.MachineName + "  (" + hostState + ")") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            string code = BoBoIdentityStore.Default.FormattedDeviceCode;
            string lan = string.IsNullOrEmpty(hostSnapshot.LanIpv4) ? "未连接" : hostSnapshot.LanIpv4;
            string ts = string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4) ? "未登录" : hostSnapshot.TailscaleIpv4;
            string v6 = string.IsNullOrEmpty(hostSnapshot.PublicIpv6) ? "无公网 IPv6" : ShortenIpv6(hostSnapshot.PublicIpv6);

            AddPopupItem(menu, "复制本机 BoBo 设备码：" + code, delegate { CopyHostAddress("本机 BoBo 设备码", code); });
            AddPopupItem(menu, "复制局域网 IP：" + lan, delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); });
            AddPopupItem(menu, "复制 Tailscale IP：" + ts, delegate { CopyHostAddress("Tailscale IP", hostSnapshot.TailscaleIpv4); });
            AddPopupItem(menu, "复制公网 IPv6：" + v6, delegate { CopyHostAddress("公网 IPv6", hostSnapshot.PublicIpv6); });
            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "前往「开始协助」生成 4 位配对码", delegate { ShowPage(AssistPage); });

            int menuW = Math.Max(DevicePixels(250), menu.PreferredSize.Width);
            Point pt = topStatusBtn.PointToScreen(new Point(topStatusBtn.Width - menuW, topStatusBtn.Height + DevicePixels(6)));
            menu.Show(pt);
        }

        private static readonly Font PopupMenuFont = new Font("Microsoft YaHei UI", 9.25f);

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

                var checkUpdBtn = Theme.Action("检查更新", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
                checkUpdBtn.Click += delegate
                {
                    dlg.DialogResult = DialogResult.Yes;
                    dlg.Close();
                };
                var tourBtn = Theme.Action("新手使用引导", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Help);
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
                string launchErr;
                if (!AppUpdater.TryLaunchDownloadedUpdate(update, downloadedPath, out launchErr))
                {
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
                    Title = "欢迎使用 BoBo远程 · 功能导航栏",
                    Description = "局域网高清串流、加密文件互传与防窥壁纸均已内置。左侧导航分为「我的设备」「远程协助」「防窥与显示」与底部「设置」四大区。",
                    Tip = "仅跨外网 2K 高帧串流与硬件级彻底熄屏属于可选进阶能力，后续步骤会说明。支持 ←/→ 翻页，Esc 跳过。",
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
                    Description = "右上角标签实时展示本机被控与防窥状态。同局域网下，被控端点「允许连接本机」生成 4 位配对码，控制端点「手动连接设备」输入对方 IP 即可零配置开启高清低延迟串流。",
                    Tip = "可选进阶：若要跨外网跑 2K 高帧串流，需两台电脑安装同一组网工具（如 Tailscale）或具备公网 IPv6。",
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
                    Description = "局域网或 Tailscale 同网设备会自动列在这里，显示在线状态与直连/中继链路。点击任意在线设备即可发起 60~144Hz 硬件编码串流；收藏常用设备后，会固定在左侧快捷栏一键直达。",
                    Tip = "首次被控若弹出 Windows 防火墙提示，请点「允许访问」；暂未发现设备可点右上角「刷新状态」。",
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
                    Title = "远程协助 · 4 位配对码与 9 位跨网设备码",
                    Description = "上方「本设备」显示本机地址与设备码，可生成 120 秒有效的 4 位配对码；下方可切换高清串流与加密文件传输。两端同时打开 BoBo远程，首次连接完成配对后即可使用。",
                    Tip = "跨公网连接可使用 Tailscale 或可达的公网 IPv6；设备码可用于查找同网及已授权设备。",
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
                    Title = "防窥壁纸与显示保护",
                    Description = "软件防窥壁纸开箱即用：开启后物理屏立刻覆盖安全壁纸，而远程画面依然清晰可见，支持远控自动开启与退出自动锁屏。紧急解除按 Ctrl+Alt+Shift+P（系统锁屏解锁后不会误重拉）。",
                    Tip = "可在串流控制中心切换超级屏和显示缩放。",
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
                    Title = "检查本机被控状态",
                    Description = "查看本机被控服务是否运行、已授权设备数量与监听端口。点击「刷新诊断」可重新检查当前网络状态。",
                    Tip = "串流时可在控制中心查看网络状态并调整画质。",
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

        private sealed class CrossNetSidebarCard : Control
        {
            private readonly UiTransition hoverTransition;
            private bool hovered;
            private bool pressed;
            private string primaryText = "内置跨网 · 未登录";
            private string secondaryText = "点击一键登录 Tailscale";
            private int visualTone = 0; // 0=Neutral/Accent, 1=Warning, 2=Success

            internal CrossNetSidebarCard()
            {
                hoverTransition = new UiTransition(this);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Cursor = Cursors.Hand;
                BackColor = Theme.ChromeBg;
                AccessibleName = "内置跨网账号状态";
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            internal string PrimaryText { get { return primaryText; } }
            internal string SecondaryText { get { return secondaryText; } }
            internal int VisualTone { get { return visualTone; } }

            internal void ApplyEmbeddedSnapshot(EmbeddedTailnetSnapshot snap)
            {
                string state = snap != null ? (snap.BackendState ?? "") : "";
                if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    string acct = !string.IsNullOrEmpty(snap.DisplayName) ? snap.DisplayName
                        : !string.IsNullOrEmpty(snap.LoginName) ? snap.LoginName
                        : !string.IsNullOrEmpty(snap.TailnetName) ? snap.TailnetName
                        : "内置跨网已就绪";
                    primaryText = acct;
                    secondaryText = string.IsNullOrEmpty(snap.SelfIp4) ? "内置跨网 · 在线" : (snap.SelfIp4 + " · 在线");
                    visualTone = 2;
                }
                else if (snap != null && !string.IsNullOrEmpty(snap.AuthUrl))
                {
                    primaryText = "等待浏览器授权…";
                    secondaryText = "点击在浏览器完成登录";
                    visualTone = 1;
                }
                else if (string.Equals(state, "Starting", StringComparison.OrdinalIgnoreCase))
                {
                    primaryText = "内置跨网启动中…";
                    secondaryText = "正在连接协调节点";
                    visualTone = 1;
                }
                else if (string.Equals(state, "NeedsMachineAuth", StringComparison.OrdinalIgnoreCase))
                {
                    primaryText = "等待设备审批";
                    secondaryText = "需管理员确认设备加入";
                    visualTone = 1;
                }
                else
                {
                    primaryText = "内置跨网 · 未登录";
                    secondaryText = "点击一键登录 Tailscale";
                    visualTone = 0;
                }
                AccessibleName = primaryText + "，" + secondaryText;
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                hoverTransition.MoveTo(1f, 115);
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                pressed = false;
                hoverTransition.MoveTo(0f, 115);
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                pressed = false;
                Invalidate();
                base.OnMouseUp(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (Width < 16 || Height < 16) return;
                Graphics g = e.Graphics;
                Theme.FillBg(g, ClientRectangle, Theme.ChromeBg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                float scale = Math.Max(1f, g.DpiX / 96f);
                float hover = pressed ? 1f : hoverTransition.Value;
                Color bg = Blend(Theme.Paper, Theme.AccentLight, hover * 0.85f);
                Color border = Blend(Theme.Stroke, Theme.AccentBorder, hovered || pressed ? 1f : hover);
                var cardRect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                Theme.Surface(g, cardRect, 8f * scale, bg, border, 1f);

                Color iconBg = visualTone == 2 ? Theme.SuccessLight : visualTone == 1 ? Theme.WarningLight : Theme.AccentLight;
                Color iconFg = visualTone == 2 ? Theme.Success : visualTone == 1 ? Theme.Warning : Theme.Accent;
                float iconBox = (float)Math.Round(30f * scale);
                float iconX = (float)Math.Round(10f * scale);
                float iconY = (float)Math.Round((Height - iconBox) * 0.5f);
                var iconRect = new RectangleF(iconX, iconY, iconBox, iconBox);
                Theme.Surface(g, iconRect, 7f * scale, iconBg, Color.Transparent);
                float pad = (float)Math.Round(6f * scale);
                Theme.DrawGlyph(g, Theme.Glyph.Network, RectangleF.Inflate(iconRect, -pad, -pad), iconFg);

                float dotSize = (float)Math.Round(6.5f * scale);
                float dotX = Width - (float)Math.Round(10f * scale) - dotSize;
                float dotY = (float)Math.Round(11f * scale);
                Color dotColor = visualTone == 2 ? Theme.Success : visualTone == 1 ? Theme.Warning : Theme.StrokeStrong;
                using (var db = new SolidBrush(dotColor))
                    g.FillEllipse(db, dotX, dotY, dotSize, dotSize);

                int textLeft = (int)Math.Round(iconX + iconBox + 10f * scale);
                int textW = Math.Max(16, (int)Math.Floor(dotX - 6f * scale - textLeft));
                int topPad = (int)Math.Round(10f * scale);
                int lineH = Math.Max(15, (int)Math.Round(18f * scale));
                int subTop = topPad + lineH + (int)Math.Round(2f * scale);

                TextRenderer.DrawText(g, primaryText, Theme.GetFont(Theme.TextSize.LabelBold),
                    new Rectangle(textLeft, topPad, textW, lineH), Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                Color subColor = visualTone == 2 ? Theme.Success : visualTone == 1 ? Theme.Warning : Theme.Muted;
                TextRenderer.DrawText(g, secondaryText, Theme.GetFont(Theme.TextSize.Small),
                    new Rectangle(textLeft, subTop, textW + (int)Math.Round(10f * scale), lineH), subColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            private static Color Blend(Color a, Color b, float t)
            {
                if (t <= 0f) return a;
                if (t >= 1f) return b;
                return Color.FromArgb(
                    (int)Math.Round(a.A + (b.A - a.A) * t),
                    (int)Math.Round(a.R + (b.R - a.R) * t),
                    (int)Math.Round(a.G + (b.G - a.G) * t),
                    (int)Math.Round(a.B + (b.B - a.B) * t));
            }
        }
    }
}
