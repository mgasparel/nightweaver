using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class OperationalObservationServiceTests
{
    [Theory]
    [InlineData("explorer:all:YWJj", "abc")]
    [InlineData("explorer:ready:", "")]
    [InlineData("cleanup:1250:all:YSAgYg", "a  b")]
    public void CanonicalSearchPreservesInteriorWhitespace(string key, string expected)
        => Assert.Equal(expected, OperationKey.Parse(key).Query);

    [Theory]
    [InlineData("explorer:all:YQ==")]
    [InlineData("explorer:all:YR")]
    [InlineData("explorer:all:_w")]
    [InlineData("explorer:all:IGE")]
    [InlineData("cleanup:01:all:")]
    [InlineData("cleanup:1251:all:")]
    [InlineData("workspace:../foreign")]
    [InlineData("digest:unknown")]
    public void RejectsNoncanonicalKeys(string key)
        => Assert.Equal(400, Assert.Throws<ObservationRequestException>(() => OperationKey.Parse(key)).StatusCode);

    [Fact]
    public async Task ReadersShareCollectionAndUnchangedObservationsAdvanceOnlyFreshness()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        var admitted = service.Read("explorer:all:");
        Assert.Equal("unavailable", admitted.Health);
        Assert.Null(admitted.Data);
        Assert.NotEqual("0", admitted.Revision);
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var first = service.Read("explorer:all:");
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var unchanged = service.Read("explorer:all:");
        Assert.Equal(first.Revision, unchanged.Revision);
        Assert.True(unchanged.ObservedAt > first.ObservedAt);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => service.Read("explorer:all:"), TestContext.Current.CancellationToken)));
        Assert.Equal(2, source.TrackerReads);
        source.Content = "external inventory edit";
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.True(ulong.Parse(service.Read("explorer:all:").Revision, System.Globalization.CultureInfo.InvariantCulture)
            > ulong.Parse(first.Revision, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PartialFailureRetainsWholeProjectionAndIndependentRuntimeContinues()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        service.Read("explorer:ready:");
        service.Read("factory");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var good = service.Read("explorer:ready:");
        source.Content = "do not blend this new inventory with failed readiness";
        source.Error = new("source-timeout", "Source timed out.");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        await service.CollectOnceAsync(true, TestContext.Current.CancellationToken);
        var stale = service.Read("explorer:ready:");
        Assert.Equal(good.Data?.GetRawText(), stale.Data?.GetRawText());
        Assert.Equal(good.ObservedAt, stale.ObservedAt);
        Assert.Equal("stale", stale.Health);
        Assert.Equal("fresh", service.Read("factory").Health);
        Assert.Equal("source-timeout", stale.Sources["ready"].Error?.Code);
        Assert.Equal("fresh", stale.Sources["inventory"].Health);
        source.Error = null;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal("fresh", service.Read("explorer:ready:").Health);
    }

    [Fact]
    public async Task ConflictsRetainDataButConfirmedDeletionClearsIt()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        service.Read("workspace:task-1");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var good = service.Read("workspace:task-1");
        source.Error = new("source-conflict", "Identity changed.");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(good.Data?.GetRawText(), service.Read("workspace:task-1").Data?.GetRawText());
        source.Error = new("resource-missing", "Deleted.", false);
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var missing = service.Read("workspace:task-1");
        Assert.Null(missing.Data);
        Assert.Null(missing.ObservedAt);
        Assert.Equal("unavailable", missing.Health);
        Assert.False(missing.Error?.Retryable);
    }

    [Fact]
    public async Task SamplingClocksDoNotChangeRevisionButCheckpointAndDomainDatesDo()
    {
        var clock = new Clock();
        var source = new Source(clock) { IncludeSamplingDates = true };
        using var service = Service(source, clock);
        service.Read("digest:auto");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var first = service.Read("digest:auto");
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(first.Revision, service.Read("digest:auto").Revision);
        source.Checkpoint = clock.GetUtcNow();
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var checkpoint = service.Read("digest:auto");
        Assert.NotEqual(first.Revision, checkpoint.Revision);
        source.Activity = clock.GetUtcNow();
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.NotEqual(checkpoint.Revision, service.Read("digest:auto").Revision);
    }

    [Fact]
    public async Task OverdueObservationBecomesStaleWhileReadRemainsInFlight()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        service.Read("attention");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var good = service.Read("attention");
        source.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));
        var stale = service.Read("attention");
        Assert.Equal("budget-exceeded", stale.Error?.Code);
        Assert.Equal(good.Data?.GetRawText(), stale.Data?.GetRawText());
        source.Hold.SetResult();
        await pending;
    }

    [Fact]
    public async Task TtlEvictionAllocatesNewRevisionsAndDiscardsOldInFlightResults()
    {
        var clock = new Clock();
        var source = new Source(clock) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = Service(source, clock);
        var first = service.Read("attention");
        var pending = service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(61));
        var readmitted = service.Read("attention");
        Assert.True(ulong.Parse(readmitted.Revision, System.Globalization.CultureInfo.InvariantCulture)
            > ulong.Parse(first.Revision, System.Globalization.CultureInfo.InvariantCulture));
        source.Hold.SetResult();
        await pending;
        var discarded = service.Read("attention");
        Assert.Null(discarded.Data);
        Assert.Equal(readmitted.Revision, discarded.Revision);
        source.Hold = null;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.NotEqual(readmitted.Revision, service.Read("attention").Revision);
    }

    [Fact]
    public async Task LruReadmissionRejectsACollectorCapturedBeforeEviction()
    {
        var clock = new Clock();
        var source = new Source(clock) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = Service(source, clock);
        var keys = Enumerable.Range(0, 64).Select(index => $"explorer:all:{Encode(index.ToString(System.Globalization.CultureInfo.InvariantCulture))}").ToArray();
        foreach (var key in keys) service.Read(key);
        var pending = service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(31)); // Grace ended, but idle TTL has not expired.
        var protectedEntry = service.Read(keys[0]);
        service.Read("attention"); // Evicts the least recently used eligible entry, keys[1].
        var readmitted = service.Read(keys[1]);
        Assert.Null(readmitted.Data);
        source.Hold.SetResult();
        await pending;
        Assert.NotNull(service.Read(keys[0]).Data);
        Assert.NotEqual(protectedEntry.Revision, service.Read(keys[0]).Revision);
        Assert.Null(service.Read(keys[1]).Data);
        Assert.Equal(readmitted.Revision, service.Read(keys[1]).Revision);
        source.Hold = null;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.NotEqual(readmitted.Revision, service.Read(keys[1]).Revision);
    }

    [Fact]
    public void AdmissionIsAtomicAndPinsProtectCapacityAndWorkspaceGrace()
    {
        var clock = new Clock();
        using var service = Service(new Source(clock), clock);
        using var pins = service.Pin(["workspace:task-1", "workspace:task-2"]);
        Assert.Equal(429, Assert.Throws<ObservationRequestException>(() => service.Read("workspace:task-3")).StatusCode);
        var first = service.Read("workspace:task-1");
        pins.Dispose();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(429, Assert.Throws<ObservationRequestException>(() => service.Read("workspace:task-3")).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.Read("workspace:task-3");
        Assert.Equal("budget-exceeded", service.Read("workspace:task-1").Error?.Code);
        using var full = Service(new Source(clock), clock);
        for (var index = 0; index < 64; index++) full.Read($"explorer:all:{Encode(index.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        Assert.Equal(429, Assert.Throws<ObservationRequestException>(() => full.RequestRefresh(["attention", "digest:auto"])).StatusCode);
        Assert.Equal(429, Assert.Throws<ObservationRequestException>(() => full.Read("attention")).StatusCode);
    }

    [Fact]
    public async Task ShutdownCancelsAndSettlesSharedCollectionAndStopsAdmission()
    {
        var clock = new Clock();
        var source = new Source(clock) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = Service(source, clock);
        service.Read("attention");
        await service.StartAsync(TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // A canceled interest holder does not own the source cancellation token.
        using (service.Pin(["attention"])) { }
        Assert.False(source.WasCanceled);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(source.WasCanceled);
        Assert.Equal(503, Assert.Throws<ObservationRequestException>(() => service.Read("attention")).StatusCode);
        Assert.Equal(1, source.TrackerReads);
    }

    [Fact]
    public async Task GatePrioritizesMutationsAndAdmitsObserverAfterEight()
    {
        using var gate = new TrackerAccessGate();
        var token = TestContext.Current.CancellationToken;
        await gate.WaitObserverAsync(token);
        var observer = gate.WaitObserverAsync(token);
        var mutations = Enumerable.Range(0, 10).Select(_ => gate.WaitAsync(token)).ToArray();
        gate.Release();
        for (var index = 0; index < 8; index++)
        {
            await mutations[index];
            Assert.False(observer.IsCompleted);
            gate.Release();
        }
        await observer;
        Assert.False(mutations[8].IsCompleted);
        gate.Release();
        await mutations[8]; gate.Release();
        await mutations[9]; gate.Release();
    }

    [Fact]
    public async Task ProjectionAndHistoryReservationsShareTheSameFiniteByteBudget()
    {
        var clock = new Clock();
        var source = new Source(clock) { Content = new string('x', 64 * 1024) };
        var memory = new ObservationMemoryBudget();
        var history = new object();
        Assert.True(memory.TryReplace([], [(history, ObservationMemoryBudget.MaximumBytes - 32 * 1024)]));
        using var service = new OperationalObservationService("test", source, clock, new Lifetime(), memory);
        service.Read("attention");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var limited = service.Read("attention");
        Assert.Null(limited.Data);
        Assert.Equal("source-limit", limited.Error?.Code);
        Assert.InRange(memory.Used, 1, ObservationMemoryBudget.MaximumBytes);
        memory.Release(history);
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal("fresh", service.Read("attention").Health);
        service.Dispose();
        Assert.Equal(0, memory.Used);
    }

    [Fact]
    public async Task SubscriptionCapturesVersionsAfterRegistrationAndDisposalStopsNotifications()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        var notifications = new List<string>();
        using var subscription = service.Subscribe(["attention"], (key, version) => notifications.Add(key + ":" + version));
        Assert.NotEqual("0", subscription.Versions["attention"]);
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Single(notifications);
        Assert.NotEqual(subscription.Versions["attention"], service.Read("attention").Revision);
        subscription.Dispose();
        source.Content = "new value";
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task CounterExhaustionStopsPublicationAndRequestsHostReplacement()
    {
        var clock = new Clock();
        var lifetime = new Lifetime();
        using var service = new OperationalObservationService("test", new Source(clock), clock, lifetime);
        service.Read("attention");
        var epoch = service.Epoch;
        typeof(OperationalObservationService).GetField("revision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, ulong.MaxValue);
        var exhausted = await Assert.ThrowsAsync<ObservationRequestException>(() => service.CollectOnceAsync(false, TestContext.Current.CancellationToken));
        Assert.Equal(503, exhausted.StatusCode);
        Assert.True(lifetime.StopRequested);
        Assert.Equal(503, Assert.Throws<ObservationRequestException>(() => service.Read("factory")).StatusCode);
        Assert.Equal(epoch, service.Epoch);
    }

    [Fact]
    public async Task BlockedCleanupDoesNotDelayRuntimeCollection()
    {
        var clock = new Clock();
        var source = new Source(clock) { CleanupHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = Service(source, clock);
        service.Read("factory"); service.Read("cleanup:1:all:");
        var cleanup = service.CollectOnceAsync(true, TestContext.Current.CancellationToken, cleanup: true);
        await service.CollectOnceAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal("fresh", service.Read("factory").Health);
        Assert.Null(service.Read("cleanup:1:all:").Data);
        source.CleanupHold.SetResult();
        await cleanup;
        Assert.Equal("fresh", service.Read("cleanup:1:all:").Health);
    }

    private static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static OperationalObservationService Service(Source source, Clock clock) => new("test", source, clock, new Lifetime());
    private sealed class Lifetime : IHostApplicationLifetime
    {
        internal bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan delta) => now += delta;
    }
    private sealed class Source(Clock clock) : IOperationalObservationSource
    {
        internal int TrackerReads { get; private set; }
        internal string Content { get; set; } = "same";
        internal ObservationError? Error { get; set; }
        internal bool IncludeSamplingDates { get; set; }
        internal DateTimeOffset? Checkpoint { get; set; }
        internal DateTimeOffset? Activity { get; set; }
        internal TaskCompletionSource? CleanupHold { get; init; }
        internal TaskCompletionSource? Hold { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasCanceled { get; private set; }
        public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
        {
            TrackerReads++;
            Entered.TrySetResult();
            if (Hold is not null)
            {
                try { await Hold.Task.WaitAsync(token); }
                catch (OperationCanceledException) { WasCanceled = true; throw; }
            }
            return Samples(keys, false);
        }
        public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadRuntimeAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
        {
            if (CleanupHold is not null && keys.Any(key => key.Kind == "cleanup")) await CleanupHold.Task.WaitAsync(token);
            return Samples(keys, true);
        }
        private Dictionary<string, ObservationSample> Samples(IReadOnlyList<OperationKey> keys, bool runtime)
        {
            var now = clock.GetUtcNow();
            return keys.ToDictionary(key => key.Value, _ => new ObservationSample(
                IncludeSamplingDates ? new { content = Content, generatedAt = now, lastCaughtUpAt = Checkpoint, activityAt = Activity } : new { content = Content },
                now, now, new Dictionary<string, ObservationSourceState>
                {
                    [runtime ? "runtime" : "inventory"] = new("fresh", now, now, null),
                    [runtime ? "cleanup" : "ready"] = new(Error is null || runtime ? "fresh" : "stale", now, now, runtime ? null : Error)
                }, runtime ? null : Error), StringComparer.Ordinal);
        }
    }
}
