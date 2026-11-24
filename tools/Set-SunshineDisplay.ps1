<#
.SYNOPSIS
  Foundation Sunshine 显示模式本地工具（双端远程）。

.DESCRIPTION
  对已核验的 Foundation Sunshine（AlkaidLab/foundation-sunshine）本地安装切换显示模式，只管理 4 个键：
    output_name / display_device_prep / resolution_change / refresh_rate_change

    Privacy（防窥）  = ZakoHDR + ensure_only_display + automatic + automatic
                       会话期间关闭物理显示器，只保留 Zako 虚拟显示器（原有窗口与鼠标全部自动迁入）。
    Virtual（虚拟屏主屏） = ZakoHDR + ensure_primary + automatic + automatic
                       启用 Zako 虚拟显示器并设为远程主屏（避免仅看到空白副屏壁纸与鼠标停在实体屏），同时保留物理显示器。

  Status 默认只读；Preview 只读预览；Apply 先备份再原子写入；Restore 只按备份还原这 4 个键。
  默认不重启 Sunshine；只有显式 -RestartSunshine 才重启服务并验证运行中的配置。

  安全边界：
    - 不读写凭据、证书私钥或全量配置；JSON 输出只投影 4 个目标键。
    - 串流状态以本机 Sunshine 官方接口 /api/runtime/sessions 为准，并对证书做固定指纹校验；
      证书缺失、指纹不符、响应异常或无法确认时一律 fail-closed，不写配置。
    - 写入前和重启前都会二次检查串流/锁屏；写入使用同目录临时文件 + File.Replace 原子替换。
    - 不修改 vdd_keep_enabled，不安装/卸载驱动，不改系统权限或服务配置。
    - 备份只保存这 4 个键的原始值/缺省状态；还原时严格校验键名、格式与配置路径。

.PARAMETER Action
  Status（默认只读）| Preview（只读预览）| Apply | Restore。

.PARAMETER Mode
  Privacy 或 Virtual。Preview/Apply 必填。

.PARAMETER RestartSunshine
  显式要求 Apply/Restore 后重启 Sunshine 服务并验证运行中配置。默认关闭。

.PARAMETER Json
  只输出一行 UTF-8 JSON（供界面解析），不含整份配置文件。

.PARAMETER SunshineRoot
  Foundation Sunshine 安装目录，默认 C:\Program Files\Sunshine。

.PARAMETER StatePath
  备份文件路径，默认 %ProgramData%\双端远程\sunshine-display-backup.json。

.NOTES
  Exit codes:
    0 成功
    1 未预期错误
    2 前置条件拒绝（缺 Mode、版本/Zako 不符、非管理员、串流/锁屏、备份缺失或不可用等）
    3 配置或备份写入失败
    4 重启或运行态验证失败（配置已尝试回滚，备份保留）
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Preview', 'Apply', 'Restore')]
    [string] $Action = 'Status',

    [string] $Mode = '',

    [switch] $RestartSunshine,

    [switch] $Json,

    [string] $SunshineRoot = 'C:\Program Files\Sunshine',

    [string] $StatePath = ''
)

$script:ToolVersion = '1.1'
$script:SupportedTag = 'v2026.1031.121458.杂鱼'
$script:ServiceName = 'SunshineService'
$script:TargetKeyNames = @('output_name', 'display_device_prep', 'resolution_change', 'refresh_rate_change')
$script:KeyDefaults = @{
    output_name          = ''
    display_device_prep  = 'no_operation'
    resolution_change    = 'automatic'
    refresh_rate_change  = 'automatic'
}
$script:ModeValues = @{
    Privacy = [ordered]@{
        output_name         = 'ZakoHDR'
        display_device_prep = 'ensure_only_display'
        resolution_change   = 'automatic'
        refresh_rate_change = 'automatic'
    }
    Virtual = [ordered]@{
        output_name         = 'ZakoHDR'
        display_device_prep = 'ensure_primary'
        resolution_change   = 'automatic'
        refresh_rate_change = 'automatic'
    }
}
$script:Result = $null
$script:LastCertThumbprint = ''

function Reset-ToolResult {
    $script:Result = [ordered]@{
        ok       = $false
        action   = $Action
        mode     = $Mode
        exitCode = 1
        summary  = ''
        messages = @()
        warnings = @()
        blockers = @()
        state    = [ordered]@{}
        changes  = @()
    }
}

function Add-ToolMessage([string] $Text) { $script:Result.messages = @($script:Result.messages) + @([string] $Text) }
function Add-ToolWarning([string] $Text) { $script:Result.warnings = @($script:Result.warnings) + @([string] $Text) }
function Add-ToolBlocker([string] $Text) { $script:Result.blockers = @($script:Result.blockers) + @([string] $Text) }
function Set-ToolSummary([string] $Text) { $script:Result.summary = [string] $Text }

function Test-IsAdministrator {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { return $false }
}

function Resolve-BackupPath([string] $Override) {
    if (-not [string]::IsNullOrWhiteSpace($Override)) { return [IO.Path]::GetFullPath($Override) }
    $base = $env:ProgramData
    if ([string]::IsNullOrWhiteSpace($base)) { $base = $env:LOCALAPPDATA }
    return (Join-Path $base '双端远程\sunshine-display-backup.json')
}

function Repair-SunshineVersionText([string] $Version) {
    if ([string]::IsNullOrEmpty($Version)) { return '' }
    if ($Version -match '[\u4e00-\u9fff]') { return $Version }
    if ($Version -match '^v\d{4}\.\d{4}\.\d{6}\.\S+$') {
        # 该版本资源把 UTF-8 字节写进了 ANSI 字符串块：bytes 先被 Windows-1252 解码。
        foreach ($codePage in @(1252, 28591)) {
            try {
                $bytes = [Text.Encoding]::GetEncoding($codePage).GetBytes($Version)
                $repaired = [Text.Encoding]::UTF8.GetString($bytes)
                if ($repaired -match '[\u4e00-\u9fff]') { return $repaired }
            }
            catch { }
        }
    }
    return $Version
}

function Test-SupportedSunshineVersion([string] $Version) {
    $text = Repair-SunshineVersionText $Version
    if ($text -eq $script:SupportedTag) { return $true }
    if ($text.StartsWith('v2026.1031.121458', [StringComparison]::Ordinal)) { return $true }
    return ($text -match '^v202[6-9]\.\d{3,4}\.\d{5,6}')
}

function Get-SunshineInstallInfo([string] $Root) {
    $info = [ordered]@{ root = $Root; exists = $false; exePath = ''; configPath = ''; version = ''; supported = $false; problems = @() }
    if ([string]::IsNullOrWhiteSpace($Root)) { $info.problems = @('未指定 Sunshine 安装目录。'); return $info }
    $resolved = $Root
    try { $resolved = [IO.Path]::GetFullPath($Root) } catch { }
    $info.root = $resolved
    $info.exePath = Join-Path $resolved 'sunshine.exe'
    $info.configPath = Join-Path $resolved 'config\sunshine.conf'
    if (-not (Test-Path -LiteralPath $info.exePath -PathType Leaf)) {
        $info.problems = @('未找到 Foundation Sunshine 主程序：' + $info.exePath)
        return $info
    }
    $info.exists = $true
    try { $info.version = Repair-SunshineVersionText ([string] (Get-Item -LiteralPath $info.exePath).VersionInfo.FileVersion) }
    catch { $info.problems = @('无法读取 sunshine.exe 版本信息。'); return $info }
    $info.supported = Test-SupportedSunshineVersion $info.version
    if (-not (Test-Path -LiteralPath $info.configPath -PathType Leaf)) {
        $info.problems = @('未找到 Sunshine 配置：' + $info.configPath)
        $info.supported = $false
        return $info
    }
    if (-not $info.supported) { $info.problems = @('当前 Sunshine 版本 ' + $info.version + ' 未经验证；需要 Foundation Sunshine v2026.x 及以上版本。') }
    return $info
}

function Get-ZakoDisplayInfo {
    $result = [ordered]@{ ok = $false; devices = @(); problems = @() }
    try {
        $devices = @(Get-PnpDevice -Class Display -ErrorAction Stop | Where-Object { $_.FriendlyName -eq 'Zako Display Adapter' })
    }
    catch { $result.problems = @('无法读取 PnP 显示设备列表。'); return $result }
    if ($devices.Count -eq 0) { $result.problems = @('未找到 Zako Display Adapter（ZakoVDD 未安装）。'); return $result }
    $healthy = $false
    foreach ($device in $devices) {
        $result.devices += [ordered]@{ status = [string] $device.Status; instanceId = [string] $device.InstanceId; present = [bool] $device.Present }
        if ($device.Status -eq 'OK' -and $device.Present) { $healthy = $true }
    }
    $result.ok = $healthy
    if (-not $healthy) { $result.problems = @('Zako Display Adapter 存在但 PnP 状态不是 OK。') }
    return $result
}

function Get-SunshineServiceInfo {
    $info = [ordered]@{ exists = $false; status = 'Unknown'; startType = '' }
    try {
        $service = Get-Service -Name $script:ServiceName -ErrorAction Stop
        $info.exists = $true
        $info.status = [string] $service.Status
        $info.startType = [string] $service.StartType
    }
    catch { }
    return $info
}

function Get-SunshineCertificateThumbprint([string] $Root) {
    $result = [ordered]@{ ok = $false; thumbprint = ''; error = '' }
    $pem = Join-Path $Root 'config\credentials\cacert.pem'
    if (-not (Test-Path -LiteralPath $pem -PathType Leaf)) { $result.error = '未找到本机 Sunshine CA 证书 cacert.pem，无法校验本地接口。'; return $result }
    try {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pem)
        if ([string]::IsNullOrWhiteSpace($certificate.Thumbprint)) { throw '证书指纹为空' }
        $result.thumbprint = [string] $certificate.Thumbprint
        $result.ok = $true
    }
    catch { $result.error = '读取 Sunshine CA 证书失败：' + $_.Exception.Message }
    return $result
}

function Get-SunshineLocalAuthToken([string] $Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return '' }
    $tokenPath = Join-Path $Root 'config\local-auth.token'
    if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) { return '' }
    try { return ([IO.File]::ReadAllText($tokenPath, [Text.Encoding]::UTF8)).Trim() }
    catch { return '' }
}

function Invoke-SunshinePinnedApi([string] $Path, [int] $Port, [string] $ExpectedThumbprint) {
    $result = [ordered]@{ ok = $false; status = 0; body = ''; error = '' }
    if ([string]::IsNullOrWhiteSpace($ExpectedThumbprint)) { $result.error = '缺少固定证书指纹，拒绝请求。'; return $result }
    $previousCallback = [Net.ServicePointManager]::ServerCertificateValidationCallback
    $previousProtocol = [Net.ServicePointManager]::SecurityProtocol
    $script:LastCertThumbprint = ''
    $script:ExpectedSunshineThumbprint = $ExpectedThumbprint
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        [Net.ServicePointManager]::ServerCertificateValidationCallback = {
            param($sender, $certificate, $chain, $errors)
            try {
                if ($null -eq $certificate) { return $false }
                $actual = $certificate.GetCertHashString()
                $script:LastCertThumbprint = $actual
                return [string]::Equals($actual, $script:ExpectedSunshineThumbprint, [StringComparison]::OrdinalIgnoreCase)
            }
            catch { return $false }
        }
        $request = [Net.HttpWebRequest]::Create(('https://localhost:{0}{1}' -f $Port, $Path))
        $request.Proxy = $null
        $request.Timeout = 5000
        $request.ReadWriteTimeout = 5000
        $request.Method = 'GET'
        $request.Accept = 'application/json'
        $localToken = Get-SunshineLocalAuthToken $SunshineRoot
        if (-not [string]::IsNullOrWhiteSpace($localToken)) {
            $request.Headers['X-Sunshine-Local-Token'] = $localToken
        }
        $response = $request.GetResponse()
        try {
            $reader = New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)
            $result.body = $reader.ReadToEnd()
            $reader.Close()
            $result.status = [int] $response.StatusCode
            $result.ok = ($result.status -eq 200)
        }
        finally { $response.Close() }
    }
    catch {
        $result.error = $_.Exception.Message
        if ($script:LastCertThumbprint -and -not [string]::Equals($script:LastCertThumbprint, $ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            $result.error = '本机接口证书指纹与 cacert.pem 不一致，已拒绝。'
        }
    }
    finally {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
        [Net.ServicePointManager]::SecurityProtocol = $previousProtocol
        $script:ExpectedSunshineThumbprint = ''
    }
    return $result
}

function Get-RuntimeSessions([string] $Thumbprint, [int] $Port) {
    $result = [ordered]@{ ok = $false; count = -1; error = '' }
    $api = Invoke-SunshinePinnedApi '/api/runtime/sessions' $Port $Thumbprint
    if (-not $api.ok) { $result.error = $api.error; return $result }
    try {
        $json = $api.body | ConvertFrom-Json
        if ($null -eq $json -or [int] $json.status_code -ne 200) { throw '接口状态不是成功' }
        $names = @($json.PSObject.Properties.Name)
        if ($names -contains 'success' -and $json.success -ne $true) { throw '接口 success 不为 true' }
        if ($null -eq $json.total_sessions) { throw '缺少 total_sessions' }
        $result.count = [int] $json.total_sessions
        $result.ok = $true
    }
    catch { $result.error = '无法解析 /api/runtime/sessions：' + $_.Exception.Message }
    return $result
}

function Get-RuntimeConfig([string] $Thumbprint, [int] $Port) {
    $result = [ordered]@{ ok = $false; error = ''; version = ''; values = @{} }
    $api = Invoke-SunshinePinnedApi '/api/config' $Port $Thumbprint
    if (-not $api.ok) { $result.error = $api.error; return $result }
    try {
        $json = $api.body | ConvertFrom-Json
        if ($null -eq $json) { throw '空响应' }
        $result.version = Repair-SunshineVersionText ([string] $json.version)
        $names = @($json.PSObject.Properties.Name)
        foreach ($key in $script:TargetKeyNames) {
            if ($names -contains $key) { $result.values[$key] = [string] $json.$key }
            else { $result.values[$key] = $null }
        }
        $result.ok = $true
    }
    catch { $result.error = '无法解析 /api/config：' + $_.Exception.Message }
    return $result
}

function Get-SafetyState($Sessions, [bool] $TcpStreamHint) {
    $state = [ordered]@{
        locked         = $false
        lockKnown      = $false
        sessionsKnown  = [bool] $Sessions.ok
        sessionCount   = [int] $Sessions.count
        sessionsError  = [string] $Sessions.error
        tcpStreamHint  = $TcpStreamHint
        streaming      = $false
    }
    try { $state.locked = (@(Get-Process -Name LogonUI -ErrorAction SilentlyContinue).Count -gt 0); $state.lockKnown = $true } catch { }
    if ($state.sessionsKnown) { $state.streaming = ($state.sessionCount -gt 0) }
    if (-not $state.sessionsKnown) { $state.streaming = $TcpStreamHint }
    if ($TcpStreamHint) { $state.streaming = $true }
    return $state
}

function Test-TcpStreamHint([int] $Port) {
    try {
        $sunshinePids = @(Get-Process -Name sunshine -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
        if ($sunshinePids.Count -eq 0) { return $false }
        $streamPorts = @(47984, 48010)
        foreach ($connection in @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue)) {
            if ($sunshinePids -notcontains $connection.OwningProcess) { continue }
            if ($streamPorts -notcontains [int] $connection.LocalPort) { continue }
            $remote = [string] $connection.RemoteAddress
            if ($remote -eq '127.0.0.1' -or $remote -eq '::1') { continue }
            return $true
        }
        return $false
    }
    catch { return $true }
}

function Get-ConfigEncoding([byte[]] $Bytes) {
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [ordered]@{ kind = 'utf8bom'; offset = 3; encoding = (New-Object System.Text.UTF8Encoding($true)) }
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) {
        return [ordered]@{ kind = 'utf16le'; offset = 2; encoding = (New-Object System.Text.UnicodeEncoding($false, $true)) }
    }
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        [void] $strictUtf8.GetString($Bytes)
        return [ordered]@{ kind = 'utf8'; offset = 0; encoding = (New-Object System.Text.UTF8Encoding($false)) }
    }
    catch { return [ordered]@{ kind = 'ansi'; offset = 0; encoding = [Text.Encoding]::Default } }
}

function Read-ConfigDocument([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw '配置文件不存在：' + $Path }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $encodingInfo = Get-ConfigEncoding $bytes
    $text = $encodingInfo.encoding.GetString($bytes, $encodingInfo.offset, $bytes.Length - $encodingInfo.offset)
    $newLine = "`r`n"
    if ($text.IndexOf("`r`n") -lt 0 -and $text.IndexOf("`n") -ge 0) { $newLine = "`n" }
    $trailingNewLine = $text.EndsWith("`r`n") -or $text.EndsWith("`n") -or $text.EndsWith("`r")
    $lines = @($text -split "`r`n|`n|`r")
    if ($trailingNewLine -and $lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
        $lines = @($lines[0..($lines.Count - 2)])
        if ($lines.Count -eq 1 -and $lines[0] -eq '' -and $text.Length -gt 0) { $lines = @() }
    }
    return [ordered]@{
        path = $Path; lines = $lines; newLine = $newLine; trailingNewLine = $trailingNewLine
        encodingKind = $encodingInfo.kind; encoding = $encodingInfo.encoding
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    }
}

function Get-ConfigEntries([string[]] $Lines) {
    $entries = @()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = [string] $Lines[$i]
        $entry = [ordered]@{ index = $i; kind = 'other'; key = ''; value = '' }
        if ($line -match '^\s*$') { $entry.kind = 'blank' }
        elseif ($line -match '^\s*#') { $entry.kind = 'comment' }
        elseif ($line -match '^\s*([A-Za-z0-9_\.\-]+)\s*=\s*(.*?)\s*$') {
            $entry.kind = 'entry'; $entry.key = $Matches[1]; $entry.value = $Matches[2]
        }
        $entries += $entry
    }
    return $entries
}

function Get-ConfigKeyMap($Entries) {
    $map = @{}
    foreach ($entry in $Entries) {
        if ($entry.kind -ne 'entry') { continue }
        $key = [string] $entry.key
        if (-not $map.ContainsKey($key)) { $map[$key] = @() }
        $map[$key] = @($map[$key]) + @($entry)
    }
    return $map
}

function Get-ConfigDuplicates($Entries) {
    $map = Get-ConfigKeyMap $Entries
    $duplicates = @()
    foreach ($key in $script:TargetKeyNames) {
        if ($map.ContainsKey($key) -and @($map[$key]).Count -gt 1) { $duplicates += $key }
    }
    return $duplicates
}

function Get-ConfigValues($Entries) {
    $values = @{}
    $map = Get-ConfigKeyMap $Entries
    foreach ($key in $map.Keys) { $list = @($map[$key]); $values[$key] = [string] $list[$list.Count - 1].value }
    return $values
}

function Get-EffectiveConfigValue($Values, [string] $Key) {
    if ($Values.ContainsKey($Key)) { return [string] $Values[$Key] }
    if ($script:KeyDefaults.ContainsKey($Key)) { return [string] $script:KeyDefaults[$Key] }
    return ''
}

function Get-DisplayModeFromValues($Values) {
    $output = Get-EffectiveConfigValue $Values 'output_name'
    $prep = Get-EffectiveConfigValue $Values 'display_device_prep'
    if ($output -eq 'ZakoHDR' -and $prep -eq 'ensure_only_display') { return 'Privacy' }
    if ($output -eq 'ZakoHDR' -and $prep -eq 'ensure_primary') { return 'Virtual' }
    if ($output -eq 'ZakoHDR' -and $prep -eq 'ensure_secondary') { return 'VirtualSecondary' }
    if ($output -eq 'ZakoHDR') { return 'ZakoVdd' }
    if ($output -eq '' -and $prep -eq 'no_operation') { return 'Default' }
    return 'Custom'
}

function Get-DisplayModeLabel([string] $ModeName) {
    switch ($ModeName) {
        'Privacy'          { return '防窥（Privacy · 仅虚拟屏）' }
        'Virtual'          { return '铺满虚拟屏为主屏（Virtual · 保留实体屏）' }
        'VirtualSecondary' { return '副屏扩展（旧配置：远控仅见空副屏壁纸，建议改为主屏或恢复原设置）' }
        'ZakoVdd'          { return 'ZakoHDR 其他显示准备' }
        'Default'          { return '默认（未指定输出）' }
        'Custom'           { return '自定义/其他' }
        default            { return $ModeName }
    }
}

function Format-ValueForText($Value) {
    if ($null -eq $Value) { return '（未设置）' }
    $text = [string] $Value
    if ($text.Length -eq 0) { return '（空/默认）' }
    return $text
}

function New-ConfigChangePlan($Entries, $TargetValues) {
    $map = Get-ConfigKeyMap $Entries
    $plan = @()
    foreach ($key in $TargetValues.Keys) {
        $value = [string] $TargetValues[$key]
        if ($map.ContainsKey($key)) {
            $list = @($map[$key])
            $plan += [ordered]@{ key = $key; action = 'replace'; before = [string] $list[$list.Count - 1].value; after = $value; lineIndices = @($list | ForEach-Object { $_.index }) }
        }
        else {
            $plan += [ordered]@{ key = $key; action = 'add'; before = $null; after = $value; lineIndices = @() }
        }
    }
    return $plan
}

function New-RestoreChangePlan($Entries, $Backup) {
    $map = Get-ConfigKeyMap $Entries
    $plan = @()
    foreach ($item in @($Backup.keys)) {
        $key = [string] $item.name
        $lines = @()
        if ($map.ContainsKey($key)) { $lines = @($map[$key] | ForEach-Object { $_.index }) }
        if ([bool] $item.present) {
            $before = $null
            if ($lines.Count -gt 0) { $list = @($map[$key]); $before = [string] $list[$list.Count - 1].value }
            $plan += [ordered]@{
                key = $key; action = $(if ($lines.Count -gt 0) { 'replace' } else { 'add' })
                before = $before; after = [string] $item.value; lineIndices = $lines
            }
        }
        elseif ($lines.Count -gt 0) {
            $list = @($map[$key])
            $plan += [ordered]@{ key = $key; action = 'remove'; before = [string] $list[$list.Count - 1].value; after = $null; lineIndices = $lines }
        }
    }
    return $plan
}

function Apply-ChangePlanToLines([string[]] $Lines, $Plan) {
    $removeIndices = @{}
    $replaceValues = @{}
    $appendLines = @()
    foreach ($change in $Plan) {
        if ($change.action -eq 'remove') {
            foreach ($index in @($change.lineIndices)) { $removeIndices[[int] $index] = $true }
        }
        elseif ($change.action -eq 'replace') {
            $replaceValues[[int] @($change.lineIndices)[0]] = ('{0} = {1}' -f $change.key, $change.after)
        }
        elseif ($change.action -eq 'add') { $appendLines += ('{0} = {1}' -f $change.key, $change.after) }
    }
    $result = @()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($removeIndices.ContainsKey($i)) { continue }
        if ($replaceValues.ContainsKey($i)) { $result += $replaceValues[$i] }
        else { $result += [string] $Lines[$i] }
    }
    foreach ($line in $appendLines) { $result += $line }
    return $result
}

function Write-ConfigDocument([string] $Path, [string[]] $Lines, $Document) {
    $text = ($Lines -join $Document.newLine)
    if ($Document.trailingNewLine) { $text += $Document.newLine }
    $directory = [IO.Path]::GetDirectoryName($Path)
    $tempPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = $Document.encoding.GetBytes($text)
        $preamble = $Document.encoding.GetPreamble()
        if ($preamble.Length -gt 0) {
            $combined = New-Object byte[] ($preamble.Length + $bytes.Length)
            [Array]::Copy($preamble, 0, $combined, 0, $preamble.Length)
            [Array]::Copy($bytes, 0, $combined, $preamble.Length, $bytes.Length)
            $bytes = $combined
        }
        [IO.File]::WriteAllBytes($tempPath, $bytes)
        [IO.File]::Replace($tempPath, $Path, [NullString]::Value)
    }
    catch {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function Test-ConfigUnchanged([string] $Path, [string] $ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) { return $false }
    $current = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    return ($current -eq $ExpectedSha256)
}

function New-BackupObject($Install, $Entries, [string] $ConfigSha256, [string] $BackupMode) {
    $map = Get-ConfigKeyMap $Entries
    $keys = @()
    foreach ($name in $script:TargetKeyNames) {
        if ($map.ContainsKey($name)) {
            $list = @($map[$name])
            $keys += [ordered]@{ name = $name; present = $true; value = [string] $list[$list.Count - 1].value }
        }
        else { $keys += [ordered]@{ name = $name; present = $false; value = $null } }
    }
    return [ordered]@{
        schema = 1; tool = 'Set-SunshineDisplay.ps1'; toolVersion = $script:ToolVersion
        createdAtUtc = [DateTime]::UtcNow.ToString('o'); sunshineVersion = [string] $Install.version
        configPath = [string] $Install.configPath; configSha256 = $ConfigSha256
        appliedMode = $BackupMode; keys = $keys
    }
}

function Test-BackupKeyValue([string] $Key, [bool] $Present, $Value) {
    if (-not $Present) { return ($null -eq $Value) }
    if ($null -eq $Value) { return $false }
    $text = [string] $Value
    if ($text.Length -gt 256) { return $false }
    if ($text -match '[\r\n\x00]') { return $false }
    switch ($Key) {
        'output_name' {
            if ($text.Length -eq 0) { return $true }
            if ($text -eq 'ZakoHDR') { return $true }
            if ($text -match '^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$') { return $true }
            if ($text -match '^\\\\\.\\DISPLAY\d+$') { return $true }
            return $false
        }
        'display_device_prep' { return ($text -in @('no_operation', 'ensure_active', 'ensure_primary', 'ensure_secondary', 'ensure_only_display', '0', '1', '2', '3', '4')) }
        'resolution_change' { return ($text -in @('no_operation', 'automatic', 'manual', '0', '1', '2')) }
        'refresh_rate_change' { return ($text -in @('no_operation', 'automatic', 'manual', '0', '1', '2')) }
        default { return $false }
    }
}

function Read-BackupFile([string] $Path, [string] $ExpectedConfigPath) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw '备份文件不存在：' + $Path }
    $fileLength = (Get-Item -LiteralPath $Path).Length
    if ($fileLength -gt 65536) { throw '备份文件异常过大，拒绝读取。' }
    $backup = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $backup -or $null -eq $backup.keys) { throw '备份文件格式无效。' }
    if ([int] $backup.schema -ne 1) { throw '备份文件版本不受支持。' }
    $expectedPath = [IO.Path]::GetFullPath($ExpectedConfigPath)
    $actualPath = ''
    try { $actualPath = [IO.Path]::GetFullPath([string] $backup.configPath) } catch { }
    if (-not [string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw '备份文件的目标配置路径与当前 Sunshine 不一致。' }
    $keys = @($backup.keys)
    if ($keys.Count -ne $script:TargetKeyNames.Count) { throw '备份文件键数量异常，拒绝还原。' }
    $seen = @()
    foreach ($item in $keys) {
        $name = [string] $item.name
        if ($script:TargetKeyNames -notcontains $name) { throw '备份文件包含本工具不管理的键：' + $name }
        if ($seen -contains $name) { throw '备份文件包含重复键：' + $name }
        $seen += $name
        if (-not (Test-BackupKeyValue $name ([bool] $item.present) $item.value)) { throw '备份文件中的值格式无效：' + $name }
    }
    return $backup
}

function Write-BackupFile([string] $Path, $Backup) {
    $directory = [IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $tempPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($tempPath, (ConvertTo-Json -InputObject $Backup -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tempPath -Destination $Path -Force -ErrorAction Stop
    }
    catch {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function Get-EnvironmentState([string] $BackupPath) {
    $install = Get-SunshineInstallInfo $SunshineRoot
    $zako = Get-ZakoDisplayInfo
    $service = Get-SunshineServiceInfo
    $cert = Get-SunshineCertificateThumbprint $install.root
    $webPort = 47990
    $document = $null
    $entries = @()
    $values = @{}
    $duplicates = @()
    if (Test-Path -LiteralPath $install.configPath -PathType Leaf) {
        try {
            $document = Read-ConfigDocument $install.configPath
            $entries = Get-ConfigEntries $document.lines
            $values = Get-ConfigValues $entries
            $duplicates = Get-ConfigDuplicates $entries
            if ($values.ContainsKey('port') -and $values['port'] -match '^\d+$') { $webPort = [int] $values['port'] + 1 }
        }
        catch { Add-ToolWarning ('读取配置失败：' + $_.Exception.Message) }
    }
    $sessions = [ordered]@{ ok = $false; count = -1; error = $cert.error }
    $runtime = [ordered]@{ ok = $false; error = $cert.error; version = ''; values = @{} }
    if ($cert.ok) {
        $sessions = Get-RuntimeSessions $cert.thumbprint $webPort
        $runtime = Get-RuntimeConfig $cert.thumbprint $webPort
    }
    $tcpHint = Test-TcpStreamHint $webPort
    $safety = Get-SafetyState $sessions $tcpHint
    $fileValues = [ordered]@{}
    foreach ($key in $script:TargetKeyNames) {
        $fileValues[$key] = [ordered]@{ present = $values.ContainsKey($key); value = $(if ($values.ContainsKey($key)) { [string] $values[$key] } else { $null }) }
    }
    $runtimeValues = [ordered]@{}
    foreach ($key in $script:TargetKeyNames) { $runtimeValues[$key] = $(if ($runtime.ok) { $runtime.values[$key] } else { $null }) }
    $state = [ordered]@{
        install = $install
        zako = $zako
        service = $service
        safety = $safety
        admin = Test-IsAdministrator
        backupPath = $BackupPath
        backupPresent = (Test-Path -LiteralPath $BackupPath -PathType Leaf)
        configExists = ($null -ne $document)
        configSha256 = $(if ($null -ne $document) { $document.sha256 } else { '' })
        encodingKind = $(if ($null -ne $document) { $document.encodingKind } else { '' })
        duplicates = $duplicates
        fileValues = $fileValues
        runtimeValues = $runtimeValues
        runtimeOk = $runtime.ok
        runtimeError = $runtime.error
        runtimeVersion = $runtime.version
        webPort = $webPort
        fileMode = (Get-DisplayModeFromValues (Convert-FileValuesToMap $fileValues))
        runtimeMode = $(if ($runtime.ok) { Get-DisplayModeFromValues (Convert-RuntimeValuesToMap $runtimeValues) } else { '' })
    }
    return [ordered]@{
        state = $state; install = $install; zako = $zako; service = $service; safety = $safety
        admin = $state.admin; backupPath = $BackupPath; backupPresent = $state.backupPresent
        document = $document; entries = $entries; values = $values; duplicates = $duplicates
        runtime = $runtime; sessions = $sessions; webPort = $webPort; cert = $cert
    }
}

function Convert-FileValuesToMap($FileValues) {
    $map = @{}
    foreach ($key in $script:TargetKeyNames) {
        $entry = $FileValues[$key]
        $map[$key] = $(if ($entry.present) { [string] $entry.value } else { Get-EffectiveConfigValue @{} $key })
    }
    return $map
}

function Convert-RuntimeValuesToMap($RuntimeValues) {
    $map = @{}
    foreach ($key in $script:TargetKeyNames) {
        $value = $RuntimeValues[$key]
        $map[$key] = $(if ($null -eq $value) { Get-EffectiveConfigValue @{} $key } else { [string] $value })
    }
    return $map
}

function Test-CommonPreconditions($Context, [bool] $RequireAdmin) {
    $ok = $true
    if (-not $Context.install.exists) { Add-ToolBlocker '未找到 Foundation Sunshine 安装，未执行任何修改。'; $ok = $false }
    elseif (-not $Context.install.supported) { Add-ToolBlocker ('当前 Foundation Sunshine 版本未经验证：' + $Context.install.version); $ok = $false }
    if (-not $Context.zako.ok) { Add-ToolBlocker 'ZakoVDD（Zako Display Adapter）PnP 状态不是 OK，未执行任何修改。'; $ok = $false }
    if (@($Context.duplicates).Count -gt 0) { Add-ToolBlocker ('配置文件存在重复的目标键，请先手动清理：' + ($Context.duplicates -join '、')); $ok = $false }
    if ($RequireAdmin -and -not $Context.admin) { Add-ToolBlocker '需要管理员权限：sunshine.conf 只有 Administrators 可写。请以管理员身份运行本工具。'; $ok = $false }
    return $ok
}

function Test-SafetyPreconditions($Context, [bool] $IncludeRestart) {
    $ok = $true
    $safety = $Context.safety
    if (-not $safety.lockKnown) { Add-ToolBlocker '无法确认 Windows 锁屏状态，为安全拒绝修改。'; $ok = $false }
    elseif ($safety.locked) { Add-ToolBlocker '检测到 Windows 已锁屏：Sunshine 自身也会拒绝显示改动，请解锁后重试。'; $ok = $false }
    if (-not $safety.sessionsKnown) { Add-ToolBlocker ('无法确认 Sunshine 活动会话（' + $safety.sessionsError + '），为安全拒绝修改。'); $ok = $false }
    elseif ($safety.sessionCount -gt 0) { Add-ToolBlocker ('检测到 ' + $safety.sessionCount + ' 个进行中的 Sunshine 会话，拒绝修改显示配置。'); $ok = $false }
    if ($safety.tcpStreamHint) { Add-ToolBlocker '检测到 Sunshine 控制/RTSP 端口仍有远端连接，保守拒绝修改。'; $ok = $false }
    if ($IncludeRestart -and -not $Context.service.exists) { Add-ToolBlocker ('未找到 Windows 服务 ' + $script:ServiceName + '，无法重启验证。'); $ok = $false }
    return $ok
}

function Restart-SunshineAndVerify($TargetValues, [int] $WebPort) {
    $restart = [ordered]@{ ok = $false; error = ''; runtime = $null }
    try {
        $service = Get-Service -Name $script:ServiceName -ErrorAction Stop
        if ($service.Status -eq 'Running') { Restart-Service -Name $script:ServiceName -Force -ErrorAction Stop }
        else { Start-Service -Name $script:ServiceName -ErrorAction Stop }
    }
    catch { $restart.error = '重启 Sunshine 服务失败：' + $_.Exception.Message; return $restart }
    $cert = Get-SunshineCertificateThumbprint $SunshineRoot
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 1000
        $service = Get-SunshineServiceInfo
        if (-not $service.exists -or $service.status -ne 'Running') { continue }
        if (-not $cert.ok) { continue }
        $runtime = Get-RuntimeConfig $cert.thumbprint $WebPort
        if (-not $runtime.ok) { continue }
        $matches = $true
        foreach ($key in $TargetValues.Keys) {
            $expected = [string] $TargetValues[$key]
            $actual = $runtime.values[$key]
            if ($null -eq $actual -or [string] $actual -ne $expected) { $matches = $false }
        }
        if ($matches) { $restart.ok = $true; $restart.runtime = $runtime; return $restart }
    }
    $restart.error = 'Sunshine 服务重启后运行中配置未达到目标值（或接口未就绪）。'
    return $restart
}

function Write-StatusHuman($Context) {
    Add-ToolMessage ('双端远程 · Sunshine 显示模式工具 ' + $script:ToolVersion)
    Add-ToolMessage ('安装：' + $Context.install.root + '（' + (Format-ValueForText $Context.install.version) + '，' + $(if ($Context.install.supported) { '已验证' } else { '未验证' }) + '）')
    Add-ToolMessage ('服务：' + $script:ServiceName + ' = ' + $Context.service.status)
    $zakoText = ($Context.zako.devices | ForEach-Object { $_['instanceId'] + ' = ' + $_['status'] }) -join '; '
    Add-ToolMessage ('ZakoVDD：' + $(if ($zakoText) { $zakoText } else { '未找到' }))
    if ($Context.safety.sessionsKnown) { Add-ToolMessage ('会话：' + $Context.safety.sessionCount + ' 个') }
    else { Add-ToolMessage ('会话：无法确认（' + $Context.safety.sessionsError + '）') }
    Add-ToolMessage ('锁屏：' + $(if ($Context.safety.lockKnown) { $(if ($Context.safety.locked) { '已锁定' } else { '未锁定' }) } else { '未知' }))
    Add-ToolMessage ('权限：' + $(if ($Context.admin) { '管理员（可写入）' } else { '非管理员（写入需要管理员）' }))
    Add-ToolMessage ('配置：' + $Context.install.configPath + '（' + (Format-ValueForText $Context.state.encodingKind) + '）')
    Add-ToolMessage '文件值：'
    foreach ($key in $script:TargetKeyNames) { Add-ToolMessage ('  ' + $key + ' = ' + (Format-ValueForText (Get-EffectiveConfigValue $Context.values $key))) }
    if ($Context.runtime.ok) {
        Add-ToolMessage '运行值（当前服务）：'
        foreach ($key in $script:TargetKeyNames) { Add-ToolMessage ('  ' + $key + ' = ' + (Format-ValueForText $Context.runtime.values[$key])) }
    }
    else { Add-ToolMessage ('运行值：无法读取（' + $Context.runtime.error + '）') }
    Add-ToolMessage ('文件模式：' + (Get-DisplayModeLabel $Context.state.fileMode))
    if ($Context.runtime.ok) {
        Add-ToolMessage ('运行模式：' + (Get-DisplayModeLabel $Context.state.runtimeMode))
        $pending = $false
        foreach ($key in $script:TargetKeyNames) {
            $fileValue = Get-EffectiveConfigValue $Context.values $key
            $runtimeValue = $Context.runtime.values[$key]
            if ($null -eq $runtimeValue) { $runtimeValue = Get-EffectiveConfigValue @{} $key }
            if ([string] $fileValue -ne [string] $runtimeValue) { $pending = $true }
        }
        Add-ToolMessage ('状态：' + $(if ($pending) { '文件配置与运行中配置不一致，需重启 Sunshine 后生效。' } else { '文件配置与运行中配置一致。' }))
    }
    Add-ToolMessage ('备份：' + $(if ($Context.backupPresent) { $Context.backupPath } else { '不存在' }))
}

function Add-ChangeSummary($Changes) {
    foreach ($change in $Changes) {
        switch ($change.action) {
            'replace' { Add-ToolMessage ('  ' + $change.key + ': ' + (Format-ValueForText $change.before) + ' -> ' + (Format-ValueForText $change.after)) }
            'add'     { Add-ToolMessage ('  ' + $change.key + ': 新增 ' + (Format-ValueForText $change.after)) }
            'remove'  { Add-ToolMessage ('  ' + $change.key + ': 删除（原始为缺省）') }
        }
    }
}

function Invoke-StatusAction {
    $context = Get-EnvironmentState (Resolve-BackupPath $StatePath)
    $script:Result.state = $context.state
    $script:Result.ok = $true
    Write-StatusHuman $context
    Add-ToolMessage ''
    Add-ToolMessage '这是只读状态，没有写入任何文件。'
    Set-ToolSummary '状态读取完成'
    return 0
}

function Invoke-PreviewAction {
    $context = Get-EnvironmentState (Resolve-BackupPath $StatePath)
    $script:Result.state = $context.state
    if ([string]::IsNullOrWhiteSpace($Mode) -or -not $script:ModeValues.ContainsKey($Mode)) {
        Add-ToolBlocker 'Preview/Apply 必须指定 -Mode Privacy 或 -Mode Virtual。'
        return 2
    }
    $childBlockers = @($script:Result.blockers)
    $commonOk = Test-CommonPreconditions $context $false
    $adminOk = [bool] $context.admin
    if (-not $adminOk) { Add-ToolWarning '当前不是管理员：Preview 只读可用；Apply/Restore 需要以管理员身份运行。' }
    $safetyOk = Test-SafetyPreconditions $context $false
    $plan = @()
    if ($context.state.configExists) { $plan = New-ConfigChangePlan $context.entries $script:ModeValues[$Mode]; $script:Result.changes = $plan }
    Add-ToolMessage ('预览：' + (Get-DisplayModeLabel $Mode))
    Add-ToolMessage '计划修改：'
    Add-ChangeSummary $plan
    Add-ToolMessage ('可执行：' + $(if ($commonOk -and $safetyOk -and $adminOk) { '是' } else { '否，见下方阻塞项/警告' }))
    $script:Result.ok = $commonOk -and $safetyOk -and $adminOk
    Set-ToolSummary ('预览完成，未写入任何文件。' + $(if ($script:Result.ok) { '' } else { ' 存在阻塞项。' }))
    return 0
}

function Invoke-ApplyAction {
    $backupPath = Resolve-BackupPath $StatePath
    $context = Get-EnvironmentState $backupPath
    $script:Result.state = $context.state
    if ([string]::IsNullOrWhiteSpace($Mode) -or -not $script:ModeValues.ContainsKey($Mode)) {
        Add-ToolBlocker 'Apply 必须指定 -Mode Privacy 或 -Mode Virtual。'
        return 2
    }
    $target = $script:ModeValues[$Mode]
    $commonOk = Test-CommonPreconditions $context $true
    $safetyOk = Test-SafetyPreconditions $context $RestartSunshine.IsPresent
    if (-not $context.state.configExists) { Add-ToolBlocker '配置文件不可读，未执行任何修改。' }
    $plan = @()
    if ($context.state.configExists) { $plan = New-ConfigChangePlan $context.entries $target; $script:Result.changes = $plan }
    Add-ToolMessage ('目标模式：' + (Get-DisplayModeLabel $Mode))
    if ($plan.Count -gt 0) { Add-ToolMessage '变更计划：'; Add-ChangeSummary $plan }
    else { Add-ToolMessage '当前文件配置已经是目标值，无需写入。' }
    if (-not ($commonOk -and $safetyOk -and $context.state.configExists)) {
        Add-ToolMessage '未执行修改：存在阻塞项。'
        return 2
    }
    if ($plan.Count -eq 0) {
        if ($RestartSunshine) {
            $restart = Restart-SunshineAndVerify $target $context.webPort
            if (-not $restart.ok) { Add-ToolBlocker $restart.error; return 4 }
            Add-ToolMessage '服务已重启，运行中配置已与目标一致。'
        }
        $script:Result.ok = $true
        Set-ToolSummary '无需修改（已是目标配置）。'
        return 0
    }
    if (-not (Test-ConfigUnchanged $context.install.configPath $context.state.configSha256)) {
        Add-ToolBlocker '读取后配置文件已被其它程序修改（hash 不一致），已中止，未创建备份也未写入。'
        return 2
    }
    if ($context.backupPresent) {
        try { [void] (Read-BackupFile $backupPath $context.install.configPath) }
        catch {
            Add-ToolBlocker ('已有备份不可用或不属于当前安装：' + $_.Exception.Message + ' 请先移走该备份再执行。')
            return 2
        }
        Add-ToolWarning ('已有较早的基线备份，保留不覆盖：' + $backupPath + '（Restore 会回到该基线）。')
    }
    else {
        $backup = New-BackupObject $context.install $context.entries $context.state.configSha256 $Mode
        try { Write-BackupFile $backupPath $backup; Add-ToolMessage ('已保存原始值备份：' + $backupPath) }
        catch { Add-ToolBlocker ('保存备份失败，未修改配置：' + $_.Exception.Message); return 3 }
    }
    if (-not (Test-ConfigUnchanged $context.install.configPath $context.state.configSha256)) {
        Add-ToolBlocker '备份写入前检测到配置文件被并发修改（hash 不一致），已中止写入。'
        return 2
    }
    try { Write-ConfigDocument $context.install.configPath (Apply-ChangePlanToLines $context.document.lines $plan) $context.document; Add-ToolMessage ('已原子写入配置：' + $context.install.configPath) }
    catch { Add-ToolBlocker ('写入配置失败：' + $_.Exception.Message); return 3 }
    if (-not $RestartSunshine) {
        Add-ToolMessage '未重启 Sunshine：配置将在 Sunshine 下次启动后生效；当前串流不受影响。'
        Add-ToolMessage '如需立即生效，请空闲时加 -RestartSunshine 重试。'
        $script:Result.ok = $true
        Set-ToolSummary ('已写入 ' + $Mode + ' 配置（待 Sunshine 重启生效）。')
        return 0
    }
    $cert = Get-SunshineCertificateThumbprint $context.install.root
    $secondSessions = $(if ($cert.ok) { Get-RuntimeSessions $cert.thumbprint $context.webPort } else { [ordered]@{ ok = $false; count = -1; error = $cert.error } })
    $secondSafety = Get-SafetyState $secondSessions (Test-TcpStreamHint $context.webPort)
    if ($secondSafety.locked -or $secondSafety.streaming -or -not $secondSafety.sessionsKnown) {
        Add-ToolBlocker '写入后二次检查检测到锁屏或串流，已跳过重启；配置已写入但未生效。请空闲时重试。'
        return 2
    }
    $restart = Restart-SunshineAndVerify $target $context.webPort
    if ($restart.ok) {
        Add-ToolMessage 'Sunshine 服务已重启，运行中配置已验证为目标值。'
        $script:Result.ok = $true
        Set-ToolSummary ('已应用 ' + $Mode + ' 配置并重启验证成功。')
        return 0
    }
    Add-ToolWarning $restart.error
    Add-ToolWarning '正在尝试回滚配置...'
    $rollbackOk = $false
    try {
        $backup = Read-BackupFile $backupPath $context.install.configPath
        $currentDocument = Read-ConfigDocument $context.install.configPath
        if (-not (Test-ConfigUnchanged $currentDocument.path $currentDocument.sha256)) { throw '回滚前配置文件被并发修改' }
        $rollbackPlan = New-RestoreChangePlan (Get-ConfigEntries $currentDocument.lines) $backup
        Write-ConfigDocument $context.install.configPath (Apply-ChangePlanToLines $currentDocument.lines $rollbackPlan) $currentDocument
        $rollbackOk = $true
        Add-ToolWarning '配置已回滚到备份值。'
    }
    catch { Add-ToolWarning ('配置回滚失败，请手动检查：' + $_.Exception.Message) }
    if ($rollbackOk) {
        $cert = Get-SunshineCertificateThumbprint $context.install.root
        $reloadSessions = $(if ($cert.ok) { Get-RuntimeSessions $cert.thumbprint $context.webPort } else { [ordered]@{ ok = $false; count = -1; error = $cert.error } })
        $reloadSafety = Get-SafetyState $reloadSessions (Test-TcpStreamHint $context.webPort)
        if ($reloadSafety.sessionsKnown -and -not $reloadSafety.streaming -and -not $reloadSafety.locked) {
            $reloadTarget = [ordered]@{}
            foreach ($item in @($backup.keys)) { if ($item.present) { $reloadTarget[[string] $item.name] = [string] $item.value } }
            $reload = Restart-SunshineAndVerify $reloadTarget $context.webPort
            if ($reload.ok) { Add-ToolWarning '已重新加载回滚后的原始配置，运行态已与备份一致。' }
            else { Add-ToolWarning '文件已回滚，但重新加载原始配置失败；运行态待核对。' }
        }
        else {
            Add-ToolWarning '文件已回滚；检测到会话/锁屏或无法确认会话，未重启，运行态待核对。'
        }
    }
    return 4
}

function Invoke-RestoreAction {
    $backupPath = Resolve-BackupPath $StatePath
    $context = Get-EnvironmentState $backupPath
    $script:Result.state = $context.state
    if (-not $context.backupPresent) { Add-ToolBlocker ('未找到备份文件，无法还原：' + $backupPath); return 2 }
    try { $backup = Read-BackupFile $backupPath $context.install.configPath }
    catch { Add-ToolBlocker ('备份文件不可用：' + $_.Exception.Message); return 2 }
    $commonOk = Test-CommonPreconditions $context $true
    if (-not $context.state.configExists) { Add-ToolBlocker '配置文件不可读，未执行还原。' }
    if ($RestartSunshine -and ($context.safety.locked -or $context.safety.streaming -or -not $context.safety.sessionsKnown)) {
        Add-ToolBlocker '要求重启时必须无串流且未锁屏；当前无法确认或存在会话，未执行还原。'
        return 2
    }
    if (-not ($commonOk -and $context.state.configExists)) { Add-ToolMessage '未执行还原：存在阻塞项。'; return 2 }
    $restorePlan = New-RestoreChangePlan $context.entries $backup
    $script:Result.changes = $restorePlan
    Add-ToolMessage ('还原来源：' + $backupPath + '（' + (Format-ValueForText $backup.createdAtUtc) + '）')
    Add-ToolMessage '还原计划：'
    Add-ChangeSummary $restorePlan
    if ($restorePlan.Count -gt 0) {
        if (-not (Test-ConfigUnchanged $context.install.configPath $context.state.configSha256)) {
            Add-ToolBlocker '读取后配置文件已被其它程序修改（hash 不一致），已中止还原。'
            return 2
        }
        try { Write-ConfigDocument $context.install.configPath (Apply-ChangePlanToLines $context.document.lines $restorePlan) $context.document; Add-ToolMessage '已按备份原子还原配置。' }
        catch { Add-ToolBlocker ('写入配置失败：' + $_.Exception.Message); return 3 }
    }
    else { Add-ToolMessage '配置已经是备份中的原始值，无需写入。' }
    if ($RestartSunshine) {
        $cert = Get-SunshineCertificateThumbprint $context.install.root
        $secondSessions = $(if ($cert.ok) { Get-RuntimeSessions $cert.thumbprint $context.webPort } else { [ordered]@{ ok = $false; count = -1; error = $cert.error } })
        $secondSafety = Get-SafetyState $secondSessions (Test-TcpStreamHint $context.webPort)
        if ($secondSafety.locked -or $secondSafety.streaming -or -not $secondSafety.sessionsKnown) {
            Add-ToolBlocker '还原已写入，但二次检查检测到锁屏或串流，已跳过重启。请空闲时重试。'
            return 2
        }
        $target = [ordered]@{}
        foreach ($item in @($backup.keys)) { if ($item.present) { $target[[string] $item.name] = [string] $item.value } }
        $restart = Restart-SunshineAndVerify $target $context.webPort
        if (-not $restart.ok) { Add-ToolBlocker ($restart.error + ' 备份文件已保留：' + $backupPath); return 4 }
        Add-ToolMessage 'Sunshine 服务已重启，运行中配置已与备份一致。'
    }
    else { Add-ToolMessage '未重启 Sunshine：配置已还原，将在 Sunshine 下次启动后生效。' }
    try { Remove-Item -LiteralPath $backupPath -Force -ErrorAction Stop; Add-ToolMessage '备份文件已删除。' }
    catch { Add-ToolWarning ('备份文件删除失败（不影响还原）：' + $_.Exception.Message) }
    $script:Result.ok = $true
    Set-ToolSummary '已还原到备份中的原始显示配置。'
    return 0
}

function Invoke-ToolMain {
    Reset-ToolResult
    try {
        switch ($Action) {
            'Status'  { $code = Invoke-StatusAction }
            'Preview' { $code = Invoke-PreviewAction }
            'Apply'   { $code = Invoke-ApplyAction }
            'Restore' { $code = Invoke-RestoreAction }
            default   { $code = 1; Add-ToolBlocker ('未知 Action：' + $Action) }
        }
    }
    catch { $code = 1; Add-ToolBlocker ('未预期错误：' + $_.Exception.Message) }
    $script:Result.exitCode = $code
    if ($code -eq 0 -and -not $script:Result.ok) { $script:Result.exitCode = 1 }
    return $script:Result.exitCode
}

function Write-StdoutUtf8([string] $Text) {
    $writer = New-Object IO.StreamWriter([Console]::OpenStandardOutput(), (New-Object System.Text.UTF8Encoding($false)))
    try { $writer.Write($Text); $writer.Flush() } finally { $writer.Dispose() }
}

function Write-ToolOutput([int] $ExitCode) {
    if ($Json) {
        $script:Result.exitCode = $ExitCode
        Write-StdoutUtf8 ((ConvertTo-Json -InputObject $script:Result -Depth 10 -Compress) + "`r`n")
        return
    }
    foreach ($message in @($script:Result.messages)) { Write-Host $message }
    foreach ($warning in @($script:Result.warnings)) { Write-Host ('[注意] ' + $warning) -ForegroundColor Yellow }
    foreach ($blocker in @($script:Result.blockers)) { Write-Host ('[阻塞] ' + $blocker) -ForegroundColor Red }
    if ($script:Result.summary) { Write-Host ('[结果] ' + $script:Result.summary) }
}

if ($MyInvocation.InvocationName -ne '.') {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $exitCode = Invoke-ToolMain
    Write-ToolOutput $exitCode
    exit $exitCode
}
