using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class OperationalTelemetryTests
{
    private const string Canary = "hostile-prompt-source-transcript-password-exception-path-url";
    private static readonly string[] DetailKeys = ["operation", "outcome", "detail.id"];
    private static readonly string[] AggregateKeys = ["operation", "outcome"];
    private static readonly string[] MetricNames =
        ["nightweaver.operations", "nightweaver.operation.duration", "nightweaver.telemetry.dropped"];

    [Fact]
    public void DisabledAndDryRunNeverReadExporterConfiguration()
    {
        var reads = 0;
        Assert.Null(OperationalTelemetry.Start(OperationalHost.Runtime, true, Assert.Fail,
            _ => throw new InvalidOperationException("Dry run must not read environment")));
        Assert.Null(OperationalTelemetry.Start(OperationalHost.Web, false, Assert.Fail, key =>
        {
            reads++;
            Assert.Equal("NIGHTWEAVER_TELEMETRY_ENABLED", key);
            return null;
        }));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://example.org/otlp")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://user:password@example.org/otlp")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://example.org/otlp?secret=value")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://example.org/otlp/v1/traces")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://example.org/otlp/v1/")]
    [InlineData("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=invalid")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=Basic%20fake%0Ainjection")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "secret=canary")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY", "disk")]
    public void InvalidConfigurationHasOnlyFixedSafeDiagnostic(string key, string value)
    {
        List<string> diagnostics = [];
        var config = OperationalTelemetryConfiguration.Read(name => name == key ? value
            : name == "NIGHTWEAVER_TELEMETRY_ENABLED" ? "true" : null, false, diagnostics.Add);
        Assert.Null(config);
        Assert.Equal(["Operational telemetry disabled: invalid configuration."], diagnostics);
    }

    [Fact]
    public void ConfigurationAcceptsOneBaseAndOnlyExplicitLoopbackQa()
    {
        var config = OperationalTelemetryConfiguration.Read(name => name switch
        {
            "NIGHTWEAVER_TELEMETRY_ENABLED" => "true",
            "OTEL_EXPORTER_OTLP_HEADERS" => "Authorization=Basic%20isolated-test-value",
            "OTEL_RESOURCE_ATTRIBUTES" => Canary,
            _ => null
        }, false, Assert.Fail);
        Assert.NotNull(config);
        Assert.Equal(OperationalTelemetryConfiguration.DefaultEndpoint + "/v1/logs", config.SignalEndpoint("logs").AbsoluteUri);
        Assert.Equal(0.1, config.SamplingRatio);
        Assert.Null(OperationalTelemetryConfiguration.Read(name => name switch
        {
            "NIGHTWEAVER_TELEMETRY_ENABLED" => "true",
            "OTEL_EXPORTER_OTLP_ENDPOINT" => "http://127.0.0.1:4318/otlp",
            _ => null
        }, false, _ => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothHostsExportDecodableSafeSignalsWithFixedResources(bool web)
    {
        await using var receiver = await Receiver.StartAsync();
        ConcurrentQueue<string> diagnostics = new();
        using var ambient = new Activity(Canary).Start();
        ambient.AddBaggage(Canary, Canary);
        ambient.TraceStateString = "canary=" + Canary;
        using var localLogs = LoggerFactory.Create(_ => { });
        using var scope = localLogs.CreateLogger(Canary).BeginScope(Canary);
        await using var telemetry = new OperationalTelemetry(web ? OperationalHost.Web : OperationalHost.Runtime,
            new(receiver.Endpoint, "Authorization=Basic%20isolated-test-value", 1), diagnostics.Enqueue, metricIntervalMilliseconds: 100);
        // Unsafe detail IDs, enum values and oversized strings fail before reaching an SDK object.
        telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 1, Canary);
        telemetry.RecordOperation((OperationalOperation)999, OperationalOutcome.Success, 1, new string('x', 100_000));
        telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, double.NaN);
        telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 1.25,
            "0123456789abcdef0123456789abcdef");
        var captured = await receiver.WaitForSignalsAsync();
        foreach (var capture in captured)
        {
            Assert.Equal("application/x-protobuf", capture.ContentType);
            Assert.True(capture.ExpectedAuthorization);
            Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(capture.Bytes));
            var resourceGroup = Message.Parse(capture.Bytes).Only(1);
            var resource = Message.Parse(resourceGroup.Bytes).Only(1);
            var attributes = Attributes(Message.Parse(resource.Bytes), 1);
            Assert.Equal(["service.name"], attributes.Keys);
            Assert.Equal(web ? "nightweaver-web" : "nightweaver-runtime", attributes["service.name"]);
            var scopeGroup = Message.Parse(resourceGroup.Bytes).Only(2);
            var scopeMessage = Message.Parse(scopeGroup.Bytes);
            Assert.Equal(OperationalTelemetry.ScopeName, Message.Parse(scopeMessage.Only(1).Bytes).Only(1).String);
            var records = scopeMessage.Fields.Where(field => field.Number == 2).Select(field => Message.Parse(field.Bytes)).ToArray();
            Assert.NotEmpty(records);
            if (capture.Signal == "traces")
            {
                var span = Assert.Single(records);
                Assert.Equal("host.start", span.Only(5).String);
                Assert.DoesNotContain(span.Fields, field => field.Number is 3 or 11 or 13);
                var tags = Attributes(span, 9);
                Assert.Equal("success", tags["outcome"]);
                Assert.Equal("0123456789abcdef0123456789abcdef", tags["detail.id"]);
                Assert.True(capture.Bytes.Length <= TelemetryAdmission.RecordBytes);
            }
            else if (capture.Signal == "logs")
            {
                var log = Assert.Single(records);
                Assert.Equal("Operational operation completed.", Message.Parse(log.Only(5).Bytes).Only(1).String);
                Assert.All(Attributes(log, 6).Keys, key => Assert.Contains(key, DetailKeys));
                Assert.True(capture.Bytes.Length <= TelemetryAdmission.RecordBytes);
            }
            else
            {
                Assert.All(records, metric => Assert.Contains(metric.Only(1).String, MetricNames));
                Assert.DoesNotContain("detail.id", Encoding.UTF8.GetString(capture.Bytes));
                Assert.DoesNotContain("0123456789abcdef0123456789abcdef", Encoding.UTF8.GetString(capture.Bytes));
                var histogram = Assert.Single(records, metric => metric.Only(1).String == "nightweaver.operation.duration");
                var data = Message.Parse(histogram.Only(9).Bytes);
                Assert.Equal(2UL, data.Only(2).Value); // cumulative temporality
                var point = Message.Parse(data.Only(1).Bytes);
                var bounds = point.Fields.Where(field => field.Number == 7).SelectMany(field => field.Bytes.Chunk(8))
                    .Select(bytes => BitConverter.ToDouble(bytes)).ToArray();
                Assert.Equal(OperationalTelemetry.DurationBuckets, bounds);
            }
        }
        Assert.Contains(diagnostics, value => value.Contains("schema", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("tag")]
    [InlineData("baggage")]
    [InlineData("exception")]
    [InlineData("name")]
    [InlineData("tracestate")]
    public async Task DecoratedAndForeignActivitiesAndMetersCannotExportUnapprovedData(string decoration)
    {
        await using var receiver = await Receiver.StartAsync();
        ConcurrentQueue<string> diagnostics = new();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1),
            diagnostics.Enqueue, metricIntervalMilliseconds: 100);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OperationalTelemetry.ScopeName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                switch (decoration)
                {
                    case "event": activity.AddEvent(new ActivityEvent(Canary)); break;
                    case "tag": activity.SetTag(Canary, new string('x', 100_000)); break;
                    case "baggage": activity.AddBaggage(Canary, Canary); break;
                    case "exception": activity.SetStatus(ActivityStatusCode.Error, new InvalidOperationException(Canary).ToString()); break;
                    case "name": activity.DisplayName = Canary; break;
                    default: activity.TraceStateString = "canary=" + Canary; break;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var foreign = new ActivitySource(OperationalTelemetry.ScopeName);
        using (foreign.StartActivity(Canary)) { }
        using var foreignMeter = new Meter(OperationalTelemetry.ScopeName);
        foreignMeter.CreateCounter<long>(Canary).Add(1, new KeyValuePair<string, object?>(Canary, Canary));
        foreignMeter.CreateCounter<long>("nightweaver.operations").Add(1, new KeyValuePair<string, object?>(Canary, Canary));
        telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 1);
        await receiver.WaitForSignalAsync("logs");
        await receiver.WaitForSignalAsync("metrics");
        await telemetry.StopAsync(false);
        Assert.DoesNotContain(receiver.Captures, capture => capture.Signal == "traces");
        Assert.All(receiver.Captures, capture => Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(capture.Bytes)));
        Assert.Contains(diagnostics, value => value.Contains("traces/export", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    public async Task RedirectAndAuthenticationFailuresStayLocalAndBounded(int status)
    {
        await using var receiver = await Receiver.StartAsync(status);
        ConcurrentQueue<string> diagnostics = new();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1),
            diagnostics.Enqueue, metricIntervalMilliseconds: 100);
        telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 0);
        await receiver.WaitForSignalsAsync();
        await telemetry.StopAsync(false);
        Assert.Equal(0, receiver.RedirectHits);
        Assert.Contains(diagnostics, value => value.Contains("logs/export", StringComparison.Ordinal));
        Assert.All(diagnostics, value => Assert.DoesNotContain(Canary, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldExporterCannotBlockAdmissionOrExtendShutdown(bool force)
    {
        await using var receiver = await Receiver.StartAsync(hold: true);
        ConcurrentQueue<string> diagnostics = new();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1),
            diagnostics.Enqueue, metricIntervalMilliseconds: 100);
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 10_000; index++)
            telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 0);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.InRange(telemetry.PendingRecords.Spans, 0, 16);
        Assert.InRange(telemetry.PendingRecords.Events, 0, 16);
        await receiver.WaitForSignalsAsync();
        timer.Restart();
        await telemetry.StopAsync(force);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(force ? 1 : 2.7));
        Assert.Contains(diagnostics, value => value.Contains("budget", StringComparison.Ordinal));
        Assert.True(diagnostics.Count <= 12);
    }

    [Fact]
    public async Task RequestTimeoutAndUnreachableReceiverAreBestEffort()
    {
        await using var receiver = await Receiver.StartAsync(hold: true);
        ConcurrentQueue<string> diagnostics = new();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1),
            diagnostics.Enqueue, metricIntervalMilliseconds: 60_000);
        telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 0);
        await receiver.WaitForSignalAsync("logs");
        await WaitUntilAsync(() => diagnostics.Any(value => value.Contains("logs/export", StringComparison.Ordinal)));
        await telemetry.StopAsync(true);
        await receiver.DisposeAsync();
        await using var unavailable = new OperationalTelemetry(OperationalHost.Web, new(receiver.Endpoint, "", 1),
            diagnostics.Enqueue);
        unavailable.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 0);
        await WaitUntilAsync(() => diagnostics.Count(value => value.Contains("logs/export", StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public void BudgetHasBoundedBurstAndMonotonicRefillAndSeriesInventoryFits()
    {
        var clock = new TestClock();
        var admission = new TelemetryAdmission(clock);
        for (var index = 0; index < 16; index++) Assert.True(admission.TryTake());
        Assert.False(admission.TryTake());
        clock.Seconds = 30;
        for (var index = 0; index < 8; index++) Assert.True(admission.TryTake());
        Assert.False(admission.TryTake());
        clock.Seconds = 1_000_000;
        for (var index = 0; index < 16; index++) Assert.True(admission.TryTake());
        Assert.False(admission.TryTake());
        Assert.Equal(116, OperationalTelemetry.ProjectedSeries);
        Assert.True(OperationalTelemetry.ProjectedSeries <= 2000);
    }

    [Fact]
    public async Task HighCardinalityDetailsNeverBecomeMetricLabelsAndMetricsAreUnsampled()
    {
        await using var receiver = await Receiver.StartAsync();
        long observed = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == OperationalTelemetry.ScopeName && instrument.Name == "nightweaver.operations")
                    observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags) Assert.Contains(tag.Key, AggregateKeys);
            Interlocked.Add(ref observed, value);
        });
        listener.Start();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Web, new(receiver.Endpoint, "", 0),
            _ => { }, metricIntervalMilliseconds: 100);
        for (var index = 0; index < 1000; index++)
            telemetry.RecordOperation(OperationalOperation.HostStart, OperationalOutcome.Success, 1, Guid.NewGuid().ToString("N"));
        await receiver.WaitForSignalAsync("metrics");
        await receiver.WaitForSignalAsync("logs");
        await telemetry.StopAsync(false);
        Assert.Equal(1000, observed);
        Assert.DoesNotContain(receiver.Captures, capture => capture.Signal == "traces");
        foreach (var capture in receiver.Captures.Where(capture => capture.Signal == "metrics"))
        {
            Assert.DoesNotContain("detail.id", Encoding.UTF8.GetString(capture.Bytes));
            Assert.DoesNotContain("service.instance.id", Encoding.UTF8.GetString(capture.Bytes));
            Assert.DoesNotContain("service.version", Encoding.UTF8.GetString(capture.Bytes));
        }
    }

    private sealed class TestClock : TimeProvider
    {
        internal int Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private static Dictionary<string, string> Attributes(Message message, int field) => message.Fields
        .Where(item => item.Number == field).Select(item => Message.Parse(item.Bytes))
        .ToDictionary(item => item.Only(1).String, item => Message.Parse(item.Only(2).Bytes).Only(1).String);

    private sealed record Capture(string Signal, string ContentType, byte[] Bytes, bool ExpectedAuthorization);

    private sealed class Receiver(WebApplication app, Uri endpoint) : IAsyncDisposable
    {
        internal Uri Endpoint { get; } = endpoint;
        internal ConcurrentQueue<Capture> Captures { get; } = new();
        internal int RedirectHits;
        private readonly CancellationTokenSource stop = new();
        private bool disposed;

        internal static async Task<Receiver> StartAsync(int status = 200, bool hold = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            Receiver? receiver = null;
            app.MapPost("/otlp/v1/{signal}", async (HttpContext context, string signal) =>
            {
                using var bytes = new MemoryStream();
                await context.Request.Body.CopyToAsync(bytes);
                receiver!.Captures.Enqueue(new(signal, context.Request.ContentType ?? "", bytes.ToArray(),
                    context.Request.Headers.Authorization == "Basic isolated-test-value"));
                if (hold)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, receiver.stop.Token); }
                    catch (OperationCanceledException) { }
                }
                context.Response.StatusCode = status;
                if (status == 301) context.Response.Headers.Location = new Uri(receiver.Endpoint, "/stolen").AbsoluteUri;
                if (status != 200) await context.Response.WriteAsync(Canary);
            });
            app.MapPost("/stolen", () => { Interlocked.Increment(ref receiver!.RedirectHits); });
            await app.StartAsync();
            receiver = new(app, new Uri(app.Urls.Single() + "/otlp/"));
            return receiver;
        }

        internal async Task<Capture> WaitForSignalAsync(string signal)
        {
            await WaitUntilAsync(() => Captures.Any(capture => capture.Signal == signal));
            return Captures.First(capture => capture.Signal == signal);
        }

        internal async Task<Capture[]> WaitForSignalsAsync() =>
            [await WaitForSignalAsync("traces"), await WaitForSignalAsync("logs"), await WaitForSignalAsync("metrics")];

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            stop.Cancel();
            await app.StopAsync();
            await app.DisposeAsync();
            stop.Dispose();
        }
    }

    // Minimal wire decoder for the public OTLP protobuf schema. No string-scanning substitutes for decoding.
    private sealed record Field(int Number, ulong Value, byte[] Bytes)
    {
        internal string String => Encoding.UTF8.GetString(Bytes);
    }

    private sealed record Message(List<Field> Fields)
    {
        internal Field Only(int number) => Assert.Single(Fields, field => field.Number == number);

        internal static Message Parse(byte[] bytes)
        {
            List<Field> fields = [];
            var offset = 0;
            ulong Varint()
            {
                ulong value = 0;
                for (var shift = 0; shift < 64; shift += 7)
                {
                    var next = bytes[offset++];
                    value |= (ulong)(next & 127) << shift;
                    if (next < 128) return value;
                }
                throw new InvalidDataException("Invalid protobuf varint");
            }
            while (offset < bytes.Length)
            {
                var tag = Varint();
                var wire = tag & 7;
                if (wire == 0) { fields.Add(new((int)(tag >> 3), Varint(), [])); continue; }
                var size = wire switch { 1 => 8, 2 => checked((int)Varint()), 5 => 4, _ => throw new InvalidDataException("Invalid protobuf field") };
                Assert.InRange(size, 0, bytes.Length - offset);
                fields.Add(new((int)(tag >> 3), 0, bytes.AsSpan(offset, size).ToArray()));
                offset += size;
            }
            return new(fields);
        }
    }
}
