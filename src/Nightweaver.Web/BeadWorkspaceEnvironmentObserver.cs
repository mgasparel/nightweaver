using System.Text;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed class BeadWorkspaceEnvironmentObserver(
    HostPaths hostPaths,
    ProjectConfig project,
    GitWorktreeReader worktreeReader,
    IProcessRunner processRunner,
    IBeadWorkspaceRuntimeObserver runtimeObserver)
{
    private const int MaxMarkerBytes = 4096;

    internal async Task<BeadWorkspaceEnvironment> ObserveAsync(ValidatedTargetRepository target, string beadId,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var runtime = ObserveRuntime(beadId);
        var root = hostPaths.WorktreesRoot(project.Name);
        var expectedPath = RuntimePaths.Normalize(Path.Combine(root, beadId));
        if (!RuntimePaths.Comparer.Equals(Path.GetDirectoryName(expectedPath), RuntimePaths.Normalize(root))
            || !string.Equals(Path.GetFileName(expectedPath), beadId, StringComparison.Ordinal))
            return BeadWorkspaceProjector.UnavailableEnvironment("unsafe", "The managed worktree path could not be verified.");

        try
        {
            var directory = RuntimeDirectoryReader.ReadRoot(root);
            if (directory.State == DirectoryState.Missing)
                return Make("absent", null, null, null, null, "unknown",
                    "No local managed checkout is present; paused or completed work may have been cleaned up.");
            if (directory.State != DirectoryState.Present)
                return Make("unsafe", null, null, null, null, "unknown", "The managed worktree directory has unsafe ancestry.");

            var entry = directory.Entries.SingleOrDefault(item => string.Equals(item.Id, beadId, StringComparison.Ordinal));
            if (entry is null)
                return Make("absent", null, null, null, null, "unknown",
                    "No local managed checkout is present; paused or completed work may have been cleaned up.");
            if (!RuntimePaths.Comparer.Equals(entry.Path, expectedPath) || entry.State != DirectoryState.Present || entry.AmbiguousIdentity)
                return Make("unsafe", null, null, null, null, "unknown", "The managed worktree identity is ambiguous or unsafe.");

            var registrations = await worktreeReader.ReadAsync(target, cancellationToken);
            var registration = registrations.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, expectedPath));
            if (registration is null || registration.Main || registration.Bare || registration.Locked || registration.Prunable)
                return Make("unsafe", null, null, null, null, "unknown", "The local directory is not a verified active worktree for this Bead.");

            var commonDirectory = await ReadCommonDirectoryAsync(target, cancellationToken);
            VerifyLinkedWorktree(expectedPath, commonDirectory);
            var before = await worktreeReader.ReadAsync(target, cancellationToken);
            if (!SameRegistration(registration, before.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, expectedPath))))
                return Make("unsafe", null, null, null, null, "unknown", "The worktree registration changed during observation.");
            VerifyLinkedWorktree(expectedPath, commonDirectory);

            ProcessResult status;
            try
            {
                status = await processRunner.RunAsync(GitCommand.Create(
                    ["status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=all"], expectedPath), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Make("unavailable", expectedPath, registration.Branch, registration.Head, registration.Detached,
                    "unknown", "Git cleanliness could not be read.");
            }

            if (status.ExitCode != 0)
                return Make("unavailable", expectedPath, registration.Branch, registration.Head, registration.Detached,
                    "unknown", "Git cleanliness could not be read.");

            VerifyLinkedWorktree(expectedPath, commonDirectory);
            var after = await worktreeReader.ReadAsync(target, cancellationToken);
            if (!SameRegistration(registration, after.SingleOrDefault(item => RuntimePaths.Comparer.Equals(item.Path, expectedPath))))
                return Make("unavailable", expectedPath, registration.Branch, registration.Head, registration.Detached,
                    "unknown", "The worktree changed while its status was being read.");

            return Make("present", expectedPath, registration.Branch,
                registration.Head is null ? null : registration.Head[..Math.Min(12, registration.Head.Length)],
                registration.Detached, string.IsNullOrEmpty(status.StandardOutput) ? "clean" : "dirty",
                "Git status excludes submodule internals.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnsafeWorktreeException)
        {
            return Make("unsafe", null, null, null, null, "unknown", "The managed worktree identity could not be verified safely.");
        }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Make("unavailable", null, null, null, null, "unknown", "Execution environment details are unavailable.");
        }

        BeadWorkspaceEnvironment Make(string state, string? path, string? branch, string? head, bool? detached,
            string gitStatus, string? warning)
            => new(state, observedAt, path, branch, head, detached, gitStatus,
                runtime.ProcessId, runtime.ProcessState, "filesystem", warning, runtime.ObservedAt, runtime.Source);
    }

    private BeadWorkspaceRuntimeObservation ObserveRuntime(string beadId)
    {
        try
        {
            var observation = runtimeObserver.Observe(project.Name.Value, beadId);
            return observation.ProcessId is null
                ? new(null, "unavailable", observation.ObservedAt, observation.Source)
                : observation;
        }
        catch (Exception)
        {
            return new(null, "unavailable", null, null);
        }
    }

    private async Task<string> ReadCommonDirectoryAsync(ValidatedTargetRepository target, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            GitCommand.Create(["rev-parse", "--path-format=absolute", "--git-common-dir"], target.Path), cancellationToken);
        if (result.ExitCode != 0) throw new ToolExecutionException("could not verify the configured Git common directory", result.StandardError);
        var value = result.StandardOutput.TrimEnd('\r', '\n');
        if (value.Length == 0 || value.Contains('\r') || value.Contains('\n') || !Path.IsPathFullyQualified(value))
            throw new ToolExecutionException("Git returned an invalid common directory");
        var normalized = RuntimePaths.Normalize(value);
        RequireNormalDirectoryChain(normalized);
        return normalized;
    }

    private static bool SameRegistration(GitWorktreeSnapshot expected, GitWorktreeSnapshot? actual)
        => actual is not null && RuntimePaths.Comparer.Equals(expected.Path, actual.Path)
            && expected.Head == actual.Head && expected.Branch == actual.Branch && expected.Detached == actual.Detached
            && expected.Bare == actual.Bare && expected.Main == actual.Main && expected.Locked == actual.Locked
            && expected.Prunable == actual.Prunable;

    private static void VerifyLinkedWorktree(string worktreePath, string commonDirectory)
    {
        RequireNormalDirectoryChain(worktreePath);
        var marker = Path.Combine(worktreePath, ".git");
        var markerText = ReadBoundedRegularFile(marker);
        var adminDirectory = ResolveGitPointer(markerText, "gitdir: ", worktreePath);
        var worktreeAdminRoot = RuntimePaths.Normalize(Path.Combine(commonDirectory, "worktrees"));
        if (!RuntimePaths.Comparer.Equals(Path.GetDirectoryName(adminDirectory), worktreeAdminRoot)
            || !IsSameOrDescendant(worktreeAdminRoot, adminDirectory))
            throw new UnsafeWorktreeException();
        RequireNormalDirectoryChain(adminDirectory);

        var commondir = ResolveGitPointer(ReadBoundedRegularFile(Path.Combine(adminDirectory, "commondir")), null, adminDirectory);
        if (!RuntimePaths.Comparer.Equals(commondir, commonDirectory))
            throw new UnsafeWorktreeException();
        var backlink = ResolveGitPointer(ReadBoundedRegularFile(Path.Combine(adminDirectory, "gitdir")), null, adminDirectory);
        if (!RuntimePaths.Comparer.Equals(backlink, marker))
            throw new UnsafeWorktreeException();
    }

    private static string ResolveGitPointer(string content, string? prefix, string baseDirectory)
    {
        var value = content.TrimEnd('\r', '\n');
        if (value.Length == 0 || value.Contains('\r') || value.Contains('\n')) throw new UnsafeWorktreeException();
        if (prefix is not null)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new UnsafeWorktreeException();
            value = value[prefix.Length..];
        }
        if (value.Length == 0) throw new UnsafeWorktreeException();
        return RuntimePaths.Normalize(Path.GetFullPath(value, baseDirectory));
    }

    private static string ReadBoundedRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new UnsafeWorktreeException();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024, FileOptions.SequentialScan);
        if (stream.Length > MaxMarkerBytes) throw new UnsafeWorktreeException();
        var bytes = new byte[MaxMarkerBytes + 1];
        var count = stream.Read(bytes, 0, bytes.Length);
        if (count > MaxMarkerBytes || stream.ReadByte() != -1) throw new UnsafeWorktreeException();
        var after = File.GetAttributes(path);
        if ((after & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new UnsafeWorktreeException();
        return new UTF8Encoding(false, true).GetString(bytes, 0, count);
    }

    private static void RequireNormalDirectoryChain(string path)
    {
        var chain = new Stack<string>();
        for (string? current = RuntimePaths.Normalize(path); current is not null; current = Path.GetDirectoryName(current))
            chain.Push(current);
        while (chain.TryPop(out var current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new UnsafeWorktreeException();
        }
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && relative != "." && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class UnsafeWorktreeException : Exception;
}
