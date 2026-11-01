using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerPoolCoordinatorTests
{
    [Fact]
    public async Task EvaluationAdmitsOnlyNativeReadyMergeTasksInPriorityOrder()
    {
        var releaseSlot = NewSignal();
        var rows = new[]
        {
            Row("later-priority", priority: 1, createdAt: "2026-01-02T00:00:00Z", labels: ["stage:merge"]),
            Row("priority-first", priority: 0, createdAt: "2026-01-03T00:00:00Z", labels: ["stage:merge"]),
            Row("blocked", status: "blocked", labels: ["stage:merge"]),
            Row("not-ready", labels: ["stage:merge"]),
            Row("foreign-owned", status: "in_progress", assignee: "human", labels: ["stage:merge"]),
            Row("recoverable-owned", status: "in_progress", assignee: "nightweaver:merger-pool-tests", labels: ["stage:merge"]),
            Row("paused", labels: ["stage:merge", "needs-human"]),
            Row("planned", labels: ["stage:merge", "planned"]),
            Row("malformed", labels: ["stage:merge", "stage:qa"]),
            Row("not-a-task", issueType: "feature", labels: ["stage:merge"]),
            Row("other-stage", labels: ["stage:review"])
        };
        var ready = new[] { "later-priority", "priority-first", "blocked", "not-ready", "foreign-owned", "recoverable-owned", "paused", "planned", "malformed", "not-a-task", "other-stage" };
        var pool = CreatePool(() => Task.FromResult(Snapshot(rows,
                ["later-priority", "priority-first", "blocked", "paused", "planned", "malformed", "not-a-task", "other-stage"],
                ["recoverable-owned"])),
            async (_, _, _, _, agentAttemptStarted, _) =>
            {
                agentAttemptStarted();
                await releaseSlot.Task;
                return Outcome(MergerSlotDisposition.Merged, handoff: true);
            });

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var admission = Assert.Single(evaluation.Admitted);
        Assert.Equal("priority-first", admission.BeadId);
        Assert.Equal(1, evaluation.CapacityLimit);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "later-priority" && item.Code == MergerPoolDiagnosticCode.CapacityReached);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "blocked" && item.Code == MergerPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "recoverable-owned" && item.Code == MergerPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "paused" && item.Code == MergerPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "planned" && item.Code == MergerPoolDiagnosticCode.InvalidWorkflowState);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "malformed" && item.Code == MergerPoolDiagnosticCode.InvalidWorkflowState);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "not-a-task" && item.Code == MergerPoolDiagnosticCode.NotPlanBead);
        Assert.DoesNotContain(evaluation.Admitted, item => ready.Contains(item.BeadId, StringComparer.Ordinal)
            && item.BeadId != "priority-first");
        releaseSlot.TrySetResult(true);
        Assert.True((await admission.Completion).MergeCompleted);
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task EqualPriorityCandidatesUseFifoAndTheReservationRemainsCapacityOne()
    {
        var first = Row("first", priority: 1, createdAt: "2026-01-01T00:00:00Z", labels: ["stage:merge"]);
        var second = Row("second", priority: 1, createdAt: "2026-01-02T00:00:00Z", labels: ["stage:merge"]);
        var release = NewSignal();
        var started = NewSignal();
        var reads = 0;
        var pool = CreatePool(() =>
        {
            var call = Interlocked.Increment(ref reads);
            var rows = call <= 3 ? new[] { second, first } : [second];
            return Task.FromResult(Snapshot(rows, ["first", "second"]));
        }, async (_, _, _, _, agentAttemptStarted, _) =>
        {
            agentAttemptStarted();
            started.TrySetResult(true);
            await release.Task;
            return Outcome(MergerSlotDisposition.Merged, handoff: true);
        }, maxConcurrentDelivery: 3);

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, evaluation.CapacityLimit);
        Assert.Equal("first", Assert.Single(evaluation.Admitted).BeadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var concurrent = await Task.WhenAll(
            pool.EvaluateAsync(TestContext.Current.CancellationToken),
            pool.EvaluateAsync(TestContext.Current.CancellationToken));
        Assert.All(concurrent, result => Assert.Empty(result.Admitted));
        Assert.All(concurrent, result => Assert.Contains(result.Diagnostics, item =>
            item.BeadId == "first" && item.Code == MergerPoolDiagnosticCode.AlreadyReserved));
        Assert.All(concurrent, result => Assert.Contains(result.Diagnostics, item =>
            item.BeadId == "second" && item.Code == MergerPoolDiagnosticCode.CapacityReached));

        release.TrySetResult(true);
        await evaluation.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(pool.ActiveReservations);
        var next = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal("second", Assert.Single(next.Admitted).BeadId);
        await next.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExceptionsAndCancellationPublishCompletionAndReleaseTheMergerSlot()
    {
        var calls = 0;
        var row = Row("candidate", labels: ["stage:merge"]);
        var pool = CreatePool(() => Task.FromResult(Snapshot([row], ["candidate"])), (_, _, _, _, agentAttemptStarted, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call >= 3) agentAttemptStarted();
            return call switch
            {
                1 => throw new InvalidOperationException("controlled Merger failure"),
                2 => throw new OperationCanceledException("controlled Merger cancellation", new CancellationToken(canceled: true)),
                _ => Task.FromResult(Outcome(MergerSlotDisposition.Merged, handoff: true))
            };
        });

        var failed = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var failure = await Assert.Single(failed.Admitted).Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.True(failure.LocalCapacityReleased);
        Assert.Empty(pool.ActiveReservations);

        var canceled = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var cancellation = await Assert.Single(canceled.Admitted).Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(cancellation.IsCanceled);
        Assert.True(cancellation.LocalCapacityReleased);
        Assert.Empty(pool.ActiveReservations);

        var completed = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.True((await Assert.Single(completed.Admitted).Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).MergeCompleted);
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task PreLaunchRefusalAdvancesToNextCandidateInSameEvaluation()
    {
        var rows = new[]
        {
            Row("first", priority: 0, labels: ["stage:merge"]),
            Row("second", priority: 1, labels: ["stage:merge"])
        };
        var firstEntered = NewSignal();
        var releaseRefusal = NewSignal();
        var secondAttemptStarted = NewSignal();
        var pool = CreatePool(() => Task.FromResult(Snapshot(rows, ["first", "second"])),
            async (_, _, _, beadId, agentAttemptStarted, _) =>
            {
                if (beadId == "first")
                {
                    firstEntered.TrySetResult(true);
                    await releaseRefusal.Task;
                    return Outcome(MergerSlotDisposition.Refused);
                }

                agentAttemptStarted();
                secondAttemptStarted.TrySetResult(true);
                return Outcome(MergerSlotDisposition.Merged, handoff: true);
            });

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(evaluationTask.IsCompleted);

        releaseRefusal.TrySetResult(true);
        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "first" && item.Code == MergerPoolDiagnosticCode.SlotRefused);
        Assert.Equal(MergerSlotDisposition.Refused,
            (await evaluation.Admitted.Single(admission => admission.BeadId == "first").Completion).Outcome?.Disposition);
        Assert.True((await evaluation.Admitted.Single(admission => admission.BeadId == "second").Completion).MergeCompleted);
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task PreLaunchExceptionAdvancesToNextCandidateAndReportsSlotEnded()
    {
        var rows = new[]
        {
            Row("first", priority: 0, labels: ["stage:merge"]),
            Row("second", priority: 1, labels: ["stage:merge"])
        };
        var firstEntered = NewSignal();
        var releaseFailure = NewSignal();
        var secondAttemptStarted = NewSignal();
        var pool = CreatePool(() => Task.FromResult(Snapshot(rows, ["first", "second"])),
            async (_, _, _, beadId, agentAttemptStarted, _) =>
            {
                if (beadId == "first")
                {
                    firstEntered.TrySetResult(true);
                    await releaseFailure.Task;
                    throw new InvalidOperationException("controlled prelaunch failure");
                }

                agentAttemptStarted();
                secondAttemptStarted.TrySetResult(true);
                return Outcome(MergerSlotDisposition.Merged, handoff: true);
            });

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(evaluationTask.IsCompleted);

        releaseFailure.TrySetResult(true);
        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "first" && item.Code == MergerPoolDiagnosticCode.SlotEnded);
        Assert.IsType<InvalidOperationException>(
            (await evaluation.Admitted.Single(admission => admission.BeadId == "first").Completion).Exception);
        Assert.True((await evaluation.Admitted.Single(admission => admission.BeadId == "second").Completion).MergeCompleted);
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task AgentAttemptSignalKeepsCiWaitAsynchronousWhileTheSlotRemainsActive()
    {
        var rows = new[]
        {
            Row("ci-wait", priority: 0, labels: ["stage:merge"]),
            Row("queued", priority: 1, labels: ["stage:merge"])
        };
        var ciStarted = NewSignal();
        var releaseCi = NewSignal();
        var pool = CreatePool(() => Task.FromResult(Snapshot(rows, ["ci-wait", "queued"])),
            async (_, _, _, beadId, agentAttemptStarted, _) =>
            {
                Assert.Equal("ci-wait", beadId);
                agentAttemptStarted();
                ciStarted.TrySetResult(true);
                await releaseCi.Task;
                return Outcome(MergerSlotDisposition.Merged, handoff: true);
            });

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await ciStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["ci-wait"], evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "queued" && item.Code == MergerPoolDiagnosticCode.CapacityReached);
        Assert.False(evaluation.Admitted[0].Completion.IsCompleted);

        Assert.False(evaluation.Admitted[0].Completion.IsCompleted);
        releaseCi.TrySetResult(true);
        Assert.True((await evaluation.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).MergeCompleted);
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task EvaluationCancellationWhileInitialAdmissionIsPendingLeavesSlotOwnedByProjectLifetime()
    {
        var rows = new[]
        {
            Row("slow-refusal", priority: 0, labels: ["stage:merge"]),
            Row("next", priority: 1, labels: ["stage:merge"])
        };
        var entered = NewSignal();
        var release = NewSignal();
        var pool = CreatePool(() => Task.FromResult(Snapshot(rows, ["slow-refusal", "next"])),
            async (_, _, _, beadId, _, _) =>
            {
                Assert.Equal("slow-refusal", beadId);
                entered.TrySetResult(true);
                await release.Task;
                return Outcome(MergerSlotDisposition.Refused);
            });

        using var cancellation = new CancellationTokenSource();
        var evaluationTask = pool.EvaluateAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluationTask);

        var active = Assert.Single(pool.ActiveReservations);
        release.TrySetResult(true);
        var completion = await active.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(completion.LocalCapacityReleased);
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task DeliveryAndMergerSlotsUseIndependentProjectCapacitiesAcrossHeldMergeWait()
    {
        var host = new HostPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cross-pool-host")));
        var target = new ValidatedTargetRepository(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cross-pool-target")), "main");
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        var project = new ProjectConfig(ProjectSlug.Parse("cross-pool-tests"), "cross-pool-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(1, 2, 1), new LimitsConfig(3, 2), new AgentSettings(AgentCli.Codex, null, "low", 1),
            new RoleSettings(roles));
        var processRunner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var deliveryRows = new[]
        {
            Row("delivery-a", priority: 0, labels: ["stage:implement"]),
            Row("delivery-b", priority: 1, labels: ["stage:implement"]),
            Row("delivery-c", priority: 2, labels: ["stage:implement"])
        };
        MergerPoolSnapshot mergerSnapshot = Snapshot([Row("merge", labels: ["stage:merge"])], ["merge"]);
        DeliveryPoolSnapshot deliverySnapshot = DeliverySnapshot(deliveryRows, ["delivery-a", "delivery-b", "delivery-c"]);
        var deliveryGates = deliveryRows.ToDictionary(
            row => ReadId(row),
            _ => (Started: NewSignal(), Release: NewSignal()), StringComparer.Ordinal);
        var mergerStarted = NewSignal();
        var releaseMerger = NewSignal();

        var deliveryPool = new DeliveryPoolCoordinator(host, project, target, processRunner,
            (_, _, _) => Task.FromResult(deliverySnapshot),
            async (_, _, _, beadId, _, agentAttemptStarted) =>
            {
                agentAttemptStarted();
                var gate = deliveryGates[beadId];
                gate.Started.TrySetResult(true);
                await gate.Release.Task;
                return new DeliverySlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome,
                    null, AgentRole.Implementer, null, false, "controlled capacity test");
            });
        var mergerPool = new MergerPoolCoordinator(host, project, target, processRunner,
            (_, _, _) => Task.FromResult(mergerSnapshot),
            async (_, _, _, beadId, agentAttemptStarted, _) =>
            {
                Assert.Equal("merge", beadId);
                agentAttemptStarted();
                mergerStarted.TrySetResult(true);
                await releaseMerger.Task;
                return Outcome(MergerSlotDisposition.StoppedAtUnsupportedOutcome);
            });

        var fullDelivery = await deliveryPool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["delivery-a", "delivery-b"], fullDelivery.Admitted.Select(admission => admission.BeadId));
        await Task.WhenAll(fullDelivery.Admitted.Select(admission =>
            deliveryGates[admission.BeadId].Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Equal(2, deliveryPool.ActiveReservations.Count);

        var mergerWhileDeliveryFull = await mergerPool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal("merge", Assert.Single(mergerWhileDeliveryFull.Admitted).BeadId);
        await mergerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(mergerWhileDeliveryFull.Admitted[0].Completion.IsCompleted);

        deliverySnapshot = DeliverySnapshot(
            [Row("delivery-a", status: "closed", priority: 0, labels: ["stage:implement"]), deliveryRows[1], deliveryRows[2]],
            ["delivery-b", "delivery-c"]);
        deliveryGates["delivery-a"].Release.TrySetResult(true);
        await fullDelivery.Admitted.Single(admission => admission.BeadId == "delivery-a").Completion
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var deliveryWhileMergerHeld = await deliveryPool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["delivery-c"], deliveryWhileMergerHeld.Admitted.Select(admission => admission.BeadId));
        await deliveryGates["delivery-c"].Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(mergerWhileDeliveryFull.Admitted[0].Completion.IsCompleted);

        releaseMerger.TrySetResult(true);
        deliveryGates["delivery-b"].Release.TrySetResult(true);
        deliveryGates["delivery-c"].Release.TrySetResult(true);
        await Task.WhenAll(mergerWhileDeliveryFull.Admitted[0].Completion,
            fullDelivery.Admitted.Single(admission => admission.BeadId == "delivery-b").Completion,
            deliveryWhileMergerHeld.Admitted[0].Completion);
        Assert.Empty(mergerPool.ActiveReservations);
        Assert.Empty(deliveryPool.ActiveReservations);
    }

    private static MergerPoolCoordinator CreatePool(Func<Task<MergerPoolSnapshot>> snapshotReader,
        MergerPoolSlotRunner slotRunner, int maxConcurrentDelivery = 1)
    {
        var host = new HostPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-pool-host")));
        var target = new ValidatedTargetRepository(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-pool-target")), "main");
        var project = CreateProject(target, maxConcurrentDelivery);
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        return new MergerPoolCoordinator(host, project, target, runner,
            (_, _, _) => snapshotReader(), slotRunner);
    }

    private static ProjectConfig CreateProject(ValidatedTargetRepository target, int maxConcurrentDelivery)
    {
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        return new ProjectConfig(ProjectSlug.Parse("merger-pool-tests"), "merger-pool-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(1, maxConcurrentDelivery, 1), new LimitsConfig(3, 2),
            new AgentSettings(AgentCli.Codex, null, "low", 1), new RoleSettings(roles));
    }

    private static MergerPoolSnapshot Snapshot(IEnumerable<string> rows, IEnumerable<string> nativeReady,
        IEnumerable<string>? recoverableOwnedReady = null) =>
        new(BeadsReader.Parse("[" + string.Join(',', rows) + "]"), BeadsReadiness.Create(nativeReady, recoverableOwnedReady ?? []));

    private static DeliveryPoolSnapshot DeliverySnapshot(IEnumerable<string> rows, IEnumerable<string> nativeReady) =>
        new(BeadsReader.Parse("[" + string.Join(',', rows) + "]"), BeadsReadiness.Create(nativeReady, []));

    private static string ReadId(string row)
    {
        using var document = JsonDocument.Parse(row);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static string Row(string id, string status = "open", string issueType = "task", int priority = 1,
        string createdAt = "2026-01-01T00:00:00Z", string? assignee = null, string[]? labels = null)
        => JsonSerializer.Serialize(new
        {
            id,
            status,
            issue_type = issueType,
            priority,
            created_at = createdAt,
            assignee,
            labels = labels ?? [],
            dependencies = Array.Empty<object>(),
            metadata = new { repair_cycles = 0 }
        });

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MergerSlotOutcome Outcome(MergerSlotDisposition disposition, bool handoff = false) =>
        new(disposition, null, null, handoff, $"{disposition} outcome");
}
