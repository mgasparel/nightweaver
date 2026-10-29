using System.Collections.Concurrent;
using System.Diagnostics;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DeliveryPoolIntegrationTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task TwoNativeSlotsFinishIndependentlyAndTheFreedCapacityStartsAThirdSlot()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beads = new[]
        {
            target.CreateBead("Delivery pool A", "task", "stage:implement"),
            target.CreateBead("Delivery pool B", "task", "stage:implement"),
            target.CreateBead("Delivery pool C", "task", "stage:implement")
        };
        for (var index = 0; index < beads.Length; index++)
            target.RunTarget("bd", ["update", beads[index], "--priority", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var gates = beads.Take(2).ToDictionary(beadId => beadId, beadId =>
        {
            var directory = Path.Combine(target.RootPath, "delivery-pool-gates");
            Directory.CreateDirectory(directory);
            return (Started: Path.Combine(directory, beadId + ".started"), Release: Path.Combine(directory, beadId + ".release"));
        }, StringComparer.Ordinal);
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 2, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner());
        var prepare = new AgentInvocationPreparer();
        var invocationIdentities = new ConcurrentDictionary<string, (string BeadId, AgentRole Role)>(StringComparer.Ordinal);
        var observations = new ConcurrentQueue<PoolStageObservation>();
        var pidFiles = new ConcurrentBag<string>();
        long pidSequence = 0;

        async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            var invocation = await prepare.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
            var canonical = Path.GetFullPath(Path.Combine(hostPaths.WorktreesRoot(config.Name), context.BeadId));
            Assert.Equal(canonical, worktreePath);
            Assert.Equal(canonical, invocation.WorkingDirectory);
            Assert.True(invocationIdentities.TryAdd(invocation.InvocationId, (context.BeadId, role)));
            return invocation;
        }

        Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            var identity = invocationIdentities[invocation.InvocationId];
            var bead = target.ReadBead(identity.BeadId);
            var worktree = invocation.WorkingDirectory;
            var stage = BeadWorkflowState.Parse(bead).Stage;
            observations.Enqueue(new PoolStageObservation(identity.BeadId, identity.Role, stage, bead.Status, bead.Assignee,
                worktree, RunGit(worktree, "branch", "--show-current"), RunGit(worktree, "rev-parse", "HEAD")));

            var action = identity.Role switch
            {
                AgentRole.Implementer when gates.ContainsKey(identity.BeadId) => "pool-implement-gated",
                AgentRole.Implementer => "slot-implement-push",
                AgentRole.Reviewer => "slot-review",
                AgentRole.Qa => "slot-qa",
                _ => throw new InvalidOperationException($"Unexpected Delivery role {identity.Role}.")
            };
            var pidFile = Path.Combine(target.RootPath, "delivery-pool-pids", Interlocked.Increment(ref pidSequence).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
            pidFiles.Add(pidFile);
            var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = project.RecoveryActor,
                ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile,
                ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = gates.TryGetValue(identity.BeadId, out var gate) && identity.Role == AgentRole.Implementer ? gate.Started : null,
                ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = gates.TryGetValue(identity.BeadId, out gate) && identity.Role == AgentRole.Implementer ? gate.Release : null
            };
            return AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                target.CreateProcessLauncher(overrides), cancellationToken);
        }

        var stages = new AgentStageCoordinator(processRunner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages);
        DeliveryRoleRunner roleRunner = (hostPaths, config, repository, beadId, role, actor, expectedState, token, progress) =>
            retries.RunAsync(hostPaths, config, repository, beadId, role, actor, token, progress, expectedState);
        DeliveryPoolSlotRunner slotRunner = (hostPaths, config, repository, beadId, token) =>
            new DeliverySlotCoordinator(processRunner, roleRunner).RunAsync(hostPaths, config, repository, beadId, token);
        var pool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            runSlot: slotRunner, slotLifetimeToken: TestContext.Current.CancellationToken);

        try
        {
        var first = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, first.Admitted.Count);
        Assert.Equal(beads.Take(2).Order(StringComparer.Ordinal), first.Admitted.Select(admission => admission.BeadId).Order(StringComparer.Ordinal));
        var startSignals = first.Admitted.ToDictionary(admission => admission.BeadId,
            admission => WaitForFileAsync(gates[admission.BeadId].Started), StringComparer.Ordinal);
        try
        {
            await Task.WhenAll(startSignals.Values);
        }
        catch (TimeoutException exception)
        {
            var slotDetails = string.Join("; ", first.Admitted.Select(admission =>
            {
                var started = File.Exists(gates[admission.BeadId].Started);
                var result = admission.Completion.IsCompletedSuccessfully
                    ? $"completed: {admission.Completion.Result.Diagnostic ?? admission.Completion.Result.Outcome?.Disposition.ToString() ?? "no outcome"}"
                    : "still active";
                return $"{admission.BeadId}: started={started}, {result}";
            }));
            throw new TimeoutException($"Timed out waiting for both Delivery slots to launch. {slotDetails}", exception);
        }
        foreach (var beadId in beads.Take(2))
        {
            var claimed = target.ReadBead(beadId);
            Assert.Equal("in_progress", claimed.Status);
            Assert.Equal(project.RecoveryActor, claimed.Assignee);
        }

        File.WriteAllText(gates[beads[1]].Release, "release B", new System.Text.UTF8Encoding(false));
        var bCompletion = await first.Admitted.Single(admission => admission.BeadId == beads[1]).Completion
            .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(bCompletion.Outcome?.MergeReady, bCompletion.Diagnostic);
        Assert.Equal("stage:merge", target.ReadState(beads[1]).Stage);
        Assert.Equal("in_progress", target.ReadBead(beads[0]).Status);
        Assert.Equal(project.RecoveryActor, target.ReadBead(beads[0]).Assignee);

        var next = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal([beads[2]], next.Admitted.Select(admission => admission.BeadId));
        var cCompletion = await next.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(cCompletion.Outcome?.MergeReady, cCompletion.Diagnostic);
        Assert.Equal("in_progress", target.ReadBead(beads[0]).Status);

        File.WriteAllText(gates[beads[0]].Release, "release A", new System.Text.UTF8Encoding(false));
        var aCompletion = await first.Admitted.Single(admission => admission.BeadId == beads[0]).Completion
            .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(aCompletion.Outcome?.MergeReady,
            $"{aCompletion.Diagnostic} {aCompletion.Outcome?.LastStage} {aCompletion.Exception}");
        Assert.Empty(pool.ActiveReservations);

        foreach (var beadId in beads)
        {
            var state = target.ReadState(beadId);
            var final = target.ReadBead(beadId);
            Assert.Equal("stage:merge", state.Stage);
            Assert.Equal("open", final.Status);
            Assert.Null(final.Assignee);
            Assert.Equal($"nightweaver/{beadId}", target.CurrentBranch(beadId));
            Assert.True(File.Exists(Path.Combine(target.WorktreePath(beadId), "delivery-slot.txt")));
            Assert.True(File.Exists(Path.Combine(target.WorktreePath(beadId), ".nightweaver-ignored", "slot-sentinel.bin")));
            Assert.NotEmpty(target.RemoteBranchSha(beadId));
            var claimWrites = AgentStageTargetFixture.ClaimWrites(processRunner.Requests, beadId);
            Assert.Equal(2, claimWrites.Length);
            Assert.Single(claimWrites, request => AgentStageTargetFixture.ClaimWriteStatus(request) == "open");
            Assert.Single(claimWrites, request => AgentStageTargetFixture.ClaimWriteStatus(request) == "in_progress");
            Assert.Empty(RunGit(target.WorktreePath(beadId), "status", "--porcelain"));
        }

        var perBeadObservations = observations.GroupBy(observation => observation.BeadId, StringComparer.Ordinal).ToArray();
        Assert.Equal(beads.Length, perBeadObservations.Length);
        foreach (var entries in perBeadObservations)
        {
            var ordered = entries.ToArray();
            var stageRoles = new List<AgentRole>();
            foreach (var entry in ordered)
            {
                if (stageRoles.Count == 0 || stageRoles[^1] != entry.Role) stageRoles.Add(entry.Role);
            }
            Assert.Equal([AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa], stageRoles);
            Assert.All(ordered.Where(entry => entry.Role == AgentRole.Implementer), entry => Assert.Equal("stage:implement", entry.Stage));
            Assert.All(ordered.Where(entry => entry.Role == AgentRole.Reviewer), entry => Assert.Equal("stage:review", entry.Stage));
            Assert.All(ordered.Where(entry => entry.Role == AgentRole.Qa), entry => Assert.Equal("stage:qa", entry.Stage));
            Assert.All(ordered, entry =>
            {
                Assert.Equal("in_progress", entry.Status);
                Assert.Equal(project.RecoveryActor, entry.Assignee);
                Assert.Equal(Path.GetFullPath(target.WorktreePath(entry.BeadId)), entry.WorktreePath);
                Assert.Equal($"nightweaver/{entry.BeadId}", entry.Branch);
            });
            var reviewer = Assert.Single(ordered, entry => entry.Role == AgentRole.Reviewer);
            var qa = Assert.Single(ordered, entry => entry.Role == AgentRole.Qa);
            Assert.Equal(reviewer.Head, qa.Head);
        }

        Assert.Equal(beads.Length, new[] { aCompletion, bCompletion, cCompletion }.Select(completion => completion.BeadId).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            foreach (var gate in gates.Values)
                File.WriteAllText(gate.Release, "cleanup release", new System.Text.UTF8Encoding(false));

            var activeCompletions = pool.ActiveReservations.Select(reservation => reservation.Completion).ToArray();
            if (activeCompletions.Length > 0)
            {
                var allCompleted = Task.WhenAll(activeCompletions);
                await Task.WhenAny(allCompleted, Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            }

            foreach (var pidFile in pidFiles)
            {
                if (File.Exists(pidFile)) AssertFixtureExited(pidFile);
            }
        }
    }

    [Fact]
    public async Task RealCompetingGuardedClaimAfterEvaluationPreventsTheLoserFromLaunchingAnAgent()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Delivery pool claim race", "task", "stage:implement");
        target.RunTarget("bd", ["update", beadId, "--priority", "0"]);
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner());
        var reader = new BeadsReader(processRunner);
        var snapshotReady = NewSignal();
        var allowEvaluation = NewSignal();
        var readOnce = 0;
        var launchCount = 0;
        string? slotObservedAssignee = null;
        var slotReader = new BeadsReader(processRunner);
        var pool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            async (repository, actor, cancellationToken) =>
            {
                var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken);
                var readiness = await reader.ReadReadinessAsync(repository, inventory, actor, cancellationToken);
                if (Interlocked.Exchange(ref readOnce, 1) == 0)
                {
                    snapshotReady.TrySetResult(true);
                    await allowEvaluation.Task;
                }
                return new DeliveryPoolSnapshot(inventory, readiness);
            },
            async (hostPaths, config, repository, requestedBead, token) =>
            {
                var slotInventory = await slotReader.ReadSnapshotAsync(repository, token);
                slotObservedAssignee = slotInventory.ById[requestedBead].Assignee;
                var roleRunner = new DeliveryRoleRunner((_, _, _, _, _, _, _, _, _) =>
                {
                    Interlocked.Increment(ref launchCount);
                    throw new InvalidOperationException("A role launch must not occur after a competing claim.");
                });
                return await new DeliverySlotCoordinator(processRunner, roleRunner)
                    .RunAsync(hostPaths, config, repository, requestedBead, token);
            },
            TestContext.Current.CancellationToken);

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await snapshotReady.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var competingWrite = target.RunTarget("bd", ["--sandbox", "--actor", "human:competitor", "--dolt-auto-commit", "on",
            "update", beadId, "--if-status", "open", "--if-assignee", "", "--status", "in_progress", "--assignee", "human:competitor", "--json"]);
        Assert.Contains("in_progress", competingWrite, StringComparison.OrdinalIgnoreCase);
        var competitorState = target.ReadBead(beadId);
        Assert.Equal("in_progress", competitorState.Status);
        Assert.Equal("human:competitor", competitorState.Assignee);
        allowEvaluation.TrySetResult(true);

        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var admission = Assert.Single(evaluation.Admitted);
        var completion = await admission.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("human:competitor", slotObservedAssignee);
        Assert.True(completion.Outcome?.Disposition == DeliverySlotDisposition.Refused,
            completion.Diagnostic ?? completion.Exception?.ToString());
        Assert.Equal(0, launchCount);
        var final = target.ReadBead(beadId);
        Assert.Equal("in_progress", final.Status);
        Assert.Equal("human:competitor", final.Assignee);
        Assert.Empty(pool.ActiveReservations);
        var poolClaimAttempts = processRunner.Requests.Where(request => request.FileName == "bd"
            && request.Arguments.Contains(beadId, StringComparer.Ordinal)
            && request.Arguments.Contains("--if-assignee", StringComparer.Ordinal)
            && request.Arguments.Contains("--if-status", StringComparer.Ordinal)).ToArray();
        Assert.Empty(poolClaimAttempts);
    }

    private sealed record PoolStageObservation(string BeadId, AgentRole Role, string? Stage, string Status,
        string? Assignee, string WorktreePath, string Branch, string Head);

    private sealed class ConcurrentRecordingProcessRunner(IProcessRunner inner) : IProcessRunner
    {
        private readonly ConcurrentQueue<ProcessRequest> requests = new();
        internal IReadOnlyList<ProcessRequest> Requests => requests.ToArray();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            return inner.RunAsync(request, cancellationToken);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForFileAsync(string path)
    {
        if (File.Exists(path)) return;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var signal = NewSignal();
        using var watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => signal.TrySetResult(true);
        watcher.Changed += (_, _) => signal.TrySetResult(true);
        if (File.Exists(path)) signal.TrySetResult(true);
        try
        {
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out waiting for fixture start file '{path}'.", exception);
        }
    }

    private static void AssertFixtureExited(string pidFile)
    {
        Assert.True(File.Exists(pidFile), $"The native fixture did not create its process handshake: {pidFile}");
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(5000), $"Fixture process {pid} survived executor completion.");
        }
        catch (ArgumentException)
        {
            // The process exited before this check.
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
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr}{stdout}");
        return stdout.Trim();
    }
}
