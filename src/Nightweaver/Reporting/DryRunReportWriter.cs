using System.Text;
using System.Text.Json;
using Nightweaver.Planning;
using System.Globalization;
using Nightweaver.Configuration;

namespace Nightweaver.Reporting;

internal static class DryRunReportWriter
{
    internal static string Render(DryRunReport report)
    {
        var project = report.Project;
        var builder = new StringBuilder();
        builder.Append("Nightweaver dry run: hypothetical startup snapshot; no actions are performed.\n\n");
        builder.Append("Project\n");
        AppendField(builder, "name", JsonSerializer.Serialize(project.Name));
        AppendField(builder, "config", JsonSerializer.Serialize(project.ConfigFilePath));
        AppendField(builder, "nightweaver_root", JsonSerializer.Serialize(project.NightweaverRoot));
        AppendField(builder, "target_repository", JsonSerializer.Serialize(project.TargetRepositoryPath));
        AppendField(builder, "default_branch", JsonSerializer.Serialize(project.DefaultBranch));
        AppendField(builder, "recovery_actor", JsonSerializer.Serialize(project.RecoveryActor));
        AppendField(builder, "worktrees_dir", JsonSerializer.Serialize(project.WorktreesDirectory));
        AppendField(builder, "tmp_dir", JsonSerializer.Serialize(project.TmpDirectory));
        AppendField(builder, "logs_dir", JsonSerializer.Serialize(project.LogsDirectory));

        builder.Append("\nStartup claim releases (hypothetical)\n");
        if (report.StartupPlan.Releases.Count == 0) builder.Append("  (none)\n");
        foreach (var release in report.StartupPlan.Releases)
        {
            builder.Append("  ").Append(JsonSerializer.Serialize(release.BeadId)).Append(": in_progress -> open; assignee ")
                .Append(JsonSerializer.Serialize(release.ExpectedAssignee)).Append(" -> unassigned\n");
        }
        builder.Append("\nWorktree removals (hypothetical)\n");
        if (report.StartupPlan.Removals.Count == 0) builder.Append("  (none)\n");
        foreach (var removal in report.StartupPlan.Removals)
            builder.Append("  ").Append(JsonSerializer.Serialize(removal.BeadId)).Append(": ").Append(JsonSerializer.Serialize(removal.Path))
                .Append("; reasons ").Append(JsonSerializer.Serialize(removal.Reasons)).Append("; ").Append(Checkout(removal.Registration)).Append('\n');
        builder.Append("\nRetained worktrees\n");
        if (report.StartupPlan.SurvivingWorktrees.Count == 0) builder.Append("  (none)\n");
        foreach (var retained in report.StartupPlan.SurvivingWorktrees)
            builder.Append("  ").Append(JsonSerializer.Serialize(retained.BeadId)).Append(": ").Append(JsonSerializer.Serialize(retained.Registration.Path))
                .Append(retained.Reusable ? "; reusable" : "; retained conservatively").Append("; presence ").Append(retained.Presence)
                .Append("; ").Append(Checkout(retained.Registration)).Append('\n');
        builder.Append("\nOrphan tmp deletions (hypothetical)\n");
        if (report.StartupPlan.OrphanTmp.Count == 0) builder.Append("  (none)\n");
        foreach (var orphan in report.StartupPlan.OrphanTmp)
            builder.Append("  ").Append(JsonSerializer.Serialize(orphan.BeadId)).Append(": ").Append(JsonSerializer.Serialize(orphan.Path)).Append("; no surviving active worktree\n");
        builder.Append("\nStartup diagnostics\n");
        if (report.StartupPlan.Diagnostics.Count == 0) builder.Append("  (none)\n");
        foreach (var diagnostic in report.StartupPlan.Diagnostics)
        {
            builder.Append("  ").Append(JsonSerializer.Serialize(diagnostic.BeadId)).Append(": ");
            if (diagnostic.Code == StartupDiagnosticCode.ForeignInProgressClaim)
                builder.Append("foreign_in_progress_claim; assignee ").Append(JsonSerializer.Serialize(diagnostic.Assignee));
            else if (diagnostic.Code == StartupDiagnosticCode.UnassignedInProgressClaim)
                builder.Append("unassigned_in_progress_claim");
            else
            {
                builder.Append(JsonNamingPolicy.SnakeCaseLower.ConvertName(diagnostic.Code.ToString()));
                if (diagnostic.Path is not null) builder.Append("; path ").Append(JsonSerializer.Serialize(diagnostic.Path));
                if (diagnostic.Reason is not null) builder.Append("; reason ").Append(JsonSerializer.Serialize(diagnostic.Reason));
            }
            builder.Append('\n');
        }

        builder.Append("\nActive Plan Beads\n");
        AppendField(builder, "count", report.PlanningPoolPlan.ActivePlanBeadCount.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "threshold", report.PlanningPoolPlan.MaxActivePlanBeads.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "backpressure", report.PlanningPoolPlan.BackpressureActive ? "active" : "inactive");
        AppendField(builder, "backpressure_reason", report.PlanningPoolPlan.BackpressureActive
            ? $"active_plan_beads={report.PlanningPoolPlan.ActivePlanBeadCount.ToString(CultureInfo.InvariantCulture)} >= max_active_plan_beads={report.PlanningPoolPlan.MaxActivePlanBeads.ToString(CultureInfo.InvariantCulture)}"
            : "none");

        builder.Append("\nPlanning selected\n");
        if (report.PlanningPoolPlan.Selected.Count == 0) builder.Append("  (none)\n");
        foreach (var candidate in report.PlanningPoolPlan.Selected)
            builder.Append("  rank=").Append(candidate.Rank.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(JsonSerializer.Serialize(candidate.Bead.Id)).Append("; role=").Append(Role(candidate.Role))
                .Append("; source=").Append(Source(candidate)).Append('\n');

        builder.Append("\nPlanning waiting\n");
        if (report.PlanningPoolPlan.Waiting.Count == 0) builder.Append("  (none)\n");
        foreach (var waiting in report.PlanningPoolPlan.Waiting)
        {
            var candidate = waiting.Candidate;
            builder.Append("  rank=").Append(candidate.Rank.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(JsonSerializer.Serialize(candidate.Bead.Id)).Append("; role=").Append(Role(candidate.Role))
                .Append("; reason=");
            if (waiting.Reason == PlanningWaitReason.Backpressure)
                builder.Append("backpressure (active_plan_beads=").Append(report.PlanningPoolPlan.ActivePlanBeadCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" >= max_active_plan_beads=").Append(report.PlanningPoolPlan.MaxActivePlanBeads.ToString(CultureInfo.InvariantCulture)).Append(')');
            else
                builder.Append("capacity (max_concurrent_planning=").Append(report.PlanningPoolPlan.MaxConcurrentPlanning.ToString(CultureInfo.InvariantCulture)).Append(')');
            builder.Append("; source=").Append(Source(candidate)).Append('\n');
        }

        builder.Append("\nPlanning excluded diagnostics\n");
        if (report.PlanningPoolPlan.Excluded.Count == 0) builder.Append("  (none)\n");
        foreach (var exclusion in report.PlanningPoolPlan.Excluded)
            builder.Append("  ").Append(JsonSerializer.Serialize(exclusion.BeadId)).Append(": ")
                .Append(JsonNamingPolicy.SnakeCaseLower.ConvertName(exclusion.Code.ToString()))
                .Append("; stages=").Append(JsonSerializer.Serialize(exclusion.StageLabels)).Append('\n');

        builder.Append('\n').Append("Configuration\n");
        AppendField(builder, "max_concurrent_planning", report.Configuration.Concurrency.MaxConcurrentPlanning.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "max_concurrent_delivery", report.Configuration.Concurrency.MaxConcurrentDelivery.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "merger_slots", ConcurrencyConfig.MergerSlots.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "max_active_plan_beads", report.Configuration.Concurrency.MaxActivePlanBeads.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "max_repair_cycles", report.Configuration.Limits.MaxRepairCycles.ToString(CultureInfo.InvariantCulture));
        AppendField(builder, "max_operational_retries", report.Configuration.Limits.MaxOperationalRetries.ToString(CultureInfo.InvariantCulture));

        builder.Append('\n').Append("Agent roles\n");
        AppendAgentSettings(builder, "agent_defaults", report.AgentRoles.AgentDefaults);
        foreach (var (role, settings) in report.AgentRoles.Roles.InCanonicalOrder())
        {
            AppendAgentSettings(builder, AgentRoles.ToConfigKey(role), settings);
        }

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string value) =>
        builder.Append("  ").Append(name).Append(": ").Append(value).Append('\n');

    private static string Checkout(Inspection.GitWorktreeSnapshot entry) => entry.Detached ? "detached HEAD" : "branch " + JsonSerializer.Serialize(entry.Branch);

    private static string Role(PlanningRole role) => role == PlanningRole.PlanChallenger ? "plan_challenger" : "planner";

    private static string Source(PlanningPoolCandidate candidate) => candidate.RecoveredAfterClaimRelease
        ? "hypothetical_post_release" : "native_unassigned_ready";

    private static void AppendAgentSettings(StringBuilder builder, string name, AgentSettings settings)
    {
        builder.Append("  ").Append(name).Append(": cli=").Append(AgentCliNames.ToConfigValue(settings.Cli));
        builder.Append(" model=").Append(settings.Model ?? "(agent-cli-default)");
        builder.Append(" effort=").Append(settings.Effort);
        builder.Append(" timeout_minutes=").Append(settings.TimeoutMinutes.ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }
}
