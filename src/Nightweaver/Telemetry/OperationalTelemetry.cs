using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Nightweaver.Telemetry;

internal enum OperationalHost { Runtime, Web }
internal enum OperationalOperation { HostStart, HostStop }
internal enum OperationalOutcome { Success, Failure, Cancelled }

/// <summary>
/// Observation only. Callers submit a closed schema, never ILogger state, exceptions or arbitrary tags.
/// SDK serialization and HTTP work run outside the workflow on bounded, process-owned workers.
/// </summary>
internal sealed class OperationalTelemetry : IAsyncDisposable
{
    internal const string ScopeName = "Nightweaver.Operational";
    internal static readonly double[] DurationBuckets = [0.1, 1, 5, 30, 120, 600, 1800, 7200];
    // Two hosts: 6 operation counters + 2 * (9 buckets + sum + count) +
    // 3 signals * 4 drop reasons + 3 export failure counters + one target_info each.
    // Includes one SDK overflow series per stream (histogram overflow costs 11 series).
    internal const int ProjectedSeries = 2 * (6 + 2 * 11 + 12 + 3 + 1 + 1 + 11 + 1 + 1);
    private readonly ActivitySource source = new(ScopeName);
    private readonly Meter meter = new(ScopeName);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Record> spans = Queue(1024);
    private readonly Channel<Record> events = Queue(512);
    private readonly TelemetryAdmission spanAdmission;
    private readonly TelemetryAdmission eventAdmission;
    private readonly Action<string> diagnostic;
    private readonly Channel<string> diagnosticMessages = Channel.CreateBounded<string>(12);
    private readonly List<HttpClient> httpClients = [];
    private readonly int[] reported = new int[12];
    private readonly Counter<long> operations;
    private readonly Histogram<double> duration;
    private readonly Counter<long> drops;
    private readonly Counter<long> failures;
    private readonly TracerProvider traces;
    private readonly MeterProvider metrics;
    private readonly ILoggerFactory logFactory;
    private readonly ILogger logger;
    private readonly Task spanWorker;
    private readonly Task eventWorker;
    private readonly Task diagnosticWorker;
    private int closing;

    private readonly record struct Record(string Operation, string Outcome, double Seconds, string? DetailId);

    internal static OperationalTelemetry? Start(OperationalHost host, bool dryRun, Action<string> diagnostic,
        Func<string, string?>? readEnvironment = null)
    {
        var configuration = OperationalTelemetryConfiguration.Read(readEnvironment ?? Environment.GetEnvironmentVariable,
            dryRun, diagnostic);
        if (configuration is null) return null;
        try { return new(host, configuration, diagnostic); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            diagnostic("Operational telemetry disabled: initialization failed.");
            return null;
        }
    }

    internal OperationalTelemetry(OperationalHost host, OperationalTelemetryConfiguration configuration,
        Action<string> diagnostic, TimeProvider? clock = null, int metricIntervalMilliseconds = 60_000)
    {
        this.diagnostic = diagnostic;
        spanAdmission = new(clock ?? TimeProvider.System);
        eventAdmission = new(clock ?? TimeProvider.System);
        var service = host switch
        {
            OperationalHost.Runtime => "nightweaver-runtime",
            OperationalHost.Web => "nightweaver-web",
            _ => throw new ArgumentOutOfRangeException(nameof(host))
        };
        var resource = ResourceBuilder.CreateEmpty().AddAttributes(
            [new KeyValuePair<string, object>("service.name", service)]);
        operations = meter.CreateCounter<long>("nightweaver.operations");
        duration = meter.CreateHistogram<double>("nightweaver.operation.duration", "s");
        drops = meter.CreateCounter<long>("nightweaver.telemetry.dropped");
        failures = meter.CreateCounter<long>("nightweaver.telemetry.export.failed");
        try
        {
            traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(ScopeName)
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(configuration.SamplingRatio)))
                .AddProcessor(new SimpleActivityExportProcessor(new SafeTraceExporter(
                    Options(configuration, "traces"), source, Failed)))
                .Build();
            metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(ScopeName)
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
                .AddView(instrument =>
                {
                    if (!ReferenceEquals(instrument.Meter, meter)) return MetricStreamConfiguration.Drop;
                    return instrument.Name switch
                    {
                        "nightweaver.operations" => new MetricStreamConfiguration
                            { TagKeys = ["operation", "outcome"], CardinalityLimit = 7 },
                        "nightweaver.operation.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["operation"], Boundaries = DurationBuckets, CardinalityLimit = 3 },
                        "nightweaver.telemetry.dropped" => new MetricStreamConfiguration
                            { TagKeys = ["signal", "reason"], CardinalityLimit = 13 },
                        "nightweaver.telemetry.export.failed" => new MetricStreamConfiguration
                            { TagKeys = ["signal"], CardinalityLimit = 4 },
                        _ => MetricStreamConfiguration.Drop
                    };
                })
                .AddReader(new PeriodicExportingMetricReader(new OtlpMetricExporter(Options(configuration, "metrics")),
                    metricIntervalMilliseconds, 3000) { TemporalityPreference = MetricReaderTemporalityPreference.Cumulative })
                .Build();
            logFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resource);
                options.IncludeScopes = false;
                options.IncludeFormattedMessage = false;
                options.AddProcessor(new SimpleLogRecordExportProcessor(new OtlpLogExporter(
                    Options(configuration, "logs"))));
            }));
            logger = logFactory.CreateLogger(ScopeName);
            // Do not inherit prompts, scopes, baggage, Activity.Current or execution context from the host.
            using (ExecutionContext.SuppressFlow())
            {
                spanWorker = Task.Run(() => DrainAsync(spans.Reader, EmitSpan));
                eventWorker = Task.Run(() => DrainAsync(events.Reader, EmitEvent));
                diagnosticWorker = Task.Run(DrainDiagnosticsAsync);
            }
        }
        catch
        {
            // A malformed SDK environment must not retain a partial provider or transport.
            stop.Cancel();
            metrics?.Dispose();
            traces?.Dispose();
            logFactory?.Dispose();
            foreach (var client in httpClients) client.Dispose();
            source.Dispose();
            meter.Dispose();
            stop.Dispose();
            throw;
        }
    }

    private static Channel<Record> Queue(int capacity) => Channel.CreateBounded<Record>(
        new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    internal void RecordOperation(OperationalOperation operation, OperationalOutcome outcome, double seconds,
        string? detailId = null)
    {
        if (Volatile.Read(ref closing) != 0) return;
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(outcome) || !double.IsFinite(seconds)
            || seconds is < 0 or > 2_592_000
            || (detailId is not null && !IsOpaqueId(detailId)))
        {
            Dropped("traces", "schema");
            Dropped("logs", "schema");
            return;
        }
        var record = new Record(operation == OperationalOperation.HostStart ? "host.start" : "host.stop",
            outcome switch { OperationalOutcome.Success => "success", OperationalOutcome.Failure => "failure", _ => "cancelled" },
            seconds, detailId);
        operations.Add(1, new("operation", record.Operation), new("outcome", record.Outcome));
        duration.Record(seconds, new KeyValuePair<string, object?>("operation", record.Operation));
        Admit(spans.Writer, spanAdmission, record, "traces");
        Admit(events.Writer, eventAdmission, record, "logs");
    }

    internal static bool IsOpaqueId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);

    internal (int Spans, int Events) PendingRecords => (spans.Reader.Count, events.Reader.Count);

    private void Admit(ChannelWriter<Record> writer, TelemetryAdmission admission, Record record, string signal)
    {
        // Reserve a conservative full 2 KiB per accepted record, including protobuf/resource overhead.
        if (!admission.TryTake()) Dropped(signal, "budget");
        else if (!writer.TryWrite(record)) Dropped(signal, "queue");
    }

    private async Task DrainAsync(ChannelReader<Record> reader, Action<Record> emit)
    {
        try
        {
            await foreach (var record in reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (stop.IsCancellationRequested) break;
                emit(record);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void EmitSpan(Record record)
    {
        using var activity = source.StartActivity(record.Operation, ActivityKind.Internal,
            default(ActivityContext), tags: Tags(record));
        activity?.SetStartTime(DateTime.UtcNow.AddSeconds(-record.Seconds));
    }

    private void EmitEvent(Record record)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.Log(LogLevel.Information, new EventId(1001, "OperationalCompletion"), Tags(record), null,
            static (_, _) => "Operational operation completed.");
    }

    private static List<KeyValuePair<string, object?>> Tags(Record record)
    {
        List<KeyValuePair<string, object?>> tags = [new("operation", record.Operation), new("outcome", record.Outcome)];
        if (record.DetailId is not null) tags.Add(new("detail.id", record.DetailId));
        return tags;
    }

    private void Failed(string signal)
    {
        failures.Add(1, new KeyValuePair<string, object?>("signal", signal));
        ReportOnce(signal, "export");
    }

    private void Dropped(string signal, string reason)
    {
        drops.Add(1, new("signal", signal), new("reason", reason));
        ReportOnce(signal, reason);
    }

    private void ReportOnce(string signal, string reason)
    {
        var index = (signal == "traces" ? 0 : signal == "logs" ? 4 : 8)
            + (reason == "budget" ? 0 : reason == "queue" ? 1 : reason == "schema" ? 2 : 3);
        if (Interlocked.Exchange(ref reported[index], 1) == 0)
            diagnosticMessages.Writer.TryWrite($"Operational telemetry loss: {signal}/{reason} (further losses counted in metrics).");
    }

    private async Task DrainDiagnosticsAsync()
    {
        try
        {
            await foreach (var message in diagnosticMessages.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try { diagnostic(message); }
                catch (Exception) { /* A local diagnostic sink cannot fail Delivery. */ }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private OtlpExporterOptions Options(OperationalTelemetryConfiguration configuration, string signal)
    {
        var client = new HttpClient(new SafeHttpHandler(signal == "metrics" ? 64 * 1024 : TelemetryAdmission.RecordBytes,
            signal == "metrics" ? null : new TelemetryAdmission(TimeProvider.System),
            () => Failed(signal), stop.Token)) { Timeout = TimeSpan.FromSeconds(3) };
        httpClients.Add(client);
        return new()
        {
            Endpoint = configuration.SignalEndpoint(signal), Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers = configuration.Headers, TimeoutMilliseconds = 3000,
            // The SDK minimum buffer is 750,000 bytes. The transport enforces our smaller wire caps.
            MaxRequestSizeBytes = 750_000,
            HttpClientFactory = () => client
        };
    }

    internal async ValueTask StopAsync(bool force)
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        spans.Writer.TryComplete();
        events.Writer.TryComplete();
        if (force) stop.Cancel();
        else stop.CancelAfter(TimeSpan.FromSeconds(2));
        var disposal = Task.Run(async () =>
        {
            await Task.WhenAll(spanWorker, eventWorker).ConfigureAwait(false);
            metrics.Dispose();
            traces.Dispose();
            logFactory.Dispose();
            source.Dispose();
            meter.Dispose();
            foreach (var client in httpClients) client.Dispose();
            diagnosticMessages.Writer.TryComplete();
            await diagnosticWorker.ConfigureAwait(false);
            stop.Dispose();
        });
        if (force) return;
        try { await disposal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { } // Concurrent completed disposal already settled HTTP work.
        }
    }

    public ValueTask DisposeAsync() => StopAsync(force: false);

    private sealed class SafeHttpHandler(int maxBytes, TelemetryAdmission? admission, Action failed,
        CancellationToken stop) : DelegatingHandler(new SocketsHttpHandler
        { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
            deadline.Token.ThrowIfCancellationRequested();
            // Do not propagate ambient HTTP trace state into the remote tenant.
            using var suppression = SuppressInstrumentationScope.Begin();
            try
            {
                if (request.Content?.Headers.ContentLength is not long bytes || bytes > maxBytes
                    || (admission is not null && !admission.TryTake()))
                {
                    failed();
                    return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new ByteArrayContent([]) };
                }
                var response = base.Send(request, deadline.Token);
                // Never buffer a remote error body. Nonempty success may be partial rejection;
                // conservatively expose loss rather than treating it as business acknowledgement.
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength != 0) failed();
                response.Content.Dispose();
                response.Content = new ByteArrayContent([]);
                return response;
            }
            catch (Exception) { failed(); throw; }
        }
    }

    private sealed class SafeTraceExporter(OtlpExporterOptions options, ActivitySource ownedSource,
        Action<string> failed) : OtlpTraceExporter(options)
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {

            foreach (var activity in batch)
            {
                // Other listeners may decorate owned Activities. Reject rather than export their fields.
                if (!ReferenceEquals(activity.Source, ownedSource) || activity.Events.Any() || activity.Links.Any()
                    || activity.Baggage.Any() || activity.TraceStateString is not null
                    || activity.StatusDescription is not null
                    || activity.DisplayName is not ("host.start" or "host.stop")
                    || activity.TagObjects.Count() > 3
                    || activity.TagObjects.Any(tag => tag.Key switch
                    {
                        "operation" => tag.Value is not ("host.start" or "host.stop"),
                        "outcome" => tag.Value is not ("success" or "failure" or "cancelled"),
                        "detail.id" => tag.Value is not string id || !IsOpaqueId(id),
                        _ => true
                    }))
                {
                    if (ReferenceEquals(activity.Source, ownedSource)) failed("traces");
                    return ExportResult.Failure;
                }
            }
            try
            {
                var result = base.Export(batch);
                if (result != ExportResult.Success) failed("traces");
                return result;
            }
            catch (Exception) { failed("traces"); return ExportResult.Failure; }
        }

    }
}
