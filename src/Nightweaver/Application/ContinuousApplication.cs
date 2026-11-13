using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Application;

/// <summary>Runs the selector-only continuous Delivery and singleton Merger runtime after startup recovery.</summary>
internal static class ContinuousApplication
{
    internal static async Task<int> RunAsync(
        CliOptions options,
        AppEnvironment environment,
        IProcessRunner processRunner,
        RuntimeShutdownSignals? shutdownSignals,
        CancellationToken cancellationToken,
        Func<FactoryTelemetryPublisher, ProjectConfig, HostPaths, ValidatedTargetRepository, CancellationToken, Task>? onRuntimeReady = null)
    {
        var selector = options.Selector ?? throw new InvalidOperationException("A Project selector is required");
        cancellationToken.ThrowIfCancellationRequested();

        var hostPaths = new HostPaths(environment.NightweaverRoot);
        string configPath;
        try
        {
            configPath = ProjectConfigLocator.ResolveConfigPath(selector, hostPaths, environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var rawPath = selector is ProjectSelector.ByConfigPath byConfig ? byConfig.RawPath : hostPaths.Root;
            throw new ProjectConfigurationException(exception.Message, null, rawPath);
        }

        var expectedName = selector is ProjectSelector.BySlug bySlug ? bySlug.Slug : (ProjectSlug?)null;
        var project = ProjectConfigLoader.Load(configPath, expectedName, hostPaths);
        var repository = await new TargetRepositoryValidator(processRunner)
            .ValidateAsync(project.TargetRepository, project.ConfigFilePath, cancellationToken).ConfigureAwait(false);

        // The process-owned lock precedes startup recovery and every live Delivery admission.
        ProcessLifetimeTargetRepositoryLock.Acquire(repository);
        EnsureInitialRoots(hostPaths, project.Name);

        var beadsReader = new BeadsReader(processRunner);
        var beadsWriter = new BeadsWriter(processRunner);
        await new StartupRecoveryCoordinator(beadsReader, beadsWriter)
            .RecoverAsync(repository, project.RecoveryActor, cancellationToken).ConfigureAwait(false);
        await using var telemetry = new FactoryTelemetryPublisher(hostPaths, project, repository,
            project.Concurrency.MaxConcurrentDelivery, ConcurrencyConfig.MergerSlots,
            message => environment.Err.WriteLine("nightweaver: " + message), processRunner);
        await telemetry.StartAsync(cancellationToken).ConfigureAwait(false);
        if (onRuntimeReady is not null)
            await onRuntimeReady(telemetry, project, hostPaths, repository, cancellationToken).ConfigureAwait(false);

        using var slotLifetime = new CancellationTokenSource();
        var stopGate = shutdownSignals?.StopGate ?? new ContinuousStopGate();
        var environments = new EnvironmentReservations();
        using var cleanup = new CompletedEnvironmentWorker(hostPaths, project, repository, processRunner,
            environments, stopGate, telemetry.RuntimeId, message => environment.Err.WriteLine("nightweaver: " + message));
        await cleanup.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var cleanupLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cleanupTask = Task.Run(() => cleanup.RunAsync(cleanupLifetime.Token), CancellationToken.None);
        var pool = new DeliveryPoolCoordinator(hostPaths, project, repository, processRunner,
            slotLifetimeToken: slotLifetime.Token,
            stopGate: stopGate,
            telemetry: telemetry, environments: environments);
        var mergerPool = new MergerPoolCoordinator(hostPaths, project, repository, processRunner,
            slotLifetimeToken: slotLifetime.Token,
            stopGate: stopGate,
            telemetry: telemetry, environments: environments, cleanupQueue: cleanup);
        var configuredPools = new[]
        {
            new ContinuousPoolRegistration(
                ContinuousPoolKind.Delivery,
                async token =>
                {
                    var evaluation = await pool.EvaluateAsync(token).ConfigureAwait(false);
                    foreach (var admission in evaluation.Admitted)
                        environment.Out.WriteLine($"Delivery reserved Plan Bead '{admission.BeadId}' (capacity {pool.CapacityLimit}).");
                    return evaluation.Diagnostics;
                },
                pool.CompletionReader,
                slotLifetime.Cancel,
                async token => { await pool.WaitForActiveSlotsAsync(token).ConfigureAwait(false); },
                pool.WaitForCompletionAccountingAsync),
            new ContinuousPoolRegistration(
                ContinuousPoolKind.Merger,
                async token =>
                {
                    var evaluation = await mergerPool.EvaluateAsync(token).ConfigureAwait(false);
                    foreach (var admission in evaluation.Admitted)
                        environment.Out.WriteLine($"Merger reserved task Bead '{admission.BeadId}' (singleton capacity {mergerPool.CapacityLimit}).");
                    return evaluation.Diagnostics;
                },
                mergerPool.CompletionReader,
                slotLifetime.Cancel,
                async token => { await mergerPool.WaitForActiveSlotsAsync(token).ConfigureAwait(false); },
                mergerPool.WaitForCompletionAccountingAsync)
        };
        var runtime = new ContinuousDiscoveryCoordinator(
            configuredPools,
            _ =>
            {
                ValidateAdmissionRoots(hostPaths, project.Name);
                return Task.CompletedTask;
            },
            environment.Out,
            environment.Err,
            journalWatcher: (requestDiscovery, token) => new BeadsJournalWatcher(
                new BeadsJournalClient(processRunner), requestDiscovery, environment.Err)
                .RunAsync(repository, token),
            stopGate: stopGate, onDiscoveryRequested: cleanup.Wake);

        environment.Out.WriteLine($"Continuous Delivery runtime starting with singleton Merger for Project '{project.Name.Value}' and Target Repository '{repository.Path}'.");
        environment.Out.WriteLine($"Delivery capacity: {pool.CapacityLimit}; Merger capacity: {mergerPool.CapacityLimit}; periodic discovery: 30 seconds; press Ctrl+C to stop.");
        try
        {
            var drained = await runtime.RunAsync(cancellationToken).ConfigureAwait(false);
            return drained ? ExitCodes.Success : ExitCodes.InspectionFailure;
        }
        finally
        {
            cleanupLifetime.Cancel();
            try { await cleanupTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cleanupLifetime.IsCancellationRequested) { }
            // All completion producers have drained. Persistence failures are required operational
            // failures; closure remains intact and startup reconciliation recovers after repair.
            if (!stopGate.IsForceStopRequested)
                await cleanup.FlushCompletionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static void EnsureInitialRoots(HostPaths hostPaths, ProjectSlug project)
    {
        SharedRuntimeRootValidator.EnsureUsable(hostPaths.WorktreesRoot(project));
        SharedRuntimeRootValidator.EnsureUsable(hostPaths.TmpRoot(project));
        SharedRuntimeRootValidator.EnsureUsable(hostPaths.LogsRoot(project));
    }

    internal static void ValidateAdmissionRoots(HostPaths hostPaths, ProjectSlug project)
    {
        SharedRuntimeRootValidator.EnsureExistingUsable(hostPaths.WorktreesRoot(project));
        SharedRuntimeRootValidator.EnsureExistingUsable(hostPaths.TmpRoot(project));
        SharedRuntimeRootValidator.EnsureExistingUsable(hostPaths.LogsRoot(project));
    }
}
