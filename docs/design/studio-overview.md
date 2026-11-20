# Studio Overview

Overview follows the maintainer-approved refined variant A. The immutable design
handoff is `H:/Nightweaver/design-handoffs/ns-aki/45d03c7b940269cd98c970bd46c35fcf509eb7ff12315cf46d733060c6a20514.html`,
SHA256 `45D03C7B940269CD98C970BD46C35FCF509EB7FF12315CF46D733060C6A20514`.
Its sample counts and controls are not production requirements.

Reading and keyboard order is the factual heading and summary, full-width
Attention, Delivery activity, then Catch up. The last two share a row above
1150 CSS pixels. Attention uses content height and keeps its Review action
next to the exact Bead identity and question. It previews three paused Beads,
retains a surviving focused row during live reorder, and links to the complete
Attention Desk. Decision and recovery labels retain the server's distinction.
Current question (or recovery diagnostic) previews use the first 140 Unicode
characters with whitespace collapsed so multiline evidence cannot displace
Review. A native keyboard-accessible disclosure after Review retains the complete
context and source comment attribution; the Workspace link remains available.
On mobile, freshness status and Refresh share a row, with source details and
warnings below. Compact spacing keeps Review visible even while the longer
recovering-updates header is displayed; warnings and type sizes remain intact.
Project navigation remains in the sidebar, including its existing mobile controls.

The summary's paused count comes from the shared `attention` observation and
links to Attention. Runtime availability comes independently from `factory`
through the existing runtime-state projection. Missing attention is unavailable,
not zero; an empty inventory says nothing about runtime health. Source freshness,
retained observations, refresh, and synchronization warnings remain visible.
Overview no longer subscribes to a separate readiness query solely for a metric.

Catch up reuses the current digest component, reporting window, snapshot cutoff,
bookmark, expansion, and checkpoint command. Its compact summary identifies the
actual window and snapshot, and links to the complete Catch up route. It does
not introduce highlight counts or a digest backend migration.

Verification extends `studio-qa.mjs` through `overview-checks.mjs` on the existing
isolated QA AppHost. Controlled resource reads cover one decision, a twelve-item
inventory with long titles/IDs, a long multiline question with keyboard access to
its complete context (also while a held sibling factory read keeps the real
coordinator recovering), empty, stale, and unavailable observations at
1440x900, 375x900, and 720x900 (the effective width of a 1440px screen at 200%).
The native shell journey retains reload/history, keyboard navigation, sidebar
dialogs and focus, legacy routes and current-project scoping. Two cheap Studio
tests cover bounded preview and focused-row retention/removal; no new native
journey or fixture publication is added.
