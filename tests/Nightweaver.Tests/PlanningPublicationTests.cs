using System.Text.Json;
using System.Text.Json.Nodes;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class PlanningPublicationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task CrashAfterEveryCreateLinkReceiptOrReleaseResumesWithoutDuplicates(int stop)
    {
        using var fixture = new Fixture { CrashAfterWrite = stop };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
        fixture.CrashAfterWrite = null;
        var result = await fixture.Publish();
        Assert.True(result.Succeeded);
        Assert.Equal(2, fixture.CreateCount);
        Assert.Equal(2, fixture.Tasks.Count());
        Assert.Equal(3, fixture.Edges.Count);
        Assert.All(fixture.Tasks, task => Assert.True(PlanningPublicationGate.Allows(task, fixture.Inventory)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task LostResponsesReconcileActualEffectsWithoutRepeatingWrites(int lost)
    {
        using var fixture = new Fixture { LoseResponseAt = lost };
        var result = await fixture.Publish();
        Assert.True(result.Succeeded);
        Assert.True(result.VerifiedState!.Planned);
        Assert.Equal(2, fixture.CreateCount);
        Assert.Equal(7, fixture.WriteCount);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("foreign")]
    [InlineData("pause")]
    [InlineData("extra-child")]
    [InlineData("extra-edge")]
    [InlineData("approval-edit")]
    [InlineData("hold")]
    public async Task ConflictsPreserveEvidenceAndNeverPublish(string conflict)
    {
        using var fixture = new Fixture { CrashAfterWrite = 2 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        var child = fixture.Rows.Values.Last();
        switch (conflict)
        {
            case "content": child["description"] = "human-edited contract"; break;
            case "foreign": child["assignee"] = "another-owner"; break;
            case "pause": fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human"); break;
            case "extra-child": fixture.Edges.Add(new("manual", "product", "discovered-from")); break;
            case "extra-edge": fixture.Edges.Add(new(child["id"]!.GetValue<string>(), "other", "blocks")); break;
            case "approval-edit": fixture.Comments[0] = fixture.Comments[0] with { Text = fixture.Comments[0].Text + " " }; break;
            case "hold": child["labels"] = new JsonArray(); break;
        }
        var before = JsonSerializer.Serialize(fixture.Rows);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.Equal(2, fixture.CreateCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task FailedWritesRemainNonexecutableAndCanBeReconciled(int rejected)
    {
        using var fixture = new Fixture { RejectWriteAt = rejected };
        await Assert.ThrowsAnyAsync<NightweaverException>(fixture.Publish);
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
        fixture.RejectWriteAt = null;
        Assert.True((await fixture.Publish()).VerifiedState!.Planned);
        Assert.Equal(2, fixture.CreateCount);
    }

    [Fact]
    public async Task ChangedReleasedGraphRequiresReplanningAndManualTasksRemainIndependent()
    {
        using var fixture = new Fixture { CrashAfterWrite = 5 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        var child = fixture.Rows.Values.Last();
        child["status"] = "in_progress";
        child["assignee"] = "nightweaver:demo";
        var before = JsonSerializer.Serialize(fixture.Rows);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        var manual = fixture.Parent with { Id = "manual", IssueType = "task" };
        Assert.True(PlanningPublicationGate.Allows(manual, fixture.Inventory));
    }

    private static readonly JsonSerializerOptions HostJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task GuidanceDoesNotResumePauseAndExplicitResumeRevalidatesThenReusesExistingTasks()
    {
        using var fixture = new Fixture { CrashAfterWrite = 2 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
        fixture.AddComment(BeadsWriter.CreateHumanGuidanceComment("resume", "Continue the same approved tasks; retain all evidence."));
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(2, fixture.WriteCount);
        fixture.Rows["product"]["labels"] = new JsonArray("ready-for-planning", "stage:plan-approved");
        var revalidation = await fixture.Publish();
        Assert.Equal("stage:plan-challenge", revalidation.VerifiedState!.Stage);
        Assert.True(revalidation.PublicationReceipt!.ClaimReleased);
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
        await fixture.Reapprove();
        Assert.True((await fixture.Publish()).VerifiedState!.Planned);
        Assert.Equal(2, fixture.CreateCount);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("dependencies")]
    [InlineData("missing")]
    [InlineData("pause")]
    public async Task ExecutingGraphChangesStopFurtherDeliveryAndPreserveCompletedWork(string change)
    {
        using var fixture = new Fixture();
        await fixture.Publish();
        var first = fixture.Rows.Values.ElementAt(1);
        var second = fixture.Rows.Values.ElementAt(2);
        first["status"] = "closed";
        second["status"] = "in_progress";
        second["assignee"] = "nightweaver:demo";
        Assert.All(fixture.Tasks, t => Assert.True(PlanningPublicationGate.Allows(t, fixture.Inventory)));
        switch (change)
        {
            case "content": second["acceptance_criteria"] = "new executing contract"; break;
            case "dependencies": second["dependencies"] = new JsonArray(); break;
            case "missing": fixture.Rows.Remove(first["id"]!.GetValue<string>()); break;
            case "pause": fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human"); break;
        }
        var before = JsonSerializer.Serialize(fixture.Rows);
        Assert.All(fixture.Tasks, t => Assert.False(PlanningPublicationGate.Allows(t, fixture.Inventory)));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.Equal("closed", first["status"]!.GetValue<string>());
        Assert.Equal("in_progress", second["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("owned")]
    [InlineData("foreign")]
    [InlineData("closed")]
    public async Task DiscoveryPublishesGraphAttentionWhilePreservingClaimsAndCompletedWork(string ownership)
    {
        using var fixture = new Fixture();
        await fixture.Publish();
        var first = fixture.Rows.Values.ElementAt(1);
        var second = fixture.Rows.Values.ElementAt(2);
        first["status"] = "closed";
        second["acceptance_criteria"] = "changed executing contract";
        second["status"] = ownership switch { "closed" => "closed", "unassigned" => "open", _ => "in_progress" };
        second["assignee"] = ownership switch { "owned" => "nightweaver:demo", "foreign" => "other-owner", _ => null };
        var expectedStatus = second["status"]!.GetValue<string>();
        var expectedOwner = second["assignee"]?.GetValue<string>();
        var changed = await fixture.Reconcile();
        Assert.Equal(ownership is "unassigned" or "owned", changed);
        var task = fixture.Inventory.ById[second["id"]!.GetValue<string>()];
        Assert.Equal(changed, task.Labels.Contains("needs-human"));
        Assert.Equal(expectedStatus, task.Status);
        Assert.Equal(expectedOwner, task.Assignee);
        Assert.Equal("closed", first["status"]!.GetValue<string>());
        var writes = fixture.WriteCount;
        Assert.False(await fixture.Reconcile());
        Assert.Equal(writes, fixture.WriteCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal Dictionary<string, JsonObject> Rows { get; } = new(StringComparer.Ordinal);
        internal List<BeadDependency> Edges { get; } = [];
        internal List<BeadCommentSnapshot> Comments { get; } = [];
        internal int? CrashAfterWrite { get; set; }
        internal int? LoseResponseAt { get; set; }
        internal int? RejectWriteAt { get; set; }
        internal int WriteCount { get; private set; }
        internal int CreateCount { get; private set; }
        private readonly AgentStageSession session;
        private readonly PlanningPublisher publisher;
        private readonly RecordingProcessRunner runner;
        internal BeadsInventory Inventory => BeadsReader.Parse(JsonSerializer.Serialize(Rows.Values));
        internal BeadSnapshot Parent => Inventory.ById["product"];
        internal IEnumerable<BeadSnapshot> Tasks => Inventory.ById.Values.Where(b => b.IssueType == "task");

        internal Fixture()
        {
            Rows["product"] = new JsonObject { ["id"] = "product", ["title"] = "Requested behavior", ["issue_type"] = "feature",
                ["description"] = "Contract", ["status"] = "in_progress", ["assignee"] = "nightweaver:demo",
                ["priority"] = 2, ["created_at"] = "2026-11-13T00:00:00Z",
                ["labels"] = new JsonArray("ready-for-planning", "stage:plan-approved"), ["metadata"] = new JsonObject() };
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            var project = new ProjectConfig(ProjectSlug.Parse("demo"), "config.json", new(temp.Path, "main"), new(1, 1, 1), new(3, 2),
                settings, new(AgentRoles.All.ToDictionary(r => r, _ => settings)));
            session = new(new(temp.Path), project, new(temp.Path, "main"), "product", AgentRole.Planner,
                WorktreePurpose.Planning, new(temp.Path, new string('a', 40)), BeadWorkflowState.Parse(Parent), "nightweaver:demo",
                new("product", "feature", "stage:plan-approved", 0), false);
            runner = new RecordingProcessRunner(Respond);
            publisher = new(runner);
            var grounding = new PlanningEvidence(runner).CaptureAsync(session, CancellationToken.None).GetAwaiter().GetResult();
            var plan = new PlanningDocument(PlanningEvidence.CandidateSchema, grounding,
                Tasks: [new("first", "First behavior", "Observable first", "Api.cs", "Advisory", ["First works"], "Check first", []),
                    new("second", "Second behavior", "Observable second", "Contract.cs", "Advisory", ["Second works"], "Check second", ["first"])],
                Constraints: [new("Preserve data", "Product contract")], Coverage: [new("Behavior", ["first", "second"], null, null)]);
            AddComment(JsonSerializer.Serialize(plan, PlanningEvidence.JsonOptions));
            var review = new PlanningDocument(PlanningEvidence.ReviewSchema, grounding, PlanCommentId: "1",
                PlanRevision: PlanningEvidence.Hash(Comments[0].Text), Assessment: "Complete", Findings: [], Questions: []);
            AddComment(JsonSerializer.Serialize(review, PlanningEvidence.JsonOptions));
            var approval = new PlanningApproval("1", PlanningEvidence.Hash(Comments[0].Text), "2", PlanningEvidence.Hash(Comments[1].Text), grounding);
            AddComment(JsonSerializer.Serialize(new { schema = "nightweaver-planning-outcome-v1", outcome = "Approved", planning_approval = approval },
                HostJsonOptions));
        }

        internal Task<AgentStageResult> Publish() => publisher.PublishAsync(session, TestContext.Current.CancellationToken);
        internal Task<bool> Reconcile() => new PlanningGraphReconciler(runner).PauseChangedGraphsAsync(session.Repository,
            Inventory, "nightweaver:demo", TestContext.Current.CancellationToken);
        internal void AddComment(string text) => Comments.Add(new((Comments.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), text,
            DateTimeOffset.UnixEpoch.AddSeconds(Comments.Count)));

        internal async Task Reapprove()
        {
            Rows["product"]["status"] = "in_progress";
            Rows["product"]["assignee"] = "nightweaver:demo";
            Rows["product"]["labels"] = new JsonArray("ready-for-planning", "stage:plan-approved");
            var grounding = await new PlanningEvidence(runner).CaptureAsync(session, TestContext.Current.CancellationToken);
            AddComment(JsonSerializer.Serialize(new PlanningDocument(PlanningEvidence.ReviewSchema, grounding,
                PlanCommentId: "1", PlanRevision: PlanningEvidence.Hash(Comments[0].Text), Assessment: "Revalidated guidance",
                Revalidation: "Guidance preserves candidate contracts and task boundaries.", Findings: [], Questions: []), PlanningEvidence.JsonOptions));
            var review = Comments[^1];
            var approval = new PlanningApproval("1", PlanningEvidence.Hash(Comments[0].Text), review.Id, PlanningEvidence.Hash(review.Text), grounding);
            AddComment(JsonSerializer.Serialize(new { schema = "nightweaver-planning-outcome-v1", outcome = "Approved", planning_approval = approval }, HostJsonOptions));
        }

        private Task<ProcessResult> Respond(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var args = request.Arguments.ToList();
            string Value(string flag) => args[args.IndexOf(flag) + 1];
            string output;
            var write = args.Contains("--dolt-auto-commit");
            if (write && WriteCount + 1 == RejectWriteAt)
            {
                WriteCount++;
                return Task.FromResult(new ProcessResult(1, "", "Write failed before commit"));
            }
            if (request.FileName == "git") output = args.Contains("status") ? "" : new string('a', 40);
            else if (args.Contains("comments"))
            {
                if (args.Contains("add")) { AddComment(args[^1]); output = ""; }
                else output = JsonSerializer.Serialize(Comments.Select(c => new { id = c.Id, issue_id = "product", text = c.Text, created_at = c.CreatedAt }));
            }
            else if (args.Contains("create"))
            {
                var id = Value("--id");
                Assert.False(Rows.ContainsKey(id), "A create must never be replayed after an uncertain write.");
                CreateCount++;
                Rows[id] = new JsonObject { ["id"] = id, ["title"] = Value("--title"), ["issue_type"] = "task",
                    ["description"] = Value("--description"), ["acceptance_criteria"] = Value("--acceptance"),
                    ["status"] = "open", ["priority"] = 2, ["created_at"] = "2026-11-13T00:00:00Z",
                    ["labels"] = new JsonArray("plan-unpublished"), ["metadata"] = JsonNode.Parse(Value("--metadata")) };
                Edges.Add(new(id, "product", "discovered-from")); output = "";
            }
            else if (args.Contains("dep"))
            {
                if (args.Contains("add"))
                {
                    var i = args.IndexOf("add"); Edges.Add(new(args[i + 1], args[i + 2], Value("--type"))); output = "";
                }
                else
                {
                    var id = args[args.IndexOf("list") + 1];
                    output = JsonSerializer.Serialize(Edges.Where(e => Value("--direction") == "up" ? e.DependsOnId == id : e.IssueId == id)
                        .Select(e => new { issue_id = e.IssueId, depends_on_id = e.DependsOnId, type = e.Type }));
                }
            }
            else if (args.Contains("update"))
            {
                var row = Rows[args[args.IndexOf("update") + 1]];
                Assert.Equal(row["status"]!.GetValue<string>(), Value("--if-status"));
                Assert.Equal(row["assignee"]?.GetValue<string>() ?? "", Value("--if-assignee"));
                var labels = row["labels"]!.AsArray();
                if (args.Contains("--remove-label"))
                {
                    var removed = labels.FirstOrDefault(l => l!.GetValue<string>() == Value("--remove-label"));
                    if (removed is not null) labels.Remove(removed);
                }
                if (args.Contains("--add-label")) labels.Add(Value("--add-label"));
                for (var i = 0; i < args.Count - 1; i++)
                    if (args[i] == "--set-metadata")
                    {
                        var pair = args[i + 1].Split('=', 2);
                        row["metadata"]![pair[0]] = JsonValue.Create(pair[1]);
                    }
                row["status"] = Value("--status"); row["assignee"] = Value("--assignee") is "" ? null : Value("--assignee"); output = "";
            }
            else if (args.Contains("show")) output = Rows.TryGetValue(args[args.IndexOf("show") + 1], out var shown)
                ? JsonSerializer.Serialize(new[] { shown }) : "[]";
            else output = JsonSerializer.Serialize(Rows.Values);
            if (write)
            {
                foreach (var row in Rows.Values)
                    row["dependencies"] = JsonSerializer.SerializeToNode(Edges.Where(e => e.IssueId == row["id"]!.GetValue<string>())
                        .Select(e => new { issue_id = e.IssueId, depends_on_id = e.DependsOnId, type = e.Type }));
                WriteCount++;
                if (WriteCount == CrashAfterWrite) throw new OperationCanceledException("Simulated crash after durable write");
                if (WriteCount == LoseResponseAt) throw new IOException("Lost response after durable write");
            }
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
        public void Dispose() => temp.Dispose();
    }
}
