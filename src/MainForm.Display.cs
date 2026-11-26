using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal sealed partial class MainForm
    {
        private readonly Theme.StatusBanner displayBanner = new Theme.StatusBanner("防窥壁纸采用系统级捕获排除（WDA_EXCLUDEFROMCAPTURE），物理屏遮挡、远控画面透明无干扰。");
        private readonly Theme.Badge curtainStateBadge = new Theme.Badge("防窥壁纸未开启", Theme.PillTone.Neutral);
        private readonly Button[] presetButtons = new Button[4];
        private WallpaperPreviewPanel wallpaperPreview;
        private Control wallpaperControlsPanel;
        private TableLayoutPanel wallpaperRootLayout;
        private Control wallpaperCardControl;
        private Panel wallpaperSplitHost;
        private int wallpaperSplitRow = -1;
        private int wallpaperCardRow = -1;
        private bool wallpaperCompactMode;
        private Theme.SlimScrollViewport privacyViewport;
        private Button toggleCurtainBtn;
        private ComboBox wallpaperFitCombo;
        private ComboBox privacyHostDisconnectActionCombo;
        private ComboBox settingsHostDisconnectActionCombo;
        private bool syncingHostDisconnectAction;
        private TextBox wallpaperCaptionBox;
        private Label customImageHint;
        private Bitmap previewCustomImage;
        private TableLayoutPanel privacyDesk;

        private Control BuildPrivacyAndDisplayPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(980, out desk);
            privacyViewport = viewport;
            privacyDesk = desk;

            curtainStateBadge.AlignRight = true;
            AddPageHeader(desk, "防窥与显示", "设置被控端防窥壁纸、物理键鼠锁与背光亮度", curtainStateBadge, 164);
            Theme.Row(desk, 18);

            var wallpaperCard = BuildPrivacyWallpaperCard();
            wallpaperCardRow = desk.RowCount;
            Theme.Row(desk, 330, wallpaperCard);
            Theme.Row(desk, 16);

            Theme.Row(desk, 26, Theme.Text("防窥保护选项", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(desk, 10);

            var autoSwitch = BuildToggle("远控自动防窥", privacyConfig.AutoOnRemote);
            autoSwitch.CheckedChanged += delegate
            {
                privacyConfig.AutoOnRemote = autoSwitch.Checked;
                SavePrivacyConfig();
                SyncRemoteGuardTimer();
                UpdatePrivacyIndicators();
                ShowDisplayBanner(autoSwitch.Checked
                    ? "已开启自动防窥守护：检测到远控连接时自动遮挡物理屏并锁定物理键鼠，断开后自动恢复。"
                    : "已关闭自动防窥守护。", Theme.PillTone.Info);
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Shield, "远控接入时自动开启防窥",
                "检测到 BoBo远程 会话连入时自动遮挡物理显示器并锁定现场键鼠，断开后自动恢复", autoSwitch);

            var blockInputSwitch = BuildToggle("防窥锁定物理键鼠", privacyConfig.BlockLocalInput);
            blockInputSwitch.CheckedChanged += delegate
            {
                privacyConfig.BlockLocalInput = blockInputSwitch.Checked;
                SavePrivacyConfig();
                if (privacyCurtain.IsActive && !privacyCurtain.IsPreview)
                {
                    privacyCurtain.SetLocalInputBlock(blockInputSwitch.Checked);
                }
                ShowDisplayBanner(blockInputSwitch.Checked
                    ? "已开启物理键鼠锁定：防窥期间旁人无法点击任务栏、开始菜单或右键刷新（仅放行远控与 Ctrl+Alt+Shift+P）。"
                    : "已关闭防窥物理键鼠锁定。", Theme.PillTone.Info);
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Lock, "防窥时彻底锁定被控端物理键鼠",
                "阻断被控端本机物理鼠标点击任务栏/开始页/右键刷新与键盘输入，仅放行远控操控及 Ctrl+Alt+Shift+P", blockInputSwitch);

            var hwBlackoutSwitch = BuildToggle("物理屏背光降亮", privacyConfig.HardwareBlackout);
            hwBlackoutSwitch.CheckedChanged += delegate
            {
                privacyConfig.HardwareBlackout = hwBlackoutSwitch.Checked;
                SavePrivacyConfig();
                if (privacyCurtain.IsActive && !privacyCurtain.IsPreview)
                {
                    if (hwBlackoutSwitch.Checked) PrivacyDisplayDimmer.DimAllMonitorsAsync(null);
                    else PrivacyDisplayDimmer.RestoreAllAsync();
                }
                ShowDisplayBanner(hwBlackoutSwitch.Checked
                    ? "已开启物理屏背光降亮（仅调节非 HDR 显示器 VCP 0x10 背光亮度，绝不修改对比度与 RGB 色温，断开后回读校验恢复）。"
                    : "已关闭物理屏背光降亮。防窥期间使用防窥壁纸和已配置的物理键鼠锁定。", Theme.PillTone.Info);
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.EyeOff, "防窥时降低物理屏背光亮度（DDC/CI 仅调亮度，可选）",
                "仅将非 HDR 显示器背光亮度（0x10）调低并回读校验恢复，绝不改对比度或 RGB 增益，杜绝退出后过曝偏色", hwBlackoutSwitch);

            var lockSwitch = BuildToggle("退出防窥自动锁屏", privacyConfig.LockOnLocalExit);
            lockSwitch.CheckedChanged += delegate
            {
                privacyConfig.LockOnLocalExit = lockSwitch.Checked;
                SavePrivacyConfig();
                ShowDisplayBanner(lockSwitch.Checked ? "物理键退出防窥后将自动锁定 Windows 桌面。" : "已关闭退出自动锁屏。", Theme.PillTone.Info);
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Lock, "物理键退出防窥时自动锁屏",
                "按下快捷键 Ctrl+Alt+Shift+P 退出防窥时立即锁定本机，防止旁人窥视", lockSwitch);

            privacyHostDisconnectActionCombo = CreateHostDisconnectActionCombo();
            AddStandaloneSettingCard(desk, Theme.Glyph.Lock, "远控退出后主机动作",
                "远控会话结束时解除防窥，并按此设置保留登录或锁定 Windows",
                Theme.WrapInput(privacyHostDisconnectActionCombo), 180);

            var badgeSwitch = BuildToggle("显示时间与提示文案", privacyConfig.ShowOverlayBadge);
            badgeSwitch.CheckedChanged += delegate
            {
                privacyConfig.ShowOverlayBadge = badgeSwitch.Checked;
                SavePrivacyConfig();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Eye, "壁纸居中显示时间与保护文案",
                "关闭后仅展示纯壁纸画面；防窥壁纸不显示退出提示，仅进入时短暂提醒快捷键", badgeSwitch);

            Theme.Row(desk, 6);
            Theme.Row(desk, 42, displayBanner);

            ReloadPreviewImage();
            viewport.ContentWidthChanged += delegate(int contentW) { ApplyPrivacyWallpaperResponsiveLayout(contentW); };
            desk.Height = SumRows(desk);
            return viewport;
        }

        private ComboBox CreateHostDisconnectActionCombo()
        {
            var combo = Theme.Choice("远控退出后主机动作", false);
            combo.Items.AddRange(new object[] { "回到桌面", "自动锁屏" });
            combo.SelectedIndex = privacyConfig.LockOnRemoteDisconnect ? 1 : 0;
            combo.SelectedIndexChanged += delegate
            {
                if (syncingHostDisconnectAction || combo.SelectedIndex < 0) return;
                bool previous = privacyConfig.LockOnRemoteDisconnect;
                privacyConfig.LockOnRemoteDisconnect = combo.SelectedIndex == 1;
                string error;
                if (!privacyConfig.Save(out error)) privacyConfig.LockOnRemoteDisconnect = previous;

                syncingHostDisconnectAction = true;
                try
                {
                    int index = privacyConfig.LockOnRemoteDisconnect ? 1 : 0;
                    if (privacyHostDisconnectActionCombo != null) privacyHostDisconnectActionCombo.SelectedIndex = index;
                    if (settingsHostDisconnectActionCombo != null) settingsHostDisconnectActionCombo.SelectedIndex = index;
                }
                finally
                {
                    syncingHostDisconnectAction = false;
                }

                if (error != null)
                {
                    ShowDisplayBanner(error, Theme.PillTone.Warning);
                    SetStatus(error, Theme.PillTone.Warning);
                }
            };
            return combo;
        }

        private Control BuildPrivacyWallpaperCard()
        {
            var card = new Theme.Card { Padding = new Padding(20, 18, 20, 18) };
            wallpaperCardControl = card;
            var root = Theme.Grid(1);
            Theme.FillColumn(root);
            wallpaperRootLayout = root;

            wallpaperPreview = new WallpaperPreviewPanel(privacyConfig, delegate { return previewCustomImage; });

            var controls = Theme.Grid(1);
            Theme.FillColumn(controls);
            wallpaperControlsPanel = controls;

            Theme.Row(controls, 24, Theme.Text("防窥壁纸主题", Theme.TextSize.LabelBold, Theme.Ink));
            Theme.Row(controls, 8);

            var presetRow = Theme.Grid(7);
            Theme.FillColumn(presetRow);
            Theme.FixedColumn(presetRow, 6);
            Theme.FillColumn(presetRow);
            Theme.FixedColumn(presetRow, 6);
            Theme.FillColumn(presetRow);
            Theme.FixedColumn(presetRow, 6);
            Theme.FillColumn(presetRow);
            presetButtons[0] = MakePresetChip(0, "深空星盾");
            presetButtons[1] = MakePresetChip(1, "极简磨砂");
            presetButtons[2] = MakePresetChip(2, "纯黑零光");
            presetButtons[3] = MakePresetChip(3, "自定义图片");
            Theme.Row(presetRow, 36, presetButtons[0], null, presetButtons[1], null, presetButtons[2], null, presetButtons[3]);
            Theme.Row(controls, 36, presetRow);
            Theme.Row(controls, 12);

            // Custom file + Fit mode
            var imgRow = Theme.Grid(3);
            Theme.FixedColumn(imgRow, 150);
            Theme.FixedColumn(imgRow, 10);
            Theme.FillColumn(imgRow);
            var chooseImg = Theme.Action("选择本地图片", Theme.ButtonStyle.Secondary, Theme.Glyph.Image);
            chooseImg.Click += delegate { ChooseCustomWallpaper(); };

            wallpaperFitCombo = Theme.Choice("图片填充方式", false);
            wallpaperFitCombo.Items.AddRange(new object[] { "铺满裁切 (Cover)", "完整留黑 (Contain)", "拉伸适配 (Stretch)" });
            wallpaperFitCombo.SelectedIndex = Math.Max(0, Math.Min(2, privacyConfig.FitMode));
            wallpaperFitCombo.SelectedIndexChanged += delegate
            {
                privacyConfig.FitMode = wallpaperFitCombo.SelectedIndex;
                SavePrivacyConfig();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            Theme.Row(imgRow, 38, chooseImg, null, Theme.WrapInput(wallpaperFitCombo));
            Theme.Row(controls, 38, imgRow);
            Theme.Row(controls, 12);

            // Caption input
            var capRow = Theme.Grid(3);
            Theme.FixedColumn(capRow, 70);
            Theme.FixedColumn(capRow, 8);
            Theme.FillColumn(capRow);
            wallpaperCaptionBox = Theme.InputBox("防窥提示文案", privacyConfig.Caption);
            wallpaperCaptionBox.MaxLength = 60;
            wallpaperCaptionBox.TextChanged += delegate
            {
                privacyConfig.Caption = wallpaperCaptionBox.Text ?? "";
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            wallpaperCaptionBox.LostFocus += delegate { SavePrivacyConfigSilent(); };
            Theme.Row(capRow, 38, Theme.Text("提示文案", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(wallpaperCaptionBox));
            Theme.Row(controls, 38, capRow);
            Theme.Row(controls, 12);

            // Primary actions
            var actRow = Theme.Grid(5);
            Theme.FixedColumn(actRow, 138);
            Theme.FixedColumn(actRow, 10);
            Theme.FixedColumn(actRow, 122);
            Theme.FixedColumn(actRow, 10);
            Theme.FillColumn(actRow);

            toggleCurtainBtn = Theme.Action("开启防窥", Theme.ButtonStyle.Primary, Theme.Glyph.Shield);
            BindPrivacyToggleButton(toggleCurtainBtn);

            var previewBtn = Theme.Action("全屏预览", Theme.ButtonStyle.Secondary, Theme.Glyph.Eye);
            previewBtn.Click += delegate
            {
                string msg;
                bool ok = privacyCurtain.Activate(privacyConfig, true, out msg);
                ShowDisplayBanner(msg, ok ? Theme.PillTone.Info : Theme.PillTone.Alert);
            };
            Theme.Row(actRow, 38, toggleCurtainBtn, null, previewBtn, null, null);
            Theme.Row(controls, 38, actRow);
            Theme.FillRow(controls);

            wallpaperSplitHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Paper };
            BuildWallpaperSplitContainer(false, 1f);
            wallpaperSplitRow = root.RowCount;
            Theme.Row(root, 236, wallpaperSplitHost);
            Theme.Row(root, 14);
            Theme.Row(root, 1, Theme.Rule());
            Theme.Row(root, 10);

            customImageHint = Theme.Text(DescribeWallpaperSource(), Theme.TextSize.Small, Theme.Muted);
            Theme.Row(root, 24, customImageHint);

            SyncPresetButtonHighlights();
            card.Controls.Add(root);
            return card;
        }

        private void BuildWallpaperSplitContainer(bool compact, float scale)
        {
            if (wallpaperSplitHost == null || wallpaperPreview == null || wallpaperControlsPanel == null) return;
            wallpaperSplitHost.SuspendLayout();
            try
            {
                // Detach reusable children before clearing old wrapper grid
                if (wallpaperPreview.Parent != null) wallpaperPreview.Parent.Controls.Remove(wallpaperPreview);
                if (wallpaperControlsPanel.Parent != null) wallpaperControlsPanel.Parent.Controls.Remove(wallpaperControlsPanel);
                for (int i = wallpaperSplitHost.Controls.Count - 1; i >= 0; i--)
                    wallpaperSplitHost.Controls[i].Dispose();

                if (compact)
                {
                    var stack = Theme.Grid(1);
                    Theme.FillColumn(stack);
                    Theme.Row(stack, 180f * scale, wallpaperPreview);
                    Theme.Row(stack, 14f * scale);
                    Theme.Row(stack, 226f * scale, wallpaperControlsPanel);
                    wallpaperSplitHost.Controls.Add(stack);
                }
                else
                {
                    var split = Theme.Grid(3);
                    Theme.FixedColumn(split, (int)Math.Round(272f * scale));
                    Theme.FixedColumn(split, (int)Math.Round(22f * scale));
                    Theme.FillColumn(split);
                    Theme.Row(split, 236f * scale, wallpaperPreview, null, wallpaperControlsPanel);
                    wallpaperSplitHost.Controls.Add(split);
                }
            }
            finally
            {
                wallpaperSplitHost.ResumeLayout(true);
            }
        }

        private void ApplyPrivacyWallpaperResponsiveLayout(int contentWidth)
        {
            if (!initialScaleApplied || wallpaperRootLayout == null || privacyDesk == null || wallpaperCardRow < 0 || wallpaperSplitRow < 0) return;
            float scale = CurrentDpiScale();
            bool compact = contentWidth < (int)Math.Round(760f * scale);
            if (compact == wallpaperCompactMode) return;
            wallpaperCompactMode = compact;

            BuildWallpaperSplitContainer(compact, scale);
            wallpaperRootLayout.RowStyles[wallpaperSplitRow].Height = (compact ? 420f : 236f) * scale;
            privacyDesk.RowStyles[wallpaperCardRow].Height = SumRows(wallpaperRootLayout)
                + (wallpaperCardControl == null ? 0 : wallpaperCardControl.Padding.Vertical);
            privacyDesk.Height = SumRows(privacyDesk);
            privacyDesk.PerformLayout();
            if (privacyViewport != null) privacyViewport.RefreshLayout();
            NotifyOnboardingLayoutChanged();
        }

        private Button MakePresetChip(int presetIndex, string caption)
        {
            var chip = Theme.Action(caption, Theme.ButtonStyle.Chip);
            chip.Click += delegate
            {
                if (presetIndex == PrivacyWallpaperConfig.PresetCustomImage
                    && (string.IsNullOrEmpty(privacyConfig.CustomImagePath) || !File.Exists(privacyConfig.CustomImagePath)))
                {
                    ChooseCustomWallpaper();
                    return;
                }
                privacyConfig.Preset = presetIndex;
                SavePrivacyConfig();
                SyncPresetButtonHighlights();
                if (customImageHint != null) customImageHint.Text = DescribeWallpaperSource();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
                ShowDisplayBanner("已切换防窥壁纸主题：" + caption, Theme.PillTone.Success);
            };
            return chip;
        }

        private void SyncPresetButtonHighlights()
        {
            for (int i = 0; i < presetButtons.Length; i++)
            {
                if (presetButtons[i] != null) Theme.Highlight(presetButtons[i], privacyConfig.Preset == i);
            }
        }

        private string DescribeWallpaperSource()
        {
            if (privacyConfig.Preset == PrivacyWallpaperConfig.PresetCustomImage && !string.IsNullOrEmpty(privacyConfig.CustomImagePath))
                return "当前自定义壁纸：" + Path.GetFileName(privacyConfig.CustomImagePath) + "  ·  全局快捷键 Ctrl+Alt+Shift+P";
            string[] names = { "深空星盾（内置）", "极简磨砂（内置）", "纯黑零光（内置）", "自定义图片" };
            int idx = Math.Max(0, Math.Min(3, privacyConfig.Preset));
            return "当前壁纸：" + names[idx] + "  ·  全局快捷键 Ctrl+Alt+Shift+P 随时开关";
        }

        private void ChooseCustomWallpaper()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择被控端防窥屏壁纸图片";
                dlg.Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string savedPath, error;
                if (!PrivacyWallpaper.ImportCustomImage(dlg.FileName, out savedPath, out error))
                {
                    ShowDisplayBanner(error, Theme.PillTone.Alert);
                    return;
                }
                privacyConfig.CustomImagePath = savedPath;
                privacyConfig.Preset = PrivacyWallpaperConfig.PresetCustomImage;
                SavePrivacyConfig();
                ReloadPreviewImage();
                SyncPresetButtonHighlights();
                if (customImageHint != null) customImageHint.Text = DescribeWallpaperSource();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
                ShowDisplayBanner("已导入自定义防窥壁纸，点击「全屏预览」或「开启防窥」即可体验。", Theme.PillTone.Success);
            }
        }

        private void ReloadPreviewImage()
        {
            DisposePreviewImage();
            if (!string.IsNullOrEmpty(privacyConfig.CustomImagePath) && File.Exists(privacyConfig.CustomImagePath))
                previewCustomImage = PrivacyWallpaper.LoadImageUnlocked(privacyConfig.CustomImagePath);
        }

        private void DisposePreviewImage()
        {
            if (previewCustomImage != null)
            {
                try { previewCustomImage.Dispose(); } catch { }
                previewCustomImage = null;
            }
        }

        private void SavePrivacyConfig()
        {
            string err;
            if (!privacyConfig.Save(out err)) ShowDisplayBanner(err, Theme.PillTone.Warning);
        }

        private void SavePrivacyConfigSilent()
        {
            string ignored;
            privacyConfig.Save(out ignored);
        }

        private void UpdatePrivacyIndicators()
        {
            bool running = privacyCurtain.IsActive && !privacyCurtain.IsPreview;
            if (curtainStateBadge != null)
            {
                if (running) curtainStateBadge.UpdateStatus("防窥壁纸生效中", Theme.PillTone.Success);
                else if (privacyConfig.AutoOnRemote) curtainStateBadge.UpdateStatus("自动防窥守护中", Theme.PillTone.Info);
                else curtainStateBadge.UpdateStatus("防窥壁纸未开启", Theme.PillTone.Neutral);
            }
            if (toggleCurtainBtn != null)
                toggleCurtainBtn.Text = running ? "关闭防窥" : "开启防窥";
            if (heroPrivacyToggle != null)
                heroPrivacyToggle.Text = running ? "关闭防窥壁纸" : "开启防窥壁纸";
            if (heroPrivacyBadge != null)
            {
                if (running) heroPrivacyBadge.UpdateStatus("防窥生效中", Theme.PillTone.Success);
                else if (privacyConfig.AutoOnRemote) heroPrivacyBadge.UpdateStatus("自动防窥待命", Theme.PillTone.Info);
                else heroPrivacyBadge.UpdateStatus("防窥未开启", Theme.PillTone.Neutral);
            }
            if (topPrivacyBtn != null)
                Theme.SetIndicatorDot(topPrivacyBtn, running || privacyConfig.AutoOnRemote, running ? Theme.Success : Theme.Accent);
        }

        private void ShowDisplayBanner(string text, Theme.PillTone tone)
        {
            displayBanner.Set(text, tone);
        }

        /// <summary>严格锁定 16:9 比例的防窥壁纸实时预览控件（任意拉伸窗口均不变形）。</summary>
        private sealed class WallpaperPreviewPanel : Control
        {
            private readonly PrivacyWallpaperConfig cfg;
            private readonly Func<Image> imageProvider;

            internal WallpaperPreviewPanel(PrivacyWallpaperConfig cfg, Func<Image> imageProvider)
            {
                this.cfg = cfg;
                this.imageProvider = imageProvider;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                BackColor = Theme.Paper;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Paper);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float scale = g.DpiX / 96f;
                float standH = 14f * scale;
                float availW = Math.Max(40f, Width - 4f);
                float availH = Math.Max(30f, Height - standH - 6f);

                // Strictly preserve 16:9 aspect ratio inside available box
                float targetW = availW;
                float targetH = targetW * 9f / 16f;
                if (targetH > availH)
                {
                    targetH = availH;
                    targetW = targetH * 16f / 9f;
                }
                float ox = (Width - targetW) / 2f;
                float oy = Math.Max(2f, (Height - standH - targetH) / 2f);

                var outer = new RectangleF(ox, oy, targetW, targetH);
                Theme.Surface(g, outer, 8f * scale, Color.FromArgb(24, 30, 42), Theme.StrokeStrong, 1.1f);

                int pad = Math.Max(4, (int)Math.Round(5 * scale));
                var screenRect = new Rectangle(
                    (int)Math.Round(outer.X) + pad,
                    (int)Math.Round(outer.Y) + pad,
                    Math.Max(10, (int)Math.Round(outer.Width) - pad * 2),
                    Math.Max(10, (int)Math.Round(outer.Height) - pad * 2));
                using (var clipPath = Theme.Round(screenRect, 4f * scale))
                {
                    var save = g.Save();
                    g.SetClip(clipPath);
                    PrivacyWallpaper.Render(g, screenRect, cfg, imageProvider(), false);
                    g.Restore(save);
                }

                // Minimal monitor stand
                float neckW = 14f * scale;
                float baseW = 54f * scale;
                using (var sb = new SolidBrush(Color.FromArgb(212, 217, 226)))
                {
                    g.FillRectangle(sb, ox + (targetW - neckW) / 2f, outer.Bottom, neckW, 8f * scale);
                    Theme.Surface(g, new RectangleF(ox + (targetW - baseW) / 2f, outer.Bottom + 7f * scale, baseW, 4f * scale), 2f * scale, Color.FromArgb(196, 202, 214), Color.Transparent);
                }
            }
        }
    }
}
