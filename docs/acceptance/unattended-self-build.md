# Sustained unattended self-build acceptance contract

Contract version: **1**. Owner: **ns-ke8**. Preparation: **ns-1dv**; contract slice: **ns-ahu**.

This is a prospective acceptance specification, not evidence that a run happened or authorization to operate production. The readiness packet identifies the exact contract commit and file hash. Preparation can complete while the packet is honestly **not ready**. ns-ke8 remains open until its actual sustained outcome is assessed; preparation completion does not satisfy that outcome.

## Selected workload and observation window

Version 1 selects an **eight-hour continuous observation window** and **three small, separately approved real-repository Plan Beads**: two initially independent roots and one follow-on with a real blocking dependency on a root. Include at least one production-code change and one documentation/test change. These are contract design choices, not previously supplied Product requirements: eight hours represents a night away, and this graph exercises concurrent admission, dependency release and ordinary Delivery/Merger handoff without manufacturing a large workload.

The readiness packet supplies actual IDs, scopes, native dependencies, Project/repository identities and applicable publication authority. Use useful approved work; do not manufacture changes just to fill the graph. Every selected Bead is incomplete at the start. Preserve ordinary Project concurrency, role settings, retry/repair limits and existing holds. Record other active work and its capacity/backpressure effects; do not remove unrelated holds or change capacity to make the run pass.

Record `T0` and `T1 = T0 + 8 hours` in UTC, local time and time-zone identifier. `T0` is the last initial authorized workload-release boundary, after a complete collector baseline is available. Archive any selected invocation already started during that initial release with its true earlier start; do not describe pre-window activity as observed end-to-end. No selected merge before `T0` counts toward this workload. For at least one selected production-code Plan Bead, its first Implementer invocation must start at or after `T0` and its verified merge must complete by `T1`. A later Reviewer or repair invocation does not establish this start boundary. If that boundary cannot be arranged, the packet is not ready.

The finite collector observes through `T1`, even if all work finishes early. Healthy idle for the remainder is part of the result. An evaluator has at most **30 minutes after `T1`** to assemble the first report from archived evidence; unresolved evidence then yields an explicit inconclusive report, not an extended window. Later corrections are separately dated and preserve the original report. Post-window observations can corroborate a historical fact but cannot fill an unobserved interval or move a merge into the window.

Changes to duration, composition or evaluation rules require a new contract version selected before the run. Actual workload IDs and resolved configuration belong in the packet, not in this reusable contract. No settings are changed by adopting it.

## Readiness and finite progress limits

Before `T0`, the packet must establish:

- Exact approved release and installed manifest/file identities; configuration hash and an allowlisted resolved view of concurrency, role timeouts and retry/repair limits; Project, physical repository and default-branch identity. A source checkout is not proof of the installed version.
- Applicable existing authorization, or the precise additional operation needing authorization. A suitable already-running installation can be reused. Neither a fresh deployment/restart nor reapproval of a still-valid grant is intrinsically required.
- Necessary repair evidence, exact-head required checks and current local suite/resource evidence. Open historical Beads are not blanket blockers: record each relevant requirement as satisfied, explicitly dispositioned or unproved, with its owner and source. Required unproved assertions prevent readiness.
- Selected workload eligibility, dependency direction, backpressure/capacity expectations and ordinary scheduling path. Account for external CI/provider waits and permitted automatic recovery. Unknown or unbounded required waits make the packet not ready.
- Available evidence sources, tested capture/evaluation tools and short disposable-fixture evidence that collection fits the bounds below. Readiness may finish with missing prerequisites listed; it does not request an expected implementation pause or manufacture readiness.

The following are **acceptance observation limits**, not replacement application timeouts or permission to alter production budgets:

| Boundary | Required bound and evidence |
| --- | --- |
| Runnable admission or a cross-pool/dependency handoff | Within **120 seconds** of corroborated eligibility and free capacity, observe a matching reservation/invocation or a fresh attributable blocking reason. This permits multiple ordinary 30-second discovery passes plus observation delay. A claim alone is insufficient. |
| Active role, operational retry, repair cycle and settlement | Before the run, record a finite upper-bound worksheet for each allowed path using the selected release's configured role budgets, actual classifier/fallback, verification, retry and process-settlement bounds. Include all permitted attempts and inter-role admission allowances. Read constants/configuration from that release; do not substitute guessed defaults. |
| Capacity, dependency, backpressure, provider or CI wait | Name the blocking identity, start and finite expiry in the worksheet. Queue position is not progress. Revalidate the blocker at least every **60 seconds** and require progress within 120 seconds after it clears. An unavailable source cannot certify the blocker. |
| Automatic recovery | If qualified and enabled, use its existing finite attempt/backoff/exhaustion bounds in the worksheet; retain every epoch and attempt. Manual restart is an intervention. Without qualified supervision, do not assume owner recovery exists. |
| Cleanup | Use the installed cleanup policy and recorded due time, including the ordinary initial attempt plus at most three transient retries after 30 seconds, 2 minutes and 10 minutes where applicable. Stop at its bounded result/attention state; no evaluator-triggered Retry or extra grace. |
| Absolute cutoff | Every selected task's conservative critical path, including contention and external waits, must fit within the eight-hour window before readiness. The worksheet names absolute UTC deadlines. No timeout extension, new full-suite retry or window extension turns a failure green. |

The evaluator distinguishes a **proved deadline violation** from **missing proof of progress**. With fresh eligibility, capacity and process/transition evidence, exceeding the admission bound is a failure. If evidence cannot establish eligibility, a blocker or a role's true state, classify the affected assertion unknown; this prevents success and can make the overall result inconclusive. At `T1`, still-running work with no proved violation is inconclusive, not an explained human pause or completed throughput.

## Evidence identity and sources

Use a unique external evidence directory per run, outside the Target Repository and installation. Preserve original inputs, versions and hashes. The archive-format slice specifies the physical format; this contract fixes the required semantic inputs. Beads remains the task tracker. The archive/report is evidence, never a second workflow-state store.

| Assertion | Required source and identity | Freshness and limits |
| --- | --- | --- |
| Installed release and Project | Manifest/package/file hashes, canonical installation/configuration/repository identity, resolved nonsecret budget worksheet and referenced authority | Baseline and endpoint corroboration; record any change as an epoch/configuration event, never silently replace the baseline. |
| Live runtime and actual Agent execution | Supported background status/owner record plus native PID **and creation time**, runtime ID, Factory telemetry and attributable invocation start/settlement evidence | Match owner/runtime/Project/repository and exact invocation/slot; apply the selected release's existing telemetry freshness policy. HTTP success, a process name, slot reservation or tracker assignee alone is not execution proof. |
| Tracker eligibility, pauses and transitions | Native public read-only Beads observations, runtime transition logs and exact selected Bead IDs/stages/claim actors; versioned operational resources may corroborate | Keep source timestamps, advertised policy/health, revisions and Web epoch distinct from collection time. SSE hints are not authoritative bodies. Missing/stale/failed data never proves a clear queue or an actionable pause. |
| Verified merge | Host `nightweaver-verified-merge-v1` evidence associated with the task/invocation plus Git ancestry and PR/head/check/merge correspondence | Archive the original evidence and queried identities. A SHA in ordinary notes, Bead closure, Digest presentation or successful PR checks alone does not establish the verified merge. |
| Cleanup | Versioned root/repository-bound obligation and outcome evidence, exact resource identity and confirmed absence or protected retention | Distinguish merge/closure from worktree/tmp/branch cleanup. A missing/corrupt store is unknown, not zero obligations; stale old-root records grant no removal authority. |
| Resource trend and incidents | Existing resource-collector semantics: PID+creation/parent identity, cumulative CPU/IO, private bytes, handles/threads, system commit/pools/paging and boot identity | Compare same-identity deltas and settled workloads. RAM/RSS or one growth sample is not leak proof. Preserve trigger evidence before any recovery action. |
| Interventions and observation coverage | UTC and monotonic collection intervals, collector identity/version, source failures/gaps, intervention journal, operator attestation and corroborating logs | A blank journal is not independent proof of no intervention. Record visibility limits and clock discontinuities; missing coverage cannot be retroactively declared continuous. |

Keep configured/requested model separate from observed provider identity. Record reliable actual model evidence when available; its absence is not permission to infer it from configuration. Universal credential redaction is not assumed: restrict capture to explicit local allowlists and safe status metadata. No credentials or unrestricted environment, command-line or configuration dumps go into evidence summaries or Beads. Keep retained raw invocation evidence local with its existing access boundary; export only an allowlisted summary.

## Finite collection contract

The collector is an independently launched **observer**, not a supervisor or a keep-alive session. It cannot launch Agents, mutate tracker state, retry/resume work, restart hosts, change configuration or delete source files. Source reads can activate documented Web observation caches; measure that cost rather than claiming reads are free. Loss of the collector must not control runtime execution.

Version 1 selects these maximum rates and bounds; the format/capture slices must demonstrate them with injected clocks/adapters before readiness:

| Collection | Bound |
| --- | --- |
| Owner/runtime/Factory read samples | Every **15 seconds**, one serial turn; at most two HTTP reads plus local identity reads. Each external request has a **2-second** deadline and the turn a **5-second** budget. Preserve actual start/end, timeout and skipped-turn records. |
| Tracker verification | At most **two native read commands per 60 seconds**, serial, each with a **2-second** bound and a **4-second** combined native-work budget. Rotate inventory and selected detail/comments without catch-up bursts; carry unfinished checkpoints forward. Record each source age. |
| Invocation/transition evidence | Incremental reads from explicit selected log identities only; no broad home-directory scan. Retain offsets, partial-record and rotation/gap information. Match the real source format rather than treating merged stdout/stderr as structured records. |
| Resource trends | At baseline, endpoint and every **15 minutes**, reuse the existing collector with two samples one second apart. An incident capture uses its existing three-sample/ten-second diagnostic procedure instead; preserve the incident's distinct cadence and reason. No overlapping resource captures. |
| Size and volume | Per HTTP/command result **1 MiB**; per incremental log chunk **64 KiB**; per evidence record **2 MiB**; per local selected invocation source **8 MiB**; total run archive **256 MiB**, including samples and copied evidence. Hash larger retained sources in place and report their required content as unavailable if it cannot be captured within limits. No silent truncation or eviction of earlier evidence to keep a success claim. |
| Missing intervals | More than **45 seconds** between accepted runtime/identity samples is a coverage gap. Tracker/detail evidence older than **180 seconds** cannot establish current eligibility/attention. These limits do not override a source's stricter existing stale/health policy. Timeout, invalid identity, unsupported version, clock uncertainty and storage exhaustion are explicit sample failures. |
| Collection overhead | Record collector CPU/IO, its elapsed read time, actual sample counts/gaps and external command count. A short disposable fixture qualification must show collection leaves its own cadence feasible and respects source budgets. Missing overhead evidence prevents readiness; these rates are not a claim of zero impact on application timing. |

Never overlap a timed-out external child with a replacement while its settlement is uncertain. Mark that source unavailable and continue only independent sources whose budgets permit it. A recovery attempt belongs to the application's recorded policy, not to the collector. Reaching the archive cap stops further payload capture with a terminal diagnostic; retain already captured evidence. The observer stops at `T1`, settles/cancels only its own collection operations within the tested finite shutdown budget recorded in the packet, and records the actual stop. A source read near `T1` cannot silently extend the observation window.

Persistent stale/unavailable sources prevent a strong verdict even when their transport remains connected. Apply current versioned envelope policy, including source health/age and Web epoch; do not reinterpret the old tracker polling interval as the current freshness rule. A short gap can be corroborated by independent archived authoritative evidence covering the same interval, with the reconciliation stated explicitly. Repeatedly reading the latest state does not reconstruct lost history.

## Intervention accounting

The operator journal records actor, exact UTC interval, action, affected identities, authority/source and effect on the result. Corroborate it with runtime/CLI/Git/tracker evidence and disclose what those sources cannot observe.

| Category | Treatment |
| --- | --- |
| Initial authorized setup/release at or before `T0` | Record outside the uninterrupted portion, including initial workload release. A suitable existing installation/authority is reusable. Setup does not count as intervention-free historical operation. |
| Passive read-only observation, finite capture and offline reporting | Allowed; disclose overhead and gaps. Closing the initiating Codex terminal must not stop the independently launched observer or runtime. |
| Ordinary configured retries, repair turns and qualified automatic recovery | Allowed within existing budgets, with exact attempts/epochs and progress evidence. Report automatic restarts explicitly; unattended does not mean restart-free. |
| Human product guidance or safety intervention | A safe actionable pause is a bounded outcome. Record the question and next action. Guidance/resume that keeps selected execution moving during the window invalidates the uninterrupted claim; it is not erased by later successful merges. |
| Manual dispatch/resume/retry, restart, deployment, configuration/credential repair or manual cleanup | Record as intervention; if required to keep selected work moving or satisfy acceptance, verdict is failed. Unrelated operator work must be isolated by identity/effect; uncertain interference prevents success. |
| Intentional crash, force-stop or other destructive fault injection | Qualify separately on disposable resources outside the sustained window. A fault-injection window is separately labelled and cannot substitute for this unassisted production window. |

No Codex session may supply operational keep-alive actions during the window. A human or Agent may later assess immutable evidence without having driven the execution. Existing pauses remain protected; the contract grants no Resume, cleanup Retry or scheduling authority.

## Verdicts and adjudication

Apply the following in precedence order. Record task throughput, live/idle intervals, restarts, cleanup status and resource findings separately from the overall verdict; one summary must not conceal a weaker assertion.

| Verdict | Necessary evidence |
| --- | --- |
| **Failed** | A proved silent runnable stall/deadline violation, duplicate ownership, lost work, unsafe action, required manual keep-alive, or other proved required acceptance violation. Cite the interval, authoritative sources and owning repair Bead. Missing data elsewhere cannot hide an already proved failure. |
| **Inconclusive** | No proved failure, but required coverage/identity/qualification or outcome proof is missing, contradictory, stale, truncated or unsupported; or selected work remains active or otherwise unaccounted for at `T1` without a proved deadline violation. Fully evidenced legitimate pauses and their blocked dependents are accounted-for states evaluated by the next row, not inconclusive merely because they are nonterminal. List the exact unproved assertions. |
| **Explained unattended pause** | Complete evidence supports a legitimate Product/safety pause or attributable cleanup attention with a concrete reason and next action; all unfinished selected work is accounted for by those states/dependencies, and unrelated runnable selected work has not silently stalled. Report actual merges and remaining throughput; this is not all-work-merged success. |
| **Completed unattended** | All three selected changes have verified in-window merges, required recovery/cleanup assertions are satisfied, no required violation/intervention/gap remains, and continuous live/fresh evidence distinguishes early completion followed by healthy idle from missing observation. |

An operational failure can be safely surfaced for human attention while remaining a **failed** required assertion; a pause label does not downgrade a proved failure. Protected cleanup retention with an honest actionable policy decision can support explained pause; a proved unsafe deletion cannot. Optional diagnostics or unselected work are reported without manufacturing additional release requirements.

Recompute the verdict offline from immutable evidence. Spot-check every claimed merge against host and Git/PR/check identity, every pause against recorded/current guarded workflow evidence, and every runtime epoch against native PID+creation identity. A later fresh read may corroborate identity but cannot replace missing in-window observations. Publish the first report by `T1 + 30 minutes`, with contract/release/workload identities, original input hashes, worksheet deadlines, actual boundaries, verdict reasons, all intervention classes, resource trends and independent cleanup results. Later source repairs or reruns are new evidence, not edits that overwrite the first result.

### Worked decisions

| Scenario | Outcome under version 1 |
| --- | --- |
| All three changes merge after two hours; the remaining six hours show fresh live owner/runtime identity, no selected runnable work, and satisfied cleanup | Completed unattended; report two-hour throughput and six-hour healthy idle separately. |
| A real Product question pauses one root; its dependent remains blocked, the other root merges, and the reason/evidence/next action are complete | Explained unattended pause; incomplete throughput is explicit. A separate runnable root exceeding its admission bound would instead fail. |
| Fresh native readiness and free capacity persist for 121 seconds, with no matching reservation/invocation or attributable blocker | Failed admission/progress assertion, with the two source intervals and owning repair evidence. A lone stale ready row cannot establish this case. |
| The owner exits and an operator restarts it to continue selected delivery | Failed uninterrupted claim even if every change later merges. Record both epochs, preserved work and the manual intervention. |
| Only a claim or HTTP health remains visible, or runtime evidence has an uncorroborated 90-second gap | Inconclusive unless another source proves a failure. Neither running delivery nor healthy idle is established. |
| Existing isolated restart tests pass but no completed sustained-window archive exists | Recovery qualification only; ns-ke8 final acceptance remains unproved. |

## Recovery qualification and external owners

Qualify retained HEAD/branch/dirty content, current-Project recovery, foreign claims, pause/counter preservation, already-merged reconciliation and independent cleanup outcomes using the minimum applicable existing component/native results. Bind each claim to an exact revision/test/artifact and identify gaps. Existing manual second-signal restart and already-merged recovery fixtures prove their observed boundaries, not automatic owner restart. Reuse trustworthy results; add new native coverage only for a demonstrated missing boundary with measured cost.

| Owner | Role in this contract |
| --- | --- |
| ns-1dv | Preparation contract, archive/capture/evaluator, qualification and readiness; may finish without a ready production packet. |
| ns-ke8 | Later real sustained observation and final attributable acceptance. Remains separate from preparation closure. |
| ns-6ri / ns-qa2 / ns-qyk | Opt-in supervision, recoverable upgrades and notifications respectively. Do not assume delivery or automatically require every sibling before a run; map only necessary assertions to verified evidence. |
| ns-aor | Retained-worktree freshness/continuation where applicable; preserve its separate hold. |
| ns-5uk / ns-1ve / ns-jbp.13 | Residual observer, telemetry and normal-parallel investigations. Their open status alone is not proof of a current run defect or a required fix. Preserve relevant failures and dispositions. |
| ns-726.5 | Resource attribution; capture before restart/kill/cleanup/reboot when its diagnostic trigger applies. A trend is not a leak verdict. |

Historical ns-17r established a real manual-plan Delivery pilot with acknowledged setup/recovery interventions and cleanup limitations; it is not an intervention-free baseline. ns-1b1.7 repaired Digest merge presentation, but presentation remains corroboration rather than authoritative merge proof. The stale root default revision and a retained Planning checkout can differ from selected main and installed production: record all identities rather than silently treating them as interchangeable.

Cross-Product context uses ordinary references or a validated non-membership relation. Current Planning publication expects exactly its immutable task membership; reconcile any existing non-graph provenance before future ns-ke8 publication. Neither this contract nor a context link creates a task graph or changes a hold.

## Repository contracts and document verification

Sources: [Product/Plan boundaries](../adr/0001-two-tier-product-and-plan-beads.md), [Orchestrator authority and recovery](../adr/0002-beads-native-agent-handoff-and-thin-orchestrator.md), [Planning evidence](../agents/planning-evidence.md), [operational observation protocol](../design/operational-observations-v1.md), [manual pilot proposal](manual-delivery-pilot.md), [background deployment](../../README.md#manual-background-deployment-windows-v1), [test-cost evidence](../agents/test-cost-evidence.md), and [hosted verification](../agents/hosted-verification.md).

Review this document against ns-1dv/ns-ke8 and walk every worked decision through the verdict table. Required repository Delivery/merge checks still apply to this documentation slice: positive test counts, committed six-thread scheduling, measured complete candidate and latest-main suites below the 240-second operating target and 300-second hard limit, and exact-head required hosted checks. A target-only miss receives cost review rather than becoming a new hard gate. No overnight test, browser stack, model invocation or production run is needed to verify the specification itself. Actual results belong in a separate versioned report backed by immutable run evidence.
