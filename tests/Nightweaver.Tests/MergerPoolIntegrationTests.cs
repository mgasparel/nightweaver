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
    public async Task NativeContinuousMergerDeliveryRepairAndMergeUseVerifiedHandoffs()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.ConfigureHostedFixtureOriginWithLocalRewrite();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "needs-human"]);
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
        var independentDeliveryBeadId = target.CreateBead("Independent native Delivery while Merger is reserved", "task", "stage:implement");
        var queuedMergerBeadId = target.CreateBead("Queued singleton Merger candidate", "task", "stage:merge");
        var project = target.CreateProject(AgentCli.Codex, maxRepairCycles: 1, maxConcurrentDelivery: 3, timeoutMinutes: 5);
        var repository = new ValidatedTargetRepository(target.Path, "main")
        {
            GitCommonDirectory = target.RunTarget("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"]).Trim()
        };
        var systemRunner = new SystemProcessRunner();
        var mergerRepairPublicationEntered = NewSignal();
        var releaseMergerRepairPublication = NewSignal();
        var deliveryMergePublicationEntered = NewSignal();
        var releaseDeliveryMergePublication = NewSignal();
        bool IsGuardedPublication(ProcessRequest request, string stage)
        {
            var arguments = request.Arguments;
            var updateIndex = arguments.ToList().IndexOf("update");
            var addLabelIndex = arguments.ToList().IndexOf("--add-label");
            return request.FileName == "bd"
                && updateIndex >= 0 && updateIndex + 1 < arguments.Count && arguments[updateIndex + 1] == beadId
                && addLabelIndex >= 0 && addLabelIndex + 1 < arguments.Count && arguments[addLabelIndex + 1] == stage
                && arguments.Contains("--if-status", StringComparer.Ordinal)
                && arguments.Contains("--if-assignee", StringComparer.Ordinal);
        }

        async Task<ProcessResult> RunMergerProcessAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (IsGuardedPublication(request, "stage:implement"))
            {
                mergerRepairPublicationEntered.TrySetResult(true);
                await releaseMergerRepairPublication.Task.WaitAsync(cancellationToken);
            }
            return await target.RunWithFixturePullRequestAsync(systemRunner, request, cancellationToken);
        }

        async Task<ProcessResult> RunDeliveryProcessAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (IsGuardedPublication(request, "stage:merge"))
            {
                deliveryMergePublicationEntered.TrySetResult(true);
                await releaseDeliveryMergePublication.Task.WaitAsync(cancellationToken);
            }
            return await target.RunWithFixturePullRequestAsync(systemRunner, request, cancellationToken);
        }

        var mergerProcessRunner = new RecordingProcessRunner(RunMergerProcessAsync);
        var mergerDiscoveryProcessRunner = new RecordingProcessRunner(RunMergerProcessAsync);
        var deliveryProcessRunner = new RecordingProcessRunner(RunDeliveryProcessAsync);
        var provisioner = new WorktreeProvisioner(systemRunner);
        var worktree = await provisioner.ProvisionAsync(target.Host, project.Name, repository, beadId,
            WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var independentWorktree = await provisioner.ProvisionAsync(target.Host, project.Name, repository, independentDeliveryBeadId,
            WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        var independentGateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "independent-delivery-gate")).FullName;
        var independentStartedFile = Path.Combine(independentGateDirectory, "started.txt");
        var independentReleaseFile = Path.Combine(independentGateDirectory, "release.txt");
        var independentPidFile = Path.Combine(independentGateDirectory, "pid.txt");
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
        var mergerPidFiles = new List<string>();
        var mergerAttempt = 0;
        var preparer = new AgentInvocationPreparer();
        var stopGate = new ContinuousStopGate();
        using var runtimeLifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var mergerPrepareCalls = 0;
        var firstMergerPrepareEntered = NewSignal();
        var releaseFirstMergerPrepare = NewSignal();
        var secondMergerEntered = NewSignal();
        var releaseSecondMerger = NewSignal();

        async Task<PreparedAgentInvocation> PrepareMerger(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            Assert.Equal(beadId, context.BeadId);
            Assert.Equal(AgentRole.Merger, role);
            Assert.Equal(worktree.Path, worktreePath);
            Assert.Equal(originalBranch, RunGit(worktreePath, "branch", "--show-current"));
            var prepareCall = Interlocked.Increment(ref mergerPrepareCalls);
            if (prepareCall == 1)
            {
                firstMergerPrepareEntered.TrySetResult(true);
                await releaseFirstMergerPrepare.Task.WaitAsync(cancellationToken);
            }
            else if (prepareCall == 2)
            {
                secondMergerEntered.TrySetResult(true);
                await releaseSecondMerger.Task.WaitAsync(cancellationToken);
            }
            return await preparer.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
        }

        Task<AgentExecutionResult> ExecuteMerger(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            var currentAttempt = Interlocked.Increment(ref mergerAttempt);
            var pidFile = Path.Combine(target.RootPath, $"merger-continuous-{currentAttempt}.pid");
            mergerPidFiles.Add(pidFile);
            return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BD_ACTOR"] = project.RecoveryActor,
                    ["NIGHTWEAVER_DEFAULT_BRANCH"] = project.TargetRepository.DefaultBranch,
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = currentAttempt == 1 ? "merger-reject-checks" : "merger-verified-merge",
                    ["NIGHTWEAVER_FIXTURE_CHECK_SOURCE"] = "local",
                    ["NIGHTWEAVER_FIXTURE_GH_LOG"] = ghLog,
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile
                }), cancellationToken);
        }

        var mergerStages = new AgentStageCoordinator(mergerProcessRunner, prepare: PrepareMerger, execute: ExecuteMerger);
        var mergerRetries = new OperationalRetryCoordinator(mergerProcessRunner, stages: mergerStages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        MergerRoleRunner mergerRole = (host, config, targetRepository, requestedBead, role, actor, expectedState, token, progress) =>
            mergerRetries.RunAsync(host, config, targetRepository, requestedBead, role, actor, token, progress, expectedState);
        var slot = new MergerSlotCoordinator(mergerProcessRunner, mergerRole);
        var mergerOutcomes = Channel.CreateUnbounded<MergerSlotOutcome>();
        var handoffTrace = new ConcurrentQueue<string>();
        MergerPoolSlotRunner mergerSlotRunner = async (host, config, targetRepository, requestedBead, agentAttemptStarted, token) =>
        {
            var outcome = await slot.RunAsync(host, config, targetRepository, requestedBead, agentAttemptStarted, token);
            handoffTrace.Enqueue($"Merger:{outcome.Disposition}");
            mergerOutcomes.Writer.TryWrite(outcome);
            return outcome;
        };
        var mergerPool = new MergerPoolCoordinator(target.Host, project, repository, mergerDiscoveryProcessRunner,
            runSlot: mergerSlotRunner, stopGate: stopGate, slotLifetimeToken: runtimeLifetime.Token);

        var resumeEvidence = Path.Combine(target.RootPath, "delivery-resume-evidence.json");
        var deliveryPidFiles = new List<string>();
        var deliveryRoles = new Dictionary<string, (string BeadId, AgentRole Role)>(StringComparer.Ordinal);
        var deliveryPrepareEntered = NewSignal();
        var releaseDeliveryPrepare = NewSignal();
        var holdPrimaryDeliveryPrepare = 1;
        async Task<PreparedAgentInvocation> PrepareDelivery(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            Assert.Contains(context.BeadId, new[] { beadId, independentDeliveryBeadId });
            var expectedWorktree = context.BeadId == beadId ? worktree : independentWorktree;
            Assert.Equal(expectedWorktree.Path, worktreePath);
            if (context.BeadId == beadId)
            {
                Assert.Equal(originalBranch, RunGit(worktreePath, "branch", "--show-current"));
                Assert.True(File.Exists(warmSentinel), "Delivery preparation lost the retained warm artifact.");
            }
            var invocation = await preparer.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
            deliveryRoles.Add(invocation.InvocationId, (context.BeadId, role));
            if (context.BeadId == beadId && Interlocked.Exchange(ref holdPrimaryDeliveryPrepare, 0) == 1)
            {
                deliveryPrepareEntered.TrySetResult(true);
                await releaseDeliveryPrepare.Task.WaitAsync(cancellationToken);
            }
            return invocation;
        }

        Task<AgentExecutionResult> ExecuteDelivery(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            var (executingBeadId, role) = deliveryRoles[invocation.InvocationId];
            if (executingBeadId == independentDeliveryBeadId)
            {
                Assert.Equal(AgentRole.Implementer, role);
                return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                    target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["BD_ACTOR"] = project.RecoveryActor,
                        ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
                        ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = independentStartedFile,
                        ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = independentReleaseFile,
                        ["NIGHTWEAVER_FIXTURE_PID_FILE"] = independentPidFile
                    }), cancellationToken);
            }
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

        var deliveryStages = new AgentStageCoordinator(deliveryProcessRunner, prepare: PrepareDelivery, execute: ExecuteDelivery);
        var deliveryRetries = new OperationalRetryCoordinator(deliveryProcessRunner, stages: deliveryStages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner deliveryRole = (host, config, targetRepository, requestedBead, role, actor, expectedState, token, progress) =>
            deliveryRetries.RunAsync(host, config, targetRepository, requestedBead, role, actor, token, progress, expectedState);
        var deliverySlot = new DeliverySlotCoordinator(deliveryProcessRunner, deliveryRole);
        var deliveryEntered = NewSignal();
        var releaseDelivery = NewSignal();
        var holdPrimaryDelivery = 1;
        var deliveryOutcomes = Channel.CreateUnbounded<DeliverySlotOutcome>();
        DeliveryPoolSlotRunner deliverySlotRunner = async (host, config, targetRepository, requestedBead, token) =>
        {
            if (requestedBead == beadId && Interlocked.Exchange(ref holdPrimaryDelivery, 0) == 1)
            {
                handoffTrace.Enqueue("Delivery:reserved");
                deliveryEntered.TrySetResult(true);
                await releaseDelivery.Task.WaitAsync(token);
            }
            var outcome = await deliverySlot.RunAsync(host, config, targetRepository, requestedBead, token);
            if (requestedBead == beadId)
            {
                handoffTrace.Enqueue($"Delivery:{outcome.Disposition}");
                deliveryOutcomes.Writer.TryWrite(outcome);
            }
            else
            {
                handoffTrace.Enqueue($"Delivery:{requestedBead}:{outcome.Disposition}");
            }
            return outcome;
        };
        var deliveryPool = new DeliveryPoolCoordinator(target.Host, project, repository, deliveryProcessRunner,
            runSlot: deliverySlotRunner, stopGate: stopGate, slotLifetimeToken: runtimeLifetime.Token);
        var runtimeOutput = new StringWriter();
        var runtime = new ContinuousDiscoveryCoordinator(
            [
                new ContinuousPoolRegistration(ContinuousPoolKind.Delivery,
                    async token => (await deliveryPool.EvaluateAsync(token)).Diagnostics,
                    deliveryPool.CompletionReader,
                    runtimeLifetime.Cancel,
                    async token => { await deliveryPool.WaitForActiveSlotsAsync(token); },
                    deliveryPool.WaitForCompletionAccountingAsync),
                new ContinuousPoolRegistration(ContinuousPoolKind.Merger,
                    async token => (await mergerPool.EvaluateAsync(token)).Diagnostics,
                    mergerPool.CompletionReader,
                    runtimeLifetime.Cancel,
                    async token => { await mergerPool.WaitForActiveSlotsAsync(token); },
                    mergerPool.WaitForCompletionAccountingAsync)
            ],
            _ => Task.CompletedTask,
            runtimeOutput,
            TextWriter.Null,
            stopGate: stopGate);
        var runtimeTask = runtime.RunAsync(runtimeLifetime.Token);

        try
        {
            await firstMergerPrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            await WaitForFileAsync(independentStartedFile, TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            await WaitForFileAsync(independentPidFile, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var independentIdentity = File.ReadAllText(independentPidFile).Split('|', StringSplitOptions.TrimEntries);
            using var independentAgent = Process.GetProcessById(int.Parse(independentIdentity[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(independentAgent.HasExited, "The independent Delivery Agent exited before demonstrating concurrent native progress.");
            var heldMergerState = target.ReadState(beadId);
            Assert.Equal("in_progress", heldMergerState.Status);
            Assert.Equal("stage:merge", heldMergerState.Stage);
            Assert.Equal(project.RecoveryActor, target.ReadBead(beadId).Assignee);
            var queuedMerger = target.ReadBead(queuedMergerBeadId);
            Assert.Equal("open", queuedMerger.Status);
            Assert.Null(queuedMerger.Assignee);
            Assert.Equal("stage:merge", BeadWorkflowState.Parse(queuedMerger).Stage);
            var independentDelivery = target.ReadBead(independentDeliveryBeadId);
            Assert.Equal("in_progress", independentDelivery.Status);
            Assert.Equal(project.RecoveryActor, independentDelivery.Assignee);
            Assert.Equal("stage:implement", BeadWorkflowState.Parse(independentDelivery).Stage);
            target.RunTarget("bd", ["update", queuedMergerBeadId, "--add-label", "needs-human"]);
            releaseFirstMergerPrepare.TrySetResult(true);

            await mergerRepairPublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            var preRepairPublication = target.ReadState(beadId);
            Assert.Equal("in_progress", preRepairPublication.Status);
            Assert.Equal("stage:merge", preRepairPublication.Stage);
            Assert.Equal(project.RecoveryActor, target.ReadBead(beadId).Assignee);
            Assert.Contains(mergerProcessRunner.Requests, request => IsGuardedPublication(request, "stage:implement"));
            releaseMergerRepairPublication.TrySetResult(true);

            var firstMerger = await mergerOutcomes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            Assert.True(firstMerger.Disposition == MergerSlotDisposition.RepairHandedOff,
                $"disposition={firstMerger.Disposition}; diagnostic={firstMerger.Diagnostic}; stage={firstMerger.Stage?.StageResult.OutcomeClassification?.Diagnostic}; "
                + $"stageOutcome={firstMerger.Stage?.StageResult.OutcomeClassification?.Outcome}; stageFailure={firstMerger.Stage?.StageResult.Failure?.Reason}; "
                + $"escalationFindings={string.Join(" | ", firstMerger.Stage?.Escalation?.Failures ?? [])}");
            Assert.True(firstMerger.RepairHandoffCompleted, firstMerger.Diagnostic);
            await deliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);

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
            AssertFixtureExited(mergerPidFiles[0]);
            Assert.Equal("Merger:RepairHandedOff", handoffTrace.First());
            var mergerRequestsAtHandoff = mergerProcessRunner.Requests.Count;
            releaseDelivery.TrySetResult(true);

            await deliveryPrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            var receivingDeliveryState = target.ReadState(beadId);
            Assert.Equal("in_progress", receivingDeliveryState.Status);
            Assert.Equal("stage:implement", receivingDeliveryState.Stage);
            Assert.Equal(project.RecoveryActor, target.ReadBead(beadId).Assignee);
            var mergerRequestsAfterDeliveryPrepare = mergerProcessRunner.Requests;
            Assert.Equal(mergerRequestsAtHandoff, mergerRequestsAfterDeliveryPrepare.Count);
            releaseDeliveryPrepare.TrySetResult(true);

            await deliveryMergePublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            var preMergePublication = target.ReadState(beadId);
            Assert.Equal("in_progress", preMergePublication.Status);
            Assert.Equal("stage:qa", preMergePublication.Stage);
            Assert.Equal(project.RecoveryActor, target.ReadBead(beadId).Assignee);
            Assert.Contains(deliveryProcessRunner.Requests, request => IsGuardedPublication(request, "stage:merge"));
            releaseDeliveryMergePublication.TrySetResult(true);

            var deliveryOutcome = await deliveryOutcomes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
            Assert.Equal(DeliverySlotDisposition.MergeReady, deliveryOutcome.Disposition);
            Assert.True(deliveryOutcome.ClaimHandoffCompleted, deliveryOutcome.Diagnostic);
        await secondMergerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var receivingMergerState = target.ReadState(beadId);
        Assert.Equal("in_progress", receivingMergerState.Status);
        Assert.Equal("stage:merge", receivingMergerState.Stage);
        Assert.Equal(project.RecoveryActor, target.ReadBead(beadId).Assignee);
        var deliveryRequestsAtHandoff = deliveryProcessRunner.Requests.Count;
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
        Assert.Equal("in_progress", resumedState.Status);
        Assert.Equal("stage:merge", resumedState.Stage);
        Assert.Equal(project.RecoveryActor, target.ReadBead(beadId).Assignee);
        Assert.Equal(1, resumedState.RepairCycles);
        Assert.Equal(deliveryRequestsAtHandoff, deliveryProcessRunner.Requests.Count);
            releaseSecondMerger.TrySetResult(true);
            var secondMerger = await mergerOutcomes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
            Assert.True(secondMerger.Disposition == MergerSlotDisposition.Merged,
                $"disposition={secondMerger.Disposition}; diagnostic={secondMerger.Diagnostic}; stage={secondMerger.Stage?.StageResult.OutcomeClassification?.Diagnostic}; "
                + $"stageOutcome={secondMerger.Stage?.StageResult.OutcomeClassification?.Outcome}; stageFailure={secondMerger.Stage?.StageResult.Failure?.Reason}; "
                + $"escalationCause={secondMerger.Stage?.EscalationCause}; duplicateFailure={secondMerger.Stage?.LastClassification?.Verdict}:"
                + $"{secondMerger.Stage?.LastClassification?.Diagnostic}; escalationFindings={string.Join(" | ", secondMerger.EscalationProgress?.Failures ?? [])}");
            Assert.True(secondMerger.MergeCompleted, secondMerger.Diagnostic);
            Assert.Equal(["Merger:RepairHandedOff", "Delivery:reserved", "Delivery:MergeReady", "Merger:Merged"], handoffTrace.ToArray());
            Assert.Contains("Merger slot", runtimeOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Delivery slot", runtimeOutput.ToString(), StringComparison.Ordinal);

            var completed = target.ReadBead(beadId);
            Assert.Equal("closed", completed.Status);
            Assert.Null(completed.Assignee);
            Assert.Equal(1, BeadWorkflowState.Parse(completed).RepairCycles);
            Assert.False(Directory.Exists(worktree.Path));
            Assert.False(Directory.Exists(Path.Combine(target.Host.TmpRoot(project.Name), beadId)));
            Assert.All(deliveryPidFiles, AssertFixtureExited);
            AssertFixtureExited(mergerPidFiles[1]);
            runtimeLifetime.Cancel();
            File.WriteAllText(independentReleaseFile, "cancel independent native Delivery", new System.Text.UTF8Encoding(false));
            Assert.True(await runtimeTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            AssertFixtureExited(independentPidFile);
        }
        finally
        {
            File.WriteAllText(independentReleaseFile, "test cleanup", new System.Text.UTF8Encoding(false));
            runtimeLifetime.Cancel();
            if (!runtimeTask.IsCompleted)
                await runtimeTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
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
