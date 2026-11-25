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
add six Beads commands. No new fixture publish is required. The complete candidate measurement and retained artifact identities
are recorded in the implementation evidence comment for
`ns-nll-pbd28ec1512a40fd2b7bd`.

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
