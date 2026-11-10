using Xunit.Sdk;
using Xunit.v3;

// Order the longest retained native collections first so they overlap with short cases.
// Start the longest collections first so their serial tail overlaps smaller tests.
// Refresh measured seconds when a native collection changes; keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(AgentStageDeliveryIntegrationTests), 111.469),
        (nameof(MergerPoolIntegrationTests), 107.119),
        (nameof(ManualDeliveryPilotSecondSignalTests), 80.129),
        (nameof(MergerOutcomeVerifierTests), 63.089),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 51.805),
        (nameof(WorktreeProvisionerIntegrationTests), 42.712),
        (nameof(PublishedContinuousStopIntegrationTests), 40.975),
        (nameof(WebBeadsIntegrationTests), 39.386),
        (nameof(MergerCompletionFinalizerTests), 36.875),
        (nameof(BeadsIntegrationTests), 29.239),
        (nameof(DeliveryPoolIntegrationTests), 22.509),
        (nameof(ManualPilotMergerRetryTests), 12.747)
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
