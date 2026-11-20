#requires -Version 7.0
Set-StrictMode -Version Latest
if (-not ('Nightweaver.Evidence.Capture.Collector' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'UnattendedCollector.cs')
}
function Invoke-UnattendedEvidenceCollection {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Nightweaver.Evidence.Capture.Options]$Options)
    $ErrorActionPreference = 'Stop'
    [Nightweaver.Evidence.Capture.Collector]::Validate($Options)
    $archive = Join-Path $Options.EvidenceRoot ('unattended-' + [guid]::NewGuid().ToString('N'))
    $clock = [Nightweaver.Evidence.Capture.RealClock]::new()
    $sources = [Nightweaver.Evidence.Capture.LocalSources]::new($Options,
        (Get-Command bd -CommandType Application).Source,
        (Get-Command git -CommandType Application).Source,
        (Join-Path $PSHOME 'pwsh.exe'),
        (Join-Path $PSScriptRoot 'Capture-UnattendedResource.ps1'))
    $writer = [Nightweaver.Evidence.Capture.ArchiveWriter]::CreateBounded($archive, [Nightweaver.Evidence.Capture.Limits]::new())
    $signal = [Nightweaver.Evidence.Capture.StopSignal]::new()
    $collector = $null
    $result = $null
    $finalization = [Diagnostics.Stopwatch]::new()
    $collectorIdentity = Get-Process -Id $PID
    try {
        $collector = [Nightweaver.Evidence.Capture.Collector]::new($Options, $clock, $sources, $writer, $signal.Token)
        $result = $collector.Run().GetAwaiter().GetResult()
        $finalization.Start()
        $result['archiveDirectory'] = $archive
        $result['externalCommands'] = $sources.Commands
        $result['collectorProcessId'] = $PID
        $result['collectorStartedUtc'] = $collectorIdentity.StartTime.ToUniversalTime().ToString('o')
    } finally {
        $signal.Dispose(); $sources.Dispose()
        if ($null -eq $collector -or $collector.WriterSettled) {
            $disposed = $writer.DisposeWithinDeadline()
            if (-not $disposed -and $result) { $result['writerSettled']=$false; $result['reason']='writer-disposal-uncertain' }
        }
    }
    $result['validation'] = @{integrityValid=$false; unattendedVerdict='not-evaluated'; errors=@(@{code='writer-settlement-uncertain'})}
    if ($result['writerSettled']) {
        $deadline = [Threading.CancellationTokenSource]::new(5000)
        try {
            $inspection = [Nightweaver.Evidence.Capture.NativeRead]::Run((Join-Path $PSHOME 'pwsh.exe'),
                @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-UnattendedEvidenceArchive.ps1'),'-ArchiveDirectory',$archive),
                $Options.EvidenceRoot, [Nightweaver.Evidence.Capture.LocalSources]::ReadEnvironment(), 1048576, $deadline.Token).GetAwaiter().GetResult()
            $result['validation'] = if ($inspection.Outcome -eq 'complete') { [Text.Encoding]::UTF8.GetString($inspection.Bytes) | ConvertFrom-Json } else { @{integrityValid=$false; unattendedVerdict='not-evaluated'; errors=@(@{code='validation-unavailable'})} }
        } finally { $deadline.Dispose() }
    }
    $finalization.Stop()
    $result['finalizationMs'] = $result['shutdownMs'] + $finalization.ElapsedMilliseconds
    return $result
}
Export-ModuleMember -Function Invoke-UnattendedEvidenceCollection
