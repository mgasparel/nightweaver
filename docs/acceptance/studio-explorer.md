# Studio Explorer acceptance

ns-mbd.9 migrates Explorer and current-project header search onto the existing Studio router and shared query coordinator. [Design and interaction policy](../design/studio-explorer.md) and the [bounded resource contract](../design/operational-observations-v1.md) describe the additive paging key; legacy APIs remain covered.

## Checks

- `dotnet build Nightweaver.slnx -warnaserror`: passed, zero warnings/errors.
- `npm test` and `npm run build` in `src/Nightweaver.Web/Studio`: all 35 tests passed; TypeScript/Vite build passed.
- Complete retained suite: `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename timing.xml --show-slowest-tests 20`: all **1,201 tests passed**, zero failures/skips. Actual wall time including build, fixture publishing and teardown was **434.032 seconds** at unchanged committed parallelism. This exceeds five minutes; it does **not** establish timing acceptance. The existing pool-concurrency test took 388.963 seconds; the new 2,000-row shared-Explorer test took 3.487 seconds. These observations do not establish a cause or a controlled main-branch comparison. Latest-main timing remains to be measured after integration.
- `studio-explorer-qa.mjs`: passed on a fresh exact isolated QA AppHost. The probe imported 81 disposable Beads, verified both HTTP and DOM 40-row bounds, all five views, native external title/dependency/readiness changes, empty results, URL reload/Back/Forward, valid page retention, disappearing page clamping with explanation and safe focus, Workspace identity/query context, current-project header search, and deferred cross-project controls without an unsupported request. A deterministic source-health response verifies retained stale results. No uncaught browser errors.
- `studio-qa.mjs`: passed direct routes, dotted legacy IDs, query history, keyboard/focus, missing asset/API 404s, foreign-project behavior and unavailable project dialogs at 1440/375/720 CSS pixels, with zero browser errors/mutations.

An earlier native Ready-view observation reached the existing two-second source timeout and correctly displayed unavailable. The final fresh probe passed native readiness with an explicit Refresh. Source timeouts, backoff and observation budgets were not widened. Earlier development probes also caught fixture blocker metadata and locator issues, corrected in the retained probe/tests. The complete suite and final browser probes above are the final results.

The exact isolated stack was stopped successfully in `finally`. Browser output remains in ignored `TestResults/studio-explorer` and `TestResults/studio`; the final backend XML is `TestResults/timing.xml`. Invocation-local build/suite logs and stopwatch output are retained under `H:/Nightweaver/prod/tmp/nightshift/ns-mbd.9/`.

## Visual evidence

Visually reviewed desktop, 375px and 720px captures for hierarchy, readable controls, source-state distinction and no horizontal page overflow. Top and bottom pagination keep the bounded list navigable. [Probe results](studio-explorer/results.json) record the final checks.

| State | Desktop | 375px | 720px |
| --- | --- | --- | --- |
| Bounded inventory | [capture](studio-explorer/inventory-1440.png) | [capture](studio-explorer/inventory-375.png) | [capture](studio-explorer/inventory-720.png) |
| Disappearing page explanation | [capture](studio-explorer/clamped-1440.png) | [capture](studio-explorer/clamped-375.png) | [capture](studio-explorer/clamped-720.png) |
| Retained stale search | [capture](studio-explorer/retained-1440.png) | [capture](studio-explorer/retained-375.png) | [capture](studio-explorer/retained-720.png) |
