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

internal delegate Task<NeedsHumanEscalationResult> MergerRepairBudgetEscalator(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    NeedsHumanRequest request,
    CancellationToken cancellationToken);

internal enum MergerSlotDisposition
{
    Merged,
    Escalated,
    Refused,
    Failed,
    Canceled,
    RepairHandedOff,
    PausedForProductDecision,
    StoppedAtUnsupportedOutcome
}

internal sealed record MergerSlotOutcome(
    MergerSlotDisposition Disposition,
    BeadWorkflowState? FinalState,
    OperationalRetryOutcome? Stage,
    bool ClaimHandoffCompleted,
    string? Diagnostic,
    NeedsHumanEscalationResult? EscalationProgress = null)
{
    internal bool MergeCompleted => Disposition == MergerSlotDisposition.Merged && ClaimHandoffCompleted;
    internal bool RepairHandoffCompleted => Disposition == MergerSlotDisposition.RepairHandedOff && ClaimHandoffCompleted;
}

/// <summary>Claims and runs one stage:merge Plan Bead, then accepts only verified closure.</summary>
internal sealed class MergerSlotCoordinator
{
    private static readonly TimeSpan ClaimFinalizationTimeout = TimeSpan.FromSeconds(15);

    private readonly BeadsReader reader;
    private readonly BeadsWriter writer;
    private readonly MergerRoleRunner runRole;
    private readonly MergerRepairBudgetEscalator escalateRepairBudget;

    internal MergerSlotCoordinator(IProcessRunner processRunner, MergerRoleRunner? runRole = null,
        MergerRepairBudgetEscalator? escalateRepairBudget = null, ICompletedEnvironmentQueue? cleanupQueue = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        reader = new BeadsReader(processRunner);
        writer = new BeadsWriter(processRunner);
        var escalator = new NeedsHumanEscalator(processRunner);
        this.escalateRepairBudget = escalateRepairBudget ?? escalator.EscalateAsync;
        if (runRole is null)
        {
            var retries = new OperationalRetryCoordinator(processRunner, cleanupQueue: cleanupQueue);
            this.runRole = (paths, project, repository, beadId, role, actor, expectedState, token, progress) =>
                retries.RunAsync(paths, project, repository, beadId, role, actor, token, progress, expectedState);
        }
        else
        {
            this.runRole = runRole;
        }
    }

    internal Task<MergerSlotOutcome> RunAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken) =>
        RunAsync(paths, project, repository, beadId, static () => { }, cancellationToken);

    internal async Task<MergerSlotOutcome> RunAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        Action agentAttemptStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(agentAttemptStarted);
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

            var refusal = ValidateAdmission(candidate, initialState, admission.Readiness);
            if (refusal is not null)
                return Result(MergerSlotDisposition.Refused, initialState, null, false, refusal);

            cancellationToken.ThrowIfCancellationRequested();
            claimAcquisitionAttempted = true;
            await writer.AcquireDispatchClaimAsync(repository, beadId, project.RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            claimAcquiredHere = true;

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

            var expectedClaimedState = initialState with { Status = "in_progress" };
            if (claimedState != expectedClaimedState)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    "Bead type, stage, pause labels, or repair_cycles changed during claim acquisition.")
                    .ConfigureAwait(false);
            if (!claimedSnapshot.Readiness.RecoverableOwnedReady.Contains(beadId))
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    "Bead is no longer in bd's recoverable-owned ready set after Merger admission; it may have become blocked or deferred.")
                    .ConfigureAwait(false);

            if (claimedState.RepairCycles > project.Limits.MaxRepairCycles)
            {
                var findings = await ReadLatestFindingsAsync(repository, beadId, null,
                    $"Persisted repair_cycles={claimedState.RepairCycles} at Merger admission; max_repair_cycles={project.Limits.MaxRepairCycles}; no Merger invocation was started.",
                    cancellationToken).ConfigureAwait(false);
                return await EscalateRepairBudgetAsync(paths, project, repository, beadId, claimedState,
                    AgentRole.Merger, null, persistedAtAdmission: true, cancellationToken, findings,
                    rejectingStage: claimedState.Stage).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            executionStarted = true;
            OperationalRetryOutcome stage;
            try
            {
                stage = await runRole(paths, project, repository, beadId, AgentRole.Merger,
                    project.RecoveryActor, claimedState, cancellationToken,
                    progress: ObserveFirstAgentAttempt(agentAttemptStarted)).ConfigureAwait(false);
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
                    completed ? null : "Operational retry escalation returned without a verified claim handoff.",
                    stage.Escalation);
            }

            if (stage.StageResult.Role != AgentRole.Merger || stage.StageResult.BeadId != beadId)
                return Result(MergerSlotDisposition.Failed, claimedState, stage, false,
                    "The operational retry coordinator returned a result for a different role or Bead.");
            if (!stage.Succeeded || !stage.StageResult.Succeeded)
                return Result(MergerSlotDisposition.Failed, claimedState, stage, false,
                    "Merger returned without a verified successful outcome.");

            var receipt = stage.StageResult.PublicationReceipt;
            if (receipt is { Role: AgentRole.Merger, Outcome: AgentOutcomeChoice.ChangesRequired })
            {
                if (!IsValidRepairReceipt(receipt, claimedState))
                    return Result(MergerSlotDisposition.Failed, receipt.PublishedState, stage, false,
                        "Merger repair publication receipt did not confirm exactly one guarded counter increment and Delivery claim release.");
                // A matching publication receipt is the final outgoing operation. Do not
                // query Beads, Git, readiness, comments, or worktree state after this point.
                return Result(MergerSlotDisposition.RepairHandedOff, receipt.PublishedState, stage, true, null);
            }
            if (receipt is { Role: AgentRole.Merger, Outcome: AgentOutcomeChoice.ProductDecisionRequired })
            {
                var expectedPause = claimedState with { ProductDecisionRequired = true, Status = "open" };
                if (receipt.PublishedState != expectedPause || receipt.PublishedAssignee is not null
                    || !receipt.ClaimReleased || receipt.HandoffToMerger)
                    return Result(MergerSlotDisposition.Failed, receipt.PublishedState, stage, false,
                        "Merger product-decision receipt did not confirm the guarded pause and claim release.");
                return Result(MergerSlotDisposition.PausedForProductDecision, receipt.PublishedState, stage, true, null);
            }

            var verified = stage.StageResult.VerifiedState;
            if (stage.StageResult.OutcomeClassification?.Outcome == AgentOutcomeChoice.Approved
                && stage.StageResult.MergerCompletion is { } completion
                && verified == completion.ClosedState && completion.ClosedState.Status == "closed")
            {
                var diagnostic = completion.CleanupDiagnostic is null && completion.ProductDiagnostic is null
                    && completion.DiagnosticPersistenceDiagnostic is null ? null
                    : $"Verified merge and task closure completed; post-merge housekeeping needs attention. Cleanup: {completion.CleanupDiagnostic ?? "completed"}; Product: {completion.ProductDiagnostic ?? "completed or safely ineligible"}; diagnostic persistence: {completion.DiagnosticPersistenceDiagnostic ?? (completion.CleanupDiagnostic is null && completion.ProductDiagnostic is null ? "not required" : "recorded")}.";
                return Result(MergerSlotDisposition.Merged, completion.ClosedState, stage, true, diagnostic);
            }

            return Result(MergerSlotDisposition.Failed, verified ?? claimedState, stage, false,
                "Merger did not return a supported verified rejection or completion publication.");
        }
        catch (AgentLaunchDeniedException exception)
        {
            if (claimAcquiredHere)
                return await RefuseAfterClaimAsync(project, repository, beadId, claimed, claimAcquiredHere,
                    $"The continuous runtime stopped before the next Agent launch; unfinished work was retained. {exception.Message}",
                    requiredFinalization: true)
                    .ConfigureAwait(false);
            if (claimAcquisitionAttempted)
                return Result(MergerSlotDisposition.Refused, initialState, null, false,
                    "The continuous runtime stopped before guarded claim acquisition was confirmed; ownership remains uncertain.");
            return Result(MergerSlotDisposition.Refused, initialState, null, false, exception.Message);
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
        BeadsReadiness readiness)
    {
        if (bead.IssueType != "task")
            return $"Merger accepts Plan Beads of type task; '{bead.Id}' is '{bead.IssueType}'.";
        if (state.PlanUnpublished)
            return $"Plan Bead '{bead.Id}' is unpublished and cannot enter Merger.";
        var stages = StageLabelParser.Parse(bead.Labels);
        if (stages.Count != 1 || stages[0] != "stage:merge" || state.Stage != "stage:merge")
            return $"Merger requires exactly one stage label, stage:merge; observed '{string.Join(", ", stages)}'.";
        if (state.Planned || state.ProductDecisionRequired || state.NeedsHuman)
            return $"Bead '{bead.Id}' is planned or paused and cannot enter the Merger slot.";

        if (bead.Status != "open" || bead.Assignee is not null)
            return $"Bead '{bead.Id}' must be open and unassigned for a fresh Merger claim; observed status '{bead.Status}' and assignee '{bead.Assignee ?? "(none)"}'.";
        return readiness.NativeUnassignedReady.Contains(bead.Id)
            ? null
            : $"Bead '{bead.Id}' is not in bd's native unassigned ready set; it may be blocked or deferred.";
    }

    private static Action<OperationalRetryProgress> ObserveFirstAgentAttempt(Action agentAttemptStarted)
    {
        return progress =>
        {
            if (progress.Kind == OperationalRetryProgressKind.AttemptStarted)
                agentAttemptStarted();
        };
    }

    private static bool IsValidRepairReceipt(DeliveryOutcomePublicationReceipt receipt,
        BeadWorkflowState claimedState)
    {
        int nextRepairCycles;
        try { nextRepairCycles = checked(claimedState.RepairCycles + 1); }
        catch (OverflowException) { return false; }
        var expected = claimedState with
        {
            Stage = "stage:implement",
            RepairCycles = nextRepairCycles
        };
        var releasedOpen = receipt.PublishedState == (expected with { Status = "open" })
            && receipt.PublishedAssignee is null;
        var receiverClaimed = receipt.PublishedState == (expected with { Status = "in_progress" })
            && !string.IsNullOrWhiteSpace(receipt.PublishedAssignee);
        return (releasedOpen || receiverClaimed) && receipt.ClaimReleased && !receipt.HandoffToMerger;
    }

    private async Task<MergerSlotOutcome> EscalateRepairBudgetAsync(HostPaths paths, ProjectConfig project,
        ValidatedTargetRepository repository, string beadId, BeadWorkflowState expectedState, AgentRole role,
        OperationalRetryOutcome? stage, bool persistedAtAdmission, CancellationToken cancellationToken,
        IReadOnlyList<string>? latestFindings = null, string? rejectingStage = null)
    {
        var findings = latestFindings ?? await ReadLatestFindingsAsync(repository, beadId, stage?.StageResult,
            $"Persisted repair_cycles={expectedState.RepairCycles}; max_repair_cycles={project.Limits.MaxRepairCycles}; current Merger lifecycle state is {expectedState.Stage ?? "(none)"}.",
            cancellationToken).ConfigureAwait(false);

        try
        {
            var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!inventory.ById.TryGetValue(beadId, out var bead))
                return Result(MergerSlotDisposition.Failed, expectedState, stage, false,
                    "Repair-budget escalation was refused because the Bead disappeared before pause handling.");
            var current = BeadWorkflowState.Parse(bead);
            if (current != expectedState || bead.Status != "in_progress"
                || !string.Equals(bead.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                return Result(MergerSlotDisposition.Failed, current, stage, false,
                    "Repair-budget escalation was refused because workflow state or claim ownership changed before pause handling.");
            var readiness = await reader.ReadReadinessAsync(repository, inventory, project.RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            if (!readiness.RecoverableOwnedReady.Contains(beadId))
                return Result(MergerSlotDisposition.Failed, current, stage, false,
                    "Repair-budget escalation was refused because the owned Bead is no longer in the recoverable-ready set.");

            // Do not adopt a new baseline if status, owner, or metadata changes during readiness.
            var verifiedInventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!verifiedInventory.ById.TryGetValue(beadId, out var verifiedBead))
                return Result(MergerSlotDisposition.Failed, current, stage, false,
                    "Repair-budget escalation was refused because the Bead disappeared after readiness verification.");
            current = BeadWorkflowState.Parse(verifiedBead);
            if (current != expectedState || verifiedBead.Status != "in_progress"
                || !string.Equals(verifiedBead.Assignee, project.RecoveryActor, StringComparison.Ordinal))
                return Result(MergerSlotDisposition.Failed, current, stage, false,
                    "Repair-budget escalation was refused because workflow state or claim ownership changed during readiness verification.");

            var limit = project.Limits.MaxRepairCycles;
            var stageName = (rejectingStage ?? expectedState.Stage) ?? "(none)";
            var reason = persistedAtAdmission
                ? $"Persisted repair_cycles={expectedState.RepairCycles} at Merger admission exceeds max_repair_cycles={limit}; no Merger invocation was started."
                : $"Verified Merger rejection at {stageName} raised repair_cycles to {expectedState.RepairCycles}, exceeding max_repair_cycles={limit}.";
            var stageResult = stage?.StageResult;
            var request = new NeedsHumanRequest(beadId, project.RecoveryActor, NeedsHumanCause.RepairBudgetExhausted,
                role, reason, findings,
                string.IsNullOrWhiteSpace(stageResult?.InvocationId) ? null : stageResult.InvocationId,
                string.IsNullOrWhiteSpace(stageResult?.LogFilePath) ? null : [stageResult.LogFilePath],
                WorktreePurpose.Delivery, expectedState);
            var progress = await escalateRepairBudget(paths, project, repository, request, cancellationToken)
                .ConfigureAwait(false);
            return BuildEscalationOutcome(expectedState, stage, progress,
                progress.Succeeded ? null : "Repair-budget needs-human escalation did not complete.");
        }
        catch (NeedsHumanEscalationException exception)
        {
            return BuildEscalationOutcome(expectedState, stage, exception.Progress, exception.Message);
        }
        catch (OperationCanceledException exception) when (TryGetEscalationProgress(exception, out var progress))
        {
            return Result(MergerSlotDisposition.Canceled, StateAfterEscalation(expectedState, progress), stage,
                false, exception.Message, progress);
        }
        catch (OperationCanceledException)
        {
            return Result(MergerSlotDisposition.Canceled, expectedState, stage, false,
                "Repair-budget escalation was canceled before completion.");
        }
        catch (Exception exception)
        {
            return Result(MergerSlotDisposition.Failed, expectedState, stage, false,
                $"Repair-budget escalation failed: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> ReadLatestFindingsAsync(ValidatedTargetRepository repository,
        string beadId, AgentStageResult? stageResult, string evidenceContext, CancellationToken cancellationToken)
    {
        try
        {
            var comments = await reader.ReadCommentsAsync(repository, beadId, cancellationToken).ConfigureAwait(false);
            if (comments.Count == 0) return [$"No Bead comments were available. {evidenceContext}"];
            var invocationComments = stageResult?.PreInvocationCommentIds is { } priorIds
                ? comments.Where(comment => !priorIds.Contains(comment.Id)).ToArray()
                : comments.ToArray();
            return [invocationComments.Length == 0 ? comments[^1].Text : invocationComments[^1].Text];
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var invocation = string.IsNullOrWhiteSpace(stageResult?.InvocationId) ? "unavailable" : stageResult.InvocationId;
            var log = string.IsNullOrWhiteSpace(stageResult?.LogFilePath) ? "unavailable" : stageResult.LogFilePath;
            return [$"Latest Bead comments could not be read ({exception.Message}). {evidenceContext} invocation={invocation}, log={log}."];
        }
    }

    private static MergerSlotOutcome BuildEscalationOutcome(BeadWorkflowState expectedState,
        OperationalRetryOutcome? stage, NeedsHumanEscalationResult progress, string? failure)
    {
        var state = StateAfterEscalation(expectedState, progress);
        return Result(progress.Succeeded ? MergerSlotDisposition.Escalated : MergerSlotDisposition.Failed,
            state, stage, progress.Succeeded,
            progress.Succeeded ? null : $"{failure ?? "Repair-budget needs-human escalation did not complete."} {string.Join("; ", progress.Failures)}".Trim(),
            progress);
    }

    private static BeadWorkflowState StateAfterEscalation(BeadWorkflowState state, NeedsHumanEscalationResult progress) =>
        state with
        {
            NeedsHuman = progress.PauseRecorded || state.NeedsHuman,
            Status = progress.ClaimReleased && state.Status == "in_progress" ? "open" : state.Status
        };

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

    private async Task<MergerSlotOutcome> RefuseAfterClaimAsync(ProjectConfig project,
        ValidatedTargetRepository repository, string beadId, BeadSnapshot? claimed, bool claimAcquiredHere, string reason,
        bool requiredFinalization = false)
    {
        if (!claimAcquiredHere)
            return Result(MergerSlotDisposition.Refused,
                claimed is null ? null : BeadWorkflowState.Parse(claimed), null, false, reason);

        string? releaseFailure = null;
        var handoff = false;
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
            var expectedStatus = claimed.Status;
            var expectedReleasedStatus = expectedStatus == "in_progress" ? "open" : expectedStatus;
            BeadWorkflowState? expectedReleasedState = null;
            try
            {
                expectedReleasedState = BeadWorkflowState.Parse(claimed) with { Status = expectedReleasedStatus };
            }
            catch (FormatException exception)
            {
                releaseFailure = $"workflow state could not be verified before release: {exception.Message}";
            }

            if (releaseFailure is null)
            {
                Exception? releaseException = null;
                try
                {
                    await writer.ReleaseDispatchClaimAsync(repository, beadId, project.RecoveryActor, expectedStatus, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) { releaseException = exception; }

                try
                {
                    // Always reconcile an uncertain guarded write once. Never replay the release.
                    var released = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                    if (!released.ById.TryGetValue(beadId, out var finalBead))
                        releaseFailure = "the Bead disappeared during guarded release reconciliation";
                    else
                    {
                        BeadWorkflowState? finalState = null;
                        try { finalState = BeadWorkflowState.Parse(finalBead); }
                        catch (FormatException exception)
                        {
                            releaseFailure = $"fresh release state could not be verified: {exception.Message}";
                        }

                        if (releaseFailure is null && finalBead.Status == expectedReleasedStatus && finalBead.Assignee is null
                            && finalState == expectedReleasedState)
                            handoff = true;
                        else if (releaseFailure is null)
                            releaseFailure = releaseException is null
                                ? "guarded claim release did not verify against the fresh Beads inventory"
                                : $"guarded claim release returned an uncertain result ({releaseException.Message}); fresh state remains status '{finalBead.Status}', assignee '{finalBead.Assignee ?? "(none)"}' and was left untouched";
                    }
                }
                catch (Exception exception)
                {
                    releaseFailure = releaseException is null
                        ? $"fresh ownership reread after guarded release failed: {exception.Message}"
                        : $"guarded claim release returned an uncertain result ({releaseException.Message}) and fresh ownership reread failed: {exception.Message}";
                }
            }
        }
        else if (releaseFailure is null && claimed is null)
        {
            releaseFailure = "the Bead is missing; claim ownership could not be verified for release";
        }
        else if (releaseFailure is null)
        {
            releaseFailure = $"claim is no longer owned by '{project.RecoveryActor}' (observed '{claimed?.Assignee ?? "(none)"}'); it was left untouched";
        }

        var finalizationRequired = requiredFinalization || ContinuousAgentLaunchContext.StopRequested;
        return Result(releaseFailure is not null && finalizationRequired
                ? MergerSlotDisposition.Failed : MergerSlotDisposition.Refused,
            claimed is null ? null : BeadWorkflowState.Parse(claimed), null, handoff,
            releaseFailure is null ? reason : $"{reason} Claim release: {releaseFailure}.");
    }

    private static MergerSlotOutcome Result(MergerSlotDisposition disposition, BeadWorkflowState? state,
        OperationalRetryOutcome? stage, bool handoff, string? diagnostic,
        NeedsHumanEscalationResult? escalationProgress = null) =>
        new(disposition, state, stage, handoff, diagnostic, escalationProgress);
}
