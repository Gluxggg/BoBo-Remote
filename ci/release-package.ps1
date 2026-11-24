<#
.SYNOPSIS
    GitHub Actions Release 打包、校验与发布脚本（UTF-8 带 BOM，避免 PowerShell 5.1 解析中文出错）。
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
Write-Host "=== Building BoBo Remote $Tag (version $ver) ==="
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -ReleaseVersion $ver
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed ($LASTEXITCODE)" }

Write-Host '=== Running self-tests on Launcher & Setup EXE ==='
$mainExe = Join-Path $repoRoot 'dist\BoBo远程-Windows-x64\BoBo远程.exe'
$setupExe = Join-Path $repoRoot 'dist\BoBoRemote-Setup-x64.exe'
$portableZip = Join-Path $repoRoot 'dist\BoBo远程-Windows-x64.zip'

$p1 = Start-Process -FilePath $mainExe -ArgumentList '--self-test' -Wait -PassThru
Write-Host "Launcher self-test exit code: $($p1.ExitCode)"
if ($p1.ExitCode -ne 0) { throw "BoBo远程.exe --self-test failed ($($p1.ExitCode))" }
$p2 = Start-Process -FilePath $setupExe -ArgumentList '--self-test' -Wait -PassThru
Write-Host "Setup self-test exit code: $($p2.ExitCode)"
if ($p2.ExitCode -ne 0) { throw "BoBoRemote-Setup-x64.exe --self-test failed ($($p2.ExitCode))" }

Write-Host '=== Building & self-testing V5 Native D3D11VA Core ==='
powershell -NoProfile -ExecutionPolicy Bypass -File .\native\bobo-stream-player\build-native.ps1 -RunSelfTest
if ($LASTEXITCODE -ne 0) { throw "build-native.ps1 failed ($LASTEXITCODE)" }
$v5Exe = Join-Path $repoRoot 'native\bobo-stream-player\out\BoBoStreamPlayer.exe'

$assetDir = Join-Path $repoRoot 'release-assets'
if (Test-Path -LiteralPath $assetDir) { Remove-Item -LiteralPath $assetDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

$outSetup = Join-Path $assetDir 'BoBoRemote-Setup-x64.exe'
$outPort  = Join-Path $assetDir 'BoBoRemote-Portable-x64.zip'
$outV5    = Join-Path $assetDir 'BoBoStreamPlayer-v5-core-x64.exe'
Copy-Item -LiteralPath $setupExe -Destination $outSetup -Force
Copy-Item -LiteralPath $portableZip -Destination $outPort -Force
Copy-Item -LiteralPath $v5Exe -Destination $outV5 -Force

$hSetup = (Get-FileHash -LiteralPath $outSetup -Algorithm SHA256).Hash.ToLower()
$hPort  = (Get-FileHash -LiteralPath $outPort -Algorithm SHA256).Hash.ToLower()
$hV5    = (Get-FileHash -LiteralPath $outV5 -Algorithm SHA256).Hash.ToLower()

$sumsPath = Join-Path $assetDir 'SHA256SUMS.txt'
$sumsText = "$hSetup  BoBoRemote-Setup-x64.exe`n$hPort  BoBoRemote-Portable-x64.zip`n$hV5  BoBoStreamPlayer-v5-core-x64.exe`n"
[System.IO.File]::WriteAllText($sumsPath, $sumsText, (New-Object System.Text.UTF8Encoding($false)))

$notesPath = Join-Path $assetDir 'release-notes.md'
$fence = '```'
$notes = @"
## BoBo Remote $Tag (Windows x64)

### 推荐下载
- **`BoBoRemote-Setup-x64.exe`（标准图形化安装包，像 UU 远程一样一键安装）**：
  - 冰川浅蓝向导界面 + 圆角方框奶牛猫图标，内置 SHA-256 完整性自校验；
  - 默认安装至 `%LOCALAPPDATA%\BoBoRemote`（免 UAC 弹窗，支持自定义目录）；
  - 自动创建**桌面快捷方式「BoBo Remote」**、**开始菜单入口**与 Windows **「应用和功能」卸载项**；
  - 覆盖升级时自动保留原有 Moonlight 本地配对记录；
  - 首次打开自动运行 **7 步灰白框选 ClearType 新手引导**，串流时自动停靠 **V5 顶部悬浮工具条**（实时切画质 / 防窥壁纸 / 性能浮层 / 一键断开）。
- **`BoBoRemote-Portable-x64.zip`（绿色免安装便携版）**：解压即可运行。
- **`BoBoStreamPlayer-v5-core-x64.exe`（V5 原生 C++17 D3D11VA 播放核心预览，~300 KB）**：可运行 `--self-test` / `--probe-gpu` 检验本机 D3D11VA 硬件解码能力。

### 开箱即用与可选进阶说明
- **下载双击即用（零额外安装）**：局域网 2K 高帧串流（内置 Foundation Sunshine + Moonlight V+ 与 4 位配对码）、跨外网 RustDesk 9 位码临时协助与文件互传、软件级物理屏防窥壁纸与锁屏守护（`Ctrl+Alt+Shift+P`）、串流顶部悬浮工具条及 7 步新手引导均已全部内置。
- **可选进阶能力（按需额外搭配）**：
  - **跨外网 2K 高帧低延迟串流**：需两台电脑安装同一异地组网工具（如 Tailscale / EasyTier），或具备公网 IPv6 直连条件；
  - **硬件级彻底熄屏防窥（关闭物理显示器信号）与虚拟屏**：需在被控端额外搭配 **Zako 虚拟屏驱动** 与 **系统安装版 Foundation Sunshine**。
- **首次运行提示**：未购买商业 EV 代码签名，Windows SmartScreen 首次提示时点击「更多信息 → 仍要运行」即可（可先核对下方 SHA-256）；首次被控开启局域网串流若弹出 Windows 防火墙提示，请点击「允许访问」。

### SHA-256 校验值
${fence}text
$hSetup  BoBoRemote-Setup-x64.exe
$hPort  BoBoRemote-Portable-x64.zip
$hV5  BoBoStreamPlayer-v5-core-x64.exe
${fence}

### 开源许可与上游源码对应说明
- BoBo Remote 启动器、图形化安装向导与脚本以 **MIT** 许可发布。
- 随发行包封装的上游组件以独立进程方式调用、未修改上游源码，各自保留原许可（完整哈希、许可文本与源码说明见 [`NOTICE.md`](https://github.com/$Repository/blob/main/NOTICE.md) 与 [`THIRD-PARTY.md`](https://github.com/$Repository/blob/main/THIRD-PARTY.md)）：
  - RustDesk `1.5.3` (AGPL-3.0): https://github.com/rustdesk/rustdesk/tree/1.5.3
  - Moonlight `6.2.0` (GPL-3.0): https://github.com/moonlight-stream/moonlight-qt/tree/v6.2.0
  - Moonlight V+ `6.5.6` (GPL-3.0): https://github.com/qiin2333/moonlight-qt/tree/v6.5.6
  - Foundation Sunshine `v2026.1123.185102` (GPL-3.0, NotSigned): https://github.com/AlkaidLab/foundation-sunshine/tree/v2026.1123.185102.%E6%9D%82%E9%B1%BC
"@
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
    gh release upload $Tag $outSetup $outPort $outV5 $sumsPath --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
} else {
    gh release create $Tag $outSetup $outPort $outV5 $sumsPath --repo $Repository --title "BoBo Remote $Tag" --notes-file $notesPath --latest
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
}
Write-Host "OK: Release $Tag published"
