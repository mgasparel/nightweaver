# Studio console cutover

Studio serves `/` and `/index.html`; `/studio` remains compatible. The configured
Project owns all `/projects/:projectId` observations and commands. The old HTML,
`js/app.js`, `js/cleanup.js` and `css/site.css` are retired. Vendored Markdown-it
and DOMPurify remain because Studio uses their existing sanitization policy.
There is no Node process in the deployed runtime.

## Reconciled operational parity

This reconciles the parent ns-mbd feature inventory and the migration inventory
in [Studio shell](../design/studio-shell.md). Routes below are relative to
`/projects/:projectId`. Browser probes run only against the disposable isolated
Aspire fixture; guarded commands use actual same-origin browser requests.

| Supported surface | Route/action | Replacement automated evidence |
| --- | --- | --- |
| Root, dedicated routes and legacy bookmarks | `/`, `/index.html`, `/studio`; `#bead/<id>` and old section hashes | `StudioWebTests`, `studio-qa.mjs`: reload, Back/Forward, query state, dotted hierarchical IDs, keyboard focus, unknown Project, API/asset 404s |
| Attention and human intervention | `/attention`; Save Note, Send & Resume, Reset Budget & Retry, two-step Abort & Close | `studio-attention-qa.mjs`, `attention.test.mjs`, existing mutation/authority .NET tests: external pause/comment/resolution, retained draft/focus, actual guarded requests, stale 409, uncertain receipt and no replay |
| Full Bead Workspace | `/beads/:id?tab=summary|history|output`; sources, Markdown, facts, relations, retained environment, active evidence | `studio-attention-qa.mjs`, `StudioWebTests`, existing Workspace relation/presentation/boundary .NET tests: pinned active evidence, bounded history, sanitization, disclosure focus, missing/stale details |
| Factory and invocation inspection | `/delivery`, Overview preview, Workspace Output; exact runtime/slot/invocation identity and follow controls | `studio-delivery-qa.mjs` through `factory-qa.mjs`, `telemetry.mjs`, `delivery.test.mjs`, existing native telemetry tests: real reconnect cursor/completion, no retarget on replacement, UTF-8/line bounds, focus/follow and health distinctions |
| Catch-up Digest | `/catch-up?window=...`; all six windows, lineage, summary disclosure, snapshot-bound Mark Caught Up | `digest-qa.mjs`, `digest.test.mjs`, existing digest/checkpoint .NET tests: native update and checkpoint persistence, concurrent newer activity, bounded long histories, stale data, errors and focus |
| Explorer and header search | `/beads?view=ready|all|active|blocked|closed&q=...&page=...` | `studio-explorer-qa.mjs`, `explorer.test.mjs`, existing native Explorer tests: 81-row inventory, external titles/dependencies/readiness, bounded pages, focus/removal, URL history and retained stale results |
| Product intake and explicit Planning readiness | `/beads`; `/beads/:id` Ready for Planning | `planning-readiness-qa.mjs` delegates to `studio-product-qa.mjs`; `product.test.mjs`, existing readiness/intake .NET tests: native creation and persisted authorization, validation, retained draft, stale 409 and ambiguous disconnect without replay |
| Housekeeping | `/cleanup`; owner/revision-bound Retry cleanup | `cleanup-qa.mjs` delegates to `studio-cleanup-qa.mjs`; `product.test.mjs`, existing native cleanup tests: stale/eligible actual inbox requests, verified absence, retained protected evidence, bounded search/pages and unavailable/stopped owner |
| Synchronization and recovery | One active tab-owned stream; independent source health; explicit reconciliation | `operations-qa.mjs`, `read-timeout-qa.mjs`, `runtime-refresh-qa.mjs`, `operations.test.mjs`: external changes, changed subscriptions, reconnect, heartbeat/read stalls, healthy transport with stale data, wake, late responses and no command replay |
| Concurrent drafts and server restart | Identity-bound memory-only guidance across route changes, web restart and page lifecycle wake | `studio-abort-refresh-qa.mjs` retains disabled confirmation/Cancel focus through reconnection; `studio-recovery-qa.mjs`: two drafts, actual exact-resource restart/new epoch, external burst title/comment edits during suspension, restored focus and no POST |
| Packaged hosting | Standalone executable and background embedded launcher | `WebHostIntegrationTests.PublishedStudioServesStandaloneAndBackgroundHosts`: fresh Release publish, root/deep links and hashed assets, empty standalone PATH, missing API 404, no frontend source or obsolete assets published |
| Unavailable multi-project controls | All projects, Add project, foreign Project route | `studio-qa.mjs`: explicit unavailable dialog, Escape/return focus, no setup inputs, no mutations; operational probe verifies no foreign-Project stream |

The old console/Explorer/Workspace/factory/invocation/intake/cleanup probe entry
names now run the Studio replacements. Schema/relations/workflow entry names
share `studio-presentation-qa.mjs`: native server-projected facts, bounded typed
relations, partial-source state, supported/fallback evidence, original source,
safe HTTP(S) keyboard links and disclosure focus. Detailed native combinatorial
coverage stays in the existing presentation/relations .NET tests. Planning
telemetry and the manual pilot selectors use the Studio routes and bays. The previous source-text and HTML-marker hosting assertions are
replaced by built hashed-asset resolution and interactive component/browser
coverage, rather than asserting React implementation strings. The operational
probe now checks one **active** connection: changing feature subscriptions
legitimately closes and replaces its stream, with a fresh hello. It still rejects
overlapping streams and verifies explicit reconnect convergence.

## Reproduction and evidence limits

Install with `npm ci` in both `src/Nightweaver.Web/Studio` and
`tests/Nightweaver.QA.AppHost`; install Chromium with `npx playwright install
chromium` in the latter. Frontend validation is `npm test`, `npx tsc --noEmit`,
and `npm run build` in Studio. Run `studio-cutover.ps1` from the repository root
for the Default group, then `-Group Commands`, `-Group Invocation`, and
`-Group Recovery`, and `-Group Presentation`. Each group starts/waits/describes its exact isolated AppHost,
uses the discovered direct endpoint/config, and stops it in `finally`. Use
`-Only <probe-name>` to rerun one of that group's probes against a fresh fixture.
Raw logs and captures are retained in `TestResults/studio-cutover`.
Pass `-OutputDirectory <fresh-directory>` for reruns that preserve those logs.

Visual review covers 1440x900, 375px, and 720 CSS-pixel reflow (the effective
layout width at 200% zoom on a 1440px desktop). The lifecycle wake test simulates
browser page suspension and wake; it is not a physical Windows sleep test.
Guidance is memory-only and is deliberately not retained after a full page
reload or tab closure. No production stack or tracker is used or deployed.

The complete solution command retains committed six-thread parallelism and a
positive floor of 1,265 tests. The integrated candidate passed all 1,265 tests,
with zero skips, in **239.285 seconds** on 2026-11-16 UTC using
`dotnet test --solution Nightweaver.slnx --minimum-expected-tests 1265 --report-xunit-xml --report-xunit-xml-filename dispatch-cutover.xml --show-slowest-tests 20`.
The stopwatch surrounds the entire command, including build, fixture publishing
and teardown; raw output/XML and timing are in `TestResults/dispatch-cutover*`.
Frontend clean install, 56 component tests, explicit typecheck and production
build passed. `dotnet build Nightweaver.slnx -warnaserror` passed without warnings;
the separate disposable Release Web publish also passed. The complete suite
includes the fresh-publish standalone/background host smoke described above.
Candidate timing cannot
establish the **latest-main** performance gate until this change is integrated;
Merger must remeasure latest main with the complete command in `tests/README.md`.


## Integrated findings addressed

- The root still served the old console and Studio linked back to it. Exact
  root entries now serve the built SPA, obsolete UI assets and reciprocal links
  are removed, and legacy Bead/section bookmarks translate into Studio.
- Missing assets at exact Workspace depth could receive the SPA. The dotted-ID
  exception now excludes asset suffixes; route tests cover JS, CSS and SVG there.
- The Ready overview card was a permanent disconnected placeholder. It now
  reads the existing shared native-ready Explorer total with independent health.
- Workspace omitted server-projected workflow/source facts and rendered all
  relations at once. It now exposes those facts in a collapsed disclosure,
  preserves relation type/status/role and reveals 15 relations at a time.
- A forced EventSource failure reproducibly dismissed Abort confirmation and
  lost Cancel focus. The short `studio-abort-refresh-qa` probe failed before the
  fix and passed afterward. Reconnection retains the exact confirmation and
  focus while submission remains disabled; actual action ineligibility still
  dismisses it. This changes no command payload or host authority.
- Integrated shell probes assumed one stream opening across every route and a
  unique global Refresh control. They now assert one active stream, a fresh
  reconnect hello and the intended synchronization control. Nested evidence and
  review titles are scoped explicitly. Screenshots wait for actual metrics
  instead of capturing bootstrap placeholders.


## Reviewed visual evidence

- [1440px Overview](studio-cutover/overview-1440.png): four observed metrics,
  persistent project navigation and the Delivery/Attention hierarchy retain the
  approved Studio composition and palette. Runtime and tracker health remain
  independent; retained tracker data is explicitly stale.
- [375px Overview](studio-cutover/overview-375.png) and
  [720 CSS-pixel Overview](studio-cutover/overview-720.png): metrics reflow to two
  columns, panels stack, text/actions remain reachable, and there is no page
  horizontal overflow. Project navigation remains keyboard reachable in its
  mobile scroll region.
- [Expanded Workspace](studio-cutover/workspace-375.png): native workflow/source
  facts, keyboard-scrollable bounded original metadata, literal hostile text,
  typed relation links and explicit incremental disclosure.
- [Recovered drafts](studio-cutover/recovery-375.png): the selected exact Bead and
  its guidance remain after server replacement and simulated lifecycle wake.
- [Retained invocation](studio-cutover/output-375.png): long diagnostics wrap,
  output scrolls within the dialog, and Close/follow/latest controls are reachable.
- [Housekeeping](studio-cutover/cleanup-375.png) and
  [Catch-up](studio-cutover/digest-375.png): bounded obligations and activity,
  guarded actions, long evidence wrapping and compact responsive controls.

The full Attention/Workspace probe passed nine groups of assertions with 14
observed POSTs (including explicit reconciliation and deliberate failure cases)
and zero uncaught page errors. The recovery probes sent no commands. Native
Planning and Delivery stream smokes passed actual process identity, reconnect,
completion and follow/focus checks. Intake/readiness confirmed a native created
Product and persisted readiness after a real stale 409. Cleanup confirmed the
fifth attempt, actual absence, unchanged delivered Bead and protected paused
sentinel. Explorer, Digest and timeout/refresh probes all passed.

The separate long manual Delivery browser pilot was updated for Studio selectors
but not rerun; its native process/recovery boundaries remain in the complete
.NET suite. One early fresh-fixture attempt timed out a tracker source while its
stream remained connected; the unavailable state was explicit. Source/command
budgets were not relaxed, and later fresh isolated runs passed. This evidence
does not claim a universal external-edit freshness SLA or physical OS sleep.
