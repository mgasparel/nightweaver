namespace Nightweaver.Configuration;

internal static class ProjectConfigDefaults
{
    internal const string DefaultBranch = "main";
    internal const int MaxConcurrentPlanning = 2;
    internal const int MaxConcurrentDelivery = 3;
    internal const int MaxActivePlanBeads = 5;
    internal const int MaxRepairCycles = 3;
    internal const int MaxOperationalRetries = 2;

    internal static AgentSettings AgentDefaults { get; } = new(AgentCli.Claude, null, "high", 30);
}
