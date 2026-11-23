#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('overview','explorer','attention','workspace','digest')][string[]]$Surfaces = @('overview','explorer','attention','workspace','digest'),
    [ValidateSet('active','settled')][string[]]$Activity = @('settled','active'),
    [ValidateRange(1,5)][int]$WarmCycles = 1,
    [ValidateRange(1,3)][int]$ColdRepetitions = 1
)
$ErrorActionPreference = 'Stop'
$checkout = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$apphost = Join-Path $PSScriptRoot 'Nightweaver.QA.AppHost.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new evidence directory' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$names = @('NIGHTWEAVER_QA_SCENARIO','NIGHTWEAVER_QA_URL','NIGHTWEAVER_QA_CONFIG','NIGHTWEAVER_QA_OUTPUT',
    'NIGHTWEAVER_PROBE_BD','NIGHTWEAVER_PROBE_GIT','NIGHTWEAVER_PROBE_TRACE','NIGHTWEAVER_PROBE_SHIM',
    'NIGHTWEAVER_PROBE_ACTIVITY','NIGHTWEAVER_PROBE_SURFACE','NIGHTWEAVER_PROBE_WARM_CYCLES')
$previous = @{}; foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$runs = [Collections.Generic.List[object]]::new()
$metadata = [ordered]@{
    schema = 'nightweaver-shared-read-baseline-v1'; revision = (& git -C $checkout rev-parse HEAD)
    tree = (& git -C $checkout rev-parse 'HEAD^{tree}'); dirty = [bool](& git -C $checkout status --porcelain)
    measuredUtc = [datetime]::UtcNow.ToString('o'); machine = [Environment]::MachineName
    bootUtc = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
    sdk = (& dotnet --version); bd = (& bd --version); aspire = (& aspire --version); node = (& node --version)
    temp = [IO.Path]::GetTempPath(); warmCycles = $WarmCycles; coldRepetitions = $ColdRepetitions
    surfaces = $Surfaces; activity = $Activity; productionTouched = $false
    preparation = 'Restore and build tools; seed each tracker before web navigation; condition each warm surface once.'
    latencyTarget = $null; runs = $runs; failure = $null
}
try {
    $env:NIGHTWEAVER_PROBE_BD = (Get-Command bd -CommandType Application).Source
    $env:NIGHTWEAVER_PROBE_GIT = (Get-Command git -CommandType Application).Source
    $shim = Join-Path $output 'shim'
    & dotnet build (Join-Path $checkout 'tests/Nightweaver.QA.ToolProbe/Nightweaver.QA.ToolProbe.csproj') -o $shim > (Join-Path $output 'shim-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Tool probe build failed' }
    Copy-Item -LiteralPath (Join-Path $shim 'bd.exe') -Destination (Join-Path $shim 'git.exe')
    $env:NIGHTWEAVER_PROBE_SHIM = $shim
    $env:NIGHTWEAVER_QA_SCENARIO = 'shared-read'
    $env:NIGHTWEAVER_PROBE_WARM_CYCLES = "$WarmCycles"
    foreach ($activityMode in $Activity) { foreach ($surface in $Surfaces) { foreach ($repetition in 1..$ColdRepetitions) {
        $run = [ordered]@{ surface = $surface; activity = $activityMode; repetition = $repetition;
            startedUtc = [datetime]::UtcNow.ToString('o'); finishedUtc = $null; startExit = $null; stopExit = $null; probeExit = $null }
        $runs.Add($run)
        $trial = Join-Path $output "$activityMode-$surface-$repetition"
        [IO.Directory]::CreateDirectory($trial) | Out-Null
        $env:NIGHTWEAVER_PROBE_TRACE = Join-Path $trial 'tools'
        $env:NIGHTWEAVER_QA_OUTPUT = $trial
        $env:NIGHTWEAVER_PROBE_ACTIVITY = $activityMode
        $env:NIGHTWEAVER_PROBE_SURFACE = $surface
        try {
            & aspire start --isolated --non-interactive --apphost $apphost > (Join-Path $trial 'start.log')
            $run.startExit = $LASTEXITCODE
            if ($LASTEXITCODE -ne 0) { throw 'Isolated AppHost start failed' }
            & aspire wait web --non-interactive --apphost $apphost > (Join-Path $trial 'wait.log')
            if ($LASTEXITCODE -ne 0) { throw 'Web readiness failed' }
            $description = & aspire describe --format Json --non-interactive --apphost $apphost | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0) { throw 'Endpoint discovery failed' }
            $web = @($description.resources | Where-Object displayName -eq web)
            if ($web.Count -ne 1) { throw 'Expected one web resource' }
            $env:NIGHTWEAVER_QA_URL = ($web[0].urls | Where-Object name -eq http).url
            $env:NIGHTWEAVER_QA_CONFIG = $web[0].environment.Nightweaver__ConfigPath
            if (!$env:NIGHTWEAVER_QA_URL -or !$env:NIGHTWEAVER_QA_CONFIG) { throw 'Missing direct URL or disposable config' }
            & node (Join-Path $PSScriptRoot 'shared-read-qa.mjs') 2>&1 | Tee-Object -FilePath (Join-Path $trial 'browser.log')
            $run.probeExit = $LASTEXITCODE
            if ($LASTEXITCODE -ne 0) { throw 'Shared read browser probe failed' }
        } finally {
            try { & aspire logs web --tail 120 --non-interactive --apphost $apphost > (Join-Path $trial 'web.log') }
            finally {
                & aspire stop --non-interactive --apphost $apphost > (Join-Path $trial 'stop.log')
                $run.stopExit = $LASTEXITCODE
                $run.finishedUtc = [datetime]::UtcNow.ToString('o')
                if ($LASTEXITCODE -ne 0) { throw 'Exact isolated stack stop failed' }
            }
        }
    } } }
} catch {
    $metadata.failure = $_.Exception.Message
    throw
} finally {
    $metadata.finishedUtc = [datetime]::UtcNow.ToString('o')
    $metadata | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'result.json')
    & node (Join-Path $PSScriptRoot 'shared-read-report.mjs') $output > (Join-Path $output 'report.log')
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object Extension -ne '.exe' |
        ForEach-Object { [pscustomobject]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'hashes.json')
}
