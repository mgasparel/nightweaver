using System.Text;
using Nightweaver.Execution;
using Nightweaver.Telemetry;
using static Nightweaver.Tests.OperationalTelemetryTests;

namespace Nightweaver.Tests;

public sealed class WorkflowTimingTests
{
    private const string RuntimeId = "0123456789abcdef0123456789abcdef";
    private const string Canary = "PRIVATE_CLASSIFIER_EVIDENCE_EXCEPTION_BODY";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedPhasesReconcileAndLateDisposalCannotCountTwice(bool cancellation)
    {
        var clock = new Clock();
        Dictionary<WorkflowPhase, double> allocation = [];
        List<double> occupied = [];
        using var timing = new WorkflowTiming(clock, allocation.Add, occupied.Add);
        using var context = timing.Enter();
        clock.Advance(1); // reserved but not running
        using (WorkflowTiming.EnterPhase(WorkflowPhase.Preparation))
        {
            clock.Advance(2);
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Agent)) clock.Advance(3);
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Classification))
            {
                clock.Advance(4);
                // Diagnostic classifier execution stays in classification.
                using (WorkflowTiming.EnterPhase(WorkflowPhase.Classification)) clock.Advance(5);
            }
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Verification)) clock.Advance(6);
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Publication)) clock.Advance(7);
        }
        var finalization = WorkflowTiming.EnterPhase(WorkflowPhase.Finalization);
        clock.Advance(8);
        if (cancellation) timing.Dispose();
        finalization?.Dispose();
        timing.Dispose();
        clock.Advance(100);
        timing.Dispose();
        Assert.Equal(36, Assert.Single(occupied));
        Assert.Equal(occupied[0], allocation.Values.Sum());
        Assert.Equal(2, allocation[WorkflowPhase.Preparation]);
        Assert.Equal(3, allocation[WorkflowPhase.Agent]);
        Assert.Equal(9, allocation[WorkflowPhase.Classification]);
        Assert.Equal(6, allocation[WorkflowPhase.Verification]);
        Assert.Equal(7, allocation[WorkflowPhase.Publication]);
        Assert.Equal(8, allocation[WorkflowPhase.Finalization]);
        Assert.Equal(1, allocation[WorkflowPhase.Other]);
    }

    [Fact]
    public async Task ConcurrentReservationsKeepIndependentAsyncContextsAndRestoreTheirParent()
    {
        var clock = new Clock();
        Dictionary<WorkflowPhase, double> first = [], second = [];
        using var a = new WorkflowTiming(clock, first.Add, _ => { });
        using var b = new WorkflowTiming(clock, second.Add, _ => { });
        using var parent = a.Enter();
        using (WorkflowTiming.EnterPhase(WorkflowPhase.Agent))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = Task.Run(async () =>
            {
                using var context = b.Enter();
                using (WorkflowTiming.EnterPhase(WorkflowPhase.Classification))
                {
                    entered.SetResult();
                    await release.Task;
                }
            }, TestContext.Current.CancellationToken);
            await entered.Task;
            clock.Advance(10);
            release.SetResult();
            await child;
            // The child context cannot change the parent's next phase.
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Verification)) clock.Advance(2);
        }
        a.Dispose();
        b.Dispose();
        Assert.Equal(10, first[WorkflowPhase.Agent]);
        Assert.Equal(2, first[WorkflowPhase.Verification]);
        Assert.Equal(0, first[WorkflowPhase.Classification]);
        Assert.Equal(10, second[WorkflowPhase.Classification]);
        Assert.Equal(0, second[WorkflowPhase.Agent]);
        Assert.Equal(12, first.Values.Sum());
        Assert.Equal(12, second.Values.Sum());
    }

    [Theory]
    [InlineData("success", "success")]
    [InlineData("invalid", "invalid")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("refused", "refused")]
    [InlineData("uncertain", "uncertain")]
    public async Task DiagnosticResultsExportOnlyClosedSchemaAndNeverBecomeBusinessSuccess(string mode, string expected)
    {
        await using var receiver = await Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1),
            _ => { }, metricIntervalMilliseconds: 100);
        using var runtime = telemetry.EnterRuntime(RuntimeId);
        var clock = new Clock();
        Dictionary<WorkflowPhase, double> phases = [];
        using var timing = new WorkflowTiming(clock, phases.Add, _ => { });
        using var context = timing.Enter();
        async Task<int> Action()
        {
            await Task.CompletedTask;
            clock.Advance(1);
            if (mode == "cancelled") throw new OperationCanceledException(Canary);
            if (mode == "refused") throw new AgentStageInputException(Canary);
            if (mode == "uncertain") throw new IOException(Canary);
            return 1;
        }
        try
        {
            await WorkflowTiming.MeasureAsync(WorkflowOperation.LifecycleFallback, WorkflowPhase.Classification,
                Action, _ => Enum.Parse<WorkflowResult>(mode, ignoreCase: true));
        }
        catch (Exception exception) when (exception is OperationCanceledException or AgentStageInputException or IOException)
        {
            Assert.Contains(Canary, exception.Message);
        }
        clock.Advance(2);
        timing.Dispose();
        Assert.Equal(1, phases[WorkflowPhase.Classification]);
        Assert.Equal(2, phases[WorkflowPhase.Other]);
        await receiver.WaitForSignalsAsync();
        await telemetry.StopAsync(false);
        foreach (var capture in receiver.Captures)
        {
            Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(capture.Bytes));
            if (capture.Signal != "traces") continue;
            var tags = Attributes(Assert.Single(FirstPathTelemetryTests.DecodeRecords(capture)), 9);
            Assert.Equal("workflow", tags["operation"]);
            Assert.Equal("LifecycleFallback", tags["kind"]);
            Assert.Equal(expected, tags["outcome"]);
        }
        var metricNames = receiver.Captures.Where(c => c.Signal == "metrics")
            .SelectMany(FirstPathTelemetryTests.DecodeRecords).Select(m => m.Only(1).String).ToArray();
        Assert.Contains("nightweaver.classification.duration", metricNames);
        Assert.Contains("nightweaver.workflow.results", metricNames);
        Assert.DoesNotContain("nightweaver.verified_merge", metricNames);
        Assert.DoesNotContain("nightweaver.published.outcomes", metricNames);
    }

    [Fact]
    public async Task ZeroSamplingPreservesPhaseRetryAndConfirmedRepairMetricsWithoutIdentityDimensions()
    {
        await using var receiver = await Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 0),
            _ => { }, metricIntervalMilliseconds: 100);
        using var runtime = telemetry.EnterRuntime(RuntimeId);
        var clock = new Clock();
        using (var timing = new WorkflowTiming(clock,
            (phase, seconds) => telemetry.RecordPhase("delivery", phase, seconds),
            seconds => telemetry.RecordOccupied("delivery", seconds)))
        {
            using var context = timing.Enter();
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Agent)) clock.Advance(2);
            using (WorkflowTiming.EnterPhase(WorkflowPhase.Classification)) clock.Advance(3);
        }
        telemetry.RecordRetry(AgentOperationalFailureKind.TimedOut);
        var state = new BeadWorkflowState("ns-proof", "task", "in_progress", "stage:implement", false, false, false, 1);
        var result = new AgentStageResult(Nightweaver.Configuration.AgentRole.Reviewer, "ns-proof", "invocation", Canary, null, []);
        Assert.Equal(WorkflowResult.Uncertain, telemetry.RecordPublication(result));
        Assert.Equal(WorkflowResult.Success, telemetry.RecordPublication(result with
        {
            PublicationReceipt = new(Nightweaver.Configuration.AgentRole.Reviewer,
                AgentOutcomeChoice.ChangesRequired, state, "owner", false, false)
        }));
        telemetry.RecordWorkflow(WorkflowOperation.LifecyclePrimary, WorkflowResult.Invalid, 3, RuntimeId);
        await receiver.WaitForSignalAsync("metrics");
        await telemetry.StopAsync(false);
        Assert.DoesNotContain(receiver.Captures, c => c.Signal == "traces");
        var metrics = receiver.Captures.Where(c => c.Signal == "metrics").SelectMany(FirstPathTelemetryTests.DecodeRecords).ToArray();
        foreach (var name in new[] { "nightweaver.phase.seconds", "nightweaver.phase.duration", "nightweaver.slot.occupied",
            "nightweaver.operational.retries", "nightweaver.semantic.repairs", "nightweaver.published.outcomes" })
            Assert.Contains(metrics, m => m.Only(1).String == name);
        foreach (var capture in receiver.Captures)
        {
            var bytes = Encoding.UTF8.GetString(capture.Bytes);
            Assert.DoesNotContain(Canary, bytes);
            if (capture.Signal != "metrics") continue;
            foreach (var key in new[] { "bead.id", "invocation.id", "runtime.id", RuntimeId, "ns-proof" })
                Assert.DoesNotContain(key, bytes);
        }
        Assert.True(OperationalTelemetry.ProjectedSeries < 2000);
    }

    [Fact]
    public async Task OverlappingProvidersCannotUpgradeTheOtherExportersZeroSampling()
    {
        await using var zeroReceiver = await Receiver.StartAsync();
        await using var sampledReceiver = await Receiver.StartAsync();
        await using var zero = new OperationalTelemetry(OperationalHost.Runtime, new(zeroReceiver.Endpoint, "", 0),
            _ => { }, metricIntervalMilliseconds: 100);
        await using var sampled = new OperationalTelemetry(OperationalHost.Runtime, new(sampledReceiver.Endpoint, "", 1),
            _ => { }, metricIntervalMilliseconds: 100);
        zero.RecordWorkflow(WorkflowOperation.LifecyclePrimary, WorkflowResult.Success, 1, RuntimeId);
        sampled.RecordWorkflow(WorkflowOperation.LifecycleFallback, WorkflowResult.Invalid, 1, RuntimeId);
        await zeroReceiver.WaitForSignalAsync("metrics");
        await sampledReceiver.WaitForSignalsAsync();
        await zero.StopAsync(false);
        await sampled.StopAsync(false);
        Assert.DoesNotContain(zeroReceiver.Captures, c => c.Signal == "traces");
        var span = Assert.Single(sampledReceiver.Captures.Where(c => c.Signal == "traces")
            .SelectMany(FirstPathTelemetryTests.DecodeRecords));
        Assert.Equal("LifecycleFallback", Attributes(span, 9)["kind"]);
    }

    internal sealed class Clock : TimeProvider
    {
        private long seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Read(ref seconds);
        internal void Advance(long value) => Interlocked.Add(ref seconds, value);
    }
}
