using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WebBeadsIntegrationTests
{
    private static readonly string[] ExpectedDtoFields = ["assignee", "id", "labels", "priority", "status", "title", "type"];
    private static readonly string[] ExpectedTaggedLabels = ["ready-for-agent", "stage:implement"];
    private static readonly string[] ExpectedDiagnosticLogs = ["C:\\not-read\\review.log"];
    private static readonly string[] InitialEscalationLogFiles = ["unavailable"];
    private static readonly string[] ExpectedAttentionFields =
    [
        "beadId", "title", "type", "priority", "status", "labels", "stage", "kind", "pausedAt",
        "pauseTimeSource", "fromRole", "question", "diagnostic", "repairCycles", "maxRepairCycles",
        "canDecide", "canRetry", "canAbort", "unavailableReason", "sourceAuthor"
    ];
    private static readonly string[] PlannedLabels = ["planned"];
    private static readonly string[] StageReviewLabels = ["stage:review"];
    private static readonly string[] RollupLabels = ["rollup"];
    private static readonly string[] NeedsHumanLabels = ["needs-human"];
    private static readonly string[] DecisionPausedLabels = ["stage:plan", "product-decision-required"];
    private static readonly string[] ExpectedLeafBugCommits = ["8b59449"];
    private static readonly string[] ExpectedLeafBugPrs = ["#19"];
    private static readonly string[] ExpectedRollupCommits = ["130f881"];
    private static readonly string[] ExpectedRollupPrs = ["#6"];

    [Fact]
    public async Task IntakeCreatesReadyProductBeadsAndPreservesLiteralContent()
    {
        using var target = new BeadsFixture("configured target");
        using var foreign = new BeadsFixture("foreign target");
        var runner = CreateFixtureRunner(target, foreign);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var foreignGitBefore = Snapshot.GitStatus(foreign.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var literalDescription = "  --description=literal\n\nContext: café ☕\nMarkup: <img src=x onerror=alert(1)>\nQuoted: \"double\", 'single', C:\\temp\\file\nShell-looking: $(do-not-run) `ticks`\n  ";
        var literalAcceptance = "  --acceptance=Given an input\nWhen it is submitted\nThen preserve Markdown **and Unicode** ✓\n  ";
        var expectedIds = new List<string>();
        var expectedIssues = new List<(string Id, string Title, string Type, int Priority, string Description, string Acceptance)>();

        foreach (var type in new[] { "feature", "bug" })
        {
            for (var priority = 0; priority <= 3; priority++)
            {
                var atLimit = type == "bug" && priority == 3;
                var title = atLimit ? "--" + new string('t', 198) : $"--literal {type} P{priority} 'café'";
                var description = atLimit ? new string('d', 4000) : literalDescription;
                var acceptance = atLimit ? new string('a', 2000) : literalAcceptance;
                var json = JsonSerializer.Serialize(new
                {
                    type,
                    priority,
                    title,
                    description,
                    acceptanceCriteria = acceptance
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/beads")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (expectedIds.Count == 0)
                    request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));

                using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Null(response.Headers.Location);

                using var rowDocument = JsonDocument.Parse(responseBody);
                var row = rowDocument.RootElement;
                Assert.Equal(ExpectedDtoFields, row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal(title, row.GetProperty("title").GetString());
                Assert.Equal(type, row.GetProperty("type").GetString());
                Assert.Equal(priority, row.GetProperty("priority").GetInt32());
                Assert.Equal("open", row.GetProperty("status").GetString());
                Assert.Empty(row.GetProperty("labels").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, row.GetProperty("assignee").ValueKind);
                var id = row.GetProperty("id").GetString()!;
                expectedIds.Add(id);

                expectedIssues.Add((id, title, type, priority, description.Trim(), acceptance.Trim()));
            }
        }

        // One real export proves all stored fields and absence of side-effect rows,
        // including both incoming and outgoing dependencies across the entire tracker.
        var exportedLines = target.Run("bd", ["--readonly", "--sandbox", "export", "--all"]);
        using var stored = JsonDocument.Parse("[" + string.Join(',', exportedLines.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + "]");
        var storedRows = stored.RootElement.EnumerateArray().ToArray();
        foreach (var expected in expectedIssues)
        {
            var shown = Assert.Single(storedRows, issue => issue.GetProperty("id").GetString() == expected.Id);
            Assert.Equal(expected.Id, shown.GetProperty("id").GetString());
            Assert.Equal(expected.Title, shown.GetProperty("title").GetString());
            Assert.Equal(expected.Description, shown.GetProperty("description").GetString());
            Assert.Equal(expected.Acceptance, shown.GetProperty("acceptance_criteria").GetString());
            Assert.Equal(expected.Type, shown.GetProperty("issue_type").GetString());
            Assert.Equal(expected.Priority, shown.GetProperty("priority").GetInt32());
            Assert.Equal("open", shown.GetProperty("status").GetString());
            if (shown.TryGetProperty("comments", out var comments)) Assert.Empty(comments.EnumerateArray());
        }
        Assert.All(storedRows, issue =>
        {
            if (issue.TryGetProperty("dependencies", out var dependencies)) Assert.Empty(dependencies.EnumerateArray());
        });

        using var nativeInventory = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0",
            "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"]));
        foreach (var id in expectedIds)
            Assert.Contains(nativeInventory.RootElement.EnumerateArray(), bead => bead.GetProperty("id").GetString() == id);

        using var nativeReady = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "ready", "--limit", "0", "--json"]));
        var nativeReadyIds = nativeReady.RootElement.EnumerateArray().Select(bead => bead.GetProperty("id").GetString()).ToArray();
        Assert.All(expectedIds, id => Assert.Contains(id, nativeReadyIds));
        var readyResponse = await GetRowsAsync(client, "/api/beads?view=ready");
        Assert.All(expectedIds, id => Assert.Contains(id, readyResponse.Rows.Select(Id)));
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(foreignGitBefore, Snapshot.GitStatus(foreign.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));
    }

    [Fact]
    public async Task InvalidQueriesReturnProblemDetailsWithoutLaunchingTools()
    {
        using var target = new BeadsApplicationFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        foreach (var path in new[]
        {
            "/api/beads?view=unsupported",
            "/api/beads?view=all&view=closed",
            "/api/beads?q=one&q=two",
            "/api/digest?window=unsupported",
            "/api/digest?window=24h&window=7d",
            "/api/digest?extra=1"
        })
        {
            var response = await GetRowsAsync(client, path, HttpStatusCode.BadRequest);
            Assert.Equal("application/problem+json", response.ContentType);
            Assert.Equal("no-store", response.CacheControl);
        }
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task DigestGroupsLineageThreadsExtractsOutcomesAndPersistsCatchUpCheckpoint()
    {
        using var target = new BeadsApplicationFixture();
        var now = DateTimeOffset.UtcNow;
        var tenDaysAgo = now.AddDays(-10).ToString("O");
        var threeDaysAgo = now.AddDays(-3).ToString("O");
        var twoHoursAgo = now.AddHours(-2).ToString("O");
        var oneHourAgo = now.AddHours(-1).ToString("O");
        var thirtyMinutesAgo = now.AddMinutes(-30).ToString("O");
        var twentyMinutesAgo = now.AddMinutes(-20).ToString("O");

        var inventoryJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = "ns-root",
                title = "Root feature closed before window",
                issue_type = "feature",
                priority = 1,
                status = "closed",
                assignee = (string?)null,
                labels = PlannedLabels,
                created_at = tenDaysAgo,
                updated_at = tenDaysAgo,
                closed_at = tenDaysAgo,
                close_reason = "Initial feature closed earlier."
            },
            new
            {
                id = "ns-mid",
                title = "Middle implementation task",
                issue_type = "task",
                priority = 2,
                status = "closed",
                assignee = "Michael Gasparelli",
                labels = Array.Empty<string>(),
                created_at = threeDaysAgo,
                updated_at = twoHoursAgo,
                closed_at = twoHoursAgo,
                close_reason = "Merged approved candidate ca8377492f8f7731f9a1229b017f4bf2d1ed820e into local main as ccf9d1c8c3be5f2871d8cba191f543170f2e3a83.",
                dependencies = new[]
                {
                    new { issue_id = "ns-mid", depends_on_id = "ns-root", type = "parent-child" }
                }
            },
            new
            {
                id = "ns-leaf-bug",
                title = "Discovered regression leaf",
                issue_type = "bug",
                priority = 1,
                status = "closed",
                assignee = "Michael Gasparelli",
                labels = Array.Empty<string>(),
                created_at = twoHoursAgo,
                updated_at = oneHourAgo,
                closed_at = oneHourAgo,
                close_reason = "Fixed in commit 8b59449 (#19).",
                dependencies = new[]
                {
                    new { issue_id = "ns-leaf-bug", depends_on_id = "ns-mid", type = "discovered-from" }
                }
            },
            new
            {
                id = "ns-leaf-open",
                title = "Follow-up hardening slice",
                issue_type = "task",
                priority = 2,
                status = "in_progress",
                assignee = "codex:ns-leaf-open",
                labels = StageReviewLabels,
                created_at = thirtyMinutesAgo,
                updated_at = twentyMinutesAgo,
                dependencies = new[]
                {
                    new { issue_id = "ns-leaf-open", depends_on_id = "ns-mid", type = "discovered-from" }
                }
            },
            new
            {
                id = "ns-rollup",
                title = "Parent rollup epic",
                issue_type = "epic",
                priority = 1,
                status = "open",
                assignee = (string?)null,
                labels = RollupLabels,
                created_at = tenDaysAgo,
                updated_at = tenDaysAgo
            },
            new
            {
                id = "ns-rollup.1",
                title = "Single active child under rollup",
                issue_type = "task",
                priority = 2,
                status = "closed",
                assignee = "Michael Gasparelli",
                labels = Array.Empty<string>(),
                created_at = threeDaysAgo,
                updated_at = oneHourAgo,
                closed_at = oneHourAgo,
                close_reason = "PR #6 squash-merged into main as 130f881.",
                dependencies = new[]
                {
                    new { issue_id = "ns-rollup.1", depends_on_id = "ns-rollup", type = "parent-child" }
                }
            },
            new
            {
                id = "ns-standalone-dup",
                title = "Duplicate standalone bug",
                issue_type = "bug",
                priority = 2,
                status = "closed",
                assignee = (string?)null,
                labels = Array.Empty<string>(),
                created_at = twoHoursAgo,
                updated_at = oneHourAgo,
                closed_at = oneHourAgo,
                close_reason = "Duplicate of existing issue; no code changes required."
            },
            new
            {
                id = "ns-standalone-aborted",
                title = "Aborted standalone task",
                issue_type = "task",
                priority = 2,
                status = "closed",
                assignee = (string?)null,
                labels = NeedsHumanLabels,
                created_at = twoHoursAgo,
                updated_at = oneHourAgo,
                closed_at = oneHourAgo,
                close_reason = "Aborted from the Human Attention Desk."
            },
            new
            {
                id = "ns-standalone-paused",
                title = "Paused standalone decision bead",
                issue_type = "feature",
                priority = 1,
                status = "open",
                assignee = (string?)null,
                labels = DecisionPausedLabels,
                created_at = threeDaysAgo,
                updated_at = twentyMinutesAgo,
                notes = "Waiting on user confirmation for digest placement."
            }
        });

        var systemRunner = new SystemProcessRunner();
        var runner = new RecordingProcessRunner((request, cancellationToken) =>
            request.FileName == "git"
                ? systemRunner.RunAsync(request, cancellationToken)
                : Task.FromResult(new ProcessResult(0, inventoryJson, string.Empty)));
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        // 1. Fresh host GET /api/digest defaults to 24h, has null lastCaughtUpAt, and does not write to HostRoot.
        using (var initialResponse = await client.GetAsync("/api/digest", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
            Assert.Equal("application/json", initialResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", initialResponse.Headers.CacheControl?.ToString());
            Assert.Empty(Directory.EnumerateFileSystemEntries(factory.HostRoot));

            using var document = JsonDocument.Parse(await initialResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var root = document.RootElement;
            Assert.Equal("24h", root.GetProperty("window").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("lastCaughtUpAt").ValueKind);
            Assert.False(root.GetProperty("isCaughtUp").GetBoolean());

            var metrics = root.GetProperty("metrics");
            Assert.Equal(5, metrics.GetProperty("completedTotal").GetInt32());
            Assert.Equal(0, metrics.GetProperty("completedFeatures").GetInt32());
            Assert.Equal(3, metrics.GetProperty("completedTasks").GetInt32());
            Assert.Equal(2, metrics.GetProperty("completedBugs").GetInt32());
            Assert.Equal(2, metrics.GetProperty("activeThreads").GetInt32());
            Assert.Equal(2, metrics.GetProperty("openOrUpdatedTotal").GetInt32());
            Assert.Equal(1, metrics.GetProperty("waitingForGuidance").GetInt32());

            var threads = root.GetProperty("threads").EnumerateArray().ToArray();
            Assert.Equal(2, threads.Length);

            var multiHopThread = Assert.Single(threads, thread => thread.GetProperty("root").GetProperty("id").GetString() == "ns-root");
            Assert.Equal("context", multiHopThread.GetProperty("root").GetProperty("activityKind").GetString());
            Assert.Equal(2, multiHopThread.GetProperty("completedCount").GetInt32());
            Assert.Equal(1, multiHopThread.GetProperty("openCount").GetInt32());
            var multiHopItems = multiHopThread.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(3, multiHopItems.Length);

            var midItem = Assert.Single(multiHopItems, item => item.GetProperty("id").GetString() == "ns-mid");
            Assert.Equal("closed", midItem.GetProperty("activityKind").GetString());
            Assert.Equal("ns-root", midItem.GetProperty("fromId").GetString());
            Assert.Equal("parent-child", midItem.GetProperty("fromRelation").GetString());
            Assert.Equal("resolved", midItem.GetProperty("outcome").GetString());
            Assert.Contains("ccf9d1c", midItem.GetProperty("commits").EnumerateArray().Select(commit => commit.GetString()));

            var leafBug = Assert.Single(multiHopItems, item => item.GetProperty("id").GetString() == "ns-leaf-bug");
            Assert.Equal("ns-mid", leafBug.GetProperty("fromId").GetString());
            Assert.Equal("discovered-from", leafBug.GetProperty("fromRelation").GetString());
            Assert.Equal(ExpectedLeafBugCommits, leafBug.GetProperty("commits").EnumerateArray().Select(commit => commit.GetString()).ToArray());
            Assert.Equal(ExpectedLeafBugPrs, leafBug.GetProperty("pullRequests").EnumerateArray().Select(pr => pr.GetString()).ToArray());

            var leafOpen = Assert.Single(multiHopItems, item => item.GetProperty("id").GetString() == "ns-leaf-open");
            Assert.Equal("opened", leafOpen.GetProperty("activityKind").GetString());
            Assert.Equal("in_progress", leafOpen.GetProperty("outcome").GetString());
            Assert.Equal("In progress (stage:review)", leafOpen.GetProperty("outcomeLabel").GetString());

            var rollupThread = Assert.Single(threads, thread => thread.GetProperty("root").GetProperty("id").GetString() == "ns-rollup");
            Assert.Equal("context", rollupThread.GetProperty("root").GetProperty("activityKind").GetString());
            var rollupChild = Assert.Single(rollupThread.GetProperty("items").EnumerateArray());
            Assert.Equal("ns-rollup.1", rollupChild.GetProperty("id").GetString());
            Assert.Equal(ExpectedRollupCommits, rollupChild.GetProperty("commits").EnumerateArray().Select(commit => commit.GetString()).ToArray());
            Assert.Equal(ExpectedRollupPrs, rollupChild.GetProperty("pullRequests").EnumerateArray().Select(pr => pr.GetString()).ToArray());

            var standaloneCompleted = root.GetProperty("standaloneCompleted").EnumerateArray().ToArray();
            Assert.Equal(2, standaloneCompleted.Length);
            var dupItem = Assert.Single(standaloneCompleted, item => item.GetProperty("id").GetString() == "ns-standalone-dup");
            Assert.Equal("no_op", dupItem.GetProperty("outcome").GetString());
            Assert.Equal("Closed (No-Op / Duplicate)", dupItem.GetProperty("outcomeLabel").GetString());

            var abortedItem = Assert.Single(standaloneCompleted, item => item.GetProperty("id").GetString() == "ns-standalone-aborted");
            Assert.Equal("aborted", abortedItem.GetProperty("outcome").GetString());
            Assert.Equal("Aborted", abortedItem.GetProperty("outcomeLabel").GetString());

            var standaloneOpen = root.GetProperty("standaloneOpen").EnumerateArray().ToArray();
            var pausedItem = Assert.Single(standaloneOpen);
            Assert.Equal("ns-standalone-paused", pausedItem.GetProperty("id").GetString());
            Assert.Equal("updated", pausedItem.GetProperty("activityKind").GetString());
            Assert.Equal("paused", pausedItem.GetProperty("outcome").GetString());
            Assert.Equal("product-decision-required", pausedItem.GetProperty("outcomeLabel").GetString());

            // 2. Mark Caught Up using generatedAt snapshot timestamp.
            var generatedAt = root.GetProperty("generatedAt").GetString()!;
            using var catchUpRequest = CreateAttentionRequest(
                client,
                "/api/digest/catch-up",
                JsonSerializer.Serialize(new { caughtUpAt = generatedAt }));
            using var catchUpResponse = await client.SendAsync(catchUpRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, catchUpResponse.StatusCode);

            using var caughtUpDoc = JsonDocument.Parse(await catchUpResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var caughtUpRoot = caughtUpDoc.RootElement;
            Assert.Equal(DateTimeOffset.Parse(generatedAt), DateTimeOffset.Parse(caughtUpRoot.GetProperty("lastCaughtUpAt").GetString()!));
        }

        var catchUpPath = Path.Combine(factory.HostRoot, "telemetry", "web-test", "catch-up.json");
        Assert.True(File.Exists(catchUpPath));

        // 3. Subsequent default GET /api/digest resolves auto -> since_caught_up and stays caught up.
        using (var subsequentResponse = await client.GetAsync("/api/digest", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, subsequentResponse.StatusCode);
            using var document = JsonDocument.Parse(await subsequentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var caughtUpDigest = document.RootElement;
            Assert.Equal("since_caught_up", caughtUpDigest.GetProperty("window").GetString());
            Assert.True(caughtUpDigest.GetProperty("isCaughtUp").GetBoolean());
            Assert.Equal(0, caughtUpDigest.GetProperty("metrics").GetProperty("completedTotal").GetInt32());
            Assert.Equal(0, caughtUpDigest.GetProperty("metrics").GetProperty("openOrUpdatedTotal").GetInt32());
            Assert.Empty(caughtUpDigest.GetProperty("threads").EnumerateArray());
            Assert.Empty(caughtUpDigest.GetProperty("standaloneCompleted").EnumerateArray());
            Assert.Empty(caughtUpDigest.GetProperty("standaloneOpen").EnumerateArray());
        }

        // 4. Explicit ?window=24h still returns the 24h delta while reporting lastCaughtUpAt.
        using (var explicitWindowResponse = await client.GetAsync("/api/digest?window=24h", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, explicitWindowResponse.StatusCode);
            using var document = JsonDocument.Parse(await explicitWindowResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("24h", document.RootElement.GetProperty("window").GetString());
            Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("lastCaughtUpAt").ValueKind);
            Assert.False(document.RootElement.GetProperty("isCaughtUp").GetBoolean());
            Assert.Equal(5, document.RootElement.GetProperty("metrics").GetProperty("completedTotal").GetInt32());
        }
    }

    [Fact]
    public async Task DigestCatchUpEnforcesLocalMutationPolicyAndValidatesRequestPayload()
    {
        using var target = new BeadsApplicationFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        using (var missingHeader = CreateAttentionRequest(client, "/api/digest/catch-up", "{}", includeHeader: false))
        using (var response = await client.SendAsync(missingHeader, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var crossOrigin = CreateAttentionRequest(client, "/api/digest/catch-up", "{}", origin: "https://evil.example"))
        using (var response = await client.SendAsync(crossOrigin, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var invalidBody in new[]
        {
            "{",
            "[]",
            "{\"caughtUpAt\":\"not-a-timestamp\"}",
            "{\"caughtUpAt\":123}",
            "{\"unexpected\":true}",
            JsonSerializer.Serialize(new { caughtUpAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O") })
        })
        {
            using var badRequest = CreateAttentionRequest(client, "/api/digest/catch-up", invalidBody);
            using var response = await client.SendAsync(badRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(factory.HostRoot));
        Assert.Empty(runner.Requests);
    }

    private sealed record ExplorerSeed(string Title, string Type = "task", string Status = "open",
        string? Assignee = null, int Priority = 2, IReadOnlyList<string>? Labels = null);

    private static JsonElement FindAttention(IEnumerable<JsonElement> rows, string id)
        => Assert.Single(rows, row => row.GetProperty("beadId").GetString() == id);

    private static object ExpectedWorkflow(string? stage, string[] pauseLabels, int repairCycles)
        => new { stage, pauseLabels, repairCycles };

    private static async Task<JsonElement[]> GetAttentionRowsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/attention", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private static HttpRequestMessage CreateAttentionRequest(HttpClient client, string path, string? body = null,
        bool includeHeader = true, string? origin = null, string contentType = "application/json", bool includeOrigin = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
        if (includeHeader) request.Headers.TryAddWithoutValidation("X-Nightweaver-Request", "console-v1");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        else if (includeHeader && includeOrigin) request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return request;
    }

    private static JsonElement ReadShownBead(BeadsFixture target, string id)
    {
        using var document = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "show", id, "--json"]));
        var root = document.RootElement;
        return (root.ValueKind == JsonValueKind.Array ? Assert.Single(root.EnumerateArray()) : root).Clone();
    }

    private static JsonElement[] ReadComments(BeadsFixture target, string id)
    {
        using var document = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "comments", id, "--json"]));
        return document.RootElement.EnumerateArray().Select(comment => comment.Clone()).ToArray();
    }

    private static bool GuidanceMatches(string text, string action, string instruction)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetProperty("schema").GetString() == "nightweaver-human-guidance-v1"
                && document.RootElement.GetProperty("action").GetString() == action
                && document.RootElement.GetProperty("instruction").GetString() == instruction
                && document.RootElement.GetProperty("actor").GetString() == "local-console";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ActionMatches(string text, string action)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetProperty("schema").GetString() == "nightweaver-human-action-v1"
                && document.RootElement.GetProperty("action").GetString() == action
                && document.RootElement.GetProperty("actor").GetString() == "local-console";
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static RecordingProcessRunner CreateFixtureRunner(BeadsApplicationFixture target)
        => new(new WebBeadsProcessRunner(target.Path).RunAsync);

    private static NightweaverWebApplicationFactory CreateFactory(BeadsApplicationFixture target, IProcessRunner runner)
        => CreateFactory(target.Path, runner);

    private static NightweaverWebApplicationFactory CreateFactory(BeadsFixture target, IProcessRunner runner)
        => CreateFactory(target.Path, runner);

    private static NightweaverWebApplicationFactory CreateFactory(string targetPath, IProcessRunner runner)
    {
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "web-test",
            target_repository = new { path = targetPath }
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

    private static string CreateIntakeJson(string title)
        => JsonSerializer.Serialize(new
        {
            type = "feature",
            priority = 2,
            title,
            description = "A concurrent test Product Bead.",
            acceptanceCriteria = "Both deliberate submissions are created once."
        });

    private static string CreateCreateOutput(string id, string titleJson, string? injectedPropertyJson = null)
        => $"{{{injectedPropertyJson ?? string.Empty}\"id\":{JsonSerializer.Serialize(id)},\"title\":{titleJson},"
            + "\"issue_type\":\"feature\",\"priority\":1,\"status\":\"open\",\"assignee\":null,\"labels\":[]}";

    private static void UpdateMaximum(ref int maximum, int observed)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (observed <= current || Interlocked.CompareExchange(ref maximum, observed, current) == current)
                return;
        }
    }

    // HTTP validation and output parsing tests exercise the real application with a
    // stateful bd boundary; native create/read semantics stay covered by the intake E2E test.
    private sealed class WebBeadsProcessRunner(string repositoryPath) : IProcessRunner
    {
        private readonly Dictionary<string, JsonObject> beads = new(StringComparer.Ordinal);

        internal string Inventory() => JsonSerializer.Serialize(beads.Values);
        internal string Show(string id) => beads[id].ToJsonString();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName == "git") return new SystemProcessRunner().RunAsync(request, cancellationToken);
            Assert.Equal("bd", request.FileName);
            Assert.Equal(repositoryPath, request.WorkingDirectory);
            var args = request.Arguments.ToList();
            if (args.Contains("create", StringComparer.Ordinal))
            {
                string Value(string option)
                {
                    var inline = args.SingleOrDefault(argument => argument.StartsWith(option + "=", StringComparison.Ordinal));
                    return inline is null ? args[args.IndexOf(option) + 1] : inline[(option.Length + 1)..];
                }
                var id = $"web-{beads.Count + 1:D3}";
                var row = new JsonObject
                {
                    ["id"] = id, ["title"] = Value("--title"), ["issue_type"] = Value("--type"),
                    ["priority"] = int.Parse(Value("--priority"), System.Globalization.CultureInfo.InvariantCulture),
                    ["description"] = Value("--description"), ["acceptance_criteria"] = Value("--acceptance"),
                    ["status"] = "open", ["assignee"] = null, ["labels"] = new JsonArray(),
                    ["created_at"] = "2026-01-01T00:00:00Z"
                };
                beads.Add(id, row);
                return Task.FromResult(new ProcessResult(0, row.ToJsonString(), ""));
            }
            Assert.True(args.Contains("--readonly", StringComparer.Ordinal), "Only creates and read-only queries are allowed.");
            Assert.True(args.Contains("list", StringComparer.Ordinal) || args.Contains("ready", StringComparer.Ordinal));
            return Task.FromResult(new ProcessResult(0, Inventory(), ""));
        }
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(content, 0, content.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
