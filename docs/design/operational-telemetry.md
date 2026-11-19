# Operational telemetry export v1

The continuous runtime and standalone/background Web executable have an optional,
observation-only OTLP HTTP/protobuf path. It does not establish readiness, claims,
resume authority, workflow transitions or exactly-once business accounting.
Factory schema 2 snapshots, local invocation output and console logs retain their
existing APIs and storage. No Collector, Alloy, account changes or production
deployment are prerequisites for this slice.

## Operator configuration

Export is disabled unless the **process environment** contains
`NIGHTWEAVER_TELEMETRY_ENABLED=true`. Dry-run never reads exporter configuration or
creates a provider, worker or HTTP client. Configuration files, command-line
arguments and ASP.NET configuration providers cannot opt in or supply credentials.
Background Web inherits its owner's local environment and runs the same Web entry
point. Changes apply on the next independently authorized host start.

| Environment variable | Contract |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | HTTPS base, default `https://otlp-gateway-prod-ca-east-0.grafana.net/otlp`; no credentials, query, fragment or existing `/v1/` signal suffix |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Only `http/protobuf`, also the default |
| `OTEL_EXPORTER_OTLP_HEADERS` | Required for HTTPS; exactly one valid Authorization entry in OTLP header syntax, supplied privately in the process environment |
| `NIGHTWEAVER_TELEMETRY_ISOLATED_QA` | Explicit `true` allows numeric loopback HTTP and sets head sampling to 100%; never enables export on its own |

Keep real tokens and encoded authorization values out of source, examples, Beads,
command arguments and logs. Missing credentials or invalid enabled configuration
produces one fixed local diagnostic and disables export without preventing
ordinary Delivery. SDK initialization errors also receive only a fixed message.
Environment resource attributes, service names/versions/instance IDs and
signal-specific exporter overrides are not imported into the approved resource.
Experimental SDK retry configuration is rejected; there is no disk spool or
resident agent. Other SDK environment settings cannot add instruments or fields.

The endpoint appends exactly one `/v1/traces`, `/v1/logs` or `/v1/metrics`.
Redirects, proxies, cookies and automatic HTTP trace-header propagation are
disabled. No error body is read or echoed. Nonempty success bodies are treated
conservatively as possible partial rejection and produce a fixed loss diagnostic;
they do not acknowledge business outcomes. HTTP failure never alters a Bead.

## Schema and ownership

Only `Nightweaver.Operational` instruments are registered. Providers additionally
check the actual owned Meter/ActivitySource instances, so another instance with
the same name cannot export data. No blanket ASP.NET, HttpClient, process,
runtime or application-log instrumentation is enabled. The dedicated private
ILogger factory uses EventId 1001 (`OperationalCompletion`) and fixed body
`Operational operation completed.`, plus EventId 1002 (`VerifiedMerge`) and
`Verified merge closure confirmed.`, and EventId 1003 (`ObservationCompletion`)
with `Observation collection completed.`; it never receives arbitrary log state,
formatted free text, scopes or exceptions.

Resources contain only `service.name`: `nightweaver-runtime` or `nightweaver-web`.
There is no project dimension in v1. Arbitrary resource attributes, paths, URLs,
prompts, source, transcripts, commands, exception messages/stacks, HTTP bodies
and baggage have no schema field. Typed enum/numeric validation rejects unknown
values, nonfinite or negative durations and durations over 30 days. The optional
detail ID must be exactly 32 ASCII hexadecimal characters and is present only in
traces/logs. No Bead, runtime, invocation or service-instance ID becomes a metric
label. Owned activities decorated by other listeners with unknown tags, events,
links, baggage, trace state, free-text status or names are dropped at export.

The host operations are `host.start` and `host.stop`; outcomes are `success`,
`failure` and `cancelled`. Runtime startup measures entry through runtime
initialization; runtime stop measures cleanup settlement after discovery exits,
excluding export disposal. Cleanup failure records failure, not successful
teardown. Web startup measures Main entry through ApplicationStarted; Web stop
measures ApplicationStopping through ApplicationStopped. Force stop emits no stop
record and starts no flush. The [first Delivery path](operational-telemetry-first-path.md)
adds invocation settlement and confirmed verified merge closure. Classification
overhead, verification duration, queue age, cleanup attempts and provider usage
remain deferred. Host stop is not verified merge success.

| Metric | Approved dimensions | Maximum normal combinations per host |
| --- | --- | ---: |
| `nightweaver.operations` | operation (2), outcome (3) | 6 |
| `nightweaver.operation.duration` (seconds) | operation (2) | 2 |
| `nightweaver.telemetry.dropped` | signal (3), reason (`budget`, `queue`, `schema`; one reserved reason) | 12 conservative |
| `nightweaver.telemetry.export.failed` | signal (`traces`, `logs`, `metrics`) | 3 |
| `nightweaver.invocation.attempts` | role (7), status (6) | 42 |
| `nightweaver.invocation.duration` (seconds) | role (7) | 7 |
| `nightweaver.verified_merge` | none | 1 |
| `nightweaver.merge.cleanup` | outcome (4) | 4 |
| `nightweaver.observation.attempts` | source (3), outcome (4) | 12 |
| `nightweaver.observation.duration` (seconds) | source (3) | 3 |
| `nightweaver.observation.health` (gauge) | source (3), health (6) | 18 |
| `nightweaver.observation.age` (seconds, gauge) | source (3), health (6) | 18 |

Metric views drop unknown instruments/foreign meters and explicitly select tag
keys. Cardinality limits include one SDK overflow point per dimensional stream
(7, 3, 13, 4, 43, 8 and 5 for the existing dimensional streams, and 13, 4,
19, 19 for observation attempts, duration, health and age); verified merge has no dimensions.
Histograms use cumulative temporality and explicit bounds
`[0.1, 1, 5, 30, 120, 600, 1800, 7200]` seconds; exemplars are disabled.
Metrics collect every 60 seconds and remain unsampled. Locally owned short traces
use parent-based 10% head sampling, with no ambient parent/baggage inherited by
their worker; diagnostic events are independent of sampling.

## Boundedness, cost and shutdown

Callers only validate, update bounded counters and try nonblocking queue admission.
Two fixed channels hold at most 1024 span records and 512 log records, each with
at most nine exported fields (below the 16-attribute allowance). Records serialize below
2 KiB including fixed resource/scope overhead; the transport rejects any larger
request. SDK serialization buffers have a fixed configured 750,000-byte cap,
the upstream minimum, independent of queue size. Each worker exports one record
per request (batch size 1, below the maximum 64). This favors a simple bounded
first path over throughput and can be revised with measured evidence.

Admission for each of traces and logs reserves 2 KiB per record from a monotonic
32 KiB/minute token bucket, with one minute of burst credit. A second identical
transport bucket charges each actual attempt, including SDK request retries, so
retries cannot bypass volume control. No durable or unbounded retry queue exists.
Metrics have twelve bounded streams and a 64 KiB request cap. Each HTTP attempt has
a three-second timeout. Slow, unavailable or rejecting export runs on owned
workers, independently of Delivery.

Loss counters have fixed reasons/dimensions. At most one fixed local message per
signal/reason is queued for the process lifetime (12 slots), with subsequent loss
visible through cumulative metrics. Local diagnostic I/O runs on its own worker;
even a blocked sink cannot hold record admission. Metrics export failure also has
a local diagnostic, since a failed tenant cannot deliver its own loss counter.
Unsent records and shutdown loss are expected: no replay, exactly-once or remote
acknowledgement guarantee is implied.

Graceful disposal completes admission, drains best effort, and cancels all HTTP
work at a total two-second deadline. It returns at that deadline even if a worker
is still settling cancellation. Force stop cancels immediately and returns without
waiting or flushing; subsequent provider cleanup cannot send a request after
cancellation. Providers, sources, meters and all three owned HTTP clients are
disposed when their workers settle. No shutdown path changes workflow authority.

The conservative translated-series inventory for **both** service identities is
`580 + 2 * (37 + 7 + 22 + 8*11 + 4 + 4*11 + 55 + 12) = 1118`: operations, histogram
(9 buckets including +Inf, sum, count), drops, failures, one `target_info`, then
one overflow point per dimensional counter and eleven per histogram, plus the
invocation, verified merge, cleanup and observation streams including their overflow, plus
confirmed outcomes, semantic repairs, phase allocation, occupied seconds,
classification duration, diagnostic results and operational retries. The new
bounded views include their overflow in this conservative inventory. See
[execution overhead](execution-overhead-telemetry.md) for dimensions and semantics.
This conservatively assumes both hosts use every stream; only runtime invokes
the Delivery instrumentation. Fixed service and
scope labels identify these series without multiplying combinations; the empty
resource builder adds no SDK/version/instance labels or SDK internal instruments.
This is below the 2,000-series design budget and Grafana Cloud Free's 10,000-series
allowance. Adding instruments/dimensions requires updating the closed schema,
views, inventory and tests together; unrecognized instruments fail closed.

At continuous full admission over 30 days, two hosts produce at most approximately
2.83 GB of protobuf payload **per signal** for traces/logs (plus an initial one-minute
burst), before transport headers. Normal host lifecycle traffic is far smaller.
Metrics cap at 5.66 GB/month of payload for two hosts; actual bounded traffic
is smaller and Free metrics allowance is active-series based. These are technical
ceilings, not billing guarantees or business accounting. Free tier constraints
remain 50 GB/month each for logs/traces and 14-day retention, as checked in the
Product on 2026-11-15. There are no account changes or paid resources here.
Alloy/Collector is a deferred production reliability option rather than an
installation dependency. The disposable first-path fixture includes a test-only
Collector and a reusable dashboard; no production exporter configuration changes.

## Verification and sources

`OperationalTelemetryTests` uses an in-process isolated loopback Kestrel receiver
and a small OTLP protobuf wire decoder to inspect all three signals, identities,
cumulative histogram buckets, caps, safe logs and canary exclusion. Listener tests
exercise activity decoration and same-name foreign meters; a zero-sampling test
proves metrics count all 1,000 high-cardinality detail events without ID labels.
Controlled held/error receivers prove redirects, authentication failure, request
timeouts, overload and graceful/force stop behavior. Token refill uses a manual
monotonic clock. These are component/network-boundary tests, with no additional
native process journeys. Existing FactoryTelemetryIntegrationTests and isolated
Aspire/Playwright smoke retain snapshot and local-output regressions.

The centrally pinned upstream `OpenTelemetry.Exporter.OpenTelemetryProtocol`
1.21.0 package provides .NET 10 assemblies. Configuration, serialization and
export use its SDK APIs, .NET ActivitySource/Meter and structured ILogger.

- [Microsoft: .NET observability with OpenTelemetry](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel)
- [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/)
- [Pinned exporter package](https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol/1.21.0)
- [OTLP protobuf schema](https://github.com/open-telemetry/opentelemetry-proto/tree/main/opentelemetry/proto)
- [Grafana Free limits](https://grafana.com/pricing/)
