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
## BoBo Remote __TAG__（低延迟传输重构版：加密 UDP+FEC、零填充硬编、0 ms 本地光标与跨网自研 ABR）

### 本次核心修复与升级
- **1. 根除硬编 CBR 99% 无效填充与静止空转（大幅降低同 Wi-Fi / 跨校园网带宽占用）**：
  - 编码器切换为低延迟 `PeakConstrainedVBR`（`AVLowLatencyMode` / `0 B-Frame` / 单参考帧 / 100 ms VBV 缓冲），并硬剥离 H.264 `NAL12` 填充字节与 `NAL9` AUD，消除 AMD 等硬编在桌面静止时的 99% 垃圾流量；
  - 用 `IMFAsyncCallback` 事件唤醒替代轮询，支持 `MF_SA_D3D11_AWARE` GPU 零拷贝 NV12 输入与无停顿动态调码率 `UpdateBitrateLive`；
  - 采集循环启用 `timeBeginPeriod(1)` 与真正 VFR 静止跳帧（保留 1 s 保活），桌面无变化时不发冗余帧。
- **2. 彻底根治“卡顿掉帧、鼠标移不动”根因**：
  - 被控端引入**高优先级控制发送队列**，非阻塞回写 `PONG` 与光标事件，彻底解决视频拥塞时 `PONG` 阻塞读循环导致所有键鼠事件停读的问题；
  - 主控端将 `GetVideoViewportRect()` 改为原子无锁读取，拆出独立解码渲染线程（积压自动追帧）并设置 DXGI `SetMaximumFrameLatency(1)`，`BoBoClientNativeHandle` 改为读写锁避免阻塞 UI 鼠标输入；
  - 自由鼠标模式下默认启用 **GPU 零拷贝 + 0 ms 本地原生指针渲染**（自动同步 14 种系统光标形态，切锁鼠模式自动热切回主机光标合成）。
- **3. 加密低延迟 UDP+FEC 通道与自研跨网 ABR 拥塞控制**：
  - TLS 1.2 握手自动协商会话密钥后无感升级为 `AES-128-CTR + HMAC-SHA256` 加密 UDP 通道（端口 `49811`，`<=1140 B` 分片避免 Tailscale/校园网 IP 分片），配合分组 XOR-FEC 丢包 0 ms 当场恢复；
  - 键鼠移动 4 ms 合并 + 尾帧补发，点击/按键原子附带最新坐标并冗余重传最近 3 条离散事件（64 序号幂等去重不双触发）；UDP 受限时自动无感回落优化后的 TCP+TLS 通道（收紧缓冲防 Bufferbloat、抑制 IDR 风暴、音频队列封顶）；
  - 跨网高 RTT 自动保守起流并按实测丢包/排队延迟动态升降码率与帧率；超级屏增加智能像素预算上限；HUD 实时显示传输协议（`UDP+FEC` / `TCP+TLS`）、丢包率与实时码率。
- **4. 多网卡定向广播与同 Wi-Fi 自动局域网直连**：
  - 遍历所有非虚拟网卡定向广播并上报 `lanIps`，同 Wi-Fi 下即使从 Tailscale 地址发起连接也会自动探测并切换为高速局域网直连，绕开 DERP 中继；
  - 四端统一同步至 `__TAG__`，保持 BoBo6.2 协议跨端字节级兼容与全链路自动化自检通过。

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
