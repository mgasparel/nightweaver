using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class PoolObservationTests
{
    [Fact]
    public async Task ConcurrentPhaseChangesAndReleaseNeverExceedOwnedCapacity()
    {
        var clock = new WorkflowTimingTests.Clock();
        var observation = new PoolObservation("delivery", 4, clock, _ => { }, (_, _) => { });
        var timings = Enumerable.Range(0, 4).Select(_ => new WorkflowTiming(clock, (_, _) => { }, _ => { })).ToArray();
        for (var index = 0; index < timings.Length; index++) observation.Reserve(index.ToString(), timings[index]);
        var workers = timings.Select((timing, index) => Task.Run(() =>
        {
            using var context = timing.Enter();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                using var phase = WorkflowTiming.EnterPhase((WorkflowPhase)(iteration % 7));
                Assert.InRange(observation.Read("occupied").Sum(item => item.Value), 0, 4);
            }
            timing.Dispose();
            observation.Release(index.ToString());
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(workers);
        Assert.Equal(0, observation.Read("occupied").Sum(item => item.Value));
    }

    [Fact]
    public async Task ExportIsUnsampledAndContainsOnlyBoundedDimensions()
    {
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 0),
            _ => { }, metricIntervalMilliseconds: 100);
        var stopped = false;
        var observation = telemetry.ObservePool("delivery", 1, () => stopped);
        observation.Observe([]);
        observation.Observe(["PRIVATE_BEAD_ID"]);
        observation.Reserve("PRIVATE_BEAD_ID", null);
        observation.Observe(["PRIVATE_WAITING_ID"]);
        stopped = true;
        Assert.Empty(observation.Read("queued"));
        await receiver.WaitForSignalAsync("metrics");
        await telemetry.StopAsync(false);
        var metrics = receiver.Captures.Where(capture => capture.Signal == "metrics")
            .SelectMany(FirstPathTelemetryTests.DecodeRecords).Select(metric => metric.Only(1).String).ToArray();
        foreach (var name in new[] { "nightweaver.eligible_wait.duration", "nightweaver.eligible_wait.censored",
            "nightweaver.pool.capacity", "nightweaver.pool.occupied", "nightweaver.pool.fresh", "nightweaver.pool.coverage" })
            Assert.Contains(name, metrics);
        foreach (var capture in receiver.Captures)
        {
            var wire = System.Text.Encoding.UTF8.GetString(capture.Bytes);
            Assert.DoesNotContain("PRIVATE_", wire);
            Assert.DoesNotContain("bead.id", wire);
            Assert.DoesNotContain("slot.id", wire);
        }
        Assert.DoesNotContain(receiver.Captures, capture => capture.Signal == "traces");
    }

    [Fact]
    public void HandoffStartsANewPoolIntervalAndReservedElsewhereIsCensored()
    {
        var clock = new WorkflowTimingTests.Clock();
        List<double> first = [], second = [];
        var delivery = new PoolObservation("delivery", 1, clock, first.Add, (_, _) => { });
        var merger = new PoolObservation("merger", 1, clock, second.Add, (_, _) => { });
        delivery.Observe([]);
        merger.Observe([]);
        delivery.Observe(["a"]);
        clock.Advance(5);
        delivery.ExcludeReserved("a");
        delivery.Reserve("a", null);
        delivery.Release("a");
        merger.Observe(["a"]);
        clock.Advance(3);
        merger.Reserve("a", null);
        Assert.Empty(first);
        Assert.Equal(3, Assert.Single(second));
    }

    [Fact]
    public void RepeatedPollsPreserveStartAndOnlyContinuousReservationsComplete()
    {
        var clock = new WorkflowTimingTests.Clock();
        List<double> completed = [];
        var observation = new PoolObservation("delivery", 1, clock, completed.Add, (_, _) => { });
        observation.Observe([]); // establish coverage before newly eligible work
        observation.Observe(["a"]);
        clock.Advance(31); // normal 30-second discovery plus native read latency
        observation.Observe(["a"]);
        clock.Advance(5);
        Assert.Equal(36, Assert.Single(observation.Read("oldest")).Value);
        observation.Reserve("a", null);
        Assert.Equal(36, Assert.Single(completed));
        observation.Observe(["a"]); // local active reservation is excluded
        Assert.Equal(0, Assert.Single(observation.Read("queued")).Value);
        Assert.Equal(1, observation.Read("occupied").Sum(item => item.Value));
        observation.Release("a");
        Assert.Equal(0, observation.Read("occupied").Sum(item => item.Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void UnknownBreaksIntervalsAndNeverExportsHealthyEmptyQueues(int reasonValue)
    {
        var reason = (WaitCensor)reasonValue;
        var clock = new WorkflowTimingTests.Clock();
        List<double> completed = [];
        List<WaitCensor> censored = [];
        var observation = new PoolObservation("delivery", 1, clock, completed.Add, (value, _) => censored.Add(value));
        observation.Observe([]);
        observation.Observe(["a"]);
        clock.Advance(5);
        observation.Unknown(reason);
        Assert.Empty(observation.Read("queued"));
        Assert.Equal(0, Assert.Single(observation.Read("fresh")).Value);
        clock.Advance(20);
        observation.Observe(["a"]);
        observation.Reserve("a", null);
        Assert.Empty(completed);
        Assert.Contains(reason, censored);
    }

    [Fact]
    public void StalenessUsesMonotonicTimeAndNewRuntimeCannotCompleteOldWait()
    {
        var clock = new WorkflowTimingTests.Clock();
        List<double> completed = [];
        var observation = new PoolObservation("merger", 1, clock, completed.Add, (_, _) => { });
        observation.Observe([]);
        observation.Observe(["a"]);
        clock.Advance(61);
        Assert.Empty(observation.Read("oldest"));
        observation.Observe(["a"]);
        observation.Reserve("a", null);
        var restarted = new PoolObservation("merger", 1, clock, completed.Add, (_, _) => { });
        restarted.Observe(["a"]);
        restarted.Reserve("a", null);
        Assert.Empty(completed);
    }

    [Fact]
    public void CapacityLossIsExplicitAndLossOfEligibilityCensorsWithoutCompletion()
    {
        var clock = new WorkflowTimingTests.Clock();
        List<double> completed = [];
        Dictionary<WaitCensor, int> censored = [];
        var observation = new PoolObservation("planning", 2, clock, completed.Add,
            (reason, count) => censored[reason] = censored.GetValueOrDefault(reason) + count);
        observation.Observe(Enumerable.Range(0, 10000).Select(index => index.ToString()));
        Assert.Equal(6667, censored[WaitCensor.Capacity]);
        Assert.Equal(0, Assert.Single(observation.Read("coverage")).Value);
        Assert.Empty(observation.Read("queued"));
        observation.Observe([], admissionBackpressure: true);
        Assert.Equal(3333, censored[WaitCensor.Ineligible]);
        Assert.Equal(1, Assert.Single(observation.Read("backpressure")).Value);
        Assert.Empty(completed);
    }

    [Fact]
    public void OccupancyIncludesPreparationAndFinalizationWithOnePhasePerReservation()
    {
        var clock = new WorkflowTimingTests.Clock();
        using var timing = new WorkflowTiming(clock, (_, _) => { }, _ => { });
        var observation = new PoolObservation("delivery", 1, clock, _ => { }, (_, _) => { });
        observation.Reserve("a", timing);
        using var context = timing.Enter();
        foreach (var phase in Enum.GetValues<WorkflowPhase>())
        {
            using var scope = WorkflowTiming.EnterPhase(phase);
            var values = observation.Read("occupied");
            Assert.Equal(1, values.Sum(item => item.Value));
            Assert.Equal(1, Assert.Single(values, item => item.Tags.ToArray()
                .Any(tag => Equals(tag.Value, phase.ToString().ToLowerInvariant()))).Value);
        }
        timing.Dispose();
        observation.Release("a");
        Assert.Equal(0, observation.Read("occupied").Sum(item => item.Value));
    }
}
