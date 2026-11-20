using System.Diagnostics.Metrics;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Telemetry;

internal enum HumanPause { NeedsHuman, ProductDecision, Combined }
internal enum LifecycleLoss { Failure, Stale, Capacity, Missing, Closed, Changed, LeftCensored, Shutdown }
internal enum ApprovalCoverage { FirstPass, LaterPass, Unknown }

// One runtime-wide ceiling shared with eligibility observation, never a workflow gate.
internal sealed class ObservationBudget(int limit = 10000)
{
    private int count;
    internal bool Acquire()
    {
        if (Interlocked.Increment(ref count) <= limit) return true;
        Interlocked.Decrement(ref count);
        return false;
    }
    internal void Release(int amount = 1) => Interlocked.Add(ref count, -amount);
}

/// <summary>Lossy, bounded runtime observations. Never consulted by workflow authority.</summary>
internal sealed class LifecycleObservation(TimeProvider clock,
    Action<HumanPause, double> completed, Action<LifecycleLoss> lost,
    Action<string, string> milestone, int limit = 10000, ObservationBudget? budget = null)
{
    private sealed class Entry
    {
        internal HumanPause? Pause;
        internal long Start;
        internal bool Complete;
        internal string? Stage;
        internal bool KnownVisit;
        internal int Repairs;
        internal int Attempts;
        internal string? LastInvocation;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long? observed;
    private bool continuous;
    private bool covered;

    internal void Unknown(LifecycleLoss reason)
    {
        lock (gate)
        {
            if (entries.Count != 0) lost(reason);
            budget?.Release(entries.Count);
            entries.Clear();
            continuous = covered = false;
        }
    }

    private void Expire()
    {
        if (continuous && observed is { } stamp && clock.GetElapsedTime(stamp).TotalSeconds > 60)
            Unknown(LifecycleLoss.Stale);
    }

    internal void Observe(IEnumerable<BeadSnapshot> inventory)
    {
        lock (gate)
        {
            Expire();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            List<(HumanPause Pause, double Seconds)> completions = [];
            var priorCoverage = continuous && covered;
            covered = true;
            foreach (var bead in inventory)
            {
                if (bead.IssueType is not ("task" or "feature" or "bug")) continue;
                seen.Add(bead.Id);
                if (bead.Status == "closed")
                {
                    if (entries.Remove(bead.Id, out var closed))
                    {
                        budget?.Release();
                        if (closed.Pause is not null) lost(LifecycleLoss.Closed);
                    }
                    continue;
                }
                var retained = entries.TryGetValue(bead.Id, out var entry);
                if (entry is null)
                {
                    if (entries.Count >= limit || budget?.Acquire() == false)
                    { covered = false; lost(LifecycleLoss.Capacity); continue; }
                    entry = new();
                    entries.Add(bead.Id, entry);
                }
                // A newly visible Bead may be old, reopened or restored after eviction.
                var knownPrevious = retained && priorCoverage;
                var human = bead.Labels.Contains("needs-human");
                var product = bead.Labels.Contains("product-decision-required");
                HumanPause? pause = human && product ? HumanPause.Combined : human ? HumanPause.NeedsHuman
                    : product ? HumanPause.ProductDecision : null;
                if (pause != entry.Pause)
                {
                    if (entry.Pause is { } previous)
                    {
                        if (pause is null && entry.Complete) completions.Add((previous, clock.GetElapsedTime(entry.Start).TotalSeconds));
                        else lost(pause is null ? LifecycleLoss.LeftCensored : LifecycleLoss.Changed);
                        milestone(bead.Id, pause is null ? "pause_exit_observed" : "pause_changed_observed");
                    }
                    else milestone(bead.Id, "pause_enter_observed");
                    entry.Pause = pause;
                    entry.Start = clock.GetTimestamp();
                    entry.Complete = knownPrevious;
                    // A resume/reset cannot establish first-visit history.
                    entry.KnownVisit = false;
                }
                var observation = BeadWorkflowObservation.Read(bead);
                var stage = observation.Error is null && observation.State is { } state
                    ? state.Stage ?? (state.Planned ? null : bead.IssueType == "task" ? "stage:implement" : "stage:plan") : null;
                if (stage != entry.Stage)
                {
                    entry.KnownVisit = knownPrevious && observation.Error is null && pause is null;
                    entry.Attempts = 0;
                    entry.LastInvocation = null;
                    entry.Stage = stage;
                }
                if (observation.Error is not null) entry.KnownVisit = false;
                if (observation.RepairCycles is { } repairs)
                {
                    if (repairs < entry.Repairs) entry.KnownVisit = false;
                    entry.Repairs = repairs;
                }
                else entry.KnownVisit = false;
            }
            foreach (var id in entries.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                entries.Remove(id);
                budget?.Release();
                lost(LifecycleLoss.Missing);
            }
            if (!covered)
                foreach (var entry in entries.Values) { entry.Complete = false; entry.KnownVisit = false; }
            else
                foreach (var interval in completions) completed(interval.Pause, interval.Seconds);
            observed = clock.GetTimestamp();
            continuous = true;
        }
    }

    internal void Attempt(string id, string role, string invocation)
    {
        lock (gate)
        {
            Expire();
            if (RoleStage(role) is not { } stage || !entries.TryGetValue(id, out var entry) || stage != entry.Stage) return;
            if (entry.LastInvocation == invocation) return;
            entry.LastInvocation = invocation;
            entry.Attempts++;
        }
    }

    internal ApprovalCoverage Published(string id, string role, string invocation, BeadWorkflowState state)
    {
        lock (gate)
        {
            Expire();
            var coverage = ApprovalCoverage.Unknown;
            if (entries.TryGetValue(id, out var entry))
            {
                if (entry.KnownVisit && entry.Stage == RoleStage(role) && entry.LastInvocation == invocation)
                    coverage = entry.Repairs == 0 && entry.Attempts == 1 ? ApprovalCoverage.FirstPass : ApprovalCoverage.LaterPass;
                entry.Stage = state.Stage;
                entry.Repairs = state.RepairCycles;
                entry.KnownVisit = !state.ProductDecisionRequired && !state.NeedsHuman;
                entry.Attempts = 0;
                entry.LastInvocation = null;
            }
            return coverage;
        }
    }

    private static string? RoleStage(string role) => role switch
    {
        "planner" => "stage:plan", "plan_challenger" => "stage:plan-challenge",
        "implementer" => "stage:implement", "reviewer" => "stage:review", "qa" => "stage:qa", "merger" => "stage:merge", _ => null
    };

    internal Measurement<double>[] Read(string instrument)
    {
        lock (gate)
        {
            Expire();
            if (instrument == "fresh") return [new(continuous ? 1 : 0)];
            if (instrument == "coverage") return [new(continuous && covered ? 1 : 0)];
            if (instrument == "age") return observed is { } stamp ? [new(clock.GetElapsedTime(stamp).TotalSeconds)] : [];
            if (!continuous || !covered) return [];
            return Enum.GetValues<HumanPause>().Select(pause =>
            {
                var intervals = entries.Values.Where(entry => entry.Pause == pause).ToArray();
                return new Measurement<double>(instrument == "paused" ? intervals.Length : intervals.Length == 0 ? 0
                    : intervals.Max(entry => clock.GetElapsedTime(entry.Start).TotalSeconds),
                    new KeyValuePair<string, object?>("pause", pause.ToString()));
            }).ToArray();
        }
    }
}
