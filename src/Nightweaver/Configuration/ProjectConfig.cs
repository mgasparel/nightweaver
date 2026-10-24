namespace Nightweaver.Configuration;

internal sealed record ProjectConfig(ProjectSlug Name, string ConfigFilePath, TargetRepositoryConfig TargetRepository)
{
    internal string RecoveryActor => $"nightweaver:{Name.Value}";
}
