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
        var rows = new[]
        {
            Row("later-priority", priority: 1, createdAt: "2026-01-02T00:00:00Z", labels: ["stage:merge"]),
            Row("priority-first", priority: 0, createdAt: "2026-01-03T00:00:00Z", labels: ["stage:merge"]),
            Row("blocked", status: "blocked", labels: ["stage:merge"]),
            Row("not-ready", labels: ["stage:merge"]),
            Row("foreign-owned", status: "in_progress", assignee: "human", labels: ["stage:merge"]),
            Row("paused", labels: ["stage:merge", "needs-human"]),
            Row("planned", labels: ["stage:merge", "planned"]),
            Row("malformed", labels: ["stage:merge", "stage:qa"]),
            Row("not-a-task", issueType: "feature", labels: ["stage:merge"]),
            Row("other-stage", labels: ["stage:review"])
        };
        var ready = new[] { "later-priority", "priority-first", "blocked", "not-ready", "foreign-owned", "paused", "planned", "malformed", "not-a-task", "other-stage" };
        var pool = CreatePool(() => Task.FromResult(Snapshot(rows, ["later-priority", "priority-first", "blocked", "paused", "planned", "malformed", "not-a-task", "other-stage"])),
            (_, _, _, _, _) => Task.FromResult(Outcome(MergerSlotDisposition.Merged, handoff: true)));

        var evaluation = await pool.EvaluateAsync(TestContext.Current.CancellationToken);
        var admission = Assert.Single(evaluation.Admitted);
        Assert.Equal("priority-first", admission.BeadId);
        Assert.Equal(1, evaluation.CapacityLimit);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "later-priority" && item.Code == MergerPoolDiagnosticCode.CapacityReached);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "blocked" && item.Code == MergerPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "paused" && item.Code == MergerPoolDiagnosticCode.NotEligible);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "planned" && item.Code == MergerPoolDiagnosticCode.InvalidWorkflowState);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "malformed" && item.Code == MergerPoolDiagnosticCode.InvalidWorkflowState);
        Assert.Contains(evaluation.Diagnostics, item => item.BeadId == "not-a-task" && item.Code == MergerPoolDiagnosticCode.NotPlanBead);
        Assert.DoesNotContain(evaluation.Admitted, item => ready.Contains(item.BeadId, StringComparer.Ordinal)
            && item.BeadId != "priority-first");
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
        }, async (_, _, _, _, _) =>
        {
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
        var pool = CreatePool(() => Task.FromResult(Snapshot([row], ["candidate"])), (_, _, _, _, _) =>
        {
            return Interlocked.Increment(ref calls) switch
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

    private static MergerPoolCoordinator CreatePool(Func<Task<MergerPoolSnapshot>> snapshotReader,
        MergerPoolSlotRunner slotRunner, int maxConcurrentDelivery = 1)
    {
        var host = new HostPaths(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-pool-host")));
        var target = new ValidatedTargetRepository(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "merger-pool-target")), "main");
        var roles = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "low", 1));
        var project = new ProjectConfig(ProjectSlug.Parse("merger-pool-tests"), "merger-pool-tests.json", new(target.Path, "main"),
            new ConcurrencyConfig(1, maxConcurrentDelivery, 1), new LimitsConfig(3, 2),
            new AgentSettings(AgentCli.Codex, null, "low", 1), new RoleSettings(roles));
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        return new MergerPoolCoordinator(host, project, target, runner,
            (_, _, _) => snapshotReader(), slotRunner);
    }

    private static MergerPoolSnapshot Snapshot(IEnumerable<string> rows, IEnumerable<string> nativeReady) =>
        new(BeadsReader.Parse("[" + string.Join(',', rows) + "]"), BeadsReadiness.Create(nativeReady, []));

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
