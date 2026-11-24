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
        private TableLayoutPanel hostCardLayout;
        private Control hostCardControl;
        private int hostCardRow = -1;
        private int hostDetailsRow = -1;
        private Control hostCardDetails;
        private bool hostDetailsExpanded;

        private Button computersGroupBtn;
        private bool computersExpanded = true;
        private TableLayoutPanel peerListHost;
        private int peerListRow = -1;
        private bool pageLoaded;

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
            devicesDesk = desk;
            LoadFavorites();

            refreshDevices = Theme.Action("刷新状态", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            refreshDevices.Click += async delegate { await RefreshDashboardAsync(); };
            AddPageHeader(desk, "全部设备", null, refreshDevices, 108);
            Theme.Row(desk, 18);

            // Top Info & Quick Connect Card (matches UU Image 2 top banner card)
            var hostCard = BuildHostInfoCard();
            hostCardRow = desk.RowCount;
            Theme.Row(desk, HostCardHeight(), hostCard);
            Theme.Row(desk, 22);

            // Collapsible "电脑 N" section header
            var groupRow = Theme.Grid(2);
            Theme.FixedColumn(groupRow, 140);
            Theme.FillColumn(groupRow);
            computersGroupBtn = Theme.Action("电脑 1", Theme.ButtonStyle.Quiet, Theme.Glyph.ChevronDown);
            computersGroupBtn.Click += delegate
            {
                computersExpanded = !computersExpanded;
                Theme.ChangeGlyph(computersGroupBtn, computersExpanded ? Theme.Glyph.ChevronDown : Theme.Glyph.ChevronRight);
                SyncPeerListLayout();
            };
            Theme.Row(groupRow, 28, computersGroupBtn, null);
            Theme.Row(desk, 28, groupRow);
            Theme.Row(desk, 10);

            peerListHost = Theme.Grid(1);
            Theme.FillColumn(peerListHost);
            peerListRow = desk.RowCount;
            Theme.Row(desk, 140, peerListHost);
            Theme.Row(desk, 16);
            Theme.Row(desk, 40, deviceBanner);

            DrawPeerRows(null);
            RecalculateDevicesDeskHeight();

            Shown += async delegate { pageLoaded = true; await RefreshDashboardAsync(); };
            return viewport;
        }

        private Control BuildHostInfoCard()
        {
            var card = new Theme.Card { Padding = new Padding(20, 16, 20, 16) };
            hostCardControl = card;
            var content = Theme.Grid(1);
            Theme.FillColumn(content);
            hostCardLayout = content;

            var top = Theme.Grid(5);
            Theme.FixedColumn(top, 24);
            Theme.FixedColumn(top, 10);
            Theme.FillColumn(top);
            Theme.FixedColumn(top, 10);
            Theme.FixedColumn(top, 232);

            var infoIcon = new Theme.IconTile(Theme.Glyph.InfoMark, false, Theme.Accent, Theme.Paper, 12f);
            heroSunshineBadge.AlignRight = true;
            heroPrivacyBadge.AlignRight = true;
            var badges = Theme.Grid(3);
            Theme.FixedColumn(badges, 114);
            Theme.FixedColumn(badges, 8);
            Theme.FixedColumn(badges, 110);
            Theme.Row(badges, 24, heroSunshineBadge, null, heroPrivacyBadge);
            Theme.Row(top, 24, infoIcon, null, Theme.Text("添加或连接你的其他设备", Theme.TextSize.LabelBold, Theme.Ink), null, badges);
            Theme.Row(content, 24, top);
            Theme.Row(content, 6);

            var subWrap = Theme.Grid(2);
            Theme.FixedColumn(subWrap, 34);
            Theme.FillColumn(subWrap);
            Theme.Row(subWrap, 22, null, Theme.Text("在笔记本或另一台电脑直接解压运行 BoBo远程，登录同账号 Tailscale 或输入本机 IP 即可秒连。", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(content, 22, subWrap);
            Theme.Row(content, 14);

            var actionsWrap = Theme.Grid(2);
            Theme.FixedColumn(actionsWrap, 34);
            Theme.FillColumn(actionsWrap);

            var actions = Theme.Grid(8);
            Theme.FixedColumn(actions, 122);
            Theme.FixedColumn(actions, 8);
            Theme.FixedColumn(actions, 122);
            Theme.FixedColumn(actions, 8);
            Theme.FixedColumn(actions, 122);
            Theme.FixedColumn(actions, 10);
            Theme.FixedColumn(actions, 104);
            Theme.FillColumn(actions);

            var manualBtn = Theme.Action("手动连接设备", Theme.ButtonStyle.AccentSoft);
            manualBtn.Click += delegate { OpenStreamPage(null); };

            var pairBtn = Theme.Action("允许连接本机", Theme.ButtonStyle.Secondary);
            pairBtn.Click += async delegate
            {
                ShowPage(AssistPage);
                await BeginLocalPairingAsync();
            };

            heroPrivacyToggle = Theme.Action("开启防窥壁纸", Theme.ButtonStyle.Secondary);
            heroPrivacyToggle.Click += delegate { TogglePrivacyCurtain(false); };

            var addrBtn = Theme.Action("查看本机地址", Theme.ButtonStyle.Link);
            addrBtn.Click += delegate { ToggleHostDetails(addrBtn); };

            Theme.Row(actions, 34, manualBtn, null, pairBtn, null, heroPrivacyToggle, null, addrBtn, null);
            Theme.Row(actionsWrap, 34, null, actions);
            Theme.Row(content, 34, actionsWrap);

            // Expandable local addresses drawer
            var details = Theme.Grid(1);
            Theme.FillColumn(details);
            details.Padding = new Padding(34, 10, 0, 0);
            hostCardDetails = details;

            heroLanLabel.AutoEllipsis = true;
            heroTsLabel.AutoEllipsis = true;
            heroV6Label.AutoEllipsis = true;
            heroCopyLan = MakeAddressCopy("局域网 IP", delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); });
            heroCopyTs = MakeAddressCopy("Tailscale IP", delegate { CopyHostAddress("Tailscale IP", hostSnapshot.TailscaleIpv4); });
            heroCopyV6 = MakeAddressCopy("公网 IPv6", delegate { CopyHostAddress("公网 IPv6", hostSnapshot.PublicIpv6); });

            Theme.Row(details, 1, Theme.Rule());
            Theme.Row(details, 8);
            Theme.Row(details, 28, BuildAddressRow("局域网 IPv4", heroLanLabel, heroCopyLan));
            Theme.Row(details, 4);
            Theme.Row(details, 28, BuildAddressRow("Tailscale 组网", heroTsLabel, heroCopyTs));
            Theme.Row(details, 4);
            Theme.Row(details, 28, BuildAddressRow("公网 IPv6", heroV6Label, heroCopyV6));

            details.Visible = false;
            hostDetailsRow = content.RowCount;
            Theme.Row(content, 0, details);

            card.Controls.Add(content);
            return card;
        }

        private static Control BuildAddressRow(string label, Label value, Button copy)
        {
            var row = Theme.Grid(4);
            Theme.FixedColumn(row, 96);
            Theme.FixedColumn(row, 8);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 68);
            Theme.Row(row, 28, Theme.Text(label, Theme.TextSize.Small, Theme.Muted), null, value, copy);
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
            hostCardLayout.RowStyles[hostDetailsRow].Height = hostDetailsExpanded ? DevicePixels(112) : 0;
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
                height += style.Height;
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
            float scale = (hostCardLayout != null ? hostCardLayout.DeviceDpi : DeviceDpi) / 96f;
            return (int)Math.Round(logical * scale);
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
                string ok = "已复制本机" + label + "：" + value;
                deviceBanner.Set(ok, Theme.PillTone.Success);
                SetStatus(ok, Theme.PillTone.Success);
            }
            catch
            {
                deviceBanner.Set("无法写入剪贴板，请手动记录：" + value, Theme.PillTone.Warning);
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

            if (!hostSnapshot.SunshineInstalled)
                heroSunshineBadge.UpdateStatus("内置宿主待准备", Theme.PillTone.Neutral);
            else if (!hostSnapshot.SunshineRunning)
                heroSunshineBadge.UpdateStatus("被控服务未运行", Theme.PillTone.Warning);
            else
                heroSunshineBadge.UpdateStatus("本机可被控制", Theme.PillTone.Success);

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
                var result = await Task.Run(() => Tailscale.LoadPeers(5200));
                if (IsDisposed) return;
                cachedPeers.Clear();
                if (result.Ok && result.Peers != null) cachedPeers.AddRange(result.Peers);
                DrawPeerRows(result.Ok ? result.Peers : null, result.Ok ? null : result.Error);
                UpdateQuickDeviceShortcut();

                if (result.Ok)
                {
                    deviceBanner.Set(
                        hostSnapshot.SunshineInstalled && !hostSnapshot.SunshineRunning
                            ? "提示：本机 Foundation Sunshine 服务未运行，作为被控端前请先启动。"
                            : hostSnapshot.SunshineInstalled && !hostSnapshot.AntiLossTuned
                                ? "提示：本机存在可优化的抗丢包编码项，建议在「设置 → 低延迟与抗丢包」一键应用。"
                                : "设备列表与本机状态已更新。点击设备行「连接」或右侧箭头可直达远程桌面。",
                        hostSnapshot.SunshineInstalled && (!hostSnapshot.SunshineRunning || !hostSnapshot.AntiLossTuned)
                            ? Theme.PillTone.Warning : Theme.PillTone.Info);
                }
                else
                {
                    deviceBanner.Set("未检测到 Tailscale 组网设备；仍可直接通过局域网 IP、IPv6 或 9 位设备码连接。", Theme.PillTone.Info);
                }
            }
            catch
            {
                if (!IsDisposed)
                    deviceBanner.Set("暂时无法读取组网设备，可点击「手动连接设备」输入 IP 或设备码。", Theme.PillTone.Warning);
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

                float scale = pageLoaded ? DeviceDpi / 96f : 1f;
                int totalDevices = 1; // Always includes This PC

                // Row 1: This PC (UU Image 2 exact match: [Windows blue icon] MACHINE_NAME [本机] ... [i])
                var selfCard = BuildThisPcRow();
                if (scale != 1f) selfCard.Scale(new SizeF(scale, scale));
                Theme.Row(peerListHost, 64f * scale, selfCard);
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
                        Theme.Row(peerListHost, 64f * scale, card);
                        Theme.Row(peerListHost, 10f * scale);
                    }
                }
                totalDevices += peerCount;

                if (peerCount == 0)
                {
                    var hintCard = BuildEmptyPeersHintRow(issue);
                    if (scale != 1f) hintCard.Scale(new SizeF(scale, scale));
                    Theme.Row(peerListHost, 64f * scale, hintCard);
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
        }

        private Control BuildThisPcRow()
        {
            var card = new Theme.Card { Padding = new Padding(16, 10, 16, 10) };
            var row = Theme.Grid(6);
            Theme.FixedColumn(row, 38);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 34);
            Theme.FixedColumn(row, 28);

            var icon = new Theme.IconTile(Theme.Glyph.WindowsTile, false, Theme.Accent, Theme.Paper, 7f);

            var nameLine = Theme.Grid(3);
            Theme.FixedColumn(nameLine, Math.Min(260, Math.Max(110, Environment.MachineName.Length * 11 + 14)));
            Theme.FixedColumn(nameLine, 54);
            Theme.FillColumn(nameLine);
            Theme.Row(nameLine, 26,
                Theme.Text(Environment.MachineName, Theme.TextSize.LabelBold, Theme.Ink),
                new Theme.Badge("本机", Theme.PillTone.Info),
                null);

            var infoBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Info);
            infoBtn.AccessibleName = "查看本机网络与宿主信息";
            infoBtn.Click += delegate { ToggleHostDetails(null); };

            var openBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.ChevronRight);
            openBtn.AccessibleName = "前往本机协助设置";
            openBtn.Click += delegate { ShowPage(AssistPage); };

            Theme.Row(row, 42, icon, null, nameLine, null, infoBtn, openBtn);
            card.Controls.Add(row);
            return card;
        }

        private Control BuildPeerCardRow(TsPeer peer)
        {
            var card = new Theme.Card { Padding = new Padding(16, 10, 16, 10) };
            var row = Theme.Grid(7);
            Theme.FixedColumn(row, 38);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 12);
            Theme.FixedColumn(row, 82);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 32);

            Color tileBg = peer.Online ? Theme.Accent : Color.FromArgb(164, 171, 182);
            var icon = new Theme.IconTile(Theme.Glyph.WindowsTile, false, tileBg, Theme.Paper, 7f);

            var identity = Theme.Grid(1);
            Theme.FillColumn(identity);
            string displayName = PeerName(peer);
            Theme.Row(identity, 22, Theme.Text(displayName, Theme.TextSize.LabelBold, peer.Online ? Theme.Ink : Theme.Muted));
            Theme.Row(identity, 19, Theme.Text(peer.Ip + "  ·  " + peer.LinkText, Theme.TextSize.Small, Theme.Muted));

            var connectBtn = Theme.Action(peer.Online ? "连接" : "离线", peer.Online ? Theme.ButtonStyle.Primary : Theme.ButtonStyle.Secondary);
            connectBtn.Enabled = peer.Online;
            connectBtn.Margin = new Padding(0, 5, 0, 5);
            connectBtn.Click += async delegate { await ConnectToDeviceAsync(peer.Ip, displayName); };

            var detailBtn = Theme.Action("", Theme.ButtonStyle.IconGhost, Theme.Glyph.ChevronRight);
            detailBtn.AccessibleName = "查看设备详情";
            detailBtn.Margin = new Padding(0, 5, 0, 5);
            detailBtn.Click += delegate { OpenDeviceDetail(displayName, peer.Ip, peer.Online, peer.LinkText, true); };

            Theme.Row(row, 42, icon, null, identity, null, connectBtn, null, detailBtn);
            card.Controls.Add(row);
            return card;
        }

        private Control BuildEmptyPeersHintRow(string issue)
        {
            var card = new Theme.Card { Padding = new Padding(16, 10, 16, 10), DashedBorder = true };
            var row = Theme.Grid(5);
            Theme.FixedColumn(row, 38);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 12);
            Theme.FixedColumn(row, 104);

            var icon = new Theme.IconTile(Theme.Glyph.Network, true, Theme.SurfaceSubtle, Theme.Muted, 7f);
            var text = Theme.Grid(1);
            Theme.FillColumn(text);
            Theme.Row(text, 22, Theme.Text(issue == null ? "暂未发现同网 Tailscale 远端电脑" : "未读取到 Tailscale 设备列表", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(text, 19, Theme.Text("可直接点右侧「手动连接」输入局域网 IP、公网 IPv6 或 9 位协助设备码。", Theme.TextSize.Small, Theme.Muted));

            var manual = Theme.Action("手动连接", Theme.ButtonStyle.Secondary);
            manual.Margin = new Padding(0, 4, 0, 4);
            manual.Click += delegate { OpenStreamPage(null); };

            Theme.Row(row, 42, icon, null, text, null, manual);
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
            Theme.FixedColumn(head, 56);
            Theme.FixedColumn(head, 10);
            Theme.FillColumn(head);
            Theme.FixedColumn(head, 10);
            Theme.FixedColumn(head, 92);

            detailStatusBadge = new Theme.Badge("在线", Theme.PillTone.Success);
            detailTitleLabel = Theme.Text("未选择设备", Theme.TextSize.Title, Theme.Ink);
            detailFavButton = Theme.Action("收藏", Theme.ButtonStyle.Secondary, Theme.Glyph.Star);
            detailFavButton.Margin = new Padding(0, 3, 0, 3);
            detailFavButton.Click += delegate { ToggleDetailDeviceFavorite(); };

            Theme.Row(head, 38, detailStatusBadge, null, detailTitleLabel, null, detailFavButton);
            Theme.Row(desk, 38, head);
            Theme.Row(desk, 18);

            // Hero Preview Card with attached clean bottom toolbar (EXACT UU Image 1!)
            var heroCard = new Theme.Card { Padding = new Padding(0) };
            detailHeroCard = heroCard;
            var hc = Theme.Grid(1);
            Theme.FillColumn(hc);
            detailPreviewCardLayout = hc;

            detailPreviewCanvas = new DevicePreviewCanvas();
            detailPreviewCanvas.Click += async delegate
            {
                if (!string.IsNullOrEmpty(detailDeviceAddress))
                    await ConnectToDeviceAsync(detailDeviceAddress, detailDeviceName);
            };
            detailPreviewRow = hc.RowCount;
            Theme.Row(hc, 232, detailPreviewCanvas);
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

            var actDesktop = Theme.Action("通用协助", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Desktop);
            actDesktop.Click += delegate
            {
                ShowPage(AssistPage);
                SelectAssistMode(1);
            };

            var actFile = Theme.Action("文件传输", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Folder);
            actFile.Click += delegate
            {
                ShowPage(AssistPage);
                SelectAssistMode(1);
            };

            var actSettings = Theme.Action("画质与抗丢包", Theme.ButtonStyle.ToolbarAction, Theme.Glyph.Gear);
            actSettings.Click += delegate
            {
                ShowPage(SettingsPage);
                SelectSettingsTab(1);
            };

            Theme.Row(strip, 48, actStream, MakeToolbarDivider(), actDesktop, MakeToolbarDivider(), actFile, MakeToolbarDivider(), actSettings);
            Theme.Row(hc, 48, strip);
            heroCard.Controls.Add(hc);

            detailHeroCardRow = desk.RowCount;
            Theme.Row(desk, 282, heroCard);
            Theme.Row(desk, 24);

            // Quick-launch quality presets section (matches UU Image 1 "快速启动" below card)
            Theme.Row(desk, 26, Theme.Text("快速启动画质档位", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(desk, 10);

            var quickCard = new Theme.Card { Padding = new Padding(16, 16, 16, 16), DashedBorder = true };
            var qGrid = Theme.Grid(5);
            Theme.FillColumn(qGrid);
            Theme.FixedColumn(qGrid, 10);
            Theme.FillColumn(qGrid);
            Theme.FixedColumn(qGrid, 10);
            Theme.FillColumn(qGrid);

            Theme.Row(qGrid, 40,
                MakeQuickPresetButton("外网流畅 1080p60", WanWideProfile), null,
                MakeQuickPresetButton("外网高清 2K60", 2), null,
                MakeQuickPresetButton("弱网抗丢包 720p60", WeakNetProfile));
            Theme.Row(qGrid, 10);
            Theme.Row(qGrid, 40,
                MakeQuickPresetButton("局域网 2K120 高刷", 6), null,
                MakeQuickPresetButton("电竞高刷 2K144", 7), null,
                MakeQuickPresetButton("4K60 超清办公", 8));
            quickCard.Controls.Add(qGrid);
            Theme.Row(desk, 124, quickCard);

            detailViewport.ContentWidthChanged += delegate(int contentW) { AdjustDetailPreviewAspect(contentW); };
            RecalculateDetailDeskHeight();
            return detailViewport;
        }

        private static Control MakeToolbarDivider()
        {
            var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper, Padding = new Padding(0, 12, 0, 12), Margin = Padding.Empty };
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
            if (detailPreviewCardLayout == null || detailDesk == null || detailPreviewRow < 0 || detailHeroCardRow < 0) return;
            float scale = detailDesk.DeviceDpi / 96f;
            int innerW = Math.Max(320, contentWidth - detailDesk.Padding.Horizontal);
            int targetPreviewH = (int)Math.Round(innerW * 0.42f);
            int minH = (int)Math.Round(180 * scale);
            int maxH = (int)Math.Round(280 * scale);
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
            private bool hovered;

            internal DevicePreviewCanvas()
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            internal void SetDevice(string name, string address, string link, bool isOnline)
            {
                deviceName = name ?? "";
                deviceAddress = address ?? "";
                linkText = link ?? "";
                online = isOnline;
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Paper);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float scale = g.DpiX / 96f;

                var rect = new RectangleF(1f, 1f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 1f));
                Color c1 = online ? Color.FromArgb(224, 240, 255) : Color.FromArgb(234, 240, 246);
                Color c2 = online ? Color.FromArgb(188, 220, 252) : Color.FromArgb(217, 225, 235);

                var clipState = g.Save();
                using (var topClip = Theme.RoundTop(rect, 7.5f * scale))
                {
                    g.SetClip(topClip);
                    using (var bg = new LinearGradientBrush(rect, c1, c2, 32f))
                        g.FillRectangle(bg, rect);

                    // Serene frosted-aurora horizon mist + layered alpine silhouettes
                    float h = Height;
                    float w = Width;
                    using (var glow = new SolidBrush(online ? Color.FromArgb(95, 255, 255, 255) : Color.FromArgb(60, 255, 255, 255)))
                        g.FillEllipse(glow, w * 0.56f, -h * 0.25f, w * 0.52f, h * 0.92f);
                    using (var hill1 = new GraphicsPath())
                    {
                        hill1.AddPolygon(new[]
                        {
                            new PointF(0, h),
                            new PointF(0, h * 0.72f),
                            new PointF(w * 0.26f, h * 0.46f),
                            new PointF(w * 0.56f, h * 0.67f),
                            new PointF(w * 0.84f, h * 0.40f),
                            new PointF(w, h * 0.54f),
                            new PointF(w, h)
                        });
                        using (var b = new SolidBrush(online ? Color.FromArgb(46, 51, 122, 255) : Color.FromArgb(38, 100, 114, 134)))
                            g.FillPath(b, hill1);
                    }
                    using (var hill2 = new GraphicsPath())
                    {
                        hill2.AddPolygon(new[]
                        {
                            new PointF(0, h),
                            new PointF(0, h * 0.84f),
                            new PointF(w * 0.34f, h * 0.65f),
                            new PointF(w * 0.66f, h * 0.80f),
                            new PointF(w, h * 0.60f),
                            new PointF(w, h)
                        });
                        using (var b = new SolidBrush(online ? Color.FromArgb(64, 34, 102, 236) : Color.FromArgb(50, 96, 110, 128)))
                            g.FillPath(b, hill2);
                    }

                    // Centered clean connect CTA pill (symmetric icon + text centering)
                    string ctaText = online ? "进入远程桌面" : "设备离线";
                    float ctaH = 42f * scale;
                    float iconSz = (float)Math.Round(16f * scale);
                    float gap = (float)Math.Round(8f * scale);
                    using (var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                    {
                        Size textSz = TextRenderer.MeasureText(g, ctaText, f, new Size(int.MaxValue, (int)ctaH),
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        float contentW = iconSz + gap + textSz.Width;
                        float ctaW = Math.Max(148f * scale, contentW + 44f * scale);
                        var ctaRect = new RectangleF((w - ctaW) / 2f, (h - ctaH) / 2f - 6f * scale, ctaW, ctaH);
                        Color ctaBg = !online ? Color.FromArgb(196, 116, 124, 138) : hovered ? Theme.AccentHover : Theme.Accent;
                        Theme.Surface(g, ctaRect, ctaH * 0.5f, ctaBg, Color.FromArgb(90, 255, 255, 255), 1f);

                        float startX = (float)Math.Round(ctaRect.X + (ctaW - contentW) / 2f);
                        Theme.DrawGlyph(g, Theme.Glyph.Desktop, new RectangleF(startX, (float)Math.Round(ctaRect.Y + (ctaH - iconSz) / 2f), iconSz, iconSz), Theme.Paper);
                        TextRenderer.DrawText(g, ctaText, f,
                            new Rectangle((int)Math.Round(startX + iconSz + gap), (int)Math.Round(ctaRect.Y), textSz.Width + 6, (int)Math.Round(ctaH)),
                            Theme.Paper, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }

                    // Bottom-left crisp address & path chip
                    string meta = string.IsNullOrEmpty(deviceAddress) ? "请选择一台设备" : (deviceAddress + (string.IsNullOrEmpty(linkText) ? "" : "  ·  " + linkText));
                    using (var mf = new Font("Consolas", 8.75f))
                    {
                        Size sz = TextRenderer.MeasureText(g, meta, mf, new Size((int)(w - 40), 26),
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        var chip = new RectangleF(16f * scale, h - 36f * scale, Math.Min(w - 32f * scale, sz.Width + 22f * scale), 24f * scale);
                        Theme.Surface(g, chip, 4f * scale, Color.FromArgb(224, 255, 255, 255), Color.FromArgb(52, 29, 33, 41), 1f);
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
            AddPageHeader(desk, "收藏设备", null, addBtn, 130);
            Theme.Row(desk, 18);

            addFavoriteCard = BuildAddFavoriteCard();
            addFavoriteCard.Visible = false;
            addFavoriteRow = desk.RowCount;
            Theme.Row(desk, 0, addFavoriteCard);

            favoritesListHost = Theme.Grid(1);
            Theme.FillColumn(favoritesListHost);
            favoritesListRow = desk.RowCount;
            Theme.Row(desk, 220, favoritesListHost);
            Theme.Row(desk, 22);

            Theme.Row(desk, 26, Theme.Text("最近连接", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(desk, 10);

            recentListHost = Theme.Grid(1);
            Theme.FillColumn(recentListHost);
            recentListRow = desk.RowCount;
            Theme.Row(desk, 90, recentListHost);
            Theme.Row(desk, 16);
            Theme.Row(desk, 40, favoritesBanner);

            RefreshSavedDevices();
            return viewport;
        }

        private Control BuildAddFavoriteCard()
        {
            var card = new Theme.Card { Padding = new Padding(18, 14, 18, 14) };
            var grid = Theme.Grid(1);
            Theme.FillColumn(grid);
            Theme.Row(grid, 22, Theme.Text("添加新的收藏设备", Theme.TextSize.LabelBold, Theme.Ink));
            Theme.Row(grid, 10);

            var row = Theme.Grid(7);
            Theme.FixedColumn(row, 134);
            Theme.FixedColumn(row, 10);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 84);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 68);

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

            Theme.Row(row, 36, Theme.WrapInput(addFavoriteKind), null, Theme.WrapInput(addFavoriteInput), null, saveBtn, null, cancelBtn);
            Theme.Row(grid, 36, row);
            card.Controls.Add(grid);
            return card;
        }

        private void ToggleAddFavoriteBar(bool? forceOpen = null)
        {
            addFavoriteOpen = forceOpen.HasValue ? forceOpen.Value : !addFavoriteOpen;
            if (addFavoriteCard != null) addFavoriteCard.Visible = addFavoriteOpen;
            if (favoritesDesk != null && addFavoriteRow >= 0)
            {
                favoritesDesk.RowStyles[addFavoriteRow].Height = addFavoriteOpen ? DevicePixels(112) : 0;
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

                float scale = pageLoaded ? DeviceDpi / 96f : 1f;
                if (favorites.Count == 0)
                {
                    var empty = new EmptyFavoritesCanvas();
                    Theme.Row(favoritesListHost, 210f * scale, empty);
                }
                else
                {
                    foreach (var fav in favorites)
                    {
                        var item = fav;
                        var card = BuildSavedDeviceCard(item, true);
                        if (scale != 1f) card.Scale(new SizeF(scale, scale));
                        Theme.Row(favoritesListHost, 64f * scale, card);
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

                float scale = pageLoaded ? DeviceDpi / 96f : 1f;
                var all = new List<SavedDevice>();
                foreach (string h in recent.MoonlightHosts) all.Add(new SavedDevice(true, h));
                foreach (string r in recent.RustDeskIds) all.Add(new SavedDevice(false, r));

                if (all.Count == 0)
                {
                    var card = new Theme.Card { Padding = new Padding(18, 12, 18, 12), DashedBorder = true };
                    card.Controls.Add(Theme.Text("暂无最近连接记录，完成一次远程连接后会自动记录在这里。", Theme.TextSize.Small, Theme.Muted));
                    if (scale != 1f) card.Scale(new SizeF(scale, scale));
                    Theme.Row(recentListHost, 52f * scale, card);
                }
                else
                {
                    foreach (var rec in all)
                    {
                        var card = BuildSavedDeviceCard(rec, false);
                        if (scale != 1f) card.Scale(new SizeF(scale, scale));
                        Theme.Row(recentListHost, 64f * scale, card);
                        Theme.Row(recentListHost, 10f * scale);
                    }
                }
            }
            finally { recentListHost.ResumeLayout(true); }
            RecalculateFavoritesDeskHeight();
        }

        private Control BuildSavedDeviceCard(SavedDevice device, bool isFavoriteList)
        {
            var card = new Theme.Card { Padding = new Padding(16, 10, 16, 10) };
            var row = Theme.Grid(7);
            Theme.FixedColumn(row, 38);
            Theme.FixedColumn(row, 14);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 12);
            Theme.FixedColumn(row, 78);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 78);

            var icon = new Theme.IconTile(device.Stream ? Theme.Glyph.WindowsTile : Theme.Glyph.Assist,
                false, device.Stream ? Theme.Accent : Color.FromArgb(56, 142, 238), Theme.Paper, 7f);

            var info = Theme.Grid(1);
            Theme.FillColumn(info);
            Theme.Row(info, 22, Theme.Text(device.Address, Theme.TextSize.LabelBold, Theme.Ink));
            Theme.Row(info, 19, Theme.Text(device.Stream ? "低延迟高速串流主机" : "临时协助 / 文件传输设备码", Theme.TextSize.Small, Theme.Muted));

            var connect = Theme.Action("连接", Theme.ButtonStyle.Primary);
            connect.Margin = new Padding(0, 4, 0, 4);
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
            second.Margin = new Padding(0, 4, 0, 4);

            Theme.Row(row, 42, icon, null, info, null, connect, null, second);
            card.Controls.Add(row);
            return card;
        }

        private void RecalculateFavoritesDeskHeight()
        {
            if (favoritesDesk == null) return;
            if (favoritesListRow >= 0 && favoritesListHost != null)
                favoritesDesk.RowStyles[favoritesListRow].Height = Math.Max(DevicePixels(64), SumRows(favoritesListHost));
            if (recentListRow >= 0 && recentListHost != null)
                favoritesDesk.RowStyles[recentListRow].Height = Math.Max(DevicePixels(54), SumRows(recentListHost));
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
