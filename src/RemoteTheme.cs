using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DualRemote
{
    internal static class RemoteTheme
    {
        // Modern Neo-Slate Light Palette
        internal static readonly Color Workspace = Color.FromArgb(244, 246, 250);
        internal static readonly Color Paper = Color.White;
        internal static readonly Color Rail = Color.FromArgb(249, 250, 253);
        internal static readonly Color SurfaceSubtle = Color.FromArgb(241, 245, 250);
        internal static readonly Color Ink = Color.FromArgb(15, 23, 42);
        internal static readonly Color Muted = Color.FromArgb(100, 116, 139);
        internal static readonly Color Stroke = Color.FromArgb(226, 232, 240);
        internal static readonly Color StrokeStrong = Color.FromArgb(203, 213, 225);
        internal static readonly Color CardShadow = Color.FromArgb(232, 237, 246);

        internal static readonly Color Accent = Color.FromArgb(59, 91, 255);
        internal static readonly Color AccentHover = Color.FromArgb(43, 73, 234);
        internal static readonly Color AccentLight = Color.FromArgb(238, 242, 255);
        internal static readonly Color AccentBorder = Color.FromArgb(199, 210, 254);

        internal static readonly Color Success = Color.FromArgb(5, 150, 105);
        internal static readonly Color SuccessLight = Color.FromArgb(236, 253, 245);
        internal static readonly Color Warning = Color.FromArgb(180, 83, 9);
        internal static readonly Color WarningLight = Color.FromArgb(254, 243, 199);
        internal static readonly Color Alert = Color.FromArgb(220, 38, 38);
        internal static readonly Color AlertLight = Color.FromArgb(254, 242, 242);
        internal static readonly Color HeroText = Color.FromArgb(203, 218, 255);

        private static readonly Font BodyFont = new Font("Microsoft YaHei UI", 9.25f);
        private static readonly Font SmallFont = new Font("Microsoft YaHei UI", 8.5f);
        private static readonly Font LabelFont = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Bold);
        private static readonly Font SectionFont = new Font("Microsoft YaHei UI", 12.25f, FontStyle.Bold);
        private static readonly Font TitleFont = new Font("Microsoft YaHei UI", 20.5f, FontStyle.Bold);
        private static readonly Font CodeFont = new Font("Consolas", 9.5f);

        internal enum TextSize { Small, Body, Label, Section, Title, Code }
        internal enum ButtonStyle { Primary, Secondary, Navigation, Quiet, OnHero, OnHeroQuiet, Caption, Close, AccentSoft, Segmented, Chip }
        internal enum Glyph { None, Devices, Desktop, Link, Gear, Help, Refresh, Folder, Arrow, Plus, Pin, Minus, Maximize, Restore, Close, Shield, Bolt, Copy, Image, Eye, Check }
        internal enum PillTone { Neutral, Success, Warning, Alert, Info, OnHero }

        internal static Label Text(string content, TextSize size, Color color)
        {
            Font font = BodyFont;
            switch (size)
            {
                case TextSize.Small: font = SmallFont; break;
                case TextSize.Label: font = LabelFont; break;
                case TextSize.Section: font = SectionFont; break;
                case TextSize.Title: font = TitleFont; break;
                case TextSize.Code: font = CodeFont; break;
            }
            return new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                ForeColor = color,
                Font = font,
                Text = content,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };
        }

        internal static Button Action(string caption, ButtonStyle style, Glyph glyph = Glyph.None)
        {
            Font f = (style == ButtonStyle.Chip || style == ButtonStyle.Segmented) ? SmallFont : LabelFont;
            if (style == ButtonStyle.Segmented) f = LabelFont;
            return new ActionButton(style, glyph)
            {
                Text = caption,
                AccessibleName = caption,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Font = f
            };
        }

        internal static void Highlight(Button button, bool active)
        {
            var painted = button as ActionButton;
            if (painted == null) return;
            painted.Active = active;
            painted.Invalidate();
        }

        internal static void ChangeGlyph(Button button, Glyph glyph)
        {
            var painted = button as ActionButton;
            if (painted == null) return;
            painted.Icon = glyph;
            painted.Invalidate();
        }

        internal static TableLayoutPanel Grid(int columns)
        {
            return new LayoutGrid
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = 0,
                Margin = Padding.Empty,
                BackColor = Color.Transparent,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
        }

        private sealed class LayoutGrid : TableLayoutPanel
        {
            internal LayoutGrid()
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
            return new Panel { Dock = DockStyle.Fill, BackColor = Stroke, Margin = Padding.Empty, TabStop = false };
        }

        internal static ComboBox Choice(string name, bool editable)
        {
            return new ComboBox
            {
                AccessibleName = name,
                Dock = DockStyle.Fill,
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = SurfaceSubtle,
                ForeColor = Ink,
                Font = BodyFont,
                Margin = Padding.Empty
            };
        }

        internal static TextBox InputBox(string name, string text)
        {
            return new TextBox
            {
                AccessibleName = name,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = SurfaceSubtle,
                ForeColor = Ink,
                Font = BodyFont,
                Text = text ?? "",
                Margin = Padding.Empty
            };
        }

        /// <summary>圆角输入容器，消除原生 WinForms ComboBox/TextBox 硬边框，聚焦时呈现靛蓝描边。</summary>
        internal static Panel WrapInput(Control inner)
        {
            return new InputShell(inner);
        }

        private sealed class InputShell : Panel
        {
            private bool focused;

            internal InputShell(Control inner)
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Paper;
                Padding = new Padding(9, 5, 8, 4);
                DoubleBuffered = true;
                ResizeRedraw = true;
                inner.BackColor = SurfaceSubtle;
                inner.GotFocus += delegate { focused = true; Invalidate(); };
                inner.LostFocus += delegate { focused = false; Invalidate(); };
                Controls.Add(inner);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color parentBg = Parent == null ? Paper : Parent.BackColor;
                if (parentBg.A == 0) parentBg = Paper;
                using (var bg = new SolidBrush(parentBg)) e.Graphics.FillRectangle(bg, e.ClipRectangle);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = e.Graphics.DpiX / 96f;
                Color border = focused ? Accent : StrokeStrong;
                Surface(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 9 * scale, SurfaceSubtle, border, focused ? 1.5f : 1f);
            }
        }

        internal static void Cue(ComboBox field, string text)
        {
            field.HandleCreated += delegate
            {
                NativeSendMessage(field.Handle, 0x1703, IntPtr.Zero, text);
            };
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr NativeSendMessage(IntPtr handle, uint message, IntPtr flag, string text);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>动态生成矢量风应用图标（供任务栏与系统托盘使用，无需外部 .ico 文件）。</summary>
        internal static Icon CreateAppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new RectangleF(1, 1, 30, 30);
                using (var path = Round(rect, 8))
                using (var brush = new LinearGradientBrush(rect, Color.FromArgb(37, 65, 200), Color.FromArgb(74, 114, 255), 45f))
                {
                    g.FillPath(brush, path);
                }
                DrawGlyph(g, Glyph.Devices, new RectangleF(6, 6, 20, 20), Paper);
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
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
            if (radius < 1)
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

        private sealed class ActionButton : Button
        {
            private readonly ButtonStyle style;
            private bool hovered;
            private bool pressed;
            internal Glyph Icon;
            internal bool Active;

            internal ActionButton(ButtonStyle style, Glyph icon)
            {
                this.style = style;
                Icon = icon;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                    | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (Width < 3 || Height < 3) return;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var background = Color.Transparent;
                var stroke = Color.Transparent;
                var text = Ink;
                bool glow = Enabled && (hovered || pressed);
                bool caption = style == ButtonStyle.Caption || style == ButtonStyle.Close;

                if (style == ButtonStyle.Primary)
                {
                    background = !Enabled ? Color.FromArgb(209, 217, 236) : pressed ? Color.FromArgb(31, 58, 204) : glow ? AccentHover : Accent;
                    text = Enabled ? Paper : Muted;
                }
                else if (style == ButtonStyle.Secondary)
                {
                    background = pressed ? AccentLight : glow ? Color.FromArgb(246, 249, 255) : Paper;
                    stroke = glow ? AccentBorder : StrokeStrong;
                    text = Ink;
                }
                else if (style == ButtonStyle.AccentSoft)
                {
                    background = pressed ? Color.FromArgb(215, 224, 255) : glow ? Color.FromArgb(224, 232, 255) : AccentLight;
                    stroke = AccentBorder;
                    text = Accent;
                }
                else if (style == ButtonStyle.OnHero)
                {
                    background = pressed ? Color.FromArgb(216, 228, 255) : glow ? Color.FromArgb(238, 244, 255) : Paper;
                    text = Color.FromArgb(24, 48, 128);
                }
                else if (style == ButtonStyle.OnHeroQuiet)
                {
                    background = Active
                        ? Color.FromArgb(16, 185, 129)
                        : pressed ? Color.FromArgb(62, 92, 182) : glow ? Color.FromArgb(72, 106, 198) : Color.FromArgb(46, 70, 146);
                    stroke = Active ? Color.FromArgb(110, 231, 183) : Color.FromArgb(95, 125, 212);
                    text = Paper;
                }
                else if (style == ButtonStyle.Chip)
                {
                    background = Active
                        ? Accent
                        : glow ? Color.FromArgb(228, 235, 252) : SurfaceSubtle;
                    stroke = Active ? Accent : StrokeStrong;
                    text = Active ? Paper : Ink;
                }
                else if (style == ButtonStyle.Segmented)
                {
                    background = Active ? Paper : glow ? Color.FromArgb(232, 238, 248) : Color.Transparent;
                    stroke = Active ? Stroke : Color.Transparent;
                    text = Active ? Accent : Muted;
                }
                else if (style == ButtonStyle.Close && glow)
                {
                    background = Color.FromArgb(220, 38, 38);
                    text = Paper;
                }
                else if (style == ButtonStyle.Navigation)
                {
                    background = Active ? AccentLight : glow ? Color.FromArgb(238, 242, 249) : Color.Transparent;
                    text = Active ? Accent : glow ? Ink : Color.FromArgb(51, 65, 85);
                }
                else if (Active || glow)
                {
                    background = Active ? AccentLight : Color.FromArgb(238, 243, 252);
                    text = Active ? Accent : Ink;
                }
                else if (style == ButtonStyle.Quiet)
                {
                    text = Accent;
                }

                if (!Enabled) text = Muted;
                if (SystemInformation.HighContrast)
                {
                    background = Active ? SystemColors.Highlight : SystemColors.ButtonFace;
                    text = !Enabled ? SystemColors.GrayText : Active ? SystemColors.HighlightText : SystemColors.ControlText;
                    stroke = SystemColors.WindowFrame;
                }

                float scale = g.DpiX / 96f;
                float radius = caption ? 0f : (style == ButtonStyle.Chip ? Height * 0.48f : (style == ButtonStyle.Segmented ? 8f * scale : 9f * scale));
                Surface(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius, background, stroke);
                if (style == ButtonStyle.Navigation && Active)
                    Surface(g, new RectangleF(2f * scale, Height * 0.22f, 3.5f * scale, Height * 0.56f), 2f * scale, Accent, Color.Transparent);

                int iconSize = Math.Max(12, (int)((style == ButtonStyle.Chip ? 13 : 15) * scale));
                int padx = (int)((style == ButtonStyle.Chip ? 8 : 12) * scale);
                int width = Width - 2 * padx;
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;

                if (caption || Text.Length == 0)
                {
                    DrawGlyph(g, Icon, new RectangleF((Width - iconSize) / 2f, (Height - iconSize) / 2f, iconSize, iconSize), text);
                }
                else if (style == ButtonStyle.Navigation)
                {
                    DrawGlyph(g, Icon, new RectangleF(padx + 2 * scale, (Height - iconSize) / 2f, iconSize, iconSize), text);
                    int x = padx + iconSize + (int)(11 * scale);
                    TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(1, Width - x - padx), Height), text, flags);
                }
                else
                {
                    int x = padx;
                    if (Icon != Glyph.None)
                    {
                        int t = TextRenderer.MeasureText(g, Text, Font, new Size(Width, Height), flags).Width;
                        int gap = (int)(6 * scale);
                        x = Math.Max(5, (Width - t - iconSize - gap) / 2);
                        DrawGlyph(g, Icon, new RectangleF(x, (Height - iconSize) / 2f, iconSize, iconSize), text);
                        x += iconSize + gap;
                        width = Math.Max(1, Width - x - 5);
                    }
                    else flags |= TextFormatFlags.HorizontalCenter;
                    TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(1, width), Height), text, flags);
                }
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4));
            }
        }

        internal sealed class Card : Panel
        {
            internal Card()
            {
                BackColor = Paper;
                Padding = new Padding(20, 16, 20, 16);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                ResizeRedraw = true;
                DoubleBuffered = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color bg = Parent == null ? Workspace : Parent.BackColor;
                if (bg.A == 0) bg = Workspace;
                using (var background = new SolidBrush(bg)) e.Graphics.FillRectangle(background, e.ClipRectangle);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = e.Graphics.DpiX / 96f;
                float r = 14 * scale;
                // Soft elevation hairline underneath card
                Surface(e.Graphics, new RectangleF(1f, 2f, Width - 2f, Height - 2.5f), r, CardShadow, Color.Transparent);
                Surface(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 2.5f), r, Paper, Stroke);
            }
        }

        internal sealed class SegmentedTrack : Panel
        {
            internal SegmentedTrack()
            {
                BackColor = SurfaceSubtle;
                Padding = new Padding(4);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                ResizeRedraw = true;
                DoubleBuffered = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color bg = Parent == null ? Workspace : Parent.BackColor;
                if (bg.A == 0) bg = Workspace;
                using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.ClipRectangle);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Surface(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 11 * e.Graphics.DpiX / 96f, SurfaceSubtle, Stroke);
            }
        }

        internal sealed class Hero : Panel
        {
            internal Hero()
            {
                Padding = new Padding(24, 18, 24, 18);
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Workspace;
                ResizeRedraw = true;
                DoubleBuffered = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color bg = Parent == null ? Workspace : Parent.BackColor;
                if (bg.A == 0) bg = Workspace;
                using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.ClipRectangle);
                if (Width < 4 || Height < 4) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96f;
                var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 2.5f);
                Surface(g, new RectangleF(2f, 3f, Width - 4f, Height - 3f), 16 * scale, Color.FromArgb(40, 30, 58, 138), Color.Transparent);
                using (var path = Round(box, 16 * scale))
                using (var fade = new LinearGradientBrush(box, Color.FromArgb(17, 27, 62), Color.FromArgb(49, 82, 206), 18f))
                {
                    g.FillPath(fade, path);
                    var save = g.Save();
                    g.SetClip(path);
                    float cx = Width * 0.87f, cy = Height * 0.45f;
                    float glowR = Math.Max(70 * scale, Height * 0.72f);
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(cx - glowR, cy - glowR, glowR * 2, glowR * 2);
                        using (var pgb = new PathGradientBrush(gp))
                        {
                            pgb.CenterColor = Color.FromArgb(62, 129, 168, 255);
                            pgb.SurroundColors = new[] { Color.FromArgb(0, 49, 82, 206) };
                            g.FillPath(pgb, gp);
                        }
                    }
                    using (var pen = new Pen(Color.FromArgb(38, 219, 234, 254), 1f))
                    {
                        for (int i = 1; i <= 3; i++)
                        {
                            float r = 28 * scale + i * 24 * scale;
                            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                        }
                    }
                    DrawGlyph(g, Glyph.Desktop, new RectangleF(cx - 34 * scale, cy - 26 * scale, 48 * scale, 48 * scale), Color.FromArgb(150, 219, 234, 254));
                    DrawGlyph(g, Glyph.Bolt, new RectangleF(cx + 12 * scale, cy - 8 * scale, 26 * scale, 26 * scale), Color.FromArgb(200, 147, 197, 253));
                    g.Restore(save);
                }
            }
        }

        internal sealed class IconTile : Control
        {
            private readonly Glyph glyph;
            private readonly bool solid;
            private readonly Color customBg;
            private readonly Color customFg;

            internal IconTile(Glyph glyph, bool solid = false)
                : this(glyph, solid, solid ? Accent : AccentLight, solid ? Paper : Accent) { }

            internal IconTile(Glyph glyph, bool solid, Color bg, Color fg)
            {
                this.glyph = glyph;
                this.solid = solid;
                customBg = bg;
                customFg = fg;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                    | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Margin = Padding.Empty;
                Dock = DockStyle.Fill;
                TabStop = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = Math.Min(Width - 3, Height - 3);
                if (d < 4) return;
                float x = (Width - d) / 2f, y = (Height - d) / 2f;
                Surface(g, new RectangleF(x, y, d, d), d * 0.28f, customBg, Color.Transparent);
                DrawGlyph(g, glyph, new RectangleF(x + d * 0.24f, y + d * 0.24f, d * 0.52f, d * 0.52f), customFg);
            }
        }

        internal sealed class Badge : Label
        {
            private PillTone tone;

            internal Badge(string caption, bool healthy)
                : this(caption, healthy ? PillTone.Success : PillTone.Neutral) { }

            internal Badge(string caption, PillTone tone)
            {
                this.tone = tone;
                Text = caption;
                AccessibleName = caption;
                Dock = DockStyle.Fill;
                Font = SmallFont;
                BackColor = Color.Transparent;
                Margin = Padding.Empty;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                    | ControlStyles.SupportsTransparentBackColor, true);
            }

            internal void UpdateStatus(string caption, PillTone newTone)
            {
                Text = caption;
                AccessibleName = caption;
                tone = newTone;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96f;
                Color fg = Muted, bg = SurfaceSubtle, border = Color.Transparent;
                switch (tone)
                {
                    case PillTone.Success: fg = Success; bg = SuccessLight; border = Color.FromArgb(167, 243, 208); break;
                    case PillTone.Warning: fg = Warning; bg = WarningLight; border = Color.FromArgb(253, 230, 138); break;
                    case PillTone.Alert: fg = Alert; bg = AlertLight; border = Color.FromArgb(254, 202, 202); break;
                    case PillTone.Info: fg = Accent; bg = AccentLight; border = AccentBorder; break;
                    case PillTone.OnHero: fg = Paper; bg = Color.FromArgb(65, 255, 255, 255); border = Color.FromArgb(80, 219, 234, 254); break;
                }
                int h = Math.Min(Height - 2, (int)(24 * scale));
                if (h < 14) h = Height;
                var rect = RectangleF.FromLTRB(0.5f, (Height - h) * 0.5f, Width - 1.5f, (Height + h) * 0.5f);
                Surface(g, rect, h * 0.5f, bg, border);
                using (var dot = new SolidBrush(tone == PillTone.OnHero ? Color.FromArgb(110, 231, 183) : fg))
                    g.FillEllipse(dot, 9 * scale, Height * 0.5f - 3 * scale, 6 * scale, 6 * scale);
                TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(19 * scale), 0, Math.Max(1, Width - (int)(22 * scale)), Height),
                    fg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        internal sealed class Switch : CheckBox
        {
            internal Switch()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                    | ControlStyles.SupportsTransparentBackColor, true);
                Cursor = Cursors.Hand;
                BackColor = Color.Transparent;
            }

            protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96f;
                float h = 22 * scale, w = 42 * scale;
                float x = Width - w - 1, y = (Height - h) * 0.5f;
                Color track = !Enabled ? Color.FromArgb(226, 232, 240) : Checked ? Accent : Color.FromArgb(203, 213, 225);
                Surface(g, new RectangleF(x, y, w, h), h * 0.5f, track, Color.Transparent);
                float thumbSize = h - 6 * scale;
                float tx = Checked ? x + w - thumbSize - 3 * scale : x + 3 * scale;
                using (var shadow = new SolidBrush(Color.FromArgb(30, 15, 23, 42)))
                    g.FillEllipse(shadow, tx, y + 3.5f * scale, thumbSize, thumbSize);
                using (var thumb = new SolidBrush(Paper))
                    g.FillEllipse(thumb, tx, y + 3 * scale, thumbSize, thumbSize);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
            }
        }

        internal sealed class StatusBanner : Panel
        {
            private PillTone tone = PillTone.Info;
            private readonly Label label;

            internal StatusBanner(string initialText)
            {
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                Padding = new Padding(32, 5, 14, 5);
                BackColor = Workspace;
                DoubleBuffered = true;
                ResizeRedraw = true;
                label = Text(initialText, TextSize.Small, Muted);
                Controls.Add(label);
            }

            internal string Message { get { return label.Text; } }

            internal void Set(string text, PillTone newTone)
            {
                tone = newTone;
                label.Text = string.IsNullOrWhiteSpace(text) ? "就绪。" : text;
                label.AccessibleDescription = label.Text;
                switch (tone)
                {
                    case PillTone.Alert: label.ForeColor = Alert; break;
                    case PillTone.Warning: label.ForeColor = Warning; break;
                    case PillTone.Success: label.ForeColor = Success; break;
                    default: label.ForeColor = Muted; break;
                }
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Color pbg = Parent == null ? Workspace : Parent.BackColor;
                if (pbg.A == 0) pbg = Workspace;
                using (var b = new SolidBrush(pbg)) e.Graphics.FillRectangle(b, e.ClipRectangle);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96f;
                Color bg = Paper, border = Stroke, dotColor = Accent;
                if (tone == PillTone.Success) { bg = SuccessLight; border = Color.FromArgb(167, 243, 208); dotColor = Success; }
                else if (tone == PillTone.Warning) { bg = WarningLight; border = Color.FromArgb(253, 230, 138); dotColor = Warning; }
                else if (tone == PillTone.Alert) { bg = AlertLight; border = Color.FromArgb(254, 202, 202); dotColor = Alert; }
                Surface(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 10 * scale, bg, border);
                using (var dot = new SolidBrush(dotColor))
                    g.FillEllipse(dot, 13 * scale, Height * 0.5f - 4 * scale, 8 * scale, 8 * scale);
            }
        }

        private static void DrawGlyph(Graphics g, Glyph icon, RectangleF area, Color color)
        {
            if (icon == Glyph.None) return;
            var state = g.Save();
            g.TranslateTransform(area.X, area.Y);
            g.ScaleTransform(area.Width / 24f, area.Height / 24f);
            using (var p = new Pen(color, 1.75f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (icon)
                {
                    case Glyph.Devices: g.DrawRectangle(p, 2, 4, 14, 11); g.DrawLine(p, 6, 19, 12, 19); g.DrawLine(p, 9, 15, 9, 19); g.DrawRectangle(p, 18, 10, 4, 10); break;
                    case Glyph.Desktop: g.DrawRectangle(p, 2, 4, 20, 13); g.DrawLine(p, 12, 17, 12, 21); g.DrawLine(p, 7, 21, 17, 21); break;
                    case Glyph.Link: g.DrawLines(p, new[] { new Point(3, 7), new Point(20, 7), new Point(16, 3) }); g.DrawLine(p, 20, 7, 16, 11); g.DrawLines(p, new[] { new Point(21, 17), new Point(4, 17), new Point(8, 13) }); g.DrawLine(p, 4, 17, 8, 21); break;
                    case Glyph.Gear: g.DrawEllipse(p, 5, 5, 14, 14); g.DrawEllipse(p, 9, 9, 6, 6); for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; g.DrawLine(p, 12 + (float)(7 * Math.Cos(a)), 12 + (float)(7 * Math.Sin(a)), 12 + (float)(10 * Math.Cos(a)), 12 + (float)(10 * Math.Sin(a))); } break;
                    case Glyph.Help: g.DrawEllipse(p, 3, 3, 18, 18); g.DrawArc(p, 9, 7, 6, 6, 190, 220); g.DrawLine(p, 12, 13, 12, 14); g.DrawLine(p, 12, 17, 12.2f, 17); break;
                    case Glyph.Refresh: g.DrawArc(p, 4, 4, 16, 16, -30, 290); g.DrawLines(p, new[] { new Point(21, 3), new Point(21, 10), new Point(14, 10) }); break;
                    case Glyph.Folder: g.DrawPolygon(p, new[] { new Point(2, 6), new Point(9, 6), new Point(12, 9), new Point(21, 9), new Point(21, 19), new Point(2, 19) }); break;
                    case Glyph.Arrow: g.DrawLine(p, 4, 12, 20, 12); g.DrawLines(p, new[] { new Point(14, 6), new Point(20, 12), new Point(14, 18) }); break;
                    case Glyph.Plus: g.DrawLine(p, 5, 12, 19, 12); g.DrawLine(p, 12, 5, 12, 19); break;
                    case Glyph.Pin: g.DrawRectangle(p, 4, 10, 16, 10); g.DrawArc(p, 8, 3, 8, 12, 185, 170); g.DrawLine(p, 12, 14, 12, 17); break;
                    case Glyph.Minus: g.DrawLine(p, 5, 12, 19, 12); break;
                    case Glyph.Maximize: g.DrawRectangle(p, 5, 5, 14, 14); break;
                    case Glyph.Restore: g.DrawRectangle(p, 4, 8, 12, 12); g.DrawLines(p, new[] { new Point(8, 8), new Point(8, 4), new Point(20, 4), new Point(20, 16), new Point(16, 16) }); break;
                    case Glyph.Close: g.DrawLine(p, 6, 6, 18, 18); g.DrawLine(p, 6, 18, 18, 6); break;
                    case Glyph.Shield: g.DrawPolygon(p, new[] { new PointF(12, 3), new PointF(20, 6.5f), new PointF(19, 14), new PointF(12, 21), new PointF(5, 14), new PointF(4, 6.5f) }); g.DrawLines(p, new[] { new PointF(8.8f, 12f), new PointF(11.2f, 14.4f), new PointF(15.6f, 9.5f) }); break;
                    case Glyph.Bolt: g.DrawPolygon(p, new[] { new PointF(13, 2), new PointF(5, 13.5f), new PointF(11.5f, 13.5f), new PointF(10.5f, 22), new PointF(19, 10.5f), new PointF(12.5f, 10.5f) }); break;
                    case Glyph.Copy: g.DrawRectangle(p, 9, 9, 11, 11); g.DrawLines(p, new[] { new Point(5, 15), new Point(4, 15), new Point(4, 4), new Point(15, 4), new Point(15, 5) }); break;
                    case Glyph.Image: g.DrawRectangle(p, 3, 5, 18, 14); g.DrawEllipse(p, 7, 8, 3, 3); g.DrawLines(p, new[] { new Point(4, 17), new Point(9, 12), new Point(13, 15), new Point(16, 11), new Point(20, 17) }); break;
                    case Glyph.Eye: g.DrawArc(p, 2, 6, 20, 12, 180, 180); g.DrawArc(p, 2, 6, 20, 12, 0, 180); g.DrawEllipse(p, 9.5f, 9.5f, 5, 5); break;
                    case Glyph.Check: g.DrawLines(p, new[] { new PointF(5, 12.5f), new PointF(10, 17.5f), new PointF(19, 7) }); break;
                }
            }
            g.Restore(state);
        }
    }
}
