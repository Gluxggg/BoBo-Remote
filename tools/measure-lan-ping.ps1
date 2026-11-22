<#
.SYNOPSIS
    局域网基线抽样：对默认网关（或指定内网目标）做固定次数 ping，统计 RTT p50/p95 与超时比。

.DESCRIPTION
    这是三类测量里的第一类：本地局域网基线。用来判断本机的 Wi-Fi/网卡本身是否拖后腿。
    它既不是 Tailscale 跨网 RTT，也不是 Moonlight 的串流网络指标，三类数据分开记录，不能混用。

    不打印目标 IP，不读取任何账号、密钥或配置，不修改系统设置。

.PARAMETER Target
    可选。内网目标地址或主机名；省略时自动使用当前 IPv4 默认网关。
.PARAMETER Count
    探测次数，默认 24（对应“空闲时 24 次”的基线建议）。
.PARAMETER OutFile
    可选。把汇总写入指定文本文件（UTF-8，带 BOM）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\measure-lan-ping.ps1

.NOTES
    退出码：0 = 有成功回包；3 = 全部超时（本地网络不可用）。
    Windows PowerShell 5.1；只在本机执行，不联网。
#>
[CmdletBinding()]
param(
    [string]$Target,
    [int]$Count = 24,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

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

if ($Count -lt 5) { throw '探测次数太少（至少 5 次）；基线建议 24 次。' }

$targetLabel = '默认网关'
$pingTarget = $Target
if ([string]::IsNullOrWhiteSpace($pingTarget)) {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } |
        Sort-Object RouteMetric |
        Select-Object -First 1
    if (-not $route) {
        throw '找不到 IPv4 默认网关（可能没联网或路由异常）。请先确认网络，或用 -Target 指定内网目标。'
    }
    $pingTarget = $route.NextHop
    $targetLabel = '默认网关'
} else {
    $targetLabel = '用户指定目标'
}

Write-Host "局域网基线抽样：$targetLabel，$Count 次（约 $Count 秒）" -ForegroundColor Cyan
Write-Host '测量前请先停止下载、串流等大流量操作，等几秒再开始。' -ForegroundColor Cyan

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$responses = @(Test-Connection -ComputerName $pingTarget -Count $Count -Delay 1 -ErrorAction SilentlyContinue)
$stopwatch.Stop()

$rtts = @($responses | ForEach-Object { $_.ResponseTime } | Where-Object { $null -ne $_ })
$success = $rtts.Count
$timeouts = $Count - $success
if ($timeouts -lt 0) { $timeouts = 0 }

$lines = New-Object System.Collections.Generic.List[string]
[void]$lines.Add('== 局域网基线抽样（本地，非同 Tailscale，也非 Moonlight 串流） ==')
[void]$lines.Add("目标：$targetLabel（不打印具体地址）")
[void]$lines.Add(("探测 {0} 次，成功 {1} 次，超时 {2} 次，超时比 {3:N1}%" -f $Count, $success, $timeouts, (($timeouts / [double]$Count) * 100)))

if ($rtts.Count -gt 0) {
    $sorted = @($rtts | Sort-Object)
    $p50 = Get-Percentile -SortedValues $sorted -Percent 50
    $p95 = Get-Percentile -SortedValues $sorted -Percent 95
    [void]$lines.Add(("RTT：p50 {0:N0} ms，p95 {1:N0} ms，最小 {2:N0} ms，最大 {3:N0} ms" -f `
        $p50, $p95, $sorted[0], $sorted[$sorted.Count - 1]))
} else {
    [void]$lines.Add('RTT：全部超时，无法统计')
}
[void]$lines.Add(("总耗时：约 {0:N1} 秒" -f $stopwatch.Elapsed.TotalSeconds))
[void]$lines.Add('说明：本结果只说明本机到局域网网关的链路状态。')
[void]$lines.Add('      如果这里抖动明显，先处理 Wi-Fi 信号/干扰或改用网线，再测 Tailscale 和 Moonlight。')

foreach ($line in $lines) { Write-Output $line }

if (-not [string]::IsNullOrWhiteSpace($OutFile)) {
    $target = $OutFile
    if (-not [System.IO.Path]::IsPathRooted($target)) {
        $target = Join-Path (Get-Location).Path $target
    }
    $encoding = New-Object System.Text.UTF8Encoding -ArgumentList $true
    [System.IO.File]::WriteAllLines($target, $lines, $encoding)
    Write-Host "已写入文件：$target"
}

if ($success -eq 0) { exit 3 }
exit 0
