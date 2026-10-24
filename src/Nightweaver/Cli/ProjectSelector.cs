using Nightweaver.Configuration;

namespace Nightweaver.Cli;

internal abstract record ProjectSelector
{
    private ProjectSelector() { }

    internal sealed record BySlug(ProjectSlug Slug) : ProjectSelector;
    internal sealed record ByConfigPath(string RawPath) : ProjectSelector;
}
