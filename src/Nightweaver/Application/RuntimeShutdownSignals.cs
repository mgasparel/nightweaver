using Nightweaver.Execution;

namespace Nightweaver.Application;

/// <summary>Owns the graceful first-stop and force-cancel second-signal paths for the console runtime.</summary>
internal sealed class RuntimeShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource firstStop = new();
    private readonly CancellationTokenSource forceStop = new();
    private int signalCount;

    internal ContinuousStopGate StopGate { get; } = new();
    internal CancellationToken FirstStopToken => firstStop.Token;
    internal CancellationToken ForceStopToken => forceStop.Token;

    internal void RequestStop()
    {
        if (Interlocked.Increment(ref signalCount) == 1)
        {
            StopGate.RequestStop();
            firstStop.Cancel();
        }
        else
        {
            forceStop.Cancel();
        }
    }

    public void Dispose()
    {
        firstStop.Dispose();
        forceStop.Dispose();
    }
}
