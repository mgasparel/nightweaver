using System.Diagnostics;
using Nightweaver.Application;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;
using Nightweaver.Supervision;
using Nightweaver.Upgrade;

namespace Nightweaver.Background;

internal static class BackgroundOwnerApplication
{
    private static readonly object ProcessTreeJobGate = new();
    private static WindowsProcessLauncher.SafeKernelHandle? processTreeJob;

    internal static async Task<int> RunAsync(BackgroundOwnerOptions options, AppEnvironment environment,
        IProcessRunner processRunner, RuntimeShutdownSignals shutdownSignals, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            environment.Err.WriteLine("nightweaver: background owner requires Windows.");
            return ExitCodes.UsageOrConfiguration;
        }

        HostPaths paths;
        ProjectSlug projectSlug;
        try
        {
            paths = new HostPaths(options.Root);
            if (!ProjectSlug.TryParse(options.Project, out projectSlug, out var reason))
                throw new InvalidDataException($"invalid background Project slug: {reason}");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            environment.Err.WriteLine($"nightweaver: invalid background owner paths: {exception.Message}");
            return ExitCodes.UsageOrConfiguration;
        }

        using var scheduledCommand = new ConsoleStopCommand(options.ScheduledGeneration is null ? null
            : await AcquireScheduledCommandAsync(paths, projectSlug, cancellationToken).ConfigureAwait(false));
        SupervisionLifecycle? scheduledLifecycle = null;
        if (options.ScheduledGeneration is not null)
        {
            try
            {
                UpgradeInterlock.RequireStartupAllowed(paths);
                var project = ProjectConfigLoader.Load(options.ConfigPath, projectSlug, paths);
                scheduledLifecycle = await SupervisionLifecycle.ForInstallationAsync(paths, project, cancellationToken).ConfigureAwait(false);
                var executable = BackgroundDeploymentManifest.Load(paths.Root).ResolveCliExecutable(paths.Root);
                if (!PathEquals(Environment.ProcessPath!, executable))
                    throw new InvalidDataException("Scheduled owner must use the installed executable.");
                using var scheduler = new WindowsSupervisionScheduler();
                var definition = ScheduledTaskDefinition.Owner(scheduledLifecycle.Store.Identity, executable, projectSlug.Value);
                var registration = scheduler.Read(definition.Name) ?? throw new InvalidDataException("Scheduled owner task is absent.");
                definition.RequireOwned(scheduler, registration);
                if (!registration.Enabled) throw new InvalidDataException("Scheduled owner task is disabled.");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException
                or ProjectConfigurationException or TargetRepositoryException or ToolExecutionException or System.Runtime.InteropServices.COMException)
            {
                environment.Err.WriteLine($"nightweaver: scheduled owner request rejected: {exception.Message}");
                return ExitCodes.InspectionFailure;
            }
        }
        InvocationRuntimeFiles.PinnedRuntimeFile ownerLock;
        try { ownerLock = BackgroundFileLock.AcquireOwner(paths, projectSlug); }
        catch (IOException)
        {
            environment.Err.WriteLine("nightweaver: another background owner already holds this Project.");
            return ExitCodes.InspectionFailure;
        }

        using (ownerLock)
        {
            if (scheduledLifecycle is not null)
            {
                try
                {
                    var previous = scheduledLifecycle.Read().State?.PendingAttempt?.PreviousOwner;
                    var retained = BackgroundOwnerRecord.TryRead(BackgroundOwnerRecord.PathFor(paths, projectSlug));
                    if (options.Urls != (retained?.WebUrl ?? "http://127.0.0.1:5000"))
                        throw new InvalidDataException("Scheduled owner URL does not match the retained owner.");
                    if (previous is null ? retained is not null
                        : retained is null || previous.ProcessId != retained.ProcessId
                            || previous.StartedUtc != retained.ProcessStartedAtUtc || previous.OwnerId != retained.RunId
                            || previous.RuntimeId != retained.RuntimeId
                            || BackgroundContainment.Read(retained) is not { State: OwnerProcessState.Exited, DescendantsSettled: true })
                        throw new InvalidDataException("Previous owner identity or Job settlement changed before scheduled launch.");
                    await ScheduledOwnerLaunch.ConsumeAsync(scheduledLifecycle.Store, options.RunId,
                        options.ScheduledGeneration!.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    environment.Err.WriteLine($"nightweaver: scheduled owner request rejected: {exception.Message}");
                    return ExitCodes.InspectionFailure;
                }
            }
            var backgroundRoot = paths.BackgroundRoot(projectSlug);
            var hostLogPath = Path.Combine(paths.LogsRoot(projectSlug), "background-host.log");
            var webLogPath = Path.Combine(paths.LogsRoot(projectSlug), "background-web.log");
            using var hostLog = InvocationRuntimeFiles.OpenAppendRuntimeLog(hostLogPath);
            await using var logWriterBase = new StreamWriter(hostLog.Stream, leaveOpen: true) { AutoFlush = true };
            var logWriter = TextWriter.Synchronized(logWriterBase);
            var ownerEnvironment = environment with { NightweaverRoot = paths.Root, Out = logWriter, Err = logWriter };
            var recordPath = BackgroundOwnerRecord.PathFor(paths, projectSlug);
            var pipeName = BackgroundIdentity.PipeName(paths, projectSlug);
            var currentProcess = Process.GetCurrentProcess();
            var initial = new BackgroundOwnerRecord(1, "starting", options.RunId, currentProcess.Id,
                currentProcess.StartTime.ToUniversalTime(), paths.Root, options.ConfigPath, projectSlug.Value,
                null, null, options.Urls, "starting", "starting", hostLogPath, webLogPath, null)
                { ContainmentVersion = 1 };
            lock (ProcessTreeJobGate)
                processTreeJob ??= WindowsProcessLauncher.AttachCurrentProcessToKillOnCloseJob(BackgroundContainment.Name(initial));
            var state = new BackgroundOwnerState(initial);
            try
            {
                await BackgroundOwnerRecord.WriteAsync(recordPath, state.Current, CancellationToken.None).ConfigureAwait(false);
                _ = BackgroundDeploymentManifest.Load(paths.Root);
                state.Update(record => record with { CliRelease = LiveReleaseIdentity.Capture(paths.Root,
                    options.ConfigPath, projectSlug.Value, typeof(BackgroundOwnerApplication).Assembly, "nightweaver.dll") });
                var projectConfig = ProjectConfigLoader.Load(options.ConfigPath, projectSlug, paths);
                if (!PathEquals(projectConfig.ConfigFilePath, options.ConfigPath))
                    throw new InvalidDataException("background owner config path did not resolve to the requested Project file");
                var lifecycle = await InitializeLifecycleAsync(shutdownSignals,
                    token => SupervisionLifecycle.OpenAsync(paths, projectConfig, token), lifecycle =>
                {
                    try
                    {
                        scheduledCommand.Persist(paths, projectSlug, lifecycle, state, recordPath);
                    }
                    catch (Exception)
                    {
                        state.Update(record => record with { State = "stopping", StopReason = "supervision-unavailable",
                            Message = "Direct stop intent could not be persisted; supervision unavailable. Inspect retained state before recovery." });
                        PersistAsync(recordPath, state).GetAwaiter().GetResult();
                        throw;
                    }
                }).ConfigureAwait(false);
                state.Update(record => record with { TargetRepository = projectConfig.TargetRepository.Path });
                await PersistAsync(recordPath, state).ConfigureAwait(false);
                ownerEnvironment.Out.WriteLine($"Background owner {options.RunId} starting Project '{projectSlug.Value}'.");

                using var serverLifetime = new CancellationTokenSource();
                BackgroundWebHost? webHost = null;
                var runtimeWasReady = false;
                var controlServer = BackgroundControlProtocol.RunServerAsync(pipeName,
                    (request, requestToken) => HandleRequestAsync(request, options, paths, projectConfig, state,
                        recordPath, () => webHost, lifecycle, requestToken),
                    serverLifetime.Token,
                    request => ApplyControlAction(request, options.RunId, shutdownSignals));
                _ = WatchControlServerAsync(controlServer, state, recordPath, shutdownSignals, serverLifetime.Token);
                scheduledCommand.Dispose();
                Func<FactoryTelemetryPublisher, ProjectConfig, HostPaths, ValidatedTargetRepository,
                    CancellationToken, Task> onReady = async (telemetry, readyProject, readyPaths, repository, token) =>
                {
                    var manifest = BackgroundDeploymentManifest.Load(readyPaths.Root);
                    state.Update(record => record with { RuntimeId = telemetry.RuntimeId,
                        TargetRepository = repository.Path, GitCommonDirectory = repository.GitCommonDirectory });
                    await PersistAsync(recordPath, state).ConfigureAwait(false);
                    var startedWebHost = BackgroundWebHost.Start(manifest.ResolveWebExecutable(readyPaths.Root),
                        readyPaths.Root, readyProject.ConfigFilePath, options.Urls, webLogPath);
                    webHost = startedWebHost;
                    await CheckWebReadinessAsync(
                        readyToken => startedWebHost.CheckReadyAsync(telemetry, readyProject.Name.Value, repository.Path, readyToken),
                        async () =>
                        {
                            try { await startedWebHost.DisposeAsync().ConfigureAwait(false); }
                            finally { webHost = null; }
                        }, state, recordPath, token).ConfigureAwait(false);

                    runtimeWasReady = true;
                    state.Update(record => record with
                    {
                        State = "running",
                        TargetRepository = repository.Path,
                        RuntimeId = telemetry.RuntimeId,
                        GitCommonDirectory = repository.GitCommonDirectory,
                        WebHealth = "healthy",
                        WebRelease = startedWebHost.Release,
                        RuntimeHealth = "live",
                        Message = null
                    });
                    await PersistAsync(recordPath, state).ConfigureAwait(false);
                    ownerEnvironment.Out.WriteLine($"Background owner {options.RunId} is ready at {options.Urls} for runtime {telemetry.RuntimeId}.");
                    _ = WatchWebHostAsync(startedWebHost, shutdownSignals, state, recordPath, cancellationToken);
                };

                var selector = new ProjectSelector.ByConfigPath(projectConfig.ConfigFilePath);
                var runtimeCode = await ContinuousApplication.RunAsync(
                    new CliOptions(selector, DryRun: false, Help: false), ownerEnvironment, processRunner,
                    shutdownSignals, cancellationToken, onReady,
                    () => RetainFailureAsync(state, recordPath, "RuntimeFailure")).ConfigureAwait(false);

                if (runtimeCode != ExitCodes.Success && state.Current.Failure is null
                    && !DeploymentObserver.IsDeliberate(state.Current.StopReason))
                    await RetainFailureAsync(state, recordPath, "RuntimeFailure").ConfigureAwait(false);

                serverLifetime.Cancel();
                try { await controlServer.ConfigureAwait(false); }
                catch (OperationCanceledException) when (serverLifetime.IsCancellationRequested) { }
                if (webHost is not null) await webHost.DisposeAsync().ConfigureAwait(false);
                state.Update(record => record with
                {
                    State = shutdownSignals.Reason is RuntimeShutdownReason.WebFailure or RuntimeShutdownReason.RuntimeFailure
                        ? "degraded" : runtimeCode == ExitCodes.Success ? "stopped" : runtimeWasReady ? "degraded" : "failed",
                    StopReason = shutdownSignals.IntentPersistenceFailure is not null || record.StopReason == "supervision-unavailable"
                        ? "supervision-unavailable" : (shutdownSignals.Reason == RuntimeShutdownReason.None ? RuntimeShutdownReason.RuntimeFailure : shutdownSignals.Reason).ToString(),
                    WebHealth = "stopped",
                    RuntimeHealth = runtimeCode == ExitCodes.Success ? "stopped" : "unknown",
                    Message = shutdownSignals.IntentPersistenceFailure ?? $"owner exited: {shutdownSignals.Reason}; runtime code {runtimeCode}"
                });
                if (state.Current.Failure is null && shutdownSignals.Reason is RuntimeShutdownReason.WebFailure or RuntimeShutdownReason.RuntimeFailure)
                    await RetainFailureAsync(state, recordPath, "RuntimeFailure").ConfigureAwait(false);
                await PersistAsync(recordPath, state).ConfigureAwait(false);
                ownerEnvironment.Out.WriteLine($"Background owner {options.RunId} exited with code {runtimeCode}.");
                return runtimeCode;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException or NotSupportedException or InvalidOperationException
                or ProjectConfigurationException or TargetRepositoryException or ToolExecutionException
                or TimeoutException)
            {
                ownerEnvironment.Err.WriteLine($"Background owner {options.RunId} failed: {exception.GetType().Name}: {exception.Message}");
                state.Update(record => record with
                {
                    State = "failed",
                    WebHealth = "unavailable",
                    RuntimeHealth = "unavailable",
                    StopReason = shutdownSignals.Reason == RuntimeShutdownReason.OperatorDrain
                        ? "supervision-unavailable" : record.StopReason,
                    Message = exception.Message
                });
                if (state.Current.Failure is null && shutdownSignals.Reason != RuntimeShutdownReason.OperatorDrain)
                    await RetainFailureAsync(state, recordPath, "RuntimeFailure").ConfigureAwait(false);
                await PersistAsync(recordPath, state).ConfigureAwait(false);
                return ExitCodes.InspectionFailure;
            }
        }
    }

    internal static async Task<IDisposable> AcquireScheduledCommandAsync(HostPaths paths, ProjectSlug project, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(SupervisionPolicy.ReconciliationBudget);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return BackgroundFileLock.AcquireStart(paths, project); }
            catch (IOException exception) when (SharingViolation(exception))
            { await Task.Delay(25, deadline.Token).ConfigureAwait(false); }
        }
    }

    internal static async Task CheckWebReadinessAsync(Func<CancellationToken, Task> checkReady,
        Func<ValueTask> disposeWeb, BackgroundOwnerState state, string recordPath, CancellationToken token)
    {
        try { await checkReady(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Caller shutdown is deliberate; keep its intent and existing incident unchanged.
            await disposeWeb().ConfigureAwait(false);
            throw;
        }
        catch
        {
            try { await RetainFailureAsync(state, recordPath, "WebFailure").ConfigureAwait(false); }
            finally { await disposeWeb().ConfigureAwait(false); }
            throw;
        }
    }

    // Serialize startup lock release with the console callback. A callback that
    // starts before release borrows that lock; later callbacks acquire their own.
    internal sealed class ConsoleStopCommand(IDisposable? startupCommand) : IDisposable
    {
        private readonly object gate = new();
        private IDisposable? command = startupCommand;

        internal void Persist(HostPaths paths, ProjectSlug project, SupervisionLifecycle? lifecycle,
            BackgroundOwnerState state, string recordPath)
        {
            lock (gate)
                PersistConsoleStopAsync(paths, project, lifecycle, state, recordPath, command is not null)
                    .GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            lock (gate)
            {
                command?.Dispose();
                command = null;
            }
        }
    }

    internal static Task PersistConsoleStopAsync(HostPaths paths, ProjectSlug project, SupervisionLifecycle? lifecycle,
        BackgroundOwnerState state, string recordPath) =>
        PersistConsoleStopAsync(paths, project, lifecycle, state, recordPath, commandAlreadyHeld: false);

    private static async Task PersistConsoleStopAsync(HostPaths paths, ProjectSlug project, SupervisionLifecycle? lifecycle,
        BackgroundOwnerState state, string recordPath, bool commandAlreadyHeld)
    {
        // A direct console stop uses the same order as manager commands. Wait only
        // for sharing contention. The independent second signal still exits at once.
        using var command = commandAlreadyHeld ? null
            : await AcquireConsoleStopCommandAsync(paths, project).ConfigureAwait(false);
        while (true)
        {
            try
            {
                await UpgradeInterlock.RecordStopAsync(paths, project, CancellationToken.None).ConfigureAwait(false);
                break;
            }
            catch (IOException exception) when (SharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }
        if (lifecycle is not null)
            await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, CancellationToken.None).ConfigureAwait(false);
        state.Update(record => record with { State = "stopping", StopReason = "OperatorDrain",
            Message = "Intentional console drain requested" });
        await PersistAsync(recordPath, state).ConfigureAwait(false);
    }

    private static async Task<IDisposable> AcquireConsoleStopCommandAsync(HostPaths paths, ProjectSlug project)
    {
        while (true)
        {
            try { return BackgroundFileLock.AcquireStart(paths, project); }
            catch (IOException exception) when (SharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            }
        }
    }

    private static bool SharingViolation(IOException exception) => (exception.HResult & 0xffff) is 32 or 33
        || exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 };

    internal static async Task<SupervisionLifecycle?> InitializeLifecycleAsync(RuntimeShutdownSignals shutdownSignals,
        Func<CancellationToken, Task<SupervisionLifecycle?>> openLifecycle, Action<SupervisionLifecycle?> persistStop)
    {
        // Identity validation must settle even when a console stop arrived before registration.
        // The callback setter reconciles that retained stop before startup can observe cancellation.
        var lifecycle = await openLifecycle(CancellationToken.None).ConfigureAwait(false);
        shutdownSignals.PersistIntentionalStop = () => persistStop(lifecycle);
        return lifecycle;
    }

    internal static async Task<BackgroundControlResponse> HandleRequestAsync(
        BackgroundControlRequest request, BackgroundOwnerOptions options, HostPaths paths,
        ProjectConfig project, BackgroundOwnerState state, string recordPath,
        Func<BackgroundWebHost?> webHostProvider, SupervisionLifecycle? lifecycle, CancellationToken cancellationToken)
    {
        if (request.Operation == "status")
        {
            var record = await ObserveAsync(paths, project, state.Current, webHostProvider(), cancellationToken).ConfigureAwait(false);
            return new BackgroundControlResponse(true, record, null);
        }
        if (request.Operation is not ("stop" or "force" or "recover" or "upgrade") || request.RunId != options.RunId)
            return new BackgroundControlResponse(false, state.Current, "control request identity or operation is invalid");

        if (request.Operation == "recover")
        {
            // Failure recovery preserves operator intent and never turns an intentional
            // drain into a force deadline. Only an already recorded failure can drain.
            if (lifecycle?.Read().State is not { Suppressed: false }
                || state.Current.Failure is null || DeploymentObserver.IsDeliberate(state.Current.StopReason)
                || request.Force || request.Maintenance)
                return new(false, state.Current, "failure recovery is suppressed or unconfirmed");
            var accepted = false;
            await state.PublishAsync(recordPath, record =>
            {
                accepted = record.Failure is not null && !DeploymentObserver.IsDeliberate(record.StopReason)
                    && lifecycle.Read().State is { Suppressed: false };
                return accepted ? record with { State = "stopping" } : record;
            }, cancellationToken).ConfigureAwait(false);
            return new(accepted, state.Current, accepted ? null : "Owner intent changed before failure drain.");
        }

        try
        {
            if (request.Operation == "upgrade")
            {
                if (request.UpgradeOperationId is null || request.Force || !request.Maintenance)
                    throw new InvalidOperationException("Upgrade drain requires current operation authority.");
                UpgradeInterlock.RequireDrain(paths, request.UpgradeOperationId);
                var read = lifecycle?.Read();
                if (read?.Availability == SupervisionAvailability.Unavailable
                    || read?.State is { } installed && (installed.MaintenanceToken is null
                        || installed.MaintenanceToken != request.MaintenanceToken))
                    throw new InvalidOperationException("Upgrade maintenance token changed.");
            }
            if (lifecycle is not null)
            {
                if (request.Maintenance)
                {
                    if (!(request.Operation == "upgrade" && lifecycle.Read().Availability == SupervisionAvailability.Manual)
                        && lifecycle.Read().State?.MaintenanceToken is null)
                        throw new InvalidOperationException("Maintenance suppression is not confirmed.");
                }
                else if (lifecycle.Read().State is not { DesiredRunning: false })
                    await lifecycle.ApplyAsync(SupervisionLifecycleAction.Stop, null, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, state.Current with { StopReason = "supervision-unavailable" }, exception.Message);
        }
        await state.PublishAsync(recordPath, record => record with
        {
            State = "stopping",
            StopReason = request.Maintenance ? "Maintenance" : request.Operation == "force" ? "ForcedStop" : "OperatorDrain",
            Message = request.Operation == "force" ? "force stop requested" : "graceful drain requested"
        }, cancellationToken).ConfigureAwait(false);
        return new BackgroundControlResponse(true, state.Current, null);
    }

    internal static void ApplyControlAction(BackgroundControlRequest request, string runId,
        RuntimeShutdownSignals shutdownSignals)
    {
        if (request.Operation is not ("stop" or "force" or "recover" or "upgrade") || request.RunId != runId) return;
        if (request.Operation == "recover")
        {
            shutdownSignals.RequestGracefulStop(RuntimeShutdownReason.RuntimeFailure);
            return;
        }
        if (request.Operation == "force")
        {
            shutdownSignals.RequestForceStop();
        }
        else
        {
            shutdownSignals.RequestGracefulStop(request.Maintenance ? RuntimeShutdownReason.Maintenance : RuntimeShutdownReason.OperatorDrain, intentAlreadyPersisted: true);
        }
    }

    private static async Task<BackgroundOwnerRecord> ObserveAsync(HostPaths paths, ProjectConfig project,
        BackgroundOwnerRecord record, BackgroundWebHost? webHost, CancellationToken cancellationToken)
    {
        record = record with { ObservedAtUtc = DateTimeOffset.UtcNow };
        if (record.RuntimeId is null) return record;
        var runtime = new FactoryTelemetryReader(paths, project).Read();
        var runtimeHealth = runtime.Availability.ToString().ToLowerInvariant();
        var webHealthy = webHost is not null && await webHost.CheckHttpHealthAsync(record.RuntimeId,
            project.Name.Value, project.TargetRepository.Path, cancellationToken).ConfigureAwait(false);
        var webHealth = webHost is null || webHost.HasExited ? "stopped" : webHealthy ? "healthy" : "unhealthy";
        if (runtime.Snapshot?.RuntimeId != record.RuntimeId) runtimeHealth = "mismatched";
        if (record.State == "running" && (!webHealthy || runtime.Availability != FactoryTelemetryAvailability.Live
            || runtime.Snapshot?.RuntimeId != record.RuntimeId))
        {
            record = record with
            {
                State = "degraded",
                WebHealth = webHealth,
                RuntimeHealth = runtimeHealth,
                Message = "Web or runtime health check no longer confirms the active deployment"
            };
        }
        else
        {
            record = record with { WebHealth = webHealth, RuntimeHealth = runtimeHealth, WebRelease = webHost?.Release };
        }
        return record;
    }

    private static async Task WatchWebHostAsync(BackgroundWebHost webHost,
        RuntimeShutdownSignals shutdownSignals, BackgroundOwnerState state, string recordPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await webHost.WaitForExitAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || shutdownSignals.Reason != RuntimeShutdownReason.None) return;
            state.Update(record => record with
            {
                State = "degraded",
                WebHealth = "stopped",
                StopReason = "WebFailure",
                RuntimeHealth = "stale",
                Message = $"Web Console exited unexpectedly with code {exitCode}; runtime is stopping and will require manual restart"
            });
            await RetainFailureAsync(state, recordPath, "WebFailure").ConfigureAwait(false);
            shutdownSignals.RequestGracefulStop(RuntimeShutdownReason.WebFailure);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException) { }
    }

    internal static async Task WatchControlServerAsync(Task serverTask, BackgroundOwnerState state,
        string recordPath, RuntimeShutdownSignals shutdownSignals, CancellationToken cancellationToken)
    {
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { }
        if (cancellationToken.IsCancellationRequested || shutdownSignals.Reason != RuntimeShutdownReason.None) return;
        state.Update(record => record with { State = "degraded", StopReason = "RuntimeFailure", Message = "local control server stopped unexpectedly" });
        await RetainFailureAsync(state, recordPath, "RuntimeFailure").ConfigureAwait(false);
        shutdownSignals.RequestGracefulStop(RuntimeShutdownReason.RuntimeFailure);
    }

    private static Task PersistAsync(string path, BackgroundOwnerState state) =>
        state.PersistAsync(path, CancellationToken.None);

    internal static async Task RetainFailureAsync(BackgroundOwnerState state, string recordPath, string reason)
    {
        state.Update(record => record with { Failure = record.Failure ?? new(DateTimeOffset.UtcNow, reason,
            record.RunId, record.RuntimeId, record.ProcessId, record.ProcessStartedAtUtc) });
        // Keep the first failing runtime even when successful drain later reports stopped.
        // Failure publication precedes the request to drain or Web disposal.
        try
        {
            await PersistAsync(recordPath, state).ConfigureAwait(false);
            await DeploymentEvidenceStore.RetainOwnerFailureAsync(state.Current).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException)
        {
            state.Update(record => record with { StopReason = "supervision-unavailable",
                Message = "Failure evidence persistence unavailable; inspect retained owner and logs before recovery." });
            await PersistAsync(recordPath, state).ConfigureAwait(false);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
