using Nightweaver.Cli;

namespace Nightweaver.Configuration;

internal static class ProjectConfigLocator
{
    internal static string ResolveConfigPath(ProjectSelector selector, HostPaths hostPaths, string currentDirectory) =>
        selector switch
        {
            ProjectSelector.BySlug bySlug => hostPaths.ProjectConfigPath(bySlug.Slug),
            ProjectSelector.ByConfigPath byConfig => Path.GetFullPath(byConfig.RawPath, currentDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(selector))
        };
}
