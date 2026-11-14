using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class BeadPresentationTests
{
    private static readonly string[] NeedsHumanLabels = ["needs-human"];
    [Theory]
    [InlineData("feature", "Product Bead")]
    [InlineData("bug", "Product Bead")]
    [InlineData("task", "Plan Bead")]
    [InlineData("epic", "Outside Nightweaver lifecycle")]
    [InlineData("spike", "Outside Nightweaver lifecycle")]
    [InlineData("custom-<script>", "Outside Nightweaver lifecycle")]
    public void RoleUsesOnlyNativeType(string type, string expected)
    {
        var bead = Bead(type);
        Assert.Equal(expected, BeadPresenter.Project(bead).Role);
        Assert.Equal(expected, BeadPresenter.Role(type));
        Assert.Equal("Role unavailable", BeadPresenter.Role(null));
        Assert.Equal(type, BeadExplorerItem.FromSnapshot(bead).Type);
    }

    [Theory]
    [InlineData("feature", "stage:plan", "Planning")]
    [InlineData("bug", "stage:plan-challenge", "Plan review")]
    [InlineData("feature", "stage:plan-approved", "Plan approved")]
    [InlineData("task", "stage:implement", "Implementation")]
    [InlineData("task", "stage:review", "Review")]
    [InlineData("task", "stage:qa", "QA")]
    [InlineData("task", "stage:merge", "Merge")]
    public void StoredStageNeverClaimsLiveExecution(string type, string stage, string expected)
    {
        var facts = BeadPresenter.Project(Bead(type, [stage], status: "in_progress", owner: "nightweaver:demo"), 3);
        Assert.Equal(expected, facts.TrackedStage);
        Assert.Contains("Tracked stage: " + expected, facts.Summary);
        Assert.DoesNotContain("Running", facts.Summary);
        Assert.Equal("Tracker ownership: nightweaver:demo", facts.Ownership);
    }

    [Theory]
    [InlineData("stage:unknown", "")]
    [InlineData("stage:plan", "")]
    [InlineData("stage:review", "stage:qa")]
    [InlineData("stage:review", "stage:review")]
    [InlineData("needs-human", "product-decision-required")]
    [InlineData("planned", "stage:review")]
    [InlineData("planned", "product-decision-required")]
    public void MalformedWorkflowHasNoConfidentStageOrPause(string first, string second)
    {
        var bead = Bead(labels: second.Length == 0 ? [first] : [first, second]);
        var facts = BeadPresenter.Project(bead, 3);
        Assert.NotNull(facts.Diagnostic);
        Assert.Null(facts.TrackedStage);
        Assert.Null(facts.Pause);
        Assert.Contains("unavailable", facts.Summary);
        Assert.Equal("invalid-workflow", AttentionDeskProjector.Project(bead, [], 3).Kind);
    }

    [Fact]
    public void PlannedHistoricalTechnicalAttentionIsVisibleWithoutActions()
    {
        var bead = Bead("feature", ["planned", "needs-human"]);
        Assert.Null(AttentionDeskProjector.InvalidWorkflowReason(bead));
        var attention = AttentionDeskProjector.Project(bead, [], 3);
        Assert.Equal("needs-human", attention.Kind);
        Assert.False(attention.CanRetry);
        Assert.False(attention.CanAbort);
        Assert.False(attention.CanDecide);
        var facts = BeadPresenter.Project(bead, 3);
        Assert.Equal("Plan published / awaiting Delivery", facts.Planning);
        Assert.Contains("technical attention", facts.Summary);
        Assert.Null(facts.Diagnostic);
        Assert.False(PlanningReadiness.Project(bead)!.CanAuthorize);
        Assert.Equal("Plan published", BeadPresenter.Project(Bead("bug", ["planned"], "closed")).Planning);
    }

    [Theory]
    [InlineData("null", 0)]
    [InlineData("{}", 0)]
    [InlineData("{\"repair_cycles\":2}", 2)]
    [InlineData("{\"repair_cycles\":2.0}", 2)]
    [InlineData("{\"repair_cycles\":\"2\"}", 2)]
    [InlineData("{\"repair_cycles\":-1}", null)]
    [InlineData("{\"repair_cycles\":\"bad\"}", null)]
    [InlineData("{\"repair_cycles\":2.5}", null)]
    [InlineData("{\"repair_cycles\":2,\"repair_cycles\":3}", null)]
    public void RepairObservationUsesRuntimeParser(string metadata, int? expected)
    {
        var bead = Bead(metadata: metadata);
        var before = bead.Payload.GetRawText();
        var facts = BeadPresenter.Project(bead, 3);
        Assert.Equal(expected, facts.RepairsUsed);
        Assert.Equal(3, facts.RepairsMax);
        Assert.Equal(expected, AttentionDeskProjector.RepairCycles(bead));
        Assert.Equal(before, bead.Payload.GetRawText());
    }

    [Fact]
    public void ManualPlansAndMissingLinkedSnapshotsKeepHonestRoles()
    {
        var manual = Bead();
        var inventory = new BeadsInventory([manual]);
        Assert.Null(BeadPresenter.Project(manual, 3, inventory).Publication);
        var linked = Bead(dependencies: new[] { new { issue_id = "bead", depends_on_id = "product", type = "discovered-from" } });
        Assert.Null(BeadPresenter.Project(linked, 3, new BeadsInventory([linked, Bead("feature", id: "product")])).Publication);
        var custom = Bead(metadata: "{\"publication_custom_note\":\"ordinary metadata\"}");
        Assert.Null(BeadPresenter.Project(custom, 3, new BeadsInventory([custom])).Publication);
        var duplicate = Bead(metadata: "{\"repair_cycles\":2,\"custom\":1,\"custom\":2}");
        Assert.NotNull(BeadPresenter.Project(duplicate).Diagnostic);
        Assert.NotNull(AttentionDeskProjector.InvalidWorkflowReason(duplicate));
        var rows = BeadWorkspaceProjector.ProjectRelations([], [new("bead", "missing", "discovered-from"), new("bead", "bead", "blocks")], inventory.ById);
        Assert.Equal("Role unavailable", rows.Single(r => r.Id == "missing").Role);
        Assert.Equal("Plan Bead", rows.Single(r => r.Id == "bead").Role);
    }

    [Theory]
    [InlineData("published", "Published graph observed")]
    [InlineData("held", "Publication hold")]
    [InlineData("partial", "Publication hold / verification unavailable")]
    [InlineData("metadata-loss", "Publication needs attention")]
    [InlineData("content", "Publication needs attention")]
    [InlineData("unpublished", "Awaiting complete publication")]
    [InlineData("missing", "Publication needs attention")]
    [InlineData("malformed", "Publication observation unavailable")]
    public void PublicationReusesObservedGraphGate(string scenario, string expected)
    {
        var graph = JsonSerializer.Serialize(new[] { new PublishedPlanningTask("bead", "Title", "Description", "Acceptance", []) });
        if (scenario == "malformed") graph = JsonSerializer.Serialize(new[]
        {
            new PublishedPlanningTask("bead", "Title", "Description", "Acceptance", []),
            new PublishedPlanningTask(null!, "Other", "Description", "Acceptance", [])
        });
        var parentMetadata = JsonSerializer.Serialize(new { publication_revision = "revision", publication_graph = graph });
        var taskMetadata = JsonSerializer.Serialize(new { publication_product = "product", publication_revision = "revision" });
        var task = Bead(labels: scenario is "held" or "partial" ? ["plan-unpublished"] : [],
            metadata: scenario == "metadata-loss" ? "{}" : taskMetadata,
            dependencies: new[] { new { issue_id = "bead", depends_on_id = "product", type = "discovered-from" } });
        if (scenario == "content") task = task with { Payload = JsonSerializer.SerializeToElement(new { title = "Changed", metadata = JsonSerializer.Deserialize<object>(taskMetadata) }) };
        var parent = Bead("feature", scenario == "unpublished" ? [] : ["planned"], id: "product",
            metadata: scenario == "partial" ? "{\"publication_revision\":\"revision\"}" : parentMetadata);
        var inventory = new BeadsInventory(scenario == "missing" ? [task] : [task, parent]);
        Assert.Equal(expected, BeadPresenter.Project(task, 3, inventory).Publication);
        if (scenario != "missing")
            Assert.Equal(scenario is "metadata-loss" or "content" or "malformed" ? "Publication needs attention"
                : scenario == "partial" ? "Publication observation unavailable" : "Publication graph observed",
                BeadPresenter.Project(parent, 3, inventory).Publication);
        if (scenario == "published")
            Assert.Equal("Publication needs attention", BeadPresenter.Project(parent, 3,
                new BeadsInventory([task with { IssueType = "epic" }, parent])).Publication);
    }

    [Fact]
    public void PlanningAndCrossContextFactsRemainReadOnly()
    {
        var bead = Bead("bug", ["ready-for-planning", "stage:plan", new string('x', 2000)]);
        var inventory = new BeadsInventory([bead]);
        var before = bead.Payload.GetRawText();
        var facts = BeadPresenter.Project(bead, 3, inventory);
        var attention = AttentionDeskProjector.Project(bead, [], 3);
        var workspace = BeadWorkspaceProjector.Project(bead, [], null, BeadWorkspaceProjector.UnavailableEnvironment("unavailable"));
        var digest = CatchUpDigestProjector.Project(inventory, "all", null, DateTimeOffset.UtcNow, 3);
        Assert.Equal("Authorized for Planning", facts.Planning);
        Assert.Equal(facts with { RepairsMax = null }, workspace.Presentation);
        Assert.Equal(facts, attention.Presentation);
        Assert.Equal(facts, Assert.Single(digest.StandaloneOpen).Presentation);
        Assert.Equal(before, bead.Payload.GetRawText());
        Assert.Equal("Awaiting Planning authorization", BeadPresenter.Project(Bead("feature")).Planning);
    }

    [Fact]
    public async Task HistoricalPlannedAttentionCannotMutateThroughHttp()
    {
        using var target = new BeadsApplicationFixture();
        var bead = Bead("feature", ["planned", "needs-human"]);
        var runner = new RecordingProcessRunner((request, token) =>
        {
            if (request.FileName == "git") return new SystemProcessRunner().RunAsync(request, token);
            Assert.Contains("--readonly", request.Arguments);
            return Task.FromResult(new ProcessResult(0, "[" + bead.Payload.GetRawText() + "]", ""));
        });
        using var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new { name = "web-test", target_repository = new { path = target.Path } }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Nightweaver-Request", "console-v1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var retry = JsonSerializer.Serialize(new { expected = new { stage = (string?)null, pauseLabels = NeedsHumanLabels, repairCycles = 0 } });
        foreach (var (action, body) in new[] { ("retry", retry), ("abort", "{}") })
        {
            using var response = await client.PostAsync("/api/beads/bead/" + action,
                new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        Assert.All(runner.Requests.Where(r => r.FileName == "bd"), request => Assert.Contains("--readonly", request.Arguments));
    }

    private static BeadSnapshot Bead(string type = "task", string[]? labels = null, string status = "open", string? owner = null,
        string metadata = "null", object? dependencies = null, string id = "bead")
    {
        var row = JsonSerializer.Serialize(new { id, issue_type = type, status, assignee = owner, priority = 2,
            labels = labels ?? [], created_at = "2026-11-14T00:00:00Z", updated_at = "2026-11-14T00:00:00Z",
            title = "Title", description = "Description", acceptance_criteria = "Acceptance", dependencies = dependencies ?? Array.Empty<object>() });
        // Keep duplicate metadata keys for parser-contract cases.
        row = row[..^1] + ",\"metadata\":" + metadata + "}";
        return BeadsReader.Parse("[" + row + "]").ById[id];
    }
}
