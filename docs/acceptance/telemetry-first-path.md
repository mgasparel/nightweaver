# Local invocation-to-verified-merge proof

On 2026-11-19 the disposable pinned Grafana LGTM fixture imported version 1 of the
[dashboard JSON](../../observability/first-path-dashboard.json), received all
three OTLP HTTP/protobuf signals from the native fixture, and rendered queryable
results with working operational-ID correlation. This is local acceptance only.
Hosted Grafana acceptance and production deployment were not performed.
The same UID now carries the version 2
[bottleneck extension and its separate local evidence](grafana-bottlenecks.md).

The controlled Agent ran through the actual Windows executor and settled its
process/job. Native Git and isolated bd supplied the independent Merger proof
and confirmed guarded closure; hosted PR/check responses were controlled fixture
responses. No actual remote merge, real token or hosted account write occurred.

| Observation | Received/query result |
| --- | --- |
| Invocation duration | 0.1683352 seconds; `merger` role, `completed` status |
| Confirmed merge counter | 1 |
| Post-closure cleanup | 1 queued; not reported as completed |
| Fixed verified-outcome event | One `Verified merge closure confirmed.` event |
| Short traces | `invocation` and `verified_merge`, both discoverable with the same IDs |
| Native smoke | 1 passed, 0 failed/skipped; 15.259s outer MTP command including fixture setup |
| Browser | Dashboard and both imported Explore links passed; no page errors |
| Teardown | Exact disposable container removed in `finally` |

The proof's Bead ID is `stage-1lz`, runtime ID is
`0123456789abcdef0123456789abcdef`, and invocation ID is
`20261119T1200000000000Z-merger`. These are disposable fixture identities.
The invocation trace is `9787e23e1ae4cc2c7b1a17439f7869eb`; confirmed closure is
`ae41cd84da1985cef15187871c894aba`. The verified revision appears in the retained
signal/event/trace details. No operational ID is an aggregate metric dimension.

Retained sanitized evidence:

- [Dashboard](telemetry-first-path/dashboard.png)
- [Trace Explore](telemetry-first-path/explore-traces.png)
- [Event Explore](telemetry-first-path/explore-events.png)
- [Actual backend queries and trace details](telemetry-first-path/queries.json)
- [Received OTLP summary](telemetry-first-path/signals.json)
- [Immutable fixture image](telemetry-first-path/fixture.json)
- [Exact fixture teardown](telemetry-first-path/stopped.txt)

The raw protobuf requests and native fixture log remain under the current
invocation's `grafana-proof-retained` evidence directory. Each trace/log request
was below the 2 KiB transport cap, and the test rejected the private prompt/output
canary in every received signal. The default suite's deterministic cases also
cover refused guards, one confirming reread, unconfirmed closure, duplicate
completion, failed cleanup and cancellation after closure. Rejected evidence,
changed claims and failed current-head checks are checked for zero verified
success. High-cardinality details remain absent from unsampled metrics.

The final browser probe navigates directly back to the exact dashboard between
Explore links. An earlier probe assumed one browser Back crossed Explore's
initialization history and timed out; that probe failure was preserved and the
navigation was corrected. This did not require any production change.

See [the ownership/schema contract and reproduction command](../design/operational-telemetry-first-path.md).
Complete-suite timing and exact candidate/baseline identities are recorded in the
Bead implementation evidence; the isolated smoke's timing is not full-suite
performance acceptance.
