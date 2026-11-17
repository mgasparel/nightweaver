# Complete-suite operating headroom

ns-726.2 verified existing headroom on actual remote `main` after ns-726.1
integrated. No production, source, test, scheduling, deadline or assertion change
was needed. This report adds no runtime work and claims no optimization gain.

## Measured identity and result

The 2026-11-17 measurement used clean revision
`a36418b064ee23e802014c442245ee7e40d77131`, tree
`0b6e5b4a5c6fd0c3683e041638d81b05154ddbb8`. Both the dispatched branch HEAD
and `git ls-remote origin refs/heads/main` resolved to that revision before the
run. The checkout was `H:/Nightweaver/prod/worktrees/nightshift/ns-726.2`.
No separate restore or first-build preparation was performed; normal restore,
build, fixture publishing, execution and ordinary teardown were inside the run.
This is not a warmed-build pair with the older prepared-checkout measurements.

```powershell
pwsh -NoProfile -File scripts/Measure-TestSuite.ps1 `
  -Checkout H:/Nightweaver/prod/worktrees/nightshift/ns-726.2 `
  -EvidenceRoot H:/Nightweaver/ns7262-evidence/20261117 `
  -BaseRevision a36418b064ee23e802014c442245ee7e40d77131 `
  -ProductionInstallation H:/Nightweaver/prod -Project nightshift
```

The wrapper executed this complete, unfiltered command:

```text
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename 70e4c40d0a8f4fd49b6bd697d8e5a588.xml --show-slowest-tests 20
```

| Measure | Result |
| --- | --- |
| Total / passed / failed / skipped | 1400 / 1400 / 0 / 0 |
| Test process / wrapper exit | 0 / 0 |
| Outer elapsed, including build/publish/teardown | 107.6460468 seconds |
| Margin below 240-second operating target | 132.3539532 seconds |
| Margin below 300-second hard limit | 192.3539532 seconds |
| Committed parallelism | Six threads |
| Source dirty / measurement errors | false / none |
| Automatic comparison / capture | unavailable / not triggered |

SDK 10.0.300, runtime 10.0.8 and bd 1.3.1 (`c1c4b642a`) ran on ARES with
12 logical processors, boot `2026-11-16T18:40:45.5000000Z`. Actual TEMP/TMP was
`H:/Nightweaver/prod/tmp/nightshift/ns-726.2/20261117T0251112484140Z-implementer`.
The scheduling-file SHA256 was
`445B59FB492FA1E6B116E60A0CDFC6D3D9621BFE273C18A10ECE6B026BCAC640`.
Production was observed running before and after with unchanged build
`89ec84739b8ba426d360984e499ac7d34a8278c7`, runtime
`0bf7dc3eb6324200ad921fecaf82db0d`, configuration SHA256
`E5A17A5F2FE3203A6379A7819CFE3DE4CFCD1D10AA121D9864882B2001888B4D`.
No production action was taken. Foreground observer demand was not quantified;
the active Orchestrator/Agent and existing production processes remained present.

## Setup and collection attribution

The xUnit assembly elapsed time was 96.091 seconds across 104 collections.
The outer-minus-assembly difference was 11.5550468 seconds, covering work
outside the reported assembly interval; it is not a standalone build timer.
`PublishedCliArtifactFixture` publishes the CLI and AgentCliTrap and prepares
Beads and Agent-stage templates concurrently, then copies/self-tests the traps
before exposing the fixture. This assembly-fixture work and teardown remain
inside the complete measurement. Individual publish/template timings are not
separately instrumented in the retained output, so no numeric partition is
claimed for them.

Largest reported collections (overlapping occupancy, not additive wall time):

| Collection | Cases | Seconds |
| --- | ---: | ---: |
| ManualDeliveryPilotSecondSignalTests | 1 | 41.745 |
| AgentStageDeliveryIntegrationTests | 1 | 34.993 |
| PoolConcurrencyIntegrationTests | 1 | 32.061 |
| MergerPoolIntegrationTests | 1 | 28.530 |
| MergerCompletionFinalizerTests | 7 | 28.422 |
| PlanningNativeTurnTests | 1 | 23.653 |
| FactoryTelemetryIntegrationTests | 15 | 23.217 |
| WebBeadsIntegrationTests | 9 | 22.608 |

Summed collection occupancy was 503.537 seconds under six-thread execution.
These costs identify retained native boundaries, without establishing an
avoidable mechanism that warrants changing them. No duplicated scenario was
removed by ns-726.2, and no schedule replay substitutes for this measurement.

## Historical comparison and coverage disposition

The matched historical D1 revision
`d1a664e4d671387428666cdc35c6ddaeaf014644` (tree
`1dac762b5fd6c5aa5690f363504197c2e5b8e700`) passed the same 1,304 cases at
six threads with production running in 261.9912098 seconds before PC reboot
and 90.4348433 seconds afterward. Raw comparison, results, logs and XML remain
under `H:/Nightweaver/ns6qh-evidence/merge-deploy-20261116`, notably
`post-reboot-comparison.json`, `main-repaired-production-up-result.json`,
`post-reboot-production-up-result.json`, corresponding `.log`/`.xml` files,
and `post-reboot-collection-comparison.json`. The 65.48% reduction supports a
machine/background-state contribution; it does not isolate RAM, Defender,
Dolt or any single process as the cause.

Later main `89ec84739b8ba426d360984e499ac7d34a8278c7` passed 1,325 cases in
106.6117489 seconds; raw `main-suite-timing.json`, `main-suite.xml` and
`main-suite.log` remain under `H:/Nightweaver/ns-sib-source-deploy-20261116`.
That result reported production running prior build `96559cf`. Changed source,
counts, build preparation, TEMP drive/root and production identity prevent a
strict comparison with this report. No baseline was supplied to the wrapper:
delta and ratio are null, rather than a manufactured optimization claim.

Compared with that 1,325-case XML, current XML contains 80 added names and five
removed/renamed names, a net increase of 75 cases. The added names comprise
49 MergerOutcomeVerifier, three AgentOutcomeClassifier, two MergerSlotCoordinator,
one PromptPackaging, 14 BeadsJournalClient, nine BeadsJournalWatcher and two
BeadsJournalClientIntegration cases; their summed test time is 12.181 seconds,
not an incremental wall-time estimate. Changes came from already integrated
ns-726.1 and ns-726.3, not this report. The prior watcher reconnect/truncation,
Merger budget-boundary and two continuous-tail integration names were replaced
by the finite-batch and performance-verification coverage described in
[the coverage pyramid](../../tests/README.md#coverage-pyramid). Native journal
reads/truncation, timer discovery during failed reads, child pipes/settlement,
Git/Beads fixtures, Delivery, merge and cleanup journeys remain exercised.
This task retains all 1,400 cases unchanged, with no coverage tradeoff.

ns-726.1's Merger also reported this exact main passing 1,400 cases in
106.2720042 seconds. Its invocation-local raw directory was absent when this
dispatch inspected it, so that comment is corroboration only; the single new
run above restores inspectable raw evidence outside completion cleanup.

## Retained evidence and integration handoff

Immutable raw artifacts are outside per-Bead temporary cleanup at
`H:/Nightweaver/ns7262-evidence/20261117/70e4c40d0a8f4fd49b6bd697d8e5a588/`:

| Artifact | SHA256 |
| --- | --- |
| result.json | `22E1489DD266EBA01B58872704AEC1EC1F1894C0FFBA981D1BBF7698AEC977D8` |
| suite.log | `E8D68655D490B58F073E8906384811D08F4D4A8AA1658C1FFA041D27CE5AD49E` |
| report-1.xml | `7899B25BF5DC3E3A49130267428B150FF05C8E1B0BEFDAA53EBDF3BEC5011063` |

The raw result remains `accepted: false`; Review can accept a separate retained
copy with original path/hash and rationale under the
[measurement contract](../agents/test-cost-evidence.md). It has no invocationId
and is not a current-invocation Merger rejection artifact. All 460 injected
measurement-policy cases also passed via
`pwsh -NoProfile -File tests/TestSuiteMeasurement.Tests.ps1`.

The measured revision is actual main before this documentation commit; it is
not the eventual integrated revision of this report. No additional full run is
needed solely for writing these facts during implementation. Merger still owns
fresh synchronized-head checks, final exact-head hosted correctness and one
post-integration latest-main measurement, recorded in the Bead. Keep ns-726.2
open until that evidence is established; reuse the required integration run.
An unexplained comparable 1.5x recurrence or broad native slowdown still requires
capture before disturbing state, as documented in
[test-cost evidence](../agents/test-cost-evidence.md); the 240/300-second policy
and ns-726.5 attribution work remain unchanged.
