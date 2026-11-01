using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nightweaver.Application;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class ContinuousDiscoveryCoordinatorTests
{
    [Fact]
    public async Task BurstsCoalesceAndActivityDuringEvaluationLeavesOnePendingPass()
    {
        using var lifetime = new CancellationTokenSource();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondStarted = NewSignal();
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        var coordinator = CreateCoordinator(async _ =>
        {
            var call = Interlocked.Increment(ref calls);
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            try
            {
                if (call == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                }
                if (call == 2) secondStarted.TrySetResult(true);
                return Array.Empty<DeliveryPoolDiagnostic>();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, completions.Reader, ticks.Reader);

        var run = coordinator.RunAsync(lifetime.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(coordinator.RequestDiscovery());
        Assert.False(coordinator.RequestDiscovery());
        Assert.False(coordinator.RequestDiscovery());
        releaseFirst.TrySetResult(true);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref maximumActive));

        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReleasedCompletionAndPeriodicTickEachRequestFreshDiscovery()
    {
        using var lifetime = new CancellationTokenSource();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var passCount = NewSignal();
        var calls = 0;
        var coordinator = CreateCoordinator(_ =>
        {
            var count = Interlocked.Increment(ref calls);
            if (count == 3) passCount.TrySetResult(true);
            return Task.FromResult<IReadOnlyList<DeliveryPoolDiagnostic>>([]);
        }, completions.Reader, ticks.Reader);

        var run = coordinator.RunAsync(lifetime.Token);
        await WaitForCallsAsync(1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        completions.Writer.TryWrite(new DeliveryPoolCompletion("completed", null, null, false, LocalCapacityReleased: true));
        await WaitForCallsAsync(2).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        ticks.Writer.TryWrite(true);
        await passCount.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(3, Volatile.Read(ref calls));

        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        async Task WaitForCallsAsync(int desired)
        {
            while (Volatile.Read(ref calls) < desired)
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FreshReadToolFailureWaitsForLaterTriggerAndUsesNoCachedAdmission()
    {
        using var lifetime = new CancellationTokenSource();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var recovered = NewSignal();
        var reads = 0;
        var error = new StringWriter();
        var coordinator = CreateCoordinator(_ =>
        {
            var read = Interlocked.Increment(ref reads);
            if (read == 1) throw new ToolExecutionException("tracker read unavailable");
            recovered.TrySetResult(true);
            return Task.FromResult<IReadOnlyList<DeliveryPoolDiagnostic>>([]);
        }, completions.Reader, ticks.Reader, error);

        var run = coordinator.RunAsync(lifetime.Token);
        await WaitForReadAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Contains("a later trigger will reread current tracker state", error.ToString(), StringComparison.Ordinal);

        ticks.Writer.TryWrite(true);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref reads));
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        async Task WaitForReadAsync()
        {
            while (Volatile.Read(ref reads) == 0)
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static ContinuousDiscoveryCoordinator CreateCoordinator(
        Func<CancellationToken, Task<IReadOnlyList<DeliveryPoolDiagnostic>>> evaluate,
        ChannelReader<DeliveryPoolCompletion> completions,
        ChannelReader<bool> timerTicks,
        TextWriter? error = null)
    {
        return new ContinuousDiscoveryCoordinator(
            evaluate,
            completions,
            _ => Task.CompletedTask,
            () => { },
            _ => Task.CompletedTask,
            TextWriter.Null,
            error ?? TextWriter.Null,
            token => ReadTicksAsync(timerTicks, token));
    }

    private static async IAsyncEnumerable<bool> ReadTicksAsync(ChannelReader<bool> ticks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var tick in ticks.ReadAllAsync(cancellationToken))
            yield return tick;
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (value <= current || Interlocked.CompareExchange(ref maximum, value, current) == current) return;
        }
    }
}
