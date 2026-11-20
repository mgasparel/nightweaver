# Grafana bottleneck dashboard acceptance

The version 2 [dashboard](../../observability/first-path-dashboard.json) extends
the existing first-path UID into 29 overview/detail panels. The
[operator contract](../design/grafana-bottlenecks.md) defines units, denominators,
coverage, retention, safe paired setup, disable/rollback and the v1/deferred ledger.
Hosted acceptance remains **pending, not authorized or performed**. No account,
paid resource, production installation or restart was changed.

## Local boundary and evidence

The pinned disposable `grafana/otel-lgtm` fixture receives actual OTLP protobuf
from the real fake-Agent/native Git/bd closure smoke, followed by two bounded
runtime/Web component scenarios (1,024 parallel arrivals, successful receiver and
HTTP 503 receiver). The native smoke is reused, not added to the default suite.
The new component collection measured 0.636 seconds in the retained focused XML;
the surrounding MTP command also includes the existing shared fixture setup.

The probe imports the actual dashboard, queries every Prometheus target, checks
translated metric names, observes stale/failed freshness with retained queue
samples hidden by the positive gate, follows both imported ID Explore links, and
renders overview/rework/details with Playwright. Actual Grafana datasource
responses and direct backend results are retained. The local datasource uses a
one-second step and five-minute view for the accelerated 100ms fixture; the
normal exporter remains 60 seconds. Unknown first-pass history and omitted queue
gauges remain unknown. There are no substituted HTTP/browser responses.

Sanitized local evidence is retained in [grafana-bottlenecks](grafana-bottlenecks/):

- Overview, rework and both Explore screenshots.
- Every panel's query/result and the actual Grafana datasource requests/results.
- Received native signal summary, paired load/503 byte summaries and pinned
  fixture identity plus exact teardown confirmation.

Raw native logs and protobuf stay in invocation tmp at
`H:/Nightweaver/prod/tmp/nightshift/ns-5gg-p8c572d3f60439d2b8870/20261120T0142286268460Z-implementer/`.
The successful final visual proof is `dashboard-proof-6`. Earlier diagnostic
attempts remain there: missing Playwright dependencies, a doubled metric unit
suffix caught by the real backend, and the local datasource query-step mismatch.
They are not represented as successful visual acceptance. Every disposable
container is removed in its own finally block.

The existing native proof injects private prompt/output canaries and rejects
them in all three signals. The component cases additionally put a private
description/path canary through the lifecycle seam, reject it in all captures,
check IDs stay out of aggregate metrics, enforce actual request caps and observe
fixed failure diagnostics with an unavailable receiver. The retained default
privacy tests cover credential/config/log/exception and foreign-listener exclusion.
No real credential was used or copied into evidence.

## Approval-ratio regression proof

The generated query derives a missing FirstPass numerator's zero from the
observed known-approval denominator, matched by role, and divides only where
that denominator is positive. LaterPass-only approvals therefore show 0%;
absent or zero known approvals remain unknown, and Unknown never enters the ratio.

`node tests/telemetry-first-path/approval-ratio-proof.mjs <fresh-evidence-directory>`
evaluates the generated panel expression with the pinned fixture image's real
Prometheus promtool, using eight deterministic cases: later-pass only, mixed
known approvals (25%), first-pass only (100%), Unknown only, no approvals, zero
known denominator, Unknown exclusion and role isolation. `Run-Proof.ps1` also
runs this fixture. It starts no server and adds no native Agent journey.

Retained before/after evidence is under invocation
`H:/Nightweaver/prod/tmp/nightshift/ns-5gg-p8c572d3f60439d2b8870/20261120T0211237457541Z-implementer/`
in `ratio-before` and `ratio-after` (`approval-ratio.yml`, `promtool.log` and
`result.json`). The retained query at `76b85a6f1aa0423a48e4a10809819228fda4728d`
fails three cases: missing later-pass-only zero, NaN for zero denominator and
missing the later-pass role in the isolation case. The repaired query passes all
eight cases. Existing import/render/correlation/privacy and unavailable-exporter
evidence above is reused; this repair changes only the panel query and description.

## Cost and limitations

The generated closed inventory covers all 38 instruments, including explicit
histograms, overflow and fixed resource identities: **1,516 translated series**
for a single runtime/Web pair, below the 2,000 internal allocation. There are no
per-runtime resource labels or automatic span-derived metrics.

The paired accelerated load measured maximum encoded requests of 419 trace bytes
and 440 log bytes. Sixteen admitted records/minute/service for a 31-day month
projects **0.599 GB traces and 0.629 GB logs** with those measured sizes. The
conservative full-request transport cap is **2.925593 GB per signal**, including
one-minute initial burst and attempted retries. Both are below 5 GB/signal.
The metrics request maximum was 8,478 bytes; metrics quota is active-series based.
In the summaries, metric `records` counts instrument records, while trace/log
`records` counts spans/events. These are not backend-billed bytes or metric points.
Instantaneous burst extrapolation is explicitly labelled and is not claimed as
sustained workload or hosted usage. Other allowed record shapes use the
conservative cap, not the measured smaller-record estimate.

The 503 fixture receives and rejects the captured requests; all three signals
report fixed export-loss diagnostics while 1,024 observations per service settle.
Per-signal rejected request counts are the corresponding `requests` fields in
`unavailable.json`; no backend acceptance is inferred. Bounded drops are expected
under pressure and do not change Delivery or workflow authority.

Public Free limits were independently rechecked; account-specific quota was not
accessed. See the separate hosted checklist and usage-check procedure in the
operator contract. Short fixture counters can yield zero extrapolated increases
or no mean when no increment was observed after their initial sample; cumulative
verified closure and correlated details supply the smoke's exact local oracle.
No dashboard counts are promoted to exactly-once business accounting.

The implementation Bead comment records the clean exact candidate revision,
complete six-thread suite counts and outer stopwatch measurement against the
explicit retained accepted main baseline. Different invocation temp roots make
strict automatic historical comparison unavailable, never zero delta. Latest
main after integration and final exact-head hosted checks remain Merger gates.
