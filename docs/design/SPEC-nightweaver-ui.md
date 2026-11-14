# Nightweaver Console Design Specification

The [console hierarchy and interaction contract](#console-hierarchy-and-interaction-contract-ns-1b1) below is authoritative for epic `ns-1b1` and children `ns-1b1.1`–`ns-1b1.8`. It specifies the intended implementation, not a claim that the current UI already satisfies it. The [architecture brief](ui-spec.md) provides representative layouts and integration acceptance checks. The prototype is illustrative only; this contract supersedes its defaults and simulated data.

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
1. **Factory Execution Bays**: Observed Planning Pool, Delivery Floor and Singleton Merger activity with independent capacities and real invocation logs. Planning requires explicit `ready-for-planning` authorization, native readiness and `max_active_plan_beads` backpressure; intake remains backlog-only. Older two-pool snapshots show Planning unavailable.
2. **"Since Last Caught Up" Catch-Up Digest**: A collapsible top-of-console window-delta digest designed for both daily check-ins and multi-week absences, grouping connected completed and newly-opened/progressed Beads into unified Lineage Threads (via `parent` / `parent-child` and multi-hop `discovered-from` chains) alongside standalone activity, surfacing best-effort outcome summaries (`close_reason` / commit SHAs / PR references with Bead state fallback), tracking a persistent per-project `last_caught_up_at` checkpoint under `<nightweaver-root>/telemetry/<project>/catch-up.json`, and providing a race-safe single-click "Mark Caught Up" action that collapses the digest after a successful checkpoint save.
3. **Human Attention Desk**: A prioritized intervention queue surfacing `product-decision-required` questions and `needs-human` escalations, supporting both instant one-line resolutions and deep context escalation.
4. **Dedicated Full Bead Workspace**: A large-surface reading and decision view featuring generous typography (15–16px body, relaxed line height), smart collapsible history (grouping older planning and review cycles under an expandable banner), spec inspection, runtime environment sidebar, and an integrated human guidance studio.
5. **Product Bead Intake Composer**: An intuitive interface to author and submit new Product Beads (`--type feature` or `--type bug`) directly into the tracker.
6. **Human-Friendly Beads Explorer**: A filterable, searchable inventory across all tracker states.

## User Stories

1. As a repository maintainer, I want to open a local dashboard via a single command, so that I can inspect my factory without configuring external servers or cloud services.
2. As a repository maintainer returning after hours, days, or weeks away, I want a collapsible Catch-Up Digest at the very top of the console showing the full delta of what completed and what was newly discovered/updated in the selected window, so that I can understand overall progress and new follow-ups without scrolling through dozens of individual tasks.
3. As a repository maintainer, I want connected Beads (both `parent-child` and multi-hop `discovered-from` chains, mixing closed and still-open items) rolled up into unified Lineage Threads alongside a clean Standalone Activity list, with best-effort outcome summaries (`close_reason`, extracted commit SHAs/PRs, and Bead state fallback), so that multi-step investigations and feature slices read as coherent stories while standalone tasks and bugs remain visible.
4. As a repository maintainer, I want an explicit checkpoint status indicator and 4 grounded tracker counters (`Completed`, `Active Threads`, `Open / Updated`, and `Waiting for You`), so that I know the exact time window and health at a glance even when the digest is collapsed.
5. As a repository maintainer, I want a race-safe "Mark Caught Up" button that records the loaded snapshot's `generatedAt` timestamp to `<nightweaver-root>/telemetry/<project>/catch-up.json` and collapses the digest after a successful save, while keeping newer activity in `Since Caught Up`, so that I can reset my baseline without hiding work completed while I was reading.
6. As a repository maintainer, I want time-window preset pills (`Since Caught Up`, `24h`, `7d`, `30d`, `All`), defaulting to `24h` when no checkpoint has been set yet, so that I can explore factory velocity over different historical windows without losing my saved checkpoint.
7. As a product owner, I want a pinned Human Attention Desk at the top of the console, so that I immediately see any beads halting agents for guidance.
8. As a product owner, I want to read the Planner Agent's exact clarification question on a `product-decision-required` bead, so that I understand what architectural ambiguity stopped planning.
9. As a product owner, I want a quick inline reply box on the attention card, so that I can resolve straightforward one-line decisions in seconds.
10. As a product owner, I want an "Open Full Bead Workspace" link on every attention card, so that I can review full specification context when a decision is complex.
11. As a repository maintainer, I want to see `needs-human` escalation cards when an agent exhausts its repair cycles, so that I know why verification failed.
12. As a repository maintainer, I want one-click actions on `needs-human` cards to reset the repair budget and retry, copy the checkout command, or abort the bead, so that I can resolve blockers with minimal friction.
13. As an operator, I want Planning to be labeled unavailable when the connected runtime supplies no Planning observations, so that missing or legacy telemetry never appears idle or live.
14. As an operator, I want real Planning invocations, capacity, logs and unavailable observations to be visible alongside Delivery and Merger.
15. As an operator, I want real-time visibility into the Delivery Floor slots, so that I can monitor isolated worktrees, active branches, diff counters, and repair cycles.
16. As an operator, I want real-time visibility into the Singleton Merger Slot, so that I can watch serialized branch rebases, conflict resolution, and remote GitHub check polling.
17. As an operator, I want to open the selected slot's invocation dialog, so that I can inspect real live or retained output and observed timing without switching to a terminal.
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
28. As an operator, I want a newly submitted Product Bead to remain discoverable, including through Show in All if absent from Ready. Creation saves backlog work; it does not authorize automated Planning.
29. As a repository maintainer, I want a filterable Beads Explorer table (Ready, Active, Blocked, All, Closed), so that I can explore my entire issue tracker visually.
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

### 3. Key Data Contracts

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

#### Attention Item Schema (`GET /api/attention`, array of items)

The camelCase wire schema is defined by `AttentionDeskItem` in `src/Nightweaver.Web/AttentionDesk.cs`. A representative item follows; unknown source/timing/model information must remain explicitly unknown rather than fabricated.

```json
{
  "beadId": "ns-99b",
  "title": "Choose an implementation approach",
  "type": "task",
  "priority": 1,
  "status": "open",
  "labels": ["stage:implement", "product-decision-required"],
  "stage": "stage:implement",
  "kind": "product-decision-required",
  "pausedAt": "2026-11-12T12:00:00Z",
  "pauseTimeSource": "comment",
  "fromRole": "implementer",
  "question": "Which of the two approaches should the implementation use?",
  "diagnostic": null,
  "repairCycles": 0,
  "maxRepairCycles": 3,
  "canDecide": true,
  "canRetry": false,
  "canAbort": false,
  "unavailableReason": null,
  "sourceAuthor": "Implementer"
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

## Console hierarchy and interaction contract (ns-1b1)

### Authority and scope

This is the shared implementation contract established by `ns-1b1.1`. The corresponding design fields on `ns-1b1.1` and parent `ns-1b1` carry this same contract. Changes to these requirements must be reconciled in both documents and both design fields. Repository lifecycle and mutation authority remain defined by [SPEC.md](../../SPEC.md), [GLOSSARY.md](../../GLOSSARY.md), and [ADR 0002](../adr/0002-beads-native-agent-handoff-and-thin-orchestrator.md).

`ns-1b1.1` changes documentation and the two authorized design fields only. Production HTML, CSS, JavaScript, and C# endpoints belong to downstream implementation children. Constants, classes, and controls described below are required target contracts; the hook inventory distinguishes additions from existing hooks. No automated Planning, graph canvas, simulated runtime status, or alternative tracker is introduced.

### Page order and desktop viewport budget

Within `#console-deck`, use this document order: compact Catch-Up Digest (`#catch-up`), pinned Human Attention Desk (`#attention`), Factory Execution Bays (`#factory`) with integrated runtime health, then bounded Beads Explorer (`#beads`). The `.site-header` precedes the deck. Invocation inspection is a modal detail path from a slot, not another full-height overview section. The Full Bead Workspace (`#bead-workspace`, route `#bead/<encoded-id>`) remains a dedicated reading surface.

At 1440×900, the initial, unexpanded overview must expose the runtime health, attention summary, and execution overview without scrolling through history. Use this illustrative busy-state allocation, including internal padding:

| Region | Target height | Initial content |
| --- | --- | --- |
| `.site-header` | 60–68px | Brand, section links, `#new-bead-trigger` |
| Collapsed `#catch-up` | 76–96px; under 6rem | Checkpoint, window/actions, single-row inline metrics |
| Busy `#attention` | about 280px | Heading/count, two compact cards side by side, disclosure |
| `#factory` | about 340px | Health header, Planning, Delivery and Merger overview |
| Shell top inset and section gaps | about 76px | 1rem top inset, 1.25rem section rhythm |

At the upper header/digest targets this totals about 860px. Explorer begins immediately afterward; it need not fit fully into the busy initial viewport. Heights are layout budgets, not fixed clipping heights. Long content must wrap or use named disclosures and detail paths. The busy fixture includes observed Planning slots alongside two occupied Delivery slots, one idle Delivery slot, one occupied Merger slot, and four paused Beads (two previewed). Higher configured capacity may extend below the fold while health, occupancy, and the start of each execution bay remain visible. Idle/caught-up state should expose the Explorer heading and initial results in the first viewport.

The header uses `min-height: 3.75rem` and `padding-block: 0.5rem`. Its brand targets `#main-content`; preserve section jumps to `#catch-up`, `#attention`, `#factory`, and `#beads`. “Pinned” means the Attention Desk remains ahead of Factory in the page hierarchy, not a fixed overlay that covers controls. Keep the existing mobile navigation and skip link.

### Shared visual and responsive language

Use existing surface tokens consistently: `--color-page: #0b1120`, `--color-header: #101827`, `--color-surface: #141f30`, `--color-surface-raised: #1a293c`, `--color-border: #34445b`, `--color-text: #f1f5f9`, `--color-secondary: #cbd5e1`, `--color-muted: #aab7c8`, `--color-accent: #7dd3fc`, and `--color-accent-strong: #38bdf8`. Use semantic emerald `#34d399`, amber `#fbbf24`, and rose `#fb7185` accents with readable text and borders. Preserve visible focus outlines (3px accent, 3px offset), selected/pressed treatments, disabled affordances, and hover feedback. Status is never communicated by color alone.

Section headings: `clamp(1.2rem, 2vw, 1.45rem)`, weight 700, tracking -0.02em. Card/slot headings: 1–1.05rem, weight 650, line-height 1.35. Dense operational body: 0.9375rem; Workspace body: 1rem with line-height 1.65. Identifiers, paths, branches, SHAs and output: `ui-monospace, SFMono-Regular, Menlo, Consolas, monospace`, 0.8125–0.875rem. Use a 0.25rem spacing unit with 0.5rem control gaps, 0.75–1rem card padding, and 1.25rem section separation; `.page-shell` padding-block is `1rem 3.5rem`. Retain coherent 1rem card radii.

| Viewport | Required reflow |
| --- | --- |
| 1440×900 at 100% | Shell width `min(100% - 2.5rem, 80rem)`; Delivery/Merger columns `minmax(0, 1.65fr) minmax(0, 1fr)`; Planning is a compact spanning notice. Two attention previews share a row. Collapsed metrics occupy one inline row. |
| 200% zoom on 1440px desktop (720px CSS width) | `@media (max-width: 52rem)` stacks bays and attention previews; wraps header, metrics, filters and actions; metadata becomes one column when needed. Vertical scrolling is expected. Do not shrink text to preserve the desktop budget. |
| 375px narrow viewport | Shell width `calc(100% - 1.5rem)`; same stacked hierarchy; mobile menu remains keyboard operable. Wrap action groups and long values, keep controls reachable and labels readable. |

Use `min-width: 0` on grid/flex children and `overflow-wrap: anywhere` for long paths/identifiers/titles. Responsive Explorer rows must keep Bead identity and navigation accessible. Only terminal output and code/table detail regions may scroll horizontally inside a bounded container; no page-level horizontal overflow (`document.documentElement.scrollWidth <= clientWidth`). Do not hide overflow on the page to mask inaccessible content. At narrow widths, dialog width is `calc(100vw - 2rem)`; metadata columns may shrink below 12rem to avoid overflow.

Use explicit glyph/text pairs: `● Live` / `● Running`, `◐ Preparing`, `◌ Idle`, `✓ Clear`, `⏸ Decision Needed`, `! Needs Human`, `✕ Failed` / `✕ Timed Out`, `⚠ Stale`, `✕ Disconnected`, and `— Unavailable`. Include meaningful text for assistive technology; decorative glyphs may be aria-hidden. Missing observations remain unavailable, never fabricated zeroes or live states. Loading, empty, error and retained-data states must be distinct.

### Catch-Up Digest — ns-1b1.2

Initialize `digestManualCollapse = true` on every fresh load, independent of volume or `isCaughtUp`. Initial HTML must agree: `#digest-body[hidden]`, `#digest-toggle[aria-expanded="false"]`, label “Expand”, and `aria-controls="digest-body"`. The JavaScript variable `digestSection` refers to `#catch-up`; there is no `#digest-section` ID. If using a collapsed-state attribute for CSS, put `data-collapsed="true|false"` on `#catch-up` and keep it synchronized with hidden/expanded state.

Keep `#digest-checkpoint-badge`, `#digest-metrics`, all four metric values/subtexts, window selector and actions accessible while collapsed. Metrics read Completed, Active Threads, Open / Updated, and Waiting for You (link to `#attention`). Use compact inline pills, not tall metric cards. Condense the caught-up banner into the checkpoint/summary while collapsed; full `#digest-caught-up-bar` copy can appear on expansion. Desktop `.digest-panel` padding is `0.75rem 1rem` when collapsed; inline metrics use flex, a 0.5rem gap, and wrap only when available width requires it.

`GET /api/digest?window=auto|since_caught_up|24h|7d|30d|all` retains its camelCase response and existing lineage semantics. Initial `auto` resolves to Since Caught Up if a checkpoint exists, otherwise 24h. Selecting any `data-digest-window` preset or View Last 24h expands the body; the Expand/Collapse button toggles it independently of window/checkpoint. Refresh and Retry preserve manual disclosure. A successful `POST /api/digest/catch-up` submits `{ caughtUpAt: currentDigest.generatedAt }`, verifies the saved checkpoint, reloads `since_caught_up`, and collapses even if newer activity arrived. Never substitute click-time `now` or hide newer activity by advancing past the loaded snapshot. Failure preserves the last confirmed checkpoint/data and exposes `#digest-error` with `#digest-retry`.

Expansion initially shows up to `DIGEST_INITIAL_THREADS = 5` compact thread summaries in `#digest-threads` and `DIGEST_INITIAL_STANDALONE = 6` combined standalone completed/open items in `#digest-standalone`. Each thread shows linked root identity/title, outcome or origin context, recent activity and aggregate counts. All child activity and root descriptions/evidence start hidden behind an independent “Show activity” / “Hide activity” disclosure, including threads with one to four children. Opening activity reveals root details plus up to `DIGEST_INITIAL_THREAD_ITEMS = 4` children; “Show N more in thread” / “Show fewer in thread” exposes all children or restores the preview. This ns-1b1.8 default supersedes the previous four-initially-visible-children contract. Preserve source ordering and totals, “Show N more threads” / “Show fewer threads”, and “Show N more standalone items” / “Show fewer standalone items”. Summaries longer than 140 characters retain `.is-clamped` with “Show more” / “Show less” inside requested activity; full text and Bead navigation remain reachable. Thread/activity/summary disclosure is keyed by stable Bead identity; new window selection resets collection bounds and activity, while refresh preserves still-valid disclosure state and focused controls. New threads start collapsed. If refresh moves the focused surviving thread beyond the initial five, reveal the full thread collection before restoring focus; Show fewer restores the five-thread preview. Disclosure controls have unique targets and accurate aria-expanded/aria-controls.

### Factory Execution Bays — ns-1b1.3

Keep `.bay-grid`, `#delivery-slots`, `#merger-slots`, `#delivery-pool-status` and `#merger-pool-status`. Pool headers report observed occupancy honestly, e.g. “1 of 3 slots active · 2 idle” or “0 of 1 slot active · Idle”. On stale data explicitly qualify these counts as last observed. Do not infer occupancy from the tracker’s Active filter. The Planning bay uses `#planning-slots` and `#planning-pool-status` with the same observed slot/log interactions. Older two-pool snapshots show Planning unavailable, never fabricated idle or live activity.

For `status !== "idle"`, a `.factory-slot` presents these visible summary rows:

1. Pool/slot identifier and glyph/text state badge.
2. Prominent Bead link to `#bead/<encoded-id>` with `data-bead-workspace-link="true"`, plus observed role and stage.
3. Started time and deadline from `slot.invocation.started_at` / `deadline_at`, repair badge `Repairs: X / Y` when present, and compact `+A / −D` when `slot.diff.status === "available"`. Missing values are labeled unavailable rather than invented.
4. `button[data-open-telemetry-slot="<slot_id>"]`: “Open live invocation” for a running slot with an invocation, “Open retained invocation” otherwise when an invocation is available. No stream action when no invocation exists.

Secondary fields live in initially collapsed `<details class="slot-diagnostics"><summary>Diagnostics</summary>…</details>`: Invocation ID, CLI, Configured model, Actual model, Branch, Worktree path, and detailed diff (binary/untracked). Actual model must not be inferred from configured model. Preserve diagnostic open state keyed by `slot.slot_id` across 2-second `refreshFactory()` polls within the same runtime; reset for a different runtime to avoid transferring context to unrelated slots. Preserve focused controls across changed snapshots. `status === "idle"` renders a single-line `.factory-slot.factory-slot-idle` (“delivery-1 · ◌ Idle — Available for admission”) with no repeated Unknown metadata. A stale retained idle row must remain visibly last observed rather than claim current availability.

### Runtime health and invocation inspection — ns-1b1.4

Move existing `#factory-status` from below `.bay-grid` into `.factory-health-bar` in the Factory header immediately above the bays. `ns-1b1.4` owns this relocation/styling; `ns-1b1.3` owns bay layout/slot diagnostics and must leave a compatible header position. Include runtime identity, observed/heartbeat time, and Target Repository; long identity/path details may wrap or disclose within that header without crowding the concise state.

`GET /api/factory/state` is polled every 2 seconds with no query parameters. Its existing snake_case envelope is `{ availability, reason, snapshot }`; snapshot identity is `runtime_id`, version is `revision`, and freshness is `observed_at`. Keep validated snapshots when connection/freshness fails and mark them honestly. Presentation `data-state="live|idle|stale|disconnected|unavailable"` is not a new API enum:

| Presentation | Meaning and copy |
| --- | --- |
| live | Fresh validated runtime: `● Live · Runtime <id> · Observed <time> · <target_repository>`. |
| idle | Fresh runtime with no occupied slots: `◌ Idle · Runtime <id> · Observed <time>`; retain explicit fresh/live runtime context. Idle is derived occupancy, not API availability. |
| stale | API reports stale: `⚠ Stale · Runtime <id> · Last heartbeat <time> · Showing last validated snapshot`. |
| disconnected | Transport failure after prior data: `✕ Disconnected · Showing last validated snapshot` with last observation time. |
| unavailable | No validated data or API unavailable: `— Unavailable · No active runtime observations (<reason>)`; if retained data exists explicitly label it last validated. |

The `#invocation-dialog` remains a native modal, with coherent surface/backdrop styling, width `min(64rem, calc(100vw - 2rem))`, max-height `min(88vh, 56rem)` and an internally scrollable body. Header contains read-only telemetry eyebrow, `#invocation-title` (“<Role> · <Bead ID>”), and `#invocation-close`. `#invocation-status` uses glyph/text status for Connecting, live/running, reconnecting/disconnected, unavailable, and ended/exited/failed/timed-out observations. A stream disconnect is not proof the Agent stopped.

`#invocation-metadata` is a definition grid using `repeat(auto-fit, minmax(12rem, 1fr))` with the narrow-width override above; monospace values wrap. Show Runtime, Slot, Invocation, Bead, CLI, models, Worktree and Branch. Keep `#invocation-pause` (“Pause follow” / “Resume follow”) and `#invocation-latest` (“Jump to latest”) in a sticky toolbar within the dialog scroll region. `#invocation-output` is scrollable monospace output, max-height 50vh, background `#070b14`; preserve the existing 2,000-line / 256 KiB display bound, ANSI/control sanitization, cursor de-duplication, reset handling, and `#invocation-truncation` notice.

Select streams by the exact `runtime_id`, `slot_id`, and `invocation_id` in `/api/telemetry/stream`; preserve EventSource reconnect and Last-Event-ID behavior and stop retrying after an end event. Factory polls must not close, retarget, clear output, reset follow mode, change scroll position or steal focus from a valid open inspection. A replacement invocation in the same slot must never silently replace the selected stream; retain its output with honest ended/unavailable messaging. Escape and Close terminate the stream and restore focus to the originating slot CTA, or the Factory heading when that CTA no longer exists (make the heading programmatically focusable). Hash navigation closes inspection without restoring focus over the destination. Keep focus inside the modal while open.

### Bounded Beads Explorer — ns-1b1.5

Fresh visits use `selectedView = "ready"`, `data-view="ready"` pressed and `data-view="all"` unpressed in both initial HTML and JavaScript. Filter order is Ready, Active, Blocked, All, Closed. Preserve the explicit explanation: “Active means issues stored as in progress. It does not indicate a live factory slot.” Ready continues to mean the server’s existing readiness view, not permission to dispatch or a promise that every Product appears there.

Use `EXPLORER_PAGE_SIZE = 15` rendered rows per page for every view and search. Keep server-side full-dataset filtering/search via `/api/beads?view=<view>&q=<query>`; the current endpoint returns the complete matching array. Paginate that returned list in the browser, not the visible page before searching. This contract requires no new page/size API parameters or server schema. Preserve returned ordering and existing stale-request cancellation/generation guards.

Introduce `#beads-pagination` with visible “Showing X–Y of N beads”, “Page X of Y”, `#beads-prev-page` (Previous) and `#beads-next-page` (Next). Disable unavailable directions; for zero results show “Showing 0–0 of 0 beads”, “Page 1 of 1” with both disabled and the correct empty/no-match message. Filter/search changes reset `explorerPage = 1`; refresh clamps it to `[1, max(1, ceil(N / 15))]`. Keep pagination controls stable in the DOM, preserve keyboard focus, and announce the new range via `#beads-live` without reading the entire table.

After `POST /api/beads`, retain the created ID/success notice, clear search and select Ready as the existing flow does. Locate `createdBeadId` across the full returned matching list and set the page containing it when present. If absent, preserve “Show in All” (`#beads-show-created`), switching to All and searching that ID. If refresh fails, keep the success ID visible and provide Retry; never imply the successful creation failed or submit again automatically. Intake saves an open, unassigned Product in backlog and does not start Planning.

### Attention bounds and integration — ns-1b1.6

`ns-1b1.6` owns Attention preview bounds and shared final polish, in addition to integrated QA. `ATTENTION_INITIAL_LIMIT = 2`; keep the server’s priority order and show the full paused count, but initially render up to two compact attention cards. `#attention-show-all.attention-more-toggle` appears only for N > 2: “Show all N paused Beads (M more)” / “Show fewer paused Beads”. Its expanded state and `aria-controls="attention-cards"` stay accurate. Clear state is one compact row (~56px) combining heading, `#attention-status` (“The Attention Desk is clear.”), `#attention-empty` and `#attention-refresh`, without redundant tall empty cards. Loading is not clear; failed reads are not zero paused work.

Busy previews retain identity, pause reason/stage, repair count, short current context, Full Bead Workspace link, and guarded actions. Long questions/diagnostics have keyboard-accessible disclosure or the Workspace detail path, never silent loss of the current question. Preserve drafts, action progress, show-all state and focus during refresh; if collapsing would hide focused content, move focus to Show all/fewer first. Expanded queues may extend below the fold. Keep `data-attention-focus` and card `data-bead-id` identity hooks.

The preview shows the first 140 characters of current context. “Read full context & pause details” exposes the complete question/diagnostic, source attribution and timing; “Write guidance” opens the existing draft and guarded Save Note / Send & Resume actions. Both disclosures retain their state per Bead across refresh and queue collapse. A removed focused card falls back to the queue toggle or Attention status. Full Bead Workspace links retain focus by Bead identity.

Preserve `POST /api/beads/{id}/decide` (`note_only` / `resume`), `/retry`, `/abort`, and Workspace readiness behavior. Resume/Retry carry the displayed stage, full pause-label set and repair count; conflicts require refresh and a new explicit action. Save Note never authorizes scheduling. Abort retains pause labels and repair budget. No visual refresh may erase in-progress guidance or bypass these guards.

### Hook ownership and compatibility inventory

Existing hooks must survive section reflow and remain unique. This is a compatibility map, not permission to rename adjacent controls:

| Owner | Preserve existing DOM hooks and semantics | Required additions/changes |
| --- | --- | --- |
| ns-1b1.2 | `#catch-up`, `#digest-body`, `#digest-toggle`, `#digest-metrics`, `#digest-checkpoint-badge`, `#digest-threads`, `#digest-standalone`, `#digest-status`, `#digest-error`, `#digest-retry`, `#digest-refresh`, `#digest-mark-caught-up`, `#digest-caught-up-bar`, `data-digest-window`, `data-bead-workspace-link`, thread `data-root-bead-id` | Initial hidden/expanded state, optional `#catch-up[data-collapsed]`, bounded collection disclosures/constants. |
| ns-1b1.3; Planning integration ns-jbp.11 | `.bay-grid`, `.bay-card`, `#planning-slots`, `#delivery-slots`, `#merger-slots`, `#planning-pool-status`, `#delivery-pool-status`, `#merger-pool-status`, `.factory-slot`, `data-status`, `data-open-telemetry-slot` | `.factory-slot-idle`, `.slot-diagnostics`, Bead workspace link, stable slot disclosure/focus state; Planning uses the same observed interactions, with unavailable state for legacy snapshots. |
| ns-1b1.4 | `#factory-status`, every `#invocation-*` ID described above, slot telemetry CTA identity | Header `.factory-health-bar`, coherent `data-state`, responsive dialog, sticky follow controls, safe focus restoration. |
| ns-1b1.5 | `#beads`, `data-view`, `#beads-search`, `#beads-table-body`, `#beads-live`, `#beads-loading`, `#beads-empty`, `#beads-no-matches`, `#beads-error`, `#beads-retry`, `#beads-create-success`, `#beads-show-created`, workspace links | Ready default, page-size constant and page state, `#beads-pagination`, `#beads-prev-page`, `#beads-next-page`. |
| ns-1b1.6 | `.site-header`, navigation/skip link, `#attention`, `#attention-status`, `#attention-empty`, `#attention-cards`, `#attention-error`, `#attention-retry`, `#attention-refresh`, `data-attention-focus`, `data-bead-id`, intake and Workspace hooks | Attention preview constant/toggle, shared spacing/typography/status/focus polish and integrated acceptance evidence. |

Each child verifies its changed runtime surfaces using the isolated Aspire and Playwright workflow in `tests/Nightweaver.QA.AppHost/README.md`, discovering the dynamic direct web endpoint and stopping the same stack afterward. No desktop Computer Use for browser QA. `ns-1b1.6` records representative before/after screenshots and functional/visual checks for busy, idle/caught-up, stale/disconnected/unavailable/error, long-content/large-tracker, expanded-history, diagnostics and invocation states at 1440×900, 375px and 720 CSS px (200% of 1440). Require no uncaught browser errors, page overflow, clipped actions or lost focus. Verify cross-refresh interaction as well as static appearance; see the [acceptance matrix](ui-spec.md#integration-acceptance-matrix).

For this specification-only child, no Aspire stack or browser run is required. Run `dotnet build Nightweaver.slnx -warnaserror` and the complete `dotnet test --solution Nightweaver.slnx` with a positive `--minimum-expected-tests`, normal committed six-thread parallelism, and actual wall time including suite build, fixture publishing and teardown under five minutes. Record failures and timing honestly. Check the branch diff for whitespace errors and leave no new uncommitted files; keep the implementation Bead open until its merge is verified by the Orchestrator.


## Tracked Bead workflow presentation

The console names Product Bead (native feature/bug) and Plan Bead (native task) separately from native Type. Epic and custom types remain valid tracker types outside Nightweaver's supported lifecycle. Manual Plans may have no Product link. Discovered-from is provenance, not hierarchy or generated-plan approval.

Workspace, Explorer, Attention and catch-up rows consume one read-only presentation. A tracked stage (Planning, Plan review, Plan approved, Implementation, Review, QA or Merge) never establishes a running Agent. Tracker ownership and in_progress are stored facts; Environment and execution bays retain their own live, stale and unavailable observations.

Products show Awaiting Planning authorization, Authorized for Planning (subject to native readiness and capacity), or Plan published / awaiting Delivery. Terminal planned Products show Plan published without promising Delivery or merge. The planned label alone does not verify a generated graph. Publication hold, incomplete observation and Publication needs attention use the runtime publication gate over the available inventory; manual provenance does not grant generated authority. A held partial publication without a graph snapshot remains unverified; an independently observed Product identity or revision conflict still shows Publication needs attention.

Compact summaries keep raw labels in a disclosure. Workspace Workflow details contain planning, publication, pauses, tracker ownership, used/configured repair budget, uncertainty and wrapped source state. Conflicting stages/pauses and invalid repair metadata stay unavailable. A planned Product can retain historical technical attention without making Retry available. Viewing and prose edits grant no workflow authority; guarded Planning, Resume/Retry and independent Housekeeping actions retain their existing owners.
