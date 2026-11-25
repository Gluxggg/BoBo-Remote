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
## BoBo Remote __TAG__ (Windows x64 · V6 全内置单文件架构)

### 核心升级亮点（彻底解决外置内核独立连控与笔记本配对码问题）
- **全部内核写入单一软件，彻底移除所有独立外置可执行内核**：
  - 不再包含 `engines\Sunshine\sunshine.exe`、`engines\Moonlight\Moonlight.exe`、`engines\MoonlightVPlus\Moonlight.exe`、`engines\RustDesk\rustdesk.exe` 或独立 `BoBoStreamPlayer.exe`；
  - DXGI/GDI 屏幕采编、Media Foundation H.264 硬件编码/D3D11VA 硬解渲染、WASAPI 回环与播放音频、键鼠 Unicode 注入、TLS 1.2 专属协议与文件传输全部内嵌进 `BoBo远程.exe`（通过进程内原生库 `BoBoCore.dll` 唯一加载，无法脱离软件独立运行），整包体积从 ~250 MB 瘦身至 **< 1 MB**！
- **严格双端必开 + 双向加密授权（彻底杜绝未开 BoBo远程 被连控）**：
  - 使用专属端口 `49810`（TLS 1.2）与局域网 UDP `49812` 自动发现，与任何第三方 Sunshine (`47989`) 彻底隔离；
  - 必须**控制端与被控端双方同时打开 BoBo远程** 且完成 BoBo 自身 4 位临时配对码（HMAC-SHA256 双向认证）后才允许连控；
  - 退出 BoBo远程 立即关闭 `49810` 监听端口，从物理层面杜绝软件未打开时仍可被外部内核连控的隐患。
- **台式机 & 笔记本零毫秒 100% 生成 4 位临时配对码**：
  - 配对码全内存加密随机即时生成，不再依赖外置 Sunshine WebUI/令牌轮询，每台电脑即点即出，有效期 120 秒。
- **原生全功能升级**：
  - 毫秒级（<5ms）被控端物理防窥幕布同步触发（首帧编码前即落下）；
  - 实时无缝切屏（`Ctrl+Alt+→`）、无缝热切换分辨率/帧率/码率（不黑屏不断连）、被控端扬声器实时静音/自动恢复、断开自动锁屏；
  - 内置 TLS 1.2 极速点对点文件传输（自动 SHA-256 校验，自动保存至「下载\BoBoRemote」）；
  - 保留 UU 风格完整控制中心、四边贴边隐藏可拖动方形悬浮球与锁鼠 `Ctrl+Alt` 随时释放。

### 下载说明
- **`BoBoRemote-Setup-x64.exe`（标准图形化安装包，< 1 MB）**：默认免 UAC 安装至 `%LOCALAPPDATA%\BoBoRemote`，从 V5 覆盖升级时自动清理旧版 `engines\` 与 `BoBoStreamPlayer.exe`。
- **`BoBoRemote-Portable-x64.zip`（绿色便携包，< 1 MB）**：解压后直接双击 `BoBo远程.exe` 即可运行。
- **`BoBoRemote-Update-x64.zip`（应用内增量更新包）**：供已有 V6 客户端自动检测升级。

### SHA-256 校验值
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
