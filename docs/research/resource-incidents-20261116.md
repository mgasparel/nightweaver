# Resource incident disposition, 2026-11-16

Bead: ns-726.5. Analysis checkout: `89ec84739b8ba426d360984e499ac7d34a8278c7`.
This investigation analyzes retained evidence. A normal validation suite was run;
no new diagnostic workload, production operation or system configuration change
was performed.

**Disposition: inconclusive attribution; no demonstrated resource leak or owning
repair.** The same-head reboot comparison establishes a large machine/background
state contribution. The retained resource captures show changed machine workload
and bounded short-window activity, but do not observe the original slow interval
or equivalent release boundaries. No production fix is justified by these data.
The next finite measurement is specified below; waiting for it is not a condition
of publishing this investigation.

## Sources and reproduction

All claims below derive from local original measurement artifacts. `E` means
`H:/Nightweaver/ns6qh-evidence/merge-deploy-20261116`; `S` means
`H:/Nightweaver/ns-sib-source-deploy-20261116`; `T` means
`H:/code/nightshift-worktrees/ns-sib/TestResults`.

| Evidence | Original files |
| --- | --- |
| Exact-head timing and machine snapshots | `E/post-reboot-comparison.json`, `E/pre-reboot-machine.json`, `E/post-reboot-machine.json` |
| Before/after original timing, counts and collection durations | `E/main-repaired-production-up-result.json`, matching `.log` and `.xml`; `E/post-reboot-production-up-result.json`, matching `.log` and `.xml`; `E/post-reboot-collection-comparison.json` |
| Healthy capture (B) | `E/resources-20261116T190018Z-healthy-post-reboot-3c0c7fe3/{metadata.json,sample-00.json,sample-01.json,sample-02.json,triggering-timing-result.json}`; pointer `E/healthy-resource-baseline.json` |
| Later capture (I) | `E/resources-20261116T230429Z-ns-sib-native-drift-3854ea13/{metadata.json,sample-00.json,sample-01.json,sample-02.json,triggering-timing-result.json}` |
| Later candidate | `S/checkpoint-candidate-cost-comparison.json`; `T/ns-sib-checkpoint-candidate-timing.json`, `T/ns-sib-checkpoint-candidate.xml`, `T/ns-sib-checkpoint-candidate.log` |
| Collection implementation | `E/Capture-ResourceIncident.ps1` |
| Later same-application-head main validation | `S/main-suite-timing.json`, `S/main-suite.log`, `S/main-suite.xml` |

The adjacent [input hash manifest](resource-incidents-20261116-inputs.sha256)
identifies the exact retained files read. Paths use the aliases above; raw captures
remain external evidence, rather than copies of all host process inventory in Git.
Both capture directories lack `capture-errors.json`, despite the collector's
current source writing that file. All six samples contain `errors: []`; this
establishes zero reported **sample** errors, not a verified zero-error metadata
capture. Do not reconstruct the missing files or treat their absence as `[]`.

From repository root, using PowerShell 7:

```powershell
$evidence = 'H:/Nightweaver/ns6qh-evidence/merge-deploy-20261116'
$comparison = & ./scripts/Compare-ResourceIncident.ps1 `
    -BaselineDirectory "$evidence/resources-20261116T190018Z-healthy-post-reboot-3c0c7fe3" `
    -IncidentDirectory "$evidence/resources-20261116T230429Z-ns-sib-native-drift-3854ea13"
# Select a NEW destination outside source evidence; no capture is modified.
$comparison | Set-Content (Join-Path $env:TEMP ('resource-comparison-' + [guid]::NewGuid().ToString('N') + '.json'))
$analysis = $comparison | ConvertFrom-Json
$analysis.incident.window.changes |
    Sort-Object { $_.delta.PrivatePageCount } -Descending |
    Select-Object identity,name,group,delta,cpuCores,rates
$analysis.betweenFirstSamples.changes |
    Sort-Object { $_.delta.PrivatePageCount } -Descending |
    Select-Object -First 10 identity,name,delta
```

The analyzer only reads explicit directories and emits an allowlisted JSON
projection. It invokes no processes, counters, application status commands or
network calls, and writes no files itself. The command above saves its output.
Run deterministic synthetic checks with:

```powershell
./scripts/tests/Compare-ResourceIncident.Tests.ps1 -OutputRoot $env:TEMP
```

Synthetic evidence is retained under a unique directory. Seventeen assertions cover
PID reuse, unknown creation identity, reused parent lifetimes, status identity
mismatch, timezone/creation precision, missing/reset counters, decreasing gauges,
100ns CPU units, fractional sample spacing, IO rates, missing error artifacts and
rejection of non-increasing sample times, and exclusion of private process data.
No native stress test was added.

## Comparability and counter semantics

Identity is PID plus creation UTC, including fractional seconds, never executable
name alone. The same PID with a different creation time appears/disappears
separately. Missing identity is counted as unknown and receives no counter delta.
Parent links require the parent to be present and created no later than the child;
a missing or newer parent remains unknown. Production owner attribution also
requires the status creation time to match the process creation time, truncated
to Win32_Process's microsecond precision. Verified parent chains identify
production descendants, including console hosts. Names only suggest roles:
`bd.exe` is not proof of a journal follower; `dotnet.exe` is not proof of an orphan.

For each adjacent interval and the whole capture window, subtract cumulative
CPU/raw transfer counters only for matching identities and divide by actual UTC
sample spacing. Negative cumulative deltas (reset/wrap) and missing counters yield
null rates. CPU counters use 100ns units: `(delta kernel + delta user) / 10^7 /
seconds` is average occupied cores, not machine utilization. IO rates are process
transfer bytes, including cached IO; they do not measure physical disk bandwidth
or wait latency. Gauge deltas (private bytes, working set, handles, threads) may
legitimately decrease. Whole-window endpoint rates do not replace examination of
the adjacent intervals for counter resets or bursts.

Appeared/disappeared identities are reported separately with observed parent
links; they indicate presence at sample boundaries, not proven birth, native
exit/settlement, or every short-lived child between samples. The collector reads
system, memory, processes and status sequentially and stamps `observedUtc` last;
timestamps and rates are approximate collection endpoints, not atomic snapshots.
System formatted paging counters are sampled rates, not cumulative counters to
subtract. Available memory, commit/limit, pools, pagefile use and uptime accompany
process changes. Do not sum working sets (shared pages), aggregate CPU/scan time
into elapsed time, or label any single memory increase a leak.

The `betweenFirstSamples` comparison spans 14,649.827 seconds on the same boot:
243 matching identities, 106 newly observed identities, 36 no longer observed.
It reports only genuinely matching identities; the two production owners and
their Web/bd children differ and cannot be subtracted as one process's growth.
This long interval contains uncontrolled work, not repeated equivalent cycles.

## Observations

### Same D1 head, before and after reboot

Both complete suites used revision `d1a664e4d671387428666cdc35c6ddaeaf014644`,
tree `1dac762b5fd6c5aa5690f363504197c2e5b8e700`, prepared checkout
`H:/code/nightshift-deploy-ns-6qh`, C: user temp, .NET 10.0.8, 12 logical
processors, six workers and production running/healthy. Both passed all 1,304
tests with zero failures/skips. Outer elapsed time was **261.9912098 →
90.4348433 seconds**, saving 171.5563665 seconds (65.4817%, 2.897x speedup).
Collection times also improved broadly: Delivery 111.519 → 29.942 seconds;
Merger 95.726 → 23.481 seconds. These collection totals overlap in wall time.

The old boot was November 12 06:02:53.5Z; the new boot November 16 18:40:45.5Z.
Machine snapshots show uptime 389,916.067 → 398.241 seconds and free physical
memory 6,175,376 → 20,837,068 KiB. These snapshots are outside the timed intervals,
not concurrent resource profiles. The slow run started at 18:14:42.7519286Z;
the retained `E/production-restart.log` places the preceding owner start around
18:11:42Z, only about three minutes earlier. Owner uptime alone is therefore
insufficient to explain the old machine's slowdown. Foreground activity and
subscription demand were not controlled. A reboot changes many variables.

### Resource captures

Both have three samples, the same boot, 12 logical processors, C: user temp and
configuration SHA256 `E5A17A5F2FE3203A6379A7819CFE3DE4CFCD1D10AA121D9864882B2001888B4D`.
Both record source D1 because the collector hard-codes its prepared checkout.
Production build is D1 for B, **`96559cfe53ecfc02e4738dc557b368450eefe1ad` for I**;
I's actual candidate timing revision is **`c4233a66c0a417ea3c04c22e61cf86a808db368d`**.
These are three separate provenance fields, not evidence that I timed D1.

| Quantity | Healthy B (19:00:21.408–19:00:42.495Z) | Later I (23:04:31.235–23:04:52.462Z) |
| --- | --- | --- |
| Actual adjacent spacing | 10.570 / 10.517s | 10.646 / 10.581s |
| Machine uptime, first sample | 1,176.319s | 15,825.947s |
| Process count | 279 / 278 / 278 | 349 / 353 / 349 |
| Unknown creation identities | 0 in each sample | 0 in each sample |
| Available MiB | 19,836 / 19,875 / 19,877 | 16,724 / 16,570 / 16,669 |
| Commit GiB | 14.26 / 14.21 / 14.21 | 21.99 / 22.25 / 22.04 |
| Commit limit GiB | 51.09 throughout | 51.09 throughout |
| Paged pool MiB | 905.64 / 905.14 / 905.39 | 1,849.59 / 1,850.10 / 1,849.16 |
| Nonpaged pool MiB | 665.33 / 665.13 / 664.16 | 961.51 / 962.76 / 960.29 |
| Pages/sec; page reads/sec | 624/39, then 0/0 twice | 313/26, 434/37, 439/34 |
| Page writes/sec | 0 throughout | 0 throughout |
| Pagefile current/peak MiB | 24/24 throughout | 167/196 throughout |

Commit rose between captures by about 7.73 GiB, paged pool by 943.95 MiB and
nonpaged pool by 296.18 MiB. These changes warrant OS/background investigation;
the short later window does not show monotonic commit or pool growth. It retains
about 16 GiB available and substantial commit headroom; neither capacity exhaustion
nor absence of transient contention during tests follows from that snapshot.

Production status stays running/healthy/live with a stable runtime within each
capture: B `42552b2748ad462084f15a6c6839d0e5`, I
`2d6ab50f384844d48b751e80d65928b6`. Principal identities and whole-window deltas:

| Capture / role candidate | PID @ creation UTC | Starting private MiB; change | Handles / threads change | Average cores; raw read MiB/s |
| --- | --- | --- | --- | --- |
| B owner | 8828 @ 18:47:33.489129Z | 87.19; −11.63 | +7 / +4 | .009; .226 |
| B Web child | 20828 @ 18:47:34.378571Z | 23.02; +.14 | +1 / −1 | .004; .00013 |
| B long-lived bd child | 15616 @ 18:47:36.381438Z | 242.27; +8.68 | 0 / 0 | .040; 10.217 |
| I owner | 4708 @ 21:56:14.747492Z | 107.08; −17.71 | +1 / +4 | .014; .452 |
| I Web child | 28620 @ 21:56:15.826325Z | 116.09; −.41 | −14 / +1 | .029; 1.097 |
| I long-lived bd child | 28548 @ 21:56:18.414521Z | 330.22; 0 | −3 / 0 | .063; 13.188 |

Dates in this table are all 2026-11-16. These children have compatible observed
owner-parent identities. The console children remain stable. B has no newly
observed production descendants and one unrelated disappearance. I's adjacent
intervals have 4/2 then 1/4 appeared/disappeared production descendants, including
transient bd/conhost children. One transient pair belongs to the owner near the
collector's status read; it cannot establish spontaneous workload or a leak.
Short-lived children are missed between samples. No growing retained child cohort
or unbounded handle trend is established.

The first-sample cross-capture ranking for matching identities includes
`MsMpEng.exe` PID 5608 @ 18:40:57.009305Z (+363.02 MiB private, +678 handles),
`explorer.exe` PID 9904 @ 18:40:58.988039Z (+71.97 MiB), and `codex.exe` PID
4592 @ 18:46:22.489854Z (+63.47 MiB). MsMpEng averages .219 cores across that
four-hour interval, but **decreases** 19.30 MiB in I's short window. This cannot
assign suite latency to scanning or distinguish retained cache from a leak.

Newly observed consumers include Brave PID 16744 @ 19:31:19.988351Z (431.09 MiB),
VBCSCompiler PID 16356 @ 22:03:51.432886Z (387.46 MiB), another Brave PID 7824
@ 19:31:23.645636Z (375.38 MiB), and SnippingTool PID 26232 @ 20:13:55.957011Z
(336.66 MiB). The compiler has a compatible dotnet parent PID 25384 @
22:03:50.029526Z, but source/worktree ownership and intended compiler lifetime are
unknown. Three dotnet processes in I are stable at 84–91 MiB private; the compiler
changes −.08 MiB with zero measured CPU. Four dotnet identities in B also remain
flat. Names and persistence after testing alone do not make them leaked/orphaned.

The later candidate passed 1,325 tests, zero failures/skips, six workers: outer
116.4953832s versus 100.0651362s at `c300c0279f929209bdc836003e3100882b3fa22a`
(+16.42%). XML assembly time is 102.360s; observer collection 9.29 → 8.98s.
The capture is post-test, not simultaneous. The candidate differs from both D1
and deployed production. This is broader native drift worth preserving, below
its 1.5x full-suite trigger; it is not a matched causal experiment or an in-test
resource profile. D1's 90.43s is not a permanent ceiling for later source/workloads.

## Ranked hypotheses and next measurements

Ranking is investigative priority, not a probability or attribution claim.

| Rank / hypothesis | Supporting observation | Contradiction or missing evidence | Discriminating measurement |
| --- | --- | --- | --- |
| 1. Mixed OS/background workload or IO/paging contention | Same D1 improves 2.897x after reboot; later commit, process count and pools are higher; browsers/compiler/other consumers changed | No original slow-window process/pool profile; later available RAM remains large; pools/commit oscillate rather than grow in 21s | One bounded simultaneous recurrence capture, correlating interval rates and process identities with actual timing; separate system pools from process private bytes |
| 2. Nightweaver-owned resource lifetime or retained descendants | Long-lived bd does sustained transfer IO; later Web/bd footprint is larger across different owners | Different builds/owners/workloads; owner private bytes decline in both captures, later bd flat; transient children disappear; no equivalent release-boundary failure | Only if a specific child/handle fails release in simultaneous evidence: three fixed isolated equal work cycles with before/active/settled identities and original teardown budget |
| 3. Journal engine-open activity | Observed long-lived bd child reads 10–13 MiB/s of transfer counters | Arguments not captured, role not proven here; cached transfers are not disk time; no suite overlap or causal A/B here | Consume ns-726.3's journal-specific bounded probe; do not duplicate it or attribute all drift to journal IO |
| 4. Compiler/test leftovers | Stable dotnet and compiler processes remain after tests | Expected caching/build-server lifetime is plausible; parent evidence does not identify a violated owner contract; no observed testhost growth | Record exact identities at a documented isolated build/test teardown boundary if recurrence implicates them; prove an intended release violation before repair |
| 5. Defender-specific or kernel pool leak | Same MsMpEng identity grows over four hours; pools are higher in I | No scan/wait correlation or pool allocation tags/stacks; MsMpEng shrinks in I; a single long interval spans uncontrolled work | If simultaneous evidence ranks it first, a separately authorized bounded trace tied to that interval; no exclusions, driver changes or reboot A/B to manufacture causality |

No hypothesis is proven or globally disproven. The strong claim that one
Nightweaver owner's age or rising RSS alone explains the original incident is
unsupported; the narrower observation that machine state matters is supported.

## Exact next bounded probe and capture-first rule

Choose hypothesis 1 for the next observation. **Trigger:** an unexplained
comparable local full suite at least 1.5x its latest accepted comparable baseline
(D1 reference 90.4348433s → 135.65226495s), broader native latency/timeouts, or
sustained resource/descendant growth across equivalent settled operations.
Source/workload changes require cost attribution and a fresh comparable reference.
The 240s operating target and 300s hard limit, all tests, native boundaries,
deadlines and six-worker schedule remain unchanged.

1. Preserve the actual triggering result JSON, XML and log in unique evidence
   paths **before** another full run, restart, reboot, kill, cleanup, dependency or
   configuration change. Record measured revision, production build/runtime,
   test counts, parallelism, temp root and timing relative to capture. The legacy
   collector copies only the result JSON, so preserve XML/log explicitly too.
2. Use the existing observation-only collector for exactly three samples ten
   seconds apart, while affected activity is still observable when possible:

   ```powershell
   & 'H:/Nightweaver/ns6qh-evidence/merge-deploy-20261116/Capture-ResourceIncident.ps1' `
       -Label recurrence-ns7265 -Samples 3 -IntervalSeconds 10 `
       -TimingResult '<absolute path to actual triggering result.json>'
   ```

   Its unique destination is printed. Inspect metadata, all sample errors and
   the final error file. A failure leaves explicit uncertainty and preserved
   timing; it does not justify retrying the benchmark. The legacy collector's
   hard-coded D1 checkout does not identify another measured source. Once
   ns-726.1 supplies its parameterized installed-source collector, use that
   collector with the actual measured checkout and record its version/arguments.
3. Stop after those three samples. Compare to B with the analyzer, prioritizing
   **within-window** rates/identity changes and system commit/pools/paging.
   Cross-head or post-test data remain confounded. No automatic remediation or
   repeated benchmark is part of this probe. If the trigger already settled,
   report that loss of temporal evidence and await a distinct trigger.
4. Success is a bounded observation that distinguishes attributable process
   activity from system pools/background activity, or explicitly records that
   neither is captured. It has **no arbitrary RAM pass threshold**. Stop with
   uncertainty if snapshots cannot correlate activity with latency; a finite
   capture is not proof of sustained growth or native process settlement.
5. Only after a specific release violation is identified, use a linked owning
   Bead for a red-capable mechanism probe: exactly three equivalent isolated
   cycles, observations before/active/after normal teardown, original native
   settlement budget. Predeclare the owner-specific release contract (original
   child identity signaled/settled and intended handles disposed), fail on that
   violation, stop at first violation or after three cycles, and check the same
   contract after repair. Bounded caches must settle across equal cycles; there
   is no fixed machine-wide memory ceiling. No such repair is authorized by
   the current retained snapshots alone.

Manual capture and this offline analyzer exist. This change installs no always-on
monitor, automatic threshold detection, collector wrapper, or production action.
ns-726.1 owns guardrail integration/collector packaging; ns-726.2 owns complete
suite headroom; ns-726.3 owns journal mitigation. This disposition changes none
of those owners or the runtime specification.

## Implementation validation

The analyzer/report candidate based on `89ec847` passed `dotnet build
Nightweaver.slnx -warnaserror` (zero warnings/errors) and the complete command in
`tests/README.md`: 1,325 passed, zero failed/skipped, six workers,
**127.8440962s outer elapsed**, including normal build/publishing/teardown.
Application source is unchanged. This run used the assigned H: invocation temp
root; the retained D1 C: baseline is not a matched comparison. The later retained
same-application-head `89ec847` main measurement is 106.6117489s
(`S/main-suite-timing.json`), also not a controlled temp-root comparison. Neither a runtime regression
nor latest-main acceptance follows from this candidate check. The separate
17-assertion analyzer checks passed, real retained-capture analysis completed,
all 30 input hashes remained unchanged, and `git diff --cached --check` passed.
