using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Execution;

/// <summary>Local disposal under repository exclusion, after owned processes settle. Callers must
/// exclude admission/use of the selected environments for the entire reconciliation.</summary>
internal interface ICompletedEnvironmentCleanup
{
    Task<IReadOnlyList<StartupCleanupDiagnostic>> ReconcileAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, string actor, CancellationToken cancellationToken,
        string? onlyBeadId = null, MergerVerifiedMergeEvidence? evidence = null, bool discoverOnly = false);
}

internal sealed class CompletedEnvironmentCleanup(BeadsReader reader, IProcessRunner processRunner, TimeProvider? timeProvider = null) : ICompletedEnvironmentCleanup
{
    public async Task<IReadOnlyList<StartupCleanupDiagnostic>> ReconcileAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, string actor, CancellationToken cancellationToken,
        string? onlyBeadId = null, MergerVerifiedMergeEvidence? evidence = null, bool discoverOnly = false)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var diagnostics = new List<StartupCleanupDiagnostic>();
        var protectedIds = new HashSet<string>(RuntimePaths.Comparer);
        // A completion session can have no tmp yet. Startup already initializes these roots;
        // the targeted completion path establishes the same safe roots before observation.
        if (onlyBeadId is not null)
        {
            SharedRuntimeRootValidator.EnsureUsable(paths.WorktreesRoot(project));
            SharedRuntimeRootValidator.EnsureUsable(paths.TmpRoot(project));
        }
        EnsureSharedRoots(paths, project);

        var store = new CompletedEnvironmentStore(paths, project, repository);
        var stored = store.Read();
        var records = stored.Obligations.Where(record => onlyBeadId is null || record.BeadId == onlyBeadId)
            .ToDictionary(record => record.BeadId, RuntimePaths.Comparer);
        var commonDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(processRunner, repository.Path,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken).ConfigureAwait(false));
        if (!RuntimePaths.Comparer.Equals(commonDirectory, RuntimePaths.Normalize(repository.GitCommonDirectory))
            || RuntimePaths.Comparer.Equals(commonDirectory, RuntimePaths.Normalize(repository.Path)))
            throw new ToolExecutionException("Target Repository Git common directory changed from its validated identity");

        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var registry = await new GitWorktreeReader(processRunner).ReadAsync(repository, cancellationToken).ConfigureAwait(false);
        var worktrees = RuntimeDirectoryReader.ReadRoot(paths.WorktreesRoot(project));
        var tmp = RuntimeDirectoryReader.ReadRoot(paths.TmpRoot(project));
        if (onlyBeadId is not null && evidence is not null && (!inventory.ById.TryGetValue(onlyBeadId, out var completed)
                || !StartupEnvironmentClassifier.IsCompletedTmpOwner(completed, actor)
                || completed.IssueType != "task"))
        {
            const string reason = "Completed task is missing or no longer closed, unassigned and unpaused; resources retained.";
            // Completion evidence still accounts for these resources, but grants no disposal
            // authority after a workflow change. Persist before the worker dequeues the hint.
            var registrations = registry.Where(entry => RuntimePaths.Comparer.Equals(entry.Path,
                Path.Combine(paths.WorktreesRoot(project), onlyBeadId))).ToArray();
            var registration = registrations.Length == 1 && !registrations[0].Main && !registrations[0].Bare
                && !registrations[0].Detached && registrations[0].Branch == $"refs/heads/nightweaver/{onlyBeadId}"
                && string.Equals(registrations[0].Head, evidence.CheckedHead, StringComparison.OrdinalIgnoreCase)
                ? registrations[0] : null;
            var retained = records.TryGetValue(onlyBeadId, out var existing) ? existing
                : store.Create(onlyBeadId, WorktreePurpose.Delivery, registration, evidence);
            await store.WriteAsync(CleanupRetryPolicy.Fail(retained, reason, false, now), cancellationToken).ConfigureAwait(false);
            diagnostics.Add(new(onlyBeadId, retained.Worktree.Path, reason));
            return diagnostics.AsReadOnly();
        }
        var plan = StartupPlanner.PlanForStartup(actor, inventory, registry, worktrees, tmp);
        AddInspectionDiagnostics(plan, diagnostics);

        // Reconciliation recovers closure-before-enqueue and legacy leftovers using current canonical
        // paths and independently read tracker/registry state, never arbitrary comment prose.
        foreach (var bead in inventory.ById.Values.Where(bead => RuntimePaths.SafeId(bead.Id)
                     && WorktreeProvisioner.IsSafeBeadId(bead.Id)
                     && (onlyBeadId is null || bead.Id == onlyBeadId)
                     && (bead.IssueType == "task" && bead.Status == "closed"
                         || bead.IssueType is "feature" or "bug" && bead.Labels.Contains("planned"))))
        {
            if (records.ContainsKey(bead.Id)) continue;
            var path = Path.Combine(paths.WorktreesRoot(project), bead.Id);
            var registrations = registry.Where(entry => RuntimePaths.Comparer.Equals(entry.Path, path)).ToArray();
            if (registrations.Length == 0 && !worktrees.Entries.Any(entry => entry.Id == bead.Id)
                && !tmp.Entries.Any(entry => entry.Id == bead.Id) && onlyBeadId is null) continue;
            var purpose = bead.IssueType == "task" ? WorktreePurpose.Delivery : WorktreePurpose.Planning;
            // Unsupported registrations are retained as attention records without claiming ownership.
            var registration = registrations.Length == 1 ? registrations[0] : null;
            var supported = registrations.Length <= 1 && (registration is null || (!registration.Main && !registration.Bare
                && (purpose == WorktreePurpose.Delivery
                    ? !registration.Detached && registration.Branch == $"refs/heads/nightweaver/{bead.Id}"
                    : registration.Detached && registration.Branch is null)));
            var headChanged = evidence is not null && registration is not null
                && !string.Equals(registration.Head, evidence.CheckedHead, StringComparison.OrdinalIgnoreCase);
            var record = store.Create(bead.Id, purpose, supported && !headChanged ? registration : null,
                onlyBeadId == bead.Id ? evidence : null);
            record = record with
            {
                Worktree = record.Worktree with { ConfirmedAbsent = registrations.Length == 0
                    && !worktrees.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, path)) },
                Tmp = record.Tmp with { ConfirmedAbsent = !tmp.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Tmp.Path)) }
            };
            if (headChanged) record = record with { Status = CleanupStatus.NeedsAttention,
                Diagnostic = "Registered worktree HEAD changed from the verified Merger head; resources retained." };
            if (!supported) record = record with { Status = CleanupStatus.NeedsAttention,
                Diagnostic = "Unsupported or ambiguous completed environment registration; resources retained." };
            await store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            records.Add(bead.Id, record);
        }
        foreach (var record in records.Values.Where(record => record.Status == CleanupStatus.Resolved).ToArray())
        {
            if (registry.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Worktree.Path))
                || worktrees.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Worktree.Path))
                || tmp.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Tmp.Path)))
            {
                var changed = CleanupRetryPolicy.Fail(record,
                    "Resources reappeared after confirmed absence; identity requires operator inspection.", false, now);
                await store.WriteAsync(changed, cancellationToken).ConfigureAwait(false);
                records[record.BeadId] = changed;
            }
        }
        if (discoverOnly) return diagnostics.AsReadOnly();
        // Interrupted removals cannot be replayed without confirming process settlement.
        foreach (var record in records.Values.ToArray())
        {
            if (CleanupRetryPolicy.IsInterrupted(record))
                await SaveAsync(CleanupRetryPolicy.Interrupted(record, now)).ConfigureAwait(false);
        }
        var due = records.Values.Where(record => CleanupRetryPolicy.IsDue(record, now))
            .Select(record => record.BeadId).ToHashSet(RuntimePaths.Comparer);
        foreach (var id in due)
            await SaveAsync(CleanupRetryPolicy.Begin(records[id], now)).ConfigureAwait(false);

        async Task SaveAsync(CompletedEnvironmentObligation record)
        {
            if (records.TryGetValue(record.BeadId, out var current) && current == record) return;
            await store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            records[record.BeadId] = record;
        }

        async Task RetainAsync(string beadId, string path, string reason, bool transient = false)
        {
            diagnostics.Add(new(beadId, path, reason));
            if (records.TryGetValue(beadId, out var record))
                await SaveAsync(CleanupRetryPolicy.Fail(record, reason, transient, (timeProvider ?? TimeProvider.System).GetUtcNow())).ConfigureAwait(false);
        }


        foreach (var candidate in plan.Removals.Where(candidate => due.Contains(candidate.BeadId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!records.TryGetValue(candidate.BeadId, out var obligation)
                || obligation.Worktree.ConfirmedAbsent || obligation.Registration is null
                || !SameRegistration(obligation.Registration, candidate.Registration)
                || obligation.VerifiedHead is not null && !string.Equals(obligation.VerifiedHead,
                    candidate.Registration.Head, StringComparison.OrdinalIgnoreCase)
                || evidence is not null && !string.Equals(evidence.CheckedHead,
                    candidate.Registration.Head, StringComparison.OrdinalIgnoreCase))
            {
                await RetainAsync(candidate.BeadId, candidate.Path,
                    "worktree identity changed from the persisted obligation; resources retained").ConfigureAwait(false);
                protectedIds.Add(candidate.BeadId);
                continue;
            }
            EnsureSharedRoots(paths, project);
            var fresh = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
            var currentCandidate = fresh.Plan.Removals.FirstOrDefault(removal => removal.BeadId == candidate.BeadId);
            if (currentCandidate is null || !SameRegistration(candidate.Registration, currentCandidate.Registration))
            {
                diagnostics.Add(new(candidate.BeadId, candidate.Path,
                    "cleanup was skipped because the Bead or registered worktree changed after discovery"));
                continue;
            }

            var identityFailure = await ValidateCandidateAsync(paths, project, repository, currentCandidate,
                cancellationToken).ConfigureAwait(false);
            if (identityFailure is not null)
            {
                await RetainAsync(currentCandidate.BeadId, currentCandidate.Path,
                    identityFailure).ConfigureAwait(false);
                protectedIds.Add(currentCandidate.BeadId);
                continue;
            }

            // Git identity checks are external operations. Re-read the current Bead after they
            // finish so a pause or ownership change during validation invalidates this plan.
            var afterIdentity = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
            var confirmedCandidate = afterIdentity.Plan.Removals.FirstOrDefault(removal => removal.BeadId == candidate.BeadId);
            if (confirmedCandidate is null || !SameRegistration(currentCandidate.Registration, confirmedCandidate.Registration))
            {
                diagnostics.Add(new(candidate.BeadId, candidate.Path,
                    "cleanup was skipped because the Bead or registered worktree changed during final Git identity validation"));
                continue;
            }
            currentCandidate = confirmedCandidate;

            EnsureSharedRoots(paths, project);
            ProcessResult removal;
            try
            {
                removal = await processRunner.RunAsync(GitCommand.Create(
                    ["worktree", "remove", "--force", "--", currentCandidate.Path], repository.Path), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is ToolExecutionException or IOException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await RetainAsync(currentCandidate.BeadId, currentCandidate.Path,
                    exception.Message, CleanupRetryPolicy.IsTransient(exception)).ConfigureAwait(false);
                protectedIds.Add(currentCandidate.BeadId);
                continue;
            }

            if (removal.ExitCode != 0)
            {
                var reason = string.IsNullOrWhiteSpace(removal.StandardError)
                    ? $"git worktree remove exited {removal.ExitCode}"
                    : $"git worktree remove exited {removal.ExitCode}: {removal.StandardError.TrimEnd()}";
                await RetainAsync(currentCandidate.BeadId, currentCandidate.Path,
                    reason, CleanupRetryPolicy.IsTransientGitFailure(reason)).ConfigureAwait(false);
                protectedIds.Add(currentCandidate.BeadId);
                continue;
            }

            // A successful command is not enough: only observed absence proves the cleanup completed.
            var afterRemoval = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
            var stillRegistered = afterRemoval.Registry.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, currentCandidate.Path));
            var stillOnDisk = afterRemoval.Worktrees.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, currentCandidate.Path));
            if (stillRegistered || stillOnDisk)
            {
                var reason = stillRegistered && stillOnDisk
                    ? "Git reported success but the worktree registration and directory remain"
                    : stillRegistered ? "Git reported success but the worktree registration remains"
                    : "Git reported success but the worktree directory remains";
                await RetainAsync(currentCandidate.BeadId, currentCandidate.Path,
                    reason).ConfigureAwait(false);
                protectedIds.Add(currentCandidate.BeadId);
            }
            else
                await SaveAsync(records[currentCandidate.BeadId] with
                { Worktree = records[currentCandidate.BeadId].Worktree with { ConfirmedAbsent = true } }).ConfigureAwait(false);
        }

        // Tmp ownership is determined only after all worktree commands have settled.
        var postWorktree = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
        AddInspectionDiagnostics(postWorktree.Plan, diagnostics);
        foreach (var candidate in postWorktree.Plan.OrphanTmp.Where(candidate => due.Contains(candidate.BeadId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!records.TryGetValue(candidate.BeadId, out var obligation) || obligation.Tmp.ConfirmedAbsent)
            {
                await RetainAsync(candidate.BeadId, candidate.Path,
                    "temporary directory reappeared after confirmed absence; resources retained").ConfigureAwait(false);
                continue;
            }
            EnsureSharedRoots(paths, project);
            var fresh = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
            var currentCandidate = fresh.Plan.OrphanTmp.FirstOrDefault(orphan => orphan.BeadId == candidate.BeadId
                && string.Equals(orphan.Path, candidate.Path, StringComparison.Ordinal));
            if (currentCandidate is null || protectedIds.Contains(candidate.BeadId))
            {
                diagnostics.Add(new(candidate.BeadId, candidate.Path,
                    "temporary files were retained because a current worktree or Bead state no longer proves orphan ownership"));
                continue;
            }

            var tmpEntry = fresh.Tmp.Entries.SingleOrDefault(entry => RuntimePaths.Comparer.Equals(entry.Path, currentCandidate.Path));
            if (tmpEntry is null || tmpEntry.State != DirectoryState.Present || tmpEntry.AmbiguousIdentity
                || !string.Equals(tmpEntry.Id, candidate.BeadId, StringComparison.Ordinal)
                || !string.Equals(tmpEntry.Path, Path.Combine(fresh.Tmp.Path, candidate.BeadId), StringComparison.Ordinal))
            {
                await RetainAsync(candidate.BeadId, candidate.Path,
                    "temporary directory identity changed or became unsafe before deletion")
                    .ConfigureAwait(false);
                protectedIds.Add(candidate.BeadId);
                continue;
            }

            // Required tracker and shared-root observations are fatal startup dependencies.
            // Keep them outside the local tmp-removal catch so an unavailable snapshot cannot
            // be downgraded to a per-Bead cleanup failure.
            EnsureSharedRoots(paths, project);
            var finalState = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!finalState.ById.TryGetValue(candidate.BeadId, out var bead)
                || !StartupEnvironmentClassifier.IsCompletedTmpOwner(bead, actor)
                || !MatchesPurpose(records[candidate.BeadId], bead))
            {
                diagnostics.Add(new(candidate.BeadId, candidate.Path,
                    "temporary files were retained because the Bead is missing, paused, or no longer completed"));
                continue;
            }
            var finalRegistry = await new GitWorktreeReader(processRunner).ReadAsync(repository, cancellationToken).ConfigureAwait(false);
            var finalWorktrees = RuntimeDirectoryReader.ReadRoot(paths.WorktreesRoot(project));
            if (finalWorktrees.State != DirectoryState.Present)
                throw new ToolExecutionException($"shared worktree root '{finalWorktrees.Path}' became unavailable or unsafe during temporary cleanup");
            var stillProtected = finalRegistry.Any(entry => WorktreeProvisioner.PathsOverlap(entry.Path, currentCandidate.Path)
                    || !entry.Main && !entry.Bare
                    && RuntimePaths.Comparer.Equals(RuntimePaths.ChildId(finalWorktrees.Path, entry.Path), candidate.BeadId))
                || finalWorktrees.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Id, candidate.BeadId));
            if (stillProtected)
            {
                diagnostics.Add(new(candidate.BeadId, candidate.Path,
                    "temporary files were retained because a worktree registration or directory survives cleanup"));
                continue;
            }

            var finalTmp = RuntimeDirectoryReader.ReadRoot(paths.TmpRoot(project));
            if (finalTmp.State != DirectoryState.Present)
                throw new ToolExecutionException($"shared temporary root '{finalTmp.Path}' became unavailable or unsafe during cleanup");
            var finalTmpEntry = finalTmp.Entries.SingleOrDefault(entry => RuntimePaths.Comparer.Equals(entry.Path, currentCandidate.Path));
            if (finalTmpEntry is null || finalTmpEntry.State != DirectoryState.Present || finalTmpEntry.AmbiguousIdentity
                || !string.Equals(finalTmpEntry.Id, candidate.BeadId, StringComparison.Ordinal)
                || !string.Equals(finalTmpEntry.Path, Path.Combine(finalTmp.Path, candidate.BeadId), StringComparison.Ordinal))
            {
                await RetainAsync(candidate.BeadId, candidate.Path,
                    "the temporary directory changed identity or became unsafe before deletion")
                    .ConfigureAwait(false);
                protectedIds.Add(candidate.BeadId);
                continue;
            }

            EnsureSharedRoots(paths, project);
            try
            {
                WorktreeProvisioner.ValidateNestedTree(currentCandidate.Path);
                Directory.Delete(currentCandidate.Path, recursive: true);
                if (Directory.Exists(currentCandidate.Path) || File.Exists(currentCandidate.Path))
                    throw new IOException("the temporary directory still exists after recursive removal");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is ToolExecutionException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                await RetainAsync(candidate.BeadId, candidate.Path,
                    exception.Message, CleanupRetryPolicy.IsTransient(exception)).ConfigureAwait(false);
                protectedIds.Add(candidate.BeadId);
            }
        }

        var observed = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
        EnsureSharedRoots(paths, project);
        foreach (var record in records.Values.Where(record => due.Contains(record.BeadId)).ToArray())
        {
            var worktreeAbsent = !observed.Registry.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Worktree.Path))
                && !observed.Worktrees.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Worktree.Path));
            var tmpAbsent = !observed.Tmp.Entries.Any(entry => RuntimePaths.Comparer.Equals(entry.Path, record.Tmp.Path));
            var eligible = observed.Inventory.ById.TryGetValue(record.BeadId, out var bead)
                && StartupEnvironmentClassifier.IsCompletedTmpOwner(bead, actor) && MatchesPurpose(record, bead);
            if (worktreeAbsent && tmpAbsent && eligible)
                await SaveAsync(record with { Worktree = record.Worktree with { ConfirmedAbsent = true },
                    Tmp = record.Tmp with { ConfirmedAbsent = true }, Status = CleanupStatus.Resolved,
                    NextAttempt = null, Diagnostic = null, ResolvedAt = record.ResolvedAt ?? (timeProvider ?? TimeProvider.System).GetUtcNow() }).ConfigureAwait(false);
            else
            {
                await SaveAsync(record with { Worktree = record.Worktree with { ConfirmedAbsent = record.Worktree.ConfirmedAbsent || worktreeAbsent },
                    Tmp = record.Tmp with { ConfirmedAbsent = record.Tmp.ConfirmedAbsent || tmpAbsent },
                    Status = eligible && record.Status == CleanupStatus.RetryScheduled ? CleanupStatus.RetryScheduled : CleanupStatus.NeedsAttention,
                    NextAttempt = eligible && record.Status == CleanupStatus.RetryScheduled ? record.NextAttempt : null, ResolvedAt = null,
                    Diagnostic = record.Diagnostic ?? "Current Bead or resource identity does not authorize completed-environment disposal." }).ConfigureAwait(false);
                diagnostics.Add(new(record.BeadId, record.Worktree.Path, records[record.BeadId].Diagnostic!));
            }
        }
        foreach (var record in records.Values.Where(record => !due.Contains(record.BeadId) && record.Status != CleanupStatus.Resolved))
            diagnostics.Add(new(record.BeadId, record.Worktree.Path, record.Diagnostic ?? "Cleanup is pending a later eligible attempt."));
        store.TrimResolvedHistory();
        return diagnostics.Where(item => onlyBeadId is null || item.BeadId == onlyBeadId).Distinct().ToList().AsReadOnly();
    }

    private async Task<CurrentPlan> ReadCurrentPlanAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, string actor, CancellationToken cancellationToken)
    {
        var commonDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(processRunner, repository.Path,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken).ConfigureAwait(false));
        if (!RuntimePaths.Comparer.Equals(commonDirectory, RuntimePaths.Normalize(repository.GitCommonDirectory)))
            throw new ToolExecutionException("Target Repository Git common directory changed during cleanup");
        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var registry = await new GitWorktreeReader(processRunner).ReadAsync(repository, cancellationToken).ConfigureAwait(false);
        var worktrees = RuntimeDirectoryReader.ReadRoot(paths.WorktreesRoot(project));
        var tmp = RuntimeDirectoryReader.ReadRoot(paths.TmpRoot(project));
        return new(inventory, registry, worktrees, tmp, StartupPlanner.PlanForStartup(actor, inventory, registry, worktrees, tmp));
    }

    private async Task<string?> ValidateCandidateAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, RemoveWorktree candidate, CancellationToken cancellationToken)
    {
        var root = RuntimePaths.Normalize(paths.WorktreesRoot(project));
        var id = RuntimePaths.ChildId(root, candidate.Path);
        if (!string.Equals(id, candidate.BeadId, StringComparison.Ordinal)
            || !string.Equals(candidate.Path, Path.Combine(root, candidate.BeadId), StringComparison.Ordinal))
            return "the registered path is not the exact canonical Bead-ID child of the current Project root";
        if (WorktreeProvisioner.PathsOverlap(candidate.Path, repository.Path)
            || WorktreeProvisioner.PathsOverlap(candidate.Path, repository.GitCommonDirectory))
            return "the candidate worktree overlaps the Target Repository or its common directory";

        var worktreeRoot = RuntimeDirectoryReader.ReadRoot(root);
        if (worktreeRoot.State != DirectoryState.Present)
            throw new ToolExecutionException($"shared runtime root '{root}' became unavailable or unsafe during startup cleanup");
        var entry = worktreeRoot.Entries.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, candidate.Path));
        if (entry is null || entry.State != DirectoryState.Present || entry.AmbiguousIdentity
            || !string.Equals(entry.Id, candidate.BeadId, StringComparison.Ordinal))
            return entry?.FailureReason ?? "the canonical worktree directory is missing, linked, unusable, or ambiguous";

        // Shared-root/tracker failures remain fatal above this boundary. Checkout-local
        // inspection failures retain only this environment and must not stop reconciliation.
        try
        {
            WorktreeProvisioner.ValidateNestedTree(candidate.Path);
            var failure = await ValidateCheckoutIdentityAsync(processRunner, repository, candidate, cancellationToken).ConfigureAwait(false);
            if (failure is not null) return failure;
            var status = await processRunner.RunAsync(GitCommand.Create(
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"], candidate.Path), cancellationToken).ConfigureAwait(false);
            return status.ExitCode != 0 || status.StandardOutput.Length != 0
                ? "Completed checkout has changed source or nonignored files; resources retained." : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ToolExecutionException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return $"Completed checkout inspection at '{candidate.Path}' failed; resources retained: {exception.Message}";
        }
    }

    internal static async Task<string?> ValidateCheckoutIdentityAsync(IProcessRunner processRunner,
        ValidatedTargetRepository repository, RemoveWorktree candidate, CancellationToken cancellationToken)
    {
        string topLevel;
        string gitDirectory;
        string commonDirectory;
        string branch;
        string head;
        try
        {
            topLevel = await ReadGitValueAsync(processRunner, candidate.Path,
                ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
            gitDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(processRunner, candidate.Path,
                ["rev-parse", "--path-format=absolute", "--git-dir"], cancellationToken).ConfigureAwait(false));
            commonDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(processRunner, candidate.Path,
                ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken).ConfigureAwait(false));
            branch = await ReadGitValueAsync(processRunner, candidate.Path,
                ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
            head = await ReadGitValueAsync(processRunner, candidate.Path,
                ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception) { return exception.Message; }
        if (!string.Equals(RuntimePaths.Normalize(topLevel), candidate.Path, StringComparison.Ordinal)
            || !string.Equals(commonDirectory, RuntimePaths.Normalize(repository.GitCommonDirectory), StringComparison.Ordinal)
            || !RuntimePaths.Comparer.Equals(Path.GetDirectoryName(gitDirectory), Path.Combine(commonDirectory, "worktrees"))
            || !string.Equals(head, candidate.Registration.Head, StringComparison.Ordinal))
            return "the .git linkage or actual checkout identity no longer matches its Target Repository registration";

        var actualDetached = branch == "HEAD";
        var actualBranch = actualDetached ? null : $"refs/heads/{branch}";
        if (candidate.Registration.Detached != actualDetached
            || !string.Equals(candidate.Registration.Branch, actualBranch, StringComparison.Ordinal))
            return "the actual branch or detached state differs from the recorded worktree registration";
        return null;
    }

    private static async Task<string> ReadGitValueAsync(IProcessRunner processRunner, string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(GitCommand.Create(arguments, workingDirectory), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolExecutionException($"git {string.Join(' ', arguments)} at '{workingDirectory}' exited {result.ExitCode}"
                + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : $": {result.StandardError.TrimEnd()}"));
        var value = result.StandardOutput.TrimEnd('\r', '\n');
        if (value.Length == 0 || value.Contains('\r') || value.Contains('\n'))
            throw new ToolExecutionException($"git {string.Join(' ', arguments)} at '{workingDirectory}' returned an invalid identity");
        return value;
    }

    private static void EnsureSharedRoots(HostPaths paths, ProjectSlug project)
    {
        SharedRuntimeRootValidator.EnsureExistingSafe(paths.WorktreesRoot(project));
        SharedRuntimeRootValidator.EnsureExistingSafe(paths.TmpRoot(project));
    }

    internal static bool SameRegistration(GitWorktreeSnapshot left, GitWorktreeSnapshot right) =>
        string.Equals(left.Path, right.Path, StringComparison.Ordinal)
        && string.Equals(left.Head, right.Head, StringComparison.Ordinal)
        && string.Equals(left.Branch, right.Branch, StringComparison.Ordinal)
        && left.Detached == right.Detached && left.Bare == right.Bare && left.Main == right.Main
        && left.Locked == right.Locked && string.Equals(left.LockReason, right.LockReason, StringComparison.Ordinal)
        && left.Prunable == right.Prunable && string.Equals(left.PruneReason, right.PruneReason, StringComparison.Ordinal);

    private static void AddInspectionDiagnostics(StartupPlan plan, List<StartupCleanupDiagnostic> diagnostics)
    {
        foreach (var diagnostic in plan.Diagnostics.Where(entry => entry.Path is not null
                     && entry.Code is not (StartupDiagnosticCode.ForeignInProgressClaim or StartupDiagnosticCode.UnassignedInProgressClaim
                         or StartupDiagnosticCode.TmpCleanupDeferred)))
            diagnostics.Add(new(diagnostic.BeadId, diagnostic.Path!, diagnostic.Reason ?? diagnostic.Code.ToString()));
    }

    private static bool MatchesPurpose(CompletedEnvironmentObligation record, BeadSnapshot bead) =>
        record.Purpose == WorktreePurpose.Delivery ? bead.IssueType == "task" : bead.IssueType is "feature" or "bug";

    private sealed record CurrentPlan(BeadsInventory Inventory, IReadOnlyList<GitWorktreeSnapshot> Registry,
        RuntimeDirectorySnapshot Worktrees, RuntimeDirectorySnapshot Tmp, StartupPlan Plan);
}
