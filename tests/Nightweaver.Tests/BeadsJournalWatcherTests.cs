using Nightweaver.Application;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class BeadsJournalWatcherTests
{
    private static readonly int[] ExpectedBackoffSeconds = [1, 2, 4, 8, 16, 30, 30, 30, 1, 2];
    private static readonly ValidatedTargetRepository Repository = new("target repository", "main");

    [Fact]
    public async Task HistoryDrainsOneBoundedPagePerTickAndEmptyBatchesAlsoWait()
    {
        var history = Enumerable.Range(1, 700).Select(value => (long)value).ToArray();
        var cursors = new List<long>();
        var requests = 0;
        var client = new FakeJournalClient((_, cursor, maximum, _) =>
        {
            cursors.Add(cursor);
            return Task.FromResult<IReadOnlyList<long>>(history.Where(value => value > cursor).Take(maximum).ToArray());
        });
        var delays = await RunTicksAsync(client, 4, () => requests++);
        Assert.Equal([0L, 256L, 512L, 700L], cursors);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromSeconds(30), delay));
        Assert.Equal(3, requests);
        Assert.Equal(1, client.EnableAttempts);
    }

    [Fact]
    public async Task FailureBackoffCapsAndSuccessfulEmptyBatchResetsIt()
    {
        var client = new FakeJournalClient((attempt, _, _, _) => attempt == 8
            ? Task.FromResult<IReadOnlyList<long>>([]) : throw new IOException("failed batch"));
        var delays = await RunTicksAsync(client, 10);
        Assert.Equal(ExpectedBackoffSeconds.Select(seconds => TimeSpan.FromSeconds(seconds)), delays);
    }

    [Fact]
    public async Task TruncationResetsCursorAndOldOrDuplicateRecordsOnlyCoalesceWakeups()
    {
        var cursors = new List<long>();
        var requests = 0;
        var client = new FakeJournalClient((attempt, cursor, _, _) =>
        {
            cursors.Add(cursor);
            return attempt switch
            {
                1 => Task.FromResult<IReadOnlyList<long>>([90]),
                2 => throw new BeadsJournalTruncatedException(42),
                3 => Task.FromResult<IReadOnlyList<long>>([43, 43, 41]),
                _ => Task.FromResult<IReadOnlyList<long>>([41])
            };
        });
        await RunTicksAsync(client, 4, () => requests++);
        Assert.Equal([0L, 90L, 42L, 43L], cursors);
        Assert.Equal(4, requests);
    }

    [Fact]
    public async Task InvalidOrFailedBatchesCannotCommitAPartialCursor()
    {
        var cursors = new List<long>();
        var client = new FakeJournalClient((attempt, cursor, _, _) =>
        {
            cursors.Add(cursor);
            return attempt switch
            {
                1 => Task.FromResult<IReadOnlyList<long>>([4]),
                2 => Task.FromResult<IReadOnlyList<long>>([100, -1]),
                3 => throw new InvalidDataException("partial read failed"),
                4 => Task.FromResult<IReadOnlyList<long>>(Enumerable.Repeat(100L, 257).ToArray()),
                _ => Task.FromResult<IReadOnlyList<long>>([])
            };
        });
        await RunTicksAsync(client, 5);
        Assert.Equal([0L, 4L, 4L, 4L, 4L], cursors);
    }

    [Fact]
    public async Task EnableFailureWaitsBeforeRetryAndDoesNotStartABatch()
    {
        var reads = 0;
        var client = new FakeJournalClient((_, _, _, _) => { reads++; return Task.FromResult<IReadOnlyList<long>>([]); })
        { FailFirstEnable = true };
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)], await RunTicksAsync(client, 2));
        Assert.Equal(2, client.EnableAttempts);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task EachRepositorySessionRestartsAtZero()
    {
        var cursors = new List<long>();
        var client = new FakeJournalClient((_, cursor, _, _) =>
        { cursors.Add(cursor); return Task.FromResult<IReadOnlyList<long>>([99]); });
        await RunTicksAsync(client, 1);
        await RunTicksAsync(client, 1, repository: new("replacement repository", "other"));
        Assert.Equal([0L, 0L], cursors);
    }

    [Fact]
    public async Task CancellationDuringReadDoesNotPublishAPartialBatchOrWait()
    {
        using var lifetime = new CancellationTokenSource();
        var client = new FakeJournalClient((_, _, _, token) =>
        { lifetime.Cancel(); return Task.FromCanceled<IReadOnlyList<long>>(token); });
        var watcher = new BeadsJournalWatcher(client, () => Assert.Fail("canceled batch published"), TextWriter.Null,
            (_, _) => { Assert.Fail("wait after canceled read"); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.RunAsync(Repository, lifetime.Token));
    }

    [Fact]
    public async Task CancellationDuringDelayStopsBeforeAnotherBatch()
    {
        using var lifetime = new CancellationTokenSource();
        var reads = 0;
        var client = new FakeJournalClient((_, _, _, _) => { reads++; return Task.FromResult<IReadOnlyList<long>>([]); });
        var watcher = new BeadsJournalWatcher(client, () => { }, TextWriter.Null, (_, token) =>
        { lifetime.Cancel(); return Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.RunAsync(Repository, lifetime.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task UncertainSettlementStopsWithoutReplacement()
    {
        var client = new FakeJournalClient((_, _, _, _) => throw new BeadsJournalSettlementException("uncertain", new TimeoutException()));
        var watcher = new BeadsJournalWatcher(client, () => { }, TextWriter.Null,
            (_, _) => { Assert.Fail("replacement delay after uncertain settlement"); return Task.CompletedTask; });
        await Assert.ThrowsAsync<BeadsJournalSettlementException>(() => watcher.RunAsync(Repository, TestContext.Current.CancellationToken));
    }

    private static async Task<List<TimeSpan>> RunTicksAsync(FakeJournalClient client, int ticks,
        Action? request = null, ValidatedTargetRepository? repository = null)
    {
        using var lifetime = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var watcher = new BeadsJournalWatcher(client, request ?? (() => { }), TextWriter.Null, (delay, _) =>
        {
            delays.Add(delay);
            if (delays.Count == ticks) lifetime.Cancel();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.RunAsync(repository ?? Repository, lifetime.Token));
        return delays;
    }

    private sealed class FakeJournalClient(
        Func<int, long, int, CancellationToken, Task<IReadOnlyList<long>>> read) : IBeadsJournalClient
    {
        internal bool FailFirstEnable { get; init; }
        internal int EnableAttempts { get; private set; }
        private int attempts;
        public Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
        {
            if (++EnableAttempts == 1 && FailFirstEnable) throw new IOException("setup failed");
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<long>> ReadBatchAsync(ValidatedTargetRepository repository, long afterSequence,
            int maxRecords, CancellationToken cancellationToken) => read(++attempts, afterSequence, maxRecords, cancellationToken);
    }
}
