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
| Full Bead Workspace | `/beads/:id?tab=summary|history|output`; sources, Markdown, facts, relations, retained environment, active evidence | `studio-attention-qa.mjs`, `studio-evidence-qa.mjs`, `StudioWebTests`, existing Workspace relation/presentation/boundary .NET tests: pinned active evidence, bounded history, sanitization, disclosure focus, missing/stale details |
| Grouped relations | Workspace Summary → Relations; Parent/Children, Blocked by/Blocks, Discovered from/Discovered work, literal unknown kind/direction | `studio-presentation-qa.mjs`: 43 children, eight mixed groups, at most 15 rows per group, exact links, total/range, keyboard Previous/Next, independent retained pages, refresh/shrink/removal/regrowth clamping and local boundary focus; `studio-attention-qa.mjs` enforces the same page bound |
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
current positive floor of 1,318 tests. The initial integrated candidate passed all 1,265 tests,
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
  preserves relation type/status/role and displays at most 15 rows per named
  kind/direction group. Each group has a total/range and Previous/Next controls.
  Pages survive observations independently, clamp on shrink, and recover focus
  to an enabled local control or range; removal of a focused group returns focus
  to the Relations summary. This restores the retired console's paging contract.
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

- Grouped relations at [1440px](studio-cutover/relations-1440.png),
  [375px](studio-cutover/relations-375.png) and
  [720 CSS pixels](studio-cutover/relations-720.png): mixed groups have distinct
  headings/dividers, wrapped literal tracker text, ranges and reachable paging
  controls. Children and Blocks retain independent last pages; boundary focus
  remains visible. These captures replace the earlier incremental relation view.
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
  typed relation links. The repair's grouped pagination captures above supersede
  its earlier incremental relation disclosure.
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

## Structured evidence navigation repair

The review finding at `1ce1e59` identified lost structured evidence actions after
retiring the legacy renderer. Studio now renders a section containing an absolute
HTTP(S) URL as an explicit new-tab link, with no credentials or whitespace.
Unsafe URLs and local paths stay literal. Markdown links share the same policy.
A section's `commentId` links to `?tab=history&comment=<id>` in that Bead's
Workspace. The selected source is revealed alongside the bounded recent history,
opened and focused on keyboard activation even when older than the first eight
comments. Repeated activation reopens it. Stable comment keys retain disclosure
and focus through observations; unresolved references explicitly say the source
is unavailable. Attention can navigate to the same exact Workspace source without
loading history or requesting evidence destinations in advance.

`studio-evidence-qa.mjs` seeds actual native merger rejection, verified-merge and
host-recorded Planning approval records through the isolated fixture's `bd` CLI,
asserts their server projections, and exercises bare links, candidate/review/source
navigation, an older source, repeat keyboard activation, external-comment refresh,
missing sources, unsafe/credential links and no automatic external requests or
commands. It also captures 1440/375/720 layouts. Run the Presentation group in
`studio-cutover.ps1`; it includes both the existing presentation smoke and this
native evidence regression. `attention.test.mjs` covers the shared URL policy.

The repaired native evidence probe and existing presentation smoke passed on
2026-11-16 UTC against a fresh isolated stack, with zero page errors, external
requests or POSTs; exact teardown succeeded and `aspire ps` returned `[]`.
Reviewed captures at [1440px](studio-cutover/evidence-1440.png),
[375px](studio-cutover/evidence-375.png) and [720 CSS pixels](studio-cutover/evidence-720.png)
show readable wrapped source IDs/links and explicit unresolved evidence without
horizontal overflow. Raw results and captures remain in
`TestResults/repair1-evidence-rerun`; the first attempt's assertion timing failure
and successful teardown are retained separately in `TestResults/repair1-evidence`.

Repair validation also passed a frontend clean install, 57 component tests,
explicit TypeScript check and production build; warning-as-error solution build;
and disposable Release Web publish. The complete retained .NET suite passed
**1,265 tests, zero failures/skips in 278.195 seconds**, measured around the entire
normal command with build, fixture publishing and teardown and unchanged
six-thread parallelism. Evidence is `TestResults/repair1-suite.log`,
`repair1-evidence-suite.xml` and `repair1-suite-timing.json`. This measures the
working repair candidate based on `1ce1e59`; the latest integrated main gate
remains Merger-owned after integration.

## Grouped relation parity repair

The subsequent Review finding identified that incremental disclosure mixed
relation kinds and eventually exceeded the retained 15-row bound. Studio now
groups by exact native kind and direction, with independent Previous/Next pages,
totals/ranges, shrink clamping and local focus recovery. Unknown kinds retain
their literal kind/direction; incomplete observations never claim an empty graph.
The Presentation probe covers 43 children and eight mixed groups, every child
reachable, refresh/shrink/removal/regrowth, both keyboard boundaries, removed-row
and removed-group focus, and 1440/375/720 reflow. Attention's former 30-row
assertion now enforces 15 rows and the second-page range.

On 2026-11-16 UTC the final Presentation layout probe passed in 17.201 seconds;
the native evidence regression also passed (43.732 seconds). Both recorded zero
page errors, external requests or POSTs. Attention passed all nine groups in
112.433 seconds with 14 expected POSTs and zero page errors. Exact isolated
AppHosts stopped successfully; final `aspire ps` returned `[]`. Logs/captures
remain under `TestResults/repair2-{relations,layout,attention}`.

Frontend clean installs, 57 tests and production typecheck/build passed, as did
the warning-as-error solution build (zero warnings/errors) and disposable Release
Web publish. The complete 1,265-test retained suite passed with zero failures or
skips in **291.893 seconds**, including build, fixture publication and teardown
at unchanged six-thread parallelism. Evidence: `TestResults/repair2-suite.log`,
`repair2-relations-suite.xml` and `repair2-suite-timing.json`. This is the working
repair candidate based on `747ffbb`, not latest integrated-main timing. The
post-integration measurement and prior physical-sleep/manual-pilot limitations
remain unchanged. No deployment was performed.

## Native Attention convergence repair

Exact-head QA on `71250f2` failed twice before the externally created paused
Bead appeared, with a native source timeout and healthy transport. Its complete
1,265-test correctness run passed but took **343.315 seconds**, exceeding the
300-second budget. Those failures supersede the earlier passing repair evidence;
the old passing timings do not clear the performance gate.

The unchanged Attention probe passed on the first fresh repair fixture. A
controlled source regression nevertheless reproduced failed convergence: charging
every Git and Beads command 1.6 seconds left five paused Beads unpublished at the
existing 30-second browser bound. The observer previously recreated its validator
and repeated immutable branch-syntax checks on every sweep. Reusing the causal
validator change from `a0db910` makes that regression pass, including a transient
comment timeout, without replaying completed comment reads. Only successful
literal syntax is retained; repository identity and tracker checks stay fresh,
and invalid/contextual branch names are rechecked. This establishes one concrete
slow-native progress defect; it does not identify which process timed out in the
earlier QA runs.

The browser probe now preserves the last 64 existing Attention responses in
`attention-observations.json`, with per-source health and attempt times, for
future timeout diagnosis. No requests, wait extensions or removed assertions
are introduced. Source and command budgets, command authority and committed
six-thread test parallelism remain unchanged.

Repair validation on 2026-11-16 UTC passed all **1,268 tests, zero failures/skips
in 281.282 seconds**, including normal build, fixture publication and teardown
at the unchanged six-thread cap. The command used `--minimum-expected-tests 1268`;
log, XML and stopwatch metadata are `TestResults/repair3-suite.log`,
`repair3-suite.xml` and `repair3-suite-timing.json`. This clears the measured
candidate overrun; latest integrated-main timing remains Merger-owned after
integration. No suite cases, native boundaries or parallelism were removed.

The focused source/validator run passed 27 tests; frontend clean install,
57 component tests, explicit typecheck/build, warning-as-error solution build
and disposable Release Web publication passed. Both fresh repaired native
Attention fixtures passed all nine groups with 14 expected POSTs and zero page
errors (128.969 and 114.036 seconds). The first also reran shell and smoke checks;
all three exact stacks, including the unchanged diagnostic run, stopped
successfully. Fresh desktop/375/720 captures were reviewed for bounded layouts,
wrapping, reachable controls and separate transport/source health. Logs and
captures remain under `TestResults/repair3-{repro,browser,attention-repeat}`.
Prior physical-sleep/manual-pilot limitations remain; no deployment occurred.

## Synchronized recovery after hosted rejection

The resumed candidate incorporates main `d1a664e` (PR54) and `a5cbf89` (PR46)
without discarding retained Studio work. The former fixes the rejected native
console test: `hold` lacked its required PID-path argument and could exit before
the unchanged 50ms timeout assertion. The retained replacement starts
`hold-published-pid`, verifies bounded readiness and the actual PID, and preserves
timeout, cancellation, repeat-wait and native-settlement assertions.

The first combined run at `c3a0b47` discovered 1,318 cases and failed only the two
retained slow-Attention component rows in 94.071 seconds. Their recording wrapper
bypassed the new scripted runner's target-path setup. They now use the existing
lightweight observation fixture and set the wrapped runner's path explicitly;
both simulated-cost and transient-timeout assertions remain intact. No new
native journey or test case was added for this repair.

The complete command at `77074a05a4aaa619d7517bdb0e32d505cd358c2c`, with
`--minimum-expected-tests 1318`, passed **1,318 tests, zero failures/skips in
95.242 seconds**, including build, fixture publishing and teardown at six threads.
Evidence: `TestResults/resume-1931-repaired.{log,xml}` and
`resume-1931-repaired-timing.json`. The retained main `d1a664e` baseline passed
1,304 tests in 90.435 seconds after reboot; this candidate adds 14 cutover cases
and took 4.807 seconds (5.32 percent) longer. Different checkout/temp roots and
machine activity prevent attributing that entire difference to the code. Both
240-second operating and 300-second hard budgets pass on this candidate;
latest-main measurement after integration remains required. The focused run
passed all 26 selected assertions but returned exit 9 because its requested
minimum was mistakenly 29; the successful complete run covers those same cases.

Clean npm installs, all 57 frontend tests, production TypeScript/build,
warning-as-error solution build and disposable Release Web publish passed.
The initial shell browser run sampled heading focus before React's navigation
effect completed. The probe now waits for actual heading focus before the same
assertion, without a blind delay or changing application behavior. Its failed
log and successful exact-stack teardown remain in `TestResults/resume-1931-browser/Default`;
fresh validation uses separate output directories.

Fresh Default, Presentation, Recovery and Commands groups passed on the synchronized
candidate. Attention passed nine groups with 14 expected POSTs and no page errors;
Explorer imported 81 native Beads; Digest persisted a real checkpoint; stalled
read/reconciliation and runtime retry probes retained their original assertions.
Presentation retained 43-child grouped paging and actual native structured
evidence navigation without external requests or commands. Recovery changed the
server epoch while preserving two drafts and converging three burst edits;
intake/readiness and fifth-attempt cleanup passed their actual guarded requests.
Logs, result JSON and fresh desktop/375/720 captures are under
`TestResults/resume-1931-browser`, with group logs `resume-1931-*.log`.
Reviewed Overview against the approved reference and inspected narrow Workspace,
relations, evidence and recovered drafts: coherent hierarchy, readable wrapping,
reachable controls, visible focus and no asserted page horizontal overflow.
Every group uses discovered direct endpoints and finally-stops its exact stack.

Invocation also passed its presentation, native Planning and native Delivery
stream probes, including exact identity, reconnect cursor without duplicate
output, completion without retry, follow/Escape focus and narrow effective
200-percent layout. All 18 probes passed across the five final groups. All six
stacks started in this dispatch (including the initial failed shell probe) stopped
successfully; final `aspire ps --format Json` returned `[]`. The remaining edits
after the complete .NET run are this evidence and the browser-only focus wait;
the tested .NET/frontend application sources are unchanged. No hosted run or
production deployment was performed. Physical sleep and long manual-pilot limits
remain as described above.
