using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class SupervisionPolicyTests
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Epoch = Guid.NewGuid().ToString("N");
    private static readonly SupervisionOwner Owner = new(123, Start.AddMinutes(-1), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));

    internal static SupervisionIdentity Identity(string root) =>
        new(root, Path.Combine(root, "projects", "demo.json"), Path.Combine(root, "repo", ".git"), "S-1-5-21-123-456-789-1001");
    private static SupervisionState Enabled() => SupervisionPolicy.SetIntent(
        SupervisionState.Manual(Identity(Path.GetFullPath("supervision-fixture"))), true, true, null);
    private static SupervisionObservation Failed(int seconds, string? epoch = null) =>
        new(epoch ?? Epoch, TimeSpan.FromSeconds(seconds).Ticks, Start.AddSeconds(seconds), SupervisionHealth.Failed, Owner);
    private static SupervisionState Qualify(SupervisionState state, int seconds, string? epoch = null)
    {
        state = SupervisionPolicy.Observe(state, Failed(seconds, epoch));
        return SupervisionPolicy.Observe(state, Failed(seconds + 60, epoch));
    }
    private static SupervisionIncident Incident(int seconds, string reason = "start-failed") =>
        new(Guid.NewGuid().ToString("N"), Start.AddSeconds(seconds), reason, Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReservationRequiresMatchingSeparatedFailuresAndDueTime()
    {
        var state = Enabled();
        state = SupervisionPolicy.Observe(state, Failed(0));
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, Failed(0), Guid.NewGuid().ToString("N")));
        state = SupervisionPolicy.Observe(state, Failed(60));
        Assert.Equal(Start.AddSeconds(90), state.NextDueUtc);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, Failed(60), Guid.NewGuid().ToString("N")));
        state = SupervisionPolicy.Observe(state, Failed(120));
        state = SupervisionPolicy.Reserve(state, Failed(120), Guid.NewGuid().ToString("N"));
        Assert.Equal(1, state.Attempts);
        Assert.Equal(Owner, state.PendingAttempt!.PreviousOwner);
        Assert.Null(state.NextDueUtc);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, Failed(120), Guid.NewGuid().ToString("N")));
        state = Qualify(state, 180);
        SupervisionValidation.Validate(state);
        Assert.Null(state.NextDueUtc);
        Assert.NotNull(state.PendingAttempt);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reset(state));
    }

    [Fact]
    public void ThreeAttemptsUsePersistedBackoffAndOnlyExplicitResetRefundsBudget()
    {
        var state = Enabled();
        var seconds = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            state = Qualify(state, seconds);
            seconds += 120;
            if (state.NextDueUtc > Start.AddSeconds(seconds))
                seconds = (int)(state.NextDueUtc.Value - Start).TotalSeconds;
            // A large gap needs two new matching failures, even when the old due time has passed.
            state = Qualify(state, seconds);
            seconds += 60;
            var observation = Failed(seconds);
            state = SupervisionPolicy.Reserve(state, observation, Guid.NewGuid().ToString("N"));
            Assert.Equal(attempt, state.Attempts);
            state = SupervisionPolicy.Settle(state, state.PendingAttempt!.Id, Start.AddSeconds(seconds), true, Incident(seconds));
            Assert.Equal(attempt == 3, state.Exhausted);
            if (attempt < 3)
                Assert.Equal(Start.AddSeconds(seconds) + SupervisionPolicy.Backoff(attempt), state.NextDueUtc);
            seconds += 60;
        }
        var exhausted = state;
        state = SupervisionPolicy.Observe(state, Failed(seconds) with { Health = SupervisionHealth.Healthy });
        state = SupervisionPolicy.Observe(state, Failed(seconds + 60, Guid.NewGuid().ToString("N")));
        Assert.Equal(3, state.Attempts);
        Assert.True(state.Exhausted);
        state = SupervisionPolicy.Reset(state);
        Assert.Equal(0, state.Attempts);
        Assert.False(state.Exhausted);
        Assert.Equal(exhausted.LastIncident, state.LastIncident);
        Assert.Equal(exhausted.History, state.History);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Gap")]
    [InlineData("Draining")]
    [InlineData("Healthy")]
    public void ObservationsWithoutConfirmedFailureNeverGrantRecovery(string health)
    {
        var state = Qualify(Enabled(), 0);
        var observation = Failed(120) with { Health = Enum.Parse<SupervisionHealth>(health) };
        state = SupervisionPolicy.Observe(state, observation);
        Assert.Equal(0, state.MatchingFailures);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, observation, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void NewEpochChangedOwnerDuplicatesAndSuspendGapRequireFreshQualification()
    {
        foreach (var observation in new[]
        {
            Failed(120, Guid.NewGuid().ToString("N")),
            Failed(120) with { Owner = Owner with { StartedUtc = Start } },
            Failed(61), Failed(600),
            Failed(120) with { Utc = Start.AddHours(1) }
        })
        {
            var state = SupervisionPolicy.Observe(Qualify(Enabled(), 0), observation);
            Assert.Equal(1, state.MatchingFailures);
            Assert.Equal(Start.AddSeconds(90), state.NextDueUtc);
            Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, observation, Guid.NewGuid().ToString("N")));
        }
    }

    [Fact]
    public void BackwardClocksRequireInspectionAndExplicitReset()
    {
        foreach (var observation in new[] { Failed(59), Failed(120) with { MonotonicTicks = 0 } })
        {
            var state = SupervisionPolicy.Observe(Qualify(Enabled(), 0), observation);
            Assert.Equal("clock-discontinuity", state.Attention);
            state = Qualify(state, 120);
            Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, Failed(180), Guid.NewGuid().ToString("N")));
            Assert.Null(SupervisionPolicy.Reset(state).Attention);
        }

        var pending = SupervisionPolicy.Observe(Qualify(Enabled(), 0), Failed(120));
        pending = SupervisionPolicy.Reserve(pending, Failed(120), Guid.NewGuid().ToString("N"));
        pending = SupervisionPolicy.Observe(pending, Failed(119));
        var attemptId = pending.PendingAttempt!.Id;
        pending = SupervisionPolicy.Settle(pending, attemptId, Start.AddSeconds(121), false, Incident(121, "settlement-unknown"));
        pending = SupervisionPolicy.Settle(pending, attemptId, Start.AddSeconds(122), true, Incident(122));
        Assert.Equal("clock-discontinuity", pending.Attention);
        Assert.Null(SupervisionPolicy.Reset(pending).Attention);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void IntentGenerationSuppressesRecoveryWithoutRefundingAttempts(bool enabled, bool running, bool maintenance)
    {
        var state = Qualify(Enabled(), 0);
        state = SupervisionPolicy.SetIntent(state, enabled, running, maintenance ? Guid.NewGuid().ToString("N") : null);
        state = Qualify(state, 120);
        Assert.True(state.Suppressed);
        Assert.Equal(2, state.Generation);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(state, Failed(180), Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void InterruptedAttemptAndItsIncidentSurviveBoundedHistoryAndIntentChanges()
    {
        var state = Qualify(Enabled(), 0);
        state = SupervisionPolicy.Observe(state, Failed(120));
        state = SupervisionPolicy.Reserve(state, Failed(120), Guid.NewGuid().ToString("N"));
        var pending = state.PendingAttempt!;
        var original = Incident(121, "settlement-unknown");
        state = SupervisionPolicy.Settle(state, pending.Id, original.Utc, false, original);
        state = SupervisionPolicy.SetIntent(state, false, false, Guid.NewGuid().ToString("N"));
        for (var i = 0; i < 40; i++)
            state = SupervisionPolicy.Settle(state, pending.Id, Start.AddSeconds(122 + i), false, Incident(122 + i, "settlement-unknown"));
        Assert.Equal(pending, state.PendingAttempt);
        Assert.Equal(original, state.UnresolvedIncident);
        Assert.Equal(32, state.History.Length);
        Assert.Equal(9, state.DiscardedHistory);
        Assert.Equal(1, state.Attempts);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reset(state));
        state = SupervisionPolicy.Settle(state, pending.Id, Start.AddSeconds(200), true, Incident(200));
        Assert.Null(state.PendingAttempt);
        Assert.Null(state.UnresolvedIncident);
        Assert.Null(state.Attention);
        Assert.True(state.Suppressed);
    }

    [Fact]
    public void TickWorksheetContainsExistingStartAndReadinessBoundsWithSettlementMargin()
    {
        var allocated = SupervisionPolicy.ProbeBudget + SupervisionPolicy.CaptureBudget + SupervisionPolicy.StartBudget
            + SupervisionPolicy.ReconciliationBudget + SupervisionPolicy.PersistenceBudget;
        Assert.Equal(TimeSpan.FromSeconds(160), allocated);
        Assert.True(SupervisionPolicy.TickDeadline > allocated);
        Assert.True(SupervisionPolicy.SchedulerActionLimit > SupervisionPolicy.TickDeadline);
        Assert.True(SupervisionPolicy.StartBudget >= TimeSpan.FromSeconds(45 + 30));
    }
}
