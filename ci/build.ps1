<#
.SYNOPSIS
    BoBo远程 CI 编译 + 全内置自检脚本（先编译内嵌原生核 BoBoCore.dll.gz 与免安装跨网核 BoBoTsNet.exe.gz，再编译单文件 BoBoRemote.exe 并运行 --self-test）。

.NOTES
    本文件保存为 UTF-8 带 BOM（适配 Windows PowerShell 5.1）。
    退出码：0 通过；其它值表示编译或自检失败。
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'ci-out',
    [ValidatePattern('^[0-9]+[.][0-9]+[.][0-9]+$')]
    [string] $ReleaseVersion = '7.6.4'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($repoRoot)) { $repoRoot = (Get-Location).Path }

Write-Host '=== Step 1: Build & self-test embedded BoBoCore.dll ==='
$coreScript = Join-Path $repoRoot 'native\bobo-core\build-core.ps1'
powershell -NoProfile -File $coreScript -RunSelfTest
if ($LASTEXITCODE -ne 0) { throw "build-core.ps1 failed with exit code $LASTEXITCODE" }

$coreGz = Join-Path $repoRoot 'native\bobo-core\out\BoBoCore.dll.gz'
if (-not (Test-Path -LiteralPath $coreGz)) { throw "missing embedded core payload: $coreGz" }

Write-Host '=== Step 1b: Build & self-test embedded BoBoTsNet.exe ==='
$tsNetScript = Join-Path $repoRoot 'native\bobo-tsnet\build-tsnet.ps1'
powershell -NoProfile -File $tsNetScript -RunSelfTest
if ($LASTEXITCODE -ne 0) { throw "build-tsnet.ps1 failed with exit code $LASTEXITCODE" }

$tsNetGz = Join-Path $repoRoot 'native\bobo-tsnet\out\BoBoTsNet.exe.gz'
if (-not (Test-Path -LiteralPath $tsNetGz)) { throw "missing embedded tsnet payload: $tsNetGz" }

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "csc.exe not found: $compiler" }

$sourceDir = Join-Path $repoRoot 'src'
$sources = @(Get-ChildItem -LiteralPath $sourceDir -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\_tmp\\' } |
    Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw "no .cs files under $sourceDir" }

$manifest = Join-Path $sourceDir 'app.manifest'
if (-not (Test-Path -LiteralPath $manifest)) { throw "missing manifest: $manifest" }
$appIcon = Join-Path $repoRoot 'assets\bobo-remote.ico'
$brandLogo = Join-Path $repoRoot 'assets\bobo-logo.png'

$referenceNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Security.dll', 'System.ServiceProcess.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')
$references = @($referenceNames | ForEach-Object { Join-Path $framework $_ })

$outputRoot = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$versionPath = Join-Path $outputRoot 'app-version.txt'
[System.IO.File]::WriteAllText($versionPath, $ReleaseVersion, (New-Object System.Text.UTF8Encoding($false)))
$asmInfoPath = Join-Path $outputRoot 'AssemblyVersionInfo.cs'
$asmInfoBody = @(
    "[assembly: System.Reflection.AssemblyVersion(`"$ReleaseVersion.0`")]"
    "[assembly: System.Reflection.AssemblyFileVersion(`"$ReleaseVersion.0`")]"
    "[assembly: System.Reflection.AssemblyInformationalVersion(`"$ReleaseVersion`")]"
) -join "`r`n"
[System.IO.File]::WriteAllText($asmInfoPath, $asmInfoBody, (New-Object System.Text.UTF8Encoding($false)))
$exePath = Join-Path $outputRoot 'BoBoRemote.exe'
if (Test-Path -LiteralPath $exePath) { Remove-Item -LiteralPath $exePath -Force }

Write-Host "=== Step 2: Compile BoBoRemote.exe with embedded BoBoCore.dll.gz & BoBoTsNet.exe.gz ==="
Write-Host "compiler: $compiler"
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/warn:4'
    '/codepage:65001'
    '/utf8output'
    "/out:$exePath"
    "/win32manifest:$manifest"
    "/reference:$($references -join ',')"
    "/resource:$coreGz,DualRemote.BoBoCore.dll.gz"
    "/resource:$tsNetGz,DualRemote.BoBoTsNet.exe.gz"
    "/resource:$versionPath,DualRemote.app-version.txt"
)
if (Test-Path -LiteralPath $appIcon) { $compilerArgs += "/win32icon:$appIcon" }
if (Test-Path -LiteralPath $brandLogo) { $compilerArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
$compilerArgs += $sources
$compilerArgs += $asmInfoPath
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "compiled $($sources.Count) source files -> $exePath ($((Get-Item -LiteralPath $exePath).Length) bytes)"

function Invoke-SelfTestBinary {
    param([string]$BinaryPath)

    if ($BinaryPath -match '[^\x00-\x7F]') {
        $tmpDir = Join-Path $env:TEMP ('bobo-selftest-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
        try {
            $tmpExe = Join-Path $tmpDir ([System.IO.Path]::GetFileName($BinaryPath))
            Copy-Item -LiteralPath $BinaryPath -Destination $tmpExe -Force
            $proc = Start-Process -FilePath $tmpExe -ArgumentList '--self-test' -Wait -PassThru
            return $proc.ExitCode
        } finally {
            Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $procDirect = Start-Process -FilePath $BinaryPath -ArgumentList '--self-test' -Wait -PassThru
    return $procDirect.ExitCode
}

function Show-SelfTestFailureLog {
    $logFile = Join-Path $env:TEMP '双端远程-self-test.log'
    if (Test-Path -LiteralPath $logFile) {
        Write-Host '=== Failed self-test entries ==='
        Get-Content -LiteralPath $logFile -Encoding UTF8 | Where-Object { $_ -match '\[失败\]|结果：' } | ForEach-Object { Write-Host $_ }
        Write-Host '=== Tail of self-test log ==='
        Get-Content -LiteralPath $logFile -Encoding UTF8 -Tail 30 | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "Self-test log not found: $logFile"
    }
}

$selfTestCode = Invoke-SelfTestBinary -BinaryPath $exePath
Write-Host "self-test exit code: $selfTestCode"
if ($selfTestCode -ne 0) {
    Show-SelfTestFailureLog
    throw "self-test failed with exit code $selfTestCode"
}

$setupSource = Join-Path $repoRoot 'installer\BoBoSetup.cs'
if (Test-Path -LiteralPath $setupSource) {
    $setupExe = Join-Path $outputRoot 'BoBoSetupStub.exe'
    if (Test-Path -LiteralPath $setupExe) { Remove-Item -LiteralPath $setupExe -Force }
    $setupRefs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll') | ForEach-Object { Join-Path $framework $_ }
    $setupArgs = @(
        '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/warnaserror+',
        '/codepage:65001', '/utf8output', "/out:$setupExe", "/win32manifest:$manifest",
        "/reference:$($setupRefs -join ',')", "/resource:$versionPath,DualRemote.setup-version.txt"
    )
    if (Test-Path -LiteralPath $appIcon) { $setupArgs += "/win32icon:$appIcon" }
    if (Test-Path -LiteralPath $brandLogo) { $setupArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
    $setupArgs += $setupSource
    & $compiler @setupArgs
    if ($LASTEXITCODE -ne 0) { throw "setup csc failed with exit code $LASTEXITCODE" }
    $setupCode = Invoke-SelfTestBinary -BinaryPath $setupExe
    Write-Host "setup self-test exit code: $setupCode"
    if ($setupCode -ne 0) {
        Show-SelfTestFailureLog
        throw "setup self-test failed with exit code $setupCode"
    }
}

Write-Host 'OK: V6 all-in-one build and self-test passed'
exit 0
