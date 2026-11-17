#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../scripts/TestSuiteMeasurement.psm1') -Force
function Check($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
$context = [ordered]@{
    schema='nightweaver-suite-measurement-v1'; commandPolicy='nightweaver-full-suite-local-v1'; threads=6
    preparation='normal'; dotnetVersion='10.0.300'; runtimeVersion='10.0.8'; bdVersion='1.3.1'; logicalProcessors=12
    tempRoot='C:/temp'; TEMP='C:/temp'; TMP='C:/temp'; productionContext='running:build:runtime:hash'
    machine='fixture'; bootUtc='fixture-boot'; schedulingHash='six'; dirty=$false
    production=@{state='running'}
    candidateRevision=('a'*40); candidateTree=('b'*40); evidenceId=('c'*32)
}
$baseline = [ordered]@{}; foreach ($key in $context.Keys) { $baseline[$key]=$context[$key] }
$baseline.command="dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename $($baseline.evidenceId).xml --show-slowest-tests 20"
$baseline.elapsedSeconds=100; $baseline.total=1325; $baseline.passed=1325
$baseline.failed=0; $baseline.skipped=0; $baseline.testExitCode=0; $baseline.accepted=$true
$directory = Join-Path ([IO.Path]::GetTempPath()) ('measurement-policy-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$script:captures=0; $script:runs=0; $script:seconds=100.0; $script:exit=0; $script:total=1325; $script:failed=0
$runner = { $script:runs++; @{ testExitCode=$script:exit; total=$script:total; passed=($script:total-$script:failed); failed=$script:failed; skipped=0 } }
$capture = { param($path); Check (Test-Path -LiteralPath $path) 'Capture preceded retention'; $script:captures++; 'retained-fixture-capture' }
function Invoke-FixtureMeasurement($Base=$baseline) {
    Invoke-TestSuiteMeasurement $context $Base (Join-Path $directory ([guid]::NewGuid().ToString('N')+'.json')) $runner { $script:seconds } $capture
}
$cases=0
# Malformed optional metadata must never hide settled candidate evidence. Exercise
# real JSON loading for every typed field, including the QA total:{} reproduction.
$stringFields=@('schema','commandPolicy','preparation','dotnetVersion','runtimeVersion','bdVersion','tempRoot','TEMP','TMP','productionContext','machine','schedulingHash','candidateRevision','candidateTree','evidenceId','command')
$integerFields=@('threads','logicalProcessors','total','passed','failed','skipped','testExitCode')
foreach ($key in $stringFields + $integerFields + @('bootUtc','dirty','accepted','production')) {
    $invalidValues=@('{}','[]','null','true','"1325"','-1','1.5','2147483648')
    if ($key -in $stringFields -or $key -eq 'bootUtc') { $invalidValues=@('{}','[]','null','true','1325','""') }
    if ($key -in @('dirty','accepted')) { $invalidValues=@('{}','[]','null','"true"','1') }
    if ($key -eq 'production') { $invalidValues=@('{}','[]','null','true','"running"','{"state":{}}','{"state":[]}','{"state":true}','{"state":"foreign"}') }
    foreach ($invalidJson in $invalidValues + @('MISSING')) {
        foreach ($testExit in @(0,17)) {
            $malformed=ConvertFrom-Json -InputObject ($baseline | ConvertTo-Json -Depth 12) -AsHashtable -NoEnumerate
            if ($invalidJson -eq 'MISSING') { $malformed.Remove($key) } else { $malformed[$key]=ConvertFrom-Json -InputObject $invalidJson -AsHashtable -NoEnumerate }
            $baselinePath=Join-Path $directory ([guid]::NewGuid().ToString('N')+'-baseline.json')
            $malformed | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $baselinePath -Encoding utf8
            $loaded=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $baselinePath -Raw) -AsHashtable -NoEnumerate
            $script:exit=$testExit; $script:failed=$(if ($testExit) { 1 } else { 0 })
            $path=Join-Path $directory ([guid]::NewGuid().ToString('N')+'.json')
            $before=$script:captures; $beforeRuns=$script:runs
            $r=Invoke-TestSuiteMeasurement $context $loaded $path $runner { 150.0 } $capture
            $retained=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            Check ($script:runs -eq $beforeRuns+1 -and $retained.testExitCode -eq $testExit -and $retained.failed -eq $script:failed -and $retained.total -eq 1325 -and $retained.passed -eq (1325-$script:failed)) "Invalid $key ($invalidJson) lost original candidate result"
            Check (!$retained.assessment.comparisonAvailable -and $null -eq $retained.assessment.deltaSeconds -and $null -eq $retained.assessment.ratio -and !$retained.assessment.captureRequired) "Invalid $key ($invalidJson) compared"
            Check ($retained.assessment.comparisonReasons -contains "Baseline missing or invalid $key metadata") "Invalid $key ($invalidJson) has no explanation"
            Check ($script:captures -eq $before -and $retained.capture.state -eq 'not-triggered') "Invalid $key triggered capture"
            Check (($retained.assessment.correctness -eq 'passed') -eq ($testExit -eq 0)) "Invalid $key changed correctness"
            $cases++
        }
    }
}
$script:exit=0; $script:failed=0
# Exercise the entry point's serialized baseline loading with an actual boot identity.
$context.bootUtc='2026-11-16T18:40:45.5000000Z'; $baseline.bootUtc=$context.bootUtc
foreach ($scenario in @(
    @{seconds=149.999; boot=$context.bootUtc; comparable=$true; captures=0},
    @{seconds=150.0; boot=$context.bootUtc; comparable=$true; captures=1},
    @{seconds=150.0; boot='2026-11-15T18:40:45.5000000Z'; comparable=$false; captures=0}
)) {
    $baseline.bootUtc=$scenario.boot
    $baselinePath=Join-Path $directory ([guid]::NewGuid().ToString('N')+'-baseline.json')
    $baseline | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $baselinePath -Encoding utf8
    $loaded=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $baselinePath -Raw) -AsHashtable -NoEnumerate
    $path=Join-Path $directory ([guid]::NewGuid().ToString('N')+'.json')
    $before=$script:captures
    $r=Invoke-TestSuiteMeasurement $context $loaded $path $runner { $scenario.seconds } $capture
    $retained=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Check ($retained.assessment.comparisonAvailable -eq $scenario.comparable) 'Serialized boot identity comparability incorrect'
    Check ($script:captures -eq $before+$scenario.captures) 'Serialized baseline capture count incorrect'
    if ($scenario.comparable) {
        Check ($retained.assessment.deltaSeconds -eq ($scenario.seconds-100) -and $retained.assessment.ratio -eq ($scenario.seconds/100)) 'Serialized baseline delta/ratio incorrect'
    } else {
        Check ($retained.assessment.comparisonReasons -contains 'Missing or mismatched bootUtc' -and $null -eq $retained.assessment.deltaSeconds -and $null -eq $retained.assessment.ratio) 'Different boot identity compared'
    }
    $cases++
}
$baseline.bootUtc=$context.bootUtc
# Preserve JSON root shape as the entry point's optional baseline input.
foreach ($json in @('42', 'true', 'false', '"baseline"', '[1,2]', '[]', '[{}]', 'null')) {
    foreach ($testExit in @(0,17)) {
        $invalidRoot=ConvertFrom-Json -InputObject $json -AsHashtable -NoEnumerate
        $script:exit=$testExit; $script:failed=$(if ($testExit) { 1 } else { 0 })
        $path=Join-Path $directory ([guid]::NewGuid().ToString('N')+'.json')
        $before=$script:captures
        $r=Invoke-TestSuiteMeasurement $context $invalidRoot $path $runner { 150.0 } $capture
        $retained=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Check ($retained.testExitCode -eq $testExit -and $retained.failed -eq $script:failed -and $retained.total -eq 1325 -and $retained.passed -eq (1325-$script:failed)) 'Invalid baseline root lost original candidate result'
        Check (!$retained.assessment.comparisonAvailable -and $null -eq $retained.assessment.deltaSeconds -and $null -eq $retained.assessment.ratio -and !$retained.assessment.captureRequired) 'Invalid baseline root compared'
        $reason=if ($null -eq $invalidRoot) { 'No explicit retained baseline supplied' } else { 'Baseline root must be a JSON object' }
        Check ($retained.assessment.comparisonReasons -contains $reason) 'Invalid baseline root has no explanation'
        Check ($script:captures -eq $before -and $retained.capture.state -eq 'not-triggered') 'Invalid baseline root triggered capture'
        Check (($retained.assessment.correctness -eq 'passed') -eq ($testExit -eq 0)) 'Invalid baseline root changed correctness'
        $cases++
    }
}
foreach ($invalid in @('not-a-number', '100', $true, @{}, @(1,2), $null, [double]::NaN, [double]::PositiveInfinity, -1, 0)) {
    foreach ($testExit in @(0,17)) {
        $baseline.elapsedSeconds=$invalid; $script:exit=$testExit; $script:failed=$(if ($testExit) { 1 } else { 0 })
        $path=Join-Path $directory ([guid]::NewGuid().ToString('N')+'.json')
        $r=Invoke-TestSuiteMeasurement $context $baseline $path $runner { 150.0 } $capture
        $retained=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Check ($retained.testExitCode -eq $testExit -and $retained.failed -eq $script:failed -and $retained.total -eq 1325) 'Malformed baseline lost original candidate result'
        Check (!$retained.assessment.comparisonAvailable -and $null -eq $retained.assessment.deltaSeconds -and $null -eq $retained.assessment.ratio -and !$retained.assessment.captureRequired) 'Malformed elapsed baseline compared'
        Check ($retained.assessment.comparisonReasons -contains 'Baseline elapsed time unavailable or invalid') 'Malformed elapsed baseline has no explanation'
        $cases++
    }
}
$baseline.elapsedSeconds=100; $script:exit=0; $script:failed=0
foreach ($seconds in @(239.999,240.0,299.999,300.0)) {
    $script:seconds=$seconds; $r=Invoke-FixtureMeasurement
    Check ($r.testExitCode -eq 0 -and $r.failed -eq 0 -and $r.assessment.correctness -eq 'passed') 'Performance fabricated correctness failure'
    Check (($r.assessment.operatingTarget -eq 'met') -eq ($seconds -lt 240)) 'Operating boundary'
    Check (($r.assessment.hardBudget -eq 'met') -eq ($seconds -lt 300)) 'Hard boundary'
    $cases++
}
$script:seconds=149.999; $before=$script:captures; $r=Invoke-FixtureMeasurement
Check ($script:captures -eq $before) 'Below 1.5x captured'; $cases++
$script:seconds=150; $before=$script:captures; $r=Invoke-FixtureMeasurement
Check ($script:captures -eq $before+1 -and $r.capture.state -eq 'captured') 'Exact 1.5x did not capture once'; $cases++
$before=$script:runs; $script:exit=17; $script:failed=1; $r=Invoke-FixtureMeasurement
Check ($r.testExitCode -eq 17 -and $r.failed -eq 1 -and $script:runs -eq $before+1 -and $r.assessment.correctness -ne 'passed') 'Failure lost or retried'; $cases++
$script:exit=0; $script:failed=0; $script:total=0; $r=Invoke-FixtureMeasurement
Check ($r.assessment.correctness -ne 'passed') 'Zero discovery accepted'; $script:total=1325; $cases++
$r=Invoke-FixtureMeasurement -Base $null
Check (!$r.assessment.comparisonAvailable -and $null -eq $r.assessment.deltaSeconds -and !$r.assessment.captureRequired) 'Missing baseline fabricated delta'; $cases++
$baseline.TEMP='H:/temp'; $r=Invoke-FixtureMeasurement
Check (!$r.assessment.comparisonAvailable -and !$r.assessment.captureRequired) 'Mismatched environment compared'; $baseline.TEMP='C:/temp'; $cases++
$saved=$baseline.candidateRevision; $baseline.Remove('candidateRevision'); $r=Invoke-FixtureMeasurement
Check (!$r.assessment.comparisonAvailable) 'Incomplete baseline accepted'; $baseline.candidateRevision=$saved; $cases++
$capture={ param($path); Check (Test-Path -LiteralPath $path) 'Failure capture preceded retention'; throw 'controlled capture failure' }
$r=Invoke-FixtureMeasurement
Check ($r.capture.state -eq 'failed' -and $r.testExitCode -eq 0 -and $r.failed -eq 0) 'Capture failure hid passing tests'; $cases++
$capture={ param($path); @{directory='retained-errors';errors=1} }
$r=Invoke-FixtureMeasurement
Check ($r.capture.state -eq 'failed' -and $r.capture.evidence.directory -eq 'retained-errors') 'Collector observation errors hidden'; $cases++
Write-Host "$cases deterministic measurement cases passed; evidence $directory"
