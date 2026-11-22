using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Supervision;
using Nightweaver.Background;

namespace Nightweaver.Upgrade;

internal sealed record UpgradeReservation(int Version, string Root, string OperationId, string ScopeDigest);
internal sealed record UpgradeLaunch(string Project, string RunId);
internal sealed record UpgradeActivation(int Version, string OperationId, string PackageDigest, UpgradeLaunch[] Launches);

/// <summary>Durable root-wide suppression. A partial publication never permits startup.</summary>
internal static class UpgradeInterlock
{
    // Like repository exclusion, a payload lease belongs to the native process.
    // Returning from a runtime method is not proof that its process has exited.
    private static readonly ConcurrentBag<IDisposable> ProcessPayloadLeases = [];
    internal static string Root(HostPaths paths) => Path.Combine(paths.Root, "run", "upgrade", "interlocks");
    internal static IDisposable AcquireManager(HostPaths paths) => InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
        Path.Combine(paths.Root, "run", "upgrade", "manager.lock"), true);
    internal static IDisposable AcquireAdmission(HostPaths paths) => InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
        Path.Combine(paths.Root, "run", "upgrade", "admission.lock"), true);
    internal static IDisposable AcquirePayload(HostPaths paths, ProjectSlug project) => InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
        Path.Combine(paths.Root, "run", project.Value, "payload.lock"), true);

    internal static void AcquireRuntime(HostPaths paths, ProjectSlug project, ValidatedTargetRepository repository)
    {
        ProcessLifetimeTargetRepositoryLock.Acquire(repository);
        using var admission = AcquireAdmission(paths);
        RequireOwnerStartupAllowed(paths, project, false);
        ProcessPayloadLeases.Add(AcquirePayload(paths, project));
    }

    internal static string WebPayloadPath(HostPaths paths, ProjectSlug project) =>
        Path.Combine(paths.Root, "run", project.Value, "web-payload.lock");
    internal static string OwnedWebPayloadPath(HostPaths paths, ProjectSlug project) =>
        Path.Combine(paths.Root, "run", project.Value, "owned-web-payload.lock");

    internal static IDisposable AcquireWeb(HostPaths paths, ProjectSlug project,
        Func<BackgroundOwnerRecord, bool>? contained = null)
    {
        using var admission = AcquireAdmission(paths);
        var owner = BackgroundOwnerRecord.TryRead(BackgroundOwnerRecord.PathFor(paths, project));
        if (owner is { ContainmentVersion: 1, RuntimeId: not null }
            && owner.Root == paths.Root && owner.Project == project.Value
            && BackgroundFileLock.ObserveOwner(paths, project) == true
            && (contained ?? IsContainedWeb)(owner))
        {
            RequireOwnerStartupAllowed(paths, project, true);
            return InvocationRuntimeFiles.OpenSharedRuntimeFile(OwnedWebPayloadPath(paths, project));
        }
        RequireStartupAllowed(paths);
        return InvocationRuntimeFiles.OpenSharedRuntimeFile(WebPayloadPath(paths, project));
    }

    private static bool IsContainedWeb(BackgroundOwnerRecord owner) =>
        BackgroundContainment.Read(owner).State == OwnerProcessState.Alive
        && WindowsProcessLauncher.IsCurrentProcessInOwnerJob(BackgroundContainment.Name(owner));

    internal static void AcquireWebForProcessLifetime(HostPaths paths, ProjectSlug project) =>
        ProcessPayloadLeases.Add(AcquireWeb(paths, project));

    internal static void RequireStartupAllowed(HostPaths paths)
    {
        foreach (var reservation in ReadAll(paths))
            if (!reservation.Released)
                throw new InvalidOperationException($"Upgrade {reservation.Reservation.OperationId} suppresses startup. Inspect the retained upgrade operation.");
    }

    private static void RequireOwnerStartupAllowed(HostPaths paths, ProjectSlug project, bool web)
    {
        var active = ReadAll(paths).Where(r => !r.Released).ToArray();
        if (active.Length == 0) return;
        if (active.Length != 1) throw new InvalidDataException("Upgrade activation is unavailable.");
        var reservation = active[0].Reservation;
        RequireActivationAllowed(paths, reservation, project);
        var activationPath = Path.Combine(Root(paths), reservation.OperationId, "activation.json");
        if (!File.Exists(activationPath))
            throw new InvalidOperationException("Upgrade suppresses startup until an exact replacement is reserved.");
        var bytes = InvocationRuntimeFiles.ReadRuntimeFileBounded(activationPath, 65536);
        var grant = ReadActivation(bytes, reservation);
        var owner = BackgroundOwnerRecord.TryRead(BackgroundOwnerRecord.PathFor(paths, project));
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        if (owner is null || owner.Root != paths.Root || owner.Project != project.Value
            || !grant.Launches.Contains(new(project.Value, owner.RunId))
            || owner.CliRelease?.PackageDigest != grant.PackageDigest
            || BackgroundFileLock.ObserveOwner(paths, project) != true
            || (!web && (owner.ProcessId != current.Id || owner.ProcessStartedAtUtc != current.StartTime.ToUniversalTime())))
            throw new InvalidOperationException("Upgrade permits only its exact replacement owner.");
    }

    internal static async Task ActivateAsync(HostPaths paths, UpgradeReservation reservation, UpgradeActivation activation, CancellationToken token)
    {
        RequireCurrent(paths, reservation);
        await PublishActivationAsync(paths, reservation, activation, token).ConfigureAwait(false);
    }

    internal static UpgradeActivation ReadActivation(HostPaths paths, UpgradeReservation reservation)
    {
        RequireCurrent(paths, reservation);
        return ReadActivation(InvocationRuntimeFiles.ReadRuntimeFileBounded(
            Path.Combine(Root(paths), reservation.OperationId, "activation.json"), 65536), reservation);
    }

    internal static bool IsReleased(HostPaths paths, UpgradeReservation reservation) =>
        ReadAll(paths).SingleOrDefault(r => r.Reservation == reservation).Released;

    private static async Task PublishActivationAsync(HostPaths paths, UpgradeReservation reservation, UpgradeActivation activation, CancellationToken token)
    {
        ReadActivation(JsonSerializer.SerializeToUtf8Bytes(activation, PreparedRelease.JsonOptions), reservation);
        await PublishAsync(Path.Combine(Root(paths), reservation.OperationId), "activation.json", activation, token).ConfigureAwait(false);
    }

    internal static void RequireCurrent(HostPaths paths, UpgradeReservation expected)
    {
        var active = ReadAll(paths).Where(r => !r.Released).ToArray();
        if (active.Length != 1 || active[0].Reservation != expected)
            throw new InvalidOperationException("Upgrade operation authority changed. Retain the boundary.");
    }

    // The caller holds the Project command lock. Admission serializes stop records
    // across Projects without taking the manager lock held by an upgrade in progress.
    internal static async Task RecordStopAsync(HostPaths paths, ProjectSlug project, CancellationToken token)
    {
        using var admission = AcquireAdmission(paths);
        foreach (var item in ReadAll(paths).Where(r => !r.Released && !r.StoppedProjects.Contains(project.Value)))
            await PublishAsync(Path.Combine(Root(paths), item.Reservation.OperationId),
                $"stopped-{project.Value}.json", item.Reservation, token).ConfigureAwait(false);
    }

    internal static void RequireActivationAllowed(HostPaths paths, UpgradeReservation expected, ProjectSlug project)
    {
        var active = ReadAll(paths).Where(r => !r.Released).ToArray();
        if (active.Length != 1 || active[0].Reservation != expected || active[0].StoppedProjects.Contains(project.Value))
            throw new InvalidOperationException("Activation intent changed; retain the upgrade interlock.");
    }

    internal static void RequireDrain(HostPaths paths, string operationId)
    {
        var active = ReadAll(paths).Where(r => !r.Released).ToArray();
        if (active.Length != 1 || active[0].Reservation.OperationId != operationId)
            throw new InvalidOperationException("Upgrade drain authority changed.");
    }

    internal static async Task ReserveAsync(HostPaths paths, UpgradeReservation reservation, CancellationToken token)
    {
        Validate(paths, reservation);
        RequireStartupAllowed(paths);
        var directory = Path.Combine(Root(paths), reservation.OperationId);
        // An existing partial operation cannot be initialized over.
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(directory, "writer.lock"), true);
        using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != "writer.lock"))
            throw new InvalidDataException("Upgrade reservation already exists.");
        await PublishAsync(directory, "reservation.json", reservation, token).ConfigureAwait(false);
        RequireCurrent(paths, reservation);
    }

    // Call only under manager/admission/project locks after reconciliation. This starts nothing.
    internal static async Task ReleaseAsync(HostPaths paths, UpgradeReservation expected, CancellationToken token)
    {
        RequireCurrent(paths, expected);
        await PublishAsync(Path.Combine(Root(paths), expected.OperationId), "released.json", expected, token).ConfigureAwait(false);
    }

    private static List<(UpgradeReservation Reservation, bool Released, string[] StoppedProjects)> ReadAll(HostPaths paths)
    {
        var result = new List<(UpgradeReservation, bool, string[])>();
        IDisposable pin;
        try { pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(Root(paths)); }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            || exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 }) { return result; }
        using (pin)
        {
            var directories = Directory.EnumerateFileSystemEntries(Root(paths)).Take(4097).ToArray();
            if (directories.Length > 4096) throw new InvalidDataException("Upgrade interlock inventory is unavailable.");
            foreach (var directory in directories)
            {
                SupervisionValidation.ValidateId(Path.GetFileName(directory));
                using var operationPin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
                var entries = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToArray();
                var stops = entries.Where(n => n is not null && n.StartsWith("stopped-", StringComparison.Ordinal)
                    && n.EndsWith(".json", StringComparison.Ordinal)).ToArray();
                if (!entries.Contains("reservation.json") || entries.Any(n => n is not ("reservation.json" or "released.json" or "activation.json" or "writer.lock")
                    && !stops.Contains(n)))
                    throw new InvalidDataException($"Interrupted upgrade interlock at '{directory}'. Retain all files.");
                var reservation = Read(paths, Path.Combine(directory, "reservation.json"));
                if (reservation.OperationId != Path.GetFileName(directory)) throw new InvalidDataException("Foreign upgrade interlock.");
                if (entries.Contains("activation.json"))
                    ReadActivation(InvocationRuntimeFiles.ReadRuntimeFileBounded(Path.Combine(directory, "activation.json"), 65536), reservation);
                var released = entries.Contains("released.json");
                if (released && Read(paths, Path.Combine(directory, "released.json")) != reservation)
                    throw new InvalidDataException("Upgrade release identity changed.");
                var stoppedProjects = stops.Select(n => ProjectSlug.Parse(n![8..^5]).Value).ToArray();
                foreach (var stop in stops)
                    if (Read(paths, Path.Combine(directory, stop!)) != reservation)
                        throw new InvalidDataException("Upgrade stop identity changed.");
                result.Add((reservation, released, stoppedProjects));
            }
        }
        return result;
    }

    private static UpgradeReservation Read(HostPaths paths, string path)
    {
        var bytes = InvocationRuntimeFiles.ReadRuntimeFileBounded(path, 4096);
        using var json = JsonDocument.Parse(bytes);
        PreparedRelease.RejectDuplicateProperties(json.RootElement);
        var value = JsonSerializer.Deserialize<UpgradeReservation>(bytes, PreparedRelease.JsonOptions)
            ?? throw new InvalidDataException("Missing upgrade interlock.");
        Validate(paths, value);
        return value;
    }

    private static UpgradeActivation ReadActivation(byte[] bytes, UpgradeReservation reservation)
    {
        using var json = JsonDocument.Parse(bytes);
        PreparedRelease.RejectDuplicateProperties(json.RootElement);
        var activation = JsonSerializer.Deserialize<UpgradeActivation>(bytes, PreparedRelease.JsonOptions);
        if (activation is not { Version: 1, PackageDigest.Length: 64, Launches.Length: <= 256 }
            || activation.OperationId != reservation.OperationId || !activation.PackageDigest.All(char.IsAsciiHexDigit)
            || activation.Launches.Any(l => l is null)
            || activation.Launches.Select(l => l.Project).Distinct(StringComparer.Ordinal).Count() != activation.Launches.Length)
            throw new InvalidDataException("Invalid upgrade activation.");
        foreach (var launch in activation.Launches)
        {
            ProjectSlug.Parse(launch.Project);
            SupervisionValidation.ValidateId(launch.RunId);
        }
        return activation;
    }

    private static void Validate(HostPaths paths, UpgradeReservation reservation)
    {
        SupervisionValidation.ValidateId(reservation.OperationId);
        if (reservation.Version != 1 || reservation.Root != paths.Root || reservation.ScopeDigest is not { Length: 64 }
            || !reservation.ScopeDigest.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Foreign upgrade interlock.");
    }

    private static async Task PublishAsync<T>(string directory, string name, T value, CancellationToken token)
    {
        using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
        var path = Path.Combine(directory, name);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, PreparedRelease.JsonOptions), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(true);
        }
        token.ThrowIfCancellationRequested();
        File.Move(pending, path, false);
    }
}
