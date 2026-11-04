using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Execution;

internal sealed record MergerCompletionReceipt(
    BeadWorkflowState ClosedState,
    string PullRequest,
    string CheckedHead,
    string MergeCommit,
    bool CleanupSucceeded,
    bool ProductCompletionSucceeded,
    string? CleanupDiagnostic,
    string? ProductDiagnostic,
    string? DiagnosticPersistenceDiagnostic);

internal sealed record MergerCompletionResult(bool Succeeded, BeadWorkflowState? FinalState,
    MergerCompletionReceipt? Receipt, string? Diagnostic);

/// <summary>Closes a task only after independent merge proof, then attempts housekeeping independently.</summary>
internal sealed class MergerCompletionFinalizer(IProcessRunner processRunner)
{
    private readonly BeadsReader reader = new(processRunner);
    private readonly BeadsWriter writer = new(processRunner);

    internal async Task<MergerCompletionResult> CompleteAsync(AgentStageSession session,
        AgentStageResult invocation, string expectedAssignee, MergerVerifiedMergeEvidence evidence,
        CancellationToken cancellationToken)
    {
        var initial = await ReadTaskAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
        EnsureClaimedMergeTask(initial, invocation, expectedAssignee);
        var evidenceComment = $"nightweaver-verified-merge-v1 pull_request={evidence.PullRequest} checked_head={evidence.CheckedHead} merge_commit={evidence.MergeCommit} required_checks=passed source_comment={evidence.SourceCommentId}";
        await AddCommentVerifiedAsync(session.Repository, session.BeadId, evidenceComment, cancellationToken)
            .ConfigureAwait(false);

        var beforeClose = await ReadTaskAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
        EnsureClaimedMergeTask(beforeClose, invocation, expectedAssignee);
        var expectedState = BeadWorkflowState.Parse(beforeClose);
        ProcessResult? write = null;
        Exception? writeFailure = null;
        try
        {
            write = await writer.CloseVerifiedTaskAsync(session.Repository, session.BeadId, expectedAssignee, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            writeFailure = exception;
        }

        if (write is { ExitCode: 13 })
            return Failed("Guarded Merger task closure was refused because the in-progress owner guard changed.");

        if (write is { ExitCode: 0 })
        {
            try
            {
                var published = BeadsReader.ParsePublicationResult(write.StandardOutput, session.BeadId);
                var publishedState = BeadWorkflowState.Parse(published);
                if (publishedState == expectedState with { Status = "closed" } && published.Assignee is null)
                    return await FinalizeHousekeepingAsync(session, evidence, publishedState, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException) { }
            catch (FormatException) { }
        }

        // A lost/ambiguous response gets one fresh read. The guarded mutation is never replayed.
        var observed = await ReadTaskAsync(session.Repository, session.BeadId, CancellationToken.None).ConfigureAwait(false);
        BeadWorkflowState observedState;
        try { observedState = BeadWorkflowState.Parse(observed); }
        catch (FormatException exception) { return Failed($"Task closure returned an invalid current state: {exception.Message}"); }
        if (observedState == expectedState with { Status = "closed" } && observed.Assignee is null)
            return await FinalizeHousekeepingAsync(session, evidence, observedState, cancellationToken).ConfigureAwait(false);

        return Failed(writeFailure is null
            ? $"Guarded Merger task closure was not confirmed; observed status '{observed.Status}' and owner '{observed.Assignee ?? "(none)"}'. No follow-up mutation was attempted."
            : $"Guarded Merger task closure could not be confirmed after one reread: {writeFailure.Message}");

        MergerCompletionResult Failed(string diagnostic) => new(false, null, null, diagnostic);
    }

    internal async Task<MergerCompletionResult> FinalizeHousekeepingAsync(AgentStageSession session,
        MergerVerifiedMergeEvidence evidence, BeadWorkflowState closedState, CancellationToken cancellationToken)
    {
        string? cleanupDiagnostic = null;
        try { await CleanupOwnedEnvironmentAsync(session, evidence, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { cleanupDiagnostic = Limit(exception.Message); }

        string? productDiagnostic = null;
        var productSucceeded = false;
        try { productSucceeded = await CompleteProductIfEligibleAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { productDiagnostic = Limit(exception.Message); }

        string? diagnosticPersistenceDiagnostic = null;
        if (cleanupDiagnostic is not null || productDiagnostic is not null)
        {
            var diagnostic = $"Verified merge closure completed; cleanup={(cleanupDiagnostic is null ? "completed" : "failed: " + cleanupDiagnostic)}; Product completion={(productDiagnostic is null ? productSucceeded ? "completed or safely ineligible" : "safely ineligible" : "failed: " + productDiagnostic)}.";
            try { await AddCommentVerifiedAsync(session.Repository, session.BeadId, $"nightweaver-finalization-v1 {diagnostic}", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { diagnosticPersistenceDiagnostic = Limit($"Required housekeeping diagnostic could not be persisted: {exception.Message}"); }
        }

        var receipt = new MergerCompletionReceipt(closedState, evidence.PullRequest, evidence.CheckedHead,
            evidence.MergeCommit, cleanupDiagnostic is null, productDiagnostic is null, cleanupDiagnostic, productDiagnostic,
            diagnosticPersistenceDiagnostic);
        var resultDiagnostic = cleanupDiagnostic is null && productDiagnostic is null && diagnosticPersistenceDiagnostic is null ? null
            : $"Verified task closure succeeded; post-merge housekeeping needs attention. Cleanup: {cleanupDiagnostic ?? "completed"}; Product: {productDiagnostic ?? (productSucceeded ? "completed or safely ineligible" : "safely ineligible or already complete")}; diagnostic persistence: {diagnosticPersistenceDiagnostic ?? (cleanupDiagnostic is null && productDiagnostic is null ? "not required" : "recorded")}.";
        return new(true, closedState, receipt, resultDiagnostic);
    }

    internal async Task CleanupOwnedEnvironmentAsync(AgentStageSession session, MergerVerifiedMergeEvidence evidence,
        CancellationToken cancellationToken)
    {
        var paths = session.HostPaths;
        var project = session.Project.Name;
        var repository = session.Repository;
        var beadId = session.BeadId;
        var branchName = $"nightweaver/{beadId}";
        var branchRef = $"refs/heads/{branchName}";
        var worktreePath = RuntimePaths.Normalize(session.Worktree.Path);
        var worktreeRoot = RuntimePaths.Normalize(paths.WorktreesRoot(project));
        if (!RuntimePaths.SafeId(beadId) || !RuntimePaths.Comparer.Equals(RuntimePaths.ChildId(worktreeRoot, worktreePath), beadId)
            || !RuntimePaths.Comparer.Equals(worktreePath, Path.Combine(worktreeRoot, beadId))
            || WorktreeProvisioner.PathsOverlap(worktreePath, repository.Path))
            throw new ToolExecutionException("owned worktree is not the exact safe Bead child under the current Project root");

        var task = await ReadTaskAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
        if (task.Status != "closed" || task.Labels.Contains("needs-human", StringComparer.Ordinal)
            || task.Labels.Contains("product-decision-required", StringComparer.Ordinal))
            throw new ToolExecutionException("completed task is no longer closed and unpaused; owned resources were retained");

        var registry = new GitWorktreeReader(processRunner);
        var commonDirectory = RuntimePaths.Normalize(await ReadGitValueAsync(repository.Path,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken).ConfigureAwait(false));
        var trustedCommonDirectory = RuntimePaths.Normalize(repository.GitCommonDirectory);
        if (RuntimePaths.Comparer.Equals(trustedCommonDirectory, RuntimePaths.Normalize(repository.Path)))
            throw new ToolExecutionException("Target Repository has no validated Git common-directory identity");
        if (!RuntimePaths.Comparer.Equals(trustedCommonDirectory, commonDirectory))
            throw new ToolExecutionException("Target Repository Git common directory changed from its validated identity");
        var registrations = await registry.ReadAsync(repository, cancellationToken).ConfigureAwait(false);
        var registration = registrations.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, worktreePath));
        var runtime = RuntimeDirectoryReader.ReadRoot(worktreeRoot);
        if (runtime.State == DirectoryState.Present)
        {
            var entry = runtime.Entries.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, worktreePath));
            if (entry is null)
            {
                if (registration is not null)
                    throw new ToolExecutionException("worktree registration remains after its canonical directory disappeared; it was retained for safe recovery");
            }
            else
            {
                if (entry.State != DirectoryState.Present || entry.AmbiguousIdentity || entry.Id != beadId)
                    throw new ToolExecutionException(entry.FailureReason ?? "owned worktree directory is unsafe or ambiguous");
                if (registration is null)
                    throw new ToolExecutionException("canonical worktree directory has no matching Git registration");
                if (registration.Main || registration.Bare || registration.Locked || registration.Prunable
                    || registration.Branch != branchRef || !string.Equals(registration.Head, evidence.CheckedHead, StringComparison.OrdinalIgnoreCase))
                    throw new ToolExecutionException("Git registration is locked, prunable, shared, or no longer matches the verified Merger worktree");
                WorktreeProvisioner.ValidateNestedTree(worktreePath);
                var checkoutFailure = await StartupEnvironmentCleaner.ValidateCheckoutIdentityAsync(processRunner, repository,
                    new RemoveWorktree(beadId, registration, DirectoryState.Present, Array.Empty<string>()), cancellationToken)
                    .ConfigureAwait(false);
                if (checkoutFailure is not null) throw new ToolExecutionException(checkoutFailure);

                await EnsureClosedUnpausedTaskAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
                var freshRegistrations = await registry.ReadAsync(repository, cancellationToken).ConfigureAwait(false);
                if (!freshRegistrations.Any(item => SameRegistration(item, registration)))
                    throw new ToolExecutionException("worktree registration changed before targeted removal");
                await RunGitAsync(["worktree", "remove", "--force", worktreePath], repository.Path, cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(worktreePath) || File.Exists(worktreePath)
                    || (await registry.ReadAsync(repository, cancellationToken).ConfigureAwait(false)).Any(item => RuntimePaths.Comparer.Equals(item.Path, worktreePath)))
                    throw new ToolExecutionException("targeted worktree removal did not remove the exact registration and directory");
            }
        }
        else if (runtime.State == DirectoryState.Missing)
        {
            if (registration is not null)
                throw new ToolExecutionException("worktree root disappeared while its exact Git registration remains");
        }
        else throw new ToolExecutionException("shared worktree root became unsafe or unavailable");

        var localRef = await RunGitAllowFailureAsync(["show-ref", "--verify", "--quiet", branchRef], repository.Path, cancellationToken).ConfigureAwait(false);
        if (localRef.ExitCode == 0)
        {
            var localHead = await ReadGitValueAsync(repository.Path, ["rev-parse", branchRef], cancellationToken).ConfigureAwait(false);
            if (!string.Equals(localHead, evidence.CheckedHead, StringComparison.OrdinalIgnoreCase))
                throw new ToolExecutionException("local branch moved since the verified Merger head; it was retained");
            var branchRegistrations = await registry.ReadAsync(repository, cancellationToken).ConfigureAwait(false);
            if (branchRegistrations.Any(item => !item.Main && !item.Bare && item.Branch == branchRef))
                throw new ToolExecutionException("verified local branch is still checked out elsewhere; it was retained");
            await EnsureClosedUnpausedTaskAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
            var deleteLocal = await RunGitAllowFailureAsync(["update-ref", "-d", branchRef, evidence.CheckedHead], repository.Path, cancellationToken)
                .ConfigureAwait(false);
            if (deleteLocal.ExitCode != 0)
                throw new ToolExecutionException($"local branch changed since validation or conditional deletion failed; it was retained: {deleteLocal.StandardError.Trim()}");
            if ((await RunGitAllowFailureAsync(["show-ref", "--verify", "--quiet", branchRef], repository.Path, cancellationToken).ConfigureAwait(false)).ExitCode == 0)
                throw new ToolExecutionException("local branch still exists after targeted deletion");
        }
        else if (localRef.ExitCode != 1) throw new ToolExecutionException("local branch identity could not be checked safely");

        var remoteUrl = await ReadGitValueAsync(repository.Path, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
        var remoteRef = await ReadRemoteBranchAsync(repository.Path, branchRef, cancellationToken).ConfigureAwait(false);
        if (remoteRef is not null)
        {
            if (!string.Equals(remoteRef, evidence.CheckedHead, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(remoteUrl, await ReadGitValueAsync(repository.Path, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                throw new ToolExecutionException("origin branch identity or head changed since merge verification; it was retained");
            await EnsureClosedUnpausedTaskAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
            var delete = await RunGitAllowFailureAsync(["push", $"--force-with-lease={branchRef}:{evidence.CheckedHead}",
                "origin", $":{branchRef}"], repository.Path, cancellationToken).ConfigureAwait(false);
            if (delete.ExitCode != 0 || await ReadRemoteBranchAsync(repository.Path, branchRef, cancellationToken).ConfigureAwait(false) is not null)
                throw new ToolExecutionException("compare-and-delete of the exact owned origin branch was not confirmed");
        }

        var tmpRoot = RuntimePaths.Normalize(paths.TmpRoot(project));
        var beadTmp = RuntimePaths.Normalize(Path.Combine(tmpRoot, beadId));
        if (!RuntimePaths.Comparer.Equals(RuntimePaths.ChildId(tmpRoot, beadTmp), beadId))
            throw new ToolExecutionException("Bead temporary path is not an exact child of the current Project root");
        var tmp = RuntimeDirectoryReader.ReadRoot(tmpRoot);
        if (tmp.State == DirectoryState.Missing) return;
        if (tmp.State != DirectoryState.Present) throw new ToolExecutionException("shared temporary root became unsafe or unavailable");
        var tmpEntry = tmp.Entries.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, beadTmp));
        if (tmpEntry is null) return;
        if (tmpEntry.State != DirectoryState.Present || tmpEntry.AmbiguousIdentity || tmpEntry.Id != beadId)
            throw new ToolExecutionException(tmpEntry.FailureReason ?? "Bead temporary directory is unsafe or ambiguous");
        WorktreeProvisioner.ValidateNestedTree(beadTmp);
        await EnsureClosedUnpausedTaskAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
        Directory.Delete(beadTmp, recursive: true);
        if (Directory.Exists(beadTmp) || File.Exists(beadTmp))
            throw new ToolExecutionException("Bead temporary directory still exists after targeted removal");
    }

    private async Task<bool> CompleteProductIfEligibleAsync(ValidatedTargetRepository repository, string taskId,
        CancellationToken cancellationToken)
    {
        var taskEdges = await reader.ReadDownDependenciesAsync(repository, taskId, cancellationToken).ConfigureAwait(false);
        var first = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var initial = MergerProductEligibility.Evaluate(taskId, taskEdges, first);
        if (initial.Check == MergerProductCheck.MultipleCandidates)
            throw new ToolExecutionException("task has multiple discovered-from Product candidates; none was closed");
        if (initial.Check == MergerProductCheck.AlreadyClosed) return true;
        if (initial.Check != MergerProductCheck.NeedLinkedTasks || initial.ProductId is null) return false;
        var productId = initial.ProductId;

        var linkedEdges = await reader.ReadUpDependenciesAsync(repository, productId, cancellationToken).ConfigureAwait(false);
        var current = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var currentCheck = MergerProductEligibility.Evaluate(taskId, taskEdges, current, linkedEdges);
        if (currentCheck.Check != MergerProductCheck.Ready) return currentCheck.Check == MergerProductCheck.AlreadyClosed;
        var taskEdgesAgain = await reader.ReadDownDependenciesAsync(repository, taskId, cancellationToken).ConfigureAwait(false);
        if (!taskEdgesAgain.Where(edge => edge.Type == "discovered-from" && edge.IssueId == taskId)
                .Select(edge => edge.DependsOnId).Order(StringComparer.Ordinal)
                .SequenceEqual(taskEdges.Where(edge => edge.Type == "discovered-from" && edge.IssueId == taskId)
                    .Select(edge => edge.DependsOnId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)) return false;
        var linkedAgain = await reader.ReadUpDependenciesAsync(repository, productId, cancellationToken).ConfigureAwait(false);
        if (!linkedAgain.Where(edge => edge.Type == "discovered-from" && edge.DependsOnId == productId)
                .Select(edge => edge.IssueId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(linkedEdges.Where(edge => edge.Type == "discovered-from" && edge.DependsOnId == productId)
                    .Select(edge => edge.IssueId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)) return false;

        var latest = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var latestCheck = MergerProductEligibility.Evaluate(taskId, taskEdgesAgain, latest, linkedAgain);
        if (latestCheck.Check == MergerProductCheck.AlreadyClosed) return true;
        if (latestCheck.Check != MergerProductCheck.Ready) return false;
        ProcessResult? write = null;
        Exception? writeFailure = null;
        try { write = await writer.ClosePlannedProductAsync(repository, productId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ToolExecutionException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { writeFailure = exception; }
        if (write is { ExitCode: 0 })
        {
            try
            {
                var result = BeadsReader.ParsePublicationResult(write.StandardOutput, productId);
                if (result.Status == "closed" && result.Assignee is null) return true;
            }
            catch (System.Text.Json.JsonException) { }
            catch (FormatException) { }
        }
        var attemptFailure = writeFailure is not null
            ? $"command error: {Limit(writeFailure.Message)}"
            : write is null
                ? "command returned no result"
                : write.ExitCode == 13
                    ? $"guard refused the update (exit 13): {Limit(write.StandardError)}"
                    : write.ExitCode != 0
                        ? $"bd update exited {write.ExitCode}: {Limit(write.StandardError)}"
                        : $"bd returned an unconfirmed or malformed success response (exit 0): {Limit(write.StandardOutput)}";
        BeadsInventory after;
        try { after = await reader.ReadSnapshotAsync(repository, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ToolExecutionException($"Planned Product close was attempted but could not be confirmed after one fresh read; {attemptFailure}; reread failed: {Limit(exception.Message)}");
        }
        var afterCheck = MergerProductEligibility.Evaluate(taskId, taskEdgesAgain, after, linkedAgain);
        if (afterCheck.Check == MergerProductCheck.AlreadyClosed) return true;
        var observed = after.ById.TryGetValue(productId, out var observedProduct)
            ? $"observed status={observedProduct.Status}, assignee={observedProduct.Assignee ?? "(none)"}, labels=[{string.Join(',', observedProduct.Labels)}]"
            : "Product was absent from the fresh inventory";
        throw new ToolExecutionException($"Planned Product close was attempted but not confirmed after one fresh read; {attemptFailure}; {observed}.");
    }

    private async Task<BeadSnapshot> ReadTaskAsync(ValidatedTargetRepository repository, string beadId,
        CancellationToken cancellationToken)
    {
        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Merger task '{beadId}' disappeared during completion finalization.");
        return bead;
    }

    private async Task EnsureClosedUnpausedTaskAsync(ValidatedTargetRepository repository, string beadId,
        CancellationToken cancellationToken)
    {
        var task = await ReadTaskAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
        if (task.Status != "closed" || task.Assignee is not null
            || task.Labels.Contains("needs-human", StringComparer.Ordinal)
            || task.Labels.Contains("product-decision-required", StringComparer.Ordinal))
            throw new ToolExecutionException("task state changed before an owned resource removal");
    }

    private static void EnsureClaimedMergeTask(BeadSnapshot bead, AgentStageResult invocation, string expectedAssignee)
    {
        var state = BeadWorkflowState.Parse(bead);
        if (bead.IssueType != "task" || state.Stage != "stage:merge" || state.Status != "in_progress"
            || state != invocation.Observations!.BeforeState
            || !string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal))
            throw new AgentStageInputException($"Merger completion for '{bead.Id}' was refused because its stage, repair counter, status, or claim owner changed.");
    }

    private async Task AddCommentVerifiedAsync(ValidatedTargetRepository repository, string beadId, string comment,
        CancellationToken cancellationToken)
    {
        var existing = await reader.ReadCommentsAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
        if (existing.Any(item => item.Text == comment)) return;
        Exception? failure = null;
        try { await writer.AddCommentAsync(repository, beadId, comment, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException)
        { failure = exception; }
        var after = await reader.ReadCommentsAsync(repository, beadId, CancellationToken.None).ConfigureAwait(false);
        if (after.Any(item => item.Text == comment)) return;
        throw new ToolExecutionException(failure is null
            ? $"Required completion evidence/diagnostic for '{beadId}' was not observable after writing."
            : $"Required completion evidence/diagnostic for '{beadId}' could not be persisted: {failure.Message}");
    }

    private async Task<string?> ReadRemoteBranchAsync(string repositoryPath, string branchRef, CancellationToken cancellationToken)
    {
        var result = await RunGitAllowFailureAsync(["ls-remote", "--heads", "origin", branchRef], repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0) throw new ToolExecutionException("origin branch state could not be checked safely");
        var fields = result.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0) return null;
        if (fields.Length != 2 || fields[1] != branchRef || fields[0].Length is not (40 or 64) || !fields[0].All(Uri.IsHexDigit))
            throw new ToolExecutionException("origin returned an ambiguous ref result");
        return fields[0];
    }

    private async Task<string> ReadGitValueAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        var value = result.StandardOutput.TrimEnd('\r', '\n');
        if (value.Length == 0 || value.Contains('\r') || value.Contains('\n'))
            throw new ToolExecutionException($"git {string.Join(' ', arguments)} returned an invalid identity");
        return value;
    }

    private async Task<ProcessResult> RunGitAsync(IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(GitCommand.Create(arguments, workingDirectory), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new ToolExecutionException($"git {string.Join(' ', arguments)} exited {result.ExitCode}: {result.StandardError}");
        return result;
    }

    private Task<ProcessResult> RunGitAllowFailureAsync(IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(GitCommand.Create(arguments, workingDirectory), cancellationToken);

    private static bool SameRegistration(GitWorktreeSnapshot left, GitWorktreeSnapshot right) =>
        left.Path == right.Path && left.Head == right.Head && left.Branch == right.Branch && left.Detached == right.Detached
        && left.Bare == right.Bare && left.Main == right.Main && left.Locked == right.Locked && left.Prunable == right.Prunable;

    private static string Limit(string value) => value.Length <= 384 ? value : value[..384];
}
