using System.Text;
using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record AttentionWorkflowSnapshot(string? Stage, IReadOnlyList<string> PauseLabels, int RepairCycles)
{
    private static readonly HashSet<string> KnownStages = new(StringComparer.Ordinal)
    {
        "stage:plan", "stage:plan-challenge", "stage:plan-approved", "stage:implement",
        "stage:review", "stage:qa", "stage:merge"
    };

    private static readonly HashSet<string> PauseLabelsSet = new(StringComparer.Ordinal)
    {
        "needs-human", "product-decision-required"
    };

    internal static bool IsKnownStage(string stage) => KnownStages.Contains(stage);
    internal static bool IsPauseLabel(string label) => PauseLabelsSet.Contains(label);

    internal bool Matches(AttentionMutationIdentity identity)
    {
        var observedPauseLabels = identity.Labels.Where(PauseLabelsSet.Contains).Order(StringComparer.Ordinal);
        return Stage == identity.Stage
            && PauseLabels.Order(StringComparer.Ordinal).SequenceEqual(observedPauseLabels, StringComparer.Ordinal)
            && RepairCycles == (identity.RepairCycles ?? 0);
    }
}

internal sealed record DecideAttentionRequest(string Instruction, string Action, AttentionWorkflowSnapshot? Expected);
internal sealed record RetryAttentionRequest(AttentionWorkflowSnapshot Expected);

internal sealed record AttentionMutationResult(string BeadId, string Action, bool Resolved,
    bool CommentRecorded, bool StateChanged);

internal sealed class AttentionRequestValidationException(string detail) : Exception(detail);

internal sealed class AttentionActionException(int statusCode, string title, string detail,
    bool? commentRecorded = null, bool? stateChanged = null, Exception? innerException = null,
    bool includeProgress = false)
    : Exception(detail, innerException)
{
    internal int StatusCode { get; } = statusCode;
    internal string Title { get; } = title;
    internal string Detail { get; } = detail;
    internal bool? CommentRecorded { get; } = commentRecorded;
    internal bool? StateChanged { get; } = stateChanged;
    internal bool IncludeProgress { get; } = includeProgress;
}

internal static class AttentionMutationRequest
{
    internal static DecideAttentionRequest ParseDecide(ReadOnlyMemory<byte> body)
    {
        using var document = ParseJson(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new AttentionRequestValidationException("The request body must be a JSON object.");
        var values = ReadUniqueFields(root);
        if (values.Keys.Any(name => name is not ("instruction" or "action" or "expected")))
            throw new AttentionRequestValidationException("Only instruction, action, and expected are allowed.");
        if (!values.TryGetValue("instruction", out var instructionValue)
            || !values.TryGetValue("action", out var actionValue))
            throw new AttentionRequestValidationException("Both instruction and action are required.");
        if (instructionValue.ValueKind != JsonValueKind.String || actionValue.ValueKind != JsonValueKind.String)
            throw new AttentionRequestValidationException("Instruction and action must be strings.");

        var instruction = ReadString(instructionValue)?.Trim();
        if (string.IsNullOrWhiteSpace(instruction))
            throw new AttentionRequestValidationException("Instruction must not be blank.");
        if (instruction.Contains('\0'))
            throw new AttentionRequestValidationException("Instruction cannot contain a NUL character.");
        if (Encoding.UTF8.GetByteCount(instruction) > 16 * 1024)
            throw new AttentionRequestValidationException("Instruction cannot exceed 16 KiB of UTF-8 text.");

        var action = ReadString(actionValue);
        if (action is not ("resume" or "note_only"))
            throw new AttentionRequestValidationException("Action must be resume or note_only.");

        if (action == "resume")
        {
            if (values.Count != 3 || !values.TryGetValue("expected", out var expectedValue))
                throw new AttentionRequestValidationException("Resume requires an expected workflow snapshot.");
            return new DecideAttentionRequest(instruction, action, ParseExpected(expectedValue));
        }

        if (values.Count != 2)
            throw new AttentionRequestValidationException("Save Note accepts only instruction and action.");
        return new DecideAttentionRequest(instruction, action, null);
    }

    internal static RetryAttentionRequest ParseRetry(ReadOnlyMemory<byte> body)
    {
        using var document = ParseJson(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new AttentionRequestValidationException("The request body must be a JSON object.");
        var values = ReadUniqueFields(root);
        if (values.Count != 1 || !values.TryGetValue("expected", out var expectedValue))
            throw new AttentionRequestValidationException("Retry requires only an expected workflow snapshot.");
        return new RetryAttentionRequest(ParseExpected(expectedValue));
    }

    internal static void ParseEmptyAction(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty) return;
        using var document = ParseJson(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new AttentionRequestValidationException("The request body must be empty or an empty JSON object.");
        if (ReadUniqueFields(document.RootElement).Count != 0)
            throw new AttentionRequestValidationException("Retry and abort do not accept request fields.");
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new AttentionRequestValidationException("The request body must be valid JSON.");
        }
    }

    private static Dictionary<string, JsonElement> ReadUniqueFields(JsonElement root)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException)
            {
                throw new AttentionRequestValidationException("Request property names must contain valid Unicode.");
            }

            if (!values.TryAdd(name, property.Value))
                throw new AttentionRequestValidationException("Request properties may be supplied only once.");
        }
        return values;
    }

    private static AttentionWorkflowSnapshot ParseExpected(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new AttentionRequestValidationException("Expected must be a workflow snapshot object.");
        var fields = ReadUniqueFields(value);
        if (fields.Count != 3 || fields.Keys.Any(name => name is not ("stage" or "pauseLabels" or "repairCycles"))
            || !fields.TryGetValue("stage", out var stageValue)
            || !fields.TryGetValue("pauseLabels", out var labelsValue)
            || !fields.TryGetValue("repairCycles", out var repairCyclesValue))
            throw new AttentionRequestValidationException("Expected must contain exactly stage, pauseLabels, and repairCycles.");

        string? stage;
        if (stageValue.ValueKind == JsonValueKind.Null)
        {
            stage = null;
        }
        else if (stageValue.ValueKind == JsonValueKind.String)
        {
            stage = ReadString(stageValue);
            if (stage is null || !AttentionWorkflowSnapshot.IsKnownStage(stage))
                throw new AttentionRequestValidationException("Expected stage is not recognized.");
        }
        else
        {
            throw new AttentionRequestValidationException("Expected stage must be a recognized stage string or null.");
        }

        if (labelsValue.ValueKind != JsonValueKind.Array)
            throw new AttentionRequestValidationException("Expected pauseLabels must be an array.");
        var labels = new List<string>();
        var uniqueLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var labelValue in labelsValue.EnumerateArray())
        {
            if (labelValue.ValueKind != JsonValueKind.String)
                throw new AttentionRequestValidationException("Expected pauseLabels must contain strings.");
            var label = ReadString(labelValue)!;
            if (!AttentionWorkflowSnapshot.IsPauseLabel(label) || !uniqueLabels.Add(label))
                throw new AttentionRequestValidationException("Expected pauseLabels must be unique recognized pause labels.");
            labels.Add(label);
        }

        if (repairCyclesValue.ValueKind != JsonValueKind.Number
            || !repairCyclesValue.TryGetInt32(out var repairCycles) || repairCycles < 0)
            throw new AttentionRequestValidationException("Expected repairCycles must be a nonnegative 32-bit integer.");

        return new AttentionWorkflowSnapshot(stage, labels, repairCycles);
    }

    private static string? ReadString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            throw new AttentionRequestValidationException("String values must contain valid Unicode.");
        }
    }
}

internal sealed record AttentionMutationIdentity(
    string IssueType,
    int Priority,
    string Status,
    string? Assignee,
    string? Stage,
    int? RepairCycles,
    bool HasRepairCycles,
    string MetadataWithoutRepairCycles,
    string[] Labels)
{
    internal static AttentionMutationIdentity Capture(BeadSnapshot bead)
    {
        var metadata = ReadMetadata(bead);
        return new AttentionMutationIdentity(bead.IssueType, bead.Priority, bead.Status, bead.Assignee,
            AttentionDeskProjector.Stage(bead), AttentionDeskProjector.RepairCycles(bead), metadata.HasRepairCycles,
            metadata.WithoutRepairCycles, bead.Labels.Order(StringComparer.Ordinal).ToArray());
    }

    internal bool SameObservedIdentity(AttentionMutationIdentity other)
        => IssueType == other.IssueType && Priority == other.Priority && Status == other.Status
            && Assignee == other.Assignee && Stage == other.Stage && RepairCycles == other.RepairCycles
            && HasRepairCycles == other.HasRepairCycles
            && MetadataWithoutRepairCycles == other.MetadataWithoutRepairCycles
            && Labels.SequenceEqual(other.Labels, StringComparer.Ordinal);

    internal bool MatchesResolution(AttentionMutationIdentity other, string action)
    {
        var expectedLabels = action == "abort"
            ? Labels
            : Labels.Where(label => label is not ("needs-human" or "product-decision-required")).ToArray();
        var expectedStatus = action == "abort" ? "closed" : "open";
        return IssueType == other.IssueType && Priority == other.Priority && other.Status == expectedStatus
            && other.Assignee is null && Stage == other.Stage
            && other.Labels.SequenceEqual(expectedLabels, StringComparer.Ordinal)
            && MetadataWithoutRepairCycles == other.MetadataWithoutRepairCycles
            && (action == "abort"
                ? RepairCycles == other.RepairCycles && HasRepairCycles == other.HasRepairCycles
                : !other.HasRepairCycles && other.RepairCycles == 0);
    }

    private static (bool HasRepairCycles, string WithoutRepairCycles) ReadMetadata(BeadSnapshot bead)
    {
        JsonElement metadata = default;
        var count = 0;
        foreach (var property in bead.Payload.EnumerateObject())
        {
            if (property.Name != "metadata") continue;
            metadata = property.Value;
            count++;
        }
        if (count > 1) return (false, "<duplicate metadata>");
        if (count == 0 || metadata.ValueKind == JsonValueKind.Null) return (false, "{}");
        if (metadata.ValueKind != JsonValueKind.Object) return (false, "<invalid metadata>");

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var hasRepairCycles = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name == "repair_cycles")
            {
                hasRepairCycles = true;
                continue;
            }
            if (!fields.TryAdd(property.Name, property.Value.GetRawText()))
                return (hasRepairCycles, "<duplicate metadata>");
        }
        var json = "{" + string.Join(',', fields.Select(field => $"{JsonSerializer.Serialize(field.Key)}:{field.Value}")) + "}";
        return (hasRepairCycles, json);
    }
}
