using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class ContinuousStopGateTests
{
    [Fact]
    public void ForceStopContinuesClosingOtherJobsAfterOneClosureError()
    {
        var gate = new ContinuousStopGate();
        var throwing = new FakeJob(throwOnClose: true);
        var second = new FakeJob();
        gate.RegisterActiveJob(throwing);
        gate.RegisterActiveJob(second);

        var failures = gate.RequestForceStop();

        Assert.True(gate.IsStopRequested);
        Assert.True(gate.IsForceStopRequested);
        Assert.Single(failures);
        Assert.Equal(1, throwing.CloseCount);
        Assert.Equal(1, second.CloseCount);
    }

    [Fact]
    public async Task ForceStopSerializesWithTheNativeLaunchBoundaryAndRejectsLaterLaunches()
    {
        var gate = new ContinuousStopGate();
        using var launchEntered = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var launch = Task.Run(() =>
        {
            using var slot = ContinuousAgentLaunchContext.Enter(gate);
            Assert.True(ContinuousAgentLaunchContext.TryLaunch(() =>
            {
                launchEntered.Set();
                releaseLaunch.Wait(cancellationToken);
            }));
        }, cancellationToken);
        Assert.True(launchEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));

        try
        {
            var force = Task.Run(gate.RequestForceStop, cancellationToken);
            await Task.Delay(100, cancellationToken);
            Assert.False(force.IsCompleted, "Force closure must serialize with the synchronous native launch boundary.");
            releaseLaunch.Set();
            await launch.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            _ = await force.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            releaseLaunch.Set();
        }

        using (ContinuousAgentLaunchContext.Enter(gate))
        using (ContinuousAgentLaunchContext.PermitOutcomeClassifier())
            Assert.False(ContinuousAgentLaunchContext.TryLaunch(static () => { }));
    }

    [Fact]
    public void StopClosesReservationsAndWorkingLaunchesButAllowsOutcomeFallbackForLaunchedTurn()
    {
        var gate = new ContinuousStopGate();
        var reservations = 0;
        Assert.True(gate.TryReserve(() => ++reservations, out var reserved));
        Assert.Equal(1, reserved);

        using (ContinuousAgentLaunchContext.Enter(gate))
        {
            var launches = 0;
            Assert.True(ContinuousAgentLaunchContext.TryLaunch(() => launches++));
            gate.RequestStop();

            Assert.False(gate.TryReserve(() => ++reservations, out _));
            Assert.False(ContinuousAgentLaunchContext.TryBeginExternalOperation(() => ++reservations, out _));
            Assert.False(ContinuousAgentLaunchContext.TryLaunch(() => launches++));

            using (ContinuousAgentLaunchContext.PermitOutcomeClassifier())
                Assert.True(ContinuousAgentLaunchContext.TryLaunch(() => launches++));

            Assert.Equal(2, launches);
            Assert.Equal(1, reservations);
        }

        using (ContinuousAgentLaunchContext.Enter(gate))
        using (ContinuousAgentLaunchContext.PermitOutcomeClassifier())
            Assert.False(ContinuousAgentLaunchContext.TryLaunch(static () => { }));
    }

    private sealed class FakeJob(bool throwOnClose = false) : IForceCloseAgentJob
    {
        internal int CloseCount { get; private set; }

        public void CloseForForceStop()
        {
            CloseCount++;
            if (throwOnClose) throw new IOException("fixture close failure");
        }
    }
}
