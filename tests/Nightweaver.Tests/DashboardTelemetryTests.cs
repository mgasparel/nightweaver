using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using static Nightweaver.Tests.OperationalTelemetryTests;

namespace Nightweaver.Tests;

// Component load, not another native journey. The retained first-path smoke owns Git/bd/Agent correlation.
public sealed class DashboardTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedExportMeasuresBoundedVolumeAndUnavailableReceiver(bool unavailable)
    {
        var output = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIRST_PATH_EVIDENCE");
        var forward = Environment.GetEnvironmentVariable("NIGHTWEAVER_FIRST_PATH_OTLP");
        await using var receiver = await Receiver.StartAsync(status: unavailable ? 503 : 200,
            forward: !unavailable && forward is not null ? new Uri(forward) : null);
        var timer = Stopwatch.StartNew();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var runtime = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1), diagnostics.Enqueue,
            metricIntervalMilliseconds: 100);
        await using var web = new OperationalTelemetry(OperationalHost.Web, new(receiver.Endpoint, "", 1), diagnostics.Enqueue,
            metricIntervalMilliseconds: 100);
        using var context = runtime.EnterRuntime(new string('a', 32));
        var bead = new BeadSnapshot("ns-load", "open", "task", 2, DateTimeOffset.UnixEpoch, null, [],
            JsonSerializer.SerializeToElement(new { description = "PRIVATE_PROMPT_SOURCE_TRANSCRIPT_EXCEPTION" }));
        runtime.Lifecycle.Observe([bead]);
        runtime.Lifecycle.Observe([bead with { Labels = ["needs-human"] }]);
        runtime.Lifecycle.Observe([bead]);
        var state = new BeadWorkflowState("ns-load", "task", "in_progress", "stage:qa", false, false, false, 0);
        var result = new AgentStageResult(AgentRole.Reviewer, "ns-load", "load-approval", "PRIVATE_PROMPT_SOURCE_TRANSCRIPT_EXCEPTION", null, []);
        runtime.RecordPublication(result with { PublicationReceipt = new(AgentRole.Reviewer, AgentOutcomeChoice.Approved, state, null, false, false) });
        runtime.RecordPublication(result with { PublicationReceipt = new(AgentRole.Reviewer, AgentOutcomeChoice.ChangesRequired, state, null, false, false) });
        runtime.RecordRetry(AgentOperationalFailureKind.TimedOut);
        foreach (var pool in new[] { "planning", "delivery", "merger" })
        {
            var observation = runtime.ObservePool(pool, 3, () => false);
            observation.Observe([]);
            observation.Observe(["ns-queued"]);
            observation.Reserve("ns-queued", null);
            observation.Observe(["ns-next"]);
        }
        // Representative exported values use the real schema. Accelerated arrivals exhaust admission
        // without advancing the transport clock or inventing monthly receiver throughput.
        Parallel.For(0, 1024, index =>
        {
            foreach (var phase in Enum.GetValues<WorkflowPhase>()) runtime.RecordPhase("delivery", phase, 0.01);
            runtime.RecordOccupied("delivery", 0.07);
            runtime.RecordInvocation("implementer", "completed", 0.07, "ns-load", new string('a', 32), $"load-{index}");
            runtime.RecordWorkflow(WorkflowOperation.LifecyclePrimary, WorkflowResult.Success, 0.01, new string('a', 32));
            web.RecordObservation("tracker", "success", 0.01);
        });
        web.SetObservationHealth("tracker", [("healthy", 1), ("stale", 65)]);
        await receiver.WaitForSignalsAsync();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!receiver.Captures.Where(c => c.Signal == "metrics")
                .SelectMany(ObservationTelemetryAssertions.Metrics).Any(m => m.Only(1).String == "nightweaver.telemetry.dropped"))
                await Task.Delay(20, deadline.Token);
        foreach (var pool in new[] { "planning", "delivery", "merger" })
            runtime.ObservePool(pool, 3, () => false).Unknown(WaitCensor.Failure);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!receiver.Captures.Where(c => c.Signal == "metrics")
                .SelectMany(ObservationTelemetryAssertions.Metrics)
                .Where(m => m.Only(1).String == "nightweaver.pool.fresh")
                .SelectMany(m => Message.Parse(m.Only(5).Bytes).Fields.Where(f => f.Number == 1))
                .Any(f => BitConverter.ToDouble(Message.Parse(f.Bytes).Only(4).Bytes) == 0))
                await Task.Delay(20, deadline.Token);
        await runtime.StopAsync(false);
        await web.StopAsync(false);
        timer.Stop();
        var captures = receiver.Captures.ToArray();
        Assert.All(captures, c => Assert.DoesNotContain("PRIVATE_PROMPT_SOURCE_TRANSCRIPT_EXCEPTION", Encoding.UTF8.GetString(c.Bytes)));
        Assert.All(captures.Where(c => c.Signal != "metrics"), c => Assert.InRange(c.Bytes.Length, 1, 2048));
        Assert.All(captures.Where(c => c.Signal == "metrics"), c => Assert.InRange(c.Bytes.Length, 1, 65536));
        foreach (var signal in new[] { "traces", "logs" })
            Assert.True(captures.Count(c => c.Signal == signal) <= 32 + timer.Elapsed.TotalMinutes * 32,
                "Two independently bounded process budgets include initial burst credit.");
        foreach (var capture in captures.Where(c => c.Signal == "metrics"))
        {
            Assert.DoesNotContain("ns-load", Encoding.UTF8.GetString(capture.Bytes));
            Assert.DoesNotContain(new string('a', 32), Encoding.UTF8.GetString(capture.Bytes));
        }
        if (unavailable) Assert.Contains(diagnostics, message => message.Contains("/export", StringComparison.Ordinal));
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, unavailable ? "unavailable.json" : "load.json"), JsonSerializer.Serialize(new
            {
                scenario = "1024 parallel component arrivals per service; runtime plus Web", unavailable,
                elapsedSeconds = timer.Elapsed.TotalSeconds, diagnostics = diagnostics.ToArray(),
                signals = captures.GroupBy(c => c.Signal).Select(group => new
                {
                    signal = group.Key, requests = group.Count(), bytes = group.Sum(c => c.Bytes.Length),
                    records = group.Sum(c => FirstPathTelemetryTests.DecodeRecords(c).Count()),
                    maxRequestBytes = group.Max(c => c.Bytes.Length),
                    burstExtrapolated31DayGB = group.Sum(c => c.Bytes.Length) / timer.Elapsed.TotalSeconds * 2678400 / 1e9,
                    measuredRecordSizeSteady31DayGB = group.Key == "metrics" ? (double?)null :
                        2.0 * group.Max(c => c.Bytes.Length) * 16 * 44640 / 1e9
                }),
                traceLog31DayTransportCeilingGB = 2.0 * (32768 * (44640 + 1)) / 1e9,
                hostedAcceptance = "pending; not authorized"
            }), TestContext.Current.CancellationToken);
        }
    }
}
