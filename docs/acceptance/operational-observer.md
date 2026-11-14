# Shared operational observer acceptance

Measured locally on Windows with bd 1.3.1, 2026-11-14. The backend implements
[v1.1](../design/operational-observations-v1.md); the SSE endpoint and browser
coordinator remain ns-mbd.3 work. No frontend or production deployment changed.

## Live disposable stack

The exact QA AppHost was started with `aspire start --isolated`, awaited with
`aspire wait web`, and its direct HTTP URL and disposable configuration were
discovered through `aspire describe`. Playwright loaded the actual Studio shell
and fetched the versioned resources with a one-second probe interval. Mutations
used public native bd commands against the validated disposable tracker; the
checkpoint POST used the browser's real same-origin policy. No response fixtures
were substituted. The exact AppHost stopped in `finally`; `aspire ps` returned
an empty list afterward.

Raw evidence: [operational-observer-results.json](operational-observer-results.json).
Times below start after the external CLI command succeeds and end when the
browser receives and validates the requested observations. Dependency time also
requires native Ready/Blocked membership to agree. Cold time requires inventory,
selected Workspace and the complete Attention cohort. These are measured samples,
not upper bounds or cross-command transactions.

| Inventory / paused | Cold complete | Comments | Dependency + membership | Readiness | Checkpoint |
| --- | ---: | ---: | ---: | ---: | ---: |
| 50 / 8 | 8.185s | 4.139s | 5.162s | 3.092s | 6.202s |
| 2,000 / 40 | 60.735s | 6.792s | 8.334s | 8.254s | 8.400s |

**Five-second target: not achieved consistently**, even at 50 Beads. The 2,000-row
sample missed it for every measured tracker change. The scheduler bounds work
and rotates histories rather than claiming fresh data: the large cohort and
tracker resources reported explicit stale/budget health while runtime remained
fresh. The comment additions changed inventory rows on this bd version; fake
source tests additionally change comment content with inventory held identical.

Runtime heartbeat visibility took 2.032s, with 1.443s between the source snapshot
clock and its HTTP observation. Heartbeats preserved the operational revision.
Releasing a native invocation fixture became visible as completed in 2.025s:
**the strict two-second end-to-end target was missed by 25ms**. The probe's
one-second polling adds sampling uncertainty. This does not establish SSE or
browser-render latency, and it does not relax the target.

The raw JSON's command list records external fixture commands, not collector
fanout. The native source smoke separately reports collector command count and
native elapsed time; fake source tests prove that 100 cache readers do not issue
additional source reads. Inventory/detail/history/byte limits, mutation fairness,
in-flight cancellation ownership and partial failures are tested independently
of this representative live fixture.

## Regressions found during verification

The initial large-fixture probe retained its old eight-item Attention projection
and failed a 90-second completion bound. Lowering probe polling from 100ms to
one second still reproduced it. A deterministic source test also showed that a
single transient history timeout discarded completed cohort work. Another
failed when waiting ten fake seconds for mutation access consumed the native
work allowance and prevented Workspace collection.

The repair retains successful bounded cohort reads across transient failures,
releases them when Attention interest ends, accounts native execution separately
from gate waits/projection CPU work, and alternates detail priority without
advancing it on turns exhausted by core work. Strict selected comments are shared
in either direction, with no more than four actual comment commands per sweep.
The original live probe subsequently completed at both inventory sizes. Failed
probe logs and JSON remain in the dispatched Bead's invocation evidence.

Cache tests also exercise TTL and LRU eviction/readmission with old in-flight
results, host-wide revision exhaustion, atomic subscription/version capture,
finite shared history/projection reservations, stale aging during blocked reads,
and shutdown. Cache reads skip payload serialization when age/health is unchanged.

Reproduce using the [QA AppHost instructions](../../tests/Nightweaver.QA.AppHost/README.md).
The native probe does not cover 10,000-row worst cases, 64-history saturation,
multiple competing Web hosts, long active worktree status reads, or mutation
storms. Those remain limits of the measurements, not freshness guarantees.

## Local validation

- `dotnet build Nightweaver.slnx -warnaserror`: succeeded with zero warnings/errors.
- `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995
  --report-xunit-xml --report-xunit-xml-filename resume-publish-full.xml
  --show-slowest-tests 20`: all 1,094 tests passed, none skipped, including 37
  observer cases. Measured wall time including build, fixture publishing and
  teardown was **236.194 seconds**, with committed six-thread scheduling.
- The required Workspace, Workspace boundary, Factory telemetry and process
  runner selection passed all 24 cases separately with a positive test minimum.
- The final full-suite native observer smoke recorded 42 collector commands
  (bd and Git validation/environment commands), 22.467s cumulative native time,
  15.338s cold Workspace, 3.011s comments visibility and 2.849s dependency
  visibility. This loaded-suite measurement is separate from the live fixture.
- `git diff --cached --check`: clean. Exact QA AppHost teardown confirmed.

These timings validate the candidate checkout. The latest-main five-minute gate
must still be measured after integration; no hosted verification was dispatched.
