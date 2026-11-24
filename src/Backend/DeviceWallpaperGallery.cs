using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace DualRemote
{
    /// <summary>
    /// 设备预览卡片 Windows 系统风景壁纸图库：
    /// - 优先只读扫描本机 %WINDIR%\Web\Wallpaper 下的 Windows 系统自带风景图，内存中按需缩略缓存（不锁文件）；
    /// - 若系统目录无可用图（例如精简版系统或无桌面 CI 环境），无缝回退到 5 套 Windows 风格矢量风景场景；
    /// - 为每台设备随机分配不同风景图，并支持点击「换壁纸」随时切换。
    /// </summary>
    internal static class DeviceWallpaperGallery
    {
        internal const int BuiltInScenicCount = 5;
        private const int MaxCachedWidth = 1200;
        private const int MaxCachedHeight = 680;
        private const int MaxCachedBitmaps = 8;
        private const long MinWallpaperBytes = 16 * 1024;
        private const long MaxWallpaperBytes = 24L * 1024L * 1024L;

        private static readonly object Gate = new object();
        private static readonly Random Rng = new Random();
        private static readonly Dictionary<string, int> DeviceAssignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Bitmap> BitmapCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> CacheOrder = new List<string>();
        private static List<string> discoveredPaths;

        internal static int TotalCount
        {
            get
            {
                lock (Gate)
                {
                    EnsureDiscoveredLocked();
                    return discoveredPaths.Count > 0 ? discoveredPaths.Count : BuiltInScenicCount;
                }
            }
        }

        internal static bool HasSystemWallpapers
        {
            get
            {
                lock (Gate)
                {
                    EnsureDiscoveredLocked();
                    return discoveredPaths.Count > 0;
                }
            }
        }

        /// <summary>为指定设备分配一张随机 Windows 风景壁纸索引（同会话内记忆，避免频繁闪烁）。</summary>
        internal static int GetOrAssignIndex(string deviceKey)
        {
            string key = string.IsNullOrWhiteSpace(deviceKey) ? "__default__" : deviceKey.Trim();
            lock (Gate)
            {
                EnsureDiscoveredLocked();
                int count = discoveredPaths.Count > 0 ? discoveredPaths.Count : BuiltInScenicCount;
                int existing;
                if (DeviceAssignments.TryGetValue(key, out existing) && existing >= 0 && existing < count)
                    return existing;
                int picked = PickRandomIndexLocked(count, -1);
                DeviceAssignments[key] = picked;
                return picked;
            }
        }

        /// <summary>为指定设备随机切换到另一张 Windows 风景壁纸。</summary>
        internal static int CycleRandomIndex(string deviceKey)
        {
            string key = string.IsNullOrWhiteSpace(deviceKey) ? "__default__" : deviceKey.Trim();
            lock (Gate)
            {
                EnsureDiscoveredLocked();
                int count = discoveredPaths.Count > 0 ? discoveredPaths.Count : BuiltInScenicCount;
                int prev;
                if (!DeviceAssignments.TryGetValue(key, out prev)) prev = -1;
                int next = PickRandomIndexLocked(count, prev);
                DeviceAssignments[key] = next;
                return next;
            }
        }

        /// <summary>将选定的 Windows 风景图或内置风景场景以 Cover 满铺方式绘制到目标矩形内。</summary>
        internal static void DrawWallpaper(Graphics g, RectangleF target, int index, bool online)
        {
            if (g == null || target.Width < 2f || target.Height < 2f) return;

            Bitmap bmp = null;
            lock (Gate)
            {
                EnsureDiscoveredLocked();
                if (discoveredPaths.Count > 0)
                {
                    int safeIdx = ((index % discoveredPaths.Count) + discoveredPaths.Count) % discoveredPaths.Count;
                    bmp = GetOrLoadBitmapLocked(discoveredPaths[safeIdx]);
                }
            }

            if (bmp != null)
            {
                DrawImageCover(g, bmp, target);
                if (!online)
                {
                    using (var veil = new SolidBrush(Color.FromArgb(118, 24, 34, 48)))
                        g.FillRectangle(veil, target);
                }
                else
                {
                    // Subtle sky/bottom contrast polish so white badges and CTA button pop cleanly
                    using (var shade = new LinearGradientBrush(target, Color.FromArgb(18, 12, 24, 42), Color.FromArgb(88, 10, 20, 36), 90f))
                        g.FillRectangle(shade, target);
                }
                return;
            }

            int scene = ((index % BuiltInScenicCount) + BuiltInScenicCount) % BuiltInScenicCount;
            DrawBuiltInWindowsLandscape(g, target, scene, online);
        }

        internal static void DisposeCache()
        {
            lock (Gate)
            {
                foreach (var kv in BitmapCache)
                {
                    try { if (kv.Value != null) kv.Value.Dispose(); } catch { }
                }
                BitmapCache.Clear();
                CacheOrder.Clear();
            }
        }

        private static int PickRandomIndexLocked(int count, int exclude)
        {
            if (count <= 1) return 0;
            if (exclude < 0 || exclude >= count) return Rng.Next(count);
            int candidate = Rng.Next(count - 1);
            return candidate >= exclude ? candidate + 1 : candidate;
        }

        private static void EnsureDiscoveredLocked()
        {
            if (discoveredPaths != null) return;
            discoveredPaths = new List<string>();
            try
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(winDir)) return;
                string root = Path.Combine(winDir, "Web", "Wallpaper");
                if (!Directory.Exists(root)) return;

                var candidates = new List<string>();
                foreach (string file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file);
                    if (!string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length < MinWallpaperBytes || fi.Length > MaxWallpaperBytes) continue;
                        candidates.Add(file);
                    }
                    catch { }
                }

                // Fisher-Yates shuffle so every launch starts with a fresh Windows scenic order
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = Rng.Next(i + 1);
                    string tmp = candidates[i];
                    candidates[i] = candidates[j];
                    candidates[j] = tmp;
                }
                discoveredPaths.AddRange(candidates);
            }
            catch
            {
            }
        }

        private static Bitmap GetOrLoadBitmapLocked(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Bitmap cached;
            if (BitmapCache.TryGetValue(path, out cached))
                return cached;

            Bitmap loaded = LoadScaledCopy(path);
            if (loaded == null) return null;

            while (CacheOrder.Count >= MaxCachedBitmaps)
            {
                string oldest = CacheOrder[0];
                CacheOrder.RemoveAt(0);
                Bitmap oldBmp;
                if (BitmapCache.TryGetValue(oldest, out oldBmp))
                {
                    BitmapCache.Remove(oldest);
                    try { if (oldBmp != null) oldBmp.Dispose(); } catch { }
                }
            }
            BitmapCache[path] = loaded;
            CacheOrder.Add(path);
            return loaded;
        }

        private static Bitmap LoadScaledCopy(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes == null || bytes.Length < MinWallpaperBytes) return null;
                using (var ms = new MemoryStream(bytes, false))
                using (var src = Image.FromStream(ms, false, true))
                {
                    if (src == null || src.Width < 160 || src.Height < 100) return null;
                    float scale = Math.Min(1f, Math.Min((float)MaxCachedWidth / src.Width, (float)MaxCachedHeight / src.Height));
                    int w = Math.Max(160, (int)Math.Round(src.Width * scale));
                    int h = Math.Max(100, (int)Math.Round(src.Height * scale));
                    var bmp = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void DrawImageCover(Graphics g, Image img, RectangleF target)
        {
            float scale = Math.Max(target.Width / img.Width, target.Height / img.Height);
            float drawW = img.Width * scale;
            float drawH = img.Height * scale;
            float drawX = target.X + (target.Width - drawW) / 2f;
            float drawY = target.Y + (target.Height - drawH) / 2f;
            InterpolationMode prevInterp = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, drawX, drawY, drawW, drawH);
            g.InterpolationMode = prevInterp;
        }

        /// <summary>5 套 Windows 风格高质感矢量风景场景（无系统壁纸目录时自动兜底）。</summary>
        internal static void DrawBuiltInWindowsLandscape(Graphics g, RectangleF r, int sceneIndex, bool online)
        {
            float w = r.Width;
            float h = r.Height;
            float x = r.X;
            float y = r.Y;

            Color skyTop;
            Color skyBot;
            Color sunGlow;
            Color ridgeFar;
            Color ridgeMid;
            Color ridgeNear;
            switch (sceneIndex % BuiltInScenicCount)
            {
                case 0: // Windows 晨曦雪山湖泊
                    skyTop = Color.FromArgb(46, 118, 212);
                    skyBot = Color.FromArgb(164, 214, 248);
                    sunGlow = Color.FromArgb(120, 255, 240, 200);
                    ridgeFar = Color.FromArgb(140, 82, 134, 198);
                    ridgeMid = Color.FromArgb(196, 44, 88, 152);
                    ridgeNear = Color.FromArgb(230, 24, 54, 102);
                    break;
                case 1: // Windows 11 Bloom 琉璃光浪
                    skyTop = Color.FromArgb(20, 68, 168);
                    skyBot = Color.FromArgb(92, 174, 248);
                    sunGlow = Color.FromArgb(128, 146, 224, 255);
                    ridgeFar = Color.FromArgb(135, 38, 118, 232);
                    ridgeMid = Color.FromArgb(182, 24, 86, 204);
                    ridgeNear = Color.FromArgb(220, 14, 52, 142);
                    break;
                case 2: // 落日海岸晚霞
                    skyTop = Color.FromArgb(56, 76, 148);
                    skyBot = Color.FromArgb(238, 158, 112);
                    sunGlow = Color.FromArgb(150, 255, 218, 146);
                    ridgeFar = Color.FromArgb(148, 136, 94, 132);
                    ridgeMid = Color.FromArgb(195, 76, 62, 104);
                    ridgeNear = Color.FromArgb(230, 38, 42, 74);
                    break;
                case 3: // 森林晨雾山谷
                    skyTop = Color.FromArgb(34, 116, 128);
                    skyBot = Color.FromArgb(172, 226, 216);
                    sunGlow = Color.FromArgb(126, 242, 255, 232);
                    ridgeFar = Color.FromArgb(145, 58, 132, 122);
                    ridgeMid = Color.FromArgb(196, 32, 92, 86);
                    ridgeNear = Color.FromArgb(232, 18, 58, 56);
                    break;
                default: // 极光星夜雪峰
                    skyTop = Color.FromArgb(14, 26, 58);
                    skyBot = Color.FromArgb(28, 86, 118);
                    sunGlow = Color.FromArgb(118, 88, 236, 188);
                    ridgeFar = Color.FromArgb(142, 26, 66, 98);
                    ridgeMid = Color.FromArgb(198, 18, 44, 74);
                    ridgeNear = Color.FromArgb(236, 12, 26, 48);
                    break;
            }

            using (var sky = new LinearGradientBrush(r, skyTop, skyBot, 90f))
                g.FillRectangle(sky, r);

            // Soft atmospheric light bloom
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(x + w * 0.50f, y - h * 0.25f, w * 0.62f, h * 0.98f);
                using (var pgb = new PathGradientBrush(glowPath))
                {
                    pgb.CenterColor = sunGlow;
                    pgb.SurroundColors = new[] { Color.FromArgb(0, sunGlow) };
                    g.FillPath(pgb, glowPath);
                }
            }

            // Layered mountain / bloom silhouettes
            using (var p1 = new GraphicsPath())
            {
                p1.AddPolygon(new[]
                {
                    new PointF(x, y + h),
                    new PointF(x, y + h * 0.64f),
                    new PointF(x + w * 0.18f, y + h * 0.44f),
                    new PointF(x + w * 0.38f, y + h * 0.58f),
                    new PointF(x + w * 0.62f, y + h * 0.36f),
                    new PointF(x + w * 0.84f, y + h * 0.52f),
                    new PointF(x + w, y + h * 0.42f),
                    new PointF(x + w, y + h)
                });
                using (var b1 = new SolidBrush(ridgeFar)) g.FillPath(b1, p1);
            }
            using (var p2 = new GraphicsPath())
            {
                p2.AddPolygon(new[]
                {
                    new PointF(x, y + h),
                    new PointF(x, y + h * 0.74f),
                    new PointF(x + w * 0.26f, y + h * 0.56f),
                    new PointF(x + w * 0.50f, y + h * 0.70f),
                    new PointF(x + w * 0.78f, y + h * 0.48f),
                    new PointF(x + w, y + h * 0.64f),
                    new PointF(x + w, y + h)
                });
                using (var b2 = new SolidBrush(ridgeMid)) g.FillPath(b2, p2);
            }
            using (var p3 = new GraphicsPath())
            {
                p3.AddPolygon(new[]
                {
                    new PointF(x, y + h),
                    new PointF(x, y + h * 0.86f),
                    new PointF(x + w * 0.32f, y + h * 0.74f),
                    new PointF(x + w * 0.68f, y + h * 0.84f),
                    new PointF(x + w, y + h * 0.70f),
                    new PointF(x + w, y + h)
                });
                using (var b3 = new SolidBrush(ridgeNear)) g.FillPath(b3, p3);
            }

            if (!online)
            {
                using (var veil = new SolidBrush(Color.FromArgb(108, 30, 38, 52)))
                    g.FillRectangle(veil, r);
            }
        }
    }
}
