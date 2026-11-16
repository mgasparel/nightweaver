param(
    [ValidateSet('Default', 'Commands', 'Invocation', 'Recovery', 'Presentation')][string]$Group = 'Default',
    [string[]]$Only = @(),
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$apphost = Join-Path $PSScriptRoot 'Nightweaver.QA.AppHost.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo "TestResults/studio-cutover/$Group" }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$previousScenario = $env:NIGHTWEAVER_QA_SCENARIO
$previousUrl = $env:NIGHTWEAVER_QA_URL
$previousConfig = $env:NIGHTWEAVER_QA_CONFIG
$previousOutput = $env:NIGHTWEAVER_QA_OUTPUT
$probes = switch ($Group) {
    Default { @('studio-qa', 'smoke', 'studio-attention-qa', 'operations-qa', 'studio-explorer-qa', 'digest-qa', 'read-timeout-qa', 'refresh-timeout-qa', 'runtime-refresh-qa') }
    Commands { @('planning-readiness-qa', 'cleanup-qa') }
    Invocation { @('factory-qa', 'planning-telemetry-qa', 'telemetry') }
    Presentation { @('studio-presentation-qa') }
    Recovery { @('studio-abort-refresh-qa', 'studio-recovery-qa') }
}
if ($Only.Count) {
    foreach ($probe in $Only) { if ($probe -notin $probes) { throw "Unknown probe for ${Group}: $probe" } }
    $probes = @($probes | Where-Object { $_ -in $Only })
}
try {
    $env:NIGHTWEAVER_QA_SCENARIO = if ($Group -eq 'Commands') { 'cleanup' } else { $null }
    aspire start --isolated --non-interactive --apphost $apphost
    if ($LASTEXITCODE -ne 0) { throw 'Isolated AppHost start failed' }
    aspire wait web --non-interactive --apphost $apphost
    if ($LASTEXITCODE -ne 0) { throw 'Web readiness failed' }
    $description = aspire describe --format Json --non-interactive --apphost $apphost | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Endpoint discovery failed' }
    $web = @($description.resources | Where-Object displayName -eq web)
    if ($web.Count -ne 1) { throw 'Expected exactly one web resource' }
    $env:NIGHTWEAVER_QA_URL = ($web[0].urls | Where-Object name -eq http).url
    $env:NIGHTWEAVER_QA_CONFIG = $web[0].environment.Nightweaver__ConfigPath
    if (!$env:NIGHTWEAVER_QA_URL -or !$env:NIGHTWEAVER_QA_CONFIG) { throw 'Missing discovered direct URL/config' }
    foreach ($probe in $probes) {
        $env:NIGHTWEAVER_QA_OUTPUT = Join-Path $output $probe
        $timer = [Diagnostics.Stopwatch]::StartNew()
        node (Join-Path $PSScriptRoot "$probe.mjs") 2>&1 | Tee-Object -FilePath (Join-Path $output "$probe.log")
        $probeExit = $LASTEXITCODE
        $timer.Stop()
        Write-Host "$probe wall seconds: $($timer.Elapsed.TotalSeconds)"
        if ($probeExit -ne 0) { throw "$probe failed (exit $probeExit)" }
    }
} finally {
    try {
        aspire logs web --tail 120 --non-interactive --apphost $apphost > (Join-Path $output "web.log")
    } finally {
        try {
            aspire stop --non-interactive --apphost $apphost
            if ($LASTEXITCODE -ne 0) { throw 'Exact AppHost teardown failed' }
        } finally {
            $env:NIGHTWEAVER_QA_SCENARIO = $previousScenario
            $env:NIGHTWEAVER_QA_URL = $previousUrl
            $env:NIGHTWEAVER_QA_CONFIG = $previousConfig
            $env:NIGHTWEAVER_QA_OUTPUT = $previousOutput
        }
    }
}
