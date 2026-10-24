namespace Nightweaver.Configuration;

internal sealed record ConcurrencyConfig(int MaxConcurrentPlanning, int MaxConcurrentDelivery, int MaxActivePlanBeads)
{
    internal const int MergerSlots = 1;
}
