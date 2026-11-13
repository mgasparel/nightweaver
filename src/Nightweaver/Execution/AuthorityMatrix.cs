using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record BeadDependency(string IssueId, string DependsOnId, string Type);

internal sealed record AuthorityMatrixResult(bool IsLegal, string? Reason)
{
    internal static AuthorityMatrixResult Legal { get; } = new(true, null);
    internal static AuthorityMatrixResult Illegal(string reason) => new(false, reason);
}

/// <summary>Pure validation of one role's allowed input and output transition.</summary>
internal static class AuthorityMatrix
{
    internal static bool TryInferLifecycleRole(BeadWorkflowState state, out AgentRole role, out string? reason)
    {
        role = default;
        reason = null;
        if (state.IssueType == "task")
        {
            var inferred = state.Stage switch
            {
                null or "stage:implement" => AgentRole.Implementer,
                "stage:review" => AgentRole.Reviewer,
                "stage:qa" => AgentRole.Qa,
                "stage:merge" => AgentRole.Merger,
                _ => (AgentRole?)null
            };
            if (inferred is { } taskRole) { role = taskRole; return true; }
            reason = $"Task Bead '{state.Id}' cannot be dispatched from stage '{state.Stage ?? "(none)"}'.";
            return false;
        }

        if (state.IssueType is "feature" or "bug")
        {
            var inferred = state.Stage switch
            {
                null or "stage:plan" or "stage:plan-approved" => AgentRole.Planner,
                "stage:plan-challenge" => AgentRole.PlanChallenger,
                _ => (AgentRole?)null
            };
            if (inferred is { } productRole) { role = productRole; return true; }
            reason = $"Product Bead '{state.Id}' cannot be dispatched from stage '{state.Stage ?? "(none)"}'.";
            return false;
        }

        reason = $"Bead '{state.Id}' has unsupported lifecycle type '{state.IssueType}'; dispatch supports task, feature, and bug.";
        return false;
    }

    internal static string? ValidateInput(AgentRole role, BeadWorkflowState state)
    {
        var expectedProduct = role is AgentRole.Planner or AgentRole.PlanChallenger;
        var expectedTask = role is AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa or AgentRole.Merger;
        if (!expectedProduct && !expectedTask)
            return role == AgentRole.Classifier ? "Classifier is not a lifecycle role for single-stage execution." : $"Unknown Agent role '{role}'.";
        if (state.Status is not ("open" or "in_progress"))
            return $"Bead '{state.Id}' must be open or in_progress before dispatch; it is '{state.Status}'.";
        if (state.Planned || state.ProductDecisionRequired || state.NeedsHuman)
            return $"Bead '{state.Id}' is already planned or paused and cannot be dispatched.";
        if (expectedProduct && state.IssueType is not ("feature" or "bug"))
            return $"Role '{role}' requires a Product Bead of type feature or bug; '{state.Id}' is '{state.IssueType}'.";
        if (expectedTask && state.IssueType != "task")
            return $"Role '{role}' requires a Plan Bead of type task; '{state.Id}' is '{state.IssueType}'.";
        if (expectedProduct && !state.ReadyForPlanning)
            return $"Product Bead '{state.Id}' requires ready-for-planning before dispatch.";
        if (expectedTask && state.PlanUnpublished)
            return $"Plan Bead '{state.Id}' is unpublished and cannot be dispatched.";

        var accepted = role switch
        {
            AgentRole.Planner => state.Stage is null or "stage:plan" or "stage:plan-approved",
            AgentRole.PlanChallenger => state.Stage == "stage:plan-challenge",
            AgentRole.Implementer => state.Stage is null or "stage:implement",
            AgentRole.Reviewer => state.Stage == "stage:review",
            AgentRole.Qa => state.Stage == "stage:qa",
            AgentRole.Merger => state.Stage == "stage:merge",
            _ => false
        };
        return accepted ? null : $"Role '{role}' cannot run from stage '{state.Stage ?? "(none)"}'.";
    }

    internal static AuthorityMatrixResult Validate(
        AgentRole role,
        BeadWorkflowState before,
        BeadWorkflowState after,
        IReadOnlyDictionary<string, BeadSnapshot> postBeads,
        IReadOnlyList<BeadDependency>? dependencies)
    {
        if (!string.Equals(before.Id, after.Id, StringComparison.Ordinal))
            return AuthorityMatrixResult.Illegal($"Bead identity changed from '{before.Id}' to '{after.Id}'.");
        if (!string.Equals(before.IssueType, after.IssueType, StringComparison.Ordinal))
            return AuthorityMatrixResult.Illegal($"Bead '{before.Id}' changed type from '{before.IssueType}' to '{after.IssueType}'.");
        if (before.ReadyForPlanning != after.ReadyForPlanning || before.PlanUnpublished != after.PlanUnpublished)
            return AuthorityMatrixResult.Illegal("Agents cannot change Planning readiness or publication holds.");
        if (after.NeedsHuman)
            return AuthorityMatrixResult.Illegal("needs-human is an Orchestrator escalation outcome, not a legal Agent transition in this matrix.");

        var result = role switch
        {
            AgentRole.Planner or AgentRole.PlanChallenger => ValidateWorkerObservation(before, after, null, null),
            AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa => ValidateWorkerObservation(before, after, null, null),
            AgentRole.Merger => ValidateMerger(before, after),
            _ => AuthorityMatrixResult.Illegal($"Role '{role}' is not supported by the Authority Matrix.")
        };
        return result;
    }

    internal static AuthorityMatrixResult ValidateWorkerObservation(
        BeadWorkflowState before,
        BeadWorkflowState after,
        string? beforeAssignee,
        string? afterAssignee)
    {
        if (before != after)
            return AuthorityMatrixResult.Illegal(
                "Lifecycle agents may report evidence but cannot mutate Bead status, type, stage, pause labels, planned state, or repair_cycles.");
        if (!string.Equals(beforeAssignee, afterAssignee, StringComparison.Ordinal))
            return AuthorityMatrixResult.Illegal("Lifecycle agents cannot change Bead claim ownership.");
        return AuthorityMatrixResult.Legal;
    }

    private static AuthorityMatrixResult ValidateMerger(BeadWorkflowState before, BeadWorkflowState after)
    {
        if (before.Stage != "stage:merge")
            return AuthorityMatrixResult.Illegal("Merger evidence must originate from stage:merge.");
        if (before != after)
            return AuthorityMatrixResult.Illegal(
                "Merger agents may report evidence but cannot mutate Bead status, type, stage, pause labels, planned state, or repair_cycles.");
        return AuthorityMatrixResult.Legal;
    }

}
