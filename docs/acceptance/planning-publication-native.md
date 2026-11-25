# Native Planning publication proof

The isolated proof covers native ID allocation, response-loss recovery, duplicate
provenance refusal and legacy partial publication. It changes no production code.
It uses disposable Git repositories, copied Beads databases and host roots.
It does not change `ns-5gg`, an installed host or a live publication.

## Reproduce and retain evidence

Run from the repository root. Set `NIGHTWEAVER_PUBLICATION_EVIDENCE` to an absolute
JSON file in the current invocation's temporary directory to retain commands,
native output, inventories, receipts, graph membership and command cost.

```powershell
dotnet test --project tests/Nightweaver.Tests --filter-class Nightweaver.Tests.PlanningPublicationNativeTests --minimum-expected-tests 2 --report-xunit-xml --report-xunit-xml-filename publication-native.xml
```

The fixture acquires the physical repository mutex before it calls the supported
publisher seam. It seeds native candidate, Challenger review and Orchestrator
approval comments with current Git and Product grounding. It constructs a fresh
publisher for each invocation. It checks the current Product version each time.
The wrapper forwards real Git and Beads commands. After one successful create
settles, it discards the response and stops the first publisher. It retains the
same database. Recovery must reuse that task and create only the missing entry.
Publisher create commands must omit `--id`. Native inventory confirms each
provenance tuple, `discovered-from` link and blocking edge. The release receipt
and Product graph must contain the same actual IDs. The first task becomes
native-ready; its dependent stays blocked. Delivery admission requires the
complete parent barrier, even when native readiness alone permits a held task.

A separate Product receives two tasks with the same provenance. The duplicate
has no native Product link. Recovery must stop before any write. Both tasks and
their holds remain. No release receipt is written. The diagnostic names the
Product, revision, entry, observed IDs and maintainer action.

The legacy Product starts with an explicitly seeded deterministic-ID task and a
missing dependent. The publisher creates only the dependent. The wrapper stops
after the native release receipt, before hold removal. Recovery must keep the
legacy ID, retained membership and exact receipt row. It releases the same graph
without replacement tasks or receipt migration.

## Writer boundary and recovery limit

The supported limit is one live Orchestrator per physical Git common directory,
across Project names, host roots and path aliases. The process test calls
`DispatchScenarioSupport.PublishedDispatchExcludesCompetingProjectsAcrossHostRootsAndRecoversAfterProcessDeath`.
It holds a published native dispatch, rejects other Projects and a second host,
checks a junction alias, and compares tracker and runtime snapshots. It kills
the owner process and proves that a later dispatch can acquire ownership and
recover. This existing scenario had no test entry point before this change.
The test shares the existing published CLI, Agent and tracker templates.

`PlanningPublicationTests.OverlappingPublisherInstancesCannotLaunchAnotherCreate`
covers in-process overlap at confirmation, launch and commit barriers.
`StaleSessionCannotUseAReusedAssignee` covers stale observation authority.
Controlled late-child and uncertain-intent cases retain their existing coverage.
This native proof does not kill an Orchestrator while its create child runs.

The guarantees have separate sources:

| Source | Guarantee |
| --- | --- |
| Process mutex | Excludes other supported Orchestrators for the physical repository. |
| In-process publisher guard | Rejects overlapping calls for the same repository and Product. |
| Confirmed retained intent | Records creation before launch and prevents replay after uncertainty. |
| Native command settlement | Establishes the durable effect before the response-loss test stops. |
| Fresh complete inventory | Resolves actual IDs and detects missing or duplicate provenance. |
| Exact graph checks and guarded parent publication | Keep partial graphs out of Delivery and bind release to the approved graph. |

Metadata search does not provide atomic uniqueness. External `bd` edits remain
outside Orchestrator serialization. A retained unresolved intent with zero
matches stops another create. It can mean a child still runs or evidence is
missing. The maintainer must inspect child settlement, tracker history and the
retained intent. The maintainer must reconcile the binding before guarded Retry.
Retry alone does not authorize another create. The publisher does not delete
tasks or choose a match when evidence conflicts.

## Cost and evidence

The first focused publication success recorded 69.058 seconds of XML test time.
The process test recorded 26.235 seconds. The publication fixture recorded 330
publisher/grounding commands, four publisher creates and 24 publisher writes.
Setup and explicit readbacks added 32 Beads commands and three Git commands.
Two later native-ready readbacks and four partial-release admission readbacks
add six Beads commands. No new fixture publish is required.

The complete candidate is `3a0dfcb2d297047bc094e407f4ddcdec7b9e321e`, tree
`c1cee4214cba6616d9d6cf0493fdda72f8a35995`. Clean source passed all 2292 tests,
with zero failures and skips, in **194.0329532 seconds**. Archive checks passed
66 cases. Collector checks passed 102 cases. Normal build, fixture publishing,
execution and teardown used six threads. Correctness passed. The below-240-second
operating target passed. The below-300-second hard limit passed. No measurement
error or resource capture trigger occurred.

All candidate artifacts are under
`H:/Nightweaver/prod/tmp/nightshift/ns-nll-pbd28ec1512a40fd2b7bd/20261125T0215342362320Z-implementer`:

| Artifact | Identity |
| --- | --- |
| Complete result | `a26782a620d24cac8c73e97ce0e6de3a/result.json`; SHA256 `E47E4A1B4B186A4EBA747C707224F80B97D1E0EAF8AAC007E01EFD661CB377E1` |
| Raw complete output and XML | `a26782a620d24cac8c73e97ce0e6de3a/suite.log` and `report-1.xml` |
| Native command/readback proof | `publication-native-proof-complete.json`; SHA256 `FDD3DC579A4630C5CB4959690D25ECA5F3805236C567B97DF8123F14D22A8A6D` |
| Accepted baseline copy | `accepted-main-baseline.json`; SHA256 `E288394060885CEA76625CF5C06A71C9FD681AAEC6AD6388DE73365D8194B477` |

The complete-run native XML costs are 122.035 seconds for publication and 27.599
seconds for process exclusion. Their serial collection sum is 149.634 seconds;
it overlaps other collections and is not an outer-suite delta. The publication
trace contains 162 Beads commands (90.459976 summed seconds) and 168 Git commands
(14.8479033 summed seconds). Independent grounding Git reads overlap. Setup and
explicit native readbacks add 38 Beads commands and three Git commands. The
fixture records four publisher creates and 24 publisher writes. The process
scenario starts six published host processes and one junction command. It uses
the existing immutable published fixtures. Production adds no work.

The complete-run inventory records these actual task maps:

| Product | `first` | `second` | Result |
| --- | --- | --- | --- |
| `fixture-4lf` | `fixture-ri8` | `fixture-c1o` | Response-loss recovery reuses `first`; exact graph and receipt pass. |
| `fixture-brm` | `fixture-brm-pe0944ce9d74954f713c8` | `fixture-6bo` | Legacy ID, membership and receipt row remain unchanged on recovery. |

Both maps retain the exact revision
`5b57ae23aae246ea606190a8cb80ba522d99d911185da4f245a789751a4350ec`.
Each second task blocks on its first task. Native readiness includes each first
task and excludes each second task. Four native readbacks after individual hold
removal confirm denied Delivery admission before parent publication. Final
admission passes for both complete graphs.

Product `fixture-eh2` retains conflicting tasks `fixture-q3z` and `fixture-poj`
for entry `first` at the same revision. Recovery makes zero writes. Both tasks
remain held, and no release receipt exists. The retained diagnostic states:

```text
Planning publication stopped: Product 'fixture-eh2', revision '5b57ae23aae246ea606190a8cb80ba522d99d911185da4f245a789751a4350ec', entry 'first', observed IDs [fixture-q3z, fixture-poj]: The task binding is not unique and exact. Maintainer: inspect the evidence and reconcile the task binding before guarded Retry.
```

The comparison baseline is latest main
`a38dacef9b76c1f26e62e3009245ee83cb245ae7`: 2290 passing tests in 198.7498432
seconds, plus 66 archive and 102 collector checks. It includes build, fixture
publishing, execution and teardown at six threads. The original result is
`H:/Nightweaver/prod/tmp/nightshift/ns-nll-p53e5998126df65c256a2/20261125T0156433539441Z-merger/latest-main-evidence/1d239b8a5f4c45569dacddad8992c08e/result.json`,
SHA256 `91B22AE5C0ADE4495494F09E21DAE6ED3418A77DD0A468099C2CAB3717F6BD86`.
Temporary roots, retained build output and the measured collection order differ.
An automatic comparison is unavailable. Raw times do not establish a zero delta
or a speed change. The two added native tests explain the added work. Merger must
measure the synchronized head and latest main after integration.

## Hosted cleanup repair

Hosted run `45536102602`, attempt 1, job `138942957995` failed at
`StandaloneHostReadsEnvironmentAndCommandLineOverrides(useCommandLine: True)`.
Fixture deletion found `host/run/standalone/web-payload.lock` still open after
the managed exit wait. The diagnostic reported `Owners: []`; it did not identify
the lock owner. The checked head was `d54266ae7f861a8bb9f7ab6c4a9eb9de6c033cf8`.

An isolated process probe reproduced the missing settlement boundary in four
attempts. Each child held 20,000 handles to one disposable file. After
`Kill(entireProcessTree: true)` and `WaitForExitAsync`, the native wait returned
`258` and exclusive file access failed. After the native process signal, the
wait returned `0` and exclusive access passed. The probe proves the race exists;
it does not identify the historical hosted lock owner. Its script and
`native-rundown-async-fresh-result.json` remain under
`H:/Nightweaver/prod/tmp/nightshift/ns-nll-pbd28ec1512a40fd2b7bd/20261125T0306429933730Z-implementer`.

The existing standalone Web host tests now use
`SystemProcessRunner.WaitForSettlementAsync` before fixture deletion. Both
environment/command-line cases and the configured-target case require exclusive
payload access after exit. Live-lock checks and strict fixture deletion remain.
The repair adds three payload opens with existing directory safety checks and
a native settlement observation per existing host exit. It adds no native
journey, fixture publication or production work. All deadlines and the
six-thread cap remain unchanged. The publication proof above remains intact.
