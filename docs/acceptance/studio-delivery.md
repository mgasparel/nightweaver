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

## Native verification repair — human recovery

The subsequent Merger run found readiness-file sharing failures, a Planning
completion timeout and excessive complete-suite runtime. Human recovery assigned
these repairs to this Bead; Merger evidence framing is separately owned by
`ns-3i4`. The Studio implementation, invocation protocol and reviewed browser
captures above are unchanged by this repair.

Readiness producers already close a temporary JSON file before atomic rename.
`WaitForJsonAsync` now retries only Windows sharing/lock violations (32/33), in
addition to incomplete JSON, within its original deadline. Other IO failures
propagate. Three cheap regressions hold exclusive/read-sharing writers open and
verify recovery after closure or deadline expiry. Both original native
stream-cleanup cases remain.

Native process timings identified repeated tracker startup/read costs in the
Planning publication path. Product and complete comments now share one fresh
`show --include-comments` read, while complete graph verification shares one fresh
inventory with embedded dependency edges. No observation is cached across a
mutation. Fault-injection regressions retain exact authority, content, edge and
unmapped-child checks. See [Planning evidence](../agents/planning-evidence.md).

Lifecycle readiness similarly obtains native blocker/deferment decisions once
with `list --ready --status open,in_progress --limit 0`. Inventory validation,
exact ownership selection, publication gates and the single stale-pair retry
remain. The real Beads fixture compares memberships with both former commands,
including blocked, deferred, foreign, assigned, closed, gate and over-50-row
controls. The call-count regression failed against the original reader with two
processes instead of one. Existing claim-race and interstage-mutation fixtures
now intercept the combined query; all cases remain enabled.

Retained evidence is under
`H:/Nightweaver/prod/tmp/nightshift/ns-mbd.5/20261115T0500549381301Z-implementer`:

| Complete run | Result | Wall time including build/publish/teardown |
| --- | --- | --- |
| `full-1.log` | 1,165/1,207 passed; 42 old query-fixture assertions subsequently repaired | 323.880s |
| `full-final.log` | 1,205/1,207 passed; Merger and native observer deadlines | 502.077s |
| `full-isolated.log` | 1,207/1,207 passed, zero skipped | 300.875s; exceeds the strict budget |
| `full-weighted.log` | 1,207/1,207 passed, zero skipped | 325.512s; overlaps a separate native Planning test |
| `full-verified.log` | 1,204/1,207 passed; Planning, Merger and native observer deadlines | 427.492s; another complete suite started during this run |

The 502-second run overlapped a second complete suite from `ns-3i4`, starting
four seconds later; process identities and command lines are retained in
`concurrent-full-suites.json`. The isolated run began after that process exited;
its sampled process inventory contained only this worktree's test executable.
Planning and Merger passed in 252.738s and 71.098s respectively. This does not
establish that every historical timeout had the same cause as contention.

The isolated measurement found an 80.333-second operational-observation
collection still weighted at 31.001 seconds. Collection weights now use that
complete 1,207-test measurement. All native deadlines and the six-thread cap
remain unchanged. Candidate timing must be distinguished from latest-main
timing after integration and Merger-owned hosted verification.

The weighted run's sampled process inventory also records a separate filtered
`PoolConcurrencyIntegrationTests.PlanningPublishesWhileDeliveryAndMergerProgressIndependently`
run in `ns-mbd.9` (`weighted-during-*.json`). Its 325.512-second result exceeds the
budget; passing correctness alone does not establish timing acceptance.

The last attempt began with no other test executable present, but `ns-mbd.9`
started a complete suite during execution (`final-before.json` and
`final-during-*.json`). Planning, Merger and the native observer timed out. These
failures remain unresolved evidence, rather than being discarded in favor of
passing runs. No further broad reruns were used to seek a passing sample.

**Historical validation blocker (resolved by the manual verification below):** final-candidate normal-suite acceptance and the strict
five-minute gate are not established. The best isolated measurement before the
last weight refresh was still 300.875s. The Orchestrator needs to coordinate a
host-exclusive complete-suite measurement for this candidate; if deadlines or
timing still fail there, further diagnosis is required. This repair makes no
claim that all historical failures share one cause or that Studio caused them.
No native deadlines, retained coverage or parallelism were reduced.


## Manual integration verification — 2026-11-15

Nightweaver and its Web Console were gracefully stopped before manual takeover,
leaving the host exclusive for verification. The retained repair was rebased onto
main 985a68af08f6e066ce536caf5392565035d57a02. Explorer routes/styles and its
fresh publication-authority guards remain alongside Delivery and the batched
graph/readiness reads. Both independent Standards and Spec reviews of candidate
96fdb317687c9a104d7d305c623e276062e52858 found no blocking static findings.

- Solution build with -warnaserror: zero warnings/errors.
- Focused Planning publication and stream readiness checks: 72 passed.
- Studio frontend tests: 40 passed.
- Complete normal suite: 1,223 passed, zero failed/skipped, **273.3928583 seconds**
  wall time including build, fixture publishing and teardown. Six-thread
  parallelism, all deadlines and retained coverage are unchanged.
- Exact isolated Aspire/Playwright: factory-qa.mjs --studio,
  invocation-qa.mjs --studio, telemetry.mjs --studio, and
  runtime-refresh-qa.mjs passed. Native cursor resume, duplicate-free bounded
  output, terminal cessation, retained identity, keyboard/follow controls and
  stalled-refresh retry were exercised. Fresh studio-qa.mjs passed direct
  reload/history/query/legacy-ID and dialog-focus checks with no errors/mutations.
  Captures at 1440/375/720 CSS pixels include long metadata/output; narrow modal
  controls and overview retry remained readable and reachable on visual review.

The first shell probe ran immediately after all other probes on the same stack
and recorded two HTTP 429 console errors. The observer admits at most two
interested Workspaces and retains interest for 30 seconds; the prior Output
probe left another Workspace interested. The documented fresh-stack shell run
passed unchanged. Both exact AppHosts stopped successfully; final describe
reported no running AppHost. No observer limit or test assertion was weakened.

Evidence: H:/Nightweaver/prod/tmp/nightshift/ns-mbd.5/manual-20261115,
including candidate-suite.log, candidate-time.json and browser captures/results;
the complete report is TestResults/manual-candidate.xml in the retained
worktree. These results resolve the candidate validation blocker without
attributing every historical timeout to contention. Exact-head hosted
verification and a complete latest-main measurement after integration remain
separate final gates.


### Hosted follow-up repair

The first final hosted run, 43717429479, failed two of 1,223 cases in 349.534s.
The generated Delivery drain exhausted its unchanged 15-second claim-finalization
budget; retained native timings showed an 11.898-second inventory read. Drain
now obtains a fresh exact-Bead preflight, validates the guarded update's complete
returned row, and performs one exact-Bead reconciliation only if the response
is uncertain. It never replays the release or accepts changed workflow/ownership.
Seven fast cases fail against the original implementation and pass the repair;
the complete Delivery/DispatchClaim selection passes 57 cases.

The separate two-native-slot telemetry observation case passed locally after
the hosted failure; its cause is not established. Its readiness waits now run
concurrently under the existing ten-second limits, and any future failure
records the persisted reader result, publisher diagnostics and both executor
outcomes. The five-second publication wait and 25-second invocation clocks
remain unchanged. No failing observation is represented as proven repaired by
a passing rerun. Full and hosted verification of this follow-up remain required.
