# Admitted Bead titles

The title change passed local correctness checks and the 300-second hard limit.
The complete suite missed the 240-second operating target, as did the baseline.

Measured source: `d16834b988c7b575fa7c8b54f641efa41a645633`.
Base: `fabde4ebde219818768372a1bf7918faaf96bdb0`.
The later evidence commit changes documentation and screenshots only.

## Behavior and checks

Planning, Delivery and Merger pass the admitted Payload title and exact Bead ID
to the existing factory publisher. The optional field adds no tracker command,
subscription or per-slot title request. Plain React text preserves Workspace
navigation. The publisher retains same-Bead titles across stage updates, clears
titles on identity change and idle, and rejects delayed foreign invocation and
release observations. The title refreshes on the next admission. The display
bound is 512 Unicode scalar values; unavailable titles use the existing fallback.

- `npm.cmd test`: 78 passed, zero failures.
- `npm.cmd run build`: passed.
- `dotnet build Nightweaver.slnx --warnaserror`: zero warnings and errors.
- Focused MTP checks: 86 passed, zero failures or skips. The command selected
  `Nightweaver.Tests.FactoryTelemetryIntegrationTests`,
  `Nightweaver.Tests.DeliveryPoolCoordinatorTests`,
  `Nightweaver.Tests.PlanningPoolCoordinatorTests` and
  `Nightweaver.Tests.MergerPoolCoordinatorTests` with repeated `--filter-class`
  and `--minimum-expected-tests 86`.
- The 22 added deterministic cases cover invalid Payload title data, Unicode
  bounds, all pools, two simultaneous Delivery identities, identity writers,
  idle, delayed foreign observations, schema versions 1 and 2, absent fields,
  stale metadata, runtime replacement, Project mismatch and oversized snapshots.
  Injected pool checks permit no tracker commands.

## Browser evidence

`tests/Nightweaver.QA.AppHost/studio-cutover.ps1 -Group Invocation -Only factory-qa`
passed in **42.420524 seconds**. It used a fresh isolated AppHost, discovered its
direct web HTTP endpoint, closed Chromium and stopped that exact AppHost in
`finally`. The native HTTP snapshot supplied two distinct Delivery titles and a
Planning title before any response substitution. Later substituted responses
checked Activity/Floor title/ID/link pairs, unavailable title data, plain text
safety, reuse, runtime replacement, stale state, keyboard links and invocation
inspection. The probe found no page overflow or uncaught page errors at
1440, 375 and 720 CSS pixels. Project mismatch and Merger admission use the
deterministic native reader/coordinator checks, not substituted browser evidence.

Representative captures were reviewed:
[Activity desktop](admission-titles/overview-1440.png),
[Activity narrow](admission-titles/overview-375.png),
[Floor narrow](admission-titles/floor-375.png).
Unrelated Overview sources were still loading in the Activity captures.

All raw probe logs, state captures and screenshots remain under
`H:/Nightweaver/prod/tmp/nightshift/ns-pmzw-p2b0bcf6d0ea6133fe150/20261122T2103591467340Z-implementer/title-browser-qa/`.

## Complete-suite cost

The single candidate measurement used `scripts/Measure-TestSuite.ps1`, normal
build, fixture publishing, execution and teardown with six workers. It passed
**2,133 tests**, **66 archive fixtures** and **102 collector fixtures**, with zero
failures or skips, in **276.8318839 outer seconds**. Test and measurement exits
were zero. Source was clean and measurement errors were empty.

Candidate tree: `a36231c92f5a3b258b270f1ce6474afbf129864b`.
Artifacts: `result.json`, `suite.log` and `report-1.xml` under
`H:/Nightweaver/prod/tmp/nightshift/ns-pmzw-p2b0bcf6d0ea6133fe150/20261122T2103591467340Z-implementer/suite/1031ba0958d344cb8147c0bc643fd4e0/`.
Result SHA256: `769974213468E61F3A9B02AD2CEF5F89CF010CFC71E40A1D8AB1C345F62CC6C6`.

The explicit accepted exact-main baseline is
`H:/Nightweaver/prod/tmp/nightshift/ns-cpbo/20261122T1833236364470Z-merger/accepted-integrated-result.json`,
SHA256 `9E005676280FFFA0EE6CE578810028ACD2F48A9B6D8F4830CAFA584C89B42346`.
Its revision is the base above; tree `7160dfa6729ed83d568e32ea11bf629d45e3fea1`.
Its retained raw evidence ID is `70fadae042394c5c953e51c8c82e2138`.
It passed 2,111 tests and the same 168 fixtures in 276.0138904 seconds.
The raw result, log and XML remain available. Both results preserve all prior
test names. The 22 added cases used **0.451 summed test seconds**.

Automatic comparison is unavailable: invocation temp paths, production identity
and scheduling file byte hashes differ. Delta and ratio remain null in the raw
result. Scheduling source matches after CRLF normalization. Machine ARES, twelve
processors, boot instant, SDK 10.0.300, runtime 10.0.8, bd 1.3.1, preparation
policy and six workers match. The baseline started from a copy without build
output; this candidate retained output from local checks. Production changed
from build `3e655fb1b677819554d612093d923fc9ab7f0c95`, runtime
`709516587f4b48a69c85e039fb6bd5bc`, to build
`fabde4ebde219818768372a1bf7918faaf96bdb0`, runtime
`bb1f124a7ac24c73aebc2258f63675bd`. This turn made no production changes.
The raw outer difference is +0.8179935 seconds. It is context, not an attributed
code-cost delta.

Production adds one bounded title projection per admission and one optional
field to each existing slot snapshot. No native journey, fixture publication,
tracker command or subscription was added. Existing UpgradeActivation test time
rose from 206.973 to 233.263 summed seconds; AgentStageDeliveryIntegration from
52.516 to 67.977; PoolConcurrencyIntegration from 49.310 to 60.273. These times
overlap and do not sum to outer elapsed time. The retained evidence does not
attribute the existing operating-target miss to title projection.

Observation-only resource capture ran before further source or environment
changes. It retained three samples with zero errors and requested no application
mutation. Evidence remains under the invocation root at
`resource-evidence/resources-20261122T211952Z-admission-title-native-collection-slowdown-36345d9c/`.
The Maintainer must review existing activation/native collection cost and this
resource evidence to restore target headroom. Assertions, deadlines, coverage
and worker limits remain unchanged. No full-suite rerun was used.

The PR remains draft. Merger owns fresh synchronized-head local checks, final
exact-head hosted verification and latest-main acceptance after integration.
