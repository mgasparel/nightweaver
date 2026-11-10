# Test suite

Run the complete retained suite from the repository root with the .NET 10 Microsoft.Testing.Platform runner. The positive minimum catches mistaken test selection. The current retained suite discovers 644 tests.

```powershell
$timer = [Diagnostics.Stopwatch]::StartNew()
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 644 --report-xunit-xml --report-xunit-xml-filename timing.xml --show-slowest-tests 20
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
