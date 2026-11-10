# Test suite

Run the complete suite from the repository root with the .NET 10 Microsoft.Testing.Platform runner:

```powershell
$timer = [Diagnostics.Stopwatch]::StartNew()
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 1
$testExit = $LASTEXITCODE
$timer.Stop()
Write-Host "Suite wall time: $($timer.Elapsed)"
if ($testExit -ne 0) { throw "Tests failed (exit $testExit)" }
if ($timer.Elapsed.TotalMinutes -ge 5) { throw 'Suite exceeded the five-minute budget' }
```

The five-minute budget includes the incremental build, fixture publishing, tests, and teardown. Keep the committed six-thread parallelism. Prerequisites are the .NET SDK, Git, bd 1.3.1 with embedded Dolt, and Windows for Job Objects. A fresh SDK/package installation is prerequisite setup.

Use `--report-xunit-xml --report-xunit-xml-filename timing.xml` to collect per-test and per-collection durations in `TestResults`. Measure a full run after changing expensive coverage; filtered timings do not establish the suite budget.

## Coverage placement

| Concern | Focused coverage | Real-tool coverage |
| --- | --- | --- |
| Delivery entry stages, role order, repair counters, pauses | `DeliverySlotCoordinatorTests` and `AuthorityMatrixTests` | One complete Delivery handoff, one repair loop, one pause cleanup, and persisted budget escalation |
| Pool capacity, overlapping lifetimes, replacement, admission races | `DeliveryPoolCoordinatorTests`, using explicit signals | Competing guarded bd claim after evaluation |
| Retry budget and duplicate/different failures | `OperationalRetryCoordinatorTests` | Planning retry, dirty-worktree retry/reset, and duplicate-failure escalation |
| Post-exit classifications, precedence, read-only HEAD protection | `AgentPostExitVerifierTests` with controlled process observations | Native lifecycle smoke tests plus process containment tests in `AgentInvocationExecutorTests` |
| Planning materialization, challenge, and worker observation rules | `AuthorityMatrixTests` and `AgentPostExitVerifierTests` | Native plan transitions and rejected Merger worker mutation; `MergerPoolIntegrationTests.NativeMergerSynchronizesChecksAndLeavesCompletionToTheCompletionOwner` verifies Merger completion ownership |

Expand combinations at the focused boundary. Add a native scenario when it checks a distinct tool contract or cross-component behavior. Repeating a decision matrix through fresh repositories and child processes adds substantial cost; the focused tests should carry those combinations.
