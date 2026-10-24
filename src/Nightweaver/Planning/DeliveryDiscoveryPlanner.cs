using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal static class DeliveryDiscoveryPlanner
{
    internal static DeliveryDiscoveryPlan Plan(PostSweepSnapshot snapshot, int maxConcurrentDelivery)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentDelivery);

        var delivery = new List<(BeadSnapshot Bead, AgentRole Role, bool Recovered)>();
        var merger = new List<(BeadSnapshot Bead, AgentRole Role, bool Recovered)>();
        var excluded = new List<TaskPoolExclusion>();

        foreach (var id in snapshot.Readiness.ReadyBeadIds.Order(StringComparer.Ordinal))
        {
            if (!snapshot.Inventory.ById.TryGetValue(id, out var bead)
                || bead.IssueType != "task"
                || HasPauseLabel(bead))
                continue;

            var recovered = snapshot.Readiness.RecoveredReadyBeadIds.Contains(id);
            if (!recovered && (bead.Status != "open" || bead.Assignee is not null))
                continue;

            var stages = StageLabelParser.Parse(bead.Labels);
            if (stages.Count > 1)
            {
                excluded.Add(new TaskPoolExclusion(bead.Id, TaskStageExclusionCode.MultipleStages, stages));
                continue;
            }

            if (stages.Count == 0)
            {
                delivery.Add((bead, AgentRole.Implementer, recovered));
                continue;
            }

            switch (stages[0])
            {
                case "stage:implement":
                    delivery.Add((bead, AgentRole.Implementer, recovered));
                    break;
                case "stage:review":
                    delivery.Add((bead, AgentRole.Reviewer, recovered));
                    break;
                case "stage:qa":
                    delivery.Add((bead, AgentRole.Qa, recovered));
                    break;
                case "stage:merge":
                    merger.Add((bead, AgentRole.Merger, recovered));
                    break;
                default:
                    excluded.Add(new TaskPoolExclusion(bead.Id, TaskStageExclusionCode.UnknownStage, stages));
                    break;
            }
        }

        var rankedDelivery = Rank(delivery);
        var rankedMerger = Rank(merger);
        var orderedExcluded = excluded
            .OrderBy(item => item.BeadId, StringComparer.Ordinal)
            .ThenBy(item => ExclusionName(item.Code), StringComparer.Ordinal)
            .Select(item => item with { StageLabels = item.StageLabels.Order(StringComparer.Ordinal).ToArray() })
            .ToArray();

        return new DeliveryDiscoveryPlan(
            maxConcurrentDelivery,
            ConcurrencyConfig.MergerSlots,
            rankedDelivery.Take(maxConcurrentDelivery).ToArray(),
            rankedDelivery.Skip(maxConcurrentDelivery).ToArray(),
            rankedMerger.Take(ConcurrencyConfig.MergerSlots).ToArray(),
            rankedMerger.Skip(ConcurrencyConfig.MergerSlots).ToArray(),
            orderedExcluded);
    }

    private static TaskPoolCandidate[] Rank(List<(BeadSnapshot Bead, AgentRole Role, bool Recovered)> candidates)
    {
        candidates.Sort((left, right) => BeadCandidateOrder.Compare(left.Bead, right.Bead));
        return candidates.Select((candidate, index) =>
            new TaskPoolCandidate(candidate.Bead, candidate.Role, index + 1, candidate.Recovered)).ToArray();
    }

    private static bool HasPauseLabel(BeadSnapshot bead) =>
        bead.Labels.Contains("product-decision-required", StringComparer.Ordinal)
        || bead.Labels.Contains("needs-human", StringComparer.Ordinal);

    private static string ExclusionName(TaskStageExclusionCode code) => code switch
    {
        TaskStageExclusionCode.UnknownStage => "unknown_stage",
        TaskStageExclusionCode.MultipleStages => "multiple_stages",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown exclusion code")
    };
}
