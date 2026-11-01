using Nightweaver.Inspection;

namespace Nightweaver.Application;

internal interface IBeadsJournalClient
{
    Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken);

    Task FollowAsync(
        ValidatedTargetRepository repository,
        long afterSequence,
        Action<long> onEvent,
        CancellationToken cancellationToken);
}

internal sealed class BeadsJournalTruncatedException(long head)
    : Exception($"The Beads events journal was truncated at head {head}.")
{
    internal long Head { get; } = head >= 0
        ? head
        : throw new ArgumentOutOfRangeException(nameof(head));
}

/// <summary>Uses tracker journal records as best-effort hints for the existing discovery path.</summary>
internal sealed class BeadsJournalWatcher(
    IBeadsJournalClient client,
    Action requestDiscovery,
    TextWriter error,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
{
    internal static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);

    internal async Task RunAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        long cursor = 0;
        var reconnectDelay = InitialReconnectDelay;
        var journalEnabled = false;
        Func<TimeSpan, CancellationToken, Task> wait = delayAsync
            ?? ((delay, token) => Task.Delay(delay, token));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!journalEnabled)
            {
                try
                {
                    await client.EnableAsync(repository, cancellationToken).ConfigureAwait(false);
                    journalEnabled = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    requestDiscovery();
                    error.WriteLine($"nightweaver: could not enable the Target Repository events journal; ordinary discovery remains active: {exception.Message}");
                }
            }

            try
            {
                await client.FollowAsync(repository, cursor, sequence =>
                {
                    if (sequence > cursor) cursor = sequence;
                    reconnectDelay = InitialReconnectDelay;
                    requestDiscovery();
                }, cancellationToken).ConfigureAwait(false);

                requestDiscovery();
                error.WriteLine("nightweaver: Target Repository events tail ended unexpectedly; reconnecting.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BeadsJournalTruncatedException exception)
            {
                requestDiscovery();
                cursor = exception.Head;
                error.WriteLine($"nightweaver: Target Repository events journal was truncated; resuming after reported head {cursor}.");
            }
            catch (Exception exception)
            {
                requestDiscovery();
                error.WriteLine($"nightweaver: Target Repository events tail failed; reconnecting: {exception.Message}");
            }

            await wait(reconnectDelay, cancellationToken).ConfigureAwait(false);
            reconnectDelay = TimeSpan.FromSeconds(Math.Min(
                reconnectDelay.TotalSeconds * 2,
                MaximumReconnectDelay.TotalSeconds));
        }
    }
}
