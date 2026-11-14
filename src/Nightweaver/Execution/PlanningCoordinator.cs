using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Reusable bounded Planning execution for a caller that already owns the Product claim.
/// Admission, interrupted-claim recovery and continuous pool registration belong to the caller.</summary>
internal sealed class PlanningCoordinator
{
    private readonly BeadsReader reader;
    private readonly DeliveryRoleRunner runRole;

    internal PlanningCoordinator(IProcessRunner runner, DeliveryRoleRunner? runRole = null)
    {
        reader = new(runner);
        var retries = new OperationalRetryCoordinator(runner);
        this.runRole = runRole ?? ((paths, project, repository, id, role, actor, state, token, progress) =>
            retries.RunAsync(paths, project, repository, id, role, actor, token, progress, state));
    }

    internal async Task<OperationalRetryOutcome?> RunClaimedAsync(HostPaths paths, ProjectConfig project,
        ValidatedTargetRepository repository, string beadId, BeadWorkflowState expectedState,
        CancellationToken token, Action<OperationalRetryProgress>? progress = null)
    {
        var state = expectedState;
        OperationalRetryOutcome? latest = null;
        while (!ContinuousAgentLaunchContext.StopRequested)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = await reader.ReadSnapshotWithReadinessAsync(repository, project.RecoveryActor, token);
            if (!snapshot.Inventory.ById.TryGetValue(beadId, out var bead)
                || BeadWorkflowState.Parse(bead) != state || bead.Assignee != project.RecoveryActor
                || state.Status != "in_progress" || !snapshot.Readiness.RecoverableOwnedReady.Contains(beadId))
                throw new AgentStageInputException("Planning claim, workflow or native readiness changed; retained work was preserved.");
            if (!AuthorityMatrix.TryInferLifecycleRole(state, out var role, out var reason)
                || role is not (AgentRole.Planner or AgentRole.PlanChallenger))
                throw new AgentStageInputException(reason ?? "Planning requires a Product at plan or plan-challenge.");
            var refusal = AuthorityMatrix.ValidateInput(role, state);
            if (refusal is not null) throw new AgentStageInputException(refusal);
            latest = await runRole(paths, project, repository, beadId, role, project.RecoveryActor, state, token, progress);
            if (!latest.Succeeded || latest.StageResult.PublicationReceipt is { ClaimReleased: true }) return latest;
            var next = latest.StageResult.VerifiedState
                ?? throw new AgentStageInputException("Planning turn returned no verified workflow state.");
            // Require a forward stage or budget change, never replay a no-op turn indefinitely.
            var advanced = role == AgentRole.Planner
                ? next == (state with { Stage = "stage:plan-challenge" })
                : next == (state with { Stage = "stage:plan-approved" })
                    || state.RepairCycles < project.Limits.MaxRepairCycles
                    && next == (state with { Stage = "stage:plan", RepairCycles = state.RepairCycles + 1 });
            if (!advanced)
                throw new AgentStageInputException("Planning turn made no verified bounded progress.");
            state = next;
        }
        return latest;
    }
}
