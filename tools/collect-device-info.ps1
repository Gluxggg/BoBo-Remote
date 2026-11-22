<#
.SYNOPSIS
    双端远程验收用设备自检（只读、无管理员权限）。

.DESCRIPTION
    只输出验收需要的硬件与链路字段：操作系统、CPU、内存、显卡与驱动、当前分辨率/刷新率、
    活动物理网卡类型（Wi-Fi/有线）与当前链路速率。

    不读取：用户名、计算机名、MAC 地址、IP 地址（含公网与内网）、Wi-Fi 名称/BSSID、
    任何密钥、账号、代理或浏览器数据；不修改任何系统设置。

.PARAMETER OutFile
    可选。把同样的内容写入指定文本文件（UTF-8，带 BOM，记事本可直接打开）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\collect-device-info.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\collect-device-info.ps1 -OutFile .\device-info.txt

.NOTES
    Windows PowerShell 5.1；只在本机执行，不联网。
#>
[CmdletBinding()]
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$lines = New-Object System.Collections.Generic.List[string]
function Add-Line {
    param([string]$Text)
    [void]$lines.Add($Text)
}

Add-Line '== 双端远程 · 设备自检（只读） =='
Add-Line ("采集时间：{0}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))

# 操作系统：用注册表版本号补足 Win32_OperatingSystem 里看不到的 22H2 / UBR
$os = Get-CimInstance -ClassName Win32_OperatingSystem
$currentVersion = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$productName = [string]$currentVersion.ProductName
if ([string]::IsNullOrWhiteSpace($productName)) { $productName = [string]$os.Caption }
$buildNumber = [string]$currentVersion.CurrentBuild
$ubr = [string]$currentVersion.UBR
$displayVersion = [string]$currentVersion.DisplayVersion
$buildValue = 0
[void][int]::TryParse($buildNumber, [ref]$buildValue)
if ($buildValue -ge 22000 -and $productName -match 'Windows 10') {
    $productName = $productName -replace 'Windows 10', 'Windows 11'
}
$architecture = if ([Environment]::Is64BitOperatingSystem) { '64 位' } else { '32 位' }
Add-Line ("操作系统：{0} {1}（Build {2}.{3}，{4}）" -f $productName, $displayVersion, $buildNumber, $ubr, $architecture)

# CPU
$cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
if ($cpu) {
    Add-Line ("CPU：{0}" -f $cpu.Name.Trim())
    Add-Line ("  物理核心 {0}，逻辑处理器 {1}，WMI 标称主频 {2} MHz（非睿频上限）" -f $cpu.NumberOfCores, $cpu.NumberOfLogicalProcessors, $cpu.MaxClockSpeed)
} else {
    Add-Line 'CPU：未查询到'
}

# 内存
$computer = Get-CimInstance -ClassName Win32_ComputerSystem
Add-Line ("内存：{0:N1} GiB" -f ($computer.TotalPhysicalMemory / 1GB))

# 显卡与当前显示模式；只取错误码为 0 的适配器
$gpus = @(Get-CimInstance -ClassName Win32_VideoController | Where-Object { $_.ConfigManagerErrorCode -eq 0 })
if ($gpus.Count -eq 0) {
    Add-Line '显示适配器：未查询到正常工作的适配器'
} else {
    $index = 0
    foreach ($gpu in $gpus) {
        $index++
        Add-Line ("显示适配器 {0}：{1}" -f $index, $gpu.Name)
        $driverDate = if ($gpu.DriverDate) { ([datetime]$gpu.DriverDate).ToString('yyyy-MM-dd') } else { '未知' }
        Add-Line ("  驱动：{0}（{1}）" -f $gpu.DriverVersion, $driverDate)
        if ($gpu.CurrentHorizontalResolution -and $gpu.CurrentVerticalResolution) {
            Add-Line ("  当前显示模式：{0}x{1} @ {2} Hz" -f $gpu.CurrentHorizontalResolution, $gpu.CurrentVerticalResolution, $gpu.CurrentRefreshRate)
        } else {
            Add-Line '  当前显示模式：未报告（该适配器未驱动显示器）'
        }
    }
}

# 活动物理网卡：只报类型、型号与当前链路速率，不报名称/IP/MAC
Add-Line '网络（活动物理网卡）：'
$adapters = @()
$adapterQueryOk = $true
try {
    $adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' })
} catch {
    $adapterQueryOk = $false
    Add-Line '  无法调用 Get-NetAdapter（该模块在本机不可用）'
}
if ($adapterQueryOk -and $adapters.Count -eq 0) {
    Add-Line '  没有处于连接状态的物理网卡'
}
if ($adapters.Count -gt 0) {
    foreach ($adapter in $adapters) {
        $description = [string]$adapter.InterfaceDescription
        $isWifi = ($adapter.MediaType -match '802\.11') -or
                  ($adapter.PhysicalMediaType -match '802\.11') -or
                  ($description -match '(?i)wi-?fi|wireless|wlan')
        $type = if ($isWifi) { 'Wi-Fi' } else { '有线' }
        $linkSpeed = if ($adapter.LinkSpeed) { [string]$adapter.LinkSpeed } else { '未知' }
        Add-Line ("  类型：{0}（{1}）" -f $type, $description)
        Add-Line ("  当前链路速率：{0}" -f $linkSpeed)
    }
}

Add-Line ''
Add-Line '说明：'
Add-Line '  - 只读取上述硬件与链路字段；不读取用户名、MAC、IP（含公网）、Wi-Fi 名称/密码、账号或密钥。'
Add-Line '  - “当前链路速率”是 Windows 报告的协商值，不代表实测吞吐。'
Add-Line '  - 显示适配器里可能包含远控/串流软件注册的虚拟显示器，属正常现象；判断硬件编码能力请看物理显卡。'
Add-Line '  - 本工具不修改任何系统设置，不需要管理员权限。'

$target = $OutFile
if (-not [string]::IsNullOrWhiteSpace($target)) {
    if (-not [System.IO.Path]::IsPathRooted($target)) {
        $target = Join-Path (Get-Location).Path $target
    }
    $encoding = New-Object System.Text.UTF8Encoding -ArgumentList $true
    [System.IO.File]::WriteAllLines($target, $lines, $encoding)
    Write-Host "已写入文件：$target"
}

foreach ($line in $lines) { Write-Output $line }
