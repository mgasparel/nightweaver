#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installation,
    [Parameter(Mandatory)][string]$Configuration,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string[]]$WorkloadIds,
    [Parameter(Mandatory)][string]$Contract,
    [Parameter(Mandatory)][string]$ContractRevision,
    [Parameter(Mandatory)][datetimeoffset]$StartUtc,
    [Parameter(Mandatory)][datetimeoffset]$CutoffUtc,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$WebUrl,
    [ValidateRange(0,28800)][int]$StopAfterSeconds = 0,
    [hashtable]$InvocationLogs = @{},
    [hashtable]$References = @{}
)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'UnattendedEvidenceCollector.psm1')
    if ($WorkloadIds.Count -eq 1 -and $WorkloadIds[0].Contains(',')) { $WorkloadIds = $WorkloadIds[0].Split(',') }
    $options = [Nightweaver.Evidence.Capture.Options]::new()
    foreach ($key in 'Installation','Configuration','Repository','Project','WorkloadIds','Contract','ContractRevision','StartUtc','CutoffUtc','EvidenceRoot','WebUrl','StopAfterSeconds') { $options.$key = Get-Variable -Name $key -ValueOnly }
    foreach ($key in $InvocationLogs.Keys) { $options.InvocationLogs.Add($key, $InvocationLogs[$key]) }
    foreach ($key in $References.Keys) { $options.References.Add($key, $References[$key]) }
    $result = Invoke-UnattendedEvidenceCollection $options
    $result | ConvertTo-Json -Depth 12
    if (-not $result.validation.integrityValid -or $result.reason -ne 'collection-cutoff') { exit 2 }
} catch {
    # Never export exception text, input paths, config contents or native stderr.
    '{"schema":"nightweaver-unattended-collector-result-v1","reason":"preflight-or-storage-unavailable","unattendedVerdict":"not-evaluated"}'
    exit 2
}
