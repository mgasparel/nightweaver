using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal interface IImplementerOutcomePublisher
{
    Task<AgentStageResult> PublishAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        bool releaseClaim,
        CancellationToken cancellationToken);
}

/// <summary>Publishes the Orchestrator's verified Implementer outcome under current Bead ownership.</summary>
internal sealed class ImplementerOutcomePublisher(IProcessRunner processRunner) : IImplementerOutcomePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly BeadsReader reader = new(processRunner);
    private readonly BeadsWriter writer = new(processRunner);

    public async Task<AgentStageResult> PublishAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        bool releaseClaim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(classification);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.Role != AgentRole.Implementer || invocation.Role != AgentRole.Implementer
            || invocation.Observations is null || classification.Outcome is null)
            throw new AgentStageInputException("Only a classified Implementer invocation can publish an outcome.");
        var expectedAssignee = session.InitialAssignee;
        if (string.IsNullOrWhiteSpace(expectedAssignee))
            throw new AgentStageInputException("Implementer outcome publication requires an explicit expected claim owner.");

        var expectedState = invocation.Observations.BeforeState;
        var initial = await ReadBeadAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(initial, expectedState, expectedAssignee);

        var label = classification.Outcome switch
        {
            AgentOutcomeChoice.ReadyForReview => "stage:review",
            AgentOutcomeChoice.ProductDecisionRequired => "product-decision-required",
            _ => throw new AgentStageInputException("Non-approval Implementer outcomes must be escalated, not published as workflow labels.")
        };
        var removeStage = classification.Outcome == AgentOutcomeChoice.ReadyForReview
            ? expectedState.Stage
            : null;
        var comment = JsonSerializer.Serialize(new OutcomeComment(
            "nightweaver-implementer-outcome-v1",
            session.BeadId,
            invocation.InvocationId,
            classification.Provider,
            classification.Outcome.Value.ToString(),
            classification.Diagnostic,
            invocation.LogFilePath), JsonOptions);

        await AddCommentVerifiedAsync(session.Repository, session.BeadId, comment, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var preWrite = await ReadBeadAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(preWrite, expectedState, expectedAssignee);

        var desiredStatus = releaseClaim ? "open" : expectedState.Status;
        var desiredAssignee = releaseClaim ? null : expectedAssignee;
        var desiredState = classification.Outcome switch
        {
            AgentOutcomeChoice.ReadyForReview => expectedState with { Stage = "stage:review", Status = desiredStatus },
            AgentOutcomeChoice.ProductDecisionRequired => expectedState with { ProductDecisionRequired = true, Status = desiredStatus },
            _ => throw new AgentStageInputException("Unsupported Implementer outcome.")
        };

        Exception? writeFailure = null;
        try
        {
            await writer.PublishImplementerOutcomeAsync(session.Repository, session.BeadId, expectedAssignee,
                expectedState.Status, label, removeStage, releaseClaim, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException)
        {
            writeFailure = exception;
        }

        var confirmed = await ReadBeadAsync(session.Repository, session.BeadId, CancellationToken.None).ConfigureAwait(false);
        var confirmedState = BeadWorkflowState.Parse(confirmed);
        if (confirmedState != desiredState || !string.Equals(confirmed.Assignee, desiredAssignee, StringComparison.Ordinal))
        {
            if (writeFailure is not null) throw writeFailure;
            throw new ToolExecutionException($"Published Implementer outcome for Bead '{session.BeadId}' did not verify; observed status '{confirmed.Status}' and assignee '{confirmed.Assignee ?? "(none)"}'.");
        }

        return invocation with
        {
            VerifiedState = confirmedState,
            OutcomeClassification = classification
        };
    }

    private async Task AddCommentVerifiedAsync(
        ValidatedTargetRepository repository,
        string beadId,
        string comment,
        CancellationToken cancellationToken)
    {
        Exception? writeFailure = null;
        try
        {
            await writer.AddCommentAsync(repository, beadId, comment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException)
        {
            writeFailure = exception;
        }

        var comments = await reader.ReadCommentsAsync(repository, beadId, CancellationToken.None).ConfigureAwait(false);
        if (comments.Any(item => string.Equals(item.Text, comment, StringComparison.Ordinal))) return;
        if (writeFailure is not null) throw writeFailure;
        throw new ToolExecutionException($"Implementer outcome evidence comment for Bead '{beadId}' was not observable after writing.");
    }

    private async Task<BeadSnapshot> ReadBeadAsync(
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken)
    {
        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Bead '{beadId}' was not found before Implementer outcome publication.");
        return bead;
    }

    private static void EnsureCurrent(BeadSnapshot bead, BeadWorkflowState expected, string expectedAssignee)
    {
        BeadWorkflowState state;
        try { state = BeadWorkflowState.Parse(bead); }
        catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
        if (state != expected || state.Status != "in_progress"
            || !string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal))
            throw new AgentStageInputException($"Implementer outcome publication for Bead '{bead.Id}' was refused because its workflow state or owner changed.");
    }

    private sealed record OutcomeComment(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("bead_id")] string BeadId,
        [property: JsonPropertyName("invocation_id")] string InvocationId,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("classification_diagnostic")] string Diagnostic,
        [property: JsonPropertyName("invocation_log")] string InvocationLog);
}
