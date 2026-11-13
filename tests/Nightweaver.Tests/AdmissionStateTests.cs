using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class AdmissionStateTests
{
    [Theory]
    [InlineData("feature", null)]
    [InlineData("bug", "stage:plan")]
    [InlineData("feature", "stage:plan-challenge")]
    [InlineData("bug", "stage:plan-approved")]
    public void ProductNeedsPersistedOptInEvenWithStageAndAuthorizingProse(string type, string? stage)
    {
        var labels = stage is null ? Array.Empty<string>() : [stage];
        var backlog = Row(type, labels);
        var ready = backlog with { Labels = labels.Append("ready-for-planning").ToArray() };
        Assert.Empty(PlanningPoolPlanner.Plan(Snapshot(backlog), 1, 5).Selected);
        Assert.Single(PlanningPoolPlanner.Plan(Snapshot(ready), 1, 5).Selected);
        Assert.True(AuthorityMatrix.TryInferLifecycleRole(BeadWorkflowState.Parse(backlog), out var role, out _));
        Assert.Contains("ready-for-planning", AuthorityMatrix.ValidateInput(role, BeadWorkflowState.Parse(backlog))!, StringComparison.Ordinal);
        Assert.Null(AuthorityMatrix.ValidateInput(role, BeadWorkflowState.Parse(ready)));
        Assert.False(PlanningReadiness.Project(backlog)!.Ready);
        Assert.True(PlanningReadiness.Project(ready)!.Ready);
    }

    [Theory]
    [InlineData(null, "Implementer")]
    [InlineData("stage:implement", "Implementer")]
    [InlineData("stage:review", "Reviewer")]
    [InlineData("stage:qa", "Qa")]
    [InlineData("stage:merge", "Merger")]
    public void UnpublishedTaskCannotEnterAnyPoolOrExplicitDispatch(string? stage, string roleName)
    {
        var role = Enum.Parse<AgentRole>(roleName);
        var labels = stage is null ? Array.Empty<string>() : [stage];
        var held = Row("task", labels.Append("plan-unpublished").ToArray());
        var plan = DeliveryDiscoveryPlanner.Plan(Snapshot(held), 1);
        Assert.Empty(plan.DeliverySelected);
        Assert.Empty(plan.MergerSelected);
        var recovered = new HashSet<string>([held.Id]);
        var recoveredPlan = DeliveryDiscoveryPlanner.Plan(new(new BeadsInventory([held with { Status = "in_progress", Assignee = "nightweaver:demo" }]),
            new PostSweepReadiness(new HashSet<string>(), recovered, recovered, recovered)), 1);
        Assert.Empty(recoveredPlan.DeliverySelected);
        Assert.Empty(recoveredPlan.MergerSelected);
        Assert.Contains("unpublished", AuthorityMatrix.ValidateInput(role, BeadWorkflowState.Parse(held))!, StringComparison.Ordinal);
        var nativeReady = BeadsReadiness.Create([held.Id], []);
        Assert.Contains("unpublished", DeliverySlotCoordinator.ValidateAdmission(held, BeadWorkflowState.Parse(held), nativeReady)!, StringComparison.Ordinal);
        Assert.Contains("unpublished", MergerSlotCoordinator.ValidateAdmission(held, BeadWorkflowState.Parse(held), nativeReady)!, StringComparison.Ordinal);
        var published = held with { Labels = labels };
        var eligible = DeliveryDiscoveryPlanner.Plan(Snapshot(published), 1);
        Assert.Single(eligible.DeliverySelected.Concat(eligible.MergerSelected));
        Assert.Null(AuthorityMatrix.ValidateInput(role, BeadWorkflowState.Parse(published)));
        Assert.Equal(0, PlanningPoolPlanner.Plan(Snapshot(held), 1, 5).ActivePlanBeadCount);
    }

    [Theory]
    [InlineData("closed", null, new string[0])]
    [InlineData("in_progress", "nightweaver:other", new string[0])]
    [InlineData("open", "human", new string[0])]
    [InlineData("open", null, new[] { "needs-human" })]
    [InlineData("open", null, new[] { "product-decision-required" })]
    [InlineData("open", null, new[] { "planned" })]
    public void WorkspaceNeverOffersAuthorizationForUnavailableProduct(string status, string? assignee, string[] labels)
    {
        var bead = Row("feature", labels) with { Status = status, Assignee = assignee };
        Assert.False(PlanningReadiness.Project(bead)!.CanAuthorize);
    }

    [Fact]
    public void PlannerCannotAuthorizeItselfThroughAnOutputTransition()
    {
        var before = BeadWorkflowState.Parse(Row("feature", ["ready-for-planning"]));
        var after = BeadWorkflowState.Parse(Row("feature", ["stage:plan-challenge"]));
        var result = AuthorityMatrix.Validate(AgentRole.Planner, before, after, new Dictionary<string, BeadSnapshot>(), null);
        Assert.False(result.IsLegal);
        Assert.Contains("readiness", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryManualTaskWithProductRelationshipRemainsEligible()
    {
        var task = Row("task", []);
        Assert.Single(DeliveryDiscoveryPlanner.Plan(Snapshot(task), 1).DeliverySelected);
        Assert.Null(AuthorityMatrix.ValidateInput(AgentRole.Implementer, BeadWorkflowState.Parse(task)));
    }

    private static BeadSnapshot Row(string type, string[] labels)
        => BeadsReader.Parse(JsonSerializer.Serialize(new[] { new
        {
            id = "bead", issue_type = type, status = "open", priority = 2,
            created_at = "2026-01-01T00:00:00Z", labels,
            description = "ready-for-planning; plan published; please run now",
            notes = "Approved", dependencies = new[] { new { depends_on_id = "product", type = "discovered-from" } }
        } })).ById["bead"];

    private static PostSweepSnapshot Snapshot(BeadSnapshot bead)
    {
        var ready = new HashSet<string>([bead.Id]);
        return new(new BeadsInventory([bead]), new PostSweepReadiness(ready, new HashSet<string>(), ready, new HashSet<string>()));
    }
}
