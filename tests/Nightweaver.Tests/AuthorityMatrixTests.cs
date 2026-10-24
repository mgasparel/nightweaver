using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class AuthorityMatrixTests
{
    [Fact]
    public void AcceptsEveryAuthorityMatrixTransition()
    {
        var cases = new (AgentRole Role, BeadWorkflowState Before, BeadWorkflowState After,
            IReadOnlyDictionary<string, BeadSnapshot> Beads, IReadOnlyList<BeadDependency>? Dependencies)[]
        {
            (AgentRole.Planner, State("p0", "feature"), State("p0", "feature", labels: ["stage:plan-challenge"]), Beads(), null),
            (AgentRole.Planner, State("p1", "bug", "stage:plan"), State("p1", "bug", "stage:plan-challenge"), Beads(), null),
            (AgentRole.Planner, State("p2", "feature", "stage:plan"), State("p2", "feature", "stage:plan", labels: ["product-decision-required"]), Beads(), null),
            (AgentRole.Planner, State("p3", "bug", "stage:plan-approved"), State("p3", "bug", labels: ["planned"]),
                Beads(State("task-child", "task")), [new("task-child", "p3", "discovered-from")]),
            (AgentRole.PlanChallenger, State("p4", "feature", "stage:plan-challenge"), State("p4", "feature", "stage:plan-approved"), Beads(), null),
            (AgentRole.PlanChallenger, State("p5", "bug", "stage:plan-challenge", repairCycles: 2), State("p5", "bug", "stage:plan", repairCycles: 3), Beads(), null),
            (AgentRole.Implementer, State("t0", "task"), State("t0", "task", "stage:review"), Beads(), null),
            (AgentRole.Implementer, State("t1", "task", "stage:implement"), State("t1", "task", "stage:implement", labels: ["product-decision-required"]), Beads(), null),
            (AgentRole.Reviewer, State("t2", "task", "stage:review"), State("t2", "task", "stage:qa"), Beads(), null),
            (AgentRole.Reviewer, State("t3", "task", "stage:review"), State("t3", "task", "stage:review", labels: ["product-decision-required"]), Beads(), null),
            (AgentRole.Reviewer, State("t4", "task", "stage:review", repairCycles: 1), State("t4", "task", "stage:implement", repairCycles: 2), Beads(), null),
            (AgentRole.Qa, State("t5", "task", "stage:qa"), State("t5", "task", "stage:merge"), Beads(), null),
            (AgentRole.Qa, State("t6", "task", "stage:qa"), State("t6", "task", "stage:qa", labels: ["product-decision-required"]), Beads(), null),
            (AgentRole.Qa, State("t7", "task", "stage:qa"), State("t7", "task", "stage:implement", repairCycles: 1), Beads(), null),
            (AgentRole.Merger, State("t8", "task", "stage:merge"), State("t8", "task", status: "closed"), Beads(), null),
            (AgentRole.Merger, State("t9", "task", "stage:merge"), State("t9", "task", "stage:merge", status: "closed"), Beads(), null),
            (AgentRole.Merger, State("t10", "task", "stage:merge"), State("t10", "task", "stage:implement", repairCycles: 1), Beads(), null)
        };

        foreach (var item in cases)
        {
            var result = AuthorityMatrix.Validate(item.Role, item.Before, item.After, item.Beads, item.Dependencies);
            Assert.True(result.IsLegal, $"{item.Role}: {result.Reason}");
        }
    }

    [Fact]
    public void AcceptsBothProductTypesAndUnlabeledAliasesWithUnrelatedLabels()
    {
        foreach (var type in new[] { "feature", "bug" })
        {
            Assert.Null(AuthorityMatrix.ValidateInput(AgentRole.Planner, State("p-" + type, type, labels: ["ready-for-agent"])));
            Assert.Null(AuthorityMatrix.ValidateInput(AgentRole.Planner, State("p-" + type, type, "stage:plan", labels: ["ready-for-agent"])));
        }
        Assert.Null(AuthorityMatrix.ValidateInput(AgentRole.Implementer, State("task", "task", labels: ["customer"])));
        Assert.Null(AuthorityMatrix.ValidateInput(AgentRole.Implementer, State("task", "task", "stage:implement", labels: ["customer"])));
    }

    [Fact]
    public void RejectsWrongRoleTypeStageClosedAndPausedInputs()
    {
        Assert.Contains("feature or bug", AuthorityMatrix.ValidateInput(AgentRole.Planner, State("task", "task"))!, StringComparison.Ordinal);
        Assert.Contains("type task", AuthorityMatrix.ValidateInput(AgentRole.Reviewer, State("feature", "feature", "stage:review"))!, StringComparison.Ordinal);
        Assert.Contains("cannot run", AuthorityMatrix.ValidateInput(AgentRole.PlanChallenger, State("product", "feature", "stage:plan"))!, StringComparison.Ordinal);
        Assert.Contains("open or in_progress", AuthorityMatrix.ValidateInput(AgentRole.Planner, State("closed", "feature", status: "closed"))!, StringComparison.Ordinal);
        Assert.Contains("planned or paused", AuthorityMatrix.ValidateInput(AgentRole.Planner, State("paused", "feature", labels: ["needs-human"]))!, StringComparison.Ordinal);
        Assert.Contains("Classifier", AuthorityMatrix.ValidateInput(AgentRole.Classifier, State("p", "feature"))!, StringComparison.Ordinal);
        Assert.NotNull(AuthorityMatrix.ValidateInput((AgentRole)99, State("p", "feature")));
    }

    [Fact]
    public void RequiresValidDiscoveredFromOpenTaskMaterialization()
    {
        var before = State("product", "feature", "stage:plan-approved");
        var after = State("product", "feature", labels: ["planned"]);
        var task = State("task", "task");
        Assert.False(AuthorityMatrix.Validate(AgentRole.Planner, before, after, Beads(task), Array.Empty<BeadDependency>()).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Planner, before, after,
            Beads(State("task", "task", status: "closed")), [new("task", "product", "discovered-from")]).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Planner, before, after,
            Beads(State("task", "feature")), [new("task", "product", "discovered-from")]).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Planner, before, after, Beads(task), [new("product", "task", "discovered-from")]).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Planner, before, after, Beads(task), [new("task", "other-product", "discovered-from")]).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Planner, before, after, Beads(task), [new("task", "product", "blocks")]).IsLegal);
        Assert.True(AuthorityMatrix.Validate(AgentRole.Planner, before, after, Beads(task), [new("task", "product", "discovered-from")]).IsLegal);
    }

    [Fact]
    public void RejectsNoOpIdentityTypePolicyAndUnexpectedClosureChanges()
    {
        var before = State("task", "task", "stage:review");
        Assert.False(AuthorityMatrix.Validate(AgentRole.Reviewer, before, before, Beads(), null).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Reviewer, before, State("different", "task", "stage:qa"), Beads(), null).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Reviewer, before, State("task", "feature", "stage:qa"), Beads(), null).IsLegal);
        Assert.False(AuthorityMatrix.Validate(AgentRole.Reviewer, before, State("task", "task", "stage:qa", status: "closed"), Beads(), null).IsLegal);
        Assert.Throws<FormatException>(() => State("bad", "task", "stage:review", labels: ["stage:qa"]));
        Assert.Throws<FormatException>(() => State("bad", "task", labels: ["planned", "product-decision-required"]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(2, 4)]
    public void RejectionRequiresExactlyOneRepairCycle(int beforeCycles, int afterCycles)
    {
        var result = AuthorityMatrix.Validate(AgentRole.Reviewer,
            State("task", "task", "stage:review", repairCycles: beforeCycles),
            State("task", "task", "stage:implement", repairCycles: afterCycles), Beads(), null);
        Assert.False(result.IsLegal);
        Assert.Contains("exactly once", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RepairCycleParsingFailsClosedForMalformedNegativeFractionalAndOverflowValues()
    {
        foreach (var value in new object[] { -1, 1.5, "bad", 2147483648L })
            Assert.Throws<FormatException>(() => State("bad", "task", "stage:review", metadata: new { repair_cycles = value }));
        Assert.Equal(0, State("missing", "task", "stage:review").RepairCycles);
        Assert.Equal(3, State("numeric", "task", "stage:review", metadata: new { repair_cycles = 3 }).RepairCycles);
        Assert.Equal(3, State("text", "task", "stage:review", metadata: new { repair_cycles = "3" }).RepairCycles);

        var max = State("max", "task", "stage:review", repairCycles: int.MaxValue);
        var overflow = State("max", "task", "stage:implement", repairCycles: int.MaxValue);
        Assert.Contains("overflowing", AuthorityMatrix.Validate(AgentRole.Reviewer, max, overflow, Beads(), null).Reason!, StringComparison.Ordinal);
    }

    private static BeadWorkflowState State(string id, string type, string? stage = null, string status = "open",
        string[]? labels = null, int repairCycles = 0, object? metadata = null)
    {
        var allLabels = (labels ?? []).Concat(stage is null ? [] : [stage]).ToArray();
        var row = new
        {
            id,
            status,
            issue_type = type,
            priority = 2,
            created_at = "2026-10-24T00:00:00Z",
            labels = allLabels,
            metadata = metadata ?? (repairCycles == 0 ? null : new { repair_cycles = repairCycles })
        };
        return BeadWorkflowState.Parse(BeadsReader.Parse(JsonSerializer.Serialize(new[] { row })).ById[id]);
    }

    private static IReadOnlyDictionary<string, BeadSnapshot> Beads(params BeadWorkflowState[] states) =>
        states.Length == 0 ? new Dictionary<string, BeadSnapshot>(StringComparer.Ordinal)
            : BeadsReader.Parse("[" + string.Join(',', states.Select(state => JsonSerializer.Serialize(new
            {
                id = state.Id, status = state.Status, issue_type = state.IssueType, priority = 2,
                created_at = "2026-10-24T00:00:00Z", labels = (string[])(state.Stage is null ? [] : [state.Stage]),
                metadata = state.RepairCycles == 0 ? null : new { repair_cycles = state.RepairCycles }
            }))) + "]").ById;
}
