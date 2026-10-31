using System.Collections.Frozen;
using Nightweaver.Inspection;

namespace Nightweaver.Planning;

internal sealed record PostSweepSnapshot(BeadsInventory Inventory, PostSweepReadiness Readiness);

internal sealed record PostSweepReadiness(
    IReadOnlySet<string> NativeUnassignedReady,
    IReadOnlySet<string> RecoverableOwnedReady,
    IReadOnlySet<string> ReadyBeadIds,
    IReadOnlySet<string> RecoveredReadyBeadIds);

internal static class PostSweepReadinessProjector
{
    internal static PostSweepSnapshot Project(StartupPlan startupPlan, BeadsReadiness readiness)
    {
        var paused = startupPlan.Inventory.ById.Values
            .Where(bead => bead.Labels.Contains("needs-human", StringComparer.Ordinal)
                || bead.Labels.Contains("product-decision-required", StringComparer.Ordinal))
            .Select(bead => bead.Id).ToFrozenSet(StringComparer.Ordinal);
        var blocked = startupPlan.Diagnostics
            .Where(item => item.Code is StartupDiagnosticCode.UnsafeRuntimePath or StartupDiagnosticCode.UnusableRuntimePath)
            .Select(item => item.BeadId).Where(id => id.Length > 0).ToFrozenSet(StringComparer.Ordinal);
        var sharedUnsafeRoot = startupPlan.Diagnostics.Any(item => item.BeadId.Length == 0
            && item.Code is StartupDiagnosticCode.UnsafeRuntimePath or StartupDiagnosticCode.UnusableRuntimePath);
        var releasable = startupPlan.Releases.Select(release => release.BeadId).ToFrozenSet(StringComparer.Ordinal);
        bool Eligible(string id) => !sharedUnsafeRoot && !paused.Contains(id) && !blocked.Contains(id);
        var native = readiness.NativeUnassignedReady.Where(Eligible).ToFrozenSet(StringComparer.Ordinal);
        var recoverable = readiness.RecoverableOwnedReady.Where(Eligible).ToFrozenSet(StringComparer.Ordinal);
        var recovered = recoverable.Where(releasable.Contains).ToFrozenSet(StringComparer.Ordinal);
        var ready = native.Concat(recovered).ToFrozenSet(StringComparer.Ordinal);
        return new PostSweepSnapshot(startupPlan.Inventory,
            new PostSweepReadiness(native, recoverable, ready, recovered));
    }
}
