# Studio visual reference

For implementation or visual review of ns-mbd, open [the interactive prototype](nightweaver-multiproject-prototype.html) and select **01 · Studio**, or append `?variant=A` to its URL. Studio is the approved direction. Fieldnotes and Control remain comparison studies.

## Reference captures

These are preserved captures from the prototype QA recorded in ns-gcn on 2026-11-14, not screenshots of the production console. The capture viewport height was 1000 CSS pixels; images include the full page.

| Width | Reference |
| --- | --- |
| 1440 CSS pixels | [Desktop overview](studio-reference/overview-1440.png) |
| 375 CSS pixels | [Mobile overview](studio-reference/overview-375.png) |
| 720 CSS pixels | [Narrow overview](studio-reference/overview-720.png) |

The 720px capture documents responsive reflow; it is not itself evidence that browser zoom was exercised. Production acceptance also requires 200% zoom coverage using the isolated QA workflow.

## Visual contract

Preserve the nocturnal ink/navy surfaces, moonlit text, violet primary actions and amber attention signals. Reuse the prototype's typography, spacing, borders, radii and status hierarchy as the starting design tokens.

Desktop uses a persistent project sidebar, restrained top bar, introductory heading and metrics, then Delivery/Attention and Projects/Catch-up panel rows. Panel headings share a consistent band and paired panels align. Narrow layouts reflow into a readable single column with reachable navigation and actions. Keep diagnostic detail and long history behind disclosure.

Compare implementation screenshots side by side with these captures at matching widths. Assess hierarchy, density, alignment, color, typography, focus visibility and reflow. Exercise representative long content, empty, loading, stale and unavailable states as well as the busy reference. Use [the isolated Aspire and Playwright workflow](../../tests/Nightweaver.QA.AppHost/README.md), discover its endpoint, and stop the exact stack afterward.

## Behavior and scope

The prototype contains sample projects, metrics, output and memory-only actions. Production uses truthful current-project data. Multi-project navigation and entry points may remain explicit placeholders in this release. The design-direction switcher, sample workspace badges and design-preview labels belong only to the study.

The prototype communicates visual and interaction intent; ns-mbd and ns-duw define the implementation and live-update contracts. Preserve guarded mutations, drafts, runtime identity, bounded output and safe Digest checkpoints. The implementation uses a dedicated Full Bead Workspace route even though the prototype demonstrates details in a dialog. Existing operational actions absent from the sample still require parity.

The approved Studio hierarchy supersedes older console layout prescriptions for this rewrite. Existing safety and behavioral requirements remain applicable. Tooling, candidate route and component structure come from ns-mbd.1; this reference does not change that dispatched task's scope.
