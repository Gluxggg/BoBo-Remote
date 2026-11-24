using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private const int ClientPairTimeoutSeconds = 120;

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
        private Process clientPairProcess;
        private string clientPairHost = "";
        private string clientPairHostName = "";
        private int connectRequestGeneration;

        private Control BuildClientPairPanel()
        {
            var panel = Theme.Grid(1);
            Theme.FillColumn(panel);
            panel.Padding = new Padding(22, 4, 22, 10);
            clientPairPanelLayout = panel;
            Theme.Row(panel, 1, Theme.Rule());
            Theme.Row(panel, 14);

            clientPairTargetLabel = Theme.Text("这台电脑首次连接，需要完成一次授权。", Theme.TextSize.LabelBold, Theme.Ink);
            Theme.Row(panel, 22, clientPairTargetLabel);
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
            Theme.FixedColumn(pinRow, 160);
            Theme.FixedColumn(pinRow, 12);
            Theme.FillColumn(pinRow);
            Theme.Row(pinRow, 40, Theme.WrapInput(clientPairPinText), null,
                Theme.Text("请输入被控端窗口显示的 4 位数字配对码。", Theme.TextSize.Small, Theme.Muted));
            Theme.Row(panel, 40, pinRow);
            Theme.Row(panel, 10);

            var actions = Theme.Grid(4);
            Theme.FixedColumn(actions, 138);
            Theme.FixedColumn(actions, 10);
            Theme.FixedColumn(actions, 86);
            Theme.FillColumn(actions);
            clientPairStartButton = Theme.Action("配对并连接", Theme.ButtonStyle.Primary, Theme.Glyph.Pin);
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
            Theme.Row(actions, 36, clientPairStartButton, null, clientPairCancelButton);
            Theme.Row(panel, 36, actions);
            Theme.Row(panel, 8);

            clientPairStatusLabel = Theme.Text("等待输入配对码。", Theme.TextSize.Small, Theme.Muted);
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
                clientPairStartButton.Text = busy ? "配对中…" : "配对并连接";
                clientPairStartButton.AccessibleName = clientPairStartButton.Text;
            }
            if (clientPairCancelButton != null)
                clientPairCancelButton.Enabled = busy || (clientPairPanel != null && clientPairPanel.Visible);
        }

        private async Task ConnectToDeviceAsync(string host, string hostName)
        {
            string error;
            string cleanHost = Engines.NormalizeHost(host, out error);
            if (cleanHost == null)
            {
                SetStatus(error, Theme.PillTone.Warning);
                streamHost.Focus();
                return;
            }

            int request = ++connectRequestGeneration;
            if (clientPairBusy)
            {
                if (string.Equals(clientPairHost, cleanHost, StringComparison.OrdinalIgnoreCase))
                {
                    SetClientPairPanelVisible(true);
                    SetClientPairStatus("正在等待被控端确认，请勿重复点击。", Theme.Muted);
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
            SetStatus("正在检查 " + (string.IsNullOrEmpty(hostName) ? cleanHost : hostName) + " 的授权状态…", Theme.PillTone.Info);

            bool paired = false;
            string detail = null;
            bool readable;
            try
            {
                readable = await Task.Run(() => FoundationClient.IsHostPaired(cleanHost, clientPairHostName, out paired, out detail));
            }
            catch (Exception ex)
            {
                readable = false;
                detail = "读取本机配对记录失败：" + ex.Message;
            }
            if (IsDisposed || request != connectRequestGeneration) return;

            if (readable && paired)
            {
                SetStatus("已完成授权，正在打开桌面…", Theme.PillTone.Success);
                StartStream(cleanHost);
                return;
            }

            if (clientPairTargetLabel != null)
            {
                string label = string.IsNullOrEmpty(hostName) ? cleanHost : hostName;
                clientPairTargetLabel.Text = "首次连接 " + label + "，请输入被控端显示的 4 位配对码。";
            }
            SetClientPairStatus(readable
                ? "尚未授权：输入 4 位码后点击「配对并连接」。"
                : (detail ?? "无法读取本机授权记录，请检查配置目录权限；仍可尝试首次配对。"),
                readable ? Theme.Muted : Theme.Warning);
            SetClientPairPanelVisible(true);
            SetStatus(readable
                ? "首次连接需要授权：请在下方输入被控端显示的 4 位配对码。"
                : (detail ?? "无法读取本机授权记录，请检查配置目录权限。"),
                readable ? Theme.PillTone.Info : Theme.PillTone.Warning);
            if (clientPairPinText != null && readable)
            {
                clientPairPinText.Text = "";
                clientPairPinText.Focus();
            }
        }

        private async Task StartClientPairingAsync()
        {
            if (clientPairBusy || IsDisposed) return;
            string pin = clientPairPinText == null ? "" : (clientPairPinText.Text ?? "").Trim();
            if (!IsFourDigitPin(pin))
            {
                SetClientPairStatus("请输入完整的 4 位数字配对码。", Theme.Warning);
                if (clientPairPinText != null) clientPairPinText.Focus();
                return;
            }

            string host = clientPairHost;
            if (string.IsNullOrEmpty(host))
            {
                string error;
                host = Engines.NormalizeHost(SelectedHost(), out error);
                if (host == null)
                {
                    SetClientPairStatus(error, Theme.Warning);
                    return;
                }
                clientPairHost = host;
            }
            if (FoundationClient.SelectEngine(true) == StreamEngine.Unavailable)
            {
                SetClientPairStatus(MissingStreamEngineMessage, Theme.Alert);
                return;
            }
            string targetName = clientPairHostName;

            clientPairBusy = true;
            int operation = ++clientPairOperation;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            clientPairCancellation = cancellation;
            SetClientPairBusyUi(true);
            SetClientPairStatus("正在向被控端发起配对…", Theme.Muted);
            SetStatus("正在发起首次授权，请留意被控端的配对确认。", Theme.PillTone.Info);

            try
            {
                Process started = null;
                string startMessage = null;
                bool startedOk;
                try
                {
                    startedOk = await Task.Run(delegate
                    {
                        return FoundationClient.StartPairWithPin(host, pin, out started, out startMessage);
                    });
                }
                catch (Exception ex)
                {
                    startedOk = false;
                    startMessage = "启动配对失败：" + ex.Message;
                }

                if (IsDisposed || operation != clientPairOperation || cancellation.IsCancellationRequested)
                {
                    await StopClientPairProcessAsync(started);
                    return;
                }
                if (!startedOk)
                {
                    if (started != null) await StopClientPairProcessAsync(started);
                    SetClientPairStatus(string.IsNullOrEmpty(startMessage)
                        ? "无法启动配对，请检查内置串流组件。" : startMessage, Theme.Alert);
                    return;
                }

                clientPairProcess = started;
                Process pairingProcess = started;
                SetClientPairStatus("已发起配对，等待被控端确认 4 位码…", Theme.Muted);
                Stopwatch clock = Stopwatch.StartNew();
                while (!cancellation.IsCancellationRequested
                    && clock.Elapsed < TimeSpan.FromSeconds(ClientPairTimeoutSeconds))
                {
                    try
                    {
                        await Task.Delay(600, cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (IsDisposed || operation != clientPairOperation || cancellation.IsCancellationRequested) return;

                    bool paired = false;
                    string detail = null;
                    bool readable;
                    try
                    {
                        readable = await Task.Run(() =>
                            FoundationClient.IsHostPaired(host, targetName, out paired, out detail));
                    }
                    catch (Exception ex)
                    {
                        readable = false;
                        detail = "读取授权记录失败：" + ex.Message;
                    }
                    if (IsDisposed || operation != clientPairOperation || cancellation.IsCancellationRequested) return;

                    if (readable && paired)
                    {
                        Process finishing = TakeClientPairProcess();
                        await StopClientPairProcessAsync(finishing);
                        if (IsDisposed || operation != clientPairOperation || cancellation.IsCancellationRequested) return;
                        SetClientPairPanelVisible(false);
                        SetClientPairStatus("授权完成。", Theme.Success);
                        SetStatus("授权完成，正在打开桌面…", Theme.PillTone.Success);
                        StartStream(host);
                        return;
                    }

                    int exitCode;
                    if (TryGetProcessExitCode(pairingProcess, out exitCode))
                    {
                        TakeClientPairProcess();
                        await StopClientPairProcessAsync(pairingProcess);
                        if (IsDisposed || operation != clientPairOperation || cancellation.IsCancellationRequested) return;
                        string exitText = exitCode == 0
                            ? "配对程序已退出，但本机还没有记录本次授权。请重新发起，并确认被控端仍在等待配对。"
                            : "配对程序提前退出（退出码 " + exitCode + "），本次没有开始串流。请检查被控端窗口与组件状态。";
                        SetClientPairStatus(exitText, Theme.Alert);
                        SetStatus(exitText, Theme.PillTone.Alert);
                        return;
                    }

                    int seconds = Math.Max(0, ClientPairTimeoutSeconds - (int)Math.Ceiling(clock.Elapsed.TotalSeconds));
                    SetClientPairStatus(readable
                        ? "等待被控端确认 4 位码… 还剩 " + seconds + " 秒"
                        : (detail ?? "暂时无法读取授权记录，正在重试…"),
                        readable ? Theme.Muted : Theme.Warning);
                }

                if (cancellation.IsCancellationRequested || IsDisposed || operation != clientPairOperation) return;
                if (clock.Elapsed >= TimeSpan.FromSeconds(ClientPairTimeoutSeconds))
                {
                    Process expired = TakeClientPairProcess();
                    await StopClientPairProcessAsync(expired);
                    if (IsDisposed || operation != clientPairOperation) return;
                    SetClientPairBusyUi(false);
                    SetClientPairStatus("配对等待已超过 120 秒，本次没有开始串流。可重试或取消。", Theme.Warning);
                    SetStatus("配对等待已超时，本次没有开始串流。", Theme.PillTone.Warning);
                }
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
            Process process = TakeClientPairProcess();
            if (process != null)
            {
                StopClientPairProcessAsync(process);
            }
            SetClientPairPanelVisible(false);
            SetClientPairStatus(string.IsNullOrEmpty(status) ? "已取消本次配对。" : status, Theme.Muted);
            SetStatus(string.IsNullOrEmpty(status) ? "已取消本次配对。" : status, Theme.PillTone.Info);
            if (clientPairStartButton != null) clientPairStartButton.Enabled = false;
            if (clientPairCancelButton != null) clientPairCancelButton.Enabled = false;
        }

        private Process TakeClientPairProcess()
        {
            Process process = clientPairProcess;
            clientPairProcess = null;
            return process;
        }

        private static Task StopClientPairProcessAsync(Process process)
        {
            if (process == null) return Task.FromResult(0);
            return Task.Run(delegate
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch { }
                try { process.WaitForExit(6000); } catch { }
                try { process.Dispose(); } catch { }
            });
        }

        private static bool TryGetProcessExitCode(Process process, out int exitCode)
        {
            exitCode = 0;
            if (process == null) return false;
            try
            {
                if (!process.HasExited) return false;
                exitCode = process.ExitCode;
                return true;
            }
            catch
            {
                return false;
            }
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
