# QA AppHost

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

The isolated AppHost starts two disposable native `AgentInvocationFixture` processes through Nightweaver's invocation preparer, telemetry publisher, and executor. Each writes a distinct marker and waits for a disposable release file, so `telemetry.mjs` can inspect the live output and then observe the actual completion. The browser script interrupts the first EventSource once, verifies the reconnect's `Last-Event-ID` and duplicate-free output from the real endpoint, exercises pause/resume-follow/jump-to-latest, checks completion and retry cessation, and tests Escape/focus at a 375px viewport and at a 640 CSS-pixel viewport with device scale factor 2 (the effective layout at 200% zoom on a 1280px desktop). It also checks stale availability while retaining the last validated occupied snapshot. The script writes both release files in its `finally` block; AppHost teardown releases them again defensively, settles the native processes, and removes the disposable repository, worktrees, logs, runtime snapshot, and processes.

## Playwright mutation QA

The disposable tracker is seeded with separate attention items for the browser actions:

- `QA product decision` for guidance and resume (`Send & Resume`).
- `QA needs human` for `Reset Budget & Retry`.
- `QA needs human abort` for the two-step `Abort & Close` confirmation.

Use headless Playwright on the same URL from `aspire describe`. Exercise each action in the page so the browser sends its same-origin headers; do not substitute `APIRequestContext` for these mutation checks. Confirm that each POST succeeds and that the page/tracker reflects the resulting state: the decision card and retry card resolve, and the abort item closes. Capture `pageerror` and console error events as in `smoke.mjs`, and close the browser in a `finally` block.

Run the full fixture interaction checks with `node workspace-qa.mjs`; this covers the existing workspace and mutation flows in addition to the invocation stream check.

Run `node planning-readiness-qa.mjs` against a fresh default fixture to check the Product backlog control, a stale 409 request, explicit Ready for Planning, persisted readiness after reload, and the 375px layout. It saves desktop/mobile screenshots under `TestResults`. This authorizes only the disposable Product; continuous Planning remains outside the runtime.

## Digest disclosure QA

Run `node digest-qa.mjs` against a fresh default fixture using the same discovered direct endpoint. It first saves a real checkpoint through the UI and independently reads persistence. Deterministic digest response fixtures then exercise long histories (5 threads / 4 children / 6 standalone initial bounds), summary disclosure, keyboard focus across refresh and removed items, window selection, retained data and save failures, and newer concurrent activity after catch-up. Screenshots under `TestResults/digest` cover busy, empty, caught-up and error states at desktop, plus compact and expanded long content at 375px and 720 CSS px. Only the digest responses are substituted; the shell and other sections use the running isolated host. Expected HTTP error fixtures may emit network console errors; uncaught page errors and other console errors fail the check. Always stop the same AppHost in `finally` afterward.

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

Run `node explorer-qa.mjs` against a fresh isolated stack's discovered direct endpoint. Deterministic 211-Bead list responses check Ready as the initial view, 15-row bounds for every filter, range/page announcements, keyboard focus across navigation and refresh, full-dataset search, stale-request cancellation, empty/end-page clamping and responsive overflow. Real same-origin intake writes to the disposable tracker and real Workspace navigation verify created-ID discovery on a later Ready page, successful creation followed by a failed refresh and Retry without reposting, and Show in All when absent from Ready. No page/size API parameters are used. Screenshots under `TestResults/explorer` cover the large tracker at 1440×900, 375px and 720 CSS px plus empty and unavailable states. Always stop the same AppHost in `finally`.

Representative Explorer evidence: [desktop](../../docs/acceptance/explorer/large-1440.png), [375px](../../docs/acceptance/explorer/large-375.png), [720 CSS px](../../docs/acceptance/explorer/large-720.png), [empty](../../docs/acceptance/explorer/empty.png) and [unavailable](../../docs/acceptance/explorer/error.png).

Explorer focus checks also use 16 results to exercise keyboard Next/Previous onto both boundaries, then refresh down to one/zero results and an unavailable tracker. Focus moves to an enabled direction or the visible result range when neither direction is available. During loading the range holds focus; a response never restores pagination focus after the user moves elsewhere. The script asserts visible focus outlines and captures [boundary focus evidence](../../docs/acceptance/explorer/boundary-focus.png). Set `NIGHTWEAVER_QA_OUTPUT` to a separate directory to preserve evidence from earlier runs.

## Integrated console polish QA

Run `node console-qa.mjs --baseline` before implementation, or set `NIGHTWEAVER_QA_BASELINE` to the pre-change commit (for ns-1b1.6: `566899e1993469cc9f4ab624956093d5dc1746f2`). Baseline mode serves only the historical shell HTML/CSS/JavaScript from Git; the same isolated host supplies the APIs. Run `node console-qa.mjs` for the candidate. Both modes use identical four-paused-Bead and busy Delivery/Merger fixtures and capture desktop, 375px and 720 CSS px busy, long-context, stale/read-error and idle screenshots under `TestResults/console`. Candidate assertions cover two-card priority order/count, Show all/fewer, disclosures, drafts, focus on changed refresh/collapse, a delayed note action across refresh, the clear row and initial execution/Explorer visibility. The delayed note response is substituted to test rendering only; `workspace-qa.mjs` separately verifies real same-origin tracker mutations.

Run the existing `digest-qa.mjs`, `explorer-qa.mjs`, `factory-qa.mjs`, `invocation-qa.mjs`, `telemetry.mjs`, `workspace-qa.mjs` and `planning-readiness-qa.mjs` for the complete acceptance matrix: large histories/inventory, real intake and Workspace actions, guarded readiness, diagnostics and native reconnect/output. Use a fresh isolated stack for mutation checks, and always stop the exact AppHost in `finally`, including after failed checks. Retained integrated evidence and the measured release gate are in [the console acceptance report](../../docs/acceptance/console-polish.md).

## Cleanup

Always stop the same AppHost in a cleanup/finally step, including after a failed browser check:

```powershell
aspire stop --non-interactive
```

Each run creates a disposable Git repository and Beads tracker with sample issues. UI writes affect that fixture; stopping the AppHost removes it. The fixture is not a copy of the user's tracker. Do not use desktop Computer Use (`cua_repl/js`) to drive browser QA.
