# Studio Digest acceptance

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
