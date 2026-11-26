# Studio Activity

The current Project has an Activity route at `/projects/:projectId/activity`.
Overview links to it without subscribing to a second feed. Delivery floor still
shows execution bays. Catch Up remains the summary after absence. Activity
records grant no lifecycle authority.

The route reads the [Activity API](activity-api.md). It displays literal messages
in journal order. Each row shows phase, exact Bead, evidence strength and level.
Evidence sections show source identity, source time, observation delay, freshness
at observation, reason, count, runtime and invocation. Absent source time and
reversed clocks have explicit displays. Historical freshness stays historical.

The URL owns `level=work|warning|error` and one exact `bead` filter. History and
SSE use the same selection. Direct navigation and browser history retain it.
Changing selection ends the old stream and cancels old reads. Filter submission
returns keyboard focus to Apply filters. Foreign Projects cannot subscribe to the
current Project's Activity.

## Reading controls and limits

New rows append at the bottom during live follow. Pause, upward scrolling,
keyboard browsing, focus within a row, evidence expansion and older loading hold
the displayed rows. Arrival never moves keyboard focus. An older page preserves
the first visible row's position. Open evidence retains its row identity.

The client holds at most 500 rows, their detail links and 20 open evidence
sections. Exact invocation output uses the existing 2,000-line / 256-KiB
controller. The stream holds only its latest bounded detail batch. Paused arrival
retains a saturating count, displayed as `500+`, and one received cursor. A paused
history response counts unseen matching records before it advances that cursor.
History replay and SSE replay do not count the same record twice. It stores
no pending row list. Only control-state changes use status announcements; the
record list is not a live announcement region.

Return to latest resumes after the last displayed scan cursor. History and SSE
catch up over unseen records, with duplicate suppression and bounded eviction.
The control starts live follow when the read starts. It closes evidence only
when the response arrives and follow is still active. A later pause, row focus,
evidence expansion or page suspension takes precedence over the pending response. That response
keeps the displayed rows, evidence and scan cursor. It cannot enable follow or
clear evidence. A fresh explicit Return to latest is required, including after
a failed cursor read falls back to fresh history.

Source gaps and client eviction remain explicit. A replacement journal resets
rows only during follow or an explicit return; paused rows stay readable. Runtime replacement remains
visible. Prior-runtime records retain their original evidence. Displayed journal
identity is separate from received scan progress. An empty replacement scan clears
old rows during follow. During pause, it holds the old rows and open evidence
until Return to latest. A journal change cancels the old older-page continuation.

Initial history selects the latest 100 retained offsets currently loaded by the
shared reader. SSE continues progressive source loading. Load older records
examines at most the preceding 100 offsets through finite API pages. The window
shrinks to the remaining row capacity. Older additions preserve all displayed
rows and open evidence. If a page exceeds capacity, the continuation retains the
excluded offsets. Filtered
scans can pass the window; those later rows do not enter the older page. The
500-row limit stops older loading and asks the operator to narrow the filters.
Empty loaded matches, loading history and unavailable storage have separate text.
The first pending read shows loading. A failed first read shows unavailable.
Opening the stream alone cannot clear that failure state.
The view shows source retention bounds and its client limit. It makes no claim
to an unlimited audit history.

## Transport and evidence links

One session owns one Activity stream. A disconnect closes that stream. After
three seconds, a new stream uses the last received cursor. It does not combine
a URL cursor with native EventSource `Last-Event-ID`. History reads have a
ten-second client deadline, including response decoding. Navigation cancels reads
and closes streams. Page suspension closes the stream and pauses browsing. Wake
reconnects without enabling follow. Return to latest stays an explicit action.

Transport, journal health and runtime health appear separately. The client ages
journal success after 60 seconds and live runtime evidence after ten seconds.
Socket connection does not prove source freshness. Source failure retains rows.
A failed cursor read can recover through a fresh bounded history request, with
an explicit gap. Failed reads do not trigger lifecycle commands.

Workspace links bind Project and Bead. PR links require `MergeVerified`, `Verified`
strength and equality with the record's accepted merge facts. The API retains the
Target Repository binding. The client rejects credentials, query strings,
fragments and non-HTTPS PR URLs. Invocation output requires the exact runtime,
slot and invocation link supplied by the API. Equivalent GUID formats preserve
identity. Output uses the existing endpoint and bounded controller; missing or
expired output stays unavailable. Activity adds no transcript access path.

## Verification and cost

`Studio/test/activity.test.mjs` adds eighteen controlled cases. They cover a
6,000-record burst, row/detail/expansion bounds, paused duplicate replay, cursor
catch-up, older identity preservation, replacement, empty scans, safe links and
delayed times. They add no native process or fixture publication.

The focused `studio-cutover.ps1 -Group Activity` journey reuses the existing
isolated native invocation fixture. An opt-in journal connects production
executor hooks to real storage, API, SSE and browser rows. One held invocation
completes after browser subscription. It also checks exact invocation output.
Separate substituted responses check presentation, bursts, anchor position,
focus, filters, source failure, replacement, reconnect, suspension and stream
disposal. Screenshots cover 1440, 720 and 375 CSS pixels. The runner stops the
exact AppHost in `finally`. This browser journey stays outside the normal suite
and uses no production workload.

Production adds a route-owned history request and stream only while Activity is
open. Overview adds only a link. Shared reader limits, tracker reads, scheduling,
six test threads, deadlines and lifecycle authority stay unchanged. The Bead
comment and acceptance report retain candidate cost and browser evidence.
