<#
.SYNOPSIS
    GitHub Actions Release 打包、校验与发布脚本（BoBo Remote V6 全内置单文件架构，UTF-8 带 BOM）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [Parameter(Mandatory = $true)]
    [string]$Repository
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($repoRoot)) { $repoRoot = (Get-Location).Path }
Set-Location $repoRoot

$ver = $Tag.TrimStart('v')
Write-Host "=== Building BoBo Remote $Tag (version $ver · V6 All-In-One) ==="
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -ReleaseVersion $ver
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed ($LASTEXITCODE)" }

Write-Host '=== Running self-tests on V6 All-In-One Launcher & Setup EXE ==='
$mainExe = Join-Path $repoRoot 'dist\BoBo远程-Windows-x64\BoBo远程.exe'
$setupExe = Join-Path $repoRoot 'dist\BoBoRemote-Setup-x64.exe'
$portableZip = Join-Path $repoRoot 'dist\BoBo远程-Windows-x64.zip'
$lightZip = Join-Path $repoRoot 'dist\BoBo远程-轻量更新包.zip'

$p1 = Start-Process -FilePath $mainExe -ArgumentList '--self-test' -Wait -PassThru
Write-Host "Launcher self-test exit code: $($p1.ExitCode)"
if ($p1.ExitCode -ne 0) { throw "BoBo远程.exe --self-test failed ($($p1.ExitCode))" }
$p2 = Start-Process -FilePath $setupExe -ArgumentList '--self-test' -Wait -PassThru
Write-Host "Setup self-test exit code: $($p2.ExitCode)"
if ($p2.ExitCode -ne 0) { throw "BoBoRemote-Setup-x64.exe --self-test failed ($($p2.ExitCode))" }

$assetDir = Join-Path $repoRoot 'release-assets'
if (Test-Path -LiteralPath $assetDir) { Remove-Item -LiteralPath $assetDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

$outSetup  = Join-Path $assetDir 'BoBoRemote-Setup-x64.exe'
$outPort   = Join-Path $assetDir 'BoBoRemote-Portable-x64.zip'
$outUpdate = Join-Path $assetDir 'BoBoRemote-Update-x64.zip'
Copy-Item -LiteralPath $setupExe -Destination $outSetup -Force
Copy-Item -LiteralPath $portableZip -Destination $outPort -Force
Copy-Item -LiteralPath $lightZip -Destination $outUpdate -Force

$hSetup  = (Get-FileHash -LiteralPath $outSetup -Algorithm SHA256).Hash.ToLower()
$hPort   = (Get-FileHash -LiteralPath $outPort -Algorithm SHA256).Hash.ToLower()
$hUpdate = (Get-FileHash -LiteralPath $outUpdate -Algorithm SHA256).Hash.ToLower()

$enginesStamp = 'bobo-v6-allinone-inproc'
$sumsPath = Join-Path $assetDir 'SHA256SUMS.txt'
$sumsText = "# engines=$enginesStamp`n$hSetup  BoBoRemote-Setup-x64.exe`n$hPort  BoBoRemote-Portable-x64.zip`n$hUpdate  BoBoRemote-Update-x64.zip`n"
[System.IO.File]::WriteAllText($sumsPath, $sumsText, (New-Object System.Text.UTF8Encoding($false)))

$notesPath = Join-Path $assetDir 'release-notes.md'
$notesTemplate = @'
## BoBo Remote __TAG__（四端互通正式版：Windows / Android / macOS / iOS）

### 本次核心升级亮点
- **1. 全新 BoBo6.2 四端统一权威协议（TCP+TLS 1.2 :49810 / LAN UDP :49812）**：
  - 固化 [`protocol/BOBO6-SPEC.md`](https://github.com/__REPO__/blob/main/protocol/BOBO6-SPEC.md) 与 [`protocol/golden-vectors.json`](https://github.com/__REPO__/blob/main/protocol/golden-vectors.json)，C# / Kotlin / Swift 三端 CI 强制字节级一致通过；
  - 局域网自动发现直连 + Tailscale 虚拟内网 IP 跨网直连，全程零中转服务器费用，保留严格的 4 位临时配对码 HMAC-SHA256 双向认证。
- **2. Android 原生客户端（`BoBoRemote-Android.apk` · Kotlin + Jetpack Compose）**：
  - **全功能主控**：MediaCodec 硬件 H.264 零拷贝解码 + 48kHz IMA-ADPCM 立体声音频，支持触控板鼠标与直点触控双模式、贴边悬浮球、控制中心、快捷键盘栏、剪贴板同步与蓝牙键盘映射；
  - **免 Root 全功能被控**：基于 MediaProjection 硬编 H.264 推流与系统无障碍服务（AccessibilityService），远程支持点击、滑动及返回/主页/多任务/通知栏/音量/锁屏。
- **3. macOS 原生客户端（`BoBoRemote-macOS.dmg` · Swift + SwiftUI + BoBoAppleCore）**：
  - **全功能主控**：AVSampleBufferDisplayLayer 零拷贝渲染、超级屏自适应、框内隐藏本机大鼠标、智能 `⌘ → Ctrl` 键位映射；
  - **全功能被控与防窥**：基于 ScreenCaptureKit（含免驱动 48kHz 系统音频采集）与 VideoToolbox 硬编，防窥遮罩窗口自动从录屏排除并联动 `CGEventTap` 物理键鼠锁（紧急退出 `⌘+⌥+⇧+P`）。
- **4. iOS / iPadOS 原生客户端（`BoBoRemote-iOS-Unsigned.ipa` · Swift + SwiftUI + ReplayKit）**：
  - **全功能主控**：60/120Hz 硬件解码、触控板/直点双模式、贴边悬浮球、控制中心与快捷 PC 键盘栏；
  - **ReplayKit 投屏协助**：内嵌 ReplayKit Broadcast Upload Extension，采用免付费 App Group 的本地回环桥接架构，支持 AltStore / SideStore / Sideloadly / Xcode 免费自签安装。
- **5. Windows 端同步升级（兼容四端 + 防窥硬件安全加固）**：
  - 自动识别远端平台（`Windows`/`macOS`/`Android`/`iOS`），连接 Android 时自动展开顶部手机导航条并保持竖屏等比居中，连接 iOS 时自动进入仅查看协助模式；
  - 防窥硬件调亮升级为安全仅调背光模式（严格只调非 HDR 显示器 VCP `0x10`，默认关闭，绝不触碰对比度与 RGB 增益）。

### 四端下载说明
- **Windows x64**：
  - `BoBoRemote-Setup-x64.exe`（图形化安装/静默覆盖升级包）
  - `BoBoRemote-Portable-x64.zip`（绿色免安装便携包）
  - `BoBoRemote-Update-x64.zip`（应用内一键增量更新包）
- **Android 手机/平板**：`BoBoRemote-Android.apk`（直接安装，支持 Android 8.0+）
- **macOS 电脑**：`BoBoRemote-macOS.dmg`（支持 macOS 13+，拖入 Applications 即可）
- **iOS / iPadOS**：`BoBoRemote-iOS-Unsigned.ipa`（支持 iOS 16+，使用 Sideloadly / AltStore / SideStore / Xcode 自签安装，详见 [`docs/CROSS-PLATFORM-GUIDE.md`](https://github.com/__REPO__/blob/main/docs/CROSS-PLATFORM-GUIDE.md)）

### Windows 产物 SHA-256 校验值
```text
__H_SETUP__  BoBoRemote-Setup-x64.exe
__H_PORT__  BoBoRemote-Portable-x64.zip
__H_UPDATE__  BoBoRemote-Update-x64.zip
```
'@
$notes = $notesTemplate.
    Replace('__TAG__', $Tag).
    Replace('__REPO__', $Repository).
    Replace('__H_SETUP__', $hSetup).
    Replace('__H_PORT__', $hPort).
    Replace('__H_UPDATE__', $hUpdate)
[System.IO.File]::WriteAllText($notesPath, $notes, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "=== Publishing GitHub Release $Tag ==="
$exists = $false
try {
    gh release view $Tag --repo $Repository >$null 2>&1
    if ($LASTEXITCODE -eq 0) { $exists = $true }
} catch {}

if ($exists) {
    gh release edit $Tag --repo $Repository --title "BoBo Remote $Tag" --notes-file $notesPath --latest
    if ($LASTEXITCODE -ne 0) { throw "gh release edit failed ($LASTEXITCODE)" }
    gh release upload $Tag $outSetup $outPort $outUpdate $sumsPath --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
} else {
    gh release create $Tag $outSetup $outPort $outUpdate $sumsPath --repo $Repository --title "BoBo Remote $Tag" --notes-file $notesPath --latest
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
}
Write-Host "OK: Release $Tag published"
