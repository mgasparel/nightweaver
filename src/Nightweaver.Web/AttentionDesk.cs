using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record AttentionDeskItem(
    [property: JsonPropertyName("beadId")] string BeadId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pausedAt")] DateTimeOffset? PausedAt,
    [property: JsonPropertyName("pauseTimeSource")] string PauseTimeSource,
    [property: JsonPropertyName("fromRole")] string? FromRole,
    [property: JsonPropertyName("question")] string? Question,
    [property: JsonPropertyName("diagnostic")] string? Diagnostic,
    [property: JsonPropertyName("repairCycles")] int? RepairCycles,
    [property: JsonPropertyName("maxRepairCycles")] int MaxRepairCycles,
    [property: JsonPropertyName("canDecide")] bool CanDecide,
    [property: JsonPropertyName("canRetry")] bool CanRetry,
    [property: JsonPropertyName("canAbort")] bool CanAbort,
    [property: JsonPropertyName("unavailableReason")] string? UnavailableReason,
    [property: JsonPropertyName("sourceAuthor")] string? SourceAuthor)
{
    [JsonPropertyName("presentation")] public BeadPresentation? Presentation { get; init; }
    [JsonPropertyName("message")] public CommentPresentation? Message { get; init; }
    [JsonPropertyName("sourceCommentId")] public string? SourceCommentId { get; init; }
    [JsonPropertyName("otherPauseReports")] public IReadOnlyList<CommentPresentation> OtherPauseReports { get; init; } = [];
    [JsonPropertyName("contextNotice")] public string? ContextNotice { get; init; }
}

internal static class AttentionDeskProjector
{
    private const string ProductDecisionLabel = "product-decision-required";
    private const string NeedsHumanLabel = "needs-human";

    internal static AttentionDeskItem Project(BeadSnapshot bead, IReadOnlyList<BeadsCommentSnapshot> comments,
        int maxRepairCycles)
        => ProjectWithSelection(bead, comments, maxRepairCycles).Item;

    internal static AttentionDeskProjection ProjectWithSelection(BeadSnapshot bead, IReadOnlyList<BeadsCommentSnapshot> comments,
        int maxRepairCycles)
    {
        var productDecision = bead.Labels.Contains(ProductDecisionLabel, StringComparer.Ordinal);
        var needsHuman = bead.Labels.Contains(NeedsHumanLabel, StringComparer.Ordinal);
        var workflow = ParseWorkflow(bead);
        var pauseCandidates = comments.Where(c => CommentPresenter.IsPauseCandidate(c.Text, productDecision && !needsHuman)).ToArray();
        var selectedStructured = pauseCandidates.LastOrDefault(c => !CommentPresenter.IsSupplemental(CommentPresenter.Project(c, bead)));
        var selectedQuestion = productDecision ? selectedStructured
            ?? comments.LastOrDefault(comment => IsLegacyDecisionContext(comment.Text)) : null;
        var selectedDiagnostic = needsHuman ? selectedStructured : null;
        var selectedMessage = selectedStructured is null ? null : CommentPresenter.Project(selectedStructured, bead);

        var kind = workflow.InvalidReason is not null
            ? "invalid-workflow"
            : productDecision ? "product-decision-required"
            : "needs-human";
        var role = productDecision && !needsHuman
            ? selectedStructured is null ? RoleForStage(workflow.Stage) : selectedMessage?.Role
            : needsHuman && !productDecision
                ? selectedMessage?.Role
                : null;
        var selectedPauseComment = productDecision && !needsHuman
            ? selectedQuestion
            : needsHuman && !productDecision
                ? selectedDiagnostic
                : null;
        var diagnosticText = needsHuman
            ? selectedMessage?.Summary ?? "Needs-human is present, but no attributable diagnostic is available. Review the history before acting."
            : workflow.InvalidReason;
        if (kind == "invalid-workflow" && needsHuman && workflow.InvalidReason is not null)
            diagnosticText = $"{workflow.InvalidReason}{Environment.NewLine}{diagnosticText}";

        var unavailableReason = workflow.InvalidReason is not null
            ? "Workflow labels or repair metadata are contradictory or malformed."
            : bead.Labels.Contains("planned", StringComparer.Ordinal)
                ? "This Product is already planned; historical technical attention does not authorize Retry."
            : bead.Status != "open"
                ? "This Bead is not open and cannot be changed from the Attention Desk."
                : bead.Assignee is not null
                    ? "This Bead has an active claim and cannot be changed from the Attention Desk."
                    : null;
        var canDecide = kind == "product-decision-required" && unavailableReason is null;
        var canRetry = kind == "needs-human" && unavailableReason is null;
        var canAbort = canRetry;
        var projected = BeadExplorerItem.FromSnapshot(bead);

        var item = new AttentionDeskItem(
            projected.Id,
            projected.Title,
            projected.Type,
            projected.Priority,
            projected.Status,
            projected.Labels,
            workflow.Stage,
            kind,
            selectedPauseComment?.CreatedAt.ToUniversalTime(),
            selectedPauseComment is not null ? "comment" : "unknown",
            role,
            selectedMessage?.Summary ?? selectedQuestion?.Text,
            diagnosticText,
            workflow.RepairCycles,
            maxRepairCycles,
            canDecide,
            canRetry,
            canAbort,
            unavailableReason,
            selectedPauseComment?.Author)
        {
            Presentation = BeadPresenter.Project(bead, maxRepairCycles),
            Message = selectedMessage,
            SourceCommentId = selectedPauseComment?.Id,
            ContextNotice = selectedMessage?.Stage is { } sourceStage && sourceStage != "(none)" && sourceStage != workflow.Stage
                ? $"This source reports {sourceStage}; the current workflow stage is {workflow.Stage ?? "unavailable"}. It may not explain the current pause. Review history before acting."
                : null,
            // Separate attributed reports, not a chronological claim of causality.
            OtherPauseReports = comments.Select(c => CommentPresenter.Project(c, bead))
                .Where(CommentPresenter.IsSupplemental).TakeLast(3).ToArray()
        };

        var selectionSource = selectedPauseComment is null
            ? "none"
            : productDecision && !needsHuman ? (selectedStructured is null ? "legacy-context" : "structured")
            : needsHuman && !productDecision ? "structured"
            : "none";
        return new AttentionDeskProjection(item, selectedPauseComment?.Id, selectionSource);
    }

    internal static BeadWorkspaceCommentClassification ClassifyComment(BeadsCommentSnapshot comment, BeadSnapshot bead)
    {
        var presentation = CommentPresenter.Project(comment, bead);
        if (presentation.State != "ordinary")
            return new(presentation.State == "fallback" ? "unsupported-message" : presentation.Kind ?? "diagnostic", presentation.Role, presentation.Stage);
        return new("ordinary", null, null);
    }

    internal static bool IsPaused(BeadSnapshot bead)
        => bead.Labels.Contains(ProductDecisionLabel, StringComparer.Ordinal)
            || bead.Labels.Contains(NeedsHumanLabel, StringComparer.Ordinal);

    internal static bool IsTerminal(BeadSnapshot bead)
        => bead.Status.Equals("closed", StringComparison.OrdinalIgnoreCase)
            || bead.Status.Equals("done", StringComparison.OrdinalIgnoreCase)
            || bead.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || bead.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || bead.Status.Equals("tombstone", StringComparison.OrdinalIgnoreCase);

    internal static string? InvalidWorkflowReason(BeadSnapshot bead)
    {
        return ParseWorkflow(bead).InvalidReason;
    }

    internal static int? RepairCycles(BeadSnapshot bead) => BeadWorkflowObservation.Read(bead).RepairCycles;

    internal static string? Stage(BeadSnapshot bead)
        => bead.Labels.FirstOrDefault(label => label.StartsWith("stage:", StringComparison.Ordinal));

    private static AttentionWorkflow ParseWorkflow(BeadSnapshot bead)
    {
        var observed = BeadWorkflowObservation.Read(bead);
        var error = observed.Error;
        if (error is null && observed.State is { } state && !AuthorityMatrix.TryInferLifecycleRole(state, out _, out var reason)) error = reason;
        return new(Stage(bead), observed.RepairCycles, error);
    }

    private static bool IsLegacyDecisionContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{')) return true;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return true;
            if (document.RootElement.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.String)
            {
                string? schemaValue;
                try
                {
                    schemaValue = schema.GetString();
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                if (schemaValue?.StartsWith("nightweaver-", StringComparison.Ordinal) == true) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            // Only a valid Nightweaver schema marks a machine comment; preserve legacy text verbatim.
            return true;
        }
    }

    private static string? RoleForStage(string? stage) => stage switch
    {
        "stage:plan" => "planner",
        "stage:plan-challenge" => "plan_challenger",
        "stage:implement" => "implementer",
        "stage:review" => "reviewer",
        "stage:qa" => "qa",
        "stage:merge" => "merger",
        _ => null
    };

    private sealed record AttentionWorkflow(string? Stage, int? RepairCycles, string? InvalidReason);
}

internal sealed record AttentionDeskProjection(AttentionDeskItem Item, string? ActiveCommentId, string SelectionSource);
internal sealed record BeadWorkspaceCommentClassification(string Kind, string? Role, string? Stage);
