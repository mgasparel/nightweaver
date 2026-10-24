using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum WorktreePurpose
{
    Planning,
    Delivery
}

internal sealed record ProvisionedWorktree(string Path, string HeadSha);

/// <summary>Creates or safely reuses the per-Bead isolated checkout.</summary>
internal sealed class WorktreeProvisioner(IProcessRunner processRunner)
{
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

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

        // Complete all filesystem and registration checks before the first fetch.
        var state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        if (purpose == WorktreePurpose.Delivery)
        {
            var format = await RunGitAsync(["check-ref-format", branchRef], repository.Path,
                "validate generated Delivery branch", destination, cancellationToken);
            if (format.ExitCode != 0)
                throw Failure("validate generated Delivery branch", destination, "Git rejected the generated branch name", format.StandardError);
            if (state.Registration is null && await RefExistsAsync(repository.Path, branchRef,
                    "check for an existing local Delivery branch", destination, cancellationToken))
                throw Failure("validate Delivery branch ownership", destination,
                    $"local branch '{branchRef}' already exists without its canonical registered worktree");
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
                throw Failure("create worktree parent directory", destination, exception.Message);
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
        else if (purpose == WorktreePurpose.Planning)
        {
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: true, cancellationToken);
            await RunRequiredGitAsync(["checkout", "--detach", "--force", defaultRef], destination,
                "reset Planning worktree checkout", destination, cancellationToken);
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: true, cancellationToken);
            await RunRequiredGitAsync(["reset", "--hard", defaultRef], destination,
                "reset Planning worktree files", destination, cancellationToken);
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: true, cancellationToken);
            await RunRequiredGitAsync(["clean", "-fd"], destination,
                "clean Planning worktree files", destination, cancellationToken);
        }

        if (purpose == WorktreePurpose.Delivery)
        {
            // New and warm Delivery worktrees follow the same clean-before-dispatch contract.
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: false, cancellationToken);
            await RunRequiredGitAsync(["reset", "--hard", "HEAD"], destination,
                "reset Delivery worktree files", destination, cancellationToken);
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: false, cancellationToken);
            await RunRequiredGitAsync(["clean", "-fd"], destination,
                "clean Delivery worktree files", destination, cancellationToken);
        }
        else if (state.Registration is null)
        {
            // A fresh Planning worktree was added at the fetched ref and is already clean.
            await RevalidateBeforeDestructiveAsync(hostPaths, project, repository, destination, beadId, branchRef,
                purpose, detached: true, cancellationToken);
        }

        return await VerifyAndReturnAsync(hostPaths, project, repository, destination, beadId, branchRef,
            purpose, planningHead, cancellationToken);
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
            if (entry.State == DirectoryState.UnsafeLink)
                throw Failure("inspect managed worktree root", entry.Path, "a worktree child is a reparse point, junction, or symbolic link");
            if (entry.AmbiguousIdentity)
                throw Failure("inspect managed worktree root", entry.Path, "case-insensitive child identities are ambiguous");
            if (RuntimePaths.Comparer.Equals(entry.Path, destination))
            {
                if (!string.Equals(entry.Id, beadId, StringComparison.Ordinal) || !string.Equals(entry.Path, destination, StringComparison.Ordinal))
                    throw Failure("validate worktree destination identity", entry.Path, "destination aliases the requested Bead ID");
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
                    throw Failure("validate worktree registration identity", registration.RawPath, "registration aliases the canonical destination path");
                if (canonical is not null)
                    throw Failure("validate worktree registration identity", destination, "multiple registrations resolve to the canonical destination");
                canonical = registration;
                continue;
            }

            if (PathsOverlap(registration.Path, destination))
                throw Failure("validate worktree registration identity", registration.RawPath,
                    "another registered worktree overlaps the canonical destination");
            if (string.Equals(registration.Branch, branchRef, StringComparison.Ordinal))
                throw Failure("validate Delivery branch ownership", registration.RawPath,
                    $"branch '{branchRef}' is registered outside the canonical destination '{destination}'");
        }

        if (canonical is null && destinationEntries.Count != 0)
            throw Failure("validate worktree destination", destination, "an unregistered directory or file already occupies the destination");

        if (canonical is not null)
        {
            if (canonical.Main || canonical.Bare)
                throw Failure("validate worktree registration", destination, "destination is registered as a main or bare worktree");
            if (canonical.Locked || canonical.Prunable)
                throw Failure("validate worktree registration", destination,
                    canonical.Locked ? $"worktree is locked{Reason(canonical.LockReason)}" : $"registration is prunable{Reason(canonical.PruneReason)}");
            if (destinationEntries.Count != 1 || destinationEntries[0].State != DirectoryState.Present)
                throw Failure("validate worktree registration", destination, "registered worktree directory is missing or ambiguous");
            ValidateNestedTree(destination);
            if (purpose == WorktreePurpose.Planning && !canonical.Detached)
                throw Failure("validate Planning worktree ownership", destination,
                    $"existing worktree is attached to '{canonical.Branch ?? "an unknown branch"}' instead of detached HEAD");
            if (purpose == WorktreePurpose.Delivery && !string.Equals(canonical.Branch, branchRef, StringComparison.Ordinal))
                throw Failure("validate Delivery worktree ownership", destination,
                    $"existing worktree is on '{canonical.Branch ?? "detached HEAD"}', expected '{branchRef}'");
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
            purpose, expectedPlanningHead: null, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var state = await ValidateWorkspaceAsync(hostPaths, project, repository, destination, beadId, branchRef, purpose, cancellationToken);
        if (state.Registration is null)
            throw Failure("verify isolated worktree", destination, "worktree registration is missing after provisioning");

        var symbolic = await RunGitAsync(["symbolic-ref", "--quiet", "HEAD"], destination,
            "verify worktree HEAD state", destination, cancellationToken);
        if (purpose == WorktreePurpose.Planning)
        {
            if (symbolic.ExitCode != 1 || symbolic.StandardOutput.Length != 0 || !string.IsNullOrWhiteSpace(symbolic.StandardError))
                throw Failure("verify Planning detached HEAD", destination, "worktree is not detached", symbolic.StandardError);
        }
        else if (symbolic.ExitCode != 0 || !string.Equals(TrimOneLine(symbolic.StandardOutput), branchRef, StringComparison.Ordinal))
        {
            throw Failure("verify Delivery branch", destination, $"expected symbolic HEAD '{branchRef}'", symbolic.StandardError);
        }

        var head = await ResolveCommitAsync(destination, "HEAD", "record pre-dispatch HEAD", destination, cancellationToken);
        if (expectedPlanningHead is not null && !string.Equals(head, expectedPlanningHead, StringComparison.OrdinalIgnoreCase))
            throw Failure("verify Planning base commit", destination,
                $"HEAD '{head}' does not match fetched origin default commit '{expectedPlanningHead}'");

        var status = await RunRequiredGitAsync(["status", "--porcelain=v1", "--untracked-files=all"], destination,
            "verify clean worktree", destination, cancellationToken);
        if (status.StandardOutput.Length != 0)
            throw Failure("verify clean worktree", destination, "worktree contains tracked changes or untracked files", status.StandardError);
        ValidateNestedTree(destination);
        return new ProvisionedWorktree(destination, head);
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

    internal static bool IsSafeBeadId(string beadId) => !string.IsNullOrEmpty(beadId)
        && !Path.IsPathRooted(beadId) && RuntimePaths.SafeId(beadId)
        && !beadId.Any(character => character <= '\u001f' || "<>:\"/\\|?*".Contains(character))
        && !beadId.EndsWith(' ') && !beadId.EndsWith('.') && !IsWindowsDeviceName(beadId);

    private static bool IsWindowsDeviceName(string beadId)
    {
        var component = beadId.TrimEnd(' ', '.');
        var stem = component.Split('.', 2)[0];
        if (WindowsDeviceNames.Contains(stem)) return true;
        return stem.Length == 4 && stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
            && stem[3] is '¹' or '²' or '³'
            || stem.Length == 4 && stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)
                && stem[3] is '¹' or '²' or '³';
    }

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
            if (entry.State == DirectoryState.UnsafeLink || entry.AmbiguousIdentity)
                throw Failure("create worktree parent directory", entry.Path, "unsafe or ambiguous worktree child identity");
            if (RuntimePaths.Comparer.Equals(entry.Path, destination))
                throw Failure("create worktree parent directory", destination, "destination is no longer missing");
        }
    }

    internal static void ValidateNestedTree(string root)
    {
        try
        {
            var attributes = File.GetAttributes(root);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw Failure("inspect isolated worktree", root, "destination is not a normal directory");

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var entryAttributes = File.GetAttributes(entry);
                    if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                        throw Failure("inspect isolated worktree", entry, "nested reparse point, junction, or symbolic link is unsafe");
                    if ((entryAttributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }
        catch (ToolExecutionException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Failure("inspect isolated worktree", root, exception.Message);
        }
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
