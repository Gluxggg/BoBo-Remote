# BoBo远程 · 四端互通安装与联调指南

Windows、Android、macOS 与 iOS / iPadOS 使用相同的 BoBo6.2 协议。`UDP 49812` 用于局域网发现，`TCP 49810` 承载 TLS 1.2 会话。两台 Windows 设备可以协商 `UDP 49811` 上的加密 UDP+FEC。跨平台远控走 TCP+TLS。 [协议规范](../protocol/BOBO6-SPEC.md)

## 四端能力

| 平台 | 安装包 | 主控 | 被控 |
| --- | --- | --- | --- |
| **Windows 10 / 11 x64** | `BoBoRemote-Setup-x64.exe` 或 `BoBoRemote-Portable-x64.zip` | 超级屏、键鼠、Android 导航键、观看 iOS 投屏 | DXGI/GDI、Media Foundation H.264、WASAPI、物理键鼠锁与防窥 |
| **Android 8.0+** | `BoBoRemote-Android.apk` | MediaCodec 硬解、触控板与直点模式、快捷键盘 | MediaProjection 录屏、无障碍触控和系统导航 |
| **macOS 13.0+** | `BoBoRemote-macOS.dmg`（Universal 2，Apple 芯片和 Intel） | 视频硬解、超级屏、`⌘ → Ctrl` | ScreenCaptureKit、VideoToolbox、防窥遮罩与 `CGEventTap` |
| **iOS / iPadOS 16.0+** | `BoBoRemote-iOS-Unsigned.ipa` | 前台观看与主控、触控板与直点模式 | ReplayKit 仅观看投屏（`view_only`） |

下载对应平台的安装包并核对发布页的 `SHA256SUMS.txt`。 [GitHub Releases](https://github.com/Gluxggg/BoBo-Remote/releases/latest)

## 连接与配对

1. **建立网络连接。** 同一局域网内打开两端 App 后，从设备列表选择被控端。跨网可使用 [Tailscale](https://tailscale.com/) 的 `100.x.y.z` 地址或其他两端互通的 IP 地址。
2. **生成配对码。** 在被控端允许传入连接并生成 4 位临时配对码。配对码有效 120 秒，成功后即失效，累计失败 5 次也会失效。
3. **输入配对码。** 使用已升级的主控和被控 App。主控从 TLS 握手证书提取并保存被控端 RSA-2048 证书的 SHA-256 指纹，被控端验证主控端的 RSA 签名。重连时主控核对已保存的指纹，证书变更会拒绝连接。

四端协议对单文件设有 2 GiB 上限，接收方校验 SHA-256 后确认文件。 [协议规范](../protocol/BOBO6-SPEC.md)

## 安装与系统权限

### Windows

运行安装包，或解压便携 ZIP 后启动 `BoBo远程.exe`。安装器默认使用 `%LOCALAPPDATA%\BoBoRemote`。被控端按 Windows 防火墙提示授权所用网络上的入站连接。

### Android

安装 `BoBoRemote-Android.apk`。作为被控端，在「本机被控」页开启屏幕录制，再到系统设置启用 **BoBo远程 免Root触控辅助服务**。完成配对后可接收触控和系统导航输入。

### macOS

打开 DMG，将 `BoBoRemote.app` 拖入 `Applications`。作为被控端，按系统提示授予**屏幕录制与系统音频录制**和**辅助功能**权限。前者用于 ScreenCaptureKit，后者用于远控键鼠和防窥键鼠锁。机主可按 `⌘ + ⌥ + ⇧ + P` 退出防窥。

### iOS / iPadOS

IPA 含主 App 和 ReplayKit 投屏扩展。可使用 Sideloadly、AltStore 或 SideStore 签装，或在 Mac 上进入 `apple/BoBoRemoteiOS/`，用 `xcodegen generate` 生成工程后在 Xcode 中设置自己的 Team 和签名。安装后按系统提示信任开发者证书并授予本地网络访问。 [Sideloadly](https://sideloadly.io/) · [AltStore](https://altstore.io/) · [SideStore](https://sidestore.io/)

- **主控与观看**需要 BoBo远程 处于前台。
- **分享本机画面**时，从「投屏被看」启动 **BoBo投屏扩展**，由另一端配对观看。扩展与主 App 通过本机回环连接，等待上一帧确认后再发送。主 App 进入后台时桥接暂停，返回前台后恢复。系统管理 App 和广播扩展的暂停周期。

## 开发检查

从仓库根目录运行对应平台的检查。以下 Apple 命令需要 macOS 和 Xcode，生成 IPA 还需要 XcodeGen。

```powershell
.\ci\build.ps1
.\build.ps1
```

```bash
(cd android && gradle :app:testDebugUnitTest :app:assembleRelease --stacktrace)
(cd apple/BoBoAppleCore && swift test)
bash apple/scripts/package-macos-dmg.sh
bash apple/scripts/package-ios-ipa.sh
```

本次 Windows 在本地直接调用编译器验证了原生自检 10/10、托管自检 118 项通过，安装器自检退出码为 0。该环境的 RemoteSigned 阻止了正常脚本执行。macOS Universal 2 打包、Apple SDK 测试与 iOS 真机联调待验证。 [CI 工作流](../.github/workflows/ci.yml) · [共享测试向量](../protocol/golden-vectors.json)
