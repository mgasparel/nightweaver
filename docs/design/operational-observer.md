## Shared base contract amendment (ns-gjwx)

[Wire amendment v1.3](operational-observations-v1.md#amendment-v13--shared-base-and-independent-sections-ns-gjwx)
defines additive base and section resources for all five Studio surfaces. It is
the implementation contract for subsequent ns-gjwx slices, not a claim of shipped
support. The retained implementation described below still assembles full
responses. Those existing keys and legacy routes keep their complete-response
rules throughout migration.

For new keys, a complete shared inventory supplies compact base facts and indexes
without waiting for optional history, relations, prose, environment or merge
evidence. Cold direct links join shared bootstrap; no targeted base lookup is
allowed. Only a complete successful current shared validation proves absence.
Independent sections retain their own source health, dirty generations, complete
validation and confirming inventory identity. Current membership and action
eligibility override retained historical detail. Cached values never authorize
guarded writes.

Bounded recent sections reuse the existing total byte/key/history capacity and
interest grace/TTL. This is the explicit exception to v1.2 deselection retirement;
it adds no allowance or inactive audit. Shared reads and fair continuation keep
selected detail, inventory refresh and guarded writes moving within the unchanged
native bounds. See the amendment for normative states, keys and scheduling, and
[shared base acceptance](../acceptance/shared-base-read.md) for source/record
traceability and equivalent cold, warm and active-factory measurements.

## Event-driven scheduling amendment (ns-sib.1)

Cold selected Workspaces coalesce already-due detail, comments and incoming
relations in a read-only `show --include-comments --include-dependents --json`.
This uses the supported local embedded backend; other backend modes remain
refused by the existing mutation-free inspection boundary. The pinned Beads
1.3.1 c1c4b642a producer propagates requested comment/dependent iterator errors.
Its embedded dependent iterator delegates to the same metadata read as native
incoming relations, including durable and ephemeral owners. Both native incoming
and show resolve only owners in the selected database; this establishes no
cross-database inventory completeness.

One bounded `version --json` discovery per collector qualifies the exact version,
build and full commit above. Only this producer's requested streams are reused;
successful iterator completion makes its omitted collections empty. This is a
targeted-read contract, not an inventory completeness claim. Unknown versions or
failed discovery retain native comments/incoming fallback, even when arrays are
present. A retained comment source state alone cannot complete a Workspace
history prefetch. If discovery exhausts the turn before a comment value is read,
the cycle retains its comment substep for a later bounded native read. This also
applies when an expired Attention history remains selected as a Workspace.
The discovery shares the same native/sweep budgets and is not retried
per sweep. Aggregate counts cannot establish emptiness or completeness.
Unsupported flags, malformed streams, read failures and payload limits retain
ordinary bounded reads and honest
health. Outgoing relations always use native `dep list --direction down` because
show's outgoing hydration is best effort. No inventory-derived edge shortcut is
used. Each coalesced source captures its own generation before the native read;
journal hints arriving during it remain pending. Final inventory identity
confirmation, memory/payload bounds and mutation priority remain unchanged.
Warm refreshes do not request combined streams. The finite-contention regression
is an injected latency model, not proof of the historical hosted timeout's cause.

The v1.2 contract amendment in `operational-observations-v1.md` supersedes the
original one-second tracker schedule below. `ConsoleJournalReader` binds public
bounded batches to Git/Dolt branch and host/repository identity and shares the
observer gate. `TrackerSourceSchedule` owns bounded source generations and
independent audit deadlines; `OperationalObservationSource.ReadScheduledAsync`
reuses clean authoritative values and drains unfinished cohorts. Cohort source
reads receive a reserved continuation turn if core reads exhaust the previous
turn's budget. This applies to scheduled mixed interests as well as Workspace-only
collections, so continuous core invalidation cannot starve history audits. The
captured inventory is reused for at most one consecutive turn; its provenance is
retained and any outstanding generation still marks the projection pending.
Cohort source generations must still be current at publication. Resource pending generations
remain owned by `OperationalObservationService` until accepted-body publication.
Shared memory reservations cover source values, histories and projections.
Workspace confirming inventory remains authoritative after native detail reads;
a confirming read is a correctness guard, not an idle polling sweep. Environment
inputs retain a separate two-second fallback. Journal failures leave audits active
and carry an independent actionable diagnostic. The Orchestrator still uses its
unchanged sequence-only projection and thirty-second cadence.

# Shared operational observations

## Independent section collection (ns-gjwx-p52d0161f682b3122f1e2)

The additive v1.3 section keys reuse this Project collector and its source
generations. Section reads do not build a full Workspace or wait for an Attention
cohort. One selected Bead advances per turn. Selected sections and background
work alternate priority. Background Attention and Digest also rotate priority
when both have interest. Core validation still runs between at most one reserved
detail continuation. The native command, history and turn limits remain unchanged.

Compatible sections share one complete inventory confirmation after their native
reads. Source order prevents an earlier inventory from confirming later detail.
Bounded identity fingerprints bind source values and pending sections to the
exact Bead, workflow and update time. Changed identity requires new source work.
Raced generations remain pending. Section publication uses the same captured
resource-entry identity as base publication. Old callbacks cannot fill a
readmitted resource. Current base membership and actions remain authoritative.

History, Attention and merge parsing share complete comments. Cached histories
retain their parser qualification. A prior Attention-only read cannot certify
a strict section or Workspace history. The first strict consumer upgrades that
source with the existing strict parser. Concurrent selected consumers share it.
Recent source
values retain at most 64 idle source entries for 60 seconds from last interest.
Idle sources have no audit slot. Histories retain the 64-entry/16-MiB limit and
evict the oldest unselected history under entry or byte pressure. Selected and
Attention histories cannot be silently evicted. Raw values, histories, base
indexes and resource bodies share the 32-MiB budget. Failed growth retains the
accepted value and source clocks. Section failure and age mark that value stale.
Host shutdown waits for collection settlement before releasing source reservations.

The two-Workspace identity limit includes section keys for the same Bead.
Text, history, relations, environment and merge descriptors keep independent
source health. Merge evidence remains unavailable without the exact matching
host records. Digest retains its 128-task acquisition cap. Legacy full-response
consumers remain supported while the later surface slices adopt section keys.

Optional sanitized collector latency, accepted-body age and health export is
specified in [Web observation telemetry](web-observation-telemetry.md). It runs
at shared collector/aging boundaries and never establishes workflow authority.

The wire contract is [Operational observations v1, amendment v1.1](operational-observations-v1.md). `ns-mbd.2` implements the shared backend and read/refresh routes. `ns-mbd.3` owns SSE transport and browser coordination. Existing console routes retain their payloads and guarded commands.

`Program` registers one `OperationalObservationService` and source adapter for the configured Project. `BackgroundWebHost` launches that same Web executable, so every actual host has its own opaque epoch and observation lifecycle. Runtime replacement within that host does not replace its epoch. Revisions use a single checked unsigned 64-bit counter: admission, semantic changes and health changes allocate positive decimal revisions; clocks alone do not. Exhaustion stops admission and requests host shutdown. Resources are serialized into immutable JSON before publication; old asynchronous results must still match their original cache-entry identity. No eviction watermark map is retained.

## Collection and authority

The tracker loop targets one-second start-to-start collection. One inventory feeds Explorer filters, Digest and Attention projections; ready and blocked membership remain separate native observations. Selected Workspace collection preserves inventory/detail/relations/comments/confirming-inventory workflow identity checks. A transient mismatch is a retryable conflict; confirmed absence clears the resource. Relation failures preserve the legacy directional partial states and independently observed Workspace data with explicit stale source/envelope health. Other failed collections retain the complete last good projection. Initial unavailable data stays null.

Ready and Blocked membership commands run only while their respective Explorer
filters are subscribed. Digest, Attention, Workspace and All inventory views do
not spend the shared native-work budget on unused memberships. When both filters
are selected their existing rotating priority and independent health remain.

Tracker source and console journal access release after each observer command.
Journal identity validation, optional setup and the bounded page each retain a
two-second command deadline after admission, sharing a four-second native-work
budget for the turn. Gate waits consume neither budget. Preparation cannot consume
the page's entire deadline merely because several individually valid commands
together exceed two seconds. Successful setup survives a later page timeout;
session cursor reset remains obligatory until a valid page is returned.
Waiting guarded operations take priority, with one due observer admitted after eight consecutive guarded operations. Guarded mutations keep uninterrupted fresh reads, expected-workflow checks and confirming reads. Successful and ambiguous outcomes request reconciliation only after releasing the gate. Checkpoint and cleanup POST completion also requests reconciliation, including uncertain failures. Cached observations never authorize writes.

Each tracker process has a two-second timeout and bounded stdout/stderr while receiving. Inventory and membership accept at most 8 MiB and 10,000 rows; list/ready request the 10,001-row sentinel. Detail/comments/relations accept at most 2 MiB; existing comment parsing additionally limits histories to 2,048 entries. Processes are killed and settled before their gate is released. No collection retry is hidden inside a sweep.

Tracker sweeps reuse the repository validator's successful literal default-branch
syntax check. Repository identity and tracker presence are still checked on every
sweep. Invalid names and contextual Git expressions are never memoized. Charging
both Git and Beads command cost in the controlled-clock Workspace regression
prevents repeated immutable syntax checks consuming the detail budget; source,
sweep and native-smoke deadlines are unchanged.

A sweep yields after four seconds, allowing the current bounded command to settle. Ready and blocked rotate priority when the budget cannot fit both. A core sweep that leaves no detail turn yields the following sweep to pending detail work before restarting core collection. Workspace substeps resume with their original identity and a confirming inventory. At most one selected Workspace advances per sweep, rotating between two selections. Up to four comment commands run per sweep, including selected Workspace comments. Attention captures a bounded cohort and rotates its histories, sharing selected comments from the same collection. It publishes a complete projection only when all required histories have been observed; a cohort over 64 fails explicitly. Backlogs become stale rather than claiming normal freshness. Source and aggregate attempt/success clocks remain separate, and `nextAttemptAt` records the next scheduled attempt, not a successful completion promise.

Runtime and cleanup sample independently every two seconds without taking the tracker gate. Factory data preserves the legacy snake-case snapshot. Missing/corrupt/stale telemetry is an observation failure; a validated stopped runtime remains a known stopped observation. Digest `generatedAt` is captured at whole-second precision before inventory collection. A strict checkpoint observation distinguishes corrupt bookmarks from absence; legacy bookmark reads retain their prior forgiving behavior. Checkpoint changes reproject Digest even when tracker content is unchanged.

All timers, age calculations and source budgets use the injected `TimeProvider`. HTTP cancellation releases that caller only; the host owns collector cancellation and process settlement. Stop rejects new admission, cancels collection and awaits collectors. The freshness task marks overdue data stale even while a source is still running.

The four-second sweep allowance counts native command execution, excluding gate
waits and projection CPU work. Detail-capable turns alternate Workspace and
Attention priority; turns exhausted by core collection do not advance that
alternation. A transient history timeout retains completed reads within the
bounded cohort, and inactive Attention releases an interrupted cohort's history
reservations. Shared selected comments use strict Workspace parsing whichever
projection reads them first. At most four actual comment commands run per sweep.

The observer retains its repository validator across sweeps. A successful literal
default-branch syntax check is reusable; contextual expansions and invalid branch
names are checked again. Repository existence, Git identity and tracker metadata
are validated on every sweep. This avoids spending repeated native process time
on immutable syntax while Attention histories await their bounded turns. The
two-second command and four-second sweep budgets remain unchanged.

A Workspace source timeout retains its completed substeps and resumes the failed
substep on the next sweep, with the original workflow identity and attempt time.
It publishes the timeout with no partial Workspace data while waiting. A successful
retry still requires the confirming inventory; a workflow conflict or non-timeout
required-source error discards the cycle. Existing partial relation/environment
states remain explicit. This avoids replaying successful detail, relation and
history commands after each transient timeout without adding retries inside a
sweep or extending the native command, sweep or observation deadlines.

## Finite interest and cache

GET renews interest for 30 seconds. A stream holds pins and receives the same grace after disconnect. Unpinned entries expire after another 30 seconds and are LRU eviction candidates. Admission supports at most 64 keys and two active Workspaces. A single atomic memory budget caps serialized projections plus separate cached histories at 32 MiB; histories also have a 64-entry/16-MiB cap. Inventory collection is separately capped at 8 MiB. Source history reservations and resource admission cannot race past the shared byte ceiling. Failed growth retains prior data with a source-limit error. Idle source caches are released without issuing tracker commands.

The internal SSE seam is `Subscribe(keys, listener)`: it atomically admits/pins keys and registers notifications before sampling its returned `ObservationInterest.Versions`. The listener receives key/revision changes; disposing the interest unregisters it and releases its pins into grace. Consumers read `Epoch` and `ProjectId` from this host owner. `Read(key)` only reads/renews cached interest; `RequestRefresh(keys)` coalesces collection. The transport must implement the separate connection, queue, timeout and resync limits in v1.1; callbacks must enqueue bounded work and return promptly.

## Verification

Fast service/source tests cover shared readers, external and comments-only changes, membership changes, failure retention, partial relations, identity conflict/deletion, unchanged observations, checkpoint/domain-clock equality, age during a blocked read, eviction/readmission ordering, shared byte capacity, atomic admission, subscription lifetime, mutation fairness, slow-source substep progress and shutdown. One real-bd smoke observes comments and dependencies through HTTP; a process-runner boundary verifies output-limit termination. Existing Workspace boundary and Factory telemetry integration cases remain intact.

Actual timings and live isolated-stack evidence are recorded in [operational observer acceptance](../acceptance/operational-observer.md). The one-second and two-second schedules are targets, not transactional or maximum-latency guarantees. Large Attention cohorts, slow commands and mutation storms can exceed them and must expose stale/budget metadata.
