using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Telemetry;

namespace Nightweaver.Web;

internal static partial class FactoryTelemetryEndpoints
{
    private const int MaximumReplayBytes = 64 * 1024;
    private const int MaximumChunkBytes = 16 * 1024;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    internal static void MapFactoryTelemetry(this WebApplication app)
    {
        app.MapGet("/api/factory/state", (HttpContext context, FactoryTelemetryReader reader) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid factory state query", detail: "Query parameters are not supported.");
            var result = reader.Read();
            return Results.Json(new
            {
                availability = result.Availability.ToString().ToLowerInvariant(),
                reason = result.Reason,
                snapshot = result.Snapshot
            }, StateJsonOptions);
        });

        app.MapGet("/api/telemetry/stream", StreamAsync);
    }

    private static async Task<IResult> StreamAsync(HttpContext context, HostPaths paths,
        ProjectConfig project, FactoryTelemetryReader reader)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (!TryReadSingle(context.Request.Query, "runtime_id", out var runtimeId)
            || !TryReadSingle(context.Request.Query, "slot_id", out var slotId)
            || !TryReadSingle(context.Request.Query, "invocation_id", out var invocationId))
            return BadRequest("runtime_id, slot_id and invocation_id must each appear exactly once.");
        if (context.Request.Query.Keys.Any(key => key is not ("runtime_id" or "slot_id" or "invocation_id" or "cursor")))
            return BadRequest("The stream query contains an unsupported parameter.");
        if (!RuntimeIdPattern().IsMatch(runtimeId) || !SlotIdPattern().IsMatch(slotId)
            || !InvocationIdPattern().IsMatch(invocationId))
            return BadRequest("The selected runtime, slot or invocation identity is invalid.");

        var headerValues = context.Request.Headers["Last-Event-ID"];
        if (headerValues.Count > 1)
            return BadRequest("Last-Event-ID may appear only once.");
        var cursorValues = context.Request.Query["cursor"];
        if (headerValues.Count == 0 && cursorValues.Count > 1)
            return BadRequest("cursor may appear only once.");

        var read = reader.Read();
        if (read.Snapshot is not { } snapshot || snapshot.RuntimeId != runtimeId)
            return Results.NotFound();
        var selection = FindSelection(snapshot, slotId, invocationId);
        if (selection is null) return Results.NotFound();

        long? requestedCursor = null;
        if (headerValues.Count == 1 && !string.IsNullOrEmpty(headerValues[0]))
        {
            if (!TryParseEventId(headerValues[0]!, runtimeId, invocationId, out var parsed))
                return BadRequest("Last-Event-ID is malformed or belongs to another invocation.");
            requestedCursor = parsed;
        }
        else if (headerValues.Count == 0 && cursorValues.Count == 1)
        {
            if (!long.TryParse(cursorValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                return BadRequest("cursor must be a nonnegative byte offset.");
            requestedCursor = parsed;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.Connection = "keep-alive";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
        using var opened = InvocationRuntimeFiles.OpenExistingLiveLogForRead(paths, project.Name,
            selection.Value.Invocation.BeadId, invocationId);
        var stream = opened.Stream;
        var length = stream.Length;
        var cursor = requestedCursor ?? Math.Max(0, length - MaximumReplayBytes);
        var reset = requestedCursor is null && length > MaximumReplayBytes;
        if (cursor > length)
        {
            cursor = length;
            reset = true;
        }
        var unalignedCursor = cursor;
        cursor = AlignUtf8Start(stream, cursor, length);
        reset |= unalignedCursor != cursor;
        if (reset)
            await WriteEventAsync(context, "reset", new { runtimeId, invocationId, cursor },
                EventId(runtimeId, invocationId, cursor), context.RequestAborted).ConfigureAwait(false);

        var initialInvocation = selection.Value.Invocation;
        await WriteEventAsync(context, "status", new
        {
            runtimeId,
            slotId,
            invocationId,
            availability = read.Availability.ToString().ToLowerInvariant(),
            status = initialInvocation.Status
        }, EventId(runtimeId, invocationId, cursor), context.RequestAborted).ConfigureAwait(false);

        var readOffset = cursor;
        var safeCursor = cursor;
        byte[] pending = [];
        var lastRefresh = DateTimeOffset.UtcNow;
        var lastHeartbeat = DateTimeOffset.UtcNow;
        var staleSince = read.Availability == FactoryTelemetryAvailability.Live ? (DateTimeOffset?)null : DateTimeOffset.UtcNow;
        var selectedInvocation = initialInvocation;
        var selectedAvailability = read.Availability;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var chunk = ReadChunk(stream, readOffset, MaximumChunkBytes);
            if (chunk.Truncated)
            {
                readOffset = 0;
                safeCursor = 0;
                pending = [];
                await WriteEventAsync(context, "reset", new { runtimeId, invocationId, cursor = 0, reason = "truncated" },
                    EventId(runtimeId, invocationId, 0), context.RequestAborted).ConfigureAwait(false);
                chunk = ReadChunk(stream, readOffset, MaximumChunkBytes);
            }
            if (chunk.Bytes.Length > 0)
            {
                readOffset += chunk.Bytes.Length;
                var decoded = DecodeAvailable(pending, chunk.Bytes, safeCursor);
                pending = decoded.Pending;
                safeCursor = decoded.Cursor;
                if (decoded.Text.Length > 0)
                    await WriteEventAsync(context, "log", new
                    {
                        runtimeId,
                        slotId,
                        invocationId,
                        text = decoded.Text,
                        cursor = safeCursor
                    }, EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            if (now - lastRefresh >= RefreshInterval)
            {
                lastRefresh = now;
                var current = reader.Read();
                if (current.Snapshot is null || current.Snapshot.RuntimeId != runtimeId)
                {
                    await WriteEventAsync(context, "error", new { runtimeId, invocationId, reason = "runtime_lost" },
                        EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
                    await WriteEventAsync(context, "end", new { runtimeId, invocationId, reason = "runtime_lost" },
                        EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
                    break;
                }
                var currentSelection = FindSelection(current.Snapshot, slotId, invocationId);
                if (currentSelection is null)
                {
                    await WriteEventAsync(context, "end", new { runtimeId, invocationId, reason = "slot_reused" },
                        EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
                    break;
                }
                selectedAvailability = current.Availability;
                selectedInvocation = currentSelection.Value.Invocation;
                if (selectedAvailability != FactoryTelemetryAvailability.Live)
                {
                    staleSince ??= now;
                    await WriteEventAsync(context, "status", new
                    {
                        runtimeId,
                        slotId,
                        invocationId,
                        availability = selectedAvailability.ToString().ToLowerInvariant(),
                        status = selectedInvocation.Status
                    }, EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
                }
                else staleSince = null;
            }

            var terminal = selectedInvocation.Status is "completed" or "failed" or "timed_out" or "cancelled"
                or "launch_failed" or "launch_denied" or "preparation_failed"
                || selectedAvailability == FactoryTelemetryAvailability.Stopped;
            if (terminal && pending.Length > 0)
            {
                safeCursor += pending.Length;
                pending = [];
                await WriteEventAsync(context, "log", new
                {
                    runtimeId,
                    slotId,
                    invocationId,
                    text = "\uFFFD",
                    cursor = safeCursor
                }, EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
            }
            if (terminal && chunk.Bytes.Length == 0)
            {
                await WriteEventAsync(context, "end", new { runtimeId, invocationId, status = selectedInvocation.Status },
                    EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
                break;
            }
            if (staleSince is { } unavailableSince && now - unavailableSince > TimeSpan.FromSeconds(10))
            {
                await WriteEventAsync(context, "end", new { runtimeId, invocationId, reason = "runtime_stale" },
                    EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
                break;
            }
            if (now - lastHeartbeat >= TimeSpan.FromSeconds(15))
            {
                lastHeartbeat = now;
                await WriteEventAsync(context, "heartbeat", new { runtimeId, invocationId, cursor = safeCursor },
                    EventId(runtimeId, invocationId, safeCursor), context.RequestAborted).ConfigureAwait(false);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), context.RequestAborted).ConfigureAwait(false);
        }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            // A stalled response is abandoned without affecting the native Agent or its log pump.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or PlatformNotSupportedException)
        {
            if (!context.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await WriteEventAsync(context, "error", new { runtimeId, invocationId, reason = "log_unavailable" },
                        EventId(runtimeId, invocationId, 0), context.RequestAborted).ConfigureAwait(false);
                    await WriteEventAsync(context, "end", new { runtimeId, invocationId, reason = "log_unavailable" },
                        EventId(runtimeId, invocationId, 0), context.RequestAborted).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        return Results.Empty;
    }

    private static (FactorySlotObservation Slot, FactoryInvocationObservation Invocation)? FindSelection(
        FactoryTelemetrySnapshot snapshot, string slotId, string invocationId)
    {
        var pool = slotId.StartsWith("delivery-", StringComparison.Ordinal) ? snapshot.Delivery : snapshot.Merger;
        var slot = pool.Slots.SingleOrDefault(candidate => candidate.SlotId == slotId);
        var invocation = slot?.Invocation;
        return invocation?.InvocationId == invocationId ? (slot!, invocation) : null;
    }

    private static (byte[] Bytes, bool Truncated) ReadChunk(Stream stream, long offset, int maximumBytes)
    {
        if (stream.Length < offset) return ([], true);
        stream.Position = offset;
        var remaining = Math.Min(maximumBytes, stream.Length - offset);
        if (remaining <= 0) return ([], false);
        var buffer = new byte[(int)remaining];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return (total == buffer.Length ? buffer : buffer[..total], false);
    }

    private static long AlignUtf8Start(Stream stream, long cursor, long length)
    {
        if (cursor <= 0 || cursor >= length) return cursor;
        stream.Position = cursor;
        var byteValue = stream.ReadByte();
        var checkedBytes = 0;
        while (byteValue >= 0 && (byteValue & 0xC0) == 0x80 && checkedBytes < 3)
        {
            cursor++;
            checkedBytes++;
            byteValue = stream.ReadByte();
        }
        return cursor;
    }

    private static (string Text, byte[] Pending, long Cursor) DecodeAvailable(byte[] pending, byte[] bytes, long cursor)
    {
        var combined = new byte[pending.Length + bytes.Length];
        pending.CopyTo(combined, 0);
        bytes.CopyTo(combined, pending.Length);
        var text = new StringBuilder(combined.Length);
        var offset = 0;
        while (offset < combined.Length)
        {
            var status = Rune.DecodeFromUtf8(combined.AsSpan(offset), out var rune, out var consumed);
            if (status == OperationStatus.NeedMoreData)
                return (text.ToString(), combined[offset..], cursor + offset);
            if (status == OperationStatus.InvalidData)
            {
                text.Append('\uFFFD');
                consumed = 1;
            }
            else text.Append(rune.ToString());
            offset += consumed;
        }
        return (text.ToString(), [], cursor + offset);
    }

    private static async Task WriteEventAsync(HttpContext context, string eventName, object data,
        string eventId, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var json = JsonSerializer.Serialize(data, SseJsonOptions);
        var frame = $"id: {eventId}\nevent: {eventName}\ndata: {json}\n\n";
        await context.Response.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private static string EventId(string runtimeId, string invocationId, long cursor)
        => $"{runtimeId}.{invocationId}.{cursor.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParseEventId(string value, string runtimeId, string invocationId, out long cursor)
    {
        cursor = 0;
        var prefix = runtimeId + "." + invocationId + ".";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out cursor)
            && cursor >= 0;
    }

    private static bool TryReadSingle(IQueryCollection query, string key, out string value)
    {
        var values = query[key];
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return values.Count == 1 && value.Length is > 0 and <= 128;
    }

    private static IResult BadRequest(string detail) => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid telemetry stream request", detail: detail);

    [GeneratedRegex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeIdPattern();

    [GeneratedRegex("^(delivery-[1-9][0-9]*|merger-1)$", RegexOptions.CultureInvariant)]
    private static partial Regex SlotIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{20,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex InvocationIdPattern();
}
