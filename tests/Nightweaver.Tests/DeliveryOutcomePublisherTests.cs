using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DeliveryOutcomePublisherTests
{
    [Fact]
    public async Task QaApprovalAtomicallyPublishesMergeStageAndClaimReleaseFromStructuredUpdateReceipt()
    {
        using var fixture = new PublisherFixture(AgentRole.Qa, "stage:qa", AgentOutcomeChoice.Approved);

        var result = await fixture.Publisher.PublishAsync(fixture.Session, fixture.Invocation,
            fixture.Classification, releaseClaim: false, TestContext.Current.CancellationToken);

        Assert.Equal("stage:merge", fixture.State.Stage);
        Assert.Equal("open", fixture.State.Status);
        Assert.Null(fixture.State.Assignee);
        Assert.Equal(1, fixture.Runner.PublishWriteCount);
        Assert.Equal(2, fixture.Runner.InventoryReadCount); // preflight reads only; success receipt adds no Bead read
        Assert.Equal("open", ValueAfter(fixture.Runner.PublishArguments!, "--status"));
        Assert.Equal(string.Empty, ValueAfter(fixture.Runner.PublishArguments!, "--assignee"));
        Assert.Contains("--if-status", fixture.Runner.PublishArguments!);
        Assert.Equal("in_progress", ValueAfter(fixture.Runner.PublishArguments!, "--if-status"));
        Assert.Equal("stage:merge", result.VerifiedState!.Stage);
        Assert.True(result.PublicationReceipt is { HandoffToMerger: true, ClaimReleased: true });
    }

    [Fact]
    public async Task ReviewerRejectionUpdatesSharedRepairBudgetWithStageInOneGuardedWrite()
    {
        using var fixture = new PublisherFixture(AgentRole.Reviewer, "stage:review", AgentOutcomeChoice.ChangesRequired);

        var result = await fixture.Publisher.PublishAsync(fixture.Session, fixture.Invocation,
            fixture.Classification, releaseClaim: false, TestContext.Current.CancellationToken);

        Assert.Equal("stage:implement", fixture.State.Stage);
        Assert.Equal(1, fixture.State.RepairCycles);
        Assert.Equal("in_progress", fixture.State.Status);
        Assert.Equal(PublisherFixture.Owner, fixture.State.Assignee);
        Assert.Equal(1, fixture.Runner.PublishWriteCount);
        Assert.Equal("repair_cycles=1", ValueAfter(fixture.Runner.PublishArguments!, "--set-metadata"));
        Assert.False(result.PublicationReceipt!.ClaimReleased);
        Assert.Equal(1, result.VerifiedState!.RepairCycles);
    }

    [Fact]
    public async Task MergerRejectionAtomicallyPublishesOneRepairIncrementAndReleasesToDelivery()
    {
        using var fixture = new PublisherFixture(AgentRole.Merger, "stage:merge", AgentOutcomeChoice.ChangesRequired);

        var result = await fixture.Publisher.PublishAsync(fixture.Session, fixture.Invocation,
            fixture.Classification, releaseClaim: true, TestContext.Current.CancellationToken);

        Assert.Equal("stage:implement", fixture.State.Stage);
        Assert.Equal(1, fixture.State.RepairCycles);
        Assert.Equal("open", fixture.State.Status);
        Assert.Null(fixture.State.Assignee);
        Assert.Contains("keep:label", fixture.State.Labels);
        Assert.Equal(1, fixture.Runner.PublishWriteCount);
        Assert.Equal(3, fixture.Runner.InventoryReadCount);
        Assert.Equal("stage:implement", ValueAfter(fixture.Runner.PublishArguments!, "--add-label"));
        Assert.Equal("stage:merge", ValueAfter(fixture.Runner.PublishArguments!, "--remove-label"));
        Assert.Equal("repair_cycles=1", ValueAfter(fixture.Runner.PublishArguments!, "--set-metadata"));
        Assert.Equal("open", ValueAfter(fixture.Runner.PublishArguments!, "--status"));
        Assert.Equal(string.Empty, ValueAfter(fixture.Runner.PublishArguments!, "--assignee"));
        Assert.Equal("in_progress", ValueAfter(fixture.Runner.PublishArguments!, "--if-status"));
        Assert.Equal(PublisherFixture.Owner, ValueAfter(fixture.Runner.PublishArguments!, "--if-assignee"));
        Assert.Equal("bd", fixture.Runner.Requests[^1].FileName);
        Assert.Contains("update", fixture.Runner.Requests[^1].Arguments);
        Assert.Equal(AgentOutcomeChoice.ChangesRequired, result.PublicationReceipt!.Outcome);
        Assert.Equal(1, result.PublicationReceipt.PublishedState.RepairCycles);
        Assert.True(result.PublicationReceipt.ClaimReleased);
        Assert.False(result.PublicationReceipt.HandoffToMerger);
        Assert.Null(result.PublicationReceipt.PublishedAssignee);
    }

    [Fact]
    public async Task LostMergerHandoffResponseAcceptsAlreadyClaimedReceiverWithoutASecondWrite()
    {
        using var fixture = new PublisherFixture(AgentRole.Merger, "stage:merge", AgentOutcomeChoice.ChangesRequired)
        {
            FailAfterCommittedHandoffWithReceiver = true
        };

        var result = await fixture.Publisher.PublishAsync(fixture.Session, fixture.Invocation,
            fixture.Classification, releaseClaim: true, TestContext.Current.CancellationToken);

        Assert.Equal("stage:implement", fixture.State.Stage);
        Assert.Equal(1, fixture.State.RepairCycles);
        Assert.Equal("in_progress", fixture.State.Status);
        Assert.Equal("nightweaver:merger", fixture.State.Assignee);
        Assert.True(result.PublicationReceipt is { ClaimReleased: true, Outcome: AgentOutcomeChoice.ChangesRequired });
        Assert.Equal(4, fixture.Runner.InventoryReadCount);
        Assert.Equal(1, fixture.Runner.PublishWriteCount);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("workflow")]
    public async Task MergerRefusesAtomicPublicationWhenOwnerOrWorkflowChangesAfterEvidence(string changeKind)
    {
        using var fixture = new PublisherFixture(AgentRole.Merger, "stage:merge", AgentOutcomeChoice.ChangesRequired)
        {
            ChangeAfterEvidenceComment = changeKind
        };

        await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.Publisher.PublishAsync(
            fixture.Session, fixture.Invocation, fixture.Classification,
            releaseClaim: true, TestContext.Current.CancellationToken));

        Assert.Equal(0, fixture.Runner.PublishWriteCount);
        if (changeKind == "owner") Assert.Equal("human@example.invalid", fixture.State.Assignee);
        else Assert.Equal("stage:review", fixture.State.Stage);
    }

    [Fact]
    public async Task MergerOverflowDoesNotPublishAnIncrement()
    {
        using var fixture = new PublisherFixture(AgentRole.Merger, "stage:merge",
            AgentOutcomeChoice.ChangesRequired, initialRepairCycles: int.MaxValue);

        await Assert.ThrowsAsync<DeliveryRepairCounterOverflowException>(() => fixture.Publisher.PublishAsync(
            fixture.Session, fixture.Invocation, fixture.Classification,
            releaseClaim: true, TestContext.Current.CancellationToken));

        Assert.Equal("stage:merge", fixture.State.Stage);
        Assert.Equal(int.MaxValue, fixture.State.RepairCycles);
        Assert.Equal(0, fixture.Runner.PublishWriteCount);
    }

    [Fact]
    public async Task LostQaHandoffResponseRereadsAndAcceptsAlreadyTransferredOwnershipWithoutAnotherWrite()
    {
        using var fixture = new PublisherFixture(AgentRole.Qa, "stage:qa", AgentOutcomeChoice.Approved)
        {
            FailAfterCommittedHandoffWithReceiver = true
        };

        var result = await fixture.Publisher.PublishAsync(fixture.Session, fixture.Invocation,
            fixture.Classification, releaseClaim: false, TestContext.Current.CancellationToken);

        Assert.Equal("stage:merge", fixture.State.Stage);
        Assert.Equal("in_progress", fixture.State.Status);
        Assert.Equal("nightweaver:merger", fixture.State.Assignee);
        Assert.True(result.PublicationReceipt is { HandoffToMerger: true, ClaimReleased: true });
        Assert.Equal("nightweaver:merger", result.PublicationReceipt!.PublishedAssignee);
        Assert.Equal(1, fixture.Runner.PublishWriteCount);
        Assert.Equal(3, fixture.Runner.InventoryReadCount); // one fresh reread resolves the lost response
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("workflow")]
    public async Task RefusesPublicationWhenOwnerOrWorkflowChangesBeforeGuardedWrite(string changeKind)
    {
        using var fixture = new PublisherFixture(AgentRole.Qa, "stage:qa", AgentOutcomeChoice.Approved)
        {
            ChangeAfterEvidenceComment = changeKind
        };

        await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.Publisher.PublishAsync(
            fixture.Session, fixture.Invocation, fixture.Classification,
            releaseClaim: false, TestContext.Current.CancellationToken));

        Assert.Equal(0, fixture.Runner.PublishWriteCount);
        if (changeKind == "owner") Assert.Equal("human@example.invalid", fixture.State.Assignee);
        else Assert.Equal("stage:review", fixture.State.Stage);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = -1;
        for (var position = 0; position < arguments.Count; position++)
            if (string.Equals(arguments[position], option, StringComparison.Ordinal)) { index = position; break; }
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing value after {option}.");
        return arguments[index + 1];
    }

    private sealed class PublisherFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal const string BeadId = "ns-pub.1";
        internal const string Owner = "nightweaver:demo";

        internal PublisherFixture(AgentRole role, string stage, AgentOutcomeChoice outcome, int initialRepairCycles = 0)
        {
            var target = temp.CreateDirectory("target");
            var host = new HostPaths(temp.CreateDirectory("host"));
            var worktreePath = Directory.CreateDirectory(Path.Combine(host.WorktreesRoot(ProjectSlug.Parse("demo")), BeadId)).FullName;
            var settings = AgentRoles.All.ToDictionary(item => item,
                _ => new AgentSettings(AgentCli.Claude, null, "low", 1));
            var project = new ProjectConfig(ProjectSlug.Parse("demo"), "fixture.json", new("target", "main"),
                new(1, 1, 1), new(3, 1), new(AgentCli.Claude, null, "low", 1), new RoleSettings(settings));
            State = new MutableBeadState(stage);
            State.RepairCycles = initialRepairCycles;
            Runner = new MutableBeadsRunner(State);
            Publisher = new DeliveryOutcomePublisher(Runner);
            var expected = new BeadWorkflowState(BeadId, "task", "in_progress", stage,
                false, false, false, State.RepairCycles);
            var repository = new ValidatedTargetRepository(target, "main");
            Session = new AgentStageSession(host, project, repository, BeadId, role, WorktreePurpose.Delivery,
                new ProvisionedWorktree(worktreePath, new string('a', 40)), expected, Owner,
                new AgentDispatchContext(BeadId, "task", stage, 0), PreserveInheritedChanges: false);
            var observations = new AgentStageObservations(expected, expected, Owner, Owner,
                AgentExecutionStatus.Exited, 0, $"nightweaver/{BeadId}", new string('b', 40), string.Empty, true);
            Invocation = new AgentStageResult(role, BeadId, "invocation-1", "C:\\logs\\invocation-1.log",
                null, [], Observations: observations);
            Classification = new(outcome, "fixture", "role evidence verified");
        }

        internal MutableBeadState State { get; }
        internal MutableBeadsRunner Runner { get; }
        internal DeliveryOutcomePublisher Publisher { get; }
        internal AgentStageSession Session { get; }
        internal AgentStageResult Invocation { get; }
        internal AgentOutcomeClassificationResult Classification { get; }
        internal bool FailAfterCommittedHandoffWithReceiver { get => Runner.FailAfterCommittedHandoffWithReceiver; init => Runner.FailAfterCommittedHandoffWithReceiver = value; }
        internal string? ChangeAfterEvidenceComment { get => Runner.ChangeAfterEvidenceComment; init => Runner.ChangeAfterEvidenceComment = value; }

        public void Dispose() => temp.Dispose();
    }

    private sealed class MutableBeadState(string stage)
    {
        internal string Status { get; set; } = "in_progress";
        internal string? Assignee { get; set; } = PublisherFixture.Owner;
        internal string Stage { get; set; } = stage;
        internal int RepairCycles { get; set; }
        internal List<string> Comments { get; } = [];
        internal string[] Labels => [Stage, "keep:label"];
    }

    private sealed class MutableBeadsRunner(MutableBeadState state) : IProcessRunner
    {
        internal string? ChangeAfterEvidenceComment { get; set; }
        internal bool FailAfterCommittedHandoffWithReceiver { get; set; }
        internal int PublishWriteCount { get; private set; }
        internal int InventoryReadCount { get; private set; }
        internal IReadOnlyList<string>? PublishArguments { get; private set; }
        internal List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.FileName != "bd") throw new InvalidOperationException($"Unexpected process {request.FileName}.");
            var args = request.Arguments;
            if (Contains(args, "comments") && Contains(args, "add"))
            {
                state.Comments.Add(args[^1]);
                if (ChangeAfterEvidenceComment == "owner")
                {
                    state.Assignee = "human@example.invalid";
                    ChangeAfterEvidenceComment = null;
                }
                else if (ChangeAfterEvidenceComment == "workflow")
                {
                    state.Stage = "stage:review";
                    ChangeAfterEvidenceComment = null;
                }
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            if (Contains(args, "comments"))
            {
                var comments = state.Comments.Select((text, index) => new
                {
                    id = $"comment-{index}", issue_id = PublisherFixture.BeadId, author = PublisherFixture.Owner, text,
                    created_at = DateTimeOffset.UnixEpoch.AddSeconds(index)
                });
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(comments), string.Empty));
            }
            if (Contains(args, "update") && Contains(args, "--add-label"))
            {
                PublishWriteCount++;
                PublishArguments = args.ToArray();
                state.Stage = args[IndexOf(args, "--add-label") + 1];
                if (ValueAfterOptional(args, "--remove-label") is { } remove && remove.StartsWith("stage:", StringComparison.Ordinal))
                    state.Stage = string.Empty;
                if (state.Stage == string.Empty) state.Stage = args[IndexOf(args, "--add-label") + 1];
                if (ValueAfterOptional(args, "--set-metadata") is { } metadata)
                    state.RepairCycles = int.Parse(metadata["repair_cycles=".Length..], System.Globalization.CultureInfo.InvariantCulture);
                var status = ValueAfterOptional(args, "--status");
                if (status is not null) state.Status = status;
                var assignee = ValueAfterOptional(args, "--assignee");
                if (assignee is not null) state.Assignee = string.IsNullOrEmpty(assignee) ? null : assignee;
                if (FailAfterCommittedHandoffWithReceiver)
                {
                    FailAfterCommittedHandoffWithReceiver = false;
                    state.Status = "in_progress";
                    state.Assignee = "nightweaver:merger";
                    return Task.FromResult(new ProcessResult(1, string.Empty, "simulated lost write response"));
                }
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[] { Row() }), string.Empty));
            }
            if (Contains(args, "ready") || Contains(args, "--ready"))
            {
                var readinessBead = Row();
                var ready = Contains(args, "list")
                    ? state.Status == "in_progress" && state.Assignee == PublisherFixture.Owner
                    : state.Status == "open" && state.Assignee is null;
                return Task.FromResult(new ProcessResult(0,
                    JsonSerializer.Serialize(ready ? new[] { readinessBead } : Array.Empty<object>()), string.Empty));
            }
            if (Contains(args, "list"))
            {
                InventoryReadCount++;
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[] { Row() }), string.Empty));
            }
            throw new InvalidOperationException($"Unexpected bd arguments: {string.Join(' ', args)}");
        }

        private object Row() => new
        {
            id = PublisherFixture.BeadId, status = state.Status, issue_type = "task", priority = 2,
            created_at = "2026-01-01T00:00:00Z", assignee = state.Assignee,
            labels = state.Labels,
            metadata = new { repair_cycles = state.RepairCycles }
        };

        private static bool Contains(IReadOnlyList<string> args, string value) => args.Contains(value, StringComparer.Ordinal);
        private static int IndexOf(IReadOnlyList<string> args, string value)
        {
            for (var index = 0; index < args.Count; index++)
                if (string.Equals(args[index], value, StringComparison.Ordinal)) return index;
            return -1;
        }
        private static string? ValueAfterOptional(IReadOnlyList<string> args, string option)
        {
            var index = IndexOf(args, option);
            return index < 0 ? null : args[index + 1];
        }
    }
}
