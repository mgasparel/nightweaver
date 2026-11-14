using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum PlanningSlotDisposition { Published, Paused, Refused, Interrupted, Failed }
internal sealed record PlanningSlotOutcome(PlanningSlotDisposition Disposition, BeadWorkflowState? FinalState,
    bool ClaimHandoffCompleted, string? Diagnostic);

/// <summary>Fresh guarded admission and finalization around the reusable claimed Planning workflow.</summary>
internal sealed class PlanningSlotCoordinator
{
    private static readonly TimeSpan ClaimFinalizationTimeout = TimeSpan.FromSeconds(15);
    private readonly BeadsReader reader;
    private readonly BeadsWriter writer;
    private readonly PlanningCoordinator coordinator;

    internal PlanningSlotCoordinator(IProcessRunner runner, DeliveryRoleRunner? runRole = null)
    {
        reader = new(runner);
        writer = new(runner);
        coordinator = new(runner, runRole);
    }

    internal async Task<PlanningSlotOutcome> RunAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken,
        Action<OperationalRetryProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(beadId))
            return Result(PlanningSlotDisposition.Refused, null, false, "Bead ID must be nonblank.");

        BeadWorkflowState? initial = null;
        BeadSnapshot? claimed = null;
        var claimAcquisitionAttempted = false;
        var claimAcquired = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var admissionSnapshot = await reader.ReadSnapshotWithReadinessAsync(repository, project.RecoveryActor,
                cancellationToken).ConfigureAwait(false);
            var inventory = admissionSnapshot.Inventory;
            var readiness = admissionSnapshot.Readiness;
            if (!inventory.ById.TryGetValue(beadId, out var candidate))
                return Result(PlanningSlotDisposition.Refused, null, false,
                    $"Bead '{beadId}' was not found in the Target Repository tracker.");

            try { initial = BeadWorkflowState.Parse(candidate); }
            catch (FormatException exception)
            {
                return Result(PlanningSlotDisposition.Refused, null, false,
                    $"Bead '{beadId}' has invalid workflow state: {exception.Message}");
            }

            var refusal = ValidateAdmission(candidate, initial, readiness);
            if (refusal is not null)
                return Result(PlanningSlotDisposition.Refused, initial, false, refusal);

            cancellationToken.ThrowIfCancellationRequested();
            claimAcquisitionAttempted = true;
            if (!ContinuousAgentLaunchContext.TryBeginExternalOperation(
                    () => writer.AcquirePlanningDispatchClaimAsync(repository, beadId, project.RecoveryActor, cancellationToken),
                    out var claimAcquisition))
                return Result(PlanningSlotDisposition.Refused, initial, false,
                    "The continuous runtime stopped before guarded claim acquisition began; the candidate remains unclaimed.");

            Exception? acquisitionFailure = null;
            try
            {
                await claimAcquisition.ConfigureAwait(false);
            }
            catch (BeadsGuardConflictException exception)
            {
                // Exit 13 is an authoritative no-write result. In particular, do not treat a
                // same-actor claim as ours or release a same-actor competitor after this guard.
                return Result(PlanningSlotDisposition.Refused, initial, false, exception.Message);
            }
            catch (Exception exception)
            {
                acquisitionFailure = exception;
            }

            if (acquisitionFailure is not null)
            {
                using var reconciliation = new CancellationTokenSource(ClaimFinalizationTimeout);
                BeadSnapshot? observed;
                try
                {
                    var fresh = await ReadFinalizationSnapshotAsync(repository, reconciliation.Token).ConfigureAwait(false);
                    fresh.ById.TryGetValue(beadId, out observed);
                }
                catch (Exception exception)
                {
                    return Result(PlanningSlotDisposition.Failed, initial, false,
                        $"Guarded claim acquisition returned an uncertain result ({acquisitionFailure.Message}); bounded ownership reread failed: {exception.Message}. No release was attempted.");
                }

                if (observed is null)
                    return Result(PlanningSlotDisposition.Refused, initial, false,
                        $"Guarded claim acquisition returned an uncertain result ({acquisitionFailure.Message}); the Bead was absent during bounded ownership reconciliation, so no claim is currently owned by this runtime. No release was attempted.");

                if (observed.Status != "in_progress"
                    || !string.Equals(observed.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                    return Result(PlanningSlotDisposition.Refused, initial, false,
                        $"Guarded claim acquisition returned an uncertain result ({acquisitionFailure.Message}); bounded reread found no claim owned by this runtime (status '{observed.Status}', assignee '{observed.Assignee ?? "(none)"}'). No release was attempted.");

                BeadWorkflowState observedState;
                try { observedState = BeadWorkflowState.Parse(observed); }
                catch (FormatException exception)
                {
                    return Result(PlanningSlotDisposition.Failed, initial, false,
                        $"Guarded claim acquisition returned an uncertain result ({acquisitionFailure.Message}); ownership reread contained invalid workflow state: {exception.Message}. No release was attempted.");
                }

                if (observedState != (initial with { Status = "in_progress" }))
                    return Result(PlanningSlotDisposition.Failed, observedState, false,
                        $"Guarded claim acquisition returned an uncertain result ({acquisitionFailure.Message}); bounded reread found changed or unconfirmed ownership/workflow (status '{observed.Status}', assignee '{observed.Assignee ?? "(none)"}'). The claim was left untouched.");

                claimed = observed;
            }
            claimAcquired = true;

            var claimedSnapshot = await reader.ReadSnapshotWithReadinessAsync(repository, project.RecoveryActor,
                cancellationToken).ConfigureAwait(false);
            var claimedInventory = claimedSnapshot.Inventory;
            var claimedReadiness = claimedSnapshot.Readiness;
            if (!claimedInventory.ById.TryGetValue(beadId, out claimed))
                return await RefuseAfterClaimAsync(project, repository, beadId,
                    "Bead disappeared after guarded claim acquisition.", null).ConfigureAwait(false);

            BeadWorkflowState claimedState;
            try { claimedState = BeadWorkflowState.Parse(claimed); }
            catch (FormatException exception)
            {
                return await RefuseAfterClaimAsync(project, repository, beadId,
                    $"Bead workflow became invalid after claim acquisition: {exception.Message}", null).ConfigureAwait(false);
            }

            if (claimed.Status != "in_progress"
                || !string.Equals(claimed.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                return await RefuseAfterClaimAsync(project, repository, beadId,
                    $"Guarded claim verification failed: observed status '{claimed.Status}' and assignee '{claimed.Assignee ?? "(none)"}'.",
                    claimedState).ConfigureAwait(false);
            if (claimedState != (initial with { Status = "in_progress" }))
                return await RefuseAfterClaimAsync(project, repository, beadId,
                    "Bead type, stage, pause labels, or repair_cycles changed during claim acquisition.", claimedState)
                    .ConfigureAwait(false);
            if (!claimedReadiness.RecoverableOwnedReady.Contains(beadId))
                return await RefuseAfterClaimAsync(project, repository, beadId,
                    "Bead is no longer in bd's recoverable-ready set after claim acquisition; it may have become blocked or deferred.",
                    claimedState).ConfigureAwait(false);

            var result = await coordinator.RunClaimedAsync(paths, project, repository, beadId, claimedState,
                cancellationToken, progress).ConfigureAwait(false);
            // These receipts end outgoing ownership: never reread or release after confirmed handoff.
            if (result?.StageResult.PublicationReceipt is { ClaimReleased: true } receipt)
            {
                var disposition = receipt.PublishedState.Planned ? PlanningSlotDisposition.Published
                    : receipt.PublishedState.ProductDecisionRequired || receipt.PublishedState.NeedsHuman
                        ? PlanningSlotDisposition.Paused : PlanningSlotDisposition.Failed;
                return Result(disposition, receipt.PublishedState, true,
                    disposition == PlanningSlotDisposition.Failed ? "Planning returned an unexpected released workflow; no outgoing operation followed handoff." : null);
            }
            if (result?.Escalation is { Succeeded: true, ClaimReleased: true })
                return Result(PlanningSlotDisposition.Paused, result.StageResult.VerifiedState, true, null);
            var finalized = await RefuseAfterClaimAsync(project, repository, beadId,
                ContinuousAgentLaunchContext.StopRequested ? "Planning stopped between turns; retained work is recoverable."
                    : "Planning ended without verified publication or pause.", null).ConfigureAwait(false);
            return finalized with { Disposition = finalized.ClaimHandoffCompleted && ContinuousAgentLaunchContext.StopRequested
                && (result is null || result.Succeeded || result.AttemptCount == 0) ? PlanningSlotDisposition.Interrupted : PlanningSlotDisposition.Failed };
        }
        catch (Exception exception)
        {
            if (ContinuousAgentLaunchContext.ForceStopRequested)
                return Result(PlanningSlotDisposition.Failed, initial, false, exception.Message);
            // Escalation may already have handed ownership away before an exception was reported.
            if (exception is OperationalRetryEscalationIncompleteException { Progress: { ClaimReleased: true } }
                || exception is NeedsHumanEscalationException { Progress: { ClaimReleased: true } })
                return Result(PlanningSlotDisposition.Failed, initial, true, exception.Message);
            if (!claimAcquired)
                return Result(PlanningSlotDisposition.Failed, initial, false,
                    $"{exception.Message} Claim acquisition attempted: {claimAcquisitionAttempted}; no unconfirmed claim was released.");
            var finalized = await RefuseAfterClaimAsync(project, repository, beadId, exception.Message, null)
                .ConfigureAwait(false);
            return finalized with { Disposition = PlanningSlotDisposition.Failed };
        }
    }

    private async Task<PlanningSlotOutcome> RefuseAfterClaimAsync(ProjectConfig project,
        ValidatedTargetRepository repository, string beadId, string reason, BeadWorkflowState? state)
    {
        if (ContinuousAgentLaunchContext.ForceStopRequested)
            return Result(PlanningSlotDisposition.Failed, state, false, reason);
        using var timeout = new CancellationTokenSource(ClaimFinalizationTimeout);
        try
        {
            // Always reread before release, including late pause/readiness changes after acquisition.
            var inventory = await ReadFinalizationSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
            if (!inventory.ById.TryGetValue(beadId, out var claimed)
                || claimed.Status != "in_progress" || claimed.Assignee != project.RecoveryActor)
                return Result(PlanningSlotDisposition.Failed, state, false,
                    reason + " Ownership changed or is unobservable; the claim was left untouched.");
            var expected = BeadWorkflowState.Parse(claimed) with { Status = "open" };
            if (!ContinuousAgentLaunchContext.TryBeginFinalization(
                    () => writer.ReleasePlanningClaimAsync(repository, beadId, project.RecoveryActor, timeout.Token), out var release))
                return Result(PlanningSlotDisposition.Failed, state, false, "Force stop prevented claim finalization.");
            var released = await release.ConfigureAwait(false);
            // A structured confirmed response is the handoff barrier. Only an uncertain response may reread.
            if (released is null)
            {
                var fresh = await ReadFinalizationSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                fresh.ById.TryGetValue(beadId, out released);
            }
            if (released?.Assignee is null && released is not null && BeadWorkflowState.Parse(released) == expected)
                return Result(PlanningSlotDisposition.Refused, expected, true, reason);
            return Result(PlanningSlotDisposition.Failed, state, false, reason + " Guarded claim release was not confirmed.");
        }
        catch (Exception exception)
        {
            return Result(PlanningSlotDisposition.Failed, state, false, reason + " Claim finalization: " + exception.Message);
        }
    }

    private async Task<BeadsInventory> ReadFinalizationSnapshotAsync(ValidatedTargetRepository repository, CancellationToken token)
    {
        if (!ContinuousAgentLaunchContext.TryBeginFinalization(() => reader.ReadSnapshotAsync(repository, token), out var read))
            throw new OperationCanceledException("Force stop prevented a new finalization read.");
        return await read.ConfigureAwait(false);
    }

    internal static string? ValidateAdmission(BeadSnapshot bead, BeadWorkflowState state, BeadsReadiness readiness)
    {
        if (bead.Status != "open" || bead.Assignee is not null)
            return "Planning requires an open, unassigned Product.";
        if (bead.IssueType is not ("feature" or "bug")) return "Planning requires a Product Bead.";
        if (!readiness.NativeUnassignedReady.Contains(bead.Id)) return "Product is not natively ready.";
        if (!AuthorityMatrix.TryInferLifecycleRole(state, out var role, out var reason)) return reason;
        return AuthorityMatrix.ValidateInput(role, state);
    }

    private static PlanningSlotOutcome Result(PlanningSlotDisposition disposition, BeadWorkflowState? state,
        bool handoff, string? diagnostic) => new(disposition, state, handoff, diagnostic);
}
