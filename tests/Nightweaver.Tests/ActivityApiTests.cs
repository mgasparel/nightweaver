using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nightweaver.ActivityFeed;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class ActivityApiTests
{
    private static readonly ActivitySelection All = new([], [], [], null, null);
    private static readonly Guid Journal = Guid.NewGuid(), Runtime = Guid.NewGuid();
    private static readonly GitHubRepositoryIdentity Repository = new("github.com", "owner", "repo");
    private static ActivityRecord Row(long offset, string bead = "ns-a", Guid? runtime = null, ActivityDetail? detail = null) =>
        ActivityProjector.Project(new(Journal, offset, runtime ?? Runtime, offset), DateTimeOffset.UtcNow,
            new(ActivityKind.HealthChanged, ActivitySource.Health, ActivityPhase.Runtime, Guid.NewGuid(), "health")
            { Bead = bead, Invocation = "invocation-1", Detail = detail, Freshness = ActivityFreshness.Fresh }, Repository)!;
    private static ActivityCursor Cursor(long offset) => new(1, Journal, offset);

    private sealed class Source : IActivityReadSource
    {
        internal ActivityPage Page = new([], Cursor(0), null, null, ActivityPageState.Ready, 0);
        internal int Reads;
        internal bool Fail;
        internal FactoryTelemetryReadResult RuntimeResult = new(FactoryTelemetryAvailability.Unavailable, null, null);
        public Task<ActivityPage> ReadAsync(ActivityCursor? cursor, CancellationToken cancellationToken)
        {
            Reads++;
            if (Fail) throw new IOException("SECRET_SENTINEL");
            return Task.FromResult(Page);
        }
        public FactoryTelemetryReadResult ReadRuntime() => RuntimeResult;
    }

    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        private readonly List<Alarm> alarms = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var alarm = new Alarm(callback, state, dueTime);
            alarms.Add(alarm); return alarm;
        }
        internal void Advance(TimeSpan time)
        {
            Ticks += time.Ticks;
            foreach (var alarm in alarms.ToArray()) alarm.Fire(time);
        }
        private sealed class Alarm(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = dueTime; return !disposed; }
            internal void Fire(TimeSpan delta)
            {
                due -= delta;
                if (!disposed && due <= TimeSpan.Zero) { disposed = true; callback(state); }
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task SharedReadsPreservePaginationFiltersAndOrderWithoutSubscriberWork()
    {
        var source = new Source { Page = new(Enumerable.Range(1, 100).Select(i => Row(i)).ToArray(), Cursor(100), 1, 200, ActivityPageState.Ready, 200000) };
        using var service = new ActivityReadService(source, TimeProvider.System, new(), "test");
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 50; i++) Assert.Equal(100, service.Read(null, All).Records.Count);
        Assert.Equal(1, source.Reads);
        Assert.True(service.Read(null, All).Loading);
        var filtered = service.Read(null, All with { Bead = "ns-other" });
        Assert.Empty(filtered.Records);
        Assert.Equal(ActivityEndpoints.FormatCursor(Cursor(100)), filtered.Cursor);
        source.Page = new(Enumerable.Range(101, 100).Select(i => Row(i)).ToArray(), Cursor(200), 1, 200, ActivityPageState.Ready, 200000);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var first = service.Read(null, All);
        Assert.Equal(100, first.Records.Count);
        var second = service.Read(Cursor(100), All);
        Assert.Equal(Enumerable.Range(101, 100).Select(i => (long)i), second.Records.Select(r => r.Position.Offset));
        Assert.Equal(1, second.Oldest); Assert.Equal(200, second.Newest);
        Assert.Empty(service.Read(Cursor(200), All).Records);
        Assert.Throws<ArgumentException>(() => service.Read(Cursor(201), All));
    }

    [Fact]
    public async Task MissingHistoryDiffersFromEmptyAndFailedReadsKeepFactsWithUnavailableHealth()
    {
        var source = new Source(); var clock = new Clock();
        using var service = new ActivityReadService(source, clock, new(), "test");
        Assert.Equal(ActivityFreshness.Unknown, service.Read(null, All).JournalHealth);
        source.Page = source.Page with { State = ActivityPageState.Unknown, Continuation = null };
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ActivityFreshness.Unavailable, service.Read(null, All).JournalHealth);
        source.Page = new([], Cursor(0), null, null, ActivityPageState.Ready, 0);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ActivityFreshness.Fresh, service.Read(null, All).JournalHealth);
        Assert.Throws<ArgumentException>(() => service.Read(Cursor(1), All));
        source.Page = new([Row(1)], Cursor(1), 1, 1, ActivityPageState.Ready, 4096);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(ActivityFreshness.Stale, service.Read(null, All).JournalHealth);
        source.Fail = true;
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var failed = service.Read(null, All);
        Assert.Single(failed.Records);
        Assert.Equal(ActivityFreshness.Unavailable, failed.JournalHealth);
        Assert.DoesNotContain("SECRET_SENTINEL", JsonSerializer.Serialize(failed, ActivityEndpoints.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredForeignAndCorruptHistoryShowGapsBeforeContinuing()
    {
        var source = new Source { Page = new([Row(20), Row(21)], Cursor(21), 20, 21, ActivityPageState.Gap | ActivityPageState.Truncated, 8192) };
        using var service = new ActivityReadService(source, TimeProvider.System, new(), "test");
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        foreach (var cursor in new[] { Cursor(1), new(1, Guid.NewGuid(), 999) })
        {
            var page = service.Read(cursor, All);
            Assert.True(page.State.HasFlag(ActivityPageState.Expired));
            Assert.True(page.State.HasFlag(ActivityPageState.Gap));
            Assert.Equal(20, page.Records[0].Position.Offset);
        }
        source.Page = new([], Cursor(21), 20, 21, ActivityPageState.Ready, 100);
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(service.Read(Cursor(20), All).State.HasFlag(ActivityPageState.Truncated));
    }

    private static FactoryTelemetryReadResult Live(Guid runtime) => new(FactoryTelemetryAvailability.Live, null,
        new(2, "test", "target", runtime.ToString("N"), 1, DateTimeOffset.UtcNow, "live", new(1, []), new(1, [])));

    [Fact]
    public async Task RuntimeReplacementAndExpiredInvocationStayIndependentOfJournalFacts()
    {
        var source = new Source { Page = new([Row(1, detail: ActivityDetail.Invocation), Row(2, detail: ActivityDetail.Bead)], Cursor(2), 1, 2, ActivityPageState.Ready, 8192), RuntimeResult = Live(Guid.NewGuid()) };
        var clock = new Clock();
        using var service = new ActivityReadService(source, clock, new(), "test");
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var page = service.Read(Cursor(1), All);
        Assert.True(page.RuntimeReplaced);
        Assert.Equal(FactoryTelemetryAvailability.Live, page.RuntimeHealth);
        Assert.Equal(ActivityFreshness.Fresh, page.JournalHealth);
        var details = service.Read(null, All).Details;
        Assert.Equal("unavailable", details[0].Availability); Assert.Null(details[0].Invocation);
        Assert.Equal("/projects/test/beads/ns-a", details[1].Workspace);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(FactoryTelemetryAvailability.Stale, service.Read(null, All).RuntimeHealth);
        Assert.Equal(ActivityFreshness.Fresh, service.Read(null, All).JournalHealth);
    }

    [Fact]
    public async Task JournalFailureDoesNotHideLiveRuntimeAndCurrentInvocationUsesExistingAccessRoute()
    {
        var live = Live(Runtime);
        var invocation = new FactoryInvocationObservation("ns-a", "invocation-1", "implementer", "codex", null,
            "high", "running", null, null, null, null);
        var slot = new FactorySlotObservation("delivery-1", "running", "ns-a", null, null, null, null, null, null, invocation, null);
        var source = new Source
        {
            RuntimeResult = live with { Snapshot = live.Snapshot! with { Delivery = new(1, [slot]) } },
            Page = new([Row(1, detail: ActivityDetail.Invocation)], Cursor(1), 1, 1, ActivityPageState.Ready, 4096)
        };
        using var service = new ActivityReadService(source, TimeProvider.System, new(), "test");
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("/api/telemetry/stream?runtime_id=", service.Read(null, All).Details[0].Invocation, StringComparison.Ordinal);
        source.Fail = true;
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var page = service.Read(null, All);
        Assert.Equal(FactoryTelemetryAvailability.Live, page.RuntimeHealth);
        Assert.Equal(ActivityFreshness.Unavailable, page.JournalHealth);
        Assert.Single(page.Records);
    }

    [Fact]
    public async Task CacheBudgetPressureReportsLossAndReleasesMemoryOnDisposal()
    {
        var budget = new ObservationMemoryBudget(); var owner = new object();
        Assert.True(budget.TryReplace([], [(owner, ObservationMemoryBudget.MaximumBytes - 5000)]));
        var source = new Source { Page = new([Row(1), Row(2)], Cursor(2), 1, 2, ActivityPageState.Ready, 8192) };
        using (var service = new ActivityReadService(source, TimeProvider.System, budget, "test"))
        {
            await service.RefreshAsync(TestContext.Current.CancellationToken);
            var page = service.Read(Cursor(0), All);
            Assert.Single(page.Records);
            Assert.True(page.State.HasFlag(ActivityPageState.Gap));
            Assert.True(page.State.HasFlag(ActivityPageState.Expired));
            Assert.InRange(budget.Used, 0, ObservationMemoryBudget.MaximumBytes);
        }
        budget.Release(owner); Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void ValidEnumSetsAndLevelFiltersUseClosedValues()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?kind=HealthChanged,RecordsLost&phase=Runtime&strength=Observed&bead=ns-a&level=work");
        Assert.True(ActivityEndpoints.TryRequest(context.Request, false, out _, out var selection));
        Assert.True(selection!.Matches(Row(1)));
        Assert.False(selection.Matches(Row(1, "ns-b")));
        Assert.False((selection with { Level = "error" }).Matches(Row(1)));
    }

    [Fact]
    public async Task FileSourceUsesOneBoundedOriginReadAndDoesNotCreateMissingHistory()
    {
        using var directory = new TempDirectory(); directory.CreateDirectory("target");
        var paths = new HostPaths(directory.CreateDirectory("host"));
        var project = ProjectConfigLoader.Load(directory.WriteFile("project.json", """
            {"name":"test","target_repository":{"path":"target"}}
            """), null, paths);
        var runner = new RecordingProcessRunner((request, _) =>
        {
            Assert.Equal("git", request.FileName);
            Assert.Equal("config --get remote.origin.url", string.Join(' ', request.Arguments));
            Assert.Equal(ActivityLimits.Refresh, request.Timeout);
            Assert.Equal(2048, request.MaximumOutputBytes);
            return Task.FromResult(new ProcessResult(0, "https://github.com/owner/repo.git\n", ""));
        });
        using var source = new ActivityReadSource(paths, project, runner, new(paths, project));
        for (var i = 0; i < 3; i++)
        {
            var page = await source.ReadAsync(null, TestContext.Current.CancellationToken);
            Assert.True(page.State.HasFlag(ActivityPageState.Unknown));
            Assert.Empty(page.Records);
        }
        Assert.Single(runner.Requests);
        Assert.False(Directory.Exists(paths.ActivityRoot(project.Name)));
        Assert.Equal(FactoryTelemetryAvailability.Unavailable, source.ReadRuntime().Availability);
    }

    [Fact]
    public async Task LossRemainsVisibleWhenFiltersHideLossRecords()
    {
        var loss = ActivityProjector.Project(new(Journal, 1, Runtime, 1), DateTimeOffset.UtcNow,
            new(ActivityKind.RecordsLost, ActivitySource.Journal, ActivityPhase.Runtime, Guid.NewGuid(), "journal"), Repository)!;
        var source = new Source { Page = new([loss, Row(2)], Cursor(2), 1, 2, ActivityPageState.Ready, 8192) };
        using var service = new ActivityReadService(source, TimeProvider.System, new(), "test");
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var page = service.Read(null, All with { Kinds = [ActivityKind.HealthChanged] });
        Assert.Single(page.Records);
        Assert.True(page.State.HasFlag(ActivityPageState.Gap));
        Assert.Null(loss.Count);
    }

    [Theory]
    [InlineData("?kind=999")]
    [InlineData("?kind=HealthChanged,HealthChanged")]
    [InlineData("?phase=runtime")]
    [InlineData("?strength=Verified,999")]
    [InlineData("?bead=../secret")]
    [InlineData("?bead=%E2%80%AE")]
    [InlineData("?level=debug")]
    [InlineData("?cursor=1.bad.0")]
    [InlineData("?path=manifest.json")]
    [InlineData("?bead=ns-a&bead=ns-b")]
    public void MalformedQueriesAreRefusedBeforeSourceAccess(string query)
    {
        var context = new DefaultHttpContext(); context.Request.QueryString = new(query);
        Assert.False(ActivityEndpoints.TryRequest(context.Request, true, out _, out _));
    }

    [Fact]
    public void CursorAndRequestLimitsRejectAmbiguityAndOversizedText()
    {
        Assert.True(ActivityEndpoints.TryCursor(ActivityEndpoints.FormatCursor(Cursor(long.MaxValue)), out var cursor));
        Assert.Equal(long.MaxValue, cursor!.Offset);
        foreach (var bad in new[] { "2." + Journal.ToString("N") + ".1", "1." + Journal.ToString("N") + ".01", "1." + Journal.ToString("N") + ".-1" })
            Assert.False(ActivityEndpoints.TryCursor(bad, out _));
        var context = new DefaultHttpContext();
        context.Request.QueryString = new("?bead=" + new string('a', 1024));
        Assert.False(ActivityEndpoints.TryRequest(context.Request, false, out _, out _));
        context.Request.QueryString = new("?cursor=" + ActivityEndpoints.FormatCursor(Cursor(1)));
        context.Request.Headers["Last-Event-ID"] = ActivityEndpoints.FormatCursor(Cursor(2));
        Assert.False(ActivityEndpoints.TryRequest(context.Request, true, out _, out _));
    }

    private static async Task<WebApplication> App(ActivityReadService service)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        using var directory = new TempDirectory();
        directory.CreateDirectory("target");
        var paths = new HostPaths(directory.CreateDirectory("host"));
        var config = ProjectConfigLoader.Load(directory.WriteFile("project.json", """
            {"name":"test","target_repository":{"path":"target"}}
            """), null, paths);
        builder.Services.AddSingleton(config); builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(TimeProvider.System);
        var app = builder.Build(); app.MapActivity();
        await app.StartAsync(TestContext.Current.CancellationToken); return app;
    }

    [Fact]
    public async Task HttpAndSseShareStableIdentitiesAndResumeAfterDeliveredRecord()
    {
        var source = new Source { Page = new([Row(1), Row(2)], Cursor(2), 1, 2, ActivityPageState.Ready, 8192) };
        using var service = new ActivityReadService(source, TimeProvider.System, new(), "test");
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        await using var app = await App(service); using var client = app.GetTestClient();
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/activity/v1/projects/foreign/history", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity/v1/projects/test/history?kind=999", token)).StatusCode);
        using var history = JsonDocument.Parse(await client.GetStringAsync("/api/activity/v1/projects/test/history", token));
        Assert.Equal(2, history.RootElement.GetProperty("records").GetArrayLength());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/activity/v1/projects/test/events");
        request.Headers.Add("Last-Event-ID", ActivityEndpoints.FormatCursor(Cursor(1)));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var text = "";
        while (!text.Contains("event: cursor", StringComparison.Ordinal)) text += await reader.ReadLineAsync(token) + "\n";
        Assert.True(text.IndexOf("event: status", StringComparison.Ordinal) < text.IndexOf("event: activity", StringComparison.Ordinal));
        Assert.Contains("id: " + ActivityEndpoints.FormatCursor(Cursor(2)), text, StringComparison.Ordinal);
        Assert.DoesNotContain("id: " + ActivityEndpoints.FormatCursor(Cursor(1)), text, StringComparison.Ordinal);
        Assert.Contains("data: " + history.RootElement.GetProperty("records")[1].GetRawText(), text, StringComparison.Ordinal);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task StreamCapacityIsFiniteAndReleasedAfterDisconnect()
    {
        using var service = new ActivityReadService(new Source(), TimeProvider.System, new(), "test");
        await using var app = await App(service); using var client = app.GetTestClient();
        for (var i = 0; i < 16; i++) Assert.True(service.TryEnterStream());
        var response = await client.GetAsync("/api/activity/v1/projects/test/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter!.Delta);
        service.ExitStream(); Assert.True(service.TryEnterStream()); Assert.False(service.TryEnterStream());
        for (var i = 0; i < 16; i++) service.ExitStream();
    }

    [Fact]
    public async Task HttpCancellationReleasesStreamAdmission()
    {
        using var service = new ActivityReadService(new Source(), TimeProvider.System, new(), "test");
        await using var app = await App(service); using var client = app.GetTestClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var response = await client.GetAsync("/api/activity/v1/projects/test/events", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        cancellation.Cancel(); response.Dispose();
        // A subsequent request is ordered after server request cancellation settles.
        await app.StopAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 16; i++) Assert.True(service.TryEnterStream());
        Assert.False(service.TryEnterStream());
        for (var i = 0; i < 16; i++) service.ExitStream();
    }

    private sealed class HeldStream : Stream
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Released.Task; }
    }

    [Fact]
    public async Task StalledWriteEndsAtDeadlineEvenWhenResponseIgnoresCancellation()
    {
        var clock = new Clock(); using var held = new HeldStream(); var context = new DefaultHttpContext(); context.Response.Body = held;
        var write = ActivityEndpoints.WriteAsync(context, "event: status\ndata: {}\n\n", clock);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(ActivityLimits.WriteDeadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        held.Released.SetResult();
    }
}
