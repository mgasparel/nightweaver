using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Inspection;
using Nightweaver.Web;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class OperationalObservationSourceTests
{
    [Fact]
    public async Task IndependentSectionsShareHistoryWithAttentionAndDigest()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("base:workspace:task-1", "section:task-1:history", "section:task-1:text", "attention");
        var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Single(runner.CommentIds);
        Assert.NotNull(samples["section:task-1:history"].Data);
        Assert.NotNull(samples["section:task-1:text"].Data);
        Assert.NotNull(samples["attention"].Data);
        var section = JsonSerializer.SerializeToElement(samples["section:task-1:history"].Data, ObservationJson.Options);
        Assert.Equal("available", section.GetProperty("state").GetString());
        Assert.Equal("Initial comment", section.GetProperty("value").GetProperty("comments")[0].GetProperty("body").GetString());
        Assert.Equal("1", section.GetProperty("baseIdentity").GetProperty("inventoryRevision").GetString());
        var commands = runner.Commands.Count;
        await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(commands, runner.Commands.Count);

        runner.Closed = true; runner.HistoryOverrides["task-1"] = DigestMergeEvidenceTests.Comments("task-1");
        source.Invalidate();
        var closed = await source.ReadScheduledAsync(Keys("section:task-1:merge", "digest:all"), (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(2, runner.CommentIds.Count);
        Assert.Equal("merged", Assert.Single(Assert.IsType<CatchUpDigestResponse>(closed["digest:all"].Data).StandaloneCompleted).Outcome);
        Assert.Contains("verified", JsonSerializer.Serialize(closed["section:task-1:merge"].Data));
    }

    [Fact]
    public async Task SelectedSectionPublishesBeforeUnrelatedHistoryCohortCompletes()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 9, FailCommentOnce = "task-2", CommentTimeoutsRemaining = 10,
            Advance = () => clock.Advance(TimeSpan.FromMilliseconds(500)) };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var samples = await source.ReadScheduledAsync(Keys("section:task-1:history", "attention"), (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.NotNull(samples["section:task-1:history"].Data);
        Assert.False(samples["section:task-1:history"].Pending);
        Assert.Null(samples.GetValueOrDefault("attention")?.Data);
        Assert.DoesNotContain("task-9", runner.CommentIds);
    }

    [Fact]
    public async Task ConcurrentSectionReadersShareWorkAndCallerCancellationDoesNotCancelCollection()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { TargetPath = target.Path };
        var held = new HoldingRunner(runner, "comments");
        using var factory = Factory(target.Path, held, clock);
        using var service = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        using var first = service.Subscribe(["section:task-1:history"], (_, _) => { });
        using var second = service.Subscribe(["section:task-1:history"], (_, _) => { });
        var token = TestContext.Current.CancellationToken;
        var collection = service.CollectOnceAsync(false, token);
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            first.Dispose();
            using var caller = new CancellationTokenSource(); caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CollectOnceAsync(false, caller.Token));
            Assert.Null(service.Read("section:task-1:history").Data);
        }
        finally { held.Release.TrySetResult(); await collection; }
        Assert.Single(runner.CommentIds);
        Assert.Equal("fresh", service.Read("section:task-1:history").Health);
    }

    [Fact]
    public async Task FailedSectionRefreshRetainsValueIdentityAndSourceClocks()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        const string key = "section:task-1:history";
        service.Read(key);
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        var accepted = service.Read(key);
        clock.Advance(TimeSpan.FromSeconds(10));
        runner.FailCommentOnce = "task-1"; source.Invalidate([new(1, "comment", "task-1", null)]);
        await service.CollectOnceAsync(false, token);
        var failed = service.Read(key);
        Assert.Equal("stale", failed.Health);
        Assert.Equal("stale", failed.Data!.Value.GetProperty("state").GetString());
        Assert.Equal(accepted.Data!.Value.GetProperty("value").GetRawText(), failed.Data.Value.GetProperty("value").GetRawText());
        Assert.Equal(accepted.Data.Value.GetProperty("baseIdentity").GetRawText(), failed.Data.Value.GetProperty("baseIdentity").GetRawText());
        Assert.Equal(accepted.Sources["comments:task-1"].ObservedAt, failed.Sources["comments:task-1"].ObservedAt);
        Assert.Equal(accepted.Sources["comments:task-1"].ValidatedTimestamp, failed.Sources["comments:task-1"].ValidatedTimestamp);
        Assert.Equal("source-timeout", failed.Error?.Code);
    }

    [Fact]
    public async Task SectionConfirmationRejectsAChangedIdentityAndNeverRestoresDeletedMembership()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { TargetPath = target.Path };
        var held = new HoldingRunner(runner, "comments");
        using var factory = Factory(target.Path, held, clock);
        var source = Adapter(factory);
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        using var pin = service.Pin(["base:workspace:task-1", "section:task-1:history"]);
        var token = TestContext.Current.CancellationToken;
        var collection = service.CollectOnceAsync(false, token);
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            runner.UpdatedAt = "2026-01-02T00:00:00Z";
        }
        finally { held.Release.TrySetResult(); await collection; }
        Assert.Equal("source-conflict", service.Read("section:task-1:history").Error?.Code);
        Assert.Equal("failed", service.Read("section:task-1:history").Data!.Value.GetProperty("state").GetString());
        await service.CollectOnceAsync(false, token);
        Assert.Equal("available", service.Read("section:task-1:history").Data!.Value.GetProperty("state").GetString());
        runner.Rows = 0; source.Invalidate();
        await service.CollectOnceAsync(false, token);
        Assert.Equal("resource-missing", service.Read("base:workspace:task-1").Error?.Code);
        Assert.Null(service.Read("section:task-1:history").Data);
    }

    [Fact]
    public async Task SectionSourcesAndReservationsRetireAfterInterestEnds()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 2 };
        using var factory = Factory(target.Path, runner, clock);
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        var token = TestContext.Current.CancellationToken;
        var selected = Keys("section:task-1:text", "section:task-1:history");
        await source.ReadScheduledAsync(selected, (_, _) => { }, token);
        Assert.True(memory.Used > 0);
        await source.ReadScheduledAsync(Keys("base:workspace:task-2"), (_, _) => { }, token);
        var commands = runner.Commands.Count;
        clock.Advance(TimeSpan.FromSeconds(61));
        await source.ReadScheduledAsync(Keys("base:workspace:task-2"), (_, _) => { }, token);
        Assert.DoesNotContain("comments", runner.Commands.Skip(commands));
        Assert.DoesNotContain("show", runner.Commands.Skip(commands));
        await source.ReadScheduledAsync([], (_, _) => { }, token);
        Assert.Equal(0, memory.Used);
    }

    [Fact]
    public async Task RepeatedSectionSelectionAllowsBaseBackgroundAndGuardedOperationsToProgress()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path, Rows = 9, Advance = () => clock.Advance(TimeSpan.FromMilliseconds(700)) };
        TrackerAccessGate? gate = null;
        Task? mutation = null;
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "bd" && request.Arguments[2] == "comments" && mutation is null)
                mutation = gate!.WaitAsync(token);
            return native.RunAsync(request, token);
        });
        using var factory = Factory(target.Path, runner, clock);
        gate = factory.Services.GetRequiredService<TrackerBridge>().ObservationGate;
        var source = Adapter(factory);
        var keys = Keys("base:workspace:task-1", "section:task-1:history", "attention");
        var token = TestContext.Current.CancellationToken;
        var first = source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.NotNull(mutation);
        await mutation.WaitAsync(TimeSpan.FromSeconds(10), token);
        gate.Release();
        await first;
        var selected = false;
        for (var turn = 0; turn < 12; turn++)
        {
            source.Invalidate([new(turn + 1, "comment", "task-1", null)]);
            var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
            selected |= samples.GetValueOrDefault("section:task-1:history")?.Data is not null;
        }
        Assert.True(selected);
        Assert.True(native.Commands.Count(command => command == "list") >= 6);
        Assert.Equal(9, native.CommentIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RecentSectionHistoryEvictsWithinEntryAndByteBudgets()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 70, Comment = new string('x', 270000) };
        using var factory = Factory(target.Path, runner, clock);
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        var token = TestContext.Current.CancellationToken;
        for (var index = 1; index <= 70; index++)
        {
            var samples = await source.ReadScheduledAsync(Keys($"section:task-{index}:history"), (_, _) => { }, token);
            Assert.NotNull(samples[$"section:task-{index}:history"].Data);
            Assert.InRange(memory.Used, 1, ObservationMemoryBudget.MaximumBytes);
        }
        var before = runner.CommentIds.Count;
        await source.ReadScheduledAsync(Keys("section:task-1:history"), (_, _) => { }, token);
        Assert.Equal(before + 1, runner.CommentIds.Count);
        await source.ReadScheduledAsync([], (_, _) => { }, token);
        Assert.Equal(0, memory.Used);
    }

    [Fact]
    public async Task SectionByteLimitRetainsAcceptedHistoryAndReportsFailure()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner, clock);
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        var keys = Keys("section:task-1:history");
        var token = TestContext.Current.CancellationToken;
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        var reserve = new object();
        Assert.True(memory.TryReplace([], [(reserve, ObservationMemoryBudget.MaximumBytes - memory.Used - 1000)]));
        runner.Comment = new string('x', 20000); source.Invalidate([new(1, "comment", "task-1", null)]);
        var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.Equal("source-limit", samples[keys[0].Value].Error?.Code);
        Assert.InRange(memory.Used, 1, ObservationMemoryBudget.MaximumBytes);
        memory.Release(reserve);
        await source.ReadScheduledAsync([], (_, _) => { }, token);
        Assert.Equal(0, memory.Used);
    }

    [Fact]
    public async Task LateSectionCallbackCannotPopulateAReadmittedEntry()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { TargetPath = target.Path };
        var held = new HoldingRunner(runner, "comments");
        using var factory = Factory(target.Path, held, clock);
        using var service = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        const string key = "section:task-1:history";
        var original = service.Read(key);
        var token = TestContext.Current.CancellationToken;
        var collection = service.CollectOnceAsync(false, token);
        string? replacement = null;
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            clock.Advance(TimeSpan.FromSeconds(61));
            replacement = service.Read(key).Revision;
            Assert.NotEqual(original.Revision, replacement);
        }
        finally { held.Release.TrySetResult(); await collection; }
        Assert.Null(service.Read(key).Data);
        Assert.Equal(replacement, service.Read(key).Revision);
        await service.CollectOnceAsync(false, token);
        Assert.Equal("available", service.Read(key).Data!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task HostShutdownSettlesSectionWorkBeforeReleasingReservations()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { TargetPath = target.Path };
        var held = new HoldingRunner(runner, "comments");
        using var factory = Factory(target.Path, held, new CostClock());
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        using var service = new OperationalObservationService("web-test", source, TimeProvider.System,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(), memory);
        service.Read("section:task-1:history");
        var token = TestContext.Current.CancellationToken;
        await service.StartAsync(token);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await service.StopAsync(token);
        Assert.Empty(runner.CommentIds);
        Assert.Equal(503, Assert.Throws<ObservationRequestException>(() => service.Read("section:task-1:history")).StatusCode);
        service.Dispose();
        Assert.Equal(0, memory.Used);
        var gate = factory.Services.GetRequiredService<TrackerBridge>().ObservationGate;
        await gate.WaitAsync(token); gate.Release();
    }

    [Fact]
    public async Task SlowCoreReservesASelectedSectionTurnBeforeUnrelatedHistories()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 9, Advance = () => clock.Advance(TimeSpan.FromSeconds(1.4)) };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("base:explorer:ready::1", "base:explorer:blocked::1", "section:task-1:history", "attention");
        var token = TestContext.Current.CancellationToken;
        var first = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.True(first["section:task-1:history"].Pending);
        source.Invalidate([new(1, "update", "task-1", null)]);
        var reserved = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.NotNull(reserved["section:task-1:history"].Data);
        Assert.False(reserved["section:task-1:history"].Pending);
        Assert.Null(reserved.GetValueOrDefault("attention")?.Data);
        Assert.Contains("task-1", runner.CommentIds);
    }

    [Fact]
    public async Task TwoSelectedSectionsSettleAndUnselectedOldIdentityBecomesStale()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 2 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("section:task-1:history", "section:task-2:history");
        var token = TestContext.Current.CancellationToken;
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        var second = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.All(second.Values, sample => Assert.False(sample.Pending));
        Assert.Empty(await source.ReadScheduledAsync(keys, (_, _) => { }, token));
        Assert.Equal(2, runner.CommentIds.Count);
        runner.Priority = 2; source.Invalidate();
        var changed = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.Equal("source-conflict", changed["section:task-2:history"].Error?.Code);
        Assert.NotNull(changed["section:task-1:history"].Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdDirectLinkWaitsForSharedValidationAndNeverUsesShow(bool fail)
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { TargetPath = target.Path, Rows = 0, FailEveryList = fail };
        var held = new HoldingRunner(runner, "list");
        using var factory = Factory(target.Path, held, new CostClock());
        using var service = new OperationalObservationService("web-test", Adapter(factory), new CostClock(),
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        service.Read("base:workspace:absent");
        var token = TestContext.Current.CancellationToken;
        var collection = service.CollectOnceAsync(false, token);
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Null(service.Read("base:workspace:absent").Data);
        }
        finally { held.Release.TrySetResult(); await collection; }
        var result = service.Read("base:workspace:absent");
        if (fail)
        {
            Assert.Null(result.Data);
            Assert.Equal("source-timeout", result.Error?.Code);
        }
        else
        {
            Assert.Equal("resource-missing", result.Error?.Code);
            Assert.Equal("unavailable", result.Health);
            Assert.NotNull(result.ObservedAt);
            Assert.Equal("absent", result.Data!.Value.GetProperty("absence").GetProperty("id").GetString());
            Assert.Null(result.PendingAgeMs);
        }
        Assert.DoesNotContain("show", runner.Commands);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task SkippedNativeMembershipContinuesWithoutErasingAcceptedBase()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Advance = () => clock.Advance(TimeSpan.FromSeconds(2)), Blocked = true };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("base:explorer:all::1", "base:explorer:ready::1", "base:explorer:blocked::1");
        var first = await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.NotNull(first["base:explorer:all::1"].Data);
        Assert.True(first["base:explorer:ready::1"].Pending);
        Assert.Equal("budget-exceeded", first["base:explorer:ready::1"].Error?.Code);
        Assert.Null(first["base:explorer:ready::1"].Data);
        var next = await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.False(next["base:explorer:ready::1"].Pending);
        Assert.NotNull(next["base:explorer:ready::1"].Data);
        Assert.Equal(1, runner.Commands.Count(command => command == "list"));
    }

    [Fact]
    public async Task BaseAuditUpdatesSharedRevisionWithoutJournalAndRetiresItsReservation()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner, clock);
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        var keys = Keys("base:workspace:task-1", "base:explorer:all::1");
        var first = await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        var revision = JsonSerializer.SerializeToElement(first[keys[0].Value].Data, ObservationJson.Options).GetProperty("inventoryRevision").GetString();
        runner.Title = "Audit change";
        clock.Advance(TimeSpan.FromSeconds(60));
        var next = await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        var data = JsonSerializer.SerializeToElement(next[keys[0].Value].Data, ObservationJson.Options);
        Assert.NotEqual(revision, data.GetProperty("inventoryRevision").GetString());
        Assert.Equal("Audit change", data.GetProperty("bead").GetProperty("title").GetString());
        Assert.True(memory.Used > 0);
        await source.ReadScheduledAsync([], (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(0, memory.Used);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("comments")]
    public async Task BasePublishesBeforeHeldLegacyDetailAndKeepsRacedRefreshPending(string command)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { TargetPath = target.Path };
        var held = new HoldingRunner(runner, command);
        using var factory = Factory(target.Path, held, clock);
        var source = Adapter(factory);
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        using var pin = service.Pin(["base:explorer:all::1", "base:workspace:task-1", "workspace:task-1"]);
        var token = TestContext.Current.CancellationToken;
        var collection = service.CollectOnceAsync(false, token);
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.False(collection.IsCompleted);
            var explorer = service.Read("base:explorer:all::1");
            Assert.Equal("fresh", explorer.Health);
            Assert.Equal("task-1", explorer.Data!.Value.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.Equal(explorer.Data.Value.GetProperty("inventoryRevision").GetString(),
                service.Read("base:workspace:task-1").Data!.Value.GetProperty("inventoryRevision").GetString());
            Assert.Null(service.Read("workspace:task-1").Data);
            runner.Title = "Changed during detail";
            service.RequestReconciliation();
            Assert.NotNull(service.Read("base:explorer:all::1").PendingAgeMs);
        }
        finally { held.Release.TrySetResult(); await collection; }
        Assert.NotNull(service.Read("base:explorer:all::1").PendingAgeMs);
        await service.CollectOnceAsync(false, token);
        Assert.Equal("Changed during detail", service.Read("base:explorer:all::1").Data!.Value
            .GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Null(service.Read("base:explorer:all::1").PendingAgeMs);
    }

    [Fact]
    public async Task BaseKeysShareInventoryAndNativeMembershipWithoutTargetedBaseReads()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 41, FailReady = true, Blocked = true };
        using var factory = Factory(target.Path, runner, new CostClock());
        var source = Adapter(factory);
        var keys = Keys("base:workspace:task-1", "base:workspace:absent", "base:explorer:all::1",
            "base:explorer:all::2", "base:explorer:blocked::1", "base:explorer:ready::1");
        var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(1, runner.Commands.Count(command => command == "list"));
        Assert.DoesNotContain("show", runner.Commands);
        Assert.Equal(1, runner.Commands.Count(command => command == "ready"));
        Assert.Equal(1, runner.Commands.Count(command => command == "blocked"));
        Assert.NotNull(samples["base:explorer:ready::1"].Error);
        Assert.Null(samples["base:explorer:ready::1"].Data);
        var all = JsonSerializer.SerializeToElement(samples["base:explorer:all::1"].Data, ObservationJson.Options);
        Assert.Equal(40, all.GetProperty("items").GetArrayLength());
        var revision = all.GetProperty("inventoryRevision").GetString();
        Assert.Equal(revision, JsonSerializer.SerializeToElement(samples["base:workspace:absent"].Data, ObservationJson.Options)
            .GetProperty("absence").GetProperty("inventoryRevision").GetString());
        Assert.Equal(1, JsonSerializer.SerializeToElement(samples["base:explorer:blocked::1"].Data, ObservationJson.Options)
            .GetProperty("total").GetInt32());
        Assert.Empty(await source.ReadScheduledAsync(keys, (_, _) => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedSharedValidationRetainsRowsAndCannotProveAbsence()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { FailEveryList = true };
        using var factory = Factory(target.Path, runner, clock);
        using var service = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        service.Read("base:workspace:task-1");
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        Assert.Null(service.Read("base:workspace:task-1").Data);
        runner.FailEveryList = false;
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.CollectOnceAsync(false, token);
        var accepted = service.Read("base:workspace:task-1").Data;
        Assert.NotNull(accepted);
        runner.Rows = 0; runner.FailEveryList = true;
        service.RequestReconciliation();
        await service.CollectOnceAsync(false, token);
        var failed = service.Read("base:workspace:task-1");
        Assert.Equal(accepted, failed.Data);
        Assert.Equal("stale", failed.Health);
        Assert.Equal(JsonValueKind.Null, failed.Data!.Value.GetProperty("absence").ValueKind);
        runner.FailEveryList = false;
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.CollectOnceAsync(false, token);
        var absent = service.Read("base:workspace:task-1");
        Assert.Equal("resource-missing", absent.Error?.Code);
        Assert.Null(absent.PendingAgeMs);
        Assert.Equal("task-1", absent.Data!.Value.GetProperty("absence").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, absent.Data.Value.GetProperty("bead").ValueKind);
        Assert.DoesNotContain("show", runner.Commands);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("blocked")]
    public async Task PendingMembershipDoesNotReportFailureBeforeItsRead(string membership)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { TargetPath = target.Path, Blocked = true };
        var held = new HoldingRunner(runner, membership);
        held.Release.TrySetResult();
        using var factory = Factory(target.Path, held, clock);
        using var service = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        using var pin = service.Pin(["base:explorer:ready::1", "base:explorer:blocked::1"]);
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        var key = $"base:explorer:{membership}::1";
        var accepted = service.Read(key).Data;
        Assert.NotNull(accepted);
        held.Reset();
        service.RequestReconciliation();
        var collection = service.CollectOnceAsync(false, token);
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            var pending = service.Read(key);
            Assert.Equal(accepted, pending.Data);
            Assert.NotNull(pending.PendingAgeMs);
            Assert.Null(pending.Error);
            Assert.Equal("fresh", pending.Health);
            clock.Advance(TimeSpan.FromSeconds(121));
            Assert.Equal("budget-exceeded", service.Read(key).Error?.Code);
        }
        finally { held.Release.TrySetResult(); await collection; }
        // The held read crossed the inventory age and native turn bounds.
        // A new turn must validate the complete required source set.
        await service.CollectOnceAsync(false, token);
        Assert.Null(service.Read(key).Error);
        Assert.Null(service.Read(key).PendingAgeMs);
        runner.FailReady = true;
        service.RequestReconciliation();
        await service.CollectOnceAsync(false, token);
        Assert.Equal("source-timeout", service.Read("base:explorer:ready::1").Error?.Code);
        Assert.NotNull(service.Read("base:explorer:ready::1").Data);
        Assert.Null(service.Read("base:explorer:blocked::1").Error);
    }

    [Fact]
    public async Task RacedInventoryHintCannotPublishCurrentAbsence()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        OperationalObservationSource? source = null;
        var raced = false;
        var runner = new Runner { Rows = 0, Advance = () =>
        {
            if (!raced)
            { raced = true; source!.Invalidate([new BeadsInvalidation(1, "delete", "absent", null)]); }
        } };
        using var factory = Factory(target.Path, runner, clock);
        source = Adapter(factory);
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        const string key = "base:workspace:absent";
        service.Read(key);
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.True(raced);
        Assert.Null(service.Read(key).Data);
        Assert.Null(service.Read(key).Error);
        Assert.NotNull(service.Read(key).PendingAgeMs);
        await service.CollectOnceAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal("resource-missing", service.Read(key).Error?.Code);
        Assert.NotNull(service.Read(key).Data);
        Assert.Null(service.Read(key).PendingAgeMs);
        Assert.DoesNotContain("show", runner.Commands);
    }

    [Fact]
    public async Task ExhaustedMembershipTurnRetainsAcceptedPageWithBudgetError()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var expensive = false;
        var runner = new Runner { Blocked = true, Advance = () =>
        { if (expensive) clock.Advance(TimeSpan.FromSeconds(2)); } };
        using var factory = Factory(target.Path, runner, clock);
        using var service = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        using var pin = service.Pin(["base:explorer:ready::1", "base:explorer:blocked::1"]);
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        var accepted = service.Read("base:explorer:blocked::1").Data;
        Assert.NotNull(accepted);
        expensive = true;
        service.RequestReconciliation();
        await service.CollectOnceAsync(false, token);
        var skipped = service.Read("base:explorer:blocked::1");
        Assert.Equal(accepted, skipped.Data);
        Assert.Equal("budget-exceeded", skipped.Error?.Code);
        Assert.Equal("stale", skipped.Health);
        Assert.NotNull(skipped.PendingAgeMs);
        await service.CollectOnceAsync(false, token);
        Assert.Null(service.Read("base:explorer:blocked::1").Error);
        Assert.Null(service.Read("base:explorer:blocked::1").PendingAgeMs);
    }

    private sealed class HoldingRunner(Runner inner, string command) : IProcessRunner
    {
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Reset()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            if (request.FileName == "bd" && request.Arguments.Contains(command))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return await inner.RunAsync(request, token);
        }
    }

    [Fact]
    public async Task ScheduledSharedSourceNoOpDoesNotAddNativeWorkOrExportedAttempts()
    {
        const string canary = "workspace-prose-not-for-export";
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Web, new(receiver.Endpoint, "", 1),
            _ => { }, metricIntervalMilliseconds: 50);
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Title = canary, Comment = canary };
        using var factory = Factory(target.Path, runner, clock);
        using var observer = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()) { Telemetry = telemetry };
        var subscribers = Enumerable.Range(0, 100).Select(_ => observer.Subscribe(["explorer:all:"], (_, _) => { })).ToArray();
        try
        {
            var token = TestContext.Current.CancellationToken;
            await observer.CollectOnceAsync(false, token);
            var commands = runner.Commands.Count;
            for (var index = 0; index < 10; index++) await observer.CollectOnceAsync(false, token);
            Assert.Equal(commands, runner.Commands.Count);
            await ObservationTelemetryAssertions.WaitForMetricAsync(receiver, "nightweaver.observation.attempts", "tracker", null, value => value == 1);
            await receiver.WaitForSignalsAsync();
            ObservationTelemetryAssertions.AssertSanitized(receiver, canary);
        }
        finally { foreach (var subscriber in subscribers) subscriber.Dispose(); }
    }

    [Fact]
    public async Task DigestAcquiresCompactEvidenceWithinBudgetAndRefreshesCommentOnlyChanges()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 9, Closed = true };
        for (var index = 1; index <= 9; index++) runner.HistoryOverrides[$"task-{index}"] = DigestMergeEvidenceTests.Comments($"task-{index}");
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("digest:all");
        var token = TestContext.Current.CancellationToken;
        ObservationSample? settled = null;
        for (var turn = 0; turn < 4; turn++)
        {
            var before = runner.CommentIds.Count;
            var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
            Assert.InRange(runner.CommentIds.Count - before, 0, 4);
            if (samples.TryGetValue("digest:all", out var sample) && !sample.Pending) settled = sample;
        }
        Assert.NotNull(settled);
        Assert.Null(settled.Error);
        Assert.All(Assert.IsType<CatchUpDigestResponse>(settled.Data).StandaloneCompleted, item => Assert.Equal("merged", item.Outcome));
        Assert.Equal(9, runner.CommentIds.Count);
        Assert.Empty(await source.ReadScheduledAsync(keys, (_, _) => { }, token));

        runner.HistoryOverrides["task-3"] = [];
        source.Invalidate([new BeadsInvalidation(1, "comment", "task-3", null)]);
        var refreshed = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        var digest = Assert.IsType<CatchUpDigestResponse>(refreshed["digest:all"].Data);
        Assert.Equal("missing", Assert.Single(digest.StandaloneCompleted, item => item.Id == "task-3").MergeEvidence!.State);
        Assert.Equal(10, runner.CommentIds.Count);
        Assert.Null(refreshed["digest:all"].Error);
        Assert.False(refreshed["digest:all"].Pending);

        clock.Advance(TimeSpan.FromSeconds(60));
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.InRange(runner.CommentIds.Count, 11, 14);
        await source.ReadScheduledAsync(Keys("explorer:all:"), (_, _) => { }, token);
        var count = runner.CommentIds.Count;
        clock.Advance(TimeSpan.FromSeconds(60));
        await source.ReadScheduledAsync(Keys("explorer:all:"), (_, _) => { }, token);
        Assert.Equal(count, runner.CommentIds.Count);
    }

    [Fact]
    public async Task DigestReportsUnavailableEvidenceOnHistoryFailureAndCapsAcquisition()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 129, Closed = true, FailCommentOnce = "task-1" };
        using var factory = Factory(target.Path, runner, new CostClock());
        var source = Adapter(factory);
        var sample = (await source.ReadScheduledAsync(Keys("digest:all"), (_, _) => { }, TestContext.Current.CancellationToken))["digest:all"];
        Assert.True(sample.Pending);
        Assert.NotNull(sample.Error);
        Assert.Equal(4, runner.CommentIds.Count);
        Assert.InRange(sample.Sources.Count, 1, 130); // 128 bounded histories plus inventory/checkpoint.
        Assert.Contains(sample.Sources, pair => pair.Key == "merge:task-1" && pair.Value.Health == "unavailable");
        var digest = Assert.IsType<CatchUpDigestResponse>(sample.Data);
        Assert.Equal(129, digest.Metrics.CompletedTotal);
        Assert.All(digest.StandaloneCompleted, item => Assert.NotEqual("merged", item.Outcome));
        Assert.Contains(digest.StandaloneCompleted, item => item.MergeEvidence!.State == "unavailable");
        var count = runner.CommentIds.Count;
        await source.ReadScheduledAsync(Keys("digest:all"), (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.InRange(runner.CommentIds.Count - count, 0, 4);
        Assert.Equal(1, runner.CommentIds.Count(id => id == "task-1")); // Backoff cannot become an uncounted read.
    }

    [Fact]
    public async Task TwoSettledWorkspacesStayIdleAndRefreshEachDueSource()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 2 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("workspace:task-1", "workspace:task-2");
        var token = TestContext.Current.CancellationToken;
        var reads = new List<string>();
        source.ReadStarted += (id, _) => reads.Add(id);
        await DrainAsync();
        await AssertIdleAsync();
        reads.Clear();
        clock.Advance(TimeSpan.FromSeconds(2));
        await DrainAsync();
        Assert.Contains("environment:task-1", reads);
        Assert.Contains("environment:task-2", reads);
        Assert.DoesNotContain(reads, id => id.StartsWith("comments:", StringComparison.Ordinal));
        await AssertIdleAsync();
        reads.Clear();
        clock.Advance(TimeSpan.FromSeconds(60));
        await DrainAsync();
        Assert.Contains("comments:task-1", reads);
        Assert.Contains("comments:task-2", reads);
        await AssertIdleAsync();
        reads.Clear();
        source.Invalidate([new BeadsInvalidation(1, "update", "task-1", null)]);
        await DrainAsync();
        Assert.Contains("detail:task-1", reads);
        Assert.Contains("detail:task-2", reads);
        await AssertIdleAsync();

        async Task DrainAsync()
        {
            var completed = new HashSet<string>(StringComparer.Ordinal);
            for (var turn = 0; turn < 8 && completed.Count < keys.Length; turn++)
                foreach (var (key, sample) in await source.ReadScheduledAsync(keys, (_, _) => { }, token))
                    if (!sample.Pending)
                    {
                        Assert.Null(sample.Error);
                        Assert.IsType<BeadWorkspaceResponse>(sample.Data);
                        completed.Add(key);
                    }
            Assert.Equal(keys.Length, completed.Count);
        }

        async Task AssertIdleAsync()
        {
            var commands = runner.Commands.Count;
            var validations = runner.RepositoryValidations;
            // Stay before the independent two-second environment deadline,
            // which an intervening invalidation must not postpone.
            for (var turn = 0; turn < 3; turn++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                Assert.Empty(await source.ReadScheduledAsync(keys, (_, _) => { }, token));
            }
            Assert.Equal(commands, runner.Commands.Count);
            Assert.Equal(validations, runner.RepositoryValidations);
        }
    }

    [Theory]
    [InlineData("attention", "explorer:all:")]
    [InlineData("workspace:task-1", "attention")]
    public async Task RetiredPausedSourcesDoNotKeepIdleTurnsActive(string retiredKey, string remainingKey)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 2 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var token = TestContext.Current.CancellationToken;
        var both = Keys(retiredKey, remainingKey);
        var remaining = Keys(remainingKey);
        var reads = new List<string>();
        source.ReadStarted += (id, _) => reads.Add(id);
        for (var turn = 0; turn < 4; turn++) await source.ReadScheduledAsync(both, (_, _) => { }, token);
        source.Invalidate(); // Navigation can retire sources during a queued Refresh.
        await source.ReadScheduledAsync(remaining, (_, _) => { }, token);
        Assert.Null(source.PendingAge(["inventory"]));
        reads.Clear();
        clock.Advance(TimeSpan.FromSeconds(60));
        await source.ReadScheduledAsync(remaining, (_, _) => { }, token);
        Assert.Contains("inventory", reads);
        Assert.DoesNotContain(reads, id => id.Contains(':') && (remainingKey != "attention" || !id.StartsWith("comments:", StringComparison.Ordinal)));
        await AssertIdleAsync();
        source.Invalidate();
        await source.ReadScheduledAsync(remaining, (_, _) => { }, token);
        await AssertIdleAsync();
        reads.Clear();
        await source.ReadScheduledAsync(both, (_, _) => { }, token);
        Assert.Contains(retiredKey == "attention" ? "comments:task-1" : "detail:task-1", reads);

        async Task AssertIdleAsync()
        {
            var commands = runner.Commands.Count;
            var validations = runner.RepositoryValidations;
            for (var turn = 0; turn < 8; turn++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                Assert.Empty(await source.ReadScheduledAsync(remaining, (_, _) => { }, token));
            }
            Assert.Equal(commands, runner.Commands.Count);
            Assert.Equal(validations, runner.RepositoryValidations);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommentRacingPublicationReconcilesWithoutDirtyingUnrelatedHistories(bool beforeSourceCapture)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        Action? duringCommand = null;
        var runner = new Runner { Rows = 5, Advance = () => duringCommand?.Invoke() };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var journal = new CommentJournal();
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(), journal: journal);
        using var pin = service.Pin(["attention", "workspace:task-1", "explorer:ready:"]);
        var token = TestContext.Current.CancellationToken;
        for (var turn = 0; turn < 4; turn++) await service.CollectOnceAsync(false, token);
        Assert.NotNull(service.Read("attention").Data);
        var historiesBefore = runner.CommentIds.Count;
        var reads = new List<string>();
        var raced = false;
        source.ReadStarted += (id, _) =>
        {
            reads.Add(id);
            if (id != "inventory" || raced) return;
            raced = true;
            clock.Advance(TimeSpan.FromSeconds(5)); // Make the next journal page due before native work starts.
            if (beforeSourceCapture) AddCommentHint();
            else duringCommand = AddCommentHint;
        };
        void AddCommentHint()
        {
            duringCommand = null;
            // Race on either side of source-generation capture: a consumed hint
            // needs reprojection; an outstanding hint still needs another read.
            journal.Sequence = 2;
            runner.CommentOverrides["task-1"] = "Newest burst comment";
            service.PollJournalOnceAsync(token).GetAwaiter().GetResult();
        }
        journal.Sequence = 1;
        await service.PollJournalOnceAsync(token);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        for (var turn = 0; turn < 6; turn++)
        {
            await service.CollectOnceAsync(false, token);
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }
        Assert.True(raced);
        Assert.NotEmpty(runner.CommentIds.Skip(historiesBefore));
        Assert.All(runner.CommentIds.Skip(historiesBefore), id => Assert.Equal("task-1", id));
        Assert.DoesNotContain("ready", reads);
        foreach (var key in Keys("attention", "workspace:task-1", "explorer:ready:"))
        {
            var observation = service.Read(key.Value);
            Assert.True(observation.PendingAgeMs is null, key.Value + ": " + JsonSerializer.Serialize(observation));
            Assert.False(observation.Synchronization!.Pending);
        }
        Assert.Contains("Newest burst comment", service.Read("workspace:task-1").Data!.Value.GetRawText());
        var commands = runner.Commands.Count;
        await service.CollectOnceAsync(false, token);
        Assert.Equal(commands, runner.Commands.Count);
    }

    private sealed class CommentJournal : IConsoleJournalReader
    {
        internal long Sequence { get; set; }
        public Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token)
            => Task.FromResult(new ConsoleJournalPage("same", [new(Sequence, "comment", "task-1", null)]));
    }

    [Fact]
    public async Task RefreshBeforeSourceReadCannotStrandTheNewerPendingGeneration()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 4 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        using var pin = service.Pin(["attention", "digest:auto", "explorer:ready:"]);
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        var refreshDuringRead = true;
        source.ReadStarted += (id, _) =>
        {
            if (id != "inventory" || !refreshDuringRead) return;
            refreshDuringRead = false;
            service.RequestRefresh(["attention"]);
        };
        service.RequestRefresh(["attention"]);
        for (var turn = 0; turn < 10; turn++) await service.CollectOnceAsync(false, token);
        var attention = service.Read("attention");
        Assert.NotNull(attention.Data);
        Assert.Null(attention.PendingAgeMs);
        Assert.False(attention.Synchronization!.Pending);
        var commands = runner.Commands.Count;
        clock.Advance(TimeSpan.FromSeconds(6));
        await service.CollectOnceAsync(false, token);
        Assert.Null(service.Read("attention").PendingAgeMs);
        Assert.Equal(commands, runner.Commands.Count);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("blocked")]
    public async Task RetiredReadinessSourcesDoNotKeepIdleTurnsActive(string filter)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var token = TestContext.Current.CancellationToken;
        var both = Keys("attention", $"explorer:{filter}:");
        var attention = Keys("attention");
        await source.ReadScheduledAsync(both, (_, _) => { }, token);
        Assert.Single(runner.Commands, command => command == filter);
        await source.ReadScheduledAsync(attention, (_, _) => { }, token);
        clock.Advance(TimeSpan.FromSeconds(60));
        await source.ReadScheduledAsync(attention, (_, _) => { }, token);
        await AssertIdleAsync();
        source.Invalidate(); // Refresh must not revive an uninterested source.
        await source.ReadScheduledAsync(attention, (_, _) => { }, token);
        await AssertIdleAsync();
        var inventories = runner.Commands.Count(command => command == "list");
        clock.Advance(TimeSpan.FromSeconds(60));
        await source.ReadScheduledAsync(attention, (_, _) => { }, token);
        Assert.Equal(inventories + 1, runner.Commands.Count(command => command == "list"));
        await AssertIdleAsync();
        await source.ReadScheduledAsync(both, (_, _) => { }, token);
        Assert.Equal(2, runner.Commands.Count(command => command == filter));

        async Task AssertIdleAsync()
        {
            var commands = runner.Commands.Count;
            var validations = runner.RepositoryValidations;
            for (var turn = 0; turn < 8; turn++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                Assert.Empty(await source.ReadScheduledAsync(attention, (_, _) => { }, token));
            }
            Assert.Equal(commands, runner.Commands.Count);
            Assert.Equal(validations, runner.RepositoryValidations);
        }
    }

    [Fact]
    public async Task NewInventoryRebuildsPendingAttentionCohortBeforePublishing()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 5 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("attention");
        var token = TestContext.Current.CancellationToken;
        Assert.Empty(await source.ReadScheduledAsync(keys, (_, _) => { }, token));
        runner.Title = "New authoritative title";
        source.Invalidate([new BeadsInvalidation(1, "update", "task-1", null)]);
        var completed = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.All(Assert.IsType<AttentionDeskItem[]>(completed["attention"].Data), row => Assert.Equal(runner.Title, row.Title));
    }

    [Fact]
    public async Task ScheduledReadsStayIdleSelectCommentHistoryAndAuditWithoutRepeatingCore()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 5 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("attention", "explorer:ready:");
        var token = TestContext.Current.CancellationToken;
        var bootstrap = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.False(bootstrap.ContainsKey("attention"));
        var completed = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.Equal(5, Assert.IsType<AttentionDeskItem[]>(completed["attention"].Data).Length);
        Assert.Single(runner.Commands, command => command == "list");
        Assert.Single(runner.Commands, command => command == "ready");
        var idleCount = runner.Commands.Count;
        for (var turn = 0; turn < 100; turn++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            Assert.Empty(await source.ReadScheduledAsync(keys, (_, _) => { }, token));
        }
        Assert.Equal(idleCount, runner.Commands.Count);
        var historiesBefore = runner.CommentIds.Count;
        runner.CommentOverrides["task-2"] = "External selective history";
        source.Invalidate([new BeadsInvalidation(1, "comment", "task-2", null)]);
        var changed = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.NotNull(changed["attention"].Data);
        Assert.Equal(["task-2"], runner.CommentIds.Skip(historiesBefore));
        Assert.Equal(2, runner.Commands.Count(command => command == "list"));
        Assert.Single(runner.Commands, command => command == "ready");
        clock.Advance(TimeSpan.FromSeconds(60));
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.Equal(3, runner.Commands.Count(command => command == "list"));
        Assert.Equal(2, runner.Commands.Count(command => command == "ready"));
        Assert.Equal(11, runner.CommentIds.Count); // Five bootstrap, one change, five audit.
    }

    [Fact]
    public async Task DirtyHintDuringAuthoritativeReadSurvivesUntilAnotherRead()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        OperationalObservationSource? source = null;
        var invalidate = false;
        var runner = new Runner { Advance = () =>
        {
            if (!invalidate) return;
            invalidate = false;
            source!.Invalidate([new BeadsInvalidation(2, "update", "task-1", null)]);
        } };
        using var factory = Factory(target.Path, runner, clock);
        source = Adapter(factory);
        var keys = Keys("explorer:all:");
        var token = TestContext.Current.CancellationToken;
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        source.Invalidate([new BeadsInvalidation(1, "update", "task-1", null)]);
        invalidate = true;
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.NotNull(source.PendingAge(["inventory"]));
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.Null(source.PendingAge(["inventory"]));
        Assert.Equal(3, runner.Commands.Count(command => command == "list"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledHistoriesProgressUnderContinuousCoreInvalidation(bool selectWorkspace)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { Rows = 5, TargetPath = target.Path };
        OperationalObservationSource? source = null;
        var busy = false;
        long sequence = 0;
        var runner = new RecordingProcessRunner((request, cancellation) =>
        {
            if (busy && request.FileName == "bd")
            {
                clock.Advance(TimeSpan.FromSeconds(1.8));
                if (request.Arguments[2] is "list" or "ready" or "blocked")
                    source!.Invalidate([new BeadsInvalidation(++sequence, "dep_add", "other-task", "another-task")]);
            }
            return native.RunAsync(request, cancellation);
        });
        using var factory = Factory(target.Path, runner, clock);
        source = Adapter(factory);
        var keys = Keys(selectWorkspace
            ? ["attention", "explorer:ready:", "explorer:blocked:", "workspace:task-1"]
            : ["attention", "explorer:ready:", "explorer:blocked:"]);
        var token = TestContext.Current.CancellationToken;

        // Cold bootstrap must drain even though every successful core read is
        // invalidated before it returns. Repeat for the independent due audit.
        busy = true;
        for (var round = 0; round < 2; round++)
        {
            var historyStart = native.CommentIds.Count;
            var listsBefore = native.Commands.Count(command => command == "list");
            var samples = new List<ObservationSample>();
            for (var turn = 0; turn < 16; turn++)
            {
                var result = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
                if (result.TryGetValue("attention", out var attention)) samples.Add(attention);
                if (native.CommentIds.Skip(historyStart).Distinct().Count() == 5) break;
            }
            Assert.Equal(Enumerable.Range(1, 5).Select(index => $"task-{index}").Order(),
                native.CommentIds.Skip(historyStart).Distinct().Order());
            Assert.True(native.Commands.Count(command => command == "list") > listsBefore);
            Assert.Contains("ready", native.Commands);
            Assert.Contains("blocked", native.Commands);
            Assert.NotNull(source.PendingAge(["inventory"]));
            var complete = Assert.Single(samples, sample => sample.Data is not null);
            Assert.Equal(5, Assert.IsType<AttentionDeskItem[]>(complete.Data).Length);
            Assert.True(complete.Pending); // History progress cannot certify the dirty core.
            Assert.All(complete.Sources.Values, state => Assert.NotNull(state.ObservedAt));
            clock.Advance(TimeSpan.FromSeconds(60));
        }
        busy = false;
        ObservationSample? recovered = null;
        for (var turn = 0; turn < 8; turn++)
        {
            var result = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
            if (result.TryGetValue("attention", out recovered) && recovered.Data is not null && !recovered.Pending) break;
        }
        Assert.NotNull(recovered?.Data);
        Assert.False(recovered.Pending);
        Assert.Null(recovered.Error);
        Assert.Null(source.PendingAge(["inventory"]));
    }

    [Fact]
    public async Task UnchangedSourcesStayFreshBetweenCompleteSerialCollections()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        OperationalObservationService? service = null;
        var monitor = false;
        var runner = new Runner { Advance = () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(400));
            if (!monitor) return;
            Assert.Equal("fresh", service!.Read("workspace:task-1").Health);
            Assert.Equal("fresh", service.Read("attention").Health);
            Assert.Equal("fresh", service.Read("digest:auto").Health);
        } };
        using var factory = Factory(target.Path, runner, clock);
        using var observations = new OperationalObservationService("web-test", Adapter(factory), clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        service = observations;
        service.Read("workspace:task-1"); service.Read("attention"); service.Read("digest:auto");
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        var first = service.Read("workspace:task-1");
        Assert.Equal("fresh", first.Health);
        monitor = true;
        await service.CollectOnceAsync(false, token);
        await service.CollectOnceAsync(false, token);
        Assert.Equal(first.Revision, service.Read("workspace:task-1").Revision);
        Assert.Equal(7, runner.Commands.Count); // One bootstrap plus one unsupported capability discovery; idle turns perform no tracker-body reads.
    }

    [Fact]
    public async Task RefusedHistoryReplacementCannotAttachNewContentProvenanceToCachedComments()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 5, Advance = () => clock.Advance(TimeSpan.FromMilliseconds(100)) };
        using var factory = Factory(target.Path, runner, clock);
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        var keys = Keys("workspace:task-1", "attention");
        var token = TestContext.Current.CancellationToken;
        var reads = new List<ObservationSourceState>();
        var first = await source.ReadTrackerAsync(keys, token, (id, state) => { if (id == "comments:task-1") reads.Add(state); });
        Assert.False(first.ContainsKey("attention")); // Four-history cap leaves a cohort in progress.
        var original = Assert.Single(reads);
        var filler = new object();
        Assert.True(memory.TryReplace([], [(filler, ObservationMemoryBudget.MaximumBytes - memory.Used - 512)]));
        runner.CommentOverrides["task-1"] = new string('B', 10000);
        reads.Clear();
        var next = await source.ReadTrackerAsync(keys, token, (id, state) => { if (id == "comments:task-1") reads.Add(state); });
        Assert.Equal("source-limit", next["workspace:task-1"].Error?.Code);
        Assert.NotNull(next["attention"].Data);
        Assert.NotSame(original.ContentIdentity, reads[0].ContentIdentity);
        Assert.Same(original.ContentIdentity, next["attention"].Sources["comments:task-1"].ContentIdentity);
        Assert.Equal(original.ObservedAt, next["attention"].Sources["comments:task-1"].ObservedAt);
    }

    [Fact]
    public async Task SelectedHistoryCacheRefusalRemainsAnExplicitRecoverableSourceLimit()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner);
        var memory = new ObservationMemoryBudget();
        var owner = new object();
        Assert.True(memory.TryReplace([], [(owner, ObservationMemoryBudget.MaximumBytes)]));
        var source = Adapter(factory, memory);
        var keys = Keys("workspace:task-1");
        var token = TestContext.Current.CancellationToken;
        var failed = await source.ReadTrackerAsync(keys, token);
        Assert.Equal("source-limit", failed[keys[0].Value].Error?.Code);
        Assert.Single(runner.Commands, command => command == "comments");
        memory.Release(owner);
        var recovered = await source.ReadTrackerAsync(keys, token);
        Assert.Null(recovered[keys[0].Value].Error);
        Assert.IsType<BeadWorkspaceResponse>(recovered[keys[0].Value].Data);
    }

    [Fact]
    public async Task SourceContentProvenanceChangesWithDataAndResetsAfterIdleEviction()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var keys = Keys("explorer:all:");
        var token = TestContext.Current.CancellationToken;
        var first = (await source.ReadTrackerAsync(keys, token))[keys[0].Value].Sources["inventory"].ContentIdentity;
        var observed = new List<ObservationSourceState>();
        await source.ReadTrackerAsync(keys, token, (id, state) => { if (id == "inventory") observed.Add(state); });
        Assert.Same(first, Assert.Single(observed).ContentIdentity);
        runner.Title = "external edit";
        observed.Clear();
        await source.ReadTrackerAsync(keys, token, (id, state) => { if (id == "inventory") observed.Add(state); });
        var changed = Assert.Single(observed).ContentIdentity;
        Assert.NotSame(first, changed);
        await source.ReadTrackerAsync([], token);
        var readmitted = (await source.ReadTrackerAsync(keys, token))[keys[0].Value].Sources["inventory"].ContentIdentity;
        Assert.NotSame(changed, readmitted);
    }

    [Fact]
    public async Task ExplorerPagesShareInventoryAndReconcileExternalChangesWithBoundedResponses()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 2000 };
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var keys = Keys("explorer:all::1", "explorer:all::2", "explorer:all::50", "explorer:ready::1", "explorer:blocked::1", "explorer:all:T3JpZ2luYWw:1");
        var first = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        Assert.Single(runner.Commands, command => command == "list");
        var one = Assert.IsType<ExplorerPage>(first[keys[0].Value].Data);
        var two = Assert.IsType<ExplorerPage>(first[keys[1].Value].Data);
        Assert.Equal(2000, one.Total);
        Assert.Equal(40, one.Items.Count);
        Assert.Equal(40, two.Items.Count);
        Assert.Empty(one.Items.Select(row => row.Id).Intersect(two.Items.Select(row => row.Id)));
        Assert.Equal(one.Items.OrderBy(row => row.Priority).ThenBy(row => row.Id, StringComparer.Ordinal), one.Items);
        Assert.Single(Assert.IsType<ExplorerPage>(first[keys[3].Value].Data).Items);
        Assert.Empty(Assert.IsType<ExplorerPage>(first[keys[4].Value].Data).Items);
        runner.Rows = 41; runner.Ready = false; runner.Blocked = true; runner.Title = new string('T', 1000);
        var second = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        var last = Assert.IsType<ExplorerPage>(second[keys[2].Value].Data);
        Assert.Equal(2, last.Page);
        Assert.Single(last.Items);
        Assert.Equal(401, last.Items[0].Title.Length);
        Assert.Empty(Assert.IsType<ExplorerPage>(second[keys[3].Value].Data).Items);
        Assert.Single(Assert.IsType<ExplorerPage>(second[keys[4].Value].Data).Items);
        Assert.Empty(Assert.IsType<ExplorerPage>(second[keys[5].Value].Data).Items);
        Assert.Equal(2, Assert.IsType<ExplorerPage>(second[keys[1].Value].Data).Page);
        runner.Rows = 0;
        var empty = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        Assert.Null(empty[keys[1].Value].Error);
        Assert.Equal(1, Assert.IsType<ExplorerPage>(empty[keys[1].Value].Data).Page);
        Assert.Empty(Assert.IsType<ExplorerPage>(empty[keys[1].Value].Data).Items);
    }

    [Fact]
    public async Task SharedDigestCheckpointUsesPreReadWholeSecondCutoffAndRetainsBoundaryActivity()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        // The inventory itself crosses the boundary; unrelated membership reads
        // must not be needed to advance the test clock.
        var runner = new Runner { Advance = () => clock.Advance(TimeSpan.FromMilliseconds(900)) };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var token = TestContext.Current.CancellationToken;
        var first = await source.ReadTrackerAsync(Keys("digest:24h"), token);
        var originalCheckpoint = first["digest:24h"].Sources["checkpoint"].ContentIdentity;
        Assert.NotNull(originalCheckpoint);
        var displayed = Assert.IsType<CatchUpDigestResponse>(first["digest:24h"].Data);
        Assert.Equal(cutoff, displayed.GeneratedAt);
        Assert.True(clock.GetUtcNow() > cutoff.AddSeconds(1));
        Assert.Empty(displayed.StandaloneOpen);
        await factory.Services.GetRequiredService<CatchUpCheckpointStore>().WriteLastCaughtUpAtAsync(displayed.GeneratedAt, token);
        // An unseen write in the exact boundary second must remain unread, even
        // though the first inventory and its projection completed after that second.
        runner.UpdatedAt = cutoff.ToString("O", CultureInfo.InvariantCulture);
        var next = await source.ReadTrackerAsync(Keys("digest:since_caught_up"), token);
        Assert.NotSame(originalCheckpoint, next["digest:since_caught_up"].Sources["checkpoint"].ContentIdentity);
        var digest = Assert.IsType<CatchUpDigestResponse>(next["digest:since_caught_up"].Data);
        Assert.Equal(cutoff, digest.LastCaughtUpAt);
        Assert.False(digest.IsCaughtUp);
        Assert.Equal(cutoff, Assert.Single(digest.StandaloneOpen).ActivityAt);
    }

    [Fact]
    public async Task SharedInventoryProjectsAllViewsAndSelectedCommentsAreSharedWithAttention()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var keys = Keys("explorer:all:", "explorer:ready:", "explorer:blocked:", "digest:all", "attention", "workspace:task-1");
        var samples = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        Assert.All(samples.Values, sample => Assert.Null(sample.Error));
        Assert.Equal(6, samples.Count);
        Assert.Equal(2, runner.Commands.Count(command => command == "list")); // core plus Workspace identity confirmation
        Assert.Single(runner.Commands, command => command == "comments");
        Assert.Single(runner.Commands, command => command == "ready");
        Assert.Single(runner.Commands, command => command == "blocked");
        Assert.Single(Assert.IsType<BeadExplorerItem[]>(samples["explorer:all:"].Data));
        Assert.Single(Assert.IsType<AttentionDeskItem[]>(samples["attention"].Data));
        var workspace = Assert.IsType<BeadWorkspaceResponse>(samples["workspace:task-1"].Data);
        Assert.Equal("task-1", workspace.Bead.Id);
        Assert.Single(workspace.Comments);
        Assert.Equal("complete", workspace.RelationObservation.Incoming);
        Assert.Equal(0, Assert.IsType<CatchUpDigestResponse>(samples["digest:all"].Data).GeneratedAt.Millisecond);
    }

    [Fact]
    public async Task AlternatingPriorityPreservesTheSharedCommentCommandBound()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 9 };
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var sawAttention = false;
        var sawWorkspace = false;
        for (var index = 0; index < 4; index++)
        {
            var previous = runner.CommentIds.Count;
            var samples = await source.ReadTrackerAsync(Keys("workspace:task-1", "attention"), TestContext.Current.CancellationToken);
            Assert.InRange(runner.CommentIds.Count - previous, 0, 4);
            sawAttention |= samples.TryGetValue("attention", out var attention) && attention.Data is AttentionDeskItem[] { Length: 9 };
            sawWorkspace |= samples.TryGetValue("workspace:task-1", out var workspace) && workspace.Data is BeadWorkspaceResponse;
        }
        Assert.True(sawAttention);
        Assert.True(sawWorkspace);
    }

    [Fact]
    public async Task ExternalMembershipAndCommentsChangesAreReadEvenWithUnchangedInventory()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var keys = Keys("explorer:ready:", "workspace:task-1");
        var first = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        runner.Ready = false;
        runner.Comment = "external comments-only edit";
        var second = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        Assert.Single(Assert.IsType<BeadExplorerItem[]>(first["explorer:ready:"].Data));
        Assert.Empty(Assert.IsType<BeadExplorerItem[]>(second["explorer:ready:"].Data));
        Assert.Equal(runner.Comment, Assert.Single(Assert.IsType<BeadWorkspaceResponse>(second["workspace:task-1"].Data).Comments).Body);
    }

    [Fact]
    public async Task ReadinessFailureDoesNotPoisonIndependentInventoryAndWorkspaceConflictIsRetryable()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { FailReady = true, Conflict = true };
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var samples = await source.ReadTrackerAsync(Keys("explorer:all:", "explorer:ready:", "workspace:task-1"), TestContext.Current.CancellationToken);
        Assert.Null(samples["explorer:all:"].Error);
        Assert.Equal("source-timeout", samples["explorer:ready:"].Error?.Code);
        Assert.Equal("source-conflict", samples["workspace:task-1"].Error?.Code);
        Assert.True(samples["workspace:task-1"].Error?.Retryable);
    }

    [Fact]
    public async Task TransientHistoryFailurePreservesCompletedCohortProgress()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 5, FailCommentOnce = "task-3" };
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var keys = Keys("attention");
        var failed = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        Assert.Equal("source-timeout", failed["attention"].Error?.Code);
        var recovered = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
        Assert.Equal(5, Assert.IsType<AttentionDeskItem[]>(recovered["attention"].Data).Length);
        Assert.Null(recovered["attention"].Error);
        Assert.Single(runner.CommentIds, id => id == "task-1");
        Assert.Single(runner.CommentIds, id => id == "task-2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientWorkspaceTimeoutRetainsCompletedSubstepsAndConfirmsIdentity(bool confirmingInventory)
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { FailCommentOnce = confirmingInventory ? null : "task-1", FailListAt = confirmingInventory ? 2 : 0 };
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var keys = Keys("workspace:task-1");
        var token = TestContext.Current.CancellationToken;
        var failed = await source.ReadTrackerAsync(keys, token);
        Assert.Null(failed[keys[0].Value].Data);
        Assert.Equal("source-timeout", failed[keys[0].Value].Error?.Code);
        var recovered = await source.ReadTrackerAsync(keys, token);
        Assert.IsType<BeadWorkspaceResponse>(recovered[keys[0].Value].Data);
        Assert.Null(recovered[keys[0].Value].Error);
        Assert.Single(runner.Commands, command => command == "show");
        Assert.Equal(2, runner.Commands.Count(command => command == "dep"));
        Assert.Equal(confirmingInventory ? 3 : 2, runner.Commands.Count(command => command == "list"));
        Assert.Equal(confirmingInventory ? 1 : 2, runner.Commands.Count(command => command == "comments"));

        // Retaining substeps cannot turn a changed workflow into a fresh blend.
        runner.FailCommentOnce = "task-1";
        runner.CommentTimeoutsRemaining = 1;
        await source.ReadTrackerAsync(keys, token);
        runner.Title = "External edit during timeout";
        runner.Priority = 2;
        var conflict = await source.ReadTrackerAsync(keys, token);
        Assert.Null(conflict[keys[0].Value].Data);
        Assert.Equal("source-conflict", conflict[keys[0].Value].Error?.Code);
        var fresh = await source.ReadTrackerAsync(keys, token);
        Assert.Equal(runner.Title, Assert.IsType<BeadWorkspaceResponse>(fresh[keys[0].Value].Data).Bead.Title);
    }

    [Fact]
    public async Task WorkspaceConflictRefreshesCoreWhileAnotherWorkspaceKeepsTimingOut()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner
        {
            Advance = () => clock.Advance(TimeSpan.FromSeconds(1.6)),
            FailCommentOnce = "task-1", CommentTimeoutsRemaining = 1000
        };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("workspace:task-1");
        var token = TestContext.Current.CancellationToken;
        await source.ReadTrackerAsync(keys, token);
        runner.Rows = 2;
        keys = Keys("workspace:task-1", "workspace:task-2");
        async Task<BeadWorkspaceResponse?> ReadHealthyAsync()
        {
            var samples = await source.ReadTrackerAsync(keys, token);
            if (samples.TryGetValue(keys[0].Value, out var blocked)) Assert.Null(blocked.Data);
            return samples.GetValueOrDefault(keys[1].Value)?.Data as BeadWorkspaceResponse;
        }
        BeadWorkspaceResponse? first = null;
        for (var turn = 0; turn < 20 && first is null; turn++) first = await ReadHealthyAsync();
        Assert.NotNull(first);
        runner.Title = "External edit while other Workspace is pending";
        runner.Priority = 2;
        BeadWorkspaceResponse? changed = null;
        for (var turn = 0; turn < 20; turn++)
        {
            var sample = await ReadHealthyAsync();
            if (sample?.Bead.Title == runner.Title) { changed = sample; break; }
        }
        Assert.NotNull(changed);
        Assert.Equal(2, changed.Bead.Priority);
        Assert.True(runner.CommentTimeoutsRemaining > 0);
        var remaining = runner.CommentTimeoutsRemaining;
        runner.FailEveryList = true;
        for (var turn = 0; turn < 8; turn++) await ReadHealthyAsync();
        Assert.True(remaining - runner.CommentTimeoutsRemaining >= 2,
            "A failed core refresh must not discard the captured inventory needed to retry pending detail work.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdWorkspaceCompletesBeforeFiniteContentionInterval(bool hasComment)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path, Rows = 2, OutgoingDependency = true };
        if (!hasComment) native.HistoryOverrides["task-1"] = [];
        var started = clock.GetUtcNow();
        var trace = new List<string>();
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.FileName != "bd") return await native.RunAsync(request, token);
            var offset = (clock.GetUtcNow() - started).TotalSeconds;
            trace.Add($"{offset:F3}s {request.Arguments[2]}");
            // Injected command-independent contention, not a claimed hosted cause.
            // Every bd command uses the same clock and rule before and after repair.
            clock.Advance(TimeSpan.FromSeconds(offset >= 8 && offset < 45 ? 2 : 1.6));
            if (offset >= 8 && offset < 45) throw new ToolExecutionException("Injected native command timed out after 2s");
            if (request.Arguments[2] == "version")
                return new ProcessResult(0, "{\"version\":\"1.3.1\",\"build\":\"c1c4b642a\",\"commit\":\"c1c4b642ac1c08d8c828007a1c2f96e47e43ef7c\",\"schema_version\":1}", "");
            var result = await native.RunAsync(request, token);
            if (request.Arguments[2] != "show") return result;
            var rows = System.Text.Json.Nodes.JsonNode.Parse(result.StandardOutput)!.AsArray();
            if (hasComment) rows[0]!["comments"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"id\":\"comment-1\",\"issue_id\":\"task-1\",\"author\":\"human\",\"text\":\"Initial comment\",\"created_at\":\"2026-01-01T00:00:00Z\"}]");
            // Omitted incoming array is empty only after pinned producer discovery.
            return result with { StandardOutput = rows.ToJsonString() };
        });
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        ObservationSample? completed = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
        {
            var samples = await source.ReadScheduledAsync(Keys("workspace:task-1"), (_, _) => { }, TestContext.Current.CancellationToken);
            if (samples.TryGetValue("workspace:task-1", out var sample) && sample.Data is not null && !sample.Pending && sample.Error is null)
            { completed = sample; break; }
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", trace));
        TestContext.Current.TestOutputHelper?.WriteLine($"Simulated cold completion: {(clock.GetUtcNow() - started).TotalSeconds:F3}s; commands {source.CommandCount}");
        Assert.NotNull(completed);
        var workspace = Assert.IsType<BeadWorkspaceResponse>(completed.Data);
        Assert.Contains(workspace.Relations, relation => relation.Id == "task-2" && relation.Type == "blocks" && relation.Direction == "outgoing");
        Assert.Equal(hasComment ? 1 : 0, workspace.Comments.Count);
        Assert.Contains(runner.Requests, request => request.Arguments.Contains("down"));
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("unsupported")]
    [InlineData("malformed")]
    [InlineData("limited")]
    [InlineData("dep-failure")]
    [InlineData("invalidation")]
    [InlineData("unknown-producer")]
    [InlineData("version-failure")]
    public async Task ColdCombinedReadPreservesNativeFallbackGenerationsAndWarmReadBudget(string mode)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path, Rows = 2, OutgoingDependency = true, FailRelations = mode == "dep-failure" };
        OperationalObservationSource? source = null;
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            var result = await native.RunAsync(request, token);
            if (request.Arguments.Contains("version"))
            {
                if (mode == "unknown-producer") return new ProcessResult(0, "{}", "");
                if (mode == "version-failure") throw new ToolExecutionException("Version discovery timed out after 2s");
                return new ProcessResult(0, "{\"version\":\"1.3.1\",\"build\":\"c1c4b642a\",\"commit\":\"c1c4b642ac1c08d8c828007a1c2f96e47e43ef7c\",\"schema_version\":1}", "");
            }
            if (!request.Arguments.Contains("--include-dependents")) return result;
            if (mode == "unsupported") return new ProcessResult(1, "", "unsupported flags");
            if (mode == "limited") return result with { StandardOutput = new string('x', 2 * 1024 * 1024 + 1) };
            if (mode == "malformed") return result with { StandardOutput = "[{}]" };
            var rows = System.Text.Json.Nodes.JsonNode.Parse(result.StandardOutput)!.AsArray();
            rows[0]!["comments"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"id\":\"comment-1\",\"issue_id\":\"task-1\",\"author\":\"human\",\"text\":\"Initial comment\",\"created_at\":\"2026-01-01T00:00:00Z\"}]");
            rows[0]!["dependents"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"id\":\"task-2\",\"dependency_type\":\"related\"}]");
            if (mode == "invalidation") source!.Invalidate([new(1, "dep_add", "task-1", null)]);
            return result with { StandardOutput = rows.ToJsonString() };
        });
        using var factory = Factory(target.Path, runner, clock);
        source = Adapter(factory);
        var key = Keys("workspace:task-1");
        var sample = (await source.ReadScheduledAsync(key, (_, _) => { }, TestContext.Current.CancellationToken))[key[0].Value];
        var workspace = Assert.IsType<BeadWorkspaceResponse>(sample.Data);
        Assert.Single(workspace.Comments);
        Assert.Contains(runner.Requests, request => request.Arguments.Contains("down"));
        if (mode == "dep-failure")
        {
            Assert.NotNull(sample.Error);
            Assert.True(sample.Partial);
            Assert.Equal("unavailable", sample.Sources["relations-down:task-1"].Health);
            return;
        }
        Assert.Null(sample.Error);
        Assert.Equal(mode == "invalidation", sample.Pending);
        if (mode == "supported")
        {
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("up"));
            Assert.Contains(workspace.Relations, relation => relation.Id == "task-2" && relation.Direction == "incoming" && relation.Type == "related");
        }
        else Assert.Contains(runner.Requests, request => request.Arguments.Contains("up"));
        var before = runner.Requests.Count;
        native.Comment = "Warm comment";
        source.Invalidate([new(2, "comment", "task-1", null)]);
        sample = (await source.ReadScheduledAsync(key, (_, _) => { }, TestContext.Current.CancellationToken))[key[0].Value];
        Assert.Null(sample.Error);
        Assert.False(sample.Pending);
        Assert.Equal("Warm comment", Assert.Single(Assert.IsType<BeadWorkspaceResponse>(sample.Data).Comments).Body);
        Assert.DoesNotContain(runner.Requests.Skip(before), request => request.Arguments.Contains("--include-dependents"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredAttentionHistoryRequiresCommentValueBeforeWorkspacePublication(bool discoveryFails)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path };
        var selectingWorkspace = false;
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.FileName == "bd" && selectingWorkspace)
            {
                var command = request.Arguments[2];
                // Initial list + combined show + producer discovery exhaust the
                // native turn. These costs are injected, not hosted timings.
                clock.Advance(TimeSpan.FromSeconds(command == "version" ? 2 : command == "show" ? 1.2 : .9));
                if (command == "version")
                {
                    if (discoveryFails) throw new ToolExecutionException("Version discovery timed out after 2s");
                    return new ProcessResult(0, "{}", "");
                }
            }
            return await native.RunAsync(request, token);
        });
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var token = TestContext.Current.CancellationToken;
        var attention = (await source.ReadScheduledAsync(Keys("attention"), (_, _) => { }, token))["attention"];
        Assert.Null(attention.Error);
        Assert.False(attention.Pending);
        Assert.Single(Assert.IsType<AttentionDeskItem[]>(attention.Data));
        Assert.Single(native.CommentIds);

        clock.Advance(TimeSpan.FromSeconds(61)); // Expire the audit without changing the source generation.
        native.Comment = "Human guidance after the Attention observation";
        selectingWorkspace = true;
        var keys = Keys("workspace:task-1"); // Retire Attention while retaining this selected history.
        var first = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        Assert.Single(native.CommentIds); // No comment read fits after discovery.
        Assert.False(first.TryGetValue(keys[0].Value, out var initial) && initial.Data is not null && !initial.Pending && initial.Error is null);
        var started = clock.GetUtcNow();
        ObservationSample? completed = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
        {
            var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
            if (samples.TryGetValue(keys[0].Value, out var sample) && sample.Data is not null && !sample.Pending && sample.Error is null)
            {
                Assert.Equal(2, native.CommentIds.Count);
                Assert.Equal(native.Comment, Assert.Single(Assert.IsType<BeadWorkspaceResponse>(sample.Data).Comments).Body);
                completed = sample;
                break;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.NotNull(completed);
        Assert.Single(runner.Requests, request => request.Arguments.Contains("version"));
    }

    [Fact]
    public async Task FailedCombinedReadCannotRepeatOrDiscardProgressAfterSpendingTheTurnBudget()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path };
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.FileName == "bd")
            {
                if (request.Arguments.Contains("--include-dependents"))
                {
                    // Inject settlement overhead after the original 2s command
                    // bound, as allowed by the retained native timeline evidence.
                    clock.Advance(TimeSpan.FromSeconds(3.2));
                    throw new ToolExecutionException("Injected native command timed out after 2s");
                }
                clock.Advance(TimeSpan.FromSeconds(1.6));
            }
            return await native.RunAsync(request, token);
        });
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var started = clock.GetUtcNow();
        ObservationSample? complete = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
        {
            var samples = await source.ReadScheduledAsync(Keys("workspace:task-1"), (_, _) => { }, TestContext.Current.CancellationToken);
            if (samples.TryGetValue("workspace:task-1", out var sample) && sample.Data is not null && !sample.Pending && sample.Error is null)
            { complete = sample; break; }
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.NotNull(complete);
        Assert.Single(runner.Requests, request => request.Arguments.Contains("--include-dependents"));
        Assert.Contains(runner.Requests, request => request.Arguments.Contains("comments"));
        Assert.Contains(runner.Requests, request => request.Arguments.Contains("up"));
        Assert.Contains(runner.Requests, request => request.Arguments.Contains("down"));
    }

    [Fact]
    public async Task SlowWorkspaceWithTransientHistoryTimeoutsPublishesWithinTheNativeSmokeDeadline()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner
        {
            Advance = () => clock.Advance(TimeSpan.FromSeconds(1.9)),
            FailCommentOnce = "task-1", CommentTimeoutsRemaining = 4
        };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var started = clock.GetUtcNow();
        ObservationSample? completed = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
        {
            var samples = await source.ReadTrackerAsync(Keys("workspace:task-1"), TestContext.Current.CancellationToken);
            if (samples.TryGetValue("workspace:task-1", out var sample) && sample.Data is not null)
            {
                completed = sample;
                break;
            }
        }
        Assert.NotNull(completed);
        Assert.IsType<BeadWorkspaceResponse>(completed.Data);
        Assert.True(clock.GetUtcNow() - started < TimeSpan.FromSeconds(45));
        Assert.Equal(5, runner.CommentIds.Count);
        Assert.Single(runner.Commands, command => command == "show");
    }

    [Fact]
    public async Task SlowNativeValidationAllowsInitialWorkspaceWithinTheNativeSmokeDeadline()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path };
        var runner = new RecordingProcessRunner((request, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1.6));
            return native.RunAsync(request, token);
        });
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var started = clock.GetUtcNow();
        ObservationSample? completed = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
        {
            var samples = await source.ReadTrackerAsync(Keys("workspace:task-1"), TestContext.Current.CancellationToken);
            if (samples.TryGetValue("workspace:task-1", out var sample) && sample.Data is not null)
            {
                completed = sample;
                break;
            }
        }
        Assert.NotNull(completed);
        Assert.IsType<BeadWorkspaceResponse>(completed.Data);
        Assert.True(clock.GetUtcNow() - started < TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task InactiveAttentionReleasesAnInterruptedCohortsHistoryReservations()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 5, FailCommentOnce = "task-3" };
        using var factory = Factory(target.Path, runner);
        var memory = new ObservationMemoryBudget();
        var source = Adapter(factory, memory);
        await source.ReadTrackerAsync(Keys("attention"), TestContext.Current.CancellationToken);
        Assert.True(memory.Used > 0);
        await source.ReadTrackerAsync(Keys("explorer:all:"), TestContext.Current.CancellationToken);
        Assert.Equal(0, memory.Used);
    }

    [Fact]
    public async Task LargeInventoryAndAttentionHistoriesFailExplicitlyWithoutPrefixes()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner { Rows = 65 };
        using var factory = Factory(target.Path, runner);
        var source = Adapter(factory);
        var samples = await source.ReadTrackerAsync(Keys("explorer:all:", "attention"), TestContext.Current.CancellationToken);
        Assert.Equal(65, Assert.IsType<BeadExplorerItem[]>(samples["explorer:all:"].Data).Length);
        Assert.Equal("source-limit", samples["attention"].Error?.Code);
        runner.Rows = 10001;
        samples = await source.ReadTrackerAsync(Keys("explorer:all:"), TestContext.Current.CancellationToken);
        Assert.Equal("source-limit", samples["explorer:all:"].Error?.Code);
        Assert.Null(samples["explorer:all:"].Data);
    }

    [Fact]
    public async Task VersionedHttpReadsValidateScopeAndRefreshWithoutSynchronousTrackerReads()
    {
        using var target = new ObservationTargetFixture();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner);
        using var client = factory.CreateClient();
        var token = TestContext.Current.CancellationToken;
        using var foreign = await client.GetAsync("/api/operations/v1/projects/foreign/resources?key=attention", token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var invalid = await client.GetAsync("/api/operations/v1/projects/web-test/resources?key=attention&key=factory", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(runner.Commands);
        using var response = await client.GetAsync("/api/operations/v1/projects/web-test/resources?key=explorer:all:", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.Equal("nightweaver-observation-v1", body.RootElement.GetProperty("schema").GetString());
        client.DefaultRequestHeaders.Add("X-Nightweaver-Request", "console-v1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var refresh = await client.PostAsync("/api/operations/v1/projects/web-test/refresh",
            new StringContent("{\"keys\":[\"attention\",\"factory\"]}", Encoding.UTF8, "application/json"), token);
        Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
        using var duplicate = await client.PostAsync("/api/operations/v1/projects/web-test/refresh",
            new StringContent("{\"keys\":[\"attention\",\"attention\"]}", Encoding.UTF8, "application/json"), token);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalCausalityIncludesDependencyHintsBeforeAndAfterCoreInventory(bool dependencyDuringDetails)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Rows = 2 };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var token = TestContext.Current.CancellationToken;
        var keys = Keys("workspace:task-1");
        await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        var reads = new List<(string Id, string Reason, bool CommentHint, bool DependencyHint)>();
        var dependencyHint = false;
        var commentHint = false;
        source.ReadStarted += (id, reason) =>
        {
            // Match the hosted interleaving: the comment starts core collection,
            // then the dependency arrives before the first detail generation.
            if (dependencyDuringDetails && id == "detail:task-1" && !dependencyHint) AddDependency();
            reads.Add((id, reason, commentHint, dependencyHint));
        };
        void AddDependency()
        {
            dependencyHint = true;
            runner.OutgoingDependency = true;
            source.Invalidate([new BeadsInvalidation(2, "dep_add", "task-1", "task-2")]);
        }
        runner.Comment = "External combined observer comment marker";
        commentHint = true;
        source.Invalidate([new BeadsInvalidation(1, "comment", "task-1", null)]);
        if (!dependencyDuringDetails) AddDependency();
        ObservationSample? published = null;
        for (var turn = 0; turn < 4; turn++)
        {
            var samples = await source.ReadScheduledAsync(keys, (_, _) => { }, token);
            if (samples.TryGetValue(keys[0].Value, out var sample) && sample.Data is not null && !sample.Pending)
            { published = sample; break; }
        }
        Assert.NotNull(published);
        Assert.Null(published.Error);
        var workspace = Assert.IsType<BeadWorkspaceResponse>(published.Data);
        Assert.Contains(workspace.Comments, comment => comment.Body == runner.Comment);
        Assert.Contains(workspace.Relations, relation => relation.Id == "task-2" && relation.Type == "blocks" && relation.Direction == "outgoing");
        Assert.Null(source.PendingAge(["inventory", "relations-down:task-1", "comments:task-1"]));
        AssertJournalCausedReads(reads, "task-1");
        if (dependencyDuringDetails)
        {
            Assert.Contains(reads, read => read.Id == "inventory" && read.Reason == "dirty" && !read.DependencyHint);
            Assert.DoesNotContain(reads, read => read.Id == "inventory" && read.Reason == "dirty" && read.DependencyHint);
        }
    }

    private static void AssertJournalCausedReads(
        IEnumerable<(string Id, string Reason, bool CommentHint, bool DependencyHint)> reads, string bead)
    {
        // Environment keeps its independent two-second policy. Tracker visibility
        // must precede an actual 60-second source audit, regardless of host speed.
        Assert.DoesNotContain(reads, read => read.Reason == "audit" && !read.Id.StartsWith("environment:", StringComparison.Ordinal));
        Assert.Contains(reads, read => read.Id == "comments:" + bead && read.Reason == "dirty" && read.CommentHint);
        Assert.Contains(reads, read => read.Id == "relations-down:" + bead && read.Reason == "dirty" && read.DependencyHint);
        Assert.Contains(reads, read => IsDependencyHintInventoryRead(read.Id, read.Reason, read.DependencyHint));
    }

    [Theory]
    [InlineData("audit", true)]
    [InlineData("bootstrap", true)]
    [InlineData("dirty", false)]
    [InlineData("confirmation", false)]
    public void JournalCausalityRejectsInventoryWithoutPostHintEventEvidence(string reason, bool dependencyHint)
    {
        (string Id, string Reason, bool CommentHint, bool DependencyHint)[] reads =
        [
            ("comments:task-1", "dirty", true, false),
            ("relations-down:task-1", "dirty", true, true),
            ("inventory", reason, true, dependencyHint)
        ];
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertJournalCausedReads(reads, "task-1"));
    }

    [Fact]
    public async Task DependencyHintDuringWorkspaceRefreshCanUseInventoryConfirmation()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner();
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("workspace:task-1");
        var token = TestContext.Current.CancellationToken;
        for (var turn = 0; turn < 4; turn++) await source.ReadScheduledAsync(keys, (_, _) => { }, token);
        var dependencyHint = false;
        var reads = new List<(string Id, string Reason, bool DependencyHint)>();
        source.ReadStarted += (id, reason) =>
        {
            reads.Add((id, reason, dependencyHint));
            if (id != "detail:task-1" || dependencyHint) return;
            dependencyHint = true;
            source.Invalidate([new BeadsInvalidation(2, "dep_add", "task-1", "task-2")]);
        };

        source.Invalidate([new BeadsInvalidation(1, "comment", "task-1", null)]);
        var refreshed = await source.ReadScheduledAsync(keys, (_, _) => { }, token);

        Assert.True(dependencyHint);
        Assert.NotNull(refreshed["workspace:task-1"].Data);
        Assert.Contains(reads, read => read.Id == "relations-down:task-1" && read.DependencyHint);
        Assert.Contains(reads, read => read.Id == "inventory" && read.Reason == "confirmation" && read.DependencyHint);
        Assert.Null(source.PendingAge(["inventory"]));
        Assert.DoesNotContain(reads, read => read.Reason == "audit");
        Assert.Contains(reads, read => IsDependencyHintInventoryRead(read.Id, read.Reason, read.DependencyHint));
    }

    private static bool IsDependencyHintInventoryRead(string id, string reason, bool dependencyHint)
        // A hint arriving after the core read can be consumed by the Workspace's
        // authoritative inventory confirmation before another core turn starts.
        => id == "inventory" && reason is "dirty" or "confirmation" && dependencyHint;

    [Fact]
    public async Task ScheduledWorkspaceRecoversFromColdSourceTimeoutsWithinTheNativeSmokeDeadline()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var native = new Runner { TargetPath = target.Path };
        var faults = new HashSet<string>(StringComparer.Ordinal);
        // The scripted producer has no supported version identity. Keep the
        // scheduled fallback boundary: detail, both relations and confirmation.
        var inventories = 0;
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "git") clock.Advance(TimeSpan.FromSeconds(.1));
            else
            {
                var source = request.Arguments[2];
                if (source == "dep") source += ":" + request.Arguments[^2];
                if (source == "list") source += ":" + ++inventories;
                var fail = (source is "show" or "dep:up" or "dep:down" or "list:2" or "list:3") && faults.Add(source);
                clock.Advance(TimeSpan.FromSeconds(fail ? 2 : 1.6));
                if (fail)
                {
                    throw new ToolExecutionException("Cold source timed out after 2s");
                }
            }
            return native.RunAsync(request, token);
        });
        using var factory = Factory(target.Path, runner, clock);
        var sourceAdapter = Adapter(factory);
        var started = clock.GetUtcNow();
        ObservationSample? completed = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
        {
            var samples = await sourceAdapter.ReadScheduledAsync(Keys("workspace:task-1"), (_, _) => { }, TestContext.Current.CancellationToken);
            if (samples.TryGetValue("workspace:task-1", out var sample) && sample.Data is not null && !sample.Pending && sample.Error is null)
            { completed = sample; break; }
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }
        Assert.NotNull(completed);
        Assert.Equal(5, faults.Count);
        var workspace = Assert.IsType<BeadWorkspaceResponse>(completed.Data);
        Assert.Equal("complete", workspace.RelationObservation.Incoming);
        Assert.Equal("complete", workspace.RelationObservation.Outgoing);
        Assert.All(completed.Sources.Values, state => Assert.Null(state.Error));
        Assert.Null(sourceAdapter.PendingAge(completed.Sources.Keys));
    }

    [Fact]
    public async Task NativeBdSmokeDetectsExternalCommentsAndDependenciesAndReportsCost()
    {
        using var target = new BeadsFixture("operational observations native");
        var bead = target.Create("Observed task", labels: ["product-decision-required"]);
        var blocker = target.Create("Blocker");
        var incoming = target.Create("Closed incoming owner", status: "closed");
        using var wispRow = JsonDocument.Parse(target.Run("bd", ["create", "Ephemeral incoming owner", "--ephemeral", "--json"]));
        var wisp = wispRow.RootElement.GetProperty("id").GetString()!;
        target.Run("bd", ["dep", "add", incoming, bead, "--type", "related"]);
        target.Run("bd", ["dep", "add", wisp, bead, "--type", "blocks"]);
        var measured = new ConcurrentQueue<string>();
        var timeline = Stopwatch.StartNew();
        var commandSequence = 0;
        void Trace(string message)
        {
            // Bound diagnostic retention even if a future retry loop regresses.
            if (measured.Count < 512) measured.Enqueue($"[native-observer] +{timeline.Elapsed.TotalMilliseconds:F1}ms {message}");
        }
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            var sequence = Interlocked.Increment(ref commandSequence);
            // Command class only: no arguments, output, environment or paths.
            var command = request.FileName == "git" ? "git"
                : request.Arguments.Contains("dep") ? "dep-" + request.Arguments[^2]
                : request.Arguments.FirstOrDefault(argument => argument is "list" or "show" or "comments" or "branch" or "config") ?? "other";
            var actual = new SystemProcessRunner { PhaseObserved = phase => Trace($"command {sequence} {command}: {phase}") };
            var commandTimer = Stopwatch.StartNew();
            var outcome = "incomplete";
            var environment = target.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            Trace($"command {sequence} {command}: requested; timeout {request.Timeout.TotalMilliseconds:F0}ms");
            try
            {
                var result = await actual.RunAsync(request with { Environment = environment }, token);
                outcome = "exit-" + result.ExitCode.ToString(CultureInfo.InvariantCulture);
                return result;
            }
            catch (Exception exception) { outcome = exception.GetType().Name; throw; }
            finally { Trace($"command {sequence} {command}: completed {outcome}; {commandTimer.Elapsed.TotalMilliseconds:F1}ms"); }
        });
        var journal = new ConcurrentQueue<ConsoleJournalPage>();
        using var factory = Factory(target.Path, runner, configure: services =>
        {
            services.RemoveAll<IConsoleJournalReader>();
            services.AddSingleton<IConsoleJournalReader>(provider => new RecordingJournalReader(
                ActivatorUtilities.CreateInstance<ConsoleJournalReader>(provider,
                    new BeadsJournalClient(runner) { PhaseObserved = phase => Trace("journal-native: " + phase) }), journal, Trace));
        });
        using var client = factory.CreateClient();
        var token = TestContext.Current.CancellationToken;
        var path = $"/api/operations/v1/projects/web-test/resources?key=workspace:{bead}";
        var source = Assert.IsType<OperationalObservationSource>(factory.Services.GetRequiredService<IOperationalObservationSource>());
        var reads = new ConcurrentQueue<(string Id, string Reason, bool CommentHint, bool DependencyHint)>();
        source.ReadStarted += (id, reason) =>
        {
            var sourceClass = id.Split(':')[0];
            Trace($"source {sourceClass}: {reason}; commands {source.CommandCount}; native {source.NativeMilliseconds:F1}ms");
            var hints = journal.SelectMany(page => page.Hints).Where(hint => hint.IssueId == bead).ToArray();
            reads.Enqueue((id, reason, hints.Any(hint => hint.Operation == "comment"), hints.Any(hint => hint.Operation == "dep_add")));
        };
        async Task<JsonElement> ReadObservedAsync(Func<JsonElement, bool> condition)
        {
            try { return await UntilAsync(client, path, condition, token); }
            catch (TimeoutException)
            {
                foreach (var measurement in measured) TestContext.Current.TestOutputHelper?.WriteLine(measurement);
                throw;
            }
        }
        var cold = Stopwatch.StartNew();
        Trace("phase cold-workspace");
        var first = await ReadObservedAsync(root => root.GetProperty("data").ValueKind == JsonValueKind.Object
            && root.GetProperty("synchronization").GetProperty("journal").GetString() == "current"
            && !root.GetProperty("synchronization").GetProperty("pending").GetBoolean());
        var beforeMutation = journal.Count;
        using (var nativeUp = JsonDocument.Parse(target.Run("bd", BeadsCommand.CreateUpDependencies(target.Path, bead).Arguments)))
        {
            var expected = BeadsReader.ParseUpDependencies(nativeUp.RootElement, bead)
                .Select(edge => (edge.IssueId, edge.Type)).Order().ToArray();
            var actual = first.GetProperty("data").GetProperty("relations").EnumerateArray()
                .Where(relation => relation.GetProperty("direction").GetString() == "incoming")
                .Select(relation => (relation.GetProperty("id").GetString()!, relation.GetProperty("type").GetString()!)).Order().ToArray();
            Assert.Equal(expected, actual);
            Assert.Contains((incoming, "related"), actual);
            Assert.Contains((wisp, "blocks"), actual);
        }
        var readsBeforeMutation = reads.Count;
        var commandsBeforeMutation = Volatile.Read(ref commandSequence);
        var coldSeconds = cold.Elapsed.TotalSeconds;
        var count = first.GetProperty("data").GetProperty("comments").GetArrayLength();
        var revision = first.GetProperty("revision").GetString();
        // The fixture's default environment is for read-only probes. Model the
        // production write profile explicitly while retaining external overlap.
        var mutation = Stopwatch.StartNew();
        Trace("phase external-mutations");
        target.Run("bd", ["--dolt-auto-commit", "on", "comments", "add", bead, "External combined observer comment marker"]);
        measured.Enqueue($"external comment write (auto-commit on): {mutation.Elapsed.TotalMilliseconds:F1}ms");
        mutation.Restart();
        target.Run("bd", ["--dolt-auto-commit", "on", "dep", "add", bead, blocker]);
        measured.Enqueue($"external dependency write (auto-commit on): {mutation.Elapsed.TotalMilliseconds:F1}ms");
        var changed = Stopwatch.StartNew();
        Trace("phase combined-visibility");
        const string commentMarker = "External combined observer comment marker";
        var combined = await ReadObservedAsync(root =>
        {
            if (root.GetProperty("data").ValueKind != JsonValueKind.Object
                || root.GetProperty("revision").GetString() == revision)
                return false;

            var data = root.GetProperty("data");
            var comments = data.GetProperty("comments");
            var relations = data.GetProperty("relations");
            return comments.GetArrayLength() == count + 1
                && comments.EnumerateArray().Any(comment => comment.GetProperty("body").GetString() == commentMarker)
                && relations.EnumerateArray().Any(relation =>
                    relation.GetProperty("id").GetString() == blocker
                    && relation.GetProperty("type").GetString() == "blocks"
                    && relation.GetProperty("direction").GetString() == "outgoing");
        });
        var combinedSeconds = changed.Elapsed.TotalSeconds;
        var mutationReads = reads.Skip(readsBeforeMutation).ToArray();
        // Keep diagnostics even if a causal assertion fails on a slower host.
        foreach (var measurement in measured) TestContext.Current.TestOutputHelper?.WriteLine(measurement);
        foreach (var read in mutationReads) TestContext.Current.TestOutputHelper?.WriteLine($"Source {read.Id}: {read.Reason}; comment hint {read.CommentHint}; dependency hint {read.DependencyHint}");
        Assert.Contains(journal.Skip(beforeMutation).SelectMany(page => page.Hints), hint => hint.IssueId == bead && hint.Operation == "comment");
        Assert.Contains(journal.Skip(beforeMutation).SelectMany(page => page.Hints), hint => hint.IssueId == bead && hint.Operation == "dep_add");
        AssertJournalCausedReads(mutationReads, bead);
        var nativeCommands = Volatile.Read(ref commandSequence);
        TestContext.Current.TestOutputHelper?.WriteLine($"Journal pages {journal.Count} ({journal.Count - beforeMutation} after baseline); source reads {reads.Count} ({mutationReads.Length} after baseline); native commands {nativeCommands} ({nativeCommands - commandsBeforeMutation} after baseline); timeline records {measured.Count}");
        Assert.NotEqual(revision, combined.GetProperty("revision").GetString());
        var combinedData = combined.GetProperty("data");
        Assert.Equal(count + 1, combinedData.GetProperty("comments").GetArrayLength());
        Assert.Contains(combinedData.GetProperty("comments").EnumerateArray(),
            comment => comment.GetProperty("body").GetString() == commentMarker);
        Assert.Contains(combinedData.GetProperty("relations").EnumerateArray(), relation =>
            relation.GetProperty("id").GetString() == blocker
            && relation.GetProperty("type").GetString() == "blocks"
            && relation.GetProperty("direction").GetString() == "outgoing");
        TestContext.Current.TestOutputHelper?.WriteLine($"Native observer: cold Workspace {coldSeconds:F3}s; combined comments+dependency visibility {combinedSeconds:F3}s; commands {source.CommandCount}; native ms {source.NativeMilliseconds:F1}");
        // Performance targets are measured evidence; parallel-suite load can exceed them.
        // UntilAsync still enforces the functional observation bound.
    }

    private sealed class RecordingJournalReader(IConsoleJournalReader inner, ConcurrentQueue<ConsoleJournalPage> pages,
        Action<string>? trace = null) : IConsoleJournalReader
    {
        public async Task<ConsoleJournalPage> ReadAsync(long cursor, CancellationToken token)
        {
            trace?.Invoke("journal-page: requested");
            try
            {
                var page = await inner.ReadAsync(cursor, token);
                pages.Enqueue(page);
                trace?.Invoke($"journal-page: completed; hints {page.Hints.Count}");
                return page;
            }
            catch (Exception exception) { trace?.Invoke("journal-page: " + exception.GetType().Name); throw; }
        }
    }

    private static async Task<JsonElement> UntilAsync(HttpClient client, string path, Func<JsonElement, bool> predicate, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        JsonElement last = default;
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync(path, token));
            last = json.RootElement.Clone();
            if (predicate(last)) return last;
            await Task.Delay(50, token);
        }
        throw new TimeoutException("Observer did not publish the expected native state. Last envelope: " + last.GetRawText());
    }

    [Fact]
    public async Task WaitingForMutationAccessDoesNotConsumeTheNativeWorkBudget()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        using var factory = Factory(target.Path, new Runner(), clock);
        var source = Adapter(factory);
        var gate = factory.Services.GetRequiredService<TrackerBridge>().ObservationGate;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var collection = source.ReadTrackerAsync(Keys("workspace:task-1"), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        gate.Release();
        var samples = await collection;
        Assert.False(samples["workspace:task-1"].Pending);
        Assert.IsType<BeadWorkspaceResponse>(samples["workspace:task-1"].Data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("task-3")]
    public async Task SlowNativeValidationAllowsExternalAttentionWithinTheBrowserDeadline(string? transientTimeout)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        // The recording wrapper hides Runner from Factory's direct-runner setup.
        var native = new Runner { TargetPath = target.Path, Rows = 5, FailCommentOnce = transientTimeout };
        var runner = new RecordingProcessRunner((request, token) =>
        {
            // Include Git validation, not just bd: both consume the native sweep.
            clock.Advance(TimeSpan.FromSeconds(1.6));
            return native.RunAsync(request, token);
        });
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var started = clock.GetUtcNow();
        ObservationSample? completed = null;
        while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(30))
        {
            var samples = await source.ReadTrackerAsync(Keys("attention"), TestContext.Current.CancellationToken);
            if (samples.TryGetValue("attention", out var sample) && sample.Data is not null)
            {
                completed = sample;
                break;
            }
        }
        Assert.Equal(5, Assert.IsType<AttentionDeskItem[]>(completed?.Data).Length);
        Assert.True(clock.GetUtcNow() - started < TimeSpan.FromSeconds(30));
        Assert.All(native.CommentIds.GroupBy(id => id), group => Assert.Equal(group.Key == transientTimeout ? 2 : 1, group.Count()));
    }

    [Fact]
    public async Task AttentionMakesProgressAlongsideAContinuouslySelectedWorkspace()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        using var factory = Factory(target.Path, new Runner { Rows = 5, Advance = () => clock.Advance(TimeSpan.FromSeconds(1.4)) }, clock);
        var source = Adapter(factory);
        ObservationSample? attention = null;
        for (var index = 0; index < 8; index++)
        {
            var samples = await source.ReadTrackerAsync(Keys("workspace:task-1", "attention"), TestContext.Current.CancellationToken);
            if (samples.TryGetValue("attention", out attention) && attention.Data is not null) break;
        }
        Assert.Equal(5, Assert.IsType<AttentionDeskItem[]>(attention?.Data).Length);
    }

    [Fact]
    public async Task PendingWorkspaceRelationFailureIsVisibleWithoutReplacingAcceptedProvenance()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var commandCost = TimeSpan.Zero;
        var runner = new Runner { TargetPath = target.Path };
        var timedRunner = new RecordingProcessRunner((request, cancellation) =>
        {
            if (request.FileName == "bd" && request.Arguments[2] != "comments") clock.Advance(commandCost);
            return runner.RunAsync(request, cancellation);
        });
        using var factory = Factory(target.Path, timedRunner, clock);
        var source = Adapter(factory);
        using var service = new OperationalObservationService("web-test", source, clock,
            factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        const string key = "workspace:task-1";
        using var pin = service.Pin([key]);
        var token = TestContext.Current.CancellationToken;
        await service.CollectOnceAsync(false, token);
        var accepted = service.Read(key);
        Assert.Equal("fresh", accepted.Health);
        Assert.NotNull(accepted.Data);

        clock.Advance(TimeSpan.FromSeconds(10));
        runner.Title = "Replacement title";
        runner.Comment = "Replacement comment";
        runner.UpdatedAt = "2026-01-01T00:01:00Z";
        runner.FailRelations = true;
        commandCost = TimeSpan.FromSeconds(1.4);
        source.Invalidate();
        var commandsBefore = runner.Commands.Count;
        await service.CollectOnceAsync(false, token);
        // Inventory + detail + failed incoming relations exhaust the native turn.
        // Confirmation/environment and the replacement body are still pending.
        Assert.Equal(["list", "comments", "show", "dep"], runner.Commands.Skip(commandsBefore));
        var pending = service.Read(key);
        Assert.Equal("source-failed", pending.Sources["relations-up:task-1"].Error?.Code);
        Assert.Equal("stale", pending.Sources["relations-up:task-1"].Health);
        Assert.Equal("source-failed", pending.Error?.Code);
        Assert.Equal("stale", pending.Health);
        Assert.NotEqual(accepted.Revision, pending.Revision);
        Assert.Equal(accepted.Data?.GetRawText(), pending.Data?.GetRawText());
        Assert.Equal(accepted.ObservedAt, pending.ObservedAt);
        Assert.Equal(14200d, pending.ValidatedAgeMs);
        foreach (var (id, previous) in accepted.Sources)
        {
            var retained = pending.Sources[id];
            Assert.Same(previous.ContentIdentity, retained.ContentIdentity);
            Assert.Equal(previous.ObservedAt, retained.ObservedAt);
            Assert.Equal(previous.ValidatedTimestamp, retained.ValidatedTimestamp);
            Assert.Equal(previous.SourceGeneration, retained.SourceGeneration);
        }
        Assert.True(pending.Sources["relations-up:task-1"].LastAttemptAt > accepted.Sources["relations-up:task-1"].LastAttemptAt);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("source-failed", service.Read(key).Error?.Code);
        Assert.Equal(20200d, service.Read(key).ValidatedAgeMs);

        runner.FailRelations = false;
        commandCost = TimeSpan.Zero;
        source.Invalidate();
        for (var turn = 0; turn < 4 && service.Read(key).Health != "fresh"; turn++)
            await service.CollectOnceAsync(false, token);
        var recovered = service.Read(key);
        Assert.Equal("fresh", recovered.Health);
        Assert.Null(recovered.Error);
        Assert.All(recovered.Sources.Values, state => Assert.Null(state.Error));
        Assert.Contains("Replacement title", recovered.Data?.GetRawText());
        Assert.Equal(0d, recovered.ValidatedAgeMs);
    }

    [Fact]
    public async Task PartialRelationsRemainExplicitAndSlowCoreYieldsToWorkspaceSubsteps()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Advance = () => clock.Advance(TimeSpan.FromSeconds(1.4)), FailRelations = true };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        var keys = Keys("workspace:task-1");
        ObservationSample? completed = null;
        for (var index = 0; index < 8; index++)
        {
            var samples = await source.ReadTrackerAsync(keys, TestContext.Current.CancellationToken);
            if (samples.TryGetValue("workspace:task-1", out var sample) && !sample.Pending) { completed = sample; break; }
        }
        Assert.NotNull(completed);
        Assert.True(completed.Partial);
        Assert.Equal("source-failed", completed.Error?.Code);
        var workspace = Assert.IsType<BeadWorkspaceResponse>(completed.Data);
        Assert.Equal("unavailable", workspace.RelationObservation.Incoming);
        Assert.Equal("unavailable", workspace.RelationObservation.Outgoing);
        Assert.Contains("comments", runner.Commands);
        Assert.Contains("show", runner.Commands);
    }

    [Theory]
    [InlineData("workspace:task-1")]
    [InlineData("digest:all")]
    [InlineData("explorer:all:")]
    public async Task UnselectedMembershipsDoNotConsumeTheObservationBudget(string key)
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner { Advance = () => clock.Advance(TimeSpan.FromSeconds(1.4)) };
        using var factory = Factory(target.Path, runner, clock);
        var source = Adapter(factory);
        await source.ReadTrackerAsync(Keys(key), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("ready", runner.Commands);
        Assert.DoesNotContain("blocked", runner.Commands);
        if (key.StartsWith("workspace:", StringComparison.Ordinal))
            Assert.Contains("show", runner.Commands);
    }

    private static OperationalObservationSource Adapter(NightweaverWebApplicationFactory factory, ObservationMemoryBudget? memory = null)
        => ActivatorUtilities.CreateInstance<OperationalObservationSource>(factory.Services, memory ?? new ObservationMemoryBudget());

    private sealed class CostClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.UtcTicks;
        internal void Advance(TimeSpan delta) => now += delta;
    }

    private static OperationKey[] Keys(params string[] keys) => keys.Select(OperationKey.Parse).ToArray();
    private static NightweaverWebApplicationFactory Factory(string target, IProcessRunner runner, TimeProvider? clock = null, Action<IServiceCollection>? configure = null)
    {
        if (runner is Runner scripted) scripted.TargetPath = target;
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton(runner);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
            configure?.Invoke(services);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new { name = "web-test", target_repository = new { path = target } }));
        return factory;
    }

    // These component cases already script bd. Keep the filesystem/backend guards
    // real, but do not launch Git merely to provide the same valid root identity.
    // The native observer smoke above retains actual Git/bd and HTTP observation.
    private sealed class ObservationTargetFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal string Path => temp.Path;
        internal ObservationTargetFixture()
        {
            temp.CreateDirectory(".git");
            temp.CreateDirectory(".beads/embeddeddolt");
            temp.WriteFile(".beads/metadata.json", "{\"backend\":\"dolt\",\"dolt_mode\":\"embedded\"}");
            temp.WriteFile(".beads.gate.lock", "");
            temp.WriteFile(".beads/embeddeddolt.gate.lock", "");
        }
        public void Dispose() => temp.Dispose();
    }

    private sealed class Runner : IProcessRunner
    {
        private static readonly string[] RootArguments = ["rev-parse", "--is-inside-work-tree", "--show-prefix", "--path-format=absolute", "--git-dir", "--git-common-dir"];
        internal string? TargetPath { get; set; }
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal int RepositoryValidations { get; private set; }
        internal ConcurrentQueue<string> CommentIds { get; } = new();
        internal string? FailCommentOnce { get; set; }
        internal int CommentTimeoutsRemaining { get; set; } = 1;
        internal int FailListAt { get; init; }
        internal bool FailEveryList { get; set; }
        internal Action? Advance { get; init; }
        internal bool FailRelations { get; set; }
        internal bool OutgoingDependency { get; set; }
        internal bool Ready { get; set; } = true;
        internal bool Blocked { get; set; }
        internal string Title { get; set; } = "Original title";
        internal int Priority { get; set; } = 1;
        internal bool FailReady { get; set; }
        internal bool Conflict { get; init; }
        internal int Rows { get; set; } = 1;
        internal bool Closed { get; set; }
        internal Dictionary<string, BeadsCommentSnapshot[]> HistoryOverrides { get; } = new(StringComparer.Ordinal);
        internal string UpdatedAt { get; set; } = "2026-01-01T00:00:00Z";
        internal string Comment { get; set; } = "Initial comment";
        internal Dictionary<string, string> CommentOverrides { get; } = new(StringComparer.Ordinal);
        private int inventories;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName == "git")
            {
                Assert.NotNull(TargetPath);
                if (request.Arguments.SequenceEqual(["check-ref-format", "--branch", "main"]))
                {
                    Assert.Equal(Path.GetTempPath(), request.WorkingDirectory);
                    return Task.FromResult(new ProcessResult(0, "main\n", ""));
                }
                Assert.Equal(TargetPath, request.WorkingDirectory);
                Assert.Equal(RootArguments, request.Arguments);
                RepositoryValidations++;
                var common = Path.Combine(TargetPath, ".git");
                return Task.FromResult(new ProcessResult(0, $"true\n\n{common}\n{common}\n", ""));
            }
            Assert.Equal("bd", request.FileName);
            Assert.Equal(TimeSpan.FromSeconds(2), request.Timeout);
            Assert.NotNull(request.MaximumOutputBytes);
            Advance?.Invoke();
            var args = request.Arguments;
            var command = args[2];
            Commands.Enqueue(command);
            if (command == "list" && (FailEveryList || inventories + 1 == FailListAt))
            {
                inventories++;
                throw new ToolExecutionException("bd list timed out after 2s");
            }
            if (command == "comments")
            {
                CommentIds.Enqueue(args[3]);
                if (args[3] == FailCommentOnce)
                {
                    if (--CommentTimeoutsRemaining <= 0) FailCommentOnce = null;
                    throw new ToolExecutionException("bd comments timed out after 2s");
                }
            }
            if (command == "dep" && FailRelations) throw new ToolExecutionException("Relations unavailable");
            if (command == "ready" && FailReady) throw new ToolExecutionException("bd timed out after 2s", "secret stderr");
            var title = Conflict && inventories > 0 ? "Concurrent edit" : Title;
            var rows = Enumerable.Range(1, Rows).Select(index => new
            {
                id = $"task-{index}", title, issue_type = "task", priority = Conflict && inventories > 0 ? 2 : Priority, status = Closed ? "closed" : "open", assignee = (string?)null,
                labels = Closed ? [] : (string[])["product-decision-required"], created_at = "2026-01-01T00:00:00Z", updated_at = UpdatedAt,
                description = "Description"
            }).ToArray();
            if (command == "list") inventories++;
            var json = command switch
            {
                "blocked" => Blocked ? "[{\"id\":\"task-1\",\"blocked_by_count\":1,\"blocked_by\":[\"task-2\"]}]" : "[]",
                "dep" => OutgoingDependency && args.Contains("down")
                    ? "[{\"issue_id\":\"task-1\",\"depends_on_id\":\"task-2\",\"type\":\"blocks\"}]" : "[]",
                "ready" => Ready ? "[{\"id\":\"task-1\"}]" : "[]",
                "show" => JsonSerializer.Serialize(rows.Where(row => row.id == args[3])),
                "comments" when HistoryOverrides.TryGetValue(args[3], out var history) => JsonSerializer.Serialize(history.Select(c => new { id = c.Id, issue_id = c.IssueId, author = c.Author, text = c.Text, created_at = c.CreatedAt })),
                "comments" => JsonSerializer.Serialize(new[] { new { id = "comment-1", issue_id = args[3], author = "human", text = CommentOverrides.GetValueOrDefault(args[3], Comment), created_at = "2026-01-01T00:00:00Z" } }),
                _ => JsonSerializer.Serialize(rows)
            };
            return Task.FromResult(new ProcessResult(0, json, ""));
        }
    }
}
