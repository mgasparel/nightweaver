# Studio migration shell

The captured [Studio prototype](nightweaver-multiproject-prototype.html) is a byte-for-byte copy of the previously uncommitted source-repository prototype (SHA256 `EF2366BFF1D40B58FF41D15B80FEA443303E657ABC96DDF5CA72A686B8E1A498`). The [reference guide and captures](studio-reference.md) from main commit `3beac39` are included unchanged following the maintainer's handoff. Variant A supplies the nocturnal palette, fixed current-project sidebar, heading rhythm and Delivery/Attention then Projects/Catch-up overview composition. Sample projects, counts, Agents and mutations from that prototype are not runtime observations.

Studio is a candidate at `/studio`. Its entry redirects with history replacement to the configured `/projects/:projectId/overview`. The existing console stays at `/` until the parity/cutover Bead. `/projects/:projectId` also redirects to Overview; project routes support `overview`, `delivery`, `attention`, `catch-up`, `beads` and `beads/:beadId`. ASP.NET serves the bundle and direct-route fallback only under `/studio` and `/projects`; missing assets and unknown APIs retain 404 responses. Studio translates `#bead/<encoded-id>` on entry or navigation into the current project's dedicated Workspace route. Existing root-console hash links keep their existing behavior, and Workspace provides a reciprocal legacy link.

The URL owns the current project and Bead, Workspace section (`tab=summary|history|output`), and Explorer preferences (`q`, `view`, with page reset when saving a changed search). Browser history retains query context. Workspace sections use native links with current-page semantics and standard keyboard activation. Navigation moves focus to the route heading; query-only navigation preserves the active control. Multi-project controls open an explicit unavailable dialog with Escape and return-focus support. A foreign project route never reads observations for the configured project under the foreign identity.

`Studio/src/context.ts` reads configuration identity only from `/api/studio/context`. This response carries no runtime/tracker health, counts, occupancy or activity claims. Operational surfaces remain explicitly unconnected and link to the usable existing console. The shell has no mutation transport. Later slices own reconciliation, observations, guarded commands, drafts/disclosures and identity-bound invocation inspection; these are not simulated here.

## Build and publish

Node >=22.12 and npm are build-time prerequisites; CI uses Node 24. `dotnet build Nightweaver.slnx -warnaserror` and `dotnet publish src/Nightweaver.Web` restore the locked npm dependencies and run TypeScript checking plus Vite compilation automatically. Incremental targets track the dependency lock and frontend sources. Generated `wwwroot/studio` assets are ignored, discovered dynamically on the first build and copied to build/publish output. Source, dependencies and Node executables are not published. The deployed host requires only .NET, with no Node server. For frontend-only iteration, run `npm ci` and `npm run build` in `src/Nightweaver.Web/Studio`.

The bundle uses [Vite's production build](https://vite.dev/guide/backend-integration) and [React Router browser history](https://reactrouter.com/api/declarative-routers/BrowserRouter). `AppShell` owns navigation, configured scope and bootstrap status. Feature route skeletons compose shared Panel, Status, Icon and Unavailable primitives, without embedding observation or command policy in rendering.

## Operational parity inventory

These flows remain available in the root console; they must move before cutover. A Studio navigation link or unavailable panel grants no workflow authority.

| Surface | Existing operations and safeguards | Migration owner |
| --- | --- | --- |
| Attention | Guidance Save Note / Send & Resume (`POST /api/beads/{id}/decide`); Reset Budget & Retry (`retry`); two-step Abort & Close (`abort`). Compare displayed stage, full pause labels and repair count; same-origin request policy, fresh workflow validation, no mutation replay. | ns-mbd.4 |
| Full Bead Workspace | Summary, description/criteria, workflow/source facts, native hierarchy/blocking/lineage links, bounded comment history and retained environment context. Drafts/disclosures survive refresh. | ns-mbd.4 |
| Delivery | Observed bounded bays and singleton Merger; distinct tracker/runtime availability; exact runtime/slot/invocation-bound output, cursor/reconnect/deduplication, bounded output, follow controls, diagnostics and retained invocation identity. No inferred running state from tracker ownership. | ns-mbd.5 |
| Catch-up | Window selection (`GET /api/digest`), bounded milestone threads and child disclosures, Workspace links; explicit checkpoint (`POST /api/digest/catch-up`) without claiming later concurrent activity was read. | ns-mbd.6 |
| Explorer / search | Ready/All/Active/Closed views, full-dataset search, bounded pages, created-ID discovery and retry-refresh without reposting. | ns-mbd.9 |
| Intake / readiness | Product feature/bug intake (`POST /api/beads`) with validation, open/unassigned backlog only; guarded explicit Ready for Planning (`POST /api/beads/{id}/ready-for-planning`) for eligible Product Beads. Planning remains manually authored. | ns-mbd.10 |
| Housekeeping | Independent bounded inventory/search, retained unsafe/denied evidence and owner/revision-guarded Retry (`POST /api/cleanup/{id}/retry`); cleanup is separate from successful Delivery and pause state. | ns-mbd.10 |
| Global | Accessible navigation/dialogs, direct and legacy links, fresh/stale/unavailable observations, visible failed-read recovery, responsive layouts, literal untrusted text and bounded collections. Root console stays available until parity is verified. | ns-mbd.3 / ns-mbd.7 |

## Verification

`StudioWebTests` covers configuration isolation, built assets and direct-route fallback boundaries. The standalone host test loads Studio with an empty PATH. For live visual and interaction checks, follow `tests/Nightweaver.QA.AppHost/README.md`, discover the isolated direct web URL, run `node studio-qa.mjs` and `node smoke.mjs`, then stop that exact stack. Studio QA retains reference/candidate screenshots at 1440, 375 and 720 CSS pixels, verifies keyboard navigation/focus, reload, Back/Forward, query state, legacy translation, foreign scope and unavailable dialogs, and rejects all browser mutations/errors.
