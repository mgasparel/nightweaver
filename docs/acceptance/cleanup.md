# Housekeeping acceptance — ns-0ol.4

Verified on 2026-11-13 against main `7ac61eb0fac06af660295287783e349801be7340` plus this implementation. Main was independently reread after browser acceptance and remained that commit. All operations used this isolated worktree and disposable fixtures; no production runtime or Project data was tested or changed.

## Delivery and cleanup stay independent

The console exposes the shared validated cleanup evidence and live-owner Retry inbox. Its default view is a compact count summary. Details provide eight-row pages, full-inventory search/state filters, exact worktree/tmp resources, confirmed absence, attempts, last/next time, reasons and resolution evidence. Stopped/stale/unavailable owners cannot accept Retry. Missing/corrupt evidence has unknown counts, rather than an empty healthy queue. Expired, protected, stale, queued and accepted/attempted request outcomes remain distinct from confirmed cleanup.

The default QA AppHost remains unchanged unless `NIGHTWEAVER_QA_SCENARIO=cleanup` is selected. That scenario creates a disposable closed task with retained QA output and induces a real Windows sharing failure by locking its tmp report. Injected time exercises four bounded attempts, persists needs-attention, and reconstructs the cleanup worker from the stored obligation. A paused task's independent tmp sentinel remains protected.

`cleanup-qa.mjs` used the real same-origin browser endpoint and atomic runtime inbox, not substituted HTTP mutations. On the final run:

- Displayed failed task `qa-7cj` had four total attempts after worker reconstruction.
- Stale revision and protected paused-task POSTs returned 409 without cleanup.
- The Retry button queued inspection; subsequent HTTP observations confirmed attempt five, an accepted request acknowledgement, resolved status and confirmed absence. Filesystem access independently confirmed that exact tmp directory was gone.
- Delivered Bead fields remained identical and closed; the protected paused sentinel retained its original bytes.
- The read fixtures subsequently exercised 27 obligations with eight-row pages, search beyond the first page, state filtering, preserved focused disclosure across refresh, queued/expired request outcomes, disabled stopped-owner actions, and unavailable/unknown counts.
- Browser page errors and unexpected console errors: zero. Expected stale/protected HTTP 409 errors are explicitly excluded from the console-error assertion.

The first browser harness revision advanced from an asynchronous predicate before the owner consumed the request and inspected the queued four-attempt snapshot too early. Retained JSON established this harness issue. Explicit bounded polling of completed HTTP observations now requires resolved status and confirmed absence before asserting attempt five. No runtime counter repair was needed.

## Integrated native and automated checks

`dotnet build Nightweaver.slnx -warnaserror -v minimal` passed with zero warnings/errors. Latest-main build wall time before the suite: 4.184431 seconds; final static-asset refresh build also passed.

```powershell
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 813 --report-xunit-xml --report-xunit-xml-filename ns-0ol.4-latest-main.xml --show-slowest-tests 20
```

Result: **813 passed, 0 failed, 0 skipped**. Actual stopwatch wall time: **137.1112771 seconds**, including the normal solution build, native fixture publishing, tests and teardown. The committed six-thread cap and current-main collection scheduling were preserved. This is below the 300-second complete-suite budget. XML is retained in `TestResults/ns-0ol.4-latest-main.xml`; command log and stopwatch result are under `H:/Nightweaver/prod/tmp/nightshift/ns-0ol.4/latest-main-suite.log` and `latest-main-result.json`.

The complete run includes both native `MergerCompletionFinalizerTests.QaRetainedArtifactsRecoverAfterLostClosureAndRemovalResponsesAcrossRestart` cases: real retained ignored worktree output before verified completion, locked tmp failure, persisted restart recovery, confirmed resource/registration absence, protected unrelated checkout/logs and unchanged completed task/Product. These existing native cases complement the actual browser-to-inbox-to-worker flow without adding another expensive serial merge matrix.

Detailed cheap coverage includes corrupt/missing evidence, full inventory paging/search, age/count retention, idle retention without disposal, HTTP origin/body/query/revision/paused/owner guards, duplicate queue coalescing and unchanged obligations on queue acceptance. Existing worker/disposal coverage retains sharing retry intervals and budget across restart, lost acknowledgement, owner replacement, graceful/force stop, active reservations, paused/reopened/foreign/unsafe/dirty resources, policy/access denial, interrupted attempts, required persistence failure and dry-run protection. New retention removes only validated resolved bookkeeping older than 30 days or beyond the latest 100 records; unresolved/unknown/corrupt evidence and resource/log directories remain protected.

## Isolated browser and visual evidence

Started with `aspire start --isolated --non-interactive --apphost ./Nightweaver.QA.AppHost.csproj`, waited for `web`, and discovered the direct HTTP endpoint through `aspire describe --format Json`. Final endpoint: `http://localhost:22070`. Ran `node cleanup-qa.mjs`, `node telemetry.mjs` and `node smoke.mjs`. All passed. The exact AppHost stopped successfully through `aspire stop --non-interactive --apphost ./Nightweaver.QA.AppHost.csproj` in `finally`. Earlier failed harness runs also stopped their exact stacks in `finally`. No desktop Computer Use was used.

Visually reviewed compact/default hierarchy, expanded paths and reasons, visible keyboard focus, stopped controls and unavailable state. No page overflow at 1440px, 375px or 720 CSS px. The original final browser logs and durable before/after fixture evidence remain under `TestResults/cleanup-final*`.

- [Compact default, 1440px](cleanup/compact-1440.png)
- [Expanded states, 1440px](cleanup/states-1440.png)
- [Expanded states and long paths, 375px](cleanup/states-375.png)
- [Expanded states and keyboard focus, 720 CSS px](cleanup/states-720.png)
- [Stopped owner, Retry disabled](cleanup/stopped.png)
- [Unavailable store, unknown counts](cleanup/unavailable.png)
- [Real confirmed cleanup after Retry](cleanup/real-resolved.png)

Housekeeping attention does not clear historical workflow pauses, reopen delivered work or reset repair budgets. Retry cannot override permissions, policy or uncertain ownership. Store/persistence failures remain explicit; data relocation requires independently verified configuration and identity reconciliation. Implementation and parent closure remain subject to Orchestrator verification and merged children.
