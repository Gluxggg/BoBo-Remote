using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Theme = DualRemote.RemoteTheme;

namespace DualRemote
{
    internal enum ControlResolutionMode
    {
        FollowLocal = 0,
        FollowHost = 1,
        Custom = 2
    }

    internal enum ControlMouseMode
    {
        Free = 0,
        Locked = 1,
        ViewOnly = 2
    }

    /// <summary>
    /// 控制中心实时状态与回调模型（由 NativeStreamForm / MainForm 共同驱动，
    /// 窗口顶栏「控制中心」按钮与全屏贴边悬浮球复用同一份完整菜单与实时状态）。
    /// </summary>
    internal sealed class StreamControlCenterModel
    {
        internal string HostDisplayName = "远程主机";
        internal string DisplayDeviceName = "显示屏 1";
        internal List<BoBoMonitorDescriptor> RemoteMonitors;
        internal int SelectedMonitorIndex;
        internal Action<int> OnSelectMonitor;
        internal int LocalDisplayWidth = 1920;
        internal int LocalDisplayHeight = 1080;
        internal int HostNativeWidth = 1920;
        internal int HostNativeHeight = 1080;
        internal int CurrentWidth = 1920;
        internal int CurrentHeight = 1080;
        internal int DisplayScalePercent = 100;
        internal ControlResolutionMode ResolutionMode = ControlResolutionMode.FollowHost;

        internal int BitrateKbps = 14000;
        internal bool AutoBitrate = true;
        internal int Fps = 60;
        internal bool TrueColor = false;
        internal bool HdrEnabled = false;

        internal bool AspectFit = true;
        internal int ScaleMode = 1; // 0=1:1, 1=AspectFit, 2=Stretch
        internal bool SuperDisplayActive;
        internal string SuperDisplayStatus = "";
        internal string ConnectionRoute = "";
        internal bool MiniWindowMode = false;
        internal int MiniWindowOpacity = 100;
        internal bool IsFullscreen = false;

        internal int VolumePercent = 100;
        internal bool AudioMuted = false;

        internal bool LockHostOnDisconnect = false;
        internal bool MuteHostAudio = true;
        internal bool RestoreHostAudioOnDisconnect = true;
        internal bool RemotePrivacyActive = false;
        internal bool AutoRemotePrivacyOnConnect = false;
        internal bool LocalPrivacyActive = false;

        internal bool SendSystemShortcuts = false;
        internal ControlMouseMode MouseMode = ControlMouseMode.Free;
        internal bool MicEnabled = false;

        internal bool ShowNetworkStats = false;
        internal bool HideFloatBallInFullscreen = false;
        internal bool ShowFloatBallInWindowed = false;

        // Callbacks
        internal Action<int, int, ControlResolutionMode> OnSelectResolution;
        internal Action<int> OnSelectDisplayScale;
        internal Action OnToggleSuperDisplay;
        internal Action OnSwitchNextDisplay;

        internal Action<int, bool> OnSelectBitrate;
        internal Action<int> OnSelectFps;
        internal Action<bool> OnSelectTrueColor;
        internal Action<bool> OnToggleHdr;
        internal Action OnSelectWeakNetwork;

        internal Action<bool> OnSelectAspectFit;
        internal Action<int> OnSelectScaleMode;
        internal Action OnToggleMiniWindow;
        internal Action<int> OnSelectMiniOpacity;

        internal Action<int, bool> OnVolumeChanged;

        internal Action<bool> OnToggleLockHostOnDisconnect;
        internal Action<bool> OnToggleMuteHostAudio;
        internal Action<bool> OnToggleRestoreHostAudio;
        internal Action<bool> OnSetRemotePrivacyNow;
        internal Action<bool> OnToggleAutoRemotePrivacy;
        internal Action OnToggleLocalPrivacy;

        internal Action<bool> OnToggleSendSystemShortcuts;
        internal Action<ControlMouseMode> OnSelectMouseMode;
        internal Action OnToggleMic;

        internal Action<int[]> OnSendRemoteKeys;
        internal Action OnSendClipboardText;
        internal Action OnCaptureScreenshot;
        internal Action OnRestoreMainWindow;

        internal Action<bool> OnToggleNetworkStats;
        internal Action<bool> OnToggleHideFloatBallInFullscreen;
        internal Action<bool> OnToggleShowFloatBallInWindowed;

        internal Action OnToggleFullscreen;
        internal Action OnDisconnect;
    }

    internal enum CcIcon
    {
        None = 0,
        Monitor,
        SwitchMonitor,
        Quality,
        Window,
        Sound,
        SoundMuted,
        Shield,
        Peripheral,
        Tool,
        More,
        Fullscreen,
        ExitFullscreen,
        Exit,
        SuperScreen,
        MiniWindow,
        Opacity,
        PrivacyEye,
        Keyboard,
        Mouse,
        Screenshot,
        Clipboard,
        Home,
        Lock,
        Mic,
        Activity
    }

    internal sealed class CcMenuItem
    {
        internal string Text = "";
        internal string Subtitle = "";
        internal string Shortcut = "";
        internal CcIcon Icon = CcIcon.None;
        internal bool IsSeparator;
        internal bool IsHeader;
        internal bool IsDanger;
        internal bool Enabled = true;
        internal bool RadioSelected;
        internal bool CheckSelected;
        internal bool CloseOnClick = true;
        internal Action OnClick;
        internal Func<List<CcMenuItem>> SubmenuBuilder;
        internal bool IsVolumeSliderSubmenu;
    }

    /// <summary>
    /// 对齐 UU远程「控制中心」的高清浅色圆角级联菜单宿主（支持悬停多级展开、子标题说明、分组头、
    /// 实时音量滑块条、高清矢量图标、快捷键提示、DPI 自适应与安全斜向移动防误关闭）。
    /// </summary>
    internal sealed class UuControlCenterSession : IDisposable
    {
        private readonly List<UuMenuPopupForm> activePopups = new List<UuMenuPopupForm>();
        private readonly OutsideClickFilter clickFilter;
        private readonly StreamControlCenterModel model;
        private readonly Action onClosed;
        private bool closed = true;

        internal bool IsOpen { get { return !closed && activePopups.Count > 0; } }

        internal UuControlCenterSession(StreamControlCenterModel model, Action onClosed)
        {
            this.model = model ?? new StreamControlCenterModel();
            this.onClosed = onClosed;
            this.clickFilter = new OutsideClickFilter(this);
        }

        internal void ShowAt(Point anchorScreenPoint, Rectangle anchorRect, Form ownerForm)
        {
            CloseAll();
            closed = false;
            List<CcMenuItem> rootItems = BuildRootMenuItems(model, this);
            var root = new UuMenuPopupForm(this, 0, rootItems, false, model);
            activePopups.Add(root);

            Screen scr = Screen.FromRectangle(anchorRect.IsEmpty ? new Rectangle(anchorScreenPoint, new Size(1, 1)) : anchorRect);
            if (scr == null) scr = Screen.PrimaryScreen;
            Rectangle wa = scr.Bounds;

            int x = anchorScreenPoint.X;
            int y = anchorScreenPoint.Y;
            if (x + root.Width > wa.Right - 8)
                x = Math.Max(wa.Left + 8, (anchorRect.IsEmpty ? wa.Right - 8 : anchorRect.Right) - root.Width);
            if (x < wa.Left + 8) x = wa.Left + 8;
            if (y + root.Height > wa.Bottom - 8)
            {
                int aboveY = (anchorRect.IsEmpty ? anchorScreenPoint.Y : anchorRect.Top) - root.Height - 6;
                y = aboveY >= wa.Top + 8 ? aboveY : Math.Max(wa.Top + 8, wa.Bottom - root.Height - 8);
            }
            if (y < wa.Top + 8) y = wa.Top + 8;

            root.Location = new Point(x, y);
            Application.AddMessageFilter(clickFilter);
            if (ownerForm != null && !ownerForm.IsDisposed && ownerForm.IsHandleCreated)
                root.Show(ownerForm);
            else
                root.Show();
            root.EnsureTopMost();
        }

        internal void OpenSubmenu(int parentLevel, Rectangle itemScreenRect, CcMenuItem item, Form ownerForm)
        {
            if (closed) return;
            CloseFromLevel(parentLevel + 1);
            if (item == null || (!item.IsVolumeSliderSubmenu && item.SubmenuBuilder == null)) return;

            UuMenuPopupForm sub;
            if (item.IsVolumeSliderSubmenu)
            {
                sub = new UuMenuPopupForm(this, parentLevel + 1, null, true, model);
            }
            else
            {
                List<CcMenuItem> items = item.SubmenuBuilder();
                if (items == null || items.Count == 0) return;
                sub = new UuMenuPopupForm(this, parentLevel + 1, items, false, model);
            }

            Screen scr = Screen.FromRectangle(itemScreenRect);
            if (scr == null) scr = Screen.PrimaryScreen;
            Rectangle wa = scr.Bounds;

            int gap = 6;
            int x = itemScreenRect.Right + gap;
            if (x + sub.Width > wa.Right - 8)
                x = itemScreenRect.Left - sub.Width - gap;
            if (x < wa.Left + 8) x = wa.Left + 8;

            int y = itemScreenRect.Top - 6;
            if (y + sub.Height > wa.Bottom - 8)
                y = Math.Max(wa.Top + 8, wa.Bottom - sub.Height - 8);
            if (y < wa.Top + 8) y = wa.Top + 8;

            sub.Location = new Point(x, y);
            activePopups.Add(sub);
            if (ownerForm != null && !ownerForm.IsDisposed && ownerForm.IsHandleCreated)
                sub.Show(ownerForm);
            else if (activePopups.Count > 1)
                sub.Show(activePopups[0]);
            else
                sub.Show();
            sub.EnsureTopMost();
        }

        internal void CloseFromLevel(int minLevel)
        {
            for (int i = activePopups.Count - 1; i >= 0; i--)
            {
                if (activePopups[i].Level >= minLevel)
                {
                    UuMenuPopupForm f = activePopups[i];
                    activePopups.RemoveAt(i);
                    try { if (!f.IsDisposed) f.Close(); } catch { }
                    try { f.Dispose(); } catch { }
                }
            }
        }

        internal bool ContainsScreenPoint(Point screenPt)
        {
            for (int i = 0; i < activePopups.Count; i++)
            {
                UuMenuPopupForm f = activePopups[i];
                if (f != null && !f.IsDisposed && f.Visible && f.Bounds.Contains(screenPt))
                    return true;
            }
            return false;
        }

        internal void CloseAll()
        {
            if (closed && activePopups.Count == 0) return;
            closed = true;
            try { Application.RemoveMessageFilter(clickFilter); } catch { }
            CloseFromLevel(0);
            if (onClosed != null)
            {
                try { onClosed(); } catch { }
            }
        }

        public void Dispose()
        {
            CloseAll();
        }

        internal static List<CcMenuItem> BuildRootMenuItems(StreamControlCenterModel m, UuControlCenterSession session)
        {
            var list = new List<CcMenuItem>();

            // 1. 显示屏 1
            string dispTitle = string.IsNullOrEmpty(m.DisplayDeviceName)
                ? ("显示屏 1 (" + m.CurrentWidth + "×" + m.CurrentHeight + ")")
                : m.DisplayDeviceName;
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Monitor,
                Text = dispTitle,
                SubmenuBuilder = delegate { return BuildDisplaySubmenu(m); }
            });

            // 2. 切换下一个屏幕
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.SwitchMonitor,
                Text = "切换下一个屏幕",
                Shortcut = "Ctrl+Alt+→",
                OnClick = delegate { if (m.OnSwitchNextDisplay != null) m.OnSwitchNextDisplay(); }
            });

            list.Add(new CcMenuItem { IsSeparator = true });

            // 3. 画质
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Quality,
                Text = "画质",
                SubmenuBuilder = delegate { return BuildQualitySubmenu(m); }
            });

            // 4. 窗口
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Window,
                Text = "窗口",
                SubmenuBuilder = delegate { return BuildWindowSubmenu(m); }
            });

            // 5. 声音（实时音量滑块条）
            list.Add(new CcMenuItem
            {
                Icon = (m.AudioMuted || m.VolumePercent <= 0) ? CcIcon.SoundMuted : CcIcon.Sound,
                Text = "声音",
                IsVolumeSliderSubmenu = true
            });

            // 6. 安全
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Shield,
                Text = "安全",
                SubmenuBuilder = delegate { return BuildSecuritySubmenu(m); }
            });

            // 7. 外设
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Peripheral,
                Text = "外设",
                SubmenuBuilder = delegate { return BuildPeripheralSubmenu(m, session); }
            });

            // 8. 工具
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Tool,
                Text = "工具",
                SubmenuBuilder = delegate { return BuildToolsSubmenu(m); }
            });

            // 9. 更多
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.More,
                Text = "更多",
                SubmenuBuilder = delegate { return BuildMoreSubmenu(m); }
            });

            list.Add(new CcMenuItem { IsSeparator = true });

            // 10. 全屏
            list.Add(new CcMenuItem
            {
                Icon = m.IsFullscreen ? CcIcon.ExitFullscreen : CcIcon.Fullscreen,
                Text = m.IsFullscreen ? "退出全屏" : "全屏",
                Shortcut = "Ctrl+Alt+F",
                OnClick = delegate { if (m.OnToggleFullscreen != null) m.OnToggleFullscreen(); }
            });

            list.Add(new CcMenuItem { IsSeparator = true });

            // 11. 退出远控
            list.Add(new CcMenuItem
            {
                Icon = CcIcon.Exit,
                Text = "退出远控",
                IsDanger = true,
                OnClick = delegate { if (m.OnDisconnect != null) m.OnDisconnect(); }
            });

            return list;
        }

        private static List<CcMenuItem> BuildDisplaySubmenu(StreamControlCenterModel m)
        {
            var items = new List<CcMenuItem>();
            if (m.RemoteMonitors != null && m.RemoteMonitors.Count > 1)
            {
                for (int i = 0; i < m.RemoteMonitors.Count; i++)
                {
                    int index = i;
                    BoBoMonitorDescriptor monitor = m.RemoteMonitors[i];
                    items.Add(new CcMenuItem
                    {
                        Text = "显示屏 " + (index + 1) + " · " + monitor.Width + " × " + monitor.Height,
                        Subtitle = monitor.Name,
                        RadioSelected = m.SelectedMonitorIndex == index,
                        OnClick = delegate { if (m.OnSelectMonitor != null) m.OnSelectMonitor(index); }
                    });
                }
                items.Add(new CcMenuItem { IsSeparator = true });
            }
            int lw = m.LocalDisplayWidth > 0 ? m.LocalDisplayWidth : 1920;
            int lh = m.LocalDisplayHeight > 0 ? m.LocalDisplayHeight : 1080;
            int hw = m.HostNativeWidth > 0 ? m.HostNativeWidth : m.CurrentWidth;
            int hh = m.HostNativeHeight > 0 ? m.HostNativeHeight : m.CurrentHeight;

            items.Add(new CcMenuItem
            {
                Text = "跟随画面区域 (" + lw + " × " + lh + ")",
                RadioSelected = m.ResolutionMode == ControlResolutionMode.FollowLocal,
                OnClick = delegate
                {
                    if (m.OnSelectResolution != null) m.OnSelectResolution(lw, lh, ControlResolutionMode.FollowLocal);
                }
            });
            items.Add(new CcMenuItem
            {
                Text = "跟随被控端 (" + hw + " × " + hh + ")",
                RadioSelected = m.ResolutionMode == ControlResolutionMode.FollowHost,
                OnClick = delegate
                {
                    if (m.OnSelectResolution != null) m.OnSelectResolution(hw, hh, ControlResolutionMode.FollowHost);
                }
            });
            items.Add(new CcMenuItem
            {
                Text = "其他分辨率",
                RadioSelected = m.ResolutionMode == ControlResolutionMode.Custom,
                SubmenuBuilder = delegate
                {
                    var resList = new List<CcMenuItem>();
                    int[,] presets = new int[,]
                    {
                        { 3840, 2160 },
                        { 2560, 1600 },
                        { 2560, 1440 },
                        { 1920, 1200 },
                        { 1920, 1080 },
                        { 1600, 900 },
                        { 1280, 720 }
                    };
                    for (int i = 0; i < presets.GetLength(0); i++)
                    {
                        int pw = presets[i, 0];
                        int ph = presets[i, 1];
                        resList.Add(new CcMenuItem
                        {
                            Text = pw + " × " + ph,
                            RadioSelected = m.ResolutionMode == ControlResolutionMode.Custom && m.CurrentWidth == pw && m.CurrentHeight == ph,
                            OnClick = delegate
                            {
                                if (m.OnSelectResolution != null) m.OnSelectResolution(pw, ph, ControlResolutionMode.Custom);
                            }
                        });
                    }
                    return resList;
                }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            int[] scales = new[] { 100, 125, 150, 175, 200 };
            foreach (int pct in scales)
            {
                int captured = pct;
                string suffix = (captured == 100 && lw <= 1920) || (captured == 150 && lw >= 2560 && lw < 2800) || (captured == 200 && lw >= 2800)
                    ? " (推荐)"
                    : "";
                items.Add(new CcMenuItem
                {
                    Text = "缩放 " + captured + "%" + suffix,
                    RadioSelected = m.DisplayScalePercent == captured,
                    OnClick = delegate
                    {
                        if (m.OnSelectDisplayScale != null) m.OnSelectDisplayScale(captured);
                    }
                });
            }
            items.Add(new CcMenuItem
            {
                Text = "其他缩放",
                RadioSelected = m.DisplayScalePercent > 200,
                SubmenuBuilder = delegate
                {
                    var moreScales = new List<CcMenuItem>();
                    foreach (int s in new[] { 225, 250, 300 })
                    {
                        int cap = s;
                        moreScales.Add(new CcMenuItem
                        {
                            Text = "缩放 " + cap + "%",
                            RadioSelected = m.DisplayScalePercent == cap,
                            OnClick = delegate { if (m.OnSelectDisplayScale != null) m.OnSelectDisplayScale(cap); }
                        });
                    }
                    return moreScales;
                }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            items.Add(new CcMenuItem
            {
                Icon = CcIcon.SuperScreen,
                Text = "超级屏",
                Subtitle = m.SuperDisplayStatus,
                CheckSelected = m.SuperDisplayActive,
                OnClick = delegate { if (m.OnToggleSuperDisplay != null) m.OnToggleSuperDisplay(); }
            });
            return items;
        }

        private static List<CcMenuItem> BuildQualitySubmenu(StreamControlCenterModel m)
        {
            var items = new List<CcMenuItem>();
            items.Add(new CcMenuItem
            {
                Text = "自动 (高清)",
                RadioSelected = m.AutoBitrate,
                OnClick = delegate { if (m.OnSelectBitrate != null) m.OnSelectBitrate(10000, true); }
            });
            items.Add(new CcMenuItem
            {
                Text = "原画 30M",
                RadioSelected = !m.AutoBitrate && m.BitrateKbps == 30000,
                OnClick = delegate { if (m.OnSelectBitrate != null) m.OnSelectBitrate(30000, false); }
            });
            items.Add(new CcMenuItem
            {
                Text = "超清 14M",
                RadioSelected = !m.AutoBitrate && m.BitrateKbps == 14000,
                OnClick = delegate { if (m.OnSelectBitrate != null) m.OnSelectBitrate(14000, false); }
            });
            items.Add(new CcMenuItem
            {
                Text = "高清 8M",
                RadioSelected = !m.AutoBitrate && m.BitrateKbps == 8000,
                OnClick = delegate { if (m.OnSelectBitrate != null) m.OnSelectBitrate(8000, false); }
            });
            items.Add(new CcMenuItem
            {
                Text = "自定义",
                RadioSelected = !m.AutoBitrate && m.BitrateKbps != 30000 && m.BitrateKbps != 14000 && m.BitrateKbps != 8000,
                SubmenuBuilder = delegate
                {
                    var custom = new List<CcMenuItem>();
                    int[] rates = new[] { 50000, 36000, 22000, 18000, 12000, 6000, 4000 };
                    foreach (int kbps in rates)
                    {
                        int cap = kbps;
                        custom.Add(new CcMenuItem
                        {
                            Text = (cap / 1000) + " Mbps" + (cap >= 36000 ? " (局域网高码)" : (cap <= 6000 ? " (弱网抗卡顿)" : "")),
                            RadioSelected = !m.AutoBitrate && m.BitrateKbps == cap,
                            OnClick = delegate { if (m.OnSelectBitrate != null) m.OnSelectBitrate(cap, false); }
                        });
                    }
                    return custom;
                }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            foreach (int fps in new[] { 144, 90, 60, 30 })
            {
                int cap = fps;
                items.Add(new CcMenuItem
                {
                    Text = cap + " 帧",
                    RadioSelected = m.Fps == cap,
                    OnClick = delegate { if (m.OnSelectFps != null) m.OnSelectFps(cap); }
                });
            }

            items.Add(new CcMenuItem { IsSeparator = true });

            items.Add(new CcMenuItem
            {
                Text = "真彩",
                RadioSelected = m.TrueColor,
                OnClick = delegate { if (m.OnSelectTrueColor != null) m.OnSelectTrueColor(true); }
            });
            items.Add(new CcMenuItem
            {
                Text = "常规色彩",
                RadioSelected = !m.TrueColor,
                OnClick = delegate { if (m.OnSelectTrueColor != null) m.OnSelectTrueColor(false); }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            items.Add(new CcMenuItem
            {
                Text = "HDR",
                CheckSelected = m.HdrEnabled,
                OnClick = delegate { if (m.OnToggleHdr != null) m.OnToggleHdr(!m.HdrEnabled); }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Activity,
                Text = "弱网抗丢包",
                Subtitle = "720p · 30 帧 · 3.5 Mbps，自适应码率",
                OnClick = m.OnSelectWeakNetwork
            });
            return items;
        }

        private static List<CcMenuItem> BuildWindowSubmenu(StreamControlCenterModel m)
        {
            var items = new List<CcMenuItem>();
            items.Add(new CcMenuItem
            {
                Text = "拉伸铺满",
                RadioSelected = m.ScaleMode == 2,
                OnClick = delegate
                {
                    if (m.OnSelectScaleMode != null) m.OnSelectScaleMode(2);
                    else if (m.OnSelectAspectFit != null) m.OnSelectAspectFit(true);
                }
            });
            items.Add(new CcMenuItem
            {
                Text = "等比居中（保留原比例）",
                RadioSelected = m.ScaleMode == 1,
                OnClick = delegate
                {
                    if (m.OnSelectScaleMode != null) m.OnSelectScaleMode(1);
                    else if (m.OnSelectAspectFit != null) m.OnSelectAspectFit(true);
                }
            });
            items.Add(new CcMenuItem
            {
                Text = "实际大小 1:1",
                RadioSelected = m.ScaleMode == 0,
                OnClick = delegate
                {
                    if (m.OnSelectScaleMode != null) m.OnSelectScaleMode(0);
                    else if (m.OnSelectAspectFit != null) m.OnSelectAspectFit(false);
                }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            items.Add(new CcMenuItem
            {
                Icon = CcIcon.MiniWindow,
                Text = m.MiniWindowMode ? "退出小窗模式" : "小窗模式",
                Shortcut = "Ctrl+Alt+U",
                CheckSelected = m.MiniWindowMode,
                OnClick = delegate { if (m.OnToggleMiniWindow != null) m.OnToggleMiniWindow(); }
            });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Opacity,
                Text = "小窗不透明度",
                SubmenuBuilder = delegate
                {
                    var ops = new List<CcMenuItem>();
                    foreach (int pct in new[] { 100, 90, 80, 70, 60, 50 })
                    {
                        int cap = pct;
                        ops.Add(new CcMenuItem
                        {
                            Text = cap + "%",
                            RadioSelected = m.MiniWindowOpacity == cap,
                            OnClick = delegate { if (m.OnSelectMiniOpacity != null) m.OnSelectMiniOpacity(cap); }
                        });
                    }
                    return ops;
                }
            });
            return items;
        }

        private static List<CcMenuItem> BuildSecuritySubmenu(StreamControlCenterModel m)
        {
            var items = new List<CcMenuItem>();
            items.Add(new CcMenuItem
            {
                Text = "远程结束后，被控端自动锁屏",
                CheckSelected = m.LockHostOnDisconnect,
                OnClick = delegate { if (m.OnToggleLockHostOnDisconnect != null) m.OnToggleLockHostOnDisconnect(!m.LockHostOnDisconnect); }
            });
            items.Add(new CcMenuItem
            {
                Text = "被控端静音运行",
                Subtitle = "开启后会将被控端设置为静音，控制端仍能播放被控端声音。",
                CheckSelected = m.MuteHostAudio,
                OnClick = delegate { if (m.OnToggleMuteHostAudio != null) m.OnToggleMuteHostAudio(!m.MuteHostAudio); }
            });
            items.Add(new CcMenuItem
            {
                Text = "远程结束后，恢复被控端声音设置",
                Enabled = m.MuteHostAudio,
                CheckSelected = m.MuteHostAudio && m.RestoreHostAudioOnDisconnect,
                OnClick = delegate { if (m.OnToggleRestoreHostAudio != null) m.OnToggleRestoreHostAudio(!m.RestoreHostAudioOnDisconnect); }
            });

            items.Add(new CcMenuItem { IsSeparator = true });

            items.Add(new CcMenuItem
            {
                Icon = CcIcon.PrivacyEye,
                Text = "被控端防窥模式",
                SubmenuBuilder = delegate
                {
                    var p = new List<CcMenuItem>();
                    p.Add(new CcMenuItem
                    {
                        Text = "立即开启被控端防窥",
                        CheckSelected = m.RemotePrivacyActive,
                        OnClick = delegate { if (m.OnSetRemotePrivacyNow != null) m.OnSetRemotePrivacyNow(true); }
                    });
                    p.Add(new CcMenuItem
                    {
                        Text = "立即关闭被控端防窥",
                        CheckSelected = !m.RemotePrivacyActive,
                        OnClick = delegate { if (m.OnSetRemotePrivacyNow != null) m.OnSetRemotePrivacyNow(false); }
                    });
                    p.Add(new CcMenuItem { IsSeparator = true });
                    p.Add(new CcMenuItem
                    {
                        Text = "被控连接瞬间自动进入防窥模式",
                        Subtitle = "客户端启用后，每次连接成功将立即通知被控端进入防窥屏。",
                        CheckSelected = m.AutoRemotePrivacyOnConnect,
                        OnClick = delegate { if (m.OnToggleAutoRemotePrivacy != null) m.OnToggleAutoRemotePrivacy(!m.AutoRemotePrivacyOnConnect); }
                    });
                    p.Add(new CcMenuItem { IsSeparator = true });
                    p.Add(new CcMenuItem
                    {
                        Text = m.LocalPrivacyActive ? "关闭本机防窥壁纸" : "开启本机防窥壁纸",
                        Shortcut = "Ctrl+Alt+Shift+P",
                        CheckSelected = m.LocalPrivacyActive,
                        OnClick = delegate { if (m.OnToggleLocalPrivacy != null) m.OnToggleLocalPrivacy(); }
                    });
                    return p;
                }
            });
            return items;
        }

        private static List<CcMenuItem> BuildPeripheralSubmenu(StreamControlCenterModel m, UuControlCenterSession session)
        {
            var items = new List<CcMenuItem>();
            items.Add(new CcMenuItem { IsHeader = true, Text = "键盘" });
            items.Add(new CcMenuItem
            {
                Text = "发送系统快捷键到被控端",
                Subtitle = "开启时，系统快捷键（如 Alt+Tab、Win 键）将传给被控端。BoBo 退出与释放鼠标快捷键始终本地生效。",
                CheckSelected = m.SendSystemShortcuts,
                OnClick = delegate { if (m.OnToggleSendSystemShortcuts != null) m.OnToggleSendSystemShortcuts(!m.SendSystemShortcuts); }
            });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Keyboard,
                Text = "配置快捷键",
                OnClick = delegate { ShowShortcutsHelpDialog(); }
            });

            items.Add(new CcMenuItem { IsSeparator = true });
            items.Add(new CcMenuItem { IsHeader = true, Text = "鼠标" });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Mouse,
                Text = "鼠标模式",
                SubmenuBuilder = delegate
                {
                    var modes = new List<CcMenuItem>();
                    modes.Add(new CcMenuItem
                    {
                        Text = "自由指针模式 (推荐办公)",
                        Subtitle = "指针可自由移出窗口并直接点击悬浮球与本机任务栏。",
                        RadioSelected = m.MouseMode == ControlMouseMode.Free,
                        OnClick = delegate { if (m.OnSelectMouseMode != null) m.OnSelectMouseMode(ControlMouseMode.Free); }
                    });
                    modes.Add(new CcMenuItem
                    {
                        Text = "锁鼠游戏模式 (FPS 视角)",
                        Subtitle = "隐藏指针并启用相对移动；随时轻按 Ctrl+Alt 立即释放鼠标。",
                        RadioSelected = m.MouseMode == ControlMouseMode.Locked,
                        OnClick = delegate { if (m.OnSelectMouseMode != null) m.OnSelectMouseMode(ControlMouseMode.Locked); }
                    });
                    modes.Add(new CcMenuItem
                    {
                        Text = "仅查看模式 (禁止输入)",
                        Subtitle = "暂停向被控端发送键鼠事件，防止误操作。",
                        RadioSelected = m.MouseMode == ControlMouseMode.ViewOnly,
                        OnClick = delegate { if (m.OnSelectMouseMode != null) m.OnSelectMouseMode(ControlMouseMode.ViewOnly); }
                    });
                    return modes;
                }
            });

            items.Add(new CcMenuItem { IsSeparator = true });
            items.Add(new CcMenuItem { IsHeader = true, Text = "麦克风" });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Mic,
                Text = "开启麦克风",
                Subtitle = "Moonlight 串流暂不承载上行语音，建议配合语音工具或 RustDesk 音频通道。",
                CheckSelected = m.MicEnabled,
                OnClick = delegate { if (m.OnToggleMic != null) m.OnToggleMic(); }
            });

            return items;
        }

        private static List<CcMenuItem> BuildToolsSubmenu(StreamControlCenterModel m)
        {
            var items = new List<CcMenuItem>();
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Keyboard,
                Text = "远程任务管理器",
                Shortcut = "Ctrl+Shift+Esc",
                OnClick = delegate { if (m.OnSendRemoteKeys != null) m.OnSendRemoteKeys(new[] { 0x11, 0x10, 0x1B }); }
            });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Shield,
                Text = "发送 Ctrl+Alt+Del",
                OnClick = delegate { if (m.OnSendRemoteKeys != null) m.OnSendRemoteKeys(new[] { 0x11, 0x12, 0x2E }); }
            });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Window,
                Text = "显示远程桌面",
                Shortcut = "Win+D",
                OnClick = delegate { if (m.OnSendRemoteKeys != null) m.OnSendRemoteKeys(new[] { 0x5B, 0x44 }); }
            });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Lock,
                Text = "立即锁定被控端屏幕",
                Shortcut = "Win+L",
                OnClick = delegate { if (m.OnSendRemoteKeys != null) m.OnSendRemoteKeys(new[] { 0x11, 0x12, 0x10, 0x78 }); }
            });
            items.Add(new CcMenuItem { IsSeparator = true });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Clipboard,
                Text = "同步本机剪贴板文本到远端",
                OnClick = delegate { if (m.OnSendClipboardText != null) m.OnSendClipboardText(); }
            });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Screenshot,
                Text = "截屏保存当前画面",
                OnClick = delegate { if (m.OnCaptureScreenshot != null) m.OnCaptureScreenshot(); }
            });
            items.Add(new CcMenuItem { IsSeparator = true });
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Home,
                Text = "返回 BoBo远程 主界面",
                OnClick = delegate { if (m.OnRestoreMainWindow != null) m.OnRestoreMainWindow(); }
            });
            return items;
        }

        private static List<CcMenuItem> BuildMoreSubmenu(StreamControlCenterModel m)
        {
            var items = new List<CcMenuItem>();
            items.Add(new CcMenuItem
            {
                Icon = CcIcon.Activity,
                Text = "显示网络状态",
                Subtitle = m.ConnectionRoute,
                CheckSelected = m.ShowNetworkStats,
                OnClick = delegate { if (m.OnToggleNetworkStats != null) m.OnToggleNetworkStats(!m.ShowNetworkStats); }
            });
            items.Add(new CcMenuItem
            {
                Text = "全屏状态隐藏悬浮球",
                Shortcut = "Ctrl+Alt+G",
                CheckSelected = m.HideFloatBallInFullscreen,
                OnClick = delegate { if (m.OnToggleHideFloatBallInFullscreen != null) m.OnToggleHideFloatBallInFullscreen(!m.HideFloatBallInFullscreen); }
            });
            items.Add(new CcMenuItem
            {
                Text = "窗口模式也显示悬浮球",
                CheckSelected = m.ShowFloatBallInWindowed,
                OnClick = delegate { if (m.OnToggleShowFloatBallInWindowed != null) m.OnToggleShowFloatBallInWindowed(!m.ShowFloatBallInWindowed); }
            });
            return items;
        }

        internal static void ShowShortcutsHelpDialog()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "BoBo远程 · 远控快捷键说明";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.TopMost = true;
                dlg.BackColor = Theme.Paper;
                dlg.ClientSize = new Size(460, 360);
                dlg.Font = new Font("Microsoft YaHei UI", 9.5f);

                var layout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(22, 18, 22, 18),
                    ColumnCount = 2,
                    RowCount = 10
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));

                var title = new Label
                {
                    Text = "内置远控快捷键（锁鼠模式下同样始终本地生效）",
                    Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                    ForeColor = Theme.Ink,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 12)
                };
                layout.Controls.Add(title, 0, 0);
                layout.SetColumnSpan(title, 2);

                string[,] rows = new string[,]
                {
                    { "锁鼠模式临时释放鼠标并唤出浮窗", "轻按 Ctrl + Alt" },
                    { "切换自由指针 / 锁鼠游戏模式", "Ctrl + Alt + M" },
                    { "切换全屏 / 窗口模式", "Ctrl + Alt + F" },
                    { "切换小窗置顶模式", "Ctrl + Alt + U" },
                    { "全屏状态隐藏 / 显示悬浮球", "Ctrl + Alt + G" },
                    { "切换下一个远程屏幕", "Ctrl + Alt + →" },
                    { "强制释放鼠标并展开控制中心", "Ctrl + Alt + Shift + B" },
                    { "立即断开并退出远控", "Ctrl + Alt + Shift + Q" },
                    { "开启 / 退出本机防窥壁纸", "Ctrl + Alt + Shift + P" }
                };

                for (int i = 0; i < rows.GetLength(0); i++)
                {
                    var lblDesc = new Label
                    {
                        Text = rows[i, 0],
                        ForeColor = Theme.BodyInk,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Height = 28
                    };
                    var lblKey = new Label
                    {
                        Text = rows[i, 1],
                        Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                        ForeColor = Theme.Accent,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleRight,
                        Height = 28
                    };
                    layout.Controls.Add(lblDesc, 0, i + 1);
                    layout.Controls.Add(lblKey, 1, i + 1);
                }

                var btnClose = Theme.Action("知道了", Theme.ButtonStyle.Primary);
                btnClose.DialogResult = DialogResult.OK;
                btnClose.Dock = DockStyle.None;
                btnClose.Font = dlg.Font;
                btnClose.Size = new Size(104, 34);
                btnClose.Anchor = AnchorStyles.Right;
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 8, 22, 10) };
                btnClose.Location = new Point(dlg.ClientSize.Width - 126, 8);
                bottom.Controls.Add(btnClose);

                dlg.Controls.Add(layout);
                dlg.Controls.Add(bottom);
                dlg.AcceptButton = btnClose;
                dlg.ShowDialog();
            }
        }

        private sealed class OutsideClickFilter : IMessageFilter
        {
            private const int WmLButtonDown = 0x0201;
            private const int WmRButtonDown = 0x0204;
            private const int WmNcLButtonDown = 0x00A1;
            private const int WmKeyDown = 0x0100;
            private readonly UuControlCenterSession session;

            internal OutsideClickFilter(UuControlCenterSession session)
            {
                this.session = session;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (!session.IsOpen) return false;
                if (m.Msg == WmKeyDown && m.WParam.ToInt32() == (int)Keys.Escape)
                {
                    session.CloseAll();
                    return true;
                }
                if (m.Msg == WmLButtonDown || m.Msg == WmRButtonDown || m.Msg == WmNcLButtonDown)
                {
                    if (!session.ContainsScreenPoint(Cursor.Position))
                    {
                        session.CloseAll();
                    }
                }
                return false;
            }
        }
    }

    /// <summary>
    /// 单个 UU 风格浅色圆角浮层菜单窗口（支持普通列表模式与「声音 → 音量滑块」模式）。
    /// </summary>
    internal sealed class UuMenuPopupForm : Form
    {
        private const int WsExTopMost = 0x00000008;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int CsDropShadow = 0x00020000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);
        private const TextFormatFlags SingleLineText = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        private const TextFormatFlags MenuText = SingleLineText | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        private const TextFormatFlags SubtitleText = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private readonly UuControlCenterSession session;
        internal readonly int Level;
        private readonly List<CcMenuItem> items;
        private readonly bool volumeSliderMode;
        private readonly StreamControlCenterModel model;
        private readonly float dpiScale;

        private Rectangle[] itemRects;
        private int[] shortcutWidths;
        private int hoveredIndex = -1;
        private int openSubmenuIndex = -1;

        private Rectangle speakerRect;
        private Rectangle sliderTrackRect;
        private readonly UiTransition volumePosition;
        private readonly UiTransition sliderEmphasis;
        private bool draggingSlider;
        private int sliderDragOffset;
        private int lastNonZeroVolume = 80;

        internal UuMenuPopupForm(UuControlCenterSession session, int level, List<CcMenuItem> items, bool volumeSliderMode, StreamControlCenterModel model)
        {
            this.session = session;
            this.Level = level;
            this.items = items ?? new List<CcMenuItem>();
            this.volumeSliderMode = volumeSliderMode;
            this.model = model ?? new StreamControlCenterModel();
            if (volumeSliderMode)
            {
                if (this.model.VolumePercent > 0) lastNonZeroVolume = this.model.VolumePercent;
                volumePosition = new UiTransition(this, EffectiveVolume / 100f);
                sliderEmphasis = new UiTransition(this);
            }

            dpiScale = ResolveScale();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Paper;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            MeasureAndLayout();
        }

        private static float ResolveScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return Math.Max(1f, Math.Min(2.5f, g.DpiX / 96f));
                }
            }
            catch { return 1f; }
        }

        private int S(float px) { return Math.Max(1, (int)Math.Round(px * dpiScale)); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExTopMost | WsExToolWindow | WsExNoActivate;
                cp.ClassStyle |= CsDropShadow;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch { }
            ApplyRoundedRegion();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseActivate)
            {
                m.Result = new IntPtr(MaNoActivate);
                return;
            }
            base.WndProc(ref m);
        }

        internal void EnsureTopMost()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate); }
            catch { }
        }

        private void ApplyRoundedRegion()
        {
            if (Width <= 4 || Height <= 4) return;
            try
            {
                using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), S(10)))
                {
                    Region old = Region;
                    Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        private void MeasureAndLayout()
        {
            if (volumeSliderMode)
            {
                Size = new Size(S(322), S(54));
                speakerRect = new Rectangle(S(14), S(11), S(32), S(32));
                sliderTrackRect = new Rectangle(S(96), S(24), S(164), S(6));
                return;
            }

            int padY = S(8);
            int padX = S(6);
            int minW = Level == 0 ? S(286) : S(246);
            int maxW = S(340);
            int textLeft = S(40);
            int subtitleRight = S(14);
            Font mainFont = Theme.GetFont(Theme.TextSize.Label);
            Font smallFont = Theme.GetFont(Theme.TextSize.Small);
            Font headerFont = Theme.GetFont(Theme.TextSize.LabelBold);
            var unconstrained = new Size(int.MaxValue, int.MaxValue);
            itemRects = new Rectangle[items.Count];
            shortcutWidths = new int[items.Count];

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                int measuredMaxW = minW;
                for (int i = 0; i < items.Count; i++)
                {
                    CcMenuItem item = items[i];
                    if (item.IsSeparator) continue;
                    Font font = item.IsHeader ? headerFont : mainFont;
                    int titleWidth = TextRenderer.MeasureText(g, item.Text, font, unconstrained, SingleLineText).Width;
                    if (!string.IsNullOrEmpty(item.Shortcut))
                    {
                        int width = TextRenderer.MeasureText(g, item.Shortcut, smallFont, unconstrained, SingleLineText).Width;
                        shortcutWidths[i] = Math.Min(S(112), width + S(2));
                    }
                    int left = item.IsHeader ? S(12) : textLeft;
                    int candidate = padX * 2 + left + titleWidth + TextRightReserve(item, i) + S(2);
                    if (!string.IsNullOrEmpty(item.Subtitle)) candidate = Math.Max(candidate, S(292));
                    measuredMaxW = Math.Max(measuredMaxW, candidate);
                }

                int finalW = Math.Min(maxW, measuredMaxW);
                int itemWidth = finalW - padX * 2;
                int y = padY;
                for (int i = 0; i < items.Count; i++)
                {
                    CcMenuItem item = items[i];
                    if (item.IsSeparator)
                    {
                        itemRects[i] = new Rectangle(S(12), y, finalW - S(24), S(11));
                        y += S(11);
                        continue;
                    }

                    int height = item.IsHeader ? S(26) : S(38);
                    if (!item.IsHeader && !string.IsNullOrEmpty(item.Subtitle))
                    {
                        var available = new Size(itemWidth - textLeft - subtitleRight, int.MaxValue);
                        int subtitleHeight = TextRenderer.MeasureText(g, item.Subtitle, smallFont, available, SubtitleText).Height;
                        height = S(34) + subtitleHeight + S(8);
                    }
                    itemRects[i] = new Rectangle(padX, y, itemWidth, height);
                    y += height + (item.IsHeader ? 0 : S(1));
                }
                Size = new Size(finalW, y + padY);
            }
        }

        private int TextRightReserve(CcMenuItem item, int index)
        {
            if (shortcutWidths[index] > 0) return S(12) + shortcutWidths[index] + S(10);
            return item.SubmenuBuilder != null || item.IsVolumeSliderSubmenu ? S(26) : S(12);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (volumeSliderMode)
            {
                if (draggingSlider) UpdateVolumeFromX(e.X - sliderDragOffset, false);
                UpdateSliderHover(e.Location);
                return;
            }

            int hit = HitTest(e.Location);
            if (hit != hoveredIndex)
            {
                hoveredIndex = hit;
                Invalidate();

                if (hit >= 0 && hit < items.Count)
                {
                    CcMenuItem it = items[hit];
                    if (it.IsVolumeSliderSubmenu || it.SubmenuBuilder != null)
                    {
                        if (openSubmenuIndex != hit)
                        {
                            openSubmenuIndex = hit;
                            Rectangle screenRow = RectangleToScreen(itemRects[hit]);
                            session.OpenSubmenu(Level, screenRow, it, Owner);
                        }
                    }
                    else if (!it.IsSeparator && !it.IsHeader)
                    {
                        openSubmenuIndex = -1;
                        session.CloseFromLevel(Level + 1);
                    }
                }
            }
            Cursor = (hit >= 0 && hit < items.Count && !items[hit].IsSeparator && !items[hit].IsHeader && items[hit].Enabled)
                ? Cursors.Hand
                : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (volumeSliderMode)
                UpdateSliderHover(new Point(-1, -1));
            else if (openSubmenuIndex < 0)
            {
                hoveredIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (volumeSliderMode)
            {
                if (speakerRect.Contains(e.Location))
                {
                    SetVolume(EffectiveVolume == 0 ? Math.Max(15, lastNonZeroVolume) : 0, true);
                }
                else if (IsOverSlider(e.Location))
                {
                    bool thumbHit = Math.Abs(e.X - SliderThumbX(volumePosition.Value)) <= S(9);
                    if (thumbHit) volumePosition.MoveTo(volumePosition.Value, 0);
                    sliderDragOffset = thumbHit ? e.X - SliderThumbX(volumePosition.Value) : 0;
                    draggingSlider = true;
                    Capture = true;
                    UpdateSliderHover(e.Location);
                    if (!thumbHit) UpdateVolumeFromX(e.X, true);
                }
                return;
            }

            int hit = HitTest(e.Location);
            if (hit < 0 || hit >= items.Count) return;
            CcMenuItem it = items[hit];
            if (it.IsSeparator || it.IsHeader || !it.Enabled) return;

            if (it.IsVolumeSliderSubmenu || it.SubmenuBuilder != null)
            {
                openSubmenuIndex = hit;
                session.OpenSubmenu(Level, RectangleToScreen(itemRects[hit]), it, Owner);
                return;
            }

            Action act = it.OnClick;
            if (it.CloseOnClick) session.CloseAll();
            if (act != null)
            {
                try { act(); } catch { }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!draggingSlider || e.Button != MouseButtons.Left) return;
            draggingSlider = false;
            Capture = false;
            UpdateSliderHover(e.Location);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!draggingSlider || Capture) return;
            draggingSlider = false;
            UpdateSliderHover(IsHandleCreated ? PointToClient(Cursor.Position) : new Point(-1, -1));
        }

        private bool IsOverSlider(Point p)
        {
            return Rectangle.Inflate(sliderTrackRect, S(12), S(13)).Contains(p);
        }

        private void UpdateSliderHover(Point point)
        {
            bool sliderHot = draggingSlider || IsOverSlider(point);
            Cursor = sliderHot || speakerRect.Contains(point) ? Cursors.Hand : Cursors.Default;
            sliderEmphasis.MoveTo(sliderHot ? 1f : 0f, 115);
        }

        private int EffectiveVolume
        {
            get { return model.AudioMuted ? 0 : Math.Max(0, Math.Min(100, model.VolumePercent)); }
        }

        private int SliderThumbX(float position)
        {
            return sliderTrackRect.Left + (int)Math.Round(sliderTrackRect.Width * position);
        }

        private void UpdateVolumeFromX(int x, bool animate)
        {
            float ratio = (float)(x - sliderTrackRect.Left) / sliderTrackRect.Width;
            SetVolume((int)Math.Round(ratio * 100f), animate);
        }

        private void SetVolume(int percent, bool animate)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            bool muted = percent == 0;
            volumePosition.MoveTo(percent / 100f, animate ? 135 : 0);
            if (model.VolumePercent == percent && model.AudioMuted == muted) return;
            model.VolumePercent = percent;
            model.AudioMuted = muted;
            if (percent > 0) lastNonZeroVolume = percent;
            if (model.OnVolumeChanged != null) model.OnVolumeChanged(percent, muted);
            Invalidate();
        }

        private int HitTest(Point pt)
        {
            for (int i = 0; i < itemRects.Length; i++)
            {
                if (itemRects[i].Contains(pt)) return i;
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            g.Clear(Theme.Paper);
            Theme.Surface(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f),
                S(10), Theme.Paper, Theme.StrokeStrong);

            if (volumeSliderMode)
            {
                PaintVolumeSlider(g);
                return;
            }

            Font mainFont = Theme.GetFont(Theme.TextSize.Label);
            Font smallFont = Theme.GetFont(Theme.TextSize.Small);
            Font headerFont = Theme.GetFont(Theme.TextSize.LabelBold);
            for (int i = 0; i < items.Count; i++)
            {
                CcMenuItem item = items[i];
                Rectangle row = itemRects[i];
                if (item.IsSeparator)
                {
                    int midY = row.Top + row.Height / 2;
                    using (var pen = new Pen(Theme.Divider, 1f))
                        g.DrawLine(pen, row.Left, midY, row.Right, midY);
                    continue;
                }
                if (item.IsHeader)
                {
                    var header = new Rectangle(row.Left + S(12), row.Top, row.Width - S(24), row.Height);
                    TextRenderer.DrawText(g, item.Text, headerFont, header, Theme.Muted, MenuText);
                    continue;
                }

                bool submenuOpen = i == openSubmenuIndex;
                if (item.Enabled && (i == hoveredIndex || submenuOpen))
                {
                    Color highlight = item.IsDanger ? Theme.AlertLight : submenuOpen ? Theme.AccentLight : Theme.SurfaceSubtle;
                    Theme.Surface(g, row, S(6), highlight, Color.Transparent);
                }

                Color foreground = !item.Enabled ? Theme.Muted : item.IsDanger ? Theme.Alert : Theme.Ink;
                bool hasSubtitle = !string.IsNullOrEmpty(item.Subtitle);
                int titleHeight = hasSubtitle ? S(34) : row.Height;
                int iconCenterX = row.Left + S(21);
                int iconCenterY = row.Top + titleHeight / 2;
                int iconSize = S(16);
                var iconRect = new RectangleF(iconCenterX - iconSize / 2f, iconCenterY - iconSize / 2f, iconSize, iconSize);

                if (item.CheckSelected)
                {
                    Theme.DrawGlyph(g, Theme.Glyph.Check, iconRect, item.Enabled ? Theme.Accent : Theme.Muted);
                }
                else if (item.RadioSelected)
                {
                    int diameter = S(6);
                    using (var dot = new SolidBrush(item.Enabled ? Theme.Accent : Theme.Muted))
                        g.FillEllipse(dot, iconCenterX - diameter / 2, iconCenterY - diameter / 2, diameter, diameter);
                }
                else if (item.Icon != CcIcon.None)
                {
                    Color iconInk = item.IsDanger || !item.Enabled ? foreground : submenuOpen ? Theme.Accent : Theme.BodyInk;
                    DrawMenuIcon(g, item.Icon, iconRect, iconInk);
                }

                int textLeft = row.Left + S(40);
                int textWidth = row.Right - TextRightReserve(item, i) - textLeft;
                var title = new Rectangle(textLeft, row.Top, textWidth, titleHeight);
                TextRenderer.DrawText(g, item.Text, mainFont, title, foreground, MenuText);
                if (hasSubtitle)
                {
                    var subtitle = new Rectangle(textLeft, title.Bottom, row.Right - S(14) - textLeft, row.Bottom - title.Bottom);
                    TextRenderer.DrawText(g, item.Subtitle, smallFont, subtitle, Theme.Muted, SubtitleText);
                }

                if (shortcutWidths[i] > 0)
                {
                    int width = shortcutWidths[i];
                    var shortcut = new Rectangle(row.Right - S(12) - width, row.Top, width, titleHeight);
                    TextRenderer.DrawText(g, item.Shortcut, smallFont, shortcut, Theme.Muted, MenuText | TextFormatFlags.Right);
                }
                else if (item.SubmenuBuilder != null || item.IsVolumeSliderSubmenu)
                {
                    int chevronSize = S(13);
                    var chevron = new RectangleF(row.Right - S(14) - chevronSize / 2f,
                        iconCenterY - chevronSize / 2f, chevronSize, chevronSize);
                    Theme.DrawGlyph(g, Theme.Glyph.ChevronRight, chevron, submenuOpen ? Theme.Accent : Theme.Muted);
                }
            }
        }

        private void PaintVolumeSlider(Graphics g)
        {
            int volume = EffectiveVolume;
            bool muted = volume == 0;
            Theme.Surface(g, speakerRect, S(6), muted ? Theme.AccentLight : Theme.SurfaceSubtle, Color.Transparent);
            int speakerSize = S(18);
            var speakerGlyph = new RectangleF(speakerRect.X + (speakerRect.Width - speakerSize) / 2f,
                speakerRect.Y + (speakerRect.Height - speakerSize) / 2f, speakerSize, speakerSize);
            Theme.DrawGlyph(g, muted ? Theme.Glyph.VolumeOff : Theme.Glyph.Volume, speakerGlyph,
                muted ? Theme.Accent : Theme.BodyInk);

            TextRenderer.DrawText(g, "音量", Theme.GetFont(Theme.TextSize.Label),
                new Rectangle(S(50), 0, S(44), Height), Theme.Ink, MenuText);
            int readoutLeft = sliderTrackRect.Right + S(14);
            var readout = new Rectangle(readoutLeft, 0, Width - readoutLeft - S(12), Height);
            TextRenderer.DrawText(g, volume + "%", Theme.GetFont(Theme.TextSize.Code), readout,
                Theme.BodyInk, MenuText | TextFormatFlags.Right);

            Theme.Surface(g, sliderTrackRect, sliderTrackRect.Height / 2f, Theme.StrokeStrong, Color.Transparent);
            int thumbX = SliderThumbX(volumePosition.Value);
            int fillWidth = thumbX - sliderTrackRect.Left;
            if (fillWidth > 0)
            {
                var fill = new Rectangle(sliderTrackRect.Left, sliderTrackRect.Top, fillWidth, sliderTrackRect.Height);
                Theme.Surface(g, fill, fill.Height / 2f, Theme.Accent, Color.Transparent);
            }

            float thumbY = sliderTrackRect.Top + sliderTrackRect.Height / 2f;
            float emphasis = sliderEmphasis.Value;
            float radius = (7.5f + emphasis * 0.75f) * dpiScale;
            if (emphasis > 0f)
            {
                float halo = radius + 2.5f * dpiScale;
                using (var brush = new SolidBrush(Color.FromArgb((int)Math.Round(42f * emphasis), Theme.Accent)))
                    g.FillEllipse(brush, thumbX - halo, thumbY - halo, halo * 2f, halo * 2f);
            }
            using (var fill = new SolidBrush(Theme.Paper))
            using (var ring = new Pen(draggingSlider ? Theme.AccentPressed : Theme.Accent, 1.7f * dpiScale))
            {
                var thumb = new RectangleF(thumbX - radius, thumbY - radius, radius * 2f, radius * 2f);
                g.FillEllipse(fill, thumb);
                g.DrawEllipse(ring, thumb);
            }
        }

        private static Theme.Glyph GetThemeGlyph(CcIcon icon)
        {
            switch (icon)
            {
                case CcIcon.Monitor: return Theme.Glyph.Desktop;
                case CcIcon.SwitchMonitor: return Theme.Glyph.SwitchDisplay;
                case CcIcon.Quality: return Theme.Glyph.Image;
                case CcIcon.Window: return Theme.Glyph.Window;
                case CcIcon.Sound: return Theme.Glyph.Volume;
                case CcIcon.SoundMuted: return Theme.Glyph.VolumeOff;
                case CcIcon.Shield: return Theme.Glyph.Shield;
                case CcIcon.Peripheral: return Theme.Glyph.Keypad;
                case CcIcon.Tool: return Theme.Glyph.Gear;
                case CcIcon.More: return Theme.Glyph.MoreDots;
                case CcIcon.Fullscreen: return Theme.Glyph.Expand;
                case CcIcon.Exit: return Theme.Glyph.Exit;
                case CcIcon.Opacity: return Theme.Glyph.Sliders;
                case CcIcon.PrivacyEye: return Theme.Glyph.EyeOff;
                case CcIcon.Keyboard: return Theme.Glyph.Keyboard;
                case CcIcon.Mouse: return Theme.Glyph.Mouse;
                case CcIcon.Screenshot: return Theme.Glyph.Camera;
                case CcIcon.Clipboard: return Theme.Glyph.Clipboard;
                case CcIcon.Lock: return Theme.Glyph.Lock;
                case CcIcon.Mic: return Theme.Glyph.Mic;
                case CcIcon.Activity: return Theme.Glyph.Activity;
                default: return Theme.Glyph.None;
            }
        }

        private static void DrawMenuIcon(Graphics g, CcIcon icon, RectangleF area, Color color)
        {
            Theme.Glyph sharedGlyph = GetThemeGlyph(icon);
            if (sharedGlyph != Theme.Glyph.None)
            {
                Theme.DrawGlyph(g, sharedGlyph, area, color);
                return;
            }

            float s = area.Width;
            float x = area.X;
            float y = area.Y;
            float cx = x + s / 2f;
            float cy = y + s / 2f;
            float stroke = Math.Max(1.3f, s / 11.5f);
            using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (icon)
                {
                    case CcIcon.ExitFullscreen:
                        g.DrawLines(pen, new[] { new PointF(x + s * 0.14f, y + s * 0.36f), new PointF(x + s * 0.36f, y + s * 0.36f), new PointF(x + s * 0.36f, y + s * 0.14f) });
                        g.DrawLines(pen, new[] { new PointF(x + s * 0.86f, y + s * 0.36f), new PointF(x + s * 0.64f, y + s * 0.36f), new PointF(x + s * 0.64f, y + s * 0.14f) });
                        g.DrawLines(pen, new[] { new PointF(x + s * 0.14f, y + s * 0.64f), new PointF(x + s * 0.36f, y + s * 0.64f), new PointF(x + s * 0.36f, y + s * 0.86f) });
                        g.DrawLines(pen, new[] { new PointF(x + s * 0.86f, y + s * 0.64f), new PointF(x + s * 0.64f, y + s * 0.64f), new PointF(x + s * 0.64f, y + s * 0.86f) });
                        break;
                    case CcIcon.SuperScreen:
                        g.DrawRectangle(pen, x + s * 0.10f, y + s * 0.16f, s * 0.80f, s * 0.64f);
                        g.DrawLine(pen, cx - s * 0.16f, cy, cx + s * 0.16f, cy);
                        g.DrawLine(pen, cx, cy - s * 0.16f, cx, cy + s * 0.16f);
                        break;
                    case CcIcon.MiniWindow:
                        g.DrawRectangle(pen, x + s * 0.10f, y + s * 0.18f, s * 0.80f, s * 0.64f);
                        using (var fill = new SolidBrush(color))
                            g.FillRectangle(fill, x + s * 0.48f, y + s * 0.46f, s * 0.34f, s * 0.26f);
                        break;
                    case CcIcon.Home:
                        g.DrawLines(pen, new[]
                        {
                            new PointF(x + s * 0.12f, y + s * 0.50f),
                            new PointF(cx, y + s * 0.14f),
                            new PointF(x + s * 0.88f, y + s * 0.50f)
                        });
                        g.DrawRectangle(pen, x + s * 0.24f, y + s * 0.48f, s * 0.52f, s * 0.38f);
                        break;
                }
            }
        }
    }
}
