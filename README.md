<p align="center">
  <img src="assets/bobo-logo.png" alt="BoBo远程 logo" width="152" />
</p>

<h1 align="center">BoBo远程</h1>

<p align="center">
  <strong>双端开箱即用的 Windows 低延迟远程桌面与高清串流入口</strong>
</p>

<p align="center">
  <a href="README.md"><img src="https://img.shields.io/badge/%E8%AF%AD%E8%A8%80-%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-dc2626" alt="简体中文"></a>
  <a href="README.en.md"><img src="https://img.shields.io/badge/Language-English-2563eb" alt="English"></a>
</p>

<p align="center">
  <a href="https://github.com/Gluxggg/dual-remote/actions/workflows/ci.yml"><img src="https://github.com/Gluxggg/dual-remote/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="许可证"></a>
  <img src="https://img.shields.io/badge/status-V4_complete-brightgreen" alt="状态">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0078d6" alt="Platform">
  <img src="https://img.shields.io/badge/.NET%20Framework-4.8-512bd4" alt=".NET Framework 4.8">
  <img src="https://img.shields.io/badge/%E5%BC%95%E6%93%8E-RustDesk%20%7C%20Moonlight%20V%2B%20%7C%20Sunshine-2e4ecb" alt="Engines">
</p>

---

**一个便携包，双端开箱即用。** 把通用远控客户端、高帧率低延迟串流客户端与内置被控宿主装进同一个免安装压缩包；无需注册账号、无需安装 SDK，在你的台式机和笔记本上解压即连。

专为**自用双机办公**、**跨网 2K 90 帧低延迟串流**与**物理屏隐私防窥**而设计——内置全新冰川浅蓝极简界面、首次打开 7 步灰白框选新手引导、自定义防窥壁纸与可还原的弱网抗丢包调优。

---

<p align="center">
  <img src="assets/screenshot-devices.png" alt="BoBo远程主界面：冰川浅蓝极简设备仪表盘，一键连接设备、允许连接本机、开启防窥壁纸与快速复制局域网/Tailscale/IPv6 地址" width="920" />
</p>

---

## 为什么选择 BoBo远程？

| 传统商业远控方案（UU 远程 / ToDesk / 向日葵） | BoBo远程（dual-remote） |
| --- | --- |
| 必须注册并登录厂商账号，设备元数据托管云端 | **零账号、无自建中转** — 走你的局域网、Tailscale 或公网 IPv6 直连 |
| 画质、码率、帧率与编解码参数黑盒化，常受会员档位限制 | **全开源引擎 + 明面调优** — Moonlight V+ / Foundation Sunshine + 可还原诊断脚本 |
| 往往需要安装常驻系统服务、驱动并改写防火墙规则 | **绿色便携、显式授权** — 独立进程编排，不静默安装服务、驱动或改防火墙 |
| 多套开源工具（RustDesk + Moonlight + Sunshine）配置繁琐 | **统一原生桌面入口** — 单一 ZIP 双端通用，内置 4 位配对码与 7 步交互式新手引导 |
| 被控端物理屏防窥往往只有单一黑屏或缺少锁屏状态联动 | **自定义防窥壁纸 + 硬件熄屏虚拟屏** — 支持远程自动拉起、退出自动锁屏与状态机防误触发 |

## 工作流程

```mermaid
flowchart LR
  subgraph L["BoBo远程.exe（原生 WinForms · C# 5）"]
    direction TB
    G["7 步灰白框选新手引导"] -.-> A["全部设备 · 本机仪表盘"]
    A --> B["远程协助 · 4 位配对 / 9 位设备码"]
    A --> C["防窥与显示 · 壁纸 / 虚拟屏 / 熄屏"]
    A --> D["设置 · 串流画质 / 抗丢包调优"]
  end

  B -->|"已授权设备：直接串流"| V["Moonlight V+ 6.5.6<br/>HEVC · Foundation ABR"]
  B -->|"首次串流：4 位本机配对码"| P["被控端本机显式授权"]
  P --> V
  B -.->|"备用串流客户端"| M["Moonlight 6.2.0"]
  B -->|"临时协助 / 文件互传"| R["RustDesk 1.5.3"]

  C -->|"物理屏防窥壁纸（WDA_EXCLUDEFROMCAPTURE）"| W["本地隐私幕布 + 锁屏守护"]
  C -->|"Zako 虚拟屏 / 硬件熄屏"| S[("Foundation Sunshine<br/>内置或复用本机运行实例")]
  D -->|"只读诊断 & 可还原调优"| S

  V --> S
  R -.->|"被控端原生窗口确认"| H[("另一台 Windows 电脑")]
  S --- H
```

客户端引擎与内置宿主均以**独立进程**方式从固定相对路径 `engines\` 调用，启动器不链接 GPL 代码、不修改上游源码、也不接受配置文件中的任意可执行文件路径。每台设备首次连接均须在被控端本机完成明确授权（RustDesk 原生同意窗口或 Sunshine 4 位本机配对码），不存在“无授权直连”。

## 连接通道与核心场景

| 场景 / 通道 | 当前支持 | 说明 |
| --- | --- | --- |
| **高速低延迟串流** | ✅ 原生支持 | 基于 Moonlight V+ 6.5.6 + Foundation Sunshine，支持 2K 90 帧、HEVC、Foundation ABR 与动态 Auto-FEC |
| **首次本机 4 位码配对** | ✅ 原生支持 | 被控端点击「允许连接本机」生成 120 秒临时 4 位配对码，控制端主连接流内完成授权，后续免重复输入 |
| **临时协助与文件互传** | ✅ 原生支持 | 内置 RustDesk 1.5.3（MSI 仅提取原版文件，不注册服务），支持 9 位设备码远控与文件传输 |
| **物理屏隐私防窥壁纸** | ✅ 原生支持 | `WDA_EXCLUDEFROMCAPTURE` 系统级捕获排除，支持本地自定义图 / 3 款主题、远控连入自动开启、退出自动锁屏 |
| **虚拟屏与硬件熄屏防窥** | ✅ 搭配 Sunshine 驱动 | 支持 Zako 虚拟屏（16:10 / 自定义比例自适应）与远程期间关闭物理屏输出，断开后自动还原 |
| **首次使用新手引导** | ✅ 原生支持 | 首次启动自动展示 7 步灰白框选聚光灯教程，双层原生 ClearType 清晰文字，支持键盘翻页与右上角 `?` 随时重看 |

## 界面一览

### 7 步灰白框选新手引导与远程协助

首次打开 BoBo远程 时会自动弹出半透明灰白框选引导，逐一解释导航栏、本机地址复制、4 位配对码、防窥壁纸与低延迟调优；采用「分层半透明遮罩 + 原生不透明 ClearType 内容层」双层架构，任意 DPI 缩放下文字均锐利清晰。

<p align="center">
  <img src="assets/screenshot-onboarding.png" alt="7 步灰白框选新手引导：清晰聚光灯框选与原生 ClearType 引导卡片" width="450" />
  <img src="assets/screenshot-assist.png" alt="远程协助页面：120 秒临时 4 位配对码与 RustDesk 9 位设备码双通道切换" width="450" />
</p>

### 物理屏防窥壁纸、虚拟屏与抗丢包设置

<p align="center">
  <img src="assets/screenshot-privacy-display.png" alt="防窥与显示页面：自定义防窥壁纸、自动防窥与退出自动锁屏、虚拟屏与硬件熄屏防窥" width="450" />
  <img src="assets/screenshot-performance.png" alt="设置与低延迟抗丢包页面：只读宿主诊断与一键可还原调优" width="450" />
</p>

<details>
<summary>点击查看被控端防窥壁纸实际全屏渲染效果</summary>
<p align="center">
  <img src="assets/screenshot-privacy-wallpaper.png" alt="被控端全屏防窥壁纸渲染效果" width="760" />
</p>
</details>

## 快速开始

> 仓库只存放源码、资源与文档；运行 `build.ps1` 会自动下载四个固定版本上游组件并逐一校验字节数与 SHA-256。生成的便携包已同时包含控制端与内置 Foundation Sunshine 被控宿主，在两台 Windows 10/11 x64 电脑上直接解压运行即可，**无需安装 SDK 或另装 Sunshine**。

```powershell
# 1. 克隆仓库
git clone https://github.com/Gluxggg/dual-remote.git
cd dual-remote

# 2. 一键联网构建便携包（自动获取上游组件并逐个校验 SHA-256）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 3. 启动 BoBo远程（将 dist\BoBo远程-Windows-x64.zip 直接发给另一台电脑解压即用）
.\dist\BoBo远程-Windows-x64\BoBo远程.exe
```

常用构建与调试参数：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipFetch            # 离线构建：复用 .cache 中已校验缓存
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -FetchOnly            # 仅拉取并校验上游引擎缓存
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipFetch -NoPublish # 仅验证编译与干净组装，不覆盖 dist/ZIP
.\BoBo远程.exe --self-test                                                            # 无 UI 自动化自检
.\BoBo远程.exe --tour                                                                 # 立即重播 7 步新手引导
```

**两台电脑首次连通（3 步）：**

1. **被控端**解压运行 `BoBo远程.exe`，点击「允许连接本机」（若本机已在运行 Sunshine 则直接复用；否则准备包内固定版本并生成 4 位配对码）。
2. **控制端**运行同一便携包，在「全部设备」点击「手动连接设备」或在「远程协助」输入对方局域网 IP / Tailscale IP / 公网 IPv6。
3. 首次高速串流时在弹窗输入被控端 4 位配对码完成本机授权；若只需临时远程协助或传文件，直接切换至「临时协助 / 文件传输」输入 RustDesk 9 位设备码即可。

## 内置引擎与锁定版本

每个上游资产的字节数与 SHA-256 均硬编码锁定在 [`build.ps1`](build.ps1) 中，任意不一致立即中止构建。完整摘要与许可义务见 [THIRD-PARTY.md](THIRD-PARTY.md) 与 [NOTICE.md](NOTICE.md)。

| 组件 | 锁定版本 | 分发介质 | 用途 |
| --- | --- | --- | --- |
| **RustDesk** | `1.5.3` | 官方 MSI（`msiexec /a` **仅提取原版文件**，不安装、不注册服务） | 9 位设备码临时远控与文件互传 |
| **Moonlight** | `6.2.0` | 官方 Windows 便携 zip（保留官方 Authenticode 签名） | 备用串流客户端 |
| **Moonlight V+** | `6.5.6` | 官方 Windows 便携 zip（GPL-3.0 增强分支，未签名） | 主力串流客户端，支持 Foundation ABR |
| **Foundation Sunshine** | `v2026.1123.35314` | 官方 Windows x64 便携 zip（GPL-3.0 预发布，未签名） | 内置被控宿主，含 Desktop 应用表与本机配对 API |

## 核心模块与目录结构

| 模块 / 路径 | 职责 |
| --- | --- |
| `src/MainForm*.cs` | 冰川浅蓝极简主窗体与四大页面：全部设备、远程协助、防窥与显示、设置与低延迟调优 |
| `src/RemoteTheme.cs` | 零第三方依赖自绘主题系统：冰川蓝/瓷白色板、圆角卡片、双色导航图标、胶囊徽章与细滑滚动条 |
| `src/OnboardingTour.cs` | 7 步灰白框选新手引导：双层原生 ClearType 清晰渲染、防重叠自适应避让与完成状态持久化 |
| `src/PrivacyCurtainForm.cs` | 被控端物理屏防窥幕布、全局热键 `Ctrl+Alt+Shift+P` 与系统锁屏状态机守护 |
| `src/Backend/*.cs` | 引擎编排、内置宿主准备、Foundation 本机配对、网络地址识别、防窥壁纸几何与无 UI 自检 |
| `tools/*.ps1` | 被控端抗丢包诊断与一键可还原调优、虚拟屏 / 硬件熄屏配置、LAN / Tailscale 链路测速脚本 |
| `build.ps1` / `ci/build.ps1` | 零 SDK 便携包构建器与 GitHub Actions CI 编译自检脚本（调用系统自带 `csc.exe`） |

<details>
<summary>展开查看完整目录树</summary>

```text
dual-remote/
├── build.ps1                          # 固定版本 + SHA-256 校验便携包构建脚本
├── ci/build.ps1                       # CI 专用编译 + --self-test 验证脚本
├── assets/
│   ├── bobo-logo.png                  # 圆角方框奶牛猫品牌图标（嵌入 EXE 资源）
│   ├── bobo-remote.ico                # Win32 多尺寸 DIB+PNG 应用图标
│   └── screenshot-*.png               # 界面与新手引导预览图
├── src/
│   ├── MainForm.cs                    # 主框架、窗体缩放与防窥状态机
│   ├── MainForm.Pages.cs              # 导航栏、顶部 4 键工具栏、7 步新手引导与关于页
│   ├── MainForm.Devices.cs            # 我的设备仪表盘、地址速查与设备卡片
│   ├── MainForm.Connection.cs         # 双通道连接流与 4 位本机配对
│   ├── MainForm.Display.cs            # 防窥壁纸配置与虚拟屏/熄屏控制
│   ├── RemoteTheme.cs                 # 冰川浅蓝自绘控件库与双色矢量图标
│   ├── OnboardingTour.cs              # 双层 ClearType 聚光灯新手引导引擎
│   ├── PrivacyCurtainForm.cs          # 物理屏隐私防窥全屏窗口
│   ├── app.manifest                   # PerMonitorV2 DPI 感知清单
│   └── Backend/                       # Engines / BundledHost / FoundationClient / SunshinePairing / PrivacyWallpaper / HostNetworkInfo / ConnectionTelemetry / SelfTest
├── tools/
│   ├── Optimize-SunshineStream.ps1    # 抗丢包诊断与一键调优（自动备份、可还原）
│   ├── Set-SunshineDisplay.ps1        # 虚拟屏 / 硬件熄屏防窥配置（可还原）
│   ├── collect-device-info.ps1        # 设备只读自检（不含个人地址/账号）
│   ├── measure-lan-ping.ps1           # 局域网延迟基线测量
│   └── measure-tailscale-path.ps1     # Tailscale 直连 / DERP 路径与 RTT 抽样
├── LICENSES/                          # AGPL-3.0 与 GPL-3.0 许可证全文
├── 低延迟调优与防窥指南.md               # 证据分级的调优推理链与实测边界
├── UU远程功能核对.md                     # 公开能力对照核查记录
├── THIRD-PARTY.md                     # 第三方组件版本、SHA-256 与源码获取说明
└── NOTICE.md                          # 许可证与上游署名声明
```

</details>

## 主要能力

- ✅ **冰川浅蓝极简 UI 与专属奶牛猫品牌视觉** — 统一冷调冰川浅蓝导航栏与纯白悬浮卡片，无边框自定义缩放与圆角方框奶牛猫图标
- ✅ **首次打开 7 步灰白框选新手引导** — 聚光灯高亮目标区域 + 双层 ClearType 锐利排版卡片，支持键盘 `← / → / Enter / Esc` 与右上角 `?` 随时重播
- ✅ **单便携包双端通用** — 同一压缩包既可作控制端发起连接，也可在被控端准备包内 Foundation Sunshine 宿主，零 SDK 编译、解压即运行
- ✅ **双引擎无缝切换** — 高帧率低延迟串流（Moonlight V+ 6.5.6，带 Moonlight 6.2.0 备用）与 9 位设备码临时协助/文件互传（RustDesk 1.5.3）
- ✅ **物理屏防窥壁纸与锁屏状态机守护** — 支持本地图片与 3 套内置主题、远控接入自动遮挡、快捷键退出自动锁屏，解决解锁后防窥误重拉与残影问题
- ✅ **虚拟显示器与硬件熄屏防窥** — 支持 Foundation Sunshine / Zako 虚拟屏自适应笔记本比例与远程期间物理屏熄灭
- ✅ **明面抗丢包诊断与可还原调优** — 只读诊断编码器与网络链路，支持 CBR + HRD + LTR + Auto-FEC 一键应用与备份还原
- ✅ **无 UI 自动化自检 (`--self-test`)** — 覆盖参数校验、命令行组装、壁纸几何、防窥锁屏状态机、引导卡片防重叠与链路诊断逻辑

## 技术栈

- **C# 5 / .NET Framework 4.8** — Windows 系统自带 `csc.exe` 零依赖编译（无 SDK、无 NuGet）
- **WinForms + GDI+ / ClearType 双层合成** — `UpdateLayeredWindow` 聚光灯遮罩 + 原生不透明 ClearType 文本层
- **PowerShell 5.1** — 锁定哈希的构建流水线与可还原被控端调优工具
- **RustDesk 1.5.3 / Moonlight V+ 6.5.6 / Moonlight 6.2.0 / Foundation Sunshine** — 独立进程编排的四大开源引擎

## 低延迟、隐私安全与已知边界

本项目坚持**证据分级**原则，严格区分**已写入的配置**、**日志中观察到的现象**与**双机同条件实测对照**，完整推理链见 [`低延迟调优与防窥指南.md`](低延迟调优与防窥指南.md)。

- **零账号与最小本地持久化**：启动器没有账号体系，不运营中转或遥测。偏好仅存于 `%LOCALAPPDATA%\双端远程\`（不含密码、PIN 或令牌）；内置宿主的 `local-auth.token`、证书与配对记录仅在首次本机准备时生成于 `%LOCALAPPDATA%\双端远程\host\config`，绝不随便携包分发。
- **显式本机授权**：RustDesk 由被控端原生弹窗同意，Foundation Sunshine 由被控端生成/确认 4 位配对码；Windows 防火墙若拦截入站连接，须由用户手动放行，启动器不静默改规则。
- **调优边界与组件签名说明**：一键调优脚本仅作用于 `C:\Program Files\Sunshine` 且写入前自动备份；包内 Moonlight V+ 与预发布版 Foundation Sunshine 为上游未签名便携二进制（`NotSigned`），Windows 可能提示 SmartScreen，构建与使用前请按 [`THIRD-PARTY.md`](THIRD-PARTY.md) 核验 SHA-256。
- **防窥与帧率实测边界**：防窥壁纸依赖 `WDA_EXCLUDEFROMCAPTURE`，对具体捕获后端的效果需按实际驱动与版本验证；90 帧档需要被控端输出模式、刷新率与客户端解码能力同时满足。项目不宣称在延迟或画质上超越任何商业产品，请以自己的两台设备实测为准。

## 路线图

| 阶段 | 目标 | 状态 |
| --- | --- | --- |
| **V1** | 零 SDK 原生 WinForms 启动器与 RustDesk / Moonlight / Moonlight V+ 三客户端编排 | ✅ 完成 |
| **V2** | 内置 Foundation Sunshine 被控宿主、本机 4 位配对流与已运行实例自动复用 | ✅ 完成 |
| **V3** | 物理屏自定义防窥壁纸、锁屏状态机守护、Zako 虚拟屏与可还原抗丢包调优 | ✅ 完成 |
| **V4** | 冰川浅蓝新界面重构、圆角方框奶牛猫视觉系统与 7 步双层 ClearType 新手引导 | ✅ 完成 |
| **V5** | 双机同条件自动化延迟/丢帧对照测量脚本与会话内无感切换码率档位 | 📋 计划中 |

## Star History

<p align="center">
  <a href="https://www.star-history.com/#Gluxggg/dual-remote&Date">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=Gluxggg/dual-remote&type=Date&theme=dark" />
      <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=Gluxggg/dual-remote&type=Date" />
      <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=Gluxggg/dual-remote&type=Date" width="700" />
    </picture>
  </a>
</p>

## 参与贡献

欢迎提交 Issue 或 Pull Request！详细协作规范与证据分级约定见 [CONTRIBUTING.md](CONTRIBUTING.md)。最直接的贡献方式是提交你机器上某个引擎的真实行为、显卡/编码器型号与 `--self-test` 输出。

## 开发检查

提交 PR 前请在 Windows 10/11 x64 环境运行以下检查（与 GitHub Actions CI 完全一致）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ci\build.ps1       # 严格编译 + 无 UI 自检
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipFetch # 离线复核完整便携包构建
```

## 许可证

启动器源码（`src\`）、脚本（`tools\`、`ci\`、`build.ps1`）与文档以 [MIT](LICENSE) 发布。随构建产物分发的四个上游组件各自保留原始开源许可：

- **RustDesk 1.5.3**：AGPL-3.0（全文见 `LICENSES\AGPL-3.0.txt`）
- **Moonlight 6.2.0 / Moonlight V+ 6.5.6 / Foundation Sunshine v2026.1123.35314**：GPL-3.0（全文见 `LICENSES\GPL-3.0.txt` 与 `engines\Sunshine\LICENSE`）

完整版本摘要、校验值与署名义务见 [THIRD-PARTY.md](THIRD-PARTY.md) 和 [NOTICE.md](NOTICE.md)。本项目不是任何上游项目或商业远控产品的官方发行版。

---

<p align="center"><sub>为你的两台 Windows 电脑而写 · BoBo远程</sub></p>
