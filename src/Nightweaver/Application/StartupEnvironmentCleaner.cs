using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Application;

internal sealed record StartupCleanupDiagnostic(string BeadId, string Path, string Reason);

/// <summary>Removes only freshly revalidated completed environments during live startup.</summary>
internal sealed class StartupEnvironmentCleaner(BeadsReader reader, BeadsWriter writer, IProcessRunner processRunner)
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(30);

    internal async Task<IReadOnlyList<StartupCleanupDiagnostic>> CleanAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, string actor, CancellationToken cancellationToken)
    {
        var diagnostics = new List<StartupCleanupDiagnostic>();
        var protectedIds = new HashSet<string>(RuntimePaths.Comparer);
        EnsureSharedRoots(paths, project);

        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var registry = await new GitWorktreeReader(processRunner).ReadAsync(repository, cancellationToken).ConfigureAwait(false);
        var worktrees = RuntimeDirectoryReader.ReadRoot(paths.WorktreesRoot(project));
        var tmp = RuntimeDirectoryReader.ReadRoot(paths.TmpRoot(project));
        var plan = StartupPlanner.PlanForStartup(actor, inventory, registry, worktrees, tmp);
        AddInspectionDiagnostics(plan, diagnostics);

        foreach (var candidate in plan.Removals)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                await EscalateFailureAsync(repository, actor, currentCandidate.BeadId, currentCandidate.Path,
                    identityFailure, diagnostics, cancellationToken).ConfigureAwait(false);
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
                await EscalateFailureAsync(repository, actor, currentCandidate.BeadId, currentCandidate.Path,
                    exception.Message, diagnostics, cancellationToken).ConfigureAwait(false);
                protectedIds.Add(currentCandidate.BeadId);
                continue;
            }

            if (removal.ExitCode != 0)
            {
                var reason = string.IsNullOrWhiteSpace(removal.StandardError)
                    ? $"git worktree remove exited {removal.ExitCode}"
                    : $"git worktree remove exited {removal.ExitCode}: {removal.StandardError.TrimEnd()}";
                await EscalateFailureAsync(repository, actor, currentCandidate.BeadId, currentCandidate.Path,
                    reason, diagnostics, cancellationToken).ConfigureAwait(false);
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
                await EscalateFailureAsync(repository, actor, currentCandidate.BeadId, currentCandidate.Path,
                    reason, diagnostics, cancellationToken).ConfigureAwait(false);
                protectedIds.Add(currentCandidate.BeadId);
            }
        }

        // Tmp ownership is determined only after all worktree commands have settled.
        var postWorktree = await ReadCurrentPlanAsync(paths, project, repository, actor, cancellationToken).ConfigureAwait(false);
        AddInspectionDiagnostics(postWorktree.Plan, diagnostics);
        foreach (var candidate in postWorktree.Plan.OrphanTmp)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                await EscalateFailureAsync(repository, actor, candidate.BeadId, candidate.Path,
                    "temporary directory identity changed or became unsafe before deletion", diagnostics, cancellationToken)
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
                || !StartupEnvironmentClassifier.IsCompletedTmpOwner(bead, actor))
            {
                diagnostics.Add(new(candidate.BeadId, candidate.Path,
                    "temporary files were retained because the Bead is missing, paused, or no longer completed"));
                continue;
            }
            var finalRegistry = await new GitWorktreeReader(processRunner).ReadAsync(repository, cancellationToken).ConfigureAwait(false);
            var finalWorktrees = RuntimeDirectoryReader.ReadRoot(paths.WorktreesRoot(project));
            if (finalWorktrees.State != DirectoryState.Present)
                throw new ToolExecutionException($"shared worktree root '{finalWorktrees.Path}' became unavailable or unsafe during temporary cleanup");
            var stillProtected = finalRegistry.Any(entry => !entry.Main && !entry.Bare
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
                await EscalateFailureAsync(repository, actor, candidate.BeadId, candidate.Path,
                    "the temporary directory changed identity or became unsafe before deletion", diagnostics, cancellationToken)
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
                await EscalateFailureAsync(repository, actor, candidate.BeadId, candidate.Path,
                    exception.Message, diagnostics, cancellationToken).ConfigureAwait(false);
                protectedIds.Add(candidate.BeadId);
            }
        }

        return diagnostics.Distinct().ToList().AsReadOnly();
    }

    private async Task<CurrentPlan> ReadCurrentPlanAsync(HostPaths paths, ProjectSlug project,
        ValidatedTargetRepository repository, string actor, CancellationToken cancellationToken)
    {
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
        if (WorktreeProvisioner.PathsOverlap(candidate.Path, repository.Path))
            return "the candidate worktree overlaps the Target Repository";

        var worktreeRoot = RuntimeDirectoryReader.ReadRoot(root);
        if (worktreeRoot.State != DirectoryState.Present)
            throw new ToolExecutionException($"shared runtime root '{root}' became unavailable or unsafe during startup cleanup");
        var entry = worktreeRoot.Entries.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, candidate.Path));
        if (entry is null || entry.State != DirectoryState.Present || entry.AmbiguousIdentity
            || !string.Equals(entry.Id, candidate.BeadId, StringComparison.Ordinal))
            return entry?.FailureReason ?? "the canonical worktree directory is missing, linked, unusable, or ambiguous";

        try { WorktreeProvisioner.ValidateNestedTree(candidate.Path); }
        catch (ToolExecutionException exception) { return exception.Message; }

        string topLevel;
        string gitDirectory;
        string commonDirectory;
        string branch;
        string head;
        try
        {
            topLevel = await ReadGitValueAsync(candidate.Path, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
            gitDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(candidate.Path,
                ["rev-parse", "--path-format=absolute", "--git-dir"], cancellationToken).ConfigureAwait(false));
            commonDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(candidate.Path,
                ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken).ConfigureAwait(false));
            branch = await ReadGitValueAsync(candidate.Path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);
            head = await ReadGitValueAsync(candidate.Path, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
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

    private async Task<string> ReadGitValueAsync(string workingDirectory, IReadOnlyList<string> arguments,
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

    private async Task EscalateFailureAsync(ValidatedTargetRepository repository, string actor, string beadId,
        string path, string reason, List<StartupCleanupDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var comment = $"Nightweaver startup retained an environment after cleanup failed.\nPath: {path}\nReason: {reason}";
        var currentInventory = await ReadForVerificationAsync(repository).ConfigureAwait(false);
        if (!currentInventory.ById.TryGetValue(beadId, out var current))
        {
            diagnostics.Add(new(beadId, path, $"cleanup failed and the Bead no longer exists: {reason}"));
            return;
        }

        if (!StartupEnvironmentClassifier.IsPaused(current))
        {
            Exception? writeFailure = null;
            try
            {
                await writer.EscalateStartupCleanupFailureAsync(repository, beadId, current.Status, current.Assignee,
                    actor, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ToolExecutionException or IOException
                or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                writeFailure = exception;
            }

            var observed = await ReadForVerificationAsync(repository).ConfigureAwait(false);
            if (observed.ById.TryGetValue(beadId, out var afterWrite)
                && StartupEnvironmentClassifier.IsPaused(afterWrite))
            {
                // A failed command can still have committed; current tracker state is authoritative.
                current = afterWrite;
            }
            else if (writeFailure is not null)
            {
                if (!observed.ById.TryGetValue(beadId, out var latest))
                    throw new ToolExecutionException($"startup cleanup could not confirm Bead '{beadId}' after an uncertain escalation write: it disappeared");
                if (latest.Status == current.Status && latest.Assignee == current.Assignee)
                    throw new ToolExecutionException($"startup cleanup could not persist needs-human for Bead '{beadId}': {writeFailure.Message}");
                current = latest; // A guarded mismatch is a concurrent state change; do not replay it.
            }
            else if (!observed.ById.TryGetValue(beadId, out current!))
            {
                throw new ToolExecutionException($"startup cleanup could not confirm Bead '{beadId}' after escalation; it disappeared");
            }
        }

        try
        {
            await writer.AddStartupCleanupCommentAsync(repository, beadId, comment, actor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolExecutionException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new ToolExecutionException($"startup cleanup could not persist the required path/reason diagnostic for Bead '{beadId}': {exception.Message}");
        }

        var confirmation = await ReadForVerificationAsync(repository).ConfigureAwait(false);
        if (!confirmation.ById.TryGetValue(beadId, out var confirmed))
            throw new ToolExecutionException($"startup cleanup could not confirm diagnostic for Bead '{beadId}': the Bead disappeared");
        if (!StartupEnvironmentClassifier.IsPaused(confirmed)
            && (confirmed.Status != current.Status || confirmed.Assignee != current.Assignee))
            diagnostics.Add(new(beadId, path, $"cleanup failed; state changed concurrently and was preserved: {reason}"));
        else if (!StartupEnvironmentClassifier.IsPaused(confirmed))
            throw new ToolExecutionException($"startup cleanup did not confirm a human pause for Bead '{beadId}' after local cleanup failed");

        var comments = await reader.ReadCommentsAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
        if (!comments.Any(existing => string.Equals(existing.Text, comment, StringComparison.Ordinal)))
            throw new ToolExecutionException($"startup cleanup did not confirm the required diagnostic comment for Bead '{beadId}'");

        // A late exact Project claim can be released, but no foreign or terminal claim is changed.
        if (confirmed.Status == "in_progress" && string.Equals(confirmed.Assignee, actor, StringComparison.Ordinal))
        {
            GuardedStrandedClaimReleaseResult result;
            try
            {
                result = await writer.ReleaseStrandedClaimAsync(repository, beadId, actor, cancellationToken).ConfigureAwait(false);
            }
            catch (ToolExecutionException exception)
            {
                var reread = await ReadForVerificationAsync(repository).ConfigureAwait(false);
                if (reread.ById.TryGetValue(beadId, out var afterRelease)
                    && !(afterRelease.Status == "in_progress" && string.Equals(afterRelease.Assignee, actor, StringComparison.Ordinal)))
                    result = GuardedStrandedClaimReleaseResult.GuardMismatch;
                else
                    throw new ToolExecutionException($"startup cleanup could not confirm release of its exact Project claim for Bead '{beadId}': {exception.Message}");
            }

            if (result == GuardedStrandedClaimReleaseResult.Released)
            {
                var released = await ReadForVerificationAsync(repository).ConfigureAwait(false);
                if (released.ById.TryGetValue(beadId, out var afterRelease)
                    && afterRelease.Status == "in_progress" && string.Equals(afterRelease.Assignee, actor, StringComparison.Ordinal))
                    throw new ToolExecutionException($"startup cleanup did not confirm release of its exact Project claim for Bead '{beadId}'");
            }
        }

        diagnostics.Add(new(beadId, path, reason));
    }

    private async Task<BeadsInventory> ReadForVerificationAsync(ValidatedTargetRepository repository)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try { return await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception)
        {
            throw new ToolExecutionException($"startup cleanup could not confirm current tracker state: {exception.Message}");
        }
    }

    private static void EnsureSharedRoots(HostPaths paths, ProjectSlug project)
    {
        SharedRuntimeRootValidator.EnsureExistingSafe(paths.WorktreesRoot(project));
        SharedRuntimeRootValidator.EnsureExistingSafe(paths.TmpRoot(project));
    }

    private static bool SameRegistration(GitWorktreeSnapshot left, GitWorktreeSnapshot right) =>
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

    private sealed record CurrentPlan(BeadsInventory Inventory, IReadOnlyList<GitWorktreeSnapshot> Registry,
        RuntimeDirectorySnapshot Worktrees, RuntimeDirectorySnapshot Tmp, StartupPlan Plan);
}
