using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Execution;

internal sealed record CleanupRetryRequest(int Version, string RequestId, string RuntimeId,
    string BeadId, string Revision, DateTimeOffset QueuedAt, string Outcome);

/// <summary>Cross-process transport only. The Web caller must first apply local mutation policy.
/// Acceptance means durably queued; only the repository owner can inspect and execute it.</summary>
internal sealed class CleanupRetryInbox(HostPaths paths, ProjectConfig project, ValidatedTargetRepository repository)
{
    private string Root => Path.Combine(paths.CleanupRoot(project.Name), "requests");
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };

    internal static string Revision(CompletedEnvironmentObligation obligation) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(obligation)));

    internal async Task<CleanupRetryRequest> QueueAsync(string runtimeId, string beadId, string revision,
        Guid requestId, CancellationToken cancellationToken)
    {
        var owner = new FactoryTelemetryReader(paths, project).Read();
        if (owner.Availability != FactoryTelemetryAvailability.Live || owner.Snapshot?.RuntimeId != runtimeId)
            throw new InvalidOperationException("Cleanup Retry requires the displayed live runtime owner.");
        CleanupRetryAcceptance.RequireAccepting(paths, project, runtimeId);
        var record = new CompletedEnvironmentStore(paths, project.Name, repository).Read().Obligations
            .SingleOrDefault(item => item.BeadId == beadId);
        if (record is null || record.Status != CleanupStatus.NeedsAttention || Revision(record) != revision)
            throw new InvalidOperationException("Cleanup obligation changed or is not eligible for Retry.");
        var request = new CleanupRetryRequest(1, requestId.ToString("N"), runtimeId, beadId, revision,
            DateTimeOffset.UtcNow, "queued");
        CleanupRetryAcceptance.RequireAccepting(paths, project, runtimeId);
        var requests = Read();
        var existing = requests.SingleOrDefault(item => item.RequestId == request.RequestId);
        if (existing is not null)
        {
            if (existing.RuntimeId != runtimeId || existing.BeadId != beadId || existing.Revision != revision)
                throw new InvalidOperationException("Cleanup request ID was already used for different evidence.");
            return existing;
        }
        var queued = requests.FirstOrDefault(item => item.RuntimeId == runtimeId && item.BeadId == beadId
            && item.Revision == revision && item.Outcome == "queued");
        if (queued is not null) return queued;
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return request;
    }

    internal IReadOnlyList<CleanupRetryRequest> Read()
    {
        var root = RuntimeDirectoryReader.ReadRoot(Root);
        if (root.State == DirectoryState.Missing) return [];
        if (root.State != DirectoryState.Present) throw new IOException("Cleanup Retry inbox is unavailable.");
        var requests = new List<CleanupRetryRequest>();
        foreach (var entry in root.Entries)
        {
            using var pinned = OperatingSystem.IsWindows() ? InvocationRuntimeFiles.OpenExistingCleanupRecordForRead(entry.Path) : null;
            using var portable = pinned is null ? File.OpenRead(entry.Path) : null;
            var stream = pinned?.Stream ?? portable!;
            if (stream.Length > 4096) throw new InvalidDataException("Oversized cleanup Retry request.");
            var request = JsonSerializer.Deserialize<CleanupRetryRequest>(stream, Json);
            if (request is null || request.Version != 1 || !Guid.TryParseExact(request.RequestId, "N", out _)
                || entry.Path != Path.Combine(Root, request.RequestId + ".json")
                || !RuntimePaths.SafeId(request.BeadId) || !WorktreeProvisioner.IsSafeBeadId(request.BeadId)
                || request.RuntimeId.Length is < 16 or > 64 || request.Revision.Length != 64
                || request.Outcome is not ("queued" or "accepted" or "stale" or "protected" or "expired"))
                throw new InvalidDataException("Invalid cleanup Retry request; evidence retained.");
            requests.Add(request);
        }
        return requests;
    }

    internal Task WriteAsync(CleanupRetryRequest request, CancellationToken token) =>
        InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(Path.Combine(Root, request.RequestId + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(request, Json), token);

    internal void TrimAcknowledged()
    {
        foreach (var item in Read().Where(item => item.Outcome != "queued")
                     .OrderByDescending(item => item.QueuedAt).Skip(100))
        {
            var path = Path.Combine(Root, item.RequestId + ".json");
            if (OperatingSystem.IsWindows())
            {
                using var pinned = InvocationRuntimeFiles.OpenExistingCleanupRecordForRead(path);
                File.Delete(path);
            }
            else File.Delete(path);
        }
    }
}
