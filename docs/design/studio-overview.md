# Studio Overview

Overview follows the maintainer-approved refined variant A. The immutable design
handoff is `H:/Nightweaver/design-handoffs/ns-aki/45d03c7b940269cd98c970bd46c35fcf509eb7ff12315cf46d733060c6a20514.html`,
SHA256 `45D03C7B940269CD98C970BD46C35FCF509EB7FF12315CF46D733060C6A20514`.
Its sample counts and controls are not production requirements.

Reading and keyboard order is the factual heading and summary, full-width
Attention, Delivery activity, then Catch up. The last two share a row above
1150 CSS pixels. Attention uses content height and keeps its Review action
next to the exact Bead identity and question. It previews three paused Beads,
retains surviving focused rows and opened context disclosures during live reorder, and links to the complete
Attention Desk. Decision and recovery labels retain the server's distinction.
Current question (or recovery diagnostic) previews use the first 140 Unicode
characters with whitespace collapsed so multiline evidence cannot displace
Review. A native keyboard-accessible disclosure after Review retains the complete
context and source comment attribution; the Workspace link remains available.
Open context is keyed by Bead identity, independently of focus. Closing releases
the extra preview row; an authoritative removal retires its disclosure state so
a later reappearance starts closed. Unavailable observations preserve that state.
Routine observation timestamps, source details and Refresh live in an Overview disclosure. Stale, unavailable, delayed, disconnected and journal-recovery warnings remain visible above the preview. Compact spacing keeps Review visible even while the longer
recovering-updates header is displayed; warnings and type sizes remain intact.
Project navigation remains in the sidebar, including its existing mobile controls.

The summary's paused count comes from the shared `base:attention` observation and
links to Attention. Runtime availability comes independently from `factory`
through the existing runtime-state projection. Missing attention is unavailable,
not zero; an empty inventory says nothing about runtime health. Source freshness,
retained observations, refresh, and synchronization warnings remain visible.
Overview no longer subscribes to a separate readiness query solely for a metric.

Catch up previews up to three actual closed outcomes across standalone and lineage work, deduplicated by Bead identity and sorted by activity time. A surviving focused outcome stays visible across reorder; removal returns focus to the preview heading. The heading identifies the actual reporting window, and the total comes from the existing digest metrics. Only verified host merge evidence permits the Merged label; other closed outcomes say Closed. Checkpoint management, filters and full evidence remain on the dedicated Catch up route. No backend migration or additional observation query is introduced.

Verification extends `studio-qa.mjs` through `overview-checks.mjs` on the existing
isolated QA AppHost. Controlled resource reads cover one decision, a twelve-item
inventory with long titles/IDs, a long multiline question with keyboard access to
its complete context (also while a held sibling factory read keeps the real
coordinator recovering), empty, stale, and unavailable observations at
1440x900, 375x900, and 720x900 (the effective width of a 1440px screen at 200%).
The native shell journey retains reload/history, keyboard navigation, sidebar
dialogs and focus, legacy routes and current-project scoping. Two cheap Studio
tests cover bounded preview and focused/open-row retention, closing and removal.
The existing browser journey replaces observations without navigation to verify
open context survives reorder after focus moves elsewhere. No new native
journey or fixture publication is added.

## Refined A visual pass (ns-jxv)

The authoritative PNG targets are under
`H:/Nightweaver/design-handoffs/ns-aki/overview-visual-pass/`.
Overview uses navy surfaces with a single amber-edged Attention surface. The
question or recovery diagnostic leads, followed by the violet Review action.
A supported Recommended answer is attributed to its report role and bounded to
140 Unicode characters; full context and exact Workspace navigation remain
available. Material retention warnings and unavailable actions remain visible.

The Delivery preview shows up to three occupied Delivery bays in one vertical
stack. Stage, exact Bead and immutable invocation navigation remain independent.
Four 5px rounded segments and one current-to-next caption express observed stage,
never completed approval history. Planning and singleton Merger are compact
occupancy summaries; the dedicated floor retains all pools, idle bays, diagnostics
and runtime Refresh. The existing Workspace Output projection is unchanged.
Factory observations lack title/type fields: explicit unavailable fallbacks are
retained rather than querying per-bay tracker history or substituting sample copy.

Historical lineage roots marked context are excluded from completed outcomes even
when their current tracker status is closed; they are not activity in this window.
The same controlled read fixture in `overview-visual-checks.mjs`, called by the
existing Studio journey, captures one decision, two Delivery bays and two closed
outcomes before/after at 1440/720/375 with 900px viewport height. It adds digest
empty/stale/unavailable/long-copy and keyboard checks to the existing native setup.
The factory and runtime-refresh probes retain stage geometry, paused/unknown/stale
positions, exact invocation inspection and refresh deduplication on the floor.
See [visual comparison](../acceptance/overview-visual-pass.md) for retained images,
projection differences and measurement evidence.
