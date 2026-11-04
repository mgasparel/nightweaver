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
        var diagnosticWriteIntercepted = false;
        var runner = new RecordingProcessRunner((request, cancellationToken) =>
        {
            if (request.FileName == "git" && request.Arguments.SequenceEqual(["worktree", "remove", "--force", worktree.Path]))
                return Task.FromResult(new ProcessResult(1, string.Empty, "injected exact worktree removal failure"));
            if (request.FileName == "bd" && request.Arguments.Contains("comments", StringComparer.Ordinal)
                && request.Arguments.Contains("add", StringComparer.Ordinal)
                && request.Arguments.Any(argument => argument.Contains("nightweaver-finalization-v1 ", StringComparison.Ordinal)))
            {
                diagnosticWriteIntercepted = true;
                return Task.FromResult(new ProcessResult(1, string.Empty, "injected diagnostic comment write failure"));
            }
            return systemRunner.RunAsync(request, cancellationToken);
        });
        var session = Session(target, project, repository, beadId, worktree);
        var logPath = Path.Combine(target.Host.LogsRoot(project.Name), beadId, "invocation.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "retain invocation evidence");
        var finalizer = new MergerCompletionFinalizer(runner);

        var result = await finalizer.FinalizeHousekeepingAsync(session, Evidence(worktree.HeadSha),
            BeadWorkflowState.Parse(target.ReadBead(beadId)), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(diagnosticWriteIntercepted, string.Join(" | ", runner.Requests.Where(request => request.FileName == "bd"
            && request.Arguments.Contains("comments", StringComparer.Ordinal)).Select(request => string.Join(' ', request.Arguments))));
        Assert.False(result.Receipt!.CleanupSucceeded);
        Assert.True(result.Receipt.ProductCompletionSucceeded);
        Assert.Contains("Required housekeeping diagnostic could not be persisted", result.Receipt.DiagnosticPersistenceDiagnostic, StringComparison.Ordinal);
        Assert.Contains("Required housekeeping diagnostic could not be persisted", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("injected exact worktree removal failure", result.Receipt.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.Equal("closed", target.ReadBead(productId).Status);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.True(File.Exists(logPath));
        Assert.DoesNotContain("nightweaver-finalization-v1", target.ReadComments(beadId), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "", "injected Product backend failure", "bd update exited 1")]
    [InlineData(13, "", "injected guard conflict", "guard refused the update (exit 13)")]
    [InlineData(0, "not-json", "", "malformed success response (exit 0)")]
    public async Task UnconfirmedProductPublicationIsReportedWhileCleanupSucceeds(int exitCode, string output,
        string error, string expectedDiagnostic)
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires native Windows CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Cleanup despite Product publication failure", "task", "stage:merge");
        var productId = target.CreateBead("Eligible Product with failed publication", "feature", "planned");
        target.RunTarget("bd", ["dep", "add", beadId, productId, "--type", "discovered-from"]);
        target.RunTarget("bd", ["update", beadId, "--status", "closed", "--assignee", ""]);
        var project = target.CreateProject(AgentCli.Codex);
        var repository = Repository(target);
        var systemRunner = new SystemProcessRunner();
        var worktree = await new WorktreeProvisioner(systemRunner).ProvisionAsync(target.Host, project.Name,
            repository, beadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var runner = new RecordingProcessRunner((request, cancellationToken) =>
        {
            if (request.FileName == "bd" && request.Arguments.Contains("update", StringComparer.Ordinal)
                && request.Arguments.Contains(productId, StringComparer.Ordinal)
                && request.Arguments.Contains("--if-status", StringComparer.Ordinal)
                && request.Arguments.Contains("--if-assignee", StringComparer.Ordinal)
                && request.Arguments.Contains("closed", StringComparer.Ordinal))
                return Task.FromResult(new ProcessResult(exitCode, output, error));
            return systemRunner.RunAsync(request, cancellationToken);
        });
        var session = Session(target, project, repository, beadId, worktree);
        var finalizer = new MergerCompletionFinalizer(runner);

        var result = await finalizer.FinalizeHousekeepingAsync(session, Evidence(worktree.HeadSha),
            BeadWorkflowState.Parse(target.ReadBead(beadId)), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Receipt!.CleanupSucceeded);
        Assert.False(result.Receipt.ProductCompletionSucceeded);
        Assert.Contains(expectedDiagnostic, result.Receipt.ProductDiagnostic, StringComparison.Ordinal);
        Assert.Contains(expectedDiagnostic, result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal("open", target.ReadBead(productId).Status);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Empty(target.RunTarget("git", ["branch", "--list", $"nightweaver/{beadId}"]));
    }

    [Theory]
    [InlineData("needs-human")]
    [InlineData("product-decision-required")]
    public async Task ProductPauseAddedAfterInitialReadPreventsAutoClosure(string pauseLabel)
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires native Windows CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Pause appears during Product completion", "task", "stage:merge");
        var productId = target.CreateBead("Product paused after first read", "feature", "planned");
        target.RunTarget("bd", ["dep", "add", beadId, productId, "--type", "discovered-from"]);
        target.RunTarget("bd", ["update", beadId, "--status", "closed", "--assignee", ""]);
        var project = target.CreateProject(AgentCli.Codex);
        var repository = Repository(target);
        var systemRunner = new SystemProcessRunner();
        var worktree = await new WorktreeProvisioner(systemRunner).ProvisionAsync(target.Host, project.Name,
            repository, beadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var pauseAdded = false;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (!pauseAdded && request.FileName == "bd" && request.Arguments.Contains("dep", StringComparer.Ordinal)
                && request.Arguments.Contains("list", StringComparer.Ordinal)
                && request.Arguments.Contains(productId, StringComparer.Ordinal)
                && request.Arguments.Contains("up", StringComparer.Ordinal))
            {
                pauseAdded = true;
                target.RunTarget("bd", ["update", productId, "--add-label", pauseLabel]);
            }
            return await systemRunner.RunAsync(request, cancellationToken);
        });
        var session = Session(target, project, repository, beadId, worktree);
        var finalizer = new MergerCompletionFinalizer(runner);

        var result = await finalizer.FinalizeHousekeepingAsync(session, Evidence(worktree.HeadSha),
            BeadWorkflowState.Parse(target.ReadBead(beadId)), TestContext.Current.CancellationToken);

        Assert.True(pauseAdded);
        Assert.True(result.Receipt!.CleanupSucceeded);
        Assert.True(result.Receipt.ProductCompletionSucceeded);
        Assert.Equal("open", target.ReadBead(productId).Status);
        Assert.Contains(pauseLabel, target.ReadBead(productId).Labels);
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task LocalBranchMovedAfterValidationSurvivesConditionalDeletion()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires native Windows CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Conditional local branch deletion", "task", "stage:merge");
        target.RunTarget("bd", ["update", beadId, "--status", "closed", "--assignee", ""]);
        var project = target.CreateProject(AgentCli.Codex);
        var repository = Repository(target);
        var systemRunner = new SystemProcessRunner();
        var worktree = await new WorktreeProvisioner(systemRunner).ProvisionAsync(target.Host, project.Name,
            repository, beadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var branchRef = $"refs/heads/nightweaver/{beadId}";
        var movedHead = target.RunTarget("git", ["commit-tree", $"{worktree.HeadSha}^{{tree}}", "-p", worktree.HeadSha,
            "-m", "concurrent local branch update"]).Trim();
        var moved = false;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (!moved && request.FileName == "git" && request.Arguments.SequenceEqual(["update-ref", "-d", branchRef, worktree.HeadSha]))
            {
                moved = true;
                target.RunTarget("git", ["update-ref", branchRef, movedHead]);
            }
            return await systemRunner.RunAsync(request, cancellationToken);
        });
        var session = Session(target, project, repository, beadId, worktree);
        var finalizer = new MergerCompletionFinalizer(runner);

        var result = await finalizer.FinalizeHousekeepingAsync(session, Evidence(worktree.HeadSha),
            BeadWorkflowState.Parse(target.ReadBead(beadId)), TestContext.Current.CancellationToken);

        Assert.True(moved);
        Assert.False(result.Receipt!.CleanupSucceeded);
        Assert.Contains("local branch changed since validation", result.Receipt.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal(movedHead, target.RunTarget("git", ["rev-parse", branchRef]).Trim());
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
