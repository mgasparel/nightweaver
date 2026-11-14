# Continuous Planning acceptance

Verified locally on Windows on 2026-11-14 for ns-jbp.11. The candidate starts at `3c316dcd0f5d27e575b64448a8316ac8a8325d65`; `git ls-remote origin refs/heads/main` matched that revision before the gate. This evidence covers the candidate, not a future merged tree or hosted CI result. The Orchestrator owns independent verification and merge.

## Runtime and recovery

`PoolConcurrencyIntegrationTests.PlanningPublishesWhileDeliveryAndMergerProgressIndependently` uses disposable native Git/Beads, real Planner/Challenger invocations and the existing deterministic fixture classifier. Planning preparation stays reserved while an unrelated native Implementer succeeds and the singleton Merger holds its preparation reservation. Delivery gracefully hands off at verified `stage:review` and the unrelated task is preserved paused. Challenger approval retains the continuous claim; a graceful boundary then releases the owned approved-stage claim without publishing or starting another role. A tmp sentinel survives interruption, a technical pause, refusal while paused, guarded Retry and a replacement Planning pool. Approved-stage recovery publishes the complete two-task graph without replaying Planner/Challenger. A fresh Delivery pool discovers the newly published independent task and runs its native Implementer through verified `stage:review`; its dependent remains blocked by native ordering. The singleton Merger performs a real merge alongside generated implementation after publication, preserving stable Planning grounding. The dedicated native Delivery test covers the full three-role journey; focused Merger pool tests cover queued successor capacity without repeating those journeys here.

The existing standalone native Planning journey remains retained. Cheap pool/slot tests cover backlog and invalid inputs, exact native readiness and ownership, same-actor exit-13 guard conflicts, uncertain acquisition/release, late pause/readiness/owner changes, capacity and priority order, duplicate evaluations, release-before-notification, lossless accounting, backpressure for blocked/deferred/assigned/merge tasks and settlement of admitted work. First-stop boundaries preserve both challenged and approved stages; force stop permits no compensating claim release. Existing publication permutations continue to verify partial graph holds, stable-ID recovery and changed-graph pauses.

The published CLI force-stop test now exercises both Delivery and Planning outcome classifiers: first stop drains the current classifier, second stop promptly closes the native Job and leaves the owned interrupted state. The cleanup worker test verifies coalesced Planning hints, reservation exclusion, persistence after graceful drain and restart disposal through the shared owner while retaining logs. The three-pool discovery test consumes the actual Planning completion contract.

## Browser observations

Started the exact `tests/Nightweaver.QA.AppHost/Nightweaver.QA.AppHost.csproj` with `aspire start --isolated --non-interactive`, waited for `web`, and obtained its direct `http://localhost:14304` binding using `aspire describe`. Ran `smoke.mjs`, `planning-telemetry-qa.mjs` and `telemetry.mjs` successfully. Finally stopped that exact AppHost with `aspire stop --non-interactive`; a subsequent `aspire ps --format Json` returned `[]`.

The Planning browser fixture is an actual native marker process launched through the Planner invocation preparer and executor. It proves observed PID/runtime/invocation/log identity and stream isolation; the cross-pool test above separately proves the actual Planning workflow. Only stale and legacy snapshot presentations are substituted. Actual process completion produces Planning idle. Keyboard Enter/Escape and return focus, 1440/720/375px reflow, no document overflow and no browser errors pass. Delivery regression checks retain EventSource reconnect/Last-Event-ID, duplicate-free replay, terminal retry cessation and 200% layout coverage.

Visually reviewed the desktop bays and mobile invocation drawer. Retained evidence:

- [1440px live pools](continuous-planning/planning-live-1440.png), [720px](continuous-planning/planning-live-720.png), [375px](continuous-planning/planning-live-375.png).
- [375px Planning output](continuous-planning/planning-log-375.png), [actual idle](continuous-planning/planning-idle.png).
- [Qualified stale observation](continuous-planning/planning-stale.png), [legacy Planning unavailable](continuous-planning/planning-legacy.png).
- [Native identity and browser results](continuous-planning/results.json).

## Complete local gate

Ran `dotnet build Nightweaver.slnx -warnaserror`, followed by unfiltered `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 775`. The minimum was a positive floor; the complete suite actually discovered and passed **1031 tests**, with **0 failures and 0 skips**. Documentation now records 1031 as the normal retained minimum. `TestScheduling.cs` remains unchanged at six threads; no classes were excluded.

The build reported zero warnings/errors (1.90 seconds). The test runner reported 4m 46.124s, including native fixture publishing and teardown. One enclosing stopwatch measured **290.7546363 seconds** (4m 50.75s) for build plus the complete test command. This passes the local five-minute requirement with about nine seconds of margin; it does not predict hosted CI timing. No waiver or causal claim about ns-1ve/ns-jbp.13 is made.

`git diff --check` passes. Earlier failed disposable native fixtures and their diagnostics remain preserved under the current Bead invocation tmp tree. No production runtime, foreign checkout or inherited content was changed or cleaned. No Bead status, stage, pause, assignee or repair-counter transition was made by the Implementer.
