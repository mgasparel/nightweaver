using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerSlotCoordinatorTests
{
    [Fact]
    public async Task ForeignClaimRaceRefusesWithoutAgentLaunchOrForeignClaimMutation()
    {
        var runner = new MergerSlotProcessRunner(foreignClaimOnAcquire: true);
        var launches = 0;
        var slot = CreateSlot(runner, (_, _, _, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(Success("candidate", AgentRole.Merger, new("candidate", "task", "closed", "stage:merge", false, false, false, 0)));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Refused, result.Disposition);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Equal(0, launches);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("human:competitor", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
        Assert.Equal("open", MergerSlotProcessRunner.GuardedStatus(Assert.Single(runner.ClaimWrites("candidate"))));
    }

    [Fact]
    public async Task SameActorInProgressRecoveryCandidateIsRefusedWithoutLaunchOrMutation()
    {
        var runner = new MergerSlotProcessRunner(initialStatus: "in_progress", initialAssignee: "nightweaver:merger-slot-tests");
        var launches = 0;
        var slot = CreateSlot(runner, (_, _, _, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            throw new InvalidOperationException("Recovered claims must not bypass startup recovery.");
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Refused, result.Disposition);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Equal(0, launches);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Empty(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task ChangedReadinessAfterGuardedClaimReleasesOnlyItsOwnClaimBeforeDispatch()
    {
        var runner = new MergerSlotProcessRunner(removeReadinessAfterClaim: true);
        var launches = 0;
        var slot = CreateSlot(runner, (_, _, _, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            throw new InvalidOperationException("Agent must not run after readiness changes.");
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Refused, result.Disposition);
        Assert.True(result.ClaimHandoffCompleted, result.Diagnostic);
        Assert.Equal(0, launches);
        Assert.Equal("open", runner.Bead.Status);
        Assert.Null(runner.Bead.Assignee);
        var claimWrites = runner.ClaimWrites("candidate");
        Assert.Equal(2, claimWrites.Length);
        Assert.Equal(["open", "in_progress"], claimWrites.Select(MergerSlotProcessRunner.GuardedStatus));
    }

    [Fact]
    public async Task SemanticRejectionAtBudgetBoundaryReleasesExactlyOnceAndPreservesMetadata()
    {
        var runner = new MergerSlotProcessRunner(initialRepairCycles: 2);
        var requestCountAtReceipt = -1;
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            runner.Bead.Labels.Remove("stage:merge");
            runner.Bead.Labels.Add("stage:implement");
            runner.Bead.RepairCycles++;
            runner.Bead.Comments.Add("Merger found a conflict in src/Feature.cs that needs implementation repair.");
            runner.Bead.Status = "open";
            runner.Bead.Assignee = null;
            var published = runner.ReadState();
            requestCountAtReceipt = runner.RequestCount;
            return Task.FromResult(PublishedOutcome(beadId, role,
                new("candidate", "task", "in_progress", "stage:merge", false, false, false, 2), published,
                null, AgentOutcomeChoice.ChangesRequired, claimReleased: true));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 3), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.RepairHandedOff, result.Disposition);
        Assert.True(result.RepairHandoffCompleted);
        Assert.False(result.MergeCompleted);
        Assert.True(result.ClaimHandoffCompleted);
        Assert.Equal("open", runner.Bead.Status);
        Assert.Null(runner.Bead.Assignee);
        Assert.Equal("stage:implement", result.FinalState!.Stage);
        Assert.Equal(3, result.FinalState.RepairCycles);
        Assert.Contains("keep:label", runner.Bead.Labels);
        Assert.Equal("preserve-me", runner.Bead.KeepMetadata);
        Assert.Equal(requestCountAtReceipt, runner.RequestCount);
        Assert.Single(runner.ClaimWrites("candidate"));
        Assert.Contains("Merger found a conflict", Assert.Single(runner.Bead.Comments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectionBeyondBudgetEscalatesWithoutAnotherCounterIncrement()
    {
        var runner = new MergerSlotProcessRunner();
        NeedsHumanRequest? received = null;
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            runner.Bead.Labels.Remove("stage:merge");
            runner.Bead.Labels.Add("stage:implement");
            runner.Bead.RepairCycles++;
            runner.Bead.Comments.Add("Required local test failed after rebase: dotnet test Feature.csproj");
            runner.Bead.Labels.Add("needs-human");
            runner.Bead.Status = "open";
            runner.Bead.Assignee = null;
            received = new NeedsHumanRequest(beadId, "nightweaver:merger-slot-tests",
                NeedsHumanCause.RepairBudgetExhausted, AgentRole.Merger,
                "repair budget exhausted", [runner.Bead.Comments[^1]], "fixture-invocation",
                CleanupPurpose: WorktreePurpose.Delivery, ExpectedWorkflowState: runner.ReadState());
            var stageResult = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty,
                null, [], runner.ReadState(), OutcomeClassification: new(AgentOutcomeChoice.ChangesRequired,
                    "fixture", "required check failed"));
            var progress = new NeedsHumanEscalationResult(true, true, true, true, []);
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Escalated,
                stageResult, 1, NeedsHumanCause.RepairBudgetExhausted, progress, null));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 0), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Escalated, result.Disposition);
        Assert.True(result.ClaimHandoffCompleted);
        Assert.True(result.EscalationProgress?.Succeeded);
        Assert.Equal(1, runner.Bead.RepairCycles);
        Assert.Equal("stage:implement", runner.ReadState().Stage);
        Assert.True(runner.ReadState().NeedsHuman);
        Assert.Equal("open", runner.Bead.Status);
        Assert.Null(runner.Bead.Assignee);
        Assert.Equal(NeedsHumanCause.RepairBudgetExhausted, received?.Cause);
        Assert.Equal(AgentRole.Merger, received?.Role);
        Assert.Equal(1, received?.ExpectedWorkflowState?.RepairCycles);
        Assert.Contains("Required local test failed", Assert.Single(received!.LatestFindings), StringComparison.Ordinal);
        Assert.Equal("fixture-invocation", received.InvocationId);
    }

    [Fact]
    public async Task AlreadyExhaustedAdmissionEscalatesWithoutLaunchingMergerOrIncrementing()
    {
        var runner = new MergerSlotProcessRunner(initialRepairCycles: 1);
        var launches = 0;
        NeedsHumanRequest? received = null;
        var slot = CreateSlot(runner, (_, _, _, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            throw new InvalidOperationException("Exhausted budget must stop before invocation.");
        }, (_, _, _, request, _) =>
        {
            received = request;
            runner.Bead.Labels.Add("needs-human");
            runner.Bead.Status = "open";
            runner.Bead.Assignee = null;
            return Task.FromResult(new NeedsHumanEscalationResult(true, true, true, true, []));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 0), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Escalated, result.Disposition);
        Assert.Equal(0, launches);
        Assert.Equal(1, runner.Bead.RepairCycles);
        Assert.Equal(1, received?.ExpectedWorkflowState?.RepairCycles);
        Assert.Contains("no Merger invocation was started", received!.Reason, StringComparison.Ordinal);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RejectionWithZeroOrMultipleIncrementsIsRejectedAndClaimRetained(int rejectedRepairCycles)
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            var before = runner.ReadState();
            var published = before with { Stage = "stage:implement", RepairCycles = rejectedRepairCycles, Status = "open" };
            return Task.FromResult(PublishedOutcome(beadId, role, before, published, null,
                AgentOutcomeChoice.ChangesRequired, claimReleased: true));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 3), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.Contains("publication receipt", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("state")]
    public async Task InvalidRepairReceiptDoesNotReportAClaimHandoff(string change)
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            var before = runner.ReadState();
            var published = before with { Stage = "stage:implement", RepairCycles = 1, Status = "open" };
            var publishedOwner = change == "owner" ? "human:receiver" : null;
            if (change == "state") published = published with { Stage = "stage:qa" };
            return Task.FromResult(PublishedOutcome(beadId, role, before, published, publishedOwner,
                AgentOutcomeChoice.ChangesRequired, claimReleased: true));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 3), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.Contains("publication receipt", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Single(runner.ClaimWrites("candidate"));
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
    }

    [Fact]
    public async Task AlreadyClaimedDeliveryReceiverIsAcceptedWithoutPostReceiptBeadsReads()
    {
        var runner = new MergerSlotProcessRunner();
        var requestCountAtReceipt = -1;
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            runner.Bead.Labels.Remove("stage:merge");
            runner.Bead.Labels.Add("stage:implement");
            runner.Bead.RepairCycles++;
            runner.Bead.Status = "in_progress";
            runner.Bead.Assignee = "nightweaver:delivery";
            var published = runner.ReadState();
            requestCountAtReceipt = runner.RequestCount;
            return Task.FromResult(PublishedOutcome(beadId, role,
                new("candidate", "task", "in_progress", "stage:merge", false, false, false, 0),
                published, "nightweaver:delivery", AgentOutcomeChoice.ChangesRequired, claimReleased: true));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 3), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.RepairHandedOff, result.Disposition);
        Assert.True(result.RepairHandoffCompleted);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:delivery", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
        Assert.Equal(requestCountAtReceipt, runner.RequestCount);
    }

    [Fact]
    public async Task ApprovedOutcomeWithoutCompletionReceiptFailsAndRetainsTheMergerClaim()
    {
        var runner = new MergerSlotProcessRunner(initialRepairCycles: 7);
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            var state = runner.ReadState();
            return Task.FromResult(PublishedOutcome(beadId, role, state, state,
                "nightweaver:merger-slot-tests", AgentOutcomeChoice.Approved, claimReleased: false));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 7), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.False(result.MergeCompleted);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Contains("did not return a supported verified rejection or completion publication", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(7, result.FinalState!.RepairCycles);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task MissingVerifiedStateIsFailedAndLeavesTheClaimUntouched()
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            var stageResult = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, []);
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Succeeded,
                stageResult, 1, null, null, null));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Contains("supported verified rejection or completion publication", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task WrongVerifiedResultIdentityIsFailedWithoutClaimRelease()
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, _, _, _, _, _, _) =>
            Task.FromResult(Success("different-bead", AgentRole.Merger,
                new("different-bead", "task", "closed", "stage:merge", false, false, false, 0))));

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.Contains("different role or Bead", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task UnsupportedLifecycleMutationIsNotAcceptedAsCompletion()
    {
        var runner = new MergerSlotProcessRunner(initialRepairCycles: 7);
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            runner.Bead.Status = "closed";
            runner.Bead.RepairCycles++;
            return Task.FromResult(Success(beadId, role, runner.ReadState()));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(maxRepairCycles: 7), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.Contains("supported verified rejection or completion", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Equal(8, runner.Bead.RepairCycles);
        Assert.Equal("closed", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task CompletedOperationalEscalationIsReportedWithoutClosedClaimFinalization()
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            runner.Bead.Status = "open";
            runner.Bead.Assignee = null;
            runner.Bead.Labels.Add("needs-human");
            var stageResult = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, [], runner.ReadState());
            var progress = new NeedsHumanEscalationResult(true, true, true, true, []);
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Escalated, stageResult, 2,
                NeedsHumanCause.OperationalRetriesExhausted, progress, null));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Escalated, result.Disposition);
        Assert.True(result.ClaimHandoffCompleted);
        Assert.True(result.Stage?.Escalation?.Succeeded);
        Assert.Equal("open", runner.Bead.Status);
        Assert.Null(runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task IncompleteOperationalEscalationIsFailedAndNotReportedAsHandoff()
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            var stageResult = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, [], runner.ReadState());
            var progress = new NeedsHumanEscalationResult(true, false, true, false, ["diagnostic write failed"]);
            return Task.FromResult(new OperationalRetryOutcome(OperationalRetryStatus.Escalated, stageResult, 2,
                NeedsHumanCause.OperationalRetriesExhausted, progress, null));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Contains("without a verified claim handoff", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    [Fact]
    public async Task IncompleteEscalationExceptionIsFailedWithoutBlindClaimRelease()
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            var stageResult = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, [], runner.ReadState());
            var progress = new NeedsHumanEscalationResult(true, false, true, false, ["claim release failed"]);
            var outcome = new OperationalRetryOutcome(OperationalRetryStatus.Escalated, stageResult, 2,
                NeedsHumanCause.OperationalRetriesExhausted, progress, null);
            throw new OperationalRetryEscalationIncompleteException("controlled escalation failure", outcome);
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.Failed, result.Disposition);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Contains("controlled escalation failure", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    private static MergerSlotCoordinator CreateSlot(MergerSlotProcessRunner runner, MergerRoleRunner roleRunner,
        MergerRepairBudgetEscalator? escalator = null) => new(runner, roleRunner, escalator);

    private static HostPaths CreateHost() => new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-slot-host")));
    private static ValidatedTargetRepository CreateRepository() =>
        new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-slot-target")), "main");

    private static ProjectConfig CreateProject(int maxRepairCycles = 0)
    {
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        var target = CreateRepository();
        return new ProjectConfig(ProjectSlug.Parse("merger-slot-tests"), "merger-slot-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(1, 3, 1), new LimitsConfig(maxRepairCycles, 3), new AgentSettings(AgentCli.Codex, null, "low", 1),
            new RoleSettings(roles));
    }

    private static OperationalRetryOutcome Success(string beadId, AgentRole role, BeadWorkflowState state)
    {
        var result = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, [], state);
        return new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, result, 1, null, null, null);
    }

    private static OperationalRetryOutcome PublishedOutcome(string beadId, AgentRole role,
        BeadWorkflowState before, BeadWorkflowState publishedState, string? publishedAssignee,
        AgentOutcomeChoice choice, bool claimReleased)
    {
        var classification = new AgentOutcomeClassificationResult(choice, "fixture", "verified by fixture");
        var observations = new AgentStageObservations(before, before, "nightweaver:merger-slot-tests",
            "nightweaver:merger-slot-tests", AgentExecutionStatus.Exited, 0,
            $"nightweaver/{beadId}", new string('c', 40), string.Empty, true);
        var receipt = new DeliveryOutcomePublicationReceipt(role, choice, publishedState, publishedAssignee,
            claimReleased, HandoffToMerger: false);
        var result = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, [],
            publishedState, observations, classification, new HashSet<string>(StringComparer.Ordinal), receipt);
        return new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, result, 1, null, null, null);
    }

    private sealed class MergerSlotProcessRunner(bool foreignClaimOnAcquire = false, bool removeReadinessAfterClaim = false,
        string initialStatus = "open", string? initialAssignee = null, int initialRepairCycles = 0,
        bool releaseFailureAfterWrite = false)
        : IProcessRunner
    {
        private readonly List<ProcessRequest> requests = [];
        internal FakeBead Bead { get; } = new(initialStatus, initialAssignee, initialRepairCycles);
        internal Action? BeforeNextSnapshot { get; set; }
        internal Action? BeforeNextReadiness { get; set; }
        internal int RequestCount => requests.Count;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            var arguments = request.Arguments;
            if (request.FileName != "bd") throw new InvalidOperationException($"Unexpected command: {request.FileName}");
            if (arguments.Contains("update", StringComparer.Ordinal)) return Task.FromResult(Update(arguments));
            if (arguments.Contains("comments", StringComparer.Ordinal))
                return Task.FromResult(Json(Bead.Comments.Select((comment, index) => (object)new
                {
                    id = $"comment-{index}", issue_id = Bead.Id, text = comment, created_at = "2026-01-01T00:00:00Z"
                })));
            if (arguments.Contains("ready", StringComparer.Ordinal) || arguments.Contains("--ready", StringComparer.Ordinal))
                return Task.FromResult(Readiness(arguments));
            if (arguments.Contains("list", StringComparer.Ordinal))
            {
                var beforeNextSnapshot = BeforeNextSnapshot;
                BeforeNextSnapshot = null;
                beforeNextSnapshot?.Invoke();
                return Task.FromResult(Json([ToRow()]));
            }
            throw new InvalidOperationException($"Unexpected bd request: {string.Join(' ', arguments)}");
        }

        internal ProcessRequest[] ClaimWrites(string beadId) => requests.Where(request => request.FileName == "bd"
            && request.Arguments.Contains("update", StringComparer.Ordinal)
            && request.Arguments.Contains(beadId, StringComparer.Ordinal)
            && request.Arguments.Contains("--if-status", StringComparer.Ordinal)
            && request.Arguments.Contains("--if-assignee", StringComparer.Ordinal)).ToArray();

        internal static string? GuardedStatus(ProcessRequest request)
        {
            var index = request.Arguments.ToList().IndexOf("--if-status");
            return index >= 0 && index + 1 < request.Arguments.Count ? request.Arguments[index + 1] : null;
        }

        internal BeadWorkflowState ReadState() => BeadWorkflowState.Parse(BeadsReader.Parse("[" + JsonSerializer.Serialize(ToRow()) + "]").ById[Bead.Id]);

        private ProcessResult Update(IReadOnlyList<string> arguments)
        {
            var guardedStatus = Value(arguments, "--if-status");
            var guardedAssignee = Value(arguments, "--if-assignee");
            var assignee = Value(arguments, "--assignee");
            var newStatus = Value(arguments, "--status");
            if (guardedStatus == "open" && guardedAssignee == string.Empty)
            {
                if (foreignClaimOnAcquire)
                {
                    Bead.Status = "in_progress";
                    Bead.Assignee = "human:competitor";
                    return new ProcessResult(1, string.Empty, "claim condition did not match");
                }
                if (Bead.Status != guardedStatus
                    || !string.Equals(Bead.Assignee ?? string.Empty, guardedAssignee, StringComparison.Ordinal))
                    return new ProcessResult(1, string.Empty, "claim condition did not match");
                Bead.Status = newStatus!;
                Bead.Assignee = assignee;
                if (removeReadinessAfterClaim) Bead.Ready = false;
                return new ProcessResult(0, "{}", string.Empty);
            }

            if (guardedAssignee == "nightweaver:merger-slot-tests" && assignee == string.Empty
                && Bead.Status == guardedStatus && Bead.Assignee == guardedAssignee)
            {
                Bead.Status = guardedStatus == "in_progress" ? "open" : guardedStatus!;
                Bead.Assignee = null;
                if (releaseFailureAfterWrite) return new ProcessResult(1, string.Empty, "fixture lost the release response");
                return new ProcessResult(0, "{}", string.Empty);
            }
            return new ProcessResult(1, string.Empty, "guarded claim state changed");
        }

        private ProcessResult Readiness(IReadOnlyList<string> arguments)
        {
            var beforeNextReadiness = BeforeNextReadiness;
            BeforeNextReadiness = null;
            var isRecoveryRead = arguments.Contains("--ready", StringComparer.Ordinal);
            var ready = Bead.Ready && (isRecoveryRead
                ? Bead.Status == "in_progress" && Bead.Assignee == "nightweaver:merger-slot-tests"
                : Bead.Status == "open" && Bead.Assignee is null);
            var result = TaskResult(ready);
            beforeNextReadiness?.Invoke();
            return result;

            ProcessResult TaskResult(bool include) => include ? Json([ToRow()]) : Json([]);
        }

        private object ToRow() => new
        {
            id = Bead.Id,
            status = Bead.Status,
            issue_type = "task",
            priority = 1,
            created_at = "2026-01-01T00:00:00Z",
            assignee = Bead.Assignee,
            labels = Bead.Labels.ToArray(),
            dependencies = Array.Empty<object>(),
            metadata = new { repair_cycles = Bead.RepairCycles, retained = Bead.KeepMetadata }
        };

        private static ProcessResult Json(IEnumerable<object> rows) =>
            new(0, JsonSerializer.Serialize(rows), string.Empty);

        private static string? Value(IReadOnlyList<string> arguments, string option)
        {
            var index = arguments.ToList().IndexOf(option);
            return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
        }
    }

    private sealed class FakeBead(string status, string? assignee, int repairCycles)
    {
        internal string Id { get; } = "candidate";
        internal string Status { get; set; } = status;
        internal string? Assignee { get; set; } = assignee;
        internal List<string> Labels { get; } = ["stage:merge", "keep:label"];
        internal int RepairCycles { get; set; } = repairCycles;
        internal string KeepMetadata { get; } = "preserve-me";
        internal List<string> Comments { get; } = [];
        internal bool Ready { get; set; } = true;
    }
}
