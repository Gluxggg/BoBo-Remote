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
        private readonly Theme.StatusBanner displayBanner = new Theme.StatusBanner("自定义防窥壁纸采用系统级捕获排除（WDA_EXCLUDEFROMCAPTURE），物理屏遮挡、远控画面透明无干扰。");
        private readonly Theme.Badge displayModeBadge = new Theme.Badge("检测中…", Theme.PillTone.Neutral);
        private readonly Theme.Badge curtainStateBadge = new Theme.Badge("防窥壁纸未开启", Theme.PillTone.Neutral);
        private readonly Button[] presetButtons = new Button[4];
        private WallpaperPreviewPanel wallpaperPreview;
        private Button toggleCurtainBtn;
        private ComboBox wallpaperFitCombo;
        private TextBox wallpaperCaptionBox;
        private Label customImageHint;
        private Bitmap previewCustomImage;

        private static string DisplayTool
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "Set-SunshineDisplay.ps1"); }
        }

        private static string OptimizeTool
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "Optimize-SunshineStream.ps1"); }
        }

        private Control BuildPrivacyAndDisplayPage()
        {
            TableLayoutPanel desk;
            var viewport = CreatePage(800, out desk);
            AddPageHeader(desk, "防窥与显示", "自定义被控端防窥屏壁纸，以及 Foundation Sunshine 虚拟屏与熄屏防窥", curtainStateBadge, 154);
            Theme.Row(desk, 14);
            Theme.Row(desk, 442, BuildPrivacyWallpaperCard());
            Theme.Row(desk, 14);
            Theme.Row(desk, 240, BuildHostDisplayCard());
            Theme.Row(desk, 12);
            Theme.Row(desk, 44, displayBanner);
            ReloadPreviewImage();
            return viewport;
        }

        private Control BuildPrivacyWallpaperCard()
        {
            var card = new Theme.Card { Padding = new Padding(20, 16, 20, 14) };
            var root = Theme.Grid(1);
            Theme.FillColumn(root);

            // Header row
            Theme.Row(root, 44, CardHeader(Theme.Glyph.Shield, "自定义被控端防窥屏壁纸", "物理显示器全屏展示自定义壁纸，远控画面透明穿透（支持实体屏与虚拟屏）", Theme.AccentLight, Theme.Accent));
            Theme.Row(root, 12);

            // Upper split: Left 16:9 live preview, Right controls
            var split = Theme.Grid(3);
            Theme.FixedColumn(split, 304);
            Theme.FixedColumn(split, 20);
            Theme.FillColumn(split);

            wallpaperPreview = new WallpaperPreviewPanel(privacyConfig, delegate { return previewCustomImage; });

            var controls = Theme.Grid(1);
            Theme.FillColumn(controls);

            Theme.Row(controls, 20, Theme.Text("壁纸主题（内置高颜值主题或选择本地图片）", Theme.TextSize.Label, Theme.Ink));
            Theme.Row(controls, 6);

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
            Theme.Row(presetRow, 30, presetButtons[0], null, presetButtons[1], null, presetButtons[2], null, presetButtons[3]);
            Theme.Row(controls, 30, presetRow);
            Theme.Row(controls, 10);

            // Custom file + Fit mode row
            var imgRow = Theme.Grid(3);
            Theme.FixedColumn(imgRow, 142);
            Theme.FixedColumn(imgRow, 10);
            Theme.FillColumn(imgRow);
            var pickImg = Theme.Action("选择自定义壁纸…", Theme.ButtonStyle.AccentSoft, Theme.Glyph.Image);
            pickImg.Click += delegate { ChooseCustomWallpaper(); };
            wallpaperFitCombo = Theme.Choice("壁纸填充方式", false);
            wallpaperFitCombo.Items.AddRange(new object[] { "铺满裁切（Cover 推荐）", "完整居中（Contain）", "拉伸填充（Fill）" });
            wallpaperFitCombo.SelectedIndex = privacyConfig.FitMode;
            wallpaperFitCombo.SelectedIndexChanged += delegate
            {
                privacyConfig.FitMode = wallpaperFitCombo.SelectedIndex;
                SavePrivacyConfig();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            Theme.Row(imgRow, 36, pickImg, null, Theme.WrapInput(wallpaperFitCombo));
            Theme.Row(controls, 36, imgRow);
            Theme.Row(controls, 6);

            // Caption + Hint
            var capRow = Theme.Grid(3);
            Theme.FixedColumn(capRow, 78);
            Theme.FixedColumn(capRow, 8);
            Theme.FillColumn(capRow);
            wallpaperCaptionBox = Theme.InputBox("防窥提示文案", privacyConfig.Caption);
            wallpaperCaptionBox.MaxLength = 48;
            wallpaperCaptionBox.TextChanged += delegate
            {
                privacyConfig.Caption = wallpaperCaptionBox.Text;
                SavePrivacyConfigSilent();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            Theme.Row(capRow, 34, Theme.Text("防窥标语", Theme.TextSize.Small, Theme.Muted), null, Theme.WrapInput(wallpaperCaptionBox));
            Theme.Row(controls, 34, capRow);
            Theme.Row(controls, 4);

            customImageHint = Theme.Text(DescribeWallpaperSource(), Theme.TextSize.Small, Theme.Muted);
            Theme.Row(controls, 20, customImageHint);
            Theme.Row(controls, 8);

            // Primary toggle + preview buttons
            var btnRow = Theme.Grid(3);
            Theme.FillColumn(btnRow);
            Theme.FixedColumn(btnRow, 10);
            Theme.FixedColumn(btnRow, 134);
            toggleCurtainBtn = Theme.Action("立即开启防窥壁纸", Theme.ButtonStyle.Primary, Theme.Glyph.Shield);
            toggleCurtainBtn.Click += delegate { TogglePrivacyCurtain(false); };
            var previewBtn = Theme.Action("全屏预览", Theme.ButtonStyle.Secondary, Theme.Glyph.Eye);
            previewBtn.Click += delegate
            {
                string msg;
                bool ok = privacyCurtain.Activate(privacyConfig, true, out msg);
                ShowDisplayBanner(msg, ok ? Theme.PillTone.Info : Theme.PillTone.Alert);
            };
            Theme.Row(btnRow, 38, toggleCurtainBtn, null, previewBtn);
            Theme.Row(controls, 38, btnRow);

            Theme.Row(split, 212, wallpaperPreview, null, controls);
            Theme.Row(root, 212, split);
            Theme.Row(root, 10);

            // Toggles section
            var autoToggle = BuildToggle("远程连接时自动开启防窥壁纸", privacyConfig.AutoOnRemote);
            autoToggle.CheckedChanged += delegate
            {
                privacyConfig.AutoOnRemote = autoToggle.Checked;
                SavePrivacyConfig();
                SyncRemoteGuardTimer();
                ShowDisplayBanner(autoToggle.Checked
                    ? "已开启防窥壁纸自动守护：检测到远程串流接入时自动遮挡物理屏，断开后自动收起。"
                    : "已关闭远程自动防窥守护（仍可随时手动开关或按 Ctrl+Alt+Shift+P）。", Theme.PillTone.Success);
            };
            AddSettingRow(root, "远程连接时自动开启防窥壁纸", "自动监测本机 Sunshine 会话，被控时自动盖上自定义壁纸，断开自动撤销。", autoToggle);

            var badgeToggle = BuildToggle("叠加时间与防窥提示条", privacyConfig.ShowOverlayBadge);
            badgeToggle.CheckedChanged += delegate
            {
                privacyConfig.ShowOverlayBadge = badgeToggle.Checked;
                SavePrivacyConfig();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            AddSettingRow(root, "壁纸叠加时间与防窥提示条", "关闭后全屏只展示纯净的自定义图片，无任何文字遮挡。", badgeToggle);

            var lockToggle = BuildToggle("本地退出防窥时自动锁屏", privacyConfig.LockOnLocalExit);
            lockToggle.CheckedChanged += delegate
            {
                privacyConfig.LockOnLocalExit = lockToggle.Checked;
                SavePrivacyConfig();
            };
            AddSettingRow(root, "本地强退或结束防窥时自动锁屏", "防他人本地按快捷键 Ctrl+Alt+Shift+P 退出防窥偷看桌面，直接锁定 Windows。", lockToggle);

            SyncPresetButtonHighlights();
            card.Controls.Add(root);
            return card;
        }

        private Button MakePresetChip(int presetIndex, string caption)
        {
            var btn = Theme.Action(caption, Theme.ButtonStyle.Chip);
            btn.Click += delegate
            {
                if (presetIndex == PrivacyWallpaperConfig.PresetCustomImage &&
                    (string.IsNullOrEmpty(privacyConfig.CustomImagePath) || !File.Exists(privacyConfig.CustomImagePath)))
                {
                    ChooseCustomWallpaper();
                    return;
                }
                privacyConfig.Preset = presetIndex;
                SavePrivacyConfig();
                SyncPresetButtonHighlights();
                if (customImageHint != null) customImageHint.Text = DescribeWallpaperSource();
                if (wallpaperPreview != null) wallpaperPreview.Invalidate();
            };
            return btn;
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
                return "当前壁纸：自定义图片（" + Path.GetFileName(privacyConfig.CustomImagePath) + "） · 快捷键 Ctrl+Alt+Shift+P";
            string[] names = { "深空星盾（内置）", "极简磨砂（内置）", "纯黑零光（内置）", "自定义图片" };
            int idx = Math.Max(0, Math.Min(3, privacyConfig.Preset));
            return "当前壁纸：" + names[idx] + " · 快捷键 Ctrl+Alt+Shift+P 随时开关";
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
                ShowDisplayBanner("已成功导入自定义防窥壁纸！点击「全屏预览」或「立即开启防窥壁纸」即可体验。", Theme.PillTone.Success);
            }
        }

        private void ReloadPreviewImage()
        {
            DisposePreviewImage();
            if (!string.IsNullOrEmpty(privacyConfig.CustomImagePath) && File.Exists(privacyConfig.CustomImagePath))
            {
                previewCustomImage = PrivacyWallpaper.LoadImageUnlocked(privacyConfig.CustomImagePath);
            }
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

        private Control BuildHostDisplayCard()
        {
            var card = new Theme.Card { Padding = new Padding(20, 14, 20, 12) };
            var content = Theme.Grid(1);
            Theme.FillColumn(content);

            var top = Theme.Grid(3);
            Theme.FillColumn(top);
            Theme.FixedColumn(top, 12);
            Theme.FixedColumn(top, 168);
            Theme.Row(top, 28, Theme.Text("被控端虚拟屏与熄屏防窥（Foundation Sunshine）", Theme.TextSize.Section, Theme.Ink), null, displayModeBadge);
            Theme.Row(content, 28, top);
            Theme.Row(content, 6);

            var preserve = Theme.Action("启用虚拟屏", Theme.ButtonStyle.Secondary);
            preserve.AccessibleName = "启用虚拟屏，保留实体屏";
            preserve.Click += delegate { OpenDisplayTool("Apply", "Virtual"); };
            AddSettingRow(content, "铺满 16:10 虚拟屏（保留实体屏）", "Zako 虚拟屏自动匹配笔记本比例，物理屏保持亮起（推荐搭配上方防窥壁纸）。", preserve);

            var privacy = Theme.Action("启用熄屏防窥", Theme.ButtonStyle.Primary);
            privacy.Click += delegate { OpenDisplayTool("Apply", "Privacy"); };
            AddSettingRow(content, "硬件级熄屏防窥（关闭物理屏）", "远程期间完全关闭物理显示器信号，仅使用虚拟屏，断开后自动恢复。", privacy);

            var restoreRow = Theme.Grid(3);
            Theme.FillColumn(restoreRow);
            Theme.FixedColumn(restoreRow, 8);
            Theme.FixedColumn(restoreRow, 86);
            var undo = Theme.Action("恢复原设置", Theme.ButtonStyle.Secondary);
            undo.Click += delegate { OpenDisplayTool("Restore", "Privacy"); };
            var inspect = Theme.Action("详细状态", Theme.ButtonStyle.AccentSoft);
            inspect.Click += delegate { OpenDisplayTool("Status", "Privacy"); };
            Theme.Row(restoreRow, 34, undo, null, inspect);
            AddSettingRow(content, "原显示拓扑与诊断", "一键还原 Sunshine 显示配置备份，或查看 Zako 驱动运行态。", restoreRow);

            card.Controls.Add(content);
            return card;
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
            {
                toggleCurtainBtn.Text = running ? "关闭防窥壁纸" : "立即开启防窥壁纸";
            }
            if (heroPrivacyToggle != null)
            {
                heroPrivacyToggle.Text = running ? "关闭防窥壁纸" : "开启防窥壁纸";
                Theme.Highlight(heroPrivacyToggle, running);
            }
            if (heroPrivacyBadge != null)
            {
                if (running) heroPrivacyBadge.UpdateStatus("防窥壁纸生效中", Theme.PillTone.Success);
                else if (privacyConfig.AutoOnRemote) heroPrivacyBadge.UpdateStatus("自动防窥待命", Theme.PillTone.OnHero);
                else heroPrivacyBadge.UpdateStatus("防窥未开启", Theme.PillTone.OnHero);
            }
            if (railGuardBadge != null)
            {
                if (running) railGuardBadge.UpdateStatus("防窥壁纸已生效", Theme.PillTone.Success);
                else if (privacyConfig.AutoOnRemote) railGuardBadge.UpdateStatus("自动防窥守护中", Theme.PillTone.Info);
                else railGuardBadge.UpdateStatus("防窥守护未开", Theme.PillTone.Neutral);
            }
            if (displayModeBadge != null)
            {
                string mode = string.IsNullOrEmpty(hostSnapshot.DisplayModeLabel) ? "未检测到宿主" : hostSnapshot.DisplayModeLabel;
                displayModeBadge.UpdateStatus(mode, hostSnapshot.SunshineInstalled ? Theme.PillTone.Info : Theme.PillTone.Neutral);
            }
        }

        private void OpenDisplayTool(string action, string mode)
        {
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
                    : "将自动备份原始配置，并应用 CBR + HRD 硬码率约束 + LTR 弱网参考帧 + 自动 FEC（该组合与历史大帧/Skipping FEC 异常相关，实际改善需会话验证），然后重启 Sunshine 服务，是否继续？";
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

        /// <summary>16:9 防窥壁纸实时预览控件。</summary>
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
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(Theme.Paper)) g.FillRectangle(bg, ClientRectangle);

                // Monitor bezel frame
                var outer = new RectangleF(1f, 4f, Width - 2f, Height - 10f);
                Theme.Surface(g, outer, 12f, Color.FromArgb(18, 24, 38), Theme.StrokeStrong, 1.2f);
                var screenRect = new Rectangle(7, 10, Math.Max(10, Width - 14), Math.Max(10, Height - 22));
                using (var clipPath = Theme.Round(screenRect, 7f))
                {
                    var save = g.Save();
                    g.SetClip(clipPath);
                    PrivacyWallpaper.Render(g, screenRect, cfg, imageProvider(), false);
                    g.Restore(save);
                }
            }
        }
    }
}
