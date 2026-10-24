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

        var inventory = await new BeadsReader(processRunner).ReadSnapshotAsync(repository, cancellationToken);
        var startupPlan = StartupPlanner.Plan(config.RecoveryActor, inventory);

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
            new AgentRolesSection(config.AgentDefaults, config.Roles), startupPlan);
        environment.Out.Write(DryRunReportWriter.Render(report));
    }
}
