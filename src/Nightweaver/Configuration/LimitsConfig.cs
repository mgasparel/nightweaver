namespace Nightweaver.Configuration;

internal sealed record LimitsConfig(int MaxRepairCycles, int MaxOperationalRetries)
{
    internal long MaxAttemptsPerStage => 1L + MaxOperationalRetries;
}
