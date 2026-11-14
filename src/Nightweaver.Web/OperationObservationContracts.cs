using System.Globalization;
using System.Text;
using Nightweaver.Execution;

namespace Nightweaver.Web;

internal sealed record OperationKey(string Value, string Kind, string? Argument = null, string? Query = null, int Page = 1)
{
    internal static OperationKey Parse(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 1024) throw new ObservationRequestException(400);
        var parts = value.Split(':');
        if (value is "factory" or "attention") return new(value, value);
        if (parts is ["workspace", var id] && BeadIdSafety.IsSafe(id)) return new(value, "workspace", id);
        if (parts is ["digest", var window] && window is "auto" or "since_caught_up" or "24h" or "7d" or "30d" or "all")
            return new(value, "digest", window);
        if (parts is ["explorer", var view, var query] && view is "all" or "active" or "ready" or "blocked" or "closed")
            return new(value, "explorer", view, Decode(query));
        if (parts is ["cleanup", var page, var status, var search]
            && int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 1250 && page == number.ToString(CultureInfo.InvariantCulture)
            && status is "all" or "pending" or "retry-scheduled" or "needs-attention" or "resolved")
            return new(value, "cleanup", status, Decode(search), number);
        throw new ObservationRequestException(400);
    }

    private static string Decode(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
            var query = new UTF8Encoding(false, true).GetString(bytes);
            if (query.Length > 200 || query != query.Trim()
                || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != encoded)
                throw new ObservationRequestException(400);
            return query;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        { throw new ObservationRequestException(400); }
    }
}

internal sealed class ObservationRequestException(int statusCode) : Exception
{
    internal int StatusCode { get; } = statusCode;
}

internal sealed record ObservationError(string Code, string Message, bool Retryable = true);
internal sealed record ObservationSourceState(string Health, DateTimeOffset? ObservedAt,
    DateTimeOffset? LastAttemptAt, ObservationError? Error);
internal sealed record ObservationEnvelope(string Schema, string ProjectId, string Key, string Epoch,
    string Revision, DateTimeOffset? ObservedAt, DateTimeOffset? LastAttemptAt, string Health,
    System.Text.Json.JsonElement? Data, ObservationError? Error,
    IReadOnlyDictionary<string, ObservationSourceState> Sources, int TargetIntervalMs, DateTimeOffset? NextAttemptAt);
internal sealed record ObservationSample(object? Data, DateTimeOffset AttemptedAt, DateTimeOffset? ObservedAt,
    IReadOnlyDictionary<string, ObservationSourceState> Sources, ObservationError? Error = null, bool Partial = false, bool Pending = false);

internal interface IOperationalObservationSource
{
    Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerAsync(IReadOnlyList<OperationKey> keys, CancellationToken token);
    Task<IReadOnlyDictionary<string, ObservationSample>> ReadRuntimeAsync(IReadOnlyList<OperationKey> keys, CancellationToken token);
}

internal static class ObservationJson
{
    internal static System.Text.Json.JsonSerializerOptions Options { get; } = Create();
    internal static System.Text.Json.JsonSerializerOptions Create(bool snakeCase = false)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        if (snakeCase) options.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(new UtcDateConverter());
        return options;
    }
    private sealed class UtcDateConverter : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTimeOffset value,
            System.Text.Json.JsonSerializerOptions options)
            => writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }
}
