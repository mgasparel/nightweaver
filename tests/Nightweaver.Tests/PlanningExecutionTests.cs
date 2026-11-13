using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class PlanningExecutionTests
{
    [Theory]
    [InlineData("draft")]
    [InlineData("approval")]
    [InlineData("repair")]
    [InlineData("question")]
    [InlineData("invalid-question")]
    [InlineData("classification-failure")]
    [InlineData("exhausted")]
    [InlineData("overflow")]
    [InlineData("grounding-change")]
    [InlineData("classifier-mutation")]
    public async Task GroundedTurnsPublishOnlyVerifiedOutcomesAndPreserveEvidence(string scenario)
    {
        using var fixture = new PlanningFixture(scenario);
        var result = await fixture.RunAsync();
        var success = scenario is "draft" or "approval" or "repair" or "question";
        Assert.Equal(success, result.Succeeded);
        Assert.Equal(1, fixture.Executions);
        Assert.True(File.Exists(fixture.Sentinel));
        Assert.NotEmpty(fixture.Comments);
        if (!success)
        {
            Assert.NotNull(fixture.Escalation);
            if (scenario is "exhausted" or "overflow") Assert.Equal(NeedsHumanCause.RepairBudgetExhausted, fixture.Escalation.Cause);
            Assert.Equal(fixture.Initial.Stage, fixture.State.Stage);
            Assert.Equal(fixture.Initial.RepairCycles, fixture.State.RepairCycles);
            Assert.Equal(0, fixture.PublishWrites);
            return;
        }
        Assert.Equal(1, fixture.PublishWrites);
        Assert.Equal(scenario switch
        {
            "draft" => "stage:plan-challenge", "approval" => "stage:plan-approved",
            "repair" => "stage:plan", _ => fixture.Initial.Stage
        }, fixture.State.Stage);
        Assert.Equal(scenario == "repair" ? 1 : 0, fixture.State.RepairCycles);
        Assert.Equal(scenario == "question", fixture.State.ProductDecisionRequired);
        Assert.True(result.StageResult.PublicationReceipt?.ClaimReleased);
        Assert.Null(fixture.Owner);
        if (scenario == "approval")
        {
            var approval = Assert.IsType<PlanningApproval>(result.StageResult.PlanningApproval);
            Assert.True(PlanningEvidence.ApprovalStillCurrent(approval, fixture.Grounding!, fixture.Comments));
            var candidate = fixture.Comments[0];
            fixture.Comments[0] = candidate with { Text = candidate.Text + " " };
            Assert.False(PlanningEvidence.ApprovalStillCurrent(approval, fixture.Grounding!, fixture.Comments));
            Assert.False(PlanningEvidence.ApprovalStillCurrent(approval,
                fixture.Grounding! with { ProductInputs = "new requirements" }, fixture.Comments));
        }
    }

    [Fact]
    public void RevalidationAndExactReviewIdentityAreRequiredAfterGroundingChanges()
    {
        var old = new PlanningGrounding("old", "old", "old");
        var current = old with { DefaultRevision = "new code" };
        var candidate = Comment("1", Plan(old));
        var review = Comment("2", new PlanningDocument(PlanningEvidence.ReviewSchema, current,
            PlanCommentId: "1", PlanRevision: PlanningEvidence.Hash(candidate.Text), Assessment: "Complete review"));
        var invocation = Invocation(AgentRole.PlanChallenger, current, ["1"]);
        var missing = PlanningEvidence.Verify(invocation, AgentOutcomeChoice.Approved, current, [candidate, review]);
        Assert.Contains("revalidation", missing.Error, StringComparison.OrdinalIgnoreCase);
        var revalidated = PlanningEvidence.Parse(review.Text)! with { Revalidation = "Rechecked the changed API; original UI slice remains valid." };
        review = Comment("2", revalidated);
        var valid = PlanningEvidence.Verify(invocation, AgentOutcomeChoice.Approved, current, [candidate, review]);
        Assert.Null(valid.Error);
        Assert.NotNull(valid.Approval);
        Assert.False(PlanningEvidence.ApprovalStillCurrent(valid.Approval, current,
            [candidate, review, Comment("3", Plan(current))]));
        Assert.NotNull(PlanningEvidence.Verify(invocation, AgentOutcomeChoice.Approved, current,
            [candidate with { Text = candidate.Text + " " }, review]).Error);
    }

    [Fact]
    public void StructuralChecksRejectMissingCoverageUnsourcedConstraintsAndBrokenDependencies()
    {
        var plan = Plan(new("head", "main", "inputs"));
        Assert.Null(PlanningEvidence.ValidateCandidate(plan));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { Coverage = [] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { Constraints = [new("Use a particular design", "")] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { Tasks = [plan.Tasks![0], plan.Tasks[0]] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { Tasks = [plan.Tasks![0] with { DependsOn = ["missing"] }] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { Tasks = [plan.Tasks![0] with { DependsOn = ["slice"] }] }));
    }

    [Fact]
    public void DisprovenAssumptionsRequireEvidencedRevisionDirectionAndFreshEvidence()
    {
        var grounding = new PlanningGrounding("head", "main", "inputs");
        var candidate = Comment("1", Plan(grounding));
        var review = new PlanningDocument(PlanningEvidence.ReviewSchema, grounding,
            PlanCommentId: "1", PlanRevision: PlanningEvidence.Hash(candidate.Text), Assessment: "Revise storage assumption",
            Findings: [new("Outcome cannot persist", "API has no write contract", "Revise storage slice to use the supported API; preserve unrelated UI task")]);
        var invocation = Invocation(AgentRole.PlanChallenger, grounding, ["1"]);
        Assert.Null(PlanningEvidence.Verify(invocation, AgentOutcomeChoice.ChangesRequired, grounding,
            [candidate, Comment("2", review)]).Error);
        Assert.NotNull(PlanningEvidence.Verify(invocation, AgentOutcomeChoice.ChangesRequired, grounding,
            [candidate, Comment("2", review with { Findings = [] })]).Error);
        Assert.NotNull(PlanningEvidence.Verify(invocation with { PreInvocationCommentIds = new HashSet<string> { "1", "2" } },
            AgentOutcomeChoice.ChangesRequired, grounding, [candidate, Comment("2", review)]).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusableCoordinatorCarriesRepairsAndRefusesPausedWorkUntilGuardedResume(bool paused)
    {
        using var temp = new TempDirectory();
        var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
        var project = new ProjectConfig(ProjectSlug.Parse("demo"), "config.json", new(temp.Path, "main"),
            new(1, 1, 1), new(3, 2), settings, new(AgentRoles.All.ToDictionary(role => role, _ => settings)));
        var state = new BeadWorkflowState("product", "feature", "in_progress", "stage:plan", false, paused, false, 0)
            { ReadyForPlanning = true };
        var runner = new RecordingProcessRunner((request, _) =>
        {
            var row = new { id = "product", issue_type = "feature", status = "in_progress", assignee = "nightweaver:demo",
                priority = 2, created_at = "2026-11-13T00:00:00Z", metadata = new { repair_cycles = state.RepairCycles },
                labels = new[] { state.Stage, "ready-for-planning" }.Concat(state.ProductDecisionRequired ? ["product-decision-required"] : Array.Empty<string>()) };
            return Task.FromResult(new ProcessResult(0, request.Arguments.Contains("--unassigned") ? "[]" : JsonSerializer.Serialize(new[] { row }), ""));
        });
        var roles = new List<AgentRole>();
        var coordinator = new PlanningCoordinator(runner, (_, _, _, _, role, _, before, _, _) =>
        {
            roles.Add(role);
            var approval = roles.Count == 4;
            state = role == AgentRole.Planner ? before with { Stage = "stage:plan-challenge" }
                : approval ? before with { Stage = "stage:plan-approved", Status = "open" }
                : before with { Stage = "stage:plan", RepairCycles = before.RepairCycles + 1 };
            var choice = approval ? AgentOutcomeChoice.Approved : role == AgentRole.Planner ? AgentOutcomeChoice.ReadyForReview : AgentOutcomeChoice.ChangesRequired;
            var result = new AgentStageResult(role, "product", "turn", "log", null, [], state,
                PublicationReceipt: new(role, choice, state, approval ? null : "nightweaver:demo", approval, false));
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, result, 1, null, null, null));
        });
        if (paused)
        {
            await Assert.ThrowsAsync<AgentStageInputException>(() => coordinator.RunClaimedAsync(new(temp.Path), project,
                new(temp.Path, "main"), "product", state, TestContext.Current.CancellationToken));
            Assert.Empty(roles);
            // Only a successful external guarded Resume may establish this unpaused input state.
            state = state with { ProductDecisionRequired = false };
        }
        var result = await coordinator.RunClaimedAsync(new(temp.Path), project, new(temp.Path, "main"), "product", state,
            TestContext.Current.CancellationToken);
        Assert.True(result?.Succeeded);
        Assert.Equal(new[] { AgentRole.Planner, AgentRole.PlanChallenger, AgentRole.Planner, AgentRole.PlanChallenger }, roles);
        Assert.Equal(1, state.RepairCycles);
        Assert.Equal("stage:plan-approved", state.Stage);
    }

    internal static PlanningDocument Plan(PlanningGrounding grounding) => new(PlanningEvidence.CandidateSchema, grounding,
        Tasks: [new("slice", "Persist outcome", "Outcome survives restart", "Storage contract at Store.cs", "Extend supported storage API",
            ["Outcome survives restart"], "Restart component and read the saved outcome", [])],
        Constraints: [], Coverage: [new("Persistence", ["slice"], null, null)]);

    private static BeadCommentSnapshot Comment(string id, PlanningDocument document) =>
        new(id, JsonSerializer.Serialize(document, PlanningEvidence.JsonOptions), DateTimeOffset.UnixEpoch.AddSeconds(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)));

    private static AgentStageResult Invocation(AgentRole role, PlanningGrounding grounding, string[] prior) =>
        new(role, "product", "turn", "log", null, [], PreInvocationCommentIds: prior.ToHashSet(StringComparer.Ordinal), PlanningGrounding: grounding);

    private sealed class PlanningFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private readonly string scenario;
        private readonly HostPaths paths;
        private readonly ProjectConfig project;
        private readonly RecordingProcessRunner runner;
        internal BeadWorkflowState Initial { get; }
        internal BeadWorkflowState State { get; private set; }
        internal string? Owner { get; private set; } = "nightweaver:demo";
        internal List<BeadCommentSnapshot> Comments { get; } = [];
        internal PlanningGrounding? Grounding { get; private set; }
        internal NeedsHumanRequest? Escalation { get; private set; }
        internal int Executions { get; private set; }
        internal int PublishWrites { get; private set; }
        internal string Sentinel { get; }
        private string DefaultSha => new(scenario == "grounding-change" && Executions > 0 ? 'b' : 'a', 40);

        internal PlanningFixture(string scenario)
        {
            this.scenario = scenario;
            paths = new(temp.Path);
            Sentinel = temp.WriteFile("retained/ignored-cache", "preserve this work");
            Initial = new("product", "feature", "in_progress", scenario == "draft" ? "stage:plan" : "stage:plan-challenge",
                false, false, false, scenario == "exhausted" ? 3 : scenario == "overflow" ? int.MaxValue : 0) { ReadyForPlanning = true };
            State = Initial;
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            project = new(ProjectSlug.Parse("demo"), "config.json", new(temp.Path, "main"), new(1, 1, 1), new(3, 2),
                settings, new(AgentRoles.All.ToDictionary(role => role, _ => settings)));
            runner = new(RespondAsync);
        }

        private string Inventory() => JsonSerializer.Serialize(new[] { new
        {
            id = State.Id, issue_type = State.IssueType, status = State.Status, assignee = Owner, priority = 2,
            created_at = "2026-11-13T00:00:00Z", title = "Persistence", description = "Persist outcomes",
            labels = new[] { State.Stage, "ready-for-planning" }.Concat(State.ProductDecisionRequired ? ["product-decision-required"] : Array.Empty<string>()), metadata = new { repair_cycles = State.RepairCycles }
        } });

        private Task<ProcessResult> RespondAsync(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string output;
            var args = request.Arguments.ToList();
            string Value(string option) => args[args.IndexOf(option) + 1];
            if (request.FileName == "git")
                output = args[0] switch { "status" => "", "branch" => "", "rev-parse" => DefaultSha, _ => throw new InvalidOperationException() };
            else if (args.Contains("comments"))
            {
                if (args.Contains("add"))
                {
                    Comments.Add(new((Comments.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), args[^1], DateTimeOffset.UnixEpoch.AddSeconds(Comments.Count + 1)));
                    output = "";
                }
                else output = JsonSerializer.Serialize(Comments.Select(c => new { id = c.Id, issue_id = "product", text = c.Text, created_at = c.CreatedAt }));
            }
            else if (args.Contains("update"))
            {
                PublishWrites++;
                Assert.Equal(Owner, Value("--if-assignee"));
                Assert.Equal(State.Status, Value("--if-status"));
                var label = Value("--add-label");
                State = State with { Stage = label.StartsWith("stage:", StringComparison.Ordinal) ? label : State.Stage,
                    ProductDecisionRequired = label == "product-decision-required", Status = Value("--status"),
                    RepairCycles = args.Contains("--set-metadata") ? State.RepairCycles + 1 : State.RepairCycles };
                Owner = Value("--assignee"); if (Owner.Length == 0) Owner = null;
                output = Inventory();
            }
            else output = Inventory();
            return Task.FromResult(new ProcessResult(0, output, ""));
        }

        internal async Task<OperationalRetryOutcome> RunAsync()
        {
            var role = scenario == "draft" ? AgentRole.Planner : AgentRole.PlanChallenger;
            var stages = new AgentStageCoordinator(runner,
                provision: (_, _, _, _, _, _) => Task.FromResult(new ProvisionedWorktree(temp.Path, new string('a', 40))),
                prepare: (_, _, _, _, _, _) => Task.FromResult(new PreparedAgentInvocation("fixture", [], temp.Path, "prompt",
                    new Dictionary<string, string>(), TimeSpan.FromSeconds(10), "turn", temp.Path, temp.Path + "/log")),
                execute: (_, _, invocation, _) =>
                {
                    Grounding = JsonSerializer.Deserialize<PlanningGrounding>(invocation.StandardInput.Split("Host grounding (copy exactly into evidence):\n")[1], PlanningEvidence.JsonOptions)!;
                    if (scenario == "draft") Comments.Add(Comment("1", Plan(Grounding)));
                    else
                    {
                        // Existing candidate is a prior turn; the fixture exposes it before comment-ID capture below.
                        var candidate = Comments[0];
                        if (scenario is "question" or "invalid-question")
                            Comments.Add(Comment("2", new(PlanningEvidence.QuestionSchema, Grounding,
                                Questions: [new("Which retention period?", "Thirty days", scenario == "question" ? "Storage cost versus history" : "")])));
                        else Comments.Add(Comment("2", new(PlanningEvidence.ReviewSchema, Grounding,
                            PlanCommentId: candidate.Id, PlanRevision: PlanningEvidence.Hash(candidate.Text), Assessment: "Reviewed complete coverage",
                            Findings: scenario is "repair" or "exhausted" or "overflow"
                                ? [new("Restart loses data", "Store contract lacks writes", "Revise the affected persistence slice")] : [])));
                    }
                    Executions++;
                    return Task.FromResult(new AgentExecutionResult(AgentExecutionStatus.Exited, 0, "turn", invocation.LogFilePath));
                });
            if (scenario != "draft")
            {
                var session = new AgentStageSession(paths, project, new(temp.Path, "main"), "product", role,
                    WorktreePurpose.Planning, new(temp.Path, new string('a', 40)), Initial, Owner,
                    new("product", "feature", Initial.Stage, Initial.RepairCycles), false);
                var grounding = await new PlanningEvidence(runner).CaptureAsync(session, TestContext.Current.CancellationToken);
                Comments.Add(Comment("1", Plan(grounding)));
            }
            var classifier = scenario switch
            {
                "draft" => AgentOutcomeChoice.ReadyForReview,
                "repair" or "exhausted" or "overflow" => AgentOutcomeChoice.ChangesRequired,
                "question" or "invalid-question" => AgentOutcomeChoice.ProductDecisionRequired,
                "classification-failure" => (AgentOutcomeChoice?)null,
                _ => AgentOutcomeChoice.Approved
            };
            var coordinator = new OperationalRetryCoordinator(runner, stages: stages,
                outcomeClassifier: new FixedAgentOutcomeClassifier(classifier),
                outcomeVerifier: new FixedDeliveryOutcomeVerifier(scenario == "classifier-mutation" ? "Classifier changed HEAD" : null),
                escalate: (_, _, _, request, _) =>
                {
                    Escalation = request;
                    return Task.FromResult(new NeedsHumanEscalationResult(true, true, true, true, []));
                });
            return await coordinator.RunAsync(paths, project, new(temp.Path, "main"), "product", role,
                "nightweaver:demo", TestContext.Current.CancellationToken);
        }

        public void Dispose() => temp.Dispose();
    }
}
