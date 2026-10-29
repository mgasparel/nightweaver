using System.Collections.Concurrent;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DeliveryPoolCoordinatorTests
{
    [Fact]
    public async Task EvaluateUsesFreshNativeReadinessAndRanksOnlyValidDeliveryCandidates()
    {
        var rows = new[]
        {
            Row("priority-first", priority: 0, createdAt: "2026-02-01T00:00:00Z", labels: ["stage:implement", "ready-for-agent"]),
            Row("task-10", priority: 1, createdAt: "2026-01-01T00:00:00Z", labels: ["stage:review"]),
            Row("task-2", priority: 1, createdAt: "2026-01-01T05:00:00+05:00", labels: ["stage:qa"]),
            Row("later", priority: 1, createdAt: "2026-01-02T00:00:00Z", labels: ["unrelated"]),
            Row("provenance", priority: 1, createdAt: "2026-01-03T00:00:00Z", labels: [], discoveredFrom: "product-1"),
            Row("merge", priority: 0, createdAt: "2026-01-04T00:00:00Z", labels: ["stage:merge"]),
            Row("planned", priority: 0, createdAt: "2026-01-05T00:00:00Z", labels: ["planned"]),
            Row("paused-decision", priority: 0, createdAt: "2026-01-06T00:00:00Z", labels: ["product-decision-required"]),
            Row("paused-human", priority: 0, createdAt: "2026-01-07T00:00:00Z", labels: ["needs-human"]),
            Row("planning-stage", priority: 0, createdAt: "2026-01-08T00:00:00Z", labels: ["stage:plan"]),
            Row("multiple-stages", priority: 0, createdAt: "2026-01-09T00:00:00Z", labels: ["stage:review", "stage:qa"]),
            Row("unrecognized-stage", priority: 0, createdAt: "2026-01-09T00:00:00Z", labels: ["stage:future"]),
            Row("invalid-metadata", priority: 0, createdAt: "2026-01-09T00:00:00Z", labels: [], repairCycles: -1),
            Row("not-ready", priority: 0, createdAt: "2026-01-10T00:00:00Z", labels: ["stage:implement"]),
            Row("assigned", priority: 0, createdAt: "2026-01-11T00:00:00Z", assignee: "human", labels: ["stage:qa"]),
            Row("closed", status: "closed", priority: 0, createdAt: "2026-01-12T00:00:00Z", labels: []),
            Row("blocked", status: "blocked", priority: 0, createdAt: "2026-01-12T00:00:00Z", labels: []),
            Row("deferred", status: "deferred", priority: 0, createdAt: "2026-01-12T00:00:00Z", labels: []),
            Row("product", issueType: "feature", priority: 0, createdAt: "2026-01-13T00:00:00Z", labels: [])
        };
        var ready = new[] { "priority-first", "task-10", "task-2", "later", "provenance", "merge", "planned",
            "paused-decision", "paused-human", "planning-stage", "multiple-stages", "unrecognized-stage", "invalid-metadata",
            "assigned", "closed", "blocked", "deferred", "product" };
        var entered = new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        var release = NewSignal();
        var pool = CreatePool(10, () => Task.FromResult(Snapshot(rows, ready)), async (_, _, _, beadId, _, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            entered.GetOrAdd(beadId, _ => NewSignal()).TrySetResult(true);
            await release.Task;
            return SlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome);
        });

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["priority-first", "task-10", "task-2", "later", "provenance"],
            evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Equal(10, evaluation.CapacityLimit);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "merge" && item.Code == DeliveryPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "planned" && item.Code == DeliveryPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "not-ready" && item.Code == DeliveryPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "product" && item.Code == DeliveryPoolDiagnosticCode.NotPlanBead);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "planning-stage" && item.Code == DeliveryPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "multiple-stages" && item.Code == DeliveryPoolDiagnosticCode.InvalidWorkflowState);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "unrecognized-stage" && item.Code == DeliveryPoolDiagnosticCode.InvalidWorkflowState);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "invalid-metadata" && item.Code == DeliveryPoolDiagnosticCode.InvalidWorkflowState);

        await Task.WhenAll(evaluation.Admitted.Select(admission => entered.GetOrAdd(admission.BeadId, _ => NewSignal()).Task));
        release.TrySetResult(true);
        await Task.WhenAll(evaluation.Admitted.Select(admission => admission.Completion));
        Assert.Empty(pool.ActiveReservations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReservationsNeverExceedConfiguredCapacity(int capacity)
    {
        if (capacity == ProjectConfigDefaults.MaxConcurrentDelivery)
            Assert.Equal(3, ProjectConfigDefaults.MaxConcurrentDelivery);

        var rows = Enumerable.Range(0, 5)
            .Select(index => Row($"task-{index}", priority: index, createdAt: "2026-01-01T00:00:00Z", labels: []))
            .ToArray();
        var release = NewSignal();
        var entered = NewSignal();
        var enteredCount = 0;
        var pool = CreatePool(capacity, () => Task.FromResult(Snapshot(rows, rows.Select(Id))), async (_, _, _, _, _, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            if (Interlocked.Increment(ref enteredCount) == capacity) entered.TrySetResult(true);
            await release.Task;
            return SlotOutcome(DeliverySlotDisposition.Refused);
        });

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(capacity, evaluation.Admitted.Count);
        Assert.Equal(capacity, pool.ActiveReservations.Count);
        var second = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(second.Admitted);
        Assert.Contains(second.Diagnostics, item => item.Code == DeliveryPoolDiagnosticCode.CapacityReached);

        release.TrySetResult(true);
        await Task.WhenAll(evaluation.Admitted.Select(admission => admission.Completion));
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task PreLaunchRefusalFreesCapacityForNextCandidateInSameEvaluation()
    {
        var rows = new[]
        {
            Row("first", priority: 0, labels: []),
            Row("second", priority: 1, labels: [])
        };
        var firstRunnerEntered = NewSignal();
        var releaseFirstRefusal = NewSignal();
        var secondAttemptStarted = NewSignal();
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var pool = CreatePool(1, () => Task.FromResult(Snapshot(rows, ["first", "second"])), async (_, _, _, beadId, _, agentAttemptStarted) =>
        {
            calls.AddOrUpdate(beadId, 1, static (_, count) => count + 1);
            if (beadId == "first")
            {
                firstRunnerEntered.TrySetResult(true);
                await releaseFirstRefusal.Task;
                return SlotOutcome(DeliverySlotDisposition.Refused);
            }

            agentAttemptStarted();
            secondAttemptStarted.TrySetResult(true);
            return SlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome);
        });

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await firstRunnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(evaluationTask.IsCompleted);

        releaseFirstRefusal.TrySetResult(true);
        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Equal(1, calls["first"]);
        Assert.Equal(1, calls["second"]);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "first"
            && item.Code == DeliveryPoolDiagnosticCode.SlotRefusedBeforeLaunch);
        Assert.Equal(DeliverySlotDisposition.Refused,
            (await evaluation.Admitted.Single(admission => admission.BeadId == "first").Completion).Outcome?.Disposition);
        await evaluation.Admitted.Single(admission => admission.BeadId == "second").Completion;
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task ActiveSlotCompletionWakesEvaluationWaitingForSlowAdmission()
    {
        var rows = new[]
        {
            Row("a-slow-admission", priority: 0, labels: []),
            Row("b-fast-slot", priority: 1, labels: []),
            Row("c-waiting", priority: 2, labels: [])
        };
        var aRunnerEntered = NewSignal();
        var allowAStart = NewSignal();
        var releaseA = NewSignal();
        var bStarted = NewSignal();
        var releaseB = NewSignal();
        var cStarted = NewSignal();
        var releaseC = NewSignal();
        var pool = CreatePool(2, () => Task.FromResult(Snapshot(rows, ["a-slow-admission", "b-fast-slot", "c-waiting"])),
            async (_, _, _, beadId, _, agentAttemptStarted) =>
            {
                if (beadId == "a-slow-admission")
                {
                    aRunnerEntered.TrySetResult(true);
                    await allowAStart.Task;
                    agentAttemptStarted();
                    await releaseA.Task;
                }
                else if (beadId == "b-fast-slot")
                {
                    agentAttemptStarted();
                    bStarted.TrySetResult(true);
                    await releaseB.Task;
                }
                else
                {
                    agentAttemptStarted();
                    cStarted.TrySetResult(true);
                    await releaseC.Task;
                }

                return SlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome);
            });

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await aRunnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(evaluationTask.IsCompleted);
        Assert.False(cStarted.Task.IsCompleted);

        releaseB.TrySetResult(true);
        await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["a-slow-admission", "b-fast-slot", "c-waiting"], evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Equal(["a-slow-admission", "c-waiting"], pool.ActiveReservations.Select(reservation => reservation.BeadId));

        allowAStart.TrySetResult(true);
        releaseA.TrySetResult(true);
        releaseC.TrySetResult(true);
        await Task.WhenAll(evaluation.Admitted.Select(admission => admission.Completion));
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task OverlappingEvaluationsUseFreshReadsAndCannotReserveTheSameBeadTwice()
    {
        var candidate = Row("same", labels: ["stage:review"]);
        var snapshot = Snapshot([candidate], ["same"]);
        var firstReadStarted = NewSignal();
        var allowFirstRead = NewSignal();
        var readCount = 0;
        var runnerStarted = NewSignal();
        var releaseRunner = NewSignal();
        var runnerCount = 0;
        var pool = CreatePool(1, async () =>
        {
            var call = Interlocked.Increment(ref readCount);
            if (call == 1)
            {
                firstReadStarted.TrySetResult(true);
                await allowFirstRead.Task;
            }
            return snapshot;
        }, async (_, _, _, _, token, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            Interlocked.Increment(ref runnerCount);
            runnerStarted.TrySetResult(true);
            await releaseRunner.Task;
            return SlotOutcome(DeliverySlotDisposition.Refused);
        });

        var firstTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var secondTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        allowFirstRead.TrySetResult(true);
        var evaluations = await Task.WhenAll(firstTask, secondTask);
        await runnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, readCount);
        Assert.Equal(1, evaluations.Sum(evaluation => evaluation.Admitted.Count));
        Assert.Contains(evaluations.SelectMany(evaluation => evaluation.Diagnostics),
            item => item.BeadId == "same" && item.Code == DeliveryPoolDiagnosticCode.AlreadyReserved);
        Assert.Equal(1, runnerCount);

        releaseRunner.TrySetResult(true);
        await Task.WhenAll(evaluations.SelectMany(evaluation => evaluation.Admitted).Select(admission => admission.Completion));
    }

    [Fact]
    public async Task CompletionReleasesCapacityBeforePublicationAndAllowsReplacementWhileAnotherSlotRuns()
    {
        var current = Snapshot(
        [
            Row("a", priority: 0, labels: ["stage:implement"]),
            Row("b", priority: 1, labels: ["stage:implement"]),
            Row("c", priority: 2, labels: ["stage:implement"])
        ], ["a", "b", "c"]);
        var gates = new ConcurrentDictionary<string, (TaskCompletionSource<bool> Started, TaskCompletionSource<bool> Release)>(StringComparer.Ordinal);
        var pool = CreatePool(2, () => Task.FromResult(current), async (_, _, _, beadId, _, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            var gate = gates.GetOrAdd(beadId, _ => (NewSignal(), NewSignal()));
            gate.Started.TrySetResult(true);
            await gate.Release.Task;
            return beadId == "b"
                ? SlotOutcome(DeliverySlotDisposition.MergeReady, handoff: true)
                : SlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome);
        });

        var first = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], first.Admitted.Select(admission => admission.BeadId));
        await Task.WhenAll(first.Admitted.Select(admission => gates.GetOrAdd(admission.BeadId, _ => (NewSignal(), NewSignal())).Started.Task));

        current = Snapshot(
        [
            Row("a", priority: 0, labels: ["stage:implement"]),
            Row("b", priority: 1, labels: ["stage:merge"]),
            Row("c", priority: 2, labels: ["stage:implement"])
        ], ["a", "b", "c"]);
        gates["b"].Release.TrySetResult(true);
        var published = await pool.CompletionReader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var bCompletion = await first.Admitted.Single(admission => admission.BeadId == "b").Completion
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Same(bCompletion, published);

        Assert.True(bCompletion.ClaimHandoffCompleted);
        Assert.True(bCompletion.LocalCapacityReleased);
        Assert.Equal(DeliverySlotDisposition.MergeReady, bCompletion.Outcome?.Disposition);
        Assert.Equal(["a"], pool.ActiveReservations.Select(reservation => reservation.BeadId));

        var second = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["c"], second.Admitted.Select(admission => admission.BeadId));
        await gates.GetOrAdd("c", _ => (NewSignal(), NewSignal())).Started.Task
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["a", "c"], pool.ActiveReservations.Select(reservation => reservation.BeadId).Order(StringComparer.Ordinal));

        gates["a"].Release.TrySetResult(true);
        gates["c"].Release.TrySetResult(true);
        await Task.WhenAll(first.Admitted.Where(admission => admission.BeadId != "b")
            .Concat(second.Admitted).Select(admission => admission.Completion));
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task ReservationSpansInternalStageProgressUntilTheWholeSlotReturns()
    {
        var current = Snapshot([Row("a", labels: ["stage:implement"]), Row("b", priority: 1, labels: [])], ["a", "b"]);
        var enteredNextStage = NewSignal();
        var finishSlot = NewSignal();
        var pool = CreatePool(1, () => Task.FromResult(current), async (_, _, _, beadId, _, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            if (beadId == "a")
            {
                enteredNextStage.TrySetResult(true);
                await finishSlot.Task;
            }
            return SlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome);
        });

        var first = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await enteredNextStage.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        current = Snapshot([Row("a", labels: ["stage:review"]), Row("b", priority: 1, labels: [])], ["a", "b"]);
        var whileRunning = await pool.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(whileRunning.Admitted);
        Assert.Equal("a", Assert.Single(whileRunning.ActiveReservations).BeadId);
        Assert.Contains(whileRunning.Diagnostics, item => item.BeadId == "b" && item.Code == DeliveryPoolDiagnosticCode.CapacityReached);

        finishSlot.TrySetResult(true);
        await first.Admitted[0].Completion;
        current = Snapshot([Row("a", labels: ["stage:merge"]), Row("b", priority: 1, labels: [])], ["a", "b"]);
        var afterCompletion = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["b"], afterCompletion.Admitted.Select(admission => admission.BeadId));
        await afterCompletion.Admitted[0].Completion;
    }

    [Fact]
    public async Task EverySlotDispositionFailureAndCancellationIsPublishedExactlyOnce()
    {
        var ids = new[] { "merge", "refused", "escalated", "paused", "failed", "unsupported", "fault", "canceled" };
        var rows = ids.Select((id, index) => Row(id, priority: index % 5, labels: [])).ToArray();
        var outcomes = new Dictionary<string, DeliverySlotOutcome>(StringComparer.Ordinal)
        {
            ["merge"] = SlotOutcome(DeliverySlotDisposition.MergeReady, handoff: true),
            ["refused"] = SlotOutcome(DeliverySlotDisposition.Refused),
            ["escalated"] = SlotOutcome(DeliverySlotDisposition.Escalated, handoff: true),
            ["paused"] = SlotOutcome(DeliverySlotDisposition.PausedForProductDecision, handoff: true),
            ["failed"] = SlotOutcome(DeliverySlotDisposition.Failed),
            ["unsupported"] = SlotOutcome(DeliverySlotDisposition.StoppedAtUnsupportedOutcome)
        };
        var pool = CreatePool(ids.Length, () => Task.FromResult(Snapshot(rows, ids)), (_, _, _, beadId, _, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            return beadId switch
            {
                "fault" => throw new InvalidOperationException("controlled slot failure"),
                "canceled" => throw new OperationCanceledException("controlled slot cancellation", new CancellationToken(canceled: true)),
                _ => Task.FromResult(outcomes[beadId])
            };
        });

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var completed = await Task.WhenAll(evaluation.Admitted.Select(admission => admission.Completion)
            .Select(task => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));

        Assert.Equal(ids.Length, completed.Length);
        Assert.Equal(ids.Length, completed.Select(completion => completion.BeadId).Distinct(StringComparer.Ordinal).Count());
        Assert.Same(outcomes["merge"], completed.Single(completion => completion.BeadId == "merge").Outcome);
        Assert.True(completed.Single(completion => completion.BeadId == "merge").ClaimHandoffCompleted);
        Assert.All(completed, completion => Assert.True(completion.LocalCapacityReleased));
        Assert.Same(outcomes["refused"], completed.Single(completion => completion.BeadId == "refused").Outcome);
        Assert.Same(outcomes["escalated"], completed.Single(completion => completion.BeadId == "escalated").Outcome);
        Assert.Same(outcomes["paused"], completed.Single(completion => completion.BeadId == "paused").Outcome);
        Assert.True(completed.Single(completion => completion.BeadId == "paused").ClaimHandoffCompleted);
        Assert.Same(outcomes["failed"], completed.Single(completion => completion.BeadId == "failed").Outcome);
        Assert.Same(outcomes["unsupported"], completed.Single(completion => completion.BeadId == "unsupported").Outcome);
        Assert.IsType<InvalidOperationException>(completed.Single(completion => completion.BeadId == "fault").Exception);
        Assert.True(completed.Single(completion => completion.BeadId == "canceled").IsCanceled);
        Assert.Empty(pool.ActiveReservations);

        var streamed = new List<DeliveryPoolCompletion>();
        for (var index = 0; index < ids.Length; index++)
            streamed.Add(await pool.CompletionReader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(ids.Length, streamed.Select(completion => completion.BeadId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task EvaluationCancellationDoesNotCancelAnAdmittedSlotButPoolLifetimeDoes()
    {
        using var lifetime = new CancellationTokenSource();
        using var evaluationCancellation = new CancellationTokenSource();
        var runnerEntered = NewSignal();
        var pool = CreatePool(1, () => Task.FromResult(Snapshot([Row("long", labels: [])], ["long"])), async (_, _, _, _, token, agentAttemptStarted) =>
        {
            agentAttemptStarted();
            runnerEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SlotOutcome(DeliverySlotDisposition.Refused);
    }, lifetime: lifetime);

        var evaluation = await pool.EvaluateAsync(evaluationCancellation.Token);
        await runnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        evaluationCancellation.Cancel();
        Assert.Single(pool.ActiveReservations);

        lifetime.Cancel();
        var completion = await evaluation.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(completion.IsCanceled);
        Assert.Empty(pool.ActiveReservations);
    }

    private static DeliveryPoolCoordinator CreatePool(
        int capacity,
        Func<Task<DeliveryPoolSnapshot>> snapshotReader,
        DeliveryPoolSlotRunner slotRunner,
        CancellationTokenSource? lifetime = null)
    {
        var host = new HostPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-pool-host")));
        var target = new ValidatedTargetRepository(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-pool-target")), "main");
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        var project = new ProjectConfig(ProjectSlug.Parse("pool-tests"), "pool-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(1, capacity, 1), new LimitsConfig(3, 2), new AgentSettings(AgentCli.Codex, null, "low", 1),
            new RoleSettings(roles));
        var processRunner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        return new DeliveryPoolCoordinator(host, project, target, processRunner,
            (_, _, _) => snapshotReader(), slotRunner, lifetime?.Token ?? CancellationToken.None);
    }

    private static DeliveryPoolSnapshot Snapshot(IEnumerable<string> rows, IEnumerable<string> nativeReady)
    {
        var inventory = BeadsReader.Parse("[" + string.Join(',', rows) + "]");
        return new DeliveryPoolSnapshot(inventory, BeadsReadiness.Create(nativeReady, []));
    }

    private static string Row(
        string id,
        string status = "open",
        string issueType = "task",
        int priority = 1,
        string createdAt = "2026-01-01T00:00:00Z",
        string? assignee = null,
        string[]? labels = null,
        string? discoveredFrom = null,
        int repairCycles = 0)
    {
        var dependencies = discoveredFrom is null ? Array.Empty<object>() : [new { depends_on_id = discoveredFrom, type = "discovered-from" }];
        return JsonSerializer.Serialize(new
        {
            id,
            status,
            issue_type = issueType,
            priority,
            created_at = createdAt,
            assignee,
            labels = labels ?? [],
            dependencies,
            metadata = new { repair_cycles = repairCycles }
        });
    }

    private static string Id(string row)
    {
        using var document = JsonDocument.Parse(row);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static DeliverySlotOutcome SlotOutcome(DeliverySlotDisposition disposition, bool handoff = false) =>
        new(disposition, null, AgentRole.Implementer, null, handoff, $"{disposition} outcome");
}
