using System.IO.Pipes;
using System.Text.Json;
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
            }, serverLifetime.Token, clientTimeout: TimeSpan.FromMilliseconds(250));

        try
        {
            await using (var idle = await ConnectClientAsync(pipeName, cancellationToken))
            {
                var response = await RequestStatusAsync(pipeName, cancellationToken);
                Assert.Equal("status-ok", response.Error);
            }

            await using (var partial = await ConnectClientAsync(pipeName, cancellationToken))
            {
                await partial.WriteAsync(new byte[] { 1, 0 }, cancellationToken);
                await partial.FlushAsync(cancellationToken);
                var response = await RequestStatusAsync(pipeName, cancellationToken);
                Assert.Equal("status-ok", response.Error);
            }

            var slowRequest = BackgroundControlProtocol.SendAsync(pipeName,
                new BackgroundControlRequest("slow-handler", "test-run"), TimeSpan.FromSeconds(3),
                cancellationToken);
            await slowHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            var afterSlowHandler = await RequestStatusAsync(pipeName, cancellationToken);
            Assert.Equal("status-ok", afterSlowHandler.Error);
            var slowRequestError = await Record.ExceptionAsync(async () => await slowRequest);
            Assert.IsAssignableFrom<IOException>(slowRequestError);

            await using (var nonReading = await ConnectClientAsync(pipeName, cancellationToken))
            {
                await WriteRequestAsync(nonReading, new BackgroundControlRequest("large-response", "test-run"),
                    cancellationToken);
                await largeResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                var response = await RequestStatusAsync(pipeName, cancellationToken);
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

    private static Task<BackgroundControlResponse> RequestStatusAsync(string pipeName,
        CancellationToken cancellationToken) => BackgroundControlProtocol.SendAsync(pipeName,
        new BackgroundControlRequest("status", "test-run"), TimeSpan.FromSeconds(3), cancellationToken);

    private static async Task WriteRequestAsync(Stream pipe, BackgroundControlRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await pipe.WriteAsync(payload, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }
}
