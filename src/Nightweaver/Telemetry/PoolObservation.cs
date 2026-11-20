using System.Diagnostics.Metrics;

namespace Nightweaver.Telemetry;

internal enum WaitCensor { Ineligible, Failure, Stale, Capacity, Shutdown, ReservedElsewhere, LeftCensored }

/// <summary>Bounded observation only; admission remains entirely with the pool.</summary>
internal sealed class PoolObservation(
    string pool, int capacity, TimeProvider clock, Action<double> complete,
    Action<WaitCensor, int> censor, Func<bool>? stopping = null, int recordLimit = 3333)
{
    // Three pools retain at most 9,999 identities per runtime, below the 10,000 cap.
    private readonly object gate = new();
    private readonly Dictionary<string, (long Start, bool Complete)> waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowTiming?> active = new(StringComparer.Ordinal);
    private long? observed;
    private bool continuous;
    private bool covered;
    private bool backpressure;
    private bool stopped;
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    internal void Observe(IEnumerable<string> eligible, bool admissionBackpressure = false)
    {
        lock (gate)
        {
            Expire();
            if (stopped) return;
            var ids = eligible.Where(id => !active.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
            foreach (var id in waiting.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                waiting.Remove(id);
                censor(WaitCensor.Ineligible, 1);
            }
            var previouslyCovered = covered;
            covered = true;
            foreach (var id in ids)
            {
                if (waiting.ContainsKey(id)) continue;
                if (waiting.Count >= recordLimit)
                {
                    covered = false;
                    censor(WaitCensor.Capacity, 1);
                    continue;
                }
                waiting.Add(id, (clock.GetTimestamp(), continuous && previouslyCovered));
            }
            observed = clock.GetTimestamp();
            continuous = true;
            backpressure = admissionBackpressure;
        }
    }

    internal void Unknown(WaitCensor reason)
    {
        lock (gate)
        {
            if (waiting.Count > 0) censor(reason, waiting.Count);
            waiting.Clear();
            continuous = covered = false;
            if (reason == WaitCensor.Shutdown) stopped = true;
        }
    }

    internal void Reserve(string id, WorkflowTiming? timing)
    {
        lock (gate)
        {
            Expire();
            if (waiting.Remove(id, out var interval))
            {
                if (interval.Complete) complete(clock.GetElapsedTime(interval.Start).TotalSeconds);
                else censor(WaitCensor.LeftCensored, 1);
            }
            active[id] = timing;
        }
    }

    internal void ExcludeReserved(string id)
    {
        lock (gate)
            if (waiting.Remove(id)) censor(WaitCensor.ReservedElsewhere, 1);
    }

    internal void Release(string id) { lock (gate) active.Remove(id); }

    private void Expire()
    {
        if (continuous && observed is { } stamp && clock.GetElapsedTime(stamp) > FreshFor)
            Unknown(WaitCensor.Stale);
    }

    internal Measurement<double>[] Read(string instrument)
    {
        // Read the stop gate outside our lock: reservation holds it before entering this helper.
        if (stopping?.Invoke() == true) Unknown(WaitCensor.Shutdown);
        lock (gate)
        {
            Expire();
            Measurement<double> Value(double value) => new(value, new KeyValuePair<string, object?>("pool", pool));
            var phases = instrument == "occupied"
                ? active.Values.Select(timing => timing?.ActivePhase ?? WorkflowPhase.Other).ToArray() : [];
            return instrument switch
            {
                "capacity" => [Value(capacity)],
                "occupied" => Enum.GetValues<WorkflowPhase>().Select(phase => new Measurement<double>(
                    phases.Count(current => current == phase),
                    new("pool", pool), new("phase", phase.ToString().ToLowerInvariant()))).ToArray(),
                "fresh" => [Value(continuous ? 1 : 0)],
                "coverage" => [Value(continuous && covered ? 1 : 0)],
                "age" => observed is { } stamp ? [Value(clock.GetElapsedTime(stamp).TotalSeconds)] : [],
                "backpressure" => continuous ? [Value(backpressure ? 1 : 0)] : [],
                "queued" => continuous && covered ? [Value(waiting.Count)] : [],
                "oldest" => continuous && covered ? [Value(waiting.Count == 0 ? 0 :
                    waiting.Values.Max(interval => clock.GetElapsedTime(interval.Start).TotalSeconds))] : [],
                _ => []
            };
        }
    }
}
