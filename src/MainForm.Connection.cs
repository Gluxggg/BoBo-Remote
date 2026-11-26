using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
        private int clientPairPort = BoBoProtocol.DefaultTcpPort;
        private string clientPairDisplayInput = "";
        private int connectRequestGeneration;
        private ComboBox connectionRouteChoice;
        private Label connectionRouteHint;
        private BoBoRoutePreference preferredConnectionRoute = BoBoRoutePreference.Auto;
        private BoBoConnectionRoute clientPairRoute;
        private BoBoConnectionRoute activeConnectionRoute;

        // Custom self-hosted fallback relay inline UI
        private TableLayoutPanel assistStreamPanelLayout;
        private Button customRelayToggleBtn;
        private Control customRelayPanel;
        private TableLayoutPanel customRelayPanelLayout;
        private ComboBox customRelayChoice;
        private Button customRelayTestBtn;
        private Button customRelayDeleteBtn;
        private TextBox customRelayNameInput;
        private TextBox customRelayEndpointInput;
        private Button customRelayAddBtn;
        private Label customRelayStatusLabel;
        private int customRelaySpacerRowIndex = -1;
        private int customRelayRowIndex = -1;
        private bool customRelayPanelExpanded;
        private bool customRelayProbeBusy;
        private bool suppressRouteUiEvents;

        private BoBoRoutePreference SelectedConnectionRoute
        {
            get
            {
                if (connectionRouteChoice == null) return preferredConnectionRoute;
                int idx = connectionRouteChoice.SelectedIndex;
                if (idx < (int)BoBoRoutePreference.Auto || idx > (int)BoBoRoutePreference.CustomRelayFallback)
                    return BoBoRoutePreference.Auto;
                return (BoBoRoutePreference)idx;
            }
        }

        private void ShowConnectionRouteHint(BoBoConnectionRoute route)
        {
            if (connectionRouteHint == null) return;
            if (route != null)
            {
                connectionRouteHint.ForeColor = Theme.Muted;
                connectionRouteHint.Text = route.Label;
                return;
            }
            switch (SelectedConnectionRoute)
            {
                case BoBoRoutePreference.LanOnly:
                    connectionRouteHint.ForeColor = Theme.Muted;
                    connectionRouteHint.Text = "仅连接同网段局域网 IP，或从「发现设备」列表选择。";
                    break;
                case BoBoRoutePreference.Tailnet:
                    connectionRouteHint.ForeColor = Theme.Muted;
                    connectionRouteHint.Text = "使用 Tailscale 地址。P2P 可用时直连，必要时走 Tailscale 节点/DERP 中继。";
                    break;
                case BoBoRoutePreference.CustomRelayFallback:
                    BoBoCustomRelay activeRelay = customRelayStore != null ? customRelayStore.ActiveRelay : null;
                    if (activeRelay != null)
                    {
                        connectionRouteHint.ForeColor = Theme.Muted;
                        connectionRouteHint.Text = "直连优先；未通时自动切换至兜底中转「" + activeRelay.DisplayLabel + "」（端到端 TLS 1.2）。";
                    }
                    else
                    {
                        connectionRouteHint.ForeColor = Theme.Warning;
                        connectionRouteHint.Text = "请先在下方添加您的自建 VPS 兜底中转地址与端口。";
                    }
                    break;
                default:
                    connectionRouteHint.ForeColor = Theme.Muted;
                    connectionRouteHint.Text = "自动优先同网段与 P2P 直连；需要自建 VPS 兜底可在线路选择「直连优先 + 自建中转兜底」。";
                    break;
            }
        }

        private sealed class RelaySectionBox : Panel
        {
            internal RelaySectionBox()
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.SurfaceSubtle;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Theme.FillBg(e.Graphics, e.ClipRectangle, Theme.Paper);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = Math.Max(1f, e.Graphics.DpiX / 96f);
                Theme.Surface(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f),
                    8f * scale, Theme.SurfaceSubtle, Theme.Stroke, 1f);
            }
        }

        private Control BuildCustomRelayPanel()
        {
            var box = new RelaySectionBox { Padding = new Padding(14, 10, 14, 10) };
            var grid = Theme.Grid(1);
            grid.Dock = DockStyle.Fill;
            Theme.FillColumn(grid);
            customRelayPanelLayout = grid;

            // Row 0: Saved fallback relay selector + Test + Delete
            var selectRow = Theme.Grid(7);
            Theme.FixedColumn(selectRow, 64);
            Theme.FixedColumn(selectRow, 8);
            Theme.FillColumn(selectRow);
            Theme.FixedColumn(selectRow, 8);
            Theme.FixedColumn(selectRow, 78);
            Theme.FixedColumn(selectRow, 6);
            Theme.FixedColumn(selectRow, 74);

            customRelayChoice = Theme.Choice("自建兜底中转节点", false);
            customRelayChoice.DropDownWidth = 340;
            customRelayChoice.SelectedIndexChanged += delegate
            {
                if (suppressRouteUiEvents || customRelayStore == null) return;
                var selected = customRelayChoice.SelectedItem as BoBoCustomRelay;
                if (selected == null) return;
                customRelayStore.SelectById(selected.Id);
                SaveCustomRelayStoreSafely();
                UpdateCustomRelayButtonsState();
                ShowConnectionRouteHint(activeConnectionRoute);
                UpdateAssistConnectButtons();
            };

            customRelayTestBtn = Theme.Action("测速", Theme.ButtonStyle.Secondary, Theme.Glyph.Activity);
            customRelayTestBtn.Click += async delegate { await ProbeSelectedCustomRelayAsync(); };

            customRelayDeleteBtn = Theme.Action("删除", Theme.ButtonStyle.DangerSoft, Theme.Glyph.Trash);
            customRelayDeleteBtn.Click += delegate { DeleteSelectedCustomRelay(); };

            Theme.Row(selectRow, 36,
                Theme.Text("兜底节点", Theme.TextSize.Small, Theme.Muted), null,
                Theme.WrapInput(customRelayChoice), null,
                customRelayTestBtn, null, customRelayDeleteBtn);
            Theme.Row(grid, 36, selectRow);
            Theme.Row(grid, 8);

            // Row 2: Add custom VPS fallback relay
            var addRow = Theme.Grid(7);
            Theme.FixedColumn(addRow, 64);
            Theme.FixedColumn(addRow, 8);
            Theme.FixedColumn(addRow, 118);
            Theme.FixedColumn(addRow, 8);
            Theme.FillColumn(addRow);
            Theme.FixedColumn(addRow, 8);
            Theme.FixedColumn(addRow, 84);

            customRelayNameInput = Theme.InputBox("中转节点备注", "");
            customRelayNameInput.MaxLength = 28;
            Theme.Cue(customRelayNameInput, "备注，如 华北 VPS");

            customRelayEndpointInput = Theme.InputBox("兜底中转地址与端口", "");
            customRelayEndpointInput.MaxLength = 253;
            Theme.Cue(customRelayEndpointInput, "VPS IP 或域名[:端口]，默认 49810");

            customRelayAddBtn = Theme.Action("添加", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Plus);
            customRelayAddBtn.Click += delegate { AddCustomRelayFromInputs(); };

            customRelayNameInput.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    if (customRelayEndpointInput != null) customRelayEndpointInput.Focus();
                }
            };
            customRelayEndpointInput.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    AddCustomRelayFromInputs();
                }
            };

            Theme.Row(addRow, 36,
                Theme.Text("新增节点", Theme.TextSize.Small, Theme.Muted), null,
                Theme.WrapInput(customRelayNameInput), null,
                Theme.WrapInput(customRelayEndpointInput), null,
                customRelayAddBtn);
            Theme.Row(grid, 36, addRow);
            Theme.Row(grid, 6);

            customRelayStatusLabel = Theme.Text(
                "支持自建 VPS 端口转发 / 域名入口与 Tailscale Peer Relay，直连不通时自动经此节点兜底。",
                Theme.TextSize.Small, Theme.Muted);
            customRelayStatusLabel.AutoEllipsis = true;
            Theme.Row(grid, 22, customRelayStatusLabel);

            box.Controls.Add(grid);
            return box;
        }

        private int MeasureCustomRelayPanelHeight()
        {
            if (customRelayPanel == null || customRelayPanelLayout == null) return 0;
            return SumRows(customRelayPanelLayout) + customRelayPanel.Padding.Vertical + DevicePixels(2);
        }

        private void SetCustomRelayPanelExpanded(bool expanded)
        {
            customRelayPanelExpanded = expanded;
            if (customRelayPanel == null || assistStreamPanelLayout == null) return;
            customRelayPanel.Visible = expanded;
            if (customRelaySpacerRowIndex >= 0 && customRelaySpacerRowIndex < assistStreamPanelLayout.RowStyles.Count)
                assistStreamPanelLayout.RowStyles[customRelaySpacerRowIndex].Height = expanded ? DevicePixels(8) : 0;
            if (customRelayRowIndex >= 0 && customRelayRowIndex < assistStreamPanelLayout.RowStyles.Count)
                assistStreamPanelLayout.RowStyles[customRelayRowIndex].Height = expanded ? MeasureCustomRelayPanelHeight() : 0;
            assistStreamPanelLayout.PerformLayout();
            UpdateCustomRelayToggleButtonText();

            if (clientPairCardLayout != null && assistModeHostRow >= 0 && activeAssistMode == 0)
            {
                int panelH = SumRows(assistStreamModePanel);
                if (panelH <= 0) panelH = DevicePixels(178);
                clientPairCardLayout.RowStyles[assistModeHostRow].Height = panelH;
                if (assistDesk != null && assistStreamCardRow >= 0)
                    assistDesk.RowStyles[assistStreamCardRow].Height = ClientStreamCardHeight();
                LayoutAssistDesk();
            }
        }

        private void ToggleCustomRelayPanel()
        {
            SetCustomRelayPanelExpanded(!customRelayPanelExpanded);
            if (customRelayPanelExpanded && customRelayStore != null && customRelayStore.Relays.Count == 0 && customRelayEndpointInput != null)
                customRelayEndpointInput.Focus();
        }

        private void UpdateCustomRelayToggleButtonText()
        {
            if (customRelayToggleBtn == null) return;
            int count = customRelayStore != null ? customRelayStore.Relays.Count : 0;
            if (customRelayPanelExpanded)
            {
                customRelayToggleBtn.Text = "收起中转";
                Theme.ChangeGlyph(customRelayToggleBtn, Theme.Glyph.ChevronUp);
            }
            else
            {
                customRelayToggleBtn.Text = count > 0 ? ("管理中转 (" + count + ")") : "添加中转";
                Theme.ChangeGlyph(customRelayToggleBtn, count > 0 ? Theme.Glyph.Sliders : Theme.Glyph.Plus);
            }
            customRelayToggleBtn.AccessibleName = customRelayToggleBtn.Text;
        }

        private void SyncCustomRelayUi()
        {
            if (customRelayChoice == null) return;
            suppressRouteUiEvents = true;
            try
            {
                customRelayChoice.BeginUpdate();
                customRelayChoice.Items.Clear();
                if (customRelayStore != null && customRelayStore.Relays.Count > 0)
                {
                    for (int i = 0; i < customRelayStore.Relays.Count; i++)
                        customRelayChoice.Items.Add(customRelayStore.Relays[i]);
                    BoBoCustomRelay active = customRelayStore.ActiveRelay;
                    int activeIdx = 0;
                    if (active != null)
                    {
                        for (int i = 0; i < customRelayStore.Relays.Count; i++)
                        {
                            if (string.Equals(customRelayStore.Relays[i].Id, active.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                activeIdx = i;
                                break;
                            }
                        }
                    }
                    customRelayChoice.SelectedIndex = activeIdx;
                    customRelayChoice.Enabled = true;
                }
                else
                {
                    customRelayChoice.Items.Add("尚未添加自建兜底中转（请在下方新增）");
                    customRelayChoice.SelectedIndex = 0;
                    customRelayChoice.Enabled = false;
                }
            }
            finally
            {
                customRelayChoice.EndUpdate();
                suppressRouteUiEvents = false;
            }
            UpdateCustomRelayButtonsState();
            UpdateCustomRelayToggleButtonText();
        }

        private void UpdateCustomRelayButtonsState()
        {
            bool hasRelay = customRelayStore != null && customRelayStore.ActiveRelay != null;
            if (customRelayTestBtn != null) customRelayTestBtn.Enabled = hasRelay && !customRelayProbeBusy;
            if (customRelayDeleteBtn != null) customRelayDeleteBtn.Enabled = hasRelay && !customRelayProbeBusy;
        }

        private void SetCustomRelayStatus(string text, Color color)
        {
            if (customRelayStatusLabel == null) return;
            customRelayStatusLabel.ForeColor = color;
            customRelayStatusLabel.Text = text;
        }

        private void SaveCustomRelayStoreSafely()
        {
            if (customRelayStore == null) return;
            try
            {
                customRelayStore.Save();
            }
            catch (IOException ex)
            {
                SetCustomRelayStatus("当前目录不可写，本次节点修改仅在窗口内有效：" + ex.Message, Theme.Warning);
            }
            catch (UnauthorizedAccessException ex)
            {
                SetCustomRelayStatus("当前权限受限，本次节点修改仅在窗口内有效：" + ex.Message, Theme.Warning);
            }
        }

        private void HandleConnectionRouteSelectionChanged()
        {
            if (suppressRouteUiEvents) return;
            preferredConnectionRoute = SelectedConnectionRoute;
            SavePreferences();
            if (preferredConnectionRoute == BoBoRoutePreference.CustomRelayFallback && !customRelayPanelExpanded)
            {
                SetCustomRelayPanelExpanded(true);
                if (customRelayStore != null && customRelayStore.Relays.Count == 0 && customRelayEndpointInput != null)
                    customRelayEndpointInput.Focus();
            }
            ShowConnectionRouteHint(activeConnectionRoute);
            UpdateAssistConnectButtons();
        }

        private void AddCustomRelayFromInputs()
        {
            if (customRelayStore == null || customRelayEndpointInput == null) return;
            string name = customRelayNameInput != null ? customRelayNameInput.Text : "";
            string endpoint = customRelayEndpointInput.Text ?? "";

            BoBoCustomRelay saved;
            string error;
            if (!customRelayStore.TryAddOrUpdate(name, endpoint, out saved, out error))
            {
                SetCustomRelayStatus(error, Theme.Alert);
                customRelayEndpointInput.Focus();
                return;
            }

            SaveCustomRelayStoreSafely();
            if (customRelayNameInput != null) customRelayNameInput.Text = "";
            customRelayEndpointInput.Text = "";
            SyncCustomRelayUi();

            if (connectionRouteChoice != null && SelectedConnectionRoute != BoBoRoutePreference.CustomRelayFallback)
            {
                suppressRouteUiEvents = true;
                connectionRouteChoice.SelectedIndex = (int)BoBoRoutePreference.CustomRelayFallback;
                suppressRouteUiEvents = false;
                preferredConnectionRoute = BoBoRoutePreference.CustomRelayFallback;
                SavePreferences();
            }

            ShowConnectionRouteHint(activeConnectionRoute);
            UpdateAssistConnectButtons();
            SetCustomRelayStatus("已保存兜底中转「" + saved.DisplayLabel + "」，直连未通时将自动走此节点。", Theme.Success);
            SetStatus("已添加自建兜底中转：" + saved.DisplayLabel + "。", Theme.PillTone.Success);
        }

        private void DeleteSelectedCustomRelay()
        {
            if (customRelayStore == null) return;
            BoBoCustomRelay target = customRelayStore.ActiveRelay;
            if (target == null) return;
            string removedLabel = target.DisplayLabel;
            if (!customRelayStore.RemoveById(target.Id)) return;

            SaveCustomRelayStoreSafely();
            SyncCustomRelayUi();
            ShowConnectionRouteHint(activeConnectionRoute);
            UpdateAssistConnectButtons();
            SetCustomRelayStatus("已移除兜底中转「" + removedLabel + "」。", Theme.Muted);
            SetStatus("已删除自建兜底中转节点：" + removedLabel + "。", Theme.PillTone.Info);
        }

        private async Task ProbeSelectedCustomRelayAsync()
        {
            if (customRelayProbeBusy || customRelayStore == null) return;
            BoBoCustomRelay relay = customRelayStore.ActiveRelay;
            if (relay == null) return;

            customRelayProbeBusy = true;
            UpdateCustomRelayButtonsState();
            SetCustomRelayStatus("正在探测兜底中转「" + relay.DisplayLabel + "」的 TCP 连通性与往返延迟…", Theme.Accent);
            try
            {
                Tuple<bool, double, string> res = await BoBoRelayStore.ProbeRelayAsync(
                    relay.Host, relay.Port, 3500, CancellationToken.None);
                if (IsDisposed) return;
                SetCustomRelayStatus(res.Item3, res.Item1 ? Theme.Success : Theme.Alert);
                SetStatus(res.Item3, res.Item1 ? Theme.PillTone.Success : Theme.PillTone.Alert);
            }
            finally
            {
                customRelayProbeBusy = false;
                if (!IsDisposed) UpdateCustomRelayButtonsState();
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
            if (pages != null && AssistPage >= 0 && AssistPage < pages.Length)
            {
                var vp = pages[AssistPage] as Theme.SlimScrollViewport;
                if (vp != null) vp.RefreshLayout();
            }
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
            BoBoRoutePreference preference = SelectedConnectionRoute;
            BoBoCustomRelay activeRelay = customRelayStore != null ? customRelayStore.ActiveRelay : null;
            string resolvedInput = ResolveBoBoHostTarget(host);

            bool directToRelayOnly = string.IsNullOrWhiteSpace(resolvedInput)
                && preference == BoBoRoutePreference.CustomRelayFallback
                && activeRelay != null;

            string cleanHost = null;
            if (!directToRelayOnly)
            {
                if (BoBoIdentityStore.IsNineDigitCode(resolvedInput))
                {
                    SetStatus("未在同局域网或已授权记录中找到设备码 " + BoBoIdentityStore.FormatDeviceCode(resolvedInput) + " 对应的 IP；请先点「发现设备」或输入对方 IP 地址。", Theme.PillTone.Warning);
                    streamHost.Focus();
                    return;
                }

                string error;
                cleanHost = Engines.NormalizeHost(resolvedInput, out error);
                if (cleanHost == null)
                {
                    SetStatus(error, Theme.PillTone.Warning);
                    streamHost.Focus();
                    return;
                }

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
            }

            int request = ++connectRequestGeneration;
            string pendingDisplayHost = directToRelayOnly ? activeRelay.Host : cleanHost;
            if (clientPairBusy)
            {
                if (string.Equals(clientPairHost, pendingDisplayHost, StringComparison.OrdinalIgnoreCase))
                {
                    SetClientPairPanelVisible(true);
                    SetClientPairStatus("正在与对方 BoBo远程 完成双向配对，请稍候。", Theme.Muted);
                    return;
                }
                CancelClientPairing("已切换连接目标。");
            }

            clientPairHost = pendingDisplayHost;
            clientPairPort = directToRelayOnly ? activeRelay.Port : BoBoProtocol.DefaultTcpPort;
            clientPairDisplayInput = directToRelayOnly ? activeRelay.EndpointText : cleanHost;
            clientPairHostName = string.IsNullOrEmpty(hostName) && directToRelayOnly ? activeRelay.Name : (hostName ?? "");
            ShowPage(AssistPage);
            SelectAssistMode(0);
            if (!directToRelayOnly) streamHost.Text = cleanHost;
            SetClientPairPanelVisible(false);

            BoBoHostProbeResult probe = null;
            bool usedCustomRelayFallback = directToRelayOnly;

            if (!directToRelayOnly)
            {
                SetStatus("正在验证对方是否已打开 BoBo远程（" + (string.IsNullOrEmpty(hostName) ? cleanHost : hostName) + "）…", Theme.PillTone.Info);
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

                if ((!probe.Reachable || !probe.IsBoBoHost) && preference == BoBoRoutePreference.CustomRelayFallback)
                {
                    if (activeRelay == null)
                    {
                        SetCustomRelayPanelExpanded(true);
                        if (customRelayEndpointInput != null) customRelayEndpointInput.Focus();
                        string needRelayMsg = "直连目标未通，请先在下方「兜底节点」添加自建 VPS 中转地址与端口后再试。";
                        SetCustomRelayStatus(needRelayMsg, Theme.Warning);
                        SetStatus(needRelayMsg, Theme.PillTone.Warning);
                        return;
                    }
                    usedCustomRelayFallback = true;
                    SetStatus("直连未通，正在通过自建兜底中转「" + activeRelay.DisplayLabel + "」尝试连接…", Theme.PillTone.Info);
                    SetCustomRelayStatus("直连未通，已自动切至「" + activeRelay.DisplayLabel + "」进行兜底建链…", Theme.Accent);
                }
            }
            else
            {
                SetStatus("正在通过自建兜底中转「" + activeRelay.DisplayLabel + "」建立 BoBo 加密连接…", Theme.PillTone.Info);
            }

            if (usedCustomRelayFallback)
            {
                try
                {
                    probe = await BoBoStreamClient.ProbeHostAsync(
                        activeRelay.Host, activeRelay.Port, BoBoIdentityStore.Default, 4500, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    probe = new BoBoHostProbeResult { Error = "通过自建中转探测失败：" + ex.Message };
                }
                if (IsDisposed || request != connectRequestGeneration) return;

                if (!probe.Reachable || !probe.IsBoBoHost)
                {
                    string relayErr = string.IsNullOrEmpty(probe.Error)
                        ? ("自建兜底中转「" + activeRelay.DisplayLabel + "」未连通 BoBo 服务端，请检查 VPS 端口转发与被控端状态。")
                        : ("自建兜底中转「" + activeRelay.DisplayLabel + "」未连通：" + probe.Error);
                    SetCustomRelayStatus(relayErr, Theme.Alert);
                    SetStatus(relayErr, Theme.PillTone.Alert);
                    deviceBanner.Set(relayErr, Theme.PillTone.Alert);
                    return;
                }
            }

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

            BoBoConnectionRoute route;
            string effectiveHost;
            if (usedCustomRelayFallback)
            {
                route = BoBoConnectionRoute.CreateCustomRelay(
                    probe.ConnectedAddress, probe.LocalAddress, probe.HandshakeRttMs,
                    activeRelay.Port, activeRelay.Name, activeRelay.EndpointText);
                effectiveHost = activeRelay.Host;
                clientPairPort = activeRelay.Port;
            }
            else
            {
                route = new BoBoConnectionRoute(probe.ConnectedAddress, probe.LocalAddress, probe.HandshakeRttMs);
                if ((preference == BoBoRoutePreference.Auto || preference == BoBoRoutePreference.CustomRelayFallback) && route.IsTailnet)
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
                effectiveHost = route.RemoteAddress != null ? route.RemoteAddress.ToString() : cleanHost;
                clientPairPort = BoBoProtocol.DefaultTcpPort;
            }

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
            clientPairPort = (route != null && route.TargetPort > 0 && route.TargetPort <= 65535)
                ? route.TargetPort
                : BoBoProtocol.DefaultTcpPort;
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
            int port = clientPairPort > 0 && clientPairPort <= 65535 ? clientPairPort : BoBoProtocol.DefaultTcpPort;
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
                        host, port, pin, BoBoIdentityStore.Default, cancellation.Token);
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
