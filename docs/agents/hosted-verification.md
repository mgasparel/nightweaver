# Local iteration and final hosted verification

Implementer publishes a draft PR after local checks. Before pushing repairs or resumed work to an already-ready PR, convert it with `gh pr ready <pr> --undo` and confirm `isDraft: true`. Reviewer uses static inspection and local implementation evidence; QA exercises local behavior. Their existing workflow authority and repair budgets remain unchanged. Only Merger promotes the synchronized, locally checked candidate after Review and QA.

This repository's `build-test.yml` starts its expensive Windows job for ready PRs, main pushes, or deliberate `workflow_dispatch`. Draft `opened`, `synchronize`, `reopened` and `converted_to_draft` events skip the job before allocating a runner. `ready_for_review` starts final verification. PR concurrency cancels superseded PR runs, including conversion back to draft. Dispatch runs have separate branch groups; main pushes retain their verification. Required check name **Windows build and test** and branch protections remain unchanged. A skipped job is not final verification, even though GitHub may present the workflow as successful.

Merger must convert any already-ready PR to draft before synchronization, run required local checks even on an unchanged head, push while draft, verify remote/local head identity, then mark ready. Always make an actual draft-to-ready transition on each dispatch; this handles unchanged heads, resumes and rebase pushes. Note the readiness time and identify the new `pull_request` run of `build-test.yml` for the branch and exact head, created at/after that transition:

```powershell
gh run list --workflow build-test.yml --branch nightweaver/<bead-id> --event pull_request --json databaseId,headSha,createdAt,status,conclusion,url
gh run view <run-id> --json headSha,event,status,conclusion,jobs,url
gh run watch <run-id> --exit-status
gh pr checks <pr> --required --json name,state,bucket
```

Wait for the readiness run to register; distinguish it from a draft synchronization run by inspecting that its Windows job actually ran and succeeded, rather than being skipped. Require the correct workflow, current head, event, readiness time, successful run and non-skipped successful job, then a nonempty set of required PR checks all with `state: SUCCESS` and `bucket: pass`. Re-read PR head after waiting. On changed head, return to draft and repeat synchronization, local checks, push and readiness. Missing runs/checks are blockers, never evidence of success. Merge with `--match-head-commit <checked-head>` plus the repository's merge method. Record run URL/ID, head, tested checkout revision and result in a natural-language Bead comment separate from the fixed merge-evidence line. Restore draft before handing a failed candidate back for repair. A later repair still passes the ordinary Review and QA gates before Merger repeats final verification.

## Explicit hosted investigation

Only a dispatched Bead explicitly requiring CI investigation or hosted verification authorizes a worker to dispatch/rerun during iteration. This is task scope, not a new permission system. Keep the PR draft. Dispatch the workflow from the candidate branch, supplying the Bead and exact authorizing requirement:

```powershell
gh workflow run build-test.yml --ref nightweaver/<bead-id> -f bead=<bead-id> -f 'requirement=<specific dispatched hosted-verification requirement>'
gh run list --workflow build-test.yml --branch nightweaver/<bead-id> --event workflow_dispatch --json databaseId,headSha,createdAt,status,conclusion,url
gh run watch <run-id> --exit-status
gh run view <run-id> --json headSha,event,status,conclusion,jobs,url
```

Record the requirement, workflow, run ID/URL, actual tested revision and outcome in the Bead comment. Verify the selected run's head against the intended candidate; branch movement can change what dispatch tests. For reruns, use `gh run rerun <run-id>` only for a run testing that intended revision and record its attempt and outcome. Neither dispatch nor rerun substitutes for the subsequent lifecycle gates or Merger's readiness run. Workflow dispatch requires the workflow to exist on the default branch; `--ref` selects the candidate version. Verify that selected version contains the intended dispatch contract.

The job summary records actual checkout revision, run URL and dispatch inputs through environment variables. PR CI checks the GitHub merge ref (head combined with base); dispatch checks the selected ref. Record both the PR head and actual checkout revision when they differ. Generic role prompts defer to Target Repository policy because drafts do not universally suppress CI.

## Timing

Hosted correctness CI reports suite wall time diagnostically and retains a 15-minute job hang timeout; elapsed time above five minutes alone is not failure. The separate **local complete-suite requirement remains below 300 seconds on latest main**, including normal build, fixture publishing, tests and teardown at committed six-thread parallelism. Use the full command and stopwatch in [tests/README.md](../../tests/README.md); filtered tests and hosted timings cannot establish this local performance gate. Report candidate measurements as candidate evidence and remeasure latest main after integration. No test deletion, filtering or performance waiver is introduced.

GitHub event and concurrency semantics: [workflow events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows), [workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax).
