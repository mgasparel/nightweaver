using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record ProductBeadIntakeParseResult(
    ProductBeadIntakeSubmission? Submission,
    Dictionary<string, string[]> Errors);

internal static class ProductBeadIntakeRequest
{
    internal static ProductBeadIntakeParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException)
        {
            AddError(errors, "request", "The request body must be valid JSON.");
            return new ProductBeadIntakeParseResult(null, Freeze(errors));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                AddError(errors, "request", "The request body must be a JSON object.");
                return new ProductBeadIntakeParseResult(null, Freeze(errors));
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                string propertyName;
                try
                {
                    propertyName = property.Name;
                }
                catch (InvalidOperationException)
                {
                    AddError(errors, "request", "Property names must contain valid Unicode.");
                    continue;
                }

                if (!IsAllowedProperty(propertyName))
                {
                    AddError(errors, propertyName, "This property is not allowed.");
                    continue;
                }

                if (!values.TryAdd(propertyName, property.Value))
                    AddError(errors, propertyName, "This property may be supplied only once.");
            }

            foreach (var name in new[] { "type", "priority", "title", "description", "acceptanceCriteria" })
            {
                if (!values.ContainsKey(name))
                    AddError(errors, name, "This field is required.");
            }

            var type = ReadType(values, errors);
            var priority = ReadPriority(values, errors);
            var title = ReadText(values, "title", 200, errors, singleLine: true);
            var description = ReadText(values, "description", 4000, errors, singleLine: false);
            var acceptance = ReadText(values, "acceptanceCriteria", 2000, errors, singleLine: false);

            var submission = errors.Count == 0
                ? new ProductBeadIntakeSubmission(type!, priority!.Value, title!, description!, acceptance!)
                : null;
            return new ProductBeadIntakeParseResult(submission, Freeze(errors));
        }
    }

    private static bool IsAllowedProperty(string name)
        => name is "type" or "priority" or "title" or "description" or "acceptanceCriteria";

    private static string? ReadType(Dictionary<string, JsonElement> values, Dictionary<string, List<string>> errors)
    {
        if (!values.TryGetValue("type", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(errors, "type", "Choose feature or bug.");
            return null;
        }

        var type = ReadJsonString(value, "type", errors);
        if (type is null) return null;
        if (type is not ("feature" or "bug"))
        {
            AddError(errors, "type", "Choose feature or bug.");
            return null;
        }
        return type;
    }

    private static int? ReadPriority(Dictionary<string, JsonElement> values, Dictionary<string, List<string>> errors)
    {
        if (!values.TryGetValue("priority", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var priority) || priority is < 0 or > 3)
        {
            AddError(errors, "priority", "Priority must be an integer from 0 through 3.");
            return null;
        }
        return priority;
    }

    private static string? ReadText(Dictionary<string, JsonElement> values, string name, int maxLength,
        Dictionary<string, List<string>> errors, bool singleLine)
    {
        if (!values.TryGetValue(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(errors, name, "This field must be a string.");
            return null;
        }

        var raw = ReadJsonString(value, name, errors);
        if (raw is null) return null;
        if (raw.Contains('\0'))
        {
            AddError(errors, name, "NUL characters are not allowed.");
            return null;
        }

        if (raw.Any(character => IsDisallowedCharacter(character, singleLine)))
        {
            AddError(errors, name, singleLine
                ? "Title cannot contain line breaks or control characters."
                : "Only tabs and line breaks are allowed as control characters.");
            return null;
        }

        var normalized = raw.Trim();
        if (normalized.Length == 0)
        {
            AddError(errors, name, "This field cannot be blank.");
            return null;
        }
        if (normalized.Length > maxLength)
        {
            AddError(errors, name, $"This field cannot exceed {maxLength} characters.");
            return null;
        }
        return normalized;
    }

    private static string? ReadJsonString(JsonElement value, string field, Dictionary<string, List<string>> errors)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            AddError(errors, field, "This field must contain valid Unicode.");
            return null;
        }
    }

    private static bool IsDisallowedCharacter(char character, bool singleLine)
    {
        if (singleLine)
        {
            return char.IsControl(character) || character is '\u2028' or '\u2029';
        }

        return char.IsControl(character) && character is not ('\r' or '\n' or '\t');
    }

    private static Dictionary<string, string[]> Freeze(Dictionary<string, List<string>> errors)
        => errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);

    private static void AddError(Dictionary<string, List<string>> errors, string name, string message)
    {
        if (!errors.TryGetValue(name, out var messages))
        {
            messages = [];
            errors.Add(name, messages);
        }
        if (!messages.Contains(message, StringComparer.Ordinal))
            messages.Add(message);
    }
}

internal static class BoundedRequestBody
{
    internal const int MaximumBytes = 64 * 1024;

    internal static async Task<byte[]?> ReadAsync(HttpRequest request, CancellationToken cancellationToken,
        int maximumBytes = MaximumBytes)
    {
        if (request.ContentLength is { } contentLength && contentLength > maximumBytes)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > maximumBytes) return null;
            buffer.Write(chunk, 0, count);
        }
    }
}
