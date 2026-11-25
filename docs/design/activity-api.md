# Activity read API v1

The Web host serves Project-bound Activity history and live updates. Activity
grants no lifecycle authority. Catch Up, guarded commands, invocation output and
diagnostic access keep their existing contracts. This slice adds no producer.

## Routes and requests

`GET /api/activity/v1/projects/{projectId}/history` returns one finite JSON page.
`GET /api/activity/v1/projects/{projectId}/events` returns SSE. Both use the exact
configured Project slug. A foreign Project receives 404 before source access.
Both responses use `Cache-Control: no-store`.

Both routes accept these optional query parameters, each once:

| Parameter | Values |
| --- | --- |
| `cursor` | Canonical `1.<journal GUID in lowercase N format>.<decimal offset>` |
| `kind` | Comma-separated exact `ActivityKind` names, at most 25 |
| `phase` | Comma-separated exact `ActivityPhase` names, at most five |
| `strength` | Comma-separated exact `ActivityStrength` names, at most four |
| `bead` | One exact safe Bead ID from the Activity contract |
| `level` | One of `work`, `warning`, `error` |

Values within an enum parameter use OR. Different parameters use AND. Empty,
duplicate, numeric and unknown enum values receive 400. Unsafe Bead IDs, unknown
parameters, malformed cursors, negative/future offsets and oversized input also
receive 400. Query text plus `Last-Event-ID` has a 1024 UTF-8-byte limit. The
limit applies before query parsing. Escaped text counts at its wire size.

SSE accepts `Last-Event-ID` instead of `cursor`. Supplying both is invalid.
History refuses `Last-Event-ID`. A cursor binds journal identity and last examined
offset. It contains no path, source text or authority. A foreign journal or an
expired offset produces `Expired, Gap` and resumes at available cached history.
The page retains the source journal's oldest and newest offsets.

A cursor request waits for the shared collector's first attempt before it sends
successful headers. This wait has a four-second deadline and observes request
cancellation. If bounds remain unavailable, the request receives 503. A same-
journal cursor above the initialized newest offset receives 400 on both routes.
Waiting SSE requests count against the 16-stream limit. These requests do not
start additional source reads.

The level filter is a display grouping. `RecoveryFailed` and `CleanupFailed` are
error. `HealthChanged` with `RuntimeFailed` or `Unhealthy` is also error.
`ChangesRequired`, `PauseObserved`, `EvidenceUnavailable`, `RecordsLost`,
`RuntimeReplaced` and `HistoryGap` are warning. Other records are work. This
grouping does not change a record's evidence strength or health reason.

## Shared reads and finite memory

One host-owned collector reads the journal. Requests never read journal files or
query Beads. With Activity interest, the collector makes at most one read turn
per two seconds. Each turn reads at most 100 records and examines at most 256 KiB,
including manifest, headers, framing and predecessor checks. One configured-origin
Git read has a two-second deadline and a 2048-byte output limit. It runs once per
Web host, on first Activity use. An unavailable origin leaves Activity unavailable
until Web restarts. It does not prevent other console operations.

An open stream or a history request in the last 60 monotonic seconds supplies
interest. With no interest, Activity performs no file or native tool reads.
Interest never triggers tracker polling. Viewer count does not multiply reads.
The separate runtime observation uses `FactoryTelemetryReader` once per turn.

The cache retains at most 4096 records, at most 18 MiB of conservative reservations
in the existing 32 MiB shared observation budget. The current journal format
retains at most 2040 records. Budget pressure removes oldest cache records and
reports a gap. It does not take memory from other reservations. Disposal releases
the reservation. No per-viewer delivery queue exists.

Initial history loads progressively. `loadedThrough` is the shared read position;
`newest` is the source's retained upper bound. `loading` means those bounds differ.
Continue polling the returned cursor while loading. An empty filtered page can
advance its cursor. Cache gaps remain explicit even when a filter hides loss
records. The API never treats an incomplete cache as complete retained history.

Each response returns at most 100 records. Its scan decodes at most 256 KiB of
canonical record bytes. The complete history response and each complete SSE
batch also fit 256 KiB. Before adding a record, the collector charges its encoded
record, detail links, array separators and SSE framing against both wire limits.
It measures fixed metadata with the longest cursor and scan count. A record that
does not fit stays after the continuation cursor for the next page. Request
filters never trigger a disk scan or an unbounded memory scan of record content.

## Wire and reconnect

History exposes `records`, `cursor`, `oldest`, `newest`, `loadedThrough`, `loading`,
`state`, `journalHealth`, `attemptedAt`, `observedAt`, `runtimeHealth`, `runtimeId`,
`runtimeObservedAt`, `runtimeReplaced`, `examinedBytes` and `details`.
Enums use their exact model names. Flags use the JSON string-enum representation.
Records use the canonical Activity schema, including source time and observation
time. Journal offset, not wall time, defines order. Source freshness on historical
records stays unchanged.

An SSE batch contains `status` before any `activity` item. Status carries retained
bounds, loading, journal health, runtime health, detail links and continuity.
Each `activity` carries its own cursor in `id` and the same canonical record as
history in `data`. A final `cursor` event advances over filtered or corrupt
positions. A disconnect after an item resumes after that item's identity. A
disconnect before that item completes leaves the prior identity in force.

Status reports source failure, truncation, expired history and loss before resumed
items. `reconnectGap` identifies a resumed cursor with unavailable continuity.
`runtimeReplaced` compares the retained cursor's runtime with the current runtime
only when a cached record matches both the cursor journal and offset exactly.
A foreign journal or missing offset supplies no runtime comparison evidence.
The history gap stays explicit. A separate comparison of runtime observations
detects replacement during an open stream, even without a retained cursor match.
Record-level replacement evidence remains in history. A healthy socket never
certifies a fresh source or a verified lifecycle outcome.

Journal health is unknown before the first attempt, unavailable after a failed
read, and fresh for at most 60 monotonic seconds after success. Runtime health
comes independently from `FactoryTelemetryReader`. A retained live runtime
observation becomes stale after ten seconds without another read. A failed journal
read retains historical facts and cannot hide a separately readable runtime.
Missing journal storage is unavailable. A successful empty journal is fresh and
empty. No active runtime is required to read prior-runtime records.

The host admits at most 16 streams. Excess requests receive 429 and `Retry-After:
2`. A viewer holds at most one bounded batch. Each write has a two-second deadline.
Timeout aborts that transport, observes any late fault and releases stream
admission. Disconnect and cancellation release admission. No viewer blocks the
journal writer, factory or another viewer's source reads.

## Detail boundaries and compatibility

`details` binds links to each record cursor. Validated Bead and comment references
use the existing Project Workspace and History routes. A current exact runtime,
Bead and invocation match can use the existing invocation SSE route. That route
still validates the runtime/slot/invocation and safe log access. A missing retained
invocation returns unavailable detail. Recovery and health references have no new
file route. Accepted merge facts retain only the Project-bound canonical PR URL.
The API accepts no arbitrary URL or file path.

The routes are additive. Existing invocation streaming, operations APIs and console
views keep their schemas. This slice changes no visible console behavior and adds
no transcript endpoint, mutation API or lifecycle action.

## Verification and cost

`ActivityApiTests` uses injected pages, a controlled monotonic clock and a held
response stream. In-process HTTP/SSE cases prove wire identity, continuation,
input refusal, capacity and cancellation. One disposable-file reader case proves
that absent storage stays absent; its origin command is injected. Existing journal
tests retain native path and framing coverage. These cases add no native journey,
fixture publication, tracker command or browser journey. The collector adds one
bounded origin read per used Web host and shared local reads only during interest.
The complete measurement must retain build, fixture publication and teardown,
all tests and the existing six threads.
