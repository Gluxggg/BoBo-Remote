using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    /// <summary>
    /// 新手引导状态持久化（保存于 %LOCALAPPDATA%\双端远程\onboarding.txt），
    /// 独立于 preferences.txt，确保旧用户升级也能自动体验首次引导，且支持随时重新打开。
    /// </summary>
    internal static class OnboardingStore
    {
        internal const string CurrentVersion = "v1";

        internal static string FilePath
        {
            get { return Path.Combine(RecentStore.DirPath, "onboarding.txt"); }
        }

        internal static bool IsCompleted()
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                return ParseCompleted(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch
            {
                return false;
            }
        }

        internal static void MarkCompleted()
        {
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                File.WriteAllText(FilePath, FormatCompleted(), new UTF8Encoding(false));
            }
            catch { }
        }

        internal static void Reset()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { }
        }

        internal static bool ParseCompleted(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (string.Equals(line, "completed=" + CurrentVersion, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static string FormatCompleted()
        {
            return "completed=" + CurrentVersion + "\r\n";
        }
    }

    internal sealed class OnboardingStep
    {
        internal string Title;
        internal string Description;
        internal string Tip;
        internal Action Prepare;
        internal Func<Rectangle> ResolveTargetClientRect;
        internal int PaddingPx = 6;
    }

    internal enum CardPlacementSide
    {
        Right,
        Bottom,
        Top,
        Left,
        Center
    }

    internal struct CardLayoutResult
    {
        internal RectangleF CardRect;
        internal CardPlacementSide Side;
        internal float ArrowCenter;
    }

    internal enum OnboardingHitTarget
    {
        None,
        Skip,
        Prev,
        Next,
        Dot
    }

    internal sealed class CardMetrics
    {
        internal int CardWidth;
        internal int CardHeight;
        internal int Inset;
        internal int ContentWidth;
        internal int ContentHeight;
        internal float FontScale = 1f;
        internal Rectangle HeaderRect;
        internal Rectangle TitleRect;
        internal Rectangle BodyRect;
        internal Rectangle TipBoxRect;
        internal Rectangle TipTextRect;
        internal Rectangle FooterRect;
        internal bool HasTip;
    }

    /// <summary>
    /// 灰白框选式交互新手引导遮罩（双层架构：杜绝 UpdateLayeredWindow 字体发虚！）：
    /// - 底层（WS_EX_LAYERED Per-Pixel Alpha）：绘制半透明深灰遮罩、100% 透明镂空高亮框、柔光白蓝边、卡片阴影与指向三角；
    /// - 顶层（原生不透明 GDI ClearType 卡片窗体）：坐落于纯白卡片内部，使用原生 ClearType（TextRenderer.DrawText）
    ///   按物理 DPI 像素字号渲染全部中文文案与交互按钮，在 100%~200% 任意缩放下都锐利如原生界面。
    /// </summary>
    internal sealed class OnboardingOverlayForm : Form
    {
        private const int WsExLayered = 0x00080000;
        private const int WsExToolWindow = 0x00000080;
        private const int UlwAlpha = 0x02;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
            public NativePoint(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
            public NativeSize(int w, int h) { Width = w; Height = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
            IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const int DwmwaNcRenderingPolicy = 2;
        private const int DwmncrpDisabled = 1;
        private const int DwmwaTransitionsForceDisabled = 3;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpDoNotRound = 1;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        private static void ConfigureWin11OverlayWindow(IntPtr handle, bool disableRounding, bool disableBorderAndShadow)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                int disableAnim = 1;
                DwmSetWindowAttribute(handle, DwmwaTransitionsForceDisabled, ref disableAnim, sizeof(int));
            }
            catch { }
            if (disableRounding)
            {
                try
                {
                    int noRound = DwmwcpDoNotRound;
                    DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref noRound, sizeof(int));
                }
                catch { }
            }
            if (disableBorderAndShadow)
            {
                try
                {
                    int noBorder = DwmwaColorNone;
                    DwmSetWindowAttribute(handle, DwmwaBorderColor, ref noBorder, sizeof(int));
                }
                catch { }
                try
                {
                    int noNc = DwmncrpDisabled;
                    DwmSetWindowAttribute(handle, DwmwaNcRenderingPolicy, ref noNc, sizeof(int));
                }
                catch { }
            }
        }

        private readonly Form hostForm;
        private readonly IList<OnboardingStep> steps;
        private readonly Action onFinished;
        private readonly bool markCompleteOnExit;
        private readonly OnboardingCardContentForm cardWindow;

        private int currentIndex;
        private Rectangle targetRect;
        private bool finished;
        private bool syncPending;

        internal OnboardingOverlayForm(Form owner, IList<OnboardingStep> steps, bool markCompleteOnExit, Action onFinished)
        {
            this.hostForm = owner;
            this.steps = steps ?? new List<OnboardingStep>();
            this.markCompleteOnExit = markCompleteOnExit;
            this.onFinished = onFinished;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Owner = owner;

            cardWindow = new OnboardingCardContentForm(this);

            hostForm.LocationChanged += OnHostGeometryChanged;
            hostForm.SizeChanged += OnHostGeometryChanged;
            hostForm.FormClosing += OnHostClosing;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExToolWindow;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ConfigureWin11OverlayWindow(Handle, true, false);
        }

        internal void NotifyHostContentChanged()
        {
            if (finished || IsDisposed || !Visible) return;
            ScheduleRefreshStepVisual();
        }

        private float CurrentScale()
        {
            return Theme.GetDpiScale((hostForm != null && !hostForm.IsDisposed) ? hostForm : (Control)this);
        }

        internal void StartTour()
        {
            if (steps.Count == 0 || hostForm == null || hostForm.IsDisposed)
            {
                CloseTour(false);
                return;
            }
            SyncBoundsToHost();
            Show(hostForm);
            GoToStep(0);
        }

        private void OnHostGeometryChanged(object sender, EventArgs e)
        {
            if (finished || IsDisposed || hostForm == null || hostForm.IsDisposed) return;
            if (hostForm.WindowState == FormWindowState.Minimized)
            {
                if (cardWindow != null && !cardWindow.IsDisposed && cardWindow.Visible) cardWindow.Hide();
                if (Visible) Hide();
                return;
            }
            if (!Visible) Show(hostForm);
            if (cardWindow != null && !cardWindow.IsDisposed && !cardWindow.Visible) cardWindow.Show(this);
            SyncBoundsToHost();
            ScheduleRefreshStepVisual();
        }

        private void OnHostClosing(object sender, FormClosingEventArgs e)
        {
            CloseTour(false);
        }

        private void SyncBoundsToHost()
        {
            if (hostForm == null || hostForm.IsDisposed) return;
            Point origin = hostForm.PointToScreen(Point.Empty);
            Size sz = hostForm.ClientSize;
            if (sz.Width < 100 || sz.Height < 100) return;
            var exact = new Rectangle(origin.X, origin.Y, sz.Width, sz.Height);
            Bounds = exact;
            if (IsHandleCreated)
            {
                SetWindowPos(Handle, IntPtr.Zero, exact.X, exact.Y, exact.Width, exact.Height, SwpNoZOrder | SwpNoActivate);
            }
        }

        private void ScheduleRefreshStepVisual()
        {
            if (syncPending || IsDisposed) return;
            syncPending = true;
            BeginInvoke((MethodInvoker)delegate
            {
                syncPending = false;
                if (finished || IsDisposed) return;
                RefreshCurrentStepTargetAndPaint();
            });
        }

        internal void GoToStep(int index)
        {
            if (finished || IsDisposed) return;
            if (index < 0) index = 0;
            if (index >= steps.Count)
            {
                CloseTour(true);
                return;
            }
            currentIndex = index;

            OnboardingStep step = steps[currentIndex];
            if (step.Prepare != null)
            {
                try { step.Prepare(); }
                catch { }
            }
            ScheduleRefreshStepVisual();
        }

        internal void NextStep() { GoToStep(currentIndex + 1); }
        internal void PrevStep() { if (currentIndex > 0) GoToStep(currentIndex - 1); }
        internal void SkipTour() { CloseTour(true); }

        private void RefreshCurrentStepTargetAndPaint()
        {
            if (finished || IsDisposed || hostForm == null || hostForm.IsDisposed) return;
            try { hostForm.PerformLayout(); } catch { }
            SyncBoundsToHost();
            OnboardingStep step = steps[currentIndex];
            Rectangle rect = Rectangle.Empty;
            if (step.ResolveTargetClientRect != null)
            {
                try { rect = step.ResolveTargetClientRect(); }
                catch { rect = Rectangle.Empty; }
            }
            float scale = CurrentScale();
            int pad = (int)Math.Round(step.PaddingPx * scale);
            if (!rect.IsEmpty && rect.Width > 8 && rect.Height > 8)
            {
                rect.Inflate(pad, pad);
                rect = Rectangle.Intersect(rect, new Rectangle(4, 4, Math.Max(10, Width - 8), Math.Max(10, Height - 8)));
            }
            targetRect = rect;

            CardMetrics metrics = MeasureCardMetrics(new Size(Width, Height), scale, step);
            CardLayoutResult layout = ComputeCardPlacement(new Size(Width, Height), targetRect, new SizeF(metrics.CardWidth, metrics.CardHeight), scale);

            // 若紧凑笔记本窗口约束了卡片宽度，按实际落地宽度重新度量文字换行高度，杜绝裁剪底部按钮
            int placedW = (int)Math.Round(layout.CardRect.Width);
            if (placedW > 120 && Math.Abs(placedW - metrics.CardWidth) >= 4)
            {
                metrics = MeasureCardMetricsForWidth(new Size(Width, Height), placedW, scale, step);
                layout = ComputeCardPlacement(new Size(Width, Height), targetRect, new SizeF(metrics.CardWidth, metrics.CardHeight), scale);
            }

            // 1. Render anti-aliased scrim + spotlight cutout + soft shadow + rounded white card shell + arrow on layered window
            bool roundOuterCorners = hostForm != null && !hostForm.IsDisposed && hostForm.WindowState != FormWindowState.Maximized;
            using (Bitmap backdrop = RenderScrimAndShell(new Size(Width, Height), scale, targetRect, layout, roundOuterCorners))
            {
                ApplyBitmapToLayeredWindow(backdrop);
            }

            // 2. Position native opaque ClearType card content form cleanly inside the pure-white interior of cardRect
            if (cardWindow != null && !cardWindow.IsDisposed)
            {
                int cx = Left + (int)Math.Round(layout.CardRect.X) + metrics.Inset;
                int cy = Top + (int)Math.Round(layout.CardRect.Y) + metrics.Inset;
                int cw = Math.Max(40, Math.Min(metrics.ContentWidth, (int)Math.Round(layout.CardRect.Width) - metrics.Inset * 2));
                int ch = Math.Max(40, Math.Min(metrics.ContentHeight, (int)Math.Round(layout.CardRect.Height) - metrics.Inset * 2));
                var screenRect = new Rectangle(cx, cy, cw, ch);
                cardWindow.UpdateStep(step, currentIndex, steps.Count, scale, metrics, screenRect);
                if (!cardWindow.Visible)
                {
                    cardWindow.Show(this);
                    cardWindow.ApplyExactBounds(screenRect);
                    cardWindow.Activate();
                }
                else
                {
                    cardWindow.ApplyExactBounds(screenRect);
                    cardWindow.BringToFront();
                }
            }
        }

        private void CloseTour(bool completed)
        {
            if (finished) return;
            finished = true;

            if (markCompleteOnExit || completed)
                OnboardingStore.MarkCompleted();

            try
            {
                if (hostForm != null)
                {
                    hostForm.LocationChanged -= OnHostGeometryChanged;
                    hostForm.SizeChanged -= OnHostGeometryChanged;
                    hostForm.FormClosing -= OnHostClosing;
                }
            }
            catch { }

            try
            {
                if (cardWindow != null && !cardWindow.IsDisposed) cardWindow.Close();
            }
            catch { }

            try
            {
                if (!IsDisposed) Close();
            }
            catch { }

            if (onFinished != null)
            {
                try { onFinished(); }
                catch { }
            }
        }

        internal void HandleTourKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                SkipTour();
            }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                NextStep();
            }
            else if (e.KeyCode == Keys.Left && currentIndex > 0)
            {
                e.Handled = true;
                PrevStep();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            HandleTourKey(e);
        }

        private void ApplyBitmapToLayeredWindow(Bitmap bitmap)
        {
            if (IsDisposed || !IsHandleCreated) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);
                var size = new NativeSize(bitmap.Width, bitmap.Height);
                var srcPt = new NativePoint(0, 0);
                var topPos = new NativePoint(Left, Top);
                var blend = new BlendFunction
                {
                    BlendOp = AcSrcOver,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AcSrcAlpha
                };
                UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref srcPt, 0, ref blend, UlwAlpha);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        internal static CardLayoutResult ComputeCardPlacement(Size viewport, Rectangle target, SizeF cardSize, float scale)
        {
            float margin = 14f * scale;
            float gap = 14f * scale;
            float maxCardW = Math.Max(220f * scale, viewport.Width - margin * 2f);
            float cardW = Math.Min(cardSize.Width, maxCardW);
            float cardH = Math.Min(cardSize.Height, Math.Max(160f * scale, viewport.Height - margin * 2f));

            if (target.IsEmpty || target.Width < 10 || target.Height < 10)
            {
                return new CardLayoutResult
                {
                    CardRect = new RectangleF((viewport.Width - cardW) * 0.5f, (viewport.Height - cardH) * 0.5f, cardW, cardH),
                    Side = CardPlacementSide.Center,
                    ArrowCenter = 0f
                };
            }

            float txCenter = target.Left + target.Width * 0.5f;
            float tyCenter = target.Top + target.Height * 0.5f;

            if (target.Right + gap + cardW + margin <= viewport.Width)
            {
                float x = target.Right + gap;
                float y = Clamp(tyCenter - cardH * 0.42f, margin, viewport.Height - margin - cardH);
                float arrowY = Clamp(tyCenter, y + 26f * scale, y + cardH - 26f * scale);
                return new CardLayoutResult { CardRect = new RectangleF(x, y, cardW, cardH), Side = CardPlacementSide.Right, ArrowCenter = arrowY };
            }
            if (target.Bottom + gap + cardH + margin <= viewport.Height)
            {
                float x = Clamp(txCenter - cardW * 0.5f, margin, viewport.Width - margin - cardW);
                float y = target.Bottom + gap;
                float arrowX = Clamp(txCenter, x + 28f * scale, x + cardW - 28f * scale);
                return new CardLayoutResult { CardRect = new RectangleF(x, y, cardW, cardH), Side = CardPlacementSide.Bottom, ArrowCenter = arrowX };
            }
            if (target.Top - gap - cardH >= margin)
            {
                float x = Clamp(txCenter - cardW * 0.5f, margin, viewport.Width - margin - cardW);
                float y = target.Top - gap - cardH;
                float arrowX = Clamp(txCenter, x + 28f * scale, x + cardW - 28f * scale);
                return new CardLayoutResult { CardRect = new RectangleF(x, y, cardW, cardH), Side = CardPlacementSide.Top, ArrowCenter = arrowX };
            }
            if (target.Left - gap - cardW >= margin)
            {
                float x = target.Left - gap - cardW;
                float y = Clamp(tyCenter - cardH * 0.42f, margin, viewport.Height - margin - cardH);
                float arrowY = Clamp(tyCenter, y + 26f * scale, y + cardH - 26f * scale);
                return new CardLayoutResult { CardRect = new RectangleF(x, y, cardW, cardH), Side = CardPlacementSide.Left, ArrowCenter = arrowY };
            }

            float spaceBelow = viewport.Height - target.Bottom;
            float spaceAbove = target.Top;
            if (spaceBelow >= spaceAbove)
            {
                float x = Clamp(txCenter - cardW * 0.5f, margin, viewport.Width - margin - cardW);
                float y = Clamp(target.Bottom + gap, margin, viewport.Height - margin - cardH);
                float arrowX = Clamp(txCenter, x + 28f * scale, x + cardW - 28f * scale);
                return new CardLayoutResult { CardRect = new RectangleF(x, y, cardW, cardH), Side = CardPlacementSide.Bottom, ArrowCenter = arrowX };
            }
            else
            {
                float x = Clamp(txCenter - cardW * 0.5f, margin, viewport.Width - margin - cardW);
                float y = Clamp(target.Top - gap - cardH, margin, viewport.Height - margin - cardH);
                float arrowX = Clamp(txCenter, x + 28f * scale, x + cardW - 28f * scale);
                return new CardLayoutResult { CardRect = new RectangleF(x, y, cardW, cardH), Side = CardPlacementSide.Top, ArrowCenter = arrowX };
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (max < min) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static TextFormatFlags WrapFlags
        {
            get { return TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding; }
        }

        internal static Font CreatePixelFont(float px96, float scale, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", Math.Max(9f, px96 * scale), style, GraphicsUnit.Pixel);
        }

        internal static CardMetrics MeasureCardMetrics(Size viewport, float scale, OnboardingStep step)
        {
            int maxAllowedW = Math.Max((int)Math.Round(240f * scale), viewport.Width - (int)Math.Round(28f * scale));
            int cardW = (int)Math.Round(Math.Min(386f * scale, Math.Max(272f * scale, Math.Min(386f * scale, maxAllowedW))));
            cardW = Math.Min(cardW, maxAllowedW);
            return MeasureCardMetricsForWidth(viewport, cardW, scale, step);
        }

        private static CardMetrics MeasureCardMetricsForWidth(Size viewport, int cardW, float scale, OnboardingStep step)
        {
            CardMetrics normal = BuildCardMetrics(cardW, scale, step, false, 1f);
            int maxAllowedH = viewport.Height - (int)Math.Round(28f * scale);
            if (maxAllowedH <= 120 || normal.CardHeight <= maxAllowedH)
                return normal;

            CardMetrics compact = BuildCardMetrics(cardW, scale, step, true, 1f);
            if (compact.CardHeight <= maxAllowedH)
                return compact;

            int maxAllowedW = Math.Max(cardW, Math.Min((int)Math.Round(480f * scale), viewport.Width - (int)Math.Round(28f * scale)));
            if (maxAllowedW > cardW + (int)Math.Round(12f * scale))
            {
                CardMetrics wider = BuildCardMetrics(maxAllowedW, scale, step, true, 1f);
                if (wider.CardHeight <= maxAllowedH)
                    return wider;
                compact = wider;
                cardW = maxAllowedW;
            }

            foreach (float fs in new[] { 0.92f, 0.85f, 0.78f })
            {
                CardMetrics scaled = BuildCardMetrics(cardW, scale, step, true, fs);
                if (scaled.CardHeight <= maxAllowedH)
                    return scaled;
                compact = scaled;
            }
            return compact;
        }

        private static CardMetrics BuildCardMetrics(int cardW, float scale, OnboardingStep step, bool compactSpacing, float fontScale)
        {
            float vFactor = compactSpacing ? 0.65f : 1f;
            float fScale = scale * fontScale;
            int inset = Math.Max(6, (int)Math.Round((compactSpacing ? 6f : 8f) * scale));
            int padX = Math.Max(10, (int)Math.Round(12f * scale));
            int padY = Math.Max(6, (int)Math.Round(10f * scale * vFactor));
            int contentW = Math.Max(160, cardW - inset * 2);
            int innerW = Math.Max(140, contentW - padX * 2);

            int headerH = (int)Math.Round(26f * scale);
            int footerH = (int)Math.Round((compactSpacing ? 30f : 34f) * scale);
            int gapHeader = (int)Math.Round(10f * scale * vFactor);
            int gapTitle = (int)Math.Round(8f * scale * vFactor);
            int gapTip = (int)Math.Round(12f * scale * vFactor);
            int gapFooter = (int)Math.Round(16f * scale * vFactor);
            int tipPadX = (int)Math.Round(10f * scale);
            int tipTextLeft = (int)Math.Round(22f * scale);
            int tipPadY = (int)Math.Round(7f * scale * vFactor);

            using (Graphics screenG = Graphics.FromHwnd(IntPtr.Zero))
            using (var titleFont = CreatePixelFont(15.5f, fScale, FontStyle.Bold))
            using (var bodyFont = CreatePixelFont(13.25f, fScale, FontStyle.Regular))
            using (var tipFont = CreatePixelFont(12f, fScale, FontStyle.Regular))
            {
                Size titleSz = TextRenderer.MeasureText(screenG, step.Title ?? "", titleFont, new Size(innerW, int.MaxValue), WrapFlags);
                Size bodySz = TextRenderer.MeasureText(screenG, step.Description ?? "", bodyFont, new Size(innerW, int.MaxValue), WrapFlags);
                bool hasTip = !string.IsNullOrWhiteSpace(step.Tip);
                int tipInnerW = Math.Max(80, innerW - tipTextLeft - tipPadX);
                Size tipSz = hasTip ? TextRenderer.MeasureText(screenG, step.Tip, tipFont, new Size(tipInnerW, int.MaxValue), WrapFlags) : Size.Empty;
                int tipBoxH = hasTip ? (tipSz.Height + tipPadY * 2 + (int)Math.Ceiling(2f * scale)) : 0;

                int y = padY;
                var m = new CardMetrics();
                m.Inset = inset;
                m.CardWidth = cardW;
                m.ContentWidth = contentW;
                m.FontScale = fontScale;
                m.HasTip = hasTip;

                m.HeaderRect = new Rectangle(padX, y, innerW, headerH);
                y += headerH + gapHeader;

                m.TitleRect = new Rectangle(padX, y, innerW, titleSz.Height + (int)Math.Ceiling(3f * scale));
                y += m.TitleRect.Height + gapTitle;

                m.BodyRect = new Rectangle(padX, y, innerW, bodySz.Height + (int)Math.Ceiling(4f * scale));
                y += m.BodyRect.Height;

                if (hasTip)
                {
                    y += gapTip;
                    m.TipBoxRect = new Rectangle(padX, y, innerW, tipBoxH);
                    m.TipTextRect = new Rectangle(padX + tipTextLeft, y + tipPadY, tipInnerW, tipSz.Height + (int)Math.Ceiling(2f * scale));
                    y += tipBoxH;
                }

                y += gapFooter;
                m.FooterRect = new Rectangle(padX, y, innerW, footerH);
                y += footerH + padY;

                m.ContentHeight = y;
                m.CardHeight = y + inset * 2;
                return m;
            }
        }

        private static Bitmap RenderScrimAndShell(Size viewport, float scale, Rectangle target, CardLayoutResult layout, bool roundOuterCorners)
        {
            var bmp = new Bitmap(viewport.Width, viewport.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0, 0, 0, 0));
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // 1. Translucent slate-gray scrim with crisp rounded spotlight cutout ("灰白框选")
                float spotRadius = 10f * scale;
                RectangleF spotRect = new RectangleF(target.X, target.Y, target.Width, target.Height);
                bool hasSpot = target.Width > 8 && target.Height > 8;

                using (var scrimPath = new GraphicsPath(FillMode.Alternate))
                {
                    if (roundOuterCorners)
                    {
                        using (var outer = Theme.Round(new RectangleF(0, 0, viewport.Width, viewport.Height), 8f * scale))
                            scrimPath.AddPath(outer, false);
                    }
                    else
                    {
                        scrimPath.AddRectangle(new RectangleF(0, 0, viewport.Width, viewport.Height));
                    }
                    if (hasSpot)
                    {
                        using (var hole = Theme.Round(spotRect, spotRadius))
                            scrimPath.AddPath(hole, false);
                    }
                    using (var scrimBrush = new SolidBrush(Color.FromArgb(146, 15, 23, 38)))
                        g.FillPath(scrimBrush, scrimPath);
                }

                if (hasSpot)
                {
                    using (var hole = Theme.Round(spotRect, spotRadius))
                    using (var hitGuard = new SolidBrush(Color.FromArgb(1, 255, 255, 255)))
                        g.FillPath(hitGuard, hole);

                    using (var outerHalo = Theme.Round(RectangleF.Inflate(spotRect, 2.5f * scale, 2.5f * scale), spotRadius + 2f * scale))
                    using (var haloPen = new Pen(Color.FromArgb(88, 255, 255, 255), 3.5f * scale))
                        g.DrawPath(haloPen, outerHalo);

                    using (var whiteRing = Theme.Round(spotRect, spotRadius))
                    using (var whitePen = new Pen(Color.FromArgb(245, 255, 255, 255), 2.2f * scale))
                        g.DrawPath(whitePen, whiteRing);

                    using (var accentRing = Theme.Round(RectangleF.Inflate(spotRect, -1.2f * scale, -1.2f * scale), Math.Max(2f, spotRadius - 1.2f * scale)))
                    using (var accentPen = new Pen(Color.FromArgb(185, 51, 122, 255), 1.1f * scale))
                        g.DrawPath(accentPen, accentRing);
                }

                // 2. Soft layered drop shadow + anti-aliased pure-white rounded card shell + directional arrow
                RectangleF card = layout.CardRect;
                float cardRadius = 12f * scale;
                float arrowSize = 9f * scale;

                for (int layer = 3; layer >= 1; layer--)
                {
                    float spread = layer * 2.5f * scale;
                    var shadowRect = new RectangleF(card.X - spread * 0.45f, card.Y + layer * 2.4f * scale, card.Width + spread * 0.9f, card.Height + spread * 0.6f);
                    using (var sp = Theme.Round(shadowRect, cardRadius + spread * 0.4f))
                    using (var sb = new SolidBrush(Color.FromArgb(14 + (4 - layer) * 8, 12, 24, 46)))
                        g.FillPath(sb, sp);
                }

                using (var cardPath = BuildCardWithArrowPath(card, cardRadius, layout.Side, layout.ArrowCenter, arrowSize))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                        g.FillPath(fill, cardPath);
                    using (var borderPen = new Pen(Color.FromArgb(255, 220, 230, 240), 1.1f * scale))
                        g.DrawPath(borderPen, cardPath);
                }
            }
            return bmp;
        }

        internal static void PaintCardContentSurface(
            Graphics g, CardMetrics m, OnboardingStep step, int stepIndex, int totalSteps, float scale,
            OnboardingHitTarget hover, int hoverDot, OnboardingHitTarget pressed,
            out Rectangle skipRect, out Rectangle prevRect, out Rectangle nextRect, out Rectangle[] dotRects)
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float fScale = scale * (m != null && m.FontScale > 0.5f ? m.FontScale : 1f);
            using (var badgeFont = CreatePixelFont(11.5f, scale, FontStyle.Bold))
            using (var titleFont = CreatePixelFont(15.5f, fScale, FontStyle.Bold))
            using (var bodyFont = CreatePixelFont(13.25f, fScale, FontStyle.Regular))
            using (var tipFont = CreatePixelFont(12f, fScale, FontStyle.Regular))
            using (var btnFont = CreatePixelFont(12.75f, scale, FontStyle.Bold))
            using (var btnLightFont = CreatePixelFont(12.75f, scale, FontStyle.Regular))
            {
                // Header: optional BoBo mini logo (step 1) + step pill + "跳过引导"
                int badgeX = m.HeaderRect.Left;
                if (stepIndex == 0)
                {
                    int logoD = (int)Math.Round(24f * scale);
                    Theme.DrawBrandMark(g, new RectangleF(badgeX, m.HeaderRect.Top + (m.HeaderRect.Height - logoD) * 0.5f, logoD, logoD));
                    badgeX += logoD + (int)Math.Round(8f * scale);
                }

                string stepTag = string.Format("步骤 {0} / {1}", stepIndex + 1, totalSteps);
                Size tagSz = TextRenderer.MeasureText(g, stepTag, badgeFont, new Size(int.MaxValue, m.HeaderRect.Height),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                int tagH = Math.Max(20, m.HeaderRect.Height - (int)Math.Round(2f * scale));
                var tagRect = new Rectangle(badgeX, m.HeaderRect.Top + (m.HeaderRect.Height - tagH) / 2, tagSz.Width + (int)Math.Round(16f * scale), tagH);
                Theme.Surface(g, tagRect, tagH * 0.5f, Theme.AccentLight, Theme.AccentBorder, 1f);
                TextRenderer.DrawText(g, stepTag, badgeFont, tagRect, Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                string skipText = "跳过引导";
                Size skipSz = TextRenderer.MeasureText(g, skipText, btnLightFont, new Size(int.MaxValue, m.HeaderRect.Height),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                int skipW = skipSz.Width + (int)Math.Round(12f * scale);
                skipRect = new Rectangle(m.HeaderRect.Right - skipW, m.HeaderRect.Top, skipW, m.HeaderRect.Height);
                if (hover == OnboardingHitTarget.Skip)
                    Theme.Surface(g, skipRect, 6f * scale, Theme.SurfaceSubtle, Color.Transparent);
                TextRenderer.DrawText(g, skipText, btnLightFont, skipRect, hover == OnboardingHitTarget.Skip ? Theme.Ink : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                // Title (crisp native ClearType)
                TextRenderer.DrawText(g, step.Title ?? "", titleFont, m.TitleRect, Color.FromArgb(17, 24, 39), WrapFlags);

                // Description (crisp high-contrast native ClearType)
                TextRenderer.DrawText(g, step.Description ?? "", bodyFont, m.BodyRect, Color.FromArgb(45, 55, 72), WrapFlags);

                // Practical Tip pill
                if (m.HasTip)
                {
                    Theme.Surface(g, m.TipBoxRect, 8f * scale, Color.FromArgb(241, 247, 254), Color.FromArgb(212, 229, 249), 1f);
                    float dotD = 6f * scale;
                    using (var dotB = new SolidBrush(Theme.Accent))
                        g.FillEllipse(dotB, m.TipBoxRect.Left + 10f * scale, m.TipBoxRect.Top + (m.TipBoxRect.Height - dotD) * 0.5f, dotD, dotD);
                    TextRenderer.DrawText(g, step.Tip, tipFont, m.TipTextRect, Color.FromArgb(30, 74, 148), WrapFlags);
                }

                // Footer: measure action buttons first so dots never overlap Prev/Next on narrow/high-DPI screens
                bool isLast = stepIndex >= totalSteps - 1;
                string nextLabel = isLast ? "开始使用" : "下一步";
                Size nextTextSz = TextRenderer.MeasureText(g, nextLabel, btnFont, new Size(int.MaxValue, m.FooterRect.Height),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                int nextW = Math.Max((int)Math.Round((isLast ? 86f : 76f) * scale), nextTextSz.Width + (int)Math.Round(24f * scale));
                nextRect = new Rectangle(m.FooterRect.Right - nextW, m.FooterRect.Top, nextW, m.FooterRect.Height);
                Color nextBg = (pressed == OnboardingHitTarget.Next) ? Theme.AccentPressed : (hover == OnboardingHitTarget.Next) ? Theme.AccentHover : Theme.Accent;
                Theme.Surface(g, nextRect, 7f * scale, nextBg, Color.Transparent);
                TextRenderer.DrawText(g, nextLabel, btnFont, nextRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                int btnLeftEdge = nextRect.Left;
                if (stepIndex > 0)
                {
                    Size prevTextSz = TextRenderer.MeasureText(g, "上一步", btnLightFont, new Size(int.MaxValue, m.FooterRect.Height),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    int prevW = Math.Max((int)Math.Round(70f * scale), prevTextSz.Width + (int)Math.Round(20f * scale));
                    prevRect = new Rectangle(nextRect.Left - (int)Math.Round(8f * scale) - prevW, m.FooterRect.Top, prevW, m.FooterRect.Height);
                    Color prevBg = (pressed == OnboardingHitTarget.Prev) ? Theme.NavHover : (hover == OnboardingHitTarget.Prev) ? Theme.SurfaceSubtle : Color.White;
                    Theme.Surface(g, prevRect, 7f * scale, prevBg, Theme.StrokeStrong, 1f);
                    TextRenderer.DrawText(g, "上一步", btnLightFont, prevRect, Theme.Ink,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    btnLeftEdge = prevRect.Left;
                }
                else
                {
                    prevRect = Rectangle.Empty;
                }

                // Clickable progress dots (auto-compact if tight so dots never touch buttons)
                dotRects = new Rectangle[totalSteps];
                int availDotW = btnLeftEdge - m.FooterRect.Left - (int)Math.Round(8f * scale);
                int dotH = Math.Max(5, (int)Math.Round(6f * scale));
                int dotGap = Math.Max(3, (int)Math.Round(6f * scale));
                int activeW = Math.Max(dotH + 4, (int)Math.Round(18f * scale));
                int totalDotW = totalSteps > 0 ? activeW + Math.Max(0, totalSteps - 1) * (dotH + dotGap) : 0;
                if (totalDotW > availDotW && totalSteps > 1)
                {
                    dotGap = Math.Max(2, (int)Math.Round(3f * scale));
                    activeW = Math.Max(dotH + 2, (int)Math.Round(12f * scale));
                    totalDotW = activeW + (totalSteps - 1) * (dotH + dotGap);
                }
                if (totalDotW <= availDotW)
                {
                    int dotX = m.FooterRect.Left;
                    int dotY = m.FooterRect.Top + (m.FooterRect.Height - dotH) / 2;
                    for (int i = 0; i < totalSteps; i++)
                    {
                        bool activeDot = (i == stepIndex);
                        int dw = activeDot ? activeW : dotH;
                        dotRects[i] = new Rectangle(dotX, dotY, dw, dotH);
                        Color dc = activeDot ? Theme.Accent : (hover == OnboardingHitTarget.Dot && hoverDot == i) ? Theme.AccentSky : Color.FromArgb(204, 214, 226);
                        Theme.Surface(g, dotRects[i], dotH * 0.5f, dc, Color.Transparent);
                        dotX += dw + dotGap;
                    }
                }
            }
        }

        /// <summary>合成整帧引导图（底层 32bpp Alpha 遮罩 + 顶层原生 ClearType 卡片内容，供自检像素验收）。</summary>
        internal static Bitmap RenderPreviewBitmap(Size viewport, float scale, Rectangle target, OnboardingStep step, int stepIndex, int totalSteps)
        {
            CardMetrics metrics = MeasureCardMetrics(viewport, scale, step);
            CardLayoutResult layout = ComputeCardPlacement(viewport, target, new SizeF(metrics.CardWidth, metrics.CardHeight), scale);
            int placedW = (int)Math.Round(layout.CardRect.Width);
            if (placedW > 120 && Math.Abs(placedW - metrics.CardWidth) >= 4)
            {
                metrics = MeasureCardMetricsForWidth(viewport, placedW, scale, step);
                layout = ComputeCardPlacement(viewport, target, new SizeF(metrics.CardWidth, metrics.CardHeight), scale);
            }
            Bitmap result = RenderScrimAndShell(viewport, scale, target, layout, false);

            // Render card content onto an opaque 24bpp ClearType surface, then copy cleanly into card interior (alpha=255)
            using (var cardBmp = new Bitmap(metrics.ContentWidth, metrics.ContentHeight, PixelFormat.Format24bppRgb))
            {
                using (Graphics cg = Graphics.FromImage(cardBmp))
                {
                    Rectangle r1, r2, r3;
                    Rectangle[] dots;
                    PaintCardContentSurface(cg, metrics, step, stepIndex, totalSteps, scale,
                        OnboardingHitTarget.None, -1, OnboardingHitTarget.None, out r1, out r2, out r3, out dots);
                }
                int dstX = (int)Math.Round(layout.CardRect.X) + metrics.Inset;
                int dstY = (int)Math.Round(layout.CardRect.Y) + metrics.Inset;
                using (Graphics rg = Graphics.FromImage(result))
                {
                    rg.CompositingMode = CompositingMode.SourceCopy;
                    rg.DrawImageUnscaled(cardBmp, dstX, dstY);
                }
            }
            return result;
        }

        private static GraphicsPath BuildCardWithArrowPath(RectangleF r, float radius, CardPlacementSide side, float arrowCenter, float arrow)
        {
            if (side == CardPlacementSide.Center || arrow < 2f)
                return Theme.Round(r, radius);

            float rad = Math.Min(radius, Math.Min(r.Width, r.Height) * 0.45f);
            float d = rad * 2f;
            var path = new GraphicsPath();

            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            if (side == CardPlacementSide.Bottom)
            {
                float ax = Clamp(arrowCenter, r.Left + rad + arrow + 2f, r.Right - rad - arrow - 2f);
                path.AddLine(r.Left + rad, r.Top, ax - arrow, r.Top);
                path.AddLine(ax - arrow, r.Top, ax, r.Top - arrow);
                path.AddLine(ax, r.Top - arrow, ax + arrow, r.Top);
                path.AddLine(ax + arrow, r.Top, r.Right - rad, r.Top);
            }
            else
            {
                path.AddLine(r.Left + rad, r.Top, r.Right - rad, r.Top);
            }

            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            if (side == CardPlacementSide.Left)
            {
                float ay = Clamp(arrowCenter, r.Top + rad + arrow + 2f, r.Bottom - rad - arrow - 2f);
                path.AddLine(r.Right, r.Top + rad, r.Right, ay - arrow);
                path.AddLine(r.Right, ay - arrow, r.Right + arrow, ay);
                path.AddLine(r.Right + arrow, ay, r.Right, ay + arrow);
                path.AddLine(r.Right, ay + arrow, r.Right, r.Bottom - rad);
            }
            else
            {
                path.AddLine(r.Right, r.Top + rad, r.Right, r.Bottom - rad);
            }

            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            if (side == CardPlacementSide.Top)
            {
                float ax = Clamp(arrowCenter, r.Left + rad + arrow + 2f, r.Right - rad - arrow - 2f);
                path.AddLine(r.Right - rad, r.Bottom, ax + arrow, r.Bottom);
                path.AddLine(ax + arrow, r.Bottom, ax, r.Bottom + arrow);
                path.AddLine(ax, r.Bottom + arrow, ax - arrow, r.Bottom);
                path.AddLine(ax - arrow, r.Bottom, r.Left + rad, r.Bottom);
            }
            else
            {
                path.AddLine(r.Right - rad, r.Bottom, r.Left + rad, r.Bottom);
            }

            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            if (side == CardPlacementSide.Right)
            {
                float ay = Clamp(arrowCenter, r.Top + rad + arrow + 2f, r.Bottom - rad - arrow - 2f);
                path.AddLine(r.Left, r.Bottom - rad, r.Left, ay + arrow);
                path.AddLine(r.Left, ay + arrow, r.Left - arrow, ay);
                path.AddLine(r.Left - arrow, ay, r.Left, ay - arrow);
                path.AddLine(r.Left, ay - arrow, r.Left, r.Top + rad);
            }
            else
            {
                path.AddLine(r.Left, r.Bottom - rad, r.Left, r.Top + rad);
            }

            path.CloseFigure();
            return path;
        }

        /// <summary>原生不透明 ClearType 文本卡片层（无 UpdateLayeredWindow 灰阶发虚，字体与主界面同等锐利）。</summary>
        private sealed class OnboardingCardContentForm : Form
        {
            private readonly OnboardingOverlayForm controller;
            private OnboardingStep step;
            private int stepIndex;
            private int totalSteps;
            private float scale = 1f;
            private CardMetrics metrics;

            private Rectangle skipRect;
            private Rectangle prevRect;
            private Rectangle nextRect;
            private Rectangle[] dotRects = new Rectangle[0];
            private OnboardingHitTarget hoverTarget = OnboardingHitTarget.None;
            private int hoverDotIndex = -1;
            private OnboardingHitTarget pressedTarget = OnboardingHitTarget.None;

            internal OnboardingCardContentForm(OnboardingOverlayForm controller)
            {
                this.controller = controller;
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.None;
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.White;
                KeyPreview = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WsExToolWindow;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ConfigureWin11OverlayWindow(Handle, true, true);
            }

            internal void ApplyExactBounds(Rectangle screenBounds)
            {
                Bounds = screenBounds;
                if (IsHandleCreated)
                {
                    SetWindowPos(Handle, IntPtr.Zero, screenBounds.X, screenBounds.Y,
                        screenBounds.Width, screenBounds.Height, SwpNoZOrder | SwpNoActivate);
                }
            }

            internal void UpdateStep(OnboardingStep newStep, int index, int total, float dpiScale, CardMetrics newMetrics, Rectangle screenBounds)
            {
                step = newStep;
                stepIndex = index;
                totalSteps = total;
                scale = dpiScale;
                metrics = newMetrics;
                hoverTarget = OnboardingHitTarget.None;
                hoverDotIndex = -1;
                pressedTarget = OnboardingHitTarget.None;
                Cursor = Cursors.Default;
                ApplyExactBounds(screenBounds);
                Invalidate();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                controller.HandleTourKey(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (step == null || metrics == null)
                {
                    e.Graphics.Clear(Color.White);
                    return;
                }
                PaintCardContentSurface(e.Graphics, metrics, step, stepIndex, totalSteps, scale,
                    hoverTarget, hoverDotIndex, pressedTarget,
                    out skipRect, out prevRect, out nextRect, out dotRects);
            }

            private OnboardingHitTarget HitTest(Point pt, out int dotIdx)
            {
                dotIdx = -1;
                if (nextRect.Contains(pt)) return OnboardingHitTarget.Next;
                if (stepIndex > 0 && prevRect.Contains(pt)) return OnboardingHitTarget.Prev;
                if (skipRect.Contains(pt)) return OnboardingHitTarget.Skip;
                for (int i = 0; i < dotRects.Length; i++)
                {
                    if (Rectangle.Inflate(dotRects[i], 3, 6).Contains(pt))
                    {
                        dotIdx = i;
                        return OnboardingHitTarget.Dot;
                    }
                }
                return OnboardingHitTarget.None;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int dotIdx;
                OnboardingHitTarget hit = HitTest(e.Location, out dotIdx);
                if (hit != hoverTarget || dotIdx != hoverDotIndex)
                {
                    hoverTarget = hit;
                    hoverDotIndex = dotIdx;
                    Cursor = hit != OnboardingHitTarget.None ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (hoverTarget != OnboardingHitTarget.None || pressedTarget != OnboardingHitTarget.None)
                {
                    hoverTarget = OnboardingHitTarget.None;
                    hoverDotIndex = -1;
                    pressedTarget = OnboardingHitTarget.None;
                    Cursor = Cursors.Default;
                    Invalidate();
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                int dotIdx;
                OnboardingHitTarget hit = HitTest(e.Location, out dotIdx);
                if (hit != OnboardingHitTarget.None)
                {
                    pressedTarget = hit;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                int dotIdx;
                OnboardingHitTarget hit = HitTest(e.Location, out dotIdx);
                OnboardingHitTarget wasPressed = pressedTarget;
                pressedTarget = OnboardingHitTarget.None;

                if (hit != OnboardingHitTarget.None && hit == wasPressed)
                {
                    if (hit == OnboardingHitTarget.Next) controller.NextStep();
                    else if (hit == OnboardingHitTarget.Prev) controller.PrevStep();
                    else if (hit == OnboardingHitTarget.Skip) controller.SkipTour();
                    else if (hit == OnboardingHitTarget.Dot && dotIdx >= 0 && dotIdx < totalSteps) controller.GoToStep(dotIdx);
                }
                else if (wasPressed != OnboardingHitTarget.None)
                {
                    Invalidate();
                }
            }
        }
    }
}
