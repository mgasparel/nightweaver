using Nightweaver.Background;
using Nightweaver.Supervision;

namespace Nightweaver.Upgrade;

internal static class UpgradeAcceptance
{
    internal static bool MatchesLaunchGrant(UpgradeProgress progress, UpgradeActivation grant) =>
        grant.PackageDigest == progress.NewPackageDigest
        && grant.Launches.Select(launch => launch.Project).Order()
            .SequenceEqual(progress.Maintenance.Where(item => item.StartRequested).Select(item => item.Project).Order());

    internal static bool HasHealthyFinalOwner(BackgroundOwnerRecord current) =>
        current.State == "running" && current.Failure is null && current.StopReason is null
        && current.WebHealth == "healthy" && current.RuntimeHealth == "live";

    internal static void ConfirmCurrentPairs(UpgradeProgress progress, string build, IEnumerable<UpgradeLaunch> launches,
        IReadOnlyDictionary<string, DeploymentObservation> observations, TimeProvider time)
    {
        foreach (var launch in launches)
            Confirm(progress, progress.Scope.Projects.Single(p => p.Name == launch.Project),
                build, launch.RunId, observations[launch.Project], time.GetUtcNow());
    }

    internal static UpgradeAcceptedPair Confirm(UpgradeProgress progress, AffectedUpgradeProject project,
        string build, string runId, DeploymentObservation observation, DateTimeOffset now)
    {
        var owner = observation.Owner;
        if (owner is null || owner.RunId != runId || progress.Owners.Any(o => o.RunId == owner.RunId)
            || observation.Health != DeploymentHealth.Healthy || observation.OwnerLockHeld != true
            || owner.State != "running" || owner.WebHealth != "healthy" || owner.RuntimeHealth != "live"
            || progress.Owners.Any(o => o.RuntimeId == owner.RuntimeId)
            || observation.Process?.State != OwnerProcessState.Alive
            || !DeploymentObserver.Fresh(observation.Utc, now)
            || !DeploymentObserver.Fresh(observation.ControlObservedUtc, now)
            || !DeploymentObserver.Fresh(observation.RuntimeObservedUtc, now)
            || observation.Installation != new DeploymentIdentity(progress.Scope.Root, project.Configuration, project.Name, project.Repository, project.GitCommonDirectory)
            || !DeploymentObserver.MatchesInstallation(observation.Installation, owner)
            || !LiveReleaseIdentity.Matches(owner.CliRelease, progress.NewPackageDigest, build,
                owner.ProcessId, owner.ProcessStartedAtUtc, progress.Scope.Root, project.Configuration, project.Name)
            || owner.WebRelease is not { ProcessId: > 0 } web || web.ProcessId == owner.ProcessId
            || !LiveReleaseIdentity.Matches(web, progress.NewPackageDigest, build, web.ProcessId, web.CreatedUtc,
                progress.Scope.Root, project.Configuration, project.Name)
            || owner.RuntimeId is null || owner.ContainmentVersion != 1)
            throw new InvalidOperationException("Fresh live CLI and Web replacement acceptance is unconfirmed.");
        return new(UpgradeLifecycle.OwnerEvidence(owner), owner.CliRelease!, web);
    }
}
