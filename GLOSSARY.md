# Nightweaver

Nightweaver is an autonomous software factory that discovers user-authored Product Beads, plans them into actionable Plan Beads (or defers under-specified ones back to humans for clarification), dispatches Agents to implement, review, and merge changes, and cleans up its execution environment.

## Language

**Bead**:
A tracked unit of work in the Beads (`bd`) tracker managed by Nightweaver.
_Avoid_: Ticket, issue, card, story

**Product Bead**:
A human-authored Bead describing user-level product requirements, feature requests, or bug reports that enters Nightweaver for Planning.
_Avoid_: Feature request, intake ticket, user story

**Plan Bead**:
An Agent-authored (or human-authored) Bead linked to a Product Bead that contains a concrete technical implementation plan ready for Delivery.
_Avoid_: Implementation ticket, dev task, sub-ticket

**Orchestrator**:
The deterministic Nightweaver host process that discovers Beads, manages isolated execution environments, dispatches Agents, and performs cleanup.
_Avoid_: Runner, harness, daemon

**Agent**:
A scoped LLM worker invocation dispatched by the Orchestrator to perform a single lifecycle responsibility on a Bead.
_Avoid_: Worker, bot, subagent

**Agent CLI**:
The external command-line runtime (such as Claude Code or Codex CLI) through which the Orchestrator launches an Agent process.
_Avoid_: Harness, wrapper, provider SDK

**Project**:
A named Nightweaver configuration that binds a Target Repository to the Orchestrator settings (concurrency limits, Agent CLI routing, timeouts, and retry budgets) used to run Nightweaver against it.
_Avoid_: Workspace, profile, target

**Target Repository**:
The external Git repository and Beads (`bd`) tracker associated with a Project that the Orchestrator monitors and delivers changes into.
_Avoid_: Client repo, host repo

**Planning**:
The pre-implementation stage in which a Planner Agent and Plan Challenger Agent evaluate a Product Bead and either create linked Plan Bead(s) for Delivery or defer the Product Bead back to a human with clarification questions.
_Avoid_: Triage, grooming, intake, preflight

**Delivery**:
The execution stage in which a Plan Bead is implemented, reviewed, verified by QA, and merged into the Target Repository inside an isolated worktree, followed by cleanup.
_Avoid_: Execution phase, Stage 2, build pipeline

**Planner Agent**:
The Planning Agent that evaluates a Product Bead against the codebase, drafts and revises a candidate decomposition of Plan Beads, creates the approved Plan Beads in the tracker, or defers the Product Bead with clarification questions (`product-decision-required`).
_Avoid_: Triager, spec writer

**Plan Challenger Agent**:
The Planning Agent that independently critiques a Planner Agent's drafted Plan Bead decomposition before any Plan Beads are created in the tracker.
_Avoid_: Plan reviewer, spec validator

**Implementer Agent**:
The Delivery Agent that writes code, tests, and domain documentation in an isolated worktree to satisfy a Plan Bead's specification, commits and pushes its branch, opens or updates the Pull Request, and resolves findings from downstream verification or merge roles.
_Avoid_: Coder, developer agent, builder

**Human Attention Desk**:
A pinned local console view of Beads paused for `product-decision-required` or `needs-human`, with guarded human guidance, retry, and abort actions.
_Avoid_: Agent monitor, live telemetry

**Human Guidance Comment**:
A structured, human-authored Bead comment recorded by the Attention Desk; `note_only` preserves the pause, while `resume` accompanies an explicit guarded pause-label removal.
_Avoid_: Agent finding, automatic retry

**Reviewer Agent**:
The Delivery Agent that performs static code, architectural, and test-quality review of an Implementer Agent's changes against codebase standards and the Plan Bead's specification.
_Avoid_: Code reviewer, static analyzer

**QA Agent**:
The Delivery Agent that boots and exercises the running software against a Plan Bead's acceptance criteria and edge cases to validate live behavior beyond unit tests and static review.
_Avoid_: Tester, verifier

**Merger Agent**:
The singleton Delivery Agent that processes `stage:merge` Plan Beads one at a time, synchronizing their Pull Request with `main` (resolving rebase conflicts when possible), verifying CI passes, and merging the approved change into the Target Repository (or returning unresolved conflict/CI failures to the Implementer Agent).
_Avoid_: Shipper, release agent, PR bot
