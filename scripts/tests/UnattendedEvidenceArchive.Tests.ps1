#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../UnattendedEvidenceArchive.psm1') -Force
$root = Join-Path $OutputRoot ('unattended-archive-checks-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$cases = 0
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Check($condition, [string]$message) { if (-not $condition) { throw $message } }
function Reference([string]$path, [byte[]]$bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
    @{path=$path; bytes=$bytes.LongLength; sha256=$hash}
}
function Sample([int]$sequence, $reference) {
    @{
        schema='nightweaver-unattended-sample-v1'; sequence=$sequence; runId=('a'*32)
        projectId='fixture'; repositoryId=('b'*64); sourceKind='runtime'; sourceId='factory'
        collection=@{startedUtc='2026-01-01T00:00:00Z'; endedUtc='2026-01-01T00:00:01Z'; startedElapsedMs=0; endedElapsedMs=1000}
        sourceObservedUtc='2025-12-31T23:59:59Z'; outcome='complete'
        epoch=@{ownerRunId=('c'*32); runtimeId=('d'*32); processId=42; processStartedUtc='2025-12-31T23:00:00Z'}
        invocationId=$null; sourcePosition=$null; payload=$reference; failureCode=$null
    }
}
function Fixture {
    $directory=Join-Path $root ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory((Join-Path $directory 'payloads')) | Out-Null
    $payload=$utf8.GetBytes('{"fixture":true}')
    [IO.File]::WriteAllBytes((Join-Path $directory 'payloads/sample.json'), $payload)
    $reference=Reference 'payloads/sample.json' $payload
    $manifest=@{
        schema='nightweaver-unattended-archive-v1'; runId=('a'*32); projectId='fixture'; repositoryId=('b'*64)
        repositoryPath='C:\fixture-target'; contract=@{version=1; revision=('e'*40); sha256=('f'*64)}
        installation=@{revision=('1'*40); manifestSha256=('2'*64); configurationSha256=('3'*64)}
        workloadIds=@('fixture-1','fixture-2','fixture-3')
        requestedWindow=@{startUtc='2026-01-01T00:00:00Z'; endUtc='2026-01-01T08:00:00Z'}
        actualWindow=@{startUtc='2026-01-01T00:00:00Z'; endUtc='2026-01-01T08:00:00Z'; elapsedMs=28800000}
        state='complete'; sampleCount=1; clockUncertain=$false
        references=@{interventionJournal=$reference; operatorAttestation=$reference; budgetWorksheet=$reference; runAuthorization=$reference; qualification=$reference; checks=$reference}
    }
    [pscustomobject]@{directory=$directory; manifest=$manifest; samples=@((Sample 1 $reference))}
}
function Save($fixture) {
    [IO.File]::WriteAllText((Join-Path $fixture.directory 'manifest.json'), ($fixture.manifest | ConvertTo-Json -Depth 12), $utf8)
    $lines=@($fixture.samples | ForEach-Object { $_ | ConvertTo-Json -Depth 12 -Compress })
    [IO.File]::WriteAllText((Join-Path $fixture.directory 'samples.jsonl'), (($lines -join "`n") + $(if($lines.Count){"`n"}else{''})), $utf8)
}
function Snapshot([string]$directory) {
    @(Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName).Hash)"
    }) -join "`n"
}
function Valid($fixture, [string]$message) {
    Save $fixture
    $before=Snapshot $fixture.directory
    $result=Test-UnattendedEvidenceArchive $fixture.directory
    Check $result.integrityValid "$message rejected"
    Check ($result.unattendedVerdict -ceq 'not-evaluated') 'Integrity validator adjudicated unattended success'
    Check ((Snapshot $fixture.directory) -ceq $before) 'Validation mutated archived bytes or write times'
    $script:cases++
    return $result
}
function Invalid($fixture, [string]$message, [scriptblock]$afterSave = {}) {
    Save $fixture
    & $afterSave $fixture
    $before=Snapshot $fixture.directory
    $result=Test-UnattendedEvidenceArchive $fixture.directory
    Check (-not $result.integrityValid -and $result.errors.Count -eq 1) "$message accepted"
    Check ($result.unattendedVerdict -ceq 'not-evaluated') 'Invalid archive acquired a verdict'
    Check ((Snapshot $fixture.directory) -ceq $before) 'Invalid input was overwritten or repaired'
    $script:cases++
}

$f=Fixture; $r=Valid $f 'Complete structural fixture'
Check ($r.recordCount -eq 1 -and $r.observationGapCount -eq 0) 'Complete sample lost'
Check ($r.manifestSha256 -eq (Get-FileHash (Join-Path $f.directory 'manifest.json')).Hash.ToLowerInvariant()) 'Manifest provenance hash wrong'
# Supplied source time deliberately predates local collection; neither is rewritten.
Check ($f.samples[0].sourceObservedUtc -ceq '2025-12-31T23:59:59Z') 'Source time changed'
foreach($outcome in 'timeout','missing','invalid','interrupted','limit','error','clock-uncertain') {
    $f=Fixture; $f.samples[0].outcome=$outcome; $f.samples[0].payload=$null; $f.samples[0].epoch=$null; $f.samples[0].failureCode='fixture-failure'
    $r=Valid $f "$outcome sample"
    Check ($r.observationGapCount -eq 1 -and $r.observationGaps[0].code -ceq $outcome) 'Failure sample hidden'
}
foreach($known in $false,$true) {
    $f=Fixture; $f.samples[0].sourceKind='invocation'; $f.samples[0].outcome='timeout'; $f.samples[0].failureCode='fixture-timeout'
    if ($known) { $f.samples[0].invocationId='fixture-turn'; $f.samples[0].sourcePosition=@{generation='file-1'; offset=0} }
    $r=Valid $f "Partial invocation timeout, attribution known=$known"
    Check ($r.observationGapCount -eq 1) 'Partial failed invocation acquired a success claim'
}
$f=Fixture; $f.manifest.state='interrupted'; $r=Valid $f 'Interrupted archive'
Check ($r.observationGaps.code -contains 'archive-interrupted') 'Interrupted state hidden'
$f=Fixture; $f.manifest.state='open'; $f.manifest.sampleCount=$null; $f.manifest.actualWindow.endUtc=$null; $f.manifest.actualWindow.elapsedMs=$null
$r=Valid $f 'Open stopped snapshot'; Check ($r.observationGaps.code -contains 'archive-open') 'Open archive became completed'
$f=Fixture; $f.manifest.references.operatorAttestation=$null; $r=Valid $f 'Missing attestation'
Check ($r.observationGaps.code -contains 'missing-operatorAttestation') 'Missing attestation hidden'
$f=Fixture; [IO.File]::WriteAllBytes((Join-Path $f.directory 'payloads/journal.txt'), [byte[]]@()); $f.manifest.references.interventionJournal=Reference 'payloads/journal.txt' ([byte[]]@())
$null=Valid $f 'Blank journal has integrity, not proof of no intervention'
$f=Fixture; $f.samples+=Sample 2 $f.samples[0].payload; $f.samples[1].epoch.processStartedUtc='2026-01-01T00:00:00Z'; $f.manifest.sampleCount=2
$r=Valid $f 'Same PID with new creation identity'; Check ($r.epochTransitionCount -eq 1) 'PID reuse was flattened'
$f=Fixture; $f.samples+=Sample 2 $f.samples[0].payload; $f.samples[1].epoch.runtimeId=('4'*32); $f.manifest.sampleCount=2
$r=Valid $f 'Runtime replacement'; Check ($r.epochTransitionCount -eq 1) 'Runtime change lost'
$f=Fixture; $f.samples+=Sample 2 $f.samples[0].payload; $f.samples[1].epoch.processStartedUtc='2025-12-31T23:00:00.0000000Z'; $f.manifest.sampleCount=2
$r=Valid $f 'Equivalent UTC precision'; Check ($r.epochTransitionCount -eq 0) 'Equivalent UTC text invented an epoch change'
$f=Fixture; $f.samples=@(); $f.manifest.sampleCount=0; $r=Valid $f 'Empty complete structure'
Check ($r.observationGaps.code -contains 'no-samples') 'Empty archive implied observed idle'
$f=Fixture; $f.samples=@(1..129 | ForEach-Object { $s=Sample $_ $null; $s.outcome='missing'; $s.failureCode='fixture-missing'; $s })
$f.manifest.sampleCount=129; $r=Valid $f 'Bounded diagnostic detail'
Check ($r.observationGapCount -eq 129 -and $r.observationGaps.Count -eq 128 -and $r.detailsTruncated) 'Diagnostic overflow was silent'

foreach($mutation in @(
    {param($f) $f.manifest.schema=@('nightweaver-unattended-archive-v1')},
    {param($f) $f.samples[0].schema=@('nightweaver-unattended-sample-v1')},
    {param($f) $f.manifest.schema='nightweaver-unattended-archive-v2'},
    {param($f) $f.manifest.workloadIds=@('same','same','other')},
    {param($f) $f.manifest.sampleCount='1'},
    {param($f) $f.manifest.sampleCount=$true},
    {param($f) $f.manifest.sampleCount=1.5},
    {param($f) $f.manifest.extraCommand='do-not-execute'},
    {param($f) $f.manifest.contract.version=2},
    {param($f) $f.manifest.requestedWindow.endUtc='2026-01-01T07:00:00Z'},
    {param($f) $f.samples[0].runId=('9'*32)},
    {param($f) $f.samples[0].repositoryId=('9'*64)},
    {param($f) $f.samples[0].projectId='foreign'},
    {param($f) $f.samples[0].sequence=2},
    {param($f) $f.samples[0].epoch=$null},
    {param($f) $f.samples[0].sourceObservedUtc='not-a-time'},
    {param($f) $f.samples[0].collection.endedElapsedMs=-1},
    {param($f) $f.samples[0].collection.endedElapsedMs=28800001},
    {param($f) $f.samples[0].payload.bytes=100},
    {param($f) $f.samples[0].payload.sha256=('0'*64)},
    {param($f) $f.samples[0].payload.path='payloads/../manifest.json'},
    {param($f) $f.samples[0].payload.path='C:/outside.json'},
    {param($f) $f.samples[0].payload.path='payloads/sample.json:stream'},
    {param($f) $f.samples[0].payload.path='payloads/CON'},
    {param($f) $f.samples[0].payload.path='payloads/missing.json'}
)) { $f=Fixture; & $mutation $f; Invalid $f 'Malformed field/identity/path/hash input' }

$f=Fixture; Invalid $f 'Duplicate JSON property' {param($f)
    $path=Join-Path $f.directory 'manifest.json'; $text=[IO.File]::ReadAllText($path)
    [IO.File]::WriteAllText($path, $text.Replace('"schema":', '"schema":"duplicate","schema":'), $utf8)
}
$f=Fixture; Invalid $f 'Truncated last record' {param($f)
    $path=Join-Path $f.directory 'samples.jsonl'; [IO.File]::WriteAllText($path, [IO.File]::ReadAllText($path).TrimEnd("`n"), $utf8)
}
$f=Fixture; Invalid $f 'Invalid UTF8' {param($f) [IO.File]::WriteAllBytes((Join-Path $f.directory 'samples.jsonl'), [byte[]]@(255,10)) }
$f=Fixture; Invalid $f 'Partial JSON' {param($f) [IO.File]::WriteAllText((Join-Path $f.directory 'samples.jsonl'), "{`n", $utf8) }
$f=Fixture; Invalid $f 'Sample count mismatch' {param($f) [IO.File]::WriteAllText((Join-Path $f.directory 'samples.jsonl'), '', $utf8) }
foreach($entry in @(@('manifest.json',1048577),@('samples.jsonl',33554433),@('payloads/huge.bin',268435457))) {
    $f=Fixture
    Invalid $f 'Bounded file/archive size' {
        param($f)
        $stream=[IO.File]::Open((Join-Path $f.directory $entry[0]), [IO.FileMode]::OpenOrCreate)
        try { $stream.SetLength($entry[1]) } finally { $stream.Dispose() }
    }
}
$f=Fixture; Invalid $f 'Record size bound' {param($f) [IO.File]::WriteAllText((Join-Path $f.directory 'samples.jsonl'), ('x'*2097153)+"`n", $utf8) }
$f=Fixture; Invalid $f 'Unreferenced payload size bound' {param($f)
    $stream=[IO.File]::Open((Join-Path $f.directory 'payloads/extra.bin'), [IO.FileMode]::CreateNew)
    try { $stream.SetLength(1048577) } finally { $stream.Dispose() }
}

# Invocation chunk and source budgets are independent of small on-disk reuse.
$f=Fixture; $chunk=New-Object byte[] 65536; [IO.File]::WriteAllBytes((Join-Path $f.directory 'payloads/log.bin'), $chunk)
$reference=Reference 'payloads/log.bin' $chunk
$f.samples=@(1..128 | ForEach-Object { $s=Sample $_ $reference; $s.sourceKind='invocation'; $s.invocationId='fixture-turn'; $s.sourcePosition=@{generation='file-1'; offset=(($_-1)*65536)}; $s })
$f.manifest.sampleCount=128; $null=Valid $f 'Exact 8MiB invocation source budget'
$s=Sample 129 $reference; $s.sourceKind='invocation'; $s.invocationId='fixture-turn'; $s.sourcePosition=@{generation='file-2'; offset=0}; $f.samples+=,$s; $f.manifest.sampleCount=129
Invalid $f 'Invocation source overflow across generations'
$f=Fixture; $bytes=New-Object byte[] 1048577; [IO.File]::WriteAllBytes((Join-Path $f.directory 'payloads/large.bin'), $bytes); $f.samples[0].payload=Reference 'payloads/large.bin' $bytes
Invalid $f 'Payload bound'

# Every payload, including an unreferenced one, fits its per-file bound;
# aggregate accounting still rejects the first byte above the archive cap.
$f=Fixture; Save $f
for($index=0; $index -lt 255; $index++) {
    $stream=[IO.File]::Open((Join-Path $f.directory "payloads/fill-$index.bin"), [IO.FileMode]::CreateNew)
    try { $stream.SetLength(1048576) } finally { $stream.Dispose() }
}
$current=(Get-ChildItem -LiteralPath $f.directory -Recurse -File | Measure-Object Length -Sum).Sum
$tail=Join-Path $f.directory 'payloads/tail.bin'; $stream=[IO.File]::Open($tail,[IO.FileMode]::CreateNew)
try { $stream.SetLength(268435456 - $current) } finally { $stream.Dispose() }
$r=Test-UnattendedEvidenceArchive $f.directory
Check ($r.integrityValid -and $r.archiveBytes -eq 268435456) 'Exact archive byte bound rejected'; $cases++
$stream=[IO.File]::Open($tail,[IO.FileMode]::Open)
try { $stream.SetLength($stream.Length+1) } finally { $stream.Dispose() }
$r=Test-UnattendedEvidenceArchive $f.directory
Check (-not $r.integrityValid) 'Aggregate archive byte overflow accepted'; $cases++

$f=Fixture; Save $f
$reader=[Nightweaver.Evidence.UnattendedArchiveReader]::new($f.directory)
try {
    $denied=$false; try { [IO.File]::WriteAllText((Join-Path $f.directory 'payloads/sample.json'), 'changed') } catch [IO.IOException] { $denied=$true }
    Check $denied 'Read-locked payload could change during hashing'
    $denied=$false; try { [IO.Directory]::Move((Join-Path $f.directory 'payloads'), (Join-Path $f.directory 'replacement')) } catch [IO.IOException] { $denied=$true }
    Check $denied 'Pinned directory could be replaced during inspection'
} finally { $reader.Dispose() }
[IO.File]::WriteAllText((Join-Path $f.directory 'payloads/sample.json'), 'released')
$cases++

# Junctions need no symlink privilege on the Windows target.
$outside=Join-Path $root 'outside'; [IO.Directory]::CreateDirectory($outside) | Out-Null
$sentinel=Join-Path $outside 'secret.txt'; [IO.File]::WriteAllText($sentinel, 'PRIVATE-SENTINEL')
$before=Snapshot $outside
$f=Fixture; Save $f
New-Item -ItemType Junction -Path (Join-Path $f.directory 'payloads/escape') -Target $outside | Out-Null
$r=Test-UnattendedEvidenceArchive $f.directory
Check (-not $r.integrityValid) 'Archive junction accepted'
Check (($r | ConvertTo-Json -Depth 8) -notmatch 'PRIVATE-SENTINEL') 'Outside payload leaked'
Check ((Snapshot $outside) -ceq $before) 'Outside target changed'
$cases++
$link=Join-Path $root 'linked-root'; New-Item -ItemType Junction -Path $link -Target $f.directory | Out-Null
$r=Test-UnattendedEvidenceArchive $link; Check (-not $r.integrityValid) 'Root ancestor reparse point accepted'; $cases++
$f=Fixture; Save $f
New-Item -ItemType HardLink -Path (Join-Path $f.directory 'payloads/hardlink.txt') -Target $sentinel | Out-Null
$r=Test-UnattendedEvidenceArchive $f.directory; Check (-not $r.integrityValid) 'Hardlinked outside payload accepted'; $cases++

# Validate the documented example as bytes, not just an asserted prose shape.
$documentation=Get-Content -Raw (Join-Path $PSScriptRoot '../../docs/acceptance/unattended-evidence-archive.md')
$examples=[regex]::Matches($documentation, '(?s)```json\r?\n(.*?)\r?\n```')
Check ($examples.Count -eq 2) 'Documented manifest/sample examples missing'
$f=Fixture
[IO.File]::WriteAllText((Join-Path $f.directory 'manifest.json'), $examples[0].Groups[1].Value, $utf8)
[IO.File]::WriteAllText((Join-Path $f.directory 'samples.jsonl'), (($examples[1].Groups[1].Value | ConvertFrom-Json | ConvertTo-Json -Depth 12 -Compress)+"`n"), $utf8)
[IO.File]::Delete((Join-Path $f.directory 'payloads/sample.json'))
[IO.File]::WriteAllText((Join-Path $f.directory 'payloads/status.json'), '{}', $utf8)
$r=Test-UnattendedEvidenceArchive $f.directory
Check ($r.integrityValid -and $r.observationGapCount -eq 6 -and $r.unattendedVerdict -ceq 'not-evaluated') 'Documented sparse archive example invalid or overclaimed'
$cases++

# Command-line exit status concerns integrity, never observation quality.
$cli=Join-Path $PSScriptRoot '../Test-UnattendedEvidenceArchive.ps1'
$cliResult=(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $cli -ArchiveDirectory $f.directory) | ConvertFrom-Json
Check ($LASTEXITCODE -eq 0 -and $cliResult.integrityValid -and $cliResult.observationGapCount -eq 6) 'CLI rejected honest gaps'
[IO.File]::WriteAllText((Join-Path $f.directory 'samples.jsonl'), 'truncated', $utf8)
$cliResult=(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $cli -ArchiveDirectory $f.directory) | ConvertFrom-Json
Check ($LASTEXITCODE -eq 2 -and -not $cliResult.integrityValid) 'CLI accepted invalid input'
$cases+=2

Write-Host "PASS: $cases archive validity, gap, identity, limit, path, hash and read-only cases"
Write-Host "Retained synthetic archives: $root"
[pscustomobject]@{schema='nightweaver-unattended-archive-fixtures-v1'; passed=$cases; failed=0; skipped=0; evidenceDirectory=$root} | ConvertTo-Json -Compress
