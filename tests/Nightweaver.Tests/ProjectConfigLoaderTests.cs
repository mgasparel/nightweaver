using Nightweaver.Configuration;
using System.Text.Json;

namespace Nightweaver.Tests;

public sealed class ProjectConfigLoaderTests
{
    [Fact]
    public void LoaderAppliesMinimalDefaultsAndResolvesTargetPathFromConfigDirectory()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var repository = temp.CreateDirectory("repository");
        var configPath = temp.WriteFile("host/settings/demo.json", "{\"name\":\"demo\",\"target_repository\":{\"path\":\"../../repository\"}}");

        var config = ProjectConfigLoader.Load(configPath, ProjectSlug.Parse("demo"), new HostPaths(host));

        Assert.Equal("demo", config.Name.Value);
        Assert.Equal(Path.GetFullPath(configPath), config.ConfigFilePath);
        Assert.Equal(repository, config.TargetRepository.Path);
        Assert.Equal("main", config.TargetRepository.DefaultBranch);
        Assert.Equal("nightweaver:demo", config.RecoveryActor);
    }

    [Fact]
    public void LoaderPreservesExplicitBranchAndIgnoresLaterConfigurationSections()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var repository = temp.CreateDirectory("repository");
        var configPath = temp.WriteFile("project.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = repository, default_branch = "release/test" },
            concurrency = new { max_concurrent_planning = 2 },
            limits = new { max_repair_cycles = 3 },
            agent_defaults = new { cli = "claude" },
            roles = new { planner = new { effort = "high" } }
        }));

        var config = ProjectConfigLoader.Load(configPath, null, new HostPaths(host));

        Assert.Equal(repository, config.TargetRepository.Path);
        Assert.Equal("release/test", config.TargetRepository.DefaultBranch);
    }

    [Fact]
    public void LoaderRejectsMalformedJsonAndConfigurationShapesWithJsonPaths()
    {
        var cases = new (string Json, string? JsonPath)[]
        {
            ("{", "$"),
            ("{\"name\":\"demo\",}", "$"),
            ("{ /* comment */ \"name\":\"demo\"}", "$"),
            ("[]", "$"),
            ("{}", "$.name"),
            ("{\"name\":7}", "$.name"),
            ("{\"name\":\"bad.slug\"}", "$.name"),
            ("{\"name\":\"demo\"}", "$.target_repository"),
            ("{\"name\":\"demo\",\"target_repository\":null}", "$.target_repository"),
            ("{\"name\":\"demo\",\"target_repository\":{}}", "$.target_repository.path"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\" \"}}", "$.target_repository.path"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":42}}", "$.target_repository.path"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"default_branch\":null}}", "$.target_repository.default_branch"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"default_branch\":\" \"}}", "$.target_repository.default_branch"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"default_branch\":1}}", "$.target_repository.default_branch"),
            ("{\"name\":\"demo\",\"name\":\"other\",\"target_repository\":{\"path\":\"repo\"}}", "$.name")
        };

        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var configPath = Path.Combine(temp.Path, "config.json");
        foreach (var (json, jsonPath) in cases)
        {
            File.WriteAllText(configPath, json);
            var exception = Assert.Throws<ProjectConfigurationException>(() =>
                ProjectConfigLoader.Load(configPath, null, new HostPaths(host)));
            Assert.Equal(jsonPath, exception.JsonPath);
            Assert.Equal(Path.GetFullPath(configPath), exception.ConfigFilePath);
        }
    }

    [Fact]
    public void LoaderReportsMissingFileAndProjectNameMismatch()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var missing = Path.Combine(host, "projects", "demo.json");
        var missingError = Assert.Throws<ProjectConfigurationException>(() =>
            ProjectConfigLoader.Load(missing, ProjectSlug.Parse("demo"), new HostPaths(host)));
        Assert.Contains("Project configuration not found", missingError.Message, StringComparison.Ordinal);
        Assert.Contains(host, missingError.Message, StringComparison.Ordinal);

        var configPath = temp.WriteFile("case.json", "{\"name\":\"Demo\",\"target_repository\":{\"path\":\"repository\"}}");
        var mismatch = Assert.Throws<ProjectConfigurationException>(() =>
            ProjectConfigLoader.Load(configPath, ProjectSlug.Parse("demo"), new HostPaths(host)));
        Assert.Equal("$.name", mismatch.JsonPath);
        Assert.Equal("name 'Demo' does not match project slug 'demo'", mismatch.Message);
    }

    [Fact]
    public void LoaderRejectsHostRootEqualToOrInsideTarget()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var configPath = temp.WriteFile("same.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = host }
        }));

        var exception = Assert.Throws<ProjectConfigurationException>(() =>
            ProjectConfigLoader.Load(configPath, null, new HostPaths(host)));
        Assert.Equal("$.target_repository.path", exception.JsonPath);
        Assert.Contains("must be outside the Target Repository", exception.Message, StringComparison.Ordinal);

        var parent = temp.CreateDirectory("parent");
        var nestedHost = Directory.CreateDirectory(Path.Combine(parent, "nightweaver")).FullName;
        var parentConfig = temp.WriteFile("parent.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = parent }
        }));
        Assert.Throws<ProjectConfigurationException>(() =>
            ProjectConfigLoader.Load(parentConfig, null, new HostPaths(nestedHost)));
    }

    [Fact]
    public void LoaderAcceptsASiblingPathWithACommonPrefix()
    {
        using var temp = new TempDirectory();
        var repository = temp.CreateDirectory("repo");
        var host = temp.CreateDirectory("repo2");
        var configPath = temp.WriteFile("sibling.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = repository }
        }));

        var config = ProjectConfigLoader.Load(configPath, null, new HostPaths(host));
        Assert.Equal(repository, config.TargetRepository.Path);
    }
}
