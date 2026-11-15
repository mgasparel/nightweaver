# Studio Delivery acceptance — ns-mbd.5

Implemented against starting main `8fd34028be5178d5ff95ee75b8136ddc7752b28e`,
verified on Windows on 2026-11-14 (2026-11-15 UTC). Studio now renders runtime
observations in Delivery, Overview and Workspace Output. The root console stays
available. See [the interaction design](../design/studio-delivery.md).

## Checks

| Check | Result |
| --- | --- |
| `dotnet build Nightweaver.slnx -warnaserror` | Passed, zero warnings/errors (20.28s cold restore/build; 6.19s final solution build). Final Web resource rebuild after presentation changes also passed with zero warnings/errors. |
| `npm test` in Studio | 38 passed, including 5 new invocation/runtime tests; final reported run 513ms. |
| `dotnet test --project tests/Nightweaver.Tests --no-build --filter-class Nightweaver.Tests.FactoryTelemetryIntegrationTests --minimum-expected-tests 1` | 15 passed, 62.273s runner duration. |
| `dotnet test --project tests/Nightweaver.Tests --no-build --filter-class Nightweaver.Tests.StudioWebTests --minimum-expected-tests 1` | 18 passed, 29.428s runner duration. |
| `factory-qa.mjs --studio`, `invocation-qa.mjs --studio` | Passed against isolated native QA AppHosts. Final invocation pass includes runtime replacement while inspection remains open and explicit output captures. |
| `telemetry.mjs --studio` | Passed once: native per-slot output, controlled disconnect, exact Last-Event-ID resume, no duplicate output, actual completion and retry cessation, follow controls, Escape/focus and 375px/effective 200% layout. |
| `studio-qa.mjs` | Passed: direct reload, route/history/query/legacy IDs, configured scope, keyboard/dialog focus; zero page errors or mutations. |
| `git diff --check` | Passed. |

The native protocol, lifecycle and output constants were unchanged. Native smoke
used the same controller retained in the final candidate. Later presentation
changes were checked by the final isolated React probe. The complete default
suite was not run; these filtered results do not establish the latest-main
five-minute performance requirement. Hosted verification remains Merger-owned.

## Behavior and visual evidence

The React probes exercise the real shell, coordinator and operational EventSource,
substituting only the factory read projection and log events for deterministic
edge cases. They verify configured/observed model separation, diagnostic disclosure
and focus across refresh, occupied versus retained idle, failed invocation, stale,
stopped and unavailable states. A new occupant and then a new runtime arrive
while output is paused: no stream is opened for the replacement, metadata remains
bound to the original invocation, and text/scroll/follow/focus survive. Failed
terminal events close the source. Separate native smoke verifies reconnect framing.

Output checks cover 2,000 lines, 256 KiB, multibyte UTF-8, terminal controls,
literal HTML, duplicate cursors and reset. Tab wraps within the modal; Escape
returns to the exact trigger or local heading after identity replacement. Source
callbacks after terminal end/close cannot publish. Tracker failure is visibly
independent of live runtime activity in these disposable fixtures; this slice
makes no claim of tracker workflow mutation verification.

Reviewed captures retain Studio's existing navy/violet palette, readable card
hierarchy and details on demand. Long paths wrap, cards do not stretch to the
height of a neighboring expanded diagnostic, and modal actions remain reachable
outside the scrolling metadata/output body. Geometry checks pass at 1440, 375
and 720 CSS pixels; native smoke additionally exercises 640 CSS pixels at device
scale factor 2 for effective desktop 200% layout.

- Floor: [desktop](studio-delivery/busy-1440-viewport.png), [375px](studio-delivery/busy-375-viewport.png), [720px](studio-delivery/busy-720-viewport.png).
- Long metadata: [375px](studio-delivery/retained-375-viewport.png), [720px](studio-delivery/retained-720-viewport.png).
- Long output: [desktop](studio-delivery/output-1440-viewport.png), [375px](studio-delivery/output-375-viewport.png), [720px](studio-delivery/output-720-viewport.png).
- Overview: [desktop](studio-delivery/overview-1440.png), [375px](studio-delivery/overview-375.png).
- [Final deterministic probe results](studio-delivery/results.json).

All exact isolated AppHosts were stopped using Aspire, including the final
capture pass. No production runtime or tracker was used for browser QA. Earlier
probe failures identified missing Tab wrapping (fixed and retested), asynchronous
React assertion timing (probe now waits for rendering), and a shell reference
capture invoked from the wrong working directory (rerun from the documented QA
directory passed). No pending product questions or downstream findings remain.

## Review repair — stalled runtime reconciliation

Reviewer P2 at `delivery-views.tsx:39` identified that disabling Refresh runtime
while `refreshing` is true prevents recovery after an accepted reconciliation
whose collector stalls. The action now remains enabled and reads Refresh runtime
again, following the shared synchronization contract. Coordinator behavior and
invocation streaming are unchanged.

`runtime-refresh-qa.mjs` passed against a fresh exact isolated AppHost on
2026-11-15 UTC. Delivery, Overview and Workspace Output each received a 202 and
at least three subsequent successful cached reads with unchanged `lastAttemptAt`.
The action remained enabled; a later explicit click issued a second POST, while
another click during that pending POST remained coalesced. Each surface recorded
exactly two POSTs and zero page errors. Geometry passed at 1440/375/720 CSS pixels.
Reviewed [Delivery at 375px](studio-delivery/refresh-repair/delivery-375.png),
[Workspace Output at 375px](studio-delivery/refresh-repair/output-375.png) and
[Overview at 720px](studio-delivery/refresh-repair/overview-720.png); the retry
label wraps/reflows without obscuring controls. [Results](studio-delivery/refresh-repair/results.json)
retain read/request counts and fixed attempt identities. All nine captures also
remain in ignored QA `TestResults/runtime-refresh-repair`.

Repair validation: solution build with `-warnaserror` passed (zero warnings/errors,
7.15s reported build time); Studio `npm test` passed all 38 tests (564ms reported);
combined `StudioWebTests` and `FactoryTelemetryIntegrationTests` passed all 33
tests with `--minimum-expected-tests 33` (46.534s reported). `git diff --check`
passed. No full-suite timing or hosted-verification claim. The first browser
probe attempt rejected the legitimate initial unavailable envelope; the probe
now allows initial collection before freezing the completed attempt. Both exact
AppHost runs were stopped in `finally`; final `aspire describe` reports no running
AppHost for this worktree. No QA/Merger repair findings or human questions remain.
