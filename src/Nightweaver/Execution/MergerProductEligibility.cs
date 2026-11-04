using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum MergerProductCheck
{
    NoCandidate,
    MultipleCandidates,
    MissingProduct,
    NotPlannedProduct,
    PausedProduct,
    AlreadyClosed,
    NotOpenOrUnassigned,
    NeedLinkedTasks,
    NoLinkedTasks,
    IncompleteTasks,
    Ready
}

/// <summary>Pure policy for the optional Product close performed after a Merger task closes.</summary>
internal static class MergerProductEligibility
{
    internal static (MergerProductCheck Check, string? ProductId) Evaluate(string taskId,
        IReadOnlyList<BeadDependency> taskEdges, BeadsInventory inventory,
        IReadOnlyList<BeadDependency>? productEdges = null)
    {
        var productIds = taskEdges.Where(edge => edge.IssueId == taskId && edge.Type == "discovered-from")
            .Select(edge => edge.DependsOnId).Distinct(StringComparer.Ordinal).ToArray();
        if (productIds.Length == 0) return (MergerProductCheck.NoCandidate, null);
        if (productIds.Length != 1) return (MergerProductCheck.MultipleCandidates, null);
        var productId = productIds[0];
        if (!inventory.ById.TryGetValue(productId, out var product))
            return (MergerProductCheck.MissingProduct, productId);
        if (product.IssueType is not ("feature" or "bug")
            || !product.Labels.Contains("planned", StringComparer.Ordinal))
            return (MergerProductCheck.NotPlannedProduct, productId);
        if (product.Labels.Contains("needs-human", StringComparer.Ordinal)
            || product.Labels.Contains("product-decision-required", StringComparer.Ordinal))
            return (MergerProductCheck.PausedProduct, productId);
        if (product.Status == "closed" && product.Assignee is null)
            return (MergerProductCheck.AlreadyClosed, productId);
        if (product.Status != "open" || product.Assignee is not null)
            return (MergerProductCheck.NotOpenOrUnassigned, productId);
        if (productEdges is null) return (MergerProductCheck.NeedLinkedTasks, productId);

        var linkedIds = productEdges.Where(edge => edge.Type == "discovered-from" && edge.DependsOnId == productId)
            .Select(edge => edge.IssueId).Distinct(StringComparer.Ordinal).ToArray();
        if (linkedIds.Length == 0) return (MergerProductCheck.NoLinkedTasks, productId);
        foreach (var linkedId in linkedIds)
        {
            if (!inventory.ById.TryGetValue(linkedId, out var linked) || linked.IssueType != "task"
                || linked.Status != "closed" || linked.Labels.Contains("needs-human", StringComparer.Ordinal)
                || linked.Labels.Contains("product-decision-required", StringComparer.Ordinal))
                return (MergerProductCheck.IncompleteTasks, productId);
        }
        return (MergerProductCheck.Ready, productId);
    }
}
