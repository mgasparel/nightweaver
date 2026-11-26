# Beads events as console cache invalidation hints (ns-dkq)

## Recommendation

Proceed with a bounded implementation of event-triggered **authoritative rereads**, with demand gating and a slower reconciliation timer. Do not replace Beads reads with event payloads or promise that a quiet journal proves freshness. The experiment establishes usable local notifications and replay, not a production performance improvement. A standalone second follower is not yet justified: prefer distributing bounded invalidation hints from the existing Project watcher across the Web host boundary, then measure that transport against one Web-owned follower if necessary.

No application source, production configuration, production processes, workflow state or deployment changed. The only tracker mutations made by the experiment were in a new disposable Git/Beads repository. The normal implementation evidence comment is separate from those fixture writes.

## Reproduction and evidence

Reference checkout: `89ec84739b8ba426d360984e499ac7d34a8278c7` (main at dispatch). Windows, installed `bd version 1.3.1 (c1c4b642a: HEAD@c1c4b642ac1c)`, embedded Dolt, auto-commit on. Fixture: `C:/Users/mgasp/AppData/Local/Temp/ns-dkq-spike-20261117/run3/fixture`. Two synthetic tasks, one dependency, tiny histories; no remote configured. Ambient `BD_*`, `BEADS_*` and `GIT_*` routing was stripped for child processes. Fixture configuration is local, hooks/metrics/export/auto-push disabled. The follower was terminated and waited in `finally`; all fixture files remain for inspection.

```powershell
python docs/research/beads-event-spike.py C:/Temp/ns-dkq-NEW
python docs/research/beads-event-spike.py --verify C:/Temp/ns-dkq-NEW/result.json C:/Temp/ns-dkq-NEW/model.json
```

Requires Python, Git and bd 1.3.1 on Windows. The runner refuses an existing output directory; it never removes data. Its SQL probe intentionally fails in embedded mode and is recorded as a limitation. Raw generated fixture content is safe synthetic data:

- [Command arguments, exit codes, outputs and timings](beads-event-evidence/commands.json).
- [Events, readiness, retention error and cost samples](beads-event-evidence/result.json).
- [Actual live follower output](beads-event-evidence/follow.stdout), [stderr](beads-event-evidence/follow.stderr).
- [Offline cache model assertions](beads-event-evidence/model.json).

Exploratory run1 stopped on an incorrect assertion that blocker close emits no dependent event; actual seq 8 disproved it. Run2 established that `bd sql` is unsupported in embedded mode, rather than demonstrating SQL noncoverage locally. Both command transcripts remain beside run3 in the temporary root. Run3 completed all live assertions. These were harness corrections, not application fixes or discarded failing application tests.

## Observed event coverage

The journal contained 19 unique, contiguous ordered sequences. Reopening is encoded as `update`, not an eighth operation. The issue payload includes post-write state; it is still only a hint for the proposed cache.

| Change | Captured sequence/operation | Implication |
| --- | --- | --- |
| Create A / B | 1 / 2 `create` | Inventory notification available. |
| Title / label add | 3 / 4 `update` | Presentation and pause membership can change. |
| Comment append | 5 `comment`, full comment plus issue | Invalidate that history; do not assume a title/status identity captures history changes. |
| Add blocking dependency | 6 `dep_add` | A disappeared from native `ready`. Edge includes kind and target. |
| Re-add same edge | 7 `dep_add` | Notifications do not count unique edges. |
| Close blocker B | 8 dependent `update`, 9 B `close` | A appeared in native `ready`; derived changes can precede the blocker event. |
| Reopen B | 10 / 11 `update` | More than one affected issue can emit. |
| Remove edge | 12 dependent `update`, 13 `dep_remove` | Both edge and issue notifications possible. |
| Remove missing edge | No event | Successful no-op is not a change notification. |
| Claim / label remove | 14 / 15 `update` | Ownership/status/pause changes observable locally. |
| Disconnected comment / priority edit | 16 `comment`, 17 `update` | Replayed identically twice after cursor 15. |
| Delete B | 18 `delete`, no issue object | Clear deleted identity and relation consumers. |
| Comment during active follow | 19 `comment` | Already-running follower delivered the write within the bounded five-second check. No exact delivery-latency measurement. |

The experiment checks readiness before and after close with native reads, not an invented graph algorithm. Only a direct blocker was exercised; transitive readiness, nonblocking relationship types, parent cascades, batch transactions, comment editing/deletion and concurrent writers were not individually exercised. Their absence from this matrix is not proof of noncoverage.

## Recovery and convergence

Same-fixture reconnect after seq 15 returned 16 and 17 in order. Repeating that tail returned identical records. The offline model deduplicates sequences, collapses a recorded burst (13 records repeated three times) to one dirty-set flush, and verifies that an event arriving during a captured read remains dirty when that older read completes. A second modeled read settles it. Replaying all 19 unique events produced the same final inventory IDs and title/status/priority/assignee/labels as the final authoritative fixture inventory. This is a deterministic feasibility model, not a threaded implementation, a burst load benchmark or proof that all resource payloads can be reconstructed.

A deliberately disabled journal interval changed B's title without a new sequence. Tail succeeded empty and warned on stderr; `show` revealed the changed title. Re-enabling did not backfill that mutation. This is direct evidence that replay alone cannot establish convergence. The full authoritative recovery read remains mandatory.

After setting both retention floors to zero **only in the fixture**, pruning before 18 made a cursor of 1 fail with `events_journal_truncated`, floor 18, head 19. A full inventory read recovered the surviving A; tail after head 19 was empty. A journal `events export` is historical replay, not an inventory baseline and cannot restore pruned issue state.

The pinned [upstream CLI contract and implementation](https://github.com/gastownhall/beads/blob/c1c4b642ac1c/cmd/bd/events.go) additionally specifies clone/branch-local sequence spaces, no journal notifications for pull/merge arrivals or raw SQL, and a one-second follow poll. Those sync/branch/SQL boundaries were **not** reproduced here; SQL is unsupported by this embedded CLI. Never reuse a cursor after changing repository/replica/Beads branch identity. Rebaseline after sync, replacement, disabled intervals, unknown events, truncation or uncertain identity. A cursor above another replica's head can look caught up indefinitely.

Suggested attach protocol: establish canonical source identity; buffer hints before taking the baseline; read authoritative sources; drain buffered generations and reread anything dirtied during that baseline. On reconnect, replay from the last accepted source-bound cursor, but mark uncertain observations and schedule a full reconciliation. On a retention gap, move to the reported head only together with a full dirty generation and authoritative reread. Retain that generation through errors; advancing the cursor alone must not clear pending work. Bound input records, batches and buffered IDs; overflow means global reconciliation. Do not persist an unbounded event history.

## Current Overview demand versus proposed cost

Source inspection at the reference checkout:

- [Overview](../../src/Nightweaver.Web/Studio/src/main.tsx) renders Delivery, Attention, Ready and catch-up metrics plus compact views. [Explorer default](../../src/Nightweaver.Web/Studio/src/explorer-model.ts) is native `ready`; [digest](../../src/Nightweaver.Web/Studio/src/digest-views.tsx) uses `digest:auto`. Repeated subscriptions share server resource entries; widget count is not CLI count.
- [Observation service](../../src/Nightweaver.Web/OperationalObservationService.cs) shares pinned/temporarily interested entries, with a tracker collection target of one second and runtime/cleanup loops of two seconds. With no interested tracker keys, collection issues no tracker CLI work. Interest lingers for 30 seconds. It currently ages tracker observations overdue after target interval + two seconds.
- [Observation source](../../src/Nightweaver.Web/OperationalObservationSource.cs) shares inventory, requests readiness only for the selected ready view, and bounds native work to roughly four seconds per sweep, two seconds per command, up to four comment histories per sweep. Attention cohorts can contain up to 64 histories. Detail continuations, errors and gate contention alter cadence; source provenance prevents falsely fresh blends. Inventory revalidation and cohort completion cost continue while Overview is interested even if content stays identical.
- [Commands](../../src/Nightweaver/Inspection/BeadsCommand.cs) use readonly/sandbox list, ready, comments, detail and directional dep reads; repository validation also costs Git calls. [Existing watcher](../../src/Nightweaver/Application/BeadsJournalWatcher.cs) advances a cursor, reconnects with bounded backoff and requests discovery. [Client](../../src/Nightweaver/Inspection/BeadsJournalClient.cs) currently exposes sequence only and discards payloads; it cannot selectively invalidate Web sources without a new bounded hint contract. Discovery still has its independent timer. Preserve that scheduling behavior.

Five interleaved native samples per command, after follower shutdown:

| Read | Median wall ms | Min–max ms | stdout bytes |
| --- | ---: | ---: | ---: |
| Full inventory (one surviving Bead) | 403.61 | 347.97–425.69 | 534 |
| Three comments | 255.71 | 225.11–642.16 | 599 |
| Native ready (empty) | 268.70 | 216.56–370.71 | 3 |
| Empty incremental tail | 192.60 | 186.97–233.75 | 0 |

These are process wall costs, not CPU time; a normal solution build started during the latter cost samples, so these are contended diagnostic samples, not a controlled speedup comparison. No production browser trace was collected by this spike. At the one-second target, the simplest interested Overview needs inventory + ready (two CLI launches per sweep), plus due histories. In the ideal under-one-second case that is about 120 launches/minute before histories and Git validation. Actual rate falls with sweep duration/budget and demand expiration. It is **not** 120 measured production launches/minute.

With a proposed 30-second reconciliation, unchanged inventory + ready would need about four launches/minute after initialization, plus event-driven reads: a modeled 96.7% reduction in those launches at the ideal target. History savings depend on paused count and events. This is call arithmetic, not an observed end-to-end improvement. One empty-tail CLI per second still costs 60 launches/minute; prefer a long-lived follower or existing-watcher hint bridge.

Idle follower: after two seconds startup, a 10.00024-second zero-event window used 0.09375 CPU seconds, 990 read operations / 24,815,900 read bytes, 90 write operations / 1,102,330 write bytes, and 5,600 other operations. It stayed alive with no event output. About 2.48 MB/s of **Windows logical process read I/O** is not physical disk traffic. The experiment does not measure durable storage growth, memory stability, large-ledger read complexity, follower effect on concurrent read latency or aggregate Web/Orchestrator CPU. A follower is not cost-free even when its stdout is quiet. Avoid adding one when the console has no interest, and avoid duplicating an already-running Project follower where practical.

## Proposed selective invalidation

| Hint | Dirty authoritative sources / consumers |
| --- | --- |
| Create/update/close | Shared inventory; affected detail; Attention membership/presentation; Explorer and digest projections; native ready/blocked because dependent eligibility can change. Recompute relation endpoint presentations from fresh inventory. |
| Comment | `comments:<id>`, matching Attention/workspace; conservatively inventory (comment-related metadata may matter). Do not reread every other history or native readiness solely for a comment. |
| dep_add / dep_remove | Inventory, both endpoints' incoming/outgoing relations, ready/blocked globally unless transitive closure is independently proven complete; workspace, generated-plan presentation and digest projections. Re-add is an upsert hint. |
| Delete | Inventory, deleted detail/history and relation consumers; ready/blocked. If reverse consumers cannot be enumerated safely, invalidate all interested relations. |
| Unknown/malformed event, gap, reconnect uncertainty, identity change | Global interested tracker reconciliation; retain visible last-good data with explicit uncertainty. |
| Runtime telemetry / cleanup / checkpoint / filesystem environment | Keep their own timers, notifications and authoritative reads. Beads journal cannot establish freshness for these sources. Digest window changes also need clock-driven reprojection. |

Coalesce by source ID and generation, serialize collection, and leave any hints received during a read pending. Never publish an event's issue/comment as a verified cache source. Preserve captured entry identity, memory/read caps, provenance and native readiness semantics. Inactive resource keys need no immediate native read; first interest must obtain a baseline. A quiet healthy stream is transport health, not a new authoritative observation timestamp. A future implementation must distinguish last successful read, stream health and reconciliation deadline so a 30-second timer does not simply create the existing three-second stale flash.

## Bounded next implementation scope

Implement a payload-aware, size-bounded hint seam for one configured source identity, connect it to generation-based invalidation of inventory and per-Bead history, and retain native ready/blocked and relation reads as above. Carry uncertainty across baseline/reconnect/gap handling. Add a slower reconciliation timer and explicit refresh/mutation wakeups; retain independent Orchestrator discovery and guarded workflow mutations. Design a minimal bridge for BackgroundWebHost rather than assuming in-process sharing. No Planner automation or new workflow authority is needed.

Validate with cheap deterministic coordinator tests for stale-generation completion, duplicates, gap/identity changes, failed rereads, history-only invalidation, expiry and idle interest. Reuse existing native coverage for the CLI boundary. Then use the isolated Aspire/Playwright workflow for Overview, Workspace and Attention freshness and recovery. Compare matched idle/no-interest/interested/burst windows on representative inventories: CLI counts, follower/process CPU and logical I/O, source ages, missed-event convergence, and full-suite elapsed cost. Do not enable the optimization in production until that evidence shows a net benefit and bounded recovery. This spike adds an opt-in research runner, no retained application test cases or runtime code paths.

## Repository validation

`dotnet build Nightweaver.slnx -warnaserror` passed with zero warnings/errors. The complete `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename ns-dkq-timing.xml --show-slowest-tests 20` passed: **1,325 succeeded, zero failures, zero skips**, committed six-thread scheduling unchanged. Outer build + complete-suite elapsed was **116.4054863 seconds**; the test runner reported 97.026 seconds. [Recorded stopwatch/revision](beads-event-evidence/suite-result.json) and [terminal summary](beads-event-evidence/suite.log) are retained here; XML remains in the worktree's ignored TestResults directory.

`npm test --prefix src/Nightweaver.Web/Studio` passed all 61 coordinator tests (216.17 ms runner duration). Python syntax parsing and offline verification against the committed evidence copies passed. `git diff --check` passed. The live experiment's assertions passed before packaging; the final script change only added the offline verifier and clarified its usage/comment.

The checked application revision is the reference `89ec84739b8ba426d360984e499ac7d34a8278c7`; the candidate changes only this research package. Prior retained latest-main evidence recorded 1,325 passing tests in 106.6117489 seconds (production-deployment Beads memory). This run includes a separate warning-as-error build and overlapped the last experiment reads, so the 9.79-second arithmetic difference is not an attributable regression or matched performance comparison. The measured 116.41 seconds has 123.59 seconds of operating headroom and is below the 300-second hard limit. No full-suite rerun or production interruption was needed. Live browser QA is deferred to implementation because this spike changes no Web behavior.
