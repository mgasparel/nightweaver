using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Nightweaver.Web;

// One instance belongs to the actual Web host, including a BackgroundWebHost child.
internal sealed class OperationalObservationService(
    string projectId, IOperationalObservationSource source, TimeProvider clock,
    IHostApplicationLifetime lifetime, ObservationMemoryBudget? memory = null) : BackgroundService
{
    private TimeProvider Clock => clock;
    private const int MetadataReserve = 16 * 1024;
    private readonly ObservationMemoryBudget budget = memory ?? new();
    private static readonly JsonSerializerOptions Json = ObservationJson.Options;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private ulong revision;
    private bool stopping;
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
        internal string Semantic { get; set; } = "";
        internal int Bytes { get; set; } = 1024;
    }

    internal ObservationEnvelope Read(string key)
    {
        lock (sync)
        {
            var entry = Admit([OperationKey.Parse(key)])[0];
            Age(entry);
            return entry.Envelope;
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
        var workspaces = entries.Values.Count(entry => entry.Key.Kind == "workspace" && IsPinned(entry, now))
            + keys.Count(key => key.Kind == "workspace" && (!entries.TryGetValue(key.Value, out var entry) || !IsPinned(entry, now)));
        if (workspaces > 2) throw new ObservationRequestException(429);
        var created = new List<Entry>();
        foreach (var key in additions)
        {
            var interval = key.Kind is "factory" or "cleanup" ? 2000 : 1000;
            var envelope = new ObservationEnvelope("nightweaver-observation-v1", projectId, key.Value, Epoch,
                ulong.MaxValue.ToString(CultureInfo.InvariantCulture), null, null, "unavailable", null, null,
                InitialSources(key), interval, now);
            var entry = new Entry(key, envelope, now);
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
        if (additions.Length > 0) RequestReconciliation();
        return result;
    }

    private static Dictionary<string, ObservationSourceState> InitialSources(OperationKey key)
    {
        string[] names = key.Kind switch
        {
            "factory" => ["runtime"], "cleanup" => ["cleanup"], "digest" => ["inventory", "checkpoint"],
            "workspace" => ["inventory", "detail:" + key.Argument, "relations-up:" + key.Argument,
                "relations-down:" + key.Argument, "comments:" + key.Argument, "environment:" + key.Argument],
            "explorer" when key.Argument is "ready" or "blocked" => ["inventory", key.Argument],
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

    internal void RequestReconciliation() => wake.Writer.TryWrite(true);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(TrackerLoopAsync(stoppingToken), RuntimeLoopAsync(stoppingToken), CleanupLoopAsync(stoppingToken), AgeLoopAsync(stoppingToken));

    private async Task TrackerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = clock.GetTimestamp();
                await CollectOnceAsync(false, token);
                var delay = TimeSpan.FromSeconds(1) - clock.GetElapsedTime(started);
                if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(1);
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
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task CollectOnceAsync(bool runtime, CancellationToken token, bool cleanup = false)
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
            if (!runtime) await source.ReadTrackerAsync([], token);
            return;
        }
        var keys = captured.Select(entry => entry.Key).ToArray();
        var samples = runtime ? await source.ReadRuntimeAsync(keys, token) : await source.ReadTrackerAsync(keys, token);
        lock (sync)
        {
            foreach (var entry in captured)
            {
                if (stopping || !entries.TryGetValue(entry.Key.Value, out var current) || !ReferenceEquals(current, entry)) continue;
                if (!samples.TryGetValue(entry.Key.Value, out var sample))
                {
                    entry.Envelope = entry.Envelope with { NextAttemptAt = clock.GetUtcNow().AddMilliseconds(entry.Envelope.TargetIntervalMs) };
                    Age(entry); continue;
                }
                var sources = sample.Pending || sample.Error is not null && !sample.Partial
                    ? entry.Envelope.Sources.ToDictionary() : new Dictionary<string, ObservationSourceState>(StringComparer.Ordinal);
                foreach (var (id, observed) in sample.Sources)
                {
                    var previous = entry.Envelope.Sources.GetValueOrDefault(id);
                    sources[id] = observed.Error is not null && observed.ObservedAt is null && previous?.ObservedAt is not null
                        ? observed with { Health = "stale", ObservedAt = previous.ObservedAt } : observed;
                }
                if (sample.Pending)
                {
                    Publish(entry, entry.Envelope with { LastAttemptAt = sample.AttemptedAt, Sources = sources,
                        NextAttemptAt = clock.GetUtcNow().AddMilliseconds(entry.Envelope.TargetIntervalMs) });
                    Age(entry);
                    continue;
                }
                var missing = sample.Error?.Code == "resource-missing";
                var data = sample.Error is null || sample.Partial ? JsonSerializer.SerializeToElement(sample.Data, Json) : missing ? null : entry.Envelope.Data;
                var health = sample.Error is null ? "fresh" : data is null ? "unavailable" : "stale";
                var updated = entry.Envelope with
                {
                    Data = data, Health = health, Error = sample.Error, Sources = sources,
                    ObservedAt = sample.Error is null ? sample.ObservedAt : missing ? null : entry.Envelope.ObservedAt,
                    LastAttemptAt = sample.AttemptedAt,
                    NextAttemptAt = clock.GetUtcNow().AddMilliseconds(entry.Envelope.TargetIntervalMs)
                };
                Publish(entry, updated);
            }
        }
    }

    private void Age(Entry entry)
    {
        var aged = WithAge(entry, entry.Envelope);
        if (aged.Health == entry.Envelope.Health && aged.Error == entry.Envelope.Error
            && aged.Sources.All(pair => entry.Envelope.Sources.TryGetValue(pair.Key, out var previous) && pair.Value == previous))
            return;
        Publish(entry, aged);
    }

    private ObservationEnvelope WithAge(Entry entry, ObservationEnvelope envelope)
    {
        var now = clock.GetUtcNow();
        var maximum = TimeSpan.FromMilliseconds(envelope.TargetIntervalMs + 2000);
        var error = new ObservationError("budget-exceeded", "Observation is overdue; retained data may be stale.");
        var overdue = now - (envelope.ObservedAt ?? envelope.LastAttemptAt ?? entry.AdmittedAt) > maximum;
        var sources = envelope.Sources.ToDictionary(pair => pair.Key, pair =>
        {
            var state = pair.Value;
            if (now - (state.ObservedAt ?? state.LastAttemptAt ?? entry.AdmittedAt) <= maximum
                || state.Error is not null && state.Error.Code != "budget-exceeded") return state;
            overdue = true;
            return state with { Health = state.ObservedAt is null ? "unavailable" : "stale", Error = error };
        });
        if (!overdue || envelope.Error is not null && envelope.Error.Code != "budget-exceeded")
            return envelope with { Sources = sources };
        return envelope with { Health = envelope.Data is null ? "unavailable" : "stale", Error = error, Sources = sources };
    }

    private void Publish(Entry entry, ObservationEnvelope envelope)
    {
        envelope = WithAge(entry, envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope with { Revision = ulong.MaxValue.ToString(CultureInfo.InvariantCulture) }, Json).Length + MetadataReserve;
        if (!budget.TryReplace([], [((object)entry, bytes)]))
        {
            envelope = envelope with { Data = entry.Envelope.Data, ObservedAt = entry.Envelope.ObservedAt,
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
        entry.Bytes = bytes;
        if (changed) Changed?.Invoke(entry.Key.Value, envelope.Revision);
    }

    private static string Semantic(ObservationEnvelope envelope)
    {
        var node = JsonSerializer.SerializeToNode(new { envelope.Data, envelope.Health, envelope.Error, envelope.Sources }, Json)!;
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
            foreach (var entry in entries.Values) budget.Release(entry);
            entries.Clear();
        }
        base.Dispose();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (sync) stopping = true;
        await base.StopAsync(cancellationToken);
    }
}
