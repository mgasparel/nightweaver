using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Application;

/// <summary>Runs the selector-only continuous Delivery runtime after conservative startup recovery.</summary>
internal static class ContinuousApplication
{
    internal static async Task<int> RunAsync(
        CliOptions options,
        AppEnvironment environment,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
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
        var cleanupDiagnostics = await new StartupEnvironmentCleaner(beadsReader, beadsWriter, processRunner)
            .CleanAsync(hostPaths, project.Name, repository, project.RecoveryActor, cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in cleanupDiagnostics)
            environment.Err.WriteLine($"nightweaver: startup cleanup retained '{diagnostic.BeadId}' at '{diagnostic.Path}': {diagnostic.Reason}");

        using var slotLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pool = new DeliveryPoolCoordinator(hostPaths, project, repository, processRunner,
            slotLifetimeToken: slotLifetime.Token);
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
                async token => { await pool.WaitForActiveSlotsAsync(token).ConfigureAwait(false); })
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
                .RunAsync(repository, token));

        environment.Out.WriteLine($"Continuous Delivery runtime starting for Project '{project.Name.Value}' and Target Repository '{repository.Path}'.");
        environment.Out.WriteLine($"Delivery capacity: {pool.CapacityLimit}; periodic discovery: 30 seconds; press Ctrl+C to stop.");
        var drained = await runtime.RunAsync(cancellationToken).ConfigureAwait(false);
        return drained ? ExitCodes.Success : ExitCodes.InspectionFailure;
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
