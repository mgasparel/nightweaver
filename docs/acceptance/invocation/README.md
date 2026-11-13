# Runtime health and invocation inspection

The Factory header now groups Project, runtime, observation time and Target Repository above the execution bays. Fresh occupied observations show **● Live**, fresh empty pools show **◌ Idle · Fresh runtime**, and aged/failed observations show **⚠ Stale**, **✕ Disconnected** or **— Unavailable**. Retained observations include the last heartbeat and explicitly identify the last validated snapshot. A stopped runtime is unavailable, including on a fresh visit; it does not become fresh idle.

Invocation inspection remains a native modal selected by the exact runtime/slot/invocation tuple. Changed Factory polls preserve the selected output, follow mode, scroll and focus. Replacement invocations never retarget inspection. Escape and Close restore the same invocation action or the Factory heading; hash navigation closes inspection before the Workspace takes focus. A queued close from an earlier inspection cannot dismiss a new one. Stream disconnection and reason-only termination do not claim that the Agent completed.

The metadata grid wraps long identities, using full-width Worktree and Branch rows. The internally scrollable dialog retains sticky follow/latest controls and bounded monospace output. Display retention is at most 2,000 lines and 256 KiB of UTF-8, preserving a safe suffix even for one oversized line. Truncation remains disclosed until reset or a new inspection.

## Browser evidence

Captured with the isolated QA AppHost and Playwright. Factory/stream response fixtures cover display and interaction cases; `telemetry.mjs` independently covers the real native fixture and endpoint, EventSource reconnect with Last-Event-ID, duplicate-free output and completion without further retries. Every stack was stopped, including unsuccessful exploratory checks. No desktop Computer Use was used.

- [Busy health header, 1440×900](health-busy-1440.png)
- [Fresh idle](health-idle.png), [stale](health-stale.png), [disconnected](health-disconnected.png), [unavailable](health-unavailable.png)
- [Retained invocation, 1440×900](retained-1440.png)
- [Long metadata, 375px](retained-375.png)
- [Output/follow controls, 375px](output-375.png)
- [Output/follow controls, 720 CSS px (200% desktop reflow)](output-720.png)

`node invocation-qa.mjs`, `node factory-qa.mjs` and `node telemetry.mjs` pass. Assertions cover health states, unavailable/stopped initial observations, malformed snapshots, exact stream identity, replacement polls, line/byte bounds, sanitization, duplicate cursors, reset, terminal outcome copy, follow controls, keyboard containment, Escape/Close/fallback, rapid reopen, Workspace destination focus, long content, and no page overflow or uncaught browser errors. The narrow/zoom views preserve readable text and reachable controls; vertical scrolling exposes long metadata and output. The test waits for layout and brings the toolbar into view before its output captures.

The Factory child remains scoped to health and invocation inspection. Attention preview bounds and integrated first-viewport budgets remain owned by ns-1b1.6; Explorer pagination remains owned by ns-1b1.5.
