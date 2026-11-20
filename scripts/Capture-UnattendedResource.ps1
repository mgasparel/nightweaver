#requires -Version 5.1
# Private read adapter: contained in the collector's Job/deadline/output budget.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$operatingSystem = Get-CimInstance Win32_OperatingSystem
if ($operatingSystem.LastBootUpTime -isnot [datetime]) { throw 'OS boot time unavailable' }
$bootUtc = $operatingSystem.LastBootUpTime.ToUniversalTime()
$system = $operatingSystem |
    Select-Object @{n='lastBootUtc';e={$_.LastBootUpTime.ToUniversalTime().ToString('o')}},TotalVisibleMemorySize,FreePhysicalMemory,TotalVirtualMemorySize,FreeVirtualMemory
$memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory |
    Select-Object AvailableMBytes,CommittedBytes,CommitLimit,PoolPagedBytes,PoolNonpagedBytes,PagesPersec,PageReadsPersec,PageWritesPersec
$pageFiles = @(Get-CimInstance Win32_PageFileUsage | Select-Object AllocatedBaseSize,CurrentUsage,PeakUsage)
$processes = @(Get-CimInstance Win32_Process |
    Select-Object ProcessId,ParentProcessId,@{n='createdUtc';e={if ($_.CreationDate) {$_.CreationDate.ToUniversalTime().ToString('o')}}},PrivatePageCount,WorkingSetSize,HandleCount,ThreadCount,KernelModeTime,UserModeTime,ReadTransferCount,WriteTransferCount,OtherTransferCount,PageFaults)
$observedUtc = [datetime]::UtcNow
if ($bootUtc -gt $observedUtc) { throw 'OS boot time follows observation time' }
[ordered]@{
    schema='nightweaver-unattended-resource-v1'; observedUtc=$observedUtc.ToString('o')
    systemUptimeSeconds=($observedUtc-$bootUtc).TotalSeconds
    system=$system; memory=$memory; pageFiles=$pageFiles; processes=$processes
} | ConvertTo-Json -Depth 6 -Compress
