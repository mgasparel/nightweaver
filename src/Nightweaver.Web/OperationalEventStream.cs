using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Nightweaver.Web;

// Subscriber callbacks never wait for network I/O or retain a replay log.
internal sealed class OperationalEventStream(OperationalObservationService observer, TimeProvider clock,
    IHostApplicationLifetime lifetime) : IDisposable
{
    private readonly SemaphoreSlim capacity = new(16, 16);
    public void Dispose() => capacity.Dispose();

    internal async Task RunAsync(HttpContext context, string projectId)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (projectId != observer.ProjectId) { context.Response.StatusCode = 404; return; }
        var query = context.Request.Query;
        if (Encoding.UTF8.GetByteCount(context.Request.PathBase + context.Request.Path + context.Request.QueryString) > 8192
            || query.Count != 1 || !query.TryGetValue("key", out var values))
        { context.Response.StatusCode = 400; return; }
        if (!capacity.Wait(0)) { Reject(context, 429); return; }
        using var queue = new Invalidations();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var token = connection.Token;
        try
        {
            using var interest = observer.Subscribe(values.Select(value => value!).ToArray(), queue.Add);
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await WriteAsync("hello", new { versions = interest.Versions, heartbeatMs = 10000 });
            await WriteAsync("resync", new { reason = "reconnect" });
            var nextHeartbeat = clock.GetUtcNow().AddSeconds(10);
            while (!token.IsCancellationRequested)
            {
                var delay = nextHeartbeat - clock.GetUtcNow();
                if (delay <= TimeSpan.Zero)
                {
                    await WriteAsync("heartbeat", new { at = clock.GetUtcNow() });
                    nextHeartbeat = clock.GetUtcNow().AddSeconds(10);
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(delay);
                try { await queue.Signal.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                var (versions, overflow) = queue.Take();
                if (overflow) await WriteAsync("resync", new { reason = "overflow" });
                else if (versions.Count > 0) await WriteAsync("invalidation", new { versions });
            }
        }
        catch (ObservationRequestException exception) when (!context.Response.HasStarted) { Reject(context, exception.StatusCode); }
        catch (OperationCanceledException) { context.Abort(); }
        catch (IOException) { context.Abort(); }
        finally { capacity.Release(); }

        async Task WriteAsync(string name, object payload)
        {
            var data = JsonSerializer.SerializeToNode(payload, ObservationJson.Options)!.AsObject();
            data["schema"] = "nightweaver-operations-v1";
            data["projectId"] = observer.ProjectId;
            data["epoch"] = observer.Epoch;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var frame = Encoding.UTF8.GetBytes($"event: {name}\ndata: {data.ToJsonString(ObservationJson.Options)}\n\n");
            await context.Response.Body.WriteAsync(frame, timeout.Token);
            await context.Response.Body.FlushAsync(timeout.Token);
        }
    }

    private static void Reject(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        if (status == 429) context.Response.Headers.RetryAfter = "2";
    }

    internal sealed class Invalidations : IDisposable
    {
        private readonly object sync = new();
        private readonly Dictionary<string, string> pending = new(StringComparer.Ordinal);
        private bool overflow;
        internal Channel<bool> Signal { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropWrite });

        internal void Add(string key, string revision)
        {
            lock (sync)
            {
                if (!overflow)
                {
                    pending[key] = revision;
                    if (pending.Count > 64 || pending.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + pair.Value.Length + 8) > 64 * 1024)
                    { pending.Clear(); overflow = true; }
                }
                Signal.Writer.TryWrite(true);
            }
        }

        internal (Dictionary<string, string> Versions, bool Overflow) Take()
        {
            lock (sync)
            {
                var result = (new Dictionary<string, string>(pending, StringComparer.Ordinal), overflow);
                pending.Clear(); overflow = false;
                return result;
            }
        }
        public void Dispose() => Signal.Writer.TryComplete();
    }
}
