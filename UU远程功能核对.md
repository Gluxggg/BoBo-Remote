# UU 远程功能核对与双端远程实现

核对日期为 2026 年 11 月 22 日（初版为 11 月 20 日，本轮按官方页面与源码现状更新）。以网易公开的功能页、帮助中心和下载页为范围。实测参照的 UU 客户端为 Windows 4.48.0（`<UU安装目录>\GameViewer`，`GameViewer.exe` 与 `GameViewerServer.exe` 均为 4.48.0.5446，服务包装程序 `GameViewerService.exe` 为 4.46.0.4716）；官网下载页列出 Windows/macOS/Android 4.48.2，更新于 2026-11-20。本机仅是小版本落后，包装程序版本较低不判 bug，没有出现故障迹象。官方页面在 2026-11-19 公告「UU 远程 VIP 服务即将上线，基础远控保持免费」，预计 2026-12-01 上线；这是未来计划，不代表当前已收费，具体权益以账号与客户端版本显示为准。以下凡标「官方宣称」的都是产品页说法，不是本机实测延迟基准。 [官方功能页](https://uuyc.163.com/features/work/) · [官方常见问题](https://uuyc.163.com/help/questions/) · [官方下载](https://uuyc.163.com/download/)

## 两台 Windows 的核心流程

双端远程当前为四页界面：我的设备、远程协助、防窥与显示、性能与设置。远程协助页左侧是 RustDesk 通用远控与文件传输，右侧是 `Moonlight V+ 6.5.6` 低延迟串流，并提供「外网抗丢包 / 6M 弱网 / 局域网 2K90」快捷档。

| 功能 | UU 远程公开行为 | 双端远程的对应实现 |
| --- | --- | --- |
| 我的设备 | 同一账号登录后看到设备，在线设备可进入桌面，能管理或删除设备 | 在授权的 Tailscale 网络里查看 Windows 设备。常用主机保留在最近连接和本机收藏。首次授权后的 Moonlight 桌面可直接从连接页启动。 [电脑控制电脑](https://uuyc.163.com/help/20241203/40220_1197651.html) |
| 临时协助 | 被协助方可免登录，按被控端设置走验证码、现场同意或双重验证，可分享限时邀请码 | 通过 RustDesk 的设备码发起，原生窗口按被控端设置验证。双端远程本身只保存地址和设备码。 [远程协助](https://uuyc.163.com/help/20241204/40220_1197664.html) |
| 收藏与历史 | 设备可备注、收藏、重连，支持按账号同步，部分认证信息留在本机 | 本机收藏及最近记录可复用，历史可清除。每一台电脑独立保存。 [收藏设备与最近连接](https://uuyc.163.com/help/20241204/40220_1197664.html) |
| 桌面、键鼠与声音 | 会话内显示桌面，接收键鼠或触控输入，控制中心调节分辨率、画质、声音和窗口 | 通用桌面采用 RustDesk。低延迟模式采用兼容 Sunshine 的主机加 `Moonlight V+`，当前代码内置六档：1080p60/10 Mbps、1920×1200@60/10 Mbps、1440p60/14 Mbps、720p60/6 Mbps、2560×1440@90/22 Mbps、2560×1600@90/24 Mbps（`src/MainForm.cs` 的 `QualityOption`）。已知 AMD 主机能硬编 H.264 与 HEVC；4:4:4、4K、144 fps 尚未在本机两机验证。 [控制中心](https://uuyc.163.com/help/20241203/40220_1197651.html) · [Moonlight 设置指南](https://github.com/moonlight-stream/moonlight-docs/wiki/Setup-Guide) |
| 文件传输 | 官方宣称独立文件入口与断点续传，协助时可拖拽或复制粘贴 | 代码里已有「文件传输」按钮，调用 RustDesk 的 `--file-transfer`（`src/Backend/Engines.cs`），权限由被控端原生确认决定；断点续传依赖 RustDesk 自身能力，本机未做续传实测。文本剪贴板同样走原生通道，双向粘贴尚未两机签收。 [UU 文件传输教程](https://uuyc.163.com/help/20241210/40220_1199229.html) · [RustDesk 1.5.3](https://github.com/rustdesk/rustdesk/blob/1.5.3/src/core_main.rs) |
| 直连、中继与弱网 | 官方宣称公网穿透、全球节点线路调优与弱网适配，但未给出统一网络下的固定延迟或零丢包承诺。远程流畅度同时受网络、编解码、帧调度影响 | 现有 Tailscale 负责组网和中继选择，程序给出路径、RTT 和超时，Moonlight 报告实际串流掉帧。2026-11-22 16:53 对 `LAPTOP-EXAMPLE` 做 12 次只读 `tailscale ping`：12/12 成功、全部直连公网端点、未走 DERP，中位 34 ms、最大 119 ms（n=12 时近似 p95）。这是控制面往返，不是视频丢包率。通用远控也可走 RustDesk 服务。 [UU 办公功能](https://uuyc.163.com/features/work/) · [Tailscale 路径说明](https://tailscale.com/kb/1257/connection-types) |

## 进阶功能与前置条件

| 功能组 | 已公开功能 | 在这两台电脑的状态 |
| --- | --- | --- |
| 高画质、高刷 | 官方宣称多档分辨率和码率，条件允许时 4K、144 帧、4:4:4 真彩，自定义码率最高 500 Mbps；还有超级屏 | 当前可选六档，见上表；`preferHevc` 默认开启，60 帧档默认也走 HEVC（用户可关闭偏好改用 H.264），90 帧档强制 HEVC。测试台式机使用 AMD RDNA2 独显，Sunshine 的 AMD 路径支持 H.264/HEVC；4:4:4、4K 与 144 fps 需按宿主、控制端与线材能力实测，本轮未验。 [UU 真彩](https://uuyc.163.com/features/color/) · [Moonlight FAQ](https://github.com/moonlight-stream/moonlight-docs/wiki/Frequently-Asked-Questions) |
| 观看和窗口 | 观看模式、缩放、平移、批注；小窗、置顶、不透明度；会话内切换 | 已有原生客户端窗口。独立观看和批注工具、置顶与透明度控制尚待开发。 [观看模式](https://uuyc.163.com/help/viewonly.html) · [小窗模式](https://uuyc.163.com/help/miniwindow.html) |
| 多屏 | 官方宣称多屏最多 5 个、虚拟屏最多 3 个，并支持物理屏切换、拆出窗口、超级屏和手机或平板无线副屏 | 原生 RustDesk 可在远控会话内选择已有屏幕，本机尚无第二物理屏实测。虚拟屏、多窗口恢复、超级屏和无线副屏仍属待验；不能把 UU 的官方数量当作本机已实现能力。 [多屏控制](https://uuyc.163.com/help/multiscreen.html) · [虚拟屏](https://uuyc.163.com/help/virtualscreen.html) · [超级屏](https://uuyc.163.com/help/superscreen.html) · [无线副屏](https://uuyc.163.com/help/20260311/40220_1286375.html) |
| 安全与隐私 | 双重确认、验证码刷新、安全设置锁、剪贴板互传开关、被控防窥、主机静音、断开后锁屏、受支持设备的解锁 | 当前授权由 RustDesk 与 Moonlight 各自执行。防窥屏与断开锁屏涉及系统和会话恢复，需要在被控端单独验证。 [协助验证方式](https://uuyc.163.com/help/20241204/40220_1197664.html) · [防窥屏](https://uuyc.163.com/help/20241203/40221_1197692.html) · [断开后锁屏](https://uuyc.163.com/help/20241203/40221_1197695.html) |
| 开机与电源 | 开机后启动、防休眠、远程开机和重启关机，WoL 可依靠同网辅助设备或兼容路由器 | 跨网唤醒需要台式机网卡、BIOS 和在线局域网辅助端。本版提供当前用户登录后自启和运行期间防待机，两项由用户手动开启。当前台式机的千兆网口断开，系统未报告已武装该 USB Wi‑Fi 网卡的唤醒能力。 [WoL 设置](https://uuyc.163.com/help/20241203/40220_1197682.html) · [路由器兼容列表](https://uuyc.163.com/help/20260407/40220_1294974.html) |
| 游戏与外设 | 游戏键位方案、触控、虚拟鼠标、3D 视角、蓝牙键鼠、多手柄及震动、宏和映射分享、麦克风直连 | 已集成 PC 原生键鼠与声音通道。手柄、移动触屏、控制端麦克风回传需要不同客户端组合或驱动，按设备单独验证。 [游戏场景](https://uuyc.163.com/games/) · [按键映射](https://uuyc.163.com/features/keyboard/) · [麦克风直连](https://uuyc.163.com/help/20260311/40220_1286300.html) |
| 账户与多平台 | Windows、macOS、Android、iOS 主控，Windows、macOS 可被控，部分 Android TV 可主控。账号设备同步及精简协助端 | 自有界面优先交付 Windows 10/11 x64。Moonlight 和 RustDesk 各有移动客户端，加入同一组网后可按上游协议联接。自有账号同步和独立移动 UI 需要新的工程。 [跨平台下载](https://uuyc.163.com/download/) · [Mac 被控](https://uuyc.163.com/help/macos.html) · [Moonlight 平台](https://moonlight-stream.org/) |
| 开发与运维 | 远程终端、可重连的多会话、TUI、端口映射 | 经远控桌面可操作用户已登录的 Windows 会话。独立终端、后台会话恢复及端口映射为独立能力，由后续明确的权限模型和接口承担。 [终端更新](https://uuyc.163.com/blog/20261010-zdgx.html) · [开发者能力](https://uuyc.163.com/blog/20261117dewhjg.html) |
| 服务体验 | 自动更新、帮助反馈、客户端版本检查、连接错误提示、跨地区中继及官方支持 | 随包提供指南与只读测量工具，组件固定版本并校验。更新用新便携包，现有配对按 Moonlight 自身位置保留。公开部署的账号和中继容量需要独立运营。 [帮助中心](https://uuyc.163.com/help/) · [错误码](https://uuyc.163.com/help/errorcode.html) |

## 用这两台设备验收

- 测试台式机环境：Windows 10 22H2、32 GB 内存、AMD RDNA2 独显、2560×1440 180 Hz。2026-11-22 只读核对时经 USB Wi-Fi 接入局域网；到默认网关的 12 次 ping 全部有回应，量级 0 ms。该结果只说明本机到网关的局域网连通性，不是视频丢包率。
- 控制端笔记本 `LAPTOP-EXAMPLE` 在同账号 Tailscale 网络中在线，地址 100.100.100.100；本轮 12 次 ping 全部直连成功，中位 34 ms、最大 119 ms。尚未采集其显卡、驱动、分辨率与屏幕参数，也未进行真实串流。
- 公平对照办法：同一对电脑、同一网络、同一分辨率与 60 fps、相同桌面操作和动态内容，UU 与双端远程各跑 10 分钟会话。分别记录网络丢帧、抖动丢帧、解码耗时、发送排队和输入到显示的时延；不直接拿两家定义不同的统计值相减。
- 判据为「相对 UU 在同条件下不劣」，或事先冻结可接受的差值；不预设固定毫秒数或零丢包。本轮未取得控制端笔记本的实际会话统计，尚未进行同条件串流对照；表中「待验」项需在上述条件下另行签收。 [V2EX 性能讨论](https://www.v2ex.com/t/1172237) · [开源方案讨论](https://www.v2ex.com/t/1193029) · [Moonlight 统计说明](https://github.com/moonlight-stream/moonlight-docs/wiki/Frequently-Asked-Questions)
