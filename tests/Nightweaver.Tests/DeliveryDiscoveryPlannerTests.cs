using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class DeliveryDiscoveryPlannerTests
{
    private const string Actor = "nightweaver:demo";

    [Fact]
    public void RoutesEligibleTasksAndReportsAmbiguousOrUnknownStages()
    {
        var rows = new[]
        {
            Row("implement", "open", "task", 1, "2026-01-01T00:00:00Z", null, ["stage:implement", "ready-for-agent"]),
            Row("unlabeled", "open", "task", 1, "2026-01-02T00:00:00Z", null, ["ready-for-agent", "unrelated"]),
            Row("planned-task", "open", "task", 1, "2026-01-03T00:00:00Z", null, ["planned"]),
            Row("review", "open", "task", 1, "2026-01-04T00:00:00Z", null, ["stage:review"]),
            Row("qa", "open", "task", 1, "2026-01-05T00:00:00Z", null, ["stage:qa"]),
            Row("merge", "open", "task", 1, "2026-01-06T00:00:00Z", null, ["stage:merge"]),
            Row("duplicate-stage", "open", "task", 1, "2026-01-07T00:00:00Z", null, ["stage:implement", "stage:implement"]),
            Row("unknown", "open", "task", 1, "2026-01-08T00:00:00Z", null, ["stage:plan"]),
            Row("multiple", "open", "task", 1, "2026-01-09T00:00:00Z", null, ["stage:review", "stage:merge"]),
            Row("paused-decision", "open", "task", 1, "2026-01-10T00:00:00Z", null, ["product-decision-required", "stage:implement"]),
            Row("paused-human", "open", "task", 1, "2026-01-11T00:00:00Z", null, ["needs-human", "stage:review"]),
            Row("assigned", "open", "task", 1, "2026-01-12T00:00:00Z", "human", ["stage:qa"]),
            Row("stranded-foreign", "in_progress", "task", 1, "2026-01-13T00:00:00Z", "human", ["stage:merge"]),
            Row("not-ready", "open", "task", 1, "2026-01-14T00:00:00Z", null, ["stage:implement"]),
            Row("product", "open", "feature", 1, "2026-01-15T00:00:00Z", null, []),
        };
        var inventory = Inventory(rows);
        var readyIds = rows.Select(Id).Where(id => id != "not-ready" && id != "stranded-foreign").ToArray();
        var snapshot = Snapshot(inventory, readyIds);

        var plan = DeliveryDiscoveryPlanner.Plan(snapshot, 10);

        Assert.Equal(["implement", "unlabeled", "planned-task", "review", "qa", "duplicate-stage"],
            plan.DeliverySelected.Select(candidate => candidate.Bead.Id));
        Assert.Equal([AgentRole.Implementer, AgentRole.Implementer, AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Qa, AgentRole.Implementer],
            plan.DeliverySelected.Select(candidate => candidate.Role));
        Assert.Equal(["merge"], plan.MergerSelected.Select(candidate => candidate.Bead.Id));
        Assert.Empty(plan.DeliveryWaiting);
        Assert.Empty(plan.MergerWaiting);
        Assert.Equal(["multiple", "unknown"], plan.Excluded.Select(item => item.BeadId));
        Assert.Equal(TaskStageExclusionCode.MultipleStages, plan.Excluded[0].Code);
        Assert.Equal(["stage:merge", "stage:review"], plan.Excluded[0].StageLabels);
        Assert.Equal(TaskStageExclusionCode.UnknownStage, plan.Excluded[1].Code);
        Assert.DoesNotContain(plan.DeliverySelected, candidate => candidate.Bead.Id == "merge");
        Assert.DoesNotContain(plan.MergerSelected, candidate => candidate.Bead.Id == "multiple");
    }

    [Fact]
    public void SortsEachPoolByPriorityThenInstantThenOrdinalIdAndPartitionsCapacity()
    {
        var rows = new[]
        {
            Row("z-priority", "open", "task", 0, "2026-01-02T00:00:00Z", null, ["stage:implement"]),
            Row("task-10", "open", "task", 1, "2026-01-01T00:00:00Z", null, ["stage:review"]),
            Row("task-2", "open", "task", 1, "2026-01-01T05:00:00+05:00", null, ["stage:qa"]),
            Row("later", "open", "task", 1, "2026-01-02T00:00:00Z", null, []),
            Row("merge-late", "open", "task", 0, "2026-01-03T00:00:00Z", null, ["stage:merge"]),
            Row("merge-first", "open", "task", 1, "2026-01-01T00:00:00Z", null, ["stage:merge"]),
        };
        DeliveryDiscoveryPlan Run(IEnumerable<string> input)
        {
            var inventory = Inventory(input);
            var ids = input.Select(Id).ToArray();
            return DeliveryDiscoveryPlanner.Plan(Snapshot(inventory, ids), 3);
        }

        var first = Run(rows);
        var shuffled = Run(rows.Reverse());

        Assert.Equal(["z-priority", "task-10", "task-2"], first.DeliverySelected.Select(candidate => candidate.Bead.Id));
        Assert.Equal(["later"], first.DeliveryWaiting.Select(candidate => candidate.Bead.Id));
        Assert.Equal([1, 2, 3], first.DeliverySelected.Select(candidate => candidate.Rank));
        Assert.Equal([4], first.DeliveryWaiting.Select(candidate => candidate.Rank));
        Assert.Equal(["merge-late"], first.MergerSelected.Select(candidate => candidate.Bead.Id));
        Assert.Equal(["merge-first"], first.MergerWaiting.Select(candidate => candidate.Bead.Id));
        Assert.Equal([1], first.MergerSelected.Select(candidate => candidate.Rank));
        Assert.Equal([2], first.MergerWaiting.Select(candidate => candidate.Rank));
        Assert.Equal(first.DeliverySelected.Select(CandidateKey), shuffled.DeliverySelected.Select(CandidateKey));
        Assert.Equal(first.DeliveryWaiting.Select(CandidateKey), shuffled.DeliveryWaiting.Select(CandidateKey));
        Assert.Equal(first.MergerSelected.Select(CandidateKey), shuffled.MergerSelected.Select(CandidateKey));
        Assert.Equal(first.MergerWaiting.Select(CandidateKey), shuffled.MergerWaiting.Select(CandidateKey));
    }

    [Fact]
    public void IncludesRecoveredOwnedTasksAndIgnoresForeignOrDependencyBlockedClaims()
    {
        var inventory = Inventory(
            Row("recovered", "in_progress", "task", 1, "2026-01-01T00:00:00Z", Actor, ["stage:review"]),
            Row("foreign", "in_progress", "task", 0, "2026-01-01T00:00:00Z", "human", ["stage:merge"]),
            Row("dependency-blocked", "in_progress", "task", 0, "2026-01-01T00:00:00Z", Actor, ["stage:qa"], new { depends_on_id = "blocker", type = "blocks" }));
        var startup = StartupPlanner.Plan(Actor, inventory);
        var readiness = BeadsReadiness.Create([], ["recovered"]);
        var snapshot = PostSweepReadinessProjector.Project(startup, readiness);

        var plan = DeliveryDiscoveryPlanner.Plan(snapshot, 1);

        var candidate = Assert.Single(plan.DeliverySelected);
        Assert.Equal("recovered", candidate.Bead.Id);
        Assert.Equal(AgentRole.Reviewer, candidate.Role);
        Assert.True(candidate.RecoveredAfterClaimRelease);
        Assert.Empty(plan.MergerSelected);
    }

    private static PostSweepSnapshot Snapshot(BeadsInventory inventory, IEnumerable<string> readyIds)
    {
        var ready = readyIds.ToHashSet(StringComparer.Ordinal);
        return new PostSweepSnapshot(inventory, new PostSweepReadiness(ready, new HashSet<string>(StringComparer.Ordinal),
            ready, new HashSet<string>(StringComparer.Ordinal)));
    }

    private static string CandidateKey(TaskPoolCandidate candidate) =>
        $"{candidate.Bead.Id}|{candidate.Role}|{candidate.Rank}|{candidate.RecoveredAfterClaimRelease}";

    private static string Id(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetString()!;
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
