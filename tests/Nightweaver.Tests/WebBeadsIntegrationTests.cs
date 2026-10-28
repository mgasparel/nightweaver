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

    [Fact]
    public async Task ExplorerReturnsCompleteReadOnlyInventoryAndAppliesNativeViewsAndSearch()
    {
        using var target = new BeadsFixture("configured target");
        using var foreign = new BeadsFixture("foreign target");

        var htmlTitle = "<img src=x onerror=alert(1)> café & needle";
        var seeds = new[]
        {
            new ExplorerSeed("Ready unassigned P0", Priority: 0),
            new ExplorerSeed("Assigned open P4", Assignee: "human", Priority: 4),
            new ExplorerSeed("Active Needle", Status: "in_progress", Priority: 1),
            new ExplorerSeed("Explicitly blocked", Status: "blocked"),
            new ExplorerSeed("Deferred issue", Priority: 3),
            new ExplorerSeed("Closed issue", Status: "closed", Priority: 3),
            new ExplorerSeed("Tagged assigned feature", Type: "feature", Assignee: "maintainer", Priority: 1,
                Labels: ["ready-for-agent", "stage:implement"]),
            new ExplorerSeed(htmlTitle, Priority: 4, Labels: ["alpha", "beta"]),
            new ExplorerSeed("Active dependency blocker"),
            new ExplorerSeed("Open dependency child"),
            new ExplorerSeed("Blocker to close and reopen"),
            new ExplorerSeed("Dependency changes after blocker update"),
            new ExplorerSeed("Nonblocking relation child"),
            new ExplorerSeed("Nonblocking relation control")
        }.Concat(Enumerable.Range(0, 53).Select(index => new ExplorerSeed($"Ordinary record {index:D2}", Priority: index % 5)));
        using var temp = new TempDirectory();
        var recordsFile = temp.WriteFile("explorer-records.jsonl", string.Join('\n', seeds.Select(seed => JsonSerializer.Serialize(new
        {
            title = seed.Title, issue_type = seed.Type, status = seed.Status, assignee = seed.Assignee,
            priority = seed.Priority, created_at = "2025-01-01T00:00:00Z", labels = seed.Labels ?? []
        }))));
        using (JsonDocument.Parse(target.Run("bd", ["import", recordsFile, "--json"]))) { }
        var seededInventory = BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments));
        var idsByTitle = seededInventory.ById.Values.ToDictionary(bead => bead.Payload.GetProperty("title").GetString()!, bead => bead.Id,
            StringComparer.Ordinal);
        var unassigned = idsByTitle["Ready unassigned P0"];
        var assigned = idsByTitle["Assigned open P4"];
        var active = idsByTitle["Active Needle"];
        var explicitlyBlocked = idsByTitle["Explicitly blocked"];
        var deferred = idsByTitle["Deferred issue"];
        var closed = idsByTitle["Closed issue"];
        var taggedAssigned = idsByTitle["Tagged assigned feature"];
        var htmlLooking = idsByTitle[htmlTitle];
        var activeBlocker = idsByTitle["Active dependency blocker"];
        var blockedChild = idsByTitle["Open dependency child"];
        var closingBlocker = idsByTitle["Blocker to close and reopen"];
        var changingChild = idsByTitle["Dependency changes after blocker update"];
        var relatedIssue = idsByTitle["Nonblocking relation child"];
        var relatedControl = idsByTitle["Nonblocking relation control"];
        // Exercise native scheduling and dependency transitions after the bulk seed.
        target.Run("bd", ["defer", deferred]);
        target.Run("bd", ["comments", "add", htmlLooking, "fixture comment"]);
        target.Run("bd", ["dep", "add", blockedChild, activeBlocker]);
        target.Run("bd", ["dep", "add", changingChild, closingBlocker]);
        target.Run("bd", ["dep", "relate", relatedIssue, relatedControl]);
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
    public async Task IntakeRejectsInvalidRequestsBeforeLaunchingTools()
    {
        using var target = new BeadsApplicationFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var valid = "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}";
        var invalidBodies = new[]
        {
            "{ malformed",
            "null",
            "[]",
            "{}",
            "{\"type\":\"feature\",\"type\":\"bug\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2.5,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":4,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":\"2\",\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":null,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"task\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\" \",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":null,\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"line\\nbreak\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Single\\u2028LineProbe\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Single\\u2029LineProbe\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"bad\\u0000title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\",\"repository\":\"elsewhere\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\",\"labels\":[]}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\",\"assignee\":\"someone\"}"
        };

        var overLimitBodies = new[]
        {
            JsonSerializer.Serialize(new { type = "feature", priority = 2, title = new string('t', 201), description = "Description", acceptanceCriteria = "Criteria" }),
            JsonSerializer.Serialize(new { type = "feature", priority = 2, title = "Title", description = new string('d', 4001), acceptanceCriteria = "Criteria" }),
            JsonSerializer.Serialize(new { type = "feature", priority = 2, title = "Title", description = "Description", acceptanceCriteria = new string('a', 2001) })
        };

        foreach (var body in invalidBodies.Concat(overLimitBodies))
        {
            using var response = await client.PostAsync("/api/beads",
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        using (var missingTitle = await client.PostAsync("/api/beads",
            new StringContent("{\"type\":\"feature\",\"priority\":2,\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken))
        {
            using var problem = JsonDocument.Parse(await missingTitle.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Contains("title", problem.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        }

        using (var query = await client.PostAsync("/api/beads?repository=elsewhere", new StringContent(valid, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);

        using (var contentType = await client.PostAsync("/api/beads", new StringContent(valid, Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, contentType.StatusCode);

        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/beads")
        {
            Content = new StringContent(valid, Encoding.UTF8, "application/json")
        })
        {
            crossOrigin.Headers.TryAddWithoutValidation("Origin", "https://attacker.invalid");
            using var response = await client.SendAsync(crossOrigin, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/beads")
        {
            Content = new StringContent(valid, Encoding.UTF8, "application/json")
        })
        {
            crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            using var response = await client.SendAsync(crossSite, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var tooLarge = new string('x', 64 * 1024 + 1);
        using (var oversized = await client.PostAsync("/api/beads",
            new StringContent(tooLarge, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

        var chunkedBytes = Encoding.UTF8.GetBytes(tooLarge);
        using (var chunked = new HttpRequestMessage(HttpMethod.Post, "/api/beads")
        {
            Content = new UnknownLengthContent(chunkedBytes)
        })
        {
            chunked.Headers.TransferEncodingChunked = true;
            chunked.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(chunked, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task IntakeRejectsInvalidEscapedUnicodeWithoutLeakingOrWriting()
    {
        using var target = new BeadsApplicationFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var inventoryBefore = Snapshot.FileTree(target.Path);
        var invalidBodies = new[]
        {
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"bad\\uD800title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"bad\\uDC00description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"bad\\uD800criteria\"}",
            "{\"type\":\"fe\\uD800ature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\",\"bad\\uD800property\":\"PROSE-MUST-NOT-LEAK\"}",
            "{\"type\":\"feature\",\"priority\":2,\"title\":\"Title\",\"description\":\"Description\",\"acceptanceCriteria\":\"Criteria\",\"bad\\uDC00property\":\"PROSE-MUST-NOT-LEAK\"}"
        };

        foreach (var body in invalidBodies)
        {
            using var response = await client.PostAsync("/api/beads",
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("PROSE-MUST-NOT-LEAK", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("surrogate", problem, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(runner.Requests);
        Assert.Equal(inventoryBefore, Snapshot.FileTree(target.Path));
    }

    [Fact]
    public async Task IntakeAcceptsValidEscapedSurrogatePairUnicode()
    {
        using var target = new BeadsApplicationFixture();
        var actual = new WebBeadsProcessRunner(target.Path);
        var runner = new RecordingProcessRunner(actual.RunAsync);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        const string title = "Keep 🐈 in escaped JSON";
        const string description = "Preserve 🧪 in the issue text.";
        const string acceptance = "The saved issue keeps 😀 intact.";
        var json = JsonSerializer.Serialize(new
        {
            type = "feature",
            priority = 2,
            title,
            description,
            acceptanceCriteria = acceptance
        });

        using var response = await client.PostAsync("/api/beads",
            new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var row = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = row.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(title, row.RootElement.GetProperty("title").GetString());

        using var shown = JsonDocument.Parse(actual.Show(id));
        var issue = shown.RootElement.ValueKind == JsonValueKind.Array
            ? Assert.Single(shown.RootElement.EnumerateArray())
            : shown.RootElement;
        Assert.Equal(title, issue.GetProperty("title").GetString());
        Assert.Equal(description, issue.GetProperty("description").GetString());
        Assert.Equal(acceptance, issue.GetProperty("acceptance_criteria").GetString());
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public async Task IntakeRejectsUnicodeLineSeparatorsInTitle(char separator)
    {
        using var target = new BeadsApplicationFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var json = JsonSerializer.Serialize(new
        {
            type = "feature",
            priority = 2,
            title = $"Single{separator}LineProbe",
            description = "Description",
            acceptanceCriteria = "Criteria"
        });

        using var response = await client.PostAsync("/api/beads",
            new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var errors = problem.RootElement.GetProperty("errors");
        var titleErrors = errors.GetProperty("title").EnumerateArray().Select(element => element.GetString()).ToArray();
        Assert.Contains("Title cannot contain line breaks or control characters.", titleErrors);
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public async Task IntakeAcceptsUnicodeLineSeparatorsInMultilineFields(char separator)
    {
        using var target = new BeadsApplicationFixture();
        var actual = new WebBeadsProcessRunner(target.Path);
        var runner = new RecordingProcessRunner(actual.RunAsync);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var description = $"Line 1{separator}Line 2";
        var acceptance = $"Criteria 1{separator}Criteria 2";
        var json = JsonSerializer.Serialize(new
        {
            type = "feature",
            priority = 2,
            title = "Valid Title",
            description,
            acceptanceCriteria = acceptance
        });

        using var response = await client.PostAsync("/api/beads",
            new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var row = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = row.RootElement.GetProperty("id").GetString()!;

        using var shown = JsonDocument.Parse(actual.Show(id));
        var issue = shown.RootElement.ValueKind == JsonValueKind.Array
            ? Assert.Single(shown.RootElement.EnumerateArray())
            : shown.RootElement;
        Assert.Equal(description, issue.GetProperty("description").GetString());
        Assert.Equal(acceptance, issue.GetProperty("acceptance_criteria").GetString());
    }

    [Theory]
    [InlineData("high-value")]
    [InlineData("low-value")]
    [InlineData("high-property")]
    [InlineData("low-property")]
    public async Task InvalidUnicodeCreateOutputReportsUncertainOutcomeWithoutRetryingOrDeleting(string corruption)
    {
        using var target = new BeadsApplicationFixture();
        var actual = new WebBeadsProcessRunner(target.Path);
        string? createdId = null;
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(request, cancellationToken);
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref createCalls);
                using var created = JsonDocument.Parse(result.StandardOutput);
                createdId = created.RootElement.GetProperty("id").GetString();
                var titleJson = corruption switch
                {
                    "high-value" => "\"bad\\uD800-output\"",
                    "low-value" => "\"bad\\uDC00-output\"",
                    _ => JsonSerializer.Serialize("Invalid Unicode output probe")
                };
                var injectedProperty = corruption switch
                {
                    "high-property" => "\"bad\\uD800-property\":\"private tool output\",",
                    "low-property" => "\"bad\\uDC00-property\":\"private tool output\",",
                    _ => null
                };
                return result with { StandardOutput = CreateCreateOutput(createdId!, titleJson, injectedProperty) };
            }
            return result;
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        const string submittedTitle = "Invalid Unicode output probe";
        var requestJson = JsonSerializer.Serialize(new
        {
            type = "feature",
            priority = 1,
            title = submittedTitle,
            description = "Create once before output parsing fails.",
            acceptanceCriteria = "The real issue remains discoverable."
        });

        using var response = await client.PostAsync("/api/beads", new StringContent(requestJson, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("Creation could not be confirmed", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Check the backlog before resubmitting", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(submittedTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain("private tool output", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Volatile.Read(ref createCalls));
        Assert.NotNull(createdId);
        var shown = actual.Show(createdId!);
        Assert.Contains(submittedTitle, shown, StringComparison.Ordinal);
        Assert.Contains("The real issue remains discoverable.", shown, StringComparison.Ordinal);

        using var ready = JsonDocument.Parse(actual.Inventory());
        Assert.Equal(1, ready.RootElement.EnumerateArray()
            .Count(bead => bead.GetProperty("id").GetString() == createdId));
        using var nextRead = await client.GetAsync("/api/beads?view=ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
    }

    [Fact]
    public async Task CreateOutputAcceptsValidEscapedSurrogatePair()
    {
        using var target = new BeadsApplicationFixture();
        var actual = new WebBeadsProcessRunner(target.Path);
        string? createdId = null;
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(request, cancellationToken);
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref createCalls);
                using var created = JsonDocument.Parse(result.StandardOutput);
                createdId = created.RootElement.GetProperty("id").GetString();
                const string escapedEmoji = "\\uD83D\\uDE00";
                var titleJson = "\"Escaped output " + escapedEmoji + " pair\"";
                return result with { StandardOutput = CreateCreateOutput(createdId!, titleJson) };
            }
            return result;
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        const string title = "Escaped output 😀 pair";
        var requestJson = JsonSerializer.Serialize(new
        {
            type = "feature",
            priority = 1,
            title,
            description = "Keep valid escaped Unicode in create output.",
            acceptanceCriteria = "The paired escape decodes to the requested title."
        });

        using var response = await client.PostAsync("/api/beads",
            new StringContent(requestJson, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var row = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(title, row.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, Volatile.Read(ref createCalls));
        Assert.NotNull(createdId);
        using var saved = JsonDocument.Parse(actual.Show(createdId!));
        Assert.Equal(title, saved.RootElement.GetProperty("title").GetString());

        using var nextRead = await client.GetAsync("/api/beads?view=ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
    }

    [Fact]
    public async Task NonzeroCreateExitReturnsSafeProblemAndLeavesTheHostUsable()
    {
        using var target = new BeadsApplicationFixture();
        const string secret = "private-stderr-marker";
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var actual = new WebBeadsProcessRunner(target.Path);
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal))
            {
                if (Interlocked.Increment(ref createCalls) == 1)
                    return new ProcessResult(23, string.Empty, $"{secret} {target.Path} submitted prose");
            }
            return await actual.RunAsync(request, cancellationToken);
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        const string submittedTitle = "No diagnostic leakage";
        var requestJson = JsonSerializer.Serialize(new
        {
            type = "bug",
            priority = 2,
            title = submittedTitle,
            description = "This request must not be echoed in a failure response.",
            acceptanceCriteria = "Safe errors are shown."
        });

        using var response = await client.PostAsync("/api/beads", new StringContent(requestJson, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("Creation could not be confirmed", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Check the backlog before resubmitting", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(submittedTitle, body, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref createCalls));
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var shell = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        using var nextRead = await client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
        using var nextWrite = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Write after nonzero create"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
        Assert.Equal(2, Volatile.Read(ref createCalls));
    }

    [Theory]
    [InlineData("missing-executable")]
    [InlineData("timeout")]
    public async Task IntakeToolFailuresReportUncertainOutcomeAndReleaseTheTrackerGate(string failure)
    {
        using var target = new BeadsApplicationFixture();
        const string secret = "intake-tool-failure-secret";
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var actual = new WebBeadsProcessRunner(target.Path);
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal)
                && Interlocked.Increment(ref createCalls) == 1)
            {
                if (failure == "missing-executable")
                    throw new ToolExecutionException($"required executable 'bd' was not found at {target.Path}", $"{secret} {target.Path}");
                throw new ToolExecutionException($"bd timed out after 0.01s at {target.Path}", $"{secret} {target.Path}");
            }
            return await actual.RunAsync(request, cancellationToken);
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        const string submittedTitle = "Unconfirmed create must not leak";

        using var response = await client.PostAsync("/api/beads", new StringContent(JsonSerializer.Serialize(new
        {
            type = "feature", priority = 2, title = submittedTitle,
            description = "Private intake description", acceptanceCriteria = "Private acceptance criteria"
        }), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Equal("Creation could not be confirmed", problem.RootElement.GetProperty("title").GetString());
            Assert.Contains("Check the backlog before resubmitting", problem.RootElement.GetProperty("detail").GetString());
        }
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(submittedTitle, body, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref createCalls));
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));

        using var nextWrite = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson($"Write after {failure}"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
        Assert.Equal(2, Volatile.Read(ref createCalls));
    }

    [Fact]
    public async Task InconsistentSuccessfulCreateOutputReportsUncertainOutcomeWithoutDeletingTheIssue()
    {
        using var target = new BeadsApplicationFixture();
        var actual = new WebBeadsProcessRunner(target.Path);
        string? createdId = null;
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(request, cancellationToken);
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal))
            {
                if (Interlocked.Increment(ref createCalls) == 1)
                {
                    using var created = JsonDocument.Parse(result.StandardOutput);
                    createdId = created.RootElement.GetProperty("id").GetString();
                    var inconsistentOutput = JsonNode.Parse(result.StandardOutput)!.AsObject();
                    inconsistentOutput["title"] = "Unexpected title";
                    return result with { StandardOutput = inconsistentOutput.ToJsonString() };
                }
            }
            return result;
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        const string submittedTitle = "Inconsistent create output probe";

        using var response = await client.PostAsync("/api/beads", new StringContent(JsonSerializer.Serialize(new
        {
            type = "bug", priority = 1, title = submittedTitle,
            description = "The real issue should remain discoverable.",
            acceptanceCriteria = "A response mismatch must not delete or retry the create."
        }), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Equal("Creation could not be confirmed", problem.RootElement.GetProperty("title").GetString());
            Assert.Contains("Check the backlog before resubmitting", problem.RootElement.GetProperty("detail").GetString());
        }
        Assert.DoesNotContain(submittedTitle, body, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref createCalls));
        Assert.NotNull(createdId);
        var shown = actual.Show(createdId!);
        Assert.Contains(submittedTitle, shown, StringComparison.Ordinal);
        Assert.Contains("The real issue should remain discoverable.", shown, StringComparison.Ordinal);
        using (var ready = JsonDocument.Parse(actual.Inventory()))
            Assert.Contains(ready.RootElement.EnumerateArray(), bead => bead.GetProperty("id").GetString() == createdId);

        using var nextWrite = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Write after inconsistent response"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
        Assert.Equal(2, Volatile.Read(ref createCalls));
    }

    [Fact]
    public async Task UnpreparedTrackerRejectsPostWithoutMutationAndReleasesTheTrackerGate()
    {
        using var target = new BeadsFixture();
        var gatePath = Path.Combine(target.Path, ".beads.gate.lock");
        var gateContents = File.ReadAllBytes(gatePath);
        File.Delete(gatePath);
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        using var rejected = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Unprepared target must not create"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Equal("Beads tracker unavailable", problem.RootElement.GetProperty("title").GetString());
            Assert.DoesNotContain("Check the backlog before resubmitting", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, request => request.FileName == "bd");
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));

        File.WriteAllBytes(gatePath, gateContents);
        using var nextWrite = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Write after unprepared tracker"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
        Assert.Contains(runner.Requests, request => request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal));
    }

    [Fact]
    public async Task UnsafeTrackerRoutingRejectsPostWithoutTouchingForeignTrackerAndReleasesTheGate()
    {
        using var target = new BeadsFixture("configured target");
        using var foreign = new BeadsFixture("foreign tracker");
        var envPath = Path.Combine(target.Path, ".beads", ".env");
        File.WriteAllText(envPath, $"BEADS_DIR={Path.Combine(foreign.Path, ".beads")}\n");
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var foreignGitBefore = Snapshot.GitStatus(foreign.Path);
        var runner = CreateFixtureRunner(target, foreign);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        using var rejected = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Unsafe routing must not create"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        using (var problem = JsonDocument.Parse(body))
            Assert.Equal("Beads tracker unavailable", problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(foreign.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, request => request.FileName == "bd");
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(foreignGitBefore, Snapshot.GitStatus(foreign.Path));

        File.Delete(envPath);
        using var nextWrite = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Write after unsafe routing"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(foreignGitBefore, Snapshot.GitStatus(foreign.Path));
    }

    [Fact]
    public async Task ConcurrentIntakeWritesShareTheTrackerGate()
    {
        using var target = new BeadsFixture();
        var actual = new SystemProcessRunner();
        var firstCreateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCount = 0;
        var activeCalls = 0;
        var maximumActiveCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(ref maximumActiveCalls, active);
            try
            {
                if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal)
                    && Interlocked.Increment(ref createCount) == 1)
                {
                    firstCreateStarted.SetResult();
                    await releaseFirstCreate.Task.WaitAsync(cancellationToken);
                }
                return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var first = CreateIntakeJson("First concurrent Product Bead");
        var second = CreateIntakeJson("Second concurrent Product Bead");
        var firstTask = client.PostAsync("/api/beads", new StringContent(first, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await firstCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var secondTask = client.PostAsync("/api/beads", new StringContent(second, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var readTask = client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(secondTask.IsCompleted);
        Assert.False(readTask.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref createCount));

        releaseFirstCreate.SetResult();
        using var firstResponse = await firstTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var secondResponse = await secondTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var readResponse = await readTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(2, Volatile.Read(ref createCount));
        Assert.Equal(1, Volatile.Read(ref maximumActiveCalls));
    }

    [Fact]
    public async Task CancellingAnIntakeWaitingForTheSharedGateDoesNotLaunchOrLeakTheWrite()
    {
        using var target = new BeadsFixture();
        var actual = new SystemProcessRunner();
        var firstCreateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCount = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal))
            {
                if (Interlocked.Increment(ref createCount) == 1)
                {
                    firstCreateStarted.SetResult();
                    await releaseFirstCreate.Task.WaitAsync(cancellationToken);
                }
            }
            return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        var firstTask = client.PostAsync("/api/beads", new StringContent(CreateIntakeJson("Gate holder"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        await firstCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        using var cancelWaitingRequest = new CancellationTokenSource();
        var waitingTask = client.PostAsync("/api/beads", new StringContent(CreateIntakeJson("Cancelled waiter"), Encoding.UTF8, "application/json"),
            cancelWaitingRequest.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        cancelWaitingRequest.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask);
        Assert.Equal(1, Volatile.Read(ref createCount));

        releaseFirstCreate.SetResult();
        using var firstResponse = await firstTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        using var nextWrite = await client.PostAsync("/api/beads", new StringContent(CreateIntakeJson("After cancellation"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
        using var nextRead = await client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
        Assert.Equal(2, Volatile.Read(ref createCount));
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
        using var target = new BeadsApplicationFixture();
        using var foreign = new BeadsApplicationFixture();
        const string secret = "raw-stderr-secret";
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var foreignGitBefore = Snapshot.GitStatus(foreign.Path);
        var actual = new WebBeadsProcessRunner(target.Path);
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName != "bd")
                return await actual.RunAsync(request, cancellationToken);
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
        using var target = new BeadsApplicationFixture();
        var metadataPath = Path.Combine(target.Path, ".beads", "metadata.json");
        var validMetadata = File.ReadAllText(metadataPath);
        File.WriteAllText(metadataPath, metadataJson);
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

        using var rejectedIntake = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Malformed metadata must not create"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var intakeBody = await rejectedIntake.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedIntake.StatusCode);
        Assert.Equal("application/problem+json", rejectedIntake.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", rejectedIntake.Headers.CacheControl?.ToString());
        using (var intakeProblem = JsonDocument.Parse(intakeBody))
        {
            Assert.Equal("Incompatible Beads output", intakeProblem.RootElement.GetProperty("title").GetString());
            Assert.Equal("The bd command returned data that Nightweaver could not read.", intakeProblem.RootElement.GetProperty("detail").GetString());
        }
        Assert.DoesNotContain("Check the backlog before resubmitting", intakeBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, intakeBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Requests, request => request.FileName == "bd");

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var shell = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(targetGitBefore, Snapshot.GitStatus(target.Path));
        Assert.Equal(hostTreeBefore, Snapshot.FileTree(factory.HostRoot));
        Assert.Equal(webTreeBefore, Snapshot.FileTree(factory.WebRoot));

        File.WriteAllText(metadataPath, validMetadata);
        using var nextWrite = await client.PostAsync("/api/beads",
            new StringContent(CreateIntakeJson("Write after malformed metadata"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, nextWrite.StatusCode);
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
        using var target = new BeadsApplicationFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();

        var response = await GetRowsAsync(client, "/api/beads");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Empty(response.Rows);
    }

    private sealed record ExplorerSeed(string Title, string Type = "task", string Status = "open",
        string? Assignee = null, int Priority = 2, IReadOnlyList<string>? Labels = null);
    [Fact]
    public async Task AttentionDeskProjectsPausedInventoryAndValidatedCommentContext()
    {
        using var target = new BeadsFixture("configured target");
        using var foreign = new BeadsFixture("foreign target");
        var decision = target.Create("Planner choice", type: "feature", priority: 0);
        target.Run("bd", ["update", decision, "--add-label", "stage:plan", "--add-label", "product-decision-required"]);
        var question = "Choose <safe> or <fast> for café ☕?";
        target.Run("bd", ["--actor", "Planner Agent", "comments", "add", decision, question]);

        var needsHuman = target.Create("Review repair exhausted", type: "task", priority: 1);
        target.Run("bd", ["update", needsHuman, "--add-label", "stage:review", "--add-label", "needs-human",
            "--set-metadata", "repair_cycles=3", "--set-metadata", "keep=unchanged"]);
        var diagnostic = JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-v1",
            bead_id = needsHuman,
            bead_type = "task",
            stage = "stage:review",
            cause = "repair-budget-exhausted",
            failed_role = "reviewer",
            reason = "A required check failed.",
            latest_findings = "Literal <b>failure</b> detail.",
            invocation_id = "fixture-invocation",
            log_directory = "C:\\private-log-directory-not-to-read",
            log_files = ExpectedDiagnosticLogs,
            cleanup_preflight = "passed"
        });
        target.Run("bd", ["--actor", "Reviewer Agent", "comments", "add", needsHuman, diagnostic]);
        await Task.Delay(TimeSpan.FromSeconds(1.1), TestContext.Current.CancellationToken);
        var cleanup = JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-cleanup-failure-v1",
            bead_id = needsHuman,
            failure = "The owned worktree was retained for inspection."
        });
        target.Run("bd", ["comments", "add", needsHuman, cleanup]);
        await Task.Delay(TimeSpan.FromSeconds(1.1), TestContext.Current.CancellationToken);
        var malformedDiagnostic = JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-v1",
            bead_id = needsHuman,
            bead_type = "task",
            stage = 42,
            cause = "repair-budget-exhausted",
            failed_role = "reviewer",
            reason = "This malformed diagnostic must not replace the valid one.",
            latest_findings = "Malformed stage field.",
            invocation_id = "invalid-fixture-invocation",
            log_directory = "not-read/invalid",
            log_files = ExpectedDiagnosticLogs,
            cleanup_preflight = "passed"
        });
        target.Run("bd", ["comments", "add", needsHuman, malformedDiagnostic]);

        var missingDiagnostic = target.Create("Missing escalation details", priority: 2);
        target.Run("bd", ["update", missingDiagnostic, "--add-label", "needs-human"]);
        var active = target.Create("Claimed pause", actor: "nightweaver:web-test", status: "in_progress", priority: 0);
        target.Run("bd", ["update", active, "--add-label", "stage:implement", "--add-label", "needs-human"]);
        var malformed = target.Create("Malformed workflow", priority: 0);
        target.Run("bd", ["update", malformed, "--add-label", "stage:implement", "--add-label", "stage:review",
            "--add-label", "needs-human", "--set-metadata", "repair_cycles=broken"]);
        var bothPauses = target.Create("Contradictory pauses", priority: 1);
        target.Run("bd", ["update", bothPauses, "--add-label", "stage:qa", "--add-label", "needs-human",
            "--add-label", "product-decision-required"]);
        var closed = target.Create("Closed pause", status: "closed", priority: 0);
        target.Run("bd", ["update", closed, "--add-label", "needs-human"]);
        for (var index = 0; index < 52; index++) target.Create($"Ordinary row {index:D2}");
        var foreignPause = foreign.Create("Foreign pause");

        var runner = CreateFixtureRunner(target, foreign);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/attention", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
        Assert.True(rows.Length > 3);
        Assert.DoesNotContain(rows, row => row.GetProperty("beadId").GetString() == closed);
        Assert.DoesNotContain(rows, row => row.GetProperty("beadId").GetString() == foreignPause);
        Assert.Equal(rows.OrderBy(row => row.GetProperty("priority").GetInt32())
                .ThenBy(row => row.GetProperty("pausedAt").ValueKind == JsonValueKind.Null ? 1 : 0)
                .ThenBy(row => row.GetProperty("pausedAt").ValueKind == JsonValueKind.String
                    ? row.GetProperty("pausedAt").GetDateTimeOffset() : DateTimeOffset.MaxValue)
                .ThenBy(row => row.GetProperty("beadId").GetString(), StringComparer.Ordinal)
                .Select(row => row.GetProperty("beadId").GetString()),
            rows.Select(row => row.GetProperty("beadId").GetString()));

        foreach (var row in rows)
            Assert.Equal(ExpectedAttentionFields.Order(StringComparer.Ordinal),
                row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        var decisionRow = FindAttention(rows, decision);
        Assert.Equal("product-decision-required", decisionRow.GetProperty("kind").GetString());
        Assert.Equal(question, decisionRow.GetProperty("question").GetString());
        Assert.Equal("Planner Agent", decisionRow.GetProperty("sourceAuthor").GetString());
        Assert.Equal("planner", decisionRow.GetProperty("fromRole").GetString());
        Assert.Equal("comment", decisionRow.GetProperty("pauseTimeSource").GetString());
        Assert.Equal(3, decisionRow.GetProperty("maxRepairCycles").GetInt32());
        Assert.True(decisionRow.GetProperty("canDecide").GetBoolean());

        var needsHumanRow = FindAttention(rows, needsHuman);
        Assert.Equal("needs-human", needsHumanRow.GetProperty("kind").GetString());
        Assert.Equal("reviewer", needsHumanRow.GetProperty("fromRole").GetString());
        var projectedDiagnostic = needsHumanRow.GetProperty("diagnostic").GetString()!;
        Assert.Contains("Cause: repair-budget-exhausted", projectedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Reason: A required check failed.", projectedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Literal <b>failure</b> detail.", projectedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("C:\\not-read\\review.log", projectedDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Cleanup warning: The owned worktree was retained for inspection.", projectedDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("This malformed diagnostic must not replace the valid one.", projectedDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private-log-directory-not-to-read", projectedDiagnostic, StringComparison.Ordinal);
        Assert.Equal(3, needsHumanRow.GetProperty("repairCycles").GetInt32());

        var activeRow = FindAttention(rows, active);
        Assert.Equal("in_progress", activeRow.GetProperty("status").GetString());
        Assert.False(activeRow.GetProperty("canRetry").GetBoolean());
        Assert.False(activeRow.GetProperty("canAbort").GetBoolean());
        Assert.NotNull(activeRow.GetProperty("unavailableReason").GetString());

        var malformedRow = FindAttention(rows, malformed);
        Assert.Equal("invalid-workflow", malformedRow.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, malformedRow.GetProperty("repairCycles").ValueKind);
        Assert.False(malformedRow.GetProperty("canRetry").GetBoolean());
        var bothRow = FindAttention(rows, bothPauses);
        Assert.Equal("invalid-workflow", bothRow.GetProperty("kind").GetString());
        Assert.False(bothRow.GetProperty("canDecide").GetBoolean());
        var fallbackRow = FindAttention(rows, missingDiagnostic);
        Assert.Contains("no valid matching escalation diagnostic", fallbackRow.GetProperty("diagnostic").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, fallbackRow.GetProperty("pausedAt").ValueKind);
        Assert.Equal("unknown", fallbackRow.GetProperty("pauseTimeSource").GetString());
    }

    [Fact]
    public async Task AttentionDeskRetainsCleanupWarningWhenTimestampTiesAndIdSortsLater()
    {
        using var target = new BeadsFixture();
        var beadId = target.Create("Tied cleanup diagnostic", type: "task");
        target.Run("bd", ["update", beadId, "--add-label", "stage:review", "--add-label", "needs-human"]);
        var diagnostic = JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-v1",
            bead_id = beadId,
            bead_type = "task",
            stage = "stage:review",
            cause = "repair-budget-exhausted",
            failed_role = "reviewer",
            reason = "The reviewer findings remain unresolved.",
            latest_findings = "A test still fails.",
            invocation_id = "fixture-review-tie",
            log_directory = "C:\\nightweaver\\logs\\fixture",
            log_files = ExpectedDiagnosticLogs,
            cleanup_preflight = "passed"
        });
        var cleanup = JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-cleanup-failure-v1",
            bead_id = beadId,
            failure = "The owned worktree was retained for inspection."
        });
        target.Run("bd", ["comments", "add", beadId, diagnostic]);
        target.Run("bd", ["comments", "add", beadId, cleanup]);

        var actual = new SystemProcessRunner();
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
            if (result.ExitCode != 0 || !request.Arguments.Contains("comments", StringComparer.Ordinal)
                || !request.Arguments.Contains(beadId, StringComparer.Ordinal))
                return result;

            var rows = JsonNode.Parse(result.StandardOutput)!.AsArray();
            var diagnosticRow = Assert.Single(rows, row => row?["text"]?.GetValue<string>() == diagnostic);
            var cleanupRow = Assert.Single(rows, row => row?["text"]?.GetValue<string>() == cleanup);
            diagnosticRow!["id"] = "a-diagnostic";
            cleanupRow!["id"] = "b-cleanup";
            diagnosticRow["created_at"] = "2026-10-27T00:00:00Z";
            cleanupRow["created_at"] = "2026-10-27T00:00:00Z";
            return result with { StandardOutput = rows.ToJsonString() };
        });

        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var row = FindAttention(await GetAttentionRowsAsync(client), beadId);
        Assert.Contains("Cleanup warning: The owned worktree was retained for inspection.",
            row.GetProperty("diagnostic").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttentionDeskAcceptsAuthenticInitialEscalationsWithoutAStageLabel()
    {
        using var target = new BeadsFixture();
        var planner = target.Create("Unlabeled planner escalation", type: "feature");
        var implementer = target.Create("Unlabeled implementer escalation", type: "task");
        target.Run("bd", ["update", planner, "--add-label", "needs-human"]);
        target.Run("bd", ["update", implementer, "--add-label", "needs-human"]);

        string Diagnostic(string id, string type, string role) => JsonSerializer.Serialize(new
        {
            schema = "nightweaver-needs-human-v1",
            bead_id = id,
            bead_type = type,
            stage = "(none)",
            cause = "operational-retries-exhausted",
            failed_role = role,
            reason = $"The initial {role} invocation failed before a lifecycle stage was assigned.",
            latest_findings = "The failure is available for human review.",
            invocation_id = $"fixture-{role}-initial",
            log_directory = "C:\\nightweaver\\logs\\fixture",
            log_files = InitialEscalationLogFiles,
            cleanup_preflight = "passed"
        });

        target.Run("bd", ["--actor", "Planner Agent", "comments", "add", planner, Diagnostic(planner, "feature", "planner")]);
        target.Run("bd", ["--actor", "Implementer Agent", "comments", "add", implementer, Diagnostic(implementer, "task", "implementer")]);

        using var factory = CreateFactory(target, CreateFixtureRunner(target));
        using var client = factory.CreateClient();
        var rows = await GetAttentionRowsAsync(client);

        foreach (var (id, role) in new[] { (planner, "planner"), (implementer, "implementer") })
        {
            var row = FindAttention(rows, id);
            Assert.Equal("needs-human", row.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("stage").ValueKind);
            Assert.Equal(role, row.GetProperty("fromRole").GetString());
            Assert.Contains($"The initial {role} invocation failed", row.GetProperty("diagnostic").GetString(), StringComparison.Ordinal);
            Assert.True(row.GetProperty("canRetry").GetBoolean());
        }
    }

    [Fact]
    public async Task AttentionGuidanceUsesFileTransportForAcceptedLargeLiteralText()
    {
        using var target = new BeadsFixture();
        var beadId = target.Create("Large guidance", type: "feature");
        target.Run("bd", ["update", beadId, "--add-label", "product-decision-required"]);
        const int literalCount = 6000;
        var instruction = new string('<', literalCount);
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var rawBody = "{\"instruction\":\"" + instruction + "\",\"action\":\"resume\"}";

        using var request = CreateAttentionRequest(client, $"/api/beads/{beadId}/decide", rawBody);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Guidance response: {responseBody}");
        Assert.Contains(ReadComments(target, beadId), comment => comment.GetProperty("text").GetString() is { } text
            && GuidanceMatches(text, "resume", instruction));
        Assert.Contains(runner.Requests, process => process.Arguments.Contains("comments", StringComparer.Ordinal)
            && process.Arguments.Contains("-f", StringComparer.Ordinal)
            && process.Arguments.All(argument => !argument.Contains(instruction, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AttentionDecisionActionsPreserveWorkflowAndRecordExactGuidance()
    {
        using var target = new BeadsFixture("configured target");
        using var foreign = new BeadsFixture("foreign target");
        var parent = target.Create("Product parent", type: "feature");
        var feature = target.Create("Product decision", type: "feature", priority: 1);
        target.Run("bd", ["dep", "add", feature, parent]);
        target.Run("bd", ["update", feature, "--add-label", "stage:plan", "--add-label", "product-decision-required",
            "--add-label", "keep-this", "--set-metadata", "repair_cycles=2", "--set-metadata", "owner=human"]);
        target.Run("bd", ["--actor", "Planner Agent", "comments", "add", feature, "Which endpoint should the plan use?"]);
        var task = target.Create("Plan decision", type: "task", priority: 2);
        target.Run("bd", ["update", task, "--add-label", "stage:review", "--add-label", "product-decision-required"]);
        target.Run("bd", ["--actor", "Reviewer Agent", "comments", "add", task, "May this add a dependency?"]);

        var runner = CreateFixtureRunner(target, foreign);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var instruction = "  Use the existing route. café ☕\nKeep <markup> as literal text.  ";

        using (var noteRequest = CreateAttentionRequest(client, $"/api/beads/{feature}/decide",
                   JsonSerializer.Serialize(new { instruction, action = "note_only" })))
        using (var noteResponse = await client.SendAsync(noteRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, noteResponse.StatusCode);
            Assert.Equal("no-store", noteResponse.Headers.CacheControl?.ToString());
            using var result = JsonDocument.Parse(await noteResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(result.RootElement.GetProperty("resolved").GetBoolean());
            Assert.True(result.RootElement.GetProperty("commentRecorded").GetBoolean());
            Assert.False(result.RootElement.GetProperty("stateChanged").GetBoolean());
        }

        var shownAfterNote = ReadShownBead(target, feature);
        Assert.Contains("product-decision-required", shownAfterNote.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal(2, shownAfterNote.GetProperty("metadata").GetProperty("repair_cycles").GetInt32());
        var commentsAfterNote = ReadComments(target, feature);
        Assert.Contains(commentsAfterNote, comment => comment.GetProperty("text").GetString() is { } text
            && GuidanceMatches(text, "note_only", instruction.Trim()));

        using (var resumeRequest = CreateAttentionRequest(client, $"/api/beads/{feature}/decide",
                   JsonSerializer.Serialize(new { instruction, action = "resume" })))
        using (var resumeResponse = await client.SendAsync(resumeRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);
            using var result = JsonDocument.Parse(await resumeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(result.RootElement.GetProperty("resolved").GetBoolean());
            Assert.True(result.RootElement.GetProperty("commentRecorded").GetBoolean());
            Assert.True(result.RootElement.GetProperty("stateChanged").GetBoolean());
        }

        var shownAfterResume = ReadShownBead(target, feature);
        var featureLabels = shownAfterResume.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ToArray();
        Assert.Contains("stage:plan", featureLabels);
        Assert.Contains("keep-this", featureLabels);
        Assert.DoesNotContain("product-decision-required", featureLabels);
        Assert.False(shownAfterResume.GetProperty("metadata").TryGetProperty("repair_cycles", out _));
        Assert.Equal("human", shownAfterResume.GetProperty("metadata").GetProperty("owner").GetString());
        using var featureDeps = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "dep", "list", feature,
            "--direction", "down", "--json"]));
        Assert.Contains(featureDeps.RootElement.EnumerateArray(), edge => edge.GetProperty("id").GetString() == parent);

        using (var taskRequest = CreateAttentionRequest(client, $"/api/beads/{task}/decide",
                   JsonSerializer.Serialize(new { instruction = "Approve this plan.", action = "resume" }), includeOrigin: false))
        using (var taskResponse = await client.SendAsync(taskRequest, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, taskResponse.StatusCode);
        var taskAfter = ReadShownBead(target, task);
        Assert.Contains("stage:review", taskAfter.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.DoesNotContain("product-decision-required", taskAfter.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
    }

    [Fact]
    public async Task AttentionRetryResetsBudgetAndAbortClosesWithoutRemovingHistory()
    {
        using var target = new BeadsFixture("configured target");
        var retry = target.Create("Retry this task", type: "task", priority: 1);
        target.Run("bd", ["update", retry, "--add-label", "stage:qa", "--add-label", "needs-human", "--add-label", "keep-label",
            "--set-metadata", "repair_cycles=3", "--set-metadata", "keep=untouched"]);
        var abort = target.Create("Abort this task", type: "task", priority: 2);
        target.Run("bd", ["update", abort, "--add-label", "stage:merge", "--add-label", "needs-human",
            "--set-metadata", "repair_cycles=2", "--set-metadata", "keep=untouched"]);

        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        using (var retryRequest = CreateAttentionRequest(client, $"/api/beads/{retry}/retry"))
        using (var retryResponse = await client.SendAsync(retryRequest, TestContext.Current.CancellationToken))
        {
            var retryBody = await retryResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(retryResponse.StatusCode == HttpStatusCode.OK, $"Retry response: {retryBody}");
            using var result = JsonDocument.Parse(retryBody);
            Assert.Equal("retry", result.RootElement.GetProperty("action").GetString());
            Assert.True(result.RootElement.GetProperty("resolved").GetBoolean());
        }

        var retryAfter = ReadShownBead(target, retry);
        var retryLabels = retryAfter.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ToArray();
        Assert.Contains("stage:qa", retryLabels);
        Assert.Contains("keep-label", retryLabels);
        Assert.DoesNotContain("needs-human", retryLabels);
        Assert.False(retryAfter.GetProperty("metadata").TryGetProperty("repair_cycles", out _));
        Assert.Equal("untouched", retryAfter.GetProperty("metadata").GetProperty("keep").GetString());
        Assert.Contains(ReadComments(target, retry), comment => comment.GetProperty("text").GetString() is { } text
            && ActionMatches(text, "retry"));

        using (var abortRequest = CreateAttentionRequest(client, $"/api/beads/{abort}/abort", "{}"))
        using (var abortResponse = await client.SendAsync(abortRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, abortResponse.StatusCode);
            using var result = JsonDocument.Parse(await abortResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("abort", result.RootElement.GetProperty("action").GetString());
            Assert.True(result.RootElement.GetProperty("resolved").GetBoolean());
        }

        var abortAfter = ReadShownBead(target, abort);
        Assert.Equal("closed", abortAfter.GetProperty("status").GetString());
        Assert.Contains("needs-human", abortAfter.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal(2, abortAfter.GetProperty("metadata").GetProperty("repair_cycles").GetInt32());
        Assert.Contains(ReadComments(target, abort), comment => comment.GetProperty("text").GetString() is { } text
            && ActionMatches(text, "abort"));
        var attention = await GetAttentionRowsAsync(client);
        Assert.DoesNotContain(attention, row => row.GetProperty("beadId").GetString() == abort);
    }

    [Fact]
    public async Task AttentionRejectsUnsafeRequestsBeforeLaunchingTrackerCommands()
    {
        using var target = new BeadsFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var invalidBodies = new[]
        {
            "{ malformed",
            "null",
            "[]",
            "{}",
            "{\"instruction\":\"hello\",\"action\":\"resume\",\"extra\":1}",
            "{\"instruction\":\"one\",\"instruction\":\"two\",\"action\":\"resume\"}",
            "{\"instruction\":3,\"action\":\"resume\"}",
            "{\"instruction\":\"hello\",\"action\":\"restart\"}",
            "{\"instruction\":\"   \",\"action\":\"resume\"}"
        };
        foreach (var body in invalidBodies)
        {
            using var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", body);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        var largeUnicode = JsonSerializer.Serialize(new { instruction = new string('x', 17 * 1024), action = "resume" });
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", largeUnicode))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var oversized = JsonSerializer.Serialize(new { instruction = new string('x', 33 * 1024), action = "resume" });
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", oversized))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", "{}", includeHeader: false))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", "{}", origin: "https://attacker.invalid"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", "{}", origin: "null"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", "{}"))
        {
            request.Headers.Host = "attacker.invalid";
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", "{}"))
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/decide", "{}", contentType: "text/plain"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/retry", "{\"force\":true}"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var request = CreateAttentionRequest(client, "/api/beads/fixture-unsafe/abort?target=other", "{}"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task AttentionReportsPartialCommentAndUpdateFailuresAndKeepsPauseVisible()
    {
        using var target = new BeadsFixture();
        var beadId = target.Create("Partial update", type: "feature");
        target.Run("bd", ["update", beadId, "--add-label", "stage:plan", "--add-label", "product-decision-required"]);
        target.Run("bd", ["comments", "add", beadId, "Should the retry preserve the route?"]);
        var actual = new SystemProcessRunner();
        var failFinalUpdate = 1;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var args = request.Arguments;
            var updateIndex = args.ToList().IndexOf("update");
            if (updateIndex >= 0 && args.Contains("--remove-label") && Interlocked.Exchange(ref failFinalUpdate, 0) == 1)
                return new ProcessResult(1, string.Empty, "simulated final update failure");
            return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        using var request = CreateAttentionRequest(client, $"/api/beads/{beadId}/decide",
            JsonSerializer.Serialize(new { instruction = "Continue with route A.", action = "resume" }));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(problem.RootElement.GetProperty("commentRecorded").GetBoolean());
        Assert.False(problem.RootElement.GetProperty("stateChanged").GetBoolean());
        Assert.Contains("Refresh", problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        var shown = ReadShownBead(target, beadId);
        Assert.Contains("product-decision-required", shown.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Contains(ReadComments(target, beadId), comment => comment.GetProperty("text").GetString() is { } text
            && GuidanceMatches(text, "resume", "Continue with route A."));
    }

    [Fact]
    public async Task AttentionReportsGuardConflictAfterRecordingTheDecision()
    {
        using var target = new BeadsFixture();
        var beadId = target.Create("Guarded decision", type: "feature");
        target.Run("bd", ["update", beadId, "--add-label", "stage:plan", "--add-label", "product-decision-required"]);
        target.Run("bd", ["comments", "add", beadId, "Should the plan keep its current route?"]);
        var actual = new SystemProcessRunner();
        var changeBeforeUpdate = 1;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var args = request.Arguments;
            if (args.ToList().IndexOf("update") >= 0 && args.Contains("--remove-label")
                && Interlocked.Exchange(ref changeBeforeUpdate, 0) == 1)
                target.Run("bd", ["update", beadId, "--status", "in_progress"]);
            return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
        });
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        using var request = CreateAttentionRequest(client, $"/api/beads/{beadId}/decide",
            JsonSerializer.Serialize(new { instruction = "Keep the current route.", action = "resume" }));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(problem.RootElement.GetProperty("commentRecorded").GetBoolean());
        Assert.True(problem.RootElement.GetProperty("stateChanged").GetBoolean());
        var shown = ReadShownBead(target, beadId);
        Assert.Equal("in_progress", shown.GetProperty("status").GetString());
        Assert.Contains("product-decision-required", shown.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Contains(ReadComments(target, beadId), comment => comment.GetProperty("text").GetString() is { } text
            && GuidanceMatches(text, "resume", "Keep the current route."));
    }

    private static JsonElement FindAttention(IEnumerable<JsonElement> rows, string id)
        => Assert.Single(rows, row => row.GetProperty("beadId").GetString() == id);

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
