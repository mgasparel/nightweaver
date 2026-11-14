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
