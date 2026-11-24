using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    /// <summary>
    /// V5 阶段一：UU 远程级「画面顶部悬浮工具条」。
    /// 在串流会话启动后自动停靠于屏幕顶部正中，默认 3 秒后收起为极细半透明胶囊以免遮挡画面；
    /// 悬停展开后支持会话内一键切换画质档位（自动无感重连）、开关性能浮层、开关物理屏防窥壁纸、
    /// 切换全屏/窗口与一键断开。使用 WS_EX_NOACTIVATE 避免抢占远程桌面键鼠焦点。
    /// </summary>
    internal sealed class StreamToolbarOverlay : Form
    {
        private const int WsExTopMost = 0x00000008;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private readonly string hostDisplay;
        private readonly string[] qualityLabels;
        private readonly Action<int> onSwitchQuality;
        private readonly Action onToggleStats;
        private readonly Action onTogglePrivacy;
        private readonly Action onToggleWindowed;
        private readonly Action onDisconnect;
        private readonly Func<bool> getStatsActive;
        private readonly Func<bool> getPrivacyActive;
        private readonly Func<bool> getWindowedActive;
        private readonly Action onClosed;

        private int trackedPid;
        private int activeQualityIndex;
        private string currentModeTag;
        private bool expanded = true;
        private bool pinned;
        private bool menuOpen;
        private int hoveredIndex = -1;

        private readonly Timer collapseTimer = new Timer();
        private readonly Timer processWatchTimer = new Timer();
        private readonly ContextMenuStrip qualityMenu = new ContextMenuStrip();
        private readonly Font qualityMenuFont = new Font("Microsoft YaHei UI", 9f);

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
            Action<int> onSwitchQuality,
            Action onToggleStats,
            Action onTogglePrivacy,
            Action onToggleWindowed,
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
            this.onSwitchQuality = onSwitchQuality;
            this.onToggleStats = onToggleStats;
            this.onTogglePrivacy = onTogglePrivacy;
            this.onToggleWindowed = onToggleWindowed;
            this.onDisconnect = onDisconnect;
            this.onClosed = onClosed;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(19, 28, 43);
            Opacity = 0.94;
            Cursor = Cursors.Hand;

            qualityMenu.ShowImageMargin = false;
            qualityMenu.Renderer = new ToolStripProfessionalRenderer();
            qualityMenu.Font = qualityMenuFont;
            qualityMenu.Opening += delegate { menuOpen = true; collapseTimer.Stop(); };
            qualityMenu.Closed += delegate
            {
                menuOpen = false;
                if (!pinned && !IsDisposed) collapseTimer.Start();
            };
            BuildQualityMenu();

            collapseTimer.Interval = 3400;
            collapseTimer.Tick += delegate
            {
                collapseTimer.Stop();
                if (!pinned && !menuOpen && !IsCursorInside())
                {
                    SetExpanded(false);
                }
            };

            processWatchTimer.Interval = 900;
            processWatchTimer.Tick += delegate
            {
                if (IsDisposed) return;
                if (trackedPid > 0 && !Engines.IsProcessAlive(trackedPid))
                {
                    CloseSafely();
                }
            };

            ApplyGeometry();
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseActivate)
            {
                m.Result = (IntPtr)MaNoActivate;
                return;
            }
            base.WndProc(ref m);
        }

        internal void StartWatching()
        {
            ApplyGeometry();
            Show();
            collapseTimer.Start();
            processWatchTimer.Start();
        }

        internal void PauseWatchingForRestart()
        {
            trackedPid = 0;
        }

        internal void UpdateSession(int newPid, string modeTag, int qualityIndex)
        {
            trackedPid = newPid;
            currentModeTag = string.IsNullOrEmpty(modeTag) ? currentModeTag : modeTag;
            activeQualityIndex = qualityIndex;
            BuildQualityMenu();
            ApplyGeometry();
            Invalidate();
        }

        internal void RefreshVisualState()
        {
            if (IsDisposed) return;
            ApplyGeometry();
            Invalidate();
        }

        internal void CloseSafely()
        {
            collapseTimer.Stop();
            processWatchTimer.Stop();
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
            collapseTimer.Dispose();
            processWatchTimer.Dispose();
            qualityMenu.Dispose();
            qualityMenuFont.Dispose();
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

        internal static Rectangle ComputeBarBounds(Rectangle screenBounds, bool isExpanded, float scale)
        {
            float s = Math.Max(1f, Math.Min(2.5f, scale));
            int w = (int)Math.Round((isExpanded ? 652 : 96) * s);
            int h = (int)Math.Round((isExpanded ? 42 : 16) * s);
            w = Math.Min(w, Math.Max(120, screenBounds.Width - 24));
            int x = screenBounds.Left + (screenBounds.Width - w) / 2;
            int y = screenBounds.Top + (isExpanded ? (int)Math.Round(8 * s) : 0);
            return new Rectangle(x, y, w, h);
        }

        private float CurrentScale()
        {
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    return Math.Max(1f, g.DpiX / 96f);
                }
            }
            catch
            {
                return 1f;
            }
        }

        private void SetExpanded(bool value)
        {
            if (expanded == value) return;
            expanded = value;
            hoveredIndex = -1;
            ApplyGeometry();
            Invalidate();
        }

        private void ApplyGeometry()
        {
            Screen scr = Screen.PrimaryScreen;
            float scale = CurrentScale();
            Rectangle bounds = ComputeBarBounds(scr.Bounds, expanded, scale);
            Bounds = bounds;
            Opacity = expanded ? 0.95 : 0.78;
            UpdateWindowRegion(bounds.Width, bounds.Height, expanded ? (int)Math.Round(12 * scale) : (int)Math.Round(8 * scale));
            LayoutButtons(bounds.Width, bounds.Height, scale);
        }

        private void UpdateWindowRegion(int w, int h, int radius)
        {
            using (GraphicsPath path = CreatePillPath(new Rectangle(0, 0, w, h), radius, !expanded))
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
                buttons = new BarButton[0];
                return;
            }

            bool statsOn = getStatsActive != null && getStatsActive();
            bool privacyOn = getPrivacyActive != null && getPrivacyActive();
            bool windowedOn = getWindowedActive != null && getWindowedActive();

            int pad = (int)Math.Round(7 * scale);
            int gap = (int)Math.Round(6 * scale);
            int btnH = h - pad * 2;

            var list = new[]
            {
                new BarButton { Id = "quality", Label = "画质 ▾", Active = false, Danger = false },
                new BarButton { Id = "stats", Label = statsOn ? "统计 开" : "统计", Active = statsOn, Danger = false },
                new BarButton { Id = "privacy", Label = privacyOn ? "防窥 开" : "防窥", Active = privacyOn, Danger = false },
                new BarButton { Id = "windowed", Label = windowedOn ? "窗口" : "全屏", Active = false, Danger = false },
                new BarButton { Id = "pin", Label = pinned ? "已固定" : "固定", Active = pinned, Danger = false },
                new BarButton { Id = "disconnect", Label = "断开", Active = false, Danger = true }
            };

            int[] widths = new[]
            {
                (int)Math.Round(68 * scale),
                (int)Math.Round(64 * scale),
                (int)Math.Round(64 * scale),
                (int)Math.Round(54 * scale),
                (int)Math.Round(58 * scale),
                (int)Math.Round(56 * scale)
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
            while (qualityMenu.Items.Count > 0)
            {
                ToolStripItem oldItem = qualityMenu.Items[0];
                qualityMenu.Items.RemoveAt(0);
                try { oldItem.Dispose(); } catch { }
            }

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

        private bool IsCursorInside()
        {
            return Bounds.Contains(Cursor.Position);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            collapseTimer.Stop();
            if (!expanded) SetExpanded(true);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoveredIndex = -1;
            Invalidate();
            if (!pinned && !menuOpen)
            {
                collapseTimer.Stop();
                collapseTimer.Start();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!expanded) return;
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

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (!expanded)
            {
                SetExpanded(true);
                return;
            }
            for (int i = 0; i < buttons.Length; i++)
            {
                if (!buttons[i].Rect.Contains(e.Location)) continue;
                string id = buttons[i].Id;
                if (id == "quality")
                {
                    Point screenPt = PointToScreen(new Point(buttons[i].Rect.Left, buttons[i].Rect.Bottom + 4));
                    qualityMenu.Show(screenPt);
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
                    onDisconnect();
                    CloseSafely();
                }
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = CurrentScale();

            if (!expanded)
            {
                using (var bg = new SolidBrush(Color.FromArgb(19, 28, 43)))
                    g.FillRectangle(bg, ClientRectangle);
                using (var dot = new SolidBrush(Color.FromArgb(51, 122, 255)))
                {
                    int d = (int)Math.Round(6 * scale);
                    g.FillEllipse(dot, (int)Math.Round(14 * scale), (Height - d) / 2, d, d);
                }
                using (var f = new Font("Microsoft YaHei UI", Math.Max(7.5f, 8f * scale), FontStyle.Bold, GraphicsUnit.Point))
                {
                    TextRenderer.DrawText(g, "BoBo ▾", f, new Rectangle((int)Math.Round(22 * scale), 0, Width - (int)Math.Round(24 * scale), Height),
                        Color.FromArgb(228, 238, 252), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                return;
            }

            using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(20, 30, 46), Color.FromArgb(14, 22, 35), 90f))
                g.FillRectangle(bg, ClientRectangle);

            using (GraphicsPath borderPath = CreatePillPath(new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(12 * scale), false))
            using (var pen = new Pen(Color.FromArgb(54, 72, 98), 1f))
                g.DrawPath(pen, borderPath);

            // Live status dot + Host & mode tag on the left
            int dotD = (int)Math.Round(8 * scale);
            int leftX = (int)Math.Round(14 * scale);
            using (var dotBr = new SolidBrush(Color.FromArgb(34, 197, 94)))
                g.FillEllipse(dotBr, leftX, (Height - dotD) / 2, dotD, dotD);

            int textLeft = leftX + dotD + (int)Math.Round(8 * scale);
            int textRight = buttons.Length > 0 ? buttons[0].Rect.Left - (int)Math.Round(8 * scale) : Width - 12;
            if (textRight > textLeft + 24)
            {
                string title = hostDisplay + "  ·  " + currentModeTag;
                using (var f = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, title, f, new Rectangle(textLeft, 0, textRight - textLeft, Height),
                        Color.FromArgb(236, 243, 252), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }

            using (var btnFont = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular))
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    BarButton b = buttons[i];
                    bool hover = (i == hoveredIndex);
                    Color fill;
                    Color fg;
                    if (b.Danger)
                    {
                        fill = hover ? Color.FromArgb(220, 53, 69) : Color.FromArgb(185, 42, 56);
                        fg = Color.White;
                    }
                    else if (b.Active)
                    {
                        fill = hover ? Color.FromArgb(65, 134, 255) : Color.FromArgb(51, 122, 255);
                        fg = Color.White;
                    }
                    else
                    {
                        fill = hover ? Color.FromArgb(46, 61, 84) : Color.FromArgb(31, 43, 62);
                        fg = Color.FromArgb(224, 234, 248);
                    }

                    using (GraphicsPath bp = CreatePillPath(b.Rect, (int)Math.Round(7 * scale), false))
                    using (var br = new SolidBrush(fill))
                    {
                        g.FillPath(br, bp);
                    }
                    TextRenderer.DrawText(g, b.Label, btnFont, b.Rect, fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }

        private static GraphicsPath CreatePillPath(Rectangle r, int radius, bool flatTop)
        {
            int d = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            if (flatTop)
            {
                path.AddLine(r.Left, r.Top, r.Right, r.Top);
                path.AddLine(r.Right, r.Top, r.Right, r.Bottom - radius);
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
