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
        AppendField(builder, "name", project.Name);
        AppendField(builder, "config", project.ConfigFilePath);
        AppendField(builder, "nightweaver_root", project.NightweaverRoot);
        AppendField(builder, "target_repository", project.TargetRepositoryPath);
        AppendField(builder, "default_branch", project.DefaultBranch);
        AppendField(builder, "recovery_actor", project.RecoveryActor);
        AppendField(builder, "worktrees_dir", project.WorktreesDirectory);
        AppendField(builder, "tmp_dir", project.TmpDirectory);
        AppendField(builder, "logs_dir", project.LogsDirectory);

        builder.Append("\nStartup claim releases (hypothetical)\n");
        if (report.StartupPlan.Releases.Count == 0) builder.Append("  (none)\n");
        foreach (var release in report.StartupPlan.Releases)
        {
            builder.Append("  ").Append(JsonSerializer.Serialize(release.BeadId)).Append(": in_progress -> open; assignee ")
                .Append(JsonSerializer.Serialize(release.ExpectedAssignee)).Append(" -> unassigned\n");
        }
        builder.Append("\nStartup diagnostics\n");
        if (report.StartupPlan.Diagnostics.Count == 0) builder.Append("  (none)\n");
        foreach (var diagnostic in report.StartupPlan.Diagnostics)
        {
            builder.Append("  ").Append(JsonSerializer.Serialize(diagnostic.BeadId)).Append(": ");
            if (diagnostic.Code == StartupDiagnosticCode.ForeignInProgressClaim)
                builder.Append("foreign_in_progress_claim; assignee ").Append(JsonSerializer.Serialize(diagnostic.Assignee));
            else
                builder.Append("unassigned_in_progress_claim");
            builder.Append('\n');
        }

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

    private static void AppendAgentSettings(StringBuilder builder, string name, AgentSettings settings)
    {
        builder.Append("  ").Append(name).Append(": cli=").Append(AgentCliNames.ToConfigValue(settings.Cli));
        builder.Append(" model=").Append(settings.Model ?? "(agent-cli-default)");
        builder.Append(" effort=").Append(settings.Effort);
        builder.Append(" timeout_minutes=").Append(settings.TimeoutMinutes.ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }
}
