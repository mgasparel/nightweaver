[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [Parameter(Mandatory)][string]$IncidentDirectory
)
$ErrorActionPreference = 'Stop'

# Pure retained-file analysis. Emit only allowlisted counters/identities, never
# command lines, environment, configuration contents or whole status documents.
function Read-Utc($value) {
    if ($null -eq $value -or "$value" -eq '') { return $null }
    if ($value -is [DateTime]) { return $value.ToUniversalTime() }
    if ($value -is [DateTimeOffset]) { return $value.UtcDateTime }
    try { return [DateTimeOffset]::Parse("$value").UtcDateTime }
    catch { return $null }
}
function Identity($process) {
    $created = Read-Utc $process.createdUtc
    if ($null -eq $process.ProcessId -or $null -eq $created) { return $null }
    return "$($process.ProcessId)@$($created.ToString('o'))"
}
function Index-Processes($sample) {
    $map = @{}
    foreach ($process in $sample.processes) {
        $key = Identity $process
        if ($key) {
            if ($map.ContainsKey($key)) { throw "Duplicate process identity: $key" }
            $map[$key] = $process
        }
    }
    return $map
}
function Parent-Identity($process, $map) {
    # The observed parent must predate the child. A reused parent PID is unknown.
    $childTime = Read-Utc $process.createdUtc
    $parents = @($map.Keys | Where-Object {
        $p = $map[$_]
        $p.ProcessId -eq $process.ParentProcessId -and
        $p.ProcessId -ne $process.ProcessId -and
        (Read-Utc $p.createdUtc) -le $childTime
    })
    if ($parents.Count -eq 1) { return $parents[0] }
    return $null
}
function Process-Row($key, $map, $sample) {
    $p = $map[$key]
    $parent = Parent-Identity $p $map
    $group = 'unattributed'
    $owner = $sample.production
    $ownerTime = Read-Utc $owner.process_started_at_utc
    $ownerKey = $null
    foreach ($candidate in $map.Keys) {
        $c = $map[$candidate]
        # Win32_Process creation timestamps have microsecond precision; status
        # retains 100ns ticks. Compare at the coarser precision, never by PID alone.
        if ($null -ne $ownerTime -and $c.ProcessId -eq $owner.process_id -and
            [math]::Floor((Read-Utc $c.createdUtc).Ticks / [decimal]10) -eq
            [math]::Floor($ownerTime.Ticks / [decimal]10)) { $ownerKey = $candidate }
    }
    $cursor = $key
    $visited = @{}
    while ($cursor -and -not $visited.ContainsKey($cursor)) {
        $visited[$cursor] = $true
        if ($cursor -eq $ownerKey) {
            $group = if ($key -eq $ownerKey) { 'production-owner' } else { 'production-descendant' }
            break
        }
        $cursor = Parent-Identity $map[$cursor] $map
    }
    [ordered]@{
        identity=$key; name=$p.Name; parentIdentity=$parent; group=$group
        observed=$p | Select-Object PrivatePageCount,WorkingSetSize,HandleCount,ThreadCount
    }
}
function Counter-Delta($before, $after, [bool]$Cumulative) {
    if ($null -eq $before -or $null -eq $after) { return $null }
    $delta = [decimal]$after - [decimal]$before
    if ($Cumulative -and $delta -lt 0) { return $null }
    return $delta
}
function Compare-Samples($before, $after) {
    $beforeTime = Read-Utc $before.observedUtc
    $afterTime = Read-Utc $after.observedUtc
    if ($null -eq $beforeTime -or $null -eq $afterTime) { throw 'Missing or invalid sample observation time' }
    $seconds = ($afterTime - $beforeTime).TotalSeconds
    if ($seconds -le 0) { throw 'Samples must have increasing valid observation times' }
    $left = Index-Processes $before
    $right = Index-Processes $after
    $changes = @(foreach ($key in ($left.Keys | Sort-Object)) {
        if (-not $right.ContainsKey($key)) { continue }
        $row = Process-Row $key $right $after
        $delta = [ordered]@{}
        $rates = [ordered]@{}
        foreach ($field in @('PrivatePageCount','WorkingSetSize','HandleCount','ThreadCount')) {
            $delta[$field] = Counter-Delta $left[$key].$field $right[$key].$field $false
        }
        foreach ($field in @('KernelModeTime','UserModeTime','ReadTransferCount','WriteTransferCount','OtherTransferCount')) {
            $d = Counter-Delta $left[$key].$field $right[$key].$field $true
            $rates[$field] = if ($null -eq $d) { $null } else { $d / [decimal]$seconds }
        }
        # CPU is core-seconds per elapsed second (not percent of all CPUs).
        $row.cpuCores = if ($null -eq $rates.KernelModeTime -or $null -eq $rates.UserModeTime) {
            $null
        } else { ($rates.KernelModeTime + $rates.UserModeTime) / 10000000 }
        $row.delta = $delta
        $row.before = $left[$key] | Select-Object PrivatePageCount,WorkingSetSize,HandleCount,ThreadCount
        $row.after = $right[$key] | Select-Object PrivatePageCount,WorkingSetSize,HandleCount,ThreadCount
        $row.rates = $rates
        $row
    })
    [ordered]@{
        seconds=$seconds
        unknownBefore=@($before.processes | Where-Object { -not (Identity $_) }).Count
        unknownAfter=@($after.processes | Where-Object { -not (Identity $_) }).Count
        changes=$changes
        appeared=@(foreach ($k in ($right.Keys | Sort-Object)) {
            if (-not $left.ContainsKey($k)) { Process-Row $k $right $after }
        })
        disappeared=@(foreach ($k in ($left.Keys | Sort-Object)) {
            if (-not $right.ContainsKey($k)) { Process-Row $k $left $before }
        })
    }
}
function Read-Capture([string]$Directory) {
    $metadata = Get-Content -LiteralPath (Join-Path $Directory 'metadata.json') -Raw | ConvertFrom-Json
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter 'sample-*.json' | Sort-Object Name)
    if ($files.Count -lt 2) { throw 'At least two samples are required' }
    $samples = @($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
    $warnings = @()
    $errorsFile = Join-Path $Directory 'capture-errors.json'
    if (Test-Path -LiteralPath $errorsFile) {
        $captureErrors = @(Get-Content -LiteralPath $errorsFile -Raw | ConvertFrom-Json)
        if ($captureErrors.Count) { $warnings += "Capture error count: $($captureErrors.Count)" }
    } else { $warnings += 'Missing capture-errors.json: metadata observation errors are unknown' }
    if ($metadata.sampleCount -ne $samples.Count) { $warnings += 'Declared sample count mismatch' }
    $system = @(foreach ($s in $samples) {
        [ordered]@{
            observedUtc=$s.observedUtc; uptimeSeconds=$s.systemUptimeSeconds
            lastBootUtc=$s.system.lastBootUtc; processCount=@($s.processes).Count
            errorCount=@($s.errors).Count
            memory=$s.memory | Select-Object AvailableMBytes,CommittedBytes,CommitLimit,PoolPagedBytes,PoolNonpagedBytes,PagesPersec,PageReadsPersec,PageWritesPersec
            pageFiles=@($s.pageFiles | Select-Object AllocatedBaseSize,CurrentUsage,PeakUsage)
            production=$s.production | Select-Object process_id,process_started_at_utc,runtime_id,state,web_health,runtime_health
        }
    })
    [ordered]@{
        sourceRevision=$metadata.sourceRevision; productionBuild=$metadata.deployment.build_version
        configurationSha256=$metadata.configurationSha256; tempRoot=$metadata.tempRoot
        logicalProcessors=$metadata.logicalProcessors; warnings=$warnings; system=$system
        intervals=@(for ($i=1; $i -lt $samples.Count; $i++) { Compare-Samples $samples[$i-1] $samples[$i] })
        window=Compare-Samples $samples[0] $samples[-1]
    }
}
[ordered]@{
    schemaVersion=1
    semantics='Appeared/disappeared are observations, not proof of birth/exit; missing/reset cumulative counters have null rates. Gauges may decrease. Windows are not equivalent workloads.'
    baseline=Read-Capture $BaselineDirectory
    incident=Read-Capture $IncidentDirectory
    betweenFirstSamples=Compare-Samples `
        (Get-Content -LiteralPath (Join-Path $BaselineDirectory 'sample-00.json') -Raw | ConvertFrom-Json) `
        (Get-Content -LiteralPath (Join-Path $IncidentDirectory 'sample-00.json') -Raw | ConvertFrom-Json)
} | ConvertTo-Json -Depth 20
