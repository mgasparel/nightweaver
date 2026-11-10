using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using Nightweaver.Configuration;
using Nightweaver.Telemetry;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;
using Nightweaver.Tests.ManualDeliveryPilotFixture;
using static Nightweaver.Tests.ManualDeliveryPilotTestHelpers;

namespace Nightweaver.Tests;

public sealed class ManualDeliveryPilotSecondSignalTests
{
    [Fact]
    public async Task SecondConsoleSignalForceStopsHeldAgentAndRestartPreservesWorktreeAndForeignClaim()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken,
            seedProfile: ManualPilotSeedProfile.ParentlessOnly);
        fixture.RetainSentinelBeadId = fixture.ParentlessTaskId;
        var foreignClaimId = await fixture.CreateForeignClaimAsync(TestContext.Current.CancellationToken);
        var runtimeAssembly = typeof(ProjectConfigLoader).Assembly.Location;
        var firstRuntime = fixture.StartRuntime(runtimeAssembly);
        Exception? primaryFailure = null;
        try
        {
            await fixture.WaitForGateAsync(fixture.ParentlessTaskId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var firstAgentPid = int.Parse(File.ReadAllText(Path.Combine(fixture.GateDirectory,
                fixture.ParentlessTaskId + ".started")), CultureInfo.InvariantCulture);
            var worktree = fixture.WorktreePath(fixture.ParentlessTaskId);
            var retainedSentinel = Path.Combine(worktree, "manual-pilot-retained-sentinel.txt");
            Assert.True(File.Exists(retainedSentinel), "The gated Agent must write the uncommitted recovery sentinel before either stop signal.");
            var branchBefore = (await fixture.ReadWorktreeBranchAsync(fixture.ParentlessTaskId,
                TestContext.Current.CancellationToken)).Trim();
            Assert.False(string.IsNullOrWhiteSpace(branchBefore));
            var firstPaths = new HostPaths(fixture.NightweaverRoot);
            var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), firstPaths);
            var firstSnapshot = WaitForLiveSnapshot(firstPaths, project, fixture.ParentlessTaskId,
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            await fixture.SendRuntimeConsoleBreakAsync(TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(firstRuntime.HasExited, "The first signal must start drain without killing the runtime.");
            Assert.True(IsProcessRunning(firstAgentPid), "The first signal must keep the current native turn alive.");
            await fixture.SendRuntimeConsoleBreakAsync(TestContext.Current.CancellationToken);
            var forcedExit = await firstRuntime.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.NotEqual(0, forcedExit);
            await WaitForProcessExitAsync(firstAgentPid, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var interrupted = await fixture.ReadBeadAsync(fixture.ParentlessTaskId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", interrupted.GetProperty("status").GetString());
            Assert.Equal("nightweaver:manualqa", interrupted.GetProperty("assignee").GetString());
            Assert.True(File.Exists(retainedSentinel), "Force stop must preserve uncommitted worktree content.");
            Assert.Equal(branchBefore, (await fixture.ReadWorktreeBranchAsync(fixture.ParentlessTaskId,
                TestContext.Current.CancellationToken)).Trim());
            await AssertForeignClaimUnchangedAsync(fixture, foreignClaimId, TestContext.Current.CancellationToken);

            File.Delete(Path.Combine(fixture.GateDirectory, fixture.ParentlessTaskId + ".started"));
            var secondRuntime = fixture.StartRuntime(runtimeAssembly);
            await fixture.WaitForGateAsync(fixture.ParentlessTaskId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var secondAgentPid = int.Parse(File.ReadAllText(Path.Combine(fixture.GateDirectory,
                fixture.ParentlessTaskId + ".started")), CultureInfo.InvariantCulture);
            var secondPaths = new HostPaths(fixture.NightweaverRoot);
            var secondProject = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), secondPaths);
            var secondSnapshot = WaitForLiveSnapshot(secondPaths, secondProject, fixture.ParentlessTaskId,
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.NotEqual(firstSnapshot.RuntimeId, secondSnapshot.RuntimeId);
            Assert.NotEqual(firstAgentPid, secondAgentPid);
            Assert.Equal(secondAgentPid, secondSnapshot.ProcessId);
            Assert.True(File.Exists(retainedSentinel), "Restart must reuse the existing worktree and retain its uncommitted sentinel.");
            Assert.Equal(branchBefore, (await fixture.ReadWorktreeBranchAsync(fixture.ParentlessTaskId,
                TestContext.Current.CancellationToken)).Trim());
            await AssertForeignClaimUnchangedAsync(fixture, foreignClaimId, TestContext.Current.CancellationToken);

            fixture.ReleaseGate(fixture.ParentlessTaskId);
            var safelyPaused = await WaitForNeedsHumanAsync(fixture, fixture.ParentlessTaskId,
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal("open", safelyPaused.GetProperty("status").GetString());
            Assert.Contains("needs-human", safelyPaused.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            Assert.Contains("stage:implement", safelyPaused.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            Assert.Contains($"Delivery slot '{fixture.ParentlessTaskId}' released local capacity (Escalated; claim handoff=True)",
                fixture.ReadRuntimeLog(), StringComparison.Ordinal);
            Assert.True(File.Exists(retainedSentinel), "Recovery must keep the dirty sentinel after safely withholding approval.");
            Assert.Equal(branchBefore, (await fixture.ReadWorktreeBranchAsync(fixture.ParentlessTaskId,
                TestContext.Current.CancellationToken)).Trim());
            await AssertForeignClaimUnchangedAsync(fixture, foreignClaimId, TestContext.Current.CancellationToken);
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
                Console.Error.WriteLine($"Pilot cleanup after second-signal recovery failure also failed: {cleanupException}");
            }
        }
    }
}

internal static class ManualDeliveryPilotTestHelpers
{
    internal static FactorySnapshotObservation WaitForLiveSnapshot(HostPaths paths, ProjectConfig project,
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

    internal static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
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

    internal static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static (string BeadId, string Role)[] ReadEvidence(string evidencePath) =>
        Nightweaver.Tests.AgentInvocationFixture.ManualPilotEvidenceLog.ReadAllLines(evidencePath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return (document.RootElement.GetProperty("bead_id").GetString()!,
                document.RootElement.GetProperty("role").GetString()!);
        }).ToArray();

    internal static async Task AssertForeignClaimUnchangedAsync(ManualPilot fixture, string beadId,
        CancellationToken cancellationToken)
    {
        var foreign = await fixture.ReadBeadAsync(beadId, cancellationToken);
        Assert.Equal("in_progress", foreign.GetProperty("status").GetString());
        Assert.Equal("foreign:operator", foreign.GetProperty("assignee").GetString());
        Assert.Contains("stage:implement", foreign.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
    }

    internal static async Task WaitForClosedAsync(ManualPilot fixture, string beadId,
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

    internal static async Task<JsonElement> WaitForNeedsHumanAsync(ManualPilot fixture, string beadId,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = await fixture.ReadBeadAsync(beadId, cancellationToken);
            if (bead.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                && labels.EnumerateArray().Any(label => label.GetString() == "needs-human")
                && bead.GetProperty("status").GetString() == "open")
                return bead;
            if (fixture.RuntimeProcess is { HasExited: true } process)
                throw new InvalidOperationException($"Runtime exited {process.ExitCode} before '{beadId}' reached needs-human: {fixture.ReadRuntimeLog()}");
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException($"Bead '{beadId}' was not safely paused within {timeout}: {fixture.ReadRuntimeLog()}");
    }

    internal static async Task<JsonElement> WaitForWorkflowAsync(ManualPilot fixture, string beadId,
        string expectedStatus, string expectedStage, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = await fixture.ReadBeadAsync(beadId, cancellationToken);
            var status = bead.GetProperty("status").GetString();
            var stage = bead.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? labels.EnumerateArray().Select(label => label.GetString()).FirstOrDefault(label => label?.StartsWith("stage:", StringComparison.Ordinal) == true)
                : null;
            if (status == expectedStatus && stage == expectedStage) return bead;
            if (bead.TryGetProperty("labels", out labels) && labels.ValueKind == JsonValueKind.Array
                && labels.EnumerateArray().Any(label => label.GetString() == "needs-human"))
                throw new InvalidOperationException($"Bead '{beadId}' escalated before reaching '{expectedStage}': {fixture.ReadRuntimeLog()}");
            if (fixture.RuntimeProcess is { HasExited: true } process)
                throw new InvalidOperationException($"Runtime exited {process.ExitCode} before '{beadId}' reached '{expectedStage}': {fixture.ReadRuntimeLog()}");
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException($"Bead '{beadId}' did not reach '{expectedStage}' within {timeout}: {fixture.ReadRuntimeLog()}");
    }

    internal sealed record FactorySnapshotObservation(string RuntimeId, int ProcessId, string State);
}
