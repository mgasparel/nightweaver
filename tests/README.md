# Test suite

Run the complete retained suite from the repository root with the .NET 10 Microsoft.Testing.Platform runner. The positive minimum catches mistaken test selection; use at least the committed floor of 995 tests and report the actual discovered total.

```powershell
$timer = [Diagnostics.Stopwatch]::StartNew()
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename timing.xml --show-slowest-tests 20
$testExit = $LASTEXITCODE
$timer.Stop()
Write-Host "Suite wall time (build, fixture publishing, tests, teardown): $($timer.Elapsed)"
if ($testExit -ne 0) { throw "Tests failed (exit $testExit)" }
if ($timer.Elapsed.TotalSeconds -ge 300) { throw 'Suite exceeded the five-minute budget' }
```

The five-minute budget covers the normal solution build, native fixture publishing, tests, and teardown. Keep the committed six-thread parallelism in `TestScheduling.cs`. For a cold-machine run, report package restore and first build separately from this warm, normal suite command. A filtered run never establishes full-suite acceptance.

This is the **local latest-main performance requirement**. Candidate timing is reported separately and latest main must be measured after integration. Hosted correctness CI reports timing diagnostically and uses a separate 15-minute hang timeout; it does not fail solely for exceeding five minutes. Local Implementation/Review/QA iteration uses draft PRs, with final hosted verification owned by Merger. See [hosted verification](../docs/agents/hosted-verification.md) for triggers and the explicit CI-investigation dispatch exception.

Use `--report-xunit-xml --report-xunit-xml-filename timing.xml` to collect per-test and per-collection durations in `TestResults`.

## Coverage pyramid

The retained suite keeps broad, fast decision and security checks in unit/coordinator tests and one representative set of expensive process boundaries:

| Concern | Detailed fast coverage | Retained real-tool boundary |
| --- | --- | --- |
| Ownership, read-only roles, failure classification, and budget precedence | `AgentPostExitVerifierTests`, `AuthorityMatrixTests`, retry and coordinator tests | One complete Delivery handoff and one Merger completion-owner/real Beads boundary |
| Slot capacity, competing writes, and claim ownership | Pool/coordinator tests | One native guarded claim after evaluation |
| Process creation, inheritable pipe cleanup, job containment, and long paths | Existing focused executor and runtime-file tests | Retained native process/window and published active-classifier smoke tests |
| Stop, restart, and preservation | Fast state/worker-observation tests | One second-signal restart case and one published force-stop case |
| Cross-pool progress, reservation, and handoff | Planning turn/publication tests and `MergerPoolCoordinatorTests` | `PoolConcurrencyIntegrationTests.DeliveryCompletesWhileMergerPreparationIsHeld` |
| Bead HTTP and workspace safety | Request/authority tests | Intake plus invalid-query HTTP cases; ambient-variable, reparse-path, and Git-admin-pointer checks |
| Merge finalization and repository guards | Cheap finalizer and branch-ownership tests | Repeatable owned cleanup, conditional branch deletion, and expected-commit guards |

The current native cross-pool smoke holds a real Merger claim and singleton reservation before Agent launch while one unrelated native Delivery Implementer completes its `stage:review` claim handoff. It verifies both task worktrees remain on their expected branches, then cancels and drains the still-held Merger without releasing its preparation gate or launching a Merger Agent. Fast Planning coordinator/publication tests retain the removed pause, resume, graph and replay decisions; the standalone native Planning approval/claim-release test, complete Delivery handoff, and native Merger completion remain separate boundaries. The deliberate coverage reduction removes one combined native Planning-publication/interruption/generated-task/Delivery/merge journey, rather than replaying those checks in several expensive integration tests.

`WindowsConsoleProcess` owns an outer test Job, assigned while the console root is suspended. Disposal terminates that group and waits for retained native process handles within the existing five-second budget before disposable repository deletion. Root exit and zero active Job processes alone do not prove native handle rundown. The logged/unlogged descendant regressions use a real `bd sql` query holding its Dolt `LOCK` after root exit, then require immediate lock deletion after test disposal. This test-owned teardown does not add waits or compensating writes to production force-stop.

The ns-e5i reduction removed 138 costly overlapping integration scenarios: the comprehensive parentless/planned pilot variants, held-Merger and crash-recovery journeys, most repeated published stop/restart and dispatch/repair/admission cross-products, and most full Bead/Web request permutations. The resumed-admission and preparation-held Merger boundaries were subsequently restored as focused regressions. Historical incident causes and dispositions remain tracked in Beads; removing coverage does not establish a causal repair.

Historical ns-e5i baseline: it discovered and passed 607 tests with no skips in 179.366 seconds, including the normal solution build, native fixture publishing, test execution, and teardown. Four focused Git cases covered short/deep pilot rebases and preservation of interrupted merge/apply rebases. One native resumed-admission regression covered dependency, capacity, and restart with explicit fixture priorities; it launched no Agent process. That earlier pool-concurrency regression held Merger preparation before its Agent clock started and then traversed native Implementer/Reviewer/QA progress and both merge closures. The current cross-pool smoke has the narrower boundary documented above.

Planning execution adds fast component cases for exact approval identity, grounding revalidation, coverage and dependency structure, evidenced assumption repair, questions, classification failure, configured budgets and guarded pause eligibility. One native Planner/Challenger smoke verifies the real Agent/Beads boundary and retained approved environment without creating tasks. The ns-47f.2 branch suite passed 698 tests with no skips in 112.48 seconds wall time, including build, fixture publishing and teardown; this is branch validation, not a timing measurement of a later main.

The ns-47f.2 CI repair refreshes collection weights from run 43509330048 while retaining all cases and the six-thread cap. PID readiness in the crash-containment test waits for a complete positive PID after writer closure within its original timeout; two cheap cases hold an exclusive or read-sharing writer open to cover both sharing violations and partial digits. Native crash containment remains exercised.

The second-signal restart smoke proves startup recovery with a new live Agent and retained dirty worktree, then requests graceful drain before releasing that Agent. This isolates its final pause/claim handoff from background discovery against embedded Dolt. It still verifies both original console signals, native process termination, recovered runtime/Agent identity, exact branch and sentinel preservation, the guarded pause, and the untouched foreign claim. Failed slot release fails immediately rather than consuming the recovery polling deadline. CI retains failed manual Delivery fixture roots alongside console artifacts for diagnosis. No production timeout or suite parallelism changes are involved.
Historical graph-publication rollout: fast fault-injection cases were added at every create/link/release boundary, along with failed and lost write responses, conflicting or foreign content, immutable executing graphs and Resume-driven Challenger revalidation. Discovery attention preserved completed and foreign-owned tasks without changing claims or repair counters. At that point, a native Planning smoke published a two-task graph, verified native dependency blocking, and drove the first task through Delivery; the current native cross-pool boundary is documented above.

The ns-47f.3 hosted runtime repair refreshes collection weights from Windows CI run 43529751486. The expanded native Planning publication-to-Delivery smoke measured 149.822 seconds while its old weight was 31.025, leaving it late in the schedule. It now starts with the longest collections. All 867 cases, fixture isolation and the six-thread cap remain intact; CI requires the current positive minimum of 870 tests after the three follow-up authority regressions. Weight refreshes only change collection start order and must still pass the complete timed suite; replaying historical durations is diagnostic evidence, not runtime acceptance.

The follow-up runtime repair shares one fresh Product/discussion/grounding observation per publication authority check, removing duplicate bd reads without caching across mutations. Tracker observations follow Git grounding reads so ownership, status and pauses changed during grounding stop the next write. Three cheap regressions cover these changes (870 total); the original 867 cases remain. The initial weight-only repair passed locally but still exceeded hosted runtime and encountered a restart-smoke timeout, so local timing and historical replays alone did not establish hosted acceptance.

After synchronization with main, historical hosted run 43535447629 passed all 905 cases but took 303.759 seconds. Its native Planning collection measured 255.284 seconds and now receives the highest scheduling weight. Independent read-only Git grounding queries overlap within each fresh observation; all settle before tracker reads and authority checks. A controlled regression fails on serial grounding and verifies overlap plus tracker ordering. All 905 synchronized cases remain, with this one additional case; six-thread test scheduling remains unchanged. The 300-second performance gate applies to the complete local latest-main suite; hosted elapsed time is diagnostic.

Historical runtime note: the overlap/scheduling change passed 906 cases locally in 177.562 seconds, while hosted run 43545299874 took 330.304 seconds and its native Planning smoke consumed 282.648 seconds. That earlier smoke stopped after the generated-task Implementer published `stage:review`; its Planner/Challenger approval, graph publication, dependency blocking, generated-task admission and implementation coverage were later split across cheaper tests and the representative native boundaries listed above. It did not repeat Reviewer and QA turns already covered by `DeliverySlotKeepsOneClaimAndPersistentWorktreeThroughMergeHandoff`.

Historical coverage note: an earlier `PoolConcurrencyIntegrationTests` version combined graph publication with one generated Implementer. Its native graph, dependency and Planning-turn assertions are no longer part of the current cross-pool smoke; fast Planning publication/coordinator tests retain those decisions.

Historical multi-pool journey (superseded by the current boundary above): the smoke once held Planning preparation, published a generated task, ran its Implementer, and completed a real merge while preserving the generated task's `stage:review` handoff. The dedicated `AgentStageDeliveryIntegrationTests.DeliverySlotKeepsOneClaimAndPersistentWorktreeThroughMergeHandoff` continues to cover the full Implementer/Reviewer/QA journey, and `MergerPoolCoordinatorTests.EqualPriorityCandidatesUseFifoAndTheReservationRemainsCapacityOne` retains successor reservation and capacity detail. The current native smoke intentionally no longer combines Planning publication with concurrent Delivery and merge.

Stream-cleanup readiness reports are published by writing and closing a temporary JSON file before atomic rename. Windows observers can still encounter an incompatible open handle on the published path. `StreamReadinessTests` holds exclusive and read-sharing writers open, verifies recovery after closure, and verifies that persistent sharing expires within the original deadline. The coordinator retries only JSON incompleteness and Windows sharing/lock violations (32/33); other IO failures propagate. The native blocked-drain and launch-burst cases remain retained. Planning authority and graph reads are batched as described in [Planning evidence](../docs/agents/planning-evidence.md), retaining fresh checks across writes and all existing fault-injection and native publication cases. No timeout, coverage or parallelism reduction is involved.

Lifecycle readiness obtains both unassigned-open and exact-owned-in-progress memberships from one unlimited native `bd list --ready --status open,in_progress` read. Native Beads still decides blockers and deferment. Every returned row is checked against the fresh complete inventory before ownership selection and the publication gate; a mismatched pair is refreshed once, and repeated disagreement fails closed. The real inventory fixture compares both memberships with the former separate commands across blocked, deferred, foreign, assigned, closed, gate and more-than-50-row controls. Component tests retain tracker failure, stale-pair refresh, competing claims and interstage mutation coverage. Collection weights use the complete local `repair-recovery-isolated.xml` run (1,207 passing tests), including operational observations and measured worktree cost. All cases, deadlines and the six-thread cap remain unchanged; weights and filtered checks do not establish the five-minute gate.


The ns-mbd.5 timing follow-up overlaps the independent CLI publish, standalone
AgentCliTrap publish and isolated Beads-template initialization before any test
collection starts. The existing CLI project build gate remains, all prerequisites
settle even on failure, and trap checks still run before the fixture is exposed.
This changes setup order only: no test, native boundary, deadline or six-thread
limit is removed. Collection weights use the 1,230-case merged-main measurement,
including previously unweighted native collections. A fast setup probe improved
from 9.817s to 8.055s; that diagnostic and scheduling replay are not complete-suite
or latest-main acceptance. The complete timed command above remains the gate.
