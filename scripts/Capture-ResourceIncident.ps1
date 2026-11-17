#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Checkout,
    [string]$ProductionInstallation,
    [string]$Project = 'nightshift',
    [string]$Label = 'recurrence',
    [ValidateRange(2,6)][int]$Samples = 3,
    [ValidateRange(1,30)][int]$IntervalSeconds = 10,
    [string]$OutputRoot = $PSScriptRoot,
    [string]$TimingResult
)
$ErrorActionPreference = 'Stop'
$captureErrors = [Collections.Generic.List[string]]::new()
$labelPart = $Label -replace '[^a-zA-Z0-9_-]', '-'
$destination = Join-Path $OutputRoot ('resources-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $labelPart + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($destination) | Out-Null
$checkout = (Resolve-Path -LiteralPath $Checkout).Path
Import-Module (Join-Path $PSScriptRoot 'TestSuiteMeasurement.psm1') -Force
function Observe([string]$Name, [scriptblock]$Read) {
    try { & $Read }
    catch { $captureErrors.Add($Name + ': ' + $_.Exception.Message); $null }
}
$revision = Observe 'source revision' {
    $value = git -C $checkout rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve checkout HEAD' }
    $value
}
$productionObservation = Observe 'production observation' { Get-ProductionObservation $ProductionInstallation $Project }
if ($TimingResult) {
    Observe 'timing evidence' { Copy-Item -LiteralPath $TimingResult -Destination (Join-Path $destination 'triggering-timing-result.json') } | Out-Null
}
[ordered]@{
    schemaVersion = 1; investigation = 'ns-726.5'; capturedUtc = [DateTime]::UtcNow.ToString('o')
    label = $Label; sampleCount = $Samples; intervalSeconds = $IntervalSeconds
    checkout = $checkout; sourceRevision = $revision; production = $productionObservation
     tempRoot = [IO.Path]::GetTempPath()
    logicalProcessors = [Environment]::ProcessorCount
    triggeringTiming = $TimingResult
    processCounterSemantics = 'Process CPU and transfer counters are cumulative. Compare deltas only for the same PID plus creation time. Changing working set or free RAM alone does not prove a leak.'
    scope = 'Observation only: no test run, application restart, process termination, configuration change, command-line capture or environment dump.'
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $destination 'metadata.json')
for ($index = 0; $index -lt $Samples; $index++) {
    $errorStart = $captureErrors.Count
    $system = Observe 'operating system' {
        Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 10 |
            Select-Object @{n='lastBootUtc';e={$_.LastBootUpTime.ToUniversalTime().ToString('o')}},TotalVisibleMemorySize,FreePhysicalMemory,TotalVirtualMemorySize,FreeVirtualMemory
    }
    $memory = Observe 'memory performance counters' {
        Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory -OperationTimeoutSec 10 |
            Select-Object AvailableMBytes,CommittedBytes,CommitLimit,PoolPagedBytes,PoolNonpagedBytes,PagesPersec,PageReadsPersec,PageWritesPersec
    }
    $pageFiles = Observe 'page files' {
        Get-CimInstance Win32_PageFileUsage -OperationTimeoutSec 10 |
            Select-Object AllocatedBaseSize,CurrentUsage,PeakUsage
    }
    $processes = Observe 'process counters' {
        Get-CimInstance Win32_Process -OperationTimeoutSec 10 |
            Select-Object Name,ProcessId,ParentProcessId,@{n='createdUtc';e={if ($_.CreationDate) {$_.CreationDate.ToUniversalTime().ToString('o')}}},PrivatePageCount,WorkingSetSize,HandleCount,ThreadCount,KernelModeTime,UserModeTime,ReadTransferCount,WriteTransferCount,OtherTransferCount,PageFaults
    }
    $owner = Observe 'production observation' { Get-ProductionObservation $ProductionInstallation $Project }
    [ordered]@{
        sample = $index; observedUtc = [DateTime]::UtcNow.ToString('o')
        systemUptimeSeconds = [Environment]::TickCount64 / 1000.0
        system = $system; memory = $memory; pageFiles = @($pageFiles)
        production = $owner; processes = @($processes)
        errors = @($captureErrors | Select-Object -Skip $errorStart)
    } | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $destination ('sample-{0:D2}.json' -f $index))
    if ($index + 1 -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
}
ConvertTo-Json -InputObject ($captureErrors.ToArray()) | Set-Content (Join-Path $destination 'capture-errors.json')
[ordered]@{directory=$destination;samples=$Samples;errors=$captureErrors.Count;applicationMutationsRequested=$false} | ConvertTo-Json

