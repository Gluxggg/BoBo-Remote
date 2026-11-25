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
        private readonly Theme.Badge heroSunshineBadge = new Theme.Badge("检测中…", Theme.PillTone.Neutral);
        private readonly Theme.Badge heroPrivacyBadge = new Theme.Badge("防窥未开", Theme.PillTone.Neutral);
        private readonly Label heroLanLabel = Theme.Text("检测中…", Theme.TextSize.Code, Theme.Ink);
        private readonly Label heroTsLabel = Theme.Text("检测中…", Theme.TextSize.Code, Theme.Ink);
        private readonly Label heroV6Label = Theme.Text("检测中…", Theme.TextSize.Code, Theme.Ink);
        private Button heroCopyLan;
        private Button heroCopyTs;
        private Button heroCopyV6;
        private Button heroPrivacyToggle;
        private Button refreshDevices;

        private TableLayoutPanel devicesDesk;
        private Theme.SlimScrollViewport devicesViewport;
        private TableLayoutPanel hostCardLayout;
        private Control hostCardControl;
        private Label heroSubtitleLabel;
        private HostActionBarPanel hostActionsBar;
        private int hostCardRow = -1;
        private int hostSubtitleRow = -1;
        private int hostActionsRow = -1;
        private int hostDetailsRow = -1;
        private Control hostCardDetails;
        private TableLayoutPanel hostCardDetailsLayout;
        private bool hostDetailsExpanded;

        private Button computersGroupBtn;
        private bool computersExpanded = true;
        private TableLayoutPanel peerListHost;
        private int peerListRow = -1;

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
            var viewport = CreatePage(740, out desk);
            devicesViewport = viewport;
            devicesDesk = desk;
            LoadFavorites();

            refreshDevices = Theme.Action("刷新状态", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            refreshDevices.Click += async delegate { await RefreshDashboardAsync(); };
            AddPageHeader(desk, "全部设备", null, refreshDevices, 118);
            Theme.Row(desk, 18);

            // Top Info & Quick Connect Card (matches UU Image 2 top banner card)
            var hostCard = BuildHostInfoCard();
            hostCardRow = desk.RowCount;
            Theme.Row(desk, HostCardHeight(), hostCard);
            Theme.Row(desk, 22);

            // Collapsible "电脑 N" section header
            var groupRow = Theme.Grid(2);
            Theme.FixedColumn(groupRow, 150);
            Theme.FillColumn(groupRow);
            computersGroupBtn = Theme.Action("电脑 1", Theme.ButtonStyle.Quiet, Theme.Glyph.ChevronDown);
            computersGroupBtn.Click += delegate
            {
                computersExpanded = !computersExpanded;
                Theme.ChangeGlyph(computersGroupBtn, computersExpanded ? Theme.Glyph.ChevronDown : Theme.Glyph.ChevronRight);
                SyncPeerListLayout();
            };
            Theme.Row(groupRow, 30, computersGroupBtn, null);
            Theme.Row(desk, 30, groupRow);
            Theme.Row(desk, 10);

            peerListHost = Theme.Grid(1);
            Theme.FillColumn(peerListHost);
            peerListRow = desk.RowCount;
            Theme.Row(desk, 156, peerListHost);
            Theme.Row(desk, 16);
            Theme.Row(desk, 42, deviceBanner);

            viewport.ContentWidthChanged += delegate(int contentW) { SyncHostCardResponsiveMetrics(contentW); };
            DrawPeerRows(null);
            RecalculateDevicesDeskHeight();

            Shown += async delegate { await RefreshDashboardAsync(); };
            return viewport;
        }

        private Control BuildHostInfoCard()
        {
            var card = new Theme.Card { Padding = new Padding(22, 18, 22, 18) };
            hostCardControl = card;
            var content = Theme.Grid(1);
            Theme.FillColumn(content);
            hostCardLayout = content;

            var top = Theme.Grid(5);
            Theme.FixedColumn(top, 26);
            Theme.FixedColumn(top, 10);
            Theme.FillColumn(top);
            Theme.FixedColumn(top, 10);
            Theme.FixedColumn(top, 252);

            var infoIcon = new Theme.IconTile(Theme.Glyph.InfoMark, false, Theme.Accent, Theme.Paper, 13f);
            heroSunshineBadge.AlignRight = true;
            heroPrivacyBadge.AlignRight = true;
            var badges = Theme.Grid(3);
            Theme.FixedColumn(badges, 126);
            Theme.FixedColumn(badges, 8);
            Theme.FixedColumn(badges, 118);
            Theme.Row(badges, 28, heroSunshineBadge, null, heroPrivacyBadge);
            Theme.Row(top, 28, infoIcon, null, Theme.Text("添加或连接你的其他设备", Theme.TextSize.LabelBold, Theme.Ink), null, badges);
            Theme.Row(content, 28, top);
            Theme.Row(content, 6);

            var subWrap = Theme.Grid(2);
            Theme.FixedColumn(subWrap, 36);
            Theme.FillColumn(subWrap);
            heroSubtitleLabel = Theme.Text("在笔记本或另一台电脑直接解压运行 BoBo远程，登录同账号 Tailscale 或输入本机 IP 即可秒连。", Theme.TextSize.Small, Theme.Muted);
            heroSubtitleLabel.AutoEllipsis = false;
            Theme.FillRow(subWrap);
            subWrap.Controls.Add(heroSubtitleLabel, 1, 0);
            hostSubtitleRow = content.RowCount;
            Theme.Row(content, 26, subWrap);
            Theme.Row(content, 12);

            var actionsWrap = Theme.Grid(2);
            Theme.FixedColumn(actionsWrap, 36);
            Theme.FillColumn(actionsWrap);

            var manualBtn = Theme.Action("手动连接设备", Theme.ButtonStyle.AccentSoft);
            manualBtn.Click += delegate { OpenStreamPage(null); };

            var pairBtn = Theme.Action("允许连接本机", Theme.ButtonStyle.Secondary);
            pairBtn.Click += async delegate
            {
                ShowPage(AssistPage);
                await BeginLocalPairingAsync();
            };

            heroPrivacyToggle = Theme.Action("开启防窥壁纸", Theme.ButtonStyle.Secondary);
            BindPrivacyToggleButton(heroPrivacyToggle);

            var addrBtn = Theme.Action("查看本机地址", Theme.ButtonStyle.Link);
            addrBtn.Click += delegate { ToggleHostDetails(addrBtn); };

            hostActionsBar = new HostActionBarPanel(manualBtn, pairBtn, heroPrivacyToggle, addrBtn);
            Theme.FillRow(actionsWrap);
            actionsWrap.Controls.Add(hostActionsBar, 1, 0);
            hostActionsRow = content.RowCount;
            Theme.Row(content, 36, actionsWrap);

            // Expandable local addresses drawer
            var details = Theme.Grid(1);
            Theme.FillColumn(details);
            details.Padding = new Padding(36, 12, 0, 0);
            hostCardDetails = details;
            hostCardDetailsLayout = details;

            heroLanLabel.AutoEllipsis = true;
            heroTsLabel.AutoEllipsis = true;
            heroV6Label.AutoEllipsis = true;
            heroCopyLan = MakeAddressCopy("局域网 IP", delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); });
            heroCopyTs = MakeAddressCopy("Tailscale IP", delegate { CopyHostAddress("Tailscale IP", hostSnapshot.TailscaleIpv4); });
            heroCopyV6 = MakeAddressCopy("公网 IPv6", delegate { CopyHostAddress("公网 IPv6", hostSnapshot.PublicIpv6); });

            Theme.Row(details, 1, Theme.Rule());
            Theme.Row(details, 8);
            Theme.Row(details, 30, BuildAddressRow("局域网 IPv4", heroLanLabel, heroCopyLan));
            Theme.Row(details, 4);
            Theme.Row(details, 30, BuildAddressRow("Tailscale 组网", heroTsLabel, heroCopyTs));
            Theme.Row(details, 4);
            Theme.Row(details, 30, BuildAddressRow("公网 IPv6", heroV6Label, heroCopyV6));

            details.Visible = false;
            hostDetailsRow = content.RowCount;
            Theme.Row(content, 0, details);

            card.Controls.Add(content);
            return card;
        }

        /// <summary>
        /// 顶部引导卡 4 按钮自适应排布容器：宽窗口单行舒展排布，窄窗口自动折行，绝不裁切「查看本机地址」。
        /// </summary>
        private sealed class HostActionBarPanel : Panel
        {
            private readonly Button b1;
            private readonly Button b2;
            private readonly Button b3;
            private readonly Button linkBtn;

            internal HostActionBarPanel(Button b1, Button b2, Button b3, Button linkBtn)
            {
                this.b1 = b1;
                this.b2 = b2;
                this.b3 = b3;
                this.linkBtn = linkBtn;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.Paper;
                DoubleBuffered = true;
                foreach (Button b in new[] { b1, b2, b3, linkBtn })
                {
                    b.Dock = DockStyle.None;
                    Controls.Add(b);
                }
            }

            internal int ComputeRequiredHeight(int availableWidth, float scale)
            {
                int w1 = (int)Math.Round(128 * scale);
                int gap = (int)Math.Round(8 * scale);
                int linkGap = (int)Math.Round(14 * scale);
                int wLink = (int)Math.Round(118 * scale);
                int btnH = (int)Math.Round(36 * scale);
                int rowGap = (int)Math.Round(8 * scale);

                int singleRowW = w1 * 3 + gap * 2 + linkGap + wLink;
                if (availableWidth >= singleRowW) return btnH;
                int threeBtnsW = w1 * 3 + gap * 2;
                if (availableWidth >= threeBtnsW) return btnH * 2 + rowGap;
                return btnH * 2 + rowGap;
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                float scale = Theme.GetDpiScale(this);
                int availW = Math.Max(180, ClientSize.Width);
                int wBtn = (int)Math.Round(128 * scale);
                int gap = (int)Math.Round(8 * scale);
                int linkGap = (int)Math.Round(14 * scale);
                int wLink = (int)Math.Round(118 * scale);
                int btnH = (int)Math.Round(36 * scale);
                int rowGap = (int)Math.Round(8 * scale);

                Button[] items = new[] { b1, b2, b3, linkBtn };
                int[] widths = new[] { wBtn, wBtn, wBtn, wLink };
                int[] gaps = new[] { gap, gap, linkGap, 0 };

                int x = 0;
                int y = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    int bw = Math.Min(widths[i], availW);
                    if (x > 0 && x + bw > availW)
                    {
                        x = 0;
                        y += btnH + rowGap;
                    }
                    items[i].Bounds = new Rectangle(x, y, bw, btnH);
                    x += bw + gaps[i];
                }
            }
        }

        private void SyncHostCardResponsiveMetrics(int contentWidth)
        {
            if (!initialScaleApplied || hostCardLayout == null || hostCardControl == null || devicesDesk == null || hostCardRow < 0)
                return;
            float scale = CurrentDpiScale();
            int innerCardW = Math.Max(260, contentWidth - devicesDesk.Padding.Horizontal - hostCardControl.Padding.Horizontal);
            int rightColW = Math.Max(200, innerCardW - (int)Math.Round(36 * scale));

            if (hostSubtitleRow >= 0 && heroSubtitleLabel != null)
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    Size sz = TextRenderer.MeasureText(g, heroSubtitleLabel.Text, Theme.GetFont(Theme.TextSize.Small),
                        new Size(rightColW, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    int subH = Math.Max((int)Math.Round(24 * scale), sz.Height + (int)Math.Ceiling(6 * scale));
                    hostCardLayout.RowStyles[hostSubtitleRow].Height = subH;
                }
            }
            if (hostActionsRow >= 0 && hostActionsBar != null)
            {
                hostCardLayout.RowStyles[hostActionsRow].Height = hostActionsBar.ComputeRequiredHeight(rightColW, scale);
            }
            if (hostDetailsRow >= 0 && hostCardDetailsLayout != null)
            {
                hostCardLayout.RowStyles[hostDetailsRow].Height = hostDetailsExpanded ? SumRows(hostCardDetailsLayout) : 0;
            }
            devicesDesk.RowStyles[hostCardRow].Height = HostCardHeight();
            RecalculateDevicesDeskHeight();
        }

        private static Control BuildAddressRow(string label, Label value, Button copy)
        {
            var row = Theme.Grid(4);
            Theme.FixedColumn(row, 102);
            Theme.FixedColumn(row, 8);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 74);
            Theme.Row(row, 30, Theme.Text(label, Theme.TextSize.Small, Theme.Muted), null, value, copy);
            return row;
        }

        private Button MakeAddressCopy(string label, EventHandler click)
        {
            var copy = Theme.Action("复制", Theme.ButtonStyle.Secondary);
            copy.AccessibleName = "复制" + label;
            copy.Click += click;
            copy.Enabled = false;
            return copy;
        }

        private void ToggleHostDetails(Button button)
        {
            if (hostCardLayout == null || hostCardDetails == null) return;
            hostDetailsExpanded = !hostDetailsExpanded;
            hostCardLayout.RowStyles[hostDetailsRow].Height = hostDetailsExpanded
                ? (hostCardDetailsLayout != null ? SumRows(hostCardDetailsLayout) : DevicePixels(120))
                : 0;
            hostCardDetails.Visible = hostDetailsExpanded;
            if (devicesDesk != null && hostCardRow >= 0)
            {
                devicesDesk.RowStyles[hostCardRow].Height = HostCardHeight();
                RecalculateDevicesDeskHeight();
            }
            if (button != null)
            {
                button.Text = hostDetailsExpanded ? "收起本机地址" : "查看本机地址";
                button.AccessibleName = button.Text;
            }
        }

        private int HostCardHeight()
        {
            if (hostCardLayout == null || hostCardControl == null) return DevicePixels(142);
            return SumRows(hostCardLayout) + hostCardControl.Padding.Vertical;
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
                deviceBanner.Set("本机暂未获取到" + label + "。", Theme.PillTone.Warning);
                SetStatus("本机暂未获取到" + label + "。", Theme.PillTone.Warning);
                return;
            }
            try
            {
                Clipboard.SetText(value);
                string ok = "已复制本机" + label + "到剪贴板（为保护隐私，状态栏不回显明文）。";
                deviceBanner.Set(ok, Theme.PillTone.Success);
                SetStatus(ok, Theme.PillTone.Success);
            }
            catch
            {
                deviceBanner.Set("无法写入剪贴板，请展开本机地址后手动复制。", Theme.PillTone.Warning);
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
            heroLanLabel.Text = string.IsNullOrEmpty(hostSnapshot.LanIpv4) ? "未连接" : hostSnapshot.LanIpv4;
            heroTsLabel.Text = string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4) ? "未登录" : hostSnapshot.TailscaleIpv4;
            heroV6Label.Text = string.IsNullOrEmpty(hostSnapshot.PublicIpv6) ? "无公网 IPv6" : ShortenIpv6(hostSnapshot.PublicIpv6);
            if (heroCopyLan != null) heroCopyLan.Enabled = !string.IsNullOrEmpty(hostSnapshot.LanIpv4);
            if (heroCopyTs != null) heroCopyTs.Enabled = !string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4);
            if (heroCopyV6 != null) heroCopyV6.Enabled = !string.IsNullOrEmpty(hostSnapshot.PublicIpv6);

            if (boboHostServer != null && boboHostServer.IsRunning && boboHostServer.AllowIncomingControl)
                heroSunshineBadge.UpdateStatus("V6 本机可被控 · 49810", Theme.PillTone.Success);
            else if (boboHostServer != null && boboHostServer.IsRunning)
                heroSunshineBadge.UpdateStatus("已关闭本机被控", Theme.PillTone.Neutral);
            else
                heroSunshineBadge.UpdateStatus("被控服务未启动", Theme.PillTone.Warning);

            UpdateLocalAssistAddressLabel();
            UpdateDiagnosticsUi();
            UpdatePrivacyIndicators();
        }

        private static string ShortenIpv6(string ipv6)
        {
            if (string.IsNullOrEmpty(ipv6) || ipv6.Length <= 24) return ipv6;
            return ipv6.Substring(0, 12) + "…" + ipv6.Substring(ipv6.Length - 8);
        }

        private async Task OpenLocalRustDesk(Button receive)
        {
            if (receive != null) receive.Enabled = false;
            deviceBanner.Set("正在打开本机 RustDesk…", Theme.PillTone.Info);
            try
            {
                string error = await Task.Run(() =>
                {
                    string detail;
                    return Engines.LaunchRustDesk(out detail) ? null : detail;
                });
                if (IsDisposed) return;
                deviceBanner.Set(error ?? "已打开临时协助窗口，请在其中查看 9 位设备码并确认连接。",
                    error == null ? Theme.PillTone.Success : Theme.PillTone.Alert);
            }
            finally { if (receive != null && !IsDisposed) receive.Enabled = true; }
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

                // 1. Live LAN auto-discovered BoBo V6 peers (zero external VPN needed!)
                if (boboHostServer != null)
                {
                    foreach (BoBoDiscoveredPeer lp in boboHostServer.GetDiscoveredLanPeers())
                    {
                        if (string.IsNullOrWhiteSpace(lp.Host)) continue;
                        seenIps.Add(lp.Host);
                        merged.Add(new TsPeer
                        {
                            HostName = lp.DeviceName,
                            Ip = lp.Host,
                            Online = lp.AllowIncoming,
                            LinkText = lp.AllowIncoming
                                ? ("BoBo V6 在线 · 局域网直连 · 码 " + BoBoIdentityStore.FormatDeviceCode(lp.DeviceCode))
                                : "局域网 · 对方已关闭被控"
                        });
                    }
                }

                // 2. Tailscale peers (optional cross-WAN mesh)
                if (result.Ok && result.Peers != null)
                {
                    foreach (TsPeer tp in result.Peers)
                    {
                        if (!string.IsNullOrWhiteSpace(tp.Ip) && seenIps.Add(tp.Ip))
                            merged.Add(tp);
                    }
                }

                // 3. Previously authorized BoBo V6 peers with saved IP
                foreach (BoBoAuthorizedPeer ap in BoBoIdentityStore.Default.ListAuthorizedPeers())
                {
                    if (!string.IsNullOrWhiteSpace(ap.LastHost) && seenIps.Add(ap.LastHost))
                    {
                        merged.Add(new TsPeer
                        {
                            HostName = string.IsNullOrWhiteSpace(ap.DeviceName) ? ap.LastHost : ap.DeviceName,
                            Ip = ap.LastHost,
                            Online = true,
                            LinkText = "已授权 BoBo 设备 · 码 " + BoBoIdentityStore.FormatDeviceCode(ap.DeviceCode)
                        });
                    }
                }

                cachedPeers.Clear();
                cachedPeers.AddRange(merged);
                DrawPeerRows(merged, merged.Count == 0 && !result.Ok ? result.Error : null);
                UpdateQuickDeviceShortcut();

                bool serverReady = boboHostServer != null && boboHostServer.IsRunning && boboHostServer.AllowIncomingControl;
                deviceBanner.Set(
                    serverReady
                        ? ("BoBo V6 全内置服务运行中（本机设备码 " + BoBoIdentityStore.Default.FormattedDeviceCode + " · 已发现 " + merged.Count + " 台设备）。")
                        : "提示：本机「允许本机被控」当前已关闭，如需被控制请在设置中开启。",
                    serverReady ? Theme.PillTone.Success : Theme.PillTone.Warning);
            }
            catch
            {
                if (!IsDisposed)
                    deviceBanner.Set("暂时无法读取同网设备，可点击「手动连接设备」输入 IP 或 9 位 BoBo 设备码。", Theme.PillTone.Warning);
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
                quickDeviceName = PeerName(best);
                quickDeviceAddress = best.Ip;
                quickDeviceOnline = best.Online;
                quickDeviceLink = best.LinkText;
                quickDeviceIsStream = true;
            }
            else if (favorites.Count > 0)
            {
                var f = favorites[0];
                quickDeviceName = f.Address;
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
                Theme.SetGlyphTint(navQuickDevice, quickDeviceOnline ? Theme.Accent : Theme.Muted);
            }
            SyncNavigationSectionVisibility();
        }

        private void DrawPeerRows(List<TsPeer> peers, string issue = null)
        {
            if (peerListHost == null) return;
            peerListHost.SuspendLayout();
            try
            {
                for (int i = peerListHost.Controls.Count - 1; i >= 0; i--) peerListHost.Controls[i].Dispose();
                peerListHost.RowStyles.Clear();
                peerListHost.RowCount = 0;

                float scale = CurrentDpiScale();
                int totalDevices = 1; // Always includes This PC

                // Row 1: This PC (UU Image 2 exact match: [Windows blue icon] MACHINE_NAME [本机] ... [i])
                var selfCard = BuildThisPcRow();
                if (scale != 1f) selfCard.Scale(new SizeF(scale, scale));
                Theme.Row(peerListHost, 72f * scale, selfCard);
                Theme.Row(peerListHost, 10f * scale);

                int peerCount = 0;
                if (peers != null)
                {
                    foreach (var peer in peers)
                    {
                        if (string.IsNullOrWhiteSpace(peer.Ip)) continue;
                        peerCount++;
                        var card = BuildPeerCardRow(peer);
                        if (scale != 1f) card.Scale(new SizeF(scale, scale));
                        Theme.Row(peerListHost, 76f * scale, card);
                        Theme.Row(peerListHost, 10f * scale);
                    }
                }
                totalDevices += peerCount;

                if (peerCount == 0)
                {
                    var hintCard = BuildEmptyPeersHintRow(issue);
                    if (scale != 1f) hintCard.Scale(new SizeF(scale, scale));
                    Theme.Row(peerListHost, 76f * scale, hintCard);
                }

                if (computersGroupBtn != null)
                    computersGroupBtn.Text = "电脑 " + totalDevices;
            }
            finally
            {
                peerListHost.ResumeLayout(true);
            }
            SyncPeerListLayout();
        }

        private void SyncPeerListLayout()
        {
            if (devicesDesk == null || peerListHost == null || peerListRow < 0) return;
            peerListHost.Visible = computersExpanded;
            devicesDesk.RowStyles[peerListRow].Height = computersExpanded ? SumRows(peerListHost) : 0;
            RecalculateDevicesDeskHeight();
            NotifyOnboardingLayoutChanged();
        }

        private Control BuildThisPcRow()
        {
            var card = new Theme.Card { Padding = new Padding(18, 12, 18, 12) };
            var row = Theme.Grid(6);
            Theme.FixedColumn(row, 40);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 36);
            Theme.FixedColumn(row, 32);

            var icon = new Theme.IconTile(Theme.Glyph.WindowsTile, false, Theme.Accent, Theme.Paper, 7.5f);
            var selfTitle = new Theme.SelfDeviceTitle(Environment.MachineName, "本机");

            var infoBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Info);
            infoBtn.AccessibleName = "查看本机网络与宿主信息";
            infoBtn.Margin = new Padding(0, 6, 0, 6);
            infoBtn.Click += delegate { ToggleHostDetails(null); };

            var openBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.ChevronRight);
            openBtn.AccessibleName = "前往本机协助设置";
            openBtn.Margin = new Padding(0, 6, 0, 6);
            openBtn.Click += delegate { ShowPage(AssistPage); };

            Theme.Row(row, 48, icon, null, selfTitle, null, infoBtn, openBtn);
            card.Controls.Add(row);
            return card;
        }

        private Control BuildPeerCardRow(TsPeer peer)
        {
            var card = new Theme.Card { Padding = new Padding(18, 12, 18, 12) };
            var row = Theme.Grid(7);
            Theme.FixedColumn(row, 42);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 14);
            Theme.FixedColumn(row, 88);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 34);

            Color tileBg = peer.Online ? Theme.Accent : Color.FromArgb(164, 171, 182);
            var icon = new Theme.IconTile(Theme.Glyph.WindowsTile, false, tileBg, Theme.Paper, 7.5f);

            string displayName = PeerName(peer);
            var identity = new Theme.TwoLineText(
                displayName,
                peer.Ip + "  ·  " + peer.LinkText,
                Theme.TextSize.LabelBold,
                peer.Online ? Theme.Ink : Theme.Muted,
                Theme.Muted);

            var connectBtn = Theme.Action(peer.Online ? "连接" : "离线", peer.Online ? Theme.ButtonStyle.Primary : Theme.ButtonStyle.Secondary);
            connectBtn.Enabled = peer.Online;
            connectBtn.Margin = new Padding(0, 8, 0, 8);
            connectBtn.Click += async delegate { await ConnectToDeviceAsync(peer.Ip, displayName); };

            var detailBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.ChevronRight);
            detailBtn.AccessibleName = "查看设备详情";
            detailBtn.Margin = new Padding(0, 8, 0, 8);
            detailBtn.Click += delegate { OpenDeviceDetail(displayName, peer.Ip, peer.Online, peer.LinkText, true); };

            Theme.Row(row, 52, icon, null, identity, null, connectBtn, null, detailBtn);
            card.Controls.Add(row);
            return card;
        }

        private Control BuildEmptyPeersHintRow(string issue)
        {
            var card = new Theme.Card { Padding = new Padding(18, 12, 18, 12), DashedBorder = true };
            var row = Theme.Grid(5);
            Theme.FixedColumn(row, 42);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 14);
            Theme.FixedColumn(row, 110);

            var icon = new Theme.IconTile(Theme.Glyph.Network, true, Theme.SurfaceSubtle, Theme.Muted, 7.5f);
            var text = new Theme.TwoLineText(
                issue == null ? "暂未发现同网 Tailscale 远端电脑" : "未读取到 Tailscale 设备列表",
                "可直接点右侧「手动连接」输入局域网 IP、公网 IPv6 或 9 位协助设备码。",
                Theme.TextSize.Label,
                Theme.Ink,
                Theme.Muted);

            var manual = Theme.Action("手动连接", Theme.ButtonStyle.Secondary);
            manual.Margin = new Padding(0, 8, 0, 8);
            manual.Click += delegate { OpenStreamPage(null); };

            Theme.Row(row, 52, icon, null, text, null, manual);
            card.Controls.Add(row);
            return card;
        }

        // ==================== DEVICE DETAIL PAGE (UU IMAGE 1 STYLE) ====================

        private Control BuildDeviceDetailPage()
        {
            TableLayoutPanel desk;
            detailViewport = CreatePage(640, out desk);
            detailDesk = desk;

            // Header: [在线/离线] DEVICE-NAME  ...  [收藏]
            var head = Theme.Grid(5);
            Theme.FixedColumn(head, 62);
            Theme.FixedColumn(head, 12);
            Theme.FillColumn(head);
            Theme.FixedColumn(head, 12);
            Theme.FixedColumn(head, 102);

            detailStatusBadge = new Theme.Badge("在线", Theme.PillTone.Success);
            detailTitleLabel = Theme.Text("未选择设备", Theme.TextSize.Title, Theme.Ink);
            detailFavButton = Theme.Action("收藏", Theme.ButtonStyle.Secondary, Theme.Glyph.Star);
            detailFavButton.Margin = new Padding(0, 4, 0, 4);
            detailFavButton.Click += delegate { ToggleDetailDeviceFavorite(); };

            Theme.Row(head, 44, detailStatusBadge, null, detailTitleLabel, null, detailFavButton);
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

            var actDesktop = Theme.Action("发送文件", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Folder);
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

            var actSettings = Theme.Action("画质与抗丢包", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Gear);
            actSettings.Click += delegate
            {
                ShowPage(SettingsPage);
                SelectSettingsTab(1);
            };

            Theme.Row(strip, 52, actStream, MakeToolbarDivider(), actDesktop, MakeToolbarDivider(), actFile, MakeToolbarDivider(), actSettings);
            Theme.Row(hc, 52, strip);
            heroCard.Controls.Add(hc);

            detailHeroCardRow = desk.RowCount;
            Theme.Row(desk, 282, heroCard);
            Theme.Row(desk, 24);

            // Quick-launch quality presets section (matches UU Image 1 "快速启动" below card)
            Theme.Row(desk, 26, Theme.Text("快速启动画质档位", Theme.TextSize.Label, Theme.Muted));
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
            private string deviceName = "";
            private string deviceAddress = "";
            private string linkText = "";
            private bool online = true;
            private bool controlling;
            private bool hovered;
            private bool hoverShuffle;
            private int wallpaperIndex;
            private RectangleF shuffleRect = RectangleF.Empty;

            internal event Action ConnectRequested;
            internal event Action DisconnectRequested;

            internal DevicePreviewCanvas()
            {
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

            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                hoverShuffle = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool overShuffle = !shuffleRect.IsEmpty && shuffleRect.Contains(e.Location);
                if (overShuffle != hoverShuffle)
                {
                    hoverShuffle = overShuffle;
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
                            Theme.Surface(g, livePill, 13f * scale, Color.FromArgb(215, 16, 136, 72), Color.FromArgb(110, 255, 255, 255), 1f);
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
                        Color sBg = hoverShuffle ? Color.FromArgb(215, 22, 34, 52) : Color.FromArgb(148, 18, 28, 44);
                        Theme.Surface(g, shuffleRect, sh * 0.5f, sBg, Color.FromArgb(95, 255, 255, 255), 1f);
                        TextRenderer.DrawText(g, shuffleLabel, sf, Rectangle.Round(shuffleRect), Color.FromArgb(245, 249, 255),
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
                        var ctaRect = new RectangleF((w - ctaW) / 2f, (h - ctaH) / 2f - 4f * scale, ctaW, ctaH);
                        // Soft elevation shadow so pill stands out crisply over bright scenic photos
                        var shadowRect = new RectangleF(ctaRect.X, ctaRect.Y + 3f * scale, ctaRect.Width, ctaRect.Height);
                        Theme.Surface(g, shadowRect, ctaH * 0.5f, Color.FromArgb(72, 10, 18, 32), Color.Transparent, 0f);

                        Color ctaBg;
                        if (controlling)
                            ctaBg = (hovered && !hoverShuffle) ? Color.FromArgb(232, 52, 64) : Theme.Alert;
                        else if (!online)
                            ctaBg = Color.FromArgb(204, 102, 112, 128);
                        else
                            ctaBg = (hovered && !hoverShuffle) ? Theme.AccentHover : Theme.Accent;

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

            Theme.Row(desk, 28, Theme.Text("最近连接", Theme.TextSize.Label, Theme.Muted));
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
            string clean = isStream ? Engines.NormalizeHost(raw, out error) : Engines.NormalizeRustDeskId(raw, out error);
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

            var icon = new Theme.IconTile(device.Stream ? Theme.Glyph.WindowsTile : Theme.Glyph.Assist,
                false, device.Stream ? Theme.Accent : Color.FromArgb(56, 142, 238), Theme.Paper, 7.5f);

            var info = new Theme.TwoLineText(
                device.Address,
                device.Stream ? "低延迟高速串流主机" : "临时协助 / 文件传输设备码",
                Theme.TextSize.LabelBold,
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
                    string error;
                    string address = stream ? Engines.NormalizeHost(line.Substring(2), out error)
                        : Engines.NormalizeRustDeskId(line.Substring(2), out error);
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
