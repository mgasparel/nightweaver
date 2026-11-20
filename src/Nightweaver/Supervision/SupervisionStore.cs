using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Supervision;

internal enum SupervisionAvailability { Manual, Available, Unavailable }
internal sealed record SupervisionRead(SupervisionAvailability Availability, SupervisionState? State, string Path, string? Diagnostic);

/// <summary>Windows local operational storage only; construction and reads never initialize or opt in.
/// Writers serialize under a pinned file lock and compare the observed revision. No execution APIs.</summary>
internal sealed class SupervisionStore
{
    internal const int MaximumBytes = 64 * 1024;
    private readonly string directory;
    private readonly SupervisionIdentity identity;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<SupervisionHealth>(allowIntegerValues: false) }
    };

    internal SupervisionStore(HostPaths paths, ProjectSlug project, SupervisionIdentity identity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Supervision v1 requires Windows no-follow storage.");
        SupervisionValidation.Validate(identity);
        if (!string.Equals(paths.Root, identity.Root, StringComparison.Ordinal))
            throw new InvalidDataException("Supervision identity does not match this host root.");
        directory = paths.SupervisionRoot(project);
        this.identity = identity;
        Path = System.IO.Path.Combine(directory, "state.json");
    }

    internal string Path { get; }

    internal SupervisionRead Read()
    {
        try
        {
            // Pin before enumeration: File.Exists would conflate access denial with opt-out.
            using var pins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
            if (Directory.EnumerateFileSystemEntries(directory, "stop-intent.uncertain*").Any())
                throw new InvalidDataException("Stop intent persistence was uncertain; inspect the retained interlock before recovery.");
            if (Directory.EnumerateFileSystemEntries(directory, "state.json.*.tmp").Any())
                throw new InvalidDataException("Interrupted atomic publication retained; inspect before recovery.");
            byte[] bytes;
            try { bytes = InvocationRuntimeFiles.ReadRuntimeFileBounded(Path, MaximumBytes); }
            catch (IOException exception) when (IsMissing(exception))
            { return new(SupervisionAvailability.Manual, null, Path, null); }
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            RejectDuplicateKeys(json.RootElement);
            var state = JsonSerializer.Deserialize<SupervisionState>(bytes, Options)
                ?? throw new InvalidDataException("Supervision state is null.");
            SupervisionValidation.Validate(state);
            if (state.Identity != identity) throw new InvalidDataException("Foreign installation identity; no recovery authority.");
            return new(SupervisionAvailability.Available, state, Path, null);
        }
        catch (IOException exception) when (IsMissing(exception))
        { return new(SupervisionAvailability.Manual, null, Path, null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        { return new(SupervisionAvailability.Unavailable, null, Path, exception.Message); }
    }

    internal Task LatchUncertainStopAsync() => InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(
        System.IO.Path.Combine(directory, "stop-intent.uncertain"),
        "Stop intent could not be confirmed. Recovery is unavailable until explicit operator inspection."u8.ToArray(),
        CancellationToken.None);

    internal async Task<SupervisionState> SaveAsync(long? expectedRevision, SupervisionState next, CancellationToken cancellationToken)
    {
        SupervisionValidation.Validate(next);
        if (next.Identity != identity || next.Revision != (expectedRevision ?? 0))
            throw new InvalidDataException("Candidate does not match the expected installation/revision.");
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
            System.IO.Path.Combine(directory, "writer.lock"), createMissingDirectories: true);
        var current = Read();
        if (current.Availability == SupervisionAvailability.Unavailable)
            throw new InvalidDataException($"Supervision store unavailable at '{Path}': {current.Diagnostic}");
        if (current.State?.Revision != expectedRevision)
            throw new InvalidOperationException("Supervision revision changed; reread before issuing a command.");
        var committed = next with { Revision = checked((expectedRevision ?? 0) + 1) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(committed, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Supervision state exceeds its byte budget.");
        await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(Path, bytes, cancellationToken).ConfigureAwait(false);
        return committed;
    }

    private static bool IsMissing(IOException exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException
        || exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate supervision JSON property.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
