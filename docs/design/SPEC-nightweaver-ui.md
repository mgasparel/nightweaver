## Problem Statement

As a software engineer and repository maintainer directing Nightweaver, I currently only have headless CLI commands (`nightweaver --dry-run`, `bd` commands in PowerShell) to monitor and direct autonomous agents. Beads (`bd`) is a terminal-only tracker, and the Nightweaver Orchestrator operates as a background daemon.

This creates several severe friction points:
1. **Absence of a Human-Friendly View**: I have no single, visual surface to inspect the state of my beads, read rich problem statements, or understand dependencies and progression through the factory.
2. **Cognitive Overload After Absence**: If I don't log in for several days or weeks, dozens of Plan Beads may have been planned, implemented, reviewed, tested, and merged. Sifting through a raw linear stream of 40+ closed task IDs or Git commit logs is overwhelming and exhausting.
3. **Friction in Human-in-the-Loop Interventions**: When autonomous agents hit an ambiguity (`product-decision-required`) or exhaust their repair budget (`needs-human`), they halt and wait. Without an integrated triage UI, discovering these pauses requires manual CLI queries, and answering them requires remembering exact `bd comments add` and `bd update` flag conventions.
4. **No Intuitive Intake for Feature Requests**: Submitting a new feature request or bug report currently requires crafting multi-flag CLI strings in terminal sessions rather than a dedicated intake composer.
5. **Lack of Deep Reading Real Estate**: Compact flyouts or side drawers lack the screen real estate and typographic readability necessary to dig deep into complex architectural specifications and navigate sprawling multi-stage comment histories.

## Solution

A local web application and dashboard console hosted by the Nightweaver Orchestrator (`nightweaver dashboard`) that serves as the single human-friendly command deck for Beads and autonomous factory operations.

The solution integrates:
1. **Factory Execution Bays**: Real-time operational visibility into the three bounded concurrency pools (Planning Pool with backpressure gauge, Delivery Floor with isolated worktrees, and Singleton Merger Air-Lock with rebase and CI monitoring).
2. **"Since Last Caught Up" Catch-Up Digest**: A collapsible top-of-console window-delta digest designed for both daily check-ins and multi-week absences, grouping connected completed and newly-opened/progressed Beads into unified Lineage Threads (via `parent` / `parent-child` and multi-hop `discovered-from` chains) alongside standalone activity, surfacing best-effort outcome summaries (`close_reason` / commit SHAs / PR references with Bead state fallback), tracking a persistent per-project `last_caught_up_at` checkpoint under `<nightweaver-root>/telemetry/<project>/catch-up.json`, and providing a race-safe single-click "Mark Caught Up" action that auto-collapses the digest when caught up.
3. **Human Attention Desk**: A prioritized intervention queue surfacing `product-decision-required` questions and `needs-human` escalations, supporting both instant one-line resolutions and deep context escalation.
4. **Dedicated Full Bead Workspace**: A large-surface reading and decision view featuring generous typography (15–16px body, relaxed line height), smart collapsible history (grouping older planning and review cycles under an expandable banner), spec inspection, runtime environment sidebar, and an integrated human guidance studio.
5. **Product Bead Intake Composer**: An intuitive interface to author and submit new Product Beads (`--type feature` or `--type bug`) directly into the tracker.
6. **Human-Friendly Beads Explorer**: A filterable, searchable inventory across all tracker states.

## User Stories

1. As a repository maintainer, I want to open a local dashboard via a single command, so that I can inspect my factory without configuring external servers or cloud services.
2. As a repository maintainer returning after hours, days, or weeks away, I want a collapsible Catch-Up Digest at the very top of the console showing the full delta of what completed and what was newly discovered/updated in the selected window, so that I can understand overall progress and new follow-ups without scrolling through dozens of individual tasks.
3. As a repository maintainer, I want connected Beads (both `parent-child` and multi-hop `discovered-from` chains, mixing closed and still-open items) rolled up into unified Lineage Threads alongside a clean Standalone Activity list, with best-effort outcome summaries (`close_reason`, extracted commit SHAs/PRs, and Bead state fallback), so that multi-step investigations and feature slices read as coherent stories while standalone tasks and bugs remain visible.
4. As a repository maintainer, I want an explicit checkpoint status indicator and 4 grounded tracker counters (`Completed`, `Active Threads`, `Open / Updated`, and `Waiting for You`), so that I know the exact time window and health at a glance even when the digest is collapsed.
5. As a repository maintainer, I want a race-safe "Mark Caught Up" button that records the loaded snapshot's `generatedAt` timestamp to `<nightweaver-root>/telemetry/<project>/catch-up.json` and auto-collapses the digest when `Since Caught Up` has zero new items, so that I can reset my baseline without hiding work completed while I was reading.
6. As a repository maintainer, I want time-window preset pills (`Since Caught Up`, `24h`, `7d`, `30d`, `All`), defaulting to `24h` when no checkpoint has been set yet, so that I can explore factory velocity over different historical windows without losing my saved checkpoint.
7. As a product owner, I want a pinned Human Attention Desk at the top of the console, so that I immediately see any beads halting agents for guidance.
8. As a product owner, I want to read the Planner Agent's exact clarification question on a `product-decision-required` bead, so that I understand what architectural ambiguity stopped planning.
9. As a product owner, I want a quick inline reply box on the attention card, so that I can resolve straightforward one-line decisions in seconds.
10. As a product owner, I want an "Open Full Bead Workspace" link on every attention card, so that I can review full specification context when a decision is complex.
11. As a repository maintainer, I want to see `needs-human` escalation cards when an agent exhausts its repair cycles, so that I know why verification failed.
12. As a repository maintainer, I want one-click actions on `needs-human` cards to reset the repair budget and retry, copy the checkout command, or abort the bead, so that I can resolve blockers with minimal friction.
13. As an operator, I want real-time visibility into the Planning Pool slots, so that I know which Product Beads are actively being drafted or critiqued.
14. As an operator, I want a live gauge of the active Plan Bead backpressure, so that I can see when planning is paused to prevent delivery queue bloat.
15. As an operator, I want real-time visibility into the Delivery Floor slots, so that I can monitor isolated worktrees, active branches, diff counters, and repair cycles.
16. As an operator, I want real-time visibility into the Singleton Merger Slot, so that I can watch serialized branch rebases, conflict resolution, and remote GitHub check polling.
17. As an operator, I want to click any active slot to slide open a live terminal telemetry drawer, so that I can inspect simulated stdout and process timeout clocks without switching to a terminal.
18. As a repository maintainer, I want a dedicated Full Bead Workspace view, so that I have ample screen real estate to read comprehensive specifications.
19. As a reader, I want generous, readable typography (15–16px body, relaxed line height), so that I can read long technical specs without eye fatigue.
20. As a reader, I want older planning drafts and adversarial challenge iterations to be collapsed by default under a single toggle banner, so that I am not overwhelmed by stale back-and-forth history.
21. As a reader, I want to click the collapsed history banner to expand all previous iterations, so that I can inspect previous revisions when needed.
22. As a reader, I want the active decision prompt or latest failure diagnostic to remain prominently expanded and accented at the bottom of the history stream, so that I immediately see the current problem.
23. As a product owner, I want a spacious response textarea in the Full Bead Workspace with pre-filled template chips, so that I can draft detailed guidance comfortably.
24. As a product owner, I want separate **Save Note** and explicit **Resume**/**Retry** actions in the workspace, so that guidance supplies context while only a guarded state change makes paused work eligible for normal scheduling.
25. As a developer, I want a sidebar in the Full Bead Workspace displaying worktree path, git branch, git cleanliness status, and Win32 Job Object PID, so that execution context is always transparent.
26. As a product owner, I want a `+ New Feature / Bug` button in the header, so that I can submit new work directly from the UI.
27. As a product owner, I want an intake modal allowing me to select type (`feature` or `bug`), priority (`P0`–`P3`), title, and acceptance criteria, so that I can capture requirements completely.
28. As an operator, I want newly submitted beads to appear immediately in the ready backlog, so that I know the Planning Pool will discover them on its next pass.
29. As a repository maintainer, I want a filterable Beads Explorer table (All, Active, Ready, Blocked, Closed), so that I can explore my entire issue tracker visually.
30. As a repository maintainer, I want to click any row in the Beads Explorer to open its Full Bead Workspace, so that navigation feels cohesive and fluid.

## Implementation Decisions

### 1. Architectural Model & UI Consensus
- **Combined Factory Mission Control & Catch-Up Digest**: The user interface synthesizes real-time factory floor monitoring (Variant A) with an executive triage and catch-up studio (Variant B).
- **Rule out Variant C**: The dependency DAG/assembly graph is ruled out from the primary interface to avoid visual clutter and maintain focus on operator actionability.
- **Single Window into Beads**: The UI acts as the sole human-facing graphical surface for the `.beads` tracker and Nightweaver daemon.

### 2. Modules to Build and Modify
- **Host HTTP Server Module**: Embedded lightweight HTTP server in the Nightweaver Orchestrator host triggered via `nightweaver dashboard` or `nightweaver serve --project <slug>`. Serves pre-compiled static SPA assets and exposes REST/SSE endpoints.
- **Tracker Bridge Service**: Interacts with the local Target Repository's `.beads` tracker via `bd` CLI queries and mutations (`bd list`, `bd show`, `bd comments add`, `bd update`, `bd create`).
- **Factory Telemetry Service**: Exposes in-memory slot states for Planning, Delivery, and Merger pools, active worktree paths, and Win32 Job Object runtime statistics.
- **Catch-Up & Rollup Service**: Queries `.beads` inventory via `TrackerBridge`, computes the full window delta (`closed_at >= since`, `created_at >= since`, or open `updated_at >= since`) against the requested window or the per-project checkpoint persisted at `<nightweaver-root>/telemetry/<project>/catch-up.json`, groups connected Beads (`parent` / `parent-child` and multi-hop `discovered-from` edges) into unified Lineage Threads plus Standalone items, and extracts best-effort outcome summaries (`close_reason` / `notes`, commit SHAs, PR refs, and state fallback).
- **Log Streaming Service**: Serves Server-Sent Events (SSE) streaming live logs from `<nightweaver-root>/logs/<project>/<bead-id>/<timestamp>-<role>.log`.

### 3. Key Data Contracts (Derived from Prototype)

#### Catch-Up Digest Schema (`GET /api/digest?window=auto|since_caught_up|24h|7d|30d|all` & `POST /api/digest/catch-up`)
```json
{
  "generatedAt": "2026-11-10T20:35:00Z",
  "window": "since_caught_up",
  "windowStart": "2026-11-09T20:35:00Z",
  "lastCaughtUpAt": "2026-11-09T20:35:00Z",
  "isCaughtUp": false,
  "metrics": {
    "completedTotal": 20,
    "completedFeatures": 0,
    "completedTasks": 6,
    "completedBugs": 14,
    "activeThreads": 4,
    "openOrUpdatedTotal": 1,
    "waitingForGuidance": 2
  },
  "threads": [
    {
      "root": {
        "id": "ns-e5i",
        "title": "Reduce full test suite wall time below five minutes",
        "type": "task",
        "priority": 1,
        "status": "closed",
        "stage": null,
        "pauseLabels": [],
        "activityKind": "closed",
        "activityAt": "2026-11-10T04:07:03Z",
        "outcome": "resolved",
        "outcomeLabel": "Resolved",
        "summary": "Validated lower-coverage suite merged into local main at 8a8679fdf54993da51aa4b051a487e4b0d263c65.",
        "commits": ["8a8679f"],
        "pullRequests": [],
        "fromId": null,
        "fromRelation": null
      },
      "completedCount": 7,
      "openCount": 0,
      "items": [
        {
          "id": "ns-dd3",
          "title": "Investigate published force-stop fixture log lock during post-body teardown",
          "type": "bug",
          "priority": 2,
          "status": "closed",
          "stage": null,
          "pauseLabels": [],
          "activityKind": "closed",
          "activityAt": "2026-11-10T15:35:57Z",
          "outcome": "resolved",
          "outcomeLabel": "Resolved",
          "summary": "Committed and merged verified changes; latest main 644/644 passes.",
          "commits": [],
          "pullRequests": [],
          "fromId": "ns-e5i",
          "fromRelation": "discovered-from"
        }
      ]
    }
  ],
  "standaloneCompleted": [],
  "standaloneOpen": []
}
```

```json
POST /api/digest/catch-up
{
  "caughtUpAt": "2026-11-10T20:35:00Z"
}
```

#### Attention Item Schema
```json
{
  "bead_id": "ns-99b",
  "type": "feature",
  "priority": "P1",
  "stage": "stage:plan",
  "label": "product-decision-required",
  "paused_duration": "4d ago",
  "from_role": "Planner Agent (claude-3-7-sonnet)",
  "question": "Should the daemon use a refreshed Bearer JWT stored in host DPAPI credentials, or rely on ephemeral session cookies via local proxy?",
  "repair_cycles": 0,
  "max_repair_cycles": 3
}
```

#### Decision Submission Contract
```json
POST /api/beads/{id}/decide
{
  "instruction": "Option 1. Use DPAPI Bearer JWT with 12h rotation. Proceed with planning.",
  "action": "resume",
  "expected": {
    "stage": "stage:plan",
    "pauseLabels": ["product-decision-required"],
    "repairCycles": 0
  }
}
```

`note_only` accepts only `instruction` and `action`; it appends context and preserves the pause. `resume` and `POST /api/beads/{id}/retry` require the displayed stage, full pause-label set, and repair count. The host compares that snapshot with current state before recording a comment, then performs one guarded update that clears both pause labels and `repair_cycles` while preserving the current stage and unrelated Bead fields. A mismatch, closed Bead, or active claim returns a refreshable conflict. Comments, edits, and audit history never authorize execution. Successful Resume/Retry makes the Bead eligible for normal dependency, capacity, stage, and claim scheduling; it does not launch an Agent directly. Abort closes without clearing its pause or repair budget.

## Testing Decisions

### Single Primary Testing Seam: The HTTP Host API Seam
- **The Seam**: The highest possible seam is the external HTTP API boundary of the Nightweaver Host server. Tests launch the host server (or use an in-process ASP.NET Core `TestServer` / `WebApplicationFactory`) targeting a disposable Git repository with an initialized `.beads` embedded Dolt tracker.
- **What Makes a Good Test**:
  - Tests verify **external behavior only**: sending HTTP requests and asserting on response payloads and verifiable side effects in the `bd` tracker and Git worktrees.
  - No coupling to internal controller classes, in-memory collection implementations, or private service methods.
  - Validates that `POST /api/beads` creates a genuine issue in `.beads` discoverable by `bd list`.
  - Validates that `POST /api/beads/{id}/decide` carries the displayed snapshot, rejects stale state before adding a comment, and performs a guarded resume that clears both pause labels and the repair budget while preserving stage and unrelated fields.
  - Validates that `GET /api/digest` computes the full window delta across closed and newly-opened/updated Beads, groups multi-hop `discovered-from` and `parent-child` lineage threads alongside standalone items, and `POST /api/digest/catch-up` atomically persists the checkpoint in `<nightweaver-root>/telemetry/<project>/catch-up.json`.
- **Prior Art**:
  - `PublishedCliIntegrationTests.cs` and `BeadsIntegrationTests.cs` in `tests/Nightweaver.Tests/`, which execute against real disposable Git repositories and verify external tool behavior under `TestSupport.cs`.

## Out of Scope
- Multi-tenant cloud SaaS deployment or user identity provider integrations (Nightweaver is a standalone single-user local factory).
- In-browser code editing or arbitrary terminal emulation (code edits belong in agent worktrees or local IDEs).
- Direct replacement or alternative storage for `.beads` (the UI strictly layers over the existing Dolt-backed Beads tracker).
- Graph-based dependency DAG canvas rendering (Variant C is explicitly excluded).

## Further Notes
- An interactive reference prototype demonstrating all UI layouts, typography rhythm, collapsible history behaviors, and data mockups is committed to the repository at `docs/design/nightweaver-ui-prototype.html`.
- The architectural brief and design exploration notes are recorded in `docs/design/ui-spec.md`.
- Stored project memory in Beads under key `ui-architecture-consensus`.
