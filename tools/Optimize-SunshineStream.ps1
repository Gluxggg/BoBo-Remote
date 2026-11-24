<#
.SYNOPSIS
  Foundation Sunshine 低延迟与抗丢包一键调优工具（双端远程）。

.DESCRIPTION
  针对 Foundation Sunshine（AlkaidLab/foundation-sunshine）在跨网/无线串流中常见的
  AMD AMF vbr_latency 大帧溢出、Skipping FEC 与丢帧后恢复异常、未开启 Auto-FEC / LTR 等历史问题，
  安全、可回滚地优化编码与网络抗丢包参数：
    - 通用抗丢包键：fec_auto / fec_percentage / fec_auto_max_percentage / address_family / ping_timeout
    - AMD AMF 专项键（仅检测到 AMD/amdvce 时写入）：
      amd_rc=cbr / amd_enforce_hrd=enabled / amd_usage=ultralowlatency / amd_quality=speed
      amd_vbaq=disabled / amd_hevc_gop_size=0 / amd_ltr_frames=2 / amd_lowlatency_mode=enabled / amd_input_queue_size=1
    - NVIDIA NVENC 专项键（仅检测到 nvenc 时写入）：
      nvenc_rate_control=cbr / nvenc_vbv_increase=0

  安全边界：
    - 仅管理白名单调优键，绝不接触账号、密码、证书、显示拓扑键。
    - 写入前与重启前都以本机 CA 证书指纹校验 /api/runtime/sessions，串流中或锁屏时拒绝写入。
    - 首次修改前自动把原始键值备份到 %ProgramData%\双端远程\sunshine-stream-tuning-backup.json，支持一键无损还原。
    - 写入采用同目录临时文件 + File.Replace 原子替换。
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Preview', 'Apply', 'Restore')]
    [string] $Action = 'Status',

    [ValidateSet('WanAntiLoss', 'LanUltraLow')]
    [string] $Profile = 'WanAntiLoss',

    [switch] $RestartSunshine,

    [switch] $Json,

    [string] $SunshineRoot = 'C:\Program Files\Sunshine',

    [string] $StatePath = ''
)

$ErrorActionPreference = 'Stop'
$script:ToolVersion = '1.0'
$script:ServiceName = 'SunshineService'
$script:UniversalKeys = @('fec_auto', 'fec_percentage', 'fec_auto_max_percentage', 'address_family', 'ping_timeout')
$script:AmdKeys = @('amd_rc', 'amd_enforce_hrd', 'amd_usage', 'amd_quality', 'amd_vbaq', 'amd_hevc_gop_size', 'amd_ltr_frames', 'amd_lowlatency_mode', 'amd_input_queue_size')
$script:NvKeys = @('nvenc_rate_control', 'nvenc_vbv_increase')
$script:AllManagedKeys = $script:UniversalKeys + $script:AmdKeys + $script:NvKeys
$script:Result = $null
$script:LastCertThumbprint = ''

function Reset-Result {
    $script:Result = [ordered]@{
        ok       = $false
        action   = $Action
        profile  = $Profile
        exitCode = 1
        summary  = ''
        messages = @()
        warnings = @()
        blockers = @()
        state    = [ordered]@{}
        changes  = @()
    }
}

function Add-Msg([string] $Text) { $script:Result.messages = @($script:Result.messages) + @([string] $Text) }
function Add-Warn([string] $Text) { $script:Result.warnings = @($script:Result.warnings) + @([string] $Text) }
function Add-Block([string] $Text) { $script:Result.blockers = @($script:Result.blockers) + @([string] $Text) }

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Resolve-BackupPath([string] $Override) {
    if (-not [string]::IsNullOrWhiteSpace($Override)) { return [IO.Path]::GetFullPath($Override) }
    $base = if ([string]::IsNullOrWhiteSpace($env:ProgramData)) { $env:LOCALAPPDATA } else { $env:ProgramData }
    return (Join-Path $base '双端远程\sunshine-stream-tuning-backup.json')
}

function Repair-VersionText([string] $Version) {
    if ([string]::IsNullOrEmpty($Version)) { return '' }
    if ($Version -match '[\u4e00-\u9fff]') { return $Version }
    if ($Version -match '^v\d{4}\.\d{3,4}\.\d{5,6}\.\S+$') {
        foreach ($cp in @(1252, 28591)) {
            try {
                $repaired = [Text.Encoding]::UTF8.GetString([Text.Encoding]::GetEncoding($cp).GetBytes($Version))
                if ($repaired -match '[\u4e00-\u9fff]') { return $repaired }
            } catch { }
        }
    }
    return $Version
}

function Get-InstallInfo([string] $Root) {
    $resolved = try { [IO.Path]::GetFullPath($Root) } catch { $Root }
    $exe = Join-Path $resolved 'sunshine.exe'
    $conf = Join-Path $resolved 'config\sunshine.conf'
    $info = [ordered]@{
        root = $resolved; exists = $false; exePath = $exe; configPath = $conf
        version = ''; supported = $false; problems = @()
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        $info.problems = @('未找到 Foundation Sunshine：' + $exe)
        return $info
    }
    $info.exists = $true
    try { $info.version = Repair-VersionText ([string] (Get-Item -LiteralPath $exe).VersionInfo.FileVersion) }
    catch { $info.problems = @('无法读取 sunshine.exe 版本。'); return $info }
    $info.supported = ($info.version -match '^v202[6-9]\.\d{3,4}\.\d{5,6}')
    if (-not (Test-Path -LiteralPath $conf -PathType Leaf)) {
        $info.problems = @('未找到配置文件：' + $conf)
        $info.supported = $false
    }
    return $info
}

function Get-CertThumbprint([string] $Root) {
    $res = [ordered]@{ ok = $false; thumbprint = ''; error = '' }
    $pem = Join-Path $Root 'config\credentials\cacert.pem'
    if (-not (Test-Path -LiteralPath $pem -PathType Leaf)) { $res.error = '未找到 cacert.pem'; return $res }
    try {
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pem)
        if ([string]::IsNullOrWhiteSpace($cert.Thumbprint)) { throw '指纹为空' }
        $res.thumbprint = [string] $cert.Thumbprint
        $res.ok = $true
    } catch { $res.error = $_.Exception.Message }
    return $res
}

function Get-LocalAuthToken([string] $Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return '' }
    $tokenPath = Join-Path $Root 'config\local-auth.token'
    if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) { return '' }
    try { return ([IO.File]::ReadAllText($tokenPath, [Text.Encoding]::UTF8)).Trim() }
    catch { return '' }
}

function Invoke-PinnedApi([string] $Path, [int] $Port, [string] $Thumbprint) {
    $res = [ordered]@{ ok = $false; status = 0; body = ''; error = '' }
    $prevCb = [Net.ServicePointManager]::ServerCertificateValidationCallback
    $prevProto = [Net.ServicePointManager]::SecurityProtocol
    $script:ExpectedThumbprint = $Thumbprint
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        [Net.ServicePointManager]::ServerCertificateValidationCallback = {
            param($s, $cert, $chain, $errs)
            if ($null -eq $cert) { return $false }
            return [string]::Equals($cert.GetCertHashString(), $script:ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase)
        }
        $req = [Net.HttpWebRequest]::Create(('https://localhost:{0}{1}' -f $Port, $Path))
        $req.Proxy = $null; $req.Timeout = 5000; $req.ReadWriteTimeout = 5000; $req.Method = 'GET'
        $localToken = Get-LocalAuthToken $SunshineRoot
        if (-not [string]::IsNullOrWhiteSpace($localToken)) {
            $req.Headers['X-Sunshine-Local-Token'] = $localToken
        }
        $resp = $req.GetResponse()
        try {
            $reader = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
            $res.body = $reader.ReadToEnd(); $reader.Close()
            $res.status = [int] $resp.StatusCode
            $res.ok = ($res.status -eq 200)
        } finally { $resp.Close() }
    } catch { $res.error = $_.Exception.Message }
    finally {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $prevCb
        [Net.ServicePointManager]::SecurityProtocol = $prevProto
    }
    return $res
}

function Get-Sessions([string] $Thumbprint, [int] $Port) {
    $res = [ordered]@{ ok = $false; count = -1; error = '' }
    $api = Invoke-PinnedApi '/api/runtime/sessions' $Port $Thumbprint
    if (-not $api.ok) { $res.error = $api.error; return $res }
    try {
        $j = $api.body | ConvertFrom-Json
        if ($null -eq $j -or [int] $j.status_code -ne 200 -or $null -eq $j.total_sessions) { throw '响应格式异常' }
        $res.count = [int] $j.total_sessions
        $res.ok = $true
    } catch { $res.error = $_.Exception.Message }
    return $res
}

function Test-TcpStreamActive {
    try {
        $pids = @(Get-Process -Name sunshine -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
        if ($pids.Count -eq 0) { return $false }
        foreach ($c in @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue)) {
            if ($pids -notcontains $c.OwningProcess) { continue }
            if (@(47984, 48010) -notcontains [int] $c.LocalPort) { continue }
            if ($c.RemoteAddress -eq '127.0.0.1' -or $c.RemoteAddress -eq '::1') { continue }
            return $true
        }
        return $false
    } catch { return $true }
}

function Read-ConfDoc([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $enc = New-Object System.Text.UTF8Encoding($false)
    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $enc = New-Object System.Text.UTF8Encoding($true); $offset = 3
    }
    $text = $enc.GetString($bytes, $offset, $bytes.Length - $offset)
    $nl = if ($text.IndexOf("`r`n") -lt 0 -and $text.IndexOf("`n") -ge 0) { "`n" } else { "`r`n" }
    $trailing = $text.EndsWith("`r`n") -or $text.EndsWith("`n")
    $lines = @($text -split "`r`n|`n|`r")
    if ($trailing -and $lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
        $lines = if ($lines.Count -eq 1) { @() } else { @($lines[0..($lines.Count - 2)]) }
    }
    $map = @{}
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ([string]$lines[$i] -match '^\s*([A-Za-z0-9_\.\-]+)\s*=\s*(.*?)\s*$') {
            $map[$Matches[1]] = $Matches[2]
        }
    }
    return [ordered]@{
        path = $Path; lines = $lines; newLine = $nl; trailing = $trailing; encoding = $enc
        values = $map; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    }
}

function Write-ConfDoc([string] $Path, [string[]] $Lines, $Doc) {
    $text = ($Lines -join $Doc.newLine) + $(if ($Doc.trailing -or $Lines.Count -gt 0) { $Doc.newLine } else { '' })
    $dir = [IO.Path]::GetDirectoryName($Path)
    $tmp = Join-Path $dir ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = $Doc.encoding.GetBytes($text)
        $pre = $Doc.encoding.GetPreamble()
        if ($pre.Length -gt 0) {
            $all = New-Object byte[] ($pre.Length + $bytes.Length)
            [Array]::Copy($pre, 0, $all, 0, $pre.Length)
            [Array]::Copy($bytes, 0, $all, $pre.Length, $bytes.Length)
            $bytes = $all
        }
        [IO.File]::WriteAllBytes($tmp, $bytes)
        [IO.File]::Replace($tmp, $Path, [NullString]::Value)
    } catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function Detect-GpuVendor($Values) {
    if ($Values.ContainsKey('encoder')) {
        $e = ([string]$Values['encoder']).ToLowerInvariant()
        if ($e -eq 'amdvce') { return 'amd' }
        if ($e -eq 'nvenc') { return 'nvidia' }
        if ($e -eq 'quicksync') { return 'intel' }
    }
    if ($Values.ContainsKey('adapter_name')) {
        $a = ([string]$Values['adapter_name']).ToLowerInvariant()
        if ($a -match 'amd|radeon') { return 'amd' }
        if ($a -match 'nvidia|geforce|rtx|gtx') { return 'nvidia' }
    }
    try {
        $gpus = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Name })
        $joined = ($gpus -join ' ').ToLowerInvariant()
        if ($joined -match 'nvidia|geforce|rtx|gtx') { return 'nvidia' }
        if ($joined -match 'amd|radeon') { return 'amd' }
    } catch { }
    return 'generic'
}

function Get-TargetMap([string] $ProfileName, [string] $Vendor) {
    $t = [ordered]@{}
    if ($ProfileName -eq 'LanUltraLow') {
        $t['fec_auto']                = 'enabled'
        $t['fec_percentage']          = '10'
        $t['fec_auto_max_percentage'] = '30'
        $t['address_family']          = 'both'
        $t['ping_timeout']            = '20000'
        if ($Vendor -eq 'amd') {
            $t['amd_rc']              = 'cbr'
            $t['amd_enforce_hrd']     = 'enabled'
            $t['amd_usage']           = 'lowlatency_high_quality'
            $t['amd_quality']         = 'balanced'
            $t['amd_vbaq']            = 'disabled'
            $t['amd_hevc_gop_size']   = '0'
            $t['amd_ltr_frames']      = '1'
            $t['amd_lowlatency_mode'] = 'enabled'
            $t['amd_input_queue_size']= '1'
        } elseif ($Vendor -eq 'nvidia') {
            $t['nvenc_rate_control']  = 'cbr'
            $t['nvenc_vbv_increase']  = '0'
        }
    } else {
        $t['fec_auto']                = 'enabled'
        $t['fec_percentage']          = '20'
        $t['fec_auto_max_percentage'] = '50'
        $t['address_family']          = 'both'
        $t['ping_timeout']            = '20000'
        if ($Vendor -eq 'amd') {
            $t['amd_rc']              = 'cbr'
            $t['amd_enforce_hrd']     = 'enabled'
            $t['amd_usage']           = 'ultralowlatency'
            $t['amd_quality']         = 'speed'
            $t['amd_vbaq']            = 'disabled'
            $t['amd_hevc_gop_size']   = '0'
            $t['amd_ltr_frames']      = '2'
            $t['amd_lowlatency_mode'] = 'enabled'
            $t['amd_input_queue_size']= '1'
        } elseif ($Vendor -eq 'nvidia') {
            $t['nvenc_rate_control']  = 'cbr'
            $t['nvenc_vbv_increase']  = '0'
        }
    }
    return $t
}

function Apply-KeyMapToLines([string[]] $Lines, $TargetMap, [string[]] $RemoveKeys) {
    $out = New-Object System.Collections.Generic.List[string]
    $written = @{}
    foreach ($line in $Lines) {
        if ([string]$line -match '^\s*([A-Za-z0-9_\.\-]+)\s*=\s*(.*?)\s*$') {
            $k = $Matches[1]
            if ($RemoveKeys -contains $k) { continue }
            if ($TargetMap.Contains($k)) {
                if (-not $written.ContainsKey($k)) {
                    $out.Add("$k = $($TargetMap[$k])")
                    $written[$k] = $true
                }
                continue
            }
        }
        $out.Add([string]$line)
    }
    foreach ($k in $TargetMap.Keys) {
        if (-not $written.ContainsKey($k)) {
            $out.Add("$k = $($TargetMap[$k])")
        }
    }
    return $out.ToArray()
}

function Build-Changes($Values, $TargetMap, [string[]] $RemoveKeys) {
    $changes = @()
    foreach ($k in $TargetMap.Keys) {
        $after = [string]$TargetMap[$k]
        if ($Values.ContainsKey($k)) {
            $before = [string]$Values[$k]
            if ($before -ne $after) {
                $changes += [ordered]@{ key = $k; action = 'replace'; before = $before; after = $after }
            }
        } else {
            $changes += [ordered]@{ key = $k; action = 'add'; before = $null; after = $after }
        }
    }
    foreach ($k in $RemoveKeys) {
        if ($Values.ContainsKey($k)) {
            $changes += [ordered]@{ key = $k; action = 'remove'; before = [string]$Values[$k]; after = $null }
        }
    }
    return $changes
}

function Restart-SunshineService {
    $res = [ordered]@{ ok = $false; error = '' }
    try {
        Restart-Service -Name $script:ServiceName -Force -ErrorAction Stop
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while ([DateTime]::UtcNow -lt $deadline) {
            $svc = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
            if ($svc -and $svc.Status -eq 'Running') {
                Start-Sleep -Milliseconds 1200
                $res.ok = $true
                return $res
            }
            Start-Sleep -Milliseconds 400
        }
        $res.error = 'SunshineService 重启超时。'
    } catch { $res.error = '重启 SunshineService 失败：' + $_.Exception.Message }
    return $res
}

function Invoke-Main {
    Reset-Result
    $backupPath = Resolve-BackupPath $StatePath
    $install = Get-InstallInfo $SunshineRoot
    $doc = if (Test-Path -LiteralPath $install.configPath -PathType Leaf) { Read-ConfDoc $install.configPath } else { $null }
    $values = if ($null -ne $doc) { $doc.values } else { @{} }
    $vendor = Detect-GpuVendor $values
    $webPort = if ($values.ContainsKey('port') -and $values['port'] -match '^\d+$') { [int]$values['port'] + 1 } else { 47990 }
    $cert = Get-CertThumbprint $install.root
    $sessions = if ($cert.ok) { Get-Sessions $cert.thumbprint $webPort } else { [ordered]@{ ok = $false; count = -1; error = $cert.error } }
    $tcpActive = Test-TcpStreamActive
    $locked = (@(Get-Process -Name LogonUI -ErrorAction SilentlyContinue).Count -gt 0)
    $streaming = ($sessions.ok -and $sessions.count -gt 0) -or $tcpActive
    $admin = Test-IsAdmin
    $backupPresent = Test-Path -LiteralPath $backupPath -PathType Leaf

    $issues = @()
    if (-not $install.exists) {
        $issues += '未找到 Foundation Sunshine，无法核验配置。'
    } elseif (-not $install.supported -or $null -eq $doc) {
        $issues += '版本或配置文件未通过可核验检查。'
    } else {
        if ($vendor -eq 'amd') {
            if ($values['amd_rc'] -ne 'cbr') { $issues += 'amd_rc 当前非 CBR（该配置与历史 Skipping FEC / 大帧异常相关，建议结合帧统计核对）' }
            if ($values['amd_enforce_hrd'] -ne 'enabled') { $issues += 'amd_enforce_hrd 未启用（缺少硬码率约束）' }
            $ltrFrames = 0
            $ltrValid = $values.ContainsKey('amd_ltr_frames') -and [int]::TryParse([string]$values['amd_ltr_frames'], [ref]$ltrFrames) -and $ltrFrames -ge 1
            if (-not $ltrValid) { $issues += 'amd_ltr_frames 未显式启用或不是有效整数（建议设为 2；恢复效果需会话验证）' }
        }
        if ($values['fec_auto'] -ne 'enabled') { $issues += 'fec_auto 自动前向纠错未启用' }
    }

    $script:Result.state = [ordered]@{
        install = $install; vendor = $vendor; admin = $admin; backupPath = $backupPath; backupPresent = $backupPresent
        locked = $locked; streaming = $streaming; sessionsKnown = $sessions.ok; sessionCount = $sessions.count
        issues = $issues; optimized = ($issues.Count -eq 0 -and $install.exists -and $install.supported -and $null -ne $doc)
    }

    if ($Action -eq 'Status') {
        Add-Msg "双端远程 · 低延迟与抗丢包调优工具 $script:ToolVersion"
        Add-Msg "Sunshine：$($install.root)（$($install.version)）"
        Add-Msg "显卡编码路径：$vendor | 管理员：$admin | 活跃会话：$($sessions.count) | 备份：$backupPresent"
        if ($issues.Count -eq 0) {
            Add-Msg '诊断结果：可核验的配置项已就绪；实际串流丢包与端到端延迟尚未实测。'
        } else {
            Add-Msg '检测到待检查或待优化项：'
            foreach ($it in $issues) { Add-Msg ('  - ' + $it) }
        }
        $script:Result.ok = $true
        $script:Result.summary = '状态检查完成。'
        return 0
    }

    if (-not $install.exists -or -not $install.supported -or $null -eq $doc) {
        Add-Block '未检测到受支持的 Foundation Sunshine 安装或配置文件。'
        return 2
    }

    if ($Action -eq 'Preview') {
        $target = Get-TargetMap $Profile $vendor
        $changes = Build-Changes $values $target @()
        $script:Result.changes = $changes
        Add-Msg "预览调优方案：$Profile（编码器：$vendor）"
        foreach ($c in $changes) { Add-Msg "  $($c.key): $($c.before) -> $($c.after)" }
        if ($changes.Count -eq 0) { Add-Msg '  当前配置已满足目标方案，无需修改。' }
        $script:Result.ok = $true
        $script:Result.summary = '预览完成（未写入任何文件）。'
        return 0
    }

    if (-not $admin) { Add-Block '修改 Sunshine 配置需要管理员权限。'; return 2 }
    if ($locked) { Add-Block '当前处于锁屏状态，已拒绝修改。'; return 2 }
    if ($streaming) { Add-Block '检测到正在进行串流，请先断开远程再应用调优。'; return 2 }
    if (-not $sessions.ok) { Add-Block ('无法通过本机接口确认串流状态：' + $sessions.error); return 2 }

    if ($Action -eq 'Apply') {
        $target = Get-TargetMap $Profile $vendor
        $changes = Build-Changes $values $target @()
        $script:Result.changes = $changes
        Add-Msg "正在应用方案：$Profile（编码器：$vendor）"
        if ($changes.Count -eq 0) {
            Add-Msg '当前配置已经是目标优化值，无需写入。'
            if ($RestartSunshine) {
                $r = Restart-SunshineService
                if (-not $r.ok) { Add-Block $r.error; return 4 }
                Add-Msg 'Sunshine 服务已重启。'
            }
            $script:Result.ok = $true
            $script:Result.summary = '已是最新优化配置。'
            return 0
        }
        if (-not $backupPresent) {
            $keys = @()
            foreach ($k in $script:AllManagedKeys) {
                if ($values.ContainsKey($k)) { $keys += [ordered]@{ name = $k; present = $true; value = [string]$values[$k] } }
                else { $keys += [ordered]@{ name = $k; present = $false; value = $null } }
            }
            $backupObj = [ordered]@{
                schema = 1; tool = 'Optimize-SunshineStream.ps1'; createdAtUtc = [DateTime]::UtcNow.ToString('o')
                configPath = $install.configPath; sunshineVersion = $install.version; keys = $keys
            }
            $bdir = [IO.Path]::GetDirectoryName($backupPath)
            if (-not (Test-Path -LiteralPath $bdir)) { New-Item -ItemType Directory -Force -Path $bdir | Out-Null }
            [IO.File]::WriteAllText($backupPath, (ConvertTo-Json -InputObject $backupObj -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
            Add-Msg ('已备份原始配置键至：' + $backupPath)
        } else {
            Add-Warn ('保留已有原始基线备份：' + $backupPath)
        }
        $newLines = Apply-KeyMapToLines $doc.lines $target @()
        Write-ConfDoc $install.configPath $newLines $doc
        foreach ($c in $changes) { Add-Msg "  已更新 $($c.key): $($c.before) -> $($c.after)" }
        if ($RestartSunshine) {
            $r = Restart-SunshineService
            if (-not $r.ok) { Add-Block $r.error; return 4 }
            Add-Msg 'SunshineService 已重启并载入低延迟抗丢包配置！'
        } else {
            Add-Msg '配置已写入，重启 SunshineService 后生效。'
        }
        $script:Result.ok = $true
        $script:Result.summary = "已成功应用 $Profile 抗丢包低延迟优化。"
        return 0
    }

    if ($Action -eq 'Restore') {
        if (-not $backupPresent) { Add-Block '未找到调优备份文件，无需还原。'; return 2 }
        $backup = Get-Content -LiteralPath $backupPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $restoreTarget = [ordered]@{}
        $removeList = @()
        foreach ($item in @($backup.keys)) {
            $k = [string]$item.name
            if ($script:AllManagedKeys -notcontains $k) { continue }
            if ([bool]$item.present) { $restoreTarget[$k] = [string]$item.value }
            else { $removeList += $k }
        }
        $changes = Build-Changes $values $restoreTarget $removeList
        $script:Result.changes = $changes
        if ($changes.Count -gt 0) {
            $newLines = Apply-KeyMapToLines $doc.lines $restoreTarget $removeList
            Write-ConfDoc $install.configPath $newLines $doc
            Add-Msg '已按备份原子还原原始编码与网络键。'
        } else {
            Add-Msg '当前配置已与备份一致。'
        }
        if ($RestartSunshine) {
            $r = Restart-SunshineService
            if (-not $r.ok) { Add-Block $r.error; return 4 }
            Add-Msg 'SunshineService 已重启。'
        }
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        $script:Result.ok = $true
        $script:Result.summary = '已还原调优前的原始配置。'
        return 0
    }
    return 1
}

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$code = 1
try { $code = Invoke-Main } catch { Reset-Result; Add-Block $_.Exception.Message; $code = 1 }
$script:Result.exitCode = $code
if ($Json) {
    $w = New-Object IO.StreamWriter([Console]::OpenStandardOutput(), (New-Object System.Text.UTF8Encoding($false)))
    try { $w.Write((ConvertTo-Json -InputObject $script:Result -Depth 8 -Compress) + "`r`n"); $w.Flush() } finally { $w.Dispose() }
} else {
    foreach ($m in @($script:Result.messages)) { Write-Host $m }
    foreach ($w in @($script:Result.warnings)) { Write-Host ('[注意] ' + $w) -ForegroundColor Yellow }
    foreach ($b in @($script:Result.blockers)) { Write-Host ('[阻塞] ' + $b) -ForegroundColor Red }
    if ($script:Result.summary) { Write-Host ('[结果] ' + $script:Result.summary) -ForegroundColor Green }
}
exit $code
