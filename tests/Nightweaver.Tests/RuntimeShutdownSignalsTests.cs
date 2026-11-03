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
}
