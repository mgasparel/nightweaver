# Grafana delivery bottlenecks v1

Import [the version 2 dashboard](../../observability/first-path-dashboard.json)
with **Dashboards → New → Import → Upload JSON**. Its existing UID is retained
for upgrades. Select the Metrics (Prometheus), Traces (Tempo) and Events (Loki)
datasources from the dashboard selectors; no account-specific UID is embedded.
Only import into an account after that account's owner authorizes it. The
disposable local proof uses its own anonymous loopback Grafana.
See [local acceptance and retained evidence](../acceptance/grafana-bottlenecks.md).

The overview starts with mean invocation duration, verified closure and separate
cleanup. It then shows occupied allocation, capacity and waiting, attempts and
diagnostic overhead, rework and human pauses, and source/export quality. The
bottom detail rows and imported Explore links filter Bead/runtime/invocation IDs.
An unresolved or missing link is expected when sampling, budget, restart or
retention removed a detail. Do not add these IDs to metric or Loki stream labels.
Loki IDs must remain structured metadata. The local fixture's service_name is
the translated service.name resource, not a per-invocation stream identity.

## Panel interpretation

Every panel carries its units, denominator and limitations in its information
description. Counter window panels use increase; duration means use rate(sum)
divided by rate(count), applying reset handling before aggregation. Grafana's
rate interval follows the datasource scrape interval. A new short-lived stream
may lack two samples: window rate/increase remains no data even when a cumulative
sample exists. In Explore, inspect the cumulative counter to diagnose that case;
do not synthesize a zero or exact throughput. Increase is extrapolated, not an
exact count. Restart resets, missing exports and short runs limit interpretation.

| Panels | Meaning and completeness |
| --- | --- |
| Occupied time allocation | Completed reservation slot-seconds/second by pool and seven disjoint phases. Active reservations publish allocation at release. Parallel slots add slot time, not elapsed wall time. Preparation/finalization include coarse native work. |
| Live occupied / configured capacity | Reservations, including preparation and settlement, divided by configured slots per pool. Not Agent CPU utilization. |
| Eligible queue / oldest / freshness / coverage / observation age | Native observed eligibility, excluding paused, blocked, assigned, reserved and Planning backpressure. Queue/oldest require fresh=1 and coverage=1 on the same pool/job/instance. Initial absence, failed reads and >60-second gaps are unknown, not empty. |
| Completed observed eligible waits / lost coverage | Mean of complete observed intervals to reservation; startup and restart left-censored. Censor counts are diagnostic observations, not unique Beads. Normal discovery quantizes boundaries by 30 seconds plus read time. |
| Invocation duration / outcomes | Settled native invocation time includes Job/process/pipe settlement. Unsampled attempt metrics, distinct from successful publication; timeout and cancellation remain visible. |
| Classifier and verification diagnostics / classifier duration | Diagnostic call outcomes and mean primary/fallback/duplicate classifier seconds. A valid classifier answer is not work approval. These durations overlap the disjoint allocation and must not be added again. Verification/publication allocation is visible in the phase panel. |
| Operational retries / semantic repairs | Separate retry decisions from confirmed ChangesRequired receipts. Neither sampled traces nor log counts establish exact accounting. |
| Known-history first-pass / approval history coverage | FirstPass/(FirstPass+LaterPass) approved visits; Unknown is always alongside. Retries also make LaterPass. Restart/gaps cannot establish known history. No denominator means no ratio. |
| Observed human pause age / completed human wait / paused inventory | Fresh complete same-runtime observations. Oldest includes left-censored pauses; mean completed wait excludes them. Notes, abort and closure do not complete a wait. Label removal is not proof of an authorized Resume. |
| Verified closures / post-closure cleanup | Confirmed independent proof and guarded closure only. Queued cleanup is not finished; failed cleanup never subtracts merge success. |
| Web source age / health | Retained maximum age and resource counts by source/health, not subscribers. Initial unavailable has no age. A live connection does not prove a fresh source. |
| Exporter loss / lifecycle freshness and coverage | Cumulative drops/failures and explicit observation quality. Missing metrics cannot prove zero loss: check fixed local diagnostics when the receiver is down. |

No-data values stay unknown. Do not enable zero filling, automatic span-derived
metrics, per-ID labels, arbitrary resource enrichment or automatic instrumentation.
The details are short independently sampled traces (10% outside isolated QA) and
fixed events under admission limits; metric observations are unsampled. Neither
is a durable or exactly-once business ledger. Free signal retention is 14 days,
so long absences and long-running paused work cannot reconstruct whole history.

## Closed series budget

Run `node observability/build-dashboard.mjs` from the repository root after
changing the dashboard definitions. It also derives
[the complete inventory](../../observability/series-budget.json) from the closed
SDK views, failing if the inventory changes unexpectedly or exceeds 2,000 series.
The 38 instruments total **1,516** worst-case translated series for the supported
single-project runtime/Web pair, including one target_info per service and the
SDK overflow points. Every explicit histogram costs eleven series per point:
eight finite bounds plus +Inf, sum and count. Both services are conservatively
allowed every instrument even though Web does not execute Delivery.

Only fixed service.name resources and fixed scope labels accompany bounded
dimensions; no runtime/instance/project IDs churn. Backend job/instance labels
must identify this same stable pair. Multiple Projects, replicas or changing
resource labels require a fresh budget; they are not covered by 1,516. Disabling
then restarting retains the same series identities. Other account workloads
consume the shared remaining allowance.

## Volume and Free limits

Public [Grafana pricing](https://grafana.com/pricing/) rechecked 2026-11-20 UTC:
Free includes 10,000 active metric series, 50 GB ingested/month each for logs
and traces, and 14-day retention. These are public limits, not a reading of this
account's remaining quota. Inspect the account's current plan before use; do not
upgrade, enable paid features or change limits as part of setup.

Each process/signal reserves 2 KiB per admitted trace or log, at 32 KiB/minute
with one minute of initial burst. A second transport bucket independently charges
actual attempts, including SDK retries. Each actual encoded request including
resource/scope/envelope must fit 2 KiB. Two processes over a 31-day month therefore
have a conservative **2.925 GB per trace/log signal** ceiling
(`2 * 32768 * (44640 + 1) / 1e9`). This is below the 5 GB/month internal allocation
per signal. It includes protobuf envelopes and attempted retransmission payloads,
but excludes HTTP/TLS/IP overhead and is not Loki/Tempo billing accounting.
Metrics have a 64 KiB request cap and normal 60-second interval; they are budgeted
by translated active series, not the trace/log byte allowance.

The proof retains encoded receiver bytes, records, request maxima and failure
diagnostics for the native representative path and an accelerated 1,024-arrival
runtime/Web component load. The load uses 100% sampling and a 100ms metric interval
solely to inspect all streams quickly; production remains 10% and 60 seconds.
Its instantaneous burst extrapolation is explicitly labelled, not a steady
monthly usage estimate. Its measured maximum request size multiplied by sixteen
admitted records/minute/service projects the specified sustained workload.
The transport ceiling remains the conservative envelope for other allowed records.
Local estimates cannot prove actual hosted ingestion or account headroom.

Before hosted use, reserve at least this signal allocation within the **shared**
account quota, with room for other senders and backend accounting differences.
With authorized account access, inspect Cloud billing/usage and ingestion views
before the run, then again after ingestion settles; record service-filtered and
account-wide active-series and log/trace byte counters over a known interval.
Compare their extrapolation to the remaining monthly allowance, not just 50 GB.
Check dashboard loss counters and local diagnostics. Stop export if projected
usage approaches its allocated 5 GB/signal or shared remaining quota is smaller;
do not fix a quota shortfall by enabling paid ingestion. Free retention limits
history even when ingestion is inexpensive.

## Local secret setup and paired deployment

The runtime and standalone Web each own their exporter. Background Web inherits
the runtime environment and uses that same Web entry point. For a separately
launched Web, inject the same approved endpoint/protocol/enablement privately into
both process environments. Resources are fixed as nightweaver-runtime and
nightweaver-web. The dashboard does not depend on a runtime ID resource.
Publish both executables with the repository's existing
`scripts/Publish-Nightweaver.ps1` paired publisher and its manifest verification;
changing this feature does not authorize installation, production restart or
account changes. See [the publisher instructions](../../README.md).

Use the existing Free stack's OTLP connection page to obtain its exact HTTPS
base and a narrowly scoped write credential for metrics/logs/traces. Inject the
required Authorization entry through a local secret manager or a private process
environment. Never paste tokens or encoded headers into commands, source, Beads,
config snapshots, shell history, screenshots or logs. No real or encoded header
example is required here. Restrict local launch/secret files to the operator
account; do not make a checked-in .env or copy the whole environment to evidence.
Account credential creation also needs the applicable separate authority.

Set NIGHTWEAVER_TELEMETRY_ENABLED=true, OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf,
OTEL_EXPORTER_OTLP_ENDPOINT to the stack's HTTPS OTLP **base**, and
OTEL_EXPORTER_OTLP_HEADERS privately to the required Authorization entry. The
exporter appends /v1/metrics, /v1/traces and /v1/logs exactly once. Do not include
signal suffixes, userinfo, query or fragment in the base. Production must not set
NIGHTWEAVER_TELEMETRY_ISOLATED_QA; that flag enables loopback and 100% QA sampling.
Config files and command arguments cannot supply exporter settings. Invalid
enabled settings fail closed with fixed local diagnostics, leaving Delivery live.

Direct SDK v1 has bounded in-memory queues, no disk spool, finite drops and no
durable acknowledgement/replay. Export failures cannot block Delivery. Graceful
shutdown drains best effort for two seconds; force stop does not flush. Grafana
[documents direct export as a quickstart and Alloy for production reliability](https://grafana.com/docs/grafana-cloud/send-data/otlp/send-data-otlp/).
An Alloy rollout is deferred, requires its own safe configuration/budget and
cannot silently enrich or forward private fields.

Disable by removing enablement and credentials from the owning launch environment
for both processes, then use an independently authorized normal stop/start. An
environment edit does not reconfigure a running exporter. Verify new processes
send no OTLP and retain normal local snapshots/logs. Roll back to the previously
verified paired deployment/manifest using the existing deployment procedure; do
not delete operational evidence or change Bead workflow to disable observation.

## Reproduction and separately authorized hosted acceptance

Run in PowerShell 7 from the checkout:

```powershell
pwsh -NoProfile -File tests/telemetry-first-path/Run-Proof.ps1 -EvidenceRoot <fresh-absolute-directory>
```

Requires Docker, .NET 10, Node and the QA project's pinned Playwright/browser
installation. The immutable LGTM fixture publishes numeric loopback ports only,
never uses hosted credentials and removes its exact container in finally. The
native fake Agent plus disposable Git/bd proof remains the real process boundary;
new load/unavailable cases are component tests. Raw protobuf and fixture logs
stay in invocation tmp; retain only reviewed sanitized summaries/screenshots.
No Nightweaver console UI changed here; Grafana browser checks use Playwright.

Hosted acceptance is **pending, not authorized or performed**. After explicit
authority and secure credentials exist:

1. Confirm existing Free plan, account-wide remaining quota, scoped permission,
   retention and isolated test identity. Record starting usage without secrets.
2. Use a disposable local Target Repository, controlled fake Agent and separate
   runtime/Web processes; inject private credentials without printing environment
   or config contents. Never exercise production as the fixture.
3. Import this dashboard into the authorized existing stack and select its three
   datasources. Keep all IDs out of Loki stream labels. Do not enable automatic
   span metrics, paid resources, data enrichment or account upgrades.
4. Execute one representative isolated invocation and verified closure with the
   controlled proof. Confirm queryable metrics, independent correlated trace/event
   detail, quality/no-data states and separate cleanup. Sampling can leave a
   detail unresolved; use explicit isolated QA sampling only in the test process.
5. Inspect actual account/service ingestion after settlement, compare with the
   byte/series allocation and shared remaining quota, and record redacted query
   results, screenshots, tested revision, UTC interval and usage deltas. Recheck
   exported canaries and exporter-unavailable nonblocking behavior.
6. Stop/remove only the exact test processes/services and disposable local fixture,
   clear their secret environment, and revoke only test credentials when
   authorized. Preserve redacted evidence and verify teardown. Report observed
   outcome or blocker; local acceptance is never substituted for hosted success.

## v1 scope ledger

| Included | Deferred and why |
| --- | --- |
| Coarse preparation/finalization, seven disjoint occupied phases, live occupancy and configured capacity | Per-command Git/bd/lock cost needs additional instrumentation/cardinality review. |
| Observed eligible waiting, censor reasons, freshness, backpressure and bounded coverage | Durable reconciliation and exactly-once accounting require a durable ledger beyond observation-only v1. |
| Invocation settlement, retry/timeout, classifier diagnostics, verification/publication allocation | Agent-internal tools, remote-CI attribution and provider actual-model/token/cost are unavailable from trustworthy bounded host signals. |
| Confirmed outcomes, semantic repairs, known/unknown first-pass approvals and same-runtime human waits | Restart-spanning cycle/lead time and long-term quality trends cannot be reconstructed from lossy 14-day telemetry. |
| Verified closure independent of housekeeping, Web source health, exporter loss | Alloy buffering/transport resilience is a separate production tradeoff; no disk spool in direct v1. |

Telemetry never establishes readiness, ownership, Resume, publication or workflow
authority. Studio remains P1; this P2 dashboard uses ordinary factory gates.
