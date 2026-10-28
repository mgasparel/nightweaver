# Nightweaver

Nightweaver is a discovery-driven software factory for Beads (`bd`) work. It is under active development. The CLI supports a read-only startup inspection and `--dry-run`, plus `dispatch` for one live lifecycle stage on one ready Bead. A scheduler that automatically advances Beads through their next stages is not implemented.

## Local prerequisites

- The .NET 10 SDK to build, test, and publish Nightweaver.
- The .NET 10 runtime to run the framework-dependent published executable.
- Git and `bd` on `PATH` for a dry run. The supported tracker is bd 1.3.1 using an initialized embedded Dolt database.
- The Target Repository must be the root of a Git working tree and contain a prepared `.beads` tracker. Complete tracker setup before running Nightweaver. A fresh `bd init` may need a normal bd write outside the dry-run to create bd's persistent gate-lock files. Nightweaver does not initialize or migrate a tracker.
- Live `dispatch` currently requires Windows for Win32 Job Object process containment, the configured Agent CLI installed and available on `PATH`, and the role prompt under `<nightweaver-root>\prompts\`. Worktree provisioning also needs the configured default branch on `origin`. A role's mandate can use `git`, `gh`, or the Target Repository's own build and test tools.

The integration tests use real Git and bd against disposable repositories and use native fake Agent apphosts for live execution. They require those tools and fail with a prerequisite error if a required tool is missing; the real-tool gate does not silently skip. A dry run does not need an Agent CLI, `gh`, a network connection, an `origin` remote, or credentials.

## Build, test, and publish

Run these commands from the repository root:

```powershell
dotnet build Nightweaver.slnx -warnaserror
dotnet test Nightweaver.slnx
dotnet publish src/Nightweaver/Nightweaver.csproj -c Release --self-contained false -p:UseAppHost=true -o 'C:\Tools\Nightweaver'
```

The publish command produces a framework-dependent apphost named `nightweaver.exe` on Windows and `nightweaver` on Unix. The machine running it needs the .NET 10 runtime.

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

# Loads C:\Tools\Nightweaver Host\projects\demo.json
& (Join-Path $nightweaverRoot 'nightweaver.exe') --project demo --dry-run

# Relative to the caller's current directory; quote the path with spaces.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --config '..\Nightweaver Host\projects\demo.json' --dry-run

# An absolute config path is also independent of the current directory.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --config 'C:\Tools\Nightweaver Host\projects\demo.json' --dry-run
```

Use the Unix apphost name `nightweaver` in place of `nightweaver.exe` on Unix systems.

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

The web application serves the static Nightweaver console at `/`, with `/index.html` available directly. Its responsive shell links the Catch-Up Digest, Human Attention Desk, Factory Execution Bays, Beads Explorer, and Full Bead Workspace. The Beads Explorer reads the configured Target Repository; the other sections remain layout placeholders in this slice. The page uses local HTML, CSS, and JavaScript assets from `wwwroot` beside the executable, so build and publish output can run independently of the source checkout. Static files remain restricted to that web root, unknown paths return 404, and there is no fallback route. `GET /health` continues to report host liveness after configuration validation without invoking Git or bd.

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

## Single-stage dispatch

`dispatch` validates the Project and Target Repository, reads the complete Beads inventory and bd's native unassigned-ready set, acquires one guarded claim, then invokes the operational retry coordinator for exactly one lifecycle stage. It does not start a scheduler or automatically choose the next role. Run one Nightweaver process at a time for a Project.

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

The six accepted lifecycle roles are `planner`, `plan_challenger`, `implementer`, `reviewer`, `qa`, and `merger`. `classifier` is reserved for automatic duplicate-failure comparison and cannot be dispatched directly. An explicit `--role` is checked against the same Authority Matrix; it cannot bypass a type, stage, terminal-state, or pause restriction. A valid task at `stage:review` can use `--role reviewer`, while `--role implementer` is refused.

Only Beads that are open, unassigned, and present in bd's native ready set can be claimed. The guarded claim changes status to `in_progress` and assigns `nightweaver:<project-name>`. Dispatch rereads ownership, workflow state, and readiness before launch. On a verified success, it releases its claim and returns the Bead to `open` with no assignee; a verified Merger closure remains closed. Legal Planning outcomes such as `planned` or `product-decision-required` are kept. Operational failures retry up to `1 + limits.max_operational_retries` attempts, resetting the isolated worktree before a retry. Repeated failures are compared by the classifier; exhaustion or a duplicate failure escalates the Bead to `needs-human` and delegates cleanup to the retry coordinator.

Exit codes are `0` for a verified stage and completed claim handoff, `1` for an ineligible Bead, operational failure, escalation, incomplete handoff, or cancellation, and `2` for command usage or Project configuration errors. Start/progress/success messages go to standard output. Operational failures, escalation, and cancellation details go to standard error. Successful dispatch retains the managed worktree, branch, and logs for a later explicit dispatch. Cancellation after execution starts stops further retries and follow-on work; inspect the coordinator's resulting claim and runtime state before retrying. This command performs no full lifecycle scheduling or general pause cleanup.

## Internal Delivery slot

Nightweaver also has an internal `DeliverySlotCoordinator` seam for one Plan Bead. It claims a native-ready, unassigned task Bead, selects Implementer, Reviewer, or QA from verified tracker state, and carries one claim and the persistent `nightweaver/<bead-id>` worktree through the Delivery path. A verified Reviewer or QA rejection returns immediately to Implementer in the same slot and worktree; both roles share the persisted `repair_cycles` budget, which is not reset by successful stages. A rejection beyond `max_repair_cycles` escalates through the needs-human cleanup path before another Agent starts, retaining the remote branch, open Pull Request, and invocation logs. At `stage:merge`, the slot verifies the handoff and releases the claim to open/unassigned for the Merger Agent. This coordinator is not exposed as a CLI command and does not provide a concurrent Delivery Pool or scheduler.

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

These role settings are reported by `--dry-run` and used when `dispatch` launches a configured Agent for its selected lifecycle stage.

## Dry-run report and safety

`--dry-run` validates the Project and Target Repository, reads the complete Beads inventory and readiness, inspects the Git worktree registry and Project runtime directories, then prints a deterministic hypothetical startup snapshot. It performs no cleanup or claim changes and launches no Agent processes. It still needs no Agent CLI, origin, network, or `gh`.

- **Startup claim releases** list owned stranded `in_progress` Beads that a startup recovery would release. The tracker is unchanged.
- **Worktree removals** and **orphan tmp deletions** describe hypothetical cleanup. Linked, locked, external, missing, or uncertain paths are preserved or reported conservatively.
- **Retained worktrees** identify checkouts that the startup sweep would keep.
- **Selected** candidates fit the configured Planning or Delivery capacity, or the singleton Merger slot. **Waiting** candidates remain eligible but exceed capacity, or Planning is waiting for active-Plan backpressure. Neither section represents a claim or dispatch.
- A `hypothetical_post_release` source means the candidate would become ready after releasing a Nightweaver-owned stranded claim. It does not mean the release has happened.

Successful inspection returns exit code `0`, including when there are no candidates or no hypothetical cleanup. Inspection and tool failures return `1`; usage or configuration errors return `2`.

Inspection uses read-only bd commands and Git with optional locks disabled. The dry-run gate compares tracker issues and dependency relationships, comments, Git refs/index/worktree registrations, repository files, and host runtime directories. With bd 1.3.1's embedded Dolt backend, read-only queries can update modification times for the existing `.beads/embeddeddolt/.../.dolt/noms` directory, its `manifest` and `journal.idx`, and the chunk journal while leaving their bytes unchanged. Those backend timestamps are accepted; paths, file contents, Git state, and logical Beads data remain unchanged.

## Documentation

- [Architecture Specification](SPEC.md): lifecycle state machines, concurrency pools, and operational invariants.
- [Glossary](GLOSSARY.md): canonical domain vocabulary.
- [Architecture Decision Records](docs/adr/): recorded decisions and trade-offs.
- [Agent Guidelines](AGENTS.md): repository instructions for contributors and coding agents.
