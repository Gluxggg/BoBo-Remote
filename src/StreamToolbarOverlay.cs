using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    /// <summary>
    /// V5 UU 远程级「画面顶部悬浮小浮窗 + 自由鼠标守护」：
    /// 1. 自由鼠标守护：桌面远控默认自动解除 Moonlight 的 ClipCursor 窗口禁锢，光标可自由滑出窗口边框、
    ///    自由滑入黑边区域触碰悬浮条或切回本机任务栏/副屏；玩 3D FPS 时可一键切为「锁鼠模式」。
    /// 2. 收起态小浮窗胶囊：常驻显示状态点与醒目红色「退出」微按钮，哪怕收起态也能 1 次点击退出控制；
    ///    悬停或点击左侧展开完整控制栏，按住拖动可自由停靠顶部任意位置。
    /// 3. 展开态控制中心：画质档位、自由鼠标/锁鼠、UU 风格「操作」快捷组合键菜单、窗口/无边框全屏、
    ///    性能浮层、本机防窥、固定/自动收起与一键「退出控制」。
    /// 4. 全局紧急快捷键 Ctrl+Alt+Shift+B：随时强制解除鼠标锁定并展开置顶浮窗。
    /// </summary>
    internal sealed class StreamToolbarOverlay : Form
    {
        private const int WsExTopMost = 0x00000008;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const int WmHotkey = 0x0312;
        private const int HotkeyShowToolbarId = 0x51B2;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkB = 0x42;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private const uint CursorShowing = 0x00000001;
        private const uint KeyEventFKeyUp = 0x0002;
        private const byte VkControl = 0x11;
        private const byte VkShift = 0x10;
        private const byte VkMenu = 0x12;
        private const byte VkLWin = 0x5B;
        private const byte VkEscape = 0x1B;
        private const byte VkTab = 0x09;
        private const byte VkD = 0x44;
        private const byte VkM = 0x4D;
        private const byte VkV = 0x56;
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int CbSize;
            public uint Flags;
            public IntPtr HCursor;
            public WinPoint ScreenPos;
        }

        [DllImport("user32.dll")]
        private static extern bool GetClipCursor(out WinRect lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CursorInfo pci);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private readonly string hostDisplay;
        private readonly string[] qualityLabels;
        private readonly Action<int> onSwitchQuality;
        private readonly Action onToggleStats;
        private readonly Action onTogglePrivacy;
        private readonly Action onToggleWindowed;
        private readonly Action onToggleMouseMode;
        private readonly Action onRestoreMainWindow;
        private readonly Action onDisconnect;
        private readonly Func<bool> getStatsActive;
        private readonly Func<bool> getPrivacyActive;
        private readonly Func<bool> getWindowedActive;
        private readonly Func<bool> getFreeMouseActive;
        private readonly Action onClosed;

        private int trackedPid;
        private int activeQualityIndex;
        private string currentModeTag;
        private bool expanded = true;
        private bool pinned;
        private bool menuOpen;
        private int hoveredIndex = -1;
        private bool freeMouseGuardEnabled = true;
        private bool hotkeyRegistered;

        // Horizontal drag positioning along top edge
        private int customCenterX = -1;
        private bool dragging;
        private bool dragMoved;
        private Point dragStartScreen;
        private int dragStartCenterX;

        private readonly Timer collapseTimer = new Timer();
        private readonly Timer processWatchTimer = new Timer();
        private readonly Timer freeMouseTimer = new Timer();
        private readonly ContextMenuStrip qualityMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip actionsMenu = new ContextMenuStrip();
        private readonly Font popupMenuFont = new Font("Microsoft YaHei UI", 9f);

        private struct BarButton
        {
            internal string Id;
            internal string Label;
            internal Rectangle Rect;
            internal bool Active;
            internal bool Danger;
        }

        private BarButton[] buttons = new BarButton[0];

        internal StreamToolbarOverlay(
            int streamPid,
            string hostDisplay,
            string modeTag,
            string[] qualityLabels,
            int activeQualityIndex,
            Func<bool> getStatsActive,
            Func<bool> getPrivacyActive,
            Func<bool> getWindowedActive,
            Func<bool> getFreeMouseActive,
            Action<int> onSwitchQuality,
            Action onToggleStats,
            Action onTogglePrivacy,
            Action onToggleWindowed,
            Action onToggleMouseMode,
            Action onRestoreMainWindow,
            Action onDisconnect,
            Action onClosed)
        {
            this.trackedPid = streamPid;
            this.hostDisplay = string.IsNullOrEmpty(hostDisplay) ? "远程桌面" : hostDisplay;
            this.currentModeTag = string.IsNullOrEmpty(modeTag) ? "串流中" : modeTag;
            this.qualityLabels = qualityLabels ?? new string[0];
            this.activeQualityIndex = activeQualityIndex;
            this.getStatsActive = getStatsActive;
            this.getPrivacyActive = getPrivacyActive;
            this.getWindowedActive = getWindowedActive;
            this.getFreeMouseActive = getFreeMouseActive;
            this.onSwitchQuality = onSwitchQuality;
            this.onToggleStats = onToggleStats;
            this.onTogglePrivacy = onTogglePrivacy;
            this.onToggleWindowed = onToggleWindowed;
            this.onToggleMouseMode = onToggleMouseMode;
            this.onRestoreMainWindow = onRestoreMainWindow;
            this.onDisconnect = onDisconnect;
            this.onClosed = onClosed;
            this.freeMouseGuardEnabled = getFreeMouseActive == null || getFreeMouseActive();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 27, 42);
            Opacity = 0.95;
            Cursor = Cursors.Hand;

            ConfigureMenu(qualityMenu);
            ConfigureMenu(actionsMenu);
            BuildQualityMenu();
            BuildActionsMenu();

            collapseTimer.Interval = 4200;
            collapseTimer.Tick += delegate
            {
                collapseTimer.Stop();
                if (!pinned && !menuOpen && !dragging && !IsCursorInside())
                {
                    SetExpanded(false);
                }
            };

            processWatchTimer.Interval = 850;
            processWatchTimer.Tick += delegate
            {
                if (IsDisposed) return;
                if (trackedPid > 0 && !Engines.IsProcessAlive(trackedPid))
                {
                    Engines.ReleaseCursorClip();
                    CloseSafely();
                    return;
                }
                if (!menuOpen && Visible)
                {
                    EnsureTopMostNoActivate();
                }
            };

            // 每 45ms 检查一次：桌面自由鼠标模式下若 Moonlight 启用了 ClipCursor 禁锢，立即释放，
            // 让光标可自由滑出窗口边界、触碰顶部小浮窗、或切回本机任务栏/副屏。
            freeMouseTimer.Interval = 45;
            freeMouseTimer.Tick += delegate { EnforceFreeMouseIfNeeded(); };

            ApplyGeometry();
        }

        private void ConfigureMenu(ContextMenuStrip menu)
        {
            menu.ShowImageMargin = false;
            menu.Renderer = new ToolStripProfessionalRenderer();
            menu.Font = popupMenuFont;
            menu.Opening += delegate
            {
                menuOpen = true;
                collapseTimer.Stop();
                Engines.ReleaseCursorClip();
            };
            menu.Closed += delegate
            {
                menuOpen = false;
                if (!pinned && !IsDisposed) collapseTimer.Start();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExTopMost | WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyShowToolbarId,
                    ModControl | ModAlt | ModShift | ModNoRepeat, VkB);
            }
            catch { hotkeyRegistered = false; }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered && IsHandleCreated)
            {
                try { UnregisterHotKey(Handle, HotkeyShowToolbarId); } catch { }
                hotkeyRegistered = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseActivate)
            {
                m.Result = (IntPtr)MaNoActivate;
                return;
            }
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyShowToolbarId)
            {
                ReleaseMouseAndRevealToolbar();
                return;
            }
            base.WndProc(ref m);
        }

        internal void StartWatching()
        {
            ApplyGeometry();
            Show();
            EnsureTopMostNoActivate();
            collapseTimer.Start();
            processWatchTimer.Start();
            freeMouseTimer.Start();
        }

        internal void PauseWatchingForRestart()
        {
            trackedPid = 0;
            Engines.ReleaseCursorClip();
        }

        internal void UpdateSession(int newPid, string modeTag, int qualityIndex)
        {
            trackedPid = newPid;
            currentModeTag = string.IsNullOrEmpty(modeTag) ? currentModeTag : modeTag;
            activeQualityIndex = qualityIndex;
            freeMouseGuardEnabled = getFreeMouseActive == null || getFreeMouseActive();
            BuildQualityMenu();
            BuildActionsMenu();
            ApplyGeometry();
            EnsureTopMostNoActivate();
            Invalidate();
        }

        internal void RefreshVisualState()
        {
            if (IsDisposed) return;
            freeMouseGuardEnabled = getFreeMouseActive == null || getFreeMouseActive();
            BuildActionsMenu();
            ApplyGeometry();
            Invalidate();
        }

        internal void CloseSafely()
        {
            collapseTimer.Stop();
            processWatchTimer.Stop();
            freeMouseTimer.Stop();
            Engines.ReleaseCursorClip();
            if (!IsDisposed)
            {
                try { Close(); }
                catch { }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            collapseTimer.Stop();
            processWatchTimer.Stop();
            freeMouseTimer.Stop();
            Engines.ReleaseCursorClip();
            collapseTimer.Dispose();
            processWatchTimer.Dispose();
            freeMouseTimer.Dispose();
            qualityMenu.Dispose();
            actionsMenu.Dispose();
            popupMenuFont.Dispose();
            if (Region != null)
            {
                Region r = Region;
                Region = null;
                r.Dispose();
            }
            base.OnFormClosed(e);
            if (onClosed != null)
            {
                try { onClosed(); }
                catch { }
            }
        }

        private void EnsureTopMostNoActivate()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate); }
            catch { }
        }

        private void EnforceFreeMouseIfNeeded()
        {
            if (IsDisposed || !freeMouseGuardEnabled || trackedPid <= 0) return;
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint fgPid;
                GetWindowThreadProcessId(fg, out fgPid);
                if ((int)fgPid != trackedPid) return;

                // 仅在桌面可见指针模式下解除窗口禁锢，不影响 3D FPS 隐藏指针的相对视角
                CursorInfo ci = new CursorInfo { CbSize = Marshal.SizeOf(typeof(CursorInfo)) };
                if (GetCursorInfo(ref ci) && (ci.Flags & CursorShowing) == 0)
                    return;

                WinRect clip;
                if (!GetClipCursor(out clip)) return;
                Rectangle vs = SystemInformation.VirtualScreen;
                int clipW = clip.Right - clip.Left;
                int clipH = clip.Bottom - clip.Top;
                if (clipW > 0 && clipH > 0 && (clipW < vs.Width - 4 || clipH < vs.Height - 4))
                {
                    ClipCursor(IntPtr.Zero);
                }
            }
            catch { }
        }

        private void ReleaseMouseAndRevealToolbar()
        {
            freeMouseGuardEnabled = true;
            Engines.ReleaseCursorClip();
            SetExpanded(true);
            EnsureTopMostNoActivate();
            collapseTimer.Stop();
            if (!pinned) collapseTimer.Start();
            Invalidate();
        }

        internal static Rectangle ComputeBarBounds(Rectangle screenBounds, bool isExpanded, float scale)
        {
            return ComputeBarBounds(screenBounds, isExpanded, scale, -1);
        }

        internal static Rectangle ComputeBarBounds(Rectangle screenBounds, bool isExpanded, float scale, int preferredCenterX)
        {
            float s = Math.Max(1f, Math.Min(2.5f, scale));
            int w = (int)Math.Round((isExpanded ? 816 : 206) * s);
            int h = (int)Math.Round((isExpanded ? 44 : 30) * s);
            w = Math.Min(w, Math.Max(140, screenBounds.Width - 20));
            int centerX = preferredCenterX >= 0 ? preferredCenterX : (screenBounds.Left + screenBounds.Width / 2);
            int x = centerX - w / 2;
            int minX = screenBounds.Left + 10;
            int maxX = Math.Max(minX, screenBounds.Right - w - 10);
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            int y = screenBounds.Top + (int)Math.Round((isExpanded ? 8 : 6) * s);
            return new Rectangle(x, y, w, h);
        }

        private float CurrentScale()
        {
            return Theme.GetDpiScale(this);
        }

        private void SetExpanded(bool value)
        {
            if (expanded == value) return;
            expanded = value;
            hoveredIndex = -1;
            ApplyGeometry();
            EnsureTopMostNoActivate();
            Invalidate();
        }

        private void ApplyGeometry()
        {
            Screen scr = Screen.FromPoint(customCenterX >= 0
                ? new Point(customCenterX, Screen.PrimaryScreen.Bounds.Top + 12)
                : new Point(Screen.PrimaryScreen.Bounds.Left + Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Top + 12));
            float scale = CurrentScale();
            Rectangle bounds = ComputeBarBounds(scr.Bounds, expanded, scale, customCenterX);
            Bounds = bounds;
            Opacity = expanded ? 0.96 : 0.88;
            UpdateWindowRegion(bounds.Width, bounds.Height, expanded ? (int)Math.Round(12 * scale) : (int)Math.Round(15 * scale));
            LayoutButtons(bounds.Width, bounds.Height, scale);
        }

        private void UpdateWindowRegion(int w, int h, int radius)
        {
            using (GraphicsPath path = CreatePillPath(new Rectangle(0, 0, w, h), radius, false))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private void LayoutButtons(int w, int h, float scale)
        {
            if (!expanded)
            {
                int exitW = (int)Math.Round(58 * scale);
                int padC = (int)Math.Round(4 * scale);
                int btnHC = Math.Max(18, h - padC * 2);
                int exitX = w - exitW - (int)Math.Round(6 * scale);
                buttons = new[]
                {
                    new BarButton
                    {
                        Id = "expand",
                        Label = "BoBo 远控 ▾",
                        Rect = new Rectangle((int)Math.Round(4 * scale), padC, Math.Max(48, exitX - (int)Math.Round(10 * scale)), btnHC),
                        Active = false,
                        Danger = false
                    },
                    new BarButton
                    {
                        Id = "disconnect",
                        Label = "退出",
                        Rect = new Rectangle(exitX, padC, exitW, btnHC),
                        Active = false,
                        Danger = true
                    }
                };
                return;
            }

            bool statsOn = getStatsActive != null && getStatsActive();
            bool privacyOn = getPrivacyActive != null && getPrivacyActive();
            bool windowedOn = getWindowedActive != null && getWindowedActive();
            bool freeMouseOn = freeMouseGuardEnabled;

            int pad = (int)Math.Round(7 * scale);
            int gap = (int)Math.Round(5 * scale);
            int btnH = h - pad * 2;

            var list = new[]
            {
                new BarButton { Id = "quality", Label = "画质 ▾", Active = false, Danger = false },
                new BarButton { Id = "mouse", Label = freeMouseOn ? "自由鼠标" : "锁鼠模式", Active = freeMouseOn, Danger = false },
                new BarButton { Id = "actions", Label = "操作 ▾", Active = false, Danger = false },
                new BarButton { Id = "windowed", Label = windowedOn ? "窗口" : "无边全屏", Active = windowedOn, Danger = false },
                new BarButton { Id = "stats", Label = statsOn ? "性能 开" : "性能", Active = statsOn, Danger = false },
                new BarButton { Id = "privacy", Label = privacyOn ? "本机防窥 开" : "本机防窥", Active = privacyOn, Danger = false },
                new BarButton { Id = "pin", Label = pinned ? "已固定" : "固定", Active = pinned, Danger = false },
                new BarButton { Id = "disconnect", Label = "退出控制", Active = false, Danger = true }
            };

            int[] widths = new[]
            {
                (int)Math.Round(64 * scale),
                (int)Math.Round(72 * scale),
                (int)Math.Round(62 * scale),
                (int)Math.Round(68 * scale),
                (int)Math.Round(58 * scale),
                (int)Math.Round(74 * scale),
                (int)Math.Round(54 * scale),
                (int)Math.Round(74 * scale)
            };

            int rightX = w - (int)Math.Round(10 * scale);
            for (int i = list.Length - 1; i >= 0; i--)
            {
                int bw = widths[i];
                rightX -= bw;
                list[i].Rect = new Rectangle(rightX, pad, bw, btnH);
                rightX -= gap;
            }
            buttons = list;
        }

        private void BuildQualityMenu()
        {
            ClearMenu(qualityMenu);
            for (int i = 0; i < qualityLabels.Length; i++)
            {
                int idx = i;
                string prefix = (i == activeQualityIndex) ? "●  " : "    ";
                var item = new ToolStripMenuItem(prefix + qualityLabels[i]);
                item.Click += delegate
                {
                    if (onSwitchQuality != null) onSwitchQuality(idx);
                };
                qualityMenu.Items.Add(item);
            }
        }

        private void BuildActionsMenu()
        {
            ClearMenu(actionsMenu);

            AddActionMenuItem("释放鼠标锁定（允许自由移出窗口）", delegate
            {
                freeMouseGuardEnabled = true;
                Engines.ReleaseCursorClip();
                if (getFreeMouseActive != null && !getFreeMouseActive() && onToggleMouseMode != null)
                    onToggleMouseMode();
                RefreshVisualState();
            });
            actionsMenu.Items.Add(new ToolStripSeparator());

            AddActionMenuItem("发送组合键：任务管理器 (Ctrl+Shift+Esc)", delegate
            {
                SendRemoteCombo(VkControl, VkShift, VkEscape);
            });
            AddActionMenuItem("发送组合键：显示桌面 (Win+D)", delegate
            {
                SendRemoteCombo(VkLWin, VkD);
            });
            AddActionMenuItem("发送组合键：切换窗口 (Alt+Tab)", delegate
            {
                SendRemoteCombo(VkMenu, VkTab);
            });
            AddActionMenuItem("发送剪贴板粘贴 (Ctrl+Alt+Shift+V)", delegate
            {
                SendRemoteCombo(VkControl, VkMenu, VkShift, VkV);
            });
            AddActionMenuItem("切换串流鼠标模式 (Ctrl+Alt+Shift+M)", delegate
            {
                SendRemoteCombo(VkControl, VkMenu, VkShift, VkM);
            });
            actionsMenu.Items.Add(new ToolStripSeparator());

            AddActionMenuItem("返回 BoBo远程 主界面", delegate
            {
                Engines.ReleaseCursorClip();
                if (onRestoreMainWindow != null) onRestoreMainWindow();
            });
            actionsMenu.Items.Add(new ToolStripSeparator());

            var hint = new ToolStripMenuItem("快捷键：Ctrl+Alt+Shift+B 唤出浮窗 · Q 立即断开") { Enabled = false };
            actionsMenu.Items.Add(hint);
        }

        private void AddActionMenuItem(string text, EventHandler click)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += click;
            actionsMenu.Items.Add(item);
        }

        private static void ClearMenu(ContextMenuStrip menu)
        {
            while (menu.Items.Count > 0)
            {
                ToolStripItem oldItem = menu.Items[0];
                menu.Items.RemoveAt(0);
                try { oldItem.Dispose(); } catch { }
            }
        }

        private void SendRemoteCombo(params byte[] vks)
        {
            if (vks == null || vks.Length == 0) return;
            try
            {
                if (trackedPid > 0) Engines.TryFocusProcessWindow(trackedPid);
                for (int i = 0; i < vks.Length; i++)
                    keybd_event(vks[i], 0, 0, UIntPtr.Zero);
                for (int i = vks.Length - 1; i >= 0; i--)
                    keybd_event(vks[i], 0, KeyEventFKeyUp, UIntPtr.Zero);
            }
            catch { }
        }

        private bool IsCursorInside()
        {
            return Bounds.Contains(Cursor.Position);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            collapseTimer.Stop();
            Engines.ReleaseCursorClip();
            if (!expanded)
            {
                // 若鼠标正好落在收起态的「退出」按钮上，不立刻跳开尺寸，方便直接点退出
                Point local = PointToClient(Cursor.Position);
                if (buttons.Length >= 2 && buttons[1].Rect.Contains(local))
                {
                    hoveredIndex = 1;
                    Invalidate();
                    return;
                }
                SetExpanded(true);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoveredIndex = -1;
            Invalidate();
            if (!pinned && !menuOpen && !dragging)
            {
                collapseTimer.Stop();
                collapseTimer.Start();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            // 若未点在功能按钮上（或点在展开态左侧信息区），允许水平拖动小浮窗停靠位置
            bool onActionButton = false;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].Rect.Contains(e.Location) && buttons[i].Id != "expand")
                {
                    onActionButton = true;
                    break;
                }
            }
            if (!onActionButton)
            {
                dragging = true;
                dragMoved = false;
                dragStartScreen = Cursor.Position;
                dragStartCenterX = Left + Width / 2;
                Capture = true;
                collapseTimer.Stop();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point cur = Cursor.Position;
                int dx = cur.X - dragStartScreen.X;
                if (Math.Abs(dx) >= 4)
                {
                    dragMoved = true;
                    customCenterX = dragStartCenterX + dx;
                    ApplyGeometry();
                    Invalidate();
                }
                return;
            }

            int found = -1;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].Rect.Contains(e.Location))
                {
                    found = i;
                    break;
                }
            }
            if (found != hoveredIndex)
            {
                hoveredIndex = found;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                Capture = false;
                if (dragMoved)
                {
                    if (!pinned && !menuOpen) collapseTimer.Start();
                    return;
                }
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (dragMoved)
            {
                dragMoved = false;
                return;
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                if (!buttons[i].Rect.Contains(e.Location)) continue;
                string id = buttons[i].Id;
                if (id == "expand")
                {
                    SetExpanded(true);
                }
                else if (id == "quality")
                {
                    Point screenPt = PointToScreen(new Point(buttons[i].Rect.Left, buttons[i].Rect.Bottom + 4));
                    qualityMenu.Show(screenPt);
                }
                else if (id == "mouse")
                {
                    freeMouseGuardEnabled = !freeMouseGuardEnabled;
                    if (freeMouseGuardEnabled) Engines.ReleaseCursorClip();
                    if (onToggleMouseMode != null) onToggleMouseMode();
                    RefreshVisualState();
                }
                else if (id == "actions")
                {
                    BuildActionsMenu();
                    Point screenPt = PointToScreen(new Point(buttons[i].Rect.Left, buttons[i].Rect.Bottom + 4));
                    actionsMenu.Show(screenPt);
                }
                else if (id == "stats" && onToggleStats != null)
                {
                    onToggleStats();
                    RefreshVisualState();
                }
                else if (id == "privacy" && onTogglePrivacy != null)
                {
                    onTogglePrivacy();
                    RefreshVisualState();
                }
                else if (id == "windowed" && onToggleWindowed != null)
                {
                    onToggleWindowed();
                    RefreshVisualState();
                }
                else if (id == "pin")
                {
                    pinned = !pinned;
                    if (pinned) collapseTimer.Stop();
                    else collapseTimer.Start();
                    RefreshVisualState();
                }
                else if (id == "disconnect" && onDisconnect != null)
                {
                    Engines.ReleaseCursorClip();
                    onDisconnect();
                    CloseSafely();
                }
                return;
            }

            if (!expanded) SetExpanded(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = CurrentScale();

            if (!expanded)
            {
                using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(24, 35, 54), Color.FromArgb(16, 24, 38), 90f))
                    g.FillRectangle(bg, ClientRectangle);
                using (GraphicsPath borderPath = CreatePillPath(new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(15 * scale), false))
                using (var pen = new Pen(Color.FromArgb(68, 96, 134), 1f))
                    g.DrawPath(pen, borderPath);

                // Status dot
                int dotD = (int)Math.Round(7 * scale);
                int dotX = (int)Math.Round(12 * scale);
                using (var dot = new SolidBrush(Color.FromArgb(34, 197, 94)))
                    g.FillEllipse(dot, dotX, (Height - dotD) / 2, dotD, dotD);

                // Left expand label
                if (buttons.Length >= 1)
                {
                    Rectangle leftR = new Rectangle(dotX + dotD + (int)Math.Round(6 * scale), 0,
                        Math.Max(20, buttons[0].Rect.Right - (dotX + dotD + (int)Math.Round(6 * scale))), Height);
                    using (var f = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        TextRenderer.DrawText(g, "BoBo 远控 ▾", f, leftR,
                            hoveredIndex == 0 ? Color.White : Color.FromArgb(228, 238, 252),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    }
                }

                // Embedded direct red Exit micro-pill button
                if (buttons.Length >= 2)
                {
                    BarButton exitBtn = buttons[1];
                    bool hoverExit = (hoveredIndex == 1);
                    Color exitBg = hoverExit ? Color.FromArgb(232, 58, 72) : Color.FromArgb(196, 44, 58);
                    using (GraphicsPath bp = CreatePillPath(exitBtn.Rect, (int)Math.Round(10 * scale), false))
                    using (var br = new SolidBrush(exitBg))
                        g.FillPath(br, bp);
                    using (var ef = new Font("Microsoft YaHei UI", 8.1f, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        TextRenderer.DrawText(g, exitBtn.Label, ef, exitBtn.Rect, Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    }
                }
                return;
            }

            using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(22, 33, 50), Color.FromArgb(14, 22, 35), 90f))
                g.FillRectangle(bg, ClientRectangle);

            using (GraphicsPath borderPath = CreatePillPath(new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(12 * scale), false))
            using (var pen = new Pen(Color.FromArgb(62, 86, 118), 1f))
                g.DrawPath(pen, borderPath);

            // Live status dot + Host & mode tag on the left (draggable handle)
            int dotD2 = (int)Math.Round(8 * scale);
            int leftX = (int)Math.Round(14 * scale);
            using (var dotBr = new SolidBrush(Color.FromArgb(34, 197, 94)))
                g.FillEllipse(dotBr, leftX, (Height - dotD2) / 2, dotD2, dotD2);

            int textLeft = leftX + dotD2 + (int)Math.Round(8 * scale);
            int textRight = buttons.Length > 0 ? buttons[0].Rect.Left - (int)Math.Round(8 * scale) : Width - 12;
            if (textRight > textLeft + 24)
            {
                string title = hostDisplay + "  ·  " + currentModeTag;
                using (var f = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, title, f, new Rectangle(textLeft, 0, textRight - textLeft, Height),
                        Color.FromArgb(236, 243, 252), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }

            using (var btnFont = new Font("Microsoft YaHei UI", 8.1f, FontStyle.Regular))
            using (var btnBoldFont = new Font("Microsoft YaHei UI", 8.1f, FontStyle.Bold))
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    BarButton b = buttons[i];
                    bool hover = (i == hoveredIndex);
                    Color fill;
                    Color fg;
                    if (b.Danger)
                    {
                        fill = hover ? Color.FromArgb(232, 58, 72) : Color.FromArgb(196, 44, 58);
                        fg = Color.White;
                    }
                    else if (b.Active)
                    {
                        fill = hover ? Color.FromArgb(65, 134, 255) : Color.FromArgb(51, 122, 255);
                        fg = Color.White;
                    }
                    else
                    {
                        fill = hover ? Color.FromArgb(48, 65, 90) : Color.FromArgb(32, 45, 65);
                        fg = Color.FromArgb(226, 236, 250);
                    }

                    using (GraphicsPath bp = CreatePillPath(b.Rect, (int)Math.Round(7 * scale), false))
                    using (var br = new SolidBrush(fill))
                    {
                        g.FillPath(br, bp);
                    }
                    TextRenderer.DrawText(g, b.Label, b.Danger ? btnBoldFont : btnFont, b.Rect, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
            }
        }

        private static GraphicsPath CreatePillPath(Rectangle r, int radius, bool flatTop)
        {
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var path = new GraphicsPath();
            if (flatTop)
            {
                path.AddLine(r.Left, r.Top, r.Right, r.Top);
                path.AddLine(r.Right, r.Top, r.Right, r.Bottom - d / 2);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
            else
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
            return path;
        }
    }
}
