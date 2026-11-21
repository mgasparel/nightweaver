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
    public string? StopReason { get; init; }
    public string? GitCommonDirectory { get; init; }
    public int ContainmentVersion { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public BackgroundFailure? Failure { get; init; }
    public Nightweaver.Upgrade.LiveRelease? CliRelease { get; init; }
    public Nightweaver.Upgrade.LiveRelease? WebRelease { get; init; }
    public Nightweaver.Supervision.SupervisionRead? Supervision { get; init; }

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

internal sealed record BackgroundFailure(DateTimeOffset Utc, string Reason, string RunId, string? RuntimeId,
    int ProcessId, DateTimeOffset ProcessStartedAtUtc);

internal sealed class BackgroundOwnerState(BackgroundOwnerRecord initial,
    Func<string, BackgroundOwnerRecord, CancellationToken, Task>? write = null) : IDisposable
{
    private readonly object sync = new();
    private BackgroundOwnerRecord current = initial;
    private readonly SemaphoreSlim persistence = new(1, 1);

    internal BackgroundOwnerRecord Current { get { lock (sync) return current; } }
    internal void Update(Func<BackgroundOwnerRecord, BackgroundOwnerRecord> update)
    {
        persistence.Wait();
        try { lock (sync) current = update(current); }
        finally { persistence.Release(); }
    }

    // Commit proposed control state only after publication. Serialize updates too,
    // so a concurrent failure cannot be overwritten by an older proposal.
    internal async Task PublishAsync(string path, Func<BackgroundOwnerRecord, BackgroundOwnerRecord> update,
        CancellationToken token)
    {
        await persistence.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proposed = update(Current);
            await (write ?? BackgroundOwnerRecord.WriteAsync)(path, proposed, token).ConfigureAwait(false);
            lock (sync) current = proposed;
        }
        finally { persistence.Release(); }
    }

    internal async Task PersistAsync(string path, CancellationToken token)
    {
        await persistence.WaitAsync(token).ConfigureAwait(false);
        try { await (write ?? BackgroundOwnerRecord.WriteAsync)(path, Current, token).ConfigureAwait(false); }
        finally { persistence.Release(); }
    }

    public void Dispose() => persistence.Dispose();
}
