# Test suite

Run the complete retained suite from the repository root with the .NET 10 Microsoft.Testing.Platform runner. The positive minimum catches mistaken test selection; use at least the committed floor of 995 tests and report the actual discovered total.

Use PowerShell 7 and the single retained measurement entry point:

```powershell
pwsh -NoProfile -File scripts/Measure-TestSuite.ps1 -Checkout <absolute-checkout> -EvidenceRoot <absolute-evidence-root> -BaseRevision <full-base-sha> -Baseline <accepted-result.json> -ProductionInstallation <absolute-installation> -Project nightshift
```

The operating target is **below 240 seconds**; the **below 300-second hard limit**
remains unchanged. Measure normal build, fixture publishing, execution and teardown
with the committed six-thread parallelism. Report actual full-suite counts, exact
baseline/candidate identities, outer elapsed time and concrete cost changes for
every change. A filtered run never establishes full-suite acceptance. Hosted time
is diagnostic and final exact-head correctness remains required.

The retained measurement also runs the cheap [unattended archive fixtures](../scripts/tests/UnattendedEvidenceArchive.Tests.ps1)
inside its outer stopwatch and reports their counts/time separately from xUnit.
Fixture failure prevents acceptance even if xUnit passes. Windows CI runs the same
checks inside the timed correctness step. For direct format inspection and its
integrity-versus-coverage semantics, see [the archive format](../docs/acceptance/unattended-evidence-archive.md).

See [test-cost evidence](../docs/agents/test-cost-evidence.md) for the versioned
measurement/comparability contract, native-boundary justification, capture-first
1.5x diagnostic trigger and ordinary repair routing. Reuse comparable retained
evidence; Merger's mandatory fresh synchronized-head checks and post-integration
latest-main acceptance remain required. For draft CI and final hosted checks see
[hosted verification](../docs/agents/hosted-verification.md).

## Coverage pyramid

Web observation telemetry adds four cheap decoder/loopback/component cases for shared
subscriber collection, scheduled no-op suppression, monotonic retained age,
timeout/unavailable/idle/stopped states and exported canary exclusion. Existing
standalone hosting journeys now inspect observation export and the existing
published standalone/background journey checks SDK dependencies. No native
journey or publish is added; the uncovered boundary is the real executable's
environment-only exporter and observer wiring, which direct service tests cannot
prove. See [the contract](../docs/design/web-observation-telemetry.md).

The invocation-to-verified-merge telemetry slice adds one native OTLP smoke using
the existing immutable AgentStage template: actual fake Agent launch/settlement,
native Git/bd verification and guarded closure must export matching operational
IDs without prompt/output canaries. Existing executor and verifier tests cannot
prove that exported correlation boundary. Seven cheap finalizer cases cover
guard mismatch, uncertain response, unconfirmed closure, duplicate callback,
cleanup failure/cancellation and already-closed inventory; bounded-role/sampling
coverage is also deterministic. Disposable Grafana/Collector/backends and
Playwright run only through `tests/telemetry-first-path/Run-Proof.ps1`, outside
the normal suite. See [the contract](../docs/design/operational-telemetry-first-path.md).

The native console inheritance probe reads its atomically published child readiness
file within one monotonic twenty-second deadline. Missing-file and sharing/lock
violations can settle within that budget; other I/O errors, malformed JSON and
nonce/PID mismatches still fail. Six cheap controlled cases exercise transient
sharing, permanent contention and unchanged payload/error handling. The existing
native journey retains its live-child, pipe inheritance and cleanup assertions;
no extra process journey or longer deadline is introduced.

The native Dolt descendant fixture waits for a CSV result marker emitted after
database acquisition before publishing lock readiness. Exclusive `LOCK` polling
could itself deny Dolt's startup open with incompatible sharing flags. The existing
settlement cases still verify the live child, held/nondeletable lock before and
after root exit, and settled child plus deletable lock after disposal. Their
ten-second readiness, fifteen-second root, thirty-second query and six-thread
scheduling bounds are unchanged; no native journey was added.

Run the cheap paired publisher contract explicitly in local Review/QA:
`powershell.exe -NoProfile -File scripts/tests/Publish-Nightweaver.Tests.ps1`
and `pwsh -NoProfile -File scripts/tests/Publish-Nightweaver.Tests.ps1`.
CI runs both shells. Disposable staged files and a controlled `dotnet` function
exercise the real script interface, Studio-only success, exact revision manifest,
missing entry/JS/CSS/apphosts/prompts, staging cleanup and destination preservation.
These checks perform no real builds or publishes. Existing published standalone
and background hosting tests retain the native hosting boundary.

Journal hint tests use fake batches and delay callbacks for pagination, atomic cursor
commit, failure backoff, truncation, session replacement and cancellation. In-memory
parser cases cover multiline JSON, invalid suffixes, UTF-8 byte and record limits,
and aggregate output including whitespace. The existing native tracker journeys
now read finite batches, retain native truncation, scrub a decoy target and prove
that an unjournaled write is discovered by the independent timer during a batch
failure. Shared managed fixture pipes cover stderr flood, malformed-output termination,
and actual child settlement on cancellation or a controlled 30-second deadline;
no real 30-second timeout is added to the suite. Journal hints now have a 30-second
cadence plus command execution, with one page per tick even under retained backlog.
This change reduces recurring journal work without asserting whole-suite causality.

The classifier timeout fixture publishes root/descendant PID handshakes by
renaming a completed, closed same-directory temporary file. Existence cannot
authorize forced termination while a PID writer is still empty. A cheap fixture
case pauses before writing and checks that the public marker is absent; the
native smoke retains actual Job termination of both processes and its original
deadlines. `SystemProcessRunnerTests` uses the existing managed `flood` fixture
for its bulk-stream boundary: both 1,100,000-byte streams and their exact contents
must drain within the unchanged 30-second limit. This removes PowerShell startup
from that contract while retaining a real child and pipe backpressure; the
The output-limit check also uses the native producer, retaining a 30-second
post-output hold so the unchanged 10-second deadline must be interrupted by the
read limit. The environment-removal check uses a native environment probe and
first verifies inheritance in a control invocation. PowerShell timeout and
Windows command-line launch checks remain. Hosted PowerShell timeouts in the
bulk, output-limit and environment checks did not establish a shared production
cause; removing that unrelated shell dependency does not diagnose its startup.

Workspace-only observation retries retain their captured inventory for one
continuation turn while detail work is pending, avoiding redundant native core
reads without indefinitely retaining a snapshot. A fresh confirming inventory still guards publication and changed identity
fails closed. Scheduled mixed Attention/Explorer subscriptions also reserve one
detail continuation when core reads leave no detail capacity, then return to core
validation. A clock-driven regression invalidates every slow inventory/readiness
read with both Ready and Blocked interested, with and without a selected Workspace.
Cold histories and their later audits must progress without a quiet core turn;
the dirty inventory keeps the projection pending until authoritative recovery.
The existing controlled timeout cases assert the reduced command counts, and
the slow-clock case models 1.9-second reads without adding wall-clock waits.
A cheap two-Workspace case holds one history source timed out, creates and selects
another Workspace, then changes its identity and requires both updates to publish. Identity conflicts force
a fresh core read so another pending cycle cannot retain the old baseline forever.
The same case then fails all core refreshes and requires pending detail retries to
continue against their captured baseline; failed refreshes retain visible errors
and cannot satisfy the final confirming read. The Beads subprocess Go CPU budget
also applies in production. A warmed balanced six-process probe measured a lower
median (1,072ms to903ms) and CPU use with two Go workers; its tail did not improve,
so this probe does not establish a fix for every hosted source timeout.
Failed published classifier smoke runs retain their gates and runtime telemetry
for hosted diagnosis; this does not extend their Agent-start or stop deadlines.

Factory snapshot publication falls back from Windows move access-denied to a
no-follow source handle rename while all destination parents stay pinned. Existing
short/deep file-boundary cases hold a delete-sharing reader that denies writes:
the old reader keeps old bytes and fresh readers receive the complete replacement.
The existing hard-link case also checks that this fallback preserves foreign
content. Read-only destinations and unsafe reparse paths still fail closed;
there is no delete-before-move or timeout increase.
The native rename buffer copies exact UTF-16 path code units. The existing deep
case uses an unmatched surrogate plus a distinct replacement-character alias
with a foreign sentinel, proving encoding cannot redirect the publication.

The native observer's two external writes explicitly enable Dolt auto-commit,
matching production rather than inheriting its fixture's read-probe default.
Read/write overlap and the original cold/update observation deadlines remain.
After a settled baseline, test-side source-read recording requires dirty comment
and dependency reads after their journal hints, plus an authoritative inventory
read after the dependency hint, and excludes tracker audits until combined
visibility. If the dependency hint arrives during Workspace detail work, its
mandatory post-detail inventory is labelled `confirmation` and satisfies the
same causal boundary as a `dirty` inventory read. Controlled component cases
cover hints before core collection and during detail work, require the combined
comment/relation projection with no pending source generation, and reject audit,
bootstrap and pre-hint inventory evidence. No native journey or deadline is added.
The independent two-second environment audit remains
allowed. This observes the actual event path instead of treating total elapsed
time below 30 seconds as proof of the 60-second tracker audit boundary. Command
timings, source causes and exact page/read counts survive assertion failures.
A balanced local comparison after identical mutations measured 5/24 reads above
two seconds with auto-commit off and 0/24 with it on; this is performance evidence,
not a claim that off loses SQL-committed data. Both smoke phases retain command
and external-write timing diagnostics on failure. `BD_DISABLE_EVENT_FLUSH` controls
the detached metrics uploader, not durable tracker journaling, and is unchanged.

The native wait/cancellation fixture starts the held child with its required PID
path and verifies the published PID before testing the original 50ms wait. A
bounded readiness phase reports early fixture exit instead of accidentally testing
startup speed. Cancellation, repeated waits and native termination bounds remain.

The Web journal's clock-driven regressions exercise slow successful preparation,
per-command two-second cancellation, the four-second cumulative native-work cap,
and cursor reset after a failed first page. They add no native journey or sleeps.
The retained timer/journal native journey serializes setup, finite pages, inventory
and raw SQL against its single embedded store, including the journal-head check
inside the SQL write section. Its one timer tick, unjournaled visibility, failed
batch and exact-target cursor assertions and original deadlines remain intact.
This removes fixture overlap; it does not claim every hosted read timeout has
the same cause. Discovery diagnostics are retained on failure.

Test fixtures copy settled immutable repositories into independent file trees,
including Git objects and tracker stores. The small `GitFixture` initializes one
empty Git baseline; manual-pilot profile templates seed independent copies of
one empty Git/Beads baseline. Consumer writes must never reach a template or
another copy. Existing isolation cases check configuration, tracker and origin
independence. Provisioning preservation snapshots obtain HEAD, branch and status
from one native `git status --porcelain=v2 --branch` command instead of launching
three processes or adding concurrent native work. Index bytes, working files,
HEAD, branch and status assertions
remain intact; upstream ahead/behind metadata is excluded because an allowed
fetch can update remote refs without changing the checkout. These setup changes retain all cases and native
boundaries; only a complete timed command establishes their suite cost.

Test-owned managed launches use `FixtureProcess.Start`, including shared manual
Delivery and QA helpers. It holds `WindowsProcessLauncher.ProcessCreationGate`
only during `Process.Start`, matching the production process runners. A native
launcher's HANDLE_LIST limits its own child; it cannot stop an unrelated managed
CreateProcess from inheriting temporary pipe writers. Bypassing the gate can
leave coordinator drains pending even after the coordinator exits and its Job
is proven empty. The two small `FixtureProcessTests` contention cases retain
both managed Start overloads; the existing native blocked-drain and launch-burst
smokes retain actual pipe/Job/foreign-child coverage. Do not add raw managed
starts to the shared test process. Console helpers restrict inheritance with
their own HANDLE_LIST and do not need to join this gate.

The ns-mbd.6 native timeout investigation found unused Ready/Blocked reads
consuming the observer's four-second sweep and repeated full inventories in
task-only Merger finalization guards. Observer component cases inject 1.4-second
command costs and require Workspace progress without unused membership reads.
Merger task guards now use fresh exact-ID `show` reads; strict component cases
reject missing/foreign, reopened, paused and assigned responses before branch
deletion. Product eligibility and shared environment cleanup still use their
required inventories. The native observer, Merger pool, lost-close/locked-tmp
recovery and conditional Git ref deletion boundaries remain. No deadline,
six-thread limit or assertion is relaxed.

The retained suite keeps broad, fast decision and security checks in unit/coordinator tests and one representative set of expensive process boundaries:

The duplicate-failure classifier timeout smoke uses a controlled role clock and
a startup release file. Its former two-second real-time budget could expire
before the managed fake CLI wrote either PID, testing startup speed instead of
Job containment. The retained native journey now holds startup, consumes one
second of the same two-second role budget, releases startup, waits for both
root/descendant handshakes, then expires the remaining second. Both actual
process termination assertions remain. Real handshake/result/cleanup bounds and
six-thread scheduling are unchanged; failure cancels and settles the invocation
before fixture disposal. `AgentInvocationDeadlineTests` covers startup/prompt
delivery and time already consumed inside native launch without real processes
or sleeps. Production uses `TimeProvider.System`, with the role deadline still
starting before native launch; cleanup continues to use its separate real budget.

| Concern | Detailed fast coverage | Retained real-tool boundary |
| --- | --- | --- |
| Ownership, read-only roles, failure classification, and budget precedence | `AgentPostExitVerifierTests`, `AuthorityMatrixTests`, retry and coordinator tests | One complete Delivery handoff and one Merger completion-owner/real Beads boundary |
| Slot capacity, competing writes, and claim ownership | Pool/coordinator tests | One native guarded claim after evaluation |
| Process creation, inheritable pipe cleanup, job containment, and long paths | Existing focused executor and runtime-file tests | Retained native process/window and published Delivery classifier-force-stop smoke |
| Stop, restart, and preservation | Fast state/worker-observation tests | One second-signal restart case and one published Delivery classifier-force-stop case |
| Cross-pool progress, reservation, and handoff | Planning turn/publication tests and `MergerPoolCoordinatorTests` | `PoolConcurrencyIntegrationTests.DeliveryCompletesWhileMergerPreparationIsHeld` |
| Bead HTTP and workspace safety | Request/authority tests and deterministic observer filesystem fixtures | Native Beads HTTP relation/routing/happy-path cases; junction and Git-admin pointer hazards use strict scripted Git |
| Merge finalization and repository guards | Cheap finalizer and branch-ownership tests | Repeatable owned cleanup, conditional branch deletion, and expected-commit guards |

`MergerPoolIntegrationTests.NativeMergerSynchronizesChecksAndLeavesCompletionToTheCompletionOwner`
uses the continuous runtime's real `CompletedEnvironmentWorker` and shared environment
reservations. Its original 90-second admission deadline covers verified merge, guarded
task closure, cleanup enqueue and pool-capacity release. Before running the worker,
the test requires a queued receipt, the retained ignored artifact and no worktree
removal. It then executes one real worker batch and retains all native assertions
for resolved worktree/tmp absence, conditional branch cleanup, unchanged workflow
fields and retained invocation logs. Admission and worker durations are reported
separately. The former fixture omitted the queue and synchronously ran disposal
inside the pool-completion wait, unlike `ContinuousApplication`; retained failure
and focused reproduction both reached housekeeping after the Agent finished.
The native finalizer recovery case below continues to cover immediate one-shot
housekeeping. No test, deadline or six-thread parallelism is removed or extended.

The native Agent-stage GitHub fixture answers checks-only requests without unused
branch/HEAD probes. Each PR view still obtains fresh real branch, HEAD and remote
main values, now concurrently, and settles all three before returning. It caches
nothing across mutations. Collection weights use the complete 1,243-case
`implement-complete.xml` measurement, including the repaired native Merger path;
the scripted Merger verifier and workspace security cases no longer displace the
longer native collections at startup. These setup/scheduling changes require a
new complete timed run; historical-duration replay is not timing acceptance.

Post-exit observation overlaps its independent status, branch and HEAD reads,
then settles every result before reading fresh tracker state. Independent Merger
verification likewise overlaps its local branch/HEAD/index/rebase-path probes
after the ownership read, settling all before evidence queries or acceptance.
There is no observation cache or overlap across writes. Six controlled component
cases hold the Git responses until every read starts, require no downstream
query before settlement, and retain failure diagnostics and rejection when a
read fails. These cases failed on the serial implementation. The complete timed
suite, with its unchanged six-thread cap, remains the performance gate.

The immutable native Agent-stage template is prepared alongside the existing
independent CLI/trap publishes and Beads template, before collections start.
Previously several of the longest first-wave collections waited on the same
lazy template while occupying test lanes. Every prerequisite still settles on
failure, and tests still receive separate mutable Git/Beads copies. This moves
existing setup work; it adds no cached runtime observation or native scenario.

Observer component cases script both Git root validation and tracker responses,
with exact Git arguments and working directories and real filesystem/backend
guards. They exercise the production projections, budgets, cohorts and HTTP
routes without repeated native launches for an unchanged valid root. The native
observer smoke still uses real Git/Beads and verifies external comments and
dependencies through HTTP. All 17 cases and assertions remain. Collection weights
now use the complete 1,297-case `ns-3i4-prepared-complete.xml` measurement, updated
with the focused observer cost; only scheduling order changes, with six threads.

`MergerOutcomeVerifierTests` keeps its detailed rejection-evidence, local transcript, hosted-job metadata, PR identity, count, and framing matrix on a strict scripted Git fixture. These component cases use stable valid commit identities and temporary log paths; they do not initialize/copy a Git database or start Git. The script matches the production Git arguments and working directory, returns deterministic observations, and fails immediately on an unknown command. The production Merger verifier still parses the real fixture files and makes every acceptance, rejection, identity, and guarded-publication decision; the GitHub and Beads fakes remain process-boundary responses. The ns-3i4 verifier class now discovers 146 cases, including full prompt/document echoes, stale failure frames after passing checks, separately attributable failed reruns, and controlled local-read overlap/settlement. These framing cases reuse the scripted fixture; no additional native journey is introduced. The compact native group below retains the real Git boundary.

`WebBeadWorkspaceBoundaryIntegrationTests.WorkspaceRejectsForeignAndReparseGitAdminPointersBeforeStatusAndRechecksBeforeStatus` and `WorkspaceReportsReparsePointAncestryWithoutFollowingIt` call the production workspace observer against ordinary on-disk repository/worktree metadata and actual Windows junctions. A strict scripted Git runner supplies only the exact worktree-list and common-directory responses and fails on any unexpected command; rejected paths assert that Git status is never requested. The cases retain marker, admin-directory, foreign backlink, reparse backlink, pre-status replacement race, and managed-root ancestry hazards with target/foreign tree snapshots. This removes repeated native Beads initialization and native worktree setup from the security permutations. Native HTTP and Beads coverage remains in `WorkspaceReadsNativeChildrenAndParentWithoutTrackerMutation` and `WorkspaceScrubsHostileAmbientTrackerAndGitRoutingVariables`, preserving relation direction, configured-versus-foreign routing, and a real managed-worktree status read.

The compact native Merger group uses independent copies of one immutable Git template to retain actual branch and HEAD checks, dirty-worktree and unmerged-index detection, active-rebase detection, target-origin mutation during check observation, and reachable/unreachable merge-commit ancestry. Its default-branch fetch request is checked against the validated origin, then mapped to the template's local `main` branch so native Git evaluates the real commit graph without network access. This deliberately reduces native Git process backing across the metadata matrix while preserving the production observation contracts.

Merger finalization retains one native `MergerCompletionFinalizerTests.QaRetainedArtifactsRecoverAfterLostClosureAndLockedTmpRetryAcrossRestart` journey. It exercises the lost authoritative task-close response, completed Product, QA artifacts, an actual locked tmp file, confirmed worktree and branch cleanup, and restart recovery of the persisted tmp retry without replaying completed removals. The duplicate full journey whose worktree-removal response was lost is removed; `CompletedEnvironmentCleanupTests.LostRemovalResponseSurvivesRestartAndResolvesFromFreshAbsenceWithoutReplayingGit` and the existing `CompletedEnvironmentWorkerTests` observation, retry-clock, restart, cancellation, and replay cases retain the cheaper response-loss decisions.

`MergerCompletionFinalizerTests.LocalBranchMovedAfterValidationSurvivesConditionalDeletion` now uses a small real Git repository and a deterministic closed-task `bd list` response. The test moves the owned ref to a real child commit immediately before forwarding the original `git update-ref -d <ref> <expected-head>` compare-and-delete to Git, then verifies that Git rejects deletion and preserves the moved ref. It no longer initializes or queries native Beads, and the production closed/unpaused guard and Git compare-and-delete remain exercised.

The current native cross-pool smoke holds a real Merger claim and singleton reservation before Agent launch while one unrelated native Delivery Implementer completes its `stage:review` claim handoff. It verifies both task worktrees remain on their expected branches, then cancels and drains the still-held Merger without releasing its preparation gate or launching a Merger Agent. Fast Planning coordinator/publication tests retain the removed pause, resume, graph and replay decisions; the standalone native Planning approval/claim-release test, complete Delivery handoff, and native Merger completion remain separate boundaries. The deliberate coverage reduction removes one combined native Planning-publication/interruption/generated-task/Delivery/merge journey, rather than replaying those checks in several expensive integration tests.

The published active-classifier force-stop smoke retains its native Delivery variant as `PublishedContinuousStopIntegrationTests.PublishedRuntimeForceStopsAnActiveDeliveryOutcomeClassifier`. Its Planning-specific native theory row is intentionally removed; fast `PlanningSlotCoordinatorTests.StopBetweenTurnsRetainsStageAndNeverStartsNextTurn` and `PlanningPoolCoordinatorTests` cancellation/stop cases retain Planning stop semantics. The failed hosted attempt reported a Planning reservation without the fixture's `agent.started` handshake within the existing 90-second bound, but did not establish a shared production cause. Removing this native Planning process-level composition is a deliberate coverage/runtime tradeoff, not a production repair. Discovery is expected to fall by one case; the committed positive floor remains 995.

`WindowsConsoleProcess` owns an outer test Job, assigned while the console root is suspended. Disposal terminates that group and waits for retained native process handles within the existing five-second budget before disposable repository deletion. Root exit and zero active Job processes alone do not prove native handle rundown. The logged/unlogged descendant regressions use a native `dolt sql -q 'SELECT SLEEP(30)'` process holding its Dolt `LOCK` after root exit, then require immediate lock deletion after test disposal. The former `bd --readonly --sandbox sql` command is unsupported in embedded mode: it exited with `Error: 'bd sql' is not yet supported in embedded mode` after a brief startup lock, which had falsely looked like a live operation. The fixture now waits for a lock-ready signal, the test retains the Dolt process handle and releases the root, and the native process must remain alive with `LOCK` held until disposal. This test-owned teardown does not add waits or compensating writes to production force-stop.

The ns-j7t native observer smoke now writes an external comment and a blocking dependency before one refresh wait. It requires the same returned envelope to contain both the marker and exact blocker relation with a changed revision. `ExternalMembershipAndCommentsChangesAreReadEvenWithUnchangedInventory` still covers comments-only invalidation at the component level. The already-merged recovery smoke reuses one native setup for failed checks followed by a new successful invocation, each with a distinct invocation ID, environment snapshot and `gh` log. Failure must leave merge evidence, task workflow state, origin main and the deleted remote branch unchanged; the fresh attempt must query the PR and required checks again. The cheap missing/nonpassing-check and current-head verifier tests remain. The deliberate reduction removes separate native comment-only refresh attribution and one repeated recovery fixture start (one theory row); it does not establish a fix for the known cold observer budget failure. The retained test-count floor remains 995.

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
