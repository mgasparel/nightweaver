namespace Nightweaver.Tests;

// Only the role budget is virtual. Native startup/handshake and cleanup waits
// retain their real bounded deadlines, so a broken fixture cannot hang the test.
internal sealed class InvocationDeadlineClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<Deadline> timers = [];
    private long timestamp;
    private readonly TaskCompletionSource timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task TimerCreated => timerCreated.Task;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return timestamp; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, period);
        lock (gate)
        {
            var timer = new Deadline(callback, state, timestamp + dueTime.Ticks);
            timers.Add(timer);
            timerCreated.TrySetResult();
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        Deadline[] due;
        lock (gate)
        {
            timestamp += elapsed.Ticks;
            due = timers.Where(timer => timer.Due <= timestamp).ToArray();
            timers.RemoveAll(timer => timer.Due <= timestamp);
        }
        foreach (var timer in due) timer.Expire();
    }

    private sealed class Deadline(TimerCallback callback, object? state, long due) : ITimer
    {
        private int disposed;
        internal long Due => due;
        internal void Expire() { if (Interlocked.Exchange(ref disposed, 1) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
