namespace Nightweaver.Configuration;

internal sealed record AgentSettingsOverride(
    Optional<AgentCli> Cli,
    Optional<string?> Model,
    Optional<string> Effort,
    Optional<int> TimeoutMinutes)
{
    internal static AgentSettingsOverride None { get; } = new(
        Optional<AgentCli>.Absent,
        Optional<string?>.Absent,
        Optional<string>.Absent,
        Optional<int>.Absent);
}
