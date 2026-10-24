using System.Text;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class PromptPackagingIntegrationTests
{
    private static readonly (AgentRole Role, string FileName)[] PromptFiles =
    [
        (AgentRole.Planner, "planner.md"),
        (AgentRole.PlanChallenger, "plan-challenger.md"),
        (AgentRole.Implementer, "implementer.md"),
        (AgentRole.Reviewer, "reviewer.md"),
        (AgentRole.Qa, "qa.md"),
        (AgentRole.Merger, "merger.md")
    ];

    [Fact]
    public async Task DotnetBuildAndPublishCopyEditablePromptsAndPreparationReadsOutputCopies()
    {
        using var temp = new TempDirectory();
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "Nightweaver", "Nightweaver.csproj");
        var buildOutput = Path.Combine(temp.Path, "dedicated build output");
        var publishOutput = Path.Combine(temp.Path, "dedicated publish output");

        var build = await ProcessTool.RunAsync("dotnet", ["build", projectPath, "--configuration", "Release", "--output", buildOutput, "-p:UseAppHost=true"],
            repositoryRoot, null, TimeSpan.FromMinutes(3));
        Assert.True(build.ExitCode == 0, $"dotnet build failed (exit {build.ExitCode})\n{build.StandardOutputText}\n{build.StandardErrorText}");

        var publish = await ProcessTool.RunAsync("dotnet", ["publish", projectPath, "--configuration", "Release", "--self-contained", "false", "-p:UseAppHost=true", "--output", publishOutput],
            repositoryRoot, null, TimeSpan.FromMinutes(3));
        Assert.True(publish.ExitCode == 0, $"dotnet publish failed (exit {publish.ExitCode})\n{publish.StandardOutputText}\n{publish.StandardErrorText}");

        foreach (var output in new[] { buildOutput, publishOutput })
        {
            var host = new HostPaths(output);
            foreach (var (role, fileName) in PromptFiles)
            {
                var source = Path.Combine(repositoryRoot, "prompts", fileName);
                var copied = Path.Combine(output, "prompts", fileName);
                Assert.True(File.Exists(copied), $"Prompt missing from output: {copied}");
                Assert.Equal(await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(copied, TestContext.Current.CancellationToken));

                var projectConfig = CreateProject();
                var prepared = await new AgentInvocationPreparer().PrepareAsync(host, projectConfig, role,
                    new("nw-package", "task", null, 0), Path.Combine(host.WorktreesRoot(projectConfig.Name), "nw-package"), TestContext.Current.CancellationToken);
                Assert.Contains(await File.ReadAllTextAsync(copied, Encoding.UTF8, TestContext.Current.CancellationToken), prepared.StandardInput, StringComparison.Ordinal);
            }

            var editedCopy = Path.Combine(output, "prompts", "planner.md");
            await File.WriteAllTextAsync(editedCopy, "Output-only operator edit {literal}\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
            var edited = await new AgentInvocationPreparer().PrepareAsync(host, CreateProject(), AgentRole.Planner,
                new("nw-package", "feature", null, 0), Path.Combine(host.WorktreesRoot(ProjectSlug.Parse("demo")), "nw-package"), TestContext.Current.CancellationToken);
            Assert.EndsWith("\n\nOutput-only operator edit {literal}\n", edited.StandardInput, StringComparison.Ordinal);
        }
    }

    private static ProjectConfig CreateProject()
    {
        var settings = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(AgentCli.Codex, null, "medium", 10));
        return new ProjectConfig(ProjectSlug.Parse("demo"), "config.json", new("target", "main"),
            new(1, 1, 1), new(3, 2), new(AgentCli.Codex, null, "medium", 10), new RoleSettings(settings));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Nightweaver.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException($"Could not locate Nightweaver.slnx above '{AppContext.BaseDirectory}'");
    }
}
