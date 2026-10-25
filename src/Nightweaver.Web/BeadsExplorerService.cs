using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record BeadExplorerItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("assignee")] string? Assignee);

internal sealed class BeadsExplorerService(
    ProjectConfig project,
    TargetRepositoryValidator targetRepositoryValidator,
    BeadsReader beadsReader) : IDisposable
{
    private readonly SemaphoreSlim readGate = new(1, 1);

    public void Dispose() => readGate.Dispose();

    internal async Task<IReadOnlyList<BeadExplorerItem>> ReadAsync(string view, string? query, CancellationToken cancellationToken)
    {
        await readGate.WaitAsync(cancellationToken);
        try
        {
            var target = await targetRepositoryValidator.ValidateAsync(
                project.TargetRepository, project.ConfigFilePath, cancellationToken);
            var inventory = await beadsReader.ReadSnapshotAsync(target, cancellationToken);
            FrozenSet<string>? readyIds = null;
            FrozenSet<string>? blockedIds = null;
            if (view == "ready")
                readyIds = await beadsReader.ReadExplorerReadinessAsync(target, cancellationToken);
            else if (view == "blocked")
                blockedIds = await beadsReader.ReadExplorerBlockedAsync(target, cancellationToken);

            // Project the complete inventory first so malformed required display data cannot
            // produce a partial response whose completeness depends on the selected filter.
            var items = inventory.ById.Values.Select(Project).ToArray();
            return items
                .Where(item => IsInView(item, view, readyIds, blockedIds))
                .Where(item => query is null
                    || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            readGate.Release();
        }
    }

    private static bool IsInView(BeadExplorerItem item, string view, FrozenSet<string>? readyIds, FrozenSet<string>? blockedIds)
        => view switch
        {
            "all" => true,
            "active" => item.Status == "in_progress",
            "ready" => item.Status == "open" && readyIds!.Contains(item.Id),
            "blocked" => item.Status != "closed" && (item.Status == "blocked" || blockedIds!.Contains(item.Id)),
            "closed" => item.Status == "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unknown Beads view")
        };

    private static BeadExplorerItem Project(BeadSnapshot bead)
    {
        var titles = bead.Payload.EnumerateObject().Where(property => property.NameEquals("title")).ToArray();
        if (titles.Length != 1 || titles[0].Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(titles[0].Value.GetString()))
            throw new JsonException($"Bead '{bead.Id}' has no valid title string.");

        return new BeadExplorerItem(bead.Id, titles[0].Value.GetString()!, bead.IssueType,
            bead.Priority, bead.Status, bead.Labels, string.IsNullOrWhiteSpace(bead.Assignee) ? null : bead.Assignee);
    }
}
