#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Checkout,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$BaseRevision,
    [string]$Baseline,
    [string]$ProductionInstallation,
    [string]$Project = 'nightshift',
    [string]$Preparation = 'normal build and restore; no separate preparation'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'TestSuiteMeasurement.psm1') -Force
$Checkout = (Resolve-Path -LiteralPath $Checkout).Path
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$id = [guid]::NewGuid().ToString('N')
$destination = Join-Path $EvidenceRoot $id
[IO.Directory]::CreateDirectory($destination) | Out-Null
function GitValue([string[]]$Arguments) {
    $value = & git -C $Checkout @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git metadata read failed: $($Arguments[0])" }
    return ($value -join "`n").Trim()
}
$production = Get-ProductionObservation $ProductionInstallation $Project
if ((Get-Content -LiteralPath (Join-Path $Checkout 'tests/Nightweaver.Tests/TestScheduling.cs') -Raw) -notmatch '\[assembly: Parallelization\(MaxThreads = 6\)\]') { throw 'Expected committed six-thread scheduling' }
$baselineValue = $null
if ($Baseline) {
    $baselineValue = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $Baseline -Raw) -AsHashtable -NoEnumerate
}
$context = [ordered]@{
    schema = 'nightweaver-suite-measurement-v1'; evidenceId = $id; measuredUtc = [datetime]::UtcNow.ToString('o')
    invocationId = $env:NIGHTWEAVER_INVOCATION_ID
    checkout = $Checkout; baseRevision = (GitValue @('rev-parse',"$BaseRevision^{commit}")); baseTree = (GitValue @('rev-parse',"$BaseRevision^{tree}"))
    candidateRevision = (GitValue @('rev-parse','HEAD')); candidateTree = (GitValue @('rev-parse','HEAD^{tree}'))
    dirty = [bool](GitValue @('status','--porcelain')); commandPolicy = 'nightweaver-full-suite-local-v1'
    command = "dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename $id.xml --show-slowest-tests 20"
    threads = 6; schedulingHash = (Get-FileHash (Join-Path $Checkout 'tests/Nightweaver.Tests/TestScheduling.cs')).Hash
    preparation = $Preparation; dotnetVersion = (& dotnet --version | Out-String).Trim(); runtimeVersion = $null; bdVersion = (& bd --version | Out-String).Trim()
    logicalProcessors = [Environment]::ProcessorCount; machine = [Environment]::MachineName
    tempRoot = [IO.Path]::GetTempPath(); TEMP = $env:TEMP; TMP = $env:TMP
    bootUtc = (Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 10).LastBootUpTime.ToUniversalTime().ToString('o')
    production = $production; productionContext = ($production | ConvertTo-Json -Compress)
    baselinePath = $Baseline; baselineSha256 = $(if ($Baseline) { (Get-FileHash -LiteralPath $Baseline).Hash } else { $null })
    log = (Join-Path $destination 'suite.log'); xml = @(); measurementErrors = @()
}
Push-Location $Checkout
try {
    $timer = [Diagnostics.Stopwatch]::new()
    $runner = {
        $timer.Start()
        $fixtureWatch = [Diagnostics.Stopwatch]::StartNew()
        try {
            $context.archiveFixtures = & (Join-Path $Checkout 'scripts/tests/UnattendedEvidenceArchive.Tests.ps1') -OutputRoot ([IO.Path]::GetTempPath()) | ConvertFrom-Json
            if ($context.archiveFixtures.passed -lt 66 -or $context.archiveFixtures.failed -ne 0 -or $context.archiveFixtures.skipped -ne 0) { throw 'Archive fixture count or result invalid' }
        } catch {
            $context.archiveFixtures = @{ failed = 1; error = 'Archive fixture verification failed' }
            $context.measurementErrors += 'Archive fixture verification failed'
        } finally {
            $fixtureWatch.Stop()
            $context.archiveFixtureSeconds = $fixtureWatch.Elapsed.TotalSeconds
        }
        $collectorWatch = [Diagnostics.Stopwatch]::StartNew()
        try {
            $context.collectorFixtures = & (Join-Path $Checkout 'scripts/tests/UnattendedEvidenceCollector.Tests.ps1') -OutputRoot ([IO.Path]::GetTempPath()) | ConvertFrom-Json
            if ($context.collectorFixtures.passed -lt 102 -or $context.collectorFixtures.failed -ne 0 -or $context.collectorFixtures.skipped -ne 0) { throw 'Collector fixture count or result invalid' }
        } catch {
            $context.collectorFixtures = @{ failed = 1; error = 'Collector fixture verification failed' }
            $context.measurementErrors += 'Collector fixture verification failed'
        } finally {
            $collectorWatch.Stop()
            $context.collectorFixtureSeconds = $collectorWatch.Elapsed.TotalSeconds
        }
        & dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename "$id.xml" --show-slowest-tests 20 2>&1 | Tee-Object -FilePath $context.log | Out-Host
        $testExit = $LASTEXITCODE
        $timer.Stop()
        $counts = @{ testExitCode = $testExit; total = 0; passed = 0; failed = 0; skipped = 0 }
        try {
            $reports = @(Get-Item -LiteralPath (Join-Path $Checkout "TestResults/$id.xml"))
            if (!$reports.Count) { throw 'No unique xUnit XML report found' }
            foreach ($report in $reports) {
                $context.xml += $report.FullName
                Copy-Item -LiteralPath $report.FullName -Destination (Join-Path $destination ("report-$($context.xml.Count).xml"))
                $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = 'Prohibit'; $settings.XmlResolver = $null
                $reader = [Xml.XmlReader]::Create($report.FullName, $settings)
                try { $xml = [Xml.XmlDocument]::new(); $xml.Load($reader) } finally { $reader.Dispose() }
                foreach ($assembly in $xml.SelectNodes('/assemblies/assembly')) {
                    if ($assembly.GetAttribute('environment') -match '\.NET ([0-9.]+)') { $context.runtimeVersion = $Matches[1] }
                    else { $context.measurementErrors += 'Test runtime identity unavailable' }
                    if ($assembly.GetAttribute('environment') -notmatch '6 threads' -or [int]$assembly.GetAttribute('errors') -ne 0 -or [int]$assembly.GetAttribute('not-run') -ne 0) { $context.measurementErrors += 'Unexpected scheduling, assembly errors or incomplete discovery' }
                    foreach ($pair in @(@('total','total'),@('passed','passed'),@('failed','failed'),@('skipped','skipped'))) { $counts[$pair[0]] += [int]$assembly.GetAttribute($pair[1]) }
                }
            }
        } catch { $context.measurementErrors += $_.Exception.Message }
        try {
            $after = GitValue @('rev-parse','HEAD')
            if ($after -ne $context.candidateRevision -or (GitValue @('status','--porcelain'))) { $context.dirty = $true; $context.measurementErrors += 'Source identity changed during measurement' }
        } catch { $context.dirty = $true; $context.measurementErrors += 'Source identity observation failed' }
        try {
            $afterProduction = Get-ProductionObservation $ProductionInstallation $Project
            if (($afterProduction | ConvertTo-Json -Compress) -cne $context.productionContext) { $context.productionContext = $null; $context.measurementErrors += 'Production identity changed during measurement' }
        } catch { $context.productionContext = $null; $context.measurementErrors += 'Production identity observation failed' }
        return $counts
    }
    $resultPath = Join-Path $destination 'result.json'
    $result = Invoke-TestSuiteMeasurement $context $baselineValue $resultPath $runner { $timer.Elapsed.TotalSeconds } {
        param($timing)
        $captured = & (Join-Path $PSScriptRoot 'Capture-ResourceIncident.ps1') -Checkout $Checkout -OutputRoot $EvidenceRoot -ProductionInstallation $ProductionInstallation -Project $Project -TimingResult $timing -Label comparable-1_5x
        $captured | ConvertFrom-Json
    }
    Write-Host "Measurement: $resultPath"
    if ($result.testExitCode -ne 0) { exit $result.testExitCode }
    if ($result.assessment.correctness -ne 'passed' -or $result.measurementErrors.Count -or $result.dirty) { exit 2 }
    if ($result.assessment.hardBudget -eq 'violated') { exit 3 }
    if ($result.capture.state -eq 'failed') { exit 4 }
} finally { Pop-Location }
