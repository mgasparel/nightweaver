namespace Nightweaver.Configuration;

internal sealed record LimitsConfig(int MaxRepairCycles, int MaxOperationalRetries)
{
    internal int MaxAttemptsPerStage => 1 + MaxOperationalRetries;
}
