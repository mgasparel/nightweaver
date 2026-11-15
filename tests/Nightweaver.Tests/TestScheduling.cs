using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from implement-complete.xml (1,243 passing tests),
// including the repaired native Merger composition and current retained boundaries.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(AgentStageDeliveryIntegrationTests), 158.272),
        (nameof(ManualDeliveryPilotSecondSignalTests), 152.164),
        (nameof(MergerPoolIntegrationTests), 138.296),
        (nameof(MergerCompletionFinalizerTests), 131.407),
        (nameof(WorktreeProvisionerIntegrationTests), 109.593),
        (nameof(PoolConcurrencyIntegrationTests), 108.747),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 91.715),
        (nameof(PlanningNativeTurnTests), 89.144),
        (nameof(OperationalObservationSourceTests), 86.991),
        (nameof(WebBeadsIntegrationTests), 80.21),
        (nameof(PublishedContinuousStopIntegrationTests), 67.924),
        (nameof(DeliveryPoolIntegrationTests), 66.636),
        (nameof(BeadsIntegrationTests), 53.859),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 41.035),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 33.256),
        (nameof(FactoryTelemetryIntegrationTests), 31.394),
        (nameof(BeadsJournalClientIntegrationTests), 29.011),
        (nameof(DeliverySlotCoordinatorTests), 26.536),
        (nameof(ManualPilotMergerRetryTests), 25.913),
        (nameof(NightweaverAppTests), 22.85),
        (nameof(MergerOutcomeVerifierTests), 20.475),
        (nameof(AgentStreamCleanupIntegrationTests), 17.645),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 15.016),
        (nameof(WindowsConsoleExitSettlementTests), 13.685),
        (nameof(WebHostIntegrationTests), 13.484),
        (nameof(TargetRepositoryValidatorTests), 13.305),
        (nameof(DispatchFixtureTemplateTests), 12.421),
        (nameof(FixtureTemplateTests), 11.948),
        (nameof(PlanningExecutionTests), 11.687),
        (nameof(WorkspaceRelationsWebTests), 10.735),
        (nameof(AgentInvocationExecutorTests), 7.984),
        (nameof(BeadsApplicationTests), 7.79),
        (nameof(PlanningReadinessWebTests), 7.064),
        (nameof(DispatchClaimTests), 6.843),
        (nameof(WindowsConsoleProcessTests), 6.764),
        (nameof(CompletedEnvironmentCleanupTests), 5.518),
        (nameof(AgentOutcomeClassifierTests), 4.758),
        (nameof(DuplicateFailureClassifierIntegrationTests), 4.066),
        (nameof(CleanupWebTests), 3.527),
        (nameof(BackgroundTerminalLifetimeTests), 3.227),
        (nameof(ManualPilotEvidenceLogTests), 2.675),
        (nameof(WindowsConsoleDescendantSettlementTests), 2.28),
        (nameof(BeadPresentationTests), 2.244),
        (nameof(StudioWebTests), 2.132),
        (nameof(SystemProcessRunnerTests), 2.086),
        (nameof(TempDirectoryCleanupTests), 1.978),
        (nameof(CompletedEnvironmentWorkerTests), 1.948),
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
