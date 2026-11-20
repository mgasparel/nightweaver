using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class LifecycleObservationTests
{
    [Fact]
    public void EligibilityAndLifecycleShareTheSameBoundAndReleaseCapacity()
    {
        var budget = new ObservationBudget(1);
        var clock = new WorkflowTimingTests.Clock();
        var pool = new PoolObservation("delivery", 1, clock, _ => { }, (_, _) => { }, budget: budget);
        var lifecycle = new LifecycleObservation(clock, (_, _) => { }, _ => { }, (_, _) => { }, budget: budget);
        pool.Observe(["ns-proof"]);
        lifecycle.Observe([Bead()]);
        Assert.Equal(0, Assert.Single(lifecycle.Read("coverage")).Value);
        pool.Unknown(WaitCensor.Failure);
        lifecycle.Observe([Bead()]);
        Assert.Equal(1, Assert.Single(lifecycle.Read("coverage")).Value);
        lifecycle.Unknown(LifecycleLoss.Failure);
        pool.Observe(["ns-proof"]);
        Assert.Equal(1, Assert.Single(pool.Read("coverage")).Value);
    }

    [Fact]
    public void UtcJumpsDoNotChangeWaitDuration()
    {
        var clock = new JumpClock();
        List<double> completed = [];
        var observation = new LifecycleObservation(clock, (_, seconds) => completed.Add(seconds), _ => { }, (_, _) => { });
        observation.Observe([Bead()]);
        observation.Observe([Bead(labels: ["needs-human"])]);
        clock.Utc = DateTimeOffset.UnixEpoch.AddDays(-100);
        clock.Seconds = 12;
        observation.Observe([Bead()]);
        Assert.Equal(12, Assert.Single(completed));
    }

    private sealed class JumpClock : TimeProvider
    {
        internal DateTimeOffset Utc = DateTimeOffset.UnixEpoch;
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private static BeadSnapshot Bead(string id = "ns-proof", string[]? labels = null, string status = "open", int repairs = 0) =>
        new(id, status, "task", 2, DateTimeOffset.UnixEpoch, null, labels ?? [],
            JsonSerializer.SerializeToElement(new { metadata = new { repair_cycles = repairs }, description = "PRIVATE_DESCRIPTION" }));

    [Fact]
    public void NotesCannotEndWaitAndCombinedLabelsCountOnce()
    {
        var clock = new WorkflowTimingTests.Clock();
        List<double> completed = [];
        var observation = new LifecycleObservation(clock, (_, seconds) => completed.Add(seconds), _ => { }, (_, _) => { });
        observation.Observe([Bead()]);
        var paused = Bead(labels: ["needs-human", "product-decision-required"]);
        observation.Observe([paused]);
        Assert.Equal(1, observation.Read("paused").Sum(m => m.Value));
        clock.Advance(20);
        observation.Observe([paused with { Payload = JsonSerializer.SerializeToElement(new { comment = "PRIVATE_NOTE" }) }]);
        Assert.Empty(completed);
        clock.Advance(4);
        observation.Observe([Bead()]);
        Assert.Equal(24, Assert.Single(completed));
    }

    [Theory]
    [InlineData("closed", LifecycleLoss.Closed)]
    [InlineData("missing", LifecycleLoss.Missing)]
    [InlineData("changed", LifecycleLoss.Changed)]
    public void AbortDeletionAndLabelChangesAreCensored(string change, int reasonValue)
    {
        var reason = (LifecycleLoss)reasonValue;
        List<LifecycleLoss> losses = [];
        List<double> completed = [];
        var observation = new LifecycleObservation(new WorkflowTimingTests.Clock(), (_, seconds) => completed.Add(seconds), losses.Add, (_, _) => { });
        observation.Observe([Bead()]);
        observation.Observe([Bead(labels: ["needs-human"])]);
        observation.Observe(change == "missing" ? [] : [Bead(labels: [change == "changed" ? "product-decision-required" : "needs-human"], status: change == "closed" ? "closed" : "open")]);
        Assert.Contains(reason, losses);
        observation.Observe([Bead()]); // reopening cannot complete a removed pause
        if (change != "changed") Assert.Empty(completed);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("stale")]
    [InlineData("failure")]
    [InlineData("capacity")]
    public void GapsAndCapacityNeverInventCompleteWaits(string mode)
    {
        var clock = new WorkflowTimingTests.Clock();
        List<double> completed = [];
        var observation = new LifecycleObservation(clock, (_, seconds) => completed.Add(seconds), _ => { }, (_, _) => { }, limit: 1);
        if (mode != "restart") observation.Observe([Bead()]);
        var paused = Bead(labels: ["needs-human"]);
        observation.Observe([paused]);
        if (mode == "stale") clock.Advance(61);
        if (mode == "failure") observation.Unknown(LifecycleLoss.Failure);
        if (mode == "capacity") observation.Observe([paused, Bead("ns-other")]);
        observation.Observe([Bead()]);
        Assert.Empty(completed);
    }

    [Fact]
    public void FirstPassRequiresKnownVisitAndAttemptAndKeepsRepairSeparateFromRetry()
    {
        var observation = new LifecycleObservation(new WorkflowTimingTests.Clock(), (_, _) => { }, _ => { }, (_, _) => { });
        var initial = Bead(labels: ["stage:review"]);
        var state = BeadWorkflowState.Parse(initial) with { Stage = "stage:qa" };
        observation.Observe([initial]); // unknown pre-runtime history, despite zero repairs
        observation.Attempt(initial.Id, "reviewer", "a");
        Assert.Equal(ApprovalCoverage.Unknown, observation.Published(initial.Id, "reviewer", "a", state));
        observation.Attempt(initial.Id, "qa", "b");
        Assert.Equal(ApprovalCoverage.FirstPass, observation.Published(initial.Id, "qa", "b", state with { Stage = "stage:merge" }));
        observation.Attempt(initial.Id, "merger", "c");
        observation.Published(initial.Id, "merger", "c", state with { Stage = "stage:implement", RepairCycles = 1 });
        observation.Attempt(initial.Id, "implementer", "d");
        observation.Published(initial.Id, "implementer", "d", state with { Stage = "stage:review", RepairCycles = 1 });
        observation.Attempt(initial.Id, "reviewer", "e");
        Assert.Equal(ApprovalCoverage.LaterPass, observation.Published(initial.Id, "reviewer", "e", state));
        observation.Attempt(initial.Id, "qa", "f");
        observation.Attempt(initial.Id, "qa", "g");
        Assert.Equal(ApprovalCoverage.LaterPass, observation.Published(initial.Id, "qa", "g", state));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WireMetricsAreUnsampledAndMilestonesExcludePrivateContent(double sampling)
    {
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", sampling), _ => { }, metricIntervalMilliseconds: 100);
        using var runtime = telemetry.EnterRuntime("0123456789abcdef0123456789abcdef");
        telemetry.Lifecycle.Observe([Bead()]);
        telemetry.Lifecycle.Observe([Bead(labels: ["needs-human"])]);
        telemetry.Lifecycle.Observe([Bead()]);
        var state = BeadWorkflowState.Parse(Bead(labels: ["stage:qa"]));
        var result = new AgentStageResult(AgentRole.Reviewer, "ns-proof", "invocation", "PRIVATE_PATH", null, []);
        Assert.Equal(WorkflowResult.Uncertain, telemetry.RecordPublication(result));
        telemetry.RecordPublication(result with { PublicationReceipt = new(AgentRole.Reviewer, AgentOutcomeChoice.Approved, state, null, false, false) });
        await receiver.WaitForSignalAsync("metrics");
        await telemetry.StopAsync(false);
        var names = receiver.Captures.Where(c => c.Signal == "metrics").SelectMany(FirstPathTelemetryTests.DecodeRecords).Select(m => m.Only(1).String).ToArray();
        Assert.Contains("nightweaver.approvals", names);
        Assert.Contains("nightweaver.human_wait.duration", names);
        if (sampling == 0) Assert.DoesNotContain(receiver.Captures, c => c.Signal == "traces");
        else Assert.Contains(receiver.Captures, c => c.Signal == "traces");
        foreach (var capture in receiver.Captures)
        {
            var wire = Encoding.UTF8.GetString(capture.Bytes);
            Assert.DoesNotContain("PRIVATE_", wire);
            if (capture.Signal == "metrics") Assert.DoesNotContain("ns-proof", wire);
        }
    }

    [Fact]
    public void PlanningApprovalUsesItsOwnVisitAndCounterResetDoesNotProveFirstPass()
    {
        var observation = new LifecycleObservation(new WorkflowTimingTests.Clock(), (_, _) => { }, _ => { }, (_, _) => { });
        var product = Bead(labels: ["stage:plan"]) with { IssueType = "feature" };
        observation.Observe([product]);
        var challenge = BeadWorkflowState.Parse(product) with { Stage = "stage:plan-challenge" };
        observation.Published(product.Id, "planner", "p", challenge);
        observation.Attempt(product.Id, "plan_challenger", "c");
        Assert.Equal(ApprovalCoverage.FirstPass, observation.Published(product.Id, "plan_challenger", "c", challenge with { Stage = "stage:plan-approved" }));
        observation.Observe([product with { Labels = ["stage:plan-challenge"], Payload = Bead(repairs: 2).Payload }]);
        observation.Observe([product with { Labels = ["stage:plan-challenge"] }]);
        observation.Attempt(product.Id, "plan_challenger", "after-reset");
        Assert.Equal(ApprovalCoverage.Unknown, observation.Published(product.Id, "plan_challenger", "after-reset", challenge));
    }
}
