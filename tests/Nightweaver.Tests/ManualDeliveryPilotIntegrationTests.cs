using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using Nightweaver.Configuration;
using Nightweaver.Telemetry;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;

namespace Nightweaver.Tests;

public sealed class ManualDeliveryPilotIntegrationTests
{
    [Fact]
    public async Task ContinuousRuntimeRepairsMergesAndClosesParentlessAndPlannedProductTasks()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken);
        var runtime = fixture.StartRuntime(typeof(ProjectConfigLoader).Assembly.Location);
        Exception? primaryFailure = null;
        try
        {
            await fixture.WaitForGateAsync(fixture.ParentlessTaskId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var paths = new HostPaths(fixture.NightweaverRoot);
            var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
            var initial = WaitForLiveSnapshot(paths, project, fixture.ParentlessTaskId, TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var gatedAgentPid = int.Parse(File.ReadAllText(Path.Combine(fixture.GateDirectory,
                fixture.ParentlessTaskId + ".started")), CultureInfo.InvariantCulture);
            Assert.Equal(gatedAgentPid, initial.ProcessId);
            Assert.Equal("running", initial.State);

            fixture.ReleaseGate(fixture.ParentlessTaskId);
            await WaitForClosedAsync(fixture, fixture.ParentlessTaskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            await WaitForClosedAsync(fixture, fixture.ProductTaskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            await WaitForClosedAsync(fixture, fixture.ProductId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var parentlessFile = await fixture.ReadRemoteMainFileAsync($"manual-delivery-{fixture.ParentlessTaskId}.txt",
                TestContext.Current.CancellationToken);
            var linkedFile = await fixture.ReadRemoteMainFileAsync($"manual-delivery-{fixture.ProductTaskId}.txt",
                TestContext.Current.CancellationToken);
            var parentlessMerge = await fixture.ReadRemoteMainFileAsync($"merger-verified-{fixture.ParentlessTaskId}.txt",
                TestContext.Current.CancellationToken);
            var linkedMerge = await fixture.ReadRemoteMainFileAsync($"merger-verified-{fixture.ProductTaskId}.txt",
                TestContext.Current.CancellationToken);
            Assert.Contains(fixture.ParentlessTaskId, parentlessFile, StringComparison.Ordinal);
            Assert.Contains(fixture.ProductTaskId, linkedFile, StringComparison.Ordinal);
            Assert.Contains(fixture.ParentlessTaskId, parentlessMerge, StringComparison.Ordinal);
            Assert.Contains(fixture.ProductTaskId, linkedMerge, StringComparison.Ordinal);
            Assert.False(Directory.Exists(fixture.WorktreePath(fixture.ParentlessTaskId)),
                "Verified closed work should be cleaned up after the merger reaches origin/main.");

            var evidence = File.ReadAllLines(fixture.EvidencePath)
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToArray();
            Assert.Contains(evidence, row => row.GetProperty("bead_id").GetString() == fixture.ParentlessTaskId
                && row.GetProperty("role").GetString() == "implementer");
            Assert.Contains(evidence, row => row.GetProperty("bead_id").GetString() == fixture.ProductTaskId
                && row.GetProperty("role").GetString() == "reviewer");
            Assert.True(evidence.Count(row => row.GetProperty("bead_id").GetString() == fixture.ProductTaskId
                    && row.GetProperty("role").GetString() == "implementer") >= 2,
                "The single reviewer rejection must return the linked task through Delivery repair.");
        }
        catch (Exception exception)
        {
            fixture.PreserveForFailure();
            primaryFailure = exception;
            throw;
        }
        finally
        {
            fixture.ReleaseGate(fixture.ParentlessTaskId);
            try { await fixture.StopRuntimeAsync(force: false, TimeSpan.FromMinutes(2)); }
            catch (Exception cleanupException) when (primaryFailure is not null)
            {
                Console.Error.WriteLine($"Pilot cleanup after the test failure also failed: {cleanupException}");
            }
        }
    }

    [Fact]
    public async Task ForcedRuntimeStopKillsNativeAgentAndRestartRecoversTheClaim()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken);
        var runtimeAssembly = typeof(ProjectConfigLoader).Assembly.Location;
        Exception? primaryFailure = null;
        try
        {
            var firstRuntime = fixture.StartRuntime(runtimeAssembly);
            var firstRuntimePid = firstRuntime.Id;
            await fixture.WaitForGateAsync(fixture.ParentlessTaskId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var firstAgentPid = int.Parse(File.ReadAllText(Path.Combine(fixture.GateDirectory,
                fixture.ParentlessTaskId + ".started")), CultureInfo.InvariantCulture);
            var paths = new HostPaths(fixture.NightweaverRoot);
            var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
            var firstSnapshot = WaitForLiveSnapshot(paths, project, fixture.ParentlessTaskId,
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(firstAgentPid, firstSnapshot.ProcessId);

            var claimed = await fixture.ReadBeadAsync(fixture.ParentlessTaskId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", claimed.GetProperty("status").GetString());
            Assert.Equal("nightweaver:manualqa", claimed.GetProperty("assignee").GetString());

            await fixture.StopRuntimeAsync(force: true, TimeSpan.FromSeconds(30));
            await WaitForProcessExitAsync(firstRuntimePid, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await WaitForProcessExitAsync(firstAgentPid, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var afterCrash = await fixture.ReadBeadAsync(fixture.ParentlessTaskId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", afterCrash.GetProperty("status").GetString());
            Assert.Equal("nightweaver:manualqa", afterCrash.GetProperty("assignee").GetString());

            File.Delete(Path.Combine(fixture.GateDirectory, fixture.ParentlessTaskId + ".started"));
            var secondRuntime = fixture.StartRuntime(runtimeAssembly);
            await fixture.WaitForGateAsync(fixture.ParentlessTaskId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var secondAgentPid = int.Parse(File.ReadAllText(Path.Combine(fixture.GateDirectory,
                fixture.ParentlessTaskId + ".started")), CultureInfo.InvariantCulture);
            var secondSnapshot = WaitForLiveSnapshot(paths, project, fixture.ParentlessTaskId,
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.NotEqual(firstSnapshot.RuntimeId, secondSnapshot.RuntimeId);
            Assert.NotEqual(firstAgentPid, secondAgentPid);
            Assert.Equal(secondAgentPid, secondSnapshot.ProcessId);

            var recovered = await fixture.ReadBeadAsync(fixture.ParentlessTaskId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", recovered.GetProperty("status").GetString());
            Assert.Equal("nightweaver:manualqa", recovered.GetProperty("assignee").GetString());

            fixture.ReleaseGate(fixture.ParentlessTaskId);
            await WaitForClosedAsync(fixture, fixture.ParentlessTaskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.False(secondRuntime.HasExited, "The restarted runtime should remain active after recovering and closing the interrupted task.");
        }
        catch (Exception exception)
        {
            fixture.PreserveForFailure();
            primaryFailure = exception;
            throw;
        }
        finally
        {
            fixture.ReleaseGate(fixture.ParentlessTaskId);
            try { await fixture.StopRuntimeAsync(force: false, TimeSpan.FromMinutes(2)); }
            catch (Exception cleanupException) when (primaryFailure is not null)
            {
                Console.Error.WriteLine($"Pilot cleanup after the force-stop/restart test failure also failed: {cleanupException}");
            }
        }
    }

    private static FactorySnapshotObservation WaitForLiveSnapshot(HostPaths paths, ProjectConfig project,
        string beadId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reader = new FactoryTelemetryReader(paths, project);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = reader.Read();
            if (observation.Availability == FactoryTelemetryAvailability.Live
                && observation.Snapshot is { } snapshot)
            {
                var slot = snapshot.Delivery.Slots.FirstOrDefault(candidate => candidate.BeadId == beadId);
                if (slot is { Invocation.ProcessId: > 0 } active)
                    return new(snapshot.RuntimeId, active.Invocation!.ProcessId!.Value, active.Status.ToLowerInvariant());
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"The live factory snapshot did not report invocation for '{beadId}'.");
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            await Task.Delay(50, cancellationToken);
        }
        throw new TimeoutException($"Process {processId} remained alive after its owning runtime was force-stopped.");
    }

    private static async Task WaitForClosedAsync(ManualPilot fixture, string beadId,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        string lastStatus = "unknown";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = await fixture.ReadBeadAsync(beadId, cancellationToken);
            lastStatus = bead.GetProperty("status").GetString() ?? "unknown";
            if (lastStatus == "closed") return;
            if (bead.TryGetProperty("labels", out var labels)
                && labels.ValueKind == JsonValueKind.Array
                && labels.EnumerateArray().Any(label => label.GetString() == "needs-human"))
                throw new InvalidOperationException($"Bead '{beadId}' escalated to needs-human before closure: {fixture.ReadRuntimeLog()}");
            var runtimeLog = fixture.ReadRuntimeLog();
            if (runtimeLog.Contains($"Delivery slot '{beadId}' released local capacity (Failed; claim handoff=False)", StringComparison.Ordinal))
                throw new InvalidOperationException($"Delivery for '{beadId}' failed without handing off its claim: {runtimeLog}");
            if (fixture.RuntimeProcess is { HasExited: true } process)
                throw new InvalidOperationException($"Runtime exited {process.ExitCode} while '{beadId}' remained '{lastStatus}': {runtimeLog}");
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"Bead '{beadId}' was not closed within {timeout}; last status '{lastStatus}'. Runtime output: {fixture.ReadRuntimeLog()}");
    }

    private sealed record FactorySnapshotObservation(string RuntimeId, int ProcessId, string State);
}
