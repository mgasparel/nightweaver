# Operational observations v1

Contract owner: ns-mbd.8; backend consumer: ns-mbd.2; stream/coordinator consumer:
ns-mbd.3. This freezes a proposed wire protocol, not a shipped API. Legacy `/api`
routes remain unchanged until the Studio cutover. No observer or frontend is
implemented by this investigation. Measurements are in
[observation-cost-results.json](observation-cost-results.json); reproduction and
limitations are below. Changes to this contract require an explicit versioned
amendment on the parent Bead before consumers diverge.

## Identity and endpoints

`projectId` is the configured `ProjectConfig.Name.Value`, case-sensitive, resolved
server-side. It is never a directory supplied by the browser. This host currently
loads one Project: another Project ID returns 404, never a fallback to that Project.
Every host instance generates a new opaque epoch, including the standalone process
launched by `BackgroundWebHost`. A runtime restart can change runtime identity
without changing the Web host epoch.

Base: `/api/operations/v1/projects/{projectId}`. JSON is UTF-8; responses are
`Cache-Control: no-store`. All query parameters must occur once except `key` on
events/refresh; reject unknown parameters and duplicate resource keys with 400.

| Method and suffix | Meaning |
| --- | --- |
| `GET /resources?key=...` | One authoritative cached resource envelope; no synchronous `bd` fanout per HTTP request |
| `GET /events?key=...&key=...` | Register interest, then operational SSE handshake; one connection per tab |
| `POST /refresh` | `{ "keys": ["..."] }`; enqueue/coalesce source reconciliation; 202 `{ "accepted": true, "keys": [...] }` is not observation success |

Refresh uses existing local origin policy, `application/json`, and
`X-Nightweaver-Request: console-v1`; maximum body 8 KiB. Existing commands keep
their existing endpoints, exact bodies, guards, statuses, and origin policy:
`POST /api/beads`, `/api/beads/{id}/{decide,retry,abort,ready-for-planning}`,
`/api/digest/catch-up`, `/api/cleanup/{id}/retry`. This single-Project host checks
the selected project matches its configured identity before issuing a legacy
command. Do not invent versioned command wrappers or retry commands on reconnect.
Known and uncertain mutations enqueue observation after releasing the gate.

Refresh accepts 1–32 canonical distinct keys (each <=1,024 UTF-8 bytes); malformed
keys are 400 and cache admission failure is 429. It renews those keys' interest.

Canonical resource identity is `(projectId, key)`; ordering identity also includes
epoch. Keys are case-sensitive strings, not filesystem paths:

| Key grammar | Data in the envelope |
| --- | --- |
| `factory` | Exact object returned by `/api/factory/state`, including snake_case nested snapshot |
| `attention` | Exact `AttentionDeskItem[]` from `/api/attention` |
| `explorer:{view}:{query}` | Exact `BeadExplorerItem[]` from `/api/beads?view=...&q=...` |
| `digest:{window}` | Exact `CatchUpDigestResponse` from `/api/digest?window=...` |
| `workspace:{id}` | Exact `BeadWorkspaceResponse` from `/api/beads/{id}` |
| `cleanup:{page}:{status}:{query}` | Exact `/api/cleanup` response |

`view` is `all|active|ready|blocked|closed`, explicitly supplied (Studio defaults
to ready). `window` is `auto|since_caught_up|24h|7d|30d|all`; checkpoint identity is
server-owned and invalidates every digest key, so it is not a client parameter.
`page` is canonical positive decimal <=1250; `status` is
`all|pending|retry-scheduled|needs-attention|resolved`. `id` is an exact safe Bead ID
using existing `BeadIdSafety`, including hierarchical dotted IDs. `query` is
unpadded base64url of UTF-8 search after trimming leading/trailing whitespace;
empty search encodes as an empty suffix. Preserve interior spaces and case; search
uses existing ordinal case-insensitive matching. Reject noncanonical encodings,
invalid UTF-8 and search longer than 200 UTF-16 code units. Apply URL encoding to
the **whole** key when putting it in a URL. Examples: `explorer:ready:`,
`explorer:all:YWJj` (abc), `workspace:ns-mbd.8`, `cleanup:1:all:`.

Legacy payload schemas are normative by the named record declarations at the
retained base b63cca8e6c2ac6fbb9e3169562ca449ab8f80b7d, including nullable fields,
presentation metadata, relation observation and history. Their sources are linked
below. Additive nullable fields may evolve; removing/renaming/changing types needs
v2. Arrays preserve existing sort order. UI pagination bounds rendering; it does
not change legacy payloads or silently truncate inventory. A future server-paged
Explorer needs its own versioned contract. Exact invocation output keeps the
independent existing `/api/telemetry/stream` protocol and its byte cursor.

## Read envelope: `nightweaver-observation-v1`

All fields below are required, including nullable fields. Unknown additive fields
are ignored. Decimal revisions match `^(0|[1-9][0-9]*)$`; compare as arbitrary-size
integers, never JS Number. UTC dates use ISO 8601 `Z`. Dates are not version tokens.

```ts
type Health = 'fresh' | 'stale' | 'unavailable';
type ObservationError = {
  code: 'source-timeout' | 'source-failed' | 'source-conflict' |
        'source-limit' | 'resource-missing' | 'budget-exceeded';
  message: string; // safe display text, <=512 chars; no tool stderr or secrets
  retryable: boolean;
};
type SourceState = {
  health: Health;
  observedAt: string | null; // last successful complete read, not HTTP response time
  lastAttemptAt: string | null; // start of most recent attempted read
  error: ObservationError | null;
};
type Envelope<T> = {
  schema: 'nightweaver-observation-v1';
  projectId: string;
  key: string;
  epoch: string;
  revision: string;
  observedAt: string | null;
  lastAttemptAt: string | null;
  health: Health;
  data: T | null;
  error: ObservationError | null;
  sources: Record<string, SourceState>;
  targetIntervalMs: number;
  nextAttemptAt: string | null;
};
```

Source IDs: `inventory`, `ready`, `blocked`, `comments:{id}`, `detail:{id}`,
`relations-up:{id}`, `relations-down:{id}`, `environment:{id}`, `checkpoint`,
`runtime`, `cleanup`. Include only sources required by this projection. Project
validation failures mark dependent sources unavailable; runtime failure does not
poison tracker sources. Workspace environment/relations keep their existing
partial observation semantics inside data, plus corresponding source health.

Revision starts at `"0"` with no data; increment on changed semantic data, source
health/error, or resource removal. Changing attempt/success timestamps alone does
not increment it. Exclude observation timestamps (`generatedAt`, environment read
time and other sampling clocks) from semantic equality, but include content,
checkpoint, runtime identity/status and domain activity timestamps. Digest
generatedAt is captured at whole-second precision **before inventory collection**,
never at HTTP response time. Unchanged successful observations can carry newer
freshness timestamps at the same revision. Heartbeats do not produce revisions.

A read returns 200 for a valid registered key even if its source is unavailable.
Before any successful observation: data/observedAt are null, health unavailable;
never substitute `[]`. After a failed collection retain the **entire last valid
projection**, set stale/error and update source attempt metadata. Do not blend a
new inventory with old comments while calling the result fresh. Independent
resources continue updating. A confirmed deleted Bead publishes null data with
`resource-missing`, unavailable health and retryable=false; it does not retain a
Workspace as though it still existed. A transient Workspace identity mismatch is
stale with `source-conflict`, retryable=true, preserving last good data.

Age past the advertised normal interval + 2 seconds without success makes health
stale with budget-exceeded (unavailable if never successful), even if no command
has completed to report failure. Scheduled detail backlogs expose their actual
nextAttemptAt. Do not label a 30-second comment rotation five-second fresh.

HTTP problem responses, outside the envelope: 400 invalid key/query/JSON; 403
rejected origin on refresh/commands; 404 unknown Project or unsupported route;
413 body limit; 415 content type; 429 resource/subscriber limits with Retry-After
2; 503 host shutting down. Existing guarded command 409 remains authoritative;
it is not a successful observation or automatic command retry.

## Collection, fairness and finite budgets

One observation owner per configured Project per actual Web host. No per-browser
tracker readers. Inventory, ready and blocked are separate native observations;
no cross-command transaction is claimed. Use existing `BeadsCommand` environment
scrubbing and backend validation. Read from public CLI only. Events/timestamps
may wake a reconciliation but never replace periodic comments/dependency/readiness
reads. Do not inspect Dolt internals or use file watching as sole correctness.

Normal desired schedule: inventory/ready/blocked every 1 second start-to-start,
selected Workspace every 1 second, paused comments every 1 second if capacity
permits; no overlaps/catch-up queues. Collect one shared inventory for summaries,
then ready/blocked. Workspace preserves before/detail/relations/comments/after
identity checks; its confirming inventory may feed summaries but cannot
retroactively make readiness transactional. Share selected-Bead comments with
Attention in that cycle. Runtime samples independently every 2 seconds, without
the tracker gate; cleanup/checkpoint file sources every 2 seconds.

There is one native tracker command in flight per Project. Release observer
access between native reads; mutation waiters precede the next observer command.
A Workspace read revalidates identity around these boundaries; it never holds
the mutation gate through an entire background sweep. Mutations keep their
existing uninterrupted guarded operation. Thus an observer delays a mutation by
at most the current command's **2-second observer timeout plus settlement**; this
is not a promise about time spent by another mutation or an external process.
After 8 consecutive mutations admit one due observation command, preventing
observer starvation. Mutation storms can violate freshness and must expose that.
No observer failure triggers a command replay or changes workflow authority.

Inventory/membership: 2s per process and 8 MiB output each; inventory maximum
10,000 rows. Prefer explicit 10,001-row sentinel limit where supported; when the
native command has no limit, bound stdout while receiving, kill/settle at the
byte bound and never cache a prefix. At row/byte limit report source-limit and
retain last valid data. Detail/comments/relations: 2s each; comments retain the
existing 2 MiB and 2,048-comment caps; detail/relations <=2 MiB/10,000 edges.
No hidden retries within a tick. Kill and settle a timed-out process before
starting another. Cancellation is owned by the host, not an individual HTTP
request. Shutdown stops admission, cancels collectors and awaits settlement.

An observer sweep has 4 seconds of native work budget, then yields; at most one
coalesced pending sweep. Always interleave core inventory/readiness before
round-robin history. Up to four due comment commands per sweep, one selected
Workspace refresh per sweep; if the budget expires continue fairly on the next
sweep, including Workspace substeps under bounded identity verification. No
resource can monopolize due history indefinitely. Up to two actively selected
Workspace keys per Project; additional interest returns 429. Pins expire 30s
after last interest, so HTTP-only clients must resubscribe/refresh while in use.
An initial GET registers interest for 30s; its first answer can be unavailable
while collection starts. Streams hold pins until disconnect plus that grace.

Large paused inventories necessarily exceed five-second history visibility on
this backend (measured below). The bounded alternative is continuous core
reconciliation, priority to selected Workspace, round-robin paused histories,
explicit stale/next-attempt metadata, and manual coalesced refresh. This is a
reported target failure, not acceptance of a weaker global freshness guarantee.
ns-mbd.2 must remeasure its actual implementation and record achieved bounds.

Limits per Project: one inventory <=8 MiB; 64 projected resource keys, 32 MiB
serialized total including history, LRU only for unpinned resources, 30s idle TTL;
two pinned Workspaces, 64 comment histories with 16 MiB combined cap. Do not evict
subscribed resources silently: reject new admission with 429 if pinned capacity
is exhausted. Admission includes byte accounting and fails explicitly for a
single projection too large. Preserve last good data within those same bounds.
Shared sources survive individual subscriber cancellation.
If Attention requires more than 64 distinct histories, or its complete projection
does not fit the byte budget, report source-limit rather than cycling evictions
forever or publishing an incomplete healthy list. Inventory/Explorer still show
those Beads. This is a finite supported observation envelope, not a tracker size
restriction. The evidence fixture remains below these history/byte bounds.

## SSE: `nightweaver-operations-v1`

At most 16 streams per Project, 32 keys per stream, URL <=8 KiB. Maximum 64 queued
distinct invalidations or 64 KiB per subscriber; latest revision per key coalesces.
Writes time out after 5s; disconnect on slow subscriber. No durable replay log.
`Last-Event-ID` is ignored; reconnect always obtains a new hello and resync.
Server SSE frames use `event:` and JSON `data:` followed by a blank line; no IDs.

```text
event: hello
data: {"schema":"nightweaver-operations-v1","projectId":"qa","epoch":"opaque-host-A","versions":{"factory":"8","attention":"3"},"heartbeatMs":10000}

event: invalidation
data: {"schema":"nightweaver-operations-v1","projectId":"qa","epoch":"opaque-host-A","versions":{"attention":"4"}}

event: resync
data: {"schema":"nightweaver-operations-v1","projectId":"qa","epoch":"opaque-host-A","reason":"overflow"}

event: heartbeat
data: {"schema":"nightweaver-operations-v1","projectId":"qa","epoch":"opaque-host-A","at":"2026-11-14T16:00:00Z"}
```

Resync reason is `overflow|reconnect|epoch-changed`; heartbeat is every 10s. Queue
overflow clears pending frames and enqueues resync, or closes if it cannot do so;
never silently discard correctness. Hello includes exactly subscribed keys,
including revision 0. Subscribe atomically **before** sampling hello versions.
Changes while hello is written remain queued. Send resync reason reconnect after
hello on every new stream (including first); no historical replay is assumed.

Bootstrap/reconnect/wake sequence:

1. Client creates one stream for current Project/active keys; waits for hello,
   buffering maximum invalidation revision per key. Changing key set replaces
   stream with a new generation. Close old connection first.
2. After hello, authoritative GET for every active key, deduplicated by identity.
   Capture epoch, connection generation and per-key request generation.
3. Accept a response only for current identity/epoch/generations and revision >=
   maximum hello/invalidation version known for that resource. A newer response
   is allowed (event can lag). Compare within resource only. A new epoch in HTTP
   initiates handshake replacement; never merge across epochs.
4. Invalidation during a read schedules exactly one further coalesced GET. An
   obsolete response cannot replace newer state; retained display stays visibly
   stale until the authoritative response catches up. No parallel reads per key.
5. Resync refetches all active keys even at equal revision, updating freshness.
   Every 2s while visible, coalesce GETs of active cached resources to learn
   freshness-only metadata (same revision is legitimate). These GETs never start
   native reads. On 25s heartbeat silence mark disconnected and reconnect with
   capped exponential jitter (1s to 15s). Heartbeats don't make tracker fresh.
6. Hidden tab may pause the freshness GET timer; on visible/focus, reconnect if
   needed and resync all active keys. Manual refresh first POSTs refresh then
   follows authoritative versions; it does not only reread stale cache.

Coordinator interface (transport independent):
`subscribe(identity, listener) -> unsubscribe`, `getSnapshot(identity) ->
{observation, connectionState, refreshing}`, `refresh(identities) -> Promise<void>`
(resolves on 202 acceptance; refreshing clears on a later attempt), and
`invalidateAfterCommand(identities)` (includes uncertain command outcomes).
For intake/workflow/guidance changes, reconcile all active tracker-backed keys
(attention, explorer, digest, workspace); checkpoint changes target digest;
cleanup Retry targets cleanup and factory. This callback coalesces POST refresh
and subsequent GETs; the backend's own after-gate signal may already be pending.
It must not fabricate a newer revision from command success alone.
Clean response cache: 64 keys/16 MiB per tab, unpinned LRU/30s idle TTL. Reject a
new pin visibly at capacity. Keep interaction state separately by Project+Bead;
up to 20 dirty drafts/1 MiB in memory, visible limit on creating another draft,
never silent draft eviction or automatic persistence of guidance.

## Partial-source examples

Factory can be fresh at revision 8 while inventory fails and Attention is stale
at revision 4 with its prior data. Neither ordering nor the heartbeat implies the
two describe one transaction. A fresh stopped-runtime observation is distinct
from a failed read: keep factory data availability `stopped` and envelope fresh.
The telemetry reader's own 10s heartbeat-expiry health remains authoritative.

```json
{
  "schema": "nightweaver-observation-v1",
  "projectId": "qa", "key": "attention", "epoch": "opaque-host-A",
  "revision": "4", "observedAt": "2026-11-14T16:00:00Z",
  "lastAttemptAt": "2026-11-14T16:00:02Z", "health": "stale",
  "data": [],
  "error": {"code":"source-timeout","message":"Tracker inventory timed out.","retryable":true},
  "sources": {"inventory":{"health":"stale","observedAt":"2026-11-14T16:00:00Z","lastAttemptAt":"2026-11-14T16:00:02Z","error":{"code":"source-timeout","message":"Tracker inventory timed out.","retryable":true}}},
  "targetIntervalMs": 1000, "nextAttemptAt": "2026-11-14T16:00:04Z"
}
```

Here `[]` is a previously validated empty observation. On first-load failure the
same fields instead have revision 1, health unavailable, data null, observedAt
null, and unavailable inventory source. A comments-only failure on an existing
pause preserves last good Attention data, marks `comments:{id}` stale, and leaves
Explorer fresh. If no complete Attention projection has ever succeeded it stays
unavailable; its inventory-derived existence/count must not be fabricated as
complete Attention details. Workspace directional relation failures retain the
existing `relationObservation` partial states and safe independent data; sources
and envelope health mark that partial result stale.

## Primary source audit

Paths refer to the retained base revision above:

* [BeadsExplorerService.cs](../../src/Nightweaver.Web/BeadsExplorerService.cs): one
  `operationGate` wraps whole legacy reads/mutations. Explorer needs inventory
  plus ready/blocked membership for those views. Attention needs inventory plus
  one comments command per paused nonterminal Bead. Digest needs one inventory.
  Workspace needs inventory, show, up/down dependencies, comments and confirming
  inventory (six native commands), then environment Git/filesystem observations.
  Cross-command workflow mismatch is 409. Gate release is in finally.
* [BeadsReader.cs](../../src/Nightweaver/Inspection/BeadsReader.cs) and
  [BeadsCommand.cs](../../src/Nightweaver/Inspection/BeadsCommand.cs): public
  readonly/sandbox queries, existing 30s process timeout, comments size/count
  guards, strict JSON identity validation, native ready and blocked semantics.
  New observers need the shorter bounded source timeout above; commands retain
  existing safety and verification budgets.
* [AttentionDesk.cs](../../src/Nightweaver.Web/AttentionDesk.cs): comments select
  questions and diagnostics independently of workflow labels; note/audit text
  is never Resume authority. [BeadWorkspace.cs](../../src/Nightweaver.Web/BeadWorkspace.cs)
  defines exact history, presentation and partial relation shapes.
* [CatchUpDigest.cs](../../src/Nightweaver.Web/CatchUpDigest.cs): all six windows,
  checkpoint file cap 16 KiB, projection over inventory. Checkpoint invalidation
  must work even if inventory content did not change.
* [FactoryTelemetryEndpoints.cs](../../src/Nightweaver.Web/FactoryTelemetryEndpoints.cs)
  and [FactoryTelemetry.cs](../../src/Nightweaver/Telemetry/FactoryTelemetry.cs):
  runtime state from <=1 MiB validated snapshot, live/stale/stopped/unavailable;
  existing invocation stream has 64 KiB replay and 16 KiB chunks, 2s sampling.
* [Program.cs](../../src/Nightweaver.Web/Program.cs) registers singleton bridge
  and endpoints. [BackgroundWebHost.cs](../../src/Nightweaver/Background/BackgroundWebHost.cs)
  launches the published Web executable with Root/ConfigPath. Both hosting paths
  therefore need the same observer registration and host-lifetime disposal.

The protocol preserves the Project/Target Repository authority boundary and
evidence-only worker model in [SPEC](../../SPEC.md), [GLOSSARY](../../GLOSSARY.md),
and [ADR 0002](../adr/0002-beads-native-agent-handoff-and-thin-orchestrator.md).

## Measured feasibility (2026-11-14)

Windows local worktree at b63cca8, Python 3.14.5, bd 1.3.1 c1c4b642a, embedded
disposable Dolt initialized by the repository's isolated QA AppHost. Fixed sizes
50 and 2,000 Beads; seeded task bodies roughly 1 KiB; comments roughly 2 KiB each;
eight and forty nonterminal paused Beads respectively (four default fixture
pauses plus four newly seeded pauses at 50, then 32 additional pauses at 2,000).
One selected nonpaused Workspace; one dependency edge added and its blocker then
closed. No production tracker data, database internals, or watchers were used.

The disposable probe performs a full inventory + ready + blocked + every paused
comments read + selected show/up/down/comments + confirming inventory, serially.
That is `8 + pausedCount` native commands per cycle. It validates actual JSON
membership/comment content, rather than just command exit. Three baseline cycles
and three changed cycles per size; 477 direct CLI invocations in total, including
fixture preparation/version queries, plus ten baseline HTTP requests and six
visibility HTTP requests. HTTP uses the real legacy projection and therefore
performs additional native commands not included in the 477 count. Runtime
sampling adds four HTTP reads without `bd`.

| Fixture | Native commands/cycle | Three cycles (s) | Legacy Attention HTTP (s) | Legacy Workspace HTTP (s) |
| --- | ---: | --- | ---: | ---: |
| 50 / 8 paused | 16 | 4.693, 4.452, 4.454 | 2.867 | 2.562 |
| 2,000 / 40 paused | 48 | 15.167, 14.875, 16.309 | 12.712 | 2.795 |

End-to-end below starts **before the external CLI mutation**, waits its success,
includes a deliberate one-second missed scheduling phase, performs a complete
collection, then finishes at receipt/validation of a real projected HTTP response
(Workspace for comment, Ready for dependency/readiness). These are measured
disposable serial-probe results, not an implemented observer/SSE/render latency.
The extra legacy HTTP read is conservative; a cached observer should not repeat
its native work. `committedToHttpMs` in raw evidence subtracts mutation time.

| Change | 50 Beads start-to-HTTP / committed-to-HTTP (s) | 2,000 Beads start-to-HTTP / committed-to-HTTP (s) |
| --- | --- | --- |
| Comments-only addition | 8.218 / 7.911 | 19.588 / 19.189 |
| Add native blocking dependency | 6.815 / 6.368 | 18.312 / 17.828 |
| Close blocker, native readiness changes | 7.360 / 6.737 | 18.203 / 17.705 |

All six changes were detected and asserted. The selected Bead's `updated_at`
changed in **none** of them. Comment addition changed only `comment_count` on
that row; dependency addition changed only `dependency_count`; closing the
blocker changed neither selected field, but changed native ready/blocked state.
The full inventory did change in these cases. Counts can provide hints but
cannot establish content equality (a same-count edit/replacement would evade
them). The public bd 1.3.1 comments CLI exposes addition, not an edit command;
in-place comment editing was not exercised or simulated through database writes.
Periodic explicit comments, relation and readiness reads remain necessary.

Across both sizes, native read elapsed milliseconds (min / median / max),
including subprocess startup and JSON transfer, were:

| Source | Samples | ms min / median / max | Largest UTF-8 output bytes |
| --- | ---: | --- | ---: |
| inventory | 36 | 314.34 / 442.94 / 820.23 | 2,567,191 |
| ready | 12 | 223.29 / 310.52 / 894.34 | 2,564,866 |
| blocked | 12 | 169.26 / 192.76 / 597.76 | 1,287 |
| comments | 300 | 200.79 / 254.71 / 1,063.72 | 2,336 |
| show | 12 | 294.38 / 359.68 / 846.75 | 2,753 |
| up dependencies | 12 | 208.22 / 244.86 / 521.29 | 3 |
| down dependencies | 12 | 225.12 / 262.91 / 565.21 | 1,318 |

Runtime read samples completed at 39.16, 2,002.78, 4,003.41 and 6,002.77 ms;
all four reported live, with independently advancing source observedAt values
about 2.005 seconds apart. HTTP cost was 2.51–39.14 ms. This verifies inexpensive
runtime visibility at the desired cadence on this fixture, not an SLA under
tracker/mutation pressure or a new observer implementation.

**Five-second result: not achieved by full serial reconciliation.** At forty
pauses the comments commands alone have a roughly ten-second median cost floor
before other reads, even though total output is small. Merely adding browsers or
shortening a timer cannot solve subprocess fanout. The finite scheduler above
prioritizes core/selected data, yields between commands for mutations, and rotates
bounded histories with explicit stale health. With four comments per sweep, forty
histories require at least ten sweeps; at a four-second work budget, roughly forty
seconds is a planning estimate before contention, not a measured upper bound.
Eight pauses can also miss five seconds; there is no unconditional small-fixture
success claim. Shared collection/avoiding legacy rereads could improve selected
visibility, but .2 must measure that actual implementation before claiming it.

Caveats: no long-history/2 MiB saturation case, managed active worktree Git-cost
case, external lock-contention load, mutation-storm fairness execution, multiple
host process competition, browser rendering, or SSE transport was measured.
The 2,000-Bead fixture is a representative inventory scale, not a proof for all
10,000-row budgets or 64-history worst cases. The 2s timeout and cache budgets
are explicit implementation bounds chosen above observed individual command
cost, not measurements of existing legacy limits. .2 owns fake-clock fairness,
partial failure, shutdown and concurrent-reader tests; .3 owns handshake and
browser transport tests. No freshness requirement is silently relaxed.

Reproduce with the exact-stack start/wait/probe/finally-stop commands in
[QA AppHost README](../../tests/Nightweaver.QA.AppHost/README.md). The raw JSON
retains every direct native command's elapsed time/bytes and per-change timings;
`seededPaused` describes newly added pauses, `actualPaused` is the complete probed
set. The earlier exploratory subset run is superseded by this all-paused run.
