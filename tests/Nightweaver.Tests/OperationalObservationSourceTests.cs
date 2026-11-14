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

namespace Nightweaver.Tests;

public sealed class OperationalObservationSourceTests
{
    [Fact]
    public async Task SharedInventoryProjectsAllViewsAndSelectedCommentsAreSharedWithAttention()
    {
        using var target = new BeadsApplicationFixture();
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
        using var target = new BeadsApplicationFixture();
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
        using var target = new BeadsApplicationFixture();
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
        using var target = new BeadsApplicationFixture();
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
        using var target = new BeadsApplicationFixture();
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

    [Fact]
    public async Task InactiveAttentionReleasesAnInterruptedCohortsHistoryReservations()
    {
        using var target = new BeadsApplicationFixture();
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
        using var target = new BeadsApplicationFixture();
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
        using var target = new BeadsApplicationFixture();
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

    [Fact]
    public async Task NativeBdSmokeDetectsExternalCommentsAndDependenciesAndReportsCost()
    {
        using var target = new BeadsFixture("operational observations native");
        var bead = target.Create("Observed task", labels: ["product-decision-required"]);
        var blocker = target.Create("Blocker");
        var actual = new SystemProcessRunner();
        var measured = new ConcurrentQueue<string>();
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            var commandTimer = Stopwatch.StartNew();
            var environment = target.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            try { return await actual.RunAsync(request with { Environment = environment }, token); }
            finally { measured.Enqueue($"{request.FileName} {string.Join(' ', request.Arguments)}: {commandTimer.Elapsed.TotalMilliseconds:F1}ms"); }
        });
        using var factory = Factory(target.Path, runner);
        using var client = factory.CreateClient();
        var token = TestContext.Current.CancellationToken;
        var path = $"/api/operations/v1/projects/web-test/resources?key=workspace:{bead}";
        var cold = Stopwatch.StartNew();
        JsonElement first;
        try { first = await UntilAsync(client, path, root => root.GetProperty("data").ValueKind == JsonValueKind.Object, token); }
        catch (TimeoutException)
        {
            foreach (var measurement in measured) TestContext.Current.TestOutputHelper?.WriteLine(measurement);
            throw;
        }
        var coldSeconds = cold.Elapsed.TotalSeconds;
        var count = first.GetProperty("data").GetProperty("comments").GetArrayLength();
        var revision = first.GetProperty("revision").GetString();
        target.Run("bd", ["comments", "add", bead, "External comments-only observation marker"]);
        var changed = Stopwatch.StartNew();
        var comment = await UntilAsync(client, path, root => root.GetProperty("data").ValueKind == JsonValueKind.Object
            && root.GetProperty("data").GetProperty("comments").GetArrayLength() == count + 1, token);
        var commentSeconds = changed.Elapsed.TotalSeconds;
        Assert.NotEqual(revision, comment.GetProperty("revision").GetString());
        target.Run("bd", ["dep", "add", bead, blocker]);
        changed.Restart();
        await UntilAsync(client, path, root => root.GetProperty("data").ValueKind == JsonValueKind.Object
            && root.GetProperty("data").GetProperty("relations").GetArrayLength() > 0, token);
        var source = Assert.IsType<OperationalObservationSource>(factory.Services.GetRequiredService<IOperationalObservationSource>());
        TestContext.Current.TestOutputHelper?.WriteLine($"Native observer: cold Workspace {coldSeconds:F3}s; comments visibility {commentSeconds:F3}s; dependency visibility {changed.Elapsed.TotalSeconds:F3}s; commands {source.CommandCount}; native ms {source.NativeMilliseconds:F1}");
        // Performance targets are measured evidence; parallel-suite load can exceed them.
        // UntilAsync still enforces the functional observation bound.
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
        using var target = new BeadsApplicationFixture();
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

    [Fact]
    public async Task AttentionMakesProgressAlongsideAContinuouslySelectedWorkspace()
    {
        using var target = new BeadsApplicationFixture();
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
    public async Task PartialRelationsRemainExplicitAndSlowCoreYieldsToWorkspaceSubsteps()
    {
        using var target = new BeadsApplicationFixture();
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
    private static NightweaverWebApplicationFactory Factory(string target, IProcessRunner runner, TimeProvider? clock = null)
    {
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton(runner);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new { name = "web-test", target_repository = new { path = target } }));
        return factory;
    }

    private sealed class Runner : IProcessRunner
    {
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal ConcurrentQueue<string> CommentIds { get; } = new();
        internal string? FailCommentOnce { get; set; }
        internal Action? Advance { get; init; }
        internal bool FailRelations { get; init; }
        internal bool Ready { get; set; } = true;
        internal bool FailReady { get; init; }
        internal bool Conflict { get; init; }
        internal int Rows { get; set; } = 1;
        internal string Comment { get; set; } = "Initial comment";
        private int inventories;
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.FileName == "git") return await new SystemProcessRunner().RunAsync(request, cancellationToken);
            Assert.Equal(TimeSpan.FromSeconds(2), request.Timeout);
            Assert.NotNull(request.MaximumOutputBytes);
            Advance?.Invoke();
            var args = request.Arguments;
            var command = args[2];
            Commands.Enqueue(command);
            if (command == "comments")
            {
                CommentIds.Enqueue(args[3]);
                if (args[3] == FailCommentOnce)
                {
                    FailCommentOnce = null;
                    throw new ToolExecutionException("bd comments timed out after 2s");
                }
            }
            if (command == "dep" && FailRelations) throw new ToolExecutionException("Relations unavailable");
            if (command == "ready" && FailReady) throw new ToolExecutionException("bd timed out after 2s", "secret stderr");
            var title = Conflict && inventories > 0 ? "Concurrent edit" : "Original title";
            object[] rows = Enumerable.Range(1, Rows).Select(index => (object)new
            {
                id = $"task-{index}", title, issue_type = "task", priority = Conflict && inventories > 0 ? 2 : 1, status = "open", assignee = (string?)null,
                labels = (string[])["product-decision-required"], created_at = "2026-01-01T00:00:00Z", updated_at = "2026-01-01T00:00:00Z",
                description = "Description"
            }).ToArray();
            if (command == "list") inventories++;
            var json = command switch
            {
                "blocked" or "dep" => "[]",
                "ready" => Ready ? "[{\"id\":\"task-1\"}]" : "[]",
                "show" => JsonSerializer.Serialize(rows.Take(1)),
                "comments" => JsonSerializer.Serialize(new[] { new { id = "comment-1", issue_id = args[3], author = "human", text = Comment, created_at = "2026-01-01T00:00:00Z" } }),
                _ => JsonSerializer.Serialize(rows)
            };
            return new(0, json, "");
        }
    }
}
