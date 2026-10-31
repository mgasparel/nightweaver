using System.Security.Cryptography;
using System.Text;

namespace Nightweaver.Execution;

internal sealed record RetainedWorktreeContentSnapshot(string Fingerprint, int EntryCount, long ContentBytes);

/// <summary>Captures file and directory contents, including ignored files that Git status omits.</summary>
internal static class RetainedWorktreeContentSnapshotReader
{
    private const int MaximumEntries = 100_000;
    private const long MaximumContentBytes = 8L * 1024 * 1024 * 1024;

    internal static async Task<RetainedWorktreeContentSnapshot> ReadAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(worktreePath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Retained worktree '{root}' is unavailable.");

        var entries = new List<Entry>();
        var directories = new Stack<string>();
        directories.Push(root);
        long contentBytes = 0;

        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo link = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                    entries.Add(new(relativePath, "link", attributes, 0, link.LinkTarget ?? string.Empty));
                }
                else if (isDirectory)
                {
                    entries.Add(new(relativePath, "directory", attributes, 0, string.Empty));
                    directories.Push(path);
                }
                else
                {
                    var info = new FileInfo(path);
                    var length = info.Length;
                    contentBytes = checked(contentBytes + length);
                    if (contentBytes > MaximumContentBytes)
                        throw new IOException($"Retained worktree content exceeds the {MaximumContentBytes} byte verification limit.");
                    var digest = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
                    entries.Add(new(relativePath, "file", attributes, length, digest));
                }

                if (entries.Count > MaximumEntries)
                    throw new IOException($"Retained worktree contains more than {MaximumEntries} entries to verify.");
            }
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

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private sealed record Entry(string Path, string Kind, FileAttributes Attributes, long Length, string Value);
}
