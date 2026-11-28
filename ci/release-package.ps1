<#
.SYNOPSIS
    构建并自检 BoBo Remote Windows x64 发布包，在 release-assets 中生成三个发布产物。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('(?-i)\Av(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z')]
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = $Tag.Substring(1)

Write-Host "=== Building BoBo Remote $Tag for Windows x64 ==="
$buildScript = Join-Path $repoRoot 'build.ps1'
powershell.exe -NoProfile -NonInteractive -File $buildScript -ReleaseVersion $version
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed ($LASTEXITCODE)" }

$distDir = Join-Path $repoRoot 'dist'
$launcher = Join-Path $distDir 'BoBo远程-Windows-x64\BoBo远程.exe'
$setup = Join-Path $distDir 'BoBoRemote-Setup-x64.exe'
$packages = [ordered]@{
    'BoBoRemote-Setup-x64.exe' = $setup
    'BoBoRemote-Portable-x64.zip' = Join-Path $distDir 'BoBo远程-Windows-x64.zip'
    'BoBoRemote-Update-x64.zip' = Join-Path $distDir 'BoBo远程-轻量更新包.zip'
}

foreach ($path in (@($launcher) + @($packages.Values))) {
    $file = Get-Item -LiteralPath $path
    if ($file -isnot [System.IO.FileInfo] -or $file.Length -eq 0) {
        throw "Missing or empty Windows release artifact: $path"
    }
}

Write-Host '=== Running launcher and installer self-tests ==='
foreach ($executable in @($launcher, $setup)) {
    $process = Start-Process -FilePath $executable -ArgumentList '--self-test' -Wait -PassThru
    Write-Host "$($executable) --self-test exit code: $($process.ExitCode)"
    if ($process.ExitCode -ne 0) {
        $logFile = Join-Path $env:TEMP '双端远程-self-test.log'
        if (Test-Path -LiteralPath $logFile) {
            Write-Host '=== Failed self-test entries ==='
            Get-Content -LiteralPath $logFile -Encoding UTF8 | Where-Object { $_ -match '\[失败\]|结果：' } | ForEach-Object { Write-Host $_ }
        }
        throw "Self-test failed for $executable ($($process.ExitCode))"
    }
}

$assetDir = Join-Path $repoRoot 'release-assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null
foreach ($package in $packages.GetEnumerator()) {
    Copy-Item -LiteralPath $package.Value -Destination (Join-Path $assetDir $package.Key) -Force
}
Write-Host "OK: Windows release packages staged in $assetDir"
