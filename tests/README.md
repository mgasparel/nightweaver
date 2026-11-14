# Test suite

Run the complete retained suite from the repository root with the .NET 10 Microsoft.Testing.Platform runner. The positive minimum catches mistaken test selection. The current retained suite discovers 903 tests.

```powershell
$timer = [Diagnostics.Stopwatch]::StartNew()
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 903 --report-xunit-xml --report-xunit-xml-filename timing.xml --show-slowest-tests 20
$testExit = $LASTEXITCODE
$timer.Stop()
Write-Host "Suite wall time (build, fixture publishing, tests, teardown): $($timer.Elapsed)"
if ($testExit -ne 0) { throw "Tests failed (exit $testExit)" }
if ($timer.Elapsed.TotalSeconds -ge 300) { throw 'Suite exceeded the five-minute budget' }
```

The five-minute budget covers the normal solution build, native fixture publishing, tests, and teardown. Keep the committed six-thread parallelism in `TestScheduling.cs`. For a cold-machine run, report package restore and first build separately from this warm, normal suite command. A filtered run never establishes full-suite acceptance.

Use `--report-xunit-xml --report-xunit-xml-filename timing.xml` to collect per-test and per-collection durations in `TestResults`.

## Coverage pyramid

The retained suite keeps broad, fast decision and security checks in unit/coordinator tests and one representative set of expensive process boundaries:

| Concern | Detailed fast coverage | Retained real-tool boundary |
| --- | --- | --- |
| Ownership, read-only roles, failure classification, and budget precedence | `AgentPostExitVerifierTests`, `AuthorityMatrixTests`, retry and coordinator tests | One complete Delivery handoff and one Merger completion-owner/real Beads boundary |
| Slot capacity, competing writes, and claim ownership | Pool/coordinator tests | One native guarded claim after evaluation |
| Process creation, inheritable pipe cleanup, job containment, and long paths | Existing focused executor and runtime-file tests | Retained native process/window and published active-classifier smoke tests |
| Stop, restart, and preservation | Fast state/worker-observation tests | One second-signal restart case and one published force-stop case |
| Bead HTTP and workspace safety | Request/authority tests | Intake plus invalid-query HTTP cases; ambient-variable, reparse-path, and Git-admin-pointer checks |
| Merge finalization and repository guards | Cheap finalizer and branch-ownership tests | Repeatable owned cleanup, conditional branch deletion, and expected-commit guards |

The ns-e5i reduction removed 138 costly overlapping integration scenarios: the comprehensive parentless/planned pilot variants, held-Merger and crash-recovery journeys, most repeated published stop/restart and dispatch/repair/admission cross-products, and most full Bead/Web request permutations. The resumed-admission and preparation-held Merger boundaries were subsequently restored as focused regressions. Historical incident causes and dispositions remain tracked in Beads; removing coverage does not establish a causal repair.

The prior ns-e5i baseline discovered and passed 607 tests with no skips in 179.366 seconds, including the normal solution build, native fixture publishing, test execution, and teardown. Four focused Git cases cover short/deep pilot rebases and preservation of interrupted merge/apply rebases. One native resumed-admission regression covers dependency, capacity, and restart with explicit fixture priorities; it launches no Agent process. The pool-concurrency regression holds Merger preparation before its Agent clock starts, then proves native Implementer/Reviewer/QA progress and both real merge closures under the unchanged singleton reservation. The five-minute budget remains the acceptance threshold. Record the exact command, artifact path, measured duration, and any further coverage changes in the Bead result.

Planning execution adds fast component cases for exact approval identity, grounding revalidation, coverage and dependency structure, evidenced assumption repair, questions, classification failure, configured budgets and guarded pause eligibility. One native Planner/Challenger smoke verifies the real Agent/Beads boundary and retained approved environment without creating tasks. The ns-47f.2 branch suite passed 698 tests with no skips in 112.48 seconds wall time, including build, fixture publishing and teardown; this is branch validation, not a timing measurement of a later main.

The ns-47f.2 CI repair refreshes collection weights from run 43509330048 while retaining all cases and the six-thread cap. PID readiness in the crash-containment test waits for a complete positive PID after writer closure within its original timeout; two cheap cases hold an exclusive or read-sharing writer open to cover both sharing violations and partial digits. Native crash containment remains exercised.

The second-signal restart smoke proves startup recovery with a new live Agent and retained dirty worktree, then requests graceful drain before releasing that Agent. This isolates its final pause/claim handoff from background discovery against embedded Dolt. It still verifies both original console signals, native process termination, recovered runtime/Agent identity, exact branch and sentinel preservation, the guarded pause, and the untouched foreign claim. Failed slot release fails immediately rather than consuming the recovery polling deadline. CI retains failed manual Delivery fixture roots alongside console artifacts for diagnosis. No production timeout or suite parallelism changes are involved.
Graph publication adds fast fault-injection cases at every create/link/release boundary, failed and lost write responses, conflicting or foreign content, immutable executing graphs and Resume-driven Challenger revalidation. Discovery attention preserves completed and foreign-owned tasks and does not change claims or repair counters. The existing native Planning smoke now publishes a two-task graph, verifies native dependency blocking, then drives the first task through Delivery. Six-thread scheduling and all retained cases remain unchanged.

The ns-47f.3 hosted runtime repair refreshes collection weights from Windows CI run 43529751486. The expanded native Planning publication-to-Delivery smoke measured 149.822 seconds while its old weight was 31.025, leaving it late in the schedule. It now starts with the longest collections. All 867 cases, fixture isolation and the six-thread cap remain intact; CI also requires the current positive minimum of 867 tests. Weight refreshes only change collection start order and must still pass the complete timed suite; replaying historical durations is diagnostic evidence, not runtime acceptance.
