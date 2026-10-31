using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record BeadWorkspaceResponse(
    [property: JsonPropertyName("bead")] BeadWorkspaceBead Bead,
    [property: JsonPropertyName("relations")] IReadOnlyList<BeadWorkspaceRelation> Relations,
    [property: JsonPropertyName("comments")] IReadOnlyList<BeadWorkspaceComment> Comments,
    [property: JsonPropertyName("history")] BeadWorkspaceHistory History,
    [property: JsonPropertyName("attention")] AttentionDeskItem? Attention,
    [property: JsonPropertyName("environment")] BeadWorkspaceEnvironment Environment);

internal sealed record BeadWorkspaceBead(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("assignee")] string? Assignee,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("repairCycles")] int? RepairCycles,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("closedAt")] DateTimeOffset? ClosedAt,
    [property: JsonPropertyName("createdBy")] string? CreatedBy,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("design")] string Design,
    [property: JsonPropertyName("acceptanceCriteria")] string AcceptanceCriteria,
    [property: JsonPropertyName("notes")] string Notes);

internal sealed record BeadWorkspaceRelation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record BeadWorkspaceComment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("stage")] string? Stage);

internal sealed record BeadWorkspaceHistory(
    [property: JsonPropertyName("olderCommentIds")] IReadOnlyList<string> OlderCommentIds,
    [property: JsonPropertyName("recentCommentIds")] IReadOnlyList<string> RecentCommentIds,
    [property: JsonPropertyName("activeCommentId")] string? ActiveCommentId,
    [property: JsonPropertyName("selectionSource")] string SelectionSource);

internal sealed record BeadWorkspaceEnvironment(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("worktreePath")] string? WorktreePath,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("head")] string? Head,
    [property: JsonPropertyName("detached")] bool? Detached,
    [property: JsonPropertyName("gitStatus")] string GitStatus,
    [property: JsonPropertyName("processId")] int? ProcessId,
    [property: JsonPropertyName("processState")] string ProcessState,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("warning")] string? Warning,
    [property: JsonPropertyName("runtimeObservedAt")] DateTimeOffset? RuntimeObservedAt = null,
    [property: JsonPropertyName("runtimeSource")] string? RuntimeSource = null);

internal sealed record BeadWorkspaceRuntimeObservation(int? ProcessId, string ProcessState, DateTimeOffset? ObservedAt,
    string? Source);

internal interface IBeadWorkspaceRuntimeObserver
{
    BeadWorkspaceRuntimeObservation Observe(string project, string beadId);
}

internal sealed class UnavailableBeadWorkspaceRuntimeObserver : IBeadWorkspaceRuntimeObserver
{
    public BeadWorkspaceRuntimeObservation Observe(string project, string beadId)
        => new(null, "unavailable", null, null);
}

internal sealed class BeadWorkspaceException(int statusCode, string title, string detail) : Exception(detail)
{
    internal int StatusCode { get; } = statusCode;
    internal string Title { get; } = title;
    internal string Detail { get; } = detail;
}

internal static class BeadWorkspaceProjector
{
    private static readonly string[] ProseFields = ["description", "design", "acceptance_criteria", "notes"];

    internal static BeadWorkspaceResponse Project(BeadSnapshot detail, IReadOnlyList<BeadsCommentSnapshot> comments,
        AttentionDeskProjection? attention, BeadWorkspaceEnvironment environment)
    {
        var bead = ProjectBead(detail);
        var commentRows = comments.Select(comment => ProjectComment(detail, comment)).ToArray();
        var selectedAttention = attention is not null && !AttentionDeskProjector.IsTerminal(detail)
            && AttentionDeskProjector.IsPaused(detail) ? attention.Item : null;
        var activeCommentId = selectedAttention is null ? null : attention!.ActiveCommentId;
        var selectionSource = activeCommentId is null ? "none" : attention!.SelectionSource;
        var history = ProjectHistory(commentRows, activeCommentId, selectionSource);
        return new(bead, ProjectRelations(detail.Payload), commentRows, history, selectedAttention, environment);
    }

    internal static BeadWorkspaceHistory ProjectHistory(IReadOnlyList<BeadWorkspaceComment> comments,
        string? activeCommentId, string selectionSource)
    {
        if (selectionSource is not ("structured" or "legacy-context" or "none"))
            throw new JsonException("history.selectionSource: unsupported selection source");
        if (activeCommentId is not null)
        {
            var activeIndex = -1;
            for (var index = 0; index < comments.Count; index++)
            {
                if (!string.Equals(comments[index].Id, activeCommentId, StringComparison.Ordinal)) continue;
                activeIndex = index;
                break;
            }
            if (activeIndex < 0 || selectionSource == "none")
                throw new JsonException("history.activeCommentId: selected comment is absent from full history");
            return new(comments.Take(activeIndex).Select(comment => comment.Id).ToArray(),
                comments.Skip(activeIndex).Select(comment => comment.Id).ToArray(), activeCommentId, selectionSource);
        }

        return new(comments.Take(Math.Max(0, comments.Count - 1)).Select(comment => comment.Id).ToArray(),
            comments.TakeLast(1).Select(comment => comment.Id).ToArray(), null, "none");
    }

    internal static BeadWorkspaceEnvironment UnavailableEnvironment(string state, string? warning = null)
        => new(state, DateTimeOffset.UtcNow, null, null, null, null, "unknown", null, "unavailable", "filesystem", warning);

    private static BeadWorkspaceBead ProjectBead(BeadSnapshot detail)
    {
        var fields = ReadUniqueProperties(detail.Payload, "$bead");
        var id = RequiredString(fields, "id", "$bead");
        if (!string.Equals(id, detail.Id, StringComparison.Ordinal))
            throw new JsonException("$bead.id: detail payload ID does not match the requested Bead");
        var title = RequiredString(fields, "title", "$bead");
        var updatedAt = RequiredTimestamp(fields, "updated_at", "$bead");
        var createdBy = OptionalString(fields, "created_by", "$bead");
        var closedAt = OptionalTimestamp(fields, "closed_at", "$bead");
        var prose = ProseFields.ToDictionary(name => name, name => OptionalProse(fields, name, "$bead"), StringComparer.Ordinal);
        var invalidWorkflow = AttentionDeskProjector.InvalidWorkflowReason(detail) is not null;
        return new BeadWorkspaceBead(detail.Id, title, detail.IssueType, detail.Priority, detail.Status,
            detail.Assignee, detail.Labels, AttentionDeskProjector.Stage(detail),
            invalidWorkflow ? null : AttentionDeskProjector.RepairCycles(detail), detail.CreatedAt.ToUniversalTime(),
            updatedAt, closedAt, createdBy, prose["description"], prose["design"], prose["acceptance_criteria"], prose["notes"]);
    }

    private static BeadWorkspaceComment ProjectComment(BeadSnapshot bead, BeadsCommentSnapshot comment)
    {
        var classification = AttentionDeskProjector.ClassifyComment(comment, bead);
        return new(comment.Id, comment.Author, comment.CreatedAt.ToUniversalTime(), comment.Text,
            classification.Kind, classification.Role, classification.Stage);
    }

    private static BeadWorkspaceRelation[] ProjectRelations(JsonElement payload)
    {
        var fields = ReadUniqueProperties(payload, "$bead");
        var relations = new List<BeadWorkspaceRelation>();
        AddRelations("dependencies", "outgoing");
        AddRelations("dependents", "incoming");
        return relations
            .DistinctBy(relation => (relation.Id, relation.Type, relation.Direction))
            .OrderBy(relation => relation.Direction, StringComparer.Ordinal)
            .ThenBy(relation => relation.Id, StringComparer.Ordinal)
            .ThenBy(relation => relation.Type, StringComparer.Ordinal)
            .ToArray();

        void AddRelations(string name, string direction)
        {
            if (!fields.TryGetValue(name, out var array)) return;
            if (array.ValueKind != JsonValueKind.Array)
                throw new JsonException($"$bead.{name}: expected an array");
            var row = 0;
            foreach (var item in array.EnumerateArray())
            {
                var path = $"$bead.{name}[{row++}]";
                var relationFields = ReadUniqueProperties(item, path);
                var id = RequiredString(relationFields, "id", path);
                var type = RequiredString(relationFields, "dependency_type", path);
                relations.Add(new(id, type, direction, OptionalString(relationFields, "title", path),
                    OptionalString(relationFields, "status", path)));
            }
        }
    }

    private static Dictionary<string, JsonElement> ReadUniqueProperties(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException($"{path}: expected an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value))
                throw new JsonException($"{path}.{property.Name}: duplicate property");
        return fields;
    }

    private static string RequiredString(Dictionary<string, JsonElement> fields, string name, string path)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new JsonException($"{path}.{name}: expected a nonblank string");
        return value.GetString()!;
    }

    private static string? OptionalString(Dictionary<string, JsonElement> fields, string name, string path)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"{path}.{name}: expected a string or null");
        return value.GetString();
    }

    private static string OptionalProse(Dictionary<string, JsonElement> fields, string name, string path)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"{path}.{name}: expected a string or null");
        return value.GetString() ?? string.Empty;
    }

    private static DateTimeOffset RequiredTimestamp(Dictionary<string, JsonElement> fields, string name, string path)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out var parsed))
            throw new JsonException($"{path}.{name}: expected an ISO timestamp");
        return parsed.ToUniversalTime();
    }

    private static DateTimeOffset? OptionalTimestamp(Dictionary<string, JsonElement> fields, string name, string path)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed))
            throw new JsonException($"{path}.{name}: expected an ISO timestamp or null");
        return parsed.ToUniversalTime();
    }
}
