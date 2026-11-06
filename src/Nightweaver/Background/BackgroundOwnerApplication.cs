using System.Diagnostics;
using Nightweaver.Application;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

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

        lock (ProcessTreeJobGate)
            processTreeJob ??= WindowsProcessLauncher.AttachCurrentProcessToKillOnCloseJob();

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

        InvocationRuntimeFiles.PinnedRuntimeFile ownerLock;
        try { ownerLock = BackgroundFileLock.AcquireOwner(paths, projectSlug); }
        catch (IOException)
        {
            environment.Err.WriteLine("nightweaver: another background owner already holds this Project.");
            return ExitCodes.InspectionFailure;
        }

        using (ownerLock)
        {
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
                null, null, options.Urls, "starting", "starting", hostLogPath, webLogPath, null);
            var state = new BackgroundOwnerState(initial);
            try
            {
                await BackgroundOwnerRecord.WriteAsync(recordPath, state.Current, CancellationToken.None).ConfigureAwait(false);
                _ = BackgroundDeploymentManifest.Load(paths.Root);
                var projectConfig = ProjectConfigLoader.Load(options.ConfigPath, projectSlug, paths);
                if (!PathEquals(projectConfig.ConfigFilePath, options.ConfigPath))
                    throw new InvalidDataException("background owner config path did not resolve to the requested Project file");
                state.Update(record => record with { TargetRepository = projectConfig.TargetRepository.Path });
                await PersistAsync(recordPath, state).ConfigureAwait(false);
                ownerEnvironment.Out.WriteLine($"Background owner {options.RunId} starting Project '{projectSlug.Value}'.");

                using var serverLifetime = new CancellationTokenSource();
                BackgroundWebHost? webHost = null;
                var runtimeWasReady = false;
                var controlServer = BackgroundControlProtocol.RunServerAsync(pipeName,
                    (request, requestToken) => HandleRequestAsync(request, options, paths, projectConfig, state,
                        recordPath, () => webHost, requestToken),
                    serverLifetime.Token,
                    request => ApplyControlAction(request, options.RunId, shutdownSignals));
                _ = WatchControlServerAsync(controlServer, state, recordPath, shutdownSignals, serverLifetime.Token);
                Func<FactoryTelemetryPublisher, ProjectConfig, HostPaths, ValidatedTargetRepository,
                    CancellationToken, Task> onReady = async (telemetry, readyProject, readyPaths, repository, token) =>
                {
                    var manifest = BackgroundDeploymentManifest.Load(readyPaths.Root);
                    var startedWebHost = BackgroundWebHost.Start(manifest.ResolveWebExecutable(readyPaths.Root),
                        readyPaths.Root, readyProject.ConfigFilePath, options.Urls, webLogPath);
                    webHost = startedWebHost;
                    try
                    {
                        await startedWebHost.CheckReadyAsync(telemetry, readyProject.Name.Value, repository.Path, token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        await startedWebHost.DisposeAsync().ConfigureAwait(false);
                        webHost = null;
                        throw;
                    }

                    runtimeWasReady = true;
                    state.Update(record => record with
                    {
                        State = "running",
                        TargetRepository = repository.Path,
                        RuntimeId = telemetry.RuntimeId,
                        WebHealth = "healthy",
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
                    shutdownSignals, cancellationToken, onReady).ConfigureAwait(false);

                serverLifetime.Cancel();
                try { await controlServer.ConfigureAwait(false); }
                catch (OperationCanceledException) when (serverLifetime.IsCancellationRequested) { }
                if (webHost is not null) await webHost.DisposeAsync().ConfigureAwait(false);
                state.Update(record => record with
                {
                    State = runtimeCode == ExitCodes.Success ? "stopped" : runtimeWasReady ? "degraded" : "failed",
                    WebHealth = "stopped",
                    RuntimeHealth = runtimeCode == ExitCodes.Success ? "stopped" : "unknown",
                    Message = runtimeCode == ExitCodes.Success ? "manual stop completed" : "runtime exited with an inspection failure"
                });
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
                    Message = exception.Message
                });
                await PersistAsync(recordPath, state).ConfigureAwait(false);
                return ExitCodes.InspectionFailure;
            }
        }
    }

    private static async Task<BackgroundControlResponse> HandleRequestAsync(
        BackgroundControlRequest request, BackgroundOwnerOptions options, HostPaths paths,
        ProjectConfig project, BackgroundOwnerState state, string recordPath,
        Func<BackgroundWebHost?> webHostProvider, CancellationToken cancellationToken)
    {
        if (request.Operation == "status")
        {
            var record = await ObserveAsync(paths, project, state.Current, webHostProvider(), cancellationToken).ConfigureAwait(false);
            return new BackgroundControlResponse(true, record, null);
        }
        if (request.Operation is not ("stop" or "force") || request.RunId != options.RunId)
            return new BackgroundControlResponse(false, state.Current, "control request identity or operation is invalid");

        var stoppingRecord = state.Current with
        {
            State = "stopping",
            Message = request.Operation == "force" ? "force stop requested" : "graceful drain requested"
        };
        await BackgroundOwnerRecord.WriteAsync(recordPath, stoppingRecord, cancellationToken).ConfigureAwait(false);
        state.Update(_ => stoppingRecord);
        return new BackgroundControlResponse(true, stoppingRecord, null);
    }

    private static void ApplyControlAction(BackgroundControlRequest request, string runId,
        RuntimeShutdownSignals shutdownSignals)
    {
        if (request.Operation is not ("stop" or "force") || request.RunId != runId) return;
        if (request.Operation == "force")
        {
            shutdownSignals.RequestForceStop();
        }
        else
        {
            shutdownSignals.RequestGracefulStop();
        }
    }

    private static async Task<BackgroundOwnerRecord> ObserveAsync(HostPaths paths, ProjectConfig project,
        BackgroundOwnerRecord record, BackgroundWebHost? webHost, CancellationToken cancellationToken)
    {
        if (record.RuntimeId is null) return record;
        var runtime = new FactoryTelemetryReader(paths, project).Read();
        var runtimeHealth = runtime.Availability.ToString().ToLowerInvariant();
        var webHealthy = webHost is not null && await webHost.CheckHttpHealthAsync(record.RuntimeId,
            project.Name.Value, project.TargetRepository.Path, cancellationToken).ConfigureAwait(false);
        var webHealth = webHost is null || webHost.HasExited ? "stopped" : webHealthy ? "healthy" : "unhealthy";
        if (record.State == "running" && (!webHealthy || runtime.Availability != FactoryTelemetryAvailability.Live))
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
            record = record with { WebHealth = webHealth, RuntimeHealth = runtimeHealth };
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
            if (cancellationToken.IsCancellationRequested) return;
            state.Update(record => record with
            {
                State = "degraded",
                WebHealth = "stopped",
                RuntimeHealth = "stale",
                Message = $"Web Console exited unexpectedly with code {exitCode}; runtime is stopping and will require manual restart"
            });
            await BackgroundOwnerRecord.WriteAsync(recordPath, state.Current, CancellationToken.None).ConfigureAwait(false);
            shutdownSignals.RequestGracefulStop();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException) { }
    }

    private static async Task WatchControlServerAsync(Task serverTask, BackgroundOwnerState state,
        string recordPath, RuntimeShutdownSignals shutdownSignals, CancellationToken cancellationToken)
    {
        try
        {
            await serverTask.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
            state.Update(record => record with { State = "degraded", Message = "local control server stopped unexpectedly" });
            await BackgroundOwnerRecord.WriteAsync(recordPath, state.Current, CancellationToken.None).ConfigureAwait(false);
            shutdownSignals.RequestGracefulStop();
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
    }

    private static Task PersistAsync(string path, BackgroundOwnerState state) =>
        BackgroundOwnerRecord.WriteAsync(path, state.Current, CancellationToken.None);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
