## Event-driven scheduling amendment (ns-sib.1)

The v1.2 contract amendment in `operational-observations-v1.md` supersedes the
original one-second tracker schedule below. `ConsoleJournalReader` binds public
bounded batches to Git/Dolt branch and host/repository identity and shares the
observer gate. `TrackerSourceSchedule` owns bounded source generations and
independent audit deadlines; `OperationalObservationSource.ReadScheduledAsync`
reuses clean authoritative values and drains unfinished cohorts. Cohort source
generations must still be current at publication. Resource pending generations
remain owned by `OperationalObservationService` until accepted-body publication.
Shared memory reservations cover source values, histories and projections.
Workspace confirming inventory remains authoritative after native detail reads;
a confirming read is a correctness guard, not an idle polling sweep. Environment
inputs retain a separate two-second fallback. Journal failures leave audits active
and carry an independent actionable diagnostic. The Orchestrator still uses its
unchanged sequence-only projection and thirty-second cadence.

# Shared operational observations

The wire contract is [Operational observations v1, amendment v1.1](operational-observations-v1.md). `ns-mbd.2` implements the shared backend and read/refresh routes. `ns-mbd.3` owns SSE transport and browser coordination. Existing console routes retain their payloads and guarded commands.

`Program` registers one `OperationalObservationService` and source adapter for the configured Project. `BackgroundWebHost` launches that same Web executable, so every actual host has its own opaque epoch and observation lifecycle. Runtime replacement within that host does not replace its epoch. Revisions use a single checked unsigned 64-bit counter: admission, semantic changes and health changes allocate positive decimal revisions; clocks alone do not. Exhaustion stops admission and requests host shutdown. Resources are serialized into immutable JSON before publication; old asynchronous results must still match their original cache-entry identity. No eviction watermark map is retained.

## Collection and authority

The tracker loop targets one-second start-to-start collection. One inventory feeds Explorer filters, Digest and Attention projections; ready and blocked membership remain separate native observations. Selected Workspace collection preserves inventory/detail/relations/comments/confirming-inventory workflow identity checks. A transient mismatch is a retryable conflict; confirmed absence clears the resource. Relation failures preserve the legacy directional partial states and independently observed Workspace data with explicit stale source/envelope health. Other failed collections retain the complete last good projection. Initial unavailable data stays null.

Ready and Blocked membership commands run only while their respective Explorer
filters are subscribed. Digest, Attention, Workspace and All inventory views do
not spend the shared native-work budget on unused memberships. When both filters
are selected their existing rotating priority and independent health remain.

Tracker source access releases after each observer command. The console journal
attempt is one composite admission covering identity validation, optional setup
and the bounded page, under its shared two-second deadline after gate admission.
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
