using System.Text.Json;
using System.Text;

namespace Nightweaver.Inspection;

internal sealed record BeadSnapshot(string Id, string Status, string IssueType, int Priority,
    DateTimeOffset CreatedAt, string? Assignee, IReadOnlyList<string> Labels, JsonElement Payload)
{
    internal const int MaximumDisplayTitleScalars = 512;

    // Admission metadata is optional. Invalid display data must not refuse work.
    internal string? AdmissionTitle()
    {
        if (Payload.ValueKind != JsonValueKind.Object) return null;
        string? title = null;
        var count = 0;
        foreach (var property in Payload.EnumerateObject())
        {
            if (!property.NameEquals("title")) continue;
            count++;
            if (property.Value.ValueKind == JsonValueKind.String) title = property.Value.GetString();
        }
        return count == 1 ? BoundDisplayTitle(title) : null;
    }

    internal static string? BoundDisplayTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var bounded = new StringBuilder();
        foreach (var rune in title.EnumerateRunes().Take(MaximumDisplayTitleScalars)) bounded.Append(rune);
        var result = bounded.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}

internal sealed record BeadCommentSnapshot(string Id, string Text, DateTimeOffset CreatedAt);
internal sealed record BeadsCommentSnapshot(string Id, string IssueId, string Author, string Text,
    DateTimeOffset CreatedAt);
