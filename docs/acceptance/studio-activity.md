# Activity console acceptance

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
0.4739 milliseconds in total. These cases use controlled records and no native
process. Production adds a journal comparison per batch or row and a bounded
older-page capacity check. It adds no tracker command or source read.
The implementation Bead records the final candidate measurement and baseline.

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
