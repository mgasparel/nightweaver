using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal delegate Task<OperationalRetryOutcome> DeliveryRoleRunner(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    string beadId,
    AgentRole role,
    string expectedAssignee,
    BeadWorkflowState expectedState,
    CancellationToken cancellationToken,
    Action<OperationalRetryProgress>? progress);

internal delegate Task<NeedsHumanEscalationResult> DeliveryRepairBudgetEscalator(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    NeedsHumanRequest request,
    CancellationToken cancellationToken);

internal enum DeliverySlotDisposition
{
    MergeReady,
    Escalated,
    Refused,
    Failed,
    StoppedAtUnsupportedOutcome
}

internal sealed record DeliverySlotOutcome(
    DeliverySlotDisposition Disposition,
    BeadWorkflowState? FinalState,
    AgentRole? LastRole,
    OperationalRetryOutcome? LastStage,
    bool ClaimHandoffCompleted,
    string? Diagnostic,
    NeedsHumanEscalationResult? EscalationProgress = null)
{
    internal bool MergeReady => Disposition == DeliverySlotDisposition.MergeReady && ClaimHandoffCompleted;
    internal bool ClaimReleased => EscalationProgress?.ClaimReleased ?? ClaimHandoffCompleted;
}

/// <summary>Runs Delivery and verified Reviewer/QA repair cycles under one guarded claim.</summary>
internal sealed class DeliverySlotCoordinator
{
    private static readonly TimeSpan ClaimFinalizationTimeout = TimeSpan.FromSeconds(15);

    private readonly BeadsReader reader;
    private readonly BeadsWriter writer;
    private readonly DeliveryRoleRunner runRole;
    private readonly DeliveryRepairBudgetEscalator escalateRepairBudget;

    internal DeliverySlotCoordinator(IProcessRunner processRunner, DeliveryRoleRunner? runRole = null,
        DeliveryRepairBudgetEscalator? escalateRepairBudget = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        reader = new BeadsReader(processRunner);
        writer = new BeadsWriter(processRunner);
        var escalator = new NeedsHumanEscalator(processRunner);
        this.escalateRepairBudget = escalateRepairBudget ?? escalator.EscalateAsync;
        if (runRole is null)
        {
            var retries = new OperationalRetryCoordinator(processRunner);
            this.runRole = (hostPaths, config, target, id, role, actor, expectedState, token, stageProgress) =>
                retries.RunAsync(hostPaths, config, target, id, role, actor, token, stageProgress, expectedState);
        }
        else
        {
            this.runRole = runRole;
        }
    }

    internal async Task<DeliverySlotOutcome> RunAsync(
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
            return Result(DeliverySlotDisposition.Refused, null, null, null, false, "Bead ID must be nonblank.");

        BeadWorkflowState? initial = null;
        BeadSnapshot? claimed = null;
        var claimAcquisitionAttempted = false;
        var claimAcquired = false;
        var executionStarted = false;
        AgentRole? lastRole = null;
        OperationalRetryOutcome? lastStage = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            var readiness = await reader.ReadReadinessAsync(repository, inventory, project.RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            if (!inventory.ById.TryGetValue(beadId, out var candidate))
                return Result(DeliverySlotDisposition.Refused, null, null, null, false,
                    $"Bead '{beadId}' was not found in the Target Repository tracker.");

            try { initial = BeadWorkflowState.Parse(candidate); }
            catch (FormatException exception)
            {
                return Result(DeliverySlotDisposition.Refused, null, null, null, false,
                    $"Bead '{beadId}' has invalid workflow state: {exception.Message}");
            }

            var refusal = ValidateAdmission(candidate, initial, readiness);
            if (refusal is not null)
                return Result(DeliverySlotDisposition.Refused, initial, null, null, false, refusal);

            cancellationToken.ThrowIfCancellationRequested();
            claimAcquisitionAttempted = true;
            await writer.AcquireDispatchClaimAsync(repository, beadId, project.RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            claimAcquired = true;

            var claimedInventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            var claimedReadiness = await reader.ReadReadinessAsync(repository, claimedInventory, project.RecoveryActor,
                cancellationToken).ConfigureAwait(false);
            if (!claimedInventory.ById.TryGetValue(beadId, out claimed))
                return await RefuseAfterClaimAsync(project, repository, beadId, null,
                    "Bead disappeared after guarded claim acquisition.", null).ConfigureAwait(false);

            BeadWorkflowState claimedState;
            try { claimedState = BeadWorkflowState.Parse(claimed); }
            catch (FormatException exception)
            {
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    $"Bead workflow became invalid after claim acquisition: {exception.Message}", null).ConfigureAwait(false);
            }

            if (claimed.Status != "in_progress"
                || !string.Equals(claimed.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    $"Guarded claim verification failed: observed status '{claimed.Status}' and assignee '{claimed.Assignee ?? "(none)"}'.",
                    claimedState).ConfigureAwait(false);
            if (claimedState != (initial with { Status = "in_progress" }))
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    "Bead type, stage, pause labels, or repair_cycles changed during claim acquisition.", claimedState)
                    .ConfigureAwait(false);
            if (!claimedReadiness.RecoverableOwnedReady.Contains(beadId))
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    "Bead is no longer in bd's recoverable-ready set after claim acquisition; it may have become blocked or deferred.",
                    claimedState).ConfigureAwait(false);

            var currentState = claimedState;
            if (currentState.RepairCycles > project.Limits.MaxRepairCycles)
                return await EscalateRepairBudgetAsync(paths, project, repository, beadId, currentState,
                    InferLifecycleRole(currentState), null, persistedAtAdmission: true, cancellationToken,
                    rejectingStage: currentState.Stage).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TrySelectRole(currentState, out var role))
                    return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, currentState, lastRole, lastStage,
                        false, $"Delivery slot cannot continue from verified stage '{currentState.Stage ?? "(none)"}'.");

                lastRole = role;
                executionStarted = true;
                lastStage = null;
                try
                {
                    lastStage = await runRole(paths, project, repository, beadId, role, project.RecoveryActor,
                        currentState, cancellationToken, progress).ConfigureAwait(false);
                }
                catch (OperationalRetryEscalationIncompleteException exception)
                {
                    lastStage = exception.Outcome;
                    var disposition = exception.InnerException is OperationCanceledException
                        ? DeliverySlotDisposition.StoppedAtUnsupportedOutcome
                        : DeliverySlotDisposition.Failed;
                    return Result(disposition,
                        exception.Outcome.StageResult.VerifiedState ?? currentState, role, lastStage, false,
                        exception.Message, exception.Progress);
                }
                catch (NeedsHumanEscalationException exception)
                {
                    return Result(DeliverySlotDisposition.Failed, currentState, role, null, false,
                        exception.Message, exception.Progress);
                }
                catch (OperationCanceledException exception) when (TryGetEscalationProgress(exception, out var escalation))
                {
                    return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, currentState, role, null, false,
                        exception.Message, escalation);
                }

                if (lastStage.Status == OperationalRetryStatus.Escalated)
                {
                    var escalation = lastStage.Escalation;
                    var handoff = escalation is { Succeeded: true, ClaimReleased: true };
                    return Result(DeliverySlotDisposition.Escalated, null, role, lastStage, handoff,
                        handoff ? null : "Operational retry escalation returned without a verified claim handoff.");
                }

                if (lastStage.StageResult.Role != role || lastStage.StageResult.BeadId != beadId)
                    return Result(DeliverySlotDisposition.Failed, currentState, role, lastStage, false,
                        "The operational retry coordinator returned a result for a different role or Bead.");

                var verified = lastStage.StageResult.VerifiedState;
                if (!lastStage.Succeeded || !lastStage.StageResult.Succeeded || verified is null)
                    return Result(DeliverySlotDisposition.Failed, currentState, role, lastStage, false,
                        $"The {AgentRoles.ToConfigKey(role)} stage returned no verified successful workflow state.");

                var afterInventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
                if (!afterInventory.ById.TryGetValue(beadId, out var afterBead))
                    return Result(DeliverySlotDisposition.Failed, verified, role, lastStage, false,
                        "Bead disappeared after stage verification; its claim and execution evidence were left untouched.");

                BeadWorkflowState after;
                try { after = BeadWorkflowState.Parse(afterBead); }
                catch (FormatException exception)
                {
                    return Result(DeliverySlotDisposition.Failed, null, role, lastStage, false,
                        $"Bead workflow became invalid after stage verification: {exception.Message}");
                }

                if (!string.Equals(afterBead.Assignee, project.RecoveryActor, StringComparison.Ordinal)
                    || afterBead.Status != "in_progress")
                    return Result(DeliverySlotDisposition.Failed, after, role, lastStage, false,
                        $"Claim ownership changed after stage verification; observed status '{afterBead.Status}' and assignee '{afterBead.Assignee ?? "(none)"}'. The claim was left untouched.");
                if (after != verified)
                    return Result(DeliverySlotDisposition.Failed, after, role, lastStage, false,
                        "Bead workflow changed after stage verification; refusing follow-on dispatch or claim handoff.");

                var repairRejection = IsReviewQaRejection(role, currentState, after);
                if (!repairRejection && IsUnsupportedLegalOutcome(role, currentState, after))
                    return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, after, role, lastStage, false,
                        $"The {AgentRoles.ToConfigKey(role)} stage produced a legal repair or product-decision outcome; the slot stopped with its claim and worktree retained.");

                if (role == AgentRole.Qa && after.Stage == "stage:merge")
                {
                    return await ReleaseMergeReadyClaimAsync(repository, beadId, project.RecoveryActor, after,
                        role, lastStage, cancellationToken).ConfigureAwait(false);
                }

                if (repairRejection)
                {
                    var findingContext = $"Verified {AgentRoles.ToConfigKey(role)} rejection: {currentState.Stage ?? "(none)"} -> {after.Stage ?? "(none)"}, repair_cycles={after.RepairCycles}";
                    var latestFindings = await ReadLatestFindingsAsync(repository, beadId, lastStage.StageResult,
                        findingContext, cancellationToken).ConfigureAwait(false);
                    if (after.RepairCycles > project.Limits.MaxRepairCycles)
                        return await EscalateRepairBudgetAsync(paths, project, repository, beadId, after, role,
                            lastStage, persistedAtAdmission: false, cancellationToken, latestFindings,
                            rejectingStage: currentState.Stage).ConfigureAwait(false);
                }

                var expectedNext = repairRejection ? AgentRole.Implementer : role switch
                {
                    AgentRole.Implementer => AgentRole.Reviewer,
                    AgentRole.Reviewer => AgentRole.Qa,
                    _ => (AgentRole?)null
                };
                if (expectedNext is null || !IsRoleInput(expectedNext.Value, after))
                    return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, after, role, lastStage, false,
                        $"The {AgentRoles.ToConfigKey(role)} stage reached unsupported verified state '{after.Stage ?? "(none)"}'; the slot stopped without releasing its claim.");

                var nextReadiness = await reader.ReadReadinessAsync(repository, afterInventory, project.RecoveryActor,
                    cancellationToken).ConfigureAwait(false);
                if (!nextReadiness.RecoverableOwnedReady.Contains(beadId))
                    return Result(DeliverySlotDisposition.Failed, after, role, lastStage, false,
                        "Bead is no longer in bd's recoverable-owned ready set before the next role; no follow-on Agent was launched.");

                var preDispatchInventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
                if (!preDispatchInventory.ById.TryGetValue(beadId, out var preDispatchBead))
                    return Result(DeliverySlotDisposition.Failed, after, role, lastStage, false,
                        "Bead disappeared after interstage readiness verification; no follow-on Agent was launched.");

                BeadWorkflowState preDispatchState;
                try { preDispatchState = BeadWorkflowState.Parse(preDispatchBead); }
                catch (FormatException exception)
                {
                    return Result(DeliverySlotDisposition.Failed, null, role, lastStage, false,
                        $"Bead workflow became invalid before the next role: {exception.Message}");
                }

                if (preDispatchBead.Status != "in_progress"
                    || !string.Equals(preDispatchBead.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                    return Result(DeliverySlotDisposition.Failed, preDispatchState, role, lastStage, false,
                        $"Claim ownership changed before the next role; observed status '{preDispatchBead.Status}' and assignee '{preDispatchBead.Assignee ?? "(none)"}'.");
                if (preDispatchState != after)
                    return Result(DeliverySlotDisposition.Failed, preDispatchState, role, lastStage, false,
                        "Bead workflow identity changed during interstage readiness verification; no follow-on Agent was launched.");
                currentState = preDispatchState;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (claimAcquired && !executionStarted)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    "Delivery slot was cancelled before role execution.", null).ConfigureAwait(false);
            if (claimAcquisitionAttempted && !claimAcquired)
                return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, initial, null, null, false,
                    "Guarded claim acquisition was cancelled before ownership was confirmed; ownership is uncertain and no release was attempted.");
            return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome, null, lastRole, lastStage, false,
                executionStarted
                    ? "Delivery slot was cancelled after role execution began; no follow-on role or blind claim release was attempted."
                    : "Delivery slot was cancelled before guarded claim acquisition completed; inspect claim ownership before retrying.");
        }
        catch (Exception exception)
        {
            if (claimAcquired && !executionStarted)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed,
                    $"Delivery admission failed: {exception.Message}", null).ConfigureAwait(false);
            if (claimAcquisitionAttempted && !claimAcquired)
                return Result(DeliverySlotDisposition.Refused, initial, null, null, false,
                    $"Guarded claim acquisition failed before ownership was confirmed; no release was attempted: {exception.Message}");
            return Result(DeliverySlotDisposition.Failed, null, lastRole, lastStage, false,
                executionStarted
                    ? $"Delivery stage execution or handoff failed; no follow-on role or blind claim release was attempted: {exception.Message}"
                    : $"Delivery slot failed before claim acquisition: {exception.Message}");
        }
    }

    private async Task<DeliverySlotOutcome> ReleaseMergeReadyClaimAsync(
        ValidatedTargetRepository repository,
        string beadId,
        string actor,
        BeadWorkflowState verifiedState,
        AgentRole role,
        OperationalRetryOutcome lastStage,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!current.ById.TryGetValue(beadId, out var bead))
                return Result(DeliverySlotDisposition.Failed, verifiedState, role, lastStage, false,
                    "Bead disappeared before merge handoff; its execution evidence was retained.");
            var state = BeadWorkflowState.Parse(bead);
            if (state != verifiedState || state.Stage != "stage:merge"
                || bead.Status != "in_progress" || !string.Equals(bead.Assignee, actor, StringComparison.Ordinal))
                return Result(DeliverySlotDisposition.Failed, state, role, lastStage, false,
                    "Merge handoff preflight found stale workflow state or changed claim ownership; the claim was left untouched.");

            await writer.ReleaseDispatchClaimAsync(repository, beadId, actor, "in_progress", cancellationToken)
                .ConfigureAwait(false);
            var released = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!released.ById.TryGetValue(beadId, out var finalBead))
                return Result(DeliverySlotDisposition.Failed, verifiedState, role, lastStage, false,
                    "Bead disappeared after guarded merge handoff.");
            var finalState = BeadWorkflowState.Parse(finalBead);
            if (finalBead.Status != "open" || finalBead.Assignee is not null
                || finalState != (verifiedState with { Status = "open" }))
                return Result(DeliverySlotDisposition.Failed, finalState, role, lastStage, false,
                    "Guarded merge handoff did not verify as stage:merge, open, and unassigned.");
            return Result(DeliverySlotDisposition.MergeReady, finalState, role, lastStage, true, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(DeliverySlotDisposition.Failed, verifiedState, role, lastStage, false,
                $"Merge handoff failed or could not be verified: {exception.Message}");
        }
    }

    private async Task<DeliverySlotOutcome> EscalateRepairBudgetAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        BeadWorkflowState expectedState,
        AgentRole role,
        OperationalRetryOutcome? lastStage,
        bool persistedAtAdmission,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? latestFindings = null,
        string? rejectingStage = null)
    {
        latestFindings ??= await ReadLatestFindingsAsync(repository, beadId, lastStage?.StageResult,
            $"Persisted repair_cycles={expectedState.RepairCycles} at Delivery admission; max_repair_cycles={project.Limits.MaxRepairCycles}; current lifecycle role={AgentRoles.ToConfigKey(role)} at {expectedState.Stage ?? "(none)"}",
            cancellationToken).ConfigureAwait(false);

        BeadWorkflowState currentState;
        try
        {
            var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!inventory.ById.TryGetValue(beadId, out var bead))
                return Result(DeliverySlotDisposition.Failed, expectedState, role, lastStage, false,
                    "Repair-budget escalation was refused because the Bead disappeared before cleanup.");
            currentState = BeadWorkflowState.Parse(bead);
            if (bead.Status != "in_progress" || !string.Equals(bead.Assignee, project.RecoveryActor, StringComparison.Ordinal)
                || currentState != expectedState)
                return Result(DeliverySlotDisposition.Failed, currentState, role, lastStage, false,
                    "Repair-budget escalation was refused because the live workflow state or claim changed before cleanup.");
            var readiness = await reader.ReadReadinessAsync(repository, inventory, project.RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            if (!readiness.RecoverableOwnedReady.Contains(beadId))
                return Result(DeliverySlotDisposition.Failed, currentState, role, lastStage, false,
                    "Repair-budget escalation was refused because the owned Bead is no longer in the recoverable-ready set.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Result(DeliverySlotDisposition.Failed, expectedState, role, lastStage, false,
                $"Repair-budget escalation preflight failed; no cleanup was attempted: {exception.Message}");
        }
        var stage = (rejectingStage ?? expectedState.Stage) ?? "(none)";
        var reason = persistedAtAdmission
            ? $"Persisted repair_cycles={expectedState.RepairCycles} at Delivery admission exceeds max_repair_cycles={project.Limits.MaxRepairCycles}; no Agent invocation was started. Current lifecycle role: {AgentRoles.ToConfigKey(role)} at {stage}."
            : $"Verified {AgentRoles.ToConfigKey(role)} rejection at {stage} raised repair_cycles to {expectedState.RepairCycles}, exceeding max_repair_cycles={project.Limits.MaxRepairCycles}.";
        var stageResult = lastStage?.StageResult;
        var request = new NeedsHumanRequest(beadId, project.RecoveryActor, NeedsHumanCause.RepairBudgetExhausted,
            role, reason, latestFindings, string.IsNullOrWhiteSpace(stageResult?.InvocationId) ? null : stageResult.InvocationId,
            string.IsNullOrWhiteSpace(stageResult?.LogFilePath) ? null : [stageResult.LogFilePath],
            WorktreePurpose.Delivery);

        try
        {
            var escalation = await escalateRepairBudget(paths, project, repository, request, cancellationToken)
                .ConfigureAwait(false);
            return BuildBudgetEscalationOutcome(currentState, role, lastStage, escalation,
                escalation.Succeeded ? null : "Repair-budget needs-human escalation did not complete.");
        }
        catch (NeedsHumanEscalationException exception)
        {
            return BuildBudgetEscalationOutcome(currentState, role, lastStage, exception.Progress, exception.Message);
        }
        catch (OperationCanceledException exception) when (TryGetEscalationProgress(exception, out var progress))
        {
            return Result(DeliverySlotDisposition.StoppedAtUnsupportedOutcome,
                StateAfterEscalation(currentState, progress), role, lastStage, false, exception.Message, progress);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Result(DeliverySlotDisposition.Failed, currentState, role, lastStage, false,
                $"Repair-budget needs-human escalation failed: {exception.Message}");
        }
    }

    private static DeliverySlotOutcome BuildBudgetEscalationOutcome(BeadWorkflowState currentState,
        AgentRole role, OperationalRetryOutcome? lastStage, NeedsHumanEscalationResult progress, string? failure)
    {
        var succeeded = progress.Succeeded;
        return Result(succeeded ? DeliverySlotDisposition.Escalated : DeliverySlotDisposition.Failed,
            StateAfterEscalation(currentState, progress), role, lastStage, succeeded,
            succeeded ? null : $"{failure ?? "Repair-budget needs-human escalation did not complete."} {string.Join("; ", progress.Failures)}".Trim(),
            progress);
    }

    private async Task<IReadOnlyList<string>> ReadLatestFindingsAsync(
        ValidatedTargetRepository repository,
        string beadId,
        AgentStageResult? stageResult,
        string evidenceContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var comments = await reader.ReadCommentsAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
            if (comments.Count == 0)
                return [$"No Bead comments were available. {evidenceContext}."];
            return [comments[^1].Text];
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var invocation = string.IsNullOrWhiteSpace(stageResult?.InvocationId) ? "unavailable" : stageResult.InvocationId;
            var log = string.IsNullOrWhiteSpace(stageResult?.LogFilePath) ? "unavailable" : stageResult.LogFilePath;
            return [$"Latest Bead comments could not be read ({exception.Message}). {evidenceContext}, invocation={invocation}, log={log}."];
        }
    }

    private static BeadWorkflowState StateAfterEscalation(BeadWorkflowState state, NeedsHumanEscalationResult progress) =>
        state with
        {
            NeedsHuman = progress.PauseRecorded || state.NeedsHuman,
            Status = progress.ClaimReleased && state.Status == "in_progress" ? "open" : state.Status
        };

    private async Task<DeliverySlotOutcome> RefuseAfterClaimAsync(
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        BeadSnapshot? claimed,
        string reason,
        BeadWorkflowState? state)
    {
        string? releaseFailure = null;
        using var timeout = new CancellationTokenSource(ClaimFinalizationTimeout);
        if (claimed is null)
        {
            try
            {
                var inventory = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                inventory.ById.TryGetValue(beadId, out claimed);
            }
            catch (Exception exception) { releaseFailure = $"claim owner could not be reread: {exception.Message}"; }
        }

        var handoff = false;
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
                    releaseFailure = "guarded release did not verify against the fresh Beads inventory";
                else
                    handoff = true;
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

        return Result(DeliverySlotDisposition.Refused, state, null, null, handoff,
            releaseFailure is null ? reason : $"{reason} Claim release: {releaseFailure}.");
    }

    private static string? ValidateAdmission(BeadSnapshot bead, BeadWorkflowState state, BeadsReadiness readiness)
    {
        if (bead.Status != "open" || bead.Assignee is not null)
            return $"Bead '{bead.Id}' must be open and unassigned before Delivery; observed status '{bead.Status}' and assignee '{bead.Assignee ?? "(none)"}'.";
        if (bead.IssueType != "task")
            return $"Delivery slot accepts Plan Beads of type task; '{bead.Id}' is '{bead.IssueType}'.";
        if (state.Planned || state.ProductDecisionRequired || state.NeedsHuman)
            return $"Bead '{bead.Id}' is planned or paused and cannot enter a Delivery slot.";
        if (state.Stage is not (null or "stage:implement" or "stage:review" or "stage:qa"))
            return $"Delivery slot cannot accept stage '{state.Stage ?? "(none)"}'.";
        if (!readiness.NativeUnassignedReady.Contains(bead.Id))
            return $"Bead '{bead.Id}' is not in bd's native unassigned ready set; it may be blocked or deferred.";
        return null;
    }

    private static bool TrySelectRole(BeadWorkflowState state, out AgentRole role)
    {
        role = state.Stage switch
        {
            null or "stage:implement" => AgentRole.Implementer,
            "stage:review" => AgentRole.Reviewer,
            "stage:qa" => AgentRole.Qa,
            _ => AgentRole.Classifier
        };
        return role != AgentRole.Classifier && AuthorityMatrix.ValidateInput(role, state) is null;
    }

    private static bool IsRoleInput(AgentRole role, BeadWorkflowState state) => AuthorityMatrix.ValidateInput(role, state) is null;

    private static bool TryGetEscalationProgress(OperationCanceledException exception,
        out NeedsHumanEscalationResult progress)
    {
        if (exception.Data[nameof(NeedsHumanEscalationResult)] is NeedsHumanEscalationResult observed)
        {
            progress = observed;
            return true;
        }
        progress = null!;
        return false;
    }

    private static bool IsUnsupportedLegalOutcome(AgentRole role, BeadWorkflowState before, BeadWorkflowState after)
    {
        if (after.ProductDecisionRequired || after.NeedsHuman) return true;
        return IsReviewQaRejection(role, before, after);
    }

    private static bool IsReviewQaRejection(AgentRole role, BeadWorkflowState before, BeadWorkflowState after)
    {
        var inputStage = role switch
        {
            AgentRole.Reviewer => "stage:review",
            AgentRole.Qa => "stage:qa",
            _ => null
        };
        if (inputStage is null || before.Stage != inputStage || after.Id != before.Id
            || after.IssueType != before.IssueType || after.Status != before.Status || after.Stage != "stage:implement"
            || after.Planned || after.ProductDecisionRequired || after.NeedsHuman) return false;
        try { return after.RepairCycles == checked(before.RepairCycles + 1); }
        catch (OverflowException) { return false; }
    }

    private static AgentRole InferLifecycleRole(BeadWorkflowState state) => state.Stage switch
    {
        "stage:review" => AgentRole.Reviewer,
        "stage:qa" => AgentRole.Qa,
        null or "stage:implement" => AgentRole.Implementer,
        _ => AgentRole.Implementer
    };

    private static DeliverySlotOutcome Result(DeliverySlotDisposition disposition, BeadWorkflowState? state,
        AgentRole? role, OperationalRetryOutcome? stage, bool handoff, string? diagnostic,
        NeedsHumanEscalationResult? escalationProgress = null) =>
        new(disposition, state, role, stage, handoff, diagnostic, escalationProgress);
}
