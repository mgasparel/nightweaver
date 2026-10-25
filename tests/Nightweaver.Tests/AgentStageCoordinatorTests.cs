using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class AgentStageCoordinatorTests
{
    private static readonly string[] StatusArguments = ["status", "--porcelain"];
    private static readonly string[] HeadArguments = ["rev-parse", "HEAD"];
    private static readonly string[] PlanLabels = ["stage:plan"];
    private static readonly string[] ChallengeLabels = ["stage:plan-challenge"];

    [Fact]
    public async Task RunsProvisionFreshBaselinePrepareExecuteAndVerificationInOrder()
    {
        var order = new List<string>();
        var inventoryReads = 0;
        PreparedAgentInvocation? prepared = null;
        var runner = new RecordingProcessRunner((request, _) =>
        {
            if (request.FileName == "bd")
            {
                Assert.Contains("list", request.Arguments);
                order.Add(inventoryReads++ switch { 0 => "initial", 1 => "fresh", _ => "post" });
                var labels = inventoryReads <= 2 ? PlanLabels : ChallengeLabels;
                return Task.FromResult(new ProcessResult(0, Inventory("product", "feature", "open", labels), ""));
            }
            if (request.FileName == "git" && request.Arguments.SequenceEqual(StatusArguments))
            {
                order.Add("verify-status");
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            if (request.FileName == "git" && request.Arguments.SequenceEqual(HeadArguments))
            {
                order.Add("verify-head");
                return Task.FromResult(new ProcessResult(0, new string('a', 40) + "\n", ""));
            }
            throw new InvalidOperationException($"Unexpected process: {request.FileName} {string.Join(' ', request.Arguments)}");
        });

        var coordinator = new AgentStageCoordinator(runner,
            provision: (_, _, _, beadId, purpose, _) =>
            {
                order.Add("provision");
                Assert.Equal("product", beadId);
                Assert.Equal(WorktreePurpose.Planning, purpose);
                return Task.FromResult(new ProvisionedWorktree("C:\\host\\worktrees\\demo\\product", new string('a', 40)));
            },
            prepare: (_, _, role, context, _, _) =>
            {
                order.Add("prepare");
                Assert.Equal(AgentRole.Planner, role);
                Assert.Equal(new AgentDispatchContext("product", "feature", "stage:plan", 0), context);
                prepared = Invocation();
                return Task.FromResult(prepared);
            },
            execute: (_, _, invocation, _) =>
            {
                order.Add("execute");
                Assert.Same(prepared, invocation);
                return Task.FromResult(new AgentExecutionResult(AgentExecutionStatus.Exited, 0, "20261024T0000000000000Z-planner", "C:\\logs\\planner.log"));
            });

        var result = await coordinator.RunAsync(Host(), Project("target"), new("target", "main"), "product",
            AgentRole.Planner, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Failure?.Reason);
        Assert.Equal(["initial", "provision", "fresh", "prepare", "execute", "verify-status", "verify-head", "post"], order);
    }

    [Fact]
    public async Task InvalidInputPreCancelledAndStaleInputNeverLaunch()
    {
        var invalidProvisionCount = 0;
        var invalidRunner = new RecordingProcessRunner((request, _) =>
        {
            Assert.Equal("bd", request.FileName);
            return Task.FromResult(new ProcessResult(0, Inventory("product", "task", "open", []), ""));
        });
        var invalidCoordinator = new AgentStageCoordinator(invalidRunner,
            provision: (_, _, _, _, _, _) => { invalidProvisionCount++; return Task.FromResult(new ProvisionedWorktree("worktree", new string('a', 40))); });

        await Assert.ThrowsAsync<AgentStageInputException>(() => invalidCoordinator.RunAsync(Host(), Project("target"),
            new("target", "main"), "product", AgentRole.Planner, TestContext.Current.CancellationToken));
        Assert.Equal(0, invalidProvisionCount);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invalidCoordinator.RunAsync(Host(), Project("target"),
            new("target", "main"), "product", AgentRole.Planner, cancellation.Token));
        Assert.Single(invalidRunner.Requests);

        await Assert.ThrowsAsync<AgentStageInputException>(() => invalidCoordinator.RunAsync(Host(), Project("target"),
            new("target", "main"), "product", AgentRole.Classifier, TestContext.Current.CancellationToken));
        Assert.Single(invalidRunner.Requests);

        var staleReads = 0;
        var staleProvisionCount = 0;
        var stalePrepareCount = 0;
        var staleRunner = new RecordingProcessRunner((_, _) =>
        {
            var labels = staleReads++ == 0 ? PlanLabels : ChallengeLabels;
            return Task.FromResult(new ProcessResult(0, Inventory("product", "feature", "open", labels), ""));
        });
        var staleCoordinator = new AgentStageCoordinator(staleRunner,
            provision: (_, _, _, _, _, _) => { staleProvisionCount++; return Task.FromResult(new ProvisionedWorktree("worktree", new string('a', 40))); },
            prepare: (_, _, _, _, _, _) => { stalePrepareCount++; return Task.FromResult(Invocation()); });
        await Assert.ThrowsAsync<AgentStageInputException>(() => staleCoordinator.RunAsync(Host(), Project("target"),
            new("target", "main"), "product", AgentRole.Planner, TestContext.Current.CancellationToken));
        Assert.Equal((2, 1, 0), (staleReads, staleProvisionCount, stalePrepareCount));
    }

    [Fact]
    public async Task ExpectedWorkflowStateIsCheckedBeforeProvisioning()
    {
        var provisionCount = 0;
        var prepareCount = 0;
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(
            new ProcessResult(0, Inventory("product", "feature", "open", PlanLabels, repairCycles: 1), "")));
        var coordinator = new AgentStageCoordinator(runner,
            provision: (_, _, _, _, _, _) =>
            {
                provisionCount++;
                return Task.FromResult(new ProvisionedWorktree("C:\\worktree", new string('a', 40)));
            },
            prepare: (_, _, _, _, _, _) =>
            {
                prepareCount++;
                return Task.FromResult(Invocation());
            });
        var expectedState = new BeadWorkflowState("product", "feature", "open", "stage:plan",
            false, false, false, 0);

        await Assert.ThrowsAsync<AgentStageInputException>(() => coordinator.OpenSessionAsync(Host(), Project("target"),
            new("target", "main"), "product", AgentRole.Planner, expectedAssignee: null,
            TestContext.Current.CancellationToken, expectedState));

        Assert.Equal((0, 0), (provisionCount, prepareCount));
    }

    private static PreparedAgentInvocation Invocation() => new("claude", [], "C:\\worktree", "prompt",
        new Dictionary<string, string>(), TimeSpan.FromSeconds(1), "20261024T0000000000000Z-planner", "C:\\tmp", "C:\\logs\\planner.log");

    private static string Inventory(string id, string type, string status, string[] labels, int repairCycles = 0) => JsonSerializer.Serialize(new[]
    {
        new { id, status, issue_type = type, priority = 2, created_at = "2026-10-24T00:00:00Z", labels,
            metadata = new { repair_cycles = repairCycles } }
    });

    private static HostPaths Host() => new("C:\\nightweaver");

    private static ProjectConfig Project(string target)
    {
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Claude, null, "low", 1));
        return new(ProjectSlug.Parse("demo"), "config.json", new(target, "main"), new(1, 1, 1), new(3, 2),
            new(AgentCli.Claude, null, "low", 1), new RoleSettings(roles));
    }
}
