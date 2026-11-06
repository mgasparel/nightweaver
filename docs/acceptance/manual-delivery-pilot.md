# Real-repository manual Delivery pilot proposal

**Proposal only. No real-repository runtime or Agent task was launched for this document.** The selected target proposal is the local Nightshift repository at `H:\code\nightshift`, using a documentation-only task after this candidate is reviewed and merged. No push, pull request, or remote merge is part of this proposal.

## Observed local settings

Read-only discovery on 2026-11-06 found:

- Canonical repository: `H:\code\nightshift`; local `main` was `c5a0fcb7df8187897270e626b250e6d73efeede0` at discovery time.
- Codex executable: `C:\Users\mgasp\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe`.
- `codex --version`: `codex-cli 0.166.0`.
- The current user Codex configuration sets model `gpt-6.1-terra` and reasoning effort `low`. These are ambient machine settings; they are not present in a Nightweaver Project JSON.
- No Nightweaver `project.json` was found in the canonical repository, `%APPDATA%\Nightweaver`, or `%LOCALAPPDATA%\Nightweaver`. The JSON below is an explicit proposal, not a discovered or created file. Reconfirm the model and executable immediately before any eventual run.
- The existing manual QA fixture uses fake Agent aliases and fixture CI. Those stand-ins are excluded from the real-repository pilot; success requires a live invocation using the installed Codex CLI and honest runtime telemetry.

## Proposed Project JSON

If the maintainer selects this target, create this file outside the repository as a temporary Project configuration, review its resolved paths, and remove it after the pilot. It was not created by this implementation.

```json
{
  "name": "nightshift-pilot",
  "target_repository": {
    "path": "H:\\code\\nightshift",
    "default_branch": "main"
  },
  "concurrency": {
    "max_concurrent_planning": 1,
    "max_concurrent_delivery": 1,
    "max_active_plan_beads": 1
  },
  "limits": {
    "max_repair_cycles": 1,
    "max_operational_retries": 1
  },
  "agent_defaults": {
    "cli": "codex",
    "model": "gpt-6.1-terra",
    "effort": "low",
    "timeout_minutes": 30
  }
}
```

The concurrency, retry, and timeout values above are proposed bounds for one small documentation task. They are not existing Nightweaver project settings. The model and effort reflect the read-only local Codex settings observed above; the CLI is the installed executable on `PATH`.

## Pilot task and procedure

Use one manually authored task Bead whose complete scope is a short documentation correction in `README.md`. Do not enable Planning or create additional tasks automatically. Start from a clean `main` worktree and a disposable task branch. Run the continuous runtime with the proposed Project config, observe the real Codex process and live factory telemetry, and let the normal Delivery, review, QA, and Merger flow decide the task. Run the repository build and normal-parallel test suite as QA. Do not issue direct stage transitions or synthetic telemetry.

Before starting, record the resolved Project JSON, repository and branch, `codex` path/version, model and effort source (Project override or ambient config), concurrency, repair/operational limits, timeout, available local checks, and the authority boundary. Redact credentials and never copy tokens into Beads or logs.

## Success criteria

- The live invocation uses the recorded executable/model/effort and reports those values honestly; no fixture alias or fake CI is on `PATH`.
- The one task is claimed by the configured Nightweaver actor, reaches reviewer and QA evidence, and is merged only after required local checks pass.
- The task closes only after local `main` contains the verified merge commit. Record the pre/post `main` SHA, task branch, worktree path, and merge evidence.
- Runtime and native process identities match the observed telemetry. After the browser closes, the runtime remains live until explicitly stopped; the task's evidence remains readable.
- The task branch/worktree and temporary Project config are cleaned up only after the verified closure; no unrelated files or Beads change.

## Abort criteria

Stop admission and preserve the task/worktree for human review if the selected Project JSON differs from the proposal without review, the real CLI/model cannot be confirmed, credentials or required local checks are unavailable, the target is dirty or the branch changes unexpectedly, another owner is mutated, an Agent times out or escalates, telemetry cannot identify the live process, or merge/closure evidence is incomplete. Do not retry by editing tracker stages, skip checks, or push to a remote. Report the exact state and leave the Bead open.

## Evidence record

For an eventual authorized run, append a Beads comment with the nonsecret resolved Project values, CLI path/version, model source, task ID, actor, runtime and Agent PIDs, branch/worktree, live-log observation, build/test commands and results, before/after `main` SHAs, cleanup result, and any limitation or abort reason. This proposal records no pilot execution result.
