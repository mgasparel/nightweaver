using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;
using static Nightweaver.Tests.ManualDeliveryPilotTestHelpers;

namespace Nightweaver.Tests;

public sealed class ManualDeliveryPilotConcurrencyTests
{
    [Fact]
    public async Task DeliveryCompletesWhileSingletonMergerIsHeldThenBothTasksMerge()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken,
            // Only seed the two tasks in this scenario. Unrelated gated/repair
            // tasks can consume Delivery slots until the held Merger times out.
            seedProfile: ManualPilotSeedProfile.Empty);
        var heldMergerId = await fixture.CreateMergeGateTaskAsync(
            "QA manual pilot held singleton Merger", TestContext.Current.CancellationToken);
        fixture.MergerGateBeadId = heldMergerId;
        fixture.MergerDiagnosticsBeadId = heldMergerId;
        var runtime = fixture.StartRuntime(typeof(ProjectConfigLoader).Assembly.Location);
        Exception? primaryFailure = null;
        string? lateTaskId = null;
        try
        {
            await fixture.WaitForMergerGateAsync(heldMergerId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var heldMerger = await fixture.ReadBeadAsync(heldMergerId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", heldMerger.GetProperty("status").GetString());
            Assert.Equal("nightweaver:manualqa", heldMerger.GetProperty("assignee").GetString());
            Assert.Contains("stage:merge", heldMerger.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));

            lateTaskId = await fixture.CreateTaskAsync("QA manual pilot Delivery during held Merger",
                TestContext.Current.CancellationToken);
            await WaitForWorkflowAsync(fixture, lateTaskId, expectedStatus: "open", expectedStage: "stage:merge",
                TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "Delivery must keep making progress while the one Merger slot is held.");
            Assert.True(File.Exists(Path.Combine(fixture.GateDirectory, heldMergerId + ".merger.started")));
            var deliveryEvidence = ReadEvidence(fixture.EvidencePath).Where(row => row.BeadId == lateTaskId).ToArray();
            Assert.Contains(deliveryEvidence, row => row.Role == "implementer");
            Assert.Contains(deliveryEvidence, row => row.Role == "reviewer");
            Assert.Contains(deliveryEvidence, row => row.Role == "qa");
            Assert.DoesNotContain(deliveryEvidence, row => row.Role == "merger");

            var mergerInvocations = Nightweaver.Tests.AgentInvocationFixture.ManualPilotEvidenceLog
                .ReadAllLines(fixture.MergerDiagnosticsPath(heldMergerId)).Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }).Where(row => row.GetProperty("bead_id").GetString() == heldMergerId
                    && row.GetProperty("phase").GetString() == "invocation"
                    && row.GetProperty("status").GetString() == "started").ToArray();
            Assert.Single(mergerInvocations);

            fixture.ReleaseMergerGate(heldMergerId);
            await WaitForClosedAsync(fixture, heldMergerId, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
            await WaitForClosedAsync(fixture, lateTaskId, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
            Assert.Contains(heldMergerId,
                await fixture.ReadRemoteMainFileAsync($"merger-verified-{heldMergerId}.txt",
                    TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Contains(lateTaskId,
                await fixture.ReadRemoteMainFileAsync($"merger-verified-{lateTaskId}.txt",
                    TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            fixture.PreserveForFailure();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Held-Merger fixture preserved at {fixture.Root}; native phase evidence: {fixture.MergerDiagnosticsPath(heldMergerId)}");
            primaryFailure = exception;
            throw;
        }
        finally
        {
            fixture.ReleaseMergerGate(heldMergerId);
            try { await fixture.StopRuntimeAsync(force: false, TimeSpan.FromMinutes(2)); }
            catch (Exception cleanupException) when (primaryFailure is not null)
            {
                Console.Error.WriteLine($"Pilot cleanup after held-Merger test failure also failed: {cleanupException}");
            }
        }
    }
}
