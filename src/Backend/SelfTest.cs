using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DualRemote
{
    /// <summary>
    /// 无 UI 的参数与解析自检：验证编译后的实际二进制里，参数拼装、输入校验、
    /// 最近目标持久化、Tailscale 输出解析都符合预期；组件缺失只作为信息输出，不算失败。
    /// </summary>
    internal static class SelfTest
    {
        private static int _failures;
        private static StringBuilder _report;

        internal static int Run()
        {
            _report = new StringBuilder();
            _failures = 0;

            Log("双端远程 self-test");
            Log("程序目录：" + Engines.AppDir);
            Log("OS：" + Environment.OSVersion.VersionString + " / CLR " + Environment.Version);
            Log("");

            string error;
            string id = Engines.NormalizeRustDeskId("123 456 789", out error);
            Check("RustDesk 设备码：允许界面空格并归一化", id == "123456789", "结果=" + (id == null ? "null" : id));
            Check("RustDesk 设备码：兼容自定义服务器 ID", Engines.NormalizeRustDeskId("my-pc_01", out error) == "my-pc_01", error);
            Check("RustDesk 设备码：拒绝非法字符", Engines.NormalizeRustDeskId("abc/def", out error) == null, error);
            Check("RustDesk 设备码：拒绝选项形态", Engines.NormalizeRustDeskId("--help", out error) == null, error);
            Check("RustDesk 设备码：拒绝过短输入", Engines.NormalizeRustDeskId("12", out error) == null, error);
            Check("RustDesk 设备码：拒绝空输入", Engines.NormalizeRustDeskId("", out error) == null, error);

            Check("主机：接受局域网 IPv4", Engines.NormalizeHost("192.168.1.20", out error) == "192.168.1.20", error);
            Check("主机：接受 Tailscale IPv4", Engines.NormalizeHost("100.64.0.12", out error) == "100.64.0.12", error);
            Check("主机：接受 MagicDNS 名称", Engines.NormalizeHost("glux-laptop.tailnet.ts.net", out error) != null, error);
            Check("主机：接受 IPv6", Engines.NormalizeHost("fd7a:115c:a1e0::1", out error) == "fd7a:115c:a1e0::1", error);
            Check("主机：接受方括号 IPv6 并去掉括号", Engines.NormalizeHost("[fd7a:115c:a1e0::1]", out error) == "fd7a:115c:a1e0::1", error);
            Check("主机：拒绝空格", Engines.NormalizeHost("bad host", out error) == null, error);
            Check("主机：拒绝带协议的 URL", Engines.NormalizeHost("https://example.com", out error) == null, error);
            Check("主机：拒绝选项形态", Engines.NormalizeHost("--help", out error) == null, error);
            Check("主机：拒绝端口写法", Engines.NormalizeHost("100.64.0.12:8080", out error) == null, error);
            Check("主机：拒绝非法 IPv4", Engines.NormalizeHost("999.1.1.1", out error) == null, error);
            Check("主机：拒绝空输入", Engines.NormalizeHost("  ", out error) == null, error);

            RecentStore store = new RecentStore();
            for (int i = 0; i < 7; i++)
            {
                store.RememberRustDeskId("12345678" + i);
            }
            store.RememberMoonlightHost("100.64.0.12");
            store.RememberMoonlightHost("192.168.1.20");
            store.RememberMoonlightHost("100.64.0.12");
            Check("最近目标：只保留 5 条", store.RustDeskIds.Count == 5, "count=" + store.RustDeskIds.Count);
            Check("最近目标：最新在前", store.RustDeskIds.Count > 0 && store.RustDeskIds[0] == "123456786",
                store.RustDeskIds.Count > 0 ? store.RustDeskIds[0] : "-");
            Check("最近主机：去重且最新在前",
                store.MoonlightHosts.Count == 2 && store.MoonlightHosts[0] == "100.64.0.12",
                store.MoonlightHosts.Count > 0 ? store.MoonlightHosts[0] : "-");
            RecentStore roundTrip = new RecentStore();
            RecentStore.Parse(RecentStore.Serialize(store), roundTrip);
            Check("最近目标：序列化后可完整恢复",
                roundTrip.RustDeskIds.Count == 5
                && roundTrip.RustDeskIds[0] == "123456786"
                && roundTrip.MoonlightHosts.Count == 2,
                "ids=" + roundTrip.RustDeskIds.Count + ", hosts=" + roundTrip.MoonlightHosts.Count);

            string pingSample =
                "pong from desk (100.1.2.3) via DERP(tok) in 42ms\n"
                + "pong from desk (100.1.2.3) via 192.168.1.9:41641 in 3ms\n";
            List<PingPong> pongs = Tailscale.ParsePongs(pingSample);
            Check("ping 解析：两行回包", pongs.Count == 2, "count=" + pongs.Count);
            Check("ping 解析：DERP 不是直连",
                pongs.Count > 0 && !pongs[0].Direct && pongs[0].Route == "DERP(tok)",
                pongs.Count > 0 ? pongs[0].Route : "-");
            Check("ping 解析：直连端点与 RTT",
                pongs.Count > 1 && pongs[1].Direct && pongs[1].Latency == "3ms",
                pongs.Count > 1 ? pongs[1].Route + " / " + pongs[1].Latency : "-");
            List<PingPong> peerRelayPongs = Tailscale.ParsePongs("pong from desk (public-ip-x) via peer-relay(glux-pc) in 7ms");
            Check("ping 解析：peer 中继不是直连",
                peerRelayPongs.Count == 1 && !peerRelayPongs[0].Direct
                && peerRelayPongs[0].Route == "peer-relay(glux-pc)",
                peerRelayPongs.Count > 0 ? peerRelayPongs[0].Route : "-");

            string directText = Tailscale.DescribeProbeOutput(
                "pong from desk (public-ip-x) via 192.168.1.9:41641 in 3ms", "", 0, false, 8);
            Check("探测文本：直连要写出直连与 RTT，且不回显端点",
                directText.IndexOf("直连", StringComparison.Ordinal) >= 0
                && directText.IndexOf("RTT 3ms", StringComparison.Ordinal) >= 0
                && directText.IndexOf("192.168", StringComparison.Ordinal) < 0
                && directText.IndexOf("41641", StringComparison.Ordinal) < 0, directText);
            string noReplyText = Tailscale.DescribeProbeOutput("", "ping \"100.64.0.12\" timed out\nno reply", 1, false, 8);
            Check("探测文本：无回包写未判定直连且不回显地址",
                noReplyText.IndexOf("无回包", StringComparison.Ordinal) >= 0
                && noReplyText.IndexOf("未判定直连", StringComparison.Ordinal) >= 0
                && noReplyText.IndexOf("100.64.0.12", StringComparison.Ordinal) < 0, noReplyText);
            string timeoutText = Tailscale.DescribeProbeOutput("", "", -1, true, 8);
            Check("探测文本：整体超时写明未判定",
                timeoutText.IndexOf("超时", StringComparison.Ordinal) >= 0
                && timeoutText.IndexOf("未判定直连", StringComparison.Ordinal) >= 0, timeoutText);
            string busyText = Tailscale.DescribeProbeOutput("", "status: 401 already in use by SYSTEM", 1, false, 8);
            Check("探测文本：被占用时不当作无设备/直连",
                busyText.IndexOf("占用", StringComparison.Ordinal) >= 0
                && busyText.IndexOf("未判定直连", StringComparison.Ordinal) >= 0, busyText);

            // 真实 stderr 样本：受限上下文里 tailscaled 的受保护命名管道拒绝当前进程。
            // 期望只出现可操作的中文，不出现内部管道名或英文原文；手动输入地址始终保留。
            string deniedSample =
                "failed to connect to local tailscaled (no tailscaled process found): "
                + "Get \"http://local-tailscaled.sock/localapi/v0/status\": "
                + "open \\\\.\\pipe\\ProtectedPrefix\\Administrators\\Tailscale\\tailscaled: Access is denied.";
            string deniedStatus = Tailscale.DescribeStatusFailure(1, deniedSample);
            Check("状态读取：权限拒绝给可操作中文且不泄内部路径",
                deniedStatus.IndexOf("当前 Windows 用户无法读取 Tailscale", StringComparison.Ordinal) >= 0
                && deniedStatus.IndexOf("手动连接", StringComparison.Ordinal) >= 0
                && deniedStatus.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) < 0
                && deniedStatus.IndexOf("tailscaled", StringComparison.OrdinalIgnoreCase) < 0
                && deniedStatus.IndexOf("Access", StringComparison.OrdinalIgnoreCase) < 0
                && deniedStatus.IndexOf("\\", StringComparison.Ordinal) < 0
                && deniedStatus.IndexOf("占用", StringComparison.Ordinal) < 0
                && deniedStatus.IndexOf("退出码", StringComparison.Ordinal) < 0, deniedStatus);
            string deniedProbe = Tailscale.DescribeProbeOutput("", deniedSample, 1, false, 8);
            Check("探测文本：权限拒绝与列表一致且不当作无回包/直连",
                deniedProbe.IndexOf("当前 Windows 用户无法读取 Tailscale", StringComparison.Ordinal) >= 0
                && deniedProbe.IndexOf("手动连接", StringComparison.Ordinal) >= 0
                && deniedProbe.IndexOf("未判定直连", StringComparison.Ordinal) >= 0
                && deniedProbe.IndexOf("无回包", StringComparison.Ordinal) < 0
                && deniedProbe.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) < 0
                && deniedProbe.IndexOf("tailscaled", StringComparison.OrdinalIgnoreCase) < 0, deniedProbe);
            string lowercaseDenied = Tailscale.DescribeStatusFailure(
                1, "open \\\\.\\pipe\\ProtectedPrefix\\Administrators\\Tailscale\\tailscaled: access denied");
            string chineseDenied = Tailscale.DescribeStatusFailure(1, "tailscale: 拒绝访问。");
            string permissionDenied = Tailscale.DescribeStatusFailure(1, "dial: permission denied");
            Check("状态读取：access denied / 拒绝访问 / permission denied 同一归类",
                lowercaseDenied.IndexOf("无法读取 Tailscale", StringComparison.Ordinal) >= 0
                && chineseDenied.IndexOf("无法读取 Tailscale", StringComparison.Ordinal) >= 0
                && permissionDenied.IndexOf("无法读取 Tailscale", StringComparison.Ordinal) >= 0,
                lowercaseDenied + " | " + chineseDenied + " | " + permissionDenied);
            string busyStatus = Tailscale.DescribeStatusFailure(1, "status: 401 already in use by SYSTEM");
            string ordinary401Status = Tailscale.DescribeStatusFailure(1, "status: 401");
            Check("状态读取：明确占用字样才归占用，普通 401 归权限",
                busyStatus.IndexOf("占用", StringComparison.Ordinal) >= 0
                && busyStatus.IndexOf("无法读取", StringComparison.Ordinal) < 0
                && ordinary401Status.IndexOf("无法读取 Tailscale", StringComparison.Ordinal) >= 0
                && ordinary401Status.IndexOf("401", StringComparison.Ordinal) < 0,
                busyStatus + " | " + ordinary401Status);
            Check("探测文本：401 系统占用仍单列",
                busyText.IndexOf("占用", StringComparison.Ordinal) >= 0
                && busyText.IndexOf("无法读取", StringComparison.Ordinal) < 0, busyText);
            string otherStatus = Tailscale.DescribeStatusFailure(7, "unexpected output");
            Check("状态读取：其他错误保持原有退出码与首行（不改变正常返回）",
                otherStatus.IndexOf("退出码 7", StringComparison.Ordinal) >= 0
                && otherStatus.IndexOf("unexpected output", StringComparison.Ordinal) >= 0, otherStatus);
            Check("状态读取：空 stderr 仍只报退出码",
                Tailscale.DescribeStatusFailure(3, "").IndexOf("退出码 3", StringComparison.Ordinal) >= 0,
                Tailscale.DescribeStatusFailure(3, ""));

            LinkProbeResult rejectedProbe = Tailscale.Probe("--help", 1000);
            Check("探测入口：选项形态参数被拒绝",
                rejectedProbe.Status == LinkProbeStatus.Failed
                && rejectedProbe.Text.IndexOf("参数无效", StringComparison.Ordinal) >= 0,
                rejectedProbe.Status + " " + rejectedProbe.Text);
            Check("探测分类：Tailscale IPv4 仅 100.64.0.0/10",
                Tailscale.IsTailnetAddress("100.64.0.12")
                && Tailscale.IsTailnetAddress("100.127.255.255")
                && !Tailscale.IsTailnetAddress("100.63.0.1")
                && !Tailscale.IsTailnetAddress("100.128.0.1"),
                "100.64/10 boundary");
            Check("探测分类：局域网、公网 IPv6、普通主机名走普通探测",
                !Tailscale.IsTailnetAddress("192.168.1.20")
                && !Tailscale.IsTailnetAddress("2408:8207:1234::1")
                && !Tailscale.IsTailnetAddress("desk.local")
                && !Tailscale.IsTailnetAddress("localhost"),
                "lan/public/hostname");
            Check("探测分类：Tailscale IPv6 与 .ts.net 归 tailnet",
                Tailscale.IsTailnetAddress("fd7a:115c:a1e0::1")
                && Tailscale.IsTailnetAddress("desk.tail1234.ts.net")
                && !Tailscale.IsTailnetAddress("fd00::1"),
                "tailnet v6/dns");
            LinkProbeResult noMatch = Tailscale.DescribeProbeResult("", "no matching peer", 1, false, 8);
            Check("探测状态：无匹配节点不返回可达/成功",
                noMatch.Status != LinkProbeStatus.Reachable
                && noMatch.Text.IndexOf("成功", StringComparison.Ordinal) < 0,
                noMatch.Status + " " + noMatch.Text);
            LinkProbeResult directProbe = Tailscale.DescribeProbeResult(
                "pong from desk (public-ip-x) via 192.168.1.9:41641 in 3ms", "", 0, false, 8);
            LinkProbeResult relayProbe = Tailscale.DescribeProbeResult(
                "pong from desk (public-ip-x) via DERP(tok) in 42ms", "", 0, false, 8);
            LinkProbeResult timeoutProbe = Tailscale.DescribeProbeResult("", "", -1, true, 8);
            Check("探测状态：直连/中继/未知显式区分",
                directProbe.Status == LinkProbeStatus.Reachable
                && relayProbe.Status == LinkProbeStatus.Relay
                && timeoutProbe.Status == LinkProbeStatus.Unknown,
                directProbe.Status + "/" + relayProbe.Status + "/" + timeoutProbe.Status);
            LinkProbeResult loopbackProbe = Tailscale.Probe("127.0.0.1", 1000);
            System.Diagnostics.Stopwatch nameClock = System.Diagnostics.Stopwatch.StartNew();
            LinkProbeResult localhostProbe = Tailscale.Probe("localhost", 1500);
            nameClock.Stop();
            if (!IcmpUsable())
            {
                // 受限环境（沙箱、部分安全软件）会禁止 ICMP。此时只能验证分类与不冒充成功，
                // 无法验证真实回包，按跳过处理，避免把环境限制误判为产品缺陷。
                Skip("普通地址探测：本机 ICMP 不可用，跳过回包验证（分类已单独通过）");
                Skip("主机名解析：本机 ICMP 不可用，跳过回包验证（预算与解析已单独覆盖）");
            }
            else
            {
                Check("普通地址探测：127.0.0.1 不经 Tailscale，失败也不冒充成功",
                    loopbackProbe.Text.IndexOf("普通地址", StringComparison.Ordinal) >= 0
                    && loopbackProbe.Text.IndexOf("Tailscale", StringComparison.Ordinal) < 0
                    && loopbackProbe.Status != LinkProbeStatus.Failed,
                    loopbackProbe.Status + " " + loopbackProbe.Text);
                Check("主机名解析：localhost 在预算内解析并按普通地址探测",
                    nameClock.ElapsedMilliseconds < 4000
                    && localhostProbe.Text.IndexOf("普通地址", StringComparison.Ordinal) >= 0
                    && localhostProbe.Text.IndexOf("Tailscale", StringComparison.Ordinal) < 0
                    && localhostProbe.Status != LinkProbeStatus.Failed,
                    localhostProbe.Status + " " + nameClock.ElapsedMilliseconds + "ms " + localhostProbe.Text);
            }
            System.Diagnostics.Stopwatch badNameClock = System.Diagnostics.Stopwatch.StartNew();
            LinkProbeResult badNameProbe = Tailscale.Probe("no-such-host.invalid", 1200);
            badNameClock.Stop();
            Check("主机名解析：无法解析时有界返回且不冒充成功",
                badNameClock.ElapsedMilliseconds < 4000
                && badNameProbe.Status != LinkProbeStatus.Reachable
                && badNameProbe.Text.IndexOf("ICMP 回包", StringComparison.Ordinal) < 0,
                badNameProbe.Status + " " + badNameClock.ElapsedMilliseconds + "ms " + badNameProbe.Text);

            string statusJson =
                "{\"BackendState\":\"Running\",\"Peer\":{"
                + "\"a\":{\"HostName\":\"glux-desk\",\"OS\":\"windows\",\"Online\":true,"
                + "\"CurAddr\":\"192.168.1.9:41641\",\"Relay\":\"tok\",\"PeerRelay\":\"\","
                + "\"TailscaleIPs\":[\"fd7a:115c:a1e0::1\",\"100.64.0.12\"]},"
                + "\"b\":{\"HostName\":\"phone\",\"OS\":\"android\",\"Online\":true,"
                + "\"TailscaleIPs\":[\"100.64.0.13\"]},"
                + "\"c\":{\"HostName\":\"glux-lap\",\"OS\":\"windows\",\"Online\":false,"
                + "\"CurAddr\":\"\",\"Relay\":\"fra\",\"PeerRelay\":\"\","
                + "\"TailscaleIPs\":[\"100.64.0.14\"]}}}";
            List<TsPeer> peers;
            string backend;
            string parseError;
            bool parsed = Tailscale.TryParseStatusJson(statusJson, out peers, out backend, out parseError);
            Check("status JSON：可解析且后端 Running", parsed && backend == "Running", parseError);
            Check("status JSON：只保留 Windows 节点", parsed && peers.Count == 2, "count=" + peers.Count);
            Check("status JSON：IPv4 优先",
                parsed && peers.Count > 0 && peers[0].Ip == "100.64.0.12",
                parsed && peers.Count > 0 ? peers[0].Ip : "-");
            Check("status JSON：在线节点排在前面",
                parsed && peers.Count == 2 && peers[0].Online && !peers[1].Online,
                parsed && peers.Count == 2 ? peers[0].Online + "/" + peers[1].Online : "-");
            Check("status JSON：直连与 DERP 文案可区分",
                parsed && peers.Count > 0 && peers.Count > 1
                && peers[0].LinkText.StartsWith("直连", StringComparison.Ordinal)
                && peers[1].LinkText.IndexOf("DERP(fra)", StringComparison.Ordinal) >= 0,
                parsed && peers.Count > 1 ? peers[0].LinkText + " | " + peers[1].LinkText : "-");

            List<TsPeer> needsLoginPeers;
            string needsLoginBackend;
            string needsLoginError;
            bool parsedNeedsLogin = Tailscale.TryParseStatusJson(
                "{\"BackendState\":\"NeedsLogin\",\"Peer\":{}}",
                out needsLoginPeers, out needsLoginBackend, out needsLoginError);
            Check("status JSON：可识别未登录状态", parsedNeedsLogin && needsLoginBackend == "NeedsLogin",
                needsLoginBackend);
            Check("未登录状态不当作无设备", !Tailscale.BackendReady("NeedsLogin")
                && Tailscale.BackendReady("Running")
                && !Tailscale.BackendReady("Stopped"), Tailscale.DescribeBackendState("NeedsLogin"));
            Check("未登录状态提示为中文说明",
                Tailscale.DescribeBackendState("NeedsLogin") == "需要登录"
                && Tailscale.DescribeBackendState("Running") == "运行中", Tailscale.DescribeBackendState("NeedsLogin"));

            // 自定义防窥壁纸校验与缩放几何
            Check("防窥壁纸扩展名校验：放行常见图片并拒绝可执行脚本",
                PrivacyWallpaper.IsSupportedExtension("wall.png")
                && PrivacyWallpaper.IsSupportedExtension("C:\\img\\pic.JPG")
                && !PrivacyWallpaper.IsSupportedExtension("bad.exe")
                && !PrivacyWallpaper.IsSupportedExtension("bad.ps1"),
                "ext");
            System.Drawing.RectangleF coverRect = PrivacyWallpaper.ComputeImageRect(
                new System.Drawing.Size(1000, 1000), new System.Drawing.Rectangle(0, 0, 1600, 900), PrivacyWallpaperConfig.FitCover);
            Check("防窥壁纸 Cover 裁切居中覆盖目标屏",
                coverRect.Width >= 1600f && coverRect.Height >= 900f && Math.Abs(coverRect.X) < 0.1f,
                coverRect.ToString());
            System.Drawing.RectangleF containRect = PrivacyWallpaper.ComputeImageRect(
                new System.Drawing.Size(1000, 1000), new System.Drawing.Rectangle(0, 0, 1600, 900), PrivacyWallpaperConfig.FitContain);
            Check("防窥壁纸 Contain 等比完整居中",
                Math.Abs(containRect.Width - 900f) < 0.5f && Math.Abs(containRect.Height - 900f) < 0.5f && Math.Abs(containRect.X - 350f) < 0.5f,
                containRect.ToString());

            // 防窥壁纸文字不暴露退出快捷键 + 手动解除自动锁屏解锁后绝不重新拉起防窥屏
            var badgeCfg = new PrivacyWallpaperConfig { Caption = "远程安全防窥保护中", ShowOverlayBadge = true };
            string badgeText = PrivacyWallpaper.FormatCurtainBadgeText(badgeCfg, new DateTime(2026, 11, 24, 9, 20, 0));
            Check("防窥壁纸文案：仅含时间与保护文案，不暴露退出快捷键",
                badgeText == "09:20  ·  远程安全防窥保护中"
                && badgeText.IndexOf("Ctrl", StringComparison.OrdinalIgnoreCase) < 0
                && badgeText.IndexOf("退出", StringComparison.Ordinal) < 0
                && badgeText.IndexOf("快捷键", StringComparison.Ordinal) < 0,
                badgeText);

            var guard = new PrivacyGuardState();
            DateTime t0 = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
            guard.OnManualActivate();
            long staleGen = guard.Generation;
            guard.OnManualExit(t0);
            Check("防窥锁屏状态机：手动退出后 2s 内拦截快捷键连击/回弹及键盘误触",
                !guard.CanActivateFromHotkey(t0.AddMilliseconds(400))
                && !guard.CanActivateFromUi(t0.AddMilliseconds(400), false)
                && guard.CanActivateFromUi(t0.AddMilliseconds(400), true),
                "debounce");
            guard.OnSessionLock(false);
            Check("防窥锁屏状态机：锁屏期间丢弃在途轮询与自动拉起",
                guard.EvaluateAutoGuardPoll(staleGen, true, false, false, true, t0.AddSeconds(1)) == PrivacyGuardAction.NoOp
                && guard.EvaluateAutoGuardPoll(guard.Generation, true, false, false, true, t0.AddSeconds(5)) == PrivacyGuardAction.NoOp,
                "locked");
            guard.OnSessionUnlock(t0.AddSeconds(8));
            Check("防窥锁屏状态机：输入密码解锁回到桌面后保持关闭，拦截回弹回车且不重新拉起",
                !guard.CanActivateFromHotkey(t0.AddSeconds(8.5))
                && !guard.CanActivateFromUi(t0.AddSeconds(9.0), false)
                && guard.CanActivateFromUi(t0.AddSeconds(9.0), true)
                && guard.EvaluateAutoGuardPoll(guard.Generation, true, false, false, true, t0.AddSeconds(12)) == PrivacyGuardAction.NoOp,
                "post-unlock");
            Check("防窥锁屏状态机：解锁冷静期内及单次状态抖动不解除屏蔽，防止桌面切换瞬间误重拉",
                guard.EvaluateAutoGuardPoll(guard.Generation, false, false, false, true, t0.AddSeconds(10)) == PrivacyGuardAction.NoOp
                && guard.EvaluateAutoGuardPoll(guard.Generation, false, false, false, true, t0.AddSeconds(14)) == PrivacyGuardAction.NoOp
                && guard.EvaluateAutoGuardPoll(guard.Generation, true, false, false, true, t0.AddSeconds(16)) == PrivacyGuardAction.NoOp,
                "anti-flap");
            Check("防窥锁屏状态机：解锁冷静期后连续 3 次观测到远控断开才重新武装，下次新远控正常自动防窥",
                guard.EvaluateAutoGuardPoll(guard.Generation, false, false, false, true, t0.AddSeconds(18)) == PrivacyGuardAction.NoOp
                && guard.EvaluateAutoGuardPoll(guard.Generation, false, false, false, true, t0.AddSeconds(20.5)) == PrivacyGuardAction.NoOp
                && guard.EvaluateAutoGuardPoll(guard.Generation, false, false, false, true, t0.AddSeconds(23)) == PrivacyGuardAction.NoOp
                && guard.EvaluateAutoGuardPoll(guard.Generation, true, false, false, true, t0.AddSeconds(26)) == PrivacyGuardAction.ActivateAuto,
                "re-arm");

            // 场景 2：防窥开启中直接触发系统锁屏（例如 Win+L），解锁返回桌面后同样保持关闭且屏蔽本轮自动重拉
            var directLockGuard = new PrivacyGuardState();
            directLockGuard.OnManualActivate();
            directLockGuard.OnSessionLock(true);
            directLockGuard.OnSessionUnlock(t0.AddSeconds(30));
            Check("防窥锁屏状态机：防窥开启后直接锁屏再解锁进入桌面，保持防窥关闭并屏蔽当前远控重拉",
                directLockGuard.ManualDismissSuppressed
                && !directLockGuard.SessionLocked
                && directLockGuard.EvaluateAutoGuardPoll(directLockGuard.Generation, true, false, false, true, t0.AddSeconds(38)) == PrivacyGuardAction.NoOp,
                "direct-lock-unlock");

            // 新手引导持久化 + 灰白框选避让几何 + 32bpp Alpha 渲染验收
            Check("新手引导状态解析：首次默认未完成，写入标记后识别已完成",
                !OnboardingStore.ParseCompleted("")
                && !OnboardingStore.ParseCompleted("completed=v0")
                && OnboardingStore.ParseCompleted(OnboardingStore.FormatCompleted()),
                OnboardingStore.FormatCompleted().Trim());

            CardLayoutResult rightPlace = OnboardingOverlayForm.ComputeCardPlacement(
                new System.Drawing.Size(1060, 700), new System.Drawing.Rectangle(6, 54, 224, 638), new System.Drawing.SizeF(368, 220), 1f);
            Check("新手引导避让：左侧栏框选自动放置于右侧且零重叠不越界",
                rightPlace.Side == CardPlacementSide.Right
                && rightPlace.CardRect.Left > 230f
                && rightPlace.CardRect.Right <= 1060f
                && rightPlace.CardRect.Bottom <= 700f,
                rightPlace.Side + " " + rightPlace.CardRect);

            CardLayoutResult topRightPlace = OnboardingOverlayForm.ComputeCardPlacement(
                new System.Drawing.Size(860, 560), new System.Drawing.Rectangle(540, 4, 164, 40), new System.Drawing.SizeF(368, 220), 1f);
            Check("新手引导避让：窄屏右上工具栏框选自动放置于下方且完整落入窗口内",
                topRightPlace.Side == CardPlacementSide.Bottom
                && topRightPlace.CardRect.Top >= 44f
                && topRightPlace.CardRect.Left >= 10f
                && topRightPlace.CardRect.Right <= 850f,
                topRightPlace.Side + " " + topRightPlace.CardRect);

            var testStep = new OnboardingStep
            {
                Title = "测试引导步骤",
                Description = "验证半透明灰遮罩、镂空框选透明区与纯白解说卡片的 32bpp Alpha 合成。",
                Tip = "支持键盘 ← / → 翻页"
            };
            using (var frame = OnboardingOverlayForm.RenderPreviewBitmap(
                new System.Drawing.Size(1060, 700), 1f, new System.Drawing.Rectangle(12, 60, 210, 580), testStep, 0, 7))
            {
                System.Drawing.Color holePx = frame.GetPixel(115, 350);
                System.Drawing.Color scrimPx = frame.GetPixel(950, 620);
                int opaqueWhiteCount = 0;
                int opaqueInkCount = 0;
                for (int y = 240; y < 440; y += 2)
                {
                    for (int x = 250; x < 590; x += 2)
                    {
                        System.Drawing.Color p = frame.GetPixel(x, y);
                        if (p.A == 255 && p.R >= 250 && p.G >= 250 && p.B >= 250) opaqueWhiteCount++;
                        else if (p.A == 255 && p.R < 90 && p.G < 100 && p.B < 120) opaqueInkCount++;
                    }
                }
                Check("新手引导灰白框选像素：镂空区近全透、遮罩半透深灰、卡片纯白且含不透明清晰文字",
                    holePx.A <= 2
                    && scrimPx.A >= 120 && scrimPx.A <= 170
                    && opaqueWhiteCount > 2000
                    && opaqueInkCount > 30,
                    string.Format("holeA={0} scrimA={1} white={2} ink={3}", holePx.A, scrimPx.A, opaqueWhiteCount, opaqueInkCount));
            }
            System.Drawing.Bitmap brandLogo = RemoteTheme.GetBrandLogoBitmap();
            Check("BoBo远程 品牌 Logo 资源：内嵌圆角方框奶牛猫徽章可正常加载",
                brandLogo != null && brandLogo.Width >= 32 && brandLogo.Height >= 32,
                brandLogo == null ? "null" : (brandLogo.Width + "x" + brandLogo.Height));
            var barExpanded = StreamToolbarOverlay.ComputeBarBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), true, 1f);
            var barCollapsed = StreamToolbarOverlay.ComputeBarBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), false, 1f);
            var barDraggedLeft = StreamToolbarOverlay.ComputeBarBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), false, 1f, 40);
            Check("V5 顶部悬浮小浮窗几何：展开/收起居中且含快捷退出区，拖动边界自动钳制",
                barExpanded.Width >= 700 && barExpanded.Top == 8 && Math.Abs((barExpanded.Left + barExpanded.Width / 2) - 960) <= 1
                && barCollapsed.Width >= 180 && barCollapsed.Width <= 240 && barCollapsed.Height >= 28 && barCollapsed.Top == 6
                && Math.Abs((barCollapsed.Left + barCollapsed.Width / 2) - 960) <= 1
                && barDraggedLeft.Left >= 10,
                "exp=" + barExpanded + " col=" + barCollapsed + " drag=" + barDraggedLeft);

            var sqRight = StreamToolbarOverlay.ComputeDockedSquareBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), FloatDockEdge.Right, 0.3f, false, 1f);
            var sqRightHid = StreamToolbarOverlay.ComputeDockedSquareBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), FloatDockEdge.Right, 0.3f, true, 1f);
            var sqLeftHid = StreamToolbarOverlay.ComputeDockedSquareBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), FloatDockEdge.Left, 0.5f, true, 1f);
            var sqTopHid = StreamToolbarOverlay.ComputeDockedSquareBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), FloatDockEdge.Top, 0.5f, true, 1f);
            var sqBotHid = StreamToolbarOverlay.ComputeDockedSquareBounds(new System.Drawing.Rectangle(0, 0, 1920, 1080), FloatDockEdge.Bottom, 0.5f, true, 1f);
            Check("V5.1 可移动四边贴边隐藏方形悬浮球几何：全四边吸附与贴边隐藏把手尺寸正确",
                sqRight.Width == 46 && sqRight.Height == 46 && sqRight.Right <= 1914
                && sqRightHid.Right == 1920 && sqRightHid.Width == 12 && sqRightHid.Height == 46
                && sqLeftHid.Left == 0 && sqLeftHid.Width == 12
                && sqTopHid.Top == 0 && sqTopHid.Height == 12 && sqTopHid.Width == 46
                && sqBotHid.Bottom == 1080 && sqBotHid.Height == 12,
                "sqR=" + sqRight + " hidR=" + sqRightHid + " hidL=" + sqLeftHid + " hidT=" + sqTopHid + " hidB=" + sqBotHid);

            var ccModel = new StreamControlCenterModel { HostDisplayName = "测试主机", HostNativeWidth = 2880, HostNativeHeight = 1800, LocalDisplayWidth = 2560, LocalDisplayHeight = 1440 };
            var ccItems = UuControlCenterSession.BuildRootMenuItems(ccModel, null);
            bool hasDisplay = false, hasQuality = false, hasWindow = false, hasSound = false, hasSec = false, hasPeriph = false, hasMore = false, hasExit = false;
            foreach (var ci in ccItems)
            {
                if (ci.Text.StartsWith("显示屏 1") && ci.SubmenuBuilder != null) hasDisplay = ci.SubmenuBuilder().Count >= 8;
                if (ci.Text == "画质" && ci.SubmenuBuilder != null) hasQuality = ci.SubmenuBuilder().Count >= 12;
                if (ci.Text == "窗口" && ci.SubmenuBuilder != null) hasWindow = ci.SubmenuBuilder().Count >= 4;
                if (ci.Text == "声音" && ci.IsVolumeSliderSubmenu) hasSound = true;
                if (ci.Text == "安全" && ci.SubmenuBuilder != null) hasSec = ci.SubmenuBuilder().Count >= 4;
                if (ci.Text == "外设" && ci.SubmenuBuilder != null) hasPeriph = ci.SubmenuBuilder().Count >= 6;
                if (ci.Text == "更多" && ci.SubmenuBuilder != null) hasMore = ci.SubmenuBuilder().Count >= 2;
                if (ci.Text == "退出远控" && ci.IsDanger) hasExit = true;
            }
            Check("V5.1 UU 风格控制中心级联菜单：显示屏/画质/窗口/音量滑块/安全/外设/更多/红色退出完整齐备",
                hasDisplay && hasQuality && hasWindow && hasSound && hasSec && hasPeriph && hasMore && hasExit,
                "count=" + ccItems.Count);

            Check("本机地址隐私脱敏：默认隐藏 IPv4 后两段与完整 IPv6 前缀",
                MainForm.MaskAddressForDisplay("192.0.2.88") == "192.0.•••.•••"
                && MainForm.MaskAddressForDisplay("100.64.12.34") == "100.64.•••.•••"
                && MainForm.MaskAddressForDisplay("2001:db8:85a3::8a2e:370:7334") == "IPv6 ••••:••••"
                && MainForm.MaskAddressForDisplay("") == "未获取到网络地址",
                MainForm.MaskAddressForDisplay("192.0.2.88"));

            // 内置自动更新器核心逻辑自检
            Check("自动更新版本比较：语义化数字顺序",
                AppUpdater.CompareVersions("4.2.0", "4.3.0") < 0
                && AppUpdater.CompareVersions("v4.3.0", "4.3.0") == 0
                && AppUpdater.CompareVersions("4.10.0", "4.9.9") > 0,
                "current=" + AppUpdater.CurrentVersion);
            Check("自动更新域名白名单：仅允许官方 GitHub HTTPS",
                AppUpdater.IsTrustedDownloadUrl("https://github.com/Gluxggg/BoBo-Remote/releases/download/v4.3.0/BoBoRemote-Update-x64.zip")
                && AppUpdater.IsTrustedDownloadUrl("https://objects.githubusercontent.com/github-production-release-asset/test")
                && !AppUpdater.IsTrustedDownloadUrl("http://github.com/Gluxggg/BoBo-Remote/releases/download/v4.3.0/BoBoRemote-Update-x64.zip")
                && !AppUpdater.IsTrustedDownloadUrl("https://evil.example.com/BoBoRemote-Update-x64.zip"),
                "whitelist");
            string sampleReleaseJson =
                "{\"tag_name\":\"v9.9.9\",\"html_url\":\"https://github.com/Gluxggg/BoBo-Remote/releases/tag/v9.9.9\",\"body\":\"- 修复 Win11 高 DPI 变形\\n- 新增自动更新\","
                + "\"assets\":["
                + "{\"name\":\"SHA256SUMS.txt\",\"size\":320,\"browser_download_url\":\"https://github.com/Gluxggg/BoBo-Remote/releases/download/v9.9.9/SHA256SUMS.txt\"},"
                + "{\"name\":\"BoBoRemote-Update-x64.zip\",\"size\":204800,\"browser_download_url\":\"https://github.com/Gluxggg/BoBo-Remote/releases/download/v9.9.9/BoBoRemote-Update-x64.zip\"},"
                + "{\"name\":\"BoBoRemote-Setup-x64.exe\",\"size\":104857600,\"browser_download_url\":\"https://github.com/Gluxggg/BoBo-Remote/releases/download/v9.9.9/BoBoRemote-Setup-x64.exe\"}"
                + "]}";
            ReleaseManifestInfo parsedRelease;
            string releaseErr;
            bool releaseOk = AppUpdater.TryParseLatestReleaseJson(sampleReleaseJson, out parsedRelease, out releaseErr);
            string sampleHashA = new string('a', 64);
            string sampleHashB = new string('b', 64);
            string sampleSums = "# engines=" + AppUpdater.CurrentEnginesStamp + "\n"
                + sampleHashA + "  BoBoRemote-Update-x64.zip\n"
                + sampleHashB + "  BoBoRemote-Setup-x64.exe\n";
            string parsedEngines;
            Dictionary<string, string> parsedHashes;
            bool sumsOk = AppUpdater.ParseSha256Sums(sampleSums, out parsedEngines, out parsedHashes);
            var lightPlan = new UpdateCheckResult();
            var fullPlan = new UpdateCheckResult();
            var roPlan = new UpdateCheckResult();
            if (releaseOk && sumsOk)
            {
                AppUpdater.SelectBestPlan(parsedRelease, parsedEngines, parsedHashes, true, true, lightPlan);
                AppUpdater.SelectBestPlan(parsedRelease, "rd1.5.4-changed", parsedHashes, true, true, fullPlan);
                AppUpdater.SelectBestPlan(parsedRelease, parsedEngines, parsedHashes, true, false, roPlan);
            }
            Check("自动更新计划选择：同引擎走轻量包、换引擎走安装包、只读目录降级手动",
                releaseOk && sumsOk
                && lightPlan.PackageKind == UpdatePackageKind.LightZip && lightPlan.ExpectedSha256 == sampleHashA
                && fullPlan.PackageKind == UpdatePackageKind.FullSetup && fullPlan.ExpectedSha256 == sampleHashB
                && roPlan.PackageKind == UpdatePackageKind.None,
                releaseErr ?? (lightPlan.PackageKind + "/" + fullPlan.PackageKind + "/" + roPlan.PackageKind));
            Check("轻量更新包 ZIP 根目录前缀剥离",
                AppUpdater.NormalizeLightZipEntry("BoBo远程/BoBo远程.exe") == "BoBo远程.exe"
                && AppUpdater.NormalizeLightZipEntry("BoBo远程/docs/guide.md") == @"docs\guide.md",
                AppUpdater.NormalizeLightZipEntry("BoBo远程/BoBo远程.exe"));

            string parsedRedirectTag = null;
            string parsedRelativeTag = null;
            string badRedirectTag = null;
            bool redirectExtractOk = AppUpdater.TryExtractTagFromReleaseUrl("https://github.com/Gluxggg/BoBo-Remote/releases/tag/v4.3.1", out parsedRedirectTag)
                && parsedRedirectTag == "v4.3.1"
                && AppUpdater.TryExtractTagFromReleaseUrl("/Gluxggg/BoBo-Remote/releases/tag/v4.3.1?foo=1", out parsedRelativeTag)
                && parsedRelativeTag == "v4.3.1"
                && !AppUpdater.TryExtractTagFromReleaseUrl("https://evil.example.com/Gluxggg/BoBo-Remote/releases/tag/v4.3.1", out badRedirectTag)
                && !AppUpdater.TryExtractTagFromReleaseUrl("https://github.com/other/repo/releases/tag/v4.3.1", out badRedirectTag)
                && !AppUpdater.TryExtractTagFromReleaseUrl("https://github.com/Gluxggg/BoBo-Remote/releases/tag/../../etc", out badRedirectTag);
            Check("自动更新 302 重定向标签安全提取", redirectExtractOk, parsedRedirectTag ?? "null");

            ReleaseManifestInfo redirectRelease = AppUpdater.BuildReleaseFromTag("v4.3.1");
            var redirectPlan = new UpdateCheckResult();
            if (redirectRelease != null && sumsOk)
            {
                AppUpdater.SelectBestPlan(redirectRelease, parsedEngines, parsedHashes, true, true, redirectPlan);
            }
            Check("自动更新由 tag 构造可信下载链接与更新计划",
                redirectRelease != null
                && AppUpdater.IsTrustedDownloadUrl(redirectRelease.Sha256SumsUrl)
                && AppUpdater.IsTrustedDownloadUrl(redirectRelease.LightZipUrl)
                && AppUpdater.IsTrustedDownloadUrl(redirectRelease.SetupExeUrl)
                && redirectPlan.PackageKind == UpdatePackageKind.LightZip
                && redirectPlan.DownloadUrl == "https://github.com/Gluxggg/BoBo-Remote/releases/download/v4.3.1/BoBoRemote-Update-x64.zip",
                redirectRelease == null ? "null" : redirectRelease.LightZipUrl);

            // Win11 多档 DPI 新手引导卡片自适应度量与渲染自检
            bool tourDpiOk = true;
            string tourDpiDetail = "ok";
            var sampleTourStep = new OnboardingStep
            {
                Title = "软件防窥壁纸与可选硬件熄屏",
                Description = "软件防窥壁纸开箱即用：开启后物理屏立刻覆盖安全壁纸，而远程画面依然清晰可见，支持远控自动开启与退出自动锁屏。紧急解除按 Ctrl+Alt+Shift+P。",
                Tip = "可选进阶：若要硬件级彻底熄屏（关闭物理显示器信号），需额外搭配 Zako 虚拟屏与系统安装版 Sunshine。"
            };
            float[] dpiScales = new[] { 1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 2.25f };
            foreach (float sc in dpiScales)
            {
                var vp = new System.Drawing.Size((int)Math.Round(960 * sc), (int)Math.Round(600 * sc));
                var target = new System.Drawing.Rectangle((int)Math.Round(240 * sc), (int)Math.Round(90 * sc), (int)Math.Round(620 * sc), (int)Math.Round(260 * sc));
                CardMetrics cm = OnboardingOverlayForm.MeasureCardMetrics(vp, sc, sampleTourStep);
                CardLayoutResult cl = OnboardingOverlayForm.ComputeCardPlacement(vp, target, new System.Drawing.SizeF(cm.CardWidth, cm.CardHeight), sc);
                if (cm.CardHeight > vp.Height || cm.FooterRect.Bottom > cm.ContentHeight || cl.CardRect.Width < 120)
                {
                    tourDpiOk = false;
                    tourDpiDetail = "scale=" + sc + " card=" + cm.CardWidth + "x" + cm.CardHeight + " vp=" + vp.Width + "x" + vp.Height;
                    break;
                }
                using (System.Drawing.Bitmap bmp = OnboardingOverlayForm.RenderPreviewBitmap(vp, sc, target, sampleTourStep, 4, 7))
                {
                    if (bmp == null || bmp.Width != vp.Width || bmp.Height != vp.Height)
                    {
                        tourDpiOk = false;
                        tourDpiDetail = "bitmap mismatch at scale=" + sc;
                        break;
                    }
                }
            }
            Check("Win10/Win11 高 DPI 新手引导卡片自适应度量与渲染（100%~225%）", tourDpiOk, tourDpiDetail);

            // 验证真实 DPI 获取与多缩放倍率下双行卡片高度绝不裁切文字（覆盖 100%~225% 缩放）
            float gdiScale = 1f;
            using (System.Drawing.Graphics sg = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                if (sg.DpiX >= 72f) gdiScale = Math.Max(1f, sg.DpiX / 96f);
            }
            float themeScale = RemoteTheme.GetDpiScale();
            Check("Win10/Win11 DPI 引擎：GetDpiScale 与真实系统 GDI DPI 严格一致（不退化为 96）",
                Math.Abs(themeScale - gdiScale) < 0.05f && themeScale >= 1f,
                "themeScale=" + themeScale + " gdiScale=" + gdiScale);

            bool cardMetricsOk = true;
            string cardMetricsDetail = "";
            foreach (float sc in dpiScales)
            {
                int innerH = (int)Math.Round(52f * sc);
                using (var testBmp = new System.Drawing.Bitmap(480, Math.Max(40, innerH)))
                {
                    testBmp.SetResolution(96f * sc, 96f * sc);
                    using (var tg = System.Drawing.Graphics.FromImage(testBmp))
                    {
                        var flags = System.Windows.Forms.TextFormatFlags.SingleLine
                            | System.Windows.Forms.TextFormatFlags.NoPrefix
                            | System.Windows.Forms.TextFormatFlags.NoPadding;
                        var sz1 = System.Windows.Forms.TextRenderer.MeasureText(tg, "Desktop-Host-01",
                            RemoteTheme.GetFont(RemoteTheme.TextSize.LabelBold), new System.Drawing.Size(2000, innerH), flags);
                        var sz2 = System.Windows.Forms.TextRenderer.MeasureText(tg, "198.51.100.42  ·  DERP(tok)（最近记录）",
                            RemoteTheme.GetFont(RemoteTheme.TextSize.Small), new System.Drawing.Size(2000, innerH), flags);
                        int neededH = sz1.Height + sz2.Height + (int)Math.Ceiling(6f * sc) + (int)Math.Round(2f * sc);
                        if (neededH > innerH)
                        {
                            cardMetricsOk = false;
                            cardMetricsDetail = "scale=" + sc + " neededH=" + neededH + " > innerH=" + innerH;
                            break;
                        }
                    }
                }
            }
            Check("Win10/Win11 双行设备卡片与设置行高度在 100%~225% DPI 下零切字", cardMetricsOk, cardMetricsDetail);

            using (var vpTest = new RemoteTheme.SlimScrollViewport())
            {
                Check("宽屏工作区自适应容器：MaxContentWidth 支持高分屏舒展排布（消除左右大面积留白）",
                    vpTest.MaxContentWidth >= 1500, "MaxContentWidth=" + vpTest.MaxContentWidth);
            }

            var defaultPrivacy = new PrivacyWallpaperConfig();
            Check("自动防窥守护默认策略：新安装默认开启被控自动防窥，防窥壁纸透明穿透不挡远控",
                defaultPrivacy.AutoOnRemote,
                "AutoOnRemote=" + defaultPrivacy.AutoOnRemote);

            int wp1 = DeviceWallpaperGallery.GetOrAssignIndex("198.51.100.25");
            int wp2 = DeviceWallpaperGallery.CycleRandomIndex("198.51.100.25");
            bool renderedSceneOk = false;
            using (var testBmp = new System.Drawing.Bitmap(320, 160))
            using (var testG = System.Drawing.Graphics.FromImage(testBmp))
            {
                DeviceWallpaperGallery.DrawBuiltInWindowsLandscape(testG, new System.Drawing.RectangleF(0, 0, 320, 160), 0, true);
                DeviceWallpaperGallery.DrawWallpaper(testG, new System.Drawing.RectangleF(0, 0, 320, 160), wp2, true);
                renderedSceneOk = testBmp.GetPixel(160, 80).A == 255;
            }
            DeviceWallpaperGallery.DisposeCache();
            Check("设备预览窗随机 Windows 风景壁纸图库：支持随机分配、切换与离屏渲染",
                DeviceWallpaperGallery.TotalCount >= 1
                && (DeviceWallpaperGallery.TotalCount == 1 || wp1 != wp2)
                && renderedSceneOk,
                "count=" + DeviceWallpaperGallery.TotalCount + " wp=" + wp1 + "->" + wp2);

            Log("");
            Info("Tailscale CLI：" + (Engines.TailscalePresent ? "已找到" : "未找到") + " " + Tailscale.ExePath);
            Info("最近目标目录：" + RecentStore.DirPath);
            // V6 全内置架构端到端闭环自检：内嵌 BoBoCore.dll + 严格双端必开 TLS 1.2 + 4 位临时配对码 + 视频帧 + 防窥指令 + 文件 SHA-256 互传
            RunV6AllInOneSelfTests();

            Log("");
            Log(_failures == 0 ? "结果：全部通过" : "结果：失败 " + _failures + " 项");
            string reportPath = Path.Combine(Path.GetTempPath(), "双端远程-self-test.log");
            if (!TryWriteReport(reportPath))
            {
                reportPath = Path.Combine(RecentStore.DirPath, "self-test.log");
                if (TryWriteReport(reportPath))
                {
                    Log("报告文件：" + reportPath);
                }
                else
                {
                    Log("[信息] 报告文件未能写入（控制台与退出码仍然有效）。");
                }
            }
            return _failures == 0 ? 0 : 1;
        }

        private static void RunV6AllInOneSelfTests()
        {
            // 1. Embedded BoBoCore.dll extraction & native capture/H.264/D3D11VA/ADPCM self-test
            string nativeReport;
            bool coreOk = BoBoCoreInterop.RunNativeSelfTest(out nativeReport);
            Check("V6 全内置原生内核（BoBoCore.dll.gz 嵌入释放 + 编解码回环自检）",
                coreOk,
                string.IsNullOrEmpty(nativeReport) ? "empty report" : nativeReport.Replace("\r", " ").Replace("\n", " | "));

            // 2. End-to-end localhost TLS 1.2 dual-end pairing, strict rejection, streaming, privacy & file transfer
            string testRoot = Path.Combine(Path.GetTempPath(), "BoBoV6-SelfTest-" + Guid.NewGuid().ToString("N"));
            string receivedFileToClean = null;
            try
            {
                Directory.CreateDirectory(testRoot);
                var hostId = new BoBoIdentityStore(Path.Combine(testRoot, "host"));
                var clientId = new BoBoIdentityStore(Path.Combine(testRoot, "client"));
                hostId.EnsureLoaded();
                clientId.EnsureLoaded();

                Check("V6 独立节点身份：RSA-2048 证书与 9 位 BoBo 设备码即时生成",
                    !string.IsNullOrEmpty(hostId.NodeId)
                    && BoBoIdentityStore.IsNineDigitCode(hostId.DeviceCode)
                    && BoBoIdentityStore.IsNineDigitCode(clientId.DeviceCode)
                    && !string.Equals(hostId.CertThumbprint, clientId.CertThumbprint, StringComparison.OrdinalIgnoreCase),
                    "host=" + hostId.FormattedDeviceCode + " client=" + clientId.FormattedDeviceCode);

                using (var server = new BoBoHostServer(hostId, 0, false))
                {
                    string srvErr;
                    bool started = server.Start(out srvErr);
                    int port = server.ListeningPort;
                    Check("V6 内置被控服务启动（TLS 1.2 动态测试端口）", started && port > 0, srvErr ?? ("port=" + port));
                    if (!started) return;

                    // 2a. Unpaired probe & strict rejection
                    BoBoHostProbeResult probe1 = BoBoStreamClient.ProbeHostAsync("127.0.0.1", port, clientId, 4000, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("V6 双端握手探测：未配对设备识别为在线但未授权",
                        probe1.Reachable && probe1.IsBoBoHost && probe1.AllowIncoming && !probe1.Authorized,
                        probe1.Error ?? ("host=" + probe1.HostName));

                    using (var unauthClient = new BoBoStreamClient(clientId))
                    {
                        var unauthRes = unauthClient.ConnectAndStartStreamAsync(
                            new BoBoStreamStartOptions { Host = "127.0.0.1", Port = port, Width = 640, Height = 360, Fps = 30, BitrateKbps = 4000 },
                            null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                        Check("V6 严格鉴权：未配对客户端必须返回 PAIR_REQUIRED 拒绝建流",
                            !unauthRes.Item1 && string.Equals(unauthRes.Item2, "PAIR_REQUIRED", StringComparison.Ordinal),
                            unauthRes.Item2 + " " + unauthRes.Item3);
                    }

                    // 2b. Instant 4-digit PIN generation + wrong PIN rejected + brute-force 5-attempt lockout + right PIN succeeds
                    BoBoLocalPairSession lockSession = server.BeginPairing(120);
                    string lockPin = lockSession.Pin;
                    string badLockPin = lockPin == "0000" ? "1111" : "0000";
                    for (int attempt = 0; attempt < BoBoLocalPairSession.MaxFailedAttempts; attempt++)
                    {
                        var failTry = BoBoStreamClient.PairWithPinAsync("127.0.0.1", port, badLockPin, clientId, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                        if (failTry.Item1) break;
                    }
                    var lockedOutTry = BoBoStreamClient.PairWithPinAsync("127.0.0.1", port, lockPin, clientId, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("V6.3 安全加固（P0-4）：4 位配对码连续失败 5 次立即作废，阻止爆破",
                        !lockedOutTry.Item1 && server.GetActivePairing() == null,
                        lockedOutTry.Item3);

                    BoBoLocalPairSession pinSession = server.BeginPairing(120);
                    string validPin = pinSession.Pin;
                    string wrongPin = validPin == "0000" ? "1111" : "0000";

                    var badPair = BoBoStreamClient.PairWithPinAsync("127.0.0.1", port, wrongPin, clientId, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("V6 临时 4 位配对码：错误配对码必须拒绝", !badPair.Item1, badPair.Item3);

                    var goodPair = BoBoStreamClient.PairWithPinAsync("127.0.0.1", port, validPin, clientId, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("V6 临时 4 位配对码：正确 HMAC + RSA 密钥证明校验通过并双向持久化授权",
                        goodPair.Item1 && hostId.IsPeerAuthorized(clientId.NodeId, clientId.CertThumbprint),
                        goodPair.Item3);

                    // 2b-2. P0-3 Security: RSA challenge proof-of-possession + impersonation rejection + client TOFU pin mismatch rejection
                    string sampleNonce = BoBoIdentityStore.GenerateRandomNonceHex(16);
                    string validSig = clientId.SignAuthChallenge(sampleNonce, hostId.CertThumbprint);
                    bool rsaOk = BoBoIdentityStore.VerifyAuthChallenge(clientId.CertDerBase64, clientId.CertThumbprint, sampleNonce, hostId.CertThumbprint, validSig);
                    bool rsaWrongHost = !BoBoIdentityStore.VerifyAuthChallenge(clientId.CertDerBase64, clientId.CertThumbprint, sampleNonce, clientId.CertThumbprint, validSig);
                    bool rsaWrongCert = !BoBoIdentityStore.VerifyAuthChallenge(hostId.CertDerBase64, clientId.CertThumbprint, sampleNonce, hostId.CertThumbprint, validSig);
                    Check("V6.3 安全加固（P0-3）：RSA-SHA256 挑战签名防身份冒充与证书替换",
                        rsaOk && rsaWrongHost && rsaWrongCert,
                        "ok=" + rsaOk + " wrongHostRej=" + rsaWrongHost + " wrongCertRej=" + rsaWrongCert);

                    // Client TOFU pin mismatch test: tamper pinned thumbprint -> ConnectAndStartStreamAsync must return CERT_MISMATCH!
                    var tofuClientId = new BoBoIdentityStore(Path.Combine(testRoot, "tofu-client"));
                    tofuClientId.EnsureLoaded();
                    hostId.AuthorizeOrUpdatePeer(new BoBoAuthorizedPeer
                    {
                        NodeId = tofuClientId.NodeId,
                        DeviceCode = tofuClientId.DeviceCode,
                        DeviceName = "TofuTest",
                        CertThumbprint = tofuClientId.CertThumbprint,
                        LastHost = "127.0.0.1"
                    });
                    string fakePinnedThumb = new string('f', 64);
                    tofuClientId.AuthorizeOrUpdatePeer(new BoBoAuthorizedPeer
                    {
                        NodeId = hostId.NodeId,
                        DeviceCode = hostId.DeviceCode,
                        DeviceName = hostId.DeviceName,
                        CertThumbprint = fakePinnedThumb,
                        LastHost = "127.0.0.1"
                    });
                    using (var tofuClient = new BoBoStreamClient(tofuClientId))
                    {
                        var tofuRes = tofuClient.ConnectAndStartStreamAsync(
                            new BoBoStreamStartOptions { Host = "127.0.0.1", Port = port, Width = 640, Height = 360, Fps = 30, BitrateKbps = 4000 },
                            null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                        BoBoAuthorizedPeer stillPinned = tofuClientId.FindPeerByNodeId(hostId.NodeId);
                        Check("V6.3 安全加固（P0-3）：客户端严格 TOFU 证书钉扎，指纹漂移拒绝连入且绝不静默覆盖",
                            !tofuRes.Item1 && tofuRes.Item2 == "CERT_MISMATCH" && stillPinned != null && stillPinned.CertThumbprint == fakePinnedThumb,
                            tofuRes.Item2 + " " + tofuRes.Item3);
                    }

                    // P0-6 Filename sanitization test
                    bool fnSafe = BoBoProtocol.SanitizeReceivedFileName(@"..\..\Windows\System32\evil.exe") == "evil.exe"
                        && BoBoProtocol.SanitizeReceivedFileName("..") == "received.bin"
                        && BoBoProtocol.SanitizeReceivedFileName("CON.txt") == "_CON.txt"
                        && BoBoProtocol.SanitizeReceivedFileName("a:b*c?d.pdf") == "abcd.pdf";
                    Check("V6.3 安全加固（P0-6）：跨平台文件名消毒（拦截目录穿越、保留设备名与非法字符）",
                        fnSafe,
                        BoBoProtocol.SanitizeReceivedFileName(@"..\..\Windows\System32\evil.exe"));

                    // 2c. Authorized stream receives real H.264 frame + <5ms sync privacy callback + direct privacy toggle
                    bool sessionStarted = false;
                    bool sessionRequestedPrivacy = false;
                    bool privacyCmdToggledOff = false;
                    var frameEvt = new System.Threading.ManualResetEventSlim(false);
                    var sessionEndedEvt = new System.Threading.ManualResetEventSlim(false);
                    var privOffEvt = new System.Threading.ManualResetEventSlim(false);
                    server.SessionStateChanged += delegate(bool active, string peer, bool reqPriv)
                    {
                        if (active) { sessionStarted = true; sessionRequestedPrivacy = reqPriv; }
                        else sessionEndedEvt.Set();
                    };
                    server.PrivacyCommandReceived += delegate(bool enable)
                    {
                        if (!enable) { privacyCmdToggledOff = true; privOffEvt.Set(); }
                    };

                    using (var authClient = new BoBoStreamClient(clientId))
                    {
                        authClient.RawVideoFrameReceived += delegate(byte[] data, bool isKey, int w, int h)
                        {
                            if (data != null && data.Length > 0) frameEvt.Set();
                        };
                        var connRes = authClient.ConnectAndStartStreamAsync(
                            new BoBoStreamStartOptions
                            {
                                Host = "127.0.0.1",
                                Port = port,
                                Width = 640,
                                Height = 360,
                                Fps = 30,
                                BitrateKbps = 4000,
                                MuteHostAudio = false,
                                RequestPrivacy = true,
                                SuperDisplay = false
                            },
                            null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();

                        bool gotFrame = connRes.Item1 && frameEvt.Wait(6000);
                        Check("V6 授权建流：实收 H.264 编码帧并同步触发防窥",
                            connRes.Item1 && gotFrame && sessionStarted && sessionRequestedPrivacy,
                            "conn=" + connRes.Item2 + " frames=" + authClient.VideoFramesReceived + " priv=" + sessionRequestedPrivacy);

                        // Wait briefly for UDP+FEC fast-path punch to complete on localhost
                        for (int uw = 0; uw < 30 && !authClient.IsUdpActive; uw++) System.Threading.Thread.Sleep(30);
                        Check("V6.3 低延迟加密 UDP+FEC 通道：握手后自动无感升级 UDP 零队头阻塞传输",
                            authClient.IsUdpActive && authClient.ActiveTransportLabel == "UDP+FEC",
                            "udpActive=" + authClient.IsUdpActive + " transport=" + authClient.ActiveTransportLabel + " udpPort=" + server.ListeningUdpPort);

                        Check("V6.3 零延迟本地光标模式：自由鼠标模式下关闭主机合成以启用零拷贝 GPU 采集与 0ms 本机指针",
                            !authClient.HostCursorComposited,
                            "hostCursorComposited=" + authClient.HostCursorComposited);

                        // Toggle to Locked mode -> host enables cursor compositing dynamically
                        authClient.SendSetCursorMode(true);
                        for (int cw = 0; cw < 25 && !authClient.HostCursorComposited; cw++) System.Threading.Thread.Sleep(30);
                        Check("V6.3 锁鼠模式光标热切换：SET_CURSOR_MODE(true) 动态恢复主机光标合成",
                            authClient.HostCursorComposited,
                            "hostCursorComposited=" + authClient.HostCursorComposited);

                        authClient.SendSetPrivacy(false);
                        bool gotPrivOff = privOffEvt.Wait(3000) && privacyCmdToggledOff;
                        Check("V6 直连控制：主控端直接开关被控端防窥指令", gotPrivOff, "toggledOff=" + privacyCmdToggledOff);

                        using (var viewportEvt = new System.Threading.ManualResetEventSlim(false))
                        {
                            BoBoDisplayState viewportReply = null;
                            Action<BoBoDisplayState> onViewport = delegate(BoBoDisplayState state)
                            {
                                if (state.RequestId != 7) return;
                                viewportReply = state;
                                viewportEvt.Set();
                            };
                            authClient.DisplayStateChanged += onViewport;
                            try
                            {
                                authClient.SendSetSuperDisplay(true, 960, 600, false, 7);
                                bool received = viewportEvt.Wait(3000);
                                Check("V7 超级屏：窗口区域协商返回实际串流尺寸，保持主机显示模式",
                                    received && authClient.SupportsSuperDisplayViewport && viewportReply.Active &&
                                    !viewportReply.DesktopModeChanged && viewportReply.ViewportWidth == 960 &&
                                    viewportReply.ViewportHeight == 600 && viewportReply.StreamWidth > 0 && viewportReply.StreamHeight > 0 &&
                                    (long)viewportReply.StreamWidth * viewportReply.StreamHeight <= 640L * 360L,
                                    received ? viewportReply.StreamWidth + "×" + viewportReply.StreamHeight : "未收到窗口适配回执");
                            }
                            finally
                            {
                                authClient.DisplayStateChanged -= onViewport;
                            }
                        }

                        var sdEvt = new System.Threading.ManualResetEventSlim(false);
                        bool sdReceived = false;
                        authClient.SuperDisplayStateChanged += delegate(bool act, bool nat, int sw, int sh)
                        {
                            sdReceived = !act;
                            sdEvt.Set();
                        };
                        authClient.SendSetSuperDisplay(false, 0, 0);
                        bool gotSd = sdEvt.Wait(3000) && sdReceived;
                        Check("V6.1 超级屏协议：SET_SUPER_DISPLAY / SUPER_DISPLAY_STATE 闭环回执", gotSd, "sdReceived=" + sdReceived);

                        // Burst coalesced mouse moves + ordered click/release + clean disconnect through FIFO writer
                        for (int m = 0; m < 20; m++)
                        {
                            authClient.SendInputPacket(new BoBoInputPacket { Kind = 1, Flags = 0, A = unchecked((short)(m * 2000)), B = unchecked((short)(m * 1500)) });
                        }
                        for (int r = 0; r < 10; r++)
                        {
                            authClient.SendInputPacket(new BoBoInputPacket { Kind = 2, Flags = 0, A = 3, B = -2 });
                        }
                        authClient.SendInputPacket(new BoBoInputPacket { Kind = 6, Flags = 0, A = 0, B = 0 });
                        authClient.SendSetPrivacy(false);
                        authClient.Disconnect();
                    }

                    Check("被控端退出动作：主控断开后主机收到会话结束通知",
                        sessionEndedEvt.Wait(3000) && !server.HasActiveRemoteSession,
                        "active=" + server.HasActiveRemoteSession);
                    sessionEndedEvt.Dispose();

                    // 2d. Built-in TLS SHA-256 file transfer
                    string sampleFile = Path.Combine(testRoot, "bobo-v6-selftest.txt");
                    string payloadText = "BoBoRemote V6 All-In-One TLS SHA-256 file transfer verification " + Guid.NewGuid().ToString("N");
                    File.WriteAllText(sampleFile, payloadText, new UTF8Encoding(false));
                    BoBoFileTransferProgress fileProg = BoBoStreamClient.SendFileAsync(
                        "127.0.0.1", port, sampleFile, clientId, null, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    receivedFileToClean = fileProg.SavedPath;
                    bool fileMatch = fileProg.Completed
                        && !string.IsNullOrEmpty(fileProg.SavedPath)
                        && File.Exists(fileProg.SavedPath)
                        && string.Equals(File.ReadAllText(fileProg.SavedPath, Encoding.UTF8), payloadText, StringComparison.Ordinal);
                    Check("V6 内置文件传输：TLS 1.2 分块传输与 SHA-256 完整性校验",
                        fileMatch,
                        fileProg.Completed ? fileProg.SavedPath : fileProg.Error);

                    // 2e. Stop server -> physically unreachable (simulating closing BoBo远程)
                    server.Stop();
                    BoBoHostProbeResult probeAfterStop = BoBoStreamClient.ProbeHostAsync("127.0.0.1", port, clientId, 1500, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    Check("V6 退出断口：关闭 BoBo远程 后端口立停、物理拒绝任何连控",
                        !probeAfterStop.Reachable,
                        "reachable=" + probeAfterStop.Reachable);

                    // 2f. V6.1 Complete Privacy Shield: native physical input blocker + DDC/CI restore serialization
                    bool blockOn = BoBoCoreInterop.SetPrivacyInputBlock(true, null);
                    bool activeMid = BoBoCoreInterop.IsPrivacyInputBlocked();
                    bool blockOff = BoBoCoreInterop.SetPrivacyInputBlock(false, null);
                    bool activeAfter = BoBoCoreInterop.IsPrivacyInputBlocked();
                    Check("V6.1 防窥物理键鼠锁：原生 WH_MOUSE_LL + WH_KEYBOARD_LL 启停闭环",
                        blockOn && activeMid && blockOff && !activeAfter,
                        "on=" + blockOn + " mid=" + activeMid + " off=" + blockOff + " after=" + activeAfter);

                    var sampleStates = new System.Collections.Generic.List<PrivacyDisplayDimmer.MonitorSavedState>();
                    var st0 = new PrivacyDisplayDimmer.MonitorSavedState { MonitorOrdinal = 0, Description = "Generic PnP Monitor" };
                    st0.VcpValues[0x10] = 75;
                    st0.VcpValues[0x12] = 50;
                    st0.VcpValues[0x16] = 20;
                    sampleStates.Add(st0);
                    string serializedRestore = PrivacyDisplayDimmer.SerializeRestoreRecords(sampleStates);
                    var parsedRestore = PrivacyDisplayDimmer.ParseRestoreRecords(serializedRestore);
                    bool restoreRoundTrip = parsedRestore.Count == 1
                        && parsedRestore[0].MonitorOrdinal == 0
                        && parsedRestore[0].VcpValues.ContainsKey(0x10) && parsedRestore[0].VcpValues[0x10] == 75
                        && parsedRestore[0].VcpValues.ContainsKey(0x12) && parsedRestore[0].VcpValues[0x12] == 50
                        && parsedRestore[0].VcpValues.ContainsKey(0x16) && parsedRestore[0].VcpValues[0x16] == 20
                        && PrivacyDisplayDimmer.HasLegacyColorVcpCodes(parsedRestore[0]);
                    Check("V6.1 防窥背光降亮：DDC/CI 崩溃自愈恢复表序列化往返与旧版 RGB 记录识别",
                        restoreRoundTrip,
                        serializedRestore.Trim());

                    bool vcpSafetyOk = PrivacyDisplayDimmer.IsSafeDimmingVcpCode(0x10)
                        && !PrivacyDisplayDimmer.IsSafeDimmingVcpCode(0x12)
                        && !PrivacyDisplayDimmer.IsSafeDimmingVcpCode(0x14)
                        && !PrivacyDisplayDimmer.IsSafeDimmingVcpCode(0x16)
                        && !PrivacyDisplayDimmer.IsSafeDimmingVcpCode(0x18)
                        && !PrivacyDisplayDimmer.IsSafeDimmingVcpCode(0x1A);
                    Check("V6.1 防窥硬件安全不变量：仅允许 VCP 0x10 背光亮度，严禁改对比度/色温/RGB 增益",
                        vcpSafetyOk,
                        "only0x10=" + vcpSafetyOk);

                    var defaultPriv = new PrivacyWallpaperConfig();
                    Check("V6.1 防窥默认策略：默认开启物理键鼠锁定，DDC/CI 背光降亮默认关闭以防显示器固件异常",
                        defaultPriv.BlockLocalInput && !defaultPriv.HardwareBlackout,
                        "blockInput=" + defaultPriv.BlockLocalInput + " hwBlackout=" + defaultPriv.HardwareBlackout);

                    string privacyPath = Path.Combine(testRoot, "privacy-wallpaper.txt");
                    File.WriteAllText(privacyPath, "lock=1" + Environment.NewLine, Encoding.UTF8);
                    var oldPrivacy = PrivacyWallpaperConfig.Load(privacyPath);
                    bool legacyDefault = oldPrivacy.LockOnLocalExit && !oldPrivacy.LockOnRemoteDisconnect;
                    oldPrivacy.LockOnRemoteDisconnect = true;
                    string privacyError;
                    bool enabledSaved = oldPrivacy.Save(out privacyError, privacyPath);
                    var savedPrivacy = PrivacyWallpaperConfig.Load(privacyPath);
                    bool enabledReloaded = savedPrivacy.LockOnLocalExit && savedPrivacy.LockOnRemoteDisconnect;
                    savedPrivacy.LockOnRemoteDisconnect = false;
                    bool disabledSaved = savedPrivacy.Save(out privacyError, privacyPath);
                    var restoredPrivacy = PrivacyWallpaperConfig.Load(privacyPath);
                    Check("被控端退出设置：旧配置默认回桌面，锁屏选项可独立保存与恢复",
                        legacyDefault && enabledSaved && enabledReloaded && disabledSaved &&
                        restoredPrivacy.LockOnLocalExit && !restoredPrivacy.LockOnRemoteDisconnect,
                        "legacy=" + legacyDefault + " lock=" + enabledReloaded + " error=" + (privacyError ?? ""));

                    // 2g. V6.2 Cross-Platform Golden Vectors (must match protocol/golden-vectors.json byte-for-byte)
                    string gvCode = BoBoIdentityStore.DeriveNineDigitCode("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
                    string gvProof = BoBoIdentityStore.ComputePinProof(
                        "7391",
                        "a1b2c3d4e5f60718293a4b5c6d7e8f90",
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                    byte[] gvVideoPayload = BoBoProtocol.BuildVideoPayload(2560, 1440, true, HexToBytes("000000016742001e0000000168ce388000000001658884"));
                    byte[] gvVideoFrame = SerializeSingleFrame(BoBoPacketType.VideoH264, 123456u, gvVideoPayload);
                    byte[] gvAudioPayload = BoBoProtocol.BuildAudioPayload(48000, 2, 8, HexToBytes("000000000000000000f7f7f7f7f77f7f"));
                    byte[] gvAudioFrame = SerializeSingleFrame(BoBoPacketType.AudioAdpcm, 0u, gvAudioPayload);
                    byte[] gvTouchPayload = BoBoProtocol.BuildInputPayload(BoBoProtocol.BuildTouchInput(1, 0, 16384, 32768));
                    byte[] gvNavPayload = BoBoProtocol.BuildInputPayload(BoBoProtocol.BuildMobileNavInput(BoBoProtocol.MobileNavBack));
                    byte[] gvFilePayload = BoBoProtocol.BuildFileChunkPayload("0123456789abcdef", 65536L, HexToBytes("deadbeef"), 4);
                    byte[] gvFileFrame = SerializeSingleFrame(BoBoPacketType.FileChunk, 0u, gvFilePayload);

                    bool gvMatch =
                        gvCode == "550555803" &&
                        gvProof == "1f553fefb979566b1c535d36c3fa2696735d3aecc3d6df44ae2c9af8b2cd539e" &&
                        BytesToHex(gvVideoFrame) == "021c00000040e20100000aa00501000000016742001e0000000168ce388000000001658884" &&
                        BytesToHex(gvAudioFrame) == "03170000000000000080bb0000020800000000000000000000f7f7f7f7f77f7f" &&
                        BytesToHex(gvTouchPayload) == "071000400080" &&
                        BytesToHex(gvNavPayload) == "080001000000" &&
                        BytesToHex(gvFileFrame) == "051c00000000000000303132333435363738396162636465660000010000000000deadbeef" &&
                        probe1.Platform == BoBoProtocol.PlatformWindows &&
                        probe1.HostControlMode == BoBoProtocol.ControlModeFull;
                    Check("V6.2 四端协议 Golden Vectors：设备码/PIN证明/音视频帧/触控与移动导航/文件块字节级一致",
                        gvMatch,
                        "code=" + gvCode + " plat=" + probe1.Platform + " ctrl=" + probe1.HostControlMode + " touch=" + BytesToHex(gvTouchPayload));

                    // 2h. V6.3 Encrypted UDP+FEC + Reliable Input Dedup + Adaptive Bitrate Controller unit verification
                    RunV63LowLatencyUnitSelfTests();
                }
            }
            catch (Exception ex)
            {
                Check("V6 端到端闭环自检异常", false, ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(receivedFileToClean))
                {
                    try { if (File.Exists(receivedFileToClean)) File.Delete(receivedFileToClean); } catch { }
                }
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static void RunV63LowLatencyUnitSelfTests()
        {
            // 1. Directional AES-128-CTR + HMAC-SHA256: round-trip, out-of-order, tamper, replay & reflection-DoS rejection
            byte[] key = BoBoUdpCrypto.GenerateRandomKey32();
            uint sid = BoBoUdpCrypto.GenerateRandomSessionId();
            using (var hostCrypto = new BoBoUdpCrypto(sid, key, true))
            using (var clientCrypto = new BoBoUdpCrypto(sid, key, false))
            {
                byte[] msg1 = Encoding.UTF8.GetBytes("bobo-v63-udp-packet-1");
                byte[] msg2 = Encoding.UTF8.GetBytes("bobo-v63-udp-packet-2-reordered");
                byte[] p1 = hostCrypto.Seal(BoBoUdpPacketType.Ping, msg1, 0, msg1.Length);
                byte[] p2 = hostCrypto.Seal(BoBoUdpPacketType.Ping, msg2, 0, msg2.Length);
                byte[] c1 = clientCrypto.Seal(BoBoUdpPacketType.Ping, msg1, 0, msg1.Length);

                // Verify distinct directional keystreams even at identical seq/type/plaintext
                bool distinctKeystream = false;
                for (int i = BoBoUdpCrypto.HeaderBytes; i < p1.Length - BoBoUdpCrypto.TagBytes; i++)
                {
                    if (p1[i] != c1[i]) { distinctKeystream = true; break; }
                }

                BoBoUdpPacketType tTmp;
                uint sTmp;
                byte[] oTmp;
                // Reflection attack: host packet bounced back to host MUST be rejected without advancing host's recv window!
                bool reflectRejected = !hostCrypto.TryOpen(p2, p2.Length, out tTmp, out sTmp, out oTmp);
                bool clientStillAccepted = hostCrypto.TryOpen(c1, c1.Length, out tTmp, out sTmp, out oTmp)
                    && Encoding.UTF8.GetString(oTmp) == "bobo-v63-udp-packet-1";

                // Deliver p2 BEFORE p1 (Wi-Fi out-of-order), then replay p1, then tamper p2
                bool ok2 = clientCrypto.TryOpen(p2, p2.Length, out tTmp, out sTmp, out oTmp)
                    && Encoding.UTF8.GetString(oTmp) == "bobo-v63-udp-packet-2-reordered";
                bool ok1 = clientCrypto.TryOpen(p1, p1.Length, out tTmp, out sTmp, out oTmp)
                    && Encoding.UTF8.GetString(oTmp) == "bobo-v63-udp-packet-1";
                bool rejectReplay = !clientCrypto.TryOpen(p1, p1.Length, out tTmp, out sTmp, out oTmp);
                p2[p2.Length - 1] ^= 0x5A;
                bool rejectTamper = !clientCrypto.TryOpen(p2, p2.Length, out tTmp, out sTmp, out oTmp);
                Check("V6.3 UDP 双向派生加密认证：乱序通过、防重放、防篡改、防反射 DoS 且密钥流方向隔离",
                    distinctKeystream && reflectRejected && clientStillAccepted && ok2 && ok1 && rejectReplay && rejectTamper,
                    "distinct=" + distinctKeystream + " reflRej=" + reflectRejected + " cOk=" + clientStillAccepted +
                    " ok2=" + ok2 + " ok1=" + ok1 + " replayRej=" + rejectReplay + " tamperRej=" + rejectTamper);
            }

            // 2. XOR FEC 0-RTT loss recovery + out-of-order frame jitter grace (F+1 completes before F's last shard)
            byte[] fakeIdr = new byte[5200]; // 5 shards (1140*4 + 640) -> 2 FEC groups on keyframe
            for (int i = 0; i < fakeIdr.Length; i++) fakeIdr[i] = (byte)((i * 37 + 13) & 0xFF);
            List<byte[]> shards = BoBoUdpFec.BuildVideoShards(1u, 100u, 1920, 1080, true, fakeIdr, 2.5);
            var asm = new BoBoUdpVideoAssembler();
            BoBoReassembledVideoFrame done = new BoBoReassembledVideoFrame();
            bool completed = false;
            bool needPli = false;
            for (int i = 0; i < shards.Count; i++)
            {
                // Drop data shard 1 (middle of group 0) AND data shard 4 (shorter final shard in last group!)
                bool isParity = (shards[i][12] & 2) != 0;
                int sIdx = BoBoUdpCrypto.ReadUInt16LE(shards[i], 13);
                if (!isParity && (sIdx == 1 || sIdx == 4)) continue;
                BoBoReassembledVideoFrame candidate;
                bool pli;
                if (asm.ProcessShard(shards[i], out candidate, out pli))
                {
                    done = candidate;
                    completed = true;
                }
                if (pli) needPli = true;
            }
            bool fecExact = completed && !needPli && done.H264Data != null && done.H264Data.Length == fakeIdr.Length && asm.FecRecoveredPackets >= 2;
            if (fecExact)
            {
                for (int i = 0; i < fakeIdr.Length; i++)
                {
                    if (done.H264Data[i] != fakeIdr[i]) { fecExact = false; break; }
                }
            }

            // Out-of-order test: deliver P-frame 3 completely BEFORE P-frame 2's last shard -> both [2, 3] delivered cleanly with 0 PLI!
            byte[] fakeP2 = new byte[2500];
            byte[] fakeP3 = new byte[1000];
            for (int i = 0; i < fakeP2.Length; i++) fakeP2[i] = (byte)(i & 0xFF);
            for (int i = 0; i < fakeP3.Length; i++) fakeP3[i] = (byte)((i + 99) & 0xFF);
            List<byte[]> s2 = BoBoUdpFec.BuildVideoShards(2u, 116u, 1920, 1080, false, fakeP2, 0.0);
            List<byte[]> s3 = BoBoUdpFec.BuildVideoShards(3u, 132u, 1920, 1080, false, fakeP3, 0.0);
            var batch = new List<BoBoReassembledVideoFrame>();
            bool oooPli = false;
            bool pTmp;
            // Feed only first shard of frame 2, then all shards of frame 3, then remaining data shards of frame 2
            asm.ProcessShardBatch(s2[0], batch, out pTmp); oooPli |= pTmp;
            for (int i = 0; i < s3.Count; i++) { asm.ProcessShardBatch(s3[i], batch, out pTmp); oooPli |= pTmp; }
            bool heldBeforeP2 = (batch.Count == 0 && !oooPli);
            for (int i = 1; i < s2.Count; i++) { asm.ProcessShardBatch(s2[i], batch, out pTmp); oooPli |= pTmp; }
            bool oooDeliveredInOrder = heldBeforeP2 && !oooPli && batch.Count == 2 && batch[0].FrameId == 2u && batch[1].FrameId == 3u;
            Check("V6.3 UDP XOR-FEC 零延迟纠错 + 跨帧乱序抖动宽限（不误丢帧/不触发 PLI 风暴）",
                fecExact && oooDeliveredInOrder,
                "fec=" + fecExact + " recovered=" + asm.FecRecoveredPackets + " oooOrder=" + oooDeliveredInOrder + " batch=" + batch.Count);

            byte[] burstIdr = new byte[11 * BoBoUdpFec.MaxShardPayloadBytes + 63];
            for (int i = 0; i < burstIdr.Length; i++) burstIdr[i] = (byte)(i * 37 + i / 256);
            var burstShards = BoBoUdpFec.BuildVideoShards(1u, 100u, 1280, 720, true, burstIdr, 1.0, true);
            var burstAssembler = new BoBoUdpVideoAssembler();
            var burstFrames = new List<BoBoReassembledVideoFrame>();
            bool burstPli = false;
            foreach (byte[] shard in burstShards)
            {
                int index = BoBoUdpCrypto.ReadUInt16LE(shard, 13);
                if ((shard[12] & 2) == 0 && index >= 8) continue; // Four consecutive data shards, including the short tail.
                bool pli;
                burstAssembler.ProcessShardBatch(shard, 85.0, burstFrames, out pli);
                burstPli |= pli;
            }
            Check("交错 XOR-FEC：连续丢四个数据分片时完整恢复关键帧",
                !burstPli && burstAssembler.FecRecoveredPackets == 4 && burstFrames.Count == 1 &&
                burstIdr.SequenceEqual(burstFrames[0].H264Data),
                "recovered=" + burstAssembler.FecRecoveredPackets + " pli=" + burstPli);

            var lossAssembler = new BoBoUdpVideoAssembler();
            lossAssembler.RecordReceivedUdpSeq(1);
            lossAssembler.RecordReceivedUdpSeq(10);
            long now = Stopwatch.GetTimestamp();
            lossAssembler.RefreshLossWindow(now + Stopwatch.Frequency * 3);
            double peakLoss = lossAssembler.LatestLossPct;
            for (int sec = 5; sec <= 17; sec += 2)
                lossAssembler.RefreshLossWindow(now + Stopwatch.Frequency * sec);
            Check("UDP 丢包统计：静止画面时历史丢包率逐步归零",
                peakLoss > 0.0 && lossAssembler.LatestLossPct == 0.0,
                "peak=" + peakLoss + " afterIdle=" + lossAssembler.LatestLossPct);

            // 3. Reliable UDP input piggyback, deduplication & non-duplicated relative mouse delta on redundant discrete copy
            var inTx = new BoBoUdpInputCodec();
            var inRx = new BoBoUdpInputCodec();
            byte[] relPlusClick = inTx.BuildPacket(false, new BoBoInputPacket(), true, 18, -12,
                new BoBoInputPacket { Kind = 3, Flags = 1, A = 1, B = 0 });
            byte[] redundantNoRel = inTx.BuildPacket(false, new BoBoInputPacket(), false, 0, 0, null);
            List<BoBoInputPacket> rFirst = inRx.ParseAndDeduplicate(relPlusClick);
            List<BoBoInputPacket> rSecond = inRx.ParseAndDeduplicate(redundantNoRel);
            bool inOk = rFirst.Count == 2 && rFirst[0].Kind == 2 && rFirst[0].A == 18 && rFirst[1].Kind == 3 && rSecond.Count == 0;
            Check("V6.3 UDP 输入冗余去重：离散按键幂等且冗余包不双倍放大相对鼠标位移",
                inOk,
                "first=" + rFirst.Count + " second=" + rSecond.Count);

            // 4. Adaptive Bitrate Controller (WAN conservative start + congestion backoff + FPS step-down)
            var abr = new BoBoAdaptiveBitrateController(true, 22000, 90, 45.0, false);
            var highRtt = new BoBoAdaptiveBitrateController(true, 22000, 90, 85.0, false);
            var lan = new BoBoAdaptiveBitrateController(true, 22000, 90, 4.0, true);
            int startBps = abr.CurrentBitrateKbps;
            int bps1, fps1;
            bool backedOff = abr.OnTelemetry(140.0, 5.5, out bps1, out fps1);
            abr.UpdateTarget(true, 36000, 144);
            bool retainedBackoff = abr.CurrentBitrateKbps == bps1 && abr.CurrentFps <= 60;
            abr.UpdateTarget(false, 22000, 90);
            bool manualTarget = abr.CurrentBitrateKbps == 22000 && abr.CurrentFps == 90;
            abr.UpdateTarget(true, 22000, 90);
            Check("自适应码率：跨网保守起流、拥塞回退与重新选档",
                startBps == 4500 && backedOff && bps1 < startBps && retainedBackoff && manualTarget &&
                abr.CurrentBitrateKbps <= 4500 && abr.CurrentFps <= 60 &&
                highRtt.CurrentBitrateKbps == 2500 && highRtt.CurrentFps == 30 &&
                lan.CurrentBitrateKbps == 22000 && lan.CurrentFps == 90,
                "start=" + startBps + " backoff=" + bps1 + "kbps/" + fps1 + "fps");

            Check("本地网段识别：私网与回环地址可用局域网档位，Tailscale 使用跨网档位",
                BoBoProtocol.IsPrivateLanIp("192.168.1.20") && BoBoProtocol.IsPrivateLanIp("10.0.0.1") &&
                BoBoProtocol.IsPrivateLanIp("::ffff:172.16.0.1") && BoBoProtocol.IsPrivateLanIp("127.0.0.1") &&
                !BoBoProtocol.IsPrivateLanIp("100.64.0.12") && !BoBoProtocol.IsPrivateLanIp("203.0.113.1"), "");

            var testedRoute = new BoBoConnectionRoute(System.Net.IPAddress.Parse("100.64.0.12"), null, 34);
            bool unknownRoute = testedRoute.Kind == BoBoRouteKind.TailnetUnknown;
            testedRoute.UpdateTailnetPath(Tailscale.ParsePongs("pong from desk (100.64.0.12) via 192.0.2.12:41641 in 34ms")[0]);
            bool directRoute = testedRoute.Kind == BoBoRouteKind.TailnetDirect && !testedRoute.PreferWeakNetworkProfile;
            testedRoute.UpdateTailnetPath(Tailscale.ParsePongs("pong from desk (100.64.0.12) via DERP(tok) in 72ms")[0]);
            bool relayRoute = testedRoute.Kind == BoBoRouteKind.TailnetDerp && testedRoute.PreferWeakNetworkProfile;
            testedRoute.UpdateTailnetPath(Tailscale.ParsePongs("pong from desk (100.64.0.12) via peer-relay(example) in 68ms")[0]);
            bool peerRelayRoute = testedRoute.Kind == BoBoRouteKind.TailnetPeerRelay && testedRoute.PreferWeakNetworkProfile;
            testedRoute.UpdateTailnetPath(null);
            Check("串流线路：P2P、DERP、节点中继和未确认路径分别显示",
                unknownRoute && directRoute && relayRoute && peerRelayRoute && testedRoute.Kind == BoBoRouteKind.TailnetUnknown, testedRoute.Label);

            var subnetAddress = System.Net.IPAddress.Parse("192.168.42.129");
            Check("同网段识别：按实际掩码区分局域网候选地址",
                BoBoConnectionRoute.HasSamePrefix(subnetAddress, System.Net.IPAddress.Parse("192.168.42.250"), 25) &&
                !BoBoConnectionRoute.HasSamePrefix(subnetAddress, System.Net.IPAddress.Parse("192.168.42.45"), 25) &&
                BoBoConnectionRoute.HasSamePrefix(System.Net.IPAddress.Parse("fd01:23::1"), System.Net.IPAddress.Parse("fd01:23::2"), 64), "");

            int videoWidth, videoHeight;
            BoBoHostServer.FitDesktopStream(1920, 1080, 2880, 1800, long.MaxValue, out videoWidth, out videoHeight);
            bool fitOriginal = videoWidth == 1920 && videoHeight == 1080;
            BoBoHostServer.FitDesktopStream(2880, 1800, 2880, 1800, 1920L * 1200, out videoWidth, out videoHeight);
            bool fitBudget = videoWidth == 1920 && videoHeight == 1200;
            BoBoHostServer.FitDesktopStream(2560, 1440, 2560, 1600, 1280L * 720, out videoWidth, out videoHeight);
            Check("超级屏：保留桌面比例，并遵守编码像素预算",
                fitOriginal && fitBudget && videoWidth == 1280 && videoHeight == 720, videoWidth + "×" + videoHeight);

            // 5. V6 All-In-One Auto-Updater Light ZIP selection check
            var fakeRel = AppUpdater.BuildReleaseFromTag("v6.9.0");
            var fakeHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            fakeHashes[AppUpdater.LightUpdateAssetName] = new string('a', 64);
            fakeHashes[AppUpdater.FullSetupAssetName] = new string('b', 64);
            var updRes = new UpdateCheckResult();
            AppUpdater.SelectBestPlan(fakeRel, AppUpdater.CurrentEnginesStamp, fakeHashes, AppUpdater.IsLocalAllInOneInstallIntact(), true, updRes);
            Check("V6 自动更新轻量包选择：全内置架构无 engines 目录也能命中 LightZip",
                updRes.PackageKind == UpdatePackageKind.LightZip,
                "intact=" + AppUpdater.IsLocalAllInOneInstallIntact() + " kind=" + updRes.PackageKind);
        }

        private static byte[] SerializeSingleFrame(BoBoPacketType type, uint flagsOrTs, byte[] payload)
        {
            using (var ms = new MemoryStream())
            {
                BoBoProtocol.WriteFrameAsync(ms, type, flagsOrTs, payload, 0, payload != null ? payload.Length : 0, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
                return ms.ToArray();
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] buf = new byte[hex.Length / 2];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return buf;
        }

        private static string BytesToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2"));
            return sb.ToString();
        }

        private static bool TryWriteReport(string reportPath)
        {
            try
            {
                File.WriteAllText(reportPath, _report.ToString(), new UTF8Encoding(false));
                Log("报告文件：" + reportPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Skip(string name)
        {
            if (_report == null) _report = new StringBuilder();
            _report.AppendLine("[跳过] " + name);
            try { Console.WriteLine("[跳过] " + name); } catch { }
        }

        private static bool IcmpUsable()
        {
            try
            {
                using (var ping = new System.Net.NetworkInformation.Ping())
                {
                    ping.Send(System.Net.IPAddress.Loopback, 500);
                    return true;
                }
            }
            catch (System.Net.NetworkInformation.PingException) { return false; }
            catch (System.Net.Sockets.SocketException) { return false; }
            catch (System.PlatformNotSupportedException) { return false; }
        }

        private static void Check(string name, bool passed, string detail)
        {
            if (passed)
            {
                Log("[通过] " + name);
            }
            else
            {
                _failures++;
                Log("[失败] " + name + "  " + (detail == null ? "" : detail));
            }
        }

        private static void Info(string text)
        {
            Log("[信息] " + text);
        }

        private static void Log(string text)
        {
            if (_report == null) _report = new StringBuilder();
            _report.AppendLine(text);
            try { Console.WriteLine(text); } catch { }
        }
    }
}
