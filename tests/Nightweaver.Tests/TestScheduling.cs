using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from Windows CI run 43509330048, with all cases retained.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(MergerCompletionFinalizerTests), 204.255),
        (nameof(PoolConcurrencyIntegrationTests), 196.015),
        (nameof(ManualDeliveryPilotSecondSignalTests), 176.549),
        (nameof(AgentStageDeliveryIntegrationTests), 133.949),
        (nameof(MergerOutcomeVerifierTests), 98.450),
        (nameof(MergerPoolIntegrationTests), 90.860),
        (nameof(DeliveryPoolIntegrationTests), 88.417),
        (nameof(WebBeadsIntegrationTests), 76.138),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 69.865),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 65.352),
        (nameof(PublishedContinuousStopIntegrationTests), 55.595),
        (nameof(BeadsJournalClientIntegrationTests), 44.546),
        (nameof(BeadsIntegrationTests), 42.267),
        (nameof(WorktreeProvisionerIntegrationTests), 37.580),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 32.000),
        (nameof(PlanningNativeTurnTests), 31.025),
        (nameof(FactoryTelemetryIntegrationTests), 22.116),
        (nameof(NightweaverAppTests), 20.925),
        (nameof(DeliverySlotCoordinatorTests), 20.220),
        (nameof(AgentStreamCleanupIntegrationTests), 18.245),
        (nameof(CompletedEnvironmentCleanupTests), 14.397),
        (nameof(WebHostIntegrationTests), 13.366),
        (nameof(DispatchClaimTests), 12.213),
        (nameof(ManualPilotMergerRetryTests), 11.360),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 10.773)
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
