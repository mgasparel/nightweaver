using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Reporting;
using Nightweaver.Planning;

namespace Nightweaver.Application;

internal static class DryRunApplication
{
    internal static async Task RunAsync(
        CliOptions options,
        AppEnvironment environment,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var selector = options.Selector ?? throw new InvalidOperationException("A Project selector is required");
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
        var config = ProjectConfigLoader.Load(configPath, expectedName, hostPaths);
        var repository = await new TargetRepositoryValidator(processRunner)
            .ValidateAsync(config.TargetRepository, config.ConfigFilePath, cancellationToken);

        var beadsReader = new BeadsReader(processRunner);
        var inventory = await beadsReader.ReadSnapshotAsync(repository, cancellationToken);
        var readiness = await beadsReader.ReadReadinessAsync(repository, inventory, config.RecoveryActor, cancellationToken);
        var registry = await new GitWorktreeReader(processRunner).ReadAsync(repository, cancellationToken);
        var runtime = RuntimeDirectoryReader.Read(hostPaths, config.Name);
        var worktrees = runtime.Worktrees;
        var startupPlan = StartupPlanner.PlanForStartup(config.RecoveryActor, inventory, registry, worktrees, runtime.Tmp);
        if (startupPlan.Removals.Count > 0)
        {
            var identityFailures = new Dictionary<string, string>(RuntimePaths.Comparer);
            foreach (var candidate in startupPlan.Removals)
            {
                var failure = await StartupEnvironmentCleaner.ValidateCheckoutIdentityAsync(processRunner, repository,
                    candidate, cancellationToken).ConfigureAwait(false);
                if (failure is not null) identityFailures[candidate.Path] = failure;
            }

            if (identityFailures.Count > 0)
            {
                // Use the same live checkout-identity check before projecting removal. Mark failed
                // observations as unusable so the planner retains the worktree and protects its tmp.
                worktrees = RuntimeDirectoryReader.MarkInvalidCheckoutIdentities(worktrees, identityFailures);
                startupPlan = StartupPlanner.PlanForStartup(config.RecoveryActor, inventory, registry, worktrees, runtime.Tmp);
            }
        }
        var postSweep = PostSweepReadinessProjector.Project(startupPlan, readiness);
        var planningPoolPlan = PlanningPoolPlanner.Plan(postSweep,
            config.Concurrency.MaxConcurrentPlanning, config.Concurrency.MaxActivePlanBeads);
        var deliveryDiscoveryPlan = DeliveryDiscoveryPlanner.Plan(postSweep, config.Concurrency.MaxConcurrentDelivery);

        var header = new ProjectHeader(
            config.Name.Value,
            config.ConfigFilePath,
            hostPaths.Root,
            repository.Path,
            repository.DefaultBranch,
            config.RecoveryActor,
            hostPaths.WorktreesRoot(config.Name),
            hostPaths.TmpRoot(config.Name),
            hostPaths.LogsRoot(config.Name));
        var report = new DryRunReport(
            header,
            new ConfigurationSection(config.Concurrency, config.Limits),
            new AgentRolesSection(config.AgentDefaults, config.Roles), startupPlan, planningPoolPlan, deliveryDiscoveryPlan);
        environment.Out.Write(DryRunReportWriter.Render(report));
    }
}
