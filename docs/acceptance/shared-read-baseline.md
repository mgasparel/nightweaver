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

The runner freezes one UTC timestamp for the whole matrix. Its default is one
hour before preparation. Pass `-FixtureTimestamp <timestamp-from-baseline>` on
the candidate to retain identical seed bytes. The 24-hour window assertions must
remain true on both tested revisions. After that window expires, capture a new
named baseline/candidate pair with a new timestamp and hash. This requires no
source edit. Do not silently reuse an expired window. Overview uses its real automatic window; Catch Up explicitly
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
Playwright transport. It is separate from the first HTTP receipt for a validated
surface resource key. Both request start and receipt must fall within the sample
interval. An unrelated resource or an earlier in-flight request cannot supply
this time. The probe uses exact Playwright request identity to record the request
timestamp before it reads the response body. A receipt may carry an empty pending
envelope. A warm retained read without a new relevant HTTP response has
`httpMs: null` and `retainedReceipt: true`; it is not a zero-duration receipt.
This flag also requires all validated resource receipts to precede the sample.
An unmatched receipt has null time and an explicit unavailable reason.
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
node --test tests/Nightweaver.QA.AppHost/shared-read-assertions.test.mjs tests/Nightweaver.QA.AppHost/shared-read-report.test.mjs tests/Nightweaver.QA.AppHost/shared-read-receipts.test.mjs
```

The long probe and tool shim remain outside normal test discovery. No native
journey, production command, deadline or test scheduling change enters the normal
suite. The QA AppHost gains only an opt-in seed and instrumentation configuration.
The ordinary complete suite still uses six threads and the retained
[measurement entry point](../agents/test-cost-evidence.md).

The report rejects missing trials, missing cold or warm samples, invalid source
evidence, browser errors, missing writes, lost native activity and unsettled
commands. The runner returns a failure when the report is incomplete. Explicit
unmeasured attribution remains separate from a failed measurement.

## Retained baseline, 23 November 2026

All ten browser trials passed. Each exact stack stopped with exit 0. The matrix
retained 110 valid samples: ten cold, fifty conditioning and fifty warm. Each
surface has one cold sample and five warm samples per activity mode. Those five
warm samples come from five different cold-host trials, each with one warm cycle;
they do not establish five consecutive warm cycles on one host.

The measured source is `1511657553168b6edd94d8adf077895441639e91`, tree
`05f8861bf942083d4a7f875595a7eef788876167`, with clean source. The run used
.NET SDK 10.0.300, bd 1.3.1 (`c1c4b642a`), Aspire CLI 13.6.0, Node v25.2.0 and
Playwright 1.64.1 on ARES. The fixture timestamp is `2026-11-23T05:01:24Z`.
Preparation plus the serial matrix ran from `2026-11-23T06:01:24.7391696Z` to
`2026-11-23T06:16:52.7904833Z`, about 928 seconds. This opt-in native measurement
covers browser rendering, actual CLI reads, external writes and live Agent CLI
process identity. Injected tests cannot establish these boundaries. It reuses
the existing isolated AppHost, invocation executor and one tracker per trial.
It adds no native journey to normal test discovery.

Median useful-content times, in milliseconds:

| Surface | Settled cold | Settled warm | Active cold | Active warm |
| --- | ---: | ---: | ---: | ---: |
| Overview | 26953.1 | 109.5 | 24632.0 | 110.1 |
| Explorer All page 1 | 6888.0 | 155.9 | 8461.4 | 156.0 |
| Attention | 10920.7 | 4.8 | 10702.9 | 4.5 |
| Direct Workspace | 6978.2 | 8.2 | 10833.7 | 7.3 |
| Catch Up 24h | 26061.0 | 75.8 | 26398.0 | 65.3 |

Workspace detail medians were 7109.9/145.2 ms for settled cold/warm and
10964.8/144.4 ms for active cold/warm. The raw samples retain HTTP receipt,
Overview panel milestones, complete rendered text and source envelopes separately.
All ten external comments became visible in 4479.7–8654.8 ms from mutation start.
Dependency relation visibility took 12589.2–30923.5 ms. Blocker-closure Ready
visibility took 1899.4–17484.8 ms. The raw changes also retain native settlement
and settled-to-render intervals. All ten guarded Save Note writes returned 200,
recorded the comment and kept workflow state unchanged. Each active trial retained
three native Agent CLI processes before and after the measured work.

The runner exited 1 because eight command starts have no final trace record.
They occur in five trials. The report correctly refuses complete attribution.
These gaps do not invalidate the separately validated rendered samples. Five
trials retain complete tool traces; the other five retain exact recorded starts
and explicit unknown overlap, bytes, exits, duration and concurrency. The analysis
report uses null for affected totals and retains settled-only observations under
`settledOnly`. It never presents those observations as complete load. Across the
matrix, 3551 commands have final records. Journal, source and Git families remain
separate in the raw traces. Queue, gate and projection timing remain unmeasured.
The documented larger workloads and contention controls remain unmeasured too.
No latency target or baseline/candidate improvement is claimed.

The original directory is
`H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-p89ed2a6fe7ade8917f1e/20261123T0557550535836Z-implementer/shared-read-baseline`.
Its `result.json`, `report.json` and `hashes.json` remain unchanged. The later
`analysis-report.json` applies the explicit unknown-load handling without a new
native run. The checked-in evidence archive retains raw results, seed bytes,
tool start/final records, screenshots, lifecycle logs and both reports. It omits
the generated shim binaries. Use the exact measured probe revision when repeating
the native workload. The later change only corrects report attribution.

### HTTP receipt repair

Use `receipt-analysis.zip` for HTTP comparisons. The original `report.json` and
`analysis-report.json` can assign an unrelated resource response to a surface.
Their HTTP values and retained-read flags are superseded. Raw files and the two
original archives remain unchanged. Useful-content times, detail times, external
changes and tool-load gaps remain unchanged.

The corrected report matches resource keys from each sample's validated source
proof. It matches request start and receipt to that sample's interval. The old
trace has no request identity. A historical match therefore requires exactly one
unanswered request for the same key. Ambiguous order makes later matches for that
key unavailable. The analysis does not assume response order. An unknown time
stays null and has a reason. It does not enter the HTTP statistics.

The repair changes 59 HTTP values across all 110 samples. The corrected report
retains 76 matched receipts and 34 retained reads. No sample needs an unavailable
HTTP reason in this archive. All 110 rendered samples remain valid. The eight
unsettled tool commands still prevent complete matrix attribution.

Corrected median HTTP times, in milliseconds:

| Surface | Settled cold | Settled warm | Active cold | Active warm |
| --- | ---: | ---: | ---: | ---: |
| Overview | 244.5 | 10.8 | 211.9 | 11.6 |
| Explorer All page 1 | 191.7 | 67.5 | 275.9 | 67.3 |
| Attention | 193.4 | retained | 212.8 | retained |
| Direct Workspace | 222.3 | 15.9 | 221.4 | 15.0 |
| Catch Up 24h | 205.2 | retained | 232.1 | retained |

Each cold cell has one receipt. Each numeric warm cell has five receipts.
Each retained warm cell has five null times. These HTTP times can describe empty
envelopes. They do not establish useful content or a latency target.

To reproduce the analysis, extract the unchanged `evidence.zip` into a new
directory. Use a separate output path to preserve its reports:

```powershell
node tests/Nightweaver.QA.AppHost/shared-read-report.mjs <extracted-directory> <new-analysis-report.json>
```

Exit 1 remains expected because the raw tool traces have settlement gaps. The
checked-in `receipt-analysis.zip` contains `receipt-analysis-report.json`. Its
SHA256 is `8EFC261F085561FBADA7AC25E8AA23471871E19D9D8B1B7556001CA4F4AB7776`.
The source archive SHA256 remains
`4FB78E1D047208ED0A2B2AD489486105AAE36A5F0D9F2ABA2C71C5973D6380DB`.
The repair adds ten cheap Node checks, including unrelated early responses,
retained warm reads, interval boundaries, ambiguous historical matches, and
report aggregation. All 36 checks pass. Probe syntax and `git diff --check` pass.
No product code, normal-suite test, native workload or scheduling policy changes.
The Reviewer permits reuse of the complete candidate measurement below for this
repair. No unchanged native matrix or complete suite was repeated.

## Local test cost

Candidate `8c84c7582fe9b0821caea4b6b68fb4ab150d186d`, tree
`22fe66a0e1d0802c4cd03379292dedf5ffb051a2`, passed all 2133 tests with no skips
or failures. Source was clean. The complete outer measurement was 259.6581192
seconds at six threads. The 66 archive cases and 102 collector cases passed too.
The hard limit passed. The run exceeded the 240-second operating target.

The explicit accepted baseline is `cd60f820058e7ea4b0ba109f972fe961de52ac56`,
tree `9e21b176a46f35fdb6c8af2ba56149ccf488b339`: 2133 passing tests in
257.8436215 seconds. Its accepted result has SHA256
`5B59746AE8F87A4F82B9D6E896998669C953A095D87397F5F555D3E0E1068A3E`.
The candidate measurement used that exact file. Automatic comparison is
unavailable because `tempRoot`, `TEMP` and `TMP` differ. Delta and ratio remain
null. Machine, boot, tools, six-thread policy and production identity match.
The two absolute elapsed times are diagnostic, not a controlled cost delta.

This change adds no normal-suite test, fixture publish or production command.
The new AppHost seed and tool shim run only for the opt-in probe. Existing native
collections dominate cost: UpgradeActivationTests took 208.120 summed seconds,
ManualDeliveryPilotSecondSignalTests 70.789, AgentStageDeliveryIntegrationTests
65.176 and PoolConcurrencyIntegrationTests 56.692. These overlapping collection
times are not outer elapsed deltas. Archive and collector script checks took
4.608 and 6.582 seconds. This slice does not claim to repair the existing target
miss. Any future cost repair must preserve the existing native boundaries.

The 26 cheap Node evidence checks pass separately. `dotnet build Nightweaver.slnx`
passed with zero warnings and errors. Later changes only retain documentation
and evidence. Merger still owns exact-head checks and fresh latest-main acceptance
after integration.

The candidate raw directory is
`H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-p89ed2a6fe7ade8917f1e/20261123T0557550535836Z-implementer/f1bbb491b99c4c278291ab84721c8adf`.
The retained suite archive contains the candidate result, log and XML, plus the
accepted baseline and its original result, log and XML. The baseline probe
archive hash is `4FB78E1D047208ED0A2B2AD489486105AAE36A5F0D9F2ABA2C71C5973D6380DB`.
All 7326 archived file hashes were verified after packaging.
