# Execution and host overhead

The opt-in [operational exporter](operational-telemetry.md) observes existing
ownership boundaries without changing scheduling, cancellation, claims or guarded
publication. Export still uses bounded queues, finite schemas, OTLP HTTP/protobuf,
short ActivitySource spans, Meter instruments and fixed structured ILogger events.
No prompt, classifier evidence, model answer, exception message, URL, command,
path or log body enters these records. Local invocation logs remain unchanged.

Each Planning, Delivery and Merger reservation owns one monotonic phase clock
from capacity reservation until local capacity release. Its seven accumulators
are disjoint: preparation, agent, classification, verification, publication,
finalization and other. Nested scopes replace the current allocation, then
restore it. Native lifecycle execution includes process/job, pipe and log
settlement; classifier native execution remains classification overhead.
Preparation includes claim admission, provisioning, prompt preparation and retry
preparation. Finalization includes owned refusal/claim settlement, human-pause
settlement and post-merge housekeeping. Other covers uninstrumented coordination
and time waiting to start the reserved task. Exception and cancellation unwinds
restore the enclosing phase; idempotent reservation disposal freezes all totals.
Concurrent reservations have independent async contexts and fixed-size arrays.

Claim admission ends after the confirming claimed snapshot. Pool wrappers do not
hold a preparation scope across the slot: interstage inventory, readiness and
ownership coordination restores Other between the individually owned operations.

Allocation is exported once at release, including zero-duration phases. Active
reservations are therefore absent from these completed-reservation totals until
they settle. Process termination may lose an unfinished allocation. No timer is
kept after release. This is observed occupied slot time, not an eligibility wait
or capacity gauge, and sums across slots may exceed elapsed wall time.

| Instrument | Dimensions | Meaning |
| --- | --- | --- |
| `nightweaver.phase.seconds` | pool, phase | Unsampled cumulative disjoint allocation in seconds |
| `nightweaver.phase.duration` | phase | Per-reservation allocation histogram, including zeros |
| `nightweaver.slot.occupied` | pool | Unsampled cumulative reservation seconds; reconciles with phase seconds |
| `nightweaver.classification.duration` | kind | Diagnostic operation histogram: LifecyclePrimary, LifecycleFallback, DuplicateFailure |
| `nightweaver.workflow.results` | kind, outcome | Completed diagnostic operations; success, unavailable, invalid, cancelled, refused, uncertain |
| `nightweaver.published.outcomes` | role, outcome | Lifecycle outcomes only after confirmed publication receipts, including Planning |
| `nightweaver.operational.retries` | reason | Operational retry preparation starts, using finite operational-failure kinds |
| `nightweaver.semantic.repairs` | role | Confirmed ChangesRequired publications; separate from operational retries |

Nine operation kinds cover preparation, primary/fallback/duplicate classification,
post-exit/outcome verification, lifecycle/Planning publication and finalization.
Their short `workflow` spans and fixed completion logs include kind, diagnostic
outcome, runtime ID and, within a reservation, available Bead/latest lifecycle
invocation IDs. Preparation may precede an invocation identity. A fallback's own
native invocation has its separate existing invocation record. IDs never enter
metric dimensions or span names. No cross-stage root span or ID cache is added.

Classification success means a valid diagnostic answer, including an unclear or
negative answer. It establishes no lifecycle transition. Missing/failed primary
transport is unavailable; malformed primary answers are invalid. Invalid fallback
answers are invalid; external cancellation remains cancelled. Verification refusal
is distinct from unconfirmed publication. A lost publication response counts as
success only when the existing authoritative reread confirms its receipt. Failure
or refusal produces diagnostic observations without a published-outcome count.
Confirmed merge closure retains its separate dimensionless verified-merge signal;
cleanup failure cannot revoke it. Housekeeping uncertainty is recorded separately.
Observation can be lost or duplicated across restart/transport; none of these
metrics is exactly-once business accounting or workflow authority.

Use Grafana Explore with the existing Metrics datasource. Prometheus translates
the counters to `nightweaver_phase_seconds_total`,
`nightweaver_slot_occupied_seconds_total`, `nightweaver_workflow_results_total`,
`nightweaver_published_outcomes_total`, `nightweaver_operational_retries_total`
and `nightweaver_semantic_repairs_total`. For a selected interval:

```promql
sum by (pool, phase) (increase(nightweaver_phase_seconds_total[$__range]))
sum by (pool) (increase(nightweaver_slot_occupied_seconds_total[$__range]))
sum by (kind) (rate(nightweaver_classification_duration_seconds_sum[$__rate_interval]))
 / sum by (kind) (rate(nightweaver_classification_duration_seconds_count[$__rate_interval]))
sum by (kind, outcome) (increase(nightweaver_workflow_results_total[$__range]))
sum by (reason) (increase(nightweaver_operational_retries_total[$__range]))
sum by (role) (increase(nightweaver_semantic_repairs_total[$__range]))
```

Reconcile raw cumulative phase and occupied totals from the same completed
reservations. Independently extrapolated Prometheus `increase` windows, exports
straddling a release or process resets can differ. Operation spans and histograms
are overlapping diagnostic breakdowns: never add primary, fallback, verification
or publication durations to the disjoint phase total. Metric counts are unsampled;
trace sampling and bounded log admission do not reduce them. Each exporter also
checks its own sampling decision before serialization, preventing another provider
listening on the same fixed ActivitySource name from upgrading zero sampling.

Granular Git/bd command, subprocess argument, lock wait and Agent sub-tool timing
remain deferred. Remote CI waiting inside Merger is Agent time in v1. Provider usage and broad bottleneck dashboards belong to later slices;
there is no new Studio UI or production/hosted account setup in this slice.

`WorkflowTimingTests` uses an injected monotonic clock for nesting, concurrent
contexts, exception/cancellation reconciliation and idempotent closure. Existing
fake classifier and publisher fixtures verify primary/fallback diagnostics,
private canaries, confirmed/lost/refused writes and absence of unconfirmed business
success. In-process OTLP receivers check the real wire schema and zero sampling.
Classifier acceptance waits for each expected primary/fallback kind before
best-effort shutdown, reporting observed kinds and bounded loss diagnostics on
failure. A controlled overlap starts the privacy-test ActivityListener between
primary export and fallback completion. Privacy decoration targets a unique
fixture detail ID because listeners are process-wide; other concurrent exports
must remain observable. Decorated owned records still fail closed. Export queues,
shutdown budgets and the six-thread test schedule are unchanged.
Existing executor, retry, Planning, verifier and pool suites retain native timeout,
launch, publication and cleanup boundaries. No native journey is added.

Live pool capacity, phase occupancy and approximate eligibility waiting now use
[pool observations](pool-observation-telemetry.md); completed allocation totals
above keep their existing release-time semantics.
