namespace Nightweaver.Web;

// Guarded operations stay uninterrupted. Observers release after every native command.
internal sealed class TrackerAccessGate : IDisposable
{
    private readonly object sync = new();
    private readonly Queue<TaskCompletionSource> mutations = new();
    private readonly Queue<TaskCompletionSource> observations = new();
    private bool held;
    private bool disposed;
    private int consecutiveMutations;

    internal Task WaitAsync(CancellationToken token) => EnterAsync(false, token);
    internal Task WaitObserverAsync(CancellationToken token) => EnterAsync(true, token);

    private Task EnterAsync(bool observer, CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (!held)
            {
                held = true;
                consecutiveMutations = observer ? 0 : consecutiveMutations + 1;
                return Task.CompletedTask;
            }
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (observer ? observations : mutations).Enqueue(waiter);
            return WaitAsync(waiter, token);
        }
    }

    private static async Task WaitAsync(TaskCompletionSource waiter, CancellationToken token)
    {
        using var registration = token.Register(() => waiter.TrySetCanceled(token));
        await waiter.Task.ConfigureAwait(false);
    }

    internal void Release()
    {
        lock (sync)
        {
            while (true)
            {
                var observer = observations.Count > 0 && (mutations.Count == 0 || consecutiveMutations >= 8);
                var queue = observer ? observations : mutations;
                if (queue.Count == 0) { held = false; return; }
                if (!queue.Dequeue().TrySetResult()) continue;
                consecutiveMutations = observer ? 0 : consecutiveMutations + 1;
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (var waiter in mutations.Concat(observations)) waiter.TrySetCanceled();
            mutations.Clear();
            observations.Clear();
        }
    }
}
