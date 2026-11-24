<#
.SYNOPSIS
    CI 用的编译 + 自检脚本（Windows 自带 csc.exe，不需要 SDK）。

.DESCRIPTION
    把源码编译成启动器，再以 --self-test 跑无 UI 自检。
    本文件保存为 UTF-8 带 BOM：Windows PowerShell 5.1 读取无 BOM 的 UTF-8 脚本时会按
    ANSI 解释，中文会变成乱码并导致语法错误，因此 CI 不把脚本内联进 workflow。

.NOTES
    退出码：0 通过；其它值表示编译或自检失败。
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'ci-out'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($repoRoot)) { $repoRoot = (Get-Location).Path }

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
$displayTool = Join-Path $repoRoot 'tools\Set-SunshineDisplay.ps1'
$optimizeTool = Join-Path $repoRoot 'tools\Optimize-SunshineStream.ps1'

$referenceNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')
$references = @($referenceNames | ForEach-Object { Join-Path $framework $_ })

$outputRoot = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$exePath = Join-Path $outputRoot 'BoBoRemote.exe'
if (Test-Path -LiteralPath $exePath) { Remove-Item -LiteralPath $exePath -Force }

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
)
if (Test-Path -LiteralPath $appIcon) { $compilerArgs += "/win32icon:$appIcon" }
if (Test-Path -LiteralPath $brandLogo) { $compilerArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
if (Test-Path -LiteralPath $displayTool) { $compilerArgs += "/resource:$displayTool,DualRemote.tools.Set-SunshineDisplay.ps1" }
if (Test-Path -LiteralPath $optimizeTool) { $compilerArgs += "/resource:$optimizeTool,DualRemote.tools.Optimize-SunshineStream.ps1" }
$compilerArgs += $sources
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "compiled $($sources.Count) source files"

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

$selfTestCode = Invoke-SelfTestBinary -BinaryPath $exePath
Write-Host "self-test exit code: $selfTestCode"
if ($selfTestCode -ne 0) { throw "self-test failed with exit code $selfTestCode" }

$setupSource = Join-Path $repoRoot 'installer\BoBoSetup.cs'
if (Test-Path -LiteralPath $setupSource) {
    $setupExe = Join-Path $outputRoot 'BoBoSetupStub.exe'
    if (Test-Path -LiteralPath $setupExe) { Remove-Item -LiteralPath $setupExe -Force }
    $setupRefs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll') | ForEach-Object { Join-Path $framework $_ }
    $setupArgs = @(
        '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/warnaserror+',
        '/codepage:65001', '/utf8output', "/out:$setupExe", "/win32manifest:$manifest",
        "/reference:$($setupRefs -join ',')"
    )
    if (Test-Path -LiteralPath $appIcon) { $setupArgs += "/win32icon:$appIcon" }
    if (Test-Path -LiteralPath $brandLogo) { $setupArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
    $setupArgs += $setupSource
    & $compiler @setupArgs
    if ($LASTEXITCODE -ne 0) { throw "setup csc failed with exit code $LASTEXITCODE" }
    $setupCode = Invoke-SelfTestBinary -BinaryPath $setupExe
    Write-Host "setup self-test exit code: $setupCode"
    if ($setupCode -ne 0) { throw "setup self-test failed with exit code $setupCode" }
}

Write-Host 'OK: build and self-test passed'
exit 0