using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Reporting;

namespace Nightweaver.Tests;

public sealed class PlanningReportTests
{
    [Fact]
    public void ReportExplainsBackpressureEvenWhenPlanningHasNoCandidates()
    {
        var inventory = BeadsReader.Parse("[" + string.Join(',',
            Row("active-task", "open", "task", []) ,
            Row("candidate", "open", "feature", ["stage:plan"])) + "]");
        var startup = StartupPlanner.Plan("nightweaver:demo", inventory);
        var candidates = BeadsReadiness.Create(["candidate"], []);
        var noCandidates = PlanningPoolPlanner.Plan(startup, BeadsReadiness.Create([], []), 1, 1);
        var waiting = PlanningPoolPlanner.Plan(startup, candidates, 1, 1);

        var emptyReport = DryRunReportWriter.Render(CreateReport(startup, noCandidates));
        var waitingReport = DryRunReportWriter.Render(CreateReport(startup, waiting));

        Assert.Contains("Active Plan Beads\n  count: 1\n  threshold: 1\n  backpressure: active\n  backpressure_reason: active_plan_beads=1 >= max_active_plan_beads=1", emptyReport, StringComparison.Ordinal);
        Assert.Contains("Planning selected\n  (none)\n\nPlanning waiting\n  (none)", emptyReport, StringComparison.Ordinal);
        Assert.Contains("reason=backpressure (active_plan_beads=1 >= max_active_plan_beads=1)", waitingReport, StringComparison.Ordinal);
    }

    private static DryRunReport CreateReport(StartupPlan startup, PlanningPoolPlan planning)
    {
        var settings = new AgentSettings(AgentCli.Claude, null, "high", 30);
        var roles = new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => settings));
        var project = new ProjectHeader("demo", "config", "root", "target", "main", "nightweaver:demo", "worktrees", "tmp", "logs");
        var delivery = DeliveryDiscoveryPlanner.Plan(new PostSweepSnapshot(startup.Inventory, planning.Readiness), 1);
        return new DryRunReport(project, new ConfigurationSection(new ConcurrencyConfig(1, 1, 1), new LimitsConfig(3, 2)),
            new AgentRolesSection(settings, roles), startup, planning, delivery);
    }

    private static string Row(string id, string status, string type, string[] labels) => JsonSerializer.Serialize(new
    {
        id, status, issue_type = type, priority = 2, created_at = "2026-10-23T00:00:00Z", assignee = (string?)null, labels
    });
}
