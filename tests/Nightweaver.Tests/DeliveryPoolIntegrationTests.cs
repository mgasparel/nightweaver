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
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        var prepare = new AgentInvocationPreparer();
        var invocationIdentities = new ConcurrentDictionary<string, (string BeadId, AgentRole Role)>(StringComparer.Ordinal);
        var observations = new ConcurrentQueue<PoolStageObservation>();
        var pidFiles = new ConcurrentBag<string>();
        var preparationAttempts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var executionAttempts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var preparationReturned = new TaskCompletionSource<PreparedAgentInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journalEnableAttempted = NewSignal();
        var allowJournalEnable = NewSignal();
        var journalEnabled = NewSignal();
        var reconnectDelayEntered = NewSignal();
        var allowReconnect = NewSignal();
        var tailProcesses = Channel.CreateUnbounded<Process>();
        var duplicateBurstDelivered = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mergerEvaluations = Channel.CreateUnbounded<int>();
        var mergerCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        long pidSequence = 0;

        async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            var invocation = await prepare.PrepareAsync(hostPaths, config, role, context, worktreePath, cancellationToken);
            var canonical = Path.GetFullPath(Path.Combine(hostPaths.WorktreesRoot(config.Name), context.BeadId));
            Assert.Equal(canonical, worktreePath);
            Assert.Equal(canonical, invocation.WorkingDirectory);
            Assert.True(invocationIdentities.TryAdd(invocation.InvocationId, (context.BeadId, role)));
            if (role == AgentRole.Implementer && context.BeadId == beads[0])
            {
                preparationAttempts.AddOrUpdate(context.BeadId, 1, static (_, count) => count + 1);
                preparationReturned.TrySetResult(invocation);
                // Hold the real prepared invocation before AgentStageCoordinator can hand it to
                // the pool's Execute delegate (and before an Agent process can be launched).
                await releasePreparation.Task.WaitAsync(cancellationToken);
            }
            return invocation;
        }

        Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            var identity = invocationIdentities[invocation.InvocationId];
            executionAttempts.AddOrUpdate(identity.BeadId, 1, static (_, count) => count + 1);
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
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner roleRunner = (hostPaths, config, repository, beadId, role, actor, expectedState, token, progress) =>
            retries.RunAsync(hostPaths, config, repository, beadId, role, actor, token, progress, expectedState);
        DeliveryPoolSlotRunner slotRunner = (hostPaths, config, repository, beadId, token) =>
            new DeliverySlotCoordinator(processRunner, roleRunner).RunAsync(hostPaths, config, repository, beadId, token);
        using var runtimeLifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            runSlot: slotRunner, slotLifetimeToken: runtimeLifetime.Token);
        var nativeJournalClient = new GatedNativeJournalClient(
            new BeadsJournalClient(processRunner, request =>
            {
                var process = StartNativeJournalTail(request);
                tailProcesses.Writer.TryWrite(process);
                return process;
            }), journalEnableAttempted, allowJournalEnable, journalEnabled, duplicateBurstDelivered);
        var evaluations = Channel.CreateUnbounded<DeliveryPoolEvaluation>();
        var noTimerTicks = Channel.CreateUnbounded<bool>();
        var runtime = new ContinuousDiscoveryCoordinator(
            [
                new ContinuousPoolRegistration(
                    ContinuousPoolKind.Delivery,
                    async token =>
                    {
                        var evaluation = await pool.EvaluateAsync(token);
                        evaluations.Writer.TryWrite(evaluation);
                        return evaluation.Diagnostics;
                    },
                    pool.CompletionReader,
                    runtimeLifetime.Cancel,
                    async token => { await pool.WaitForActiveSlotsAsync(token); },
                    pool.WaitForCompletionAccountingAsync),
                new ContinuousPoolRegistration(
                    ContinuousPoolKind.Merger,
                    _ =>
                    {
                        mergerEvaluations.Writer.TryWrite(1);
                        return Task.FromResult<IReadOnlyList<DeliveryPoolDiagnostic>>([]);
                    },
                    mergerCompletions.Reader,
                    static () => { },
                    static _ => Task.CompletedTask)
            ],
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTimerTicksAsync(noTimerTicks.Reader, token),
            (requestDiscovery, token) => new BeadsJournalWatcher(
                nativeJournalClient,
                requestDiscovery,
                TextWriter.Null,
                (_, delayToken) =>
                {
                    reconnectDelayEntered.TrySetResult(true);
                    return allowReconnect.Task.WaitAsync(delayToken);
                }).RunAsync(new(target.Path, "main"), token));
        var runtimeTask = runtime.RunAsync(runtimeLifetime.Token);

        try
        {
        await journalEnableAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var first = await evaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var completedBeforeAttempt = first.Admitted.Where(admission => admission.Completion.IsCompletedSuccessfully)
            .Select(admission =>
            {
                var completion = admission.Completion.Result;
                return $"{admission.BeadId}: disposition={completion.Outcome?.Disposition}; exception={completion.Exception}; diagnostic={completion.Diagnostic}";
            });
        Assert.True(first.Admitted.Count == 2,
            $"Expected exactly two gated slot admissions. Actual={first.Admitted.Count}; diagnostics="
            + string.Join(" | ", first.Diagnostics.Select(diagnostic => $"{diagnostic.BeadId}:{diagnostic.Code}:{diagnostic.Message}"))
            + "; completed-before-attempt=" + string.Join(" | ", completedBeforeAttempt));
        Assert.Equal(beads.Take(2).Order(StringComparer.Ordinal), first.Admitted.Select(admission => admission.BeadId).Order(StringComparer.Ordinal));
        var preparation = await preparationReturned.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(preparation.WorkingDirectory), $"The real preparer did not create A's worktree: {preparation.WorkingDirectory}");
        Assert.Equal($"nightweaver/{beads[0]}", RunGit(preparation.WorkingDirectory, "branch", "--show-current"));
        Assert.Equal(beads[0], invocationIdentities[preparation.InvocationId].BeadId);
        Assert.DoesNotContain(beads[0], executionAttempts.Keys);
        var bStarted = WaitForFileAsync(gates[beads[1]].Started);
        var mergerFirstEvaluation = mergerEvaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await Task.WhenAll(bStarted, mergerFirstEvaluation).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            var aReachedExecute = executionAttempts.ContainsKey(beads[0]);
            throw new TimeoutException($"Timed out waiting for real A preparation and later Merger evaluation. A reached Execute={aReachedExecute}; B started={File.Exists(gates[beads[1]].Started)}; {exception.Message}", exception);
        }
        Assert.False(releasePreparation.Task.IsCompleted, "A's preparation gate must still be holding before any A agent launch.");
        Assert.Equal(1, preparationAttempts[beads[0]]);
        Assert.Equal(2, pool.ActiveReservations.Count);
        Assert.Contains(pool.ActiveReservations, reservation => reservation.BeadId == beads[0]);
        Assert.Contains(pool.ActiveReservations, reservation => reservation.BeadId == beads[1]);
        Assert.False(journalEnabled.Task.IsCompleted,
            "Startup discovery and real Delivery admission must finish while native journal attachment is still blocked.");
        foreach (var beadId in beads.Take(2))
        {
            var claimed = target.ReadBead(beadId);
            Assert.Equal("in_progress", claimed.Status);
            Assert.Equal(project.RecoveryActor, claimed.Assignee);
        }

        // The selected native tracker emits one real event followed by duplicate and older
        // sequence hints. Every hint is only a wakeup: fresh tracker eligibility and existing
        // reservations prevent duplicate Agent launches.
        allowJournalEnable.TrySetResult(true);
        await journalEnabled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var initialJournalHead = await ReadNativeJournalHeadAsync(processRunner, target.Path,
            TestContext.Current.CancellationToken);
        var firstTail = await tailProcesses.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await nativeJournalClient.WaitForObservedSequenceAsync(initialJournalHead,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await DrainEvaluationsUntilQuietAsync(evaluations.Reader, TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);
        nativeJournalClient.DuplicateNextEventBurst();
        target.RunTarget("bd", ["comments", "add", beads[0], "duplicate and old journal wakeup fixture"]);
        var burstSequence = await duplicateBurstDelivered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(burstSequence > 0);
        var repeatedEvaluation = await evaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.Empty(repeatedEvaluation.Admitted);
        Assert.Equal(2, repeatedEvaluation.ActiveReservations.Count);
        Assert.Equal(1, preparationAttempts[beads[0]]);
        Assert.DoesNotContain(beads[0], executionAttempts.Keys);
        Assert.Equal(1, executionAttempts[beads[1]]);
        await mergerEvaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.False(releasePreparation.Task.IsCompleted, "The later Merger pool must evaluate while A preparation remains gated.");

        // Stop the actual native tail. While reconnect delay is held, the B completion must
        // still request discovery and admit C without disturbing A's active reservation.
        if (!firstTail.HasExited) firstTail.Kill(entireProcessTree: true);
        await reconnectDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        File.WriteAllText(gates[beads[1]].Release, "release B", new System.Text.UTF8Encoding(false));
        var bCompletion = await first.Admitted.Single(admission => admission.BeadId == beads[1]).Completion
            .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(bCompletion.Outcome?.MergeReady, bCompletion.Diagnostic);
        Assert.Equal("stage:merge", target.ReadState(beads[1]).Stage);
        Assert.Equal("in_progress", target.ReadBead(beads[0]).Status);
        Assert.Equal(project.RecoveryActor, target.ReadBead(beads[0]).Assignee);

        var next = await ReadUntilAdmittedAsync(beads[2]);
        Assert.Equal([beads[2]], next.Admitted.Select(admission => admission.BeadId));
        var cCompletion = await next.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.True(cCompletion.Outcome?.MergeReady, cCompletion.Diagnostic);
        Assert.Equal("in_progress", target.ReadBead(beads[0]).Status);
        Assert.Equal(1, preparationAttempts[beads[0]]);
        Assert.DoesNotContain(beads[0], executionAttempts.Keys);
        Assert.Contains(pool.ActiveReservations, reservation => reservation.BeadId == beads[0]);

        releasePreparation.TrySetResult();
        await WaitForFileAsync(gates[beads[0]].Started).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        Assert.Equal(1, executionAttempts[beads[0]]);
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
            Assert.Equal(4, claimWrites.Length);
            Assert.Single(claimWrites, request => AgentStageTargetFixture.ClaimWriteStatus(request) == "open");
            Assert.Equal(3, claimWrites.Count(request => AgentStageTargetFixture.ClaimWriteStatus(request) == "in_progress"));
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
            allowJournalEnable.TrySetResult(true);
            allowReconnect.TrySetResult(true);
            releasePreparation.TrySetResult();
            foreach (var gate in gates.Values)
                File.WriteAllText(gate.Release, "cleanup release", new System.Text.UTF8Encoding(false));

            var activeCompletions = pool.ActiveReservations.Select(reservation => reservation.Completion).ToArray();
            if (activeCompletions.Length > 0)
            {
                var allCompleted = Task.WhenAll(activeCompletions);
                await Task.WhenAny(allCompleted, Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            }

            runtimeLifetime.Cancel();
            try { await runtimeTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }

            foreach (var pidFile in pidFiles)
            {
                if (File.Exists(pidFile)) AssertFixtureExited(pidFile);
            }
        }

        async Task<DeliveryPoolEvaluation> ReadUntilAdmittedAsync(string beadId)
        {
            while (true)
            {
                var evaluation = await evaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
                if (evaluation.Admitted.Any(admission => admission.BeadId == beadId)) return evaluation;
            }
        }
    }

    [Fact]
    public async Task TwoNativeSlotsDrainAfterOneRequiredFinalizationFailure()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var failedBead = target.CreateBead("Native slot with finalization fault", "task", "stage:implement");
        var healthyBead = target.CreateBead("Native slot that must still drain", "task", "stage:implement");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 2, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        target.FailDeliveryReleaseForBeadBeforeCommitOnce = failedBead;
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "two-native-slot-drain-gates")).FullName;
        var gates = new[] { failedBead, healthyBead }.ToDictionary(beadId => beadId, beadId => (
            Started: Path.Combine(gateDirectory, beadId + ".started"),
            Release: Path.Combine(gateDirectory, beadId + ".release"),
            Pid: Path.Combine(gateDirectory, beadId + ".pid")), StringComparer.Ordinal);
        var executionCount = 0;
        var evaluations = Channel.CreateUnbounded<DeliveryPoolEvaluation>();
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        using var slotLifetime = new CancellationTokenSource();
        using var forceRegistration = forceStop.Token.Register(slotLifetime.Cancel);
        var stopGate = new ContinuousStopGate();

        Task<AgentExecutionResult> Execute(HostPaths paths, ProjectSlug slug, PreparedAgentInvocation invocation,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            var beadId = invocation.EnvironmentOverrides["NIGHTWEAVER_BEAD_ID"];
            var gate = gates[beadId];
            var action = beadId == failedBead ? "pool-implement-fail-gated" : "pool-implement-gated";
            return AgentInvocationExecutor.ExecuteAsync(paths, slug, invocation,
                target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BD_ACTOR"] = project.RecoveryActor,
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                    ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = gate.Started,
                    ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = gate.Release,
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = gate.Pid
                }), cancellationToken);
        }

        var stages = new AgentStageCoordinator(processRunner, execute: Execute);
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        DeliveryRoleRunner runRole = (paths, config, repository, beadId, role, actor, expectedState, token, progress) =>
            retries.RunAsync(paths, config, repository, beadId, role, actor, token, progress, expectedState);
        DeliveryPoolSlotRunner slotRunner = (paths, config, repository, beadId, token) =>
            new DeliverySlotCoordinator(processRunner, runRole).RunAsync(paths, config, repository, beadId, token);
        var pool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            runSlot: slotRunner, stopGate: stopGate, slotLifetimeToken: slotLifetime.Token);
        var noTicks = Channel.CreateUnbounded<bool>();
        var runtime = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                async token =>
                {
                    var evaluation = await pool.EvaluateAsync(token).ConfigureAwait(false);
                    evaluations.Writer.TryWrite(evaluation);
                    return evaluation.Diagnostics;
                },
                pool.CompletionReader,
                slotLifetime.Cancel,
                async token => { await pool.WaitForActiveSlotsAsync(token).ConfigureAwait(false); },
                pool.WaitForCompletionAccountingAsync)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTimerTicksAsync(noTicks.Reader, token),
            stopGate: stopGate);
        var runtimeTask = runtime.RunAsync(gracefulStop.Token, forceStop.Token);

        try
        {
            var evaluation = await evaluations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(2, evaluation.Admitted.Count);
            await Task.WhenAll(
                WaitForFileAsync(gates[failedBead].Started).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken),
                WaitForFileAsync(gates[healthyBead].Started).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            using var healthyAgent = Process.GetProcessById(ReadFixtureProcessId(gates[healthyBead].Pid));

            gracefulStop.Cancel();
            File.WriteAllText(gates[failedBead].Release, "finish with a native Agent failure", new UTF8Encoding(false));
            var failedCompletion = await Assert.Single(evaluation.Admitted, admission => admission.BeadId == failedBead)
                .Completion.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            Assert.Equal(DeliverySlotDisposition.Failed, failedCompletion.Outcome?.Disposition);
            Assert.False(runtimeTask.IsCompleted, "The healthy native slot must continue draining after its peer fails finalization.");
            Assert.False(healthyAgent.HasExited, "The unaffected slot must remain active until its own Agent turn is released.");

            File.WriteAllText(gates[healthyBead].Release, "finish the unaffected native turn", new UTF8Encoding(false));
            Assert.False(await runtimeTask.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken));
            Assert.True(Volatile.Read(ref executionCount) == 2, "No next role or retry Agent may start after first stop.");

            var failed = target.ReadBead(failedBead);
            Assert.Equal("in_progress", failed.Status);
            Assert.Equal(project.RecoveryActor, failed.Assignee);
            Assert.Equal("stage:implement", BeadWorkflowState.Parse(failed).Stage);
            Assert.True(File.Exists(Path.Combine(target.WorktreePath(failedBead), "unfinished-after-stop.txt")));
            var healthy = target.ReadBead(healthyBead);
            Assert.Equal("open", healthy.Status);
            Assert.Null(healthy.Assignee);
            Assert.Equal("stage:review", BeadWorkflowState.Parse(healthy).Stage);
            AssertFixtureExited(gates[failedBead].Pid);
            AssertFixtureExited(gates[healthyBead].Pid);
        }
        finally
        {
            foreach (var gate in gates.Values)
                File.WriteAllText(gate.Release, "test cleanup", new UTF8Encoding(false));
            if (!runtimeTask.IsCompleted) gracefulStop.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
        }
    }

    [Fact]
    public async Task FirstStopKeepsNativeCurrentTurnAliveAndDoesNotLaunchNextRole()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Graceful stop current turn", "task", "stage:implement");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        var gateDirectory = Path.Combine(target.RootPath, "graceful-stop-gates");
        Directory.CreateDirectory(gateDirectory);
        var startedFile = Path.Combine(gateDirectory, "agent.started");
        var releaseFile = Path.Combine(gateDirectory, "agent.release");
        var pidFile = Path.Combine(gateDirectory, "agent.pid");
        var prepare = new AgentInvocationPreparer();
        var identities = new ConcurrentDictionary<string, AgentRole>(StringComparer.Ordinal);
        var executionCount = 0;
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        var stopGate = new ContinuousStopGate();

        async Task<PreparedAgentInvocation> Prepare(HostPaths paths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken token)
        {
            var invocation = await prepare.PrepareAsync(paths, config, role, context, worktreePath, token);
            identities[invocation.InvocationId] = role;
            return invocation;
        }

        Task<AgentExecutionResult> Execute(HostPaths paths, ProjectSlug slug, PreparedAgentInvocation invocation,
            CancellationToken token)
        {
            Interlocked.Increment(ref executionCount);
            var role = identities[invocation.InvocationId];
            var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = project.RecoveryActor,
                ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
                ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile,
                ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = role == AgentRole.Implementer ? startedFile : null,
                ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = role == AgentRole.Implementer ? releaseFile : null
            };
            return AgentInvocationExecutor.ExecuteAsync(paths, slug, invocation,
                target.CreateProcessLauncher(overrides), token);
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
            await WaitForFileAsync(startedFile).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var pidParts = (await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)).Split('|');
            using var agent = Process.GetProcessById(int.Parse(pidParts[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(agent.HasExited);

            gracefulStop.Cancel();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(agent.HasExited, "The first stop must leave the launched current turn alive to finish.");
            Assert.False(runtimeTask.IsCompleted);

            File.WriteAllText(releaseFile, "complete current turn", new UTF8Encoding(false));
            Assert.True(await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            Assert.True(agent.HasExited);
            Assert.Equal(1, Volatile.Read(ref executionCount));
            Assert.Empty(pool.ActiveReservations);

            var final = target.ReadBead(beadId);
            var state = BeadWorkflowState.Parse(final);
            Assert.Equal("open", final.Status);
            Assert.Null(final.Assignee);
            Assert.Equal("stage:review", state.Stage);
            Assert.True(File.Exists(Path.Combine(target.WorktreePath(beadId), "delivery-slot.txt")));
            Assert.True(File.Exists(Path.Combine(target.WorktreePath(beadId), ".nightweaver-ignored", "slot-sentinel.bin")));
            AssertFixtureExited(pidFile);
        }
        finally
        {
            File.WriteAllText(releaseFile, "test cleanup", new UTF8Encoding(false));
            if (!runtimeTask.IsCompleted) gracefulStop.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), CancellationToken.None);
        }
    }

    [Fact]
    public async Task FirstStopDuringPreparationReleasesClaimWithoutLaunchingNativeAgent()
    {
        await RunFirstStopDuringPreparationAsync(changeOwnerDuringPreparation: false);
    }

    [Fact]
    public async Task FirstStopDuringPreparationLeavesChangedForeignOwnerUntouchedAndFailsDrain()
    {
        await RunFirstStopDuringPreparationAsync(changeOwnerDuringPreparation: true);
    }

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

    [Fact]
    public async Task FirstStopReconcilesLostAcquireAndReleaseResponsesBeforeReportingDrainSuccess()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Lost claim responses during first stop", "task", "stage:implement");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        target.LoseDeliveryAcquireResponseAfterCommitOnce = true;
        target.LoseDeliveryReleaseResponseAfterCommitOnce = true;
        target.DeliveryAcquireCommitted = NewSignal();
        target.ContinueAfterDeliveryAcquireCommit = NewSignal();
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        var stopGate = new ContinuousStopGate();
        var runtime = CreateClaimBoundaryRuntime(target, project, processRunner, stopGate, forceStop.Token, out var pool);
        var runtimeTask = runtime.RunAsync(gracefulStop.Token, forceStop.Token);

        try
        {
            await target.DeliveryAcquireCommitted!.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            gracefulStop.Cancel();
            target.ContinueAfterDeliveryAcquireCommit!.TrySetResult(true);

            Assert.True(await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            Assert.Empty(pool.ActiveReservations);
            var final = target.ReadBead(beadId);
            Assert.Equal("open", final.Status);
            Assert.Null(final.Assignee);
            Assert.Equal("stage:implement", BeadWorkflowState.Parse(final).Stage);
        }
        finally
        {
            target.ContinueAfterDeliveryAcquireCommit!.TrySetResult(true);
            if (!runtimeTask.IsCompleted) gracefulStop.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnreadableRereadAfterLostAcquireResponseFailsDrainAndRetainsClaimForRecovery()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Unresolved claim response during first stop", "task", "stage:implement");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        target.LoseDeliveryAcquireResponseAfterCommitOnce = true;
        target.FailOwnershipRereadAfterLostDeliveryAcquireOnce = true;
        target.DeliveryAcquireCommitted = NewSignal();
        target.ContinueAfterDeliveryAcquireCommit = NewSignal();
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        var stopGate = new ContinuousStopGate();
        var runtime = CreateClaimBoundaryRuntime(target, project, processRunner, stopGate, forceStop.Token, out _);
        var runtimeTask = runtime.RunAsync(gracefulStop.Token, forceStop.Token);

        try
        {
            await target.DeliveryAcquireCommitted!.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            gracefulStop.Cancel();
            target.ContinueAfterDeliveryAcquireCommit!.TrySetResult(true);

            Assert.False(await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            var final = target.ReadBead(beadId);
            Assert.Equal("in_progress", final.Status);
            Assert.Equal(project.RecoveryActor, final.Assignee);
        }
        finally
        {
            target.ContinueAfterDeliveryAcquireCommit!.TrySetResult(true);
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
    public async Task FirstStopAfterFailedNativeTurnDoesNotResetOrRetryAndRetainsUnfinishedFiles()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Graceful stop failed current turn", "task", "stage:implement");
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1, timeoutMinutes: 5);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        var gateDirectory = Path.Combine(target.RootPath, "graceful-stop-failure-gates");
        Directory.CreateDirectory(gateDirectory);
        var startedFile = Path.Combine(gateDirectory, "agent.started");
        var releaseFile = Path.Combine(gateDirectory, "agent.release");
        var pidFile = Path.Combine(gateDirectory, "agent.pid");
        var identities = new ConcurrentDictionary<string, AgentRole>(StringComparer.Ordinal);
        var executionCount = 0;
        var prepare = new AgentInvocationPreparer();
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        var stopGate = new ContinuousStopGate();

        async Task<PreparedAgentInvocation> Prepare(HostPaths paths, ProjectConfig config, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken token)
        {
            var invocation = await prepare.PrepareAsync(paths, config, role, context, worktreePath, token);
            identities[invocation.InvocationId] = role;
            return invocation;
        }

        Task<AgentExecutionResult> Execute(HostPaths paths, ProjectSlug slug, PreparedAgentInvocation invocation,
            CancellationToken token)
        {
            Interlocked.Increment(ref executionCount);
            var role = identities[invocation.InvocationId];
            var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = project.RecoveryActor,
                ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-fail-gated",
                ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidFile,
                ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = role == AgentRole.Implementer ? startedFile : null,
                ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = role == AgentRole.Implementer ? releaseFile : null
            };
            return AgentInvocationExecutor.ExecuteAsync(paths, slug, invocation,
                target.CreateProcessLauncher(overrides), token);
        }

        var stages = new AgentStageCoordinator(processRunner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages);
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
            await WaitForFileAsync(startedFile).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var pidParts = (await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)).Split('|');
            using var agent = Process.GetProcessById(int.Parse(pidParts[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(agent.HasExited);

            gracefulStop.Cancel();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(agent.HasExited, "The first stop must not cancel the current Agent process.");
            File.WriteAllText(releaseFile, "finish with a failed result", new UTF8Encoding(false));

            Assert.False(await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            Assert.True(agent.HasExited);
            Assert.Equal(1, Volatile.Read(ref executionCount));
            Assert.Empty(pool.ActiveReservations);

            var final = target.ReadBead(beadId);
            var state = BeadWorkflowState.Parse(final);
            Assert.Equal("open", final.Status);
            Assert.Null(final.Assignee);
            Assert.Equal("stage:implement", state.Stage);
            Assert.True(File.Exists(Path.Combine(target.WorktreePath(beadId), "unfinished-after-stop.txt")));
            Assert.DoesNotContain(processRunner.Requests, request => request.FileName == "git"
                && request.Arguments.Contains("reset", StringComparer.Ordinal));
            AssertFixtureExited(pidFile);
        }
        finally
        {
            File.WriteAllText(releaseFile, "test cleanup", new UTF8Encoding(false));
            if (!runtimeTask.IsCompleted) gracefulStop.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(90), CancellationToken.None);
        }
    }

    [Fact]
    public async Task NativeRuntimeRetriesEligibilityReadOutageOnLaterTimerWithFreshInventory()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target,
            failFirstInventoryRead: true);
        using var runtimeLifetime = new CancellationTokenSource();
        var slotStarted = NewSignal();
        var ticks = Channel.CreateUnbounded<bool>();
        var error = new StringWriter();
        var pool = new DeliveryPoolCoordinator(target.Host, project, new(target.Path, "main"), processRunner,
            runSlot: async (_, _, _, _, cancellationToken) =>
            {
                slotStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new DeliverySlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome,
                    null, null, null, ClaimHandoffCompleted: false, Diagnostic: null);
            },
            slotLifetimeToken: runtimeLifetime.Token);
        var runtime = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                async cancellationToken => (await pool.EvaluateAsync(cancellationToken)).Diagnostics,
                pool.CompletionReader,
                runtimeLifetime.Cancel,
                async cancellationToken => { await pool.WaitForActiveSlotsAsync(cancellationToken); },
                pool.WaitForCompletionAccountingAsync)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            error,
            token => ReadTimerTicksAsync(ticks.Reader, token));
        var runtimeTask = runtime.RunAsync(runtimeLifetime.Token);

        try
        {
            await processRunner.FirstInventoryReadFailure.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var beadId = target.CreateBead("Created during a tracker read outage", "task", "stage:implement");
            ticks.Writer.TryWrite(true);
            await slotStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(beadId, Assert.Single(pool.ActiveReservations).BeadId);
            Assert.Equal(2, processRunner.Requests.Count(request => request.FileName == "bd"
                && request.Arguments.Contains("--all", StringComparer.Ordinal)));
            Assert.Contains("Delivery discovery failed; a later trigger will reread current tracker state", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("open", target.ReadBead(beadId).Status);
        }
        finally
        {
            runtimeLifetime.Cancel();
            await runtimeTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }

    [Fact]
    public async Task ResumedBeadWaitsForDependencyAndCapacityThenStartsOnceAfterPoolRestart()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows Job Objects and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var capacityBead = target.CreateBead("Delivery capacity holder", "task", "stage:implement");
        var dependency = target.CreateBead("Resumed dependency", "task");
        var resumedBead = target.CreateBead("Resumed delivery plan", "task", "stage:implement", "needs-human");
        target.RunTarget("bd", ["update", resumedBead, "--set-metadata", "repair_cycles=2"]);
        target.RunTarget("bd", ["dep", "add", resumedBead, dependency]);

        var project = target.CreateProject(AgentCli.Codex, maxConcurrentDelivery: 1);
        var processRunner = new ConcurrentRecordingProcessRunner(new SystemProcessRunner(), target);
        var repository = new ValidatedTargetRepository(target.Path, "main");
        using var slotLifetime = new CancellationTokenSource();
        var firstSlotStarted = NewSignal();
        var releaseFirstSlot = NewSignal();
        var firstPoolSlotInvocations = 0;
        var firstPool = new DeliveryPoolCoordinator(target.Host, project, repository, processRunner,
            runSlot: async (_, _, _, beadId, cancellationToken) =>
            {
                Assert.Equal(capacityBead, beadId);
                Interlocked.Increment(ref firstPoolSlotInvocations);
                firstSlotStarted.TrySetResult(true);
                await releaseFirstSlot.Task.WaitAsync(cancellationToken);
                return new DeliverySlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome,
                    null, null, null, ClaimHandoffCompleted: false, Diagnostic: null);
            },
            slotLifetimeToken: slotLifetime.Token);

        try
        {
            var initial = await firstPool.EvaluateAsync(TestContext.Current.CancellationToken);
            var heldAdmission = Assert.Single(initial.Admitted);
            Assert.Equal(capacityBead, heldAdmission.BeadId);
            await firstSlotStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var beforeResume = target.ReadBead(resumedBead);
            Assert.Equal("open", beforeResume.Status);
            Assert.Contains("needs-human", beforeResume.Labels);
            Assert.Equal(2, BeadWorkflowState.Parse(beforeResume).RepairCycles);
            Assert.Empty((await firstPool.EvaluateAsync(TestContext.Current.CancellationToken)).Admitted);
            Assert.Equal(1, firstPoolSlotInvocations);

            await new BeadsWriter(processRunner).RetryNeedsHumanAsync(repository, resumedBead,
                TestContext.Current.CancellationToken);
            var afterResume = target.ReadBead(resumedBead);
            Assert.Equal("open", afterResume.Status);
            Assert.Contains("stage:implement", afterResume.Labels);
            Assert.DoesNotContain("needs-human", afterResume.Labels);
            Assert.DoesNotContain("product-decision-required", afterResume.Labels);
            Assert.Equal(0, BeadWorkflowState.Parse(afterResume).RepairCycles);
            Assert.False(afterResume.Payload.TryGetProperty("metadata", out var afterResumeMetadata)
                && afterResumeMetadata.TryGetProperty("repair_cycles", out _));

            var dependencyWait = await firstPool.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Empty(dependencyWait.Admitted);
            Assert.Contains(dependencyWait.Diagnostics, diagnostic => diagnostic.BeadId == resumedBead
                && diagnostic.Code == DeliveryPoolDiagnosticCode.NotEligible);
            Assert.Equal(1, firstPoolSlotInvocations);

            target.RunTarget("bd", ["update", dependency, "--status", "closed"]);
            var capacityWait = await firstPool.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Empty(capacityWait.Admitted);
            Assert.Contains(capacityWait.Diagnostics, diagnostic => diagnostic.BeadId == resumedBead
                && diagnostic.Code == DeliveryPoolDiagnosticCode.CapacityReached);
            Assert.Equal(capacityBead, Assert.Single(firstPool.ActiveReservations).BeadId);

            releaseFirstSlot.TrySetResult(true);
            await heldAdmission.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Empty(firstPool.ActiveReservations);
            target.RunTarget("bd", ["update", capacityBead, "--status", "closed"]);

            // A fresh pool represents runtime restart: it rereads the persisted resumed state,
            // passes the ordinary guarded claim, and does not replay the same open Bead twice.
            var launchCount = 0;
            var restartedPool = new DeliveryPoolCoordinator(target.Host, project, repository, processRunner,
                runSlot: async (_, config, targetRepository, beadId, cancellationToken) =>
                {
                    await new BeadsWriter(processRunner).AcquireDispatchClaimAsync(targetRepository, beadId,
                        config.RecoveryActor, cancellationToken);
                    Interlocked.Increment(ref launchCount);
                    return new DeliverySlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome,
                        null, null, null, ClaimHandoffCompleted: false, Diagnostic: null);
                },
                slotLifetimeToken: slotLifetime.Token);

            var resumedAdmission = await restartedPool.EvaluateAsync(TestContext.Current.CancellationToken);
            var admission = Assert.Single(resumedAdmission.Admitted);
            Assert.Equal(resumedBead, admission.BeadId);
            await admission.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var claimed = target.ReadBead(resumedBead);
            Assert.Equal("in_progress", claimed.Status);
            Assert.Equal(project.RecoveryActor, claimed.Assignee);
            Assert.Equal(1, launchCount);

            var duplicateEvaluation = await restartedPool.EvaluateAsync(TestContext.Current.CancellationToken);
            Assert.Empty(duplicateEvaluation.Admitted);
            Assert.Equal(1, launchCount);
        }
        finally
        {
            releaseFirstSlot.TrySetResult(true);
            await firstPool.WaitForActiveSlotsAsync(CancellationToken.None);
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

    private sealed record PoolStageObservation(string BeadId, AgentRole Role, string? Stage, string Status,
        string? Assignee, string WorktreePath, string Branch, string Head);

    private sealed class GatedNativeJournalClient(
        IBeadsJournalClient inner,
        TaskCompletionSource<bool> enableAttempted,
        TaskCompletionSource<bool> allowEnable,
        TaskCompletionSource<bool> enabled,
        TaskCompletionSource<long> duplicateBurstDelivered) : IBeadsJournalClient
    {
        private readonly Channel<long> observedSequences = Channel.CreateUnbounded<long>();
        private int duplicateNextEvent;

        internal void DuplicateNextEventBurst() => Interlocked.Exchange(ref duplicateNextEvent, 1);

        public async Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
        {
            enableAttempted.TrySetResult(true);
            await allowEnable.Task.WaitAsync(cancellationToken);
            await inner.EnableAsync(repository, cancellationToken);
            enabled.TrySetResult(true);
        }

        public async Task FollowAsync(ValidatedTargetRepository repository, long afterSequence,
            Action<long> onEvent, CancellationToken cancellationToken)
        {
            await inner.FollowAsync(repository, afterSequence, sequence =>
            {
                observedSequences.Writer.TryWrite(sequence);
                if (Interlocked.Exchange(ref duplicateNextEvent, 0) != 0)
                {
                    onEvent(sequence);
                    onEvent(sequence);
                    onEvent(Math.Max(0, sequence - 1));
                    duplicateBurstDelivered.TrySetResult(sequence);
                }
                else
                {
                    onEvent(sequence);
                }
            }, cancellationToken);
        }

        internal async Task WaitForObservedSequenceAsync(long expected, CancellationToken cancellationToken)
        {
            var greatest = 0L;
            while (greatest < expected)
                greatest = Math.Max(greatest, await observedSequences.Reader.ReadAsync(cancellationToken));
        }
    }

    private static Process StartNativeJournalTail(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Environment)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the native Delivery journal tail fixture.");
        }
        process.StandardInput.Close();
        return process;
    }

    private static async Task<long> ReadNativeJournalHeadAsync(
        IProcessRunner processRunner, string repositoryPath, CancellationToken cancellationToken)
    {
        var tail = BeadsCommand.CreateJournalTail(repositoryPath, 0);
        var result = await processRunner.RunAsync(new ProcessRequest("bd",
            ["--readonly", "--sandbox", "events", "tail", "--json"], repositoryPath,
            tail.Environment, TimeSpan.FromSeconds(30)), cancellationToken);
        Assert.Equal(0, result.ExitCode);
        var head = 0L;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            using var record = JsonDocument.Parse(line);
            head = Math.Max(head, record.RootElement.GetProperty("seq").GetInt64());
        }
        return head;
    }

    private static async Task DrainEvaluationsUntilQuietAsync(
        ChannelReader<DeliveryPoolEvaluation> evaluations, TimeSpan quietPeriod, CancellationToken cancellationToken)
    {
        while (true)
        {
            while (evaluations.TryRead(out _)) { }
            var available = evaluations.WaitToReadAsync(cancellationToken).AsTask();
            var quiet = Task.Delay(quietPeriod, cancellationToken);
            if (await Task.WhenAny(available, quiet) == quiet) return;
            if (!await available) return;
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
