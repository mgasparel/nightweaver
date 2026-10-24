using System.Collections.Frozen;

namespace Nightweaver.Inspection;

internal sealed record BeadsReadiness(
    IReadOnlySet<string> NativeUnassignedReady,
    IReadOnlySet<string> RecoverableOwnedReady)
{
    internal static BeadsReadiness Create(IEnumerable<string> nativeUnassignedReady, IEnumerable<string> recoverableOwnedReady) =>
        new(nativeUnassignedReady.ToFrozenSet(StringComparer.Ordinal), recoverableOwnedReady.ToFrozenSet(StringComparer.Ordinal));
}
