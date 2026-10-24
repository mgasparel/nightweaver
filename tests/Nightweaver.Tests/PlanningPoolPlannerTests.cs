using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class PlanningPoolPlannerTests
{
    private const string Actor = "nightweaver:demo";
    private static readonly string[] ProjectedReadyIds = ["native", "recovered"];
    private static readonly string[] RecoveredIds = ["recovered"];
    private static readonly string[] ProjectedSelection = ["recovered", "native"];
    private static readonly string[] SelectedByRank = ["z-priority", "a", "b"];
    private static readonly PlanningRole[] RolesByRank = [PlanningRole.Planner, PlanningRole.PlanChallenger, PlanningRole.Planner];
    private static readonly string[] WaitingByCapacity = ["approved", "no-stage"];
    private static readonly string[] ExcludedById = ["multiple", "unknown"];
    private static readonly string[] ExcludedStages = ["stage:plan", "stage:weird"];
    private static readonly string[] WaitingByBackpressure = ["ready-a", "ready-b"];

    [Fact]
    public void PostSweepProjectionUnionsNativeAndOnlyReadyRecoverableClaims()
    {
        var inventory = Inventory(
            Row("native", "open", "feature", 2, "2026-01-01T00:00:00Z", null, ["stage:plan"]),
            Row("recovered", "in_progress", "bug", 1, "2026-01-02T00:00:00Z", Actor, ["stage:plan-challenge"]),
            Row("blocked-recovery", "in_progress", "feature", 0, "2026-01-03T00:00:00Z", Actor, ["stage:plan"]));
        var startup = StartupPlanner.Plan(Actor, inventory);
        var readiness = BeadsReadiness.Create(["native"], ["recovered"]);

        var plan = PlanningPoolPlanner.Plan(startup, readiness, 4, 5);

        Assert.Equal(ProjectedReadyIds, plan.Readiness.ReadyBeadIds.Order(StringComparer.Ordinal));
        Assert.Equal(RecoveredIds, plan.Readiness.RecoveredReadyBeadIds);
        Assert.DoesNotContain("blocked-recovery", plan.Readiness.ReadyBeadIds);
        Assert.Equal(ProjectedSelection, plan.Selected.Select(candidate => candidate.Bead.Id));
        Assert.All(plan.Selected.Where(candidate => candidate.Bead.Id == "recovered"), candidate => Assert.True(candidate.RecoveredAfterClaimRelease));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(2, true)]
    public void ActivePlanCountIncludesActiveTasksAndAppliesInclusiveBackpressure(int threshold, bool expectedBackpressure)
    {
        var inventory = Inventory(
            Row("open-unlabeled", "open", "task", 2, "2026-01-01T00:00:00Z", null, Array.Empty<string>()),
            Row("assigned-merge", "in_progress", "task", 2, "2026-01-01T00:00:00Z", "human", ["stage:merge"]),
            Row("dependency-blocked", "open", "task", 2, "2026-01-01T00:00:00Z", null, Array.Empty<string>(), new { depends_on_id = "blocker", type = "blocks" }),
            Row("closed", "closed", "task", 2, "2026-01-01T00:00:00Z", null, Array.Empty<string>()),
            Row("deferred", "deferred", "task", 2, "2026-01-01T00:00:00Z", null, Array.Empty<string>()),
            Row("status-blocked", "blocked", "task", 2, "2026-01-01T00:00:00Z", null, Array.Empty<string>()),
            Row("paused-product", "open", "task", 2, "2026-01-01T00:00:00Z", null, ["product-decision-required"]),
            Row("paused-human", "in_progress", "task", 2, "2026-01-01T00:00:00Z", null, ["needs-human"]),
            Row("not-a-plan", "open", "feature", 2, "2026-01-01T00:00:00Z", null, Array.Empty<string>()));

        var plan = PlanningPoolPlanner.Plan(StartupPlanner.Plan(Actor, inventory), BeadsReadiness.Create(Array.Empty<string>(), Array.Empty<string>()), 2, threshold);

        Assert.Equal(3, plan.ActivePlanBeadCount);
        Assert.Equal(expectedBackpressure, plan.BackpressureActive);
    }

    [Fact]
    public void PlanningRoutesSupportedStagesAndSortsByPriorityInstantThenOrdinalId()
    {
        var rows = new[]
        {
            Row("z-priority", "open", "feature", 0, "2026-01-02T00:00:00Z", null, ["stage:plan"]),
            Row("b", "open", "bug", 1, "2026-01-01T00:00:00Z", null, ["stage:plan"]),
            Row("a", "open", "feature", 1, "2026-01-01T05:00:00+05:00", null, ["stage:plan-challenge"]),
            Row("approved", "open", "feature", 1, "2026-01-01T01:00:00Z", null, ["stage:plan-approved"]),
            Row("no-stage", "open", "bug", 2, "2026-01-01T00:00:00Z", null, ["unrelated"]),
            Row("unknown", "open", "feature", 0, "2026-01-01T00:00:00Z", null, ["stage:delivery"]),
            Row("multiple", "open", "feature", 0, "2026-01-01T00:00:00Z", null, ["stage:weird", "stage:plan"]),
            Row("planned", "open", "feature", 0, "2026-01-01T00:00:00Z", null, ["planned", "stage:unknown"]),
            Row("paused", "open", "feature", 0, "2026-01-01T00:00:00Z", null, ["needs-human", "stage:unknown"]),
            Row("not-ready", "open", "feature", 0, "2026-01-01T00:00:00Z", null, ["stage:unknown"]),
            Row("task", "open", "task", 0, "2026-01-01T00:00:00Z", null, ["stage:plan"])
        };
        var inventory = Inventory(rows);
        var ready = new[] { "z-priority", "b", "a", "approved", "no-stage", "unknown", "multiple", "planned", "paused", "task" };

        var plan = PlanningPoolPlanner.Plan(StartupPlanner.Plan(Actor, inventory), BeadsReadiness.Create(ready, []), 3, 5);

        Assert.Equal(SelectedByRank, plan.Selected.Select(candidate => candidate.Bead.Id));
        Assert.Equal(RolesByRank, plan.Selected.Select(candidate => candidate.Role));
        Assert.Equal(WaitingByCapacity, plan.Waiting.Select(waiting => waiting.Candidate.Bead.Id));
        Assert.All(plan.Waiting, waiting => Assert.Equal(PlanningWaitReason.Capacity, waiting.Reason));
        Assert.Equal(ExcludedById, plan.Excluded.Select(exclusion => exclusion.BeadId));
        Assert.Equal(PlanningExclusionCode.MultipleStages, plan.Excluded[0].Code);
        Assert.Equal(ExcludedStages, plan.Excluded[0].StageLabels);
        Assert.Equal(PlanningExclusionCode.UnknownStage, plan.Excluded[1].Code);
        Assert.DoesNotContain(plan.Excluded, exclusion => exclusion.BeadId is "planned" or "paused" or "not-ready");
    }

    [Fact]
    public void BackpressureSelectsNothingButRetainsEligibleAndInvalidStageDiagnostics()
    {
        var inventory = Inventory(
            Row("task", "open", "task", 2, "2026-01-01T00:00:00Z", null, []),
            Row("ready-a", "open", "feature", 0, "2026-01-01T00:00:00Z", null, ["stage:plan"]),
            Row("ready-b", "open", "bug", 1, "2026-01-01T00:00:00Z", null, ["stage:plan-approved"]),
            Row("bad-stage", "open", "feature", 1, "2026-01-01T00:00:00Z", null, ["stage:merge"]));

        var plan = PlanningPoolPlanner.Plan(StartupPlanner.Plan(Actor, inventory), BeadsReadiness.Create(["ready-a", "ready-b", "bad-stage"], []), 2, 1);

        Assert.True(plan.BackpressureActive);
        Assert.Empty(plan.Selected);
        Assert.Equal(WaitingByBackpressure, plan.Waiting.Select(waiting => waiting.Candidate.Bead.Id));
        Assert.All(plan.Waiting, waiting => Assert.Equal(PlanningWaitReason.Backpressure, waiting.Reason));
        Assert.Equal(PlanningExclusionCode.UnknownStage, Assert.Single(plan.Excluded).Code);
    }

    [Fact]
    public void StageParsingAndPlanningAreIndependentOfInputAndLabelOrder()
    {
        var rows = new[]
        {
            Row("candidate", "open", "feature", 1, "2026-01-01T00:00:00Z", null, ["unrelated", "stage:plan"]),
            Row("invalid", "open", "bug", 1, "2026-01-01T00:00:00Z", null, ["stage:plan-challenge", "stage:plan"])
        };
        PlanningPoolPlan Run(IEnumerable<string> input) => PlanningPoolPlanner.Plan(
            StartupPlanner.Plan(Actor, Inventory(input)), BeadsReadiness.Create(["candidate", "invalid"], []), 1, 5);

        var first = Run(rows);
        var second = Run(rows.Reverse().Select(json => json.Replace("[\"stage:plan-challenge\",\"stage:plan\"]", "[\"stage:plan\",\"stage:plan-challenge\"]", StringComparison.Ordinal)));

        Assert.Equal(first.Selected.Select(candidate => (candidate.Bead.Id, candidate.Role, candidate.Rank, candidate.RecoveredAfterClaimRelease)),
            second.Selected.Select(candidate => (candidate.Bead.Id, candidate.Role, candidate.Rank, candidate.RecoveredAfterClaimRelease)));
        Assert.Equal(first.Waiting.Select(waiting => (waiting.Candidate.Bead.Id, waiting.Candidate.Role, waiting.Candidate.Rank, waiting.Reason)),
            second.Waiting.Select(waiting => (waiting.Candidate.Bead.Id, waiting.Candidate.Role, waiting.Candidate.Rank, waiting.Reason)));
        Assert.Equal(first.Excluded.Select(exclusion => (exclusion.BeadId, exclusion.Code, string.Join('|', exclusion.StageLabels))),
            second.Excluded.Select(exclusion => (exclusion.BeadId, exclusion.Code, string.Join('|', exclusion.StageLabels))));
        Assert.Equal(["stage:plan", "stage:plan-challenge"], StageLabelParser.Parse(["stage:plan-challenge", "stage:plan", "stage:plan"]));
    }

    private static BeadsInventory Inventory(params string[] rows) => Inventory((IEnumerable<string>)rows);
    private static BeadsInventory Inventory(IEnumerable<string> rows) => BeadsReader.Parse("[" + string.Join(',', rows) + "]");

    private static string Row(string id, string status, string type, int priority, string createdAt, string? assignee,
        string[] labels, object? dependency = null) => JsonSerializer.Serialize(new
    {
        id, status, issue_type = type, priority, created_at = createdAt, assignee, labels,
        dependencies = dependency is null ? Array.Empty<object>() : new[] { dependency }
    });
}
