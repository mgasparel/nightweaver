# Studio Product intake, readiness and Housekeeping

The current Project's Beads route contains Product feature/bug intake. Its fields
map directly to the existing `POST /api/beads` contract: type, priority, title,
description and acceptance criteria. Server field errors remain attached to the
fields and appear in an actionable summary. The draft lives in the tab's bounded
interaction store, separate from observations, survives route changes and is
retained after both failed and successful commands. Start a new draft explicitly
discards it. A confirmed creation links directly to its exact Full Bead Workspace;
it does not opt into Planning or infer Explorer inclusion from creation alone.

The Workspace shows the existing `planning` projection for Products. Ready for
Planning submits only the displayed expected identity to the existing guarded
endpoint. The host rereads authoritative state; overdue source observations do
not bypass that check. Missing resources, disconnected/stale coordinator state
and unavailable observations disable authorization. A 409 retains the conflict
message and requires explicit Refresh and review before another submission.
Normal scheduling, ownership and pause rules remain independent.

Housekeeping is a current-Project shell route at `cleanup`. Its URL owns the
bounded page, status and search preferences. It consumes `cleanup:<page>:<status>:<query>`
through the shared coordinator, with eight rows per server page. Counts, owner
availability, retained denials, paths, attempts and owner acknowledgements are
display evidence. Missing counts are unavailable evidence, never an empty queue.
Stable Bead keys preserve disclosures and focus during observation replacement;
removed controls return focus to the surviving route heading.

Retry is available only for a displayed needs-attention obligation with current
cleanup observations and an accepting owner, and is disabled while queued. The
request carries the exact runtime owner, obligation revision and a tab-generated
request ID stable for that owner/revision. Manual retries after ambiguous results
reuse that ID. A terminal stale/protected/expired acknowledgement permits a new
request identity after inspection. Queuing and acceptance do not establish
disposal; only resolved evidence confirms absence. The existing server checks
protected Beads, owner availability and exact obligation evidence before queuing.

All three commands use same-origin JSON requests and the existing request marker.
Tab-owned pending state survives navigation. A ten-second deadline covers both
the response and body; network loss, malformed receipts and server failures retain
input and require explicit evidence review. Late receipts cannot settle a newer
command. There is no automatic POST replay or Planning execution. Tracker commands
reconcile active Attention, Explorer, digest, Workspace and cleanup resources;
cleanup commands reconcile cleanup and Factory resources. Features own no polling
loops, response caches or EventSources.

Verification uses the Studio command/coordinator tests, existing
`WebBeadsIntegrationTests`, `PlanningReadinessWebTests` and `CleanupWebTests`, plus
`planning-readiness-qa.mjs --studio` and `cleanup-qa.mjs --studio` on the exact
isolated cleanup QA AppHost. The latter uses real native cleanup before read-only
fixtures cover inventory, unavailable/stopped owners and responsive evidence.
