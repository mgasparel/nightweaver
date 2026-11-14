using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

// Read-only display facts; none of these fields grants dispatch or mutation authority.
internal sealed record BeadPresentation(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("trackedStage")] string? TrackedStage,
    [property: JsonPropertyName("planning")] string? Planning,
    [property: JsonPropertyName("publication")] string? Publication,
    [property: JsonPropertyName("pause")] string? Pause,
    [property: JsonPropertyName("ownership")] string Ownership,
    [property: JsonPropertyName("repairsUsed")] int? RepairsUsed,
    [property: JsonPropertyName("repairsMax")] int? RepairsMax,
    [property: JsonPropertyName("diagnostic")] string? Diagnostic);

internal static class BeadPresenter
{
    internal static string Role(string? type) => type switch
    {
        "feature" or "bug" => "Product Bead",
        "task" => "Plan Bead",
        null or "" => "Role unavailable",
        _ => "Outside Nightweaver lifecycle"
    };

    internal static BeadPresentation Project(BeadSnapshot bead, int? maxRepairs = null, BeadsInventory? inventory = null)
    {
        var role = Role(bead.IssueType);
        var ownership = bead.Assignee is { } owner ? $"Tracker ownership: {owner}" : "Tracker ownership: unassigned";
        var observation = BeadWorkflowObservation.Read(bead);
        var state = observation.State;
        var diagnostic = observation.Error;
        if (state is not null && !AuthorityMatrix.TryInferLifecycleRole(state, out _, out var reason)) diagnostic = reason;
        if (state is { Planned: true } && bead.IssueType is not ("feature" or "bug"))
            diagnostic = "The planned label requires a Product Bead.";
        var stage = diagnostic is null && state?.Stage is { } label ? StageName(label) : null;
        var pause = diagnostic is not null ? null : state is { ProductDecisionRequired: true } ? "Pause: product decision"
            : state is { NeedsHuman: true } ? "Pause: technical attention" : null;
        string? planning = null;
        if (diagnostic is null && bead.IssueType is "feature" or "bug")
            planning = state!.Planned
                ? AttentionDeskProjector.IsTerminal(bead) ? "Plan published" : "Plan published / awaiting Delivery"
                : AttentionDeskProjector.IsTerminal(bead) ? null
                : state.ReadyForPlanning ? "Authorized for Planning" : "Awaiting Planning authorization";
        var publication = Publication(bead, inventory);
        var tracked = state?.Planned == true ? planning
            : stage is not null ? $"Tracked stage: {stage}"
            : planning ?? (AttentionDeskProjector.IsTerminal(bead) ? $"Tracker status: {bead.Status}" : "Initial Delivery state");
        var summary = diagnostic is not null ? "Workflow unavailable / unsupported"
            : string.Join(" · ", new[] { pause, publication, tracked }
                .Where(value => value is not null));
        if (pause is not null)
            summary += observation.RepairCycles is { } used && maxRepairs is { } maximum
                ? $" · Repairs: {used} used / {maximum} configured" : " · Repair budget unavailable";
        return new(role, summary, stage, planning, publication, pause, ownership,
            observation.RepairCycles, maxRepairs, diagnostic);
    }

    internal static string StageName(string stage) => stage switch
    {
        "stage:plan" => "Planning", "stage:plan-challenge" => "Plan review", "stage:plan-approved" => "Plan approved",
        "stage:implement" => "Implementation", "stage:review" => "Review", "stage:qa" => "QA", "stage:merge" => "Merge",
        _ => "Unavailable"
    };

    private static string? Publication(BeadSnapshot bead, BeadsInventory? inventory)
    {
        if (bead.IssueType is "feature" or "bug")
        {
            if (!PlanningPublicationGate.HasPublicationMetadata(bead)) return null;
            if (inventory is null) return "Publication observation unavailable";
            return PlanningPublicationGate.HasProductGraphConflict(bead, inventory) switch
            {
                true => "Publication needs attention", false => "Publication graph observed", _ => "Publication observation unavailable"
            };
        }
        if (bead.IssueType != "task") return null;
        var held = bead.Labels.Contains("plan-unpublished", StringComparer.Ordinal);
        try
        {
            if (inventory is null) return held ? "Publication hold / verification unavailable" : "Publication observation unavailable";
            var products = PlanningPublicationGate.Products(bead, inventory);
            var metadata = PlanningPublicationGate.HasPublicationMetadata(bead);
            if (products.Count == 0 && !metadata) return held ? "Publication hold / verification unavailable" : null;
            var conflict = PlanningPublicationGate.ObserveGraphConflict(bead, inventory);
            if (held && conflict is null)
                return "Publication hold / verification unavailable";
            if (conflict != false) return "Publication needs attention";
            if (held) return "Publication hold";
            return PlanningPublicationGate.Allows(bead, inventory) ? "Published graph observed" : "Awaiting complete publication";
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return "Publication observation unavailable";
        }
    }
}
