using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private readonly Theme.StatusBanner deviceBanner = new Theme.StatusBanner("正在检查本机与同网设备…");
        private readonly List<SavedDevice> favorites = new List<SavedDevice>();
        private readonly List<TsPeer> cachedPeers = new List<TsPeer>();
        private readonly ToolTip deviceCardToolTip = new ToolTip();
        private Button addDeviceBtn;
        private Button refreshDevices;

        private TableLayoutPanel devicesDesk;
        private Theme.SlimScrollViewport devicesViewport;
        private int devicesGuideRow = -1;
        private int devicesGuideSpacerRow = -1;
        private Theme.Card devicesGuideCard;
        private TableLayoutPanel devicesGuideLayout;
        private Theme.IconTile devicesGuideIcon;
        private Label devicesGuideTitleLabel;
        private Label devicesGuideSubLabel;
        private Panel devicesGuideActionsHost;
        private Button devicesGuideLoginBtn;
        private Button devicesGuideHelpBtn;
        private Button devicesGuideOpenAuthBtn;
        private Button devicesGuideCopyUrlBtn;
        private Button devicesGuideCancelBtn;
        private Button devicesGuideCopyIpBtn;
        private Button devicesGuideLogoutBtn;
        private Button devicesGuideCloseBtn;
        private bool devicesGuideDismissed;

        private string lastEmbeddedAuthUrl = "";
        private string lastEmbeddedBackendState = "";
        private bool embeddedTsBusy;

        private Button computersGroupBtn;
        private bool computersExpanded = true;
        private TableLayoutPanel peerListHost;
        private int peerListRow = -1;

        private Button mobilesGroupBtn;
        private bool mobilesExpanded = true;
        private TableLayoutPanel mobileListHost;
        private int mobileSpacerRow = -1;
        private int mobileGroupRow = -1;
        private int mobileGroupGapRow = -1;
        private int mobileListRow = -1;
        private bool hasMobilePeers;
        private Label devicesHintLabel;

        // Quick-access sidebar device & Detail Page state (UU Image 1)
        private string quickDeviceName = "";
        private string quickDeviceAddress = "";
        private bool quickDeviceOnline;
        private string quickDeviceLink = "";
        private bool quickDeviceIsStream = true;

        private string detailDeviceName = "";
        private string detailDeviceAddress = "";
        private bool detailDeviceOnline;
        private string detailDeviceLink = "";
        private bool detailDeviceIsStream = true;

        private TableLayoutPanel detailDesk;
        private Theme.SlimScrollViewport detailViewport;
        private Theme.Badge detailStatusBadge;
        private Label detailTitleLabel;
        private Button detailFavButton;
        private Button detailRemoveButton;
        private DevicePreviewCanvas detailPreviewCanvas;
        private TableLayoutPanel detailPreviewCardLayout;
        private int detailPreviewRow = -1;
        private int detailHeroCardRow = -1;
        private Control detailHeroCard;

        // Favorites Page state (UU Image 4)
        private TableLayoutPanel favoritesDesk;
        private Control addFavoriteCard;
        private int addFavoriteRow = -1;
        private bool addFavoriteOpen;
        private ComboBox addFavoriteKind;
        private TextBox addFavoriteInput;
        private TableLayoutPanel favoritesListHost;
        private int favoritesListRow = -1;
        private TableLayoutPanel recentListHost;
        private int recentListRow = -1;
        private readonly Theme.StatusBanner favoritesBanner = new Theme.StatusBanner("常用电脑加入收藏后，可一键发起高清低延迟串流。");

        private Control BuildDevicesPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(640, out desk);
            devicesViewport = viewport;
            devicesDesk = desk;
            LoadFavorites();

            var headActions = Theme.Grid(3);
            Theme.FixedColumn(headActions, 108);
            Theme.FixedColumn(headActions, 8);
            Theme.FixedColumn(headActions, 84);
            addDeviceBtn = Theme.Action("添加设备", Theme.ButtonStyle.Secondary, Theme.Glyph.Plus);
            addDeviceBtn.Click += delegate { ShowAddDeviceDialog(); };
            refreshDevices = Theme.Action("刷新", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            refreshDevices.Click += async delegate { await RefreshDashboardAsync(); };
            Theme.Row(headActions, 34, addDeviceBtn, null, refreshDevices);
            AddPageHeader(desk, "全部设备", null, headActions, 200);
            Theme.Row(desk, 16);

            // Clean UU-Remote-style single dismissible guide / cross-net banner card
            var guideCard = BuildDevicesGuideCard();
            devicesGuideRow = desk.RowCount;
            Theme.Row(desk, DevicesGuideCardHeight(), guideCard);
            devicesGuideSpacerRow = desk.RowCount;
            Theme.Row(desk, 14);

            // Collapsible "电脑 N" section header
            var groupRow = Theme.Grid(2);
            Theme.FixedColumn(groupRow, 168);
            Theme.FillColumn(groupRow);
            computersGroupBtn = Theme.Action("电脑 1", Theme.ButtonStyle.Quiet, Theme.Glyph.ChevronRight);
            Theme.SetChevronExpanded(computersGroupBtn, computersExpanded);
            computersGroupBtn.Click += delegate
            {
                computersExpanded = !computersExpanded;
                Theme.SetChevronExpanded(computersGroupBtn, computersExpanded);
                SyncPeerListLayout();
            };
            Theme.Row(groupRow, 28, computersGroupBtn, null);
            Theme.Row(desk, 28, groupRow);
            Theme.Row(desk, 8);

            peerListHost = Theme.Grid(1);
            Theme.FillColumn(peerListHost);
            peerListRow = desk.RowCount;
            Theme.Row(desk, 64, peerListHost);

            // Collapsible "手机/平板 M" section (shown automatically whenever mobile peers exist)
            mobileSpacerRow = desk.RowCount;
            Theme.Row(desk, 0);
            var mobileHeadRow = Theme.Grid(2);
            Theme.FixedColumn(mobileHeadRow, 180);
            Theme.FillColumn(mobileHeadRow);
            mobilesGroupBtn = Theme.Action("手机/平板 0", Theme.ButtonStyle.Quiet, Theme.Glyph.ChevronRight);
            Theme.SetChevronExpanded(mobilesGroupBtn, mobilesExpanded);
            mobilesGroupBtn.Click += delegate
            {
                mobilesExpanded = !mobilesExpanded;
                Theme.SetChevronExpanded(mobilesGroupBtn, mobilesExpanded);
                SyncPeerListLayout();
            };
            Theme.Row(mobileHeadRow, 28, mobilesGroupBtn, null);
            mobileGroupRow = desk.RowCount;
            Theme.Row(desk, 0, mobileHeadRow);
            mobileGroupGapRow = desk.RowCount;
            Theme.Row(desk, 0);

            mobileListHost = Theme.Grid(1);
            Theme.FillColumn(mobileListHost);
            mobileListHost.Visible = false;
            mobileListRow = desk.RowCount;
            Theme.Row(desk, 0, mobileListHost);

            Theme.Row(desk, 10);
            devicesHintLabel = Theme.Text("", Theme.TextSize.Small, Theme.Muted);
            devicesHintLabel.AutoEllipsis = true;
            Theme.Row(desk, 22, devicesHintLabel);

            viewport.ContentWidthChanged += delegate(int contentW) { SyncDevicesResponsiveMetrics(contentW); };
            DrawPeerRows(null);
            RecalculateDevicesDeskHeight();

            Shown += async delegate { await RefreshDashboardAsync(); };
            return viewport;
        }

        private int DevicesGuideCardHeight()
        {
            if (devicesGuideDismissed) return 0;
            if (devicesGuideLayout == null || devicesGuideCard == null) return DevicePixels(98);
            int h = SumRows(devicesGuideLayout) + devicesGuideCard.Padding.Vertical;
            return Math.Max(DevicePixels(92), h);
        }

        private Control BuildDevicesGuideCard()
        {
            var card = new Theme.Card { Padding = new Padding(18, 14, 14, 14) };
            devicesGuideCard = card;
            var root = Theme.Grid(1);
            Theme.FillColumn(root);
            devicesGuideLayout = root;

            var top = Theme.Grid(5);
            Theme.FixedColumn(top, 24);
            Theme.FixedColumn(top, 10);
            Theme.FillColumn(top);
            Theme.FixedColumn(top, 8);
            Theme.FixedColumn(top, 28);

            devicesGuideIcon = new Theme.IconTile(Theme.Glyph.Info, true, Theme.AccentLight, Theme.Accent, 12f);
            devicesGuideTitleLabel = Theme.Text("添加你的其他设备", Theme.TextSize.LabelBold, Theme.Ink);
            devicesGuideCloseBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Close);
            devicesGuideCloseBtn.AccessibleName = "收起提示卡片";
            devicesGuideCloseBtn.Click += delegate { DismissDevicesGuideCard(); };
            Theme.Row(top, 24, devicesGuideIcon, null, devicesGuideTitleLabel, null, devicesGuideCloseBtn);
            Theme.Row(root, 24, top);
            Theme.Row(root, 3);

            var subWrap = Theme.Grid(2);
            Theme.FixedColumn(subWrap, 34);
            Theme.FillColumn(subWrap);
            devicesGuideSubLabel = Theme.Text("跨网登录同一 Tailscale 账号，或点右上角「添加设备」即可一键远控。", Theme.TextSize.Small, Theme.Muted);
            devicesGuideSubLabel.AutoEllipsis = true;
            Theme.FillRow(subWrap);
            subWrap.Controls.Add(devicesGuideSubLabel, 1, 0);
            Theme.Row(root, 20, subWrap);
            Theme.Row(root, 10);

            var actWrap = Theme.Grid(2);
            Theme.FixedColumn(actWrap, 34);
            Theme.FillColumn(actWrap);

            devicesGuideLoginBtn = Theme.Action("跨网账号登录", Theme.ButtonStyle.Secondary);
            devicesGuideLoginBtn.Click += async delegate { await BeginEmbeddedTailnetLoginAsync(); };

            devicesGuideHelpBtn = Theme.Action("如何添加设备", Theme.ButtonStyle.Secondary);
            devicesGuideHelpBtn.Click += delegate { ShowAddDeviceDialog(); };

            devicesGuideOpenAuthBtn = Theme.Action("打开登录页", Theme.ButtonStyle.AccentSoft);
            devicesGuideOpenAuthBtn.Click += delegate { OpenEmbeddedAuthUrlInBrowser(); };

            devicesGuideCopyUrlBtn = Theme.Action("复制链接", Theme.ButtonStyle.Secondary);
            devicesGuideCopyUrlBtn.Click += delegate { CopyEmbeddedAuthUrlToClipboard(); };

            devicesGuideCancelBtn = Theme.Action("取消", Theme.ButtonStyle.Secondary);
            devicesGuideCancelBtn.Click += async delegate { await CancelOrLogoutEmbeddedTailnetAsync(); };

            devicesGuideCopyIpBtn = Theme.Action("复制跨网 IP", Theme.ButtonStyle.Secondary);
            devicesGuideCopyIpBtn.Click += delegate
            {
                EmbeddedTailnetSnapshot s = BoBoEmbeddedTailnet.Default.LatestSnapshot;
                CopyHostAddress("内置 Tailscale IP", s != null ? s.SelfIp4 : "");
            };

            devicesGuideLogoutBtn = Theme.Action("退出账号", Theme.ButtonStyle.DangerSoft);
            devicesGuideLogoutBtn.Click += async delegate { await CancelOrLogoutEmbeddedTailnetAsync(); };

            devicesGuideActionsHost = new GuideActionsStrip(
                this,
                devicesGuideLoginBtn,
                devicesGuideHelpBtn,
                devicesGuideOpenAuthBtn,
                devicesGuideCopyUrlBtn,
                devicesGuideCancelBtn,
                devicesGuideCopyIpBtn,
                devicesGuideLogoutBtn);
            Theme.FillRow(actWrap);
            actWrap.Controls.Add(devicesGuideActionsHost, 1, 0);
            Theme.Row(root, 32, actWrap);

            card.Controls.Add(root);
            ApplyEmbeddedTailnetUi(BoBoEmbeddedTailnet.Default.LatestSnapshot);
            return card;
        }

        private void DismissDevicesGuideCard()
        {
            devicesGuideDismissed = true;
            if (devicesGuideCard != null) devicesGuideCard.Visible = false;
            if (devicesDesk != null)
            {
                if (devicesGuideRow >= 0) devicesDesk.RowStyles[devicesGuideRow].Height = 0;
                if (devicesGuideSpacerRow >= 0) devicesDesk.RowStyles[devicesGuideSpacerRow].Height = 0;
                RecalculateDevicesDeskHeight();
                if (devicesViewport != null) devicesViewport.RefreshLayout();
            }
        }

        private sealed class GuideActionsStrip : Panel
        {
            private readonly MainForm owner;
            private readonly Button loginBtn;
            private readonly Button helpBtn;
            private readonly Button openAuthBtn;
            private readonly Button copyUrlBtn;
            private readonly Button cancelBtn;
            private readonly Button copyIpBtn;
            private readonly Button logoutBtn;

            internal GuideActionsStrip(
                MainForm owner,
                Button loginBtn,
                Button helpBtn,
                Button openAuthBtn,
                Button copyUrlBtn,
                Button cancelBtn,
                Button copyIpBtn,
                Button logoutBtn)
            {
                this.owner = owner;
                this.loginBtn = loginBtn;
                this.helpBtn = helpBtn;
                this.openAuthBtn = openAuthBtn;
                this.copyUrlBtn = copyUrlBtn;
                this.cancelBtn = cancelBtn;
                this.copyIpBtn = copyIpBtn;
                this.logoutBtn = logoutBtn;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.Paper;
                DoubleBuffered = true;

                Button[] all = new[] { loginBtn, helpBtn, openAuthBtn, copyUrlBtn, cancelBtn, copyIpBtn, logoutBtn };
                for (int i = 0; i < all.Length; i++)
                {
                    all[i].Dock = DockStyle.None;
                    Controls.Add(all[i]);
                }
            }

            internal void SetMode(int mode, bool busy, bool hasAuthUrl)
            {
                // mode: 0=unlogged, 1=waiting auth, 2=running
                loginBtn.Visible = mode == 0;
                loginBtn.Enabled = mode == 0 && !busy;
                helpBtn.Visible = mode == 0;
                helpBtn.Enabled = mode == 0;

                openAuthBtn.Visible = mode == 1;
                openAuthBtn.Enabled = mode == 1 && hasAuthUrl;
                copyUrlBtn.Visible = mode == 1;
                copyUrlBtn.Enabled = mode == 1 && hasAuthUrl;
                cancelBtn.Visible = mode == 1;
                cancelBtn.Enabled = mode == 1;

                copyIpBtn.Visible = mode == 2;
                copyIpBtn.Enabled = mode == 2;
                logoutBtn.Visible = mode == 2;
                logoutBtn.Enabled = mode == 2 && !busy;

                PerformLayout();
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                int gap = owner.DevicePixels(8);
                int h = Math.Min(ClientSize.Height, owner.DevicePixels(32));
                int y = Math.Max(0, (ClientSize.Height - h) / 2);
                int x = 0;

                Button[] items = new[] { loginBtn, helpBtn, openAuthBtn, copyUrlBtn, cancelBtn, copyIpBtn, logoutBtn };
                int[] logicalWidths = new[] { 116, 112, 98, 86, 62, 108, 88 };

                for (int i = 0; i < items.Length; i++)
                {
                    Button btn = items[i];
                    if (!btn.Visible) continue;
                    int bw = Math.Min(owner.DevicePixels(logicalWidths[i]), Math.Max(48, ClientSize.Width - x));
                    btn.Bounds = new Rectangle(x, y, bw, h);
                    x += bw + gap;
                }
            }
        }

        private void HookAndAutoStartEmbeddedTailnet()
        {
            BoBoEmbeddedTailnet.Default.StateChanged += OnEmbeddedTailnetStateChanged;
            if (BoBoEmbeddedTailnet.HasSavedState)
            {
                StartEmbeddedTailnetBackgroundAsync().ContinueWith(delegate { }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private async Task StartEmbeddedTailnetBackgroundAsync()
        {
            try
            {
                await BoBoEmbeddedTailnet.Default.EnsureStartedAsync(System.Threading.CancellationToken.None);
            }
            catch { }
        }

        private void OnEmbeddedTailnetStateChanged(EmbeddedTailnetSnapshot snap)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    string prevState = lastEmbeddedBackendState;
                    ApplyEmbeddedTailnetUi(snap);
                    string nextState = snap != null ? (snap.BackendState ?? "") : "";
                    if (!string.Equals(prevState, "Running", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(nextState, "Running", StringComparison.OrdinalIgnoreCase))
                    {
                        RefreshDashboardAsync().ContinueWith(delegate { }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }));
            }
            catch { }
        }

        private void ApplyEmbeddedTailnetUi(EmbeddedTailnetSnapshot snap)
        {
            string state = snap != null ? (snap.BackendState ?? "Stopped") : "Stopped";
            lastEmbeddedBackendState = state;
            lastEmbeddedAuthUrl = snap != null ? (snap.AuthUrl ?? "") : "";

            SyncSettingsCrossNetRow(snap);

            bool isRunning = string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
            bool hasUrl = !string.IsNullOrEmpty(lastEmbeddedAuthUrl);
            bool isWaiting = !isRunning && (hasUrl
                || string.Equals(state, "Starting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "NeedsMachineAuth", StringComparison.OrdinalIgnoreCase));

            if (topStatusBtn != null)
            {
                bool hostFail = boboHostServer == null || !boboHostServer.IsRunning;
                if (hostFail)
                    Theme.SetIndicatorDot(topStatusBtn, true, Theme.WarningDot);
                else if (isRunning)
                    Theme.SetIndicatorDot(topStatusBtn, true, Theme.SuccessDot);
                else if (isWaiting)
                    Theme.SetIndicatorDot(topStatusBtn, true, Theme.WarningDot);
                else
                    Theme.SetIndicatorDot(topStatusBtn, false, Theme.Accent);
            }

            if (devicesGuideCard == null) return;

            bool hostReady = boboHostServer != null && boboHostServer.IsRunning;
            int hostPort = hostReady ? boboHostServer.ListeningPort : BoBoProtocol.DefaultTcpPort;
            var actions = devicesGuideActionsHost as GuideActionsStrip;

            if (isRunning)
            {
                if (devicesGuideIcon != null) devicesGuideIcon.SetColors(Theme.SuccessLight, Theme.Success);
                string account = (snap != null && !string.IsNullOrEmpty(snap.DisplayName)) ? snap.DisplayName
                    : (snap != null && !string.IsNullOrEmpty(snap.LoginName)) ? snap.LoginName
                    : (snap != null && !string.IsNullOrEmpty(snap.TailnetName)) ? snap.TailnetName
                    : "已登录";
                string ipText = snap != null && !string.IsNullOrEmpty(snap.SelfIp4) ? snap.SelfIp4 : "分配中";
                int count = snap != null && snap.Peers != null ? snap.Peers.Count : 0;
                devicesGuideTitleLabel.Text = "跨网直连在线 · " + account + " (" + ipText + ")";
                devicesGuideSubLabel.ForeColor = Theme.Muted;
                devicesGuideSubLabel.Text = hostReady
                    ? ("已对接本机被控端口 " + hostPort + " · 发现 " + count + " 台跨网设备")
                    : ("跨网通道已在线（发现 " + count + " 台设备），本机被控端口初始化中");
                if (actions != null) actions.SetMode(2, embeddedTsBusy, false);
            }
            else if (isWaiting || embeddedTsBusy)
            {
                if (devicesGuideIcon != null) devicesGuideIcon.SetColors(Theme.WarningLight, Theme.Warning);
                devicesGuideTitleLabel.Text = hasUrl ? "请在浏览器完成 Tailscale 授权" : "正在启动内置跨网通道…";
                devicesGuideSubLabel.ForeColor = Theme.Warning;
                devicesGuideSubLabel.Text = hasUrl
                    ? "授权完成后同账号电脑与手机/平板将自动显示在下方列表。"
                    : (!string.IsNullOrEmpty(snap != null ? snap.Error : null) ? snap.Error : "正在初始化用户态跨网通道…");
                if (actions != null) actions.SetMode(1, embeddedTsBusy, hasUrl);
            }
            else
            {
                if (devicesGuideIcon != null) devicesGuideIcon.SetColors(Theme.AccentLight, Theme.Accent);
                devicesGuideTitleLabel.Text = "添加你的其他设备";
                string err = snap != null ? snap.Error : "";
                if (!string.IsNullOrEmpty(err))
                {
                    devicesGuideSubLabel.ForeColor = Theme.Alert;
                    devicesGuideSubLabel.Text = err;
                }
                else
                {
                    devicesGuideSubLabel.ForeColor = Theme.Muted;
                    devicesGuideSubLabel.Text = "在另一台电脑或手机打开 BoBo远程（或登录同一 Tailscale 账号），即可在此一键连接。";
                }
                if (actions != null) actions.SetMode(0, embeddedTsBusy, false);
            }
            devicesGuideCard.Invalidate();
        }

        private async Task HandleCrossNetCardClickAsync()
        {
            ShowPage(AllDevicesPage);
            EmbeddedTailnetSnapshot snap = BoBoEmbeddedTailnet.Default.LatestSnapshot;
            string st = snap != null ? (snap.BackendState ?? "") : "";
            if (string.Equals(st, "Running", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(snap.SelfIp4))
                    CopyHostAddress("内置 Tailscale IP", snap.SelfIp4);
                await RefreshDashboardAsync();
                return;
            }
            if (snap != null && !string.IsNullOrEmpty(snap.AuthUrl))
            {
                OpenEmbeddedAuthUrlInBrowser();
                return;
            }
            await BeginEmbeddedTailnetLoginAsync();
        }

        private async Task BeginEmbeddedTailnetLoginAsync()
        {
            if (embeddedTsBusy) return;
            embeddedTsBusy = true;
            ApplyEmbeddedTailnetUi(BoBoEmbeddedTailnet.Default.LatestSnapshot);
            SetDevicesHint("正在启动内置免安装跨网引擎，请稍候…", Theme.Muted);
            try
            {
                EmbeddedTailnetSnapshot snap = await BoBoEmbeddedTailnet.Default.BeginLoginAsync(System.Threading.CancellationToken.None);
                if (IsDisposed) return;
                ApplyEmbeddedTailnetUi(snap);
                if (snap != null && !string.IsNullOrEmpty(snap.AuthUrl))
                {
                    OpenEmbeddedAuthUrlInBrowser();
                    SetDevicesHint("已在默认浏览器打开 Tailscale 登录页，完成登录后设备将自动上线。", Theme.Accent);
                }
                else if (snap != null && string.Equals(snap.BackendState, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    SetDevicesHint("内置跨网通道已上线（" + snap.SelfIp4 + "）。", Theme.Success);
                    await RefreshDashboardAsync();
                }
                else if (snap != null && !string.IsNullOrEmpty(snap.Error))
                {
                    SetDevicesHint(snap.Error, Theme.Warning);
                }
            }
            finally
            {
                embeddedTsBusy = false;
                if (!IsDisposed)
                    ApplyEmbeddedTailnetUi(BoBoEmbeddedTailnet.Default.LatestSnapshot);
            }
        }

        private void OpenEmbeddedAuthUrlInBrowser()
        {
            string url = lastEmbeddedAuthUrl;
            if (string.IsNullOrEmpty(url))
            {
                EmbeddedTailnetSnapshot cur = BoBoEmbeddedTailnet.Default.LatestSnapshot;
                url = cur != null ? cur.AuthUrl : "";
            }
            if (string.IsNullOrEmpty(url)) return;
            string err;
            if (Engines.TryOpenUrl(url, out err))
                SetDevicesHint("已打开 Tailscale 登录页；完成后等待 2~3 秒自动同步。", Theme.Accent);
            else
                SetDevicesHint("无法自动拉起浏览器：" + err + "，请点「复制链接」后粘贴打开。", Theme.Warning);
        }

        private void CopyEmbeddedAuthUrlToClipboard()
        {
            if (string.IsNullOrEmpty(lastEmbeddedAuthUrl)) return;
            try
            {
                Clipboard.SetText(lastEmbeddedAuthUrl);
                SetDevicesHint("已复制 Tailscale 登录授权链接。", Theme.Success);
            }
            catch
            {
                SetDevicesHint("无法访问剪贴板，请直接点击「打开登录页」。", Theme.Warning);
            }
        }

        private async Task CancelOrLogoutEmbeddedTailnetAsync()
        {
            if (embeddedTsBusy) return;
            embeddedTsBusy = true;
            ApplyEmbeddedTailnetUi(BoBoEmbeddedTailnet.Default.LatestSnapshot);
            try
            {
                await BoBoEmbeddedTailnet.Default.LogoutAsync(System.Threading.CancellationToken.None);
                if (!IsDisposed)
                {
                    SetDevicesHint("已退出内置跨网账号。", Theme.Muted);
                    await RefreshDashboardAsync();
                }
            }
            finally
            {
                embeddedTsBusy = false;
                if (!IsDisposed)
                    ApplyEmbeddedTailnetUi(BoBoEmbeddedTailnet.Default.LatestSnapshot);
            }
        }

        private void SetDevicesHint(string text, Color color)
        {
            if (devicesHintLabel == null) return;
            devicesHintLabel.ForeColor = color;
            devicesHintLabel.Text = text ?? "";
        }

        private void SyncDevicesResponsiveMetrics(int contentWidth)
        {
            if (!initialScaleApplied || devicesDesk == null) return;
            if (devicesGuideRow >= 0 && !devicesGuideDismissed)
                devicesDesk.RowStyles[devicesGuideRow].Height = DevicesGuideCardHeight();
            RecalculateDevicesDeskHeight();
        }

        private static int SumRows(Control grid)
        {
            var table = grid as TableLayoutPanel;
            if (table == null) return 0;
            float height = table.Padding.Vertical;
            foreach (RowStyle style in table.RowStyles)
            {
                if (style.SizeType == SizeType.Absolute)
                    height += style.Height;
            }
            return (int)Math.Ceiling(height);
        }

        private void RecalculateDevicesDeskHeight()
        {
            if (devicesDesk == null) return;
            devicesDesk.Height = SumRows(devicesDesk);
            devicesDesk.PerformLayout();
        }

        private int DevicePixels(int logical)
        {
            return (int)Math.Round(logical * CurrentDpiScale());
        }

        private void CopyHostAddress(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SetDevicesHint("本机暂未获取到" + label + "。", Theme.Warning);
                SetStatus("本机暂未获取到" + label + "。", Theme.PillTone.Warning);
                return;
            }
            try
            {
                Clipboard.SetText(value);
                string ok = "已复制本机" + label + "到剪贴板。";
                SetDevicesHint(ok, Theme.Success);
                SetStatus(ok, Theme.PillTone.Success);
            }
            catch
            {
                SetDevicesHint("无法写入剪贴板，请手动复制。", Theme.Warning);
            }
        }

        private async Task RefreshDashboardAsync()
        {
            await RefreshHostDiagnosticsAsync();
            await RefreshNetworkDevices();
        }

        private async Task RefreshHostDiagnosticsAsync()
        {
            try
            {
                HostSnapshot snap = await Task.Run(() => HostNetworkInfo.Collect());
                if (IsDisposed) return;
                hostSnapshot = snap;
                ApplyHostSnapshotToUi();
            }
            catch { }
        }

        private void ApplyHostSnapshotToUi()
        {
            ApplyEmbeddedTailnetUi(BoBoEmbeddedTailnet.Default.LatestSnapshot);
            UpdateLocalAssistAddressLabel();
            UpdateDiagnosticsUi();
            UpdatePrivacyIndicators();
        }

        private static string ShortenIpv6(string ipv6)
        {
            if (string.IsNullOrEmpty(ipv6) || ipv6.Length <= 24) return ipv6;
            return ipv6.Substring(0, 12) + "…" + ipv6.Substring(ipv6.Length - 8);
        }

        private HashSet<string> CollectLocalIpSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add("127.0.0.1");
            set.Add("::1");
            if (!string.IsNullOrWhiteSpace(hostSnapshot.LanIpv4)) set.Add(hostSnapshot.LanIpv4.Trim());
            if (!string.IsNullOrWhiteSpace(hostSnapshot.TailscaleIpv4)) set.Add(hostSnapshot.TailscaleIpv4.Trim());
            if (!string.IsNullOrWhiteSpace(hostSnapshot.PublicIpv6)) set.Add(hostSnapshot.PublicIpv6.Trim());
            EmbeddedTailnetSnapshot emb = BoBoEmbeddedTailnet.Default.LatestSnapshot;
            if (emb != null && !string.IsNullOrWhiteSpace(emb.SelfIp4)) set.Add(emb.SelfIp4.Trim());
            try
            {
                System.Net.NetworkInformation.NetworkInterface[] nics =
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    var props = nics[i].GetIPProperties();
                    if (props == null) continue;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua != null && ua.Address != null)
                            set.Add(ua.Address.ToString());
                    }
                }
            }
            catch { }
            return set;
        }

        private static bool IsMobilePeer(TsPeer peer)
        {
            if (peer == null) return false;
            string os = (peer.Os ?? "").ToLowerInvariant();
            if (os.Contains("android") || os.Contains("ios") || os.Contains("iphone") || os.Contains("ipad"))
                return true;
            string link = (peer.LinkText ?? "").ToLowerInvariant();
            return link.Contains("android") || link.Contains("ios");
        }

        private static void MergeDedupPeer(
            List<TsPeer> targetList,
            Dictionary<string, int> keyIndexMap,
            HashSet<string> seenIps,
            HashSet<string> localIps,
            string selfMachineKey,
            DeviceCatalogStore catalog,
            TsPeer candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Ip)) return;
            string ip = candidate.Ip.Trim();
            if (localIps != null && localIps.Contains(ip)) return;

            string machineKey = DeviceCatalogStore.NormalizePeerKey(candidate.HostName);
            if (!string.IsNullOrEmpty(selfMachineKey) && string.Equals(machineKey, selfMachineKey, StringComparison.Ordinal))
                return;

            if (catalog != null && catalog.IsHidden(ip, candidate.HostName))
                return;

            if (!seenIps.Add(ip)) return;

            if (!string.IsNullOrEmpty(machineKey))
            {
                int existingIdx;
                if (keyIndexMap.TryGetValue(machineKey, out existingIdx))
                {
                    TsPeer prev = targetList[existingIdx];
                    if (!prev.Online && candidate.Online)
                    {
                        targetList[existingIdx] = candidate;
                    }
                    return;
                }
                keyIndexMap[machineKey] = targetList.Count;
            }
            targetList.Add(candidate);
        }

        private async Task RefreshNetworkDevices()
        {
            if (refreshDevices == null || !refreshDevices.Enabled) return;
            refreshDevices.Enabled = false;
            try
            {
                if (boboHostServer != null) boboHostServer.SendDiscoveryProbeNow();
                var result = await Task.Run(() => Tailscale.LoadPeers(4200));
                if (IsDisposed) return;

                var merged = new List<TsPeer>();
                var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenMachineKeys = new Dictionary<string, int>(StringComparer.Ordinal);
                HashSet<string> localIps = CollectLocalIpSet();
                string selfMachineKey = DeviceCatalogStore.NormalizePeerKey(Environment.MachineName);

                // 1. Live LAN auto-discovered BoBo V6 peers (fastest path)
                if (boboHostServer != null)
                {
                    foreach (BoBoDiscoveredPeer lp in boboHostServer.GetDiscoveredLanPeers())
                    {
                        if (string.IsNullOrWhiteSpace(lp.Host)) continue;
                        string platTag = "Windows";
                        if (string.Equals(lp.Platform, BoBoProtocol.PlatformMacOS, StringComparison.OrdinalIgnoreCase)) platTag = "macOS";
                        else if (string.Equals(lp.Platform, BoBoProtocol.PlatformAndroid, StringComparison.OrdinalIgnoreCase)) platTag = "Android";
                        else if (string.Equals(lp.Platform, BoBoProtocol.PlatformIOS, StringComparison.OrdinalIgnoreCase)) platTag = "iOS";
                        bool isViewOnly = string.Equals(lp.HostControlMode, BoBoProtocol.ControlModeViewOnly, StringComparison.OrdinalIgnoreCase);
                        var lanPeer = new TsPeer
                        {
                            HostName = lp.DeviceName,
                            Ip = lp.Host,
                            Os = platTag,
                            Online = lp.AllowIncoming,
                            LinkText = lp.AllowIncoming
                                ? (platTag + (isViewOnly ? " · 仅投屏观看" : " · 局域网直连") + " · 码 " + BoBoIdentityStore.FormatDeviceCode(lp.DeviceCode))
                                : (platTag + " · 局域网 · 对方已关闭被控")
                        };
                        MergeDedupPeer(merged, seenMachineKeys, seenIps, localIps, selfMachineKey, deviceCatalog, lanPeer);
                    }
                }

                // 2. Tailscale peers (deduplicated across LAN / BoBoTsNet / system Tailscale)
                if (result.Ok && result.Peers != null)
                {
                    foreach (TsPeer tp in result.Peers)
                    {
                        MergeDedupPeer(merged, seenMachineKeys, seenIps, localIps, selfMachineKey, deviceCatalog, tp);
                    }
                }

                // 3. Previously authorized BoBo V6 peers with saved IP
                foreach (BoBoAuthorizedPeer ap in BoBoIdentityStore.Default.ListAuthorizedPeers())
                {
                    if (string.IsNullOrWhiteSpace(ap.LastHost)) continue;
                    var authPeer = new TsPeer
                    {
                        HostName = string.IsNullOrWhiteSpace(ap.DeviceName) ? ap.LastHost : ap.DeviceName,
                        Ip = ap.LastHost,
                        Online = true,
                        LinkText = "已授权设备 · 码 " + BoBoIdentityStore.FormatDeviceCode(ap.DeviceCode)
                    };
                    MergeDedupPeer(merged, seenMachineKeys, seenIps, localIps, selfMachineKey, deviceCatalog, authPeer);
                }

                // 4. Manually added devices from DeviceCatalogStore
                if (deviceCatalog != null)
                {
                    for (int i = 0; i < deviceCatalog.ManualDevices.Count; i++)
                    {
                        ManagedDeviceItem m = deviceCatalog.ManualDevices[i];
                        if (string.IsNullOrWhiteSpace(m.Address)) continue;
                        string resolvedIp = ResolveBoBoHostTarget(m.Address);
                        if (!string.IsNullOrEmpty(resolvedIp) && seenIps.Contains(resolvedIp)) continue;
                        var manualPeer = new TsPeer
                        {
                            HostName = string.IsNullOrWhiteSpace(m.Alias) ? m.Address : m.Alias,
                            Ip = m.Address,
                            Os = m.IsMobile ? "ios" : "windows",
                            Online = true,
                            LinkText = "手动添加设备"
                        };
                        MergeDedupPeer(merged, seenMachineKeys, seenIps, localIps, selfMachineKey, deviceCatalog, manualPeer);
                    }
                }

                cachedPeers.Clear();
                cachedPeers.AddRange(merged);
                DrawPeerRows(merged, merged.Count == 0 && !result.Ok ? result.Error : null);
                UpdateQuickDeviceShortcut();

                bool serverReady = boboHostServer != null && boboHostServer.IsRunning && boboHostServer.AllowIncomingControl;
                if (boboHostServer != null && boboHostServer.IsRunning)
                {
                    string portText = boboHostServer.IsUsingFallbackPort
                        ? ("备用端口 " + boboHostServer.ListeningPort)
                        : ("端口 " + boboHostServer.ListeningPort);
                    SetDevicesHint(
                        serverReady
                            ? ("本机码 " + BoBoIdentityStore.Default.FormattedDeviceCode + " · " + portText + " 就绪")
                            : "已暂停本机被控（可在「设置 → 网络与服务」开启）",
                        serverReady ? Theme.Muted : Theme.Warning);
                }
                else
                {
                    SetDevicesHint("本机被控端口暂未启动 · 点击「开始协助」可自动修复或切换备用端口", Theme.Warning);
                }
            }
            catch
            {
                if (!IsDisposed)
                    SetDevicesHint("暂未获取同网列表 · 可点右上角「添加设备」或「开始协助」直连", Theme.Warning);
            }
            finally { if (!IsDisposed) refreshDevices.Enabled = true; }
        }

        private void UpdateQuickDeviceShortcut()
        {
            TsPeer best = null;
            foreach (var p in cachedPeers)
            {
                if (string.IsNullOrWhiteSpace(p.Ip)) continue;
                if (best == null || (p.Online && !best.Online)) best = p;
            }
            if (best != null)
            {
                quickDeviceName = deviceCatalog != null
                    ? deviceCatalog.ResolveDisplayName(best.Ip, best.HostName, PeerName(best))
                    : PeerName(best);
                quickDeviceAddress = best.Ip;
                quickDeviceOnline = best.Online;
                quickDeviceLink = best.LinkText;
                quickDeviceIsStream = true;
            }
            else if (favorites.Count > 0)
            {
                var f = favorites[0];
                quickDeviceName = deviceCatalog != null
                    ? deviceCatalog.ResolveDisplayName(f.Address, f.Address, f.Address)
                    : f.Address;
                quickDeviceAddress = f.Address;
                quickDeviceOnline = true;
                quickDeviceLink = f.Stream ? "收藏串流主机" : "收藏协助设备";
                quickDeviceIsStream = f.Stream;
            }
            else if (recent.MoonlightHosts.Count > 0)
            {
                quickDeviceName = recent.MoonlightHosts[0];
                quickDeviceAddress = recent.MoonlightHosts[0];
                quickDeviceOnline = true;
                quickDeviceLink = "最近连接主机";
                quickDeviceIsStream = true;
            }
            else
            {
                quickDeviceName = "";
                quickDeviceAddress = "";
            }

            if (navQuickDevice != null)
            {
                navQuickDevice.Text = string.IsNullOrEmpty(quickDeviceName) ? "暂无设备" : quickDeviceName;
                navQuickDevice.AccessibleName = navQuickDevice.Text;
                Theme.ChangeGlyph(navQuickDevice, best != null ? PeerOsGlyph(best) : Theme.Glyph.WindowsTile);
                Theme.SetQuickDeviceIndicator(navQuickDevice, !string.IsNullOrEmpty(quickDeviceAddress), quickDeviceOnline);
            }
            SyncNavigationSectionVisibility();
        }

        private void DrawPeerRows(List<TsPeer> peers, string issue = null)
        {
            if (peerListHost == null) return;
            var pcPeers = new List<TsPeer>();
            var mobPeers = new List<TsPeer>();
            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    TsPeer p = peers[i];
                    if (string.IsNullOrWhiteSpace(p.Ip)) continue;
                    if (IsMobilePeer(p)) mobPeers.Add(p);
                    else pcPeers.Add(p);
                }
            }

            float scale = CurrentDpiScale();
            peerListHost.SuspendLayout();
            try
            {
                for (int i = peerListHost.Controls.Count - 1; i >= 0; i--) peerListHost.Controls[i].Dispose();
                peerListHost.RowStyles.Clear();
                peerListHost.RowCount = 0;

                Control selfCard = BuildThisPcCard();
                if (scale != 1f) selfCard.Scale(new SizeF(scale, scale));
                Theme.Row(peerListHost, 60f * scale, selfCard);

                for (int i = 0; i < pcPeers.Count; i++)
                {
                    Theme.Row(peerListHost, 8f * scale);
                    Control card = BuildPeerDeviceCard(pcPeers[i]);
                    if (scale != 1f) card.Scale(new SizeF(scale, scale));
                    Theme.Row(peerListHost, 60f * scale, card);
                }

                if (computersGroupBtn != null)
                    computersGroupBtn.Text = "电脑 " + (1 + pcPeers.Count);
            }
            finally
            {
                peerListHost.ResumeLayout(true);
            }

            hasMobilePeers = mobPeers.Count > 0;
            if (mobileListHost != null)
            {
                mobileListHost.SuspendLayout();
                try
                {
                    for (int i = mobileListHost.Controls.Count - 1; i >= 0; i--) mobileListHost.Controls[i].Dispose();
                    mobileListHost.RowStyles.Clear();
                    mobileListHost.RowCount = 0;

                    for (int i = 0; i < mobPeers.Count; i++)
                    {
                        if (i > 0) Theme.Row(mobileListHost, 8f * scale);
                        Control card = BuildPeerDeviceCard(mobPeers[i]);
                        if (scale != 1f) card.Scale(new SizeF(scale, scale));
                        Theme.Row(mobileListHost, 60f * scale, card);
                    }
                }
                finally
                {
                    mobileListHost.ResumeLayout(true);
                }
                if (mobilesGroupBtn != null)
                    mobilesGroupBtn.Text = "手机/平板 " + mobPeers.Count;
            }

            SyncPeerListLayout();
        }

        private void SyncPeerListLayout()
        {
            if (devicesDesk == null || peerListHost == null || peerListRow < 0) return;
            float scale = CurrentDpiScale();
            peerListHost.Visible = computersExpanded;
            devicesDesk.RowStyles[peerListRow].Height = computersExpanded ? SumRows(peerListHost) : 0;

            if (mobileSpacerRow >= 0 && mobileGroupRow >= 0 && mobileGroupGapRow >= 0 && mobileListRow >= 0 && mobileListHost != null)
            {
                if (mobilesGroupBtn != null && mobilesGroupBtn.Parent != null)
                    mobilesGroupBtn.Parent.Visible = hasMobilePeers;
                mobileListHost.Visible = hasMobilePeers && mobilesExpanded;
                devicesDesk.RowStyles[mobileSpacerRow].Height = hasMobilePeers ? 14f * scale : 0f;
                devicesDesk.RowStyles[mobileGroupRow].Height = hasMobilePeers ? 28f * scale : 0f;
                devicesDesk.RowStyles[mobileGroupGapRow].Height = (hasMobilePeers && mobilesExpanded) ? 8f * scale : 0f;
                devicesDesk.RowStyles[mobileListRow].Height = (hasMobilePeers && mobilesExpanded) ? SumRows(mobileListHost) : 0f;
            }

            RecalculateDevicesDeskHeight();
            if (devicesViewport != null) devicesViewport.RefreshLayout();
            NotifyOnboardingLayoutChanged();
        }

        private Control BuildThisPcCard()
        {
            var card = new Theme.Card { Padding = new Padding(16, 10, 14, 10), Interactive = true };
            var row = Theme.Grid(6);
            Theme.FixedColumn(row, 38);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 32);
            Theme.FixedColumn(row, 28);

            bool hostReady = allowIncomingControl && boboHostServer != null && boboHostServer.IsRunning;
            var icon = new Theme.IconTile(Theme.Glyph.WindowsTile, false, Theme.Accent, Theme.Paper, 8f)
            {
                Status = hostReady ? Theme.TileStatus.Online : Theme.TileStatus.None
            };
            string tag = "本机";
            if (!allowIncomingControl) tag = "本机 · 已暂停被控";
            else if (boboHostServer == null || !boboHostServer.IsRunning) tag = "本机 · 端口未就绪";
            var selfTitle = new Theme.SelfDeviceTitle(Environment.MachineName, tag, Theme.PillTone.Info);

            var infoBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Info);
            infoBtn.AccessibleName = "查看本机网络与隐藏设备管理";
            infoBtn.Margin = new Padding(0, 4, 0, 4);
            infoBtn.Click += delegate { ShowThisPcMenu(infoBtn); };

            var openBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.ChevronRight);
            openBtn.AccessibleName = "前往本机远程协助与配对";
            openBtn.Margin = new Padding(0, 4, 0, 4);
            openBtn.Click += delegate { ShowPage(AssistPage); };

            Theme.Row(row, 38, icon, null, selfTitle, null, infoBtn, openBtn);
            card.Controls.Add(row);
            card.AttachInteractiveHover(row);
            selfTitle.Cursor = Cursors.Hand;
            selfTitle.Click += delegate { ShowPage(AssistPage); };
            return card;
        }

        private Control BuildPeerDeviceCard(TsPeer peer)
        {
            var card = new Theme.Card { Padding = new Padding(16, 10, 14, 10), Interactive = true };
            string displayName = deviceCatalog != null
                ? deviceCatalog.ResolveDisplayName(peer.Ip, peer.HostName, PeerName(peer))
                : PeerName(peer);
            bool hasLanTag = !string.IsNullOrEmpty(peer.LinkText) && peer.LinkText.IndexOf("局域网", StringComparison.Ordinal) >= 0;

            var row = Theme.Grid(9);
            Theme.FixedColumn(row, 38);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 78);
            Theme.FixedColumn(row, 6);
            Theme.FixedColumn(row, 32);
            Theme.FixedColumn(row, 2);
            Theme.FixedColumn(row, 28);

            var icon = new Theme.IconTile(
                PeerOsGlyph(peer),
                !peer.Online,
                peer.Online ? Theme.Accent : Theme.SurfaceSubtle,
                peer.Online ? Theme.Paper : Theme.Muted,
                8f);
            icon.Status = peer.Online ? Theme.TileStatus.Online : Theme.TileStatus.Offline;

            Theme.PillTone stateTone = peer.Online ? Theme.PillTone.Success : Theme.PillTone.Neutral;
            string primaryPill = peer.Online ? "在线" : "离线";
            string secondaryPill = (peer.Online && hasLanTag) ? "局域网" : null;
            var titleCtrl = new Theme.SelfDeviceTitle(displayName, primaryPill, stateTone, secondaryPill);
            titleCtrl.Cursor = Cursors.Hand;
            titleCtrl.Click += delegate { OpenDeviceDetail(displayName, peer.Ip, peer.Online, peer.LinkText, true); };
            titleCtrl.DoubleClick += async delegate
            {
                if (peer.Online) await ConnectToDeviceAsync(peer.Ip, displayName);
            };
            deviceCardToolTip.SetToolTip(titleCtrl, peer.Ip + " · " + peer.LinkText);

            var connectBtn = Theme.Action(peer.Online ? "连接" : "离线",
                peer.Online ? Theme.ButtonStyle.AccentSoft : Theme.ButtonStyle.Secondary,
                peer.Online ? Theme.Glyph.Arrow : Theme.Glyph.None);
            connectBtn.Enabled = peer.Online;
            connectBtn.Margin = new Padding(0, 3, 0, 3);
            connectBtn.Click += async delegate { await ConnectToDeviceAsync(peer.Ip, displayName); };

            var infoBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Info);
            infoBtn.AccessibleName = "管理此设备";
            infoBtn.Margin = new Padding(0, 4, 0, 4);
            infoBtn.Click += delegate { ShowPeerActionMenu(infoBtn, peer, displayName); };

            var detailBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.ChevronRight);
            detailBtn.AccessibleName = "查看设备详情";
            detailBtn.Margin = new Padding(0, 4, 0, 4);
            detailBtn.Click += delegate { OpenDeviceDetail(displayName, peer.Ip, peer.Online, peer.LinkText, true); };

            Theme.Row(row, 38, icon, null, titleCtrl, null, connectBtn, null, infoBtn, null, detailBtn);
            card.Controls.Add(row);
            card.AttachInteractiveHover(row);
            icon.MouseUp += delegate(object sender, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Right) ShowPeerActionMenu(card, peer, displayName);
            };
            titleCtrl.MouseUp += delegate(object sender, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Right) ShowPeerActionMenu(card, peer, displayName);
            };
            return card;
        }

        private void ShowThisPcMenu(Control anchor)
        {
            var menu = CreateCleanPopupMenu();
            bool serverUp = boboHostServer != null && boboHostServer.IsRunning;
            string portText = serverUp ? ("端口 " + boboHostServer.ListeningPort + (boboHostServer.IsUsingFallbackPort ? " (备用)" : "")) : "未启动";
            var header = new ToolStripMenuItem("本机：" + Environment.MachineName + " · " + portText) { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            string code = BoBoIdentityStore.Default.FormattedDeviceCode;
            AddPopupItem(menu, "复制本机设备码：" + code, delegate { CopyHostAddress("本机设备码", code); }, Theme.Glyph.Copy);
            if (!string.IsNullOrEmpty(hostSnapshot.LanIpv4))
                AddPopupItem(menu, "复制局域网 IP：" + hostSnapshot.LanIpv4, delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); }, Theme.Glyph.Copy);
            if (!string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4))
                AddPopupItem(menu, "复制跨网 IP：" + hostSnapshot.TailscaleIpv4, delegate { CopyHostAddress("跨网 IP", hostSnapshot.TailscaleIpv4); }, Theme.Glyph.Copy);
            AddPopupItem(menu, "前往「开始协助」生成 4 位配对码", delegate { ShowPage(AssistPage); }, Theme.Glyph.Key);

            int hiddenCount = deviceCatalog != null ? deviceCatalog.HiddenKeys.Count : 0;
            if (hiddenCount > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                AddPopupItem(menu, "恢复已隐藏的设备（" + hiddenCount + " 条规则）", async delegate
                {
                    deviceCatalog.UnhideAll();
                    deviceCatalog.Save();
                    SetDevicesHint("已恢复所有此前隐藏的设备，正在刷新列表…", Theme.Success);
                    await RefreshNetworkDevices();
                }, Theme.Glyph.Refresh);
            }

            Point pt = anchor.PointToScreen(new Point(0, anchor.Height + DevicePixels(4)));
            menu.Show(pt);
        }

        private void ShowPeerActionMenu(Control anchor, TsPeer peer, string displayName)
        {
            if (peer == null) return;
            var menu = CreateCleanPopupMenu();
            var infoHeader = new ToolStripMenuItem(peer.Ip + "  ·  " + peer.LinkText) { Enabled = false };
            menu.Items.Add(infoHeader);
            menu.Items.Add(new ToolStripSeparator());

            if (peer.Online)
            {
                AddPopupItem(menu, "连接远程桌面", async delegate { await ConnectToDeviceAsync(peer.Ip, displayName); }, Theme.Glyph.Arrow);
            }
            AddPopupItem(menu, "发送文件到此设备", delegate
            {
                remoteId.Text = peer.Ip;
                ShowPage(AssistPage);
                SelectAssistMode(1);
            }, Theme.Glyph.Send);
            AddPopupItem(menu, "修改备注名称…", delegate { ShowRenameDeviceDialog(peer, displayName); }, Theme.Glyph.Sliders);
            AddPopupItem(menu, "复制设备地址：" + peer.Ip, delegate { CopyHostAddress("设备地址", peer.Ip); }, Theme.Glyph.Copy);

            bool isFav = favorites.Exists(f => f.Stream && string.Equals(f.Address, peer.Ip, StringComparison.OrdinalIgnoreCase));
            AddPopupItem(menu, isFav ? "移出收藏设备" : "加入收藏设备", delegate
            {
                var item = new SavedDevice(true, peer.Ip);
                if (isFav) favorites.RemoveAll(f => f.SameAs(item));
                else favorites.Insert(0, item);
                SaveFavorites();
            }, isFav ? Theme.Glyph.StarFilled : Theme.Glyph.Star);

            menu.Items.Add(new ToolStripSeparator());
            AddPopupItem(menu, "移除 / 隐藏此设备", delegate { RemoveOrHidePeer(peer, displayName); }, Theme.Glyph.Trash, true);

            Point pt = anchor.PointToScreen(new Point(0, anchor.Height + DevicePixels(4)));
            menu.Show(pt);
        }

        private void RemoveOrHidePeer(TsPeer peer, string displayName)
        {
            if (peer == null || deviceCatalog == null) return;
            deviceCatalog.HideDevice(peer.Ip, peer.HostName);
            deviceCatalog.Save();
            cachedPeers.RemoveAll(p => string.Equals(p.Ip, peer.Ip, StringComparison.OrdinalIgnoreCase));
            DrawPeerRows(cachedPeers);
            UpdateQuickDeviceShortcut();
            SetDevicesHint("已移除「" + displayName + "」（在本机 (i) 菜单中可随时恢复）。", Theme.Muted);
        }

        private void ShowAddDeviceDialog()
        {
            using (var dlg = new Form())
            {
                dlg.SuspendLayout();
                dlg.Text = "添加设备";
                dlg.AutoScaleMode = AutoScaleMode.Dpi;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(440, 286);
                dlg.BackColor = Theme.Workspace;
                if (appIcon != null) dlg.Icon = appIcon;

                var root = Theme.Grid(1);
                Theme.FillColumn(root);
                root.Padding = new Padding(22, 18, 22, 18);

                Theme.Row(root, 22, Theme.Text("设备地址或 9 位设备码", Theme.TextSize.Small, Theme.Muted));
                Theme.Row(root, 6);
                TextBox addrInput = Theme.InputBox("设备地址", "");
                Theme.Cue(addrInput, "例如 192.168.1.20、100.x.x.x 或 9 位 BoBo 设备码");
                Theme.Row(root, 38, Theme.WrapInput(addrInput));
                Theme.Row(root, 12);

                Theme.Row(root, 22, Theme.Text("备注名称（选填）", Theme.TextSize.Small, Theme.Muted));
                Theme.Row(root, 6);
                TextBox aliasInput = Theme.InputBox("备注名称", "");
                Theme.Cue(aliasInput, "例如：家里台式机 / 办公室笔记本");
                Theme.Row(root, 38, Theme.WrapInput(aliasInput));
                Theme.Row(root, 12);

                var typeRow = Theme.Grid(3);
                Theme.FixedColumn(typeRow, 76);
                Theme.FixedColumn(typeRow, 10);
                Theme.FillColumn(typeRow);
                ComboBox typeChoice = Theme.Choice("设备类型", false);
                typeChoice.Items.AddRange(new object[] { "电脑 (Windows / macOS)", "手机 / 平板 (Android / iOS)" });
                typeChoice.SelectedIndex = 0;
                Theme.Row(typeRow, 36, Theme.Text("设备类型", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(typeChoice));
                Theme.Row(root, 36, typeRow);
                Theme.Row(root, 16);

                var btnRow = Theme.Grid(4);
                Theme.FillColumn(btnRow);
                Theme.FixedColumn(btnRow, 86);
                Theme.FixedColumn(btnRow, 10);
                Theme.FixedColumn(btnRow, 104);
                var cancelBtn = Theme.Action("取消", Theme.ButtonStyle.Secondary);
                cancelBtn.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
                var okBtn = Theme.Action("添加设备", Theme.ButtonStyle.Primary);
                okBtn.Click += delegate
                {
                    string err;
                    string clean = NormalizeFavoriteTarget(addrInput.Text, out err);
                    if (clean == null)
                    {
                        MessageBox.Show(dlg, err ?? "请输入有效的 IP 地址或 9 位 BoBo 设备码。", "格式无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        addrInput.Focus();
                        return;
                    }
                    if (deviceCatalog != null)
                    {
                        deviceCatalog.AddOrUpdateManual(clean, aliasInput.Text, typeChoice.SelectedIndex == 1);
                        deviceCatalog.Save();
                    }
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };
                Theme.Row(btnRow, 36, null, cancelBtn, null, okBtn);
                Theme.Row(root, 36, btnRow);

                dlg.Controls.Add(root);
                dlg.AcceptButton = okBtn;
                dlg.CancelButton = cancelBtn;
                dlg.AutoScaleDimensions = new SizeF(96f, 96f);
                dlg.ResumeLayout(false);
                dlg.PerformAutoScale();
                dlg.PerformLayout();

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    SetDevicesHint("已添加设备到列表，正在更新状态…", Theme.Success);
                    var _ = RefreshNetworkDevices();
                }
            }
        }

        private void ShowRenameDeviceDialog(TsPeer peer, string currentName)
        {
            using (var dlg = new Form())
            {
                dlg.SuspendLayout();
                dlg.Text = "修改设备备注";
                dlg.AutoScaleMode = AutoScaleMode.Dpi;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(380, 174);
                dlg.BackColor = Theme.Workspace;
                if (appIcon != null) dlg.Icon = appIcon;

                var root = Theme.Grid(1);
                Theme.FillColumn(root);
                root.Padding = new Padding(22, 18, 22, 16);
                Theme.Row(root, 22, Theme.Text("为 " + peer.Ip + " 设置备注（留空恢复默认）", Theme.TextSize.Small, Theme.Muted));
                Theme.Row(root, 8);
                TextBox box = Theme.InputBox("备注名称", currentName);
                Theme.Row(root, 38, Theme.WrapInput(box));
                Theme.Row(root, 16);

                var btnRow = Theme.Grid(4);
                Theme.FillColumn(btnRow);
                Theme.FixedColumn(btnRow, 82);
                Theme.FixedColumn(btnRow, 10);
                Theme.FixedColumn(btnRow, 88);
                var cancelBtn = Theme.Action("取消", Theme.ButtonStyle.Secondary);
                cancelBtn.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
                var saveBtn = Theme.Action("保存", Theme.ButtonStyle.Primary);
                saveBtn.Click += delegate
                {
                    if (deviceCatalog != null)
                    {
                        deviceCatalog.SetAlias(peer.Ip, peer.HostName, box.Text);
                        deviceCatalog.Save();
                    }
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };
                Theme.Row(btnRow, 36, null, cancelBtn, null, saveBtn);
                Theme.Row(root, 36, btnRow);

                dlg.Controls.Add(root);
                dlg.AcceptButton = saveBtn;
                dlg.CancelButton = cancelBtn;
                dlg.AutoScaleDimensions = new SizeF(96f, 96f);
                dlg.ResumeLayout(false);
                dlg.PerformAutoScale();
                dlg.PerformLayout();

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    DrawPeerRows(cachedPeers);
                    UpdateQuickDeviceShortcut();
                }
            }
        }

        // ==================== DEVICE DETAIL PAGE (UU IMAGE 1 STYLE) ====================

        private Control BuildDeviceDetailPage()
        {
            TableLayoutPanel desk;
            detailViewport = CreatePage(640, out desk);
            detailDesk = desk;

            // Header: [在线/离线] DEVICE-NAME  ...  [收藏] [移除]
            var head = Theme.Grid(7);
            Theme.FixedColumn(head, 62);
            Theme.FixedColumn(head, 12);
            Theme.FillColumn(head);
            Theme.FixedColumn(head, 12);
            Theme.FixedColumn(head, 92);
            Theme.FixedColumn(head, 8);
            Theme.FixedColumn(head, 76);

            detailStatusBadge = new Theme.Badge("在线", Theme.PillTone.Success);
            detailTitleLabel = Theme.Text("未选择设备", Theme.TextSize.Title, Theme.Ink);
            detailFavButton = Theme.Action("收藏", Theme.ButtonStyle.Secondary, Theme.Glyph.Star);
            detailFavButton.Margin = new Padding(0, 4, 0, 4);
            detailFavButton.Click += delegate { ToggleDetailDeviceFavorite(); };

            detailRemoveButton = Theme.Action("移除", Theme.ButtonStyle.DangerSoft);
            detailRemoveButton.Margin = new Padding(0, 4, 0, 4);
            detailRemoveButton.Click += delegate
            {
                if (string.IsNullOrEmpty(detailDeviceAddress) || deviceCatalog == null) return;
                deviceCatalog.HideDevice(detailDeviceAddress, detailDeviceName);
                deviceCatalog.Save();
                cachedPeers.RemoveAll(p => string.Equals(p.Ip, detailDeviceAddress, StringComparison.OrdinalIgnoreCase));
                DrawPeerRows(cachedPeers);
                UpdateQuickDeviceShortcut();
                ShowPage(AllDevicesPage);
                SetDevicesHint("已移除「" + detailDeviceName + "」。", Theme.Muted);
            };

            Theme.Row(head, 44, detailStatusBadge, null, detailTitleLabel, null, detailFavButton, null, detailRemoveButton);
            Theme.Row(desk, 44, head);
            Theme.Row(desk, 18);

            // Hero Preview Card with attached clean bottom toolbar (EXACT UU Image 1!)
            var heroCard = new Theme.Card { Padding = new Padding(0) };
            detailHeroCard = heroCard;
            var hc = Theme.Grid(1);
            Theme.FillColumn(hc);
            detailPreviewCardLayout = hc;

            detailPreviewCanvas = new DevicePreviewCanvas();
            detailPreviewCanvas.ConnectRequested += async delegate
            {
                if (!string.IsNullOrEmpty(detailDeviceAddress))
                    await ConnectToDeviceAsync(detailDeviceAddress, detailDeviceName);
            };
            detailPreviewCanvas.DisconnectRequested += delegate
            {
                DisconnectActiveStreamSession();
            };
            detailPreviewRow = hc.RowCount;
            Theme.Row(hc, 228, detailPreviewCanvas);
            Theme.Row(hc, 1, Theme.Rule());

            // Attached 4-slot action strip: 低延迟串流 | 通用桌面 | 文件传输 | 画质设置
            var strip = Theme.Grid(7);
            Theme.FillColumn(strip);
            Theme.FixedColumn(strip, 1);
            Theme.FillColumn(strip);
            Theme.FixedColumn(strip, 1);
            Theme.FillColumn(strip);
            Theme.FixedColumn(strip, 1);
            Theme.FillColumn(strip);

            var actStream = Theme.Action("低延迟串流", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Bolt);
            Theme.SetGlyphTint(actStream, Theme.Accent);
            actStream.Click += async delegate
            {
                if (!string.IsNullOrEmpty(detailDeviceAddress))
                    await ConnectToDeviceAsync(detailDeviceAddress, detailDeviceName);
            };

            var actDesktop = Theme.Action("发送文件", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Send);
            actDesktop.Click += delegate
            {
                if (!string.IsNullOrEmpty(detailDeviceAddress)) remoteId.Text = detailDeviceAddress;
                ShowPage(AssistPage);
                SelectAssistMode(1);
            };

            var actFile = Theme.Action("管理授权", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Shield);
            actFile.Click += delegate
            {
                ShowAuthorizedPeersDialog();
            };

            var actSettings = Theme.Action("画质与抗丢包", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Sliders);
            actSettings.Click += delegate
            {
                ShowPage(SettingsPage);
                SelectSettingsTab(2);
            };

            Theme.Row(strip, 52, actStream, MakeToolbarDivider(), actDesktop, MakeToolbarDivider(), actFile, MakeToolbarDivider(), actSettings);
            Theme.Row(hc, 52, strip);
            heroCard.Controls.Add(hc);

            detailHeroCardRow = desk.RowCount;
            Theme.Row(desk, 282, heroCard);
            Theme.Row(desk, 24);

            // Quick-launch quality presets section (matches UU Image 1 "快速启动" below card)
            Theme.Row(desk, 26, Theme.Text("快速启动画质档位", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(desk, 10);

            var quickCard = new Theme.Card { Padding = new Padding(18, 18, 18, 18), DashedBorder = true };
            var qGrid = Theme.Grid(5);
            Theme.FillColumn(qGrid);
            Theme.FixedColumn(qGrid, 12);
            Theme.FillColumn(qGrid);
            Theme.FixedColumn(qGrid, 12);
            Theme.FillColumn(qGrid);

            Theme.Row(qGrid, 44,
                MakeQuickPresetButton("外网流畅 1080p60", WanWideProfile), null,
                MakeQuickPresetButton("外网高清 2K60", 2), null,
                MakeQuickPresetButton("弱网抗丢包 720p60", WeakNetProfile));
            Theme.Row(qGrid, 12);
            Theme.Row(qGrid, 44,
                MakeQuickPresetButton("局域网 2K120 高刷", 6), null,
                MakeQuickPresetButton("电竞高刷 2K144", 7), null,
                MakeQuickPresetButton("4K60 超清办公", 8));
            quickCard.Controls.Add(qGrid);
            Theme.Row(desk, 136, quickCard);

            detailViewport.ContentWidthChanged += delegate(int contentW) { AdjustDetailPreviewAspect(contentW); };
            RecalculateDetailDeskHeight();
            return detailViewport;
        }

        private static Control MakeToolbarDivider()
        {
            var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper, Padding = new Padding(0, 14, 0, 14), Margin = Padding.Empty };
            wrap.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Divider });
            return wrap;
        }

        private Button MakeQuickPresetButton(string caption, int presetIndex)
        {
            var btn = Theme.Action(caption, Theme.ButtonStyle.Secondary, Theme.Glyph.Bolt);
            btn.Click += async delegate
            {
                if (presetIndex >= 0 && presetIndex < quality.Items.Count)
                    quality.SelectedIndex = presetIndex;
                if (!string.IsNullOrEmpty(detailDeviceAddress))
                    await ConnectToDeviceAsync(detailDeviceAddress, detailDeviceName);
                else
                    ShowPage(AssistPage);
            };
            return btn;
        }

        private void AdjustDetailPreviewAspect(int contentWidth)
        {
            if (!initialScaleApplied || detailPreviewCardLayout == null || detailDesk == null || detailPreviewRow < 0 || detailHeroCardRow < 0) return;
            float scale = CurrentDpiScale();
            int innerW = Math.Max(320, contentWidth - detailDesk.Padding.Horizontal);
            int targetPreviewH = (int)Math.Round(innerW * 0.28f);
            int minH = (int)Math.Round(196 * scale);
            int maxH = (int)Math.Round(268 * scale);
            targetPreviewH = Math.Max(minH, Math.Min(maxH, targetPreviewH));

            detailPreviewCardLayout.RowStyles[detailPreviewRow].Height = targetPreviewH;
            detailDesk.RowStyles[detailHeroCardRow].Height = SumRows(detailPreviewCardLayout) + (detailHeroCard == null ? 0 : detailHeroCard.Padding.Vertical);
            RecalculateDetailDeskHeight();
        }

        private void RecalculateDetailDeskHeight()
        {
            if (detailDesk == null) return;
            detailDesk.Height = SumRows(detailDesk);
            detailDesk.PerformLayout();
        }

        private void OpenDeviceDetail(string name, string address, bool online, string link, bool isStream)
        {
            detailDeviceName = string.IsNullOrWhiteSpace(name) ? address : name;
            detailDeviceAddress = address ?? "";
            detailDeviceOnline = online;
            detailDeviceLink = link ?? "";
            detailDeviceIsStream = isStream;

            if (detailTitleLabel != null) detailTitleLabel.Text = detailDeviceName;
            if (detailStatusBadge != null)
                detailStatusBadge.UpdateStatus(online ? "在线" : "离线", online ? Theme.PillTone.Success : Theme.PillTone.Neutral);
            if (detailPreviewCanvas != null)
                detailPreviewCanvas.SetDevice(detailDeviceName, detailDeviceAddress, detailDeviceLink, online);
            SyncDetailFavoriteButton();
            SyncActiveControlIndicators();
            ShowPage(DeviceDetailPage);
        }

        private void SyncDetailFavoriteButton()
        {
            if (detailFavButton == null) return;
            bool exists = !string.IsNullOrEmpty(detailDeviceAddress) && favorites.Exists(f =>
                f.Stream == detailDeviceIsStream && string.Equals(f.Address, detailDeviceAddress, StringComparison.OrdinalIgnoreCase));
            detailFavButton.Text = exists ? "已收藏" : "收藏";
            Theme.ChangeGlyph(detailFavButton, exists ? Theme.Glyph.StarFilled : Theme.Glyph.Star);
            Theme.SetGlyphTint(detailFavButton, exists ? Theme.Accent : Theme.Muted);
        }

        private void ToggleDetailDeviceFavorite()
        {
            if (string.IsNullOrEmpty(detailDeviceAddress)) return;
            var item = new SavedDevice(detailDeviceIsStream, detailDeviceAddress);
            int idx = favorites.FindIndex(f => f.SameAs(item));
            if (idx >= 0) favorites.RemoveAt(idx);
            else favorites.Insert(0, item);
            SaveFavorites();
            SyncDetailFavoriteButton();
        }

        private sealed class DevicePreviewCanvas : Control
        {
            private readonly UiTransition ctaHoverTransition;
            private readonly UiTransition shuffleHoverTransition;
            private readonly UiTransition pressTransition;
            private string deviceName = "";
            private string deviceAddress = "";
            private string linkText = "";
            private bool online = true;
            private bool controlling;
            private bool hovered;
            private bool hoverShuffle;
            private bool pressed;
            private int wallpaperIndex;
            private RectangleF shuffleRect = RectangleF.Empty;

            internal event Action ConnectRequested;
            internal event Action DisconnectRequested;

            internal DevicePreviewCanvas()
            {
                ctaHoverTransition = new UiTransition(this);
                shuffleHoverTransition = new UiTransition(this);
                pressTransition = new UiTransition(this);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                ResizeRedraw = true;
                wallpaperIndex = DeviceWallpaperGallery.GetOrAssignIndex("__init__");
            }

            internal void SetDevice(string name, string address, string link, bool isOnline)
            {
                deviceName = name ?? "";
                deviceAddress = address ?? "";
                linkText = link ?? "";
                online = isOnline;
                string key = !string.IsNullOrEmpty(deviceAddress) ? deviceAddress : deviceName;
                wallpaperIndex = DeviceWallpaperGallery.GetOrAssignIndex(key);
                Invalidate();
            }

            internal void SetControlling(bool active)
            {
                if (controlling == active) return;
                controlling = active;
                Invalidate();
            }

            private void SyncHoverState()
            {
                ctaHoverTransition.AnimateTo(hovered && !hoverShuffle ? 1f : 0f, 135, UiEase.EaseOutExpo);
                shuffleHoverTransition.AnimateTo(hoverShuffle ? 1f : 0f, 110, UiEase.EaseOutExpo);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                SyncHoverState();
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                hoverShuffle = false;
                if (pressed)
                {
                    pressed = false;
                    pressTransition.AnimateTo(0f, 180, UiEase.EaseOutQuint);
                }
                SyncHoverState();
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    pressed = true;
                    pressTransition.AnimateTo(1f, 60, UiEase.EaseOutQuint);
                    Invalidate();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (pressed)
                {
                    pressed = false;
                    pressTransition.AnimateTo(0f, 180, UiEase.EaseOutQuint);
                    Invalidate();
                }
                base.OnMouseUp(e);
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (!pressed || Capture) return;
                pressed = false;
                pressTransition.AnimateTo(0f, 180, UiEase.EaseOutQuint);
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool overShuffle = !shuffleRect.IsEmpty && shuffleRect.Contains(e.Location);
                if (overShuffle != hoverShuffle)
                {
                    hoverShuffle = overShuffle;
                    SyncHoverState();
                    Invalidate();
                }
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button != MouseButtons.Left) return;
                if (!shuffleRect.IsEmpty && shuffleRect.Contains(e.Location))
                {
                    string key = !string.IsNullOrEmpty(deviceAddress) ? deviceAddress : deviceName;
                    wallpaperIndex = DeviceWallpaperGallery.CycleRandomIndex(key);
                    Invalidate();
                    return;
                }
                if (controlling)
                {
                    var handler = DisconnectRequested;
                    if (handler != null) handler();
                    return;
                }
                var connectHandler = ConnectRequested;
                if (connectHandler != null) connectHandler();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Paper);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float scale = g.DpiX / 96f;
                float ctaHover = ctaHoverTransition.Value;
                float shuffleHover = shuffleHoverTransition.Value;
                float press = pressTransition.Value;

                var rect = new RectangleF(1f, 1f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 1f));
                var clipState = g.Save();
                using (var topClip = Theme.RoundTop(rect, 7.5f * scale))
                {
                    g.SetClip(topClip);

                    // 随机 Windows 系统风景图（或内置高质感 Windows 风景场景）
                    DeviceWallpaperGallery.DrawWallpaper(g, rect, wallpaperIndex, online);

                    float h = Height;
                    float w = Width;

                    // Top-left live status badge when actively controlling this device
                    if (controlling)
                    {
                        string liveText = "● 正在远程控制中";
                        using (var lf = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Bold))
                        {
                            Size lsz = TextRenderer.MeasureText(g, liveText, lf, new Size(260, 26),
                                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            var livePill = new RectangleF(16f * scale, 14f * scale, lsz.Width + 22f * scale, 26f * scale);
                            Theme.Surface(g, livePill, 13f * scale, Color.FromArgb(235, Theme.Success), Color.FromArgb(110, 255, 255, 255), 1f);
                            TextRenderer.DrawText(g, liveText, lf, Rectangle.Round(livePill), Color.White,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                        }
                    }

                    // Top-right frosted "换壁纸" mini pill
                    using (var sf = new Font("Microsoft YaHei UI", 8.1f, FontStyle.Regular))
                    {
                        const string shuffleLabel = "换风景图";
                        Size ssz = TextRenderer.MeasureText(g, shuffleLabel, sf, new Size(160, 24),
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        float sw = ssz.Width + 22f * scale;
                        float sh = 24f * scale;
                        shuffleRect = new RectangleF(w - sw - 14f * scale, 14f * scale, sw, sh);
                        float shufflePress = hoverShuffle ? press : 0f;
                        RectangleF drawShuffle = shuffleRect;
                        drawShuffle.Y += shufflePress * 0.7f * scale;
                        int sAlpha = (int)Math.Round(148f + 70f * Math.Max(shuffleHover, shufflePress));
                        Color sBg = Color.FromArgb(sAlpha, 20, 31, 48);
                        Theme.Surface(g, drawShuffle, sh * 0.5f, sBg, Color.FromArgb(95 + (int)Math.Round(45f * shuffleHover), 255, 255, 255), 1f);
                        TextRenderer.DrawText(g, shuffleLabel, sf, Rectangle.Round(drawShuffle), Color.FromArgb(245, 249, 255),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }

                    // Centered clean CTA pill (connect or exit remote control)
                    string ctaText = controlling ? "退出远程控制" : (online ? "进入远程桌面" : "设备离线");
                    float ctaH = 42f * scale;
                    float iconSz = (float)Math.Round(16f * scale);
                    float gap = (float)Math.Round(8f * scale);
                    using (var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                    {
                        Size textSz = TextRenderer.MeasureText(g, ctaText, f, new Size(int.MaxValue, (int)ctaH),
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        float contentW = iconSz + gap + textSz.Width;
                        float ctaW = Math.Max(154f * scale, contentW + 46f * scale);
                        float ctaPress = hoverShuffle ? 0f : press;
                        float liftY = (-1.1f * ctaHover + 1.2f * ctaPress) * scale;
                        var ctaRect = new RectangleF((w - ctaW) / 2f, (h - ctaH) / 2f - 4f * scale + liftY, ctaW, ctaH);
                        // Soft elevation shadow so pill stands out crisply over bright scenic photos
                        int shadowAlpha = (int)Math.Round((68f + 26f * ctaHover) * (1f - 0.35f * ctaPress));
                        var shadowRect = new RectangleF(ctaRect.X, ctaRect.Y + (3f - 1.2f * ctaPress + 0.8f * ctaHover) * scale, ctaRect.Width, ctaRect.Height);
                        Theme.Surface(g, shadowRect, ctaH * 0.5f, Color.FromArgb(Math.Max(20, shadowAlpha), 10, 18, 32), Color.Transparent, 0f);

                        Color ctaBg;
                        if (controlling)
                        {
                            Color hotAlert = ControlPaint.Dark(Theme.Alert, 0.08f);
                            ctaBg = MixPreviewColor(Theme.Alert, hotAlert, Math.Max(ctaHover, ctaPress));
                        }
                        else if (!online)
                        {
                            ctaBg = Color.FromArgb(204, 102, 112, 128);
                        }
                        else
                        {
                            ctaBg = MixPreviewColor(MixPreviewColor(Theme.Accent, Theme.AccentHover, ctaHover), Theme.AccentPressed, ctaPress);
                        }

                        Theme.Surface(g, ctaRect, ctaH * 0.5f, ctaBg, Color.FromArgb(130, 255, 255, 255), 1.1f);

                        float startX = (float)Math.Round(ctaRect.X + (ctaW - contentW) / 2f);
                        Theme.Glyph ctaGlyph = controlling ? Theme.Glyph.Close : Theme.Glyph.Desktop;
                        Theme.DrawGlyph(g, ctaGlyph, new RectangleF(startX, (float)Math.Round(ctaRect.Y + (ctaH - iconSz) / 2f), iconSz, iconSz), Theme.Paper);
                        TextRenderer.DrawText(g, ctaText, f,
                            new Rectangle((int)Math.Round(startX + iconSz + gap), (int)Math.Round(ctaRect.Y), textSz.Width + 6, (int)Math.Round(ctaH)),
                            Theme.Paper, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }

                    // Bottom-left crisp frosted address & path chip
                    string meta = string.IsNullOrEmpty(deviceAddress) ? "请选择一台设备" : (deviceAddress + (string.IsNullOrEmpty(linkText) ? "" : "  ·  " + linkText));
                    using (var mf = new Font("Consolas", 8.75f))
                    {
                        Size sz = TextRenderer.MeasureText(g, meta, mf, new Size((int)(w - 40), 26),
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        var chip = new RectangleF(16f * scale, h - 36f * scale, Math.Min(w - 32f * scale, sz.Width + 22f * scale), 24f * scale);
                        Theme.Surface(g, chip, 5f * scale, Color.FromArgb(230, 255, 255, 255), Color.FromArgb(72, 20, 28, 40), 1f);
                        TextRenderer.DrawText(g, meta, mf, Rectangle.Round(chip), Theme.Ink,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }
                }
                g.Restore(clipState);
            }

            private static Color MixPreviewColor(Color a, Color b, float t)
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

        // ==================== FAVORITES PAGE (UU IMAGE 4 STYLE) ====================

        private Control BuildFavoritesPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(640, out desk);
            favoritesDesk = desk;

            var addBtn = Theme.Action("添加收藏设备", Theme.ButtonStyle.Secondary);
            addBtn.Click += delegate { ToggleAddFavoriteBar(); };
            AddPageHeader(desk, "收藏设备", null, addBtn, 138);
            Theme.Row(desk, 18);

            addFavoriteCard = BuildAddFavoriteCard();
            addFavoriteCard.Visible = false;
            addFavoriteRow = desk.RowCount;
            Theme.Row(desk, 0, addFavoriteCard);

            favoritesListHost = Theme.Grid(1);
            Theme.FillColumn(favoritesListHost);
            favoritesListRow = desk.RowCount;
            Theme.Row(desk, 190, favoritesListHost);
            Theme.Row(desk, 20);

            Theme.Row(desk, 28, Theme.Text("最近连接", Theme.TextSize.Caption, Theme.CaptionInk));
            Theme.Row(desk, 10);

            recentListHost = Theme.Grid(1);
            Theme.FillColumn(recentListHost);
            recentListRow = desk.RowCount;
            Theme.Row(desk, 90, recentListHost);
            Theme.Row(desk, 16);
            Theme.Row(desk, 42, favoritesBanner);

            RefreshSavedDevices();
            return viewport;
        }

        private Control BuildAddFavoriteCard()
        {
            var card = new Theme.Card { Padding = new Padding(20, 16, 20, 16) };
            var grid = Theme.Grid(1);
            Theme.FillColumn(grid);
            Theme.Row(grid, 26, Theme.Text("添加新的收藏设备", Theme.TextSize.LabelBold, Theme.Ink));
            Theme.Row(grid, 10);

            var row = Theme.Grid(7);
            Theme.FixedColumn(row, 158);
            Theme.FixedColumn(row, 10);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 88);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 76);

            addFavoriteKind = Theme.Choice("收藏类型", false);
            addFavoriteKind.Items.Add("高速串流主机");
            addFavoriteKind.Items.Add("临时协助设备码");
            addFavoriteKind.SelectedIndex = 0;

            addFavoriteInput = Theme.InputBox("设备地址或设备码", "");
            Theme.Cue(addFavoriteInput, "输入主机 IP / Tailscale IP 或 9 位设备码");

            var saveBtn = Theme.Action("保存", Theme.ButtonStyle.Primary);
            saveBtn.Click += delegate { SaveNewFavoriteFromBar(); };
            var cancelBtn = Theme.Action("取消", Theme.ButtonStyle.Secondary);
            cancelBtn.Click += delegate { ToggleAddFavoriteBar(false); };

            Theme.Row(row, 38, Theme.WrapInput(addFavoriteKind), null, Theme.WrapInput(addFavoriteInput), null, saveBtn, null, cancelBtn);
            Theme.Row(grid, 38, row);
            card.Controls.Add(grid);
            return card;
        }

        private void ToggleAddFavoriteBar(bool? forceOpen = null)
        {
            addFavoriteOpen = forceOpen.HasValue ? forceOpen.Value : !addFavoriteOpen;
            if (addFavoriteCard != null) addFavoriteCard.Visible = addFavoriteOpen;
            if (favoritesDesk != null && addFavoriteRow >= 0)
            {
                int openH = DevicePixels(120);
                if (addFavoriteCard != null && addFavoriteCard.Controls.Count > 0)
                {
                    int inner = SumRows(addFavoriteCard.Controls[0]);
                    if (inner > 0) openH = inner + addFavoriteCard.Padding.Vertical + DevicePixels(12);
                }
                favoritesDesk.RowStyles[addFavoriteRow].Height = addFavoriteOpen ? openH : 0;
                RecalculateFavoritesDeskHeight();
            }
            if (addFavoriteOpen && addFavoriteInput != null) addFavoriteInput.Focus();
        }

        private void SaveNewFavoriteFromBar()
        {
            bool isStream = addFavoriteKind != null && addFavoriteKind.SelectedIndex == 0;
            string raw = addFavoriteInput == null ? "" : (addFavoriteInput.Text ?? "").Trim();
            string error;
            string clean = NormalizeFavoriteTarget(raw, out error);
            if (clean == null)
            {
                favoritesBanner.Set(error ?? "请输入有效的设备地址或设备码。", Theme.PillTone.Warning);
                return;
            }
            var item = new SavedDevice(isStream, clean);
            if (!favorites.Exists(f => f.SameAs(item))) favorites.Insert(0, item);
            if (addFavoriteInput != null) addFavoriteInput.Text = "";
            ToggleAddFavoriteBar(false);
            SaveFavorites();
        }

        private void RefreshSavedDevices()
        {
            RenderFavoritesList();
            RenderRecentsList();
            UpdateQuickDeviceShortcut();
        }

        private void RenderFavoritesList()
        {
            if (favoritesListHost == null) return;
            favoritesListHost.SuspendLayout();
            try
            {
                for (int i = favoritesListHost.Controls.Count - 1; i >= 0; i--) favoritesListHost.Controls[i].Dispose();
                favoritesListHost.RowStyles.Clear();
                favoritesListHost.RowCount = 0;

                float scale = CurrentDpiScale();
                if (favorites.Count == 0)
                {
                    var empty = new EmptyFavoritesCanvas();
                    Theme.Row(favoritesListHost, 180f * scale, empty);
                }
                else
                {
                    foreach (var fav in favorites)
                    {
                        var item = fav;
                        var card = BuildSavedDeviceCard(item, true);
                        if (scale != 1f) card.Scale(new SizeF(scale, scale));
                        Theme.Row(favoritesListHost, 76f * scale, card);
                        Theme.Row(favoritesListHost, 10f * scale);
                    }
                }
            }
            finally { favoritesListHost.ResumeLayout(true); }
            RecalculateFavoritesDeskHeight();
        }

        private void RenderRecentsList()
        {
            if (recentListHost == null) return;
            recentListHost.SuspendLayout();
            try
            {
                for (int i = recentListHost.Controls.Count - 1; i >= 0; i--) recentListHost.Controls[i].Dispose();
                recentListHost.RowStyles.Clear();
                recentListHost.RowCount = 0;

                float scale = CurrentDpiScale();
                var all = new List<SavedDevice>();
                foreach (string h in recent.MoonlightHosts) all.Add(new SavedDevice(true, h));
                foreach (string r in recent.RustDeskIds) all.Add(new SavedDevice(false, r));

                if (all.Count == 0)
                {
                    var card = new Theme.Card { Padding = new Padding(18, 12, 18, 12), DashedBorder = true };
                    card.Controls.Add(Theme.Text("暂无最近连接记录，完成一次远程连接后会自动记录在这里。", Theme.TextSize.Small, Theme.Muted));
                    if (scale != 1f) card.Scale(new SizeF(scale, scale));
                    Theme.Row(recentListHost, 58f * scale, card);
                }
                else
                {
                    foreach (var rec in all)
                    {
                        var card = BuildSavedDeviceCard(rec, false);
                        if (scale != 1f) card.Scale(new SizeF(scale, scale));
                        Theme.Row(recentListHost, 76f * scale, card);
                        Theme.Row(recentListHost, 10f * scale);
                    }
                }
            }
            finally { recentListHost.ResumeLayout(true); }
            RecalculateFavoritesDeskHeight();
        }

        private Control BuildSavedDeviceCard(SavedDevice device, bool isFavoriteList)
        {
            var card = new Theme.Card { Padding = new Padding(18, 12, 18, 12) };
            var row = Theme.Grid(7);
            Theme.FixedColumn(row, 42);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 14);
            Theme.FixedColumn(row, 86);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 88);

            var icon = new Theme.IconTile(device.Stream ? Theme.Glyph.WindowsTile : Theme.Glyph.Send,
                true, Theme.AccentLight, Theme.Accent, 8f);

            var info = new Theme.TwoLineText(
                device.Address,
                device.Stream ? "低延迟高速串流主机" : "临时协助 / 文件传输设备码",
                Theme.TextSize.Label,
                Theme.Ink,
                Theme.Muted);

            var connect = Theme.Action("连接", Theme.ButtonStyle.Primary);
            connect.Margin = new Padding(0, 8, 0, 8);
            connect.Click += async delegate { await ConnectSavedDeviceAsync(device); };

            Button second;
            if (isFavoriteList)
            {
                second = Theme.Action("移出", Theme.ButtonStyle.Secondary);
                second.Click += delegate { favorites.RemoveAll(f => f.SameAs(device)); SaveFavorites(); };
            }
            else
            {
                bool alreadyFav = favorites.Exists(f => f.SameAs(device));
                second = Theme.Action(alreadyFav ? "已收藏" : "收藏", Theme.ButtonStyle.Secondary);
                second.Enabled = !alreadyFav;
                second.Click += delegate
                {
                    if (!favorites.Exists(f => f.SameAs(device)))
                    {
                        favorites.Insert(0, device);
                        SaveFavorites();
                    }
                };
            }
            second.Margin = new Padding(0, 8, 0, 8);

            Theme.Row(row, 52, icon, null, info, null, connect, null, second);
            card.Controls.Add(row);
            return card;
        }

        private void RecalculateFavoritesDeskHeight()
        {
            if (favoritesDesk == null) return;
            if (favoritesListRow >= 0 && favoritesListHost != null)
                favoritesDesk.RowStyles[favoritesListRow].Height = Math.Max(DevicePixels(76), SumRows(favoritesListHost));
            if (recentListRow >= 0 && recentListHost != null)
                favoritesDesk.RowStyles[recentListRow].Height = Math.Max(DevicePixels(58), SumRows(recentListHost));
            favoritesDesk.Height = SumRows(favoritesDesk);
            favoritesDesk.PerformLayout();
        }

        /// <summary>UU Image 4 同款「暂无收藏」轻蓝简约空态插画。</summary>
        private sealed class EmptyFavoritesCanvas : Control
        {
            internal EmptyFavoritesCanvas()
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.Workspace;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Workspace);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float scale = g.DpiX / 96f;
                float cx = Width / 2f;
                float cy = Height / 2f - 14f * scale;

                float bw = 64f * scale;
                float bh = 46f * scale;
                var box = new RectangleF(cx - bw / 2f, cy - bh / 2f + 8f * scale, bw, bh);

                // Open box back flap
                PointF[] backFlap = new[]
                {
                    new PointF(box.Left + 6 * scale, box.Top),
                    new PointF(box.Left + 16 * scale, box.Top - 14 * scale),
                    new PointF(box.Right - 4 * scale, box.Top - 16 * scale),
                    new PointF(box.Right - 10 * scale, box.Top)
                };
                using (var b = new SolidBrush(Color.FromArgb(168, 212, 250))) g.FillPolygon(b, backFlap);

                // Box front body
                Theme.Surface(g, box, 5f * scale, Color.FromArgb(196, 228, 255), Color.Transparent);

                // Top lid shading
                var lid = new RectangleF(box.X, box.Y, box.Width, 8f * scale);
                using (var b = new SolidBrush(Color.FromArgb(158, 206, 248))) g.FillRectangle(b, lid);

                // White star centered on box
                PointF[] star = new PointF[10];
                float sx = cx;
                float sy = box.Y + box.Height * 0.56f;
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5;
                    float r = (i % 2 == 0) ? 11f * scale : 5f * scale;
                    star[i] = new PointF(sx + (float)(r * Math.Cos(a)), sy + (float)(r * Math.Sin(a)));
                }
                using (var sb = new SolidBrush(Theme.Paper)) g.FillPolygon(sb, star);

                // Caption below
                using (var f = new Font("Microsoft YaHei UI", 9.5f))
                {
                    TextRenderer.DrawText(g, "暂无收藏", f,
                        new Rectangle(0, (int)(box.Bottom + 14f * scale), Width, (int)(26f * scale)),
                        Theme.BodyInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        private static Theme.Glyph PeerOsGlyph(TsPeer peer)
        {
            string os = (peer != null && peer.Os != null ? peer.Os : "").Trim().ToLowerInvariant();
            if (os.Length == 0 && peer != null && !string.IsNullOrEmpty(peer.LinkText))
            {
                string lt = peer.LinkText.ToLowerInvariant();
                if (lt.IndexOf("[android]", StringComparison.Ordinal) >= 0 || lt.IndexOf("[ios]", StringComparison.Ordinal) >= 0)
                    return Theme.Glyph.Smartphone;
                if (lt.IndexOf("[macos]", StringComparison.Ordinal) >= 0 || lt.IndexOf("[darwin]", StringComparison.Ordinal) >= 0)
                    return Theme.Glyph.Apple;
                if (lt.IndexOf("[linux]", StringComparison.Ordinal) >= 0)
                    return Theme.Glyph.Linux;
            }
            if (os == "android" || os == "ios") return Theme.Glyph.Smartphone;
            if (os == "macos" || os == "darwin") return Theme.Glyph.Apple;
            if (os == "linux" || os == "freebsd" || os == "openbsd") return Theme.Glyph.Linux;
            return Theme.Glyph.WindowsTile;
        }

        private static string PeerName(TsPeer peer)
        {
            if (!string.IsNullOrWhiteSpace(peer.HostName)) return peer.HostName;
            if (!string.IsNullOrWhiteSpace(peer.DnsName)) return peer.DnsName;
            return peer.Ip;
        }

        private void OpenStreamPage(string address)
        {
            ShowPage(AssistPage);
            SelectAssistMode(0);
            if (!string.IsNullOrEmpty(address)) streamHost.Text = address;
            streamHost.Focus();
        }

        private async Task ConnectSavedDeviceAsync(SavedDevice selected)
        {
            if (selected == null) return;
            if (!selected.Stream)
            {
                ShowPage(AssistPage);
                SelectAssistMode(1);
                remoteId.Text = selected.Address;
                remoteId.Focus();
                SetStatus("已填入临时协助设备码，点击「连接」或「传输文件」即可开始。", Theme.PillTone.Info);
                return;
            }
            await ConnectToDeviceAsync(selected.Address, null);
        }

        private static string NormalizeFavoriteTarget(string raw, out string error)
        {
            string code = BoBoIdentityStore.NormalizeDeviceCode(raw);
            if (code.Length == 0) return Engines.NormalizeHost(raw, out error);
            error = null;
            return code;
        }

        private static string FavoritesPath { get { return Path.Combine(RecentStore.DirPath, "favorites.txt"); } }

        private void LoadFavorites()
        {
            if (!File.Exists(FavoritesPath)) return;
            try
            {
                foreach (string line in File.ReadLines(FavoritesPath))
                {
                    if (line.Length < 3 || line[1] != '\t' || (line[0] != 'S' && line[0] != 'R')) continue;
                    bool stream = line[0] == 'S';
                    string raw = line.Substring(2);
                    string error;
                    string address = NormalizeFavoriteTarget(raw, out error);
                    if (address == null && !stream) address = Engines.NormalizeRustDeskId(raw, out error);
                    if (address == null) continue;
                    var item = new SavedDevice(stream, address);
                    if (!favorites.Exists(value => value.SameAs(item))) favorites.Add(item);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void SaveFavorites()
        {
            RefreshSavedDevices();
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                File.WriteAllLines(FavoritesPath, favorites.ConvertAll(item => (item.Stream ? "S\t" : "R\t") + item.Address));
                deviceBanner.Set("收藏列表已保存到本机。", Theme.PillTone.Success);
                favoritesBanner.Set("收藏列表已保存到本机。", Theme.PillTone.Success);
            }
            catch (IOException) { FavoriteError(); }
            catch (UnauthorizedAccessException) { FavoriteError(); }
        }

        private void FavoriteError()
        {
            deviceBanner.Set("无法保存收藏，本次改动仅在窗口打开期间生效。", Theme.PillTone.Warning);
            favoritesBanner.Set("无法保存收藏，本次改动仅在窗口打开期间生效。", Theme.PillTone.Warning);
        }

        private sealed class SavedDevice
        {
            internal readonly bool Stream;
            internal readonly string Address;
            internal SavedDevice(bool stream, string address) { Stream = stream; Address = address; }
            internal bool SameAs(SavedDevice other)
            {
                return Stream == other.Stream && string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase);
            }
            public override string ToString() { return (Stream ? "高速串流 · " : "临时协助 · ") + Address; }
        }
    }
}
