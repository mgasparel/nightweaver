using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from Windows CI run 43502446524, with all 722 cases retained.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(ManualDeliveryPilotSecondSignalTests), 243.257),
        (nameof(PoolConcurrencyIntegrationTests), 233.211),
        (nameof(AgentStageDeliveryIntegrationTests), 132.472),
        (nameof(MergerCompletionFinalizerTests), 104.235),
        (nameof(DeliveryPoolIntegrationTests), 98.570),
        (nameof(MergerPoolIntegrationTests), 91.852),
        (nameof(MergerOutcomeVerifierTests), 80.551),
        (nameof(WebBeadsIntegrationTests), 67.215),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 67.152),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 62.440),
        (nameof(AgentStageCoordinatorTests), 61.916),
        (nameof(PublishedContinuousStopIntegrationTests), 55.747),
        (nameof(BeadsIntegrationTests), 39.871),
        (nameof(BeadsJournalClientIntegrationTests), 38.888),
        (nameof(WorktreeProvisionerIntegrationTests), 34.392),
        (nameof(AgentStreamCleanupIntegrationTests), 27.549),
        (nameof(DeliverySlotCoordinatorTests), 21.531),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 19.254),
        (nameof(FactoryTelemetryIntegrationTests), 19.230),
        (nameof(NightweaverAppTests), 18.629),
        (nameof(ManualPilotMergerRetryTests), 12.457),
        (nameof(AgentOutcomeClassifierTests), 11.353),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 10.674),
        (nameof(WebHostIntegrationTests), 10.651),
        (nameof(FixtureTemplateTests), 9.086),
        (nameof(SystemProcessRunnerTests), 8.852),
        (nameof(DispatchFixtureTemplateTests), 8.253),
        (nameof(BeadsApplicationTests), 8.229)
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
