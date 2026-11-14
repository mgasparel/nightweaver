using System.Collections.Concurrent;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class PlanningPoolCoordinatorTests
{
    [Fact]
    public async Task RepeatedReadinessSnapshotMismatchFailsBeforePlanningSelectionOrLaunch()
    {
        const string actor = "nightweaver:pool-tests";
        var stale = Row("candidate", status: "open", labels: ["stage:plan"]);
        var current = Row("candidate", status: "in_progress", assignee: actor, labels: ["stage:plan"]);
        var outputs = new Queue<ProcessResult>([
            new(0, $"[{stale}]", ""), new(0, $"[{stale}]", ""), new(0, $"[{current}]", ""),
            new(0, $"[{stale}]", ""), new(0, $"[{stale}]", ""), new(0, $"[{current}]", "")
        ]);
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(outputs.Dequeue()));
        var repository = new ValidatedTargetRepository(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "planning-pool-target")), "main");
        var reader = new BeadsReader(runner);
        var launches = 0;
        var pool = CreatePool(1, async () =>
        {
            var snapshot = await reader.ReadSnapshotWithReadinessAsync(repository, actor, CancellationToken.None);
            return new PlanningPoolSnapshot(snapshot.Inventory, snapshot.Readiness);
        }, (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(SlotOutcome(PlanningSlotDisposition.Interrupted));
        });

        await Assert.ThrowsAsync<BeadsReadinessSnapshotMismatchException>(() =>
            pool.EvaluateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(6, runner.Requests.Count);
        Assert.Equal(0, launches);
        Assert.Empty(pool.ActiveReservations);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReservationsNeverExceedConfiguredCapacity(int capacity)
    {
        if (capacity == ProjectConfigDefaults.MaxConcurrentPlanning)
            Assert.Equal(2, ProjectConfigDefaults.MaxConcurrentPlanning);

        var rows = Enumerable.Range(0, 5)
            .Select(index => Row($"task-{index}", priority: index, createdAt: "2026-01-01T00:00:00Z", labels: []))
            .ToArray();
        var release = NewSignal();
        var entered = NewSignal();
        var enteredCount = 0;
        var pool = CreatePool(capacity, () => Task.FromResult(Snapshot(rows, rows.Select(Id))), async (_, _, _, _, _) =>
        {
            if (Interlocked.Increment(ref enteredCount) == capacity) entered.TrySetResult(true);
            await release.Task;
            return SlotOutcome(PlanningSlotDisposition.Refused);
        });

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(capacity, evaluation.Admitted.Count);
        Assert.Equal(capacity, pool.ActiveReservations.Count);
        var second = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(second.Admitted);
        Assert.Contains(second.Diagnostics, item => item.Code == PlanningPoolDiagnosticCode.CapacityReached);

        release.TrySetResult(true);
        await Task.WhenAll(evaluation.Admitted.Select(admission => admission.Completion));
        Assert.Empty(pool.ActiveReservations);
    }
    [Fact]
    public async Task SlowPreparationDoesNotBlockAdmissionAndNextCandidateUsesLaterFreshPass()
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
        var current = Snapshot(rows, ["first", "second"]);
        var pool = CreatePool(1, () => Task.FromResult(current), async (_, _, _, beadId, _) =>
        {
            calls.AddOrUpdate(beadId, 1, static (_, count) => count + 1);
            if (beadId == "first")
            {
                firstRunnerEntered.TrySetResult(true);
                await releaseFirstRefusal.Task;
                return SlotOutcome(PlanningSlotDisposition.Refused);
            }

            secondAttemptStarted.TrySetResult(true);
            return SlotOutcome(PlanningSlotDisposition.Interrupted);
        });

        var evaluationTask = pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await firstRunnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var evaluation = await evaluationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["first"], evaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "second"
            && item.Code == PlanningPoolDiagnosticCode.CapacityReached);
        Assert.Single(pool.ActiveReservations);

        releaseFirstRefusal.TrySetResult(true);
        var firstCompletion = await evaluation.Admitted[0].Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(PlanningSlotDisposition.Refused, firstCompletion.Outcome?.Disposition);
        current = Snapshot([Row("first", labels: ["stage:merge"]), rows[1]], ["second"]);
        var nextEvaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["second"], nextEvaluation.Admitted.Select(admission => admission.BeadId));
        Assert.Equal(1, calls["first"]);
        Assert.Equal(1, calls["second"]);
        await nextEvaluation.Admitted.Single().Completion;
        Assert.Empty(pool.ActiveReservations);
    }
    [Fact]
    public async Task OverlappingEvaluationsUseFreshReadsAndCannotReserveTheSameBeadTwice()
    {
        var candidate = Row("same", labels: ["stage:plan-challenge"]);
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
        }, async (_, _, _, _, token) =>
        {
            Interlocked.Increment(ref runnerCount);
            runnerStarted.TrySetResult(true);
            await releaseRunner.Task;
            return SlotOutcome(PlanningSlotDisposition.Refused);
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
            item => item.BeadId == "same" && item.Code == PlanningPoolDiagnosticCode.AlreadyReserved);
        Assert.Equal(1, runnerCount);

        releaseRunner.TrySetResult(true);
        await Task.WhenAll(evaluations.SelectMany(evaluation => evaluation.Admitted).Select(admission => admission.Completion));
    }
    [Fact]
    public async Task CompletionReleasesCapacityBeforePublicationAndAllowsReplacementWhileAnotherSlotRuns()
    {
        var current = Snapshot(
        [
            Row("a", priority: 0, labels: ["stage:plan"]),
            Row("b", priority: 1, labels: ["stage:plan"]),
            Row("c", priority: 2, labels: ["stage:plan"])
        ], ["a", "b", "c"]);
        var gates = new ConcurrentDictionary<string, (TaskCompletionSource<bool> Started, TaskCompletionSource<bool> Release)>(StringComparer.Ordinal);
        var pool = CreatePool(2, () => Task.FromResult(current), async (_, _, _, beadId, _) =>
        {
            var gate = gates.GetOrAdd(beadId, _ => (NewSignal(), NewSignal()));
            gate.Started.TrySetResult(true);
            await gate.Release.Task;
            return beadId == "b"
                ? SlotOutcome(PlanningSlotDisposition.Published, handoff: true)
                : SlotOutcome(PlanningSlotDisposition.Interrupted);
        });

        var first = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], first.Admitted.Select(admission => admission.BeadId));
        await Task.WhenAll(first.Admitted.Select(admission => gates.GetOrAdd(admission.BeadId, _ => (NewSignal(), NewSignal())).Started.Task));

        current = Snapshot(
        [
            Row("a", priority: 0, labels: ["stage:plan"]),
            Row("b", priority: 1, labels: ["stage:merge"]),
            Row("c", priority: 2, labels: ["stage:plan"])
        ], ["a", "b", "c"]);
        gates["b"].Release.TrySetResult(true);
        var published = await pool.CompletionReader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var bCompletion = await first.Admitted.Single(admission => admission.BeadId == "b").Completion
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Same(bCompletion, published);

        Assert.True(bCompletion.ClaimHandoffCompleted);
        Assert.True(bCompletion.LocalCapacityReleased);
        Assert.Equal(PlanningSlotDisposition.Published, bCompletion.Outcome?.Disposition);
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
    public async Task EvaluationCancellationDoesNotCancelAnAdmittedSlotButPoolLifetimeDoes()
    {
        using var lifetime = new CancellationTokenSource();
        using var evaluationCancellation = new CancellationTokenSource();
        var runnerEntered = NewSignal();
        var pool = CreatePool(1, () => Task.FromResult(Snapshot([Row("long", labels: [])], ["long"])), async (_, _, _, _, token) =>
        {
            runnerEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SlotOutcome(PlanningSlotDisposition.Refused);
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

    [Fact]
    public async Task LiveAdmissionExcludesBacklogPausesOwnershipAndOrdersSupportedProducts()
    {
        var rows = new[] { Row("backlog", authorized: false), Row("pause", labels: ["needs-human"]),
            Row("planned", labels: ["planned"]), Row("foreign", assignee: "operator"),
            Row("bad", labels: ["stage:qa"]), Row("multiple", labels: ["stage:plan", "stage:plan-challenge"]),
            Row("not-ready"), Row("b", labels: ["stage:plan-approved"]),
            Row("a", labels: ["stage:plan-challenge"]), Row("first", priority: 0) };
        var release = NewSignal();
        var pool = CreatePool(3, () => Task.FromResult(Snapshot(rows, rows.Select(Id).Where(id => id != "not-ready"))),
            async (_, _, _, _, _) => { await release.Task; return SlotOutcome(PlanningSlotDisposition.Published, true); });
        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        try { Assert.Equal(["first", "a", "b"], evaluation.Admitted.Select(item => item.BeadId)); }
        finally { release.TrySetResult(true); await Task.WhenAll(evaluation.Admitted.Select(item => item.Completion)); }
    }

    [Theory]
    [InlineData("open", null)]
    [InlineData("in_progress", "operator")]
    [InlineData("blocked", null)]
    [InlineData("deferred", null)]
    public async Task BackpressureCountsEveryNonclosedNonpausedTaskAndLetsAdmittedWorkSettle(string status, string? owner)
    {
        var current = Snapshot([Row("product")], ["product"]);
        var release = NewSignal();
        var pool = CreatePool(2, () => Task.FromResult(current), async (_, _, _, _, _) =>
        { await release.Task; return SlotOutcome(PlanningSlotDisposition.Published, true); }, maxActive: 1);
        var first = Assert.Single((await pool.EvaluateAsync(TestContext.Current.CancellationToken)).Admitted);
        current = Snapshot([Row("product"), Row("next"), Row("task", status: status, issueType: "task", assignee: owner, labels: ["stage:merge"])],
            ["product", "next"]);
        try
        {
            Assert.Empty((await pool.EvaluateAsync(TestContext.Current.CancellationToken)).Admitted);
            Assert.Single(pool.ActiveReservations);
        }
        finally { release.TrySetResult(true); await first.Completion; }
        Assert.Empty(pool.ActiveReservations);
    }

    [Fact]
    public async Task StopRejectsNewReservationsAndCompletionAccountingIsLossless()
    {
        var gate = new ContinuousStopGate();
        gate.RequestStop();
        var pool = CreatePool(1, () => Task.FromResult(Snapshot([Row("product")], ["product"])),
            (_, _, _, _, _) => throw new InvalidOperationException("No launch after stop"), stopGate: gate);
        Assert.Empty((await pool.EvaluateAsync(TestContext.Current.CancellationToken)).Admitted);
        var barrier = pool.WaitForCompletionAccountingAsync(TestContext.Current.CancellationToken);
        var completion = await pool.CompletionReader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(completion.IsAccountingBarrier);
        completion.AccountingBarrier!.TrySetResult(true);
        await barrier;
    }

    private static PlanningPoolCoordinator CreatePool(
        int capacity,
        Func<Task<PlanningPoolSnapshot>> snapshotReader,
        PlanningPoolSlotRunner slotRunner,
        CancellationTokenSource? lifetime = null, int maxActive = 100, ContinuousStopGate? stopGate = null)
    {
        var host = new HostPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "planning-pool-host")));
        var target = new ValidatedTargetRepository(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "planning-pool-target")), "main");
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        var project = new ProjectConfig(ProjectSlug.Parse("pool-tests"), "pool-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(capacity, 1, maxActive), new LimitsConfig(3, 2), new AgentSettings(AgentCli.Codex, null, "low", 1),
            new RoleSettings(roles));
        var processRunner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        return new PlanningPoolCoordinator(host, project, target, processRunner,
            (_, _, _) => snapshotReader(), slotRunner, stopGate: stopGate, slotLifetimeToken: lifetime?.Token ?? CancellationToken.None);
    }

    private static PlanningPoolSnapshot Snapshot(IEnumerable<string> rows, IEnumerable<string> nativeReady)
    {
        var inventory = BeadsReader.Parse("[" + string.Join(',', rows) + "]");
        return new PlanningPoolSnapshot(inventory, BeadsReadiness.Create(nativeReady, []));
    }

    private static string Row(
        string id,
        string status = "open",
        string issueType = "feature",
        int priority = 1,
        string createdAt = "2026-01-01T00:00:00Z",
        string? assignee = null,
        string[]? labels = null,
        string? discoveredFrom = null,
        int repairCycles = 0, bool authorized = true)
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
            labels = (labels ?? []).Concat(authorized ? ["ready-for-planning"] : []).ToArray(),
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

    private static PlanningSlotOutcome SlotOutcome(PlanningSlotDisposition disposition, bool handoff = false) =>
        new(disposition, null, handoff, $"{disposition} outcome");
}
