# Shared-read results and latency proposals

Shared base publication makes cold useful content available earlier on all five
Studio surfaces in the retained comparison. It does not establish faster detail
completion, lower total tracker load, or a numerical latency commitment. Warm
results are mixed. The first candidate has slower comment visibility. Complete
native attribution remains unavailable. No failed measurement is declared passed.

## Evidence and scope

Use the [baseline evidence](shared-read-baseline.md#retained-baseline-23-november-2026),
its [corrected HTTP receipts](shared-read-baseline.md#http-receipt-repair), and the
[candidate evidence](shared-read-candidate.md). The implemented contract is
[v1.3 and v1.3.1](../design/operational-observations-v1.md#amendment-v13--shared-base-and-independent-sections-ns-gjwx).
The [measurement definitions](shared-base-read.md#measurement-events) distinguish
rendered base facts, validated sections, external changes and native work.

| Data set | Runtime revision | Tree | Result |
| --- | --- | --- | --- |
| Baseline | `1511657553168b6edd94d8adf077895441639e91` | `05f8861bf942083d4a7f875595a7eef788876167` | 110 valid rendered samples; eight missing final command records |
| First candidate | `f3e3ed1c7dc53f6eed286c4cfcff899cf95d28a7` | `fdf25e6d08f71c307f21b11de73126b516ca3171` | 110 valid rendered samples; 37 missing final records |
| Direct-boundary retry | `44b2116e25908125ae07b2652c956d49a7fe4fbc` | `b626e5b884351ce89d51ec3c636c4861254db00c` | 110 valid rendered samples; bounded scenario work, incomplete full trials |
| Settlement repair | `c1b057fa61016b14d7dbb08ee723454df7587dc3` | `90a54a4c4a05f4d9b1dedfc7b648e59664e66a9b` | All 258 observed Jobs empty after stop; separate settlement evidence |

All recorded runtime sources were clean. The pair uses the same 50-Bead seed,
eight pauses with eight comments each, sixteen closed tasks and native dependency.
Seed SHA256 is `427A449DEE11CB70E2485250E3969916E46CAE4026F6113D5330F885687491B4`.
The timestamp is `2026-11-23T05:01:24Z`. Each surface/activity cell has one cold
sample and five warm samples from different hosts. Conditioning adds fifty samples
per matrix. These are not repeated warm cycles on one host. Three disposable
Agent CLI processes supply active-factory activity, not a full Delivery lifecycle.

Machine ARES, boot `2026-11-20T05:29:51.5000000Z`, SDK 10.0.300, bd 1.3.1
(`c1c4b642a`), Aspire 13.6.0, Node v25.2.0 and Playwright 1.64.1 match.
TEMP differs. Runs are serial, not alternated. Resource capture overlaps first
candidate settled Attention. Overview requires a changed probe resource selection
because its shipped consumer uses independent bases. The retry removes nested Git
instrumentation and adds an independent process observer. These differences limit
causal attribution. Do not pool the candidate revisions into one distribution.

Raw candidate data remain local under the operator's publication restriction.
The baseline archives remain linked from its report. These exact local receipts
permit review without publishing raw payloads:

| Local receipt | SHA256 |
| --- | --- |
| [Corrected baseline report](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-pa2d5e7f69ee8f9ee28be/20261123T2342548748565Z-implementer/baseline-receipt-report.json>) | `e29d12ff46200a5c2a2001f63fa195fdb06c0bdd5da7b0000acea95bd6fc6e56` |
| [First candidate result](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-pa2d5e7f69ee8f9ee28be/20261123T2342548748565Z-implementer/shared-read-candidate-v2/result.json>) | `cc89f91d403689a50a8f6726b789444cfe8f13f0265cbdbc4f57f5f6a9dd752f` |
| [Active Overview retry result](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-pa2d5e7f69ee8f9ee28be/20261124T0144597548247Z-implementer/observed-active-overview/result.json>) | `eb67e4bbb624dae63a2cd18f93db3d77a0e0926806c602036a5621c29b36e525` |
| [Eight-cell retry result](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-pa2d5e7f69ee8f9ee28be/20261124T0144597548247Z-implementer/direct-boundary-remaining/result.json>) | `98f08c155851c72e9cec551a208aaa3f55b056bb812f5d40d7b189db3cccfd04` |
| [Settled Overview retry result](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-pa2d5e7f69ee8f9ee28be/20261124T0230077264550Z-implementer/direct-settled-overview/result.json>) | `3d5d0a8b1f6b2a2caaa4ad23b60cf1f04a80411bb0ff6bb83489d03082094761` |

## Useful content and detail completion

The following values are observed milliseconds. Each pair is baseline / first
candidate. Cold values have one sample; warm values are medians of five samples.
Recalculation uses unrounded samples. Percent reductions are descriptive.

| Surface | Settled cold | Active cold | Settled / active cold reduction | Settled warm | Active warm |
| --- | ---: | ---: | ---: | ---: | ---: |
| Overview | 26953.1 / 1654.4 | 24632.0 / 2970.5 | 93.9% / 87.9% | 109.5 / 120.7 | 110.1 / 121.8 |
| Explorer All page 1 | 6888.0 / 4432.6 | 8461.4 / 2096.9 | 35.6% / 75.2% | 155.9 / 155.0 | 156.0 / 151.3 |
| Attention | 10920.7 / 1880.9 | 10702.9 / 2855.9 | 82.8% / 73.3% | 4.8 / 5.4 | 4.5 / 5.2 |
| Direct Workspace | 6978.2 / 2217.4 | 10833.7 / 3091.7 | 68.2% / 71.5% | 8.2 / 8.6 | 7.3 / 7.1 |
| Catch Up 24h | 26061.0 / 2124.2 | 26398.0 / 2737.5 | 91.8% / 89.6% | 75.8 / 74.5 | 65.3 / 77.4 |

The first candidate's settled warm Explorer range is 149.0–9054.8 ms. Its median
does not establish a tail bound. The retry's separate cold observations span
1451.0–2540.6 ms; warm medians span 5.0–153.2 ms. See its per-surface table in
[retry evidence](shared-read-candidate.md#retry-result-and-native-load-bounds).

Workspace requested-detail medians are baseline / first candidate:

| Activity | Cold ms | Warm ms |
| --- | ---: | ---: |
| Settled | 7109.9 / 10412.0 | 145.2 / 210.4 |
| Active | 10964.8 / 11789.2 | 144.4 / 211.0 |

The request set is Summary, History, relations and environment. Earlier headers
are not earlier detail completion. The first candidate section analysis has 154
observations and 99 new successful receipts; the retry has 154 and 100. Each
section retains request/receipt identity and a separate state. Merge entries with
no matching verified reports have null completion, not successful empty evidence.
Explorer and unselected Attention request no section. Candidate non-Workspace
`detailMs` measures base-key completion; it cannot measure explanation or merge
completion. Section-interest-to-completion and admission timing remain unavailable;
individual polling request times cannot establish the initial section interest.
Output panel rendering is retained, but no fixed retained byte range
was selected. Output completion is unmeasured.

The corrected baseline HTTP medians range from 191.7 to 275.9 ms cold. First
candidate cold receipts range from 211.2 to 277.1 ms. Warm Attention and Catch Up
use retained receipts with null HTTP times. HTTP can carry a pending envelope.
It is not useful content. Gate wait, admission wait, projection/publication time
and browser receive-to-render attribution remain unavailable, not zero. The
browser uses one monotonic clock and 100 ms polling; render times include browser
transport. Native inclusive durations use separate process clocks. Their sum is
not page latency. Independent Overview panel milestones remain in the raw reports.

## External changes and tracker load

These ranges cover ten changes per kind in each data set. Each cell gives
mutation-start-to-render / successful-native-settlement-to-render milliseconds.
Native Ready and Blocked reads corroborate membership separately from relations.

| Change | Baseline | First candidate | Retry |
| --- | ---: | ---: | ---: |
| Comment in History | 4479.7–8654.8 / 4148.6–8070.4 | 10060.9–23473.5 / 9686.5–22526.6 | 4287.3–8974.9 / 3825.8–8508.6 |
| Dependency relation | 12589.2–30923.5 / 11681.4–30260.5 | 13948.5–30704.3 / 13014.6–29815.4 | 6570.0–15168.4 / 5952.2–14648.4 |
| Ready after blocker closure | 1899.4–17484.8 / 1252.4–17043.0 | 4623.1–13703.1 / 4086.6–13013.4 | 3504.4–10587.0 / 2791.5–9965.8 |

All guarded notes succeeded and preserved the pause. The separate native boundary
trial records a guarded note in 3597.4 ms while selected History was pending.
With journal reads refused, a comment reached History in 47657.0 ms, including
388.3 ms native settlement. This proves backup recovery, not a general recovery
deadline. Failed inventory retained the header and stale state with no absence
proof. Recovery restored fresh data.

Baseline full trials have 3551 final records and eight missing finals; first
candidate full trials have 3235 and 37. Both retain ninety separate external CLI
operations. Final-record family observations are `bd:list` 110/173,
`bd:comments` 396/230, `bd:show` 38/39, `bd:events` 145/142 and `bd:ready` 46/40.
They are not complete command counts or valid load reductions. Missing records
leave exact bytes, exits/timeouts, duration, overlap and peak concurrency unknown.

The retry measures the application CLI boundary once. Its scenario starts at cold
navigation and ends after final native Ready verification. Preparation and later
shutdown are outside this window. Conditioning and warm waits remain inside it.

| Retry scenario | Window seconds | Starts / second | Starts / finals | Inclusive native ms, lower–upper | Bytes, lower |
| --- | ---: | ---: | ---: | ---: | ---: |
| Settled Overview | 58.241 | 2.060 | 120 / 120 | 41771.7–41771.7 | 999844 |
| Settled Explorer | 46.223 | 2.077 | 96 / 96 | 34427.6–34427.6 | 826847 |
| Settled Attention | 51.322 | 2.104 | 108 / 108 | 38626.5–38626.5 | 831757 |
| Settled Workspace | 45.256 | 2.121 | 96 / 96 | 35535.0–35535.0 | 782176 |
| Settled Catch Up | 49.773 | 2.089 | 104 / 104 | 36726.4–36726.4 | 857869 |
| Active Overview | 62.489 | 2.048 | 128 / 127 | 45940.6–47922.6 | 1043278 |
| Active Explorer | 47.506 | 2.126 | 101 / 101 | 35646.1–35646.1 | 740112 |
| Active Attention | 63.105 | 2.108 | 133 / 132 | 46644.4–48361.4 | 1040727 |
| Active Workspace | 56.432 | 2.091 | 118 / 118 | 42680.5–42680.5 | 965494 |
| Active Catch Up | 56.328 | 2.095 | 118 / 118 | 42138.7–42138.7 | 948785 |

All ten windows contain 1122 starts and 1120 finals, 400137.6–403836.6 inclusive
native ms and at least 9036889 bytes. Eight complete windows have zero recorded
errors and peak concurrency one. Active Overview and Attention have unknown
complete error counts and peak concurrency. Their missing commands have finite
settlement bounds but unknown results and byte upper bounds. Full trials retain
eleven missing finals; two lack child observations. The grouped reports remain
`completed: false`. The separate settlement repair does not repair those records.
Different instrumentation prevents a numerical native-load delta to baseline.
Journal/setup, source, Git/environment and guarded work remain separate in raw
command families; missing results prevent complete totals for each family.

## Architecture result and target proposals

Shared inventory projection removes optional history, relation, environment and
merge acquisition from base publication prerequisites. It does not remove those
commands from later detail work. Section identity confirmation adds shared
inventory reads after native detail turns. Cold direct links still wait for
shared inventory validation. Selected details still wait for bounded scheduling,
the serialized gate, native commands and identity confirmation. Those phase waits
are not independently measured. Warm cache hits can avoid fresh HTTP receipts.

| Proposed target scope | Decision from retained cold, warm and active evidence |
| --- | --- |
| Useful base content | Do not set a numerical target yet. All ten cold first-candidate cases improve, but one sample per cell cannot establish a percentile. Five warm samples include the 9054.8 ms Explorer outlier. Active fixtures do not cover a full factory lifecycle or 2000 Beads. |
| Requested details | Do not set a numerical target yet. Workspace cold and warm completion is slower in the first matrix. Merge completion is unavailable in the timed seed. Output range completion is unmeasured. Independent section receipts do not create an equivalent baseline section event. |
| External-change visibility | Do not set a numerical target yet. The first candidate's comment range worsens. The retry uses different instrumentation. One missed-journal recovery cannot establish an audit latency distribution. |
| Gate/admission wait and tracker load | Do not set a numerical target yet. Wait phases are unavailable, full traces have gaps, and the retry has no equivalent direct-boundary baseline. Native settlement bounds cannot substitute for successful results. |

These are target proposal decisions, not new SLOs or relaxed deadlines. Keep the
two-second native bound, four-second native-work turn, four history commands per
turn, mutation fairness, source-specific freshness and v1.2 journal/audit/age
policies. Keep finite memory/admission limits and the 128-task merge acquisition
cap. Cached reads never authorize mutations. This report changes no runtime rule.

## Product coverage and delivery status

The architecture slices are merged through `060d84fe14751a077402602cbdfad3a1e9fb0169`.
This report is the final interpretation slice; Review, QA and Merger checks still
apply. Implementation support does not mean complete representative measurement.

| Product requirement | Acceptance evidence or remaining failure |
| --- | --- |
| One shared base cache across five surfaces | v1.3 implementation and native cold-base trial. Overview composes Attention/Digest bases plus runtime; reserved `base:overview` stays unsupported. |
| Cold pages/direct links, no targeted fallback, validated absence | Native base-only trial records inventory validation and absence proof with no `show`, `comments` or `dep` start. `ColdDirectLinkWaitsForSharedValidationAndNeverUsesShow` covers controlled races. |
| Native Ready/Blocked semantics | Matrix dependency and blocker-closure checks use native corroboration. Deterministic source tests cover independent membership failure and bounded continuation. Representative Ready/Blocked cold/warm timings remain unmeasured. |
| Readable retained data and truthful freshness | Native failed-inventory trial and responsive browser journeys; stale/remount Digest cases prevent source-age renewal. |
| Base before expensive sections and separate loading states | Workspace, Attention and Digest browser journeys plus independent section receipts. Missing merge evidence stays unavailable. |
| Duplicate sharing and finite recent detail retention | `IndependentSectionsShareHistoryWithAttentionAndDigest` and `RecentSectionHistoryEvictsWithinEntryAndByteBudgets`; native matrix does not measure capacity at 2000 Beads. |
| Selected detail, inventory refresh and guarded-write progress | Controlled scheduling/fairness tests and pending-History guarded note. Timed full-factory contention and a second competing Workspace remain unmeasured in the representative pair. |
| Current membership/actions override historical detail | Workspace deletion/pause-removal browser checks, Attention workflow checks and source identity/generation tests. Guarded notes preserve fresh authoritative validation. |
| Pause/closure before evidence; qualified claims | Attention loading indicators and fresh Digest paired/malformed/agent-only merge controls. Cleanup diagnostics stay separate. Deterministic cap checks retain the 128-task bound. |
| Journal hints and backup audits | Native external mutations and journal-refusal recovery; `BaseAuditUpdatesSharedRevisionWithoutJournalAndRetiresItsReservation`. |
| Existing CLI, representative measurement and wait attribution | Current adapter and cold/warm/active fixture reports. Full native attribution and phase waits remain unavailable. No numerical commitment is supported. |
| Superseded page examples: Explorer, Workspace and competing Catch Up | Existing source/coordinator cases and responsive journeys cover publication and presentation. Representative page 3, Product lineage, empty selected history, structured pause, fixed Output range and competing Workspace timings remain unmeasured. |

The evidence owner must complete the missing equivalent scenarios and direct
native-boundary baseline before proposing numerical commitments. The maintainer
must assess the retained warm Explorer outlier, slower first-candidate details
and comment visibility with matched instrumentation. This report authorizes no
new workload or production mutation. Reviewer must check these limits against the
Product. Merger must run fresh synchronized-head checks and latest-main acceptance.
The retained matrix failures remain open evidence limits; this report does not
declare full Product acceptance complete.

## Verification and cost

This slice recomputes existing reports and checks their revision and receipt
identities. It adds no acceptance tool, native journey, production command, normal
test, fixture publication or scheduling change. Detailed regressions remain in
the existing cheap fixtures. Raw recomputation output and local suite evidence
remain in the dispatched invocation directory and are identified in the Bead
handoff. See [test-cost policy](../agents/test-cost-evidence.md) for the required
complete candidate measurement and the separate latest-main check after merge.

The complete candidate measurement `99d99ddf8c7845dcb440b1ad9c67530f` tested clean
revision `565b4e6f6a97a3e1f6040fcc73728b157acf4c54`, tree
`88487d83dd94b4f89b0f3607472f281127c85e73`. All 2184 tests passed at six threads,
with zero failures and skips. Outer elapsed time was 271.0822801 seconds. All 66
archive checks and 102 collector checks passed. Measurement errors were empty.
The hard limit passed; the 240-second operating target failed.

The [raw result](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-p25bbf6cce20b1b48cb05/20261124T0654390508876Z-implementer/99d99ddf8c7845dcb440b1ad9c67530f/result.json>)
has SHA256 `4148b0e90d9e97467d3324a5086efee3adf82157121590d1c67114bc3c8fcc43`.
Its directory also retains `suite.log` and `report-1.xml`. The explicit accepted
baseline is `1b14daa242197b4fc830c7f0589932f299657933`, tree
`8d0c58df5be57105a53f67701a1230026b82bce0`: 2184 passing tests in 283.9156518 seconds.
Its [accepted result](<H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-pa2d5e7f69ee8f9ee28be/20261123T2342548748565Z-implementer/accepted-suite-baseline.json>)
has SHA256 `B1CC8787F1650158BEC83C0023E2C834D8A08FD101092A7D8495EC1755562441`.
Machine, boot, tools, production identity and six-thread policy match. Different
`tempRoot`, `TEMP` and `TMP` make automatic comparison unavailable. Delta and ratio
remain null. Absolute times do not establish a controlled cost reduction. No
resource trigger occurred. Subsequent edits only record evidence and update the
specification's delivery status; production and test sources stay unchanged.

The target miss needs cost review. This documentation slice adds no normal-suite
work. Production and normal test sources also match the accepted baseline; the
intervening changes affect optional QA tools and documentation. Existing native
collections dominate execution cost: `UpgradeActivationTests` 178.695 summed
seconds, `ManualDeliveryPilotSecondSignalTests` 93.810,
`AgentStageDeliveryIntegrationTests` 85.206, `MergerCompletionFinalizerTests`
74.049 and `PoolConcurrencyIntegrationTests` 72.480. Collection times overlap and
are not outer elapsed deltas. The maintainer must review those costs with retained evidence;
this report establishes no attributable material regression and no runtime fix.
Merger must measure latest main after integration.
