# Rejection and human-pause message acceptance — ns-psu

Verified on 2026-11-13 in the isolated `nightweaver/ns-psu` worktree, based on `c81efcb9522c58833a2bab23584865d51e830bd5`. The retained root checkout and production runtime were preserved.

The shared projection presents Merger conflict/design/local-check/remote-check reports, current and historical needs-human diagnostics, supplemental pause and cleanup failures, and nested Planning questions. Reported check failures remain distinct from failed host verification. Source identity, exact comments, additional fields, log references and verbose findings remain inspectable. New unsupported or malformed pause evidence qualifies uncertainty rather than asserting an older explanation is current. Later ordinary notes stay visible without replacing structured questions.

`schema-messages-qa.mjs` passed against an isolated Aspire stack using actual comments seeded into a disposable Beads tracker. It exercised history, Workspace selected context and Human Attention Desk; exact original/source agreement; current/historical messages; independent corrective prose; newer unsupported diagnostics with notes; malformed/foreign/mixed payloads; question/recommendation/tradeoffs; nested literal findings; hostile text/URLs; long paths; keyboard disclosure focus/state across refresh; and no page overflow at 1440, 375 and 720 CSS pixels. Observed page errors, workflow mutations and external requests were all zero. Payload references remain literal and selectable.

Visual review confirmed readable reason/cause/findings/retention hierarchy, compact evidence disclosures, wrapped source IDs, reachable disclosures and visible focus. Representative retained screenshots:

- [Desktop selected diagnostic](schema-messages/message-1440.png)
- [375 CSS pixel selected diagnostic](schema-messages/message-375.png)
- [720 CSS pixel / 200% effective zoom selected diagnostic](schema-messages/message-720.png)

Full console, Desk and question screenshots plus machine-readable results remain under `TestResults/schema-messages`. The browser regression caught disclosure focus loss during replacement; rendering now captures actual focus/open state immediately before replacement and restores the same source control afterward, with a heading fallback when the source is unavailable.

`workspace-qa.mjs` passed separately against a fresh default disposable stack: legacy questions, source/history navigation, stale action guards, failed refresh recovery, route/action races, drafts, Save Note, Send & Resume, Reset Budget & Retry, confirmed Abort, Markdown safety and responsive layout. Each exact AppHost was stopped in `finally`, including earlier unsuccessful harness runs. No desktop Computer Use was used.

The final complete retained suite passed **844 tests, zero failures/skips, in 151.633 seconds wall time** with normal six-thread scheduling unchanged:

```powershell
dotnet test --solution Nightweaver.slnx --minimum-expected-tests 844 --report-xunit-xml --report-xunit-xml-filename timing.xml --show-slowest-tests 20
```

The measurement includes normal build, fixture publishing, tests and teardown. XML evidence is `TestResults/timing.xml`; invocation evidence is retained at `H:/Nightweaver/prod/tmp/nightshift/ns-psu/20261113T2258219400807Z-implementer/final-full-suite.log` and `final-suite-time.txt`. The 31 added fast cases cover shape/type/enum/duplicate/identity/depth handling, every escalation cause, both retention generations, rejection variants, supplemental separation, source uncertainty, structured questions and unchanged action availability. No retained tests or native boundary coverage were removed. JavaScript syntax checks and `git diff --check` also passed.

Workflow state, classifier/verifier contracts and schema producers are unchanged. Display never authorizes execution or verifies a reported check. The Orchestrator owns subsequent stage transitions and closure after merge.
