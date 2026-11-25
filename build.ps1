<#
.SYNOPSIS
    BoBo远程 V6.0 全内置单文件 Windows x64 构建脚本。

.DESCRIPTION
    全程四步（零外置 Sunshine / Moonlight / RustDesk / 独立播放核）：
    1. 编译并自检内嵌原生内核 native\bobo-core\out\BoBoCore.dll（压成 BoBoCore.dll.gz）；
    2. 使用系统自带 .NET 4.8 csc.exe 编译 BoBo远程.exe，并将 BoBoCore.dll.gz 直接嵌入主程序资源；
    3. 组装干净单文件发布目录（校验不存在任何其它 .exe 或 engines\ 目录）；
    4. 输出 dist\BoBo远程-Windows-x64、便携 ZIP、轻量更新包与标准图形安装包 BoBoRemote-Setup-x64.exe。

.NOTES
    本文件保存为 UTF-8 带 BOM（适配 Windows PowerShell 5.1）。
#>
[CmdletBinding()]
param(
    [switch]$FetchOnly,
    [switch]$SkipFetch,
    [switch]$NoPublish,
    [string]$SourceDir = 'src',
    [string]$OutputDir = '',
    [string]$ReleaseVersion = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = (Get-Location).Path }
if (-not [string]::IsNullOrWhiteSpace($OutputDir) -and -not [System.IO.Path]::IsPathRooted($OutputDir)) {
    throw '-OutputDir 必须是显式绝对路径；默认发布目录才是项目下 dist。'
}

$BuildTimestamp = Get-Date
$BuildVersion = if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) { '6.2.2' } else { $ReleaseVersion.Trim().TrimStart('v', 'V') }

$BuildDir       = Join-Path $Root '.cache\build'
$StageRoot      = Join-Path $BuildDir 'clean-stage'
$PackageDir     = Join-Path $StageRoot 'BoBo远程'
$DistDir        = if ([string]::IsNullOrWhiteSpace($OutputDir)) { Join-Path $Root 'dist' } else { [System.IO.Path]::GetFullPath($OutputDir) }
$LauncherOut    = Join-Path $BuildDir 'BoBo远程.exe'
$CoreScriptPath = Join-Path $Root 'native\bobo-core\build-core.ps1'
$CoreGzPath     = Join-Path $Root 'native\bobo-core\out\BoBoCore.dll.gz'
$ZipPath        = Join-Path $DistDir 'BoBo远程-Windows-x64.zip'
$LightZipPath   = Join-Path $DistDir 'BoBo远程-轻量更新包.zip'
$SetupExePath   = Join-Path $DistDir 'BoBoRemote-Setup-x64.exe'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step { param([string]$Text) Write-Host ''; Write-Host "== $Text ==" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "  [通过] $Text" -ForegroundColor Green }
function Write-Info { param([string]$Text) Write-Host "  $Text" }
function Get-FileSha256 { param([string]$LiteralPath) return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash }

function Find-CscExe {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw "未找到系统自带 csc.exe：$compiler" }
    return [pscustomobject]@{ Compiler = $compiler; Framework = $framework }
}

function Invoke-CoreBuildStage {
    Write-Step '第 1 步：编译并自检内嵌原生内核 BoBoCore.dll'
    if (-not (Test-Path -LiteralPath $CoreScriptPath)) { throw "缺少原生核构建脚本：$CoreScriptPath" }
    powershell -NoProfile -ExecutionPolicy Bypass -File $CoreScriptPath -RunSelfTest
    if ($LASTEXITCODE -ne 0) { throw "BoBoCore.dll 编译或自检失败（exit=$LASTEXITCODE）" }
    if (-not (Test-Path -LiteralPath $CoreGzPath)) { throw "未生成内嵌压缩内核：$CoreGzPath" }
    $gzLen = (Get-Item -LiteralPath $CoreGzPath).Length
    Write-Ok "BoBoCore.dll.gz 就绪（$gzLen 字节，SHA256 $(Get-FileSha256 $CoreGzPath)）"
}

function Invoke-CompileStage {
    Write-Step '第 2 步：编译单文件 BoBo远程.exe（内嵌 BoBoCore.dll.gz）'
    $csc = Find-CscExe
    $srcPath = Join-Path $Root $SourceDir
    $sources = @(Get-ChildItem -LiteralPath $srcPath -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '\\_tmp\\' } |
        Select-Object -ExpandProperty FullName)
    if ($sources.Count -eq 0) { throw "在 $srcPath 下未找到任何 .cs 源码" }

    $manifest = Join-Path $srcPath 'app.manifest'
    if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少清单文件：$manifest" }

    New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
    if (Test-Path -LiteralPath $LauncherOut) { Remove-Item -LiteralPath $LauncherOut -Force }

    $verFile = Join-Path $BuildDir 'app-version.txt'
    [System.IO.File]::WriteAllText($verFile, $BuildVersion, (New-Object System.Text.UTF8Encoding($false)))

    $refNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')
    $refs = @($refNames | ForEach-Object { Join-Path $csc.Framework $_ })

    $appIcon = Join-Path $Root 'assets\bobo-remote.ico'
    $brandLogo = Join-Path $Root 'assets\bobo-logo.png'
    $displayTool = Join-Path $Root 'tools\Set-SunshineDisplay.ps1'
    $optimizeTool = Join-Path $Root 'tools\Optimize-SunshineStream.ps1'

    $argsList = @(
        '/nologo',
        '/target:winexe',
        '/platform:x64',
        '/optimize+',
        '/warn:4',
        '/codepage:65001',
        '/utf8output',
        "/out:$LauncherOut",
        "/win32manifest:$manifest",
        "/reference:$($refs -join ',')",
        "/resource:$CoreGzPath,DualRemote.BoBoCore.dll.gz",
        "/resource:$verFile,DualRemote.app-version.txt"
    )
    if (Test-Path -LiteralPath $appIcon) { $argsList += "/win32icon:$appIcon" }
    if (Test-Path -LiteralPath $brandLogo) { $argsList += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
    if (Test-Path -LiteralPath $displayTool) { $argsList += "/resource:$displayTool,DualRemote.tools.Set-SunshineDisplay.ps1" }
    if (Test-Path -LiteralPath $optimizeTool) { $argsList += "/resource:$optimizeTool,DualRemote.tools.Optimize-SunshineStream.ps1" }
    $argsList += $sources

    & $csc.Compiler @argsList
    if ($LASTEXITCODE -ne 0) { throw "csc.exe 编译 BoBo远程.exe 失败（exit=$LASTEXITCODE）" }
    $exeLen = (Get-Item -LiteralPath $LauncherOut).Length
    Write-Ok "已编译 BoBo远程.exe（$($sources.Count) 个源码文件，$exeLen 字节，内嵌 BoBoCore）"
}

function Invoke-Stage {
    Write-Step '第 3 步：组装纯净 V6 全内置发布目录（零外置独立内核）'
    if (Test-Path -LiteralPath $StageRoot) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

    Copy-Item -LiteralPath $LauncherOut -Destination (Join-Path $PackageDir 'BoBo远程.exe') -Force

    foreach ($docName in @('README.md', 'LICENSE', '低延迟调优与防窥指南.md')) {
        $docSrc = Join-Path $Root $docName
        if (Test-Path -LiteralPath $docSrc) {
            Copy-Item -LiteralPath $docSrc -Destination (Join-Path $PackageDir $docName) -Force
        }
    }

    $buildInfo = @(
        "BoBo Remote Windows x64 (V6 All-In-One)"
        "Version: $BuildVersion"
        "BuiltUtc: $($BuildTimestamp.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
        "Architecture: Single-App Embedded BoBoCore.dll (DXGI/GDI + MF H.264 + D3D11VA + WASAPI + TLS 1.2 Port 49810)"
        "StandaloneExternalEngines: None (0)"
        "MainExeSha256: $(Get-FileSha256 (Join-Path $PackageDir 'BoBo远程.exe'))"
    ) -join "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $PackageDir 'BUILD-INFO.txt'), $buildInfo, (New-Object System.Text.UTF8Encoding($false)))

    # Strict architectural gate: ONLY BoBo远程.exe may exist as an executable; zero engines\ folder!
    $allExes = @(Get-ChildItem -LiteralPath $PackageDir -Recurse -File -Filter '*.exe')
    if ($allExes.Count -ne 1 -or $allExes[0].Name -ne 'BoBo远程.exe') {
        throw "V6 纯净校验失败：发布目录中存在多余的可执行文件：$($allExes.FullName -join ', ')"
    }
    if (Test-Path -LiteralPath (Join-Path $PackageDir 'engines')) {
        throw 'V6 纯净校验失败：不应存在 engines 目录'
    }
    Write-Ok "发布目录纯净校验通过：仅含唯一可执行程序 BoBo远程.exe（零外置独立内核）"
}

function Build-SetupInstaller {
    param([string]$ZipSource, [string]$OutputExe)

    $setupSrc = Join-Path $Root 'installer\BoBoSetup.cs'
    if (-not (Test-Path -LiteralPath $setupSrc)) { throw "缺少安装向导源码：$setupSrc" }

    $csc = Find-CscExe
    $setupBuildDir = Join-Path $BuildDir 'setup-build'
    New-Item -ItemType Directory -Force -Path $setupBuildDir | Out-Null

    $stubExe = Join-Path $setupBuildDir 'BoBoSetupStub.exe'
    if (Test-Path -LiteralPath $stubExe) { Remove-Item -LiteralPath $stubExe -Force }

    $verFile = Join-Path $setupBuildDir 'setup-version.txt'
    [System.IO.File]::WriteAllText($verFile, $BuildVersion, (New-Object System.Text.UTF8Encoding($false)))

    $manifest = Join-Path $Root 'src\app.manifest'
    $appIcon  = Join-Path $Root 'assets\bobo-remote.ico'
    $brandLogo = Join-Path $Root 'assets\bobo-logo.png'
    $setupRefs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll') |
        ForEach-Object { Join-Path $csc.Framework $_ }

    $setupArgs = @(
        '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/warnaserror+',
        '/codepage:65001', '/utf8output', "/out:$stubExe", "/win32manifest:$manifest",
        "/reference:$($setupRefs -join ',')",
        "/resource:$verFile,DualRemote.setup-version.txt"
    )
    if (Test-Path -LiteralPath $appIcon) { $setupArgs += "/win32icon:$appIcon" }
    if (Test-Path -LiteralPath $brandLogo) { $setupArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
    $setupArgs += $setupSrc

    & $csc.Compiler @setupArgs
    if ($LASTEXITCODE -ne 0) { throw "编译安装向导 BoBoSetup.cs 失败（exit=$LASTEXITCODE）" }

    $tmpSetup = Join-Path $setupBuildDir 'BoBoRemote-Setup-x64.exe.tmp'
    if (Test-Path -LiteralPath $tmpSetup) { Remove-Item -LiteralPath $tmpSetup -Force }

    $stubBytes = [System.IO.File]::ReadAllBytes($stubExe)
    $payloadOffset = [long]$stubBytes.Length
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $outStream = [System.IO.File]::Create($tmpSetup)
    try {
        $outStream.Write($stubBytes, 0, $stubBytes.Length)
        $zipStream = [System.IO.File]::OpenRead($ZipSource)
        try {
            $payloadLength = [long]$zipStream.Length
            $buf = New-Object byte[] (1024 * 1024)
            while ($true) {
                $n = $zipStream.Read($buf, 0, $buf.Length)
                if ($n -le 0) { break }
                $outStream.Write($buf, 0, $n)
                [void]$sha.TransformBlock($buf, 0, $n, $null, 0)
            }
            [void]$sha.TransformFinalBlock((New-Object byte[] 0), 0, 0)
            $hashBytes = $sha.Hash

            $trailer = New-Object byte[] 64
            $m1 = [System.Text.Encoding]::ASCII.GetBytes('BOBOSET1')
            $m2 = [System.Text.Encoding]::ASCII.GetBytes('END1')
            [Array]::Copy($m1, 0, $trailer, 0, 8)
            [Array]::Copy([BitConverter]::GetBytes($payloadOffset), 0, $trailer, 8, 8)
            [Array]::Copy([BitConverter]::GetBytes($payloadLength), 0, $trailer, 16, 8)
            [Array]::Copy($hashBytes, 0, $trailer, 24, 32)
            [Array]::Copy([BitConverter]::GetBytes([int]1), 0, $trailer, 56, 4)
            [Array]::Copy($m2, 0, $trailer, 60, 4)
            $outStream.Write($trailer, 0, 64)
        } finally {
            $zipStream.Dispose()
        }
    } finally {
        $outStream.Dispose()
        $sha.Dispose()
    }

    Move-Item -LiteralPath $tmpSetup -Destination $OutputExe -Force
    Write-Ok "标准安装包：dist\$(Split-Path -Leaf $OutputExe)（$((Get-Item -LiteralPath $OutputExe).Length) 字节）"
}

function Invoke-Publish {
    Write-Step '第 4 步：输出到 dist（便携目录、便携 ZIP、增量更新包与图形安装包）'
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

    $releaseDir = Join-Path $DistDir 'BoBo远程-Windows-x64'
    if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    Get-ChildItem -LiteralPath $PackageDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $releaseDir -Recurse -Force
    }
    Write-Ok "最新解压目录：$releaseDir"

    # Full portable ZIP (root folder BoBo远程\)
    $fullTmp = Join-Path $BuildDir 'BoBo远程-Windows-x64.zip.tmp'
    if (Test-Path -LiteralPath $fullTmp) { Remove-Item -LiteralPath $fullTmp -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($StageRoot, $fullTmp, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $fullTmp -Destination $ZipPath -Force
    Write-Ok "完整便携包：dist\BoBo远程-Windows-x64.zip（$((Get-Item -LiteralPath $ZipPath).Length) 字节）"

    # Light update ZIP (flat files inside root for AppUpdater delta apply)
    $lightTmp = Join-Path $BuildDir 'BoBo远程-轻量更新包.zip.tmp'
    if (Test-Path -LiteralPath $lightTmp) { Remove-Item -LiteralPath $lightTmp -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($PackageDir, $lightTmp, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $lightTmp -Destination $LightZipPath -Force
    Write-Ok "轻量更新包：dist\BoBo远程-轻量更新包.zip（$((Get-Item -LiteralPath $LightZipPath).Length) 字节）"

    Build-SetupInstaller -ZipSource $ZipPath -OutputExe $SetupExePath
    return $releaseDir
}

try {
    Write-Host 'BoBo远程 V6.0 · 全内置单文件 Windows x64 构建' -ForegroundColor Cyan
    Write-Info "项目目录：$Root"
    Write-Info "发布版本：$BuildVersion"

    if (-not [Environment]::Is64BitOperatingSystem) { throw '本脚本只支持 64 位 Windows。' }
    if ($FetchOnly) {
        Write-Ok 'V6 全内置架构无需下载任何第三方外置引擎，直接跳过 FetchOnly。'
        exit 0
    }

    Invoke-CoreBuildStage
    Invoke-CompileStage
    Invoke-Stage
    if ($NoPublish) {
        Write-Ok "干净组装完成（未发布）：$PackageDir"
        exit 0
    }
    $releaseDir = Invoke-Publish

    Write-Step '构建完成'
    $files = @(Get-ChildItem -LiteralPath $releaseDir -Recurse -File)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Info "最新解压目录：$releaseDir（$($files.Count) 个文件，$([Math]::Round($bytes / 1KB, 1)) KiB）"
    foreach ($p in @($SetupExePath, $ZipPath, $LightZipPath)) {
        if (Test-Path -LiteralPath $p) {
            $fi = Get-Item -LiteralPath $p
            Write-Info "  $(Split-Path -Leaf $p)：$($fi.Length) 字节，SHA256 $(Get-FileSha256 $p)"
        }
    }
} catch {
    Write-Host ''
    Write-Host "构建失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
