# Studio Explorer and current-project search

Studio's Beads route uses the existing tab-owned query coordinator and host-owned operational observer. The URL owns `view`, `q` and `page`; Ready is the operational default. Ready/Blocked use native tracker membership, Active uses tracker `in_progress`, and none establishes runtime execution. All and Closed remain available. Search matches complete titles and IDs within the configured current Project, ordered by priority then ordinal ID.

The four-part Explorer resource key selects a bounded 40-row projection from the shared inventory sweep. It carries total/page counts in data and observation metadata in the standard envelope. Legacy `/api/beads` and three-part resources retain their existing contracts. No per-page tracker reader or second browser cache is introduced. Invalid URL state displays recovery without subscribing to an invalid key.

Live updates retain a valid page. When a fresh observation shows that the page ceased to exist, Studio replaces its URL with the last valid page and announces why. A stale retained result never clamps navigation. Rows use exact ID keys and Workspace links retain query context; Back to Explorer restores it. Removal of a focused row returns focus to the heading, never a different Bead. Query-only changes preserve control focus.

The header search navigates to this Project's All view. Deferred All projects controls retain their unavailable dialog and issue no cross-project search request. Source freshness, disconnected state, last observation time and explicit refresh share the same controls as Attention and Workspace.

Browser verification uses `studio-explorer-qa.mjs` on the exact isolated QA AppHost, including native external title/dependency changes, URL history/reload, bounded pages, page disappearance, focus, Workspace identity, stale retained results and 1440/375/720 CSS-pixel layouts. Component/source tests cover 2,000-row inventory and malformed canonical keys.
