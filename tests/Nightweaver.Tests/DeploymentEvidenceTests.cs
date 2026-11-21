using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class DeploymentEvidenceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task OlderRecoveryCannotHideNewerRequiredCapture(bool pending, bool recoveredCaptureFailed, bool replaceWithLaterFailure)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        var recovered = DeploymentEvidenceStore.NewIncident(paths, project, observation, 1,
            recoveredCaptureFailed ? ResourceIncidentSignal.NativeTimeouts : ResourceIncidentSignal.None,
            recoveredCaptureFailed ? new(IncidentCaptureState.Failed, null, "capture-unavailable")
                : new(IncidentCaptureState.NotRequired, null, null));
        await store.RetainAsync(observation, recovered, TestContext.Current.CancellationToken);
        await store.RetainAsync(observation with { Health = DeploymentHealth.Healthy }, null, TestContext.Current.CancellationToken);
        var blocked = DeploymentEvidenceStore.NewIncident(paths, project, observation, 2,
            ResourceIncidentSignal.NativeTimeouts,
            new(pending ? IncidentCaptureState.Pending : IncidentCaptureState.Failed, null, "capture-unavailable"));
        await store.RetainAsync(observation, blocked, TestContext.Current.CancellationToken);
        if (replaceWithLaterFailure)
            await store.RetainAsync(observation, DeploymentEvidenceStore.NewIncident(paths, project, observation, 2,
                ResourceIncidentSignal.None, new(IncidentCaptureState.NotRequired, null, null)), TestContext.Current.CancellationToken);
        Assert.Equal(recovered.Id, store.Read()!.RecoveredIncident!.Id);
        Assert.Equal(blocked.Id, store.Read()!.ContinuingIncident!.Id);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken))!;
        journal["continuingIncident"] = replaceWithLaterFailure ? journal["history"]!.AsArray().Last()!.DeepClone() : null;
        var original = Encoding.UTF8.GetBytes(journal.ToJsonString());
        await File.WriteAllBytesAsync(store.Path, original, TestContext.Current.CancellationToken);

        var readError = Record.Exception(() => store.Read());
        var calls = 0;
        var recorder = new DeploymentIncidentRecorder(paths, project, store, (_, _) =>
        {
            calls++;
            return Task.FromResult(new IncidentCapture(IncidentCaptureState.Captured, directory.Path, null));
        });
        Assert.IsType<InvalidDataException>(readError);
        foreach (var signal in new[] { ResourceIncidentSignal.None, ResourceIncidentSignal.NativeTimeouts })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => recorder.PrepareAsync(observation, 2,
                signal, TestContext.Current.CancellationToken));
            Assert.Equal(0, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(store.Path, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryPreservesBlockedIncidentAfterHistoryEviction(bool pending)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        var blocked = DeploymentEvidenceStore.NewIncident(paths, project, observation, 1,
            ResourceIncidentSignal.NativeTimeouts,
            new(pending ? IncidentCaptureState.Pending : IncidentCaptureState.Failed, null, "capture-unavailable"));
        await store.RetainAsync(observation, blocked, TestContext.Current.CancellationToken);
        for (var index = 0; index < SupervisionPolicy.MaximumHistory + 1; index++)
        {
            var later = DeploymentEvidenceStore.NewIncident(paths, project, observation, 1,
                ResourceIncidentSignal.None, new(IncidentCaptureState.NotRequired, null, null));
            await store.RetainAsync(observation, later, TestContext.Current.CancellationToken);
        }
        var retained = store.Read()!;
        Assert.Equal(SupervisionPolicy.MaximumHistory, retained.History.Length);
        Assert.Equal(2, retained.Discarded);
        Assert.DoesNotContain(retained.History, item => item.Id == blocked.Id);
        Assert.Equal(blocked.Id, retained.ContinuingIncident!.Id);
        await store.RetainAsync(observation with { Health = DeploymentHealth.Healthy }, null, TestContext.Current.CancellationToken);
        await store.RetainAsync(observation with { Health = DeploymentHealth.Unknown }, null, TestContext.Current.CancellationToken);
        Assert.Null(store.Read()!.ContinuingIncident);
        Assert.Equal(blocked.Id, store.Read()!.RecoveredIncident!.Id);
        var recorder = new DeploymentIncidentRecorder(paths, project, store, (_, _) =>
            Task.FromResult(new IncidentCapture(IncidentCaptureState.Failed, null, "capture-unavailable")));
        var result = await recorder.PrepareAsync(observation, 2, ResourceIncidentSignal.NativeTimeouts, TestContext.Current.CancellationToken);
        Assert.False(result.ReadyForReconciliation);
        Assert.NotEqual(blocked.Id, result.Incident.Id);
        Assert.Equal(blocked.Id, store.Read()!.RecoveredIncident!.Id);
        Assert.Equal(result.Incident.Id, store.Read()!.ContinuingIncident!.Id);
    }

    [Theory]
    [InlineData(false, "missing-incident")]
    [InlineData(true, "missing-incident")]
    [InlineData(false, "missing-state")]
    [InlineData(true, "missing-state")]
    [InlineData(false, "undefined-state")]
    [InlineData(true, "undefined-state")]
    [InlineData(false, "null-capture")]
    [InlineData(false, "missing-observation")]
    [InlineData(false, "foreign-observation")]
    [InlineData(false, "undefined-health")]
    [InlineData(false, "undefined-process")]
    [InlineData(false, "negative-attempts")]
    [InlineData(false, "inconsistent-capture")]
    [InlineData(false, "inconsistent-history")]
    [InlineData(false, "null-history-entry")]
    [InlineData(false, "missing-owner-identity")]
    [InlineData(false, "null-incident")]
    [InlineData(true, "null-incident")]
    [InlineData(false, "missing-nullable-capture-field")]
    [InlineData(false, "duplicate-field")]
    [InlineData(false, "unknown-field")]
    public async Task CorruptCaptureJournalIsRejectedWithoutOverwritingEvidence(bool pending, string corruption)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        var incident = DeploymentEvidenceStore.NewIncident(paths, project, observation, 1,
            ResourceIncidentSignal.NativeTimeouts,
            new(pending ? IncidentCaptureState.Pending : IncidentCaptureState.Failed, null, "capture-unavailable"));
        await store.RetainAsync(observation, incident, TestContext.Current.CancellationToken);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken))!;
        var continuing = journal["continuingIncident"]!;
        switch (corruption)
        {
            case "missing-incident": journal.AsObject().Remove("continuingIncident"); break;
            case "missing-state": continuing["capture"]!.AsObject().Remove("state"); break;
            case "undefined-state": continuing["capture"]!["state"] = 999; break;
            case "null-capture": continuing["capture"] = null; break;
            case "missing-observation": journal.AsObject().Remove("lastObservation"); break;
            case "foreign-observation": continuing["observation"]!["installation"]!["project"] = "foreign"; break;
            case "undefined-health": continuing["observation"]!["health"] = 999; break;
            case "undefined-process": continuing["observation"]!["process"]!["state"] = 999; break;
            case "negative-attempts": continuing["attempts"] = -1; break;
            case "inconsistent-capture": continuing["capture"]!["state"] = "NotRequired"; break;
            case "inconsistent-history": journal["history"]![0]!["attempts"] = 2; break;
            case "null-history-entry": journal["history"]![0] = null; break;
            case "missing-owner-identity": continuing["observation"]!["owner"]!.AsObject().Remove("runId"); break;
            case "null-incident": journal["continuingIncident"] = null; break;
            case "missing-nullable-capture-field": continuing["capture"]!.AsObject().Remove("directory"); break;
            case "unknown-field": continuing["capture"]!["unexpected"] = true; break;
        }
        var json = journal.ToJsonString();
        if (corruption == "duplicate-field") json = json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        var original = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(store.Path, original, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => store.Read());
        var calls = 0;
        var recorder = new DeploymentIncidentRecorder(paths, project, store, (_, _) =>
        {
            calls++;
            return Task.FromResult(new IncidentCapture(IncidentCaptureState.Captured, directory.Path, null));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => recorder.PrepareAsync(observation, 1,
            ResourceIncidentSignal.None, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        Assert.Equal(original, await File.ReadAllBytesAsync(store.Path, TestContext.Current.CancellationToken));
    }

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
    public async Task FailedOrInterruptedCaptureCannotBeDowngradedByLaterTickOrFailure(bool interrupted)
    {
        using var directory = new TempDirectory();
        var paths = new HostPaths(directory.Path);
        var project = ProjectSlug.Parse("demo");
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var observation = Observation(identity);
        var store = new DeploymentEvidenceStore(paths, project, identity);
        var blocked = DeploymentEvidenceStore.NewIncident(paths, project, observation, 1, ResourceIncidentSignal.NativeTimeouts,
            new(interrupted ? IncidentCaptureState.Pending : IncidentCaptureState.Failed, null, "capture-unavailable"));
        await store.RetainAsync(observation, blocked, TestContext.Current.CancellationToken);
        var recorder = new DeploymentIncidentRecorder(paths, project, new(paths, project, identity),
            (_, _) => throw new InvalidOperationException("No automatic capture retry"));
        var result = await recorder.PrepareAsync(observation, 1, ResourceIncidentSignal.None, TestContext.Current.CancellationToken);
        Assert.False(result.ReadyForReconciliation);
        Assert.Equal(blocked.Id, result.Incident.Id);
        using var ownerState = new BackgroundOwnerState(observation.Owner!);
        await BackgroundOwnerApplication.RetainFailureAsync(ownerState, BackgroundOwnerRecord.PathFor(paths, project), "WebFailure");
        Assert.Equal(blocked.Id, store.Read()!.ContinuingIncident!.Id);
        result = await recorder.PrepareAsync(observation, 1, ResourceIncidentSignal.NativeTimeouts, TestContext.Current.CancellationToken);
        Assert.False(result.ReadyForReconciliation);
        Assert.Equal(blocked.Id, store.Read()!.ContinuingIncident!.Id);
        await store.RetainAsync(observation with { Health = DeploymentHealth.Healthy }, null, TestContext.Current.CancellationToken);
        await store.RetainAsync(observation with { Health = DeploymentHealth.Unknown }, null, TestContext.Current.CancellationToken);
        Assert.Null(store.Read()!.ContinuingIncident);
        Assert.Equal(blocked.Id, store.Read()!.RecoveredIncident!.Id);
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
