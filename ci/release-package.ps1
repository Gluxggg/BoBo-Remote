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
## BoBo Remote __TAG__（四端互通稳定增强版：鼠标低延迟、光标合成与防窥输入加固）

### 本次核心修复与升级
- **1. 鼠标高频移动合并与 TLS 小包单帧写入（显著降低操控延迟）**：
  - 控制端 FIFO 发送队列自动合并高频绝对/相对鼠标移动事件，并在点击、滚轮、按键与控制指令前严格冲刷保序，消除快速移动或拖拽时的输入堆积；
  - `<= 4096 B` 的输入事件、控制 JSON 与音频帧在协议层合并为单次 TLS 记录写入，大幅减少小包握手与系统调用开销；
  - 锁定鼠标模式下兼容笔记本 Precision Touchpad / 笔设备的绝对 Raw Input，并提供 `RegisterRawInputDevices` 失败时的相对位移回退。
- **2. 主机光标合成协商与无头/超级屏光标兜底**：
  - 被控端在 `STREAM_STARTED` 中显式声明 `hostCursorComposited`，控制端仅在确认远端已合成主机光标且首帧画面到达后再隐藏本机指针，避免双光标或光标消失；
  - 修复无头桌面、虚拟显示器或超级屏切换分辨率后系统清除 `CURSOR_SHOWING` 导致远端看不到鼠标的问题，自动保持主机指针可见并内置清晰箭头光标回退。
- **3. 防窥低级输入钩子与 UIPI 兼容性加固**：
  - 防窥模式下的 `WH_MOUSE_LL` / `WH_KEYBOARD_LL` 改用 `BoBoCore.dll` 自身模块句柄挂载，使用 32 位安全注入魔数并兼容第三方钩子剥离 `dwExtraInfo` 时的短窗口放行；
  - 当被控端前台为管理员提权窗口受 UIPI 拦截导致 `SendInput` 失效时自动回退 `SetCursorPos`，同时缓存显示器枚举（250 ms TTL，超级屏切换时即时失效）；
  - 修复首帧视频到达后异步状态回调偶发重新覆写「正在加载实时画面」浮层的竞态问题。
- **4. 四端完整同步（Windows / Android / macOS / iOS）**：
  - 统一升级至 `__TAG__`，保持 BoBo6.2 协议（TCP+TLS 1.2 :49810 / LAN UDP :49812）四端字节级互通与全链路自检通过。

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
