using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal enum OperationalRetryStatus
{
    Succeeded,
    Escalated
}

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
    internal OperationalRetryEscalationIncompleteException(string message, NeedsHumanEscalationResult progress)
        : base(message) => Progress = progress;

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
            CancellationToken, Task<NeedsHumanEscalationResult>>? escalate = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        this.stages = stages ?? new AgentStageCoordinator(processRunner);
        this.classifier = classifier ?? new DuplicateFailureClassifier();
        this.escalator = escalator ?? new NeedsHumanEscalator(processRunner);
        this.resetter = resetter ?? new WorktreeRetryResetter(processRunner);
        this.reset = reset ?? this.resetter.ResetAsync;
        this.compare = compare ?? this.classifier.CompareAsync;
        this.escalate = escalate ?? this.escalator.EscalateAsync;
    }

    internal async Task<OperationalRetryOutcome> RunAsync(
        HostPaths paths,
        ProjectConfig project,
        ValidatedTargetRepository repository,
        string beadId,
        AgentRole role,
        string expectedAssignee,
        CancellationToken cancellationToken)
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

        var session = await stages.OpenSessionAsync(paths, project, repository, beadId, role,
            expectedAssignee, cancellationToken).ConfigureAwait(false);
        var attemptCount = 1L;
        var beforeState = session.InitialState;
        var beforeHead = session.Worktree.HeadSha;
        var latest = await stages.RunAttemptAsync(session, beforeState, beforeHead, cancellationToken).ConfigureAwait(false);
        if (latest.Succeeded)
            return Success(latest, attemptCount);

        AgentStageResult previous = latest;
        DuplicateFailureClassificationResult? lastClassification = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                // Compare adjacent failures with the original stage identity. The current failed
                // worktree and settled logs are still present at this point.
                lastClassification = await compare(paths, project, session.DispatchContext,
                    role, session.Worktree.Path, previous, latest, cancellationToken).ConfigureAwait(false);
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
                resetWorktree = await reset(paths, project.Name, repository, beadId, session.Purpose, cancellationToken)
                    .ConfigureAwait(false);
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
            latest = await stages.RunAttemptAsync(session, beforeState, beforeHead, cancellationToken).ConfigureAwait(false);
            if (latest.Succeeded)
                return new(OperationalRetryStatus.Succeeded, latest, attemptCount, null, null, lastClassification);
        }
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
        CancellationToken cancellationToken)
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
            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unsupported retry escalation cause")
        };
        var request = new NeedsHumanRequest(session.BeadId, expectedAssignee, cause, session.Role,
            reason, findings, latest.InvocationId, logPaths, cleanupPurpose);
        var progress = await escalate(session.HostPaths, session.Project, session.Repository, request, cancellationToken)
            .ConfigureAwait(false);
        if (!progress.Succeeded)
            throw new OperationalRetryEscalationIncompleteException(
                $"Needs-human escalation for '{session.BeadId}' was incomplete: {string.Join("; ", progress.Failures)}", progress);
        return new(OperationalRetryStatus.Escalated, latest, attempts, cause, progress, classification);
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
