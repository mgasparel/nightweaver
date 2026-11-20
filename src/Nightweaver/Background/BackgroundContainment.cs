using System.Runtime.InteropServices;
using Nightweaver.Execution;

namespace Nightweaver.Background;

internal enum OwnerProcessState { Alive, Exited, Reused, Unavailable }
internal sealed record OwnerProcessObservation(OwnerProcessState State, bool? DescendantsSettled);

internal static class BackgroundContainment
{
    internal static string Name(BackgroundOwnerRecord owner) => "Local\\Nightweaver.Owner." + BackgroundIdentity.Hash(
        Path.GetFullPath(owner.Root).ToUpperInvariant() + "\n" + Path.GetFullPath(owner.ConfigPath).ToUpperInvariant()
        + "\n" + owner.Project + "\n" + owner.RunId + "\n" + owner.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "\n" + owner.ProcessStartedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static OwnerProcessObservation Read(BackgroundOwnerRecord owner)
    {
        if (!OperatingSystem.IsWindows()) return new(OwnerProcessState.Unavailable, null);
        using var process = OpenProcess(0x00100000 | 0x1000, false, owner.ProcessId); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (process.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() != 87) return new(OwnerProcessState.Unavailable, null);
        }
        else
        {
            if (!GetProcessTimes(process, out var creation, out _, out _, out _))
                return new(OwnerProcessState.Unavailable, null);
            if (DateTimeOffset.FromFileTime(creation) != owner.ProcessStartedAtUtc)
                return new(OwnerProcessState.Reused, null);
            var wait = WaitForSingleObject(process, 0);
            if (wait == 258) return new(OwnerProcessState.Alive, false);
            if (wait != 0) return new(OwnerProcessState.Unavailable, null);
        }
        return new(OwnerProcessState.Exited, owner.ContainmentVersion == 1
            ? WindowsProcessLauncher.IsExitedOwnerJobEmpty(Name(owner)) : null);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern WindowsProcessLauncher.SafeKernelHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(WindowsProcessLauncher.SafeKernelHandle process,
        out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(WindowsProcessLauncher.SafeKernelHandle process, uint milliseconds);
}
