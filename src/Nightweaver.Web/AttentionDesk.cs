using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
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
    [JsonPropertyName("message")] public CommentPresentation? Message { get; init; }
    [JsonPropertyName("sourceCommentId")] public string? SourceCommentId { get; init; }
    [JsonPropertyName("otherPauseReports")] public IReadOnlyList<CommentPresentation> OtherPauseReports { get; init; } = [];
    [JsonPropertyName("contextNotice")] public string? ContextNotice { get; init; }
}

internal static class AttentionDeskProjector
{
    private const string ProductDecisionLabel = "product-decision-required";
    private const string NeedsHumanLabel = "needs-human";

    private static readonly HashSet<string> KnownStages = new(StringComparer.Ordinal)
    {
        "stage:plan", "stage:plan-challenge", "stage:plan-approved",
        "stage:implement", "stage:review", "stage:qa", "stage:merge"
    };

    internal static AttentionDeskItem Project(BeadSnapshot bead, IReadOnlyList<BeadsCommentSnapshot> comments,
        int maxRepairCycles)
        => ProjectWithSelection(bead, comments, maxRepairCycles).Item;

    internal static AttentionDeskProjection ProjectWithSelection(BeadSnapshot bead, IReadOnlyList<BeadsCommentSnapshot> comments,
        int maxRepairCycles)
    {
        var productDecision = bead.Labels.Contains(ProductDecisionLabel, StringComparer.Ordinal);
        var needsHuman = bead.Labels.Contains(NeedsHumanLabel, StringComparer.Ordinal);
        var workflow = ParseWorkflow(bead, productDecision, needsHuman);
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
            return new(presentation.State == "fallback" ? "unsupported-message" : "diagnostic", presentation.Role, presentation.Stage);

        try
        {
            using var document = JsonDocument.Parse(comment.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new("ordinary", null, null);
            var fields = ReadUniqueProperties(document.RootElement);
            if (!ReadString(fields, "schema", out var schema) || !ReadString(fields, "actor", out var actor)
                || actor != "local-console" || !ReadString(fields, "action", out var action))
                return new("ordinary", null, null);
            if (schema == "nightweaver-human-guidance-v1"
                && (action is "resume" or "note_only")
                && ReadString(fields, "instruction", out _))
                return new("human-guidance", null, null);
            if (schema == "nightweaver-human-action-v1" && (action is "retry" or "abort"))
                return new("human-action", null, null);
        }
        catch (JsonException)
        {
            // Unknown or malformed comments remain available as exact plain text.
        }
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
        var productDecision = bead.Labels.Contains(ProductDecisionLabel, StringComparer.Ordinal);
        var needsHuman = bead.Labels.Contains(NeedsHumanLabel, StringComparer.Ordinal);
        return ParseWorkflow(bead, productDecision, needsHuman).InvalidReason;
    }

    internal static int? RepairCycles(BeadSnapshot bead) => ParseRepairCycles(bead, out _);

    internal static string? Stage(BeadSnapshot bead) => ParseStage(bead, out _);

    private static AttentionWorkflow ParseWorkflow(BeadSnapshot bead, bool productDecision, bool needsHuman)
    {
        var stage = ParseStage(bead, out var stageError);
        var repairCycles = ParseRepairCycles(bead, out var repairError);
        var planned = bead.Labels.Contains("planned", StringComparer.Ordinal);
        string? invalid = stageError ?? repairError;
        if (invalid is null && productDecision && needsHuman)
            invalid = "Both product-decision-required and needs-human pause labels are present.";
        else if (invalid is null && planned && (stage is not null || productDecision || needsHuman))
            invalid = "The planned label conflicts with stage or pause labels.";
        return new(stage, repairCycles, invalid);
    }

    private static string? ParseStage(BeadSnapshot bead, out string? error)
    {
        var stages = bead.Labels.Where(label => label.StartsWith("stage:", StringComparison.Ordinal)).ToArray();
        if (stages.Length > 1)
        {
            error = "Multiple stage labels are present.";
            return stages[0];
        }
        if (stages.Length == 0)
        {
            error = null;
            return null;
        }
        if (!KnownStages.Contains(stages[0]))
        {
            error = "The stage label is not recognized by Nightweaver.";
            return stages[0];
        }
        error = null;
        return stages[0];
    }

    private static int? ParseRepairCycles(BeadSnapshot bead, out string? error)
    {
        JsonElement metadata = default;
        var metadataCount = 0;
        foreach (var property in bead.Payload.EnumerateObject())
        {
            if (property.Name != "metadata") continue;
            metadata = property.Value;
            metadataCount++;
        }
        if (metadataCount > 1)
        {
            error = "Bead metadata contains duplicate fields.";
            return null;
        }
        if (metadataCount == 0 || metadata.ValueKind == JsonValueKind.Null)
        {
            error = null;
            return 0;
        }
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            error = "Bead metadata is not an object.";
            return null;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in metadata.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value))
            {
                error = "Bead metadata contains duplicate fields.";
                return null;
            }
        }
        if (!values.TryGetValue("repair_cycles", out var value))
        {
            error = null;
            return 0;
        }

        int result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result))
        {
            // Numeric JSON values that are not integral are rejected by TryGetInt32.
        }
        else if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
        {
            // bd accepts an integer encoded as a string for this metadata value.
        }
        else
        {
            error = "repair_cycles metadata is not a 32-bit integer.";
            return null;
        }
        if (result < 0)
        {
            error = "repair_cycles metadata cannot be negative.";
            return null;
        }
        error = null;
        return result;
    }

    private static Dictionary<string, JsonElement> ReadUniqueProperties(JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException exception)
            {
                throw new JsonException("Comment property name contains invalid Unicode.", exception);
            }

            if (!fields.TryAdd(name, property.Value)) throw new JsonException("duplicate property");
        }
        return fields;
    }

    private static bool ReadString(Dictionary<string, JsonElement> fields, string name, out string value,
        bool allowEmpty = false)
    {
        value = string.Empty;
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = ReadJsonString(element);
        return allowEmpty || !string.IsNullOrWhiteSpace(value);
    }

    private static string ReadJsonString(JsonElement element)
    {
        try
        {
            return element.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("Comment string contains invalid Unicode.", exception);
        }
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
