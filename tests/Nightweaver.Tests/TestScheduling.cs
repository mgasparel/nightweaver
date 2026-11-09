using Xunit.Sdk;
using Xunit.v3;

// Native Git/Dolt tests are limited by disk/process contention, rather than CPU.
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
        (nameof(DispatchCompetingProjectsIntegrationTests), 42.485), // Measured in the combined focused Dispatch run.
        (nameof(ManualDeliveryPilotIntegrationTests), 568.674),
        (nameof(ManualDeliveryPilotSecondSignalTests), 44.116),
        (nameof(DeliveryPoolIntegrationTests), 402.243),
        (nameof(PublishedContinuousStopIntegrationTests), 369.103),
        (nameof(ManualDeliveryPilotHeldMergerTests), 350.962),
        (nameof(MergerPoolIntegrationTests), 340.332),
        (nameof(WebBeadsIntegrationTests), 308.293),
        (nameof(AgentStageDeliveryIntegrationTests), 297.508),
        (nameof(ManualDeliveryPilotCrashRecoveryTests), 281.147),
        (nameof(AgentStageIntegrationTests), 276.660),
        (nameof(PublishedCliIntegrationTests), 260.417),
        (nameof(DispatchAdmissionIntegrationTests), 170.755),
        (nameof(BackgroundDeploymentIntegrationTests), 216.275),
        (nameof(AgentStageRepairIntegrationTests), 197.540),
        (nameof(MergerCompletionFinalizerTests), 192.540),
        (nameof(AgentStageRetryIntegrationTests), 176.877),
        (nameof(ManualPilotMergerRetryTests), 169.623),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 162.187),
        (nameof(DispatchRecoveryIntegrationTests), 130.632),
        (nameof(DispatchStartupCleanupIntegrationTests), 118.498),
        (nameof(DispatchFixtureTemplateTests), 9.809),
        (nameof(NeedsHumanEscalatorIntegrationTests), 120),
        (nameof(WorktreeProvisionerIntegrationTests), 90),
        (nameof(DeliveryDiscoveryIntegrationTests), 75),
        (nameof(PlanningIntegrationTests), 60),
        (nameof(BeadsIntegrationTests), 45),
        (nameof(WorktreeIntegrationTests), 30),
        (nameof(NightweaverAppTests), 20),
        (nameof(DeliverySlotCoordinatorTests), 10),
        (nameof(ProductDecisionPauseHandlerTests), 10)
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
