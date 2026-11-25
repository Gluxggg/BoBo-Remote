# BoBo远程 · 四端互通（Windows / Android / macOS / iOS）安装与联调指南

BoBo远程 V6.2 采用统一的 **BoBo6.2 权威二进制协议**（`TCP+TLS 1.2 :49810` / 局域网 `UDP :49812`），所有平台原生硬编硬解，**零中转服务器费用**（局域网自动发现直连，跨公网可通过 Tailscale 虚拟内网 IP `100.x.y.z` 端到端直连）。

---

## 一、四端能力总览

| 平台 | 安装产物 | 主控（控制他人） | 被控（被他人控制） | 特色能力 |
|---|---|---|---|---|
| **Windows** | `BoBoRemote-Setup-x64.exe` / 便携 ZIP | ✅ 全功能（含控制 Android 手机导航栏、观看 iOS 投屏） | ✅ 全功能（DXGI/MF 硬编 + 物理键鼠锁防窥） | 超级屏无黑边、框内小指针、进程内单文件 |
| **Android** | `BoBoRemote-Android.apk` | ✅ 全功能（触控板鼠标 + 直点触控 + 悬浮球 + 快捷键盘） | ✅ **免 Root 全功能**（MediaProjection + 无障碍触控） | 蓝牙键盘映射 Windows VK、支持 720p/1080p/2K 切换 |
| **macOS** | `BoBoRemote-macOS.dmg` | ✅ 全功能（AVSampleBufferDisplayLayer 零拷贝 + ⌘→Ctrl 智能映射） | ✅ 全功能（ScreenCaptureKit 含免驱动系统音频 + 防窥遮罩 + CGEventTap） | 防窥窗口自动从录屏排除，远端清晰机前黑屏 |
| **iOS / iPadOS** | `BoBoRemote-iOS-Unsigned.ipa` | ✅ 全功能（60/120Hz 硬解 + 触控板/直点双模式 + 悬浮球 + 快捷键盘） | 👁️ **ReplayKit 投屏被看协助**（Apple 沙盒限制禁止全局触控注入） | 免付费 App Group，兼容 AltStore / SideStore / Sideloadly 自签 |

---

## 二、快速四端互联（局域网 & Tailscale 跨网）

1. **同一局域网（Wi-Fi / 有线）**：
   - 两端均打开 BoBo远程，软件会自动通过 `UDP 49812` 相互发现并在设备列表显示平台标识（`Windows` / `macOS` / `Android` / `iOS`）。
   - 也可以直接输入对方界面上的 **9 位 BoBo 设备码**。
2. **跨外网互联（例如手机 5G 控家里电脑）**：
   - 在两端安装免费版 [Tailscale](https://tailscale.com/) 并登录同一账号；
   - 在 BoBo远程 输入对方的 Tailscale IP（如 `100.83.x.x`）即可端到端 TLS 1.2 加密直连。
3. **首次配对（4 位临时配对码）**：
   - 在被控端点击「生成 4 位临时配对码」（120 秒有效、单次消费）；
   - 在主控端输入这 4 位数字完成 HMAC-SHA256 双向证书指纹绑定；
   - 配对一次后该设备进入信任列表，后续直接免码一键直连。关闭被控端 BoBo远程 后端口立停，绝对安全。

---

## 三、各平台安装与权限指引

### 1. Android 端（`BoBoRemote-Android.apk`）
- 直接在手机上安装 `BoBoRemote-Android.apk`（若系统提示未知来源，允许安装即可）。
- **作为主控端**（手机控电脑/Mac/另一台手机）：无需任何特殊权限，打开即用。
- **作为免 Root 被控端**（电脑/Mac/手机控这台安卓）：
  1. 切到「本机被控」页，点击「1. 屏幕画面授权」并允许录屏；
  2. 点击「2. 免 Root 远程触控」，在系统无障碍设置中开启 **BoBo远程 免Root触控辅助服务**；
  3. 生成 4 位临时配对码即可被完整操控（含返回/主页/多任务/通知栏/音量/锁屏）。

### 2. macOS 端（`BoBoRemote-macOS.dmg`）
- 双击打开 `BoBoRemote-macOS.dmg`，将 `BoBoRemote.app` 拖入 `Applications`。
- 首次打开若提示“未验证开发者”，请在 **系统设置 → 隐私与安全性** 底部点击「仍要打开」，或在终端执行：
  ```bash
  xattr -cr /Applications/BoBoRemote.app
  ```
- **作为被控端**，在左侧栏权限面板一键授权两项系统权限：
  1. **屏幕录制与系统音频录制**（用于 ScreenCaptureKit 采集画面与免驱动系统声音）；
  2. **辅助功能**（用于 CGEvent 远控键鼠输入与防窥屏物理键鼠锁）。
- 防窥开启后，机主可随时按 **`⌘ + ⌥ + ⇧ + P`** 紧急退出防窥。

### 3. iOS / iPadOS 端（`BoBoRemote-iOS-Unsigned.ipa`）
CI 自动构建未签 `.ipa`（内含主 App + ReplayKit 投屏扩展），任选以下免费方式自签安装到 iPhone / iPad：
- **方式 A（推荐，电脑端一键安装）**：使用 [Sideloadly](https://sideloadly.io/)（支持 Windows/macOS），拖入 `BoBoRemote-iOS-Unsigned.ipa`，登录个人 Apple ID 即可签装。
- **方式 B（无线自动续签）**：通过 [AltStore](https://altstore.io/) 或 [SideStore](https://sidestore.io/) 导入 `.ipa` 安装。
- **方式 C（Xcode 源码直装）**：在 Mac 上进入 `apple/BoBoRemoteiOS/` 执行 `xcodegen generate && open BoBoRemoteiOS.xcodeproj`，选择自己的 Team 即可真机运行。
- 安装后在 iPhone **设置 → 通用 → VPN 与设备管理** 信任开发者证书，并允许「本地网络」权限即可使用。
- 如需将 iPhone 屏幕共享给电脑查看：切到「投屏被看」页，点击系统录屏按钮启动 **BoBo投屏扩展**，再由电脑端配对连接即可。

---

## 四、开发者协议一致性自检

所有平台均强制校验同一份 [`protocol/golden-vectors.json`](../protocol/golden-vectors.json)：
- **Windows**：`.\ci\build.ps1`（Native 9/9 + Managed Golden Vectors + Setup Self-Test）
- **Android**：`cd android && gradle :app:testDebugUnitTest :app:assembleRelease`
- **Apple Core / macOS / iOS**：`cd apple/BoBoAppleCore && swift test`、`bash apple/scripts/package-macos-dmg.sh`、`bash apple/scripts/package-ios-ipa.sh`
