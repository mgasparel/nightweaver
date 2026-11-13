using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Nonblocking exclusion between pool reservations and completed-environment removal.
/// A slow cleanup excludes only its Bead; unrelated admission never waits for filesystem IO.</summary>
internal sealed class EnvironmentReservations
{
    private readonly HashSet<string> active = new(RuntimePaths.Comparer);
    internal IDisposable? TryAcquire(string beadId)
    {
        lock (active) return active.Add(beadId) ? new Lease(this, beadId) : null;
    }

    private sealed class Lease(EnvironmentReservations owner, string beadId) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lock (owner.active) owner.active.Remove(beadId);
        }
    }
}
