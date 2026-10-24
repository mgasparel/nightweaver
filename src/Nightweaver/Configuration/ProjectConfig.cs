namespace Nightweaver.Configuration;

internal sealed record ProjectConfig(
    ProjectSlug Name,
    string ConfigFilePath,
    TargetRepositoryConfig TargetRepository,
    ConcurrencyConfig Concurrency,
    LimitsConfig Limits,
    AgentSettings AgentDefaults,
    RoleSettings Roles)
{
    internal string RecoveryActor => $"nightweaver:{Name.Value}";
}
