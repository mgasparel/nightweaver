using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal interface IDeliveryOutcomePublisher
{
    Task<AgentStageResult> PublishAsync(
        AgentStageSession session,
        AgentStageResult invocation,
        AgentOutcomeClassificationResult classification,
        bool releaseClaim,
        CancellationToken cancellationToken);
}

/// <summary>Publishes Orchestrator-verified Delivery outcomes under the current Bead owner guard.</summary>
internal sealed class DeliveryOutcomePublisher(IProcessRunner processRunner) : IDeliveryOutcomePublisher
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
        if (!AgentRoles.IsDeliveryRole(session.Role) || invocation.Role != session.Role
            || invocation.Observations is null || classification.Outcome is null)
            throw new AgentStageInputException("Only a classified Delivery invocation can publish an outcome.");

        var expectedAssignee = session.InitialAssignee;
        if (string.IsNullOrWhiteSpace(expectedAssignee))
            throw new AgentStageInputException("Delivery outcome publication requires an explicit expected claim owner.");

        var expectedState = invocation.Observations.BeforeState;
        var transition = BuildTransition(session.Role, expectedState, classification.Outcome.Value, releaseClaim);
        var initial = await ReadBeadAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(initial, expectedState, expectedAssignee);

        var comment = JsonSerializer.Serialize(new OutcomeComment(
            "nightweaver-delivery-outcome-v1",
            session.BeadId,
            AgentRoles.ToConfigKey(session.Role),
            invocation.InvocationId,
            classification.Provider,
            classification.Outcome.Value.ToString(),
            classification.Diagnostic,
            invocation.LogFilePath), JsonOptions);

        await AddCommentVerifiedAsync(session.Repository, session.BeadId, comment, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var preWrite = await ReadBeadAsync(session.Repository, session.BeadId, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(preWrite, expectedState, expectedAssignee);

        ProcessResult? writeResult = null;
        Exception? writeFailure = null;
        try
        {
            writeResult = await writer.PublishDeliveryOutcomeAsync(session.Repository, session.BeadId, expectedAssignee,
                expectedState.Status, transition.Label, transition.RemoveStageLabel, transition.RepairCycles,
                transition.ReleaseClaim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ToolExecutionException or IOException or UnauthorizedAccessException)
        {
            writeFailure = exception;
        }

        if (writeResult is { ExitCode: 0 })
        {
            try
            {
                var published = BeadsReader.ParsePublicationResult(writeResult.StandardOutput, session.BeadId);
                var publishedState = BeadWorkflowState.Parse(published);
                if (MatchesPublication(published, publishedState, transition, expectedAssignee))
                    return Published(invocation, classification, session.Role, transition, published, publishedState);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                // An absent or incompatible structured result is ambiguous; reread once below.
            }
        }

        // Failed or ambiguous update responses are never treated as success. Reread the
        // current state once, accepting only the exact publication while respecting a
        // receiver that has already claimed a released stage.
        var confirmed = await ReadBeadAsync(session.Repository, session.BeadId, CancellationToken.None).ConfigureAwait(false);
        BeadWorkflowState confirmedState;
        try { confirmedState = BeadWorkflowState.Parse(confirmed); }
        catch (FormatException exception)
        {
            throw new ToolExecutionException($"Published Delivery outcome for Bead '{session.BeadId}' has invalid state: {exception.Message}");
        }

        if (MatchesPublication(confirmed, confirmedState, transition, expectedAssignee))
            return Published(invocation, classification, session.Role, transition, confirmed, confirmedState);

        if (writeFailure is not null) throw writeFailure;
        var detail = writeResult is null
            ? "the guarded update did not return a result"
            : $"bd exited {writeResult.ExitCode} and observed status '{confirmed.Status}' with assignee '{confirmed.Assignee ?? "(none)"}'";
        throw new ToolExecutionException($"Delivery outcome publication for Bead '{session.BeadId}' was not confirmed; {detail}. No follow-up mutation was attempted.",
            writeResult?.StandardError ?? string.Empty);
    }

    private static Transition BuildTransition(AgentRole role, BeadWorkflowState expected,
        AgentOutcomeChoice outcome, bool releaseClaim)
    {
        var release = releaseClaim || outcome == AgentOutcomeChoice.ProductDecisionRequired
            || role == AgentRole.Qa && outcome == AgentOutcomeChoice.Approved;
        return (role, outcome) switch
        {
            (AgentRole.Implementer, AgentOutcomeChoice.ReadyForReview) =>
                new("stage:review", expected.Stage, null, expected with { Stage = "stage:review", Status = release ? "open" : expected.Status }, release, false),
            (AgentRole.Reviewer, AgentOutcomeChoice.Approved) =>
                new("stage:qa", expected.Stage, null, expected with { Stage = "stage:qa", Status = release ? "open" : expected.Status }, release, false),
            (AgentRole.Qa, AgentOutcomeChoice.Approved) =>
                new("stage:merge", expected.Stage, null, expected with { Stage = "stage:merge", Status = "open" }, true, true),
            (AgentRole.Reviewer or AgentRole.Qa, AgentOutcomeChoice.ChangesRequired) => BuildRepairTransition(expected, release),
            (AgentRole.Implementer or AgentRole.Reviewer or AgentRole.Qa, AgentOutcomeChoice.ProductDecisionRequired) =>
                new("product-decision-required", null, null,
                    expected with { ProductDecisionRequired = true, Status = "open" }, true, false),
            _ => throw new AgentStageInputException($"Outcome '{outcome}' is not allowed for Delivery role '{AgentRoles.ToConfigKey(role)}'.")
        };
    }

    private static Transition BuildRepairTransition(BeadWorkflowState expected, bool release)
    {
        int cycles;
        try { cycles = checked(expected.RepairCycles + 1); }
        catch (OverflowException)
        {
            throw new AgentStageInputException("The shared Delivery repair counter cannot be incremented without overflowing.");
        }
        return new("stage:implement", expected.Stage, cycles,
            expected with { Stage = "stage:implement", RepairCycles = cycles, Status = release ? "open" : expected.Status },
            release, false);
    }

    private static bool MatchesPublication(BeadSnapshot bead, BeadWorkflowState observed,
        Transition transition, string expectedAssignee)
    {
        if (transition.ReleaseClaim)
        {
            // A receiving pool may claim immediately after an atomic release. The stage
            // publication is still confirmed, and the outgoing slot must stop touching it.
            var workflow = observed with { Status = transition.DesiredState.Status };
            if (workflow != transition.DesiredState) return false;
            return observed.Status == transition.DesiredState.Status && bead.Assignee is null
                || observed.Status == "in_progress" && !string.IsNullOrWhiteSpace(bead.Assignee);
        }
        if (observed != transition.DesiredState) return false;
        return observed.Status == "in_progress"
            && string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal);
    }

    private static AgentStageResult Published(AgentStageResult invocation,
        AgentOutcomeClassificationResult classification, AgentRole role, Transition transition,
        BeadSnapshot bead, BeadWorkflowState state)
    {
        var receipt = new DeliveryOutcomePublicationReceipt(role, classification.Outcome!.Value,
            state, bead.Assignee, transition.ReleaseClaim, transition.HandoffToMerger);
        return invocation with
        {
            VerifiedState = state,
            OutcomeClassification = classification,
            PublicationReceipt = receipt
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
        throw new ToolExecutionException($"Delivery outcome evidence comment for Bead '{beadId}' was not observable after writing.");
    }

    private async Task<BeadSnapshot> ReadBeadAsync(
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken)
    {
        var inventory = await reader.ReadSnapshotAsync(repository, cancellationToken).ConfigureAwait(false);
        if (!inventory.ById.TryGetValue(beadId, out var bead))
            throw new AgentStageInputException($"Bead '{beadId}' was not found before Delivery outcome publication.");
        return bead;
    }

    private static void EnsureCurrent(BeadSnapshot bead, BeadWorkflowState expected, string expectedAssignee)
    {
        BeadWorkflowState state;
        try { state = BeadWorkflowState.Parse(bead); }
        catch (FormatException exception) { throw new AgentStageInputException(exception.Message); }
        if (state != expected || state.Status != "in_progress"
            || !string.Equals(bead.Assignee, expectedAssignee, StringComparison.Ordinal))
            throw new AgentStageInputException($"Delivery outcome publication for Bead '{bead.Id}' was refused because its workflow state or owner changed.");
    }

    private sealed record Transition(
        string Label,
        string? RemoveStageLabel,
        int? RepairCycles,
        BeadWorkflowState DesiredState,
        bool ReleaseClaim,
        bool HandoffToMerger);

    private sealed record OutcomeComment(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("bead_id")] string BeadId,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("invocation_id")] string InvocationId,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("classification_diagnostic")] string Diagnostic,
        [property: JsonPropertyName("invocation_log")] string InvocationLog);
}
