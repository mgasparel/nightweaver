#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArchiveDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'UnattendedEvidenceArchive.psm1') -Force
$result = Test-UnattendedEvidenceArchive -ArchiveDirectory $ArchiveDirectory
$result | ConvertTo-Json -Depth 8
if (-not $result.integrityValid) { exit 2 }
