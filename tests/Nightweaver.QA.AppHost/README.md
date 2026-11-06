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

## Manual continuous-runtime pilot

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

## Cleanup

Always stop the same AppHost in a cleanup/finally step, including after a failed browser check:

```powershell
aspire stop --non-interactive
```

Each run creates a disposable Git repository and Beads tracker with sample issues. UI writes affect that fixture; stopping the AppHost removes it. The fixture is not a copy of the user's tracker. Do not use desktop Computer Use (`cua_repl/js`) to drive browser QA.
