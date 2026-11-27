using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    /// BoBo Remote V5.1 原生一体化远程控制窗口（对齐 UU远程 交互体验）：
    /// - 窗口模式配备浅色 UU 风格顶栏（静音按钮 +「控制中心」级联菜单按钮 + 醒目红色「退出远控」）；
    /// - 全屏模式自动交由可移动四边贴边隐藏方形悬浮球（StreamToolbarOverlay）承载同一套「控制中心」；
    /// - 锁鼠游戏模式下随时轻按 Ctrl+Alt 即可瞬间释放鼠标并唤出浮窗，支持 Ctrl+Alt+F/U/G/M/→ 等全套快捷键；
    /// - 宽敞 DPI 自适应连接中提醒卡片（含 BoBo 徽标、丝滑进度条、快捷键提示区与取消按钮）+ 顶部状态轻提示条；
    /// - 原生支持 1:1 实际大小、置顶小窗与透明度调节、实时 WASAPI 音量滑块、系统快捷键透传、仅查看模式、
    ///   截屏保存、连接瞬间立即开启被控端防窥以及断开自动锁屏。
    /// </summary>
    internal sealed class NativeStreamForm : Form
    {
        private string host;
        private readonly string hostDisplayName;
        private int targetPort = BoBoProtocol.DefaultTcpPort;
        private bool hostAudio;
        private bool preferHevc;
        private bool isCustomRelayRoute;
        private int relayUdpPort;
        private string customStunHost = "";
        private int customStunPort;
        private bool preferCompressedAudio = true;
        private int currentWidth;
        private int currentHeight;
        private int currentFps;
        private int currentBitrateKbps;
        private int activeMonitorIndex;
        private List<BoBoMonitorDescriptor> remoteMonitors = new List<BoBoMonitorDescriptor>();
        private string currentModeTag;
        private bool windowedMode;
        private bool freeMouseMode;
        private bool hudVisible;

        // Extended UU Control Center live session state
        private int hostNativeWidth;
        private int hostNativeHeight;
        private int displayScalePercent = 100;
        private ControlResolutionMode resolutionMode = ControlResolutionMode.FollowHost;
        private bool autoBitrate = true;
        private bool trueColor;
        private bool hdrEnabled;
        private int scaleMode = 1;
        private bool superDisplayMode;
        private bool lastSuperDisplayRequestEnabled;
        private int displayRequestId;
        private Size lastRequestedViewport;
        private bool lastRequestedFullscreen;
        private Size actualStreamSize;
        private BoBoDisplayState displayState;
        private readonly System.Windows.Forms.Timer superDisplayResizeTimer;
        private bool showDisplayResult;
        private bool miniWindowMode;
        private int miniWindowOpacity = 100;
        private int volumePercent = 100;
        private int lastNonZeroVolume = 100;
        private bool audioMuted;
        private bool lockHostOnDisconnect;
        private bool restoreHostAudioOnDisconnect = true;
        private bool remotePrivacyActive;
        private bool autoRemotePrivacyOnConnect;
        private bool sendSystemShortcuts;
        private ControlMouseMode mouseMode = ControlMouseMode.Free;
        private bool micEnabled;
        private bool hideFloatBallInFullscreen;
        private bool showFloatBallInWindowed;
        private bool hasSavedPreMiniBounds;
        private Rectangle savedPreMiniBounds;
        private double lastRttMs;

        // UI components
        private readonly Panel nativeTopBar;
        private readonly Panel mobileNavBar;
        private readonly PictureBox topBarLogo;
        private readonly Label topBarTitleLabel;
        private readonly Button topBarMousePillBtn;
        private readonly Button topBarMuteBtn;
        private readonly Button topBarControlCenterBtn;
        private readonly Button topBarExitBtn;
        private readonly Panel videoHost;
        private readonly ConnectingOverlayCard statusOverlay;
        private readonly NativeHudOverlay hudOverlay;
        private readonly StreamHintToast hintToast;
        private UuControlCenterSession topBarMenuSession;

        private readonly object procGate = new object();
        private BoBoStreamClient streamClient;
        private BoBoCoreInterop.BoBoClientNativeHandle nativeClient;
        private System.Windows.Forms.Timer statsTimer;
        private int sessionGeneration;
        private CancellationTokenSource launchCts;
        private bool closingIntentionally;
        private bool switchingQuality;
        private volatile bool remoteSessionConnected;
        private bool hasSavedWindowedBounds;
        private Rectangle savedWindowedBounds;
        private string lastErrorMessage = "";

        internal event Action<int> PlayerProcessStarted;
        internal event Action<bool> MouseModeChanged;
        internal event Action ToolbarHotkeyPressed;
        internal event Action BoundsMoved;
        internal event Action RestoreMainRequested;
        internal event Action ToggleWindowedRequested;
        internal event Action ToggleHudRequested;
        internal event Action ToggleLocalPrivacyRequested;
        internal event Action<bool> HostAudioPreferenceChanged;
        internal event Action<bool> AutoRemotePrivacyPreferenceChanged;
        internal event Action<bool> LockHostOnDisconnectPreferenceChanged;
        internal event Action<bool> HideFloatBallPreferenceChanged;
        internal event Action<bool> ShowFloatBallWindowedPreferenceChanged;
        internal event Action<bool> SendSysKeysPreferenceChanged;
        internal event Action<int> MiniOpacityPreferenceChanged;
        internal event Action<string> StatusUpdated;
        internal event Action<string> PairRequired;
        internal event Action SessionEnded;

        internal Func<bool> LocalPrivacyActiveProvider;
        internal Func<string> ConnectionRouteLabelProvider { get; set; }

        internal string Host { get { return host; } }
        internal int TargetPort { get { return targetPort; } }
        internal string HostDisplayName { get { return hostDisplayName; } }
        internal bool IsWindowedMode { get { return windowedMode; } }
        internal bool IsFreeMouseMode { get { return freeMouseMode; } }
        internal bool IsHudVisible { get { return hudVisible; } }
        internal bool ShouldShowFloatingSquare
        {
            get
            {
                if (miniWindowMode) return false;
                if (!windowedMode) return !hideFloatBallInFullscreen;
                return showFloatBallInWindowed;
            }
        }
        internal int PlayerPid
        {
            get
            {
                return remoteSessionConnected ? Process.GetCurrentProcess().Id : 0;
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
            Icon appIcon,
            bool autoRemotePrivacy = false,
            bool lockHostOnDisconnect = false,
            bool hideFloatBallInFullscreen = false,
            bool showFloatBallInWindowed = false,
            bool sendSystemShortcuts = false,
            int miniOpacity = 100,
            bool superDisplay = true,
            int port = BoBoProtocol.DefaultTcpPort,
            bool preferCompressedAudio = true)
        {
            this.host = host ?? "";
            this.targetPort = port > 0 && port <= 65535 ? port : BoBoProtocol.DefaultTcpPort;
            this.hostDisplayName = string.IsNullOrEmpty(hostDisplayName) ? this.host : hostDisplayName;
            this.currentWidth = width > 0 ? width : 1920;
            this.currentHeight = height > 0 ? height : 1080;
            this.hostNativeWidth = this.currentWidth;
            this.hostNativeHeight = this.currentHeight;
            this.currentFps = fps > 0 ? fps : 60;
            this.currentBitrateKbps = bitrateKbps > 0 ? bitrateKbps : 14000;
            this.currentModeTag = string.IsNullOrEmpty(modeTag) ? (this.currentWidth + "x" + this.currentHeight + "@" + this.currentFps) : modeTag;
            this.windowedMode = windowed;
            this.freeMouseMode = freeMouse;
            this.mouseMode = freeMouse ? ControlMouseMode.Free : ControlMouseMode.Locked;
            this.hudVisible = showHud;
            this.hostAudio = hostAudio;
            this.preferHevc = preferHevc;
            this.preferCompressedAudio = preferCompressedAudio;
            this.autoRemotePrivacyOnConnect = autoRemotePrivacy;
            this.lockHostOnDisconnect = lockHostOnDisconnect;
            this.hideFloatBallInFullscreen = hideFloatBallInFullscreen;
            this.showFloatBallInWindowed = showFloatBallInWindowed;
            this.sendSystemShortcuts = sendSystemShortcuts;
            this.miniWindowOpacity = Math.Max(50, Math.Min(100, miniOpacity));
            superDisplayMode = superDisplay;
            superDisplayResizeTimer = new System.Windows.Forms.Timer { Interval = 350 };
            superDisplayResizeTimer.Tick += delegate
            {
                superDisplayResizeTimer.Stop();
                SendViewportIfChanged();
            };

            Text = BuildWindowTitle();
            if (appIcon != null) Icon = appIcon;
            BackColor = Theme.VideoBackdrop;
            KeyPreview = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;

            float dpi = Theme.GetDpiScale(this);
            int barH = Math.Max(36, (int)Math.Round(40 * dpi));

            videoHost = new Panel();
            videoHost.Dock = DockStyle.Fill;
            videoHost.BackColor = Theme.VideoBackdrop;
            videoHost.SizeChanged += delegate { SyncChildViewportSize(); };
            Controls.Add(videoHost);

            // 窗口模式的远控顶栏
            nativeTopBar = new Panel();
            nativeTopBar.Dock = DockStyle.Top;
            nativeTopBar.Height = barH;
            nativeTopBar.BackColor = Theme.ChromeBg;
            nativeTopBar.Padding = new Padding((int)Math.Round(12 * dpi), (int)Math.Round(5 * dpi), (int)Math.Round(10 * dpi), (int)Math.Round(5 * dpi));
            nativeTopBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Divider, 1f))
                    e.Graphics.DrawLine(pen, 0, nativeTopBar.Height - 1, nativeTopBar.Width, nativeTopBar.Height - 1);
            };

            var leftFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            topBarLogo = new PictureBox
            {
                Size = new Size((int)Math.Round(22 * dpi), (int)Math.Round(22 * dpi)),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Margin = new Padding(0, (int)Math.Round(4 * dpi), (int)Math.Round(8 * dpi), 0)
            };
            Bitmap brandBmp = Theme.GetBrandLogoBitmap();
            if (brandBmp != null) topBarLogo.Image = brandBmp;
            leftFlow.Controls.Add(topBarLogo);

            topBarTitleLabel = new Label
            {
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
                ForeColor = Theme.Ink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, (int)Math.Round(6 * dpi), (int)Math.Round(10 * dpi), 0)
            };
            leftFlow.Controls.Add(topBarTitleLabel);

            topBarMousePillBtn = CreateTopBarButton("锁鼠 · 按 Ctrl+Alt 释放", Theme.ButtonStyle.WarningSoft, Theme.Glyph.Lock, dpi);
            topBarMousePillBtn.Visible = false;
            topBarMousePillBtn.Click += delegate { ApplyMouseModeSelection(ControlMouseMode.Free, true); };
            leftFlow.Controls.Add(topBarMousePillBtn);

            var topBarActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            topBarMuteBtn = CreateTopBarButton("", Theme.ButtonStyle.IconGhost, Theme.Glyph.Volume, dpi);
            topBarMuteBtn.Width = (int)Math.Round(34 * dpi);
            topBarMuteBtn.Click += delegate
            {
                if (audioMuted || volumePercent <= 0)
                {
                    audioMuted = false;
                    volumePercent = Math.Max(20, lastNonZeroVolume);
                }
                else
                {
                    lastNonZeroVolume = volumePercent;
                    audioMuted = true;
                    volumePercent = 0;
                }
                ApplyVolumeToNative();
                RefreshTopBarVisuals();
            };

            topBarControlCenterBtn = CreateTopBarButton("控制中心", Theme.ButtonStyle.Chip, Theme.Glyph.Sliders, dpi);
            topBarControlCenterBtn.Click += delegate { ToggleTopBarControlCenter(); };

            topBarExitBtn = CreateTopBarButton("退出远控", Theme.ButtonStyle.DangerSoft, Theme.Glyph.Exit, dpi);
            topBarExitBtn.Click += delegate { DisconnectAndClose(true); };

            topBarActions.Controls.Add(topBarMuteBtn);
            topBarActions.Controls.Add(topBarControlCenterBtn);
            topBarActions.Controls.Add(topBarExitBtn);

            nativeTopBar.Controls.Add(topBarActions);
            nativeTopBar.Controls.Add(leftFlow);

            // V6.2 手机被控快捷导航条（连接 Android 被控端时自动显现）
            mobileNavBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = Math.Max(32, (int)Math.Round(34 * dpi)),
                BackColor = Theme.ChromeBg,
                Padding = new Padding((int)Math.Round(10 * dpi), (int)Math.Round(3 * dpi), (int)Math.Round(10 * dpi), (int)Math.Round(3 * dpi)),
                Visible = false
            };
            var mobileNavFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            Action<string, Theme.Glyph, int> addMobBtn = delegate(string label, Theme.Glyph glyph, int navAct)
            {
                Button mb = CreateTopBarButton(label, Theme.ButtonStyle.IconGhost, glyph, dpi);
                mb.Height = Math.Max(24, (int)Math.Round(26 * dpi));
                mb.MinimumSize = new Size(0, mb.Height);
                mb.Click += delegate
                {
                    BoBoStreamClient sc = streamClient;
                    if (sc != null && sc.IsConnected) sc.SendMobileNav(navAct);
                    FocusChildViewport();
                };
                mobileNavFlow.Controls.Add(mb);
            };
            addMobBtn("返回", Theme.Glyph.None, BoBoProtocol.MobileNavBack);
            addMobBtn("主页", Theme.Glyph.None, BoBoProtocol.MobileNavHome);
            addMobBtn("多任务", Theme.Glyph.Restore, BoBoProtocol.MobileNavRecents);
            addMobBtn("通知栏", Theme.Glyph.Bell, BoBoProtocol.MobileNavNotifications);
            addMobBtn("音量+", Theme.Glyph.Plus, BoBoProtocol.MobileNavVolumeUp);
            addMobBtn("音量-", Theme.Glyph.Minus, BoBoProtocol.MobileNavVolumeDown);
            addMobBtn("锁屏", Theme.Glyph.Lock, BoBoProtocol.MobileNavLock);
            mobileNavBar.Controls.Add(mobileNavFlow);
            Controls.Add(mobileNavBar);

            Controls.Add(nativeTopBar);
            RefreshTopBarVisuals();

            // 宽敞精美的进入/连接提醒卡片（彻底解决旧版字块太挤、高 DPI 挤压的问题）
            statusOverlay = new ConnectingOverlayCard(this.hostDisplayName, this.currentModeTag, delegate { DisconnectAndClose(true); });
            Controls.Add(statusOverlay);
            statusOverlay.BringToFront();

            hudOverlay = new NativeHudOverlay();
            hintToast = new StreamHintToast();

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
                if (BoundsMoved != null) { try { BoundsMoved(); } catch { } }
            };

            Move += delegate
            {
                QueueViewportUpdate();
                UpdateHudPosition();
                if (BoundsMoved != null) { try { BoundsMoved(); } catch { } }
            };

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
                    ApplyMouseModeSelection(ControlMouseMode.Free, false);
                    RaiseToolbarHotkey();
                }
                else if (e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.F)
                {
                    e.Handled = true;
                    HandleToggleFullscreenRequest();
                }
                else if (e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.U)
                {
                    e.Handled = true;
                    ToggleMiniWindowMode();
                }
                else if (e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.G)
                {
                    e.Handled = true;
                    ToggleHideFloatBallInFullscreen();
                }
                else if (e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.M)
                {
                    e.Handled = true;
                    ApplyMouseModeSelection(freeMouseMode ? ControlMouseMode.Locked : ControlMouseMode.Free, true);
                }
                else if (e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.Right)
                {
                    e.Handled = true;
                    SwitchNextRemoteDisplay();
                }
            };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                CloseTopBarControlCenter();
                if (!closingIntentionally)
                {
                    closingIntentionally = true;
                    CleanupSessionResources(true);
                }
                try { if (hudOverlay != null && !hudOverlay.IsDisposed) hudOverlay.Close(); } catch { }
                try { if (hintToast != null && !hintToast.IsDisposed) hintToast.Close(); } catch { }
            };

            FormClosed += delegate
            {
                superDisplayResizeTimer.Dispose();
                Engines.ReleaseCursorClip();
                if (SessionEnded != null)
                {
                    try { SessionEnded(); } catch { }
                }
            };
        }

        private static Button CreateTopBarButton(string text, Theme.ButtonStyle style, Theme.Glyph glyph, float dpi)
        {
            var btn = Theme.Action(text, style, glyph);
            int height = Math.Max(26, (int)Math.Round(28 * dpi));
            // The native Button measurement sees the caption, so include room for the painted glyph in its padding.
            int horizontalPadding = (int)Math.Round((glyph == Theme.Glyph.None ? 8 : 17) * dpi);
            btn.Dock = DockStyle.None;
            btn.Font = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Bold);
            btn.AutoSize = !string.IsNullOrEmpty(text);
            btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btn.Size = new Size(btn.Width, height);
            btn.MinimumSize = new Size(0, height);
            btn.Padding = new Padding(horizontalPadding, 0, horizontalPadding, 0);
            btn.Margin = new Padding((int)Math.Round(6 * dpi), (int)Math.Round(dpi), 0, 0);
            btn.TabStop = false;
            return btn;
        }

        internal StreamControlCenterModel BuildControlCenterModel()
        {
            Size viewport = GetPhysicalVideoViewport();

            string displayLabel = "显示屏 1 (" + hostDisplayName + ")";
            if (remoteMonitors != null && remoteMonitors.Count > 0)
            {
                int idx = Math.Max(0, Math.Min(activeMonitorIndex, remoteMonitors.Count - 1));
                BoBoMonitorDescriptor m = remoteMonitors[idx];
                displayLabel = "显示屏 " + (idx + 1) + " / " + remoteMonitors.Count + " (" + m.Name + ")";
            }

            return new StreamControlCenterModel
            {
                HostDisplayName = hostDisplayName,
                DisplayDeviceName = displayLabel,
                RemoteMonitors = remoteMonitors,
                SelectedMonitorIndex = activeMonitorIndex,
                OnSelectMonitor = SwitchRemoteDisplay,
                LocalDisplayWidth = viewport.Width > 0 ? viewport.Width : currentWidth,
                LocalDisplayHeight = viewport.Height > 0 ? viewport.Height : currentHeight,
                HostNativeWidth = hostNativeWidth > 0 ? hostNativeWidth : currentWidth,
                HostNativeHeight = hostNativeHeight > 0 ? hostNativeHeight : currentHeight,
                CurrentWidth = currentWidth,
                CurrentHeight = currentHeight,
                DisplayScalePercent = displayScalePercent,
                ResolutionMode = resolutionMode,
                BitrateKbps = currentBitrateKbps,
                AutoBitrate = autoBitrate,
                Fps = currentFps,
                TrueColor = trueColor,
                HdrEnabled = hdrEnabled,
                AspectFit = scaleMode != 0,
                ScaleMode = scaleMode,
                SuperDisplayActive = superDisplayMode,
                SuperDisplayStatus = GetSuperDisplayStatus(),
                ConnectionRoute = GetConnectionPath(streamClient),
                MiniWindowMode = miniWindowMode,
                MiniWindowOpacity = miniWindowOpacity,
                IsFullscreen = !windowedMode && !miniWindowMode,
                VolumePercent = volumePercent,
                AudioMuted = audioMuted,
                LockHostOnDisconnect = lockHostOnDisconnect,
                MuteHostAudio = !hostAudio,
                RestoreHostAudioOnDisconnect = restoreHostAudioOnDisconnect,
                RemotePrivacyActive = remotePrivacyActive,
                AutoRemotePrivacyOnConnect = autoRemotePrivacyOnConnect,
                LocalPrivacyActive = LocalPrivacyActiveProvider != null && LocalPrivacyActiveProvider(),
                SendSystemShortcuts = sendSystemShortcuts,
                MouseMode = mouseMode,
                MicEnabled = micEnabled,
                ShowNetworkStats = hudVisible,
                HideFloatBallInFullscreen = hideFloatBallInFullscreen,
                ShowFloatBallInWindowed = showFloatBallInWindowed,

                OnSelectResolution = async delegate(int w, int h, ControlResolutionMode mode)
                {
                    resolutionMode = mode;
                    SetSuperDisplayMode(false, false);
                    string tag = h + "p" + currentFps + " · D3D11VA";
                    ShowToastHint("正在切换分辨率至 " + w + " × " + h + "…");
                    await SwitchQualityAsync(w, h, currentFps, currentBitrateKbps, tag);
                },
                OnSelectDisplayScale = delegate(int pct)
                {
                    displayScalePercent = pct;
                    ShowToastHint("已记录远控显示缩放 " + pct + "%（被控端 DPI 缩放生效后画面将按此比例呈现）");
                },
                OnToggleSuperDisplay = delegate
                {
                    ToggleSuperDisplayMode();
                },
                OnSwitchNextDisplay = delegate { SwitchNextRemoteDisplay(); },

                OnSelectBitrate = async delegate(int kbps, bool isAuto)
                {
                    autoBitrate = isAuto;
                    string label = isAuto ? "自动高清" : ((kbps / 1000) + " Mbps");
                    ShowToastHint("正在切换画质码率至 " + label + "…");
                    await SwitchQualityAsync(currentWidth, currentHeight, currentFps, kbps, currentHeight + "p" + currentFps + " · " + label);
                },
                OnSelectFps = async delegate(int fps)
                {
                    ShowToastHint("正在切换帧率至 " + fps + " 帧…");
                    await SwitchQualityAsync(currentWidth, currentHeight, fps, currentBitrateKbps, currentHeight + "p" + fps + " · D3D11VA");
                },
                OnSelectTrueColor = async delegate(bool tc)
                {
                    trueColor = tc;
                    preferHevc = tc || hdrEnabled;
                    ShowToastHint(tc ? "已切换为真彩编码优先（HEVC 高色深）" : "已切换为常规色彩编码");
                    await SwitchQualityAsync(currentWidth, currentHeight, currentFps, currentBitrateKbps, currentModeTag);
                },
                OnToggleHdr = async delegate(bool on)
                {
                    hdrEnabled = on;
                    preferHevc = on || trueColor;
                    ShowToastHint(on ? "已开启 HDR 优先传输" : "已关闭 HDR 传输");
                    await SwitchQualityAsync(currentWidth, currentHeight, currentFps, currentBitrateKbps, currentModeTag);
                },
                OnSelectWeakNetwork = async delegate
                {
                    autoBitrate = true;
                    ShowToastHint("已选择弱网抗丢包画质 · 720p / 30 帧");
                    await SwitchQualityAsync(1280, 720, 30, 3500, "弱网抗丢包 · 720p30");
                },

                OnSelectAspectFit = delegate(bool fit)
                {
                    ApplyScaleModeSelection(fit ? 1 : 0);
                },
                OnSelectScaleMode = delegate(int mode)
                {
                    ApplyScaleModeSelection(mode);
                },
                OnToggleMiniWindow = delegate { ToggleMiniWindowMode(); },
                OnSelectMiniOpacity = delegate(int pct)
                {
                    miniWindowOpacity = Math.Max(50, Math.Min(100, pct));
                    if (miniWindowMode) Opacity = miniWindowOpacity / 100.0;
                    if (MiniOpacityPreferenceChanged != null)
                        try { MiniOpacityPreferenceChanged(miniWindowOpacity); } catch { }
                    ShowToastHint("小窗不透明度已设为 " + miniWindowOpacity + "%");
                },

                OnVolumeChanged = delegate(int vol, bool muted)
                {
                    volumePercent = Math.Max(0, Math.Min(100, vol));
                    audioMuted = muted || volumePercent == 0;
                    if (volumePercent > 0) lastNonZeroVolume = volumePercent;
                    ApplyVolumeToNative();
                    RefreshTopBarVisuals();
                },

                OnToggleLockHostOnDisconnect = delegate(bool on)
                {
                    lockHostOnDisconnect = on;
                    if (LockHostOnDisconnectPreferenceChanged != null)
                        try { LockHostOnDisconnectPreferenceChanged(on); } catch { }
                    ShowToastHint(on ? "已开启：断开远控后被控端将自动锁屏" : "已关闭：断开远控后不自动锁屏");
                },
                OnToggleMuteHostAudio = delegate(bool muteHost)
                {
                    hostAudio = !muteHost;
                    if (HostAudioPreferenceChanged != null)
                        try { HostAudioPreferenceChanged(hostAudio); } catch { }
                    BoBoStreamClient sc = streamClient;
                    if (sc != null && sc.IsConnected) sc.SendSetMuteHost(muteHost);
                    ShowToastHint(muteHost ? "已实时静音被控端扬声器（仅控制端播放声音）" : "已恢复被控端同步发声");
                },
                OnToggleRestoreHostAudio = delegate(bool on)
                {
                    restoreHostAudioOnDisconnect = on;
                    ShowToastHint(on ? "远程结束后将自动恢复被控端音频输出" : "已调整远程结束后的声音恢复选项");
                },
                OnSetRemotePrivacyNow = delegate(bool enable)
                {
                    remotePrivacyActive = enable;
                    BoBoStreamClient sc = streamClient;
                    if (sc != null && sc.IsConnected) sc.SendSetPrivacy(enable);
                    ShowToastHint(enable ? "已直连通知被控端立即开启防窥屏" : "已直连通知被控端关闭防窥屏");
                },
                OnToggleAutoRemotePrivacy = delegate(bool on)
                {
                    autoRemotePrivacyOnConnect = on;
                    if (AutoRemotePrivacyPreferenceChanged != null)
                        try { AutoRemotePrivacyPreferenceChanged(on); } catch { }
                    if (on && remoteSessionConnected)
                    {
                        remotePrivacyActive = true;
                        BoBoStreamClient sc = streamClient;
                        if (sc != null && sc.IsConnected) sc.SendSetPrivacy(true);
                    }
                    ShowToastHint(on ? "已开启：连接瞬间由 BoBo 协议直连同步拉起被控端防窥" : "已关闭连接自动防窥");
                },
                OnToggleLocalPrivacy = delegate
                {
                    if (ToggleLocalPrivacyRequested != null) ToggleLocalPrivacyRequested();
                },

                OnToggleSendSystemShortcuts = delegate(bool on)
                {
                    sendSystemShortcuts = on;
                    BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                    if (nc != null) nc.SetSysKeys(on);
                    if (SendSysKeysPreferenceChanged != null)
                        try { SendSysKeysPreferenceChanged(on); } catch { }
                    ShowToastHint(on ? "已开启：Alt+Tab / Win 键将直接发送到被控端" : "已关闭：系统快捷键保留在本机生效");
                },
                OnSelectMouseMode = delegate(ControlMouseMode m) { ApplyMouseModeSelection(m, true); },
                OnToggleMic = delegate
                {
                    micEnabled = !micEnabled;
                    ShowToastHint(micEnabled ? "提示：当前版本默认传输被控端系统音频（48kHz 立体声）" : "已关闭麦克风提示状态");
                },

                OnSendRemoteKeys = delegate(int[] vks) { SendRemoteKeys(vks); },
                OnSendClipboardText = delegate { SyncLocalClipboardToRemote(); },
                OnCaptureScreenshot = delegate { CaptureScreenshotToPictures(); },
                OnRestoreMainWindow = delegate
                {
                    Engines.ReleaseCursorClip();
                    if (RestoreMainRequested != null) RestoreMainRequested();
                },

                OnToggleNetworkStats = delegate(bool on)
                {
                    if (hudVisible != on)
                    {
                        if (ToggleHudRequested != null) ToggleHudRequested();
                        else SetHudVisible(on);
                    }
                },
                OnToggleHideFloatBallInFullscreen = delegate(bool hide)
                {
                    hideFloatBallInFullscreen = hide;
                    if (HideFloatBallPreferenceChanged != null)
                        try { HideFloatBallPreferenceChanged(hide); } catch { }
                    ShowToastHint(hide ? "全屏状态已隐藏悬浮球 · 按 Ctrl+Alt+G 随时恢复" : "全屏状态已恢复显示悬浮球");
                },
                OnToggleShowFloatBallInWindowed = delegate(bool show)
                {
                    showFloatBallInWindowed = show;
                    if (ShowFloatBallWindowedPreferenceChanged != null)
                        try { ShowFloatBallWindowedPreferenceChanged(show); } catch { }
                },

                OnToggleFullscreen = delegate { HandleToggleFullscreenRequest(); },
                OnDisconnect = delegate { DisconnectAndClose(true); }
            };
        }

        private void ToggleTopBarControlCenter()
        {
            if (topBarMenuSession != null && topBarMenuSession.IsOpen)
            {
                CloseTopBarControlCenter();
                return;
            }
            Engines.ReleaseCursorClip();
            StreamControlCenterModel model = BuildControlCenterModel();
            topBarMenuSession = new UuControlCenterSession(model, delegate
            {
                SafeUiInvoke(delegate
                {
                    if (topBarControlCenterBtn != null && !topBarControlCenterBtn.IsDisposed)
                        Theme.Highlight(topBarControlCenterBtn, false);
                });
            });
            Rectangle btnScreen = topBarControlCenterBtn.RectangleToScreen(topBarControlCenterBtn.ClientRectangle);
            topBarMenuSession.ShowAt(new Point(btnScreen.Left - 140, btnScreen.Bottom + 6), btnScreen, this);
            Theme.Highlight(topBarControlCenterBtn, topBarMenuSession.IsOpen);
        }

        private void CloseTopBarControlCenter()
        {
            if (topBarMenuSession != null)
            {
                UuControlCenterSession s = topBarMenuSession;
                topBarMenuSession = null;
                s.CloseAll();
            }
        }

        private void ToggleSuperDisplayMode()
        {
            SetSuperDisplayMode(!superDisplayMode, true);
        }

        private void SetSuperDisplayMode(bool enabled, bool showHint)
        {
            if (superDisplayMode == enabled) return;
            superDisplayMode = enabled;
            if (enabled)
            {
                scaleMode = 1;
                if (nativeClient != null) nativeClient.SetScaleMode(scaleMode);
            }
            showDisplayResult = showHint && enabled;
            SendViewportIfChanged();
            if (showHint)
                ShowToastHint(enabled ? GetSuperDisplayStatus() : "已关闭超级屏，恢复原串流分辨率设置");
        }

        private string GetSuperDisplayStatus()
        {
            if (!superDisplayMode) return "按画面区域自适应，全屏时可临时调整被控端分辨率";
            BoBoStreamClient client = streamClient;
            if (!remoteSessionConnected || client == null) return "连接后自动适配画面";
            if (!client.SupportsSuperDisplayViewport) return "对方暂不支持超级屏分辨率协商，当前使用等比显示";
            if (displayState == null || displayState.RequestId != displayRequestId || !displayState.Active)
                return "正在适配当前画面区域…";

            string desktop = displayState.DesktopWidth > 0 && displayState.DesktopHeight > 0
                ? displayState.DesktopWidth + " × " + displayState.DesktopHeight : "原分辨率";
            string stream = actualStreamSize.IsEmpty ? "" : " · 串流 " + actualStreamSize.Width + " × " + actualStreamSize.Height;
            if (displayState.NativeMatch) return "被控端分辨率已匹配 " + desktop + stream;
            if (displayState.DesktopModeChanged) return "被控端调整为 " + desktop + "，按比例显示" + stream;
            return "保留被控端 " + desktop + "，按比例显示" + stream;
        }

        private void ApplyScaleModeSelection(int mode)
        {
            if (mode < 0 || mode > 2) mode = 1;
            SetSuperDisplayMode(false, false);
            scaleMode = mode;
            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
            if (nc != null) nc.SetScaleMode(mode);
            if (mode == 2) ShowToastHint("窗口画面：拉伸铺满");
            else if (mode == 1) ShowToastHint("窗口画面：等比居中缩放");
            else ShowToastHint("窗口画面：视频像素 1:1 显示");
        }

        private void HandleToggleFullscreenRequest()
        {
            if (miniWindowMode)
            {
                ToggleMiniWindowMode();
                return;
            }
            if (ToggleWindowedRequested != null) ToggleWindowedRequested();
            else SetWindowedMode(!windowedMode);
        }

        internal void ToggleMiniWindowMode()
        {
            if (IsDisposed) return;
            CloseTopBarControlCenter();
            miniWindowMode = !miniWindowMode;
            if (miniWindowMode)
            {
                if (!windowedMode)
                {
                    windowedMode = true;
                }
                if (FormBorderStyle == FormBorderStyle.Sizable && WindowState == FormWindowState.Normal)
                {
                    savedPreMiniBounds = Bounds;
                    hasSavedPreMiniBounds = true;
                }
                Screen scr = Screen.FromControl(this) ?? Screen.PrimaryScreen;
                Rectangle wa = scr.WorkingArea;
                float dpi = Theme.GetDpiScale(this);
                int mw = Math.Min(wa.Width - 32, (int)Math.Round(520 * dpi));
                int mh = Math.Min(wa.Height - 32, (int)Math.Round(330 * dpi));
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.SizableToolWindow;
                TopMost = true;
                Opacity = miniWindowOpacity / 100.0;
                Bounds = new Rectangle(wa.Right - mw - 18, wa.Bottom - mh - 18, mw, mh);
                ShowToastHint("已进入置顶小窗模式（透明度 " + miniWindowOpacity + "%） · 按 Ctrl+Alt+U 恢复");
            }
            else
            {
                Opacity = 1.0;
                TopMost = false;
                FormBorderStyle = windowedMode ? FormBorderStyle.Sizable : FormBorderStyle.None;
                if (hasSavedPreMiniBounds && savedPreMiniBounds.Width >= 640 && savedPreMiniBounds.Height >= 400)
                    Bounds = savedPreMiniBounds;
                else
                    ApplyWindowStyle(windowedMode, false);
                ShowToastHint("已退出小窗模式");
            }
            RefreshTopBarVisuals();
            SyncChildViewportSize();
            UpdateHudPosition();
            if (BoundsMoved != null) { try { BoundsMoved(); } catch { } }
        }

        private void ToggleHideFloatBallInFullscreen()
        {
            hideFloatBallInFullscreen = !hideFloatBallInFullscreen;
            if (HideFloatBallPreferenceChanged != null)
                try { HideFloatBallPreferenceChanged(hideFloatBallInFullscreen); } catch { }
            ShowToastHint(hideFloatBallInFullscreen
                ? "已隐藏全屏悬浮球 · 按 Ctrl+Alt+G 恢复或 Ctrl+Alt+Shift+B 唤出控制中心"
                : "已恢复显示全屏贴边悬浮球");
        }

        private void SwitchNextRemoteDisplay()
        {
            BoBoStreamClient sc = streamClient;
            if (sc == null || !sc.IsConnected)
            {
                ShowToastHint("远控连接尚未建立，暂无法切换屏幕");
                return;
            }
            if (remoteMonitors == null || remoteMonitors.Count <= 1)
            {
                ShowToastHint("被控端当前仅有 1 块活跃显示器");
                return;
            }
            SwitchRemoteDisplay((activeMonitorIndex + 1) % remoteMonitors.Count);
        }

        private void SwitchRemoteDisplay(int index)
        {
            BoBoStreamClient sc = streamClient;
            if (sc == null || !sc.IsConnected || remoteMonitors == null || index < 0 || index >= remoteMonitors.Count || index == activeMonitorIndex) return;
            activeMonitorIndex = index;
            BoBoMonitorDescriptor m = remoteMonitors[index];
            if (m.Width > 0 && m.Height > 0)
            {
                hostNativeWidth = m.Width;
                hostNativeHeight = m.Height;
            }
            sc.SendReconfigure(activeMonitorIndex, currentWidth, currentHeight, currentFps, currentBitrateKbps, autoBitrate);
            RefreshTopBarVisuals();
            ShowToastHint("正在切换至显示屏 " + (activeMonitorIndex + 1) + "（" + m.Name + "）");
        }

        private void ApplyMouseModeSelection(ControlMouseMode mode, bool showHint)
        {
            mouseMode = mode;
            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
            if (mode == ControlMouseMode.ViewOnly)
            {
                freeMouseMode = true;
                Engines.ReleaseCursorClip();
                if (nc != null) { nc.SetMouseMode(true); nc.SetViewOnly(true); }
                BoBoStreamClient scVo = streamClient;
                if (scVo != null && scVo.IsConnected) scVo.SendSetCursorMode(false);
                if (showHint) ShowToastHint("已切换至「仅查看模式」· 远端键鼠输入已锁定防误触");
            }
            else if (mode == ControlMouseMode.Locked)
            {
                freeMouseMode = false;
                if (nc != null) { nc.SetViewOnly(false); nc.SetMouseMode(false); }
                BoBoStreamClient scLk = streamClient;
                if (scLk != null && scLk.IsConnected) scLk.SendSetCursorMode(true);
                if (showHint) ShowToastHint("已进入「锁鼠游戏模式」· 随时轻按 Ctrl+Alt 立即释放鼠标并点浮窗");
            }
            else
            {
                freeMouseMode = true;
                Engines.ReleaseCursorClip();
                if (nc != null) { nc.SetViewOnly(false); nc.SetMouseMode(true); }
                BoBoStreamClient scFr = streamClient;
                if (scFr != null && scFr.IsConnected) scFr.SendSetCursorMode(false);
                if (showHint) ShowToastHint("已切回「自由指针模式」· 光标零延迟跟随并可自由移出窗口");
            }

            RefreshTopBarVisuals();
            if (MouseModeChanged != null)
            {
                try { MouseModeChanged(freeMouseMode); } catch { }
            }
        }

        private void ApplyVolumeToNative()
        {
            int effective = audioMuted ? 0 : Math.Max(0, Math.Min(100, volumePercent));
            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
            if (nc != null) nc.SetVolume(effective);
        }

        private void SyncLocalClipboardToRemote()
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    ShowToastHint("本机剪贴板暂无文本内容可同步");
                    return;
                }
                string text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return;
                if (text.Length > 4000) text = text.Substring(0, 4000);
                SendRemoteText(text);
                ShowToastHint("已将本机剪贴板文本同步输入到远端（" + text.Length + " 字）");
            }
            catch
            {
                ShowToastHint("读取本机剪贴板失败");
            }
        }

        private void CaptureScreenshotToPictures()
        {
            try
            {
                if (videoHost == null || videoHost.Width < 16 || videoHost.Height < 16) return;
                Point topLeft = videoHost.PointToScreen(Point.Empty);
                string picRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BoBoRemote");
                Directory.CreateDirectory(picRoot);
                string fileName = "BoBo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";
                string fullPath = Path.Combine(picRoot, fileName);
                using (var bmp = new Bitmap(videoHost.Width, videoHost.Height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(topLeft, Point.Empty, videoHost.Size);
                    bmp.Save(fullPath, ImageFormat.Png);
                }
                ShowToastHint("截屏已保存：图片\\BoBoRemote\\" + fileName);
            }
            catch
            {
                ShowToastHint("截屏保存失败，请重试");
            }
        }

        internal void ShowToastHint(string text)
        {
            if (string.IsNullOrEmpty(text) || IsDisposed) return;
            SafeUiInvoke(delegate
            {
                if (IsDisposed || hintToast == null || hintToast.IsDisposed) return;
                Rectangle hostRect = videoHost != null && videoHost.IsHandleCreated
                    ? videoHost.RectangleToScreen(videoHost.ClientRectangle)
                    : Bounds;
                hintToast.ShowHint(text, hostRect, this);
            });
        }

        internal void SetWindowedMode(bool windowed)
        {
            if (IsDisposed) return;
            CloseTopBarControlCenter();
            if (miniWindowMode)
            {
                miniWindowMode = false;
                Opacity = 1.0;
            }
            if (windowedMode == windowed) return;
            windowedMode = windowed;
            ApplyWindowStyle(windowedMode, false);
            RefreshTopBarVisuals();
            CenterStatusOverlay();
            SyncChildViewportSize();
            UpdateHudPosition();
            FocusChildViewport();
            if (BoundsMoved != null) { try { BoundsMoved(); } catch { } }
        }

        internal void SetFreeMouse(bool free)
        {
            ApplyMouseModeSelection(free ? ControlMouseMode.Free : ControlMouseMode.Locked, false);
        }

        internal void SetHudVisible(bool visible)
        {
            hudVisible = visible;
            RefreshTopBarVisuals();
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
            BoBoStreamClient sc = streamClient;
            if (sc != null && sc.IsConnected) sc.SendKeyCombo(vks);
        }

        internal void SendRemoteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            BoBoStreamClient sc = streamClient;
            if (sc != null && sc.IsConnected) sc.SendClipboardText(text);
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
            RefreshTopBarVisuals();

            BoBoStreamClient sc = streamClient;
            if (remoteSessionConnected && sc != null && sc.IsConnected)
            {
                // V6 热切换：原连接无缝调整分辨率/帧率/码率，零黑屏不断连
                sc.SendReconfigure(activeMonitorIndex, currentWidth, currentHeight, currentFps, currentBitrateKbps, autoBitrate);
                return;
            }

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
            CloseTopBarControlCenter();
            if (closingIntentionally)
            {
                if (!IsDisposed)
                {
                    try { Close(); } catch { }
                }
                return;
            }
            closingIntentionally = true;

            // 若开启「远程结束后被控端自动锁屏」，在断开前直接通过 BoBo 协议通知被控端锁屏
            // SendLockWorkstation() 与后续 Disconnect() 中的 DISCONNECT 严格按 FIFO 发送队列顺序刷出，无需在 UI 线程 Thread.Sleep 阻塞
            bool shouldLockRemote = cancelRemoteHost && lockHostOnDisconnect && remoteSessionConnected;
            if (shouldLockRemote)
            {
                try
                {
                    BoBoStreamClient sc = streamClient;
                    if (sc != null && sc.IsConnected)
                    {
                        sc.SendLockWorkstation();
                    }
                }
                catch { }
            }

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

        internal async Task SwitchEndpointAsync(string newHost, int newPort, string reasonText)
        {
            if (IsDisposed || closingIntentionally || string.IsNullOrWhiteSpace(newHost)) return;
            host = newHost.Trim();
            targetPort = (newPort > 0 && newPort <= 65535) ? newPort : BoBoProtocol.DefaultTcpPort;
            ShowStatusOverlay(
                "正在动态切换中转节点",
                string.IsNullOrEmpty(reasonText)
                    ? ("已优选切换至 " + host + ":" + targetPort + "，正在重建加密传输通道…")
                    : reasonText,
                false);
            await StartOrRestartStreamInternalAsync();
        }

        private async Task StartOrRestartStreamInternalAsync()
        {
            int gen = Interlocked.Increment(ref sessionGeneration);
            remoteSessionConnected = false;
            lastErrorMessage = "";

            StopCurrentPlayerAndCancelCts(true);

            if (IsDisposed || closingIntentionally) return;
            ShowStatusOverlay("正在连接 " + hostDisplayName, "正在与对方 BoBo远程 建立 TLS 1.2 双端加密通道（" + currentModeTag + "）…", false);

            string coreErr;
            if (!BoBoCoreInterop.EnsureLoaded(out coreErr))
            {
                ShowFatalError(coreErr ?? "内置 BoBoCore 原生内核加载失败。");
                return;
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            launchCts = cts;

            var client = new BoBoStreamClient(BoBoIdentityStore.Default);
            string nativeErr;
            BoBoCoreInterop.BoBoClientNativeHandle nc = BoBoCoreInterop.CreateClientSession(
                videoHost.Handle,
                currentWidth,
                currentHeight,
                freeMouseMode,
                delegate(BoBoInputPacket pkt)
                {
                    if (client.IsConnected) client.SendInputPacket(pkt);
                },
                delegate(int hotkeyCode, int param)
                {
                    HandleNativeHotkey(hotkeyCode, gen);
                },
                out nativeErr);

            if (nc == null)
            {
                client.Dispose();
                ShowFatalError(nativeErr ?? "初始化进程内 D3D11VA 渲染视口失败。");
                return;
            }

            lock (procGate)
            {
                streamClient = client;
                nativeClient = nc;
            }

            SyncChildViewportSize();
            nc.SetScaleMode(scaleMode);
            nc.SetViewOnly(mouseMode == ControlMouseMode.ViewOnly);
            nc.SetSysKeys(sendSystemShortcuts);
            ApplyVolumeToNative();

            client.StreamStarted += delegate(List<BoBoMonitorDescriptor> mons, int w, int h, int fps)
            {
                if (gen != sessionGeneration) return;
                string remotePlat = client.RemotePlatform ?? BoBoProtocol.PlatformWindows;
                string remoteCtrlMode = client.RemoteHostControlMode ?? BoBoProtocol.ControlModeFull;
                bool isViewOnlyRemote = string.Equals(remoteCtrlMode, BoBoProtocol.ControlModeViewOnly, StringComparison.OrdinalIgnoreCase);

                SafeUiInvoke(delegate
                {
                    if (gen != sessionGeneration || IsDisposed) return;
                    if (mons != null && mons.Count > 0)
                    {
                        remoteMonitors = mons;
                        int idx = Math.Max(0, Math.Min(activeMonitorIndex, mons.Count - 1));
                        hostNativeWidth = mons[idx].Width;
                        hostNativeHeight = mons[idx].Height;
                    }
                    actualStreamSize = new Size(w, h);
                    statusOverlay.SetOverlayVisible(false);
                    if (mobileNavBar != null && !mobileNavBar.IsDisposed)
                    {
                        mobileNavBar.Visible = string.Equals(remotePlat, BoBoProtocol.PlatformAndroid, StringComparison.OrdinalIgnoreCase);
                    }
                    if (isViewOnlyRemote)
                    {
                        ApplyMouseModeSelection(ControlMouseMode.ViewOnly, false);
                    }
                    remoteSessionConnected = true;
                    SyncChildViewportSize();
                    SendViewportIfChanged();
                    FocusChildViewport();
                    StartStatsPollingTimer();
                    if (hudVisible && hudOverlay != null && !hudOverlay.IsDisposed)
                    {
                        UpdateHudPosition();
                        if (!hudOverlay.Visible) hudOverlay.Show(this);
                    }
                    if (autoRemotePrivacyOnConnect) remotePrivacyActive = true;
                    if (PlayerProcessStarted != null)
                    {
                        try { PlayerProcessStarted(Process.GetCurrentProcess().Id); } catch { }
                    }
                    if (isViewOnlyRemote)
                    {
                        ShowToastHint("已进入仅查看协助模式（对方端仅支持屏幕共享，无法远程注入输入）");
                    }
                    else if (string.Equals(remotePlat, BoBoProtocol.PlatformAndroid, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowToastHint("已连接 Android 手机被控端 · 可直接点击拖动画面或使用顶部手机导航栏");
                    }
                    else if (!freeMouseMode)
                    {
                        ShowToastHint("已进入锁鼠模式 · 随时轻按 Ctrl+Alt 释放鼠标并点浮窗 · Ctrl+Alt+Shift+Q 退出");
                    }
                    NotifyStatus("已通过 BoBo V6.2 全内置内核接通 " + hostDisplayName + "（" + currentModeTag + "）。");
                });
            };

            client.RttUpdated += delegate(double rtt)
            {
                lastRttMs = rtt;
            };

            client.RemotePrivacyStateChanged += delegate(bool active)
            {
                remotePrivacyActive = active;
            };

            client.DisplayStateChanged += delegate(BoBoDisplayState state)
            {
                SafeUiInvoke(delegate
                {
                    if (gen != sessionGeneration || IsDisposed) return;
                    if (state.RequestId != displayRequestId || (state.MonitorIndex >= 0 && state.MonitorIndex != activeMonitorIndex)) return;
                    if (state.StreamWidth > 0 && state.StreamHeight > 0)
                        actualStreamSize = new Size(state.StreamWidth, state.StreamHeight);
                    displayState = state;
                    if (showDisplayResult && superDisplayMode && state.Active)
                    {
                        showDisplayResult = false;
                        ShowToastHint(GetSuperDisplayStatus());
                    }
                });
            };

            client.StreamError += delegate(string code, string emsg)
            {
                if (gen != sessionGeneration) return;
                lastErrorMessage = emsg;
            };

            client.Disconnected += delegate(string reason)
            {
                if (gen != sessionGeneration || closingIntentionally || switchingQuality || IsDisposed) return;
                SafeUiInvoke(delegate
                {
                    if (gen != sessionGeneration || closingIntentionally || switchingQuality || IsDisposed) return;
                    if (remoteSessionConnected)
                    {
                        NotifyStatus("远程桌面会话已结束：" + (string.IsNullOrEmpty(reason) ? "对方已断开" : reason));
                        DisconnectAndClose(false);
                    }
                    else
                    {
                        ShowFatalError(string.IsNullOrEmpty(lastErrorMessage) ? reason : lastErrorMessage);
                    }
                });
            };

            lastRequestedViewport = GetPhysicalVideoViewport();
            lastRequestedFullscreen = !windowedMode && !miniWindowMode;
            lastSuperDisplayRequestEnabled = superDisplayMode && CanAdaptViewport(lastRequestedViewport);
            displayRequestId = 0;
            displayState = null;
            actualStreamSize = Size.Empty;
            var startOpts = new BoBoStreamStartOptions
            {
                Host = host,
                Port = targetPort,
                MonitorIndex = activeMonitorIndex,
                Width = currentWidth,
                Height = currentHeight,
                Fps = currentFps,
                BitrateKbps = currentBitrateKbps,
                AutoBitrate = autoBitrate,
                FreeMouse = freeMouseMode,
                MuteHostAudio = !hostAudio,
                PreferCompressedAudio = preferCompressedAudio,
                PreferHevc = preferHevc,
                RequestPrivacy = autoRemotePrivacyOnConnect,
                SuperDisplay = lastSuperDisplayRequestEnabled,
                ClientWidth = lastRequestedViewport.Width,
                ClientHeight = lastRequestedViewport.Height,
                ClientFullscreen = lastRequestedFullscreen,
                IsCustomRelay = isCustomRelayRoute,
                RelayUdpPort = relayUdpPort,
                CustomStunHost = customStunHost,
                CustomStunPort = customStunPort
            };

            Tuple<bool, string, string> connRes;
            try
            {
                connRes = await client.ConnectAndStartStreamAsync(startOpts, nc, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (gen == sessionGeneration && !closingIntentionally && !IsDisposed)
                    ShowFatalError("发起 BoBo V6 加密串流失败：" + ex.Message);
                return;
            }

            if (gen != sessionGeneration || closingIntentionally || IsDisposed) return;

            if (!connRes.Item1)
            {
                if (string.Equals(connRes.Item2, "PAIR_REQUIRED", StringComparison.Ordinal))
                {
                    NotifyStatus("目标主机尚未授权本机 BoBo 客户端，请输入对方生成的 4 位临时配对码。");
                    if (PairRequired != null)
                    {
                        try { PairRequired(host); } catch { }
                    }
                    DisconnectAndClose(false);
                    return;
                }
                ShowFatalError(string.IsNullOrEmpty(connRes.Item3) ? "无法连接目标 BoBo远程 主机。" : connRes.Item3);
                return;
            }

            if (!remoteSessionConnected)
            {
                ShowStatusOverlay("正在加载实时画面", "已完成双端 TLS 身份校验，正在接收首帧视频画面…", false);
            }
        }

        internal void SetRouteTransportHints(BoBoConnectionRoute route)
        {
            if (route == null) return;
            isCustomRelayRoute = (route.Kind == BoBoRouteKind.CustomRelay);
            relayUdpPort = route.CustomRelayUdpPort;
            customStunHost = route.RemoteAddress != null ? route.RemoteAddress.ToString() : "";
            customStunPort = route.CustomRelayStunPort;
        }

        private string GetConnectionPath(BoBoStreamClient client)
        {
            if (client == null || !remoteSessionConnected) return "";
            string route = ConnectionRouteLabelProvider != null ? ConnectionRouteLabelProvider() : "";
            string tr = client.ActiveTransportLabel;
            string pathLabel = client.ActiveUdpPathLabel;
            if (!string.IsNullOrEmpty(pathLabel) && tr.IndexOf(pathLabel, StringComparison.Ordinal) < 0)
            {
                tr = pathLabel + " · " + tr;
            }
            if (client.IsDirectUdpPromoted &&
                (route.StartsWith("Tailscale", StringComparison.Ordinal) || route.StartsWith("自建兜底中转", StringComparison.Ordinal)))
            {
                return tr;
            }
            return string.IsNullOrEmpty(route) ? tr : route + " · " + tr;
        }

        private void StartStatsPollingTimer()
        {
            if (statsTimer != null) return;
            statsTimer = new System.Windows.Forms.Timer { Interval = 500 };
            statsTimer.Tick += delegate
            {
                if (IsDisposed || !remoteSessionConnected) return;
                BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
                BoBoStreamClient sc = streamClient;
                if (nc == null || sc == null) return;
                int liveBps = sc.ActiveBitrateKbps > 0 ? sc.ActiveBitrateKbps : currentBitrateKbps;
                NativeStreamStats stats = nc.GetStats(sc.LatestRttMs, liveBps);
                stats.Codec = sc.ActiveVideoCodecLabel;
                stats.PacketLossPct = sc.LatestPacketLossPct;
                stats.JitterMs = sc.LatestJitterMs;
                stats.FecRecoveredPackets = sc.UdpFecRecoveredPackets;
                stats.NackRecoveredPackets = sc.UdpNackRecoveredPackets;
                stats.DroppedIncompleteFrames = sc.UdpDroppedFrames;
                stats.Transport = GetConnectionPath(sc);
                lastRttMs = stats.RttMs;
                if (hudVisible && hudOverlay != null && !hudOverlay.IsDisposed)
                {
                    hudOverlay.UpdateStats(stats, currentModeTag);
                    if (!hudOverlay.Visible)
                    {
                        UpdateHudPosition();
                        hudOverlay.Show(this);
                    }
                }
                RefreshTopBarVisuals();
            };
            statsTimer.Start();
        }

        private void HandleNativeHotkey(int hotkeyCode, int gen)
        {
            if (gen != sessionGeneration || IsDisposed) return;
            SafeUiInvoke(delegate
            {
                if (gen != sessionGeneration || IsDisposed) return;
                switch (hotkeyCode)
                {
                    case 1: // ReleaseMouse (Ctrl+Alt solo tap)
                        freeMouseMode = true;
                        mouseMode = ControlMouseMode.Free;
                        Engines.ReleaseCursorClip();
                        BoBoStreamClient scRel = streamClient;
                        if (scRel != null && scRel.IsConnected) scRel.SendSetCursorMode(false);
                        RefreshTopBarVisuals();
                        if (MouseModeChanged != null) { try { MouseModeChanged(true); } catch { } }
                        RaiseToolbarHotkey();
                        ShowToastHint("已释放鼠标 · 现在可直接点击悬浮球、控制中心或本机窗口");
                        break;
                    case 2: // Toolbar (Ctrl+Alt+Shift+B)
                    case 8: // EdgeTop
                        RaiseToolbarHotkey();
                        break;
                    case 3: // ToggleFullscreen (Ctrl+Alt+F)
                        HandleToggleFullscreenRequest();
                        break;
                    case 4: // ToggleFloatBall (Ctrl+Alt+G)
                        ToggleHideFloatBallInFullscreen();
                        break;
                    case 5: // ToggleMini (Ctrl+Alt+U)
                        ToggleMiniWindowMode();
                        break;
                    case 6: // NextDisplay (Ctrl+Alt+Right)
                        SwitchNextRemoteDisplay();
                        break;
                    case 7: // Disconnect (Ctrl+Alt+Shift+Q)
                        DisconnectAndClose(true);
                        break;
                }
            });
        }

        private void CleanupSessionResources(bool cancelRemoteHost)
        {
            Engines.ReleaseCursorClip();
            StopCurrentPlayerAndCancelCts(cancelRemoteHost);
        }

        private void StopCurrentPlayerAndCancelCts(bool cancelRemoteHost)
        {
            superDisplayResizeTimer.Stop();
            if (statsTimer != null)
            {
                try { statsTimer.Stop(); statsTimer.Dispose(); } catch { }
                statsTimer = null;
            }

            CancellationTokenSource oldCts = launchCts;
            launchCts = null;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                try { oldCts.Dispose(); } catch { }
            }

            BoBoStreamClient scToClose;
            BoBoCoreInterop.BoBoClientNativeHandle ncToClose;
            lock (procGate)
            {
                scToClose = streamClient;
                ncToClose = nativeClient;
                streamClient = null;
                nativeClient = null;
            }

            if (scToClose != null)
            {
                try { scToClose.Dispose(); } catch { }
            }
            if (ncToClose != null)
            {
                try { ncToClose.Dispose(); } catch { }
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
            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
            if (nc != null) nc.Resize(w, h);
            QueueViewportUpdate();
        }

        private void QueueViewportUpdate()
        {
            if (!remoteSessionConnected || !superDisplayMode || streamClient == null || !streamClient.SupportsSuperDisplayViewport)
                return;
            superDisplayResizeTimer.Stop();
            superDisplayResizeTimer.Start();
        }

        private void SendViewportIfChanged()
        {
            BoBoStreamClient client = streamClient;
            if (!remoteSessionConnected || client == null || !client.IsConnected || !client.SupportsSuperDisplayViewport)
                return;
            Size viewport = GetPhysicalVideoViewport();
            bool fullscreen = !windowedMode && !miniWindowMode;
            bool enable = superDisplayMode && CanAdaptViewport(viewport);
            if (superDisplayMode && !enable && (fullscreen || !lastRequestedFullscreen)) return;
            if (lastSuperDisplayRequestEnabled == enable &&
                (!enable || (viewport == lastRequestedViewport && fullscreen == lastRequestedFullscreen))) return;
            lastSuperDisplayRequestEnabled = enable;
            lastRequestedViewport = viewport;
            lastRequestedFullscreen = fullscreen;
            client.SendSetSuperDisplay(enable, viewport.Width, viewport.Height, fullscreen, ++displayRequestId);
        }

        private static bool CanAdaptViewport(Size viewport)
        {
            return viewport.Width >= 320 && viewport.Width <= 16384 && viewport.Height >= 240 && viewport.Height <= 16384;
        }

        private Size GetPhysicalVideoViewport()
        {
            if (!videoHost.IsHandleCreated || WindowState == FormWindowState.Minimized) return Size.Empty;
            // Ask for physical screen coordinates even if the app is system-DPI aware and the window is on a secondary monitor.
            IntPtr oldContext = IntPtr.Zero;
            try { oldContext = SetThreadDpiAwarenessContext(new IntPtr(-3)); }
            catch (EntryPointNotFoundException) { }
            try
            {
                NativeRect rect;
                if (oldContext != IntPtr.Zero && GetWindowRect(videoHost.Handle, out rect))
                    return new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
                return videoHost.ClientSize;
            }
            finally
            {
                if (oldContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldContext);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        private void FocusChildViewport()
        {
            BoBoCoreInterop.BoBoClientNativeHandle nc = nativeClient;
            if (nc != null) nc.Focus();
        }

        private void ShowStatusOverlay(string title, string detail, bool isError)
        {
            SafeUiInvoke(delegate
            {
                if (IsDisposed) return;
                statusOverlay.SetState(title, detail, currentModeTag, isError);
                statusOverlay.SetOverlayVisible(true);
                CenterStatusOverlay();
                statusOverlay.BringToFront();
            });
        }

        private void ShowFatalError(string message)
        {
            SafeUiInvoke(delegate
            {
                if (IsDisposed) return;
                statusOverlay.SetState("连接未能完成", message ?? "未知错误", currentModeTag, true);
                statusOverlay.SetOverlayVisible(true);
                CenterStatusOverlay();
                statusOverlay.BringToFront();
                NotifyStatus(message ?? "内置串流连接失败。");
            });
        }

        private void CenterStatusOverlay()
        {
            if (statusOverlay == null || IsDisposed) return;
            statusOverlay.UpdateDpiLayout(Theme.GetDpiScale(this), ClientSize);
            int x = Math.Max(0, (ClientSize.Width - statusOverlay.Width) / 2);
            int topOffset = (nativeTopBar != null && nativeTopBar.Visible) ? nativeTopBar.Height : 0;
            int availH = Math.Max(100, ClientSize.Height - topOffset);
            int y = topOffset + Math.Max(0, (availH - statusOverlay.Height) / 2);
            statusOverlay.Location = new Point(x, y);
        }

        internal static string FormatHudPipelineLine(NativeStreamStats s)
        {
            return NativeHudOverlay.FormatHudPipelineLine(s);
        }

        private void UpdateHudPosition()
        {
            if (hudOverlay == null || hudOverlay.IsDisposed || IsDisposed) return;
            Point pt = videoHost.PointToScreen(new Point(14, 14));
            hudOverlay.Location = pt;
        }

        private void RefreshTopBarVisuals()
        {
            SafeUiInvoke(delegate
            {
                if (IsDisposed || nativeTopBar == null) return;
                nativeTopBar.Visible = windowedMode || miniWindowMode;
                if (topBarTitleLabel != null)
                {
                    string rttText = (remoteSessionConnected && lastRttMs > 0.1)
                        ? (" · " + Math.Round(lastRttMs) + "ms")
                        : "";
                    topBarTitleLabel.Text = hostDisplayName + "  ·  " + currentModeTag + rttText;
                }
                if (topBarMuteBtn != null)
                {
                    bool muted = audioMuted || volumePercent <= 0;
                    Theme.ChangeGlyph(topBarMuteBtn, muted ? Theme.Glyph.VolumeOff : Theme.Glyph.Volume);
                    Theme.Highlight(topBarMuteBtn, muted);
                    topBarMuteBtn.AccessibleName = muted ? "取消静音" : "静音";
                }
                if (topBarMousePillBtn != null)
                {
                    if (mouseMode == ControlMouseMode.Locked)
                    {
                        topBarMousePillBtn.Text = "锁鼠 · 按 Ctrl+Alt 释放";
                        topBarMousePillBtn.AccessibleName = topBarMousePillBtn.Text;
                        Theme.ChangeGlyph(topBarMousePillBtn, Theme.Glyph.Lock);
                        Theme.ChangeStyle(topBarMousePillBtn, Theme.ButtonStyle.WarningSoft);
                        topBarMousePillBtn.Visible = true;
                    }
                    else if (mouseMode == ControlMouseMode.ViewOnly)
                    {
                        topBarMousePillBtn.Text = "仅查看模式 · 点击恢复操控";
                        topBarMousePillBtn.AccessibleName = topBarMousePillBtn.Text;
                        Theme.ChangeGlyph(topBarMousePillBtn, Theme.Glyph.Eye);
                        Theme.ChangeStyle(topBarMousePillBtn, Theme.ButtonStyle.AccentSoft);
                        topBarMousePillBtn.Visible = true;
                    }
                    else
                    {
                        topBarMousePillBtn.Visible = false;
                    }
                }
            });
        }

        private string BuildWindowTitle()
        {
            return "BoBo远程 — " + hostDisplayName + "（" + currentModeTag + "）";
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

        /// <summary>
        /// 全新设计的宽敞 DPI 自适应「进入远控/连接中」提醒卡片：
        /// 清晰的三段式视觉分区（品牌与标题区 / 动态进度与状态区 / 快捷键提示条 / 底部取消按钮），彻底告别文字挤压。
        /// </summary>
        private sealed class ConnectingOverlayCard : Control
        {
            private readonly Button cancelBtn;
            private readonly System.Windows.Forms.Timer animTimer;
            private string titleText;
            private string detailText;
            private string modeTag;
            private bool errorState;
            private float progressPhase;
            private float scale = 1f;

            internal ConnectingOverlayCard(string hostName, string modeTag, Action onCancel)
            {
                this.titleText = "正在连接 " + hostName;
                this.detailText = "正在通过内置加密通道握手…";
                this.modeTag = modeTag ?? "";

                DoubleBuffered = true;
                BackColor = Theme.Paper;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

                cancelBtn = Theme.Action("取消并退出", Theme.ButtonStyle.Secondary);
                cancelBtn.Dock = DockStyle.None;
                cancelBtn.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
                cancelBtn.TabStop = false;
                cancelBtn.Click += delegate { if (onCancel != null) onCancel(); };
                Controls.Add(cancelBtn);

                animTimer = new System.Windows.Forms.Timer { Interval = 30 };
                animTimer.Tick += delegate
                {
                    progressPhase += 0.025f;
                    if (progressPhase > 1.4f) progressPhase = -0.4f;
                    Invalidate();
                };
                animTimer.Start();
            }

            internal void SetState(string title, string detail, string tag, bool isError)
            {
                titleText = title ?? "";
                detailText = detail ?? "";
                if (!string.IsNullOrEmpty(tag)) modeTag = tag;
                errorState = isError;
                cancelBtn.Text = isError ? "关闭窗口" : "取消并退出";
                cancelBtn.AccessibleName = cancelBtn.Text;
                if (isError) animTimer.Stop();
                else if (Visible) animTimer.Start();
                Invalidate();
            }

            internal void UpdateDetail(string detail)
            {
                detailText = detail ?? "";
                Invalidate();
            }

            internal void SetOverlayVisible(bool visible)
            {
                Visible = visible;
                if (visible && !errorState) animTimer.Start();
                else animTimer.Stop();
            }

            internal void UpdateDpiLayout(float dpiScale, Size parentClient)
            {
                scale = Math.Max(1f, Math.Min(2.4f, dpiScale));
                int w = Math.Min(Math.Max(340, parentClient.Width - 40), (int)Math.Round(520 * scale));
                int h = (int)Math.Round(276 * scale);
                Size = new Size(w, h);

                int btnW = (int)Math.Round(132 * scale);
                int btnH = (int)Math.Round(36 * scale);
                cancelBtn.SetBounds(w - (int)Math.Round(28 * scale) - btnW, h - (int)Math.Round(22 * scale) - btnH, btnW, btnH);

                try
                {
                    using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), (int)Math.Round(16 * scale)))
                    {
                        Region old = Region;
                        Region = new Region(p);
                        if (old != null) old.Dispose();
                    }
                }
                catch { }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { animTimer.Stop(); animTimer.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                g.Clear(Theme.Paper);
                Theme.Surface(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f),
                    16f * scale, Theme.Paper, Theme.StrokeStrong);

                int padX = (int)Math.Round(28 * scale);
                int topY = (int)Math.Round(26 * scale);
                int logoSize = (int)Math.Round(44 * scale);

                // 1. Header Row: BoBo circular logo + Title + Mode Pill
                Bitmap logo = Theme.GetBrandLogoBitmap();
                if (logo != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using (var clip = Theme.Round(new Rectangle(padX, topY, logoSize, logoSize), 11f * scale))
                    {
                        var st = g.Save();
                        g.SetClip(clip);
                        g.DrawImage(logo, new Rectangle(padX, topY, logoSize, logoSize));
                        g.Restore(st);
                    }
                }
                else
                {
                    Theme.DrawBrandMark(g, new RectangleF(padX, topY, logoSize, logoSize));
                }

                int textX = padX + logoSize + (int)Math.Round(16 * scale);
                int textW = Width - textX - padX;
                using (var titleFont = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold))
                using (var tagFont = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular))
                using (var detailFont = new Font("Microsoft YaHei UI", 9.6f, FontStyle.Regular))
                using (var tipTitleFont = new Font("Microsoft YaHei UI", 8.4f, FontStyle.Bold))
                using (var tipKeyFont = new Font("Consolas", 8.5f, FontStyle.Bold))
                using (var tipDescFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
                using (var leftFmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    using (var tb = new SolidBrush(Theme.Ink))
                        g.DrawString(titleText, titleFont, tb, new RectangleF(textX, topY + 1f * scale, textW, 28f * scale), leftFmt);

                    string subtitlePill = string.IsNullOrEmpty(modeTag) ? "BoBo V5 原生 D3D11VA 硬解" : ("原生 D3D11VA 硬解  ·  " + modeTag);
                    using (var mb = new SolidBrush(Theme.Muted))
                        g.DrawString(subtitlePill, tagFont, mb, new RectangleF(textX, topY + 27f * scale, textW, 20f * scale), leftFmt);

                    // 2. Progress Bar + Stage Detail (spacious middle section)
                    int progY = topY + logoSize + (int)Math.Round(20 * scale);
                    int progW = Width - padX * 2;
                    int progH = Math.Max(4, (int)Math.Round(5 * scale));
                    var trackRect = new RectangleF(padX, progY, progW, progH);
                    using (var tp = Theme.Round(trackRect, progH / 2f))
                    using (var trackBg = new SolidBrush(errorState ? Theme.AlertLight : Theme.Stroke))
                        g.FillPath(trackBg, tp);

                    if (!errorState)
                    {
                        float barW = progW * 0.34f;
                        float barX = padX + (progW - barW) * Math.Max(0f, Math.Min(1f, progressPhase));
                        float clippedLeft = Math.Max(padX, barX);
                        float clippedRight = Math.Min(padX + progW, barX + barW);
                        if (clippedRight > clippedLeft + 2f)
                        {
                            var activeRect = new RectangleF(clippedLeft, progY, clippedRight - clippedLeft, progH);
                            using (var ap = Theme.Round(activeRect, progH / 2f))
                            using (var ab = new SolidBrush(Theme.Accent))
                                g.FillPath(ab, ap);
                        }
                    }

                    int detailY = progY + progH + (int)Math.Round(12 * scale);
                    using (var db = new SolidBrush(errorState ? Theme.Alert : Theme.BodyInk))
                        g.DrawString(detailText, detailFont, db, new RectangleF(padX, detailY, progW, 40f * scale));

                    // 3. Clean Shortcuts Quick-Tips Box
                    int tipY = detailY + (int)Math.Round(42 * scale);
                    int tipH = (int)Math.Round(46 * scale);
                    var tipBox = new RectangleF(padX, tipY, progW, tipH);
                    using (var boxP = Theme.Round(tipBox, 10f * scale))
                    using (var boxBg = new SolidBrush(Theme.SurfaceSubtle))
                    using (var boxBd = new Pen(Theme.Stroke, 1f))
                    {
                        g.FillPath(boxBg, boxP);
                        g.DrawPath(boxBd, boxP);
                    }

                    string[,] tips = new string[,]
                    {
                        { "Ctrl+Alt", "释放鼠标" },
                        { "Ctrl+Alt+F", "全屏切换" },
                        { "Ctrl+Alt+Shift+Q", "立即断开" }
                    };
                    float colW = tipBox.Width / 3f;
                    using (var keyBg = new SolidBrush(Theme.Paper))
                    using (var keyBd = new Pen(Theme.StrokeStrong, 1f))
                    using (var keyBr = new SolidBrush(Theme.Accent))
                    using (var descBr = new SolidBrush(Theme.BodyInk))
                    using (var centerFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            float cx = tipBox.X + colW * i + colW * 0.5f;
                            var kRect = new RectangleF(cx - colW * 0.42f, tipBox.Y + 6f * scale, colW * 0.84f, 18f * scale);
                            using (var kp = Theme.Round(kRect, 5f * scale))
                            {
                                g.FillPath(keyBg, kp);
                                g.DrawPath(keyBd, kp);
                            }
                            g.DrawString(tips[i, 0], tipKeyFont, keyBr, kRect, centerFmt);
                            g.DrawString(tips[i, 1], tipDescFont, descBr,
                                new RectangleF(tipBox.X + colW * i, tipBox.Y + 25f * scale, colW, 18f * scale), centerFmt);
                        }
                    }

                    // Bottom left hint
                    using (var hb = new SolidBrush(Theme.Muted))
                        g.DrawString("全屏模式支持拖动四边贴边悬浮球打开控制中心", tipTitleFont, hb,
                            new RectangleF(padX, Height - 44f * scale, progW - 150f * scale, 22f * scale), leftFmt);
                }
            }
        }

        /// <summary>顶部瞬时状态轻提示条（锁鼠提示 / 释放鼠标 / 截屏成功等，3.2s 后自动淡出，不抢焦点、点击穿透）。</summary>
        private sealed class StreamHintToast : Form
        {
            private const int WsExTransparent = 0x00000020;
            private const int WsExToolWindow = 0x00000080;
            private const int WsExNoActivate = 0x08000000;
            private const int WsExTopMost = 0x00000008;

            private readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 33 };
            private DateTime shownUtc;
            private string text = "";

            internal StreamHintToast()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.OverlaySurface;
                DoubleBuffered = true;
                fadeTimer.Tick += delegate { TickFade(); };
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

            internal void ShowHint(string msg, Rectangle hostScreenRect, Form owner)
            {
                text = msg ?? "";
                if (text.Length == 0 || IsDisposed) return;

                float s = Theme.GetDpiScale(this);
                int w, h;
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                using (var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(text, f);
                    w = Math.Min(Math.Max(220, hostScreenRect.Width - 40), (int)Math.Ceiling(sz.Width + 44 * s));
                    h = (int)Math.Round(38 * s);
                }

                Size = new Size(w, h);
                Location = new Point(
                    hostScreenRect.Left + (hostScreenRect.Width - w) / 2,
                    hostScreenRect.Top + (int)Math.Round(16 * s));

                try
                {
                    using (var p = Theme.Round(new Rectangle(0, 0, w, h), h / 2))
                    {
                        Region old = Region;
                        Region = new Region(p);
                        if (old != null) old.Dispose();
                    }
                }
                catch { }

                shownUtc = DateTime.UtcNow;
                Opacity = 0.92;
                if (!Visible)
                {
                    if (owner != null && !owner.IsDisposed && owner.IsHandleCreated) Show(owner);
                    else Show();
                }
                Invalidate();
                fadeTimer.Stop();
                fadeTimer.Start();
            }

            private void TickFade()
            {
                if (IsDisposed) { fadeTimer.Stop(); return; }
                double elapsed = (DateTime.UtcNow - shownUtc).TotalMilliseconds;
                if (elapsed < 2800.0)
                {
                    Opacity = 0.92;
                    return;
                }
                if (elapsed >= 3400.0)
                {
                    fadeTimer.Stop();
                    Hide();
                    return;
                }
                Opacity = Math.Max(0.0, 0.92 * (1.0 - (elapsed - 2800.0) / 600.0));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { fadeTimer.Stop(); fadeTimer.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Theme.OverlaySurface);
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f))
                using (var pen = new Pen(Color.FromArgb(64, Theme.OverlayMuted), 1.1f))
                    g.DrawPath(pen, p);
                using (var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (var b = new SolidBrush(Theme.OverlayInk))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(text, f, b, ClientRectangle, sf);
            }
        }

        /// <summary>轻量化、不拦截鼠标点击与焦点的性能监控浮层（覆盖于 D3D11 视口左上角）。</summary>
        private sealed class NativeHudOverlay : Form
        {
            private const int WsExTransparent = 0x00000020;
            private const int WsExToolWindow = 0x00000080;
            private const int WsExNoActivate = 0x08000000;
            private const int WsExTopMost = 0x00000008;

            private string line1 = "BoBo V6 · 全内置 D3D11VA 硬解";
            private string line2 = "等待网络指标…";
            private string line3 = "帧管线等待首帧…";
            private bool line3Warn;

            internal NativeHudOverlay()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.OverlaySurface;
                Opacity = 0.86;
                ApplyHudSize();
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

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyHudSize();
            }

            private void ApplyHudSize()
            {
                float scale = Theme.GetDpiScale(this);
                Size = new Size((int)Math.Round(492 * scale), (int)Math.Round(76 * scale));
            }

            internal static string FormatHudPipelineLine(NativeStreamStats s)
            {
                if (s == null) return "帧管线等待首帧…";
                double totalMs = s.FrameTotalMs > 0.0 ? s.FrameTotalMs : (s.QueueWaitMs + s.DecodeMs + s.RenderMs);
                var sb = new StringBuilder(96);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "帧管线 {0:F1}ms（排队 {1:F1} · 解码 {2:F1} · 渲染 {3:F1}）· 队列 {4}帧",
                    totalMs, s.QueueWaitMs, s.DecodeMs, s.RenderMs, s.QueueDepth);
                if (s.DecodeDroppedFrames > 0 || s.DecodeQueueResets > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        " · 丢帧 {0}/重置 {1}",
                        s.DecodeDroppedFrames, s.DecodeQueueResets);
                }
                if (s.WaitingForKeyframe)
                {
                    sb.Append(" · 等待关键帧");
                }
                return sb.ToString();
            }

            internal void UpdateStats(NativeStreamStats s, string modeTag)
            {
                if (s == null || IsDisposed) return;
                double mbps = s.BitrateKbps / 1000.0;
                string tr = s.Transport ?? "";
                bool isUdp = tr.IndexOf("UDP", StringComparison.OrdinalIgnoreCase) >= 0;
                line1 = string.Format(CultureInfo.InvariantCulture,
                    "{0:F1} FPS | {1}x{2} {3} | {4} | {5}",
                    s.Fps, s.Width, s.Height,
                    string.IsNullOrEmpty(s.Codec) ? "H.264" : s.Codec,
                    tr,
                    string.IsNullOrEmpty(s.Decoder) ? "D3D11VA" : s.Decoder);
                line2 = isUdp
                    ? string.Format(CultureInfo.InvariantCulture,
                        "网络 RTT {0:F0}ms(±{1:F0}) · 丢包 {2:F1}% · 纠错 F{3}/N{4} · {5:F1} Mbps",
                        s.RttMs, s.JitterMs, s.PacketLossPct, s.FecRecoveredPackets, s.NackRecoveredPackets, mbps)
                    : string.Format(CultureInfo.InvariantCulture,
                        "网络 RTT {0:F0}ms · 丢包 {1:F1}% · {2:F1} Mbps",
                        s.RttMs, s.PacketLossPct, mbps);
                line3 = FormatHudPipelineLine(s);
                line3Warn = s.WaitingForKeyframe || s.QueueDepth >= 6 || s.MaxQueueWaitMs >= 120.0;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                float scale = Theme.GetDpiScale(this);
                using (var bg = new SolidBrush(Theme.OverlaySurface))
                    g.FillRectangle(bg, ClientRectangle);
                using (var pen = new Pen(Color.FromArgb(64, Theme.OverlayMuted), 1f))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                int padX = (int)Math.Round(10 * scale);
                int line1Y = (int)Math.Round(6 * scale);
                int line2Y = (int)Math.Round(28 * scale);
                int line3Y = (int)Math.Round(50 * scale);
                int textW = Math.Max(40, Width - padX * 2);
                int lineH = (int)Math.Round(20 * scale);
                const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                Color line3Color = line3Warn ? Color.FromArgb(255, 245, 194, 107) : Theme.OverlayMuted;
                using (var f1 = new Font("Consolas", 9f, FontStyle.Bold))
                using (var f2 = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
                {
                    TextRenderer.DrawText(g, line1, f1, new Rectangle(padX, line1Y, textW, lineH), Theme.OverlayInk, flags);
                    TextRenderer.DrawText(g, line2, f2, new Rectangle(padX, line2Y, textW, lineH), Theme.OverlayMuted, flags);
                    TextRenderer.DrawText(g, line3, f2, new Rectangle(padX, line3Y, textW, lineH), line3Color, flags);
                }
            }
        }
    }
}
