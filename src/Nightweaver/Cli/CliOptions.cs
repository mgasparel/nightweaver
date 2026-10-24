using Nightweaver.Configuration;

namespace Nightweaver.Cli;

internal sealed record CliOptions(ProjectSelector? Selector, bool DryRun, bool Help);
