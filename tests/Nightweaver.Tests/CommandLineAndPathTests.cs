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
        Assert.Contains("nightweaver dispatch <bead-id>", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("Exit codes: 0 success, 1 dispatch/inspection failure, 2 usage or configuration error.", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserAcceptsDispatchSelectorsRoleOverridesAndHelp()
    {
        var automatic = CommandLineParser.Parse(["dispatch", "ns-du2.4", "--project=demo"]);
        Assert.Equal("ns-du2.4", automatic.Dispatch!.BeadId);
        Assert.Null(automatic.Dispatch.ExplicitRole);
        Assert.IsType<ProjectSelector.BySlug>(automatic.Selector);

        var explicitRole = CommandLineParser.Parse(["dispatch", "ns-du2.4", "--role", "reviewer", "--config=project.json"]);
        Assert.Equal(AgentRole.Reviewer, explicitRole.Dispatch!.ExplicitRole);
        Assert.IsType<ProjectSelector.ByConfigPath>(explicitRole.Selector);

        var helpWithoutTarget = CommandLineParser.Parse(["dispatch", "--help"]);
        Assert.True(helpWithoutTarget.Help);
        Assert.Null(helpWithoutTarget.Dispatch!.BeadId);
        var helpWithTarget = CommandLineParser.Parse(["dispatch", "ns-du2.4", "--help", "--project", "demo"]);
        Assert.True(helpWithTarget.Help);
        Assert.Equal("ns-du2.4", helpWithTarget.Dispatch!.BeadId);
        Assert.Throws<UsageException>(() => CommandLineParser.Parse(["dispatch", "--help", "--unknown"]));
        Assert.Throws<UsageException>(() => CommandLineParser.Parse(["dispatch", "ns-du2.4", "--help", "--role"]));
    }

    [Fact]
    public void ParserRejectsInvalidDispatchSyntaxAndUnsafeIds()
    {
        var cases = new (string[] Args, string Error)[]
        {
            (["dispatch"], "requires one Bead ID"),
            (["dispatch", "ns-du2.4"], "one of '--project"),
            (["dispatch", "ns-du2.4", "extra", "--project", "demo"], "unexpected argument 'extra'"),
            (["dispatch", "ns-du2.4", "--project", "demo", "--config", "x.json"], "cannot be used together"),
            (["dispatch", "ns-du2.4", "--project=demo", "--project", "other"], "specified more than once"),
            (["dispatch", "ns-du2.4", "--role"], "option '--role' requires a value"),
            (["dispatch", "ns-du2.4", "--role="], "option '--role' requires a value"),
            (["dispatch", "ns-du2.4", "--role", "Classifier"], "unknown lifecycle role"),
            (["dispatch", "ns-du2.4", "--role", "classifier"], "not a lifecycle role"),
            (["dispatch", "ns-du2.4", "--dry-run", "--project", "demo"], "cannot be combined"),
            (["dispatch", "../escape", "--project", "demo"], "unsafe Bead ID"),
            (["dispatch", "CON.notes", "--project", "demo"], "unsafe Bead ID"),
            (["--project", "demo", "--role", "reviewer", "--dry-run"], "only valid with 'dispatch'")
        };
        foreach (var (args, error) in cases)
        {
            var exception = Assert.Throws<UsageException>(() => CommandLineParser.Parse(args));
            Assert.Contains(error, exception.Message, StringComparison.Ordinal);
        }
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
