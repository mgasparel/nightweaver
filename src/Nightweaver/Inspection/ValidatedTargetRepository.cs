using Nightweaver.Configuration;

namespace Nightweaver.Inspection;

internal sealed record ValidatedTargetRepository(string Path, string DefaultBranch)
{
    internal string GitCommonDirectory { get; init; } = Path;
}
