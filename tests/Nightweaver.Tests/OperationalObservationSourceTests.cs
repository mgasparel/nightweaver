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
        var displayed = Assert.IsType<CatchUpDigestResponse>(first["digest:24h"].Data);
        Assert.Equal(cutoff, displayed.GeneratedAt);
        Assert.True(clock.GetUtcNow() > cutoff.AddSeconds(1));
        Assert.Empty(displayed.StandaloneOpen);
        await factory.Services.GetRequiredService<CatchUpCheckpointStore>().WriteLastCaughtUpAtAsync(displayed.GeneratedAt, token);
        // An unseen write in the exact boundary second must remain unread, even
        // though the first inventory and its projection completed after that second.
        runner.UpdatedAt = cutoff.ToString("O", CultureInfo.InvariantCulture);
        var next = await source.ReadTrackerAsync(Keys("digest:since_caught_up"), token);
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
        Assert.Equal(confirmingInventory ? 4 : 3, runner.Commands.Count(command => command == "list"));
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
    public async Task SlowWorkspaceWithTransientHistoryTimeoutsPublishesWithinTheNativeSmokeDeadline()
    {
        using var target = new ObservationTargetFixture();
        var clock = new CostClock();
        var runner = new Runner
        {
            Advance = () => clock.Advance(TimeSpan.FromSeconds(1.6)),
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
        target.Run("bd", ["comments", "add", bead, "External combined observer comment marker"]);
        target.Run("bd", ["dep", "add", bead, blocker]);
        var changed = Stopwatch.StartNew();
        const string commentMarker = "External combined observer comment marker";
        var combined = await UntilAsync(client, path, root =>
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
        }, token);
        var combinedSeconds = changed.Elapsed.TotalSeconds;
        Assert.NotEqual(revision, combined.GetProperty("revision").GetString());
        var combinedData = combined.GetProperty("data");
        Assert.Equal(count + 1, combinedData.GetProperty("comments").GetArrayLength());
        Assert.Contains(combinedData.GetProperty("comments").EnumerateArray(),
            comment => comment.GetProperty("body").GetString() == commentMarker);
        Assert.Contains(combinedData.GetProperty("relations").EnumerateArray(), relation =>
            relation.GetProperty("id").GetString() == blocker
            && relation.GetProperty("type").GetString() == "blocks"
            && relation.GetProperty("direction").GetString() == "outgoing");
        var source = Assert.IsType<OperationalObservationSource>(factory.Services.GetRequiredService<IOperationalObservationSource>());
        TestContext.Current.TestOutputHelper?.WriteLine($"Native observer: cold Workspace {coldSeconds:F3}s; combined comments+dependency visibility {combinedSeconds:F3}s; commands {source.CommandCount}; native ms {source.NativeMilliseconds:F1}");
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
    private static NightweaverWebApplicationFactory Factory(string target, IProcessRunner runner, TimeProvider? clock = null)
    {
        if (runner is Runner scripted) scripted.TargetPath = target;
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton(runner);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
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
        internal ConcurrentQueue<string> CommentIds { get; } = new();
        internal string? FailCommentOnce { get; set; }
        internal int CommentTimeoutsRemaining { get; set; } = 1;
        internal int FailListAt { get; init; }
        internal Action? Advance { get; init; }
        internal bool FailRelations { get; init; }
        internal bool Ready { get; set; } = true;
        internal bool Blocked { get; set; }
        internal string Title { get; set; } = "Original title";
        internal int Priority { get; set; } = 1;
        internal bool FailReady { get; init; }
        internal bool Conflict { get; init; }
        internal int Rows { get; set; } = 1;
        internal string UpdatedAt { get; set; } = "2026-01-01T00:00:00Z";
        internal string Comment { get; set; } = "Initial comment";
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
            if (command == "list" && inventories + 1 == FailListAt)
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
            object[] rows = Enumerable.Range(1, Rows).Select(index => (object)new
            {
                id = $"task-{index}", title, issue_type = "task", priority = Conflict && inventories > 0 ? 2 : Priority, status = "open", assignee = (string?)null,
                labels = (string[])["product-decision-required"], created_at = "2026-01-01T00:00:00Z", updated_at = UpdatedAt,
                description = "Description"
            }).ToArray();
            if (command == "list") inventories++;
            var json = command switch
            {
                "blocked" => Blocked ? "[{\"id\":\"task-1\",\"blocked_by_count\":1,\"blocked_by\":[\"task-2\"]}]" : "[]",
                "dep" => "[]",
                "ready" => Ready ? "[{\"id\":\"task-1\"}]" : "[]",
                "show" => JsonSerializer.Serialize(rows.Take(1)),
                "comments" => JsonSerializer.Serialize(new[] { new { id = "comment-1", issue_id = args[3], author = "human", text = Comment, created_at = "2026-01-01T00:00:00Z" } }),
                _ => JsonSerializer.Serialize(rows)
            };
            return Task.FromResult(new ProcessResult(0, json, ""));
        }
    }
}
