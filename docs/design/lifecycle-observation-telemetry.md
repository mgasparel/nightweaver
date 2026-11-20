# Observed rework, approvals and human waiting

Telemetry observes Beads; it never authorizes scheduling, resumption, ownership or
publication. No durable lifecycle ledger is introduced. The Delivery discovery
pass submits its existing fresh complete inventory, including Products, before
selection. Failed reads invalidate observation immediately. There are no new bd
commands, classifiers, native journeys, provider/model/token/cost inferences or
Studio changes.

Confirmed `DeliveryOutcomePublisher` receipts (including exact confirming rereads)
and `PlanningPublisher` final planned-state verification record
`nightweaver.published.outcomes` by finite role/outcome. Only confirmed
ChangesRequired receipts increment `nightweaver.semantic.repairs`; operational
retry and classifier diagnostics retain their separate instruments. Approved
graph materialization is a `planning_published` milestone, not another Planner
approval. Verified merge closure and cleanup retain their separate streams.

`nightweaver.approvals{role,coverage}` counts verified approvals with FirstPass,
LaterPass or Unknown coverage. FirstPass requires an observed role-visit boundary,
one settled observed invocation, and known zero prior semantic repairs. LaterPass
includes repeated operational attempts as well as nonzero semantic repair history;
use semantic.repairs to distinguish the two. The ratio numerator is FirstPass;
the denominator is FirstPass + LaterPass. Always show Unknown alongside it.
An initial or newly visible Bead, reset counter, malformed workflow, missing
attempt, stale read, restart or lost cache history cannot establish first-pass
history. Confirmed predecessor publication establishes the next visit boundary.
Malformed observations retain the last valid stage and observed attempts while
making the visit unknown. Recovery to the same stage preserves that unknown
coverage; a valid stage change or confirmed predecessor publication is required
to establish a new visit.
Counts describe observed publications, not exactly-once business accounting.

| Instrument | Dimensions | Interpretation |
| --- | --- | --- |
| `nightweaver.human_wait.duration` | pause | Completed continuously observed unpaused-to-paused-to-unpaused interval, seconds |
| `nightweaver.lifecycle.paused` | pause | Currently observed paused count |
| `nightweaver.lifecycle.oldest` | pause | Oldest observed pause age, seconds; includes left-censored intervals |
| `nightweaver.lifecycle.fresh` | none | Successful observation within 60 monotonic seconds |
| `nightweaver.lifecycle.coverage` | none | Fresh complete retained inventory membership |
| `nightweaver.lifecycle.age` | none | Seconds since last successful inventory; absent before success |
| `nightweaver.lifecycle.censored` | reason | Partial or lost observations, not unique Beads |

Pause values are NeedsHuman, ProductDecision and Combined. Both labels yield one
Combined interval, independent of the authority parser's refusal of contradictory
workflow state. Label-set changes censor the old interval and begin a new segment.
Only observed pause removal completes an interval; comments and Save Note cannot.
External removal is an observation, not proof of a console Resume action. Abort or
close retaining pause labels, deletion and visibility loss do not complete waits.
Reopened/newly visible and startup-paused records are left-censored. Failure,
staleness, capacity loss and shutdown discard completeness; no restart-spanning
duration is synthesized. UTC clock changes do not affect monotonic ages/durations.
Discovery quantizes boundaries by its normal 30-second cadence plus read latency.

Eligibility and lifecycle caches share one runtime-wide 10,000-record ceiling;
eligibility retains its 3,333 per-pool limit. At exhaustion existing records remain,
new records are rejected, coverage becomes incomplete, and successful duration
coverage is conservatively lost. Removal releases shared capacity. Reasons are
Failure, Stale, Capacity, Missing, Closed, Changed, LeftCensored and Shutdown.
Counts/oldest are absent when stale/incomplete. Grafana panels must gate on fresh
and coverage because Prometheus may retain earlier samples; unknown is never zero.

Fixed `lifecycle` milestone records carry UTC observation time, provenance
`confirmed_publication` or `observed_snapshot`, Bead/runtime IDs and, for confirmed
role publication, invocation/role IDs. Snapshot events cover observed pause entry,
label change and exit. Correlate by Bead plus runtime/invocation, allowing missing
or unresolved links. No span stays open through a human wait. No total Bead lead
time or whole-cycle duration is claimed. Existing verified-merge/outcome comments
remain local evidence; export, crash and sampling can lose milestones. Metrics
remain unsampled, IDs occur only in details, and prompts, descriptions, guidance,
source, transcripts and model evidence are never submitted. The existing bounded
queues, admission budgets and strict OTLP HTTP/protobuf transport remain in use.

The additional conservative budget for both service identities is
`2 * (19 + 4*11 + 9 + 4 + 4 + 3) = 166` series, giving 1,516 total, below the
2,000 internal allocation and Grafana Cloud Free's 10,000 active-series allowance.
Free retention is 14 days; loss/retention mean this cannot serve as an accounting
ledger. No account, paid provisioning, hosted acceptance or production rollout
is performed by this slice. Existing native workflow journeys remain the authority
oracle; deterministic helper/component and loopback wire tests cover this slice.
