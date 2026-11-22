# Scheduled supervision operations

Supervision is opt-in. It recovers a local installed owner with the existing
runtime and Web Console. It does not check, download, install or replace software.
An operator must deploy each reviewed package. Use the installed CLI for all
commands below. Run commands as the selected Windows user in that user's existing
interactive session. Use an explicit Project selector for each command.

```powershell
$nightweaver = 'C:\Nightweaver\nightweaver.exe'
& $nightweaver background supervision status --project demo
& $nightweaver background supervision install --project demo
& $nightweaver background supervision status --project demo
```

Install creates **two disabled tasks** and persists disabled, stopped intent.
Inspect both registered definitions before enable. Status supplies `task_name`,
`owner_task_name`, both definitions, enabled/running state and last results, and
the durable supervision state. Task names bind the installation root,
configuration, physical Git common directory and user SID. Another installation
has another identity. A foreign or changed definition causes refusal.

| Task | Triggers | Action and bounds |
| --- | --- | --- |
| Tick | Selected user's logon; one-minute timer | Installed `background tick --config ...`; IgnoreNew; 240-second Scheduler limit; 180-second internal deadline |
| Owner | None; demand start only | Installed `background owner ...`; exact reservation ID and generation; IgnoreNew; no Scheduler execution deadline |

Both tasks use limited `InteractiveToken` credentials. A tick reserves an attempt
before it requests the separate owner task. It exits without waiting for that
owner. A successful Scheduler Run reply means only **requested**, not Healthy.
The owner consumes the exact request under startup and owner locks, then acquires
the existing physical repository mutex. A later tick must confirm matching native
owner, runtime and Web identity. An unresolved request cannot launch again.

## Enable and inspect

```powershell
# Establish the first owner record and select the loopback URL retained for recovery.
& $nightweaver background start --project demo --urls http://127.0.0.1:5000
& $nightweaver background supervision enable --project demo
& $nightweaver background status --project demo
& $nightweaver background supervision status --project demo
```

Enable preserves attempt history and requests the first finite tick. Periodic
ticks do later work. A missing owner record is Unknown and grants no launch.
Use explicit manual start for the first owner and after a deliberate stop.
Inspect `observation`, native identity, source freshness,
`attempts`, `pending_attempt`, `next_due_utc`, `attention` and `exhausted`.
The state file uses camelCase names; CLI output uses snake_case names.
Do not infer readiness from task success, a PID alone, an HTTP response alone,
an old telemetry file or an owner-lock release.

Qualification requires two matching Failed observations 60–120 seconds apart.
Backoff is 30 seconds, 2 minutes and 10 minutes for attempts one through three.
The next eligible tick rounds up the due time. Health, tick restart and logon do
not refund attempts. Unknown identity, source gaps, uncertain settlement, capture
failure and exhausted attempts retain evidence and prevent another launch.
The runtime is in the owner process; the Web Console is a contained child.
Supervision does not clear human pauses or reset Delivery repair counts.
Ordinary Orchestrator startup retains its own guarded claim-recovery authority.

## Stop, disable and remove

```powershell
& $nightweaver background stop --project demo
& $nightweaver background supervision status --project demo

& $nightweaver background supervision disable --project demo
& $nightweaver background supervision uninstall --project demo
& $nightweaver background supervision status --project demo
```

Stop records deliberate stopped intent before drain. Later ticks must keep it
stopped. An explicit start or restart requests running intent again. A first stop
uses normal Agent bounds and has no fixed runtime drain deadline. Use the existing
explicit `background stop --force` path only when the operator intends force stop.

Disable and uninstall first persist suppression and advance generation. They
revoke queued launch authority. They leave an existing owner for explicit stop.
Uninstall removes only matching task definitions. It retains state, attempts,
logs, capture bundles, worktrees and temporary work. Confirm both registrations
are absent and confirm native process settlement after stop. Do not delete state
or reinstall to bypass exhaustion. No public recovery-budget reset command exists
in this version. A maintainer must inspect exhaustion and choose a supported repair.

## Maintenance

```powershell
& $nightweaver background maintenance enter --project demo
& $nightweaver background maintenance status --project demo
# Read the current token and revision from this fresh status.
& $nightweaver background maintenance leave --project demo --token <token> --revision <revision>
```

Enter persists an operation token before drain. The token has no timeout. Leave
requires the exact current token and revision; it starts nothing. Inspect status
again after any concurrent stop or disable. Never release another operation's
maintenance token. Package replacement remains a separate explicit operation.

## Session boundary and failure diagnosis

Reboot recovery can begin only after the selected user logs on. These tasks do
not create a session. They provide no service, password, S4U or boot-before-logon
fallback. Logout, sleep, Scheduler failure and an absent interactive session are
observation gaps. They do not establish health or permit another execution user.

1. Preserve the original failure before restart, reset or cleanup. For resource
   slowdown triggers, follow [capture-first diagnostics](test-cost-evidence.md).
2. Read background status and supervision status with the installed executable.
   Retain both task definitions, enabled/running states and last results. Check
   that action paths still refer to the same installed package and configuration.
3. Inspect `run/<project>/supervision/state.json`,
   `run/<project>/background/owner.json`, `owner-failures.json`, and
   `observations.json`. The last two files are in the background directory.
   Inspect the host and Web log paths returned by status.
4. Match the pending reservation, generation, owner run ID, runtime ID, PID and
   creation time. Preserve uncertain launch replies. Never replay a request from
   its old command line. A previous owner's exit does not prove a queued owner's
   absence. Native exit and original Job settlement are separate from lock release.
5. If storage is unavailable, preserve the reported canonical path and diagnostic.
   Retain interrupted `state.json.*.tmp` files. A maintainer must inspect foreign
   definitions, malformed stores, required capture failure and exhausted budgets.
   Missing storage means manual opt-out; access denial does not mean missing.

Use [the isolated acceptance record](../acceptance/scheduled-recovery.md) for tested
boundaries and evidence. It does not certify production operation or overnight
reliability. The [supervision contract](../design/supervision-v1.md) defines the
policy and injected coverage.
