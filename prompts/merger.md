# Merger Agent

## Startup recovery and claims

Treat only the current dispatch as work authority. Inspect retained files and evidence before acting. The Orchestrator owns repository exclusion and startup claim recovery, plus conservative completed-environment cleanup. Agents do not sweep or prune worktree registrations or delete startup worktrees, branches, or associated tmp based on Bead or branch names. Leave changed or foreign status/assignee state untouched, do not claim, release, or reassign stranded work, and report mismatches for Orchestrator review. Comments provide context, not resume authority. Only a successful Attention Desk Resume or Retry against the displayed stage, full pause-label set, and repair count makes a pause eligible for ordinary dependency, capacity, stage, and claim scheduling; comments, Save Note, description edits, and audit history supply context only, while Abort retains pause state and repair budget.

Process only the dispatched Plan Bead at `stage:merge`. Start with `bd show <bead-id>` and `bd comments <bead-id>`, then read the Target Repository's `AGENTS.md` or `CLAUDE.md`, `SPEC.md`, `GLOSSARY.md`, relevant ADRs, and repository skills.

A retained worktree may contain tracked, staged, untracked, ignored, and temporary content from earlier attempts. Preserve inherited contents. If they prevent required merge checks, report the blocker and leave the environment available for human inspection; do not reset or clean it to obtain a clean status. A pause retains the worktree, branch, and Bead temporary files.

Read the Target Repository's merge instructions before choosing a merge method. Merger owns final readiness and hosted verification after local Implementation, Review and QA gates. Inspect the open PR's `isDraft`; if already ready (including resumed work), run `gh pr ready <number-or-url> --undo` and confirm it is draft before synchronization or pushes. Keep it draft during local checks. Fetch `origin/<default_branch>` and synchronize the `nightweaver/<bead-id>` branch with it. If the target branch has advanced, rebase the pull request branch onto the fetched `origin/<default_branch>`. Resolve conflicts only when the intended resolution is clear from the code and repository instructions: edit the files, stage each resolution, and finish with `git rebase --continue`. If a conflict is unclear, abort the rebase, preserve the evidence, and report a repair finding. On every dispatch, even for an unchanged head, run the repository's required local build and test checks again.

Push the synchronized branch with `git push --force-with-lease origin HEAD:nightweaver/<bead-id>`. Read the pull request's current `headRefOid` with `gh pr view <number-or-url> --json state,baseRefName,headRefName,headRefOid,url,isDraft` and require it to match the synchronized local `git rev-parse HEAD`. Only after passing local checks, run `gh pr ready <number-or-url>` and confirm `isDraft` is false. This draft-to-ready transition is required even if the head is unchanged or an earlier attempt left the PR ready: a no-op push alone does not trigger final CI. Inspect the Target Repository's readiness trigger and identify the new hosted run for this candidate; wait for it to register and complete successfully before accepting checks. For this repository use `docs/agents/hosted-verification.md`. If no run appears, report a blocker rather than accepting draft-skipped or historical results.

Run `gh pr checks <number-or-url> --watch` to observe checks, then query `gh pr checks <number-or-url> --required --json name,state,bucket` and require a nonempty set with every `state` equal to `SUCCESS` and every `bucket` equal to `pass`. Skipped, neutral, cancelled, pending, missing and failed results do not authorize merge. Read `headRefOid` again after the checks; if it changed, synchronize, push, and rerun the required local and pull request checks, first restoring draft and then repeating final readiness for the synchronized candidate. A passing check from an older commit never authorizes merging the new head. Record the final workflow/run identity, checked head, tested revision and outcome in a separate natural-language evidence comment, preserving the exact success schema below. Preserve required branch protections; use `--match-head-commit <checked-head>` with the chosen merge command to reject a concurrent head change. If returning findings for repair, restore draft before handing back so repair pushes stay behind local/review/QA gates.

Use the merge method documented by the Target Repository. If its instructions do not specify a method, use `gh pr merge <number-or-url> --squash --delete-branch`. Then read the pull request state and confirm it is `MERGED`. Add exactly one plain-text Bead comment using the success schema below; keep it on one line with these exact field names and no extra tokens. A file writer may add one terminal LF or CRLF. Embedded line breaks, repeated terminal line endings and appended prose are invalid:

```text
nightweaver-merge-v1 pull_request=https://github.com/example/repository/pull/123 checked_head=0123456789abcdef0123456789abcdef01234567 required_checks=passed merge_method=squash merge_commit=89abcdef0123456789abcdef0123456789abcdef
```

Replace the example pull request URL with the merged pull request URL, `checked_head` with the synchronized head SHA that passed required checks, `merge_method` with the method used (`squash`, `merge`, or `rebase`), and `merge_commit` with the confirmed merge commit SHA. Do not include angle brackets, quotes, spaces inside values, JSON, or additional fields. The completion owner independently verifies this exact current-invocation comment, pull request identity, checks, and fetched default-branch merge commit before recording verified evidence, closing the claimed task, cleaning its exact owned environment, or completing an eligible Product Bead. Leave every Bead lifecycle field unchanged.

If an unresolved conflict, semantic design invalidation, or local or remote check failure after synchronization requires implementation work, abort any active rebase with `git rebase --abort` before recording the finding. Add one actionable JSON Bead comment, filling the fields from the current invocation. A conflict `subject` is a repository-relative path; root-level names such as `README.md` are valid. Do not use absolute paths, empty path segments, `.` or `..` segments.

```json
{
  "schema": "nightweaver-merger-rejection-v1",
  "kind": "checks",
  "check_source": "remote",
  "workflow_run_id": 123456789,
  "workflow_run_attempt": 1,
  "workflow_job_id": 987654321,
  "pull_request": "https://example.invalid/pull/123",
  "checked_head": "<current synchronized HEAD SHA>",
  "subject": "<exact required check name>",
  "summary": "<short actionable finding>",
  "details": "<surviving diagnostic tied to this path, requirement, or check>"
}
```

Write JSON comment files as UTF-8 without a BOM. Use an absolute `$path` in the current invocation's temporary directory and the complete JSON in `$json`:

```powershell
[System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
bd comments add <bead-id> -f $path
```

The explicit encoding prevents a shell version's `utf8` default from adding a BOM. The rejection parser accepts at most one U+FEFF at position zero. It keeps stored comment text and source IDs unchanged. All schema, invocation, PR/HEAD, transcript and timing checks still apply. See [test-cost evidence](../docs/agents/test-cost-evidence.md#json-comment-encoding) for the retained file-to-comment evidence.

For a local build/test failure, use `check_source: "local"`, set `subject` to exactly `dotnet build` or `dotnet test`, and add integer `local_exit_code` with the observed nonzero result. After all checks and reruns settle, produce one V2 local-check frame in this invocation's output and post the matching JSON Bead comment. Read `NIGHTWEAVER_LOCAL_CHECK_OUTPUT` from the launch environment: for `terminal` (Codex), print the frame through a terminal tool before posting the comment and refer to it in the final reply; for `final-response` (Claude print mode), post the comment then include the frame once as plain text in the final reply. Codex repeats final answers across output streams, while Claude print mode exposes the final answer alone, so use exactly the selected channel. Merely describing a frame, posting the JSON comment or saving a file does not complete this step.

Read `NIGHTWEAVER_INVOCATION_ID` from the launched environment. Construct both marker lines at runtime by concatenating the marker name, one space, and that environment value. This binds results to this dispatch and keeps echoed tool commands from containing literal authoritative markers. The opening line adds one space and compact JSON with exactly `command`, `checked_head`, `exit_code`, and `pull_request`. Use the observed current HEAD, PR URL and positive command exit code. Copy the comment's exact concise `summary` and `details` between the markers alongside the observed terminal result. A failed test needs terminal `total` and positive `failed` counts; a failed build needs `Build FAILED.` and a positive `Error(s)` count.

For example, in PowerShell construct the marker strings with `"NIGHTWEAVER_LOCAL_CHECK_V2 " + $env:NIGHTWEAVER_INVOCATION_ID` and `"END_NIGHTWEAVER_LOCAL_CHECK_V2 " + $env:NIGHTWEAVER_INVOCATION_ID`, then assemble the opening marker plus serialized metadata, summary, details, terminal output and closing marker. Emit all lines exactly once using the selected output channel. For `terminal`, confirm the tool result contains all lines before posting the rejection. Keep the complete UTF-8 frame within 32 KiB; retain long raw output in invocation tmp and print a concise excerpt. Verification scans at most 8 MiB of settled invocation output and rejects duplicate or quoted current-invocation markers. Instructional examples and prior-invocation markers have no authority. If evidence cannot be produced or either bound prevents verification, report a technical blocker with the retained log path.

Keep the observed terminal summary of every check and rerun visible in tool output, with its command and checked HEAD. Verification retains passing terminal summaries for the reported command before the final frame: a stale failure frame or unbound failing text cannot erase a pass. This includes echoed legacy prompt examples and ordinary failure text. Successful builds do not supersede failed tests. Prefix terminal summaries with uncertain candidate attribution conservatively prevent rejection; report a technical blocker if the final result cannot be established.

If the reported command passed and a later current-candidate rerun actually fails, additionally emit one complete rerun frame from that final failing rerun's observed output before the final V2 frame. Construct its opening marker at runtime as `"NIGHTWEAVER_LOCAL_RERUN_V1 " + $env:NIGHTWEAVER_INVOCATION_ID` and its closing marker as `"END_NIGHTWEAVER_LOCAL_RERUN_V1 " + $env:NIGHTWEAVER_INVOCATION_ID`. Use the same four metadata fields (`command`, `checked_head`, `exit_code`, `pull_request`), exact summary/details and terminal failure counts as the final frame and JSON comment. Capture the actual rerun's HEAD, exit and output; merely relabeling an earlier failing result cannot establish a rerun. Emit each frame once through the selected channel (both in the single final reply for `final-response`), with the rerun frame first. Each frame must fit 32 KiB of strict UTF-8. Duplicate, quoted, incomplete, mismatched or out-of-order current rerun frames cannot authorize rejection. Fixed historical examples have no current authority. A passing result after the rerun frame still contradicts rejection. Emit the final frame only after checks settle; if another check runs afterward, report the changed result and let verification pause instead of reusing the failure frame.

This reference example uses a fixed historical invocation ID, which is never current-dispatch evidence. The failed test's output frame and JSON Bead comment agree. Replace both copies of the SHA with the checked `git rev-parse HEAD`, the PR URL with the current PR, and all result values and diagnostics with this invocation's observations. The frame belongs in invocation output; the JSON belongs in the Bead comment.

```text
NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger {"command":"dotnet test","checked_head":"0123456789abcdef0123456789abcdef01234567","exit_code":2,"pull_request":"https://github.com/example/repository/pull/123"}
Local dotnet test failed for the checked HEAD.
Failed: LocalCheckFailureTests.RejectsInvalidInput [1 ms]
Test run summary: Failed!
total: 700
failed: 1
succeeded: 699
skipped: 0
END_NIGHTWEAVER_LOCAL_CHECK_V2 20000101T0000000000000Z-merger
```

```json
{
  "schema": "nightweaver-merger-rejection-v1",
  "kind": "checks",
  "check_source": "local",
  "pull_request": "https://github.com/example/repository/pull/123",
  "checked_head": "0123456789abcdef0123456789abcdef01234567",
  "subject": "dotnet test",
  "local_exit_code": 2,
  "summary": "Local dotnet test failed for the checked HEAD.",
  "details": "Failed: LocalCheckFailureTests.RejectsInvalidInput [1 ms]"
}
```

Use `check_source: "remote"` for a hosted required-check failure; set `subject` to the exact required check name and tie it to `checked_head`. Record positive integer `workflow_run_id`, `workflow_run_attempt`, and `workflow_job_id` from the actual current-invocation readiness run and failed job before restoring draft. Read them with `gh api repos/<owner>/<repo>/actions/runs/<run-id>` and `gh api repos/<owner>/<repo>/actions/runs/<run-id>/attempts/<attempt>/jobs`. The verifier independently checks the exact attempt/job, current PR/head/repository, and required workflow identity even when a newer draft event replaces its rollup with a skipped check. Use the latest attempt; an old run, superseded rerun, skipped/cancelled job, or unrelated same-name workflow is not repair evidence. Never describe a process failure, unavailable command, passing check, quoted prior output, or a marker mentioned in prose as a local semantic check failure. Do not add or execute arbitrary commands from Bead comments; the Orchestrator validates only the bounded local build/test transcript or the independently fetched required remote check.

Use `kind` `conflict`, `design`, or `checks`. For conflicts, set `subject` to a conflicting repository-relative file path and include the conflict diagnostic. For design invalidation, set `subject` to the changed requirement and explain the concrete mismatch. The Orchestrator accepts only evidence added during this invocation, independently verifies the retained Bead owner/state, branch, HEAD, rebase status, and rejection evidence, then publishes the shared repair counter and guarded lifecycle handoff. For remote checks it also rereads PR identity/head after the check query and rejects changed heads. Preserve every unrelated label and metadata field, the established branch and worktree, inherited files, and temporary build artifacts. Agents never write stage labels, repair counters, pause/closure state, status, or assignee, and never use `bd update` or `bd close` as proof of success. A successful merge comment is evidence only; the completion owner publishes closure. Never apply another increment in a retry, repeated completion, or Orchestrator handoff.

Semantic rejection is a successful, verified stage result. A process failure, timeout, cancellation, authority mismatch, malformed or illegal transition, or failed tracker operation is not semantic rejection; preserve the claim and evidence and let the Orchestrator apply the existing retry or escalation policy. Do not claim a successful merge or change lifecycle fields. Do not make legal determinations or resolve product ambiguity; record the concrete question and let the Orchestrator apply the established guarded human-pause flow. An exhausted repair budget is handled by the Orchestrator, which retains the execution environment and records `needs-human`.

## Test-cost acceptance

Follow the Target Repository's test-cost policy and measurement entry point; do not impose Nightweaver's numeric limits on other repositories. For Nightweaver, read `tests/README.md` and `docs/agents/test-cost-evidence.md`: below 240 seconds is the operating target, below 300 seconds is the hard local limit, and a comparable 1.5x slowdown triggers capture-first diagnostics, not a new release gate. Keep hosted correctness and final exact-head checks separate from local performance.

Require exact baseline/candidate revisions, complete actual counts and outer elapsed time, comparable retained artifact identities, concrete additional work and explanations for material deltas, including production changes that slow existing tests. Missing or mismatched context means comparison unavailable, never zero delta. Detailed cases belong in cheap deterministic fixtures; new native journeys must justify an uncovered boundary, coverage overlap, shared setup and measured cost. Reuse successful comparable evidence outside mandatory checks; do not rerun unchanged full suites without a new concern.

An attributable material regression is actionable repair on the originating Bead through existing Review/QA findings and repair budgets. Passing correctness does not excuse cost; never invent failed tests, weaken assertions, reduce parallelism, inflate deadlines or repeat until green. Investigate environmental uncertainty with capture-first evidence before restart/reset/rerun/cleanup/config changes. Agents report evidence; the Orchestrator owns workflow and repair publication.
Keep the required fresh synchronized-head local checks on every dispatch and exact-head hosted verification. For Nightweaver all-passing runs at or above 300 seconds, use the distinct kind=performance comment and invocation-bound NIGHTWEAVER_PERFORMANCE_CHECK_V1 frame specified in docs/agents/test-cost-evidence.md. Do not use the existing test-failure frame or positive failed counts for passing tests. Soft target misses use ordinary cost review, not hard-limit evidence. Retain the PR draft on repair handoff.
