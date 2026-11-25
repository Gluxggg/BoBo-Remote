<#
.SYNOPSIS
    双端远程 Windows x64 便携包构建脚本。

.DESCRIPTION
    全程四步：获取并校验固定版本上游组件 -> 用系统自带 csc 编译启动器 ->
    组装发布目录 -> 输出 dist 下的解压目录和 zip。

    脚本只读写本项目目录，不修改系统服务、防火墙、注册表或网络配置，也不上传任何数据。
    下载的每个文件都按固定 SHA256 与字节数校验，不一致立即中止，绝不使用未校验文件。
    RustDesk 用官方 MSI 以管理安装（msiexec /a）方式“只提取文件”，不安装产品、不注册服务；
    本机 Windows Installer 会因此留下一条约 20 KiB 的 SourceHash 记账文件，属已知副作用。
    Moonlight 每次从校验过的 zip 重新解压到构建专属的干净目录，不复用可变缓存。

.PARAMETER FetchOnly
    只执行第一步：下载、校验并展开上游组件，报告 RustDesk MSI 展开结构与 Moonlight
    便携包结构，然后退出。这一步不需要 src 源码。

.PARAMETER SkipFetch
    离线构建：不联网下载，但 .cache 里的缓存必须已存在且通过 SHA256 校验，否则中止。

.PARAMETER OutputDir
    发布目录，默认项目下 dist。正式构建固定使用此目录；隔离验证应传入项目外的
    临时绝对路径，避免覆盖已有便携包或把上一轮旧文件带进 ZIP。

.PARAMETER NoPublish
    只完成下载校验、编译和干净组装到 .cache\build\clean-stage，不写 dist，也不生成 ZIP。
    用于在 UI/后端源码尚未稳定时检查组件集成，不产生可误发的旧程序包。

.PARAMETER ReleaseVersion
    写入 BUILD-INFO 的发包版本标签；默认取当前 UTC 时间并带 -dev 后缀。正式发布时
    由主代理显式传入，便于区分旧包。

.PARAMETER SourceDir
    启动器源码目录，默认 src（相对脚本所在目录）。仅供主模型在源码尚未合入时
    单独验证编译链使用，正常构建不要改。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -FetchOnly

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

.NOTES
    目标环境：Windows 10/11 x64，Windows PowerShell 5.1，.NET Framework 4.8（系统自带 csc.exe）。
    固定上游版本：RustDesk 1.5.3（官方 MSI）、Moonlight 6.2.0。升级版本时必须同步更新
    本脚本的 URL/大小/SHA256、README.md、THIRD-PARTY.md 与 LICENSES/。
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
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    # 旧系统可能没有 Tls12 枚举；继续用默认协议
}

$Root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = (Get-Location).Path }
if (-not [string]::IsNullOrWhiteSpace($OutputDir) -and -not [System.IO.Path]::IsPathRooted($OutputDir)) {
    throw "-OutputDir 必须是显式绝对路径；默认发布目录才是项目下 dist。"
}
$BuildTimestamp = Get-Date
$BuildVersion = if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    'dev-' + $BuildTimestamp.ToUniversalTime().ToString("s").Replace("-", "").Replace(":", "")
} else {
    $ReleaseVersion.Trim()
}

$DownloadsDir = Join-Path $Root '.cache\downloads'
$BuildDir     = Join-Path $Root '.cache\build'
$MlExtractDir = Join-Path $BuildDir 'moonlight-extract'
$RdMsiExtractDir = Join-Path $BuildDir 'rd-msi-extract'
$RdMsiPayloadRelative = 'PFiles64\RustDesk'
$StageRoot    = Join-Path $BuildDir 'clean-stage'
$PackageDir   = Join-Path $StageRoot 'BoBo远程'
$VPlusExtractDir = Join-Path $BuildDir 'vplus-extract'
$DeltaStageRoot  = Join-Path $BuildDir 'delta-stage'
$DistDir      = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path $Root 'dist'
} else {
    [System.IO.Path]::GetFullPath($OutputDir)
}
if ($DistDir.TrimEnd('\') -eq [System.IO.Path]::GetPathRoot($DistDir).TrimEnd('\')) {
    throw "拒绝把磁盘根目录当作发布目录：$DistDir"
}
if ($DistDir.TrimEnd('\').Equals([System.IO.Path]::GetFullPath($Root).TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝把项目根目录当作发布目录：$DistDir"
}
$ZipPath      = Join-Path $DistDir 'BoBo远程-Windows-x64.zip'
$LightZipPath = Join-Path $DistDir 'BoBo远程-轻量更新包.zip'
$SetupExePath = Join-Path $DistDir 'BoBoRemote-Setup-x64.exe'
$SunshineExtractDir = Join-Path $BuildDir 'sunshine-extract'

# 固定上游资产：版本、官方地址、字节数、SHA256 全部锁死。
# 固定 Release 资产均可用 GitHub Releases API 复核（见 THIRD-PARTY.md）。
# RustDesk 使用官方 MSI：本机实测单文件 portable exe 启动无窗口，MSI 展开的原版
# RustDesk.exe + librustdesk.dll + data\ 布局则可以直接运行。
$Assets = @(
    [pscustomobject]@{
        Label       = 'RustDesk 1.5.3（官方 MSI，构建时仅提取原版文件）'
        File        = 'rustdesk-1.5.3-x86_64.msi'
        Url         = 'https://github.com/rustdesk/rustdesk/releases/download/1.5.3/rustdesk-1.5.3-x86_64.msi'
        ApiFallback = 'https://api.github.com/repos/rustdesk/rustdesk/releases/assets/727209108'
        Size        = 26734592
        Sha256      = '27F6BFB48A93D524860DD7F4A79E349BE14B6DC71295746CF132A060BA4869C8'
    },
    [pscustomobject]@{
        Label       = 'Moonlight 6.2.0（低延迟串流客户端便携包）'
        File        = 'MoonlightPortable-x64-6.2.0.zip'
        Url         = 'https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.2.0/MoonlightPortable-x64-6.2.0.zip'
        ApiFallback = 'https://api.github.com/repos/moonlight-stream/moonlight-qt/releases/assets/608780016'
        Size        = 37242232
        Sha256      = '6F91D268B41ED5CA8066D9D7578DC403B8DB190991FF48F00B1700465F8F7EBF'
    }
)

# 许可证全文固定来自两个上游仓库的对应 tag（见 THIRD-PARTY.md），构建时校验。
$LicenseAssets = @(
    [pscustomobject]@{ File = 'AGPL-3.0.txt'; Component = 'RustDesk 1.5.3'; Size = 34523; Sha256 = '8486A10C4393CEE1C25392769DDD3B2D6C242D6EC7928E1414EFFF7DFB2F07EF' },
    [pscustomobject]@{ File = 'GPL-3.0.txt';  Component = 'Moonlight 6.2.0'; Size = 35141; Sha256 = '589ED823E9A84C56FEB95AC58E7CF384626B9CBF4FDA2A907BC36E103DE1BAD2' }
)

# MSI 展开后的关键文件（官方原样），构建时逐个校验，防止解包缺文件或版本漂移。
$RustDeskPayloadKeyFiles = @(
    [pscustomobject]@{ Path = 'RustDesk.exe'; Size = 386416; Sha256 = '34170D41670E8C3F8824D8C3CD7DE5388CC547205FE696021BDE528933B3C21D' },
    [pscustomobject]@{ Path = 'librustdesk.dll'; Size = 41430896; Sha256 = '05B647CF443BBB2851059D0D0D19FB450FD8096AB453DF84C464BF5A9C99E563' },
    [pscustomobject]@{ Path = 'flutter_windows.dll'; Size = 18058096; Sha256 = 'DE67CA5CF8D9FB8F5DDF648AD0B176DF92F3D997061000DB5DBDCB7CE2991FE7' },
    [pscustomobject]@{ Path = 'data\app.so'; Size = 13747104; Sha256 = 'EC23B8EC501AC97A09193D5F802ABB8F19808DEF7A87FE588E41965368528387' },
    [pscustomobject]@{ Path = 'data\icudtl.dat'; Size = 778864; Sha256 = 'C12537022EF818991A7BFED41A76D8D6AE962FFBC0E6511AC762A5D0845E7F7C' }
)

# 随包分发的工具脚本固定清单；不按通配符复制，避免把临时报告带进产物。
$ToolFiles = @(
    'collect-device-info.ps1',
    'measure-tailscale-path.ps1',
    'measure-lan-ping.ps1',
    'Set-SunshineDisplay.ps1',
    'Optimize-SunshineStream.ps1'
)

# 随包分发的文档固定清单；不按通配符复制。
$DocFiles = @(
    'README.md',
    'README.en.md',
    'THIRD-PARTY.md',
    'NOTICE.md',
    'LICENSE',
    'UU远程功能核对.md',
    '低延迟调优与防窥指南.md'
)

# Moonlight V+ 6.5.6：独立 GPLv3 分支，作为 engines\MoonlightVPlus 主力低延迟/ABR 引擎。
# 使用 .cache\downloads 中的固定 SHA256 缓存，缺失时可从官方 GitHub Release 下载并校验。
$VPlusAsset = [pscustomobject]@{
    Label        = 'Moonlight V+ 6.5.6（GPL-3.0 分支，支持 Foundation ABR）'
    File         = 'Moonlight-VPlus-Portable-x64-6.5.6.zip'
    Url          = 'https://github.com/qiin2333/moonlight-qt/releases/download/v6.5.6/Moonlight-VPlus-Portable-x64-6.5.6.zip'
    SourceTagUrl = 'https://github.com/qiin2333/moonlight-qt/tree/v6.5.6'
    SourceArchiveUrl = 'https://github.com/qiin2333/moonlight-qt/archive/refs/tags/v6.5.6.zip'
    SourceArchiveSha256 = 'FEC23C49AC93AC03D99C9091175DA62E511117543EC289B6D751404D25430714'
    Size         = 48208826
    Sha256       = 'E02447BE08ECA5DDD2555FD7669A114F6C896FDA3B70866401881026C27314E5'
    Version      = '6.5.6.0'
}

# Foundation Sunshine v2026.1123.185102（正式发布）官方 Windows x64 便携包，作为内置被控宿主。
# 只取官方 portable zip；不使用 standardLizardByte 或 Windows Installer，也不复制本机
# Program Files\Sunshine\config 的私人证书、令牌、配对状态或日志。
# GitHub Release 资产摘要与本地 zip 已交叉校验；资产签名未提供 Authenticode（NotSigned）。
$SunshineAsset = [pscustomobject]@{
    Label        = 'Foundation Sunshine v2026.1123.185102（官方 Windows x64 便携包，内置宿主）'
    File         = 'Sunshine.v2026.1123.185102.Portable-x64.zip'
    Url          = 'https://github.com/AlkaidLab/foundation-sunshine/releases/download/v2026.1123.185102.%E6%9D%82%E9%B1%BC/Sunshine.v2026.1123.Portable-x64.zip'
    ApiFallback  = 'https://api.github.com/repos/AlkaidLab/foundation-sunshine/releases/assets/784208027'
    Size         = 51644454
    Sha256       = '610788D951FE9BA0BA74F370C4BB83D535A61531F17937CD0A4644F00EABBA49'
    Version      = 'v2026.1123.185102'
    ReleaseTag   = 'v2026.1123.185102.杂鱼'
    Prerelease   = $false
    PublishedAt  = '2026-11-23T19:13:06Z'
    Commit       = '19ca6c8c484ab5f52bdc4c7f9315fa7c98321377'
    SourceTagUrl = 'https://github.com/AlkaidLab/foundation-sunshine/tree/v2026.1123.185102.杂鱼'
    SourceArchiveUrl = 'https://github.com/AlkaidLab/foundation-sunshine/archive/refs/tags/v2026.1123.185102.%E6%9D%82%E9%B1%BC.zip'
    ExeSize      = 53252608
    ExeSha256    = '87B4D685E23D843AD8D6CD19678EB468159CB3C45479E2F997B358220FE359C7'
}

$SunshineKeyFiles = @(
    [pscustomobject]@{ Path = 'sunshine.exe'; Size = 53252608; Sha256 = '87B4D685E23D843AD8D6CD19678EB468159CB3C45479E2F997B358220FE359C7' },
    [pscustomobject]@{ Path = 'zlib1.dll'; Size = 187392; Sha256 = '6D6A2429C74E2A659917C7D87037EF7F377036BB8D115DE96A645D8E6B184916' },
    [pscustomobject]@{ Path = 'assets\apps.json'; Size = 1764; Sha256 = '449F0816E37140F0ED40AACC21CB05B14E53DDDFB3AC6F4CB0BD6DB6C86F394B' },
    [pscustomobject]@{ Path = 'LICENSE'; Size = 35149; Sha256 = '3972DC9744F6499F0F9B2DBF76696F2AE7AD8AF9B23DDE66D6AF86C9DFB36986' },
    [pscustomobject]@{ Path = 'NOTICE'; Size = 3414; Sha256 = 'A3676180C504A10CA85563EB781C5F2574AD0E244054DCF3741184EE60463B0A' }
)

# V+ 展开后的关键文件（官方发布包原样），构建时逐个校验。
$VPlusKeyFiles = @(
    [pscustomobject]@{ Path = 'Moonlight.exe'; Size = 9191936; Sha256 = '847757D838157806D327BE626889A7C05A291F608BCED1848FFB61A01B799C63' },
    [pscustomobject]@{ Path = 'moonlight-clipboard-helper.exe'; Size = 146432; Sha256 = 'E94F8C0ABD5C705E1CC76F6977652F02B91186F04F42DE8CBCC44F852B9F3AF9' },
    [pscustomobject]@{ Path = 'portable.dat'; Size = 3; Sha256 = '6BDF66B5BF2A44E658BEA2EE86695AB150A06E600BF67CD5CCE245AD54962C61' }
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "  [通过] $Text" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Text)
    Write-Host "  [注意] $Text" -ForegroundColor Yellow
}

function Write-Info {
    param([string]$Text)
    Write-Host "  $Text"
}

function Get-FileSha256 {
    param([string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

# 只允许删除/移动构建目录内的对象；遇到重解析点（符号链接/目录联接）直接拒绝。
function Assert-BuildOwnedPath {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $buildFull = [System.IO.Path]::GetFullPath($BuildDir).TrimEnd('\')
    $inside = $full.Equals($buildFull, [System.StringComparison]::OrdinalIgnoreCase) -or
              $full.StartsWith($buildFull + '\', [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $inside) { throw "拒绝操作构建目录之外的路径：$full" }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "路径是重解析点，拒绝删除或移动：$full"
        }
    }
    return $full
}

function Assert-NoReparseTree {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "路径是重解析点，拒绝递归操作：$($root.FullName)"
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "路径树中含重解析点，拒绝递归操作：$($item.FullName)"
        }
    }
}

function Assert-DistOwnedPath {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $distFull = [System.IO.Path]::GetFullPath($DistDir).TrimEnd('\')
    $expectedRelease = Join-Path $distFull 'BoBo远程-Windows-x64'
    if (-not $full.Equals($expectedRelease, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作非精确 releaseDir 的路径：$full（只允许 $expectedRelease）"
    }
    if (Test-Path -LiteralPath $distFull) {
        $distItem = Get-Item -LiteralPath $distFull -Force
        if (($distItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "分发父目录是重解析点，拒绝递归操作：$distFull"
        }
    }
    Assert-NoReparseTree $full
    return $full
}

function Remove-DistPath {
    param([string]$Path)
    $full = Assert-DistOwnedPath $Path
    if (-not (Test-Path -LiteralPath $full)) { return }
    $root = Get-Item -LiteralPath $full -Force
    if (($root.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
        $root.Attributes = $root.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $full -Recurse -Force -ErrorAction Stop)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
        }
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}

function Remove-BuildPath {
    param([string]$Path)
    $full = Assert-BuildOwnedPath $Path
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

function Test-CachedAsset {
    param($Asset)
    $path = Join-Path $DownloadsDir $Asset.File
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne $Asset.Size) {
        Write-Warn "$($Asset.File) 缓存大小不符（$($item.Length) 字节，应为 $($Asset.Size) 字节），将重新下载"
        return $false
    }
    $hash = Get-FileSha256 $path
    if ($hash -ne $Asset.Sha256) {
        Write-Warn "$($Asset.File) 缓存 SHA256 不符，将重新下载"
        return $false
    }
    Write-Ok "$($Asset.File) 缓存校验通过（$($Asset.Size) 字节，SHA256 $hash）"
    return $true
}

function Invoke-Download {
    param($Asset)
    $dest = Join-Path $DownloadsDir $Asset.File
    $part = "$dest.part"

    $candidates = @($Asset.Url)
    if ($Asset.ApiFallback) { $candidates += $Asset.ApiFallback }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
        $url = $candidates[($attempt - 1) % $candidates.Count]
        Write-Info "下载 $attempt/3：$url"
        try {
            $req = @{
                Uri             = $url
                OutFile         = $part
                UseBasicParsing = $true
                TimeoutSec      = 900
                UserAgent       = 'DoubleEndRemote-build/1.0'
            }
            if ($url -like 'https://api.github.com/*') {
                # 官方 Releases API 资产端点需要显式要求 octet-stream 才会返回二进制
                $req.Headers = @{ Accept = 'application/octet-stream' }
            }
            Invoke-WebRequest @req
        } catch {
            Write-Warn "下载失败：$($_.Exception.Message)"
            Start-Sleep -Seconds (2 * $attempt)
            continue
        }

        $item = Get-Item -LiteralPath $part
        $hash = Get-FileSha256 $part
        if ($item.Length -eq $Asset.Size -and $hash -eq $Asset.Sha256) {
            Move-Item -LiteralPath $part -Destination $dest -Force
            Write-Ok "下载并校验通过：$($Asset.File)（$($Asset.Size) 字节，SHA256 $hash）"
            return
        }
        Write-Warn "下载内容校验不符（大小 $($item.Length)/$($Asset.Size) 字节，SHA256 $hash）"
        Remove-Item -LiteralPath $part -Force
        Start-Sleep -Seconds (2 * $attempt)
    }

    throw "无法获取可信的 $($Asset.File)。请先确认本机能访问 GitHub Releases（失败时可改用官方 API 资产地址，但同样必须通过 SHA256 校验），或使用已有的可信缓存；构建脚本不会使用未校验的文件。"
}

function Get-MoonlightExePath {
    if (-not (Test-Path -LiteralPath $MlExtractDir)) { return $null }
    $found = @(Get-ChildItem -LiteralPath $MlExtractDir -Recurse -File -Filter 'Moonlight.exe' -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1)
    if ($found.Count -eq 0) { return $null }
    return $found[0].FullName
}

function Get-RustDeskPayloadDir {
    $path = Join-Path $RdMsiExtractDir $RdMsiPayloadRelative
    if (Test-Path -LiteralPath (Join-Path $path 'RustDesk.exe')) { return $path }
    return $null
}

function Get-VPlusRoot {
    if (-not (Test-Path -LiteralPath $VPlusExtractDir)) { return $null }
    $found = @(Get-ChildItem -LiteralPath $VPlusExtractDir -Recurse -File -Filter 'Moonlight.exe' -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1)
    if ($found.Count -eq 0) { return $null }
    return $found[0].DirectoryName
}

function Assert-VPlusPayload {
    param([string]$PayloadDir, [string]$Context)
    foreach ($key in $VPlusKeyFiles) {
        $path = Join-Path $PayloadDir $key.Path
        if (-not (Test-Path -LiteralPath $path)) { throw "$Context 缺少关键文件：$($key.Path)" }
        $item = Get-Item -LiteralPath $path
        $hash = Get-FileSha256 $path
        if ($item.Length -ne $key.Size -or $hash -ne $key.Sha256) {
            throw "$Context 关键文件校验失败：$($key.Path)（应为 $($key.Size) 字节 / SHA256 $($key.Sha256)）"
        }
    }

    $exePath = Join-Path $PayloadDir 'Moonlight.exe'
    $signature = Get-AuthenticodeSignature -LiteralPath $exePath
    if ($signature.Status -ne 'NotSigned') {
        throw "$Context 预期 Windows Authenticode 为 NotSigned，实际为 $($signature.Status)；原包可能已变化，请人工复核。"
    }
    $fileVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
    if ($fileVersion -notlike '6.5.6*') {
        throw "$Context 版本不符：FileVersion=$fileVersion（预期 6.5.6.x）"
    }

    $forbidden = @(Get-ChildItem -LiteralPath $PayloadDir -Recurse -File -Force | Where-Object {
        $_.Name -match '(?i)\.(ini|log|pdb|conf)$' -or $_.FullName -match '(?i)\\source\\'
    })
    if ($forbidden.Count -gt 0) {
        $names = @($forbidden | ForEach-Object { $_.FullName.Substring($PayloadDir.Length + 1) })
        throw "$Context 含禁止的私有配置/源码对象：$($names -join '、')"
    }
}

function Get-SunshineRoot {
    if (-not (Test-Path -LiteralPath $SunshineExtractDir)) { return $null }
    $found = @(Get-ChildItem -LiteralPath $SunshineExtractDir -Recurse -File -Filter 'sunshine.exe' -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1)
    if ($found.Count -eq 0) { return $null }
    return $found[0].DirectoryName
}

function Assert-SunshinePayload {
    param([string]$PayloadDir, [string]$Context)
    foreach ($key in $SunshineKeyFiles) {
        $path = Join-Path $PayloadDir $key.Path
        if (-not (Test-Path -LiteralPath $path)) { throw "$Context 缺少关键文件：$($key.Path)" }
        $item = Get-Item -LiteralPath $path
        $hash = Get-FileSha256 $path
        if ($item.Length -ne $key.Size -or $hash -ne $key.Sha256) {
            throw "$Context 关键文件校验失败：$($key.Path)（应为 $($key.Size) 字节 / SHA256 $($key.Sha256)）"
        }
    }

    $exePath = Join-Path $PayloadDir 'sunshine.exe'
    $version = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
    if ($version -notlike "$($SunshineAsset.Version)*") {
        throw "$Context 版本不符：FileVersion=$version（预期 $($SunshineAsset.Version)*）"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $exePath
    if ($signature.Status -ne 'NotSigned') {
        throw "$Context 预期 Windows Authenticode 为 NotSigned，实际为 $($signature.Status)；官方原包可能已变化，请人工复核。"
    }

    $forbidden = @(Get-ChildItem -LiteralPath $PayloadDir -Recurse -File -Force | Where-Object {
        $_.FullName -match '(?i)\\config\\' -or
        $_.Name -match '(?i)^(local-auth\.token|cacert\.pem|cakey\.pem|sunshine_state\.json|sunshine\.conf|startup_guard\.json|client-fingerprint-rules\.json)$' -or
        $_.Name -match '(?i)\.(log|pdb|part|tmp)$' -or
        $_.FullName -match '(?i)\\credentials\\'
    })
    if ($forbidden.Count -gt 0) {
        $names = @($forbidden | ForEach-Object { $_.FullName.Substring($PayloadDir.Length + 1) })
        throw "$Context 含禁止的私人配置/凭据/日志：$($names -join '、')"
    }
    foreach ($privateDir in @('config', 'credentials', 'logs', 'logs_archive', 'crashes', 'telemetry')) {
        if (Test-Path -LiteralPath (Join-Path $PayloadDir $privateDir)) {
            throw "$Context 含禁止的私人目录：$privateDir"
        }
    }

    $appsPath = Join-Path $PayloadDir 'assets\apps.json'
    $appsText = Get-Content -LiteralPath $appsPath -Encoding UTF8 -Raw
    if ($appsText.IndexOf('"Desktop"', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$Context 的默认应用表缺少 Desktop；连接后可能没有可启动的桌面应用。"
    }
}

function Invoke-SunshinePrepare {
    $asset = $SunshineAsset
    $dest = Join-Path $DownloadsDir $asset.File
    if (-not (Test-CachedAsset $asset)) {
        if ($SkipFetch) {
            throw "离线构建模式（-SkipFetch）缺少已校验的 Foundation Sunshine 缓存：$dest"
        }
        Invoke-Download -Asset $asset
    }

    Remove-BuildPath $SunshineExtractDir
    New-Item -ItemType Directory -Force -Path $SunshineExtractDir | Out-Null
    Write-Info '展开 Foundation Sunshine 官方便携包（只从已校验 zip）…'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($dest, $SunshineExtractDir)
    $root = Get-SunshineRoot
    if (-not $root) { throw 'Foundation Sunshine 展开后未找到 sunshine.exe。' }
    Assert-SunshinePayload -PayloadDir $root -Context 'Foundation Sunshine 展开目录'

    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Ok "Foundation Sunshine 展开完成：$($files.Count) 个文件，$([Math]::Round($bytes / 1MB, 1)) MiB（$($asset.Version)，NotSigned）"
    Write-Info '  展开目录：.cache\build\sunshine-extract'
    Write-Info '  内置宿主运行时不读取源目录 config；首次准备才在用户稳定目录生成配置和凭据。'
}

function Invoke-VPlusPrepare {
    $asset = $VPlusAsset
    $dest = Join-Path $DownloadsDir $asset.File
    $valid = $false

    if (Test-Path -LiteralPath $dest) {
        $item = Get-Item -LiteralPath $dest
        if ($item.Length -eq $asset.Size -and (Get-FileSha256 $dest) -eq $asset.Sha256) {
            $valid = $true
            Write-Ok "V+ 原包缓存校验通过（$($asset.Size) 字节，SHA256 $($asset.Sha256)）"
        } else {
            Write-Warn 'V+ 原包缓存大小或 SHA256 不符，将用 .qa 原包重建缓存'
        }
    }

    if (-not $valid) {
        if ($SkipFetch) {
            throw "离线构建模式（-SkipFetch）缺少已校验的 Moonlight V+ 缓存：$dest"
        }
        Invoke-Download -Asset $asset
    }

    Remove-BuildPath $VPlusExtractDir
    New-Item -ItemType Directory -Force -Path $VPlusExtractDir | Out-Null
    Write-Info '展开 Moonlight V+ 便携包（只从已校验 zip）…'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($dest, $VPlusExtractDir)
    $root = Get-VPlusRoot
    if (-not $root) { throw 'V+ 展开后未找到 Moonlight.exe。' }
    Assert-VPlusPayload -PayloadDir $root -Context 'Moonlight V+ 展开目录'

    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    $top = @(Get-ChildItem -LiteralPath $root -Force | Select-Object -ExpandProperty Name)
    Write-Ok "Moonlight V+ 展开完成：$($files.Count) 个文件，$([Math]::Round($bytes / 1MB, 1)) MiB（$($asset.Version)，NotSigned）"
    Write-Info '  展开目录：.cache\build\vplus-extract'
    Write-Info "  顶层条目：$($top -join '、')"
}

function Assert-RustDeskPayload {
    param([string]$PayloadDir, [string]$Context)
    foreach ($key in $RustDeskPayloadKeyFiles) {
        $path = Join-Path $PayloadDir $key.Path
        if (-not (Test-Path -LiteralPath $path)) { throw "$Context 缺少关键文件：$($key.Path)" }
        $item = Get-Item -LiteralPath $path
        $hash = Get-FileSha256 $path
        if ($item.Length -ne $key.Size -or $hash -ne $key.Sha256) {
            throw "$Context 关键文件校验失败：$($key.Path)（应为 $($key.Size) 字节 / SHA256 $($key.Sha256)）"
        }
    }
}

function Invoke-RustDeskMsiExtract {
    $asset = @($Assets | Where-Object { $_.File -like '*.msi' })[0]
    if (-not $asset) { throw '资产表里没有 RustDesk MSI。' }
    $msi = Join-Path $DownloadsDir $asset.File
    if (-not (Test-Path -LiteralPath $msi)) { throw "缓存缺少 RustDesk MSI：$msi" }
    $hash = Get-FileSha256 $msi
    if ($hash -ne $asset.Sha256) { throw "RustDesk MSI 校验失败，拒绝解包：$hash" }

    Remove-BuildPath $RdMsiExtractDir
    New-Item -ItemType Directory -Force -Path $RdMsiExtractDir | Out-Null
    $logPath = Join-Path $BuildDir 'rd-msi-extract.log'
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }

    $msiexec = Join-Path $env:WINDIR 'System32\msiexec.exe'
    if (-not (Test-Path -LiteralPath $msiexec)) { throw '未找到 msiexec.exe，无法执行 MSI 管理安装提取。' }
    Write-Info '用 msiexec /a 仅提取 RustDesk MSI 文件（不安装产品、不注册服务）…'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $msiexec
    $psi.Arguments = "/a `"$msi`" /qn TARGETDIR=`"$RdMsiExtractDir`" /L*v `"$logPath`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()
    if (-not $process.WaitForExit(300000)) {
        try { $process.Kill() } catch { }
        throw 'msiexec /a 超过 300 秒未完成，请检查 Windows Installer 服务是否可用。'
    }
    $exitCode = $process.ExitCode
    $process.Dispose()

    # 管理安装由 Installer 服务完成，客户端进程有可能先返回；等文件落盘。
    $payload = Join-Path $RdMsiExtractDir $RdMsiPayloadRelative
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath (Join-Path $payload 'RustDesk.exe')) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($exitCode -ne 0) { throw "msiexec /a 提取失败（退出码 $exitCode）。日志：$logPath" }
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'RustDesk.exe'))) {
        throw "msiexec /a 未展开出 $RdMsiPayloadRelative\RustDesk.exe。日志：$logPath"
    }
    Assert-RustDeskPayload -PayloadDir $payload -Context 'RustDesk MSI 展开目录'

    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
    $payloadBytes = ($payloadFiles | Measure-Object -Property Length -Sum).Sum
    Write-Ok "RustDesk MSI 仅提取完成：$($payloadFiles.Count) 个文件，$([Math]::Round($payloadBytes / 1MB, 1)) MiB（关键文件已校验）"
}

function Invoke-FetchStage {
    Write-Step '第 1 步：获取并校验上游组件'

    if (-not (Test-Path -LiteralPath $DownloadsDir)) {
        New-Item -ItemType Directory -Force -Path $DownloadsDir | Out-Null
    }
    if (-not (Test-Path -LiteralPath $BuildDir)) {
        New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
    }

    foreach ($asset in $Assets) {
        if (Test-CachedAsset $asset) { continue }
        if ($SkipFetch) {
            throw "缓存缺失或校验失败：$($asset.File)。去掉 -SkipFetch 重新联网下载。"
        }
        Invoke-Download $asset
    }

    # Moonlight：删除上次的展开目录，从刚校验过的 zip 重新解压到构建专属目录。
    $zipPath = Join-Path $DownloadsDir 'MoonlightPortable-x64-6.2.0.zip'
    $zipHash = Get-FileSha256 $zipPath
    if ($zipHash -ne '6F91D268B41ED5CA8066D9D7578DC403B8DB190991FF48F00B1700465F8F7EBF') {
        throw "Moonlight zip 校验失败，拒绝展开：$zipHash"
    }
    Remove-BuildPath $MlExtractDir
    New-Item -ItemType Directory -Force -Path $MlExtractDir | Out-Null
    Write-Info '从已校验的 zip 重新展开 Moonlight 便携包…'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $MlExtractDir)
    Write-Ok 'Moonlight 便携包展开完成'

    $exePath = Get-MoonlightExePath
    if (-not $exePath) { throw '展开后未找到 Moonlight.exe，上游包结构可能已变化，请人工确认。' }
    $mlSignature = Get-AuthenticodeSignature -LiteralPath $exePath
    if ($mlSignature.Status -ne 'Valid') {
        throw "官方 Moonlight 6.2.0 的 Authenticode 签名异常：$($mlSignature.Status)。拒绝继续，以免替换掉已签名原版。"
    }
    Write-Ok '官方 Moonlight 6.2.0 Authenticode 签名保持 Valid'
    $mlRoot = Split-Path -Parent $exePath
    $relRoot = $mlRoot.Substring($MlExtractDir.Length).TrimStart('\')
    if ([string]::IsNullOrEmpty($relRoot)) { $relRoot = '(zip 根目录)' }

    $files = @(Get-ChildItem -LiteralPath $mlRoot -Recurse -File)
    $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    $topNames = @(Get-ChildItem -LiteralPath $mlRoot -Force | Select-Object -ExpandProperty Name)

    Write-Step '上游组件缓存与结构'
    foreach ($asset in $Assets) {
        $item = Get-Item -LiteralPath (Join-Path $DownloadsDir $asset.File)
        Write-Info "$($asset.Label)"
        Write-Info "  文件：.cache\downloads\$($asset.File)（$($item.Length) 字节）"
        Write-Info "  SHA256：$($asset.Sha256)"
    }
    Write-Info 'Moonlight 展开目录：.cache\build\moonlight-extract（本次构建专用）'
    Write-Info "  归一化根目录：$relRoot"
    Write-Info "  文件数：$($files.Count)，合计 $([Math]::Round($totalBytes / 1MB, 1)) MiB"
    Write-Info "  顶层条目：$($topNames -join '、')"

    $licenseFiles = @(Get-ChildItem -LiteralPath $MlExtractDir -Recurse -File |
        Where-Object { $_.Name -match '(?i)^(licen[cs]e|copying|notice)' })
    if ($licenseFiles.Count -eq 0) {
        Write-Warn '上游 Moonlight zip 未附带许可证文本；本项目用官方仓库 tag v6.2.0 的 GPL-3.0 全文补齐（构建时校验）。'
    } else {
        Write-Info "  上游随附许可文件：$(($licenseFiles | ForEach-Object { $_.Name }) -join '、')（将原样保留）"
    }

    Invoke-RustDeskMsiExtract
    $rdPayload = Get-RustDeskPayloadDir
    $rdFiles = @(Get-ChildItem -LiteralPath $rdPayload -Recurse -File)
    $rdBytes = ($rdFiles | Measure-Object -Property Length -Sum).Sum
    Write-Step 'RustDesk MSI 展开结构（官方原版文件，仅提取）'
    Write-Info "  展开目录：.cache\build\rd-msi-extract\$RdMsiPayloadRelative"
    Write-Info "  文件数：$($rdFiles.Count)，合计 $([Math]::Round($rdBytes / 1MB, 1)) MiB"
    Write-Info '  入口：RustDesk.exe（386,416 字节）；关键依赖：librustdesk.dll、flutter_windows.dll、data\app.so'
    Write-Info '  说明：msiexec /a 只解包，不安装产品、不注册服务；Windows Installer 会留一条 SourceHash 记账文件。'

    Invoke-VPlusPrepare
    Invoke-SunshinePrepare

    if ($FetchOnly) {
        Write-Host ''
        Write-Host '仅获取组件完成。下一步不带 -FetchOnly 运行，即编译启动器并打包。' -ForegroundColor Cyan
        exit 0
    }
}

function Invoke-CompileStage {
    Write-Step '第 2 步：用系统 .NET Framework 编译器编译启动器'

    $frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $csc = Join-Path $frameworkDir 'csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) {
        throw '未找到系统 .NET Framework 编译器（C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe）。请确认这是 Windows 10/11 x64 且 .NET Framework 4.8 可用。'
    }

    $srcDir = $SourceDir
    if (-not [System.IO.Path]::IsPathRooted($srcDir)) { $srcDir = Join-Path $Root $srcDir }
    if (-not (Test-Path -LiteralPath $srcDir)) {
        throw "找不到启动器源码目录：$srcDir（默认 src，源码由启动器任务提供；尚未合入时可先用 -SourceDir 指向临时目录验证编译链）。"
    }

    # src\_tmp 是开发期的临时编译试验目录（含同名类型的桩文件），不参与正式构建
    $allCsFiles = @(Get-ChildItem -LiteralPath $srcDir -Recurse -File -Filter '*.cs' | Sort-Object FullName)
    $excludedCs = @($allCsFiles | Where-Object { $_.FullName -match '\\_tmp\\' })
    $csFiles = @($allCsFiles | Where-Object { $_.FullName -notmatch '\\_tmp\\' })
    if ($excludedCs.Count -gt 0) {
        Write-Warn "已排除 src\_tmp 下的临时源码 $($excludedCs.Count) 个（不参与构建）"
    }
    if ($csFiles.Count -eq 0) { throw "源码目录中没有可编译的 .cs 文件：$srcDir" }

    $manifest = Join-Path $srcDir 'app.manifest'
    if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少 Windows 应用程序清单：$manifest" }

    $refNames = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')
    $refs = @()
    foreach ($name in $refNames) {
        $refPath = Join-Path $frameworkDir $name
        if (-not (Test-Path -LiteralPath $refPath)) { throw "缺少程序集引用：$refPath" }
        $refs += $refPath
    }

    $appDir = Join-Path $BuildDir 'app'
    if (-not (Test-Path -LiteralPath $appDir)) { New-Item -ItemType Directory -Force -Path $appDir | Out-Null }
    $exePath = Join-Path $appDir 'BoBo远程.exe'
    if (Test-Path -LiteralPath $exePath) { Remove-Item -LiteralPath $exePath -Force }
    $appVerLabel = if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) { '5.0.0' } else { $ReleaseVersion.Trim().TrimStart('v', 'V') }
    $appVerFile = Join-Path $appDir 'app-version.txt'
    [System.IO.File]::WriteAllText($appVerFile, $appVerLabel, (New-Object System.Text.UTF8Encoding($false)))

    $appIcon = Join-Path $Root 'assets\bobo-remote.ico'
    $brandLogo = Join-Path $Root 'assets\bobo-logo.png'
    $displayTool = Join-Path $Root 'tools\Set-SunshineDisplay.ps1'
    $optimizeTool = Join-Path $Root 'tools\Optimize-SunshineStream.ps1'
    $cscArgs = @(
        '/nologo'
        '/target:winexe'
        '/platform:x64'
        '/optimize+'
        '/warn:4'
        # 源码统一按 UTF-8 读取，避免中文源码在 GBK 代码页下变成乱码字面量
        '/codepage:65001'
        '/utf8output'
        "/out:$exePath"
        "/win32manifest:$manifest"
        "/reference:$($refs -join ',')"
    )
    if (Test-Path -LiteralPath $appIcon) { $cscArgs += "/win32icon:$appIcon" }
    if (Test-Path -LiteralPath $brandLogo) { $cscArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
    if (Test-Path -LiteralPath $displayTool) { $cscArgs += "/resource:$displayTool,DualRemote.tools.Set-SunshineDisplay.ps1" }
    if (Test-Path -LiteralPath $optimizeTool) { $cscArgs += "/resource:$optimizeTool,DualRemote.tools.Optimize-SunshineStream.ps1" }
    $cscArgs += "/resource:$appVerFile,DualRemote.app-version.txt"
    $cscArgs += $csFiles | ForEach-Object { $_.FullName }

    Write-Info "编译器：$csc（$((Get-Item -LiteralPath $csc).VersionInfo.FileVersion)）"
    Write-Info "源码：$($csFiles.Count) 个 .cs 文件，清单：$manifest"

    $compilerOutput = & $csc $cscArgs 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in @($compilerOutput)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$line)) { Write-Info "csc: $line" }
    }
    if ($exitCode -ne 0) { throw "csc 编译失败（退出码 $exitCode），请查看上方编译器输出。" }
    if (-not (Test-Path -LiteralPath $exePath)) { throw 'csc 报告成功，但没有生成 BoBo远程.exe。' }

    $exeSize = (Get-Item -LiteralPath $exePath).Length
    if ($exeSize -lt 1024) { throw "生成的 exe 只有 $exeSize 字节，疑似编译异常。" }
    Write-Ok "编译完成：BoBo远程.exe（$exeSize 字节）"

    # 编译 V5 原生一体化串流播放核 BoBoStreamPlayer.exe（静态链接 moonlight-common-c + D3D11VA + WASAPI）
    $nativeScript = Join-Path $Root 'native\bobo-stream-player\build-native.ps1'
    $nativeOutExe = Join-Path $Root 'native\bobo-stream-player\out\BoBoStreamPlayer.exe'
    if (Test-Path -LiteralPath $nativeScript) {
        Write-Info '正在构建 V5 原生一体化串流内核 BoBoStreamPlayer.exe…'
        & powershell -NoProfile -ExecutionPolicy Bypass -File $nativeScript -RunSelfTest
        if ($LASTEXITCODE -ne 0) { throw "BoBoStreamPlayer.exe 构建或自检失败（退出码 $LASTEXITCODE）。" }
    }
    if (-not (Test-Path -LiteralPath $nativeOutExe)) {
        throw "缺少 V5 原生一体化串流内核：$nativeOutExe"
    }
    Copy-Item -LiteralPath $nativeOutExe -Destination (Join-Path $appDir 'BoBoStreamPlayer.exe') -Force
    $nativeSize = (Get-Item -LiteralPath (Join-Path $appDir 'BoBoStreamPlayer.exe')).Length
    Write-Ok "V5 原生串流内核就绪：BoBoStreamPlayer.exe（$nativeSize 字节）"
}

function Assert-ProjectFiles {
    $required = @()
    foreach ($doc in $DocFiles) { $required += $doc }
    foreach ($name in $ToolFiles) { $required += "tools\$name" }
    foreach ($lic in $LicenseAssets) { $required += "LICENSES\$($lic.File)" }

    $missing = @()
    foreach ($rel in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $rel))) { $missing += $rel }
    }
    if ($missing.Count -gt 0) {
        throw "仓库文件不完整，缺少：$($missing -join '、')。构建只接受固定清单文件；若 Set-SunshineDisplay.ps1 或文档尚未写入，等文件就绪后再构建。"
    }

    foreach ($lic in $LicenseAssets) {
        $path = Join-Path (Join-Path $Root 'LICENSES') $lic.File
        $item = Get-Item -LiteralPath $path
        $hash = Get-FileSha256 $path
        if ($item.Length -ne $lic.Size -or $hash -ne $lic.Sha256) {
            throw "许可证文件校验失败：LICENSES\$($lic.File)（应为 $($lic.Size) 字节 / SHA256 $($lic.Sha256)）"
        }
    }
    Write-Ok '仓库文件与许可证校验通过'
}

function Invoke-Stage {
    Write-Step '第 3 步：组装发布目录'
    Assert-ProjectFiles

    Remove-BuildPath $StageRoot
    New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

    # 启动器与 V5 原生一体化串流播放核
    Copy-Item -LiteralPath (Join-Path (Join-Path $BuildDir 'app') 'BoBo远程.exe') -Destination $PackageDir
    Copy-Item -LiteralPath (Join-Path (Join-Path $BuildDir 'app') 'BoBoStreamPlayer.exe') -Destination $PackageDir

    # RustDesk：官方 MSI 管理安装展开后的原版文件布局，整体放进 engines\RustDesk
    $rdPayload = Get-RustDeskPayloadDir
    if (-not $rdPayload) { throw '未找到已提取的 RustDesk MSI 文件，请先执行 -FetchOnly 或完整构建。' }
    Assert-RustDeskPayload -PayloadDir $rdPayload -Context 'RustDesk 发布源'
    $rdDir = Join-Path $PackageDir 'engines\RustDesk'
    New-Item -ItemType Directory -Force -Path $rdDir | Out-Null
    Get-ChildItem -LiteralPath $rdPayload -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $rdDir -Recurse -Force
    }
    Assert-RustDeskPayload -PayloadDir $rdDir -Context '发布目录 engines\RustDesk'

    # Moonlight：保留全部 DLL、插件与上游文件，按 Moonlight.exe 所在层归一化
    $mlExe = Get-MoonlightExePath
    if (-not $mlExe) { throw '未找到本次展开的 Moonlight.exe，请先执行 -FetchOnly 或完整构建。' }
    $mlRoot = Split-Path -Parent $mlExe
    $mlDir = Join-Path $PackageDir 'engines\Moonlight'
    New-Item -ItemType Directory -Force -Path $mlDir | Out-Null
    Get-ChildItem -LiteralPath $mlRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $mlDir -Recurse -Force
    }
    if ($mlRoot -ne $MlExtractDir) {
        # 上游若多套了一层目录，把 zip 根部的零散文件（许可/说明）也带进来，避免丢文件
        Get-ChildItem -LiteralPath $MlExtractDir -File -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $mlDir -Force
        }
        Write-Warn 'Moonlight 包存在嵌套目录，已按 Moonlight.exe 所在层归一化。'
    }

    # Moonlight V+ 6.5.6：独立 opt-in 引擎，保留原包布局；不复制 .qa 源目录/源码对象。
    $vplusRoot = Get-VPlusRoot
    if (-not $vplusRoot) { throw '未找到本次展开的 Moonlight V+ 目录，请先执行 -FetchOnly 或完整构建。' }
    Assert-VPlusPayload -PayloadDir $vplusRoot -Context 'Moonlight V+ 发布源'
    $vplusDir = Join-Path $PackageDir 'engines\MoonlightVPlus'
    New-Item -ItemType Directory -Force -Path $vplusDir | Out-Null
    Get-ChildItem -LiteralPath $vplusRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $vplusDir -Recurse -Force
    }
    Assert-VPlusPayload -PayloadDir $vplusDir -Context '发布目录 engines\MoonlightVPlus'

    # Foundation Sunshine：官方 portable 文件的固定版本，作为内置被控宿主。
    $sunshineRoot = Get-SunshineRoot
    if (-not $sunshineRoot) { throw '未找到本次展开的 Foundation Sunshine 目录，请先执行 -FetchOnly 或完整构建。' }
    Assert-SunshinePayload -PayloadDir $sunshineRoot -Context 'Foundation Sunshine 发布源'
    $sunshineDir = Join-Path $PackageDir 'engines\Sunshine'
    New-Item -ItemType Directory -Force -Path $sunshineDir | Out-Null
    Get-ChildItem -LiteralPath $sunshineRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $sunshineDir -Recurse -Force
    }
    Assert-SunshinePayload -PayloadDir $sunshineDir -Context '发布目录 engines\Sunshine'

    # 文档、工具、许可证：固定清单逐个复制，不按通配符
    foreach ($doc in $DocFiles) {
        Copy-Item -LiteralPath (Join-Path $Root $doc) -Destination $PackageDir
    }
    $toolsDir = Join-Path $PackageDir 'tools'
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    foreach ($name in $ToolFiles) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $Root 'tools') $name) -Destination $toolsDir
    }
    $licDir = Join-Path $PackageDir 'LICENSES'
    New-Item -ItemType Directory -Force -Path $licDir | Out-Null
    foreach ($lic in $LicenseAssets) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $Root 'LICENSES') $lic.File) -Destination $licDir
    }
    Copy-Item -LiteralPath (Join-Path $sunshineRoot 'LICENSE') -Destination (Join-Path $licDir 'Sunshine-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $sunshineRoot 'NOTICE') -Destination (Join-Path $licDir 'Sunshine-NOTICE.txt') -Force

    # 构建信息：只写版本、校验值和构建环境，不写任何账号/设备/网络信息
    $now = $BuildTimestamp
    $info = New-Object System.Collections.Generic.List[string]
    $info.Add('BoBo远程（Windows x64 便携包）')
    $info.Add('')
    $info.Add("包版本：$BuildVersion")
    $utcStamp = $now.ToUniversalTime().ToString("s") + "Z"
    $info.Add("构建标识：$BuildVersion@$utcStamp")
    $info.Add("构建时间：$($now.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
    $info.Add("构建环境：$([Environment]::OSVersion.VersionString)，Windows PowerShell $($PSVersionTable.PSVersion)")
    $info.Add('编译器：系统 .NET Framework csc（C# 5，/codepage:65001）')
    $info.Add('')
    $info.Add('上游组件（构建时均按固定 SHA256 校验）：')
    foreach ($asset in $Assets) {
        $info.Add("  $($asset.Label)")
        $info.Add("    文件：$($asset.File)（$($asset.Size) 字节）")
        $info.Add("    SHA256：$($asset.Sha256)")
        $info.Add("    来源：$($asset.Url)")
    }
    $info.Add('')
    $info.Add('RustDesk 布局：官方 MSI 经 msiexec /a 管理安装仅提取文件（不安装产品、不注册服务），')
    $info.Add('原样放入 engines\RustDesk；RustDesk.exe / librustdesk.dll / flutter_windows.dll / data\app.so 已按固定哈希校验。')
    $info.Add('Moonlight 6.2.0（engines\Moonlight）：官方原版，构建时校验 Authenticode 为 Valid。')
    $info.Add('Moonlight V+ 6.5.6（engines\MoonlightVPlus）：GPL-3.0 分支，Windows Authenticode NotSigned，仅供 opt-in 使用。')
    $info.Add("  原包：$($VPlusAsset.File)（$($VPlusAsset.Size) 字节，SHA256 $($VPlusAsset.Sha256)）")
    $info.Add("  来源：$($VPlusAsset.Url)")
    $info.Add("  源码 tag：$($VPlusAsset.SourceTagUrl)")
    $info.Add('')
    $info.Add('Foundation Sunshine ' + $SunshineAsset.Version + '（engines\Sunshine）：GPL-3.0，官方 Windows x64 便携包，预发布。')
    $info.Add("  原包：$($SunshineAsset.File)（$($SunshineAsset.Size) 字节，SHA256 $($SunshineAsset.Sha256)）")
    $info.Add("  sunshine.exe：$($SunshineAsset.ExeSize) 字节，SHA256 $($SunshineAsset.ExeSha256)，Authenticode NotSigned")
    $info.Add("  来源：$($SunshineAsset.Url)")
    $info.Add("  源码 tag：$($SunshineAsset.SourceTagUrl)")
    $info.Add("  源码 commit：$($SunshineAsset.Commit)")
    $info.Add("  prerelease：$($SunshineAsset.Prerelease)；published_at：$($SunshineAsset.PublishedAt)；不是 latest/stable")
    $info.Add("  源码归档：$($SunshineAsset.SourceArchiveUrl)（GitHub 自动归档不含 submodule；完整源码请 git clone 后 checkout 该 commit，并执行 git submodule update --init --recursive）")
    $info.Add('  内置宿主不含 config、local-auth.token、证书、私钥、配对状态或日志；首次准备时在 %LOCALAPPDATA%\双端远程\host 生成。')
    $info.Add('')
    $info.Add('许可证：')
    foreach ($lic in $LicenseAssets) {
        $info.Add("  LICENSES\$($lic.File)（$($lic.Component)，SHA256 $($lic.Sha256)）")
    }
    $info.Add("  engines\Sunshine\LICENSE（Foundation Sunshine $($SunshineAsset.Version)，SHA256 3972DC9744F6499F0F9B2DBF76696F2AE7AD8AF9B23DDE66D6AF86C9DFB36986）")
    $info.Add("  LICENSES\Sunshine-LICENSE.txt（Foundation Sunshine $($SunshineAsset.Version)，SHA256 3972DC9744F6499F0F9B2DBF76696F2AE7AD8AF9B23DDE66D6AF86C9DFB36986）")
    $info.Add("  LICENSES\Sunshine-NOTICE.txt（Foundation Sunshine $($SunshineAsset.Version)，SHA256 A3676180C504A10CA85563EB781C5F2574AD0E244054DCF3741184EE60463B0A）")
    $info.Add('')
    $info.Add('本包不含任何账号、设备 ID、节点地址、许可证密钥、缓存或本机配置。')
    $utf8Bom = New-Object System.Text.UTF8Encoding -ArgumentList $true
    [System.IO.File]::WriteAllText((Join-Path $PackageDir 'BUILD-INFO.txt'), ($info -join "`r`n"), $utf8Bom)

    $requiredInPackage = @(
        'BoBo远程.exe',
        'BoBoStreamPlayer.exe',
        'engines\RustDesk\RustDesk.exe',
        'engines\RustDesk\librustdesk.dll',
        'engines\RustDesk\flutter_windows.dll',
        'engines\RustDesk\data\app.so',
        'engines\Moonlight\Moonlight.exe',
        'engines\MoonlightVPlus\Moonlight.exe',
        'engines\Sunshine\sunshine.exe',
        'engines\Sunshine\assets\apps.json',
        'engines\Sunshine\LICENSE',
        'engines\Sunshine\NOTICE',
        'LICENSES\Sunshine-LICENSE.txt',
        'LICENSES\Sunshine-NOTICE.txt',
        'BUILD-INFO.txt'
    )
    foreach ($doc in $DocFiles) { $requiredInPackage += $doc }
    foreach ($name in $ToolFiles) { $requiredInPackage += "tools\$name" }
    foreach ($lic in $LicenseAssets) { $requiredInPackage += "LICENSES\$($lic.File)" }
    foreach ($rel in $requiredInPackage) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageDir $rel))) { throw "发布目录缺少必需文件：$rel" }
    }
    Assert-CleanPackage -Path $PackageDir -Context '完整发布目录'
    Write-Ok '发布目录组装完成'
}

function Assert-CleanPackage {
    param([string]$Path, [string]$Context)
    $badFiles = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Where-Object {
        ($_.Name -match '(?i)\.(ini|log|pdb|conf|tmp|part)$' -and
            $_.FullName -notmatch '(?i)\\engines\\RustDesk\\drivers\\') -or
        $_.FullName -match '(?i)\\source\\' -or
        $_.Name -match '(?i)^(recent\.txt|preferences\.txt|favorites\.txt)$'
    })
    $badDirs = @(Get-ChildItem -LiteralPath $Path -Recurse -Directory -Force | Where-Object {
        $_.Name -match '(?i)^(Moonlight Game Streaming Project|source)$'
    })
    $sunshineRoot = Join-Path $Path 'engines\Sunshine'
    $sunshinePrivate = @()
    if (Test-Path -LiteralPath $sunshineRoot) {
        foreach ($privateRel in @('config', 'credentials', 'logs', 'logs_archive', 'crashes', 'telemetry')) {
            $privatePath = Join-Path $sunshineRoot $privateRel
            if (Test-Path -LiteralPath $privatePath) { $sunshinePrivate += $privatePath }
        }
        $sunshinePrivate += @(Get-ChildItem -LiteralPath $sunshineRoot -Recurse -File -Force | Where-Object {
            $_.Name -match '(?i)^(local-auth\.token|cacert\.pem|cakey\.pem|sunshine_state\.json|sunshine\.log|startup_guard\.json|client-fingerprint-rules\.json)$'
        } | ForEach-Object { $_.FullName })
        $allowedApps = [System.IO.Path]::GetFullPath((Join-Path $sunshineRoot 'assets\apps.json'))
        $sunshinePrivate += @(Get-ChildItem -LiteralPath $sunshineRoot -Recurse -File -Force -Filter 'apps.json' | Where-Object {
            -not [string]::Equals([System.IO.Path]::GetFullPath($_.FullName), $allowedApps, [System.StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { $_.FullName })
    }
    if ($badFiles.Count -gt 0 -or $badDirs.Count -gt 0 -or $sunshinePrivate.Count -gt 0) {
        $names = @()
        $names += @($badFiles | ForEach-Object { $_.FullName.Substring($Path.Length + 1) })
        $names += @($badDirs | ForEach-Object { $_.FullName.Substring($Path.Length + 1) + '\' })
        $names += @($sunshinePrivate | ForEach-Object { $_.Substring($Path.Length + 1) })
        throw "$Context 含禁止的私有配置/源码对象：$($names -join '、')"
    }
}

function Assert-DeltaZipEntries {
    param([string]$ZipPath, [string[]]$AllowedPaths, [string]$Context)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            $name = ($entry.FullName -replace '/', '\').TrimEnd('\')
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $ok = $false
            foreach ($allowed in $AllowedPaths) {
                $a = $allowed.TrimEnd('\')
                if ($name -eq $a -or
                    $name.StartsWith($a + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $a.StartsWith($name + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $ok = $true
                    break
                }
            }
            if (-not $ok) { throw "$Context 含不在固定清单中的文件：$name" }
        }
    } finally {
        $archive.Dispose()
    }
}

function New-RelativeFileSetZip {
    param(
        [string]$SourceRoot,
        [string[]]$RelativePaths,
        [string]$StagePath,
        [string]$OutputPath,
        [string]$Context,
        [long]$MaxBytes = 0
    )

    Remove-BuildPath $StagePath
    New-Item -ItemType Directory -Force -Path $StagePath | Out-Null
    foreach ($rel in $RelativePaths) {
        $src = Join-Path $SourceRoot $rel
        if (-not (Test-Path -LiteralPath $src)) { throw "$Context 缺少固定清单源：$rel" }
        $dst = Join-Path $StagePath $rel
        $parent = Split-Path -Parent $dst
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    }
    Assert-CleanPackage -Path $StagePath -Context $Context

    $tmp = Join-Path $BuildDir ((Split-Path -Leaf $OutputPath) + '.tmp')
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $StagePath,
        $tmp,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    Assert-DeltaZipEntries -ZipPath $tmp -AllowedPaths $RelativePaths -Context $Context

    $size = (Get-Item -LiteralPath $tmp).Length
    if ($MaxBytes -gt 0 -and $size -ge $MaxBytes) {
        Remove-Item -LiteralPath $tmp -Force
        throw "$Context 压缩后为 $size 字节，超过上限 $MaxBytes 字节。"
    }

    Move-Item -LiteralPath $tmp -Destination $OutputPath -Force
    $hash = Get-FileSha256 $OutputPath
    Write-Ok "$Context：$(Split-Path -Leaf $OutputPath)（$size 字节，SHA256 $hash）"
}

function Invoke-Publish {
    Write-Step '第 4 步：输出到 dist（单一最新便携版与轻量更新包）'

    if (-not (Test-Path -LiteralPath $DistDir)) { New-Item -ItemType Directory -Force -Path $DistDir | Out-Null }

    $releaseDir = Assert-DistOwnedPath (Join-Path $DistDir 'BoBo远程-Windows-x64')
    # 若旧解压目录内有用户本地配对设置，先暂存，替换程序文件后再还原，避免丢配对。
    $savedPairingDirs = @()
    foreach ($relCfg in @('engines\Moonlight\Moonlight Game Streaming Project', 'engines\MoonlightVPlus\Moonlight Game Streaming Project', 'engines\Sunshine\config')) {
        $existingCfg = Join-Path $releaseDir $relCfg
        if (Test-Path -LiteralPath $existingCfg) {
            $tmpSave = Join-Path $BuildDir ('preserve-' + [Guid]::NewGuid().ToString('N'))
            Copy-Item -LiteralPath $existingCfg -Destination $tmpSave -Recurse -Force
            $savedPairingDirs += [pscustomobject]@{ Rel = $relCfg; Backup = $tmpSave }
        }
    }
    if (Test-Path -LiteralPath $releaseDir) {
        Remove-DistPath $releaseDir
    }
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    Get-ChildItem -LiteralPath $PackageDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $releaseDir -Recurse -Force
    }
    foreach ($saved in $savedPairingDirs) {
        $destCfg = Join-Path $releaseDir $saved.Rel
        Copy-Item -LiteralPath $saved.Backup -Destination $destCfg -Recurse -Force
        Remove-BuildPath $saved.Backup
        Write-Info "已保留原有本机配对目录：$($saved.Rel)"
    }
    Write-Ok "最新解压目录：$releaseDir"

    # 完整便携包：唯一根目录 BoBo远程，含 RustDesk 1.5.3 + Moonlight 6.2.0 + Moonlight V+ 6.5.6 + Foundation Sunshine。
    $fullTmp = Join-Path $BuildDir 'BoBo远程-Windows-x64.zip.tmp'
    if (Test-Path -LiteralPath $fullTmp) { Remove-Item -LiteralPath $fullTmp -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $StageRoot,
        $fullTmp,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    $archive = [System.IO.Compression.ZipFile]::OpenRead($fullTmp)
    try {
        $rootNames = @($archive.Entries | ForEach-Object { ($_.FullName -split '[\\/]')[0] } | Sort-Object -Unique)
    } finally {
        $archive.Dispose()
    }
    if ($rootNames.Count -ne 1 -or $rootNames[0] -ne 'BoBo远程') {
        throw "完整 zip 结构异常：根目录应为唯一的 BoBo远程，实际为 $($rootNames -join '、')"
    }
    Move-Item -LiteralPath $fullTmp -Destination $ZipPath -Force
    Write-Ok "完整便携包：dist\BoBo远程-Windows-x64.zip"

    # 轻量更新包：含新启动器、V5 原生一体化串流播放核、显示/抗丢包调优工具与最新文档，便于已装完整包的电脑秒级覆盖更新。
    $lightStage = Join-Path $BuildDir 'base-delta-stage'
    $lightPaths = @(
        'BoBo远程.exe',
        'BoBoStreamPlayer.exe',
        'tools\Set-SunshineDisplay.ps1',
        'tools\Optimize-SunshineStream.ps1',
        'README.md',
        'README.en.md',
        'THIRD-PARTY.md',
        'NOTICE.md',
        'LICENSE',
        '低延迟调优与防窥指南.md'
    )
    New-RelativeFileSetZip -SourceRoot $PackageDir -RelativePaths $lightPaths -StagePath $lightStage `
        -OutputPath $LightZipPath -Context '轻量更新包' -MaxBytes 2097152

    # 标准图形化单文件安装程序：BoBoRemote-Setup-x64.exe（双击向导安装 + 桌面/开始菜单快捷方式 + 系统卸载项）
    New-SetupInstaller -ZipSource $ZipPath -OutputExe $SetupExePath -VersionLabel $BuildVersion

    return $releaseDir
}

function New-SetupInstaller {
    param(
        [string]$ZipSource,
        [string]$OutputExe,
        [string]$VersionLabel
    )

    $setupSource = Join-Path $Root 'installer\BoBoSetup.cs'
    if (-not (Test-Path -LiteralPath $setupSource)) {
        Write-Info '未找到 installer\BoBoSetup.cs，跳过生成图形化安装包。'
        return
    }

    $frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $csc = Join-Path $frameworkDir 'csc.exe'
    $manifest = Join-Path $Root 'src\app.manifest'
    $appIcon = Join-Path $Root 'assets\bobo-remote.ico'
    $brandLogo = Join-Path $Root 'assets\bobo-logo.png'

    $setupBuildDir = Join-Path $BuildDir 'setup'
    New-Item -ItemType Directory -Force -Path $setupBuildDir | Out-Null
    $stubExe = Join-Path $setupBuildDir 'BoBoSetupStub.exe'
    $verFile = Join-Path $setupBuildDir 'setup-version.txt'
    $displayVer = if ([string]::IsNullOrWhiteSpace($VersionLabel)) { '4.1.0' } else { $VersionLabel }
    [System.IO.File]::WriteAllText($verFile, $displayVer, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $stubExe) { Remove-Item -LiteralPath $stubExe -Force }

    $refs = @(
        'System.dll',
        'System.Core.dll',
        'System.Drawing.dll',
        'System.Windows.Forms.dll',
        'System.IO.Compression.dll',
        'System.IO.Compression.FileSystem.dll'
    ) | ForEach-Object { Join-Path $frameworkDir $_ }

    $cscArgs = @(
        '/nologo'
        '/target:winexe'
        '/platform:x64'
        '/optimize+'
        '/warn:4'
        '/warnaserror+'
        '/codepage:65001'
        '/utf8output'
        "/out:$stubExe"
        "/win32manifest:$manifest"
        "/reference:$($refs -join ',')"
    )
    if (Test-Path -LiteralPath $appIcon) { $cscArgs += "/win32icon:$appIcon" }
    if (Test-Path -LiteralPath $brandLogo) { $cscArgs += "/resource:$brandLogo,DualRemote.bobo-logo.png" }
    $cscArgs += "/resource:$verFile,DualRemote.setup-version.txt"
    $cscArgs += $setupSource

    $outLines = & $csc $cscArgs 2>&1
    $code = $LASTEXITCODE
    foreach ($ln in @($outLines)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$ln)) { Write-Info "setup-csc: $ln" }
    }
    if ($code -ne 0) { throw "BoBoSetup.cs 编译失败（退出码 $code）。" }

    # 流式拼接：Stub EXE + 完整便携 ZIP + 64 字节 BOBOSET1 SHA-256 尾标
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
    Write-Ok "标准安装包：dist\$(Split-Path -Leaf $OutputExe)"
}

function Show-Summary {
    param([string]$ReleaseDir)

    Write-Step '构建完成'

    $files = @(Get-ChildItem -LiteralPath $ReleaseDir -Recurse -File)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum

    Write-Info "最新解压目录：$ReleaseDir"
    Write-Info "包版本：$BuildVersion"
    Write-Info "  文件数 $($files.Count)，合计 $([Math]::Round($bytes / 1MB, 1)) MiB"
    Write-Info "  入口：$(Join-Path $ReleaseDir 'BoBo远程.exe')"

    foreach ($pair in @(
        @{ Label = '标准安装包'; Path = $SetupExePath },
        @{ Label = '完整便携包'; Path = $ZipPath },
        @{ Label = '轻量更新包'; Path = $LightZipPath }
    )) {
        if (Test-Path -LiteralPath $pair.Path) {
            $item = Get-Item -LiteralPath $pair.Path
            Write-Info "$($pair.Label)：dist\$(Split-Path -Leaf $pair.Path)"
            Write-Info "  $($item.Length) 字节，SHA256 $((Get-FileSha256 $pair.Path))"
        }
    }
    Write-Info ''
    Write-Info '本脚本不修改系统服务、防火墙、注册表或网络配置；RustDesk 用 msiexec /a 仅解包，未安装任何产品。'
    Write-Info ('已集成 Foundation Sunshine ' + $SunshineAsset.Version + ' 内置宿主、Moonlight V+ 6.5.6 与主机只读诊断。')
    Write-Info '内置宿主首次准备才在 %LOCALAPPDATA%\双端远程\host 生成配置与凭据，不复制 Program Files 私人配置。'
}

try {
    Write-Host 'BoBo远程 · Windows x64 便携包构建' -ForegroundColor Cyan
    Write-Info "项目目录：$Root"

    if (-not [Environment]::Is64BitOperatingSystem) { throw '本脚本只支持 64 位 Windows。' }
    if ($PSVersionTable.PSVersion.Major -lt 5) { throw '需要 Windows PowerShell 5.1。' }

    Invoke-FetchStage
    Invoke-CompileStage
    Invoke-Stage
    if ($NoPublish) {
        Write-Step '检查完成（未发布）'
        Write-Ok "干净发布目录：$PackageDir"
        Write-Info "包版本：$BuildVersion"
        Write-Info '本次未写 dist、未生成 ZIP；现有发布包保持不变。'
        exit 0
    }
    $releaseDir = Invoke-Publish
    Show-Summary -ReleaseDir $releaseDir
} catch {
    Write-Host ''
    Write-Host "构建失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
