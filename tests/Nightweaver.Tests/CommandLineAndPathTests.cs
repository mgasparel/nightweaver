using Nightweaver.Cli;
using Nightweaver.Configuration;

namespace Nightweaver.Tests;

public sealed class CommandLineAndPathTests
{
    [Fact]
    public void ParserAcceptsSupportedForms()
    {
        var cases = new[]
        {
            new[] { "--project", "demo", "--dry-run" },
            new[] { "--project=demo", "--dry-run" },
            new[] { "--config", "project.json", "--dry-run" },
            new[] { "--config=project.json", "--dry-run" }
        };

        foreach (var args in cases)
        {
            var options = CommandLineParser.Parse(args);
            Assert.True(options.DryRun);
            Assert.False(options.Help);
            Assert.NotNull(options.Selector);
        }
    }

    [Fact]
    public void ParserReportsStrictUsageErrors()
    {
        var cases = new (string[] Args, string Error)[]
        {
            (["--bogus"], "unknown option '--bogus'"),
            (["stray"], "unexpected argument 'stray'"),
            (["--dry-run", "--dry-run"], "option '--dry-run' specified more than once"),
            (["--help", "-h"], "option '--help' specified more than once"),
            (["--project", "demo", "--project=other"], "option '--project' specified more than once"),
            (["--config"], "option '--config' requires a value"),
            (["--project", "--dry-run"], "option '--project' requires a value"),
            (["--project="], "option '--project' requires a value"),
            (["--config="], "option '--config' requires a value"),
            (["--project", "demo", "--config", "other.json", "--dry-run"], "options '--project' and '--config' cannot be used together"),
            ([], "one of '--project <project-slug>' or '--config <path-to-project-json>' is required"),
            (["--project", "bad.slug", "--dry-run"], "invalid project slug 'bad.slug':"),
            (["--project", "demo"], "live orchestration is not implemented yet; only '--dry-run' is supported")
        };

        foreach (var (args, error) in cases)
        {
            var exception = Assert.Throws<UsageException>(() => CommandLineParser.Parse(args));
            Assert.StartsWith(error, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HelpWinsOverSelectorErrorsAfterTokenization()
    {
        var options = CommandLineParser.Parse(["--help", "--project", "demo", "--config", "other.json"]);
        Assert.True(options.Help);
        Assert.Throws<UsageException>(() => CommandLineParser.Parse(["--help", "--bogus"]));
        const string expectedHelp =
            "Usage: nightweaver (--project <project-slug> | --config <path-to-project-json>) --dry-run\n" +
            "       nightweaver --help\n\n" +
            "Options:\n" +
            "  --project <project-slug>       Load <nightweaver-root>/projects/<project-slug>.json.\n" +
            "  --config <path-to-project-json> Load a Project configuration file.\n" +
            "  --dry-run                      Validate and print a hypothetical startup snapshot.\n" +
            "  --help, -h                     Print this help.\n\n" +
            "<nightweaver-root> is the directory containing the nightweaver executable.\n\n" +
            "Exit codes: 0 success, 1 inspection failure, 2 usage or configuration error.\n";
        Assert.Equal(expectedHelp, HelpText.Usage);
    }

    [Fact]
    public void ProjectSlugAcceptsSafeAsciiSlugsAndRejectsUnsafeNames()
    {
        var accepted = new[] { "a", "my-app", "A_1", new string('a', 64), "console", "nul1" };
        foreach (var value in accepted)
        {
            Assert.True(ProjectSlug.TryParse(value, out var slug, out var reason), $"{value}: {reason}");
            Assert.Equal(value, slug.Value);
        }

        var rejected = new[]
        {
            "", "-a", "_a", ".", "..", "a/b", "a\\b", "C:", "c:x", "a.b", "a b", "é",
            new string('a', 65), "con", "NUL", "Com1", "lpt9"
        };
        foreach (var value in rejected)
        {
            Assert.False(ProjectSlug.TryParse(value, out _, out _), value);
        }
    }

    [Fact]
    public void HostPathsAndConfigLocatorUseTheCorrectRoots()
    {
        using var temp = new TempDirectory();
        var host = Path.Combine(temp.Path, "host");
        var caller = Path.Combine(temp.Path, "unrelated");
        Directory.CreateDirectory(host);
        Directory.CreateDirectory(caller);
        var paths = new HostPaths(host + Path.DirectorySeparatorChar);
        var slug = ProjectSlug.Parse("demo");

        Assert.Equal(host, paths.Root);
        Assert.Equal(Path.Combine(host, "projects", "demo.json"), paths.ProjectConfigPath(slug));
        Assert.Equal(Path.Combine(host, "worktrees", "demo"), paths.WorktreesRoot(slug));
        Assert.Equal(Path.Combine(host, "tmp", "demo"), paths.TmpRoot(slug));
        Assert.Equal(Path.Combine(host, "logs", "demo"), paths.LogsRoot(slug));
        Assert.Equal(paths.ProjectConfigPath(slug), ProjectConfigLocator.ResolveConfigPath(new ProjectSelector.BySlug(slug), paths, caller));
        Assert.Equal(Path.GetFullPath(Path.Combine(caller, "..", "x.json")),
            ProjectConfigLocator.ResolveConfigPath(new ProjectSelector.ByConfigPath(Path.Combine("..", "x.json")), paths, caller));

        var absolute = Path.Combine(host, "special.json");
        Assert.Equal(absolute, ProjectConfigLocator.ResolveConfigPath(new ProjectSelector.ByConfigPath(absolute), paths, caller));
    }
}
