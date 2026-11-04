using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Configuration;

namespace Nightweaver.Tests;

public sealed class MergerCompletionFinalizerTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task ExactCleanupCanRepeatWithoutRemovingUnrelatedWorktreeOrInvocationLog()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires native Windows CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Repeat exact Merger cleanup", "task", "stage:merge");
        target.RunTarget("bd", ["update", beadId, "--status", "closed", "--assignee", ""]);
        var project = target.CreateProject(AgentCli.Codex);
        var repository = Repository(target);
        var systemRunner = new SystemProcessRunner();
        var worktree = await new WorktreeProvisioner(systemRunner).ProvisionAsync(target.Host, project.Name,
            repository, beadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var unrelatedPath = Path.Combine(target.RootPath, "unrelated-checkout");
        target.RunTarget("git", ["worktree", "add", "-b", "unrelated-checkout", unrelatedPath, "main"]);
        var logPath = Path.Combine(target.Host.LogsRoot(project.Name), beadId, "invocation.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "retain invocation evidence");
        var session = Session(target, project, repository, beadId, worktree);
        var evidence = Evidence(worktree.HeadSha);
        var finalizer = new MergerCompletionFinalizer(systemRunner);

        await finalizer.CleanupOwnedEnvironmentAsync(session, evidence, TestContext.Current.CancellationToken);
        await finalizer.CleanupOwnedEnvironmentAsync(session, evidence, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.False(target.RunTarget("git", ["branch", "--list", $"nightweaver/{beadId}"]).Length > 0);
        Assert.True(Directory.Exists(unrelatedPath));
        Assert.Equal("unrelated-checkout", target.RunTarget("git", ["branch", "--show-current"], unrelatedPath).Trim());
        Assert.True(File.Exists(logPath));
        Assert.Equal("retain invocation evidence", File.ReadAllText(logPath));
        target.RunTarget("git", ["worktree", "remove", "--force", unrelatedPath]);
        target.RunTarget("git", ["branch", "-D", "unrelated-checkout"]);
    }

    [Fact]
    public async Task ProductCompletionProceedsWhenOwnedWorktreeCleanupFails()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires native Windows CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Product completion despite cleanup failure", "task", "stage:merge");
        var productId = target.CreateBead("Planned Product with one completed task", "feature", "planned");
        target.RunTarget("bd", ["dep", "add", beadId, productId, "--type", "discovered-from"]);
        target.RunTarget("bd", ["update", beadId, "--status", "closed", "--assignee", ""]);
        var project = target.CreateProject(AgentCli.Codex);
        var repository = Repository(target);
        var systemRunner = new SystemProcessRunner();
        var worktree = await new WorktreeProvisioner(systemRunner).ProvisionAsync(target.Host, project.Name,
            repository, beadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var runner = new RecordingProcessRunner((request, cancellationToken) =>
            request.FileName == "git" && request.Arguments.SequenceEqual(["worktree", "remove", "--force", worktree.Path])
                ? Task.FromResult(new ProcessResult(1, string.Empty, "injected exact worktree removal failure"))
                : systemRunner.RunAsync(request, cancellationToken));
        var session = Session(target, project, repository, beadId, worktree);
        var logPath = Path.Combine(target.Host.LogsRoot(project.Name), beadId, "invocation.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "retain invocation evidence");
        var finalizer = new MergerCompletionFinalizer(runner);

        var result = await finalizer.FinalizeHousekeepingAsync(session, Evidence(worktree.HeadSha),
            BeadWorkflowState.Parse(target.ReadBead(beadId)), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Receipt!.CleanupSucceeded);
        Assert.True(result.Receipt.ProductCompletionSucceeded);
        Assert.Contains("injected exact worktree removal failure", result.Receipt.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.Equal("closed", target.ReadBead(productId).Status);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.True(File.Exists(logPath));
        Assert.Contains("nightweaver-finalization-v1", target.ReadComments(beadId), StringComparison.Ordinal);
    }

    private static ValidatedTargetRepository Repository(AgentStageTargetFixture target) =>
        new(target.Path, "main")
        {
            GitCommonDirectory = target.RunTarget("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"]).Trim()
        };

    private static AgentStageSession Session(AgentStageTargetFixture target, ProjectConfig project,
        ValidatedTargetRepository repository, string beadId, ProvisionedWorktree worktree)
    {
        var state = BeadWorkflowState.Parse(target.ReadBead(beadId));
        return new AgentStageSession(target.Host, project, repository, beadId, AgentRole.Merger,
            WorktreePurpose.Delivery, worktree, state, null,
            new AgentDispatchContext(beadId, "task", "stage:merge", state.RepairCycles), PreserveInheritedChanges: true);
    }

    private static MergerVerifiedMergeEvidence Evidence(string head) => new(
        "https://example.invalid/pull/123", head, head, "squash", "comment-1");
}
