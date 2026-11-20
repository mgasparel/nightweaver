#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$watch = [Diagnostics.Stopwatch]::StartNew()
if (-not ('Nightweaver.Evidence.Tests.CollectorFixtures' -as [type])) {
    Add-Type -Path @((Join-Path $PSScriptRoot '../UnattendedCollector.cs'), (Join-Path $PSScriptRoot 'UnattendedCollectorFixtures.cs'))
}
Import-Module (Join-Path $PSScriptRoot '../UnattendedEvidenceArchive.psm1')
$root = Join-Path $OutputRoot ('unattended-collector-checks-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$result = [Nightweaver.Evidence.Tests.CollectorFixtures]::RunAll($root).GetAwaiter().GetResult()
$sourceDirectory = Join-Path $root 'source-boundary'
[IO.Directory]::CreateDirectory($sourceDirectory) | Out-Null
$source = Join-Path $sourceDirectory 'original.bin'
[IO.File]::WriteAllBytes($source, [byte[]](1,2,3,4))
$sourceHash = (Get-FileHash $source).Hash
$pinned = [Nightweaver.Evidence.Capture.SafeFile]::new($source)
try {
    $renamed=$false
    try { [IO.Directory]::Move($sourceDirectory, ($sourceDirectory+'-moved')); $renamed=$true } catch [IO.IOException] {}
    if ($renamed) { throw 'Source ancestor could be replaced while read handle was pinned' }
    $bytes=$pinned.Read(2,0,[Threading.CancellationToken]::None,$true).GetAwaiter().GetResult()
    if ($bytes.Length -ne 2 -or -not $pinned.Generation) { throw 'Incremental file chunk or generation incorrect' }
    $result['passed']+=2
} finally { $pinned.Dispose() }
if ((Get-FileHash $source).Hash -ne $sourceHash) { throw 'Read boundary mutated source bytes' }
$result['passed']++
$alias=Join-Path $root 'source-alias'
New-Item -ItemType Junction -Path $alias -Target $sourceDirectory | Out-Null
$rejected=$false
try { $unsafe=[Nightweaver.Evidence.Capture.SafeFile]::new((Join-Path $alias 'original.bin')); $unsafe.Dispose() } catch { $rejected=$true }
if (-not $rejected) { throw 'Source junction followed' }
$result['passed']++
$hardlink=Join-Path $sourceDirectory 'hardlink.bin'
New-Item -ItemType HardLink -Path $hardlink -Target $source | Out-Null
$rejected=$false
try { $unsafe=[Nightweaver.Evidence.Capture.SafeFile]::new($source); $unsafe.Dispose() } catch { $rejected=$true }
if (-not $rejected) { throw 'Source hard link accepted' }
$result['passed']++
foreach ($archive in $result['archives']) {
    $validation = Test-UnattendedEvidenceArchive $archive
    $validation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($archive + '-validation.json') -Encoding utf8
    if (-not $validation.integrityValid -or $validation.unattendedVerdict -ne 'not-evaluated') { throw "Stopped collector fixture failed independent integrity validation: $archive" }
    $result['passed']++
}
$watch.Stop()
$result['seconds'] = $watch.Elapsed.TotalSeconds
$result['evidenceDirectory'] = $root
$result | ConvertTo-Json -Depth 5
