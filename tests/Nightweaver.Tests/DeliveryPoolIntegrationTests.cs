using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DeliveryPoolIntegrationTests : AgentStageIntegrationTestSupport
{

    private async Task RunFirstStopDuringPreparationAsync(bool changeOwnerDuringPreparation)
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Graceful stop during preparation", "task", "stage:implement");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        var preparationStarted = NewSignal();
        var releasePreparation = NewSignal();
        var fixtureStartedFile = Path.Combine(target.RootPath, "prepared-agent.pid");
        var prepare = new AgentInvocationPreparer();
        var invocationIdentities = new ConcurrentDictionary<string, AgentRole>(StringComparer.Ordinal);
        var executeCalls = 0;
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        var stopGate = new ContinuousStopGate();

        async Task<PreparedAgentInvocation> Prepare(HostPaths paths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken token)
        {
            var invocation = await prepare.PrepareAsync(paths, config, role, context, worktreePath, token);
            invocationIdentities[invocation.InvocationId] = role;
            if (role == AgentRole.Implementer)
            {
                preparationStarted.TrySetResult(true);
                await releasePreparation.Task.WaitAsync(token);
            }
            return invocation;
        }

        Task<AgentExecutionResult> Execute(HostPaths paths, ProjectSlug slug, PreparedAgentInvocation invocation,
            CancellationToken token)
        {
            Interlocked.Increment(ref executeCalls);
            var role = invocationIdentities[invocation.InvocationId];
            return AgentInvocationExecutor.ExecuteAsync(paths, slug, invocation,
                target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BD_ACTOR"] = project.RecoveryActor,
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = "slot-implement-push",
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = role == AgentRole.Implementer ? fixtureStartedFile : null
                }), token);
        }

        var stages = new AgentStageCoordinator(processRunner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner runRole = (paths, config, repository, id, role, actor, expectedState, token, progress) =>
            retries.RunAsync(paths, config, repository, id, role, actor, token, progress, expectedState);
        DeliveryPoolSlotRunner slotRunner = (paths, config, repository, id, token) =>
            new DeliverySlotCoordinator(processRunner, runRole).RunAsync(paths, config, repository, id, token);
        var pool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            runSlot: slotRunner,
            stopGate: stopGate,
            slotLifetimeToken: forceStop.Token);
        var evaluations = Channel.CreateUnbounded<DeliveryPoolEvaluation>();
        var noTicks = Channel.CreateUnbounded<bool>();
        var runtime = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                async token =>
                {
                    var evaluation = await pool.EvaluateAsync(token);
                    evaluations.Writer.TryWrite(evaluation);
                    return evaluation.Diagnostics;
                },
                pool.CompletionReader,
                forceStop.Cancel,
                async token => { await pool.WaitForActiveSlotsAsync(token); },
                pool.WaitForCompletionAccountingAsync)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTimerTicksAsync(noTicks.Reader, token),
            stopGate: stopGate);
        var runtimeTask = runtime.RunAsync(gracefulStop.Token, forceStop.Token);

        try
        {
            var first = await evaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(beadId, Assert.Single(first.Admitted).BeadId);
            await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            if (changeOwnerDuringPreparation)
                target.RunTarget("bd", ["update", beadId, "--assignee", "human:competitor", "--force", "--json"]);
            gracefulStop.Cancel();
            releasePreparation.TrySetResult(true);
            Assert.Equal(!changeOwnerDuringPreparation,
                await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));

            Assert.Equal(1, Volatile.Read(ref executeCalls));
            Assert.False(File.Exists(fixtureStartedFile), "The prepared invocation must be denied before native process launch.");
            Assert.Empty(pool.ActiveReservations);
            var final = target.ReadBead(beadId);
            Assert.Equal(changeOwnerDuringPreparation ? "in_progress" : "open", final.Status);
            Assert.Equal(changeOwnerDuringPreparation ? "human:competitor" : null, final.Assignee);
            Assert.Equal("stage:implement", BeadWorkflowState.Parse(final).Stage);
            Assert.True(Directory.Exists(target.WorktreePath(beadId)), "The unfinished prepared worktree must remain available.");
        }
        finally
        {
            releasePreparation.TrySetResult(true);
            if (!runtimeTask.IsCompleted) gracefulStop.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), CancellationToken.None);
        }
    }

    private static ContinuousDiscoveryCoordinator CreateClaimBoundaryRuntime(
        AgentStageTargetFixture target,
        ProjectConfig project,
        IProcessRunner processRunner,
        ContinuousStopGate stopGate,
        CancellationToken forceStopToken,
        out DeliveryPoolCoordinator pool)
    {
        DeliveryRoleRunner mustNotLaunch = (_, _, _, _, _, _, _, _, _) =>
            Task.FromException<OperationalRetryOutcome>(new InvalidOperationException("No Agent may launch in this claim-boundary test."));
        DeliveryPoolSlotRunner runSlot = (paths, config, repository, beadId, token) =>
            new DeliverySlotCoordinator(processRunner, mustNotLaunch).RunAsync(paths, config, repository, beadId, token);
        var deliveryPool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            runSlot: runSlot, stopGate: stopGate, slotLifetimeToken: forceStopToken);
        pool = deliveryPool;
        var noTicks = Channel.CreateUnbounded<bool>();
        return new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                async token => (await deliveryPool.EvaluateAsync(token).ConfigureAwait(false)).Diagnostics,
                deliveryPool.CompletionReader,
                () => { },
                async token => { await deliveryPool.WaitForActiveSlotsAsync(token).ConfigureAwait(false); },
                deliveryPool.WaitForCompletionAccountingAsync)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTimerTicksAsync(noTicks.Reader, token),
            stopGate: stopGate);
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
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
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
            slotLifetimeToken: TestContext.Current.CancellationToken);

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

    private sealed class PostAcquireReadinessBarrierProcessRunner(IProcessRunner inner, string beadId) : IProcessRunner
    {
        private readonly TaskCompletionSource<bool> recoverableReadinessBlocked = NewSignal();
        private readonly TaskCompletionSource<bool> continueReadinessQuery = NewSignal();
        private int deliveryClaimAcquired;
        private int readinessBarrierUsed;

        internal Task RecoverableReadinessBlocked => recoverableReadinessBlocked.Task;

        internal void ContinueReadinessQuery() => continueReadinessQuery.TrySetResult(true);

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.FileName == "bd" && IsDeliveryClaimAcquire(request, beadId))
            {
                var result = await inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0)
                    Volatile.Write(ref deliveryClaimAcquired, 1);
                return result;
            }

            if (Volatile.Read(ref deliveryClaimAcquired) != 0
                && IsRecoverableReadiness(request)
                && Interlocked.CompareExchange(ref readinessBarrierUsed, 1, 0) == 0)
            {
                recoverableReadinessBlocked.TrySetResult(true);
                await continueReadinessQuery.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsDeliveryClaimAcquire(ProcessRequest request, string expectedBeadId)
        {
            var updateIndex = request.Arguments.ToList().IndexOf("update");
            return updateIndex >= 0 && updateIndex + 1 < request.Arguments.Count
                && request.Arguments[updateIndex + 1] == expectedBeadId
                && ArgumentAfter(request, "--if-status") == "open"
                && ArgumentAfter(request, "--if-assignee") == string.Empty
                && ArgumentAfter(request, "--status") == "in_progress"
                && request.Arguments.Contains("--assignee", StringComparer.Ordinal);
        }

        private static bool IsRecoverableReadiness(ProcessRequest request) =>
            request.FileName == "bd"
            && request.Arguments.Contains("list", StringComparer.Ordinal)
            && request.Arguments.Contains("--ready", StringComparer.Ordinal)
            && ArgumentAfter(request, "--status") == "in_progress"
            && request.Arguments.Contains("--assignee", StringComparer.Ordinal);

        private static string? ArgumentAfter(ProcessRequest request, string option)
        {
            var index = request.Arguments.ToList().IndexOf(option);
            return index >= 0 && index + 1 < request.Arguments.Count ? request.Arguments[index + 1] : null;
        }
    }

    private sealed class ConcurrentRecordingProcessRunner(
        IProcessRunner inner,
        AgentStageTargetFixture? target = null,
        bool failFirstInventoryRead = false) : IProcessRunner
    {
        private readonly ConcurrentQueue<ProcessRequest> requests = new();
        private readonly TaskCompletionSource<bool> firstInventoryReadFailure = NewSignal();
        private int failureInjected;
        internal IReadOnlyList<ProcessRequest> Requests => requests.ToArray();
        internal Task FirstInventoryReadFailure => firstInventoryReadFailure.Task;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            if (failFirstInventoryRead && request.FileName == "bd"
                && request.Arguments.Contains("--all", StringComparer.Ordinal)
                && Interlocked.CompareExchange(ref failureInjected, 1, 0) == 0)
            {
                firstInventoryReadFailure.TrySetResult(true);
                return Task.FromResult(new ProcessResult(1, "", "controlled temporary inventory read outage"));
            }
            return target is null
                ? inner.RunAsync(request, cancellationToken)
                : target.RunWithFixturePullRequestAsync(inner, request, cancellationToken);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async IAsyncEnumerable<bool> ReadTimerTicksAsync(ChannelReader<bool> ticks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var tick in ticks.ReadAllAsync(cancellationToken))
            yield return tick;
    }

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
            // The process exited before this check.
        }
    }

    private static int ReadFixtureProcessId(string pidFile)
    {
        Assert.True(File.Exists(pidFile), $"The native fixture did not create its process handshake: {pidFile}");
        var identity = File.ReadAllText(pidFile).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, identity.Length);
        return int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture);
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
