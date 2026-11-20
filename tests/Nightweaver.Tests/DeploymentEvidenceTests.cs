using System.Text;
using System.Text.Json;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class DeploymentEvidenceTests
{
    private static DeploymentObservation Observation(DeploymentIdentity identity) => new(1, DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString("N"), 123, identity, DeploymentObservationTests.Owner(identity) with { Message = "SECRET_CANARY" },
        DeploymentHealth.Failed, "owner-exited", false, new(OwnerProcessState.Exited, true), null, null);

    [Theory]
    [InlineData("captured", true)]
    [InlineData("partial", false)]
    [InlineData("error", false)]
    [InlineData("timeout", false)]
    [InlineData("unconfirmed", false)]
    public async Task RetainsTriggerBeforeCaptureAndBlocksIncompleteCapture(string result, bool ready)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        var recorder = new DeploymentIncidentRecorder(paths, project, store, (_, _) =>
        {
            var pending = store.Read()!;
            Assert.Equal(IncidentCaptureState.Pending, pending.ContinuingIncident!.Capture.State);
            Assert.Equal(observation.Owner!.RunId, pending.LastObservation.Owner!.RunId);
            Assert.Equal(2, pending.ContinuingIncident.Attempts);
            return result switch
            {
                "error" => throw new IOException("SECRET_CANARY"),
                "timeout" => throw new TimeoutException("SECRET_CANARY"),
                "partial" => Task.FromResult(new IncidentCapture(IncidentCaptureState.Failed, directory.Path, "capture-source-errors")),
                "unconfirmed" => Task.FromResult(new IncidentCapture(IncidentCaptureState.Pending, null, null)),
                _ => Task.FromResult(new IncidentCapture(IncidentCaptureState.Captured, directory.Path, null))
            };
        });
        var evidence = await recorder.PrepareAsync(observation, 2, ResourceIncidentSignal.NativeTimeouts, TestContext.Current.CancellationToken);
        Assert.Equal(ready, evidence.ReadyForReconciliation);
        var retained = store.Read()!;
        Assert.Single(retained.History);
        Assert.Equal(evidence.Incident.Id, retained.ContinuingIncident!.Id);
        Assert.Equal(ready ? IncidentCaptureState.Captured : IncidentCaptureState.Failed, retained.ContinuingIncident.Capture.State);
        Assert.DoesNotContain("SECRET_CANARY", await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("uncertain-process")]
    [InlineData("reused-pid")]
    [InlineData("live-owner")]
    [InlineData("foreign-lock")]
    [InlineData("unknown")]
    [InlineData("draining")]
    public async Task CaptureDoesNotManufactureNativeOrHealthAuthority(string scenario)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        observation = scenario switch
        {
            "uncertain-process" => observation with { Process = new(OwnerProcessState.Exited, null) },
            "reused-pid" => observation with { Process = new(OwnerProcessState.Reused, null) },
            "live-owner" => observation with { Process = new(OwnerProcessState.Alive, false) },
            "foreign-lock" => observation with { OwnerLockHeld = true },
            "draining" => observation with { Health = DeploymentHealth.Draining },
            _ => observation with { Health = DeploymentHealth.Unknown }
        };
        var recorder = new DeploymentIncidentRecorder(paths, project, new(paths, project, identity),
            (_, _) => throw new InvalidOperationException("No required resource signal"));
        var evidence = await recorder.PrepareAsync(observation, 1, ResourceIncidentSignal.None, TestContext.Current.CancellationToken);
        Assert.False(evidence.ReadyForReconciliation);
    }

    [Fact]
    public async Task HistoryIsBoundedAndRecoveryDoesNotEraseContinuingFailure()
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        var recorder = new DeploymentIncidentRecorder(paths, project, store, (_, _) => throw new InvalidOperationException());
        for (var index = 0; index < 34; index++)
            await recorder.PrepareAsync(observation, 2, ResourceIncidentSignal.None, TestContext.Current.CancellationToken);
        var last = store.Read()!;
        Assert.Equal(32, last.History.Length);
        Assert.Equal(2, last.Discarded);
        var firstFailure = last.ContinuingIncident;
        await store.RetainAsync(observation with { Health = DeploymentHealth.Unknown, Reason = "gap" }, null, TestContext.Current.CancellationToken);
        Assert.Equal(firstFailure, store.Read()!.ContinuingIncident);
        await store.RetainAsync(observation with { Health = DeploymentHealth.Healthy }, null, TestContext.Current.CancellationToken);
        var recovered = store.Read()!;
        Assert.Null(recovered.ContinuingIncident);
        Assert.Equal(firstFailure, recovered.RecoveredIncident);
        await recorder.PrepareAsync(observation, 2, ResourceIncidentSignal.None, TestContext.Current.CancellationToken);
        Assert.NotNull(store.Read()!.ContinuingIncident);
        Assert.Equal(firstFailure, store.Read()!.RecoveredIncident);
        Assert.Throws<InvalidDataException>(() => new DeploymentEvidenceStore(paths, project,
            identity with { ConfigPath = Path.Combine(directory.Path, "foreign.json") }).Read());
    }

    [Fact]
    public async Task FirstFailureKeepsExactRunAndRuntimeAfterSuccessfulDrain()
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var owner = DeploymentObservationTests.Owner(identity);
        var state = new BackgroundOwnerState(owner);
        var recordPath = BackgroundOwnerRecord.PathFor(paths, project);
        await BackgroundOwnerApplication.RetainFailureAsync(state, recordPath, "WebFailure");
        state.Update(value => value with { State = "stopped", RuntimeHealth = "stopped", WebHealth = "stopped" });
        await state.PersistAsync(recordPath, TestContext.Current.CancellationToken);
        await BackgroundOwnerApplication.RetainFailureAsync(state, recordPath, "RuntimeFailure");
        var retained = BackgroundOwnerRecord.TryRead(recordPath)!;
        Assert.Equal("WebFailure", retained.Failure!.Reason);
        Assert.Equal(owner.RuntimeId, retained.Failure.RuntimeId);
        Assert.Equal(owner.RunId, retained.Failure.RunId);
        Assert.Equal(owner.ProcessStartedAtUtc, retained.Failure.ProcessStartedAtUtc);
        Assert.Equal(owner.ProcessId, retained.Failure.ProcessId);
        Assert.Equal("WebFailure", new DeploymentEvidenceStore(paths, project, identity).Read()!.ContinuingIncident!.Observation.Reason);
    }

    [Fact]
    public async Task UnwritableEvidenceCannotReturnReadiness()
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        await store.RetainAsync(observation, null, TestContext.Current.CancellationToken);
        using var held = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(paths.BackgroundRoot(project), "observations.lock"), false);
        var recorder = new DeploymentIncidentRecorder(paths, project, store, (_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<IOException>(() => recorder.PrepareAsync(observation, 0,
            ResourceIncidentSignal.None, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedCollectorResultRequiresZeroSourceErrors(bool partial)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var projectSlug = ProjectSlug.Parse("demo");
        Directory.CreateDirectory(paths.BackgroundRoot(projectSlug));
        var destination = Path.Combine(paths.BackgroundRoot(projectSlug), "resources-fixture");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
            { name = "demo", target_repository = new { path = identity.TargetRepository } }), TestContext.Current.CancellationToken);
        var project = ProjectConfigLoader.Load(identity.ConfigPath, projectSlug, paths);
        await File.WriteAllTextAsync(Path.Combine(destination, "capture-errors.json"), partial ? "[\"error\"]" : "[]", TestContext.Current.CancellationToken);
        var capture = new ResourceIncidentCapture(paths, project, new Runner(request =>
        {
            Assert.Equal(SupervisionPolicy.CaptureBudget, request.Timeout);
            Assert.Contains("-NonInteractive", request.Arguments);
            var script = request.Arguments[3];
            Assert.EndsWith("Capture-ResourceIncident.ps1", script);
            Assert.True(File.Exists(script));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(script)!, "TestSuiteMeasurement.psm1")));
            return new(0, JsonSerializer.Serialize(new { directory = destination, samples = 3, errors = partial ? 1 : 0,
                applicationMutationsRequested = false }), "SECRET_CANARY");
        }));
        var result = await capture.CaptureAsync(ResourceIncidentSignal.ComparableSlowdown, TestContext.Current.CancellationToken);
        Assert.Equal(partial ? IncidentCaptureState.Failed : IncidentCaptureState.Captured, result.State);
        Assert.DoesNotContain("SECRET_CANARY", result.Error ?? "");
    }

    [Fact]
    public async Task WebObservationRejectsOversizeBodies()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024 + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackgroundWebHost.ReadBoundedAsync(body, TestContext.Current.CancellationToken));
    }

    private sealed class Runner(Func<ProcessRequest, ProcessResult> run) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) => Task.FromResult(run(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedControlExitOrFaultRetainsFailureBeforeRequestingDrain(bool fault)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var identity = DeploymentObservationTests.Identity(directory.Path);
        using var state = new BackgroundOwnerState(DeploymentObservationTests.Owner(identity));
        using var shutdown = new Nightweaver.Application.RuntimeShutdownSignals();
        var recordPath = BackgroundOwnerRecord.PathFor(paths, ProjectSlug.Parse("demo"));
        await BackgroundOwnerApplication.WatchControlServerAsync(fault ? Task.FromException(new IOException("SECRET_CANARY"))
            : Task.CompletedTask, state, recordPath, shutdown, TestContext.Current.CancellationToken);
        Assert.Equal(Nightweaver.Application.RuntimeShutdownReason.RuntimeFailure, shutdown.Reason);
        Assert.Equal("RuntimeFailure", BackgroundOwnerRecord.TryRead(recordPath)!.Failure!.Reason);
        Assert.True(File.Exists(Path.Combine(paths.BackgroundRoot(ProjectSlug.Parse("demo")), "owner-failures.json")));
    }

    [Fact]
    public async Task StartupFailureHistorySurvivesAnotherOwnerAndFailedPublicationIsExplicit()
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var owner = DeploymentObservationTests.Owner(identity) with { State = "starting", RuntimeId = null,
            TargetRepository = null, GitCommonDirectory = null };
        using var first = new BackgroundOwnerState(owner);
        var recordPath = BackgroundOwnerRecord.PathFor(paths, project);
        await BackgroundOwnerApplication.RetainFailureAsync(first, recordPath, "RuntimeFailure");
        using var second = new BackgroundOwnerState(owner with { RunId = Guid.NewGuid().ToString("N") });
        await BackgroundOwnerApplication.RetainFailureAsync(second, recordPath, "RuntimeFailure");
        var historyPath = Path.Combine(paths.BackgroundRoot(project), "owner-failures.json");
        using var history = JsonDocument.Parse(await File.ReadAllTextAsync(historyPath, TestContext.Current.CancellationToken));
        Assert.Equal(2, history.RootElement.GetArrayLength());
        Assert.Equal(owner.RunId, history.RootElement[0].GetProperty("failure").GetProperty("runId").GetString());
        using var held = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(paths.BackgroundRoot(project), "owner-failures.lock"), false);
        using var third = new BackgroundOwnerState(owner with { RunId = Guid.NewGuid().ToString("N") });
        await BackgroundOwnerApplication.RetainFailureAsync(third, recordPath, "WebFailure");
        Assert.Equal("supervision-unavailable", BackgroundOwnerRecord.TryRead(recordPath)!.StopReason);
    }
}
