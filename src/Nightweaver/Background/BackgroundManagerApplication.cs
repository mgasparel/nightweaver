using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Background;

internal static class BackgroundManagerApplication
{
    private static readonly TimeSpan OwnerStartTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OwnerStopTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    internal static async Task<int> RunAsync(BackgroundCommandOptions options, AppEnvironment environment,
        CancellationToken cancellationToken)
    {
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
        switch (options.Action)
        {
            case BackgroundAction.Status:
                return WriteStatus(await QueryAsync(paths, project, configPath, pipeName, recordPath,
                    cancellationToken).ConfigureAwait(false), environment);
            case BackgroundAction.Stop:
                return await StopAsync(paths, project, configPath, pipeName, recordPath, options.Force,
                    environment, cancellationToken).ConfigureAwait(false);
            case BackgroundAction.Start:
                return await StartAsync(paths, project, configPath, options.Urls ?? "http://127.0.0.1:5000",
                    pipeName, recordPath, environment, cancellationToken).ConfigureAwait(false);
            case BackgroundAction.Restart:
                return await RestartAsync(paths, project, configPath, options.Urls, pipeName, recordPath,
                    environment, cancellationToken).ConfigureAwait(false);
            default:
                environment.Err.WriteLine("nightweaver: invalid background manager action.");
                return ExitCodes.UsageOrConfiguration;
        }
    }

    private static async Task<int> StartAsync(HostPaths paths, ProjectConfig project, string configPath,
        string urls, string pipeName, string recordPath, AppEnvironment environment,
        CancellationToken cancellationToken)
    {
        BackgroundDeploymentManifest manifest;
        try { manifest = BackgroundDeploymentManifest.Load(paths.Root); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            environment.Err.WriteLine($"nightweaver: background deployment is incomplete: {exception.Message}");
            return ExitCodes.InspectionFailure;
        }

        InvocationRuntimeFiles.PinnedRuntimeFile startLock;
        try { startLock = BackgroundFileLock.AcquireStart(paths, project.Name); }
        catch (IOException)
        {
            environment.Err.WriteLine("nightweaver: another background start or restart is already being prepared.");
            return ExitCodes.InspectionFailure;
        }
        using (startLock)
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

            var runId = Guid.NewGuid().ToString("N");
            try
            {
                var executable = manifest.ResolveCliExecutable(paths.Root);
                var ownerArguments = BuildOwnerStartArguments(paths.Root, configPath, project.Name.Value, runId, urls);
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

    private static async Task<int> StopAsync(HostPaths paths, ProjectConfig project, string configPath,
        string pipeName, string recordPath, bool force, AppEnvironment environment,
        CancellationToken cancellationToken)
    {
        var existing = await QueryAsync(paths, project, configPath, pipeName, recordPath,
            cancellationToken).ConfigureAwait(false);
        if (!existing.Active)
        {
            if (existing.OwnerLockActive)
            {
                environment.Err.WriteLine("nightweaver: an owner lock is active but its control pipe is unavailable; no process was changed.");
                return WriteStatus(existing, environment);
            }
            return WriteStatus(existing, environment);
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
                new BackgroundControlRequest(force ? "force" : "stop", owner.RunId, force),
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

        if (!await WaitForOwnerExitAsync(paths, project, OwnerStopTimeout, cancellationToken).ConfigureAwait(false))
        {
            var pending = await QueryAsync(paths, project, configPath, pipeName, recordPath,
                cancellationToken).ConfigureAwait(false);
            environment.Err.WriteLine(force
                ? "nightweaver: force-stop was requested but the owner has not exited; inspect owner identity and logs."
                : "nightweaver: runtime is still draining. Run background status, or use background stop --force after reviewing active work.");
            return WriteStatus(pending, environment);
        }

        var stopped = await QueryAsync(paths, project, configPath, pipeName, recordPath,
            cancellationToken).ConfigureAwait(false);
        return WriteStatus(stopped, environment);
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

    private static int WriteStatus(OwnerQuery query, AppEnvironment environment)
    {
        var record = query.Record ?? new BackgroundOwnerRecord(1,
            query.OwnerLockActive ? "unavailable" : "stopped", string.Empty, 0, DateTimeOffset.MinValue,
            string.Empty, string.Empty, string.Empty, null, null, string.Empty, "unknown",
            query.OwnerLockActive ? "unknown" : "stale", string.Empty, string.Empty,
            query.Failure ?? "no background owner has been recorded");
        environment.Out.WriteLine(JsonSerializer.Serialize(record, OutputJson));
        if (record.State == "running" || record.State == "stopped") return ExitCodes.Success;
        environment.Err.WriteLine("nightweaver: background runtime is " + record.State + ". " + (query.Failure ?? record.Message ?? string.Empty));
        return ExitCodes.InspectionFailure;
    }

    private sealed record OwnerQuery(bool Active, BackgroundOwnerRecord? Record, bool OwnerLockActive, string? Failure);
}
