using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace DualRemote
{
    internal enum PrivacyGuardAction
    {
        NoOp = 0,
        ActivateAuto = 1,
        DeactivateAuto = 2
    }

    /// <summary>
    /// 防窥屏状态机（纯逻辑，供 MainForm 与 SelfTest 共用）：
    /// - 任何锁屏事件（快捷键自动锁屏、Win+L、Ctrl+Alt+Del 或系统息屏锁屏）均会同步记录锁屏并关闭物理屏幕布；
    /// - 用户通过 Windows 锁屏解锁进入桌面后无条件保持防窥关闭，屏蔽本轮远控自动重拉起，并拦截解锁瞬间的快捷键/回车键回弹；
    /// - 解锁保护窗口（5s）结束后，仅在连续多次（≥3 次）观测到远控断开后才重新武装自动防窥，防止桌面切换瞬间网络/状态抖动误拉起。
    /// </summary>
    internal sealed class PrivacyGuardState
    {
        internal const int HotkeyExitDebounceMs = 2000;
        internal const int PostUnlockKeyboardGuardMs = 2500;
        internal const int PostUnlockSuppressMs = 5000;
        internal const int RearmRequiredInactivePolls = 3;

        internal bool SessionLocked { get; private set; }
        internal bool ManualDismissSuppressed { get; private set; }
        internal bool AutoActivated { get; private set; }
        internal long Generation { get; private set; }
        internal int ConsecutiveInactivePolls { get; private set; }
        internal DateTime LastManualExitUtc { get; private set; }
        internal DateTime LastUnlockUtc { get; private set; }

        internal void OnManualActivate()
        {
            Generation++;
            ManualDismissSuppressed = false;
            AutoActivated = false;
            ConsecutiveInactivePolls = 0;
        }

        internal void OnManualExit(DateTime nowUtc)
        {
            Generation++;
            ManualDismissSuppressed = true;
            AutoActivated = false;
            ConsecutiveInactivePolls = 0;
            LastManualExitUtc = nowUtc;
        }

        internal void OnAutoGuardDisabled()
        {
            Generation++;
            AutoActivated = false;
            ManualDismissSuppressed = false;
            ConsecutiveInactivePolls = 0;
        }

        internal void OnSessionLock()
        {
            OnSessionLock(false);
        }

        internal void OnSessionLock(bool curtainWasActive)
        {
            Generation++;
            SessionLocked = true;
            AutoActivated = false;
            ConsecutiveInactivePolls = 0;
            if (curtainWasActive)
            {
                ManualDismissSuppressed = true;
            }
        }

        internal void OnSessionUnlock(DateTime nowUtc)
        {
            Generation++;
            SessionLocked = false;
            AutoActivated = false;
            // 用户已通过 Windows 锁屏解锁返回桌面：必须保持防窥关闭，屏蔽当前远控会话自动重拉起
            ManualDismissSuppressed = true;
            ConsecutiveInactivePolls = 0;
            LastUnlockUtc = nowUtc;
        }

        internal bool CanActivateFromHotkey(DateTime nowUtc)
        {
            if (SessionLocked) return false;
            if (LastManualExitUtc != DateTime.MinValue
                && (nowUtc - LastManualExitUtc).TotalMilliseconds < HotkeyExitDebounceMs)
                return false;
            if (LastUnlockUtc != DateTime.MinValue
                && (nowUtc - LastUnlockUtc).TotalMilliseconds < HotkeyExitDebounceMs)
                return false;
            return true;
        }

        internal bool CanActivateFromUi(DateTime nowUtc, bool pointerTriggered)
        {
            if (SessionLocked) return false;
            if (pointerTriggered) return true;
            // 拦截非鼠标点击触发（例如锁屏输入密码按 Enter/Space 后残留按键落到原焦点按钮上）
            if (LastManualExitUtc != DateTime.MinValue
                && (nowUtc - LastManualExitUtc).TotalMilliseconds < PostUnlockKeyboardGuardMs)
                return false;
            if (LastUnlockUtc != DateTime.MinValue
                && (nowUtc - LastUnlockUtc).TotalMilliseconds < PostUnlockKeyboardGuardMs)
                return false;
            return true;
        }

        internal PrivacyGuardAction EvaluateAutoGuardPoll(long pollGeneration, bool activeSession,
            bool curtainActive, bool curtainIsPreview, bool autoOnRemote, DateTime nowUtc)
        {
            if (!autoOnRemote || pollGeneration != Generation) return PrivacyGuardAction.NoOp;
            if (SessionLocked) return PrivacyGuardAction.NoOp;

            bool inPostUnlockGrace = LastUnlockUtc != DateTime.MinValue
                && (nowUtc - LastUnlockUtc).TotalMilliseconds < PostUnlockSuppressMs;

            if (!activeSession)
            {
                if (AutoActivated && curtainActive && !curtainIsPreview)
                {
                    AutoActivated = false;
                    ConsecutiveInactivePolls = 0;
                    return PrivacyGuardAction.DeactivateAuto;
                }
                AutoActivated = false;

                if (ManualDismissSuppressed)
                {
                    // 解锁冷静期内不解除屏蔽；冷静期结束后须连续多次观测到真正无远控会话才重新武装
                    if (!inPostUnlockGrace)
                    {
                        ConsecutiveInactivePolls++;
                        if (ConsecutiveInactivePolls >= RearmRequiredInactivePolls)
                        {
                            ManualDismissSuppressed = false;
                            ConsecutiveInactivePolls = 0;
                        }
                    }
                }
                return PrivacyGuardAction.NoOp;
            }

            // 远控会话仍处于连接状态，重置连续断开计数，防止瞬时抖动清零屏蔽标记
            ConsecutiveInactivePolls = 0;

            if (ManualDismissSuppressed || inPostUnlockGrace)
                return PrivacyGuardAction.NoOp;

            if (!curtainActive)
            {
                AutoActivated = true;
                return PrivacyGuardAction.ActivateAuto;
            }
            return PrivacyGuardAction.NoOp;
        }
    }

    /// <summary>被控端防窥屏壁纸设置（持久化于 %LOCALAPPDATA%\双端远程\privacy-wallpaper.txt）。</summary>
    internal sealed class PrivacyWallpaperConfig
    {
        internal const int PresetDeepSpace = 0;
        internal const int PresetMinimalFrost = 1;
        internal const int PresetPureBlack = 2;
        internal const int PresetCustomImage = 3;

        internal const int FitCover = 0;
        internal const int FitContain = 1;
        internal const int FitStretch = 2;

        internal int Preset = PresetDeepSpace;
        internal string CustomImagePath = "";
        internal int FitMode = FitCover;
        internal bool AutoOnRemote = true;
        internal bool ShowOverlayBadge = true;
        internal bool LockOnLocalExit = false;
        internal bool BlockLocalInput = true;
        internal bool HardwareBlackout = true;
        internal string Caption = "远程安全防窥保护中";

        internal static string ConfigPath
        {
            get { return Path.Combine(RecentStore.DirPath, "privacy-wallpaper.txt"); }
        }

        internal static PrivacyWallpaperConfig Load()
        {
            var cfg = new PrivacyWallpaperConfig();
            try
            {
                if (!File.Exists(ConfigPath)) return cfg;
                foreach (string raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    int sep = raw.IndexOf('=');
                    if (sep <= 0) continue;
                    string k = raw.Substring(0, sep).Trim();
                    string v = raw.Substring(sep + 1).Trim();
                    int n;
                    if (k == "preset" && int.TryParse(v, out n) && n >= 0 && n <= 3) cfg.Preset = n;
                    else if (k == "image") cfg.CustomImagePath = v;
                    else if (k == "fit" && int.TryParse(v, out n) && n >= 0 && n <= 2) cfg.FitMode = n;
                    else if (k == "auto") cfg.AutoOnRemote = v == "1";
                    else if (k == "badge") cfg.ShowOverlayBadge = v != "0";
                    else if (k == "lock") cfg.LockOnLocalExit = v == "1";
                    else if (k == "blockInput") cfg.BlockLocalInput = v != "0";
                    else if (k == "hwBlackout") cfg.HardwareBlackout = v != "0";
                    else if (k == "caption" && v.Length <= 60) cfg.Caption = v;
                }
                if (cfg.Preset == PresetCustomImage && (string.IsNullOrEmpty(cfg.CustomImagePath) || !File.Exists(cfg.CustomImagePath)))
                {
                    cfg.Preset = PresetDeepSpace;
                }
            }
            catch
            {
            }
            return cfg;
        }

        internal bool Save(out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(RecentStore.DirPath);
                var sb = new StringBuilder();
                sb.AppendLine("preset=" + Preset);
                sb.AppendLine("image=" + (CustomImagePath ?? ""));
                sb.AppendLine("fit=" + FitMode);
                sb.AppendLine("auto=" + (AutoOnRemote ? "1" : "0"));
                sb.AppendLine("badge=" + (ShowOverlayBadge ? "1" : "0"));
                sb.AppendLine("lock=" + (LockOnLocalExit ? "1" : "0"));
                sb.AppendLine("blockInput=" + (BlockLocalInput ? "1" : "0"));
                sb.AppendLine("hwBlackout=" + (HardwareBlackout ? "1" : "0"));
                sb.AppendLine("caption=" + (Caption ?? "").Replace("\r", "").Replace("\n", ""));
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = "无法保存防窥壁纸配置：" + ex.Message;
                return false;
            }
        }
    }

    /// <summary>自定义防窥壁纸导入、缩放计算与 GDI+ 渲染器（预览框与全屏幕布共用）。</summary>
    internal static class PrivacyWallpaper
    {
        private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
        private const long MaxImageBytes = 32L * 1024L * 1024L;

        internal static bool IsSupportedExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (string ok in SupportedExtensions)
            {
                if (string.Equals(ext, ok, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>校验并将用户选择的壁纸复制到本机应用数据目录，避免原图移动或删除后失效。</summary>
        internal static bool ImportCustomImage(string sourcePath, out string savedPath, out string error)
        {
            savedPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "所选图片文件不存在。";
                return false;
            }
            if (!IsSupportedExtension(sourcePath))
            {
                error = "仅支持 PNG、JPG、JPEG、BMP、GIF 或 TIFF 图片。";
                return false;
            }
            try
            {
                var info = new FileInfo(sourcePath);
                if (info.Length <= 0 || info.Length > MaxImageBytes)
                {
                    error = "图片大小需在 1 B – 32 MB 之间。";
                    return false;
                }
                // 先加载验证确实为合法图像，并转换为无锁内存副本
                using (Image verify = LoadImageUnlocked(sourcePath))
                {
                    if (verify == null || verify.Width < 16 || verify.Height < 16)
                    {
                        error = "无法识别图片尺寸或图片过小。";
                        return false;
                    }
                }
                Directory.CreateDirectory(RecentStore.DirPath);
                string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                string target = Path.Combine(RecentStore.DirPath, "privacy-wallpaper-custom" + ext);
                // 清理其他后缀的旧自定义壁纸
                foreach (string oldExt in SupportedExtensions)
                {
                    string oldFile = Path.Combine(RecentStore.DirPath, "privacy-wallpaper-custom" + oldExt);
                    if (!string.Equals(oldFile, target, StringComparison.OrdinalIgnoreCase) && File.Exists(oldFile))
                    {
                        try { File.Delete(oldFile); } catch { }
                    }
                }
                File.Copy(sourcePath, target, true);
                savedPath = target;
                return true;
            }
            catch (Exception ex)
            {
                error = "导入图片失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>无文件锁读取图像（防止后续替换自定义壁纸时报文件占用）。</summary>
        internal static Bitmap LoadImageUnlocked(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                using (var raw = Image.FromStream(ms))
                {
                    var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                    }
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>计算 Cover / Contain / Stretch 三种适配模式下的绘制目标矩形。</summary>
        internal static RectangleF ComputeImageRect(Size imageSize, Rectangle bounds, int fitMode)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
                return bounds;
            if (fitMode == PrivacyWallpaperConfig.FitStretch)
                return bounds;

            float scaleX = (float)bounds.Width / imageSize.Width;
            float scaleY = (float)bounds.Height / imageSize.Height;
            float scale = fitMode == PrivacyWallpaperConfig.FitContain
                ? Math.Min(scaleX, scaleY)
                : Math.Max(scaleX, scaleY);

            float drawW = imageSize.Width * scale;
            float drawH = imageSize.Height * scale;
            float x = bounds.X + (bounds.Width - drawW) * 0.5f;
            float y = bounds.Y + (bounds.Height - drawH) * 0.5f;
            return new RectangleF(x, y, drawW, drawH);
        }

        internal static void Render(Graphics g, Rectangle bounds, PrivacyWallpaperConfig cfg, Image customImage, bool previewMode)
        {
            if (bounds.Width < 2 || bounds.Height < 2) return;
            var save = g.Save();
            g.SetClip(bounds);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            bool drewCustom = false;
            if (cfg.Preset == PrivacyWallpaperConfig.PresetCustomImage && customImage != null)
            {
                using (var bg = new SolidBrush(Color.FromArgb(10, 14, 24)))
                    g.FillRectangle(bg, bounds);
                RectangleF dest = ComputeImageRect(customImage.Size, bounds, cfg.FitMode);
                g.DrawImage(customImage, dest);
                drewCustom = true;
            }

            if (!drewCustom)
            {
                RenderBuiltInTheme(g, bounds, cfg.Preset);
            }

            if (cfg.ShowOverlayBadge || previewMode)
            {
                RenderOverlayBadge(g, bounds, cfg, previewMode);
            }
            g.Restore(save);
        }

        private static void RenderBuiltInTheme(Graphics g, Rectangle bounds, int preset)
        {
            if (preset == PrivacyWallpaperConfig.PresetPureBlack)
            {
                using (var black = new SolidBrush(Color.FromArgb(6, 7, 10)))
                    g.FillRectangle(black, bounds);
                return;
            }

            if (preset == PrivacyWallpaperConfig.PresetMinimalFrost)
            {
                using (var bg = new LinearGradientBrush(bounds, Color.FromArgb(24, 28, 38), Color.FromArgb(14, 17, 25), 135f))
                    g.FillRectangle(bg, bounds);
                float cx = bounds.X + bounds.Width * 0.5f;
                float cy = bounds.Y + bounds.Height * 0.44f;
                float r = Math.Min(bounds.Width, bounds.Height) * 0.16f;
                using (var ring = new Pen(Color.FromArgb(35, 226, 232, 240), Math.Max(1.2f, r * 0.03f)))
                {
                    g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
                    g.DrawEllipse(ring, cx - r * 1.45f, cy - r * 1.45f, r * 2.9f, r * 2.9f);
                }
                DrawShieldMark(g, cx, cy, r * 0.46f, Color.FromArgb(180, 226, 232, 240));
                return;
            }

            // PresetDeepSpace (默认：深空星盾渐变 + 柔和光晕)
            using (var bg = new LinearGradientBrush(bounds, Color.FromArgb(12, 20, 46), Color.FromArgb(24, 42, 98), 42f))
                g.FillRectangle(bg, bounds);

            float glowX = bounds.X + bounds.Width * 0.5f;
            float glowY = bounds.Y + bounds.Height * 0.43f;
            float glowR = Math.Min(bounds.Width, bounds.Height) * 0.38f;
            if (glowR > 8)
            {
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(glowX - glowR, glowY - glowR, glowR * 2, glowR * 2);
                    using (var pgb = new PathGradientBrush(gp))
                    {
                        pgb.CenterColor = Color.FromArgb(72, 79, 134, 255);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, 24, 42, 98) };
                        g.FillPath(pgb, gp);
                    }
                }
                using (var orbit = new Pen(Color.FromArgb(34, 191, 219, 254), Math.Max(1f, glowR * 0.008f)))
                {
                    g.DrawEllipse(orbit, glowX - glowR * 0.55f, glowY - glowR * 0.55f, glowR * 1.1f, glowR * 1.1f);
                    g.DrawEllipse(orbit, glowX - glowR * 0.85f, glowY - glowR * 0.85f, glowR * 1.7f, glowR * 1.7f);
                }
                DrawShieldMark(g, glowX, glowY, glowR * 0.24f, Color.FromArgb(215, 224, 238, 255));
            }
        }

        private static void DrawShieldMark(Graphics g, float cx, float cy, float radius, Color color)
        {
            if (radius < 6) return;
            var pts = new[]
            {
                new PointF(cx, cy - radius),
                new PointF(cx + radius * 0.82f, cy - radius * 0.58f),
                new PointF(cx + radius * 0.72f, cy + radius * 0.22f),
                new PointF(cx, cy + radius),
                new PointF(cx - radius * 0.72f, cy + radius * 0.22f),
                new PointF(cx - radius * 0.82f, cy - radius * 0.58f)
            };
            using (var fill = new SolidBrush(Color.FromArgb(28, color)))
                g.FillPolygon(fill, pts);
            using (var pen = new Pen(color, Math.Max(1.4f, radius * 0.08f)) { LineJoin = LineJoin.Round })
                g.DrawPolygon(pen, pts);
            using (var check = new Pen(color, Math.Max(1.4f, radius * 0.085f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(check, new[]
                {
                    new PointF(cx - radius * 0.28f, cy + radius * 0.02f),
                    new PointF(cx - radius * 0.05f, cy + radius * 0.26f),
                    new PointF(cx + radius * 0.34f, cy - radius * 0.22f)
                });
            }
        }

        internal static string FormatCurtainBadgeText(PrivacyWallpaperConfig cfg, DateTime now)
        {
            string timeText = now.ToString("HH:mm");
            string caption = cfg == null || string.IsNullOrWhiteSpace(cfg.Caption)
                ? ""
                : cfg.Caption.Trim();
            return caption.Length > 0 ? (timeText + "  ·  " + caption) : timeText;
        }

        /// <summary>
        /// 进入防窥屏瞬间的顶部悬浮快捷键提醒卡片（宽敞留白、独立按键胶囊与清晰的视觉层级，展示约 3.5s 后平滑淡出）。
        /// </summary>
        internal static void RenderEntryToast(Graphics g, Rectangle bounds, float opacity, bool lockOnLocalExit)
        {
            if (opacity <= 0.01f || bounds.Width < 300 || bounds.Height < 180) return;
            float clamped = Math.Max(0f, Math.Min(1f, opacity));
            var save = g.Save();
            try
            {
                g.SetClip(bounds);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                float unit = Math.Max(0.82f, Math.Min(1.28f, bounds.Height / 1080f));
                string title = "已进入防窥保护模式";
                string statusTag = "远控画面透明";
                string hintPrefix = "本地退出";
                string[] keys = new[] { "Ctrl", "Alt", "Shift", "P" };
                string sub = lockOnLocalExit ? "按下退出快捷键后将自动锁定 Windows 桌面" : "";
                bool hasSub = sub.Length > 0;

                using (var titleFont = new Font("Microsoft YaHei UI", 12.5f * unit, FontStyle.Bold))
                using (var tagFont = new Font("Microsoft YaHei UI", 8.5f * unit, FontStyle.Bold))
                using (var keyFont = new Font("Consolas", 9.5f * unit, FontStyle.Bold))
                using (var labelFont = new Font("Microsoft YaHei UI", 9.5f * unit, FontStyle.Regular))
                using (var subFont = new Font("Microsoft YaHei UI", 9f * unit, FontStyle.Regular))
                using (var leftFmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                using (var centerFmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                {
                    SizeF titleSz = g.MeasureString(title, titleFont);
                    SizeF tagSz = g.MeasureString(statusTag, tagFont);
                    SizeF prefixSz = g.MeasureString(hintPrefix, labelFont);
                    SizeF plusSz = g.MeasureString("+", labelFont);
                    float keyPadX = 9f * unit;
                    float keyGap = 5f * unit;
                    float keyH = 24f * unit;
                    float[] keyWidths = new float[keys.Length];
                    float keysTotalW = prefixSz.Width + 10f * unit;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        keyWidths[i] = Math.Max(26f * unit, g.MeasureString(keys[i], keyFont).Width + keyPadX * 2f);
                        keysTotalW += keyWidths[i];
                        if (i < keys.Length - 1) keysTotalW += plusSz.Width + keyGap * 2f;
                    }
                    SizeF subSz = hasSub ? g.MeasureString(sub, subFont) : SizeF.Empty;

                    float iconSize = 42f * unit;
                    float padLeft = 24f * unit;
                    float padRight = 26f * unit;
                    float padTop = 18f * unit;
                    float padBottom = 18f * unit;
                    float iconGap = 18f * unit;
                    float row1W = titleSz.Width + 12f * unit + tagSz.Width + 18f * unit;
                    float textColW = Math.Max(row1W, Math.Max(keysTotalW, subSz.Width));

                    float cardW = Math.Min(bounds.Width * 0.88f, padLeft + iconSize + iconGap + textColW + padRight);
                    float row1H = 26f * unit;
                    float rowGap1 = 10f * unit;
                    float row2H = keyH + 2f * unit;
                    float rowGap2 = hasSub ? 10f * unit : 0f;
                    float row3H = hasSub ? 20f * unit : 0f;
                    float cardH = padTop + row1H + rowGap1 + row2H + rowGap2 + row3H + padBottom;

                    float cx = bounds.X + (bounds.Width - cardW) * 0.5f;
                    float cy = bounds.Y + Math.Max(34f, 52f * unit);
                    var card = new RectangleF(cx, cy, cardW, cardH);

                    int shadowAlpha = (int)Math.Round(72 * clamped);
                    int bgAlpha = (int)Math.Round(236 * clamped);
                    int borderAlpha = (int)Math.Round(115 * clamped);
                    int accentAlpha = (int)Math.Round(225 * clamped);
                    int titleAlpha = (int)Math.Round(252 * clamped);
                    int labelAlpha = (int)Math.Round(200 * clamped);
                    int subAlpha = (int)Math.Round(190 * clamped);

                    // Soft elevated shadow behind card
                    var shadowRect = new RectangleF(card.X - 2f, card.Y + 5f * unit, card.Width + 4f, card.Height + 3f);
                    using (var sp = CreateRoundedRect(shadowRect, 16f * unit))
                    using (var sb = new SolidBrush(Color.FromArgb(shadowAlpha, 2, 6, 23)))
                    {
                        g.FillPath(sb, sp);
                    }

                    // Main frosted dark slate card
                    using (var path = CreateRoundedRect(card, 14f * unit))
                    using (var bg = new SolidBrush(Color.FromArgb(bgAlpha, 15, 23, 42)))
                    using (var border = new Pen(Color.FromArgb(borderAlpha, 148, 184, 226), 1.15f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }

                    // Subtle top cyan accent bar
                    var accentBar = new RectangleF(card.X + 22f * unit, card.Y, card.Width - 44f * unit, 2.5f * unit);
                    using (var accentBrush = new SolidBrush(Color.FromArgb(accentAlpha, 56, 189, 248)))
                    {
                        g.FillRectangle(accentBrush, accentBar);
                    }

                    // Left shield icon badge
                    float iconX = card.X + padLeft;
                    float iconY = card.Y + (card.Height - iconSize) * 0.5f;
                    using (var badgeBg = new SolidBrush(Color.FromArgb((int)Math.Round(215 * clamped), 24, 52, 86)))
                    using (var badgeBorder = new Pen(Color.FromArgb((int)Math.Round(135 * clamped), 56, 189, 248), 1.2f))
                    {
                        g.FillEllipse(badgeBg, iconX, iconY, iconSize, iconSize);
                        g.DrawEllipse(badgeBorder, iconX, iconY, iconSize, iconSize);
                    }
                    DrawShieldMark(g, iconX + iconSize * 0.5f, iconY + iconSize * 0.5f, iconSize * 0.34f,
                        Color.FromArgb(titleAlpha, 96, 198, 255));

                    // Right text & keycap column
                    float tx = iconX + iconSize + iconGap;
                    float y1 = card.Y + padTop;

                    using (var titleBrush = new SolidBrush(Color.FromArgb(titleAlpha, 248, 250, 252)))
                    using (var labelBrush = new SolidBrush(Color.FromArgb(labelAlpha, 168, 186, 212)))
                    using (var subBrush = new SolidBrush(Color.FromArgb(subAlpha, 147, 197, 253)))
                    {
                        // Row 1: Title + green status pill
                        g.DrawString(title, titleFont, titleBrush, new RectangleF(tx, y1, titleSz.Width + 6f, row1H), leftFmt);

                        float tagX = tx + titleSz.Width + 12f * unit;
                        float tagW = tagSz.Width + 16f * unit;
                        float tagH = 20f * unit;
                        float tagY = y1 + (row1H - tagH) * 0.5f;
                        if (tagX + tagW < card.Right - 14f * unit)
                        {
                            var tagRect = new RectangleF(tagX, tagY, tagW, tagH);
                            using (var tp = CreateRoundedRect(tagRect, tagH * 0.5f))
                            using (var tbg = new SolidBrush(Color.FromArgb((int)Math.Round(190 * clamped), 16, 72, 54)))
                            using (var tbd = new Pen(Color.FromArgb((int)Math.Round(150 * clamped), 52, 211, 153), 1f))
                            using (var tbr = new SolidBrush(Color.FromArgb(titleAlpha, 110, 231, 183)))
                            {
                                g.FillPath(tbg, tp);
                                g.DrawPath(tbd, tp);
                                g.DrawString(statusTag, tagFont, tbr, tagRect, centerFmt);
                            }
                        }

                        // Row 2: "本地退出" + separated rounded keycaps
                        float y2 = y1 + row1H + rowGap1;
                        g.DrawString(hintPrefix, labelFont, labelBrush, new RectangleF(tx, y2, prefixSz.Width + 4f, row2H), leftFmt);
                        float kx = tx + prefixSz.Width + 10f * unit;
                        float ky = y2 + (row2H - keyH) * 0.5f;
                        using (var keyBg = new SolidBrush(Color.FromArgb((int)Math.Round(220 * clamped), 30, 41, 59)))
                        using (var keyBorder = new Pen(Color.FromArgb((int)Math.Round(155 * clamped), 100, 116, 139), 1f))
                        using (var keyBrush = new SolidBrush(Color.FromArgb(titleAlpha, 241, 245, 249)))
                        {
                            for (int i = 0; i < keys.Length; i++)
                            {
                                var kr = new RectangleF(kx, ky, keyWidths[i], keyH);
                                using (var kp = CreateRoundedRect(kr, 6f * unit))
                                {
                                    g.FillPath(keyBg, kp);
                                    g.DrawPath(keyBorder, kp);
                                }
                                g.DrawString(keys[i], keyFont, keyBrush, kr, centerFmt);
                                kx += keyWidths[i];
                                if (i < keys.Length - 1)
                                {
                                    g.DrawString("+", labelFont, labelBrush,
                                        new RectangleF(kx, y2, plusSz.Width + keyGap * 2f, row2H), centerFmt);
                                    kx += plusSz.Width + keyGap * 2f;
                                }
                            }
                        }

                        // Row 3: Optional lock-on-exit footer note
                        if (hasSub)
                        {
                            float y3 = y2 + row2H + rowGap2;
                            g.DrawString(sub, subFont, subBrush,
                                new RectangleF(tx, y3, card.Right - padRight - tx, row3H), leftFmt);
                        }
                    }
                }
            }
            finally
            {
                g.Restore(save);
            }
        }

        private static void RenderOverlayBadge(Graphics g, Rectangle bounds, PrivacyWallpaperConfig cfg, bool previewMode)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float unit = Math.Max(0.24f, Math.Min(1.25f, bounds.Height / 900f));
            bool isMiniPreview = bounds.Width < 460;
            string mainText = FormatCurtainBadgeText(cfg, DateTime.Now);

            if (!previewMode)
            {
                if (!cfg.ShowOverlayBadge) return;

                float fontSize = isMiniPreview ? Math.Max(6.5f, Math.Min(8.5f, bounds.Height / 19f)) : Math.Max(11f, 14f * unit);
                using (var mainFont = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold))
                using (var mainBrush = new SolidBrush(Color.FromArgb(242, 246, 255)))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    SizeF measured = g.MeasureString(mainText, mainFont);
                    float padX = isMiniPreview ? 12f : Math.Max(22f, 28f * unit);
                    float pillW = Math.Min(bounds.Width * 0.90f, measured.Width + padX * 2f);
                    float pillH = isMiniPreview ? Math.Max(20f, measured.Height + 8f) : Math.Max(38f, 46f * unit);
                    float px = bounds.X + (bounds.Width - pillW) * 0.5f;
                    float py = bounds.Bottom - pillH - (isMiniPreview ? Math.Max(8f, bounds.Height * 0.08f) : Math.Max(28f, 48f * unit));
                    var pill = new RectangleF(px, py, pillW, pillH);

                    using (var path = CreateRoundedRect(pill, pillH * 0.5f))
                    using (var bg = new SolidBrush(Color.FromArgb(152, 12, 18, 34)))
                    using (var border = new Pen(Color.FromArgb(68, 226, 232, 240), 1f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                    g.DrawString(mainText, mainFont, mainBrush,
                        new RectangleF(pill.X + 8f, pill.Y, pill.Width - 16f, pill.Height), fmt);
                }
                return;
            }

            // Full-screen interactive preview mode only
            string previewHint = "壁纸预览模式 · 点击任意位置或按 Esc 退出";
            float fontSizeMain = Math.Max(10.5f, 13.5f * unit);
            float fontSizeSub = Math.Max(8.5f, 10f * unit);
            using (var mainFont = new Font("Microsoft YaHei UI", fontSizeMain, FontStyle.Bold))
            using (var subFont = new Font("Microsoft YaHei UI", fontSizeSub))
            using (var mainBrush = new SolidBrush(Color.FromArgb(242, 246, 255)))
            using (var subBrush = new SolidBrush(Color.FromArgb(186, 204, 232)))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                SizeF mainSz = cfg.ShowOverlayBadge ? g.MeasureString(mainText, mainFont) : SizeF.Empty;
                SizeF subSz = g.MeasureString(previewHint, subFont);
                float padX = Math.Max(24f, 28f * unit);
                float pillW = Math.Min(bounds.Width * 0.88f, Math.Max(mainSz.Width, subSz.Width) + padX * 2f);
                float pillH = cfg.ShowOverlayBadge ? Math.Max(54f, 66f * unit) : Math.Max(36f, 42f * unit);
                float px = bounds.X + (bounds.Width - pillW) * 0.5f;
                float py = bounds.Bottom - pillH - Math.Max(24f, 44f * unit);
                var pill = new RectangleF(px, py, pillW, pillH);

                using (var path = CreateRoundedRect(pill, pillH * 0.5f))
                using (var bg = new SolidBrush(Color.FromArgb(156, 12, 18, 34)))
                using (var border = new Pen(Color.FromArgb(68, 226, 232, 240), 1f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }

                if (cfg.ShowOverlayBadge)
                {
                    g.DrawString(mainText, mainFont, mainBrush,
                        new RectangleF(pill.X + 12f, pill.Y + pill.Height * 0.10f, pill.Width - 24f, pill.Height * 0.46f), fmt);
                    g.DrawString(previewHint, subFont, subBrush,
                        new RectangleF(pill.X + 12f, pill.Y + pill.Height * 0.54f, pill.Width - 24f, pill.Height * 0.36f), fmt);
                }
                else
                {
                    g.DrawString(previewHint, subFont, mainBrush, pill, fmt);
                }
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float rad = Math.Min(radius, Math.Min(r.Width, r.Height) * 0.5f);
            float d = rad * 2f;
            if (d < 1f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
