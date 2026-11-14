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
    internal string? DecisionsApiKey { get; init; }

    internal DecisionsApiProvider DecisionsApiProvider { get; init; }
    internal Uri DecisionsApiEndpoint { get; init; } = new("https://api.openai.com/v1/decisions");
    internal string DecisionsApiModel { get; init; } = "gpt-6-luna";

    internal string RecoveryActor => $"nightweaver:{Name.Value}";
}
