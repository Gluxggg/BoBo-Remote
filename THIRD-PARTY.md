# 第三方组件、版本、许可与源码获取

本包实际分发的第三方二进制有三项：RustDesk（AGPL-3.0）、官方 Moonlight（GPL-3.0）、可选的 Moonlight V+（GPL-3.0）。本项目没有修改它们的源码；启动器以独立进程方式调用它们。

- RustDesk：AGPL-3.0。用官方 MSI **仅提取文件**得到原版布局，放在 `engines\RustDesk\`（不安装产品、不注册服务）
- Moonlight：GPL-3.0，官方 Windows 便携 zip 原样展开到 `engines\Moonlight\`（含 Qt、FFmpeg 等第三方库；Windows 二进制保留官方签名）
- Moonlight V+：GPL-3.0，官方 `Moonlight-VPlus-Portable-x64-6.5.6.zip` 原样展开到 `engines\MoonlightVPlus\`（opt-in 增强客户端；Windows 二进制未签名）
- 启动器 `双端远程.exe`：本项目自写代码，源码在本仓库 `src\`，不属于上游组件
- .NET Framework 4.8：Windows 系统组件，不随包分发；构建时使用系统自带 `csc.exe`

## 1. 固定版本与校验值

| 组件 | 版本 | 分发介质 | 字节数 | SHA256 |
| --- | --- | --- | --- | --- |
| RustDesk | 1.5.3 | `rustdesk-1.5.3-x86_64.msi`（官方 MSI，Release 发布 2026-11-09） | 26,734,592 | `27f6bfb48a93d524860dd7f4a79e349be14b6dc71295746cf132a060ba4869c8` |
| RustDesk（MSI 展开后） | 1.5.3 | `PFiles64\RustDesk\` → 包内 `engines\RustDesk\` | 104 个文件，83,590,630 字节 | 关键文件见 §2 |
| Moonlight | 6.2.0 | `MoonlightPortable-x64-6.2.0.zip`（Release 发布 2026-10-04） | 37,242,232 | `6f91d268b41ed5ca8066d9d7578dc403b8db190991ff48f00b1700465f8f7ebf` |
| Moonlight（展开后） | 6.2.0 | 包内 `engines\Moonlight\` | 284 个文件，106,505,953 字节 | `Moonlight.exe` 2,860,296 字节，SHA256 `2f2536a5670a9c3f2a2a9c5e9fb46dfd684ccd9721be8f2d5ac590b81b7cb0db` |
| Moonlight V+ | 6.5.6 | `Moonlight-VPlus-Portable-x64-6.5.6.zip`（官方发布资产） | 48,208,826 | `e02447be08eca5ddd2555fd7669a114f6c896fda3b70866401881026c27314e5` |
| Moonlight V+（展开后） | 6.5.6 | 包内 `engines\MoonlightVPlus\` | 1307 个文件，133,622,279 字节 | 关键文件见 §3 |

官方来源：

- RustDesk Release：<https://github.com/rustdesk/rustdesk/releases/tag/1.5.3>（MSI 资产 ID 727209108）
- Moonlight Release：<https://github.com/moonlight-stream/moonlight-qt/releases/tag/v6.2.0>（资产 ID 608780016）
- Moonlight V+ Release：<https://github.com/qiin2333/moonlight-qt/releases/tag/v6.5.6>；二进制资产 <https://github.com/qiin2333/moonlight-qt/releases/download/v6.5.6/Moonlight-VPlus-Portable-x64-6.5.6.zip>；源码 tag <https://github.com/qiin2333/moonlight-qt/tree/v6.5.6>

两个官方安装介质在本机核验 Authenticode 为 `Valid`（RustDesk 由 PURSLANE 签发，Moonlight 由 Cameron Gutman 签发），构建脚本要求官方 Moonlight 必须保持 `Valid`。Moonlight V+ 的 Windows 二进制为 `NotSigned`（无 Authenticode 签名），构建脚本明确校验这一状态，不会把它当作已签名软件。

## 2. RustDesk 的 MSI“仅提取”布局

- 不使用单文件 `rustdesk-1.5.3-x86_64.exe`（自解压 wrapper，在受限子进程里解包失败、无窗口）。
- 构建时执行 `msiexec /a "<msi>" /qn TARGETDIR="<构建目录>"`：这是 Windows Installer 的管理安装，只把文件展开到目标目录，**不安装产品、不注册服务、不写卸载项**；随后把 `PFiles64\RustDesk\*` 原样复制到 `engines\RustDesk\`。
- 已知构建副作用：Windows Installer 会在 `C:\Windows\Installer` 留一条约 20 KiB 的 `SourceHash{...}` 记账文件；不是安装。
- 展开出的关键文件（构建时逐个校验）：

| 文件 | 字节数 | SHA256 |
| --- | --- | --- |
| `RustDesk.exe`（FileVersion `1.5.3+71`） | 386,416 | `34170d41670e8c3f8824d8c3cd7de5388cc547205fe696021bde528933b3c21d` |
| `librustdesk.dll` | 41,430,896 | `05b647cf443bbb2851059d0d0d19fb450fd8096ab453df84c464bf5a9c99e563` |
| `flutter_windows.dll` | 18,058,096 | `de67ca5cf8d9fb8f5ddf648ad0b176df92f3d997061000db5dbdcb7ce2991fe7` |
| `data\app.so` | 13,747,104 | `ec23b8ec501ac97a09193d5f802abb8f19808def7a87fe588e41965368528387` |
| `data\icudtl.dat` | 778,864 | `c12537022ef818991a7bfed41a76d8d6ae962ffbc0e6511ac762a5d0845e7f7c` |

MSI 展开物里保留了官方 `data\flutter_assets\NOTICES.Z`（Flutter 及依赖署名）和 `usbmmidd_v2\License.txt`；没有 AGPL 全文，因此包内 `LICENSES\AGPL-3.0.txt` 来自官方仓库 tag。

## 3. Moonlight V+ 6.5.6（opt-in 引擎）

- 上游：`qiin2333/moonlight-qt` 的 **Moonlight V+ for PC**，基于官方 Moonlight Qt 维护的增强分支；源码 tag `v6.5.6`，许可证 GPL-3.0（源码包内 `LICENSE` 为 GPLv3 全文，SHA256 `589ed823e9a84c56feb95ac58e7cf384626b9cbf4fda2a907bc36e103de1bad2`，与本包 `LICENSES\GPL-3.0.txt` 相同）。
- 二进制资产：`Moonlight-VPlus-Portable-x64-6.5.6.zip`，48,208,826 字节，SHA256 `e02447be08eca5ddd2555fd7669a114f6c896fda3b70866401881026c27314e5`；Windows Authenticode `NotSigned`（Product `Moonlight V+ for PC`）。
- 源码归档（本机校验，不随包分发）：`source-v6.5.6.zip`，6,705,967 字节，SHA256 `fec23c49ac93ac03d99c9091175da62e511117543ec289b6d751404d25430714`；官方下载地址 <https://github.com/qiin2333/moonlight-qt/archive/refs/tags/v6.5.6.zip>。
- 构建只把发布 zip 的**二进制文件**展开到 `engines\MoonlightVPlus\`；不复制 `.qa\90-engine\vplus\source`、源码归档、私有 `.ini`/配置或构建产物。原包本身不含源码目录，也不含 `LICENSES` 文件；GPLv3 全文由本包 `LICENSES\GPL-3.0.txt` 共用提供。
- 关键文件（构建时逐个校验）：

| 文件 | 字节数 | SHA256 |
| --- | --- | --- |
| `Moonlight.exe` | 9,191,936 | `847757d838157806d327be626889a7c05a291f608bced1848ffb61a01b799c63` |
| `moonlight-clipboard-helper.exe` | 146,432 | `e94f8c0abd5c705e1cc76f6977652f02b91186f04f42de8cbcc44f852b9f3af9` |
| `portable.dat` | 3 | `6bdf66b5bf2a44e658bea2ee86695ab150a06e600bf67cd5cce245ad54962c61` |

- V+ 是独立进程；官方 Moonlight 6.2.0 仍完整保留在 `engines\Moonlight\`，两者不互相覆盖。启动器是否使用 V+ 由用户/设置 opt-in，默认不改变官方 Moonlight 的配对与设置。

## 4. 许可证全文（随包）

| 文件 | 对应组件 | 来源（固定 tag） | 字节数 | SHA256 |
| --- | --- | --- | --- | --- |
| `LICENSES\AGPL-3.0.txt` | RustDesk 1.5.3 | `https://raw.githubusercontent.com/rustdesk/rustdesk/1.5.3/LICENCE` | 34,523 | `8486a10c4393cee1c25392769ddd3b2d6c242d6ec7928e1414efff7dfb2f07ef` |
| `LICENSES\GPL-3.0.txt` | Moonlight 6.2.0 与 Moonlight V+ 6.5.6 共用 | `https://raw.githubusercontent.com/moonlight-stream/moonlight-qt/v6.2.0/LICENSE` | 35,141 | `589ed823e9a84c56feb95ac58e7cf384626b9cbf4fda2a907bc36e103de1bad2` |

官方 Moonlight 便携 zip **没有附带**许可证文本，V+ 便携 zip **也没有**附带许可证文本；两者的 GPL-3.0 全文由本包从官方 tag 的 `LICENSE` 获取，并按上表校验后放入 `LICENSES\`，内容未改。V+ 源码包内的 `LICENSE` 与上表 GPL-3.0 哈希完全一致。

## 5. Moonlight 与 V+ 便携包内的第三方库（原样保留）

包内 `engines\Moonlight\` 和 `engines\MoonlightVPlus\` 都保留各自全部 DLL 与插件目录（`platforms`、`imageformats`、`iconengines`、`tls`、`networkinformation`、`qml`、`translations`、`multimedia`、`portable.dat` 等），没有删减、替换或重新打包。版本取自包内二进制版本资源，仅作清点（以官方 Moonlight 6.2.0 为样本；V+ 同族但版本/文件集合略多）：

| 组件 | 包内文件（示例） | 版本 | 上游常见许可证 |
| --- | --- | --- | --- |
| Qt 6 | `Qt6Core.dll`、`Qt6Gui.dll`、`Qt6Network.dll`、`Qt6Quick*.dll`、`platforms\qwindows.dll` 等 | 6.12.0 | LGPL-3.0 / GPL-3.0 双许可（Qt 开源版）；本包按动态链接保留 |
| FFmpeg | `avcodec-63.dll`、`avformat-63.dll`、`avutil-61.dll`、`swscale-10.dll` | avcodec/avformat 63.1.102、avutil 61.1.102、swscale 10.1.102 | LGPL-2.1-or-later（取决于上游构建配置） |
| SDL | `SDL2.dll`、`SDL3.dll`、`SDL2_ttf.dll` | SDL2 2.32.74、SDL3 3.4.18 | zlib |
| OpenSSL | `libssl-3-x64.dll`、`libcrypto-3-x64.dll` | 3.6.5 | Apache-2.0 |
| dav1d | `dav1d.dll` | 1.5.4 | BSD-2-Clause |
| libplacebo | `libplacebo-374.dll` | v7.374.0 | LGPL-2.1-or-later |
| opus / Discord RPC / AntiHooking | `opus.dll`、`discord-rpc.dll`、`AntiHooking.dll` | 版本资源未标明 | 分别为 BSD-3-Clause、MIT、Moonlight 上游随附组件 |
| MSVC 运行时 | `msvcp140*.dll`、`vcruntime140*.dll`、`concrt140.dll`、`vccorlib140.dll` | 14.51.36247.0 | Microsoft Visual C++ 可再发行组件条款 |

以上许可证归属为通用信息，具体以上游实际构建与发布说明为准。本包未修改这些二进制；公开分发时请按各项目要求核对署名与源码提供方式。

## 6. 许可义务：私用与公开分发

**私用（自己两台电脑之间）**：保留上游版权声明与许可证全文（本包已放 `LICENSES\`，并在 `BUILD-INFO.txt` 记录版本与校验值），不需要对外提供源码。

**公开分发前必须重新检查**（本文件不代替法律意见）：

1. **GPL-3.0（Moonlight 6.2.0 与 Moonlight V+ 6.5.6）**：分发二进制通常需同时提供对应源码，或提供符合 GPL 第 6 条的书面要约；只给下载链接一般不够。对应源码 tag：`moonlight-stream/moonlight-qt` 的 `v6.2.0` 与 `qiin2333/moonlight-qt` 的 `v6.5.6`。
2. **AGPL-3.0（RustDesk）**：除分发义务外，若作为网络服务提供给他人使用，第 13 条要求向使用者提供 Corresponding Source。源码 tag 为 `1.5.3`。
3. **Qt（LGPL-3.0）**：动态链接通常可行，但必须允许最终用户替换 Qt 库、保留版权声明，并按 LGPL 要求提供 Qt 源码获取方式；本包未修改或封装这些 DLL。
4. **FFmpeg、SDL、OpenSSL、dav1d、libplacebo、opus、Discord RPC 等**：各自再分发条款不同，公开分发请逐项核对，必要时随附对应源码/许可文本。
5. **Microsoft VC++ 运行时**：按 Microsoft 可再发行条款处理，不要单独拆分。
6. **V+ 未签名**：Windows 可能显示 SmartScreen/未知发布者提示；这属于上游二进制事实，不要把它描述成已签名或官方 Moonlight 发行版。
7. 本包不是 RustDesk、Moonlight 或 Moonlight V+ 的官方发行版；相关名称与商标归各自所有者，不要暗示官方背书。

## 7. 源码获取路径

```powershell
# RustDesk 1.5.3
git clone --depth 1 --branch 1.5.3 https://github.com/rustdesk/rustdesk.git rustdesk-1.5.3

# 官方 Moonlight 6.2.0
git clone --depth 1 --branch v6.2.0 https://github.com/moonlight-stream/moonlight-qt.git moonlight-qt-6.2.0

# Moonlight V+ 6.5.6
git clone --depth 1 --branch v6.5.6 https://github.com/qiin2333/moonlight-qt.git moonlight-qt-6.5.6
```

也可用官方 tag 归档：

- `https://github.com/rustdesk/rustdesk/archive/refs/tags/1.5.3.zip`
- `https://github.com/moonlight-stream/moonlight-qt/archive/refs/tags/v6.2.0.zip`
- `https://github.com/qiin2333/moonlight-qt/archive/refs/tags/v6.5.6.zip`（本机校验 SHA256 见 §3）

启动器源码在本仓库 `src\`（`src\*.cs` 与 `src\app.manifest`），不来自上述上游项目。

## 8. 在成包里复核完整性

```powershell
Get-FileHash .\engines\RustDesk\RustDesk.exe -Algorithm SHA256
# 应为 34170d41670e8c3f8824d8c3cd7de5388cc547205fe696021bde528933b3c21d

Get-FileHash .\engines\Moonlight\Moonlight.exe -Algorithm SHA256
# 应为 2f2536a5670a9c3f2a2a9c5e9fb46dfd684ccd9721be8f2d5ac590b81b7cb0db
# 并应保持 Authenticode Valid

Get-FileHash .\engines\MoonlightVPlus\Moonlight.exe -Algorithm SHA256
# 应为 847757d838157806d327be626889a7c05a291f608bced1848ffb61a01b799c63
# Authenticode 应为 NotSigned

Get-FileHash .\LICENSES\AGPL-3.0.txt -Algorithm SHA256
Get-FileHash .\LICENSES\GPL-3.0.txt -Algorithm SHA256
```

包内 `BUILD-INFO.txt` 另记录了本次构建时间、构建环境、上游资产来源与 V+ 的未签名状态。构建脚本在打包前会二次校验 V+ 展开目录的关键文件哈希、`NotSigned` 状态，以及“不含私有 `.ini`/源码对象”的清洁边界。
