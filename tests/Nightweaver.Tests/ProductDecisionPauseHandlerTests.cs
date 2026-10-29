using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class ProductDecisionPauseHandlerTests
{
    [Theory]
    [InlineData("Implementer", "stage:implement")]
    [InlineData("Implementer", "")]
    [InlineData("Reviewer", "stage:review")]
    [InlineData("Qa", "stage:qa")]
    public async Task SuccessfulPauseRemovesDeliveryWorktreeBranchAndTmpAndReleasesClaim(
        string roleName, string stage)
    {
        var role = Enum.Parse<AgentRole>(roleName);
        var labels = new List<string> { "product-decision-required" };
        if (stage.Length > 0) labels.Add(stage);
        using var target = new FakePauseTarget("task", labels);
        var handler = target.Handler();

        var request = target.Request(role, stage.Length > 0 ? stage : null);
        var result = await handler.PauseAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.CleanupCompleted);
        Assert.True(result.ClaimReleased);
        Assert.Empty(result.Failures);
        Assert.Contains("product-decision-required", target.Labels);
        if (stage.Length > 0) Assert.Contains(stage, target.Labels);
        Assert.Equal("open", target.Status);
        Assert.Null(target.Assignee);
        Assert.False(Directory.Exists(target.WorktreePath));
        Assert.False(Directory.Exists(target.TemporaryPath));
        Assert.Equal("settled log stays", File.ReadAllText(target.LogPath));

        var gitArgs = target.Runner.Requests.Where(r => r.FileName == "git").Select(r => r.Arguments).ToArray();
        Assert.Contains(gitArgs, args => args.Contains("worktree") && args.Contains("remove"));
        Assert.Contains(gitArgs, args => args.Contains("branch") && args.Contains("-D"));
        Assert.Contains(gitArgs, args => args.Contains("worktree") && args.Contains("prune"));

        var bdWrites = target.Runner.Requests.Where(IsWrite).ToArray();
        Assert.Contains(bdWrites, w => w.Arguments.Contains("--status") && w.Arguments.Contains("open") && w.Arguments.Contains("--assignee") && w.Arguments.Contains(""));
    }

    [Fact]
    public async Task PreflightRefusalLeavesResourcesIntactAndReleasesConfirmedClaim()
    {
        using var target = new FakePauseTarget("task", ["stage:review", "product-decision-required"])
        {
            MalformedWorktreeOutput = true
        };

        var handler = target.Handler();
        var request = target.Request(AgentRole.Reviewer, "stage:review");
        var result = await handler.PauseAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.CleanupCompleted);
        Assert.True(result.ClaimReleased);
        Assert.NotEmpty(result.Failures);
        Assert.Contains("refused", result.Failures[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.Equal("open", target.Status);
        Assert.Null(target.Assignee);
    }

    [Fact]
    public async Task OwnershipChangeBeforeCleanupStopsDestructiveWorkAndPreventsReleasingForeignClaim()
    {
        using var target = new FakePauseTarget("task", ["stage:implement", "product-decision-required"])
        {
            ChangeClaimBeforeCleanup = true
        };

        var handler = target.Handler();
        var request = target.Request(AgentRole.Implementer, "stage:implement");
        var result = await handler.PauseAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.CleanupCompleted);
        Assert.False(result.ClaimReleased);
        Assert.Equal("someone-else", target.Assignee);
        Assert.True(Directory.Exists(target.WorktreePath));
        Assert.True(Directory.Exists(target.TemporaryPath));
        Assert.DoesNotContain(target.Runner.Requests, r => r.FileName == "git"
            && r.Arguments.Any(arg => arg is "remove" or "prune" or "-D"));
    }

    [Fact]
    public async Task CleanupFailureWritesSupplementalCommentWhenClaimOwned()
    {
        using var target = new FakePauseTarget("task", ["stage:review", "product-decision-required"])
        {
            FailBranchDelete = true
        };

        var handler = target.Handler();
        var request = target.Request(AgentRole.Reviewer, "stage:review");
        var result = await handler.PauseAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.CleanupCompleted);
        Assert.True(result.ClaimReleased);
        Assert.Equal("open", target.Status);
        Assert.Null(target.Assignee);
        Assert.Single(target.Comments);
        using var doc = JsonDocument.Parse(target.Comments[0]);
        Assert.Equal("nightweaver-product-decision-cleanup-failure-v1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(target.BeadId, doc.RootElement.GetProperty("bead_id").GetString());
    }

    [Fact]
    public async Task FailedClaimReleaseReportsIncompleteWithoutUndoingCleanup()
    {
        using var target = new FakePauseTarget("task", ["stage:qa", "product-decision-required"])
        {
            FailRelease = true
        };

        var handler = target.Handler();
        var request = target.Request(AgentRole.Qa, "stage:qa");
        var result = await handler.PauseAsync(target.Paths, target.Project, target.Repository, request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.CleanupCompleted);
        Assert.False(result.ClaimReleased);
        Assert.Equal("in_progress", target.Status);
        Assert.False(Directory.Exists(target.WorktreePath));
        Assert.False(Directory.Exists(target.TemporaryPath));
    }

    [Fact]
    public async Task CancellationDuringCleanupStopsCleanupAndUsesBoundedGuardedRelease()
    {
        using var target = new FakePauseTarget("task", ["stage:implement", "product-decision-required"]);
        using var cancellation = new CancellationTokenSource();
        target.CancelDuringCleanup = cancellation;

        var handler = target.Handler();
        var request = target.Request(AgentRole.Implementer, "stage:implement");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.PauseAsync(target.Paths, target.Project, target.Repository, request, cancellation.Token));

        var progress = Assert.IsType<ProductDecisionPauseResult>(error.Data[nameof(ProductDecisionPauseResult)]);
        Assert.False(progress.CleanupCompleted);
        Assert.True(progress.ClaimReleased);
        Assert.Equal("open", target.Status);
        Assert.Null(target.Assignee);
    }

    private static bool IsWrite(ProcessRequest request) => request.FileName == "bd"
        && request.Arguments.Count > 3 && request.Arguments[0] == "--sandbox";

    private sealed class FakePauseTarget : IDisposable
    {
        private readonly TempDirectory temp = new();
        private bool registered = true;
        private bool localBranchPresent = true;

        internal FakePauseTarget(string issueType, IReadOnlyList<string> labels,
            string? assignee = "nightweaver:demo", string status = "in_progress")
        {
            RepositoryRoot = temp.CreateDirectory("target repo");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".git"));
            var beads = Directory.CreateDirectory(Path.Combine(RepositoryRoot, ".beads")).FullName;
            File.WriteAllText(Path.Combine(beads, "metadata.json"), "{\"dolt_mode\":\"embedded\"}");
            Directory.CreateDirectory(Path.Combine(beads, "embeddeddolt"));
            File.WriteAllText(Path.Combine(RepositoryRoot, ".beads.gate.lock"), "");
            File.WriteAllText(Path.Combine(beads, "embeddeddolt.gate.lock"), "");
            Paths = new HostPaths(temp.CreateDirectory("host"));
            Project = new ProjectSlug("demo");
            BeadId = "fixture-bead";
            IssueType = issueType;
            Labels = labels.ToList();
            Assignee = assignee;
            Status = status;
            WorktreePath = Path.Combine(Paths.WorktreesRoot(Project), BeadId);
            TemporaryPath = Path.Combine(Paths.TmpRoot(Project), BeadId);
            LogDirectory = Path.Combine(Paths.LogsRoot(Project), BeadId);
            Directory.CreateDirectory(WorktreePath);
            Directory.CreateDirectory(Path.Combine(TemporaryPath, "invocation"));
            LogPath = Path.Combine(LogDirectory, "inv.log");
            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(LogPath, "settled log stays");
            File.WriteAllText(Path.Combine(TemporaryPath, "invocation", "tmp.txt"), "tmp payload");
            Repository = new ValidatedTargetRepository(RepositoryRoot, "main");
            Runner = new RecordingProcessRunner(RunRequestAsync);
        }

        internal string RepositoryRoot { get; }
        internal string BeadId { get; }
        internal string IssueType { get; }
        internal List<string> Labels { get; }
        internal string Status { get; private set; }
        internal string? Assignee { get; private set; }
        internal string WorktreePath { get; }
        internal string TemporaryPath { get; }
        internal string LogDirectory { get; }
        internal string LogPath { get; }
        internal HostPaths Paths { get; }
        internal ProjectSlug Project { get; }
        internal ValidatedTargetRepository Repository { get; }
        internal RecordingProcessRunner Runner { get; }
        internal List<string> Comments { get; } = [];
        internal bool FailBranchDelete { get; set; }
        internal bool FailRelease { get; set; }
        internal bool ChangeClaimBeforeCleanup { get; set; }
        internal bool MalformedWorktreeOutput { get; set; }
        internal CancellationTokenSource? CancelDuringCleanup { get; set; }

        internal ProductDecisionPauseRequest Request(AgentRole role, string? stage, int repairCycles = 0)
        {
            var verifiedState = new BeadWorkflowState(BeadId, IssueType, Status, stage, false, true, false, repairCycles);
            var result = new AgentStageResult(role, BeadId, "inv-id", LogPath, null, [], verifiedState);
            var lastStage = new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, result, 1, null, null, null);
            return new ProductDecisionPauseRequest(BeadId, Assignee ?? "nightweaver:demo", role, verifiedState, lastStage);
        }

        internal ProductDecisionPauseHandler Handler() => new(Runner);

        private Task<ProcessResult> RunRequestAsync(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.FileName == "git") return Task.FromResult(RunGit(request));
            if (request.FileName == "bd") return Task.FromResult(RunBd(request));
            throw new InvalidOperationException($"Unexpected command: {request.FileName}");
        }

        private ProcessResult RunGit(ProcessRequest request)
        {
            var args = request.Arguments;
            if (args.SequenceEqual(["worktree", "list", "--porcelain", "-z"]))
            {
                if (MalformedWorktreeOutput) return new ProcessResult(0, "not porcelain\n", "");
                var porcelain = $"worktree {RepositoryRoot}\0HEAD {new string('a', 40)}\0branch refs/heads/main\0\0";
                if (registered)
                    porcelain += $"worktree {WorktreePath}\0HEAD {new string('b', 40)}\0branch refs/heads/nightweaver/{BeadId}\0\0";
                return new ProcessResult(0, porcelain, "");
            }
            if (args.SequenceEqual(["show-ref", "--verify", "--quiet", $"refs/heads/nightweaver/{BeadId}"]))
                return new ProcessResult(localBranchPresent ? 0 : 1, "", "");
            if (args.Count >= 4 && args[0] == "worktree" && args[1] == "remove" && args[2] == "--force")
            {
                if (CancelDuringCleanup is not null)
                {
                    CancelDuringCleanup.Cancel();
                    throw new OperationCanceledException(CancelDuringCleanup.Token);
                }
                registered = false;
                if (Directory.Exists(WorktreePath)) Directory.Delete(WorktreePath, true);
                return new ProcessResult(0, "", "");
            }
            if (args.Count >= 3 && args[0] == "branch" && args[1] == "-D")
            {
                if (FailBranchDelete) return new ProcessResult(1, "", "failed to delete branch");
                localBranchPresent = false;
                return new ProcessResult(0, "", "");
            }
            if (args.SequenceEqual(["worktree", "prune"])) return new ProcessResult(0, "", "");
            throw new InvalidOperationException($"Unexpected git request: {string.Join(' ', args)}");
        }

        private ProcessResult RunBd(ProcessRequest request)
        {
            var args = request.Arguments;
            if (args.Contains("--readonly") && args.Contains("--all"))
            {
                if (ChangeClaimBeforeCleanup)
                {
                    Assignee = "someone-else";
                }
                var row = new
                {
                    id = BeadId,
                    status = Status,
                    issue_type = IssueType,
                    priority = 2,
                    labels = Labels,
                    assignee = Assignee,
                    created_at = "2026-10-24T14:22:33Z",
                    metadata = new { repair_cycles = 0 }
                };
                return new ProcessResult(0, JsonSerializer.Serialize(new[] { row }), "");
            }
            if (args.Contains("comments") && args.Contains("add"))
            {
                var text = args[^1];
                Comments.Add(text);
                return new ProcessResult(0, "{}", "");
            }
            if (args.Contains("--status") && args.Contains("open") && args.Contains("--assignee"))
            {
                if (FailRelease) return new ProcessResult(13, "", "release guard failed");
                Status = "open";
                Assignee = null;
                return new ProcessResult(0, "{}", "");
            }
            throw new InvalidOperationException($"Unexpected bd request: {string.Join(' ', args)}");
        }

        public void Dispose() => temp.Dispose();
    }
}
