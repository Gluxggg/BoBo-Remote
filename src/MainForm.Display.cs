using System;
using System.Diagnostics;
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
        private readonly Theme.Badge displayModeBadge = new Theme.Badge("检测中…", Theme.PillTone.Neutral);
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
        private TextBox wallpaperCaptionBox;
        private Label customImageHint;
        private Bitmap previewCustomImage;
        private TableLayoutPanel privacyDesk;

        private static string DisplayTool
        {
            get { return EmbeddedTools.ResolveDisplayTool(); }
        }

        private static string OptimizeTool
        {
            get { return EmbeddedTools.ResolveOptimizeTool(); }
        }

        private Control BuildPrivacyAndDisplayPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(980, out desk);
            privacyViewport = viewport;
            privacyDesk = desk;

            curtainStateBadge.AlignRight = true;
            displayModeBadge.AlignRight = true;
            AddPageHeader(desk, "防窥与显示", "自定义被控端物理屏防窥壁纸，以及虚拟屏与硬件级熄屏防窥", curtainStateBadge, 152);
            Theme.Row(desk, 18);

            var wallpaperCard = BuildPrivacyWallpaperCard();
            wallpaperCardRow = desk.RowCount;
            Theme.Row(desk, 314, wallpaperCard);
            Theme.Row(desk, 16);

            Theme.Row(desk, 24, Theme.Text("防窥保护选项", Theme.TextSize.Label, Theme.Muted));
            Theme.Row(desk, 10);

            var autoSwitch = BuildToggle("远控自动防窥", privacyConfig.AutoOnRemote);
            autoSwitch.CheckedChanged += delegate
            {
                privacyConfig.AutoOnRemote = autoSwitch.Checked;
                SavePrivacyConfig();
                SyncRemoteGuardTimer();
                UpdatePrivacyIndicators();
                ShowDisplayBanner(autoSwitch.Checked
                    ? "已开启自动防窥守护：检测到远控连接时自动遮挡物理屏，断开后自动恢复。"
                    : "已关闭自动防窥守护。", Theme.PillTone.Info);
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Shield, "远控接入时自动开启防窥壁纸",
                "检测到 Sunshine 或 RustDesk 会话连入时自动遮挡被控端物理显示器，断开自动收起", autoSwitch);

            var lockSwitch = BuildToggle("退出防窥自动锁屏", privacyConfig.LockOnLocalExit);
            lockSwitch.CheckedChanged += delegate
            {
                privacyConfig.LockOnLocalExit = lockSwitch.Checked;
                SavePrivacyConfig();
                ShowDisplayBanner(lockSwitch.Checked ? "物理键退出防窥后将自动锁定 Windows 桌面。" : "已关闭退出自动锁屏。", Theme.PillTone.Info);
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Lock, "物理键退出防窥时自动锁屏",
                "按下快捷键 Ctrl+Alt+Shift+P 退出防窥时立即锁定本机，防止旁人窥视", lockSwitch);

            var badgeSwitch = BuildToggle("显示时间与提示文案", privacyConfig.ShowOverlayBadge);
            badgeSwitch.CheckedChanged += delegate
            {
                privacyConfig.ShowOverlayBadge = badgeSwitch.Checked;
                SavePrivacyConfig();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            AddStandaloneSettingCard(desk, Theme.Glyph.Eye, "壁纸居中显示时间与保护文案",
                "关闭后仅展示纯壁纸画面；防窥壁纸不显示退出提示，仅进入时短暂提醒快捷键", badgeSwitch);

            Theme.Row(desk, 12);
            var hostHeader = Theme.Grid(3);
            Theme.FillColumn(hostHeader);
            Theme.FixedColumn(hostHeader, 12);
            Theme.FixedColumn(hostHeader, 168);
            Theme.Row(hostHeader, 26, Theme.Text("虚拟屏与硬件熄屏（可选进阶 · 需 Zako + 系统版 Sunshine）", Theme.TextSize.Label, Theme.Muted), null, displayModeBadge);
            Theme.Row(desk, 26, hostHeader);
            Theme.Row(desk, 10);

            var preserve = Theme.Action("启用虚拟屏", Theme.ButtonStyle.Secondary);
            preserve.AccessibleName = "启用虚拟屏，保留实体屏";
            preserve.Click += delegate { OpenDisplayTool("Apply", "Virtual"); };
            AddStandaloneSettingCard(desk, Theme.Glyph.Desktop, "铺满 16:10 虚拟屏（保留实体屏）",
                "需搭配 Zako 虚拟屏与系统安装版 Sunshine，自动匹配 16:10 比例并保留物理屏", preserve, 124);

            var privacy = Theme.Action("启用熄屏防窥", Theme.ButtonStyle.Primary);
            privacy.Click += delegate { OpenDisplayTool("Apply", "Privacy"); };
            AddStandaloneSettingCard(desk, Theme.Glyph.EyeOff, "硬件级熄屏防窥（关闭物理屏信号）",
                "需搭配 Zako 虚拟屏与系统安装版 Sunshine，远程关闭物理显示器信号，断开自动恢复", privacy, 124);

            var restoreRow = Theme.Grid(3);
            Theme.FillColumn(restoreRow);
            Theme.FixedColumn(restoreRow, 8);
            Theme.FixedColumn(restoreRow, 92);
            var undo = Theme.Action("恢复原设置", Theme.ButtonStyle.Secondary);
            undo.Click += delegate { OpenDisplayTool("Restore", "Privacy"); };
            var inspect = Theme.Action("详细状态", Theme.ButtonStyle.Secondary);
            inspect.Click += delegate { OpenDisplayTool("Status", "Privacy"); };
            Theme.Row(restoreRow, 34, undo, null, inspect);
            AddStandaloneSettingCard(desk, Theme.Glyph.Refresh, "显示拓扑还原与诊断",
                "一键还原 Sunshine 显示配置备份，或检查 Zako 虚拟屏驱动状态", restoreRow, 214);

            Theme.Row(desk, 6);
            Theme.Row(desk, 40, displayBanner);

            ReloadPreviewImage();
            viewport.ContentWidthChanged += delegate(int contentW) { ApplyPrivacyWallpaperResponsiveLayout(contentW); };
            desk.Height = SumRows(desk);
            return viewport;
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

            Theme.Row(controls, 22, Theme.Text("防窥壁纸主题", Theme.TextSize.LabelBold, Theme.Ink));
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
            Theme.Row(presetRow, 34, presetButtons[0], null, presetButtons[1], null, presetButtons[2], null, presetButtons[3]);
            Theme.Row(controls, 34, presetRow);
            Theme.Row(controls, 12);

            // Custom file + Fit mode
            var imgRow = Theme.Grid(3);
            Theme.FixedColumn(imgRow, 144);
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
            Theme.Row(imgRow, 36, chooseImg, null, Theme.WrapInput(wallpaperFitCombo));
            Theme.Row(controls, 36, imgRow);
            Theme.Row(controls, 12);

            // Caption input
            var capRow = Theme.Grid(3);
            Theme.FixedColumn(capRow, 64);
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
            Theme.Row(capRow, 36, Theme.Text("提示文案", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(wallpaperCaptionBox));
            Theme.Row(controls, 36, capRow);
            Theme.Row(controls, 12);

            // Primary actions
            var actRow = Theme.Grid(5);
            Theme.FixedColumn(actRow, 132);
            Theme.FixedColumn(actRow, 10);
            Theme.FixedColumn(actRow, 116);
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
            Theme.Row(actRow, 36, toggleCurtainBtn, null, previewBtn, null, null);
            Theme.Row(controls, 36, actRow);
            Theme.FillRow(controls);

            wallpaperSplitHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Paper };
            BuildWallpaperSplitContainer(false, 1f);
            wallpaperSplitRow = root.RowCount;
            Theme.Row(root, 224, wallpaperSplitHost);
            Theme.Row(root, 14);
            Theme.Row(root, 1, Theme.Rule());
            Theme.Row(root, 10);

            customImageHint = Theme.Text(DescribeWallpaperSource(), Theme.TextSize.Small, Theme.Muted);
            Theme.Row(root, 22, customImageHint);

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
                    Theme.Row(stack, 176f * scale, wallpaperPreview);
                    Theme.Row(stack, 14f * scale);
                    Theme.Row(stack, 214f * scale, wallpaperControlsPanel);
                    wallpaperSplitHost.Controls.Add(stack);
                }
                else
                {
                    var split = Theme.Grid(3);
                    Theme.FixedColumn(split, (int)Math.Round(264f * scale));
                    Theme.FixedColumn(split, (int)Math.Round(20f * scale));
                    Theme.FillColumn(split);
                    Theme.Row(split, 224f * scale, wallpaperPreview, null, wallpaperControlsPanel);
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
            wallpaperRootLayout.RowStyles[wallpaperSplitRow].Height = (compact ? 404f : 224f) * scale;
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
            if (displayModeBadge != null)
            {
                string mode = string.IsNullOrEmpty(hostSnapshot.DisplayModeLabel) ? "未检测到宿主" : hostSnapshot.DisplayModeLabel;
                displayModeBadge.UpdateStatus(mode, hostSnapshot.SunshineInstalled ? Theme.PillTone.Info : Theme.PillTone.Neutral);
            }
        }

        private void OpenDisplayTool(string action, string mode)
        {
            if (hostSnapshot.ActiveUsesBundledRuntime || !hostSnapshot.TuningSupported)
            {
                string reason = string.IsNullOrEmpty(hostSnapshot.TuningUnsupportedReason)
                    ? "当前内置宿主不支持一键显示调整；不会改动本机已安装的 Sunshine。"
                    : hostSnapshot.TuningUnsupportedReason;
                ShowDisplayBanner("本次未执行显示调整。" + reason + " 防窥壁纸仍可直接使用。", Theme.PillTone.Warning);
                return;
            }
            if (!File.Exists(DisplayTool))
            {
                ShowDisplayBanner("缺少 tools\\Set-SunshineDisplay.ps1，请完整解压发行包。", Theme.PillTone.Alert);
                return;
            }
            bool changesConfiguration = action != "Status";
            if (changesConfiguration)
            {
                string title = action == "Restore" ? "恢复原显示设置" : mode == "Privacy" ? "启用熄屏防窥" : "启用虚拟屏";
                if (MessageBox.Show(this, "应用后会安全重启空闲的本机 Sunshine 服务。请确认当前没有正在进行的串流。",
                    title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                    return;
            }
            LaunchPowerShellTool(DisplayTool,
                "-Action " + action + " -Mode " + mode + (changesConfiguration ? " -RestartSunshine" : ""),
                changesConfiguration,
                delegate(string msg, Theme.PillTone tone) { ShowDisplayBanner(msg, tone); });
        }

        private void OpenOptimizeTool(string action, string profile)
        {
            if (hostSnapshot.ActiveUsesBundledRuntime || !hostSnapshot.TuningSupported)
            {
                string reason = string.IsNullOrEmpty(hostSnapshot.TuningUnsupportedReason)
                    ? "当前内置宿主已在初始化时默认启用 Auto-FEC 与双栈；系统版一键调优仅针对本机安装版 Sunshine。"
                    : hostSnapshot.TuningUnsupportedReason;
                ShowSettingsBanner(reason, Theme.PillTone.Warning);
                return;
            }
            if (!File.Exists(OptimizeTool))
            {
                ShowSettingsBanner("缺少 tools\\Optimize-SunshineStream.ps1，请完整解压发行包。", Theme.PillTone.Alert);
                return;
            }
            bool changesConfiguration = action == "Apply" || action == "Restore";
            if (changesConfiguration)
            {
                string title = action == "Restore" ? "还原原始编码与网络配置" : "应用低延迟与抗丢包调优";
                string prompt = action == "Restore"
                    ? "将从备份还原调优前的原始 sunshine.conf 编码/网络参数并重启 Sunshine 服务，是否继续？"
                    : "将自动备份原始配置，并应用 CBR + HRD 硬码率约束 + LTR 弱网参考帧 + 自动 FEC，然后重启 Sunshine 服务，是否继续？";
                if (MessageBox.Show(this, prompt, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) != DialogResult.OK)
                    return;
            }
            LaunchPowerShellTool(OptimizeTool,
                "-Action " + action + " -Profile " + profile + (changesConfiguration ? " -RestartSunshine" : ""),
                changesConfiguration,
                delegate(string msg, Theme.PillTone tone) { ShowSettingsBanner(msg, tone); });
        }

        private void LaunchPowerShellTool(string scriptPath, string extraArgs, bool runElevated, Action<string, Theme.PillTone> feedback)
        {
            try
            {
                string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(shell)
                {
                    Arguments = "-NoProfile -NoExit -ExecutionPolicy Bypass -File \"" + scriptPath + "\" " + extraArgs,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                    Verb = runElevated ? "runas" : "open"
                };
                using (Process launched = Process.Start(start)) { }
                feedback("已在独立窗口执行操作，完成后点「刷新诊断」即可同步最新状态。", Theme.PillTone.Success);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                feedback(error.NativeErrorCode == 1223 ? "已取消权限授权，未做任何修改。" : "无法启动本地调优脚本。", Theme.PillTone.Warning);
            }
            catch (InvalidOperationException)
            {
                feedback("无法启动本地调优脚本。", Theme.PillTone.Alert);
            }
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
