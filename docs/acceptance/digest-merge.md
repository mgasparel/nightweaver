# Catch-Up Digest verified merges

The digest consumes the existing host finalizer protocol, including its referenced
agent report, for the exact closed task. It shows Merged with the full PR URL and
merge commit; only validated safe HTTPS evidence becomes a link. Source-only
records, arbitrary SHAs and invalid/conflicting records retain honest resolved,
no-op or aborted outcomes. Cleanup failures appear separately.

`DigestMergeEvidenceTests` passes the finalizer's actual formatter through parsing
and digest projection. The cheap cases cover empty close reason, cleanup failure,
missing/agent-only/malformed/foreign/conflicting/mismatched history, duplicate
source IDs, credential URLs and legitimate non-code closures. Both records must
identify an actual PR: issue/homepage URLs and nonpositive or nonnumeric PR IDs
remain invalid even when the source and host records agree.
`OperationalObservationSourceTests` checks four-history turns, compact reuse,
idle collection, comments-only invalidation, periodic auditing, source retirement,
failure health and the 128-task acquisition cap without truncating digest counts.
No native .NET journey was added; existing native observation, HTTP and merger
journeys retain their process/tool boundaries and six-thread scheduling.

Run the scoped browser check using the established exact isolated lifecycle:

```powershell
pwsh -NoProfile -File tests/Nightweaver.QA.AppHost/studio-cutover.ps1 `
  -Only digest-merge-qa -OutputDirectory <fresh-invocation-evidence-directory>
```

The script starts a fresh isolated stack, discovers its direct endpoint and
disposable tracker, then stops that exact stack in `finally`. The probe seeds
closed tasks through real `bd`, observes real collector responses in Playwright,
checks exact links, matching issue-URL rejection, separate cleanup warnings, notes/agent-only rejection,
non-code closure, automatic comment-only refresh and legacy shared-evidence reuse.
It captures 1440/375/720 layouts and rejects overflow, browser errors, automatic
external requests and browser mutations. This adds the previously uncovered
producer-format history-to-digest rendering boundary without replaying native
Delivery or Merger. Reports/screenshots and full-suite timing stay in invocation
evidence; the Bead comment records exact candidate, counts and baseline limits.
