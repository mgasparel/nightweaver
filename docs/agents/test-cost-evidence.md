# Test cost in Delivery

Every change owns its test cost, including production changes that make existing
native tests issue more commands, publish more fixtures or repeat lifecycle work.
Use cheap deterministic unit/component/coordinator fixtures for detailed cases.
An added native journey must identify an uncovered process/tool boundary, explain
why existing coverage cannot prove it, review overlap, share immutable setup, and
report its measured additional cost. Do not delete coverage, weaken assertions,
reduce the committed six threads, inflate deadlines or rerun until green.

The local operating target is **below 240 seconds**; the hard limit remains
**below 300 seconds**. Both include normal build, fixture publishing, execution
and ordinary teardown. A 240–299.999-second run requires actionable cost review,
not a new hard release gate. A complete all-passing run at or above 300 seconds
violates performance policy with zero failed tests. Correctness, operating target
and hard budget are separate results. Hosted time remains diagnostic, with the
existing hang timeout and exact-head final correctness checks unchanged.

## Retained measurements

Use PowerShell 7 and explicit paths from the repository root:

```powershell
pwsh -NoProfile -File scripts/Measure-TestSuite.ps1 `
  -Checkout <absolute-checkout> -EvidenceRoot <absolute-invocation-evidence-root> `
  -BaseRevision <full-base-sha> -Baseline <accepted-result.json> `
  -ProductionInstallation <absolute-installation> -Project nightshift
```

Omit `-Baseline` when no compatible artifact exists; report comparison unavailable
and why. Never manufacture a zero delta or silently choose the newest file.
The baseline JSON root must be an object. Scalar and array roots make comparison
unavailable with an explicit reason; candidate counts and the original test exit
are still retained. JSON null is treated as no supplied baseline.
All consumed baseline metadata is type-checked before comparison: counts and
exit codes must be nonnegative bounded integers, acceptance/dirty flags must be
booleans, identities and environment fields must have their documented scalar
types, and production must contain a recognized state. Malformed or missing
metadata records an explicit unavailable-comparison reason, retains original
candidate counts/exit, leaves delta/ratio null and cannot trigger capture.
Parsed boot timestamp objects are normalized to the measurement's UTC round-trip
string before comparison, so JSON date conversion preserves identical boot
identities. A different boot identity still makes comparison unavailable.
`-Preparation` describes any separate restore/first-build preparation (default:
none). Use the same preparation for a comparable pair. The script runs the normal
complete command with a unique report filename, without `--no-build`, filters or
scheduling overrides. The outer stopwatch stops after the test process and normal
teardown settle; XML retention, metadata and incident capture are outside it.

Each unique evidence directory contains `result.json`, raw `suite.log`, and copied
xUnit reports. The v1 result records full base/candidate SHA and tree, dirty state,
command, actual total/pass/fail/skip and test exit, outer seconds, six-thread
source hash, preparation, .NET SDK/runtime and bd versions, CPU count, machine/boot identity,
actual temp path/TEMP/TMP and production state/build/runtime/configuration hash.
The baseline path and SHA256 identify the exact retained comparison. No generic
environment, command-line or configuration-content dumps are collected.

Keep the raw result immutable after measurement. An accepted baseline is a
retained reviewed copy with `accepted: true`, the original result path/hash and
the acceptance rationale recorded in the Bead comment. Acceptance requires clean
source, complete passing counts and matching policy/environment metadata; the
script never self-accepts a measurement. Baseline/candidate source revisions may
differ: review the diff and workload to attribute cost. Missing fields or changed
preparation, temp roots, tooling, scheduling or production context make automatic
comparison unavailable. A production owner record is checked against PID plus
creation time; unavailable/unknown state is not evidence that production is down.

Report exact artifact/revision identities, discovered counts, outer seconds,
comparable delta and concrete additional work for every change, even if no tests
were added. Explain material deltas with code/workload evidence. If the artifact
format cannot compare older retained evidence, reuse its raw facts with an explicit
manual comparability analysis; do not rerun an unchanged baseline just to convert
its format. A successful focused run never establishes full-suite acceptance.

Implementer supplies one complete candidate run and attribution. Reviewer checks
the evidence and native coverage overlap by static inspection. QA reuses retained
timing when applicable and exercises any new behavior with focused checks. An
attributable material regression is actionable `changes_required` on the
originating Bead under the existing repair lifecycle; correctness may still pass.
Do not call code-cost repair out of scope or a technical blocker solely because
checks failed. Missing reliable measurements or unresolved environment conditions
must be investigated and reported accurately. Merger still runs fresh required
checks on the synchronized head on every dispatch, even an unchanged head, and
latest main must be measured after integration. Reuse other successful evidence;
do not add duplicate complete suites without a new concern.

## Capture before disturbing state

After retaining a measurement, an elapsed time **at least 1.5×** an explicitly
comparable accepted baseline automatically invokes the observation-only collector
once for that measurement. This conservative early warning preserves evidence
even before attribution is resolved. It is a diagnostic trigger, not another
release gate and not proof of a leak. The measurement script performs no rerun,
reset, restart, reboot, kill, cleanup or configuration change. Capture errors are
retained separately and never replace the original test result. Its process exit
is the original test exit on failure; otherwise 2 means incomplete measurement,
3 means hard-budget violation, and 4 means capture failed. Target-only misses
remain exit 0 with explicit cost-review evidence.

For broad native slowdown/timeouts or sustained resource drift, invoke explicitly
with a reason in `-Label`, before any action that erases process state:

```powershell
pwsh -NoProfile -File scripts/Capture-ResourceIncident.ps1 `
  -Checkout <absolute-checkout> -OutputRoot <absolute-evidence-root> `
  -ProductionInstallation <absolute-installation> -Project nightshift `
  -Label <reason> -TimingResult <triggering-result.json>
```

The default three samples ten seconds apart retain PID/creation/parent identities,
private bytes, working set, handles, threads, cumulative CPU/raw IO, RAM/commit,
paged/nonpaged pools, paging/pagefile, uptime and safe source/production metadata.
Compare same-process deltas and correlated resource cost. RAM/RSS alone or one
growth sample cannot prove a leak. ns-726.5 owns attribution; this hook packages
and invokes capture only. No always-running monitor is installed.

## JSON comment encoding

Write JSON comment files as UTF-8 without a BOM. Set `$path` to an absolute file
path in the current invocation's temporary directory. Set `$json` to the complete
comment JSON. Use the explicit encoding in all PowerShell versions:

```powershell
[System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
bd comments add <bead-id> -f $path
```

`Set-Content -Encoding utf8` can add a BOM, depending on the shell version.
The retained file
`H:/Nightweaver/prod/tmp/nightshift/ns-gjwx-p89ed2a6fe7ade8917f1e/20261123T1230348658366Z-merger/performance-rejection.json`
starts with `EF-BB-BF-7B-22-73-63-68`. Stored comment
`e060eb87-9adf-52f3-a59c-e15e31ba610b` starts with code point `65279`.
The retained invocation log shows `Set-Content -Encoding utf8`, followed by
`bd comments add` with `-f`. This evidence proves that the BOM reached the stored
comment. It does not prove that the full historical Merger invocation is valid.
Do not rewrite the historical comment, logs or paused Bead state.

The rejection parser removes at most one U+FEFF at position zero from its local
input view. It preserves stored comment text, source comment IDs and U+FEFF inside
JSON strings. Repeated leading BOMs and a BOM after whitespace remain invalid.
All schema, current-invocation, PR/HEAD, transcript, measurement and timing checks
still apply. Parser cases use the existing component fixture and guarded repair
handoff. They add no native journey or fixture publication.

## Merge success line ending

The success parser accepts one terminal LF or CRLF. It removes that ending only
from its local input view. It rejects all remaining CR and LF characters before
it checks the existing six-field grammar. Stored text and source IDs stay unchanged.
It does not extract a record from prose or quoted text.

The retained `merge-success.txt` for PR #95 has SHA256
`0DD8634B8B574990280D1868BE34CABB37790BE05196F75390AC8A3141196D5D`.
Its path is
`H:/Nightweaver/prod/tmp/nightshift/ns-a3d4/20261121T1444145098722Z-merger/merge-success.txt`.
Native comment `ebff9911-a5fe-5cb5-9f19-f788e736e8e8` contains the same
record followed by CRLF. The previous parser split on ASCII space. It left CRLF
in `merge_commit`, so SHA validation failed. The regression fixture uses this
record with no ending, LF and CRLF. It does not replay the historical merge.

Native comment IDs identify rows. `BeadsReader` orders comments by timestamp and
native row order. The success row has author Michael Gasparelli and timestamp
`2026-11-21T14:59:22Z`. Summary row `a29049f5-58ac-5db0-a90d-d7d7559af1eb`
precedes it at `2026-11-21T14:59:21Z`. Pause row
`95f63c21-f6cd-5407-9e0f-a9efbc4c2267` follows at `2026-11-21T14:59:44Z`.
`BeadCommentSnapshot` does not carry author. The verifier selects new IDs against
`PreInvocationCommentIds`; it does not authenticate authors. Missing baselines,
historical IDs and two valid new records still prevent completion. A malformed
companion does not replace a valid record or hide a second valid record.

The retained `comments.json` has 22 rows. Its last ID is
`df6191ee-1877-580e-a5af-f508155bf52f` at `2026-11-21T14:44:02Z`.
It excludes the success ID. This supports a timing comparison. It is not a saved
copy of the host's in-memory baseline. The settled invocation log records the
later `Set-Content` and `bd comments add` calls and the success ID. The terminal
CRLF explains the parser failure without a race claim. This repair does not
verify the full historical invocation or change the paused `ns-a3d4` workflow.

The repair adds 27 deterministic parser and injected verifier cases to
`MergerOutcomeVerifierTests`. It adds no native journey or fixture publication.
The existing native Git, required-check and workflow cases remain. Production
adds a bounded terminal-ending check and a scan for remaining CR and LF per
candidate comment. It adds no native command. Report measured candidate cost
and the retained baseline comparison in the Delivery evidence.

## All-passing hard-limit Merger rejection

Use the existing `nightweaver-merger-rejection-v1` comment with exactly ten top-level
fields: `schema`, `kind: "performance"`, `pull_request`, `checked_head`,
`subject: "nightweaver-full-suite-local-v1"`, actionable `summary` and `details`
(each at least 12 characters), `check_source: "local"`, `local_exit_code: 0`,
and `performance`. The nested object has exactly these fields:

```json
{
  "reason": "hard-limit",
  "evidence_id": "0123456789abcdef0123456789abcdef",
  "command": "dotnet test --solution Nightweaver.slnx --minimum-expected-tests 995 --report-xunit-xml --report-xunit-xml-filename 0123456789abcdef0123456789abcdef.xml --show-slowest-tests 20",
  "total": 1325,
  "passed": 1325,
  "failed": 0,
  "skipped": 0,
  "elapsed_seconds": 300.5,
  "hard_limit_seconds": 300,
  "test_exit_code": 0
}
```

Use actual measured counts (total at least 995, passed equal to total), finite
elapsed time at least 300 and the measurement's 32-hex GUID evidence ID. Include
raw artifact paths and attribution in a separate natural-language comment. Do
not fabricate failed-test counts or send this result through the test-failure
reader. Ordinary nonzero build/test and remote Actions contracts remain unchanged.
Soft target misses and qualitative cost findings use Review/QA findings, never
this hard-limit schema.

For this rejection path, run the measurement entry point with `-EvidenceRoot`
equal to the current invocation's `TEMP` directory. Keep its original
`result.json`, `suite.log` and copied `report-1.xml` under `<EvidenceRoot>/<evidence_id>/`.
The result records `NIGHTWEAVER_INVOCATION_ID` as `invocationId`; old or foreign
measurements cannot authorize a current rejection. The verifier opens only these
canonical current-invocation files with pinned, read-only, no-reparse handles.
It requires unique JSON keys, clean exact head, full command, six threads, no
measurement errors, matching passing counts/exit and finite elapsed time equal
to the frame. It cross-checks the raw complete passing terminal summary and
xUnit assembly counts, errors and not-run counts. Missing or contradictory
artifacts retain the diagnostic pause and cannot consume repair budget.

Construct `NIGHTWEAVER_PERFORMANCE_CHECK_V1 ` plus the launched
`NIGHTWEAVER_INVOCATION_ID` at runtime. Emit exactly one opening line consisting
of that marker, a space and compact JSON identical to the entire rejection comment;
emit `END_` plus the same marker on the next line. Use the existing
`NIGHTWEAVER_LOCAL_CHECK_OUTPUT` channel selection (terminal for Codex, single
final-response for Claude). Do not echo a literal current marker in tool commands.
Emit only after all checks settle, and run no more checks after it. Passing MTP
output before this frame is required complete correctness evidence. A missing,
failed or incomplete terminal summary cannot be replaced by matching JSON claims.
For the final-response channel, include the actual retained `suite.log` terminal
summary immediately before the frame so it is also visible in the settled log.

Independent verification uses the pinned settled log (8 MiB maximum), one
unquoted current-ID frame (32 KiB maximum), strict UTF-8, unique JSON keys, exact
frame/comment correspondence and fresh PR/head/origin checks. Missing, foreign,
duplicate, oversized, filtered or contradictory later results cannot authorize
repair. Verification failure retains the existing diagnostic pause behavior.
A verified rejection reuses `ChangesRequired` and existing guarded publication:
one repair increment, `stage:merge` to `stage:implement`, claim release and the
unchanged ownership/budget rules. Agents never publish these transitions.

Run the cheap injected script checks with
`pwsh -NoProfile -File tests/TestSuiteMeasurement.Tests.ps1`; the C# performance
cases extend `MergerOutcomeVerifierTests` without new native journeys.
