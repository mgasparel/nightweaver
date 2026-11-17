using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class ProbeMetrics
{
    internal static object? ReadIo(Process process)
        => OperatingSystem.IsWindows() && GetProcessIoCounters(process.SafeHandle, out var io)
            ? new { io.ReadOperations, io.WriteOperations, io.OtherOperations, io.ReadBytes, io.WriteBytes, io.OtherBytes } : null;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

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
}
