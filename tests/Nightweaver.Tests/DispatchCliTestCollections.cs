namespace Nightweaver.Tests;

public sealed class DispatchAdmissionIntegrationTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public Task PublishedDispatchRunsInferredAndExplicitStagesAndRefusesIneligibleWork() =>
        DispatchScenarioSupport.PublishedDispatchRunsInferredAndExplicitStagesAndRefusesIneligibleWork(published);

    [Fact]
    public Task PublishedDispatchMaterializesApprovedPlanAndReleasesClaimExactlyOnce() =>
        DispatchScenarioSupport.PublishedDispatchMaterializesApprovedPlanAndReleasesClaimExactlyOnce(published);

    [Fact]
    public Task PlannedHandoffRefusesChangedOwnerAndPreservesEvidence() =>
        DispatchScenarioSupport.PlannedHandoffRefusesChangedOwnerAndPreservesEvidence(published);

    [Theory]
    [InlineData("planned-materialization-nonzero", "planned", "NonZeroExit")]
    [InlineData("partial-materialization", "stage:plan-approved", "NonZeroExit")]
    [InlineData("planned-without-link", "planned", "IllegalBeadTransition")]
    public Task FailedMaterializationRetainsEvidenceAndNeverReplays(string action, string label, string failure) =>
        DispatchScenarioSupport.FailedMaterializationRetainsEvidenceAndNeverReplays(published, action, label, failure);
}

public sealed class DispatchStartupCleanupIntegrationTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public Task PublishedDispatchEscalatesAnAgentThatExitsWithoutAValidTransition() =>
        DispatchScenarioSupport.PublishedDispatchEscalatesAnAgentThatExitsWithoutAValidTransition(published);

    [Fact]
    public Task PublishedStartupRemovesOnlyCompletedManagedWorktreesAndKeepsBranchesAndSurvivors() =>
        DispatchScenarioSupport.PublishedStartupRemovesOnlyCompletedManagedWorktreesAndKeepsBranchesAndSurvivors(published);

    [Fact]
    public Task PublishedStartupRechecksPauseBeforeRemovingAPlannedWorktree() =>
        DispatchScenarioSupport.PublishedStartupRechecksPauseBeforeRemovingAPlannedWorktree(published);

    [Fact]
    public Task PublishedStartupRetainsAndEscalatesOnlyFailedRemovalThenContinuesDispatch() =>
        DispatchScenarioSupport.PublishedStartupRetainsAndEscalatesOnlyFailedRemovalThenContinuesDispatch(published);

    [Fact]
    public Task PublishedStartupStopsWhenRequiredCleanupEscalationCannotBePersisted() =>
        DispatchScenarioSupport.PublishedStartupStopsWhenRequiredCleanupEscalationCannotBePersisted(published);
}

public sealed class DispatchCompetingProjectsIntegrationTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public Task PublishedDispatchExcludesCompetingProjectsAcrossHostRootsAndRecoversAfterProcessDeath() =>
        DispatchScenarioSupport.PublishedDispatchExcludesCompetingProjectsAcrossHostRootsAndRecoversAfterProcessDeath(published);
}

public sealed class DispatchRecoveryIntegrationTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public Task PublishedDispatchRecoversOnlyExactProjectClaimsAndAdmitsFromFreshReadiness() =>
        DispatchScenarioSupport.PublishedDispatchRecoversOnlyExactProjectClaimsAndAdmitsFromFreshReadiness(published);

    [Fact]
    public Task PublishedDispatchStopsOnRequiredTrackerAndSharedRootFailures() =>
        DispatchScenarioSupport.PublishedDispatchStopsOnRequiredTrackerAndSharedRootFailures(published);

    [Fact]
    public Task PublishedRecoveryRereadsGuardMismatchAndUncertainWritesWithoutReplay() =>
        DispatchScenarioSupport.PublishedRecoveryRereadsGuardMismatchAndUncertainWritesWithoutReplay(published);
}

public sealed class DispatchFixtureTemplateTests
{
    [Fact]
    public Task DispatchTargetCopiesKeepTrackerAndGitOriginChangesInsideTheirOwnRoot() =>
        DispatchScenarioSupport.DispatchTargetCopiesKeepTrackerAndGitOriginChangesInsideTheirOwnRoot();
}

internal sealed class DispatchRepositoryCopy(BeadsFixture target, string origin) : IDisposable
{
    internal BeadsFixture Target { get; } = target;
    internal string Origin { get; } = origin;

    public void Dispose() => Target.Dispose();
}

internal static class DispatchRepositoryTemplate
{
    private static readonly Lazy<Seed> Template = new(CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static DispatchRepositoryCopy CreateCopy(string relativePath)
    {
        var seed = Template.Value;
        var target = new BeadsFixture(relativePath, seed.Target.Path, seed.Target.ControlBeadId);
        var origin = Path.Combine(target.Root, "bare origin.git");
        TestDirectoryTemplate.Copy(seed.Origin, origin);
        target.Run("git", ["remote", "set-url", "origin", origin]);
        return new(target, origin);
    }

    internal static IReadOnlyDictionary<string, int> SeedToolRunCounts => Template.Value.Target.ToolRunCounts;

    private static Seed CreateTemplate()
    {
        var target = new BeadsFixture("dispatch target template");
        target.Run("git", ["config", "user.name", "Nightweaver Dispatch Fixture"]);
        target.Run("git", ["config", "user.email", "fixture@example.invalid"]);
        File.WriteAllText(Path.Combine(target.Path, "README.md"), "dispatch base\n", new System.Text.UTF8Encoding(false));
        target.Run("git", ["add", "README.md"]);
        target.Run("git", ["commit", "-m", "dispatch fixture base"]);
        var origin = Path.Combine(target.Root, "bare origin.git");
        Directory.CreateDirectory(origin);
        target.Run("git", ["init", "--bare", origin]);
        target.Run("git", ["remote", "add", "origin", origin]);
        target.Run("git", ["push", "--set-upstream", "origin", "main"]);
        var seed = new Seed(target, origin);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => seed.Dispose();
        return seed;
    }

    private sealed class Seed(BeadsFixture target, string origin) : IDisposable
    {
        internal BeadsFixture Target { get; } = target;
        internal string Origin { get; } = origin;

        public void Dispose() => Target.Dispose();
    }
}
