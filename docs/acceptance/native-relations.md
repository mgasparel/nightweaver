# Native Type and directional relations acceptance — ns-j4v.1

Implemented from `main` d1de8a27d311dedef14437e1b2f210ffd4569498 in the assigned isolated worktree. No inherited changes or downstream findings were present on dispatch.

## Reproduction and HTTP verification

Before implementation, `WorkspaceReadsNativeChildrenAndParentWithoutTrackerMutation` created a real disposable epic, feature child and nested bug. Native `bd dep list --direction up/down --json` found the child and parent, while `GET /api/beads/{epic}` returned an empty relations array. The regression failed at `Assert.Single(children)` with “The collection was empty.” It passes with the new native read path; the same smoke verifies a non-epic parent, linked native issue type, unchanged native inventory and read-only flags on every tracker request.

`WorkspaceRelationsWebTests` adds nine cheap HTTP cases for successful empty/complete reads, incoming/outgoing/total failures, malformed/blank/foreign edges, equivalent canonical/native duplicates, mixed hierarchy/blocking/lineage, unknown nonblank relationship and issue types, hostile titles, and missing targets. Verified rows survive the other direction's failure; only one exact detail read occurs, with no linked-item subprocess reads. The existing required inventory/detail parsers and workflow identity guards remain authoritative.

Focused validation passed: four `WebBeadWorkspaceBoundaryIntegrationTests` and nine `WorkspaceRelationsWebTests`, using fully qualified filters and positive minimum expected counts. JavaScript syntax checks passed for the application and relations QA script.

## Live browser verification

Used the repository QA AppHost with `NIGHTWEAVER_QA_SCENARIO=relations`, `aspire start --isolated --non-interactive`, `aspire wait web`, and the direct HTTP endpoint returned by `aspire describe`. Ran headless `relations-qa.mjs` and stopped the exact AppHost in `finally`, including after every rerun.

The disposable native tracker had an epic with 17 children, a nested spike under a feature, a standalone decision, and independent incoming/outgoing blocking and discovered-from edges. Assertions passed for native counts/deduplication, labeled Type, parent/child exact keyboard navigation and destination heading focus, 15-row pagination with every child reachable, boundary range focus, visible focus outlines, refresh page retention, loading and successful empty states. Inventory was identical before/after and the browser made zero writes.

Presentation-only HTTP response substitutions covered partial and total relation failures, missing target metadata, custom issue/relationship types, hostile titles and very long literal relationship kinds. Directional native failures themselves are covered at the HTTP boundary above. No unexpected console or page errors occurred. Existing `workspace-qa.mjs`, `explorer-qa.mjs`, and `planning-readiness-qa.mjs` also passed, each against its own fresh default isolated fixture with teardown. Their deliberate 404/409/503 cases were expected; Planning readiness rejected stale identity with 409 and persisted the explicit authorized action with 200.

## Visual review

Reviewed the final [1440px desktop](native-relations/relations-1440.png), [375px narrow](native-relations/relations-375.png), and [720 CSS px](native-relations/relations-720.png) captures. Group headings distinguish hierarchy, blocking and lineage; native Type remains separately labeled. Long titles and type chips wrap, pagination uses the console's existing 44px controls, and keyboard focus is visible. All three widths passed the page-overflow assertion.

Additional evidence: [pagination boundary focus](native-relations/pagination-focus.png), [partial observation](native-relations/partial.png), [total failure](native-relations/failed.png), and [unknown/missing/hostile values](native-relations/unknown.png). The latter also passed overflow checks at 375px and 720 CSS px.

## Runtime and limits

The first complete build/default-suite gate passed 952 tests with zero failures or skips in 164.590 seconds wall time, including build, fixture publishing and teardown. The final gate after visual refinements also passed all 952 tests with zero failures or skips: `dotnet build Nightweaver.slnx -warnaserror` followed by `dotnet test --solution Nightweaver.slnx --minimum-expected-tests 952`, in 191.715 seconds total wall time (suite 183.668 seconds). Both gates include build, fixture publishing and teardown, remain under five minutes, and preserve committed six-thread parallelism.

These sequential native reads are not an atomic snapshot of external tracker writes. Observation coverage is explicit per direction; required Bead identity/data failures retain HTTP failure behavior. No scheduling, mutation, Catch-Up Digest grouping, Product/Plan role derivation, tracker schema or production deployment behavior changed. Detailed raw logs/results remain in ignored `TestResults/ns-j4v-*` and `TestResults/relations/`.
