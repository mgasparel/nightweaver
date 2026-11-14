using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from Windows CI run 43596043005, including continuous Planning
// and both native outcome-classifier force-stop cases.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(PoolConcurrencyIntegrationTests), 227.976),
        (nameof(PlanningNativeTurnTests), 156.307),
        (nameof(MergerCompletionFinalizerTests), 144.371),
        (nameof(PublishedContinuousStopIntegrationTests), 142.808),
        (nameof(ManualDeliveryPilotSecondSignalTests), 104.864),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 86.089),
        (nameof(AgentStageDeliveryIntegrationTests), 85.610),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 67.918),
        (nameof(MergerOutcomeVerifierTests), 66.087),
        (nameof(BeadsIntegrationTests), 62.487),
        (nameof(WebBeadsIntegrationTests), 56.931),
        (nameof(MergerPoolIntegrationTests), 54.682),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 53.005),
        (nameof(DeliveryPoolIntegrationTests), 52.288),
        (nameof(DeliverySlotCoordinatorTests), 49.010),
        (nameof(FactoryTelemetryIntegrationTests), 44.347),
        (nameof(BeadsJournalClientIntegrationTests), 33.149),
        (nameof(NightweaverAppTests), 27.690),
        (nameof(WorktreeProvisionerIntegrationTests), 27.029),
        (nameof(AgentStreamCleanupIntegrationTests), 22.484),
        (nameof(CompletedEnvironmentCleanupTests), 15.668),
        (nameof(ManualPilotMergerRetryTests), 13.448),
        (nameof(WebHostIntegrationTests), 12.414),
        (nameof(CompletedEnvironmentWorkerTests), 12.024),
        (nameof(DispatchClaimTests), 10.712),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 10.383),
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
