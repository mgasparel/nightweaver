# Nightweaver UI Architecture: Synthesized Factory Console

The console combines Factory Mission Control, a compact Catch-Up Digest, and a dedicated Full Bead Workspace. Variant C (Assembly Graph) remains excluded. This brief and the [authoritative hierarchy and interaction contract](SPEC-nightweaver-ui.md#console-hierarchy-and-interaction-contract-ns-1b1) define epic `ns-1b1`; the detailed contract owns exact hooks, API semantics, constants and child boundaries. Neither document claims the downstream runtime changes have shipped.

## Operational hierarchy

The first screen answers: what needs me, what is running, and can I trust the observation? History and tracker inventory have explicit, bounded entry points.

```text
.site-header                        Brand · section links · + New Feature / Bug
#console-deck
  #catch-up                         Collapsed checkpoint + inline summary metrics
  #attention                        Clear row or two-card paused preview + Show all
  #factory
    #factory-status                 Runtime health in the section header
    .bay-card.planning-disabled      Compact manual Planning notice
    .bay-grid                       Delivery Floor | Singleton Merger
      occupied .factory-slot        Bead · role/stage · state · timing · repairs · diff
        .slot-diagnostics           Invocation/model/branch/worktree details on demand
        invocation CTA              Live or retained output in #invocation-dialog
      .factory-slot-idle            One-line idle observation
  #beads                            Ready · Active · Blocked · All · Closed
                                    15 rows/page · range · Previous / Next
#bead-workspace                     Separate full reading and guidance view
```

The Attention Desk is pinned by document order, ahead of Factory; it does not overlay controls. Invocation telemetry is a modal detail path. Manual Planning remains explicit: only Delivery and singleton Merger execute in the current continuous runtime. Intake creates backlog work, not automatic Planning authorization. Tracker Active means stored in progress, not a live slot.

## Representative layouts

### Busy: 1440×900 desktop

Use approximately 64px of header, 84px of collapsed digest, 280px of Attention preview, 340px of Factory, and 76px of shell inset/gaps: approximately 844px total. These are layout targets with internal padding, not clipping heights. Runtime health, the attention count and execution overview must be visible in the initial viewport. Explorer begins immediately below.

```text
┌ Nightweaver          Catch-Up · Attention · Factory · Beads        + New ┐
│ Catch-Up · checkpoint   [Since Caught Up  24h  7d  30d  All]            │
│ 20 Completed · 4 Active Threads · 1 Open / Updated · 4 Waiting          │
│                                      Mark Caught Up · Refresh · Expand │
├ Attention · 4 paused                         Show all 4 (2 more)        ┤
│ ns-a · Decision Needed             │ ns-b · Needs Human                 │
│ short context + actions/workspace  │ short diagnostic + actions         │
├ Factory · ● Live · Runtime … · Observed … · repository …                ┤
│ Planning Pool · Manual planning mode (automated Planning disabled)      │
│ Delivery: 2 of 3 active             │ Merger: 1 of 1 active              │
│ ns-c · Implementer · ● Running      │ ns-e · Merger · ● Running          │
│ Started … Deadline … Repairs +/−   │ Started … Deadline … Repairs +/−   │
│ ▸ Diagnostics · Open live          │ ▸ Diagnostics · Open live          │
│ ns-d · Reviewer · ● Running …      │                                    │
│ delivery-3 · ◌ Idle                │                                    │
└ Beads Explorer · Ready selected · Showing 1–15 of 200 beads              ┘
```

The sketch conveys order and grouping; exact rows may combine within the height budget. Busy Attention cards sit side by side, with current context disclosed rather than expanded histories. Higher slot capacities may extend below the fold while pool occupancy and the beginning of both bays remain visible.

### Idle and caught up

Header → collapsed digest with Caught Up checkpoint → single-line Attention clear row (~56px) → fresh runtime `◌ Idle` with runtime identity and observation time → single-line idle slots → Ready Explorer heading and initial results. Fresh runtime availability remains explicit even with no occupied slots. Do not fill empty slots with Unknown metadata. Do not infer caught-up state from a failed read.

### Stale, disconnected, unavailable and error

Keep the hierarchy stable. The health header uses `⚠ Stale` for an aged snapshot, `✕ Disconnected` for a failed connection with prior observations, and `— Unavailable` when observations are unavailable. Include the last observation time when present; retained occupancy is last observed, never current admission availability. Paused/failed cards use `⏸ Decision Needed`, `! Needs Human`, `✕ Failed` or `✕ Timed Out` with text as well as color.

`#digest-error`, `#attention-error`, and `#beads-error` present Retry and preserve last confirmed data where available, explicitly marked as retained. Loading has its own status; an empty response has a purposeful empty/no-match state. Invocation disconnection reports reconnecting without implying the Agent exited. Failed mutations preserve drafts and show conflict/retry context; an audit comment alone never means Resume succeeded.

### Long content, large tracker, mobile and zoom

At 375px width and 200% zoom on a 1440px desktop (720px CSS width), `@media (max-width: 52rem)` stacks bays and Attention previews, wraps filters/metrics/actions, and reduces metadata to a single column as needed. Use shell width `calc(100% - 1.5rem)` at 375px; desktop uses `min(100% - 2.5rem, 80rem)`. Vertical scrolling is expected after reflow. Text size stays readable; no page-level horizontal overflow or offscreen controls.

Exercise 200-character titles, deep Windows worktree paths, 40-character SHAs, 50+ digest items and 200+ Beads. Long identifiers wrap; only bounded output/code/table detail regions may scroll internally. Detail buttons expose full content. Fresh digest is always collapsed; expanded results initially show 5 threads, 4 children/thread plus root, and 6 combined standalone items, with summaries longer than 140 characters clamped until Show more. Every Explorer filter/search renders at most 15 rows per page, with visible counts/navigation. Search still queries the full matching server dataset.

## Interaction and visual rules

- Initial digest HTML and state agree: `#digest-body[hidden]`, toggle `aria-expanded="false"`, label Expand, `digestManualCollapse = true`. Window pills expand. Refresh preserves disclosure; successful Mark Caught Up saves the loaded `generatedAt`, selects Since Caught Up and collapses, even if newer activity exists. `auto` uses the checkpoint or falls back to 24h. The available presets are Since Caught Up, 24h, 7d, 30d, All.
- Attention uses `ATTENTION_INITIAL_LIMIT = 2`; Show all N paused Beads (M more) / Show fewer keeps the count and all guarded actions reachable. Current questions/diagnostics have disclosure and Workspace paths. Preserve drafts/focus and do not collapse a focused card without moving focus to the toggle.
- Slot summaries show Bead, role/stage, observed state, timing, repairs and diff. Diagnostics hold Invocation ID, CLI, configured/actual model, branch, worktree and detailed diff. State is preserved across 2-second polls within the same runtime. Invocation controls open the exact selected identity, preserve output/follow/scroll/focus across polls, and never silently retarget to a replacement invocation.
- Fresh Explorer uses `selectedView = "ready"`; `EXPLORER_PAGE_SIZE = 15`. Show X–Y of N, Page X of Y, Previous and Next. Search/filter resets page 1; refresh clamps the current page. After intake, find the created ID across the returned Ready array and select its page; otherwise preserve Show in All. An empty list shows 0–0 of 0 and disabled navigation.
- Use the existing dark canvas/surface/raised/border/text/accent tokens from the detailed contract; 0.25rem spacing units, 0.75–1rem card padding, 1.25rem section gaps, and a compact 3.75rem minimum header. Operational text is 15px; Workspace remains 16px with line-height 1.65. Monospace metadata wraps. Text/glyph status, visible focus, pressed/hover/disabled controls, and accessible disclosure are shared requirements.
- Keep the native invocation modal, Escape/Close, focus containment and restoration to the opening CTA (Factory heading fallback). Use responsive metadata and sticky Pause follow / Jump to latest controls. Preserve bounded output, reconnect/de-duplication and end behavior. Do not interpret log silence as idle or failure.

## Full Bead Workspace and human guidance

The dedicated `#bead/<encoded-id>` view retains specification reading, current context, collapsible history, environment metadata and spacious guidance. At desktop it uses a main reading column with metadata sidebar; at narrow width it stacks. Older iterations may collapse, while the current question/failure stays prominent. Preserve intake and Workspace IDs and `data-bead-workspace-link` navigation across all overview sections.

Save Note (`note_only`) supplies context only. Resume and Retry require the displayed stage, complete pause-label set, and repair count. The host verifies current state and applies guarded updates; stale, closed or claimed work requires refresh and a new explicit action. Successful Resume/Retry makes work eligible for ordinary scheduling, not immediate dispatch. Abort retains pause state and repair budget. Workspace Ready for Planning remains a distinct explicit authorization; continuous automated Planning is still out of scope.

## Integration acceptance matrix

`ns-1b1.1` owns this specification and reconciliation of both design fields. `ns-1b1.2` owns digest density/bounds, `ns-1b1.3` owns bay summaries/diagnostics, `ns-1b1.4` owns health placement/dialog polish, `ns-1b1.5` owns Explorer default/pagination, and `ns-1b1.6` owns Attention bounds, shared polish and integrated evidence. Preserve the detailed contract’s DOM/API compatibility inventory; additions there are target hooks, not claims of existing markup.

| Scenario | Functional and visual evidence |
| --- | --- |
| Busy desktop | Two attention previews from four paused Beads, two occupied plus one idle Delivery slots and one occupied Merger; health/attention/execution visible initially at 1440×900; no expanded history crowding. |
| Idle/caught up | Collapsed digest, clear Attention row, truthful fresh idle runtime, compact idle slots, Explorer begins in initial viewport. |
| Expanded histories | 5/4/6 bounds, 140-character summary disclosure, Show more/fewer via keyboard/pointer; accurate totals/checkpoint/window; full text and Workspace links reachable. |
| Large tracker/intake | 200+ Beads; 15 rows/page on every filter; range/Previous/Next; off-page server search; empty/end-page clamp; created Bead located or Show in All fallback. |
| Stale/disconnected/error | Last confirmed data labeled, observation timestamp, distinct loading/empty/error, Retry, truthful invocation lifecycle; no mutation authority changes. |
| Diagnostic/invocation detail | Correct runtime/slot/invocation; open diagnostics survive changed 2-second snapshots; follow/pause/latest, reconnect without duplicates, end, Escape/Close and focus restoration. |
| Long content/mobile/zoom | 200-character titles, paths/SHAs and large data at 375px and 720 CSS px; readable text, wrapped actions/metadata; `scrollWidth <= clientWidth`; no clipped controls or lost focus. |
| Retained flows | Intake, Workspace navigation/history, guidance drafts and guarded Resume/Retry/Abort still work; stale conflict is refreshable, not blindly resubmitted. |

Runtime-changing children use [isolated Aspire and Playwright QA](../../tests/Nightweaver.QA.AppHost/README.md) with dynamic direct endpoint discovery and teardown of the same stack, including failure paths. Record representative baseline/integrated screenshots at 1440×900, 375px and 720 CSS px, with no uncaught browser errors. Review hierarchy, density and readability as well as assertions. The specification-only `ns-1b1.1` does not require a stack. Required build/full-suite checks retain six threads and report actual suite wall time (including build, fixture publishing and teardown) against the five-minute budget.

## Reference prototype

[Repository prototype](nightweaver-ui-prototype.html) illustrates the architecture and Workspace; its simulated runtime, Planning bay and older defaults are not normative. Implement from the current contracts and actual endpoint schemas.
