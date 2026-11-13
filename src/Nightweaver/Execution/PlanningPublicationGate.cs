using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal static class PlanningPublicationGate
{
    internal static bool Allows(BeadSnapshot bead, BeadsInventory inventory)
    {
        var product = PlanningPublisher.Metadata(bead, "publication_product");
        if (product is null) return true; // Native manual tasks retain their existing admission contract.
        var revision = PlanningPublisher.Metadata(bead, "publication_revision");
        if (!(revision is not null && inventory.ById.TryGetValue(product, out var parent)
            && parent.Labels.Contains("planned", StringComparer.Ordinal)
            && !parent.Labels.Contains("needs-human", StringComparer.Ordinal)
            && !parent.Labels.Contains("product-decision-required", StringComparer.Ordinal)
            && PlanningPublisher.Metadata(parent, "publication_revision") == revision)) return false;
        try
        {
            var graph = PlanningPublisher.Metadata(parent, "publication_graph");
            if (graph is null) return false;
            var intended = JsonSerializer.Deserialize<PublishedPlanningTask[]>(graph, PlanningEvidence.JsonOptions);
            if (intended is not { Length: > 0 } || intended.Any(t => t is null || t.Dependencies is null)
                || !intended.Any(t => t.Id == bead.Id)) return false;
            foreach (var task in intended)
            {
                if (!inventory.ById.TryGetValue(task.Id, out var actual) || actual.IssueType != "task"
                    || PlanningPublisher.Metadata(actual, "publication_product") != product
                    || PlanningPublisher.Metadata(actual, "publication_revision") != revision
                    || Text(actual, "title") != task.Title || Text(actual, "description") != task.Description
                    || Text(actual, "acceptance_criteria") != task.AcceptanceCriteria) return false;
                var dependencies = actual.Payload.TryGetProperty("dependencies", out var edges)
                    ? BeadsReader.ParseDownDependencies(edges, actual.Id) : [];
                var expected = task.Dependencies.Select(id => new BeadDependency(task.Id, id, "blocks"))
                    .Append(new(task.Id, product, "discovered-from"));
                if (!dependencies.ToHashSet().SetEquals(expected)) return false;
            }
            var intendedIds = intended.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            return !inventory.ById.Values.Any(b => !intendedIds.Contains(b.Id)
                && b.Payload.TryGetProperty("dependencies", out var edges)
                && BeadsReader.ParseDownDependencies(edges, b.Id).Any(e => e.Type == "discovered-from" && e.DependsOnId == product));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private static string? Text(BeadSnapshot bead, string name) =>
        bead.Payload.TryGetProperty(name, out var value) ? value.GetString() : null;
}

internal sealed record PublishedPlanningTask(string Id, string Title, string Description,
    string AcceptanceCriteria, string[] Dependencies);
