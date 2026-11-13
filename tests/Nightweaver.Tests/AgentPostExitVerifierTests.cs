using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class AgentPostExitVerifierTests
{
    private static readonly string[] StatusArguments = ["status", "--porcelain"];
    private static readonly string[] BranchArguments = ["branch", "--show-current"];
    private static readonly string[] HeadArguments = ["rev-parse", "HEAD"];

    [Theory]
    [InlineData("timeout", "TimedOut")]
    [InlineData("nonzero", "NonZeroExit")]
    [InlineData("dirty", "DirtyWorktree")]
    [InlineData("head", "ForbiddenHeadMutation")]
    [InlineData("noop", "IllegalBeadTransition")]
    [InlineData("missing-counter", "IllegalBeadTransition")]
    public async Task ClassifiesFailuresAndPreservesInvocationEvidence(string scenario, string expectedKind)
    {
        var role = scenario is "noop" or "missing-counter" ? AgentRole.PlanChallenger : AgentRole.Reviewer;
        var result = await VerifyAsync(role, scenario);

        Assert.Equal(Enum.Parse<AgentOperationalFailureKind>(expectedKind), result.Failure?.Kind);
        Assert.Equal("invocation", result.InvocationId);
        Assert.Equal("agent.log", result.LogFilePath);
        if (role is AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa or AgentRole.Merger)
        {
            Assert.NotNull(result.Observations);
            Assert.Null(result.VerifiedState);
        }
        else
        {
            Assert.Null(result.Observations);
            Assert.NotNull(result.VerifiedState);
        }
        if (scenario == "dirty") Assert.Contains("dirty.txt", result.Failure!.Reason);
        if (scenario == "nonzero") Assert.Contains("19", result.Failure!.Reason);
        if (scenario == "missing-counter") Assert.Contains("repair_cycles", result.Failure!.Reason);
    }

    [Theory]
    [InlineData("Planner")]
    [InlineData("PlanChallenger")]
    [InlineData("Reviewer")]
    [InlineData("Qa")]
    [InlineData("Implementer")]
    [InlineData("Merger")]
    public async Task HeadMutationIsForbiddenOnlyForReadOnlyRoles(string roleName)
    {
        var role = Enum.Parse<AgentRole>(roleName);
        var result = await VerifyAsync(role, "head");

        if (role is AgentRole.Implementer or AgentRole.Merger)
        {
            Assert.True(result.Succeeded);
            Assert.Null(result.VerifiedState);
            Assert.NotNull(result.Observations);
            Assert.Equal(new string('b', 40), result.Observations.HeadSha);
            Assert.Equal("nightweaver/bead", result.Observations.BranchName);
        }
        else
        {
            Assert.Equal(AgentOperationalFailureKind.ForbiddenHeadMutation, result.Failure?.Kind);
        }
    }

    [Theory]
    [InlineData("dirty", "DirtyWorktree")]
    [InlineData("head", "ForbiddenHeadMutation")]
    [InlineData("timeout", "TimedOut")]
    [InlineData("nonzero", "NonZeroExit")]
    public async Task QaStillRejectsIntegrityAndExecutionFailures(string scenario, string expected)
    {
        var result = await VerifyAsync(AgentRole.Qa, scenario);

        Assert.False(result.Succeeded);
        Assert.Equal(Enum.Parse<AgentOperationalFailureKind>(expected), result.Failure?.Kind);
    }

    [Theory]
    [InlineData("all-timeout", "TimedOut", 3)]
    [InlineData("all-nonzero", "NonZeroExit", 3)]
    [InlineData("all-exited", "DirtyWorktree", 2)]
    public async Task FailurePrecedenceRetainsSecondaryFindings(string scenario, string expectedKind, int secondaryCount)
    {
        var result = await VerifyAsync(AgentRole.PlanChallenger, scenario);

        Assert.Equal(Enum.Parse<AgentOperationalFailureKind>(expectedKind), result.Failure?.Kind);
        Assert.Equal(secondaryCount, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("IllegalBeadTransition:", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("ForbiddenHeadMutation:", StringComparison.Ordinal));
        if (secondaryCount == 3)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("DirtyWorktree:", StringComparison.Ordinal));
    }

    private static Task<AgentStageResult> VerifyAsync(AgentRole role, string scenario)
    {
        var (type, beforeStage, legalAfterStage) = role switch
        {
            AgentRole.Planner => ("feature", "stage:plan", "stage:plan-challenge"),
            AgentRole.PlanChallenger => ("feature", "stage:plan-challenge", "stage:plan-approved"),
            AgentRole.Implementer => ("task", "stage:implement", "stage:implement"),
            AgentRole.Reviewer => ("task", "stage:review", "stage:review"),
            AgentRole.Qa => ("task", "stage:qa", "stage:qa"),
            AgentRole.Merger => ("task", "stage:merge", "stage:merge"),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var deliveryOrMerger = role is AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa or AgentRole.Merger;
        var beforeStatus = deliveryOrMerger ? "in_progress" : "open";
        var beforeAssignee = deliveryOrMerger ? "nightweaver:demo" : null;
        var beforeCycles = scenario is "noop" or "missing-counter" or "all-timeout" or "all-nonzero" or "all-exited" ? 2 : 0;
        var allFindings = scenario.StartsWith("all-", StringComparison.Ordinal);
        var afterStage = allFindings || scenario is "noop" or "missing-counter" ? beforeStage : legalAfterStage;
        if (scenario == "missing-counter") afterStage = "stage:plan";
        var afterCycles = beforeCycles;
        var afterHead = scenario == "head" || allFindings ? new string('b', 40) : new string('a', 40);
        var worktreeStatus = scenario == "dirty" || allFindings ? "?? dirty.txt\n" : string.Empty;
        var labels = new[] { afterStage };
        var metadata = afterCycles == 0
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["repair_cycles"] = afterCycles };
        var bead = new Dictionary<string, object?>
        {
            ["id"] = "bead",
            ["status"] = beforeStatus,
            ["issue_type"] = type,
            ["priority"] = 2,
            ["created_at"] = "2026-10-24T00:00:00Z",
            ["assignee"] = beforeAssignee,
            ["labels"] = labels,
            ["metadata"] = metadata
        };
        var beadJson = JsonSerializer.Serialize(new[] { bead });
        var runner = new RecordingProcessRunner((request, _) => Task.FromResult(request.FileName switch
        {
            "bd" => new ProcessResult(0, beadJson, ""),
            "git" when request.Arguments.SequenceEqual(StatusArguments) => new ProcessResult(0, worktreeStatus, ""),
            "git" when request.Arguments.SequenceEqual(BranchArguments) => new ProcessResult(0, "nightweaver/bead\n", ""),
            "git" when request.Arguments.SequenceEqual(HeadArguments) => new ProcessResult(0, afterHead, ""),
            _ => throw new InvalidOperationException($"Unexpected process: {request.FileName} {string.Join(' ', request.Arguments)}")
        }));
        var execution = new AgentExecutionResult(
            scenario is "timeout" or "all-timeout" ? AgentExecutionStatus.TimedOut : AgentExecutionStatus.Exited,
            scenario is "nonzero" or "all-nonzero" ? 19 : 0,
            "invocation", "agent.log");
        var before = new BeadWorkflowState("bead", type, beforeStatus, beforeStage, false, false, false, beforeCycles);
        return new AgentPostExitVerifier(runner, new BeadsReader(runner)).VerifyAsync(
            new("target", "main"), "worktree", role, before, beforeAssignee, new string('a', 40), execution,
            TestContext.Current.CancellationToken);
    }
}
