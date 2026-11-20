using Nightweaver.Application;
using Nightweaver.Background;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Supervision;
using Nightweaver.Inspection;
using Nightweaver.Execution;
using System.Text.Json;

namespace Nightweaver.Tests;

public sealed class SupervisionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyConsoleStopIsPersistedWhenOwnerLifecycleInitializationSettles(bool beforeInitialization)
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var store = new SupervisionStore(new HostPaths(temp.Path), ProjectSlug.Parse("demo"), identity);
        var token = TestContext.Current.CancellationToken;
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), token);
        using var shutdown = new RuntimeShutdownSignals();
        var initialization = new TaskCompletionSource<SupervisionLifecycle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (beforeInitialization) shutdown.RequestStop();
        var persists = 0;
        var opening = BackgroundOwnerApplication.InitializeLifecycleAsync(shutdown, initializationToken =>
        {
            Assert.False(initializationToken.CanBeCanceled);
            return initialization.Task;
        }, lifecycle =>
        {
            persists++;
            lifecycle!.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, CancellationToken.None).GetAwaiter().GetResult();
        });
        if (!beforeInitialization) shutdown.RequestStop();
        Assert.False(opening.IsCompleted);
        Assert.True(shutdown.StopGate.IsStopRequested);
        Assert.True(shutdown.FirstStopToken.IsCancellationRequested);
        initialization.SetResult(new(store));
        await opening.WaitAsync(TimeSpan.FromSeconds(5), token);

        var reloaded = new SupervisionStore(new HostPaths(temp.Path), ProjectSlug.Parse("demo"), identity).Read();
        Assert.Equal(SupervisionAvailability.Available, reloaded.Availability);
        Assert.False(reloaded.State!.DesiredRunning);
        Assert.Equal(1, persists);
        Assert.Null(shutdown.IntentPersistenceFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartActiveOwnerRetainsGenerationGuardAcrossDrain(bool interveningStop)
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var paths = new HostPaths(temp.Path);
        var project = ProjectSlug.Parse("demo");
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(identity.ConfigPath)!);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = Path.Combine(temp.Path, "target") }
        }), token);
        var store = new SupervisionStore(paths, project, identity);
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), token);
        var lifecycle = new SupervisionLifecycle(store);
        var runId = Guid.NewGuid().ToString("N");
        var owner = new BackgroundOwnerRecord(1, "running", runId, 123, SupervisionPolicyTests.Start,
            temp.Path, identity.ConfigPath, "demo", null, null, "http://127.0.0.1:5000", "healthy", "live", "", "", null);
        using var ownerLock = BackgroundFileLock.AcquireOwner(paths, project);
        using var serverLifetime = new CancellationTokenSource();
        var stops = 0;
        var server = BackgroundControlProtocol.RunServerAsync(BackgroundIdentity.PipeName(paths, project),
            async (request, requestToken) =>
            {
                if (request.Operation == "stop")
                {
                    Assert.Equal(runId, request.RunId);
                    Assert.False(store.Read().State!.DesiredRunning);
                    Assert.Throws<IOException>(() => BackgroundFileLock.AcquireStart(paths, project));
                    stops++;
                    // Direct owner stop can advance intent while the manager waits for drain.
                    if (interveningStop)
                        await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, requestToken);
                }
                return new(true, owner, null);
            }, serverLifetime.Token, afterResponse: _ => ownerLock.Dispose());
        var error = new StringWriter();
        try
        {
            var result = await BackgroundManagerApplication.RunAsync(
                new(BackgroundAction.Restart, new ProjectSelector.ByConfigPath(identity.ConfigPath)),
                new(temp.Path, temp.Path, new StringWriter(), error), token,
                (_, _, _) => Task.FromResult<SupervisionLifecycle?>(lifecycle));
            Assert.Equal(1, result);
            Assert.Equal(1, stops);
            Assert.False(BackgroundFileLock.IsOwnerActive(paths, project));
            Assert.Equal(!interveningStop, store.Read().State!.DesiredRunning);
            Assert.Contains(interveningStop ? "Intent changed during restart drain" : "deployment manifest is missing",
                error.ToString(), StringComparison.Ordinal);
            using var nextCommand = BackgroundFileLock.AcquireStart(paths, project);
        }
        finally
        {
            await serverLifetime.CancelAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAbsentOwnerReleasesCommandLockAndReachesExplicitStart(bool supervised)
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var paths = new HostPaths(temp.Path);
        var project = ProjectSlug.Parse("demo");
        Directory.CreateDirectory(Path.GetDirectoryName(identity.ConfigPath)!);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = Path.Combine(temp.Path, "target") }
        }), TestContext.Current.CancellationToken);
        var store = new SupervisionStore(paths, project, identity);
        if (supervised)
            await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, false, null),
                TestContext.Current.CancellationToken);
        var before = store.Read().State;
        var error = new StringWriter();
        var environment = new AppEnvironment(temp.Path, temp.Path, new StringWriter(), error);

        var result = await BackgroundManagerApplication.RunAsync(
            new(BackgroundAction.Restart, new ProjectSelector.ByConfigPath(identity.ConfigPath)), environment,
            TestContext.Current.CancellationToken, (_, _, _) => Task.FromResult<SupervisionLifecycle?>(new(store)));

        // The missing manifest prevents native launch after the explicit start action.
        Assert.Equal(1, result);
        Assert.Contains("deployment manifest is missing", error.ToString(), StringComparison.Ordinal);
        Assert.False(BackgroundFileLock.IsOwnerActive(paths, project));
        using var nextCommand = BackgroundFileLock.AcquireStart(paths, project);
        if (supervised)
        {
            var after = store.Read().State!;
            Assert.True(after.DesiredRunning);
            Assert.Equal(before!.Generation + 2, after.Generation);
        }
        else
            Assert.False(Directory.Exists(paths.SupervisionRoot(project)));
    }

    [Fact]
    public async Task OwnerAcceptsStopOnlyAfterPersistenceAndDuplicateRepliesNeverForceDrain()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var projectSlug = ProjectSlug.Parse("demo");
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(identity.ConfigPath)!);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = Path.Combine(temp.Path, "target") }
        }), TestContext.Current.CancellationToken);
        var project = ProjectConfigLoader.Load(identity.ConfigPath, projectSlug, paths);
        var store = new SupervisionStore(paths, projectSlug, identity);
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), TestContext.Current.CancellationToken);
        var lifecycle = new SupervisionLifecycle(store);
        var runId = Guid.NewGuid().ToString("N");
        var owner = new BackgroundOwnerOptions(temp.Path, identity.ConfigPath, "demo", runId, "http://127.0.0.1:5000");
        var record = new BackgroundOwnerRecord(1, "running", runId, 123, SupervisionPolicyTests.Start,
            temp.Path, identity.ConfigPath, "demo", null, null, owner.Urls, "healthy", "live", "", "", null);
        var state = new BackgroundOwnerState(record);
        var request = new BackgroundControlRequest("stop", runId);
        var recordPath = BackgroundOwnerRecord.PathFor(paths, projectSlug);
        var rejected = await BackgroundOwnerApplication.HandleRequestAsync(request with { RunId = "foreign" }, owner, paths,
            project, state, recordPath, () => null, lifecycle, TestContext.Current.CancellationToken);
        Assert.False(rejected.Accepted);
        Assert.True(store.Read().State!.DesiredRunning);
        var response = await BackgroundOwnerApplication.HandleRequestAsync(request, owner, paths, project, state,
            recordPath, () => null, lifecycle, TestContext.Current.CancellationToken);
        Assert.True(response.Accepted);
        Assert.False(store.Read().State!.DesiredRunning);
        Assert.Equal("OperatorDrain", BackgroundOwnerRecord.TryRead(recordPath)!.StopReason);
        // A lost reply/crash cannot undo the intent already committed by the handler.
        using var shutdown = new RuntimeShutdownSignals(_ => throw new InvalidOperationException("must not force"));
        BackgroundOwnerApplication.ApplyControlAction(request, runId, shutdown);
        BackgroundOwnerApplication.ApplyControlAction(request, runId, shutdown);
        Assert.True(shutdown.StopGate.IsStopRequested);
        Assert.False(shutdown.StopGate.IsForceStopRequested);
        Assert.Equal(RuntimeShutdownReason.OperatorDrain, shutdown.Reason);
    }

    [Fact]
    public async Task ManagerAbsentOwnerStopMaintenanceAndExplicitStartUsePersistedIntent()
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var paths = new HostPaths(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(identity.ConfigPath)!);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = Path.Combine(temp.Path, "target") }
        }), TestContext.Current.CancellationToken);
        var store = new SupervisionStore(paths, ProjectSlug.Parse("demo"), identity);
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var error = new StringWriter();
        var environment = new AppEnvironment(temp.Path, temp.Path, output, error);
        async Task<int> Command(params string[] tail)
        {
            output.GetStringBuilder().Clear();
            List<string> args = ["background"];
            args.AddRange(tail);
            args.AddRange(["--project", "demo"]);
            return await BackgroundManagerApplication.RunAsync(BackgroundCommandParser.Parse(args, out _), environment,
                TestContext.Current.CancellationToken, (_, _, _) => Task.FromResult<SupervisionLifecycle?>(new(store)));
        }
        Assert.Equal(0, await Command("stop", "--force"));
        Assert.False(store.Read().State!.DesiredRunning);
        using (var document = JsonDocument.Parse(output.ToString()))
            Assert.False(document.RootElement.GetProperty("supervision").GetProperty("state").GetProperty("desired_running").GetBoolean());
        Assert.Equal(0, await Command("maintenance", "enter"));
        var entered = store.Read().State!;
        Assert.NotNull(entered.MaintenanceToken);
        Assert.Equal(1, await Command("start"));
        Assert.Equal(entered.Generation, store.Read().State!.Generation);
        Assert.Equal(0, await Command("maintenance", "status"));
        Assert.Equal(0, await Command("maintenance", "leave", "--token", entered.MaintenanceToken!, "--revision", entered.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(store.Read().State!.DesiredRunning);
        // Missing deployment refuses native launch, but the explicit running action is durable.
        Assert.Equal(1, await Command("start"));
        Assert.True(store.Read().State!.DesiredRunning);
        Assert.False(BackgroundFileLock.IsOwnerActive(paths, ProjectSlug.Parse("demo")));
    }

    [Fact]
    public async Task ManagerManualStatusAndStopDoNotCreateSupervision()
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(identity.ConfigPath)!);
        await File.WriteAllTextAsync(identity.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "demo", target_repository = new { path = Path.Combine(temp.Path, "target") }
        }), TestContext.Current.CancellationToken);
        var environment = new AppEnvironment(temp.Path, temp.Path, new StringWriter(), new StringWriter());
        foreach (var action in new[] { BackgroundAction.Status, BackgroundAction.Stop })
            Assert.Equal(0, await BackgroundManagerApplication.RunAsync(new(action, new ProjectSelector.ByConfigPath(identity.ConfigPath)),
                environment, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(new HostPaths(temp.Path).SupervisionRoot(ProjectSlug.Parse("demo"))));
    }

    [Fact]
    public async Task StopCommitsBeforeLostReplyAndPreservesPendingAttemptAcrossReload()
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var store = new SupervisionStore(new HostPaths(temp.Path), ProjectSlug.Parse("demo"), identity);
        var initial = SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null) with
        {
            Attempts = 1,
            PendingAttempt = new(Guid.NewGuid().ToString("N"), 1, 1, SupervisionPolicyTests.Start,
                Guid.NewGuid().ToString("N"), null)
        };
        await store.SaveAsync(null, initial, TestContext.Current.CancellationToken);
        var lifecycle = new SupervisionLifecycle(store);
        await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, TestContext.Current.CancellationToken);
        // No owner/reply is needed for this acceptance: a crash here retains stopped intent.
        var retained = store.Read().State!;
        Assert.True(retained.Suppressed);
        Assert.Equal(initial.PendingAttempt, retained.PendingAttempt);
        Assert.Equal(1, retained.Attempts);
        Assert.Throws<InvalidOperationException>(() => SupervisionLifecycle.Transition(retained, SupervisionLifecycleAction.Start));
        await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, TestContext.Current.CancellationToken);
        Assert.False(store.Read().State!.DesiredRunning);
        Assert.Equal(retained.Generation + 1, store.Read().State!.Generation);
    }

    [Fact]
    public async Task InterruptedMaintenanceAndInterveningStopRequireCurrentReleaseAndRetainStop()
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var store = new SupervisionStore(new HostPaths(temp.Path), ProjectSlug.Parse("demo"), identity);
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), TestContext.Current.CancellationToken);
        var lifecycle = new SupervisionLifecycle(store);
        var entered = (await lifecycle.ApplyAsync(SupervisionLifecycleAction.EnterMaintenance, null, null, TestContext.Current.CancellationToken)).State!;
        Assert.True(entered.Suppressed);
        Assert.True(entered.DesiredRunning);
        Assert.Throws<InvalidOperationException>(() => SupervisionLifecycle.Transition(entered, SupervisionLifecycleAction.Start));
        Assert.Throws<InvalidOperationException>(() => SupervisionLifecycle.Transition(entered, SupervisionLifecycleAction.EnterMaintenance));
        await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance,
            entered.MaintenanceToken, entered.Revision, TestContext.Current.CancellationToken));
        var current = store.Read().State!;
        Assert.Equal(entered.MaintenanceToken, current.MaintenanceToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance,
            Guid.NewGuid().ToString("N"), current.Revision, TestContext.Current.CancellationToken));
        var released = (await lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance,
            current.MaintenanceToken, current.Revision, TestContext.Current.CancellationToken)).State!;
        Assert.Null(released.MaintenanceToken);
        Assert.False(released.DesiredRunning);
        Assert.True(released.Suppressed);
    }

    [Fact]
    public void ReleasePreservesDisableAndStartDoesNotRefundExhaustion()
    {
        var state = SupervisionState.Manual(SupervisionPolicyTests.Identity(Path.GetFullPath("host")));
        state = SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.EnterMaintenance);
        state = SupervisionPolicy.SetIntent(state, false, false, state.MaintenanceToken);
        state = SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.LeaveMaintenance, state.MaintenanceToken, state.Revision);
        Assert.False(state.Enabled);
        Assert.False(state.DesiredRunning);
        state = state with { Attempts = 3, Exhausted = true };
        state = SupervisionLifecycle.Transition(state, SupervisionLifecycleAction.Start);
        Assert.True(state.DesiredRunning);
        Assert.True(state.Exhausted);
        Assert.False(state.Enabled);
        Assert.Equal(3, state.Attempts);
    }

    [Fact]
    public async Task FailedWriteCannotAcknowledgeStopOrChangeCommittedIntent()
    {
        using var temp = new TempDirectory();
        var identity = SupervisionPolicyTests.Identity(temp.Path);
        var paths = new HostPaths(temp.Path);
        var project = ProjectSlug.Parse("demo");
        var store = new SupervisionStore(paths, project, identity);
        await store.SaveAsync(null, SupervisionPolicy.SetIntent(SupervisionState.Manual(identity), true, true, null), TestContext.Current.CancellationToken);
        var committedBytes = await File.ReadAllBytesAsync(store.Path, TestContext.Current.CancellationToken);
        using var held = new FileStream(Path.Combine(paths.SupervisionRoot(project), "writer.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new SupervisionLifecycle(store).ApplyAsync(SupervisionLifecycleAction.Stop,
            null, null, TestContext.Current.CancellationToken));
        Assert.Equal(SupervisionAvailability.Unavailable, store.Read().Availability);
        Assert.Equal(committedBytes, await File.ReadAllBytesAsync(store.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LifecycleCommandsSerializeWithLaunchAndRefuseContendingCommand()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.Path);
        var project = ProjectSlug.Parse("demo");
        using (BackgroundFileLock.AcquireStart(paths, project))
            Assert.Throws<IOException>(() => BackgroundFileLock.AcquireStart(paths, project));
        using var next = BackgroundFileLock.AcquireStart(paths, project);
    }

    [Theory]
    [InlineData("enter", (int)BackgroundAction.MaintenanceEnter)]
    [InlineData("status", (int)BackgroundAction.MaintenanceStatus)]
    [InlineData("leave", (int)BackgroundAction.MaintenanceLeave)]
    public void ParsesMaintenanceCommands(string operation, int expected)
    {
        var token = Guid.NewGuid().ToString("N");
        string[] args = operation == "leave"
            ? ["background", "maintenance", operation, "--project", "demo", "--token", token, "--revision", "42"]
            : ["background", "maintenance", operation, "--project", "demo"];
        var parsed = BackgroundCommandParser.Parse(args, out var help);
        Assert.False(help);
        Assert.Equal((BackgroundAction)expected, parsed.Action);
        if (operation == "leave") { Assert.Equal(token, parsed.MaintenanceToken); Assert.Equal(42, parsed.Revision); }
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--urls")]
    [InlineData("--token")]
    public void InvalidMaintenanceOptionsAreRejected(string option)
    {
        Assert.Throws<UsageException>(() => BackgroundCommandParser.Parse(
            ["background", "maintenance", "enter", "--project", "demo", option, "invalid"], out _));
        Assert.Throws<UsageException>(() => BackgroundCommandParser.Parse(
            ["background", "maintenance", "leave", "--project", "demo"], out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternalFailureDoesNotPersistManualStop(bool web)
    {
        using var shutdown = new RuntimeShutdownSignals();
        var persisted = 0;
        shutdown.PersistIntentionalStop = () => persisted++;
        var reason = web ? RuntimeShutdownReason.WebFailure : RuntimeShutdownReason.RuntimeFailure;
        shutdown.RequestGracefulStop(reason);
        Assert.Equal(reason, shutdown.Reason);
        Assert.True(shutdown.StopGate.IsStopRequested);
        Assert.Equal(0, persisted);
    }

    [Fact]
    public async Task SecondStopNeverWaitsForDirectIntentPersistence()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var exits = 0;
        using var shutdown = new RuntimeShutdownSignals(_ => Interlocked.Increment(ref exits));
        shutdown.PersistIntentionalStop = () => { entered.Set(); release.Wait(); };
        var first = Task.Run(shutdown.RequestStop, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            Assert.False(first.IsCompleted);
            Assert.True(shutdown.StopGate.IsStopRequested);
            Assert.False(shutdown.FirstStopToken.IsCancellationRequested);
            Assert.False(shutdown.StopGate.TryReserve(() => throw new InvalidOperationException("reservation admitted"), out bool _));
            using (ContinuousAgentLaunchContext.Enter(shutdown.StopGate))
                Assert.False(ContinuousAgentLaunchContext.TryLaunch(() => throw new InvalidOperationException("launch admitted")));
            shutdown.RequestStop();
            Assert.Equal(1, exits);
            Assert.Equal(RuntimeShutdownReason.ForcedStop, shutdown.Reason);
            Assert.True(shutdown.StopGate.IsForceStopRequested);
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public void DirectStopPersistsBeforeCancellationAndReportsFailureWithoutUndoingDrain()
    {
        using var shutdown = new RuntimeShutdownSignals();
        shutdown.PersistIntentionalStop = () =>
        {
            Assert.False(shutdown.FirstStopToken.IsCancellationRequested);
            throw new IOException("unavailable store");
        };
        shutdown.RequestStop();
        Assert.Equal("unavailable store", shutdown.IntentPersistenceFailure);
        Assert.True(shutdown.FirstStopToken.IsCancellationRequested);
        Assert.Equal(RuntimeShutdownReason.OperatorDrain, shutdown.Reason);
    }
}
