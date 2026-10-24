namespace Nightweaver.Configuration;

internal sealed record AgentSettings(AgentCli Cli, string? Model, string Effort, int TimeoutMinutes)
{
    internal TimeSpan Timeout => TimeSpan.FromMinutes(TimeoutMinutes);
}
