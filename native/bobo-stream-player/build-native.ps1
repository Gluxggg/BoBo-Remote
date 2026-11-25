<#
.SYNOPSIS
    构建 V5 原生一体化 C++17 D3D11VA + moonlight-common-c 串流播放核心（BoBoStreamPlayer.exe）。

.DESCRIPTION
    自动检测本机 Visual Studio 2022 / 2019 MSVC（cl.exe / lib.exe）与 Windows 10/11 SDK，
    先将随仓库的 moonlight-common-c + enet + nanors + Windows CNG BCrypt 加密层编译为静态库，
    再以 /W4 /WX 编译零 Qt6、零 OpenSSL 依赖的原生 Direct3D 11 + Media Foundation D3D11VA + WASAPI 播放器。
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
    'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools',
    'D:\BuildTools\VS2019'
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
$libExe = Join-Path $binDir 'lib.exe'
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

$mcRoot = Join-Path (Split-Path -Parent $root) 'third_party\moonlight-common-c'
$hasMoonlightC = Test-Path -LiteralPath (Join-Path $mcRoot 'src\Limelight.h')

$prevInclude = $env:INCLUDE
$prevLib = $env:LIB
$prevPath = $env:PATH
try {
    $env:INCLUDE = ($incPaths -join ';')
    $env:LIB = ($libPaths -join ';')
    $env:PATH = "$binDir;$prevPath"

    Write-Host "MSVC: $msvcDir"
    Write-Host "WinSDK: $sdkVer"
    Write-Host "moonlight-common-c: $hasMoonlightC"

    $exeOut = Join-Path $outDir 'BoBoStreamPlayer.exe'
    $objDir = Join-Path $outDir 'obj'
    $cObjDir = Join-Path $outDir 'obj-mc'
    New-Item -ItemType Directory -Force -Path $objDir | Out-Null
    New-Item -ItemType Directory -Force -Path $cObjDir | Out-Null

    $extraIncArgs = @()
    $extraDefArgs = @()
    $extraLibs = @()

    Push-Location -LiteralPath $root
    try {
        if ($hasMoonlightC) {
            # Build moonlight-common-c + enet + nanors + Windows CNG BCrypt backend into static library
            $mcLibOut = Join-Path $outDir 'moonlight_core.lib'
            $cSources = @(
                '..\third_party\moonlight-common-c\enet\callbacks.c',
                '..\third_party\moonlight-common-c\enet\compress.c',
                '..\third_party\moonlight-common-c\enet\host.c',
                '..\third_party\moonlight-common-c\enet\list.c',
                '..\third_party\moonlight-common-c\enet\packet.c',
                '..\third_party\moonlight-common-c\enet\peer.c',
                '..\third_party\moonlight-common-c\enet\protocol.c',
                '..\third_party\moonlight-common-c\enet\win32.c',
                '..\third_party\moonlight-common-c\nanors\rs.c',
                '..\third_party\moonlight-common-c\nanors\deps\obl\oblas_common.c',
                '..\third_party\moonlight-common-c\nanors\deps\obl\oblas_lite.c',
                '..\third_party\moonlight-common-c\src\AudioStream.c',
                '..\third_party\moonlight-common-c\src\ByteBuffer.c',
                '..\third_party\moonlight-common-c\src\Connection.c',
                '..\third_party\moonlight-common-c\src\ConnectionTester.c',
                '..\third_party\moonlight-common-c\src\ControlStream.c',
                '..\third_party\moonlight-common-c\src\FakeCallbacks.c',
                '..\third_party\moonlight-common-c\src\InputStream.c',
                '..\third_party\moonlight-common-c\src\LinkedBlockingQueue.c',
                '..\third_party\moonlight-common-c\src\Misc.c',
                '..\third_party\moonlight-common-c\src\Platform.c',
                '..\third_party\moonlight-common-c\src\PlatformSockets.c',
                '..\third_party\moonlight-common-c\src\RecorderCallbacks.c',
                '..\third_party\moonlight-common-c\src\RtpAudioQueue.c',
                '..\third_party\moonlight-common-c\src\RtpVideoQueue.c',
                '..\third_party\moonlight-common-c\src\RtspConnection.c',
                '..\third_party\moonlight-common-c\src\RtspParser.c',
                '..\third_party\moonlight-common-c\src\SdpGenerator.c',
                '..\third_party\moonlight-common-c\src\SimpleStun.c',
                '..\third_party\moonlight-common-c\src\VideoDepacketizer.c',
                '..\third_party\moonlight-common-c\src\VideoFormats.c',
                '..\third_party\moonlight-common-c\src\VideoStream.c',
                'src\platform_crypto_win32.c'
            )

            $cFoArg = if ($cObjDir -match '\s') { "/Fo$cObjDir\\" } else { "/Fo$cObjDir\" }
            $cArgs = @(
                '/nologo', '/c', '/O2', '/W3', '/wd4005', '/wd4028', '/wd4100', '/wd4142', '/wd4232', '/wd4244', '/wd4267', '/wd4996', '/wd5105',
                '/DNDEBUG', '/DHAS_SOCKLEN_T', '/D_CRT_SECURE_NO_WARNINGS', '/D_WINSOCK_DEPRECATED_NO_WARNINGS',
                '/Icompat',
                '/I..\third_party\moonlight-common-c\src',
                '/I..\third_party\moonlight-common-c\enet\include',
                '/I..\third_party\moonlight-common-c\nanors',
                '/I..\third_party\moonlight-common-c\nanors\deps\obl',
                $cFoArg
            ) + $cSources

            & $clExe @cArgs
            if ($LASTEXITCODE -ne 0) { throw "moonlight-common-c compile failed with exit code $LASTEXITCODE" }

            $cObjs = @(Get-ChildItem -LiteralPath $cObjDir -Filter '*.obj' | Select-Object -ExpandProperty FullName)
            & $libExe /nologo "/OUT:$mcLibOut" @cObjs
            if ($LASTEXITCODE -ne 0) { throw "lib.exe failed with exit code $LASTEXITCODE" }

            $extraIncArgs = @('/Icompat', '/I..\third_party\moonlight-common-c\src')
            $extraDefArgs = @('/DBOBO_WITH_MOONLIGHT_COMMON_C')
            $extraLibs = @($mcLibOut, 'bcrypt.lib')
        }

        $srcRel = @(
            'src\main.cpp',
            'src\ipc_protocol.cpp',
            'src\d3d11_renderer.cpp',
            'src\mf_decoder.cpp',
            'src\wasapi_audio.cpp',
            'src\moonlight_bridge.cpp'
        )
        $foArg = if ($objDir -match '\s') { "/Fo$objDir\\" } else { "/Fo$objDir\" }
        $feArg = "/Fe:$exeOut"

        $clArgs = @(
            '/nologo', '/std:c++17', '/EHsc', '/O2', '/W4', '/WX', '/utf-8',
            $foArg,
            $feArg
        ) + $extraDefArgs + $extraIncArgs + $srcRel + @(
            '/link',
            'd3d11.lib', 'dxgi.lib', 'mfplat.lib', 'mfuuid.lib', 'mf.lib',
            'strmiids.lib', 'ole32.lib', 'user32.lib', 'ws2_32.lib', 'winmm.lib'
        ) + $extraLibs

        & $clExe @clArgs
        if ($LASTEXITCODE -ne 0) { throw "MSVC compile failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
} finally {
    $env:INCLUDE = $prevInclude
    $env:LIB = $prevLib
    $env:PATH = $prevPath
}

$size = (Get-Item -LiteralPath $exeOut).Length
Write-Host "Built: $exeOut ($size bytes)"

if ($RunSelfTest) {
    $testExe = $exeOut
    $tmpDir = $null
    try {
        $p = Start-Process -FilePath $testExe -ArgumentList '--self-test' -Wait -PassThru -NoNewWindow
        if ($p.ExitCode -eq -1073741502) {
            # 目录带有 Low Mandatory Level（例如部分非系统盘）时 Windows 禁止直接初始化 USER32/D3D11；复制到标准 TEMP 目录验证
            $tmpDir = Join-Path $env:TEMP ('bobo-native-selftest-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
            $testExe = Join-Path $tmpDir 'BoBoStreamPlayer.exe'
            Copy-Item -LiteralPath $exeOut -Destination $testExe -Force
            $p = Start-Process -FilePath $testExe -ArgumentList '--self-test' -Wait -PassThru -NoNewWindow
        }
        if ($p.ExitCode -ne 0) { throw "BoBoStreamPlayer --self-test failed with exit code $($p.ExitCode)" }
    } finally {
        if ($tmpDir -and (Test-Path -LiteralPath $tmpDir)) {
            Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
