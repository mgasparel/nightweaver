using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Execution;

internal sealed record CleanupStatusRow(string BeadId, string Status, string Revision,
    CompletedEnvironmentObligation Record, CleanupRetryRequest? Retry);
internal sealed record CleanupStatusPage(string Project, string Repository, string Availability,
    string? Reason, DateTimeOffset ObservedAt, string? RuntimeId, bool CanRetry,
    IReadOnlyDictionary<string, int>? Counts, int Page, int Pages, int Total,
    IReadOnlyList<CleanupStatusRow> Items);

/// <summary>Reads validated durable evidence, never disposal authority. Display bounds do not
/// discard unresolved obligations. Runtime availability is independent of persisted status.</summary>
internal sealed class CleanupStatusReader(HostPaths paths, ProjectConfig project, ValidatedTargetRepository repository)
{
    internal const int PageSize = 8;

    internal static string StatusName(CleanupStatus status) => status switch
    {
        CleanupStatus.RetryScheduled => "retry-scheduled",
        CleanupStatus.NeedsAttention => "needs-attention",
        _ => status.ToString().ToLowerInvariant()
    };

    internal CleanupStatusPage Read(int page = 1, string? status = null, string? search = null)
    {
        var owner = new FactoryTelemetryReader(paths, project).Read();
        var availability = owner.Availability.ToString().ToLowerInvariant();
        var reason = owner.Reason;
        var canRetry = false;
        if (owner.Availability == FactoryTelemetryAvailability.Live && owner.Snapshot is { } live)
        {
            try { CleanupRetryAcceptance.RequireAccepting(paths, project, live.RuntimeId); canRetry = true; }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException
                or IOException or UnauthorizedAccessException) { }
            if (!canRetry)
            {
                availability = "unavailable";
                reason = "The owner is not accepting cleanup requests. Stored evidence remains available.";
            }
        }
        try
        {
            var snapshot = new CompletedEnvironmentStore(paths, project.Name, repository).Read();
            if (!snapshot.Exists) return Unavailable("Cleanup store is missing; initialization or reconciliation is required.");
            var requests = new CleanupRetryInbox(paths, project, repository).Read();
            var counts = Enum.GetValues<CleanupStatus>().ToDictionary(StatusName,
                state => snapshot.Obligations.Count(record => record.Status == state));
            var matching = snapshot.Obligations.Where(record =>
                (status is null || StatusName(record.Status) == status)
                && (string.IsNullOrWhiteSpace(search) || new[] { record.BeadId, record.Worktree.Path,
                    record.Tmp.Path, record.Diagnostic, record.LastFailure, record.LastDenial }
                    .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)))
                .OrderBy(record => record.Status == CleanupStatus.Resolved ? 1 : 0)
                .ThenBy(record => record.BeadId, StringComparer.Ordinal).ToArray();
            var pages = Math.Max(1, (matching.Length + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 1, pages);
            return new(project.Name.Value, repository.Path, availability, reason, DateTimeOffset.UtcNow,
                owner.Snapshot?.RuntimeId, canRetry, counts, page, pages, matching.Length,
                matching.Skip((page - 1) * PageSize).Take(PageSize).Select(record => new CleanupStatusRow(
                    record.BeadId, StatusName(record.Status), CleanupRetryInbox.Revision(record), record,
                    requests.Where(request => request.BeadId == record.BeadId)
                        .OrderByDescending(request => request.QueuedAt).FirstOrDefault())).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            return Unavailable("Cleanup evidence cannot be validated. Resources and diagnostic files are retained for inspection.");
        }

        CleanupStatusPage Unavailable(string reason) => new(project.Name.Value, repository.Path,
            "unavailable", reason, DateTimeOffset.UtcNow, owner.Snapshot?.RuntimeId, false,
            null, 1, 1, 0, []);
    }
}
