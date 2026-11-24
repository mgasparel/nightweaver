# Shared-read candidate evidence

The launch-gap repair proves tool-process settlement in two replacement native
trials. It closes Reviewer finding 1 at the Windows process boundary. The old
unknown child records stay unknown. All failed reports remain unchanged.

## Launch-gap repair

The checked runtime is `c1b057fa61016b14d7dbb08ee723454df7587dc3`, tree
`90a54a4c4a05f4d9b1dedfc7b648e59664e66a9b`. The runner recorded clean source.
It repeats only settled Explorer and Attention, the two cells with unknown
children in the previous retry. Both use the retained fixture timestamp
`2026-11-23T05:01:24Z`. Both seed hashes are
`427A449DEE11CB70E2485250E3969916E46CAE4026F6113D5330F885687491B4`.

Each wrapper enters a separate nested Windows Job before it writes `.pending`.
The observer opens that exact Job before it writes `.admitted`. The wrapper
cannot launch a tool before admission. Children inherit the Job; breakaway is
not permitted. The observer keeps its handle through exact Aspire stop. A Job
with zero active processes proves settlement even if the wrapper dies before
`.launched`. A missing Job with no admission proves non-launch under this new
protocol. Legacy records have no such proof. Live or unknown containment makes
the observer and runner fail. Neither a wrapper exit nor stack stop alone proves
child settlement. This changes only optional instrumentation.

Five controlled native checks passed in 1.4761595 seconds, without a tracker or
browser. They cover termination before admission, before launch and after launch
but before record publication. They distinguish proved non-launch from a launched
child whose Job reaches zero. Negative checks require observer failure for a live
child and unknown legacy evidence. The live-child check also confirms native
settlement after exact Job cleanup. These checks use the real Windows boundary;
injected source checks cannot prove Job inheritance or kernel settlement.

| Replacement trial | Starts / final records / Job observations | Unproved Jobs | Outer seconds through exact stop |
| --- | --- | ---: | ---: |
| Settled Explorer | 129 / 129 / 129 | 0 | 94.2619984 |
| Settled Attention | 129 / 128 / 129 | 0 | 98.1016351 |

Both browser probes, stack starts, exact stops and observers returned 0. The
observer recorded zero active processes for all 258 Jobs after stack stop. The
Attention journal command without a final record has independently signalled
wrapper and child handles, plus zero active Job processes. Its missing native
result and byte counts remain unknown. The controlled after-launch case supplies
the missing-record boundary that these browser trials need not hit by chance.
Aspire then reported no running AppHost. Final read-only checks found all six
recorded fixture PIDs absent. No process was stopped by those checks.

The runner returned 1 and its report has `completed: false`. It retains the
missing final record and `Invalid native activity: settled-attention-1/qa-live-planning`.
That activity check tests a numerical PID. The browser had already required all
settled fixture PIDs to be absent before navigation; the later check found that
number present. It does not establish the later process identity. An initial
post-stop check also found that number present; the final check found it absent.
No retained creation identity identifies the later process. This repair makes no
claim about its lifetime or identity. The 22 rendered samples are valid, but
these trials supply separate settlement evidence, not replacement latency or
activity distributions. The original 110-sample comparison remains separate from this
instrumentation change. No failed result is declared passed.

Local artifact identity is `settlement-repair` in invocation
`20261124T0245571266625Z-implementer`. All 1,345 stored file hashes passed.
Raw files stay local. Aggregate identities are:

| Artifact | SHA256 |
| --- | --- |
| Trial result | `42CF94F300D5A2B21EF83A5B7467FBEA489AA9C1A990FB0F49BCA4C36A68FAAB` |
| Trial report | `981975AC217949A1618424198C1B2AA513E9D72A8F0FD607A398522173E1080F` |
| Five-check result (`settlement-check-v2`) | `26338F01FCC3C3C99B1B18556444A911339BAE61AC898C7449DB26E610B7B952` |
| Final fixture PID observation | `813278A9B094E3094E42F08BB7E2F2F6C6E935EB3B1D5461A0A22BCF9365F11E` |

Build the opt-in ToolProbe and run `bd.exe --settlement-check <new-local-directory>`
from its output. Reproduce the native trials with
`shared-read-baseline.ps1 -Surfaces explorer,attention -Activity settled`
and the fixture timestamp above. Keep output local. One additional Node case
requires a failed observer to fail report acceptance; all 59 Node checks passed.
The solution and ToolProbe builds passed with zero warnings and errors. The
repair adds no production or normal-suite work. The complete 2184-test measurement
below remains applicable; production, normal test sources and fixture publishing
match its revision. No unchanged full suite was repeated. Merger still owns
fresh synchronized-head checks and latest-main acceptance.

## Retained retry evidence

The retry supplies bounded native-load evidence for all ten scenario windows.
It has 110 valid rendered samples across all five surfaces. All browser trials
and exact stack stops passed. Two scenario commands have finite settlement bounds
but unknown final bytes and native results. The original runner reports remain
incomplete. The earlier matrix below retains its 37 missing final records.
This report does not replace a failed result or set a latency target.

## Retry result and native-load bounds

The retry measures runtime revision `44b2116e25908125ae07b2652c956d49a7fe4fbc`,
tree `b626e5b884351ce89d51ec3c636c4861254db00c`. It uses three retained runs:

| Local artifact identity | Result SHA256 | Cells |
| --- | --- | ---: |
| `observed-active-overview` | `eb67e4bbb624dae63a2cd18f93db3d77a0e0926806c602036a5621c29b36e525` | 1 |
| `direct-boundary-remaining` | `98f08c155851c72e9cec551a208aaa3f55b056bb812f5d40d7b189db3cccfd04` | 8 |
| `direct-settled-overview` | `3d5d0a8b1f6b2a2caaa4ad23b60cf1f04a80411bb0ff6bb83489d03082094761` | 1 |

The source was clean when each runner recorded its identity. Only the final
settled Overview cell was missing at this dispatch. It took 76.8006866 seconds
from trial start through stack stop. This dispatch reused the other nine cells.
Report analysis changed during the final trial; its runtime, shim and browser
probe did not change. Derived reports use the analysis in this candidate.
All 4,859 stored file hashes passed. All ten seed hashes match the baseline.
All ten browser probes, stack starts, stack stops and independent observers
returned 0. `aspire ps` then returned no running AppHost.

The operator required a reproduction before any measured-window claim. The
retained `repro-overview` run reproduced one missing final record at
`195f6bae6dada8f077606d907cb8414d427d46ec`. The next observed Overview run at
`f9bc14bcca2dbee139879e7695a3a4ea4789fa10` recorded four missing finals.
Three paired wrapper/tool handles signalled within 1.98–2.00 seconds of their
recorded starts, during browser work. The fourth pair signalled during shutdown.
Both processes reported exit `-1` in these pairs. This is consistent with the
bounded caller termination path. It is not proof of a successful tool command,
an exact cancellation instant or the missing byte count. The runner retained
the failed reports and resource capture before further diagnostic work.

That run also showed nested Git instrumentation inside native Beads. The retry
restores the original PATH in delegated native tools and starts its observer
outside the Web process tree. Thus it measures the application's CLI boundary
once. It does not change production deadlines, turns, fairness or cancellation.
The old baseline includes nested shim work. Its native totals cannot establish
a direct load delta against this retry. The earlier same-shim comparison below
remains available with its original unknown values.

The scenario window starts at cold navigation. It ends after the final native
Ready verification, following comment, guarded note, dependency and blocker
closure checks. It includes conditioning, all warm navigation and their waits.
It excludes preparation and subsequent shutdown. The end uses the browser's
stored Node clock origin and monotonic offset, rounded outward. The report keeps
this window separate from the full trial. It never changes `completed` to true
because a missing final has a bound.

| Scenario | Recorded starts | Missing finals in window | Inclusive native ms, lower–upper | Bytes, lower |
| --- | ---: | ---: | ---: | ---: |
| Settled Overview | 120 | 0 | 41771.7–41771.7 | 999844 |
| Settled Explorer | 96 | 0 | 34427.6–34427.6 | 826847 |
| Settled Attention | 108 | 0 | 38626.5–38626.5 | 831757 |
| Settled Workspace | 96 | 0 | 35535.0–35535.0 | 782176 |
| Settled Catch Up | 104 | 0 | 36726.4–36726.4 | 857869 |
| Active Overview | 128 | 1 | 45940.6–47922.6 | 1043278 |
| Active Explorer | 101 | 0 | 35646.1–35646.1 | 740112 |
| Active Attention | 133 | 1 | 46644.4–48361.4 | 1040727 |
| Active Workspace | 118 | 0 | 42680.5–42680.5 | 965494 |
| Active Catch Up | 118 | 0 | 42138.7–42138.7 | 948785 |

The ten windows have 1,122 starts and 1,120 final records. Inclusive time is
400137.6–403836.6 ms across these serial windows. Bytes are at least 9,036,889.
The two active commands lack final byte and native-result records. Their paired
handles signalled within 1,982 and 1,717 ms, respectively. These are conservative
upper bounds, not measured exact durations. Byte upper bounds, exact missing
exits, exact overlap and peak concurrency remain unknown in those two windows.
All 110 sample intervals have finite load bounds.

Full trials contain 1,152 starts, 1,141 final records and eleven missing finals.
Nine missing finals start after their scenario ends. Two of those nine have no
delegated-child observation; their full-trial settlement and duration remain
unknown. The exact Aspire stops passed, but those receipts do not reconstruct
missing child records. Complete full-trial native attribution remains unavailable.
Nine affected trials retain failures. All three grouped reports retain
`completed: false`; no report failure is suppressed. The active Catch Up cell
has no missing final.

The unchanged seed and workload support this descriptive browser comparison.
The baseline remains `1511657553168b6edd94d8adf077895441639e91`. Machine, boot and
tool versions match those below. TEMP differs and runs are serial. Native PATH
forwarding and independent observation add instrumentation differences. These
facts prevent controlled causal latency claims and a numerical native-load delta.

| Surface | Settled cold ms | Settled warm median ms | Active cold ms | Active warm median ms |
| --- | ---: | ---: | ---: | ---: |
| Overview | 2209.0 | 113.5 | 2309.0 | 7.2 |
| Explorer | 1567.2 | 149.3 | 2540.6 | 153.2 |
| Attention | 1779.1 | 5.6 | 1451.0 | 5.0 |
| Workspace | 1552.5 | 9.6 | 2123.5 | 9.3 |
| Catch Up | 1673.3 | 74.6 | 1593.3 | 73.9 |

Each cold cell has one sample. Each warm cell has five samples from separate
hosts. The independent section analysis has 154 observations and 100 new
successful receipts. It retains null completion for unavailable merge evidence.
All ten external comments reached rendered History in 4287.3–8974.9 ms.
Dependency visibility took 6569.9–15168.4 ms; Ready after blocker closure took
3504.4–10587.0 ms. All guarded notes passed. Use these descriptive values with
the baseline table below. Do not combine samples from different revisions into
a single candidate latency distribution.

The separate retained native boundary trial below still supplies cold shared
base, confirmed absence, no targeted fallback, guarded-write progress, failed
inventory retention and missed-journal audit evidence. Existing responsive
browser evidence still supplies presentation checks across all five surfaces.
No UI or production source changed in this retry.

Reproduce the three run groups with the existing matrix runner and the same
fixture timestamp. Use `-Surfaces overview -Activity active`, then the other
four surfaces in both activity modes, then `-Surfaces overview -Activity settled`.
Use fresh local output directories. `shared-read-report.mjs` adds `scenario`
beside full-trial load. Its `window`, `load` and `bounds` retain independent
identity checks and explicit unknowns. Reanalyse retained data into a new local
report path; never overwrite an original result or hash manifest.

The branch retains archive-free ancestry from
`1b14daa242197b4fc830c7f0589932f299657933`. The original archive-bearing commit
`195f6bae6dada8f077606d907cb8414d427d46ec` has a retained local reference and is
not an ancestor. Raw evidence remains local. Only reviewed aggregate evidence,
probe code and reproduction instructions belong in this candidate.

All 58 focused Node checks pass, including window boundaries, missing clocks,
unknown bytes, child-identity mismatch and outward timestamp rounding. The
normal solution build passes with zero warnings and errors. The complete
2184-test measurement below is reused: production and normal test sources are
byte-identical to that measured revision. This retry adds eight cheap Node
checks and no normal-suite native journey or fixture publication. Merger still
owns fresh synchronized-head and latest-main acceptance.

## Identities and comparison limits

The measured candidate is `f3e3ed1c7dc53f6eed286c4cfcff899cf95d28a7`, tree
`fdf25e6d08f71c307f21b11de73126b516ca3171`, with clean source. The retained
baseline is `1511657553168b6edd94d8adf077895441639e91`, tree
`05f8861bf942083d4a7f875595a7eef788876167`, with clean source. Use the corrected
HTTP receipts from [the retained baseline](shared-read-baseline.md), not its
superseded HTTP values.

Both runs used ARES, boot `2026-11-20T05:29:51.5000000Z`, .NET SDK 10.0.300,
bd 1.3.1 (`c1c4b642a`), Aspire CLI 13.6.0, Node v25.2.0 and pinned Playwright
1.64.1. Both used one cold host per surface and activity mode, one conditioning
cycle and one warm cycle per host. Thus each activity/surface group has one cold
sample and five warm samples from different hosts. The five warm samples do not
represent five consecutive cycles on one host.

The seed is byte-identical: 48,039 bytes, SHA256
`427A449DEE11CB70E2485250E3969916E46CAE4026F6113D5330F885687491B4`.
Both use timestamp `2026-11-23T05:01:24Z`, 50 Beads, eight pauses with eight
comments each, sixteen closed tasks and the same native dependency. Both runs
finished within the fixture's 24-hour window. Summary, History, relations and
environment remain the Workspace request set. Output records panel rendering
only; neither run selects a retained byte range.

The candidate probe needs an Overview adapter change. The implemented page uses
`base:attention`, `base:digest:auto` and `factory`; it does not mount
`base:overview` or the old complete Attention/Digest keys. The new selection
requires both independent tracker bases and runtime. It retains legacy selection
for baseline consumers. The disposable shim adds a disabled fault-file check and
an explicit `injectedFailure` field. Ordinary matrix trials create no fault file.
Do not treat these instrumented times as unmodified production latency.

The invocation TEMP directories differ. Runs were serial, not alternated.
Observation-only resource capture overlapped the settled Attention trial after
the first command trace gaps were found. It collected three samples with zero
errors and requested no application changes. Native command time includes shim
startup, forwarding and nested Git commands. Gate wait, admission wait,
projection time and browser receive-to-render attribution remain unavailable.
These limits prevent a controlled causal latency or complete native-load claim.

## Useful content

These are observed median milliseconds, not targets. Every raw sample, range,
source envelope and rendered fact remains in the evidence. The first cell in
each pair is baseline; the second is candidate.

| Surface | Settled cold | Settled warm | Active cold | Active warm |
| --- | ---: | ---: | ---: | ---: |
| Overview | 26953.1 / 1654.4 | 109.5 / 120.7 | 24632.0 / 2970.5 | 110.1 / 121.8 |
| Explorer All page 1 | 6888.0 / 4432.6 | 155.9 / 155.0 | 8461.4 / 2096.9 | 156.0 / 151.3 |
| Attention | 10920.7 / 1880.9 | 4.8 / 5.4 | 10702.9 / 2855.9 | 4.5 / 5.2 |
| Direct Workspace | 6978.2 / 2217.4 | 8.2 / 8.6 | 10833.7 / 3091.7 | 7.3 / 7.1 |
| Catch Up 24h | 26061.0 / 2124.2 | 75.8 / 74.5 | 26398.0 / 2737.5 | 65.3 / 77.4 |

Workspace requested-detail medians were 10412.0/210.4 ms for candidate settled
cold/warm and 11789.2/211.0 ms for candidate active cold/warm. The corresponding
baseline values were 7109.9/145.2 and 10964.8/144.4 ms. Earlier base display does
not imply faster detail completion. The raw evidence also records History and
Output panel boundaries separately.

The added section report retains 154 section observations. It identifies 99 new
successful section receipts. Merge aggregates retain each entry's own state and
source; available inventory does not certify merge evidence. In the timed
fixture, sampled merge entries remain loading or unavailable because the seed
has no matching verified merge reports. A missing successful receipt has null
completion time and a reason. Explorer and unselected Attention request no
detail section. On the candidate, the old non-Workspace `detailMs` field measures
completion of the selected base keys. It must not be used as explanation or merge
completion. Use `candidate-sections.json` for independent section observations.

## Native changes and load

All ten external comments reached rendered History in 10060.9–23473.5 ms from
mutation start. The baseline range was 4479.7–8654.8 ms. This observed increase
must remain visible in any later comparison. Candidate dependency visibility
took 13948.5–30704.3 ms. Blocker-closure Ready visibility took 4623.1–13703.1 ms.
The evidence retains native settlement and settled-to-render intervals separately.
Native Blocked reads corroborate dependency membership. Native Ready reads
corroborate the rendered result after blocker closure. All ten guarded Save Note
writes returned 200, recorded the comment and preserved the pause.

The candidate retains 3,235 final tool records and 37 starts without final
records. The baseline retains 3,551 final records and eight starts without final
records. Each matrix also retains ninety separate external CLI operations.
Unknown overlap, bytes, exits, duration and peak concurrency remain null in
affected totals. Settled-only values are observations, not complete totals.

The command-family repair skips values for global options. Thus `--actor
local-console` and `--dolt-auto-commit on` do not create false command families.
The separate `candidate-load-analysis.json` applies that repair without changing
the original report or rerunning the matrix. Relevant final-record observations:

| Family | Baseline final records | Candidate final records |
| --- | ---: | ---: |
| `bd:list` | 110 | 173 |
| `bd:comments` | 396 | 230 |
| `bd:show` | 38 | 39 |
| `bd:events` | 145 | 142 |
| `bd:ready` | 46 | 40 |

These are not complete command totals or valid load deltas. Candidate detail
identity confirmation adds shared inventory reads after native detail turns.
Independent base publication defers unselected history work. The traces and
source changes support those work differences, but missing final records prevent
full cost attribution. No command deadline, turn budget or fairness rule changed.

## Separate native boundary trial

Candidate `1014c8799acacfba189d4592a772eca17c940c26`, tree
`1c68c364b774a82e22aa87697234218404a013ee`, passed both explicit boundary
assertion groups. The browser probe and exact stack stop returned 0. The runner
still returned 1 for its retained tool trace gaps. Its 165.973-second outer time
includes preparation and the diagnostic browser sequence. Do not add its later
browser samples to the cold/warm comparison: the base-only check already warmed
the host.

The cold base check requested only `base:workspace:qa-read-000` and
`base:workspace:qa-read-missing`. A successful complete shared inventory supplied
the existing row and matching absence proof. Neither final records nor recorded
starts contain `show`, `comments` or `dep` in that interval. Missing final records
cannot conceal a targeted start from this negative assertion.

The competing history request began unavailable and pending. Guarded Save Note
returned 200 in 3597.4 ms and preserved workflow state. The selected history then
reached available. The native trace records its successful comments command.
This proves progress of a guarded write with selected detail pending; it does
not claim simultaneous native execution through the serialized gate.

The shim then refused observer `events` commands. The external comment reached
rendered History in 47657.0 ms, including 388.3 ms for native mutation settlement.
Four failed journal reads supply no useful hint. The accepted History source
still becomes fresh. A subsequent refused inventory read retains the existing
row, a null absence proof and the visible header. The envelope becomes stale
with `source-failed`. Clearing the fault allows fresh inventory recovery.
`native-failed-inventory.png` records the actual failed-source UI.

This trial adds the real missed-journal and failed-native-source boundaries that
the baseline matrix lacked. Injected source tests cannot prove that the CLI,
collector, HTTP transport and browser preserve these facts together. It reuses
the existing AppHost, tracker seed, browser, Agent fixtures and shim. It adds no
journey to the normal suite. Both failed and corrected boundary trials remain
retained. The first failed only at a Summary-only DOM assertion while History
was selected; the corrected check uses the common header.

## Retained artifacts and reproduction

Four existing browser journeys cover all five surfaces at 1440, 720 and 375 CSS
pixels. `studio-qa` passed in 112.497 seconds, `workspace-base-qa` in 55.262
seconds and `studio-explorer-qa` in 46.948 seconds. They check navigation and
keyboard focus, delayed section indicators, retained drafts, current membership,
deletion, bounded inventories and visible stale states. Captures show readable
base facts and separate section status. Held or substituted responses establish
presentation behavior only. Native pause removal, deletion, title, dependency
and Ready checks remain separately identifiable.

The combined browser run took 274.746 seconds and exited 1. Its final
`digest-merge-qa` check captured four closed tasks before enrichment, then five
after refresh. That probe requires a fresh stack, but the combined run had a warm
shared cache while its last fixture closure arrived. No count assertion changed.
The unchanged probe passed alone on a fresh exact stack in 44.423 seconds; its
runner exited 0 and stopped the stack. The retained failure remains in
`presentation`; the successful run is in `presentation-digest-fresh`.

Fresh Digest evidence checks valid paired merge reports, malformed matching
reports, agent-only notes, duplicate closure, separate cleanup diagnostics,
comments-only refresh and compatible legacy reads. Its controlled stale/remount
and HTTP-200-retention cases prove that old merge evidence cannot renew itself
through membership health. Its initial native base becomes useful in 1553.3 ms;
the later evidence phase completes in 6073.7 ms. These are diagnostic fixture
observations, not another cold/warm comparison. This reuses the existing native
journey; it adds no normal-suite journey or fixture publication.

Existing deterministic `OperationalObservationSourceTests` retain duplicate
history sharing, selected progress, cancellation, identity races, entry/byte
eviction, source audits, native-work bounds and admission refusal. In particular,
`IndependentSectionsShareHistoryWithAttentionAndDigest`,
`RecentSectionHistoryEvictsWithinEntryAndByteBudgets`,
`ColdDirectLinkWaitsForSharedValidationAndNeverUsesShow` and
`BaseAuditUpdatesSharedRevisionWithoutJournalAndRetiresItsReservation` cover
their detailed state decisions without new native journeys.

The local invocation evidence identity is `20261123T2342548748565Z-implementer`.
The Bead records its storage location. Raw evidence stays local.
The original `shared-read-candidate` directory retains the obsolete Overview
probe failure. `shared-read-candidate-v2` retains the complete ten-trial matrix.
`shared-read-boundaries` retains the first boundary failure.
`shared-read-boundaries-v2` retains the corrected boundary assertions.
Original results, reports, hashes, screenshots and logs remain unchanged.

Run the ordinary matrix with the retained fixture timestamp as described in the
[QA workflow](../../tests/Nightweaver.QA.AppHost/README.md). Run boundary checks
separately with `NIGHTWEAVER_PROBE_BOUNDARIES=1`, `-Surfaces workspace` and
`-Activity active`. The source-substituted browser checks are presentation
evidence only. Native matrix and native fault-control evidence remain separate.

Run the cheap checks with:

```powershell
node --test tests/Nightweaver.QA.AppHost/shared-read-assertions.test.mjs tests/Nightweaver.QA.AppHost/shared-read-report.test.mjs tests/Nightweaver.QA.AppHost/shared-read-receipts.test.mjs tests/Nightweaver.QA.AppHost/shared-read-keys.test.mjs tests/Nightweaver.QA.AppHost/shared-read-section-report.test.mjs
```

All 47 checks pass. They add eleven cheap cases outside normal .NET discovery.
The ordinary solution build passed with zero warnings and errors. No production
code, normal-suite native journey, fixture publication, source deadline or
six-thread scheduling rule changes in this slice.

## Complete local suite and cost

Candidate `c02e4f13a99d2373c46834ea225264476a0c71b4`, tree
`407aacdcc54b422a02cb9f48cce4a3ded7734bc1`, passed all 2184 tests. Source was
clean. There were no failures or skips. The complete outer time was 295.4855693
seconds at the committed six threads. All 66 archive checks and 102 collector
checks passed. Their times were 6.0224022 and 6.3489733 seconds. The hard limit
passed. The 240-second operating target failed and requires cost review.

The explicit accepted baseline is `1b14daa242197b4fc830c7f0589932f299657933`,
tree `8d0c58df5be57105a53f67701a1230026b82bce0`. It passed all 2184 tests in
283.9156518 seconds. The exact accepted result SHA256 is
`B1CC8787F1650158BEC83C0023E2C834D8A08FD101092A7D8495EC1755562441`.
Automatic comparison is unavailable because `tempRoot`, `TEMP` and `TMP` differ.
Delta and ratio stay null. Machine, boot, tools, production identity and the
six-thread policy match. These absolute times do not establish a controlled cost
delta. No comparable 1.5x resource trigger occurred. No suite rerun was made.

This slice adds no normal-suite test, fixture publication or production command.
The eleven added Node checks run outside normal .NET discovery. Existing native
collections account for the largest measured costs: `UpgradeActivationTests`
took 207.656 summed seconds, `ManualDeliveryPilotSecondSignalTests` 100.487,
`AgentStageDeliveryIntegrationTests` 91.201, `MergerCompletionFinalizerTests`
82.767 and `PoolConcurrencyIntegrationTests` 79.356. Collections overlap. These
values are not outer elapsed deltas. The baseline already missed the operating
target. Reviewer must assess the retained cost evidence. Merger must measure
latest main after integration. This slice does not claim a runtime repair.

The complete candidate measurement is `9fd175cf9cf14e0aa1d20e736be67647`.
Its original result, log and XML stay local. The Bead records their storage
location. Later changes affect only opt-in QA tools and this aggregate report.

## Acceptance status

Browser facts, independent section receipts, native membership, guarded-write
progress, cold base validation and backup-audit recovery have retained evidence.
Complete full-trial native-load attribution remains unavailable. The retry above
supplies bounded scenario load with explicit byte and result gaps. The shim writes final
records in `finally`; a missing final record does not prove an exit, timeout or
duration. The retained files cannot recover those values. The matrix remains
incomplete. This report does not declare every acceptance criterion passed.

Raw QA and suite archives stay local under the operator's publication restriction.
The outgoing candidate contains aggregate results and reproduction instructions.
The original commit and all raw files remain available for local review.
