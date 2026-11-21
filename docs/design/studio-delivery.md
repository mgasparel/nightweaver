# Studio Delivery and invocation inspection

Delivery, the bounded Overview activity preview, and Workspace Output reuse the
tab-owned `factory` observation. Runtime health, occupied bays and retained idle
history come from runtime observations; tracker status and ownership confer no
execution claim. Stale and stopped snapshots are labelled independently from
unavailable data. Overview shows at most three occupied bays per pool; the floor
shows the runtime's bounded Planning, Delivery and singleton Merger inventory.
Diagnostics contain paths, branch, runtime identity, configured model and observed
model. Missing observed model remains Unavailable, never inferred from configuration.

## Delivery stage position

The immutable refined A handoff is
`H:/Nightweaver/design-handoffs/ns-aki/45d03c7b940269cd98c970bd46c35fcf509eb7ff12315cf46d733060c6a20514.html`
(SHA256 `45D03C7B940269CD98C970BD46C35FCF509EB7FF12315CF46D733060C6A20514`).
Production uses its thin, rounded violet segments and single caption, without
shipping the prototype or its sample data.

`FactoryTelemetryPublisher.ObservePreparedInvocation` copies the prepared
invocation's `Stage` from `AgentInvocationPreparer`'s `context.StageLabel`.
The Web `ReadRuntimeAsync` factory projection serializes that snapshot directly,
independently of tracker inventory. `deliveryProgress` maps only these exact values:

| Runtime slot stage | Display position | Active caption |
| --- | --- | --- |
| `stage:implement` | Stage 1 of 4 | Implement → Review |
| `stage:review` | Stage 2 of 4 | Review → QA |
| `stage:qa` | Stage 3 of 4 | QA → Merge |
| `stage:merge` | Stage 4 of 4 | Merge · final stage |

Only a live running slot with a running invocation gets the active violet segment
and current-to-next caption. Paused slots retain position with a neutral current
segment and “Paused at …”. Other retained/preparing/settling states use “Observed
at …”; stale, stopped and unavailable snapshots additionally keep their qualifier
in the caption. Missing/unsupported stages show “Stage unavailable”, all slate
segments and no ordinal. Repairs can return to Implement: preceding muted violet
segments mean position only, never verified approval or percent completion.
Equivalent explicit text accompanies the decorative, aria-hidden track.

Tracks appear only in the Delivery pool, in both Overview and the full floor;
Planning and singleton Merger retain their separate inventories and semantics.
The current factory schema supplies neither native type nor task title. Cards
therefore show explicit “Title unavailable” and secondary “Type unavailable”
fallbacks beside exact Project/Bead identity, role, start time and invocation
status. They do not infer failure from native type or fetch per-card Workspace
data. Bead links and immutable invocation inspection remain separate controls.
Timing, repairs, diffs and technical metadata remain in floor diagnostics.
No observation, subscription, polling or log ownership changes are introduced.

## Inspection lifetime

Runtime Refresh stays available while reconciliation is pending, labelled
Refresh runtime again. A 202 acceptance followed by unchanged cached attempt
timestamps must not block an explicit retry. The shared coordinator coalesces
concurrent requests; polling never replays reconciliation POSTs.

Inspection captures an immutable runtime/slot/invocation selection. Shared
operational revisions can update the floor without changing that selection,
output, follow preference, scroll position or focused control. A replaced runtime
or occupant shows a retained-selection notice. Workspace Output lists only exact
matching invocation Bead identities. No match means no retained invocation in the
current snapshot; it does not claim there was no historical execution.

`InvocationController` owns only the existing `/api/telemetry/stream` connection.
Its `runtime_id`, `slot_id` and `invocation_id` query fields stay exact. Native
EventSource reconnect sends Last-Event-ID; operational epochs/revisions are never
log cursors. Duplicate byte cursors are ignored, reset establishes a new cursor,
and end closes the stream even for failures. Old/closed source callbacks cannot
publish. Output remains literal React text, strips terminal control sequences,
and keeps the existing 2,000-line / 256-KiB UTF-8-safe bounds.

The modal provides pause/resume follow and jump-to-latest. Escape and Close stop
only inspection, restoring focus to the exact still-present trigger or the local
activity heading. Tab and Shift+Tab wrap within its visible controls. Route
unmount stops the log connection. Long metadata is disclosed inside a scrolling
body while follow actions remain outside that scroll area.

Verification uses `npm test` and the isolated AppHost workflow. Run
`factory-qa.mjs --studio` and `invocation-qa.mjs --studio` for React/shared-resource
scenarios, then `telemetry.mjs --studio` once for native cursor resume, output,
terminal cessation and effective 200% layout. The no-flag scripts retain legacy
console coverage. Screenshots include 1440/375/720 CSS pixels and viewport captures.
Stop the exact AppHost afterward. See the acceptance report for recorded results.
