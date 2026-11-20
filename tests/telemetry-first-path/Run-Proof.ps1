#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$checkout = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path $output) { throw 'Use a fresh evidence directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$fixture = Get-Content (Join-Path $PSScriptRoot 'fixture.json') -Raw | ConvertFrom-Json
$name = 'nightweaver-first-path-' + [guid]::NewGuid().ToString('N')
$names = @('NIGHTWEAVER_FIRST_PATH_OTLP','NIGHTWEAVER_FIRST_PATH_EVIDENCE','NIGHTWEAVER_GRAFANA_URL','NIGHTWEAVER_METRICS_URL','NIGHTWEAVER_LOKI_URL','NIGHTWEAVER_TEMPO_URL')
$saved = @{}
foreach ($key in $names) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
$container = $null
Push-Location $checkout
try {
    $dockerArgs = @('run','--detach','--name',$name,'--label','nightweaver.fixture=first-path',
        '--env','GF_AUTH_ANONYMOUS_ENABLED=true','--env','GF_AUTH_ANONYMOUS_ORG_ROLE=Admin',
        '--env','GF_ANALYTICS_REPORTING_ENABLED=false','--env','GF_ANALYTICS_CHECK_FOR_UPDATES=false')
    foreach ($port in $fixture.ports) { $dockerArgs += @('--publish',"127.0.0.1::$port") }
    $container = (& docker @dockerArgs $fixture.image | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Disposable Grafana container could not start.' }
    function Port([int]$port) {
        $mapping = (& docker port $name "$port/tcp" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $mapping -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Unexpected fixture port binding.' }
        return "http://$mapping"
    }
    $env:NIGHTWEAVER_GRAFANA_URL = Port 3000
    $env:NIGHTWEAVER_FIRST_PATH_OTLP = (Port 4318) + '/'
    $env:NIGHTWEAVER_FIRST_PATH_EVIDENCE = $output
    $env:NIGHTWEAVER_METRICS_URL = Port 9090
    $env:NIGHTWEAVER_LOKI_URL = Port 3100
    $env:NIGHTWEAVER_TEMPO_URL = Port 3200
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while ($true) {
        & docker exec $name test -f /tmp/ready
        if ($LASTEXITCODE -eq 0) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Local Grafana readiness deadline expired.' }
        Start-Sleep -Seconds 1
    }
    & dotnet test --solution Nightweaver.slnx --filter-method Nightweaver.Tests.FirstPathTelemetryTests.NativeInvocationAndIndependentlyVerifiedClosureExportCorrelatedSignals --minimum-expected-tests 1 --report-xunit-xml --report-xunit-xml-filename first-path-grafana.xml 2>&1 | Tee-Object (Join-Path $output 'native-smoke.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Native OTLP proof failed.' }
    & dotnet test --solution Nightweaver.slnx --filter-class Nightweaver.Tests.DashboardTelemetryTests --minimum-expected-tests 2 --report-xunit-xml --report-xunit-xml-filename dashboard-load.xml 2>&1 | Tee-Object (Join-Path $output 'component-load.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Paired load/unavailable proof failed.' }
    & node (Join-Path $checkout 'tests/Nightweaver.QA.AppHost/first-path-grafana-qa.mjs') $output
    if ($LASTEXITCODE -ne 0) { throw 'Grafana query/browser proof failed.' }
    @{ image = $fixture.image; container = $container; hostedAcceptance = 'not performed'; accountChanges = $false } |
        ConvertTo-Json | Set-Content (Join-Path $output 'fixture.json')
} finally {
    if ($container) {
        & docker logs $name *> (Join-Path $output 'fixture.log')
        & docker rm --force $name | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Error 'Exact disposable container cleanup failed.' }
        'Exact disposable container removed.' | Set-Content (Join-Path $output 'stopped.txt')
    }
    foreach ($key in $names) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    Pop-Location
}
