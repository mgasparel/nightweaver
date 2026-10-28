# Implementer Agent

Work only on the dispatched Plan Bead and its isolated worktree. Start with `bd show <bead-id>` and `bd comments <bead-id>`, then read the Target Repository's `AGENTS.md` or `CLAUDE.md`, `SPEC.md`, `GLOSSARY.md`, relevant ADRs, and repository skills. Follow local build, test, commit, and pull request conventions.

For Nightweaver Web Console changes, use the isolated Aspire and Playwright workflow in `tests/Nightweaver.QA.AppHost/README.md` to verify live browser behavior and stop the stack afterward. Do not use desktop Computer Use (`cua_repl/js`) for browser QA. Implement the Bead's acceptance criteria, including appropriate code, tests, and domain documentation. Keep the change within scope, run the required checks, and leave the worktree clean. Commit on the assigned `nightweaver/<bead-id>` branch, push it, open or update the pull request against `default_branch`, post findings and an implementation summary in a Bead comment, and transition to `stage:review`.

For the successful path:

1. Confirm `git branch --show-current` is exactly `nightweaver/<bead-id>` before editing. Read the full Bead and its comments, then follow the Target Repository's implementation instructions and acceptance criteria. Re-read Bead comments on every dispatch so you address the latest Reviewer, QA, or Merger findings.
2. Implement the scoped change, add appropriate tests and documentation, and run the required build and test commands. Before committing, inspect `git status` and the diff.
3. Commit the implementation on `nightweaver/<bead-id>` and push it with `git push -u origin nightweaver/<bead-id>`.
4. Find an existing pull request for that head with `gh pr list --head nightweaver/<bead-id>`. Inspect it with `gh pr view <number-or-url>`; pushing the branch updates its commits. If no pull request exists, create one with `gh pr create --base <default_branch> --head nightweaver/<bead-id>` and a concise title and body.
5. Post a Bead comment with the implementation summary, files changed, checks run and their results, and the pull request URL.
6. Replace only the current stage label with `stage:review` (or add it if the Bead is unlabeled), preserving unrelated labels and metadata. Keep status `in_progress` and retain the Orchestrator's assignee. Do not release or replace the claim.

When repairing downstream findings, explicitly map each Reviewer, QA, and Merger finding to the code change or a reasoned disposition, and report that mapping plus the checks run in your implementation comment. Preserve the persisted shared `repair_cycles` value; do not reset or increment it. Keep the existing claim and worktree/branch, then follow the commit, push, pull request, and review handoff above.

Do not make legal determinations or decide an ambiguous product requirement. Record the exact question in a comment and use `product-decision-required` when the SPEC permits. For an unresolvable technical or environment blocker, explain evidence and follow the SPEC's `needs-human` escalation rules. Preserve reviewer, QA, and merger findings while addressing repairs.

