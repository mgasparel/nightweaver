using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nightweaver.Tests;

// Query-only Restart Manager usage; never shuts down or restarts a lock owner.
// https://learn.microsoft.com/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist
internal static class WindowsFileLockDiagnostics
{
    internal static void Record(string path, IOException failure)
    {
        try
        {
            var report = Capture(path);
            var directory = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "TestResults", "cleanup-locks"));
            var reportPath = Path.Combine(directory.FullName, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(reportPath, report);
            failure.Data["CleanupLockReport"] = reportPath;
            Console.Error.WriteLine($"Cleanup lock report: {reportPath}; {report}");
        }
        catch (Exception captureFailure)
        {
            // Diagnostics must never replace the original disposal failure.
            failure.Data["CleanupLockCaptureFailure"] = captureFailure.ToString();
        }
    }

    internal static string Capture(string path)
    {
        var result = RmStartSession(out var session, 0, new char[33]);
        if (result != 0) throw new Win32Exception(result);
        try
        {
            result = RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != 0) throw new Win32Exception(result);
            uint count = 0;
            result = RmGetList(session, out var needed, ref count, null, out _);
            if (result is not (0 or 234)) throw new Win32Exception(result);
            var processes = new ProcessInfo[needed];
            count = needed;
            if (needed > 0)
            {
                result = RmGetList(session, out _, ref count, processes, out _);
                if (result != 0) throw new Win32Exception(result);
            }
            return JsonSerializer.Serialize(new
            {
                CapturedUtc = DateTime.UtcNow,
                Path = path,
                ObserverPid = Environment.ProcessId,
                Owners = processes.Take((int)count).Select(item => new
                {
                    Pid = item.Process.Id,
                    StartFileTime = ((long)item.Process.Start.dwHighDateTime << 32) | (uint)item.Process.Start.dwLowDateTime,
                    item.AppName,
                    item.AppStatus,
                    ProcessObservation = ObserveOwner(item.Process.Id,
                        ((long)item.Process.Start.dwHighDateTime << 32) | (uint)item.Process.Start.dwLowDateTime)
                })
            });
        }
        finally { _ = RmEndSession(session); }
    }

    internal sealed record OwnerObservation(long? ObservedStartFileTime, bool? IdentityMatches,
        bool? HasExited, uint? NativeWaitStatus, string? ExecutablePath, string? QueryError);

    internal static OwnerObservation ObserveOwner(int processId, long expectedStartFileTime)
    {
        long? start = null;
        bool? identityMatches = null;
        bool? hasExited = null;
        uint? nativeWaitStatus = null;
        string? executable = null;
        string? error = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            _ = process.SafeHandle;
            start = process.StartTime.ToFileTimeUtc();
            identityMatches = start == expectedStartFileTime;
            if (identityMatches.Value)
            {
                hasExited = process.HasExited;
                nativeWaitStatus = WaitForSingleObject(process.SafeHandle, 0);
                if (nativeWaitStatus == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (nativeWaitStatus == 258)
                    executable = process.MainModule?.FileName;
            }
        }
        catch (Exception failure)
        {
            // An owner may disappear or become inaccessible after Restart Manager's snapshot.
            error = $"{failure.GetType().Name}: {failure.Message}";
        }
        return new OwnerObservation(start, identityMatches, hasExited, nativeWaitStatus, executable, error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Id;
        public System.Runtime.InteropServices.ComTypes.FILETIME Start;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, [Out] char[] key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] paths,
        uint applications, IntPtr processes, uint services, IntPtr names);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? processes, out uint reasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint milliseconds);
}

public sealed class WindowsFileLockDiagnosticsTests
{
    [Fact]
    public void FindsKnownFileOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var path = temp.WriteFile("locked.log", "probe");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var report = JsonDocument.Parse(WindowsFileLockDiagnostics.Capture(path));
        Assert.Contains(report.RootElement.GetProperty("Owners").EnumerateArray(),
            owner => owner.GetProperty("Pid").GetInt32() == Environment.ProcessId);
        var owner = Assert.Single(report.RootElement.GetProperty("Owners").EnumerateArray(),
            item => item.GetProperty("Pid").GetInt32() == Environment.ProcessId);
        var observation = owner.GetProperty("ProcessObservation");
        Assert.True(observation.GetProperty("IdentityMatches").GetBoolean());
        Assert.False(observation.GetProperty("HasExited").GetBoolean());
        Assert.Equal(258u, observation.GetProperty("NativeWaitStatus").GetUInt32());
        Assert.Equal(Environment.ProcessPath, observation.GetProperty("ExecutablePath").GetString());
        Assert.Equal(JsonValueKind.Null, observation.GetProperty("QueryError").ValueKind);
        Assert.True(owner.TryGetProperty("AppStatus", out _));
    }

    [Fact]
    public void ReusedIdentityDoesNotAttributeCurrentExecutableToTheOldOwner()
    {
        using var current = Process.GetCurrentProcess();
        var actualStart = current.StartTime.ToFileTimeUtc();
        var observation = WindowsFileLockDiagnostics.ObserveOwner(current.Id, actualStart + 1);
        Assert.Equal(actualStart, observation.ObservedStartFileTime);
        Assert.False(observation.IdentityMatches);
        Assert.Null(observation.HasExited);
        Assert.Null(observation.NativeWaitStatus);
        Assert.Null(observation.ExecutablePath);
        Assert.Null(observation.QueryError);
    }

    [Fact]
    public void MissingOwnerDoesNotReplaceThePrimaryRestartManagerObservation()
    {
        var observation = WindowsFileLockDiagnostics.ObserveOwner(-1, 123);
        Assert.Null(observation.IdentityMatches);
        Assert.Null(observation.HasExited);
        Assert.Null(observation.NativeWaitStatus);
        Assert.Null(observation.ExecutablePath);
        Assert.NotEmpty(observation.QueryError!);
    }

    [Fact]
    public void CleanupFailureKeepsOriginalErrorAndReportsKnownOwnerWithoutDeletingItsFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var path = temp.WriteFile("locked.log", "retained diagnostic evidence");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var failure = Assert.Throws<IOException>(temp.Dispose);
            Assert.Equal(32, failure.HResult & 0xFFFF);
            var reportPath = Assert.IsType<string>(failure.Data["CleanupLockReport"]);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(path, report.RootElement.GetProperty("Path").GetString());
            Assert.Contains(report.RootElement.GetProperty("Owners").EnumerateArray(), owner =>
                owner.GetProperty("Pid").GetInt32() == Environment.ProcessId
                && owner.GetProperty("StartFileTime").GetInt64() > 0);
            Assert.True(File.Exists(path));
            Assert.True(stream.CanWrite);
        }
        finally
        {
            stream.Dispose();
        }
    }

    [Fact]
    public void UnsupportedResourceDoesNotReplaceTheOriginalCleanupFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var failure = new IOException("original cleanup failure");
        var hresult = failure.HResult;
        // Restart Manager rejects directory resources with access denied.
        WindowsFileLockDiagnostics.Record(temp.Path, failure);
        Assert.Null(failure.Data["CleanupLockReport"]);
        Assert.IsType<string>(failure.Data["CleanupLockCaptureFailure"]);
        Assert.Equal("original cleanup failure", failure.Message);
        Assert.Equal(hresult, failure.HResult);
    }
}
