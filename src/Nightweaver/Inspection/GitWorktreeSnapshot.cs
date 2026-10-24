namespace Nightweaver.Inspection;

internal sealed record GitWorktreeSnapshot(string RawPath, string Path, string? Head, string? Branch,
    bool Detached, bool Bare, bool Locked, string? LockReason, bool Prunable, string? PruneReason, bool Main = false);

internal static class RuntimePaths
{
    internal static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    internal static bool SafeId(string id) => id.Length > 0 && id is not "." and not ".."
        && !id.Contains('/') && !id.Contains('\\') && !id.Contains(':')
        && id.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && (!OperatingSystem.IsWindows() || (!id.EndsWith(' ') && !id.EndsWith('.')));

    internal static string? ChildId(string root, string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path);
        var id = System.IO.Path.GetFileName(path);
        return parent is not null && Comparer.Equals(Normalize(parent), Normalize(root)) && SafeId(id) ? id : null;
    }
}
