using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class PlanningExecutionTests
{
    internal static PlanningLocalInput SyntheticInput(string id = "export") => new(id,
        "synthetic/export.json", new("tracker export", Revision: "reported-revision", ExportCommand: "bd export"),
        new string('a', 64), new("demo", new string('b', 64)), "sha256/" + new string('a', 64));

    internal static PlanningDocument InputPlan()
    {
        var plan = Plan(new("head", "main", "inputs"));
        return plan with { LocalInputs = [SyntheticInput()], Tasks = [plan.Tasks![0] with { LocalInputIds = ["export"] }] };
    }

    [Fact]
    public void LocalInputsRoundTripAndBindApprovalToExactSourceAndTaskUse()
    {
        var plan = InputPlan();
        var candidate = Comment("1", plan);
        var parsed = PlanningEvidence.Parse(candidate.Text)!;
        Assert.Null(PlanningEvidence.ValidateCandidate(parsed));
        Assert.Equal(SyntheticInput(), Assert.Single(parsed.LocalInputs!));
        Assert.Equal("export", Assert.Single(parsed.Tasks![0].LocalInputIds!));
        var review = Comment("2", new(PlanningEvidence.ReviewSchema, plan.Grounding,
            PlanCommentId: "1", PlanRevision: PlanningEvidence.Hash(candidate.Text), Assessment: "Checked declared inputs"));
        var result = PlanningEvidence.Verify(Invocation(AgentRole.PlanChallenger, plan.Grounding, ["1"]),
            AgentOutcomeChoice.Approved, plan.Grounding, [candidate, review]);
        Assert.Null(result.Error);
        Assert.True(PlanningEvidence.ApprovalStillCurrent(result.Approval!, plan.Grounding, [candidate, review]));
        var changed = plan with { LocalInputs = [SyntheticInput() with { SourcePath = "another/export.json" }] };
        Assert.False(PlanningEvidence.ApprovalStillCurrent(result.Approval!, plan.Grounding, [Comment("1", changed), review]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void LocalInputsRejectMalformedHashes(string hash)
    {
        var input = SyntheticInput();
        Assert.NotNull(PlanningEvidence.ValidateCandidate(InputPlan() with { LocalInputs = [input with { ExpectedSha256 = hash }] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(InputPlan() with { LocalInputs = [input with { Store = input.Store with { RepositoryIdentity = hash } }] }));
    }

    [Theory]
    [InlineData("../export")]
    [InlineData("/absolute")]
    [InlineData("C:\\export")]
    [InlineData("\\\\server\\export")]
    [InlineData("https://example.test/export")]
    [InlineData("sha256/%2e%2e/export")]
    [InlineData("sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/extra")]
    [InlineData("sha256/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void LocalInputsRejectUnsafeOrUnboundLocators(string locator) =>
        Assert.NotNull(PlanningEvidence.ValidateCandidate(InputPlan() with { LocalInputs = [SyntheticInput() with { Locator = locator }] }));

    [Fact]
    public void LocalInputsRejectMissingDuplicateUnknownAndUnboundedFields()
    {
        var plan = InputPlan();
        PlanningLocalInput[] invalid = [null!, SyntheticInput("../bad"), SyntheticInput() with { SourcePath = "" },
            SyntheticInput() with { Source = null! }, SyntheticInput() with { Store = null! },
            SyntheticInput() with { Source = new(" ") }, SyntheticInput() with { SourcePath = new string('s', 4097) },
            SyntheticInput() with { Source = new("tracker", Revision: "") },
            SyntheticInput() with { Source = new("tracker", ExportCommand: "bd\nexport") },
            SyntheticInput() with { Store = new("", new string('b', 64)) }];
        foreach (var input in invalid)
            Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { LocalInputs = [input] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { LocalInputs = [SyntheticInput(), SyntheticInput()] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { LocalInputs = Enumerable.Repeat(SyntheticInput(), 65).ToArray() }));
        foreach (string[] references in new string[][] { ["unknown"], [null!], ["export", "export"], [], Enumerable.Repeat("export", 65).ToArray() })
            Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { Tasks = [plan.Tasks![0] with { LocalInputIds = references }] }));
        Assert.NotNull(PlanningEvidence.ValidateCandidate(plan with { LocalInputs = null }));
        Assert.Null(PlanningEvidence.Parse("{\"localInputs\":\"wrong type\"}"));
    }

    [Fact]
    public void LegacyCandidateKeepsItsWireShapeAndInputReceiptIsOnlyProtocolEvidence()
    {
        var plan = Plan(new("head", "main", "inputs"));
        var candidate = Comment("1", plan);
        Assert.DoesNotContain("localInput", candidate.Text, StringComparison.Ordinal);
        Assert.Null(PlanningEvidence.ValidateCandidate(PlanningEvidence.Parse(candidate.Text)!));
        var receipt = new BeadCommentSnapshot("2", "{\"schema\":\"nightweaver-plan-input-receipt-v1\"}", DateTimeOffset.UnixEpoch);
        Assert.True(PlanningEvidence.IsProtocol(receipt.Text));
        Assert.Equal("1", PlanningEvidence.LatestCandidate([candidate, receipt])!.Value.Comment.Id);
        Assert.NotNull(PlanningEvidence.Verify(Invocation(AgentRole.PlanChallenger, plan.Grounding, ["1"]),
            AgentOutcomeChoice.Approved, plan.Grounding, [candidate, receipt]).Error);
    }

    [Fact]
    public async Task OversizedDiscussionFallsBackToRecentCommentsAndKeepsExactCandidateAndGuidance()
    {
        using var fixture = new PlanningFixture("approval") { DependencyText = new string('d', 135_000) };
        fixture.Comments.Add(new("old", new string('x', 135_000), DateTimeOffset.UnixEpoch));
        fixture.Comments.Add(new("guidance", BeadsWriter.CreateHumanGuidanceComment("note_only", "Keep this decision"), DateTimeOffset.UnixEpoch.AddSeconds(1)));
        fixture.Comments.Add(new("recent", "Keep recent discussion", DateTimeOffset.UnixEpoch.AddSeconds(2)));
        var result = await fixture.RunAsync();
        Assert.True(result.Succeeded);
        using var context = JsonDocument.Parse(result.StageResult.PlanningContext!);
        Assert.Equal(1, context.RootElement.GetProperty("omittedDiscussionCount").GetInt32());
        var excerpt = Assert.Single(context.RootElement.GetProperty("olderDiscussionExcerpts").EnumerateArray());
        Assert.Equal("old", excerpt.GetProperty("id").GetString());
        Assert.Equal(new string('x', 240), excerpt.GetProperty("text").GetString());
        Assert.Contains("Keep this decision", result.StageResult.PlanningContext, StringComparison.Ordinal);
        Assert.Contains("Keep recent discussion", result.StageResult.PlanningContext, StringComparison.Ordinal);
        Assert.Equal(fixture.Comments.Single(c => c.Id == "1").Text,
            context.RootElement.GetProperty("candidateText").GetString());
        Assert.False(context.RootElement.GetProperty("product").TryGetProperty("dependencies", out _));
    }

    [Fact]
    public async Task OversizedRequiredContextPausesAndReleasesWithoutRunningTheAgent()
    {
        using var fixture = new PlanningFixture("approval") { Description = new string('d', 135_000) };
        var groundingBeforePause = await fixture.CaptureAsync();
        var result = await fixture.RunAsync();
        Assert.Equal(OperationalRetryStatus.Escalated, result.Status);
        Assert.True(result.Escalation?.ClaimReleased);
        Assert.Equal(0, result.AttemptCount);
        Assert.Equal(0, fixture.Executions);
        Assert.Equal("stage:plan-challenge", fixture.State.Stage);
        Assert.Equal(0, fixture.State.RepairCycles);
        Assert.True(fixture.State.NeedsHuman);
        Assert.Equal("open", fixture.State.Status);
        Assert.Null(fixture.Owner);
        Assert.Equal(2, fixture.Comments.Count);
        Assert.Contains("131072", fixture.Comments[1].Text, StringComparison.Ordinal);
        Assert.Contains("planning-context-too-large", fixture.Comments[1].Text, StringComparison.Ordinal);
        Assert.NotNull(PlanningEvidence.LatestCandidate(fixture.Comments));
        Assert.Contains("already planned or paused", await fixture.ReadAdmissionRefusalAsync(), StringComparison.Ordinal);
        Assert.Equal(groundingBeforePause.ProductInputs, (await fixture.CaptureAsync()).ProductInputs);
    }

    [Fact]
    public async Task GroundingOverlapsIndependentGitReadsBeforeObservingTracker()
    {
        using var fixture = new PlanningFixture("draft");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var completed = 0;
        var observedTracker = false;
        var trackerReads = 0;
        fixture.BeforeTrackerRead = () =>
        {
            trackerReads++;
            Assert.Equal(4, reads);
            Assert.Equal(4, completed);
            Assert.True(release.Task.IsCompleted);
            observedTracker = true;
        };
        fixture.GitRead = async (request, token) =>
        {
            if (Interlocked.Increment(ref reads) == 4) started.SetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Increment(ref completed);
            return new ProcessResult(0, request.Arguments.Contains("status") ? "" : new string('a', 40), "");
        };
        var capture = fixture.CaptureAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(capture.IsCompleted);
        }
        finally
        {
            release.SetResult();
            await capture;
        }
        Assert.Equal(4, reads);
        Assert.True(observedTracker);
        Assert.Equal(1, trackerReads);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("approval")]
    public async Task IsolatedCheckoutGroundingIgnoresRootEditsAndRefusesDirtyRetainedApproval(string scenario)
    {
        using var fixture = new PlanningFixture(scenario, realGit: true);
        var rootSource = Path.Combine(fixture.RepositoryPath, "Source.cs");
        File.WriteAllText(rootSource, "unrelated root edit");

        var result = await fixture.RunAsync();
        Assert.True(result.Succeeded, result.StageResult.Failure?.Reason ?? fixture.Escalation?.Reason);
        Assert.Equal(1, fixture.Executions);
        Assert.Equal("unrelated root edit", File.ReadAllText(rootSource));
        if (scenario == "draft") return;
        var approval = Assert.IsType<PlanningApproval>(result.StageResult.PlanningApproval);
        var current = await fixture.CaptureAsync();
        Assert.True(PlanningEvidence.ApprovalStillCurrent(approval, current, fixture.Comments));
        Assert.Equal("unrelated root edit", File.ReadAllText(rootSource));

        // Leave the root clean so only the retained checkout can invalidate approval.
        File.WriteAllText(rootSource, "original source");
        var retainedSource = Path.Combine(fixture.WorktreePath, "Source.cs");
        File.WriteAllText(retainedSource, "relevant retained checkout edit");
        var exception = await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.CaptureAsync());
        Assert.Contains("clean isolated Planning checkout", exception.Message, StringComparison.Ordinal);
        Assert.Equal("relevant retained checkout edit", File.ReadAllText(retainedSource));
        Assert.Equal("original source", File.ReadAllText(rootSource));
        Assert.True(File.Exists(fixture.Sentinel));
    }

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
        using var capture = new ActivityCapture();
        using var fixture = new PlanningFixture(scenario);
        var result = await fixture.RunAsync();
        var success = scenario is "draft" or "approval" or "repair" or "question";
        Assert.Equal(success, result.Succeeded);
        Assert.Equal(scenario == "approval", capture.Evidence.Any(e => e.Kind == Nightweaver.ActivityFeed.ActivityKind.PlanningApproved));
        Assert.Equal(scenario == "repair", capture.Evidence.Any(e => e.Kind == Nightweaver.ActivityFeed.ActivityKind.ChangesRequired));
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
            var publication = before.Stage == "stage:plan-approved";
            state = publication ? before with { Stage = null, Planned = true, Status = "open" }
                : role == AgentRole.Planner ? before with { Stage = "stage:plan-challenge" }
                : approval ? before with { Stage = "stage:plan-approved" }
                : before with { Stage = "stage:plan", RepairCycles = before.RepairCycles + 1 };
            var choice = approval ? AgentOutcomeChoice.Approved : role == AgentRole.Planner ? AgentOutcomeChoice.ReadyForReview : AgentOutcomeChoice.ChangesRequired;
            var result = new AgentStageResult(role, "product", "turn", "log", null, [], state,
                PublicationReceipt: new(role, choice, state, publication ? null : "nightweaver:demo", publication, false));
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
        Assert.Equal(new[] { AgentRole.Planner, AgentRole.PlanChallenger, AgentRole.Planner, AgentRole.PlanChallenger, AgentRole.Planner }, roles);
        Assert.Equal(1, state.RepairCycles);
        Assert.True(state.Planned);
        Assert.Null(state.Stage);
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
        private readonly bool realGit;
        internal string RepositoryPath { get; }
        internal string WorktreePath { get; }
        internal BeadWorkflowState Initial { get; }
        internal BeadWorkflowState State { get; private set; }
        internal string? Owner { get; private set; } = "nightweaver:demo";
        internal List<BeadCommentSnapshot> Comments { get; } = [];
        internal PlanningGrounding? Grounding { get; private set; }
        internal NeedsHumanRequest? Escalation { get; private set; }
        internal int Executions { get; private set; }
        internal int PublishWrites { get; private set; }
        internal string Description { get; init; } = "Persist outcomes";
        internal string? DependencyText { get; init; }
        internal Func<ProcessRequest, CancellationToken, Task<ProcessResult>>? GitRead { get; set; }
        internal Action? BeforeTrackerRead { get; set; }
        internal string Sentinel { get; }
        private string DefaultSha => new(scenario == "grounding-change" && Executions > 0 ? 'b' : 'a', 40);

        internal PlanningFixture(string scenario, bool realGit = false)
        {
            this.scenario = scenario;
            this.realGit = realGit;
            paths = new(temp.Path);
            RepositoryPath = temp.CreateDirectory("repository");
            WorktreePath = Path.Combine(temp.Path, "planning");
            if (realGit)
            {
                GitFixture.RunGit(RepositoryPath, ["init", "-b", "main"]);
                temp.WriteFile("repository/Source.cs", "original source");
                GitFixture.RunGit(RepositoryPath, ["add", "Source.cs"]);
                GitFixture.RunGit(RepositoryPath, ["-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "Initial source"]);
                GitFixture.RunGit(RepositoryPath, ["worktree", "add", "--detach", WorktreePath, "HEAD"]);
            }
            else Directory.CreateDirectory(WorktreePath);
            Sentinel = temp.WriteFile("retained/ignored-cache", "preserve this work");
            Initial = new("product", "feature", "in_progress", scenario == "draft" ? "stage:plan" : "stage:plan-challenge",
                false, false, false, scenario == "exhausted" ? 3 : scenario == "overflow" ? int.MaxValue : 0) { ReadyForPlanning = true };
            State = Initial;
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            project = new(ProjectSlug.Parse("demo"), "config.json", new(RepositoryPath, "main"), new(1, 1, 1), new(3, 2),
                settings, new(AgentRoles.All.ToDictionary(role => role, _ => settings)));
            runner = new(RespondAsync);
        }

        private string Inventory() => JsonSerializer.Serialize(new[] { new
        {
            id = State.Id, issue_type = State.IssueType, status = State.Status, assignee = Owner, priority = 2,
            created_at = "2026-11-13T00:00:00Z", title = "Persistence", description = Description,
            dependencies = DependencyText is null ? Array.Empty<object>() : [new { id = "dependency", dependency_type = "blocks", description = DependencyText }],
            labels = new[] { State.Stage, "ready-for-planning" }
                .Concat(State.ProductDecisionRequired ? ["product-decision-required"] : Array.Empty<string>())
                .Concat(State.NeedsHuman ? ["needs-human"] : Array.Empty<string>()), metadata = new { repair_cycles = State.RepairCycles },
            comments = Comments.Select(c => new { id = c.Id, issue_id = "product", text = c.Text, created_at = c.CreatedAt })
        } });

        private Task<ProcessResult> RespondAsync(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string output;
            var args = request.Arguments.ToList();
            string Value(string option) => args[args.IndexOf(option) + 1];
            if (request.FileName != "git") BeforeTrackerRead?.Invoke();
            if (request.FileName == "git")
            {
                if (GitRead is not null) return GitRead(request, token);
                if (realGit) return new SystemProcessRunner().RunAsync(request, token);
                output = args[0] switch { "status" => "", "branch" => "", "rev-parse" => DefaultSha, _ => throw new InvalidOperationException() };
            }
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
                var label = args.Contains("--add-label") ? Value("--add-label") : null;
                State = State with { Stage = label?.StartsWith("stage:", StringComparison.Ordinal) == true ? label : State.Stage,
                    ProductDecisionRequired = label == "product-decision-required", NeedsHuman = State.NeedsHuman || label == "needs-human",
                    Status = args.Contains("--status") ? Value("--status") : State.Status,
                    RepairCycles = args.Contains("--set-metadata") ? State.RepairCycles + 1 : State.RepairCycles };
                if (args.Contains("--assignee")) Owner = Value("--assignee");
                if (Owner?.Length == 0) Owner = null;
                output = Inventory();
            }
            else output = Inventory();
            return Task.FromResult(new ProcessResult(0, output, ""));
        }

        internal async Task<OperationalRetryOutcome> RunAsync()
        {
            var role = scenario == "draft" ? AgentRole.Planner : AgentRole.PlanChallenger;
            var stages = new AgentStageCoordinator(runner,
                provision: async (_, _, _, _, _, token) => new ProvisionedWorktree(WorktreePath,
                    realGit ? (await runner.RunAsync(GitCommand.Create(["rev-parse", "HEAD"], WorktreePath), token)).StandardOutput.Trim()
                        : new string('a', 40)),
                prepare: (_, _, _, _, _, _) => Task.FromResult(new PreparedAgentInvocation("fixture", [], WorktreePath, "prompt",
                    new Dictionary<string, string>(), TimeSpan.FromSeconds(10), "turn", temp.Path, temp.Path + "/log")),
                execute: (_, _, invocation, _) =>
                {
                    Grounding = JsonSerializer.Deserialize<PlanningGrounding>(invocation.StandardInput.Split("Host grounding (copy exactly into evidence):\n")[1], PlanningEvidence.JsonOptions)!;
                    if (scenario == "draft") Comments.Add(Comment("1", Plan(Grounding)));
                    else
                    {
                        // Existing candidate is a prior turn; the fixture exposes it before comment-ID capture below.
                        var candidate = Comments.Single(c => c.Id == "1");
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
                var grounding = await CaptureAsync();
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
                    return request.Cause == NeedsHumanCause.PlanningContextTooLarge
                        ? new NeedsHumanEscalator(runner).EscalateAsync(paths, project, new(RepositoryPath, "main"), request, TestContext.Current.CancellationToken)
                        : Task.FromResult(new NeedsHumanEscalationResult(true, true, true, true, []));
                });
            return await coordinator.RunAsync(paths, project, new(RepositoryPath, "main"), "product", role,
                "nightweaver:demo", TestContext.Current.CancellationToken);
        }

        internal async Task<string?> ReadAdmissionRefusalAsync()
        {
            var snapshot = await new BeadsReader(runner).ReadSnapshotWithReadinessAsync(
                new(RepositoryPath, "main"), project.RecoveryActor, TestContext.Current.CancellationToken);
            return PlanningSlotCoordinator.ValidateAdmission(snapshot.Inventory.ById[State.Id], State, snapshot.Readiness);
        }

        internal Task<PlanningGrounding> CaptureAsync() => new PlanningEvidence(runner).CaptureAsync(
            new(paths, project, new(RepositoryPath, "main"), "product", AgentRole.PlanChallenger,
                WorktreePurpose.Planning, new(WorktreePath, new string('a', 40)), Initial, Owner,
                new("product", "feature", Initial.Stage, Initial.RepairCycles), false), TestContext.Current.CancellationToken);

        public void Dispose() => temp.Dispose();
    }
}
