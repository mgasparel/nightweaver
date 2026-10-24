namespace Nightweaver.Configuration;

internal enum AgentCli
{
    Claude,
    Codex
}

internal static class AgentCliNames
{
    internal static string ToConfigValue(AgentCli cli) => cli switch
    {
        AgentCli.Claude => "claude",
        AgentCli.Codex => "codex",
        _ => throw new ArgumentOutOfRangeException(nameof(cli), cli, "Unknown Agent CLI")
    };

    internal static bool TryParseConfigValue(string value, out AgentCli cli)
    {
        switch (value)
        {
            case "claude":
                cli = AgentCli.Claude;
                return true;
            case "codex":
                cli = AgentCli.Codex;
                return true;
            default:
                cli = default;
                return false;
        }
    }
}
