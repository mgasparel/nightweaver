# Planning evidence

The outcome classifier receives Product title, description, design, acceptance criteria and notes, plus the latest candidate verbatim and relevant discussion. Expanded tracker relationships and metadata stay out of this payload. If the discussion exceeds 128 KiB of serialized context, Nightweaver preserves structured human guidance and fits up to 12 recent comments with older sourced excerpts where space allows. Grounding and approval still use the complete tracker evidence. If the required fields, candidate and guidance exceed the budget, Nightweaver records a `needs-human` pause, releases the claim and preserves the stage, repair count and candidate. After correcting the input, choose **Reset Budget & Retry** in Attention Desk. The diagnostic does not change Product grounding.

Planning uses complete raw JSON comments on the Product. These are evidence, not workflow commands. The host captures a `grounding` object with `checkoutRevision`, `defaultRevision` and `productInputs` (SHA-256 of Product title, description, design, acceptance criteria, notes and ordinary discussion). Protocol comments and workflow fields are excluded from those discussion inputs. The dispatch appends the exact object for the role to copy. Inputs must remain unchanged during a turn. The default revision is the local configured default branch; `observedRemoteRevision` also records the available origin tracking ref (null if absent). The isolated Planning checkout must be observable and clean. No implicit fetch or reset occurs.

`nightweaver-plan-v1` contains `grounding`, `tasks`, `constraints`, `coverage`. Tasks contain `id`, `title`, `outcome`, `context`, `approach`, `acceptanceCriteria` (array), `verification`, `dependsOn` (IDs). Constraints contain `requirement`, `source`. Coverage entries contain `requirement`, `taskIds`, `satisfiedEvidence`, `authorizedExclusion`; use null for unused alternatives. Task IDs are unique and dependencies must form a known acyclic graph. Acceptance criteria describe observable behavior; concrete implementation instructions belong in approach, and hard constraints require authoritative sources.

## Local input contract

A candidate can declare `localInputs`. Each task can declare `localInputIds`.
Absent, null and empty arrays mean no local inputs. Old candidates retain their
parse, approval and task-description behavior. No dependency is inferred from prose.
The Planner must declare every local file needed by Delivery. The Challenger must
check the declarations, source claims and task use against the Product requirements.
Use synthetic data in tests. Never put raw exports or input bytes in Git or comments.

Each input has this structure:

```json
{
  "id": "tracker-export",
  "sourcePath": "evidence/export.json",
  "source": {
    "identity": "synthetic tracker export",
    "revision": "reported tracker revision",
    "exportCommand": "bd export"
  },
  "expectedSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "store": {
    "project": "demo",
    "repositoryIdentity": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  },
  "locator": "sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}
```

`id` is unique within the candidate. It starts with a lowercase ASCII letter.
It contains only lowercase ASCII letters, digits and `-`, with at most 64 characters.
Each declared input must be used by at least one task. Task references are unique,
case-sensitive IDs from this list. The plan and each task can name at most 64 inputs.
The existing 64 KiB UTF-8 candidate parse limit still applies.

`sourcePath` records the original file location. A relative path is relative to the
Planning checkout. An absolute path records a location outside that checkout.
It is not a store path or a Delivery fallback. It has at most 4096 characters.
`source.identity` describes the reported origin, such as a tracker or dataset.
It has at most 1024 characters. Optional `revision` and `exportCommand` record
reported source facts, with limits of 1024 and 4096 characters. Supplied text must
be nonblank and contain no control characters. Commands are descriptions and must
never be executed by input resolution. A reported revision is not a verified
tracker revision. The host must not assert a source revision it did not verify.

`expectedSha256` is exactly 64 lowercase hexadecimal characters. It names the
expected bytes. Validation does not read files or prove this hash. The store
binding contains the configured Project slug (nonblank, at most 128 characters,
no control characters) and `repositoryIdentity`. The latter is the lowercase
SHA-256 of the UTF-8 physical Git common-directory identity returned by
`ProcessLifetimeTargetRepositoryLock.ResolveIdentity`. It binds aliases and
worktrees to the same physical repository. Future host checks must compare both
values to the current Project and repository; pure candidate validation checks
their format only. The store namespace is this pair. Project names alone do not
identify a repository.

`locator` is exactly `sha256/` followed by `expectedSha256`. No absolute path,
backslash, URL, encoding, traversal, suffix or other locator form is allowed.
The locator is logical and does not contain an invocation directory. The host
chooses the physical store root outside the Target Repository, worktrees and
invocation temporary directories. A locator grants no access outside its namespace.

### Storage and handoff requirements

This change defines the contract. It does not activate storage, file reads,
publication barriers or Delivery resolution. The dependent storage and integration
tasks must implement the following requirements before inputs are operational:

- Before publication, read each declared regular file under restricted access.
  Reject links, reparse traversal and unstable or unreadable files. Limit each
  input to 16 MiB and the candidate's input bytes to 64 MiB. Verify the bytes
  against `expectedSha256`. Retain the source record as reported facts.
- Store immutable bytes and a manifest in the bound namespace. Restrict access
  to the host account and its authorized Delivery processes. Write to a temporary
  sibling, verify, then publish atomically without overwriting conflicting bytes.
  On restart, reuse only a complete manifest and bytes that pass all identity,
  size and hash checks. Preserve conflicting or partial evidence for inspection.
- The verified manifest records `schema: nightweaver-local-input-v1`, `store`,
  `locator`, `sha256` and `sizeBytes`. These are verified byte and store facts.
  The manifest also retains `sourcePath` and `source` as reported facts.
- Before removing publication holds, retain a handoff record with
  `schema: nightweaver-plan-input-handoff-v1`, `product`, `planCommentId`,
  `planRevision` (the exact candidate comment hash), `store` and `inputs`.
  Each input entry records `id`, `locator`, `sha256`, `sizeBytes`, `sourcePath`,
  `source` and the approved task entry IDs that use it. Group records by store
  binding if a candidate names more than one binding; host verification must
  reject any binding foreign to the current Project and repository.
- A `nightweaver-plan-input-receipt-v1` comment may cite the handoff identity and
  verified input IDs. It is protocol evidence and is excluded from Product
  discussion grounding. It is not a candidate, review, approval, release or Resume
  command. Missing or malformed receipts cannot prove byte availability. Receipts
  must not carry raw bytes. The host must verify the retained record and bytes.
- Delivery resolves only the declared task IDs through the retained handoff and
  current bound store. It verifies Product, exact candidate revision, task use,
  locator, size, restricted access and SHA-256 before use. It must still resolve
  after normal Planning cleanup. It must not fall back to `sourcePath`. A missing,
  foreign or corrupt input stops publication or Delivery with the Product, candidate
  revision, input ID, locator and failed check. The maintainer must inspect and
  reconcile the evidence before an authorized Retry. Keep raw bytes out of diagnostics.

Task materialization appends `## Local inputs` only for a task with input references.
Its JSON array contains the exact declared input records in `localInputIds` order.
It includes no runtime path or receipt. Resolution must not edit the approved
candidate or published task text. Normal exact candidate approval, creation intents,
immutable graph checks, publication holds and the final parent barrier still apply.
Receipts do not grant lifecycle authority or permit graph replacement. Cleanup
retains the external store and logs without broader pruning or branch authority.
This contract concerns prevention. It does not replace or resume the `ns-ptxz` graph.

## Approval and publication

`nightweaver-plan-review-v1` contains current `grounding`, `planCommentId`, `planRevision` (SHA-256 of the exact persisted candidate comment's UTF-8 text), `assessment`, `revalidation`, `findings`, `questions`. Each finding contains `violation`, `evidence`, `direction`. Approval requires no unresolved findings/questions. Changed grounding requires targeted revalidation, documenting affected portions and preserved unaffected work. Semantic checks (full Product coverage, authoritative sources, executable context, implementation assumptions) use the role-specific classifier; structural and identity checks are deterministic. Classification never grants authority by itself.

`nightweaver-plan-question-v1` contains current `grounding` and nonempty `questions`, each with `question`, `recommendedAnswer`, `tradeoffs`. A material product gap pauses; routine omissions receive bounded repair. Findings about disproven assumptions require revision of the affected plan.

The host persists `nightweaver-planning-outcome-v1` with invocation evidence. Challenger approval includes `planning_approval`: exact candidate ID/hash, review ID/hash and grounding (snake_case fields in the host outcome comment). A consumer must obtain fresh grounding through `PlanningEvidence.CaptureAsync` and use persisted comments to check `PlanningEvidence.ApprovalStillCurrent`; `stage:plan-approved` alone cannot establish an approved graph. Capture refuses a dirty or unobservable isolated Planning checkout, so a failed capture cannot establish current approval. Root-checkout edits do not block capture. Preserve all edits for inspection and revalidation. Later candidate additions or edits invalidate the approved identity. A host evidence comment written before an interrupted stage update does not authorize publication; check workflow and claim as well.

Each publication authority check obtains one fresh `PlanningEvidence.ObserveAsync` observation: independent read-only Git grounding queries overlap and all settle first, followed by one native `bd --readonly --sandbox show <product> --include-comments --json` read of the current Product and complete discussion. Omitted or foreign comment bodies fail the observation; a missing comments array is accepted only with an explicit zero count. Grounding, workflow/ownership and approval checks consume those same observed inputs without duplicate tracker queries or reuse across mutation boundaries. Git and tracker reads do not provide an atomic cross-source revision; guarded writes and post-write verification still apply.

Publication places grounding checks at mutation boundaries. Read-only graph verification does not start another grounding sweep: the next create, missing dependency, retained-membership update, hold removal or final parent update obtains fresh authority before writing. For a new two-task graph with one blocking dependency this bounds grounding to fifteen sweeps, including creation intents, binding confirmation and fresh pre-launch authority. A pause or ownership change during graph reads still blocks the next mutation. The membership/receipt authority check follows the retained-parent read, including resume when membership metadata is already present. The release receipt remains protocol evidence and cannot admit generated work without the final parent barrier.

Approved-stage dispatch uses `PlanningPublisher`, with no new Agent turn. Beads allocates each new task ID through ordinary creation without `--id`. Initial creation includes `plan-unpublished`, native `discovered-from`, and `publication_product`, `publication_revision`, `publication_entry` metadata. The host resolves each entry from the complete inventory by this exact provenance tuple. It checks all matches, retained creation bindings, graph membership, release maps and known legacy IDs before adoption. A duplicate without a native link is still a conflict. A known legacy ID with damaged provenance is a conflict, not an absent entry. Valid retained `<product>-p<20 hex characters>` IDs stay unchanged. Partial legacy publications and unchanged-candidate reapproval reuse verified tasks and release evidence. The host materializes outcome, relevant code/contracts, sourced constraints, advisory approach, acceptance, verification and ordering. Missing tasks with no retained creation evidence may be created once; retained missing tasks require human reconciliation. Missing links can be reconciled only while the graph remains unpublished. Changed or foreign content and unmapped existing children are preserved for human reconciliation.

After a complete content/link readback, the host persists and confirms `publication_revision` and `publication_graph` on the still-claimed, unpublished Product before removing any task hold. This independent membership survives child metadata loss throughout partial release. A conflicting retained snapshot is preserved and refused. Then `nightweaver-plan-release-v1` records the exact Product, candidate revision and entry-to-task map. This protocol comment is excluded from Product grounding. It authorizes recovery of partial hold removal only under unchanged approval, workflow and claim. It does not authorize Delivery or Resume. Every uncertain write is reconciled through current tracker reads. A retained creation intent prevents another create after an uncertain launch, including when no task is yet visible.

Before a new create, the publisher checks all retained creation records against the approved graph. A malformed or foreign record stops publication. The publisher then saves a `nightweaver-plan-create-v1` intent as a JSON string in Product metadata under `publication_create_<hash>`. The hash binds the approved revision and entry. The record contains the Product, revision, entry, task ID and resolved flag. A new unresolved intent uses an empty task ID. Only a verified inventory match supplies the resolved actual ID. Legacy intents may retain their original deterministic task ID. The guarded, file-backed metadata patch preserves other metadata. A fresh read must confirm the exact record. A fresh inventory check stops launch if a task appeared after intent confirmation. The final pre-launch authority observation checks the same record, current workflow, ownership, approval and grounding. Each publication session also checks the Product's observed `updated_at` version. An old session cannot rely only on a reused assignee. Confirmed publisher writes advance its observed version.

A fresh complete inventory must show exactly one matching task with exact content, provenance, native Product link, lifecycle and permitted dependencies before the publisher saves a resolved binding. A lost command response can resolve without a returned ID. A retained unresolved intent with no task refuses another create, including after restart or guarded Retry. It can represent a child command that still runs after owner death. Missing, malformed or conflicting evidence preserves tasks and remaining holds. The diagnostic identifies the Product, revision, entry, observed IDs and failed check. The maintainer must inspect the evidence and reconcile the binding before guarded Retry. Valid retained deterministic-ID tasks without intents remain recoverable after exact validation.

The supported writer boundary is the Orchestrator. Both continuous Planning and explicit approved-stage dispatch reach the same publisher through `OperationalRetryCoordinator`. The existing process-lifetime mutex excludes other Orchestrators by physical Git common directory across Project names and host roots. The publisher also rejects overlapping calls for the same physical repository and Product inside one process, across publisher instances. These locks are not an atomic Beads metadata uniqueness constraint. Direct external `bd` writes remain outside serialization. Fresh authority, provenance conflict checks and guarded write/readback checks detect conflicts without adopting or deleting foreign work. No workflow database or new stage is added.

The publisher advances its Product observation version only from its own successful guarded update response. A fresh confirmation read must match that version and the owned workflow. If an intent or membership update loses its response and advances the Product version, content readback alone cannot restore authority. The invocation stops and preserves the intent, tasks and remaining holds. A later authorized invocation can reconcile that evidence. A zero-match retained intent still forbids another create. Release comments do not change the native Product version; receipt confirmation must retain the prior version. A release and reclaim with the same assignee cannot supply authority through any of these confirmation reads. Before the generic child check, a complete inventory checks each approved entry for conflicting provenance. Duplicate tasks retain their native links and receive the full Product, revision, entry, observed IDs, failed check and maintainer action diagnostic.

Before each missing dependency write, the host refreshes Product authority after observing the missing edge, then rereads the dependent task and validates its content, publication hold, open status and empty assignee. A change since creation verification stops publication without writing that dependency. The current `bd dep add` interface has no conditional status/assignee guards: this final read narrows the concurrent-write window but cannot make a separate read and dependency write atomic. Complete graph verification still precedes release.

Complete graph verification uses one fresh full native inventory, including each task's embedded dependency edges. It checks task content and ownership, exact dependency sets and unmapped Product children together. The final hold checks reuse only that same verification read, before the next mutation; the final Product write still requires a fresh authority/grounding check. Batching these reads avoids repeatedly opening native tracker processes for individual nodes without caching across writes or changing publication/recovery deadlines.

Generated-task identity also comes from retained Product graph membership, creation bindings and native `discovered-from` links to Products carrying publication evidence. Missing, invalid or conflicting child provenance fails admission and surfaces graph attention even for a single-task graph or an interrupted, unpublished graph. Admission checks unique entries, actual IDs and all provenance matches, including duplicates without native links. Valid partial publication remains held without creating a graph-conflict pause. Removing a child's metadata does not turn it into manual work. Manual tasks without publication evidence retain their existing admission behavior.

After publication, a follow-up Product (`feature` or `bug`) may cite the published Product with `discovered-from` without joining its task graph or pausing Delivery. An unmapped task with that link still requires human reconciliation, including a manual task without publication metadata. Every member named in the retained graph must remain a task with matching content and dependencies; changing its type cannot evade graph validation.

The final guarded Product update atomically adds `planned`, removes `stage:plan-approved`, releases the claim, and confirms the retained `publication_revision` plus `publication_graph` (a JSON string containing exact task IDs, titles, descriptions, acceptance and blocking dependencies). Generated task admission requires the parent to be published and unpaused and the entire live graph to match that snapshot, including native links. This parent barrier prevents admission during partial hold removal. Completed and in-flight statuses may advance normally; changing task boundaries, content or links stops further admission. Live discovery and explicit dispatch record human-attention evidence and guarded `needs-human` pauses on affected open/unassigned or currently owned tasks, preserving stages, claims and repair counters. Closed and foreign-owned tasks remain untouched. Replanning requires human guidance and renewed review, never automatic rewriting of executing tasks. Manual tasks keep their existing contract.

Retain comments, checkout and temporary evidence across interruption or human pause. Startup can release an exact owned approved-stage claim for reconciliation; it preserves ambiguous legacy claimed/planned Products. A publication conflict follows the existing guarded needs-human path without a Planner replay or repair-counter change. Attention Desk Resume/Retry is the only pause-resume authority; a saved answer or edited Product changes grounding but never clears a pause. If grounding changes while the exact approved candidate/review remain intact, approved-stage dispatch hands that retained candidate back to `stage:plan-challenge` under the current claim guards. Reapproval of unchanged task boundaries reuses the same task IDs and partial release evidence. Changed candidate identity or conflicting existing tasks requires human reconciliation. Continuous Planning uses the same approved-stage publisher for restart reconciliation.

`PlanningCoordinator.RunClaimedAsync` composes bounded turns under an already-owned claim, rereading native readiness before each role. The caller owns admission, interrupted-claim recovery and drain/cancellation finalization. Planner/repair and approval turns retain the claim for automatic graph publication; verified publication or a product pause releases it atomically. Standalone Challenger dispatch can still release verified approval for a later approved-stage dispatch. The continuous Planning slot owns this seam: fresh native admission, guarded open/unassigned claim acquisition, bounded uncertain-write reconciliation and exact owned-claim finalization. A confirmed publication or pause receipt ends outgoing operations; local reservations are released before discovery notification. Preparation and turns run outside serialized admission. Backpressure counts every nonclosed/nonpaused published task, including blocked, assigned and merge work, but excludes persisted `deferred` tasks regardless of the defer-until date; already admitted workflows settle.
