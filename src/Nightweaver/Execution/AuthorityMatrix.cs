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
        if (after.NeedsHuman)
            return AuthorityMatrixResult.Illegal("needs-human is an Orchestrator escalation outcome, not a legal Agent transition in this matrix.");

        var result = role switch
        {
            AgentRole.Planner => ValidatePlanner(before, after, postBeads, dependencies),
            AgentRole.PlanChallenger => ValidatePlanChallenger(before, after),
            AgentRole.Implementer => ValidateImplementer(before, after),
            AgentRole.Reviewer => ValidateReviewOrQa(before, after, "stage:review", "stage:qa", role),
            AgentRole.Qa => ValidateReviewOrQa(before, after, "stage:qa", "stage:merge", role),
            AgentRole.Merger => ValidateMerger(before, after),
            _ => AuthorityMatrixResult.Illegal($"Role '{role}' is not supported by the Authority Matrix.")
        };
        return result;
    }

    private static AuthorityMatrixResult ValidatePlanner(BeadWorkflowState before, BeadWorkflowState after,
        IReadOnlyDictionary<string, BeadSnapshot> postBeads, IReadOnlyList<BeadDependency>? dependencies)
    {
        if (before.Stage == "stage:plan-approved")
        {
            if (!after.Planned || after.Stage is not null || after.ProductDecisionRequired)
                return AuthorityMatrixResult.Illegal("Planner from stage:plan-approved must transition to planned with no stage or pause label.");
            if (after.Status != "open")
                return AuthorityMatrixResult.Illegal("A planned Product Bead must have open status.");
            if (after.RepairCycles != before.RepairCycles)
                return CounterUnchanged(before, after);
            if (dependencies is null)
                return AuthorityMatrixResult.Illegal("The discovered-from relationships required to verify planned materialization were unavailable.");

            var materialized = dependencies.Any(dependency => dependency.Type == "discovered-from"
                && string.Equals(dependency.DependsOnId, before.Id, StringComparison.Ordinal)
                && !string.Equals(dependency.IssueId, before.Id, StringComparison.Ordinal)
                && postBeads.TryGetValue(dependency.IssueId, out var child)
                && child.IssueType == "task" && child.Status == "open");
            return materialized ? AuthorityMatrixResult.Legal
                : AuthorityMatrixResult.Illegal("planned requires at least one open task linked to this Product Bead by discovered-from.");
        }

        if (after.Stage == "stage:plan-challenge" && !after.Planned && !after.ProductDecisionRequired)
            return UnchangedCounter(before, after);
        if (after.ProductDecisionRequired && !after.Planned && after.Stage == before.Stage)
            return UnchangedCounter(before, after);
        return AuthorityMatrixResult.Illegal("Planner must transition to stage:plan-challenge or product-decision-required.");
    }

    private static AuthorityMatrixResult ValidatePlanChallenger(BeadWorkflowState before, BeadWorkflowState after)
    {
        if (after.Stage == "stage:plan-approved" && !after.Planned && !after.ProductDecisionRequired)
            return UnchangedCounter(before, after);
        if (after.Stage == "stage:plan" && !after.Planned && !after.ProductDecisionRequired)
            return IncrementCounter(before, after);
        return AuthorityMatrixResult.Illegal("Plan Challenger must transition to stage:plan-approved or reject to stage:plan.");
    }

    private static AuthorityMatrixResult ValidateImplementer(BeadWorkflowState before, BeadWorkflowState after)
    {
        if (after.Stage == "stage:review" && !after.Planned && !after.ProductDecisionRequired)
            return UnchangedCounter(before, after);
        if (after.ProductDecisionRequired && !after.Planned && after.Stage == before.Stage)
            return UnchangedCounter(before, after);
        return AuthorityMatrixResult.Illegal("Implementer must transition to stage:review or product-decision-required.");
    }

    private static AuthorityMatrixResult ValidateReviewOrQa(BeadWorkflowState before, BeadWorkflowState after,
        string inputStage, string acceptedStage, AgentRole role)
    {
        if (before.Stage != inputStage)
            return AuthorityMatrixResult.Illegal($"Role '{role}' has an unexpected pre-dispatch stage '{before.Stage}'.");
        if (after.Stage == acceptedStage && !after.Planned && !after.ProductDecisionRequired)
            return UnchangedCounter(before, after);
        if (after.ProductDecisionRequired && !after.Planned && after.Stage == before.Stage)
            return UnchangedCounter(before, after);
        if (after.Stage == "stage:implement" && !after.Planned && !after.ProductDecisionRequired)
            return IncrementCounter(before, after);
        return AuthorityMatrixResult.Illegal($"Role '{role}' has no legal transition to the observed workflow state.");
    }

    private static AuthorityMatrixResult ValidateMerger(BeadWorkflowState before, BeadWorkflowState after)
    {
        if (after.Status == "closed" && (after.Stage is null or "stage:merge")
            && !after.Planned && !after.ProductDecisionRequired)
            return after.RepairCycles == before.RepairCycles ? AuthorityMatrixResult.Legal : CounterUnchanged(before, after);
        if (after.Stage == "stage:implement" && after.Status is "open" or "in_progress"
            && !after.Planned && !after.ProductDecisionRequired)
            return IncrementCounter(before, after);
        return AuthorityMatrixResult.Illegal("Merger must close the stage:merge Plan Bead or reject it to stage:implement.");
    }

    private static AuthorityMatrixResult UnchangedCounter(BeadWorkflowState before, BeadWorkflowState after) =>
        after.RepairCycles == before.RepairCycles ? AllowedStatus(after) : CounterUnchanged(before, after);

    private static AuthorityMatrixResult IncrementCounter(BeadWorkflowState before, BeadWorkflowState after)
    {
        int expected;
        try { expected = checked(before.RepairCycles + 1); }
        catch (OverflowException) { return AuthorityMatrixResult.Illegal("repair_cycles cannot be incremented without overflowing a 32-bit integer."); }
        if (after.RepairCycles != expected)
            return AuthorityMatrixResult.Illegal($"Rejected transition must increment repair_cycles exactly once from {before.RepairCycles} to {expected}; observed {after.RepairCycles}.");
        return AllowedStatus(after);
    }

    private static AuthorityMatrixResult CounterUnchanged(BeadWorkflowState before, BeadWorkflowState after) =>
        AuthorityMatrixResult.Illegal($"repair_cycles must remain {before.RepairCycles} for this transition; observed {after.RepairCycles}.");

    private static AuthorityMatrixResult AllowedStatus(BeadWorkflowState state) =>
        state.Status is "open" or "in_progress" ? AuthorityMatrixResult.Legal
            : AuthorityMatrixResult.Illegal($"A nonterminal workflow transition must remain open or in_progress; observed '{state.Status}'.");
}
