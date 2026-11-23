# Shared read baseline probe

`tests/Nightweaver.QA.AppHost/shared-read-baseline.ps1` runs the same disposable
read workload on a retained baseline and a later candidate. It starts a fresh
isolated Web host for each cold surface, activity mode and repetition. It stops
that exact AppHost in `finally`, including failed measurements. It does not
change production scheduling or set a latency target.

Install the pinned browser tools as described in the
[QA workflow](../../tests/Nightweaver.QA.AppHost/README.md). Run from the repository
root with PowerShell 7:

```powershell
pwsh -NoProfile -File tests/Nightweaver.QA.AppHost/shared-read-baseline.ps1 `
  -OutputDirectory <new-absolute-evidence-directory>
```

Use the same probe source, native tools, fixture hash, environment, requested
sections and parameters on both revisions. Retain separate directories. The
default matrix has one cold host per surface and one warm cycle after a complete
conditioning cycle, for settled and active native Agent fixtures. Increase
`-ColdRepetitions 3 -WarmCycles 5` for the proposed larger repetition matrix.
`-Surfaces workspace -Activity active` is a diagnostic subset, not full coverage.
There is no automatic baseline acceptance or automatic latency comparison.

The fixture contains 50 fixed Bead IDs, eight paused Beads with eight comments
each, sixteen closed tasks, and a native dependency. The selected Workspace has
an absent environment. It does not overlap the telemetry fixture's unregistered
directories. Three actual Agent CLI fixture processes run through the existing
invocation preparer and executor. Their native PIDs, invocation identities,
readiness files and before/after process existence checks establish activity.
Settled trials release and observe those processes before navigation. Active
trials retain them through the measured reads and guarded Save Note. The fixture
does not run a full autonomous Delivery lifecycle.

The frozen seed uses `2026-11-23T00:00:00Z`. The 24-hour window assertions must
remain true on both tested revisions. A later date requires a new named fixture
and baseline pair, with an explicit timestamp/hash change. Do not silently reuse
an expired window. Overview uses its real automatic window; Catch Up explicitly
uses 24 hours. Explorer uses All page 1. The requested Workspace sections are
Summary, History, relations and environment. Output has no fixed requested range
in this first manifest and is explicitly unmeasured.

## Evidence boundaries

`result.json` records the source revision/tree, dirty state, machine/boot, tools,
TEMP, preparation, parameters, trial exits and stack stop exits. Each trial retains
seed bytes, browser traces, source envelopes, rendered text, screenshots, external
tracker writes, guarded receipts and native process observations. `hashes.json`
identifies retained artifacts. It excludes itself and executable files; the shim
DLL and runtime metadata remain hashed. Preserve failed trials before a changed
diagnostic run.

The browser controller uses one Node monotonic clock. Navigation and same-tab
route actions start each interval. Polling samples actual rendered fixture facts
at 100 ms intervals. The measured render time is an upper bound that includes
Playwright transport. It is separate from the first HTTP receipt, which may carry
an empty pending envelope. A warm retained read without a new HTTP response has
`httpMs: null` and `retainedReceipt: true`; it is not a zero-duration receipt.
The legacy full-response contract exposes no earlier independent section event.
The report records the full-response boundary and the later visible History
boundary. It does not claim that hidden content was visibly rendered.

The disposable PATH shim delegates to the exact installed `bd` and `git` paths.
It forwards bytes without substituting responses. It records command arguments,
native child identity, stdout/stderr bytes, nonzero exits, startup and inclusive
launch-to-exit/drain time. Journal setup and journal reads pass through the same
shim. Pending records survive a killed shim; missing final records are explicit
settlement gaps. The shim adds process startup and forwarding cost. Use identical
instrumentation for a pair; these are instrumented observations, not unmodified
production latency.

`report.json` separates cold and warm useful content, detail completion, HTTP
receipt, external-change visibility and native load. It includes every sample,
median/range/count, failures and missing measurements. Native command families,
bytes, errors, cumulative inclusive time and peak concurrency are reported per
sample and trial. Native UTC intervals are correlated at millisecond precision;
the probe does not subtract clocks from different processes. Gate wait, admission
wait and projection time are null with reasons. They are not zero. Independent
Overview panel render milestones use the same controller clock and polling
interval. Browser receive-to-render phase attribution remains unavailable.

External comment, dependency and blocker-closure trials retain successful native
settlement and separate browser visibility. Native Ready/Blocked reads corroborate
membership. A real same-origin guarded Save Note must return a successful receipt,
retain the pause and appear in native comments. These writes affect only the
validated disposable tracker. Fixture preparation stays outside the measured
read intervals. Mutation work remains separately attributed.

## Missing measurements

The first manifest does not cover 2,000 Beads, cold/warm Ready and Blocked views,
Explorer page 3, Product lineage, empty selected history, structured pause reports,
matching/malformed verified merge controls, the 129-task evidence cap, a second
competing Workspace, refresh contention, a fixed Output range or backup-audit recovery. The report also states
repetition limits and unavailable timing attribution. Existing deterministic and
native acceptance tests retain their contracts. This evidence does not replace
those tests or claim coverage for an unmeasured case.

Run the cheap failure assertions separately:

```powershell
node --test tests/Nightweaver.QA.AppHost/shared-read-assertions.test.mjs
```

The long probe and tool shim remain outside normal test discovery. No native
journey, production command, deadline or test scheduling change enters the normal
suite. The QA AppHost gains only an opt-in seed and instrumentation configuration.
The ordinary complete suite still uses six threads and the retained
[measurement entry point](../agents/test-cost-evidence.md).
