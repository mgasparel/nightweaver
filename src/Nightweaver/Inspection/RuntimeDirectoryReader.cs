using Nightweaver.Configuration;

namespace Nightweaver.Inspection;

internal static class RuntimeDirectoryReader
{
    internal static (RuntimeDirectorySnapshot Worktrees, RuntimeDirectorySnapshot Tmp) Read(HostPaths paths, ProjectSlug project) =>
        (ReadRoot(paths.WorktreesRoot(project)), ReadRoot(paths.TmpRoot(project)));

    internal static RuntimeDirectorySnapshot ReadRoot(string root)
    {
        root = RuntimePaths.Normalize(root);
        try
        {
            // Inspect from the volume root down, before probing anything beneath a link.
            var chain = new Stack<string>();
            for (string? current = root; current is not null; current = Path.GetDirectoryName(current)) chain.Push(current);
            while (chain.TryPop(out var ancestor))
            {
                var state = Observe(ancestor);
                if (state != DirectoryState.Present) return new(root, state, Array.Empty<RuntimeDirectoryEntry>());
            }
            var entries = new List<RuntimeDirectoryEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var normalized = RuntimePaths.Normalize(path);
                var id = RuntimePaths.ChildId(root, normalized);
                var state = ObserveEntry(path, out var failureReason);
                entries.Add(new(id ?? Path.GetFileName(path), normalized, state, id is null, failureReason));
            }
            var collisions = entries.GroupBy(entry => entry.Id, RuntimePaths.Comparer).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(RuntimePaths.Comparer);
            entries = entries.Select(entry => entry with { AmbiguousIdentity = entry.AmbiguousIdentity || collisions.Contains(entry.Id) }).ToList();
            return new(root, DirectoryState.Present, entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList().AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ToolExecutionException($"runtime directory inspection '{root}': {exception.Message}"); }
    }

    internal static RuntimeDirectorySnapshot InspectNestedWorktrees(RuntimeDirectorySnapshot snapshot,
        IEnumerable<string> candidatePaths)
    {
        if (snapshot.State != DirectoryState.Present) return snapshot;
        var candidates = candidatePaths.Select(RuntimePaths.Normalize).ToHashSet(RuntimePaths.Comparer);
        if (candidates.Count == 0) return snapshot;
        var entries = snapshot.Entries.Select(entry => candidates.Contains(entry.Path) ? InspectNestedTree(entry) : entry)
            .ToList().AsReadOnly();
        return snapshot with { Entries = entries };
    }

    internal static RuntimeDirectoryEntry InspectNestedTree(string path) =>
        InspectNestedTree(new RuntimeDirectoryEntry(Path.GetFileName(RuntimePaths.Normalize(path)),
            RuntimePaths.Normalize(path), DirectoryState.Present));

    internal static RuntimeDirectoryEntry InspectNestedTree(RuntimeDirectoryEntry entry)
    {
        if (entry.State != DirectoryState.Present) return entry;
        try
        {
            var rootAttributes = File.GetAttributes(entry.Path);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                return entry with { State = DirectoryState.UnsafeLink, FailureReason = "worktree root is a reparse point, junction, or symbolic link" };
            if ((rootAttributes & FileAttributes.Directory) == 0)
                return entry with { State = DirectoryState.Unusable, FailureReason = "expected a directory" };

            var pending = new Stack<string>();
            pending.Push(entry.Path);
            while (pending.TryPop(out var directory))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return entry with
                        {
                            State = DirectoryState.UnsafeLink,
                            FailureReason = $"nested reparse point, junction, or symbolic link is unsafe: '{RuntimePaths.Normalize(child)}'"
                        };
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            }
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return entry with { State = DirectoryState.Unusable, FailureReason = exception.Message };
        }
    }

    private static DirectoryState ObserveEntry(string path, out string? failureReason)
    {
        try
        {
            var state = Observe(path);
            failureReason = state == DirectoryState.Missing ? "entry disappeared during inspection" : null;
            return state == DirectoryState.Missing ? DirectoryState.Unusable : state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failureReason = exception.Message;
            return DirectoryState.Unusable;
        }
    }

    private static DirectoryState Observe(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return DirectoryState.Missing; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) return DirectoryState.UnsafeLink;
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException($"expected a directory at '{path}'");
        return DirectoryState.Present;
    }
}
