using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Background;

internal sealed record BackgroundOwnerRecord(
    int SchemaVersion,
    string State,
    string RunId,
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    string Root,
    string ConfigPath,
    string Project,
    string? TargetRepository,
    string? RuntimeId,
    string WebUrl,
    string WebHealth,
    string RuntimeHealth,
    string HostLogPath,
    string WebLogPath,
    string? Message)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 8
    };

    internal static string PathFor(HostPaths paths, ProjectSlug project) =>
        Path.Combine(paths.BackgroundRoot(project), "owner.json");

    internal static async Task WriteAsync(string path, BackgroundOwnerRecord record, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static BackgroundOwnerRecord? TryRead(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<BackgroundOwnerRecord>(InvocationRuntimeFiles.ReadRuntimeFileBounded(path, 64 * 1024), JsonOptions);
            return record is { SchemaVersion: 1 }
                && record.State is "starting" or "running" or "stopping" or "stopped" or "degraded" or "failed"
                && record.RunId is { Length: >= 16 and <= 64 }
                && record.Root is not null && record.ConfigPath is not null && record.Project is not null
                && record.ProcessId > 0
                ? record : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }
}

internal sealed class BackgroundOwnerState(BackgroundOwnerRecord initial)
{
    private readonly object sync = new();
    private BackgroundOwnerRecord current = initial;

    internal BackgroundOwnerRecord Current { get { lock (sync) return current; } }
    internal void Update(Func<BackgroundOwnerRecord, BackgroundOwnerRecord> update)
    {
        lock (sync) current = update(current);
    }
}
