using Nightweaver.Application;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Configuration;

namespace Nightweaver.Tests;

public sealed class MergerCompletionFinalizerTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task QaRetainedArtifactsRecoverAfterLostClosureAndRemovalResponsesAcrossRestart()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires native Windows CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("QA artifacts await verified Merger cleanup", "task", "stage:qa");
        var productId = target.CreateBead("Native planned Product completion", "feature", "planned");
        target.RunTarget("bd", ["dep", "add", beadId, productId, "--type", "discovered-from"]);
        var project = target.CreateProject(AgentCli.Codex);
        target.RunTarget("bd", ["update", beadId, "--status", "in_progress", "--assignee", project.RecoveryActor]);
        var repository = Repository(target);
        var systemRunner = new SystemProcessRunner();
        var worktree = await new WorktreeProvisioner(systemRunner).ProvisionAsync(target.Host, project.Name,
            repository, beadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var ignoredDirectory = Directory.CreateDirectory(Path.Combine(worktree.Path, ".nightweaver-ignored")).FullName;
        var inheritedCache = Path.Combine(ignoredDirectory, "inherited-cache.txt");
        File.WriteAllText(inheritedCache, "warm build cache");
        var qaReport = Path.Combine(ignoredDirectory, "qa-report.xml");
        File.WriteAllText(qaReport, "passing QA with disposal pending");
        var qaTmp = Directory.CreateDirectory(Path.Combine(target.Host.TmpRoot(project.Name), beadId, "qa-invocation")).FullName;
        File.WriteAllText(Path.Combine(qaTmp, "evidence.txt"), "retained QA evidence");
        var qaState = BeadWorkflowState.Parse(target.ReadBead(beadId));
        var qa = await new AgentPostExitVerifier(systemRunner, new BeadsReader(systemRunner)).VerifyAsync(
            repository, worktree.Path, AgentRole.Qa, qaState, project.RecoveryActor, worktree.HeadSha,
            new AgentExecutionResult(AgentExecutionStatus.Exited, 0, "qa-invocation", "qa.log"),
            TestContext.Current.CancellationToken);
        Assert.True(qa.Succeeded, qa.Failure?.Reason);
        Assert.True(qa.Observations!.WorktreeClean);
        Assert.Equal(worktree.HeadSha, qa.Observations.HeadSha);
        Assert.Equal("warm build cache", File.ReadAllText(inheritedCache));
        Assert.True(File.Exists(qaReport));
        Assert.True(Directory.Exists(qaTmp));
        target.RunTarget("bd", ["update", beadId, "--remove-label", "stage:qa", "--add-label", "stage:merge"]);
        var unrelatedPath = Path.Combine(target.RootPath, "unrelated-checkout");
        target.RunTarget("git", ["worktree", "add", "-b", "unrelated-checkout", unrelatedPath, "main"]);
        var logPath = Path.Combine(target.Host.LogsRoot(project.Name), beadId, "invocation.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "retain invocation evidence");
        var session = Session(target, project, repository, beadId, worktree);
        var evidence = Evidence(worktree.HeadSha);
        var observations = new AgentStageObservations(session.InitialState, session.InitialState, project.RecoveryActor,
            project.RecoveryActor, AgentExecutionStatus.Exited, 0, $"nightweaver/{beadId}", worktree.HeadSha, string.Empty, true);
        var invocation = new AgentStageResult(AgentRole.Merger, beadId, "fixture-invocation", logPath, null, [],
            Observations: observations);
        target.LoseVerifiedTaskCloseResponseAfterCommitOnce = true;
        var lostRemoval = false;
        var removalCalls = 0;
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            var result = await target.RunWithFixturePullRequestAsync(systemRunner, request, token);
            if (request.FileName == "git" && request.Arguments.Take(2).SequenceEqual(["worktree", "remove"]))
            {
                removalCalls++;
                if (!lostRemoval && result.ExitCode == 0)
                {
                    lostRemoval = true;
                    return new ProcessResult(73, string.Empty, "fixture lost removal response after commit");
                }
            }
            return result;
        });
        var finalizer = new MergerCompletionFinalizer(runner);

        var completion = await finalizer.CompleteAsync(session, invocation, project.RecoveryActor, evidence,
            TestContext.Current.CancellationToken);
        Assert.True(completion.Succeeded, completion.Diagnostic);
        Assert.False(completion.Receipt!.CleanupSucceeded);
        Assert.Contains("lost removal response", completion.Receipt.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.True(lostRemoval);
        var store = new CompletedEnvironmentStore(target.Host, project.Name, repository);
        var pending = Assert.Single(store.Read().Obligations);
        Assert.Equal(CleanupStatus.NeedsAttention, pending.Status);
        Assert.True(pending.Worktree.ConfirmedAbsent);
        Assert.True(Directory.Exists(qaTmp));
        Assert.True(completion.Receipt.ProductCompletionSucceeded, completion.Diagnostic);
        Assert.False(target.LoseVerifiedTaskCloseResponseAfterCommitOnce);
        Assert.Single(AgentStageTargetFixture.ClaimWrites(runner.Requests, beadId));
        Assert.Equal("closed", target.ReadBead(beadId).Status);
        Assert.Equal("closed", target.ReadBead(productId).Status);
        var restarted = new StartupEnvironmentCleaner(new BeadsReader(runner), new BeadsWriter(runner), runner);
        await restarted.CleanAsync(target.Host, project.Name, repository, project.RecoveryActor,
            TestContext.Current.CancellationToken);
        Assert.Equal(CleanupStatus.Resolved, Assert.Single(store.Read().Obligations).Status);
        Assert.Equal(1, removalCalls);
        Assert.Equal("closed", target.ReadBead(beadId).Status);
        Assert.DoesNotContain("needs-human", target.ReadBead(beadId).Labels);
        // Existing exact branch cleanup remains a separate completion operation, never a startup sweep.
        await finalizer.CleanupOwnedEnvironmentAsync(session, evidence, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.False(Directory.Exists(Path.Combine(target.Host.TmpRoot(project.Name), beadId)));
        Assert.False(target.RunTarget("git", ["branch", "--list", $"nightweaver/{beadId}"]).Length > 0);
        Assert.True(Directory.Exists(unrelatedPath));
        Assert.Equal("unrelated-checkout", target.RunTarget("git", ["branch", "--show-current"], unrelatedPath).Trim());
        Assert.True(File.Exists(logPath));
        Assert.Equal("retain invocation evidence", File.ReadAllText(logPath));
        target.RunTarget("git", ["worktree", "remove", "--force", unrelatedPath]);
        target.RunTarget("git", ["branch", "-D", "unrelated-checkout"]);
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

        Assert.True(moved, result.Diagnostic);
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
