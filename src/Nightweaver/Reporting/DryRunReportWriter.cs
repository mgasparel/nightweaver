using System.Text;

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
        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string name, string value) =>
        builder.Append("  ").Append(name).Append(": ").Append(value).Append('\n');
}
