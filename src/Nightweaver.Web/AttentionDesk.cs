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
    [property: JsonPropertyName("sourceAuthor")] string? SourceAuthor);

internal static class AttentionDeskProjector
{
    private const string ProductDecisionLabel = "product-decision-required";
    private const string NeedsHumanLabel = "needs-human";
    private const string NeedsHumanSchema = "nightweaver-needs-human-v1";
    private const string CleanupFailureSchema = "nightweaver-needs-human-cleanup-failure-v1";

    private static readonly HashSet<string> KnownStages = new(StringComparer.Ordinal)
    {
        "stage:plan", "stage:plan-challenge", "stage:plan-approved",
        "stage:implement", "stage:review", "stage:qa", "stage:merge"
    };

    internal static AttentionDeskItem Project(BeadSnapshot bead, IReadOnlyList<BeadsCommentSnapshot> comments,
        int maxRepairCycles)
    {
        var productDecision = bead.Labels.Contains(ProductDecisionLabel, StringComparer.Ordinal);
        var needsHuman = bead.Labels.Contains(NeedsHumanLabel, StringComparer.Ordinal);
        var workflow = ParseWorkflow(bead, productDecision, needsHuman);
        var diagnostics = comments.Select(comment => (Comment: comment, Diagnostic: TryReadNeedsHumanDiagnostic(comment, bead)))
            .Where(pair => pair.Diagnostic is not null)
            .Select(pair => (pair.Comment, Diagnostic: pair.Diagnostic!))
            .ToArray();
        var latestDiagnostic = diagnostics.LastOrDefault();
        var cleanupWarning = latestDiagnostic.Diagnostic is null ? null : comments
            .Where(comment => comment.CreatedAt > latestDiagnostic.Comment.CreatedAt)
            .Select(TryReadCleanupFailure)
            .LastOrDefault(value => value is not null);
        var selectedQuestion = productDecision ? comments.LastOrDefault(comment => IsLegacyDecisionContext(comment.Text)) : null;

        var kind = workflow.InvalidReason is not null
            ? "invalid-workflow"
            : productDecision ? "product-decision-required"
            : "needs-human";
        var role = latestDiagnostic.Diagnostic?.FailedRole ?? RoleForStage(workflow.Stage);
        var diagnosticText = needsHuman
            ? FormatNeedsHumanDiagnostic(latestDiagnostic.Diagnostic, cleanupWarning)
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

        return new AttentionDeskItem(
            projected.Id,
            projected.Title,
            projected.Type,
            projected.Priority,
            projected.Status,
            projected.Labels,
            workflow.Stage,
            kind,
            selectedQuestion?.CreatedAt.ToUniversalTime() ?? latestDiagnostic.Comment?.CreatedAt.ToUniversalTime(),
            selectedQuestion is not null || latestDiagnostic.Comment is not null ? "comment" : "unknown",
            role,
            selectedQuestion?.Text,
            diagnosticText,
            workflow.RepairCycles,
            maxRepairCycles,
            canDecide,
            canRetry,
            canAbort,
            unavailableReason,
            selectedQuestion?.Author ?? latestDiagnostic.Comment?.Author);
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

    private static NeedsHumanDiagnostic? TryReadNeedsHumanDiagnostic(BeadsCommentSnapshot comment, BeadSnapshot bead)
    {
        try
        {
            using var document = JsonDocument.Parse(comment.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = ReadUniqueProperties(document.RootElement);
            if (!ReadString(fields, "schema", out var schema) || schema != NeedsHumanSchema
                || !ReadString(fields, "bead_id", out var beadId) || beadId != bead.Id
                || !ReadString(fields, "bead_type", out var beadType) || beadType != bead.IssueType
                || !ReadString(fields, "cause", out var cause) || !KnownCause(cause)
                || !ReadString(fields, "failed_role", out var failedRole) || !KnownLifecycleRole(failedRole)
                || !ReadString(fields, "reason", out var reason) || string.IsNullOrWhiteSpace(reason)
                || !ReadString(fields, "latest_findings", out var latestFindings, allowEmpty: true)
                || !ReadString(fields, "cleanup_preflight", out var cleanupPreflight, allowEmpty: true)
                || !ReadString(fields, "stage", out var stage) || !KnownStages.Contains(stage)
                || !ReadString(fields, "invocation_id", out _)
                || !ReadString(fields, "log_directory", out _)
                || !fields.TryGetValue("log_files", out var logFilesElement) || logFilesElement.ValueKind != JsonValueKind.Array)
                return null;
            var logs = new List<string>();
            foreach (var log in logFilesElement.EnumerateArray())
            {
                if (log.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(log.GetString())) return null;
                logs.Add(log.GetString()!);
            }
            return new(cause, failedRole, reason, latestFindings, cleanupPreflight, logs);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadCleanupFailure(BeadsCommentSnapshot comment)
    {
        try
        {
            using var document = JsonDocument.Parse(comment.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = ReadUniqueProperties(document.RootElement);
            return ReadString(fields, "schema", out var schema) && schema == CleanupFailureSchema
                && ReadString(fields, "bead_id", out var beadId) && beadId == comment.IssueId
                && ReadString(fields, "failure", out var failure) && !string.IsNullOrWhiteSpace(failure)
                    ? failure : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement> ReadUniqueProperties(JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value)) throw new JsonException("duplicate property");
        return fields;
    }

    private static bool ReadString(Dictionary<string, JsonElement> fields, string name, out string value,
        bool allowEmpty = false)
    {
        value = string.Empty;
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return allowEmpty || !string.IsNullOrWhiteSpace(value);
    }

    private static bool KnownCause(string cause) => cause is "operational-retries-exhausted" or "duplicate-failure"
        or "repair-budget-exhausted" or "environment-blocked";

    private static bool KnownLifecycleRole(string role) => role is "planner" or "plan_challenger" or "implementer"
        or "reviewer" or "qa" or "merger";

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
                && schema.ValueKind == JsonValueKind.String
                && schema.GetString()!.StartsWith("nightweaver-", StringComparison.Ordinal)) return false;
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

    private static string? FormatNeedsHumanDiagnostic(NeedsHumanDiagnostic? diagnostic, string? cleanupWarning)
    {
        if (diagnostic is null)
            return cleanupWarning is null
                ? "Needs-human is present, but no valid matching escalation diagnostic comment is available."
                : $"Needs-human is present, but no valid matching escalation diagnostic comment is available.{Environment.NewLine}Cleanup warning: {cleanupWarning}";

        var lines = new List<string>
        {
            $"Cause: {diagnostic.Cause}",
            $"Reason: {diagnostic.Reason}",
            $"Latest findings: {diagnostic.LatestFindings}",
            $"Log files: {(diagnostic.LogFiles.Count == 0 ? "Unknown" : string.Join(", ", diagnostic.LogFiles))}"
        };
        if (cleanupWarning is not null)
            lines.Add($"Cleanup warning: {cleanupWarning}");
        else if (!string.Equals(diagnostic.CleanupPreflight, "passed", StringComparison.OrdinalIgnoreCase))
            lines.Add($"Cleanup warning: {diagnostic.CleanupPreflight}");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record AttentionWorkflow(string? Stage, int? RepairCycles, string? InvalidReason);
    private sealed record NeedsHumanDiagnostic(string Cause, string FailedRole, string Reason, string LatestFindings,
        string CleanupPreflight, IReadOnlyList<string> LogFiles);
}
