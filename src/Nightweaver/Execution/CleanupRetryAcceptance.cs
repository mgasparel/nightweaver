using Nightweaver.Background;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

/// <summary>Ephemeral, runtime-specific cleanup admission signal for the Windows live owner.
/// Stop resets it synchronously; process exit closes it. Durable requests remain in the inbox.</summary>
internal sealed class CleanupRetryAcceptance : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly ContinuousStopGate gate;

    internal CleanupRetryAcceptance(HostPaths paths, ProjectConfig project, string runtimeId, ContinuousStopGate gate)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live cleanup Retry requires Windows.");
        signal = new EventWaitHandle(false, EventResetMode.ManualReset, Name(paths, project, runtimeId));
        this.gate = gate;
        gate.RegisterStopSignal(signal);
    }

    internal static void RequireAccepting(HostPaths paths, ProjectConfig project, string runtimeId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live cleanup Retry requires Windows.");
        if (EventWaitHandle.TryOpenExisting(Name(paths, project, runtimeId), out var signal))
        {
            using (signal)
                if (signal.WaitOne(0)) return;
        }
        throw new InvalidOperationException("The runtime owner is not accepting cleanup Retry requests.");
    }

    private static string Name(HostPaths paths, ProjectConfig project, string runtimeId) =>
        BackgroundIdentity.InstanceMutexName(paths, project.Name) + "_CleanupRetry_v1_" + BackgroundIdentity.Hash(runtimeId);

    public void Dispose()
    {
        gate.UnregisterStopSignal(signal);
        signal.Dispose();
    }
}
