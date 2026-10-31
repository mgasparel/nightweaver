using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum WorktreePurpose
{
    Planning,
    Delivery
}

internal sealed record ProvisionedWorktree(string Path, string HeadSha, bool Reused = false, bool WasDirty = false);

/// <summary>Creates or safely reuses the per-Bead isolated checkout.</summary>
internal sealed class WorktreeProvisioner(IProcessRunner processRunner)
{
    internal async Task<ProvisionedWorktree> ProvisionAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        var destination = GetDestination(hostPaths, project, repository, beadId);
        var branch = $"nightweaver/{beadId}";
        var branchRef = $"refs/heads/{branch}";

        // Complete all filesystem and registration checks before any Target Repository mutation.
        var state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        if (purpose == WorktreePurpose.Delivery)
        {
            var format = await RunGitAsync(["check-ref-format", branchRef], repository.Path,
                "validate generated Delivery branch", destination, cancellationToken);
            if (format.ExitCode != 0)
                throw Failure("validate generated Delivery branch", destination, "Git rejected the generated branch name", format.StandardError);
            if (state.Registration is null && await RefExistsAsync(repository.Path, branchRef,
                    "check for an existing local Delivery branch", destination, cancellationToken))
                throw new BeadEnvironmentException(destination,
                    $"local branch '{branchRef}' already exists without its canonical registered worktree");
        }

        if (state.Registration is not null)
        {
            // A retained checkout is the work being resumed. It is validated in place and never
            // fetched, checked out, reset, or cleaned during dispatch preparation.
            return await VerifyAndReturnAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, expectedPlanningHead: null, allowDirty: true, reused: true, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Recheck ownership directly before fetching; fetch is the only root-checkout mutation here.
        state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        var fetchArgs = purpose == WorktreePurpose.Planning
            ? new[] { "fetch", "origin", repository.DefaultBranch }
            : new[] { "fetch", "origin" };
        await RunRequiredGitAsync(fetchArgs, repository.Path, "fetch origin", destination, cancellationToken);

        var defaultRef = $"refs/remotes/origin/{repository.DefaultBranch}";
        string? planningHead = null;
        string? deliverySource = null;
        if (purpose == WorktreePurpose.Planning)
        {
            planningHead = await ResolveCommitAsync(repository.Path, defaultRef, "resolve fetched Planning base", destination, cancellationToken);
        }
        else if (state.Registration is null)
        {
            var remoteBranchRef = $"refs/remotes/origin/{branch}";
            if (await RefExistsAsync(repository.Path, remoteBranchRef, "probe fetched Delivery branch", destination, cancellationToken))
            {
                // Validate the ref as a commit now, before worktree add can create anything.
                _ = await ResolveCommitAsync(repository.Path, remoteBranchRef, "resolve fetched Delivery branch", destination, cancellationToken);
                deliverySource = remoteBranchRef;
            }
            else
            {
                _ = await ResolveCommitAsync(repository.Path, defaultRef, "resolve fetched Delivery base", destination, cancellationToken);
                deliverySource = defaultRef;
            }
        }

        state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        if (state.Registration is null)
        {
            EnsureMissingParentsAreSafe(hostPaths.WorktreesRoot(project), destination);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new BeadEnvironmentException(destination, $"could not create the worktree parent: {exception.Message}");
            }

            // Directory creation can race with unrelated filesystem changes; revalidate before worktree add.
            state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
            if (state.Registration is not null || Directory.Exists(destination) || File.Exists(destination))
                throw Failure("create isolated worktree", destination, "destination became occupied before registration");

            string[] addArgs;
            if (purpose == WorktreePurpose.Planning)
            {
                addArgs = ["worktree", "add", "--detach", destination, defaultRef];
            }
            else if (string.Equals(deliverySource, defaultRef, StringComparison.Ordinal))
            {
                addArgs = ["worktree", "add", "--no-track", "-b", branch, destination, defaultRef];
            }
            else
            {
                addArgs = ["worktree", "add", "--track", "-b", branch, destination, deliverySource!];
            }
            await RunRequiredGitAsync(addArgs, repository.Path, "add isolated worktree", destination, cancellationToken);
        }
        if (purpose == WorktreePurpose.Planning)
        {
            // A fresh Planning worktree was added at the fetched ref and is already clean.
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: true, cancellationToken);
        }

        return await VerifyAndReturnAsync(hostPaths, project, repository, destination, beadId, branchRef,
            purpose, planningHead, allowDirty: false, reused: false, cancellationToken);
    }

    private async Task<WorkspaceState> ValidateWorkspaceAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string destination,
        string beadId,
        string branchRef,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var worktreesRoot = RuntimePaths.Normalize(hostPaths.WorktreesRoot(project));
        var repositoryRoot = RuntimePaths.Normalize(repository.Path);
        if (PathsOverlap(destination, repositoryRoot))
            throw Failure("validate isolated worktree location", destination,
                $"managed worktree path overlaps Target Repository root '{repositoryRoot}'");

        var runtime = RuntimeDirectoryReader.ReadRoot(worktreesRoot);
        if (runtime.State == DirectoryState.UnsafeLink)
            throw Failure("inspect managed worktree root", worktreesRoot, "an ancestor is a reparse point, junction, or symbolic link");
        var destinationEntries = new List<RuntimeDirectoryEntry>();
        foreach (var entry in runtime.Entries)
        {
            if (RuntimePaths.Comparer.Equals(entry.Path, destination))
            {
                if (!string.Equals(entry.Id, beadId, StringComparison.Ordinal) || !string.Equals(entry.Path, destination, StringComparison.Ordinal))
                    throw new BeadEnvironmentException(entry.Path, "destination aliases the requested Bead ID");
                if (entry.State != DirectoryState.Present)
                    throw new BeadEnvironmentException(entry.Path, entry.FailureReason
                        ?? (entry.State == DirectoryState.UnsafeLink ? "destination is a reparse point, junction, or symbolic link" : "destination is not a usable directory"));
                if (entry.AmbiguousIdentity)
                    throw new BeadEnvironmentException(entry.Path, "case-insensitive destination identity is ambiguous");
                destinationEntries.Add(entry);
            }
        }

        var registry = await ReadWorktreesAsync(repository, destination, cancellationToken);
        var targetMain = registry.Where(entry => RuntimePaths.Comparer.Equals(entry.Path, repositoryRoot)).ToArray();
        if (targetMain.Length != 1 || !targetMain[0].Main || targetMain[0].Bare || targetMain[0].Locked || targetMain[0].Prunable)
            throw Failure("validate Target Repository registration", repositoryRoot,
                "the validated Target Repository root does not have one healthy main worktree registration");

        GitWorktreeSnapshot? canonical = null;
        foreach (var registration in registry)
        {
            var sameDestination = RuntimePaths.Comparer.Equals(registration.Path, destination);
            if (sameDestination)
            {
                if (!string.Equals(registration.Path, destination, StringComparison.Ordinal))
                    throw new BeadEnvironmentException(registration.RawPath, "registration aliases the canonical destination path");
                if (canonical is not null)
                    throw new BeadEnvironmentException(destination, "multiple registrations resolve to the canonical destination");
                canonical = registration;
                continue;
            }

            if (PathsOverlap(registration.Path, destination))
                throw new BeadEnvironmentException(registration.RawPath, "another registered worktree overlaps the canonical destination");
            if (string.Equals(registration.Branch, branchRef, StringComparison.Ordinal))
                throw new BeadEnvironmentException(registration.RawPath,
                    $"branch '{branchRef}' is registered outside the canonical destination '{destination}'");
        }

        if (canonical is null && destinationEntries.Count != 0)
            throw new BeadEnvironmentException(destination, "an unregistered directory or file already occupies the destination");

        if (canonical is not null)
        {
            if (canonical.Main || canonical.Bare)
                throw new BeadEnvironmentException(destination, "destination is registered as a main or bare worktree");
            if (canonical.Locked || canonical.Prunable)
                throw new BeadEnvironmentException(destination,
                    canonical.Locked ? $"worktree is locked{Reason(canonical.LockReason)}" : $"registration is prunable{Reason(canonical.PruneReason)}");
            if (destinationEntries.Count != 1 || destinationEntries[0].State != DirectoryState.Present)
                throw new BeadEnvironmentException(destination, "registered worktree directory is missing or ambiguous");
            try { ValidateNestedTree(destination); }
            catch (ToolExecutionException exception) { throw new BeadEnvironmentException(destination, exception.Message); }
            if (purpose == WorktreePurpose.Planning && !canonical.Detached)
                throw new BeadEnvironmentException(destination,
                    $"Planning worktree is attached to '{canonical.Branch ?? "an unknown branch"}' instead of detached HEAD");
            if (purpose == WorktreePurpose.Delivery && !string.Equals(canonical.Branch, branchRef, StringComparison.Ordinal))
                throw new BeadEnvironmentException(destination,
                    $"Delivery worktree is on '{canonical.Branch ?? "detached HEAD"}', expected '{branchRef}'");
        }

        return new WorkspaceState(canonical);
    }

    private async Task<IReadOnlyList<GitWorktreeSnapshot>> ReadWorktreesAsync(
        ValidatedTargetRepository repository,
        string destination,
        CancellationToken cancellationToken)
    {
        var result = await RunRequiredGitAsync(["worktree", "list", "--porcelain", "-z"], repository.Path,
            "inspect Target Repository worktree registrations", destination, cancellationToken);
        try
        {
            return GitWorktreeReader.Parse(result.StandardOutput);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure("inspect Target Repository worktree registrations", destination,
                $"invalid porcelain-z output: {exception.Message}", result.StandardError);
        }
    }

    private async Task RevalidateBeforeDestructiveAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string destination,
        string beadId,
        string branchRef,
        WorktreePurpose purpose,
        bool detached,
        CancellationToken cancellationToken)
    {
        var state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        if (state.Registration is null || state.Registration.Detached != detached)
            throw Failure("revalidate isolated worktree", destination, "worktree registration identity changed before a destructive operation");
    }

    internal async Task ValidateRetryWorktreeAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        var destination = GetDestination(hostPaths, project, repository, beadId);
        var branchRef = $"refs/heads/nightweaver/{beadId}";
        var state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef,
            purpose, cancellationToken);
        if (state.Registration is null)
            throw Failure("revalidate retry worktree", destination, "canonical worktree registration is missing");
        if (state.Registration.Locked || state.Registration.Prunable || state.Registration.Main || state.Registration.Bare)
            throw Failure("revalidate retry worktree", destination, "registration is locked, prunable, main, or bare");
        if (purpose == WorktreePurpose.Planning && (!state.Registration.Detached || state.Registration.Branch is not null))
            throw Failure("revalidate retry worktree", destination, "Planning worktree is not detached");
        if (purpose == WorktreePurpose.Delivery
            && !string.Equals(state.Registration.Branch, branchRef, StringComparison.Ordinal))
            throw Failure("revalidate retry worktree", destination, $"Delivery worktree is not on owned branch '{branchRef}'");
    }

    internal Task<ProvisionedWorktree> VerifyRetryWorktreeAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        var destination = GetDestination(hostPaths, project, repository, beadId);
        var branchRef = $"refs/heads/nightweaver/{beadId}";
        return VerifyAndReturnAsync(hostPaths, project, repository, destination, beadId, branchRef,
            purpose, expectedPlanningHead: null, allowDirty: false, reused: true, cancellationToken);
    }

    internal Task<ProvisionedWorktree> VerifyRetainedRetryWorktreeAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string beadId,
        WorktreePurpose purpose,
        CancellationToken cancellationToken)
    {
        var destination = GetDestination(hostPaths, project, repository, beadId);
        var branchRef = $"refs/heads/nightweaver/{beadId}";
        return VerifyAndReturnAsync(hostPaths, project, repository, destination, beadId, branchRef,
            purpose, expectedPlanningHead: null, allowDirty: true, reused: true, cancellationToken);
    }

    private async Task<ProvisionedWorktree> VerifyAndReturnAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        string destination,
        string beadId,
        string branchRef,
        WorktreePurpose purpose,
        string? expectedPlanningHead,
        bool allowDirty,
        bool reused,
        CancellationToken cancellationToken)
    {
        var state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        if (state.Registration is null)
            throw Failure("verify isolated worktree", destination, "worktree registration is missing after provisioning");

        try
        {
            var symbolic = await RunGitAsync(["symbolic-ref", "--quiet", "HEAD"], destination,
                "verify worktree HEAD state", destination, cancellationToken);
            if (purpose == WorktreePurpose.Planning)
            {
                if (symbolic.ExitCode != 1 || symbolic.StandardOutput.Length != 0 || !string.IsNullOrWhiteSpace(symbolic.StandardError))
                    throw new BeadEnvironmentException(destination, $"Planning worktree is not detached: {symbolic.StandardError}");
            }
            else if (symbolic.ExitCode != 0 || !string.Equals(TrimOneLine(symbolic.StandardOutput), branchRef, StringComparison.Ordinal))
            {
                throw new BeadEnvironmentException(destination, $"expected symbolic HEAD '{branchRef}': {symbolic.StandardError}");
            }

            var head = await ResolveCommitAsync(destination, "HEAD", "record pre-dispatch HEAD", destination, cancellationToken);
            if (expectedPlanningHead is not null && !string.Equals(head, expectedPlanningHead, StringComparison.OrdinalIgnoreCase))
                throw new BeadEnvironmentException(destination,
                    $"HEAD '{head}' does not match fetched origin default commit '{expectedPlanningHead}'");

            var status = await RunRequiredGitAsync(["status", "--porcelain=v1", "--untracked-files=all"], destination,
                "verify usable worktree", destination, cancellationToken);
            var dirty = status.StandardOutput.Length != 0;
            if (!allowDirty && dirty)
                throw new BeadEnvironmentException(destination, "new worktree is unexpectedly dirty after creation");
            ValidateNestedTree(destination);
            return new ProvisionedWorktree(destination, head, reused, dirty);
        }
        catch (OperationCanceledException) { throw; }
        catch (BeadEnvironmentException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new BeadEnvironmentException(destination, exception.Message);
        }
    }

    private async Task<string> ResolveCommitAsync(string cwd, string revision, string operation, string path, CancellationToken cancellationToken)
    {
        var result = await RunRequiredGitAsync(["rev-parse", "--verify", $"{revision}^{{commit}}"], cwd, operation, path, cancellationToken);
        var sha = TrimOneLine(result.StandardOutput);
        if (sha is null || sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw Failure(operation, path, $"Git returned an invalid full commit SHA '{result.StandardOutput}'", result.StandardError);
        return sha.ToLowerInvariant();
    }

    private async Task<bool> RefExistsAsync(string cwd, string reference, string operation, string path, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(["show-ref", "--verify", "--quiet", reference], cwd, operation, path, cancellationToken);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardError)) return false;
        throw Failure(operation, path, $"Git could not inspect ref '{reference}' (exit {result.ExitCode})", result.StandardError);
    }

    private async Task<ProcessResult> RunRequiredGitAsync(
        IReadOnlyList<string> arguments,
        string cwd,
        string operation,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(arguments, cwd, operation, path, cancellationToken);
        if (result.ExitCode != 0)
            throw Failure(operation, path, $"git {string.Join(' ', arguments)} exited {result.ExitCode}", result.StandardError);
        return result;
    }

    private async Task<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        string cwd,
        string operation,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await processRunner.RunAsync(GitCommand.Create(arguments, cwd), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToolExecutionException exception)
        {
            throw Failure(operation, path, exception.Message, exception.StandardError);
        }
    }

    private static string GetDestination(HostPaths hostPaths, ProjectSlug project, ValidatedTargetRepository repository, string beadId)
    {
        if (!IsSafeBeadId(beadId))
            throw Failure("validate Bead worktree ID", beadId, "ID is empty, rooted, traversal-like, platform-invalid, or aliases a Windows path name");

        var root = RuntimePaths.Normalize(hostPaths.WorktreesRoot(project));
        var destination = RuntimePaths.Normalize(Path.Combine(root, beadId));
        if (!RuntimePaths.Comparer.Equals(Path.GetDirectoryName(destination), root)
            || !string.Equals(Path.GetFileName(destination), beadId, StringComparison.Ordinal))
            throw Failure("validate Bead worktree path", destination, "ID does not name one literal child of the managed worktree root");
        var repositoryRoot = RuntimePaths.Normalize(repository.Path);
        if (PathsOverlap(destination, repositoryRoot))
            throw Failure("validate Bead worktree path", destination, $"destination overlaps Target Repository root '{repositoryRoot}'");
        return destination;
    }

    internal static bool IsSafeBeadId(string beadId) => BeadIdSafety.IsSafe(beadId);

    internal static bool PathsOverlap(string left, string right) => IsSameOrAncestor(left, right) || IsSameOrAncestor(right, left);

    private static bool IsSameOrAncestor(string possibleAncestor, string path)
    {
        var ancestor = RuntimePaths.Normalize(possibleAncestor);
        for (var current = RuntimePaths.Normalize(path); current is not null; current = Path.GetDirectoryName(current))
            if (RuntimePaths.Comparer.Equals(current, ancestor)) return true;
        return false;
    }

    private static void EnsureMissingParentsAreSafe(string worktreesRoot, string destination)
    {
        var snapshot = RuntimeDirectoryReader.ReadRoot(worktreesRoot);
        if (snapshot.State == DirectoryState.UnsafeLink)
            throw Failure("create worktree parent directory", worktreesRoot, "an ancestor is a reparse point, junction, or symbolic link");
        foreach (var entry in snapshot.Entries)
        {
            if (RuntimePaths.Comparer.Equals(entry.Path, destination))
            {
                if (entry.State != DirectoryState.Present || entry.AmbiguousIdentity)
                    throw new BeadEnvironmentException(destination, entry.FailureReason ?? "destination has an unsafe or ambiguous identity");
                throw new BeadEnvironmentException(destination, "destination is no longer missing");
            }
        }
    }

    internal static void ValidateNestedTree(string root)
    {
        var inspected = RuntimeDirectoryReader.InspectNestedTree(root);
        if (inspected.State != DirectoryState.Present)
            throw Failure("inspect isolated worktree", inspected.Path,
                inspected.FailureReason ?? "destination is not a normal directory");
    }

    private static string? TrimOneLine(string output)
    {
        var value = output.TrimEnd('\r', '\n');
        return value.Contains('\r') || value.Contains('\n') ? null : value;
    }

    private static string Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason}";

    private static ToolExecutionException Failure(string operation, string path, string detail, string? standardError = null) =>
        new($"{operation} at '{path}': {detail}", standardError);

    private sealed record WorkspaceState(GitWorktreeSnapshot? Registration);
}
