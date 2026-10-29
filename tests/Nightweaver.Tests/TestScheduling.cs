using Xunit.Sdk;
using Xunit.v3;

// Native Git/Dolt tests are limited by disk/process contention, rather than CPU.
// Start the longest collections first so their serial tail overlaps smaller tests.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections) where TTestCollection : ITestCollection
        => testCollections.OrderBy(collection => Priority(collection.TestCollectionDisplayName))
            .ThenBy(collection => collection.TestCollectionDisplayName, StringComparer.Ordinal).ToArray();

    private static int Priority(string name)
    {
        if (name.Contains(nameof(AgentStageDeliveryIntegrationTests), StringComparison.Ordinal)
            || name.Contains(nameof(AgentStageDeliveryPauseIntegrationTests), StringComparison.Ordinal)) return 0;
        if (name.Contains(nameof(AgentStageIntegrationTests), StringComparison.Ordinal)) return 1;
        if (name.Contains(nameof(AgentStageRepairIntegrationTests), StringComparison.Ordinal)) return 2;
        if (name.Contains(nameof(WebBeadsIntegrationTests), StringComparison.Ordinal)) return 3;
        if (name.Contains(nameof(PublishedCliIntegrationTests), StringComparison.Ordinal)) return 4;
        if (name.Contains(nameof(DispatchCliIntegrationTests), StringComparison.Ordinal)) return 5;
        if (name.Contains(nameof(AgentStageRetryIntegrationTests), StringComparison.Ordinal)) return 6;
        if (name.Contains(nameof(NeedsHumanEscalatorIntegrationTests), StringComparison.Ordinal)) return 7;
        if (name.Contains(nameof(WorktreeProvisionerIntegrationTests), StringComparison.Ordinal)) return 8;
        if (name.Contains(nameof(DeliveryDiscoveryIntegrationTests), StringComparison.Ordinal)) return 9;
        if (name.Contains(nameof(PlanningIntegrationTests), StringComparison.Ordinal)) return 10;
        if (name.Contains(nameof(BeadsIntegrationTests), StringComparison.Ordinal)) return 11;
        if (name.Contains(nameof(WorktreeIntegrationTests), StringComparison.Ordinal)) return 12;
        if (name.Contains(nameof(NightweaverAppTests), StringComparison.Ordinal)) return 13;
        if (name.Contains(nameof(DeliverySlotCoordinatorTests), StringComparison.Ordinal)
            || name.Contains(nameof(ProductDecisionPauseHandlerTests), StringComparison.Ordinal)) return 14;
        return 15;
    }
}
