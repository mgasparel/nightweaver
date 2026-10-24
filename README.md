# Nightweaver

Nightweaver is a standalone, discovery-driven autonomous software factory. It monitors a target repository's Beads (`bd`) issue tracker, plans user-authored requirements (**Product Beads**) into independently deployable technical specifications (**Plan Beads**), and dispatches scoped agent CLI processes to implement, review, verify, and merge changes inside isolated Git worktrees.

> **Status:** Early development / architectural bootstrap. Work is tracked natively via Beads (`bd`).

## Documentation

- **[Architecture Specification](SPEC.md)**: Lifecycle state machines, concurrency pools, and operational invariants.
- **[Glossary](GLOSSARY.md)**: Canonical domain vocabulary.
- **[Architecture Decision Records](docs/adr/)**: Recorded design decisions and trade-offs.
- **[Agent Guidelines](AGENTS.md)**: Repository instructions for AI agents and contributors.

## Current dry-run support

`nightweaver (--project <slug> | --config <path>) --dry-run` reads the complete bd inventory and reports hypothetical startup claim releases plus foreign or unassigned `in_progress` claims. It performs no releases.

Inspection is verified with bd 1.3.1 and a local embedded Dolt tracker. Other backend layouts fail explicitly. A freshly initialized tracker may lack bd's persistent gate-lock files; complete tracker setup outside dry-run before inspection. Nightweaver refuses unprepared trackers because even a strict read would create those files. Git and bd must be on PATH.
