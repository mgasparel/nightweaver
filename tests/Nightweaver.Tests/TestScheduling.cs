using Xunit.Sdk;
using Xunit.v3;

// Start the longest collections first so their serial tail overlaps smaller tests.
// Weights refreshed from manual-main.xml (1,230 passing tests),
// including previously unweighted native collections in the final tail.
// Keep the six-thread cap and isolated fixtures intact.
[assembly: Parallelization(MaxThreads = 6)]
[assembly: AssemblyFixture(typeof(Nightweaver.Tests.PublishedCliArtifactFixture))]
[assembly: TestCollectionOrderer(typeof(Nightweaver.Tests.IntegrationFirstCollectionOrderer))]

namespace Nightweaver.Tests;

public sealed class IntegrationFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly (string Name, double Seconds)[] MeasuredCollectionWeights =
    [
        (nameof(PoolConcurrencyIntegrationTests), 241.256),
        (nameof(MergerCompletionFinalizerTests), 174.283),
        (nameof(AgentStageDeliveryIntegrationTests), 96.910),
        (nameof(MergerOutcomeVerifierTests), 89.557),
        (nameof(ManualDeliveryPilotSecondSignalTests), 86.994),
        (nameof(WebBeadWorkspaceBoundaryIntegrationTests), 85.406),
        (nameof(OperationalObservationSourceTests), 77.800),
        (nameof(MergerPoolIntegrationTests), 72.429),
        (nameof(PublishedContinuousStopIntegrationTests), 69.835),
        (nameof(WorktreeProvisionerIntegrationTests), 57.974),
        (nameof(ManualPilotMergerRecoveryIntegrationTests), 57.677),
        (nameof(PlanningNativeTurnTests), 57.654),
        (nameof(WebBeadsIntegrationTests), 55.254),
        (nameof(DeliveryPoolIntegrationTests), 48.649),
        (nameof(BeadsIntegrationTests), 38.747),
        (nameof(StartupEnvironmentCleanerIntegrationTests), 34.802),
        (nameof(FactoryTelemetryIntegrationTests), 32.967),
        (nameof(BeadsJournalClientIntegrationTests), 30.731),
        (nameof(ManualPilotMergerRetryTests), 26.034),
        (nameof(DeliverySlotCoordinatorTests), 25.594),
        (nameof(NightweaverAppTests), 24.629),
        (nameof(ManualDeliveryPilotFixtureTemplateTests), 16.750),
        (nameof(WebHostIntegrationTests), 16.462),
        (nameof(AgentStreamCleanupIntegrationTests), 16.318),
        (nameof(DispatchFixtureTemplateTests), 12.227),
        (nameof(TargetRepositoryValidatorTests), 12.131),
        (nameof(WindowsConsoleExitSettlementTests), 11.490),
        (nameof(FixtureTemplateTests), 11.371),
        (nameof(PlanningExecutionTests), 11.040),
        (nameof(WorkspaceRelationsWebTests), 10.171),
        (nameof(CompletedEnvironmentCleanupTests), 10.096),
        (nameof(BeadsApplicationTests), 9.197),
        (nameof(AgentInvocationExecutorTests), 7.478),
        (nameof(DuplicateFailureClassifierIntegrationTests), 6.856),
        (nameof(PlanningReadinessWebTests), 6.273),
        (nameof(DispatchClaimTests), 5.465),
        (nameof(WindowsConsoleProcessTests), 5.139),
        (nameof(AgentOutcomeClassifierTests), 4.761),
        (nameof(BackgroundTerminalLifetimeTests), 4.403),
        (nameof(CompletedEnvironmentWorkerTests), 4.263),
        (nameof(CleanupWebTests), 4.029),
        (nameof(BeadPresentationTests), 3.668),
        (nameof(WindowsConsoleDescendantSettlementTests), 2.095),
        (nameof(ManualPilotEvidenceLogTests), 2.082),
        (nameof(TempDirectoryCleanupTests), 1.969),
        (nameof(SystemProcessRunnerTests), 1.949),
        (nameof(StudioWebTests), 1.810),
        (nameof(NativeBeadsCleanupTests), 1.249),
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
