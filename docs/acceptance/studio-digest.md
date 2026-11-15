# Studio Digest acceptance

## QA drain repair verification

Candidate `0e3dbc7f2bd971e92e8ce1fb5ecdddc40957d358` passes the complete
documented suite: **1,243 discovered/passed, zero failed/skipped, exit 0,
outer 262.0749291 seconds**, including build, fixture publishing, execution
and teardown at the committed six threads. Report:
`TestResults/fixture-launch-full.xml`. This is candidate validation;
latest-main timing still requires measurement after integration.

The preceding QA run at `a4204eb` passed Digest behavior and visual checks but
failed `NativeLaunchBurstDoesNotDependOnAvailableThreadPoolWorkers` while
awaiting coordinator stdout after root exit and Job-empty confirmation.
Its 1,240/1,241 result and 334.9294306-second wall time remain preserved in
`TestResults/qa-full.xml` and the `20261115T2159449750013Z-qa` invocation.

A controlled native reproduction pinned an unrelated managed helper launch
inside the native launcher's temporary inheritable-pipe window. With the native
root settled and its Job empty/closed, stdout still timed out; releasing the
live foreign helper immediately allowed both drains to finish. HANDLE_LIST
limits the native child, but an unrelated managed CreateProcess can inherit
the same temporary writers. Production runners already share
`WindowsProcessLauncher.ProcessCreationGate`; test-owned raw `Process.Start`
calls did not. This reproduces the exact QA failure mechanism. QA did not record
the historical writer owner, so its identity cannot be established retrospectively.

All managed starts in the shared test process now go through `FixtureProcess`,
which holds that existing gate only during process creation. Shared manual
Delivery/QA helpers use it too. Both Start overloads have small contention
regressions observed failing before the guard and passing afterward; their
combined full-suite cost is 1.190 seconds. The existing native blocked-drain and
launch-burst smokes remain and pass in 16.446s and 1.664s respectively. Native
observer and Merger completion checks also pass (30.148s and 86.838s). No
production code, source deadlines, drain timeout, coverage or scheduling cap was
changed. Temporary launcher instrumentation and the diagnostic test were removed
before final validation; their source/patch and result remain in invocation evidence.

Solution build with `-warnaserror --no-restore` passes with zero warnings/errors;
Studio tests pass 46/46 and TypeScript/Vite build passes with existing notices.
Both fresh isolated Digest browser probes pass: real checkpoint persistence and
concurrent unread activity, automatic updates, all windows, last-good errors,
lineage/disclosure bounds and fifth-root/fourth-child/sixth-standalone focus and
viewport preservation. Fresh captures at 1440/375/720 are retained under this
invocation's `digest` and `digest-threads` directories. Desktop compact, mobile
retained-error and focused-child captures were visually inspected for readable
wrapping, reachable controls, visible focus and explicit retained-state context.
The exact Aspire AppHost stopped successfully before the full suite; `aspire ps`
returned an empty list. No hosted CI was dispatched.

Current evidence root:
`H:/Nightweaver/prod/tmp/nightshift/ns-mbd.6/20261115T2210416116096Z-implementer`.
It contains `full-suite.log`, `full-suite-time.json`, the controlled reproduction
source/patch/result and both browser logs/capture sets. Focused red/green reports
are `TestResults/fixture-launch-red.xml` (two failures before repair) and
`TestResults/fixture-launch-green.xml` (four passes including both native smokes).
Historical findings and failed runs below remain evidence, not current acceptance.

## Native budget repair verification

Candidate `d24eabf555776bb74ff24ddcb029f74cc767635f` passes the complete
documented six-thread suite: **1,241 discovered/passed, zero failed/skipped,
exit 0, outer 260.2864187 seconds**, including build, fixture publishing,
execution and teardown. Report: `TestResults/digest-budget-full.xml`.
The stopwatch and full log are retained under
`H:/Nightweaver/prod/tmp/nightshift/ns-mbd.6/20261115T2136029552093Z-implementer`
as `final-time.json` and `final-full.log`. This establishes candidate correctness
and timing; latest-main timing remains a separate post-integration measurement.

The retained failed Merger fixture had a completed Agent log, a closed,
unassigned task and no worktree registration, but still had its local branch.
Its timeout therefore reached post-close finalization. Repeated whole-inventory
task guards amplified cumulative native command cost. A read-only comparison
under concurrent load measured inventory at 2.127s and exact-task `show` at
0.750s. Finalization now performs three fresh exact-task reads instead of those
three inventories; all mutation, ownership and expected-head checks remain.
Product and shared cleanup inventory reads remain. The full native Merger test
passes in 90.419s total, including setup outside its unchanged 90-second
admission-completion bound (17 inventories and three exact-task reads).

The observer spent its four-second sweep on unused Ready/Blocked memberships
even for Workspace-only subscriptions. Three deterministic 1.4-second-cost
cases reproduce that unnecessary work and lost Workspace detail turn on the
old code; they pass after demand-scoping membership reads. The existing native
HTTP comment/dependency smoke passes in 31.041s, with cold data in 15.960s,
combined external changes in 8.668s and 31 commands. The unchanged source
deadlines still expose slow reads as stale; no process-settlement or historical
sharing-lock cause is claimed for these failures.

The strict conditional-ref regression also failed on the old inventory query
and now passes on the exact-task query. Five cheap guard cases reject missing,
foreign-ID, reopened, paused and assigned tasks before deletion. Ten focused
regression/boundary cases pass (`TestResults/narrowed-corrected.xml`). The first
new guard fixture needed a valid root Git registration, and the checkpoint clock
now crosses its second during the inventory itself rather than depending on
unused membership work. Assertions remain intact. No expensive native case,
timeout increase, retry, sleep, skipped test or reduced parallelism was added.

Both isolated browser probes pass again on the repaired source, with real
checkpoint/concurrent-update behavior and all focus/lineage assertions intact.
Fresh captures in `TestResults/digest-budget-repair` and
`TestResults/digest-threads-budget-repair` cover 1440/375/720 layouts. Desktop
compact, mobile retained-error and focused-child captures were visually reviewed:
readable wrapping, visible focus, reachable controls and explicit retained state.
The exact QA AppHost stopped successfully before the complete test run.
Solution build passes with zero warnings/errors; Studio tests pass 46/46 and
TypeScript/Vite build passes with the existing vendor/directive notices.

A temporary process-timing diagnostic run is **not validation**: its shared log
caused one instrumentation-only sharing exception (1,232/1,233 passed,
301.929s). `diagnostic-full.xml`, its log and measurements remain preserved.
All diagnostic code was removed before the passing complete run. Native
measurements support command-cost investigation; that run does not erase or
explain every earlier timeout. PR #43 remains draft; no hosted CI was dispatched.

## Resumed verification after the shared repair

**Earlier verification was blocked.** That complete run reported the native
observer and Merger timeouts described below. The earlier passing rebase run
does not establish reliable complete-suite acceptance.

The validated 2026-11-15 retry starts from retained candidate
`d371cdc24b21c07d0e3780a2c8444a50a76c99eb`, rebased onto main `72ae9de`.
The candidate includes the integrated ns-mbd.5 repairs: `a4a8212` retries
Windows readiness sharing/lock violations within the original deadline and
batches fresh Planning verification reads; `3de792e` batches lifecycle readiness
reads. Subsequent merged test/runtime work is also present. This reassessment
uses those integrated repairs and new verification; it does not attribute all
historical timeouts to the readiness exception or discard the failures below.

Both adapted probes passed again on a fresh exact isolated AppHost:
`digest-qa.mjs` and `digest-threads-qa.mjs`. Real same-origin checkpoint persistence,
concurrent external activity remaining unread, external live updates, all windows,
last-good retention and late-save filter isolation passed. The fourth child,
sixth standalone item and fifth root retain focus/disclosure/viewport across
insertion; actual removal restores the Digest disclosure focus. The exact
AppHost stopped successfully in `finally` before complete-suite verification.

Fresh 1440/375/720 captures replace the linked representative images below.
Visual review covered compact desktop/mobile, expanded 720px, empty, caught-up,
retained-error and focused-child states: long titles wrap, actions remain
reachable, focus is visible and source errors retain their context. Probe output
is preserved in `TestResults/digest-resume-20261115` and
`TestResults/digest-threads-resume-20261115`.

Studio `npm test` passes all 46 tests. `npm run build` passes TypeScript/Vite;
Vite reports the existing vendor-script and React Router directive notices.
`dotnet build Nightweaver.slnx -warnaserror --no-restore` passes with zero
warnings/errors. Three mixed line endings in the retained checkpoint regression
were normalized; its behavior is unchanged.

The retained maintenance-rebase report independently confirms 1,233/1,233 passed,
zero failed/skipped, exit 0 and 200.2115171 seconds including build/publish/teardown
at normal six-thread parallelism (`TestResults/digest-rebase-full.xml`,
`TestResults/rebase-20261115/time.json`). This is candidate evidence; latest-main
timing must still be measured after integration. Hosted verification remains
Merger-owned and the PR stays draft.

The fresh complete command is `dotnet test --solution Nightweaver.slnx
--minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename
digest-resume-full.xml --show-slowest-tests 20`, with the committed six threads.
Result: **1,231/1,233 passed, two failed, zero skipped, exit 2; 312.5061735
seconds** outer wall time including build, fixture publishing, tests and teardown.
This fails both correctness and the five-minute candidate timing check.
`NativeBdSmokeDetectsExternalCommentsAndDependenciesAndReportsCost` timed out
waiting for the combined external comment/dependency update after its initial
Workspace read succeeded (line 265). The retained envelope reports repeated
`source-timeout` for detail, comments, relations and inventory, with last-good
data still visible. `NativeMergerSynchronizesChecksAndLeavesCompletionToTheCompletionOwner`
also timed out at `MergerPoolIntegrationTests.cs:97`. Its fixture is retained at
`H:/Nightweaver/prod/tmp/nightshift/ns-mbd.6/20261115T2113595433551Z-implementer/nightweaver-17fcf1de692c4ada8b3d82b8190ea56c`.
The causes are not established. No native repair, deadline increase, coverage
reduction or broad rerun was used to hide these failures. The full log and outer
stopwatch evidence are preserved in that invocation's `full-suite.log` and
`full-suite-time.json`.

## Historical implementation and repair evidence

The following results describe the earlier candidates before the shared repair
integrated. Their failed reports and invocation evidence remain preserved.

The 2026-11-15 review repair reproduced the fourth-child focus loss in the
adapted browser probe before the fix (`false !== true` for the original control's
connected/focused identity). Child and standalone previews now retain only
surviving focused/open identities beyond their normal bounds. The probe passes
with both the fourth-child and sixth-standalone summary expanded and focused
while activity is prepended: identical DOM control, open summary and viewport
position within three CSS pixels. Moving focus away preserves the open summary;
removing the actual item returns focus to the Digest disclosure. The standalone
preview returns to six after removal. Existing fifth-root, keyboard, navigation
and 375/720 reflow assertions also pass.

Reviewed repair captures: [child boundary](studio-digest/focused-child-boundary.png)
and [standalone boundary](studio-digest/focused-standalone-boundary.png). Both show
readable long titles and the retained control's visible keyboard focus. The full
`digest-qa.mjs` checkpoint/window/error probe and all 39 Studio tests also pass
after the repair. The exact isolated AppHost was stopped successfully before
the new complete-suite run. Prior failed-run evidence below remains preserved.

Repair complete-suite run: 1,194/1,195 passed, zero skipped, exit 2, **469.295
seconds** wall time including normal build/publish/teardown at the committed
six-thread parallelism. Both previously failing Planning concurrency and Merger
cases passed in this run. The sole failure was
`AgentStreamCleanupIntegrationTests.NativeExecutorAndManagedRunnerExposeBothBlockedDrainsWithReleaseControls`:
the fixture's `WaitForJsonAsync` attempted to read `scenario/holder.json` while
another process held the file, producing `IOException` at
`tests/ExecutionCrashHost/AgentStreamCleanupCoordinator.cs:520`. That helper
catches malformed JSON but not sharing errors. This evidence explains the
reported exception, without establishing the causes of the earlier timeouts.
Those native tests/helpers and production lifecycle code remain unchanged.
Full-suite correctness and timing acceptance remain blocked; no passing full
suite or latest-main timing is claimed. Evidence: `TestResults/digest-repair-full.xml`
and `H:/Nightweaver/prod/tmp/nightshift/ns-mbd.6/20261115T0253575215179Z-implementer/full-suite.log`.

Local browser checks on 2026-11-15 used this branch's isolated QA AppHost, with
its discovered direct web endpoint and disposable `qa` tracker. The exact AppHost
was stopped after the probes. No production tracker or runtime was changed.

`digest-qa.mjs` passes on the Studio Catch-up route: a real same-origin checkpoint
POST is held while an external Bead is created, the persisted bookmark equals the
displayed snapshot cutoff, concurrent activity remains visible, and a second
external change appears automatically without changing the bookmark or focus.
Deterministic v1 resource projections exercise five/four/six preview bounds,
independent long-summary disclosures, Show more/fewer, filter changes, last-good
read failures, rejected/unverifiable saves, empty and caught-up states. A failed first read for a newly selected window keeps
the prior successful resource visible; a delayed save cannot overwrite a filter
chosen while it waited. Browser
page errors and unexpected console errors fail the probe.

`digest-threads-qa.mjs` passes: compact 1/4/8-child cases, independent keyboard
activity controls, stable focused fifth-thread identity after insertion, incoming
collapsed threads, Show fewer restoring the preview, unique IDs and real root
Workspace navigation. The focused control also retains its measured viewport position after insertion.
No horizontal overflow at 1440/375/720 CSS pixels.

Representative captures were visually reviewed for readable titles, reachable
controls, compact lineage summaries and source-state clarity:

- [Compact desktop](studio-digest/compact-1440.png), [375px](studio-digest/compact-375.png)
- [Compact lineage desktop](studio-digest/lineage-1440.png)
- [Expanded activity at 375px](studio-digest/activity-375.png), [720px](studio-digest/expanded-720.png)
- [Focused boundary after insertion](studio-digest/focused-boundary-1440.png)
- [Caught up](studio-digest/caught-up-375.png), [empty desktop](studio-digest/empty-1440.png), [375px](studio-digest/empty-375.png), [720px](studio-digest/empty-720.png), [retained read error](studio-digest/retained-error-375.png), [failed new window](studio-digest/failed-window-1440.png)

The full candidate .NET suite discovered 1,195 tests: 1,194 passed, one existing
native `PoolConcurrencyIntegrationTests.PlanningPublishesWhileDeliveryAndMergerProgressIndependently`
timed out waiting for recovered Planning completion at line 211. Wall time was
466.750 seconds including build/publish/teardown; this does **not** satisfy the
five-minute suite requirement. The run overlapped the isolated browser QA stack;
causation is not established. `TestResults/timing.xml` and the preserved native
fixture under the invocation tmp contain the failure evidence. No native runtime
code, test parallelism, timeout or existing coverage was changed for this slice.

`dotnet build Nightweaver.slnx -warnaserror` passes. Studio `npm test` passes
39 tests, including displayed-cutoff capture, duplicate prevention, invalid save
receipts, transport/body deadlines and late-response isolation. A focused .NET
run with fully qualified `--filter-class` selectors for
`Nightweaver.Tests.OperationalObservationSourceTests`,
`Nightweaver.Tests.WebBeadsIntegrationTests` and
`Nightweaver.Tests.StudioWebTests`, plus `--minimum-expected-tests 25`, passes all
40 tests (`TestResults/digest-focused.xml`). The added shared-observer case checks
the pre-read, whole-second cutoff and unread activity in that same second.

With the QA stack stopped, the complete-suite rerun discovered the same 1,195
tests: 1,193 passed and two native tests timed out. The Planning concurrency case
again failed at line 211; `MergerPoolIntegrationTests.NativeMergerSynchronizesChecksAndLeavesCompletionToTheCompletionOwner`
also timed out at line 88. Total wall time was 415.888 seconds. Both correctness
and timing remain blocked; neither this rerun nor focused passes establish full
suite acceptance. The cause is not established, and those native test files and
production lifecycle implementations are unchanged by this Digest slice.
Evidence: `TestResults/timing-rerun.xml` and the invocation tmp's
`full-suite-rerun.log`; failed native fixtures remain preserved. No further broad
rerun or unrelated runtime repair was attempted.
