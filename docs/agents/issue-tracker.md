# Issue tracker: Beads (`bd`)

Issues and specs for this repo live in Beads (`bd` CLI, stealth mode, prefix `ns`). Use the `bd` CLI for all operations.

## Conventions

- **Create an issue**: `bd create "<title>" -t <type> -d "<body>" -l "<labels>"`. For multi-line bodies, pass `--body-file <file>` or `--stdin`.
- **Read an issue**: `bd show <id>` (or `bd show <id> --json`) and `bd comments <id>` for comments.
- **List issues**: `bd list --status=open` (or `bd list --status=open --json`), filtering with `--label <label>`, `--parent <id>`, or `--ready`.
- **Make an issue a child of a parent**: `bd create "<title>" --parent <parent-id> ...` (or `bd update <child-id> --parent <parent-id>`).
- **Add / remove blocking dependencies**: `bd dep add <blocked-id> <blocker-id>` (makes `<blocked-id>` depend on `<blocker-id>`), or at creation time with `--deps blocked-by:<blocker-id>`. Remove with `bd dep remove <blocked-id> <blocker-id>`.
- **Comment on an issue**: `bd comments add <id> "<comment>"` (or `-f <file>`).
- **Apply / remove labels**: `bd update <id> --add-label "<label>"` / `--remove-label "<label>"`.
- **Update body / description**: `bd update <id> -d "<new-body>"` (or `--body-file <file>`).
- **Claim**: `bd update <id> --claim`.
- **Close**: `bd close <id> -r "<reason>"`.

## When a skill says "publish to the issue tracker"

Create a Beads issue via `bd create`.

## When a skill says "fetch the relevant ticket"

Read it with `bd show <id>` and `bd comments <id>`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single Beads issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Destination / Notes / Decisions-so-far / Not-yet-specified / Out-of-scope body:
  `bd create "<map title>" -t epic -l "wayfinder:map" -d "<body>"`
- **Child ticket**: an issue linked to the map via `--parent <map-id>` and carrying `--no-inherit-labels` so it does not inherit `wayfinder:map`. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`):
  `bd create "<question title>" -t decision --parent <map-id> --no-inherit-labels -l "wayfinder:<type>" -d "<body>"`
- **Blocking**: Beads' **native dependency relationship**, which powers `bd ready`. Wire a blocker in the second pass with:
  `bd dep add <child-id> <blocker-id>`
  A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open, unblocked, unclaimed children using Beads' native blocker-aware ready query:
  `bd ready --parent <map-id> --unassigned`
  First in map order wins.
- **Claim**: `bd update <id> --claim`, the session's first write before any work.
- **Resolve**: `bd comments add <id> "<answer>"`, then `bd close <id>`, then update the map body (`bd update <map-id> -d "<updated-map-body>"`) to append a context pointer (`- [<closed ticket title>](<id>): <one-line gist>`) under `## Decisions so far`.
