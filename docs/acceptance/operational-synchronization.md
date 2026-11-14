# Operational synchronization acceptance

Verified locally on 2026-11-14 for ns-mbd.3. Implementation and downstream API
usage are documented in [operational synchronization](../design/operational-synchronization.md).

`dotnet build Nightweaver.slnx -warnaserror` passed with zero warnings/errors,
including successive incremental builds replacing Vite's hashed assets.
`npm test --prefix src/Nightweaver.Web/Studio` passed all 17 deterministic
coordinator tests. `dotnet test --project tests/Nightweaver.Tests --filter-class
Nightweaver.Tests.OperationalEventStreamTests --minimum-expected-tests 9` passed
all nine backend framing/admission/queue/bootstrap/cancellation cases.

The complete retained .NET suite command from `tests/README.md`, with minimum 995,
xUnit timing output and normal six-thread parallelism, passed 1,163 tests with no
failures or skips in **245.140 seconds** including build, fixture publishing and
teardown. Artifact: `TestResults/timing.xml`; full local output:
`H:/Nightweaver/prod/tmp/nightshift/ns-mbd.3/final-suite.log`.
This is candidate evidence; latest main still requires measurement after merge.
The final frontend refinements were subsequently checked with the warning-clean
solution build, all 17 coordinator tests, and the browser matrix below.

The fresh isolated exact QA AppHost passed `operations-qa.mjs`, `studio-qa.mjs`
and `smoke.mjs`. Aspire readiness and direct endpoint/config discovery preceded
Playwright; the same AppHost stopped successfully in `finally`.

The [recorded result](operations/result.json) shows four initial paused Beads
converging to five in both the navigation and overview after a native external
fixture mutation, with invalidation delivery deliberately suppressed. Route
mounting shared one real EventSource. Forced replacement produced two hellos
across two successive streams, with maximum concurrent streams **one**. Thirteen
real invalidations were observed. The only browser POST was the explicit manual
refresh, accepted with 202. No command was replayed, no browser page error was
captured, and a foreign Project route opened no operational stream.

Visual review at [1440px](operations/live-1440.png), [375px](operations/live-375.png)
and [720px](operations/live-720.png) confirmed readable connection/source status,
reachable Refresh, matching Attention counts, retained-data labeling, and no page
overflow. Studio regression checks also passed direct reload, Back/Forward,
keyboard/focus, URL query state, legacy dotted Bead IDs, unknown Project/route,
unavailable dialogs and no browser mutations. The root console smoke found its
real seeded Bead.

The fixture's shared observer sometimes reports stale source health within its
documented collection limits. The stream remains connected while the count and
metric explicitly say stale; heartbeats never relabel those sources fresh. This
verifies synchronization and truthful freshness, not a stronger native collection
latency guarantee or completion of later Studio feature slices.

## Review repair: stalled resource reads

The Reviewer found that an unresolved resource GET could block every later read
while healthy SSE heartbeats kept the connection alive. The new fake-clock
regression failed before repair (the request never aborted). Each resource read
now expires after ten seconds, clears its identity before aborting, exposes stale
retained data and a timeout error, and schedules one replacement. Late completion
cannot publish; lifecycle cleanup cancels deadlines.

`npm test --prefix src/Nightweaver.Web/Studio` now passes **19 tests**, including
deadline recovery through periodic polling, invalidation, Refresh and wake, plus
late completion and settlement/unsubscribe/suspend/disposal cleanup. The solution
build with `-warnaserror` passes with zero warnings/errors. The complete retained
suite command above passed **1,163 tests**, zero failures/skips, in **245.447
seconds**, including build, fixture publishing and teardown with normal committed
parallelism. Candidate log: `H:/Nightweaver/prod/tmp/nightshift/ns-mbd.3/repair-suite.log`;
latest-main timing still requires measurement after integration.

On a fresh exact isolated AppHost, `read-timeout-qa.mjs` held complete real HTTP
responses while the real operational stream stayed connected. It verified the
abort and [stale/error view at 375px](operations/timeout-375.png), reachable Refresh
with one 202 POST, focus recovery, a replacement response and rejection of the
old response after [recovery](operations/recovered-375.png). Both captures were
visually reviewed for readable status, retained counts and no page overflow.
No commands were replayed and no page errors occurred.

`operations-qa.mjs`, `studio-qa.mjs` and `smoke.mjs` also passed on that same host:
4-to-5 missed-notification count convergence, maximum one concurrent stream,
two hellos, explicit 202 Refresh, no command replay, no page errors, route/history
and 1440/375/720px regressions. The exact AppHost stopped successfully in `finally`.
Fresh browser outputs are retained beneath
`H:/Nightweaver/prod/tmp/nightshift/ns-mbd.3/20261114T2248493334094Z-implementer/`.
