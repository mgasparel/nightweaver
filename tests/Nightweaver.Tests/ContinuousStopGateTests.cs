using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class ContinuousStopGateTests
{
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
}
