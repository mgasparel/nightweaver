# Recoverable paired upgrades v1

This is the contract for the ns-qa2 implementation graph, not an upgrade command.
`UpgradePolicy` is a pure decision worksheet; it neither establishes facts nor
persists progress, releases maintenance, launches processes or authorizes IO.
Public mutation remains withheld until rollback is implemented and composed.
Production release selection and each production operation require explicit
operator authorization. This contract installs no supervisor and starts no
collector, unattended run or automatic self-update.

## Internal explicit resume

`UpgradeCoordinator.ResumeAsync` accepts the exact observed operation revision.
It uses the existing operation, scope, manager digest, maintenance and native
boundaries. Public mutation remains unavailable until rollback is delivered.
Resume creates no Bead, cleanup record, supervision attempt or launch identity.

Prepared operations run preflight. Draining operations must prove original native
settlement; resume does not repeat an uncertain drain request. Settled operations
use the replacement primitive. Replacing operations verify candidate and retained
inventories, then reconcile each journaled file against actual length and hash.
Exact new bytes or confirmed removal advance evidence without another write.
Exact old bytes permit continuation of the same intent under the mutation locks.
Partial or unexpected bytes require inspection and stay intact. Before the first
file intent, the complete live old package supplies the backup inventory. Resume
checks existing backup files and copies only missing files. It never overwrites
an uncertain backup. Inaccessible files do not count as absent files.

Installed operations use activation checks. Verifying operations read the exact
durable activation grant and observe its reserved owners. They never replay a
launch, including when an owner is absent or native settlement is unknown. A
healthy matching replacement passes the same two fresh observations and final
native/health checks. Missing activation evidence, a partial maintenance release,
foreign owner, pending supervision reservation or changed intent retains the
interlock and reports the unresolved prerequisite. Resume does not infer that a
maintenance release belongs to this operation from a null token alone.

Accepted operations check live package, backup and fresh owner evidence. If the
matching release publication is missing, resume completes it under the existing
locks and exact intent checks. Repeated completion does not add journal revisions
or start owners. Historical acceptance never restores later running intent.
Interrupted journal/interlock siblings remain unavailable. Inspected and rollback
phases require their separate preparation or rollback path. No timer, startup
recovery or supervision tick invokes resume automatically.

## Internal progress storage

`UpgradeJournal` stores evidence at
`run/upgrade/operations/<32-hex-operation-id>/<8-digit-revision>.json`.
It derives storage paths from the caller's canonical root and operation ID.
`RetainedPackageRoot` identifies the separate
`run/upgrade/packages/<operation-id>` location; this store does not copy or
delete package bytes. The replacement adapter must retain and verify old bytes
there before it changes live payload. Neither location is replaceable payload.

Each immutable revision binds the root, SID, affected Project/configuration and
physical repository inventory, old/new package and manager digests, and backup
ID. It records exact phase, pending and last confirmed operation IDs, saved
maintenance tokens/generations/revisions, historical native PID/creation/run/
owner/runtime/Job identities, per-file intent/result hashes and bounded failure,
recovery and acceptance references. Configuration contents and process command
lines have no fields. Evidence references use operational IDs and fixed-format
codes; they are not paths to follow or proof that external evidence exists.

Read-only `Read()` creates nothing. Missing operation directories are distinct
from unavailable evidence. Empty existing directories, revision gaps, unknown or
duplicate properties, invalid enums, foreign bindings, inaccessible files and
interrupted siblings return unavailable. Writers hold a pinned exclusive lock,
compare the observed revision, flush a unique sibling, then rename without
overwrite. They retain interrupted siblings and all prior revisions. Old/new
package bindings and prior failure/owner evidence cannot be removed by a later
revision. A first-write interruption cannot be initialized over.

Metadata is limited to 1 MiB per revision, 4,096 file entries, 256 evidence
references and 4,096 revisions per operation. Budget exhaustion requires attention;
the store never evicts history to permit progress. Publication has a ten-second
cancellation budget. A synchronous native flush/rename cannot be forcibly
cancelled; it must settle before the caller can continue.

A lost reply is not a failed disk commit. A new manager must read durable state:
an interrupted sibling blocks progress, while a complete renamed revision can
still contain an unresolved operation. `SaveAsync` rereads before it reports
success. Both its return and persisted acceptance remain historical evidence.
Consumers must reconcile actual files/native owners and fresh intent under the
boundary locks before they act. This store enforces no phase transition and
grants no filesystem, launch, maintenance, cleanup or Beads authority. Public
mutating upgrade commands remain unavailable.

## Upgrade drain boundary

`UpgradeLifecycle` is the internal drain and mutation boundary for the later
replacement adapter. It accepts only a prepared journal revision and a fresh,
complete installation inventory. The production inventory adapter reads all
configured Projects, external configurations in the bound scope, runtime
directories and retained background owners. It checks configuration hashes,
physical Git identities and the current SID. Unknown consumers, unreadable
evidence and changed membership refuse the operation. A foreground runtime or
explicit dispatch holds a Project payload lease until native process exit,
including after the runtime method returns. This version refuses an active
foreground consumer because it has no background drain endpoint.

Standalone Web hosts check admission before listening and hold a shared
`run/<project>/web-payload.lock` lease until native process exit. Multiple Web
hosts can share this lease. The inventory refuses an active standalone Web host,
including when a background owner is also present, because standalone hosts have
no drain endpoint. Web admission uses a separate `owned-web-payload.lock` only
when the fresh root/Project owner record and held owner lock match, the original
owner is natively alive, and the current Web process belongs to that exact named
owner Job. An owner record alone never exempts a standalone Web consumer. The
self-admission probe opens a query-only Job handle for one synchronous membership
call and closes it before startup continues. It retains no Job handle across
listening, awaits or process lifetime. External settlement observers still never
open a Job while its owner lives. The original owner's native process and named
Job descendant settlement remain required, including for the contained Web child.
A Web host cannot start across a reserved or active mutation boundary.

Fresh drain, settlement, mutation and release checks refuse any unresolved
supervision `PendingAttempt`, even when the previous owner is settled or absent.
They preserve its reservation, attempt count and incident history. Maintenance
ownership alone cannot prove that a detached reserved launch has settled.

The caller must independently prove that the source implements the upgrade
boundary. An inventory capability string does not supply this proof. Legacy
sources remain unsupported. Public preflight still refuses mutation until the
replacement, recovery and cross-version compatibility adapters are delivered.

The manager takes `run/upgrade/manager.lock`, each affected `start.lock` in
ordinal Project order, then `run/upgrade/admission.lock`. Foreground startup and
explicit dispatch first acquire the ordinary physical-repository mutex, before
creating runtime files or directories. They then take admission serialization
through their interlock check and payload lease acquisition. They release
admission serialization before runtime work. Background start/restart and finite
ticks check the interlock under `start.lock`; restart checks again after drain.
The upgrade manager never acquires or substitutes for repository exclusion.

Reservation publishes an immutable root-bound record at
`run/upgrade/interlocks/<operation-id>/reservation.json`. Its digest binds the
complete journal scope. The manager reserves before maintenance or drain. It
enters exact-token maintenance only for existing supervision stores. Manual
installations acquire the same root interlock without creating supervision.
Enabled/desired state, attempts and incident history remain intact. The manager
records maintenance tokens, generations, revisions and original native identities
in a new journal revision. It releases command, configuration and admission
locks before sending drain requests. No wait or total force deadline is added.
Existing role drain and successor/retry suppression remain authoritative.

The internal `upgrade` owner-control request requires the original run ID,
current root operation and exact installed maintenance token. It preserves
desired-running intent and uses the existing Maintenance graceful-stop path.
An uncertain reply retains the operation, interlock and journal. Stop remains
available and can change durable intent during drain.

The drain step reads the exact retained owner again before each request. It
skips the control pipe only when the owner lock is absent, the original native
process has exited and its Job descendants have settled. It retains that owner
identity in the operation. All other cases still require the drain request and
the separate settlement check before payload replacement.

A direct console stop takes the Project command lock and records the same
operation-bound stop evidence as a manager stop. It waits for sharing contention
before it cancels graceful runtime work. A second console signal keeps the
immediate force-exit path. Manual mode creates no supervision store. Intent
capture also excludes a manual owner already in an intentional drain. A console
stop after capture cancels activation and retains the operation, backup and
startup suppression, even if the upgrade pipe later accepts a drain request.

Settlement is an explicit observation, not expiration of the manager's stop
wait. Confirmation requires the original PID/creation/run/runtime identity,
native process exit and an empty named containment Job. PID reuse, missing
original records, foreign owners, active owner locks and inaccessible native
evidence refuse mutation. Before each mutation callback, the manager reacquires
command/admission locks, pins configuration files against writes, verifies their
hashes and the current journal revision under its writer lock, and repeats
inventory, maintenance and native settlement checks. A settled journal phase
alone grants no mutation authority. The callback must not launch an owner or
write the journal; the replacement adapter retains its per-file intent first.

Explicit reconciled release requires a settled, accepted or rolled-back
operation, fresh native settlement and the saved exact maintenance revisions.
It clears only owned maintenance and publishes matching immutable
`released.json` evidence. It starts nothing and never restores saved running
intent. A later stop/disable, changed generation or foreign maintenance token
prevents stale release and mutation. Missing, malformed, unknown or interrupted
interlock files suppress startup. Partial multi-Project maintenance/release and
lost publication replies retain all files for the later explicit recovery
adapter; this boundary never clears them automatically. Interlock history is
bounded at 4,096 operations and requires explicit inspection at that limit.

`UpgradeLifecycleTests` use real disposable command/store locks and injected
native settlement/control operations. They cover manual and supervised drain,
concurrent command exclusion, interrupted requests and files, stale revisions,
later stop/disable, configuration writes, foreground admission and root-wide
inventory refusal. They add no owner, Agent, Scheduler or browser journey.

## Installation and ownership

V1 replaces only inventoried package files **in place at the same canonical
local root**. A manager copied to a separately inventoried, immutable recovery
location outside the replaceable payload runs the operation. It must survive
replacement of both CLI and Web. It uses the paired publisher's prepared output;
the publisher's fresh-destination rule is unchanged. Directory swapping, copying
operator state to a new root, rewriting Git registrations and manual directory
choreography are not supported upgrade paths.

One durable 32-hex operation ID binds root, current user SID, physical repository
common directories, affected Project/configuration identities, old/new release
inventories, manager capability, and the original native owners. Inventory hashes
are SHA256 over exact relative names, lengths and bytes. Old and new inventories
are immutable and retained outside live payload; paths are derived from trusted
roots, not followed from journal strings. Reparse/device/network/alternate-stream
aliases, hard-linked payloads, path collisions and ambiguous ownership refuse.
Pinned no-reparse handles and the existing safe runtime publication primitives
must be used by later persistence/replacement code. Progress is evidence, never
filesystem authority.

The affected set is root-wide: enumerate every configured Project, external
configuration used by any owner, runtime directory, manual foreground/background
owner and supervision installation sharing CLI/Web bytes. Bind config hashes,
Project slugs, physical Git identities and original PID/creation/run/runtime/Job
identities. Unknown owners, inaccessible inventory, missing configs, changed
membership and incompatible external configuration refuse before drain. A second
Project may not continue using the shared payload. Refuse unsupported alternate
payload consumers rather than silently ignoring them. Recheck inventory at every
mutation boundary; external edits invalidate preflight.

The future root upgrade lock serializes upgrade managers. All affected existing
`start.lock` locks are acquired in canonical order for interlock publication and
native mutation boundaries. A durable root upgrade interlock must be honored by
manual start/restart, foreground startup and finite supervision ticks, including
when supervision storage is absent. Stop remains usable. Do not hold command
locks while waiting for Agents to drain; reacquire and compare identity/generation
before proceeding. The original owners retain physical-repository process-lifetime
exclusion until exit; replacement owners acquire that existing exclusion normally.
The manager never holds repository exclusion while waiting for a child to acquire
it. The interlock is not a replacement for repository or native owner exclusion.

## Preservation and authority inventory

| Resource | Required rule through success, interruption and rollback |
| --- | --- |
| Root, Project/config paths, physical Git common directories and SID | Keep identities fixed; aliases, relocation or changed bindings refuse. External configurations are read and fingerprinted, never rewritten. |
| `projects/` and configuration/secrets outside the root | Preserve exact bytes, ACLs and location; no schema migration or config-content evidence dump. |
| `worktrees/<project>/`, Git common-directory registrations and branches | Preserve exact canonical paths, current HEAD/index, staged/unstaged/untracked/ignored content, registrations and locks. No sweep, prune, reset, rebase or branch deletion. |
| `tmp/<project>/`, `logs/<project>/`, `telemetry/<project>/` | Preserve all content including retained invocation files, catch-up state and interrupted writes. Old telemetry is historical, never proof of a new runtime. |
| `run/<project>/cleanup/` | Preserve root/repository-bound obligations, revisions, retries, failures and progress. Only ordinary cleanup revalidates current authority; upgrade/rollback never rebinds records or turns old-root records into deletion permission. |
| `run/<project>/supervision/` and capture bundles | Preserve enabled/desired intent, generation, exact maintenance token/revision, pending reservation, exhaustion, due time, incidents and history. Missing is manual; malformed, interrupted, inaccessible or uncertain-stop state is unavailable, never opt-out. No reset or expiry. |
| `run/<project>/background/` owner/identity/control records, factory runtime files | Retain old identity evidence in place or in an allowlisted historical snapshot before ordinary startup publication. Never restore old owner files as current identity, delete evidence to manufacture absence or infer authority from a PID alone. New owner/runtime files come only from ordinary startup. |
| Native owner locks, named Jobs, command/writer locks | Do not copy, delete or recreate them to claim settlement. Confirm original process signal and original Job descendant accounting; PID reuse, lock release or missing pipe alone is insufficient. |
| Root upgrade journal, interlock, recovery manager and inventories | Retain operation/revision, attempts, interrupted sibling files and per-file progress through failures. Unknown or partial publication blocks mutation until reconciled; no automatic initialization over evidence. |
| CLI/Web executables, dependencies, prompts, Studio assets, deployment manifest | Replace only exact allowlisted old/new package union; unknown extras are preserved and collisions refuse. Retain complete old bytes before the first write. Manifest publication alone is not a multi-file transaction. |
| Other files/directories, existing Scheduler tasks and Beads | Preserve unchanged. No task registration, workflow transition, claim release, pause change, repair reset or notifications. Ordinary owner startup recovery retains its existing guarded authority. |

## Compatibility before drain

The legacy deployment manifest v1 provides only schema/build/CLI/Web paths. It
does not prove integrity, state compatibility, upgrade capability or a live pair.
Preflight derives a complete inventory from the prepared paired release and
requires explicit capability evidence for **both** source and target; a Git SHA
or a version ordering is insufficient. The later release validator owns parsing
and reports the following specific safe refusal before any drain:

| Check | Supported v1 input / refusal detail |
| --- | --- |
| Package pair | Manifest v1, exact same build for CLI/Web, complete prompts, dependencies, Studio entry and local JS/CSS; reject `pair-mismatch`, `inventory-invalid` or `payload-collision`. Do not restore retired legacy HTML validation. |
| Host | Windows/current user, architecture and required .NET runtimes for both packages and external manager proven available; otherwise `runtime-unsupported`. No runtime download/install fallback. |
| Manager/source lifecycle | Source and target advertise root interlock support in foreground/background/tick launch paths, exact-token/revision maintenance when supervised, identity-bound observation, native owner/descendant settlement and shared command serialization; manager advertises this contract version and journal recovery. Otherwise `capability-unsupported`. Delivered supervision alone does not establish the future root interlock. |
| Configuration | Every affected config is readable by old and new readers at the same identity, with required settings understood. Unknown schema/unsupported external config refuses `configuration-unsupported`; no rewriting to make it pass. |
| Durable state | Old and new explicitly support current cleanup, supervision, runtime/telemetry/catch-up and upgrade journal versions. Strict readers must reject unknown/malformed/interrupted records; `state-unavailable` or `state-unsupported` refuses. |
| Rollback | Old binaries can read every state format the candidate may write during verification and ordinary resumed work. V1 forbids irreversible state migration; no state snapshot restore. Missing proof refuses `rollback-incompatible`. |
| Ownership/exclusion | Complete affected inventory, original identities, no unresolved recovery launch, root/command lock support and no foreign active owner. Otherwise `identity-unknown`, `reservation-pending` or `exclusion-unavailable`. |

Legacy releases lacking any required capability support read-only inspection
only, even if stopped. An operator may use a separately authorized future
bootstrap procedure, but undocumented file copying is never successful upgrade.
The contract therefore does not claim that today's deployed package can already
be upgraded by a future manager.

## Commands, phases and evidence

`inspect` is read-only and returns package/identity/phase/failure evidence without
creating state. `preflight` validates and proposes Prepared; any persisted result
is revision guarded and still requires fresh revalidation before mutation.
`apply` creates one operation, durably suppresses root-wide launches and performs
the ordered boundaries below. `status` is read-only and distinguishes pending,
refused, failed, verified and accepted. `resume <operation>` reconciles actual
bytes, native identities, journal/interlock and intent before proposing the next
boundary; it never replays an uncertain mutation. `rollback <operation>` restores
package bytes only. `accept <operation>` commits explicit acceptance of freshly
verified evidence; it is separate from launch or maintenance leave.

| From -> to / worksheet step | Evidence required in addition to fresh identity, compatible inputs, reconciled journal and held boundary locks |
| --- | --- |
| Inspected -> Prepared / Preflight | Immutable inventories, old-state readability and target/manager capability proof; no drain. |
| Prepared -> Draining / BeginDrain | Root interlock durably confirmed; every supervised Project's exact token/revision maintenance confirmed without changing desired/enabled intent; manual Projects use root interlock only. Persist original owner identities and drain request intent before requesting existing graceful maintenance control. |
| Draining -> Settled / ConfirmSettlement | All original owners and their original Jobs natively settled, no replacement/unknown owner, all pending supervision reservations reconciled. A two-minute manager wait reports pending only. |
| Settled -> Replacing / BeginReplacement | Still suppressed and settled; complete old package verified and durably retained, new inventory fixed, per-file write intent persisted before any package mutation. |
| Replacing -> Installed / CommitPayload | Each live file matches new inventory, removed old-only files have recorded disposition, unknown files preserved; exact full pair verified and commit marker durable. |
| Installed -> Verifying / BeginVerification | Old bytes retained, new bytes exact, current intent/generation inspected. Stopped, disabled or maintenance intent selects stopped verification. Running verification requires separate explicit start authorization plus fresh desired/enabled intent, exact maintenance leave and root-interlock handoff under command locks. |
| Verifying -> Verifying / Verify | Fresh running or stopped acceptance below; verification expiry or a new intent change invalidates the proof. No implicit launch/retry. |
| Verifying -> Accepted / Accept | Revalidate the same fresh proof and exact installed pair; atomically persist acceptance before releasing this operation's remaining root interlock. Release starts nothing. |
| Replacing/Installed/Verifying -> RollingBack / BeginRollback | Reestablish root/maintenance suppression, gracefully drain any candidate owner and confirm its original native Job settlement too. Exact old bytes available, current forward state readable by old package. |
| RollingBack -> RolledBack / CommitRollback | Original and candidate owners and their native descendants still confirmed settled/absent under suppression; restored old live inventory exact, current operator state preserved, journal commit durable. RolledBack means bytes restored, **not** running/healthy; separate explicit start and the same fresh acceptance checks prove usability. |

`UpgradeFacts.AllOriginalOwnersAndJobsSettled` covers the pre-replacement owners;
`UpgradeFacts.AllCandidateOwnersAndJobsSettled` independently covers every
candidate owner and its original native Job descendants. Both BeginRollback and
CommitRollback require this fact as well as original-owner settlement. True means
confirmed native settlement of every candidate, or reconciled launch/reservation
evidence proving none was launched; live, draining, unknown or lost-reply state
must supply false and returns `NativeSettlementPending` without advancing.
A permitted live verification owner does not require candidate settlement during
BeginVerification/Verify/Accept and instead must match fresh running acceptance.
Neither settlement fact means merely absence of a PID.
`SuppressionConfirmed` means either full durable root/maintenance suppression or,
during running verification only, the exact controlled handoff below; it must
never be supplied merely because a maintenance command returned exit zero.
`EvidenceReconciled` excludes unresolved writes, launch replies and pending native
reservations. Boolean facts are trusted adapter inputs, not serialized permits.

Running verification cannot use ordinary `start` while maintenance is active.
Under the affected command locks, compare latest generations and operation tokens,
leave only this operation's maintenance using the fresh revision, and temporarily
permit only the recorded verification start through the root interlock. Persist
its reserved run identity before launch; source ticks and other manual starts
remain excluded. New owner acquires repository exclusion normally. A stop/disable
during handoff wins and prevents launch; a lost reply retains the reservation for
reconciliation, never another start. Future adapters must implement this without
bypassing existing maintenance checks. Manager crash leaves only that recorded
owner permitted; status/resume reconciles it before any action.

## Fresh acceptance and stopped intent

Running acceptance requires two matching Healthy observations at least ten
seconds apart within a 60-second verification window. Each uses the delivered
observer's <=10-second freshness, <=1-second future skew, native PID/creation,
run/owner/runtime, held owner lock, current-user control, Project/config/root and
physical Git common directory, actual Web health and factory runtime identity.
Additionally prove exact **live** CLI and Web release identities match the new
inventory, with installed file hashes rechecked. `/health`, a manifest, an old
factory file or transient readiness alone is insufficient. Release identity
attestation is a future adapter requirement, not something existing health proves.

Stopped acceptance requires the same exact installed pair and compatibility,
fresh root-wide original native settlement/absence, current stopped/disabled or
maintenance intent and retained matching suppression evidence. It is explicitly
reported as **accepted stopped**, never healthy/running. Manual missing-store
installations retain separately journaled operator intent; adapters must not
fabricate enabled supervision. `Enabled` in the worksheet means current launch
permission (true for manual operation), not a request to initialize a store.
Later stop/disable and foreign maintenance always win over saved initial intent.
Accept, resume, rollback and maintenance leave never write desired-running or
refund supervision attempts/Delivery repair budgets. No token expires by time.

V1 routine bounds are design choices: 10 seconds for a probe (reuse delivered
observer), 60 seconds for fresh acceptance, 10 seconds per journal publication,
and at most 4,096 package files / 2 GiB total per release / 1 MiB journal metadata
with bounded per-file entries. Preflight rejects oversize input before drain and
checks free space for both retained packages plus staging with 10% margin.
These contain local inspection/publication work; they never extend existing
startup deadlines or impose a total graceful-drain force deadline. Cancellation
and timeout mean pending/unknown until native and durable settlement is proven.

## Interruption and failure disposition

Before every irreversible mutation persist its intent and exact inputs; after it,
confirm actual state before recording completion. Publication is atomic, flushed,
revision guarded and strict-schema loaded. Unknown fields/versions, partial sibling
files, changed operation IDs and missing evidence are explicit attention, not
permission to initialize. Failures report fixed code, phase, operation/revision,
affected allowlisted path, expected/observed hash or identity, native settlement,
and permitted next actions; no unrestricted command line/environment/config dump.

| Power loss, process death, canceled command or failed acknowledgement at boundary | Resume / rollback disposition |
| --- | --- |
| Inspection/preflight before operation commit | No mutation authorized. Inspect/revalidate; discard no existing foreign evidence. |
| Operation/interlock or per-Project maintenance publication | Reread exact operation and all tokens/revisions; partial suppression cannot drain or replace. Resume completes only this operation's provable suppression. Unknown/foreign token retains evidence for inspection. |
| Drain request/reply or owner exit | Reobserve original PID/creation/run/Job. Live/draining/uncertain means wait; no replacement or rollback. No implicit force stop; an explicit separate force operation still requires capture-first evidence and subsequent native settlement. |
| Old package retention or first replacement intent | Verify retained bytes before any live write. Missing/corrupt backup blocks mutation. If no write occurred, a guarded cancellation may release only owned interlocks, starting nothing. |
| Any individual payload write/delete or pair commit | Classify each file as old/new/recorded intermediate under locks. Known partial replacement resumes from verified per-file intent, or rolls back from verified old bytes. Unknown bytes, extra collision or lost journal leaves both paths blocked for inspection. Never launch a mixed pair. |
| Start reservation, launch, readiness or response | Reconcile exact reserved owner and native descendants; no replay launch. Failed acceptance retains old package/interlocks. Rollback first suppresses/drains candidate and proves settlement. Unknown native state blocks both replacement and rollback. |
| Verification or acceptance publication | Reread journal and fresh observations. Verified-but-unaccepted retains backup; expired health requires new probes, not new launch. Unknown accept publication is not success. A confirmed accepted operation is terminal: no automatic rollback, purge or reapply. A later upgrade is a new operation. |
| Rollback write or commit | Reconcile exact old/new per-file progress, then resume restoration only with old-readable current state and no active owner. Never restore config/worktrees/tracker/cleanup/supervision snapshots. Unsupported newer state blocks rollback and retains inspection evidence. |
| Maintenance/root-interlock release | Reread exact token/revision/current intent. Already released is reconciled as fact; foreign/newer operation refuses. Release never starts, enables or resets anything. Preserve later stop/disable. |

Map fixed worksheet refusals to these diagnostics: `InvalidTransition` (inspect
phase), `IdentityUnknown` (reinventory), `CompatibilityUnsupported` (specific
preflight reason above), `InterlockUnavailable` (no mutation), `EvidenceUncertain`
(inspect retained journal), `NativeSettlementPending` (wait/observe),
`PayloadMismatch` (inspect exact file), `IntentChanged` (fresh operator decision),
`AcceptanceUnproven` (probe without relaunch) and `RollbackIncompatible` (retain
newer state and both packages). Refusal leaves phase and all evidence unchanged.
There is no automatic file repair or repeated launch loop. Old bytes and recovery
evidence remain through acceptance; v1 defines no automatic post-accept deletion.

## Verification and delivery seams

### Read-only release preflight

The paired publisher now writes `nightweaver-release.json` alongside the unchanged
deployment manifest v1. Inventory schema 1 has exactly `schema_version`,
`build_version`, `capabilities` and `files`; each file has exactly `path`, `length`
and `sha256`. Paths use `/` and retain exact spelling; collisions compare without
case. The SHA256 of the inventory's exact UTF-8 bytes is the package digest.
The inventory includes the deployment manifest and every other payload file,
excluding itself. CLI and Web publish with the same pinned informational build
identity; inspection checks all three shipping CLI/Web assembly identities and
the identical CLI library included in both outputs. This is local integrity and
pair evidence, not signed provenance or permission to mutate an installation.

```text
nightweaver upgrade preflight --installation H:\Nightweaver\installed --candidate H:\Releases\prepared
```

Repeat `--config <absolute-path>` for known external configurations. All root
`projects/*.json` are included automatically; duplicate Project identities and
unsafe/overlapping roots refuse. Read-only Git queries resolve each physical
common-directory identity using the same identity function as owner exclusion.
The bounded JSON report includes canonical installation/candidate/config/repository
paths, config hashes, old/new inventory digests when verified, affected Projects,
compatibility, rollback eligibility and fixed refusal codes. It emits no config
contents, tool output, owner commands or credentials. Exit 1 means refused,
exit 2 means invalid command syntax; `upgrade --help` displays usage.

Current releases advertise only `paired-inventory-v1`. Preflight deliberately
reports `compatible: false` and `rollback_eligible: false`: the root interlock,
external recovery manager, complete root-wide owner census and cross-version
durable-state readers have not shipped. Arbitrary capability strings cannot
override this implementation boundary. It reports `capability-unsupported`,
`state-unsupported`, `identity-unknown` and `rollback-incompatible` after otherwise
successful inventory. Missing/malformed packages/configurations and missing
runtimes add bounded earlier refusals. Legacy manifests without the inventory
remain inspection-only with an unavailable package digest. No historical SHA is
treated as capability proof. This command does not produce Prepared authority.

Inspection holds no-follow directory/file handles, rejects hard-linked payloads,
reparse points, device/stream/short-path aliases, traversal, duplicate/unknown JSON
members, unknown schemas and builds, missing/changed/mixed files, state paths,
oversize inputs and unlisted candidate files. Installed operator state and unknown
extras remain untouched. No staging is created while compatibility is refused;
future Prepared persistence must retain immutable verified bytes and revalidate
them under the mutation locks specified above. No journal, runtime or supervision
store is initialized and no owner is stopped or launched by preflight.

`PreparedReleaseTests` use small disposable trees plus copies of the already-built
assemblies. They cover valid integrity, tamper/deletion/mixed builds, strict schema,
state overlap, Studio escape, bounded command refusals and unchanged sentinels.
One junction/hard-link fixture proves the no-follow/link-count boundary with one
short `mklink /J` helper; it adds no owner, Agent, publish, supervision or browser
journey. Publisher fixtures reuse their injected paired publish boundary.

`UpgradePolicyTests` cover all allowed edges, every other phase/step pair,
missing identity/compatibility/interlock/journal evidence, pending native drain,
payload mismatch, forward-state rollback refusal, fresh health and changed or
stopped/disabled/maintenance intent. They use no files, clocks or child processes.
No native coverage, fixture publish, browser journey or runtime work is added.
Later tasks own strict release validation, journal/interlock persistence, drain,
replacement, activation, resume, rollback and representative isolated native
proof/runbook. Their adapters must establish these facts under real exclusion;
passing this worksheet alone cannot prove their behavior. Reuse existing
[supervision](supervision-v1.md), [unattended acceptance](../acceptance/unattended-self-build.md)
and [archive](../acceptance/unattended-evidence-archive.md) contracts.

## Internal package replacement

`UpgradeReplacement` is an internal one-shot component. Only the later composed
manager can expose mutation after recovery and rollback exist. It requires a
fresh settled journal revision, the bound old/new inventories, and an external
manager whose actual assembly hash matches the journal. It starts no process,
releases no maintenance, and never changes a canonical root or Git registration.

The component retains the complete old package, including retired files and the
exact inventory, at `RetainedPackageRoot`. It flushes and verifies every backup
before the first live write. Candidate and backup read handles reject reparse
points and hard links and deny writes and deletion for the operation lifetime.
Existing backup content refuses a fresh attempt. Interrupted backup files remain
for recovery inspection; the component never initializes over them.

Only the validated inventory union and `nightweaver-release.json` can change.
Unknown extras remain in place. New-name collisions and file/directory changes
refuse. Unchanged file hashes avoid redundant writes. Each changed file has a
persisted pending intent before the lifecycle boundary repeats configuration,
maintenance, inventory and native settlement checks. Existing files open with
exclusive read/write/delete access and no-follow/single-link validation. Writes
use that same handle; retirement marks that same handle for deletion. Fresh
files use create-new under pinned parents. The inventory changes last.

Live writes are deliberately not a multi-file transaction. Interrupted writes,
deletions or uncertain confirmations retain `Replacing` and pending evidence;
readers must treat pending files as unknown until reconciliation. A confirmed
per-file result is historical evidence. Only a final complete payload check
permits `Installed`, which grants no acceptance or startup authority. The
component does not retry, release suppression, restore owner records or remove
retained evidence. It reserves enough journal revisions before it changes files;
budget exhaustion refuses. Recovery and rollback remain later work.

## Internal activation and live release evidence

`UpgradeCoordinator` composes a fresh compatible preflight report, prepared
journal, drain, native settlement, replacement and activation. Its production
factory uses the existing inventory, ordinary background startup and shared
deployment observer. The external manager supplies independently proven
compatibility; untrusted inventory capabilities cannot enable it. Public
preflight still refuses mutation until recovery and rollback are delivered.
An unsettled drain returns pending. The coordinator never repeats a launch or
an uncertain mutation. Cancellation retains the operation for explicit recovery.

Activation persists `activation.json` under the current root interlock before
launch. It binds the operation, new package digest and exact Project/run pairs.
The manager compares configuration, journal, maintenance generation/token/revision,
old native settlement and both payloads under the existing command/admission
locks. It leaves only its own maintenance and keeps desired intent unchanged.
Ordinary starts and ticks remain suppressed. Only the reserved owner process
and its contained Web child can obtain payload leases. Missing, malformed,
foreign or interrupted activation evidence refuses startup, even beside a
historical release record. A failed launch retains the grant, pending intent,
backup and root interlock; the one-shot coordinator cannot replay it.

Command locks cover the final launch boundary and are released for health
observation. Stop remains usable. Final publication reacquires all affected
command locks and admission, then rechecks intent, inventory, native identity
and installed hashes. The final owner inventory must still report running state,
healthy Web and live runtime, with no failure or stop reason. A recorded failure
rejects acceptance even while the exact native owner remains alive and holds its
lock. It retains the pending operation, backup and root interlock.
Running acceptance requires the same CLI/Web pair in two
fresh Healthy observations at least ten seconds apart within sixty seconds.
The journal retains actual PID/creation/run/runtime/Job identities and each
pair's first and final observation times. Stopped acceptance verifies payload
and settlement without launching or inventing supervision state.

Manager stop and force stop record `stopped-<Project>.json` in each active root
interlock under the Project command lock and shared admission lock, before querying
the owner. The record binds the exact reservation. A repeated stop is idempotent.
This records a later manual stop even after the old owner has settled, without
creating supervision state. Activation checks affected Project stop records before
grant publication, each launch and final acceptance. A stop for another Project
does not cancel activation. Malformed, foreign or interrupted records retain
suppression. No automatic path clears these records or restores saved start intent.

CLI and Web capture startup release evidence once from a validated installed
inventory. Loaded assembly location, informational build and module identity
must agree with the package. Web `/health` returns that captured identity with
`Cache-Control: no-store`. The owner compares its HTTP response with the exact
launched Web PID and creation time. Its fresh control response carries both
release identities alongside the existing runtime health. Legacy/dev installs
without release inventories report no attestation and cannot establish upgrade
acceptance. A later disk manifest cannot change a captured live identity.
Representative native cross-version confirmation remains in `upgrade-proof`.
