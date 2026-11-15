using Nightweaver.Application;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Configuration;

namespace Nightweaver.Tests;

public sealed class MergerCompletionFinalizerTests : AgentStageIntegrationTestSupport
{
    private static readonly string[] ClosedMergeLabels = ["stage:merge"];

    [Theory]
    [InlineData("open")]
    [InlineData("paused")]
    [InlineData("assigned")]
    [InlineData("missing")]
    [InlineData("foreign-id")]
    public async Task ExactTaskReadFailsClosedBeforeBranchDeletion(string change)
    {
        using var temp = new TempDirectory();
        var repository = new ValidatedTargetRepository(temp.CreateDirectory("target"), "main");
        var host = new HostPaths(temp.CreateDirectory("host"));
        var slug = ProjectSlug.Parse("demo");
        var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
        var project = new ProjectConfig(slug, "fixture.json", new(repository.Path, "main"),
            new(1, 1, 1), new(3, 1), settings,
            new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => settings)));
        const string id = "task-1";
        var head = new string('a', 40);
        var state = new BeadWorkflowState(id, "task", "closed", "stage:merge", false, false, false, 0);
        var session = new AgentStageSession(host, project, repository, id, AgentRole.Merger,
            WorktreePurpose.Delivery, new(Path.Combine(host.WorktreesRoot(slug), id), head), state, null,
            new AgentDispatchContext(id, "task", "stage:merge", 0), PreserveInheritedChanges: true);
        var runner = new RecordingProcessRunner((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(repository.Path, request.WorkingDirectory);
            if (request.FileName == "git")
            {
                var args = request.Arguments;
                if (args.SequenceEqual(["worktree", "list", "--porcelain", "-z"]))
                    return Task.FromResult(new ProcessResult(0,
                        $"worktree {repository.Path}\0HEAD {head}\0branch refs/heads/main\0\0", ""));
                if (args.SequenceEqual(["show-ref", "--verify", "--quiet", "refs/heads/nightweaver/task-1"]))
                    return Task.FromResult(new ProcessResult(0, "", ""));
                if (args.SequenceEqual(["rev-parse", "refs/heads/nightweaver/task-1"]))
                    return Task.FromResult(new ProcessResult(0, head, ""));
                throw new InvalidOperationException("Unexpected Git command: " + string.Join(' ', args));
            }
            Assert.Equal("bd", request.FileName);
            Assert.Equal(new[] { "--readonly", "--sandbox", "show", id, "--json" }, request.Arguments);
            object[] rows = change == "missing" ? [] : [new
            {
                id = change == "foreign-id" ? "other-task" : id,
                issue_type = "task", status = change == "open" ? "open" : "closed", priority = 2,
                created_at = "2026-11-15T00:00:00Z",
                assignee = change == "assigned" ? "other-owner" : (string?)null,
                labels = change == "paused" ? ["stage:merge", "needs-human"] : ClosedMergeLabels
            }];
            return Task.FromResult(new ProcessResult(0, System.Text.Json.JsonSerializer.Serialize(rows), ""));
        });
        await Assert.ThrowsAsync<ToolExecutionException>(() => new MergerCompletionFinalizer(runner)
            .CleanupBranchesAsync(session, Evidence(head), TestContext.Current.CancellationToken));
        Assert.Single(runner.Requests, request => request.FileName == "bd");
        Assert.DoesNotContain(runner.Requests, request => request.Arguments[0] is "update-ref" or "push");
    }

    [Fact]
    public async Task QaRetainedArtifactsRecoverAfterLostClosureAndLockedTmpRetryAcrossRestart()
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
        var removalCalls = 0;
        using var lockedTmp = new FileStream(Path.Combine(qaTmp, "evidence.txt"), FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var remotePresent = true;
        var remoteDeleted = false;
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            // Inject an exact origin ref without adding a second native remote fixture.
            if (request.FileName == "git" && request.Arguments[0] == "ls-remote")
                return new ProcessResult(0, remotePresent ? $"{worktree.HeadSha}\trefs/heads/nightweaver/{beadId}\n" : "", "");
            if (request.FileName == "git" && request.Arguments[0] == "push")
            {
                Assert.Equal(new[] { "push", $"--force-with-lease=refs/heads/nightweaver/{beadId}:{worktree.HeadSha}",
                    "origin", $":refs/heads/nightweaver/{beadId}" }, request.Arguments);
                remotePresent = false;
                remoteDeleted = true;
                return new ProcessResult(0, "", "");
            }
            var result = await target.RunWithFixturePullRequestAsync(systemRunner, request, token);
            if (request.FileName == "git" && request.Arguments.Take(2).SequenceEqual(["worktree", "remove"]))
                removalCalls++;
            return result;
        });
        var finalizer = new MergerCompletionFinalizer(runner);

        var completion = await finalizer.CompleteAsync(session, invocation, project.RecoveryActor, evidence,
            TestContext.Current.CancellationToken);
        Assert.True(completion.Succeeded, completion.Diagnostic);
        Assert.False(completion.Receipt!.CleanupSucceeded);
        Assert.True(remoteDeleted);
        Assert.False(target.RunTarget("git", ["branch", "--list", $"nightweaver/{beadId}"]).Length > 0);
        var store = new CompletedEnvironmentStore(target.Host, project.Name, repository);
        var pending = Assert.Single(store.Read().Obligations);
        Assert.Equal(CleanupStatus.RetryScheduled, pending.Status);
        Assert.True(pending.Worktree.ConfirmedAbsent);
        Assert.True(Directory.Exists(qaTmp));
        Assert.True(completion.Receipt.ProductCompletionSucceeded, completion.Diagnostic);
        Assert.False(target.LoseVerifiedTaskCloseResponseAfterCommitOnce);
        Assert.Single(AgentStageTargetFixture.ClaimWrites(runner.Requests, beadId));
        Assert.Equal("closed", target.ReadBead(beadId).Status);
        Assert.Equal("closed", target.ReadBead(productId).Status);
        var branchCommandsBeforeRestart = runner.Requests.Count(request => request.FileName == "git"
            && request.Arguments[0] is "update-ref" or "push");
        lockedTmp.Dispose();
        // Restart honors the persisted retry time without replaying branch or worktree removal.
        var clock = new CompletedEnvironmentWorkerTests.Clock
        { Now = pending.NextAttempt ?? DateTimeOffset.UtcNow };
        using var restarted = new CompletedEnvironmentWorker(target.Host, project, repository, runner,
            new EnvironmentReservations(), new ContinuousStopGate(), "native-restarted-owner", _ => { }, clock);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        await restarted.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CleanupStatus.Resolved, Assert.Single(store.Read().Obligations).Status);
        Assert.Equal(1, removalCalls);
        Assert.Equal(branchCommandsBeforeRestart, runner.Requests.Count(request => request.FileName == "git"
            && request.Arguments[0] is "update-ref" or "push"));
        Assert.Equal("closed", target.ReadBead(beadId).Status);
        Assert.DoesNotContain("needs-human", target.ReadBead(beadId).Labels);
        // The original completion independently deletes eligible refs even while tmp remains.

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
        using var target = new GitFixture();
        target.CommitEmpty();
        // GitFixture normally carries only a metadata marker for Beads. This
        // boundary is Git-only, so leave no local Beads backend for inspection.
        Directory.Delete(Path.Combine(target.Path, ".beads"), recursive: true);
        GitFixture.RunGit(target.Path, ["config", "user.name", "Nightweaver Git Fixture"]);
        GitFixture.RunGit(target.Path, ["config", "user.email", "fixture@example.invalid"]);
        var beadId = "ns-np6-conditional-delete";
        var branchName = $"nightweaver/{beadId}";
        var branchRef = $"refs/heads/{branchName}";
        var systemRunner = new SystemProcessRunner();
        async Task<string> GitValueAsync(params string[] arguments)
        {
            var result = await systemRunner.RunAsync(GitCommand.Create(arguments, target.Path),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            return result.StandardOutput.Trim();
        }

        var checkedHead = await GitValueAsync("rev-parse", "HEAD");
        GitFixture.RunGit(target.Path, ["branch", branchName, checkedHead]);
        var movedCommit = await systemRunner.RunAsync(GitCommand.Create(
            ["commit-tree", $"{checkedHead}^{{tree}}", "-p", checkedHead, "-m", "concurrent local branch update"], target.Path),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, movedCommit.ExitCode);
        var movedHead = movedCommit.StandardOutput.Trim();

        using var hostTemp = new TempDirectory();
        var host = new HostPaths(hostTemp.CreateDirectory("host"));
        var slug = ProjectSlug.Parse("demo");
        var defaults = new AgentSettings(AgentCli.Codex, null, "low", 1);
        var project = new ProjectConfig(slug, "fixture.json", new("target", "main"),
            new(1, 1, 1), new(3, 1), defaults,
            new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => defaults)));
        var repository = new ValidatedTargetRepository(target.Path, "main")
        {
            GitCommonDirectory = await GitValueAsync("rev-parse", "--path-format=absolute", "--git-common-dir")
        };
        var worktreePath = Path.Combine(host.WorktreesRoot(slug), beadId);
        Assert.False(Directory.Exists(worktreePath));
        var closedState = new BeadWorkflowState(beadId, "task", "closed", "stage:merge", false, false, false, 0);
        var session = new AgentStageSession(host, project, repository, beadId, AgentRole.Merger,
            WorktreePurpose.Delivery, new(worktreePath, checkedHead), closedState, null,
            new AgentDispatchContext(beadId, "task", "stage:merge", 0), PreserveInheritedChanges: true);
        var beadReads = 0;
        IReadOnlyList<string>? compareDelete = null;
        var moved = false;
        var runner = new RecordingProcessRunner(async (request, cancellationToken) =>
        {
            if (request.FileName == "bd")
            {
                Assert.Equal(new[] { "--readonly", "--sandbox", "show", beadId, "--json" }, request.Arguments);
                Assert.DoesNotContain("update", request.Arguments);
                Assert.Equal(target.Path, request.WorkingDirectory);
                beadReads++;
                var closedTask = new[]
                {
                    new
                    {
                        id = beadId,
                        status = "closed",
                        issue_type = "task",
                        priority = 2,
                        created_at = "2026-11-15T00:00:00Z",
                        assignee = (string?)null,
                        labels = ClosedMergeLabels,
                        metadata = new { repair_cycles = 0 }
                    }
                };
                return new ProcessResult(0, System.Text.Json.JsonSerializer.Serialize(closedTask), string.Empty);
            }

            if (request.FileName == "git" && request.Arguments[0] == "update-ref" && request.Arguments[1] == "-d")
            {
                compareDelete = request.Arguments.ToArray();
                Assert.Equal(new[] { "update-ref", "-d", branchRef, checkedHead }, compareDelete);
                Assert.False(moved);
                var move = await systemRunner.RunAsync(
                    GitCommand.Create(["update-ref", branchRef, movedHead], target.Path), cancellationToken);
                Assert.Equal(0, move.ExitCode);
                moved = true;
                var deletion = await systemRunner.RunAsync(request, cancellationToken);
                Assert.NotEqual(0, deletion.ExitCode);
                return deletion;
            }

            return await systemRunner.RunAsync(request, cancellationToken);
        });
        var finalizer = new MergerCompletionFinalizer(runner);

        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => finalizer.CleanupBranchesAsync(
            session, Evidence(checkedHead), TestContext.Current.CancellationToken));

        Assert.True(moved, error.Message);
        Assert.Equal(new[] { "update-ref", "-d", branchRef, checkedHead }, compareDelete);
        Assert.Contains("local branch changed since validation", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, beadReads);
        Assert.All(runner.Requests.Where(request => request.FileName == "bd"),
            request => Assert.Contains("show", request.Arguments));
        Assert.False(Directory.Exists(worktreePath));
        Assert.Equal(movedHead, await GitValueAsync("rev-parse", branchRef));
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
