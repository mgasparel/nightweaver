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
2. **"Since Last Visit" Catch-Up Digest**: A milestone-oriented rollup architecture designed for long user absences (days or weeks), grouping delivered slices into completed Product Feature packages, tracking a persistent `last_visited` checkpoint, and providing a single-click "Mark Caught Up" action.
3. **Human Attention Desk**: A prioritized intervention queue surfacing `product-decision-required` questions and `needs-human` escalations, supporting both instant one-line resolutions and deep context escalation.
4. **Dedicated Full Bead Workspace**: A large-surface reading and decision view featuring generous typography (15–16px body, relaxed line height), smart collapsible history (grouping older planning and review cycles under an expandable banner), spec inspection, runtime environment sidebar, and an integrated human guidance studio.
5. **Product Bead Intake Composer**: An intuitive interface to author and submit new Product Beads (`--type feature` or `--type bug`) directly into the tracker.
6. **Human-Friendly Beads Explorer**: A filterable, searchable inventory across all tracker states.

## User Stories

1. As a repository maintainer, I want to open a local dashboard via a single command, so that I can inspect my factory without configuring external servers or cloud services.
2. As a repository maintainer returning after two weeks away, I want a high-level summary of what shipped while I was gone, so that I can understand overall progress without scrolling through dozens of individual tasks.
3. As a repository maintainer, I want completed Plan Beads to be rolled up into their parent Product Features in the catch-up digest, so that I see cohesive milestones rather than fragmented implementation slices.
4. As a repository maintainer, I want an explicit "Last seen X days ago" indicator, so that I know exactly what time window the digest covers.
5. As a repository maintainer, I want a "Mark Caught Up" button, so that I can acknowledge recent accomplishments and reset my baseline checkpoint for future visits.
6. As a repository maintainer, I want a time-horizon selector (Since Last Visit, Last 48 Hours, Last 7 Days, All Time), so that I can explore factory velocity over different historical windows.
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
24. As a product owner, I want submitting guidance in the workspace to automatically post a comment to `bd comments` and remove the `product-decision-required` label, so that the agent resumes without manual CLI intervention.
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
- **Catch-Up & Rollup Service**: Queries closed beads and commits, groups child Plan Beads by their parent Product Bead via `discovered-from` metadata, and computes delta rollups against the user's persisted `last_visited` timestamp.
- **Log Streaming Service**: Serves Server-Sent Events (SSE) streaming live logs from `<nightweaver-root>/logs/<project>/<bead-id>/<timestamp>-<role>.log`.

### 3. Key Data Contracts (Derived from Prototype)

#### Catch-Up Digest Schema
```json
{
  "last_visited": "2026-10-12T08:00:00Z",
  "interval_label": "12 days ago (Oct 12)",
  "metrics": {
    "plan_beads_delivered": 18,
    "prs_merged": 14,
    "product_features_completed": 4,
    "items_needing_guidance": 2
  },
  "feature_rollups": [
    {
      "product_bead_id": "ns-xci",
      "title": "Worktree Isolation Spine",
      "slices_completed": 4,
      "total_slices": 4,
      "prs": ["#41", "#44"]
    }
  ]
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
  "action": "resume" // "resume" removes product-decision-required, "note_only" preserves label
}
```

## Testing Decisions

### Single Primary Testing Seam: The HTTP Host API Seam
- **The Seam**: The highest possible seam is the external HTTP API boundary of the Nightweaver Host server. Tests launch the host server (or use an in-process ASP.NET Core `TestServer` / `WebApplicationFactory`) targeting a disposable Git repository with an initialized `.beads` embedded Dolt tracker.
- **What Makes a Good Test**:
  - Tests verify **external behavior only**: sending HTTP requests and asserting on response payloads and verifiable side effects in the `bd` tracker and Git worktrees.
  - No coupling to internal controller classes, in-memory collection implementations, or private service methods.
  - Validates that `POST /api/beads` creates a genuine issue in `.beads` discoverable by `bd list`.
  - Validates that `POST /api/beads/{id}/decide` records a comment in `bd comments` and removes the `product-decision-required` label.
  - Validates that `GET /api/digest` correctly groups child Plan Beads into their parent Product Bead milestones based on `discovered-from` relations.
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
