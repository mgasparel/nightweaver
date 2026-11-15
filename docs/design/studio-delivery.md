# Studio Delivery and invocation inspection

Delivery, the bounded Overview activity preview, and Workspace Output reuse the
tab-owned `factory` observation. Runtime health, occupied bays and retained idle
history come from runtime observations; tracker status and ownership confer no
execution claim. Stale and stopped snapshots are labelled independently from
unavailable data. Overview shows at most three occupied bays per pool; the floor
shows the runtime's bounded Planning, Delivery and singleton Merger inventory.
Diagnostics contain paths, branch, runtime identity, configured model and observed
model. Missing observed model remains Unavailable, never inferred from configuration.

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
