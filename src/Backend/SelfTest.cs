using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            Check("精确串流：允许 4K60 (3840x2160@60) 通过几何校验",
                Engines.TryValidateStreamGeometry(3840, 2160, 60, 35000, out streamMessage)
                && Engines.BuildMoonlightStreamArgs("100.64.0.12", 3840, 2160, 60, 35000, false, false, false, true, true, false)
                    == "stream \"100.64.0.12\" \"Desktop\" --resolution 3840x2160 --fps 60 --bitrate 35000 --video-codec HEVC --no-performance-overlay --no-audio-on-host --display-mode fullscreen --no-vsync --no-frame-pacing",
                streamMessage ?? "4K60 参数拼装");
            Check("精确串流：允许 2K144 (2560x1440@144) 通过几何校验",
                Engines.TryValidateStreamGeometry(2560, 1440, 144, 36000, out streamMessage)
                && Engines.BuildMoonlightStreamArgs("100.64.0.12", 2560, 1440, 144, 36000, false, false, false, true, true, false)
                    == "stream \"100.64.0.12\" \"Desktop\" --resolution 2560x1440 --fps 144 --bitrate 36000 --video-codec HEVC --no-performance-overlay --no-audio-on-host --display-mode fullscreen --no-vsync --no-frame-pacing",
                streamMessage ?? "2K144 参数拼装");
            Check("精确串流：拒绝边界宽度",
                !Engines.StreamMoonlight("100.64.0.12", 4096, 2160, 60, 25000, false, false, true, false, out streamMessage)
                && streamMessage.IndexOf("宽度需大于", StringComparison.Ordinal) >= 0, streamMessage);
            Check("精确串流：拒绝边界高度",
                !Engines.StreamMoonlight("100.64.0.12", 1920, 2560, 60, 25000, false, false, true, false, out streamMessage)
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
            Check("Moonlight 配对参数：4 位 PIN",
                Engines.BuildMoonlightPairArgs("100.64.0.12", "0123") == "pair \"100.64.0.12\" --pin 0123",
                Engines.BuildMoonlightPairArgs("100.64.0.12", "0123"));
            Check("Moonlight 配对参数：拒绝非 4 位/非数字 PIN",
                Engines.BuildMoonlightPairArgs("100.64.0.12", "123") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12", "12345") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12", "12a4") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12", "１２３４") == null,
                "pin validation");
            Check("Moonlight 配对参数：拒绝主机命令注入",
                Engines.BuildMoonlightPairArgs("100.64.0.12\" --pin 0000") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12\" --pin 0000", "0123") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12\n--pin 0000", "0123") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12 & calc", "0123") == null,
                "host validation");
            Check("Moonlight 配对参数：拒绝 PIN 命令注入",
                Engines.BuildMoonlightPairArgs("100.64.0.12", "0123;calc") == null
                && Engines.BuildMoonlightPairArgs("100.64.0.12", "01 23") == null,
                "pin injection");
            Process badPairProcess = null;
            string badPairMessage = null;
            Check("Moonlight PIN：非法 host/PIN 不启动进程",
                !FoundationClient.StartPairWithPin("100.64.0.12\" --bad", "0123",
                    out badPairProcess, out badPairMessage)
                && badPairProcess == null
                && !FoundationClient.StartPairWithPin("100.64.0.12", "01 23",
                    out badPairProcess, out badPairMessage)
                && badPairProcess == null,
                badPairMessage);
            Check("Moonlight 配对参数：保留旧单参版本",
                Engines.BuildMoonlightPairArgs("desk.ts.net") == "pair \"desk.ts.net\"",
                Engines.BuildMoonlightPairArgs("desk.ts.net"));

            string certificate = "@ByteArray(-----BEGIN CERTIFICATE-----\\n"
                + new string('A', 160)
                + "\\n-----END CERTIFICATE-----)";
            string iniHosts =
                "[General]\nkey=value\n\n[hosts]\n"
                + "3\\hostname=Desk-Unordered\n3\\manualaddress=192.168.1.30\n"
                + "3\\srvcert=" + certificate + "\n"
                + "7\\hostname=Desk-A\n7\\manualaddress=192.168.1.20\n7\\remoteaddress=\n"
                + "7\\ipv6address=fd7a:115c:a1e0::20\n7\\srvcert=" + certificate + "\n"
                + "2\\hostname=Desk-B\n2\\manualaddress=localhost\n2\\remoteaddress=desk-b.ts.net\n"
                + "2\\srvcert=" + certificate + "\n";
            Check("Moonlight.ini：显示名称本身不认定为已配对",
                !FoundationClient.ContainsPairedHost(iniHosts, "Desk-A"), "hostname");
            Check("Moonlight.ini：同编号有效 srvcert + 手动地址才已配对",
                FoundationClient.ContainsPairedHost(iniHosts, "192.168.1.20"), "manualaddress");
            Check("Moonlight.ini：按远程地址判断已配对",
                FoundationClient.ContainsPairedHost(iniHosts, "desk-b.ts.net"), "remoteaddress");
            Check("Moonlight.ini：IPv6 方括号与展开形式等价",
                FoundationClient.ContainsPairedHost(iniHosts, "[fd7a:115c:a1e0::20]")
                && FoundationClient.ContainsPairedHost(iniHosts, "fd7a:115c:a1e0:0:0:0:0:20"),
                "ipv6address");
            Check("Moonlight.ini：乱序编号仍可读取",
                FoundationClient.ContainsPairedHost(iniHosts, "192.168.1.30"), "unordered index");
            Check("Moonlight.ini：只有 hostname/地址、没有 srvcert 不算配对",
                !FoundationClient.ContainsPairedHost(
                    "[hosts]\n1\\hostname=Desk-A\n1\\manualaddress=192.168.1.20\n",
                    "192.168.1.20"), "missing srvcert");
            Check("Moonlight.ini：空 srvcert 不算配对",
                !FoundationClient.ContainsPairedHost(
                    "[hosts]\n1\\manualaddress=192.168.1.20\n1\\srvcert=@ByteArray()\n",
                    "192.168.1.20"), "empty srvcert");
            Check("Moonlight.ini：残缺/过短 srvcert 不算有效证书候选",
                !FoundationClient.ContainsPairedHost(
                    "[hosts]\n1\\manualaddress=192.168.1.20\n1\\srvcert=@ByteArray(-----BEGIN CERTIFICATE-----\\nMIIB\\n-----END CERTIFICATE-----)\n",
                    "192.168.1.20")
                && !FoundationClient.ContainsPairedHost(
                    "[hosts]\n1\\manualaddress=192.168.1.20\n1\\srvcert=not-a-certificate\n",
                    "192.168.1.20"), "truncated srvcert");
            Check("Moonlight.ini：证书必须与地址同编号",
                !FoundationClient.ContainsPairedHost(
                    "[hosts]\n1\\manualaddress=192.168.1.20\n1\\srvcert=@ByteArray()\n"
                    + "2\\manualaddress=192.168.1.21\n2\\srvcert=" + certificate + "\n",
                    "192.168.1.20")
                && FoundationClient.ContainsPairedHost(
                    "[hosts]\n1\\manualaddress=192.168.1.20\n1\\srvcert=@ByteArray()\n"
                    + "2\\manualaddress=192.168.1.21\n2\\srvcert=" + certificate + "\n",
                    "192.168.1.21"), "same numbered srvcert");
            Check("Moonlight.ini：不跨节误判",
                !FoundationClient.ContainsPairedHost("[apps]\n1\\hostname=Desk-A\n1\\manualaddress=192.168.1.20\n",
                    "Desk-A"), "section boundary");
            Check("Moonlight.ini：未配对返回 false",
                !FoundationClient.ContainsPairedHost(iniHosts, "10.0.0.9"), "unknown host");
            Check("Moonlight.ini：显示名参数不能给未知地址背书",
                !FoundationClient.ContainsPairedHost(iniHosts, "10.0.0.9", "Desk-A"), "display name");
            bool pairedState;
            string pairedMessage;
            Check("Moonlight.ini：无 INI 是可读的未配对状态",
                FoundationClient.TryEvaluatePairedSettings(false, null, "100.64.0.12", "",
                    out pairedState, out pairedMessage) && !pairedState,
                pairedMessage);

            int parsedPort;
            string portError;
            Check("Sunshine 配置：默认 WebUI 端口",
                SunshinePairing.TryParseWebUiPort("address_family = both\n", out parsedPort, out portError)
                && parsedPort == 47990, portError);
            Check("Sunshine 配置：base port + 1",
                SunshinePairing.TryParseWebUiPort("port = 47989\n", out parsedPort, out portError)
                && parsedPort == 47990, portError);
            Check("Sunshine 配置：显式 web_ui_port 优先",
                SunshinePairing.TryParseWebUiPort("port = 50000\nweb_ui_port = 51234\n", out parsedPort, out portError)
                && parsedPort == 51234, portError);
            Check("Sunshine 配置：拒绝无效端口",
                !SunshinePairing.TryParseWebUiPort("port = abc\n", out parsedPort, out portError),
                portError);

            string pendingJson =
                "{\"generation\":7,\"pending\":true,\"requests\":[{\"id\":\"ABC-123\","
                + "\"device_name\":\"客厅电脑\",\"started_at\":\"2026-11-23T10:00:00Z\","
                + "\"expires_at\":\"2026-11-23T10:02:00Z\"},{\"device_name\":\"缺编号\"}]}";
            List<PendingPairRequest> pendingRequests;
            string pendingError;
            Check("Sunshine pending：解析请求且跳过缺编号项",
                SunshinePairing.TryParsePending(pendingJson, out pendingRequests, out pendingError)
                && pendingRequests.Count == 1
                && pendingRequests[0].Id == "ABC-123"
                && pendingRequests[0].DeviceName == "客厅电脑",
                pendingError);
            Check("Sunshine pending：空列表也是有效响应",
                SunshinePairing.TryParsePending("{\"pending\":false,\"requests\":[]}", out pendingRequests, out pendingError)
                && pendingRequests.Count == 0, pendingError);
            Check("Sunshine pending：畸形 JSON 拒绝",
                !SunshinePairing.TryParsePending("{\"requests\":[", out pendingRequests, out pendingError)
                && pendingRequests.Count == 0, pendingError);
            Check("Sunshine pending：缺 requests 数组不算有效响应",
                !SunshinePairing.TryParsePending("{\"pending\":false}", out pendingRequests, out pendingError),
                pendingError);
            Check("Sunshine pending：请求编号只允许 1–64 位 [A-Za-z0-9_-]",
                SunshinePairing.IsSafeRequestId("A")
                && SunshinePairing.IsSafeRequestId(new string('a', 64))
                && SunshinePairing.IsSafeRequestId("ABC-123_x")
                && !SunshinePairing.IsSafeRequestId("")
                && !SunshinePairing.IsSafeRequestId("ABC/123")
                && !SunshinePairing.IsSafeRequestId("ABC 123")
                && !SunshinePairing.IsSafeRequestId(new string('a', 65)),
                "request id");

            bool confirmed;
            string statusError;
            Check("Sunshine pin：status=true 才算成功",
                SunshinePairing.TryParseStatus("{\"status\":true}", out confirmed, out statusError) && confirmed,
                statusError);
            Check("Sunshine pin：status=false 明确失败",
                SunshinePairing.TryParseStatus("{\"status\":false,\"error\":\"invalid pin\"}", out confirmed, out statusError)
                && !confirmed, statusError);
            Check("Sunshine pin：缺少 status 不冒充成功",
                !SunshinePairing.TryParseStatus("{\"error\":\"bad\"}", out confirmed, out statusError),
                statusError);
            Check("Sunshine pin：空响应与畸形 JSON 不冒充成功",
                !SunshinePairing.TryParseStatus("", out confirmed, out statusError)
                && !SunshinePairing.TryParseStatus("{\"status\":true", out confirmed, out statusError)
                && !SunshinePairing.TryParseStatus("{\"status\":1}", out confirmed, out statusError)
                && !SunshinePairing.TryParseStatus("{\"status\":\"true\"}", out confirmed, out statusError)
                && !confirmed, statusError);
            Check("Sunshine pin：status=true 只表示 PIN 已提交，不当作配对完成",
                SunshinePairing.TryParseStatus("{\"status\":true}", out confirmed, out statusError)
                && confirmed, "UI 仍需以新 paired UUID 为完成证据");

            string confirmBody = SunshinePairing.BuildConfirmBody("0123", "我的、电脑", "ABC-123");
            Check("Sunshine confirm：JSON 字段与转义",
                confirmBody == "{\"pin\":\"0123\",\"name\":\"我的、电脑\",\"id\":\"ABC-123\"}",
                confirmBody);
            string confirmError;
            string unusedConfigDirectory = Path.Combine(Path.GetTempPath(), "DualRemote-selftest-unused-config");
            Check("Sunshine confirm：拒绝非 4 位 PIN",
                !SunshinePairing.TryConfirm(unusedConfigDirectory, "123", "ABC-123", "pc", out confirmError)
                && confirmError.IndexOf("4 位", StringComparison.Ordinal) >= 0,
                confirmError);
            Check("Sunshine confirm：非法请求编号不发 POST",
                !SunshinePairing.TryConfirm(unusedConfigDirectory, "0123", "ABC/123", "pc", out confirmError)
                && confirmError.IndexOf("编号", StringComparison.Ordinal) >= 0,
                confirmError);
            Check("Sunshine reject：非法请求编号不发 DELETE",
                !SunshinePairing.TryReject(unusedConfigDirectory, "A B/1", out confirmError),
                confirmError);
            Check("Sunshine reject：空响应与畸形 JSON 不算拒绝成功（源码纯函数）",
                !SunshinePairing.TryParseStatus("", out confirmed, out statusError)
                && !SunshinePairing.TryParseStatus("null", out confirmed, out statusError)
                && !confirmed, statusError);
            Check("Sunshine reject：请求编号需转义",
                SunshinePairing.BuildPendingPath("A B/1") == "/api/pairing/pending/A%20B%2F1",
                SunshinePairing.BuildPendingPath("A B/1"));
            List<PairedClientInfo> parsedClients;
            string clientError;
            string namedCerts =
                "{\"named_certs\":[{\"uuid\":\"7FC79E4E-E8D9-4533-8D6A-8F31EE2AF0A9\","
                + "\"name\":\"DualRemote-Test\",\"enabled\":true,\"streaming\":false},"
                + "{\"uuid\":\"bad\",\"name\":\"ignored\"}]}";
            Check("Sunshine clients：解析 paired UUID 并忽略畸形条目",
                SunshinePairing.TryParsePairedClients(namedCerts, out parsedClients, out clientError)
                && parsedClients.Count == 1
                && parsedClients[0].Id == "7FC79E4E-E8D9-4533-8D6A-8F31EE2AF0A9"
                && parsedClients[0].Name == "DualRemote-Test", clientError);
            string sameNameCerts =
                "{\"named_certs\":[{\"uuid\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"同名\"},"
                + "{\"uuid\":\"22222222-2222-2222-2222-222222222222\",\"name\":\"同名\"}]}";
            Check("Sunshine clients：同名设备仍按两个 UUID 保留，不靠显示名合并",
                SunshinePairing.TryParsePairedClients(sameNameCerts, out parsedClients, out clientError)
                && parsedClients.Count == 2
                && parsedClients[0].Id != parsedClients[1].Id
                && parsedClients[0].Name == parsedClients[1].Name, clientError);
            Check("Sunshine clients：畸形 JSON / 缺 named_certs 拒绝",
                !SunshinePairing.TryParsePairedClients("{\"named_certs\":[", out parsedClients, out clientError)
                && !SunshinePairing.TryParsePairedClients("{\"clients\":[]}", out parsedClients, out clientError),
                clientError);
            Check("Sunshine clients：UUID 只接受标准 36 位格式",
                SunshinePairing.IsSafeClientUuid("7FC79E4E-E8D9-4533-8D6A-8F31EE2AF0A9")
                && !SunshinePairing.IsSafeClientUuid("7FC79E4E-E8D9-4533-8D6A-8F31EE2AF0A")
                && !SunshinePairing.IsSafeClientUuid("7FC79E4E-E8D9-4533-8D6A-8F31EE2AF0AG"),
                "uuid");

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
            bool localRuntime = File.Exists(FoundationClient.EngineExe)
                && File.Exists(Path.Combine(FoundationClient.EngineDir, "portable.dat"));
            bool bundledRuntime = File.Exists(FoundationClient.BundledEngineExe)
                && File.Exists(Path.Combine(FoundationClient.BundledEngineDir, "portable.dat"));
            Check("V+ 可用性 = 稳定 runtime 或随包完整目录（干净解压也可用）",
                FoundationClient.IsAvailable == (localRuntime || bundledRuntime),
                "local=" + localRuntime + ", bundled=" + bundledRuntime);
            Check("V+ 版本指纹变化触发更新，相同指纹不重复复制",
                FoundationClient.ShouldRefreshRuntime("bundle-v2", "bundle-v1")
                && !FoundationClient.ShouldRefreshRuntime("bundle-v1", "bundle-v1")
                && !FoundationClient.ShouldRefreshRuntime(null, "bundle-v1"),
                "version fingerprint");

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
            Check("内置调优与显示脚本：已编译进主程序资源并可解析落地",
                EmbeddedTools.HasEmbeddedResource(EmbeddedTools.DisplayResourceName)
                && EmbeddedTools.HasEmbeddedResource(EmbeddedTools.OptimizeResourceName)
                && File.Exists(EmbeddedTools.ResolveDisplayTool())
                && File.Exists(EmbeddedTools.ResolveOptimizeTool()),
                EmbeddedTools.ResolveOptimizeTool());
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

            bool bundledAvailable = BundledHost.BundledAvailable;
            if (!string.IsNullOrEmpty(BundledHost.BundledSourceRoot))
            {
                string verifyErr;
                bool verifyOk = BundledHost.TryVerifyBundledExecutable(out verifyErr);
                Check("内置 Foundation Sunshine 完整性校验：版本、字节数与 SHA-256 一致",
                    bundledAvailable && verifyOk,
                    verifyOk ? BundledHost.BundleVersion : (verifyErr ?? "version-mismatch"));
            }
            else
            {
                Check("内置 Foundation Sunshine 缺失安全处理：无目录时返回不可用且不抛异常",
                    !bundledAvailable && BundledHost.BundledSourceExecutable == null,
                    "absent-safe");
            }
            int allocBasePort;
            int allocWebPort;
            string allocPortErr;
            bool allocPortOk = BundledHost.TryFindFreePortPair(out allocBasePort, out allocWebPort, out allocPortErr);
            Check("内置宿主端口分配：优先标准 47989 或回退可用端口组",
                allocPortOk && allocWebPort == allocBasePort + 1
                && (allocBasePort == BundledHost.StandardBasePort || (allocBasePort >= 48989 && allocBasePort <= 59989)),
                allocPortOk ? ("base=" + allocBasePort + " web=" + allocWebPort) : (allocPortErr ?? "fail"));

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

            // Sunshine 配置目录解析空安全（杜绝 Path.Combine(path1=null) 异常）
            bool resolveNoThrow = true;
            string explicitCfg = "";
            try
            {
                SunshinePairing.ResolveEffectiveConfigDirectory(null);
                explicitCfg = SunshinePairing.ResolveEffectiveConfigDirectory(@"C:\Program Files\Sunshine\config");
            }
            catch { resolveNoThrow = false; }
            Check("Sunshine 配对配置目录空安全解析",
                resolveNoThrow && string.Equals(explicitCfg, @"C:\Program Files\Sunshine\config", StringComparison.OrdinalIgnoreCase),
                explicitCfg ?? "null");

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
                && AppUpdater.NormalizeLightZipEntry("BoBo远程/tools/Set-SunshineDisplay.ps1") == @"tools\Set-SunshineDisplay.ps1",
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

            // 验证 Sunshine / Moonlight 远程桌面应用名称自动匹配（修复「找不到应用程序 Desktop」）
            Check("应用名匹配：中文 Foundation Sunshine 优先命中「桌面」并排除「大屏」/Steam/Xbox",
                Engines.SelectBestDesktopAppName(new[] { "Steam Big Picture 模式", "Xbox 游戏", "大屏", "桌面" }) == "桌面",
                "Chinese Foundation Sunshine");
            Check("应用名匹配：英文随包 Sunshine 命中「Desktop」",
                Engines.SelectBestDesktopAppName(new[] { "Big Screen", "Desktop", "Steam Big Picture" }) == "Desktop",
                "English Sunshine");
            Check("应用名匹配：用户自定义命名兜底跳过启动器选择首个桌面项",
                Engines.SelectBestDesktopAppName(new[] { "Big Screen", "Steam Big Picture", "远程办公主屏" }) == "远程办公主屏",
                "Custom desktop name");
            Check("应用名匹配：空列表安全回退 Desktop",
                Engines.SelectBestDesktopAppName(new string[0]) == "Desktop",
                "Empty fallback");

            Check("Qt INI 转义解码：支持 \\x684c\\x9762 与 @String(...) 还原「桌面」",
                FoundationClient.UnescapeQtIniString("\\x684c\\x9762") == "桌面"
                && FoundationClient.UnescapeQtIniString("\"@String(\\x684c\\x9762)\"") == "桌面"
                && FoundationClient.UnescapeQtIniString("Desktop") == "Desktop",
                "Qt QSettings INI unescape");

            string iniWithApps =
                "[General]\nsunshineabr=true\n\n[hosts]\n"
                + "1\\hostname=Office-PC\n1\\manualaddress=198.51.100.25\n"
                + "1\\srvcert=" + certificate + "\n"
                + "1\\apps\\1\\name=\\x684c\\x9762\n"
                + "1\\apps\\2\\name=Steam Big Picture \\x6a21\\x5f0f\n"
                + "1\\apps\\3\\name=\\x5927\\x5c4f\n"
                + "1\\apps\\size=3\n"
                + "2\\hostname=Unpaired-PC\n2\\manualaddress=198.51.100.99\n"
                + "2\\apps\\1\\name=ShouldIgnore\n";
            List<string> parsedIniApps = FoundationClient.ReadHostAppsFromIniText(iniWithApps, "198.51.100.25");
            Check("Moonlight.ini 应用列表解析：读取已配对主机的中文应用并自动选出「桌面」",
                parsedIniApps.Count == 3
                && parsedIniApps[0] == "桌面"
                && Engines.SelectBestDesktopAppName(parsedIniApps) == "桌面"
                && FoundationClient.ReadHostAppsFromIniText(iniWithApps, "198.51.100.99").Count == 0,
                "count=" + parsedIniApps.Count);

            byte[] utf8CliBytes = Encoding.UTF8.GetBytes("Loading app list...\r\n桌面\r\n大屏\r\n");
            List<string> parsedCliApps = Engines.ParseAppListOutput(Engines.DecodeCliOutputBytes(utf8CliBytes));
            Check("Moonlight list CLI 解析：过滤状态行并准确提取 UTF-8 中文应用名",
                parsedCliApps.Count == 2 && parsedCliApps[0] == "桌面" && parsedCliApps[1] == "大屏",
                "cliApps=" + string.Join(",", parsedCliApps.ToArray()));

            string customStreamArgs = Engines.BuildMoonlightStreamArgsWithApp(
                "198.51.100.25", "桌面\" --evil\n", 1920, 1080, 60, 10000, false, false, false, true, true, false);
            Check("Moonlight stream 参数：支持中文「桌面」并剥离引号/换行防参数注入",
                customStreamArgs == "stream \"198.51.100.25\" \"桌面 --evil\" --resolution 1920x1080 --fps 60 --bitrate 10000 --video-codec HEVC --no-performance-overlay --no-audio-on-host --display-mode fullscreen --no-vsync --no-frame-pacing",
                customStreamArgs);

            string desktopMouseQuitArgs = Engines.BuildMoonlightStreamArgsWithApp(
                "198.51.100.25", "桌面", 2560, 1440, 90, 22000, false, false, false, true, true, false, true, true);
            string relativeNoQuitArgs = Engines.BuildMoonlightStreamArgsWithApp(
                "198.51.100.25", "Desktop", 1920, 1080, 60, 10000, false, false, true, false, true, false, false, false);
            Check("Moonlight stream 参数：桌面远控默认无边框全屏、不劫持系统键、绝对鼠标与 --quit-after",
                desktopMouseQuitArgs.IndexOf(" --display-mode borderless", StringComparison.Ordinal) >= 0
                && desktopMouseQuitArgs.IndexOf(" --capture-system-keys never", StringComparison.Ordinal) >= 0
                && desktopMouseQuitArgs.EndsWith(" --absolute-mouse --quit-after", StringComparison.Ordinal)
                && relativeNoQuitArgs.IndexOf(" --display-mode windowed", StringComparison.Ordinal) >= 0
                && relativeNoQuitArgs.IndexOf(" --capture-system-keys fullscreen", StringComparison.Ordinal) >= 0
                && relativeNoQuitArgs.EndsWith(" --no-absolute-mouse --no-quit-after", StringComparison.Ordinal),
                desktopMouseQuitArgs);

            DateTime procStart = new DateTime(2026, 11, 24, 23, 30, 0);
            string activeLog = "[2026-11-24 23:20:00.000]: Info: CLIENT CONNECTED\r\n"
                + "[2026-11-24 23:43:48.388]: Info: New streaming session started [active sessions: 1]\r\n"
                + "[2026-11-24 23:43:48.721]: Info: CLIENT CONNECTED\r\n";
            string closedLog = activeLog
                + "[2026-11-24 23:52:06.435]: Info: CLIENT DISCONNECTED\r\n";
            string staleLog = "[2026-11-24 23:10:00.000]: Info: New streaming session started [active sessions: 1]\r\n"
                + "[2026-11-24 23:10:01.000]: Info: CLIENT CONNECTED\r\n";
            string twoThenOneLog = "[2026-11-24 23:43:48.388]: Info: New streaming session started [active sessions: 1]\r\n"
                + "[2026-11-24 23:44:00.000]: Info: New streaming session started [active sessions: 2]\r\n"
                + "[2026-11-24 23:45:00.000]: Info: CLIENT DISCONNECTED\r\n";
            Check("Sunshine 日志兜底被控检测：精准识别活跃、断开、多会话与重启前旧日志",
                HostNetworkInfo.ParseSunshineLogSessionActive(activeLog, procStart)
                && !HostNetworkInfo.ParseSunshineLogSessionActive(closedLog, procStart)
                && !HostNetworkInfo.ParseSunshineLogSessionActive(staleLog, procStart)
                && HostNetworkInfo.ParseSunshineLogSessionActive(twoThenOneLog, procStart),
                "log-session-parser");

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

            var primaryVddSnap = new HostSnapshot { SunshineInstalled = true };
            var primaryVddMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            primaryVddMap["output_name"] = "ZakoHDR";
            primaryVddMap["display_device_prep"] = "ensure_primary";
            primaryVddMap["fec_auto"] = "enabled";
            HostNetworkInfo.AnalyzeConfig(primaryVddSnap, primaryVddMap);
            Check("显示拓扑诊断：识别 ZakoHDR + ensure_primary 为主屏虚拟屏且无扩展告警",
                primaryVddSnap.DisplayModeLabel.IndexOf("主屏", StringComparison.Ordinal) >= 0
                && primaryVddSnap.AntiLossTuned,
                primaryVddSnap.DisplayModeLabel);

            var secondaryVddSnap = new HostSnapshot { SunshineInstalled = true };
            var secondaryVddMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            secondaryVddMap["output_name"] = "ZakoHDR";
            secondaryVddMap["display_device_prep"] = "ensure_secondary";
            secondaryVddMap["fec_auto"] = "enabled";
            HostNetworkInfo.AnalyzeConfig(secondaryVddSnap, secondaryVddMap);
            Check("显示拓扑诊断：检出旧版 ensure_secondary 副屏扩展并给出修复建议",
                secondaryVddSnap.DisplayModeLabel.IndexOf("副屏扩展", StringComparison.Ordinal) >= 0
                && !secondaryVddSnap.AntiLossTuned
                && secondaryVddSnap.Recommendations.Count >= 1,
                secondaryVddSnap.DisplayModeLabel);

            int parsedSessions;
            Check("Sunshine 活跃会话解析：支持 total_sessions、sessions 数组并拒绝畸形 JSON",
                SunshinePairing.TryParseActiveSessionCount("{\"sessions\":[],\"total_sessions\":0}", out parsedSessions) && parsedSessions == 0
                && SunshinePairing.TryParseActiveSessionCount("{\"sessions\":[{\"id\":1},{\"id\":2}],\"total_sessions\":2}", out parsedSessions) && parsedSessions == 2
                && SunshinePairing.TryParseActiveSessionCount("{\"sessions\":[{\"id\":1}]}", out parsedSessions) && parsedSessions == 1
                && !SunshinePairing.TryParseActiveSessionCount("{bad", out parsedSessions),
                "sessions=" + parsedSessions);

            string sampleBundledSun = Path.Combine(BundledHost.LocalRoot, BundledHost.ExecutableName);
            string sampleInstalledSun = Path.Combine(BundledHost.DefaultInstalledRoot, BundledHost.ExecutableName);
            Check("退出清理进程边界：精准命中 BoBo 托管路径，绝不误伤系统安装版",
                BundledHost.IsManagedProcessPath(sampleBundledSun, BundledHost.LocalRoot)
                && BundledHost.IsManagedProcessPath(FoundationClient.EngineExe, FoundationClient.EngineExe)
                && !BundledHost.IsManagedProcessPath(sampleInstalledSun, BundledHost.LocalRoot)
                && !BundledHost.IsManagedProcessPath(@"C:\Program Files\RustDesk\rustdesk.exe", Engines.RustDeskExe),
                sampleBundledSun);

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

            // V5 原生一体化 GameStream 协议、X.509 身份生成与 IPC 编解码自检
            string nativeTempDir = Path.Combine(Path.GetTempPath(), "bobo-native-id-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.Security.Cryptography.X509Certificates.X509Certificate2 idCert;
                string idPem, idUid, idErr;
                bool idCreated = GameStreamClient.EnsureIdentityInDir(nativeTempDir, false, out idCert, out idPem, out idUid, out idErr);
                Check("V5 内置客户端生成 RSA-2048 证书与 PFX", idCreated && idCert != null && idCert.HasPrivateKey && !string.IsNullOrEmpty(idPem) && idUid.Length == 16, idErr);
                if (idCreated && idCert != null)
                {
                    byte[] sigBytes = GameStreamClient.ExtractX509SignatureBytes(idCert.RawData);
                    Check("V5 X.509 DER 证书签名提取（256 字节）", sigBytes != null && sigBytes.Length == 256,
                        sigBytes == null ? "null" : sigBytes.Length.ToString());
                    idCert.Dispose();

                    // 二次加载持久化 PFX 验证
                    System.Security.Cryptography.X509Certificates.X509Certificate2 reloaded;
                    string rPem, rUid, rErr;
                    bool reloadOk = GameStreamClient.EnsureIdentityInDir(nativeTempDir, false, out reloaded, out rPem, out rUid, out rErr);
                    Check("V5 内置客户端重新加载持久化 PFX", reloadOk && reloaded != null && reloaded.HasPrivateKey && rUid == idUid, rErr);
                    if (reloaded != null) reloaded.Dispose();
                }

                byte[] testSalt = GameStreamClient.GenerateRandomBytes(16);
                byte[] testAesKey = GameStreamClient.DerivePairingAesKey(testSalt, "4829", true);
                byte[] testChallenge = GameStreamClient.GenerateRandomBytes(32);
                byte[] testEnc = GameStreamClient.AesEcbEncrypt(testAesKey, testChallenge);
                byte[] testDec = GameStreamClient.AesEcbDecrypt(testAesKey, testEnc);
                bool aesMatch = testEnc.Length == 32 && testDec.Length == 32;
                for (int i = 0; aesMatch && i < 32; i++) if (testChallenge[i] != testDec[i]) aesMatch = false;
                Check("V5 配对 AES-128-ECB + SHA-256 密钥派生回环", aesMatch, "");

                GameStreamServerInfo parsedInfo;
                string infoErr;
                string sampleServerXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><root status_code=\"200\">"
                    + "<hostname>BoBo-Host</hostname><appversion>7.1.431.-1</appversion><GfeVersion>3.23.0.74</GfeVersion>"
                    + "<HttpsPort>47984</HttpsPort><ExternalPort>47989</ExternalPort><ServerCodecModeSupport>769</ServerCodecModeSupport>"
                    + "<SupportedDisplayMode><DisplayMode><Width>2880</Width><Height>1800</Height><RefreshRate>120</RefreshRate></DisplayMode></SupportedDisplayMode>"
                    + "<PairStatus>1</PairStatus><currentgame>0</currentgame></root>";
                bool infoOk = GameStreamClient.TryParseServerInfoXml(sampleServerXml, "127.0.0.1", 47989, out parsedInfo, out infoErr);
                Check("V5 解析 Sunshine /serverinfo XML（含主机原生分辨率）", infoOk && parsedInfo != null && parsedInfo.Paired && parsedInfo.HttpsPort == 47984 && parsedInfo.ServerCodecModeSupport == 769 && parsedInfo.NativeWidth == 2880 && parsedInfo.NativeHeight == 1800, infoErr);

                List<GameStreamAppInfo> parsedApps = GameStreamClient.ParseAppListXml(
                    "<root status_code=\"200\"><App><ID>881448767</ID><AppTitle>桌面</AppTitle></App><App><ID>123</ID><AppTitle>Steam</AppTitle></App></root>");
                Check("V5 解析 Sunshine /applist 中文桌面应用", parsedApps.Count == 2 && parsedApps[0].Id == 881448767 && parsedApps[0].Title == "桌面", "");

                string keysCmd = NativeStreamCore.BuildSendKeysCommand(new int[] { 17, 16, 27 });
                string textCmd = NativeStreamCore.BuildSendTextCommand("hello \"bobo\"\n");
                string volCmd = NativeStreamCore.BuildVolumeCommand(65);
                string scaleCmd = NativeStreamCore.BuildScaleModeCommand(false);
                string viewCmd = NativeStreamCore.BuildViewOnlyCommand(true);
                string sysKeyCmd = NativeStreamCore.BuildSysKeysCommand(true);
                string hudCmd = NativeStreamCore.BuildHudCommand(true);
                NativePlayerEvent statsEv = NativeStreamCore.TryParseEventLine(
                    "{\"event\":\"stats\",\"fps\":89.8,\"decodeMs\":1.4,\"renderMs\":0.6,\"rttMs\":3.0,\"bitrateKbps\":22000,\"width\":2560,\"height\":1440,\"codec\":\"H264\",\"decoder\":\"D3D11VA\"}");
                NativePlayerEvent stageEv = NativeStreamCore.TryParseEventLine(
                    "{\"event\":\"stage\",\"stage\":\"RTSP handshake\",\"status\":\"starting\",\"errorCode\":0}");
                NativePlayerEvent hwndEv = NativeStreamCore.TryParseEventLine("{\"event\":\"viewport_hwnd\",\"hwnd\":123456}");
                Check("V5 原生播放核 IPC 编解码（含音量/1:1/仅查看/系统键）",
                    keysCmd == "{\"cmd\":\"send_keys\",\"vks\":[17,16,27]}"
                    && textCmd.IndexOf("\\\"bobo\\\"\\n", StringComparison.Ordinal) >= 0
                    && volCmd == "{\"cmd\":\"set_volume\",\"volume\":65}"
                    && scaleCmd == "{\"cmd\":\"set_scale_mode\",\"fit\":false}"
                    && viewCmd == "{\"cmd\":\"set_view_only\",\"enabled\":true}"
                    && sysKeyCmd == "{\"cmd\":\"set_sys_keys\",\"enabled\":true}"
                    && hudCmd == "{\"cmd\":\"set_hud\",\"enabled\":true}"
                    && statsEv != null && statsEv.Stats != null && statsEv.Stats.Width == 2560
                    && stageEv != null && stageEv.Name == "RTSP handshake" && stageEv.State == "starting"
                    && hwndEv != null && hwndEv.ViewportHwnd == 123456UL,
                    keysCmd + " | " + volCmd);
            }
            finally
            {
                try { if (Directory.Exists(nativeTempDir)) Directory.Delete(nativeTempDir, true); } catch { }
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
