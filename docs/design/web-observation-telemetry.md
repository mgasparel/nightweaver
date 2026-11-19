# Web observation telemetry

The optional [operational exporter](operational-telemetry.md) adds sanitized Web
observation signals. Observation caches and exported telemetry supply no mutation,
readiness, ownership or Resume authority. The public HTTP/SSE contracts and legacy
`factory.json` availability remain unchanged. No telemetry UI is added.

## Measurement boundary

`OperationalObservationService` records a shared collector turn, once after the
source returns (including partial/pending results), independently of subscriber
count. Scheduled no-ops and no-interest housekeeping do not count. Duration is
monotonic source-collection latency, excluding interest selection, reconciliation
debounce, serialization, publication and browser paint. A turn may share several
native commands across multiple cached resources; attempts are not native command
counts. Unexpected failures and cancellation record only fixed outcome categories
before preserving existing exception behavior. Returned `source-timeout` codes
map to `timed_out`; other source errors map to `failure`. No error text crosses
the exporter boundary.

| Signal | Dimensions and meaning |
| --- | --- |
| `nightweaver.observation.attempts` | `source`: tracker/runtime/cleanup; `outcome`: success/failure/cancelled/timed_out |
| `nightweaver.observation.duration` | seconds by the same three source families |
| `nightweaver.observation.health` | current interested cached-resource counts by source and healthy/stale/unavailable/pending; idle or stopped sentinel is 1 when applicable |
| `nightweaver.observation.age` | maximum accepted-body validation age in seconds for each source/health group with known age |

Age uses the existing successful accepted-body provenance and monotonic clock.
Partial pending replacements cannot refresh it. Unknown age, idle and host-stopped
age are absent, never synthetic zero. A newly validated body can have a real zero
age. Health ages on the existing 250ms loop even without reads or new publications.
The 30-second interest grace applies; idle means no interested resources remain,
not fresh empty data. Runtime `availability=stopped` is distinct from host shutdown
and is used only while that observation is healthy; a failed/overdue retained
stopped body becomes stale. Host shutdown reports stopped for all families.
No interested source data produces unavailable until a successful body exists.

These are gauges of the bounded current cache, not counters of health transitions.
All six health groups are refreshed, including zeros, so old groups clear. The
age gauge omits groups without known ages. Consult health alongside age and export
loss. Metrics export every 60 seconds; very short state transitions may be missed.
The worker records short `observation` spans and dedicated structured EventId
1003 with only operation/outcome/source and a fixed body. Existing queue, sampling,
admission and shutdown budgets apply; busy collection can intentionally exhaust
the detail-event allowance while aggregate metrics remain unsampled. No per-turn
detail export or remote receipt guarantee is implied.

## Safety and deployment

The schema accepts only three fixed source families and six health states, with
at most 64 resource inputs per family and no Workspace key, Bead ID, Project,
URL, source map, exception, prose, prompt, local invocation output, HTTP/SSE body,
credential or arbitrary application log. The three family snapshots replace
bounded in-memory arrays; no telemetry cache drives source collection. Export
failure does not add native reads or block collection. Twelve streams translate
to a conservative 1118 series (including the execution overhead extension) across both service identities including overflow,
below the unchanged 2,000-series design budget. Runtime normally emits none of
the four Web observation streams; the inventory deliberately overestimates it.

Standalone Web: supply `NIGHTWEAVER_TELEMETRY_ENABLED=true`, the approved OTLP
HTTPS base/protocol and privately supplied Authorization header in the process
environment, then launch the published Web executable with the ordinary Project
and host-root arguments. Use the foundation's environment-only secret mechanism;
do not place credentials in Project JSON, shell arguments or checked-in examples.
Local isolated tests use only numeric loopback, an empty or synthetic header and
`NIGHTWEAVER_TELEMETRY_ISOLATED_QA=true`.

For the background pair, give the same approved environment to the Runtime owner
at its next separately authorized start. `BackgroundWebHost` inherits that
environment and launches the same paired Web executable. Runtime exports as
`nightweaver-runtime`; Web exports as `nightweaver-web`. Each process owns an
independent SDK, bounded queues and two-second graceful disposal budget. Use
`scripts/Publish-Nightweaver.ps1` for a complete paired artifact; telemetry SDK
assemblies accompany Web. No account changes, production installation/restart,
paid provisioning or hosted Grafana acceptance is authorized by this slice.

## Verification

Controlled-time service tests inspect OTLP protobuf through a loopback receiver:
100 subscribers still yield one turn, timeout retains stale last-good age, initial
failure is unavailable, idle and stopped differ, and hostile Workspace/error/body
canaries never export. A configured WebApplicationFactory source fixture proves
ten scheduled no-op turns add neither native work nor exported attempts. Existing
standalone executable journeys now enable isolated export and inspect Web identity
and observation records. The existing published standalone/background journey
checks the telemetry dependencies without adding another publish or process
journey. Existing two-host exporter tests preserve service identity separation,
export-failure boundedness and independent shutdown behavior. No UI behavior was
changed; browser QA is not required for these instrumentation-only changes.

Journal attachment health, individual native-command latency, source-specific
Workspace detail breakdown and browser paint/network latency remain deferred.
