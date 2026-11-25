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
## BoBo Remote __TAG__ (Windows x64 · V6.1 全内置单文件架构)

### 本次核心升级亮点
- **1. 框内主机小指针 · 框外本机大鼠标无缝分离**：
  - 被控端内置原生主机光标实时合成（支持彩色透明、阴影及反色 I-Beam 光标，DXGI 与 GDI 路径均精确合成，静态桌面移动光标亦实时刷新）；
  - 控制端视口进入视频画面区域瞬间零闪烁隐藏本机大鼠标，仅呈现被控端原生小鼠标指针；移出画面框（顶栏、悬浮球、控制中心、窗口外或副屏）瞬间无缝切回本机大鼠标点击，彻底告别双光标重叠；
  - 修正边缘像素坐标精度及点击瞬间坐标同步，锁鼠模式依然支持 `Ctrl+Alt` 瞬间释放。
- **2. UU 同款「超级屏」全屏自适应模式（彻底消除笔记本上下黑边）**：
  - 默认开启超级屏全屏自适应（`scaleMode=2`），在 16:10（如 2880×1800）等异形屏笔记本全屏远控 16:9 台式机时自动消除上下黑边；
  - 新增 `SET_SUPER_DISPLAY` / `SUPER_DISPLAY_STATE` 协议闭环：优先尝试以无闪烁临时模式（`CDS_FULLSCREEN`）匹配被控端原生同比例高分辨率（低于 1440×900 自动保护不降质），未匹配时客户端以 GPU SuperFill 满屏无黑边呈现且鼠标点击 1:1 精准对应，断开/退出自动还原被控端分辨率；
  - 控制中心「显示屏」新增「切换使用超级屏」，「窗口」菜单提供「全屏自适应（无黑边）/ 等比居中 / 实际大小 1:1」三档实时切换。
- **3. 被控端真·物理级完全防窥保护（防任务栏/开始菜单/右键刷新/打字外泄）**：
  - **原生低级物理键鼠锁（`WH_MOUSE_LL` + `WH_KEYBOARD_LL`）**：防窥开启瞬间 100% 阻断被控端现场物理鼠标（点击任务栏、开始菜单、右键刷新、悬停）与物理键盘输入，仅精准放行带 BoBo 专属签名（`kBoBoInjectedMagic`）的远控注入事件及机主紧急退出热键 `Ctrl+Alt+Shift+P`；
  - **DDC/CI 显示器硬件级深度熄黑（`dxva2.dll`）**：将物理显示器面板亮度/对比度/RGB 增益归零，对 DXGI 远控采集 100% 透明，彻底杜绝 Win11 开始菜单、现代右键刷新菜单及中文输入法打字候选框外泄；熄黑前落盘原始寄存器表，支持退出防窥、锁屏、退出软件及异常重启自动恢复；
  - **0ms `SetWinEventHook` Z 序守护 + 分辨率变更无缝重绘**：超级屏切分辨率时防窥锁鼠与熄黑全程不中断、不闪烁。

### 下载说明
- **`BoBoRemote-Setup-x64.exe`（标准图形化安装包，< 1 MB）**：默认免 UAC 安装至 `%LOCALAPPDATA%\BoBoRemote`，支持无缝静默覆盖升级。
- **`BoBoRemote-Portable-x64.zip`（绿色便携包，< 1 MB）**：解压后直接双击 `BoBo远程.exe` 即可运行。
- **`BoBoRemote-Update-x64.zip`（应用内增量更新包）**：供已有 V6 客户端应用内一键升级。

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
