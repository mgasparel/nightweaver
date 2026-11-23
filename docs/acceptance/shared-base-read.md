# Shared base read acceptance and measurement

The [v1.3 amendment](../design/operational-observations-v1.md#amendment-v13--shared-base-and-independent-sections-ns-gjwx)
defines the next read contract. This document defines acceptance and measurement;
it reports no implemented latency improvement. Baseline capture and candidate
verification belong to the subsequent ns-gjwx delivery slices. Keep the current
CLI adapter, journal and backup audits for the first comparison. Do not set a
numerical latency target until the comparable measurements exist.

## Source and consumer trace

These records at `1b6e378e357c158d646648b4bd5b55c6a1f97324` define the retained
behavior. New keys add partial-page publication; existing full responses stay valid.

| Contract fact/state | Retained producer or record | Consumer/acceptance boundary |
| --- | --- | --- |
| Base ID, title, status, type, priority, labels, assignee, times and workflow | `BeadSnapshot`; `BeadExplorerItem`, `BeadWorkspaceBead`; `AttentionDeskProjector`; `BeadPresenter` | Overview counts/cards, Explorer page, Attention membership, Workspace header and Digest base rows |
| Native Ready/Blocked membership | `BeadsReader.ReadExplorerReadinessAsync` / `ReadExplorerBlockedAsync`; `TrackerBridge.IsInView` | Explorer membership, independent source health; no dependency-count inference |
| Window, checkpoint, metrics and lineage | `CatchUpDigestResponse`, `CatchUpDigestMetrics`, `CatchUpDigestThread`, `CatchUpDigestItem`; strict checkpoint read | Overview Digest summary and Catch Up; same membership/order/counts before and after enrichment |
| Text | `BeadWorkspaceBead` prose and `BeadWorkspaceResponse.SourceMetadata` | Workspace Summary text loads after header facts |
| Complete history, selected explanation and confirmed empty history | `BeadWorkspaceComment`, `BeadWorkspaceHistory`, `AttentionDeskItem`; `BeadWorkspaceProjector.ProjectHistory` | Workspace History and Attention reports; never select a report from an incomplete history |
| Relations and directional health | `BeadWorkspaceRelation`, `BeadWorkspaceRelationObservation`; qualified combined incoming read and native outgoing read | Workspace relation section; one failed direction cannot make the other complete or empty |
| Environment and Output | `BeadWorkspaceEnvironment`; existing `/api/telemetry/stream` | Workspace environment and invocation-bound Output remain separate from base freshness |
| Verified merge, unavailable evidence and separate cleanup warning | `DigestMergeEvidence.Read` / `Parse`; matching host/agent records | Catch Up enrichment; see [merge acceptance](digest-merge.md) |
| Loading, pending, stale, failed and unavailable | `ObservationEnvelope`, `ObservationSourceState`, `ObservationSample`; `TrackerSourceSchedule` | New section descriptor states refine existing envelope health; initial null never means empty |
| Epoch/revision, admission and pending generation | `OperationalObservationService`, `ObservationMemoryBudget`; Studio observation coordinator | Reject obsolete responses, retain readable data truthfully, preserve drafts and focus |

Record declarations are in [Workspace](../../src/Nightweaver.Web/BeadWorkspace.cs),
[Attention](../../src/Nightweaver.Web/AttentionDesk.cs),
[Explorer](../../src/Nightweaver.Web/BeadsExplorerService.cs),
[Digest](../../src/Nightweaver.Web/CatchUpDigest.cs),
[merge evidence](../../src/Nightweaver.Web/DigestMergeEvidence.cs), and
[observation contracts](../../src/Nightweaver.Web/OperationObservationContracts.cs).
Collection and publication currently occur in
[OperationalObservationSource](../../src/Nightweaver.Web/OperationalObservationSource.cs)
and [OperationalObservationService](../../src/Nightweaver.Web/OperationalObservationService.cs).
The new contract changes that publication boundary; these links do not assert
that the retained implementation already supports v1.3 keys.

## Functional acceptance

| Scenario | Required observation |
| --- | --- |
| Cold Overview and direct Workspace link | One shared complete base inventory supplies useful facts. Base publication precedes deferred completion. No per-Bead base fallback command occurs. |
| Missing ID at bootstrap, pending refresh and source failure | Show loading/unavailable or retained stale data. Only a successful complete shared validation with a current inventory generation supplies absence proof. Test deletion and later recreation too. |
| Warm navigation and two consumers of one section | Reuse base and the same validated section within capacity. Duplicate readers share one in-flight source read. A caller cancellation does not stop host-owned work for another reader. |
| History empty, delayed, failed and changed | Empty requires a complete successful source and identity check. Delayed/failed work has its own state. Equal inventory counts or `updatedAt` do not suppress independent history validation. |
| External comment and dependency change | Journal hints invalidate the correct sources. Backup audits recover missed hints. Check actual comments, both relation directions and native Ready/Blocked membership. |
| Pause then Resume, closure then reopen, deletion, claim change | New base membership/action eligibility wins immediately. Old explanation or merge evidence stays historical. A stale guarded command receives the existing authoritative refusal. |
| Competing Workspace, Attention, Digest, refresh and guarded write | Selected missing sections, due core refresh and guarded writes each advance. Neither base nor selected detail waits for the whole unrelated cohort. Keep native budgets, settlement and eight-write observer fairness. |
| TTL, LRU, admission failure and late callback | Stay within shared byte/key/history limits. Report limits explicitly. Old admission/epoch results cannot populate new entries. |
| Closed task before merge acquisition | Show closure first. Show Merged only with matching verified records; unacquired evidence remains unavailable. Keep all window members/counts beyond the 128-task acquisition cap. |
| Existing full-response HTTP and Studio consumer | Payload shape, complete-source validation, sort order, error semantics and guarded command endpoints remain valid. New base payloads occur only on new keys. |

Use cheap deterministic source/service/coordinator tests for state matrices,
generation races, finite progress, deduplication and eviction. Reuse existing
native tool/HTTP journeys for their boundaries. Justify and measure any additional
native coverage as required by [test cost](../agents/test-cost-evidence.md).
Use the [isolated Aspire and Playwright workflow](../../tests/Nightweaver.QA.AppHost/README.md)
for representative live browser acceptance. Stop the exact stack in `finally`.
Check readable compact layouts at 1440, 720 and 375 pixels, keyboard access,
section loading announcements, no overflow and preserved focus/drafts.

## Measurement events

Use a monotonic clock per process. Store event offsets, process clock identity,
scenario and repetition ID. Correlate cross-process events by explicit operation
IDs; do not subtract unrelated clocks. Record unavailable attribution as null
with a reason. Preserve raw timing traces and exact artifact hashes.

| Measure | Start | Finish and required evidence |
| --- | --- | --- |
| Useful content | Browser navigation start, or route action for same-tab navigation | First rendered required base facts with exact fixture IDs/counts and source state. Overview requires tracker summaries plus independent runtime state, which can be explicitly unavailable. Explorer requires the requested native view/page. Attention requires paused membership. Workspace requires ID/header/workflow facts. Catch Up requires window/counts/membership. Shell, HTTP 200, empty pending envelope or loading indicator is not useful content. Record each independent panel as well as the page milestone. |
| Detail completion | Same navigation/route start; also record each section's request/admission time | Each requested section reaches available/empty after identity validation. Record each section and the last requested section separately. Failed/unavailable/stale is a reported incomplete result, not a completion success. Include section-request-to-completion time. Output completes only the requested retained invocation range, never an endless live stream. |
| External-change visibility | Immediately before the external CLI mutation; record successful settlement separately | First browser render with the exact changed fact and relevant source generation. Report start-to-render and settled-to-render. Failed/uncertain writes are separate trials. For dependencies require native Ready/Blocked agreement and relation visibility as separate milestones. |
| Tracker load | Before scenario navigation/activity starts | End after requested sections and pending scenario changes settle, or at the recorded observation cutoff. Count all observer CLI commands, journal/setup, Git validation/environment and guarded-operation commands separately; report bytes, errors/timeouts, cumulative native time and peak concurrency. Report commands/second over the exact interval. |
| Wait attribution | Enqueue source/command work | Record admission wait, gate wait, native launch-to-settlement, projection/publication and browser receive-to-render separately where instrumented. Include confirming inventory and journal preparation. Overlapping phase sums are not outer latency. |

Measure baseline **equivalent visible facts** even when the old full-response
contract withholds them until all details finish. Do not compare a baseline full
Workspace completion with candidate header-only latency as though both were detail
completion. Record both milestones on both revisions. Hold the requested section
set fixed. For baseline missing section states, record the observable full-response
wait, not an invented earlier section completion.

## Proposed fixture and repetition matrix

These parameters are proposals for the first baseline/candidate pair, not limits
or latency targets. Freeze the final manifest in baseline evidence before running
the candidate. A changed parameter requires a separately named comparison.

| Parameter | Proposed value |
| --- | --- |
| Inventory / paused cohort | 50 / 8 and 2,000 / 40; same IDs, types, statuses, priorities, timestamps and lineage in both revisions |
| Closed evidence candidates | 16 closed tasks per fixture; separate deterministic 129-task cap case, outside representative latency timing |
| History | 8 comments per paused/selected task; fixed serialized text sizes and exact hash. Include an empty selected history, a valid pause report, matching merge reports and malformed/missing merge evidence controls. |
| Relations / selected Workspaces | One known blocker chain and one Product/task lineage; two selected Workspaces with fixed incoming/outgoing edges |
| Request set | Overview, Explorer Ready/All page 1 and All page 3, Attention, Workspace Summary/History/Output and Catch Up 24h; use the same requested sections and output range on both revisions |
| Repetitions | Three independent cold hosts and five warm navigation cycles per fixture/revision/scenario; report every sample, median and range, failures and incomplete observations |
| Active factory | Disposable local Project, two scripted Agent fixture invocations with fixed hold/release schedule; one external comment, dependency change, blocker closure and guarded guidance write at fixed manifest offsets. Record actual offsets and completion, not only requested times. |
| Observation cutoff | Proposed 180 seconds per scenario; report incomplete milestones at cutoff. This is probe containment, not a relaxed source deadline or latency target. |

Cold means a fresh Web host/epoch with empty observation/detail caches and a
seeded tracker; browser profile/tab state is recorded. Warm means the same host
with successful base and specified section validation, then route navigation
within the documented retention interval. Cold direct link is a separate cold
trial, not a navigation after Overview warms the cache. Active-factory trials
repeat both cold and warm states with the fixed disposable workload above.
Include competing Workspace reads during Catch Up and a newly requested Explorer
page while other resources refresh. Run a missed-journal trial separately through
one backup audit; distinguish normal hint detection from audit recovery.

Record full baseline/candidate revisions and trees, clean/dirty state, tool and
backend versions, machine/boot, preparation, temp paths, fixture manifest/hash,
source row counts/bytes, active workload and cache admission/validation state.
Use identical payloads, interests, action order, output ranges, audit phase,
instrumentation and sampling cadence. Alternate baseline/candidate trial order
when practical; disclose drift or unmatched contention. Never use production
mutations for this workload. Existing ns-peyw samples motivate the scenarios;
they are not a controlled pair or a numerical target.

The report must separate useful-content latency, requested-detail completion,
external visibility, queue/gate wait and native load. Retain failed trials before
diagnostic changes. Mark noncomparable cases explicitly. The Implementer records
one complete local suite measurement and concrete test/production cost changes
for each delivery slice through `scripts/Measure-TestSuite.ps1`, using an explicit
retained comparable baseline when available. Missing baseline context means
comparison unavailable. Contract-only documentation adds no native journey or
production command; it does not waive required build or complete-suite checks.
Merger owns synchronized exact-head hosted checks and latest-main acceptance.
