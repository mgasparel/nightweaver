using System.IO.Pipes;
using System.Text.Json;

namespace Nightweaver.Background;

internal sealed record BackgroundControlRequest(string Operation, string RunId, bool Force = false);
internal sealed record BackgroundControlResponse(bool Accepted, BackgroundOwnerRecord? Owner, string? Error);

internal static class BackgroundControlProtocol
{
    private const int MaximumMessageBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 8
    };

    internal static async Task RunServerAsync(string pipeName,
        Func<BackgroundControlRequest, Task<BackgroundControlResponse>> handle,
        CancellationToken cancellationToken,
        Action<BackgroundControlRequest>? afterResponse = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 4096, outBufferSize: 4096);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var request = await ReadAsync<BackgroundControlRequest>(pipe, cancellationToken).ConfigureAwait(false);
                var response = request is null
                    ? new BackgroundControlResponse(false, null, "invalid control request")
                    : await handle(request).ConfigureAwait(false);
                await WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (request is not null) afterResponse?.Invoke(request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { }
        }
    }

    internal static async Task<BackgroundControlResponse> SendAsync(string pipeName,
        BackgroundControlRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            return await ReadAsync<BackgroundControlResponse>(pipe, deadline.Token).ConfigureAwait(false)
                ?? new BackgroundControlResponse(false, null, "owner returned an empty control response");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("background owner control request timed out");
        }
        catch (EndOfStreamException)
        {
            throw new IOException("background owner closed the control pipe before replying");
        }
    }

    private static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(prefix);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("background control message size is invalid");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("background control response is too large");
        var prefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}
