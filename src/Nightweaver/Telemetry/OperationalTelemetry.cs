using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Nightweaver.Configuration;
using Nightweaver.Execution;
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
    internal const int ProjectedSeries = 1350 + 2 * (19 + 4 * 11 + 9 + 4 + 4 + 3);
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
    private readonly Func<string, HttpMessageHandler>? createExportTransport;
    private readonly int[] reported = new int[12];
    private readonly Counter<long> operations;
    private readonly Histogram<double> duration;
    private readonly Counter<long> invocationAttempts;
    private readonly Histogram<double> invocationDuration;
    private readonly Counter<long> verifiedMerges;
    private readonly Counter<long> mergeCleanup;
    private readonly Counter<double> phaseSeconds;
    private readonly Histogram<double> phaseDuration;
    private readonly Counter<double> occupiedSeconds;
    private readonly Histogram<double> classificationDuration;
    private readonly Counter<long> workflowResults;
    private readonly Counter<long> operationalRetries;
    private readonly Counter<long> publishedOutcomes;
    private readonly Counter<long> semanticRepairs;
    private readonly Counter<long> approvals;
    private readonly Histogram<double> humanWait;
    private readonly Counter<long> lifecycleLoss;
    internal LifecycleObservation Lifecycle { get; }
    private readonly Dictionary<string, PoolObservation> pools = new(StringComparer.Ordinal);
    private readonly ObservationBudget observationBudget = new();
    private readonly Histogram<double> eligibleWait;
    private readonly Counter<long> waitCensored;
    private readonly Counter<long> drops;
    private readonly Counter<long> failures;
    private readonly Counter<long> observationAttempts;
    private readonly Histogram<double> observationDuration;
    private readonly object observationSync = new();
    private readonly Dictionary<string, Measurement<double>[]> observationHealth = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Measurement<double>[]> observationAge = new(StringComparer.Ordinal);
    private readonly TracerProvider traces;
    private readonly MeterProvider metrics;
    private readonly ILoggerFactory logFactory;
    private readonly ILogger logger;
    private readonly Task spanWorker;
    private readonly Task eventWorker;
    private readonly Task diagnosticWorker;
    private int closing;

    private readonly record struct Record(string Operation, string Outcome, double Seconds, string? DetailId,
        string? Role = null, string? BeadId = null, string? RuntimeId = null, string? InvocationId = null,
        string? CheckedHead = null, string? MergeCommit = null, DateTime? EndUtc = null, string? Kind = null, string? Source = null);

    private static readonly AsyncLocal<RuntimeScope?> Runtime = new();
    internal static RuntimeScope? Current => Runtime.Value;

    // The scope follows owned async work; it never propagates into Agent stdin or environment.
    internal RuntimeScope EnterRuntime(string runtimeId) => new(this, runtimeId);

    internal sealed class RuntimeScope : IDisposable
    {
        private readonly RuntimeScope? previous;
        internal OperationalTelemetry Telemetry { get; }
        internal string RuntimeId { get; }
        internal RuntimeScope(OperationalTelemetry telemetry, string runtimeId)
        {
            if (!IsOpaqueId(runtimeId)) throw new ArgumentException("Invalid runtime identity.", nameof(runtimeId));
            Telemetry = telemetry;
            RuntimeId = runtimeId;
            previous = Runtime.Value;
            Runtime.Value = this;
        }
        public void Dispose() => Runtime.Value = previous;
    }

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
        Action<string> diagnostic, TimeProvider? clock = null, int metricIntervalMilliseconds = 60_000,
        Func<string, HttpMessageHandler>? createExportTransport = null)
    {
        this.diagnostic = diagnostic;
        this.createExportTransport = createExportTransport;
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
        invocationAttempts = meter.CreateCounter<long>("nightweaver.invocation.attempts");
        invocationDuration = meter.CreateHistogram<double>("nightweaver.invocation.duration", "s");
        verifiedMerges = meter.CreateCounter<long>("nightweaver.verified_merge");
        mergeCleanup = meter.CreateCounter<long>("nightweaver.merge.cleanup");
        phaseSeconds = meter.CreateCounter<double>("nightweaver.phase.seconds", "s");
        phaseDuration = meter.CreateHistogram<double>("nightweaver.phase.duration", "s");
        occupiedSeconds = meter.CreateCounter<double>("nightweaver.slot.occupied", "s");
        classificationDuration = meter.CreateHistogram<double>("nightweaver.classification.duration", "s");
        workflowResults = meter.CreateCounter<long>("nightweaver.workflow.results");
        operationalRetries = meter.CreateCounter<long>("nightweaver.operational.retries");
        publishedOutcomes = meter.CreateCounter<long>("nightweaver.published.outcomes");
        semanticRepairs = meter.CreateCounter<long>("nightweaver.semantic.repairs");
        approvals = meter.CreateCounter<long>("nightweaver.approvals");
        humanWait = meter.CreateHistogram<double>("nightweaver.human_wait.duration", "s");
        lifecycleLoss = meter.CreateCounter<long>("nightweaver.lifecycle.censored");
        Lifecycle = new(clock ?? TimeProvider.System,
            (pause, seconds) => humanWait.Record(seconds, new KeyValuePair<string, object?>("pause", pause.ToString())),
            reason => lifecycleLoss.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString())),
            (id, outcome) => RecordMilestone(id, null, null, outcome, "observed_snapshot"), budget: observationBudget);
        foreach (var name in new[] { "paused", "oldest", "fresh", "coverage", "age" })
        {
            var key = name;
            meter.CreateObservableGauge("nightweaver.lifecycle." + name, () => Lifecycle.Read(key),
                name is "oldest" or "age" ? "s" : null);
        }
        eligibleWait = meter.CreateHistogram<double>("nightweaver.eligible_wait.duration", "s");
        waitCensored = meter.CreateCounter<long>("nightweaver.eligible_wait.censored");
        foreach (var name in new[] { "capacity", "occupied", "fresh", "coverage", "age", "backpressure", "queued", "oldest" })
        {
            var key = name;
            meter.CreateObservableGauge("nightweaver.pool." + name, () => ReadPools(key),
                name is "age" or "oldest" ? "s" : null);
        }
        drops = meter.CreateCounter<long>("nightweaver.telemetry.dropped");
        failures = meter.CreateCounter<long>("nightweaver.telemetry.export.failed");
        observationAttempts = meter.CreateCounter<long>("nightweaver.observation.attempts");
        observationDuration = meter.CreateHistogram<double>("nightweaver.observation.duration", "s");
        meter.CreateObservableGauge("nightweaver.observation.health", () => ObservationMeasurements(observationHealth));
        meter.CreateObservableGauge("nightweaver.observation.age", () => ObservationMeasurements(observationAge), "s");
        try
        {
            traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(ScopeName)
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(configuration.SamplingRatio)))
                .AddProcessor(new SimpleActivityExportProcessor(new SafeTraceExporter(
                    Options(configuration, "traces"), source, configuration.SamplingRatio, Failed)))
                .Build();
            metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(ScopeName)
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
                .AddView(instrument =>
                {
                    if (!ReferenceEquals(instrument.Meter, meter)) return MetricStreamConfiguration.Drop;
                    return instrument.Name switch
                    {
                        "nightweaver.approvals" => new MetricStreamConfiguration
                            { TagKeys = ["role", "coverage"], CardinalityLimit = 19 },
                        "nightweaver.human_wait.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["pause"], Boundaries = DurationBuckets, CardinalityLimit = 4 },
                        "nightweaver.lifecycle.censored" => new MetricStreamConfiguration
                            { TagKeys = ["reason"], CardinalityLimit = 9 },
                        "nightweaver.lifecycle.paused" or "nightweaver.lifecycle.oldest" => new MetricStreamConfiguration
                            { TagKeys = ["pause"], CardinalityLimit = 4 },
                        "nightweaver.lifecycle.fresh" or "nightweaver.lifecycle.coverage" or "nightweaver.lifecycle.age" => new MetricStreamConfiguration
                            { TagKeys = [], CardinalityLimit = 1 },
                        "nightweaver.eligible_wait.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["pool"], Boundaries = DurationBuckets, CardinalityLimit = 4 },
                        "nightweaver.eligible_wait.censored" => new MetricStreamConfiguration
                            { TagKeys = ["pool", "reason"], CardinalityLimit = 22 },
                        "nightweaver.pool.occupied" => new MetricStreamConfiguration
                            { TagKeys = ["pool", "phase"], CardinalityLimit = 22 },
                        "nightweaver.pool.capacity" or "nightweaver.pool.fresh" or "nightweaver.pool.coverage"
                            or "nightweaver.pool.age" or "nightweaver.pool.backpressure" or "nightweaver.pool.queued"
                            or "nightweaver.pool.oldest" => new MetricStreamConfiguration
                            { TagKeys = ["pool"], CardinalityLimit = 4 },
                        "nightweaver.operations" => new MetricStreamConfiguration
                            { TagKeys = ["operation", "outcome"], CardinalityLimit = 7 },
                        "nightweaver.operation.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["operation"], Boundaries = DurationBuckets, CardinalityLimit = 3 },
                        "nightweaver.invocation.attempts" => new MetricStreamConfiguration
                            { TagKeys = ["role", "status"], CardinalityLimit = 43 },
                        "nightweaver.invocation.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["role"], Boundaries = DurationBuckets, CardinalityLimit = 8 },
                        "nightweaver.verified_merge" => new MetricStreamConfiguration
                            { TagKeys = [], CardinalityLimit = 1 },
                        "nightweaver.merge.cleanup" => new MetricStreamConfiguration
                            { TagKeys = ["outcome"], CardinalityLimit = 5 },
                        "nightweaver.phase.seconds" => new MetricStreamConfiguration
                            { TagKeys = ["pool", "phase"], CardinalityLimit = 22 },
                        "nightweaver.phase.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["phase"], Boundaries = DurationBuckets, CardinalityLimit = 8 },
                        "nightweaver.slot.occupied" => new MetricStreamConfiguration
                            { TagKeys = ["pool"], CardinalityLimit = 4 },
                        "nightweaver.classification.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["kind"], Boundaries = DurationBuckets, CardinalityLimit = 4 },
                        "nightweaver.workflow.results" => new MetricStreamConfiguration
                            { TagKeys = ["kind", "outcome"], CardinalityLimit = 55 },
                        "nightweaver.published.outcomes" => new MetricStreamConfiguration
                            { TagKeys = ["role", "outcome"], CardinalityLimit = 37 },
                        "nightweaver.semantic.repairs" => new MetricStreamConfiguration
                            { TagKeys = ["role"], CardinalityLimit = 7 },
                        "nightweaver.operational.retries" => new MetricStreamConfiguration
                            { TagKeys = ["reason"], CardinalityLimit = 12 },
                        "nightweaver.telemetry.dropped" => new MetricStreamConfiguration
                            { TagKeys = ["signal", "reason"], CardinalityLimit = 13 },
                        "nightweaver.telemetry.export.failed" => new MetricStreamConfiguration
                            { TagKeys = ["signal"], CardinalityLimit = 4 },
                        "nightweaver.observation.attempts" => new MetricStreamConfiguration
                            { TagKeys = ["source", "outcome"], CardinalityLimit = 13 },
                        "nightweaver.observation.duration" => new ExplicitBucketHistogramConfiguration
                            { TagKeys = ["source"], Boundaries = DurationBuckets, CardinalityLimit = 4 },
                        "nightweaver.observation.health" or "nightweaver.observation.age" => new MetricStreamConfiguration
                            { TagKeys = ["source", "health"], CardinalityLimit = 19 },
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

    internal static bool IsOperationalId(string value) => value.Length is > 0 and <= 96
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
    private static bool IsRevision(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    private static bool IsRole(string? value) => value is "planner" or "plan-challenger" or "implementer"
        or "reviewer" or "qa" or "merger" or "classifier";

    internal PoolObservation ObservePool(string pool, int capacity, Func<bool> stopping)
    {
        if (pool is not ("planning" or "delivery" or "merger")) throw new ArgumentException("Invalid pool.", nameof(pool));
        lock (pools)
        {
            if (pools.TryGetValue(pool, out var existing)) return existing;
            var observation = new PoolObservation(pool, capacity, TimeProvider.System,
                seconds => eligibleWait.Record(seconds, new KeyValuePair<string, object?>("pool", pool)),
                (reason, count) => waitCensored.Add(count, new("pool", pool), new("reason", reason.ToString())), stopping,
                budget: observationBudget);
            pools.Add(pool, observation);
            return observation;
        }
    }

    private Measurement<double>[] ReadPools(string instrument)
    {
        PoolObservation[] snapshot;
        lock (pools) snapshot = pools.Values.ToArray();
        return snapshot.SelectMany(pool => pool.Read(instrument)).ToArray();
    }

    private static bool IsObservationSource(string value) => value is "tracker" or "runtime" or "cleanup";
    private static readonly string[] ObservationStates = ["healthy", "stale", "unavailable", "idle", "stopped", "pending"];

    private Measurement<double>[] ObservationMeasurements(Dictionary<string, Measurement<double>[]> values)
    {
        lock (observationSync) return values.Values.SelectMany(value => value).ToArray();
    }

    // Counts describe cached resources, never subscribers. Unknown age is absent, never zero.
    internal void SetObservationHealth(string family, IReadOnlyList<(string Health, double? Age)> resources)
    {
        if (Volatile.Read(ref closing) != 0 || !IsObservationSource(family) || resources.Count > 64
            || resources.Any(item => !ObservationStates.Contains(item.Health)
                || item.Age is { } age && (!double.IsFinite(age) || age < 0))) return;
        lock (observationSync)
        {
            observationHealth[family] = ObservationStates.Select(health => new Measurement<double>(
                resources.Count(item => item.Health == health), new("source", family), new("health", health))).ToArray();
            observationAge[family] = resources.Where(item => item.Age is not null).GroupBy(item => item.Health)
                .Select(group => new Measurement<double>(group.Max(item => item.Age!.Value),
                    new("source", family), new("health", group.Key))).ToArray();
        }
    }

    internal void RecordObservation(string family, string outcome, double seconds)
    {
        if (Volatile.Read(ref closing) != 0 || !IsObservationSource(family)
            || outcome is not ("success" or "failure" or "cancelled" or "timed_out")
            || !double.IsFinite(seconds) || seconds is < 0 or > 2_592_000) return;
        observationAttempts.Add(1, new("source", family), new("outcome", outcome));
        observationDuration.Record(seconds, new KeyValuePair<string, object?>("source", family));
        Submit(new("observation", outcome, seconds, null, EndUtc: DateTime.UtcNow, Source: family));
    }

    internal void RecordInvocation(string role, string status, double seconds, string beadId,
        string runtimeId, string invocationId)
    {
        // Outcome-classifier is diagnostic work, never a lifecycle success.
        if (role == "outcome-classifier") role = "classifier";
        if (!IsRole(role) || status is not ("completed" or "failed" or "cancelled" or "timed_out" or "launch_failed" or "unknown")
            || !double.IsFinite(seconds) || seconds is < 0 or > 2_592_000
            || !IsOperationalId(beadId) || !IsOpaqueId(runtimeId) || !IsOperationalId(invocationId))
        { Dropped("traces", "schema"); Dropped("logs", "schema"); return; }
        if (Volatile.Read(ref closing) != 0) return;
        invocationAttempts.Add(1, new("role", role), new("status", status));
        invocationDuration.Record(seconds, new KeyValuePair<string, object?>("role", role));
        Lifecycle.Attempt(beadId, role, invocationId);
        Submit(new("invocation", status, seconds, null, role, beadId, runtimeId, invocationId, EndUtc: DateTime.UtcNow));
    }

    internal void RecordVerifiedMerge(string beadId, string runtimeId, string invocationId, string checkedHead, string mergeCommit)
    {
        if (!IsOperationalId(beadId) || !IsOpaqueId(runtimeId) || !IsOperationalId(invocationId)
            || !IsRevision(checkedHead) || !IsRevision(mergeCommit))
        { Dropped("traces", "schema"); Dropped("logs", "schema"); return; }
        if (Volatile.Read(ref closing) != 0) return;
        verifiedMerges.Add(1);
        Submit(new("verified_merge", "success", 0, null, "merger", beadId, runtimeId, invocationId, checkedHead, mergeCommit, DateTime.UtcNow));
    }

    internal void RecordMergeCleanup(string outcome)
    {
        if (Volatile.Read(ref closing) == 0 && outcome is "completed" or "queued" or "failed" or "cancelled")
            mergeCleanup.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    internal void RecordPhase(string pool, WorkflowPhase phase, double seconds)
    {
        if (Volatile.Read(ref closing) != 0 || pool is not ("planning" or "delivery" or "merger")
            || !Enum.IsDefined(phase) || !double.IsFinite(seconds) || seconds < 0) return;
        var name = phase.ToString().ToLowerInvariant();
        phaseSeconds.Add(seconds, new("pool", pool), new("phase", name));
        phaseDuration.Record(seconds, new KeyValuePair<string, object?>("phase", name));
    }

    internal void RecordOccupied(string pool, double seconds)
    {
        if (Volatile.Read(ref closing) == 0 && pool is "planning" or "delivery" or "merger"
            && double.IsFinite(seconds) && seconds >= 0)
            occupiedSeconds.Add(seconds, new KeyValuePair<string, object?>("pool", pool));
    }

    internal void RecordRetry(AgentOperationalFailureKind reason)
    {
        if (Volatile.Read(ref closing) == 0 && Enum.IsDefined(reason))
            operationalRetries.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
    }

    internal WorkflowResult RecordPublication(AgentStageResult result)
    {
        if (result.PublicationReceipt is not { } receipt || !Enum.IsDefined(receipt.Role)
            || receipt.Role == AgentRole.Classifier) return WorkflowResult.Uncertain;
        if (Volatile.Read(ref closing) != 0) return WorkflowResult.Success;
        var role = AgentRoles.ToConfigKey(receipt.Role);
        if (!IsRole(role) || !Enum.IsDefined(receipt.Outcome)) return WorkflowResult.Uncertain;
        publishedOutcomes.Add(1, new("role", role), new("outcome", receipt.Outcome.ToString()));
        if (receipt.Outcome == AgentOutcomeChoice.ChangesRequired)
            semanticRepairs.Add(1, new KeyValuePair<string, object?>("role", role));
        var coverage = Lifecycle.Published(result.BeadId, role, result.InvocationId, receipt.PublishedState);
        // Graph materialization is a milestone, not a second Planner first-pass approval.
        if (receipt.Outcome == AgentOutcomeChoice.Approved && !receipt.PublishedState.Planned)
            approvals.Add(1, new("role", role), new("coverage", coverage.ToString()));
        RecordMilestone(result.BeadId, role, result.InvocationId,
            receipt.PublishedState.Planned ? "planning_published" : receipt.Outcome.ToString(), "confirmed_publication");
        return WorkflowResult.Success;
    }

    private void RecordMilestone(string beadId, string? role, string? invocationId, string outcome, string provenance)
    {
        var runtime = Current?.RuntimeId;
        if (Volatile.Read(ref closing) != 0 || !IsOperationalId(beadId) || runtime is null
            || invocationId is not null && !IsOperationalId(invocationId)) return;
        Submit(new("lifecycle", outcome, 0, null, role, beadId, runtime, invocationId,
            EndUtc: DateTime.UtcNow, Source: provenance));
    }

    internal void RecordWorkflow(WorkflowOperation operation, WorkflowResult outcome, double seconds, string runtimeId,
        string? beadId = null, string? invocationId = null)
    {
        if (Volatile.Read(ref closing) != 0 || !Enum.IsDefined(operation) || !Enum.IsDefined(outcome)
            || !double.IsFinite(seconds) || seconds is < 0 or > 2_592_000 || !IsOpaqueId(runtimeId)
            || (beadId is not null && !IsOperationalId(beadId))
            || (invocationId is not null && !IsOperationalId(invocationId))) return;
        var kind = operation.ToString();
        var result = outcome.ToString().ToLowerInvariant();
        workflowResults.Add(1, new("kind", kind), new("outcome", result));
        if (operation is WorkflowOperation.LifecyclePrimary or WorkflowOperation.LifecycleFallback or WorkflowOperation.DuplicateFailure)
            classificationDuration.Record(seconds, new KeyValuePair<string, object?>("kind", kind));
        Submit(new("workflow", result, seconds, null, BeadId: beadId, RuntimeId: runtimeId,
            InvocationId: invocationId, EndUtc: DateTime.UtcNow,
            Kind: kind));
    }

    private void Submit(Record record)
    {
        Admit(spans.Writer, spanAdmission, record, "traces");
        Admit(events.Writer, eventAdmission, record, "logs");
    }

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
        var end = record.EndUtc ?? DateTime.UtcNow;
        activity?.SetStartTime(end.AddSeconds(-record.Seconds));
        activity?.SetEndTime(end);
    }

    private void EmitEvent(Record record)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        logger.Log(LogLevel.Information, record.Operation == "observation"
                ? new EventId(1003, "ObservationCompletion") : record.Operation == "verified_merge"
                ? new EventId(1002, "VerifiedMerge") : record.Operation == "lifecycle"
                ? new EventId(1004, "LifecycleMilestone") : new EventId(1001, "OperationalCompletion"), Tags(record), null,
            (_, _) => record.Operation == "observation" ? "Observation collection completed."
                : record.Operation == "verified_merge" ? "Verified merge closure confirmed."
                : record.Operation == "lifecycle" ? "Lifecycle milestone observed." : "Operational operation completed.");
    }

    private static List<KeyValuePair<string, object?>> Tags(Record record)
    {
        List<KeyValuePair<string, object?>> tags = [new("operation", record.Operation), new("outcome", record.Outcome)];
        if (record.Kind is not null) tags.Add(new("kind", record.Kind));
        if (record.DetailId is not null) tags.Add(new("detail.id", record.DetailId));
        if (record.Role is not null) tags.Add(new("role", record.Role));
        if (record.BeadId is not null) tags.Add(new("bead.id", record.BeadId));
        if (record.RuntimeId is not null) tags.Add(new("runtime.id", record.RuntimeId));
        if (record.InvocationId is not null) tags.Add(new("invocation.id", record.InvocationId));
        if (record.CheckedHead is not null) tags.Add(new("checked.head", record.CheckedHead));
        if (record.MergeCommit is not null) tags.Add(new("merge.commit", record.MergeCommit));
        if (record.Source is not null) tags.Add(new("source", record.Source));
        if (record.Operation == "lifecycle" && record.EndUtc is { } utc)
            tags.Add(new("observed.utc", utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
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
            () => Failed(signal), createExportTransport?.Invoke(signal), stop.Token)) { Timeout = TimeSpan.FromSeconds(3) };
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
        Lifecycle.Unknown(LifecycleLoss.Shutdown);
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
        HttpMessageHandler? transport, CancellationToken stop) : DelegatingHandler(transport ?? new SocketsHttpHandler
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

    private sealed class SafeTraceExporter(OtlpExporterOptions options, ActivitySource ownedSource, double samplingRatio,
        Action<string> failed) : OtlpTraceExporter(options)
    {
        private readonly TraceIdRatioBasedSampler sampler = new(samplingRatio);

        public override ExportResult Export(in Batch<Activity> batch)
        {

            foreach (var activity in batch)
            {
                // A different provider listening to the same fixed source name can upgrade
                // IsAllDataRequested globally. SimpleActivityExportProcessor sends one span
                // per batch; reapply this instance's root sampling decision before export.
                var parameters = new SamplingParameters(default, activity.TraceId, activity.DisplayName,
                    activity.Kind, activity.TagObjects, activity.Links);
                if (sampler.ShouldSample(parameters).Decision != SamplingDecision.RecordAndSample)
                    return ExportResult.Success;
                // Other listeners may decorate owned Activities. Reject rather than export their fields.
                if (!ReferenceEquals(activity.Source, ownedSource) || activity.Events.Any() || activity.Links.Any()
                    || activity.Baggage.Any() || activity.TraceStateString is not null
                    || activity.StatusDescription is not null
                    || activity.DisplayName is not ("host.start" or "host.stop" or "invocation" or "verified_merge" or "workflow" or "observation" or "lifecycle")
                    || activity.TagObjects.Count() > 9
                    || activity.TagObjects.Any(tag => tag.Key switch
                    {
                        "operation" => tag.Value is not ("host.start" or "host.stop" or "invocation" or "verified_merge" or "workflow" or "observation" or "lifecycle"),
                        "outcome" => tag.Value is not ("success" or "failure" or "cancelled" or "completed" or "failed" or "timed_out" or "launch_failed" or "unknown" or "unavailable" or "invalid" or "refused" or "uncertain"
                            or "Approved" or "ReadyForReview" or "ChangesRequired" or "ProductDecisionRequired" or "planning_published"
                            or "pause_enter_observed" or "pause_exit_observed" or "pause_changed_observed"),
                        "source" => tag.Value is not string family || !(IsObservationSource(family) || family is "confirmed_publication" or "observed_snapshot"),
                        "observed.utc" => tag.Value is not string utc || !DateTime.TryParseExact(utc, "O",
                            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedUtc)
                            || parsedUtc.Kind != DateTimeKind.Utc,
                        "kind" => tag.Value is not string kind || !Enum.TryParse<WorkflowOperation>(kind, out var operation) || !Enum.IsDefined(operation),
                        "detail.id" => tag.Value is not string id || !IsOpaqueId(id),
                        "role" => tag.Value is not string role || !IsRole(role),
                        "bead.id" or "invocation.id" => tag.Value is not string identity || !IsOperationalId(identity),
                        "runtime.id" => tag.Value is not string runtime || !IsOpaqueId(runtime),
                        "checked.head" or "merge.commit" => tag.Value is not string revision || !IsRevision(revision),
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
