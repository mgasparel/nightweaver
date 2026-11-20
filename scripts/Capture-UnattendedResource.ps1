#requires -Version 5.1
# Private read adapter: contained in the collector's Job/deadline/output budget.
$ErrorActionPreference = 'Stop'
$system = Get-CimInstance Win32_OperatingSystem |
    Select-Object @{n='lastBootUtc';e={$_.LastBootUpTime.ToUniversalTime().ToString('o')}},TotalVisibleMemorySize,FreePhysicalMemory,TotalVirtualMemorySize,FreeVirtualMemory
$memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory |
    Select-Object AvailableMBytes,CommittedBytes,CommitLimit,PoolPagedBytes,PoolNonpagedBytes,PagesPersec,PageReadsPersec,PageWritesPersec
$pageFiles = @(Get-CimInstance Win32_PageFileUsage | Select-Object AllocatedBaseSize,CurrentUsage,PeakUsage)
$processes = @(Get-CimInstance Win32_Process |
    Select-Object ProcessId,ParentProcessId,@{n='createdUtc';e={if ($_.CreationDate) {$_.CreationDate.ToUniversalTime().ToString('o')}}},PrivatePageCount,WorkingSetSize,HandleCount,ThreadCount,KernelModeTime,UserModeTime,ReadTransferCount,WriteTransferCount,OtherTransferCount,PageFaults)
[ordered]@{
    schema='nightweaver-unattended-resource-v1'; observedUtc=[datetime]::UtcNow.ToString('o')
    systemUptimeSeconds=[Environment]::TickCount64/1000.0
    system=$system; memory=$memory; pageFiles=$pageFiles; processes=$processes
} | ConvertTo-Json -Depth 6 -Compress
