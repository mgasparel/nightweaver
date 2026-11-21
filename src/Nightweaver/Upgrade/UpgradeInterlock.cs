using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Supervision;

namespace Nightweaver.Upgrade;

internal sealed record UpgradeReservation(int Version, string Root, string OperationId, string ScopeDigest);

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
        RequireStartupAllowed(paths);
        ProcessPayloadLeases.Add(AcquirePayload(paths, project));
    }

    internal static string WebPayloadPath(HostPaths paths, ProjectSlug project) =>
        Path.Combine(paths.Root, "run", project.Value, "web-payload.lock");

    internal static IDisposable AcquireWeb(HostPaths paths, ProjectSlug project)
    {
        using var admission = AcquireAdmission(paths);
        RequireStartupAllowed(paths);
        return InvocationRuntimeFiles.OpenSharedRuntimeFile(WebPayloadPath(paths, project));
    }

    internal static void AcquireWebForProcessLifetime(HostPaths paths, ProjectSlug project) =>
        ProcessPayloadLeases.Add(AcquireWeb(paths, project));

    internal static void RequireStartupAllowed(HostPaths paths)
    {
        foreach (var reservation in ReadAll(paths))
            if (!reservation.Released)
                throw new InvalidOperationException($"Upgrade {reservation.Reservation.OperationId} suppresses startup. Inspect the retained upgrade operation.");
    }

    internal static void RequireCurrent(HostPaths paths, UpgradeReservation expected)
    {
        var active = ReadAll(paths).Where(r => !r.Released).ToArray();
        if (active.Length != 1 || active[0].Reservation != expected)
            throw new InvalidOperationException("Upgrade operation authority changed. Retain the boundary.");
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

    private static List<(UpgradeReservation Reservation, bool Released)> ReadAll(HostPaths paths)
    {
        var result = new List<(UpgradeReservation, bool)>();
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
                if (!entries.Contains("reservation.json") || entries.Any(n => n is not ("reservation.json" or "released.json" or "writer.lock")))
                    throw new InvalidDataException($"Interrupted upgrade interlock at '{directory}'. Retain all files.");
                var reservation = Read(paths, Path.Combine(directory, "reservation.json"));
                if (reservation.OperationId != Path.GetFileName(directory)) throw new InvalidDataException("Foreign upgrade interlock.");
                var released = entries.Contains("released.json");
                if (released && Read(paths, Path.Combine(directory, "released.json")) != reservation)
                    throw new InvalidDataException("Upgrade release identity changed.");
                result.Add((reservation, released));
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

    private static void Validate(HostPaths paths, UpgradeReservation reservation)
    {
        SupervisionValidation.ValidateId(reservation.OperationId);
        if (reservation.Version != 1 || reservation.Root != paths.Root || reservation.ScopeDigest is not { Length: 64 }
            || !reservation.ScopeDigest.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Foreign upgrade interlock.");
    }

    private static async Task PublishAsync(string directory, string name, UpgradeReservation value, CancellationToken token)
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
