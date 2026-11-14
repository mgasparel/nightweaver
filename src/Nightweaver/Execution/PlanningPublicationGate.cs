using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal static class PlanningPublicationGate
{
    internal static bool Allows(BeadSnapshot bead, BeadsInventory inventory)
        => CheckGraph(bead, inventory, requirePublished: true);

    internal static bool HasGraphConflict(BeadSnapshot bead, BeadsInventory inventory)
        => !CheckGraph(bead, inventory, requirePublished: false);

    // Read-only Product observation uses the same complete graph check as task admission.
    // Absence of a snapshot supplies no evidence of graph validity.
    internal static bool? HasProductGraphConflict(BeadSnapshot product, BeadsInventory inventory)
    {
        var graph = PlanningPublisher.Metadata(product, "publication_graph");
        if (graph is null) return null;
        try
        {
            var intended = JsonSerializer.Deserialize<PublishedPlanningTask[]>(graph, PlanningEvidence.JsonOptions);
            if (intended is not { Length: > 0 } || intended[0] is null
                || !inventory.ById.TryGetValue(intended[0].Id, out var task) || task.IssueType != "task") return true;
            return HasGraphConflict(task, inventory);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return true; }
    }

    private static bool CheckGraph(BeadSnapshot bead, BeadsInventory inventory, bool requirePublished)
    {
        if (bead.IssueType != "task") return true;
        var product = PlanningPublisher.Metadata(bead, "publication_product");
        var products = Products(bead, inventory);
        if (products.Count == 0)
            return !HasPublicationMetadata(bead); // Native manual tasks retain their admission contract.
        if (product is null || products.Count != 1 || !products.Contains(product)) return false;
        var revision = PlanningPublisher.Metadata(bead, "publication_revision");
        if (!(revision is not null && inventory.ById.TryGetValue(product, out var parent)
            && (!requirePublished || parent.Labels.Contains("planned", StringComparer.Ordinal)
                && !parent.Labels.Contains("needs-human", StringComparer.Ordinal)
                && !parent.Labels.Contains("product-decision-required", StringComparer.Ordinal))
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

    // Child metadata is mutable. Retained parent membership and native provenance
    // independently identify generated tasks even when that metadata is lost.
    internal static HashSet<string> Products(BeadSnapshot bead, BeadsInventory inventory)
    {
        var products = new HashSet<string>(StringComparer.Ordinal);
        if (PlanningPublisher.Metadata(bead, "publication_product") is { } product) products.Add(product);
        var nativeParents = bead.Payload.TryGetProperty("dependencies", out var edges)
            ? BeadsReader.ParseDownDependencies(edges, bead.Id).Where(e => e.Type == "discovered-from")
                .Select(e => e.DependsOnId).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var parent in inventory.ById.Values)
        {
            if (nativeParents.Contains(parent.Id) && HasPublicationMetadata(parent)) products.Add(parent.Id);
            var graph = PlanningPublisher.Metadata(parent, "publication_graph");
            if (graph is null) continue;
            try
            {
                if (JsonSerializer.Deserialize<PublishedPlanningTask[]>(graph, PlanningEvidence.JsonOptions)
                    ?.Any(t => t is not null && t.Id == bead.Id) == true) products.Add(parent.Id);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
        }
        return products;
    }

    internal static bool HasPublicationMetadata(BeadSnapshot bead) =>
        bead.Payload.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
        && (metadata.TryGetProperty("publication_product", out _) || metadata.TryGetProperty("publication_revision", out _)
            || metadata.TryGetProperty("publication_entry", out _) || metadata.TryGetProperty("publication_graph", out _));

    private static string? Text(BeadSnapshot bead, string name) =>
        bead.Payload.TryGetProperty(name, out var value) ? value.GetString() : null;
}

internal sealed record PublishedPlanningTask(string Id, string Title, string Description,
    string AcceptanceCriteria, string[] Dependencies);
