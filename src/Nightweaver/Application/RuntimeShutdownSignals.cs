using Nightweaver.Execution;

namespace Nightweaver.Application;

/// <summary>Owns graceful first-stop and direct force-exit second-signal paths for the console runtime.</summary>
internal sealed class RuntimeShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource firstStop = new();
    private readonly Action<int> terminateProcess;
    private int signalCount;
    private int forceStopStarted;

    internal RuntimeShutdownSignals(Action<int>? terminateProcess = null) =>
        this.terminateProcess = terminateProcess ?? Environment.Exit;

    internal ContinuousStopGate StopGate { get; } = new();
    internal CancellationToken FirstStopToken => firstStop.Token;

    internal void RequestStop()
    {
        if (Interlocked.CompareExchange(ref signalCount, 1, 0) == 0)
        {
            StopGate.RequestStop();
            firstStop.Cancel();
            return;
        }

        if (Interlocked.CompareExchange(ref forceStopStarted, 1, 0) != 0)
            return;

        _ = StopGate.RequestForceStop();
        TryStartDeferredRecoveryDiagnostic();
        terminateProcess(ExitCodes.InspectionFailure);
    }

    private static void TryStartDeferredRecoveryDiagnostic()
    {
        try
        {
            var reporter = new Thread(static () =>
            {
                try
                {
                    Console.Error.WriteLine("nightweaver: force stop closed active Agent process trees; unfinished operations may be uncertain and recovery is deferred until restart.");
                }
                catch (Exception) { }
            })
            {
                IsBackground = true,
                Name = "Nightweaver force-stop diagnostic"
            };
            reporter.Start();
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        firstStop.Dispose();
    }
}
