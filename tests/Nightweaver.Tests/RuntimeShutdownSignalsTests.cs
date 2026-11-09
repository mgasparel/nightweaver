using Nightweaver.Application;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class RuntimeShutdownSignalsTests
{
    [Fact]
    public void SecondSignalSelectsForceOnceAndDeniesClassifierLaunches()
    {
        var exits = new List<int>();
        using var shutdown = new RuntimeShutdownSignals(exits.Add);
        var launches = 0;

        using (ContinuousAgentLaunchContext.Enter(shutdown.StopGate))
        {
            Assert.True(ContinuousAgentLaunchContext.TryLaunch(() => launches++));
            shutdown.RequestStop();
            using (ContinuousAgentLaunchContext.PermitOutcomeClassifier())
                Assert.True(ContinuousAgentLaunchContext.TryLaunch(() => launches++));

            shutdown.RequestStop();
            shutdown.RequestStop();

            using (ContinuousAgentLaunchContext.PermitOutcomeClassifier())
                Assert.False(ContinuousAgentLaunchContext.TryLaunch(() => launches++));
        }

        Assert.True(shutdown.StopGate.IsForceStopRequested);
        Assert.Equal(2, launches);
        Assert.Equal([1], exits);
    }

    [Fact]
    public async Task SecondSignalExitsWhileFirstSignalCancellationCallbackIsBlocked()
    {
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var forceExit = new ManualResetEventSlim();
        using var shutdown = new RuntimeShutdownSignals(_ => forceExit.Set());
        using var registration = shutdown.FirstStopToken.Register(() =>
        {
            callbackEntered.Set();
            releaseCallback.Wait();
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        var firstSignal = Task.Run(shutdown.RequestStop, cancellationToken);
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken), "The first-signal callback did not enter its gate.");

        try
        {
            var secondSignal = Task.Run(shutdown.RequestStop, cancellationToken);
            Assert.True(forceExit.Wait(TimeSpan.FromSeconds(5), cancellationToken), "Force exit waited for a blocked first-signal cancellation callback.");
            await secondSignal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            releaseCallback.Set();
            await firstSignal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    [Fact]
    public void GracefulStopObserverRunsOnceAfterGateAndCancellation()
    {
        var observations = 0;
        var observerSawAcceptedState = false;
        RuntimeShutdownSignals? observedShutdown = null;
        using var shutdown = new RuntimeShutdownSignals(gracefulStopAccepted: () =>
        {
            observations++;
            observerSawAcceptedState = observedShutdown is { } current
                && current.StopGate.IsStopRequested
                && current.FirstStopToken.IsCancellationRequested;
        });
        observedShutdown = shutdown;
        shutdown.RequestGracefulStop();
        shutdown.RequestGracefulStop();

        Assert.Equal(1, observations);
        Assert.True(observerSawAcceptedState);
    }

    [Fact]
    public void ForceFirstDoesNotNotifyGracefulStopObserver()
    {
        var observations = 0;
        using var shutdown = new RuntimeShutdownSignals(_ => { }, () => observations++);

        shutdown.RequestForceStop();

        Assert.Equal(0, observations);
    }

    [Fact]
    public async Task SecondSignalExitsWhileAcceptedStopObserverIsBlocked()
    {
        using var observerEntered = new ManualResetEventSlim();
        using var releaseObserver = new ManualResetEventSlim();
        using var forceExit = new ManualResetEventSlim();
        using var shutdown = new RuntimeShutdownSignals(_ => forceExit.Set(), () =>
        {
            observerEntered.Set();
            releaseObserver.Wait();
        });
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstSignal = Task.Run(shutdown.RequestStop, cancellationToken);
        Assert.True(observerEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken),
            "The accepted-stop observer did not enter after gate closure and cancellation.");

        try
        {
            var secondSignal = Task.Run(shutdown.RequestStop, cancellationToken);
            Assert.True(forceExit.Wait(TimeSpan.FromSeconds(5), cancellationToken),
                "Force exit waited for a blocked accepted-stop observer.");
            await secondSignal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            releaseObserver.Set();
            await firstSignal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    [Fact]
    public void ThrowingAcceptedStopObserverDoesNotUndoGracefulOrForceStop()
    {
        var exits = new List<int>();
        using var shutdown = new RuntimeShutdownSignals(exits.Add,
            () => throw new InvalidOperationException("diagnostic output failed"));

        shutdown.RequestGracefulStop();

        Assert.True(shutdown.StopGate.IsStopRequested);
        Assert.True(shutdown.FirstStopToken.IsCancellationRequested);
        Assert.Empty(exits);

        shutdown.RequestStop();

        Assert.True(shutdown.StopGate.IsForceStopRequested);
        Assert.Equal([1], exits);
    }
}
