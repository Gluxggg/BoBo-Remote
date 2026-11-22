using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private const int DevicesPage = 0;
        private const int AssistPage = 1;
        private const int PrivacyPage = 2;
        private const int SettingsPage = 3;

        private readonly Button[] navigation = new Button[4];
        private readonly Control[] pages = new Control[4];
        private readonly Theme.StatusBanner settingsBanner = new Theme.StatusBanner("设置变更会自动保存到本机。");
        private readonly Theme.Badge railGuardBadge = new Theme.Badge("防窥守护未开", Theme.PillTone.Neutral);
        private readonly Theme.Badge diagStatusBadge = new Theme.Badge("检测本机宿主中…", Theme.PillTone.Neutral);
        private readonly Label diagVersionLabel = Theme.Text("正在读取 Foundation Sunshine 版本与编码参数…", Theme.TextSize.Small, Theme.Muted);
        private readonly Label diagDetailsLabel = Theme.Text("", Theme.TextSize.Small, Theme.Ink);
        private Theme.Badge sunshineComponentBadge;

        private Button maximizeButton;
        private Rectangle normalBounds;
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
            var frame = Theme.Grid(1);
            Theme.FillColumn(frame);
            frame.BackColor = Theme.Workspace;
            Theme.Row(frame, 48, BuildCaption());
            Theme.Row(frame, 1, Theme.Rule());

            var window = Theme.Grid(3);
            Theme.FixedColumn(window, 198);
            Theme.FixedColumn(window, 1);
            Theme.FillColumn(window);
            Theme.Row(window, 0);
            window.RowStyles[0].SizeType = SizeType.Percent;
            window.RowStyles[0].Height = 100;
            window.Controls.Add(BuildNavigation(), 0, 0);
            window.Controls.Add(Theme.Rule(), 1, 0);

            var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Workspace };
            pages[DevicesPage] = BuildDevicesPage();
            pages[AssistPage] = BuildWorkspace();
            pages[PrivacyPage] = BuildPrivacyAndDisplayPage();
            pages[SettingsPage] = BuildSettingsPage();
            foreach (var page in pages) content.Controls.Add(page);
            ShowPage(DevicesPage);
            window.Controls.Add(content, 2, 0);
            Theme.FillRow(frame, window);
            return frame;
        }

        private Control BuildCaption()
        {
            var bar = Theme.Grid(8);
            bar.BackColor = Theme.Paper;
            Theme.FixedColumn(bar, 16);
            Theme.FixedColumn(bar, 32);
            Theme.FixedColumn(bar, 82);
            Theme.FixedColumn(bar, 114);
            Theme.FillColumn(bar);
            Theme.FixedColumn(bar, 44);
            Theme.FixedColumn(bar, 44);
            Theme.FixedColumn(bar, 46);

            var brandIcon = new Theme.IconTile(Theme.Glyph.Devices, true) { Margin = new Padding(0, 8, 0, 8) };
            var brand = Theme.Text("双端远程", Theme.TextSize.Label, Theme.Ink);
            brand.Padding = new Padding(8, 0, 0, 0);
            var edition = new Theme.Badge("低延迟防窥版", Theme.PillTone.Info) { Margin = new Padding(0, 11, 0, 11) };
            var drag = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper, Margin = Padding.Empty, TabStop = false };
            brand.MouseDown += CaptionMouseDown;
            brandIcon.MouseDown += CaptionMouseDown;
            edition.MouseDown += CaptionMouseDown;
            drag.MouseDown += CaptionMouseDown;
            drag.DoubleClick += delegate { ToggleWindowSize(); };
            brand.DoubleClick += delegate { ToggleWindowSize(); };

            var minimize = CaptionButton("最小化", Theme.Glyph.Minus, Theme.ButtonStyle.Caption, delegate { WindowState = FormWindowState.Minimized; });
            maximizeButton = CaptionButton("最大化", Theme.Glyph.Maximize, Theme.ButtonStyle.Caption, delegate { ToggleWindowSize(); });
            var close = CaptionButton("关闭", Theme.Glyph.Close, Theme.ButtonStyle.Close, delegate { Close(); });
            Theme.Row(bar, 48, null, brandIcon, brand, edition, drag, minimize, maximizeButton, close);
            Resize += delegate
            {
                bool max = WindowState == FormWindowState.Maximized;
                maximizeButton.Text = max ? "还原窗口" : "最大化";
                maximizeButton.AccessibleName = maximizeButton.Text;
                Theme.ChangeGlyph(maximizeButton, max ? Theme.Glyph.Restore : Theme.Glyph.Maximize);
            };
            return bar;
        }

        private static Button CaptionButton(string name, Theme.Glyph glyph, Theme.ButtonStyle style, EventHandler click)
        {
            var button = Theme.Action(name, style, glyph);
            button.TabStop = false;
            button.Click += click;
            return button;
        }

        private void ToggleWindowSize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                if (!normalBounds.IsEmpty) Bounds = normalBounds;
                normalBounds = Rectangle.Empty;
            }
            else
            {
                normalBounds = Bounds;
                MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr WindowMessage(IntPtr window, uint msg, IntPtr first, IntPtr second);

        private void CaptionMouseDown(object sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            if (args.Clicks > 1) return;
            ReleaseCapture();
            WindowMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams chrome = base.CreateParams;
                chrome.Style |= 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
                return chrome;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyPrivacyId)
            {
                TogglePrivacyCurtain(true);
                return;
            }
            if (message.Msg == 0x0083 && message.WParam != IntPtr.Zero)
            {
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            if (message.Msg != 0x0084 || WindowState != FormWindowState.Normal) return;
            long packed = message.LParam.ToInt64();
            var position = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
            int edge = Math.Max(5, SystemInformation.FrameBorderSize.Width);
            bool left = position.X < edge, right = position.X >= ClientSize.Width - edge;
            bool top = position.Y < edge, bottom = position.Y >= ClientSize.Height - edge;
            if (top) message.Result = (IntPtr)(left ? 13 : right ? 14 : 12);
            else if (bottom) message.Result = (IntPtr)(left ? 16 : right ? 17 : 15);
            else if (left || right) message.Result = (IntPtr)(left ? 10 : 11);
        }

        private Control BuildNavigation()
        {
            var rail = Theme.Grid(1);
            Theme.FillColumn(rail);
            rail.BackColor = Theme.Rail;
            rail.Padding = new Padding(12, 18, 12, 12);
            Theme.Row(rail, 42, MakePageButton(DevicesPage, Theme.Glyph.Devices, "我的设备"));
            Theme.Row(rail, 6);
            Theme.Row(rail, 42, MakePageButton(AssistPage, Theme.Glyph.Link, "远程协助"));
            Theme.Row(rail, 6);
            Theme.Row(rail, 42, MakePageButton(PrivacyPage, Theme.Glyph.Shield, "防窥与显示"));
            Theme.Row(rail, 6);
            Theme.Row(rail, 42, MakePageButton(SettingsPage, Theme.Glyph.Bolt, "性能与设置"));
            Theme.FillRow(rail);
            Theme.Row(rail, 30, railGuardBadge);
            Theme.Row(rail, 10);
            Theme.Row(rail, 1, Theme.Rule());
            Theme.Row(rail, 8);
            var help = Theme.Action("调优与指南", Theme.ButtonStyle.Navigation, Theme.Glyph.Help);
            help.Click += delegate { OpenGuide(); };
            Theme.Row(rail, 38, help);
            return rail;
        }

        private Button MakePageButton(int page, Theme.Glyph glyph, string title)
        {
            var button = Theme.Action(title, Theme.ButtonStyle.Navigation, glyph);
            button.Click += delegate { ShowPage(page); };
            navigation[page] = button;
            return button;
        }

        private void ShowPage(int selected)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                bool active = i == selected;
                pages[i].Visible = active;
                Theme.Highlight(navigation[i], active);
            }
            pages[selected].BringToFront();
            if (selected == DevicesPage) RefreshSavedDevices();
        }

        private static Panel CreatePage(int height, out TableLayoutPanel desk)
        {
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, BackColor = Theme.Workspace };
            desk = Theme.Grid(1);
            Theme.FillColumn(desk);
            desk.Dock = DockStyle.Top;
            desk.Height = height;
            desk.Padding = new Padding(26, 18, 26, 18);
            desk.BackColor = Theme.Workspace;
            viewport.Controls.Add(desk);
            return viewport;
        }

        private static void AddPageHeader(TableLayoutPanel grid, string title, string subtitle, Control trailing = null, int actionWidth = 128)
        {
            var titles = Theme.Grid(1);
            Theme.FillColumn(titles);
            Theme.Row(titles, 36, Theme.Text(title, Theme.TextSize.Title, Theme.Ink));
            Theme.Row(titles, 20, Theme.Text(subtitle, Theme.TextSize.Small, Theme.Muted));

            if (trailing == null)
            {
                Theme.Row(grid, 56, titles);
                return;
            }
            var heading = Theme.Grid(3);
            Theme.FillColumn(heading);
            Theme.FixedColumn(heading, 12);
            Theme.FixedColumn(heading, actionWidth);
            trailing.Margin = new Padding(0, 10, 0, 10);
            Theme.Row(heading, 56, titles, null, trailing);
            Theme.Row(grid, 56, heading);
        }

        private Control BuildSettingsPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(740, out desk);
AddPageHeader(desk, "性能与设置", "降低外网丢包与延迟的主机调优、Moonlight V+ 串流参数与系统组件管理");
            Theme.Row(desk, 12);

            // Modern Segmented Tab Bar
            var track = new Theme.SegmentedTrack();
            var tabBar = Theme.Grid(5);
            Theme.FillColumn(tabBar);
            Theme.FixedColumn(tabBar, 4);
            Theme.FillColumn(tabBar);
            Theme.FixedColumn(tabBar, 4);
            Theme.FillColumn(tabBar);
            var tabPerf = Theme.Action("低延迟与抗丢包", Theme.ButtonStyle.Segmented, Theme.Glyph.Bolt);
            var tabStream = Theme.Action("串流与画面", Theme.ButtonStyle.Segmented, Theme.Glyph.Desktop);
            var tabGeneral = Theme.Action("常规与组件", Theme.ButtonStyle.Segmented, Theme.Glyph.Gear);
            Theme.Row(tabBar, 34, tabPerf, null, tabStream, null, tabGeneral);
            track.Controls.Add(tabBar);

            var trackWrap = Theme.Grid(2);
            Theme.FixedColumn(trackWrap, 480);
            Theme.FillColumn(trackWrap);
            Theme.Row(trackWrap, 42, track);
            Theme.Row(desk, 42, trackWrap);
            Theme.Row(desk, 14);

            var tabContent = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Workspace };
            var labels = new[] { tabPerf, tabStream, tabGeneral };
            var panels = new Control[] { BuildPerformanceTab(), BuildStreamSettingsTab(), BuildGeneralAndComponentsTab() };
            for (int i = 0; i < panels.Length; i++)
            {
                int tabIndex = i;
                tabContent.Controls.Add(panels[i]);
                labels[i].Click += delegate { SelectSettingsTab(labels, panels, tabIndex); };
            }
            SelectSettingsTab(labels, panels, 0);
            Theme.Row(desk, 535, tabContent);
            Theme.Row(desk, 12);
            Theme.Row(desk, 44, settingsBanner);
            return viewport;
        }

        private Control BuildPerformanceTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            // Card 1: Host Foundation Sunshine Diagnostic & One-Click Tuning
            var hostCard = new Theme.Card { Padding = new Padding(20, 16, 20, 14) };
            var h = Theme.Grid(1);
            Theme.FillColumn(h);

            var top = Theme.Grid(3);
            Theme.FillColumn(top);
            Theme.FixedColumn(top, 12);
            Theme.FixedColumn(top, 180);
            Theme.Row(top, 28, Theme.Text("被控端抗丢包诊断与一键调优（Foundation Sunshine）", Theme.TextSize.Section, Theme.Ink), null, diagStatusBadge);
            Theme.Row(h, 28, top);
            Theme.Row(h, 22, diagVersionLabel);
            Theme.Row(h, 6);
            diagDetailsLabel.TextAlign = ContentAlignment.TopLeft;
            Theme.Row(h, 68, diagDetailsLabel);
            Theme.Row(h, 10);

            var btnBar = Theme.Grid(7);
            Theme.FixedColumn(btnBar, 178);
            Theme.FixedColumn(btnBar, 8);
            Theme.FixedColumn(btnBar, 118);
            Theme.FixedColumn(btnBar, 8);
            Theme.FixedColumn(btnBar, 112);
            Theme.FixedColumn(btnBar, 8);
            Theme.FillColumn(btnBar);
            var applyWan = Theme.Action("一键外网抗丢包调优", Theme.ButtonStyle.Primary, Theme.Glyph.Bolt);
            var applyLan = Theme.Action("局域网极速档", Theme.ButtonStyle.Secondary);
            var restoreCfg = Theme.Action("还原原配置", Theme.ButtonStyle.Secondary);
            var upgradeFs = Theme.Action("升级 Sunshine (查看 Releases)", Theme.ButtonStyle.AccentSoft);
            applyWan.Click += delegate { OpenOptimizeTool("Apply", "WanAntiLoss"); };
            applyLan.Click += delegate { OpenOptimizeTool("Apply", "LanUltraLow"); };
            restoreCfg.Click += delegate { OpenOptimizeTool("Restore", "WanAntiLoss"); };
            upgradeFs.Click += delegate { OpenExternalUrl(HostNetworkInfo.FoundationReleasesUrl, "已打开 Foundation Sunshine 官方 Releases 页；版本与功能请以发布说明为准，本工具不按版本日期推定发包平滑已生效。"); };
            Theme.Row(btnBar, 38, applyWan, null, applyLan, null, restoreCfg, null, upgradeFs);
            Theme.Row(h, 38, btnBar);
            hostCard.Controls.Add(h);
            Theme.Row(page, 212, hostCard);
            Theme.Row(page, 14);

            // Card 2: Cross-network Acceleration & Transport Tier Guide
            var netCard = new Theme.Card { Padding = new Padding(20, 16, 20, 14) };
            var n = Theme.Grid(1);
            Theme.FillColumn(n);
            Theme.Row(n, 28, Theme.Text("跨网丢包与高延迟的可核验优化方向（社区与 GitHub 方案）", Theme.TextSize.Section, Theme.Ink));
            Theme.Row(n, 6);
            Theme.Row(n, 36, Theme.Text("1. 编码层（AMD）：关闭 vbr_latency，改用 CBR + HRD + LTR=2 + 按需 GOP + 自动 FEC；该组合与历史 Skipping FEC / 大帧异常相关，改善效果需结合本次会话帧统计验收。", Theme.TextSize.Small, Theme.Ink));
            Theme.Row(n, 36, Theme.Text("2. 宿主编码与客户端：以官方 Releases 说明为准选择版本；本工具不按版本日期推定发包平滑已生效。控制端可用 Moonlight V+ 开启 HEVC + 请求 ABR + 关闭 VSync。", Theme.TextSize.Small, Theme.Ink));
            Theme.Row(n, 36, Theme.Text("3. 链路优先级：同网直连局域网 IP（1–2ms） ＞ 双方公网 IPv6 直连（免 1280 MTU 隧道） ＞ EasyTier（国内中继 + KCP/QUIC 抗丢包） ＞ Tailscale。", Theme.TextSize.Small, Theme.Ink));
            Theme.Row(n, 34, Theme.Text("4. 物理网卡：USB Wi-Fi 无线上传大码率易抖动，台式机接入有线网口可明显降低尾延迟（p99）。", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(n, 10);

            var netBtns = Theme.Grid(5);
            Theme.FixedColumn(netBtns, 176);
            Theme.FixedColumn(netBtns, 8);
            Theme.FixedColumn(netBtns, 152);
            Theme.FixedColumn(netBtns, 8);
            Theme.FixedColumn(netBtns, 118);
            var openGuideBtn = Theme.Action("打开完整调优指南", Theme.ButtonStyle.Primary, Theme.Glyph.Help);
            var openEasyTier = Theme.Action("获取 EasyTier 组网", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Link);
            var refreshDiag = Theme.Action("刷新诊断", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            openGuideBtn.Click += delegate { OpenGuide("低延迟调优与防窥指南.md"); };
            openEasyTier.Click += delegate { OpenExternalUrl(HostNetworkInfo.EasyTierReleasesUrl, "已打开 EasyTier 官方 GitHub Releases 页（支持国内中继与 KCP/QUIC 抗丢包）。"); };
            refreshDiag.Click += async delegate { await RefreshHostDiagnosticsAsync(); ShowSettingsBanner("本机抗丢包与宿主诊断已刷新。", Theme.PillTone.Success); };
            Theme.Row(netBtns, 36, openGuideBtn, null, openEasyTier, null, refreshDiag);
            Theme.Row(n, 36, netBtns);
            netCard.Controls.Add(n);
            Theme.Row(page, 266, netCard);
            return page;
        }

        private void UpdateDiagnosticsUi()
        {
            if (diagStatusBadge == null) return;
            if (!hostSnapshot.SunshineInstalled)
            {
                diagStatusBadge.UpdateStatus("未检测到宿主", Theme.PillTone.Neutral);
                diagVersionLabel.Text = "本机未安装 Foundation Sunshine（控制端笔记本无需安装，直接连台式机即可）。";
                diagDetailsLabel.Text = "控制端：" + StreamAvailabilityText() + " 画质与连接参数以『远程协助』中的当前选择为准。";
            }
            else
            {
                bool hostRunning = hostSnapshot.SunshineRunning;
                string hostState = hostRunning ? "宿主运行" : "宿主已停止";
                if (hostSnapshot.AntiLossTuned)
                    diagStatusBadge.UpdateStatus(hostState + " · 配置项已就绪", hostRunning ? Theme.PillTone.Success : Theme.PillTone.Warning);
                else
                    diagStatusBadge.UpdateStatus(hostState + " · 待检查配置", Theme.PillTone.Warning);
                diagVersionLabel.Text = "版本：" + hostSnapshot.SunshineVersion + "  ·  编码器：" + hostSnapshot.Encoder
                    + "  ·  服务：" + (hostSnapshot.SunshineRunning ? "运行中" : "已停止")
                    + "  ·  发包平滑：未核验（不按版本日期推断）";
                if (hostSnapshot.Recommendations.Count == 0)
                    diagDetailsLabel.Text = (hostRunning ? "" : "宿主服务当前未运行；")
                        + "可核验的配置项符合目标；实际串流丢包与端到端延迟尚未实测。";
                else
                    diagDetailsLabel.Text = "• " + string.Join("\r\n• ", hostSnapshot.Recommendations.ToArray());
            }
            if (sunshineComponentBadge != null)
            {
                sunshineComponentBadge.UpdateStatus(hostSnapshot.SunshineInstalled ? (hostSnapshot.SunshineRunning ? "运行中" : "已安装") : "未安装",
                    hostSnapshot.SunshineRunning ? Theme.PillTone.Success : Theme.PillTone.Neutral);
            }
        }

        private void OpenExternalUrl(string url, string statusText)
        {
            string err;
            if (Engines.TryOpenUrl(url, out err)) ShowSettingsBanner(statusText, Theme.PillTone.Success);
            else ShowSettingsBanner("无法打开浏览器：" + err, Theme.PillTone.Alert);
        }

        private Control BuildStreamSettingsTab()
        {
            var video = Theme.Grid(1);
            Theme.FillColumn(video);
            var stream = SettingGroup(video, "低延迟桌面串流参数（Moonlight V+）", 435);
            var enhanced = PreferenceToggle("Foundation 自适应码率", useFoundationClient,
                value => { useFoundationClient = value; UpdateStreamHint(); });
            enhanced.Enabled = FoundationClient.IsAvailable;
            AddSettingRow(stream, "Foundation 自适应码率（ABR · 推荐开启）",
                enhanced.Enabled ? "使用 Moonlight V+ 根据实时丢包与 RTT 动态调节码率，防卡死。" : "缺少 engines\\MoonlightVPlus 组件。", enhanced);
            AddSettingRow(stream, "优先使用 HEVC (H.265) 编码", "同画质节省约 35–40% 带宽，外网及弱网档位也受益（默认开启）。",
                PreferenceToggle("优先使用 HEVC 编码", preferHevc, value => { preferHevc = value; UpdateStreamHint(); }));
            AddSettingRow(stream, "极速低延迟（关闭垂直同步 VSync）", "传入 --no-vsync，减少约 1 帧显示缓冲延迟，操作更跟手（默认开启）。",
                PreferenceToggle("极速低延迟", lowLatencyNoVsync, value => { lowLatencyNoVsync = value; UpdateStreamHint(); }));
            AddSettingRow(stream, "帧节奏平滑（Frame Pacing）", "关闭时延迟最低；若高刷屏拖动有微抖动可尝试开启。",
                PreferenceToggle("帧节奏平滑", enableFramePacing, value => { enableFramePacing = value; UpdateStreamHint(); }));
            AddSettingRow(stream, "串流性能统计浮层", "连接后显示接收帧率、网络丢帧、RTT 与解码耗时（也可按 Ctrl+Alt+Shift+S）。",
                PreferenceToggle("串流统计", showStreamStats, value => showStreamStats = value));
            AddSettingRow(stream, "在被控端同时播放声音", "关闭时声音仅发送到控制端笔记本。",
                PreferenceToggle("在被控端同时播放声音", playAudioOnHost, value => playAudioOnHost = value));
            AddSettingRow(stream, "使用窗口模式", "开启后以可调整大小的窗口打开桌面串流；关闭为全屏。",
                PreferenceToggle("使用窗口模式", windowedStream, value => windowedStream = value));
            return video;
        }

        private Control BuildGeneralAndComponentsTab()
        {
            var page = Theme.Grid(1);
            Theme.FillColumn(page);

            var topSplit = Theme.Grid(3);
            Theme.FillColumn(topSplit);
            Theme.FixedColumn(topSplit, 16);
            Theme.FillColumn(topSplit);

            // Left: General preferences
            var leftCard = new Theme.Card { Padding = new Padding(18, 14, 18, 12) };
            var left = Theme.Grid(1);
            Theme.FillColumn(left);
            Theme.Row(left, 28, Theme.Text("使用习惯与默认项", Theme.TextSize.Section, Theme.Ink));
            AddSettingRow(left, "登录后自动启动", "开机进入 Windows 后自动运行。",
                SystemToggle("登录后自动启动", WindowsSession.StartsAtSignIn(), WindowsSession.TrySetStartAtSignIn));
            AddSettingRow(left, "关闭窗口最小化到托盘", "保持防窥壁纸守护后台运行，托盘可彻底退出。",
                PreferenceToggle("最小化到托盘", minimizeToTray, value => minimizeToTray = value));
            AddSettingRow(left, "窗口打开时防待机", "避免被控电脑因闲置自动休眠断连。",
                SystemToggle("窗口打开时防待机", false, ChangeKeepAwake));
            AddSettingRow(left, "启动时填入上次设备", "自动回填最近连接过的设备码和主机 IP。",
                PreferenceToggle("启动时填入上次设备", fillLastTarget, value => fillLastTarget = value));

            var defaultProfile = Theme.Choice("默认串流画质", false);
            defaultProfile.DropDownWidth = 360;
            foreach (var item in quality.Items) defaultProfile.Items.Add(item);
            defaultProfile.SelectedIndex = defaultQuality;
            defaultProfile.SelectedIndexChanged += delegate
            {
                defaultQuality = defaultProfile.SelectedIndex;
                quality.SelectedIndex = defaultQuality;
                SavePreferences();
            };
            AddSettingRow(left, "默认串流画质", "发起桌面串流时默认选中的档位。", Theme.WrapInput(defaultProfile));

            var clear = Theme.Action("清空记录", Theme.ButtonStyle.Secondary);
            clear.Click += delegate { ClearRecents(); };
            AddSettingRow(left, "最近连接历史", "清除本机保存的设备码与 IP 历史。", clear);
            leftCard.Controls.Add(left);

            // Right: Engine Health
            var rightCard = new Theme.Card { Padding = new Padding(18, 14, 18, 12) };
            var right = Theme.Grid(1);
            Theme.FillColumn(right);
            Theme.Row(right, 28, Theme.Text("内置引擎与本机组件", Theme.TextSize.Section, Theme.Ink));
            AddSettingRow(right, "Moonlight V+ 6.5.6", "主力低延迟客户端，支持 ABR 自适应码率。", EngineAvailability(FoundationClient.IsAvailable));
            AddSettingRow(right, "Moonlight 6.2.0", "官方便携备用串流引擎。", EngineAvailability(Engines.MoonlightPresent));
            AddSettingRow(right, "RustDesk 1.5.3", "通用远程协助、设备码与文件互传。", EngineAvailability(Engines.RustDeskPresent));
            AddSettingRow(right, "Tailscale CLI", "读取组网设备与路径探测。", EngineAvailability(Engines.TailscalePresent));
            sunshineComponentBadge = new Theme.Badge("检测中…", Theme.PillTone.Neutral) { Margin = new Padding(96, 0, 0, 0) };
            AddSettingRow(right, "Foundation Sunshine", "本机被控端串流宿主与 Zako 虚拟屏。", sunshineComponentBadge);
            Theme.FillRow(right);
            rightCard.Controls.Add(right);

            Theme.Row(topSplit, 430, leftCard, null, rightCard);
            Theme.Row(page, 430, topSplit);
            return page;
        }

        private static TableLayoutPanel SettingGroup(TableLayoutPanel parent, string title, int height)
        {
            var card = new Theme.Card { Padding = new Padding(20, 14, 20, 10) };
            var content = Theme.Grid(1);
            Theme.FillColumn(content);
            Theme.Row(content, 28, Theme.Text(title, Theme.TextSize.Section, Theme.Ink));
            card.Controls.Add(content);
            Theme.Row(parent, height, card);
            return content;
        }

        private static void SelectSettingsTab(Button[] labels, Control[] panels, int selected)
        {
            for (int i = 0; i < panels.Length; i++)
            {
                panels[i].Visible = i == selected;
                Theme.Highlight(labels[i], i == selected);
            }
            panels[selected].BringToFront();
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
                Dock = DockStyle.Fill,
                Margin = new Padding(124, 0, 0, 0)
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
            return new Theme.Badge(present ? "已就绪" : "未找到", present) { Margin = new Padding(96, 0, 0, 0) };
        }

        private static void AddSettingRow(TableLayoutPanel parent, string title, string description, Control action)
        {
            var row = Theme.Grid(3);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 12);
            Theme.FixedColumn(row, 184);
            var text = Theme.Grid(1);
            Theme.FillColumn(text);
            text.Padding = new Padding(0, 6, 0, 6);
            Theme.Row(text, 20, Theme.Text(title, Theme.TextSize.Label, Theme.Ink));
            Theme.Row(text, 19, Theme.Text(description, Theme.TextSize.Small, Theme.Muted));
            action.Margin = new Padding(action.Margin.Left, 9, 0, 9);
            Theme.Row(row, 52, text, null, action);
            Theme.Row(parent, 1, Theme.Rule());
            Theme.Row(parent, 52, row);
        }

        private static string PreferencePath { get { return Path.Combine(RecentStore.DirPath, "preferences.txt"); } }

        private void LoadPreferences()
        {
            // 智能低延迟默认：若已携带 Moonlight V+ 则默认开启 ABR + HEVC + 无 VSync
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
                // 版本 2 偏好格式（带 v2 头），旧版偏好平滑升级到低延迟默认同时保留用户核心习惯
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
                ShowSettingsBanner("已清空本机最近连接历史。", Theme.PillTone.Success);
            }
            catch (IOException) { PreferenceError(); }
            catch (UnauthorizedAccessException) { PreferenceError(); }
        }
    }
}
