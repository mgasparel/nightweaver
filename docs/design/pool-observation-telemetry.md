# Observed eligibility and pool capacity

The opt-in operational exporter consumes each pool's existing successful fresh
native admission selection. It performs no additional tracker commands and never
changes candidate order, reservation, publication, pause or ownership authority.
Planning backpressure is the existing active-Plan threshold gate; it is reported
separately and its candidates do not enter the capacity-wait queue.

| Instrument | Dimensions | Meaning |
| --- | --- | --- |
| `nightweaver.pool.capacity` | pool | Configured reservation limit |
| `nightweaver.pool.occupied` | pool, phase | Live owned reservations, one phase per reservation |
| `nightweaver.pool.queued` | pool | Observed eligible unreserved count; absent when unknown/incomplete |
| `nightweaver.pool.oldest` | pool | Oldest observed queued interval in seconds; absent when unknown/incomplete |
| `nightweaver.pool.fresh` | pool | 1 for a successful observation no older than 60 monotonic seconds; otherwise 0 |
| `nightweaver.pool.coverage` | pool | 1 for fresh complete retained membership, otherwise 0 |
| `nightweaver.pool.age` | pool | Monotonic seconds since the last successful observation; absent before any success |
| `nightweaver.pool.backpressure` | pool | Planning admission gate closed (1); absent when stale |
| `nightweaver.eligible_wait.duration` | pool | Seconds to reservation, only for continuously observed complete intervals |
| `nightweaver.eligible_wait.censored` | pool, reason | Lost or incomplete intervals/observations, never successful wait samples |

Pool labels are planning, delivery and merger. Phases use the seven existing
[execution-time allocations](execution-overhead-telemetry.md). Reserved work before
its task starts is Other, owned preparation is Preparation, and settlement remains
occupied through local capacity release. Live phase snapshots read each reservation
once, preventing phase transitions from double-counting a slot. Existing disjoint
phase-seconds and occupied-seconds counters still publish completed allocations at
release; they include preparation and settlement. They are not live gauges.

Waiting begins at fresh observed admission eligibility, excluding locally reserved
work and all native admission refusals (paused, assigned, deferred, blocked,
publication-held or wrong stage). Repeated polls retain the original timestamp.
Reservation removes the record immediately; an observed eligibility loss censors
it. An environment reservation refusal removes it as ReservedElsewhere. Cross-pool
handoff starts a new interval in the receiving pool and never carries elapsed time.

Startup/restart and recovery from failed or stale reads are left-censored: records
present in the first successful observation cannot produce complete durations.
Later newly eligible records may complete. Failure clears intervals immediately.
A gap over 60 seconds clears them before either export, reservation or recovery;
unobserved time never enters a complete sample. Shutdown closes waiting coverage
when the next collection sees the existing stop gate, while draining owned slots
remain occupied. Process termination loses unfinished observations.

Discovery normally quantizes eligibility by up to 30 seconds, plus native read
latency; failures add further delay. The 60-second freshness limit allows one normal cadence
plus bounded read/scheduling latency. Longer gaps conservatively become stale
and restart left-censored. Time between successful
polls is approximate observed eligibility, not proof of continuous business
eligibility. Even fresh zero is an observation, never an authorization.

Each runtime retains at most 3,333 waiting records per pool (9,999 total), sharing
one 10,000-record ceiling with [lifecycle observations](lifecycle-observation-telemetry.md).
At capacity it keeps existing intervals and rejects additional
records, counts Capacity loss, and omits queue/oldest values until a complete fresh
membership fits. No eviction can fabricate a completion. Censor reasons are the
finite enum Ineligible, Failure, Stale, Capacity, Shutdown, ReservedElsewhere and
LeftCensored. Repeated rejected observations can increment Capacity repeatedly;
these counts are diagnostic observations, not unique Bead accounting.

These streams use the existing bounded OTLP HTTP/protobuf exporter and are
unsampled. No Bead, runtime, invocation or slot ID is a metric dimension. The
additional conservative series budget for both service identities is
`2 * (4*11 + 22 + 22 + 7*4) = 232`, including overflow. Lifecycle observation
adds 166 more, for 1,516 total.

Grafana Explore can compare `sum by (pool) (nightweaver_pool_occupied)` with
`nightweaver_pool_capacity`, break occupancy down by phase, and inspect queued and
oldest alongside fresh, coverage and age. Gate queue panels on fresh and coverage;
Prometheus may retain an earlier sample when an absent gauge is omitted. Never
coalesce unknown to zero. Use the wait histogram for sampled completed intervals
and censor counters to explain its coverage, rather than claiming total backlog
age or exactly-once business latency. No Studio UI or account/deployment changes
are included.

Controlled coordinator tests prove selection, full capacity, active exclusion,
publication/authorization gates, failed reads and lost readiness with unchanged
read counts. Deterministic helper tests cover monotonic gaps, restart, capacity,
handoff, phase ownership and shutdown; an in-process OTLP receiver checks unsampled
wire signals and private-ID exclusion. Existing pool concurrency native journeys
remain the process/tool regression boundary; no new native journey is added.
