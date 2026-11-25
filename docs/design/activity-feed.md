# Activity evidence contract v1

Activity shows local Project observations. Beads remains the lifecycle authority.
No Activity record permits scheduling, Retry, Resume, claim recovery, a stage or
pause change, or a repair-budget change. A consumer must use the existing guarded
command path. This contract adds a pure model in `Activity/ActivityModel.cs`.
It adds no active writer, producer hook, route, file scan, or browser behavior.

The dependent [Activity journal module](activity-journal.md) now implements
bounded storage and shared reads. It remains separate from runtime producers
and console routes.

One runtime-owned Project journal will supply Web. It must work with OTLP export
disabled. Keep it under the Nightweaver Project runtime root, outside the Target
Repository. Do not parse runtime logs or scan transcripts to reconstruct events.
Keep raw diagnostics and runtime data. Catch-Up remains the summary after absence.
Per-Agent transcripts and existing diagnostic detail remain the detail sources.

## Evidence and messages

The projector accepts typed evidence from trusted source adapters. Source names
are provenance assertions by those adapters, not authentication of arbitrary input.
Do not expose this input as a write API. A producer must call the projector only
after the stated source boundary succeeds. A claim, stage label, Agent report,
invalidation hint or process exit cannot be mapped to a stronger source.

Every record includes a source, source reference and strength. The message is a
fixed template, independent of all supplied text. The phase gives context without
changing the strength. An optional closed role identifies Planner, Plan Challenger,
Implementer, Reviewer, QA or Merger. Health reason codes distinguish starting,
running, draining, stopped, failed, unhealthy, recovered and unknown owner.
The following table lists all event kinds and source rules.

| Kind | Source and strength | Required boundary and meaning |
| --- | --- | --- |
| `InvocationStarted` | Invocation, observed | Native launch acknowledged for the exact Bead and invocation. Reservation or preparation does not prove launch. |
| `InvocationCompleted` | Invocation, observed | Exact invocation settled. Its result is not an approval or merge. |
| `PlanningApproved` | Publication, confirmed | Confirmed Challenger approval publication, bound to the approved candidate and current grounding. |
| `PlanningPublished` | Publication, confirmed | `PlanningPublisher` verifies final planned state and the complete immutable graph with actual Beads-allocated IDs. A label or child count is insufficient. |
| `DeliveryApproved` | Publication, confirmed | `DeliveryOutcomePublisher` returns the confirmed exact role transition receipt or its exact confirming reread. |
| `ChangesRequired` | Publication, confirmed | Confirmed semantic repair handoff. Operational retry and classifier diagnostics are separate. |
| `QaAdmitted` | Admission, confirmed | Fresh guarded QA admission for this Bead. Approval for QA and actual QA admission are separate. |
| `MergeVerified` | MergeVerifier, verified | Existing independent live merge verification, or the existing historical proof contract, accepts exact PR, checked head, merge commit, checks and source identity. |
| `BeadClosed` | Publication, confirmed | Exact guarded closure response or confirming reread. Closure alone does not prove merge. |
| `StateObserved` | Inventory, observed | Successful inventory shows current state. Startup uses this kind, not invented historical transitions. |
| `PauseObserved` | Inventory, observed | Pause labels appear in the successful inventory. Reason distinguishes needs-human, product decision and combined pause. |
| `PauseRemovalObserved` | Inventory, observed | Inventory no longer contains the prior pause labels. This does not prove console Resume. |
| `ResumeConfirmed` | Attention, confirmed | Persisted guarded Resume or Retry succeeds. A note, comment or audit record alone is insufficient. |
| `RecoveryStarted` | Recovery, observed | Exact background recovery attempt starts. |
| `RecoveryCompleted` | Recovery, confirmed | Identity-bound recovery completion is confirmed. No Beads recovery authority follows. |
| `RecoveryFailed` | Recovery, observed | Exact recovery attempt fails. |
| `HealthChanged` | Health, observed | Current identity-bound owner/control/Web/runtime observation changes. Missing evidence is not healthy. |
| `WaitStarted`, `WaitChanged`, `WaitEnded` | Pool, observed | Existing fresh admission observation changes eligible capacity wait or Planning backpressure. No new tracker read is permitted. An ended interval is not total Bead lead time. |
| `CleanupFailed` | Recovery, observed | Environment cleanup fails independently of successful merge verification or closure. |
| `EvidenceUnavailable` | Journal, unknown | Evidence cannot establish a fact. A failed stronger projection preserves its original source and uses this kind with an explicit reason. |
| `RecordsLost` | Journal, observed | Queue, persistence or delivery loses records. Count is optional; unknown loss is never zero. |
| `RuntimeReplaced` | Journal, observed | A new runtime identity is observed. Earlier activity can be missing. |
| `HistoryGap` | Journal, observed | Retention, partial tail, cursor expiry or client eviction removes continuity. |

`ActivityProjector.Rule` contains the exact fixed message for each kind.
Confirmed and verified facts require fresh source evidence. Failed, missing or
stale source evidence produces unknown evidence, never success. Old source time
alone does not invalidate a newly verified historical fact. Freshness describes
the source read, not the historical event's age.

`ActivityMergeProof` contains two normalized `MergerFacts` values. The source
adapter must first perform the live `MergerOutcomeVerifier` checks or the
historical `DigestMergeEvidence` checks. The historical path requires the closed
task's exact `nightweaver-verified-merge-v1` record and its unique referenced
`nightweaver-merge-v1` report. Reject foreign, malformed, conflicting or missing
records. Normalize the agreed fields and both reference identities on each side.
The projector checks equality, Bead binding, checks-passed, hashes, distinct report
and verification references, and the Project's PR repository. It does not query
GitHub, perform hosted checks, or replace either verifier. Missing or conflicting
proof becomes unknown. Cleanup failure cannot revoke or establish merge evidence.

Preserve the Planning creation intent, publication holds, verified actual task
IDs and final parent publication barrier. Preserve pause handoff and Merger BOM
and terminal-line-ending checks. These safeguards exist at retained revision
`e5ce0e0d4dcee649a3caabb532b1746ea6de9291`, after supplied default revision
`10777a72243a3d1e9a9801f4ac310c9e05fa3328`. Startup revision
`2883363e7b318ee156398593e9939cdff2077dba` also refuses oversized Planning
classifier context before launch. Such a pause cannot create an invocation event.
This module does not change those paths.

## Identity, order and suppression

`ActivityPosition` contains journal ID, journal offset, runtime ID and runtime
sequence. IDs are nonempty GUIDs. Offsets and sequences are positive Int64 values.
The writer allocates strictly increasing journal offsets and runtime sequences.
Do not use wall time for identity or order. Do not wrap counters; replace the
journal with a visible gap before exhaustion. A replacement runtime starts a new
runtime sequence. A retained journal keeps its offset order across runtimes.

Keep `sourceTime` nullable and separate from `observedAt`. Missing source time
stays unknown. Display late source time as late without moving an open row.
UTC clock changes cannot reorder replay or calculate source freshness.

Each distinct causal occurrence has a GUID `cause`. A confirmed publication and
its later inventory observation share this value only when the adapter can bind
the inventory to that exact publication. The suppression key includes runtime, cause,
Bead, phase and invocation. Do not use Bead plus stage alone. Initial inventory
without causal proof uses a new cause and remains observed. Each invocation and
repair visit has its own cause. Distinct semantic kinds remain separate, even
with the same cause; `StateObserved` can coalesce with its proven publication.
The adapter must set `observedPublication` on that inventory record to the exact
publication kind: `PlanningApproved`, `PlanningPublished`, `DeliveryApproved`,
`ChangesRequired` or `BeadClosed`. The shared causal identity must bind that
publication. An absent binding keeps inventory separate. Other event kinds cannot
set this field. Invocation, admission, merge, recovery, wait, health and loss facts
remain separate from inventory. Two different publication kinds remain separate,
even when they share a cause. An inventory binding grants no confirmation strength.

The bounded presentation fold retains the first row position as its anchor.
Stronger evidence updates that row in place. Weaker inventory cannot downgrade
it. Journal cursor progress is independent of the last displayed row. Exact
runtime/sequence duplicates are suppressed; conflicting content for that identity
is rejected. Conflicting facts for one causal occurrence are also rejected.
A cursor older than the finite duplicate window requires a history
reset, not silently reordered rows. A different journal requires an explicit
history reset and visible gap. A new runtime can report initial state, but cannot
infer past starts, approvals, Resume, or complete wait intervals.

## Safe schema and detail access

Records contain schema version 1, fixed enums/templates, GUIDs, two timestamps,
bounded identifiers, optional bounded integer facts, a typed detail category,
and optional accepted merge facts. Bead, invocation and source-reference IDs
contain at most 128 ASCII characters. They start with a letter or digit and
contain only letters, digits, `.`, `_` and `-`. Reject overlong values rather than
truncate identity. Reject Unicode, including unmatched surrogates, combining
characters, bidi controls and emoji in identifiers. There is no user title or
free-text field in this contract. Numeric facts range from 0 to 1,000,000,000.
Unknown numeric facts are null. Do not substitute zero. Source references are
identities, not filesystem paths or URLs.

The only external link is a canonical HTTPS PR URL of at most 512 ASCII
characters in the configured Project repository. Reject credentials, query,
fragment, port, backslash, escaped path, foreign repository and normalized path
tricks. Checked head and merge commit are exactly 40 hexadecimal characters.
Use typed detail category plus validated identity to resolve existing local
detail routes. A reference does not bypass transcript or diagnostic access rules.

Never copy exception, prompt, guidance, command-line, environment, configuration
or transcript text. A valid-looking identifier is not a secret scanner: source
adapters must take identifiers from their named identity fields, never encode
free text into them. Safe reason codes are the closed `ActivityReason` enum.
Use `Unsupported` for an unsupported detail and link to existing evidence when
available. Do not copy the original reason text. Unknown enum values are rejected.
Serialized UTF-8 records, including all fields, must fit 4096 bytes.

## Finite budgets and failure behavior

These are final v1 limits for dependent journal, API, producer and console tasks.
The pure model defines constants and projection/replay checks. The later tasks
must enforce storage, deadlines and transport limits at their own boundaries.

| Boundary | Limit and required behavior |
| --- | --- |
| Producer queue | 512 records. Admission is nonblocking. Drop new entries at capacity. Maintain a saturating loss count outside the queue and emit `RecordsLost` when writing resumes. |
| Record | 4096 UTF-8 bytes including schema and newline framing. Reject oversized records. No truncation of identity or secret-bearing fallback text. |
| Storage | At most eight 1 MiB segments, including the active segment, and at most 4096 records. Total journal bytes are at most 8 MiB. Segment rotation accounts for framing before append. Evict oldest whole segments until both bounds hold. |
| Age | Size/count retention only. No minimum or maximum duration is promised. Quiet history can remain indefinitely. |
| Metadata | One Project journal manifest, at most 16 KiB. One runtime writer lock. At most one active temporary manifest, at most 16 KiB. Total observation storage is at most 8 MiB plus 32 KiB metadata. No other log cleanup is authorized. |
| Startup recovery | Scan at most eight segments, 8 MiB and 4096 records. A partial final record is ignored and marked `HistoryGap` with `PartialTail`. Never salvage it as a complete fact. Corrupt middle data makes that segment unavailable and creates a gap. |
| Read/replay turn | At most 100 records returned and 256 KiB examined/decoded. Never scan the full retained journal per poll. Stop before either bound. Return the last examined offset as continuation even if filters match no rows. |
| Streaming | Same 100-record/256-KiB batch limits. One pending batch per viewer; no unbounded per-viewer queue. Refresh at most once per two seconds. A write stalled for two seconds ends that viewer's stream. The runtime writer never waits for a viewer. |
| Views | At most 16 active streams per Web host and one per browser view. Refuse excess streams with a finite retry response. Disconnected viewers hold no delivery queue. |
| Filters | Closed kind, phase and strength enums plus one validated Bead ID. At most 25 kind values, five phase values and four strength values. No regular expression or arbitrary predicate. Bound request/cursor text to 1024 bytes. |
| Cursor | Version, journal identity and last examined offset. No path or source text. An expired cursor returns a visible gap and the oldest available continuation; runtime replacement remains independently visible. |
| Client | At most 500 rendered records plus 500 fixed-size identity/SHA256 duplicate receipts. Evict the oldest row with visible `ClientEviction` gap and a retained eviction count. Do not retain hidden unbounded rows when filters change. |
| Suppression | At most 500 presentation causal keys, derived from retained rows. No permanent dedup ledger. Duplicate receipts also have a 500-entry ceiling. Loss or eviction can end suppression coverage. Never claim exactly-once accounting. |
| Persistence failure | Disk denial, full disk or unavailable path drops observations without delaying factory work. Keep one bounded failure state and saturating count, not repeated exception text. Report unknown loss if count is unavailable. Recovery emits explicit loss/gap evidence before ordinary progress. |

A client can be connected while source evidence is stale or unavailable. A
disconnected client can retain fresh data until its monotonic age expires.
Track connection, freshness, runtime replacement and history continuity separately.
Successful reads are fresh for at most 60 monotonic seconds. A failed source read
is unavailable immediately. Before the first success, freshness is unknown.
Retained records keep their original source-time and freshness evidence; current
source status must not rewrite historical facts. Wait duration follows existing
continuous same-runtime coverage rules. Gaps never create a completed interval.

## Verification and ownership

`ActivityModelTests` exercises every kind, evidence strength, unavailable and
conflicting proof, unsafe URLs, malformed enums/fields, Unicode and byte bounds,
secret sentinels, duplicate inputs, late time, clock changes, runtime replacement,
bounded replay and eviction. Detailed cases use memory only. No new native
journey, fixture publication, tracker command or active production work is added.
The full local measurement still includes existing builds, fixture publication,
six-thread execution and teardown. Storage and live browser proof belong to the
dependent tasks once those boundaries exist.
