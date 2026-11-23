using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Nightweaver.Web;
using Nightweaver.Telemetry;
using static Nightweaver.Tests.ObservationTelemetryAssertions;

namespace Nightweaver.Tests;

public sealed class OperationalObservationServiceTests
{
    [Fact]
    public void BaseAndLegacyWorkspaceKeysShareTheTwoIdentityLimit()
    {
        var clock = new Clock();
        using var service = Service(new Source(clock), clock);
        using var pin = service.Pin(["base:workspace:task-1", "workspace:task-1", "base:workspace:task-2"]);
        Assert.Equal(429, Assert.Throws<ObservationRequestException>(() => service.Read("base:workspace:task-3")).StatusCode);
        Assert.Null(service.Read("workspace:task-2").Data);
    }

    [Fact]
    public async Task LateBaseCallbackCannotPopulateAnEvictedAndReadmittedEntry()
    {
        var clock = new Clock();
        var source = new Source(clock) { ProgressiveHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = Service(source, clock);
        var original = service.Read("base:workspace:task-1");
        var collecting = service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        await source.Entered.Task;
        clock.Advance(TimeSpan.FromSeconds(61));
        var replacement = service.Read("base:workspace:task-1");
        Assert.NotEqual(original.Revision, replacement.Revision);
        source.ProgressiveHold.SetResult();
        await collecting;
        Assert.Null(service.Read("base:workspace:task-1").Data);
        Assert.Equal(replacement.Revision, service.Read("base:workspace:task-1").Revision);
        source.ProgressiveHold = null;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.NotNull(service.Read("base:workspace:task-1").Data);
    }

    [Fact]
    public async Task SharedCollectorExportsSafeHealthAgeAndOneAttemptAcrossManySubscribers()
    {
        const string canary = "secret-workspace-comment-transcript";
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Web, new(receiver.Endpoint, "", 1),
            _ => { }, metricIntervalMilliseconds: 50);
        var clock = new Clock();
        var source = new Source(clock) { Content = canary };
        using var service = Service(source, clock);
        service.Telemetry = telemetry;
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        await WaitForMetricAsync(receiver, "nightweaver.observation.health", "tracker", "idle", value => value == 1);
        Assert.DoesNotContain(receiver.Captures.SelectMany(Metrics), metric => metric.Only(1).String == "nightweaver.observation.attempts");
        var subscribers = Enumerable.Range(0, 100).Select(_ => service.Subscribe(["workspace:" + canary], (_, _) => { })).ToArray();
        try
        {
            await service.CollectOnceAsync(false, token);
            // The fake's empty-key bookkeeping read above does no tracker work.
            Assert.Equal(2, source.TrackerReads);
            await WaitForMetricAsync(receiver, "nightweaver.observation.attempts", "tracker", null, value => value == 1);
            await WaitForMetricAsync(receiver, "nightweaver.observation.health", "tracker", "healthy", value => value == 1);
            clock.Advance(TimeSpan.FromSeconds(8));
            source.Error = new("source-timeout", canary);
            await service.CollectOnceAsync(false, token);
            Assert.NotNull(service.Read("workspace:" + canary).Data);
            await WaitForMetricAsync(receiver, "nightweaver.observation.health", "tracker", "stale", value => value == 1);
            await WaitForMetricAsync(receiver, "nightweaver.observation.age", "tracker", "stale", value => value == 8);
            await receiver.WaitForSignalsAsync();
            AssertSanitized(receiver, canary);
        }
        finally { foreach (var subscriber in subscribers) subscriber.Dispose(); }
        clock.Advance(TimeSpan.FromSeconds(31));
        await service.CollectOnceAsync(false, token);
        await WaitForMetricAsync(receiver, "nightweaver.observation.health", "tracker", "healthy", value => value == 0);
        await service.StopAsync(token);
        await WaitForMetricAsync(receiver, "nightweaver.observation.health", "tracker", "stopped", value => value == 1);
    }

    [Fact]
    public async Task InitialTimeoutIsUnavailableAndStoppedRuntimeRemainsDistinct()
    {
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Web, new(receiver.Endpoint, "", 1),
            _ => { }, metricIntervalMilliseconds: 50);
        var clock = new Clock();
        var source = new Source(clock) { Error = new("source-timeout", "private-error"), RuntimeStopped = true };
        using var service = Service(source, clock);
        service.Telemetry = telemetry;
        service.Read("attention"); service.Read("factory");
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        await service.CollectOnceAsync(true, token);
        await WaitForMetricAsync(receiver, "nightweaver.observation.health", "tracker", "unavailable", value => value == 1);
        await WaitForMetricAsync(receiver, "nightweaver.observation.health", "runtime", "stopped", value => value == 1);
        var attempt = await receiver.WaitForSignalAsync("logs");
        Assert.Contains("timed_out", System.Text.Encoding.UTF8.GetString(attempt.Bytes));
        AssertSanitized(receiver, "private-error");
    }

    [Fact]
    public async Task IdentityReplacementRebaselinesAndNoProgressPageDegradesWithoutLosingCursor()
    {
        var clock = new Clock();
        var journal = new Journal { Page = new("first", [new(42, "update", "task-1", null)]) };
        using var service = new OperationalObservationService("test", new Source(clock), clock, new Lifetime(), journal: journal);
        using var pin = service.Pin(["attention"]);
        var token = TestContext.Current.CancellationToken;
        await service.PollJournalOnceAsync(token);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Page = new("replacement", [new(1, "update", "task-1", null)]);
        await service.PollJournalOnceAsync(token);
        Assert.Equal("catching-up", service.Read("attention").Synchronization!.Journal);
        Assert.NotNull(service.Read("attention").PendingAgeMs);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Page = new("replacement", Enumerable.Repeat(new Nightweaver.Inspection.BeadsInvalidation(1, "update", "task-1", null), 256).ToArray());
        await service.PollJournalOnceAsync(token);
        Assert.Equal("degraded", service.Read("attention").Synchronization!.Journal);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Page = new("replacement", []);
        await service.PollJournalOnceAsync(token);
        Assert.Equal([0L, 42L, 1L, 1L], journal.Cursors);
    }

    [Fact]
    public async Task OneJournalReaderSharesInterestUsesFiveSecondCadenceAndStopsAfterGrace()
    {
        var clock = new Clock();
        var source = new Source(clock);
        var journal = new Journal();
        using var service = new OperationalObservationService("test", source, clock, new Lifetime(), journal: journal);
        var pins = Enumerable.Range(0, 100).Select(_ => service.Pin(["attention"])).ToArray();
        var token = TestContext.Current.CancellationToken;
        await service.PollJournalOnceAsync(token);
        Assert.Single(journal.Cursors);
        clock.Advance(TimeSpan.FromSeconds(4));
        await service.PollJournalOnceAsync(token);
        Assert.Single(journal.Cursors);
        clock.Advance(TimeSpan.FromSeconds(1));
        journal.Page = new("same", [new(1, "comment", "task-1", null)]);
        await service.PollJournalOnceAsync(token);
        Assert.Equal(2, journal.Cursors.Count);
        await service.CollectOnceAsync(false, token);
        var current = service.Read("attention");
        Assert.Null(current.PendingAgeMs);
        journal.Page = new("same", [new(2, "update", "task-1", null)]);
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.PollJournalOnceAsync(token);
        clock.Advance(TimeSpan.FromMilliseconds(5001));
        var delayed = service.Read("attention");
        Assert.True(delayed.Synchronization!.Delayed);
        Assert.NotEqual(current.Revision, delayed.Revision);
        Assert.Equal("fresh", delayed.Health);
        foreach (var pin in pins) pin.Dispose();
        clock.Advance(TimeSpan.FromSeconds(31));
        var before = journal.Cursors.Count;
        await service.PollJournalOnceAsync(token);
        Assert.Equal(before, journal.Cursors.Count);
        Assert.Equal("inactive", service.Read("attention").Synchronization!.Journal);
    }

    [Fact]
    public async Task InvalidJournalPageCannotCommitCursorAndSettlementFailureStopsReplacement()
    {
        var clock = new Clock();
        var journal = new Journal { Page = new("same", [new(1, "update", "task-1", null)]) };
        using var service = new OperationalObservationService("test", new Source(clock), clock, new Lifetime(), journal: journal);
        using var pin = service.Pin(["attention"]);
        var token = TestContext.Current.CancellationToken;
        await service.PollJournalOnceAsync(token);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Page = new("same", [new(3, "update", "task-1", null), new(2, "update", "task-1", null)]);
        await service.PollJournalOnceAsync(token);
        Assert.Equal("degraded", service.Read("attention").Synchronization!.Journal);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Page = new("same", []);
        await service.PollJournalOnceAsync(token);
        Assert.Equal([0L, 1L, 1L], journal.Cursors);
        clock.Advance(TimeSpan.FromSeconds(5));
        journal.Error = new Nightweaver.Application.BeadsJournalSettlementException("uncertain child", new IOException());
        await service.PollJournalOnceAsync(token);
        var before = journal.Cursors.Count;
        clock.Advance(TimeSpan.FromSeconds(120));
        await service.PollJournalOnceAsync(token);
        Assert.Equal(before, journal.Cursors.Count);
        var stopped = service.Read("attention").Synchronization!;
        Assert.Equal("degraded", stopped.Journal);
        Assert.Equal("journal-settlement", stopped.Error!.Code);
        Assert.False(stopped.Error.Retryable);
    }

    [Fact]
    public async Task ReadCompletionCannotClearNewerDirtyGenerationAndReceiptsDoNotRefreshAge()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        using var pin = service.Pin(["attention"]);
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        source.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RequestReconciliation();
        var read = service.CollectOnceAsync(false, token);
        clock.Advance(TimeSpan.FromSeconds(6));
        service.RequestReconciliation();
        source.Hold.SetResult(); await read;
        Assert.True(service.Read("attention").Synchronization!.Delayed);
        Assert.NotNull(service.Read("attention").PendingAgeMs);
        var accepted = service.Read("attention").ValidatedAgeMs;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(service.Read("attention").ValidatedAgeMs > accepted + 9999);
        source.Hold = null;
        await service.CollectOnceAsync(false, token);
        Assert.Null(service.Read("attention").PendingAgeMs);
    }

    private sealed class Journal : IConsoleJournalReader
    {
        internal List<long> Cursors { get; } = [];
        internal ConsoleJournalPage Page { get; set; } = new("same", []);
        internal Exception? Error { get; set; }
        public Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token)
        {
            Cursors.Add(cursor);
            return Error is null ? Task.FromResult(Page) : Task.FromException<ConsoleJournalPage>(Error);
        }
    }

    [Theory]
    [InlineData("explorer:all:YWJj", "abc")]
    [InlineData("explorer:ready:", "")]
    [InlineData("explorer:ready::250", "")]
    [InlineData("cleanup:1250:all:YSAgYg", "a  b")]
    public void CanonicalSearchPreservesInteriorWhitespace(string key, string expected)
        => Assert.Equal(expected, OperationKey.Parse(key).Query);

    [Theory]
    [InlineData("explorer:all:YQ==")]
    [InlineData("explorer:all:YR")]
    [InlineData("explorer:all:_w")]
    [InlineData("explorer:all:IGE")]
    [InlineData("explorer:all::0")]
    [InlineData("explorer:all::01")]
    [InlineData("explorer:all::251")]
    [InlineData("explorer:unknown::1")]
    [InlineData("explorer:all:YR:1")]
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
        service.Read("explorer:ready::2");
        service.Read("factory");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var good = service.Read("explorer:ready:");
        var paged = service.Read("explorer:ready::2");
        source.Content = "do not blend this new inventory with failed readiness";
        source.Error = new("source-timeout", "Source timed out.");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        await service.CollectOnceAsync(true, TestContext.Current.CancellationToken);
        var stale = service.Read("explorer:ready:");
        Assert.Equal(good.Data?.GetRawText(), stale.Data?.GetRawText());
        Assert.Equal(paged.Data?.GetRawText(), service.Read("explorer:ready::2").Data?.GetRawText());
        Assert.Equal("stale", service.Read("explorer:ready::2").Health);
        Assert.Equal(good.ObservedAt, stale.ObservedAt);
        Assert.Equal("stale", stale.Health);
        Assert.Equal("fresh", service.Read("factory").Health);
        Assert.Null(service.Read("factory").Policy);
        Assert.Equal(0d, service.Read("factory").ValidatedAgeMs);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(5000d, service.Read("factory").ValidatedAgeMs);
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
        using var agePin = service.Pin(["attention"]);
        clock.Advance(TimeSpan.FromSeconds(121));
        var stale = service.Read("attention");
        Assert.Equal("budget-exceeded", stale.Error?.Code);
        Assert.Equal(good.Data?.GetRawText(), stale.Data?.GetRawText());
        source.Hold.SetResult();
        await pending;
    }

    [Fact]
    public async Task SourceRevalidationRequiresMatchingContentAndCannotHideOtherStaleSourcesOrProjectionErrors()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        service.Read("attention");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var good = service.Read("attention");
        using var agePin = service.Pin(["attention"]);
        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal("stale", service.Read("attention").Health);
        var fresh = new ObservationSourceState("fresh", clock.GetUtcNow(), clock.GetUtcNow(), null);
        source.Revalidate!("inventory", fresh with { ContentIdentity = new object() });
        Assert.Equal(good.Sources["inventory"].ObservedAt, service.Read("attention").Sources["inventory"].ObservedAt);
        source.Revalidate("inventory", fresh with { ContentIdentity = source.InventoryIdentity });
        Assert.Equal("stale", service.Read("attention").Health); // Readiness is still overdue.
        source.Revalidate("ready", fresh with { ContentIdentity = source.ReadyIdentity });
        Assert.Equal("fresh", service.Read("attention").Health);
        Assert.Equal(good.Data, service.Read("attention").Data);
        Assert.Equal(1, source.TrackerReads);
        Assert.DoesNotContain("contentIdentity", JsonSerializer.Serialize(service.Read("attention"), ObservationJson.Options));
        source.Error = new("source-conflict", "Projection identity changed.");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        source.Revalidate!("ready", fresh with { ContentIdentity = source.ReadyIdentity });
        Assert.Equal("source-conflict", service.Read("attention").Error?.Code);
        Assert.Equal("stale", service.Read("attention").Health);
    }

    [Fact]
    public async Task PendingFirstLoadFailurePublishesDiagnosticsWithoutUnacceptedProvenance()
    {
        var clock = new Clock();
        var source = new Source(clock) { Pending = true, Error = new("source-failed", "Relation read failed.") };
        using var service = Service(source, clock);
        service.Read("workspace:task-1");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var failed = service.Read("workspace:task-1");
        Assert.Equal("unavailable", failed.Health);
        Assert.Equal("source-failed", failed.Error?.Code);
        Assert.Null(failed.Data);
        Assert.Null(failed.ObservedAt);
        Assert.Null(failed.ValidatedAgeMs);
        var state = failed.Sources["ready"];
        Assert.Equal("unavailable", state.Health);
        Assert.Equal(source.Error, state.Error);
        Assert.Equal(clock.GetUtcNow(), state.LastAttemptAt);
        Assert.Null(state.ObservedAt);
        Assert.Null(state.ContentIdentity);
        Assert.Null(state.ValidatedTimestamp);
        Assert.Null(state.SourceGeneration);
        Assert.Null(failed.Sources["inventory"].ObservedAt);
        Assert.Null(failed.Sources["inventory"].ContentIdentity);
        source.Pending = false;
        source.Error = null;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal("fresh", service.Read("workspace:task-1").Health);
        Assert.Null(service.Read("workspace:task-1").Error);
        Assert.NotNull(service.Read("workspace:task-1").Data);
    }

    [Fact]
    public async Task PendingChangedSourceMetadataCannotRevalidateRetainedData()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        service.Read("attention");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var original = service.Read("attention").Data;
        source.Content = "changed but not yet published";
        source.InventoryIdentity = new(); source.ReadyIdentity = new(); source.Pending = true;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        using var agePin = service.Pin(["attention"]);
        var changedSource = new ObservationSourceState("fresh", clock.GetUtcNow(), clock.GetUtcNow(), null)
            { ContentIdentity = source.InventoryIdentity };
        source.Revalidate!("inventory", changedSource);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(6000d, service.Read("attention").PendingAgeMs);
        Assert.True(service.Read("attention").Synchronization!.Delayed);
        source.Revalidate("inventory", changedSource);
        Assert.Equal(6000d, service.Read("attention").PendingAgeMs);
        clock.Advance(TimeSpan.FromSeconds(115));
        Assert.Equal("stale", service.Read("attention").Health);
        var fresh = new ObservationSourceState("fresh", clock.GetUtcNow(), clock.GetUtcNow(), null);
        source.Revalidate!("inventory", fresh with { ContentIdentity = source.InventoryIdentity });
        source.Revalidate("ready", fresh with { ContentIdentity = source.ReadyIdentity });
        Assert.Equal(original, service.Read("attention").Data);
        Assert.Equal("stale", service.Read("attention").Health);
        source.Pending = false;
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Null(service.Read("attention").PendingAgeMs);
        Assert.Equal("fresh", service.Read("attention").Health);
    }

    [Fact]
    public async Task RevalidationFromAnEvictedCollectionCannotRefreshItsReplacement()
    {
        var clock = new Clock();
        var source = new Source(clock);
        using var service = Service(source, clock);
        service.Read("attention");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var obsolete = source.Revalidate!;
        clock.Advance(TimeSpan.FromSeconds(61));
        service.Read("attention");
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        var current = service.Read("attention");
        clock.Advance(TimeSpan.FromSeconds(1));
        obsolete("inventory", new("fresh", clock.GetUtcNow(), clock.GetUtcNow(), null) { ContentIdentity = source.InventoryIdentity });
        Assert.Equal(current.Revision, service.Read("attention").Revision);
        Assert.Equal(current.ObservedAt, service.Read("attention").ObservedAt);
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
        Assert.Null(service.Read("workspace:task-1").Error);
        Assert.Null(service.Read("workspace:task-1").Data); // Initial loading has no accepted age.
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
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.UtcTicks;
        internal void Advance(TimeSpan delta) => now += delta;
    }
    private sealed class Source(Clock clock) : IOperationalObservationSource, IProgressiveTrackerObservationSource
    {
        internal TaskCompletionSource? ProgressiveHold { get; set; }
        public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadProgressiveAsync(IReadOnlyList<OperationKey> keys,
            Action<string, ObservationSourceState> revalidated,
            Action<IReadOnlyDictionary<string, ObservationSample>> publish, CancellationToken token)
        {
            var samples = await ReadTrackerAsync(keys, token, revalidated);
            if (ProgressiveHold is not null)
            {
                await ProgressiveHold.Task.WaitAsync(token);
                publish(samples);
            }
            return samples;
        }
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
        internal object InventoryIdentity { get; set; } = new();
        internal object ReadyIdentity { get; set; } = new();
        internal bool Pending { get; set; }
        internal bool RuntimeStopped { get; set; }
        internal Action<string, ObservationSourceState>? Revalidate { get; private set; }
        public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerAsync(IReadOnlyList<OperationKey> keys, CancellationToken token,
            Action<string, ObservationSourceState>? revalidated = null)
        {
            Revalidate = revalidated;
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
                runtime && RuntimeStopped ? new { availability = "stopped" } :
                    IncludeSamplingDates ? new { content = Content, generatedAt = now, lastCaughtUpAt = Checkpoint, activityAt = Activity } : new { content = Content },
                now, now, new Dictionary<string, ObservationSourceState>
                {
                    [runtime ? "runtime" : "inventory"] = new("fresh", now, now, null) { ContentIdentity = InventoryIdentity },
                    [runtime ? "cleanup" : "ready"] = new(Error is null || runtime ? "fresh" : "stale", now, now, runtime ? null : Error)
                        { ContentIdentity = ReadyIdentity }
                }, runtime ? null : Error, Pending: Pending), StringComparer.Ordinal);
        }
    }
}
