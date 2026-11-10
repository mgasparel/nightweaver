using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerPoolIntegrationTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task NativeMergerSynchronizesChecksAndLeavesCompletionToTheCompletionOwner()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.ConfigureHostedFixtureOriginWithLocalRewrite();
        var beadId = target.CreateBead("Native Merger pool closure", "task", "stage:merge");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 3, timeoutMinutes: 5)
            with { Limits = new LimitsConfig(3, 0) };
        var repository = new ValidatedTargetRepository(target.Path, "main")
        {
            GitCommonDirectory = target.RunTarget("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"]).Trim()
        };
        var systemRunner = new SystemProcessRunner();
        var processRunner = new RecordingProcessRunner((request, cancellationToken) =>
            target.RunWithFixturePullRequestAsync(systemRunner, request, cancellationToken));
        var provisioner = new WorktreeProvisioner(processRunner);
        var worktree = await provisioner.ProvisionAsync(target.Host, project.Name, repository, beadId,
            WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var sentinel = Path.Combine(worktree.Path, ".nightweaver-ignored", "warm-build-artifact.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "retained across Merger dispatch\n", new System.Text.UTF8Encoding(false));
        var originalBranch = target.CurrentBranch(beadId);
        var originalHead = RunGit(worktree.Path, "rev-parse", "HEAD");
        var ghLog = Path.Combine(target.RootPath, "merger-gh-commands.jsonl");
        var pidFile = Path.Combine(target.RootPath, "merger-native-fixture.pid");
        var preparer = new AgentInvocationPreparer();

        async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            Assert.Equal(beadId, context.BeadId);
            Assert.Equal(AgentRole.Merger, role);
            Assert.Equal(worktree.Path, worktreePath);
            Assert.Equal(originalBranch, RunGit(worktreePath, "branch", "--show-current"));
            return await preparer.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
        }

        Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            Assert.Equal(worktree.Path, invocation.WorkingDirectory);
            return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BD_ACTOR"] = project.RecoveryActor,
                    ["NIGHTWEAVER_DEFAULT_BRANCH"] = project.TargetRepository.DefaultBranch,
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = "merger-verified-merge",
                    ["NIGHTWEAVER_FIXTURE_CI_RESULT"] = "success",
                    ["NIGHTWEAVER_FIXTURE_GH_LOG"] = ghLog,
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile
                }), cancellationToken);
        }

        var stages = new AgentStageCoordinator(processRunner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        MergerRoleRunner roleRunner = (host, config, targetRepository, requestedBead, role, actor, expectedState, token, progress) =>
            retries.RunAsync(host, config, targetRepository, requestedBead, role, actor, token, progress, expectedState);
        var slot = new MergerSlotCoordinator(processRunner, roleRunner);
        using var mergerLifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pool = new MergerPoolCoordinator(target.Host, project, repository, processRunner, runSlot: slot.RunAsync,
            slotLifetimeToken: mergerLifetime.Token);

        Task[] admissionTasks = [];
        var testSucceeded = false;
        Exception? cleanupFailure = null;
        try
        {
            var evaluation = await pool.EvaluateAsync(mergerLifetime.Token);
            var admissions = evaluation.Admitted.ToArray();
            admissionTasks = admissions.Select(admission => (Task)admission.Completion).ToArray();
            var admission = Assert.Single(admissions);
            Assert.Equal(beadId, admission.BeadId);
            var completion = await admission.Completion.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            var resultLog = completion.Outcome?.Stage?.StageResult.LogFilePath;
            var resultLogContents = resultLog is not null && File.Exists(resultLog) ? File.ReadAllText(resultLog) : "(no stage log)";

            Assert.Equal(MergerSlotDisposition.Merged, completion.Outcome?.Disposition);
            Assert.True(completion.MergeCompleted,
                $"outcome={completion.Outcome?.Disposition}; diagnostic={completion.Diagnostic}; exception={completion.Exception}; "
                + $"retryFailure={completion.Outcome?.Stage?.StageResult.Failure?.Reason}; "
                + $"escalation={completion.Outcome?.Stage?.Escalation?.Failures.Count}; log={resultLogContents}");
            Assert.True(completion.LocalCapacityReleased);
            Assert.Empty(pool.ActiveReservations);
            var finalBead = target.ReadBead(beadId);
            var finalState = BeadWorkflowState.Parse(finalBead);
            Assert.Equal("closed", finalBead.Status);
            Assert.Null(finalBead.Assignee);
            Assert.Equal("stage:merge", finalState.Stage);
            Assert.Equal(0, finalState.RepairCycles);
            Assert.False(Directory.Exists(worktree.Path), completion.Diagnostic ?? "The owned completed worktree was not removed.");
            Assert.Empty(target.RunTarget("git", ["branch", "--list", originalBranch]));
            Assert.False(Directory.Exists(Path.Combine(target.Host.TmpRoot(project.Name), beadId)));
            Assert.True(resultLog is not null && File.Exists(resultLog), "The invocation log was removed with the worktree.");
            var remoteMain = target.RunTarget("git", ["ls-remote", "origin", "refs/heads/main"])
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).First();
            Assert.NotEqual(originalHead, remoteMain);
            Assert.Empty(target.RunTarget("git", ["ls-remote", "origin", $"refs/heads/nightweaver/{beadId}"]));

            using (var comments = JsonDocument.Parse(target.ReadComments(beadId)))
                Assert.Contains(StringValues(comments.RootElement), value => value.Contains(
                    $"nightweaver-merge-v1 pull_request={AgentStageTargetFixture.FixturePullRequestUrl} checked_head={remoteMain} required_checks=passed merge_method=squash merge_commit={remoteMain}",
                    StringComparison.Ordinal));

            var ghCommands = File.ReadAllLines(ghLog).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            var checksIndex = Array.FindIndex(ghCommands, command => command.Length >= 2 && command[0] == "pr" && command[1] == "checks");
            var mergeIndex = Array.FindIndex(ghCommands, command => command.Length >= 2 && command[0] == "pr" && command[1] == "merge");
            Assert.True(checksIndex >= 0, "The native fixture did not wait for the pull request checks.");
            Assert.True(mergeIndex > checksIndex, "The native fixture attempted to merge before checks passed.");
            Assert.Contains("--watch", ghCommands[checksIndex]);
            Assert.Contains("--squash", ghCommands[mergeIndex]);
            Assert.Contains("--delete-branch", ghCommands[mergeIndex]);
            Assert.Contains(processRunner.Requests, request => request.FileName == "git"
                && request.Arguments.SequenceEqual(["config", "--get", "remote.origin.url"], StringComparer.Ordinal)
                && request.WorkingDirectory == target.Path);
            var verifierViews = processRunner.Requests.Where(request => request.FileName == "gh"
                && request.Arguments.Count >= 2 && request.Arguments[0] == "pr" && request.Arguments[1] == "view"
                && request.Arguments.Contains("--repo", StringComparer.Ordinal)).ToArray();
            Assert.Equal(2, verifierViews.Length);
            Assert.All(verifierViews, request => Assert.Equal("github.com/nightweaver-fixture/target",
                request.Arguments[request.Arguments.ToList().IndexOf("--repo") + 1]));
            Assert.Contains(processRunner.Requests, request => request.FileName == "gh"
                && request.Arguments.Count >= 2 && request.Arguments[0] == "pr" && request.Arguments[1] == "checks"
                && request.Arguments.Contains("--required", StringComparer.Ordinal)
                && request.Arguments.Contains("github.com/nightweaver-fixture/target", StringComparer.Ordinal));
            Assert.True(File.Exists(pidFile));
            AssertFixtureExited(pidFile);
            var claimWrites = AgentStageTargetFixture.ClaimWrites(processRunner.Requests, beadId);
            Assert.Equal(2, claimWrites.Length);
            Assert.Equal("open", AgentStageTargetFixture.ClaimWriteStatus(claimWrites[0]));
            Assert.Equal("in_progress", AgentStageTargetFixture.ClaimWriteStatus(claimWrites[1]));
            Assert.Contains("closed", claimWrites[1].Arguments);
            testSucceeded = true;
        }
        finally
        {
            if (!testSucceeded)
                target.PreserveForFailure();

            try
            {
                mergerLifetime.Cancel();
            }
            catch (Exception cancellationException)
            {
                cleanupFailure = cancellationException;
            }
            if (admissionTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(admissionTasks).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    cleanupFailure ??= cleanupException;
                }
            }
            if (cleanupFailure is not null)
            {
                target.PreserveForFailure();
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Native Merger admission cleanup failed within its bounded wait; fixture preserved at {target.RootPath}; "
                    + $"taskStates=[{string.Join(",", admissionTasks.Select(task => task.Status))}]; "
                    + $"cleanup={cleanupFailure.GetType().Name}: {cleanupFailure.Message}");
            }
        }
        if (testSucceeded && cleanupFailure is not null)
            throw new InvalidOperationException("Native Merger admission cleanup failed after the test body completed.", cleanupFailure);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(workingDirectory, "missing-global-config");
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr}{stdout}");
        return stdout.Trim();
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        Assert.True(File.Exists(path), $"Timed out waiting for native fixture handshake: {path}");
    }

    private static void AssertFixtureExited(string pidFile)
    {
        Assert.True(File.Exists(pidFile), $"The native fixture did not create its process handshake: {pidFile}");
        var identity = File.ReadAllText(pidFile).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, identity.Length);
        var pid = int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture);
        var startTime = long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            var actualStartTime = process.StartTime.ToUniversalTime().Ticks;
            if (actualStartTime != startTime) return;
            Assert.True(process.WaitForExit(5000), $"Fixture process {pid} started at {new DateTime(startTime, DateTimeKind.Utc):O} survived executor completion.");
        }
        catch (ArgumentException)
        {
            // The fixture process exited before this check.
        }
    }
}
