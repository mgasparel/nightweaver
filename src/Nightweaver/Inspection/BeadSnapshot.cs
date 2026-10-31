using System.Text.Json;

namespace Nightweaver.Inspection;

internal sealed record BeadSnapshot(string Id, string Status, string IssueType, int Priority,
    DateTimeOffset CreatedAt, string? Assignee, IReadOnlyList<string> Labels, JsonElement Payload);

internal sealed record BeadCommentSnapshot(string Id, string Text, DateTimeOffset CreatedAt);
internal sealed record BeadsCommentSnapshot(string Id, string IssueId, string Author, string Text,
    DateTimeOffset CreatedAt);
