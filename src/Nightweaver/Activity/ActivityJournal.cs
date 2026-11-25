using System.Threading.Channels;
using Nightweaver.Inspection;

namespace Nightweaver.ActivityFeed;

internal sealed record ActivityJournalHealth(bool Available, ActivityReason Reason, long? Lost);
internal sealed record ActivityJournalStart(Guid Journal, long NextOffset, bool Gap, bool Replaced,
    ActivityReason GapReason = ActivityReason.None);

// A storage operation can fail or ignore cancellation. Only the worker calls it.
// Disposal never releases its lease while an operation is still in flight.
internal interface IActivityJournalStorage : IDisposable
{
    Task<ActivityJournalStart> StartAsync(Guid runtime, CancellationToken cancellationToken);
    Task AppendAsync(ActivityRecord record, CancellationToken cancellationToken);
}

internal sealed class ActivityJournal : IAsyncDisposable
{
    private sealed record Submission(ActivityEvidence Evidence, ActivityMergeProof? Proof, DateTimeOffset Time);
    private readonly Channel<Submission> queue = Channel.CreateBounded<Submission>(new BoundedChannelOptions(ActivityLimits.ProducerQueue)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource stop = new();
    private readonly IActivityJournalStorage storage;
    private readonly GitHubRepositoryIdentity repository;
    private readonly Guid runtime;
    private readonly TimeProvider clock;
    private readonly Task worker;
    private ActivityJournalHealth health = new(false, ActivityReason.RuntimeStarting, 0);
    private long pendingLoss;
    private long totalLoss;
    private int unknownLoss;
    private int closing;

    internal ActivityJournal(IActivityJournalStorage storage, GitHubRepositoryIdentity repository,
        Guid runtime, TimeProvider? clock = null)
    {
        if (runtime == Guid.Empty) throw new ArgumentException("Runtime identity is required.", nameof(runtime));
        this.storage = storage; this.repository = repository; this.runtime = runtime;
        this.clock = clock ?? TimeProvider.System;
        worker = Task.Run(RunAsync);
    }

    internal ActivityJournalHealth Health => Volatile.Read(ref health) with
    { Lost = Volatile.Read(ref unknownLoss) != 0 ? null : Interlocked.Read(ref totalLoss) };

    // No serialization, disk access, async wait or viewer work on the caller path.
    internal bool TrySubmit(ActivityEvidence evidence, ActivityMergeProof? proof = null)
    {
        if (Volatile.Read(ref closing) != 0) return false;
        if (!Bounded(evidence, proof)) { Lose(); return false; }
        if (!queue.Writer.TryWrite(new(evidence, proof, clock.GetUtcNow())))
        { Lose(); return false; }
        return true;
    }

    private static bool Bounded(ActivityEvidence e, ActivityMergeProof? proof) =>
        e.SourceReference is { Length: <= ActivityLimits.IdentifierCharacters }
        && (e.Bead is null || e.Bead.Length <= ActivityLimits.IdentifierCharacters)
        && (e.Invocation is null || e.Invocation.Length <= ActivityLimits.IdentifierCharacters)
        && e.Merge is null && (proof is null || FactsBounded(proof.Report) && FactsBounded(proof.Confirmation));
    private static bool FactsBounded(MergerFacts f) => f.Bead is { Length: <= 128 }
        && f.PullRequest is { Length: <= 512 } && f.CheckedHead is { Length: 40 } && f.MergeCommit is { Length: 40 }
        && f.ReportReference is { Length: <= 128 } && f.VerificationReference is { Length: <= 128 };

    private void Lose(long count = 1)
    { Saturate(ref pendingLoss, count); Saturate(ref totalLoss, count); }
    private static void Saturate(ref long target, long count)
    {
        long old, next;
        do { old = Interlocked.Read(ref target); next = old > long.MaxValue - count ? long.MaxValue : old + count; }
        while (Interlocked.CompareExchange(ref target, next, old) != old);
    }

    private async Task RunAsync()
    {
        try
        {
            var start = await storage.StartAsync(runtime, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            var offset = start.NextOffset;
            // Journal notices have their own sequence. Producer sequences are assigned
            // below in worker order; queue drops are counted independently.
            long writtenSequence = 0;
            async Task Notice(ActivityKind kind, ActivityReason reason, int? count = null)
            {
                if (offset == long.MaxValue || writtenSequence == long.MaxValue)
                    throw new InvalidDataException("Activity counters are exhausted.");
                var e = new ActivityEvidence(kind, ActivitySource.Journal, ActivityPhase.Runtime, Guid.NewGuid(), "activity-journal")
                { Reason = reason, Count = count, Freshness = ActivityFreshness.Fresh };
                var r = ActivityProjector.Project(new(start.Journal, offset++, runtime, ++writtenSequence), clock.GetUtcNow(), e, repository)!;
                await storage.AppendAsync(r, stop.Token).ConfigureAwait(false);
            }
            if (start.Gap) await Notice(ActivityKind.HistoryGap, start.GapReason).ConfigureAwait(false);
            if (start.Replaced) await Notice(ActivityKind.RuntimeReplaced, ActivityReason.Replaced).ConfigureAwait(false);
            Volatile.Write(ref health, new(true, ActivityReason.None, 0));
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var loss = Interlocked.Exchange(ref pendingLoss, 0);
                    if (loss > 0)
                    {
                        try { await Notice(ActivityKind.RecordsLost, ActivityReason.None,
                            loss > 1000000000 ? null : (int)loss).ConfigureAwait(false); }
                        catch { Saturate(ref pendingLoss, loss); throw; }
                    }
                    if (offset == long.MaxValue || writtenSequence == long.MaxValue)
                        throw new InvalidDataException("Activity counters are exhausted.");
                    var record = ActivityProjector.Project(new(start.Journal, offset, runtime, ++writtenSequence),
                        item.Time, item.Evidence, repository, item.Proof);
                    if (record is null) { Lose(); continue; }
                    await storage.AppendAsync(record, stop.Token).ConfigureAwait(false);
                    offset++;
                    Volatile.Write(ref health, new(true, ActivityReason.None, 0));
                }
                catch (Exception e) when (StorageFailure(e))
                {
                    Volatile.Write(ref unknownLoss, 1);
                    Lose(); Volatile.Write(ref health, new(false, ActivityReason.DiskFailure, 0));
                    // An uncertain write must be recovered by a new runtime. Do not
                    // append behind a torn record or reuse its position.
                    queue.Writer.TryComplete();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) when (StorageFailure(e))
        {
            Volatile.Write(ref unknownLoss, 1);
            Volatile.Write(ref health, new(false, ActivityReason.DiskFailure, 0)); queue.Writer.TryComplete();
        }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Lose();
            try { storage.Dispose(); }
            catch (Exception e) when (StorageFailure(e))
            { Volatile.Write(ref unknownLoss, 1); Volatile.Write(ref health, new(false, ActivityReason.DiskFailure, 0)); }
        }
    }

    internal static bool StorageFailure(Exception e) => e is IOException or UnauthorizedAccessException
        or InvalidDataException or ArgumentException or NotSupportedException or System.Text.Json.JsonException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        queue.Writer.TryComplete();
        try { await worker.WaitAsync(ActivityLimits.WriteDeadline).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Volatile.Write(ref unknownLoss, 1);
            stop.Cancel(); Volatile.Write(ref health, new(false, ActivityReason.DiskFailure, 0));
        }
        // A blocked worker retains the lease and its bounded queue until it settles.
    }
}
