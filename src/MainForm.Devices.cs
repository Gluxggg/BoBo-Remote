using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private readonly Theme.StatusBanner deviceBanner = new Theme.StatusBanner("正在检查本机网络地址、Sunshine 状态与组网设备…");
        private readonly Label networkStatus = Theme.Text("", Theme.TextSize.Small, Theme.Muted);
        private readonly List<SavedDevice> favorites = new List<SavedDevice>();
        private readonly TableLayoutPanel peerRows = Theme.Grid(1);
        private readonly Theme.Badge heroSunshineBadge = new Theme.Badge("检测宿主中…", Theme.PillTone.OnHero);
        private readonly Theme.Badge heroPrivacyBadge = new Theme.Badge("防窥待命", Theme.PillTone.OnHero);
        private readonly Label heroLanLabel = Theme.Text("局域网：检测中…", Theme.TextSize.Code, Theme.HeroText);
        private readonly Label heroTsLabel = Theme.Text("Tailscale：检测中…", Theme.TextSize.Code, Theme.HeroText);
        private readonly Label heroV6Label = Theme.Text("IPv6：检测中…", Theme.TextSize.Code, Theme.HeroText);
        private Button heroCopyLan;
        private Button heroCopyTs;
        private Button heroCopyV6;
        private Button heroPrivacyToggle;
        private ComboBox savedDevices;
        private ComboBox favoriteDevices;
        private Button refreshDevices;
        private bool pageLoaded;

        private Control BuildDevicesPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(685, out desk);
            LoadFavorites();

            refreshDevices = Theme.Action("刷新状态", Theme.ButtonStyle.Secondary, Theme.Glyph.Refresh);
            refreshDevices.Click += async delegate { await RefreshDashboardAsync(); };
            AddPageHeader(desk, "我的设备", "本机直连地址、防窥快捷开关与同组网设备总览", refreshDevices, 118);
            Theme.Row(desk, 14);
            Theme.Row(desk, 198, BuildHostCard());
            Theme.Row(desk, 10);
            Theme.Row(desk, 42, deviceBanner);
            Theme.Row(desk, 12);

            var heading = Theme.Grid(3);
            Theme.FillColumn(heading);
            Theme.FixedColumn(heading, 12);
            Theme.FixedColumn(heading, 116);
            var manual = Theme.Action("手动连接", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Plus);
            manual.Click += delegate { OpenStreamPage(null, false); };
            Theme.Row(heading, 34, Theme.Text("组网设备", Theme.TextSize.Section, Theme.Ink), null, manual);
            Theme.Row(desk, 34, heading);
            Theme.Row(desk, 8);
            Theme.Row(desk, 152, BuildNetworkList());
            Theme.Row(desk, 22, networkStatus);
            Theme.Row(desk, 8);
            Theme.Row(desk, 126, BuildSavedDevices());

            Shown += async delegate { pageLoaded = true; await RefreshDashboardAsync(); };
            return viewport;
        }

        private Control BuildHostCard()
        {
            var host = new Theme.Hero();
            var content = Theme.Grid(1);
            Theme.FillColumn(content);

            // Top header: label + live pills
            var top = Theme.Grid(5);
            Theme.FixedColumn(top, 78);
            Theme.FixedColumn(top, 8);
            Theme.FixedColumn(top, 170);
            Theme.FixedColumn(top, 8);
            Theme.FixedColumn(top, 138);
            Theme.Row(top, 26,
                Theme.Text("本机设备", Theme.TextSize.Label, Theme.HeroText), null,
                heroSunshineBadge, null, heroPrivacyBadge);
            Theme.Row(content, 26, top);

            var name = Theme.Text(Environment.MachineName, Theme.TextSize.Title, Theme.Paper);
            name.AccessibleName = "本机电脑名称";
            Theme.Row(content, 40, name);
            Theme.Row(content, 6);

            // Address quick-copy bar
            var addrs = Theme.Grid(8);
            Theme.FixedColumn(addrs, 185);
            Theme.FixedColumn(addrs, 58);
            Theme.FixedColumn(addrs, 12);
            Theme.FixedColumn(addrs, 195);
            Theme.FixedColumn(addrs, 58);
            Theme.FixedColumn(addrs, 12);
            Theme.FillColumn(addrs);
            Theme.FixedColumn(addrs, 62);

            heroCopyLan = Theme.Action("复制", Theme.ButtonStyle.OnHeroQuiet);
            heroCopyTs = Theme.Action("复制", Theme.ButtonStyle.OnHeroQuiet);
            heroCopyV6 = Theme.Action("复制", Theme.ButtonStyle.OnHeroQuiet);
            heroCopyLan.Click += delegate { CopyHostAddress("局域网 IP", hostSnapshot.LanIpv4); };
            heroCopyTs.Click += delegate { CopyHostAddress("Tailscale IP", hostSnapshot.TailscaleIpv4); };
            heroCopyV6.Click += delegate { CopyHostAddress("公网 IPv6", hostSnapshot.PublicIpv6); };
            heroCopyLan.Enabled = heroCopyTs.Enabled = heroCopyV6.Enabled = false;

            Theme.Row(addrs, 28,
                heroLanLabel, heroCopyLan, null,
                heroTsLabel, heroCopyTs, null,
                heroV6Label, heroCopyV6);
            Theme.Row(content, 28, addrs);
            Theme.Row(content, 16);

            // Action buttons row
            var actions = Theme.Grid(6);
            Theme.FixedColumn(actions, 148);
            Theme.FixedColumn(actions, 10);
            Theme.FixedColumn(actions, 168);
            Theme.FixedColumn(actions, 10);
            Theme.FixedColumn(actions, 162);
            Theme.FillColumn(actions);

            var receive = Theme.Action("打开本机远控", Theme.ButtonStyle.OnHero, Theme.Glyph.Desktop);
            receive.Click += async delegate { await OpenLocalRustDesk(receive); };
            var pair = Theme.Action("本机串流配对页", Theme.ButtonStyle.OnHeroQuiet, Theme.Glyph.Pin);
            pair.Click += delegate { OpenDeviceAction(Engines.OpenFoundationPairing, "已打开本机 47990 配对页，请输入控制端显示的四位 PIN。"); };
            heroPrivacyToggle = Theme.Action("开启防窥壁纸", Theme.ButtonStyle.OnHeroQuiet, Theme.Glyph.Shield);
            heroPrivacyToggle.Click += delegate { TogglePrivacyCurtain(false); };

            Theme.Row(actions, 38, receive, null, pair, null, heroPrivacyToggle);
            Theme.Row(content, 38, actions);
            host.Controls.Add(content);
            return host;
        }

        private void CopyHostAddress(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                deviceBanner.Set("本机暂未获取到" + label + "。", Theme.PillTone.Warning);
                return;
            }
            try
            {
                Clipboard.SetText(value);
                deviceBanner.Set("已复制本机" + label + "：" + value + "（同局域网或双端公网 IPv6 直连延迟最低）。", Theme.PillTone.Success);
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
            heroLanLabel.Text = "局域网 " + (string.IsNullOrEmpty(hostSnapshot.LanIpv4) ? "未连接" : hostSnapshot.LanIpv4);
            heroTsLabel.Text = "组网 " + (string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4) ? "未登录" : hostSnapshot.TailscaleIpv4);
            heroV6Label.Text = "IPv6 " + (string.IsNullOrEmpty(hostSnapshot.PublicIpv6) ? "无公网" : ShortenIpv6(hostSnapshot.PublicIpv6));
            heroCopyLan.Enabled = !string.IsNullOrEmpty(hostSnapshot.LanIpv4);
            heroCopyTs.Enabled = !string.IsNullOrEmpty(hostSnapshot.TailscaleIpv4);
            heroCopyV6.Enabled = !string.IsNullOrEmpty(hostSnapshot.PublicIpv6);

            if (!hostSnapshot.SunshineInstalled)
                heroSunshineBadge.UpdateStatus("控制端模式（无宿主）", Theme.PillTone.OnHero);
            else if (!hostSnapshot.SunshineRunning)
                heroSunshineBadge.UpdateStatus("宿主已停止", Theme.PillTone.Warning);
            else if (hostSnapshot.AntiLossTuned)
                heroSunshineBadge.UpdateStatus("宿主运行 · 配置项已就绪", Theme.PillTone.OnHero);
            else
                heroSunshineBadge.UpdateStatus("宿主运行 · 待检查配置", Theme.PillTone.Warning);

            UpdateDiagnosticsUi();
            UpdatePrivacyIndicators();
        }

        private static string ShortenIpv6(string ipv6)
        {
            if (string.IsNullOrEmpty(ipv6) || ipv6.Length <= 22) return ipv6;
            return ipv6.Substring(0, 12) + "…" + ipv6.Substring(ipv6.Length - 8);
        }

        private async Task OpenLocalRustDesk(Button receive)
        {
            receive.Enabled = false;
            deviceBanner.Set("正在打开本机 RustDesk…", Theme.PillTone.Info);
            try
            {
                string error = await Task.Run(() =>
                {
                    string detail;
                    return Engines.LaunchRustDesk(out detail) ? null : detail;
                });
                if (IsDisposed) return;
                deviceBanner.Set(error ?? "请在 RustDesk 窗口中查看本机设备码并授权远程协助。", error == null ? Theme.PillTone.Success : Theme.PillTone.Alert);
            }
            finally { if (!IsDisposed) receive.Enabled = true; }
        }

        private Control BuildNetworkList()
        {
            var card = new Theme.Card { Padding = new Padding(16, 8, 14, 10) };
            var scroller = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, AutoScroll = true, BackColor = Theme.Paper };
            Theme.FillColumn(peerRows);
            peerRows.Dock = DockStyle.Top;
            scroller.Controls.Add(peerRows);
            card.Controls.Add(scroller);
            DrawPeerRows(null);
            return card;
        }

        private async Task RefreshNetworkDevices()
        {
            if (!refreshDevices.Enabled) return;
            refreshDevices.Enabled = false;
            networkStatus.ForeColor = Theme.Muted;
            networkStatus.Text = "正在读取 Tailscale 组网设备…";
            try
            {
                var result = await Task.Run(() => Tailscale.LoadPeers(5200));
                if (IsDisposed) return;
                DrawPeerRows(result.Ok ? result.Peers : null, result.Ok ? null : result.Error);
                networkStatus.Text = result.Ok && result.Peers.Count > 0
                    ? "同局域网建议直接使用局域网 IP；跨网首次连接请先配对。"
                    : "";
                if (result.Ok)
                    deviceBanner.Set(hostSnapshot.SunshineInstalled && !hostSnapshot.SunshineRunning
                        ? "提示：本机 Foundation Sunshine 服务未运行，串流前请先启动。"
                        : hostSnapshot.SunshineInstalled && !hostSnapshot.AntiLossTuned
                            ? "提示：本机 Sunshine 存在待检查的抗丢包配置项，建议前往「性能与设置」查看。"
                            : "设备与本机状态已更新。点击「进入桌面」：" + StreamAvailabilityText(),
                        hostSnapshot.SunshineInstalled && (!hostSnapshot.SunshineRunning || !hostSnapshot.AntiLossTuned)
                            ? Theme.PillTone.Warning : Theme.PillTone.Info);
            }
            catch (Exception)
            {
                if (!IsDisposed)
                {
                    networkStatus.ForeColor = Theme.Alert;
                    networkStatus.Text = "暂时读不到组网设备，可刷新或点击「手动连接」。";
                }
            }
            finally { if (!IsDisposed) refreshDevices.Enabled = true; }
        }

        private void DrawPeerRows(List<TsPeer> peers, string issue = null)
        {
            peerRows.SuspendLayout();
            try
            {
                for (int i = peerRows.Controls.Count - 1; i >= 0; i--) peerRows.Controls[i].Dispose();
                peerRows.RowStyles.Clear();
                peerRows.RowCount = 0;
                float scale = pageLoaded ? CurrentAutoScaleDimensions.Width / 96f : 1f;
                int count = 0;
                if (peers != null)
                {
                    foreach (var peer in peers)
                    {
                        if (string.IsNullOrWhiteSpace(peer.Ip)) continue;
                        if (count++ > 0) Theme.Row(peerRows, scale, Theme.Rule());
                        var row = BuildPeerRow(peer);
                        if (scale != 1f) row.Scale(new SizeF(scale, scale));
                        Theme.Row(peerRows, 62 * scale, row);
                    }
                }
                if (count == 0)
                {
                    var empty = Theme.Grid(3);
                    Theme.FixedColumn(empty, 44);
                    Theme.FixedColumn(empty, 14);
                    Theme.FillColumn(empty);
                    var text = Theme.Grid(1);
                    Theme.FillColumn(text);
                    Theme.Row(text, 23, Theme.Text(issue == null ? "没有可显示的组网设备" : "暂时无法读取设备列表", Theme.TextSize.Label, Theme.Ink));
                    Theme.Row(text, 38, Theme.Text(issue ?? "两台电脑登录同一 Tailscale 网络后点「刷新状态」，或直接点「手动连接」输入局域网 / IPv6 地址。", Theme.TextSize.Small,
                        issue == null ? Theme.Muted : Theme.Alert));
                    text.Padding = new Padding(0, 14, 0, 0);
                    Theme.Row(empty, 82, new Theme.IconTile(Theme.Glyph.Devices), null, text);
                    if (scale != 1f) empty.Scale(new SizeF(scale, scale));
                    Theme.Row(peerRows, 92 * scale, empty);
                }
                peerRows.Height = count == 0 ? (int)(92 * scale) : (int)Math.Ceiling((count * 63 - 1) * scale);
            }
            finally { peerRows.ResumeLayout(true); }
        }

        private Control BuildPeerRow(TsPeer peer)
        {
            var row = Theme.Grid(9);
            Theme.FixedColumn(row, 42);
            Theme.FixedColumn(row, 12);
            Theme.FillColumn(row);
            Theme.FixedColumn(row, 74);
            Theme.FixedColumn(row, 10);
            Theme.FixedColumn(row, 92);
            Theme.FixedColumn(row, 8);
            Theme.FixedColumn(row, 104);
            Theme.FixedColumn(row, 2);

            var identity = Theme.Grid(1);
            Theme.FillColumn(identity);
            identity.Padding = new Padding(0, 8, 6, 0);
            string name = string.IsNullOrWhiteSpace(peer.HostName) ? peer.DnsName : peer.HostName;
            Theme.Row(identity, 23, Theme.Text(string.IsNullOrWhiteSpace(name) ? peer.Ip : name, Theme.TextSize.Label, Theme.Ink));
            Theme.Row(identity, 19, Theme.Text(peer.Ip + "  ·  " + peer.LinkText, Theme.TextSize.Code, Theme.Muted));

            var pair = Theme.Action("首次配对", Theme.ButtonStyle.Secondary);
            var open = Theme.Action("进入桌面", Theme.ButtonStyle.Primary);
            pair.Margin = new Padding(0, 13, 0, 13);
            open.Margin = pair.Margin;
            pair.Enabled = open.Enabled = peer.Online;
            pair.Click += delegate { OpenStreamPage(peer.Ip, true); };
            open.Click += delegate { OpenStreamPage(peer.Ip, false); StartStream(false); };
            var badge = new Theme.Badge(peer.Online ? "在线" : "离线", peer.Online);
            Theme.Row(row, 61, new Theme.IconTile(Theme.Glyph.Desktop), null, identity, badge, null, pair, null, open);
            return row;
        }

        private void OpenStreamPage(string address, bool pair)
        {
            ShowPage(AssistPage);
            if (!string.IsNullOrEmpty(address)) streamHost.Text = address;
            streamHost.Focus();
            if (pair) StartStream(true);
        }

        private Control BuildSavedDevices()
        {
            var card = new Theme.Card { Padding = new Padding(18, 10, 18, 12) };
            var columns = Theme.Grid(3);
            Theme.FillColumn(columns);
            Theme.FixedColumn(columns, 22);
            Theme.FillColumn(columns);

            var history = Theme.Grid(1);
            Theme.FillColumn(history);
            Theme.Row(history, 24, Theme.Text("最近连接", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(history, 4);
            savedDevices = Theme.Choice("最近连接设备", false);
            Theme.Row(history, 36, Theme.WrapInput(savedDevices));
            Theme.Row(history, 8);
            var historyActions = Theme.Grid(4);
            Theme.FixedColumn(historyActions, 84);
            Theme.FixedColumn(historyActions, 8);
            Theme.FixedColumn(historyActions, 84);
            Theme.FillColumn(historyActions);
            var use = Theme.Action("填入使用", Theme.ButtonStyle.AccentSoft);
            use.Click += delegate { SelectSavedDevice(savedDevices.SelectedItem as SavedDevice); };
            var add = Theme.Action("加入收藏", Theme.ButtonStyle.Secondary);
            add.Click += delegate
            {
                var selected = savedDevices.SelectedItem as SavedDevice;
                if (selected == null)
                {
                    deviceBanner.Set("请先从最近连接中选择要收藏的设备。", Theme.PillTone.Warning);
                    return;
                }
                if (favorites.Exists(item => item.SameAs(selected))) return;
                favorites.Insert(0, selected);
                SaveFavorites();
            };
            Theme.Row(historyActions, 30, use, null, add);
            Theme.Row(history, 30, historyActions);

            var saved = Theme.Grid(1);
            Theme.FillColumn(saved);
            Theme.Row(saved, 24, Theme.Text("我的收藏", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(saved, 4);
            favoriteDevices = Theme.Choice("已收藏的设备", false);
            Theme.Row(saved, 36, Theme.WrapInput(favoriteDevices));
            Theme.Row(saved, 8);
            var savedActions = Theme.Grid(4);
            Theme.FixedColumn(savedActions, 84);
            Theme.FixedColumn(savedActions, 8);
            Theme.FixedColumn(savedActions, 84);
            Theme.FillColumn(savedActions);
            var useSaved = Theme.Action("填入使用", Theme.ButtonStyle.AccentSoft);
            useSaved.Click += delegate { SelectSavedDevice(favoriteDevices.SelectedItem as SavedDevice); };
            var remove = Theme.Action("移出收藏", Theme.ButtonStyle.Secondary);
            remove.Click += delegate
            {
                var selected = favoriteDevices.SelectedItem as SavedDevice;
                if (selected == null) return;
                favorites.Remove(selected);
                SaveFavorites();
            };
            Theme.Row(savedActions, 30, useSaved, null, remove);
            Theme.Row(saved, 30, savedActions);

            Theme.Row(columns, 102, history, null, saved);
            card.Controls.Add(columns);
            return card;
        }

        private void RefreshSavedDevices()
        {
            savedDevices.Items.Clear();
            foreach (string target in recent.RustDeskIds) savedDevices.Items.Add(new SavedDevice(false, target));
            foreach (string target in recent.MoonlightHosts) savedDevices.Items.Add(new SavedDevice(true, target));
            if (savedDevices.Items.Count == 0) savedDevices.Items.Add("暂无连接记录");
            savedDevices.SelectedIndex = 0;
            RefreshFavorites();
        }

        private void SelectSavedDevice(SavedDevice selected)
        {
            if (selected == null) return;
            ShowPage(AssistPage);
            ComboBox field = selected.Stream ? streamHost : remoteId;
            field.Text = selected.Address;
            field.Focus();
        }

        private void RefreshFavorites()
        {
            favoriteDevices.Items.Clear();
            foreach (var favorite in favorites) favoriteDevices.Items.Add(favorite);
            if (favorites.Count == 0) favoriteDevices.Items.Add("暂无收藏");
            favoriteDevices.SelectedIndex = 0;
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
            RefreshFavorites();
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                File.WriteAllLines(FavoritesPath, favorites.ConvertAll(item => (item.Stream ? "S\t" : "R\t") + item.Address));
                deviceBanner.Set("收藏已保存到本机。", Theme.PillTone.Success);
            }
            catch (IOException) { FavoriteError(); }
            catch (UnauthorizedAccessException) { FavoriteError(); }
        }

        private void FavoriteError()
        {
            deviceBanner.Set("无法保存收藏，本次改动仅在窗口打开期间生效。", Theme.PillTone.Warning);
        }

        private void OpenDeviceAction(OpenApplication action, string instruction)
        {
            string detail;
            bool launched = action(out detail);
            deviceBanner.Set(launched ? instruction : detail, launched ? Theme.PillTone.Success : Theme.PillTone.Alert);
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
            public override string ToString() { return (Stream ? "桌面串流 · " : "通用远控 · ") + Address; }
        }
    }
}
