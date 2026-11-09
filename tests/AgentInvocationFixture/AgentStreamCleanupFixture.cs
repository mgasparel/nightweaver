using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nightweaver.Tests.AgentInvocationFixture;

internal static class AgentStreamCleanupFixture
{
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x00000002;

    internal static int RunProducer(string[] args)
    {
        if (args.Length != 5) return 64;
        var coordinatorPid = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var reportPath = args[2];
        var releasePath = args[3];
        var inheritable = bool.Parse(args[4]);
        using var output = Console.OpenStandardOutput();

        var coordinator = OpenProcess(ProcessDuplicateHandle, inheritHandle: false, checked((uint)coordinatorPid));
        if (coordinator == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(coordinator)");

        try
        {
            var process = GetCurrentProcess();
            var stdoutSource = GetStdHandle(-11);
            var stderrSource = GetStdHandle(-12);
            if (stdoutSource == IntPtr.Zero || stdoutSource == new IntPtr(-1)
                || stderrSource == IntPtr.Zero || stderrSource == new IntPtr(-1))
                throw new InvalidOperationException("Producer standard output or error handle is unavailable.");

            if (!DuplicateHandle(process, stdoutSource, coordinator, out var stdoutCopy, 0,
                    inheritable, DuplicateSameAccess))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle(stdout)");
            if (!DuplicateHandle(process, stderrSource, coordinator, out var stderrCopy, 0,
                    inheritable, DuplicateSameAccess))
            {
                CloseRemoteHandle(coordinator, stdoutCopy);
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle(stderr)");
            }

            var current = Process.GetCurrentProcess();
            WriteJsonAtomically(reportPath, new ProducerReport(
                Environment.ProcessId,
                current.StartTime.ToUniversalTime().Ticks,
                stdoutCopy.ToInt64(),
                stderrCopy.ToInt64(),
                inheritable,
                DateTimeOffset.UtcNow));

            output.Write(Encoding.UTF8.GetBytes("prefix-"));
            output.Write([0xE2, 0x82]);
            output.Flush();
            WaitForFile(releasePath, TimeSpan.FromSeconds(20));
            output.Write([0xAC]);
            output.Write(Encoding.UTF8.GetBytes("-suffix\n"));
            output.Flush();
            return 0;
        }
        finally
        {
            CloseHandle(coordinator);
        }
    }

    internal static int RunRaceProducer(string[] args)
    {
        if (args.Length != 3) return 64;
        var reportPath = args[1];
        var releasePath = args[2];
        using var output = Console.OpenStandardOutput();
        using var current = Process.GetCurrentProcess();
        WriteJsonAtomically(reportPath, new ProducerReport(
            Environment.ProcessId,
            current.StartTime.ToUniversalTime().Ticks,
            0,
            0,
            false,
            DateTimeOffset.UtcNow));
        output.Write(Encoding.UTF8.GetBytes("prefix-"));
        output.Write([0xE2, 0x82]);
        output.Flush();
        WaitForFile(releasePath, TimeSpan.FromSeconds(20));
        output.Write([0xAC]);
        output.Write(Encoding.UTF8.GetBytes("-suffix\n"));
        output.Flush();
        return 0;
    }

    internal static int RunHolder(string[] args)
    {
        if (args.Length != 3) return 64;
        var readyPath = args[1];
        var releasePath = args[2];
        using var current = Process.GetCurrentProcess();
        WriteJsonAtomically(readyPath, new HolderReport(
            Environment.ProcessId,
            current.StartTime.ToUniversalTime().Ticks,
            DateTimeOffset.UtcNow));
        WaitForFile(releasePath, TimeSpan.FromSeconds(30));
        return 0;
    }

    internal static void CloseRemoteHandle(IntPtr process, long value)
    {
        if (!DuplicateHandle(process, new IntPtr(value), process, out var replacement, 0,
                inheritHandle: false, DuplicateSameAccess | DuplicateCloseSource))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle(close remote)");
        if (replacement != IntPtr.Zero && !CloseHandle(replacement))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CloseHandle(replacement)");
    }

    internal static void CloseRemoteHandle(IntPtr process, IntPtr value)
        => CloseRemoteHandle(process, value.ToInt64());

    internal static IntPtr OpenProcessForDuplicateHandles(int processId)
    {
        var process = OpenProcess(ProcessDuplicateHandle, inheritHandle: false, checked((uint)processId));
        if (process == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(duplicate handles)");
        return process;
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(value), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: false);
    }

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException($"Stream-cleanup fixture gate timed out: {path}");
            Thread.Sleep(10);
        }
    }

    internal sealed record ProducerReport(int ProcessId, long StartTimeUtcTicks, long StdoutHandle,
        long StderrHandle, bool Inheritable, DateTimeOffset DuplicatesCreatedAtUtc);
    internal sealed record HolderReport(int ProcessId, long StartTimeUtcTicks, DateTimeOffset ReadyAtUtc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    private const uint DuplicateCloseSource = 0x00000001;
}
