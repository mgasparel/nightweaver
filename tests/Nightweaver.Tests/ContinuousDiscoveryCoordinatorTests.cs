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

    [Fact]
    public async Task JournalWatcherFailureDoesNotStopTimerOrCompletionDiscovery()
    {
        using var lifetime = new CancellationTokenSource();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var error = new StringWriter();
        var watcherStopped = NewSignal();
        var thirdPass = NewSignal();
        var calls = 0;
        var coordinator = new ContinuousDiscoveryCoordinator(
            [Registration(ContinuousPoolKind.Delivery, _ =>
            {
                if (Interlocked.Increment(ref calls) == 3) thirdPass.TrySetResult(true);
                return EmptyDiagnostics();
            }, completions.Reader)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            error,
            token => ReadTicksAsync(ticks.Reader, token),
            (requestDiscovery, _) =>
            {
                requestDiscovery();
                watcherStopped.TrySetResult(true);
                throw new IOException("tail transport exited");
            });

        var run = coordinator.RunAsync(lifetime.Token);
        await watcherStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitForCallsAsync(1);
        Assert.Contains("periodic and completion discovery remain active", error.ToString(), StringComparison.Ordinal);

        completions.Writer.TryWrite(new DeliveryPoolCompletion("completed", null, null, false, true));
        await WaitForCallsAsync(2);
        ticks.Writer.TryWrite(true);
        await thirdPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
    public async Task LaterConfiguredPoolRunsWhileDeliveryPreparationIsBlockedAndCompletionFansIn()
    {
        using var lifetime = new CancellationTokenSource();
        var planningCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var deliveryCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var mergerCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var preparationStarted = NewSignal();
        var releasePreparation = NewSignal();
        var secondPass = NewSignal();
        var order = new List<ContinuousPoolKind>();
        var deliveryPasses = 0;
        var mergerPasses = 0;
        Task? blockedPreparation = null;

        var registrations = new[]
        {
            Registration(ContinuousPoolKind.Merger, _ =>
            {
                order.Add(ContinuousPoolKind.Merger);
                if (Interlocked.Increment(ref mergerPasses) == 2) secondPass.TrySetResult(true);
                return EmptyDiagnostics();
            }, mergerCompletions.Reader),
            Registration(ContinuousPoolKind.Delivery, _ =>
            {
                order.Add(ContinuousPoolKind.Delivery);
                if (Interlocked.Increment(ref deliveryPasses) == 1)
                {
                    blockedPreparation = Task.Run(async () =>
                    {
                        preparationStarted.TrySetResult(true);
                        await releasePreparation.Task.ConfigureAwait(false);
                    }, CancellationToken.None);
                }
                return EmptyDiagnostics();
            }, deliveryCompletions.Reader),
            Registration(ContinuousPoolKind.Planning, _ =>
            {
                order.Add(ContinuousPoolKind.Planning);
                return EmptyDiagnostics();
            }, planningCompletions.Reader)
        };
        var coordinator = new ContinuousDiscoveryCoordinator(
            registrations,
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTicksAsync(ticks.Reader, token));

        var run = coordinator.RunAsync(lifetime.Token);
        await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        while (Volatile.Read(ref mergerPasses) == 0)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        var preparationTask = Assert.IsAssignableFrom<Task>(blockedPreparation);
        Assert.False(preparationTask.IsCompleted);
        Assert.Equal([ContinuousPoolKind.Planning, ContinuousPoolKind.Delivery, ContinuousPoolKind.Merger], order);

        planningCompletions.Writer.TryWrite(new DeliveryPoolCompletion("planning-finished", null, null, false, true));
        await secondPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref deliveryPasses));
        Assert.False(preparationTask.IsCompleted);

        releasePreparation.TrySetResult(true);
        await preparationTask;
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AdmissionInfrastructureFailureStopsFurtherPassesAndCancelsAndDrainsEveryPool()
    {
        using var lifetime = new CancellationTokenSource();
        var planningCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var deliveryCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var firstPass = NewSignal();
        var validationCount = 0;
        var planningPasses = 0;
        var deliveryPasses = 0;
        var planningCancels = 0;
        var deliveryCancels = 0;
        var planningDrains = 0;
        var deliveryDrains = 0;

        var registrations = new[]
        {
            Registration(ContinuousPoolKind.Delivery, _ =>
            {
                Interlocked.Increment(ref deliveryPasses);
                firstPass.TrySetResult(true);
                return EmptyDiagnostics();
            }, deliveryCompletions.Reader,
                () => Interlocked.Increment(ref deliveryCancels),
                _ => { Interlocked.Increment(ref deliveryDrains); return Task.CompletedTask; }),
            Registration(ContinuousPoolKind.Planning, _ =>
            {
                Interlocked.Increment(ref planningPasses);
                return EmptyDiagnostics();
            }, planningCompletions.Reader,
                () => Interlocked.Increment(ref planningCancels),
                _ => { Interlocked.Increment(ref planningDrains); return Task.CompletedTask; })
        };
        var coordinator = new ContinuousDiscoveryCoordinator(
            registrations,
            _ =>
            {
                if (Interlocked.Increment(ref validationCount) == 2)
                    throw new ToolExecutionException("runtime Logs root disappeared");
                return Task.CompletedTask;
            },
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTicksAsync(ticks.Reader, token));

        var run = coordinator.RunAsync(lifetime.Token);
        await firstPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        ticks.Writer.TryWrite(true);
        var failure = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("runtime Logs root disappeared", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, Volatile.Read(ref validationCount));
        Assert.Equal(1, Volatile.Read(ref planningPasses));
        Assert.Equal(1, Volatile.Read(ref deliveryPasses));
        Assert.Equal(1, Volatile.Read(ref planningCancels));
        Assert.Equal(1, Volatile.Read(ref deliveryCancels));
        Assert.Equal(1, Volatile.Read(ref planningDrains));
        Assert.Equal(1, Volatile.Read(ref deliveryDrains));
    }

    [Fact]
    public async Task IndividualPoolSlotFailureRequestsDiscoveryWithoutStoppingOtherPools()
    {
        using var lifetime = new CancellationTokenSource();
        var planningCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var deliveryCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var secondPass = NewSignal();
        var deliveryPasses = 0;
        var cancels = 0;
        var registrations = new[]
        {
            Registration(ContinuousPoolKind.Delivery, _ =>
            {
                if (Interlocked.Increment(ref deliveryPasses) == 2) secondPass.TrySetResult(true);
                return EmptyDiagnostics();
            }, deliveryCompletions.Reader, () => Interlocked.Increment(ref cancels)),
            Registration(ContinuousPoolKind.Planning, _ => EmptyDiagnostics(), planningCompletions.Reader,
                () => Interlocked.Increment(ref cancels))
        };
        var coordinator = new ContinuousDiscoveryCoordinator(
            registrations,
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null,
            token => ReadTicksAsync(ticks.Reader, token));

        var run = coordinator.RunAsync(lifetime.Token);
        while (Volatile.Read(ref deliveryPasses) == 0)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        deliveryCompletions.Writer.TryWrite(new DeliveryPoolCompletion(
            "failed-slot", null, new InvalidOperationException("one slot failed"), false, true));
        await secondPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref deliveryPasses));
        Assert.Equal(0, Volatile.Read(ref cancels));
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConfiguredPoolCompletionStreamTerminationCancelsAndDrainsEveryPool()
    {
        using var lifetime = new CancellationTokenSource();
        var deliveryCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var mergerCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        mergerCompletions.Writer.TryComplete();
        var cancels = new int[3];
        var drains = new int[3];

        ContinuousPoolRegistration Registration(ContinuousPoolKind kind, ChannelReader<DeliveryPoolCompletion> completions) =>
            new(kind,
                _ => EmptyDiagnostics(),
                completions,
                () => Interlocked.Increment(ref cancels[(int)kind]),
                _ => { Interlocked.Increment(ref drains[(int)kind]); return Task.CompletedTask; });

        var coordinator = new ContinuousDiscoveryCoordinator(
            [
                Registration(ContinuousPoolKind.Merger, mergerCompletions.Reader),
                Registration(ContinuousPoolKind.Delivery, deliveryCompletions.Reader)
            ],
            _ => Task.CompletedTask,
            TextWriter.Null,
            TextWriter.Null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("completion stream stopped unexpectedly", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref cancels[(int)ContinuousPoolKind.Delivery]));
        Assert.Equal(1, Volatile.Read(ref cancels[(int)ContinuousPoolKind.Merger]));
        Assert.Equal(1, Volatile.Read(ref drains[(int)ContinuousPoolKind.Delivery]));
        Assert.Equal(1, Volatile.Read(ref drains[(int)ContinuousPoolKind.Merger]));
    }

    private static ContinuousDiscoveryCoordinator CreateCoordinator(
        Func<CancellationToken, Task<IReadOnlyList<DeliveryPoolDiagnostic>>> evaluate,
        ChannelReader<DeliveryPoolCompletion> completions,
        ChannelReader<bool> timerTicks,
        TextWriter? error = null)
    {
        return new ContinuousDiscoveryCoordinator(
            [Registration(ContinuousPoolKind.Delivery, evaluate, completions)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            error ?? TextWriter.Null,
            token => ReadTicksAsync(timerTicks, token));
    }

    private static ContinuousPoolRegistration Registration(
        ContinuousPoolKind kind,
        Func<CancellationToken, Task<IReadOnlyList<DeliveryPoolDiagnostic>>> evaluate,
        ChannelReader<DeliveryPoolCompletion> completions,
        Action? cancel = null,
        Func<CancellationToken, Task>? drain = null) =>
        new(kind, evaluate, completions, cancel ?? (() => { }), drain ?? (_ => Task.CompletedTask));

    private static Task<IReadOnlyList<DeliveryPoolDiagnostic>> EmptyDiagnostics() =>
        Task.FromResult<IReadOnlyList<DeliveryPoolDiagnostic>>([]);

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
