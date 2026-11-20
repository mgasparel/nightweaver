using Nightweaver.Execution;

namespace Nightweaver.Application;

/// <summary>Owns graceful first-stop and direct force-exit second-signal paths for the console runtime.</summary>
internal sealed class RuntimeShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource firstStop = new();
    private readonly Action<int> terminateProcess;
    private readonly Action? gracefulStopAccepted;
    private readonly object intentGate = new();
    private Action? persistIntentionalStop;
    private bool intentionalStopPending;
    private bool intentPersistenceStarted;
    internal Action? PersistIntentionalStop
    {
        set
        {
            lock (intentGate) persistIntentionalStop = value;
            PersistPendingIntentionalStop();
        }
    }
    internal string? IntentPersistenceFailure { get; private set; }
    private int reason;
    internal RuntimeShutdownReason Reason => (RuntimeShutdownReason)Volatile.Read(ref reason);
    private int signalCount;
    private int forceStopStarted;

    internal RuntimeShutdownSignals(Action<int>? terminateProcess = null, Action? gracefulStopAccepted = null)
    {
        this.terminateProcess = terminateProcess ?? Environment.Exit;
        this.gracefulStopAccepted = gracefulStopAccepted;
    }

    internal ContinuousStopGate StopGate { get; } = new();
    internal CancellationToken FirstStopToken => firstStop.Token;

    internal void RequestStop()
    {
        if (Interlocked.CompareExchange(ref signalCount, 1, 0) == 0)
        {
            BeginGracefulStop(RuntimeShutdownReason.OperatorDrain, false);
            return;
        }

        RequestForceStop();
    }

    /// <summary>Requests a graceful drain once without interpreting duplicate owner events as force signals.</summary>
    internal void RequestGracefulStop(RuntimeShutdownReason stopReason = RuntimeShutdownReason.OperatorDrain, bool intentAlreadyPersisted = false)
    {
        if (Interlocked.CompareExchange(ref signalCount, 1, 0) != 0) return;
        BeginGracefulStop(stopReason, intentAlreadyPersisted);
    }

    /// <summary>Closes active Agent Jobs immediately, preserving the nonwaiting force-stop path.</summary>
    internal void RequestForceStop()
    {
        Interlocked.CompareExchange(ref signalCount, 1, 0);
        if (Interlocked.CompareExchange(ref forceStopStarted, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref reason, (int)RuntimeShutdownReason.ForcedStop);
        _ = StopGate.RequestForceStop();
        TryStartDeferredRecoveryDiagnostic();
        terminateProcess(ExitCodes.InspectionFailure);
    }

    private void BeginGracefulStop(RuntimeShutdownReason stopReason, bool intentAlreadyPersisted)
    {
        // Close admission before potentially blocking intent IO; cancellation still follows persistence.
        StopGate.RequestStop();
        Interlocked.CompareExchange(ref reason, (int)stopReason, 0);
        if (stopReason == RuntimeShutdownReason.OperatorDrain && !intentAlreadyPersisted)
        {
            lock (intentGate) intentionalStopPending = true;
            PersistPendingIntentionalStop();
        }
        firstStop.Cancel();
        try { gracefulStopAccepted?.Invoke(); }
        catch (Exception) { }
    }

    private void PersistPendingIntentionalStop()
    {
        Action persist;
        lock (intentGate)
        {
            if (!intentionalStopPending || intentPersistenceStarted || persistIntentionalStop is null
                || Volatile.Read(ref forceStopStarted) != 0) return;
            intentPersistenceStarted = true;
            persist = persistIntentionalStop;
        }
        try { persist(); }
        catch (Exception exception) { IntentPersistenceFailure = exception.Message; }
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

internal enum RuntimeShutdownReason { None, OperatorDrain, ForcedStop, Maintenance, WebFailure, RuntimeFailure }
