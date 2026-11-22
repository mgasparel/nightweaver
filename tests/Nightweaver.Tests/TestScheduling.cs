using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from ns-3i4-prepared-complete.xml (1,297 passing tests),
// with the observer component cost updated from ns-3i4-observer-components.xml.
// Upgrade weights use cost-before.xml (110 passing tests) from the ns-qa2
// resume cost repair. Activation must overlap the native collections.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(UpgradeActivationTests), 150.465),
        (nameof(UpgradeReplacementTests), 38.491),
        (nameof(AgentStageDeliveryIntegrationTests), 114.906),
        (nameof(ManualDeliveryPilotSecondSignalTests), 125.621),
        (nameof(MergerPoolIntegrationTests), 97.970),
        (nameof(MergerCompletionFinalizerTests), 92.049),
        (nameof(WorktreeProvisionerIntegrationTests), 76.534),
        (nameof(PoolConcurrencyIntegrationTests), 94.895),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 79.030),
        (nameof(PlanningNativeTurnTests), 97.226),
        (nameof(OperationalObservationSourceTests), 58.136),
        (nameof(WebBeadsIntegrationTests), 89.953),
        (nameof(PublishedContinuousStopIntegrationTests), 55.210),
        (nameof(DeliveryPoolIntegrationTests), 74.476),
        (nameof(BeadsIntegrationTests), 50.431),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 51.644),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 46.866),
        (nameof(FactoryTelemetryIntegrationTests), 34.474),
        (nameof(BeadsJournalClientIntegrationTests), 39.635),
        (nameof(DeliverySlotCoordinatorTests), 32.555),
        (nameof(ManualPilotMergerRetryTests), 38.255),
        (nameof(NightweaverAppTests), 38.002),
        (nameof(MergerOutcomeVerifierTests), 38.460),
        (nameof(AgentStreamCleanupIntegrationTests), 23.452),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 26.724),
        (nameof(WindowsConsoleExitSettlementTests), 20.340),
        (nameof(WebHostIntegrationTests), 19.795),
        (nameof(TargetRepositoryValidatorTests), 16.505),
        (nameof(DispatchFixtureTemplateTests), 14.885),
        (nameof(FixtureTemplateTests), 12.781),
        (nameof(PlanningExecutionTests), 12.382),
        (nameof(WorkspaceRelationsWebTests), 14.692),
        (nameof(AgentInvocationExecutorTests), 7.984),
        (nameof(BeadsApplicationTests), 12.877),
        (nameof(PlanningReadinessWebTests), 9.705),
        (nameof(DispatchClaimTests), 8.735),
        (nameof(WindowsConsoleProcessTests), 8.623),
        (nameof(CompletedEnvironmentCleanupTests), 4.555),
        (nameof(AgentOutcomeClassifierTests), 6.316),
        (nameof(DuplicateFailureClassifierIntegrationTests), 4.066),
        (nameof(CleanupWebTests), 5.525),
        (nameof(BackgroundTerminalLifetimeTests), 4.723),
        (nameof(ManualPilotEvidenceLogTests), 3.117),
        (nameof(WindowsConsoleDescendantSettlementTests), 2.191),
        (nameof(BeadPresentationTests), 1.474),
        (nameof(StudioWebTests), 1.945),
        (nameof(SystemProcessRunnerTests), 1.964),
        (nameof(TempDirectoryCleanupTests), 1.983),
        (nameof(CompletedEnvironmentWorkerTests), 2.229),
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
