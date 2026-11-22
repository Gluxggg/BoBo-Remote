using System;
using System.Collections.Generic;
using System.IO;
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

            string rustDeskArgs = Engines.BuildRustDeskConnectArgs("123456789");
            Check("RustDesk 参数：--connect + 归一化 ID", rustDeskArgs == "--connect 123456789", rustDeskArgs);
            string transferArgs = Engines.BuildRustDeskFileTransferArgs("123456789");
            Check("RustDesk 参数：--file-transfer + 归一化 ID",
                transferArgs == "--file-transfer 123456789", transferArgs);
            string badIdMessage;
            Check("设备码校验：连接与文件传输共用且拒绝非法 ID",
                !Engines.ConnectRustDesk("abc/def", out badIdMessage)
                && !Engines.TransferRustDesk("--help", out badIdMessage), badIdMessage);
            string moonArgs = Engines.BuildMoonlightStreamArgs("100.64.0.12", 1080, 15000);
            string expectedMoonArgs = "stream \"100.64.0.12\" \"Desktop\" --1080 --fps 60 --bitrate 15000 --video-codec H.264";
            Check("Moonlight 参数：1080p60/15Mbps/H.264", moonArgs == expectedMoonArgs, moonArgs);
            string moon720 = Engines.BuildMoonlightStreamArgs("100.64.0.12", 720, 8000);
            Check("Moonlight 参数：720 映射 --720 且保留 60fps",
                moon720 == "stream \"100.64.0.12\" \"Desktop\" --720 --fps 60 --bitrate 8000 --video-codec H.264",
                moon720);
            string moon1440 = Engines.BuildMoonlightStreamArgs("desk.ts.net", 1440, 25000);
            Check("Moonlight 参数：1440 映射 --1440",
                moon1440 == "stream \"desk.ts.net\" \"Desktop\" --1440 --fps 60 --bitrate 25000 --video-codec H.264",
                moon1440);
            Check("Moonlight 参数：默认重载等于 1080p60/15Mbps",
                Engines.BuildMoonlightStreamArgs("100.64.0.12") == expectedMoonArgs,
                Engines.BuildMoonlightStreamArgs("100.64.0.12"));
            string explicitArgs = Engines.BuildMoonlightStreamArgs("100.64.0.12", 1080, 15000, true, false, true, false);
            Check("Moonlight 显式参数：浮层开 / 主机静音 / 窗口 / H.264",
                explicitArgs == "stream \"100.64.0.12\" \"Desktop\" --1080 --fps 60 --bitrate 15000"
                              + " --video-codec H.264 --performance-overlay --no-audio-on-host --display-mode windowed",
                explicitArgs);
            string hevcArgs = Engines.BuildMoonlightStreamArgs("desk.ts.net", 1440, 25000, false, true, false, true);
            Check("Moonlight 显式参数：HEVC / 主机播放 / 全屏",
                hevcArgs == "stream \"desk.ts.net\" \"Desktop\" --1440 --fps 60 --bitrate 25000"
                          + " --video-codec HEVC --no-performance-overlay --audio-on-host --display-mode fullscreen",
                hevcArgs);
            string streamMessage;
            Check("串流参数：拒绝非法分辨率",
                !Engines.StreamMoonlight("100.64.0.12", 999, 15000, out streamMessage)
                && streamMessage.IndexOf("720 / 1080 / 1440", StringComparison.Ordinal) >= 0, streamMessage);
            Check("串流参数：拒绝超范围码率",
                !Engines.StreamMoonlight("100.64.0.12", 1080, 999999, out streamMessage)
                && streamMessage.IndexOf("码率", StringComparison.Ordinal) >= 0, streamMessage);
            string preciseArgs = Engines.BuildMoonlightStreamArgs("100.64.0.12", 2560, 1440, 90, 25000, true, false, true, true);
            Check("Moonlight 精确参数：2560x1440@90 / HEVC / 窗口 / 浮层",
                preciseArgs == "stream \"100.64.0.12\" \"Desktop\" --resolution 2560x1440 --fps 90 --bitrate 25000"
                            + " --video-codec HEVC --performance-overlay --no-audio-on-host --display-mode windowed",
                preciseArgs);
            string tallArgs = Engines.BuildMoonlightStreamArgs("desk.ts.net", 2560, 1600, 90, 20000, false, true, false, false);
            Check("Moonlight 精确参数：2560x1600@90 / H.264 / 全屏 / 主机播放",
                tallArgs == "stream \"desk.ts.net\" \"Desktop\" --resolution 2560x1600 --fps 90 --bitrate 20000"
                          + " --video-codec H.264 --no-performance-overlay --audio-on-host --display-mode fullscreen",
                tallArgs);
            string lowBitrateArgs = Engines.BuildMoonlightStreamArgs("100.64.0.12", 1920, 1200, 60, 3000, true, false, true, false);
            Check("Moonlight 精确参数：1920x1200@60 / 低码率 3000",
                lowBitrateArgs == "stream \"100.64.0.12\" \"Desktop\" --resolution 1920x1200 --fps 60 --bitrate 3000"
                               + " --video-codec H.264 --performance-overlay --no-audio-on-host --display-mode windowed",
                lowBitrateArgs);
            Check("精确串流：拒绝奇数宽",
                !Engines.StreamMoonlight("100.64.0.12", 2561, 1440, 90, 25000, true, false, true, false, out streamMessage)
                && streamMessage.IndexOf("偶数", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝奇数高",
                !Engines.StreamMoonlight("100.64.0.12", 1920, 1201, 60, 8000, false, false, true, false, out streamMessage)
                && streamMessage.IndexOf("偶数", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝边界宽度",
                !Engines.StreamMoonlight("100.64.0.12", 3840, 2160, 60, 25000, false, false, true, false, out streamMessage)
                && streamMessage.IndexOf("宽度需大于", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝边界高度",
                !Engines.StreamMoonlight("100.64.0.12", 1920, 2160, 60, 25000, false, false, true, false, out streamMessage)
                && streamMessage.IndexOf("高度需大于", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝边界帧率",
                !Engines.StreamMoonlight("100.64.0.12", 1920, 1080, 240, 15000, false, false, true, false, out streamMessage)
                && streamMessage.IndexOf("帧率", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝边界码率",
                !Engines.StreamMoonlight("100.64.0.12", 1920, 1080, 60, 500, true, false, true, false, out streamMessage)
                && streamMessage.IndexOf("码率", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝主机名注入引号",
                Engines.NormalizeHost("bad\"host", out streamMessage) == null
                && !Engines.StreamMoonlight("bad\"host", 1920, 1080, 60, 15000, false, false, true, false, out streamMessage),
                streamMessage);
            Check("Moonlight 配对参数：pair + 主机", Engines.BuildMoonlightPairArgs("100.64.0.12") == "pair \"100.64.0.12\"",
                Engines.BuildMoonlightPairArgs("100.64.0.12"));

            // 内部范围校验不启动进程：借 StreamMoonlight 的参数校验路径不现实（需要组件），
            // 这里只验证公开常量的边界与映射一致性。
            Check("Moonlight 码率边界在官方支持范围内",
                Engines.MinimalStreamBitrateKbps >= 500 && Engines.MaximalStreamBitrateKbps <= 500000,
                Engines.MinimalStreamBitrateKbps + "–" + Engines.MaximalStreamBitrateKbps);

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

            // 引擎选择矩阵：GUI、配对、串流共用同一纯函数，这里只做无副作用断言。
            Check("引擎选择：标准存在默认 Official",
                FoundationClient.SelectEngine(true, true, false) == StreamEngine.Official
                && FoundationClient.SelectEngine(true, false, false) == StreamEngine.Official,
                FoundationClient.SelectEngine(true, true, false).ToString());
            Check("引擎选择：明确优先且 V+ 存在时 Enhanced",
                FoundationClient.SelectEngine(true, true, true) == StreamEngine.Enhanced
                && FoundationClient.SelectEngine(false, true, true) == StreamEngine.Enhanced,
                FoundationClient.SelectEngine(true, true, true).ToString());
            Check("引擎选择：只有 V+ 时直接 Enhanced",
                FoundationClient.SelectEngine(false, true, false) == StreamEngine.Enhanced,
                FoundationClient.SelectEngine(false, true, false).ToString());
            Check("引擎选择：两者皆无时 Unavailable",
                FoundationClient.SelectEngine(false, false, false) == StreamEngine.Unavailable
                && FoundationClient.SelectEngine(false, false, true) == StreamEngine.Unavailable,
                FoundationClient.SelectEngine(false, false, false).ToString());
            Check("引擎选择：实际文件重载与文件状态一致",
                FoundationClient.SelectEngine(false)
                    == FoundationClient.SelectEngine(Engines.MoonlightPresent, FoundationClient.IsAvailable, false)
                && FoundationClient.SelectEngine(true)
                    == FoundationClient.SelectEngine(Engines.MoonlightPresent, FoundationClient.IsAvailable, true),
                FoundationClient.SelectEngine(false).ToString());
            bool vplusExe = File.Exists(FoundationClient.EngineExe);
            bool vplusMarker = File.Exists(Path.Combine(FoundationClient.EngineDir, "portable.dat"));
            Check("V+ 可用性 = exe 且同目录 portable.dat",
                FoundationClient.IsAvailable == (vplusExe && vplusMarker),
                FoundationClient.IsAvailable ? "available" : "unavailable");

            // 低延迟 CLI 参数（VSync / Frame Pacing）
            string llArgs = Engines.BuildMoonlightStreamArgs("100.64.0.12", 1920, 1080, 60, 10000,
                false, false, false, true, true, false);
            Check("低延迟参数：--no-vsync --no-frame-pacing + HEVC",
                llArgs.IndexOf("--video-codec HEVC", StringComparison.Ordinal) >= 0
                && llArgs.IndexOf("--no-vsync", StringComparison.Ordinal) >= 0
                && llArgs.IndexOf("--no-frame-pacing", StringComparison.Ordinal) >= 0,
                llArgs);
            string smoothArgs = Engines.BuildMoonlightStreamArgs("100.64.0.12", 2560, 1600, 90, 24000,
                true, true, true, true, false, true);
            Check("平滑参数：--vsync --frame-pacing + 16:10 90fps",
                smoothArgs.IndexOf("--resolution 2560x1600 --fps 90", StringComparison.Ordinal) >= 0
                && smoothArgs.IndexOf("--vsync", StringComparison.Ordinal) >= 0
                && smoothArgs.IndexOf("--frame-pacing", StringComparison.Ordinal) >= 0,
                smoothArgs);

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

            var badSnap = new HostSnapshot { SunshineInstalled = true };
            var badMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            badMap["encoder"] = "amdvce";
            badMap["amd_rc"] = "vbr_latency";
            HostNetworkInfo.AnalyzeConfig(badSnap, badMap);
            Check("抗丢包诊断：检出 AMD vbr_latency 与缺失 HRD/LTR/Auto-FEC",
                !badSnap.AntiLossTuned && badSnap.Recommendations.Count >= 4,
                badSnap.TuningSummary);
            var goodSnap = new HostSnapshot { SunshineInstalled = true };
            var goodMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            goodMap["encoder"] = "amdvce";
            goodMap["amd_rc"] = "cbr";
            goodMap["amd_enforce_hrd"] = "enabled";
            goodMap["amd_ltr_frames"] = "2";
            goodMap["fec_auto"] = "enabled";
            HostNetworkInfo.AnalyzeConfig(goodSnap, goodMap);
            Check("抗丢包诊断：目标配置只称配置项已就绪，不宣称实测通过",
                goodSnap.AntiLossTuned && goodSnap.Recommendations.Count == 0
                && goodSnap.TuningSummary.IndexOf("配置项已就绪", StringComparison.Ordinal) >= 0
                && goodSnap.TuningSummary.IndexOf("尚未实测", StringComparison.Ordinal) >= 0
                && goodSnap.TuningSummary.IndexOf("已调优", StringComparison.Ordinal) < 0
                && goodSnap.TuningSummary.IndexOf("发包平滑", StringComparison.Ordinal) < 0
                && goodSnap.TuningSummary.IndexOf("1270", StringComparison.Ordinal) < 0,
                goodSnap.TuningSummary);
            var autoSnap = new HostSnapshot { SunshineInstalled = true };
            var autoMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            autoMap["encoder"] = "";
            autoMap["fec_auto"] = "enabled";
            HostNetworkInfo.AnalyzeConfig(autoSnap, autoMap);
            Check("非 AMD/自动编码器只核验 Auto-FEC，不宣称 CBR+HRD+LTR",
                autoSnap.AntiLossTuned && autoSnap.Recommendations.Count == 0
                && autoSnap.TuningSummary.IndexOf("Auto-FEC", StringComparison.Ordinal) >= 0
                && autoSnap.TuningSummary.IndexOf("CBR", StringComparison.Ordinal) < 0
                && autoSnap.TuningSummary.IndexOf("HRD", StringComparison.Ordinal) < 0
                && autoSnap.TuningSummary.IndexOf("LTR", StringComparison.Ordinal) < 0,
                autoSnap.TuningSummary);
            autoMap["fec_auto"] = "disabled";
            HostNetworkInfo.AnalyzeConfig(autoSnap, autoMap);
            Check("非 AMD/自动编码器缺 Auto-FEC 仍报待修",
                !autoSnap.AntiLossTuned && autoSnap.Recommendations.Count == 1,
                autoSnap.TuningSummary);

            Log("");
            Info("RustDesk 组件：" + (Engines.RustDeskPresent ? "已找到" : "未找到") + " " + Engines.RustDeskExe);
            Info("Moonlight 组件：" + (Engines.MoonlightPresent ? "已找到" : "未找到") + " " + Engines.MoonlightExe);
            Info("Tailscale CLI：" + (Engines.TailscalePresent ? "已找到" : "未找到") + " " + Tailscale.ExePath);
            Info("最近目标目录：" + RecentStore.DirPath);
            if (!Engines.RustDeskPresent)
            {
                string launchMessage;
                Check("无 RustDesk 组件时“打开本机远控”返回可操作错误",
                    !Engines.LaunchRustDesk(out launchMessage)
                    && launchMessage != null
                    && launchMessage.IndexOf("RustDesk", StringComparison.Ordinal) >= 0,
                    launchMessage);
            }

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
