using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Execution;

internal delegate Task<OperationalRetryOutcome> MergerRoleRunner(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    string beadId,
    AgentRole role,
    string expectedAssignee,
    BeadWorkflowState expectedState,
    CancellationToken cancellationToken,
    Action<OperationalRetryProgress>? progress);

internal enum MergerSlotDisposition
{
    Merged,
    Escalated,
    Refused,
    Failed,
    Canceled,
    StoppedAtUnsupportedOutcome
}

internal sealed record MergerSlotOutcome(
    MergerSlotDisposition Disposition,
    BeadWorkflowState? FinalState,
    OperationalRetryOutcome? Stage,
    bool ClaimHandoffCompleted,
    string? Diagnostic)
{
    internal bool MergeCompleted => Disposition == MergerSlotDisposition.Merged && ClaimHandoffCompleted;
}

/// <summary>Claims and runs one stage:merge Plan Bead, then accepts only verified closure.</summary>
internal sealed class MergerSlotCoordinator
{
    private static readonly TimeSpan ClaimFinalizationTimeout = TimeSpan.FromSeconds(15);

    private readonly BeadsReader reader;
    private readonly BeadsWriter writer;
    private readonly MergerRoleRunner runRole;

    internal MergerSlotCoordinator(IProcessRunner processRunner, MergerRoleRunner? runRole = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        reader = new BeadsReader(processRunner);
        writer = new BeadsWriter(processRunner);
        if (runRole is null)
        {
            var retries = new OperationalRetryCoordinator(processRunner);
            this.runRole = (paths, project, repository, beadId, role, actor, expectedState, token, progress) =>
                retries.RunAsync(paths, project, repository, beadId, role, actor, token, progress, expectedState);
        }
        else
        {
            this.runRole = runRole;
        }
    }

    internal async Task<MergerSlotOutcome> RunAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(beadId))
            return Result(MergerSlotDisposition.Refused, null, null, false, "Bead ID must be nonblank.");

        BeadSnapshot? claimed = null;
        BeadWorkflowState? initialState = null;
        var claimAcquisitionAttempted = false;
        var claimAcquiredHere = false;
        var executionStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var admission = await reader.ReadSnapshotWithReadinessAsync(repository, project.RecoveryActor,
                cancellationToken).ConfigureAwait(false);
            if (!admission.Inventory.ById.TryGetValue(beadId, out var candidate))
                return Result(MergerSlotDisposition.Refused, null, null, false,
                    $"Bead '{beadId}' was not found in the Target Repository tracker.");

            try { initialState = BeadWorkflowState.Parse(candidate); }
            catch (FormatException exception)
            {
                return Result(MergerSlotDisposition.Refused, null, null, false,
                    $"Bead '{beadId}' has invalid workflow state: {exception.Message}");
            }

            var refusal = ValidateAdmission(candidate, initialState, admission.Readiness, project.RecoveryActor);
            if (refusal is not null)
                return Result(MergerSlotDisposition.Refused, initialState, null, false, refusal);

            var wasAlreadyOwned = candidate.Status == "in_progress";
            if (!wasAlreadyOwned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                claimAcquisitionAttempted = true;
                await writer.AcquireDispatchClaimAsync(repository, beadId, project.RecoveryActor, cancellationToken)
                    .ConfigureAwait(false);
                claimAcquiredHere = true;
            }

            var claimedSnapshot = await reader.ReadSnapshotWithReadinessAsync(repository, project.RecoveryActor,
                cancellationToken).ConfigureAwait(false);
            if (!claimedSnapshot.Inventory.ById.TryGetValue(beadId, out claimed))
                return await RefuseAfterClaimAsync(project, repository, beadId, null,
                    claimAcquiredHere, "Bead disappeared after Merger admission.").ConfigureAwait(false);

            BeadWorkflowState claimedState;
            try { claimedState = BeadWorkflowState.Parse(claimed); }
            catch (FormatException exception)
            {
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    claimAcquiredHere, $"Bead workflow became invalid after Merger admission: {exception.Message}")
                    .ConfigureAwait(false);
            }

            if (claimed.Status != "in_progress"
                || !string.Equals(claimed.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    $"Guarded claim verification failed: observed status '{claimed.Status}' and assignee '{claimed.Assignee ?? "(none)"}'.")
                    .ConfigureAwait(false);

            var expectedClaimedState = wasAlreadyOwned ? initialState : initialState with { Status = "in_progress" };
            if (claimedState != expectedClaimedState)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    "Bead type, stage, pause labels, or repair_cycles changed during claim acquisition.")
                    .ConfigureAwait(false);
            if (!claimedSnapshot.Readiness.RecoverableOwnedReady.Contains(beadId))
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    "Bead is no longer in bd's recoverable-owned ready set after Merger admission; it may have become blocked or deferred.")
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            executionStarted = true;
            OperationalRetryOutcome stage;
            try
            {
                stage = await runRole(paths, project, repository, beadId, AgentRole.Merger,
                    project.RecoveryActor, claimedState, cancellationToken, progress: null).ConfigureAwait(false);
            }
            catch (OperationalRetryEscalationIncompleteException exception)
            {
                return Result(exception.InnerException is OperationCanceledException
                        ? MergerSlotDisposition.Canceled : MergerSlotDisposition.Failed,
                    exception.Outcome.StageResult.VerifiedState ?? claimedState, exception.Outcome, false,
                    exception.Message);
            }
            catch (NeedsHumanEscalationException exception)
            {
                return Result(MergerSlotDisposition.Failed, claimedState, null,
                    exception.Progress.ClaimReleased, exception.Message);
            }

            if (stage.Status == OperationalRetryStatus.Escalated)
            {
                var completed = stage.Escalation is { Succeeded: true, ClaimReleased: true };
                return Result(completed ? MergerSlotDisposition.Escalated : MergerSlotDisposition.Failed,
                    null, stage, completed,
                    completed ? null : "Operational retry escalation returned without a verified claim handoff.");
            }

            if (stage.StageResult.Role != AgentRole.Merger || stage.StageResult.BeadId != beadId)
                return Result(MergerSlotDisposition.Failed, claimedState, stage, false,
                    "The operational retry coordinator returned a result for a different role or Bead.");
            var verified = stage.StageResult.VerifiedState;
            if (!stage.Succeeded || !stage.StageResult.Succeeded || verified is null)
                return Result(MergerSlotDisposition.Failed, claimedState, stage, false,
                    "Merger returned no verified successful workflow state.");

            if (verified.Stage == "stage:implement" && verified.Status is "open" or "in_progress")
            {
                var rejectionSnapshot = await reader.ReadSnapshotWithReadinessAsync(repository, project.RecoveryActor,
                    cancellationToken).ConfigureAwait(false);
                if (!rejectionSnapshot.Inventory.ById.TryGetValue(beadId, out var rejectedBead))
                    return Result(MergerSlotDisposition.Failed, verified, stage, false,
                        "Rejected Plan Bead disappeared after post-exit verification; its claim and worktree were left untouched.");
                BeadWorkflowState rejectedState;
                try { rejectedState = BeadWorkflowState.Parse(rejectedBead); }
                catch (FormatException exception)
                {
                    return Result(MergerSlotDisposition.Failed, null, stage, false,
                        $"Rejected Bead workflow became invalid after post-exit verification: {exception.Message}");
                }
                if (rejectedState != verified || rejectedBead.Status != "in_progress"
                    || !string.Equals(rejectedBead.Assignee, project.RecoveryActor, StringComparison.Ordinal)
                    || !rejectionSnapshot.Readiness.RecoverableOwnedReady.Contains(beadId))
                    return Result(MergerSlotDisposition.Failed, rejectedState, stage, false,
                        "Semantic rejection changed or lost its owned ready state after verification; claim and worktree were left untouched.");
                return Result(MergerSlotDisposition.StoppedAtUnsupportedOutcome, rejectedState, stage, false,
                    "Merger reported a semantic rejection. Repair handoff is not implemented in this slot; its claim and Delivery worktree are retained.");
            }

            if (verified.Status != "closed" || verified.Stage is not (null or "stage:merge")
                || verified.Planned || verified.ProductDecisionRequired || verified.NeedsHuman
                || verified.RepairCycles != claimedState.RepairCycles)
                return Result(MergerSlotDisposition.Failed, verified, stage, false,
                    "Verified Merger state was not a closed stage:merge Plan Bead with unchanged repair_cycles.");

            return await ReleaseClosedClaimAsync(repository, beadId, project.RecoveryActor, verified, stage,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (claimAcquiredHere && !executionStarted)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    "Merger slot was canceled before Agent execution.").ConfigureAwait(false);
            if (claimAcquisitionAttempted && !claimAcquiredHere)
                return Result(MergerSlotDisposition.Canceled, initialState, null, false,
                    "Claim acquisition was canceled before ownership was confirmed; ownership is uncertain and no release was attempted.");
            return Result(MergerSlotDisposition.Canceled, null, null, false,
                executionStarted
                    ? "Merger execution was canceled; no blind claim release was attempted."
                    : "Merger slot was canceled before claim acquisition completed.");
        }
        catch (Exception exception)
        {
            if (claimAcquiredHere && !executionStarted)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    $"Merger admission failed: {exception.Message}").ConfigureAwait(false);
            if (claimAcquisitionAttempted && !claimAcquiredHere)
                return Result(MergerSlotDisposition.Refused, initialState, null, false,
                    $"Guarded claim acquisition failed before ownership was confirmed; no release was attempted: {exception.Message}");
            return Result(MergerSlotDisposition.Failed, null, null, false,
                executionStarted
                    ? $"Merger execution or closure verification failed; the claim was left untouched: {exception.Message}"
                    : $"Merger slot failed before claim acquisition: {exception.Message}");
        }
    }

    internal static string? ValidateAdmission(BeadSnapshot bead, BeadWorkflowState state,
        BeadsReadiness readiness, string recoveryActor)
    {
        if (bead.IssueType != "task")
            return $"Merger accepts Plan Beads of type task; '{bead.Id}' is '{bead.IssueType}'.";
        var stages = StageLabelParser.Parse(bead.Labels);
        if (stages.Count != 1 || stages[0] != "stage:merge" || state.Stage != "stage:merge")
            return $"Merger requires exactly one stage label, stage:merge; observed '{string.Join(", ", stages)}'.";
        if (state.Planned || state.ProductDecisionRequired || state.NeedsHuman)
            return $"Bead '{bead.Id}' is planned or paused and cannot enter the Merger slot.";

        if (bead.Status == "open" && bead.Assignee is null)
            return readiness.NativeUnassignedReady.Contains(bead.Id)
                ? null
                : $"Bead '{bead.Id}' is not in bd's native unassigned ready set; it may be blocked or deferred.";
        if (bead.Status == "in_progress" && string.Equals(bead.Assignee, recoveryActor, StringComparison.Ordinal))
            return readiness.RecoverableOwnedReady.Contains(bead.Id)
                ? null
                : $"Bead '{bead.Id}' is not in bd's recoverable-owned ready set.";
        return $"Bead '{bead.Id}' must be open and unassigned or recoverable under '{recoveryActor}'; observed status '{bead.Status}' and assignee '{bead.Assignee ?? "(none)"}'.";
    }

    private async Task<MergerSlotOutcome> ReleaseClosedClaimAsync(ValidatedTargetRepository repository, string beadId,
        string actor, BeadWorkflowState verifiedState, OperationalRetryOutcome stage, CancellationToken cancellationToken)
    {
        try
        {
            var current = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!current.ById.TryGetValue(beadId, out var bead))
                return Result(MergerSlotDisposition.Failed, verifiedState, stage, false,
                    "Closed Plan Bead disappeared before claim handoff; its execution evidence was retained.");
            var state = BeadWorkflowState.Parse(bead);
            if (state != verifiedState || bead.Status != "closed"
                || !string.Equals(bead.Assignee, actor, StringComparison.Ordinal))
                return Result(MergerSlotDisposition.Failed, state, stage, false,
                    "Closed claim handoff preflight found stale workflow state or changed ownership; the claim was left untouched.");

            await writer.ReleaseDispatchClaimAsync(repository, beadId, actor, "closed", cancellationToken)
                .ConfigureAwait(false);
            var released = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!released.ById.TryGetValue(beadId, out var finalBead))
                return Result(MergerSlotDisposition.Failed, verifiedState, stage, false,
                    "Closed Plan Bead disappeared after guarded claim handoff.");
            var finalState = BeadWorkflowState.Parse(finalBead);
            if (finalBead.Status != "closed" || finalBead.Assignee is not null
                || finalState != verifiedState)
                return Result(MergerSlotDisposition.Failed, finalState, stage, false,
                    "Guarded Merger claim handoff did not verify as closed, unassigned, and unchanged.");
            return Result(MergerSlotDisposition.Merged, finalState, stage, true, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(MergerSlotDisposition.Failed, verifiedState, stage, false,
                $"Verified merge closure claim handoff failed: {exception.Message}");
        }
    }

    private async Task<MergerSlotOutcome> RefuseAfterClaimAsync(ProjectConfig project,
        ValidatedTargetRepository repository, string beadId, BeadSnapshot? claimed, bool claimAcquiredHere, string reason)
    {
        if (!claimAcquiredHere)
            return Result(MergerSlotDisposition.Refused,
                claimed is null ? null : BeadWorkflowState.Parse(claimed), null, false, reason);

        string? releaseFailure = null;
        using var timeout = new CancellationTokenSource(ClaimFinalizationTimeout);
        if (claimed is null)
        {
            try
            {
                var fresh = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                fresh.ById.TryGetValue(beadId, out claimed);
            }
            catch (Exception exception) { releaseFailure = $"claim owner could not be reread: {exception.Message}"; }
        }

        if (releaseFailure is null && claimed is not null
            && string.Equals(claimed.Assignee, project.RecoveryActor, StringComparison.Ordinal)
            && claimed.Status is "open" or "in_progress" or "closed")
        {
            try
            {
                var expectedStatus = claimed.Status;
                await writer.ReleaseDispatchClaimAsync(repository, beadId, project.RecoveryActor, expectedStatus, timeout.Token)
                    .ConfigureAwait(false);
                var released = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                var expectedReleasedStatus = expectedStatus == "in_progress" ? "open" : expectedStatus;
                if (!released.ById.TryGetValue(beadId, out var finalBead) || finalBead.Assignee is not null
                    || finalBead.Status != expectedReleasedStatus)
                    releaseFailure = "guarded claim release did not verify against the fresh Beads inventory";
            }
            catch (Exception exception) { releaseFailure = exception.Message; }
        }
        else if (releaseFailure is null && claimed is null)
        {
            releaseFailure = "the Bead is missing; claim ownership could not be verified for release";
        }
        else if (releaseFailure is null)
        {
            releaseFailure = $"claim is no longer owned by '{project.RecoveryActor}' (observed '{claimed?.Assignee ?? "(none)"}'); it was left untouched";
        }

        return Result(MergerSlotDisposition.Refused,
            claimed is null ? null : BeadWorkflowState.Parse(claimed), null,
            releaseFailure is null, releaseFailure is null ? reason : $"{reason} Claim release: {releaseFailure}.");
    }

    private static MergerSlotOutcome Result(MergerSlotDisposition disposition, BeadWorkflowState? state,
        OperationalRetryOutcome? stage, bool handoff, string? diagnostic) =>
        new(disposition, state, stage, handoff, diagnostic);
}
