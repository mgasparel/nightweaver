# Invocation to verified merge

The opt-in [operational exporter](operational-telemetry.md) observes one bounded
Delivery path. `AgentInvocationExecutor` starts timing immediately before the
native launch attempt and records after process/job, pipes and local log settlement.
The short `invocation` span carries the measured interval even when export is
queued. Failed launch, failure, cancellation and timeout remain separate from
`completed` (exit zero); none establishes a successful Delivery outcome.
Preparation failures and launches denied by shutdown do not count as attempts.

`MergerOutcomeVerifier` still owns independent current-invocation PR, required
check and default-branch reachability proof. Only `MergerCompletionFinalizer`'s
confirmed guarded task close emits `nightweaver.verified_merge`, a short
`verified_merge` span and the fixed `VerifiedMerge` structured ILogger event.
An uncertain close uses the existing single fresh reread; no close is replayed.
Guard mismatch, unconfirmed close, rejected evidence and already-closed startup
inventory emit no verified success. Direct housekeeping also emits no success.
Emission precedes housekeeping, so failed cleanup or cancellation afterward
cannot remove or reclassify a confirmed success. `nightweaver.merge.cleanup`
records `completed`, `queued`, `failed` or `cancelled` separately. Queue admission
is not cleanup completion; Product completion is also independent.

The owned session has one atomic emission flag, with no global ID cache or
durable deduplication store. It prevents duplicate emission within that completion
path, including an uncertain-write confirmation. Telemetry remains observation:
restart or transport loss/duplication is possible. Counters are cumulative for
the process, not exactly-once accounting or a durable count of all closed tasks.

Runtime scope flows through owned async work and is restored on disposal. The
export workers suppress its execution context, Activity.Current and baggage.
Every operation creates its own short trace; no trace state enters Agent prompts,
stdin or launch environment. Invocation context is not retained after asynchronous
queue export, so a later merge has no ActivityLink to an available invocation
context. Correlation uses `bead.id`, `runtime.id` and `invocation.id` in details;
`checked.head` and `merge.commit` appear only on verified merge details. There
is no indefinitely open Bead trace or cross-restart trace cache.

Detail IDs are bounded to 96 ASCII letters/digits/dots/hyphens, runtime IDs to
32 hex digits, and revisions to 40/64 hex digits. No arbitrary strings, exception
messages, paths, commands, prompts, output or secrets are accepted. These fields
never become metric dimensions. Seven roles are allowed: planner,
plan-challenger, implementer, reviewer, qa, merger and classifier. The diagnostic
outcome-classifier maps to classifier; it cannot increment verified merge.
Attempts have six statuses: completed, failed, cancelled, timed_out,
launch_failed and unknown. Invocation latency has only role. Verified merge has
no dimensions; cleanup has four outcomes. All existing queue, payload, sampling,
loss and shutdown limits apply. The conservative two-host inventory is 390
translated series, including overflow, below the 2,000-series design budget.

## Dashboard and local proof

Import [first-path-dashboard.json](../../observability/first-path-dashboard.json)
into Grafana. Metrics, Tempo and Loki datasource UIDs are configurable datasource
variables. Only the detail panels use Bead/runtime/invocation ID filters. The
metric panels show mean observed invocation duration, cumulative confirmed merges
and distinct cleanup state. The event panel and trace table expose the same
details; both Explore links preserve the selected operational IDs. Loki's native
OTLP translation normalizes dots to underscores; Prometheus translates metric
names to underscores and adds `_seconds`/`_total` as appropriate.

For disposable proof, start a local Docker engine, install the repository-pinned
Playwright tools (`npm ci` and `npx playwright install chromium` from
`tests/Nightweaver.QA.AppHost`), then from the checkout run PowerShell 7:

```powershell
pwsh -NoProfile -File tests/telemetry-first-path/Run-Proof.ps1 -EvidenceRoot <fresh-absolute-directory>
```

The fixture pins Grafana's development-only LGTM image by digest in
`tests/telemetry-first-path/fixture.json`, binds dynamically assigned ports on
numeric loopback, and imports the exact dashboard JSON. It uses native Agent,
Git and bd fixtures with controlled hosted PR responses and a local bare origin;
it performs no actual remote merge. A recording receiver forwards the sanitized
OTLP protobuf to the test-only Collector. The same native OTLP metric/log/trace
translations feed local Prometheus/Loki/Tempo. Browser QA follows the imported
Explore links and captures the rendered dashboard; API queries retain actual
metric values, matching event, trace IDs and trace details. All fixture services
are removed in `finally`. No account writes, real tokens, hosted Grafana
acceptance, paid provisioning or production deployment are part of this proof.

The normal suite includes one native OTLP smoke using the existing immutable
AgentStage template. Its uncovered boundary is native launch/settlement through
sanitized OTLP export and independently verified guarded closure. Existing
executor tests prove containment; verifier tests prove individual evidence rules;
neither proves exported correlation across both boundaries. Seven deterministic
finalizer cases cover detailed closure/cleanup decisions without native journeys.
Backends and browser QA run only through the explicit script, outside the normal
suite. Classification/verification breakdowns, queue/observation freshness,
provider usage and broad bottleneck dashboards remain deferred.

Configuration sources: [Grafana's disposable LGTM backend](https://grafana.com/docs/opentelemetry/docker-lgtm/),
[Loki OTLP translation](https://grafana.com/docs/loki/latest/send-data/otel/),
[Tempo TraceQL queries](https://grafana.com/docs/grafana/latest/datasources/tempo/query-editor/),
[Prometheus OTLP ingestion](https://prometheus.io/docs/guides/opentelemetry/).
