using System.Threading.Channels;
using System.Collections.Concurrent;
using Nightweaver.Planning;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal interface ICompletedEnvironmentQueue
{
    Task EnqueueAsync(AgentStageSession session, MergerVerifiedMergeEvidence evidence, CancellationToken token);
}

/// <summary>One serialized housekeeping owner. Wakes coalesce with discovery hints; removal
/// runs outside admission and pool capacity, under per-Bead lifetime exclusion.</summary>
internal sealed class CompletedEnvironmentWorker(HostPaths paths, ProjectConfig project,
    ValidatedTargetRepository repository, IProcessRunner runner, EnvironmentReservations reservations,
    ContinuousStopGate stopGate, string runtimeId, Action<string> report, TimeProvider? timeProvider = null)
    : ICompletedEnvironmentQueue, IDisposable
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Channel<bool> wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CompletedEnvironmentCleanup cleanup = new(new BeadsReader(runner), runner, timeProvider);
    private readonly CompletedEnvironmentStore store = new(paths, project.Name, repository);
    private readonly CleanupRetryInbox inbox = new(paths, project, repository);
    private readonly Dictionary<string, (AgentStageSession Session, MergerVerifiedMergeEvidence Evidence)> branchCompletions = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private CleanupRetryAcceptance? acceptance;

    public void Dispose()
    {
        Interlocked.Exchange(ref acceptance, null)?.Dispose();
        serial.Dispose();
    }

    internal void Wake() { if (!stopGate.IsStopRequested) wakeups.Writer.TryWrite(true); }

    internal async Task InitializeAsync(CancellationToken token)
    {
        // Once-only inventory reconciliation recovers closure-before-enqueue. No removal on startup.
        var diagnostics = await cleanup.ReconcileAsync(paths, project.Name, repository, project.RecoveryActor, token,
            discoverOnly: true).ConfigureAwait(false);
        foreach (var diagnostic in diagnostics) report($"Cleanup retained '{diagnostic.BeadId}': {diagnostic.Reason}");
        foreach (var request in inbox.Read().Where(request => request.Outcome == "queued" && request.RuntimeId != runtimeId))
        {
            var consumed = store.Read().Obligations.Any(record => record.LastRetryRequestId == request.RequestId);
            await inbox.WriteAsync(request with { Outcome = consumed ? "accepted" : "expired" }, token).ConfigureAwait(false);
        }
        foreach (var legacy in store.Read().Obligations.Where(record => record.CycleAttempts is null
                     && record.Status == CleanupStatus.NeedsAttention && record.Attempts is > 0 and <= 4
                     && record.Diagnostic is not null && CleanupRetryPolicy.IsTransientGitFailure(record.Diagnostic)))
            await store.WriteAsync(CleanupRetryPolicy.Fail(legacy with { CycleAttempts = legacy.Attempts },
                legacy.Diagnostic!, true, legacy.LastAttempt!.Value), token).ConfigureAwait(false);
        await SettleInterruptedAttemptsAsync(token).ConfigureAwait(false);
        foreach (var record in store.Read().Obligations.Where(record => record.Status == CleanupStatus.NeedsAttention))
            report($"Cleanup needs attention for '{record.BeadId}' at '{record.Worktree.Path}': {record.Diagnostic}");
        acceptance = new CleanupRetryAcceptance(paths, project, runtimeId, stopGate);
    }

    private readonly ConcurrentDictionary<string, (AgentStageSession Session, MergerVerifiedMergeEvidence Evidence)> completions = new(StringComparer.Ordinal);

    // Only under serialization after the owned reconciliation has returned. Never replay an
    // uncertain removal; preserve its budget and require guarded operator inspection/Retry.
    private async Task SettleInterruptedAttemptsAsync(CancellationToken token)
    {
        foreach (var record in store.Read().Obligations.Where(CleanupRetryPolicy.IsInterrupted))
            await store.WriteAsync(CleanupRetryPolicy.Interrupted(record, clock.GetUtcNow()), token).ConfigureAwait(false);
    }

    public Task EnqueueAsync(AgentStageSession session, MergerVerifiedMergeEvidence evidence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        completions.TryAdd(session.BeadId, (session, evidence));
        Wake();
        return Task.CompletedTask;
    }

    // Called after pool drain as well: graceful stop persists completed work without starting removal.
    internal async Task FlushCompletionsAsync(CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try { await PersistCompletionsAsync(token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }

    private async Task PersistCompletionsAsync(CancellationToken token)
    {
        foreach (var item in completions.ToArray())
        {
            await cleanup.ReconcileAsync(paths, project.Name, repository, project.RecoveryActor, token,
                item.Key, item.Value.Evidence, discoverOnly: true).ConfigureAwait(false);
            var record = store.Read().Obligations.Single(record => record.BeadId == item.Key);
            if (record.Status == CleanupStatus.NeedsAttention)
                report($"Cleanup needs attention for '{record.BeadId}': {record.Diagnostic}");
            else
                branchCompletions.TryAdd(item.Key, item.Value);
            completions.TryRemove(item.Key, out _);
        }
    }

    internal async Task RunAsync(CancellationToken token)
    {
        Wake();
        await foreach (var _ in wakeups.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (stopGate.IsStopRequested) return;
            try { await RunBatchAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                report($"Cleanup unavailable; durable obligations retained for inspection and a later wake: {exception.Message}");
            }
        }
    }

    internal async Task RunBatchAsync(CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await SettleInterruptedAttemptsAsync(token).ConfigureAwait(false);
            await PersistCompletionsAsync(token).ConfigureAwait(false);
            foreach (var request in inbox.Read().Where(request => request.Outcome == "queued").Take(8))
            {
                if (stopGate.IsStopRequested) return;
                var record = store.Read().Obligations.SingleOrDefault(item => item.BeadId == request.BeadId);
                if (record?.LastRetryRequestId == request.RequestId)
                {
                    await inbox.WriteAsync(request with { Outcome = "accepted" }, token).ConfigureAwait(false);
                    continue;
                }
                var outcome = "stale";
                if (request.RuntimeId == runtimeId && record is not null
                    && record.Status == CleanupStatus.NeedsAttention && CleanupRetryInbox.Revision(record) == request.Revision)
                {
                    using var lease = reservations.TryAcquire(record.BeadId);
                    var inventory = await new BeadsReader(runner).ReadSnapshotAsync(repository, token).ConfigureAwait(false);
                    if (lease is not null && inventory.ById.TryGetValue(record.BeadId, out var bead)
                        && StartupEnvironmentClassifier.IsCompletedTmpOwner(bead, project.RecoveryActor))
                    {
                        // Commit the request ID with the new cycle before acknowledging. If
                        // acknowledgement is interrupted, replay observes this ID without rearming.
                        if (!stopGate.TryStart(() => store.WriteAsync(record with { Status = CleanupStatus.Pending,
                                CycleAttempts = 0, NextAttempt = null, LastRetryRequestId = request.RequestId }, token),
                                out var acceptance)) return;
                        await acceptance.ConfigureAwait(false);
                        await inbox.WriteAsync(request with { Outcome = "accepted" }, token).ConfigureAwait(false);
                        continue;
                    }
                    outcome = "protected";
                }
                await inbox.WriteAsync(request with { Outcome = outcome }, token).ConfigureAwait(false);
            }
            inbox.TrimAcknowledged();
            var due = store.Read().Obligations.Where(record => CleanupRetryPolicy.IsDue(record, clock.GetUtcNow()))
                .OrderBy(record => record.NextAttempt ?? DateTimeOffset.MinValue).ThenBy(record => record.BeadId, StringComparer.Ordinal)
                .Take(8).ToArray();
            foreach (var record in due)
            {
                token.ThrowIfCancellationRequested();
                if (!stopGate.TryStart(() => reservations.TryAcquire(record.BeadId), out var lease)) return;
                using (lease)
                {
                    if (lease is null) continue;
                    var diagnostics = await cleanup.ReconcileAsync(paths, project.Name, repository, project.RecoveryActor,
                        token, record.BeadId).ConfigureAwait(false);
                    foreach (var diagnostic in diagnostics) report($"Cleanup retained '{diagnostic.BeadId}': {diagnostic.Reason}");
                    // Preserve completion's existing one-shot ref cleanup. Startup/timer/manual
                    // retries never gain branch or Product completion authority.
                    if (!stopGate.IsStopRequested && branchCompletions.Remove(record.BeadId, out var completion))
                    {
                        try
                        {
                            await new MergerCompletionFinalizer(runner).CleanupBranchesAsync(completion.Session,
                                completion.Evidence, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            var message = $"Verified completion one-shot branch cleanup retained '{record.BeadId}': {exception.Message}";
                            report(message);
                            await new BeadsWriter(runner).AddCommentAsync(repository, record.BeadId,
                                message.Length <= 1024 ? message : message[..1024], token).ConfigureAwait(false);
                        }
                    }
                }
            }
            // Reserved environments wait for the existing completion/timer hint instead of spinning.
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Required observations can fail after Begin or even after removal. Persist
            // honest uncertainty while the owner is still live, then report the failure.
            // If persistence itself fails, the next wake retries this settlement first.
            await SettleInterruptedAttemptsAsync(token).ConfigureAwait(false);
            throw;
        }
        finally { serial.Release(); }
    }
}
