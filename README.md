# Nightweaver

Nightweaver is a discovery-driven software factory for Beads (`bd`) issues. It is under active development. The runnable CLI currently supports read-only startup inspection and `--dry-run`; live scheduling, worktree cleanup, issue updates, and Agent dispatch are not implemented.

## Local prerequisites

- The .NET 10 SDK to build, test, and publish Nightweaver.
- The .NET 10 runtime to run the framework-dependent published executable.
- Git and `bd` on `PATH` for a dry run. The supported tracker is bd 1.3.1 using an initialized embedded Dolt database.
- The Target Repository must be the root of a Git working tree and contain a prepared `.beads` tracker. Complete tracker setup before running Nightweaver. A fresh `bd init` may need a normal bd write outside the dry-run to create bd's persistent gate-lock files. Nightweaver does not initialize or migrate a tracker.

The integration tests use real Git and bd against disposable repositories. They require those tools and fail with a prerequisite error if either is missing; the real-tool gate does not silently skip. A dry run does not need an Agent CLI, `gh`, a network connection, an `origin` remote, or credentials.

## Build, test, and publish

Run these commands from the repository root:

```powershell
dotnet build Nightweaver.slnx -warnaserror
dotnet test Nightweaver.slnx
dotnet publish src/Nightweaver/Nightweaver.csproj -c Release --self-contained false -p:UseAppHost=true -o 'C:\Tools\Nightweaver'
```

The publish command produces a framework-dependent apphost named `nightweaver.exe` on Windows and `nightweaver` on Unix. The machine running it needs the .NET 10 runtime.

## Project files and path resolution

`<nightweaver-root>` is the directory containing the running `nightweaver` executable (`AppContext.BaseDirectory`). It does not depend on the caller's current directory. `--project <slug>` loads `<nightweaver-root>\projects\<slug>.json`.

`--config <path>` loads the explicitly named JSON file. A relative config path is resolved from the caller's current directory. A relative `target_repository.path` is resolved from the directory containing that JSON file. The Project's `name` owns its runtime namespace and must match the slug when using `--project`.

Keep the Nightweaver host and Target Repository in separate directories. Nightweaver stores Project configs in `projects\`, and reserves `worktrees\<name>\`, `tmp\<name>\`, and `logs\<name>\` under the host root. The Target Repository path identifies a separate root Git working tree with its prepared bd tracker.

A minimal valid Project config is:

```json
{
  "name": "demo",
  "target_repository": {
    "path": "H:\\Work\\Demo Product"
  }
}
```

JSON uses `\\` to represent a Windows path separator. The target path may also be relative to the config file's directory.

Both selectors work from an unrelated working directory. These PowerShell examples use paths containing spaces:

```powershell
$nightweaverRoot = 'C:\Tools\Nightweaver Host'
Set-Location 'C:\Tools\Unrelated Caller Directory'

# Loads C:\Tools\Nightweaver Host\projects\demo.json
& (Join-Path $nightweaverRoot 'nightweaver.exe') --project demo --dry-run

# Relative to the caller's current directory; quote the path with spaces.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --config '..\Nightweaver Host\projects\demo.json' --dry-run

# An absolute config path is also independent of the current directory.
& (Join-Path $nightweaverRoot 'nightweaver.exe') --config 'C:\Tools\Nightweaver Host\projects\demo.json' --dry-run
```

Use the Unix apphost name `nightweaver` in place of `nightweaver.exe` on Unix systems.

## Built-in defaults and role inheritance

Omitted settings receive these defaults:

| Setting | Default |
| --- | --- |
| `target_repository.default_branch` | `main` |
| `concurrency.max_concurrent_planning` | `2` |
| `concurrency.max_concurrent_delivery` | `3` |
| Singleton Merger slots | `1` |
| `concurrency.max_active_plan_beads` | `5` |
| `limits.max_repair_cycles` | `3` |
| `limits.max_operational_retries` | `2` |
| `agent_defaults.cli` | `claude` |
| `agent_defaults.model` | `null` (use the Agent CLI's model default) |
| `agent_defaults.effort` | `high` |
| `agent_defaults.timeout_minutes` | `30` |

The seven roles are `planner`, `plan_challenger`, `implementer`, `reviewer`, `qa`, `merger`, and `classifier`. Each role inherits each omitted property independently from `agent_defaults`. A role may override only the properties it needs. An explicit `"model": null` clears an inherited model and uses the Agent CLI's own default; omitting `model` inherits the configured value.

`max_active_plan_beads` gates new Planning selections only. Delivery and the singleton Merger slot are selected independently while Planning is backpressured.

For example, this sets a shared model and overrides the reviewer's effort while leaving all other values inherited:

```json
{
  "name": "demo",
  "target_repository": { "path": "H:\\Work\\Demo Product" },
  "agent_defaults": { "model": "example-model" },
  "roles": {
    "reviewer": { "effort": "medium" },
    "qa": { "model": null }
  }
}
```

These role settings are reported as configuration. They do not launch Agents in the current CLI.

## Dry-run report and safety

`--dry-run` validates the Project and Target Repository, reads the complete Beads inventory and readiness, inspects the Git worktree registry and Project runtime directories, then prints a deterministic hypothetical startup snapshot. It performs no cleanup or claim changes and launches no Agent processes.

- **Startup claim releases** list owned stranded `in_progress` Beads that a startup recovery would release. The tracker is unchanged.
- **Worktree removals** and **orphan tmp deletions** describe hypothetical cleanup. Linked, locked, external, missing, or uncertain paths are preserved or reported conservatively.
- **Retained worktrees** identify checkouts that the startup sweep would keep.
- **Selected** candidates fit the configured Planning or Delivery capacity, or the singleton Merger slot. **Waiting** candidates remain eligible but exceed capacity, or Planning is waiting for active-Plan backpressure. Neither section represents a claim or dispatch.
- A `hypothetical_post_release` source means the candidate would become ready after releasing a Nightweaver-owned stranded claim. It does not mean the release has happened.

Successful inspection returns exit code `0`, including when there are no candidates or no hypothetical cleanup. Inspection and tool failures return `1`; usage or configuration errors return `2`.

Inspection uses read-only bd commands and Git with optional locks disabled. The dry-run gate compares tracker issues and dependency relationships, comments, Git refs/index/worktree registrations, repository files, and host runtime directories. With bd 1.3.1's embedded Dolt backend, read-only queries can update modification times for the existing `.beads/embeddeddolt/.../.dolt/noms` directory, its `manifest` and `journal.idx`, and the chunk journal while leaving their bytes unchanged. Those backend timestamps are accepted; paths, file contents, Git state, and logical Beads data remain unchanged.

## Documentation

- [Architecture Specification](SPEC.md): lifecycle state machines, concurrency pools, and operational invariants.
- [Glossary](GLOSSARY.md): canonical domain vocabulary.
- [Architecture Decision Records](docs/adr/): recorded decisions and trade-offs.
- [Agent Guidelines](AGENTS.md): repository instructions for contributors and coding agents.
