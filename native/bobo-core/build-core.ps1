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

# 1. Locate MSVC x64 cl.exe
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
if (-not $msvcDir) { throw 'MSVC x64 compiler (cl.exe) not found.' }

# 2. Locate latest Windows 10/11 SDK
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
if (-not $sdkVer) { throw 'Windows 10/11 SDK with d3d11.h / d3d11.lib not found.' }

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

$coreSrcDir = Join-Path $root 'src'
$playerSrcDir = Join-Path (Split-Path -Parent $root) 'bobo-stream-player\src'
$buildTmpDir = Join-Path $env:TEMP ('bobo-core-build-' + [Guid]::NewGuid().ToString('N'))
$objDir = Join-Path $buildTmpDir 'obj'
New-Item -ItemType Directory -Force -Path $objDir | Out-Null
$tmpDllOut = Join-Path $buildTmpDir 'BoBoCore.dll'
$dllOut = Join-Path $outDir 'BoBoCore.dll'
$gzOut = Join-Path $outDir 'BoBoCore.dll.gz'

$prevInclude = $env:INCLUDE
$prevLib = $env:LIB
$prevPath = $env:PATH
try {
    $env:INCLUDE = ($incPaths -join ';')
    $env:LIB = ($libPaths -join ';')
    $env:PATH = "$binDir;$prevPath"

    Write-Host "MSVC: $msvcDir"
    Write-Host "WinSDK: $sdkVer"

    Push-Location $root
    try {
        $sources = @(
            'src\bobo_core.cpp',
            'src\screen_capture.cpp',
            'src\mf_encoder.cpp',
            'src\mf_audio_aac.cpp',
            'src\wasapi_loopback.cpp',
            '..\bobo-stream-player\src\d3d11_renderer.cpp',
            '..\bobo-stream-player\src\mf_decoder.cpp'
        )

        $clArgs = @(
            '/nologo', '/LD', '/std:c++17', '/EHsc', '/O2', '/MT',
            '/W4', '/WX',
            '/DUNICODE', '/D_UNICODE', '/DNOMINMAX', '/D_CRT_SECURE_NO_WARNINGS', '/DBOBOCORE_EXPORTS',
            '/Isrc',
            '/I..\bobo-stream-player\src',
            "/Fo$objDir\",
            "/Fe$tmpDllOut"
        ) + $sources + @(
            '/link', '/NOLOGO', '/OPT:REF', '/OPT:ICF',
            'd3d11.lib', 'dxgi.lib', 'mfplat.lib', 'mfuuid.lib', 'wmcodecdspuuid.lib',
            'strmiids.lib', 'ole32.lib', 'oleaut32.lib', 'user32.lib', 'gdi32.lib', 'advapi32.lib', 'winmm.lib'
        )

        & $clExe @clArgs
        if ($LASTEXITCODE -ne 0) { throw "BoBoCore.dll compilation failed (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
    if (-not (Test-Path -LiteralPath $tmpDllOut)) { throw "Missing compiled DLL: $tmpDllOut" }
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            Copy-Item -LiteralPath $tmpDllOut -Destination $dllOut -Force
            break
        } catch {
            if ($attempt -ge 7) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not (Test-Path -LiteralPath $dllOut)) { throw "Missing output DLL: $dllOut" }

    # Compress BoBoCore.dll -> BoBoCore.dll.gz for embedding inside BoBoRemote.exe
    $rawBytes = [System.IO.File]::ReadAllBytes($dllOut)
    $ms = New-Object System.IO.MemoryStream
    $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $gz.Write($rawBytes, 0, $rawBytes.Length)
    $gz.Dispose()
    [System.IO.File]::WriteAllBytes($gzOut, $ms.ToArray())
    $ms.Dispose()
    Write-Host "Built: $dllOut ($($rawBytes.Length) bytes, gzip=$($gzOut.Length) -> $((Get-Item -LiteralPath $gzOut).Length) bytes)"

    if ($RunSelfTest) {
        $testCode = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class BoBoCoreSelfTestRunner {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SelfTestFn(StringBuilder buf, int len);

    public static int Run(string dllPath, out string report) {
        IntPtr mod = LoadLibrary(dllPath);
        if (mod == IntPtr.Zero) { report = "LoadLibrary failed: " + Marshal.GetLastWin32Error(); return 2; }
        IntPtr proc = GetProcAddress(mod, "BoBoCore_SelfTest");
        if (proc == IntPtr.Zero) { report = "GetProcAddress(BoBoCore_SelfTest) failed"; return 3; }
        var fn = (SelfTestFn)Marshal.GetDelegateForFunctionPointer(proc, typeof(SelfTestFn));
        var sb = new StringBuilder(4096);
        int rc = fn(sb, sb.Capacity);
        report = sb.ToString();
        return rc;
    }
}
"@
        Add-Type -TypeDefinition $testCode -Language CSharp
        $rep = ''
        $rc = [BoBoCoreSelfTestRunner]::Run($dllOut, [ref]$rep)
        Write-Host $rep
        if ($rc -ne 0) { throw "BoBoCore.dll self-test failed (rc=$rc)" }
    }
} finally {
    $env:INCLUDE = $prevInclude
    $env:LIB = $prevLib
    $env:PATH = $prevPath
    if (-not [string]::IsNullOrWhiteSpace($buildTmpDir) -and (Test-Path -LiteralPath $buildTmpDir)) {
        Remove-Item -LiteralPath $buildTmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
