using System.Text;
using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DispatchCliIntegrationTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public async Task PublishedDispatchRunsInferredAndExplicitStagesAndRefusesIneligibleWork()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var rootHead = target.Run("git", ["rev-parse", "HEAD"]).Trim();
        var rootBranch = target.Run("git", ["branch", "--show-current"]).Trim();
        var rootStatus = target.Run("git", ["status", "--porcelain"]);
        var implementer = target.Create("Dispatch implementer", type: "task");

        var inferred = await fixture.RunAsync(["dispatch", implementer, "--project=demo"], "review");
        Assert.Equal(0, inferred.ExitCode);
        Assert.Empty(inferred.StandardError);
        Assert.Contains("role 'implementer' (inferred)", inferred.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Attempt 1/1 started", inferred.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Succeeded:", inferred.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, implementer, "stage:review");
        AssertRetainedExecution(fixture, implementer);

        var explicitDispatch = await fixture.RunAsync(
            ["dispatch", implementer, "--role=reviewer", "--config=" + Path.GetRelativePath(fixture.Caller, fixture.Config)],
            "review-qa");
        Assert.Equal(0, explicitDispatch.ExitCode);
        Assert.Empty(explicitDispatch.StandardError);
        Assert.Contains("role 'reviewer' (explicit)", explicitDispatch.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, implementer, "stage:qa");

        var product = target.Create("Dispatch planner", type: "feature");
        target.Run("bd", ["update", product, "--add-label", "stage:plan"]);
        var planning = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "plan-challenge");
        Assert.Equal(0, planning.ExitCode);
        Assert.Empty(planning.StandardError);
        Assert.Contains("role 'planner' (inferred)", planning.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, product, "stage:plan-challenge");

        var blocker = target.Create("Dispatch blocker", type: "task");
        var blocked = target.Create("Dispatch blocked", type: "task");
        target.Run("bd", ["dep", "add", blocked, blocker]);
        var refused = await fixture.RunAsync(["dispatch", blocked, "--project", "demo"], "review");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("native unassigned ready set", refused.StandardErrorText, StringComparison.Ordinal);
        var blockedAfter = ReadBead(target, blocked);
        Assert.Equal("open", blockedAfter.Status);
        Assert.Null(blockedAfter.Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(blocked)));
        Assert.Equal(rootHead, target.Run("git", ["rev-parse", "HEAD"]).Trim());
        Assert.Equal(rootBranch, target.Run("git", ["branch", "--show-current"]).Trim());
        Assert.Equal(rootStatus, target.Run("git", ["status", "--porcelain"]));
    }

    [Fact]
    public async Task PublishedDispatchEscalatesAnAgentThatExitsWithoutAValidTransition()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var beadId = fixture.Target.Create("Dispatch invalid transition", type: "task");

        var result = await fixture.RunAsync(["dispatch", beadId, "--project", "demo"], "noop");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Escalated to needs-human", result.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains("Attempt 1/1 started", result.StandardOutputText, StringComparison.Ordinal);
        var bead = ReadBead(fixture.Target, beadId);
        Assert.Equal("open", bead.Status);
        Assert.Null(bead.Assignee);
        Assert.Contains("needs-human", bead.Labels);
        Assert.False(Directory.Exists(fixture.WorktreePath(beadId)));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", beadId), "*.log"));
    }

    private static void AssertSuccessfulHandoff(BeadsFixture target, string beadId, string expectedStage)
    {
        var bead = ReadBead(target, beadId);
        Assert.Equal("open", bead.Status);
        Assert.Null(bead.Assignee);
        Assert.Contains(expectedStage, bead.Labels);
    }

    private static BeadSnapshot ReadBead(BeadsFixture target, string beadId) =>
        BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments)).ById[beadId];

    private static void AssertRetainedExecution(LiveDispatchFixture fixture, string beadId)
    {
        Assert.True(Directory.Exists(fixture.WorktreePath(beadId)));
        Assert.Equal("nightweaver/" + beadId, fixture.Target.Run("git", ["-C", fixture.WorktreePath(beadId), "branch", "--show-current"]).Trim());
        Assert.True(Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", beadId), "*.log").Any());
    }

    private sealed class LiveDispatchFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private readonly string fakeCliDirectory;
        private readonly string origin;

        internal BeadsFixture Target { get; } = new("target repository with spaces");
        internal string Host { get; }
        internal string Caller { get; }
        internal string Config { get; }

        internal LiveDispatchFixture(PublishedCliArtifactFixture published)
        {
            Host = temp.CreateDirectory("nightweaver host with spaces");
            Caller = temp.CreateDirectory("unrelated caller with spaces");
            published.CopyPublishedAppTo(Host);
            Config = Path.Combine(Host, "projects", "demo.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
            Directory.CreateDirectory(Path.Combine(Host, "prompts"));
            foreach (var role in new[] { "implementer", "reviewer", "planner" })
                File.WriteAllText(Path.Combine(Host, "prompts", role + ".md"), $"Fixture prompt for {role}.\n", new UTF8Encoding(false));

            WriteProjectConfig();
            Target.Run("git", ["config", "user.name", "Nightweaver Dispatch Fixture"]);
            Target.Run("git", ["config", "user.email", "fixture@example.invalid"]);
            File.WriteAllText(Path.Combine(Target.Path, "README.md"), "dispatch base\n", new UTF8Encoding(false));
            Target.Run("git", ["add", "README.md"]);
            Target.Run("git", ["commit", "-m", "dispatch fixture base"]);
            origin = Path.Combine(Target.Root, "bare origin.git");
            Directory.CreateDirectory(origin);
            Target.Run("git", ["init", "--bare", origin]);
            Target.Run("git", ["remote", "add", "origin", origin]);
            Target.Run("git", ["push", "--set-upstream", "origin", "main"]);

            fakeCliDirectory = temp.CreateDirectory("native fake agent cli");
            CopyFixtureCli("AgentInvocationFixture.exe");
            CopyFixtureCli("AgentInvocationFixture.dll");
            CopyFixtureCli("AgentInvocationFixture.deps.json");
            CopyFixtureCli("AgentInvocationFixture.runtimeconfig.json");
        }

        internal string WorktreePath(string beadId) => Path.Combine(Host, "worktrees", "demo", beadId);

        internal async Task<RawProcessResult> RunAsync(IReadOnlyList<string> arguments, string action)
        {
            var environment = new Dictionary<string, string?>(Target.Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = "nightweaver:demo",
                ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                ["PATH"] = fakeCliDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            };
            var executable = Path.Combine(Host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
            return await ProcessTool.RunAsync(executable, arguments, Caller, environment, TimeSpan.FromMinutes(2));
        }

        private void WriteProjectConfig()
        {
            var json = JsonSerializer.Serialize(new
            {
                name = "demo",
                target_repository = new { path = Target.Path, default_branch = "main" },
                limits = new { max_operational_retries = 0 },
                agent_defaults = new { cli = "claude", effort = "low", timeout_minutes = 1 }
            });
            File.WriteAllText(Config, json, new UTF8Encoding(false));
        }

        private void CopyFixtureCli(string name)
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException($"Native Agent fixture component '{name}' was not built.", source);
            File.Copy(source, Path.Combine(fakeCliDirectory, name), overwrite: true);
            if (name == "AgentInvocationFixture.exe")
            {
                File.Copy(source, Path.Combine(fakeCliDirectory, "claude.exe"), overwrite: true);
                File.Copy(source, Path.Combine(fakeCliDirectory, "codex.exe"), overwrite: true);
            }
        }

        public void Dispose()
        {
            temp.Dispose();
            Target.Dispose();
        }
    }
}
