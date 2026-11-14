# Studio shell acceptance

Verified on the ns-mbd.1 branch on 2026-11-14. The existing console remains at `/`; `/studio` enters the candidate. Operational observations and mutations are intentionally left for the subsequent vertical slices and are visibly unavailable here.

## Checks

- `dotnet build Nightweaver.slnx -warnaserror`: passed, zero warnings/errors. Vite emits an informational dependency directive diagnostic for React Router's `use client`; this client-only bundle has no server-component boundary.
- Focused `StudioWebTests` and `WebHostIntegrationTests` run: 28 passed. The standalone host tests run with an empty PATH and load Studio successfully without Node. HTTP tests verify bootstrap identity without tracker access, all direct routes and their hashed assets, and missing-asset/API 404 boundaries.
- Complete `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename ns-mbd.1.xml --show-slowest-tests 20`: 1,008 passed, zero failed/skipped. Measured wall time including build, fixture publishing and teardown: **3m 43.569s**, with committed normal parallelism. This measures this implementation branch, not a later main revision.
- A fresh source-only copy from the staged index, with no bin/obj/node_modules/generated bundle, passed `dotnet publish .../src/Nightweaver.Web/Nightweaver.Web.csproj -o .../published -warnaserror`. Automatic npm restore and Vite build ran; published output includes `wwwroot/studio/index.html` and hashed JS/CSS plus compression variants, with no Studio sources or npm dependencies. The copy and publish output remain in invocation tmp under `cold-build`. An additional ad hoc published-process launch/termination check was rejected by automatic approval review as `blocked by policy` and did not run; the automated standalone tests above provide the empty-PATH runtime evidence.
- Isolated `Nightweaver.QA.AppHost` started with `--isolated --non-interactive`, waited healthy and used its discovered direct endpoint `http://localhost:22153`. `studio-qa.mjs` passed: direct reload, keyboard route activation/heading focus, Back/Forward, query preferences, Workspace section history, escaped legacy Bead identity and reciprocal legacy link, foreign project and unknown route, unavailable project dialogs/Escape/return focus. No page/console errors and **zero browser mutations**. `smoke.mjs` then passed against the unchanged legacy console and seeded native tracker.
- The exact isolated AppHost stopped successfully afterward. Production was not deployed or operated.

## Visual review

Reviewed the candidate and [approved reference](../design/studio-reference.md) at matching 1440, 375 and 720 CSS-pixel widths. No document overflow. The candidate preserves navy/ink surfaces, violet navigation, amber Attention accents, type hierarchy, 224px desktop sidebar, 74px top bar, matching panel heading bands and paired desktop panel rows. Narrow layouts stack the panels; navigation remains reachable by horizontal scrolling and keyboard focus. Operational content differs deliberately: unconnected state replaces prototype sample metrics, Agent activity and projects. Focus outlines are visible in the captures because route headings receive navigation focus. 720 CSS pixels documents responsive reflow; full-product zoom acceptance belongs to the later parity/cutover gate.

| Viewport | Candidate |
| --- | --- |
| 1440 CSS pixels | [Overview](studio-shell/overview-1440.png) |
| 375 CSS pixels | [Overview](studio-shell/overview-375.png) |
| 720 CSS pixels | [Overview](studio-shell/overview-720.png) |

Additional Attention and freshly rendered prototype captures remain in `tests/Nightweaver.QA.AppHost/TestResults/studio`. The prototype HTML remains byte-identical to the maintainer's source (SHA256 recorded in the migration guide); its original CRLF bytes are intentionally preserved. `git -c core.whitespace=cr-at-eol diff --cached --check` accepts those preserved line endings.

## Dotted Workspace route repair

The reviewer identified that the generic `nonfile` fallback rejects hierarchical Bead IDs. Four new HTTP cases reproduced 404s for `ns-mbd.1` and `ns-mbd.1.2`, with and without Workspace query state. A dedicated `/projects/:projectId/beads/:beadId` fallback now serves the entry for these IDs, while the existing asset/API 404 checks and an additional nested missing-JS check remain green.

- `dotnet build Nightweaver.slnx -warnaserror`: passed with zero warnings/errors.
- `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 1013 --report-xunit-xml --report-xunit-xml-filename ns-mbd.1-repair.xml --show-slowest-tests 20`: **1,013 passed**, zero failed/skipped; **3m 24.060s** wall time including build, fixture publishing and teardown, normal committed parallelism. This measures the implementation branch, not latest main.
- The extended `studio-qa.mjs` passed three consecutive runs against isolated direct endpoint `http://localhost:12734`: legacy translation of both dotted IDs, successful reloads with and without History query state, existing keyboard/history/dialog checks and missing-asset/API 404s. Each run covered 1440, 375 and 720 CSS pixels with zero browser errors, mutations or document overflow. Reviewed the fresh overview captures at all three widths; layout is unchanged.
- An initial browser run passed the new routing checks but exposed a test race around the queued native dialog `close` event. Checking focus immediately could fail, or moving focus before React's `onClose` could interrupt the next keyboard navigation. The script now waits for that event before asserting focus or continuing; application focus behavior is unchanged.
- `smoke.mjs` passed against the existing root console and seeded tracker. Both isolated AppHost instances stopped successfully; `aspire ps --format Json` returned `[]` afterward. New captures remain under invocation tmp in `20261114T1355482204018Z-implementer/studio-repair`; inherited captures and files were preserved.
