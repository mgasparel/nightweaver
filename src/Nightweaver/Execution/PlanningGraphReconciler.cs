using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Surfaces immutable publication-graph conflicts as human attention without
/// rewriting a task graph or taking over a foreign/completed task.</summary>
internal sealed class PlanningGraphReconciler(IProcessRunner runner)
{
    private readonly BeadsReader reader = new(runner);
    private readonly BeadsWriter writer = new(runner);

    internal async Task<bool> PauseChangedGraphsAsync(ValidatedTargetRepository repository,
        BeadsInventory inventory, string actor, CancellationToken token)
    {
        var changed = false;
        foreach (var candidate in inventory.ById.Values)
        {
            var product = PlanningPublicationGate.Products(candidate, inventory)
                .Order(StringComparer.Ordinal).FirstOrDefault(id => inventory.ById.TryGetValue(id, out var parent)
                    && (parent.Labels.Contains("planned", StringComparer.Ordinal)
                        || PlanningPublisher.Metadata(parent, "publication_graph") is not null
                            && PlanningPublicationGate.HasGraphConflict(candidate, inventory)));
            if (candidate.IssueType != "task" || product is null
                || PlanningPublicationGate.Allows(candidate, inventory)) continue;
            if (ContinuousAgentLaunchContext.StopRequested) break;
            var fresh = await reader.ReadSnapshotAsync(repository, token);
            if (!fresh.ById.TryGetValue(candidate.Id, out var current)
                || !fresh.ById.TryGetValue(product, out var currentParent)
                || !(currentParent.Labels.Contains("planned", StringComparer.Ordinal)
                    || PlanningPublisher.Metadata(currentParent, "publication_graph") is not null
                        && PlanningPublicationGate.HasGraphConflict(current, fresh))
                || PlanningPublicationGate.Allows(current, fresh)
                || current.Labels.Contains("needs-human", StringComparer.Ordinal)
                || current.Labels.Contains("product-decision-required", StringComparer.Ordinal)
                || !(current.Status == "open" && current.Assignee is null
                    || current.Status == "in_progress" && current.Assignee == actor)) continue;
            await writer.AddCommentAsync(repository, current.Id,
                $"Product publication graph '{product}' no longer matches its verified task content and dependencies, or its published Product is paused. "
                + "Preserved completed and in-flight work. Reconcile the graph with human guidance before explicit Resume/Retry; automatic replanning is refused.", token);
            await runner.RunAsync(BeadsCommand.CreateWrite(repository.Path,
                ["update", current.Id, "--add-label", "needs-human", "--status", current.Status,
                    "--assignee", current.Assignee ?? "", "--if-status", current.Status,
                    "--if-assignee", current.Assignee ?? "", "--json"], actor), token);
            var confirmed = await reader.ReadDetailAsync(repository, current.Id, token);
            if (!confirmed.Labels.Contains("needs-human", StringComparer.Ordinal))
                throw new ToolExecutionException($"Published graph conflict pause for '{current.Id}' was not confirmed; no task was admitted.");
            changed = true;
        }
        return changed;
    }
}
