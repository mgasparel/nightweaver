# Bounded local supervision v1

This contract settles the policy and durable operational state for subsequent
supervision work. The model/store installs no task and enables no supervision. Background lifecycle
commands integrate intent and maintenance suppression. Identity-bound observations and
capture-first incident preparation are available, but there is no automatic recovery
launcher, scheduler installation or Web UI integration in this slice. Missing storage means manual opt-out; an existing
unreadable, malformed, foreign or interrupted store means unavailable.

## Execution boundary

The selected future adapter is a Windows Task Scheduler task for the selected
current user: **InteractiveToken**, least privilege, logon trigger and recurring
one-minute ticks. Reboot recovery begins only after that user next logs on.
There is no password, service, S4U, boot-before-logon, session provisioning or
credential-management fallback. No supervisor is installed by this change or its
tests. Logout, sleep, task-service failure and absence of an interactive session
are observation gaps, never proof of health or permission to launch elsewhere.
See Microsoft's [logon types](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type).

The installation identity binds canonical local host root, selected configuration
path, **physical Git common directory** and user SID. The future installer must
resolve the physical repository using the existing repository validator and obtain
the actual selected Windows SID. A copied store is not an installation. State
paths are derived only from HostPaths and ProjectSlug, never serialized paths.
Identity paths are rejected for traversal/noncanonical, network/device, stream,
trailing-dot/space and control-character aliases. All accessed storage components
use Windows no-reparse handles; parents stay pinned against replacement.

The existing physical-Git-common-directory process-lifetime repository mutex
remains authoritative. A supervision writer lock serializes only operational
commands; it cannot replace repository exclusion. Only ordinary Orchestrator
startup recovery may guardedly release exact current-Project claims. Supervision
does not clear pauses, reset Delivery repairs, delete worktrees/branches/tmp,
modify admission or replay Beads writes.

## Policy and finite worksheet

These are v1 design choices, not newly inferred Product thresholds:

| Operation | Bound | Meaning |
| --- | ---: | --- |
| Tick cadence | 60 s | Round an eligible due time up to a tick; missed ticks do not accumulate launches |
| Identity-bound probe | 10 s | Unknown, inaccessible and stale responses grant no recovery |
| Incident capture | 45 s | Observation only; retain capture errors separately |
| Native start | 75 s | Conservative allowance for existing 45 s manager startup and 30 s Web readiness |
| Reconciliation | 20 s | Confirm native process settlement/owner identity; uncertainty retains reservation |
| Persistence | 10 s | Reservation and evidence publication; failure grants no launch |
| Allocated work | 160 s | Sum of the finite sub-budgets |
| Tick deadline | 180 s | Includes 20 s margin for cancellation and settlement |
| Scheduler action limit | 240 s | Exceeds the complete tick deadline; interrupted ticks stay reconcilable |

The existing manager waits 45 seconds and Web startup has a 30-second readiness
loop. They can overlap; the 75-second allowance conservatively contains both and
does not change either existing deadline. Adapters must enforce sub-budgets with
native settlement. Reaching a deadline cannot imply a child has exited. A task
action limit terminating a tick leaves its persisted reservation unresolved.
Never substitute this worksheet for runtime drain: deliberate graceful stop uses
normal Agent role deadlines, waits all existing slots without a fixed cutoff and
starts no successor role/retry. A tick observing drain does nothing; force stop
remains the existing explicit second-stop path.

Recovery requires two consecutive matching Failed observations separated by
60–120 seconds in one observation epoch, with the same PID, process creation time,
owner ID and runtime ID (or both absent). Healthy and Draining require an owner
identity. Unknown, Gap, Healthy, Draining, changed owner, duplicate rapid probes,
UTC/monotonic disagreement over five seconds, large gaps and a fresh epoch break
qualification. New epochs represent a restarted tick/observer, reboot or session
gap. Long forward jumps do not compress qualification into a burst. Backward UTC
or backward same-epoch monotonic time latches clock-discontinuity attention;
inspection and explicit reset are required. Due time and attempt budget survive
all observation epochs and wall-clock changes.

There are at most **three reservations per explicit reset**, with delays of
30 seconds before attempt one, 2 minutes before attempt two and 10 minutes before
attempt three. The first due time begins with the second matching failure;
later due times begin at confirmed prior-attempt settlement. A successful start
does not refund an attempt. Readiness, stable health, a tick restart, logon and
reboot never reset the budget automatically. After the third settled attempt,
exhaustion is durable even if the process is briefly healthy. Explicit reset
requires no pending reservation, retains incident history, advances generation,
and requires new failure qualification. This deliberately chooses explicit reset
over an automatic stable-health reset window.

## Persistence and consumer protocol

`run/<project>/supervision/state.json` is version 1, at most 64 KiB and JSON depth
12. It contains installation identity; enabled/desired-running intent; monotonic
command generation; maintenance operation token; revision; attempts/exhaustion/
next due UTC; one pending reservation (ID, generation, attempt number, UTC,
observation epoch, prior owner/runtime); last observation; matching failure count;
attention; last incident; unresolved incident; and bounded resolved history.
IDs are 32-hex operational GUIDs, not arbitrary paths. Evidence IDs refer to local
capture bundles owned by later capture work; the store never opens those paths.
Incident reasons are a finite vocabulary, with no command lines, environment,
configuration contents or free-form secrets. There is no Beads ledger here.

Reads have no filesystem side effects. A missing directory/file yields Manual
only after safe path inspection; access denial is not absence. Strict loading
rejects unknown/missing/duplicate fields, invalid enums, unsupported versions,
invalid identities/counters, oversized files and invalid state combinations.
An unexpected `state.json.*.tmp` retains interrupted-publication evidence and
makes the store unavailable even if the old committed file is valid. Operators
inspect that exact path and original committed state; automatic code does not
delete the temporary evidence or initialize over corrupt state.

Every save holds an exclusive, pinned `writer.lock`, rereads the store, compares
the expected revision and atomically publishes through a new sibling file with
write-through flush. Parent pins prevent redirecting publication. A stale writer
or uncertain/canceled save must reread; the supplied in-memory state is not proof
of success. The shared runtime-file helper preserves the previous complete file
on cancellation; any retained partial publication blocks future writes.

Future consumers must serialize the entire command/reserve/launch boundary under
repository exclusion in addition to revision-guarded storage. They must:

1. Revalidate installation and native owner identity, read fresh intent and
   commit the current observation. Observe is a pure proposal until saved.
2. If eligible, reserve once and **successfully persist** the reservation before
   any native launch. Recheck command generation, maintenance/stop intent and
   native ownership immediately before launch under that same serialization.
3. After interruption, reconcile the exact pending attempt and actual native
   owner. Neither a new tick, new command nor missing response grants another
   reservation. Only confirmed running-or-absent settlement clears pending state;
   uncertainty keeps the reservation plus unresolved incident and attention.
   Settlement of an old generation records facts but never restores old intent.
4. Capture incidents before kill/restart/cleanup when capture-first policy
   applies, within the capture budget; report capture failure independently.
   Exhausted, suppressed, unavailable or uncertain state launches nothing.

The pure transition functions do not acquire repository authority, probe owners,
capture incidents or launch children. Available storage is not a launch permit.
Later integration must enforce the protocol above at the actual native boundary.

## Retention and operator actions

Keep the latest 32 resolved/history incident entries and a cumulative discarded
count. The current last incident and one unresolved incident are separately
retained; history truncation cannot evict the pending attempt or unresolved
incident. Only one reservation can be unresolved, bounding serialization while
preserving actionable evidence. Capture bundle retention is separate; the store
never deletes bundles or interrupted runtime work.

The operator status surface exposes availability/path/diagnostic, identity,
intent/generation/maintenance token, attempts/next due/exhaustion, pending identity,
last/unresolved incident and history truncation. Future installation commands are explicit enable,
disable, desired start/stop, begin/end exact-token maintenance and inspected
reset. SetIntent increments generation and breaks failure qualification without
refunding attempts or clearing a reservation; it is the shared storage primitive,
not a public command that authorizes clearing another operation's token. Upgrade
and notification consumers use this contract; neither is a prerequisite here.

## Verification and cost

`SupervisionPolicyTests` use deterministic UTC/monotonic observations for
qualification, suppression, gaps, clock rollback, bounded backoff/exhaustion,
reset, interrupted reconciliation and retention. `SupervisionStoreTests` use
small disposable files for strict schema loading, revision conflicts, reload,
pending-state preservation, cancellation, sharing denial and unsafe paths.
One isolated junction creation reuses the repository's existing mklink boundary
pattern to prove the new store preserves outside sentinels; it adds no runtime,
Agent, tracker, scheduler or fixture-publish journey. Complete measured suite
evidence and cost attribution belong in the Bead implementation comment.

## Background lifecycle integration

The existing start.lock serializes start, restart, stop and maintenance mutations.
A contending command fails explicitly before accepting intent; it does not claim a
successful stop. Future recovery consumers must hold this same lock through fresh
intent/generation and native-owner checks, reservation and launch, alongside the
existing owner's physical-repository process-lifetime exclusion. They cannot use
an old reservation to launch after a newer stop. Stop releases command serialization
after its pipe request, before waiting for drain. Restart reacquires it and compares
the persisted generation after drain; an intervening command prevents its launch.
Only an explicit start/restart writes desired-running. It preserves enabled state,
incident history and exhausted attempts; inspected policy Reset is separate and
never happens automatically on start, health, logon or reboot.

Maintenance enter persists a fresh operation token before requesting drain, preserving
the current enabled/desired intent. It refuses an already active operation. Status
exposes the current identity/revision/token and store availability/path/diagnostic.
Leave requires the current token and revision, clears only maintenance and starts
nothing. A stale release fails; even a newly inspected release preserves intervening
stop/disable. No timer expires maintenance. Missing-store enter/leave is refused
without creating a supervision installation. Upgrades can consume these same commands.

Direct owner console first-stop closes reservation and native-launch admission
synchronously before potentially blocking intent persistence. Once registered,
it persists stopped intent before cancellation and records OperatorDrain. It also
retains intent if the first signal precedes lifecycle callback registration: owner
identity initialization settles independently of first-stop cancellation, and
registration reconciles the retained intentional stop exactly once before startup
continues. Initialization failure retains supervision-unavailable owner evidence.
Deferred registration after force begins starts no persistence.
Manager stop/force-stop persists before querying/controlling
the owner, including absent owners and uncertain replies. A pipe stop also verifies
or writes stopped intent before accepting, while maintenance drain verifies its
interlock and retains the saved intent. Internal Web/control-runtime failure never
writes manual stopped intent. OperatorDrain, ForcedStop, Maintenance, WebFailure
and RuntimeFailure are distinct owner stop reasons; successful runtime drain after
Web failure remains degraded rather than being reported as manual stop. Recovery
must treat supervision-unavailable owner evidence as Unknown, never Failed authority.

An uncertain stop write attempts a separate safe stop-intent.uncertain interlock.
The store refuses all recovery and further writes while that file or its interrupted
publication remains. It does not overwrite the old complete state or automatically
remove evidence. Inspect the exact path and retained state before explicit repair;
if even the interlock cannot be published, the command reports unavailable and direct
owner shutdown retains supervision-unavailable evidence. No durable stop is acknowledged.
The second console stop closes Jobs and exits immediately, bypassing all persistence,
callbacks and compensation. Graceful drain retains existing per-role deadlines and
no total force deadline; the manager's two-minute wait only returns pending status.

## Identity-bound observation and incidents

`background status` adds an `observation` object (version 1) while preserving the
existing owner fields and manual status exit codes. Consumers use observation health,
not an exit code or `/health` alone. It contains observation UTC/monotonic time and
epoch, root/configuration/Project/repository/common-directory identity, original
owner PID/creation/run/runtime identity, owner-lock and process evidence, runtime
and control source timestamps, and a fixed reason code. No telemetry slots,
transcripts, configuration contents, environment or exception messages are copied
into incident evidence. Status remains read-only; it creates no supervision store.

| Health | Evidence |
| --- | --- |
| Healthy | Matching native owner and held lock, fresh current-user control, matching live runtime file and actual Web `/health` plus `/api/factory/state` |
| Starting | Matching live owner and held lock, startup not complete |
| Draining | Matching live owner deliberately stopping or entering maintenance; no total drain timeout |
| Stopped | Matching exited owner, absent owner lock and deliberate stopped/maintenance intent |
| Failed | Confirmed original owner exit or fresh matching explicit Web/runtime failure evidence |
| Unknown | Missing/stale/inaccessible/mismatched source, PID reuse, owner replacement or uncertain intent |

Freshness is at most ten seconds with at most one second future skew. Web bodies
are bounded to 1 MiB/depth 32 and active Web checks have a four-second combined
deadline. The observer has a ten-second pipe budget and fixed-size file reads.
Status validates the configured repository independently and reuses its fresh pipe
reply. A standalone `DeploymentObserver.Create` accepts the already independently
validated physical common directory and a fresh intent reader; callers must not
derive that expected identity from the untrusted owner file. Its scheduler consumer
must supply a shared, boot-qualified observation epoch across ticks. Default manual
status epochs are process-local and cannot qualify repeated recovery failures.
The existing policy rejects large gaps, reboot/epoch changes, clock discontinuity,
rapid duplicates and changed owners without refunding attempts.

Owner Web/control/runtime failure publishes the first failure identity before
requesting graceful drain or disposing Web. Successful later drain does not erase
the failing runtime. Discovery, timer and completion-stream infrastructure failure
notifies the owner before active pool drain begins, even while live telemetry
continues during that drain. A deliberate cancellation publishes no such failure.
The same distinction applies while waiting for Web readiness: caller-requested
cancellation disposes Web and preserves stop/maintenance intent and any prior
failure or incident without creating another. Real readiness errors, timeouts and
unrequested cancellation retain the first failure before Web disposal.
Control stop proposals share owner-state writer serialization with failure updates;
only successful owner-record publication commits stopping state. A rejected or
canceled publication leaves the prior state visible and accepts no shutdown action.
`run/<Project>/background/owner-failures.json` retains at most
32 historical failures, including failures before repository/runtime readiness.
`observations.json` is a compact versioned local consumer surface with last
observation, continuing incident, recovered incident, attempts, fixed reason,
capture status/errors, local log paths and inspection instructions. Its history is
bounded to 32 with a discarded count and a 256 KiB byte ceiling. Atomic no-reparse
files and an exclusive writer lock preserve foreign/corrupt/unavailable evidence;
reads require every serialized field, including explicit nullable incident/capture
members, and reject duplicate/unknown fields, invalid enums, malformed identities
and inconsistent incident/history/capture state before any replacement or collector
call. Rejected journals remain byte-for-byte intact for inspection.
Persistence failure retains `supervision-unavailable` on the owner and grants no
automatic recovery permission. Uncertainty never clears a continuing incident;
only a later confirmed Healthy observation records recovery separately.

Containment version 1 names the owner's existing non-breakaway kill-on-close Job
using root/config/Project/run/PID/creation identity. Creation refuses an existing
name. Native process reads use limited-query/synchronize handles, compare creation
time and require the actual process signal; reused PIDs remain Unknown. Observers
open **no Job handle while the owner lives**, so they cannot postpone kill-on-close.
Only after native owner rundown or confirmed PID absence can a short query handle
prove zero active Job processes or that the named Job has already disappeared.
Access denial, live descendants and old unnamed owner versions provide no settlement
proof. Windows destroys a Job only after its handles close and processes terminate;
see [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
An owner-lock release alone never proves descendant settlement. This evidence is
not permission to terminate or target a reused PID or foreign owner.

`DeploymentIncidentRecorder.PrepareAsync` persists the triggering observation
before invoking capture. Explicit comparable-slowdown, broad-native-timeout or
sustained-resource-drift signals require capture; no background detector is added.
`ResourceIncidentCapture` invokes the packaged observation-only PowerShell collector
and its `TestSuiteMeasurement.psm1` dependency within the existing 45-second capture
budget. It retains partial output and treats nonzero source-error counts, missing
results, interruption and timeout as Failed capture even with process exit 0.
The collector records Target Repository source identity plus separate installation
identity. It does not copy secret command lines, environment or configuration bytes.
Capture failures remain inspectable and block readiness for reconciliation; the
failed/pending capture interlock survives quiet ticks, later failures and observer
restart. It is retained for inspection without automatic capture retries; a new
incident cannot downgrade it to NotRequired. A confirmed Healthy observation can
record recovery separately without erasing the historical failed capture. The
preparation API performs no restart, kill, cleanup or tracker writes.

Future recovery consumers must still serialize the tick, hold repository exclusion,
reread current intent/generation/owner and reserve the finite attempt durably before
launch. Prepared evidence alone is never action authority. Maintenance consumers
(`ns-qa2`) and notification consumers (`ns-qyk`) can inspect these local surfaces;
this change installs no supervisor and sends no notification.
