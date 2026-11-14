namespace Nightweaver.Web;

// Projection payloads and the separate history cache share a single byte budget.
// Reservation changes are atomic so source writes cannot race HTTP admission.
internal sealed class ObservationMemoryBudget
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<object, int> reservations = new(ReferenceEqualityComparer.Instance);
    private int used;
    internal int Used { get { lock (sync) return used; } }
    internal bool TryReplace(IReadOnlyList<object> remove, IReadOnlyList<(object Owner, int Bytes)> add)
    {
        lock (sync)
        {
            long next = used;
            foreach (var owner in remove.Distinct(ReferenceEqualityComparer.Instance)) next -= reservations.GetValueOrDefault(owner);
            foreach (var (owner, bytes) in add)
            {
                if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(add));
                if (!remove.Contains(owner, ReferenceEqualityComparer.Instance)) next -= reservations.GetValueOrDefault(owner);
                next += bytes;
            }
            if (next > MaximumBytes) return false;
            foreach (var owner in remove) reservations.Remove(owner);
            foreach (var (owner, bytes) in add) reservations[owner] = bytes;
            used = (int)next;
            return true;
        }
    }
    internal void Release(object owner)
    {
        lock (sync) if (reservations.Remove(owner, out var bytes)) used -= bytes;
    }
}
