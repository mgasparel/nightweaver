using Nightweaver.Configuration;

namespace Nightweaver.Cli;

internal static class CommandLineParser
{
    internal static CliOptions Parse(IReadOnlyList<string> args)
    {
        string? project = null;
        string? config = null;
        var hasProject = false;
        var hasConfig = false;
        var hasDryRun = false;
        var hasHelp = false;

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (token is "--project" or "--config")
            {
                var isProject = token == "--project";
                if (isProject ? hasProject : hasConfig)
                {
                    throw Repeated(token);
                }

                if (index + 1 >= args.Count || args[index + 1].Length == 0 || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"option '{token}' requires a value");
                }

                var value = args[++index];
                if (isProject)
                {
                    hasProject = true;
                    project = value;
                }
                else
                {
                    hasConfig = true;
                    config = value;
                }

                continue;
            }

            if (token.StartsWith("--project=", StringComparison.Ordinal))
            {
                if (hasProject)
                {
                    throw Repeated("--project");
                }

                project = token["--project=".Length..];
                if (project.Length == 0)
                {
                    throw new UsageException("option '--project' requires a value");
                }

                hasProject = true;
                continue;
            }

            if (token.StartsWith("--config=", StringComparison.Ordinal))
            {
                if (hasConfig)
                {
                    throw Repeated("--config");
                }

                config = token["--config=".Length..];
                if (config.Length == 0)
                {
                    throw new UsageException("option '--config' requires a value");
                }

                hasConfig = true;
                continue;
            }

            if (token == "--dry-run")
            {
                if (hasDryRun)
                {
                    throw Repeated("--dry-run");
                }

                hasDryRun = true;
                continue;
            }

            if (token is "--help" or "-h")
            {
                if (hasHelp)
                {
                    throw Repeated("--help");
                }

                hasHelp = true;
                continue;
            }

            if (token.Length > 0 && token[0] == '-')
            {
                throw new UsageException($"unknown option '{token}'");
            }

            throw new UsageException($"unexpected argument '{token}'");
        }

        if (hasHelp)
        {
            return new CliOptions(null, hasDryRun, Help: true);
        }

        if (hasProject && hasConfig)
        {
            throw new UsageException("options '--project' and '--config' cannot be used together");
        }

        if (!hasProject && !hasConfig)
        {
            throw new UsageException("one of '--project <project-slug>' or '--config <path-to-project-json>' is required");
        }

        ProjectSelector selector;
        if (hasProject)
        {
            if (!ProjectSlug.TryParse(project, out var slug, out var reason))
            {
                throw new UsageException($"invalid project slug '{project}': {reason}");
            }

            selector = new ProjectSelector.BySlug(slug);
        }
        else
        {
            selector = new ProjectSelector.ByConfigPath(config!);
        }

        if (!hasDryRun)
        {
            throw new UsageException("live orchestration is not implemented yet; only '--dry-run' is supported");
        }

        return new CliOptions(selector, hasDryRun, Help: false);
    }

    private static UsageException Repeated(string option) =>
        new($"option '{option}' specified more than once");
}
