using System.ComponentModel;
using System.Runtime.InteropServices;
using Nightweaver.Execution;

namespace Nightweaver.Tests.ExecutionCrashHost;

internal static class TerminalOwnerProbe
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["child"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        if (args is ["owner", var ready])
        {
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

        if (args is not [var mode, var path] || mode is not ("terminal" or "standalone" or "restricted")) return 64;
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
            || (mode != "standalone" && !AssignProcessToJobObject(terminalJob, GetCurrentProcess())))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            using var owner = WindowsProcessLauncher.LaunchDetached(Environment.ProcessPath!,
                ["--terminal-owner-probe", "owner", path], Environment.CurrentDirectory);
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
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
