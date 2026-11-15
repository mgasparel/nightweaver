# Studio Catch-up Digest

The Catch-up route and Overview summary subscribe to the shared `digest:<window>`
observations through the tab's synchronization coordinator. URL `window` supports
`auto`, `since_caught_up`, `24h`, `7d`, `30d` and `all`; Back, Forward and direct
reload preserve the chosen window. No feature-owned polling, EventSource or
response cache is introduced. Existing console routes remain available.

The initial digest is collapsed. Expanded history previews five lineage threads,
four children per opened thread and six standalone items. Explicit controls reveal
the rest. Root and child links open the dedicated Full Bead Workspace. Descriptions
and extracted commit / PR references appear only in requested activity; long
summaries have an independent disclosure. Untrusted content is rendered as text.

React keys bind disclosures to root and child identities. When a focused or opened
thread crosses the fifth-thread boundary, the full thread collection stays visible;
incoming threads remain collapsed. Show fewer restores the preview. Updates
preserve the focused DOM control and its viewport position. Removing that control
returns focus to the digest disclosure, without selecting another Bead. Changing
the window starts a bounded disclosure view for that filter.

Child and standalone previews retain surviving focused items and open summaries
when newer activity pushes them past the four/six-item boundary. Only those
identities are added to the preview; unrelated hidden history stays bounded.
Moving focus outside a collection releases its focus retention, while explicitly
opened summaries remain visible. Show more counts reflect the remaining hidden
items. Removing the actual item uses the same digest-disclosure focus fallback.

Mark Caught Up is an explicit same-origin checkpoint POST. The tab-owned command
captures `generatedAt` from the displayed successful observation before awaiting
anything, prevents duplicate submissions, and has a ten-second deadline covering
both transport and receipt decoding. Failure retains the displayed history and
explains that the write could not be verified; only a human can submit again.
Late responses do not replay a write. Navigation does not transfer the action's
collapse/filter effect to another route or to a newly selected window.

The existing observer captures the cutoff before reading inventory at whole-second
precision, including when retaining an inventory across bounded detail turns.
Activity at the boundary second is included by the server's inclusive window;
changes during the POST remain unread. Success reconciles active digest resources
and selects Since caught up on the originating route. Live updates, window changes,
Expand and Refresh never save a bookmark or change workflow state.

Connection state and source freshness remain separate. Failed reads keep the
coordinator's last confirmed observation, show a visible retained-data warning and
allow explicit refresh. A newly requested filter keeps the previous successful
resource subscribed until the new resource has data, with explicit old-window
context; this retains shared cache ownership rather than copying responses.
Checkpoint persistence and mutation-origin validation are unchanged. See the QA AppHost README for isolated browser verification.
