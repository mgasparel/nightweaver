using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Application;

internal delegate Task<OperationalRetryOutcome> DispatchStageRunner(
    HostPaths paths,
    ProjectConfig project,
    ValidatedTargetRepository repository,
    string beadId,
    AgentRole role,
    string expectedAssignee,
    CancellationToken cancellationToken,
    Action<OperationalRetryProgress> progress);

/// <summary>Runs one explicitly selected lifecycle stage for one ready, unassigned Bead.</summary>
internal static class DispatchApplication
{
    private static readonly TimeSpan ClaimFinalizationTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<int> RunAsync(
        CliOptions options,
        AppEnvironment environment,
        IProcessRunner processRunner,
        CancellationToken cancellationToken,
        DispatchStageRunner? runStage = null)
    {
        var dispatch = options.Dispatch ?? throw new InvalidOperationException("Dispatch options are required");
        var beadId = dispatch.BeadId ?? throw new InvalidOperationException("Dispatch Bead ID is required outside help");
        var selector = options.Selector ?? throw new InvalidOperationException("A Project selector is required for dispatch");
        cancellationToken.ThrowIfCancellationRequested();

        var hostPaths = new HostPaths(environment.NightweaverRoot);
        string configPath;
        try
        {
            configPath = ProjectConfigLocator.ResolveConfigPath(selector, hostPaths, environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var rawPath = selector is ProjectSelector.ByConfigPath byConfig ? byConfig.RawPath : hostPaths.Root;
            throw new ProjectConfigurationException(exception.Message, null, rawPath);
        }

        var expectedName = selector is ProjectSelector.BySlug bySlug ? bySlug.Slug : (ProjectSlug?)null;
        var project = ProjectConfigLoader.Load(configPath, expectedName, hostPaths);
        var repository = await new TargetRepositoryValidator(processRunner)
            .ValidateAsync(project.TargetRepository, project.ConfigFilePath, cancellationToken).ConfigureAwait(false);
        var beadsReader = new BeadsReader(processRunner);
        var inventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        var readiness = await beadsReader.ReadReadinessAsync(repository, inventory, project.RecoveryActor, cancellationToken).ConfigureAwait(false);

        if (!inventory.ById.TryGetValue(beadId, out var bead))
            return Fail(environment, $"Bead '{beadId}' was not found in Target Repository '{repository.Path}'.");

        BeadWorkflowState initialState;
        try { initialState = BeadWorkflowState.Parse(bead); }
        catch (FormatException exception)
        { return Fail(environment, $"Bead '{beadId}' has invalid workflow state: {exception.Message}"); }

        AgentRole role;
        var roleSource = dispatch.ExplicitRole is null ? "inferred" : "explicit";
        if (dispatch.ExplicitRole is { } explicitRole)
        {
            role = explicitRole;
        }
        else if (!AuthorityMatrix.TryInferLifecycleRole(initialState, out role, out var reason))
        {
            return Fail(environment, reason!);
        }

        var inputReason = AuthorityMatrix.ValidateInput(role, initialState);
        if (inputReason is not null) return Fail(environment, inputReason);
        if (bead.Status != "open" || bead.Assignee is not null)
            return Fail(environment, $"Bead '{beadId}' must be open and unassigned before dispatch; observed status '{bead.Status}' and assignee '{bead.Assignee ?? "(none)"}'.");
        if (!readiness.NativeUnassignedReady.Contains(beadId))
            return Fail(environment, $"Bead '{beadId}' is not in bd's native unassigned ready set; it may be blocked or deferred.");

        var writer = new BeadsWriter(processRunner);
        var claimWriteStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            claimWriteStarted = true;
            await writer.AcquireDispatchClaimAsync(repository, beadId, project.RecoveryActor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(environment, claimWriteStarted
                ? $"Cancelled while acquiring the claim for Bead '{beadId}'; the guarded write may have completed, so inspect its owner and status before retrying."
                : $"Cancelled before acquiring the claim for Bead '{beadId}'.");
        }
        catch (ToolExecutionException exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return Fail(environment, $"Cancelled while acquiring the claim for Bead '{beadId}'; the guarded write may have completed, so inspect its owner and status before retrying. Details: {exception.Message}");
            return Fail(environment, $"Could not acquire dispatch claim for Bead '{beadId}': {exception.Message}");
        }

        BeadSnapshot? claimedBead = null;
        var handedToCoordinator = false;
        var coordinatorReturned = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimedInventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
            var claimedReadiness = await beadsReader.ReadReadinessAsync(repository, claimedInventory, project.RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            if (!claimedInventory.ById.TryGetValue(beadId, out claimedBead))
                return await RefuseAfterClaimAsync(environment, beadsReader, writer, repository, beadId, project.RecoveryActor,
                    null, "Bead disappeared after guarded claim acquisition.").ConfigureAwait(false);

            string? refusal = null;
            BeadWorkflowState? claimedState = null;
            try { claimedState = BeadWorkflowState.Parse(claimedBead); }
            catch (FormatException exception) { refusal = $"Bead workflow became invalid after claim acquisition: {exception.Message}"; }

            if (refusal is null && (claimedBead.Status != "in_progress"
                || !string.Equals(claimedBead.Assignee, project.RecoveryActor, StringComparison.Ordinal)))
                refusal = $"Guarded claim verification failed: observed status '{claimedBead.Status}' and assignee '{claimedBead.Assignee ?? "(none)"}'.";
            if (refusal is null && claimedState != (initialState with { Status = "in_progress" }))
                refusal = "Bead type, stage, pause labels, or repair_cycles changed during claim acquisition.";
            if (refusal is null && !claimedReadiness.RecoverableOwnedReady.Contains(beadId))
                refusal = "Bead is no longer in bd's recoverable-ready set after claim acquisition; it may have become blocked or deferred.";
            if (refusal is not null)
                return await RefuseAfterClaimAsync(environment, beadsReader, writer, repository, beadId, project.RecoveryActor,
                    claimedBead, refusal).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            environment.Out.WriteLine($"Dispatch starting: project '{project.Name.Value}', Bead '{beadId}', role '{AgentRoles.ToConfigKey(role)}' ({roleSource}).");
            var coordinator = runStage ?? CreateCoordinatorRunner(processRunner);
            handedToCoordinator = true;
            var outcome = await coordinator(hostPaths, project, repository, beadId, role, project.RecoveryActor,
                cancellationToken, progress => WriteProgress(environment, beadId, progress)).ConfigureAwait(false);
            coordinatorReturned = true;

            if (outcome.Status == OperationalRetryStatus.Escalated)
            {
                var failure = outcome.StageResult.Failure;
                environment.Err.WriteLine($"nightweaver: Escalated to needs-human for Bead '{beadId}' after {outcome.AttemptCount} attempt(s) "
                    + $"({outcome.EscalationCause}); latest failure: {failure?.Kind}: {failure?.Reason}; "
                    + $"invocation '{outcome.StageResult.InvocationId}'");
                WriteLogReference(environment.Err, outcome.StageResult.LogFilePath);
                return ExitCodes.InspectionFailure;
            }

            var verifiedState = outcome.StageResult.VerifiedState;
            if (!outcome.Succeeded || !outcome.StageResult.Succeeded || verifiedState is null)
                return Fail(environment, $"Dispatch coordinator returned no verified successful state for Bead '{beadId}'.");

            await ReleaseSuccessfulClaimAsync(beadsReader, writer, repository, beadId, project.RecoveryActor,
                role, verifiedState, cancellationToken).ConfigureAwait(false);
            var verifiedLabel = verifiedState.Status == "closed" ? "closed"
                : verifiedState.Planned ? "planned"
                : verifiedState.ProductDecisionRequired ? "product-decision-required"
                : verifiedState.Stage ?? "(no stage label)";
            environment.Out.WriteLine($"Succeeded: Bead '{beadId}' completed role '{AgentRoles.ToConfigKey(role)}', verified state '{verifiedLabel}', and its claim was handed off.");
            WriteLogReference(environment.Out, outcome.StageResult.LogFilePath);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (handedToCoordinator)
                return Fail(environment, coordinatorReturned
                    ? $"Cancelled during claim handoff for Bead '{beadId}'; the release outcome is uncertain, so inspect its owner and status."
                    : $"Cancelled while dispatching Bead '{beadId}'; the coordinator stopped further retries and follow-on work. Inspect the current Bead owner, runtime, and log state before retrying.");
            if (claimWriteStarted)
                return await CancelBeforeCoordinatorAsync(environment, beadsReader, writer, repository, beadId,
                    project.RecoveryActor).ConfigureAwait(false);
            return Fail(environment, $"Cancelled before acquiring the claim for Bead '{beadId}'.");
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (handedToCoordinator)
                    return Fail(environment, coordinatorReturned
                        ? $"Cancelled during claim handoff for Bead '{beadId}'; the release outcome is uncertain, so inspect its owner and status."
                        : $"Cancelled while dispatching Bead '{beadId}'; the coordinator stopped further retries and follow-on work. Inspect the current Bead owner, runtime, and log state before retrying.");
                if (claimWriteStarted)
                    return await CancelBeforeCoordinatorAsync(environment, beadsReader, writer, repository, beadId,
                        project.RecoveryActor).ConfigureAwait(false);
                return Fail(environment, $"Cancelled before acquiring the claim for Bead '{beadId}'.");
            }
            if (!handedToCoordinator)
                return await RefuseAfterClaimAsync(environment, beadsReader, writer, repository, beadId,
                    project.RecoveryActor, claimedBead, $"dispatch preflight failed: {exception.Message}").ConfigureAwait(false);
            var context = handedToCoordinator
                ? $"stage execution or claim handoff failed for Bead '{beadId}'"
                : $"dispatch preflight failed for Bead '{beadId}'";
            return Fail(environment, $"{context}: {exception.Message}");
        }
    }

    private static async Task ReleaseSuccessfulClaimAsync(BeadsReader reader, BeadsWriter writer,
        ValidatedTargetRepository repository, string beadId, string actor, AgentRole role,
        BeadWorkflowState verifiedState, CancellationToken cancellationToken)
    {
        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        if (!inventory.ById.TryGetValue(beadId, out var current))
            throw new ToolExecutionException($"Bead '{beadId}' disappeared before claim handoff; its execution log is retained.");

        var currentState = BeadWorkflowState.Parse(current);
        if (currentState != verifiedState)
            throw new ToolExecutionException($"Bead '{beadId}' workflow changed after stage verification; refusing to release a stale claim.");

        if (current.Assignee is null)
        {
            if (role == AgentRole.Merger && current.Status == "closed") return;
            throw new ToolExecutionException($"Bead '{beadId}' claim ownership changed before handoff; observed it unassigned.");
        }
        if (!string.Equals(current.Assignee, actor, StringComparison.Ordinal))
            throw new ToolExecutionException($"Bead '{beadId}' claim ownership changed before handoff to '{current.Assignee}'.");
        if (current.Status is not ("open" or "in_progress" or "closed"))
            throw new ToolExecutionException($"Bead '{beadId}' has unsupported status '{current.Status}' at claim handoff.");
        if (current.Status == "closed" && role != AgentRole.Merger)
            throw new ToolExecutionException($"Only a verified Merger closure may release a closed Bead claim ('{beadId}').");

        await writer.ReleaseDispatchClaimAsync(repository, beadId, actor, current.Status, cancellationToken).ConfigureAwait(false);
        var released = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        if (!released.ById.TryGetValue(beadId, out var finalBead))
            throw new ToolExecutionException($"Bead '{beadId}' disappeared after claim handoff.");
        var expectedStatus = current.Status == "closed" ? "closed" : "open";
        if (finalBead.Assignee is not null || finalBead.Status != expectedStatus)
            throw new ToolExecutionException($"Guarded claim handoff for Bead '{beadId}' did not verify; observed status '{finalBead.Status}' and assignee '{finalBead.Assignee ?? "(none)"}'.");
        var finalState = BeadWorkflowState.Parse(finalBead);
        if (finalState != (verifiedState with { Status = expectedStatus }))
            throw new ToolExecutionException($"Bead '{beadId}' workflow changed during claim handoff; refusing to report success.");
    }

    private static async Task<int> RefuseAfterClaimAsync(AppEnvironment environment, BeadsReader reader, BeadsWriter writer,
        ValidatedTargetRepository repository, string beadId, string actor, BeadSnapshot? claimedBead, string reason)
    {
        string? releaseFailure = null;
        using var timeout = new CancellationTokenSource(ClaimFinalizationTimeout);
        if (claimedBead is null)
        {
            try
            {
                var fresh = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                fresh.ById.TryGetValue(beadId, out claimedBead);
            }
            catch (Exception exception) { releaseFailure = $"claim owner could not be reread: {exception.Message}"; }
        }

        if (releaseFailure is null && claimedBead is not null
            && string.Equals(claimedBead.Assignee, actor, StringComparison.Ordinal)
            && claimedBead.Status is "open" or "in_progress" or "closed")
        {
            try
            {
                var expectedStatus = claimedBead.Status;
                await writer.ReleaseDispatchClaimAsync(repository, beadId, actor, expectedStatus, timeout.Token).ConfigureAwait(false);
                var released = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
                var expectedReleasedStatus = expectedStatus == "in_progress" ? "open" : expectedStatus;
                if (!released.ById.TryGetValue(beadId, out var finalBead) || finalBead.Assignee is not null
                    || finalBead.Status != expectedReleasedStatus)
                    releaseFailure = "guarded release did not verify against the fresh Beads inventory";
            }
            catch (Exception exception) { releaseFailure = exception.Message; }
        }
        else if (releaseFailure is null && claimedBead is null)
        {
            releaseFailure = "the Bead is missing; claim ownership could not be verified for release";
        }
        else if (releaseFailure is null)
        {
            releaseFailure = $"claim is no longer owned by '{actor}' (observed '{claimedBead?.Assignee ?? "(none)"}'); it was left untouched";
        }

        return Fail(environment, releaseFailure is null ? reason : $"{reason} Claim release: {releaseFailure}.");
    }

    private static async Task<int> CancelBeforeCoordinatorAsync(AppEnvironment environment, BeadsReader reader,
        BeadsWriter writer, ValidatedTargetRepository repository, string beadId, string actor)
    {
        using var timeout = new CancellationTokenSource(ClaimFinalizationTimeout);
        try
        {
            var inventory = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
            if (!inventory.ById.TryGetValue(beadId, out var bead)
                || !string.Equals(bead.Assignee, actor, StringComparison.Ordinal))
                return Fail(environment, $"Cancelled before stage execution for Bead '{beadId}'; claim ownership could not be confirmed, so inspect it manually.");
            if (bead.Status is "open" or "in_progress" or "closed")
                await writer.ReleaseDispatchClaimAsync(repository, beadId, actor, bead.Status, timeout.Token).ConfigureAwait(false);
            var released = await reader.ReadSnapshotAsync(repository, timeout.Token).ConfigureAwait(false);
            var expectedStatus = bead.Status == "in_progress" ? "open" : bead.Status;
            if (!released.ById.TryGetValue(beadId, out var after) || after.Assignee is not null || after.Status != expectedStatus)
                return Fail(environment, $"Cancelled before stage execution for Bead '{beadId}'; guarded release did not verify, so inspect its owner and status.");
            return Fail(environment, $"Cancelled before stage execution for Bead '{beadId}'; its confirmed claim was released.");
        }
        catch (Exception exception)
        {
            return Fail(environment, $"Cancelled before stage execution for Bead '{beadId}'; claim release outcome is uncertain: {exception.Message}");
        }
    }

    private static DispatchStageRunner CreateCoordinatorRunner(IProcessRunner processRunner)
    {
        var coordinator = new OperationalRetryCoordinator(processRunner);
        return (paths, project, repository, beadId, role, actor, token, progress) =>
            coordinator.RunAsync(paths, project, repository, beadId, role, actor, token, progress);
    }

    private static void WriteProgress(AppEnvironment environment, string beadId,
        OperationalRetryProgress progress)
    {
        switch (progress.Kind)
        {
            case OperationalRetryProgressKind.AttemptStarted:
                environment.Out.WriteLine($"Attempt {progress.Attempt}/{progress.MaximumAttempts} started for Bead '{beadId}'.");
                break;
            case OperationalRetryProgressKind.RetryPreparing:
                environment.Out.WriteLine($"Retry {progress.Attempt}/{progress.MaximumAttempts} after {progress.FailureKind}: {progress.Reason}; "
                    + $"failed invocation '{progress.InvocationId}', log '{progress.LogFilePath}'");
                break;
            case OperationalRetryProgressKind.ClassifierStarted:
                environment.Out.WriteLine($"Classifier comparison starting after attempt {progress.Attempt} for Bead '{beadId}' "
                    + $"against failed invocation '{progress.InvocationId}' (log '{progress.LogFilePath}').");
                break;
            case OperationalRetryProgressKind.ClassifierCompleted:
                environment.Out.WriteLine($"Classifier verdict after attempt {progress.Attempt}: {progress.ClassifierVerdict}; {progress.ClassifierDiagnostic}; "
                    + $"classifier invocation '{progress.ClassifierInvocationId}', log '{progress.ClassifierLogFilePath}'");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(progress), progress.Kind, "Unknown retry progress event");
        }
    }

    private static void WriteLogReference(TextWriter writer, string logPath)
    {
        if (!string.IsNullOrWhiteSpace(logPath)) writer.WriteLine($"  log: {logPath}");
    }

    private static int Fail(AppEnvironment environment, string message)
    {
        environment.Err.WriteLine($"nightweaver: Failed: {message}");
        return ExitCodes.InspectionFailure;
    }
}
