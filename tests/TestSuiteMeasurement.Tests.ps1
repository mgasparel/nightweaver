#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../scripts/TestSuiteMeasurement.psm1') -Force
function Check($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
$context = [ordered]@{
    schema='nightweaver-suite-measurement-v1'; commandPolicy='nightweaver-full-suite-local-v1'; threads=6
    preparation='normal'; dotnetVersion='10.0.8'; bdVersion='1.3.1'; logicalProcessors=12
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
