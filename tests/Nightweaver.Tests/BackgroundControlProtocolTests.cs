using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Nightweaver.Background;

namespace Nightweaver.Tests;

public sealed class BackgroundControlProtocolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ExpiredClientDoesNotBlockLaterControlRequests()
    {
        var pipeName = "Nightweaver-Test-" + Guid.NewGuid().ToString("N");
        var cancellationToken = TestContext.Current.CancellationToken;
        using var serverLifetime = new CancellationTokenSource();
        var slowHandlerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var largeResponseStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusRequests = 0;
        var clock = new ClientDeadlineClock();
        var server = BackgroundControlProtocol.RunServerAsync(pipeName,
            async (request, requestToken) =>
            {
                requestToken.ThrowIfCancellationRequested();
                if (request.Operation == "slow-handler")
                {
                    slowHandlerStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, requestToken);
                    requestToken.ThrowIfCancellationRequested();
                }

                if (request.Operation == "large-response")
                {
                    largeResponseStarted.TrySetResult(true);
                    return new BackgroundControlResponse(true, null, new string('x', 60 * 1024));
                }

                Interlocked.Increment(ref statusRequests);
                return new BackgroundControlResponse(true, null, "status-ok");
            }, serverLifetime.Token, clientTimeout: TimeSpan.FromMilliseconds(250), timeProvider: clock);

        try
        {
            await using (var idle = await ConnectClientAsync(pipeName, cancellationToken))
            {
                (await clock.NextAsync(cancellationToken)).Expire();
                var response = await RequestDelayedStatusAsync(pipeName, clock, cancellationToken);
                Assert.Equal("status-ok", response.Error);
            }

            await using (var partial = await ConnectClientAsync(pipeName, cancellationToken))
            {
                await partial.WriteAsync(new byte[] { 1, 0 }, cancellationToken);
                await partial.FlushAsync(cancellationToken);
                (await clock.NextAsync(cancellationToken)).Expire();
                var response = await RequestStatusAsync(pipeName, clock, cancellationToken);
                Assert.Equal("status-ok", response.Error);
            }

            var slowRequest = BackgroundControlProtocol.SendAsync(pipeName,
                new BackgroundControlRequest("slow-handler", "test-run"), TimeSpan.FromSeconds(3),
                cancellationToken);
            await slowHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            (await clock.NextAsync(cancellationToken)).Expire();
            var afterSlowHandler = await RequestStatusAsync(pipeName, clock, cancellationToken);
            Assert.Equal("status-ok", afterSlowHandler.Error);
            var slowRequestError = await Record.ExceptionAsync(async () => await slowRequest);
            Assert.IsAssignableFrom<IOException>(slowRequestError);

            await using (var nonReading = await ConnectClientAsync(pipeName, cancellationToken))
            {
                await WriteRequestAsync(nonReading, new BackgroundControlRequest("large-response", "test-run"),
                    cancellationToken);
                await largeResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                (await clock.NextAsync(cancellationToken)).Expire();
                var response = await RequestStatusAsync(pipeName, clock, cancellationToken);
                Assert.Equal("status-ok", response.Error);
                var partialResponse = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await nonReading.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    partialResponse.Write(buffer, 0, count);
                }
                Assert.True(partialResponse.Length < 60 * 1024,
                    "server must drop a peer that does not drain the framed response before its deadline");
            }

            Assert.Equal(4, statusRequests);
        }
        finally
        {
            serverLifetime.Cancel();
            await server.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private static async Task<NamedPipeClientStream> ConnectClientAsync(string pipeName,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task<BackgroundControlResponse> RequestStatusAsync(string pipeName,
        ClientDeadlineClock clock, CancellationToken cancellationToken)
    {
        var response = BackgroundControlProtocol.SendAsync(pipeName,
            new BackgroundControlRequest("status", "test-run"), TimeSpan.FromSeconds(3), cancellationToken);
        _ = await clock.NextAsync(cancellationToken);
        return await response;
    }

    private static async Task<BackgroundControlResponse> RequestDelayedStatusAsync(string pipeName,
        ClientDeadlineClock clock, CancellationToken token)
    {
        await using var client = await ConnectClientAsync(pipeName, token);
        _ = await clock.NextAsync(token);
        // Model a healthy client descheduled beyond the old test's wall deadline.
        await Task.Delay(TimeSpan.FromMilliseconds(350), token);
        await WriteRequestAsync(client, new BackgroundControlRequest("status", "test-run"), token);
        var prefix = new byte[sizeof(int)];
        await client.ReadExactlyAsync(prefix, token);
        var payload = new byte[BitConverter.ToInt32(prefix)];
        await client.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<BackgroundControlResponse>(payload, JsonOptions)!;
    }

    // Expire only the deliberately stalled peer. Real 250 ms timers also expired
    // healthy requests when the CI worker was descheduled, obscuring recovery.
    private sealed class ClientDeadlineClock : TimeProvider
    {
        private readonly Channel<ClientDeadline> deadlines = Channel.CreateUnbounded<ClientDeadline>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), dueTime);
            var deadline = new ClientDeadline(callback, state);
            deadlines.Writer.TryWrite(deadline);
            return deadline;
        }

        internal async Task<ClientDeadline> NextAsync(CancellationToken token) =>
            await deadlines.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
    }

    private sealed class ClientDeadline(TimerCallback callback, object? state) : ITimer
    {
        private int disposed;
        internal void Expire() { if (Volatile.Read(ref disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private static async Task WriteRequestAsync(Stream pipe, BackgroundControlRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await pipe.WriteAsync(payload, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }
}
