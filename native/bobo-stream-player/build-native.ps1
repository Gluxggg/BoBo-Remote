<#
.SYNOPSIS
    构建 V5 原生 C++17 D3D11VA 串流播放核心（BoBoStreamPlayer.exe）。

.DESCRIPTION
    自动检测本机 Visual Studio 2022 / 2019 MSVC（cl.exe）与 Windows 10/11 SDK，
    直接配置 INCLUDE/LIB 并调用 cl.exe（绕过 vcvars64.bat 对系统异常 PATH 字符的兼容性问题），
    编译零 Qt6 依赖的原生 Direct3D 11 + Media Foundation D3D11VA 硬件解码播放器。
#>
[CmdletBinding()]
param(
    [string]$OutputDir = '',
    [switch]$RunSelfTest
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) { $root = (Get-Location).Path }
$outDir = if ([string]::IsNullOrWhiteSpace($OutputDir)) { Join-Path $root 'out' } else { $OutputDir }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 1. Locate MSVC toolset directory containing bin\Hostx64\x64\cl.exe
$vsRoots = New-Object System.Collections.Generic.List[string]
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    foreach ($inst in @(& $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)) {
        if (-not [string]::IsNullOrWhiteSpace($inst)) { $vsRoots.Add($inst) }
    }
}
foreach ($fallback in @(
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional',
    'C:\Program Files\Microsoft Visual Studio\2022\Community',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools',
    'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools'
)) {
    if ((Test-Path -LiteralPath $fallback) -and -not $vsRoots.Contains($fallback)) {
        $vsRoots.Add($fallback)
    }
}

$msvcDir = $null
foreach ($vs in $vsRoots) {
    $toolsRoot = Join-Path $vs 'VC\Tools\MSVC'
    if (-not (Test-Path -LiteralPath $toolsRoot)) { continue }
    $vers = @(Get-ChildItem -LiteralPath $toolsRoot -Directory | Sort-Object Name -Descending)
    foreach ($v in $vers) {
        if (Test-Path -LiteralPath (Join-Path $v.FullName 'bin\Hostx64\x64\cl.exe')) {
            $msvcDir = $v.FullName
            break
        }
    }
    if ($msvcDir) { break }
}
if (-not $msvcDir) { throw '未检测到 MSVC x64 编译器（cl.exe）。请安装 Visual Studio C++ 生成工具。' }

# 2. Locate latest Windows 10/11 SDK with d3d11.h
$kitsRoot = 'C:\Program Files (x86)\Windows Kits\10'
$incRoot = Join-Path $kitsRoot 'Include'
$sdkVer = $null
if (Test-Path -LiteralPath $incRoot) {
    foreach ($d in @(Get-ChildItem -LiteralPath $incRoot -Directory | Sort-Object Name -Descending)) {
        if ((Test-Path -LiteralPath (Join-Path $d.FullName 'um\d3d11.h')) -and
            (Test-Path -LiteralPath (Join-Path $kitsRoot "Lib\$($d.Name)\um\x64\d3d11.lib"))) {
            $sdkVer = $d.Name
            break
        }
    }
}
if (-not $sdkVer) { throw '未检测到包含 d3d11.h / d3d11.lib 的 Windows 10/11 SDK。' }

$binDir = Join-Path $msvcDir 'bin\Hostx64\x64'
$clExe = Join-Path $binDir 'cl.exe'
$incPaths = @(
    (Join-Path $msvcDir 'include'),
    (Join-Path $kitsRoot "Include\$sdkVer\ucrt"),
    (Join-Path $kitsRoot "Include\$sdkVer\shared"),
    (Join-Path $kitsRoot "Include\$sdkVer\um"),
    (Join-Path $kitsRoot "Include\$sdkVer\winrt")
)
$libPaths = @(
    (Join-Path $msvcDir 'lib\x64'),
    (Join-Path $kitsRoot "Lib\$sdkVer\ucrt\x64"),
    (Join-Path $kitsRoot "Lib\$sdkVer\um\x64")
)

$prevInclude = $env:INCLUDE
$prevLib = $env:LIB
$prevPath = $env:PATH
try {
    $env:INCLUDE = ($incPaths -join ';')
    $env:LIB = ($libPaths -join ';')
    $env:PATH = "$binDir;$prevPath"

    Write-Host "MSVC: $msvcDir"
    Write-Host "WinSDK: $sdkVer"

    $exeOut = Join-Path $outDir 'BoBoStreamPlayer.exe'
    $objDir = Join-Path $outDir 'obj\'
    New-Item -ItemType Directory -Force -Path $objDir | Out-Null

    $srcFiles = @(
        'src\main.cpp',
        'src\ipc_protocol.cpp',
        'src\d3d11_renderer.cpp',
        'src\mf_decoder.cpp',
        'src\moonlight_bridge.cpp'
    ) | ForEach-Object { Join-Path $root $_ }

    $clArgs = @(
        '/nologo', '/std:c++17', '/EHsc', '/O2', '/W4', '/WX', '/utf-8',
        "/Fo$objDir",
        "/Fe:$exeOut"
    ) + $srcFiles + @(
        '/link',
        'd3d11.lib', 'dxgi.lib', 'mfplat.lib', 'mfuuid.lib', 'mf.lib',
        'strmiids.lib', 'ole32.lib', 'user32.lib', 'ws2_32.lib', 'winmm.lib'
    )

    & $clExe @clArgs
    if ($LASTEXITCODE -ne 0) { throw "MSVC compile failed with exit code $LASTEXITCODE" }
} finally {
    $env:INCLUDE = $prevInclude
    $env:LIB = $prevLib
    $env:PATH = $prevPath
}

$size = (Get-Item -LiteralPath $exeOut).Length
Write-Host "Built: $exeOut ($size bytes)"

if ($RunSelfTest) {
    $p = Start-Process -FilePath $exeOut -ArgumentList '--self-test' -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "BoBoStreamPlayer --self-test failed with exit code $($p.ExitCode)" }
}
