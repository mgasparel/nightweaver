using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>
/// Pins validated directory components and exclusively reserves the invocation tmp leaf and log file.
/// Windows share-mode semantics are required: every pinned directory denies FILE_SHARE_DELETE, which prevents
/// its path from being renamed/replaced while descendants are reserved. This relies on the filesystem honoring
/// Win32 sharing and reparse-point semantics (as NTFS and supported SMB servers do).
/// </summary>
internal sealed class InvocationRuntimeFiles : IDisposable
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint ErrorFileNotFound = 2;
    private const uint ErrorPathNotFound = 3;
    private const uint ErrorAlreadyExists = 183;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;

    private readonly Dictionary<string, SafeFileHandle> pinnedDirectories = new(RuntimePaths.Comparer);
    private bool disposed;

    private InvocationRuntimeFiles(string temporaryDirectory, string logFilePath, FileStream logStream)
    {
        TemporaryDirectory = temporaryDirectory;
        LogFilePath = logFilePath;
        LogStream = logStream;
    }

    internal string TemporaryDirectory { get; }
    internal string LogFilePath { get; }
    internal FileStream LogStream { get; private set; } = null!;

    internal static InvocationRuntimeFiles Reserve(
        HostPaths hostPaths,
        ProjectSlug project,
        string beadId,
        PreparedAgentInvocation invocation)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Agent CLI execution requires Windows Job Objects.");

        ValidatePreparedPaths(hostPaths, project, beadId, invocation);
        var files = new InvocationRuntimeFiles(invocation.TemporaryDirectory, invocation.LogFilePath, null!);
        try
        {
            files.PinDirectoryChain(invocation.WorkingDirectory, createMissing: false, exclusiveLeaf: false);
            files.PinDirectoryChain(Path.GetDirectoryName(invocation.TemporaryDirectory)!, createMissing: true, exclusiveLeaf: false);
            files.PinDirectoryChain(invocation.TemporaryDirectory, createMissing: true, exclusiveLeaf: true);
            files.PinDirectoryChain(Path.GetDirectoryName(invocation.LogFilePath)!, createMissing: true, exclusiveLeaf: false);

            var logHandle = CreateFileW(
                invocation.LogFilePath,
                GenericWrite,
                FileShareRead,
                IntPtr.Zero,
                CreateNew,
                FileFlagOverlapped | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (logHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                logHandle.Dispose();
                throw NativeError("Could not exclusively reserve the invocation log", error);
            }

            // FileStream takes ownership of this CREATE_NEW handle; parent directory pins remain owned here.
            var logStream = new FileStream(logHandle, FileAccess.Write, 64 * 1024, isAsync: true);
            files.LogStream = logStream;
            return files;
        }
        catch
        {
            files.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a settled lifecycle log for bounded, read-only comparison. Existing directory components are pinned
    /// with the same no-reparse and no-delete-sharing rules used by invocation reservation.
    /// </summary>
    internal static PinnedLogReadStream OpenExistingSettledLogForRead(
        HostPaths hostPaths,
        ProjectSlug project,
        string beadId,
        string invocationId,
        string logFilePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Settled invocation logs require Windows no-follow file handles.");
        if (!SafeBeadId(beadId)) throw new InvalidDataException($"Bead ID '{beadId}' is not a safe Windows path segment.");
        if (!SafeWindowsSegment(project.Value)) throw new InvalidDataException($"Project slug '{project.Value}' is not a safe Windows path segment.");
        if (!IsValidInvocationId(invocationId)) throw new InvalidDataException($"Invocation ID '{invocationId}' is not valid.");

        var expected = RuntimePaths.Normalize(Path.Combine(hostPaths.LogsRoot(project), beadId, invocationId + ".log"));
        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(logFilePath))
                throw new InvalidDataException("The failed invocation log path must be absolute.");
            fullPath = Path.GetFullPath(logFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"The failed invocation log path '{logFilePath}' is invalid.", exception);
        }

        // Require the exact path emitted by the preparer. This excludes traversal aliases, alternate streams,
        // case aliases, sibling-prefix tricks, device paths and unexpected suffixes before touching the source.
        if (!string.Equals(logFilePath, expected, StringComparison.Ordinal)
            || !string.Equals(fullPath, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Failed invocation log path '{logFilePath}' is not the canonical log for invocation '{invocationId}'.");

        ValidatePathSegments(expected);
        var guard = new InvocationRuntimeFiles(string.Empty, expected, null!);
        try
        {
            guard.PinDirectoryChain(Path.GetDirectoryName(expected)!, createMissing: false, exclusiveLeaf: false);
            var handle = CreateFileW(expected, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting,
                FileFlagOverlapped | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw NativeError($"Could not open settled invocation log '{expected}' for read", error);
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out var info))
                    throw NativeError($"Could not inspect settled invocation log '{expected}'", Marshal.GetLastWin32Error());
                if ((info.FileAttributes & AttributeReparsePoint) != 0)
                    throw new InvalidDataException($"Refusing reparse point in failed invocation log path: '{expected}'.");
                if ((info.FileAttributes & AttributeDirectory) != 0)
                    throw new InvalidDataException($"Failed invocation log path '{expected}' is a directory.");

                var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: true);
                return new PinnedLogReadStream(stream, guard);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;

    internal async Task FlushLogAsync(CancellationToken cancellationToken)
    {
        await LogStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePreparedPaths(HostPaths paths, ProjectSlug project, string beadId, PreparedAgentInvocation invocation)
    {
        if (!SafeBeadId(beadId)) throw new InvalidDataException($"Bead ID '{beadId}' is not a safe Windows path segment.");
        if (!SafeWindowsSegment(project.Value)) throw new InvalidDataException($"Project slug '{project.Value}' is not a safe Windows path segment.");
        if (!IsValidInvocationId(invocation.InvocationId)) throw new InvalidDataException($"Invocation ID '{invocation.InvocationId}' is not valid.");

        string expectedWorking;
        string expectedTmp;
        string expectedLog;
        try
        {
            expectedWorking = RuntimePaths.Normalize(Path.Combine(paths.WorktreesRoot(project), beadId));
            expectedTmp = RuntimePaths.Normalize(Path.Combine(paths.TmpRoot(project), beadId, invocation.InvocationId));
            expectedLog = RuntimePaths.Normalize(Path.Combine(paths.LogsRoot(project), beadId, invocation.InvocationId + ".log"));
            if (!RuntimePaths.Comparer.Equals(expectedWorking, RuntimePaths.Normalize(invocation.WorkingDirectory))
                || !RuntimePaths.Comparer.Equals(expectedTmp, RuntimePaths.Normalize(invocation.TemporaryDirectory))
                || !RuntimePaths.Comparer.Equals(expectedLog, RuntimePaths.Normalize(invocation.LogFilePath)))
                throw new InvalidDataException("Prepared cwd, temporary directory or log path does not match the canonical HostPaths layout.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("Prepared invocation contains an invalid runtime path.", exception);
        }

        if (!invocation.EnvironmentOverrides.TryGetValue("NIGHTWEAVER_BEAD_ID", out var envBeadId)
            || !string.Equals(envBeadId, beadId, StringComparison.Ordinal)
            || !invocation.EnvironmentOverrides.TryGetValue("TMP", out var envTmp)
            || !RuntimePaths.Comparer.Equals(expectedTmp, RuntimePaths.Normalize(envTmp))
            || !invocation.EnvironmentOverrides.TryGetValue("TEMP", out var envTemp)
            || !RuntimePaths.Comparer.Equals(expectedTmp, RuntimePaths.Normalize(envTemp))
            || !invocation.EnvironmentOverrides.TryGetValue("TMPDIR", out var envTmpDir)
            || !RuntimePaths.Comparer.Equals(expectedTmp, RuntimePaths.Normalize(envTmpDir)))
            throw new InvalidDataException("Prepared environment overrides do not match the invocation identity and runtime paths.");
        if (invocation.EnvironmentOverrides.Count != 4
            || invocation.EnvironmentOverrides.Keys.Any(key => key is not ("NIGHTWEAVER_BEAD_ID" or "TMP" or "TEMP" or "TMPDIR")))
            throw new InvalidDataException("Prepared invocation may override only NIGHTWEAVER_BEAD_ID, TMP, TEMP and TMPDIR.");

        foreach (var directory in new[] { invocation.WorkingDirectory, Path.GetDirectoryName(expectedTmp)!, expectedTmp, Path.GetDirectoryName(expectedLog)! })
            ValidatePathSegments(directory);
    }

    private static bool IsValidInvocationId(string value)
    {
        if (value.Length < 25 || value.Length > 64 || value[8] != 'T' || value[22] != 'Z' || value[23] != '-') return false;
        if (!value.AsSpan(0, 8).ToString().All(char.IsAsciiDigit)
            || !value.AsSpan(9, 13).ToString().All(char.IsAsciiDigit)) return false;
        var suffix = value[24..];
        if (suffix is not ("planner" or "plan-challenger" or "implementer" or "reviewer" or "qa" or "merger" or "classifier")) return false;
        return DateTime.TryParseExact(value[..23], "yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out _);
    }

    private static void ValidatePathSegments(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException($"Path '{path}' has no volume root.");
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".") return;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            if (!SafeWindowsSegment(segment)) throw new InvalidDataException($"Path '{path}' contains unsafe Windows segment '{segment}'.");
    }

    private static bool SafeWindowsSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
            || segment.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || segment.Any(char.IsControl)) return false;
        var stem = segment.Split('.', 2)[0];
        return !WindowsDeviceNames.Contains(stem)
            && !(stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is '¹' or '²' or '³');
    }

    private static bool SafeBeadId(string beadId) => beadId.Length is > 0 and <= 128
        && char.IsAsciiLetterOrDigit(beadId[0])
        && beadId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && !beadId.EndsWith('.')
        && SafeWindowsSegment(beadId);

    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private void PinDirectoryChain(string requestedPath, bool createMissing, bool exclusiveLeaf)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        ValidatePathSegments(fullPath);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException($"Path '{requestedPath}' has no volume root.");
        PinExistingDirectory(root);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".") return;

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var leaf = index == segments.Length - 1;
            if (pinnedDirectories.ContainsKey(current)) continue;

            if (exclusiveLeaf && leaf)
            {
                if (!CreateDirectoryW(current, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if ((uint)error == ErrorAlreadyExists)
                        throw new IOException($"Invocation temporary directory '{current}' is already reserved.");
                    throw NativeError($"Could not exclusively reserve temporary directory '{current}'", error);
                }
                PinExistingDirectory(current);
                continue;
            }

            if (TryOpenDirectory(current, out var handle, out var errorCode))
            {
                AddPinned(current, handle);
                continue;
            }

            if (!createMissing || ((uint)errorCode != ErrorFileNotFound && (uint)errorCode != ErrorPathNotFound))
                throw NativeError($"Could not safely open directory '{current}'", errorCode);

            if (!CreateDirectoryW(current, IntPtr.Zero))
            {
                var createError = Marshal.GetLastWin32Error();
                if ((uint)createError != ErrorAlreadyExists)
                    throw NativeError($"Could not create directory '{current}'", createError);
                // Another creator won. Open and validate the object; never trust CreateDirectory's collision result.
            }

            PinExistingDirectory(current);
        }
    }

    private void PinExistingDirectory(string path)
    {
        if (pinnedDirectories.ContainsKey(path)) return;
        if (!TryOpenDirectory(path, out var handle, out var errorCode))
            throw NativeError($"Could not pin directory '{path}'", errorCode);
        AddPinned(path, handle);
    }

    private void AddPinned(string path, SafeFileHandle handle)
    {
        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw NativeError($"Could not inspect pinned directory '{path}'", Marshal.GetLastWin32Error());
            if ((info.FileAttributes & AttributeReparsePoint) != 0)
                throw new InvalidDataException($"Refusing reparse point in runtime path: '{path}'.");
            if ((info.FileAttributes & AttributeDirectory) == 0)
                throw new InvalidDataException($"Expected a directory in runtime path, found a file: '{path}'.");
            pinnedDirectories.Add(path, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool TryOpenDirectory(string path, out SafeFileHandle handle, out int errorCode)
    {
        handle = CreateFileW(path, FileReadAttributes, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        handle.Dispose();
        handle = null!;
        return false;
    }

    private static IOException NativeError(string operation, int errorCode)
    {
        var error = new Win32Exception(errorCode);
        return new IOException($"{operation} (Win32 error {errorCode}: {error.Message}).", error);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { LogStream?.Dispose(); }
        finally
        {
            foreach (var handle in pinnedDirectories.Values) handle.Dispose();
            pinnedDirectories.Clear();
        }
    }

    internal sealed class PinnedLogReadStream(FileStream stream, InvocationRuntimeFiles directoryPins) : IDisposable
    {
        internal FileStream Stream { get; } = stream;

        public void Dispose()
        {
            try { Stream.Dispose(); }
            finally { directoryPins.Dispose(); }
        }
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}
