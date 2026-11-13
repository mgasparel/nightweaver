# Compact Catch-Up lineage threads — ns-1b1.8

Baseline: `855c2b1`, the dispatched main base. Candidate: this PR. Captured on 2026-11-13 using headless Chromium on the isolated Aspire QA AppHost, with its discovered direct HTTP endpoint. The host was stopped after each run. Production was not changed.

Opening the Digest now shows compact thread summaries with linked identity/title, outcome or origin context, latest activity, aggregate counts and an independent activity disclosure. Child rows and root descriptions/evidence appear only after Show activity. Root details retain their own long-summary disclosure; child activity initially previews four entries and offers access to all. Thread and preview state survive refresh by root identity, while new threads start collapsed.

## Density and visual evidence

Both desktop captures use the same fixture, 1440×900 viewport, 24h window, five initially visible threads, long titles/descriptions, mixed completed/open activity and child counts of 8, 1, 4, 3 and 6. Two additional threads remain behind the collection disclosure. No font-size reduction, title clipping or fixed content height is used.

| Initial expanded lineage section | Height |
| --- | ---: |
| Baseline | 4,954.33px |
| Candidate | 904.16px |
| Reduction | 4,050.17px (81.75%) |

The measured section is `#digest-threads-section`, including its heading and collection disclosure, with the Digest body open. The collapsed Digest header remains 93.875px.

| View | Evidence |
| --- | --- |
| Complete baseline lineage content | [Before](digest-threads/baseline-lineage.png) |
| Complete candidate lineage content | [After](digest-threads/candidate-lineage.png) |
| 1440×900 viewport | [Before](digest-threads/baseline-1440.png), [after](digest-threads/candidate-1440.png) |
| 375px, summaries / requested activity | [Summaries](digest-threads/candidate-375-lineage.png), [activity](digest-threads/activity-375.png) |
| 720 CSS px, summaries / requested activity | [Summaries](digest-threads/candidate-720-lineage.png), [activity](digest-threads/activity-720.png) |

Responsive captures also include the newly inserted thread used to verify collapsed defaults. Visual inspection confirmed readable wrapping and reachable controls. Deliberately long titles extend vertically; no page-level horizontal overflow or inaccessible clipping occurs.

## Verification

- `digest-threads-qa.mjs --baseline` and `digest-threads-qa.mjs`: zero default child rows/descriptions; independent keyboard expansion/collapse for one, four and eight children; bounded preview/all/fewer access; root details; surviving activity and focused controls across refresh; newly added collapsed thread; unique IDs and valid disclosure targets; child link identity and real root Workspace navigation; 375/720 CSS px reflow. No page or console errors.
- Updated `digest-qa.mjs`: real checkpoint persistence on a fresh disposable tracker; collapsed activity and 5-thread/4-child-preview/6-standalone bounds; 140/141-character summary boundary; summary/focus persistence; removed-control fallback; window reset; retained-data/read errors; failed/invalid saves; newer activity after catch-up; empty/caught-up and responsive layouts. Only deliberately injected HTTP errors occurred.
- `smoke.mjs`: real host connectivity, console shell and seeded Bead visibility.
- `dotnet build Nightweaver.slnx -warnaserror`: zero warnings/errors.
- `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 644`: all 681 tests passed, zero failures/skips, normal committed six-thread parallelism. Combined build/full-suite wall time was **112.79 seconds**, including test build, native fixture publishing and teardown, within the five-minute budget.
- `git diff --check`: passed.

The canonical design paragraph was reconciled in both repository UI design documents and the explicitly authorized design fields of `ns-1b1` and `ns-1b1.1`; unrelated text and workflow fields were preserved. No downstream findings existed at the initial dispatch. Generated ignored build/browser output is retained for Orchestrator-owned verified completion cleanup. This task remains open until merge.

## Reviewer repair: focused thread crossing the preview bound

The reviewer identified a surviving focused fifth thread being removed from the DOM when incoming activity moved it beyond the five-thread preview. The regression now expands and focuses `thread-4`, prepends a new thread, and refreshes programmatically without moving focus. Against the original implementation (`a35a5e9`), it failed with active focus key `undefined` instead of `activity:thread-4`.

The renderer now identifies the focused thread before replacing the DOM and reveals the full thread collection if that surviving thread moved beyond the preview. This preserves source order, its focused control and activity expansion. The initial five-thread default is unchanged; Show fewer restores that preview. New threads still start collapsed. Both UI design contracts describe this behavior.

The same browser regression passes after repair, including keyboard collapse from the restored control, incoming-thread collapsed state, and Show fewer/all access. [Focused boundary thread after refresh](digest-threads/focused-boundary-refresh.png) shows the original Hide activity control with its visible focus outline. Reviewed fresh 375/720 CSS px screenshots retain readable wrapping and reachable controls without page overflow. Initial section height remains **904.15625px**; the collapsed header remains **93.875px**.

Repair validation on 2026-11-13:

- Fresh isolated Aspire stack, discovered direct endpoint: `digest-threads-qa.mjs`, `digest-qa.mjs` and `smoke.mjs` passed. Both reproduction and passing stacks were stopped in `finally`. Existing checkpoint/window/error handling and navigation checks passed; only intentional HTTP error fixtures occurred.
- `dotnet build Nightweaver.slnx -warnaserror`: zero warnings/errors. `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 644`: **681 passed, zero failures/skips** with unchanged normal parallelism. Combined wall time including build, fixture publishing and teardown: **108.13 seconds** on the repair branch.
- JavaScript syntax checks and `git diff --check` passed. Prior evidence was preserved; fresh browser artifacts are in the repair invocation's temporary directories. No workflow fields or repair counter were changed.
