using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace DualRemote
{
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
        internal bool AutoOnRemote = false;
        internal bool ShowOverlayBadge = true;
        internal bool LockOnLocalExit = false;
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

        private static void RenderOverlayBadge(Graphics g, Rectangle bounds, PrivacyWallpaperConfig cfg, bool previewMode)
        {
            float unit = Math.Max(0.28f, Math.Min(1.25f, bounds.Height / 900f));
            string caption = string.IsNullOrWhiteSpace(cfg.Caption) ? "远程安全防窥保护中" : cfg.Caption.Trim();
            string timeText = DateTime.Now.ToString("HH:mm");
            string subText = previewMode
                ? "壁纸预览 · 点击画面或按 Esc 立即退出"
                : caption + " · 按 Ctrl+Alt+Shift+P 退出";

            float pillW = Math.Min(bounds.Width * 0.86f, Math.Max(210f * unit, 440f * unit));
            float pillH = Math.Max(26f, 66f * unit);
            float px = bounds.X + (bounds.Width - pillW) * 0.5f;
            float py = bounds.Bottom - pillH - Math.Max(12f, 44f * unit);
            var pill = new RectangleF(px, py, pillW, pillH);

            using (var path = CreateRoundedRect(pill, pillH * 0.5f))
            using (var bg = new SolidBrush(Color.FromArgb(148, 12, 18, 34)))
            using (var border = new Pen(Color.FromArgb(65, 226, 232, 240), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            float fontSizeMain = Math.Max(7.5f, 13.5f * unit);
            float fontSizeSub = Math.Max(6.5f, 9.5f * unit);
            using (var mainFont = new Font("Microsoft YaHei UI", fontSizeMain, FontStyle.Bold))
            using (var subFont = new Font("Microsoft YaHei UI", fontSizeSub))
            using (var mainBrush = new SolidBrush(Color.FromArgb(242, 246, 255)))
            using (var subBrush = new SolidBrush(Color.FromArgb(186, 204, 232)))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                if (cfg.ShowOverlayBadge)
                {
                    g.DrawString(timeText + "  ·  " + caption, mainFont, mainBrush,
                        new RectangleF(pill.X + 12, pill.Y + pill.Height * 0.10f, pill.Width - 24, pill.Height * 0.46f), fmt);
                    g.DrawString(previewMode ? "壁纸预览模式 · 点击任意位置或按 Esc 退出" : "物理屏已防窥隐藏 · 快捷键 Ctrl+Alt+Shift+P 退出",
                        subFont, subBrush,
                        new RectangleF(pill.X + 12, pill.Y + pill.Height * 0.54f, pill.Width - 24, pill.Height * 0.36f), fmt);
                }
                else
                {
                    g.DrawString(subText, subFont, mainBrush, pill, fmt);
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
