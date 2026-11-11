namespace Nightweaver.Configuration;

internal sealed record HostPaths
{
    internal HostPaths(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("Nightweaver root must be absolute", nameof(root));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    internal string Root { get; }
    internal string ProjectsDirectory => Path.Combine(Root, "projects");
    internal string ProjectConfigPath(ProjectSlug slug) => Path.Combine(ProjectsDirectory, slug.Value + ".json");
    internal string WorktreesRoot(ProjectSlug slug) => Path.Combine(Root, "worktrees", slug.Value);
    internal string TmpRoot(ProjectSlug slug) => Path.Combine(Root, "tmp", slug.Value);
    internal string LogsRoot(ProjectSlug slug) => Path.Combine(Root, "logs", slug.Value);
    internal string TelemetryRoot(ProjectSlug slug) => Path.Combine(Root, "telemetry", slug.Value);
    internal string FactoryTelemetryPath(ProjectSlug slug) => Path.Combine(TelemetryRoot(slug), "factory.json");
    internal string CatchUpStatePath(ProjectSlug slug) => Path.Combine(TelemetryRoot(slug), "catch-up.json");
    internal string BackgroundRoot(ProjectSlug slug) => Path.Combine(Root, "run", slug.Value, "background");
}
