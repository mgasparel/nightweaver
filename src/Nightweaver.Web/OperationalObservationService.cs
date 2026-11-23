using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Nightweaver.Telemetry;

namespace Nightweaver.Web;

// One instance belongs to the actual Web host, including a BackgroundWebHost child.
internal sealed class OperationalObservationService(
    string projectId, IOperationalObservationSource source, TimeProvider clock,
    IHostApplicationLifetime lifetime, ObservationMemoryBudget? memory = null, IConsoleJournalReader? journal = null) : BackgroundService
{
    private TimeProvider Clock => clock;
    private const int MetadataReserve = 16 * 1024;
    private static readonly string[] InventorySource = ["inventory"];
    private readonly ObservationMemoryBudget budget = memory ?? new();
    private static readonly JsonSerializerOptions Json = ObservationJson.Options;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private ulong revision;
    private bool stopping;
    internal OperationalTelemetry? Telemetry { get; set; }
    private string journalState = "inactive";
    private string? journalIdentity;
    private long journalCursor;
    private bool journalStopped;
    private ObservationError? journalError;
    private long? journalAttempt;
    private int journalFailures;
    private double journalWaitSeconds = 5;
    private long? dirtyReceived;
    private readonly SemaphoreSlim collection = new(1, 1);
    internal string ProjectId => projectId;
    internal string Epoch { get; } = Guid.NewGuid().ToString("N");
    private event Action<string, string>? Changed;

    private sealed class Entry(OperationKey key, ObservationEnvelope envelope, DateTimeOffset now)
    {
        internal DateTimeOffset AdmittedAt { get; } = now;
        internal OperationKey Key { get; } = key;
        internal ObservationEnvelope Envelope { get; set; } = envelope;
        internal DateTimeOffset InterestUntil { get; set; } = now.AddSeconds(30);
        internal DateTimeOffset LastAccess { get; set; } = now;
        internal int Pins { get; set; }
        internal long AdmittedTimestamp { get; set; }
        internal long? AcceptedTimestamp { get; set; }
        internal double AcceptedAgeMs { get; set; }
        internal long? SectionAgeTimestamp { get; set; }
        internal long DirtyGeneration { get; set; } = 1;
        internal long? PendingTimestamp { get; set; }
        internal string Semantic { get; set; } = "";
        internal int Bytes { get; set; } = 1024;
        internal Dictionary<string, object?> ContentIdentities { get; set; } = new(StringComparer.Ordinal);
    }

    internal ObservationEnvelope Read(string key)
    {
        lock (sync)
        {
            var entry = Admit([OperationKey.Parse(key)])[0];
            Age(entry);
            return Response(entry);
        }
    }

    internal void RequestRefresh(IReadOnlyList<string> keys)
    {
        if (keys.Count is < 1 or > 32 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw new ObservationRequestException(400);
        lock (sync) Admit(keys.Select(OperationKey.Parse).ToArray());
        RequestReconciliation();
    }

    // Stream owners subscribe before sampling versions. No asynchronous result owns a key
    // by name alone: a captured Entry is the publication identity.
    internal IDisposable Pin(IReadOnlyList<string> keys)
    {
        if (keys.Count is < 1 or > 32 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw new ObservationRequestException(400);
        lock (sync)
        {
            var admitted = Admit(keys.Select(OperationKey.Parse).ToArray());
            foreach (var entry in admitted) entry.Pins++;
            return new PinLease(this, admitted);
        }
    }

    internal ObservationInterest Subscribe(IReadOnlyList<string> keys, Action<string, string> listener)
    {
        lock (sync)
        {
            var pin = Pin(keys);
            var selected = keys.ToHashSet(StringComparer.Ordinal);
            void Forward(string key, string version) { if (selected.Contains(key)) listener(key, version); }
            Changed += Forward;
            var versions = keys.ToDictionary(key => key, key => entries[key].Envelope.Revision, StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);
            return new(versions, () =>
            {
                lock (sync) { Changed -= Forward; pin.Dispose(); }
            });
        }
    }

    internal sealed class ObservationInterest(IReadOnlyDictionary<string, string> versions, Action release) : IDisposable
    {
        private Action? releaseOnce = release;
        internal IReadOnlyDictionary<string, string> Versions { get; } = versions;
        public void Dispose() => Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }

    private sealed class PinLease(OperationalObservationService owner, Entry[] pinned) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                disposed = true;
                foreach (var entry in pinned)
                {
                    entry.Pins--;
                    entry.InterestUntil = owner.Clock.GetUtcNow().AddSeconds(30);
                }
            }
        }
    }

    private Entry[] Admit(IReadOnlyList<OperationKey> keys)
    {
        if (stopping) throw new ObservationRequestException(503);
        var now = clock.GetUtcNow();
        EvictExpired(now);
        var additions = keys.Where(key => !entries.ContainsKey(key.Value)).ToArray();
        var requested = keys.Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        var workspaces = entries.Values.Where(entry => entry.Key.Kind is "workspace" or "base-workspace" or "section" && IsPinned(entry, now))
            .Select(entry => entry.Key.Argument).Concat(keys.Where(key => key.Kind is "workspace" or "base-workspace" or "section")
                .Select(key => key.Argument)).Distinct(StringComparer.Ordinal).Count();
        if (workspaces > 2) throw new ObservationRequestException(429);
        var created = new List<Entry>();
        foreach (var key in additions)
        {
            var interval = key.Kind is "factory" or "cleanup" ? 2000 : 1000;
            var envelope = new ObservationEnvelope("nightweaver-observation-v1", projectId, key.Value, Epoch,
                ulong.MaxValue.ToString(CultureInfo.InvariantCulture), null, null, "unavailable", null, null,
                InitialSources(key), interval, now);
            var entry = new Entry(key, envelope, now) { AdmittedTimestamp = clock.GetTimestamp(), PendingTimestamp = clock.GetTimestamp() };
            if (key.Kind is not ("factory" or "cleanup"))
                entry.Envelope = envelope with { Policy = new(), Synchronization = new(journalState, true) };
            entry.Bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json).Length + MetadataReserve;
            entry.Semantic = Semantic(envelope);
            created.Add(entry);
        }
        var candidates = entries.Values.Where(entry => !IsPinned(entry, now) && !requested.Contains(entry.Key.Value))
            .OrderBy(entry => entry.LastAccess).ToArray();
        var needed = Math.Max(0, entries.Count + additions.Length - 64);
        var bytes = budget.Used + created.Sum(entry => entry.Bytes);
        var victims = new List<Entry>();
        foreach (var candidate in candidates)
        {
            if (victims.Count >= needed && bytes <= ObservationMemoryBudget.MaximumBytes) break;
            victims.Add(candidate);
            bytes -= candidate.Bytes;
        }
        if (victims.Count < needed || bytes > ObservationMemoryBudget.MaximumBytes) throw new ObservationRequestException(429);
        if (!budget.TryReplace(victims.Cast<object>().ToArray(), created.Select(entry => ((object)entry, entry.Bytes)).ToArray()))
            throw new ObservationRequestException(429);
        foreach (var victim in victims) entries.Remove(victim.Key.Value);
        foreach (var entry in created)
        {
            entry.Envelope = entry.Envelope with { Revision = NextRevision() };
            entries.Add(entry.Key.Value, entry);
        }
        var result = keys.Select(key => entries[key.Value]).ToArray();
        foreach (var entry in result) { entry.LastAccess = now; entry.InterestUntil = now.AddSeconds(30); }
        if (additions.Length > 0) wake.Writer.TryWrite(true);
        return result;
    }

    private static Dictionary<string, ObservationSourceState> InitialSources(OperationKey key)
    {
        string[] names = key.Kind switch
        {
            "section" => InventorySource.Concat(OperationalObservationSource.SectionSources(key)).ToArray(),
            "factory" => ["runtime"], "cleanup" => ["cleanup"], "digest" or "base-digest" or "digest-merges" => ["inventory", "checkpoint"],
            "workspace" => ["inventory", "detail:" + key.Argument, "relations-up:" + key.Argument,
                "relations-down:" + key.Argument, "comments:" + key.Argument, "environment:" + key.Argument],
            "explorer" or "base-explorer" when key.Argument is "ready" or "blocked" => ["inventory", key.Argument],
            _ => ["inventory"]
        };
        return names.ToDictionary(name => name, _ => new ObservationSourceState("unavailable", null, null, null), StringComparer.Ordinal);
    }

    private static bool IsPinned(Entry entry, DateTimeOffset now) => entry.Pins > 0 || now < entry.InterestUntil;
    private void EvictExpired(DateTimeOffset now)
    {
        foreach (var entry in entries.Values.Where(entry => entry.Pins == 0 && now >= entry.InterestUntil.AddSeconds(30)).ToArray())
            { entries.Remove(entry.Key.Value); budget.Release(entry); }
    }

    internal void RequestReconciliation()
    {
        lock (sync) { MarkDirty(); journalAttempt = null; }
        (source as IScheduledTrackerObservationSource)?.Invalidate();
        wake.Writer.TryWrite(true);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(JournalLoopAsync(stoppingToken), TrackerLoopAsync(stoppingToken), RuntimeLoopAsync(stoppingToken), CleanupLoopAsync(stoppingToken), AgeLoopAsync(stoppingToken));


    private void MarkDirty()
    {
        foreach (var entry in entries.Values.Where(entry => entry.Key.Kind is not ("factory" or "cleanup")))
        { entry.DirtyGeneration++; entry.PendingTimestamp ??= clock.GetTimestamp(); }
    }
    private double? PendingAge(Entry entry) => entry.PendingTimestamp is { } pending ? clock.GetElapsedTime(pending).TotalMilliseconds : null;
    private double? ValidatedAge(Entry entry) => entry.AcceptedTimestamp is { } accepted
        ? entry.AcceptedAgeMs + clock.GetElapsedTime(accepted).TotalMilliseconds : null;
    private void AcceptAge(Entry entry, ObservationEnvelope envelope)
    {
        if (envelope.Data is null || envelope.Sources.Values.Any(state => state.ObservedAt is null))
        { entry.AcceptedTimestamp = null; return; }
        entry.AcceptedTimestamp = clock.GetTimestamp();
        entry.AcceptedAgeMs = envelope.Sources.Values.Max(state => state.ValidatedTimestamp is { } validated
            ? clock.GetElapsedTime(validated).TotalMilliseconds
            : Math.Max(0, (clock.GetUtcNow() - state.ObservedAt!.Value).TotalMilliseconds));
    }
    private ObservationEnvelope Response(Entry entry) => entry.Envelope with
    {
        Data = ResponseData(entry),
        ValidatedAgeMs = ValidatedAge(entry),
        PendingAgeMs = PendingAge(entry)
    };

    private JsonElement? ResponseData(Entry entry)
    {
        var data = entry.Envelope.Data;
        if (entry.Key.Kind != "digest-merges" || data is not { ValueKind: JsonValueKind.Object }) return data;
        // Age the accepted body only in the response. Repeated reads, failed or
        // pending replacements and base revalidation cannot reset section age.
        var node = JsonNode.Parse(data.Value.GetRawText())!;
        foreach (var section in node["sections"]!.AsObject().Select(pair => pair.Value!))
        {
            section["validatedAgeMs"] = entry.SectionAgeTimestamp is { } timestamp
                && section["validatedAgeMs"]?.GetValue<double>() is { } age
                    ? JsonValue.Create(age + clock.GetElapsedTime(timestamp).TotalMilliseconds) : null;
        }
        return JsonSerializer.SerializeToElement(node, Json);
    }

    internal async Task PollJournalOnceAsync(CancellationToken token)
    {
        if (journal is null) return;
        bool interested;
        lock (sync) interested = entries.Values.Any(entry => entry.Key.Kind is not ("factory" or "cleanup") && IsPinned(entry, clock.GetUtcNow()));
        if (!interested) { lock (sync) journalState = "inactive"; }
        else if (!journalStopped && (journalAttempt is null || clock.GetElapsedTime(journalAttempt.Value).TotalSeconds >= journalWaitSeconds))
        {
            journalAttempt = clock.GetTimestamp();
            try
            {
                var page = await journal.ReadAsync(journalCursor, token);
                var changed = journalIdentity is not null && journalIdentity != page.Identity;
                var cursor = changed ? 0 : journalCursor;
                var hints = new List<Nightweaver.Inspection.BeadsInvalidation>();
                var gap = changed;
                long? previousSequence = null;
                foreach (var hint in page.Hints)
                {
                    if (hint.Sequence < 0 || previousSequence is { } previous && hint.Sequence < previous)
                        throw new InvalidDataException("Console journal page is out of order.");
                    previousSequence = hint.Sequence;
                    if (hint.Sequence <= cursor) continue;
                    if (hint.Sequence != cursor + 1) gap = true;
                    hints.Add(hint); cursor = hint.Sequence;
                }
                if (page.Hints.Count > 256) throw new InvalidDataException("Console journal page exceeded its limit.");
                if (page.Hints.Count == 256 && hints.Count == 0)
                    throw new InvalidDataException("Console journal returned a full page without cursor progress.");
                lock (sync)
                {
                    journalIdentity = page.Identity; journalCursor = cursor;
                    journalState = page.Hints.Count == 256 || gap ? "catching-up" : "current";
                    if (source is IScheduledTrackerObservationSource scheduled)
                    {
                        if (gap) scheduled.Invalidate(); else if (hints.Count > 0) scheduled.Invalidate(hints);
                    }
                    if (gap || hints.Count > 0) { MarkDirty(); dirtyReceived ??= clock.GetTimestamp(); }
                }
                journalFailures = 0; journalWaitSeconds = 5; journalError = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Nightweaver.Application.BeadsJournalTruncatedException truncated)
            {
                // Lower head proves a replaced/reset session; discard its cursor.
                journalCursor = truncated.Head < journalCursor ? 0 : truncated.Head;
                lock (sync) { journalState = "catching-up"; MarkDirty(); }
                (source as IScheduledTrackerObservationSource)?.Invalidate();
                journalWaitSeconds = 5;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TimeoutException
                or OperationCanceledException or Nightweaver.Application.BeadsJournalSettlementException
                or TargetRepositoryException or ProjectConfigurationException
                or ToolExecutionException or System.Text.Json.JsonException)
            {
                lock (sync) journalState = "degraded";
                journalStopped = exception is Nightweaver.Application.BeadsJournalSettlementException;
                journalError = journalStopped
                    ? new("journal-settlement", "Event reader stopped because child settlement is uncertain. Inspect Web host diagnostics before restarting it.", false)
                    : new("journal-failed", "Event updates are unavailable; source audits continue while the reader retries.");
                journalWaitSeconds = Math.Min(30, 5 * Math.Pow(2, Math.Min(3, journalFailures++)));
            }
        }
    }

    private async Task JournalLoopAsync(CancellationToken token)
    {
        if (journal is null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await PollJournalOnceAsync(token);
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task TrackerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = clock.GetTimestamp();
                await CollectOnceAsync(false, token);
                var delay = TimeSpan.FromMilliseconds(250) - clock.GetElapsedTime(started);
                if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(250);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var timer = Task.Delay(delay, clock, wait.Token);
                var signal = wake.Reader.ReadAsync(wait.Token).AsTask();
                await Task.WhenAny(timer, signal);
                await wait.CancelAsync();
                try { await Task.WhenAll(timer, signal); } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RuntimeLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try { do { await CollectOnceAsync(true, token); } while (await timer.WaitForNextTickAsync(token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try { do { await CollectOnceAsync(true, token, cleanup: true); } while (await timer.WaitForNextTickAsync(token)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task AgeLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                lock (sync)
                {
                    EvictExpired(clock.GetUtcNow());
                    foreach (var entry in entries.Values) Age(entry);
                    ReportHealth();
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task CollectOnceAsync(bool runtime, CancellationToken token, bool cleanup = false)
    {
        if (runtime) { await CollectCoreAsync(runtime, cleanup, token); return; }
        await collection.WaitAsync(token);
        try { await CollectCoreAsync(runtime, cleanup, token); }
        finally { collection.Release(); }
    }

    private async Task CollectCoreAsync(bool runtime, bool cleanup, CancellationToken token)
    {
        Entry[] captured;
        lock (sync)
        {
            EvictExpired(clock.GetUtcNow());
            captured = entries.Values.Where(entry => IsPinned(entry, clock.GetUtcNow())
                && (runtime ? entry.Key.Kind == (cleanup ? "cleanup" : "factory") : entry.Key.Kind is not ("factory" or "cleanup"))).ToArray();
        }
        if (captured.Length == 0)
        {
            if (!runtime)
            {
                if (source is IScheduledTrackerObservationSource scheduled) await scheduled.ReadScheduledAsync([], (_, _) => { }, token);
                else await source.ReadTrackerAsync([], token);
            }
            lock (sync) ReportHealth();
            return;
        }
        if (!runtime && dirtyReceived is { } dirty)
        {
            var remaining = TimeSpan.FromMilliseconds(250) - clock.GetElapsedTime(dirty);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, clock, token);
            dirtyReceived = null;
        }
        Dictionary<Entry, long> generations;
        lock (sync) generations = captured.ToDictionary(entry => entry, entry => entry.DirtyGeneration);
        var keys = captured.Select(entry => entry.Key).ToArray();
        var family = runtime ? cleanup ? "cleanup" : "runtime" : "tracker";
        var started = clock.GetTimestamp();
        IReadOnlyDictionary<string, ObservationSample> samples;
        try
        {
            samples = runtime ? await source.ReadRuntimeAsync(keys, token)
                : source is IProgressiveTrackerObservationSource progressive
                    ? await progressive.ReadProgressiveAsync(keys, (id, state) => Revalidate(captured, id, state),
                        completed => ApplySamples(captured, generations, completed, partialTurn: true), token)
                : source is IScheduledTrackerObservationSource scheduledSource
                    ? await scheduledSource.ReadScheduledAsync(keys, (id, state) => Revalidate(captured, id, state), token)
                    : await source.ReadTrackerAsync(keys, token, (id, state) => Revalidate(captured, id, state));
        }
        catch (Exception)
        {
            Telemetry?.RecordObservation(family, token.IsCancellationRequested ? "cancelled" : "failure", clock.GetElapsedTime(started).TotalSeconds);
            throw;
        }
        // A scheduled no-op is not a collector attempt. Projection counts and subscriber counts
        // never multiply this record; a turn may share one native inventory across many keys.
        if (samples.Count > 0)
            Telemetry?.RecordObservation(family, samples.Values.Any(sample => sample.Error?.Code == "source-timeout"
                || sample.Sources.Values.Any(state => state.Error?.Code == "source-timeout")) ? "timed_out"
                : samples.Values.Any(sample => sample.Error is not null || sample.Sources.Values.Any(state => state.Error is not null))
                    ? "failure" : "success", clock.GetElapsedTime(started).TotalSeconds);
        ApplySamples(captured, generations, samples);
    }

    private void ApplySamples(Entry[] captured, Dictionary<Entry, long> generations,
        IReadOnlyDictionary<string, ObservationSample> samples, bool partialTurn = false)
    {
        lock (sync)
        {
            var reconcileAgain = false;
            foreach (var entry in captured)
            {
                if (stopping || !entries.TryGetValue(entry.Key.Value, out var current) || !ReferenceEquals(current, entry)) continue;
                if (!samples.TryGetValue(entry.Key.Value, out var sample))
                {
                    if (partialTurn) continue;
                    entry.Envelope = entry.Envelope with { NextAttemptAt = clock.GetUtcNow().AddMilliseconds(entry.Envelope.TargetIntervalMs) };
                    Age(entry); continue;
                }
                var sources = sample.Pending || sample.Error is not null && !sample.Partial && !sample.ConfirmedAbsence
                    ? entry.Envelope.Sources.ToDictionary() : new Dictionary<string, ObservationSourceState>(StringComparer.Ordinal);
                foreach (var (id, observed) in sample.Sources)
                {
                    var previous = entry.Envelope.Sources.GetValueOrDefault(id);
                    if (entry.Key.Kind == "section" && sample.Error is not null && !sample.Partial
                        && entry.Envelope.Data is not null && previous is not null
                        && !ReferenceEquals(previous.ContentIdentity, observed.ContentIdentity))
                    {
                        // A failed replacement cannot lend new base provenance
                        // to the retained historical section body.
                        sources[id] = previous with { Health = "stale", LastAttemptAt = observed.LastAttemptAt, Error = observed.Error };
                        continue;
                    }
                    if (sample.Pending)
                    {
                        // Pending successes belong to a replacement body. Only
                        // failure diagnostics may overlay the accepted provenance.
                        if (observed.Error is not null)
                            sources[id] = (previous ?? new ObservationSourceState("unavailable", null, null, null)) with
                            {
                                Health = previous?.ObservedAt is null ? "unavailable" : "stale",
                                LastAttemptAt = observed.LastAttemptAt, Error = observed.Error
                            };
                        continue;
                    }
                    sources[id] = observed.Error is not null && observed.ObservedAt is null && previous?.ObservedAt is not null
                        ? observed with { Health = "stale", ObservedAt = previous.ObservedAt } : observed;
                }
                if (sample.Pending)
                {
                    var failure = sample.Error ?? sample.Sources.Values.FirstOrDefault(state => state.Error is not null)?.Error;
                    Publish(entry, entry.Envelope with { Sources = sources,
                        Health = failure is null ? entry.Envelope.Health : entry.Envelope.Data is null ? "unavailable" : "stale",
                        Error = failure ?? entry.Envelope.Error, LastAttemptAt = sample.AttemptedAt,
                        NextAttemptAt = clock.GetUtcNow().AddMilliseconds(entry.Envelope.TargetIntervalMs) });
                    Age(entry);
                    continue;
                }
                var missing = sample.Error?.Code == "resource-missing";
                var data = sample.Error is null || sample.Partial || sample.ConfirmedAbsence
                    ? JsonSerializer.SerializeToElement(sample.Data, Json) : missing ? null : entry.Envelope.Data;
                var health = sample.Error is null ? "fresh" : missing || data is null ? "unavailable" : "stale";
                if (entry.Key.Kind == "section" && !missing && data is null && sample.Error is not null)
                    data = JsonSerializer.SerializeToElement(new { schema = "nightweaver-section-v1", beadId = entry.Key.Argument,
                        name = entry.Key.Query, baseIdentity = (object?)null, state = "failed", value = (object?)null }, Json);
                if (entry.Key.Kind == "section" && data is { } section && section.GetProperty("value").ValueKind == JsonValueKind.Null)
                    health = "unavailable";
                var updated = entry.Envelope with
                {
                    Data = data, Health = health, Error = sample.Error, Sources = sources,
                    ObservedAt = sample.Error is null || sample.ConfirmedAbsence ? sample.ObservedAt : missing ? null : entry.Envelope.ObservedAt,
                    LastAttemptAt = sample.AttemptedAt,
                    NextAttemptAt = clock.GetUtcNow().AddMilliseconds(entry.Envelope.TargetIntervalMs)
                };
                var previousPending = entry.PendingTimestamp;
                if (sample.Error is null || sample.ConfirmedAbsence)
                {
                    if (generations[entry] == entry.DirtyGeneration) entry.PendingTimestamp = null;
                    else reconcileAgain = true;
                }
                var accepted = Publish(entry, updated, projection: sample.Error is null || sample.Partial || missing,
                    sectionAgeTimestamp: sample.SectionAgeTimestamp);
                if (!accepted) entry.PendingTimestamp = previousPending;
            }
            // A wake can precede the source read but follow our generation capture.
            // The source may already be clean while the newer publication obligation
            // remains. Reproject current source generations on another turn;
            // actual outstanding hints retain their selective source dirtiness.
            // Broad invalidation here would turn a named comment into full reads.
            if (reconcileAgain) (source as IScheduledTrackerObservationSource)?.Reconcile();
            ReportHealth();
        }
    }

    // Called under sync. Only fixed family/state names and monotonic accepted-body age
    // cross the telemetry boundary. No key, source map, payload or error text is submitted.
    private void ReportHealth()
    {
        if (Telemetry is not { } telemetry) return;
        foreach (var family in new[] { "tracker", "runtime", "cleanup" })
        {
            var interested = entries.Values.Where(entry => IsPinned(entry, clock.GetUtcNow())
                && (family == "runtime" ? entry.Key.Kind == "factory"
                    : family == "cleanup" ? entry.Key.Kind == "cleanup" : entry.Key.Kind is not ("factory" or "cleanup"))).ToArray();
            if (stopping) telemetry.SetObservationHealth(family, [("stopped", null)]);
            else if (interested.Length == 0) telemetry.SetObservationHealth(family, [("idle", null)]);
            else telemetry.SetObservationHealth(family, interested.Select(entry =>
            {
                var envelope = WithAge(entry, entry.Envelope);
                var health = envelope.Health == "fresh" ? "healthy" : envelope.Health;
                if (family == "runtime" && health == "healthy" && envelope.Data is { } data && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("availability", out var availability) && availability.ValueKind == JsonValueKind.String
                    && availability.GetString() == "stopped") health = "stopped";
                else if (health == "healthy" && entry.PendingTimestamp is not null) health = "pending";
                var age = ValidatedAge(entry) / 1000;
                return (health, age);
            }).ToArray());
        }
    }

    private void Revalidate(Entry[] captured, string id, ObservationSourceState state)
    {
        if (state.ContentIdentity is null || state.Health != "fresh" || state.Error is not null) return;
        lock (sync)
        {
            foreach (var entry in captured)
            {
                if (stopping || !entries.TryGetValue(entry.Key.Value, out var current) || !ReferenceEquals(current, entry)) continue;
                var envelope = entry.Envelope;
                if (envelope.Data is null || !entry.ContentIdentities.TryGetValue(id, out var backing)
                    || !envelope.Sources.TryGetValue(id, out var previous)) continue;
                if (!ReferenceEquals(backing, state.ContentIdentity))
                {
                    // Audits can discover unjournaled changes too. Start the known
                    // replacement obligation without lending its age to the old body.
                    entry.PendingTimestamp ??= clock.GetTimestamp();
                    continue;
                }
                if (!ReferenceEquals(previous.ContentIdentity, state.ContentIdentity)) continue;
                var sources = envelope.Sources.ToDictionary();
                sources[id] = state;
                var observed = sources.Values.Min(item => item.ObservedAt);
                var coherent = sources.Count == entry.ContentIdentities.Count && sources.All(pair => pair.Value.ContentIdentity is not null
                    && entry.ContentIdentities.TryGetValue(pair.Key, out var identity) && ReferenceEquals(identity, pair.Value.ContentIdentity));
                var recovered = coherent && envelope.Error?.Code == "budget-exceeded"
                    && sources.Values.All(item => item.Health == "fresh" && item.Error is null);
                var candidate = envelope with { Sources = sources,
                    ObservedAt = coherent && observed > envelope.ObservedAt ? observed : envelope.ObservedAt,
                    Health = recovered ? "fresh" : envelope.Health, Error = recovered ? null : envelope.Error };
                if (coherent) AcceptAge(entry, candidate);
                var updated = WithAge(entry, candidate);
                if (updated.Health == envelope.Health && updated.Error == envelope.Error
                    && updated.Sources.All(pair => envelope.Sources.TryGetValue(pair.Key, out var old) && pair.Value.Health == old.Health && pair.Value.Error == old.Error))
                    // Only fixed-width sampling timestamps changed. Avoid serializing
                    // the complete retained projection for every source read.
                    entry.Envelope = updated with { Sources = updated.Sources.ToFrozenDictionary(StringComparer.Ordinal) };
                else Publish(entry, updated);
            }
        }
    }

    private void Age(Entry entry)
    {
        var aged = WithAge(entry, entry.Envelope);
        if (aged.Policy is not null)
        {
            var pending = PendingAge(entry);
            aged = aged with { Synchronization = new(journalState, pending is not null)
                { Delayed = pending > aged.Policy.MaxPendingAgeMs, Error = journalError } };
        }
        if (aged.Health == entry.Envelope.Health && aged.Error == entry.Envelope.Error && aged.Synchronization == entry.Envelope.Synchronization
            && aged.Sources.All(pair => entry.Envelope.Sources.TryGetValue(pair.Key, out var previous) && pair.Value == previous))
            return;
        Publish(entry, aged);
    }

    private ObservationEnvelope WithAge(Entry entry, ObservationEnvelope envelope)
    {
        envelope = WithSourceAge(entry, envelope);
        if (entry.Key.Kind != "section" || envelope.Data is not { } data) return envelope;
        var value = data.GetProperty("value");
        var state = envelope.Health == "stale" && value.ValueKind != JsonValueKind.Null ? "stale"
            : envelope.Error is not null && value.ValueKind == JsonValueKind.Null ? "failed" : data.GetProperty("state").GetString();
        if (state == data.GetProperty("state").GetString()) return envelope;
        var node = JsonNode.Parse(data.GetRawText())!;
        node["state"] = state;
        return envelope with { Data = JsonSerializer.SerializeToElement(node, Json) };
    }

    private ObservationEnvelope WithSourceAge(Entry entry, ObservationEnvelope envelope)
    {
        var now = clock.GetUtcNow();
        var maximum = TimeSpan.FromMilliseconds(envelope.Policy?.MaxSnapshotAgeMs ?? envelope.TargetIntervalMs + 2000);
        var error = new ObservationError("budget-exceeded", "Observation is overdue; retained data may be stale.");
        var overdue = envelope.Policy is not null
            ? ValidatedAge(entry) is { } age && age > maximum.TotalMilliseconds
            : now - (envelope.ObservedAt ?? envelope.LastAttemptAt ?? entry.AdmittedAt) > maximum;
        var sources = envelope.Sources.ToDictionary(pair => pair.Key, pair =>
        {
            var state = pair.Value;
            var sourceAge = state.ValidatedTimestamp is { } validated ? clock.GetElapsedTime(validated)
                : now - (state.ObservedAt ?? state.LastAttemptAt ?? entry.AdmittedAt);
            if (sourceAge <= maximum
                || state.Error is not null && state.Error.Code != "budget-exceeded") return state;
            overdue = true;
            return state with { Health = state.ObservedAt is null ? "unavailable" : "stale", Error = error };
        });
        if (!overdue || envelope.Error is not null && envelope.Error.Code != "budget-exceeded")
            return envelope with { Sources = sources };
        return envelope with { Health = envelope.Data is null ? "unavailable" : "stale", Error = error, Sources = sources };
    }

    private bool Publish(Entry entry, ObservationEnvelope envelope, bool projection = false, long? sectionAgeTimestamp = null)
    {
        var previousAccepted = entry.AcceptedTimestamp;
        var previousAge = entry.AcceptedAgeMs;
        if (projection) AcceptAge(entry, envelope);
        envelope = WithAge(entry, envelope);
        if (envelope.Policy is not null)
        {
            var pending = PendingAge(entry);
            envelope = envelope with { Synchronization = new(journalState, pending is not null)
                { Delayed = pending > envelope.Policy.MaxPendingAgeMs, Error = journalError } };
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope with { Revision = ulong.MaxValue.ToString(CultureInfo.InvariantCulture) }, Json).Length + MetadataReserve;
        if (!budget.TryReplace([], [((object)entry, bytes)]))
        {
            projection = false;
            entry.AcceptedTimestamp = previousAccepted; entry.AcceptedAgeMs = previousAge;
            envelope = envelope with { Data = entry.Envelope.Data, ObservedAt = entry.Envelope.ObservedAt, Sources = entry.Envelope.Sources,
                Health = entry.Envelope.Data is null ? "unavailable" : "stale",
                Error = new("source-limit", "Projection exceeds the observation cache budget.") };
            var required = JsonSerializer.SerializeToUtf8Bytes(envelope with { Revision = ulong.MaxValue.ToString(CultureInfo.InvariantCulture) }, Json).Length;
            bytes = Math.Max(entry.Bytes, required);
            if (!budget.TryReplace([], [((object)entry, bytes)]))
            {
                // Keep the already reserved metadata shape when a growing source map
                // itself cannot fit. The explicit projection-level limit remains visible.
                envelope = envelope with { Sources = entry.Envelope.Sources };
                bytes = entry.Bytes;
                _ = budget.TryReplace([], [((object)entry, bytes)]);
            }
        }
        var semantic = Semantic(envelope);
        var changed = semantic != entry.Semantic;
        if (changed) envelope = envelope with { Revision = NextRevision() };
        entry.Semantic = semantic;
        entry.Envelope = envelope with { Sources = envelope.Sources.ToFrozenDictionary(StringComparer.Ordinal) };
        if (projection) entry.SectionAgeTimestamp = sectionAgeTimestamp;
        if (projection) entry.ContentIdentities = envelope.Sources.ToDictionary(pair => pair.Key, pair => pair.Value.ContentIdentity, StringComparer.Ordinal);
        entry.Bytes = bytes;
        if (changed) Changed?.Invoke(entry.Key.Value, envelope.Revision);
        return projection;
    }

    private static string Semantic(ObservationEnvelope envelope)
    {
        var node = JsonSerializer.SerializeToNode(new { envelope.Data, envelope.Health, envelope.Error, envelope.Sources, envelope.Policy, envelope.Synchronization }, Json)!;
        // Only sampling clocks are excluded; domain activity and checkpoint dates remain.
        static void Strip(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                foreach (var key in new[] { "observedAt", "lastAttemptAt", "generatedAt", "runtimeObservedAt", "observed_at" }) obj.Remove(key);
                // The runtime publisher increments its revision on every heartbeat.
                if (obj.ContainsKey("runtime_id")) obj.Remove("revision");
                var properties = obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                obj.Clear();
                foreach (var pair in properties) { Strip(pair.Value); obj.Add(pair.Key, pair.Value); }
            }
            else if (value is JsonArray array) foreach (var item in array) Strip(item);
        }
        Strip(node);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    private string NextRevision()
    {
        if (revision == ulong.MaxValue)
        {
            stopping = true;
            lifetime.StopApplication();
            throw new ObservationRequestException(503);
        }
        return (++revision).ToString(CultureInfo.InvariantCulture);
    }

    public override void Dispose()
    {
        lock (sync)
        {
            stopping = true;
            ReportHealth();
            foreach (var entry in entries.Values) budget.Release(entry);
            entries.Clear();
        }
        base.Dispose();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (sync) { stopping = true; ReportHealth(); }
        await base.StopAsync(cancellationToken);
        await collection.WaitAsync(cancellationToken);
        try
        {
            if (source is OperationalObservationSource adapter) await adapter.ReadScheduledAsync([], (_, _) => { }, CancellationToken.None);
        }
        finally { collection.Release(); }
    }
}
