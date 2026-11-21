using System.Collections.Concurrent;
using System.Net;
using static Nightweaver.Tests.OperationalTelemetryTests;

namespace Nightweaver.Tests;

// Classifier tests exercise the real SDK serialization and safe export handler,
// with an immediate, empty OTLP acknowledgement. Socket scheduling belongs to
// OperationalTelemetryTests and FirstPathTelemetryTests, which retain real HTTP.
internal sealed class ClassifierTelemetryReceiver(bool holdMetricsResponse, bool rejectMetrics = false) : IDisposable
{
    internal Uri Endpoint { get; } = new("http://127.0.0.1/otlp/");
    internal ConcurrentQueue<Capture> Captures { get; } = new();
    internal ConcurrentQueue<string> CompletedSignals { get; } = new();
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool holdMetrics = holdMetricsResponse;
    private readonly bool failMetrics = rejectMetrics;

    internal HttpMessageHandler CreateTransport(string signal) => new Transport(this, signal);

    private sealed class Transport(ClassifierTelemetryReceiver receiver, string signal) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bytes = new MemoryStream();
            request.Content!.CopyTo(bytes, null, cancellationToken);
            Assert.Equal(new Uri(receiver.Endpoint, "v1/" + signal), request.RequestUri);
            receiver.Captures.Enqueue(new(signal, request.Content.Headers.ContentType?.ToString() ?? "",
                bytes.ToArray(), false));
            if (signal == "metrics" && receiver.holdMetrics)
                receiver.release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            var status = signal == "metrics" && (receiver.holdMetrics || receiver.failMetrics)
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            receiver.CompletedSignals.Enqueue(signal);
            return new(status) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    internal void ReleaseHeldResponse() => release.TrySetResult();

    internal async Task WaitForSignalAsync(string signal)
        => await WaitUntilAsync(() => Captures.Any(c => c.Signal == signal));

    internal async Task WaitForSignalsAsync()
    {
        foreach (var signal in new[] { "traces", "logs", "metrics" })
        {
            await WaitForSignalAsync(signal);
            if (signal != "metrics" || !holdMetrics)
                await WaitUntilAsync(() => CompletedSignals.Contains(signal));
        }
    }

    internal async Task WaitForWorkflowKindsAsync(string[] expected, IEnumerable<string> diagnostics)
    {
        string[] Observed() => Captures.Where(c => c.Signal == "traces")
            .SelectMany(FirstPathTelemetryTests.DecodeRecords).Select(s => Attributes(s, 9))
            .Select(tags => tags.GetValueOrDefault("kind", "")).ToArray();
        try { await WaitUntilAsync(() => expected.All(kind => Observed().Contains(kind))); }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Expected workflow kinds={string.Join(",", expected)}; observed={string.Join(",", Observed())}; losses={string.Join(";", diagnostics)}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    public void Dispose() => release.TrySetResult();
}
