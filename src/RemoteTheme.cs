using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DualRemote
{
    internal static class RemoteTheme
    {
        internal static readonly Color ChromeBg = Color.FromArgb(238, 240, 244);
        internal static readonly Color Rail = ChromeBg;
        internal static readonly Color Workspace = Color.FromArgb(245, 246, 248);
        internal static readonly Color Paper = Color.FromArgb(252, 252, 253);
        internal static readonly Color SurfaceSubtle = Color.FromArgb(238, 241, 245);
        internal static readonly Color NavHover = Color.FromArgb(226, 232, 241);
        internal static readonly Color NavActive = Color.FromArgb(220, 230, 246);

        internal static readonly Color Ink = Color.FromArgb(29, 36, 49);
        internal static readonly Color BodyInk = Color.FromArgb(60, 74, 94);
        internal static readonly Color Muted = Color.FromArgb(76, 90, 111);
        internal static readonly Color Placeholder = Color.FromArgb(102, 115, 135);

        internal static readonly Color Stroke = Color.FromArgb(222, 227, 235);
        internal static readonly Color StrokeStrong = Color.FromArgb(194, 204, 218);
        internal static readonly Color Divider = Color.FromArgb(227, 231, 237);
        internal static readonly Color WindowBorder = StrokeStrong;

        internal static readonly Color Accent = Color.FromArgb(53, 90, 151);
        internal static readonly Color AccentHover = Color.FromArgb(46, 82, 137);
        internal static readonly Color AccentPressed = Color.FromArgb(39, 71, 121);
        internal static readonly Color AccentLight = Color.FromArgb(234, 240, 249);
        internal static readonly Color AccentBorder = Color.FromArgb(191, 207, 231);
        internal static readonly Color AccentSky = Color.FromArgb(134, 159, 198);

        internal static readonly Color DisabledBg = Color.FromArgb(230, 233, 239);
        internal static readonly Color DisabledText = Muted;

        internal static readonly Color Success = Color.FromArgb(36, 111, 74);
        internal static readonly Color SuccessLight = Color.FromArgb(233, 243, 237);
        internal static readonly Color Warning = Color.FromArgb(128, 83, 20);
        internal static readonly Color WarningLight = Color.FromArgb(250, 241, 225);
        internal static readonly Color Alert = Color.FromArgb(160, 48, 63);
        internal static readonly Color AlertLight = Color.FromArgb(249, 235, 238);
        internal static readonly Color VideoBackdrop = Color.FromArgb(19, 24, 34);
        internal static readonly Color OverlaySurface = Color.FromArgb(35, 44, 59);
        internal static readonly Color OverlayInk = Color.FromArgb(242, 245, 250);
        internal static readonly Color OverlayMuted = Color.FromArgb(189, 201, 222);

        private static readonly Font BodyFont = new Font("Microsoft YaHei UI", 9.5f);
        private static readonly Font SmallFont = new Font("Microsoft YaHei UI", 9f);
        private static readonly Font LabelFont = new Font("Microsoft YaHei UI", 9.75f, FontStyle.Regular);
        private static readonly Font LabelBoldFont = new Font("Microsoft YaHei UI", 9.75f, FontStyle.Bold);
        private static readonly Font SectionFont = new Font("Microsoft YaHei UI", 11.25f, FontStyle.Bold);
        private static readonly Font TitleFont = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold);
        private static readonly Font CodeFont = new Font("Consolas", 9.5f);
        private static readonly Font BigCodeFont = new Font("Consolas", 17.5f, FontStyle.Bold);

        internal enum TextSize { Small, Body, Label, LabelBold, Section, Title, Code, BigCode }

        internal enum ButtonStyle
        {
            Primary,
            Secondary,
            Navigation,
            NavSection,
            Quiet,
            Link,
            Caption,
            Close,
            AccentSoft,
            UnderlineTab,
            Chip,
            ToolbarAction,
            IconGhost,
            DangerPill,
            WarningSoft,
            DangerSoft
        }

        internal enum Glyph
        {
            None,
            Devices,
            AllDevices,
            Desktop,
            WindowsTile,
            Link,
            Assist,
            Star,
            Gear,
            Help,
            Refresh,
            Folder,
            Arrow,
            ChevronRight,
            ChevronDown,
            ChevronUp,
            Plus,
            Pin,
            Minus,
            Maximize,
            Restore,
            Close,
            Shield,
            Bolt,
            Copy,
            Image,
            Eye,
            EyeOff,
            Check,
            Info,
            InfoMark,
            Bell,
            Menu,
            Terminal,
            Network,
            MoreDots,
            Trash,
            Lock,
            Keypad,
            Activity,
            Sliders,
            Mouse,
            Volume,
            VolumeOff,
            Expand,
            SwitchDisplay,
            Keyboard,
            Camera,
            Mic,
            Window,
            Clipboard,
            Exit,
            Search,
            WarningMark
        }

        internal enum PillTone { Neutral, Success, Warning, Alert, Info }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint GetDpiForWindowDelegate(IntPtr hwnd);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint GetDpiForSystemDelegate();

        private const int LogPixelsX = 88;
        private static bool dpiApiProbed;
        private static GetDpiForWindowDelegate pGetDpiForWindow;
        private static GetDpiForSystemDelegate pGetDpiForSystem;
        private static float cachedSystemDpiScale;

        private static void EnsureDpiApisProbed()
        {
            if (dpiApiProbed) return;
            dpiApiProbed = true;
            try
            {
                IntPtr user32 = GetModuleHandle("user32.dll");
                if (user32 != IntPtr.Zero)
                {
                    IntPtr fnWin = GetProcAddress(user32, "GetDpiForWindow");
                    if (fnWin != IntPtr.Zero)
                        pGetDpiForWindow = (GetDpiForWindowDelegate)Marshal.GetDelegateForFunctionPointer(fnWin, typeof(GetDpiForWindowDelegate));
                    IntPtr fnSys = GetProcAddress(user32, "GetDpiForSystem");
                    if (fnSys != IntPtr.Zero)
                        pGetDpiForSystem = (GetDpiForSystemDelegate)Marshal.GetDelegateForFunctionPointer(fnSys, typeof(GetDpiForSystemDelegate));
                }
            }
            catch { }
        }

        /// <summary>
        /// 跨 Windows 10 / 11 全版本可靠获取真实系统/窗口 DPI 缩放倍率（彻底绕开 csc.exe .NET 4.0 兼容模式下 Control.DeviceDpi 恒返回 96 的缺陷）。
        /// 与 AutoScaleMode.Dpi (GetDeviceCaps LOGPIXELSX) 严格 1:1 对齐，确保静态与动态控件缩放完全一致。
        /// </summary>
        internal static float GetDpiScale(Control control = null)
        {
            EnsureDpiApisProbed();
            try
            {
                if (control != null && !control.IsDisposed && control.IsHandleCreated && pGetDpiForWindow != null)
                {
                    uint winDpi = pGetDpiForWindow(control.Handle);
                    if (winDpi >= 72 && winDpi <= 960)
                        return Math.Max(1f, winDpi / 96f);
                }
            }
            catch { }

            if (cachedSystemDpiScale >= 1f)
                return cachedSystemDpiScale;

            float resolved = 1f;
            try
            {
                IntPtr screenDc = GetDC(IntPtr.Zero);
                if (screenDc != IntPtr.Zero)
                {
                    try
                    {
                        int dpiX = GetDeviceCaps(screenDc, LogPixelsX);
                        if (dpiX >= 72 && dpiX <= 960)
                            resolved = Math.Max(1f, dpiX / 96f);
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, screenDc);
                    }
                }
            }
            catch { }

            if (resolved <= 1f && pGetDpiForSystem != null)
            {
                try
                {
                    uint sysDpi = pGetDpiForSystem();
                    if (sysDpi >= 72 && sysDpi <= 960)
                        resolved = Math.Max(1f, sysDpi / 96f);
                }
                catch { }
            }

            cachedSystemDpiScale = resolved;
            return resolved;
        }

        internal static Font GetFont(TextSize size)
        {
            switch (size)
            {
                case TextSize.Small: return SmallFont;
                case TextSize.Label: return LabelFont;
                case TextSize.LabelBold: return LabelBoldFont;
                case TextSize.Section: return SectionFont;
                case TextSize.Title: return TitleFont;
                case TextSize.Code: return CodeFont;
                case TextSize.BigCode: return BigCodeFont;
                default: return BodyFont;
            }
        }

        /// <summary>
        /// 双行自适应居中排版控件（主标题 + 副标题）：
        /// 在 OnPaint 时基于当前 Graphics 真实物理 DPI 即时度量两行文字高度并垂直居中绘制，
        /// 从根源上杜绝 Win10/Win11 任意高分屏缩放（100%~250%）下出现切底、挤字或上下偏移。
        /// </summary>
        internal sealed class TwoLineText : Control
        {
            private string titleText;
            private string subtitleText;
            private TextSize titleSize;
            private Color titleColor;
            private Color subtitleColor;

            internal TwoLineText(string title, string subtitle, TextSize titleFontKind = TextSize.LabelBold,
                Color? customTitleColor = null, Color? customSubtitleColor = null)
            {
                this.titleText = title ?? "";
                this.subtitleText = subtitle ?? "";
                this.titleSize = titleFontKind;
                this.titleColor = customTitleColor.HasValue ? customTitleColor.Value : Ink;
                this.subtitleColor = customSubtitleColor.HasValue ? customSubtitleColor.Value : Muted;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                TabStop = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Paper;
            }

            internal string TitleText
            {
                get { return titleText; }
                set { titleText = value ?? ""; Invalidate(); }
            }

            internal string SubtitleText
            {
                get { return subtitleText; }
                set { subtitleText = value ?? ""; Invalidate(); }
            }

            internal void SetTitleColor(Color color)
            {
                titleColor = color;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Color bg = ResolveSurface(this, Paper);
                g.Clear(bg);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                float scale = Math.Max(1f, g.DpiX / 96f);
                Font fTitle = GetFont(titleSize);
                Font fSub = SmallFont;
                int availW = Math.Max(16, Width - 2);

                TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                TextFormatFlags drawFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

                Size szTitle = TextRenderer.MeasureText(g, string.IsNullOrEmpty(titleText) ? "Ag" : titleText, fTitle, new Size(int.MaxValue, Height), measureFlags);
                bool hasSub = !string.IsNullOrEmpty(subtitleText);
                if (!hasSub)
                {
                    int h1 = szTitle.Height + (int)Math.Ceiling(4f * scale);
                    var r1 = new Rectangle(0, (Height - h1) / 2, availW, h1);
                    TextRenderer.DrawText(g, titleText, fTitle, r1, titleColor, drawFlags);
                    return;
                }

                Size szSub = TextRenderer.MeasureText(g, subtitleText, fSub, new Size(int.MaxValue, Height), measureFlags);
                int row1H = szTitle.Height + (int)Math.Ceiling(3f * scale);
                int row2H = szSub.Height + (int)Math.Ceiling(3f * scale);
                int gap = Math.Max(1, (int)Math.Round(2f * scale));
                int totalH = row1H + gap + row2H;
                int startY = Math.Max(0, (Height - totalH) / 2);

                var rectTitle = new Rectangle(0, startY, availW, row1H);
                var rectSub = new Rectangle(0, startY + row1H + gap, availW, row2H);
                TextRenderer.DrawText(g, titleText, fTitle, rectTitle, titleColor, drawFlags);
                TextRenderer.DrawText(g, subtitleText, fSub, rectSub, subtitleColor, drawFlags);
            }
        }

        /// <summary>
        /// 本机设备名 + 紧随其后的胶囊标签（如「DESKTOP-EXAMPLE01 [本机]」）：
        /// 基于真实渲染 DPI 动态测量主机名宽度并紧贴绘制胶囊，彻底杜绝固定列宽截断主机名。
        /// </summary>
        internal sealed class SelfDeviceTitle : Control
        {
            private readonly string hostTitle;
            private readonly string pillText;

            internal SelfDeviceTitle(string hostTitle, string pillText)
            {
                this.hostTitle = hostTitle ?? "";
                this.pillText = pillText ?? "本机";
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                TabStop = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Paper;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Color bg = ResolveSurface(this, Paper);
                g.Clear(bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                float scale = Math.Max(1f, g.DpiX / 96f);
                Font nameFont = LabelBoldFont;
                Font badgeFont = SmallFont;
                TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

                Size pillTextSz = TextRenderer.MeasureText(g, pillText, badgeFont, new Size(int.MaxValue, Height), measureFlags);
                int pillW = Math.Max((int)Math.Round(42 * scale), pillTextSz.Width + (int)Math.Round(18 * scale));
                int pillH = Math.Max((int)Math.Round(20 * scale), pillTextSz.Height + (int)Math.Round(5 * scale));
                int gap = (int)Math.Round(10 * scale);

                int maxNameW = Math.Max(40, Width - pillW - gap - 4);
                Size fullNameSz = TextRenderer.MeasureText(g, hostTitle, nameFont, new Size(int.MaxValue, Height), measureFlags);
                int drawNameW = Math.Min(maxNameW, fullNameSz.Width + (int)Math.Ceiling(4 * scale));

                var nameRect = new Rectangle(0, 0, drawNameW, Height);
                TextRenderer.DrawText(g, hostTitle, nameFont, nameRect, Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                float pillX = drawNameW + gap;
                float pillY = (Height - pillH) / 2f;
                var pillBox = new RectangleF(pillX, pillY, pillW, pillH);
                Surface(g, pillBox, pillH * 0.5f, AccentLight, Color.Transparent);
                TextRenderer.DrawText(g, pillText, badgeFont, Rectangle.Round(pillBox), Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        internal static Label Text(string content, TextSize size, Color color)
        {
            Font font = GetFont(size);
            return new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                ForeColor = color,
                Font = font,
                Text = content,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
        }

        internal static Button Action(string caption, ButtonStyle style, Glyph glyph = Glyph.None)
        {
            Font f = LabelFont;
            if (style == ButtonStyle.Primary || style == ButtonStyle.UnderlineTab) f = LabelFont;
            else if (style == ButtonStyle.Chip || style == ButtonStyle.Link) f = SmallFont;
            else if (style == ButtonStyle.NavSection) f = BodyFont;
            else if (style == ButtonStyle.ToolbarAction) f = BodyFont;

            return new ActionButton(style, glyph)
            {
                Text = caption,
                AccessibleName = string.IsNullOrEmpty(caption) ? glyph.ToString() : caption,
                Font = f,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
        }

        internal static void Highlight(Button button, bool active)
        {
            var action = button as ActionButton;
            if (action == null) return;
            if (action.Active == active) return;
            action.Active = active;
            action.Invalidate();
        }

        internal static void SetGlyphTint(Button button, Color tint)
        {
            var action = button as ActionButton;
            if (action == null) return;
            action.CustomGlyphColor = tint;
            action.Invalidate();
        }

        internal static void SetNavIndent(Button button, int indentPx)
        {
            var action = button as ActionButton;
            if (action == null) return;
            action.NavIndent = indentPx;
            action.Invalidate();
        }

        internal static void SetIndicatorDot(Button button, bool show, Color color)
        {
            var action = button as ActionButton;
            if (action == null) return;
            action.ShowIndicatorDot = show;
            action.IndicatorDotColor = color;
            action.Invalidate();
        }

        internal static void ChangeGlyph(Button button, Glyph glyph)
        {
            var action = button as ActionButton;
            if (action == null || action.Icon == glyph) return;
            action.Icon = glyph;
            action.Invalidate();
        }

        internal static void ChangeStyle(Button button, ButtonStyle style)
        {
            var action = button as ActionButton;
            if (action == null || action.Style == style) return;
            action.Style = style;
            action.Invalidate();
        }

        internal static TableLayoutPanel Grid(int columns)
        {
            return new GridPanel
            {
                ColumnCount = columns,
                RowCount = 0,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
        }

        private sealed class GridPanel : TableLayoutPanel
        {
            internal GridPanel()
            {
                DoubleBuffered = true;
            }

            protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
            {
                var oldColumnSize = new float[ColumnStyles.Count];
                var oldRowSize = new float[RowStyles.Count];
                for (int i = 0; i < oldColumnSize.Length; i++) oldColumnSize[i] = ColumnStyles[i].Width;
                for (int i = 0; i < oldRowSize.Length; i++) oldRowSize[i] = RowStyles[i].Height;
                base.ScaleControl(factor, specified);
                if ((specified & BoundsSpecified.Width) != 0)
                    for (int i = 0; i < oldColumnSize.Length; i++)
                        if (ColumnStyles[i].SizeType == SizeType.Absolute) ColumnStyles[i].Width = oldColumnSize[i] * factor.Width;
                if ((specified & BoundsSpecified.Height) != 0)
                    for (int i = 0; i < oldRowSize.Length; i++)
                        if (RowStyles[i].SizeType == SizeType.Absolute) RowStyles[i].Height = oldRowSize[i] * factor.Height;
            }
        }

        internal static void Row(TableLayoutPanel grid, float height, params Control[] controls)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            for (int col = 0; col < controls.Length; col++)
                if (controls[col] != null) grid.Controls.Add(controls[col], col, row);
        }

        internal static void FillRow(TableLayoutPanel grid, Control content = null)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            if (content != null) grid.Controls.Add(content, 0, row);
        }

        internal static void FillColumn(TableLayoutPanel grid)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        internal static void FixedColumn(TableLayoutPanel grid, int width)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        }

        internal static Panel Rule()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Divider, Margin = Padding.Empty, TabStop = false };
        }

        internal static Panel StrongRule()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = Stroke, Margin = Padding.Empty, TabStop = false };
        }

        internal static ComboBox Choice(string name, bool editable)
        {
            if (!editable)
            {
                return new StyledChoiceBox
                {
                    AccessibleName = name
                };
            }
            return new ComboBox
            {
                AccessibleName = name,
                Dock = DockStyle.None,
                DropDownStyle = ComboBoxStyle.DropDown,
                FlatStyle = FlatStyle.Flat,
                BackColor = Paper,
                ForeColor = Ink,
                Font = BodyFont,
                Margin = Padding.Empty
            };
        }

        internal sealed class StyledChoiceBox : ComboBox
        {
            private bool hovered;

            internal StyledChoiceBox()
            {
                Dock = DockStyle.None;
                DropDownStyle = ComboBoxStyle.DropDownList;
                FlatStyle = FlatStyle.Flat;
                DrawMode = DrawMode.OwnerDrawVariable;
                IntegralHeight = false;
                MaxDropDownItems = 10;
                BackColor = Paper;
                ForeColor = Ink;
                Font = BodyFont;
                Cursor = Cursors.Hand;
                Margin = Padding.Empty;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                UpdateClosedItemHeight();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                UpdateClosedItemHeight();
            }

            private void UpdateClosedItemHeight()
            {
                if (!IsHandleCreated) return;
                float scale = GetDpiScale(this);
                int desired = Math.Max(18, Math.Min(36, (int)Math.Round(22f * scale)));
                try { ItemHeight = desired; } catch { }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnGotFocus(EventArgs e)
            {
                Invalidate();
                base.OnGotFocus(e);
            }

            protected override void OnLostFocus(EventArgs e)
            {
                Invalidate();
                base.OnLostFocus(e);
            }

            protected override void OnDropDown(EventArgs e)
            {
                Invalidate();
                base.OnDropDown(e);
            }

            protected override void OnDropDownClosed(EventArgs e)
            {
                Invalidate();
                base.OnDropDownClosed(e);
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                Invalidate();
                base.OnSelectedIndexChanged(e);
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                BackColor = Enabled ? Paper : SurfaceSubtle;
                ForeColor = Enabled ? Ink : Muted;
                Invalidate();
                if (Parent != null) Parent.Invalidate();
                base.OnEnabledChanged(e);
            }

            protected override void OnTextChanged(EventArgs e)
            {
                Invalidate();
                base.OnTextChanged(e);
            }

            protected override void OnMeasureItem(MeasureItemEventArgs e)
            {
                base.OnMeasureItem(e);
                float scale = GetDpiScale(this);
                e.ItemHeight = Math.Max(26, (int)Math.Round(32f * scale));
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if ((e.State & DrawItemState.ComboBoxEdit) != 0)
                {
                    PaintClosedFace(e.Graphics, e.Bounds);
                    return;
                }
                if (e.Index < 0 || e.Index >= Items.Count) return;

                Graphics g = e.Graphics;
                bool highContrast = SystemInformation.HighContrast;
                bool hot = (e.State & DrawItemState.Selected) != 0 || (e.State & DrawItemState.HotLight) != 0;
                bool isCurrent = e.Index == SelectedIndex;
                float scale = Math.Max(1f, g.DpiX / 96f);

                Color rowBg = highContrast ? SystemColors.Window : Paper;
                FillBg(g, e.Bounds, rowBg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                if (hot)
                {
                    if (highContrast)
                    {
                        FillBg(g, e.Bounds, SystemColors.Highlight);
                    }
                    else
                    {
                        var pill = new RectangleF(
                            e.Bounds.X + 4f * scale,
                            e.Bounds.Y + 2f * scale,
                            Math.Max(8f, e.Bounds.Width - 8f * scale),
                            Math.Max(8f, e.Bounds.Height - 4f * scale));
                        Surface(g, pill, 5f * scale, AccentLight, AccentBorder, 1f);
                    }
                }

                if (isCurrent && !highContrast)
                {
                    float barW = Math.Max(2.5f, 2.5f * scale);
                    float barH = Math.Min(e.Bounds.Height - 10f * scale, 16f * scale);
                    if (barH >= 6f)
                    {
                        Surface(g,
                            new RectangleF(e.Bounds.X + 7f * scale, e.Bounds.Y + (e.Bounds.Height - barH) * 0.5f, barW, barH),
                            barW * 0.5f, Accent, Color.Transparent);
                    }
                }

                int leftPad = (int)Math.Round(15f * scale);
                int rightReserve = isCurrent ? (int)Math.Round(26f * scale) : (int)Math.Round(10f * scale);
                var textRect = new Rectangle(
                    e.Bounds.X + leftPad,
                    e.Bounds.Y,
                    Math.Max(8, e.Bounds.Width - leftPad - rightReserve),
                    e.Bounds.Height);
                Color fg = highContrast
                    ? (hot ? SystemColors.HighlightText : SystemColors.WindowText)
                    : (isCurrent ? Accent : Ink);
                string text = GetItemText(Items[e.Index]);
                TextRenderer.DrawText(g, text, (isCurrent && !highContrast) ? LabelBoldFont : Font, textRect, fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                if (isCurrent)
                {
                    float checkSize = (float)Math.Round(13f * scale);
                    var checkRect = new RectangleF(
                        e.Bounds.Right - 10f * scale - checkSize,
                        e.Bounds.Y + (e.Bounds.Height - checkSize) * 0.5f,
                        checkSize,
                        checkSize);
                    DrawGlyph(g, Glyph.Check, checkRect, highContrast && hot ? SystemColors.HighlightText : Accent);
                }
            }

            internal void PaintClosedFace(Graphics g, Rectangle bounds)
            {
                if (g == null || bounds.Width <= 0 || bounds.Height <= 0) return;
                bool highContrast = SystemInformation.HighContrast;
                Color bg = highContrast ? SystemColors.Window : (Enabled ? Paper : SurfaceSubtle);
                FillBg(g, bounds, bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                float scale = Math.Max(1f, g.DpiX / 96f);
                int leftPad = Math.Max(4, (int)Math.Round(4f * scale));
                int rightPad = Math.Max(6, (int)Math.Round(6f * scale));
                float chevSize = (float)Math.Round(13f * scale);
                int chevReserve = (int)Math.Ceiling(chevSize + rightPad + 4f * scale);

                var textRect = new Rectangle(
                    bounds.X + leftPad,
                    bounds.Y,
                    Math.Max(8, bounds.Width - leftPad - chevReserve),
                    bounds.Height);
                string display = (SelectedIndex >= 0 && SelectedIndex < Items.Count)
                    ? GetItemText(Items[SelectedIndex])
                    : (Text ?? "");
                Color fg = highContrast
                    ? (Enabled ? SystemColors.WindowText : SystemColors.GrayText)
                    : (Enabled ? Ink : Muted);
                TextRenderer.DrawText(g, display, Font, textRect, fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                Color chevColor = highContrast
                    ? (Enabled ? SystemColors.WindowText : SystemColors.GrayText)
                    : (Enabled ? ((hovered || DroppedDown || Focused) ? Accent : Muted) : StrokeStrong);
                var chevRect = new RectangleF(
                    bounds.Right - rightPad - chevSize,
                    bounds.Y + (bounds.Height - chevSize) * 0.5f,
                    chevSize,
                    chevSize);
                DrawGlyph(g, DroppedDown ? Glyph.ChevronUp : Glyph.ChevronDown, chevRect, chevColor);
            }

            protected override void WndProc(ref Message m)
            {
                const int WmPaint = 0x000F;
                const int WmEraseBkgnd = 0x0014;
                if (m.Msg == WmEraseBkgnd)
                {
                    m.Result = (IntPtr)1;
                    return;
                }
                if (m.Msg == WmPaint)
                {
                    var ps = new PaintStruct();
                    IntPtr hdc = BeginPaint(Handle, out ps);
                    try
                    {
                        if (hdc != IntPtr.Zero && Width > 0 && Height > 0)
                        {
                            using (Graphics g = Graphics.FromHdc(hdc))
                            using (BufferedGraphics buf = BufferedGraphicsManager.Current.Allocate(g, ClientRectangle))
                            {
                                PaintClosedFace(buf.Graphics, ClientRectangle);
                                buf.Render(g);
                            }
                        }
                    }
                    finally
                    {
                        EndPaint(Handle, ref ps);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct PaintStruct
            {
                public IntPtr hdc;
                public bool fErase;
                public int rcLeft;
                public int rcTop;
                public int rcRight;
                public int rcBottom;
                public bool fRestore;
                public bool fIncUpdate;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
                public byte[] rgbReserved;
            }

            [DllImport("user32.dll")]
            private static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct lpPaint);

            [DllImport("user32.dll")]
            private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct lpPaint);
        }

        internal static TextBox InputBox(string name, string text)
        {
            return new TextBox
            {
                AccessibleName = name,
                Dock = DockStyle.None,
                BorderStyle = BorderStyle.None,
                BackColor = Paper,
                ForeColor = Ink,
                Font = BodyFont,
                Text = text ?? "",
                Margin = Padding.Empty
            };
        }

        internal static Panel WrapInput(Control inner)
        {
            return new InputShell(inner);
        }

        private sealed class InputShell : Panel
        {
            private readonly Control inner;
            private readonly Panel comboClip;
            private bool focused;
            private bool hovered;
            private bool dropped;

            internal InputShell(Control inner)
            {
                this.inner = inner;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Paper;
                DoubleBuffered = true;
                ResizeRedraw = true;
                inner.BackColor = inner.Enabled ? Paper : SurfaceSubtle;
                inner.GotFocus += delegate { focused = true; Invalidate(); };
                inner.LostFocus += delegate { focused = false; Invalidate(); };
                inner.MouseEnter += delegate { hovered = true; Invalidate(); };
                inner.MouseLeave += delegate { hovered = false; Invalidate(); };
                inner.EnabledChanged += delegate
                {
                    inner.BackColor = inner.Enabled ? Paper : SurfaceSubtle;
                    Invalidate();
                };
                var styledChoice = inner as StyledChoiceBox;
                if (styledChoice != null)
                {
                    Cursor = Cursors.Hand;
                    styledChoice.DropDown += delegate { dropped = true; Invalidate(); };
                    styledChoice.DropDownClosed += delegate { dropped = false; Invalidate(); };
                    Controls.Add(inner);
                }
                else if (inner is ComboBox)
                {
                    // Clip off the native 1px flat square border of editable WinForms ComboBox so only our crisp rounded shell shows
                    comboClip = new Panel { BackColor = Paper, Margin = Padding.Empty, Padding = Padding.Empty };
                    comboClip.Controls.Add(inner);
                    Controls.Add(comboClip);
                }
                else
                {
                    Controls.Add(inner);
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (inner != null && inner.CanFocus)
                {
                    inner.Focus();
                    var choice = inner as StyledChoiceBox;
                    if (choice != null && choice.Enabled)
                        choice.DroppedDown = !choice.DroppedDown;
                }
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                if (inner == null) return;
                float scale = GetDpiScale(this);
                int padX = Math.Max(8, (int)Math.Round(10 * scale));
                int w = Math.Max(16, ClientSize.Width - padX * 2);
                var tb = inner as TextBoxBase;
                int h = tb != null ? tb.PreferredHeight : inner.Height;
                if (h <= 0) h = inner.PreferredSize.Height;
                if (h <= 0) h = Math.Max(18, (int)Math.Round(20 * scale));
                if (inner is StyledChoiceBox)
                {
                    int clipPadX = Math.Max(6, (int)Math.Round(6 * scale));
                    int choiceW = Math.Max(16, ClientSize.Width - clipPadX * 2);
                    int prefH = inner.PreferredSize.Height;
                    int choiceH = Math.Max(18, Math.Min(ClientSize.Height - 4, prefH > 0 ? prefH : h));
                    int choiceY = Math.Max(2, (ClientSize.Height - choiceH) / 2);
                    inner.Bounds = new Rectangle(clipPadX, choiceY, choiceW, choiceH);
                }
                else if (comboClip != null)
                {
                    int clipPadX = Math.Max(6, (int)Math.Round(6 * scale));
                    int clipW = Math.Max(16, ClientSize.Width - clipPadX * 2);
                    int clipH = Math.Max(18, Math.Min(ClientSize.Height - 4, h - 2));
                    int clipY = Math.Max(2, (ClientSize.Height - clipH) / 2);
                    comboClip.Bounds = new Rectangle(clipPadX, clipY, clipW, clipH);
                    inner.Bounds = new Rectangle(-1, -1, clipW + 2, clipH + 2);
                }
                else
                {
                    int y = Math.Max(2, (ClientSize.Height - h) / 2);
                    inner.Bounds = new Rectangle(padX, y, w, h);
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color parentBg = ResolveSurface(this, Paper);
                FillBg(e.Graphics, e.ClipRectangle, parentBg);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = e.Graphics.DpiX / 96f;
                bool disabled = inner != null && !inner.Enabled;
                Color fill = disabled ? SurfaceSubtle : Paper;
                Color border = disabled ? Stroke : (focused || dropped) ? Accent : hovered ? AccentBorder : StrokeStrong;
                Surface(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 6f * scale, fill, border, (focused || dropped) ? 1.4f : 1f);
            }
        }

        internal static void Cue(ComboBox field, string text)
        {
            field.HandleCreated += delegate
            {
                NativeSendMessage(field.Handle, 0x1703, IntPtr.Zero, text);
            };
        }

        internal static void Cue(TextBox field, string text)
        {
            field.HandleCreated += delegate
            {
                NativeSendMessage(field.Handle, 0x1501, IntPtr.Zero, text);
            };
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr NativeSendMessage(IntPtr handle, uint message, IntPtr flag, string text);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr largeIcon, out IntPtr smallIcon, uint count);

        internal static Icon CreateSmallAppIcon()
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                ExtractIconEx(Application.ExecutablePath, 0, IntPtr.Zero, out handle, 1);
                if (handle != IntPtr.Zero)
                {
                    using (Icon icon = Icon.FromHandle(handle))
                        return (Icon)icon.Clone();
                }
            }
            catch { }
            finally
            {
                if (handle != IntPtr.Zero) DestroyIcon(handle);
            }
            return null;
        }

        private static Bitmap cachedBrandLogo;
        private static bool brandLogoLoaded;

        internal static Bitmap GetBrandLogoBitmap()
        {
            if (brandLogoLoaded) return cachedBrandLogo;
            brandLogoLoaded = true;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DualRemote.bobo-logo.png"))
                {
                    if (stream != null)
                    {
                        using (var img = Image.FromStream(stream))
                            cachedBrandLogo = new Bitmap(img);
                        return cachedBrandLogo;
                    }
                }
            }
            catch { }

            try
            {
                Icon associated = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (associated != null)
                {
                    using (associated)
                        cachedBrandLogo = associated.ToBitmap();
                }
            }
            catch { }
            return cachedBrandLogo;
        }

        /// <summary>绘制 BoBo远程 专属圆形徽章 Logo（优先加载内嵌高分 BoBo-Remote 资源，高 DPI 抗锯齿缩放）。</summary>
        internal static void DrawBrandMark(Graphics g, RectangleF area)
        {
            if (area.Width < 2f || area.Height < 2f) return;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            Bitmap logo = GetBrandLogoBitmap();
            if (logo != null)
            {
                g.DrawImage(logo, area);
                g.Restore(state);
                return;
            }

            // Fallback crisp circular monogram if compiled without embedded logo resource
            Surface(g, area, area.Width * 0.5f, Paper, StrokeStrong, 1f);
            using (var pen = new Pen(Ink, Math.Max(1.4f, area.Width * 0.08f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float r = area.Width * 0.24f;
                g.DrawEllipse(pen, area.X + area.Width * 0.5f - r, area.Y + area.Height * 0.5f - r, r * 2f, r * 2f);
            }
            g.Restore(state);
        }

        internal static Icon CreateAppIcon()
        {
            try
            {
                Icon associated = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (associated != null) return associated;
            }
            catch { }

            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                DrawBrandMark(g, new RectangleF(1, 1, 30, 30));
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        internal static GraphicsPath Round(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
            if (radius < 0.8f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float d = 2 * radius;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>无抗锯齿实心填充背景（杜绝 GDI+ AntiAlias FillRectangle 在双缓冲上产生顶部/左侧黑边）。</summary>
        internal static void FillBg(Graphics g, Rectangle rect, Color color)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (var brush = new SolidBrush(color)) g.FillRectangle(brush, rect);
        }

        internal static GraphicsPath RoundTop(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
            if (radius < 0.8f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float d = 2 * radius;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddLine(bounds.Right, bounds.Y + radius, bounds.Right, bounds.Bottom);
            path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
            path.AddLine(bounds.X, bounds.Bottom, bounds.X, bounds.Y + radius);
            path.CloseFigure();
            return path;
        }

        internal static GraphicsPath RoundTopLeft(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
            if (radius < 0.8f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float d = 2 * radius;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddLine(bounds.X + radius, bounds.Y, bounds.Right, bounds.Y);
            path.AddLine(bounds.Right, bounds.Y, bounds.Right, bounds.Bottom);
            path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
            path.AddLine(bounds.X, bounds.Bottom, bounds.X, bounds.Y + radius);
            path.CloseFigure();
            return path;
        }

        internal static void Surface(Graphics graphics, RectangleF bounds, float radius, Color background, Color border, float borderWidth = 1f)
        {
            if (bounds.Width < 1 || bounds.Height < 1) return;
            using (var path = Round(bounds, radius))
            {
                if (background.A > 0)
                    using (var brush = new SolidBrush(background)) graphics.FillPath(brush, path);
                if (border.A > 0)
                    using (var pen = new Pen(border, borderWidth)) graphics.DrawPath(pen, path);
            }
        }

        private static Color Mix(Color from, Color to, float amount)
        {
            if (amount <= 0f) return from;
            if (amount >= 1f) return to;
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * amount),
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private static void FocusOutline(Graphics graphics, RectangleF bounds, float radius, Color color, float width)
        {
            using (var path = Round(bounds, radius))
            using (var pen = new Pen(color, width))
                graphics.DrawPath(pen, path);
        }

        private static Color ResolveSurface(Control control, Color fallback)
        {
            for (Control current = control.Parent; current != null; current = current.Parent)
            {
                if (current is Card) return Paper;
                if (current is WorkspaceFrame) return Workspace;
                if (current.BackColor.A == 255) return current.BackColor;
            }
            return fallback;
        }

        /// <summary>UU 风格纯白圆角工作区容器（左上角柔和切角接入 #F3F5F8 窗口骨架）。</summary>
        internal sealed class WorkspaceFrame : Panel
        {
            internal WorkspaceFrame()
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Padding = new Padding(1, 1, 0, 0);
                BackColor = Workspace;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                FillBg(g, e.ClipRectangle, ChromeBg);
                if (Width < 4 || Height < 4) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = g.DpiX / 96f;
                float radius = 10f * scale;
                var rect = new RectangleF(0.5f, 0.5f, Width, Height);
                using (var path = RoundTopLeft(rect, radius))
                {
                    using (var fill = new SolidBrush(Workspace)) g.FillPath(fill, path);
                    using (var pen = new Pen(Stroke, 1f)) g.DrawPath(pen, path);
                }
            }
        }

        private sealed class ActionButton : Button
        {
            private ButtonStyle style;
            internal ButtonStyle Style { get { return style; } set { style = value; } }
            private readonly UiTransition hoverTransition;
            private bool hovered;
            private bool pressed;
            private bool keyboardFocus;
            internal Glyph Icon;
            internal bool Active;
            internal Color CustomGlyphColor = Color.Empty;
            internal int NavIndent = 0;
            internal bool ShowIndicatorDot;
            internal Color IndicatorDotColor = Success;

            internal ActionButton(ButtonStyle style, Glyph icon)
            {
                this.style = style;
                hoverTransition = new UiTransition(this);
                Icon = icon;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                    | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                UpdateHover();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = pressed = false;
                UpdateHover();
                base.OnMouseLeave(e);
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible) return;
                hovered = pressed = false;
                UpdateHover();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                if (!Enabled) hovered = pressed = false;
                UpdateHover();
            }

            private void UpdateHover()
            {
                if (hoverTransition != null)
                    hoverTransition.MoveTo(Enabled && hovered && style != ButtonStyle.Close ? 1f : 0f, 115);
                Invalidate();
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                keyboardFocus = false;
                pressed = e.Button == MouseButtons.Left;
                Invalidate();
                base.OnMouseDown(e);
            }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (!pressed || Capture) return;
                pressed = false;
                Invalidate();
            }

            protected override void OnEnter(EventArgs e)
            {
                keyboardFocus = Control.MouseButtons == MouseButtons.None;
                base.OnEnter(e);
                Invalidate();
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                keyboardFocus = true;
                base.OnKeyDown(e);
                Invalidate();
            }
            protected override void OnLostFocus(EventArgs e)
            {
                keyboardFocus = false;
                base.OnLostFocus(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (Width < 3 || Height < 3) return;
                Graphics g = e.Graphics;
                GraphicsState state = g.Save();
                try
                {
                    Color defaultParent = (style == ButtonStyle.Navigation || style == ButtonStyle.NavSection
                        || style == ButtonStyle.Caption || style == ButtonStyle.Close || style == ButtonStyle.IconGhost
                        || style == ButtonStyle.DangerPill)
                        ? ChromeBg : Paper;
                    Color surface = ResolveSurface(this, defaultParent);
                    g.Clear(surface);

                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    Color background = Color.Transparent;
                    Color stroke = Color.Transparent;
                    Color text = Ink;
                    Color glyphColor = Color.Empty;
                    bool glow = Enabled && (hovered || pressed);
                    float hover = !Enabled ? 0f : pressed ? 1f : hoverTransition.Value;
                    float scale = GetDpiScale(this);

                    if (style == ButtonStyle.Primary)
                    {
                        background = !Enabled ? DisabledBg : pressed ? AccentPressed : Mix(Accent, AccentHover, hover);
                        text = !Enabled ? DisabledText : Paper;
                    }
                    else if (style == ButtonStyle.Secondary)
                    {
                        background = !Enabled ? SurfaceSubtle : pressed ? NavHover : Mix(Paper, SurfaceSubtle, hover);
                        stroke = Mix(StrokeStrong, AccentBorder, hover);
                        text = Enabled ? Ink : Muted;
                    }
                    else if (style == ButtonStyle.AccentSoft)
                    {
                        background = Active || pressed ? NavActive : Mix(AccentLight, NavHover, hover);
                        stroke = Active ? Accent : AccentBorder;
                        text = Accent;
                    }
                    else if (style == ButtonStyle.WarningSoft || style == ButtonStyle.DangerSoft)
                    {
                        bool warning = style == ButtonStyle.WarningSoft;
                        text = warning ? Warning : Alert;
                        background = warning ? WarningLight : AlertLight;
                        stroke = glow ? text : Color.Transparent;
                    }
                    else if (style == ButtonStyle.Chip)
                    {
                        background = Active ? AccentLight : Mix(Paper, SurfaceSubtle, hover);
                        stroke = Active ? Accent : StrokeStrong;
                        text = Active ? Accent : Ink;
                    }
                    else if (style == ButtonStyle.UnderlineTab)
                    {
                        background = surface;
                        text = Active ? Ink : glow ? BodyInk : Muted;
                    }
                    else if (style == ButtonStyle.Navigation)
                    {
                        background = Active ? NavActive : Mix(surface, NavHover, hover);
                        text = Active ? Ink : glow ? Ink : BodyInk;
                    }
                    else if (style == ButtonStyle.NavSection)
                    {
                        background = Mix(surface, NavHover, hover);
                        text = Ink;
                    }
                    else if (style == ButtonStyle.ToolbarAction)
                    {
                        background = Mix(Paper, SurfaceSubtle, hover);
                        text = Enabled ? BodyInk : Muted;
                    }
                    else if (style == ButtonStyle.Link)
                    {
                        background = surface;
                        text = Enabled ? (glow ? AccentHover : Accent) : Muted;
                    }
                    else if (style == ButtonStyle.Quiet)
                    {
                        background = Mix(surface, SurfaceSubtle, hover);
                        text = Enabled ? (Active ? Accent : BodyInk) : Muted;
                    }
                    else if (style == ButtonStyle.IconGhost)
                    {
                        background = Active ? NavActive : Mix(surface, NavHover, hover);
                        text = Ink;
                    }
                    else if (style == ButtonStyle.Close)
                    {
                        background = glow ? Alert : surface;
                        text = glow ? Paper : BodyInk;
                    }
                    else if (style == ButtonStyle.Caption)
                    {
                        background = Mix(surface, NavHover, hover);
                        text = BodyInk;
                    }
                    else if (style == ButtonStyle.DangerPill)
                    {
                        background = !Enabled ? DisabledBg : Alert;
                        stroke = glow ? Ink : Color.Transparent;
                        text = Enabled ? Paper : DisabledText;
                    }

                    if (!Enabled && style != ButtonStyle.Primary && style != ButtonStyle.DangerPill) text = Muted;
                    if (SystemInformation.HighContrast)
                    {
                        background = Active ? SystemColors.Highlight : SystemColors.ButtonFace;
                        text = !Enabled ? SystemColors.GrayText : Active ? SystemColors.HighlightText : SystemColors.ControlText;
                        stroke = SystemColors.WindowFrame;
                    }
                    if (background.A == 0) background = surface;

                    float radius = (style == ButtonStyle.Caption || style == ButtonStyle.Close || style == ButtonStyle.UnderlineTab
                        || style == ButtonStyle.Link || style == ButtonStyle.ToolbarAction)
                        ? 0f : style == ButtonStyle.DangerPill ? (Height - 2f) * 0.5f : (style == ButtonStyle.Navigation || style == ButtonStyle.NavSection) ? 8f * scale : 6.5f * scale;

                    if (style != ButtonStyle.UnderlineTab && style != ButtonStyle.Link)
                    {
                        Surface(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius, background, stroke);
                    }

                    // Inset accent marker for the active page
                    if (style == ButtonStyle.Navigation && Active)
                    {
                        float barW = Math.Max(2.5f, 2.5f * scale);
                        float barH = Math.Min(Height - 14f * scale, 18f * scale);
                        Surface(g, new RectangleF(1.5f * scale, (Height - barH) / 2f, barW, barH), barW * 0.5f, SystemInformation.HighContrast ? text : Accent, Color.Transparent);
                    }

                    // Accent underline for the active tab
                    if (style == ButtonStyle.UnderlineTab && Active)
                    {
                        float lineW = Math.Min(Width - 8f * scale, 22f * scale);
                        float lineH = Math.Max(2.5f, 2.5f * scale);
                        Surface(g, new RectangleF((Width - lineW) / 2f, Height - lineH - 1f, lineW, lineH), lineH * 0.5f, SystemInformation.HighContrast ? text : Accent, Color.Transparent);
                    }

                    if (keyboardFocus && Focused && style != ButtonStyle.Caption && style != ButtonStyle.Close)
                        FocusOutline(g, new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f), Math.Max(2f, radius - 1f),
                            SystemInformation.HighContrast || style == ButtonStyle.Primary || style == ButtonStyle.DangerPill ? text : Accent, 1.5f);

                    glyphColor = CustomGlyphColor.IsEmpty || SystemInformation.HighContrast ? text : CustomGlyphColor;
                    if (style == ButtonStyle.Navigation && Enabled && CustomGlyphColor == Accent && !Active) glyphColor = BodyInk;
                    if (style == ButtonStyle.Navigation && Enabled && Active && !SystemInformation.HighContrast) glyphColor = Accent;
                    if (!Enabled) glyphColor = text;
                    if (style == ButtonStyle.Primary || style == ButtonStyle.DangerPill) glyphColor = text;

                    Font drawFont = Font;
                    if (style == ButtonStyle.UnderlineTab && Active)
                        drawFont = LabelBoldFont;

                    if (style == ButtonStyle.Navigation || style == ButtonStyle.NavSection)
                    {
                        float iconSize = (float)Math.Round((style == ButtonStyle.NavSection ? 15f : 16f) * scale);
                        float padLeft = (style == ButtonStyle.NavSection ? 10f : (14f + NavIndent)) * scale;
                        float x = (float)Math.Round(padLeft);
                        if (Icon != Glyph.None)
                        {
                            DrawGlyph(g, Icon, new RectangleF(x, (float)Math.Round((Height - iconSize) / 2f), iconSize, iconSize), glyphColor);
                            x += iconSize + (float)Math.Round(10f * scale);
                        }
                        int rightReserve = style == ButtonStyle.NavSection ? (int)Math.Round(26 * scale) : (int)Math.Round(10 * scale);
                        var labelRect = new Rectangle((int)Math.Round(x), 0, Math.Max(8, Width - (int)Math.Round(x) - rightReserve), Height);
                        TextRenderer.DrawText(g, Text, drawFont, labelRect, text,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                        if (style == ButtonStyle.NavSection)
                        {
                            float chevSize = (float)Math.Round(13f * scale);
                            DrawGlyph(g, Active ? Glyph.ChevronUp : Glyph.ChevronDown,
                                new RectangleF(Width - (float)Math.Round(8f * scale) - chevSize,
                                    (float)Math.Round((Height - chevSize) / 2f), chevSize, chevSize), Muted);
                        }
                        return;
                    }

                    if (string.IsNullOrEmpty(Text))
                    {
                        float s = (float)Math.Round(((style == ButtonStyle.Caption || style == ButtonStyle.Close) ? 13f : 16f) * scale);
                        DrawGlyph(g, Icon, new RectangleF((float)Math.Round((Width - s) / 2f), (float)Math.Round((Height - s) / 2f), s, s), glyphColor);
                        if (ShowIndicatorDot)
                        {
                            float dot = 6.5f * scale;
                            using (var db = new SolidBrush(IndicatorDotColor))
                                g.FillEllipse(db, Width / 2f + 4f * scale, Height / 2f - 9f * scale, dot, dot);
                        }
                        return;
                    }

                    int textTopOffset = style == ButtonStyle.UnderlineTab ? -(int)Math.Round(3 * scale) : 0;

                    // Text-only centered buttons: use native HorizontalCenter + VerticalCenter + SingleLine + NoPadding
                    if (Icon == Glyph.None && style != ButtonStyle.Link && style != ButtonStyle.Quiet)
                    {
                        int padX = (int)Math.Round(6 * scale);
                        var centerRect = new Rectangle(padX, textTopOffset, Math.Max(8, Width - padX * 2), Height - textTopOffset);
                        TextRenderer.DrawText(g, Text, drawFont, centerRect, text,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                        return;
                    }

                    TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                    Size textSize = TextRenderer.MeasureText(g, Text, drawFont, new Size(int.MaxValue, Height), measureFlags);
                    float iconDim = Icon == Glyph.None ? 0f : (float)Math.Round(14f * scale);
                    float gap = Icon == Glyph.None ? 0f : (float)Math.Round(6f * scale);
                    float contentW = iconDim + gap + textSize.Width;
                    float padEdge = (float)Math.Round(6f * scale);
                    float startX = Math.Max(padEdge, (float)Math.Round((Width - contentW) / 2f));
                    if (style == ButtonStyle.Link) startX = 0f;
                    else if (style == ButtonStyle.Quiet) startX = (float)Math.Round(4f * scale);

                    if (Icon != Glyph.None)
                    {
                        DrawGlyph(g, Icon, new RectangleF((float)Math.Round(startX), (float)Math.Round((Height - iconDim) / 2f), iconDim, iconDim), glyphColor);
                        startX += iconDim + gap;
                    }
                    int rightPad = (style == ButtonStyle.Link) ? 0 : (int)Math.Round(4 * scale);
                    var rect = new Rectangle((int)Math.Round(startX), textTopOffset, Math.Max(8, Width - (int)Math.Round(startX) - rightPad), Height - textTopOffset);
                    TextRenderer.DrawText(g, Text, drawFont, rect, text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                finally
                {
                    g.Restore(state);
                }
            }
        }

        /// <summary>浅色内容卡片。</summary>
        internal sealed class Card : Panel
        {
            internal bool DashedBorder;

            internal Card()
            {
                Padding = new Padding(22, 18, 22, 18);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Paper;
                ResizeRedraw = true;
                DoubleBuffered = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color bg = ResolveSurface(this, Workspace);
                FillBg(e.Graphics, e.ClipRectangle, bg);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = e.Graphics.DpiX / 96f;
                float radius = 8f * scale;
                if (DashedBorder)
                {
                    var dashBox = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                    using (var path = Round(dashBox, radius))
                    {
                        using (var fill = new SolidBrush(Paper)) e.Graphics.FillPath(fill, path);
                        using (var pen = new Pen(StrokeStrong, 1f) { DashStyle = DashStyle.Dash })
                            e.Graphics.DrawPath(pen, path);
                    }
                    return;
                }
                var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                Surface(e.Graphics, box, radius, Paper, Stroke, 1f);
            }
        }

        internal sealed class SlimScrollViewport : Panel
        {
            private const int ScrollbarReserve = 14;
            private static readonly Color ThumbIdle = Color.FromArgb(158, 167, 180);
            private static readonly Color ThumbHover = Color.FromArgb(104, 117, 136);
            private readonly UiTransition thumbEmphasis;
            private Control content;
            private int offset;
            private bool dragging;
            private bool thumbHot;
            private float thumbGrab;

            internal int MaxContentWidth = 1680;
            internal bool SuspendResponsiveLayout;
            internal event Action<int> ContentWidthChanged;

            internal SlimScrollViewport()
            {
                thumbEmphasis = new UiTransition(this, invalidate: InvalidateScrollbar);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Padding = Padding.Empty;
                BackColor = Workspace;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            internal void ScrollByWheel(int delta)
            {
                if (HasOverflow()) SetOffset(offset - delta / 3);
            }

            internal void ResetScroll()
            {
                SetOffset(0);
            }

            internal void RefreshLayout()
            {
                LayoutContent();
            }

            protected override void OnControlAdded(ControlEventArgs e)
            {
                base.OnControlAdded(e);
                if (content == null && e.Control != null)
                {
                    content = e.Control;
                    content.Dock = DockStyle.None;
                    content.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    content.SizeChanged += delegate { ClampOffsetAndRedraw(); };
                    HookMouseWheel(content);
                    LayoutContent();
                }
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                LayoutContent();
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible) LayoutContent();
                else if (dragging || thumbHot) FinishDrag(false);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                ScrollByWheel(e.Delta);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !HasOverflow()) return;
                RectangleF track;
                RectangleF thumb;
                GetScrollbarGeometry(out track, out thumb);
                bool overThumb = HitsThumb(e.Location, track, thumb);
                if (!overThumb && !track.Contains(e.Location)) return;

                thumbGrab = overThumb ? (e.Y - thumb.Top) / thumb.Height : 0.5f;
                dragging = true;
                Capture = true;
                UpdateThumbState(true);
                if (!overThumb) MoveThumb(e.Y, track, thumb);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragging)
                {
                    RectangleF track;
                    RectangleF thumb;
                    GetScrollbarGeometry(out track, out thumb);
                    MoveThumb(e.Y, track, thumb);
                    return;
                }

                bool hot = IsOverThumb(e.Location);
                if (hot != thumbHot) UpdateThumbState(hot);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (dragging && e.Button == MouseButtons.Left) FinishDrag(IsOverThumb(e.Location));
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (dragging && !Capture) FinishDrag(Visible && IsOverThumb(PointToClient(Cursor.Position)));
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (!dragging && thumbHot) UpdateThumbState(false);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color background = BackColor.A == 0 ? Workspace : BackColor;
                FillBg(e.Graphics, e.ClipRectangle, background);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (!HasOverflow()) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                RectangleF track;
                RectangleF thumb;
                GetScrollbarGeometry(out track, out thumb);
                Color color = dragging ? BodyInk : Mix(ThumbIdle, ThumbHover, thumbEmphasis.Value);
                thumb.Y = (float)Math.Round(thumb.Y);
                Surface(e.Graphics, thumb, thumb.Width * 0.5f, color, Color.Transparent);
            }

            private void HookMouseWheel(Control control)
            {
                control.MouseWheel += delegate(object sender, MouseEventArgs e)
                {
                    var combo = sender as ComboBox;
                    if (combo != null && combo.DroppedDown) return;
                    ScrollByWheel(e.Delta);
                };
                control.ControlAdded += delegate(object sender, ControlEventArgs e) { HookMouseWheel(e.Control); };
                foreach (Control child in control.Controls)
                    HookMouseWheel(child);
            }

            private int ScrollbarWidth
            {
                get { return Math.Max(8, (int)Math.Round(ScrollbarReserve * GetDpiScale(this))); }
            }

            private void LayoutContent()
            {
                if (content == null || SuspendResponsiveLayout) return;
                float scale = GetDpiScale(this);
                int avail = Math.Max(280, ClientSize.Width - ScrollbarWidth);
                int targetWidth = MaxContentWidth > 0
                    ? Math.Min(avail, (int)Math.Round(MaxContentWidth * scale))
                    : avail;
                int leftX = avail > targetWidth ? (avail - targetWidth) / 2 : 0;

                bool widthChanged = content.Width != targetWidth;
                if (widthChanged)
                {
                    content.Width = targetWidth;
                    var handler = ContentWidthChanged;
                    if (handler != null) handler(targetWidth);
                }
                offset = Math.Min(MaxOffset(), offset);
                content.Location = new Point(leftX, -offset);
                if (!HasOverflow() && (dragging || thumbHot)) FinishDrag(false);
                Invalidate();
            }

            private void ClampOffsetAndRedraw()
            {
                SetOffset(offset);
                if (!HasOverflow() && (dragging || thumbHot)) FinishDrag(false);
                InvalidateScrollbar();
            }

            private bool HasOverflow()
            {
                return MaxOffset() > 1;
            }

            private int MaxOffset()
            {
                return content == null ? 0 : Math.Max(0, content.Height - ClientSize.Height);
            }

            private void SetOffset(int value)
            {
                int next = Math.Max(0, Math.Min(MaxOffset(), value));
                if (offset == next) return;
                offset = next;
                if (content != null) content.Location = new Point(content.Left, -offset);
                if (thumbHot && !dragging && !IsOverThumb(PointToClient(Cursor.Position))) UpdateThumbState(false);
                Invalidate();
            }

            private void GetScrollbarGeometry(out RectangleF track, out RectangleF thumb)
            {
                float scale = GetDpiScale(this);
                float top = (float)Math.Round(8f * scale);
                float trackHeight = Math.Max(1f, ClientSize.Height - top * 2f);
                int reserve = ScrollbarWidth;
                track = new RectangleF(ClientSize.Width - reserve, top, reserve, trackHeight);

                float width = (3f + 2.5f * thumbEmphasis.Value) * scale;
                float thumbHeight = Math.Max(32f * scale, trackHeight * ClientSize.Height / Math.Max(1f, content.Height));
                thumbHeight = Math.Min(trackHeight, (float)Math.Round(thumbHeight));
                float travel = trackHeight - thumbHeight;
                float thumbTop = top + travel * offset / Math.Max(1f, MaxOffset());
                thumb = new RectangleF(track.Left + (reserve - width) / 2f, thumbTop, width, thumbHeight);
            }

            private bool IsOverThumb(Point point)
            {
                if (!HasOverflow()) return false;
                RectangleF track;
                RectangleF thumb;
                GetScrollbarGeometry(out track, out thumb);
                return HitsThumb(point, track, thumb);
            }

            private bool HitsThumb(Point point, RectangleF track, RectangleF thumb)
            {
                var hit = new RectangleF(track.Left, thumb.Top, track.Width, thumb.Height);
                hit.Inflate(0, 4f * GetDpiScale(this));
                return hit.Contains(point);
            }

            private void MoveThumb(int pointerY, RectangleF track, RectangleF thumb)
            {
                float travel = track.Height - thumb.Height;
                if (travel <= 0) return;
                float ratio = (pointerY - track.Top - thumbGrab * thumb.Height) / travel;
                SetOffset((int)Math.Round(ratio * MaxOffset()));
            }

            private void FinishDrag(bool hot)
            {
                dragging = false;
                if (Capture) Capture = false;
                UpdateThumbState(hot);
            }

            private void UpdateThumbState(bool hot)
            {
                thumbHot = hot;
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                thumbEmphasis.MoveTo(hot ? 1f : 0f, 120);
                InvalidateScrollbar();
            }

            private void InvalidateScrollbar()
            {
                int width = Math.Min(ClientSize.Width, ScrollbarWidth);
                Invalidate(new Rectangle(ClientSize.Width - width, 0, width, ClientSize.Height));
            }
        }

        internal sealed class IconTile : Control
        {
            private readonly Glyph glyph;
            private readonly bool soft;
            private Color customBg;
            private Color customFg;
            private readonly bool hasCustom;
            private readonly float customRadius;

            internal IconTile(Glyph glyph, bool soft = false)
            {
                this.glyph = glyph;
                this.soft = soft;
                this.customRadius = 7f;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                TabStop = false;
                DoubleBuffered = true;
            }

            internal IconTile(Glyph glyph, bool soft, Color bg, Color fg, float radius = 7f) : this(glyph, soft)
            {
                customBg = bg;
                customFg = fg;
                hasCustom = true;
                customRadius = radius;
            }

            internal void SetColors(Color bg, Color fg)
            {
                customBg = bg;
                customFg = fg;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Color bg = ResolveSurface(this, Paper);
                g.Clear(bg);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = g.DpiX / 96f;
                float side = Math.Min(Width - 2f, Height - 2f);
                if (side < 8f) return;
                var box = new RectangleF((float)Math.Round((Width - side) / 2f), (float)Math.Round((Height - side) / 2f), (float)Math.Round(side), (float)Math.Round(side));
                Color fill = hasCustom ? customBg : (soft ? SurfaceSubtle : Accent);
                Color fg = hasCustom ? customFg : (soft ? Accent : Paper);
                if (fill.A > 0)
                    Surface(g, box, customRadius * scale, fill, Color.Transparent);
                float pad = fill.A > 0 ? side * 0.24f : side * 0.14f;
                DrawGlyph(g, glyph, RectangleF.Inflate(box, -pad, -pad), fg);
            }
        }

        internal sealed class Switch : CheckBox
        {
            private readonly UiTransition position;
            private bool hovered;
            internal bool ShowStateText = true;

            internal Switch()
            {
                position = new UiTransition(this);
                AutoSize = false;
                Cursor = Cursors.Hand;
                Margin = Padding.Empty;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                    | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnCheckedChanged(EventArgs e)
            {
                base.OnCheckedChanged(e);
                position.MoveTo(Checked ? 1f : 0f, 150);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(ResolveSurface(this, Paper));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                float scale = GetDpiScale(this);
                float w = (float)Math.Round(40f * scale);
                float h = (float)Math.Round(22f * scale);
                float inset = (float)Math.Ceiling(4f * scale);
                float x = Math.Max(inset, Width - w - inset);
                float y = (float)Math.Round((Height - h) / 2f);
                float progress = position.Value;
                bool hot = Enabled && hovered;

                if (ShowStateText && x > 22f * scale)
                {
                    var textRect = new Rectangle(0, 0, (int)Math.Floor(x - 8f * scale), Height);
                    TextRenderer.DrawText(g, Checked ? "开" : "关", BodyFont, textRect, Enabled ? BodyInk : Muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }

                Color onFill = Enabled ? (hot ? AccentHover : Accent) : AccentBorder;
                Color offFill = hot ? SurfaceSubtle : Paper;
                Color offBorder = Enabled ? (hot ? BodyInk : Muted) : StrokeStrong;
                Surface(g, new RectangleF(x, y, w, h), h / 2f, Mix(offFill, onFill, progress), Mix(offBorder, onFill, progress), scale);

                float pad = (float)Math.Round(3f * scale);
                float diameter = h - pad * 2f;
                float thumbX = x + pad + (w - h) * progress;
                Color thumbColor = Mix(Enabled ? BodyInk : Muted, Paper, progress);
                using (var dot = new SolidBrush(thumbColor))
                    g.FillEllipse(dot, thumbX, y + pad, diameter, diameter);

                if (Focused && ShowFocusCues)
                {
                    float gap = 2f * scale;
                    FocusOutline(g, new RectangleF(x - gap, y - gap, w + gap * 2f, h + gap * 2f),
                        h / 2f + gap, Accent, 1.25f * scale);
                }
            }
        }

        /// <summary>轻克制状态标签（匹配 UU远程 `本机` / `离线` / `在线` 微标签）。</summary>
        internal sealed class Badge : Label
        {
            private PillTone tone;
            internal bool AlignRight;

            internal Badge(string caption, bool positive)
                : this(caption, positive ? PillTone.Success : PillTone.Neutral) { }

            internal Badge(string caption, PillTone tone)
            {
                this.tone = tone;
                Text = caption;
                AccessibleName = caption;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Font = SmallFont;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            internal void UpdateStatus(string caption, PillTone newTone)
            {
                if (Text == caption && tone == newTone) return;
                Text = caption;
                AccessibleName = caption;
                tone = newTone;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Color parentBg = ResolveSurface(this, Paper);
                g.Clear(parentBg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Color fill = SurfaceSubtle;
                Color fg = BodyInk;
                if (tone == PillTone.Success) { fill = SuccessLight; fg = Success; }
                else if (tone == PillTone.Warning) { fill = WarningLight; fg = Warning; }
                else if (tone == PillTone.Alert) { fill = AlertLight; fg = Alert; }
                else if (tone == PillTone.Info) { fill = AccentLight; fg = Accent; }

                float scale = g.DpiX / 96f;
                Size textSize = TextRenderer.MeasureText(g, Text, Font, new Size(Width, Height),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int pillW = Math.Max(22, Math.Min(Width - 2, textSize.Width + (int)Math.Round(18 * scale)));
                int pillH = Math.Max(16, Math.Min(Height - 2, (int)Math.Round(22 * scale)));
                float boxX = AlignRight ? Math.Max(1f, Width - pillW - 1f) : 1f;
                var box = new RectangleF(boxX, (Height - pillH) / 2f, pillW, pillH);
                Surface(g, box, pillH * 0.5f, fill, Color.Transparent);
                TextRenderer.DrawText(g, Text, Font, Rectangle.Round(box), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        internal sealed class StatusBanner : Control
        {
            private string messageText;
            private PillTone tone = PillTone.Info;

            internal StatusBanner(string initial)
            {
                messageText = initial ?? "";
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                TabStop = false;
                BackColor = Workspace;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            internal string Message { get { return messageText; } }

            internal void Set(string message, PillTone newTone = PillTone.Info)
            {
                messageText = message ?? "";
                tone = newTone;
                AccessibleName = messageText;
                Invalidate();
            }

            private void GetTonePalette(out Color bg, out Color border, out Color dotColor, out Color textColor)
            {
                bg = SurfaceSubtle;
                border = Stroke;
                dotColor = Accent;
                textColor = BodyInk;
                if (tone == PillTone.Success)
                {
                    bg = SuccessLight;
                    border = Stroke;
                    dotColor = Success;
                    textColor = Success;
                }
                else if (tone == PillTone.Warning)
                {
                    bg = WarningLight;
                    border = Stroke;
                    dotColor = Warning;
                    textColor = Warning;
                }
                else if (tone == PillTone.Alert)
                {
                    bg = AlertLight;
                    border = Stroke;
                    dotColor = Alert;
                    textColor = Alert;
                }
                else if (tone == PillTone.Info)
                {
                    bg = AccentLight;
                    border = AccentBorder;
                    dotColor = Accent;
                    textColor = BodyInk;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Color parentBg = ResolveSurface(this, Workspace);
                g.Clear(parentBg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float scale = Math.Max(1f, g.DpiX / 96f);
                Color bg, border, dotColor, textColor;
                GetTonePalette(out bg, out border, out dotColor, out textColor);

                if (SystemInformation.HighContrast)
                {
                    bg = SystemColors.Window;
                    border = SystemColors.WindowFrame;
                    dotColor = textColor = SystemColors.WindowText;
                }
                Surface(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8f * scale, bg, border);
                Glyph marker = tone == PillTone.Success ? Glyph.Check :
                    tone == PillTone.Warning || tone == PillTone.Alert ? Glyph.WarningMark : Glyph.Info;
                float iconSize = (float)Math.Round(16f * scale);
                DrawGlyph(g, marker, new RectangleF(12f * scale, (Height - iconSize) / 2f, iconSize, iconSize), dotColor);

                int leftPad = (int)Math.Round(36 * scale);
                int rightPad = (int)Math.Round(14 * scale);
                var textRect = new Rectangle(leftPad, 0, Math.Max(16, Width - leftPad - rightPad), Height);
                TextRenderer.DrawText(g, messageText, SmallFont, textRect, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        private static void DrawWindowGlyph(Graphics g, Glyph icon, Rectangle bounds, Color color)
        {
            int side = (int)Math.Round(bounds.Width * 0.65f);
            int stroke = Math.Max(1, (int)Math.Round(bounds.Width / 13f));
            int x = bounds.Left + (bounds.Width - side) / 2;
            int y = bounds.Top + (bounds.Height - side) / 2;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            using (var ink = new SolidBrush(color))
            {
                if (icon == Glyph.Minus)
                {
                    g.FillRectangle(ink, x, y + (side - stroke) / 2, side, stroke);
                }
                else if (icon == Glyph.Maximize)
                {
                    FillPixelFrame(g, ink, new Rectangle(x, y, side, side), stroke);
                }
                else
                {
                    int shift = Math.Max(stroke + 1, side / 4);
                    int face = side - shift;
                    g.FillRectangle(ink, x + shift, y, face, stroke);
                    g.FillRectangle(ink, x + side - stroke, y + stroke, stroke, Math.Max(1, face - stroke));
                    g.FillRectangle(ink, x + shift, y + stroke, stroke, shift - stroke);
                    g.FillRectangle(ink, x + face, y + face - stroke, shift, stroke);
                    FillPixelFrame(g, ink, new Rectangle(x, y + shift, face, face), stroke);
                }
            }
            g.Restore(state);
        }

        private static void FillPixelFrame(Graphics g, Brush ink, Rectangle box, int stroke)
        {
            g.FillRectangle(ink, box.Left, box.Top, box.Width, stroke);
            g.FillRectangle(ink, box.Left, box.Bottom - stroke, box.Width, stroke);
            g.FillRectangle(ink, box.Left, box.Top + stroke, stroke, box.Height - stroke * 2);
            g.FillRectangle(ink, box.Right - stroke, box.Top + stroke, stroke, box.Height - stroke * 2);
        }

        internal static void DrawGlyph(Graphics g, Glyph icon, RectangleF area, Color color)
        {
            if (icon == Glyph.None || area.Width < 2f || area.Height < 2f) return;
            int dim = (int)Math.Round(Math.Min(area.Width, area.Height));
            int x = (int)Math.Round(area.X + (area.Width - dim) * 0.5f);
            int y = (int)Math.Round(area.Y + (area.Height - dim) * 0.5f);
            if (dim >= 8 && (icon == Glyph.Minus || icon == Glyph.Maximize || icon == Glyph.Restore))
            {
                DrawWindowGlyph(g, icon, new Rectangle(x, y, dim, dim), color);
                return;
            }

            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float unit = dim / 24f;
            g.TranslateTransform(x, y);
            g.ScaleTransform(unit, unit);
            float strokeW = Math.Max(1.8f, 1.5f / unit);
            using (var p = new Pen(color, strokeW) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var fill = new SolidBrush(color))
            {
                switch (icon)
                {
                    case Glyph.Devices:
                        using (var mon = Round(new RectangleF(2.5f, 4.5f, 12.5f, 9.5f), 1.6f))
                            g.DrawPath(p, mon);
                        g.DrawLine(p, 5.8f, 18.2f, 11.8f, 18.2f);
                        g.DrawLine(p, 8.8f, 14f, 8.8f, 18.2f);
                        using (var phone = Round(new RectangleF(17.2f, 9.5f, 4.4f, 9.2f), 1.2f))
                            g.DrawPath(p, phone);
                        break;
                    case Glyph.AllDevices:
                        for (int row = 0; row < 2; row++)
                        {
                            for (int col = 0; col < 2; col++)
                            {
                                using (var tile = Round(new RectangleF(3.8f + col * 9.6f, 3.8f + row * 9.6f, 6.8f, 6.8f), 1.4f))
                                    g.DrawPath(p, tile);
                            }
                        }
                        break;
                    case Glyph.WindowsTile:
                        using (var r1 = Round(new RectangleF(4f, 4f, 7f, 7f), 1.3f)) g.FillPath(fill, r1);
                        using (var r2 = Round(new RectangleF(13f, 4f, 7f, 7f), 1.3f)) g.FillPath(fill, r2);
                        using (var r3 = Round(new RectangleF(4f, 13f, 7f, 7f), 1.3f)) g.FillPath(fill, r3);
                        using (var r4 = Round(new RectangleF(13f, 13f, 7f, 7f), 1.3f)) g.FillPath(fill, r4);
                        break;
                    case Glyph.Desktop:
                        using (var screen = Round(new RectangleF(3f, 4.2f, 18f, 12.2f), 2f))
                            g.DrawPath(p, screen);
                        g.DrawLine(p, 12f, 16.4f, 12f, 19.8f);
                        g.DrawLine(p, 7.5f, 19.8f, 16.5f, 19.8f);
                        break;
                    case Glyph.Assist:
                        using (var screen = Round(new RectangleF(3.2f, 4.5f, 17.6f, 12.8f), 2f))
                            g.DrawPath(p, screen);
                        g.DrawLine(p, 8f, 20.4f, 16f, 20.4f);
                        g.DrawLine(p, 8f, 13.4f, 15.7f, 8.2f);
                        g.DrawLines(p, new[] { new PointF(11.7f, 8.2f), new PointF(15.7f, 8.2f), new PointF(15.7f, 12.2f) });
                        break;
                    case Glyph.Star:
                        var star = new PointF[10];
                        for (int i = 0; i < star.Length; i++)
                        {
                            double angle = -Math.PI / 2 + i * Math.PI / 5;
                            float radius = i % 2 == 0 ? 8.8f : 4.3f;
                            star[i] = new PointF(12f + radius * (float)Math.Cos(angle), 12.6f + radius * (float)Math.Sin(angle));
                        }
                        if (color == Accent) g.FillPolygon(fill, star);
                        g.DrawPolygon(p, star);
                        break;
                    case Glyph.Link:
                        g.DrawLines(p, new[] { new PointF(4f, 8f), new PointF(19.5f, 8f), new PointF(15.5f, 4.2f) });
                        g.DrawLine(p, 19.5f, 8f, 15.5f, 11.8f);
                        g.DrawLines(p, new[] { new PointF(20f, 16f), new PointF(4.5f, 16f), new PointF(8.5f, 12.2f) });
                        g.DrawLine(p, 4.5f, 16f, 8.5f, 19.8f);
                        break;
                    case Glyph.Gear:
                        g.DrawEllipse(p, 6.2f, 6.2f, 11.6f, 11.6f);
                        g.DrawEllipse(p, 9.6f, 9.6f, 4.8f, 4.8f);
                        for (int i = 0; i < 6; i++)
                        {
                            double a = i * Math.PI / 3;
                            g.DrawLine(p, 12f + (float)(6.2 * Math.Cos(a)), 12f + (float)(6.2 * Math.Sin(a)),
                                12f + (float)(8.8 * Math.Cos(a)), 12f + (float)(8.8 * Math.Sin(a)));
                        }
                        break;
                    case Glyph.Help:
                        g.DrawEllipse(p, 3.5f, 3.5f, 17f, 17f);
                        g.DrawArc(p, 9.2f, 7.2f, 5.6f, 5.2f, 190, 220);
                        g.DrawLine(p, 12f, 12.8f, 12f, 14.3f);
                        g.FillEllipse(fill, 10.9f, 16.5f, 2.2f, 2.2f);
                        break;
                    case Glyph.Info:
                        g.DrawEllipse(p, 3.5f, 3.5f, 17f, 17f);
                        g.FillEllipse(fill, 10.9f, 7.3f, 2.2f, 2.2f);
                        g.DrawLine(p, 12f, 11.2f, 12f, 16.5f);
                        break;
                    case Glyph.InfoMark:
                        g.FillEllipse(fill, 10.4f, 4.6f, 3.2f, 3.2f);
                        using (var boldPen = new Pen(color, Math.Max(2.6f, strokeW * 1.25f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(boldPen, 12f, 10.5f, 12f, 18.6f);
                        break;
                    case Glyph.Refresh:
                        g.DrawArc(p, 4.2f, 4.2f, 15.6f, 15.6f, -35, 295);
                        g.DrawLines(p, new[] { new PointF(19.8f, 4f), new PointF(19.8f, 9.8f), new PointF(14.2f, 9.8f) });
                        break;
                    case Glyph.Folder:
                        g.DrawPolygon(p, new[] { new PointF(3.2f, 6.2f), new PointF(9.6f, 6.2f), new PointF(11.8f, 8.6f), new PointF(20.8f, 8.6f), new PointF(20.8f, 18.6f), new PointF(3.2f, 18.6f) });
                        break;
                    case Glyph.Arrow:
                        g.DrawLine(p, 4.5f, 12f, 19.5f, 12f);
                        g.DrawLines(p, new[] { new PointF(14f, 6.5f), new PointF(19.5f, 12f), new PointF(14f, 17.5f) });
                        break;
                    case Glyph.ChevronRight:
                        g.DrawLines(p, new[] { new PointF(9.2f, 6.2f), new PointF(15f, 12f), new PointF(9.2f, 17.8f) });
                        break;
                    case Glyph.ChevronDown:
                        g.DrawLines(p, new[] { new PointF(6.2f, 9.2f), new PointF(12f, 15f), new PointF(17.8f, 9.2f) });
                        break;
                    case Glyph.ChevronUp:
                        g.DrawLines(p, new[] { new PointF(6.2f, 14.8f), new PointF(12f, 9f), new PointF(17.8f, 14.8f) });
                        break;
                    case Glyph.Plus:
                        g.DrawLine(p, 5.2f, 12f, 18.8f, 12f);
                        g.DrawLine(p, 12f, 5.2f, 12f, 18.8f);
                        break;
                    case Glyph.Pin:
                        g.DrawPolygon(p, new[] { new PointF(8f, 4f), new PointF(16f, 4f), new PointF(15f, 9f),
                            new PointF(18f, 13.2f), new PointF(6f, 13.2f), new PointF(9f, 9f) });
                        g.DrawLine(p, 12f, 13.2f, 12f, 20.5f);
                        break;
                    case Glyph.Lock:
                        using (var body = Round(new RectangleF(5.2f, 10.6f, 13.6f, 9.4f), 2f))
                            g.DrawPath(p, body);
                        g.DrawArc(p, 8.2f, 4.4f, 7.6f, 9f, 180, 180);
                        g.DrawLine(p, 12f, 14f, 12f, 16.8f);
                        break;
                    case Glyph.Minus:
                        g.DrawLine(p, 5f, 12f, 19f, 12f);
                        break;
                    case Glyph.Maximize:
                        g.DrawRectangle(p, 5.2f, 5.2f, 13.6f, 13.6f);
                        break;
                    case Glyph.Restore:
                        g.DrawRectangle(p, 4.8f, 8.2f, 11f, 11f);
                        g.DrawLines(p, new[] { new PointF(8.2f, 8.2f), new PointF(8.2f, 4.8f), new PointF(19.2f, 4.8f), new PointF(19.2f, 15.8f), new PointF(15.8f, 15.8f) });
                        break;
                    case Glyph.Close:
                        g.DrawLine(p, 6.2f, 6.2f, 17.8f, 17.8f);
                        g.DrawLine(p, 6.2f, 17.8f, 17.8f, 6.2f);
                        break;
                    case Glyph.Shield:
                        g.DrawPolygon(p, new[] { new PointF(12f, 3.4f), new PointF(19.6f, 6.6f), new PointF(18.6f, 14f), new PointF(12f, 20.6f), new PointF(5.4f, 14f), new PointF(4.4f, 6.6f) });
                        g.DrawLines(p, new[] { new PointF(8.8f, 12f), new PointF(11.1f, 14.3f), new PointF(15.5f, 9.6f) });
                        break;
                    case Glyph.Bolt:
                        g.DrawPolygon(p, new[] { new PointF(13f, 3f), new PointF(5.8f, 13.2f), new PointF(11.6f, 13.2f), new PointF(10.8f, 21f), new PointF(18.2f, 10.8f), new PointF(12.4f, 10.8f) });
                        break;
                    case Glyph.Copy:
                        using (var front = Round(new RectangleF(8.8f, 8.8f, 11f, 11f), 1.6f))
                            g.DrawPath(p, front);
                        g.DrawLines(p, new[] { new PointF(6f, 15f), new PointF(4.2f, 15f), new PointF(4.2f, 4.2f), new PointF(15f, 4.2f), new PointF(15f, 6f) });
                        break;
                    case Glyph.Image:
                        using (var frame = Round(new RectangleF(3.2f, 5f, 17.6f, 14f), 1.8f))
                            g.DrawPath(p, frame);
                        g.FillEllipse(fill, 7.2f, 8.2f, 2.6f, 2.6f);
                        g.DrawLines(p, new[] { new PointF(4.5f, 17f), new PointF(9.2f, 12.2f), new PointF(13f, 15.2f), new PointF(16f, 11.4f), new PointF(19.6f, 17f) });
                        break;
                    case Glyph.Eye:
                        using (var eye = new GraphicsPath())
                        {
                            eye.AddBezier(3f, 12f, 7.5f, 5.8f, 16.5f, 5.8f, 21f, 12f);
                            eye.AddBezier(21f, 12f, 16.5f, 18.2f, 7.5f, 18.2f, 3f, 12f);
                            g.DrawPath(p, eye);
                        }
                        g.FillEllipse(fill, 9.6f, 9.6f, 4.8f, 4.8f);
                        break;
                    case Glyph.EyeOff:
                        g.DrawBezier(p, 11.2f, 7.3f, 15f, 6.5f, 19f, 9.2f, 21f, 12f);
                        g.DrawBezier(p, 21f, 12f, 20.3f, 13.1f, 19.6f, 13.7f, 18.6f, 14.5f);
                        g.DrawBezier(p, 5.5f, 9.5f, 4.5f, 10.1f, 3.6f, 11.1f, 3f, 12f);
                        g.DrawBezier(p, 3f, 12f, 5.2f, 15.5f, 9.8f, 17.7f, 13.4f, 16.6f);
                        g.DrawLine(p, 4.2f, 4.2f, 19.8f, 19.8f);
                        break;
                    case Glyph.Check:
                        g.DrawLines(p, new[] { new PointF(5.2f, 12.5f), new PointF(10f, 17.2f), new PointF(18.8f, 7.2f) });
                        break;
                    case Glyph.Bell:
                        g.DrawArc(p, 6.2f, 4.2f, 11.6f, 12.2f, 180, 180);
                        g.DrawLine(p, 6.2f, 10.3f, 5.2f, 16.2f);
                        g.DrawLine(p, 17.8f, 10.3f, 18.8f, 16.2f);
                        g.DrawLine(p, 3.8f, 16.2f, 20.2f, 16.2f);
                        g.DrawArc(p, 9.8f, 16.8f, 4.4f, 3.2f, 0, 180);
                        break;
                    case Glyph.Menu:
                        g.DrawLine(p, 4.5f, 7f, 19.5f, 7f);
                        g.DrawLine(p, 4.5f, 12f, 19.5f, 12f);
                        g.DrawLine(p, 4.5f, 17f, 19.5f, 17f);
                        break;
                    case Glyph.Terminal:
                        using (var term = Round(new RectangleF(3.2f, 4.8f, 17.6f, 14.4f), 1.8f))
                            g.DrawPath(p, term);
                        g.DrawLines(p, new[] { new PointF(7f, 9.5f), new PointF(10.5f, 12.3f), new PointF(7f, 15.2f) });
                        g.DrawLine(p, 12.5f, 15.2f, 17f, 15.2f);
                        break;
                    case Glyph.Network:
                        g.DrawLines(p, new[] { new PointF(5.7f, 16.2f), new PointF(12f, 7f), new PointF(18.3f, 16.2f) });
                        g.FillEllipse(fill, 9.35f, 4.35f, 5.3f, 5.3f);
                        g.FillEllipse(fill, 3.05f, 13.55f, 5.3f, 5.3f);
                        g.FillEllipse(fill, 15.65f, 13.55f, 5.3f, 5.3f);
                        break;
                    case Glyph.WarningMark:
                        g.DrawPolygon(p, new[] { new PointF(12f, 3.7f), new PointF(20.6f, 19.6f), new PointF(3.4f, 19.6f) });
                        g.DrawLine(p, 12f, 9f, 12f, 13.1f);
                        g.FillEllipse(fill, 10.9f, 15.2f, 2.2f, 2.2f);
                        break;
                    case Glyph.MoreDots:
                        g.FillEllipse(fill, 10.2f, 4.5f, 3.6f, 3.6f);
                        g.FillEllipse(fill, 10.2f, 10.2f, 3.6f, 3.6f);
                        g.FillEllipse(fill, 10.2f, 15.9f, 3.6f, 3.6f);
                        break;
                    case Glyph.Keypad:
                        using (var pad = Round(new RectangleF(5.4f, 3.4f, 13.2f, 17.2f), 2.2f))
                            g.DrawPath(p, pad);
                        for (int row = 0; row < 3; row++)
                            for (int col = 0; col < 2; col++)
                                g.FillEllipse(fill, 8f + col * 5.5f, 6.9f + row * 4f, 2.4f, 2.4f);
                        break;
                    case Glyph.Activity:
                        g.DrawLines(p, new[] { new PointF(3.4f, 12.5f), new PointF(7.8f, 12.5f), new PointF(10.8f, 5.4f),
                            new PointF(14f, 18.6f), new PointF(16.8f, 12.5f), new PointF(20.6f, 12.5f) });
                        break;
                    case Glyph.Sliders:
                        g.DrawLine(p, 3.5f, 8f, 7f, 8f);
                        g.DrawLine(p, 12f, 8f, 20.5f, 8f);
                        g.DrawEllipse(p, 7f, 5.5f, 5f, 5f);
                        g.DrawLine(p, 3.5f, 16f, 12f, 16f);
                        g.DrawLine(p, 17f, 16f, 20.5f, 16f);
                        g.DrawEllipse(p, 12f, 13.5f, 5f, 5f);
                        break;
                    case Glyph.Mouse:
                        using (var mouse = Round(new RectangleF(6.4f, 3.6f, 11.2f, 16.8f), 5.4f))
                            g.DrawPath(p, mouse);
                        g.DrawLine(p, 12f, 7f, 12f, 10.2f);
                        break;
                    case Glyph.Volume:
                    case Glyph.VolumeOff:
                        g.DrawPolygon(p, new[] { new PointF(3.6f, 9.2f), new PointF(7.4f, 9.2f), new PointF(12.2f, 5.1f),
                            new PointF(12.2f, 18.9f), new PointF(7.4f, 14.8f), new PointF(3.6f, 14.8f) });
                        if (icon == Glyph.Volume) g.DrawArc(p, 10f, 5.8f, 10.1f, 12.4f, -58f, 116f);
                        else
                        {
                            g.DrawLine(p, 15.4f, 9.6f, 20.3f, 14.4f);
                            g.DrawLine(p, 15.4f, 14.4f, 20.3f, 9.6f);
                        }
                        break;
                    case Glyph.Expand:
                        g.DrawLines(p, new[] { new PointF(4.6f, 9.2f), new PointF(4.6f, 4.6f), new PointF(9.2f, 4.6f) });
                        g.DrawLines(p, new[] { new PointF(14.8f, 4.6f), new PointF(19.4f, 4.6f), new PointF(19.4f, 9.2f) });
                        g.DrawLines(p, new[] { new PointF(4.6f, 14.8f), new PointF(4.6f, 19.4f), new PointF(9.2f, 19.4f) });
                        g.DrawLines(p, new[] { new PointF(14.8f, 19.4f), new PointF(19.4f, 19.4f), new PointF(19.4f, 14.8f) });
                        break;
                    case Glyph.SwitchDisplay:
                        using (var frontScreen = Round(new RectangleF(3.6f, 8.3f, 13.3f, 10.8f), 1.8f))
                            g.DrawPath(p, frontScreen);
                        g.DrawLines(p, new[] { new PointF(7.5f, 5f), new PointF(20.4f, 5f), new PointF(20.4f, 15.1f) });
                        break;
                    case Glyph.Keyboard:
                        using (var keys = Round(new RectangleF(3.1f, 6f, 17.8f, 12f), 2f))
                            g.DrawPath(p, keys);
                        for (int col = 0; col < 3; col++) g.FillEllipse(fill, 6.4f + col * 4.3f, 8.9f, 2.5f, 2.5f);
                        g.DrawLine(p, 7.6f, 14.2f, 16.4f, 14.2f);
                        break;
                    case Glyph.Camera:
                        using (var camera = Round(new RectangleF(3.4f, 6.8f, 17.2f, 12f), 2f))
                            g.DrawPath(p, camera);
                        g.DrawLine(p, 9f, 4.6f, 15f, 4.6f);
                        g.DrawEllipse(p, 9f, 9.7f, 6f, 6f);
                        break;
                    case Glyph.Mic:
                        using (var mic = Round(new RectangleF(8.7f, 3.6f, 6.6f, 10.7f), 3.3f))
                            g.DrawPath(p, mic);
                        g.DrawArc(p, 5.7f, 6.7f, 12.6f, 11.3f, 0f, 180f);
                        g.DrawLine(p, 12f, 18f, 12f, 20.6f);
                        break;
                    case Glyph.Window:
                        using (var window = Round(new RectangleF(3.4f, 4.6f, 17.2f, 14.8f), 2f))
                            g.DrawPath(p, window);
                        g.DrawLine(p, 3.7f, 9f, 20.3f, 9f);
                        break;
                    case Glyph.Clipboard:
                        using (var sheet = Round(new RectangleF(5.4f, 5.7f, 13.2f, 14.4f), 1.8f))
                            g.DrawPath(p, sheet);
                        using (var clip = Round(new RectangleF(8.7f, 3.3f, 6.6f, 3.7f), 1.4f))
                            g.FillPath(fill, clip);
                        g.DrawLine(p, 9f, 12f, 15f, 12f);
                        g.DrawLine(p, 9f, 15.6f, 14f, 15.6f);
                        break;
                    case Glyph.Exit:
                        g.DrawLines(p, new[] { new PointF(12.5f, 4.3f), new PointF(4.3f, 4.3f), new PointF(4.3f, 19.7f), new PointF(12.5f, 19.7f) });
                        g.DrawLine(p, 9.5f, 12f, 20.3f, 12f);
                        g.DrawLines(p, new[] { new PointF(16.5f, 8.2f), new PointF(20.3f, 12f), new PointF(16.5f, 15.8f) });
                        break;
                    case Glyph.Search:
                        g.DrawEllipse(p, 4f, 4f, 11.8f, 11.8f);
                        g.DrawLine(p, 14.3f, 14.3f, 20.1f, 20.1f);
                        break;
                    case Glyph.Trash:
                        g.DrawLine(p, 4.4f, 6.6f, 19.6f, 6.6f);
                        g.DrawLine(p, 9.5f, 4.2f, 14.5f, 4.2f);
                        using (var bin = Round(new RectangleF(6.6f, 6.6f, 10.8f, 13.2f), 1.4f))
                            g.DrawPath(p, bin);
                        g.DrawLine(p, 10.2f, 10f, 10.2f, 16.4f);
                        g.DrawLine(p, 13.8f, 10f, 13.8f, 16.4f);
                        break;
                }
            }
            g.Restore(state);
        }
    }
}
