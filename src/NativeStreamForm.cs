using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    /// <summary>
    /// BoBo Remote V5 原生一体化远程控制窗口（完全内置在 BoBo 进程体系内，零外置 Moonlight 窗口）：
    /// - 内部嵌入 BoBoStreamPlayer.exe（moonlight-common-c + Direct3D 11 + Media Foundation D3D11VA + WASAPI）；
    /// - 支持无边框全屏 ↔ 窗口模式即时切换、自由鼠标 ↔ FPS 锁鼠即时切换、实时性能浮层（FPS/解码/渲染/RTT）、
    ///   远端组合键与剪贴板文本直传、不跳出窗体的原地画质切换；
    /// - 关闭或退出时同时通知内核断开并向远端 Sunshine 发送 HTTPS /cancel，确保双端彻底收尾。
    /// </summary>
    internal sealed class NativeStreamForm : Form
    {
        private const int SwpNoZOrder = 0x0004;
        private const int SwpNoActivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private readonly string host;
        private readonly string hostDisplayName;
        private readonly bool hostAudio;
        private readonly bool preferHevc;
        private int currentWidth;
        private int currentHeight;
        private int currentFps;
        private int currentBitrateKbps;
        private string currentModeTag;
        private bool windowedMode;
        private bool freeMouseMode;
        private bool hudVisible;

        private readonly Panel videoHost;
        private readonly Panel statusOverlay;
        private readonly Label statusTitleLabel;
        private readonly Label statusDetailLabel;
        private readonly Button statusCancelButton;
        private readonly NativeHudOverlay hudOverlay;

        private readonly object procGate = new object();
        private Process playerProcess;
        private StreamWriter playerStdin;
        private IntPtr childViewportHwnd = IntPtr.Zero;
        private int activeHttpsPort = GameStreamClient.DefaultHttpsPort;
        private int sessionGeneration;
        private CancellationTokenSource launchCts;
        private bool closingIntentionally;
        private bool switchingQuality;
        private bool remoteSessionConnected;
        private bool hasSavedWindowedBounds;
        private Rectangle savedWindowedBounds;
        private string lastErrorMessage = "";

        internal event Action<int> PlayerProcessStarted;
        internal event Action<bool> MouseModeChanged;
        internal event Action ToolbarHotkeyPressed;
        internal event Action<string> StatusUpdated;
        internal event Action<string> PairRequired;
        internal event Action SessionEnded;

        internal string Host { get { return host; } }
        internal string HostDisplayName { get { return hostDisplayName; } }
        internal bool IsWindowedMode { get { return windowedMode; } }
        internal bool IsFreeMouseMode { get { return freeMouseMode; } }
        internal bool IsHudVisible { get { return hudVisible; } }
        internal int PlayerPid
        {
            get
            {
                lock (procGate)
                {
                    try { return (playerProcess != null && !playerProcess.HasExited) ? playerProcess.Id : 0; }
                    catch { return 0; }
                }
            }
        }

        internal NativeStreamForm(
            string host,
            string hostDisplayName,
            int width,
            int height,
            int fps,
            int bitrateKbps,
            string modeTag,
            bool windowed,
            bool freeMouse,
            bool showHud,
            bool hostAudio,
            bool preferHevc,
            Icon appIcon)
        {
            this.host = host ?? "";
            this.hostDisplayName = string.IsNullOrEmpty(hostDisplayName) ? this.host : hostDisplayName;
            this.currentWidth = width > 0 ? width : 1920;
            this.currentHeight = height > 0 ? height : 1080;
            this.currentFps = fps > 0 ? fps : 60;
            this.currentBitrateKbps = bitrateKbps > 0 ? bitrateKbps : 15000;
            this.currentModeTag = string.IsNullOrEmpty(modeTag) ? (this.currentWidth + "x" + this.currentHeight + "@" + this.currentFps) : modeTag;
            this.windowedMode = windowed;
            this.freeMouseMode = freeMouse;
            this.hudVisible = showHud;
            this.hostAudio = hostAudio;
            this.preferHevc = preferHevc;

            Text = BuildWindowTitle();
            if (appIcon != null) Icon = appIcon;
            BackColor = Color.FromArgb(14, 21, 32);
            KeyPreview = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;

            videoHost = new Panel();
            videoHost.Dock = DockStyle.Fill;
            videoHost.BackColor = Color.FromArgb(14, 21, 32);
            videoHost.SizeChanged += delegate { SyncChildViewportSize(); };
            Controls.Add(videoHost);

            statusOverlay = new Panel();
            statusOverlay.Size = new Size(440, 176);
            statusOverlay.BackColor = Color.FromArgb(22, 32, 48);
            statusOverlay.Padding = new Padding(24, 20, 24, 18);
            statusOverlay.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(72, 130, 195), 1.5f))
                {
                    e.Graphics.DrawRectangle(pen, 1, 1, statusOverlay.Width - 3, statusOverlay.Height - 3);
                }
            };

            statusTitleLabel = new Label();
            statusTitleLabel.Text = "正在连接 " + this.hostDisplayName;
            statusTitleLabel.Font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold);
            statusTitleLabel.ForeColor = Color.White;
            statusTitleLabel.AutoSize = false;
            statusTitleLabel.SetBounds(24, 20, 392, 30);
            statusOverlay.Controls.Add(statusTitleLabel);

            statusDetailLabel = new Label();
            statusDetailLabel.Text = "正在通过内置加密通道握手（零外置窗口）…";
            statusDetailLabel.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            statusDetailLabel.ForeColor = Color.FromArgb(186, 208, 232);
            statusDetailLabel.AutoSize = false;
            statusDetailLabel.SetBounds(24, 58, 392, 48);
            statusOverlay.Controls.Add(statusDetailLabel);

            statusCancelButton = new Button();
            statusCancelButton.Text = "取消并退出";
            statusCancelButton.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            statusCancelButton.ForeColor = Color.White;
            statusCancelButton.BackColor = Color.FromArgb(196, 62, 62);
            statusCancelButton.FlatStyle = FlatStyle.Flat;
            statusCancelButton.FlatAppearance.BorderSize = 0;
            statusCancelButton.Cursor = Cursors.Hand;
            statusCancelButton.SetBounds(152, 120, 136, 36);
            statusCancelButton.Click += delegate { DisconnectAndClose(true); };
            statusOverlay.Controls.Add(statusCancelButton);

            Controls.Add(statusOverlay);
            statusOverlay.BringToFront();

            hudOverlay = new NativeHudOverlay();

            ApplyWindowStyle(windowedMode, true);
            CenterStatusOverlay();

            Shown += async delegate
            {
                CenterStatusOverlay();
                UpdateHudPosition();
                await StartOrRestartStreamInternalAsync();
            };

            Resize += delegate
            {
                CenterStatusOverlay();
                SyncChildViewportSize();
                UpdateHudPosition();
            };

            Move += delegate { UpdateHudPosition(); };

            Activated += delegate
            {
                FocusChildViewport();
            };

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.Alt && e.Shift && e.KeyCode == Keys.Q)
                {
                    e.Handled = true;
                    DisconnectAndClose(true);
                }
                else if (e.Control && e.Alt && e.Shift && e.KeyCode == Keys.B)
                {
                    e.Handled = true;
                    SetFreeMouse(true);
                    RaiseToolbarHotkey();
                }
            };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!closingIntentionally)
                {
                    closingIntentionally = true;
                    CleanupSessionResources(true);
                }
                try
                {
                    if (hudOverlay != null && !hudOverlay.IsDisposed) hudOverlay.Close();
                }
                catch { }
            };

            FormClosed += delegate
            {
                Engines.ReleaseCursorClip();
                if (SessionEnded != null)
                {
                    try { SessionEnded(); } catch { }
                }
            };
        }

        internal void SetWindowedMode(bool windowed)
        {
            if (IsDisposed) return;
            if (windowedMode == windowed) return;
            windowedMode = windowed;
            ApplyWindowStyle(windowedMode, false);
            CenterStatusOverlay();
            SyncChildViewportSize();
            UpdateHudPosition();
            FocusChildViewport();
        }

        internal void SetFreeMouse(bool free)
        {
            freeMouseMode = free;
            if (free) Engines.ReleaseCursorClip();
            SendIpcLine(NativeStreamCore.BuildMouseModeCommand(free));
            if (MouseModeChanged != null)
            {
                try { MouseModeChanged(free); } catch { }
            }
        }

        internal void SetHudVisible(bool visible)
        {
            hudVisible = visible;
            SendIpcLine(NativeStreamCore.BuildHudCommand(visible));
            if (hudOverlay != null && !hudOverlay.IsDisposed)
            {
                if (visible && remoteSessionConnected)
                {
                    UpdateHudPosition();
                    if (!hudOverlay.Visible) hudOverlay.Show(this);
                }
                else if (hudOverlay.Visible)
                {
                    hudOverlay.Hide();
                }
            }
        }

        internal void SendRemoteKeys(int[] vks)
        {
            if (vks == null || vks.Length == 0) return;
            SendIpcLine(NativeStreamCore.BuildSendKeysCommand(vks));
        }

        internal void SendRemoteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            SendIpcLine(NativeStreamCore.BuildSendTextCommand(text));
        }

        internal async Task SwitchQualityAsync(int width, int height, int fps, int bitrateKbps, string modeTag)
        {
            if (IsDisposed || closingIntentionally) return;
            currentWidth = width > 0 ? width : currentWidth;
            currentHeight = height > 0 ? height : currentHeight;
            currentFps = fps > 0 ? fps : currentFps;
            currentBitrateKbps = bitrateKbps > 0 ? bitrateKbps : currentBitrateKbps;
            currentModeTag = string.IsNullOrEmpty(modeTag)
                ? (currentWidth + "x" + currentHeight + "@" + currentFps)
                : modeTag;
            Text = BuildWindowTitle();

            switchingQuality = true;
            try
            {
                await StartOrRestartStreamInternalAsync();
            }
            finally
            {
                switchingQuality = false;
            }
        }

        internal void DisconnectAndClose(bool cancelRemoteHost)
        {
            if (closingIntentionally)
            {
                if (!IsDisposed)
                {
                    try { Close(); } catch { }
                }
                return;
            }
            closingIntentionally = true;
            CleanupSessionResources(cancelRemoteHost);
            if (!IsDisposed)
            {
                try
                {
                    if (InvokeRequired) BeginInvoke(new Action(delegate { if (!IsDisposed) Close(); }));
                    else Close();
                }
                catch { }
            }
        }

        private async Task StartOrRestartStreamInternalAsync()
        {
            int gen = Interlocked.Increment(ref sessionGeneration);
            remoteSessionConnected = false;
            lastErrorMessage = "";

            StopCurrentPlayerAndCancelCts(true);

            if (IsDisposed || closingIntentionally) return;
            ShowStatusOverlay("正在连接 " + hostDisplayName, "正在协商加密桌面串流（" + currentModeTag + "）…");

            string playerExe, playerErr;
            if (!NativeStreamCore.EnsureRuntimePlayerExe(out playerExe, out playerErr))
            {
                ShowFatalError(playerErr);
                return;
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            launchCts = cts;
            GameStreamLaunchResult launch;
            try
            {
                launch = await Task.Run(delegate
                {
                    return GameStreamClient.LaunchDesktopSession(
                        host, currentWidth, currentHeight, currentFps, hostAudio, cts.Token);
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (gen == sessionGeneration && !closingIntentionally && !IsDisposed)
                    ShowFatalError("发起内置串流失败：" + ex.Message);
                return;
            }

            if (gen != sessionGeneration || closingIntentionally || IsDisposed) return;

            if (!launch.Success)
            {
                if (string.Equals(launch.Error, "NOT_PAIRED", StringComparison.Ordinal))
                {
                    NotifyStatus("目标主机尚未授权内置客户端，请完成一次 4 位配对码授权。");
                    if (PairRequired != null)
                    {
                        try { PairRequired(host); } catch { }
                    }
                    DisconnectAndClose(false);
                    return;
                }
                ShowFatalError(string.IsNullOrEmpty(launch.Error) ? "启动远程桌面失败。" : launch.Error);
                return;
            }

            activeHttpsPort = launch.HttpsPort > 0 ? launch.HttpsPort : GameStreamClient.DefaultHttpsPort;
            ShowStatusOverlay("正在启动内置 D3D11VA 解码器", "已接通 " + launch.AppTitle + "（" + currentModeTag + "），正在等待画面…");

            IntPtr parentHandle = videoHost.Handle;
            string args = NativeStreamCore.BuildStreamArgs(
                parentHandle,
                launch,
                currentWidth,
                currentHeight,
                currentFps,
                currentBitrateKbps,
                preferHevc,
                freeMouseMode,
                hudVisible,
                true);

            Process proc = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(playerExe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.WorkingDirectory = Path.GetDirectoryName(playerExe);

                proc = Process.Start(psi);
                if (proc == null)
                {
                    ShowFatalError("启动内置播放核 BoBoStreamPlayer.exe 失败。");
                    return;
                }

                StreamWriter utf8In = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false));
                utf8In.AutoFlush = true;

                lock (procGate)
                {
                    playerProcess = proc;
                    playerStdin = utf8In;
                    childViewportHwnd = IntPtr.Zero;
                }

                if (PlayerProcessStarted != null)
                {
                    try { PlayerProcessStarted(proc.Id); } catch { }
                }

                StartPlayerMonitorThreads(proc, gen);
            }
            catch (Exception ex)
            {
                if (proc != null)
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                }
                ShowFatalError("启动内置播放核失败：" + ex.Message);
            }
        }

        private void StartPlayerMonitorThreads(Process proc, int gen)
        {
            Thread stdoutThread = new Thread(delegate()
            {
                try
                {
                    string line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        NativePlayerEvent ev = NativeStreamCore.TryParseEventLine(line);
                        if (ev != null)
                        {
                            HandlePlayerEventOnWorker(ev, gen);
                        }
                    }
                }
                catch { }
            });
            stdoutThread.IsBackground = true;
            stdoutThread.Start();

            Thread waitThread = new Thread(delegate()
            {
                int exitCode = 0;
                try
                {
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                }
                catch { }

                if (gen != sessionGeneration || closingIntentionally || switchingQuality || IsDisposed)
                    return;

                SafeUiInvoke(delegate
                {
                    if (gen != sessionGeneration || closingIntentionally || switchingQuality || IsDisposed)
                        return;
                    if (remoteSessionConnected && exitCode == 0)
                    {
                        NotifyStatus("远程串流会话已结束。");
                        DisconnectAndClose(true);
                    }
                    else
                    {
                        string reason = !string.IsNullOrEmpty(lastErrorMessage)
                            ? lastErrorMessage
                            : ("内置播放核已退出（退出码 " + exitCode + "）。");
                        ShowFatalError(reason);
                    }
                });
            });
            waitThread.IsBackground = true;
            waitThread.Start();
        }

        private void HandlePlayerEventOnWorker(NativePlayerEvent ev, int gen)
        {
            if (gen != sessionGeneration || IsDisposed) return;

            if (ev.EventType == "viewport_hwnd" && ev.ViewportHwnd != 0)
            {
                IntPtr h = new IntPtr(unchecked((long)ev.ViewportHwnd));
                lock (procGate) { childViewportHwnd = h; }
                SafeUiInvoke(delegate
                {
                    SyncChildViewportSize();
                    FocusChildViewport();
                });
                return;
            }

            if (ev.EventType == "stage")
            {
                if (ev.State == "starting")
                {
                    string zh = NativeStreamCore.TranslateStageLabel(ev.Name);
                    SafeUiInvoke(delegate
                    {
                        if (!remoteSessionConnected && statusOverlay.Visible)
                        {
                            statusDetailLabel.Text = zh;
                        }
                    });
                }
                else if (ev.State == "failed")
                {
                    lastErrorMessage = "串流握手失败（阶段：" + ev.Name + "，错误码 " + ev.Code + "）。";
                }
                return;
            }

            if (ev.EventType == "connected")
            {
                remoteSessionConnected = true;
                SafeUiInvoke(delegate
                {
                    statusOverlay.Visible = false;
                    SyncChildViewportSize();
                    FocusChildViewport();
                    if (hudVisible && hudOverlay != null && !hudOverlay.IsDisposed)
                    {
                        UpdateHudPosition();
                        if (!hudOverlay.Visible) hudOverlay.Show(this);
                    }
                    NotifyStatus("已通过内置 D3D11VA 内核接通 " + hostDisplayName + "（" + currentModeTag + "）。");
                });
                return;
            }

            if (ev.EventType == "stats" && ev.Stats != null)
            {
                NativeStreamStats stats = ev.Stats;
                SafeUiInvoke(delegate
                {
                    if (hudVisible && hudOverlay != null && !hudOverlay.IsDisposed)
                    {
                        hudOverlay.UpdateStats(stats, currentModeTag);
                        if (!hudOverlay.Visible && remoteSessionConnected)
                        {
                            UpdateHudPosition();
                            hudOverlay.Show(this);
                        }
                    }
                });
                return;
            }

            if (ev.EventType == "mouse_mode")
            {
                freeMouseMode = ev.FreeMouse;
                SafeUiInvoke(delegate
                {
                    if (MouseModeChanged != null)
                    {
                        try { MouseModeChanged(ev.FreeMouse); } catch { }
                    }
                });
                return;
            }

            if (ev.EventType == "hotkey" && ev.Name == "toolbar")
            {
                SafeUiInvoke(delegate { RaiseToolbarHotkey(); });
                return;
            }

            if (ev.EventType == "error")
            {
                if (!string.IsNullOrEmpty(ev.Message))
                    lastErrorMessage = ev.Message;
                return;
            }

            if (ev.EventType == "terminated")
            {
                if (!string.IsNullOrEmpty(ev.Reason))
                    lastErrorMessage = ev.Reason;
            }
        }

        private void SendIpcLine(string jsonLine)
        {
            if (string.IsNullOrEmpty(jsonLine)) return;
            lock (procGate)
            {
                if (playerStdin == null) return;
                try
                {
                    playerStdin.WriteLine(jsonLine);
                }
                catch { }
            }
        }

        private void CleanupSessionResources(bool cancelRemoteHost)
        {
            Engines.ReleaseCursorClip();
            StopCurrentPlayerAndCancelCts(cancelRemoteHost);
        }

        private void StopCurrentPlayerAndCancelCts(bool cancelRemoteHost)
        {
            CancellationTokenSource oldCts = launchCts;
            launchCts = null;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                try { oldCts.Dispose(); } catch { }
            }

            Process procToStop;
            StreamWriter stdinToClose;
            lock (procGate)
            {
                procToStop = playerProcess;
                stdinToClose = playerStdin;
                playerProcess = null;
                playerStdin = null;
                childViewportHwnd = IntPtr.Zero;
            }

            string hostToCancel = host;
            int httpsPortToCancel = activeHttpsPort;

            if (procToStop != null || cancelRemoteHost)
            {
                Task.Run(delegate
                {
                    if (stdinToClose != null)
                    {
                        try { stdinToClose.WriteLine(NativeStreamCore.BuildDisconnectCommand()); } catch { }
                        try { stdinToClose.Flush(); } catch { }
                        try { stdinToClose.Dispose(); } catch { }
                    }
                    if (procToStop != null)
                    {
                        try
                        {
                            if (!procToStop.HasExited && !procToStop.WaitForExit(1500))
                            {
                                procToStop.Kill();
                                procToStop.WaitForExit(1000);
                            }
                        }
                        catch { }
                        try { procToStop.Dispose(); } catch { }
                    }
                    if (cancelRemoteHost && !string.IsNullOrEmpty(hostToCancel))
                    {
                        GameStreamClient.CancelSession(hostToCancel, httpsPortToCancel);
                    }
                });
            }
        }

        private void ApplyWindowStyle(bool windowed, bool initial)
        {
            Screen screen = Screen.FromControl(this);
            if (screen == null) screen = Screen.PrimaryScreen;
            Rectangle wa = screen.WorkingArea;

            if (windowed)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                TopMost = false;
                if (hasSavedWindowedBounds && savedWindowedBounds.Width >= 640 && savedWindowedBounds.Height >= 400)
                {
                    Bounds = savedWindowedBounds;
                }
                else
                {
                    int w = Math.Max(960, (int)(wa.Width * 0.86));
                    int h = Math.Max(580, (int)(wa.Height * 0.86));
                    Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
                }
            }
            else
            {
                if (!initial && FormBorderStyle == FormBorderStyle.Sizable && WindowState == FormWindowState.Normal)
                {
                    savedWindowedBounds = Bounds;
                    hasSavedWindowedBounds = true;
                }
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                TopMost = false;
                Bounds = screen.Bounds;
            }
        }

        private void SyncChildViewportSize()
        {
            if (IsDisposed || videoHost == null) return;
            int w = Math.Max(16, videoHost.ClientSize.Width);
            int h = Math.Max(16, videoHost.ClientSize.Height);

            IntPtr child;
            lock (procGate) { child = childViewportHwnd; }
            if (child != IntPtr.Zero && IsWindow(child))
            {
                MoveWindow(child, 0, 0, w, h, true);
            }
            SendIpcLine(NativeStreamCore.BuildResizeCommand(w, h));
        }

        private void FocusChildViewport()
        {
            IntPtr child;
            lock (procGate) { child = childViewportHwnd; }
            if (child != IntPtr.Zero && IsWindow(child))
            {
                try { SetFocus(child); } catch { }
            }
        }

        private void ShowStatusOverlay(string title, string detail)
        {
            SafeUiInvoke(delegate
            {
                if (IsDisposed) return;
                statusTitleLabel.Text = title ?? "";
                statusDetailLabel.Text = detail ?? "";
                statusCancelButton.Text = "取消并退出";
                statusOverlay.Visible = true;
                CenterStatusOverlay();
                statusOverlay.BringToFront();
            });
        }

        private void ShowFatalError(string message)
        {
            SafeUiInvoke(delegate
            {
                if (IsDisposed) return;
                statusTitleLabel.Text = "连接未能完成";
                statusDetailLabel.Text = message ?? "未知错误";
                statusCancelButton.Text = "关闭窗口";
                statusOverlay.Visible = true;
                CenterStatusOverlay();
                statusOverlay.BringToFront();
                NotifyStatus(message ?? "内置串流连接失败。");
            });
        }

        private void CenterStatusOverlay()
        {
            if (statusOverlay == null || IsDisposed) return;
            int x = Math.Max(0, (ClientSize.Width - statusOverlay.Width) / 2);
            int y = Math.Max(0, (ClientSize.Height - statusOverlay.Height) / 2);
            statusOverlay.Location = new Point(x, y);
        }

        private void UpdateHudPosition()
        {
            if (hudOverlay == null || hudOverlay.IsDisposed || IsDisposed) return;
            Point pt = videoHost.PointToScreen(new Point(14, 14));
            hudOverlay.Location = pt;
        }

        private string BuildWindowTitle()
        {
            return "BoBo Remote — " + hostDisplayName + "（" + currentModeTag + " · 内置原生引擎）";
        }

        private void RaiseToolbarHotkey()
        {
            if (ToolbarHotkeyPressed != null)
            {
                try { ToolbarHotkeyPressed(); } catch { }
            }
        }

        private void NotifyStatus(string text)
        {
            if (StatusUpdated != null && !string.IsNullOrEmpty(text))
            {
                try { StatusUpdated(text); } catch { }
            }
        }

        private void SafeUiInvoke(Action action)
        {
            if (action == null || IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }

        /// <summary>轻量化、不拦截鼠标点击与焦点的性能监控浮层（覆盖于 D3D11 视口左上角）。</summary>
        private sealed class NativeHudOverlay : Form
        {
            private const int WsExTransparent = 0x00000020;
            private const int WsExToolWindow = 0x00000080;
            private const int WsExNoActivate = 0x08000000;
            private const int WsExTopMost = 0x00000008;

            private string line1 = "BoBo V5 · D3D11VA 硬解";
            private string line2 = "等待帧率统计…";

            internal NativeHudOverlay()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(14, 21, 32);
                Opacity = 0.84;
                Size = new Size(310, 50);
                DoubleBuffered = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopMost;
                    return cp;
                }
            }

            internal void UpdateStats(NativeStreamStats s, string modeTag)
            {
                if (s == null || IsDisposed) return;
                double mbps = s.BitrateKbps / 1000.0;
                line1 = string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} FPS  |  {1}x{2}  |  {3} ({4})",
                    s.Fps, s.Width, s.Height,
                    string.IsNullOrEmpty(s.Codec) ? "H264" : s.Codec,
                    string.IsNullOrEmpty(s.Decoder) ? "D3D11VA" : "D3D11VA");
                line2 = string.Format(CultureInfo.InvariantCulture,
                    "解码 {0:F1}ms · 渲染 {1:F1}ms · RTT {2:F0}ms · {3:F1} Mbps",
                    s.DecodeMs, s.RenderMs, s.RttMs, mbps);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(Color.FromArgb(20, 30, 44)))
                    g.FillRectangle(bg, ClientRectangle);
                using (var pen = new Pen(Color.FromArgb(62, 136, 210), 1f))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                using (var f1 = new Font("Consolas", 9f, FontStyle.Bold))
                using (var f2 = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular))
                using (var b1 = new SolidBrush(Color.FromArgb(132, 224, 255)))
                using (var b2 = new SolidBrush(Color.FromArgb(224, 234, 246)))
                {
                    g.DrawString(line1, f1, b1, 10, 7);
                    g.DrawString(line2, f2, b2, 10, 27);
                }
            }
        }
    }
}
