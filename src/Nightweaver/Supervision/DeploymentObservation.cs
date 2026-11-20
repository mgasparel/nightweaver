using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Telemetry;

namespace Nightweaver.Supervision;

internal enum DeploymentHealth { Unknown, Starting, Healthy, Draining, Stopped, Failed }
internal sealed record DeploymentIdentity(string Root, string ConfigPath, string Project, string TargetRepository,
    string GitCommonDirectory);
internal sealed record DeploymentObservation(int Version, DateTimeOffset Utc, string Epoch, long MonotonicTicks,
    DeploymentIdentity Installation, BackgroundOwnerRecord? Owner, DeploymentHealth Health, string Reason,
    bool? OwnerLockHeld, OwnerProcessObservation? Process, DateTimeOffset? RuntimeObservedUtc,
    DateTimeOffset? ControlObservedUtc)
{
    internal SupervisionObservation ToPolicyObservation() => new(Epoch, MonotonicTicks, Utc,
        Health switch
        {
            DeploymentHealth.Healthy => SupervisionHealth.Healthy,
            DeploymentHealth.Draining => SupervisionHealth.Draining,
            DeploymentHealth.Failed => SupervisionHealth.Failed,
            _ => SupervisionHealth.Unknown
        }, Owner is { } owner ? new(owner.ProcessId, owner.ProcessStartedAtUtc, owner.RunId, owner.RuntimeId) : null);
}

// Sources are injected so uncertainty, clock changes and deadlines need no native journeys.
internal sealed record DeploymentObservationSources(Func<BackgroundOwnerRecord?> Owner,
    Func<bool?> OwnerLock, Func<BackgroundOwnerRecord, OwnerProcessObservation> Process,
    Func<CancellationToken, Task<BackgroundControlResponse>> Control,
    Func<FactoryTelemetryReadResult> Runtime, Func<SupervisionRead> Intent);

/// <summary>Read-only bounded observation. It grants no launch, stop, cleanup or tracker authority.</summary>
internal sealed class DeploymentObserver(DeploymentIdentity identity, DeploymentObservationSources sources,
    TimeProvider? clock = null, string? observationEpoch = null)
{
    private static readonly string Epoch = Guid.NewGuid().ToString("N");
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    internal static DeploymentObserver Create(HostPaths paths, ProjectConfig project, string gitCommonDirectory,
        Func<SupervisionRead> intent, string observationEpoch) => new(new(paths.Root, project.ConfigFilePath, project.Name.Value,
            project.TargetRepository.Path, gitCommonDirectory), new(
                () => BackgroundOwnerRecord.TryRead(BackgroundOwnerRecord.PathFor(paths, project.Name)),
                () => BackgroundFileLock.ObserveOwner(paths, project.Name), BackgroundContainment.Read,
                token => BackgroundControlProtocol.SendAsync(BackgroundIdentity.PipeName(paths, project.Name),
                    new("status", string.Empty, false), TimeSpan.FromSeconds(7), token),
                () => new FactoryTelemetryReader(paths, project).Read(), intent), observationEpoch: observationEpoch);

    internal async Task<DeploymentObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        BackgroundOwnerRecord? owner = null;
        bool? held = null;
        OwnerProcessObservation? process = null;
        DateTimeOffset? runtimeUtc = null;
        DateTimeOffset? controlUtc = null;
        DeploymentObservation Result(DeploymentHealth health, string reason) => new(1, time.GetUtcNow(), observationEpoch ?? Epoch,
            (long)(time.GetTimestamp() * (double)TimeSpan.TicksPerSecond / time.TimestampFrequency),
            identity, owner, health, reason, held, process, runtimeUtc, controlUtc);
        try
        {
            owner = sources.Owner();
            if (owner is null) return Result(DeploymentHealth.Unknown, "owner-record-unavailable");
            if (!MatchesInstallation(identity, owner)) return Result(DeploymentHealth.Unknown, "installation-mismatch");
            held = sources.OwnerLock();
            process = sources.Process(owner);
            var intent = sources.Intent();
            if (intent.Availability == SupervisionAvailability.Unavailable || owner.StopReason == "supervision-unavailable")
                return Result(DeploymentHealth.Unknown, "intent-unavailable");
            if (held is null || process.State is OwnerProcessState.Unavailable or OwnerProcessState.Reused)
                return Result(DeploymentHealth.Unknown, "native-identity-uncertain");
            if (process.State == OwnerProcessState.Exited)
            {
                if (held == true) return Result(DeploymentHealth.Unknown, "foreign-or-replacing-owner-lock");
                var stopped = IsDeliberate(owner.StopReason) || intent.State is { DesiredRunning: false }
                    || intent.State?.MaintenanceToken is not null;
                return Result(stopped ? DeploymentHealth.Stopped : DeploymentHealth.Failed,
                    stopped ? "intentional-stop" : owner.Failure?.Reason ?? "owner-exited");
            }
            if (held != true) return Result(DeploymentHealth.Unknown, "owner-lock-not-confirmed");
            if (owner.State == "stopping" && IsDeliberate(owner.StopReason))
                return Result(DeploymentHealth.Draining, "intentional-drain");
            // A starting owner may not yet have a pipe or a runtime. It is never death evidence.
            if (owner.State == "starting") return Result(DeploymentHealth.Starting, "startup-incomplete");
            using var budget = new CancellationTokenSource(SupervisionPolicy.ProbeBudget, time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            var response = await sources.Control(deadline.Token)
                .WaitAsync(SupervisionPolicy.ProbeBudget, time, cancellationToken).ConfigureAwait(false);
            if (!response.Accepted || response.Owner is not { } live || !SameOwner(owner, live))
                return Result(DeploymentHealth.Unknown, "control-identity-unavailable");
            controlUtc = live.ObservedAtUtc;
            if (!Fresh(controlUtc, time.GetUtcNow())) return Result(DeploymentHealth.Unknown, "control-stale");
            owner = live;
            // Confirm the record and native identity again after asynchronous work; a replacement
            // can never borrow the old owner's control response or settlement evidence.
            var confirm = sources.Owner();
            if (confirm is null || !SameOwner(owner, confirm) || sources.OwnerLock() != true
                || sources.Process(owner).State != OwnerProcessState.Alive)
                return Result(DeploymentHealth.Unknown, "owner-changed-during-probe");
            if (sources.Intent().Availability == SupervisionAvailability.Unavailable)
                return Result(DeploymentHealth.Unknown, "intent-unavailable");
            if (owner.StopReason == "supervision-unavailable") return Result(DeploymentHealth.Unknown, "intent-unavailable");
            if (owner.State == "stopping" && IsDeliberate(owner.StopReason))
                return Result(DeploymentHealth.Draining, "intentional-drain");
            if (owner.Failure is not null || owner.StopReason is "WebFailure" or "RuntimeFailure")
                return Result(DeploymentHealth.Failed, owner.Failure?.Reason ?? owner.StopReason!);
            var runtime = sources.Runtime();
            runtimeUtc = runtime.Snapshot?.ObservedAt;
            if (runtime.Availability != FactoryTelemetryAvailability.Live || runtime.Snapshot is not { } snapshot
                || snapshot.RuntimeId != owner.RuntimeId || snapshot.Project != identity.Project
                || !PathEquals(snapshot.TargetRepository, identity.TargetRepository) || !Fresh(runtimeUtc, time.GetUtcNow()))
                return Result(DeploymentHealth.Unknown, "runtime-not-fresh-or-matching");
            return owner.State == "running" && owner.WebHealth == "healthy" && owner.RuntimeHealth == "live"
                ? Result(DeploymentHealth.Healthy, "matching-live-runtime-and-web")
                : Result(DeploymentHealth.Unknown, "web-or-runtime-unconfirmed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result(DeploymentHealth.Unknown, "probe-deadline"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { return Result(DeploymentHealth.Unknown, "observation-unavailable"); }
    }

    internal static bool Fresh(DateTimeOffset? observed, DateTimeOffset utc) => observed is { } at
        && utc - at >= TimeSpan.FromSeconds(-1) && utc - at <= TimeSpan.FromSeconds(10);

    internal static bool IsDeliberate(string? reason) => reason is "OperatorDrain" or "ForcedStop" or "Maintenance";
    internal static bool MatchesInstallation(DeploymentIdentity identity, BackgroundOwnerRecord owner) =>
        owner.SchemaVersion == 1 && owner.ProcessId > 0 && Guid.TryParseExact(owner.RunId, "N", out _)
        && owner.Project == identity.Project && PathEquals(owner.Root, identity.Root)
        && PathEquals(owner.ConfigPath, identity.ConfigPath)
        && (owner.State == "starting" || PathEquals(owner.TargetRepository, identity.TargetRepository)
            && PathEquals(owner.GitCommonDirectory, identity.GitCommonDirectory));

    internal static bool SameOwner(BackgroundOwnerRecord left, BackgroundOwnerRecord right) =>
        left.RunId == right.RunId && left.RuntimeId == right.RuntimeId && left.ProcessId == right.ProcessId
        && left.ProcessStartedAtUtc == right.ProcessStartedAtUtc && left.Project == right.Project
        && PathEquals(left.Root, right.Root) && PathEquals(left.ConfigPath, right.ConfigPath)
        && PathEquals(left.TargetRepository, right.TargetRepository) && PathEquals(left.GitCommonDirectory, right.GitCommonDirectory);

    private static bool PathEquals(string? left, string? right) => left is not null && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
