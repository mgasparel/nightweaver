using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from Windows CI run 43529751486, including full graph publication/Delivery.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(MergerCompletionFinalizerTests), 182.582),
        (nameof(PoolConcurrencyIntegrationTests), 170.145),
        (nameof(PlanningNativeTurnTests), 149.822),
        (nameof(ManualDeliveryPilotSecondSignalTests), 138.969),
        (nameof(AgentStageDeliveryIntegrationTests), 108.902),
        (nameof(MergerPoolIntegrationTests), 78.587),
        (nameof(MergerOutcomeVerifierTests), 77.420),
        (nameof(DeliveryPoolIntegrationTests), 67.701),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 65.718),
        (nameof(WebBeadsIntegrationTests), 62.403),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 60.669),
        (nameof(PublishedContinuousStopIntegrationTests), 43.120),
        (nameof(BeadsJournalClientIntegrationTests), 38.638),
        (nameof(WorktreeProvisionerIntegrationTests), 35.267),
        (nameof(BeadsIntegrationTests), 32.957),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 29.840),
        (nameof(AgentStreamCleanupIntegrationTests), 22.703),
        (nameof(FactoryTelemetryIntegrationTests), 20.762),
        (nameof(NightweaverAppTests), 20.370),
        (nameof(CompletedEnvironmentCleanupTests), 20.301),
        (nameof(DeliverySlotCoordinatorTests), 19.924),
        (nameof(ManualPilotMergerRetryTests), 14.374),
        (nameof(WebHostIntegrationTests), 12.717),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 10.788),
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
