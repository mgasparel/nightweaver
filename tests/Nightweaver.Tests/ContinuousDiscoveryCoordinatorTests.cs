using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Nightweaver.Application;
using Nightweaver.Configuration;
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
        var failWatcher = NewSignal();
        var passGates = Channel.CreateUnbounded<TaskCompletionSource<bool>>();
        var coordinator = new ContinuousDiscoveryCoordinator(
            [Registration(ContinuousPoolKind.Delivery, token =>
            {
                var gate = NewSignal();
                passGates.Writer.TryWrite(gate);
                return WaitForPassReleaseAsync(gate, token);
            }, completions.Reader)],
            _ => Task.CompletedTask,
            TextWriter.Null,
            error,
            token => ReadTicksAsync(ticks.Reader, token),
            async (_, token) =>
            {
                await failWatcher.Task.WaitAsync(token);
                watcherStopped.TrySetResult(true);
                throw new IOException("tail transport exited");
            });

        var run = coordinator.RunAsync(lifetime.Token);
        try
        {
            var startupPass = await ReadPassGateAsync();
            // Failure before startup begins may correctly coalesce with startup's
            // request. Hold startup in flight before requiring a separate pass.
            failWatcher.TrySetResult(true);
            await watcherStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await WaitForWatcherFailureAsync();
            Assert.Contains("periodic and completion discovery remain active", error.ToString(), StringComparison.Ordinal);

            startupPass.TrySetResult(true);
            var watcherFailurePass = await ReadPassGateAsync();
            watcherFailurePass.TrySetResult(true);

            completions.Writer.TryWrite(new DeliveryPoolCompletion("completed", null, null, false, true));
            var completionPass = await ReadPassGateAsync();
            completionPass.TrySetResult(true);

            ticks.Writer.TryWrite(true);
            var timerPass = await ReadPassGateAsync();
            timerPass.TrySetResult(true);
        }
        finally
        {
            lifetime.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        async Task<TaskCompletionSource<bool>> ReadPassGateAsync() =>
            await passGates.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        async Task WaitForWatcherFailureAsync()
        {
            while (!error.ToString().Contains("tracker journal watcher stopped", StringComparison.Ordinal))
                await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        static async Task<IReadOnlyList<DeliveryPoolDiagnostic>> WaitForPassReleaseAsync(
            TaskCompletionSource<bool> release, CancellationToken cancellationToken)
        {
            await release.Task.WaitAsync(cancellationToken);
            return await EmptyDiagnostics();
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
    public async Task AdmissionInfrastructureFailureStopsFurtherPassesWithoutCancellingSlotsAndDrainsEveryPool()
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
        Assert.Equal(0, Volatile.Read(ref planningCancels));
        Assert.Equal(0, Volatile.Read(ref deliveryCancels));
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
    public async Task ConfiguredPoolCompletionStreamTerminationDrainsEveryPoolWithoutCancellingSlots()
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
        Assert.Equal(0, Volatile.Read(ref cancels[(int)ContinuousPoolKind.Delivery]));
        Assert.Equal(0, Volatile.Read(ref cancels[(int)ContinuousPoolKind.Merger]));
        Assert.Equal(1, Volatile.Read(ref drains[(int)ContinuousPoolKind.Delivery]));
        Assert.Equal(1, Volatile.Read(ref drains[(int)ContinuousPoolKind.Merger]));
    }

    [Fact]
    public async Task GracefulStopClosesAdmissionAndKeepsCompletionAccountingAliveThroughDrain()
    {
        using var gracefulStop = new CancellationTokenSource();
        using var forceStop = new CancellationTokenSource();
        var gate = new ContinuousStopGate();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var drainStarted = NewSignal();
        var releaseDrain = NewSignal();
        var firstPass = NewSignal();
        var error = new StringWriter();
        var output = new StringWriter();
        var cancels = 0;
        var coordinator = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                _ => { firstPass.TrySetResult(true); return EmptyDiagnostics(); },
                completions.Reader,
                () => Interlocked.Increment(ref cancels),
                async token =>
                {
                    drainStarted.TrySetResult(true);
                    await releaseDrain.Task.WaitAsync(token);
                    completions.Writer.TryWrite(new DeliveryPoolCompletion("failed-current-turn", null,
                        new IOException("required finalization failed"), false, LocalCapacityReleased: true));
                },
                async token =>
                {
                    var accounted = NewSignal();
                    completions.Writer.TryWrite(new DeliveryPoolCompletion(string.Empty, null, null, false,
                        LocalCapacityReleased: true, AccountingBarrier: accounted));
                    await accounted.Task.WaitAsync(token);
                })],
            _ => Task.CompletedTask,
            output,
            error,
            token => ReadTicksAsync(ticks.Reader, token),
            stopGate: gate);

        var run = coordinator.RunAsync(gracefulStop.Token, forceStop.Token);
        await firstPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        gracefulStop.Cancel();
        await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(coordinator.RequestDiscovery());
        Assert.Equal(0, Volatile.Read(ref cancels));

        releaseDrain.TrySetResult(true);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains("failed-current-turn", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref cancels));
    }

    [Fact]
    public async Task OnePoolFinalizationFailureDoesNotPreventOtherPoolsFromDraining()
    {
        using var gracefulStop = new CancellationTokenSource();
        var completions = new[]
        {
            Channel.CreateUnbounded<DeliveryPoolCompletion>(),
            Channel.CreateUnbounded<DeliveryPoolCompletion>()
        };
        var firstDrainStarted = NewSignal();
        var secondDrainStarted = NewSignal();
        var releaseSecondDrain = NewSignal();
        var error = new StringWriter();
        var pools = new[]
            {
                (ContinuousPoolKind.Delivery, completions[0].Reader),
                (ContinuousPoolKind.Merger, completions[1].Reader)
            }
            .Select((entry, index) => new ContinuousPoolRegistration(
                entry.Item1,
                _ => EmptyDiagnostics(),
                entry.Item2,
                static () => { },
                async token =>
                {
                    if (index == 0)
                    {
                        firstDrainStarted.TrySetResult(true);
                        throw new IOException("Delivery ownership finalization failed.");
                    }

                    secondDrainStarted.TrySetResult(true);
                    await releaseSecondDrain.Task.WaitAsync(token);
                }))
            .ToArray();
        var coordinator = new ContinuousDiscoveryCoordinator(
            pools,
            _ => Task.CompletedTask,
            TextWriter.Null,
            error);

        var run = coordinator.RunAsync(gracefulStop.Token);
        gracefulStop.Cancel();
        await Task.WhenAll(
            firstDrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            secondDrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(run.IsCompleted, "A local failure must not skip or finish the other pool's drain.");

        releaseSecondDrain.TrySetResult(true);
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains("Delivery ownership finalization failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergerCompletionProjectionTriggersDiscoveryAndDrainAccounting()
    {
        using var lifetime = new CancellationTokenSource();
        var completions = Channel.CreateUnbounded<MergerPoolCompletion>();
        var firstPass = NewSignal();
        var secondPass = NewSignal();
        var output = new StringWriter();
        var passes = 0;
        var barriers = 0;
        var coordinator = new ContinuousDiscoveryCoordinator(
            [new ContinuousPoolRegistration(
                ContinuousPoolKind.Merger,
                _ =>
                {
                    if (Interlocked.Increment(ref passes) == 1) firstPass.TrySetResult(true);
                    else secondPass.TrySetResult(true);
                    return Task.FromResult<IReadOnlyList<MergerPoolDiagnostic>>([]);
                },
                completions.Reader,
                static () => { },
                _ => Task.CompletedTask,
                async token =>
                {
                    var accounted = NewSignal();
                    completions.Writer.TryWrite(new MergerPoolCompletion(string.Empty, null, null, false,
                        LocalCapacityReleased: true, AccountingBarrier: accounted));
                    await accounted.Task.WaitAsync(token);
                    Interlocked.Increment(ref barriers);
                })],
            _ => Task.CompletedTask,
            output,
            TextWriter.Null);

        var run = coordinator.RunAsync(lifetime.Token);
        await firstPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        completions.Writer.TryWrite(new MergerPoolCompletion("merge-failed",
            new MergerSlotOutcome(MergerSlotDisposition.Failed, null, null, false, "controlled failure"),
            null, false, LocalCapacityReleased: true));
        await secondPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        lifetime.Cancel();
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref barriers));
        Assert.Contains("Merger slot 'merge-failed' released local capacity (Failed; claim handoff=False): controlled failure", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "cleanup needs retry", null, false)]
    [InlineData(true, false, null, "Product completion failed", false)]
    [InlineData(true, true, null, "Required housekeeping diagnostic could not be persisted", false)]
    [InlineData(true, false, null, null, true)]
    public async Task MergedHousekeepingReceiptControlsDrainWithoutSkippingDeliveryAccounting(
        bool cleanupSucceeded, bool productCompletionSucceeded, string? productDiagnostic,
        string? diagnosticPersistenceDiagnostic, bool expectedDrain)
    {
        using var lifetime = new CancellationTokenSource();
        var deliveryCompletions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var mergerCompletions = Channel.CreateUnbounded<MergerPoolCompletion>();
        var initialPasses = NewSignal();
        var completionPass = NewSignal();
        var evaluationCounts = new int[3];
        var drainCounts = 0;
        var accountingCounts = 0;
        var output = new ChannelLineWriter();

        Task<IReadOnlyList<TDiagnostic>> Evaluate<TDiagnostic>(int poolIndex, Func<IReadOnlyList<TDiagnostic>> diagnostics)
        {
            var otherPoolIndex = poolIndex == (int)ContinuousPoolKind.Delivery
                ? (int)ContinuousPoolKind.Merger
                : (int)ContinuousPoolKind.Delivery;
            var count = Interlocked.Increment(ref evaluationCounts[poolIndex]);
            if (count == 1 && Volatile.Read(ref evaluationCounts[otherPoolIndex]) >= 1)
                initialPasses.TrySetResult(true);
            if (count >= 2 && Volatile.Read(ref evaluationCounts[otherPoolIndex]) >= 2)
                completionPass.TrySetResult(true);
            return Task.FromResult(diagnostics());
        }

        async Task WaitDeliveryAccountingAsync(CancellationToken token)
        {
            var barrier = NewSignal();
            deliveryCompletions.Writer.TryWrite(new DeliveryPoolCompletion(string.Empty, null, null, false,
                LocalCapacityReleased: true, AccountingBarrier: barrier));
            await barrier.Task.WaitAsync(token);
            Interlocked.Increment(ref accountingCounts);
        }

        async Task WaitMergerAccountingAsync(CancellationToken token)
        {
            var barrier = NewSignal();
            mergerCompletions.Writer.TryWrite(new MergerPoolCompletion(string.Empty, null, null, false,
                LocalCapacityReleased: true, AccountingBarrier: barrier));
            await barrier.Task.WaitAsync(token);
            Interlocked.Increment(ref accountingCounts);
        }

        var coordinator = new ContinuousDiscoveryCoordinator(
            [
                new ContinuousPoolRegistration(ContinuousPoolKind.Delivery,
                    _ => Evaluate((int)ContinuousPoolKind.Delivery, Array.Empty<DeliveryPoolDiagnostic>),
                    deliveryCompletions.Reader, static () => { }, _ => { Interlocked.Increment(ref drainCounts); return Task.CompletedTask; },
                    WaitDeliveryAccountingAsync),
                new ContinuousPoolRegistration(ContinuousPoolKind.Merger,
                    _ => Evaluate((int)ContinuousPoolKind.Merger, Array.Empty<MergerPoolDiagnostic>),
                    mergerCompletions.Reader, static () => { }, _ => { Interlocked.Increment(ref drainCounts); return Task.CompletedTask; },
                    WaitMergerAccountingAsync)
            ],
            _ => Task.CompletedTask,
            output,
            TextWriter.Null);

        var run = coordinator.RunAsync(lifetime.Token);
        await initialPasses.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var closed = new BeadWorkflowState("merge-task", "task", "closed", "stage:merge", false, false, false, 0);
        var receipt = new MergerCompletionReceipt(closed, "https://example.invalid/pull/123", new string('a', 40),
            new string('b', 40), cleanupSucceeded, productCompletionSucceeded,
            cleanupSucceeded ? null : "cleanup needs retry", productDiagnostic, diagnosticPersistenceDiagnostic);
        var stage = new AgentStageResult(AgentRole.Merger, "merge-task", "fixture-invocation", string.Empty,
            null, [], MergerCompletion: receipt);
        var mergerOutcome = new MergerSlotOutcome(MergerSlotDisposition.Merged, closed,
            new OperationalRetryOutcome(OperationalRetryStatus.Succeeded, stage, 1, null, null, null),
            ClaimHandoffCompleted: true, Diagnostic: "typed completion diagnostic");
        mergerCompletions.Writer.TryWrite(new MergerPoolCompletion("merge-task", mergerOutcome, null, false,
            LocalCapacityReleased: true));
        var deliveryState = new BeadWorkflowState("delivery-task", "task", "open", "stage:merge", false, false, false, 0);
        deliveryCompletions.Writer.TryWrite(new DeliveryPoolCompletion("delivery-task",
            new DeliverySlotOutcome(DeliverySlotDisposition.MergeReady, deliveryState, AgentRole.Qa, null, true, null),
            null, false, LocalCapacityReleased: true));

        await completionPass.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var observedLines = new List<string>();
        while (!observedLines.Any(line => line.Contains("Merger slot 'merge-task'", StringComparison.Ordinal))
            || !observedLines.Any(line => line.Contains("Delivery slot 'delivery-task'", StringComparison.Ordinal)))
        {
            observedLines.Add(await output.Lines.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        lifetime.Cancel();
        bool drained;
        try
        {
            drained = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"Continuous drain stalled; drains={Volatile.Read(ref drainCounts)}, accounting={Volatile.Read(ref accountingCounts)}, "
                + $"evals={string.Join(',', evaluationCounts)}; output={string.Join(" | ", observedLines)}", exception);
        }

        Assert.Equal(expectedDrain, drained);
        Assert.Equal("closed", mergerOutcome.FinalState?.Status);
        Assert.True(mergerOutcome.MergeCompleted);
        Assert.Equal(2, Volatile.Read(ref drainCounts));
        Assert.Equal(2, Volatile.Read(ref accountingCounts));
        Assert.Contains("Merger slot 'merge-task' released local capacity (Merged; claim handoff=True)",
            string.Join(Environment.NewLine, observedLines), StringComparison.Ordinal);
        Assert.Contains("Delivery slot 'delivery-task' released local capacity (MergeReady; claim handoff=True)",
            string.Join(Environment.NewLine, observedLines), StringComparison.Ordinal);
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

    private sealed class ChannelLineWriter : TextWriter
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        internal ChannelReader<string> Lines => lines.Reader;
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => lines.Writer.TryWrite(value ?? string.Empty);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (value <= current || Interlocked.CompareExchange(ref maximum, value, current) == current) return;
        }
    }
}
