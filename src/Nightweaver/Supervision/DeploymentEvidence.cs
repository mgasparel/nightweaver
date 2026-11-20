using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Supervision;

internal enum ResourceIncidentSignal { None, ComparableSlowdown, NativeTimeouts, SustainedResourceDrift }
internal enum IncidentCaptureState { NotRequired, Pending, Captured, Failed }
internal sealed record IncidentCapture(IncidentCaptureState State, string? Directory, string? Error);
internal sealed record DeploymentIncident(string Id, DeploymentObservation Observation, int Attempts,
    ResourceIncidentSignal ResourceSignal, IncidentCapture Capture, string HostLogPath, string WebLogPath,
    string Instructions);
internal sealed record DeploymentEvidence(int Version, DeploymentIdentity Installation, DeploymentObservation LastObservation,
    DeploymentIncident? ContinuingIncident, DeploymentIncident? RecoveredIncident, DeploymentIncident[] History, long Discarded);
internal sealed record RecoveryEvidence(DeploymentIncident Incident, bool ReadyForReconciliation);
internal sealed record OwnerFailureEvidence(int Version, string Root, string ConfigPath, string Project,
    string? TargetRepository, string? GitCommonDirectory, BackgroundFailure Failure, string HostLogPath, string WebLogPath);

/// <summary>Bounded, local operational evidence, separate from Beads and recovery policy state.</summary>
internal sealed class DeploymentEvidenceStore(HostPaths paths, ProjectSlug project, DeploymentIdentity identity)
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter() }
    };
    internal string Path => System.IO.Path.Combine(paths.BackgroundRoot(project), "observations.json");

    internal DeploymentEvidence? Read()
    {
        byte[] bytes;
        try { bytes = InvocationRuntimeFiles.ReadRuntimeFileBounded(Path, MaximumBytes); }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            || exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }) { return null; }
        var value = JsonSerializer.Deserialize<DeploymentEvidence>(bytes, Options);
        if (value is not { Version: 1 } || value.Installation != identity || value.History is null
            || value.History.Length > SupervisionPolicy.MaximumHistory || value.Discarded < 0)
            throw new InvalidDataException("Deployment evidence is invalid or belongs to another installation.");
        return value;
    }

    internal async Task RetainAsync(DeploymentObservation observation, DeploymentIncident? incident, CancellationToken token)
    {
        if (observation.Installation != identity) throw new InvalidDataException("Observation installation mismatch.");
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
            System.IO.Path.Combine(paths.BackgroundRoot(project), "observations.lock"), true);
        var old = Read();
        // No arbitrary exception messages, transcript, environment, config bytes or telemetry slots.
        observation = Sanitize(observation);
        if (incident is not null) incident = incident with { Observation = observation };
        var history = old?.History ?? [];
        if (incident is not null)
            history = history.Where(item => item.Id != incident.Id).Append(incident).ToArray();
        var overflow = Math.Max(0, history.Length - SupervisionPolicy.MaximumHistory);
        var blocked = old?.ContinuingIncident is { Capture.State: IncidentCaptureState.Failed or IncidentCaptureState.Pending };
        var continuing = blocked && incident?.Id != old!.ContinuingIncident!.Id
            ? old!.ContinuingIncident : incident ?? old?.ContinuingIncident;
        var recovered = old?.RecoveredIncident;
        if (observation.Health == DeploymentHealth.Healthy && continuing is not null)
        {
            recovered = continuing;
            continuing = null;
        }
        var next = new DeploymentEvidence(1, identity, observation, continuing, recovered,
            history.Skip(overflow).ToArray(), checked((old?.Discarded ?? 0) + overflow));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Deployment evidence exceeds its byte budget.");
        await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(Path, bytes, token).ConfigureAwait(false);
    }

    internal static DeploymentObservation Sanitize(DeploymentObservation observation) => observation with
    {
        Owner = observation.Owner is { } owner ? owner with { Message = null, Supervision = null } : null
    };

    internal static async Task RetainOwnerFailureAsync(BackgroundOwnerRecord owner)
    {
        var paths = new HostPaths(owner.Root);
        var project = ProjectSlug.Parse(owner.Project);
        // Startup may fail before a repository/runtime identity is established. Keep that
        // exact historical failure separately; incomplete identity never becomes recovery proof.
        using (var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
            System.IO.Path.Combine(paths.BackgroundRoot(project), "owner-failures.lock"), true))
        {
            var path = System.IO.Path.Combine(paths.BackgroundRoot(project), "owner-failures.json");
            OwnerFailureEvidence[] history;
            try
            {
                history = JsonSerializer.Deserialize<OwnerFailureEvidence[]>(
                    InvocationRuntimeFiles.ReadRuntimeFileBounded(path, MaximumBytes), Options)
                    ?? throw new InvalidDataException("Invalid owner failure history.");
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException
                || exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }) { history = []; }
            if (history.Length > SupervisionPolicy.MaximumHistory || history.Any(item => item is null || item.Version != 1
                || item.Root != paths.Root || item.Project != project.Value || item.ConfigPath != owner.ConfigPath))
                throw new InvalidDataException("Foreign or invalid owner failure history.");
            var entry = new OwnerFailureEvidence(1, paths.Root, owner.ConfigPath, project.Value,
                owner.TargetRepository, owner.GitCommonDirectory, owner.Failure!,
                System.IO.Path.Combine(paths.LogsRoot(project), "background-host.log"),
                System.IO.Path.Combine(paths.LogsRoot(project), "background-web.log"));
            var next = history.Where(item => item.Failure.RunId != owner.RunId).Append(entry)
                .TakeLast(SupervisionPolicy.MaximumHistory).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Options);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Owner failure history exceeds its byte budget.");
            await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(path, bytes, CancellationToken.None).ConfigureAwait(false);
        }
        var identity = new DeploymentIdentity(paths.Root, owner.ConfigPath, owner.Project,
            owner.TargetRepository ?? string.Empty, owner.GitCommonDirectory ?? string.Empty);
        // Before repository validation completes the separate failure history retains the exact failure.
        // Do not publish a foreign/incomplete installation identity into the observation journal.
        if (identity.TargetRepository.Length == 0 || identity.GitCommonDirectory.Length == 0) return;
        var observation = new DeploymentObservation(1, DateTimeOffset.UtcNow, owner.RunId, 0, identity, owner,
            DeploymentHealth.Failed, owner.Failure!.Reason, true, new(OwnerProcessState.Alive, false), null, null);
        var incident = NewIncident(paths, project, observation, 0, ResourceIncidentSignal.None,
            new(IncidentCaptureState.NotRequired, null, null));
        await new DeploymentEvidenceStore(paths, project, identity).RetainAsync(observation, incident, CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static DeploymentIncident NewIncident(HostPaths paths, ProjectSlug project, DeploymentObservation observation,
        int attempts, ResourceIncidentSignal signal, IncidentCapture capture) => new(Guid.NewGuid().ToString("N"),
            Sanitize(observation), attempts, signal, capture,
            System.IO.Path.Combine(paths.LogsRoot(project), "background-host.log"),
            System.IO.Path.Combine(paths.LogsRoot(project), "background-web.log"),
            "Inspect matching owner/run/runtime and retained logs; reconcile native settlement and required capture before recovery. Preserve Bead pauses, claims, repairs and retained work.");
}

// This is preparation, never a recovery executor. A consumer must still hold repository
// exclusion and revalidate current owner, intent, generation and reservation before acting.
internal sealed class DeploymentIncidentRecorder(HostPaths paths, ProjectSlug project, DeploymentEvidenceStore store,
    Func<ResourceIncidentSignal, CancellationToken, Task<IncidentCapture>> capture)
{
    internal async Task<RecoveryEvidence> PrepareAsync(DeploymentObservation observation, int attempts,
        ResourceIncidentSignal signal, CancellationToken token)
    {
        // A later quiet tick, changed failure or process restart cannot erase a required
        // capture that failed or was interrupted. Retain it for inspection rather than
        // silently downgrading the requirement to NotRequired or retrying until green.
        if (store.Read()?.ContinuingIncident is { Capture.State: IncidentCaptureState.Failed or IncidentCaptureState.Pending } blocked)
        {
            await store.RetainAsync(observation, null, token).ConfigureAwait(false);
            return new(blocked, false);
        }
        var incident = DeploymentEvidenceStore.NewIncident(paths, project, observation, attempts, signal,
            new(signal == ResourceIncidentSignal.None ? IncidentCaptureState.NotRequired : IncidentCaptureState.Pending, null, null));
        // The triggering observation survives interrupted/failed capture, and capture always
        // settles before a consumer can receive permission to proceed to reconciliation.
        await store.RetainAsync(observation, incident, token).ConfigureAwait(false);
        if (signal != ResourceIncidentSignal.None)
        {
            IncidentCapture result;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(SupervisionPolicy.CaptureBudget);
                result = await capture(signal, deadline.Token).WaitAsync(SupervisionPolicy.CaptureBudget, token).ConfigureAwait(false);
                if (result.State != IncidentCaptureState.Captured || string.IsNullOrWhiteSpace(result.Directory))
                    result = new(IncidentCaptureState.Failed, result.Directory, result.Error ?? "capture-unconfirmed");
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException or InvalidDataException or ToolExecutionException)
            { result = new(IncidentCaptureState.Failed, null, "capture-unavailable-or-interrupted"); }
            incident = incident with { Capture = result };
            // Retain a failed cancellation too, independently of the canceled probe token.
            await store.RetainAsync(observation, incident, CancellationToken.None).ConfigureAwait(false);
        }
        return new(incident, !token.IsCancellationRequested && observation.Health == DeploymentHealth.Failed
            && observation.Process is { State: OwnerProcessState.Exited, DescendantsSettled: true }
            && observation.OwnerLockHeld == false
            && incident.Capture.State is IncidentCaptureState.Captured or IncidentCaptureState.NotRequired);
    }
}
