# Studio Attention and Full Bead Workspace

Studio's current-project Attention route and overview preview consume the shared
`attention` observation. Dedicated `/projects/:projectId/beads/:beadId` routes
subscribe to the exact `workspace:<beadId>` key. They reuse the tab's coordinator;
there is no feature-owned response cache, polling loop or EventSource. Legacy
root-console links translate into Studio routes. Summary, History and Output are native
query links, with direct loads and browser history supported by the shell.

Review links from Overview enter the exact Project+Bead Workspace. Its Attention
and guidance panel precedes Summary, History and environment content. The explicit
Attention Desk selection uses the same question-first presentation: pause kind,
stage and repair count, the attributable question and server-presented summary,
validated Question/Recommended answer/Tradeoffs sections when present, then
editable guidance with adjacent Send & Resume and Save Note controls.
Original evidence and additional context are a stable disclosure below the editor;
source-comment links still open the native History route. Freshness and material
context warnings remain visible. Resume permits ordinary scheduling for this Bead
and does not directly launch an Agent or override a paused Project.

The current observation contract has no structured alternatives. Guidance remains
freeform; arbitrary prose is never converted into choice buttons, consequences or
a fabricated recommendation. Technical recovery retains its distinct Retry/Abort
controls and confirmation flow. The frozen refined-A design reference is
`H:/Nightweaver/design-handoffs/ns-aki/45d03c7b940269cd98c970bd46c35fcf509eb7ff12315cf46d733060c6a20514.html`
(SHA256 `45D03C7B940269CD98C970BD46C35FCF509EB7FF12315CF46D733060C6A20514`).

Attention is an explicit selection: incoming rows never select themselves.
Removing a selected row leaves its editor and exact Bead identity in place with
an unavailable-action explanation. Guidance lives in the shared Project+Bead
interaction store, survives observation replacement and route changes, and is
never automatically persisted or transferred. Successful sends also retain the
draft until the human explicitly discards it. Storage-limit errors are visible;
the command's existing 16 KiB UTF-8 and NUL validation is checked before sending.

Commands have a separate tab-owned lifetime, independent of mounted views.
Pending actions disable submissions for that Project+Bead even after navigation.
Save Note uses `note_only`; Resume and Retry carry the displayed stage, complete
pause-label set and repair count. Abort uses the existing empty-body contract
after a second confirmation; Escape/Cancel returns focus to the trigger. The
server remains the authority for eligibility and every write. No optimistic
workflow transition or automatic command replay is introduced.

A ten-second command deadline ends a stalled UI without assuming the write
failed. Unreadable, foreign or incomplete receipts, network failures and partial
write failures display uncertainty and reconcile active tracker observations.
Another action requires an explicit acknowledgement that current state/history
was reviewed. HTTP 409 explains the conflict and permits an explicit action
against the newly displayed state; 404 preserves the draft. Late receipts update
only their original command identity. They cannot clear another draft, select a
Bead or move focus on the destination route.

Connection liveness, envelope freshness and individual source freshness are
separate. Stale source warnings do not replace server-side workflow validation.
Cached details remain readable when stale; a missing-resource report
disables actions even if historical details survive. Environment and runtime
observations include their own times and sources and do not imply a running
Agent. Output now consumes the shared factory observation and opens exact identity-bound Studio inspection; see [Delivery inspection](studio-delivery.md).

The observation contract retains its two-Workspace interest limit and 30-second
disconnect grace. Rapid navigation to a third identity can temporarily exhaust
that capacity; the coordinator reconnects with bounded backoff and the page
shows connection/observation unavailability until admission and collection
complete. Unsafe identifiers are rejected before resource subscription.

History initially renders active pause evidence plus eight recent comments.
Explicit controls reveal eight older comments at a time. The active source is
pinned even when older than the preview. Stable comment keys preserve expanded
disclosures and focus across updates and movement. If a focused control is
removed, focus returns to a surviving local heading. Query-only navigation keeps
the tab link focused; section content remains mounted behind native `hidden` so
disclosures survive tab changes. Structured message summaries and details reuse
the server's presentation, with the original source always discoverable.

Markdown reuses the console's locally vendored Markdown-it and DOMPurify with
raw HTML disabled and an explicit tag/attribute allowlist. Images are omitted;
links permit only absolute HTTP(S) without credentials and open with
`noopener noreferrer`. Missing renderer dependencies fall back to literal text.

Verification uses `npm test` in Studio, the workspace HTTP integration/boundary
tests, and `studio-attention-qa.mjs` on the exact isolated QA AppHost described in
`tests/Nightweaver.QA.AppHost/README.md`. The browser probe combines native
disposable tracker changes and real same-origin guarded POSTs with deterministic
read projections for long history and degraded sources. It does not mutate the
configured production Project.
