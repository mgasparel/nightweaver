namespace Nightweaver.Inspection;

internal static class SharedRuntimeRootValidator
{
    internal static void EnsureUsable(string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        try
        {
            var volumeRoot = Path.GetPathRoot(fullRoot)
                ?? throw new IOException("path has no filesystem root");
            var current = Path.TrimEndingDirectorySeparator(volumeRoot);
            if (current.Length == 0) current = volumeRoot;
            EnsureDirectory(current, createMissing: false);

            var relative = Path.GetRelativePath(volumeRoot, fullRoot);
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                EnsureDirectory(current, createMissing: true);
            }
            ProbeWritable(fullRoot);
        }
        catch (ToolExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new ToolExecutionException($"shared runtime root '{fullRoot}' is unusable: {exception.Message}");
        }
    }

    private static void ProbeWritable(string root)
    {
        var probe = Path.Combine(root, $".nightweaver-root-check-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            try
            {
                if (Directory.Exists(probe)) Directory.Delete(probe, recursive: true);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"writeability check failed and its probe could not be removed: {cleanupException.Message}", exception);
            }
            throw new IOException($"directory is not writable: {exception.Message}", exception);
        }
    }

    private static void EnsureDirectory(string path, bool createMissing)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!createMissing) throw new IOException($"filesystem root is unavailable at '{path}'", exception);
            Directory.CreateDirectory(path);
            attributes = File.GetAttributes(path);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"reparse point is not allowed in a shared runtime root: '{path}'");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException($"expected a directory in a shared runtime root at '{path}'");
    }
}
