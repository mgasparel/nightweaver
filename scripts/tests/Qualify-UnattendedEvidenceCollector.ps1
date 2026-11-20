#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputRoot, [ValidateRange(12,60)][int]$Seconds = 20)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../UnattendedEvidenceCollector.psm1')
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
public sealed class CollectorQualificationServer : IDisposable {
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private Task worker;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int Gets;
    public CollectorQualificationServer(string repository) {
        listener.Start();
        worker = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                using (var client = await listener.AcceptTcpClientAsync(stop.Token))
                using (var stream = client.GetStream()) {
                    var buffer = new byte[4096];
                    int count = await stream.ReadAsync(buffer.AsMemory(), stop.Token);
                    var request = Encoding.ASCII.GetString(buffer, 0, count).Split('\r')[0].Split(' ');
                    if (request.Length != 3 || request[0] != "GET") throw new Exception("Non-GET request");
                    Interlocked.Increment(ref Gets);
                    string key = Uri.UnescapeDataString(request[1].Split("?key=")[1]);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema="nightweaver-observation-v1", projectId="fixture", key,
                        epoch="qualification-web-epoch", observedAt=DateTimeOffset.UtcNow, health="fresh", data=new { availability="live", snapshot=new { runtime_id=new string('c',32), project="fixture", target_repository=repository } },
                        sources=new {}, policy=new { maxSnapshotAgeMs=120000, maxPendingAgeMs=5000 }, validatedAgeMs=0, pendingAgeMs=(int?)null });
                    var header=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                    await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(bytes, stop.Token);
                }
            }
        });
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); try { worker.Wait(500); } catch {} stop.Dispose(); }
}
'@
$root = Join-Path $OutputRoot ('unattended-qualification-' + [guid]::NewGuid().ToString('N'))
$installation = Join-Path $root 'installation'
$repository = Join-Path $root 'repository'
$evidence = Join-Path $root 'evidence'
foreach ($path in @($installation,$repository,$evidence,(Join-Path $installation 'projects'),(Join-Path $installation 'web'),(Join-Path $installation 'run/fixture/background'),(Join-Path $installation 'telemetry/fixture'))) { [IO.Directory]::CreateDirectory($path) | Out-Null }
$utf8 = [Text.UTF8Encoding]::new($false)
function Save($Path,$Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12 -Compress), $utf8) }
# Synthetic Git repository only: no host lifecycle, Agents, real workload or tracker setup.
git -C $repository init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Disposable git initialization failed' }
git -C $repository -c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty -m fixture --quiet
if ($LASTEXITCODE -ne 0) { throw 'Disposable git identity preparation failed' }
$configuration = Join-Path $installation 'projects/fixture.json'
Save $configuration @{credential='configuration-canary-never-copy';project='fixture'}
Save (Join-Path $installation 'nightweaver-deployment.json') @{schema_version=1;build_version=('a'*40)}
[IO.File]::WriteAllBytes((Join-Path $installation 'nightweaver.exe'), [byte[]](1,2,3))
[IO.File]::WriteAllBytes((Join-Path $installation 'web/Nightweaver.Web.exe'), [byte[]](4,5,6))
$contract = Join-Path $root 'contract.md'
[IO.File]::WriteAllText($contract, 'Contract version: **1**. Synthetic qualification only.', $utf8)
$process = Get-Process -Id $PID
$created = $process.StartTime.ToUniversalTime().ToString('o')
Save (Join-Path $installation 'run/fixture/background/owner.json') @{
    schema_version=1;project='fixture';root=$installation;config_path=$configuration;target_repository=$repository
    run_id=('b'*32);runtime_id=('c'*32);process_id=$PID;process_started_at_utc=$created;state='running'
}
Save (Join-Path $installation 'telemetry/fixture/factory.json') @{
    schema_version=2;project='fixture';target_repository=$repository;runtime_id=('c'*32);observed_at=[datetime]::UtcNow.ToString('o')
}
function Snapshot {
    @(foreach ($path in @($installation,$repository,$contract)) {
        Get-ChildItem -LiteralPath $path -Recurse -Force -File | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName).Hash)" }
    }) -join "`n"
}
$before = Snapshot
$server = [CollectorQualificationServer]::new($repository)
$options = [Nightweaver.Evidence.Capture.Options]::new()
$options.Installation=$installation; $options.Configuration=$configuration; $options.Repository=$repository; $options.EvidenceRoot=$evidence
$options.Project='fixture'; $options.Contract=$contract; $options.ContractRevision=('d'*40)
$options.WorkloadIds=@('fixture-1','fixture-2','fixture-3'); $options.StartUtc=[datetimeoffset]::UtcNow; $options.CutoffUtc=$options.StartUtc.AddHours(8)
$options.StopAfterSeconds=$Seconds; $options.WebUrl="http://127.0.0.1:$($server.Port)/"
$timer=[Diagnostics.Stopwatch]::StartNew(); $cpu=$process.TotalProcessorTime.TotalMilliseconds
$ioBefore=[Nightweaver.Evidence.Capture.NativeRead]::CurrentIo()
$captureDeadline=[Threading.CancellationTokenSource]::new(($Seconds+15)*1000)
try {
    $native=[Nightweaver.Evidence.Capture.NativeRead]::Run((Join-Path $PSHOME 'pwsh.exe'), @(
        '-NoProfile','-File',(Join-Path $PSScriptRoot '../Capture-UnattendedEvidence.ps1'),
        '-Installation',$installation,'-Configuration',$configuration,'-Repository',$repository,'-Project','fixture',
        '-WorkloadIds','fixture-1,fixture-2,fixture-3','-Contract',$contract,'-ContractRevision',('d'*40),
        '-StartUtc',$options.StartUtc.ToString('o'),'-CutoffUtc',$options.CutoffUtc.ToString('o'),
        '-EvidenceRoot',$evidence,'-WebUrl',$options.WebUrl,'-StopAfterSeconds',([string]$Seconds)
    ),$repository,[Nightweaver.Evidence.Capture.LocalSources]::ReadEnvironment(),1048576,$captureDeadline.Token).GetAwaiter().GetResult()
    if ($native.Outcome -ne 'complete' -or -not $native.NativeAccountingAvailable) { throw 'Independent collector process failed to settle' }
    $result=[Text.Encoding]::UTF8.GetString($native.Bytes) | ConvertFrom-Json -AsHashtable
    if ($result['collectorProcessId'] -eq $PID) { throw 'Collector was not independent of synthetic owner' }
} finally { $captureDeadline.Dispose(); $server.Dispose() }
$timer.Stop(); $process.Refresh()
if ((Snapshot) -cne $before) { throw 'Collector changed disposable source content' }
if (-not $result['validation'].integrityValid) { throw 'Qualification archive failed validation' }
$archive=$result['archiveDirectory']
$samples=@(Get-Content -LiteralPath (Join-Path $archive 'samples.jsonl') | ConvertFrom-Json)
$payloadBytes=(Get-ChildItem -LiteralPath (Join-Path $archive 'payloads') -File | Measure-Object Length -Sum).Sum
$archiveBytes=(Get-ChildItem -LiteralPath $archive -Recurse -File | Measure-Object Length -Sum).Sum
# A measured mean is a forecast, not readiness or a guarantee against changing log volume.
$forecast=@{runtime=6*1920;tracker=3*480;resource=68;installation=13;contract=1;authority=6}
$projectedRecords=0; $projectedBytes=1048576.0
foreach ($kind in $forecast.Keys) {
    $projectedRecords+=$forecast[$kind]
    $group=@($samples | Where-Object sourceKind -eq $kind)
    $cost=0.0
    foreach ($sample in $group) { $cost+=[Text.Encoding]::UTF8.GetByteCount(($sample | ConvertTo-Json -Depth 20 -Compress))+1; if ($sample.payload) { $cost+=$sample.payload.bytes } }
    if ($group.Count) { $projectedBytes+=$forecast[$kind]*$cost/$group.Count }
}
$projection=[ordered]@{records=$projectedRecords;bytes=[Math]::Ceiling($projectedBytes);files=$projectedRecords+3;assumption='Per-source-kind mean bytes with fixed baseline/endpoint, 1920 runtime turns (including two host logs), 480 tracker turns and 68 resource samples. New invocation volume and changing payload sizes may exhaust the archive.'}
if ($projection.records -gt 32768 -or $projection.files -gt 16384 -or $projection.bytes -gt 268435456) { throw 'Measured projection exceeds archive bounds' }
$resourceSamples=@($samples | Where-Object { $_.sourceKind -eq 'resource' -and $_.outcome -eq 'complete' })
if ($resourceSamples.Count -lt 2 -or $timer.Elapsed.TotalSeconds -gt ($Seconds+10)) { throw 'Resource or finite-settlement qualification unavailable' }
$report=[ordered]@{
    schema='nightweaver-unattended-collector-qualification-v1';synthetic=$true;sourcePreserved=$true
    archiveDirectory=$archive;archiveSha256=(Get-FileHash (Join-Path $archive 'manifest.json')).Hash.ToLowerInvariant()
    samplesSha256=(Get-FileHash (Join-Path $archive 'samples.jsonl')).Hash.ToLowerInvariant()
    result=$result;outerSeconds=$timer.Elapsed.TotalSeconds;collectorCpuMs=$native.NativeCpuMs
    collectorIo=@{readBytes=$native.NativeReadBytes;writeBytes=$native.NativeWriteBytes}
    collectorProcessId=$result['collectorProcessId'];collectorStartedUtc=$result['collectorStartedUtc']
    counterScope='Settled collector Job accounting includes its resource/validator descendants; external CIM service work is excluded.'
    qualifierCpuMs=$process.TotalProcessorTime.TotalMilliseconds-$cpu;qualifierIoBefore=$ioBefore;qualifierIoAfter=[Nightweaver.Evidence.Capture.NativeRead]::CurrentIo()
    httpGets=$server.Gets;archiveBytes=$archiveBytes;payloadBytes=$payloadBytes
    gaps=@($samples | Where-Object outcome -ne 'complete').Count;projection=$projection
    visibility='No tracker is initialized; native readonly reads fail honestly. Synthetic factory heartbeat ages without a publisher. No production or Agent process is started.'
}
$probes=[ordered]@{}
foreach ($probe in @(
    @{name='output-limit';code="[Console]::Out.Write(('x'*1100000)); Start-Sleep -Seconds 20";milliseconds=2000;expected='limit'},
    @{name='deadline';code='Start-Sleep -Seconds 20';milliseconds=300;expected='timeout'}
)) {
    $deadline=[Threading.CancellationTokenSource]::new($probe.milliseconds)
    $probeWatch=[Diagnostics.Stopwatch]::StartNew()
    try {
        $value=[Nightweaver.Evidence.Capture.NativeRead]::Run((Join-Path $PSHOME 'pwsh.exe'),
            @('-NoProfile','-Command',$probe.code),$repository,[Nightweaver.Evidence.Capture.LocalSources]::ReadEnvironment(),1048576,$deadline.Token).GetAwaiter().GetResult()
        if ($value.Outcome -ne $probe.expected -or $value.Code -eq 'settlement-uncertain') { throw 'Native containment probe failed' }
        $probes[$probe.name]=@{outcome=$value.Outcome;code=$value.Code;elapsedMs=$probeWatch.ElapsedMilliseconds}
    } finally { $deadline.Dispose() }
}
$report['nativeContainment']=$probes
$report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $root 'qualification.json') -Encoding utf8
$report | ConvertTo-Json -Depth 14
