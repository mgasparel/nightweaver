# Activity lifecycle producers

The continuous runtime owns one Project Activity journal. Its runtime GUID is the
existing factory snapshot identity. The journal starts after startup recovery and
stops after pool drain. Its producer scope is independent of OTLP configuration.
Startup uses the existing single Git origin read to bind hosted links. It adds no
tracker command. A missing, invalid or failed origin read disables the journal
and preserves factory execution. Both producer sets share the same journal.
Unavailable journal preparation does not stop factory execution. Storage failures
retain the journal's safe health and loss state.

ActivityRuntime accepts typed facts from existing boundaries:

| Fact | Boundary |
| --- | --- |
| InvocationStarted | Native launcher returns the exact process handle. |
| InvocationCompleted | Process containment and output settlement succeed. Exit, timeout, cancellation and failure remain observations. |
| EvidenceUnavailable | Preparation, launch or settlement cannot establish completion. No launch is invented. |
| QaAdmitted | Stage opening confirms current QA workflow and expected ownership before and after provisioning. |
| PlanningApproved | Challenger publication returns its confirmed receipt after evidence and grounding checks. |
| PlanningPublished | PlanningPublisher confirms the final Product state and immutable graph with actual task IDs. |
| DeliveryApproved | Implementer, Reviewer or QA publication returns its confirmed receipt, including the existing exact confirming reread. |
| ChangesRequired | The existing guarded semantic repair publication returns its receipt. Count is the published repair count. |
| OperationalRetryStarted | The existing stop gate permits operational retry preparation. Count is the next attempt number. This is separate from runtime recovery and semantic repair. |
| MergeVerified | MergerOutcomeVerifier accepts exact report, hosted required checks, PR identity, checked head, merge commit and default-branch reachability. |
| BeadClosed | MergerCompletionFinalizer accepts exact guarded closure or its confirming reread. |
| CleanupFailed | Direct finalization or the existing deferred cleanup worker reports retention/failure. Merge evidence remains valid. Deferred counts describe previously recorded attempts. |

Planning records retain the invocation and approved plan revision in their causal
receipt. Merge proof retains the actual report comment ID and a verification
identity derived from the current invocation. The live verifier supplies both
normalized sides only after its full checks pass. The projector still checks
agreement and Project repository identity. Closure and cleanup never manufacture
merge proof. The retained Planning guards and Merger parsing checks are unchanged.

Successful cleanup queue entry remains pending. It does not produce CleanupFailed.
A failed queue entry or direct cleanup failure produces CleanupFailed. The deferred
worker reports any later cleanup failure independently. BeadClosed remains separate.

The Delivery discovery pass supplies its existing successful complete inventory.
Initial pauses are observations. Label-only reasons explicitly say that the
concrete reason is unavailable. NeedsHumanEscalator supplies a closed host cause
only after its existing final pause check. No diagnostic text, title, guidance,
prompt, command line, path or configuration enters an Activity record. Notes and
Abort with retained labels cannot assert Resume. A later removal is observed,
with MissingEvidence for its human action. This runtime has no persisted guarded
Attention action receipt. It therefore emits no ResumeConfirmed record.

An inventory read that overlaps a local publication cannot replace that receipt
state or invent pause removal. It reports stale source evidence.
Unchanged inventory is silent. Exact receipt states suppress later matching
inventory reports. Distinct invocations and repair visits remain distinct.
Suppression retains at most 500 receipts. Inventory retains at most 10,000 bounded
workflow identities. Failed or older-than-60-second inventory loses transition
coverage and emits unavailable evidence; the next success supplies initial state.
Missing Beads lose their prior state. These limits do not promise permanent
exactly-once history. The existing journal supplies queue loss, runtime replacement,
retention gaps and finite disk/read budgets.

Producer hooks perform no tracker read or write. Hooks after a confirmed
cross-pool handoff only submit bounded local evidence. Journal admission does not
serialize, access disk or wait for viewers. No Activity value controls scheduling,
claims, stages, pauses, Retry, Resume, repair budgets or recovery.

Controlled publisher, Planning, escalation, coordinator, retry and Merger fixtures
assert records while preserving their authority and command-count assertions.
ActivityRuntimeTests checks pause changes, missing coverage, exact receipt
suppression, sink failure and real journal persistence without an exporter.
Existing native launch and closure/cleanup journeys gain record assertions. No
new native factory journey, fixture publication or browser behavior is added.
