using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal static class PlanningPoolPlanner
{
    internal static PlanningPoolPlan Plan(StartupPlan startupPlan, BeadsReadiness readiness,
        int maxConcurrentPlanning, int maxActivePlanBeads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentPlanning);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActivePlanBeads);

        var inventory = startupPlan.Inventory;
        var postSweep = PostSweepReadinessProjector.Project(startupPlan, readiness);
        var activeCount = inventory.ById.Values.Count(bead => bead.IssueType == "task"
            && bead.Status is "open" or "in_progress"
            && !HasPauseLabel(bead));
        var backpressure = activeCount >= maxActivePlanBeads;
        var candidates = new List<(BeadSnapshot Bead, PlanningRole Role, bool Recovered)>();
        var excluded = new List<PlanningPoolExclusion>();

        foreach (var id in postSweep.ReadyBeadIds.Order(StringComparer.Ordinal))
        {
            if (!inventory.ById.TryGetValue(id, out var bead) || !IsProductBead(bead) || HasPauseLabel(bead) || bead.Labels.Contains("planned", StringComparer.Ordinal))
                continue;
            var recovered = postSweep.RecoveredReadyBeadIds.Contains(id);
            var unassignedAfterSweep = recovered || (bead.Status == "open" && bead.Assignee is null);
            if (!unassignedAfterSweep) continue;

            var stages = StageLabelParser.Parse(bead.Labels);
            if (stages.Count > 1)
            {
                excluded.Add(new PlanningPoolExclusion(bead.Id, PlanningExclusionCode.MultipleStages, stages));
                continue;
            }
            if (stages.Count == 1 && stages[0] is not ("stage:plan" or "stage:plan-challenge" or "stage:plan-approved"))
            {
                excluded.Add(new PlanningPoolExclusion(bead.Id, PlanningExclusionCode.UnknownStage, stages));
                continue;
            }

            var role = stages.Count == 1 && stages[0] == "stage:plan-challenge"
                ? PlanningRole.PlanChallenger : PlanningRole.Planner;
            candidates.Add((bead, role, recovered));
        }

        candidates.Sort((left, right) => ProductBeadOrder.Compare(left.Bead, right.Bead));
        var ranked = candidates.Select((candidate, index) => new PlanningPoolCandidate(candidate.Bead, candidate.Role, index + 1, candidate.Recovered)).ToArray();
        var selected = backpressure ? Array.Empty<PlanningPoolCandidate>() : ranked.Take(maxConcurrentPlanning).ToArray();
        var selectedIds = selected.Select(candidate => candidate.Bead.Id).ToHashSet(StringComparer.Ordinal);
        var waiting = ranked.Where(candidate => !selectedIds.Contains(candidate.Bead.Id))
            .Select(candidate => new PlanningPoolWaiting(candidate, backpressure ? PlanningWaitReason.Backpressure : PlanningWaitReason.Capacity))
            .ToArray();
        var orderedExcluded = excluded.OrderBy(item => item.BeadId, StringComparer.Ordinal)
            .ThenBy(item => item.Code)
            .Select(item => item with { StageLabels = item.StageLabels.Order(StringComparer.Ordinal).ToArray() })
            .ToArray();
        return new PlanningPoolPlan(activeCount, maxActivePlanBeads, maxConcurrentPlanning, backpressure, postSweep,
            selected, waiting, orderedExcluded);
    }

    private static bool IsProductBead(BeadSnapshot bead) => bead.IssueType is "feature" or "bug";

    private static bool HasPauseLabel(BeadSnapshot bead) => bead.Labels.Contains("product-decision-required", StringComparer.Ordinal)
        || bead.Labels.Contains("needs-human", StringComparer.Ordinal);
}

internal static class ProductBeadOrder
{
    internal static int Compare(BeadSnapshot left, BeadSnapshot right)
    {
        var priority = left.Priority.CompareTo(right.Priority);
        if (priority != 0) return priority;
        var created = left.CreatedAt.CompareTo(right.CreatedAt);
        return created != 0 ? created : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }
}

internal static class StageLabelParser
{
    internal static IReadOnlyList<string> Parse(IEnumerable<string> labels) => labels
        .Where(label => label.StartsWith("stage:", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
