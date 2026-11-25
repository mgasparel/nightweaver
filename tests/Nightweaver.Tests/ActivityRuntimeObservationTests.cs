using Nightweaver.ActivityFeed;

namespace Nightweaver.Tests;

public sealed class ActivityRuntimeObservationTests
{
    [Fact]
    public void GeneratedEvidencePassesTheJournalSchemaAndBoundsUnknownNumericFacts()
    {
        var repository = new Nightweaver.Inspection.GitHubRepositoryIdentity("github.com", "owner", "repo");
        long sequence = 0;
        var journal = Guid.NewGuid();
        var runtime = Guid.NewGuid();
        List<ActivityRecord> records = [];
        bool Submit(ActivityEvidence evidence)
        {
            var record = ActivityProjector.Project(new(journal, ++sequence, runtime, sequence), DateTimeOffset.UtcNow, evidence, repository);
            Assert.NotNull(record);
            records.Add(ActivityProjector.Deserialize(ActivityProjector.Serialize(record), repository));
            return true;
        }
        var pool = new ActivityPoolObservation(ActivityPhase.Planning, Submit);
        pool.Observe(ActivityReason.Backpressure, int.MaxValue, 10);
        pool.Unknown();
        pool.Observe(ActivityReason.None, 0, 10);
        new ActivityRuntimeObservation(Submit).Observe(ActivityReason.RuntimeStopped);
        Assert.Null(records[0].Count);
        Assert.Equal(10, records[0].Limit);
        Assert.All(records, r => Assert.Null(r.Bead));
    }

    [Fact]
    public void CountsChangeWithoutRepeatedRowsAndReasonsKeepOneWaitIdentity()
    {
        List<ActivityEvidence> rows = [];
        var clock = new WorkflowTimingTests.Clock();
        var observation = new ActivityPoolObservation(ActivityPhase.Planning, e => { rows.Add(e); return true; }, clock);
        observation.Observe(ActivityReason.Backpressure, 20, 20);
        for (var i = 0; i < 10000; i++) observation.Observe(ActivityReason.Backpressure, 20 + i, 20);
        Assert.Equal(10019, observation.Current.Count);
        var start = Assert.Single(rows);
        Assert.Equal(20, start.Count);
        Assert.Equal(20, start.Limit);
        observation.Observe(ActivityReason.Capacity, 5, 2);
        observation.Observe(ActivityReason.None, 0, 2);
        Assert.Equal(new[] { ActivityKind.WaitStarted, ActivityKind.WaitChanged, ActivityKind.WaitEnded }, rows.Select(r => r.Kind));
        Assert.All(rows, r => { Assert.Equal(start.Cause, r.Cause); Assert.Null(r.Bead); Assert.Null(r.Invocation); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceLossBreaksWaitContinuityAndRecoveryDoesNotCompleteAnUnknownWait(bool stale)
    {
        List<ActivityEvidence> rows = [];
        var clock = new WorkflowTimingTests.Clock();
        var observation = new ActivityPoolObservation(ActivityPhase.Delivery, e => { rows.Add(e); return true; }, clock);
        observation.Observe(ActivityReason.Capacity, 3, 1);
        if (stale)
        {
            clock.Advance(61);
            Assert.Equal(ActivityFreshness.Stale, observation.Current.Freshness);
        }
        else
        {
            observation.Unknown();
            observation.Unknown();
            Assert.Null(observation.Current.Count);
            Assert.Equal(ActivityFreshness.Unavailable, observation.Current.Freshness);
        }
        observation.Observe(ActivityReason.None, 0, 1);
        Assert.Equal(new[] { ActivityKind.WaitStarted, ActivityKind.EvidenceUnavailable, ActivityKind.HealthChanged }, rows.Select(r => r.Kind));
        Assert.Equal(stale ? ActivityReason.StaleSource : ActivityReason.SourceFailed, rows[1].Reason);
        Assert.Equal(ActivityReason.Recovered, rows[2].Reason);
        observation.Observe(ActivityReason.Capacity, 1, 1);
        Assert.NotEqual(rows[0].Cause, rows[^1].Cause);
    }

    [Fact]
    public void LostSubmissionRetriesWithoutClaimingSuccessfulCoverage()
    {
        var accept = false;
        List<ActivityEvidence> rows = [];
        var observation = new ActivityPoolObservation(ActivityPhase.Merger, e => { rows.Add(e); return accept; });
        observation.Observe(ActivityReason.Capacity, 1, 1);
        accept = true;
        observation.Observe(ActivityReason.Capacity, 2, 1);
        observation.Observe(ActivityReason.Capacity, 3, 1);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ActivityKind.WaitStarted, r.Kind));
    }

    [Fact]
    public void RuntimeHealthDoesNotAssertMergeAndSuppressesRepeatedStates()
    {
        List<ActivityEvidence> rows = [];
        var observation = new ActivityRuntimeObservation(e => { rows.Add(e); return true; });
        foreach (var reason in new[] { ActivityReason.RuntimeStarting, ActivityReason.RuntimeRunning,
            ActivityReason.RuntimeFailed, ActivityReason.RuntimeDraining, ActivityReason.RuntimeStopped })
        {
            observation.Observe(reason);
            observation.Observe(reason);
        }
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => { Assert.Equal(ActivityKind.HealthChanged, r.Kind); Assert.Null(r.Merge); Assert.Null(r.Bead); });
    }
}
