# Shared-read candidate evidence

The candidate has 110 valid rendered samples across all five surfaces. All ten
browser trials passed. Each exact isolated stack stopped with exit 0. Complete
native-load attribution remains unavailable: 37 command starts have no final
record. The matrix runner exited 1 and its report has `completed: false`. This
report does not replace that failure with a passing result or set a latency target.

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
Complete native-load attribution remains unavailable. The shim writes final
records in `finally`; a missing final record does not prove an exit, timeout or
duration. The retained files cannot recover those values. The matrix remains
incomplete. This report does not declare every acceptance criterion passed.

Raw QA and suite archives stay local under the operator's publication restriction.
The outgoing candidate contains aggregate results and reproduction instructions.
The original commit and all raw files remain available for local review.
