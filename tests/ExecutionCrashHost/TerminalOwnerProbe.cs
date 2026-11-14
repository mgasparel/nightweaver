using System.ComponentModel;
using System.Runtime.InteropServices;
using Nightweaver.Execution;

namespace Nightweaver.Tests.ExecutionCrashHost;

public static class TerminalOwnerProbe
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["child"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args is ["owner", var ready, var cleanupJobName])
        {
            // The test owns the only lasting handle. If it fails before reading
            // readiness, closing that handle still contains this detached owner.
            using (var cleanupJob = OpenJobObjectW(1, false, cleanupJobName))
            {
                if (cleanupJob.IsInvalid || !AssignProcessToJobObject(cleanupJob, GetCurrentProcess()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using var ownerJob = WindowsProcessLauncher.AttachCurrentProcessToKillOnCloseJob();
            using var child = WindowsProcessLauncher.Launch(Environment.ProcessPath!,
                ["--terminal-owner-probe", "child"], Environment.CurrentDirectory,
                new Dictionary<string, string>());
            File.WriteAllText(ready + ".child.tmp", child.ProcessId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(ready + ".child.tmp", ready + ".child");
            File.WriteAllText(ready + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(ready + ".tmp", ready);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args is not [var mode, var path, var cleanupName] || mode is not ("terminal" or "inherited-host" or "restricted")) return 64;
        using var terminalJob = CreateJobObjectW(IntPtr.Zero, null);
        if (terminalJob.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits
            {
                Flags = JobObjectLimitKillOnJobClose | (mode == "restricted" ? 0u : JobObjectLimitBreakawayOk)
            }
        };
        if (!SetInformationJobObject(terminalJob, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            || (mode != "inherited-host" && !AssignProcessToJobObject(terminalJob, GetCurrentProcess())))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var isInJob))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var canBreakAway = true;
        if (isInJob)
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 9, out var callerLimits,
                    (uint)Marshal.SizeOf<ExtendedLimits>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            canBreakAway = (callerLimits.Basic.Flags & (JobObjectLimitBreakawayOk | JobObjectLimitSilentBreakawayOk)) != 0;
        }
        Console.WriteLine(canBreakAway ? "caller-policy=allow" : "caller-policy=deny");

        try
        {
            using var owner = WindowsProcessLauncher.LaunchDetached(Environment.ProcessPath!,
                ["--terminal-owner-probe", "owner", path, cleanupName], Environment.CurrentDirectory);
            Console.WriteLine($"detached-owner={owner.ProcessId}");
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine($"detachment-error={exception.NativeErrorCode}: {exception.Message}");
            return exception.NativeErrorCode;
        }
    }

    public static SafeHandle CreateCleanupJob(string name)
    {
        var job = CreateJobObjectW(IntPtr.Zero, name);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = JobObjectLimitKillOnJobClose } };
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
        var error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(error);
    }

    public static async Task StopCleanupJobAsync(SafeHandle job, CancellationToken cancellationToken)
    {
        if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        while (true)
        {
            if (!QueryInformationJobObject(job, 1, out var accounting,
                    (uint)Marshal.SizeOf<Accounting>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        internal long UserTime;
        internal long KernelTime;
        internal long PeriodUserTime;
        internal long PeriodKernelTime;
        internal uint PageFaults;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern WindowsProcessLauncher.SafeKernelHandle OpenJobObjectW(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeHandle job, int informationClass,
        out Accounting information, uint length, out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime;
        internal long JobTime;
        internal uint Flags;
        internal nuint MinimumWorkingSet;
        internal nuint MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations;
        internal ulong WriteOperations;
        internal ulong OtherOperations;
        internal ulong ReadBytes;
        internal ulong WriteBytes;
        internal ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic;
        internal IoCounters Io;
        internal nuint ProcessMemory;
        internal nuint JobMemory;
        internal nuint PeakProcessMemory;
        internal nuint PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern WindowsProcessLauncher.SafeKernelHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(WindowsProcessLauncher.SafeKernelHandle job, int informationClass,
        ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(WindowsProcessLauncher.SafeKernelHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out ExtendedLimits information, uint length, out uint returnLength);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
