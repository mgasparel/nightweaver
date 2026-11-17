# QA AppHost

## Observation cost investigation

For the implemented shared observer, install the Playwright tools below and start
a fresh exact isolated AppHost. After `aspire wait web`, use `aspire describe
--format Json --non-interactive --apphost ./Nightweaver.QA.AppHost.csproj` to set
`NIGHTWEAVER_QA_URL` from the direct `web` HTTP endpoint and
`NIGHTWEAVER_QA_CONFIG` from that resource's `Nightweaver__ConfigPath` environment
value. From this directory run `node operational-observer-qa.mjs <evidence.json>`
and stop that exact AppHost in `finally`.

The probe validates disposable fixture identity before importing 50/2,000 Beads,
then observes external comments, dependencies, readiness and catch-up checkpoint
changes through the real versioned resources in a browser. It also measures
native runtime completion visibility. Its recorded CLI commands are external
fixture preparation/mutations, not observer command counts; the native
`OperationalObservationSourceTests` smoke records collector commands and cost.
See [observer acceptance](../../docs/acceptance/operational-observer.md) for actual
timings and target limitations. This does not test the SSE/browser coordinator
owned by ns-mbd.3.

`observation-cost.py` is a disposable native CLI/HTTP probe for ns-mbd.8, not a
production observer or a UI mutation script. Requires Python 3. Start a **fresh**
isolated exact AppHost as below, wait for `web`, then from the repository root run:

```powershell
try {
    aspire start --isolated --non-interactive --apphost tests/Nightweaver.QA.AppHost/Nightweaver.QA.AppHost.csproj
    aspire wait web --non-interactive --apphost tests/Nightweaver.QA.AppHost/Nightweaver.QA.AppHost.csproj
    python tests/Nightweaver.QA.AppHost/observation-cost.py --output docs/design/observation-cost-results.json
    if ($LASTEXITCODE -ne 0) { throw 'Observation probe failed' }
} finally {
    aspire stop --non-interactive --apphost tests/Nightweaver.QA.AppHost/Nightweaver.QA.AppHost.csproj
}
```

The probe discovers this exact AppHost's direct HTTP URL and disposable tracker
through `aspire describe`, validates their fixture identity, scrubs ambient
tracker routing, then uses public CLI import to seed 50/2,000 Beads. It measures
three serial collection cycles, all paused histories, selected Workspace reads,
legacy HTTP projections, and comment/dependency/readiness change visibility
through a real HTTP response. Output contains no configuration paths or secrets.
The one-second phase delay and extra legacy HTTP read are deliberate conservative
costs; this does not claim a production observer, SSE or browser-rendering result.
No browser UI changed in this investigation. See the
[v1 contract and measurement interpretation](../../docs/design/operational-observations-v1.md).

## Studio default console

`node source-freshness-qa.mjs` exercises the real native collector on a fresh
exact isolated default stack. Set the discovered URL and config as below. It
retains one disposable paused Bead, watches its actual Attention/Workspace
responses and rendered statuses for at least 130 seconds across two real source
audits and the 120-second age boundary, then
verifies an external comments-only edit reaches the selected Workspace. No responses
or health metadata are substituted. Evidence and 1440/375/720 captures go to
`TestResults/source-freshness` or `NIGHTWEAVER_QA_OUTPUT`. Stop the exact stack
in `finally`, including on failure.
The probe also navigates to Overview and watches Digest checkpoint freshness
while the previous Workspace interest remains within its disconnect grace.

`node status-stability-qa.mjs` checks the shared Studio status lifecycle against
the exact isolated stack. It injects frequent version notifications and delays
real factory replacement responses to catch stale/current flashing, then checks
authoritative stale health and disconnection still appear. Screenshots at
1440/375/720 pixels and JSON evidence go to `TestResults/status-stability` or
`NIGHTWEAVER_QA_OUTPUT`. Discover `NIGHTWEAVER_QA_URL` as below and stop the same
AppHost in `finally`.

`node studio-explorer-qa.mjs` verifies Studio Explorer and current-project header search against a fresh isolated default fixture. Set the exact discovered URL and config as below. The probe imports 81 disposable Beads, verifies native external title/dependency changes, all views, bounded pages, Back/Forward/reload, focused-row removal on a still-valid page without URL changes, surviving-row focus, page removal and focus, Workspace query context, deferred cross-project controls and stale retained results. Captures at 1440/375/720 CSS pixels and results go to `TestResults/studio-explorer` (or `NIGHTWEAVER_QA_OUTPUT`). Stop the same AppHost in `finally`.

`node runtime-refresh-qa.mjs` verifies Delivery, Overview and Workspace Output
against the same exact isolated stack. Accepted reconciliation responses and
successful factory reads with unchanged `lastAttemptAt` reproduce a stalled
collector. The runtime action stays enabled across polls, an explicit retry
issues a second POST, and concurrent clicks share that pending POST. Captures
at 1440/375/720 CSS pixels and results go to `NIGHTWEAVER_QA_OUTPUT` or
`TestResults/runtime-refresh`. Stop the exact AppHost in `finally`.

Start a fresh isolated stack with `NIGHTWEAVER_QA_SCENARIO=cleanup` for
`node planning-readiness-qa.mjs --studio` and `node cleanup-qa.mjs --studio`.
Use the exact direct endpoint below and stop the same AppHost in `finally`.
The Product probe checks retained drafts during observations/navigation, server
validation, ambiguous disconnect with no replay, real creation, stale readiness
409 and explicit persisted authorization. The cleanup probe sends real same-origin
stale and eligible retries to the durable owner inbox, verifies the fifth attempt,
actual absence, unchanged Bead and protected paused evidence, then substitutes
only reads for bounded pagination/search, stopped/unavailable owners and denials.
Both capture 1440/375/720 CSS-pixel layouts and browser errors under
`TestResults/studio-product` and `TestResults/studio-cleanup` (or
`NIGHTWEAVER_QA_OUTPUT`). The documented entry points now delegate to Studio; `--studio` remains harmless compatibility syntax.

`node studio-attention-qa.mjs` verifies the live Studio Attention and dedicated
Bead Workspace slice on a fresh isolated default fixture. Set both
`NIGHTWEAVER_QA_URL` and `NIGHTWEAVER_QA_CONFIG` from the exact direct `web`
resource discovered below. The probe validates fixture identity, creates its own
disposable paused Beads, observes external escalation/comment/resolution while
typing, reconnects the real stream, and sends real same-origin Save Note,
Resume, stale 409, Retry and two-step Abort requests. Deterministic read
projections cover pinned active evidence, bounded history, sanitized Markdown,
focused disclosure refresh/removal, stale, unavailable and empty states.
Screenshots at 1440/375/720 CSS pixels and JSON evidence go to
`NIGHTWEAVER_QA_OUTPUT` or `TestResults/studio-attention`. Review the captures and
stop the exact AppHost in `finally`, including on failure.

The Attention probe also records the last 64 existing Attention resource responses
in `attention-observations.json`, including source health and attempt times. This
postmortem capture adds no requests and does not extend convergence deadlines.

`node studio-presentation-qa.mjs` also verifies grouped Workspace relations with
43 children and mixed kinds/directions: a hard 15-row bound per group, exact links,
total/range, keyboard Previous/Next, independently retained pages, refresh and
shrink/removal/regrowth clamping, and focus recovery at boundaries or removed
rows/groups. It runs in the isolated Presentation group and captures desktop,
375px and 720 CSS-pixel layouts without external evidence requests or commands.

`node refresh-timeout-qa.mjs` holds real reconciliation 202 responses while the
operational stream and resource reads continue. It verifies deadline abort,
visible failure at 375px, a second explicit POST, replacement deduplication after
late settlement, and no automatic replay or page errors. Screenshots go to
`NIGHTWEAVER_QA_OUTPUT` or `TestResults/refresh-timeout`. Use the same exact-stack
start, direct endpoint discovery and finally-stop lifecycle below.

After discovering the exact isolated direct endpoint, `node read-timeout-qa.mjs`
first blocks operational responses through the 25-second cold-start silence
deadline, verifies Disconnected across retries, then restores real handshake/read
recovery. It holds a real resource response while the operational SSE remains live
and checks the ten-second abort, retained recent data/read error at 375px, a successful explicit
Refresh, wake recovery, a replacement read and rejection of the late held response.
The probe never replays commands. Screenshots go to `NIGHTWEAVER_QA_OUTPUT` or
`TestResults/read-timeout`. Stop the same AppHost in `finally` after all probes.

For integrated cutover QA, run `./tests/Nightweaver.QA.AppHost/studio-cutover.ps1`
from the repository root after the npm setup below. Repeat with `-Group Commands`,
`-Group Invocation`, `-Group Recovery`, and `-Group Presentation`. Each group creates its own isolated
fixture, waits for readiness, discovers the exact direct URL/config, records probe
logs/screenshots in `TestResults/studio-cutover`, and stops that exact AppHost in
`finally`, including on failure. Pass `-OutputDirectory <fresh-directory>` to
preserve earlier evidence when repeating a group. Recovery restarts only the disposable web resource
and simulates page suspension/wake while preserving two identity-bound drafts.
This is browser lifecycle coverage, not a physical OS sleep test.

On a fresh isolated default fixture, use the discovered direct `web` HTTP URL below and run `node studio-qa.mjs` and `node smoke.mjs`. Studio checks the default root entry, compatible `/studio` entry and `/projects/...` routes. The old root HTML/JS/CSS is retired. Reference and candidate screenshots at 1440, 375 and 720 CSS pixels are saved under `TestResults/studio`. Checks include direct reload, Back/Forward, URL preferences, legacy Bead translation (including dotted hierarchical IDs reloaded with and without Workspace query state), missing-asset/API 404s, keyboard/focus, unavailable project controls, no mutations and no browser errors. Review the screenshots visually and stop the same exact AppHost afterward.

Requires .NET 10, Aspire CLI, Git, `bd`, Node.js and npm. From this directory, install the browser tools once:

```powershell
npm ci
npx playwright install chromium
```

## Start the isolated stack

Start a fresh isolated stack and discover the dynamic `web` URL:

```powershell
aspire start --isolated --non-interactive
aspire wait web --non-interactive
aspire describe --format Json --non-interactive
```

Use the `url` of the `http` endpoint on the `web` resource from `aspire describe` for both the smoke check and browser QA. The endpoint is dynamic; do not construct or assume its port. The AppHost exposes Kestrel's direct local HTTP binding with `isProxied: false`. Nightweaver's mutation-origin policy checks that the browser's origin matches the local request binding. A DCP proxy URL uses a different public port, so a page served through that URL cannot pass the binding check for its POST actions.

Set `NIGHTWEAVER_QA_URL` to the returned endpoint URL and run the connectivity smoke check:

```powershell
$env:NIGHTWEAVER_QA_URL = '<web http endpoint URL from aspire describe>'
node smoke.mjs
node telemetry.mjs
```

The isolated AppHost starts three disposable native `AgentInvocationFixture` processes through Nightweaver's invocation preparer, telemetry publisher, and executor. Each writes a distinct marker and waits for a disposable release file, so `telemetry.mjs` can inspect the live output and then observe the actual completion. The browser script interrupts the first EventSource once, verifies the reconnect's `Last-Event-ID` and duplicate-free output from the real endpoint, exercises pause/resume-follow/jump-to-latest, checks completion and retry cessation, and tests Escape/focus at a 375px viewport and at a 640 CSS-pixel viewport with device scale factor 2 (the effective layout at 200% zoom on a 1280px desktop). It also checks stale availability while retaining the last validated occupied snapshot. The script writes both release files in its `finally` block; AppHost teardown releases them again defensively, settles the native processes, and removes the disposable repository, worktrees, logs, runtime snapshot, and processes.

## Playwright mutation QA

The disposable tracker is seeded with separate attention items for the browser actions:

- `QA product decision` for guidance and resume (`Send & Resume`).
- `QA needs human` for `Reset Budget & Retry`.
- `QA needs human abort` for the two-step `Abort & Close` confirmation.

Use headless Playwright on the same URL from `aspire describe`. Exercise each action in the page so the browser sends its same-origin headers; do not substitute `APIRequestContext` for these mutation checks. Confirm that each POST succeeds and that the page/tracker reflects the resulting state: the decision card and retry card resolve, and the abort item closes. Capture `pageerror` and console error events as in `smoke.mjs`, and close the browser in a `finally` block.

Run `node workspace-qa.mjs` (the Studio Attention/Workspace probe) for guarded tracker actions and `node telemetry.mjs` separately for the native invocation stream.

Run `node planning-readiness-qa.mjs` against a fresh default fixture to check the Product backlog control, a stale 409 request, explicit Ready for Planning, persisted readiness after reload, and the 375px layout. It saves desktop/mobile screenshots under `TestResults`. This authorizes only the disposable Product; ordinary continuous scheduling still requires a live repository owner.

## Compact lineage thread QA

Run `node digest-threads-qa.mjs` on the discovered direct endpoint for Studio. Optional `--baseline` retains the legacy console comparison. Baseline mode serves the pre-change 855c2b1 JS/CSS from Git. The identical long-title fixture initially shows five threads, with one, four and eight child cases and mixed completed/open activity. It retains section/viewport screenshots and measured heights under `TestResults/digest-threads`. Candidate checks cover zero default child rows/descriptions, independent keyboard activity controls, preview/all access, refresh/focus, newly added collapsed threads, unique disclosure IDs, real root Workspace navigation, and 375/720 CSS px reflow. Review screenshots for readability; always stop the exact isolated stack in finally.

The refresh regression expands and focuses the fifth thread before prepending new activity. Its original control must remain rendered, focused and expanded when moved past the initial bound; the incoming thread stays collapsed. The full collection becomes visible, and Show fewer restores the five-thread preview. Set `NIGHTWEAVER_QA_OUTPUT` to a separate directory to preserve earlier evidence.

## Digest disclosure QA

Run `node digest-qa.mjs` against a fresh default fixture using the same discovered direct endpoint. Set `NIGHTWEAVER_QA_CONFIG` from that exact web resource as well. The probe targets the Studio Catch-up route, verifies external tracker updates and holds a real checkpoint POST while creating concurrent activity. It first saves a real checkpoint through the UI and independently reads persistence. Deterministic digest response fixtures then exercise long histories (5 threads / 4 children / 6 standalone initial bounds), summary disclosure, keyboard focus across refresh and removed items, window selection, retained data and save failures, and newer concurrent activity after catch-up. Screenshots under `TestResults/digest` cover busy, empty, caught-up and error states at desktop, plus compact and expanded long content at 375px and 720 CSS px. Only deterministic digest resource responses and later save-failure responses are substituted; the Studio shell, shared coordinator and initial checkpoint POST use the running isolated host. Expected HTTP error fixtures may emit network console errors; uncaught page errors and other console errors fail the check. Always stop the same AppHost in `finally` afterward.

Representative evidence: [desktop compact](../../docs/acceptance/digest/busy-desktop.png), [375px compact](../../docs/acceptance/digest/busy-375.png), [720px expanded](../../docs/acceptance/digest/expanded-720.png), [caught up](../../docs/acceptance/digest/caught-up-desktop.png), [empty](../../docs/acceptance/digest/empty-desktop.png), and [retained-data error](../../docs/acceptance/digest/error-desktop.png).

## Execution bay QA

Run `node factory-qa.mjs` against the discovered direct endpoint of a fresh isolated stack. The real host supplies the shell and initial runtime identity; deterministic Factory responses exercise busy, idle, disabled, preparing, failed/timed-out, paused, stale and disconnected observations. The script checks observed occupancy, unavailable metadata, Bead Workspace links, live/retained invocation query identities, changed two-second polling, disclosure/focus preservation and reset on runtime replacement. Configured model never supplies the actual model. A failed invocation remains prominent while its slot settles.

Factory section and viewport screenshots under `TestResults/factory` cover 1440px, 375px and 720 CSS px, including long identifiers and deep Windows paths with diagnostics expanded. The script requires no page overflow or uncaught browser errors. Run `telemetry.mjs` on the same fresh stack for the native invocation stream smoke, then stop that exact AppHost in `finally`, including on failure. Factory fixtures do not authorize tracker actions or simulate native execution evidence.

Representative execution bay evidence is retained under [docs/acceptance/factory](../../docs/acceptance/factory).

The Release-shaped idle regression retains completed and failed invocations while clearing current Bead/role/stage/path/repair/diff metadata. It checks both changed running-to-idle polls and initial idle loads: compact idle occupancy, no diagnostic dump, surviving button focus, retained Bead identity and exact runtime/slot/invocation output. Idle retained actions are also exercised at 1440px, 375px and 720 CSS px. Set `NIGHTWEAVER_QA_OUTPUT` to a separate output directory to preserve screenshots from earlier runs.

Retained idle evidence: [1440px](../../docs/acceptance/factory/idle-retained-1440.png), [375px](../../docs/acceptance/factory/idle-retained-375.png), and [720 CSS px](../../docs/acceptance/factory/idle-retained-720.png).

## Runtime health and invocation inspection QA

Run `node invocation-qa.mjs` against the discovered direct endpoint. Deterministic Factory observations and stream events exercise the five health presentations, initial unavailable/stopped observations, exact invocation identity across replacement polls, preserved output/follow/scroll/focus, UTF-8 byte and line bounds, ANSI/control sanitization, cursor de-duplication, reset, terminal status, keyboard containment, Escape/Close restoration and hash navigation to the Full Bead Workspace. The native endpoint and EventSource reconnect remain covered separately by `node telemetry.mjs`; run both, plus `node factory-qa.mjs`, on a fresh isolated stack and stop that same stack in `finally`.

Screenshots under `TestResults/invocation` show long metadata and retained output at 1440×900, 375px and 720 CSS px, including the scrolled output/follow toolbar and live/idle/stale/disconnected/unavailable health. Review readability and reachable controls as well as the asserted absence of page overflow and uncaught browser errors. Representative evidence is retained under [docs/acceptance/invocation](../../docs/acceptance/invocation).

## Manual continuous-runtime pilot

The native second-console-signal test waits for the resumed Delivery slot's capacity-release log before reading its persisted pause. This avoids repeated embedded-tracker subprocess reads during recovery while still checking the saved status, labels, retained worktree and foreign claim within the original two-minute recovery budget. Pilot tool cancellation kills and settles the child before returning; failures include runtime output in the test report.

The assembly's real-tool prerequisite setup prepares the shared Beads template before parallel test collections start, so its one-time `bd init` does not compete with active native turns. Individual tests still receive isolated copies and run with the committed six-thread scheduling. The cancellation regression uses the native AgentInvocationFixture probe directly, without a PowerShell script or script execution-policy dependency.

The cross-pool concurrency smoke retains a full native Delivery and first native merge, verifies that the held singleton does not block Delivery, and observes the queued successor's reservation after release. It stops that successor at the pool boundary rather than repeating a second native merge. Native merge completion remains covered by the first slot and the dedicated Merger integration tests.

The real-repository proposal, observed local Codex settings, proposed Project JSON, success criteria, and abort conditions are documented in [the manual Delivery pilot proposal](../../../docs/acceptance/manual-delivery-pilot.md). It is a proposal only; this QA AppHost continues to use disposable repositories and fixture Agent/CI aliases.

The manual Delivery scenario starts the actual Nightweaver continuous CLI against a disposable Beads tracker and bare Git origin. Web and the CLI share the fixture's nested Nightweaver root and project config. Native Agent invocations pause at fixture gates so the browser can inspect live output and process identity before releasing them. The integrated flow includes real automatic reviewer rejections through the configured repair limit (with `max_repair_cycles=2`, two repair attempts follow the initial attempt and the third rejected review pauses at persisted `repair_cycles=3`), a task created after the runtime becomes idle, and a late Delivery task while the singleton Merger is held.

Start the isolated AppHost with the manual scenario selected, then use the dynamic `web` HTTP endpoint reported by `aspire describe`:

```powershell
$env:NIGHTWEAVER_QA_SCENARIO = 'manual-delivery'
aspire start --isolated --non-interactive --apphost .\Nightweaver.QA.AppHost.csproj
aspire wait web --non-interactive
aspire describe --format Json --non-interactive
$env:NIGHTWEAVER_QA_URL = '<web http endpoint URL from aspire describe>'
node manual-delivery-qa.mjs
```

The Playwright script verifies the live factory snapshot, opens the gated invocation stream, disconnects the browser while the native Agent remains held, and confirms that the runtime identity and process continue. It submits a deliberately stale same-origin Retry snapshot and verifies the 409 guard leaves the Bead unchanged, then verifies real `Send & Resume` and `Reset Budget & Retry` responses. After the retry launches a native Agent, the script force-crashes only the isolated runtime process tree, checks HTTP stale telemetry, retained runtime identity, worktree branch/sentinel bytes, and a foreign-owned claim, then requests a new runtime and verifies a changed HTTP runtime identity and PID. Startup recovery may release the interrupted claim before ordinary discovery re-admits the task; the script waits for a new native Agent, releases its gate, and verifies the dirty worktree is paused with the recorded worktree-cleanliness reason. It records the sentinel before explicitly deleting that disposable file, then uses the real same-origin Retry UI action to complete and merge the clean task. It retries only the documented transient 409 returned when a Bead changes during its workspace read, with a bounded consecutive-conflict limit. The script releases gates in `finally`; stop the same AppHost afterward using the cleanup command below.

Run scripts from this directory so `playwright` resolves from its local installation. The `web-browser-logs` dashboard resource captures logs from its own tracked browser session, not arbitrary Playwright sessions.

## Beads Explorer QA

`node explorer-qa.mjs` now delegates to `studio-explorer-qa.mjs`. The replacement uses the shared versioned resource and a native 81-row disposable inventory. Search, all five views, bounded pages, external edits/dependencies/readiness, direct links, Back/Forward and focus/removal are covered. Product creation and created-ID discovery are covered separately by `studio-product-qa.mjs`. See the [cutover parity matrix](../../docs/acceptance/studio-cutover.md).

## Integrated console polish QA

`node console-qa.mjs` now delegates to `studio-qa.mjs`; legacy DOM probes are retired. Run `studio-cutover.ps1` and its Commands, Invocation, Recovery and Presentation groups for the integrated Studio matrix. The [cutover report](../../docs/acceptance/studio-cutover.md) maps every former surface to replacement browser/component/native coverage and reviewed screenshots.

## Schema message QA

`node schema-messages-qa.mjs` now delegates to `studio-presentation-qa.mjs`, also run once by `studio-cutover.ps1 -Group Presentation`. The representative read projection checks structured/fallback/original evidence, safe keyboard links, disclosure focus, raw source literals, partial relations and responsive geometry in the actual Studio shell. Detailed schema/source/provenance decisions remain covered by `CommentPresentationTests`, `AuditMessagePresentationTests` and `BeadPresentationTests`; the browser no longer repeats that combinatorial native matrix.

## Operational protocol ordering

For the implemented SSE/coordinator browser smoke, start a fresh exact isolated
AppHost as above. Set `NIGHTWEAVER_QA_URL` from the direct `web` HTTP endpoint and
`NIGHTWEAVER_QA_CONFIG` from that resource's `Nightweaver__ConfigPath` environment.
From this directory run `node operations-qa.mjs`, then `node studio-qa.mjs` and
`node smoke.mjs`. The operations probe validates disposable fixture identity,
creates one external paused Bead, deliberately misses invalidations, verifies
count convergence, forces real stream replacement, and checks manual refresh,
one connection per tab and no replayed commands. It records screenshots/results
under `TestResults/operations`. Review desktop/375/720 screenshots and stop the
same exact AppHost in `finally`. Frontend deterministic tests: `npm test` in
`src/Nightweaver.Web/Studio`; see [synchronization](../../docs/design/operational-synchronization.md).

For the proposed operational protocol's eviction/reconnect ordering rules, run
`python tests/Nightweaver.QA.AppHost/observation-ordering.py` from the repository
root. This disposable model uses no AppHost, tracker or network. It covers TTL
and LRU readmission with client state retained, changed/unchanged source data,
late responses/collectors, equal-revision reconnects, epoch replacement and
counter exhaustion. See [the v1.1 contract amendment](../../docs/design/operational-observations-v1.md#eviction-continuity-conformance).
It is protocol analysis; backend and coordinator consumers still need to run
these cases against their actual implementations.

## Cleanup

`digest-threads-qa.mjs` also prepends activity while the fourth child and sixth
standalone summary controls are focused. It checks the original DOM node,
expanded summary and viewport position, retention after moving focus away,
bounded standalone defaults and the fallback after actual item removal. Captures
include `focused-child-boundary.png` and `focused-standalone-boundary.png`.

Always stop the same AppHost in a cleanup/finally step, including after a failed browser check:

```powershell
aspire stop --non-interactive
```

Each run creates a disposable Git repository and Beads tracker with sample issues. UI writes affect that fixture; stopping the AppHost removes it. The fixture is not a copy of the user's tracker. Do not use desktop Computer Use (`cua_repl/js`) to drive browser QA.

## Housekeeping acceptance

Run `node cleanup-qa.mjs` against the discovered direct `web` endpoint of a fresh isolated stack started with `NIGHTWEAVER_QA_SCENARIO=cleanup`, and stop that exact AppHost in `finally`. The fixture retains a native locked tmp report through four bounded attempts with injected time, reconstructs the cleanup worker from durable evidence, and waits for real same-origin browser Retry. The script checks stale/protected 409s, queue acceptance versus confirmed resolution, a fifth attempt, actual tmp absence, unchanged delivered Bead and preserved paused evidence. Existing native full-suite cleanup smokes additionally cover retained ignored worktree output, verified merge completion, Git registration absence and restart after lost responses.

Deterministic read fixtures then verify all four states, expired owner acknowledgement, eight-row pagination/search over 27 obligations, disclosure/focus preservation, unavailable counts and no overflow/errors at 1440px, 375px and 720 CSS px. Screenshots are written under `TestResults/cleanup` (override `NIGHTWEAVER_QA_OUTPUT` to preserve prior evidence). Only those later reads are substituted; the Retry acceptance uses the real endpoint and atomic runtime inbox. The disposable fixture includes a protected paused tmp sentinel and never changes production data.

Representative checks and reviewed screenshots are retained in [the Housekeeping acceptance report](../../docs/acceptance/cleanup.md).

## Structured evidence navigation QA

Run `node studio-evidence-qa.mjs` with both the exact discovered direct URL and
config from a fresh isolated default stack, or use the Presentation group in
`studio-cutover.ps1`. The probe validates fixture identity, seeds native merger
rejection/verified-merge and Planning approval records through its disposable
tracker, checks the actual server projections and exercises safe bare evidence
links plus keyboard navigation to old candidate/review/source comments. It also
checks repeat activation, focus/disclosures across a real external comment,
unresolved sources, unsafe/credential URLs, no automatic external requests or
commands, and 1440/375/720 screenshots. Stop the exact AppHost in `finally`.

## Native relations and Type QA

`node relations-qa.mjs` now delegates to the representative `studio-presentation-qa.mjs` smoke. It checks bounded typed relation links, directional unavailable state, safe literal text and 1440/375/720 reflow. Native hierarchy/blocking/lineage and partial-source semantics remain in `WorkspaceRelationsWebTests`, `WebBeadWorkspaceIntegrationTests` and presentation tests. Studio Attention additionally checks incremental relation disclosure with long history.

## Bead roles and tracked workflow QA

`node workflow-qa.mjs` now delegates to `studio-presentation-qa.mjs`. The Workspace displays the existing server-projected role, tracked stage, Planning/publication state, ownership, repair facts and diagnostic with raw source metadata in a collapsed disclosure. The browser checks visible facts and focus; `BeadPresentationTests` retains the detailed stage/type/publication/repair/provenance matrix. Neither displayed role nor tracker ownership claims active execution.

## Continuous Planning observations

On a fresh default isolated stack, run `node planning-telemetry-qa.mjs` against the exact discovered direct HTTP endpoint. The third native invocation uses the Planner preparer/purpose and writes `planning-native` into its real retained log. The script verifies actual PID/runtime/invocation identity, exact Planning stream selection, keyboard/focus, 1440/720/375px layouts and actual completion to idle. Only stale and legacy snapshots are substituted for presentation checks. Screenshots and results go to `TestResults/planning-telemetry`. `PoolConcurrencyIntegrationTests` separately exercises actual opted-in Planning through Challenger and approved-stage restart/publication into native Delivery while unrelated Delivery/Merger progress. Stop the same AppHost in finally.

## Studio Delivery and invocation QA

On a fresh exact isolated default AppHost, run `node factory-qa.mjs --studio`
and `node invocation-qa.mjs --studio`, then `node telemetry.mjs --studio` once.
The first two adapt the existing probes to React and the shared factory resource;
the native smoke uses the original telemetry endpoint with the Studio dialog.
Set `NIGHTWEAVER_QA_URL` from the direct `web` endpoint. Optional
`NIGHTWEAVER_QA_OUTPUT` selects screenshot/results output. Default evidence goes
to `TestResults/studio-factory` and `TestResults/studio-invocation` relative to the
working directory. The no-flag scripts now target Studio too; factory and invocation share one representative presentation smoke.

Coverage includes operational/log interleaving, pinned replacement identity,
failed end/disconnect, reset, bounded sanitized output, disclosure/focus/follow,
configured versus observed model, retained idle/stale/stopped/unavailable,
Overview summary and long metadata at 1440/375/720 CSS pixels. Native telemetry
additionally verifies actual Last-Event-ID resume, duplicate-free output,
completion cessation and effective 200% zoom. Stop the exact AppHost in `finally`.

## Event-cache scale acceptance (opt-in)

Run the actual Web cache module with recording source/journal adapters:

```powershell
dotnet run --project tests/Nightweaver.QA.CacheProbe -- --scale --revision=$(git rev-parse HEAD) --output=TestResults/event-cache-scale.json
```

Run from the repository root. This separate executable is deliberately absent
from `Nightweaver.slnx` and normal test discovery. It creates and retains a new
isolated repository/home, imports 2,000 Beads with 40 paused histories and 800
comments, measures a matching authoritative reference cohort, bootstraps the
actual collector, drains bounded journal backlog and watches at least 130 seconds
of settled operation. It records real source command counts and causes, journal
pages, successful source age, comment visibility, native readiness/dependency and
unjournaled audit fallback. Process CPU, logical IO and private-byte samples cover
the probe host; short-lived child CPU/IO are explicitly excluded. It stops and
settles the collector in `finally` and exposes no production debug endpoint.
Large probes and sustained Playwright runs are never normal xUnit prerequisites.
