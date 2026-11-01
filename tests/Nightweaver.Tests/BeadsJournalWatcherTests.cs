using Nightweaver.Application;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class BeadsJournalWatcherTests
{
    private static readonly ValidatedTargetRepository Repository = new("target repository", "main");
    private static readonly TimeSpan[] ExpectedReconnectDelays =
        new[] { 1, 2, 4, 8, 16, 30, 30, 1 }.Select(seconds => TimeSpan.FromSeconds(seconds)).ToArray();

    [Fact]
    public async Task ReconnectBackoffCapsAtThirtySecondsAndResetsAfterJournalActivity()
    {
        using var lifetime = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var cursors = new List<long>();
        var requests = 0;
        var client = new FakeJournalClient((attempt, cursor, onEvent, token) =>
        {
            cursors.Add(cursor);
            if (attempt < 8) throw new IOException($"interrupted tail {attempt}");
            onEvent(5);
            return Task.CompletedTask;
        });
        var watcher = new BeadsJournalWatcher(client, () => requests++, TextWriter.Null, (delay, _) =>
        {
            delays.Add(delay);
            if (delays.Count == 8) lifetime.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.RunAsync(Repository, lifetime.Token));

        Assert.Equal(1, client.EnableAttempts);
        Assert.Equal(8, cursors.Count);
        Assert.All(cursors, cursor => Assert.Equal(0, cursor));
        Assert.Equal(ExpectedReconnectDelays, delays);
        Assert.Equal(9, requests);
    }

    [Fact]
    public async Task TruncationDiscardsOldCursorAndDuplicateOrOldRecordsRemainWakeups()
    {
        using var lifetime = new CancellationTokenSource();
        var cursors = new List<long>();
        var observed = new List<long>();
        var requests = 0;
        var client = new FakeJournalClient(FakeFollowAsync);
        var watcher = new BeadsJournalWatcher(client, () => requests++, TextWriter.Null,
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.RunAsync(Repository, lifetime.Token));

        Assert.Equal([0L, 42L, 43L], cursors);
        Assert.Equal([43L, 43L, 41L], observed);
        Assert.Equal(5, requests);

        Task FakeFollowAsync(int attempt, long cursor, Action<long> onEvent, CancellationToken token)
        {
            cursors.Add(cursor);
            if (attempt == 2)
            {
                onEvent(43);
                observed.Add(43);
                onEvent(43);
                observed.Add(43);
                onEvent(41);
                observed.Add(41);
                return Task.CompletedTask;
            }

            if (attempt == 1) throw new BeadsJournalTruncatedException(42);
            lifetime.Cancel();
            throw new OperationCanceledException(token);
        }
    }

    private sealed class FakeJournalClient(
        Func<int, long, Action<long>, CancellationToken, Task> follow) : IBeadsJournalClient
    {
        internal int EnableAttempts { get; private set; }
        private int followAttempts;

        public Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
        {
            EnableAttempts++;
            return Task.CompletedTask;
        }

        public Task FollowAsync(ValidatedTargetRepository repository, long afterSequence,
            Action<long> onEvent, CancellationToken cancellationToken)
            => follow(++followAttempts, afterSequence, onEvent, cancellationToken);
    }
}
