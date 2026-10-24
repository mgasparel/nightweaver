using System.Text.Json;

namespace Nightweaver.Inspection;

internal sealed record BeadSnapshot(string Id, string Status, string IssueType, int Priority,
    DateTimeOffset CreatedAt, string? Assignee, IReadOnlyList<string> Labels, JsonElement Payload);
