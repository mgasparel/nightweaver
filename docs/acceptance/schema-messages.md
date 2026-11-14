# Rejection and human-pause message acceptance — ns-psu

Verified on 2026-11-13 in the isolated `nightweaver/ns-psu` worktree, based on `c81efcb9522c58833a2bab23584865d51e830bd5`. The retained root checkout and production runtime were preserved.

The shared projection presents Merger conflict/design/local-check/remote-check reports, current and historical needs-human diagnostics, supplemental pause and cleanup failures, and nested Planning questions. Reported check failures remain distinct from failed host verification. Source identity, exact comments, additional fields, log references and verbose findings remain inspectable. New unsupported or malformed pause evidence qualifies uncertainty rather than asserting an older explanation is current. Later ordinary notes stay visible without replacing structured questions.

`schema-messages-qa.mjs` passed against an isolated Aspire stack using actual comments seeded into a disposable Beads tracker. It exercised history, Workspace selected context and Human Attention Desk; exact original/source agreement; current/historical messages; independent corrective prose; newer unsupported diagnostics with notes; malformed/foreign/mixed payloads; question/recommendation/tradeoffs; nested literal findings; hostile text/URLs; long paths; keyboard disclosure focus/state across refresh; and no page overflow at 1440, 375 and 720 CSS pixels. Observed page errors, workflow mutations and external requests were all zero. Local paths and unsafe URLs remain literal and selectable; safe HTTP(S) evidence navigation was added in the Reviewer repair below.

Visual review confirmed readable reason/cause/findings/retention hierarchy, compact evidence disclosures, wrapped source IDs, reachable disclosures and visible focus. Representative retained screenshots:

- [Desktop selected diagnostic](schema-messages/message-1440.png)
- [375 CSS pixel selected diagnostic](schema-messages/message-375.png)
- [720 CSS pixel / 200% effective zoom selected diagnostic](schema-messages/message-720.png)

Full console, Desk and question screenshots plus machine-readable results remain under `TestResults/schema-messages`. The browser regression caught disclosure focus loss during replacement; rendering now captures actual focus/open state immediately before replacement and restores the same source control afterward, with a heading fallback when the source is unavailable.

`workspace-qa.mjs` passed separately against a fresh default disposable stack: legacy questions, source/history navigation, stale action guards, failed refresh recovery, route/action races, drafts, Save Note, Send & Resume, Reset Budget & Retry, confirmed Abort, Markdown safety and responsive layout. Each exact AppHost was stopped in `finally`, including earlier unsuccessful harness runs. No desktop Computer Use was used.

The initial implementation complete retained suite passed **844 tests, zero failures/skips, in 151.633 seconds wall time** with normal six-thread scheduling unchanged:

```powershell
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 844 --report-xunit-xml --report-xunit-xml-filename timing.xml --show-slowest-tests 20
```

The measurement includes normal build, fixture publishing, tests and teardown. XML evidence is `TestResults/timing.xml`; invocation evidence is retained at `H:/Nightweaver/prod/tmp/nightshift/ns-psu/20261113T2258219400807Z-implementer/final-full-suite.log` and `final-suite-time.txt`. The 31 added fast cases cover shape/type/enum/duplicate/identity/depth handling, every escalation cause, both retention generations, rejection variants, supplemental separation, source uncertainty, structured questions and unchanged action availability. No retained tests or native boundary coverage were removed. JavaScript syntax checks and `git diff --check` also passed.

Workflow state, classifier/verifier contracts and schema producers are unchanged. Display never authorizes execution or verifies a reported check. The Orchestrator owns subsequent stage transitions and closure after merge.

## Reviewer repair verification

Both findings from the 2026-11-13 static review are addressed:

1. Selection inspects every top-level schema claim, including duplicate fields in either order and invalid array/object schema values. A newer malformed diagnostic or question displaces older supported evidence with an explicit uncertainty fallback. Nested examples and ordinary notes do not become pause evidence. Four fast table-driven cases cover both pause families with and without interposed/later notes, five invalid forms each, exact originals, older history access and unchanged action availability. All 35 `CommentPresentationTests` cases passed.
2. The shared renderer exposes absolute HTTP(S) evidence URLs without credentials, whitespace or backslashes as keyboard-accessible links. Local paths and unsafe schemes stay literal. Enter opens a separate tab with `noopener noreferrer`; links retain focus across refresh. Playwright intercepts the destination locally and proves one explicit navigation to the expected HTTPS PR URL, with no automatic external requests or workflow writes during display, expansion and refresh. Hostile schemes and credential-bearing URLs are checked independently.

The repaired `schema-messages-qa.mjs` passed on all three surfaces, including disposable duplicate-schema diagnostic and question comments followed by ordinary notes. Older supported records remain inspectable. Desktop/375px/720px screenshots were visually inspected; no page overflow or page errors occurred. Machine-readable evidence is `TestResults/schema-messages-repair/result.json`. The [focused PR evidence link](schema-messages/evidence-link-desktop.png) shows explicit navigation affordance and visible keyboard focus. The initial repair harness assertion used visible-text extraction for a hostile URL inside a closed disclosure; it was corrected to inspect retained text, and the full script passed on a fresh fixture.

The retained `workspace-qa.mjs` also passed on a separate fresh default fixture, including real Save Note, Resume, Retry and confirmed Abort plus stale/race guards. Every repair AppHost, including the unsuccessful harness run, stopped successfully through exact-target Aspire teardown in `finally`.

Final repair gates passed: `dotnet build Nightweaver.slnx -warnaserror` (zero warnings/errors), JavaScript syntax checks, and the complete **848-test suite, zero failures/skips, in 154.777 seconds wall time**, including normal build, fixture publishing, tests and teardown. Six-thread scheduling and every retained test are unchanged. This is validation of the retained branch based on locally available main `c81efcb`, without fetching or resetting.

```powershell
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 848 --report-xunit-xml --report-xunit-xml-filename repair-timing.xml --show-slowest-tests 20
```

Report: `TestResults/repair-timing.xml`. Invocation evidence: `H:/Nightweaver/prod/tmp/nightshift/ns-psu/20261113T2330158591141Z-implementer/build.log`, `full-suite.log` and `suite-time.txt`. Earlier invocation evidence and original timing XML remain preserved.
