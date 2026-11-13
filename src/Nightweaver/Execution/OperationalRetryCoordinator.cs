using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum OperationalRetryStatus
{
    Succeeded,
    Escalated,
    StoppedForDrain
}

internal enum OperationalRetryProgressKind
{
    AttemptStarted,
    RetryPreparing,
    ClassifierStarted,
    ClassifierCompleted
}

internal sealed record OperationalRetryProgress(
    OperationalRetryProgressKind Kind,
    long Attempt,
    long MaximumAttempts,
    AgentOperationalFailureKind? FailureKind = null,
    string? Reason = null,
    DuplicateFailureVerdict? ClassifierVerdict = null,
    string? ClassifierDiagnostic = null,
    string? InvocationId = null,
    string? LogFilePath = null,
    string? ClassifierInvocationId = null,
    string? ClassifierLogFilePath = null);

internal sealed record OperationalRetryOutcome(
    OperationalRetryStatus Status,
    AgentStageResult StageResult,
    long AttemptCount,
    NeedsHumanCause? EscalationCause,
    NeedsHumanEscalationResult? Escalation,
    DuplicateFailureClassificationResult? LastClassification)
{
    internal bool Succeeded => Status == OperationalRetryStatus.Succeeded;
}

internal sealed class OperationalRetryEscalationIncompleteException : NightweaverException
{
    internal OperationalRetryEscalationIncompleteException(string message, OperationalRetryOutcome outcome)
        : base(message)
    {
        Outcome = outcome;
        Progress = outcome.Escalation
            ?? throw new ArgumentException("Incomplete escalation outcome must include escalation progress.", nameof(outcome));
    }

    internal OperationalRetryEscalationIncompleteException(string message, OperationalRetryOutcome outcome,
        Exception innerException)
        : base(message, innerException)
    {
        Outcome = outcome;
        Progress = outcome.Escalation
            ?? throw new ArgumentException("Incomplete escalation outcome must include escalation progress.", nameof(outcome));
    }

    internal OperationalRetryOutcome Outcome { get; }
    internal NeedsHumanEscalationResult Progress { get; }
}

/// <summary>Runs one claimed lifecycle stage with bounded operational retries and needs-human handoff.</summary>
internal sealed class OperationalRetryCoordinator
{
    private const int MaxFindingCharacters = 1024;

    private readonly AgentStageCoordinator stages;
    private readonly DuplicateFailureClassifier classifier;
    private readonly NeedsHumanEscalator escalator;
    private readonly WorktreeRetryResetter resetter;
    private readonly Func<HostPaths, ProjectSlug, ValidatedTargetRepository, string, WorktreePurpose,
        CancellationToken, Task<ProvisionedWorktree>> reset;
    private readonly Func<HostPaths, ProjectConfig, AgentDispatchContext, AgentRole, string, AgentStageResult,
        AgentStageResult, CancellationToken, Task<DuplicateFailureClassificationResult>> compare;
    private readonly Func<HostPaths, ProjectConfig, ValidatedTargetRepository, NeedsHumanRequest,
        CancellationToken, Task<NeedsHumanEscalationResult>> escalate;
    private readonly BeadsReader beadsReader;
    private readonly PlanningEvidence planningEvidence;
    private readonly PlanningPublisher planningPublisher;
    private readonly IAgentOutcomeClassifier outcomeClassifier;
    private readonly IDeliveryOutcomePublisher outcomePublisher;
    private readonly IDeliveryOutcomeVerifier outcomeVerifier;
    private readonly IMergerOutcomeVerifier mergerOutcomeVerifier;
    private readonly MergerCompletionFinalizer mergerCompletionFinalizer;

    internal OperationalRetryCoordinator(
        IProcessRunner processRunner,
        AgentStageCoordinator? stages = null,
        DuplicateFailureClassifier? classifier = null,
        NeedsHumanEscalator? escalator = null,
        WorktreeRetryResetter? resetter = null,
        Func<HostPaths, ProjectSlug, ValidatedTargetRepository, string, WorktreePurpose,
            CancellationToken, Task<ProvisionedWorktree>>? reset = null,
        Func<HostPaths, ProjectConfig, AgentDispatchContext, AgentRole, string, AgentStageResult,
            AgentStageResult, CancellationToken, Task<DuplicateFailureClassificationResult>>? compare = null,
        Func<HostPaths, ProjectConfig, ValidatedTargetRepository, NeedsHumanRequest,
            CancellationToken, Task<NeedsHumanEscalationResult>>? escalate = null,
        IAgentOutcomeClassifier? outcomeClassifier = null,
        IDeliveryOutcomePublisher? outcomePublisher = null,
        IDeliveryOutcomeVerifier? outcomeVerifier = null,
        IMergerOutcomeVerifier? mergerOutcomeVerifier = null,
        ICompletedEnvironmentQueue? cleanupQueue = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        this.stages = stages ?? new AgentStageCoordinator(processRunner);
        this.classifier = classifier ?? new DuplicateFailureClassifier();
        this.escalator = escalator ?? new NeedsHumanEscalator(processRunner);
        this.resetter = resetter ?? new WorktreeRetryResetter(processRunner);
        this.reset = reset ?? this.resetter.ResetAsync;
        this.compare = compare ?? this.classifier.CompareAsync;
        this.escalate = escalate ?? this.escalator.EscalateAsync;
        beadsReader = new BeadsReader(processRunner);
        planningEvidence = new PlanningEvidence(processRunner);
        planningPublisher = new PlanningPublisher(processRunner);
        this.outcomeClassifier = outcomeClassifier ?? new AgentOutcomeClassifier();
        this.outcomePublisher = outcomePublisher ?? new DeliveryOutcomePublisher(processRunner);
        this.outcomeVerifier = outcomeVerifier ?? new DeliveryOutcomeVerifier(processRunner);
        this.mergerOutcomeVerifier = mergerOutcomeVerifier ?? new MergerOutcomeVerifier(processRunner);
        mergerCompletionFinalizer = new MergerCompletionFinalizer(processRunner, cleanupQueue);
    }

    internal async Task<OperationalRetryOutcome> RunAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        AgentRole role,
        string expectedAssignee,
        CancellationToken cancellationToken,
        Action<OperationalRetryProgress>? progress = null,
        BeadWorkflowState? expectedState = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(expectedAssignee))
            throw new AgentStageInputException("Expected claim assignee must be nonblank.");

        // Capture the caller's budget exactly once. Long indexing keeps int.MaxValue safe.
        var retries = project.Limits.MaxOperationalRetries;
        if (retries < 0)
            throw new AgentStageInputException("max_operational_retries cannot be negative.");
        var maximumAttempts = project.Limits.MaxAttemptsPerStage;
        cancellationToken.ThrowIfCancellationRequested();

        AgentStageSession session;
        try
        {
            session = await stages.OpenSessionAsync(paths, project, repository, beadId, role,
                expectedAssignee, cancellationToken, expectedState, allowPublication: true).ConfigureAwait(false);
        }
        catch (BeadEnvironmentException exception)
        {
            return await EscalateProvisionFailureAsync(paths, project, repository, beadId, role, expectedAssignee,
                expectedState, 0, exception, cancellationToken).ConfigureAwait(false);
        }
        if (role == AgentRole.Planner && session.InitialState.Stage == "stage:plan-approved")
        {
            try
            {
                var published = await planningPublisher.PublishAsync(session, cancellationToken, releaseRevalidationClaim: expectedState is null);
                return new(OperationalRetryStatus.Succeeded, published, 0, null, null, null);
            }
            catch (Exception exception) when (exception is AgentStageInputException or ToolExecutionException or IOException)
            {
                var failed = new AgentStageResult(role, beadId, "planning-publication", "", null, []);
                return await EscalateDeliveryOutcomeAsync(session, failed, expectedAssignee, 0,
                    exception.Message, null, cancellationToken);
            }
        }
        var attemptCount = 1L;
        var beforeState = session.InitialState;
        var beforeHead = session.Worktree.HeadSha;
        progress?.Invoke(new(OperationalRetryProgressKind.AttemptStarted, attemptCount, maximumAttempts));
        AgentStageResult latest;
        try
        {
            latest = await stages.RunAttemptAsync(session, beforeState, beforeHead, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentInvocationExecutionException exception) when (exception.InnerException is BeadEnvironmentException environmentException)
        {
            return await EscalateProvisionFailureAsync(paths, project, repository, beadId, role, expectedAssignee,
                session.InitialState, 0, environmentException, cancellationToken).ConfigureAwait(false);
        }
        if (latest.Succeeded)
            return await FinalizeSuccessfulStageAsync(session, latest, expectedAssignee,
                releaseClaim: expectedState is null, attempts: attemptCount, cancellationToken: cancellationToken).ConfigureAwait(false);

        var unsafeDeliveryAttempt = await EscalateUnsafeDeliveryAttemptAsync(session, latest,
            expectedAssignee, attemptCount, cancellationToken).ConfigureAwait(false);
        if (unsafeDeliveryAttempt is not null) return unsafeDeliveryAttempt;

        if (ContinuousAgentLaunchContext.StopRequested)
            return StoppedForDrain(latest, attemptCount);

        AgentStageResult previous = latest;
        DuplicateFailureClassificationResult? lastClassification = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContinuousAgentLaunchContext.StopRequested)
                return StoppedForDrain(latest, attemptCount, lastClassification);
            if (attemptCount >= maximumAttempts)
            {
                var findings = RetryFindings(attemptCount, retries, latest, lastClassification,
                    "Operational retry budget exhausted.");
                var cleanupPurpose = await ReadCurrentCleanupPurposeAsync(session, expectedAssignee, attemptCount,
                    cancellationToken).ConfigureAwait(false);
                return await EscalateAsync(session, expectedAssignee, latest, attemptCount,
                    NeedsHumanCause.OperationalRetriesExhausted, findings, lastClassification,
                    cleanupPurpose, cancellationToken).ConfigureAwait(false);
            }

            var current = await ReadAndValidateRetryStateAsync(session, expectedAssignee,
                attemptCount, cancellationToken).ConfigureAwait(false);
            if (current.Refusal is not null)
                return await EscalateEnvironmentBlockAsync(session, expectedAssignee, latest, attemptCount,
                    retries, current.Refusal, lastClassification, current.CleanupPurpose, cancellationToken).ConfigureAwait(false);

            if (attemptCount >= 2)
            {
                if (ContinuousAgentLaunchContext.StopRequested)
                    return StoppedForDrain(latest, attemptCount, lastClassification);
                // Compare adjacent failures with the original stage identity. The current failed
                // worktree and settled logs are still present at this point.
                progress?.Invoke(new(OperationalRetryProgressKind.ClassifierStarted, attemptCount, maximumAttempts,
                    latest.Failure!.Kind, latest.Failure.Reason, InvocationId: latest.InvocationId,
                    LogFilePath: latest.LogFilePath));
                lastClassification = await compare(paths, project, session.DispatchContext,
                    role, session.Worktree.Path, previous, latest, cancellationToken).ConfigureAwait(false);
                progress?.Invoke(new(OperationalRetryProgressKind.ClassifierCompleted, attemptCount, maximumAttempts,
                    latest.Failure!.Kind, latest.Failure.Reason, lastClassification.Verdict, lastClassification.Diagnostic,
                    latest.InvocationId, latest.LogFilePath, lastClassification.ClassifierInvocationId,
                    lastClassification.ClassifierLogFilePath));
                if (lastClassification.Verdict == DuplicateFailureVerdict.Duplicate)
                {
                    var findings = RetryFindings(attemptCount, retries, latest, lastClassification,
                        lastClassification.Diagnostic);
                    var cleanupPurpose = await ReadCurrentCleanupPurposeAsync(session, expectedAssignee,
                        attemptCount, cancellationToken).ConfigureAwait(false);
                    return await EscalateAsync(session, expectedAssignee, latest, attemptCount,
                        NeedsHumanCause.DuplicateFailure, findings, lastClassification,
                        cleanupPurpose, cancellationToken).ConfigureAwait(false);
                }
            }

            // The classifier may take time. Re-read ownership and workflow identity immediately
            // before reset so its verdict never authorizes replay from stale tracker state.
            current = await ReadAndValidateRetryStateAsync(session, expectedAssignee,
                attemptCount, cancellationToken).ConfigureAwait(false);
            if (current.Refusal is not null)
                return await EscalateEnvironmentBlockAsync(session, expectedAssignee, latest, attemptCount,
                    retries, current.Refusal, lastClassification, current.CleanupPurpose, cancellationToken).ConfigureAwait(false);

            ProvisionedWorktree resetWorktree;
            try
            {
                if (!ContinuousAgentLaunchContext.TryBeginExternalOperation(
                        () => session.PreserveInheritedChanges
                            ? resetter.PreserveAsync(paths, project.Name, repository, beadId, session.Purpose, cancellationToken)
                            : reset(paths, project.Name, repository, beadId, session.Purpose, cancellationToken),
                        out var resetTask))
                    return StoppedForDrain(latest, attemptCount, lastClassification);
                progress?.Invoke(new(OperationalRetryProgressKind.RetryPreparing, attemptCount + 1, maximumAttempts,
                    latest.Failure!.Kind, latest.Failure.Reason, InvocationId: latest.InvocationId,
                    LogFilePath: latest.LogFilePath));
                resetWorktree = await resetTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolExecutionException exception)
            {
                return await EscalateEnvironmentBlockAsync(session, expectedAssignee, latest, attemptCount,
                    retries, $"Pre-retry worktree reset failed: {exception.Message}", lastClassification,
                    session.Purpose, cancellationToken).ConfigureAwait(false);
            }

            current = await ReadAndValidateRetryStateAsync(session, expectedAssignee,
                attemptCount, cancellationToken).ConfigureAwait(false);
            if (current.Refusal is not null)
                return await EscalateEnvironmentBlockAsync(session, expectedAssignee, latest, attemptCount,
                    retries, current.Refusal, lastClassification, current.CleanupPurpose, cancellationToken).ConfigureAwait(false);

            beforeState = current.Snapshot.State;
            beforeHead = resetWorktree.HeadSha;
            previous = latest;
            attemptCount++;
            progress?.Invoke(new(OperationalRetryProgressKind.AttemptStarted, attemptCount, maximumAttempts,
                previous.Failure!.Kind, previous.Failure.Reason, InvocationId: previous.InvocationId,
                LogFilePath: previous.LogFilePath));
            try
            {
                latest = await stages.RunAttemptAsync(session, beforeState, beforeHead, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentInvocationExecutionException exception) when (exception.InnerException is BeadEnvironmentException environmentException)
            {
                return await EscalateProvisionFailureAsync(paths, project, repository, beadId, role, expectedAssignee,
                    session.InitialState, attemptCount - 1, environmentException, cancellationToken).ConfigureAwait(false);
            }
            if (latest.Succeeded)
            {
                var finalized = await FinalizeSuccessfulStageAsync(session, latest, expectedAssignee,
                    releaseClaim: expectedState is null, attempts: attemptCount, cancellationToken: cancellationToken).ConfigureAwait(false);
                return finalized with { LastClassification = lastClassification };
            }
            unsafeDeliveryAttempt = await EscalateUnsafeDeliveryAttemptAsync(session, latest,
                expectedAssignee, attemptCount, cancellationToken).ConfigureAwait(false);
            if (unsafeDeliveryAttempt is not null) return unsafeDeliveryAttempt;
        }
    }

    private static OperationalRetryOutcome StoppedForDrain(
        AgentStageResult latest,
        long attempts,
        DuplicateFailureClassificationResult? classification = null) =>
        new(OperationalRetryStatus.StoppedForDrain, latest, attempts, null, null, classification);

    private async Task<OperationalRetryOutcome?> EscalateUnsafeDeliveryAttemptAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        string expectedAssignee,
        long attempts,
        CancellationToken cancellationToken)
    {
        if (!(AgentRoles.IsDeliveryRole(session.Role) || session.Purpose == WorktreePurpose.Planning) || invocation.Failure is null) return null;

        var observations = invocation.Observations;
        var unsafeObservation = observations is null
            || observations.BeforeState != observations.ObservedState
            || !string.Equals(observations.BeforeAssignee, observations.ObservedAssignee, StringComparison.Ordinal)
            || !string.Equals(observations.BeforeAssignee, expectedAssignee, StringComparison.Ordinal);
        var forbiddenFailure = invocation.Failure.Kind is AgentOperationalFailureKind.IllegalBeadTransition
            or AgentOperationalFailureKind.DirtyWorktree
            or AgentOperationalFailureKind.ForbiddenHeadMutation
            or AgentOperationalFailureKind.OutcomeVerificationFailed;
        if (!unsafeObservation && !forbiddenFailure) return null;

        var reason = unsafeObservation
            ? $"{AgentRoles.ToConfigKey(session.Role)} workflow or claim state could not be verified unchanged; automatic retry is unsafe."
            : $"{AgentRoles.ToConfigKey(session.Role)} changed guarded workflow or claim state before outcome classification.";
        var current = await stages.ReadRequestedStateAsync(session.Repository, session.BeadId,
            cancellationToken, allowConflictingPauseLabels: true).ConfigureAwait(false);
        AgentStageCoordinator.EnsureExpectedClaim(current, expectedAssignee, requireInProgress: false);
        if (current.State.Planned)
            throw new AgentStageInputException($"Cannot escalate Bead '{session.BeadId}' after attempt {attempts}: it is planned.");
        var cleanupPurpose = current.State.IssueType switch
        {
            "feature" or "bug" => WorktreePurpose.Planning,
            "task" => WorktreePurpose.Delivery,
            _ => throw new AgentStageInputException($"Cannot safely escalate Bead '{session.BeadId}' with unsupported type '{current.State.IssueType}'.")
        };
        var findings = new[]
        {
            $"Lifecycle attempts completed: {attempts}.",
            $"Latest failure: {invocation.Failure.Kind}: {Limit(invocation.Failure.Reason)}",
            $"Latest invocation: {invocation.InvocationId}; log: {invocation.LogFilePath}.",
            Limit(reason)
        };
        return await EscalateAsync(session, expectedAssignee, invocation, attempts,
            NeedsHumanCause.DeliveryOutcomeFailure, findings, null, cleanupPurpose, cancellationToken,
            expectedWorkflowState: current.State).ConfigureAwait(false);
    }

    private async Task<RetryStateObservation> ReadAndValidateRetryStateAsync(
        AgentStageSession session,
        string expectedAssignee,
        long attempts,
        CancellationToken cancellationToken)
    {
        var snapshot = await stages.ReadRequestedStateAsync(session.Repository, session.BeadId, cancellationToken,
            allowConflictingPauseLabels: true)
            .ConfigureAwait(false);
        AgentStageCoordinator.EnsureExpectedClaim(snapshot, expectedAssignee, requireInProgress: false);
        if (snapshot.State.Planned)
            throw new AgentStageInputException($"Cannot retry Bead '{session.BeadId}' after attempt {attempts}: it is terminal or planned.");

        var changedIdentity = snapshot.State.IssueType != session.InitialState.IssueType
            || snapshot.State.Stage != session.InitialState.Stage
            || snapshot.State.RepairCycles != session.InitialState.RepairCycles;
        var hasPauseLabel = snapshot.State.ProductDecisionRequired || snapshot.State.NeedsHuman;
        if (!changedIdentity && !hasPauseLabel)
            return new(snapshot, null, session.Purpose);

        var refusal = changedIdentity
            ? $"Retry refused because Bead workflow identity changed from type/stage/repair_cycles "
                + $"'{session.InitialState.IssueType}/{session.InitialState.Stage ?? "(none)"}/{session.InitialState.RepairCycles}' "
                + $"to '{snapshot.State.IssueType}/{snapshot.State.Stage ?? "(none)"}/{snapshot.State.RepairCycles}'."
            : "Retry refused because the Bead now has a pause label.";
        var purpose = snapshot.State.IssueType switch
        {
            "feature" or "bug" => WorktreePurpose.Planning,
            "task" => WorktreePurpose.Delivery,
            _ => throw new AgentStageInputException($"Cannot safely escalate Bead '{session.BeadId}' with unsupported type '{snapshot.State.IssueType}'.")
        };
        return new(snapshot, refusal, purpose);
    }

    private async Task<OperationalRetryOutcome> EscalateProvisionFailureAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        AgentRole role,
        string expectedAssignee,
        BeadWorkflowState? expectedState,
        long attemptCount,
        BeadEnvironmentException exception,
        CancellationToken cancellationToken)
    {
        var current = await stages.ReadRequestedStateAsync(repository, beadId, cancellationToken,
            allowConflictingPauseLabels: true).ConfigureAwait(false);
        AgentStageCoordinator.EnsureExpectedClaim(current, expectedAssignee, requireInProgress: true);
        if (expectedState is not null && current.State != expectedState)
            throw new AgentStageInputException($"Bead '{beadId}' workflow state changed before its environment failure could be escalated.");

        var purpose = role is AgentRole.Planner or AgentRole.PlanChallenger
            ? WorktreePurpose.Planning : WorktreePurpose.Delivery;
        var failure = new AgentOperationalFailure(AgentOperationalFailureKind.EnvironmentBlocked, exception.Message);
        var synthetic = new AgentStageResult(role, beadId, "environment-preflight", string.Empty, failure,
            [exception.Message], current.State);
        var request = new NeedsHumanRequest(beadId, expectedAssignee, NeedsHumanCause.EnvironmentBlocked, role,
            $"Could not safely prepare the Bead environment at '{exception.Path}': {exception.Reason}",
            [$"Environment path: {exception.Path}", exception.Reason], CleanupPurpose: purpose,
            ExpectedWorkflowState: current.State);

        NeedsHumanEscalationResult progress;
        try
        {
            progress = await escalate(paths, project, repository, request, cancellationToken).ConfigureAwait(false);
        }
        catch (NeedsHumanEscalationException escalationException)
        {
            var incomplete = new OperationalRetryOutcome(OperationalRetryStatus.Escalated, synthetic, attemptCount,
                NeedsHumanCause.EnvironmentBlocked, escalationException.Progress, null);
            throw new OperationalRetryEscalationIncompleteException(escalationException.Message, incomplete,
                escalationException);
        }
        if (!progress.Succeeded)
        {
            var incomplete = new OperationalRetryOutcome(OperationalRetryStatus.Escalated, synthetic, attemptCount,
                NeedsHumanCause.EnvironmentBlocked, progress, null);
            throw new OperationalRetryEscalationIncompleteException(
                $"Needs-human escalation for unusable environment '{exception.Path}' was incomplete: {string.Join("; ", progress.Failures)}",
                incomplete);
        }
        return new OperationalRetryOutcome(OperationalRetryStatus.Escalated, synthetic, attemptCount,
            NeedsHumanCause.EnvironmentBlocked, progress, null);
    }

    private async Task<OperationalRetryOutcome> EscalateEnvironmentBlockAsync(
        AgentStageSession session,
        string expectedAssignee,
        AgentStageResult latest,
        long attempts,
        int retries,
        string reason,
        DuplicateFailureClassificationResult? classification,
        WorktreePurpose? cleanupPurpose,
        CancellationToken cancellationToken)
    {
        var resolvedPurpose = await ReadCurrentCleanupPurposeAsync(session, expectedAssignee, attempts, cancellationToken)
            .ConfigureAwait(false);
        if (cleanupPurpose is { } explicitPurpose && explicitPurpose != resolvedPurpose)
            throw new AgentStageInputException($"Cannot escalate Bead '{session.BeadId}': its Bead type changed during escalation preflight.");
        var findings = RetryFindings(attempts, retries, latest, classification, reason);
        return await EscalateAsync(session, expectedAssignee, latest, attempts,
            NeedsHumanCause.EnvironmentBlocked, findings, classification, resolvedPurpose, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OperationalRetryOutcome> FinalizeSuccessfulStageAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        string expectedAssignee,
        bool releaseClaim,
        long attempts,
        CancellationToken cancellationToken)
    {
        if (session.Role == AgentRole.Merger)
            return await FinalizeSuccessfulMergerStageAsync(session, invocation, expectedAssignee, attempts,
                cancellationToken).ConfigureAwait(false);
        if (session.Purpose == WorktreePurpose.Planning)
            return await FinalizePlanningAsync(session, invocation, expectedAssignee, releaseClaim, attempts, cancellationToken);
        if (!AgentRoles.IsDeliveryRole(session.Role))
            return Success(invocation, attempts);

        AgentOutcomeClassificationResult classification;
        try
        {
            var comments = await beadsReader.ReadCommentsAsync(session.Repository, session.BeadId, cancellationToken)
                .ConfigureAwait(false);
            classification = await outcomeClassifier.ClassifyAsync(session.HostPaths, session.Project,
                session.DispatchContext, session.Worktree.Path, invocation, comments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            classification = new(null, "classification", $"Outcome classification could not complete ({exception.GetType().Name}).");
        }

        invocation = invocation with { OutcomeClassification = classification };
        var classifierMutation = await outcomeVerifier.VerifyNoClassifierMutationAsync(session.Repository,
            session.Worktree.Path, invocation, expectedAssignee, cancellationToken).ConfigureAwait(false);
        if (classifierMutation is not null)
            return await EscalateDeliveryOutcomeAsync(session, invocation, expectedAssignee, attempts,
                classifierMutation, classification, cancellationToken).ConfigureAwait(false);

        if (classification.Outcome is null)
            return await EscalateDeliveryOutcomeAsync(session, invocation, expectedAssignee, attempts,
                classification.Diagnostic, classification, cancellationToken).ConfigureAwait(false);

        var outcome = classification.Outcome.Value;
        var approval = session.Role switch
        {
            AgentRole.Implementer => outcome == AgentOutcomeChoice.ReadyForReview,
            AgentRole.Reviewer or AgentRole.Qa => outcome == AgentOutcomeChoice.Approved,
            _ => false
        };
        var repair = session.Role is AgentRole.Reviewer or AgentRole.Qa
            && outcome == AgentOutcomeChoice.ChangesRequired;
        var productPause = outcome == AgentOutcomeChoice.ProductDecisionRequired;
        if (!approval && !repair && !productPause)
            return await EscalateDeliveryOutcomeAsync(session, invocation, expectedAssignee, attempts,
                $"{AgentRoles.ToConfigKey(session.Role)} outcome '{outcome}' requires human review: {classification.Diagnostic}",
                classification, cancellationToken).ConfigureAwait(false);

        if (approval)
        {
            var verificationFailure = await outcomeVerifier.VerifyApprovalAsync(session.Repository,
                session.Worktree.Path, invocation, expectedAssignee, session.Project.TargetRepository.DefaultBranch,
                cancellationToken).ConfigureAwait(false);
            if (verificationFailure is not null)
                return await EscalateDeliveryOutcomeAsync(session, invocation, expectedAssignee, attempts,
                    verificationFailure, classification, cancellationToken).ConfigureAwait(false);
        }

        AgentStageResult published;
        try
        {
            published = await outcomePublisher.PublishAsync(session, invocation, classification,
                releaseClaim, cancellationToken).ConfigureAwait(false);
        }
        catch (DeliveryRepairCounterOverflowException exception)
        {
            return await EscalateDeliveryOutcomeAsync(session, invocation, expectedAssignee, attempts,
                exception.Message, classification, cancellationToken).ConfigureAwait(false);
        }
        return Success(published, attempts);
    }

    private async Task<OperationalRetryOutcome> FinalizePlanningAsync(
        AgentStageSession session, AgentStageResult invocation, string actor, bool releaseClaim,
        long attempts, CancellationToken token)
    {
        AgentOutcomeClassificationResult classification;
        var budgetExhausted = false;
        try
        {
            var comments = await beadsReader.ReadCommentsAsync(session.Repository, session.BeadId, token);
            classification = await outcomeClassifier.ClassifyAsync(session.HostPaths, session.Project,
                session.DispatchContext, session.Worktree.Path, invocation, comments, token);
            invocation = invocation with { OutcomeClassification = classification };
            var mutation = await outcomeVerifier.VerifyNoClassifierMutationAsync(session.Repository,
                session.Worktree.Path, invocation, actor, token);
            if (mutation is not null) throw new AgentStageInputException(mutation);
            if (classification.Outcome is not (AgentOutcomeChoice.ReadyForReview or AgentOutcomeChoice.Approved
                or AgentOutcomeChoice.ChangesRequired or AgentOutcomeChoice.ProductDecisionRequired))
                throw new AgentStageInputException(classification.Diagnostic);
            if (invocation.Observations is not { WorktreeClean: true, ProcessStatus: AgentExecutionStatus.Exited, ExitCode: 0 })
                throw new AgentStageInputException("Planning process and clean read-only checkout could not be verified.");
            var grounding = await planningEvidence.CaptureAsync(session, token);
            comments = await beadsReader.ReadCommentsAsync(session.Repository, session.BeadId, token);
            var verified = PlanningEvidence.Verify(invocation, classification.Outcome.Value, grounding, comments);
            if (verified.Error is not null) throw new AgentStageInputException(verified.Error);
            invocation = invocation with { PlanningApproval = verified.Approval, OutcomeClassification = classification };
            if (classification.Outcome == AgentOutcomeChoice.ChangesRequired
                && (session.InitialState.RepairCycles == int.MaxValue
                    || session.InitialState.RepairCycles >= session.Project.Limits.MaxRepairCycles))
            {
                budgetExhausted = true;
                throw new AgentStageInputException("Planning repair budget exhausted; preserve the plan and findings for guarded Retry.");
            }
            var published = await outcomePublisher.PublishAsync(session, invocation, classification, releaseClaim || classification.Outcome == AgentOutcomeChoice.Approved, token);
            return Success(published, attempts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return await EscalateDeliveryOutcomeAsync(session, invocation, actor, attempts,
                $"Planning outcome was not verified: {exception.Message}", invocation.OutcomeClassification, token,
                budgetExhausted ? NeedsHumanCause.RepairBudgetExhausted : NeedsHumanCause.DeliveryOutcomeFailure);
        }
    }

    private async Task<OperationalRetryOutcome> FinalizeSuccessfulMergerStageAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        string expectedAssignee,
        long attempts,
        CancellationToken cancellationToken)
    {
        AgentOutcomeClassificationResult classification;
        IReadOnlyList<BeadCommentSnapshot> comments;
        try
        {
            comments = await beadsReader.ReadCommentsAsync(session.Repository, session.BeadId, cancellationToken)
                .ConfigureAwait(false);
            classification = await outcomeClassifier.ClassifyAsync(session.HostPaths, session.Project,
                session.DispatchContext, session.Worktree.Path, invocation, comments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            classification = new(null, "classification",
                $"Merger outcome classification could not complete ({exception.GetType().Name}).");
            comments = [];
        }

        invocation = invocation with { OutcomeClassification = classification };
        var classifierMutation = await outcomeVerifier.VerifyNoClassifierMutationAsync(session.Repository,
            session.Worktree.Path, invocation, expectedAssignee, cancellationToken).ConfigureAwait(false);
        if (classifierMutation is not null)
            return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                classifierMutation, classification, NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken)
                .ConfigureAwait(false);

        if (classification.Outcome is null)
            return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                classification.Diagnostic, classification, NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken)
                .ConfigureAwait(false);

        if (classification.Outcome == AgentOutcomeChoice.Approved)
        {
            var approvalVerification = await mergerOutcomeVerifier.VerifyApprovalAsync(session, invocation,
                classification, comments, expectedAssignee, cancellationToken).ConfigureAwait(false);
            if (!approvalVerification.Succeeded || approvalVerification.Evidence is null)
                return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                    approvalVerification.Diagnostic, classification, NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken)
                    .ConfigureAwait(false);

            MergerCompletionResult completion;
            try
            {
                completion = await mergerCompletionFinalizer.CompleteAsync(session, invocation, expectedAssignee,
                    approvalVerification.Evidence, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                completion = new(false, null, null, $"Verified merge could not be finalized: {exception.Message}");
            }
            if (!completion.Succeeded || completion.FinalState is null || completion.Receipt is null)
                return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                    completion.Diagnostic ?? "Verified merge task closure could not be confirmed.", classification,
                    NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken).ConfigureAwait(false);
            return Success(invocation with
            {
                VerifiedState = completion.FinalState,
                MergerCompletion = completion.Receipt,
                Diagnostics = completion.Diagnostic is null ? invocation.Diagnostics : invocation.Diagnostics.Append(completion.Diagnostic).ToArray()
            }, attempts);
        }

        if (classification.Outcome == AgentOutcomeChoice.ProductDecisionRequired)
        {
            try
            {
                var paused = await outcomePublisher.PublishAsync(session, invocation, classification,
                    releaseClaim: true, cancellationToken).ConfigureAwait(false);
                return Success(paused, attempts);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                    $"Guarded product-decision pause publication was not confirmed: {exception.Message}", classification,
                    NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken).ConfigureAwait(false);
            }
        }

        if (classification.Outcome != AgentOutcomeChoice.ChangesRequired)
            return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                $"Merger outcome '{classification.Outcome}' does not establish an actionable rejection: {classification.Diagnostic}",
                classification, NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken).ConfigureAwait(false);

        var verification = await mergerOutcomeVerifier.VerifyRejectionAsync(session.HostPaths, session.Project,
            session, invocation, classification, comments, expectedAssignee, cancellationToken).ConfigureAwait(false);
        if (!verification.Succeeded)
            return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                verification.Diagnostic, classification, NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken)
                .ConfigureAwait(false);

        int nextRepairCycles;
        try { nextRepairCycles = checked(invocation.Observations!.BeforeState.RepairCycles + 1); }
        catch (OverflowException)
        {
            return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                "Verified Merger rejection cannot increment repair_cycles without overflowing a 32-bit integer.",
                classification, NeedsHumanCause.RepairBudgetExhausted, cancellationToken).ConfigureAwait(false);
        }

        var releaseClaim = nextRepairCycles <= session.Project.Limits.MaxRepairCycles;
        AgentStageResult published;
        try
        {
            published = await outcomePublisher.PublishAsync(session, invocation, classification,
                releaseClaim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return await EscalateMergerOutcomeAsync(session, invocation, expectedAssignee, attempts,
                $"Verified Merger rejection publication was not confirmed: {exception.Message}", classification,
                NeedsHumanCause.DeliveryOutcomeFailure, cancellationToken).ConfigureAwait(false);
        }

        if (releaseClaim) return Success(published, attempts);
        return await EscalatePublishedMergerBudgetAsync(session, published, expectedAssignee, attempts,
            verification.Finding, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationalRetryOutcome> EscalateMergerOutcomeAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        string expectedAssignee,
        long attempts,
        string reason,
        AgentOutcomeClassificationResult? classification,
        NeedsHumanCause cause,
        CancellationToken cancellationToken)
    {
        var current = await stages.ReadRequestedStateAsync(session.Repository, session.BeadId, cancellationToken,
            allowConflictingPauseLabels: true).ConfigureAwait(false);
        AgentStageCoordinator.EnsureExpectedClaim(current, expectedAssignee, requireInProgress: false);
        var expected = invocation.Observations?.BeforeState ?? session.InitialState;
        if (current.State != expected)
            throw new AgentStageInputException(
                $"Cannot escalate Merger outcome for '{session.BeadId}': workflow state changed after verification.");

        var failure = new AgentOperationalFailure(AgentOperationalFailureKind.OutcomeVerificationFailed, reason);
        var failed = invocation with
        {
            Failure = failure,
            OutcomeClassification = classification,
            Diagnostics = invocation.Diagnostics.Append(reason).ToArray()
        };
        var findings = new[]
        {
            $"Merger invocation: {invocation.InvocationId}; log: {invocation.LogFilePath}.",
            $"Outcome classifier: {classification?.Provider ?? "unavailable"}; outcome: {classification?.Outcome?.ToString() ?? "unavailable"}.",
            reason
        };
        var request = new NeedsHumanRequest(session.BeadId, expectedAssignee, cause, AgentRole.Merger,
            reason, findings, invocation.InvocationId, [invocation.LogFilePath], WorktreePurpose.Delivery, expected);
        return await RunMergerEscalationAsync(session, failed, attempts, cause, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OperationalRetryOutcome> EscalatePublishedMergerBudgetAsync(
        AgentStageSession session,
        AgentStageResult published,
        string expectedAssignee,
        long attempts,
        MergerRejectionFinding? finding,
        CancellationToken cancellationToken)
    {
        var state = published.VerifiedState
            ?? throw new AgentStageInputException("Budget escalation requires the verified Merger repair state.");
        var reason = $"Verified Merger rejection raised repair_cycles to {state.RepairCycles}, exceeding max_repair_cycles={session.Project.Limits.MaxRepairCycles}.";
        var findings = new List<string>
        {
            $"Merger invocation: {published.InvocationId}; log: {published.LogFilePath}.",
            finding is null ? "Current actionable rejection evidence was verified." :
                $"Rejection finding ({finding.Kind}) from comment {finding.SourceCommentId}: {finding.Subject}: {finding.Summary} {finding.Details}",
            reason
        };
        var request = new NeedsHumanRequest(session.BeadId, expectedAssignee, NeedsHumanCause.RepairBudgetExhausted,
            AgentRole.Merger, reason, findings, published.InvocationId, [published.LogFilePath],
            WorktreePurpose.Delivery, state);
        return await RunMergerEscalationAsync(session, published, attempts,
            NeedsHumanCause.RepairBudgetExhausted, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationalRetryOutcome> RunMergerEscalationAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        long attempts,
        NeedsHumanCause cause,
        NeedsHumanRequest request,
        CancellationToken cancellationToken)
    {
        NeedsHumanEscalationResult progress;
        try
        {
            progress = await escalate(session.HostPaths, session.Project, session.Repository, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NeedsHumanEscalationException exception)
        {
            throw IncompleteEscalation(exception.Message, invocation, attempts, cause, exception.Progress,
                classification: null, innerException: exception);
        }
        if (!progress.Succeeded)
            throw IncompleteEscalation(
                $"Merger needs-human escalation for '{session.BeadId}' was incomplete: {string.Join("; ", progress.Failures)}",
                invocation, attempts, cause, progress, classification: null);
        return new(OperationalRetryStatus.Escalated, invocation, attempts, cause, progress, null);
    }

    private async Task<OperationalRetryOutcome> EscalateDeliveryOutcomeAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        string expectedAssignee,
        long attempts,
        string reason,
        AgentOutcomeClassificationResult? classification,
        CancellationToken cancellationToken,
        NeedsHumanCause cause = NeedsHumanCause.DeliveryOutcomeFailure)
    {
        var failure = new AgentOperationalFailure(AgentOperationalFailureKind.OutcomeVerificationFailed, reason);
        var withFailure = invocation with
        {
            Failure = failure,
            OutcomeClassification = classification,
            Diagnostics = invocation.Diagnostics.Append(reason).ToArray()
        };
        var findings = new[]
        {
            $"{AgentRoles.ToConfigKey(session.Role)} invocation: {invocation.InvocationId}; log: {invocation.LogFilePath}.",
            $"Outcome classifier: {classification?.Provider ?? "unavailable"}; outcome: {classification?.Outcome?.ToString() ?? "unavailable"}.",
            reason
        };
        var current = await stages.ReadRequestedStateAsync(session.Repository, session.BeadId,
            cancellationToken, allowConflictingPauseLabels: true).ConfigureAwait(false);
        AgentStageCoordinator.EnsureExpectedClaim(current, expectedAssignee, requireInProgress: false);
        if (current.State.Planned)
            throw new AgentStageInputException($"Cannot escalate Bead '{session.BeadId}' after attempt {attempts}: it is planned.");
        var cleanupPurpose = current.State.IssueType switch
        {
            "feature" or "bug" => WorktreePurpose.Planning,
            "task" => WorktreePurpose.Delivery,
            _ => throw new AgentStageInputException($"Cannot safely escalate Bead '{session.BeadId}' with unsupported type '{current.State.IssueType}'.")
        };
        return await EscalateAsync(session, expectedAssignee, withFailure, attempts,
            cause, findings, null, cleanupPurpose,
            cancellationToken, expectedWorkflowState: current.State).ConfigureAwait(false);
    }

    private async Task<WorktreePurpose> ReadCurrentCleanupPurposeAsync(
        AgentStageSession session,
        string expectedAssignee,
        long attempts,
        CancellationToken cancellationToken)
    {
        var current = await stages.ReadRequestedStateAsync(session.Repository, session.BeadId, cancellationToken,
            allowConflictingPauseLabels: true).ConfigureAwait(false);
        try { AgentStageCoordinator.EnsureExpectedClaim(current, expectedAssignee, requireInProgress: false); }
        catch (AgentStageInputException exception)
        { throw new AgentStageInputException($"Cannot escalate Bead '{session.BeadId}' after attempt {attempts}: {exception.Message}"); }
        if (current.State.Planned)
            throw new AgentStageInputException($"Cannot escalate Bead '{session.BeadId}' after attempt {attempts}: it is planned.");
        return current.State.IssueType switch
        {
            "feature" or "bug" => WorktreePurpose.Planning,
            "task" => WorktreePurpose.Delivery,
            _ => throw new AgentStageInputException($"Cannot safely escalate Bead '{session.BeadId}' with unsupported type '{current.State.IssueType}'.")
        };
    }

    private async Task<OperationalRetryOutcome> EscalateAsync(
        AgentStageSession session,
        string expectedAssignee,
        AgentStageResult latest,
        long attempts,
        NeedsHumanCause cause,
        IReadOnlyList<string> findings,
        DuplicateFailureClassificationResult? classification,
        WorktreePurpose cleanupPurpose,
        CancellationToken cancellationToken,
        BeadWorkflowState? expectedWorkflowState = null)
    {
        var failure = latest.Failure!;
        var logPaths = new[]
        {
            latest.LogFilePath,
            classification?.PreviousLogFilePath,
            classification?.LatestLogFilePath,
            classification?.ClassifierLogFilePath
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        var reason = cause switch
        {
            NeedsHumanCause.OperationalRetriesExhausted => $"Operational retries exhausted after {attempts} attempt(s): {failure.Kind}.",
            NeedsHumanCause.DuplicateFailure => $"Duplicate operational failure detected after {attempts} attempt(s): {failure.Kind}.",
            NeedsHumanCause.EnvironmentBlocked => $"Operational retry environment is blocked after {attempts} attempt(s): {failure.Kind}.",
            NeedsHumanCause.RepairBudgetExhausted => failure.Reason,
            NeedsHumanCause.DeliveryOutcomeFailure => $"Delivery outcome was not verified after {attempts} completed attempt(s): {failure.Reason}.",
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unsupported retry escalation cause")
        };
        var request = new NeedsHumanRequest(session.BeadId, expectedAssignee, cause, session.Role,
            reason, findings, latest.InvocationId, logPaths, cleanupPurpose,
            expectedWorkflowState ?? session.InitialState);
        NeedsHumanEscalationResult progress;
        try
        {
            progress = await escalate(session.HostPaths, session.Project, session.Repository, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NeedsHumanEscalationException exception)
        {
            throw IncompleteEscalation(exception.Message, latest, attempts, cause, exception.Progress,
                classification, exception);
        }
        catch (OperationCanceledException exception) when (TryGetEscalationProgress(exception, out var observed))
        {
            throw IncompleteEscalation(exception.Message, latest, attempts, cause, observed, classification,
                exception);
        }
        if (!progress.Succeeded)
            throw IncompleteEscalation(
                $"Needs-human escalation for '{session.BeadId}' was incomplete after invocation '{latest.InvocationId}' "
                + $"(log '{latest.LogFilePath}'): {string.Join("; ", progress.Failures)}",
                latest, attempts, cause, progress, classification);
        return new(OperationalRetryStatus.Escalated, latest, attempts, cause, progress, classification);
    }

    private static OperationalRetryEscalationIncompleteException IncompleteEscalation(
        string message,
        AgentStageResult latest,
        long attempts,
        NeedsHumanCause cause,
        NeedsHumanEscalationResult progress,
        DuplicateFailureClassificationResult? classification,
        Exception? innerException = null)
    {
        var outcome = new OperationalRetryOutcome(OperationalRetryStatus.Escalated, latest, attempts, cause,
            progress, classification);
        return innerException is null
            ? new OperationalRetryEscalationIncompleteException(message, outcome)
            : new OperationalRetryEscalationIncompleteException(message, outcome, innerException);
    }

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

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> RetryFindings(
        long attempts,
        int retries,
        AgentStageResult latest,
        DuplicateFailureClassificationResult? classification,
        string detail)
    {
        var findings = new List<string>
        {
            $"Lifecycle attempts completed: {attempts}.",
            $"Configured max_operational_retries: {retries}.",
            $"Latest failure: {latest.Failure!.Kind}: {Limit(latest.Failure.Reason)}",
            $"Latest invocation: {latest.InvocationId}; log: {latest.LogFilePath}.",
            Limit(detail)
        };
        if (classification is not null)
            findings.Add($"Last classifier verdict: {classification.Verdict}; reason: {Limit(classification.Diagnostic)}"
                + (classification.ClassifierLogFilePath is null ? string.Empty : $"; log: {classification.ClassifierLogFilePath}"));
        foreach (var diagnostic in latest.Diagnostics.Take(3))
            findings.Add($"Invocation diagnostic: {Limit(diagnostic)}");
        return findings.AsReadOnly();
    }

    private static string Limit(string value) => value.Length <= MaxFindingCharacters
        ? value
        : value[..MaxFindingCharacters] + "…";

    private static OperationalRetryOutcome Success(AgentStageResult result, long attempts) =>
        new(OperationalRetryStatus.Succeeded, result, attempts, null, null, null);

    private sealed record RetryStateObservation(
        (BeadSnapshot Bead, BeadWorkflowState State) Snapshot,
        string? Refusal,
        WorktreePurpose CleanupPurpose);
}
