using Nightweaver.Inspection;

namespace Nightweaver.Application;

internal interface IBeadsJournalClient
{
    Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken);

    Task<IReadOnlyList<long>> ReadBatchAsync(
        ValidatedTargetRepository repository,
        long afterSequence,
        int maxRecords,
        CancellationToken cancellationToken);
}

internal sealed class BeadsJournalSettlementException(string message, Exception innerException)
    : Exception(message, innerException);

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
    internal const int MaximumBatchRecords = 256;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
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

            var nextDelay = reconnectDelay;
            try
            {
                if (!journalEnabled)
                {
                    await client.EnableAsync(repository, cancellationToken).ConfigureAwait(false);
                    journalEnabled = true;
                }

                var batch = await client.ReadBatchAsync(repository, cursor, MaximumBatchRecords,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Validate the entire bounded result before committing any cursor progress.
                if (batch.Count > MaximumBatchRecords || batch.Any(sequence => sequence < 0))
                    throw new InvalidDataException("Invalid Beads journal batch.");
                if (batch.Count > 0)
                {
                    cursor = Math.Max(cursor, batch.Max());
                    requestDiscovery();
                }
                reconnectDelay = InitialReconnectDelay;
                nextDelay = PollInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BeadsJournalSettlementException)
            {
                // Uncertain children must never be replaced. The coordinator keeps ordinary discovery active.
                requestDiscovery();
                throw;
            }
            catch (BeadsJournalTruncatedException exception)
            {
                requestDiscovery();
                cursor = exception.Head;
                nextDelay = PollInterval;
                reconnectDelay = InitialReconnectDelay;
                error.WriteLine($"nightweaver: Target Repository events journal was truncated; resuming after reported head {cursor}.");
            }
            catch (Exception exception)
            {
                requestDiscovery();
                error.WriteLine($"nightweaver: Target Repository events batch failed; retrying: {exception.Message}");
            }

            await wait(nextDelay, cancellationToken).ConfigureAwait(false);
            if (nextDelay != PollInterval) reconnectDelay = TimeSpan.FromSeconds(Math.Min(
                reconnectDelay.TotalSeconds * 2,
                MaximumReconnectDelay.TotalSeconds));
        }
    }
}
