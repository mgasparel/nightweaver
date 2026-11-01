using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nightweaver.Execution;

namespace Nightweaver.Application;

internal delegate IAsyncEnumerable<bool> DiscoveryTimerTicks(CancellationToken cancellationToken);

/// <summary>Coalesces startup, timer, and released-slot signals into one serial discovery reader.</summary>
internal sealed class ContinuousDiscoveryCoordinator(
    Func<CancellationToken, Task<IReadOnlyList<DeliveryPoolDiagnostic>>> evaluateDelivery,
    ChannelReader<DeliveryPoolCompletion> deliveryCompletions,
    Func<CancellationToken, Task> validateAdmissionInfrastructure,
    Action cancelSlots,
    Func<CancellationToken, Task> drainSlots,
    TextWriter output,
    TextWriter error,
    DiscoveryTimerTicks? timerTicks = null)
{
    internal static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(30);

    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    internal bool RequestDiscovery() => requests.Writer.TryWrite(true);

    internal async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = DiscoveryLoopAsync(runtimeCancellation.Token);
        var timer = TimerLoopAsync(runtimeCancellation.Token);
        var completions = CompletionLoopAsync(runtimeCancellation.Token);
        var drained = true;
        RequestDiscovery();

        try
        {
            var first = await Task.WhenAny(worker, timer, completions,
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
            cancelSlots();
            runtimeCancellation.Cancel();
            try
            {
                await Task.WhenAll(worker, timer, completions)
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

            using var drainTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await drainSlots(drainTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                drained = false;
                error.WriteLine($"nightweaver: Delivery shutdown could not confirm all local slots stopped: {exception.Message}");
            }
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
            try
            {
                var diagnostics = await evaluateDelivery(cancellationToken).ConfigureAwait(false);
                foreach (var diagnostic in diagnostics)
                    error.WriteLine($"nightweaver: Delivery skipped '{diagnostic.BeadId}' ({diagnostic.Code}): {diagnostic.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is not ToolExecutionException)
                    throw;
                error.WriteLine($"nightweaver: Delivery discovery failed; a later trigger will reread current tracker state: {exception.Message}");
            }
        }
    }

    private async Task TimerLoopAsync(CancellationToken cancellationToken)
    {
        var ticks = timerTicks ?? ReadTimerTicksAsync;
        await foreach (var _ in ticks(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            RequestDiscovery();
    }

    private async Task CompletionLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var completion in deliveryCompletions.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = completion.Exception is not null
                ? $"failed: {completion.Exception.Message}"
                : completion.IsCanceled
                    ? "canceled"
                    : completion.Outcome?.Disposition.ToString() ?? "ended without an outcome";
            output.WriteLine($"Delivery slot '{completion.BeadId}' released local capacity ({result}).");
            RequestDiscovery();
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
