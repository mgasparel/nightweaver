using System.Text.Json;
using System.Threading.Channels;
using Nightweaver.Application;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Supervision;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class OwnerFailurePublicationTests
{
    [Theory]
    [InlineData("stop", false, false)]
    [InlineData("force", false, false)]
    [InlineData("stop", true, false)]
    [InlineData("stop", false, true)]
    [InlineData("force", false, true)]
    [InlineData("stop", true, true)]
    public async Task FailedControlPublicationKeepsRunningState(string operation, bool maintenance, bool canceled)
    {
        using var directory = new TempDirectory();
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var owner = DeploymentObservationTests.Owner(identity);
        var paths = new HostPaths(directory.Path);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = identity.TargetRepository }
        }), TestContext.Current.CancellationToken);
        var project = ProjectConfigLoader.Load(identity.ConfigPath, ProjectSlug.Parse("demo"), paths);
        using var requestLifetime = new CancellationTokenSource();
        using var state = new BackgroundOwnerState(owner, (_, proposed, token) =>
        {
            Assert.Equal("stopping", proposed.State);
            if (!canceled) throw new IOException("injected sharing denial");
            requestLifetime.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var request = new BackgroundControlRequest(operation, owner.RunId, Maintenance: maintenance);
        using var shutdown = new RuntimeShutdownSignals(_ => throw new InvalidOperationException("unexpected force"));
        var exception = await Record.ExceptionAsync(async () =>
        {
            var response = await BackgroundOwnerApplication.HandleRequestAsync(request,
                new(directory.Path, identity.ConfigPath, "demo", owner.RunId, owner.WebUrl), paths,
                project, state, BackgroundOwnerRecord.PathFor(paths, project.Name), () => null, null,
                requestLifetime.Token);
            if (response.Accepted) BackgroundOwnerApplication.ApplyControlAction(request, owner.RunId, shutdown);
        });
        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(exception);
        else Assert.IsType<IOException>(exception);
        Assert.Equal(owner, state.Current);
        Assert.False(shutdown.StopGate.IsStopRequested);
        Assert.False(shutdown.StopGate.IsForceStopRequested);
    }

    [Fact]
    public async Task ConcurrentFailureSurvivesSuccessfulControlPublication()
    {
        using var directory = new TempDirectory();
        var owner = DeploymentObservationTests.Owner(DeploymentObservationTests.Identity(directory.Path));
        var writing = Signal();
        var release = Signal();
        using var state = new BackgroundOwnerState(owner, async (path, proposed, token) =>
        {
            writing.TrySetResult(true);
            await release.Task.WaitAsync(token);
            await BackgroundOwnerRecord.WriteAsync(path, proposed, token);
        });
        var path = Path.Combine(directory.Path, "owner.json");
        var publication = state.PublishAsync(path, record => record with
        {
            State = "stopping", StopReason = "OperatorDrain"
        }, TestContext.Current.CancellationToken);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(owner, state.Current);
        var failure = new BackgroundFailure(DateTimeOffset.UtcNow, "RuntimeFailure", owner.RunId,
            owner.RuntimeId, owner.ProcessId, owner.ProcessStartedAtUtc);
        var update = Task.Run(() => state.Update(record => record with { Failure = failure }),
            TestContext.Current.CancellationToken);
        release.TrySetResult(true);
        await Task.WhenAll(publication, update);
        await state.PersistAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("stopping", state.Current.State);
        Assert.Equal(failure, state.Current.Failure);
        Assert.Equal(failure, BackgroundOwnerRecord.TryRead(path)!.Failure);
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("timer")]
    [InlineData("completion")]
    public async Task InfrastructureFailureIsRetainedBeforeBlockedPoolDrain(string source)
    {
        using var directory = new TempDirectory();
        var identity = DeploymentObservationTests.Identity(directory.Path);
        var owner = DeploymentObservationTests.Owner(identity);
        using var state = new BackgroundOwnerState(owner);
        var paths = new HostPaths(directory.Path);
        var path = BackgroundOwnerRecord.PathFor(paths, ProjectSlug.Parse("demo"));
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var releaseDrain = Signal();
        var draining = Signal();
        var stopGate = new ContinuousStopGate();
        var pool = new ContinuousPoolRegistration(ContinuousPoolKind.Delivery,
            _ => Task.FromResult<IReadOnlyList<DeliveryPoolDiagnostic>>([]), completions.Reader,
            () => throw new InvalidOperationException("ordinary failure must not cancel slots"), async _ =>
            {
                draining.TrySetResult(true);
                await releaseDrain.Task;
            });
        var coordinator = new ContinuousDiscoveryCoordinator([pool],
            _ => source == "discovery" ? Task.FromException(new IOException("infrastructure fault")) : Task.CompletedTask,
            TextWriter.Null, TextWriter.Null,
            token => ticks.Reader.ReadAllAsync(token), stopGate: stopGate,
            onInfrastructureFailure: () => BackgroundOwnerApplication.RetainFailureAsync(state, path, "RuntimeFailure"));
        var run = coordinator.RunAsync(TestContext.Current.CancellationToken);
        if (source == "timer") ticks.Writer.TryComplete();
        if (source == "completion") completions.Writer.TryComplete();
        try
        {
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.True(stopGate.IsStopRequested);
            var retained = BackgroundOwnerRecord.TryRead(path)!;
            Assert.Equal(new BackgroundFailure(retained.Failure!.Utc, "RuntimeFailure", owner.RunId,
                owner.RuntimeId, owner.ProcessId, owner.ProcessStartedAtUtc), retained.Failure);
            var snapshot = new FactoryTelemetrySnapshot(2, identity.Project, identity.TargetRepository,
                owner.RuntimeId!, 1, DateTimeOffset.UtcNow, "running", new(1, []), new(1, []));
            var observer = new DeploymentObserver(identity, new(() => retained, () => true,
                _ => new(OwnerProcessState.Alive, false),
                _ => Task.FromResult(new BackgroundControlResponse(true,
                    retained with { ObservedAtUtc = DateTimeOffset.UtcNow }, null)),
                () => new(FactoryTelemetryAvailability.Live, null, snapshot),
                () => new(SupervisionAvailability.Manual, null, "", null)));
            Assert.Equal(DeploymentHealth.Failed,
                (await observer.ObserveAsync(TestContext.Current.CancellationToken)).Health);
        }
        finally { releaseDrain.TrySetResult(true); }
        Assert.NotNull(await Record.ExceptionAsync(async () => await run));
    }

    [Fact]
    public async Task DeliberateStopDoesNotPublishInfrastructureFailure()
    {
        using var lifetime = new CancellationTokenSource();
        var completions = Channel.CreateUnbounded<DeliveryPoolCompletion>();
        var ticks = Channel.CreateUnbounded<bool>();
        var evaluated = Signal();
        var failures = 0;
        var pool = new ContinuousPoolRegistration(ContinuousPoolKind.Delivery, _ =>
        {
            evaluated.TrySetResult(true);
            return Task.FromResult<IReadOnlyList<DeliveryPoolDiagnostic>>([]);
        }, completions.Reader, () => { }, _ => Task.CompletedTask);
        var coordinator = new ContinuousDiscoveryCoordinator([pool], _ => Task.CompletedTask,
            TextWriter.Null, TextWriter.Null, token => ticks.Reader.ReadAllAsync(token),
            onInfrastructureFailure: () => { failures++; return Task.CompletedTask; });
        var run = coordinator.RunAsync(lifetime.Token);
        await evaluated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lifetime.Cancel();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(0, failures);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
