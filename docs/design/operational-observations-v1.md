## Amendment v1.3 — shared base and independent sections (ns-gjwx)

Contract owner: ns-gjwx-p88cf0dfa3650cc8fbb7a. This amendment defines the next
additive read contract. It does not claim that these resources are implemented.

Implementation slice ns-gjwx-paea384f7cce36dfdace5 enables only
`base:explorer:{view}:{query}:{page}` and `base:workspace:{id}`. Studio Explorer
uses the new paged base key. Workspace uses the shared base key and independent
text, history, relations and environment section keys.
Slice ns-gjwx-p52d0161f682b3122f1e2 enables all five
`section:{id}:{name}` resources. Slice ns-gjwx-p92fb31bbae007f103e1a enables
`base:attention` with the Attention base shape below. Studio Attention and the
Overview paused count use this inventory-only resource. Explanations use the
shared history section and require matching update time and workflow identity.
The other reserved base keys remain
unsupported. One compact inventory projection supplies exact-ID, ordered
search, status, type, stage, pause, assignee and priority indexes. It shares the
host memory budget with raw sources and resource envelopes. Base callbacks
publish on the serialized collection turn before optional native reads.
Callbacks retain captured admission identities and dirty generations. Ready
and Blocked publish with independent native membership and source health.
Skipped membership work continues on the next bounded turn. The compact cache
retires when base interest ends; accepted resource bodies keep their existing
bounded retention and freshness rules. Inventory lineage remains in the shared
inventory used by the existing presentation projector. No base row certifies
complete relations or history.

The existing full-response resources and legacy HTTP routes keep their payloads,
validation and publication rules during migration. Consumers must opt into the
new keys together with their backend support. Existing keys never return a base
payload in place of a full response. Unsupported new keys still return 400.

For the new resources only, this amendment supersedes whole-turn publication,
whole-Attention-history completion and full-Workspace completion as prerequisites
for **base** publication. A completed base projection publishes immediately,
before unrelated native work or optional sections finish. It cannot lend its
freshness, completeness or validation time to a section. All other v1.1/v1.2
identity, ordering, source, journal, audit, age, cancellation, settlement and
capacity rules remain in force. The [measurement and acceptance contract](../acceptance/shared-base-read.md)
defines the comparison required before numerical latency targets are proposed.

### Shared base and source map

Workspace consumer amendment v1.3.1 (ns-gjwx-p2c7fdd08ead7204a94ff):
base rows add nullable `planning` with the existing `PlanningReadiness` shape.
The server projects its guarded identity from the shared inventory. Legacy
full-response keys and payloads remain valid. Studio mounts section interest
only after base content exists. Each section displays its own source state and
Refresh action. Retained section text cannot replace current base facts.
History supplies pause context only when its timestamp and full workflow match
the base row. Base eligibility controls actions. Confirmed deletion removes all
current actions. Output keeps its separate runtime and invocation identity.

One host-owned Project cache holds the complete compact inventory and indexes.
Every Bead surface uses it, including cold direct links. No cache miss, HTTP
request, view filter or section request may start a targeted base `show` read.
The existing CLI adapter remains the source boundary. A successful shared read
must validate repository/Project identity, complete output, unique safe IDs,
required fields, payload bounds and the 10,001-row overflow sentinel before it
can replace the inventory. A limited, failed or partial read proves no absence.

The minimum base row contains `id`, `title`, `type`, `priority`, `status`,
`assignee`, `labels`, `stage`, `repairCycles`, `createdAt`, `updatedAt`,
`closedAt` and `createdBy`. Nullable fields retain their existing meaning.
Malformed workflow metadata is explicit; it must not become an empty pause set
or a zero repair count. Base presentation derives role, workflow badge, pause
kind and action eligibility from these facts and configured limits. It includes
the expected workflow identity (stage, full pause-label set and repair count).
Action eligibility is display evidence only. Guarded commands still read fresh
authoritative state and apply their existing guards.

Required bounded indexes are exact ID; status/type/stage/pause/assignee/priority;
complete ID/title search values and existing sort order; activity/window times;
and inventory-provided lineage edges used by Digest and presentation. Build them
from the same accepted inventory identity. Lineage indexes carry only the edges
the inventory actually supplies. They never certify complete incoming/outgoing
relations. Native Ready and Blocked are separate shared sources, not deductions
from labels, dependency counts or inventory edges. A failure in either source
does not withhold unrelated base projections. The existing display truncation
and 40-row Explorer page apply after search and ordering.

| Surface | Useful base content | Required sources | Deferred content and source |
| --- | --- | --- | --- |
| Overview | Counts, compact Attention and Catch Up rows, Bead links | `inventory`; `checkpoint` for Digest window/counts | Pause explanation: `comments:{id}`; merge result: `merge:{id}`. Activity cards use independent `runtime` through existing `factory`, including admitted title metadata; cleanup uses `cleanup`. Neither source gates tracker base. |
| Explorer | Existing filtered/searchable page, count and ordering; ID, title, type, priority, status, stage, assignee and badges | `inventory`; `ready` only for Ready, `blocked` only for Blocked | No history requirement. Selected Bead sections use the same section keys as Workspace. |
| Attention | Current nonterminal paused rows, workflow validity, pause kind, repair count/limit, links and action eligibility | `inventory`, configured limits | Explanation, attributed role/author, question/diagnostic, pause time, active report and other reports: complete `comments:{id}` plus current base workflow identity. Inventory update time is not an attributable pause time. |
| Workspace | Header, summary facts, badges, links and action eligibility for exact ID | `inventory`, configured limits | Full prose/metadata: `detail:{id}`; History and pause explanation: `comments:{id}`; relations: independent `relations-up:{id}` and `relations-down:{id}`; environment: `environment:{id}` with independent runtime provenance. Output retains the existing invocation identity and telemetry stream protocol. |
| Catch Up | Window/checkpoint, counts, thread/standalone membership, lineage, activity time, current pause/closure and links | `inventory`, strict `checkpoint` | Verified merge/PR/commit and evidence-derived outcome/summary: `merge:{id}` from complete `comments:{id}`. Cleanup warnings remain separate. Closure alone means closed, never Merged. |

Full prose means `description`, `design`, `acceptanceCriteria`, `notes` and raw
`sourceMetadata`. History includes full comments, classified messages, active
selection and older/recent IDs. These fields retain the existing projector
semantics; base data must not invent substitutes. A missing/corrupt checkpoint
keeps its existing absent-versus-failed distinction. Inventory-based counts may
remain visible with explicit checkpoint unavailability; a window that needs the
checkpoint cannot claim complete membership until that source succeeds.

### Additive resource and section contract

Use the existing `/api/operations/v1/projects/{projectId}` GET, refresh and SSE
transport, envelope schema `nightweaver-observation-v1` and host revision counter.
The following key families are reserved by v1.3. Existing key canonicalization,
query encoding, Project matching and safe Bead ID rules apply unchanged.

| New key | `data` schema and meaning |
| --- | --- |
| `base:overview` | `nightweaver-base-v1`: inventory counts and compact Attention/Digest summaries with independent source states |
| `base:explorer:{view}:{query}:{page}` | `nightweaver-base-v1`: existing page metadata and base rows; same view/query/page rules as paged Explorer |
| `base:attention` | `nightweaver-base-v1`: complete paused membership and base rows; no explanation fields |
| `base:workspace:{id}` | `nightweaver-base-v1`: one base row, or a confirmed absence |
| `base:digest:{window}` | `nightweaver-base-v1`: existing window, metrics, threads and standalone grouping, with base facts only |
| `section:{id}:{name}` | `nightweaver-section-v1`, where `name` is `text`, `history`, `relations`, `environment` or `merge` |

Every base `data` object has `schema`, `inventoryRevision` (the accepted shared
inventory's host-allocated positive decimal revision), `completeness`
(`complete` or `pending`), and `absence` (`null` or `{id, inventoryRevision}`).
It also has the surface fields named in the table. Base rows use the minimum
fields above; existing full-response presentation field names remain reserved
for their complete meanings. `inventoryRevision` identifies one shared value,
not an atomic transaction with readiness, checkpoint or runtime. Consumers must
not compare it with unrelated resource revisions to infer freshness.

Surface payload fields are fixed as follows. `row` means the minimum base row
above plus `presentation` restricted to inventory-derived badges/role and
`actions: {canDecide, canRetry, canAbort, unavailableReason, expectedWorkflow}`.
The three flags are booleans, `unavailableReason` is nullable safe text, and
`expectedWorkflow` has `stage`, `pauseLabels` and `repairCycles` with existing
workflow parser semantics. Non-Attention actions keep their existing separate
contracts. Do not copy history-derived `AttentionDeskItem` fields into a row.

| Key kind | Additional fields |
| --- | --- |
| Workspace base | `bead: row | null`; confirmed absence has null bead and nonnull absence; pending bootstrap has null envelope data |
| Explorer base | `items: row[]`, `total`, `page`, `pages`, `pageSize`; same existing Explorer page ordering, bounds and clamping |
| Attention base | `items: row[]`, `maxRepairCycles`; exact existing nonterminal paused membership, ordered by priority then ordinal ID so history cannot gate base ordering |
| Digest base | `generatedAt`, `window`, `windowStart`, `lastCaughtUpAt`, `isCaughtUp`, `metrics`, `threads`, `standaloneCompleted`, `standaloneOpen`; existing Digest types/ordering, except each item contains only a base row plus `activityKind`, `activityAt`, `fromId`, `fromRelation`. Thread `root`/`items` use these items; `completedCount`/`openCount` remain unchanged. No evidence-derived outcome/summary/PR/commit fields. |
| Overview base | `counts: {total, active, paused, closed}` from inventory, where active is the existing Explorer active predicate and paused is the Attention predicate; `attention` contains the first eight Attention base rows plus `total`; `digest` contains the first eight Digest base threads/standalone items in existing order plus complete `metrics` and window/checkpoint fields. A compact list is not the complete list. Runtime cards remain a separate `factory` resource. |

Missing required membership sources leave that projection pending/unavailable
with retained data as applicable; they cannot create an empty complete page.
Overview panels carry their contributing `sources` independently and may show
unavailable Digest beside available Attention. `completeness: complete` describes
the key's required sources, never completion of optional sections or an atomic
multi-source snapshot. Sections do not change base ordering or counts.

A section `data` object has `schema`, `beadId`, `name`, `baseIdentity`, `state`
and `value`. `baseIdentity` contains `inventoryRevision`, `updatedAt`, and the
expected workflow identity from the confirming shared inventory. `value` uses
the corresponding existing Workspace section or `DigestMergeEvidence` shape;
`history` groups `comments`, `history` and the history-derived Attention fields.
`text` groups the four prose fields and `sourceMetadata`. `relations` groups
the rows and `relationObservation`. Directional source health stays independent.
An available result for one direction cannot certify the other direction empty.
For `merge`, absence of matching verified records is an explicit unavailable
evidence result, not an empty successful merge. Safe URL validation is unchanged.

| Section state | Required evidence and display |
| --- | --- |
| `loading` | Admitted work has no accepted value. `value` is null. Show a section loading indicator, never an empty history. |
| `available` | All required sources completed and identity confirmation succeeded. Show the accepted nonempty value with its own provenance. |
| `empty` | The same complete validation succeeded and the section has no entries/text. Render confirmed empty. Inventory counts are insufficient. |
| `stale` | Retain the last accepted value after invalidation, age expiry, source failure or identity change. Show age, pending/error and historical identity. Do not use it for current actions or current explanations. |
| `unavailable` | No accepted value exists and work is not currently able to supply one, including unacquired/capped merge evidence. State the reason. `value` is null. |
| `failed` | An attempted read failed with no accepted value. `value` is null; expose the safe error and retry metadata. A failed refresh with a retained value uses `stale`. |

These states refine section presentation, not envelope `health`. Loading and
unavailable have unavailable health; failed has unavailable health plus an error;
available/empty require fresh contributing sources; stale has stale health.
Pending refresh can keep available/empty data readable until the existing
pending/age rules make it stale. Expose `synchronization.pending` immediately.
Initial loading uses a section descriptor with null value; it never changes the
existing rule that an unobserved envelope has null `data`. Once observed, the
descriptor carries the states above. Source errors and pending generations remain
visible even when base data is fresh. Aggregate health covers only the sources
required by that resource; it does not claim page-wide detail completion.

### Absence, identity and independent publication

Cold entry shows shared-base loading until a complete shared inventory succeeds.
A direct link joins that same work. An unknown ID during bootstrap or failed
refresh remains loading/unavailable, not missing. Confirm absence only from a
successful complete shared validation with no unincorporated inventory generation.
Publish its `absence` proof and the existing `resource-missing` error. A previously
confirmed absence is historical while a new generation is pending or failed;
do not present it as a new current absence proof. A confirmed deletion clears
the current Workspace base and disables its actions. Retained detail is optional
historical display with explicit stale identity; it cannot restore membership.

The collector accepts the absence proof and error as one validated projection.
It retains the inventory validation time and clears the completed pending
obligation. The resource is unavailable, with a null `bead` and the absence
proof in `data`. A raced inventory generation keeps the projection pending and
does not publish a new proof or a missing-resource error. Legacy Workspace
missing-resource responses still clear `data`.

Each native Ready or Blocked read publishes only its completed membership.
The other membership retains accepted data and pending refresh. An unfinished
read is not a budget failure. Report actual source failures and work skipped
by the completed bounded turn. Independent age rules still apply during a hold.

Each source captures its dirty generation and cache-entry identity before work.
Publication requires that identity to remain admitted. A raced hint keeps the
pending obligation and schedules continuation. Successful reads refresh clocks
only for the exact content they validate. New inventory content cannot refresh
old detail clocks. Before accepting a detail section, compare exact Bead ID,
workflow identity and `updatedAt` against a successful shared inventory validation
after its native reads. Reuse one confirming shared read across compatible
sections; never use a targeted base fallback. Conflict retains old detail as
stale, exposes `source-conflict`, and schedules bounded reconciliation. Comments
and dependency changes also require their independent source generations: equal
`updatedAt` or aggregate counts cannot prove those sources unchanged.

A partial relation section requires a current confirming inventory generation
and a current source generation for each successful direction. A failed direction
remains unavailable. A raced generation keeps the replacement pending and retains
the accepted body and its provenance, including when core work spends the turn.

Complete-source parsing, producer-qualified combined `show` handling and native
outgoing relation reads remain as specified in the observer scheduling amendment.
Combined reads may fill multiple due sections, but do not publish them before
their own validation. Merge requires matching `nightweaver-verified-merge-v1`
and referenced `nightweaver-merge-v1` records for the exact closed task, PR, checked
head and merge commit. Keep the 128 most-recent-closed-task acquisition cap per
requested window; older/unacquired tasks stay in base counts with unavailable
evidence. A reopened, resolved, unpaused, deleted or differently claimed base row
controls current membership and action eligibility immediately. Old explanation,
environment or merge data cannot reverse that observation.

### Bounded reuse and progress

Deduplicate native work by Project, epoch, Bead ID, source and dirty generation,
not by browser or surface. Share Attention/Workspace history and merge parsing.
Canceling one caller releases its interest only. Host cancellation owns native
termination and settlement. Do not release admission before process settlement.

Base, section descriptors, retained section values, source values and indexes
share the existing 32-MiB host budget. Keep the 8-MiB/10,000-row inventory bound,
64 resource keys, two selected Workspaces, 64 histories/16 MiB, 2-MiB detail
outputs and 2,048-comment limits. A section key consumes a resource slot; it does
not create another allowance. Retain recent unpinned sections through the existing
30-second interest grace and subsequent 30-second idle TTL, then evict by LRU
within these limits. Pinned entries cannot be silently evicted. Growth/admission
failure is explicit `source-limit`/429 and preserves accepted data. Browser cache
and stream limits remain unchanged. v1.2 immediate source retirement is relaxed
only for these bounded recent section values; audits stop when interest ends.
Source interest follows the admitted section names. Text interest alone cannot
keep history audits active or extend the history value's idle retention time.
Workspace, Attention and Digest retain the source interest each still requires.
Eviction removes provenance and admission identity with the value. Old callbacks
cannot populate readmitted entries or a replacement epoch.

Schedule selected missing sections before unrelated background enrichment. Keep
the four-second native-work turn, two-second command bound, four history commands
per turn and release the gate between commands. Continue unfinished work without
waiting for another hint. When core work consumes a turn, reserve the next turn
for selected/due details against the captured inventory; reuse that baseline for
at most one consecutive turn before core validation. Rotate between two selected
Workspaces, then due Attention/merge work; age pending obligations from their
first hint. Continuous selection or invalidation must not reset older obligations.
Waiting guarded operations keep priority with one due observer after eight guarded
operations. Confirmation reads count as core work. If confirmation is pending,
publish completed base work and keep the section pending. No optional section or
legacy full-response cohort can block base publication. Tests must show selected
detail, inventory refresh and guarded writes each advance under finite competing
load; none may require all unrelated histories to complete first.

## Amendment v1.2 — event-driven tracker observations (ns-sib.1)

This amendment supersedes the one-second tracker command schedule and its
three-second overdue rule. Runtime and cleanup retain their existing policies.
The URL, schema identifier, HTTP/SSE topology and guarded commands are unchanged;
backend and Studio assets ship together.

Tracker envelopes add `policy: {expectedValidationIntervalMs: 60000,
maxSnapshotAgeMs: 120000, maxPendingAgeMs: 5000}`, `validatedAgeMs` (null before
accepted data), `pendingAgeMs` (null when clean), and `synchronization: {journal:
"catching-up"|"current"|"degraded"|"inactive", pending: boolean}`. Ages are response
metadata, excluded from semantic revisions. Actual delayed/stale/source-status
transitions notify subscribers. Successful validation of the exact accepted
source content resets age; pending replacement provenance cannot refresh old data.
Pending cohorts publish known source failures immediately, including an envelope
warning. Failure health, error and attempt time overlay the accepted source state;
its content identity and successful validation clocks still describe the retained
body. Successful pending reads cannot lend replacement provenance to that body.
Without an accepted body, failed sources have no successful validation provenance.
Server deadlines and browser receipt ageing use monotonic clocks. On wake, retained
data requires reconciliation; browser wall-clock skew cannot confer freshness.

One Web-owned bounded journal reader serves interested tracker resources, including
standalone Web. It checks at five-second start-to-start cadence, at most one
256-record/8-MiB page, with a linked two-second command deadline, no overlap or catch-up
spin (minimum 250ms after overruns). Failed pages do not advance the cursor;
retry is 5/10/20/30 seconds. Uncertain child settlement stops replacements.
Identity/setup commands have their own two-second bounds; all native commands
share a four-second turn budget and release mutation admission between commands.
Successful preparation is not a healthy journal receipt or cursor progress.
No interested tracker resources means no journal process after the 30-second
interest grace. Hidden/suspended browser consumers release tracker interest;
wake requests immediate reconciliation and journal checking.

Hints invalidate sources, never supply authoritative bodies or authorize actions.
Create/update/close/delete and dependency changes invalidate inventory, interested
native ready/blocked sets and selected Workspace inputs. Comment invalidates
inventory and only the named cached history plus dependent presentations.
Unknown operations, gaps and identity ambiguity request full reconciliation.
Cold replay starts at zero in bounded pages with explicit catching-up; truncation
head is only a resume hint and requires an authoritative baseline. Cursors are
host/session-bound and discarded when repository/branch identity is uncertain.
Dirty generations coalesce for at most 250ms from the first hint, with at most
512 retained source identities and broad invalidation on overflow. New hints
cannot erase an older outstanding five-second obligation or a newer revision.
If a new reconciliation generation races publication after collection begins,
retain its pending obligation and request another projection turn even when
the source cache has already consumed the wake. This request does not invalidate
clean sources: reuse current authoritative generations, read only sources still
dirty or due, and preserve unrelated cached histories. A request arriving during
collection survives for the next turn. It must not wait idle for an audit.

Each interested source also receives an authoritative audit every 60 seconds,
spread after bootstrap, sharing inventory/readiness through continuation turns.
Four-history and four-second native-work limits, two-second native-command bound,
mutation fairness, two selected Workspaces and memory limits remain unchanged.
Unfinished Attention/Workspace cohorts drain without another event. Idle turns
issue no list/ready/comments commands before audit. If core work consumes a turn
before detail work can run, the next turn reserves capacity for those details
against the captured authoritative inventory, even if core hints keep arriving.
At most one consecutive turn reuses that baseline before returning to core
validation. Original source provenance and dirty-generation checks still prevent
a pending cohort from certifying a newer inventory; Workspace confirmation remains
required. This protects due histories from continuous core invalidation.
Explicit refresh and local writes wake reconciliation immediately, even after
uncertain command outcomes.
Ready/Blocked source values and audit slots retire when their Explorer keys lose
interest; returning to those views requires authoritative readiness validation.
Continuation tracks outstanding projections across bounded turns, so a completed
Workspace omitted from another Workspace's turn does not keep idle collection
active. Histories remain cached for selected Workspaces and, only while Attention
is interested, its paused cohort. Workspace detail, relation and environment
sources retire when deselected even if Attention still consumes the same history.
Retirement releases values, provenance, memory reservations and audit slots;
renewed interest establishes a new authoritative baseline for retired inputs.
A broad refresh clears once every retained source has incorporated its dirty
generation; retired sources cannot strand that pending obligation.

Connection evidence, source failure, snapshot age and pending delay are separate.
Transport heartbeat remains 10 seconds; 25 seconds without valid backend evidence
means disconnected. Stream errors mean recovering until that deadline; valid HTTP
reads still prove backend reachability. Before any valid evidence, the silence
interval starts at the first active connection attempt and is not reset by retries.
A failed request retains data and is a read error. A retry
start never proves recovery. A valid identity handshake and resource read are
required for synchronization recovery. Snapshot states are loading/unavailable,
current/refreshing/delayed/stale, with source errors immediately visible. Age
continues locally during disconnection, and drafts/focus survive resynchronization.
Normal supported changes take five-second detection plus bounded read/render
work; backlog/startup/failure and unjournaled-write audit recovery are exceptional.
Large scale and 130-second browser acceptance remain opt-in, outside normal tests.

`validatedAgeMs` also accompanies runtime/cleanup responses so retained data can
age while disconnected without comparing browser and server wall clocks. Those
resources retain their own `targetIntervalMs + 2000` allowance; they do not adopt
the tracker 60/120-second policy.

# Operational observations v1

Contract owner: ns-mbd.8; backend consumer: ns-mbd.2; stream/coordinator consumer:
ns-mbd.3. This freezes a proposed wire protocol, not a shipped API. Legacy `/api`
routes remain unchanged until the Studio cutover. No observer or frontend is
implemented by this investigation. Measurements are in
[observation-cost-results.json](observation-cost-results.json); reproduction and
limitations are below. Changes to this contract require an explicit versioned
amendment on the parent Bead before consumers diverge.

Contract amendment v1.1 (2026-11-14, ns-mbd.8 review repair): revision allocation
and eviction continuity below supersede v1's per-resource zero initialization.
Wire schema names and payload shapes remain v1; .2 and .3 must use this amendment
together before shipping.

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
| `explorer:{view}:{query}:{page}` | Bounded Studio `ExplorerPage` v1: `items`, `total`, `page`, `pages`, `pageSize` |
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
not change legacy payloads or silently truncate inventory. The server-paged
Explorer uses the additive four-part key contract above, leaving the legacy three-part key unchanged. Its canonical page is 1–250 (the supported 10,000-row inventory at 40 rows per page). Items contain exact ID, bounded title (400 code units plus ellipsis), type/status/stage (80), assignee (200), and priority. Search and ordering use complete source values before display truncation. Each shared sweep collects inventory/readiness/blocked membership once for all page keys, with the same independent source health and retained-result policy. Page projection returns at most 40 rows and clamps only pages beyond the final page; an empty result has page/pages 1. Exact invocation output keeps the
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

Revisions are allocated from one host-wide unsigned 64-bit counter, initially
zero, serialized across all Projects/keys in that host epoch. Every admission
(including readmission after TTL or LRU eviction) and every changed semantic
data, source health/error, or resource removal takes the next counter value.
Allocated revisions are positive decimal strings; zero is never published, even
for an initial unavailable envelope. Gaps are normal; clients compare within a
resource only, never infer cross-resource transactions. No per-key tombstones or
unbounded revision map is retained. The counter survives every cache/source
eviction and runtime restart within the Web host. It never wraps or resets in
the same epoch: on exhaustion stop observation admission/publication with 503
and shut down the host; its replacement starts with a new epoch.

Changing attempt/success timestamps alone does not allocate a revision. Exclude
observation timestamps (`generatedAt`, environment read
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

A successful source read may refresh sampling metadata on retained projections
before the complete serial collection finishes, only when its host-local content
identity still matches the source content backing each retained projection.
Changed content waits for complete projection/identity validation; metadata from
a new source value must never make an older projection fresh. Content identities
are not serialized, survive only with bounded source/cache entries, and callbacks
from evicted collection entries cannot update readmissions. Revalidation adds no
native reads and does not extend freshness limits. Read the selected Workspace's
shared comment history once at a consistent point in its cycle so alternating
Attention/Workspace priority cannot introduce artificial gaps between those reads.

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
resources during disconnect grace; the idle TTL starts when their last pin expires.
Do not evict subscribed resources silently: reject new admission with 429 if pinned capacity
is exhausted. Admission includes byte accounting and fails explicitly for a
single projection too large. Preserve last good data within those same bounds.
Shared sources survive individual subscriber cancellation.

Eviction removes a resource's data, revision and collection identity together.
Readmission publishes a new unavailable/null-data envelope at a newly allocated
revision, then schedules collection, even if the source has not changed. The
first successful collection allocates another revision. Before publishing any
asynchronous result, confirm its captured cache-entry identity is still current;
discard results from an evicted entry, even if its key has been readmitted.
Counter allocation and publication occur atomically with that identity check.
This uses only one fixed-size counter plus identities on the bounded cache
entries; eviction never needs retained server-side watermarks.

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
never silently discard correctness. Admit/pin all requested keys before hello
(or reject admission with 429); hello includes exactly subscribed keys and their
positive current revisions, including initial unavailable envelopes. Subscribe
atomically **before** sampling hello versions.
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
   In the same epoch, also retain the maximum revision already accepted for each
   retained cached resource across reconnects; hello cannot lower it. Include
   that watermark in the acceptance threshold. On a new epoch, clear revision
   watermarks, invalidate old requests and mark retained display stale before
   bootstrap; old data is display-only until replaced in the new epoch.
4. Invalidation during a read schedules exactly one further coalesced GET. An
   obsolete response cannot replace newer state. Routine invalidations retain the
   last validated status while the authoritative replacement is pending, avoiding
   stale/current flashing on every update. A fixed ten-second catch-up deadline
   starts at the first pending invalidation; later notifications and lagging
   responses cannot postpone it. If no response reaches the wanted revision by
   that deadline, retained display becomes stale. Read errors/timeouts, disconnects,
   epoch changes and authoritative source health still surface immediately.
   Accepting a current response or ending the subscription/connection cancels the
   deadline. No parallel reads per key.
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

Disconnect invalidates the connection generation immediately. A same-epoch
readmission's higher-revision unavailable envelope replaces the old observation
with null data; any retained old display is explicitly stale and cannot count as
current data. A later collected envelope supersedes it. Requests issued before
disconnect or under an old key/request generation cannot publish after reconnect,
even if their revisions would otherwise qualify. Retain watermarks only with the
bounded response-cache entries; evicting a client entry also invalidates its
requests. Recreating it requires the hello/GET bootstrap, never accepting a
response from its previous lifetime.

### Eviction continuity conformance

For both idle TTL (disconnect grace plus 30s idle) and unpinned capacity LRU:
observe revision N, disconnect while retaining client state, evict on the server,
mutate the source, and readmit in the **same** epoch. Hello must advertise M > N
with the new entry initially unavailable. Accept its GET at M as unavailable,
then accept the collected changed data at P > M. Deliver an old response at N
and a late collector completion from the evicted entry: neither may replace P.
Also exercise unchanged source readmission, reconnect without eviction (equal
revision is accepted), new epoch bootstrap, and allocator exhaustion without
wraparound. A bounded executable protocol model is in
[observation-ordering.py](../../tests/Nightweaver.QA.AppHost/observation-ordering.py).
Run `python tests/Nightweaver.QA.AppHost/observation-ordering.py` without an
AppHost or tracker. This verifies the proposed ordering rules, not shipped
observer/SSE code; .2/.3 must apply these cases to their actual implementations.

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
same fields instead have the next allocated revision, health unavailable, data
null, observedAt null, and unavailable inventory source. A comments-only failure on an existing
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
