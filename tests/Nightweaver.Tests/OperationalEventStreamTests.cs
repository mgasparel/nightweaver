using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class OperationalEventStreamTests
{
    [Fact]
    public async Task PublicationDuringHelloIsQueuedAndHostShutdownEndsTheConnection()
    {
        using var lifetime = new Lifetime();
        var source = new Source();
        using var observer = new OperationalObservationService("web-test", source, TimeProvider.System, lifetime);
        using var stream = new OperationalEventStream(observer, TimeProvider.System, lifetime);
        var context = Context();
        using var body = new InterleavingStream(async frame =>
        {
            if (frame.StartsWith("event: hello", StringComparison.Ordinal))
            {
                source.Content = "changed during hello";
                await observer.CollectOnceAsync(false, TestContext.Current.CancellationToken);
            }
            if (frame.StartsWith("event: invalidation", StringComparison.Ordinal)) lifetime.StopApplication();
        });
        context.Response.Body.Dispose(); context.Response.Body = body;
        await stream.RunAsync(context, "web-test").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var frames = System.Text.Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, frames.Length);
        Assert.StartsWith("event: hello\ndata: ", frames[0], StringComparison.Ordinal);
        Assert.StartsWith("event: resync\ndata: ", frames[1], StringComparison.Ordinal);
        Assert.StartsWith("event: invalidation\ndata: ", frames[2], StringComparison.Ordinal);
        using var hello = JsonDocument.Parse(frames[0].Split("data: ")[1]);
        using var changed = JsonDocument.Parse(frames[2].Split("data: ")[1]);
        Assert.NotEqual(hello.RootElement.GetProperty("versions").GetProperty("attention").GetString(),
            changed.RootElement.GetProperty("versions").GetProperty("attention").GetString());
    }

    [Fact]
    public async Task RealFramesHandshakeInvalidationAndCancellationShareTheObservationOwner()
    {
        var source = new Source();
        using var factory = new NightweaverWebApplicationFactory(configureServices: services =>
            services.AddSingleton<IOperationalObservationSource>(source));
        using var client = factory.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/operations/v1/projects/web-test/events?key=attention");
        request.Headers.Add("Last-Event-ID", "ignored-old-log");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        var hello = await Frame("hello");
        Assert.Equal("nightweaver-operations-v1", hello.GetProperty("schema").GetString());
        Assert.Equal("web-test", hello.GetProperty("projectId").GetString());
        Assert.Single(hello.GetProperty("versions").EnumerateObject());
        Assert.True(ulong.Parse(hello.GetProperty("versions").GetProperty("attention").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("reconnect", (await Frame("resync")).GetProperty("reason").GetString());
        var observer = factory.Services.GetRequiredService<OperationalObservationService>();
        source.Content = "external change";
        await observer.CollectOnceAsync(false, cancellation.Token);
        var changed = await Frame("invalidation");
        Assert.Equal(observer.Epoch, changed.GetProperty("epoch").GetString());
        Assert.True(ulong.Parse(changed.GetProperty("versions").GetProperty("attention").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            > ulong.Parse(hello.GetProperty("versions").GetProperty("attention").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        cancellation.Cancel();

        async Task<JsonElement> Frame(string name)
        {
            Assert.Equal("event: " + name, await reader.ReadLineAsync(cancellation.Token));
            var data = await reader.ReadLineAsync(cancellation.Token);
            Assert.StartsWith("data: ", data, StringComparison.Ordinal);
            Assert.Equal("", await reader.ReadLineAsync(cancellation.Token));
            using var json = JsonDocument.Parse(data![6..]);
            return json.RootElement.Clone();
        }
    }

    [Theory]
    [InlineData("foreign/events?key=attention", 404)]
    [InlineData("web-test/events", 400)]
    [InlineData("web-test/events?key=attention&extra=1", 400)]
    [InlineData("web-test/events?key=attention&key=attention", 400)]
    [InlineData("web-test/events?key=workspace:../foreign", 400)]
    public async Task RejectsUnknownProjectsAndMalformedSubscriptions(string suffix, int status)
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/operations/v1/projects/" + suffix, TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task SubscriberLimitAndCancellationReleaseCapacityWithoutStoppingSharedSources()
    {
        using var factory = new NightweaverWebApplicationFactory(configureServices: services =>
            services.AddSingleton<IOperationalObservationSource>(new Source()));
        using var client = factory.CreateClient(); // Initializes the real service owner.
        var stream = factory.Services.GetRequiredService<OperationalEventStream>();
        var contexts = Enumerable.Range(0, 16).Select(_ => Context()).ToArray();
        var cancellations = contexts.Select(_ => new CancellationTokenSource()).ToArray();
        var tasks = contexts.Select((context, index) =>
        {
            context.RequestAborted = cancellations[index].Token;
            return stream.RunAsync(context, "web-test");
        }).ToArray();
        try
        {
            var rejected = Context();
            await stream.RunAsync(rejected, "web-test");
            Assert.Equal(429, rejected.Response.StatusCode);
            Assert.Equal("2", rejected.Response.Headers.RetryAfter);
            cancellations[0].Cancel(); await tasks[0];
            var replacement = Context(); using var cancelled = new CancellationTokenSource();
            replacement.RequestAborted = cancelled.Token;
            var resumed = stream.RunAsync(replacement, "web-test");
            cancelled.Cancel(); await resumed;
            Assert.Equal(200, replacement.Response.StatusCode);
            Assert.NotNull(factory.Services.GetRequiredService<OperationalObservationService>().Read("attention"));
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Cancel();
            await Task.WhenAll(tasks);
            foreach (var cancellation in cancellations) cancellation.Dispose();
            foreach (var context in contexts) context.Response.Body.Dispose();
        }
    }

    [Fact]
    public void QueueCoalescesAndOverflowForcesResyncWithoutKeepingReplay()
    {
        using var queue = new OperationalEventStream.Invalidations();
        for (var i = 1; i <= 10000; i++) queue.Add("attention", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var batch = queue.Take();
        Assert.False(batch.Overflow); Assert.Single(batch.Versions); Assert.Equal("10000", batch.Versions["attention"]);
        for (var i = 0; i < 65; i++) queue.Add("key" + i, "1");
        batch = queue.Take(); Assert.True(batch.Overflow); Assert.Empty(batch.Versions);
        queue.Add("attention", "10001"); Assert.Single(queue.Take().Versions);
        queue.Add(new string('a', 65536), "1"); Assert.True(queue.Take().Overflow);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?key=attention");
        context.Response.Body = new MemoryStream();
        return context;
    }
    private sealed class Source : IOperationalObservationSource
    {
        internal string Content { get; set; } = "initial";
        public Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
            => Read(keys);
        public Task<IReadOnlyDictionary<string, ObservationSample>> ReadRuntimeAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
            => Read(keys);
        private Task<IReadOnlyDictionary<string, ObservationSample>> Read(IReadOnlyList<OperationKey> keys)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyDictionary<string, ObservationSample>>(keys.ToDictionary(key => key.Value,
                _ => new ObservationSample(new[] { Content }, now, now,
                    new Dictionary<string, ObservationSourceState> { ["inventory"] = new("fresh", now, now, null) })));
        }
    }

    private sealed class InterleavingStream(Func<string, Task> onWrite) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            await onWrite(System.Text.Encoding.UTF8.GetString(buffer.Span));
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
}
