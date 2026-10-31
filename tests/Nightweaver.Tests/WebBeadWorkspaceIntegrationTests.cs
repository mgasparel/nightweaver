using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WebBeadWorkspaceIntegrationTests
{
    [Fact]
    public async Task WorkspaceReturnsLosslessDetailOrderedHistoryAndHonestAbsentEnvironment()
    {
        using var target = new BeadsFixture("workspace target");
        const string title = "Workspace café — 📘";
        var description = "# Description\n\n" + new string('é', 1300);
        var design = "## Complete plan\n\n" + new string('界', 1700);
        var acceptance = "- [ ] Unicode ✓\n" + new string('a', 900);
        var notes = "Notes remain separate.\n" + new string('n', 300);
        var beadId = target.Create(title, type: "feature", priority: 4,
            labels: ["product-decision-required", "stage:plan"]);
        target.Run("bd", ["update", beadId, "--description", description, "--design", design,
            "--acceptance", acceptance, "--notes", notes]);
        var dependencyId = target.Create("Workspace prerequisite");
        target.Run("bd", ["dep", "add", beadId, dependencyId]);
        target.Run("bd", ["comments", "add", beadId, "Older planning context"]);
        target.Run("bd", ["comments", "add", beadId, "Which option should we use?"]);

        using var factory = CreateFactory(target);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/beads/{Uri.EscapeDataString(beadId)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var bead = root.GetProperty("bead");
        Assert.Equal(title, bead.GetProperty("title").GetString());
        Assert.Equal("feature", bead.GetProperty("type").GetString());
        Assert.Equal(4, bead.GetProperty("priority").GetInt32());
        Assert.Equal(description, bead.GetProperty("description").GetString());
        Assert.Equal(design, bead.GetProperty("design").GetString());
        Assert.Equal(acceptance, bead.GetProperty("acceptanceCriteria").GetString());
        Assert.Equal(notes, bead.GetProperty("notes").GetString());
        Assert.DoesNotContain(root.EnumerateObject(), property => property.Name is "payload" or "owner");
        Assert.Contains(root.GetProperty("relations").EnumerateArray(), relation =>
            relation.GetProperty("id").GetString() == dependencyId);

        var comments = root.GetProperty("comments").EnumerateArray().ToArray();
        Assert.Equal("Older planning context", comments[^2].GetProperty("body").GetString());
        Assert.Equal("Which option should we use?", comments[^1].GetProperty("body").GetString());
        Assert.Equal("ordinary", comments[^2].GetProperty("kind").GetString());
        Assert.Equal("legacy-context", root.GetProperty("history").GetProperty("selectionSource").GetString());
        var activeCommentId = comments[^1].GetProperty("id").GetString();
        Assert.Equal(activeCommentId, root.GetProperty("history").GetProperty("activeCommentId").GetString());
        Assert.Contains(activeCommentId, root.GetProperty("history").GetProperty("recentCommentIds")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Which option should we use?", root.GetProperty("attention").GetProperty("question").GetString());

        var environment = root.GetProperty("environment");
        Assert.Equal("absent", environment.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, environment.GetProperty("worktreePath").ValueKind);
        Assert.Equal(JsonValueKind.Null, environment.GetProperty("processId").ValueKind);
        Assert.Equal("unavailable", environment.GetProperty("processState").GetString());
    }

    [Fact]
    public async Task WorkspaceGuidanceNoteKeepsActiveQuestionAndResumeClearsOnlyPause()
    {
        using var target = new BeadsFixture("workspace actions target");
        var beadId = target.Create("Workspace decision", type: "task", labels: ["product-decision-required", "stage:plan"]);
        target.Run("bd", ["comments", "add", beadId, "Choose the safer option."]);
        using var factory = CreateFactory(target);
        using var client = factory.CreateClient();

        using (var noteRequest = CreateGuidanceRequest(client, beadId, "Save this context.", "note_only"))
        using (var noteResponse = await client.SendAsync(noteRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, noteResponse.StatusCode);
        }

        using (var workspace = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken))
        using (var detail = JsonDocument.Parse(await workspace.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(HttpStatusCode.OK, workspace.StatusCode);
            var comments = detail.RootElement.GetProperty("comments").EnumerateArray().ToArray();
            Assert.Equal("human-guidance", comments[^1].GetProperty("kind").GetString());
            Assert.Equal("Choose the safer option.", detail.RootElement.GetProperty("attention").GetProperty("question").GetString());
            Assert.Equal(comments[0].GetProperty("id").GetString(),
                detail.RootElement.GetProperty("history").GetProperty("activeCommentId").GetString());
            var older = detail.RootElement.GetProperty("history").GetProperty("olderCommentIds").EnumerateArray()
                .Select(value => value.GetString()).ToArray();
            var recent = detail.RootElement.GetProperty("history").GetProperty("recentCommentIds").EnumerateArray()
                .Select(value => value.GetString()).ToArray();
            Assert.Empty(older);
            Assert.Equal(comments.Select(comment => comment.GetProperty("id").GetString()), recent);
        }

        using (var resumeRequest = CreateGuidanceRequest(client, beadId, "Use the safer option and continue.", "resume"))
        using (var resumeResponse = await client.SendAsync(resumeRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);
        }

        using (var finalResponse = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken))
        using (var final = JsonDocument.Parse(await finalResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(HttpStatusCode.OK, finalResponse.StatusCode);
            Assert.Equal(JsonValueKind.Null, final.RootElement.GetProperty("attention").ValueKind);
            Assert.DoesNotContain(final.RootElement.GetProperty("bead").GetProperty("labels").EnumerateArray(),
                label => label.GetString() == "product-decision-required");
            Assert.Contains(final.RootElement.GetProperty("comments").EnumerateArray(), comment =>
                comment.GetProperty("kind").GetString() == "human-guidance"
                && comment.GetProperty("body").GetString()!.Contains("Use the safer option and continue.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WorkspaceDistinguishesUnsafeAndUnknownExactIds()
    {
        using var target = new BeadsFixture("workspace errors target");
        var beadId = target.Create("Exact workspace ID");
        using var factory = CreateFactory(target);
        using var client = factory.CreateClient();

        using var invalid = await client.GetAsync("/api/beads/fixture%3Aunsafe", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unknown = await client.GetAsync($"/api/beads/{beadId}prefix", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unsupportedQuery = await client.GetAsync($"/api/beads/{beadId}?other=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedQuery.StatusCode);
    }

    private static HttpRequestMessage CreateGuidanceRequest(HttpClient client, string beadId, string instruction, string action)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/beads/{Uri.EscapeDataString(beadId)}/decide")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { instruction, action }), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Nightweaver-Request", "console-v1");
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return request;
    }

    private static NightweaverWebApplicationFactory CreateFactory(BeadsFixture target)
    {
        var actual = new SystemProcessRunner();
        var runner = new RecordingProcessRunner((request, cancellationToken) =>
        {
            var environment = target.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return actual.RunAsync(request with { Environment = environment }, cancellationToken);
        });
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "workspace-test",
            target_repository = new { path = target.Path }
        }));
        return factory;
    }
}
