using Nightweaver.Configuration;

namespace Nightweaver.Cli;

internal static class BackgroundCommandParser
{
    internal static BackgroundCommandOptions Parse(IReadOnlyList<string> args, out bool help)
    {
        help = false;
        if (args.Count < 2)
        {
            throw new UsageException("'background' requires start, status, stop, or restart");
        }

        if (args[1] == "owner") return ParseOwner(args);
        if (args[1] is "--help" or "-h")
        {
            help = true;
            ValidateHelpTail(args, 2);
            return new(BackgroundAction.Status, null);
        }

        var action = args[1] switch
        {
            "start" => BackgroundAction.Start,
            "status" => BackgroundAction.Status,
            "stop" => BackgroundAction.Stop,
            "restart" => BackgroundAction.Restart,
            _ => throw new UsageException($"unknown background action '{args[1]}'")
        };
        string? project = null;
        string? config = null;
        string? urls = null;
        var hasProject = false;
        var hasConfig = false;
        var hasUrls = false;
        var force = false;

        for (var index = 2; index < args.Count; index++)
        {
            var token = args[index];
            if (token is "--help" or "-h")
            {
                help = true;
                ValidateHelpTail(args, index + 1);
                break;
            }

            if (token is "--project" or "--config" or "--urls")
            {
                var value = ReadValue(args, ref index, token);
                switch (token)
                {
                    case "--project": if (hasProject) throw Repeated(token); project = value; hasProject = true; break;
                    case "--config": if (hasConfig) throw Repeated(token); config = value; hasConfig = true; break;
                    default: if (hasUrls) throw Repeated(token); urls = value; hasUrls = true; break;
                }
                continue;
            }

            if (TryReadEquals(token, "--project", out var projectValue))
            {
                if (hasProject) throw Repeated("--project");
                project = RequireValue(projectValue, "--project"); hasProject = true; continue;
            }
            if (TryReadEquals(token, "--config", out var configValue))
            {
                if (hasConfig) throw Repeated("--config");
                config = RequireValue(configValue, "--config"); hasConfig = true; continue;
            }
            if (TryReadEquals(token, "--urls", out var urlsValue))
            {
                if (hasUrls) throw Repeated("--urls");
                urls = RequireValue(urlsValue, "--urls"); hasUrls = true; continue;
            }
            if (token == "--force")
            {
                if (force) throw Repeated(token);
                force = true;
                continue;
            }
            if (token.StartsWith('-')) throw new UsageException($"unknown option '{token}'");
            throw new UsageException($"unexpected argument '{token}'");
        }

        if (hasProject && hasConfig) throw new UsageException("options '--project' and '--config' cannot be used together");
        if (!hasProject && !hasConfig) throw new UsageException("one of '--project <project-slug>' or '--config <path-to-project-json>' is required for background management");
        if (force && action != BackgroundAction.Stop) throw new UsageException("'--force' is only valid with 'background stop'");
        if (hasUrls && action is not (BackgroundAction.Start or BackgroundAction.Restart))
            throw new UsageException("'--urls' is only valid with 'background start' or 'background restart'");
        if (hasUrls && !IsLoopbackUrl(urls!)) throw new UsageException("'--urls' must be one HTTP loopback URL with an explicit port");

        ProjectSelector selector;
        if (hasProject)
        {
            if (!ProjectSlug.TryParse(project, out var slug, out var reason))
                throw new UsageException($"invalid project slug '{project}': {reason}");
            selector = new ProjectSelector.BySlug(slug);
        }
        else
        {
            selector = new ProjectSelector.ByConfigPath(config!);
        }

        return new BackgroundCommandOptions(action, selector, urls, force);
    }

    private static BackgroundCommandOptions ParseOwner(IReadOnlyList<string> args)
    {
        string? root = null, config = null, project = null, runId = null, urls = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Count; index++)
        {
            var option = args[index];
            if (option is not ("--root" or "--config" or "--project" or "--run-id" or "--urls"))
                throw new UsageException($"unknown internal background owner option '{option}'");
            if (!seen.Add(option)) throw Repeated(option);
            var value = ReadValue(args, ref index, option);
            switch (option)
            {
                case "--root": root = value; break;
                case "--config": config = value; break;
                case "--project": project = value; break;
                case "--run-id": runId = value; break;
                case "--urls": urls = value; break;
            }
        }

        if (root is null || config is null || project is null || runId is null || urls is null)
            throw new UsageException("internal background owner arguments are incomplete");
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(config))
            throw new UsageException("internal background owner paths must be absolute");
        if (!ProjectSlug.TryParse(project, out _, out var reason))
            throw new UsageException($"invalid background Project slug '{project}': {reason}");
        if (runId.Length is < 16 or > 64 || runId.Any(character => !Uri.IsHexDigit(character)))
            throw new UsageException("internal background run identity is invalid");
        if (!IsLoopbackUrl(urls)) throw new UsageException("'--urls' must be one HTTP loopback URL with an explicit port");
        return new BackgroundCommandOptions(BackgroundAction.Owner, null, urls, Owner: new(root, config, project, runId, urls));
    }

    internal static bool IsLoopbackUrl(string value)
    {
        if (value.Contains(';') || value.Contains(',') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            return false;
        return uri.Port is > 0 and <= 65535 && uri.IsLoopback;
    }

    private static void ValidateHelpTail(IReadOnlyList<string> args, int start)
    {
        for (var index = start; index < args.Count; index++)
            if (args[index] is not ("--project" or "--config" or "--urls" or "--force" or "-h" or "--help"))
                throw new UsageException($"unknown option '{args[index]}'");
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"option '{option}' requires a value");
        return args[++index];
    }

    private static string RequireValue(string value, string option) => string.IsNullOrWhiteSpace(value)
        ? throw new UsageException($"option '{option}' requires a value") : value;

    private static bool TryReadEquals(string token, string option, out string value)
    {
        var prefix = option + "=";
        if (!token.StartsWith(prefix, StringComparison.Ordinal)) { value = string.Empty; return false; }
        value = token[prefix.Length..];
        return true;
    }

    private static UsageException Repeated(string option) => new($"option '{option}' specified more than once");
}
