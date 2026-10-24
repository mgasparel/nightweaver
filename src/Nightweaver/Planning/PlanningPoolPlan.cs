using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal enum PlanningRole
{
    Planner,
    PlanChallenger
}

internal enum PlanningWaitReason
{
    Capacity,
    Backpressure
}

internal enum PlanningExclusionCode
{
    UnknownStage,
    MultipleStages
}

internal sealed record PlanningPoolCandidate(BeadSnapshot Bead, PlanningRole Role, int Rank, bool RecoveredAfterClaimRelease);
internal sealed record PlanningPoolWaiting(PlanningPoolCandidate Candidate, PlanningWaitReason Reason);
internal sealed record PlanningPoolExclusion(string BeadId, PlanningExclusionCode Code, IReadOnlyList<string> StageLabels);

internal sealed record PlanningPoolPlan(
    int ActivePlanBeadCount,
    int MaxActivePlanBeads,
    int MaxConcurrentPlanning,
    bool BackpressureActive,
    PostSweepReadiness Readiness,
    IReadOnlyList<PlanningPoolCandidate> Selected,
    IReadOnlyList<PlanningPoolWaiting> Waiting,
    IReadOnlyList<PlanningPoolExclusion> Excluded);
