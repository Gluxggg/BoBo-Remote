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

$referenceNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
$references = @($referenceNames | ForEach-Object { Join-Path $framework $_ })

$outputRoot = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$exePath = Join-Path $outputRoot 'DualRemote.exe'
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
) + $sources
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "compiled $($sources.Count) source files"

$process = Start-Process -FilePath $exePath -ArgumentList '--self-test' -Wait -PassThru
Write-Host "self-test exit code: $($process.ExitCode)"
if ($process.ExitCode -ne 0) { throw "self-test failed with exit code $($process.ExitCode)" }

Write-Host 'OK: build and self-test passed'
exit 0