# Pure policy and injected execution seams keep boundary tests free of native journeys.
function Get-TestSuiteAssessment {
    param($Result, $Baseline)
    $correct = $Result.testExitCode -eq 0 -and $Result.total -ge 995 -and $Result.failed -eq 0 -and $Result.skipped -eq 0 -and $Result.passed -eq $Result.total
    $reasons = [Collections.Generic.List[string]]::new()
    if ($null -eq $Baseline) { $reasons.Add('No explicit retained baseline supplied') }
    else {
        foreach ($key in @('schema','commandPolicy','threads','preparation','dotnetVersion','runtimeVersion','bdVersion','logicalProcessors','tempRoot','TEMP','TMP','productionContext','machine','bootUtc','schedulingHash')) {
            if (!$Baseline.Contains($key) -or !$Result.Contains($key) -or $null -eq $Baseline[$key] -or $Baseline[$key] -cne $Result[$key]) { $reasons.Add("Missing or mismatched $key") }
        }
        foreach ($key in @('candidateRevision','candidateTree','evidenceId','command','elapsedSeconds','total','passed','failed','skipped','testExitCode','dirty','accepted')) {
            if (!$Baseline.Contains($key)) { $reasons.Add("Baseline missing $key") }
        }
        if ($Baseline.dirty -ne $false -or $Baseline.accepted -ne $true -or $Baseline.testExitCode -ne 0 -or $Baseline.failed -ne 0 -or $Baseline.skipped -ne 0 -or $Baseline.total -lt 995 -or $Baseline.passed -ne $Baseline.total) { $reasons.Add('Baseline is not an accepted clean complete passing measurement') }
        if ($Result.dirty) { $reasons.Add('Candidate worktree is dirty') }
        if ($Baseline.candidateRevision -notmatch '^[0-9a-fA-F]{40}$' -or $Baseline.candidateTree -notmatch '^[0-9a-fA-F]{40}$' -or
            $Baseline.evidenceId -notmatch '^[0-9a-fA-F]{32}$') { $reasons.Add('Baseline identity invalid') }
        $expectedCommand = "dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename $($Baseline.evidenceId).xml --show-slowest-tests 20"
        if ($Baseline.command -cne $expectedCommand) { $reasons.Add('Baseline command is not the complete normal suite') }
        if (!$Result.production -or $Result.production.state -in @('unknown','unobserved') -or
            !$Baseline.production -or $Baseline.production.state -in @('unknown','unobserved')) { $reasons.Add('Production state unavailable') }
        # JSON strings, arrays and objects are not numeric measurements. Never let optional
        # baseline conversion prevent retention of the candidate's original test result.
        $baselineSeconds = 0.0
        $numeric = $Baseline.elapsedSeconds -is [ValueType] -and $Baseline.elapsedSeconds -isnot [bool] -and
            [double]::TryParse([string]$Baseline.elapsedSeconds, [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$baselineSeconds)
        if (!$numeric -or ![double]::IsFinite($baselineSeconds) -or $baselineSeconds -le 0) { $reasons.Add('Baseline elapsed time unavailable or invalid') }
    }
    $comparable = $reasons.Count -eq 0
    [ordered]@{
        correctness = $(if ($correct) { 'passed' } else { 'failed-or-incomplete' })
        operatingTarget = $(if ($Result.elapsedSeconds -lt 240) { 'met' } else { 'missed-cost-review-required' })
        hardBudget = $(if ($Result.elapsedSeconds -lt 300) { 'met' } else { 'violated' })
        comparisonAvailable = $comparable; comparisonReasons = @($reasons)
        deltaSeconds = $(if ($comparable) { $Result.elapsedSeconds - $baselineSeconds } else { $null })
        ratio = $(if ($comparable) { $Result.elapsedSeconds / $baselineSeconds } else { $null })
        captureRequired = $comparable -and $Result.elapsedSeconds -ge 1.5 * $baselineSeconds
    }
}

function Invoke-TestSuiteMeasurement {
    param([System.Collections.IDictionary]$Context, $Baseline, [string]$ResultPath,
        [scriptblock]$Runner, [scriptblock]$Elapsed, [scriptblock]$Capture)
    $result = [ordered]@{}; foreach ($key in $Context.Keys) { $result[$key] = $Context[$key] }
    # Runner settles before timing stops; evidence is written before capture. Never retry here.
    $observed = & $Runner
    foreach ($key in $Context.Keys) { $result[$key] = $Context[$key] }
    foreach ($key in @('testExitCode','total','passed','failed','skipped')) { $result[$key] = $observed[$key] }
    $result.elapsedSeconds = & $Elapsed
    if (![double]::IsFinite($result.elapsedSeconds) -or $result.elapsedSeconds -lt 0) { throw 'Invalid outer elapsed time' }
    $result.assessment = Get-TestSuiteAssessment $result $Baseline
    $result.accepted = $false # Acceptance requires explicit review of attribution and comparable context.
    $result.capture = [ordered]@{ state = 'not-triggered'; elapsedSeconds = 0; evidence = $null; error = $null }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ResultPath -Encoding utf8
    if ($result.assessment.captureRequired) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            $result.capture.evidence = & $Capture $ResultPath
            if ($result.capture.evidence.errors -gt 0) { throw 'Collector retained observation errors; inspect capture-errors.json' }
            $result.capture.state = 'captured'
        }
        catch { $result.capture.state = 'failed'; $result.capture.error = $_.Exception.Message }
        finally { $watch.Stop(); $result.capture.elapsedSeconds = $watch.Elapsed.TotalSeconds }
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ResultPath -Encoding utf8
    }
    return $result
}

function Get-ProductionObservation {
    param([string]$Installation, [string]$Project)
    if (!$Installation) { return [ordered]@{ state = 'unobserved'; build = $null; runtime = $null; configurationHash = $null } }
    $manifest = Get-Content -LiteralPath (Join-Path $Installation 'nightweaver-deployment.json') -Raw | ConvertFrom-Json
    $ownerPath = Join-Path $Installation "run/$Project/background/owner.json"
    $owner = if (Test-Path -LiteralPath $ownerPath) { Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json } else { $null }
    $state = 'unknown'; $runtime = $null
    if ($owner) {
        $process = Get-Process -Id $owner.process_id -ErrorAction SilentlyContinue
        if ($process -and [Math]::Abs(($process.StartTime.ToUniversalTime() - ([datetimeoffset]::Parse($owner.process_started_at_utc)).UtcDateTime).TotalSeconds) -lt 1) {
            $state = $owner.state; $runtime = $owner.runtime_id
        } elseif ($owner.state -eq 'stopped') { $state = 'stopped' }
    }
    [ordered]@{ state = $state; build = $manifest.build_version; runtime = $runtime
        configurationHash = (Get-FileHash -LiteralPath (Join-Path $Installation "projects/$Project.json") -Algorithm SHA256).Hash }
}
Export-ModuleMember -Function Get-TestSuiteAssessment, Invoke-TestSuiteMeasurement, Get-ProductionObservation
