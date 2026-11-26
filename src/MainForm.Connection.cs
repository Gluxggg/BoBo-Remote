using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private TableLayoutPanel clientPairCardLayout;
        private Control clientPairCard;
        private Control clientPairPanel;
        private TableLayoutPanel clientPairPanelLayout;
        private int clientPairPanelRow;
        private int assistStreamCardRow = -1;
        private Label clientPairTargetLabel;
        private Label clientPairStatusLabel;
        private TextBox clientPairPinText;
        private Button clientPairStartButton;
        private Button clientPairCancelButton;
        private bool clientPairBusy;
        private int clientPairOperation;
        private CancellationTokenSource clientPairCancellation;
        private string clientPairHost = "";
        private string clientPairHostName = "";
        private int connectRequestGeneration;
        private ComboBox connectionRouteChoice;
        private Label connectionRouteHint;
        private BoBoConnectionRoute clientPairRoute;
        private BoBoConnectionRoute activeConnectionRoute;

        private BoBoRoutePreference SelectedConnectionRoute
        {
            get { return connectionRouteChoice == null ? BoBoRoutePreference.Auto : (BoBoRoutePreference)connectionRouteChoice.SelectedIndex; }
        }

        private void ShowConnectionRouteHint(BoBoConnectionRoute route)
        {
            if (connectionRouteHint == null) return;
            if (route != null)
            {
                connectionRouteHint.Text = route.Label;
                return;
            }
            switch (SelectedConnectionRoute)
            {
                case BoBoRoutePreference.LanOnly:
                    connectionRouteHint.Text = "输入同网段 IP，或从「发现设备」选择。";
                    break;
                case BoBoRoutePreference.Tailnet:
                    connectionRouteHint.Text = "使用 Tailscale 地址。直连可用时走 P2P，必要时走中继。";
                    break;
                default:
                    connectionRouteHint.Text = "优先使用已核验身份的同网直连。";
                    break;
            }
        }

        private void TrackSessionRoute(NativeStreamForm session, BoBoConnectionRoute route)
        {
            session.ConnectionRouteLabelProvider = delegate { return route == null ? "" : route.Label; };
            if (route == null || !route.IsTailnet) return;
            var timer = new System.Windows.Forms.Timer { Interval = 10000 };
            bool refreshing = false;
            timer.Tick += async delegate
            {
                if (refreshing || IsDisposed || session.IsDisposed || activeNativeStreamForm != session) return;
                refreshing = true;
                try
                {
                    LinkProbeResult result = await Task.Run(() => Tailscale.Probe(route.RemoteAddress.ToString(), 2200));
                    if (!IsDisposed && !session.IsDisposed && activeNativeStreamForm == session)
                    {
                        route.UpdateTailnetPath(result.LastPong);
                        ShowConnectionRouteHint(route);
                    }
                }
                catch { }
                finally { refreshing = false; }
            };
            session.Disposed += delegate { timer.Dispose(); };
            timer.Start();
        }

        private Control BuildClientPairPanel()
        {
            var panel = Theme.Grid(1);
            Theme.FillColumn(panel);
            panel.Padding = new Padding(22, 4, 22, 10);
            clientPairPanelLayout = panel;
            Theme.Row(panel, 1, Theme.Rule());
            Theme.Row(panel, 14);

            clientPairTargetLabel = Theme.Text("这台电脑首次通过 BoBo远程 连接，需要完成一次 4 位临时码双向授权。", Theme.TextSize.LabelBold, Theme.Ink);
            Theme.Row(panel, 26, clientPairTargetLabel);
            Theme.Row(panel, 8);

            clientPairPinText = Theme.InputBox("4 位配对码", "");
            clientPairPinText.MaxLength = 4;
            clientPairPinText.TextAlign = HorizontalAlignment.Center;
            clientPairPinText.Font = new Font("Consolas", 14f, FontStyle.Bold);
            clientPairPinText.KeyPress += delegate(object sender, KeyPressEventArgs args)
            {
                if (!char.IsControl(args.KeyChar) && !char.IsDigit(args.KeyChar)) args.Handled = true;
            };

            var pinRow = Theme.Grid(3);
            Theme.FixedColumn(pinRow, 168);
            Theme.FixedColumn(pinRow, 12);
            Theme.FillColumn(pinRow);
            Theme.Row(pinRow, 42, Theme.WrapInput(clientPairPinText), null,
                Theme.Text("请在被控端 BoBo远程 点「生成配对码」，并将显示的 4 位数字输入此处。", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(panel, 42, pinRow);
            Theme.Row(panel, 10);

            var actions = Theme.Grid(4);
            Theme.FixedColumn(actions, 148);
            Theme.FixedColumn(actions, 10);
            Theme.FixedColumn(actions, 92);
            Theme.FillColumn(actions);
            clientPairStartButton = Theme.Action("配对并连接", Theme.ButtonStyle.Primary, Theme.Glyph.Keypad);
            clientPairCancelButton = Theme.Action("取消", Theme.ButtonStyle.Secondary);
            clientPairStartButton.Click += async delegate { await StartClientPairingAsync(); };
            clientPairCancelButton.Click += delegate { CancelClientPairing("已取消本次配对。"); };
            clientPairPinText.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter && !args.Shift)
                {
                    args.SuppressKeyPress = true;
                    if (clientPairStartButton.Enabled) clientPairStartButton.PerformClick();
                }
            };
            Theme.Row(actions, 38, clientPairStartButton, null, clientPairCancelButton);
            Theme.Row(panel, 38, actions);
            Theme.Row(panel, 8);

            clientPairStatusLabel = Theme.Text("等待输入被控端 4 位配对码。", Theme.TextSize.Small, Theme.Muted);
            Theme.Row(panel, 28, clientPairStatusLabel);
            return panel;
        }

        private int ClientStreamCardHeight()
        {
            if (clientPairCardLayout == null || clientPairCard == null) return 0;
            return SumRows(clientPairCardLayout) + clientPairCard.Padding.Vertical;
        }

        private void SetClientPairPanelVisible(bool visible)
        {
            if (clientPairPanel == null || clientPairCardLayout == null) return;
            clientPairPanel.Visible = visible;
            clientPairCardLayout.RowStyles[clientPairPanelRow].Height =
                visible ? SumRows(clientPairPanelLayout) : 0;
            if (assistDesk != null && assistStreamCardRow >= 0)
                assistDesk.RowStyles[assistStreamCardRow].Height = ClientStreamCardHeight();
            if (visible) SetClientPairBusyUi(clientPairBusy);
            LayoutAssistDesk();
        }

        private void LayoutAssistDesk()
        {
            if (assistDesk == null) return;
            assistDesk.Height = SumRows(assistDesk);
            assistDesk.PerformLayout();
            NotifyOnboardingLayoutChanged();
        }

        private void SetClientPairStatus(string text, Color color)
        {
            if (clientPairStatusLabel == null) return;
            clientPairStatusLabel.ForeColor = color;
            clientPairStatusLabel.Text = text;
        }

        private void SetClientPairBusyUi(bool busy)
        {
            if (clientPairPinText != null) clientPairPinText.Enabled = !busy;
            if (clientPairStartButton != null)
            {
                clientPairStartButton.Enabled = !busy;
                clientPairStartButton.Text = busy ? "双向验证中…" : "配对并连接";
                clientPairStartButton.AccessibleName = clientPairStartButton.Text;
            }
            if (clientPairCancelButton != null)
                clientPairCancelButton.Enabled = busy || (clientPairPanel != null && clientPairPanel.Visible);
        }

        private string ResolveBoBoHostTarget(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return "";
            if (boboHostServer != null)
            {
                string resolved = boboHostServer.ResolveTargetHost(rawInput);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
            BoBoAuthorizedPeer saved = BoBoIdentityStore.Default.FindPeerByCodeOrHost(rawInput);
            if (saved != null && !string.IsNullOrWhiteSpace(saved.LastHost))
                return saved.LastHost;
            return rawInput.Trim();
        }

        private async Task ConnectToDeviceAsync(string host, string hostName)
        {
            string resolvedInput = ResolveBoBoHostTarget(host);
            if (BoBoIdentityStore.IsNineDigitCode(resolvedInput))
            {
                SetStatus("未在同局域网或已授权记录中找到设备码 " + BoBoIdentityStore.FormatDeviceCode(resolvedInput) + " 对应的 IP；请先点「发现设备」或输入对方 IP 地址。", Theme.PillTone.Warning);
                streamHost.Focus();
                return;
            }

            string error;
            string cleanHost = Engines.NormalizeHost(resolvedInput, out error);
            if (cleanHost == null)
            {
                SetStatus(error, Theme.PillTone.Warning);
                streamHost.Focus();
                return;
            }

            BoBoRoutePreference preference = SelectedConnectionRoute;
            IPAddress requestedAddress;
            if (preference == BoBoRoutePreference.LanOnly &&
                (!IPAddress.TryParse(cleanHost, out requestedAddress) || !BoBoConnectionRoute.IsOnLocalSubnet(requestedAddress, null)))
            {
                SetStatus("局域网直连需要对方的同网段 IP 地址，可在「发现设备」选择。", Theme.PillTone.Warning);
                return;
            }
            if (preference == BoBoRoutePreference.Tailnet && !Tailscale.IsTailnetAddress(cleanHost))
            {
                SetStatus("跨网连接请输入对方的 Tailscale IP 或完整 MagicDNS 地址。", Theme.PillTone.Warning);
                return;
            }

            int request = ++connectRequestGeneration;
            if (clientPairBusy)
            {
                if (string.Equals(clientPairHost, cleanHost, StringComparison.OrdinalIgnoreCase))
                {
                    SetClientPairPanelVisible(true);
                    SetClientPairStatus("正在与对方 BoBo远程 完成双向配对，请稍候。", Theme.Muted);
                    return;
                }
                CancelClientPairing("已切换连接目标。");
            }

            clientPairHost = cleanHost;
            clientPairHostName = hostName ?? "";
            ShowPage(AssistPage);
            SelectAssistMode(0);
            streamHost.Text = cleanHost;
            SetClientPairPanelVisible(false);
            SetStatus("正在验证对方是否已打开 BoBo远程（" + (string.IsNullOrEmpty(hostName) ? cleanHost : hostName) + "）…", Theme.PillTone.Info);

            BoBoHostProbeResult probe;
            try
            {
                probe = await BoBoStreamClient.ProbeHostAsync(
                    cleanHost, BoBoProtocol.DefaultTcpPort, BoBoIdentityStore.Default, 4500, CancellationToken.None);
            }
            catch (Exception ex)
            {
                probe = new BoBoHostProbeResult { Error = "探测目标 BoBo远程 失败：" + ex.Message };
            }
            if (IsDisposed || request != connectRequestGeneration) return;

            if (!probe.Reachable || !probe.IsBoBoHost)
            {
                string offlineMsg = string.IsNullOrEmpty(probe.Error)
                    ? "未检测到对方打开 BoBo远程（必须双方同时打开 BoBo远程 才可连控）。"
                    : probe.Error;
                SetStatus(offlineMsg, Theme.PillTone.Alert);
                deviceBanner.Set(offlineMsg, Theme.PillTone.Alert);
                return;
            }

            if (!probe.AllowIncoming)
            {
                string disabledMsg = "对方 BoBo远程 当前关闭了「允许本机被控」，请在被控端开启后再连。";
                SetStatus(disabledMsg, Theme.PillTone.Warning);
                deviceBanner.Set(disabledMsg, Theme.PillTone.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(probe.HostName))
                clientPairHostName = probe.HostName;

            var route = new BoBoConnectionRoute(probe.ConnectedAddress, probe.LocalAddress, probe.HandshakeRttMs);
            if (preference == BoBoRoutePreference.Auto && route.IsTailnet)
            {
                BoBoHostProbeResult direct = null;
                try
                {
                    direct = await BoBoStreamClient.TryResolveDirectLanHostAsync(probe, BoBoIdentityStore.Default, 650, CancellationToken.None);
                }
                catch { }
                if (IsDisposed || request != connectRequestGeneration) return;
                if (direct != null)
                {
                    probe = direct;
                    route = new BoBoConnectionRoute(probe.ConnectedAddress, probe.LocalAddress, probe.HandshakeRttMs);
                }
            }

            if ((preference == BoBoRoutePreference.LanOnly && route.Kind != BoBoRouteKind.Lan) ||
                (preference == BoBoRoutePreference.Tailnet && !route.IsTailnet))
            {
                SetStatus("目标实际使用的地址与所选线路不符，请检查地址或选择自动。", Theme.PillTone.Warning);
                return;
            }
            if (route.IsTailnet)
            {
                SetStatus("正在检测 Tailscale 直连或中继路径…", Theme.PillTone.Info);
                try
                {
                    LinkProbeResult link = await Task.Run(() => Tailscale.Probe(route.RemoteAddress.ToString(), 2400));
                    route.UpdateTailnetPath(link.LastPong);
                }
                catch { }
                if (IsDisposed || request != connectRequestGeneration) return;
            }

            string effectiveHost = route.RemoteAddress != null ? route.RemoteAddress.ToString() : cleanHost;
            clientPairRoute = route;
            clientPairHost = effectiveHost;
            ShowConnectionRouteHint(route);
            if (probe.Authorized)
            {
                await StartStreamAsync(effectiveHost, route);
                return;
            }

            ShowClientPairPrompt(effectiveHost, clientPairHostName, true, route.Label + " · 请输入对方生成的 4 位临时配对码。", route);
        }

        private void ShowClientPairPrompt(string cleanHost, string hostName, bool readable, string detail, BoBoConnectionRoute route)
        {
            string label = string.IsNullOrEmpty(hostName) ? cleanHost : (hostName + " (" + cleanHost + ")");
            clientPairHost = cleanHost;
            clientPairHostName = hostName ?? "";
            clientPairRoute = route;
            clientPairTargetLabel.Text = "连接 " + label + " 需要 BoBo 双端配对授权（仅首次一次）";
            clientPairPinText.Text = "";
            SetClientPairPanelVisible(true);
            SetClientPairBusyUi(false);
            SetClientPairStatus(string.IsNullOrEmpty(detail) ? "请输入被控端 BoBo远程 显示的 4 位临时配对码。" : detail,
                readable ? Theme.Muted : Theme.Warning);
            SetStatus("请在被控端 BoBo远程 点击「生成配对码」，并将 4 位码输入下方。", Theme.PillTone.Info);
            clientPairPinText.Focus();
        }

        private async Task StartClientPairingAsync()
        {
            if (clientPairBusy) return;
            string host = clientPairHost;
            BoBoConnectionRoute route = clientPairRoute;
            if (string.IsNullOrWhiteSpace(host))
            {
                SetClientPairStatus("目标主机地址无效，请重新点击连接。", Theme.Alert);
                return;
            }

            string pin = (clientPairPinText.Text ?? "").Trim();
            if (!IsFourDigitPin(pin))
            {
                SetClientPairStatus("请输入 4 位数字配对码。", Theme.Warning);
                clientPairPinText.Focus();
                return;
            }

            clientPairBusy = true;
            int operation = ++clientPairOperation;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            clientPairCancellation = cancellation;
            SetClientPairBusyUi(true);
            SetClientPairStatus("正在与对方 BoBo远程 进行 TLS 1.2 双向配对校验…", Theme.Muted);
            SetStatus("正在提交 4 位配对码进行双向加密认证…", Theme.PillTone.Info);

            try
            {
                Tuple<bool, string, string> res;
                try
                {
                    res = await BoBoStreamClient.PairWithPinAsync(
                        host, BoBoProtocol.DefaultTcpPort, pin, BoBoIdentityStore.Default, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    res = Tuple.Create(false, "", "配对异常：" + ex.Message);
                }

                if (IsDisposed || operation != clientPairOperation || cancellation.IsCancellationRequested)
                    return;

                if (res.Item1)
                {
                    if (!string.IsNullOrEmpty(res.Item2)) clientPairHostName = res.Item2;
                    SetClientPairPanelVisible(false);
                    SetClientPairStatus("双向配对授权完成。", Theme.Success);
                    SetStatus("双向配对授权完成，正在建立全内置加密远程桌面…", Theme.PillTone.Success);
                    await StartStreamAsync(host, route);
                    return;
                }

                string errText = string.IsNullOrEmpty(res.Item3)
                    ? "配对未能完成，请核对被控端 BoBo远程 的 4 位配对码后重试。"
                    : res.Item3;
                SetClientPairStatus(errText, Theme.Alert);
                SetStatus(errText, Theme.PillTone.Alert);
            }
            finally
            {
                if (operation == clientPairOperation)
                {
                    clientPairBusy = false;
                    if (clientPairCancellation == cancellation)
                    {
                        clientPairCancellation = null;
                        cancellation.Dispose();
                    }
                    if (!IsDisposed && clientPairPanel != null && clientPairPanel.Visible)
                    {
                        SetClientPairBusyUi(false);
                        if (clientPairStartButton != null) clientPairStartButton.Enabled = true;
                    }
                }
            }
        }

        private void CancelClientPairing(string status)
        {
            if (clientPairCancellation != null) clientPairCancellation.Cancel();
            clientPairBusy = false;
            clientPairOperation++;
            SetClientPairPanelVisible(false);
            SetClientPairStatus(string.IsNullOrEmpty(status) ? "已取消本次配对。" : status, Theme.Muted);
            SetStatus(string.IsNullOrEmpty(status) ? "已取消本次配对。" : status, Theme.PillTone.Info);
            if (clientPairStartButton != null) clientPairStartButton.Enabled = false;
            if (clientPairCancelButton != null) clientPairCancelButton.Enabled = false;
        }

        private static bool IsFourDigitPin(string text)
        {
            if (text == null || text.Length != 4) return false;
            for (int i = 0; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }
    }
}
