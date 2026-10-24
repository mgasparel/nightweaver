using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Execution;

/// <summary>Deletes a managed temporary tree without following reparse points or allowing directory substitution.</summary>
internal static class TemporaryTreeDeleter
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const int FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    internal static void Delete(string path, Action<string>? beforeDelete = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            DeleteManaged(path, beforeDelete);
            return;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new IOException($"Temporary path '{path}' has no parent directory.");
        var pins = PinDirectoryChain(parent);
        try
        {
            using var root = TryOpen(path, allowMissing: true, deleteAccess: true);
            if (root is null) return;
            DeleteOpened(path, root, beforeDelete);
        }
        finally
        {
            for (var index = pins.Count - 1; index >= 0; index--) pins[index].Dispose();
        }
    }

    private static List<SafeFileHandle> PinDirectoryChain(string directory)
    {
        var chain = new Stack<string>();
        for (string? current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
            chain.Push(current);
        var handles = new List<SafeFileHandle>();
        try
        {
            while (chain.TryPop(out var path))
            {
                var handle = TryOpen(path, allowMissing: false, deleteAccess: false)!;
                var attributes = ReadAttributes(handle, path);
                if ((attributes & AttributeReparsePoint) != 0 || (attributes & AttributeDirectory) == 0)
                {
                    handle.Dispose();
                    throw new IOException($"Temporary directory ancestor '{path}' is a reparse point or non-directory.");
                }
                handles.Add(handle);
            }
            return handles;
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
            throw;
        }
    }

    private static void DeleteOpened(string path, SafeFileHandle handle, Action<string>? beforeDelete)
    {
        var attributes = ReadAttributes(handle, path);
        if ((attributes & AttributeReparsePoint) != 0)
            throw new IOException($"Temporary entry '{path}' became a reparse point; deletion stopped.");

        if ((attributes & AttributeDirectory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                using var childHandle = TryOpen(child, allowMissing: false, deleteAccess: true)
                    ?? throw new IOException($"Temporary entry '{child}' disappeared during deletion.");
                DeleteOpened(child, childHandle, beforeDelete);
            }
        }

        beforeDelete?.Invoke(path);
        // The open handle denies delete-sharing. Disposition-by-handle removes this inspected object even if
        // its name would otherwise be substituted after enumeration.
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw NativeError($"Could not delete managed temporary entry '{path}'", Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle? TryOpen(string path, bool allowMissing, bool deleteAccess)
    {
        var access = FileReadAttributes | (deleteAccess ? DeleteAccess : 0);
        var handle = CreateFileW(path, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (allowMissing && error is ErrorFileNotFound or ErrorPathNotFound) return null;
        throw NativeError($"Could not safely open managed temporary entry '{path}'", error);
    }

    private static uint ReadAttributes(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw NativeError($"Could not inspect managed temporary entry '{path}'", Marshal.GetLastWin32Error());
        return information.FileAttributes;
    }

    private static void DeleteManaged(string path, Action<string>? beforeDelete)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Temporary entry '{path}' became a reparse point; deletion stopped.");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteManaged(child, beforeDelete);
            beforeDelete?.Invoke(path);
            attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException($"Temporary directory '{path}' changed identity during deletion.");
            Directory.Delete(path, recursive: false);
        }
        else
        {
            beforeDelete?.Invoke(path);
            attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
                throw new IOException($"Temporary file '{path}' changed identity during deletion.");
            File.Delete(path);
        }
    }

    private static IOException NativeError(string operation, int errorCode) =>
        new($"{operation} (Win32 error {errorCode}: {new Win32Exception(errorCode).Message}).");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        ref FileDispositionInfo information, uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        internal byte DeleteFile;
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
}
