using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WebBeadsIntegrationTests
{
    private static readonly string[] ExpectedDtoFields = ["assignee", "id", "labels", "priority", "status", "title", "type"];
    private static readonly string[] ExpectedTaggedLabels = ["ready-for-agent", "stage:implement"];

    [Fact]
    public async Task ExplorerReturnsCompleteReadOnlyInventoryAndAppliesNativeViewsAndSearch()
    {
        using var target = new BeadsFixture("configured target");
        using var foreign = new BeadsFixture("foreign target");

        var unassigned = target.Create("Ready unassigned P0", priority: 0);
        var assigned = target.Create("Assigned open P4", actor: "human", priority: 4);
        var active = target.Create("Active Needle", status: "in_progress", priority: 1);
        var explicitlyBlocked = target.Create("Explicitly blocked", status: "blocked", priority: 2);
        var deferred = target.Create("Deferred issue", priority: 3);
        target.Run("bd", ["defer", deferred]);
        var closed = target.Create("Closed issue", status: "closed", priority: 3);
        var taggedAssigned = target.Create("Tagged assigned feature", actor: "maintainer", type: "feature", priority: 1);
        target.Run("bd", ["update", taggedAssigned, "--add-label", "ready-for-agent", "--add-label", "stage:implement"]);
        var htmlTitle = "<img src=x onerror=alert(1)> café & needle";
        var htmlLooking = target.Create(htmlTitle, priority: 4);
        target.Run("bd", ["update", htmlLooking, "--add-label", "alpha", "--add-label", "beta"]);
        target.Run("bd", ["comments", "add", htmlLooking, "fixture comment"]);

        var activeBlocker = target.Create("Active dependency blocker");
        var blockedChild = target.Create("Open dependency child");
        target.Run("bd", ["dep", "add", blockedChild, activeBlocker]);
        var closingBlocker = target.Create("Blocker to close and reopen");
        var changingChild = target.Create("Dependency changes after blocker update");
        target.Run("bd", ["dep", "add", changingChild, closingBlocker]);
        var relatedIssue = target.Create("Nonblocking relation child");
        var relatedControl = target.Create("Nonblocking relation control");
        target.Run("bd", ["dep", "relate", relatedIssue, relatedControl]);

        for (var index = 0; index < 53; index++)
            target.Create($"Ordinary record {index:D2}", priority: index % 5);
        var beyondDefaultLimit = target.Create("Inventory beyond default limit", priority: 4);
        var foreignBead = foreign.Create("Must not appear in configured target");

        var inventoryJson = target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0",
            "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"]);
        var nativeReadyJson = target.Run("bd", ["--readonly", "--sandbox", "ready", "--limit", "0", "--json"]);
        var nativeBlockedJson = target.Run("bd", ["--readonly", "--sandbox", "blocked", "--json"]);
        var commentJson = target.Run("bd", ["--readonly", "--sandbox", "comments", htmlLooking, "--json"]);
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var foreignGitBefore = Snapshot.GitStatus(foreign.Path);

        var processRunner = CreateFixtureRunner(target, foreign);
        using var factory = CreateFactory(target, processRunner);
        var hostTreeBefore = Snapshot.FileTree(factory.HostRoot);
        var webTreeBefore = Snapshot.FileTree(factory.WebRoot);
        using var client = factory.CreateClient();

        var all = await GetRowsAsync(client, "/api/beads");
        Assert.Equal(HttpStatusCode.OK, all.Status);
        Assert.Equal("application/json", all.ContentType);
        Assert.Equal("no-store", all.CacheControl);
        using var nativeInventory = JsonDocument.Parse(inventoryJson);
        var expectedInventory = nativeInventory.RootElement.EnumerateArray()
            .Select(row => new
            {
                Id = row.GetProperty("id").GetString()!,
                Priority = row.GetProperty("priority").GetInt32(),
                Status = row.GetProperty("status").GetString()!
            })
            .OrderBy(row => row.Priority)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedInventory.Select(row => row.Id), all.Rows.Select(row => row.GetProperty("id").GetString()));
        Assert.True(all.Rows.Length > 50);
        Assert.Contains(all.Rows, row => row.GetProperty("id").GetString() == beyondDefaultLimit);
        Assert.DoesNotContain(all.Rows, row => row.GetProperty("id").GetString() == foreignBead);

        foreach (var row in all.Rows)
        {
            Assert.Equal(ExpectedDtoFields,
                row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(JsonValueKind.Number, row.GetProperty("priority").ValueKind);
            Assert.InRange(row.GetProperty("priority").GetInt32(), 0, 4);
            Assert.Equal(JsonValueKind.Array, row.GetProperty("labels").ValueKind);
        }

        var unassignedRow = Find(all.Rows, unassigned);
        Assert.Equal(JsonValueKind.Null, unassignedRow.GetProperty("assignee").ValueKind);
        Assert.Empty(unassignedRow.GetProperty("labels").EnumerateArray());
        var assignedRow = Find(all.Rows, assigned);
        Assert.Equal("human", assignedRow.GetProperty("assignee").GetString());
        Assert.Equal(4, assignedRow.GetProperty("priority").GetInt32());
        var taggedRow = Find(all.Rows, taggedAssigned);
        Assert.Equal("feature", taggedRow.GetProperty("type").GetString());
        Assert.Equal(ExpectedTaggedLabels,
            taggedRow.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal("in_progress", Find(all.Rows, active).GetProperty("status").GetString());
        Assert.Equal("closed", Find(all.Rows, closed).GetProperty("status").GetString());
        Assert.Equal(htmlTitle, Find(all.Rows, htmlLooking).GetProperty("title").GetString());

        var blankView = await GetRowsAsync(client, "/api/beads?view=%20%20");
        var mixedCaseView = await GetRowsAsync(client, "/api/beads?view=AlL");
        Assert.Equal(all.Rows.Select(Id), blankView.Rows.Select(Id));
        Assert.Equal(all.Rows.Select(Id), mixedCaseView.Rows.Select(Id));

        var activeView = await GetRowsAsync(client, "/api/beads?view=active");
        Assert.Equal(new[] { active }, activeView.Rows.Select(Id));
        var closedView = await GetRowsAsync(client, "/api/beads?view=closed");
        Assert.Equal(new[] { closed }, closedView.Rows.Select(Id));

        using var readyInventory = JsonDocument.Parse(inventoryJson);
        using var nativeReady = JsonDocument.Parse(nativeReadyJson);
        var openIds = readyInventory.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("status").GetString() == "open")
            .Select(row => row.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedReady = nativeReady.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()!)
            .Where(openIds.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var readyView = await GetRowsAsync(client, "/api/beads?view=ready");
        Assert.Equal(expectedReady, readyView.Rows.Select(Id).Order(StringComparer.Ordinal));
        Assert.Contains(assigned, readyView.Rows.Select(Id));
        Assert.DoesNotContain(deferred, readyView.Rows.Select(Id));
        Assert.DoesNotContain(blockedChild, readyView.Rows.Select(Id));

        using var nativeBlocked = JsonDocument.Parse(nativeBlockedJson);
        foreach (var row in nativeBlocked.RootElement.EnumerateArray())
        {
            Assert.True(row.GetProperty("blocked_by_count").GetInt32() >= 0);
            Assert.Equal(JsonValueKind.Array, row.GetProperty("blocked_by").ValueKind);
        }
        var expectedBlocked = inventoryJson.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            readyInventory.RootElement.EnumerateArray()
                .Where(row => row.GetProperty("status").GetString() == "blocked")
                .Select(row => row.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        foreach (var row in nativeBlocked.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            var status = readyInventory.RootElement.EnumerateArray()
                .FirstOrDefault(issue => issue.GetProperty("id").GetString() == id);
            if (status.ValueKind == JsonValueKind.Object && status.GetProperty("status").GetString() != "closed")
                expectedBlocked.Add(id);
        }
        var blockedView = await GetRowsAsync(client, "/api/beads?view=blocked");
        Assert.Equal(expectedBlocked.Order(StringComparer.Ordinal), blockedView.Rows.Select(Id).Order(StringComparer.Ordinal));
        Assert.Contains(blockedChild, blockedView.Rows.Select(Id));
        var blockedMetadata = Assert.Single(nativeBlocked.RootElement.EnumerateArray(), row => row.GetProperty("id").GetString() == blockedChild);
        Assert.Contains(activeBlocker, blockedMetadata.GetProperty("blocked_by").EnumerateArray().Select(blocker => blocker.GetString()));
        Assert.Contains(explicitlyBlocked, blockedView.Rows.Select(Id));
        Assert.DoesNotContain(relatedIssue, blockedView.Rows.Select(Id));

        var byId = await GetRowsAsync(client, $"/api/beads?q={Uri.EscapeDataString(unassigned.ToUpperInvariant())}");
        Assert.Equal(new[] { unassigned }, byId.Rows.Select(Id));
        var byTitle = await GetRowsAsync(client, "/api/beads?q=CAF%C3%89%20%26%20NEEDLE");
        Assert.Equal(new[] { htmlLooking }, byTitle.Rows.Select(Id));
        var trimmedSearch = await GetRowsAsync(client, "/api/beads?q=%20%20");
        Assert.Equal(all.Rows.Select(Id), trimmedSearch.Rows.Select(Id));
        var andSearch = await GetRowsAsync(client, "/api/beads?view=active&q=needle");
        Assert.Equal(new[] { active }, andSearch.Rows.Select(Id));
        var noMatches = await GetRowsAsync(client, "/api/beads?view=closed&q=ordinary");
        Assert.Empty(noMatches.Rows);

        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));
        Assert.Equal(foreignGitBefore, Snapshot.GitStatus(foreign.Path));
        Assert.Equal(inventoryJson, target.Run("bd", BeadsCommand.Create(target.Path).Arguments));
        Assert.Equal(commentJson, target.Run("bd", ["--readonly", "--sandbox", "comments", htmlLooking, "--json"]));
        Assert.Equal(hostTreeBefore, Snapshot.FileTree(factory.HostRoot));
        Assert.Equal(webTreeBefore, Snapshot.FileTree(factory.WebRoot));
        target.Run("bd", ["close", closingBlocker, "--force"]);
        var afterClose = await GetRowsAsync(client, "/api/beads?view=blocked");
        Assert.DoesNotContain(changingChild, afterClose.Rows.Select(Id));
        target.Run("bd", ["update", closingBlocker, "--status", "open"]);
        var afterReopen = await GetRowsAsync(client, "/api/beads?view=blocked");
        Assert.Contains(changingChild, afterReopen.Rows.Select(Id));
    }

    [Fact]
    public async Task InvalidQueriesReturnProblemDetailsWithoutLaunchingTools()
    {
        using var target = new BeadsFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        foreach (var path in new[]
        {
            "/api/beads?view=unsupported",
            "/api/beads?view=all&view=closed",
            "/api/beads?q=one&q=two"
        })
        {
            var response = await GetRowsAsync(client, path, HttpStatusCode.BadRequest);
            Assert.Equal("application/problem+json", response.ContentType);
            Assert.Equal("no-store", response.CacheControl);
        }
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("missing-executable", "Beads tracker unavailable")]
    [InlineData("nonzero-exit", "Beads tracker unavailable")]
    [InlineData("timeout", "Beads tracker timed out")]
    [InlineData("malformed-json", "Incompatible Beads output")]
    public async Task ToolFailuresReturnSafeProblemDetailsAndLeaveHostAvailable(string failure, string expectedTitle)
    {
        using var target = new BeadsFixture();
        using var foreign = new BeadsFixture("foreign tracker");
        const string secret = "raw-stderr-secret";
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var foreignGitBefore = Snapshot.GitStatus(foreign.Path);
        var actual = new SystemProcessRunner();
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName != "bd")
                return await actual.RunAsync(AddFixtureEnvironment(target, request, foreign), cancellationToken);
            return failure switch
            {
                "missing-executable" => throw new ToolExecutionException($"required executable 'bd' was not found at {target.Path}", $"{secret} {target.Path}"),
                "nonzero-exit" => new ProcessResult(19, string.Empty, $"{secret} {target.Path}"),
                "timeout" => throw new ToolExecutionException("bd timed out after 0.01s", $"{secret} {target.Path}"),
                "malformed-json" => new ProcessResult(0, "{ malformed", string.Empty),
                _ => throw new InvalidOperationException("Unknown injected test fault.")
            };
        });
        using var factory = CreateFactory(target, runner);
        var hostTreeBefore = Snapshot.FileTree(factory.HostRoot);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(expectedTitle, problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var shell = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));
        Assert.Equal(foreignGitBefore, Snapshot.GitStatus(foreign.Path));
        Assert.Equal(hostTreeBefore, Snapshot.FileTree(factory.HostRoot));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"dolt_mode\":7}")]
    public async Task MalformedMetadataReturnsSafeProblemDetailsAndLeavesHostAvailable(string metadataJson)
    {
        using var target = new BeadsFixture();
        File.WriteAllText(Path.Combine(target.Path, ".beads", "metadata.json"), metadataJson);
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        var hostTreeBefore = Snapshot.FileTree(factory.HostRoot);
        var webTreeBefore = Snapshot.FileTree(factory.WebRoot);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("Incompatible Beads output", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("The bd command returned data that Nightweaver could not read.", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metadata.json", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var shell = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));
        Assert.Equal(hostTreeBefore, Snapshot.FileTree(factory.HostRoot));
        Assert.Equal(webTreeBefore, Snapshot.FileTree(factory.WebRoot));
    }

    [Fact]
    public async Task AbortingWhileWaitingForReadGateDoesNotStartAnotherToolQuery()
    {
        using var target = new BeadsFixture();
        var actual = new SystemProcessRunner();
        var firstQueryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasBlockedFirstQuery = 0;
        var beadQueryCount = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName != "bd")
                return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
            Interlocked.Increment(ref beadQueryCount);
            if (Interlocked.CompareExchange(ref hasBlockedFirstQuery, 1, 0) == 0)
            {
                firstQueryStarted.SetResult();
                await releaseFirstQuery.Task.WaitAsync(cancellationToken);
            }
            return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        var firstRequest = client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        await firstQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        using var cancelWaitingRequest = new CancellationTokenSource();
        var waitingRequest = client.GetAsync("/api/beads", cancelWaitingRequest.Token);
        cancelWaitingRequest.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingRequest);
        Assert.Equal(1, Volatile.Read(ref beadQueryCount));

        releaseFirstQuery.SetResult();
        using var firstResponse = await firstRequest.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var nextResponse = await client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        Assert.Equal(2, Volatile.Read(ref beadQueryCount));
    }

    [Fact]
    public async Task PreparedEmptyTrackerReturnsAnEmptyArray()
    {
        using var target = new BeadsFixture();
        target.Run("bd", ["delete", target.ControlBeadId, "--force"]);
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        var response = await GetRowsAsync(client, "/api/beads");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Empty(response.Rows);
    }

    private static RecordingProcessRunner CreateFixtureRunner(BeadsFixture target, BeadsFixture? foreign = null)
    {
        var actual = new SystemProcessRunner();
        return new RecordingProcessRunner((request, cancellationToken) =>
            actual.RunAsync(AddFixtureEnvironment(target, request, foreign), cancellationToken));
    }

    private static ProcessRequest AddFixtureEnvironment(BeadsFixture target, ProcessRequest request, BeadsFixture? foreign = null)
    {
        var environment = target.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (foreign is not null)
        {
            environment["BEADS_DIR"] = Path.Combine(foreign.Path, ".beads");
            environment["BEADS_DB"] = Path.Combine(foreign.Path, ".beads", "beads.db");
            environment["BD_DB"] = "foreign-db";
            environment["GIT_DIR"] = Path.Combine(foreign.Path, ".git");
        }
        foreach (var pair in request.Environment)
            environment[pair.Key] = pair.Value;
        return request with { Environment = environment };
    }

    private static NightweaverWebApplicationFactory CreateFactory(BeadsFixture target, IProcessRunner runner)
    {
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "web-test",
            target_repository = new { path = target.Path }
        }));
        return factory;
    }

    private static async Task<(HttpStatusCode Status, string? ContentType, string? CacheControl, JsonElement[] Rows)> GetRowsAsync(
        HttpClient client, string path, HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray()
            : Array.Empty<JsonElement>();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType,
            response.Headers.CacheControl?.ToString(), rows);
    }

    private static JsonElement Find(IEnumerable<JsonElement> rows, string id)
        => Assert.Single(rows, row => row.GetProperty("id").GetString() == id);

    private static string Id(JsonElement row) => row.GetProperty("id").GetString()!;
}
