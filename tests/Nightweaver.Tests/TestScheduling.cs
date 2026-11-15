using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from repair-recovery-isolated.xml (1,207 passing tests),
// including measured operational observation and native worktree costs.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(PoolConcurrencyIntegrationTests), 252.738),
        (nameof(MergerCompletionFinalizerTests), 182.802),
        (nameof(MergerOutcomeVerifierTests), 105.957),
        (nameof(ManualDeliveryPilotSecondSignalTests), 101.137),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 98.097),
        (nameof(AgentStageDeliveryIntegrationTests), 87.934),
        (nameof(PublishedContinuousStopIntegrationTests), 86.508),
        (nameof(OperationalObservationSourceTests), 80.333),
        (nameof(MergerPoolIntegrationTests), 71.098),
        (nameof(PlanningNativeTurnTests), 59.032),
        (nameof(WorktreeProvisionerIntegrationTests), 55.457),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 55.194),
        (nameof(WebBeadsIntegrationTests), 53.405),
        (nameof(DeliveryPoolIntegrationTests), 42.157),
        (nameof(BeadsIntegrationTests), 39.806),
        (nameof(FactoryTelemetryIntegrationTests), 32.764),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 31.251),
        (nameof(BeadsJournalClientIntegrationTests), 30.074),
        (nameof(DeliverySlotCoordinatorTests), 24.505),
        (nameof(ManualPilotMergerRetryTests), 22.420),
        (nameof(NightweaverAppTests), 21.344),
        (nameof(AgentStreamCleanupIntegrationTests), 16.399),
        (nameof(WebHostIntegrationTests), 13.546),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 12.731),
        (nameof(WindowsConsoleExitSettlementTests), 11.118),
        (nameof(DispatchFixtureTemplateTests), 8.667),
        (nameof(TargetRepositoryValidatorTests), 8.560),
        (nameof(PlanningExecutionTests), 8.432),
        (nameof(FixtureTemplateTests), 7.651),
        (nameof(BeadsApplicationTests), 7.474),
        (nameof(WorkspaceRelationsWebTests), 7.067),
        (nameof(PlanningReadinessWebTests), 6.348),
        (nameof(WindowsConsoleProcessTests), 5.995),
        (nameof(DispatchClaimTests), 5.375),
        (nameof(CompletedEnvironmentCleanupTests), 5.338),
        (nameof(AgentOutcomeClassifierTests), 5.016),
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
