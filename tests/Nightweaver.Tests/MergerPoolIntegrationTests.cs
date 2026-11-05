using System.Diagnostics;
using System.Text.Json;
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
        var productId = target.CreateBead("Native planned Product completion", "feature", "planned");
        target.RunTarget("bd", ["dep", "add", beadId, productId, "--type", "discovered-from"]);
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 3, timeoutMinutes: 5)
            with { Limits = new LimitsConfig(3, 0) };
        var repository = new ValidatedTargetRepository(target.Path, "main")
        {
            GitCommonDirectory = target.RunTarget("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"]).Trim()
        };
        var systemRunner = new SystemProcessRunner();
        var processRunner = new RecordingProcessRunner((request, cancellationToken) =>
            target.RunWithFixturePullRequestAsync(systemRunner, request, cancellationToken));
        target.LoseVerifiedTaskCloseResponseAfterCommitOnce = true;
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
        var pool = new MergerPoolCoordinator(target.Host, project, repository, processRunner, runSlot: slot.RunAsync,
            slotLifetimeToken: TestContext.Current.CancellationToken);

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var admission = Assert.Single(evaluation.Admitted);
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
        Assert.Equal("closed", target.ReadBead(productId).Status);
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
        Assert.False(target.LoseVerifiedTaskCloseResponseAfterCommitOnce,
            "The fixture did not exercise a lost guarded-close response after the tracker committed it.");
    }

    [Fact]
    public async Task NativeLocalBuildRejectionReturnsWorkToDeliveryAndImplementerRepairsTheRetainedEnvironment()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        File.WriteAllText(Path.Combine(target.Path, "LocalCheckFixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>\n",
            new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(target.Path, "BrokenFixture.cs"),
            "public sealed class LocalCheckFixture { public int Broken => ; }\n", new System.Text.UTF8Encoding(false));
        target.RunTarget("git", ["add", "LocalCheckFixture.csproj", "BrokenFixture.cs"]);
        target.RunTarget("git", ["commit", "-m", "seed controlled local build failure"]);
        target.RunTarget("git", ["push", "origin", "main"]);
        var beadId = target.CreateBead("Native Merger rejection handoff", "task", "stage:merge", "keep:label");
        target.FixtureRequiredCheckFails = false;
        target.RunTarget("bd", ["update", beadId, "--set-metadata", "retained=fixture"]);
        var project = target.CreateProject(AgentCli.Codex, maxRepairCycles: 1, maxConcurrentDelivery: 3, timeoutMinutes: 5);
        var repository = new ValidatedTargetRepository(target.Path, "main");
        var systemRunner = new SystemProcessRunner();
        var processRunner = new RecordingProcessRunner((request, cancellationToken) =>
            target.RunWithFixturePullRequestAsync(systemRunner, request, cancellationToken));
        var provisioner = new WorktreeProvisioner(processRunner);
        var worktree = await provisioner.ProvisionAsync(target.Host, project.Name, repository, beadId,
            WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var warmSentinel = Path.Combine(worktree.Path, ".nightweaver-ignored", "warm-build-artifact.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(warmSentinel)!);
        File.WriteAllText(warmSentinel, "retain for Delivery repair\n", new System.Text.UTF8Encoding(false));
        var tmpSentinel = Path.Combine(target.Host.TmpRoot(project.Name), beadId, "prior-build", "tmp-sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(tmpSentinel)!);
        File.WriteAllText(tmpSentinel, "retain prior build evidence\n", new System.Text.UTF8Encoding(false));
        var originalBranch = target.CurrentBranch(beadId);
        var mainHead = target.RunTarget("git", ["ls-remote", "origin", "refs/heads/main"])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).First();
        var ghLog = Path.Combine(target.RootPath, "merger-rejection-gh-commands.jsonl");
        var mergerPid = Path.Combine(target.RootPath, "merger-rejection-native-fixture.pid");
        var preparer = new AgentInvocationPreparer();

        async Task<PreparedAgentInvocation> PrepareMerger(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            Assert.Equal(beadId, context.BeadId);
            Assert.Equal(AgentRole.Merger, role);
            Assert.Equal(worktree.Path, worktreePath);
            Assert.Equal(originalBranch, RunGit(worktreePath, "branch", "--show-current"));
            return await preparer.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
        }

        Task<AgentExecutionResult> ExecuteMerger(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken) =>
            AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BD_ACTOR"] = project.RecoveryActor,
                    ["NIGHTWEAVER_DEFAULT_BRANCH"] = project.TargetRepository.DefaultBranch,
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = "merger-reject-checks",
                    ["NIGHTWEAVER_FIXTURE_CHECK_SOURCE"] = "local",
                    ["NIGHTWEAVER_FIXTURE_GH_LOG"] = ghLog,
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = mergerPid
                }), cancellationToken);

        var mergerStages = new AgentStageCoordinator(processRunner, prepare: PrepareMerger, execute: ExecuteMerger);
        var mergerRetries = new OperationalRetryCoordinator(processRunner, stages: mergerStages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        MergerRoleRunner mergerRole = (host, config, targetRepository, requestedBead, role, actor, expectedState, token, progress) =>
            mergerRetries.RunAsync(host, config, targetRepository, requestedBead, role, actor, token, progress, expectedState);
        var slot = new MergerSlotCoordinator(processRunner, mergerRole);
        var pool = new MergerPoolCoordinator(target.Host, project, repository, processRunner, runSlot: slot.RunAsync,
            slotLifetimeToken: TestContext.Current.CancellationToken);

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var admission = Assert.Single(evaluation.Admitted);
        Assert.Equal(beadId, admission.BeadId);
        var completion = await admission.Completion.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(completion.LocalCapacityReleased);
        Assert.Empty(pool.ActiveReservations);
        Assert.True(completion.Outcome?.Disposition == MergerSlotDisposition.RepairHandedOff,
            $"disposition={completion.Outcome?.Disposition}; diagnostic={completion.Diagnostic}; stage={completion.Outcome?.Stage?.StageResult.OutcomeClassification?.Diagnostic}; "
            + $"stageOutcome={completion.Outcome?.Stage?.StageResult.OutcomeClassification?.Outcome}; stageFailure={completion.Outcome?.Stage?.StageResult.Failure?.Reason}; "
            + $"escalationFindings={string.Join(" | ", completion.Outcome?.Stage?.Escalation?.Failures ?? [])}");
        Assert.True(completion.Outcome?.RepairHandoffCompleted, completion.Diagnostic);
        var rejected = target.ReadBead(beadId);
        var rejectedState = BeadWorkflowState.Parse(rejected);
        Assert.Equal("open", rejected.Status);
        Assert.Null(rejected.Assignee);
        Assert.Equal("stage:implement", rejectedState.Stage);
        Assert.Equal(1, rejectedState.RepairCycles);
        Assert.Contains("keep:label", rejected.Labels);
        Assert.Equal("fixture", rejected.Payload.GetProperty("metadata").GetProperty("retained").GetString());
        Assert.Equal(originalBranch, target.CurrentBranch(beadId));
        Assert.True(File.Exists(warmSentinel));
        Assert.True(File.Exists(tmpSentinel));
        Assert.Equal(mainHead, target.RunTarget("git", ["ls-remote", "origin", "refs/heads/main"])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).First());
        Assert.NotEmpty(target.RunTarget("git", ["ls-remote", "origin", $"refs/heads/{originalBranch}"]));
        using (var comments = JsonDocument.Parse(target.ReadComments(beadId)))
        {
            var findingText = StringValues(comments.RootElement).Single(value => value.Contains(
                "nightweaver-merger-rejection-v1", StringComparison.Ordinal));
            using var finding = JsonDocument.Parse(findingText);
            Assert.Equal("local", finding.RootElement.GetProperty("check_source").GetString());
            Assert.Equal("dotnet build", finding.RootElement.GetProperty("subject").GetString());
            Assert.Equal(1, finding.RootElement.GetProperty("local_exit_code").GetInt32());
        }
        var mergerCommands = File.Exists(ghLog)
            ? File.ReadAllLines(ghLog).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray()
            : [];
        Assert.DoesNotContain(mergerCommands, command => command.Length >= 2 && command[0] == "pr" && command[1] == "checks");
        Assert.DoesNotContain(mergerCommands, command => command.Length >= 2 && command[0] == "pr" && command[1] == "merge");
        Assert.True(File.Exists(mergerPid));
        AssertFixtureExited(mergerPid);

        var resumeEvidence = Path.Combine(target.RootPath, "delivery-resume-evidence.json");
        var deliveryPidFiles = new List<string>();
        var deliveryRoles = new Dictionary<string, AgentRole>(StringComparer.Ordinal);
        async Task<PreparedAgentInvocation> PrepareDelivery(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            Assert.Equal(beadId, context.BeadId);
            Assert.Equal(worktree.Path, worktreePath);
            Assert.Equal(originalBranch, RunGit(worktreePath, "branch", "--show-current"));
            Assert.True(File.Exists(warmSentinel), "Delivery preparation lost the retained warm artifact.");
            var invocation = await preparer.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
            deliveryRoles.Add(invocation.InvocationId, role);
            return invocation;
        }

        Task<AgentExecutionResult> ExecuteDelivery(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            var role = deliveryRoles[invocation.InvocationId];
            var action = role switch
            {
                AgentRole.Implementer => "merger-resumed-implement",
                AgentRole.Reviewer => "slot-review",
                AgentRole.Qa => "slot-qa",
                _ => throw new InvalidOperationException($"Unexpected resumed Delivery role {role}.")
            };
            var pid = Path.Combine(target.RootPath, $"delivery-resume-{deliveryPidFiles.Count}.pid");
            deliveryPidFiles.Add(pid);
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = project.RecoveryActor,
                ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pid,
                ["NIGHTWEAVER_FIXTURE_EXPECTED_REPAIR_CYCLES"] = "1",
                ["NIGHTWEAVER_FIXTURE_EXPECTED_FINDING"] = "nightweaver-merger-rejection-v1",
                ["NIGHTWEAVER_FIXTURE_FIX_LOCAL_BUILD"] = "true",
                ["NIGHTWEAVER_FIXTURE_WARM_SENTINEL"] = warmSentinel,
                ["NIGHTWEAVER_FIXTURE_TMP_SENTINEL"] = tmpSentinel,
                ["NIGHTWEAVER_FIXTURE_RESUME_EVIDENCE"] = resumeEvidence
            };
            return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                target.CreateProcessLauncher(environment), cancellationToken);
        }

        var deliveryStages = new AgentStageCoordinator(processRunner, prepare: PrepareDelivery, execute: ExecuteDelivery);
        var deliveryRetries = new OperationalRetryCoordinator(processRunner, stages: deliveryStages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner deliveryRole = (host, config, targetRepository, requestedBead, role, actor, expectedState, token, progress) =>
            deliveryRetries.RunAsync(host, config, targetRepository, requestedBead, role, actor, token, progress, expectedState);
        var deliverySlot = new DeliverySlotCoordinator(processRunner, deliveryRole);
        var deliveryOutcome = await deliverySlot.RunAsync(target.Host, project, repository, beadId,
            TestContext.Current.CancellationToken);

        Assert.Equal(DeliverySlotDisposition.MergeReady, deliveryOutcome.Disposition);
        Assert.True(deliveryOutcome.ClaimHandoffCompleted, deliveryOutcome.Diagnostic);
        Assert.True(File.Exists(resumeEvidence));
        using (var evidence = JsonDocument.Parse(File.ReadAllText(resumeEvidence)))
        {
            Assert.Equal("implementer", evidence.RootElement.GetProperty("role").GetString());
            Assert.Equal(1, evidence.RootElement.GetProperty("repairCycles").GetInt32());
            Assert.True(evidence.RootElement.GetProperty("warmArtifactPresent").GetBoolean());
            Assert.True(evidence.RootElement.GetProperty("tmpArtifactPresent").GetBoolean());
        }
        Assert.All(deliveryPidFiles, AssertFixtureExited);
        Assert.Equal(originalBranch, target.CurrentBranch(beadId));
        Assert.True(File.Exists(warmSentinel));
        Assert.True(File.Exists(tmpSentinel));
        Assert.Equal("public sealed class LocalCheckFixture { public int Repaired => 1; }\n",
            File.ReadAllText(Path.Combine(worktree.Path, "BrokenFixture.cs")));
        var resumedState = target.ReadState(beadId);
        Assert.Equal("open", resumedState.Status);
        Assert.Equal("stage:merge", resumedState.Stage);
        Assert.Equal(1, resumedState.RepairCycles);
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
