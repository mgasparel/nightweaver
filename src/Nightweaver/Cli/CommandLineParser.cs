using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Cli;

internal static class CommandLineParser
{
    internal static CliOptions Parse(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == "dispatch" ? ParseDispatch(args) : ParseDryRun(args);

    private static CliOptions ParseDryRun(IReadOnlyList<string> args)
    {
        string? project = null;
        string? config = null;
        var hasProject = false;
        var hasConfig = false;
        var hasDryRun = false;
        var hasRole = false;
        var hasHelp = false;

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (token is "--project" or "--config" or "--role")
            {
                var isProject = token == "--project";
                var isConfig = token == "--config";
                if ((isProject && hasProject) || (isConfig && hasConfig) || (token == "--role" && hasRole))
                    throw Repeated(token);

                var value = ReadValue(args, ref index, token);
                if (isProject) { hasProject = true; project = value; }
                else if (isConfig) { hasConfig = true; config = value; }
                else { hasRole = true; _ = ParseRole(value); }
                continue;
            }

            if (TryReadEqualsValue(token, "--project", out var projectValue))
            {
                if (hasProject) throw Repeated("--project");
                project = RequireValue(projectValue, "--project");
                hasProject = true;
                continue;
            }

            if (TryReadEqualsValue(token, "--config", out var configValue))
            {
                if (hasConfig) throw Repeated("--config");
                config = RequireValue(configValue, "--config");
                hasConfig = true;
                continue;
            }

            if (TryReadEqualsValue(token, "--role", out var roleValue))
            {
                if (hasRole) throw Repeated("--role");
                _ = ParseRole(RequireValue(roleValue, "--role"));
                hasRole = true;
                continue;
            }

            if (token == "--dry-run")
            {
                if (hasDryRun) throw Repeated("--dry-run");
                hasDryRun = true;
                continue;
            }

            if (token is "--help" or "-h")
            {
                if (hasHelp) throw Repeated("--help");
                hasHelp = true;
                continue;
            }

            if (token.Length > 0 && token[0] == '-') throw new UsageException($"unknown option '{token}'");
            throw new UsageException($"unexpected argument '{token}'");
        }

        if (hasHelp) return new CliOptions(null, hasDryRun, Help: true);
        if (hasRole) throw new UsageException("option '--role' is only valid with 'dispatch'");
        if (hasProject && hasConfig)
            throw new UsageException("options '--project' and '--config' cannot be used together");
        if (!hasProject && !hasConfig)
            throw new UsageException("one of '--project <project-slug>' or '--config <path-to-project-json>' is required");

        var selector = ParseSelector(project, config, hasProject);
        if (!hasDryRun)
            throw new UsageException("live orchestration is not implemented yet; only '--dry-run' is supported");
        return new CliOptions(selector, DryRun: true, Help: false);
    }

    private static CliOptions ParseDispatch(IReadOnlyList<string> args)
    {
        string? beadId = null;
        string? project = null;
        string? config = null;
        AgentRole? role = null;
        var hasProject = false;
        var hasConfig = false;
        var hasRole = false;
        var hasDryRun = false;
        var hasHelp = false;
        var index = 1;

        if (index < args.Count && args[index] is not ("--help" or "-h")
            && (args[index].Length == 0 || args[index][0] != '-'))
        {
            beadId = args[index++];
            if (!BeadIdSafety.IsSafe(beadId))
                throw new UsageException($"unsafe Bead ID '{beadId}': expected a safe literal Bead path segment");
        }

        while (index < args.Count)
        {
            var token = args[index];
            if (token is "--project" or "--config" or "--role")
            {
                if (token == "--project")
                {
                    if (hasProject) throw Repeated(token);
                    project = ReadValue(args, ref index, token);
                    hasProject = true;
                }
                else if (token == "--config")
                {
                    if (hasConfig) throw Repeated(token);
                    config = ReadValue(args, ref index, token);
                    hasConfig = true;
                }
                else
                {
                    if (hasRole) throw Repeated(token);
                    role = ParseRole(ReadValue(args, ref index, token));
                    hasRole = true;
                }
                index++;
                continue;
            }

            if (TryReadEqualsValue(token, "--project", out var projectValue))
            {
                if (hasProject) throw Repeated("--project");
                project = RequireValue(projectValue, "--project");
                hasProject = true;
                index++;
                continue;
            }
            if (TryReadEqualsValue(token, "--config", out var configValue))
            {
                if (hasConfig) throw Repeated("--config");
                config = RequireValue(configValue, "--config");
                hasConfig = true;
                index++;
                continue;
            }
            if (TryReadEqualsValue(token, "--role", out var roleValue))
            {
                if (hasRole) throw Repeated("--role");
                role = ParseRole(RequireValue(roleValue, "--role"));
                hasRole = true;
                index++;
                continue;
            }
            if (token == "--dry-run")
            {
                if (hasDryRun) throw Repeated("--dry-run");
                hasDryRun = true;
                index++;
                continue;
            }
            if (token is "--help" or "-h")
            {
                if (hasHelp) throw Repeated("--help");
                hasHelp = true;
                index++;
                continue;
            }
            if (token.Length > 0 && token[0] == '-') throw new UsageException($"unknown option '{token}'");
            throw new UsageException($"unexpected argument '{token}'");
        }

        // Help is decided after tokenization, so malformed tokens still fail but selectors are optional.
        if (hasHelp)
            return new CliOptions(null, DryRun: false, Help: true, new DispatchCommandOptions(beadId, role));

        if (beadId is null) throw new UsageException("'dispatch' requires one Bead ID");
        if (hasDryRun) throw new UsageException("'dispatch' cannot be combined with '--dry-run'");
        if (hasProject && hasConfig)
            throw new UsageException("options '--project' and '--config' cannot be used together");
        if (!hasProject && !hasConfig)
            throw new UsageException("one of '--project <project-slug>' or '--config <path-to-project-json>' is required for dispatch");

        return new CliOptions(ParseSelector(project, config, hasProject), DryRun: false, Help: false,
            new DispatchCommandOptions(beadId, role));
    }

    private static ProjectSelector ParseSelector(string? project, string? config, bool hasProject)
    {
        if (hasProject)
        {
            if (!ProjectSlug.TryParse(project, out var slug, out var reason))
                throw new UsageException($"invalid project slug '{project}': {reason}");
            return new ProjectSelector.BySlug(slug);
        }
        return new ProjectSelector.ByConfigPath(config!);
    }

    private static AgentRole ParseRole(string value)
    {
        if (!AgentRoles.TryParseConfigKey(value, out var role))
            throw new UsageException($"unknown lifecycle role '{value}'; expected planner, plan_challenger, implementer, reviewer, qa, or merger");
        if (role == AgentRole.Classifier)
            throw new UsageException("role 'classifier' is not a lifecycle role and cannot be dispatched directly");
        return role;
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"option '{option}' requires a value");
        return args[++index];
    }

    private static string RequireValue(string value, string option) => string.IsNullOrWhiteSpace(value)
        ? throw new UsageException($"option '{option}' requires a value")
        : value;

    private static bool TryReadEqualsValue(string token, string option, out string value)
    {
        var prefix = option + "=";
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }
        value = token[prefix.Length..];
        return true;
    }

    private static UsageException Repeated(string option) =>
        new($"option '{option}' specified more than once");
}
