<#
.SYNOPSIS
    对指定 Tailscale 节点做路径抽样，统计直连/peer-relay/DERP/未分类、RTT 分位数与超时比。

.DESCRIPTION
    调用本机 tailscale.exe 的 ping 命令做固定次数采样，逐行解析真实回包，严格区分：
      - 直连（显式 UDP endpoint，如 192.0.2.10:41641）
      - peer-relay（对端中继）
      - DERP（Tailscale 的 TCP 中继）
      - 未分类（disco、ICMP、TSMP、其它无法确认端点的路径，一律不计直连）
    超时按真实 "timed out" 行逐条计数；无法解析的结果单独列出，不补算成超时。
    401、already in use、lookup 失败等命令行/权限问题单独判为“测量无效”，不算丢包。

    只读取探测结果，不修改 Tailscale 配置、不登录、不启用转发。输出不含目标名称/IP。
    注意：Tailscale 网络层 RTT 不是 Moonlight 的视频掉帧率或解码耗时，不能互相替代。

.PARAMETER Peer
    要探测的 Tailscale 节点名或 Tailscale IP（由用户按自己环境填写，本工具不预置、不回显）。
.PARAMETER Samples
    探测次数，默认 25。验收建议不少于 20 次。
.PARAMETER PerPingTimeoutSeconds
    单次探测超时秒数，默认 2。
.PARAMETER OutFile
    可选。把汇总写入指定文本文件（UTF-8，带 BOM）。文件内容同样不写目标名称/IP。
.PARAMETER SelfTest
    用内置合成样本验证解析与分类逻辑，不联网、不需要 Peer。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\measure-tailscale-path.ps1 -Peer <节点名> -OutFile .\tailscale-path.txt

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\measure-tailscale-path.ps1 -SelfTest

.NOTES
    退出码：0 = 测量有效；2 = 命令行/权限/整体超时，本次无效；3 = 全部请求都真实超时。
    Windows PowerShell 5.1；需要本机已安装 Tailscale 并已登录（本工具不负责登录）。
#>
[CmdletBinding()]
param(
    [string]$Peer,
    [int]$Samples = 25,
    [int]$PerPingTimeoutSeconds = 2,
    [string]$OutFile,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-TailscaleExe {
    $command = Get-Command -Name 'tailscale.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $fallback = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw '未找到 tailscale.exe。请先安装 Tailscale 并确认命令行工具可用；本工具不负责安装或登录。'
}

function Get-Percentile {
    param(
        [double[]]$SortedValues,
        [double]$Percent
    )
    if ($SortedValues.Count -eq 0) { return $null }
    $rank = [int][Math]::Ceiling(($Percent / 100.0) * $SortedValues.Count)
    if ($rank -lt 1) { $rank = 1 }
    if ($rank -gt $SortedValues.Count) { $rank = $SortedValues.Count }
    return $SortedValues[$rank - 1]
}

# 直接匹配 tailscale ping 的 "pong from ... (...) via ... in ..." 行
# 兼容 ms / s / µs(us) 三种时延单位
$script:PongRegex = [regex]'(?i)pong from .*?\(([^)]*)\)(?:, [^)]*)? via (.+?) in ([0-9]+(?:\.[0-9]+)?)(µs|us|ms|s)'

# 明确的 UDP endpoint，例如 192.0.2.10:41641、[2001:db8::1]:41641、host-1:41641
$script:EndpointRegex = [regex]'^(?:\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9._-]+):[0-9]{1,5}$'

function Convert-RttToMilliseconds {
    param([string]$Value, [string]$Unit)
    $number = [double]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
    switch ($Unit.ToLowerInvariant()) {
        'ms' { return $number }
        's'  { return $number * 1000.0 }
        'us' { return $number / 1000.0 }
        'µs' { return $number / 1000.0 }
        default { return $null }
    }
}

# 逐行解析，返回分类计数、RTT 列表、超时行、错误与未解析行
function Convert-PingOutput {
    param([string[]]$Lines)

    $rtts = New-Object System.Collections.Generic.List[double]
    $direct = 0
    $peerRelay = 0
    $derp = 0
    $unclassified = New-Object System.Collections.Generic.List[string]
    $timeouts = 0
    $errors = New-Object System.Collections.Generic.List[string]
    $unparsed = New-Object System.Collections.Generic.List[string]

    foreach ($raw in $Lines) {
        if ($null -eq $raw) { continue }
        $line = ([string]$raw).Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        # 401 / 权限 / 解析失败等：属于测量环境问题，不是丢包
        if ($line -match '(?i)already in use|401|not logged|not running|access denied|no such host|lookup .*failed|failed to connect|permission denied') {
            [void]$errors.Add($line)
            continue
        }

        $match = $script:PongRegex.Match($line)
        if ($match.Success) {
            $route = $match.Groups[2].Value.Trim()
            $rtt = Convert-RttToMilliseconds -Value $match.Groups[3].Value -Unit $match.Groups[4].Value
            if ($null -eq $rtt) {
                [void]$unparsed.Add($line)
                continue
            }
            [void]$rtts.Add($rtt)

            if ($route -match '(?i)^DERP\(') { $derp++; continue }
            if ($route -match '(?i)^peer-relay\(') { $peerRelay++; continue }
            if ($script:EndpointRegex.IsMatch($route)) { $direct++; continue }

            # disco / ICMP / TSMP / 其它无法确认端点的路径：绝不默认算直连
            [void]$unclassified.Add($route)
            continue
        }

        # 真实超时行，逐条计数
        if ($line -match '(?i)timed out|timeout waiting|no response') {
            $timeouts++
            continue
        }

        # "no reply" 是命令结束时的汇总提示，不重复计超时
        if ($line -match '(?i)^no reply\s*$') { continue }

        if ($line -match '(?i)error|failed|unknown peer') {
            [void]$errors.Add($line)
            continue
        }

        [void]$unparsed.Add($line)
    }

    return [pscustomobject]@{
        Rtts         = $rtts
        Direct       = $direct
        PeerRelay    = $peerRelay
        Derp         = $derp
        Unclassified = $unclassified
        Timeouts     = $timeouts
        Errors       = $errors
        Unparsed     = $unparsed
    }
}

function Format-Stats {
    param(
        [int]$Requested,
        $Parsed,
        [double]$ElapsedSeconds
    )

    $pongs = $Parsed.Direct + $Parsed.PeerRelay + $Parsed.Derp + $Parsed.Unclassified.Count
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('== Tailscale 路径抽样（网络层，不是视频丢包） ==')
    [void]$lines.Add('目标：用户指定节点（名称/IP 不打印）')
    [void]$lines.Add(("请求 {0} 次，有效回应 {1} 次；真实超时 {2} 次；未解析结果 {3} 条；命令行错误 {4} 条" -f `
        $Requested, $pongs, $Parsed.Timeouts, $Parsed.Unparsed.Count, $Parsed.Errors.Count))

    if ($pongs -gt 0) {
        [void]$lines.Add(("路径（按回应计）：直连 {0}（{1:N1}%）、peer-relay {2}（{3:N1}%）、DERP {4}（{5:N1}%）、未分类 {6}（{7:N1}%）" -f `
            $Parsed.Direct, (($Parsed.Direct / [double]$pongs) * 100),
            $Parsed.PeerRelay, (($Parsed.PeerRelay / [double]$pongs) * 100),
            $Parsed.Derp, (($Parsed.Derp / [double]$pongs) * 100),
            $Parsed.Unclassified.Count, (($Parsed.Unclassified.Count / [double]$pongs) * 100)))

        $sorted = @($Parsed.Rtts | Sort-Object)
        $p50 = Get-Percentile -SortedValues $sorted -Percent 50
        $p95 = Get-Percentile -SortedValues $sorted -Percent 95
        [void]$lines.Add(("RTT：p50 {0:N1} ms，p95 {1:N1} ms，最小 {2:N1} ms，最大 {3:N1} ms（仅统计成功回包）" -f `
            $p50, $p95, $sorted[0], $sorted[$sorted.Count - 1]))
    } else {
        [void]$lines.Add('路径/RTT：没有成功回包，无法统计。')
    }

    if ($Parsed.Unclassified.Count -gt 0) {
        $routes = @($Parsed.Unclassified | Sort-Object -Unique)
        [void]$lines.Add('未分类路径（不计入直连）：' + ($routes -join '；'))
    }
    if ($Parsed.Unparsed.Count -gt 0) {
        [void]$lines.Add('未解析结果（不补算超时）：' + (($Parsed.Unparsed | Select-Object -First 3) -join '；'))
    }
    if ($Parsed.Errors.Count -gt 0) {
        [void]$lines.Add('命令行错误（不算丢包）：' + (($Parsed.Errors | Select-Object -First 3) -join '；'))
    }

    [void]$lines.Add(("总耗时：约 {0:N1} 秒" -f $ElapsedSeconds))
    if ($pongs -gt 0) {
        [void]$lines.Add('参考目标：跨网直连场景 RTT p95 ≤ 50 ms（由两机实测记录判定，本工具不自动下结论）')
        [void]$lines.Add('路径口径：只有显式 UDP endpoint 才算直连；DERP、peer-relay、disco 与其它未确认路径都不计入直连。')
    }
    [void]$lines.Add('说明：Tailscale 层 RTT 与 Moonlight 的“网络掉帧/解码耗时”是两回事，不能互相替代。')
    return $lines
}

function Write-Result {
    param($Lines, [string]$OutFile)
    foreach ($line in $Lines) { Write-Output $line }

    if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
        $target = $OutFile
        if (-not [System.IO.Path]::IsPathRooted($target)) {
            $target = Join-Path (Get-Location).Path $target
        }
        $encoding = New-Object System.Text.UTF8Encoding -ArgumentList $true
        [System.IO.File]::WriteAllLines($target, $Lines, $encoding)
        Write-Host "已写入文件：$target"
    }
}

# 用 Process 同步读取 stdout/stderr，避免 PS 5.1 把原生命令 stderr 变成 NativeCommandError
function Invoke-TailscalePing {
    param(
        [string]$ExePath,
        [string]$Target,
        [int]$Count,
        [int]$TimeoutSeconds
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ExePath
    $psi.Arguments = "ping --c $Count --timeout $($TimeoutSeconds)s --until-direct=false `"$Target`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()

    # 整体超时：按最坏情况多留余量；被整体终止时本次测量判无效，不把残余结果当超时
    $overallTimeoutMs = [Math]::Max(15000, (($Count * ($TimeoutSeconds + 2)) + 10) * 1000)
    $timedOut = -not $process.WaitForExit($overallTimeoutMs)
    if ($timedOut) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit() } catch { }
    }
    $stopwatch.Stop()

    $stdout = ''
    $stderr = ''
    try { $stdout = $outTask.Result } catch { }
    try { $stderr = $errTask.Result } catch { }
    $exitCode = -1
    if (-not $timedOut) {
        try { $exitCode = $process.ExitCode } catch { $exitCode = -1 }
    }
    try { $process.Dispose() } catch { }

    return [pscustomobject]@{
        StdOut   = $stdout
        StdErr   = $stderr
        ExitCode = $exitCode
        TimedOut = $timedOut
        Elapsed  = $stopwatch.Elapsed.TotalSeconds
    }
}

function Join-OutputLines {
    param([string]$StdOut, [string]$StdErr)
    $result = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrEmpty($StdOut)) {
        foreach ($line in ($StdOut -split "`r?`n")) { if ($line.Length -gt 0) { [void]$result.Add($line) } }
    }
    if (-not [string]::IsNullOrEmpty($StdErr)) {
        foreach ($line in ($StdErr -split "`r?`n")) { if ($line.Length -gt 0) { [void]$result.Add($line) } }
    }
    return $result.ToArray()
}

if ($SelfTest) {
    # 合成样本：2 直连（一个 ms、一个 µs）、1 DERP、1 peer-relay、1 disco（未分类）、
    # 1 条 s 单位直连、1 条真实超时、1 条 no reply 汇总、1 条未解析、1 条 401 错误
    $synthetic = @(
        'pong from node-a (100.64.0.1) via 192.0.2.10:41641 in 26ms',
        'pong from node-a (100.64.0.1) via 192.0.2.10:41641 in 850µs',
        'pong from node-a (100.64.0.1) via DERP(hkg) in 41ms',
        'pong from node-a (100.64.0.1) via peer-relay(node-b) in 33ms',
        'pong from node-a (100.64.0.1) via disco in 5ms',
        'pong from node-a (100.64.0.1) via 192.0.2.10:41641 in 1.2s',
        'ping "100.64.0.1" timed out',
        'no reply',
        'some unexpected line',
        'status: 401 already in use by SYSTEM'
    )
    $parsed = Convert-PingOutput -Lines $synthetic
    $pongs = $parsed.Direct + $parsed.PeerRelay + $parsed.Derp + $parsed.Unclassified.Count
    $lines = Format-Stats -Requested 7 -Parsed $parsed -ElapsedSeconds 3.0
    Write-Result -Lines $lines -OutFile ''

    $ok = $parsed.Direct -eq 3 -and $parsed.Derp -eq 1 -and $parsed.PeerRelay -eq 1 -and
          $parsed.Unclassified.Count -eq 1 -and $parsed.Timeouts -eq 1 -and
          $parsed.Unparsed.Count -eq 1 -and $parsed.Errors.Count -eq 1 -and
          $pongs -eq 6 -and [Math]::Abs(($parsed.Rtts | Measure-Object -Average).Average - 217.6417) -lt 0.01
    if ($ok) {
        Write-Host '[自测通过] 3 直连(ms/µs/s) / 1 DERP / 1 peer-relay / 1 未分类 / 1 真实超时 / 1 未解析 / 1 错误' -ForegroundColor Green
        exit 0
    }
    Write-Host "[自测失败] 直连 $($parsed.Direct)、DERP $($parsed.Derp)、peer-relay $($parsed.PeerRelay)、未分类 $($parsed.Unclassified.Count)、超时 $($parsed.Timeouts)、未解析 $($parsed.Unparsed.Count)、错误 $($parsed.Errors.Count)、回应 $pongs" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($Peer)) {
    throw '缺少 -Peer 参数。示例：-Peer <节点名>；或使用 -SelfTest 只验证解析逻辑。'
}
if ($Peer -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{0,252}$') {
    throw '节点名/地址格式不受支持：只允许字母、数字、点、短横线、下划线和冒号。'
}
if ($Samples -lt 5) { throw '样本数太少（至少 5 次）；验收建议 20 次以上。' }
if ($Samples -lt 20) { Write-Warning "样本数 $Samples 少于验收建议的 20 次，本次结果只作参考。" }

$tailscaleExe = Get-TailscaleExe
$plannedSeconds = $Samples * ($PerPingTimeoutSeconds + 1)
Write-Host "开始 Tailscale 路径抽样：$Samples 次，单次超时 ${PerPingTimeoutSeconds}s（最坏约 $plannedSeconds 秒）；目标名称不打印。" -ForegroundColor Cyan

$run = Invoke-TailscalePing -ExePath $tailscaleExe -Target $Peer -Count $Samples -TimeoutSeconds $PerPingTimeoutSeconds
$lines = Join-OutputLines -StdOut $run.StdOut -StdErr $run.StdErr
$parsed = Convert-PingOutput -Lines $lines
$pongs = $parsed.Direct + $parsed.PeerRelay + $parsed.Derp + $parsed.Unclassified.Count

if ($run.TimedOut) {
    Write-Host '本次测量无效：tailscale ping 整体超时被终止，残余结果不作数。' -ForegroundColor Red
    exit 2
}

if ($pongs -eq 0) {
    if ($parsed.Timeouts -gt 0) {
        # 全部请求都真实超时：这是有效的“链路不可用”结果，不是丢包率虚报
        $summary = Format-Stats -Requested $Samples -Parsed $parsed -ElapsedSeconds $run.Elapsed
        Write-Result -Lines $summary -OutFile $OutFile
        Write-Host '全部请求真实超时：确认对端在线、已登录 Tailscale、没有被防火墙拦截。' -ForegroundColor Yellow
        exit 3
    }

    Write-Host '本次测量无效：没有可用回包，也没有可确认的超时行。' -ForegroundColor Red
    Write-Host "tailscale 退出码：$($run.ExitCode)" -ForegroundColor Red
    if ($parsed.Errors.Count -gt 0) {
        foreach ($errorLine in ($parsed.Errors | Select-Object -First 5)) { Write-Host "  $errorLine" -ForegroundColor Red }
    } else {
        Write-Host '  常见原因：节点名写错、Tailscale 未登录、当前用户读不到状态（例如 already in use by SYSTEM），或需要管理员权限。' -ForegroundColor Red
        Write-Host '  处理：用管理员身份重新打开 PowerShell 再试；本工具不会自动提权，也不会改 Tailscale 配置。' -ForegroundColor Red
    }
    exit 2
}

$summary = Format-Stats -Requested $Samples -Parsed $parsed -ElapsedSeconds $run.Elapsed
Write-Result -Lines $summary -OutFile $OutFile
exit 0
