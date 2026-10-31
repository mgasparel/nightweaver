using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Inspection;

/// <summary>Owns one OS mutex for the lifetime of the process that runs live dispatch.</summary>
internal static class ProcessLifetimeTargetRepositoryLock
{
    private static readonly ConcurrentDictionary<string, Thread> OwnedHere = new(StringComparer.Ordinal);

    internal static void Acquire(ValidatedTargetRepository repository)
    {
        var identity = TargetRepositoryIdentity.Resolve(repository.GitCommonDirectory);
        var mutexName = MutexName(identity);
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() => OwnForProcessLifetime(mutexName, acquired))
        {
            IsBackground = true,
            Name = "Nightweaver Target Repository mutex owner"
        };

        if (!OwnedHere.TryAdd(identity, owner))
            throw AlreadyOwned(repository.Path);

        try
        {
            owner.Start();
            if (!acquired.Task.GetAwaiter().GetResult())
            {
                OwnedHere.TryRemove(identity, out _);
                throw AlreadyOwned(repository.Path);
            }
        }
        catch (ToolExecutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            OwnedHere.TryRemove(identity, out _);
            throw new ToolExecutionException(
                $"could not acquire the Target Repository process lock for '{repository.Path}': {exception.Message}");
        }
    }

    private static void OwnForProcessLifetime(string mutexName, TaskCompletionSource<bool> acquired)
    {
        Mutex? mutex = null;
        var ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous process died while it owned the mutex. This process now owns it.
                ownsMutex = true;
            }

            acquired.TrySetResult(ownsMutex);
            if (ownsMutex) Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception exception)
        {
            acquired.TrySetException(exception);
        }
        finally
        {
            // This path is used only when acquisition failed or the owner thread itself is interrupted.
            if (ownsMutex)
            {
                try { mutex?.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
            mutex?.Dispose();
        }
    }

    private static string MutexName(string identity)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return OperatingSystem.IsWindows()
            ? $"Global\\Nightweaver_TargetRepository_{hash}"
            : $"Nightweaver_TargetRepository_{hash}";
    }

    private static ToolExecutionException AlreadyOwned(string repositoryPath) =>
        new($"another Nightweaver Orchestrator already owns Target Repository '{repositoryPath}'");

    private static class TargetRepositoryIdentity
    {
        internal static string Resolve(string commonDirectory)
        {
            var fullPath = Path.GetFullPath(commonDirectory);
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsDirectoryIdentity(fullPath);

                var resolved = ResolveLinks(fullPath);
                return $"path:{(OperatingSystem.IsMacOS() ? resolved.ToUpperInvariant() : resolved)}";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or Win32Exception or ArgumentException or NotSupportedException)
            {
                throw new ToolExecutionException($"could not identify Target Repository '{fullPath}' for process exclusion: {exception.Message}");
            }
        }

        private static string ResolveLinks(string fullPath)
        {
            var root = Path.GetPathRoot(fullPath)
                ?? throw new IOException($"Git common directory '{fullPath}' has no filesystem root");
            var current = Path.TrimEndingDirectorySeparator(root);
            if (current.Length == 0) current = root;
            var relative = Path.GetRelativePath(root, fullPath);
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var info = new DirectoryInfo(current);
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }

        private static string WindowsDirectoryIdentity(string path)
        {
            using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero,
                OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not open Git common directory '{path}' for identity");
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not read Git common directory identity for '{path}'");

            var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            return $"windows:{information.VolumeSerialNumber:x8}:{fileId:x16}";
        }

        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }
    }
}
