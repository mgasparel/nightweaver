param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = Join-Path $OutputRoot ('analyzer-checks-' + [guid]::NewGuid().ToString('N'))
$baseline = New-Item -ItemType Directory -Path (Join-Path $root 'baseline')
$incident = New-Item -ItemType Directory -Path (Join-Path $root 'incident')
function Process($id, $parent, $created, $private, $cpu, $read) {
    @{ Name='fixture'; ProcessId=$id; ParentProcessId=$parent; createdUtc=$created
       PrivatePageCount=$private; WorkingSetSize=$private; HandleCount=10; ThreadCount=2
       KernelModeTime=$cpu; UserModeTime=0; ReadTransferCount=$read
       WriteTransferCount=0; OtherTransferCount=0 }
}
$owner = Process 10 0 '2026-01-01T00:00:00.123456Z' 100 10000000 100
$child = Process 11 10 '2026-01-01T00:00:01Z' 100 10000000 100
$oldParentChild = Process 12 13 '2026-01-01T00:00:01Z' 100 10000000 100
$reusedParent = Process 13 0 '2026-01-01T00:00:02Z' 100 10000000 100
$unknown = Process 14 10 $null 100 10000000 100
$unknown.CommandLine='PRIVATE-SENTINEL-never-export'
$reusedPid = Process 15 0 '2026-01-01T00:00:01Z' 100 10000000 100
foreach ($directory in @($baseline.FullName,$incident.FullName)) {
    @{ sampleCount=2; sourceRevision='test'; deployment=@{build_version='test'} } |
        ConvertTo-Json | Set-Content (Join-Path $directory 'metadata.json')
    $s = @{ observedUtc='2026-01-01T00:00:10Z'; processes=@($owner,$child,$oldParentChild,$reusedParent,$unknown,$reusedPid)
        production=@{process_id=10;process_started_at_utc='2025-12-31T19:00:00.1234567-05:00'}; errors=@() }
    $s | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'sample-00.json')
    $s.observedUtc='2026-01-01T00:00:12.5Z'
    $afterOwner = $owner.Clone(); $afterOwner.PrivatePageCount=90; $afterOwner.KernelModeTime=35000000; $afterOwner.ReadTransferCount=150
    $afterChild = $child.Clone(); $afterChild.KernelModeTime=1; $afterChild.ReadTransferCount=$null
    $newPid = $reusedPid.Clone(); $newPid.createdUtc='2026-01-01T00:00:11Z'
    $s.processes=@($afterOwner,$afterChild,$oldParentChild,$reusedParent,$unknown,$newPid)
    $s | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'sample-01.json')
}
# Across-capture timestamps must increase, while retaining the same identities.
$path = Join-Path $incident.FullName 'sample-00.json'
$s = Get-Content $path -Raw | ConvertFrom-Json
$s.observedUtc='2026-01-01T00:00:11Z'
$s | ConvertTo-Json -Depth 8 | Set-Content $path
$script = Join-Path $PSScriptRoot '../Compare-ResourceIncident.ps1'
$serialized = (& $script -BaselineDirectory $baseline.FullName -IncidentDirectory $incident.FullName) -join [Environment]::NewLine
$result = $serialized | ConvertFrom-Json
$w = $result.baseline.window
function Assert($condition, $message) { if (-not $condition) { throw $message } }
Assert ($serialized -notmatch 'PRIVATE-SENTINEL') 'Non-allowlisted process data leaked'
$o = @($w.changes | Where-Object name -eq fixture | Where-Object group -eq production-owner)[0]
$c = @($w.changes | Where-Object group -eq production-descendant)[0]
Assert ($w.seconds -eq 2.5) 'Actual fractional interval lost'
Assert ($o.identity -match '00:00:00.1234560Z') 'Creation precision or UTC conversion lost'
Assert ($o.delta.PrivatePageCount -eq -10) 'Decreasing gauge invalidated'
Assert ($o.cpuCores -eq 1) 'CPU 100ns conversion or elapsed interval wrong'
Assert ($o.rates.ReadTransferCount -eq 20) 'IO rate wrong'
Assert ($null -eq $c.cpuCores -and $null -eq $c.rates.ReadTransferCount) 'Reset/missing counters must be null'
Assert ($c.parentIdentity -eq $o.identity) 'Verified parent missing'
$unrelated = @($w.changes | Where-Object { $_.identity -like '12@*' })[0]
Assert ($null -eq $unrelated.parentIdentity -and $unrelated.group -eq 'unattributed') 'Reused parent falsely attached'
Assert ($w.unknownBefore -eq 1 -and $w.unknownAfter -eq 1) 'Unknown identity lost'
Assert (@($w.appeared | Where-Object { $_.identity -like '15@*' }).Count -eq 1) 'Reused PID not appeared'
Assert (@($w.disappeared | Where-Object { $_.identity -like '15@*' }).Count -eq 1) 'Reused PID not disappeared'
Assert (@($w.changes | Where-Object { $_.identity -like '15@*' }).Count -eq 0) 'Reused PID counters subtracted'
Assert ($result.baseline.warnings -match 'Missing capture-errors') 'Missing error artifact hidden'
# A status PID without its matching creation time grants no owner attribution.
$s.production.process_started_at_utc='2026-01-01T00:00:03Z'
$s | ConvertTo-Json -Depth 8 | Set-Content $path
$mismatch = (& $script -BaselineDirectory $baseline.FullName -IncidentDirectory $incident.FullName) | ConvertFrom-Json
Assert (@($mismatch.incident.window.changes | Where-Object group -eq production-owner).Count -eq 1) 'Final sample owner should still match'
Assert (@($mismatch.betweenFirstSamples.changes | Where-Object group -ne unattributed).Count -eq 0) 'Mismatched status identity accepted'
$s.observedUtc='2026-01-01T00:00:12.5Z'
$s | ConvertTo-Json -Depth 8 | Set-Content $path
$rejected = $false
try { & $script -BaselineDirectory $baseline.FullName -IncidentDirectory $incident.FullName | Out-Null }
catch { $rejected = $true }
Assert $rejected 'Non-increasing sample times accepted'
Write-Host 'PASS: 17 identity, counter, attribution, precision, privacy and sample-spacing assertions'
Write-Host "Retained synthetic evidence: $root"
