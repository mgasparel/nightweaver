# Unattended evidence archive format, version 1

This format implements the archive-format slice of ns-1dv against [acceptance contract version 1](unattended-self-build.md). It stores evidence for later capture and offline adjudication; it is not a workflow store, a collector or an evaluator. Its validator checks schema, boundedness and byte integrity, never whether a source is truthful or an unattended run succeeded.

## Layout and inspection

```text
<unique external run directory>/
  manifest.json
  samples.jsonl
  payloads/                 # optional subdirectories, ordinary files only
```

Use an operator-owned local archive outside the Target Repository and installation. Inspect a stopped snapshot: the Windows PowerShell 7 reader pins every directory component against replacement and opens every enumerated file read-only without write/delete sharing until inspection ends. Reparse points, hardlinks, device entries, UNC roots, alternate streams, path escapes and unknown top-level files are rejected. A live writer can make the archive unreadable; stop/copy collection before retrying inspection, rather than weakening file sharing. The validator never follows a reference outside the archive or opens the declared Target Repository.

```powershell
# JSON result; exit 0 means integrity-valid, exit 2 means invalid/unreadable.
pwsh -NoProfile -File scripts/Test-UnattendedEvidenceArchive.ps1 -ArchiveDirectory C:/Evidence/run-123

# Reusable interface, with no exit/side effects on the caller:
Import-Module ./scripts/UnattendedEvidenceArchive.psm1
$result = Test-UnattendedEvidenceArchive -ArchiveDirectory C:/Evidence/run-123
```

`integrityValid: true` is compatible with timeouts, missing references, interrupted/open snapshots, no samples, and inadequate observation coverage. `unattendedVerdict` is always `not-evaluated`. The result reports explicit observation gaps and epoch transitions, plus hashes of the actual manifest and sample bytes. The `complete` archive state means capture has closed its records, not that the contract is satisfied. Journal/attestation/qualification/authority contents are opaque bytes here; even a present blank journal does not prove no intervention.

The returned `errors` contain stable `code`, `field` and record number, never source JSON or raw exception messages. `observationGapCount` and `epochTransitionCount` are totals; each detail array retains at most 128 entries and sets `detailsTruncated` on overflow. Missing semantic coverage and unrecorded sampling intervals are for the evaluator to assess; the gap list is not a proof of continuous observation.

## Manifest

All keys shown are required. Objects reject unknown or duplicate keys. Strings remain strings, including UTC timestamps; JSON arrays, booleans and numeric strings cannot substitute for scalar types. Identifiers/hashes use lowercase hexadecimal of exactly 32/40/64 characters as shown. UTC strings use `YYYY-MM-DDTHH:mm:ss[.1-7 fractional digits]Z`. Numbers are nonnegative JSON integers, not fractions or coerced text.

The following example is intentionally sparse and contains missing references. Paired with the sample below and `payloads/status.json` containing exactly `{}` (two UTF-8 bytes), it has valid integrity while proving no unattended outcome.

```json
{
  "schema": "nightweaver-unattended-archive-v1",
  "runId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "projectId": "example",
  "repositoryId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "repositoryPath": "C:\\Repositories\\example",
  "contract": {
    "version": 1,
    "revision": "cccccccccccccccccccccccccccccccccccccccc",
    "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
  },
  "installation": {
    "revision": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
    "manifestSha256": "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
    "configurationSha256": "1111111111111111111111111111111111111111111111111111111111111111"
  },
  "workloadIds": ["example-1", "example-2", "example-3"],
  "requestedWindow": {"startUtc": "2026-01-01T00:00:00Z", "endUtc": "2026-01-01T08:00:00Z"},
  "actualWindow": {"startUtc": "2026-01-01T00:00:00Z", "endUtc": "2026-01-01T08:00:00Z", "elapsedMs": 28800000},
  "state": "complete",
  "sampleCount": 1,
  "clockUncertain": false,
  "references": {
    "interventionJournal": null, "operatorAttestation": null, "budgetWorksheet": null,
    "runAuthorization": null, "qualification": null, "checks": null
  }
}
```

- `repositoryId` is the packet's stable SHA-256 identity of the physical Git common directory, retained separately from its display path. `repositoryPath` is declared metadata, not filesystem proof. The collector must obtain and document that identity authoritatively; the validator does not inspect the repository. `projectId` and source IDs are nonempty strings of at most 128 characters; repository paths at most 4,096.
- `contract` binds version, exact source commit and document SHA-256; `installation` binds actual installed revision, package manifest and configuration hashes, not inferred source state. This schema supports contract version 1 only: exactly three unique workload IDs and an eight-hour requested UTC interval. A changed contract requires a supported new schema rather than silent defaults. Workload IDs are at most 128 ASCII letters/digits/dot/underscore/hyphen, starting with a letter/digit.
- `state` is `open`, `complete` or `interrupted`. An open stopped snapshot has null `actualWindow.endUtc`, `actualWindow.elapsedMs` and `sampleCount`. Closed states declare actual endpoint and count, even when interrupted. The actual window is collection coverage, which may include a baseline before the requested workload boundary. UTC order must be valid unless explicitly `clockUncertain`; elapsed time is independent monotonic duration and bounded to one year. Unknown or interrupted timing remains a gap for adjudication.
- Each `references` value is null or a payload reference below. Null is an explicit observation gap, never implicit authority. Raw nonsecret reference payloads preserve the selected contract's journal/attestation, finite progress worksheet, applicable run authorization, qualification and required-check evidence. The format validates presence/bytes, not their meaning or sufficiency. Further selected installation, resource and workflow evidence is carried in samples.

## Sample records

`samples.jsonl` is strict UTF-8 without a BOM: one complete JSON object per line, LF or CRLF, with a final newline and no blank records. A missing final newline/partial JSON/invalid UTF-8 is invalid, not an ignored tail. Each record has exactly the keys below, including explicit nulls. `sequence` is contiguous from 1; missing collection turns should be represented by failure records rather than silently deleting sequence numbers. A closed manifest's `sampleCount` must match actual records.

```json
{
  "schema": "nightweaver-unattended-sample-v1",
  "sequence": 1,
  "runId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "projectId": "example",
  "repositoryId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "sourceKind": "tracker",
  "sourceId": "inventory",
  "collection": {"startedUtc": "2026-01-01T00:00:00Z", "endedUtc": "2026-01-01T00:00:01Z", "startedElapsedMs": 0, "endedElapsedMs": 1000},
  "sourceObservedUtc": null,
  "outcome": "complete",
  "epoch": null,
  "invocationId": null,
  "sourcePosition": null,
  "payload": {"path": "payloads/status.json", "bytes": 2, "sha256": "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"},
  "failureCode": null
}
```

Store this object on one line in JSONL. `runId`, `projectId` and `repositoryId` must exactly match the manifest. Source kinds are `runtime`, `tracker`, `invocation`, `cleanup`, `resource`, `installation`, `intervention`, `attestation`, `authority`, `qualification`, `checks`, `contract`. Source IDs distinguish sources within a kind; they are not paths to open.

`collection` records the observer's actual interval, separate from nullable **supplied** `sourceObservedUtc`. Stale source times are retained, not replaced by receipt time. Per-record elapsed bounds increase within the interval and cannot exceed a closed manifest's duration. UTC collection bounds must fit the declared actual window when its clock is known; `clock-uncertain` samples preserve discontinuities explicitly. Source freshness/age policy and acceptance deadlines are interpreted only by the later evaluator, using retained payloads and the contract.

Outcomes are `complete`, `timeout`, `missing`, `invalid`, `interrupted`, `limit`, `error`, `clock-uncertain`. Complete records require a payload and null `failureCode`. Other outcomes require a nonempty lowercase letter/digit/hyphen diagnostic code of at most 80 characters and may retain bounded partial bytes as payload; they are gaps even when their bytes/hash are valid. Codes should be allowlisted, not stderr or secret-bearing text. Failed sources can omit unavailable identity metadata.

A nonnull `epoch` contains exactly `ownerRunId`, `runtimeId` (32 hex), `processId` (1–2,147,483,647), `processStartedUtc` (UTC). Complete runtime samples require an epoch. The validator reports identity changes for the same source by owner/runtime/PID/creation time; equivalent UTC precision does not manufacture a change. Epochs are asserted observations, not verified liveness or proof that two owners never overlapped.

Complete invocation records additionally require `invocationId` (nonempty, at most 128 characters) and `sourcePosition: {"generation": "file-identity", "offset": 0}`. Generation is an opaque bounded identity supplied by capture, not a filename heuristic; offset is the byte start of the retained chunk. Preserve generations/offsets and record gaps on rotation/truncation. Parsing provider events, assembling replay or deciding whether records are duplicates belongs to later adapters/evaluation, not this validator.

Failed invocation reads may retain bounded partial bytes with null invocation ID or position. They remain gaps, not attributable invocation output. Their bytes still count toward the 8-MiB source budget: a missing invocation ID uses the source's unknown-invocation group, and a missing position never fabricates a cursor. Known positions must fit the offset bound even on failed reads.

## Payloads and bounds

A payload reference has exactly `path`, `bytes`, `sha256`. Paths are relative under `payloads/`, use `/`, ASCII letters/digits/dot/underscore/hyphen, at most 240 characters; empty/dot/parent segments, trailing dots, reserved Windows device names, backslashes, colons and rooted paths are invalid. Bytes must match the pinned ordinary file's length and lowercase SHA-256. There is no external payload-file reference: an unavailable larger retained source can be described by a bounded allowlisted metadata payload without reading or exposing its external path. Raw payload contents remain opaque and local.

| Bound | Value |
| --- | --- |
| Entire archive, all enumerated file bytes | 256 MiB |
| Archive entries / pinned directory components including ancestors | 16,384 / 128 |
| Manifest / sample stream | 1 MiB / 32 MiB |
| Individual JSONL record / total records | 2 MiB / 32,768 |
| Ordinary payload / invocation chunk | 1 MiB / 64 KiB |
| Captured invocation bytes, per invocation ID and source ID across generations | 8 MiB; each offset + chunk also fits 8 MiB |
| Root path / relative payload path | 4,096 / 240 characters |

Inventory size and per-file bounds (including unreferenced payloads) are checked before payload hashing, and streams/JSON parsing are bounded. Each file remains read-locked, so repeated references reuse a hash of the same pinned bytes, not a mutable path. Inspection releases only its own handles; it never rewrites, repairs, truncates or removes evidence. Unsupported JSON versions/fields, noninteger metadata, duplicate properties, identity/count/order errors, missing/mismatched payloads and limit violations make integrity false. A low-level unreadable archive is likewise invalid; original source bytes remain available for diagnosis.

The entry bound accommodates eight hours of the contract's runtime/tracker samples plus log chunks; capture should still estimate records, files and bytes in its readiness qualification. Identical payload bytes may reuse a reference while each sample retains its own times/outcome. Reuse does not erase gaps, and a forecast exceeding any cap makes the proposed capture strategy not ready.

## Verification and later slices

```powershell
pwsh -NoProfile -File scripts/tests/UnattendedEvidenceArchive.Tests.ps1
```

The synthetic fixture command retains its evidence under a unique temporary directory (or explicit `-OutputRoot`), reports a positive passed count and fails on an assertion. It covers incomplete snapshots, source errors, epoch/PID reuse, type/version/path/hash/truncation/size boundaries, Windows junction/hardlink rejection and held-read/parent-directory protections. It asserts source preservation on valid and invalid inspection. These checks run in normal local measured verification and Windows CI; the new fixture time is included in suite wall time rather than hidden outside it.

Archive validity is the handoff to capture/adjudication, not acceptance. The capture slice must supply truthful source identities, allowed reads, cadence, storage and settlement; adjudication must correlate actual records and source formats against the selected contract. Neither task may infer authority, live progress, merge success or no intervention from this format's structural validity.
