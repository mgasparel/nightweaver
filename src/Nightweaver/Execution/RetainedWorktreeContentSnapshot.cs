using System.Security.Cryptography;
using System.Text;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record RetainedWorktreeContentSnapshot(string Fingerprint, int EntryCount, long ContentBytes);

/// <summary>Captures retained file and directory contents, including ignored files that Git status omits.</summary>
internal static class RetainedWorktreeContentSnapshotReader
{
    private const int MaximumEntries = 100_000;
    private const long MaximumContentBytes = 8L * 1024 * 1024 * 1024;

    internal static Task<RetainedWorktreeContentSnapshot> ReadAsync(
        string worktreePath,
        CancellationToken cancellationToken) =>
        ReadRootsAsync([new("worktree", worktreePath, null, Required: true)], cancellationToken);

    internal static Task<RetainedWorktreeContentSnapshot> ReadInvocationEnvironmentAsync(
        string worktreePath,
        string retainedTemporaryRoot,
        string retainedLogsRoot,
        string classifierTemporaryDirectory,
        string classifierLogFilePath,
        CancellationToken cancellationToken) =>
        ReadRootsAsync(
            [
                new("worktree", worktreePath, null, Required: true),
                new("bead-temp", retainedTemporaryRoot, classifierTemporaryDirectory, Required: false),
                new("bead-logs", retainedLogsRoot, classifierLogFilePath, Required: false)
            ],
            cancellationToken);

    private static async Task<RetainedWorktreeContentSnapshot> ReadRootsAsync(
        IReadOnlyList<SnapshotRoot> roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<Entry>();
        long contentBytes = 0;
        foreach (var rootSpec in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            contentBytes = await AppendRootAsync(rootSpec, entries, contentBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = $"{entry.Path}\0{entry.Kind}\0{(int)entry.Attributes}\0{entry.Length}\0{entry.Value}\n";
            fingerprint.AppendData(Encoding.UTF8.GetBytes(record));
        }

        return new(Convert.ToHexString(fingerprint.GetHashAndReset()), entries.Count, contentBytes);
    }

    private static async Task<long> AppendRootAsync(
        SnapshotRoot rootSpec,
        List<Entry> entries,
        long contentBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSpec.Path);
        var root = RuntimePaths.Normalize(rootSpec.Path);
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (FileNotFoundException) when (!rootSpec.Required)
        {
            return contentBytes;
        }
        catch (DirectoryNotFoundException) when (!rootSpec.Required)
        {
            return contentBytes;
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException($"Retained content root '{root}' is not a directory.");
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Retained content root '{root}' is a reparse point and cannot be safely verified.");

        string? excludedPath = null;
        if (rootSpec.ExcludedPath is not null)
        {
            excludedPath = RuntimePaths.Normalize(rootSpec.ExcludedPath);
            var parent = Path.GetDirectoryName(excludedPath);
            if (parent is null || !RuntimePaths.Comparer.Equals(RuntimePaths.Normalize(parent), root))
                throw new IOException($"Expected classifier artifact '{excludedPath}' is not a direct child of retained root '{root}'.");
        }

        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = RuntimePaths.Normalize(path);
                if (IsExcluded(fullPath, excludedPath)) continue;

                var attributes = File.GetAttributes(fullPath);
                var relativePath = $"{rootSpec.Label}/{Path.GetRelativePath(root, fullPath).Replace('\\', '/')}";
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo link = isDirectory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
                    entries.Add(new(relativePath, "link", attributes, 0, link.LinkTarget ?? string.Empty));
                }
                else if (isDirectory)
                {
                    entries.Add(new(relativePath, "directory", attributes, 0, string.Empty));
                    directories.Push(fullPath);
                }
                else
                {
                    var info = new FileInfo(fullPath);
                    var length = info.Length;
                    contentBytes = checked(contentBytes + length);
                    if (contentBytes > MaximumContentBytes)
                        throw new IOException($"Retained environment content exceeds the {MaximumContentBytes} byte verification limit.");
                    var digest = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
                    entries.Add(new(relativePath, "file", attributes, length, digest));
                }

                if (entries.Count > MaximumEntries)
                    throw new IOException($"Retained environment contains more than {MaximumEntries} entries to verify.");
            }
        }

        return contentBytes;
    }

    private static bool IsExcluded(string path, string? excludedPath)
    {
        if (excludedPath is null) return false;
        if (RuntimePaths.Comparer.Equals(path, excludedPath)) return true;
        var prefix = Path.TrimEndingDirectorySeparator(excludedPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(prefix, comparison);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private sealed record SnapshotRoot(string Label, string Path, string? ExcludedPath, bool Required);
    private sealed record Entry(string Path, string Kind, FileAttributes Attributes, long Length, string Value);
}
