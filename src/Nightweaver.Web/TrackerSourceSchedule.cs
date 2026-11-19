using Nightweaver.Inspection;

namespace Nightweaver.Web;

// The schedule retains identities and generations, never journal payloads. One
// collector owns values; hints can arrive concurrently with an authoritative read.
internal sealed class TrackerSourceSchedule(TimeProvider clock)
{
    private sealed class Slot(long now)
    {
        internal long Generation = 1;
        internal long? DirtySince = now;
        internal long? Validated;
        internal long? Attempt;
        internal bool Failed;
        internal long? Audit;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private bool broad;
    private long broadGeneration;
    private long? broadSince;
    internal void Track(string id)
    {
        lock (sync)
        {
            if (slots.ContainsKey(id)) return;
            if (slots.Count >= 512) { InvalidateAll(); return; }
            slots.Add(id, new(clock.GetTimestamp()));
        }
    }
    internal void Retain(ISet<string> ids)
    {
        lock (sync)
        {
            foreach (var id in slots.Keys.Where(id => !ids.Contains(id)).ToArray()) slots.Remove(id);
            // Retired dirty sources cannot leave a broad Refresh pending after
            // every remaining consumer has incorporated its authoritative read.
            if (slots.Values.All(slot => slot.DirtySince is null))
            { broad = false; broadSince = null; }
        }
    }
    internal void InvalidateAll()
    {
        lock (sync)
        {
            broad = true; broadGeneration++; broadSince ??= clock.GetTimestamp();
            foreach (var slot in slots.Values) Dirty(slot);
        }
    }
    internal void InvalidateNames(IEnumerable<string> ids)
    { lock (sync) foreach (var id in ids) if (slots.TryGetValue(id, out var slot)) Dirty(slot); }
    internal void Invalidate(BeadsInvalidation hint)
    {
        lock (sync)
        {
            if (hint.IssueId is null || hint.Operation is not ("comment" or "create" or "update" or "close" or "delete" or "dep_add" or "dep_remove"))
            { InvalidateAll(); return; }
            foreach (var (id, slot) in slots)
                if (id == "inventory" || id == "comments:" + hint.IssueId || id == "merge:" + hint.IssueId
                    || hint.Operation != "comment" && id != "checkpoint" && !id.StartsWith("comments:", StringComparison.Ordinal)) Dirty(slot);
        }
    }
    private void Dirty(Slot slot) { slot.Generation++; slot.DirtySince ??= clock.GetTimestamp(); }
    internal bool IsCurrent(string id, long? generation)
    { lock (sync) return slots.TryGetValue(id, out var slot) && slot.DirtySince is null && slot.Generation == generation; }
    internal string Reason(string id)
    {
        lock (sync) return !slots.TryGetValue(id, out var slot) || slot.Validated is null ? "bootstrap"
            : slot.DirtySince is not null ? "dirty" : "audit";
    }
    internal bool Due(string id)
    {
        lock (sync)
        {
            Track(id);
            if (!slots.TryGetValue(id, out var slot)) return true;
            if (slot.Failed && slot.Attempt is { } attempt && clock.GetElapsedTime(attempt).TotalSeconds < 5) return false;
            return slot.DirtySince is not null || slot.Validated is null
                || slot.Audit is { } audit && clock.GetElapsedTime(audit).TotalSeconds >= Interval(id);
        }
    }
    internal bool HasDue { get { lock (sync) return slots.Keys.Any(Due); } }
    internal (long Source, long Broad) Begin(string id)
    {
        lock (sync)
        {
            Track(id);
            if (!slots.TryGetValue(id, out var slot)) return (0, broadGeneration);
            slot.Attempt = clock.GetTimestamp();
            return (slot.Generation, broadGeneration);
        }
    }
    internal void Complete(string id, (long Source, long Broad) generation, bool success)
    {
        lock (sync)
        {
            if (!slots.TryGetValue(id, out var slot)) return;
            slot.Failed = !success;
            if (!success) return;
            var now = clock.GetTimestamp();
            slot.Validated = now;
            // Audit deadlines do not move on every event, so a continuous burst
            // cannot postpone the independent safety net.
            if (slot.Audit is null || clock.GetElapsedTime(slot.Audit.Value).TotalSeconds >= Interval(id))
            {
                // Spread initial deadlines across the interval, all within 60s.
                var offset = id.StartsWith("environment:", StringComparison.Ordinal) ? 0 : slots.Keys.Order(StringComparer.Ordinal).ToList().IndexOf(id) % 30;
                var started = slot.Attempt ?? now;
                slot.Audit = slot.Audit is null ? started - (long)(offset * clock.TimestampFrequency) : started;
            }
            if (slot.Generation == generation.Source) slot.DirtySince = null;
            if (broad && generation.Broad == broadGeneration && slots.Values.All(item => item.DirtySince is null))
            { broad = false; broadSince = null; }
        }
    }
    internal double? PendingAge(IEnumerable<string> ids)
    {
        lock (sync)
        {
            var pending = ids.Where(slots.ContainsKey).Select(id => slots[id].DirtySince).Where(value => value.HasValue).ToList();
            if (broadSince is not null) pending.Add(broadSince);
            return pending.Count == 0 ? null : pending.Max(value => clock.GetElapsedTime(value!.Value).TotalMilliseconds);
        }
    }
    private static double Interval(string id) => id.StartsWith("environment:", StringComparison.Ordinal) ? 2 : 60;
    internal void Reset() { lock (sync) { slots.Clear(); broad = false; broadSince = null; } }
}
