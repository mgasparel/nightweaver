using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using Fixture = Nightweaver.Tests.CompletedEnvironmentCleanupTests.Fixture;

namespace Nightweaver.Tests;

public sealed class CompletedEnvironmentWorkerTests
{
    [Theory]
    [InlineData("paused")]
    [InlineData("reopened")]
    [InlineData("assigned")]
    [InlineData("missing")]
    public async Task ProtectedCompletionPersistsAttentionWithoutBlockingOtherCleanupOrDrain(string change)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var inventory = await new BeadsReader(fixture.Runner).ReadSnapshotAsync(fixture.Repository, token);
        var state = BeadWorkflowState.Parse(inventory.ById["done"]);
        var session = new AgentStageSession(fixture.Paths, Config(fixture), fixture.Repository, "done", AgentRole.Merger,
            WorktreePurpose.Delivery, new(fixture.Worktree, fixture.Runner.Head), state, null,
            new("done", "task", "stage:merge", state.RepairCycles), true);
        var otherTmp = Directory.CreateDirectory(Path.Combine(fixture.Paths.TmpRoot(Fixture.Project), "other")).FullName;
        File.WriteAllText(Path.Combine(otherTmp, "disposable"), "done");
        await fixture.Store.WriteAsync(fixture.Store.Create("other", WorktreePurpose.Delivery, null), token);
        var runner = new RecordingProcessRunner(async (request, cancellation) =>
        {
            var result = await fixture.Runner.RunAsync(request, cancellation);
            if (request.FileName != "bd") return result;
            var beads = System.Text.Json.Nodes.JsonNode.Parse(result.StandardOutput)!.AsArray();
            if (change == "missing") beads.Clear();
            beads.Add(System.Text.Json.Nodes.JsonNode.Parse("""
                {"id":"other","status":"closed","issue_type":"task","priority":1,"created_at":"2026-11-13T00:00:00Z","labels":[]}
                """));
            return result with { StandardOutput = beads.ToJsonString() };
        });
        var gate = new ContinuousStopGate();
        using var worker = Worker(fixture, new Clock(), gate: gate, runner: runner);
        await worker.EnqueueAsync(session, new("https://example.invalid/pull/1", fixture.Runner.Head,
            fixture.Runner.Head, "squash", "comment-1"), token);
        fixture.Runner.Paused = change == "paused";
        fixture.Runner.Status = change == "reopened" ? "open" : "closed";
        fixture.Runner.Assignee = change == "assigned" ? "human:other" : null;

        await worker.RunBatchAsync(token);
        var records = fixture.Store.Read().Obligations;
        var protectedRecord = Assert.Single(records, record => record.BeadId == "done");
        Assert.Equal(CleanupStatus.NeedsAttention, protectedRecord.Status);
        Assert.Equal(0, protectedRecord.Attempts);
        Assert.Equal(fixture.Runner.Head, protectedRecord.VerifiedHead);
        Assert.Equal(CleanupStatus.Resolved, Assert.Single(records, record => record.BeadId == "other").Status);
        Assert.False(Directory.Exists(otherTmp));
        await worker.RunBatchAsync(token);
        gate.RequestStop();
        await worker.FlushCompletionsAsync(token);
        Assert.Equal(protectedRecord, Assert.Single(fixture.Store.Read().Obligations, record => record.BeadId == "done"));
        Assert.True(Directory.Exists(fixture.Worktree));
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationFailureAfterBeginBecomesAttentionWithoutRestartOrRemovalReplay(bool afterRemoval)
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var fail = true;
        var runner = new RecordingProcessRunner(async (request, cancellation) =>
        {
            if (fail && request.FileName == "bd" && File.Exists(fixture.RecordPath)
                && fixture.Read() is { Status: CleanupStatus.Pending, Attempts: 1 }
                && (!afterRemoval || fixture.Runner.Removals == 1))
            {
                fail = false;
                throw new ToolExecutionException("tracker unavailable after Begin");
            }
            return await fixture.Runner.RunAsync(request, cancellation);
        });
        var clock = new Clock();
        using var worker = Worker(fixture, clock, runner: runner);
        await worker.InitializeAsync(token);
        await Assert.ThrowsAsync<ToolExecutionException>(() => worker.RunBatchAsync(token));
        var interrupted = fixture.Read();
        Assert.Equal(CleanupStatus.NeedsAttention, interrupted.Status);
        Assert.Equal(1, interrupted.Attempts);
        Assert.Equal(1, interrupted.CycleAttempts);
        Assert.Contains("interrupted", interrupted.Diagnostic, StringComparison.Ordinal);
        clock.Now += TimeSpan.FromDays(1);
        await worker.RunBatchAsync(token);
        Assert.Equal(interrupted, fixture.Read());
        Assert.Equal(afterRemoval ? 1 : 0, fixture.Runner.Removals);
        Assert.True(Directory.Exists(fixture.Tmp));

        var inbox = new CleanupRetryInbox(fixture.Paths, Config(fixture), fixture.Repository);
        await inbox.WriteAsync(new(1, Guid.NewGuid().ToString("N"), "test-runtime-owner-0001", "done",
            CleanupRetryInbox.Revision(interrupted), clock.Now, "queued"), token);
        await worker.RunBatchAsync(token);
        Assert.Equal("accepted", Assert.Single(inbox.Read()).Outcome);
        Assert.Equal(CleanupStatus.Resolved, fixture.Read().Status);
        Assert.Equal(2, fixture.Read().Attempts);
        Assert.Equal(1, fixture.Runner.Removals);
    }

    [Fact]
    public async Task RetryIntervalsAndBudgetSurviveEveryRestartWithoutHotLoop()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        fixture.Runner.TransientFailures = 100;
        var expected = new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) };
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var worker = Worker(fixture, clock);
            await worker.InitializeAsync(TestContext.Current.CancellationToken);
            await worker.RunBatchAsync(TestContext.Current.CancellationToken);
            var record = fixture.Read();
            Assert.Equal(attempt, record.Attempts);
            Assert.Equal(attempt, record.CycleAttempts);
            Assert.Equal(attempt, fixture.Runner.Removals);
            if (attempt == 4)
            {
                Assert.Equal(CleanupStatus.NeedsAttention, record.Status);
                clock.Now += TimeSpan.FromDays(1);
                await worker.RunBatchAsync(TestContext.Current.CancellationToken);
                Assert.Equal(record, fixture.Read());
            }
            else
            {
                Assert.Equal(CleanupStatus.RetryScheduled, record.Status);
                Assert.Equal(clock.Now + expected[attempt - 1], record.NextAttempt);
                clock.Now = record.NextAttempt!.Value - TimeSpan.FromTicks(1);
                await worker.RunBatchAsync(TestContext.Current.CancellationToken);
                Assert.Equal(record, fixture.Read());
                clock.Now += TimeSpan.FromTicks(1);
            }
        }
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("active")]
    [InlineData("foreign")]
    [InlineData("head")]
    [InlineData("dirty")]
    [InlineData("resolved")]
    public async Task RetryRevalidatesCurrentIdentityAndEligibility(string change)
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        using var worker = Worker(fixture, clock);
        fixture.Runner.TransientFailures = 1;
        await worker.InitializeAsync(TestContext.Current.CancellationToken);
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        clock.Now += TimeSpan.FromSeconds(30);
        switch (change)
        {
            case "paused": fixture.Runner.Paused = true; break;
            case "active": fixture.Runner.Status = "open"; break;
            case "foreign": fixture.Runner.Assignee = "human:other"; break;
            case "head": fixture.Runner.Head = new string('b', 40); break;
            case "dirty": fixture.Runner.Dirty = true; break;
        }
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(change == "resolved" ? CleanupStatus.Resolved : CleanupStatus.NeedsAttention, fixture.Read().Status);
        Assert.Equal(change != "resolved", Directory.Exists(fixture.Worktree));
        Assert.Equal(change != "resolved", Directory.Exists(fixture.Tmp));
        Assert.Equal(change == "resolved" ? 2 : 1, fixture.Runner.Removals);
        Assert.All(fixture.Runner.Requests.Where(request => request.FileName == "bd"), request => Assert.Contains("list", request.Arguments));
    }

    [Fact]
    public async Task ConcurrentBatchesAndActiveReservationExcludeDuplicateRemovalAndAllowOtherReservations()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        var reservations = new EnvironmentReservations();
        using var active = reservations.TryAcquire("done");
        using var worker = Worker(fixture, clock, reservations);
        await worker.InitializeAsync(TestContext.Current.CancellationToken);
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Read().Attempts);
        active!.Dispose();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.Arguments.Take(2).SequenceEqual(["worktree", "remove"]))
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
            return await fixture.Runner.RunAsync(request, token);
        });
        using var slow = Worker(fixture, clock, reservations, runner: runner);
        var first = slow.RunBatchAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(reservations.TryAcquire("done"));
        using var unrelated = reservations.TryAcquire("other");
        Assert.NotNull(unrelated);
        var duplicate = slow.RunBatchAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(first, duplicate);
        Assert.Equal(1, fixture.Runner.Removals);
        Assert.Equal(CleanupStatus.Resolved, fixture.Read().Status);
    }

    [Fact]
    public async Task InterruptedAttemptAndPolicyDenialNeedInspectionAndNeverAutomaticallyRearm()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        await fixture.Store.WriteAsync(CleanupRetryPolicy.Begin(fixture.Create(), clock.Now), TestContext.Current.CancellationToken);
        using var worker = Worker(fixture, clock);
        await worker.InitializeAsync(TestContext.Current.CancellationToken);
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
        Assert.Equal(0, fixture.Runner.Removals);
        Assert.Contains("interrupted", fixture.Read().Diagnostic, StringComparison.Ordinal);
        var record = fixture.Read();
        await fixture.Store.WriteAsync(record with { CycleAttempts = 0, Status = CleanupStatus.Pending }, TestContext.Current.CancellationToken);
        fixture.Runner.RemovalDenied = true;
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        clock.Now += TimeSpan.FromDays(2);
        var denied = fixture.Read();
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(denied, fixture.Read());
        Assert.Contains("policy denied", denied.LastDenial, StringComparison.Ordinal);
        Assert.Equal(2, denied.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedInboxRejectsStaleAndStoppedOwnersAndCoalescesDuplicateRequests(bool force)
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        var config = Config(fixture);
        fixture.Runner.RemovalDenied = true;
        using (var first = Worker(fixture, clock))
        {
            await first.InitializeAsync(TestContext.Current.CancellationToken);
            await first.RunBatchAsync(TestContext.Current.CancellationToken);
        }
        var denied = fixture.Read();
        var inbox = new CleanupRetryInbox(fixture.Paths, config, fixture.Repository);
        await using var telemetry = new FactoryTelemetryPublisher(fixture.Paths, config, fixture.Repository, 2, 1);
        await telemetry.StartAsync(TestContext.Current.CancellationToken);
        var gate = new ContinuousStopGate();
        using var worker = Worker(fixture, clock, gate: gate, runtimeId: telemetry.RuntimeId);
        await worker.InitializeAsync(TestContext.Current.CancellationToken);
        var requestId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync("stale-owner", "done", CleanupRetryInbox.Revision(denied), requestId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync(telemetry.RuntimeId, "done", "stale-revision", requestId, TestContext.Current.CancellationToken));
        var queued = await inbox.QueueAsync(telemetry.RuntimeId, "done", CleanupRetryInbox.Revision(denied), requestId, TestContext.Current.CancellationToken);
        Assert.Equal(queued, await inbox.QueueAsync(telemetry.RuntimeId, "done", CleanupRetryInbox.Revision(denied), requestId, TestContext.Current.CancellationToken));
        // A duplicate ID with another request still refers to the original revision; it cannot grant two cycles.
        Assert.Equal(queued, await inbox.QueueAsync(telemetry.RuntimeId, "done", CleanupRetryInbox.Revision(denied), Guid.NewGuid(), TestContext.Current.CancellationToken));
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Read().Attempts);
        Assert.Equal(1, fixture.Read().CycleAttempts);
        Assert.Equal(denied.LastDenial, fixture.Read().LastDenial);
        Assert.Single(inbox.Read(), request => request.Outcome == "accepted");
        Assert.Single(inbox.Read());
        // Keep telemetry and the worker alive to represent a held graceful pool drain.
        // A request queued before stop is retained honestly but must not rearm on this owner.
        var stopped = fixture.Read();
        var beforeStop = await inbox.QueueAsync(telemetry.RuntimeId, "done", CleanupRetryInbox.Revision(stopped), Guid.NewGuid(), TestContext.Current.CancellationToken);
        if (force) Assert.Empty(gate.RequestForceStop());
        else gate.RequestStop();
        Assert.Equal(FactoryTelemetryAvailability.Live, new FactoryTelemetryReader(fixture.Paths, config).Read().Availability);
        var count = inbox.Read().Count;
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync(telemetry.RuntimeId, "done", CleanupRetryInbox.Revision(stopped), Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Contains("accepting", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(count, inbox.Read().Count);
        Assert.Equal(beforeStop, Assert.Single(inbox.Read(), request => request.RequestId == beforeStop.RequestId));
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(stopped, fixture.Read());
        await telemetry.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync(telemetry.RuntimeId, "done", CleanupRetryInbox.Revision(stopped), Guid.NewGuid(), TestContext.Current.CancellationToken));
        using var restarted = Worker(fixture, clock);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("expired", Assert.Single(inbox.Read(), request => request.RequestId == beforeStop.RequestId).Outcome);
    }

    [Fact]
    public async Task FreshTelemetryAloneCannotQueueBeforeInitializationAfterDisposalOrOnAlreadyStoppedGate()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        fixture.Runner.RemovalDenied = true;
        await fixture.CleanAsync();
        var before = fixture.Read();
        var config = Config(fixture);
        var inbox = new CleanupRetryInbox(fixture.Paths, config, fixture.Repository);
        await using var telemetry = new FactoryTelemetryPublisher(fixture.Paths, config, fixture.Repository, 2, 1);
        await telemetry.StartAsync(token);
        var gate = new ContinuousStopGate();
        using (var worker = Worker(fixture, new Clock(), gate: gate, runtimeId: telemetry.RuntimeId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync(telemetry.RuntimeId,
                "done", CleanupRetryInbox.Revision(before), Guid.NewGuid(), token));
            await worker.InitializeAsync(token);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync(telemetry.RuntimeId,
            "done", CleanupRetryInbox.Revision(before), Guid.NewGuid(), token));
        gate.RequestStop();
        using var stopped = Worker(fixture, new Clock(), gate: gate, runtimeId: telemetry.RuntimeId);
        await stopped.InitializeAsync(token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.QueueAsync(telemetry.RuntimeId,
            "done", CleanupRetryInbox.Revision(before), Guid.NewGuid(), token));
        Assert.Empty(inbox.Read());
        Assert.Equal(before, fixture.Read());
        Assert.Equal(FactoryTelemetryAvailability.Live, new FactoryTelemetryReader(fixture.Paths, config).Read().Availability);
    }

    [Fact]
    public async Task StoreFailureIsExplicitAndStopStartsNoAttempt()
    {
        using var fixture = new Fixture();
        var gate = new ContinuousStopGate();
        using var worker = Worker(fixture, new Clock(), gate: gate);
        await worker.InitializeAsync(TestContext.Current.CancellationToken);
        gate.RequestStop();
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Read().Attempts);
        File.WriteAllText(fixture.RecordPath, "{");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => worker.RunBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    [Fact]
    public async Task CompletionHintsCoalesceAndPersistOnGracefulStopWithoutRemoval()
    {
        using var fixture = new Fixture();
        var config = Config(fixture);
        var gate = new ContinuousStopGate();
        using var worker = Worker(fixture, new Clock(), gate: gate);
        var inventory = await new BeadsReader(fixture.Runner).ReadSnapshotAsync(fixture.Repository, TestContext.Current.CancellationToken);
        var state = BeadWorkflowState.Parse(inventory.ById["done"]);
        var session = new AgentStageSession(fixture.Paths, config, fixture.Repository, "done", AgentRole.Merger,
            WorktreePurpose.Delivery, new(fixture.Worktree, fixture.Runner.Head), state, null,
            new("done", "task", "stage:merge", state.RepairCycles), true);
        var evidence = new MergerVerifiedMergeEvidence("https://example.invalid/pull/1", fixture.Runner.Head,
            fixture.Runner.Head, "squash", "comment-1");
        Assert.True(worker.EnqueueAsync(session, evidence, TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        await worker.EnqueueAsync(session, evidence, TestContext.Current.CancellationToken);
        Assert.False(fixture.Store.Read().Exists);
        gate.RequestStop();
        await worker.FlushCompletionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(evidence.CheckedHead, fixture.Read().VerifiedHead);
        Assert.Equal(0, fixture.Read().Attempts);
        Assert.Equal(0, fixture.Runner.Removals);
        Assert.True(Directory.Exists(fixture.Tmp));
    }

    [Fact]
    public async Task CancellationDuringRemovalRetainsAttemptAndRestartRequiresSettlementInspection()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.Arguments.Take(2).SequenceEqual(["worktree", "remove"]))
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return await fixture.Runner.RunAsync(request, token);
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var worker = Worker(fixture, new Clock(), runner: runner);
        await worker.InitializeAsync(lifetime.Token);
        var attempt = worker.RunBatchAsync(lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(CleanupStatus.Pending, fixture.Read().Status);
        using var restarted = Worker(fixture, new Clock());
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        await restarted.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
        Assert.Equal(1, fixture.Read().Attempts);
        Assert.True(Directory.Exists(fixture.Worktree));
    }

    [Fact]
    public async Task LegacyTransientFailureKeepsItsBudgetAndOriginalRetryTime()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        var legacy = fixture.Create() with { Status = CleanupStatus.NeedsAttention, Attempts = 2,
            LastAttempt = clock.Now, Diagnostic = "resource busy: sharing violation" };
        await fixture.Store.WriteAsync(legacy, TestContext.Current.CancellationToken);
        using var worker = Worker(fixture, clock);
        await worker.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(2), fixture.Read().NextAttempt);
        Assert.Equal(2, fixture.Read().CycleAttempts);
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Runner.Removals);
        clock.Now += TimeSpan.FromMinutes(2);
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CleanupStatus.Resolved, fixture.Read().Status);
        Assert.Equal(3, fixture.Read().Attempts);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("reserved")]
    [InlineData("old-owner")]
    [InlineData("old-revision")]
    public async Task RetryRequestCannotOverrideProtectionOrStaleEvidence(string change)
    {
        using var fixture = new Fixture();
        fixture.Runner.RemovalDenied = true;
        await fixture.CleanAsync();
        fixture.Runner.RemovalDenied = false;
        var before = fixture.Read();
        var reservations = new EnvironmentReservations();
        using var active = change == "reserved" ? reservations.TryAcquire("done") : null;
        fixture.Runner.Paused = change == "paused";
        var inbox = new CleanupRetryInbox(fixture.Paths, Config(fixture), fixture.Repository);
        await inbox.WriteAsync(new(1, Guid.NewGuid().ToString("N"),
            change == "old-owner" ? "another-runtime-owner" : "test-runtime-owner-0001", "done",
            change == "old-revision" ? new string('0', 64) : CleanupRetryInbox.Revision(before),
            DateTimeOffset.UtcNow, "queued"), TestContext.Current.CancellationToken);
        using var worker = Worker(fixture, new Clock(), reservations);
        await worker.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, fixture.Read());
        Assert.Equal(0, fixture.Runner.Removals);
        Assert.Equal(change is "paused" or "reserved" ? "protected" : "stale", Assert.Single(inbox.Read()).Outcome);
    }

    [Fact]
    public async Task LostAcknowledgementAfterRearmingDoesNotGrantAnotherCycleOnRestart()
    {
        using var fixture = new Fixture();
        var clock = new Clock();
        var requestId = Guid.NewGuid().ToString("N");
        var before = CleanupRetryPolicy.Fail(CleanupRetryPolicy.Begin(fixture.Create(), clock.Now), "policy denied", false, clock.Now);
        var inbox = new CleanupRetryInbox(fixture.Paths, Config(fixture), fixture.Repository);
        await inbox.WriteAsync(new(1, requestId, "previous-runtime-owner", "done", CleanupRetryInbox.Revision(before),
            clock.Now, "queued"), TestContext.Current.CancellationToken);
        await fixture.Store.WriteAsync(before with { Status = CleanupStatus.Pending, CycleAttempts = 0,
            LastRetryRequestId = requestId }, TestContext.Current.CancellationToken);
        using var restarted = Worker(fixture, clock);
        await restarted.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("accepted", Assert.Single(inbox.Read()).Outcome);
        Assert.Equal(0, fixture.Read().CycleAttempts);
        await restarted.RunBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CleanupStatus.Resolved, fixture.Read().Status);
        Assert.Equal(2, fixture.Read().Attempts);
        Assert.Equal(1, fixture.Read().CycleAttempts);
        Assert.Equal("policy denied", fixture.Read().LastDenial);
    }

    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CompletedEnvironmentWorker Worker(Fixture fixture, Clock clock,
        EnvironmentReservations? reservations = null, ContinuousStopGate? gate = null,
        string runtimeId = "test-runtime-owner-0001", IProcessRunner? runner = null) => new(fixture.Paths, Config(fixture),
        fixture.Repository, runner ?? fixture.Runner, reservations ?? new(), gate ?? new(), runtimeId, _ => { }, clock);

    private static ProjectConfig Config(Fixture fixture)
    {
        var defaults = new AgentSettings(AgentCli.Codex, null, "low", 1);
        return new(Fixture.Project, "test.json", new(fixture.Repository.Path, "main"), new(1, 2, 10), new(3, 3), defaults,
            new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => defaults)));
    }
}
