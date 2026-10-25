using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class OperationalRetryCoordinatorTests
{
    [Fact]
    public async Task InitialSuccessReturnsWithoutResetClassificationOrEscalation()
    {
        var fixture = new RetryFixture(2, new Attempt(0, "stage:review"));

        var outcome = await fixture.RunAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.AttemptCount);
        Assert.Equal("stage:review", fixture.Stage);
        Assert.Equal((1, 0, 0, 0), (fixture.ProvisionCount, fixture.ResetCount, fixture.CompareCount, fixture.EscalateCount));
    }

    [Fact]
    public async Task OperationalFailureResetsOneRetainedWorktreeAndCanSucceedWithoutRepairCycleMutation()
    {
        var fixture = new RetryFixture(2,
            new Attempt(19, null),
            new Attempt(0, "stage:review"));
        var order = new List<string>();
        fixture.OnAttempt = order.Add;
        fixture.OnReset = () => order.Add("reset");

        var outcome = await fixture.RunAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal("stage:review", fixture.Stage);
        Assert.Equal(4, fixture.RepairCycles);
        Assert.Equal(1, fixture.ProvisionCount);
        Assert.Equal(1, fixture.ResetCount);
        Assert.Equal(0, fixture.CompareCount);
        Assert.Equal(0, fixture.EscalateCount);
        Assert.Equal(["attempt-1", "reset", "attempt-2"], order);
    }

    [Fact]
    public async Task DuplicateAdjacentFailuresEscalateAndSkipRemainingAttemptsAndReset()
    {
        var fixture = new RetryFixture(3,
            new Attempt(19, null),
            new Attempt(23, null),
            new Attempt(0, "stage:review"))
        {
            Verdict = DuplicateFailureVerdict.Duplicate
        };

        var outcome = await fixture.RunAsync();

        Assert.False(outcome.Succeeded);
        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal(NeedsHumanCause.DuplicateFailure, outcome.EscalationCause);
        Assert.Equal(2, fixture.AttemptCount);
        Assert.Equal(1, fixture.ResetCount);
        Assert.Equal(1, fixture.CompareCount);
        Assert.Equal(1, fixture.EscalateCount);
        Assert.Equal(NeedsHumanCause.DuplicateFailure, fixture.LastEscalation!.Cause);
        Assert.Equal("stage:implement", fixture.LastComparisonContext!.StageLabel);
        Assert.Equal("invocation-1", fixture.LastComparisonPrevious!.InvocationId);
        Assert.Equal("invocation-2", fixture.LastComparisonLatest!.InvocationId);
        Assert.NotEqual(fixture.LastComparisonPrevious!.InvocationId, fixture.LastComparisonLatest!.InvocationId);
    }

    [Fact]
    public async Task DifferentFailuresContinueToFinalRetryButFinalFailureIsNotClassified()
    {
        var fixture = new RetryFixture(2,
            new Attempt(19, null),
            new Attempt(23, null),
            new Attempt(29, null))
        {
            Verdict = DuplicateFailureVerdict.Different
        };

        var outcome = await fixture.RunAsync();

        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(3, outcome.AttemptCount);
        Assert.Equal(NeedsHumanCause.OperationalRetriesExhausted, outcome.EscalationCause);
        Assert.Equal(2, fixture.ResetCount);
        Assert.Equal(1, fixture.CompareCount);
        Assert.Equal(1, fixture.EscalateCount);
        Assert.Equal(DuplicateFailureVerdict.Different, outcome.LastClassification!.Verdict);
    }

    [Fact]
    public async Task InconclusiveAdjacentComparisonsContinueThroughBudgetThree()
    {
        var fixture = new RetryFixture(3,
            new Attempt(19, null),
            new Attempt(23, null),
            new Attempt(29, null),
            new Attempt(0, "stage:review"));

        var outcome = await fixture.RunAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(4, outcome.AttemptCount);
        Assert.Equal(3, fixture.ResetCount);
        Assert.Equal(2, fixture.CompareCount);
        Assert.Equal([("invocation-1", "invocation-2"), ("invocation-2", "invocation-3")], fixture.ComparisonPairs);
        Assert.Equal(DuplicateFailureVerdict.Inconclusive, outcome.LastClassification!.Verdict);
    }

    [Fact]
    public async Task ProgressReportsActualAttemptsRetriesAndClassifierVerdictInOrder()
    {
        var fixture = new RetryFixture(2,
            new Attempt(19, null),
            new Attempt(23, null),
            new Attempt(0, "stage:review"))
        {
            Verdict = DuplicateFailureVerdict.Different
        };
        var events = new List<OperationalRetryProgress>();

        var outcome = await fixture.RunAsync(events.Add);

        Assert.True(outcome.Succeeded);
        Assert.Equal(
            [
                OperationalRetryProgressKind.AttemptStarted,
                OperationalRetryProgressKind.RetryPreparing,
                OperationalRetryProgressKind.AttemptStarted,
                OperationalRetryProgressKind.ClassifierStarted,
                OperationalRetryProgressKind.ClassifierCompleted,
                OperationalRetryProgressKind.RetryPreparing,
                OperationalRetryProgressKind.AttemptStarted
            ],
            events.Select(item => item.Kind));
        Assert.Equal(3, events.Last(item => item.Kind == OperationalRetryProgressKind.AttemptStarted).Attempt);
        var retry = events.First(item => item.Kind == OperationalRetryProgressKind.RetryPreparing);
        Assert.Equal("invocation-1", retry.InvocationId);
        Assert.Equal("C:\\logs\\1.log", retry.LogFilePath);
        var classifier = events.Single(item => item.Kind == OperationalRetryProgressKind.ClassifierCompleted);
        Assert.Equal(DuplicateFailureVerdict.Different,
            classifier.ClassifierVerdict);
        Assert.Equal("classifier-invocation", classifier.ClassifierInvocationId);
        Assert.Equal("C:\\logs\\classifier.log", classifier.ClassifierLogFilePath);
    }

    [Fact]
    public async Task OneRetryBudgetDoesNotClassifyItsFinalFailure()
    {
        var fixture = new RetryFixture(1, new Attempt(19, null), new Attempt(23, null));

        var outcome = await fixture.RunAsync();

        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal(0, fixture.CompareCount);
        Assert.Equal(1, fixture.ResetCount);
        Assert.Equal(NeedsHumanCause.OperationalRetriesExhausted, outcome.EscalationCause);
    }

    [Theory]
    [InlineData("returned")]
    [InlineData("needs-human-exception")]
    [InlineData("cancellation")]
    public async Task IncompleteEscalationRetainsProgressAndLatestFailedStage(string failureKind)
    {
        var fixture = new RetryFixture(0, new Attempt(19, null));
        var progress = new NeedsHumanEscalationResult(true, true, false, true, ["worktree cleanup failed"]);
        fixture.EscalationBehavior = (_, _) => failureKind switch
        {
            "returned" => Task.FromResult(progress),
            "needs-human-exception" => Task.FromException<NeedsHumanEscalationResult>(
                new NeedsHumanEscalationException("cleanup failed", progress, new InvalidOperationException("cleanup failed"))),
            "cancellation" => Task.FromException<NeedsHumanEscalationResult>(CancellationWith(progress)),
            _ => throw new InvalidOperationException($"Unknown fixture failure kind '{failureKind}'.")
        };

        var exception = await Assert.ThrowsAsync<OperationalRetryEscalationIncompleteException>(() => fixture.RunAsync());

        Assert.Same(progress, exception.Progress);
        Assert.Equal(NeedsHumanCause.OperationalRetriesExhausted, exception.Outcome.EscalationCause);
        Assert.Equal(1, exception.Outcome.AttemptCount);
        Assert.Equal(AgentRole.Implementer, exception.Outcome.StageResult.Role);
        Assert.Equal("invocation-1", exception.Outcome.StageResult.InvocationId);
        Assert.Equal("C:\\logs\\1.log", exception.Outcome.StageResult.LogFilePath);
        Assert.False(exception.Progress.CleanupCompleted);
        Assert.True(exception.Progress.ClaimReleased);
    }

    private static OperationCanceledException CancellationWith(NeedsHumanEscalationResult progress)
    {
        var exception = new OperationCanceledException("escalation cancelled");
        exception.Data[nameof(NeedsHumanEscalationResult)] = progress;
        return exception;
    }

    [Fact]
    public async Task RetryRefusalForAdvancedStageUsesEnvironmentBlockWithoutClassifierOrReset()
    {
        var fixture = new RetryFixture(2, new Attempt(19, "stage:review"));

        var outcome = await fixture.RunAsync();

        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(1, outcome.AttemptCount);
        Assert.Equal(NeedsHumanCause.EnvironmentBlocked, outcome.EscalationCause);
        Assert.Equal(0, fixture.ResetCount);
        Assert.Equal(0, fixture.CompareCount);
        Assert.Contains("workflow identity changed", string.Join(" ", fixture.LastEscalation!.LatestFindings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairCounterMutationStopsRetryAndEscalatesAsEnvironmentBlock()
    {
        var fixture = new RetryFixture(2, new Attempt(19, null, RepairCyclesAfter: 5));

        var outcome = await fixture.RunAsync();

        Assert.Equal(NeedsHumanCause.EnvironmentBlocked, outcome.EscalationCause);
        Assert.Equal(5, fixture.RepairCycles);
        Assert.Equal(0, fixture.ResetCount);
        Assert.Equal(0, fixture.CompareCount);
        Assert.Equal(WorktreePurpose.Delivery, fixture.LastEscalation!.CleanupPurpose);
    }

    [Fact]
    public async Task ChangedBeadTypeUsesCurrentTypeForCleanupWhileRetainingFailedRole()
    {
        var fixture = new RetryFixture(2, new Attempt(19, null, IssueTypeAfter: "feature"));

        var outcome = await fixture.RunAsync();

        Assert.Equal(NeedsHumanCause.EnvironmentBlocked, outcome.EscalationCause);
        Assert.Equal(WorktreePurpose.Planning, fixture.LastEscalation!.CleanupPurpose);
        Assert.Equal(AgentRole.Implementer, fixture.LastEscalation.Role);
        Assert.Equal(0, fixture.ResetCount);
    }

    [Fact]
    public async Task ConflictingPauseLabelsAreHandedToEscalatorForDeterministicResolution()
    {
        var fixture = new RetryFixture(2, new Attempt(19, null,
            AdditionalLabels: ["product-decision-required", "needs-human"]));

        var outcome = await fixture.RunAsync();

        Assert.Equal(NeedsHumanCause.EnvironmentBlocked, outcome.EscalationCause);
        Assert.Equal(1, fixture.EscalateCount);
        Assert.Equal(0, fixture.ResetCount);
    }

    [Fact]
    public async Task TerminalStateAndForeignClaimRefuseWithoutTrackerMutation()
    {
        var terminal = new RetryFixture(2, new Attempt(19, null, StatusAfter: "closed"));
        await Assert.ThrowsAsync<AgentStageInputException>(() => terminal.RunAsync());
        Assert.Equal(0, terminal.ResetCount);
        Assert.Equal(0, terminal.EscalateCount);

        var foreign = new RetryFixture(2, new Attempt(19, null, AssigneeAfter: "someone-else"));
        await Assert.ThrowsAsync<AgentStageInputException>(() => foreign.RunAsync());
        Assert.Equal(0, foreign.ResetCount);
        Assert.Equal(0, foreign.EscalateCount);
    }

    [Fact]
    public async Task LostClaimStopsWithoutEscalationOrReset()
    {
        var fixture = new RetryFixture(2, new Attempt(19, null, AssigneeAfter: "someone-else"));

        await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.RunAsync());

        Assert.Equal(0, fixture.ResetCount);
        Assert.Equal(0, fixture.CompareCount);
        Assert.Equal(0, fixture.EscalateCount);
    }

    [Fact]
    public async Task ZeroAndNegativeRetryBudgetsHaveSafeBoundaries()
    {
        var zero = new RetryFixture(0, new Attempt(19, null));
        var outcome = await zero.RunAsync();
        Assert.Equal(OperationalRetryStatus.Escalated, outcome.Status);
        Assert.Equal(1, outcome.AttemptCount);
        Assert.Equal(0, zero.ResetCount);
        Assert.Equal(0, zero.CompareCount);

        var negative = new RetryFixture(-1, new Attempt(0, "stage:review"));
        await Assert.ThrowsAsync<AgentStageInputException>(() => negative.RunAsync());
        Assert.Equal(0, negative.ProvisionCount);
        Assert.Equal(0, negative.AttemptCount);
        Assert.Equal((long)int.MaxValue + 1, new LimitsConfig(3, int.MaxValue).MaxAttemptsPerStage);
    }

    private sealed record Attempt(
        int ExitCode,
        string? StageAfter,
        string? AssigneeAfter = null,
        string? IssueTypeAfter = null,
        int? RepairCyclesAfter = null,
        string? StatusAfter = null,
        IReadOnlyList<string>? AdditionalLabels = null);

    private sealed class RetryFixture
    {
        private const string Bead = "fixture-bead";
        private const string Owner = "nightweaver:demo";
        private readonly HostPaths host = new("C:\\nightweaver");
        private readonly ProjectConfig project;
        private readonly ValidatedTargetRepository repository = new("C:\\target", "main");
        private readonly Queue<Attempt> attempts;
        private string currentStage = "stage:implement";
        private string currentIssueType = "task";
        private int currentRepairCycles = 4;
        private string currentStatus = "in_progress";
        private readonly List<string> additionalLabels = [];
        private string currentAssignee = Owner;
        private int invocation;
        private string head = new('a', 40);

        internal RetryFixture(int retries, params Attempt[] attempts)
        {
            this.attempts = new(attempts);
            var roles = AgentRoles.All.ToDictionary(role => role,
                _ => new AgentSettings(AgentCli.Claude, null, "low", 1));
            project = new(ProjectSlug.Parse("demo"), "fixture.json", new(repository.Path, "main"),
                new(1, 1, 1), new(3, retries), new(AgentCli.Claude, null, "low", 1), new RoleSettings(roles));
            var runner = new RecordingProcessRunner((request, _) =>
            {
                if (request.FileName == "bd")
                    return Task.FromResult(new ProcessResult(0, Inventory(), ""));
                if (request.FileName == "git" && request.Arguments.SequenceEqual(["status", "--porcelain"]))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (request.FileName == "git" && request.Arguments.SequenceEqual(["rev-parse", "HEAD"]))
                    return Task.FromResult(new ProcessResult(0, head + "\n", ""));
                throw new InvalidOperationException($"Unexpected fake process: {request.FileName} {string.Join(' ', request.Arguments)}");
            });

            var stages = new AgentStageCoordinator(runner,
                provision: (_, _, _, beadId, purpose, _) =>
                {
                    Assert.Equal(Bead, beadId);
                    Assert.Equal(WorktreePurpose.Delivery, purpose);
                    ProvisionCount++;
                    return Task.FromResult(new ProvisionedWorktree(Path.Combine(host.WorktreesRoot(project.Name), beadId), head));
                },
                prepare: (_, _, _, _, worktreePath, _) => Task.FromResult(new PreparedAgentInvocation(
                    "fixture", [], worktreePath, "prompt", new Dictionary<string, string>(), TimeSpan.FromMinutes(1),
                    $"invocation-{invocation + 1}", $"C:\\tmp\\{invocation + 1}", $"C:\\logs\\{invocation + 1}.log")),
                execute: (_, _, prepared, _) =>
                {
                    invocation++;
                    AttemptCount++;
                    OnAttempt?.Invoke($"attempt-{invocation}");
                    var action = this.attempts.Dequeue();
                    if (action.StageAfter is not null) currentStage = action.StageAfter;
                    if (action.AssigneeAfter is not null) currentAssignee = action.AssigneeAfter;
                    if (action.IssueTypeAfter is not null) currentIssueType = action.IssueTypeAfter;
                    if (action.RepairCyclesAfter is not null) currentRepairCycles = action.RepairCyclesAfter.Value;
                    if (action.StatusAfter is not null) currentStatus = action.StatusAfter;
                    if (action.AdditionalLabels is not null) additionalLabels.AddRange(action.AdditionalLabels);
                    if (action.ExitCode == 0) currentStage = "stage:review";
                    return Task.FromResult(new AgentExecutionResult(AgentExecutionStatus.Exited, action.ExitCode,
                        prepared.InvocationId, prepared.LogFilePath));
                });

            Coordinator = new OperationalRetryCoordinator(runner, stages,
                reset: (_, _, _, _, _, _) =>
                {
                    ResetCount++;
                    OnReset?.Invoke();
                    head = new('b', 40);
                    return Task.FromResult(new ProvisionedWorktree(Path.Combine(host.WorktreesRoot(project.Name), Bead), head));
                },
                compare: (_, _, context, _, _, previous, latest, _) =>
                {
                    CompareCount++;
                    LastComparisonContext = context;
                    LastComparisonPrevious = previous;
                    LastComparisonLatest = latest;
                    ComparisonPairs.Add((previous.InvocationId, latest.InvocationId));
                    return Task.FromResult(new DuplicateFailureClassificationResult(Verdict, null, $"{Verdict} fixture verdict",
                        "classifier-invocation", "C:\\logs\\classifier.log", previous.InvocationId, previous.LogFilePath,
                        latest.InvocationId, latest.LogFilePath));
                },
                escalate: (_, _, _, request, token) =>
                {
                    EscalateCount++;
                    LastEscalation = request;
                    if (EscalationBehavior is { } behavior) return behavior(request, token);
                    return Task.FromResult(new NeedsHumanEscalationResult(true, true, true, true, []));
                });
        }

        internal OperationalRetryCoordinator Coordinator { get; }
        internal int ProvisionCount { get; private set; }
        internal int AttemptCount { get; private set; }
        internal int ResetCount { get; private set; }
        internal int CompareCount { get; private set; }
        internal int EscalateCount { get; private set; }
        internal string Stage => currentStage;
        internal int RepairCycles => currentRepairCycles;
        internal List<(string Previous, string Latest)> ComparisonPairs { get; } = [];
        internal DuplicateFailureVerdict Verdict { get; init; } = DuplicateFailureVerdict.Inconclusive;
        internal NeedsHumanRequest? LastEscalation { get; private set; }
        internal AgentDispatchContext? LastComparisonContext { get; private set; }
        internal AgentStageResult? LastComparisonPrevious { get; private set; }
        internal AgentStageResult? LastComparisonLatest { get; private set; }
        internal Action<string>? OnAttempt { get; set; }
        internal Action? OnReset { get; set; }
        internal Func<NeedsHumanRequest, CancellationToken, Task<NeedsHumanEscalationResult>>? EscalationBehavior { get; set; }

        internal Task<OperationalRetryOutcome> RunAsync(Action<OperationalRetryProgress>? progress = null) =>
            Coordinator.RunAsync(host, project, repository, Bead, AgentRole.Implementer, Owner,
                TestContext.Current.CancellationToken, progress);

        private string Inventory() => JsonSerializer.Serialize(new[]
        {
            new
            {
                id = Bead,
                status = currentStatus,
                issue_type = currentIssueType,
                priority = 2,
                created_at = "2026-10-24T00:00:00Z",
                assignee = currentAssignee,
                labels = new[] { currentStage }.Concat(additionalLabels).ToArray(),
                metadata = new { repair_cycles = currentRepairCycles }
            }
        });
    }
}
