# Test suite

Activity pause repair adds five held-response cases to the existing opt-in
browser journey. They check Pause and page suspension during initial history
and Return to latest, plus row focus during Return to latest. Rows, open evidence
and scroll position stay fixed until another explicit return. The repair adds
no normal-suite native journey, fixture publication or Studio model case.
Production checks current pause intent before it applies a history response.
It adds no request, timer, tracker command or lifecycle write.

Studio Activity adds sixteen controlled client model cases. A 6,000-record burst
checks the 500-row bound. Other cases cover pause, cursor replay, older rows,
filters, evidence links and delayed times. The opt-in Activity browser journey
reuses the isolated QA invocation fixture and connects its production executor
hooks to the journal. It adds no native journey or fixture publication to this
normal suite. Overview adds only a link; Activity reads begin on route entry.
See [Studio Activity](../docs/design/studio-activity.md).

Cleanup Activity repair adds three controlled finalizer cases for successful queue
entry, failed queue entry and direct cleanup failure. Existing native direct
cleanup and deferred worker failure checks remain. The repair adds no native
journey, fixture publication or production command. It checks the existing cleanup
diagnostic before it emits CleanupFailed.

Activity API cases use injected readers, clocks and held response streams for
pagination, filters, loss, cursor expiry, runtime replacement, source failure,
cache bounds and write deadlines. In-process HTTP/SSE checks cover actual wire
identity, reconnect, refusal, capacity and cancellation. A disposable-file case
checks that missing history stays absent with one injected origin read. No native
journey or fixture publication is added. Production reads one bounded journal page
and one runtime file per shared refresh, at most once per two seconds during
Activity interest. One bounded origin Git command runs on first Activity use.
Idle Web hosts add no reads. Existing tests, deadlines and six threads remain.
See [Activity read API](../docs/design/activity-api.md).

Activity API repair adds six controlled cases. Cold history and SSE requests
must wait for shared bounds and refuse a future cursor before successful headers.
Unavailable bounds return 503. Controlled time proves the initialization deadline
and independent cancellation. Maximum permitted PR links and identifiers exercise
complete UTF-8 history and SSE batch sizes, with loss-free continuation across
pages. These cases add no native journey, tracker work or fixture publication.
Wire accounting adds bounded serialization per selected record. It adds no file
read and keeps shared refresh, six test threads and production deadlines.

Cursor runtime repair adds three controlled HTTP/SSE cases. A foreign journal
or missing offset cannot supply runtime replacement evidence from another row.
An exact retained cursor still detects replacement. Each case keeps gap evidence
and checks the separate comparison of runtime observations on an open stream.
The repair adds one journal comparison per examined cache row. It adds no native
journey, fixture publication, source read or tracker command.

Activity startup repair adds five controlled application cases. They run real
repository validation, recovery, pool setup and file-backed journal creation.
HTTPS and SSH origins produce stored runtime and Planning gate records. Missing,
invalid and failed origin reads preserve admission and report journal loss.
Only process-lifetime exclusion is substituted; its native coverage remains.
The fixture refuses native watcher setup. No native process, journey or fixture
publication is added. Production adds one scrubbed Git origin read per startup.
Tracker reads and scheduling limits remain unchanged.

Activity wait and health producers add six controlled helper cases, two discovery
drain cases and one large-inventory gate case. Existing Planning, Delivery and Merger coordinator cases
now assert Activity records beside unchanged admission results and source read
counts. Planning threshold cases check the observed count and configured limit.
Ten thousand same-reason updates retain one state and one row. Source failure,
stale coverage, recovery and submission loss do not complete an unknown wait.
No native journey or fixture publication is added. Existing native concurrency
and journal storage coverage retain those boundaries. Production adds fixed
state per pool, one nonblocking submission per change, and the existing bounded
journal worker. It adds no tracker command. Unchanged polling adds no disk write.

Activity lifecycle producers add controlled record assertions at existing host
boundaries and nine cheap runtime cases. Existing native launch and closure/cleanup
journeys gain record assertions. They add no native journey, fixture publication
or browser work. Production adds one bounded journal worker, finite receipt and
inventory state, and one Git origin read at runtime startup. Producer hooks add no
tracker command. See [Activity lifecycle producers](../docs/design/activity-lifecycle.md).

Activity journal tests add controlled storage and clock cases plus disposable-file
read, write, rotation, cursor, corruption, identity and sentinel checks. One short
junction command and one hard-link call prove actual Windows path safety. They
add no factory, Agent, tracker or browser journey and no fixture publication.
The storage slice adds no active production work. See
[Activity journal](../docs/design/activity-journal.md) for format and failure rules.

Activity evidence uses in-memory projection, schema and replay cases in
`ActivityModelTests`. They cover all event families, evidence strength, absent or
conflicting merge proof, stale sources, unsafe links, secret sentinels, Unicode
identifier rejection, clock changes, runtime replacement and finite client
memory. They add no native journey, fixture publication or active production
work. The model is not connected to runtime or Web behavior in this task.
Replay repair adds six in-memory cases. They check inventory before and after
distinct events, exact publication binding, absent binding and invalid binding.
They preserve publication row anchors and cursor progress. No native cost is added.

Pause handoff repair removes one redundant inventory command after a successful
diagnostic write and final workflow check. The guarded claim release uses that
immediately preceding check. Failed or cancelled verification still requires a
fresh read before release. The separate ten-second release bound stays unchanged.
Two controlled cases check success without a read inside that bound and refusal
after a pause change during the diagnostic write. The existing second-signal
native journey retains its dirty-worktree, foreign-claim, process-settlement and
pause assertions. No native journey or fixture publication is added. Six threads
and all deadlines stay unchanged. See the
[publication proof](../docs/acceptance/planning-publication-native.md#hosted-pause-handoff-repair)
for the failure boundary and evidence limits.

Standalone Web host teardown waits for the native process signal before fixture
deletion. Managed exit can precede Windows handle rundown and leave the
process-lifetime `web-payload.lock` open. The existing environment/command-line
and configured-target journeys now require exclusive payload access after exit.
They retain their live-lock and cleanup assertions. This repair adds three
exclusive file opens across existing cases. It adds no native journey, fixture
publication or production work. Deadlines and six threads stay unchanged.

Publication native proof adds two tests in `PlanningPublicationNativeTests`.
One test shares a copied Beads template, an isolated host and one detached
Planning checkout across response-loss recovery, provenance conflict and legacy
partial-graph recovery. The second test calls the existing cross-process,
cross-Project, cross-host and physical-alias exclusion scenario, which previously
had no test entry point. Both reuse existing immutable fixture publications.
Native database semantics and OS mutex ownership cannot be proved by injected
responses. Detailed fault and concurrency cases remain controlled tests.
The focused XML costs are 69.058 seconds for publication and 26.235 seconds for
process exclusion. Their collection starts early at that measured weight; the
six-thread cap and deadlines stay unchanged. No production code changes.
See [publication proof](../docs/acceptance/planning-publication-native.md) for
native evidence, concurrency limits and complete candidate cost.

Legacy ID adoption repair adds four controlled publication cases. They reject an
ID for another entry or revision, with and without a retained binding, before any
publication write. They preserve the task and its publication hold. The repair
adds one in-memory identity check per binding. It adds no native command, journey
or fixture publication.

Actual task ID publication adds 26 controlled publication cases. They check native
allocation, invalid or missing create responses, legacy partial creation and
release, changed entries, retained graph and receipt conflicts, unlinked duplicates, damaged
provenance, missing tasks and late task appearance. They add no native journey
or fixture publication. The fixture rejects `--id` in every new create command.
Production adds one complete inventory check per new task before launch and one
native child check after task resolution. Graph verification and Delivery use
the verified actual IDs. Detailed recovery checks stay in the controlled fixture.
The separate native-proof Plan Bead owns full isolated native publication proof.
Six test threads and all existing deadlines remain unchanged.

Publication confirmation repair adds three controlled cases for a reused assignee
at intent, membership and receipt confirmation. The existing publication fixture
now advances the Product version on ordinary updates and returns native-shaped
mutation responses. Lost-update cases require preserved holds and fresh recovery
authority. The duplicate case includes its native Product link and checks every
required diagnostic field. The existing isolated metadata transport journey also
checks returned versions and the unchanged Product version after a comment. It
adds two native commands (comment and show), no new journey or fixture publish.
These native version semantics cannot be proved by an injected runner. Production
adds one complete inventory read per publication call before the generic child
check. It adds no grounding sweep or write to a successful publication.

Shared-read candidate evidence adds eleven cheap Node checks for actual Overview
resource selection, section receipts and native command families. The opt-in native boundary trial checks cold shared base
reads, confirmed absence, journal refusal, backup audit recovery and failed
inventory retention. It reuses the existing isolated tracker, browser, Agent
fixture and tool shim. It adds no normal-suite test, publish or production work.
Source-substituted browser journeys retain presentation-only evidence.
Three additional Node cases check independent settlement bounds and unknown
bytes. The opt-in process observer shares each existing QA trial and adds no
normal-suite journey. It polls identity-checked native handles every 10 ms,
retains at most 10,000 entries and runs only in the explicit measurement runner.
Eight more cheap Node cases check scenario boundaries, identity confirmation,
missing clock evidence and incomplete native results. The retry reuses nine
native matrix cells and adds only the missing settled Overview cell. Its
76.8006866-second trial includes startup, browser work and exact stack stop.
It adds no normal-suite journey. The retained 2184-test complete measurement
remains applicable because production and normal test sources are unchanged.

The launch-gap repair adds one cheap Node rejection case and five opt-in native
process checks. The native checks prove Windows Job inheritance and termination
between launch and record publication. Injected source tests cannot prove this
kernel boundary. They share one probe binary and need no tracker, AppHost or
browser. The repair repeats only the two affected settled Explorer and Attention
cells with the existing fixture. It adds Job admission polling to that optional
instrumentation, with a one-second refusal bound inside the unchanged production
command deadline. It adds no normal-suite work. The candidate acceptance report
records measured check and trial costs. The unchanged complete suite is reused.

Workspace base rendering adds five injected source cases and five Studio model
cases. They check publication before held text, history and relations, shared
Planning identity, section failure, delayed identity and confirmed absence.
The coordinator fixture also checks guarded-command refresh of base and section
keys. These cases add no normal-suite native journey or fixture publication.
The optional `workspace-base-qa` browser probe checks held section receipts,
direct links, failed refresh, pause removal, deletion, keyboard tabs and draft
retention. It shares the existing QA AppHost group. The native browser boundary
proves that React renders base content before section responses arrive; injected
source tests alone cannot prove that display behavior. Production uses existing
bounded section reads and adds Planning identity projection to shared base rows.

Digest retention repair adds three controlled-clock service cases and two Studio
coordinator cases. They poll retained successful responses for 130 seconds of
controlled time after failed or pending replacement reads. They check section
expiry and recovery separately from base age. No native journey or fixture
publication is added. The existing digest browser journey checks HTTP 200 stale
section responses in Catch Up and Overview. Production keeps one monotonic anchor
per accepted resource and advances at most 128 section ages per response. It adds
no tracker command and keeps the existing cache and native-work limits.

Digest receipt repair adds one controlled-clock Studio coordinator case. It
checks failed reads, source expiry, a short view absence and successful recovery.
The existing digest merge browser journey checks the actual view mount with a
controlled monotonic clock and failed section responses. It shares the existing
tracker, AppHost and browser, and adds a five-second navigation absence. No
normal-suite native journey or fixture publication is added. Production exposes
the existing shared receipt time and uses it for section age across view mounts.

Digest base publication adds six injected observer cases, one projector case and one Studio model
case. They hold history across base publication, check partial progress, comment
and audit refresh, failed/capped evidence, checkpoint changes and identity races.
They add no normal-suite native journey or fixture publication. The existing
digest merge browser journey checks Catch Up and Overview before withheld merge
sections arrive, with a competing Workspace history request. It reuses its
disposable tracker, AppHost and browser. Existing detailed producer checks remain.
Production adds bounded base projection and independent compact evidence states.
Native history work shares the existing schedule and one confirming inventory
read per detail turn. All command limits and six test threads remain unchanged.

Section repair adds four deterministic cases. They hold partial-relation
confirmation across inventory or dependency invalidation, spend a core turn
after a warm partial result, and keep only text interest after reading history.
They check accepted provenance, current successful directions, idle history
reservation release and zero collection work after required audits settle.
These cases add no native journey or fixture publication. Production adds
generation checks and retires unused section audit slots.

Independent section reads add deterministic observer cases for shared histories,
selected progress before cohort completion, caller cancellation, failed-refresh
provenance, identity conflicts and deletion, entry/byte eviction, reservation
failure, late callbacks and host shutdown. Controlled native costs also check
base, background and guarded-operation progress during repeated selection.
They add no native journey or fixture publication. The existing operations
Playwright journey checks the additive section API and a comments-only update
on its existing disposable tracker, AppHost and browser. Production reuses the
current CLI parsers and shared source values. Compatible sections add one shared
confirmation only when native detail work follows the latest inventory read.

Shared base publication adds injected observer cases for held legacy detail,
cold direct-link validation, native membership failure and continuation,
failed-refresh retention, confirmed absence, audit refresh, raced hints,
late callbacks after eviction and the shared two-Workspace identity limit.
These cases add no native journey or fixture publication. The existing isolated
Explorer browser journey checks the new schema and inventory revision, native
membership changes, warm navigation and retained stale rows at three sizes.
Repair cases check the missing-resource error with its absence proof, deletion
after accepted membership, a raced inventory hint, held Ready and Blocked reads,
actual source failure, age expiry and real turn exhaustion. These cases use
injected commands, barriers and controlled time. They add no native journey.
Production projects one compact indexed cache per accepted inventory content
identity when base keys have interest. Base callbacks add no native command.
Legacy full-response collection and its native budgets remain unchanged.

Admission titles use deterministic payload, publisher/reader and injected pool
checks. They cover invalid title metadata, scalar bounds, slot identity changes,
same-Bead stage updates, idle clearing, delayed foreign observations, both wire
schemas and Project/runtime replacement. Injected admission permits zero tracker
commands. The existing `factory-qa` browser journey adds title/ID/link and plain
text checks to its current isolated stack; no normal-suite native journey or
fixture publish is added. Production adds bounded title projection once per
admission and one optional field per slot in the existing snapshot.

Scheduler registration and owner admission use deterministic store/adapter tests
in `ScheduledSupervisionTests`. These cover partial registration, lost responses,
foreign definitions, suppression, stale and duplicate requests, lock contention
and preserved recovery budgets. They add no native process or fixture publication
to the complete suite. Production Scheduler work runs only for explicit commands
or opted-in ticks; manual startup keeps its existing behavior.

The explicit native topology proof is outside the solution and normal suite:
build `tests/Nightweaver.QA.SchedulerProbe`, copy its complete output to `<unique-root>/bin`,
then run `<unique-root>/bin/Nightweaver.QA.SchedulerProbe.exe proof <unique-root> <nonce-N-guid>`.
Use an existing current-user interactive session. The proof creates only two
nonce-named limited InteractiveToken tasks. It retains requested/registered XML,
tick/request/owner/child identities, post-tick observation, settlement and cleanup.
It disables and removes only definitions it created and verified. Preserve failed
evidence before a changed diagnostic run. This proof covers Scheduler and Job
boundaries that recording adapters cannot establish. It shares one isolated root
and tests no live Project. Independent full recovery verification belongs to ns-8s1h.

Upgrade activation uses in-memory unit cases for orchestration and release
attestation. The coordinator's production wiring still uses the real journal,
payload replacement and lifecycle. The separate package replacement, journal,
and lifecycle test collections cover their own filesystem contracts.

The unit cases exercise reserved launch ordering, resume without launch replay,
intent changes, final owner health and expiry of prior observations. Unit
coverage replaces the former package-backed activation journeys. The
`UpgradeReplacementTests` collection keeps its 38.491-second scheduling
weight from the earlier resume cost repair.

Existing Web journeys also exercise the compatible health endpoint; legacy/dev
hosts omit release evidence and keep their exact health JSON. Normal owner startup validates a release
inventory only when one exists, and each owner health probe checks the launched
Web process identity. Complete-suite measurements include that production cost.

Upgrade drain uses disposable store/command-lock fixtures and injected native
operations. Cases cover manual opt-out, exact maintenance ownership, root-wide
startup suppression, lost drain replies, PID reuse, unsettled descendants,
foreign/unknown owners, changed configuration and later stop/disable. The
inventory adapter uses injected Git output with real files and leases. These
cases add no native process or fixture-publish journey. Existing continuous and
explicit-dispatch native journeys now acquire two short startup file locks and
read the root interlock; complete-suite timing includes this production cost.
Standalone Web admission adds shared process-lifetime payload leases. Disposable
file checks cover concurrent Web consumers, background/Web overlap, reservation
and mutation suppression, and release. Injected native membership distinguishes
the contained Web child from an additional standalone Web consumer. The real
inventory and drain boundary require the original Job's descendant settlement.
Valid pending supervision reservations refuse drain, settlement and mutation
with either a settled previous owner or no previous owner record. These repair
cases add no native journey or fixture publication. Existing standalone Web native journeys
now assert that the process holds its lease. One short child of the existing
ExecutionCrashHost fixture proves that a competing process cannot create a fresh
host root before repository exclusion. In-process mutex checks cannot prove that
native boundary. This adds no fixture publish, Agent, owner or browser journey.

Cold Workspace coalescing adds deterministic parser/fallback, generation-race and
finite-contention cases. The identical command-independent simulated contention
interval fails the old cold predicate and permits the combined path to complete;
it does not identify the historical hosted cause. The existing native observer
smoke now seeds one closed and one ephemeral incoming owner, retains the empty
cold comment history and compares incoming relations with native `up`. This
extends its existing journey rather than adding another native fixture launch:
the uncovered boundary is actual embedded show iterator parity, which injected
responses cannot establish. Native outgoing reads and combined external
comment/dependency journal-causality assertions remain. Omission, malformed or
unsupported producer/flag responses, discovery failure, limits, permanent outgoing failure and warm budgets are
covered cheaply. Deadlines and committed six-thread scheduling are unchanged.
Two deterministic cases also retain a nonempty Attention history across an
expired audit and Workspace selection. Failed or unsupported producer discovery
exhausts the injected turn budget. Both cases require a later native comment
read and the actual history before successful Workspace publication. They add
no native journey or fixture publish.

The cold native observer smoke retains at most 512 payload-free timeline records
with monotonic offsets, source/reason, command class, requested deadline and
outcome. Opt-in process phases distinguish launch, managed exit, native settlement
and stream drain; journal phases distinguish preparation from page parsing and
settlement. These callbacks are unset in production. Diagnostics add no native
journey or command and preserve the two-second command, four-second sweep,
45-second functional bound and six-worker scheduling. Passing instrumentation
alone does not establish a repair for the intermittent hosted cold timeout
tracked by ns-a3d4/ns-5uk.

Run the complete retained suite from the repository root with the .NET 10 Microsoft.Testing.Platform runner. The positive minimum catches mistaken test selection; use at least the committed floor of 995 tests and report the actual discovered total.

Use PowerShell 7 and the single retained measurement entry point:

```powershell
pwsh -NoProfile -File scripts/Measure-TestSuite.ps1 -Checkout <absolute-checkout> -EvidenceRoot <absolute-evidence-root> -BaseRevision <full-base-sha> -Baseline <accepted-result.json> -ProductionInstallation <absolute-installation> -Project nightshift
```

The operating target is **below 240 seconds**; the **below 300-second hard limit**
remains unchanged. Measure normal build, fixture publishing, execution and teardown
with the committed six-thread parallelism. Report actual full-suite counts, exact
baseline/candidate identities, outer elapsed time and concrete cost changes for
every change. A filtered run never establishes full-suite acceptance. Hosted time
is diagnostic and final exact-head correctness remains required.

The retained measurement also runs the cheap [unattended archive fixtures](../scripts/tests/UnattendedEvidenceArchive.Tests.ps1)
inside its outer stopwatch and reports their counts/time separately from xUnit.
Fixture failure prevents acceptance even if xUnit passes. Windows CI runs the same
checks inside the timed correctness step. For direct format inspection and its
integrity-versus-coverage semantics, see [the archive format](../docs/acceptance/unattended-evidence-archive.md).

The same stopwatch includes [collector fixtures](../scripts/tests/UnattendedEvidenceCollector.Tests.ps1)
and reports `collectorFixtures` counts and `collectorFixtureSeconds`. These use
injected clocks/readers/storage for the eight-hour schedule and short disk
publication intervals; they add no Agent or overnight lifecycle journey. A
separate disposable [qualification](../docs/acceptance/unattended-evidence-collector.md)
exercises real file/HTTP/process boundaries and reports overhead without
operating production. Collector fixture failures prevent suite acceptance.

See [test-cost evidence](../docs/agents/test-cost-evidence.md) for the versioned
measurement/comparability contract, native-boundary justification, capture-first
1.5x diagnostic trigger and ordinary repair routing. Reuse comparable retained
evidence; Merger's mandatory fresh synchronized-head checks and post-integration
latest-main acceptance remain required. For draft CI and final hosted checks see
[hosted verification](../docs/agents/hosted-verification.md).

## Coverage pyramid

Upgrade journal fixtures use disposable files for restart inspection, immutable
history, stale writers, strict schema and identity rejection, sharing denial,
cancellation and lost replies before/after atomic rename. One short junction
helper verifies that the journal's actual no-reparse access cannot touch an
outside sentinel. Existing supervision tests prove the common primitive; this
case proves this store uses it for reads and writes. No owner, Agent, browser,
fixture publish or lifecycle journey is added. See the
[internal progress contract](../docs/design/upgrade-v1.md#internal-progress-storage).

Prepared release fixtures validate complete paired inventories, assembly identities,
strict schema/path/hash refusals, Project/root/repository inventory and read-only
preflight command output. They retain sentinels and reject unavailable compatibility
without lifecycle writes. One short junction helper proves link refusal; no owner,
Agent, repeated publish or browser journey is added. Run publisher component checks
with `pwsh -NoProfile -File scripts/tests/Publish-Nightweaver.Tests.ps1`; these reuse
injected publishes. See [upgrade preflight](../docs/design/upgrade-v1.md#read-only-release-preflight).

Upgrade contract fixtures exhaust the pure phase/step table and refuse missing
identity, compatibility, interlock, native settlement, payload or fresh acceptance
evidence. Stopped/disabled/maintenance intent and rollback-readable forward state
are checked without processes, files, fixture publishing or browser journeys.
See [recoverable upgrade v1](../docs/design/upgrade-v1.md); this worksheet does not
prove future mutation adapters.

Finite supervision ticks add deterministic coordinator cases and disposable
store/command-entry fixtures. They cover mutation-boundary suppression, crash
reservations, exact-run readiness, uncertainty, failed startups, persistent finite
retry exhaustion, manual/tick contention, and failure-only graceful control. No
native journey, scheduler registration, Agent launch or fixture publishing is added.
Existing physical-repository exclusion and detached-owner containment journeys
remain the native boundaries; the later isolated recovery proof covers their combined
journey. See [supervision v1](../docs/design/supervision-v1.md).

Classifier telemetry fixtures use a controlled HTTP transport beneath the real
OTLP serializers and safe export handler. The five successful active-phase cases
still require loss-free diagnostics; a separate active metrics rejection requires
the exact export-loss diagnostic, and a held response distinguishes request capture
from acknowledgement before shutdown. Classifier records, private-data exclusion
and absence of unverified lifecycle publications remain required in every case.
Real loopback transport, response failures, privacy and native first-path coverage
remain in `OperationalTelemetryTests` and `FirstPathTelemetryTests`. This adds one
cheap deterministic case, no native journey or fixture publish, and changes no
production export deadline, shutdown budget or six-thread scheduling.

Identity-bound supervision adds cheap injected process/control/runtime/clock and
collector cases for healthy/start/drain/failure/uncertainty, source identity and
freshness, PID reuse, owner replacement, capture ordering/errors, bounded history,
privacy and failed persistence. The existing detached-owner containment journey
now names its Job and probes through the production observer before and after
owner death. Its original owner/child launches, caller-policy checks and deadlines
remain; no native journey or publish is added. This proves the uncovered Windows
boundary that live observation holds no Job handle delaying kill-on-close and
that original native exit plus Job accounting can establish descendant settlement.
Report its measured cost against retained baseline XML with the full candidate run.

Disposable capture-journal mutations cover missing/null required members, invalid
enums and observation identity, duplicate/unknown fields and inconsistent incident
history/capture state. Reads and preparation must reject them, invoke no collector
and preserve the original bytes. Recovery-A/capture-B mutations cover explicit
null and misdirected current pointers for Failed/Pending B, with both ordinary and
failed-capture A. Positive cases retain recovery followed by uncertainty and a
subsequent incident after the blocked record outlives bounded history eviction.
These deterministic cases add no native journey.

`OwnerFailurePublicationTests` inject sharing denial and publication cancellation
for stop/force/maintenance, concurrent owner failure, and discovery/timer/completion
infrastructure termination with a blocked pool drain. These cheap cases verify
truthful control state and retained failure identity before drain without adding
native journeys or fixture publishing. Deliberate cancellation remains distinct.
Blocked Web-readiness cases cover control stop, maintenance and console stop,
with and without prior failure evidence. They verify disposal and unchanged
failure/incident history; genuine readiness errors still publish before disposal.
These use injected operations without adding native launches or fixture publishing.

Journal cancellation/deadline tests assert native process signaling before
return and disposable-file teardown. Their 16 iterations per path cover Windows
process rundown, where a managed exit notification can precede native settlement;
the journal shares the process runner's native wait within its existing command
and five-second cleanup budgets. No teardown sharing-error retry is added.

Supervision v1 adds deterministic state/clock/backoff fixtures and small local
store fixtures for revision guards, crash reservations, corrupt/foreign state,
sharing denial, bounded serialization and sentinel-preserving unsafe-path checks.
One disposable junction creation proves the new store's actual no-reparse
boundary using the existing mklink pattern. No runtime, Agent, scheduler or
fixture-publish journey is added. See [the dormant contract](../docs/design/supervision-v1.md).

Dashboard acceptance reuses the native first-path Git/bd/Agent correlation smoke.
Two bounded paired-runtime/Web loopback component cases add parallel arrival
pressure, actual encoded byte/record measurement and unavailable receiver checks;
no native journey or fixture publish is added. Disposable Grafana import, every
panel query, freshness gating and Playwright links run outside the default suite
through `tests/telemetry-first-path/Run-Proof.ps1`. See the
[dashboard contract](../docs/design/grafana-bottlenecks.md).

Run `node tests/telemetry-first-path/approval-ratio-proof.mjs <fresh-evidence-directory>`
for eight deterministic PromQL cases against the generated approval panel using
the pinned local image's promtool (Docker required). This checks later-pass-only
zero, mixed/first-pass ratios, absent/zero denominators, Unknown exclusion and
role isolation without another native journey or a running backend.

Lifecycle observation adds deterministic monotonic-clock coverage of first-pass
known/unknown visits, semantic repair versus retry attempts, pause labels, notes,
abort, deletion/reopen, restart, stale reads, UTC jumps and the shared cache bound.
Existing confirmed/reread/refused Delivery publisher fixtures and a Planning
publication fixture check lifecycle wire records without another native journey.
See [the lifecycle contract](../docs/design/lifecycle-observation-telemetry.md).

The native Beads cancellation fixture pins the lock owner's PID and creation time,
then requires the actual Windows process signal and exclusive lock access before
disposal. `SystemProcessRunner` waits for that signal before reading the native
exit code or completing cancellation: managed exit notification can precede
Windows process rundown. This strengthens the existing journey without adding
native cases, changing its deadlines or reducing six-worker scheduling.

Web observation telemetry adds four cheap decoder/loopback/component cases for shared
subscriber collection, scheduled no-op suppression, monotonic retained age,
timeout/unavailable/idle/stopped states and exported canary exclusion. Existing
standalone hosting journeys now inspect observation export and the existing
published standalone/background journey checks SDK dependencies. No native
journey or publish is added; the uncovered boundary is the real executable's
environment-only exporter and observer wiring, which direct service tests cannot
prove. See [the contract](../docs/design/web-observation-telemetry.md).

Execution overhead adds deterministic monotonic-clock phase allocation cases and
reuses fake classifier/publisher seams plus in-process OTLP receivers for private
canaries, primary/fallback diagnostics, confirmed/reread/refused publication,
cancellation, concurrent scopes and unsampled retry/repair totals. No native
journey, deadline change or additional tool command is introduced. See the
[observation contract](../docs/design/execution-overhead-telemetry.md).

The invocation-to-verified-merge telemetry slice adds one native OTLP smoke using
the existing immutable AgentStage template: actual fake Agent launch/settlement,
native Git/bd verification and guarded closure must export matching operational
IDs without prompt/output canaries. Existing executor and verifier tests cannot
prove that exported correlation boundary. Seven cheap finalizer cases cover
guard mismatch, uncertain response, unconfirmed closure, duplicate callback,
cleanup failure/cancellation and already-closed inventory; bounded-role/sampling
coverage is also deterministic. Disposable Grafana/Collector/backends and
Playwright run only through `tests/telemetry-first-path/Run-Proof.ps1`, outside
the normal suite. See [the contract](../docs/design/operational-telemetry-first-path.md).

The native console inheritance probe reads its atomically published child readiness
file within one monotonic twenty-second deadline. Missing-file and sharing/lock
violations can settle within that budget; other I/O errors, malformed JSON and
nonce/PID mismatches still fail. Six cheap controlled cases exercise transient
sharing, permanent contention and unchanged payload/error handling. The existing
native journey retains its live-child, pipe inheritance and cleanup assertions;
no extra process journey or longer deadline is introduced.

The native Dolt descendant fixture waits for a CSV result marker emitted after
database acquisition before publishing lock readiness. Exclusive `LOCK` polling
could itself deny Dolt's startup open with incompatible sharing flags. The existing
settlement cases still verify the live child, held/nondeletable lock before and
after root exit, and settled child plus deletable lock after disposal. Their
ten-second readiness, fifteen-second root, thirty-second query and six-thread
scheduling bounds are unchanged; no native journey was added.

Run the cheap paired publisher contract explicitly in local Review/QA:
`powershell.exe -NoProfile -File scripts/tests/Publish-Nightweaver.Tests.ps1`
and `pwsh -NoProfile -File scripts/tests/Publish-Nightweaver.Tests.ps1`.
CI runs both shells. Disposable staged files and a controlled `dotnet` function
exercise the real script interface, Studio-only success, exact revision manifest,
missing entry/JS/CSS/apphosts/prompts, staging cleanup and destination preservation.
These checks perform no real builds or publishes. Existing published standalone
and background hosting tests retain the native hosting boundary.

Journal hint tests use fake batches and delay callbacks for pagination, atomic cursor
commit, failure backoff, truncation, session replacement and cancellation. In-memory
parser cases cover multiline JSON, invalid suffixes, UTF-8 byte and record limits,
and aggregate output including whitespace. The existing native tracker journeys
now read finite batches, retain native truncation, scrub a decoy target and prove
that an unjournaled write is discovered by the independent timer during a batch
failure. Shared managed fixture pipes cover stderr flood, malformed-output termination,
and actual child settlement on cancellation or a controlled 30-second deadline;
no real 30-second timeout is added to the suite. Journal hints now have a 30-second
cadence plus command execution, with one page per tick even under retained backlog.
This change reduces recurring journal work without asserting whole-suite causality.

The classifier timeout fixture publishes root/descendant PID handshakes by
renaming a completed, closed same-directory temporary file. Existence cannot
authorize forced termination while a PID writer is still empty. A cheap fixture
case pauses before writing and checks that the public marker is absent; the
native smoke retains actual Job termination of both processes and its original
deadlines. `SystemProcessRunnerTests` uses the existing managed `flood` fixture
for its bulk-stream boundary: both 1,100,000-byte streams and their exact contents
must drain within the unchanged 30-second limit. This removes PowerShell startup
from that contract while retaining a real child and pipe backpressure; the
The output-limit check also uses the native producer, retaining a 30-second
post-output hold so the unchanged 10-second deadline must be interrupted by the
read limit. The environment-removal check uses a native environment probe and
first verifies inheritance in a control invocation. PowerShell timeout and
Windows command-line launch checks remain. Hosted PowerShell timeouts in the
bulk, output-limit and environment checks did not establish a shared production
cause; removing that unrelated shell dependency does not diagnose its startup.

Workspace-only observation retries retain their captured inventory for one
continuation turn while detail work is pending, avoiding redundant native core
reads without indefinitely retaining a snapshot. A fresh confirming inventory still guards publication and changed identity
fails closed. Scheduled mixed Attention/Explorer subscriptions also reserve one
detail continuation when core reads leave no detail capacity, then return to core
validation. A clock-driven regression invalidates every slow inventory/readiness
read with both Ready and Blocked interested, with and without a selected Workspace.
Cold histories and their later audits must progress without a quiet core turn;
the dirty inventory keeps the projection pending until authoritative recovery.
The existing controlled timeout cases assert the reduced command counts, and
the slow-clock case models 1.9-second reads without adding wall-clock waits.
A cheap two-Workspace case holds one history source timed out, creates and selects
another Workspace, then changes its identity and requires both updates to publish. Identity conflicts force
a fresh core read so another pending cycle cannot retain the old baseline forever.
The same case then fails all core refreshes and requires pending detail retries to
continue against their captured baseline; failed refreshes retain visible errors
and cannot satisfy the final confirming read. The Beads subprocess Go CPU budget
also applies in production. A warmed balanced six-process probe measured a lower
median (1,072ms to903ms) and CPU use with two Go workers; its tail did not improve,
so this probe does not establish a fix for every hosted source timeout.
Failed published classifier smoke runs retain their gates and runtime telemetry
for hosted diagnosis; this does not extend their Agent-start or stop deadlines.

Factory snapshot publication falls back from Windows move access-denied to a
no-follow source handle rename while all destination parents stay pinned. Existing
short/deep file-boundary cases hold a delete-sharing reader that denies writes:
the old reader keeps old bytes and fresh readers receive the complete replacement.
The existing hard-link case also checks that this fallback preserves foreign
content. Read-only destinations and unsafe reparse paths still fail closed;
there is no delete-before-move or timeout increase.
The native rename buffer copies exact UTF-16 path code units. The existing deep
case uses an unmatched surrogate plus a distinct replacement-character alias
with a foreign sentinel, proving encoding cannot redirect the publication.

The native observer's two external writes explicitly enable Dolt auto-commit,
matching production rather than inheriting its fixture's read-probe default.
Read/write overlap and the original cold/update observation deadlines remain.
After a settled baseline, test-side source-read recording requires dirty comment
and dependency reads after their journal hints, plus an authoritative inventory
read after the dependency hint, and excludes tracker audits until combined
visibility. If the dependency hint arrives during Workspace detail work, its
mandatory post-detail inventory is labelled `confirmation` and satisfies the
same causal boundary as a `dirty` inventory read. Controlled component cases
cover hints before core collection and during detail work, require the combined
comment/relation projection with no pending source generation, and reject audit,
bootstrap and pre-hint inventory evidence. No native journey or deadline is added.
The independent two-second environment audit remains
allowed. This observes the actual event path instead of treating total elapsed
time below 30 seconds as proof of the 60-second tracker audit boundary. Command
timings, source causes and exact page/read counts survive assertion failures.
A controlled cold-start case injects one timeout in detail and each relation
direction plus two inventory timeouts, then requires a complete error-free
scheduled Workspace with no pending generation inside the same 45-second bound.
It retains the unsupported-producer fallback path through the existing scripted
runner and clock, without another native journey. The bounded native timeline
described above supplies command outcomes, journal phases and source-read causes.
The earlier separate journal diagnostic queue and full command records are
superseded by that timeline. The five-timeout case proves fallback recovery;
the merged cold coalescing regressions prove the independent production repair.
A balanced local comparison after identical mutations measured 5/24 reads above
two seconds with auto-commit off and 0/24 with it on; this is performance evidence,
not a claim that off loses SQL-committed data. Both smoke phases retain command
and external-write timing diagnostics on failure. `BD_DISABLE_EVENT_FLUSH` controls
the detached metrics uploader, not durable tracker journaling, and is unchanged.

The native wait/cancellation fixture starts the held child with its required PID
path and verifies the published PID before testing the original 50ms wait. A
bounded readiness phase reports early fixture exit instead of accidentally testing
startup speed. Cancellation, repeated waits and native termination bounds remain.

The Web journal's clock-driven regressions exercise slow successful preparation,
per-command two-second cancellation, the four-second cumulative native-work cap,
and cursor reset after a failed first page. They add no native journey or sleeps.
The retained timer/journal native journey serializes setup, finite pages, inventory
and raw SQL against its single embedded store, including the journal-head check
inside the SQL write section. Its one timer tick, unjournaled visibility, failed
batch and exact-target cursor assertions and original deadlines remain intact.
This removes fixture overlap; it does not claim every hosted read timeout has
the same cause. Discovery diagnostics are retained on failure.

Test fixtures copy settled immutable repositories into independent file trees,
including Git objects and tracker stores. The small `GitFixture` initializes one
empty Git baseline; manual-pilot profile templates seed independent copies of
one empty Git/Beads baseline. Consumer writes must never reach a template or
another copy. Existing isolation cases check configuration, tracker and origin
independence. Provisioning preservation snapshots obtain HEAD, branch and status
from one native `git status --porcelain=v2 --branch` command instead of launching
three processes or adding concurrent native work. Index bytes, working files,
HEAD, branch and status assertions
remain intact; upstream ahead/behind metadata is excluded because an allowed
fetch can update remote refs without changing the checkout. These setup changes retain all cases and native
boundaries; only a complete timed command establishes their suite cost.

Test-owned managed launches use `FixtureProcess.Start`, including shared manual
Delivery and QA helpers. It holds `WindowsProcessLauncher.ProcessCreationGate`
only during `Process.Start`, matching the production process runners. A native
launcher's HANDLE_LIST limits its own child; it cannot stop an unrelated managed
CreateProcess from inheriting temporary pipe writers. Bypassing the gate can
leave coordinator drains pending even after the coordinator exits and its Job
is proven empty. The two small `FixtureProcessTests` contention cases retain
both managed Start overloads; the existing native blocked-drain and launch-burst
smokes retain actual pipe/Job/foreign-child coverage. Do not add raw managed
starts to the shared test process. Console helpers restrict inheritance with
their own HANDLE_LIST and do not need to join this gate.

The ns-mbd.6 native timeout investigation found unused Ready/Blocked reads
consuming the observer's four-second sweep and repeated full inventories in
task-only Merger finalization guards. Observer component cases inject 1.4-second
command costs and require Workspace progress without unused membership reads.
Merger task guards now use fresh exact-ID `show` reads; strict component cases
reject missing/foreign, reopened, paused and assigned responses before branch
deletion. Product eligibility and shared environment cleanup still use their
required inventories. The native observer, Merger pool, lost-close/locked-tmp
recovery and conditional Git ref deletion boundaries remain. No deadline,
six-thread limit or assertion is relaxed.

The retained suite keeps broad, fast decision and security checks in unit/coordinator tests and one representative set of expensive process boundaries:

The duplicate-failure classifier timeout smoke uses a controlled role clock and
a startup release file. Its former two-second real-time budget could expire
before the managed fake CLI wrote either PID, testing startup speed instead of
Job containment. The retained native journey now holds startup, consumes one
second of the same two-second role budget, releases startup, waits for both
root/descendant handshakes, then expires the remaining second. Both actual
process termination assertions remain. Real handshake/result/cleanup bounds and
six-thread scheduling are unchanged; failure cancels and settles the invocation
before fixture disposal. `AgentInvocationDeadlineTests` covers startup/prompt
delivery and time already consumed inside native launch without real processes
or sleeps. Production uses `TimeProvider.System`, with the role deadline still
starting before native launch; cleanup continues to use its separate real budget.

| Concern | Detailed fast coverage | Retained real-tool boundary |
| --- | --- | --- |
| Ownership, read-only roles, failure classification, and budget precedence | `AgentPostExitVerifierTests`, `AuthorityMatrixTests`, retry and coordinator tests | One complete Delivery handoff and one Merger completion-owner/real Beads boundary |
| Slot capacity, competing writes, and claim ownership | Pool/coordinator tests | One native guarded claim after evaluation |
| Process creation, inheritable pipe cleanup, job containment, and long paths | Existing focused executor and runtime-file tests | Retained native process/window and published Delivery classifier-force-stop smoke |
| Stop, restart, and preservation | Fast state/worker-observation tests | One second-signal restart case and one published Delivery classifier-force-stop case |
| Cross-pool progress, reservation, and handoff | Planning turn/publication tests and `MergerPoolCoordinatorTests` | `PoolConcurrencyIntegrationTests.DeliveryCompletesWhileMergerPreparationIsHeld` |
| Bead HTTP and workspace safety | Request/authority tests and deterministic observer filesystem fixtures | Native Beads HTTP relation/routing/happy-path cases; junction and Git-admin pointer hazards use strict scripted Git |
| Merge finalization and repository guards | Cheap finalizer and branch-ownership tests | Repeatable owned cleanup, conditional branch deletion, and expected-commit guards |

`MergerPoolIntegrationTests.NativeMergerSynchronizesChecksAndLeavesCompletionToTheCompletionOwner`
uses the continuous runtime's real `CompletedEnvironmentWorker` and shared environment
reservations. Its original 90-second admission deadline covers verified merge, guarded
task closure, cleanup enqueue and pool-capacity release. Before running the worker,
the test requires a queued receipt, the retained ignored artifact and no worktree
removal. It then executes one real worker batch and retains all native assertions
for resolved worktree/tmp absence, conditional branch cleanup, unchanged workflow
fields and retained invocation logs. Admission and worker durations are reported
separately. The former fixture omitted the queue and synchronously ran disposal
inside the pool-completion wait, unlike `ContinuousApplication`; retained failure
and focused reproduction both reached housekeeping after the Agent finished.
The native finalizer recovery case below continues to cover immediate one-shot
housekeeping. No test, deadline or six-thread parallelism is removed or extended.

The native Agent-stage GitHub fixture answers checks-only requests without unused
branch/HEAD probes. Each PR view still obtains fresh real branch, HEAD and remote
main values, now concurrently, and settles all three before returning. It caches
nothing across mutations. Collection weights use the complete 1,243-case
`implement-complete.xml` measurement, including the repaired native Merger path;
the scripted Merger verifier and workspace security cases no longer displace the
longer native collections at startup. These setup/scheduling changes require a
new complete timed run; historical-duration replay is not timing acceptance.

Post-exit observation overlaps its independent status, branch and HEAD reads,
then settles every result before reading fresh tracker state. Independent Merger
verification likewise overlaps its local branch/HEAD/index/rebase-path probes
after the ownership read, settling all before evidence queries or acceptance.
There is no observation cache or overlap across writes. Six controlled component
cases hold the Git responses until every read starts, require no downstream
query before settlement, and retain failure diagnostics and rejection when a
read fails. These cases failed on the serial implementation. The complete timed
suite, with its unchanged six-thread cap, remains the performance gate.

The immutable native Agent-stage template is prepared alongside the existing
independent CLI/trap publishes and Beads template, before collections start.
Previously several of the longest first-wave collections waited on the same
lazy template while occupying test lanes. Every prerequisite still settles on
failure, and tests still receive separate mutable Git/Beads copies. This moves
existing setup work; it adds no cached runtime observation or native scenario.

Observer component cases script both Git root validation and tracker responses,
with exact Git arguments and working directories and real filesystem/backend
guards. They exercise the production projections, budgets, cohorts and HTTP
routes without repeated native launches for an unchanged valid root. The native
observer smoke still uses real Git/Beads and verifies external comments and
dependencies through HTTP. All 17 cases and assertions remain. Collection weights
now use the complete 1,297-case `ns-3i4-prepared-complete.xml` measurement, updated
with the focused observer cost; only scheduling order changes, with six threads.

`MergerOutcomeVerifierTests` keeps its detailed rejection-evidence, local transcript, hosted-job metadata, PR identity, count, and framing matrix on a strict scripted Git fixture. These component cases use stable valid commit identities and temporary log paths; they do not initialize/copy a Git database or start Git. The script matches the production Git arguments and working directory, returns deterministic observations, and fails immediately on an unknown command. The production Merger verifier still parses the real fixture files and makes every acceptance, rejection, identity, and guarded-publication decision; the GitHub and Beads fakes remain process-boundary responses. The ns-3i4 verifier class now discovers 146 cases, including full prompt/document echoes, stale failure frames after passing checks, separately attributable failed reruns, and controlled local-read overlap/settlement. These framing cases reuse the scripted fixture; no additional native journey is introduced. The compact native group below retains the real Git boundary.

`WebBeadWorkspaceBoundaryIntegrationTests.WorkspaceRejectsForeignAndReparseGitAdminPointersBeforeStatusAndRechecksBeforeStatus` and `WorkspaceReportsReparsePointAncestryWithoutFollowingIt` call the production workspace observer against ordinary on-disk repository/worktree metadata and actual Windows junctions. A strict scripted Git runner supplies only the exact worktree-list and common-directory responses and fails on any unexpected command; rejected paths assert that Git status is never requested. The cases retain marker, admin-directory, foreign backlink, reparse backlink, pre-status replacement race, and managed-root ancestry hazards with target/foreign tree snapshots. This removes repeated native Beads initialization and native worktree setup from the security permutations. Native HTTP and Beads coverage remains in `WorkspaceReadsNativeChildrenAndParentWithoutTrackerMutation` and `WorkspaceScrubsHostileAmbientTrackerAndGitRoutingVariables`, preserving relation direction, configured-versus-foreign routing, and a real managed-worktree status read.

The compact native Merger group uses independent copies of one immutable Git template to retain actual branch and HEAD checks, dirty-worktree and unmerged-index detection, active-rebase detection, target-origin mutation during check observation, and reachable/unreachable merge-commit ancestry. Its default-branch fetch request is checked against the validated origin, then mapped to the template's local `main` branch so native Git evaluates the real commit graph without network access. This deliberately reduces native Git process backing across the metadata matrix while preserving the production observation contracts.

Merger finalization retains one native `MergerCompletionFinalizerTests.QaRetainedArtifactsRecoverAfterLostClosureAndLockedTmpRetryAcrossRestart` journey. It exercises the lost authoritative task-close response, completed Product, QA artifacts, an actual locked tmp file, confirmed worktree and branch cleanup, and restart recovery of the persisted tmp retry without replaying completed removals. The duplicate full journey whose worktree-removal response was lost is removed; `CompletedEnvironmentCleanupTests.LostRemovalResponseSurvivesRestartAndResolvesFromFreshAbsenceWithoutReplayingGit` and the existing `CompletedEnvironmentWorkerTests` observation, retry-clock, restart, cancellation, and replay cases retain the cheaper response-loss decisions.

`MergerCompletionFinalizerTests.LocalBranchMovedAfterValidationSurvivesConditionalDeletion` now uses a small real Git repository and a deterministic closed-task `bd list` response. The test moves the owned ref to a real child commit immediately before forwarding the original `git update-ref -d <ref> <expected-head>` compare-and-delete to Git, then verifies that Git rejects deletion and preserves the moved ref. It no longer initializes or queries native Beads, and the production closed/unpaused guard and Git compare-and-delete remain exercised.

The current native cross-pool smoke holds a real Merger claim and singleton reservation before Agent launch while one unrelated native Delivery Implementer completes its `stage:review` claim handoff. It verifies both task worktrees remain on their expected branches, then cancels and drains the still-held Merger without releasing its preparation gate or launching a Merger Agent. Fast Planning coordinator/publication tests retain the removed pause, resume, graph and replay decisions; the standalone native Planning approval/claim-release test, complete Delivery handoff, and native Merger completion remain separate boundaries. The deliberate coverage reduction removes one combined native Planning-publication/interruption/generated-task/Delivery/merge journey, rather than replaying those checks in several expensive integration tests.

The published active-classifier force-stop smoke retains its native Delivery variant as `PublishedContinuousStopIntegrationTests.PublishedRuntimeForceStopsAnActiveDeliveryOutcomeClassifier`. Its Planning-specific native theory row is intentionally removed; fast `PlanningSlotCoordinatorTests.StopBetweenTurnsRetainsStageAndNeverStartsNextTurn` and `PlanningPoolCoordinatorTests` cancellation/stop cases retain Planning stop semantics. The failed hosted attempt reported a Planning reservation without the fixture's `agent.started` handshake within the existing 90-second bound, but did not establish a shared production cause. Removing this native Planning process-level composition is a deliberate coverage/runtime tradeoff, not a production repair. Discovery is expected to fall by one case; the committed positive floor remains 995.

`WindowsConsoleProcess` owns an outer test Job, assigned while the console root is suspended. Disposal terminates that group and waits for retained native process handles within the existing five-second budget before disposable repository deletion. Root exit and zero active Job processes alone do not prove native handle rundown. The logged/unlogged descendant regressions use a native `dolt sql -q 'SELECT SLEEP(30)'` process holding its Dolt `LOCK` after root exit, then require immediate lock deletion after test disposal. The former `bd --readonly --sandbox sql` command is unsupported in embedded mode: it exited with `Error: 'bd sql' is not yet supported in embedded mode` after a brief startup lock, which had falsely looked like a live operation. The fixture now waits for a lock-ready signal, the test retains the Dolt process handle and releases the root, and the native process must remain alive with `LOCK` held until disposal. This test-owned teardown does not add waits or compensating writes to production force-stop.

The ns-j7t native observer smoke now writes an external comment and a blocking dependency before one refresh wait. It requires the same returned envelope to contain both the marker and exact blocker relation with a changed revision. `ExternalMembershipAndCommentsChangesAreReadEvenWithUnchangedInventory` still covers comments-only invalidation at the component level. The already-merged recovery smoke reuses one native setup for failed checks followed by a new successful invocation, each with a distinct invocation ID, environment snapshot and `gh` log. Failure must leave merge evidence, task workflow state, origin main and the deleted remote branch unchanged; the fresh attempt must query the PR and required checks again. The cheap missing/nonpassing-check and current-head verifier tests remain. The deliberate reduction removes separate native comment-only refresh attribution and one repeated recovery fixture start (one theory row); it does not establish a fix for the known cold observer budget failure. The retained test-count floor remains 995.

The ns-e5i reduction removed 138 costly overlapping integration scenarios: the comprehensive parentless/planned pilot variants, held-Merger and crash-recovery journeys, most repeated published stop/restart and dispatch/repair/admission cross-products, and most full Bead/Web request permutations. The resumed-admission and preparation-held Merger boundaries were subsequently restored as focused regressions. Historical incident causes and dispositions remain tracked in Beads; removing coverage does not establish a causal repair.

Historical ns-e5i baseline: it discovered and passed 607 tests with no skips in 179.366 seconds, including the normal solution build, native fixture publishing, test execution, and teardown. Four focused Git cases covered short/deep pilot rebases and preservation of interrupted merge/apply rebases. One native resumed-admission regression covered dependency, capacity, and restart with explicit fixture priorities; it launched no Agent process. That earlier pool-concurrency regression held Merger preparation before its Agent clock started and then traversed native Implementer/Reviewer/QA progress and both merge closures. The current cross-pool smoke has the narrower boundary documented above.

Planning execution adds fast component cases for exact approval identity, grounding revalidation, coverage and dependency structure, evidenced assumption repair, questions, classification failure, configured budgets and guarded pause eligibility. One native Planner/Challenger smoke verifies the real Agent/Beads boundary and retained approved environment without creating tasks. The ns-47f.2 branch suite passed 698 tests with no skips in 112.48 seconds wall time, including build, fixture publishing and teardown; this is branch validation, not a timing measurement of a later main.

The ns-47f.2 CI repair refreshes collection weights from run 43509330048 while retaining all cases and the six-thread cap. PID readiness in the crash-containment test waits for a complete positive PID after writer closure within its original timeout; two cheap cases hold an exclusive or read-sharing writer open to cover both sharing violations and partial digits. Native crash containment remains exercised.

The second-signal restart smoke proves startup recovery with a new live Agent and retained dirty worktree, then requests graceful drain before releasing that Agent. This isolates its final pause/claim handoff from background discovery against embedded Dolt. It still verifies both original console signals, native process termination, recovered runtime/Agent identity, exact branch and sentinel preservation, the guarded pause, and the untouched foreign claim. Failed slot release fails immediately rather than consuming the recovery polling deadline. CI retains failed manual Delivery fixture roots alongside console artifacts for diagnosis. No production timeout or suite parallelism changes are involved.
Historical graph-publication rollout: fast fault-injection cases were added at every create/link/release boundary, along with failed and lost write responses, conflicting or foreign content, immutable executing graphs and Resume-driven Challenger revalidation. Discovery attention preserved completed and foreign-owned tasks without changing claims or repair counters. At that point, a native Planning smoke published a two-task graph, verified native dependency blocking, and drove the first task through Delivery; the current native cross-pool boundary is documented above.

The ns-47f.3 hosted runtime repair refreshes collection weights from Windows CI run 43529751486. The expanded native Planning publication-to-Delivery smoke measured 149.822 seconds while its old weight was 31.025, leaving it late in the schedule. It now starts with the longest collections. All 867 cases, fixture isolation and the six-thread cap remain intact; CI requires the current positive minimum of 870 tests after the three follow-up authority regressions. Weight refreshes only change collection start order and must still pass the complete timed suite; replaying historical durations is diagnostic evidence, not runtime acceptance.

The follow-up runtime repair shares one fresh Product/discussion/grounding observation per publication authority check, removing duplicate bd reads without caching across mutations. Tracker observations follow Git grounding reads so ownership, status and pauses changed during grounding stop the next write. Three cheap regressions cover these changes (870 total); the original 867 cases remain. The initial weight-only repair passed locally but still exceeded hosted runtime and encountered a restart-smoke timeout, so local timing and historical replays alone did not establish hosted acceptance.

After synchronization with main, historical hosted run 43535447629 passed all 905 cases but took 303.759 seconds. Its native Planning collection measured 255.284 seconds and now receives the highest scheduling weight. Independent read-only Git grounding queries overlap within each fresh observation; all settle before tracker reads and authority checks. A controlled regression fails on serial grounding and verifies overlap plus tracker ordering. All 905 synchronized cases remain, with this one additional case; six-thread test scheduling remains unchanged. The 300-second performance gate applies to the complete local latest-main suite; hosted elapsed time is diagnostic.

Historical runtime note: the overlap/scheduling change passed 906 cases locally in 177.562 seconds, while hosted run 43545299874 took 330.304 seconds and its native Planning smoke consumed 282.648 seconds. That earlier smoke stopped after the generated-task Implementer published `stage:review`; its Planner/Challenger approval, graph publication, dependency blocking, generated-task admission and implementation coverage were later split across cheaper tests and the representative native boundaries listed above. It did not repeat Reviewer and QA turns already covered by `DeliverySlotKeepsOneClaimAndPersistentWorktreeThroughMergeHandoff`.

Historical coverage note: an earlier `PoolConcurrencyIntegrationTests` version combined graph publication with one generated Implementer. Its native graph, dependency and Planning-turn assertions are no longer part of the current cross-pool smoke; fast Planning publication/coordinator tests retain those decisions.

Historical multi-pool journey (superseded by the current boundary above): the smoke once held Planning preparation, published a generated task, ran its Implementer, and completed a real merge while preserving the generated task's `stage:review` handoff. The dedicated `AgentStageDeliveryIntegrationTests.DeliverySlotKeepsOneClaimAndPersistentWorktreeThroughMergeHandoff` continues to cover the full Implementer/Reviewer/QA journey, and `MergerPoolCoordinatorTests.EqualPriorityCandidatesUseFifoAndTheReservationRemainsCapacityOne` retains successor reservation and capacity detail. The current native smoke intentionally no longer combines Planning publication with concurrent Delivery and merge.

Stream-cleanup readiness reports are published by writing and closing a temporary JSON file before atomic rename. Windows observers can still encounter an incompatible open handle on the published path. `StreamReadinessTests` holds exclusive and read-sharing writers open, verifies recovery after closure, and verifies that persistent sharing expires within the original deadline. The coordinator retries only JSON incompleteness and Windows sharing/lock violations (32/33); other IO failures propagate. The native blocked-drain and launch-burst cases remain retained. Planning authority and graph reads are batched as described in [Planning evidence](../docs/agents/planning-evidence.md), retaining fresh checks across writes and all existing fault-injection and native publication cases. No timeout, coverage or parallelism reduction is involved.

Lifecycle readiness obtains both unassigned-open and exact-owned-in-progress memberships from one unlimited native `bd list --ready --status open,in_progress` read. Native Beads still decides blockers and deferment. Every returned row is checked against the fresh complete inventory before ownership selection and the publication gate; a mismatched pair is refreshed once, and repeated disagreement fails closed. The real inventory fixture compares both memberships with the former separate commands across blocked, deferred, foreign, assigned, closed, gate and more-than-50-row controls. Component tests retain tracker failure, stale-pair refresh, competing claims and interstage mutation coverage. Collection weights use the complete local `repair-recovery-isolated.xml` run (1,207 passing tests), including operational observations and measured worktree cost. All cases, deadlines and the six-thread cap remain unchanged; weights and filtered checks do not establish the five-minute gate.


The ns-mbd.5 timing follow-up overlaps the independent CLI publish, standalone
AgentCliTrap publish and isolated Beads-template initialization before any test
collection starts. The existing CLI project build gate remains, all prerequisites
settle even on failure, and trap checks still run before the fixture is exposed.
This changes setup order only: no test, native boundary, deadline or six-thread
limit is removed. Collection weights use the 1,230-case merged-main measurement,
including previously unweighted native collections. A fast setup probe improved
from 9.817s to 8.055s; that diagnostic and scheduling replay are not complete-suite
or latest-main acceptance. The complete timed command above remains the gate.

Pool waiting and capacity telemetry adds controlled-clock coordinator/helper cases
and one in-process OTLP wire/privacy check. Existing native
`PoolConcurrencyIntegrationTests` retain the process/tool boundary; no native
journey, additional tracker command or scheduling change is added. See
[pool observation semantics](../docs/design/pool-observation-telemetry.md).

Background lifecycle intent adds cheap store/transition/command-parser/signal fixtures
for durable absent-owner stop, lost-response crash points, pending reservations,
maintenance token/revision release, intervening stop/disable, command-lock contention,
failed-write interlocks and direct-stop versus internal-failure reasons. The second
signal is checked while persistence is deliberately blocked. These add no native
runtime, Agent, scheduler or publish journey; existing control-pipe tests retain the
peer deadline/disconnect boundary and existing background/native hosting coverage remains.

Package replacement fixtures use disposable paired packages and injected
interruption callbacks before/after backup, write, retirement and confirmation.
They check complete rollback bytes, pending mixed payload, retained suppression,
operator sentinels, sharing denial, source changes and pinned path replacement.
Two cases reuse the shared Git template and create one disposable worktree each
to compare actual `git worktree list --porcelain` before and after success or
interruption. Injected Git output cannot prove preservation of a real registration.
This adds no owner, Agent, fixture publish or browser journey. Other detailed
cases stay in file/component fixtures. Unchanged payload files avoid redundant
writes and journal revisions; normal runtime paths do not call this component.

Attention base publication adds two deterministic observer cases. They check
history-free membership, closed and deleted members, and selected explanation
completion with 65 unrelated pauses. Studio model cases check changed update
time, stage, pause labels, repair count, source health and action eligibility.
They add no native journey or fixture publication. Browser checks extend the
existing Studio and Overview journey. Production adds one inventory-only base
projection and defers selected explanations to the existing shared history cache.

Planning creation safety adds controlled-runner cases for intent confirmation,
pre-launch authority, lost responses, late child completion, publisher restart,
malformed or duplicate evidence, legacy task recovery and same-process overlap.
Coordinator coverage checks a changed Product version during provisioning with a
reused assignee. These cases add no native journey or fixture publication.
`UpgradeLifecycleTests.CompetingProcessLeavesFreshHostRootUnchanged` proves native
process exclusion at the same repository path. The native guarded metadata test
proves file transport and metadata merging. The approved-stage publication and
cross-Project/host-root alias scenarios in `DispatchScenarioSupport` have no test
wrappers in the retained suite. Full native intent publication, abrupt owner death
with a late child, and alias/restart recovery remain proof for the `native-proof`
slice. This change adds no native journey. Each newly created task adds two guarded metadata writes, confirmation
reads and fresh authority observations. Recovery never uses a zero-match intent as
permission to repeat a create. Six-thread scheduling and deadlines stay unchanged.
