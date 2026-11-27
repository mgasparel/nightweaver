# Durable Planning input storage

`PlanningInputFiles` preserves approved local inputs. `PlanningPublisher` and
`AgentStageCoordinator` use it through the local input handoff.
The [local input contract](../agents/planning-evidence.md#local-input-contract)
defines declarations and lifecycle authority.

`HostPaths.PlanningInputsRoot` selects
`<host>/evidence/<project>/planning-inputs/<repositoryIdentity>/sha256/<hash>/`.
This location is outside worktrees, invocation temporary directories and the
Target Repository. The host root and Target Repository must already exist.
The caller supplies the current physical repository identity from the contract.
Every request must match that Project and repository binding. The module refuses
physical aliases and reparse traversal. It does not delete or relocate evidence.
Normal completed-environment cleanup remains unchanged.

Each immutable object directory contains `bytes` and `manifest.json`. The manifest
uses `nightweaver-local-input-v1` and the contract fields. Size and hash describe
verified bytes. Source path, identity, revision and export command remain reported
facts. The module never executes a command or logs input bytes. It does not write
the Target Repository. One locator has one exact source record. A different source
record for the same bytes requires inspection; it cannot replace that record.

New directories have a protected Windows DACL at creation. Only the host account,
Local System and Builtin Administrators receive full control. Files inherit those
rules. Existing directories, source files, stored bytes, records and writer locks
must have an observable owner and DACL limited to these principals. The host account
must have an applicable allow entry. Broad inherited access, deny entries, unknown
owners and unobservable access rules cause failure. The module does not repair an
existing ACL. Delivery must use the same authorized host account. This protects
against other accounts; it does not isolate mutually hostile processes that use
the host account or authorized system administration accounts.

Preservation pins the namespace and takes an exclusive writer lease. It opens each
source with no write or delete sharing. It rejects device names, traversal, unsafe
Windows spellings, path aliases, reparse points and hard links. Streaming reads
enforce the 16 MiB input limit and 64 MiB candidate limit. Each declaration counts
toward the candidate limit, including declarations that reuse an object.

A new object uses a private sibling `<hash>.pending`. The module creates this
directory exclusively. It writes and flushes bytes, compares SHA-256, then writes
and flushes the source record. It verifies the complete staging object before one
directory rename without replacement. It verifies the final object again before
returning success. The rename exposes the bytes and manifest together. Interrupted
staging, conflicting objects and failed hash checks remain for inspection. A later
request refuses retained staging for the same hash. The maintainer must inspect
and reconcile it before an authorized Retry. No automatic deletion occurs.

An exact repeated request verifies the object and, when present, the source file.
A changed or inaccessible source causes failure. A missing source can reuse only
an already verified object with the exact recorded binding. `Verify` reads only
the durable object. It never falls back to the source path. A new store instance
can verify that object after normal Planning checkout removal.

The disposable Windows tests exercise real ACLs, stable read handles, two directory
junctions, source and object hard links, long paths and atomic publication. Existing
runtime file tests prove general handle safety but do not prove this store's DACL,
manifest binding or cleanup survival. Detailed refusals use small synthetic files.
The size-limit case shares one 16 MiB object. These tests add no factory, tracker,
Agent or browser journey and no fixture publication. The module adds no active
production work for plans without declared inputs. Retain measured costs with the
complete candidate evidence.

## Publication and Delivery handoff

Before the first graph mutation, the shared publisher preserves every declared
input. It checks fresh approval, ownership, Product version and grounding before
and after file IO. It writes a canonical `nightweaver-plan-input-handoff-v1`
Product comment through file transport and confirms the exact readback. The record
binds Product, candidate comment ID and hash, current Project and physical repository,
each input's source, locator, verified size and hash, and approved task entry IDs.
It contains no payload bytes. This schema is protocol evidence, excluded from
ordinary discussion grounding. Optional input receipts remain evidence only.

A lost response can reuse the confirmed exact record. Conflicting records stop
publication. Cancellation retains stored objects, comments and all task identities.
Restart reuses verified objects after the source is removed. It does not create a
replacement source record. The publisher confirms the handoff and verifies all
declared objects before each task creation, each hold release and the final parent
barrier. Existing guarded graph, approval, claim and pause checks still apply.

New graph snapshots include optional `localInputs` on tasks that use declared
inputs. Absent fields preserve the exact legacy snapshot format. Delivery reads
this structured field; prose cannot declare a dependency. A generated Delivery
attempt with a local input section adds one fresh complete inventory read. A task without structured inputs
needs no store or Product history read. A task with inputs reads Product comments,
checks the exact candidate hash and task content, handoff store, task use, source,
size, access and bytes. It appends only input IDs, verified absolute paths, expected
hashes and sizes to the dispatch. It never falls back to a Planning path.

Failures prevent Agent launch and name the Product, candidate, input, locator and
failed check. The maintainer must inspect and reconcile the evidence before an
authorized Retry. Ordinary completed-environment cleanup remains independent.
No published graph is retrofitted, including `ns-ptxz`.
