using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Application;

internal sealed class StartupRecoveryCoordinator(BeadsReader reader, BeadsWriter writer)
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(30);

    internal async Task RecoverAsync(ValidatedTargetRepository repository, string actor,
        CancellationToken cancellationToken)
    {
        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var plannedReleases = StartupPlanner.Plan(actor, inventory).Releases;
        foreach (var release in plannedReleases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentInventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!currentInventory.ById.TryGetValue(release.BeadId, out var current)
                || !IsExactOwnedClaim(current, actor))
                continue;

            GuardedStrandedClaimReleaseResult result = GuardedStrandedClaimReleaseResult.GuardMismatch;
            Exception? writeFailure = null;
            try
            {
                result = await writer.ReleaseStrandedClaimAsync(repository, release.BeadId, actor, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                writeFailure = exception;
            }

            var observedInventory = await ReadForVerificationAsync(repository).ConfigureAwait(false);
            if (writeFailure is not null)
            {
                if (!observedInventory.ById.TryGetValue(release.BeadId, out var failedWriteObserved))
                    throw Unconfirmed(release.BeadId, writeFailure, "the Bead disappeared during recovery verification");
                throw Unconfirmed(release.BeadId, writeFailure,
                    $"the fresh state is status '{failedWriteObserved.Status}' and assignee '{failedWriteObserved.Assignee ?? "(none)"}'");
            }

            if (!observedInventory.ById.TryGetValue(release.BeadId, out var observed)) continue;

            if (IsReleasedClaim(observed)) continue;

            if (result == GuardedStrandedClaimReleaseResult.GuardMismatch)
            {
                // Status and assignee are the complete guard. Any fresh state is authoritative;
                // do not replay a stale transition even when it still resembles the old claim.
                continue;
            }

            if (!IsExactOwnedClaim(observed, actor))
            {
                // A concurrent actor or terminal/status transition won. Preserve that current state.
                continue;
            }

            throw new ToolExecutionException(
                $"startup recovery for Bead '{release.BeadId}' returned success but the fresh state remains status '{observed.Status}' "
                + $"and assignee '{observed.Assignee ?? "(none)"}'; refusing admission after an unconfirmed release");
        }
    }

    private async Task<BeadsInventory> ReadForVerificationAsync(ValidatedTargetRepository repository)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try
        {
            return await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ToolExecutionException(
                $"startup recovery could not confirm the latest tracker state: {exception.Message}");
        }
    }

    private static bool IsExactOwnedClaim(BeadSnapshot bead, string actor) =>
        bead.Status == "in_progress" && string.Equals(bead.Assignee, actor, StringComparison.Ordinal);

    private static bool IsReleasedClaim(BeadSnapshot bead) => bead.Status == "open" && bead.Assignee is null;

    private static ToolExecutionException Unconfirmed(string beadId, Exception failure, string observed) =>
        new($"startup recovery for Bead '{beadId}' had an uncertain write; fresh inspection found {observed}. "
            + $"Startup stops without replaying the update. Details: {failure.Message}");
}
