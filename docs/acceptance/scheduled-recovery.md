# Isolated installed Scheduler recovery

Owner-loss and Web-loss recovery passed in isolation. Both replacement owners
survived the requesting tick and matched fresh live runtime and Web evidence.
Stop, disable, maintenance and maintenance leave suppressed later starts.
Exact task and process teardown passed. Production supervision stayed unchanged.

This record belongs to `ns-8s1h`. The installed pair was published from
`63ed5132386f9c4eaac912dd2b48c1555b14791e` with
`scripts/Publish-Nightweaver.ps1`. The proof uses the real installed CLI,
Orchestrator runtime, Web Console, embedded Beads database and Windows Scheduler.
It does not operate the production Project or install production supervision.
The [operator runbook](../agents/scheduled-supervision.md) describes supported
commands and inspection.

## Reproduction and retained evidence

Use a new disposable repository and a separate empty installation destination.
Create them as the Windows user who will run Scheduler. Initialize Git and Beads,
then make a normal fixture Bead write. `bd init` alone does not create every gate
file required by mutation-free runtime inspection. Publish the checkout into the
new destination. Select only that repository in its Project configuration. Use a
free loopback port and an existing interactive user session.

Create an open paused task with a known Delivery repair count. Register its
`nightweaver/<id>` worktree under the isolated host, and leave a dirty sentinel
file and a temporary sentinel file. Retain the initial Bead JSON, worktree
registration and file contents. Do not add eligible Agent work.

Run the installed CLI's supervision install, manual start with the chosen URL,
then supervision enable. Retain both complete registered XML definitions.
After initial readiness, kill only the exact fixture owner's native process,
checking PID and creation time first. Do not call start, restart, task Run or
manual tick during recovery. Poll retained state and native identities while
normal Scheduler ticks qualify failure, wait for backoff, reserve, consume and
reconcile the replacement. Check `/health` and `/api/factory/state` against the
new owner, Web and runtime identities. Repeat for the exact replacement's Web
child. The runtime runs inside the owner process; its abrupt loss shares the
first native fault boundary.

Check a second manual start returns the same owner. Stop deliberately and wait
longer than one tick interval. Then inspect stop, disable and maintenance
suppression with finite ticks. Preserve the Bead pause, ownership and repair
count, dirty work, registered worktree and temporary work across recovery.
Uninstall only the matching fixture definitions, stop the fixture owner and
confirm both tasks and all installed fixture processes are absent. Keep evidence
and fixture files for review.

The retained invocation is:

`H:/Nightweaver/prod/tmp/nightshift/ns-8s1h/20261122T2057532176919Z-implementer/installed-recovery/`

Its `verify.ps1` records owner loss. `web-verify.ps1` records Web loss and the
remaining operator checks under `web-proof/`. `install-user.json` and `enable.json`
retain the registered XML. `timeline.jsonl` records UTC, elapsed time, state,
owner identity and both Scheduler running states every ten seconds.
`owner-loss.json`, `web-loss.json`, `identities.json`, `native-*.json`,
`health-*.json` and `factory-*.json` retain the native and HTTP evidence.
The `*-bead.json` and `*-worktrees.txt` files retain preservation evidence.
`web-proof/result.json`, both `cleanup.json` files and command JSON/stderr files retain assertions,
elapsed time, suppression and exact teardown. The Bead handoff records artifact
hashes, final measurements and the documentation commit.

Initial preparation retained two explicit failures. Git refused the first
sandbox-owned disposable repository under the Scheduler user. A second,
user-owned repository removed that mismatch without a global trust change.
The first start on that repository refused missing tracker gate files. A normal
fixture Bead write completed tracker preparation. Neither failure authorized a
production change. Original files and logs remain available.

## Native results

| Fault | Previous owner / runtime | Scheduler replacement owner / runtime |
| --- | --- | --- |
| Owner loss | PID `24260`, run `73eaa2c4881b45b6b0322d4cc49468f0`, runtime `1fd276f4c2a3416cbba4ba779e181dd9` | PID `26904`, run `d3d1b74b7ed2478b8895494b6e0b2252`, runtime `dfb35ce0bfdd48de81f2bd62e28f2f21` |
| Web loss | PID `31008`, run `ffca45c99b824876b5e9f702434d20cd`, runtime `734de28a71524190a70d53177f6ae542`; Web PID `35548` | PID `15128`, run `d4e41a5987954240bde9d97cd4e0deca`, runtime `4da5096f11674391986d9fdd57c54b14`; Web PID `12884` |

The first reservation was at `2026-11-22T21:07:02.6445196+00:00`.
The second was at `2026-11-22T21:15:04.772172+00:00`. No start, restart,
manual tick or task Run command announced either replacement. The scripts only
observed files, Scheduler state, native processes and HTTP after each fault.
Native inventories showed one installed owner and one matching Web child after
tick exit. The idempotent manual start check kept the second replacement's ID.

The separate `identity-audit.json` passed 12 checks across four owners. It
compares recorded live HTTP runtime IDs, Project/repository identity, package
digest, installed build and exact owner/Web PID plus creation time. Bead
`proof-1iq` stayed open, unassigned and paused with `repair_cycles: 2`.
Its registered dirty worktree and temporary sentinel files survived both
recoveries. No Agent was admitted.

The first script stopped after owner recovery on a proof assertion: CIM rounds
creation time and did not equal the exact Web process timestamp. It did not kill
Web. `failure.json` retains the error and 20 preceding checks. Its teardown
passed, with 25 total checks and 273.6669572 elapsed seconds. This is retained
failed proof evidence, not an application test failure. The second script used
the owner's exact `web_release` PID/creation identity and a fresh manual fixture
owner. It reused the published package and the spent recovery attempt. It did
not repeat the passed owner-loss case.

The second script passed 47 checks in 485.0324672 seconds before teardown.
Including teardown, it passed 52 checks in 488.1875775 seconds. This includes
normal qualification/backoff and a 70-second wait after deliberate stop.
Maintenance was checked with enabled/running intent, independently of disable.
Maintenance leave cleared the token and launched nothing. Final state retained
two spent attempts, no pending attempt, no attention and no maintenance token.
Both exact tasks were absent; no process under the isolated installation remained.
The first and second timed scripts total 761.8545347 seconds. Package publication
and initial fixture preparation are separate from these script timers.

Observed Scheduler spacing sometimes fell below 60 seconds and restarted
qualification. A tick also ran just before the due time. The later eligible
ticks recovered without compressing backoff or refunding attempts. The recorded
times are evidence; the one-minute trigger is not an exact recovery-time promise.

## Coverage boundaries and cost

The normal suite adds no test, native journey, fixture publish, production check
or scheduling change for this documentation and verification task. The separate
native proof shares one published package, disposable repository and paused
worktree across owner loss, Web loss and suppression checks. It proves the
installed Scheduler/owner/runtime/Web boundary that injected operations cannot
establish. Its timer and backoff waits are outside complete-suite elapsed time.

The selected existing tests cover the larger failure matrix cheaply:

| Existing coverage | Boundary |
| --- | --- |
| `ScheduledSupervisionTests` | Duplicate/stale/revoked requests, partial registration, foreign definitions, lost responses, disabled install and retained budget |
| `SupervisionTickTests` | Web/runtime failure drain, exact pending reconciliation, uncertain launch, capture failure and suppression at async boundaries |
| `SupervisionPolicyTests` | Three attempts, backoff, exhaustion, clock/session gaps, no automatic budget refund and retained incidents |
| `OwnerFailurePublicationTests` | Discovery/timer/completion runtime failure before blocked drain, Web readiness failure and deliberate stop |

All 111 selected cases passed with zero failures and skips. The positive minimum
was 40. The retained command uses repeated fully qualified `--filter-class`
arguments and MTP XML output. `focused.log` and
`TestResults/ns-8s1h-focused.xml` retain the result.

The prior `ns-ghk3` topology proof already covers overlapping Scheduler requests,
consumption, original owner/child Job settlement and exact fixture removal.
This task does not repeat that synthetic topology proof. Its production mutex
and consumption checks remain in force in the installed journey.

Runtime infrastructure failures use the existing injected checks; this proof
does not inject a separate infrastructure exception into the installed runtime.
No logout, reboot, sleep, absent-session, production, long-running Agent or
overnight acceptance run is claimed. InteractiveToken/logon behavior is a task
definition and platform boundary; it is not a tested reboot result.
