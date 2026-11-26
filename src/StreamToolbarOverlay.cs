using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal enum FloatDockEdge
    {
        Left = 0,
        Right = 1,
        Top = 2,
        Bottom = 3
    }

    /// <summary>
    /// V5.1 UU远程级「可自由移动、任意贴边自动隐藏的方形小浮窗（悬浮球）」+ 自由鼠标守护：
    /// 1. 方形悬浮球（46×46 圆角浅色毛玻璃卡片 + BoBo 徽标 + 实时状态点）：左键按住可拖到屏幕任意位置，
    ///    松手自动吸附最近边缘（左/右/上/下），闲置 2 秒自动贴边半隐藏成纤细把手，鼠标轻触即刻滑出；
    /// 2. 左键单击直接弹出完整 UU 风格「控制中心」级联菜单；右键提供一键释放鼠标/切全屏/隐藏/退出远控；
    /// 3. 自由鼠标守护与全局紧急快捷键 Ctrl+Alt+Shift+B（锁鼠模式下随时释放鼠标并弹出悬浮球/控制中心）；
    /// 4. 兼容保留 ComputeBarBounds 静态方法供既有自检使用，并新增四边贴边几何计算 ComputeDockedSquareBounds。
    /// </summary>
    internal sealed class StreamToolbarOverlay : Form
    {
        private const int WsExTopMost = 0x00000008;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int CsDropShadow = 0x00020000;
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
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint { public int X, Y; }

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

        private readonly string hostDisplay;
        private readonly Func<bool> getFreeMouseActive;
        private readonly Action onToggleMouseMode;
        private readonly Action onToggleFullscreen;
        private readonly Action onDisconnect;
        private readonly Action onClosed;
        private readonly Func<StreamControlCenterModel> buildControlCenterModel;
        private readonly Action<bool> onToggleHideInFullscreen;

        private Func<bool> customSessionAlive;
        private Form ownerStreamForm;
        private readonly int selfPid = Process.GetCurrentProcess().Id;

        private int trackedPid;
        private bool freeMouseGuardEnabled = true;
        private bool hotkeyRegistered;
        private bool hiddenAtEdge;
        private bool hovered;
        private bool userHiddenBySetting;
        private bool menuOpen;

        // Movable 4-edge docking state
        private FloatDockEdge dockEdge = FloatDockEdge.Right;
        private float dockRatio = 0.28f;
        private bool dragging;
        private bool dragMoved;
        private Point dragStartCursor;
        private Point dragStartLocation;

        private UuControlCenterSession activeMenuSession;
        private readonly ContextMenuStrip quickRightMenu = new ContextMenuStrip();
        private readonly Timer edgeHideTimer = new Timer();
        private readonly Timer processWatchTimer = new Timer();
        private readonly Timer freeMouseTimer = new Timer();

        internal StreamToolbarOverlay(
            int streamPid,
            string hostDisplay,
            Func<bool> getFreeMouseActive,
            Func<StreamControlCenterModel> buildControlCenterModel,
            Action onToggleMouseMode,
            Action onToggleFullscreen,
            Action<bool> onToggleHideInFullscreen,
            Action onDisconnect,
            Action onClosed)
        {
            this.trackedPid = streamPid;
            this.hostDisplay = string.IsNullOrEmpty(hostDisplay) ? "远程桌面" : hostDisplay;
            this.getFreeMouseActive = getFreeMouseActive;
            this.buildControlCenterModel = buildControlCenterModel;
            this.onToggleMouseMode = onToggleMouseMode;
            this.onToggleFullscreen = onToggleFullscreen;
            this.onToggleHideInFullscreen = onToggleHideInFullscreen;
            this.onDisconnect = onDisconnect;
            this.onClosed = onClosed;
            this.freeMouseGuardEnabled = getFreeMouseActive == null || getFreeMouseActive();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Paper;
            AccessibleName = "控制中心";
            Opacity = 0.96;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            edgeHideTimer.Interval = 2200;
            edgeHideTimer.Tick += delegate
            {
                edgeHideTimer.Stop();
                if (!menuOpen && !dragging && !Bounds.Contains(Cursor.Position))
                {
                    SetEdgeHidden(true);
                }
            };

            processWatchTimer.Interval = 1200;
            processWatchTimer.Tick += delegate
            {
                if (IsDisposed) return;
                if (customSessionAlive != null)
                {
                    bool alive = false;
                    try { alive = customSessionAlive(); } catch { }
                    if (!alive)
                    {
                        CloseSafely();
                        return;
                    }
                    if (!userHiddenBySetting && Visible) EnsureTopMostNoActivate();
                    return;
                }
                if (trackedPid <= 0) return;
                if (!Engines.IsProcessAlive(trackedPid))
                {
                    CloseSafely();
                    return;
                }
                if (!userHiddenBySetting && Visible) EnsureTopMostNoActivate();
            };

            freeMouseTimer.Interval = 180;
            freeMouseTimer.Tick += delegate { EnforceFreeMouseIfNeeded(); };

            quickRightMenu.Font = Theme.GetFont(Theme.TextSize.Body);
            quickRightMenu.ShowImageMargin = false;
            if (!SystemInformation.HighContrast)
            {
                quickRightMenu.BackColor = Theme.Paper;
                quickRightMenu.ForeColor = Theme.Ink;
                quickRightMenu.Renderer = new ToolStripProfessionalRenderer(new QuickMenuColors()) { RoundedEdges = false };
            }
            BuildQuickRightMenu();
            ApplyDockedGeometry();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExTopMost | WsExToolWindow | WsExNoActivate;
                cp.ClassStyle |= CsDropShadow;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { hotkeyRegistered = RegisterHotKey(Handle, HotkeyShowToolbarId, ModControl | ModAlt | ModShift | ModNoRepeat, VkB); }
            catch { hotkeyRegistered = false; }
            ApplyRoundedRegion();
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
                m.Result = new IntPtr(MaNoActivate);
                return;
            }
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyShowToolbarId)
            {
                RevealAndOpenControlCenter();
                return;
            }
            base.WndProc(ref m);
        }

        internal void SetOwnerForm(Form owner)
        {
            ownerStreamForm = owner;
            if (!IsDisposed)
            {
                try
                {
                    if (owner != null && !owner.IsDisposed && owner.IsHandleCreated && Owner != owner)
                        Owner = owner;
                }
                catch { }
                ApplyDockedGeometry();
            }
        }

        internal void SetNativeSessionAliveCallback(Func<bool> isAlive)
        {
            customSessionAlive = isAlive;
        }

        internal void StartWatching()
        {
            if (IsDisposed) return;
            ApplyDockedGeometry();
            if (!Visible && !userHiddenBySetting)
            {
                if (ownerStreamForm != null && !ownerStreamForm.IsDisposed && ownerStreamForm.IsHandleCreated)
                    Show(ownerStreamForm);
                else
                    Show();
            }
            if (Visible) EnsureTopMostNoActivate();
            edgeHideTimer.Stop();
            edgeHideTimer.Start();
            processWatchTimer.Start();
            freeMouseTimer.Start();
        }

        internal void UpdateSession(int newPid)
        {
            trackedPid = newPid;
            if (IsDisposed) return;
            freeMouseGuardEnabled = getFreeMouseActive == null || getFreeMouseActive();
            ApplyDockedGeometry();
            if (!userHiddenBySetting && Visible) EnsureTopMostNoActivate();
            Invalidate();
            processWatchTimer.Start();
            freeMouseTimer.Start();
        }

        internal void PauseWatchingForRestart()
        {
            trackedPid = 0;
            processWatchTimer.Stop();
        }

        internal void SetVisibleByMode(bool shouldShow)
        {
            userHiddenBySetting = !shouldShow;
            if (IsDisposed) return;
            if (shouldShow)
            {
                ApplyDockedGeometry();
                if (!Visible)
                {
                    if (ownerStreamForm != null && !ownerStreamForm.IsDisposed && ownerStreamForm.IsHandleCreated)
                        Show(ownerStreamForm);
                    else
                        Show();
                }
                EnsureTopMostNoActivate();
                Invalidate();
            }
            else
            {
                CloseControlCenter();
                if (Visible) Hide();
            }
        }

        internal void RepositionToOwnerScreen()
        {
            if (IsDisposed || dragging) return;
            ApplyDockedGeometry();
        }

        internal void RevealFromHotkey()
        {
            if (IsDisposed) return;
            freeMouseGuardEnabled = true;
            Engines.ReleaseCursorClip();
            if (!Visible)
            {
                if (ownerStreamForm != null && !ownerStreamForm.IsDisposed && ownerStreamForm.IsHandleCreated)
                    Show(ownerStreamForm);
                else
                    Show();
            }
            SetEdgeHidden(false);
            EnsureTopMostNoActivate();
            edgeHideTimer.Stop();
            edgeHideTimer.Start();
            Invalidate();
        }

        internal void RevealAndOpenControlCenter()
        {
            RevealFromHotkey();
            OpenControlCenter();
        }

        internal void RefreshVisualState()
        {
            if (IsDisposed) return;
            freeMouseGuardEnabled = getFreeMouseActive == null || getFreeMouseActive();
            BuildQuickRightMenu();
            Invalidate();
        }

        internal void CloseControlCenter()
        {
            if (activeMenuSession != null)
            {
                UuControlCenterSession s = activeMenuSession;
                activeMenuSession = null;
                s.CloseAll();
            }
            menuOpen = false;
        }

        internal void CloseSafely()
        {
            CloseControlCenter();
            edgeHideTimer.Stop();
            processWatchTimer.Stop();
            freeMouseTimer.Stop();
            Engines.ReleaseCursorClip();
            if (!IsDisposed)
            {
                try { Close(); } catch { }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CloseControlCenter();
            edgeHideTimer.Stop();
            processWatchTimer.Stop();
            freeMouseTimer.Stop();
            Engines.ReleaseCursorClip();
            edgeHideTimer.Dispose();
            processWatchTimer.Dispose();
            freeMouseTimer.Dispose();
            quickRightMenu.Dispose();
            if (Region != null)
            {
                Region r = Region;
                Region = null;
                r.Dispose();
            }
            base.OnFormClosed(e);
            if (onClosed != null)
            {
                try { onClosed(); } catch { }
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
            if (IsDisposed || !freeMouseGuardEnabled) return;
            if (trackedPid <= 0 && ownerStreamForm == null) return;
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint fgPid;
                GetWindowThreadProcessId(fg, out fgPid);
                bool matchesTracked = (trackedPid > 0 && (int)fgPid == trackedPid)
                    || (ownerStreamForm != null && !ownerStreamForm.IsDisposed && (int)fgPid == selfPid);
                if (!matchesTracked) return;

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

        /// <summary>
        /// 计算四边贴边可移动方形小浮窗（展开方形 / 贴边隐藏纤细把手）的屏幕坐标（纯逻辑，供自检与运行共用）。
        /// </summary>
        internal static Rectangle ComputeDockedSquareBounds(Rectangle screenBounds, FloatDockEdge edge, float ratio, bool isEdgeHidden, float scale)
        {
            float s = Math.Max(1f, Math.Min(2.5f, scale));
            float clampedRatio = Math.Max(0.06f, Math.Min(0.94f, ratio));
            int sq = (int)Math.Round(46 * s);
            int margin = (int)Math.Round(10 * s);
            int tabThick = (int)Math.Round(12 * s);
            int tabLen = (int)Math.Round(46 * s);

            if (!isEdgeHidden)
            {
                int x, y;
                switch (edge)
                {
                    case FloatDockEdge.Left:
                        x = screenBounds.Left + margin;
                        y = screenBounds.Top + (int)Math.Round((screenBounds.Height - sq) * clampedRatio);
                        break;
                    case FloatDockEdge.Right:
                        x = screenBounds.Right - sq - margin;
                        y = screenBounds.Top + (int)Math.Round((screenBounds.Height - sq) * clampedRatio);
                        break;
                    case FloatDockEdge.Bottom:
                        x = screenBounds.Left + (int)Math.Round((screenBounds.Width - sq) * clampedRatio);
                        y = screenBounds.Bottom - sq - margin;
                        break;
                    case FloatDockEdge.Top:
                    default:
                        x = screenBounds.Left + (int)Math.Round((screenBounds.Width - sq) * clampedRatio);
                        y = screenBounds.Top + margin;
                        break;
                }
                x = Math.Max(screenBounds.Left + 4, Math.Min(screenBounds.Right - sq - 4, x));
                y = Math.Max(screenBounds.Top + 4, Math.Min(screenBounds.Bottom - sq - 4, y));
                return new Rectangle(x, y, sq, sq);
            }
            else
            {
                switch (edge)
                {
                    case FloatDockEdge.Left:
                    {
                        int y = screenBounds.Top + (int)Math.Round((screenBounds.Height - tabLen) * clampedRatio);
                        y = Math.Max(screenBounds.Top + 8, Math.Min(screenBounds.Bottom - tabLen - 8, y));
                        return new Rectangle(screenBounds.Left, y, tabThick, tabLen);
                    }
                    case FloatDockEdge.Right:
                    {
                        int y = screenBounds.Top + (int)Math.Round((screenBounds.Height - tabLen) * clampedRatio);
                        y = Math.Max(screenBounds.Top + 8, Math.Min(screenBounds.Bottom - tabLen - 8, y));
                        return new Rectangle(screenBounds.Right - tabThick, y, tabThick, tabLen);
                    }
                    case FloatDockEdge.Bottom:
                    {
                        int x = screenBounds.Left + (int)Math.Round((screenBounds.Width - tabLen) * clampedRatio);
                        x = Math.Max(screenBounds.Left + 8, Math.Min(screenBounds.Right - tabLen - 8, x));
                        return new Rectangle(x, screenBounds.Bottom - tabThick, tabLen, tabThick);
                    }
                    case FloatDockEdge.Top:
                    default:
                    {
                        int x = screenBounds.Left + (int)Math.Round((screenBounds.Width - tabLen) * clampedRatio);
                        x = Math.Max(screenBounds.Left + 8, Math.Min(screenBounds.Right - tabLen - 8, x));
                        return new Rectangle(x, screenBounds.Top, tabLen, tabThick);
                    }
                }
            }
        }

        /// <summary>向后兼容保留：既有自检调用的顶部条几何计算。</summary>
        internal static Rectangle ComputeBarBounds(Rectangle screenBounds, bool isExpanded, float scale)
        {
            return ComputeBarBounds(screenBounds, isExpanded, scale, -1);
        }

        internal static Rectangle ComputeBarBounds(Rectangle screenBounds, bool isExpanded, float scale, int preferredCenterX)
        {
            float s = Math.Max(1f, Math.Min(2.5f, scale));
            int w = (int)Math.Round((isExpanded ? 816 : 206) * s);
            int h = (int)Math.Round((isExpanded ? 42 : 30) * s);
            int margin = Math.Max(10, (int)Math.Round(12 * s));
            w = Math.Min(w, Math.Max(160, screenBounds.Width - margin * 2));
            int centerX = preferredCenterX > 0 ? preferredCenterX : (screenBounds.Left + screenBounds.Width / 2);
            int x = centerX - w / 2;
            int minX = screenBounds.Left + margin;
            int maxX = screenBounds.Right - w - margin;
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            int y = screenBounds.Top + (int)Math.Round((isExpanded ? 8 : 6) * s);
            return new Rectangle(x, y, w, h);
        }

        private Screen ResolveTargetScreen()
        {
            try
            {
                if (ownerStreamForm != null && !ownerStreamForm.IsDisposed && ownerStreamForm.IsHandleCreated)
                    return Screen.FromControl(ownerStreamForm);
                if (IsHandleCreated)
                    return Screen.FromControl(this);
            }
            catch { }
            return Screen.PrimaryScreen;
        }

        private void SetEdgeHidden(bool hide)
        {
            if (hiddenAtEdge == hide) return;
            hiddenAtEdge = hide;
            ApplyDockedGeometry();
            Invalidate();
        }

        private void ApplyDockedGeometry()
        {
            Screen scr = ResolveTargetScreen();
            if (scr == null) scr = Screen.PrimaryScreen;
            float scale = Theme.GetDpiScale(this);
            Rectangle r = ComputeDockedSquareBounds(scr.Bounds, dockEdge, dockRatio, hiddenAtEdge && !menuOpen && !dragging, scale);
            Bounds = r;
            Opacity = (hiddenAtEdge && !hovered && !menuOpen) ? 0.72 : 0.97;
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            if (Width <= 2 || Height <= 2) return;
            float scale = Theme.GetDpiScale(this);
            int radius = hiddenAtEdge ? (int)Math.Round(6 * scale) : (int)Math.Round(12 * scale);
            try
            {
                using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), radius))
                {
                    Region old = Region;
                    Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        private void SnapToNearestEdge(Point centerScreen)
        {
            Screen scr = Screen.FromPoint(centerScreen);
            if (scr == null) scr = Screen.PrimaryScreen;
            Rectangle b = scr.Bounds;

            int distLeft = Math.Abs(centerScreen.X - b.Left);
            int distRight = Math.Abs(b.Right - centerScreen.X);
            int distTop = Math.Abs(centerScreen.Y - b.Top);
            int distBottom = Math.Abs(b.Bottom - centerScreen.Y);

            int minDist = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));
            if (minDist == distLeft)
            {
                dockEdge = FloatDockEdge.Left;
                dockRatio = (float)(centerScreen.Y - b.Top) / Math.Max(1f, b.Height);
            }
            else if (minDist == distRight)
            {
                dockEdge = FloatDockEdge.Right;
                dockRatio = (float)(centerScreen.Y - b.Top) / Math.Max(1f, b.Height);
            }
            else if (minDist == distTop)
            {
                dockEdge = FloatDockEdge.Top;
                dockRatio = (float)(centerScreen.X - b.Left) / Math.Max(1f, b.Width);
            }
            else
            {
                dockEdge = FloatDockEdge.Bottom;
                dockRatio = (float)(centerScreen.X - b.Left) / Math.Max(1f, b.Width);
            }
            dockRatio = Math.Max(0.06f, Math.Min(0.94f, dockRatio));
            ApplyDockedGeometry();
        }

        private void OpenControlCenter()
        {
            if (IsDisposed) return;
            if (activeMenuSession != null && activeMenuSession.IsOpen)
            {
                CloseControlCenter();
                return;
            }

            SetEdgeHidden(false);
            edgeHideTimer.Stop();
            Engines.ReleaseCursorClip();

            StreamControlCenterModel model = buildControlCenterModel != null ? buildControlCenterModel() : new StreamControlCenterModel();
            menuOpen = true;
            activeMenuSession = new UuControlCenterSession(model, delegate
            {
                menuOpen = false;
                if (IsDisposed) return;
                Invalidate();
                if (!Bounds.Contains(Cursor.Position))
                {
                    edgeHideTimer.Stop();
                    edgeHideTimer.Start();
                }
            });

            Point anchor;
            switch (dockEdge)
            {
                case FloatDockEdge.Left:
                    anchor = new Point(Right + 8, Top);
                    break;
                case FloatDockEdge.Right:
                    anchor = new Point(Left - 294, Top);
                    break;
                case FloatDockEdge.Bottom:
                    anchor = new Point(Left, Top - 420);
                    break;
                case FloatDockEdge.Top:
                default:
                    anchor = new Point(Left, Bottom + 8);
                    break;
            }
            activeMenuSession.ShowAt(anchor, Bounds, ownerStreamForm ?? this);
            Invalidate();
        }

        private void BuildQuickRightMenu()
        {
            while (quickRightMenu.Items.Count > 0)
            {
                ToolStripItem old = quickRightMenu.Items[0];
                quickRightMenu.Items.RemoveAt(0);
                try { old.Dispose(); } catch { }
            }
            quickRightMenu.Items.Add("打开控制中心", null, delegate { OpenControlCenter(); });
            bool free = getFreeMouseActive == null || getFreeMouseActive();
            quickRightMenu.Items.Add(free ? "切换锁鼠游戏模式 (Ctrl+Alt+M)" : "释放鼠标 (Ctrl+Alt)", null, delegate
            {
                if (onToggleMouseMode != null) onToggleMouseMode();
            });
            quickRightMenu.Items.Add("切换全屏 / 窗口 (Ctrl+Alt+F)", null, delegate
            {
                if (onToggleFullscreen != null) onToggleFullscreen();
            });
            quickRightMenu.Items.Add("全屏状态隐藏悬浮球 (Ctrl+Alt+G)", null, delegate
            {
                if (onToggleHideInFullscreen != null) onToggleHideInFullscreen(true);
            });
            quickRightMenu.Items.Add(new ToolStripSeparator());
            var exitItem = new ToolStripMenuItem("退出远控 (Ctrl+Alt+Shift+Q)")
            {
                ForeColor = SystemInformation.HighContrast ? SystemColors.MenuText : Theme.Alert
            };
            exitItem.Click += delegate { if (onDisconnect != null) onDisconnect(); };
            quickRightMenu.Items.Add(exitItem);
        }

        private sealed class QuickMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Paper; } }
            public override Color MenuBorder { get { return Theme.StrokeStrong; } }
            public override Color MenuItemSelected { get { return Theme.SurfaceSubtle; } }
            public override Color MenuItemBorder { get { return Theme.Stroke; } }
            public override Color SeparatorDark { get { return Theme.Divider; } }
            public override Color SeparatorLight { get { return Theme.Paper; } }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            edgeHideTimer.Stop();
            Engines.ReleaseCursorClip();
            if (hiddenAtEdge)
            {
                SetEdgeHidden(false);
            }
            Opacity = 0.98;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            Invalidate();
            if (!menuOpen && !dragging)
            {
                edgeHideTimer.Stop();
                edgeHideTimer.Start();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                edgeHideTimer.Stop();
                BuildQuickRightMenu();
                quickRightMenu.Show(Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            dragging = true;
            dragMoved = false;
            dragStartCursor = Cursor.Position;
            if (hiddenAtEdge)
            {
                SetEdgeHidden(false);
            }
            dragStartLocation = Location;
            Capture = true;
            edgeHideTimer.Stop();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Point cur = Cursor.Position;
            int dx = cur.X - dragStartCursor.X;
            int dy = cur.Y - dragStartCursor.Y;
            if (!dragMoved && (Math.Abs(dx) >= 5 || Math.Abs(dy) >= 5))
            {
                dragMoved = true;
                CloseControlCenter();
            }
            if (dragMoved)
            {
                Location = new Point(dragStartLocation.X + dx, dragStartLocation.Y + dy);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (!dragging) return;

            bool moved = dragMoved;
            dragging = false;
            dragMoved = false;
            Capture = false;

            if (moved)
            {
                SnapToNearestEdge(new Point(Left + Width / 2, Top + Height / 2));
                edgeHideTimer.Stop();
                edgeHideTimer.Start();
            }
            else
            {
                OpenControlCenter();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Theme.GetDpiScale(this);
            bool free = getFreeMouseActive == null || getFreeMouseActive();
            Color statusDot = free ? Theme.Success : Theme.Warning;

            if (hiddenAtEdge && !menuOpen && !dragging)
            {
                g.Clear(Theme.SurfaceSubtle);
                Theme.Surface(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f),
                    5f * s, Theme.SurfaceSubtle, Theme.StrokeStrong);
                using (var grip = new Pen(Theme.Muted, Math.Max(1.6f, 1.6f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var dot = new SolidBrush(statusDot))
                {
                    if (dockEdge == FloatDockEdge.Left || dockEdge == FloatDockEdge.Right)
                    {
                        float cx = Width / 2f;
                        g.DrawLine(grip, cx, Height * 0.30f, cx, Height * 0.70f);
                        float d = 4.5f * s;
                        g.FillEllipse(dot, cx - d / 2f, Height * 0.14f, d, d);
                    }
                    else
                    {
                        float cy = Height / 2f;
                        g.DrawLine(grip, Width * 0.30f, cy, Width * 0.70f, cy);
                        float d = 4.5f * s;
                        g.FillEllipse(dot, Width * 0.14f, cy - d / 2f, d, d);
                    }
                }
                return;
            }

            Color cardFill = menuOpen ? Theme.AccentLight : hovered ? Theme.SurfaceSubtle : Theme.Paper;
            Color cardBorder = menuOpen ? Theme.AccentBorder : Theme.StrokeStrong;

            g.Clear(cardFill);
            Theme.Surface(g, new RectangleF(0.6f, 0.6f, Width - 1.6f, Height - 1.6f),
                11f * s, cardFill, cardBorder);

            // Centered BoBo brand logo (or crisp control-center icon fallback)
            Bitmap logo = Theme.GetBrandLogoBitmap();
            if (logo != null)
            {
                int logoSize = (int)Math.Round(28 * s);
                int lx = (Width - logoSize) / 2;
                int ly = (Height - logoSize) / 2;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var clip = Theme.Round(new Rectangle(lx, ly, logoSize, logoSize), 7f * s))
                {
                    var state = g.Save();
                    g.SetClip(clip);
                    g.DrawImage(logo, new Rectangle(lx, ly, logoSize, logoSize));
                    g.Restore(state);
                }
            }
            else
            {
                float glyphSize = 21f * s;
                Theme.DrawGlyph(g, Theme.Glyph.Sliders,
                    new RectangleF((Width - glyphSize) / 2f, (Height - glyphSize) / 2f, glyphSize, glyphSize), Theme.BodyInk);
            }

            // Tiny live connection/mouse-mode indicator dot at top-right corner
            float dotSize = 8f * s;
            float dx = Width - dotSize - 4.5f * s;
            float dy = 4.5f * s;
            using (var ring = new SolidBrush(cardFill))
            using (var dot = new SolidBrush(statusDot))
            {
                g.FillEllipse(ring, dx - 1.2f, dy - 1.2f, dotSize + 2.4f, dotSize + 2.4f);
                g.FillEllipse(dot, dx, dy, dotSize, dotSize);
            }
        }
    }
}
