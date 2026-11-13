# Integrated console polish — ns-1b1.6

Baseline: `566899e1993469cc9f4ab624956093d5dc1746f2` (the dispatched `origin/main`, including ns-1b1.2–5). Candidate: this PR. Evidence captured on 2026-11-13 with headless Chromium and the isolated QA AppHost. No desktop Computer Use.

The Attention Desk initially renders two cards in server order while announcing all paused work. Show all/fewer preserves queue and disclosure state. Context previews have an explicit full-text disclosure; guidance has its own disclosure, retaining drafts and action state. Shared header, section headings, spacing and controls now follow the console tokens. All-idle Delivery slots form a compact wrapping row. Explorer controls and table spacing expose initial results in the idle desktop viewport.

## Before and after

| State | Baseline | Candidate |
| --- | --- | --- |
| Busy, 1440×900 viewport | [Before](console/baseline/busy-1440-viewport.png) | [After](console/candidate/busy-1440-viewport.png) |
| Busy, 375px | [Before](console/baseline/busy-375.png) | [After](console/candidate/busy-375.png) |
| Busy, 720 CSS px | [Before](console/baseline/busy-720.png) | [After](console/candidate/busy-720.png) |
| Idle/caught up, 1440×900 viewport | [Before](console/baseline/idle-1440-viewport.png) | [After](console/candidate/idle-1440-viewport.png) |
| Idle/caught up, 375px | [Before](console/baseline/idle-375.png) | [After](console/candidate/idle-375.png) |
| Idle/caught up, 720 CSS px | [Before](console/baseline/idle-720.png) | [After](console/candidate/idle-720.png) |
| Long context, 1440px | [Before](console/baseline/long-expanded-1440.png) | [After](console/candidate/long-expanded-1440.png) |
| Long context, 375px | [Before](console/baseline/long-expanded-375.png) | [After](console/candidate/long-expanded-375.png) |
| Long context, 720 CSS px | [Before](console/baseline/long-expanded-720.png) | [After](console/candidate/long-expanded-720.png) |
| Stale/read error, 1440px | [Before](console/baseline/retained-error-1440.png) | [After](console/candidate/retained-error-1440.png) |
| Stale/read error, 375px | [Before](console/baseline/retained-error-375.png) | [After](console/candidate/retained-error-375.png) |
| Stale/read error, 720 CSS px | [Before](console/baseline/retained-error-720.png) | [After](console/candidate/retained-error-720.png) |

The busy fixture has 64 completed digest milestones in seven threads plus standalone items, 211 inventory Beads, four paused Beads, two occupied Delivery slots plus one idle, and an occupied Merger. Long-context evidence uses a 200-character unbroken title, long author attribution, full current questions and deep worktree paths. The 720 CSS px viewport exercises the reflow at 200% desktop zoom. The candidate has no page-level horizontal overflow. Full-page captures show vertical scrolling intentionally retained for expanded details; viewport captures show the initial fold. Expanded Attention queue evidence: [1440px](console/candidate/expanded-queue-1440.png), [375px](console/candidate/expanded-queue-375.png), [720 CSS px](console/candidate/expanded-queue-720.png).

Detailed acceptance captures from the same browser checks:

| Surface | 1440px | 375px | 720 CSS px |
| --- | --- | --- | --- |
| Expanded digest | [Desktop](console/details/digest-expanded-1440.png) | [Narrow](console/details/digest-expanded-375.png) | [Zoom reflow](console/details/digest-expanded-720.png) |
| Large Explorer | [Desktop](console/details/explorer-1440.png) | [Narrow](console/details/explorer-375.png) | [Zoom reflow](console/details/explorer-720.png) |
| Invocation output | [Desktop](console/details/invocation-1440.png) | [Narrow](console/details/invocation-375.png) | [Zoom reflow](console/details/invocation-720.png) |

Visual review checked hierarchy, readable wrapping and reachable controls at all three widths. Busy desktop exposes runtime health, occupancy and the identities/roles/stages of all three occupied slots before scrolling; invocation controls continue below the fold. Idle desktop exposes the clear row, runtime identity, idle slots, Explorer heading and first result. No fixed height clips content.

## Acceptance matrix

| Contract | Checks and evidence |
| --- | --- |
| Attention bounds and refresh | `console-qa.mjs`: first two IDs preserve priority order, full count, exact toggle label/expanded state, draft and selection focus across changed refresh, collapse focus fallback, disclosure persistence and delayed note-action busy/disabled state. Full context remains reachable. |
| Loading/empty/error | Clear row is at most 64px (56px target); failed reads remove the clear presentation and retain the last confirmed list with an explicit unavailable status. [Failed read after clear](console/candidate/empty-read-failed.png). |
| Expanded histories | `digest-qa.mjs`: real checkpoint save/persistence, 5/4/6 disclosure bounds, 140-character summary boundary, keyboard/focus, removed items, windows, save/read failures and activity after catch-up. Fresh captures in `TestResults/digest`. |
| Large tracker/intake | `explorer-qa.mjs`: 211 Beads, every filter limited to 15 rows, range/page controls, off-page search, cancellation, boundary focus, clamping, native intake, created-ID discovery and Show in All/failed-refresh recovery. Re-run after final Explorer spacing. |
| Factory/diagnostics | `factory-qa.mjs`: observed occupancy, unavailable metadata, runtime-scoped disclosure, changed polls/focus, runtime replacement, preparation/failure/pause, retained data, idle retained invocation identities and deep paths. Re-run after idle layout changes. |
| Health/invocation detail | `invocation-qa.mjs`: all five health presentations, identity retention, follow/scroll/focus, byte/line bounds, ANSI/control sanitization, reset/de-duplication, terminal status, modal containment, Escape/Close and Workspace navigation. |
| Native execution/output | `telemetry.mjs`: two native fixtures, isolated output, interrupted EventSource/reconnect with Last-Event-ID, no duplicate output, completion/end without retry, follow controls, Escape/focus and stale occupancy. |
| Retained operator flows | `workspace-qa.mjs`: native Save Note/Resume/Retry/confirmed Abort, pause/budget semantics, stale action blocking and explicit refresh, unknown outcomes, draft preservation, route/action races, history/source, sanitizer, navigation and responsive Workspace geometry. `planning-readiness-qa.mjs`: stale 409, explicit authorization, reload persistence, mobile layout. |

All browser scripts passed with no uncaught page errors or unexpected console errors. Deliberate 404/409/503 fixtures were explicitly checked. Deterministic response fixtures test rendering; native telemetry, intake, guidance and readiness checks independently exercise the real disposable host/tracker. Fixtures never mutate the user's tracker.

## Release gate and teardown

`dotnet build Nightweaver.slnx -warnaserror` passed with zero warnings/errors. `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 647 --report-xunit-xml --report-xunit-xml-filename ci.xml --show-slowest-tests 20` passed all 681 tests with zero failures/skips. The final complete suite took **103.44 seconds**, including its build, fixture publishing and teardown (an earlier complete run took 107.39 seconds). The committed six-thread parallelism is unchanged; no tests were removed or disabled. This measures the complete retained suite on the integrated candidate based on dispatched `origin/main`, not a filtered subset.

Each QA stack used `aspire start --isolated --non-interactive`, `aspire wait web`, and direct `web` HTTP endpoint discovery from `aspire describe`. The exact `Nightweaver.QA.AppHost.csproj` was stopped successfully in cleanup/finally after browser runs, including failed assertions. Observed disposable endpoints included ports 14827, 25183, 27316, 24582 and final 19420; none was assumed. Browser instances close in each script's `finally`. Ignored build/test evidence is retained for Orchestrator-owned cleanup.
