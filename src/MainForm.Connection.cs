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
        private ComboBox customRelayModeChoice;
        private Button customRelayAutoSelectBtn;
        private ComboBox customRelayChoice;
        private Button customRelayTestBtn;
        private Button customRelayDeleteBtn;
        private TextBox customRelayNameInput;
        private TextBox customRelayEndpointInput;
        private TextBox customRelayHealthInput;
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
                    connectionRouteHint.Text = "仅连接局域网同网段 IP。";
                    break;
                case BoBoRoutePreference.Tailnet:
                    connectionRouteHint.ForeColor = Theme.Muted;
                    connectionRouteHint.Text = "优先 Tailscale P2P 直连，必要时经节点中继。";
                    break;
                case BoBoRoutePreference.CustomRelayFallback:
                    BoBoCustomRelay activeRelay = customRelayStore != null ? customRelayStore.ActiveRelay : null;
                    BoBoRelaySelectionMode selMode = customRelayStore != null
                        ? customRelayStore.SelectionMode
                        : BoBoRelaySelectionMode.DynamicLoadBalanced;
                    if (activeRelay != null)
                    {
                        connectionRouteHint.ForeColor = Theme.Muted;
                        if (customRelayStore != null && customRelayStore.Relays.Count > 1 && selMode != BoBoRelaySelectionMode.Manual)
                        {
                            string schedDesc = selMode == BoBoRelaySelectionMode.LowestLatency
                                ? "按最低延迟择优"
                                : "按延迟+负载动态调度";
                            connectionRouteHint.Text = "直连未通时在 " + customRelayStore.Relays.Count + " 个节点中" + schedDesc + "（当前：" + activeRelay.DisplayLabel + "）";
                        }
                        else
                        {
                            connectionRouteHint.Text = "直连未通时切换兜底中转「" + activeRelay.DisplayLabel + "」（TLS 1.2）";
                        }
                    }
                    else
                    {
                        connectionRouteHint.ForeColor = Theme.Warning;
                        connectionRouteHint.Text = "请在下方添加自建 VPS 兜底中转地址与端口。";
                    }
                    break;
                default:
                    connectionRouteHint.ForeColor = Theme.Muted;
                    connectionRouteHint.Text = "自动优先局域网与 P2P 直连；需要自建中转可在上方切换。";
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

            // Row 0: Scheduling strategy + Auto-select best button
            var modeRow = Theme.Grid(5);
            Theme.FixedColumn(modeRow, 64);
            Theme.FixedColumn(modeRow, 8);
            Theme.FillColumn(modeRow);
            Theme.FixedColumn(modeRow, 8);
            Theme.FixedColumn(modeRow, 102);

            customRelayModeChoice = Theme.Choice("中转调度策略", false);
            customRelayModeChoice.Items.AddRange(new object[]
            {
                "手动固定所选节点",
                "延迟优先（自动选最低 RTT）",
                "智能动态调度（延迟 + 服务器负载）"
            });
            customRelayModeChoice.DropDownWidth = 290;
            customRelayModeChoice.SelectedIndexChanged += delegate
            {
                if (suppressRouteUiEvents || customRelayStore == null) return;
                int idx = Math.Max(0, Math.Min(2, customRelayModeChoice.SelectedIndex));
                customRelayStore.SelectionMode = (BoBoRelaySelectionMode)idx;
                SaveCustomRelayStoreSafely();
                UpdateCustomRelayButtonsState();
                ShowConnectionRouteHint(activeConnectionRoute);
                if (customRelayStore.SelectionMode == BoBoRelaySelectionMode.Manual)
                    SetCustomRelayStatus("已设为手动固定节点（故障时仍可顺序兜底）", Theme.Muted);
                else if (customRelayStore.SelectionMode == BoBoRelaySelectionMode.LowestLatency)
                    SetCustomRelayStatus("已设为延迟优先：连控时并发测速并选择最低 RTT 节点", Theme.Accent);
                else
                    SetCustomRelayStatus("已设为智能动态调度：综合延迟与负载优选，过载自动切换", Theme.Accent);
            };

            customRelayAutoSelectBtn = Theme.Action("智能优选", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Bolt);
            customRelayAutoSelectBtn.Click += async delegate { await ProbeAndSelectBestCustomRelayAsync(); };

            Theme.Row(modeRow, 36,
                Theme.Text("调度策略", Theme.TextSize.Small, Theme.Muted), null,
                Theme.WrapInput(customRelayModeChoice), null,
                customRelayAutoSelectBtn);
            Theme.Row(grid, 36, modeRow);
            Theme.Row(grid, 8);

            // Row 1: Saved fallback relay selector + Test + Delete
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
            Theme.Row(grid, 8);

            // Row 3: Optional health/load endpoint for bobo-relay
            var healthRow = Theme.Grid(3);
            Theme.FixedColumn(healthRow, 64);
            Theme.FixedColumn(healthRow, 8);
            Theme.FillColumn(healthRow);

            customRelayHealthInput = Theme.InputBox("负载监控接口", "");
            customRelayHealthInput.MaxLength = 300;
            Theme.Cue(customRelayHealthInput, "负载接口（可选）：如 :49811，留空自动探测端口+1");
            customRelayHealthInput.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    AddCustomRelayFromInputs();
                }
            };
            Theme.Row(healthRow, 34,
                Theme.Text("负载接口", Theme.TextSize.Small, Theme.Muted), null,
                Theme.WrapInput(customRelayHealthInput));
            Theme.Row(grid, 34, healthRow);
            Theme.Row(grid, 6);

            customRelayStatusLabel = Theme.Text(
                "支持多节点并发测速、实时负载优选与过载切换",
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
            if (customRelayPanel == null || assistAdvancedPanel == null) return;
            customRelayPanel.Visible = expanded;
            if (customRelaySpacerRowIndex >= 0 && customRelaySpacerRowIndex < assistAdvancedPanel.RowStyles.Count)
                assistAdvancedPanel.RowStyles[customRelaySpacerRowIndex].Height = expanded ? DevicePixels(8) : 0;
            if (customRelayRowIndex >= 0 && customRelayRowIndex < assistAdvancedPanel.RowStyles.Count)
                assistAdvancedPanel.RowStyles[customRelayRowIndex].Height = expanded ? MeasureCustomRelayPanelHeight() : 0;
            assistAdvancedPanel.PerformLayout();
            UpdateCustomRelayToggleButtonText();

            if (assistStreamPanelLayout != null && assistAdvancedRowIndex >= 0 && assistAdvancedRowIndex < assistStreamPanelLayout.RowStyles.Count)
            {
                assistStreamPanelLayout.RowStyles[assistAdvancedRowIndex].Height = assistAdvancedExpanded ? SumRows(assistAdvancedPanel) : 0;
                assistStreamPanelLayout.PerformLayout();
            }

            if (clientPairCardLayout != null && assistModeHostRow >= 0 && activeAssistMode == 0)
            {
                int panelH = SumRows(assistStreamModePanel);
                if (panelH <= 0) panelH = DevicePixels(120);
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
                if (customRelayModeChoice != null && customRelayStore != null)
                {
                    int mIdx = Math.Max(0, Math.Min(customRelayModeChoice.Items.Count - 1, (int)customRelayStore.SelectionMode));
                    if (customRelayModeChoice.SelectedIndex != mIdx)
                        customRelayModeChoice.SelectedIndex = mIdx;
                }

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
            bool hasAny = customRelayStore != null && customRelayStore.Relays.Count > 0;
            bool hasRelay = customRelayStore != null && customRelayStore.ActiveRelay != null;
            if (customRelayAutoSelectBtn != null) customRelayAutoSelectBtn.Enabled = hasAny && !customRelayProbeBusy;
            if (customRelayModeChoice != null) customRelayModeChoice.Enabled = !customRelayProbeBusy;
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
            string healthUrl = customRelayHealthInput != null ? customRelayHealthInput.Text : "";

            BoBoCustomRelay saved;
            string error;
            if (!customRelayStore.TryAddOrUpdate(name, endpoint, healthUrl, 100, out saved, out error))
            {
                SetCustomRelayStatus(error, Theme.Alert);
                customRelayEndpointInput.Focus();
                return;
            }

            SaveCustomRelayStoreSafely();
            if (customRelayNameInput != null) customRelayNameInput.Text = "";
            customRelayEndpointInput.Text = "";
            if (customRelayHealthInput != null) customRelayHealthInput.Text = "";
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
            SetCustomRelayStatus("已保存兜底中转「" + saved.DisplayLabel + "」，可点上方「智能优选」一键测速排序。", Theme.Success);
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
            SetCustomRelayStatus("正在探测「" + relay.DisplayLabel + "」的 TCP 延迟与服务器负载…", Theme.Accent);
            try
            {
                BoBoRelayProbeSnapshot snap = await BoBoRelayScheduler.ProbeRelayWithTelemetryAsync(
                    relay, 3500, 900, customRelayStore.SelectionMode, CancellationToken.None);
                if (IsDisposed) return;
                SyncCustomRelayUi();
                Theme.PillTone tone = !snap.Reachable ? Theme.PillTone.Alert
                    : (!snap.Accepting || (snap.HasServerLoad && snap.ServerLoad >= 0.85)) ? Theme.PillTone.Warning
                    : Theme.PillTone.Success;
                Color statusColor = !snap.Reachable ? Theme.Alert
                    : (!snap.Accepting || (snap.HasServerLoad && snap.ServerLoad >= 0.85)) ? Theme.Warning
                    : Theme.Success;
                SetCustomRelayStatus(snap.Summary, statusColor);
                SetStatus(snap.Summary, tone);
                ShowConnectionRouteHint(activeConnectionRoute);
            }
            finally
            {
                customRelayProbeBusy = false;
                if (!IsDisposed) UpdateCustomRelayButtonsState();
            }
        }

        private async Task ProbeAndSelectBestCustomRelayAsync()
        {
            if (customRelayProbeBusy || customRelayStore == null || customRelayStore.Relays.Count == 0) return;
            customRelayProbeBusy = true;
            UpdateCustomRelayButtonsState();
            int total = customRelayStore.Relays.Count;
            SetCustomRelayStatus("正在并发探测全部 " + total + " 个自建中转节点的延迟与实时负载…", Theme.Accent);
            SetStatus("正在并发评估 " + total + " 个自建中转节点的 RTT 与负载…", Theme.PillTone.Info);
            try
            {
                System.Collections.Generic.List<BoBoRelayProbeSnapshot> ranked =
                    await BoBoRelayScheduler.ProbeAndRankAllAsync(customRelayStore, 3000, CancellationToken.None);
                if (IsDisposed) return;

                BoBoRelayProbeSnapshot best = null;
                for (int i = 0; i < ranked.Count; i++)
                {
                    if (ranked[i].Reachable)
                    {
                        best = ranked[i];
                        break;
                    }
                }

                if (best != null && best.Relay != null)
                {
                    customRelayStore.SelectById(best.Relay.Id);
                    SaveCustomRelayStoreSafely();
                    SyncCustomRelayUi();
                    ShowConnectionRouteHint(activeConnectionRoute);
                    UpdateAssistConnectButtons();

                    string modeTag = customRelayStore.SelectionMode == BoBoRelaySelectionMode.LowestLatency
                        ? "延迟优选"
                        : customRelayStore.SelectionMode == BoBoRelaySelectionMode.Manual ? "手动" : "动态优选";
                    string msg = "已从 " + ranked.Count + " 节点" + modeTag + "：" + best.Summary;
                    Theme.PillTone tone = !best.Accepting ? Theme.PillTone.Warning : Theme.PillTone.Success;
                    SetCustomRelayStatus(msg, !best.Accepting ? Theme.Warning : Theme.Success);
                    SetStatus(msg, tone);
                }
                else
                {
                    SyncCustomRelayUi();
                    string failMsg = "全部 " + ranked.Count + " 个自建中转节点均暂不可达，请检查 VPS 防火墙或 bobo-relay 运行状态。";
                    SetCustomRelayStatus(failMsg, Theme.Alert);
                    SetStatus(failMsg, Theme.PillTone.Alert);
                }
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
            if (route == null) return;

            if (route.IsTailnet)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 10000 };
                bool refreshing = false;
                timer.Tick += async delegate
                {
                    if (refreshing || IsDisposed || session.IsDisposed || activeNativeStreamForm != session) return;
                    refreshing = true;
                    try
                    {
                        LinkProbeResult result = await Task.Run(delegate
                        {
                            string target = route.RemoteAddress.ToString();
                            return BoBoEmbeddedTailnet.Default.ShouldRouteHost(target)
                                ? BoBoEmbeddedTailnet.Default.PingPeerSync(target, 2200)
                                : Tailscale.Probe(target, 2200);
                        });
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
                return;
            }

            if (route.Kind == BoBoRouteKind.CustomRelay
                && customRelayStore != null
                && customRelayStore.Relays.Count >= 2
                && customRelayStore.SelectionMode != BoBoRelaySelectionMode.Manual)
            {
                var relayTimer = new System.Windows.Forms.Timer { Interval = 15000 };
                bool checking = false;
                int degradeStrikes = 0;
                long lastMigrationUtcTicks = DateTime.UtcNow.Ticks;
                relayTimer.Tick += async delegate
                {
                    if (checking || IsDisposed || session.IsDisposed || activeNativeStreamForm != session) return;
                    if (customRelayStore == null || customRelayStore.Relays.Count < 2 || customRelayStore.SelectionMode == BoBoRelaySelectionMode.Manual)
                        return;

                    checking = true;
                    try
                    {
                        BoBoCustomRelay active = null;
                        for (int i = 0; i < customRelayStore.Relays.Count; i++)
                        {
                            if (string.Equals(customRelayStore.Relays[i].Id, route.CustomRelayId, StringComparison.OrdinalIgnoreCase))
                            {
                                active = customRelayStore.Relays[i];
                                break;
                            }
                        }
                        if (active == null) active = customRelayStore.ActiveRelay;
                        if (active == null) return;

                        BoBoRelayProbeSnapshot curSnap = await BoBoRelayScheduler.ProbeRelayWithTelemetryAsync(
                            active, 2500, 800, customRelayStore.SelectionMode, CancellationToken.None);
                        if (IsDisposed || session.IsDisposed || activeNativeStreamForm != session) return;

                        route.UpdateCustomRelayMetrics(curSnap.RttMs, curSnap.HasServerLoad, curSnap.ServerLoad, curSnap.Accepting);
                        ShowConnectionRouteHint(route);

                        bool hardTrigger = !curSnap.Reachable || !curSnap.Accepting;
                        bool softTrigger = (curSnap.HasServerLoad && curSnap.ServerLoad >= 0.85) || curSnap.RttMs >= 130.0;
                        if (hardTrigger)
                            degradeStrikes = 2;
                        else if (softTrigger)
                            degradeStrikes++;
                        else
                            degradeStrikes = 0;

                        if (degradeStrikes < 2) return;
                        if ((DateTime.UtcNow.Ticks - lastMigrationUtcTicks) < TimeSpan.FromSeconds(45).Ticks) return;

                        System.Collections.Generic.List<BoBoRelayProbeSnapshot> ranked =
                            await BoBoRelayScheduler.ProbeAndRankAllAsync(customRelayStore, 2500, CancellationToken.None);
                        if (IsDisposed || session.IsDisposed || activeNativeStreamForm != session) return;

                        BoBoRelayProbeSnapshot best = null;
                        for (int i = 0; i < ranked.Count; i++)
                        {
                            BoBoRelayProbeSnapshot cand = ranked[i];
                            if (cand.Relay == null || string.Equals(cand.Relay.Id, active.Id, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (BoBoRelayScheduler.ShouldMigrateActiveRelay(curSnap, cand, customRelayStore.SelectionMode))
                            {
                                best = cand;
                                break;
                            }
                        }
                        if (best == null || best.Relay == null) return;

                        BoBoHostProbeResult hp = null;
                        try
                        {
                            hp = await BoBoStreamClient.ProbeHostAsync(
                                best.Relay.Host, best.Relay.Port, BoBoIdentityStore.Default, 3000, CancellationToken.None);
                        }
                        catch { }
                        if (IsDisposed || session.IsDisposed || activeNativeStreamForm != session) return;

                        bool hpOk = hp != null && hp.Reachable && hp.IsBoBoHost && hp.AllowIncoming;
                        customRelayStore.RecordConnectOutcome(best.Relay.Id, hpOk);
                        if (!hpOk) return;

                        lastMigrationUtcTicks = DateTime.UtcNow.Ticks;
                        degradeStrikes = 0;
                        customRelayStore.SelectById(best.Relay.Id);
                        SaveCustomRelayStoreSafely();
                        SyncCustomRelayUi();

                        route.SwitchCustomRelay(
                            hp.ConnectedAddress ?? route.RemoteAddress,
                            best.RttMs,
                            best.Relay,
                            customRelayStore.SelectionMode,
                            best.HasServerLoad,
                            best.ServerLoad,
                            best.Accepting);
                        clientPairPort = best.Relay.Port;
                        clientPairHost = best.Relay.Host;
                        ShowConnectionRouteHint(route);

                        string reason = "原中转「" + active.DisplayLabel + "」"
                            + (!curSnap.Reachable ? "连接中断" : !curSnap.Accepting ? "进入维护" : "负载或延迟偏高")
                            + "，已动态切换至 " + best.Summary;
                        SetCustomRelayStatus(reason, Theme.Accent);
                        SetStatus(reason, Theme.PillTone.Info);
                        await session.SwitchEndpointAsync(best.Relay.Host, best.Relay.Port, reason);
                    }
                    catch { }
                    finally { checking = false; }
                };
                session.Disposed += delegate { relayTimer.Dispose(); };
                relayTimer.Start();
            }
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
            BoBoRelayProbeSnapshot selectedRelaySnap = null;
            BoBoCustomRelay selectedRelay = null;

            if (!directToRelayOnly)
            {
                string targetLabel = string.IsNullOrEmpty(hostName) ? cleanHost : hostName;
                int initialProbeTimeoutMs = Tailscale.IsTailnetAddress(cleanHost) ? 8000 : 4500;
                SetStatus("正在验证对方是否已打开 BoBo远程（" + targetLabel + "）…", Theme.PillTone.Info);
                try
                {
                    probe = await BoBoStreamClient.ProbeHostAsync(
                        cleanHost, BoBoProtocol.DefaultTcpPort, BoBoIdentityStore.Default, initialProbeTimeoutMs, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    probe = new BoBoHostProbeResult { Error = "探测目标 BoBo远程 失败：" + ex.Message };
                }
                if (IsDisposed || request != connectRequestGeneration) return;

                if ((!probe.Reachable || !probe.IsBoBoHost) && probe.TransientFailure)
                {
                    SetStatus("首次连接正在唤醒跨网加密通道，正在自动重试（" + targetLabel + "）…", Theme.PillTone.Info);
                    if (Tailscale.IsTailnetAddress(cleanHost))
                    {
                        await Task.Run(delegate
                        {
                            if (BoBoEmbeddedTailnet.Default.ShouldRouteHost(cleanHost))
                                BoBoEmbeddedTailnet.Default.PingPeerSync(cleanHost, 1800);
                        });
                        if (IsDisposed || request != connectRequestGeneration) return;
                    }
                    try
                    {
                        probe = await BoBoStreamClient.ProbeHostAsync(
                            cleanHost, BoBoProtocol.DefaultTcpPort, BoBoIdentityStore.Default, initialProbeTimeoutMs, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        probe = new BoBoHostProbeResult { Error = "探测目标 BoBo远程 失败：" + ex.Message };
                    }
                    if (IsDisposed || request != connectRequestGeneration) return;
                }

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
                    SetStatus("直连未通，正在评估自建兜底中转节点延迟与负载…", Theme.PillTone.Info);
                }
            }
            else
            {
                SetStatus("正在通过自建兜底中转建立 BoBo 加密连接…", Theme.PillTone.Info);
            }

            if (usedCustomRelayFallback)
            {
                var candidates = new System.Collections.Generic.List<BoBoRelayProbeSnapshot>();
                BoBoRelaySelectionMode selMode = customRelayStore != null
                    ? customRelayStore.SelectionMode
                    : BoBoRelaySelectionMode.DynamicLoadBalanced;

                if (customRelayStore != null && (selMode != BoBoRelaySelectionMode.Manual || customRelayStore.Relays.Count > 1))
                {
                    SetCustomRelayStatus("正在并发探测 " + customRelayStore.Relays.Count + " 个自建中转节点的延迟与负载…", Theme.Accent);
                    System.Collections.Generic.List<BoBoRelayProbeSnapshot> ranked =
                        await BoBoRelayScheduler.ProbeAndRankAllAsync(customRelayStore, 2500, CancellationToken.None);
                    if (IsDisposed || request != connectRequestGeneration) return;
                    SyncCustomRelayUi();
                    for (int i = 0; i < ranked.Count; i++)
                    {
                        if (ranked[i].Reachable) candidates.Add(ranked[i]);
                    }
                    if (candidates.Count == 0 && ranked.Count > 0)
                        candidates.Add(ranked[0]);
                }

                if (candidates.Count == 0 && activeRelay != null)
                {
                    candidates.Add(new BoBoRelayProbeSnapshot
                    {
                        Relay = activeRelay,
                        Reachable = true,
                        Accepting = true
                    });
                }

                BoBoHostProbeResult chosenProbe = null;
                string lastProbeErr = "";
                for (int i = 0; i < candidates.Count; i++)
                {
                    BoBoRelayProbeSnapshot cand = candidates[i];
                    if (cand.Relay == null) continue;
                    SetStatus("正在经中转节点「" + cand.Relay.DisplayLabel + "」探测远端 BoBo 宿主…", Theme.PillTone.Info);
                    BoBoHostProbeResult candProbe;
                    try
                    {
                        candProbe = await BoBoStreamClient.ProbeHostAsync(
                            cand.Relay.Host, cand.Relay.Port, BoBoIdentityStore.Default, 4500, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        candProbe = new BoBoHostProbeResult { Error = "通过自建中转探测失败：" + ex.Message };
                    }
                    if (IsDisposed || request != connectRequestGeneration) return;

                    bool candOk = candProbe.Reachable && candProbe.IsBoBoHost;
                    if (customRelayStore != null) customRelayStore.RecordConnectOutcome(cand.Relay.Id, candOk);
                    if (candOk)
                    {
                        selectedRelaySnap = cand;
                        selectedRelay = cand.Relay;
                        chosenProbe = candProbe;
                        break;
                    }

                    lastProbeErr = candProbe.Error;
                    if (i + 1 < candidates.Count)
                        SetCustomRelayStatus("中转「" + cand.Relay.DisplayLabel + "」未就绪，自动切换下一备选节点…", Theme.Warning);
                }

                if (chosenProbe == null || selectedRelay == null)
                {
                    BoBoCustomRelay labelRelay = (candidates.Count > 0 && candidates[0].Relay != null) ? candidates[0].Relay : activeRelay;
                    string relayLabel = labelRelay != null ? labelRelay.DisplayLabel : "自建中转";
                    string relayErr = string.IsNullOrEmpty(lastProbeErr)
                        ? ("自建兜底中转「" + relayLabel + "」等候选节点均未连通 BoBo 服务端，请检查 VPS 端口转发与被控端状态。")
                        : ("自建兜底中转「" + relayLabel + "」未连通：" + lastProbeErr);
                    SetCustomRelayStatus(relayErr, Theme.Alert);
                    SetStatus(relayErr, Theme.PillTone.Alert);
                    deviceBanner.Set(relayErr, Theme.PillTone.Alert);
                    return;
                }

                probe = chosenProbe;
                if (customRelayStore != null)
                {
                    if (selMode != BoBoRelaySelectionMode.Manual)
                        customRelayStore.SelectById(selectedRelay.Id);
                    SaveCustomRelayStoreSafely();
                    SyncCustomRelayUi();
                }
                if (!string.IsNullOrEmpty(selectedRelaySnap.Summary))
                    SetCustomRelayStatus("已选用中转：" + selectedRelaySnap.Summary, Theme.Success);
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
            if (usedCustomRelayFallback && selectedRelay != null)
            {
                BoBoRelaySelectionMode selMode = customRelayStore != null
                    ? customRelayStore.SelectionMode
                    : BoBoRelaySelectionMode.Manual;
                double effectiveRtt = (selectedRelaySnap != null && selectedRelaySnap.RttMs > 0.1)
                    ? selectedRelaySnap.RttMs
                    : probe.HandshakeRttMs;
                route = BoBoConnectionRoute.CreateScheduledCustomRelay(
                    probe.ConnectedAddress,
                    probe.LocalAddress,
                    effectiveRtt,
                    selectedRelay,
                    selMode,
                    selectedRelaySnap != null && selectedRelaySnap.HasServerLoad,
                    selectedRelaySnap != null ? selectedRelaySnap.ServerLoad : 0.0,
                    selectedRelaySnap == null || selectedRelaySnap.Accepting);
                if (selectedRelaySnap != null)
                {
                    route.UpdateCustomRelayPorts(selectedRelaySnap.UdpPort, selectedRelaySnap.StunPort);
                }
                effectiveHost = selectedRelay.Host;
                clientPairPort = selectedRelay.Port;
                clientPairDisplayInput = selectedRelay.EndpointText;
            }
            else
            {
                route = new BoBoConnectionRoute(probe.ConnectedAddress, probe.LocalAddress, probe.HandshakeRttMs);
                if ((preference == BoBoRoutePreference.Auto || preference == BoBoRoutePreference.CustomRelayFallback) && route.IsTailnet)
                {
                    BoBoHostProbeResult direct = null;
                    try
                    {
                        direct = await BoBoStreamClient.TryResolveDirectLanHostAsync(probe, BoBoIdentityStore.Default, 850, CancellationToken.None);
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
                        string tsTarget = route.RemoteAddress.ToString();
                        LinkProbeResult link = await Task.Run(delegate
                        {
                            return BoBoEmbeddedTailnet.Default.ShouldRouteHost(tsTarget)
                                ? BoBoEmbeddedTailnet.Default.PingPeerSync(tsTarget, 2400)
                                : Tailscale.Probe(tsTarget, 2400);
                        });
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
