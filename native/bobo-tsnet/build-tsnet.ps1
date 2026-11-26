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

$goExe = (Get-Command 'go' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)
if (-not $goExe) {
    foreach ($cand in @(
        'C:\Program Files\Go\bin\go.exe',
        'D:\Go\bin\go.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Go\bin\go.exe')
    )) {
        if (Test-Path -LiteralPath $cand) { $goExe = $cand; break }
    }
}
if (-not $goExe) { throw 'Go compiler (go.exe) not found. Please install Go or enable actions/setup-go in CI.' }

$exeOut = Join-Path $outDir 'BoBoTsNet.exe'
$gzOut = Join-Path $outDir 'BoBoTsNet.exe.gz'

Push-Location $root
try {
    $env:CGO_ENABLED = '0'
    $env:GOTOOLCHAIN = 'auto'
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'

    Write-Host '=== Running Go unit tests (bobo-tsnet) ==='
    & $goExe test -v ./...
    if ($LASTEXITCODE -ne 0) { throw "go test failed (exit=$LASTEXITCODE)" }

    Write-Host '=== Building BoBoTsNet.exe (pure Go, CGO_ENABLED=0) ==='
    & $goExe build -trimpath '-ldflags=-s -w -H=windowsgui' -o $exeOut .
    if ($LASTEXITCODE -ne 0) { throw "go build failed (exit=$LASTEXITCODE)" }

    if ($RunSelfTest) {
        Write-Host '=== Running BoBoTsNet.exe --self-test ==='
        $testDir = Join-Path $env:TEMP ('bobo-tsnet-selftest-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $testDir | Out-Null
        try {
            $tmpExe = Join-Path $testDir 'BoBoTsNet.exe'
            Copy-Item -LiteralPath $exeOut -Destination $tmpExe -Force
            $proc = Start-Process -FilePath $tmpExe -ArgumentList '--self-test' -Wait -PassThru
            if ($proc.ExitCode -ne 0) { throw "BoBoTsNet.exe --self-test failed (exit=$($proc.ExitCode))" }
        } finally {
            Remove-Item -LiteralPath $testDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $exeBytes = [System.IO.File]::ReadAllBytes($exeOut)
    $fs = [System.IO.File]::Create($gzOut)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        try {
            $gz.Write($exeBytes, 0, $exeBytes.Length)
        } finally {
            $gz.Dispose()
        }
    } finally {
        $fs.Dispose()
    }
    $gzLen = (Get-Item -LiteralPath $gzOut).Length
    Write-Host "OK: BoBoTsNet.exe ($($exeBytes.Length) B) -> BoBoTsNet.exe.gz ($gzLen B)"
} finally {
    Pop-Location
}
