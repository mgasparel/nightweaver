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
            cancellationToken: TestContext.Current.CancellationToken,
            includeExhaustionTask: true);
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
            var exhausted = await WaitForNeedsHumanAsync(fixture, fixture.ExhaustionTaskId,
                TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            // Two configured repairs follow the initial attempt; the third rejected
            // review records the exhausted counter at max_repair_cycles + 1.
            Assert.Equal(3, exhausted.GetProperty("metadata").GetProperty("repair_cycles").GetInt32());
            Assert.Contains("needs-human", exhausted.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            Assert.Contains("stage:implement", exhausted.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            Assert.False(exhausted.TryGetProperty("assignee", out _),
                "Automatic review-budget exhaustion must release its runtime claim when it pauses.");

            // All initial work is now closed or human-paused. This newly authored task
            // proves the same continuous runtime discovers work after reaching idle.
            var idleDiscoveredTaskId = await fixture.CreateTaskAsync(
                "QA manual pilot task authored after runtime idle", TestContext.Current.CancellationToken);
            await WaitForClosedAsync(fixture, idleDiscoveredTaskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.Contains(idleDiscoveredTaskId,
                await fixture.ReadRemoteMainFileAsync($"manual-delivery-{idleDiscoveredTaskId}.txt",
                    TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.False(runtime.HasExited, "The continuous runtime must remain available after idle discovery and completion.");

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

            var evidence = Nightweaver.Tests.AgentInvocationFixture.ManualPilotEvidenceLog.ReadAllLines(fixture.EvidencePath)
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
            Assert.Equal(3, evidence.Count(row => row.GetProperty("bead_id").GetString() == fixture.ExhaustionTaskId
                && row.GetProperty("role").GetString() == "implementer"));
            Assert.Equal(3, evidence.Count(row => row.GetProperty("bead_id").GetString() == fixture.ExhaustionTaskId
                && row.GetProperty("role").GetString() == "reviewer"));
            Assert.DoesNotContain(evidence, row => row.GetProperty("bead_id").GetString() == fixture.ExhaustionTaskId
                && row.GetProperty("role").GetString() is "qa" or "merger");
            Assert.Contains(evidence, row => row.GetProperty("bead_id").GetString() == idleDiscoveredTaskId
                && row.GetProperty("role").GetString() == "implementer");
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
    public async Task AbruptRuntimeCrashKillsNativeAgentAndRestartRecoversTheClaim()
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

            await fixture.CrashRuntimeAsync(TimeSpan.FromSeconds(30));
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

    [Fact]
    public async Task FirstConsoleSignalDrainsHeldManualPilotTurnWithoutLaunchingNextRole()
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

            var evidenceBefore = ReadEvidence(fixture.EvidencePath);
            Assert.Single(evidenceBefore, row => row.BeadId == fixture.ParentlessTaskId
                && row.Role == "implementer");
            var agentPid = int.Parse(File.ReadAllText(Path.Combine(fixture.GateDirectory,
                fixture.ParentlessTaskId + ".started")), CultureInfo.InvariantCulture);
            var runtimeLogOffset = new FileInfo(fixture.RuntimeLogPath).Length;
            await fixture.SendRuntimeConsoleBreakAsync(TestContext.Current.CancellationToken);
            await fixture.WaitForGracefulStopAcceptedAsync(runtimeLogOffset, TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.False(runtime.HasExited, "The first real console signal must drain rather than kill the held runtime.");
            Assert.True(IsProcessRunning(agentPid), "The active native Agent must remain alive until the current turn settles.");
            var evidenceDuringDrain = ReadEvidence(fixture.EvidencePath)
                .Where(row => row.BeadId == fixture.ParentlessTaskId).ToArray();
            Assert.Single(evidenceDuringDrain, row => row.Role == "implementer");
            Assert.DoesNotContain(evidenceDuringDrain, row => row.Role != "implementer");
            var activeClaim = await fixture.ReadBeadAsync(fixture.ParentlessTaskId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", activeClaim.GetProperty("status").GetString());
            Assert.Equal("nightweaver:manualqa", activeClaim.GetProperty("assignee").GetString());

            fixture.ReleaseGate(fixture.ParentlessTaskId);
            Assert.Equal(0, await runtime.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));

            var drained = await fixture.ReadBeadAsync(fixture.ParentlessTaskId, TestContext.Current.CancellationToken);
            Assert.Equal("open", drained.GetProperty("status").GetString());
            Assert.False(drained.TryGetProperty("assignee", out _),
                "bd show omits assignee after the graceful handoff clears it.");
            Assert.Contains("stage:review", drained.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            var evidenceAfter = ReadEvidence(fixture.EvidencePath).Where(row => row.BeadId == fixture.ParentlessTaskId).ToArray();
            Assert.Single(evidenceAfter, row => row.Role == "implementer");
            Assert.DoesNotContain(evidenceAfter, row => row.Role != "implementer");
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
                Console.Error.WriteLine($"Pilot cleanup after first-signal drain failure also failed: {cleanupException}");
            }
        }
    }

    [Fact]
    public async Task SecondConsoleSignalForceStopsHeldAgentAndRestartPreservesWorktreeAndForeignClaim()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken);
        fixture.RetainSentinelBeadId = fixture.ParentlessTaskId;
        var foreignClaimId = await fixture.CreateForeignClaimAsync(TestContext.Current.CancellationToken);
        var runtimeAssembly = typeof(ProjectConfigLoader).Assembly.Location;
        var firstRuntime = fixture.StartRuntime(runtimeAssembly);
        Exception? primaryFailure = null;
        try
        {
            await fixture.WaitForGateAsync(fixture.ParentlessTaskId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            await WaitForClosedAsync(fixture, fixture.ProductTaskId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            await WaitForClosedAsync(fixture, fixture.ProductId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task DeliveryCompletesWhileSingletonMergerIsHeldThenBothTasksMerge()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken);
        var heldMergerId = await fixture.CreateMergeGateTaskAsync(
            "QA manual pilot held singleton Merger", TestContext.Current.CancellationToken);
        fixture.MergerGateBeadId = heldMergerId;
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
            primaryFailure = exception;
            throw;
        }
        finally
        {
            fixture.ReleaseMergerGate(heldMergerId);
            fixture.ReleaseGate(fixture.ParentlessTaskId);
            try { await fixture.StopRuntimeAsync(force: false, TimeSpan.FromMinutes(2)); }
            catch (Exception cleanupException) when (primaryFailure is not null)
            {
                Console.Error.WriteLine($"Pilot cleanup after held-Merger test failure also failed: {cleanupException}");
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

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static (string BeadId, string Role)[] ReadEvidence(string evidencePath) =>
        Nightweaver.Tests.AgentInvocationFixture.ManualPilotEvidenceLog.ReadAllLines(evidencePath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return (document.RootElement.GetProperty("bead_id").GetString()!,
                document.RootElement.GetProperty("role").GetString()!);
        }).ToArray();

    private static async Task AssertForeignClaimUnchangedAsync(ManualPilot fixture, string beadId,
        CancellationToken cancellationToken)
    {
        var foreign = await fixture.ReadBeadAsync(beadId, cancellationToken);
        Assert.Equal("in_progress", foreign.GetProperty("status").GetString());
        Assert.Equal("foreign:operator", foreign.GetProperty("assignee").GetString());
        Assert.Contains("stage:implement", foreign.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
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

    private static async Task<JsonElement> WaitForNeedsHumanAsync(ManualPilot fixture, string beadId,
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

    private static async Task<JsonElement> WaitForWorkflowAsync(ManualPilot fixture, string beadId,
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

    private sealed record FactorySnapshotObservation(string RuntimeId, int ProcessId, string State);
}
