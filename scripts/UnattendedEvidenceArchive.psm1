#requires -Version 7.0
Set-StrictMode -Version Latest
if (-not ('Nightweaver.Evidence.UnattendedArchiveReader' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'UnattendedArchiveReader.cs')
}

function Test-UnattendedEvidenceArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ArchiveDirectory)

    $result = [ordered]@{
        schema = 'nightweaver-unattended-archive-validation-v1'; integrityValid = $false
        unattendedVerdict = 'not-evaluated'; archiveState = $null; archiveBytes = 0L
        recordCount = 0; manifestSha256 = $null; samplesSha256 = $null
        observationGapCount = 0; epochTransitionCount = 0; detailsTruncated = $false
        errors = [Collections.Generic.List[object]]::new()
        observationGaps = [Collections.Generic.List[object]]::new()
        epochTransitions = [Collections.Generic.List[object]]::new()
    }
    $reader = $null
    $field = 'archive'
    $recordNumber = 0
    function Require($condition) { if (-not $condition) { throw [IO.InvalidDataException]::new('Invalid archive field.') } }
    function Object($value, [string[]]$keys) {
        Require ($value -is [Collections.Generic.Dictionary[string,object]])
        Require ($value.Count -eq $keys.Count)
        foreach ($key in $keys) { Require ($value.ContainsKey($key)) }
    }
    function Text($value, [int]$maximum = 128) { Require ($value -is [string] -and $value.Length -gt 0 -and $value.Length -le $maximum) }
    function Number($value, [long]$maximum = [long]::MaxValue) { Require ($value -is [long] -and $value -ge 0 -and $value -le $maximum) }
    function Hex($value, [int]$length) { Text $value $length; Require ($value -cmatch "^[0-9a-f]{$length}$") }
    function Utc($value) {
        Text $value 28
        Require ($value -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$')
        $parsed = [datetimeoffset]::MinValue
        Require ([datetimeoffset]::TryParse($value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed))
        return $parsed
    }
    function Gap([string]$code, [int]$sequence = 0) {
        $result.observationGapCount++
        if ($result.observationGaps.Count -lt 128) { $result.observationGaps.Add(@{code=$code; sequence=$sequence}) }
        else { $result.detailsTruncated = $true }
    }
    function Payload($reference, [int]$maximum = 1048576) {
        Object $reference @('path','bytes','sha256')
        Text $reference.path 240
        Require ($reference.path.StartsWith('payloads/', [StringComparison]::Ordinal))
        Number $reference.bytes $maximum
        Hex $reference.sha256 64
        [Nightweaver.Evidence.UnattendedArchiveReader]::ValidateRelative($reference.path)
        Require ($reader.Length($reference.path) -eq $reference.bytes)
        Require ($reader.Sha256($reference.path) -ceq $reference.sha256)
    }
    try {
        $reader = [Nightweaver.Evidence.UnattendedArchiveReader]::new($ArchiveDirectory)
        $result.archiveBytes = $reader.TotalBytes
        $field = 'manifest'
        $manifest = [Nightweaver.Evidence.UnattendedArchiveReader]::Json($reader.Read('manifest.json', 1048576))
        Object $manifest @('schema','runId','projectId','repositoryId','repositoryPath','contract','installation','workloadIds','requestedWindow','actualWindow','state','sampleCount','clockUncertain','references')
        Require ($manifest.schema -is [string] -and $manifest.schema -ceq 'nightweaver-unattended-archive-v1')
        Hex $manifest.runId 32
        Text $manifest.projectId
        Hex $manifest.repositoryId 64
        Text $manifest.repositoryPath 4096
        Object $manifest.contract @('version','revision','sha256')
        Number $manifest.contract.version 1
        Require ($manifest.contract.version -eq 1)
        Hex $manifest.contract.revision 40
        Hex $manifest.contract.sha256 64
        Object $manifest.installation @('revision','manifestSha256','configurationSha256')
        Hex $manifest.installation.revision 40
        Hex $manifest.installation.manifestSha256 64
        Hex $manifest.installation.configurationSha256 64
        Require ($manifest.workloadIds -is [object[]] -and $manifest.workloadIds.Count -eq 3)
        $workload = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($id in $manifest.workloadIds) { Text $id; Require ($id -cmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$'); Require ($workload.Add($id)) }
        Object $manifest.requestedWindow @('startUtc','endUtc')
        $start = Utc $manifest.requestedWindow.startUtc
        $end = Utc $manifest.requestedWindow.endUtc
        Require (($end - $start) -eq [timespan]::FromHours(8))
        Require ($manifest.state -is [string] -and $manifest.state -cin @('open','complete','interrupted'))
        Require ($manifest.clockUncertain -is [bool])
        Object $manifest.actualWindow @('startUtc','endUtc','elapsedMs')
        $actualStart = Utc $manifest.actualWindow.startUtc
        if ($manifest.state -ceq 'open') {
            Require ($null -eq $manifest.actualWindow.endUtc -and $null -eq $manifest.actualWindow.elapsedMs -and $null -eq $manifest.sampleCount)
            Gap 'archive-open'
        } else {
            $actualEnd = Utc $manifest.actualWindow.endUtc
            Number $manifest.actualWindow.elapsedMs 31536000000
            if (-not $manifest.clockUncertain) { Require ($actualEnd -ge $actualStart) }
            Number $manifest.sampleCount 32768
            if ($manifest.state -ceq 'interrupted') { Gap 'archive-interrupted' }
        }
        if ($manifest.clockUncertain) { Gap 'clock-uncertain' }
        $result.archiveState = $manifest.state
        $field = 'references'
        Object $manifest.references @('interventionJournal','operatorAttestation','budgetWorksheet','runAuthorization','qualification','checks')
        foreach ($key in $manifest.references.Keys) {
            if ($null -eq $manifest.references[$key]) { Gap "missing-$key" }
            else { Payload $manifest.references[$key] }
        }
        $field = 'samples'
        $bytes = $reader.Read('samples.jsonl', 33554432)
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        Require ($text.Length -eq 0 -or $text.EndsWith("`n", [StringComparison]::Ordinal))
        $lines = $text.Split("`n")
        $epochs = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        $invocationBytes = [Collections.Generic.Dictionary[object,long]]::new()
        for ($index = 0; $index -lt ($lines.Count - 1); $index++) {
            $recordNumber = $index + 1
            Require ($recordNumber -le 32768)
            $line = $lines[$index].TrimEnd("`r")
            Require ($line.Length -gt 0 -and [Text.Encoding]::UTF8.GetByteCount($line) -le 2097152)
            $sample = [Nightweaver.Evidence.UnattendedArchiveReader]::Json([Text.Encoding]::UTF8.GetBytes($line))
            Object $sample @('schema','sequence','runId','projectId','repositoryId','sourceKind','sourceId','collection','sourceObservedUtc','outcome','epoch','invocationId','sourcePosition','payload','failureCode')
            Require ($sample.schema -is [string] -and $sample.schema -ceq 'nightweaver-unattended-sample-v1')
            Number $sample.sequence 32768
            Require ($sample.sequence -eq $recordNumber)
            Require ($sample.runId -is [string] -and $sample.runId -ceq $manifest.runId)
            Require ($sample.projectId -is [string] -and $sample.projectId -ceq $manifest.projectId)
            Require ($sample.repositoryId -is [string] -and $sample.repositoryId -ceq $manifest.repositoryId)
            Require ($sample.sourceKind -is [string] -and $sample.sourceKind -cin @('runtime','tracker','invocation','cleanup','resource','installation','intervention','attestation','authority','qualification','checks','contract'))
            Text $sample.sourceId
            Object $sample.collection @('startedUtc','endedUtc','startedElapsedMs','endedElapsedMs')
            $collectedStart = Utc $sample.collection.startedUtc
            $collectedEnd = Utc $sample.collection.endedUtc
            Number $sample.collection.startedElapsedMs 31536000000
            Number $sample.collection.endedElapsedMs 31536000000
            Require ($sample.collection.endedElapsedMs -ge $sample.collection.startedElapsedMs)
            if ($manifest.state -cne 'open') { Require ($sample.collection.endedElapsedMs -le $manifest.actualWindow.elapsedMs) }
            Require ($sample.outcome -is [string] -and $sample.outcome -cin @('complete','timeout','missing','invalid','interrupted','limit','error','clock-uncertain'))
            if ($sample.outcome -cne 'clock-uncertain') {
                Require ($collectedEnd -ge $collectedStart)
                if (-not $manifest.clockUncertain) {
                    Require ($collectedStart -ge $actualStart)
                    if ($manifest.state -cne 'open') { Require ($collectedEnd -le $actualEnd) }
                }
            }
            if ($null -ne $sample.sourceObservedUtc) { $null = Utc $sample.sourceObservedUtc }
            if ($null -ne $sample.invocationId) { Text $sample.invocationId }
            if ($null -ne $sample.sourcePosition) {
                Object $sample.sourcePosition @('generation','offset')
                Text $sample.sourcePosition.generation
                Number $sample.sourcePosition.offset 8388608
            }
            if ($null -ne $sample.epoch) {
                Object $sample.epoch @('ownerRunId','runtimeId','processId','processStartedUtc')
                Hex $sample.epoch.ownerRunId 32
                Hex $sample.epoch.runtimeId 32
                Number $sample.epoch.processId 2147483647
                Require ($sample.epoch.processId -gt 0)
                $created = Utc $sample.epoch.processStartedUtc
                $identity = "$($sample.epoch.ownerRunId)/$($sample.epoch.runtimeId)/$($sample.epoch.processId)/$($created.UtcDateTime.ToString('o', [Globalization.CultureInfo]::InvariantCulture))"
                $source = "$($sample.sourceKind)/$($sample.sourceId)"
                if ($epochs.ContainsKey($source) -and $epochs[$source] -cne $identity) {
                    $result.epochTransitionCount++
                    if ($result.epochTransitions.Count -lt 128) { $result.epochTransitions.Add(@{sequence=$recordNumber; sourceId=$sample.sourceId; previous=$epochs[$source]; current=$identity}) }
                    else { $result.detailsTruncated = $true }
                }
                $epochs[$source] = $identity
            }
            if ($sample.outcome -ceq 'complete') {
                Require ($null -ne $sample.payload -and $null -eq $sample.failureCode)
                if ($sample.sourceKind -ceq 'runtime') { Require ($null -ne $sample.epoch) }
                if ($sample.sourceKind -ceq 'invocation') { Require ($null -ne $sample.invocationId -and $null -ne $sample.sourcePosition) }
            } else {
                Text $sample.failureCode 80
                Require ($sample.failureCode -cmatch '^[a-z0-9-]+$')
                Gap $sample.outcome $recordNumber
            }
            if ($null -ne $sample.payload) {
                $maximum = if ($sample.sourceKind -ceq 'invocation') { 65536 } else { 1048576 }
                Payload $sample.payload $maximum
                if ($sample.sourceKind -ceq 'invocation') {
                    if ($null -ne $sample.sourcePosition) { Require ($sample.sourcePosition.offset + $sample.payload.bytes -le 8388608) }
                    # Failed reads can retain bytes without knowing their cursor
                    # or invocation. Charge those bytes to the source's unknown
                    # invocation bucket rather than inventing attribution.
                    $key = [Tuple[string,string]]::new($sample.invocationId, $sample.sourceId)
                    if (-not $invocationBytes.ContainsKey($key)) { $invocationBytes[$key] = 0 }
                    $invocationBytes[$key] += $sample.payload.bytes
                    Require ($invocationBytes[$key] -le 8388608)
                }
            }
            $result.recordCount++
        }
        if ($manifest.state -cne 'open') { Require ($manifest.sampleCount -eq $result.recordCount) }
        if ($result.recordCount -eq 0) { Gap 'no-samples' }
        $result.manifestSha256 = $reader.Sha256('manifest.json')
        $result.samplesSha256 = $reader.Sha256('samples.jsonl')
        $result.integrityValid = $true
    } catch {
        # Return stable diagnostics, not untrusted JSON, filesystem paths or raw
        # exception messages. Callers distinguish invalid input from sample gaps.
        $result.errors.Add(@{code='invalid-archive'; field=$field; record=$recordNumber})
    } finally {
        if ($null -ne $reader) { $reader.Dispose() }
    }
    [pscustomobject]$result
}

Export-ModuleMember -Function Test-UnattendedEvidenceArchive
