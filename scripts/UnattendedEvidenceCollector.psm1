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
    $collectorIdentity = Get-Process -Id $PID
    try {
        $collector = [Nightweaver.Evidence.Capture.Collector]::new($Options, $clock, $sources, $writer, $signal.Token)
        $result = $collector.Run().GetAwaiter().GetResult()
        $result['archiveDirectory'] = $archive
        $result['externalCommands'] = $sources.Commands
        $result['collectorProcessId'] = $PID
        $result['collectorStartedUtc'] = $collectorIdentity.StartTime.ToUniversalTime().ToString('o')
    } finally {
        $signal.Dispose()
        $result = $collector.Finish($result, (Join-Path $PSHOME 'pwsh.exe'),
            (Join-Path $PSScriptRoot 'Test-UnattendedEvidenceArchive.ps1'), $archive).GetAwaiter().GetResult()
    }
    return $result
}
Export-ModuleMember -Function Invoke-UnattendedEvidenceCollection
