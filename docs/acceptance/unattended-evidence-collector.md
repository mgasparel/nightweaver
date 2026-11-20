# Finite unattended evidence collector

`scripts/Capture-UnattendedEvidence.ps1` is an opt-in Windows/PowerShell 7
observer for [contract version 1](unattended-self-build.md). It writes the
[delivered archive format](unattended-evidence-archive.md), then invokes its
independent validator against the stopped archive. `unattendedVerdict` is always
`not-evaluated`. An integrity-valid archive can contain missing, stale, interrupted
or failed observations. This tool does not establish readiness or operate an
unattended workload.

Run it independently from a terminal, for example from an operator-owned launch
script executed with `pwsh -NoProfile -File C:/Evidence/launch-collector.ps1`:

```powershell
# Set these explicitly for the selected release and approved workload.
$start = [datetimeoffset]'2026-12-01T01:00:00Z'
& C:/Source/Nightweaver/scripts/Capture-UnattendedEvidence.ps1 `
  -Installation C:/Nightweaver/installed `
  -Configuration C:/Nightweaver/installed/projects/example.json `
  -Repository C:/Repositories/example -Project example `
  -WorkloadIds @('example-1','example-2','example-3') `
  -Contract C:/Source/Nightweaver/docs/acceptance/unattended-self-build.md `
  -ContractRevision <exact-40-character-contract-commit> `
  -StartUtc $start -CutoffUtc $start.AddHours(8) `
  -EvidenceRoot C:/Evidence -WebUrl http://127.0.0.1:5180/
```

`EvidenceRoot` must already exist, on a local drive outside both the Target
Repository and installation. Every invocation reserves a new unpredictable
directory using native create-new semantics. Existing archives are never reused.
The archive and its sibling `-incomplete` staging directory have protected ACLs
granting access only to the current Windows identity and SYSTEM. Keep those raw
local archives private; export the safe result/validator summaries. Do not attach
raw logs, tracker comments or reference payloads to public Beads/PRs.

For native `-File` invocation, `-WorkloadIds example-1,example-2,example-3` is
also supported. `-StopAfterSeconds 20` is a disposable short-capture option; it
preserves the requested eight-hour metadata and closes as **interrupted**. It is
not an abbreviated acceptance run. Ctrl+C requests cancellation; forcibly killing
the process is an interruption and may leave an open archive or incomplete tail.
Use a new capture afterward; never repair or delete retained evidence in place.

The process ends after closure/inspection. There is no service, scheduled task,
keep-alive, watchdog, retry/resume action, deployment or owner lifecycle control.
Exit 0 means the finite requested capture (including an explicitly short capture)
closed and passed structural inspection. Exit 2 means preflight, source settlement,
storage/limits or inspection could not finish normally. Neither exit means the
workload succeeded. Read individual source outcomes even when exit is zero.

## Identity and read boundary

Preflight requires three distinct safe workload IDs, an eight-hour requested
window, an exact declared contract commit, installed manifest build revision,
configuration hash and native Git common-directory/source-HEAD observations.
Essential observations fail within their deadlines rather than inventing hashes.
The installed revision comes from `nightweaver-deployment.json`; it is never
replaced with source HEAD. The manifest, CLI and Web executable bytes and
configuration hash are sampled again at the endpoint, leaving baseline bytes
unchanged. Configuration is stream-hashed without retaining its contents.

The physical repository ID is SHA-256 over UTF-8 of the Git common directory's
native volume-GUID path, with backslashes changed to `/`, trailing separators
removed and invariant uppercase normalization. The native path is resolved from
the open common-directory `HEAD` file handle. Ancestors are pinned without delete
sharing; reparse points, hard links, alternate streams and device/UNC paths are
rejected. This distinguishes a worktree's source revision, physical repository,
installation, owner/runtime PID+creation identity and the Web envelope epoch.
The declared contract commit and file hash are retained together; an external
readiness packet remains responsible for selecting the approved contract revision.

The finite allowlist consists of:

- Git `rev-parse` for physical common directory and HEAD; no fetch or writes.
- Installation manifest, fixed CLI/Web executable paths, configuration hash,
  `run/<Project>/background/owner.json` and `telemetry/<Project>/factory.json`.
- GET only on the configured loopback HTTP origin's
  `/api/operations/v1/projects/<Project>/resources?key=...`: Factory plus one
  rotating cleanup/selected Workspace key. Redirects, proxy/cookie/default
  credentials and arbitrary URLs are disabled. Full envelopes retain Web epoch,
  revision, supplied times, source health, policy and pending/validation ages.
- Native `bd --readonly --sandbox` inventory, unassigned readiness and selected
  `show --include-comments --json`, rotating serially. The reader scrubs ambient
  Git/Beads routing, suppresses hooks/auto-commit/export/push/metrics and preserves
  required server credentials without logging them. It never configures a journal.
  Embedded bd 1.3.1 backend journal/timestamp effects remain subject to the
  previously accepted exception; logical tracker state must remain unchanged.
- Canonical Project `background-host.log` and `background-web.log`, and selected
  invocation log identities corroborated by fresh matching Factory/owner evidence.
  Explicit `-InvocationLogs @{ 'bead-id/invocation-id' = 'canonical-local-path' }`
  inputs are also accepted. No broad directory or private-home scan occurs.
- Two bounded resource samples at baseline, endpoint and every fifteen minutes:
  PID+creation/parent identity, CPU/IO, private/working-set bytes, handles/threads,
  OS boot, commit/pools/paging/page-file fields. The private resource adapter uses
  built-in Windows PowerShell 5.1 CIM cmdlets, in a collector-owned native Job;
  missing cmdlets, policy denials and failures remain gaps. It does not capture
  command lines or environment/configuration contents.
- Optional `-References` keys `interventionJournal`, `operatorAttestation`,
  `budgetWorksheet`, `runAuthorization`, `qualification`, `checks`, each an
  explicit nonsecret local ordinary file. Absent references stay null. Present
  references are opaque evidence, never inferred authority.

Owner records must match the explicit installation/configuration/Project/Target
Repository and native process creation time. Factory must match that runtime and
Project/repository, support its version, and meet its ten-second heartbeat policy.
HTTP success cannot make an unhealthy/stale/pending-overdue envelope current.
Plain host logs and merged invocation stdout/stderr remain opaque bytes; chunk
boundaries may split text/records. Generations, offsets and hashes survive source
replacement/truncation with explicit gaps. There is no provider-event or GitHub
verdict parser. Native tracker comments and host logs preserve original merge
evidence for later correlation; a SHA in notes is not verified merge proof.

## Deadlines, storage and visibility

Runtime turns are serial, every fifteen seconds, with at most two HTTP requests,
two-second per-read bounds and a five-second turn budget including local identity
reads. Tracker turns have at most two commands per sixty seconds, two seconds
each/four combined. Inventory/readiness/three selected details rotate without
catch-up; a separate sample retains monotonic ages and failures beyond 180 seconds.
More than 45 seconds between accepted owner identities is an explicit gap.
Source policy can be stricter than those outer limits.

Log turns rotate at most four chunks in two seconds; each chunk read has at most
one second. Each chunk is at most 64 KiB, and each source retains at most 8 MiB
across generations (including failed retained bytes). At 128 selected invocation
identities, discovery stops with a limit record. Log churn and output floods can
exhaust coverage; missing bytes are not silently reconstructed. Each resource
capture has five seconds total for two serial reads, each at most two seconds,
with a one-second separation. Near cutoff its remaining budget is clipped and
missing endpoint reads are explicit. Resource captures never overlap. Incident
three-sample/ten-second captures remain distinct, operator-supplied evidence via
explicit references; this collector installs no incident monitor or recovery.

No replacement is admitted while a timed-out operation is unsettled. Native
children start suspended, join a kill-on-close Job before running, and have
bounded stdout/stderr drains. Only that Job is terminated on timeout/output cap.
Source cancellation gets a 300-ms settlement allowance. An unsettled source stops
admissions; it cannot create an accumulating series of replacement subprocesses.
Clock discontinuities preserve monotonic duration and set clock uncertainty.
Collection ends at the original monotonic cutoff even if UTC changes.

Archive admission enforces the delivered 256-MiB total, 16,384 entries, 32-MiB
sample stream, 32,768 records, 2-MiB record, 1-MiB ordinary payload/manifest and
path/component bounds. Payload and record admission reserves 16 KiB, an entry and
a terminal record for closure. A 64-KiB physical staging reserve is released for
terminal diagnostics. Earlier complete records are retained; there is no eviction.
Complete payloads and records are staged outside the archive namespace, then
published to the committed prefix. An append failure rolls back only its own
uncommitted tail. Interrupted staged bytes remain in the private sibling directory.
Disk failure may prevent even reserved diagnostics; report that failure rather
than fabricating successful closure.

Storage calls have one-second observer deadlines. An unsettled writer is never
replaced, closed or inspected concurrently; the result reports
`writerSettled=false` and integrity unavailable. Stop the collector before later
independent snapshot inspection. The shutdown allowance is **ten seconds** beyond
the collection cutoff, including source settlement, terminal publication/closure
and a separately owned five-second validator Job. Exhaustion is an unavailable
inspection, not permission to extend collection. OS-level process termination or
uninterruptible storage can leave only the last committed prefix/staging evidence;
these are gaps and never sustained acceptance.

## Reproduction and cost

```powershell
pwsh -NoProfile -File scripts/tests/UnattendedEvidenceCollector.Tests.ps1
pwsh -NoProfile -File scripts/tests/Qualify-UnattendedEvidenceCollector.ps1 `
  -OutputRoot C:/Evidence -Seconds 20
```

Deterministic fixtures simulate the full eight-hour schedule, interruption,
timeouts, stale identity/health, PID reuse, replacement/chunk caps, cancellation,
uncertain settlement, clock skew, storage/cap failures and read allowlists. Short
real-writer intervals pass the delivered validator; pinned-directory/junction/
hard-link checks preserve source content. They run inside the local measured
suite and Windows CI with positive counts and no skips.

Qualification uses synthetic local installation/owner/HTTP/files plus disposable
Git and native read boundaries, with no Agent or production lifecycle. It records
source hashes before/after, collector CPU/IO counters, read elapsed time, command
counts, actual samples/gaps, finalization, archive hashes and a per-source-kind
eight-hour footprint projection. Projection assumptions are explicit: changing
payload sizes and future invocations can exhaust caps. Its missing tracker and
nonrenewed synthetic heartbeat are intentional gaps. Required production readiness,
real-run qualification, incident diagnosis, semantic evaluation and ns-ke8 remain
separate work.
