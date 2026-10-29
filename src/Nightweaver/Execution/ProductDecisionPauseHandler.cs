using System.Text.Encodings.Web;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record ProductDecisionPauseRequest(
    string BeadId,
    string ExpectedAssignee,
    AgentRole Role,
    BeadWorkflowState VerifiedState,
    OperationalRetryOutcome LastStage);

internal sealed record ProductDecisionPauseResult(
    bool CleanupCompleted,
    bool ClaimReleased,
    BeadWorkflowState? FinalState,
    IReadOnlyList<string> Failures)
{
    internal bool Succeeded => CleanupCompleted && ClaimReleased && Failures.Count == 0;
}

/// <summary>
/// Safely disposes the owned local execution environment for a product-decision pause and releases the claim.
/// </summary>
internal sealed class ProductDecisionPauseHandler
{
    private static readonly TimeSpan ClaimReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly BeadsReader beadsReader;
    private readonly BeadsWriter beadsWriter;
    private readonly PauseEnvironmentCleaner cleaner;

    internal ProductDecisionPauseHandler(
        IProcessRunner processRunner,
        PauseEnvironmentCleaner? cleaner = null,
        BeadsReader? beadsReader = null,
        BeadsWriter? beadsWriter = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        this.beadsReader = beadsReader ?? new BeadsReader(processRunner);
        this.beadsWriter = beadsWriter ?? new BeadsWriter(processRunner);
        this.cleaner = cleaner ?? new PauseEnvironmentCleaner(processRunner);
    }

    internal async Task<ProductDecisionPauseResult> PauseAsync(
        HostPaths paths,
        ProjectSlug project,
        ValidatedTargetRepository repository,
        ProductDecisionPauseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        var initialBead = await ReadBeadAsync(repository, request.BeadId, cancellationToken).ConfigureAwait(false);
        var initialState = BeadWorkflowState.Parse(initialBead);

        if (initialState.IssueType != "task"
            || !initialState.ProductDecisionRequired
            || initialState.NeedsHuman
            || initialState.Planned
            || initialState.Stage != request.VerifiedState.Stage
            || initialState.RepairCycles != request.VerifiedState.RepairCycles
            || initialBead.Status != "in_progress"
            || !string.Equals(initialBead.Assignee, request.ExpectedAssignee, StringComparison.Ordinal))
        {
            return new ProductDecisionPauseResult(
                false,
                false,
                initialState,
                ["Bead workflow state or claim ownership changed before pause disposition."]);
        }

        var preflight = await cleaner.InspectAsync(
            paths, project, repository, request.BeadId, WorktreePurpose.Delivery, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var cleanupCompleted = false;
        var claimReleased = false;
        var mayReleaseClaim = true;
        var failures = new List<string>();
        Exception? primaryFailure = null;
        Exception? releaseFailure = null;
        OperationCanceledException? cancellationFailure = null;
        BeadWorkflowState? finalState = initialState;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!preflight.Safe)
            {
                failures.Add(preflight.Refusal ?? "Pause cleanup preflight refused.");
            }
            else
            {
                await VerifyActiveClaimAsync(repository, request, cancellationToken).ConfigureAwait(false);

                async Task GuardBetweenDestructiveOperationsAsync(string _, CancellationToken token)
                {
                    await VerifyActiveClaimAsync(repository, request, token).ConfigureAwait(false);
                }

                await cleaner.CleanAsync(
                    paths,
                    project,
                    repository,
                    request.BeadId,
                    preflight,
                    cancellationToken,
                    GuardBetweenDestructiveOperationsAsync).ConfigureAwait(false);

                cleanupCompleted = true;
            }
        }
        catch (OperationCanceledException exception)
        {
            cancellationFailure = exception;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            if (mayReleaseClaim)
            {
                try
                {
                    using var releaseTimeout = new CancellationTokenSource(ClaimReleaseTimeout);
                    var current = await ReadBeadAsync(repository, request.BeadId, releaseTimeout.Token).ConfigureAwait(false);
                    var currentState = BeadWorkflowState.Parse(current);

                    if (currentState.IssueType != request.VerifiedState.IssueType
                        || !currentState.ProductDecisionRequired
                        || currentState.NeedsHuman
                        || currentState.Stage != request.VerifiedState.Stage
                        || currentState.RepairCycles != request.VerifiedState.RepairCycles)
                    {
                        releaseFailure = new ToolExecutionException(
                            $"Release product-decision claim for Bead '{request.BeadId}' was refused because its workflow identity changed.");
                    }
                    else if (currentState.Status == "open" && current.Assignee is null)
                    {
                        claimReleased = true;
                        finalState = currentState;
                    }
                    else if (currentState.Status == "in_progress"
                        && string.Equals(current.Assignee, request.ExpectedAssignee, StringComparison.Ordinal))
                    {
                        await beadsWriter.ReleaseClaimAsync(
                            repository, request.BeadId, request.ExpectedAssignee, releaseTimeout.Token).ConfigureAwait(false);

                        var postRelease = await ReadBeadAsync(repository, request.BeadId, releaseTimeout.Token).ConfigureAwait(false);
                        var postReleaseState = BeadWorkflowState.Parse(postRelease);
                        if (postRelease.Status == "open"
                            && postRelease.Assignee is null
                            && postReleaseState.ProductDecisionRequired
                            && postReleaseState.Stage == request.VerifiedState.Stage
                            && postReleaseState.RepairCycles == request.VerifiedState.RepairCycles)
                        {
                            claimReleased = true;
                            finalState = postReleaseState;
                        }
                        else
                        {
                            releaseFailure = new ToolExecutionException(
                                $"Release product-decision claim verification failed for Bead '{request.BeadId}'; observed status '{postRelease.Status}' and assignee '{postRelease.Assignee ?? "(none)"}'.");
                        }
                    }
                    else
                    {
                        releaseFailure = new ToolExecutionException(
                            $"Release product-decision claim for Bead '{request.BeadId}' was refused because its current status or assignee no longer matches the observed owner.");
                    }

                    if (releaseFailure is not null)
                        failures.Add($"Claim release failed: {releaseFailure.Message}");
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;
                    failures.Add($"Claim release failed: {exception.Message}");
                }
            }
        }

        if (primaryFailure is not null && !failures.Contains(primaryFailure.Message))
            failures.Insert(0, primaryFailure.Message);

        if (primaryFailure is not null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var supplemental = CreateSupplementalComment(request.BeadId, primaryFailure.Message);
                await beadsWriter.AddCommentAsync(repository, request.BeadId, supplemental, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add($"Supplemental diagnostic comment failed: {exception.Message}");
            }
        }

        if (cancellationFailure is not null)
        {
            var progress = new ProductDecisionPauseResult(cleanupCompleted, claimReleased, finalState, failures.AsReadOnly());
            cancellationFailure.Data[nameof(ProductDecisionPauseResult)] = progress;
            throw cancellationFailure;
        }

        return new ProductDecisionPauseResult(cleanupCompleted, claimReleased, finalState, failures.AsReadOnly());
    }

    private async Task<BeadSnapshot> ReadBeadAsync(
        ValidatedTargetRepository repository, string beadId, CancellationToken cancellationToken)
    {
        BeadsInventory inventory;
        try
        {
            inventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolExecutionException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Bead '{beadId}' was not found in the Target Repository tracker.");
        return bead;
    }

    private async Task VerifyActiveClaimAsync(
        ValidatedTargetRepository repository, ProductDecisionPauseRequest request, CancellationToken cancellationToken)
    {
        var current = await ReadBeadAsync(repository, request.BeadId, cancellationToken).ConfigureAwait(false);
        var currentState = BeadWorkflowState.Parse(current);
        if (!string.Equals(current.Assignee, request.ExpectedAssignee, StringComparison.Ordinal)
            || current.Status != "in_progress")
        {
            throw new ToolExecutionException(
                $"Claim ownership changed before pause cleanup; observed status '{current.Status}' and assignee '{current.Assignee ?? "(none)"}'.");
        }
        if (currentState.IssueType != request.VerifiedState.IssueType
            || !currentState.ProductDecisionRequired
            || currentState.NeedsHuman
            || currentState.Stage != request.VerifiedState.Stage
            || currentState.RepairCycles != request.VerifiedState.RepairCycles)
        {
            throw new ToolExecutionException(
                $"Workflow state changed before pause cleanup for Bead '{request.BeadId}'.");
        }
    }

    private static string CreateSupplementalComment(string beadId, string failure) =>
        JsonSerializer.Serialize(
            new { schema = "nightweaver-product-decision-cleanup-failure-v1", bead_id = beadId, failure },
            DiagnosticJsonOptions);
}
