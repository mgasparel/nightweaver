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
    // Keep credentials out of synthesized record formatting and debugger property expansion.
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal string? OpenAiApiKey { get; init; }

    internal string RecoveryActor => $"nightweaver:{Name.Value}";
}
