# Studio Product actions acceptance

Verified on the isolated cleanup QA AppHost using its dynamically discovered
direct HTTP endpoint. The stack was stopped successfully in `finally`.

`planning-readiness-qa.mjs --studio` created disposable Product `qa-jpp` through
the real same-origin intake form. Validation and a simulated disconnected
submission retained all input; navigating away and back produced no automatic
submission. The subsequent explicit request returned 201, leaving the Product
open, unassigned and without Planning authorization. A deliberately stale expected
identity returned 409 without changing readiness. Explicit Refresh/review and
Ready for Planning succeeded; the native HTTP read and a reloaded Studio
Workspace both confirmed persisted readiness. There were no uncaught page errors.
See [Product request evidence](studio-product/product-result.json).

`cleanup-qa.mjs --studio` exercised native obligation `qa-9pk`: a stale revision
returned 409, then an eligible request returned 202 and the live owner resolved
the obligation on total attempt five. The temporary resource was actually absent,
the delivered Bead was unchanged, and the paused environment's protected sentinel
retained its bytes. Queue acceptance was displayed separately from confirmed
resolution. See [cleanup evidence](studio-product/cleanup-result.json).

Only subsequent read projections were substituted for 27 obligations, eight-row
pagination, search, status selection, long denied paths, expired acknowledgement,
stopped owner and unavailable evidence. Existing disclosures and focus survived
Refresh. Search focus and page overflow were checked at all three viewports;
there were no uncaught page errors. The native Retry requests were not mocked.

Representative reviewed captures:

| Surface | Desktop | 375 CSS px | 720 CSS px |
| --- | --- | --- | --- |
| Product form | [1440](studio-product/intake-1440.png) | [375](studio-product/intake-375.png) | [720](studio-product/intake-720.png) |
| Readiness receipt with retained observation | [1440](studio-product/readiness-1440.png) | [375](studio-product/readiness-375.png) | [720](studio-product/readiness-720.png) |
| Cleanup and resource evidence | [1440](studio-product/cleanup-before-1440.png) | [375](studio-product/cleanup-before-375.png) | [720](studio-product/cleanup-before-720.png) |

The readiness captures intentionally retain the overdue pre-command observation
beside the confirmed command receipt. Studio removes the completed command button
while waiting for reconciliation; reload subsequently confirmed the new observed
readiness. Earlier probe attempts exposed a too-strict whole-Workspace freshness
gate and an obsolete button during delayed reconciliation. The final implementation
preserves server-side expected-identity validation and explicit conflict recovery,
and the final Product and cleanup probes both passed on a fresh fixture.

Local validation also includes TypeScript/Vite compilation, Studio command and
coordinator tests, the existing native HTTP intake/readiness/cleanup boundary
tests, and the complete candidate .NET suite. Exact counts and wall time are
recorded in the implementation Bead comment. Hosted CI remains Merger-owned.
