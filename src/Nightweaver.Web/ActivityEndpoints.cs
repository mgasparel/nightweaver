using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Nightweaver.ActivityFeed;
using Nightweaver.Configuration;

namespace Nightweaver.Web;

internal static class ActivityEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    internal static void AddActivity(this IServiceCollection services)
    {
        services.AddSingleton<IActivityReadSource, ActivityReadSource>();
        services.AddSingleton(s => new ActivityReadService(s.GetRequiredService<IActivityReadSource>(),
            s.GetRequiredService<TimeProvider>(), s.GetRequiredService<ObservationMemoryBudget>(),
            s.GetRequiredService<ProjectConfig>().Name.Value));
        services.AddHostedService(s => s.GetRequiredService<ActivityReadService>());
    }

    internal static void MapActivity(this WebApplication app)
    {
        app.MapGet("/api/activity/v1/projects/{projectId}/history", History);
        app.MapGet("/api/activity/v1/projects/{projectId}/events", StreamAsync);
    }

    private static async Task<IResult> History(HttpContext context, string projectId, ProjectConfig project, ActivityReadService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (projectId != project.Name.Value) return Results.NotFound();
        if (!TryRequest(context.Request, false, out var cursor, out var filter)) return Results.BadRequest();
        if (!await service.InitializeCursorAsync(cursor, context.RequestAborted).ConfigureAwait(false))
            return Results.StatusCode(503);
        try { return Results.Json(service.Read(cursor, filter!), Json); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> StreamAsync(HttpContext context, string projectId, ProjectConfig project,
        ActivityReadService service, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (projectId != project.Name.Value) return Results.NotFound();
        if (!TryRequest(context.Request, true, out var cursor, out var filter)) return Results.BadRequest();
        if (!service.TryEnterStream())
        { context.Response.Headers.RetryAfter = "2"; return Results.StatusCode(429); }
        try
        {
            if (!await service.InitializeCursorAsync(cursor, context.RequestAborted).ConfigureAwait(false))
                return Results.StatusCode(503);
            ActivityReadPage page;
            try { page = service.Read(cursor, filter!); }
            catch (ArgumentException) { return Results.BadRequest(); }
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            string? previousRuntime = null;
            while (!context.RequestAborted.IsCancellationRequested)
            {
                // Status precedes resumed data, including every reconnect and gap.
                // Each complete item advances Last-Event-ID. The final cursor
                // also advances over filtered or unavailable positions.
                var batch = EncodeBatch(page, cursor, previousRuntime);
                await WriteAsync(context, batch, clock).ConfigureAwait(false);
                if (page.RuntimeId is not null) previousRuntime = page.RuntimeId;
                if (page.Cursor is { } value) { TryCursor(value, out cursor); }
                await Task.Delay(ActivityLimits.Refresh, clock, context.RequestAborted).ConfigureAwait(false);
                page = service.Read(cursor, filter!);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ArgumentException) { context.Abort(); }
        finally { service.ExitStream(); }
        return Results.Empty;
    }

    internal static string EncodeBatch(ActivityReadPage page, ActivityCursor? cursor = null, string? previousRuntime = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            page.Oldest, page.Newest, page.LoadedThrough, page.Loading, page.State,
            page.JournalHealth, page.AttemptedAt, page.ObservedAt, page.RuntimeHealth,
            page.RuntimeId, page.RuntimeObservedAt, page.Details,
            reconnectGap = cursor is not null && (page.JournalHealth != ActivityFreshness.Fresh || page.State.HasFlag(ActivityPageState.Gap)),
            runtimeReplaced = page.RuntimeReplaced || previousRuntime is not null && page.RuntimeId is not null && previousRuntime != page.RuntimeId
        }, Json);
        var batch = new StringBuilder("event: status\ndata: " + payload + "\n\n");
        foreach (var record in page.Records) batch.Append(EncodeRecord(record));
        if (page.Cursor is { } next) batch.Append("event: cursor\nid: " + next + "\ndata: {}\n\n");
        return batch.ToString();
    }

    internal static string EncodeRecord(ActivityRecord record) =>
        "event: activity\nid: " + FormatCursor(new(1, record.Position.Journal, record.Position.Offset))
        + "\ndata: " + Encoding.UTF8.GetString(ActivityProjector.Serialize(record)) + "\n\n";

    internal static async Task WriteAsync(HttpContext context, string batch, TimeProvider clock)
    {
        using var deadline = new CancellationTokenSource(ActivityLimits.WriteDeadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, context.RequestAborted);
        // WaitAsync also bounds streams that ignore cancellation. Abort the transport
        // and retain no second pending batch when the write fails to settle.
        var write = context.Response.WriteAsync(batch, linked.Token);
        try { await write.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch
        {
            context.Abort();
            _ = write.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal static string FormatCursor(ActivityCursor cursor) =>
        "1." + cursor.Journal.ToString("N") + "." + cursor.Offset.ToString(CultureInfo.InvariantCulture);

    internal static bool TryCursor(string value, out ActivityCursor? cursor)
    {
        cursor = null;
        if (value.Length is < 36 or > 54) return false;
        var parts = value.Split('.');
        if (parts.Length != 3 || parts[0] != "1" || !Guid.TryParseExact(parts[1], "N", out var journal)
            || journal == Guid.Empty || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0) return false;
        cursor = new(1, journal, offset);
        return value == FormatCursor(cursor);
    }

    internal static bool TryRequest(HttpRequest request, bool stream, out ActivityCursor? cursor, out ActivitySelection? filter)
    {
        cursor = null; filter = null;
        // Check raw text before query parsing or enum splitting allocates arrays.
        var header = request.Headers["Last-Event-ID"];
        if (Encoding.UTF8.GetByteCount(request.QueryString.Value ?? "") + Encoding.UTF8.GetByteCount(header.ToString()) > 1024
            || header.Count > 1 || !stream && header.Count != 0) return false;
        var query = request.Query;
        if (query.Keys.Any(k => k is not ("cursor" or "kind" or "phase" or "strength" or "bead" or "level"))
            || query.Any(p => p.Value.Count != 1)) return false;
        if (header.Count != 0 && query.ContainsKey("cursor")) return false;
        var text = header.Count == 1 ? header[0] : query["cursor"].FirstOrDefault();
        if (text is not null && !TryCursor(text, out cursor)) return false;
        if (!Values<ActivityKind>(query["kind"].FirstOrDefault(), 25, out var kinds)
            || !Values<ActivityPhase>(query["phase"].FirstOrDefault(), 5, out var phases)
            || !Values<ActivityStrength>(query["strength"].FirstOrDefault(), 4, out var strengths)) return false;
        var bead = query["bead"].FirstOrDefault();
        if (bead is not null && !ActivityProjector.SafeIdentifier(bead)) return false;
        var level = query["level"].FirstOrDefault();
        if (level is not (null or "work" or "warning" or "error")) return false;
        filter = new(kinds, phases, strengths, bead, level);
        return true;
    }

    private static bool Values<T>(string? text, int maximum, out T[] values) where T : struct, Enum
    {
        values = [];
        if (text is null) return true;
        var parts = text.Split(',');
        if (parts.Length > maximum || parts.Any(p => !Enum.GetNames<T>().Contains(p, StringComparer.Ordinal))) return false;
        values = parts.Select(Enum.Parse<T>).ToArray();
        return values.Distinct().Count() == values.Length;
    }
}
