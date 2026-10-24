using Nightweaver.Configuration;

namespace Nightweaver.Tests;

public sealed class ProjectConfigSchemaTests
{
    [Fact]
    public void MinimalConfigurationAppliesEveryBuiltInDefault()
    {
        using var temp = new TempDirectory();
        var config = Load(temp, "{\"name\":\"demo\",\"target_repository\":{\"path\":\"repository\"}}");

        Assert.Equal("main", config.TargetRepository.DefaultBranch);
        Assert.Equal("main", ProjectConfigDefaults.DefaultBranch);
        Assert.Equal(2, config.Concurrency.MaxConcurrentPlanning);
        Assert.Equal(3, config.Concurrency.MaxConcurrentDelivery);
        Assert.Equal(5, config.Concurrency.MaxActivePlanBeads);
        Assert.Equal(1, ConcurrencyConfig.MergerSlots);
        Assert.Equal(3, config.Limits.MaxRepairCycles);
        Assert.Equal(2, config.Limits.MaxOperationalRetries);
        Assert.Equal(3, config.Limits.MaxAttemptsPerStage);
        Assert.Equal(new AgentSettings(AgentCli.Claude, null, "high", 30), ProjectConfigDefaults.AgentDefaults);
        Assert.Equal(ProjectConfigDefaults.AgentDefaults, config.AgentDefaults);
        Assert.Equal(AgentRoles.All, config.Roles.InCanonicalOrder().Select(pair => pair.Role));
        Assert.All(config.Roles.InCanonicalOrder(), pair => Assert.Equal(config.AgentDefaults, pair.Settings));
    }

    [Fact]
    public void EmptyOptionalObjectsApplyTheirDefaults()
    {
        using var temp = new TempDirectory();
        var config = Load(temp, """
            {"name":"demo","target_repository":{"path":"repository"},"concurrency":{},"limits":{},"agent_defaults":{},"roles":{}}
            """);

        Assert.Equal(2, config.Concurrency.MaxConcurrentPlanning);
        Assert.Equal(3, config.Concurrency.MaxConcurrentDelivery);
        Assert.Equal(5, config.Concurrency.MaxActivePlanBeads);
        Assert.Equal(3, config.Limits.MaxRepairCycles);
        Assert.Equal(2, config.Limits.MaxOperationalRetries);
        Assert.Equal(ProjectConfigDefaults.AgentDefaults, config.AgentDefaults);
        Assert.All(config.Roles.InCanonicalOrder(), pair => Assert.Equal(ProjectConfigDefaults.AgentDefaults, pair.Settings));
    }

    [Fact]
    public void FullSpecExampleResolvesEveryRole()
    {
        using var temp = new TempDirectory();
        var config = Load(temp, """
            {
              "name": "demo",
              "target_repository": { "path": "repository", "default_branch": "main" },
              "concurrency": { "max_concurrent_planning": 2, "max_concurrent_delivery": 3, "max_active_plan_beads": 5 },
              "limits": { "max_repair_cycles": 3, "max_operational_retries": 2 },
              "agent_defaults": { "cli": "claude", "effort": "high", "timeout_minutes": 30 },
              "roles": {
                "planner": { "cli": "claude", "model": "<model-id>", "effort": "high", "timeout_minutes": 30 },
                "plan_challenger": { "cli": "codex", "model": "<model-id>", "effort": "high", "timeout_minutes": 20 },
                "implementer": { "cli": "claude", "model": "<model-id>", "effort": "high", "timeout_minutes": 45 },
                "reviewer": { "cli": "codex", "model": "<model-id>", "effort": "high", "timeout_minutes": 20 },
                "qa": { "cli": "claude", "model": "<model-id>", "effort": "high", "timeout_minutes": 30 },
                "merger": { "cli": "claude", "model": "<model-id>", "effort": "medium", "timeout_minutes": 20 },
                "classifier": { "cli": "claude", "model": "<model-id>", "effort": "low", "timeout_minutes": 2 }
              }
            }
            """);

        Assert.Equal(new AgentSettings(AgentCli.Claude, null, "high", 30), config.AgentDefaults);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "<model-id>", "high", 30), config.Roles[AgentRole.Planner]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, "<model-id>", "high", 20), config.Roles[AgentRole.PlanChallenger]);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "<model-id>", "high", 45), config.Roles[AgentRole.Implementer]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, "<model-id>", "high", 20), config.Roles[AgentRole.Reviewer]);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "<model-id>", "high", 30), config.Roles[AgentRole.Qa]);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "<model-id>", "medium", 20), config.Roles[AgentRole.Merger]);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "<model-id>", "low", 2), config.Roles[AgentRole.Classifier]);
    }

    [Fact]
    public void PartialRoleOverridesMergePropertyByPropertyForAllRoles()
    {
        using var temp = new TempDirectory();
        var config = Load(temp, """
            {
              "name":"demo",
              "target_repository":{"path":"repository"},
              "agent_defaults":{"cli":"codex","model":"m1","effort":"low","timeout_minutes":12},
              "roles":{
                "planner":{"cli":"claude"},
                "plan_challenger":{"model":"m2"},
                "implementer":{"effort":"medium"},
                "reviewer":{"timeout_minutes":13},
                "qa":{"model":null},
                "merger":{"cli":"claude"},
                "classifier":{"effort":"high"}
              }
            }
            """);

        Assert.Equal(new AgentSettings(AgentCli.Codex, "m1", "low", 12), config.AgentDefaults);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "m1", "low", 12), config.Roles[AgentRole.Planner]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, "m2", "low", 12), config.Roles[AgentRole.PlanChallenger]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, "m1", "medium", 12), config.Roles[AgentRole.Implementer]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, "m1", "low", 13), config.Roles[AgentRole.Reviewer]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, null, "low", 12), config.Roles[AgentRole.Qa]);
        Assert.Equal(new AgentSettings(AgentCli.Claude, "m1", "low", 12), config.Roles[AgentRole.Merger]);
        Assert.Equal(new AgentSettings(AgentCli.Codex, "m1", "high", 12), config.Roles[AgentRole.Classifier]);
    }

    [Fact]
    public void ExplicitModelNullIsPresentAndClearsAnInheritedModel()
    {
        var inherited = new AgentSettings(AgentCli.Claude, "inherited", "high", 30);
        var absent = AgentSettingsResolver.Apply(inherited, AgentSettingsOverride.None);
        var clearModel = AgentSettingsResolver.Apply(inherited, AgentSettingsOverride.None with
        {
            Model = Optional<string?>.Of(null)
        });

        Assert.Equal("inherited", absent.Model);
        Assert.Null(clearModel.Model);

        using var temp = new TempDirectory();
        var config = Load(temp, """
            {"name":"demo","target_repository":{"path":"repository"},"agent_defaults":{"model":"m1"},"roles":{"qa":{"model":null}}}
            """);
        Assert.Equal("m1", config.AgentDefaults.Model);
        Assert.Equal("m1", config.Roles[AgentRole.Planner].Model);
        Assert.Null(config.Roles[AgentRole.Qa].Model);
    }

    [Fact]
    public void RejectsSchemaErrorsAtTheirExactPropertyPaths()
    {
        var invalidConfigs = new (string Json, string Path)[]
        {
            ("{", "$"),
            ("[]", "$"),
            ("null", "$"),
            ("{\"name\":\"demo\",\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"}}", "$.name"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"path\":\"repo\"}}", "$.target_repository.path"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":1,\"max_concurrent_planning\":1}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"merger\":{\"cli\":\"claude\",\"cli\":\"claude\"}}}", "$.roles.merger.cli"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"unknown\":true}", "$.unknown"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"unknown\":true}}", "$.target_repository.unknown"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"unknown\":true}}", "$.concurrency.unknown"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"limits\":{\"unknown\":true}}", "$.limits.unknown"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"unknown\":true}}", "$.agent_defaults.unknown"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"tester\":{}}}", "$.roles.tester"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"Planner\":{}}}", "$.roles.Planner"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"qa\":{\"unknown\":true}}}", "$.roles.qa.unknown"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"a b\":true}", "$['a b']"),
            ("{\"name\":null,\"target_repository\":{\"path\":\"repo\"}}", "$.name"),
            ("{\"name\":\"demo\",\"target_repository\":null}", "$.target_repository"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":null}}", "$.target_repository.path"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"default_branch\":null}}", "$.target_repository.default_branch"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":null}", "$.concurrency"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"limits\":null}", "$.limits"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":null}", "$.agent_defaults"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":null}", "$.roles"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"qa\":null}}", "$.roles.qa"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":\"3\"}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":true}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":[]}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":{}}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":2.0}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":1e1}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":2147483648}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_planning\":0}}", "$.concurrency.max_concurrent_planning"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_delivery\":0}}", "$.concurrency.max_concurrent_delivery"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_active_plan_beads\":0}}", "$.concurrency.max_active_plan_beads"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"limits\":{\"max_repair_cycles\":-1}}", "$.limits.max_repair_cycles"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"limits\":{\"max_operational_retries\":-1}}", "$.limits.max_operational_retries"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_concurrent_delivery\":null}}", "$.concurrency.max_concurrent_delivery"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"concurrency\":{\"max_active_plan_beads\":null}}", "$.concurrency.max_active_plan_beads"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"limits\":{\"max_repair_cycles\":null}}", "$.limits.max_repair_cycles"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"limits\":{\"max_operational_retries\":null}}", "$.limits.max_operational_retries"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"timeout_minutes\":0}}", "$.agent_defaults.timeout_minutes"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"reviewer\":{\"timeout_minutes\":0}}}", "$.roles.reviewer.timeout_minutes"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"cli\":\"Claude\"}}", "$.agent_defaults.cli"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"cli\":\"gemini\"}}", "$.agent_defaults.cli"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"cli\":\"\"}}", "$.agent_defaults.cli"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"cli\":null}}", "$.agent_defaults.cli"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"effort\":null}}", "$.agent_defaults.effort"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"timeout_minutes\":null}}", "$.agent_defaults.timeout_minutes"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"qa\":{\"cli\":null}}}", "$.roles.qa.cli"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"qa\":{\"effort\":null}}}", "$.roles.qa.effort"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"qa\":{\"timeout_minutes\":null}}}", "$.roles.qa.timeout_minutes"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"model\":42}}", "$.agent_defaults.model"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"effort\":\" \"}}", "$.agent_defaults.effort"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"effort\":\" padded \"}}", "$.agent_defaults.effort"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"model\":\"\"}}", "$.agent_defaults.model"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"model\":\" model \"}}", "$.agent_defaults.model"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"agent_defaults\":{\"model\":\"bad\\u007fvalue\"}}", "$.agent_defaults.model"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\"},\"roles\":{\"qa\":{\"effort\":\"bad\\u0001value\"}}}", "$.roles.qa.effort"),
            ("{\"name\":\"demo\",\"target_repository\":{\"path\":\"repo\",\"default_branch\":\" main \"}}", "$.target_repository.default_branch")
        };

        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var configPath = Path.Combine(temp.Path, "project.json");
        foreach (var (json, expectedPath) in invalidConfigs)
        {
            File.WriteAllText(configPath, json);
            var exception = Assert.Throws<ProjectConfigurationException>(() =>
                ProjectConfigLoader.Load(configPath, null, new HostPaths(host)));
            Assert.NotEmpty(exception.Errors);
            Assert.Equal(expectedPath, exception.Errors[0].JsonPath);
            Assert.Equal(Path.GetFullPath(configPath), exception.ConfigFilePath);
        }
    }

    [Fact]
    public void LoaderLeavesGitSyntaxValidationToTheGitInspectionStage()
    {
        using var temp = new TempDirectory();
        var config = Load(temp, "{\"name\":\"demo\",\"target_repository\":{\"path\":\"repository\",\"default_branch\":\"bad..branch\"}}");

        Assert.Equal("bad..branch", config.TargetRepository.DefaultBranch);
    }

    private static ProjectConfig Load(TempDirectory temp, string json)
    {
        var host = temp.CreateDirectory("host");
        var configPath = temp.WriteFile("project.json", json);
        return ProjectConfigLoader.Load(configPath, null, new HostPaths(host));
    }
}
