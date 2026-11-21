using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Supervision;
using Nightweaver.Inspection;

namespace Nightweaver.Background;

internal static class BackgroundManagerApplication
{
    private static readonly TimeSpan OwnerStartTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OwnerStopTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal static async Task<int> RunAsync(BackgroundCommandOptions options, AppEnvironment environment,
        CancellationToken cancellationToken,
        Func<HostPaths, ProjectConfig, CancellationToken, Task<SupervisionLifecycle?>>? openLifecycle = null,
        Func<SupervisionLifecycle, CancellationToken, Task<SupervisionTick>>? createTick = null)
    {
        using var tickDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Action == BackgroundAction.Tick) tickDeadline.CancelAfter(SupervisionPolicy.TickDeadline);
        cancellationToken = tickDeadline.Token;
        if (!OperatingSystem.IsWindows())
        {
            environment.Err.WriteLine("nightweaver: background deployment management currently requires Windows.");
            return ExitCodes.UsageOrConfiguration;
        }

        HostPaths paths;
        ProjectConfig project;
        string configPath;
        try
        {
            paths = new HostPaths(environment.NightweaverRoot);
            configPath = ProjectConfigLocator.ResolveConfigPath(options.Selector!, paths, environment.CurrentDirectory);
            var expectedName = options.Selector is ProjectSelector.BySlug bySlug ? bySlug.Slug : (ProjectSlug?)null;
            project = ProjectConfigLoader.Load(configPath, expectedName, paths);
            configPath = project.ConfigFilePath;
        }
        catch (ProjectConfigurationException exception)
        {
            foreach (var error in exception.Errors)
                environment.Err.WriteLine($"nightweaver: error: {exception.ConfigFilePath}: {error.JsonPath}: {error.Message}");
            return ExitCodes.UsageOrConfiguration;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or PathTooLongException)
        {
            environment.Err.WriteLine($"nightweaver: background configuration error: {exception.Message}");
            return ExitCodes.UsageOrConfiguration;
        }

        var pipeName = BackgroundIdentity.PipeName(paths, project.Name);
        var recordPath = BackgroundOwnerRecord.PathFor(paths, project.Name);
        try
        {
            // Future recovery launchers must hold this same lock through final intent check and launch.
            using var commandLock = options.Action is BackgroundAction.Status or BackgroundAction.MaintenanceStatus or BackgroundAction.SupervisionStatus
                ? null : BackgroundFileLock.AcquireStart(paths, project.Name);
            if (options.Action is BackgroundAction.SupervisionStatus or BackgroundAction.SupervisionInstall
                or BackgroundAction.SupervisionEnable or BackgroundAction.SupervisionDisable or BackgroundAction.SupervisionUninstall)
            {
                var installation = await SupervisionLifecycle.ForInstallationAsync(paths, project, cancellationToken).ConfigureAwait(false);
                var manifest = BackgroundDeploymentManifest.Load(paths.Root);
                var executable = manifest.ResolveCliExecutable(paths.Root);
                using var rootPins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(paths.Root);
                using var executablePins = InvocationRuntimeFiles.PinExistingRuntimeDirectory(Path.GetDirectoryName(executable)!);
                _ = InvocationRuntimeFiles.ReadRuntimeFileBounded(executable, 32 * 1024 * 1024);
                var definition = ScheduledTaskDefinition.Build(installation.Store.Identity, executable,
                    ["background", "tick", "--config", configPath], paths.Root);
                using var scheduler = new WindowsSupervisionScheduler();
                var action = options.Action switch
                {
                    BackgroundAction.SupervisionInstall => ScheduledSupervisionAction.Install,
                    BackgroundAction.SupervisionEnable => ScheduledSupervisionAction.Enable,
                    BackgroundAction.SupervisionDisable => ScheduledSupervisionAction.Disable,
                    BackgroundAction.SupervisionUninstall => ScheduledSupervisionAction.Uninstall,
                    _ => ScheduledSupervisionAction.Status
                };
                var status = await new ScheduledSupervision(installation.Store, installation.Store.Identity, definition, scheduler)
                    .ApplyAsync(action, cancellationToken).ConfigureAwait(false);
                environment.Out.WriteLine(JsonSerializer.Serialize(status, OutputJson));
                return ExitCodes.Success;
            }
            var lifecycle = await (openLifecycle ?? SupervisionLifecycle.OpenAsync)(paths, project, cancellationToken).ConfigureAwait(false);
            if (options.Action == BackgroundAction.Tick)
            {
                if (lifecycle is null)
                {
                    environment.Out.WriteLine("{\"outcome\":\"manual\"}");
                    return ExitCodes.Success;
                }
                var read = lifecycle.Read();
                if (read.Availability == SupervisionAvailability.Manual)
                {
                    environment.Out.WriteLine("{\"outcome\":\"manual\"}");
                    return ExitCodes.Success;
                }
                if (read.State is not { } installed)
                    throw new InvalidDataException($"Supervision unavailable at '{read.Path}': {read.Diagnostic}");
                if (installed.Suppressed && installed.PendingAttempt is null)
                {
                    environment.Out.WriteLine(JsonSerializer.Serialize(new SupervisionTickResult("suppressed", installed), OutputJson));
                    return ExitCodes.Success;
                }
                async Task<SupervisionTick> CreateProductionTickAsync(SupervisionLifecycle currentLifecycle, CancellationToken tickToken)
                {
                    var epoch = await ReadTickEpochAsync(tickToken).ConfigureAwait(false);
                    var observer = DeploymentObserver.Create(paths, project, installed.Identity.GitCommonDirectory,
                        currentLifecycle.Read, epoch);
                    var identity = new DeploymentIdentity(paths.Root, configPath, project.Name.Value,
                        project.TargetRepository.Path, installed.Identity.GitCommonDirectory);
                    var evidence = new DeploymentEvidenceStore(paths, project.Name, identity);
                    var recorder = new DeploymentIncidentRecorder(paths, project.Name, evidence,
                        new ResourceIncidentCapture(paths, project, new SystemProcessRunner()).CaptureAsync);
                    return new SupervisionTick(new(currentLifecycle.Read,
                        (revision, next, token) => currentLifecycle.Store.SaveAsync(revision, next, token),
                        async token =>
                        {
                            var observation = await observer.ObserveAsync(token).ConfigureAwait(false);
                            await evidence.RetainAsync(observation, null, token).ConfigureAwait(false);
                            return observation;
                        },
                        // The journal retains required failed/pending resource captures from
                        // prior observations. Owner failure alone is not resource-leak evidence.
                        (observation, attempts, token) => recorder.PrepareAsync(observation, attempts,
                            ResourceIncidentSignal.None, token),
                        async (observation, token) =>
                        {
                            var response = await BackgroundControlProtocol.SendAsync(pipeName,
                                new("recover", observation.Owner!.RunId), SupervisionPolicy.ProbeBudget, token).ConfigureAwait(false);
                            return response.Accepted && response.Owner is { } owner
                                && DeploymentObserver.SameOwner(observation.Owner, owner);
                        },
                        async (attempt, permit, token) =>
                        {
                            await StartAsync(paths, project, configPath,
                                BackgroundOwnerRecord.TryRead(recordPath)?.WebUrl ?? "http://127.0.0.1:5000",
                                pipeName, recordPath, environment, token, attempt, permit).ConfigureAwait(false);
                        }));
                }
                var tick = await (createTick ?? CreateProductionTickAsync)(lifecycle, cancellationToken).ConfigureAwait(false);
                var result = await tick.RunAsync(cancellationToken).ConfigureAwait(false);
                environment.Out.WriteLine(JsonSerializer.Serialize(result, OutputJson));
                return result.Outcome is "needs-attention" or "capture-blocked" or "settlement-unknown" or "drain-unconfirmed"
                    ? ExitCodes.InspectionFailure : ExitCodes.Success;
            }
            if (options.Action is BackgroundAction.Status or BackgroundAction.MaintenanceStatus)
            {
                var supervision = lifecycle?.Read() ?? new SupervisionRead(SupervisionAvailability.Manual, null,
                    Path.Combine(paths.SupervisionRoot(project.Name), "state.json"), null);
                if (options.Action == BackgroundAction.MaintenanceStatus)
                {
                    environment.Out.WriteLine(JsonSerializer.Serialize(supervision, OutputJson));
                    return supervision?.Availability == SupervisionAvailability.Unavailable ? ExitCodes.InspectionFailure : ExitCodes.Success;
                }
                var query = await QueryAsync(paths, project, configPath, pipeName, recordPath,
                    cancellationToken).ConfigureAwait(false);
                var observation = await ObserveStatusAsync(paths, project, query, lifecycle, supervision,
                    cancellationToken).ConfigureAwait(false);
                return WriteStatus(query, environment, supervision, observation);
            }
            if (options.Action == BackgroundAction.MaintenanceLeave)
            {
                if (lifecycle is null) throw new InvalidOperationException("No supervision installation; maintenance is unavailable.");
                var released = await lifecycle.ApplyAsync(SupervisionLifecycleAction.LeaveMaintenance,
                    options.MaintenanceToken, options.Revision, cancellationToken).ConfigureAwait(false);
                environment.Out.WriteLine(JsonSerializer.Serialize(released, OutputJson));
                return ExitCodes.Success;
            }
            if (options.Action == BackgroundAction.MaintenanceEnter)
            {
                if (lifecycle is null) throw new InvalidOperationException("No supervision installation; maintenance is unavailable.");
                var entered = await lifecycle.ApplyAsync(SupervisionLifecycleAction.EnterMaintenance, null, null,
                    cancellationToken).ConfigureAwait(false);
                return await StopAsync(paths, project, configPath, pipeName, recordPath, false,
                    environment, cancellationToken, maintenance: true, releaseCommand: commandLock!.Dispose, supervision: entered).ConfigureAwait(false);
            }
            SupervisionRead? intent = null;
            if (lifecycle is not null)
                intent = await lifecycle.ApplyAsync(options.Action is BackgroundAction.Stop or BackgroundAction.Restart
                    ? SupervisionLifecycleAction.Stop : SupervisionLifecycleAction.Start,
                    null, null, cancellationToken).ConfigureAwait(false);
            switch (options.Action)
            {
                case BackgroundAction.Stop:
                    return await StopAsync(paths, project, configPath, pipeName, recordPath, options.Force,
                        environment, cancellationToken, releaseCommand: commandLock!.Dispose, supervision: intent).ConfigureAwait(false);
                case BackgroundAction.Start:
                    return await StartAsync(paths, project, configPath, options.Urls ?? "http://127.0.0.1:5000",
                        pipeName, recordPath, environment, cancellationToken).ConfigureAwait(false);
                case BackgroundAction.Restart:
                {
                    var stopped = await StopAsync(paths, project, configPath, pipeName, recordPath, false,
                        environment, cancellationToken, releaseCommand: commandLock!.Dispose).ConfigureAwait(false);
                    if (stopped != ExitCodes.Success || BackgroundFileLock.IsOwnerActive(paths, project.Name))
                        return ExitCodes.InspectionFailure;
                    using var restartLock = BackgroundFileLock.AcquireStart(paths, project.Name);
                    if (lifecycle is not null && lifecycle.Read().State?.Generation != intent?.State?.Generation)
                        throw new InvalidOperationException("Intent changed during restart drain; no owner launched.");
                    if (lifecycle is not null)
                        await lifecycle.ApplyAsync(SupervisionLifecycleAction.Start, null, null, cancellationToken).ConfigureAwait(false);
                    return await RestartAsync(paths, project, configPath, options.Urls, pipeName, recordPath,
                        environment, cancellationToken).ConfigureAwait(false);
                }
                default: return ExitCodes.UsageOrConfiguration;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or TargetRepositoryException or ToolExecutionException or ProjectConfigurationException
            or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            environment.Err.WriteLine($"nightweaver: lifecycle command unavailable; inspect retained intent and scheduler status: {exception.Message}");
            return ExitCodes.InspectionFailure;
        }
    }

    private static async Task<int> StartAsync(HostPaths paths, ProjectConfig project, string configPath,
        string urls, string pipeName, string recordPath, AppEnvironment environment,
        CancellationToken cancellationToken, string? reservedRunId = null,
        Func<CancellationToken, Task<bool>>? permitLaunch = null)
    {
        BackgroundDeploymentManifest manifest;
        try { manifest = BackgroundDeploymentManifest.Load(paths.Root); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            environment.Err.WriteLine($"nightweaver: background deployment is incomplete: {exception.Message}");
            return ExitCodes.InspectionFailure;
        }

        {
            var existing = await QueryAsync(paths, project, configPath, pipeName, recordPath,
                cancellationToken).ConfigureAwait(false);
            if (existing.Active)
            {
                if (!SameConfig(existing.Record!, configPath))
                {
                    environment.Err.WriteLine("nightweaver: a background runtime for this Project is active with a different config path; no process was changed.");
                    return ExitCodes.InspectionFailure;
                }
                return WriteStatus(existing, environment);
            }
            if (existing.OwnerLockActive)
            {
                environment.Err.WriteLine("nightweaver: an owner is active but its status endpoint is unavailable; inspect status and logs before retrying.");
                return WriteStatus(existing with { Failure = "owner lock is active but control is unavailable" }, environment);
            }

            var runId = reservedRunId ?? Guid.NewGuid().ToString("N");
            try
            {
                var executable = manifest.ResolveCliExecutable(paths.Root);
                var ownerArguments = BuildOwnerStartArguments(paths.Root, configPath, project.Name.Value, runId, urls);
                if (permitLaunch is not null && !await permitLaunch(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Recovery launch authority changed; reservation retained.");
                using var process = WindowsProcessLauncher.LaunchDetached(executable, ownerArguments, paths.Root);
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < OwnerStartTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = await TryQueryPipeAsync(pipeName, "status", string.Empty, false,
                        TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    if (response?.Owner is { } owner)
                    {
                        if (!string.Equals(owner.RunId, runId, StringComparison.Ordinal))
                        {
                            environment.Err.WriteLine("nightweaver: a different owner answered the new run identity; refusing to control it.");
                            return ExitCodes.InspectionFailure;
                        }
                        if (owner.State == "running") return WriteStatus(new OwnerQuery(true, owner, true, null), environment);
                        if (owner.State is "failed" or "degraded" or "stopped")
                            return WriteStatus(new OwnerQuery(false, owner, false, owner.Message ?? "background owner did not become healthy"), environment);
                    }
                    else if (process.HasExited)
                    {
                        var failed = BackgroundOwnerRecord.TryRead(recordPath);
                        environment.Err.WriteLine($"nightweaver: background owner exited during startup (code {process.ExitCode}); see {failed?.HostLogPath ?? recordPath}.");
                        return ExitCodes.InspectionFailure;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }

                var uncertain = await QueryAsync(paths, project, configPath, pipeName, recordPath,
                    cancellationToken).ConfigureAwait(false);
                var message = uncertain.Active
                    ? "background startup is still in progress or unavailable; inspect status and logs before retrying"
                    : $"background owner did not become ready within {OwnerStartTimeout}; inspect status and logs before retrying";
                var uncertainRecord = uncertain.Record is { } record
                    ? record with { Message = message }
                    : new BackgroundOwnerRecord(1, uncertain.OwnerLockActive ? "unavailable" : "starting",
                        runId, 0, DateTimeOffset.MinValue, paths.Root, configPath, project.Name.Value,
                        project.TargetRepository.Path, null, urls, "unknown", "unknown",
                        Path.Combine(paths.LogsRoot(project.Name), "background-host.log"),
                        Path.Combine(paths.LogsRoot(project.Name), "background-web.log"), message);
                environment.Out.WriteLine(JsonSerializer.Serialize(uncertainRecord, OutputJson));
                environment.Err.WriteLine("nightweaver: " + message);
                return ExitCodes.InspectionFailure;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or UnauthorizedAccessException or Win32Exception)
            {
                environment.Err.WriteLine($"nightweaver: background owner launch failed: {exception.Message}");
                return ExitCodes.InspectionFailure;
            }
        }
    }

    private static async Task<string> ReadTickEpochAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // A boot/session-qualified epoch is shared by finite CLI invocations. Failure
        // to obtain it is visible, never replaced with a guessed/process-local epoch.
        var result = await new SystemProcessRunner().RunAsync(new("pwsh",
            ["-NoProfile", "-NonInteractive", "-Command",
                "(Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().Ticks"],
            Environment.CurrentDirectory, new Dictionary<string, string?>(), SupervisionPolicy.ProbeBudget)
            { MaximumOutputBytes = 1024 }, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !long.TryParse(result.StandardOutput.Trim(), out var boot) || boot <= 0)
            throw new InvalidDataException("Boot identity unavailable; supervision tick suppressed.");
        return TickEpoch(boot, SupervisionLogon.ReadIdentity());
    }

    internal static string TickEpoch(long boot, string logon)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            boot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + logon));
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static async Task<int> StopAsync(HostPaths paths, ProjectConfig project, string configPath,
        string pipeName, string recordPath, bool force, AppEnvironment environment,
        CancellationToken cancellationToken, bool maintenance = false, Action? releaseCommand = null, SupervisionRead? supervision = null)
    {
        var existing = await QueryAsync(paths, project, configPath, pipeName, recordPath,
            cancellationToken).ConfigureAwait(false);
        if (!existing.Active)
        {
            if (existing.OwnerLockActive)
            {
                environment.Err.WriteLine("nightweaver: an owner lock is active but its control pipe is unavailable; no process was changed.");
                return WriteStatus(existing, environment, supervision);
            }
            releaseCommand?.Invoke();
            return WriteStatus(existing, environment, supervision);
        }
        var owner = existing.Record!;
        if (!SameConfig(owner, configPath))
        {
            environment.Err.WriteLine("nightweaver: active background owner uses a different config path; no process was changed.");
            return ExitCodes.InspectionFailure;
        }

        try
        {
            var stopResponse = await BackgroundControlProtocol.SendAsync(pipeName,
                new BackgroundControlRequest(force ? "force" : "stop", owner.RunId, force, maintenance),
                TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!stopResponse.Accepted || stopResponse.Owner?.RunId != owner.RunId)
            {
                environment.Err.WriteLine("nightweaver: owner rejected stop because its run identity changed; no further action was taken.");
                return ExitCodes.InspectionFailure;
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
        {
            if (BackgroundFileLock.IsOwnerActive(paths, project.Name))
            {
                environment.Err.WriteLine($"nightweaver: background stop was not confirmed: {exception.Message}");
                return ExitCodes.InspectionFailure;
            }
        }

        releaseCommand?.Invoke();
        if (!await WaitForOwnerExitAsync(paths, project, OwnerStopTimeout, cancellationToken).ConfigureAwait(false))
        {
            var pending = await QueryAsync(paths, project, configPath, pipeName, recordPath,
                cancellationToken).ConfigureAwait(false);
            environment.Err.WriteLine(force
                ? "nightweaver: force-stop was requested but the owner has not exited; inspect owner identity and logs."
                : "nightweaver: runtime is still draining. Run background status, or use background stop --force after reviewing active work.");
            return WriteStatus(pending, environment, supervision);
        }

        var stopped = await QueryAsync(paths, project, configPath, pipeName, recordPath,
            cancellationToken).ConfigureAwait(false);
        return WriteStatus(stopped, environment, supervision);
    }

    private static async Task<int> RestartAsync(HostPaths paths, ProjectConfig project, string configPath,
        string? requestedUrls, string pipeName, string recordPath, AppEnvironment environment,
        CancellationToken cancellationToken)
    {
        var existing = await QueryAsync(paths, project, configPath, pipeName, recordPath,
            cancellationToken).ConfigureAwait(false);
        if (existing.Active && existing.Record is { } current)
        {
            if (!SameConfig(current, configPath))
            {
                environment.Err.WriteLine("nightweaver: active background owner uses a different config path; no process was changed.");
                return ExitCodes.InspectionFailure;
            }
            var stopCode = await StopAsync(paths, project, configPath, pipeName, recordPath,
                force: false, environment, cancellationToken).ConfigureAwait(false);
            if (stopCode != ExitCodes.Success) return stopCode;
        }
        else if (existing.OwnerLockActive)
        {
            environment.Err.WriteLine("nightweaver: background owner is active but cannot be controlled; restart stopped without launching another process.");
            return WriteStatus(existing, environment);
        }

        var url = requestedUrls ?? existing.Record?.WebUrl ?? "http://127.0.0.1:5000";
        return await StartAsync(paths, project, configPath, url, pipeName, recordPath,
            environment, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OwnerQuery> QueryAsync(HostPaths paths, ProjectConfig project,
        string configPath, string pipeName, string recordPath, CancellationToken cancellationToken)
    {
        // An absent owner lock cannot authorize a live response. Avoid waiting on a
        // nonexistent pipe for manual status and durable absent-owner commands.
        if (!BackgroundFileLock.IsOwnerActive(paths, project.Name))
        {
            var retained = BackgroundOwnerRecord.TryRead(recordPath);
            if (retained is not null && retained.State is "running" or "starting" or "stopping")
                retained = retained with { State = "stopped", WebHealth = "unknown", RuntimeHealth = "stale",
                    Message = "owner is not running; retained status is diagnostic only" };
            return new(false, retained, false, "no background owner is running");
        }
        try
        {
            var response = await TryQueryPipeAsync(pipeName, "status", string.Empty, false,
                TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (response?.Owner is { } owner)
            {
                var lockActive = BackgroundFileLock.IsOwnerActive(paths, project.Name);
                return new(lockActive && owner.State is not "stopped" and not "failed", owner, lockActive, response.Error);
            }
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException
            or UnauthorizedAccessException)
        {
            var record = BackgroundOwnerRecord.TryRead(recordPath);
            var lockActive = BackgroundFileLock.IsOwnerActive(paths, project.Name);
            if (lockActive)
                return new(false, record is null ? null : record with { State = "unavailable", WebHealth = "unknown", RuntimeHealth = "unknown", Message = "owner lock is active but named-pipe status is unavailable" }, true, exception.Message);
            if (record is not null && record.State is "running" or "starting" or "stopping")
                record = record with { State = "stopped", WebHealth = "unknown", RuntimeHealth = "stale", Message = "owner is not running; retained status is diagnostic only" };
            return new(false, record, false, exception.Message);
        }

        var fallback = BackgroundOwnerRecord.TryRead(recordPath);
        var active = BackgroundFileLock.IsOwnerActive(paths, project.Name);
        if (active)
            return new(false, fallback is null ? null : fallback with { State = "unavailable", WebHealth = "unknown", RuntimeHealth = "unknown", Message = "owner lock is active but no valid status record was returned" }, true, "control endpoint unavailable");
        if (fallback is not null && fallback.State is "running" or "starting" or "stopping")
            fallback = fallback with { State = "stopped", WebHealth = "unknown", RuntimeHealth = "stale", Message = "owner is not running; retained status is diagnostic only" };
        return new(false, fallback, false, "no background owner is running");
    }

    private static async Task<BackgroundControlResponse?> TryQueryPipeAsync(string pipeName,
        string operation, string runId, bool force, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await BackgroundControlProtocol.SendAsync(pipeName,
                new BackgroundControlRequest(operation, runId, force), timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForOwnerExitAsync(HostPaths paths, ProjectConfig project,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BackgroundFileLock.IsOwnerActive(paths, project.Name)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        return !BackgroundFileLock.IsOwnerActive(paths, project.Name);
    }

    private static IReadOnlyList<string> BuildOwnerStartArguments(string root, string configPath,
        string project, string runId, string urls) =>
    [
        "background", "owner", "--root", root, "--config", configPath, "--project", project,
        "--run-id", runId, "--urls", urls
    ];

    private static bool SameConfig(BackgroundOwnerRecord record, string configPath) =>
        string.Equals(Path.GetFullPath(record.ConfigPath), Path.GetFullPath(configPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<DeploymentObservation> ObserveStatusAsync(HostPaths paths, ProjectConfig project,
        OwnerQuery query, SupervisionLifecycle? lifecycle, SupervisionRead supervision, CancellationToken token)
    {
        var identity = new DeploymentIdentity(paths.Root, project.ConfigFilePath, project.Name.Value,
            project.TargetRepository.Path, string.Empty);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(SupervisionPolicy.ProbeBudget);
            var repository = await new TargetRepositoryValidator(new SystemProcessRunner())
                .ValidateAsync(project.TargetRepository, project.ConfigFilePath, deadline.Token).ConfigureAwait(false);
            identity = identity with { GitCommonDirectory = repository.GitCommonDirectory };
            var observer = new DeploymentObserver(identity, new(
                () => BackgroundOwnerRecord.TryRead(BackgroundOwnerRecord.PathFor(paths, project.Name)),
                () => BackgroundFileLock.ObserveOwner(paths, project.Name), BackgroundContainment.Read,
                _ => Task.FromResult(new BackgroundControlResponse(query.OwnerLockActive, query.Record, query.Failure)),
                () => new Nightweaver.Telemetry.FactoryTelemetryReader(paths, project).Read(),
                () => lifecycle?.Read() ?? supervision));
            return await observer.ObserveAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ProjectConfigurationException or TargetRepositoryException or ToolExecutionException or TimeoutException
            or OperationCanceledException)
        {
            return new(1, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), 0, identity, null,
                DeploymentHealth.Unknown, "identity-observation-unavailable", null, null, null, null);
        }
    }

    private static int WriteStatus(OwnerQuery query, AppEnvironment environment, SupervisionRead? supervision = null,
        DeploymentObservation? observation = null)
    {
        var record = query.Record ?? new BackgroundOwnerRecord(1,
            query.OwnerLockActive ? "unavailable" : "stopped", string.Empty, 0, DateTimeOffset.MinValue,
            string.Empty, string.Empty, string.Empty, null, null, string.Empty, "unknown",
            query.OwnerLockActive ? "unknown" : "stale", string.Empty, string.Empty,
            query.Failure ?? "no background owner has been recorded");
        var output = JsonSerializer.SerializeToNode(record with { Supervision = supervision }, OutputJson)!.AsObject();
        if (observation is not null)
            output["observation"] = JsonSerializer.SerializeToNode(DeploymentEvidenceStore.Sanitize(observation), OutputJson);
        environment.Out.WriteLine(output.ToJsonString(OutputJson));
        if (supervision?.Availability == SupervisionAvailability.Unavailable) return ExitCodes.InspectionFailure;
        if (record.State == "running" || record.State == "stopped") return ExitCodes.Success;
        environment.Err.WriteLine("nightweaver: background runtime is " + record.State + ". " + (query.Failure ?? record.Message ?? string.Empty));
        return ExitCodes.InspectionFailure;
    }

    private sealed record OwnerQuery(bool Active, BackgroundOwnerRecord? Record, bool OwnerLockActive, string? Failure);
}
