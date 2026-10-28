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

        using var temp = new TempDirectory();
        var records = Enumerable.Range(0, 53).Select(index => new
        {
            title = $"Ordinary record {index:D2}",
            issue_type = "task",
            status = "open",
            priority = index % 5,
            created_at = "2025-01-01T00:00:00Z",
            labels = Array.Empty<string>()
        }).ToArray();
        var recordsFile = temp.WriteFile("ordinary-records.jsonl", string.Join('\n', records.Select(r => JsonSerializer.Serialize(r))));
        using (JsonDocument.Parse(target.Run("bd", ["import", recordsFile, "--json"]))) { }
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

                using var shownDocument = JsonDocument.Parse(target.Run("bd", ["show", id, "--json"]));
                var shown = shownDocument.RootElement.ValueKind == JsonValueKind.Array
                    ? Assert.Single(shownDocument.RootElement.EnumerateArray())
                    : shownDocument.RootElement;
                Assert.Equal(id, shown.GetProperty("id").GetString());
                Assert.Equal(title, shown.GetProperty("title").GetString());
                Assert.Equal(description.Trim(), shown.GetProperty("description").GetString());
                Assert.Equal(acceptance.Trim(), shown.GetProperty("acceptance_criteria").GetString());
                Assert.Equal(type, shown.GetProperty("issue_type").GetString());
                Assert.Equal(priority, shown.GetProperty("priority").GetInt32());
                Assert.Equal("open", shown.GetProperty("status").GetString());
                foreach (var direction in new[] { "up", "down" })
                {
                    using var dependencies = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "dep", "list", id,
                        "--direction", direction, "--json"]));
                    Assert.Equal(JsonValueKind.Array, dependencies.RootElement.ValueKind);
                    Assert.Empty(dependencies.RootElement.EnumerateArray());
                }
                using var comments = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "comments", id, "--json"]));
                Assert.Empty(comments.RootElement.EnumerateArray());
            }
        }

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
        using var target = new BeadsFixture();
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
        using var target = new BeadsFixture();
        var runner = CreateFixtureRunner(target);
        using var factory = CreateFactory(target, runner);
        using var client = factory.CreateClient();
        var inventoryBefore = target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0",
            "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"]);
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
        Assert.Equal(inventoryBefore, target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0",
            "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"]));
    }

    [Fact]
    public async Task IntakeAcceptsValidEscapedSurrogatePairUnicode()
    {
        using var target = new BeadsFixture();
        var runner = CreateFixtureRunner(target);
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

        using var shown = JsonDocument.Parse(target.Run("bd", ["show", id, "--json"]));
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
        using var target = new BeadsFixture();
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
        using var target = new BeadsFixture();
        var runner = CreateFixtureRunner(target);
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

        using var shown = JsonDocument.Parse(target.Run("bd", ["show", id, "--json"]));
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
        using var target = new BeadsFixture();
        var actual = new SystemProcessRunner();
        string? createdId = null;
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
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
        var shown = target.Run("bd", ["show", createdId!]);
        Assert.Contains(submittedTitle, shown, StringComparison.Ordinal);
        Assert.Contains("The real issue remains discoverable.", shown, StringComparison.Ordinal);

        using var ready = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "ready", "--limit", "0", "--json"]));
        Assert.Equal(1, ready.RootElement.EnumerateArray()
            .Count(bead => bead.GetProperty("id").GetString() == createdId));
        using var nextRead = await client.GetAsync("/api/beads?view=ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
    }

    [Fact]
    public async Task CreateOutputAcceptsValidEscapedSurrogatePair()
    {
        using var target = new BeadsFixture();
        var actual = new SystemProcessRunner();
        string? createdId = null;
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
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
        Assert.Contains(title, target.Run("bd", ["show", createdId!]), StringComparison.Ordinal);

        using var nextRead = await client.GetAsync("/api/beads?view=ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextRead.StatusCode);
    }

    [Fact]
    public async Task NonzeroCreateExitReturnsSafeProblemAndLeavesTheHostUsable()
    {
        using var target = new BeadsFixture();
        const string secret = "private-stderr-marker";
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var actual = new SystemProcessRunner();
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName == "bd" && request.Arguments.Contains("create", StringComparer.Ordinal))
            {
                if (Interlocked.Increment(ref createCalls) == 1)
                    return new ProcessResult(23, string.Empty, $"{secret} {target.Path} submitted prose");
            }
            return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
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
        using var target = new BeadsFixture();
        const string secret = "intake-tool-failure-secret";
        var targetTreeBefore = Snapshot.FileTree(target.Path);
        var targetGitBefore = Snapshot.GitStatus(target.Path);
        var actual = new SystemProcessRunner();
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
            return await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
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
        using var target = new BeadsFixture();
        var actual = new SystemProcessRunner();
        string? createdId = null;
        var createCalls = 0;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            var result = await actual.RunAsync(AddFixtureEnvironment(target, request), cancellationToken);
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
        var shown = target.Run("bd", ["show", createdId!, "--json"]);
        Assert.Contains(submittedTitle, shown, StringComparison.Ordinal);
        Assert.Contains("The real issue should remain discoverable.", shown, StringComparison.Ordinal);
        using (var ready = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "ready", "--limit", "0", "--json"])))
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
