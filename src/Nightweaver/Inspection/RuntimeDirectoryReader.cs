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
                var state = Observe(path);
                if (state == DirectoryState.Missing) throw new IOException($"child disappeared during inspection: '{path}'");
                entries.Add(new(id ?? Path.GetFileName(path), normalized, state, id is null));
            }
            var collisions = entries.GroupBy(entry => entry.Id, RuntimePaths.Comparer).Where(group => group.Count() > 1)
                .Select(group => group.Key).ToHashSet(RuntimePaths.Comparer);
            entries = entries.Select(entry => entry with { AmbiguousIdentity = entry.AmbiguousIdentity || collisions.Contains(entry.Id) }).ToList();
            return new(root, DirectoryState.Present, entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList().AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ToolExecutionException($"runtime directory inspection '{root}': {exception.Message}"); }
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
