using Nightweaver.Execution;

namespace Nightweaver.Web;

// Inventory is collected once per shared sweep; page keys only select a bounded projection.
internal sealed record ExplorerPage(IReadOnlyList<ExplorerRow> Items, int Total, int Page, int Pages, int PageSize)
{
    internal const int Size = 40;
    internal static ExplorerPage Project(IReadOnlyList<BeadExplorerItem> items, int requestedPage)
    {
        var pages = Math.Max(1, (items.Count + Size - 1) / Size);
        var page = Math.Clamp(requestedPage, 1, pages);
        var selected = items.Skip((page - 1) * Size).Take(Size).ToArray();
        if (selected.Any(item => !BeadIdSafety.IsSafe(item.Id)))
            throw new InvalidDataException("Explorer contains an invalid exact Bead ID.");
        return new(selected.Select(item => new ExplorerRow(
            item.Id, Bound(item.Title, 400), Bound(item.Type, 80), item.Priority, Bound(item.Status, 80),
            item.Assignee is null ? null : Bound(item.Assignee, 200),
            Stage(item.Labels))).ToArray(), items.Count, page, pages, Size);
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
    private static string? Stage(IReadOnlyList<string> labels)
    {
        var stages = labels.Where(label => label.StartsWith("stage:", StringComparison.Ordinal)).Take(2).ToArray();
        return stages.Length switch { 0 => null, 1 => Bound(stages[0], 80), _ => "Conflicting stage labels" };
    }
}

internal sealed record ExplorerRow(string Id, string Title, string Type, int Priority, string Status, string? Assignee, string? Stage);
