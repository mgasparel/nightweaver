using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nightweaver.Execution;

namespace Nightweaver.Application;

internal delegate IAsyncEnumerable<bool> DiscoveryTimerTicks(CancellationToken cancellationToken);

/// <summary>Coalesces startup, timer, and released-slot signals into one serial discovery reader.</summary>
internal sealed class ContinuousDiscoveryCoordinator(
    IReadOnlyList<ContinuousPoolRegistration> configuredPools,
    Func<CancellationToken, Task> validateAdmissionInfrastructure,
    TextWriter output,
    TextWriter error,
    DiscoveryTimerTicks? timerTicks = null,
    Func<Action, CancellationToken, Task>? journalWatcher = null,
    ContinuousStopGate? stopGate = null)
{
    internal static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<ContinuousPoolRegistration> pools =
        ContinuousPoolRegistration.Order(configuredPools);
    private readonly ContinuousStopGate? stopGate = stopGate;
    private int drainFailureObserved;

    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    internal bool RequestDiscovery() => stopGate?.IsStopRequested != true && requests.Writer.TryWrite(true);

    internal async Task<bool> RunAsync(CancellationToken cancellationToken, CancellationToken forceStopToken = default)
    {
        using var stopRequest = cancellationToken.Register(() => stopGate?.RequestStop());
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var completionReaderCancellation = new CancellationTokenSource();
        var worker = DiscoveryLoopAsync(runtimeCancellation.Token);
        var timer = TimerLoopAsync(runtimeCancellation.Token);
        var poolCompletionReaders = pools
            .Select(pool => ReadPoolCompletionsAsync(pool, completionReaderCancellation.Token))
            .ToArray();
        var completionMonitor = MonitorPoolCompletionReadersAsync(poolCompletionReaders, completionReaderCancellation.Token);
        var drained = true;
        var forceCancelFailed = 0;
        using var forceCancellation = forceStopToken.Register(() =>
        {
            foreach (var pool in pools)
            {
                try { pool.Cancel(); }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref forceCancelFailed, 1);
                    error.WriteLine($"nightweaver: {pool.Kind} force stop could not cancel local work: {exception.Message}");
                }
            }
        });
        RequestDiscovery();
        var journalTask = journalWatcher is null
            ? Task.CompletedTask
            : ObserveJournalWatcherAsync(journalWatcher, runtimeCancellation.Token);

        try
        {
            var first = await Task.WhenAny(worker, timer, completionMonitor,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                await first.ConfigureAwait(false);
                throw new InvalidOperationException("Continuous Delivery runtime infrastructure stopped unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // This closes reservation and native-launch boundaries before helper cancellation.
            stopGate?.RequestStop();
            runtimeCancellation.Cancel();
            try
            {
                await Task.WhenAll(worker, timer, journalTask)
                    .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                if (exception is TimeoutException)
                {
                    drained = false;
                    error.WriteLine("nightweaver: runtime helper tasks did not stop within 30 seconds.");
                }
            }
            catch (Exception exception)
            {
                error.WriteLine($"nightweaver: runtime helper task failed during shutdown: {exception.Message}");
            }

            if (forceStopToken.IsCancellationRequested)
                drained = false;

            var drainTasks = pools.Select(async pool =>
            {
                try
                {
                    await pool.Drain(forceStopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (forceStopToken.IsCancellationRequested)
                {
                    drained = false;
                    error.WriteLine($"nightweaver: {pool.Kind} drain was interrupted by a subsequent stop signal.");
                }
                catch (Exception exception)
                {
                    drained = false;
                    error.WriteLine($"nightweaver: {pool.Kind} shutdown could not confirm local work stopped: {exception.Message}");
                }
            }).ToArray();
            await Task.WhenAll(drainTasks).ConfigureAwait(false);

            var accountingTasks = pools
                .Where(pool => pool.WaitForCompletionAccountingAsync is not null)
                .Select(async pool =>
                {
                    try
                    {
                        await pool.WaitForCompletionAccountingAsync!(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        drained = false;
                        error.WriteLine($"nightweaver: {pool.Kind} completion accounting did not finish after drain: {exception.Message}");
                    }
                }).ToArray();
            await Task.WhenAll(accountingTasks).ConfigureAwait(false);

            completionReaderCancellation.Cancel();
            try
            {
                await Task.WhenAll(poolCompletionReaders.Append(completionMonitor))
                    .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                drained = false;
                error.WriteLine("nightweaver: completion readers did not stop within 30 seconds after slot drain.");
            }
            catch (Exception exception)
            {
                drained = false;
                error.WriteLine($"nightweaver: completion reader failed during drain: {exception.Message}");
            }

            if (Volatile.Read(ref drainFailureObserved) != 0 || Volatile.Read(ref forceCancelFailed) != 0)
                drained = false;
            output.WriteLine("Continuous Delivery runtime stopped.");
        }
        return drained;
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var _ in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Shared runtime roots are infrastructure. A broken root ends admission and shutdowns
            // active work; ordinary fresh tracker reads below are retryable on a later trigger.
            await validateAdmissionInfrastructure(cancellationToken).ConfigureAwait(false);
            foreach (var pool in pools)
            {
                try
                {
                    var diagnostics = await pool.EvaluateAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var diagnostic in diagnostics)
                        error.WriteLine($"nightweaver: {pool.Kind} skipped '{diagnostic.BeadId}' ({diagnostic.Code}): {diagnostic.Message}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (exception is not ToolExecutionException)
                        throw;
                    error.WriteLine($"nightweaver: {pool.Kind} discovery failed; a later trigger will reread current tracker state: {exception.Message}");
                }
            }
        }
    }

    private async Task TimerLoopAsync(CancellationToken cancellationToken)
    {
        var ticks = timerTicks ?? ReadTimerTicksAsync;
        await foreach (var _ in ticks(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            RequestDiscovery();
    }

    private async Task ReadPoolCompletionsAsync(
        ContinuousPoolRegistration pool,
        CancellationToken cancellationToken)
    {
        await foreach (var completion in pool.Completions.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (completion.IsAccountingBarrier)
            {
                completion.AccountingBarrier!.TrySetResult(true);
                continue;
            }

            if (IsDrainFailure(completion))
                Interlocked.Exchange(ref drainFailureObserved, 1);
            var result = completion.Exception is not null
                ? $"failed: {completion.Exception.Message}"
                : completion.IsCanceled
                    ? "canceled"
                    : completion.Outcome?.Disposition.ToString() ?? "ended without an outcome";
            output.WriteLine($"{pool.Kind} slot '{completion.BeadId}' released local capacity ({result}).");
            RequestDiscovery();
        }
    }

    private static async Task MonitorPoolCompletionReadersAsync(Task[] readers)
        => await MonitorPoolCompletionReadersAsync(readers, CancellationToken.None).ConfigureAwait(false);

    private static async Task MonitorPoolCompletionReadersAsync(Task[] readers, CancellationToken cancellationToken)
    {
        var first = await Task.WhenAny(readers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await first.ConfigureAwait(false);
        throw new InvalidOperationException("A configured pool completion stream stopped unexpectedly.");
    }

    private static bool IsDrainFailure(DeliveryPoolCompletion completion) =>
        completion.Exception is not null
        || completion.IsCanceled
        || completion.Outcome?.Disposition is DeliverySlotDisposition.Failed or DeliverySlotDisposition.IncompletePauseDisposition
        || completion.Outcome?.Disposition == DeliverySlotDisposition.Escalated && !completion.ClaimHandoffCompleted
        || completion.Outcome?.Disposition == DeliverySlotDisposition.PausedForProductDecision && !completion.ClaimHandoffCompleted;

    private async Task ObserveJournalWatcherAsync(
        Func<Action, CancellationToken, Task> journalWatcher,
        CancellationToken cancellationToken)
    {
        try
        {
            await journalWatcher(() => RequestDiscovery(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RequestDiscovery();
            error.WriteLine($"nightweaver: tracker journal watcher stopped; periodic and completion discovery remain active: {exception.Message}");
        }
    }

    private static async IAsyncEnumerable<bool> ReadTimerTicksAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DiscoveryInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            yield return true;
    }
}
