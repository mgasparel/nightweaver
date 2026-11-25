# Temporary observation for the one dispatched hosted investigation.
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$timer = [Diagnostics.Stopwatch]::StartNew()
$path = Join-Path $OutputDirectory 'resources.jsonl'
while ($timer.Elapsed.TotalSeconds -lt 720 -and !(Test-Path (Join-Path $OutputDirectory 'stop'))) {
    try {
        $system = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 5 |
            Select-Object TotalVisibleMemorySize,FreePhysicalMemory,TotalVirtualMemorySize,FreeVirtualMemory
        $processes = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5 |
            Where-Object { $_.Name -match '^(bd|dolt|dotnet|Nightweaver.*|testhost.*)\.exe$' } |
            Select-Object Name,ProcessId,ParentProcessId,CreationDate,KernelModeTime,UserModeTime,ReadTransferCount,WriteTransferCount,WorkingSetSize,PrivatePageCount,ThreadCount,HandleCount)
        @{ utc = [DateTime]::UtcNow.ToString('o'); system = $system; processes = $processes } |
            ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $path
    } catch {
        @{ utc = [DateTime]::UtcNow.ToString('o'); error = $_.Exception.Message } |
            ConvertTo-Json -Compress | Add-Content -LiteralPath $path
    }
    Start-Sleep -Seconds 2
}
