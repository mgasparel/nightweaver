using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record PauseCleanupPlan(
    bool Safe,
    string? Refusal,
    string WorktreePath,
    string TemporaryPath,
    string BranchName,
    WorktreePurpose Purpose,
    bool WorktreePresent,
    bool RegistrationPresent,
    bool TemporaryPresent,
    bool? LocalBranchPresent);

/// <summary>Preflights and removes only the runtime environment derived for one paused Bead.</summary>
internal sealed class PauseEnvironmentCleaner
{
    private readonly IProcessRunner processRunner;
    private readonly GitWorktreeReader worktreeReader;
    private readonly Action<string>? beforeDestructiveOperation;

    internal PauseEnvironmentCleaner(IProcessRunner processRunner, Action<string>? beforeDestructiveOperation = null)
    {
        this.processRunner = processRunner;
        this.beforeDestructiveOperation = beforeDestructiveOperation;
        worktreeReader = new GitWorktreeReader(processRunner);
    }

    internal async Task<PauseCleanupPlan> InspectAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(paths, project, repository, beadId);
        var worktreePath = RuntimePaths.Normalize(Path.Combine(paths.WorktreesRoot(project), beadId));
        var temporaryPath = RuntimePaths.Normalize(Path.Combine(paths.TmpRoot(project), beadId));
        var branchName = $"nightweaver/{beadId}";
        try
        {
            var worktree = InspectRuntimeRoot(paths.WorktreesRoot(project), worktreePath, beadId);
            var temporary = InspectRuntimeRoot(paths.TmpRoot(project), temporaryPath, beadId);
            var registrations = await worktreeReader.ReadAsync(repository, cancellationToken);
            var registered = ValidateRegistrations(repository, worktreePath, branchName, purpose, worktree, registrations);
            bool? localBranchPresent = purpose == WorktreePurpose.Delivery
                ? await ProbeLocalBranchAsync(repository, branchName, cancellationToken)
                : null;
            return new(true, null, worktreePath, temporaryPath, branchName, purpose,
                worktree.TargetPresent, registered, temporary.TargetPresent, localBranchPresent);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(false, $"Pause cleanup preflight refused: {exception.Message}", worktreePath, temporaryPath,
                branchName, purpose, false, false, false, purpose == WorktreePurpose.Delivery ? false : null);
        }
    }

    internal async Task CleanAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        PauseCleanupPlan initial,
        CancellationToken cancellationToken)
    {
        if (!initial.Safe) throw new ToolExecutionException(initial.Refusal ?? "Pause cleanup was not preflighted safely.");
        cancellationToken.ThrowIfCancellationRequested();

        if (initial.WorktreePresent)
        {
            beforeDestructiveOperation?.Invoke("git worktree remove");
            var current = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
            if (current.WorktreePresent)
            {
                if (!current.RegistrationPresent)
                    throw Refusal("remove worktree", initial.WorktreePath, "the destination is no longer registered");
                await RunGitRequiredAsync(repository, ["worktree", "remove", "--force", initial.WorktreePath],
                    "remove owned worktree", initial.WorktreePath, cancellationToken);
                current = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
                if (current.WorktreePresent || current.RegistrationPresent)
                    throw Refusal("verify worktree removal", initial.WorktreePath, "the worktree directory or registration remains");
            }
        }

        if (initial.Purpose == WorktreePurpose.Delivery)
        {
            var current = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
            if (current.LocalBranchPresent == true)
            {
                beforeDestructiveOperation?.Invoke("git branch -D");
                current = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
                if (current.LocalBranchPresent == true)
                {
                    if (current.RegistrationPresent)
                        throw Refusal("delete local Delivery branch", initial.WorktreePath, "the branch still has an owned worktree registration");
                    await RunGitRequiredAsync(repository, ["branch", "-D", initial.BranchName],
                        "delete local Delivery branch", initial.WorktreePath, cancellationToken);
                    current = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
                    if (current.LocalBranchPresent == true)
                        throw Refusal("verify local Delivery branch deletion", initial.WorktreePath, "the local branch remains");
                }
            }
        }

        // Prune is repository-wide, so do it only when this Bead had an owned Git resource to remove.
        var needsPrune = initial.WorktreePresent || initial.RegistrationPresent || initial.LocalBranchPresent == true;
        if (needsPrune)
        {
            beforeDestructiveOperation?.Invoke("git worktree prune");
            _ = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
            await RunGitRequiredAsync(repository, ["worktree", "prune"], "prune worktree registrations",
                initial.WorktreePath, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var beforeTmpDelete = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
        if (beforeTmpDelete.TemporaryPresent)
        {
            beforeDestructiveOperation?.Invoke("delete Bead temporary directory");
            beforeTmpDelete = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
            if (beforeTmpDelete.TemporaryPresent)
                TemporaryTreeDeleter.Delete(beforeTmpDelete.TemporaryPath,
                    _ =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        beforeDestructiveOperation?.Invoke("delete temporary entry");
                        cancellationToken.ThrowIfCancellationRequested();
                    });
        }

        var final = await ReinspectAsync(paths, project, repository, beadId, initial, cancellationToken);
        if (final.WorktreePresent || final.RegistrationPresent || final.TemporaryPresent || final.LocalBranchPresent == true)
            throw Refusal("verify pause cleanup", initial.WorktreePath, "one or more owned runtime resources remain");
    }

    private async Task<PauseCleanupPlan> ReinspectAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        PauseCleanupPlan initial,
        CancellationToken cancellationToken)
    {
        var current = await InspectAsync(paths, project, repository, beadId, initial.Purpose, cancellationToken);
        if (!current.Safe) throw Refusal("revalidate pause cleanup", initial.WorktreePath, current.Refusal ?? "runtime identity changed");
        EnsureNoNewResources(initial, current);
        return current;
    }

    private static void EnsureNoNewResources(PauseCleanupPlan initial, PauseCleanupPlan current)
    {
        if ((!initial.WorktreePresent && current.WorktreePresent)
            || (!initial.RegistrationPresent && current.RegistrationPresent)
            || (!initial.TemporaryPresent && current.TemporaryPresent)
            || (initial.LocalBranchPresent == false && current.LocalBranchPresent == true))
            throw Refusal("revalidate pause cleanup", initial.WorktreePath, "a new resource appeared after cleanup preflight");
    }

    private static void ValidateIdentity(HostPaths paths, ProjectSlug project, ValidatedTargetRepository repository, string beadId)
    {
        if (!WorktreeProvisioner.IsSafeBeadId(beadId))
            throw new AgentStageInputException($"Bead ID '{beadId}' is not a safe managed runtime path segment.");
        var repoRoot = RuntimePaths.Normalize(repository.Path);
        var worktreePath = RuntimePaths.Normalize(Path.Combine(paths.WorktreesRoot(project), beadId));
        var temporaryPath = RuntimePaths.Normalize(Path.Combine(paths.TmpRoot(project), beadId));
        if (!string.Equals(Path.GetFileName(worktreePath), beadId, StringComparison.Ordinal)
            || !RuntimePaths.Comparer.Equals(Path.GetDirectoryName(worktreePath), RuntimePaths.Normalize(paths.WorktreesRoot(project)))
            || !string.Equals(Path.GetFileName(temporaryPath), beadId, StringComparison.Ordinal)
            || !RuntimePaths.Comparer.Equals(Path.GetDirectoryName(temporaryPath), RuntimePaths.Normalize(paths.TmpRoot(project)))
            || WorktreeProvisioner.PathsOverlap(worktreePath, repoRoot)
            || WorktreeProvisioner.PathsOverlap(temporaryPath, repoRoot))
            throw new AgentStageInputException($"Derived pause cleanup paths for Bead '{beadId}' are not canonical direct children outside the Target Repository root.");
    }

    private static RuntimeRootState InspectRuntimeRoot(string root, string targetPath, string beadId)
    {
        var snapshot = RuntimeDirectoryReader.ReadRoot(root);
        if (snapshot.State == DirectoryState.UnsafeLink)
            throw Refusal("inspect runtime root", root, "an ancestor is a reparse point, junction, or symbolic link");
        if (snapshot.State == DirectoryState.Missing) return new(false);

        var targetPresent = false;
        foreach (var entry in snapshot.Entries)
        {
            if (entry.State != DirectoryState.Present || entry.AmbiguousIdentity || !WorktreeProvisioner.IsSafeBeadId(entry.Id))
                throw Refusal("inspect runtime root", entry.Path, "runtime entry has an unsafe or ambiguous identity");
            WorktreeProvisioner.ValidateNestedTree(entry.Path);
            if (!RuntimePaths.Comparer.Equals(entry.Path, targetPath)) continue;
            if (!string.Equals(entry.Id, beadId, StringComparison.Ordinal) || !string.Equals(entry.Path, targetPath, StringComparison.Ordinal))
                throw Refusal("validate runtime entry", entry.Path, "runtime entry aliases the requested Bead path");
            targetPresent = true;
        }
        return new(targetPresent);
    }

    private static bool ValidateRegistrations(ValidatedTargetRepository repository, string targetPath, string branchName,
        WorktreePurpose purpose, RuntimeRootState runtime, IReadOnlyList<GitWorktreeSnapshot> registrations)
    {
        var repositoryRoot = RuntimePaths.Normalize(repository.Path);
        var main = registrations.Where(item => RuntimePaths.Comparer.Equals(item.Path, repositoryRoot)).ToArray();
        if (main.Length != 1 || !main[0].Main || main[0].Bare || main[0].Locked || main[0].Prunable
            || !string.Equals(main[0].Path, repositoryRoot, StringComparison.Ordinal))
            throw Refusal("validate Target Repository registration", repositoryRoot,
                $"the root checkout does not have one healthy canonical main worktree registration; observed {string.Join("; ", registrations.Select(item => $"'{item.RawPath}' normalized '{item.Path}' main={item.Main} bare={item.Bare} locked={item.Locked} prunable={item.Prunable}"))}");

        GitWorktreeSnapshot? target = null;
        foreach (var registration in registrations)
        {
            if (RuntimePaths.Comparer.Equals(registration.Path, targetPath))
            {
                if (!string.Equals(registration.Path, targetPath, StringComparison.Ordinal) || target is not null)
                    throw Refusal("validate worktree registration", registration.RawPath, "registration aliases or duplicates the canonical destination");
                target = registration;
                continue;
            }
            if (WorktreeProvisioner.PathsOverlap(registration.Path, targetPath))
                throw Refusal("validate worktree registration", registration.RawPath, "another registered worktree overlaps the canonical destination");
            if (purpose == WorktreePurpose.Delivery && string.Equals(registration.Branch, $"refs/heads/{branchName}", StringComparison.Ordinal))
                throw Refusal("validate Delivery branch ownership", registration.RawPath, "the local branch is registered outside the canonical Bead worktree");
        }

        if (target is null)
        {
            if (runtime.TargetPresent)
                throw Refusal("validate worktree destination", targetPath, "an unregistered directory occupies the canonical destination");
            return false;
        }
        if (!runtime.TargetPresent)
            throw Refusal("validate worktree registration", targetPath, "the registered worktree directory is missing");
        if (target.Main || target.Bare || target.Locked || target.Prunable)
            throw Refusal("validate worktree registration", targetPath, "the registration is main, bare, locked, or prunable");
        if (purpose == WorktreePurpose.Planning && (!target.Detached || target.Branch is not null))
            throw Refusal("validate Planning worktree ownership", targetPath, "Planning worktree is not detached");
        if (purpose == WorktreePurpose.Delivery && !string.Equals(target.Branch, $"refs/heads/{branchName}", StringComparison.Ordinal))
            throw Refusal("validate Delivery worktree ownership", targetPath, $"expected local branch 'refs/heads/{branchName}'");
        return true;
    }

    private async Task<bool> ProbeLocalBranchAsync(ValidatedTargetRepository repository, string branchName, CancellationToken cancellationToken)
    {
        var reference = $"refs/heads/{branchName}";
        var result = await RunGitAsync(repository, ["show-ref", "--verify", "--quiet", reference],
            "inspect local Delivery branch", repository.Path, cancellationToken);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardError)) return false;
        throw Refusal("inspect local Delivery branch", repository.Path, $"Git exited {result.ExitCode} while checking '{reference}'", result.StandardError);
    }

    private async Task RunGitRequiredAsync(ValidatedTargetRepository repository, IReadOnlyList<string> arguments,
        string operation, string path, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repository, arguments, operation, path, cancellationToken);
        if (result.ExitCode != 0)
            throw Refusal(operation, path, $"git {string.Join(' ', arguments)} exited {result.ExitCode}", result.StandardError);
    }

    private async Task<ProcessResult> RunGitAsync(ValidatedTargetRepository repository, IReadOnlyList<string> arguments,
        string operation, string path, CancellationToken cancellationToken)
    {
        try { return await processRunner.RunAsync(GitCommand.Create(arguments, repository.Path), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception) { throw Refusal(operation, path, exception.Message, exception.StandardError); }
    }

    private static ToolExecutionException Refusal(string operation, string path, string reason, string? stderr = null) =>
        new($"{operation} at '{path}' was refused or failed: {reason}"
            + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"; stderr: {stderr.TrimEnd()}"), stderr);

    private sealed record RuntimeRootState(bool TargetPresent);
}
