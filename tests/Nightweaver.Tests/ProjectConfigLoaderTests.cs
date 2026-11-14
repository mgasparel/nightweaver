using Nightweaver.Configuration;
using System.Text.Json;

namespace Nightweaver.Tests;

public sealed class ProjectConfigLoaderTests
{
    [Theory]
    [InlineData("{}", "OpenAi", "https://api.openai.com/v1/decisions", "gpt-6-luna")]
    [InlineData("{\"decisions_api_provider\":\"openrouter\"}", "OpenRouter", "https://openrouter.ai/api/alpha/decisions", "typesafe/jev-1.14")]
    [InlineData("{\"decisions_api_provider\":\"openrouter\",\"decisions_api_endpoint\":\"https://gateway.example.test/decisions\",\"decisions_api_model\":\"custom/model\"}", "OpenRouter", "https://gateway.example.test/decisions", "custom/model")]
    public void DecisionsSettingsApplyProviderDefaultsAndExplicitOverrides(string settings, string provider, string endpoint, string model)
    {
        using var temp = new TempDirectory();
        var values = JsonSerializer.Deserialize<Dictionary<string, object>>(settings)!;
        values["name"] = "demo";
        values["target_repository"] = new { path = "repository" };
        var path = temp.WriteFile("project.json", JsonSerializer.Serialize(values));
        var config = ProjectConfigLoader.Load(path, null, new HostPaths(temp.CreateDirectory("host")));

        Assert.Equal(provider, config.DecisionsApiProvider.ToString());
        Assert.Equal(endpoint, config.DecisionsApiEndpoint.AbsoluteUri);
        Assert.Equal(model, config.DecisionsApiModel);
    }

    [Fact]
    public void LegacyKeyLoadsButCannotBeCombinedWithRenamedKey()
    {
        using var temp = new TempDirectory();
        var host = new HostPaths(temp.CreateDirectory("host"));
        var path = temp.WriteFile("project.json", """
            {"name":"demo","target_repository":{"path":"repository"},"openai_api_key":"legacy-fixture-secret"}
            """);
        var config = ProjectConfigLoader.Load(path, null, host);
        Assert.Equal("legacy-fixture-secret", config.DecisionsApiKey);
        Assert.DoesNotContain("legacy-fixture-secret", config.ToString(), StringComparison.Ordinal);

        File.WriteAllText(path, """
            {"name":"demo","target_repository":{"path":"repository"},
             "openai_api_key":"legacy-fixture-secret","decisions_api_key":"new-fixture-secret"}
            """);
        var exception = Assert.Throws<ProjectConfigurationException>(() => ProjectConfigLoader.Load(path, null, host));
        Assert.Equal("$.decisions_api_key", exception.JsonPath);
        Assert.DoesNotContain("fixture-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("decisions_api_provider", "null")]
    [InlineData("decisions_api_provider", "\"unknown\"")]
    [InlineData("decisions_api_endpoint", "42")]
    [InlineData("decisions_api_endpoint", "\"relative/path\"")]
    [InlineData("decisions_api_endpoint", "\"http://example.test/decisions\"")]
    [InlineData("decisions_api_endpoint", "\"https://fixture-secret@example.test/decisions\"")]
    [InlineData("decisions_api_endpoint", "\"https://example.test/decisions?key=fixture-secret\"")]
    [InlineData("decisions_api_endpoint", "\"https://example.test/decisions#fixture-secret\"")]
    [InlineData("decisions_api_endpoint", "\"https://example.test/fixture secret\"")]
    [InlineData("decisions_api_model", "null")]
    [InlineData("decisions_api_model", "\"\"")]
    [InlineData("decisions_api_model", "\" fixture-secret\"")]
    [InlineData("decisions_api_model", "\"fixture-secret\\n\"")]
    public void DecisionsSettingsRejectInvalidValuesWithoutEchoingThem(string setting, string value)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("project.json",
            "{\"name\":\"demo\",\"target_repository\":{\"path\":\"repository\"},\"" + setting + "\":" + value + "}");
        var exception = Assert.Throws<ProjectConfigurationException>(() =>
            ProjectConfigLoader.Load(path, null, new HostPaths(temp.CreateDirectory("host"))));
        Assert.Equal("$." + setting, exception.JsonPath);
        Assert.DoesNotContain("fixture-secret", exception.ToString(), StringComparison.Ordinal);
    }

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
        Assert.Null(config.DecisionsApiKey);
    }

    [Theory]
    [InlineData("REPLACE_WITH_YOUR_KEY")]
    [InlineData("fixture-secret-without-required-prefix")]
    public void LoaderStoresOptionalKeyWithoutExposingItInRecordFormatting(string key)
    {
        using var temp = new TempDirectory();
        var configPath = temp.WriteFile("project.json", JsonSerializer.Serialize(new
        {
            name = "demo",
            target_repository = new { path = "repository" },
            decisions_api_key = key
        }));

        var config = ProjectConfigLoader.Load(configPath, null, new HostPaths(temp.CreateDirectory("host")));

        Assert.Equal(key, config.DecisionsApiKey);
        Assert.DoesNotContain(key, config.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, $"{config with { }}", StringComparison.Ordinal);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(config), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[\"fixture-secret\"]")]
    [InlineData("{\"value\":\"fixture-secret\"}")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\" fixture-secret\"")]
    [InlineData("\"fixture-secret \"")]
    [InlineData("\"fixture-secret\\nembedded\"")]
    [InlineData("\"fixture-secret\\u0000embedded\"")]
    [InlineData("\"fixture-secret\\u007fembedded\"")]
    public void LoaderRejectsInvalidKeysWithoutEchoingTheirValues(string jsonValue)
    {
        using var temp = new TempDirectory();
        var configPath = temp.WriteFile("project.json",
            "{\"name\":\"demo\",\"target_repository\":{\"path\":\"repository\"},\"decisions_api_key\":" + jsonValue + "}");

        var exception = Assert.Throws<ProjectConfigurationException>(() => ProjectConfigLoader.Load(
            configPath, null, new HostPaths(temp.CreateDirectory("host"))));

        Assert.Equal("$.decisions_api_key", exception.JsonPath);
        Assert.DoesNotContain("fixture-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderRejectsDuplicateKeysWithoutEchoingEitherValue()
    {
        using var temp = new TempDirectory();
        var configPath = temp.WriteFile("project.json", """
            {"name":"demo","target_repository":{"path":"repository"},
             "decisions_api_key":"fixture-secret-one","decisions_api_key":"fixture-secret-two"}
            """);

        var exception = Assert.Throws<ProjectConfigurationException>(() => ProjectConfigLoader.Load(
            configPath, null, new HostPaths(temp.CreateDirectory("host"))));

        Assert.Equal("$.decisions_api_key", exception.JsonPath);
        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderPreservesExplicitBranchAndLoadsLaterConfigurationSections()
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
        Assert.Equal(2, config.Concurrency.MaxConcurrentPlanning);
        Assert.Equal(3, config.Concurrency.MaxConcurrentDelivery);
        Assert.Equal(5, config.Concurrency.MaxActivePlanBeads);
        Assert.Equal(3, config.Limits.MaxRepairCycles);
        Assert.Equal(2, config.Limits.MaxOperationalRetries);
        Assert.Equal(AgentCli.Claude, config.AgentDefaults.Cli);
        Assert.Equal("high", config.Roles[AgentRole.Planner].Effort);
        Assert.Equal(7, config.Roles.InCanonicalOrder().Count());
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
