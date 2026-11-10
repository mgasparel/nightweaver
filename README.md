# Nightweaver

Nightweaver is a discovery-driven software factory for Beads (`bd`) work. It is under active development. The CLI supports a read-only `--dry-run`, a continuous Delivery and singleton Merger runtime, and `dispatch` for one explicitly selected lifecycle stage. Planning remains manually authored and is not scheduled by the runtime.

## Local prerequisites

- The .NET 10 SDK to build, test, and publish Nightweaver.
- The .NET 10 runtime to run the framework-dependent published executable.
- Git and `bd` on `PATH` for target repository operations. The supported tracker is bd 1.3.1 using an initialized embedded Dolt database.
- The Target Repository must be the root of a Git working tree and contain a prepared `.beads` tracker. Complete tracker setup before running Nightweaver. A fresh `bd init` may need a normal bd write outside the dry-run to create bd's persistent gate-lock files. Nightweaver does not initialize or migrate a tracker.
- Live operation currently requires Windows for Win32 Job Object process containment, the configured Agent CLI installed and available on `PATH`, and the role prompt under `<nightweaver-root>\prompts\`. Worktree provisioning also needs the configured default branch on `origin`. A role's mandate can use `git`, `gh`, or the Target Repository's own build and test tools.

Invocation temp-directory reservation and runtime native log, status, lock, diagnostic, and snapshot file boundaries support paths beyond the legacy Windows 248/260-character limits through extended-length native paths. Canonical runtime identities, reparse-point rejection, directory pins and exclusive reservation still apply; child `TEMP`, `TMP` and `TMPDIR` retain the ordinary invocation-scoped path. Filesystem component limits and the Agent CLI's own path restrictions still apply.

The integration tests use real Git and bd against disposable repositories and use native fake Agent apphosts for live execution. They require those tools and fail with a prerequisite error if a required tool is missing; the real-tool gate does not silently skip. A dry run does not need an Agent CLI, `gh`, a network connection, an `origin` remote, or credentials.

## Build, test, and publish

Run these commands from the repository root:

```powershell
dotnet build Nightweaver.slnx -warnaserror
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 636
dotnet publish src/Nightweaver/Nightweaver.csproj -c Release --self-contained false -p:UseAppHost=true -o 'C:\Tools\Nightweaver'
```

The single-project publish command produces the framework-dependent CLI apphost named `nightweaver.exe` on Windows and `nightweaver` on Unix. For paired background deployment on Windows, use the fresh-destination script below; the machine running the pair needs .NET 10 and ASP.NET Core 10.

## Project files and path resolution

`<nightweaver-root>` is the directory containing the running `nightweaver` executable (`AppContext.BaseDirectory`). It does not depend on the caller's current directory. `--project <slug>` loads `<nightweaver-root>\projects\<slug>.json`.

`--config <path>` loads the explicitly named JSON file. A relative config path is resolved from the caller's current directory. A relative `target_repository.path` is resolved from the directory containing that JSON file. The Project's `name` owns its runtime namespace and must match the slug when using `--project`.

Keep the Nightweaver host and Target Repository in separate directories. Nightweaver stores Project configs in `projects\`, and reserves `worktrees\<name>\`, `tmp\<name>\`, and `logs\<name>\` under the host root. The Target Repository path identifies a separate root Git working tree with its prepared bd tracker.

A minimal valid Project config is:

```json
{
  "name": "demo",
  "target_repository": {
    "path": "H:\\Work\\Demo Product"
  }
}
```

JSON uses `\\` to represent a Windows path separator. The target path may also be relative to the config file's directory.

Both selectors work from an unrelated working directory. These PowerShell examples use paths containing spaces:

```powershell
$nightweaverRoot = 'C:\Tools\Nightweaver Host'
Set-Location 'C:\Tools\Unrelated Caller Directory'

# Loads C:\Tools\Nightweaver Host\projects\demo.json and starts continuous Delivery and Merger until Ctrl+C.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --project demo

# Prints a one-shot read-only startup snapshot and exits.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --project demo --dry-run

# Relative to the caller's current directory; quote the path with spaces.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --config '..\Nightweaver Host\projects\demo.json' --dry-run

# An absolute config path is also independent of the current directory.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --config 'C:\Tools\Nightweaver Host\projects\demo.json' --dry-run
```

Use the Unix apphost name `nightweaver` in place of `nightweaver.exe` on Unix systems.

## Manual background deployment (Windows v1)

The supported v1 deployment starts one detached runtime and its matching Web Console manually. Closing the starting terminal or browser does not stop the runtime. A crash requires an operator to run `background start` or `background restart`; Nightweaver installs no login task or Windows service and does not restart a crashed owner automatically.

Publish both apphosts and their prompts/static assets to a new destination. Pass the existing Target Repository so the script can refuse a destination that overlaps either repository:

```powershell
.\scripts\Publish-Nightweaver.ps1 `
  -Destination 'C:\Tools\Nightweaver Host' `
  -TargetRepository 'H:\Work\Demo Product'
```

Create the Project JSON under `<destination>\projects\<slug>.json`, or keep it elsewhere and select it with `--config`. The installation is framework-dependent. The script only publishes binaries and a small pair/build manifest; it does not copy or rewrite Project configuration or initialize a tracker. It refuses nonempty destinations and never prunes an existing installation.

From any caller directory, the `background` commands use the same resolved Project configuration and host root for CLI telemetry and Web Console:

```powershell
$nightweaverRoot = 'C:\Tools\Nightweaver Host'
$nightweaver = Join-Path $nightweaverRoot 'nightweaver.exe'

& $nightweaver background start --project demo
& $nightweaver background status --project demo
Start-Process 'http://127.0.0.1:5000'
& $nightweaver background stop --project demo
& $nightweaver background restart --project demo
```

`background start` waits until the Web `/health` endpoint and `/api/factory/state` both identify the actual live runtime, Project, and Target Repository. This check separates host health from runtime health. Status prints a JSON owner record with starting/running/stopping/stopped/degraded state, process/run identity, Web URL and health, actual runtime health, shared root/configuration, and diagnostic log paths. The current-user named pipe controls the owner; there is no TCP management endpoint. Only loopback HTTP URLs with an explicit port are accepted (default `http://127.0.0.1:5000`).

The owner keeps one deployment/Project lock and the existing Target Repository process lock remains authoritative across host roots. A second start for the same owner is idempotent; a different config selection is rejected. `stop` requests the runtime's existing graceful drain and reports `stopping` if it has not completed. `stop --force` uses the existing second-stop Job Object path. Stop never respawns the owner. Restart waits for the old owner to exit before launching a new run identity; it preserves `worktrees\<slug>`, `tmp\<slug>`, `logs\<slug>`, and `telemetry\<slug>` so normal startup recovery can inspect unfinished operations. Web output and host diagnostics are written to `logs\<slug>\background-web.log` and `background-host.log` in bounded chunks. Inspect those logs and the returned status before retrying an uncertain startup or stop.

For an explicit upgrade, stop the old owner, publish into another fresh destination, then restore the operator-owned `projects`, `worktrees`, `tmp`, `logs`, and `telemetry` directories. Do not copy `run\<slug>\background\owner.lock` or `owner.json`; those identify only the old local process and are regenerated. Keep the old installation until the new pair passes `background status` and browser checks. The script never overwrites an installation or deletes runtime data.

The console can be opened or closed independently; its HTTP/SSE requests never own or cancel the runtime. A Web Console exit marks the owner degraded and requests a graceful runtime stop. A runtime or owner crash leaves a stale/unavailable observation and requires manual recovery. An explicit force stop can interrupt a tracker operation; refresh the tracker before resubmitting an operation whose result was uncertain.

## Web host scaffold

The standalone ASP.NET Core host requires the .NET 10 ASP.NET Core runtime. Run it with an existing Project JSON file:

```powershell
dotnet run --project src/Nightweaver.Web -- --Nightweaver:ConfigPath 'C:\Tools\Nightweaver Host\projects\demo.json' --Nightweaver:Root 'C:\Tools\Nightweaver Host'
```

Or supply bootstrap settings through environment variables:

```powershell
$env:Nightweaver__ConfigPath = 'C:\Tools\Nightweaver Host\projects\demo.json'
$env:Nightweaver__Root = 'C:\Tools\Nightweaver Host'
dotnet run --project src/Nightweaver.Web
```

`Nightweaver:ConfigPath` is required. `Nightweaver:Root` defaults to the web executable's directory. Relative bootstrap paths resolve from the process working directory; relative `target_repository.path` values resolve from the Project JSON directory. The existing Project schema supplies concurrency, limits, Agent defaults, and role settings. Command-line settings override environment variables, which override appsettings.

The default local address is `http://localhost:5000`; set `ASPNETCORE_URLS` or pass `--urls` to override it. `GET /health` returns HTTP 200 with `{"status":"ok"}` after configuration validation. It checks host liveness and does not invoke Git, bd, Agents, or the Orchestrator. A plain target directory suffices for this scaffold. Invalid or missing configuration prevents startup.

The web application serves the static Nightweaver console at `/`, with `/index.html` available directly. Its responsive shell links the Catch-Up Digest, Human Attention Desk, Factory Execution Bays, and Beads Explorer. The Beads Explorer, Attention Desk, and Full Bead Workspace read the configured Target Repository; factory bays show observations published by the actual continuous runtime, while Catch-Up remains a layout placeholder. Select an issue or an Attention Desk workspace link to open its full Bead workspace. The page uses local HTML, CSS, JavaScript, and vendored Markdown libraries from `wwwroot` beside the executable, so build and publish output can run independently of the source checkout. Static files remain restricted to that web root, unknown paths return 404, and there is no fallback route. `GET /health` continues to report host liveness after configuration validation without invoking Git or bd.

### Factory telemetry contract

The continuous CLI runtime is the sole writer of `<Nightweaver:Root>/telemetry/<project>/factory.json`; the standalone Web host reads this snapshot and the selected invocation log. Snapshot schema version 1 identifies the Project, canonical target repository, runtime ID, monotonic revision, observation time, runtime state, and configured Delivery/Merger capacities with stable slot IDs. Each slot reports only observed lifecycle and invocation fields. A missing, corrupt, oversized, or mismatched snapshot is unavailable; a heartbeat older than 10 seconds is stale; a graceful shutdown is stopped. These states do not imply healthy idle slots.

`GET /api/factory/state` returns a no-store object with `availability`, `reason`, and `snapshot`. `GET /api/telemetry/stream` selects one invocation using `runtime_id`, `slot_id`, and `invocation_id`; clients cannot supply a Project, filesystem path, or log root. It streams named UTF-8 SSE status, log, reset, and end events. The optional nonnegative `cursor` query parameter resumes by byte offset. A valid `Last-Event-ID` takes precedence over the query value. Initial replay and older resume cursors are clamped to the most recent 64 KiB, aligned to a UTF-8 boundary, and identified by a reset event. The stream drains appended bytes before reporting a terminal outcome.

The snapshot's `configured_model` is the model selected by Project or role configuration. Nightweaver does not infer the model an Agent actually used from that setting or from an ambient CLI default; actual model identity is unknown unless the runtime can report it reliably. Invocation logs combine stdout and stderr into one stream, so the telemetry API cannot distinguish those channels.

### Beads Explorer API

`GET /api/beads` returns a JSON array of tracker items with exactly these fields: `id`, `title`, `type`, `priority` (integer P0–P4), `status` (the stored status), `labels` (always an array), and `assignee` (a string or `null`). The response is `Cache-Control: no-store`. Optional query parameters are:

| Parameter | Meaning |
| --- | --- |
| `view=all` (default) | Complete inventory across statuses, types, labels, and assignees. |
| `view=active` | Stored status is `in_progress`; this does not prove a factory slot is live. |
| `view=ready` | IDs returned by native `bd ready --limit 0 --json`, intersected with inventory and limited to stored `open` status. |
| `view=blocked` | Stored status is `blocked` or native `bd blocked --json` reports an active blocker; closed issues are excluded. |
| `view=closed` | Stored status is `closed`. A closed Bead does not prove a Git merge. |
| `q=<text>` | Trimmed, case-insensitive substring search in the ID or title; combines with the selected view. |

View names are case-insensitive. A blank view means `all`; unsupported or repeated `view`/`q` parameters return HTTP 400. Successful empty queries return `[]`. Expected repository, tool, timeout, backend, and incompatible-output failures return HTTP 503 Problem Details without exposing host paths, raw stderr, or stack traces. The console clears rows while loading or after errors and lets the maintainer retry.

Explorer requests require Git and bd 1.3.1 on `PATH`, a Git root configured as the Target Repository, and an already initialized, prepared embedded Dolt tracker with its gate files. Requests run bd commands against that configured repository; the web host never initializes a tracker, creates gate files, or starts the Dolt backend. Tracker validation happens when an API request is made, so `/health` and the static console can start when the tracker is unavailable. Reads and intake writes share one host-local gate around embedded-Dolt operations.

#### Product Bead intake

The `+ New Feature / Bug` header action opens a modal that submits `POST /api/beads` as `application/json`:

```json
{
  "type": "feature",
  "priority": 2,
  "title": "Explain the requested outcome",
  "description": "Problem, context, and desired behavior",
  "acceptanceCriteria": "Observable conditions for success"
}
```

All five properties are required. `type` must be `feature` or `bug`; `priority` must be a JSON integer from 0 through 3. The server trims outer whitespace from text values, preserves internal Markdown, Unicode, and newlines in the multiline fields, and enforces these limits after trimming: title 200 UTF-16 code units, description 4,000, and acceptance criteria 2,000. NUL is rejected in every field; title rejects line breaks and control characters. The whole request body is limited to 64 KiB. Unknown or repeated properties and all query parameters are rejected.

Creation requires the configured Target Repository and a prepared embedded Dolt tracker with its existing gate files, just like Explorer reads. The endpoint creates one open, unassigned `feature` or `bug` Product Bead with no workflow labels or dependencies. Ready is derived from bd's native readiness query; the endpoint does not assign an Agent, set a stored `ready` status, or launch Planning. Successful requests return HTTP 201 with the Explorer row shape.

After a confirmed 201, the console clears its saved form draft, switches the Explorer to Ready, clears its search, and refreshes the list. If the new item is no longer in Ready, the console offers **Show in All** using its ID search. If the refresh fails, the creation success message remains visible and the Explorer's Retry control can reload the list. Validation and request-policy failures return 400, 403, 413, or 415 Problem Details. Expected tracker/tool failures return 503 without exposing paths, stderr, or submitted prose. If a create command was attempted but its result could not be confirmed, the response and console tell the operator to check the backlog before manually resubmitting; the console never retries a POST automatically. Creating a Product Bead does not launch Planning or start an Agent.

#### Manual Merger authoring and completion

Maintainers may create executable `task` Beads at a supported Delivery stage directly; running Planner or Plan Challenger is not required. A task without a Product parent, or with a missing or unplanned parent, still completes and cleans up normally. To request automatic Product completion, mark the `feature` or `bug` Product Bead `planned` and add the native discovered-from dependency from task to Product: `bd dep add <task-id> <product-id> --type discovered-from`. The relationship points from the Plan task to its Product. Nightweaver closes that Product only after it has at least one discovered task and every linked task is closed and unpaused; hierarchy and blocking links do not count.

#### Human Attention Desk

The pinned Attention Desk refreshes on page load, its **Refresh**/**Retry** controls, and after a completed action. It reads the complete configured inventory and requests comments only for nonterminal Beads carrying `product-decision-required` or `needs-human`. Paused Beads remain visible when assigned or when their workflow is contradictory; the server disables actions for those rows and revalidates every action under the same host-local tracker gate used by Explorer and intake.

`GET /api/attention` returns a no-store JSON array. Each camelCase item includes `beadId`, `title`, `type`, `priority`, `status`, `labels`, `stage`, `kind`, `pausedAt`, `pauseTimeSource`, `fromRole`, `question`, `diagnostic`, `repairCycles`, `maxRepairCycles`, `canDecide`, `canRetry`, `canAbort`, `unavailableReason`, and `sourceAuthor`. Needs-human diagnostics are projected from matching `nightweaver-needs-human-v1` comments and later matching cleanup-failure comments. The console displays the cause, reason, latest findings, log-file paths, and cleanup warning as text; it never reads referenced files. Legacy decision comments have no shared schema, so the Desk shows the latest nonempty non-Nightweaver comment as **Latest comment/context**, with its author and comment timestamp. This is a review aid, not a reliable extraction of a historical question. The pause time is based on that selected comment; otherwise it is `Unknown`. It is never inferred from Bead creation or update time. No model, process, checkout, or live-agent state is inferred.

Escalation diagnostics may use `stage: "(none)"` when an initial Planner or Implementer failure happened before a stage label was assigned; the Desk keeps the item's stage unknown and uses the validated failed role from the diagnostic. Comments are ordered by timestamp and then ordinal comment ID, so a cleanup warning with the same timestamp as its diagnostic is still retained when its ID sorts later. Legacy pause timestamps cannot be recovered reliably when no matching comment exists.

`POST /api/beads/{id}/decide` accepts `{"instruction":"...","action":"note_only"}` to save guidance, or `{"instruction":"...","action":"resume","expected":{"stage":"stage:..."|null,"pauseLabels":["product-decision-required"],"repairCycles":0}}` to resume. Instructions are trimmed, must not be blank, and are limited to 16 KiB of UTF-8 inside a 32 KiB JSON body. Both actions append a `nightweaver-human-guidance-v1` comment attributed to `local-console`; only a successful guarded state update authorizes resumption. `POST /api/beads/{id}/retry` requires `{"expected":{"stage":"stage:..."|null,"pauseLabels":["needs-human"],"repairCycles":0}}` and records a `nightweaver-human-action-v1` audit comment. Resume and Retry reread the Bead under the shared tracker gate and compare the displayed stage, complete pause-label set, and repair count before writing any comment. A stale, closed, claimed, or otherwise unavailable Bead returns a refreshable conflict. After the audit comment, the service rechecks the observed identity and makes one native update guarded for open status and an empty assignee; it removes both pause labels and unsets `repair_cycles` while preserving stage and unrelated fields. `POST /api/beads/{id}/abort` records an audit comment and attempts a guarded close; it keeps pause labels and repair budget and does not merge, delete, or change a remote branch or pull request. Abort accepts no body or `{}`. Native Beads dependency rules still apply.

Attention Desk writes require `application/json`, the `X-Nightweaver-Request: console-v1` header, and a same-origin request to the configured local binding. No cross-origin policy is enabled. Direct local clients without an `Origin` must still provide the header. Resume and Retry only make a Bead eligible for ordinary dependency, capacity, stage, and claim scheduling; they do not directly launch an Agent or create a second dispatch. Successful mutations are verified by reading the tracker back. A failed update remains a failed action even when its audit comment persisted. Responses report `commentRecorded` and `stateChanged` as booleans or `null` when the current state could not be verified; refresh before manually retrying. Timeout and disconnect outcomes are not exactly-once.

Notes, comments, description edits, and audit history provide context only; they never authorize execution. Only a successful explicit Attention Desk Resume or Retry state update removes the pause and resets `repair_cycles`. Description remains read-only in this console. This console refreshes on load, explicit request, and completed action; scheduling and Agent launch remain in the normal runtime. Exact historical pause times and reliable question extraction for legacy comments remain unavailable.

#### Full Bead Workspace

`GET /api/beads/{id}` returns a no-store workspace envelope for one exact Bead ID from the configured Target Repository. It rejects query parameters, validates the ID, and returns 404 when that exact ID is absent. The server reads the authoritative inventory, `bd show`, and complete ordered comment history, then revalidates the Bead's workflow identity before returning the response; a concurrent workflow change returns 409 so the console can refresh. Expected tracker, tool, and malformed-output errors return a safe 503 Problem Details response without host paths or raw process output.

The `bead` object contains `id`, `title`, `type`, `priority`, `status`, `assignee`, `labels`, `stage`, `repairCycles`, `createdAt`, `updatedAt`, `closedAt`, `createdBy`, `description`, `design`, `acceptanceCriteria`, and `notes`. Missing or null prose fields are represented as empty strings. `relations` includes dependency and dependent Beads with their IDs, native dependency type, direction, title, and status. `comments` contains the complete ordered history with native comment ID, author, timestamp, exact stored body, and recognized comment kind/role/stage where available. `history` provides disjoint chronological `olderCommentIds` and `recentCommentIds`, plus `activeCommentId` and its `selectionSource` (`structured`, `legacy-context`, or `none`). A selected active comment starts the recent segment; without a selection, the latest comment is recent. The client exposes older comments behind an expansion control and keeps exact comment text available even when structured human guidance is summarized.

The optional `attention` object uses the existing Human Attention Desk projection only for a currently paused, nonterminal Bead; it is `null` otherwise. Workspace guidance reuses the same note, resume, retry, and guarded abort actions, validation, pending-action protection, and in-memory drafts as the Attention Desk. A failed or uncertain action blocks another attempt until the operator refreshes.

The `environment` object reports only a verified managed Git worktree snapshot and available runtime metadata. Its state distinguishes `present`, `absent`, `unsafe`, and `unavailable`; Git cleanliness is checked with untracked files included and submodule internals excluded. The observer verifies the registered worktree and linked-worktree markers before reading status. Runtime process IDs are shown only when a trusted observer supplies them; they are unavailable otherwise. Environment observation is read-only and can be unavailable without hiding Bead details. No process, checkout, branch, or merge state is inferred from tracker fields.

Description, design, acceptance criteria, and notes render as Markdown through locally vendored `markdown-it` 15.0.3 and DOMPurify 3.4.17. The assets are pinned copies of their exact npm releases, checked with package integrity metadata during import; the browser does not fetch packages at runtime. Raw HTML and automatic linkification are disabled, generated markup is restricted to a small allowlist, and images render as text rather than loading remote resources. Only `http` and `https` links open externally; local Markdown anchors are limited to comments in the current workspace. If the renderer is unavailable, the exact Markdown source remains visible as text. The libraries and their license files ship from `wwwroot/vendor` with build and publish output.

## Single-stage dispatch

`dispatch` validates the Project and Target Repository, then acquires an OS-managed process-lifetime lock keyed to the Target Repository's Git common-directory identity. The lock is shared across Project names and Nightweaver host roots and lives outside the Target Repository. A competing process exits before Beads inventory/readiness queries, recovery, dispatch, or runtime-directory changes; process exit or death releases the lock. Under the lock, dispatch validates the current Project's shared worktree, temporary, and log roots, releases only exact `in_progress` claims assigned to that Project's recovery actor with guarded status/assignee updates, and rereads uncertain writes rather than replaying them. It then reads current inventory and bd's actual unassigned-ready set, acquires one guarded claim, and invokes the operational retry coordinator for exactly one lifecycle stage. `--dry-run` remains read-only and acquires no recovery authority. `dispatch` does not start or join the continuous scheduler.

The continuous runtime registers Delivery and the singleton Merger in that order, with independent capacities and a shared stop gate. Merger admission reads fresh Beads readiness, selects eligible `stage:merge` tasks in priority, creation-time, then ordinal-ID order, and admits at most one. It queues preparation without holding the shared discovery pass; Merger completion releases local capacity before notifying discovery. Both pools share the same timer, journal, completion, and shutdown lifecycle.

The Merger slot reuses the retained Delivery worktree and operational retry handling. Merger Agents provide current-invocation findings while leaving Bead lifecycle fields unchanged; the Orchestrator verifies evidence and publishes one guarded stage/counter/claim update for a repairable rejection. A repairable rejection is released as open and unassigned for Delivery; an exhausted budget follows the existing needs-human escalation path. A handoff receipt ends outgoing Bead and environment operations, while local Merger capacity is released independently. For success, the completion owner requires a new invocation merge-evidence comment, an unchanged `stage:merge` claim and worktree head, a matching merged pull request, passing required checks on the checked head, and the merge commit on the fetched default branch. It records verified evidence and performs one guarded task closure, rereading once after an ambiguous response without replaying the write. Confirmed closure triggers independent exact-resource cleanup and planned Product completion. A Product closes only when the task has one discovered-from planned `feature` or `bug` parent, that Product has at least one discovered task, and every linked task is closed and unpaused; fresh reads and a guarded update protect the transition. Cleanup and Product failures are diagnosed independently and do not undo a verified merge.

```text
nightweaver dispatch <bead-id> [--role <role>] (--project <project-slug> | --config <path-to-project-json>)
```

Without `--role`, the role is inferred from the Bead's issue type and stage label:

| Bead type | Stage | Inferred role |
| --- | --- | --- |
| `task` | absent or `stage:implement` | `implementer` |
| `task` | `stage:review` | `reviewer` |
| `task` | `stage:qa` | `qa` |
| `task` | `stage:merge` | `merger` |
| `feature` or `bug` | absent, `stage:plan`, or `stage:plan-approved` | `planner` |
| `feature` or `bug` | `stage:plan-challenge` | `plan_challenger` |

The six accepted lifecycle roles are `planner`, `plan_challenger`, `implementer`, `reviewer`, `qa`, and `merger`. `classifier` is reserved for automatic duplicate-failure comparison and the bounded shared Delivery outcome fallback for Implementer, Reviewer, or QA; it cannot be dispatched directly. Each Delivery role receives its own allowed outcomes and submits evidence without writing workflow fields. The Orchestrator classifies the evidence with OpenAI Decisions first and at most one fallback using the identical role contract, then independently verifies and publishes a guarded outcome. An explicit `--role` is checked against the same Authority Matrix; it cannot bypass a type, stage, terminal-state, or pause restriction. A valid task at `stage:review` can use `--role reviewer`, while `--role implementer` is refused.

Only Beads that are open, unassigned, and present in bd's native ready set can be claimed. The guarded claim changes status to `in_progress` and assigns `nightweaver:<project-name>`. Dispatch rereads ownership, workflow state, and readiness before launch. Each Delivery Agent (Implementer, Reviewer, or QA) supplies role-specific natural-language evidence and leaves workflow fields unchanged. The Orchestrator uses OpenAI Decisions first and at most one standard Agent CLI fallback with the same role-specific question, allowed outcomes, and evidence; deterministic verification remains authoritative. Verified Implementer approval advances to `stage:review`; Reviewer approval advances to `stage:qa`; a Reviewer or QA rejection returns to `stage:implement` while incrementing the shared repair budget in the same guarded update. A concrete human question becomes `product-decision-required`; unclear/invalid classification, failed verification, counter overflow, a blocker, exhaustion, or duplicate operational failure records diagnostics and pauses as `needs-human`, preserving the stage, counter, and work when no repair can be published. Classification or verification failure does not rerun the completed Delivery Agent. QA approval publishes `stage:merge`, status `open`, and an empty assignee together in one guarded update. Dispatch and the internal Delivery slot consume the verified publication receipt; if the response is ambiguous they reread once and respect a receiver that already owns the Bead. After a confirmed QA handoff, outgoing code performs no further Bead/worktree operation. On verified single-stage success, dispatch releases its claim in the same guarded publication and returns the Bead to `open` with no assignee. Merger completion separately verifies the current invocation's merge evidence and owns guarded closure, exact cleanup, and eligible Product completion. Legal Planning outcomes such as `planned` or `product-decision-required` are kept. Operational retries may reset an initially clean session; retained dirty sessions preserve inherited tracked, staged, untracked, ignored, and temporary contents across retries and escalate if those contents prevent a verified success.

Exit codes are `0` for a verified stage and completed claim handoff, `1` for an ineligible Bead, operational failure, escalation, incomplete handoff, or cancellation, and `2` for command usage or Project configuration errors. Start/progress/success messages go to standard output. Operational failures, escalation, and cancellation details go to standard error. Successful dispatch retains the managed worktree, branch, and logs for a later explicit dispatch. Cancellation after execution starts stops further retries and follow-on work; inspect the coordinator's resulting claim and runtime state before retrying. This command performs no full lifecycle scheduling or general pause cleanup.

## Internal Delivery slot

Nightweaver also has an internal `DeliverySlotCoordinator` seam for one Plan Bead. It claims a native-ready, unassigned task Bead, selects Implementer, Reviewer, or QA from verified tracker state, and carries one claim and the persistent `nightweaver/<bead-id>` worktree through Delivery. Each role returns evidence; the Orchestrator classifies that role's allowed outcomes, independently verifies approval, and publishes stage changes under the current owner guard. A Reviewer or QA rejection returns to Implementer in the same slot and worktree; both roles share the persisted `repair_cycles` budget. A rejection beyond `max_repair_cycles`, or a counter that cannot be incremented without overflow, records a `needs-human` pause while preserving the stage, counter, worktree, branch, temporary files, and invocation logs, then releases only the still-owned claim. After QA verification and all outgoing work, one guarded update publishes `stage:merge`, status `open`, and an empty assignee. The slot consumes that publication receipt immediately; when a response is ambiguous it rereads once, accepts only a matching publication, and stops all outgoing Bead/worktree operations if a receiver already owns the stage. This coordinator is not exposed as a CLI command.

The internal `DeliveryPoolCoordinator` composes those complete slots for one long-lived Project instance. A caller explicitly evaluates it after startup, tracker changes, or slot completions. Each evaluation rereads inventory and native `bd ready` state, reserves open unassigned task Beads in priority/FIFO order up to `concurrency.max_concurrent_delivery`, and starts each slot independently. An evaluation returns admissions without waiting for slot completion; callers can await each admission and consume the lossless single-reader completion channel, then evaluate again. Reservations remain active through the complete slot, including repairs and claim handoff, and completion reports local capacity release separately from tracker claim handoff. Capacity is process-local to that pool instance and guarded Bead claims prevent duplicate ownership; this is not a distributed capacity lease, continuous event watcher, recovery loop, or public scheduler.

Examples from an unrelated caller directory with spaces in the paths:

```powershell
$nightweaverRoot = 'C:\Tools\Nightweaver Host'
Set-Location 'C:\Tools\Unrelated Caller Directory'

# Infer the role from this Bead's type and stage.
& (Join-Path $nightweaverRoot 'nightweaver.exe') dispatch ns-42 --project demo

# Resolve the relative config path from the caller directory.
& (Join-Path $nightweaverRoot 'nightweaver.exe') dispatch ns-42 --config '..\Nightweaver Host\projects\demo.json'

# Explicit reviewer selection for a task currently labelled stage:review.
& (Join-Path $nightweaverRoot 'nightweaver.exe') dispatch ns-42 --role reviewer --config '..\Nightweaver Host\projects\demo.json'
```

## Built-in defaults and role inheritance

Omitted settings receive these defaults:

| Setting | Default |
| --- | --- |
| `target_repository.default_branch` | `main` |
| `concurrency.max_concurrent_planning` | `2` |
| `concurrency.max_concurrent_delivery` | `3` |
| Singleton Merger slots | `1` |
| `concurrency.max_active_plan_beads` | `5` |
| `limits.max_repair_cycles` | `3` |
| `limits.max_operational_retries` | `2` |
| `agent_defaults.cli` | `claude` |
| `agent_defaults.model` | `null` (use the Agent CLI's model default) |
| `agent_defaults.effort` | `high` |
| `agent_defaults.timeout_minutes` | `30` |

The seven roles are `planner`, `plan_challenger`, `implementer`, `reviewer`, `qa`, `merger`, and `classifier`. Each role inherits each omitted property independently from `agent_defaults`. A role may override only the properties it needs. An explicit `"model": null` clears an inherited model and uses the Agent CLI's own default; omitting `model` inherits the configured value.

`max_active_plan_beads` gates new Planning selections only. Delivery and the singleton Merger slot are selected independently while Planning is backpressured.

For example, this sets a shared model and overrides the reviewer's effort while leaving all other values inherited:

```json
{
  "name": "demo",
  "target_repository": { "path": "H:\\Work\\Demo Product" },
  "agent_defaults": { "model": "example-model" },
  "roles": {
    "reviewer": { "effort": "medium" },
    "qa": { "model": null }
  }
}
```

These role settings are reported by `--dry-run` and used when continuous Delivery or `dispatch` launches a configured Agent.

## Continuous Delivery and Merger runtime

A selector without `--dry-run` starts the live Orchestrator. It acquires the Target Repository process lock, validates shared runtime roots, performs startup recovery and conservative environment cleanup, then begins Delivery and singleton Merger discovery. The process stays alive when no work is eligible.

Startup, best-effort journal events, an always-on 30-second timer, and every Delivery or Merger completion after local capacity release request the same single-reader discovery path. Bursts coalesce, overlapping passes do not run, and a request received during a pass stays pending for a subsequent fresh read. Each pass evaluates Delivery then Merger using current Beads inventory and readiness. A failed tracker read admits no work from a stale snapshot; a later timer or pool completion retries it. Both pools reserve local capacity before preparation so slow setup for one task does not hold up the other pool's admission. Candidates retain priority, creation-time, then ordinal-ID ordering within each pool.

The runtime composes Delivery and singleton Merger; Planning remains manually authored and is not scheduled. Guarded Attention Desk Resume and Retry actions make paused Beads eligible for ordinary discovery. The runtime best-effort enables and tails the configured Target Repository's durable events journal; each record requests the same discovery path, including events authored by the Orchestrator. Startup admission does not wait for journal setup, attachment, head capture, or historical replay. A terminated or truncated tail requests discovery and reconnects with exponential backoff capped at 30 seconds. The always-on timer and both pool completion triggers remain active while the tail reconnects, and fresh tracker reads determine eligibility. The first Ctrl+C closes shared admission and Agent-launch boundaries. Preparation already in flight settles without launching an Agent; an already launched turn can finish verification, bounded outcome classification and guarded result recording under its ordinary role timeout. Shutdown starts no next role, operational retry, duplicate-failure classifier, or retry-only reset. It safely finalizes still-owned claims, retains unfinished work, drains both pools, and exits unsuccessfully when a turn or required finalization fails. A second Ctrl+C closes every active Agent Job Object and exits unsuccessfully without waiting for executor cleanup, classification, external operations, verification, resets, environment cleanup, or claim release. It starts no fallback or compensating write; later signals do not restart shutdown. A brief diagnostic is best effort, and the repository lock remains held through process exit. Startup recovery rereads actual tracker and filesystem state and handles exact Nightweaver-owned claims after interruption while retaining unfinished and paused work.

## Dry-run report and safety

`--dry-run` validates the Project and Target Repository, reads the complete Beads inventory and readiness, inspects the Git worktree registry and Project runtime directories, then prints a deterministic hypothetical startup snapshot. It does not acquire the Target Repository lock, perform claim recovery, create runtime directories, or launch Agent processes. It still needs no Agent CLI, origin, network, or `gh`.

- **Startup claim releases** list exact current-Project actor-matching stranded `in_progress` Beads that live dispatch recovery would release. The tracker is unchanged.
- **Worktree removals** include only a `planned` feature/bug on its detached Planning checkout or a closed task on its exact `nightweaver/<bead-id>` Delivery branch. **Orphan tmp deletions** require a known completed Bead and no surviving registration or directory. Pause-labeled, locked, missing, prunable, unregistered, linked, deleted-Bead, or otherwise uncertain environments and their tmp are retained and diagnosed. Startup cleanup removes only the targeted worktree; it does not broadly prune registrations or delete branches.
- **Retained worktrees** identify checkouts that the startup sweep would keep.
- Paused and locally unsafe or unusable environments are excluded from projected selection; diagnostics identify the affected path while unrelated valid Beads remain eligible. This projection does not perform an escalation or write tracker state.
- **Selected** candidates fit the configured Planning or Delivery capacity, or the singleton Merger slot. **Waiting** candidates remain eligible but exceed capacity, or Planning is waiting for active-Plan backpressure. Neither section represents a claim or dispatch.
- A `hypothetical_post_release` source means the candidate would become ready after releasing a Nightweaver-owned stranded claim. It does not mean the release has happened.

Successful inspection returns exit code `0`, including when there are no candidates or no hypothetical cleanup. Inspection and tool failures return `1`; usage or configuration errors return `2`.

Inspection uses read-only bd commands and Git with optional locks disabled. The dry-run gate compares tracker issues and dependency relationships, comments, Git refs/index/worktree registrations, repository files, and host runtime directories. With bd 1.3.1's embedded Dolt backend, read-only queries can update modification times for the existing `.beads/embeddeddolt/.../.dolt/noms` directory, its `manifest` and `journal.idx`, and the chunk journal while leaving their bytes unchanged. Those backend timestamps are accepted; paths, file contents, Git state, and logical Beads data remain unchanged.

## Documentation

- [Architecture Specification](SPEC.md): lifecycle state machines, concurrency pools, and operational invariants.
- [Glossary](GLOSSARY.md): canonical domain vocabulary.
- [Architecture Decision Records](docs/adr/): recorded decisions and trade-offs.
- [Agent Guidelines](AGENTS.md): repository instructions for contributors and coding agents.
