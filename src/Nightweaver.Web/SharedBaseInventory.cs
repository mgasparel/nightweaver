using System.Collections.Frozen;
using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

// One compact, indexed projection per accepted shared inventory. Search uses
// complete titles; display bounds apply only after membership and ordering.
internal sealed class SharedBaseInventory
{
    internal FrozenDictionary<string, BaseBeadRow> ById { get; }
    internal BaseBeadRow[] Ordered { get; }
    internal FrozenDictionary<string, BaseBeadRow[]> Status { get; }
    internal FrozenDictionary<string, BaseBeadRow[]> Type { get; }
    internal FrozenDictionary<string, BaseBeadRow[]> Stage { get; }
    internal FrozenDictionary<string, BaseBeadRow[]> Pause { get; }
    internal FrozenDictionary<string, BaseBeadRow[]> Assignee { get; }
    internal FrozenDictionary<int, BaseBeadRow[]> Priority { get; }
    internal string Revision { get; }
    private readonly int maxRepairCycles;
    private readonly FrozenSet<string> pausedIds;

    internal SharedBaseInventory(BeadsInventory inventory, int maxRepairs, string revision)
    {
        Revision = revision;
        maxRepairCycles = maxRepairs;
        pausedIds = inventory.ById.Values.Where(AttentionDeskProjector.IsPaused)
            .Where(bead => !AttentionDeskProjector.IsTerminal(bead)).Select(bead => bead.Id).ToFrozenSet(StringComparer.Ordinal);
        Ordered = inventory.ById.Values.Select(bead => BaseBeadRow.Project(bead, inventory, maxRepairs))
            .OrderBy(row => row.Priority).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        ById = Ordered.ToFrozenDictionary(row => row.Id, StringComparer.Ordinal);
        Status = Index(row => row.Status); Type = Index(row => row.Type);
        Stage = Index(row => row.Stage ?? ""); Assignee = Index(row => row.Assignee ?? "");
        Pause = Ordered.SelectMany(row => row.Actions.ExpectedWorkflow.PauseLabels.Select(label => (label, row)))
            .GroupBy(pair => pair.label, StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.row).ToArray(), StringComparer.Ordinal);
        Priority = Ordered.GroupBy(row => row.Priority).ToFrozenDictionary(group => group.Key, group => group.ToArray());
    }

    private FrozenDictionary<string, BaseBeadRow[]> Index(Func<BaseBeadRow, string> key)
        => Ordered.GroupBy(key, StringComparer.Ordinal).ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    internal object Workspace(string id) => new
    {
        schema = "nightweaver-base-v1", inventoryRevision = Revision, completeness = "complete",
        absence = ById.ContainsKey(id) ? null : new { id, inventoryRevision = Revision },
        bead = ById.GetValueOrDefault(id)?.ForDisplay()
    };

    internal object Attention() => new
    {
        schema = "nightweaver-base-v1", inventoryRevision = Revision, completeness = "complete", absence = (object?)null,
        items = Ordered.Where(row => pausedIds.Contains(row.Id))
            .Select(row => row.ForDisplay()).ToArray(),
        maxRepairCycles
    };

    internal object Explorer(OperationKey key, ISet<string>? ready, ISet<string>? blocked)
    {
        var candidates = key.Argument is "active" or "closed"
            ? Status.GetValueOrDefault(key.Argument == "active" ? "in_progress" : "closed") ?? [] : Ordered;
        var filtered = candidates.Where(row => key.Argument switch
        {
            "active" => row.Status == "in_progress", "closed" => row.Status == "closed",
            "ready" => row.Status == "open" && ready!.Contains(row.Id),
            "blocked" => row.Status != "closed" && (row.Status == "blocked" || blocked!.Contains(row.Id)),
            _ => true
        }).Where(row => string.IsNullOrEmpty(key.Query) || row.Id.Contains(key.Query, StringComparison.OrdinalIgnoreCase)
            || row.Title.Contains(key.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var pages = Math.Max(1, (filtered.Length + ExplorerPage.Size - 1) / ExplorerPage.Size);
        var page = Math.Clamp(key.Page, 1, pages);
        return new
        {
            schema = "nightweaver-base-v1", inventoryRevision = Revision, completeness = "complete", absence = (object?)null,
            items = filtered.Skip((page - 1) * ExplorerPage.Size).Take(ExplorerPage.Size).Select(row => row.ForDisplay()).ToArray(),
            total = filtered.Length, page, pages, pageSize = ExplorerPage.Size
        };
    }
}

internal sealed record BaseWorkflow(string? Stage, IReadOnlyList<string> PauseLabels, int? RepairCycles);
internal sealed record BaseActions(bool CanDecide, bool CanRetry, bool CanAbort, string? UnavailableReason, BaseWorkflow ExpectedWorkflow);
internal sealed record BaseBeadRow(string Id, string Title, string Type, int Priority, string Status,
    string? Assignee, IReadOnlyList<string> Labels, string? Stage, int? RepairCycles,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ClosedAt, string? CreatedBy,
    BeadPresentation Presentation, BaseActions Actions)
{
    internal static BaseBeadRow Project(BeadSnapshot bead, BeadsInventory inventory, int maxRepairs)
    {
        var item = BeadExplorerItem.FromSnapshot(bead);
        var attention = AttentionDeskProjector.ProjectWithSelection(bead, [], maxRepairs).Item;
        var valid = AttentionDeskProjector.InvalidWorkflowReason(bead) is null;
        var stage = valid ? AttentionDeskProjector.Stage(bead) : null;
        var repairs = valid ? AttentionDeskProjector.RepairCycles(bead) : null;
        var paused = AttentionDeskProjector.IsPaused(bead) && !AttentionDeskProjector.IsTerminal(bead);
        var workflow = new BaseWorkflow(stage, bead.Labels.Where(label => label is "needs-human" or "product-decision-required")
            .Order(StringComparer.Ordinal).ToArray(), repairs);
        return new(bead.Id, item.Title, bead.IssueType, bead.Priority, bead.Status, bead.Assignee, bead.Labels,
            stage, repairs, bead.CreatedAt, bead.Payload.TryGetProperty("updated_at", out var updated)
                ? Timestamp(updated) : throw new JsonException("Missing updated_at."),
            bead.Payload.TryGetProperty("closed_at", out var closed) && closed.ValueKind != JsonValueKind.Null ? Timestamp(closed) : null,
            bead.Payload.TryGetProperty("created_by", out var creator) && creator.ValueKind != JsonValueKind.Null
                ? creator.ValueKind == JsonValueKind.String ? creator.GetString() : throw new JsonException("Invalid created_by.") : null,
            BeadPresenter.Project(bead, maxRepairs, inventory), new(paused && attention.CanDecide,
                paused && attention.CanRetry, paused && attention.CanAbort, attention.UnavailableReason, workflow));
    }

    private static DateTimeOffset Timestamp(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time)
        ? time : throw new JsonException("Invalid base timestamp.");

    internal BaseBeadRow ForDisplay() => this with { Title = Title.Length <= 400 ? Title : Title[..400] + "…" };
}
