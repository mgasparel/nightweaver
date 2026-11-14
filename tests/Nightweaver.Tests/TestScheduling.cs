using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from Windows CI run 43535447629, including full graph publication/Delivery.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(PlanningNativeTurnTests), 255.284),
        (nameof(MergerCompletionFinalizerTests), 175.200),
        (nameof(PoolConcurrencyIntegrationTests), 172.255),
        (nameof(AgentStageDeliveryIntegrationTests), 102.213),
        (nameof(ManualDeliveryPilotSecondSignalTests), 102.166),
        (nameof(MergerOutcomeVerifierTests), 91.525),
        (nameof(MergerPoolIntegrationTests), 79.366),
        (nameof(DeliveryPoolIntegrationTests), 68.626),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 62.953),
        (nameof(WebBeadsIntegrationTests), 60.587),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 58.691),
        (nameof(PublishedContinuousStopIntegrationTests), 44.943),
        (nameof(BeadsJournalClientIntegrationTests), 38.593),
        (nameof(WorktreeProvisionerIntegrationTests), 36.930),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 33.263),
        (nameof(BeadsIntegrationTests), 29.909),
        (nameof(NightweaverAppTests), 20.637),
        (nameof(FactoryTelemetryIntegrationTests), 19.797),
        (nameof(DeliverySlotCoordinatorTests), 18.287),
        (nameof(AgentStreamCleanupIntegrationTests), 18.074),
        (nameof(WebHostIntegrationTests), 13.006),
        (nameof(ManualPilotMergerRetryTests), 12.666),
        (nameof(CompletedEnvironmentWorkerTests), 12.024),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 11.521),
    ];

    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections) where TTestCollection : ITestCollection
        => OrderByWeight(testCollections, static collection => collection.TestCollectionDisplayName).ToArray();

    private static IOrderedEnumerable<TItem> OrderByWeight<TItem>(IEnumerable<TItem> items, Func<TItem, string> getName) =>
        items.OrderByDescending(item => EstimatedWeight(getName(item)))
            .ThenBy(getName, StringComparer.Ordinal);

    private static double EstimatedWeight(string name)
    {
        foreach (var (collection, seconds) in MeasuredCollectionWeights)
            if (name.Contains(collection, StringComparison.Ordinal)) return seconds;
        return 0;
    }
}
