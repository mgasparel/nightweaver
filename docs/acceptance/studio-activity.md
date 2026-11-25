# Activity console acceptance

## Pending history pause repair

The remaining Review finding passes. A pending history response cannot cancel a
later pause, row focus or page suspension. Follow starts at request entry. The
response clears evidence only if follow remains active. Cursor recovery defers
row removal until that same check. Navigation cancellation and finite reads stay
in place. The repair starts at `0a4a897fab6393a16e2f3aab3d954f8cdc3164f2`.

The extended browser probe failed before the fix. Its first held initial read
removed `Browsing paused` after Pause. The failed run remains at
`H:/Nightweaver/prod/tmp/nightshift/ns-ejr-p95942f1ecde1d100ce8d/20261125T2340189864146Z-implementer/pause-before`.
The exact stack stopped. The repaired run passed at the same invocation's
`pause-after` directory. The direct Web URL was `http://localhost:23769`.
The exact stack stopped successfully in `finally`.

Command: `pwsh -NoProfile -File tests/Nightweaver.QA.AppHost/studio-cutover.ps1 -Group Activity -OutputDirectory <invocation-TEMP>/pause-after`.
The Node process took 14.5070825 seconds. Probe assertions took 14.0327064 seconds.
Both exclude AppHost start and stop. No page errors or lifecycle mutations occurred.
The [result](activity/pause-repair/result.json) records five held-response cases:
initial load with Pause or suspension, and Return to latest with Pause,
suspension or row focus. Return cases retain 80 rows and one open evidence
section. Every case retains scroll position, with zero delta. Later arrival and
wake keep browsing paused. Only a new explicit return resumes follow and catch-up.
Initial cases retain the empty display until that return.

The existing real executor-to-journal-to-API-to-SSE-to-browser start and completion
checks also passed. Completion occurred after subscription. Exact invocation
output passed. [Native history](activity/pause-repair/native-history.json) and
the result bind journal `9bf13fe8-22c1-461f-81e3-13b66b16b3b1`, runtime
`df72fb55-9b19-4575-bce8-64a1903a4146` and invocation
`20261125T2343263141906Z-implementer`. This native boundary reuses existing
fixture processes. The five new cases use controlled history responses in that
same opt-in journey. They add no normal-suite native journey or publication.

I inspected the [1440](activity/pause-repair/activity-expanded-1440.png),
[720](activity/pause-repair/activity-expanded-720.png) and
[375](activity/pause-repair/activity-expanded-375.png) CSS-pixel captures.
Controls remain visible. Evidence wraps without page or feed overflow.
The prior replacement and older-capacity assertions still pass.
[Source hashes](activity/pause-repair/sources.json) match all five tested files.

Studio passed 104 tests, with zero failures or skips, in 1.9323453 seconds.
`npm.cmd run build` passed. `dotnet build Nightweaver.slnx -warnaserror` passed
with zero warnings or errors. Measurement policy passed 460 cases. Publisher
contracts passed 16 cases in PowerShell 7 and 16 in Windows PowerShell.
The repair diff passed `git diff --check`.

Complete measurement `ad06b86c396f4f9cad5389bfe4a7a01c` tested clean revision
`fd1680517625dbd384020b612705e2f52b5b3ddb`, tree
`a556586dced9f2b0c7876ce5873586235f5c07d7`. Base revision was
`cdc378599d8edceac118f98713e34f860e070632`. All 2,447 tests passed. Failed 0.
Skipped 0. XML errors 0. Outer elapsed time was 245.1771603 seconds. Build,
fixture publication, execution and teardown used six threads. Archive fixtures
passed 66 cases. Collector fixtures passed 102 cases. Measurement errors are empty.
Artifacts remain under the current invocation's
`complete-candidate/ad06b86c396f4f9cad5389bfe4a7a01c` directory as `result.json`,
`suite.log` and `report-1.xml`. Result SHA256 is
`103EE951FA2DFD2D9BEC23D0576005937A40E0FF9F23B79E10EC5CACFD60D701`.

Cost review: Correctness and the below-300-second hard limit passed. The
below-240-second target was missed by 5.1771603 seconds. The unchanged native
publication case took 172.997 seconds. The unchanged second-signal case took
66.253 seconds. Cases overlap across six threads; their times are not additive.
Archive and collector checks took 5.2183668 and 6.1326555 seconds. The .NET
runner reported 227.396 seconds. This repair changes no .NET test or production
runtime path used by these cases. It adds one current-pause check and deferred
client state changes per history response. It adds no request, timer, tracker
command or normal-suite work. No attributable material regression is established.
Review must assess this retained cost evidence. Merger must measure the required
synchronized head and latest main. If the target miss persists, retain the native
publication and setup timings for cost diagnosis. Do not rerun this unchanged
candidate or alter coverage, deadlines or parallelism to obtain a lower result.

The explicit accepted baseline remains
`H:/Nightweaver/prod/tmp/nightshift/ns-ejr-p0576c5b4432a78c1fd65/20261125T2225078340478Z-merger/accepted-main-baseline.json`.
SHA256 is `9293ECEF95CE9CEDE382FDF40EA75A53CB52D08DD3C46CB4CEADB87DE0E494AA`.
Evidence `ab8d48a5d1c3491683e279e94dd54db4` tested the exact base and passed
the same 2,447 cases in 213.3012778 seconds. Machine, boot, tools, production
identity and six-thread scheduling match. Preparation and temporary paths differ.
Timing comparison is unavailable. Delta and ratio remain unknown. No resource
capture trigger occurred. Only documentation changes follow this measurement.
The five production and fixture hashes still match the tested candidate.

## Review repair

Both blocking review findings pass the repaired model and browser checks.
The repair starts at `52f7312c97f951e659767bf676bff02ef44520ae`.
[Source hashes](activity/repair/sources.json) bind the tested files to this repair.
The original evidence below remains unchanged.

Finding 1: Displayed journal identity is separate from received scan progress.
Empty replacement history and SSE scans no longer discard later matching rows.
Paused rows and open evidence remain until Return to latest. Journal replacement
clears the older-page continuation. The [browser result](activity/repair/result.json)
checks both transports in following and paused states with different journal IDs.

Finding 2: Older pages add only rows that fit. All displayed rows and open
evidence remain. The model checks 450 displayed rows plus 100 older records.
It retains offsets 51 through 550 and returns continuation 50. The browser checks
the same capacity boundary, open evidence on row 550 and a visible row near the
newest row. Its anchor delta is -0.28125 CSS pixels. The client-limit notice now
appears below the viewport so it cannot shift the visible row at capacity.

Command: `pwsh -NoProfile -File tests/Nightweaver.QA.AppHost/studio-cutover.ps1 -Group Activity -OutputDirectory <invocation-TEMP>/activity-repair-3`.
The exact direct URL was `http://localhost:24681`. The Node process took
12.5242391 seconds. Probe assertions took 12.0938225 seconds. Both exclude
AppHost start and stop. The exact AppHost stopped successfully in `finally`.
The browser reported no page errors or lifecycle mutations. Real invocation
start, completion after subscription and exact output passed again through the
journal, API, SSE and browser. The journey reuses the existing native fixture.
It adds no normal-suite native journey or fixture publication.

The repair evidence root is
`H:/Nightweaver/prod/tmp/nightshift/ns-ejr-p95942f1ecde1d100ce8d/20261125T2323326112135Z-implementer`.
Run 1 found the client-limit notice position defect. Run 2 found a probe defect:
the example-row helper did not accept an empty journal's zero cursor. The fixture
now accepts zero and handles foreign-journal cursors like the API. Both failed
runs remain retained. Each exact stack stopped. Run 3 passed all assertions.

I inspected [desktop](activity/repair/activity-expanded-1440.png),
[tablet](activity/repair/activity-expanded-720.png) and
[narrow](activity/repair/activity-expanded-375.png) captures. Controls remain
visible and evidence wraps without horizontal overflow. The
[capacity capture](activity/repair/repair-older-1440.png) retains the narrow
375-pixel viewport from the preceding checks, despite its filename.

Studio passed 104 cases with zero failures or skips in 1.6701712 seconds.
Five added cases cover four empty replacement combinations and older overflow.
The overflow case took 2.0217 milliseconds. The four replacement cases took
0.4749 milliseconds in total. These cases use controlled records and no native
process. Production adds a journal comparison per batch or row and a bounded
older-page capacity check. It adds no tracker command or source read.
The implementation Bead records the final candidate measurement and baseline.

The complete measurement tested clean revision
`143a6c40616437ab1f369a8e0386abd21c1bed3c`, tree
`37df20b759da857ef8ee89dddc8a6e775d97d454`. All 2,447 tests passed with zero
failures or skips in 231.7461791 outer seconds. Six threads, normal build,
fixture publication and teardown remain. Archive and collector fixtures passed
66 and 102 cases. Measurement errors are empty. Both local time limits passed.
Evidence `5b1bbc9290104acd99f4ad6400ab5254` is retained under this invocation's
`complete-candidate` directory. The result SHA256 is
`D0F2663BF7DFED8DBB4BCC3A49113E5EB4E22D319D677E9C8E989B4AD1F617A6`.

The explicit accepted baseline is evidence
`ab8d48a5d1c3491683e279e94dd54db4` at base
`cdc378599d8edceac118f98713e34f860e070632`. It passed the same 2,447 tests in
213.3012778 seconds. Its accepted file SHA256 is
`9293ECEF95CE9CEDE382FDF40EA75A53CB52D08DD3C46CB4CEADB87DE0E494AA`.
Preparation and temporary paths differ. Timing comparison is unavailable.
Delta and ratio remain unknown. The repair adds five controlled Studio cases,
bounded client checks and browser assertions in the existing opt-in journey.
It adds no normal-suite native journey, fixture publication or source read.
No attributable material cost regression was established. No resource capture
trigger occurred. After measurement, only this report and the source-hash file's
line endings changed. Production, test and fixture source hashes still match.

## Initial implementation evidence

The Activity journey passed. The isolated direct Web endpoint was
`http://localhost:25707`. The exact AppHost stopped successfully in `finally`.
The browser reported no page errors or lifecycle mutations. The existing shared
observation wake reconciliation remains separate from lifecycle mutations.

Command: `pwsh -NoProfile -File tests/Nightweaver.QA.AppHost/studio-cutover.ps1 -Group Activity -OutputDirectory <invocation-TEMP>/activity-browser-6`.
The runner measured 10.6000086 seconds for the Node process, including browser
startup and exit. The probe measured 10.2200685 seconds through its assertions.
These times exclude AppHost startup and stop. This opt-in journey adds no native
journey or fixture publication to the normal complete suite.

The invocation evidence root is
`H:/Nightweaver/prod/tmp/nightshift/ns-ejr-p95942f1ecde1d100ce8d/20261125T2253424020092Z-implementer`.
The successful run is `activity-browser-6`. Runs 1–5 retain earlier failed local
checks. Run 1 found equivalent runtime GUID formats. Run 2 found early anchor
consumption. Run 3 found the level filter's missing accessible name. Runs 4 and 5
found probe synchronization errors after filter navigation and route disposal.
Each exact stack stopped. No failed evidence was overwritten or removed.

The retained source base is `cdc378599d8edceac118f98713e34f860e070632`.
The browser used the working candidate before commit. [Source hashes](activity/sources.json)
bind the tested production and fixture files to the committed candidate. The
implementation Bead records the final clean candidate revision, complete-suite
measurement and retained accepted baseline. Browser time does not establish
complete-suite performance or latest-main acceptance.

## Native evidence

The opt-in fixture uses the production Agent executor hooks and Activity journal.
[History](activity/native-history.json) retains actual invocation start records.
The browser opened exact invocation output and observed completion after its
subscription, through the real journal, shared API reader and SSE transport.
The [result](activity/result.json) binds journal
`7f192824-2e49-496e-a85f-5c5bfaf8bc3e`, runtime
`adbbb867-67ac-487b-b49d-8eb9c527a7e3`, invocation
`20261125T2310592972978Z-implementer`, and cursor
`1.7f1928242e49496ea85f5c5bfaf8bc3e.1`.
[Native browser capture](activity/native-1440.png) shows the actual observations.
Completion text explicitly leaves the lifecycle outcome unconfirmed.

## Presentation evidence

Separate substituted responses cover Planning, Delivery, QA, Merger, pause,
recovery, wait and health records. Verified merge facts expose a bound PR link.
Unsupported evidence exposes no verified PR link. Workspace navigation preserves
the exact Bead and source History section. Literal script text remains text.

The journey loads older records with open evidence. Its visible anchor moves
0.375 CSS pixels. A 900-record burst leaves the focused, expanded row and all
200 displayed rows unchanged, with a `500+` pending count. Return to latest
catches up and holds 500 rows. It closes evidence. Separate checks cover exact
Bead/level filters, direct reload, empty matches, unavailable journal storage,
independent runtime health, history gaps, replacement, reconnect, page suspension
and unused stream disposal. Catch Up and exact invocation output remain usable.

The inspected captures have clear controls, readable evidence, visible focus,
wrapping identifiers and no horizontal page or feed overflow:

- [1440 CSS pixels](activity/activity-expanded-1440.png)
- [720 CSS pixels](activity/activity-expanded-720.png)
- [375 CSS pixels](activity/activity-expanded-375.png)

The existing narrow navigation scrolls horizontally. Activity controls and
evidence stay within the page width. The record viewport scrolls independently.
The record list has no per-row live announcement region.

## Cost and boundaries

Eleven deterministic Studio cases add controlled burst, replay, bound, filter,
link and time coverage. Studio now passes 99 cases. The 6,000-record case took
1.950 seconds in the final frontend run; all Studio cases took 2.094 seconds.
The production route owns bounded client memory and one Activity stream. The
Overview entry adds no Activity read. Existing API and native journal tests keep
their coverage. No scheduling, tracker command, lifecycle write, deadline or
six-thread setting changed. See [the design](../design/studio-activity.md).
