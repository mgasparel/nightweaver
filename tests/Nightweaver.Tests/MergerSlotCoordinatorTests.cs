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
    public async Task SemanticRejectionPreservesClaimAndIsNotReportedAsSuccessfulHandoff()
    {
        var runner = new MergerSlotProcessRunner();
        var slot = CreateSlot(runner, (_, _, _, beadId, role, _, _, _, _) =>
        {
            runner.Bead.Labels.Remove("stage:merge");
            runner.Bead.Labels.Add("stage:implement");
            runner.Bead.RepairCycles = 1;
            var state = runner.ReadState();
            return Task.FromResult(Success(beadId, role, state));
        });

        var result = await slot.RunAsync(CreateHost(), CreateProject(), CreateRepository(), "candidate",
            TestContext.Current.CancellationToken);

        Assert.Equal(MergerSlotDisposition.StoppedAtUnsupportedOutcome, result.Disposition);
        Assert.False(result.MergeCompleted);
        Assert.False(result.ClaimHandoffCompleted);
        Assert.Equal("in_progress", runner.Bead.Status);
        Assert.Equal("nightweaver:merger-slot-tests", runner.Bead.Assignee);
        Assert.Equal("stage:implement", result.FinalState!.Stage);
        Assert.Equal(1, result.FinalState.RepairCycles);
        Assert.Single(runner.ClaimWrites("candidate"));
    }

    private static MergerSlotCoordinator CreateSlot(MergerSlotProcessRunner runner, MergerRoleRunner roleRunner) =>
        new(runner, roleRunner);

    private static HostPaths CreateHost() => new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-slot-host")));
    private static ValidatedTargetRepository CreateRepository() =>
        new(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-slot-target")), "main");

    private static ProjectConfig CreateProject()
    {
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        var target = CreateRepository();
        return new ProjectConfig(ProjectSlug.Parse("merger-slot-tests"), "merger-slot-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(1, 3, 1), new LimitsConfig(3, 0), new AgentSettings(AgentCli.Codex, null, "low", 1),
            new RoleSettings(roles));
    }

    private static OperationalRetryOutcome Success(string beadId, AgentRole role, BeadWorkflowState state)
    {
        var result = new AgentStageResult(role, beadId, "fixture-invocation", string.Empty, null, [], state);
        return new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, result, 1, null, null, null);
    }

    private sealed class MergerSlotProcessRunner(bool foreignClaimOnAcquire = false, bool removeReadinessAfterClaim = false)
        : IProcessRunner
    {
        private readonly List<ProcessRequest> requests = [];
        internal FakeBead Bead { get; } = new();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            var arguments = request.Arguments;
            if (request.FileName != "bd") throw new InvalidOperationException($"Unexpected command: {request.FileName}");
            if (arguments.Contains("update", StringComparer.Ordinal)) return Task.FromResult(Update(arguments));
            if (arguments.Contains("ready", StringComparer.Ordinal) || arguments.Contains("--ready", StringComparer.Ordinal))
                return Task.FromResult(Readiness(arguments));
            if (arguments.Contains("list", StringComparer.Ordinal)) return Task.FromResult(Json([ToRow()]));
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
                return new ProcessResult(0, "{}", string.Empty);
            }
            return new ProcessResult(1, string.Empty, "guarded claim state changed");
        }

        private ProcessResult Readiness(IReadOnlyList<string> arguments)
        {
            var isRecoveryRead = arguments.Contains("--ready", StringComparer.Ordinal);
            var ready = Bead.Ready && (isRecoveryRead
                ? Bead.Status == "in_progress" && Bead.Assignee == "nightweaver:merger-slot-tests"
                : Bead.Status == "open" && Bead.Assignee is null);
            return TaskResult(ready);

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
            metadata = new { repair_cycles = Bead.RepairCycles }
        };

        private static ProcessResult Json(IEnumerable<object> rows) =>
            new(0, JsonSerializer.Serialize(rows), string.Empty);

        private static string? Value(IReadOnlyList<string> arguments, string option)
        {
            var index = arguments.ToList().IndexOf(option);
            return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
        }
    }

    private sealed class FakeBead
    {
        internal string Id { get; } = "candidate";
        internal string Status { get; set; } = "open";
        internal string? Assignee { get; set; }
        internal List<string> Labels { get; } = ["stage:merge"];
        internal int RepairCycles { get; set; }
        internal bool Ready { get; set; } = true;
    }
}
