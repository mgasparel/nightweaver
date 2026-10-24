using System.Collections.Frozen;
using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal sealed record PostSweepReadiness(
    IReadOnlySet<string> NativeUnassignedReady,
    IReadOnlySet<string> RecoverableOwnedReady,
    IReadOnlySet<string> ReadyBeadIds,
    IReadOnlySet<string> RecoveredReadyBeadIds);

internal static class PostSweepReadinessProjector
{
    internal static PostSweepReadiness Project(StartupPlan startupPlan, BeadsReadiness readiness)
    {
        var releasable = startupPlan.Releases.Select(release => release.BeadId).ToFrozenSet(StringComparer.Ordinal);
        var recovered = readiness.RecoverableOwnedReady.Where(releasable.Contains).ToFrozenSet(StringComparer.Ordinal);
        var ready = readiness.NativeUnassignedReady.Concat(recovered).ToFrozenSet(StringComparer.Ordinal);
        return new PostSweepReadiness(readiness.NativeUnassignedReady, readiness.RecoverableOwnedReady, ready, recovered);
    }
}
