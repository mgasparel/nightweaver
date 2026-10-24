using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal enum TaskStageExclusionCode
{
    UnknownStage,
    MultipleStages
}

internal sealed record TaskPoolCandidate(BeadSnapshot Bead, AgentRole Role, int Rank, bool RecoveredAfterClaimRelease);
internal sealed record TaskPoolExclusion(string BeadId, TaskStageExclusionCode Code, IReadOnlyList<string> StageLabels);

internal sealed record DeliveryDiscoveryPlan(
    int MaxConcurrentDelivery,
    int MergerCapacity,
    IReadOnlyList<TaskPoolCandidate> DeliverySelected,
    IReadOnlyList<TaskPoolCandidate> DeliveryWaiting,
    IReadOnlyList<TaskPoolCandidate> MergerSelected,
    IReadOnlyList<TaskPoolCandidate> MergerWaiting,
    IReadOnlyList<TaskPoolExclusion> Excluded);
