using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Background;

internal static class BackgroundFileLock
{
    internal static string OwnerLockPath(HostPaths paths, ProjectSlug project) =>
        Path.Combine(paths.BackgroundRoot(project), "owner.lock");

    internal static string StartLockPath(HostPaths paths, ProjectSlug project) =>
        Path.Combine(paths.BackgroundRoot(project), "start.lock");

    internal static InvocationRuntimeFiles.PinnedRuntimeFile AcquireOwner(HostPaths paths, ProjectSlug project)
    {
        var path = OwnerLockPath(paths, project);
        return InvocationRuntimeFiles.OpenExclusiveRuntimeFile(path, createMissingDirectories: true);
    }

    internal static InvocationRuntimeFiles.PinnedRuntimeFile AcquireStart(HostPaths paths, ProjectSlug project)
    {
        var path = StartLockPath(paths, project);
        return InvocationRuntimeFiles.OpenExclusiveRuntimeFile(path, createMissingDirectories: true);
    }

    internal static bool IsOwnerActive(HostPaths paths, ProjectSlug project)
    {
        var path = OwnerLockPath(paths, project);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(path, createMissingDirectories: false);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        catch (InvalidDataException) { return true; }
    }
}
