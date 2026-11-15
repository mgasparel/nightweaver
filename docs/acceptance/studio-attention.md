# Studio Attention and Full Bead Workspace acceptance

Implemented ns-mbd.4 on the Studio shell and shared synchronization coordinator
from main `fc0ec6b`. Attention now has a bounded live inventory and an explicit,
identity-bound guidance editor. Dedicated Workspace routes render Summary,
History, environment/source disclosures and an existing-controller Output entry
point. The [interaction policy](../design/studio-attention-workspace.md) records
command lifetimes, draft retention, source health and authority boundaries.

## Local checks

### Telemetry teardown repair

The subsequent human recovery requirement concerns hosted run `43657940380`:
`ExplicitCursorsAreClampedToTheBoundedReplayWindow` failed when fixture cleanup
deleted a still-open telemetry `.tmp` file. The historical owner report was
empty; it does not identify that run's file holder.

The controlled regression
`PublisherDisposalSettlesOpenSnapshotBeforeFixtureCleanup` holds the actual
atomic snapshot stream before flush, with its normal Windows sharing policy and
directory pins. Before the repair, both cases failed: final-publication disposal
returned after two seconds, and background-writer disposal after four seconds,
while Windows rejected deletion of the open temporary file. This establishes a
writer-lifetime defect capable of the exact observed cleanup failure without
requiring an external file holder. It does not retrospectively identify the
historical holder.

`WaitAsync` bounded the wait without canceling or settling the underlying write.
Disposal now stops observations, cancels and joins background producers, then
attempts the final stopped snapshot with the existing two-second publication
budget applied to the write's cancellation token. It awaits actual stream/pin
unwind before disposing synchronization resources or returning. Concurrent
disposal joins the same completion. The regression checks cancellation, both
write lifetimes, concurrent disposal and immediate native cleanup without
retries. The flush gate is a per-publisher internal test seam; normal writes use
the unchanged atomic filesystem implementation. No fixture cleanup retries,
test coverage or six-thread scheduling limits changed.

The focused telemetry suite passed all 15 cases after the repair, including the
original cursor test and both new settlement cases. The final complete suite
passed all **1,194 tests**, zero failures/skips, in **293.541 seconds** including
build, fixture publishing and teardown at the unchanged six-thread setting.
This is candidate timing, not an integrated-main measurement. The warning-as-error
solution build and all 33 Studio unit tests passed. Fresh browser results are
recorded in the ns-mbd.4 implementation evidence comment: Attention/Workspace
passed all real-command, live-update and history checks with zero uncaught page
errors; all 21 captures at 1440/375/720 CSS pixels were reviewed. The subsequent
shell probe encountered four HTTP 429s on that reused fixture; shell and legacy
smoke passed with zero browser errors on a separate fresh isolated fixture.
Both exact AppHosts stopped successfully and left empty Aspire inventories.

### Original Studio validation

- `dotnet build Nightweaver.slnx -warnaserror`: passed, zero warnings/errors.
- `npm test --prefix src/Nightweaver.Web/Studio`: **33 passed**, including 11 new
  identity, wire-contract, duplicate-submit, late-response, 404/409, partial-write,
  uncertainty and deadline checks. TypeScript/Vite builds also passed.
- `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995
  --report-xunit-xml --report-xunit-xml-filename ns-mbd4.xml
  --show-slowest-tests 20`: **1,163 passed**, zero failed/skipped; candidate wall
  time **4m 8.808s**, including build, fixture publishing and teardown, with the
  committed six-thread parallelism. This is candidate evidence, not a new timing
  measurement of integrated main.
- Targeted `WebBeadWorkspaceBoundaryIntegrationTests` and
  `WorkspaceRelationsWebTests`, repeated `--filter-class` and
  `--minimum-expected-tests 13`: **13 passed**, zero failed/skipped, 36.859s.
  `WebBeadWorkspaceIntegrationTests.cs` currently contains imports only; it is
  not used as a zero-test validation target.

## Browser checks

The final fresh exact QA AppHost used `--isolated --non-interactive`, waited for
`web`, and discovered direct endpoint `http://localhost:25297`. From the QA
directory, `studio-qa.mjs`, `studio-attention-qa.mjs` and `smoke.mjs` all passed.
The AppHost stopped in `finally`; a subsequent `aspire ps --format Json` returned
`[]`. Production was not operated or deployed.

The shell probe passed direct reload, dotted and unsafe legacy identity handling,
Back/Forward, query state, keyboard/heading focus, foreign-project isolation and
dialogs, with zero browser errors or mutations. The Attention/Workspace probe
recorded [its checks and exact request bodies](studio-attention/result.json),
with zero uncaught page errors:

- External native `bd` escalation, comments and resolution appeared without
  refreshing. Concurrent text and textarea focus survived; resolution retained
  the selected Bead's editor and draft instead of selecting another row.
- Real EventSource disconnect/reconnect restored observations without replaying
  actions or dropping the draft.
- Real same-origin Save Note, Resume, stale-snapshot 409, fresh Retry and
  two-step Abort reached the existing host handlers. Save Note retained the
  pause; Abort closed the exact disposable Bead. Escape returned focus to Abort.
- Pending requests prevented duplicate submission. A held real Resume receipt
  arriving after another Bead was selected did not affect that selection.
  Controlled 404/503 command failures retained drafts and required explicit
  review after uncertainty; no command replay occurred.
- Direct Workspace loads/reloads and native History Back/Forward worked. Native
  external comments remained discoverable. An 80-comment read fixture initially
  rendered active evidence plus eight recent comments, then revealed eight more.
  Disclosure focus/expansion survived refresh; removal returned focus to a
  surviving heading. HTML/script/image/unsafe-link input did not execute or
  become active content.

Long-history and degraded/empty read projections are deterministic browser
fixtures, not claims about native runtime execution. Guarded command successes
and the stale 409 used actual browser-origin requests against the isolated host.

## Visual review

Reviewed the 1440, 375 and 720 CSS-pixel matrix below. Studio's navy surfaces,
violet selection/focus and amber source warnings remain coherent. Desktop uses
an inventory/editor split; narrow views stack bounded inventory, evidence and
guidance. Actions and native disclosures remain reachable, text wraps, and the
probe asserted no document overflow at every width. The 720 CSS-pixel captures
exercise the effective reflow width of a 1440px display at 200% zoom; they do not
claim a native browser zoom-setting test.

| State | 1440 CSS px | 375 CSS px | 720 CSS px |
| --- | --- | --- | --- |
| Live Attention and typing | [Desktop](studio-attention/attention-busy-1440.png) | [Mobile](studio-attention/attention-busy-375.png) | [Reflow](studio-attention/attention-busy-720.png) |
| Workspace Summary/actions | [Desktop](studio-attention/workspace-busy-1440.png) | [Mobile](studio-attention/workspace-busy-375.png) | [Reflow](studio-attention/workspace-busy-720.png) |
| Active evidence / long history | [Desktop](studio-attention/long-history-1440.png) | [Mobile](studio-attention/long-history-375.png) | [Reflow](studio-attention/long-history-720.png) |
| Expanded history | [Desktop](studio-attention/busy-1440.png) | [Mobile](studio-attention/busy-375.png) | [Reflow](studio-attention/busy-720.png) |
| Retained stale source | [Desktop](studio-attention/stale-1440.png) | [Mobile](studio-attention/stale-375.png) | [Reflow](studio-attention/stale-720.png) |
| Missing / unavailable | [Desktop](studio-attention/unavailable-1440.png) | [Mobile](studio-attention/unavailable-375.png) | [Reflow](studio-attention/unavailable-720.png) |
| Empty Attention | [Desktop](studio-attention/empty-1440.png) | [Mobile](studio-attention/empty-375.png) | [Reflow](studio-attention/empty-720.png) |

The existing observation contract still admits two Workspace interests and
retains disconnect grace for 30 seconds. Rapid third-identity navigation can
temporarily wait for admission, reconnect backoff and collection. This change
surfaces unavailability and preserves exact identities instead of relaxing that
budget. Source-overdue warnings also remain visible; guarded commands reread
fresh tracker state on submission. Output continues to link to the existing
controller until ns-mbd.5 supplies Studio invocation inspection.
