using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Tests;

public sealed class TempDirectoryCleanupTests
{
    [Fact]
    public async Task DisposeRetriesTransientWindowsDirectorySharingViolation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var temp = new TempDirectory();
        using var directoryHandle = OpenDirectoryWithoutDeleteSharing(temp.Path);
        var initialFailure = Assert.Throws<IOException>(() => Directory.Delete(temp.Path));
        Assert.Equal(32, initialFailure.HResult & 0xFFFF);

        var releaseHandle = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            directoryHandle.Dispose();
        }, TestContext.Current.CancellationToken);

        try
        {
            temp.Dispose();
            await releaseHandle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(temp.Path));
        }
        finally
        {
            directoryHandle.Dispose();
            if (Directory.Exists(temp.Path)) temp.Dispose();
        }
    }

    [Fact]
    public void DisposeStillFailsWhenWindowsDirectoryRemainsLocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        var temp = new TempDirectory();
        using var directoryHandle = OpenDirectoryWithoutDeleteSharing(temp.Path);
        try
        {
            var failure = Assert.Throws<IOException>(temp.Dispose);
            Assert.Equal(32, failure.HResult & 0xFFFF);
            Assert.True(Directory.Exists(temp.Path));
        }
        finally
        {
            directoryHandle.Dispose();
            if (Directory.Exists(temp.Path)) temp.Dispose();
        }
    }

    private static SafeFileHandle OpenDirectoryWithoutDeleteSharing(string path)
    {
        var handle = CreateFileW(path, 0x00010000, FileShare.Read | FileShare.Write, IntPtr.Zero,
            3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
