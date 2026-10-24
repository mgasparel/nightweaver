using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class AgentInvocationPreparerTests
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
    public async Task PreparesExactHeaderAndReadsCurrentTemplateForEveryShippedRole()
    {
        using var temp = new TempDirectory();
        var host = CreateHost(temp.Path, role => $"Template for {role}: {{literal}} – café\n\nKeep trailing newline\n");
        var project = CreateProject(defaultBranch: "release/next", maxRepairCycles: 7);
        var preparer = new AgentInvocationPreparer(new FixedTimeProvider(DateTimeOffset.Parse("2026-10-24T12:34:56Z", CultureInfo.InvariantCulture)));
        var worktree = CanonicalWorktree(host, project, "ns-xci.2");

        foreach (var (role, fileName) in PromptFiles)
        {
            var body = File.ReadAllText(Path.Combine(host.Root, "prompts", fileName), Encoding.UTF8);
            var prepared = await preparer.PrepareAsync(host, project, role,
                new("ns-xci.2", "task", "stage:review", 3), worktree, TestContext.Current.CancellationToken);

            Assert.Equal(
                "Bead ID: ns-xci.2\nBead type: task\nStage: stage:review\ndefault_branch: release/next\nRemaining repair budget: 4\n\n" + body,
                prepared.StandardInput);
        }

        var editedPath = Path.Combine(host.Root, "prompts", "reviewer.md");
        await File.WriteAllTextAsync(editedPath, "Edited after the first dispatch {verbatim}\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var secondRead = await preparer.PrepareAsync(host, project, AgentRole.Reviewer,
            new("ns-xci.2", "task", null, 7), worktree, TestContext.Current.CancellationToken);
        Assert.Equal(
            "Bead ID: ns-xci.2\nBead type: task\nStage: (none)\ndefault_branch: release/next\nRemaining repair budget: 0\n\nEdited after the first dispatch {verbatim}\n",
            secondRead.StandardInput);

        var overBudget = await preparer.PrepareAsync(host, project, AgentRole.Planner,
            new("ns-xci.2", "feature", null, 8), worktree, TestContext.Current.CancellationToken);
        Assert.Contains("Remaining repair budget: -1\n\n", overBudget.StandardInput, StringComparison.Ordinal);

        var zeroBudget = await preparer.PrepareAsync(host, CreateProject(maxRepairCycles: 0), AgentRole.Planner,
            new("ns-xci.2", "feature", null, 0), worktree, TestContext.Current.CancellationToken);
        Assert.Contains("Remaining repair budget: 0\n\n", zeroBudget.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildsExactClaudeAndCodexArgumentsWithOptionalModelsAndResolvedTimeout()
    {
        using var temp = new TempDirectory();
        var host = CreateHost(temp.Path);
        var worktree = Path.Combine(host.Root, "worktrees", "demo", "ns-xci.2");
        var claudeProject = CreateProject(AgentCli.Claude, model: "model name with spaces", effort: "high", timeoutMinutes: 11);
        var claude = await new AgentInvocationPreparer().PrepareAsync(host, claudeProject, AgentRole.Implementer,
            new("ns-xci.2", "task", "stage:implement", 0), worktree, TestContext.Current.CancellationToken);
        Assert.Equal("claude", claude.Executable);
        Assert.Equal(["-p", "--dangerously-skip-permissions", "--no-session-persistence", "--model", "model name with spaces", "--effort", "high"], claude.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(11), claude.Timeout);

        var claudeWithoutModel = await new AgentInvocationPreparer().PrepareAsync(host,
            CreateProject(AgentCli.Claude, model: null, effort: "low"), AgentRole.Reviewer,
            new("ns-xci.2", "task", "stage:review", 0), worktree, TestContext.Current.CancellationToken);
        Assert.Equal(["-p", "--dangerously-skip-permissions", "--no-session-persistence", "--effort", "low"], claudeWithoutModel.Arguments);

        var codexProject = CreateProject(AgentCli.Codex, model: "codex model", effort: "medium", timeoutMinutes: 23);
        var codex = await new AgentInvocationPreparer().PrepareAsync(host, codexProject, AgentRole.Qa,
            new("ns-xci.2", "task", "stage:qa", 1), worktree, TestContext.Current.CancellationToken);
        Assert.Equal("codex", codex.Executable);
        Assert.Equal(["exec", "--dangerously-bypass-approvals-and-sandbox", "--ephemeral", "--model", "codex model", "-c", "model_reasoning_effort=\"medium\"", "-"], codex.Arguments);
        Assert.Equal("model_reasoning_effort=\"medium\"", codex.Arguments[^2]);
        Assert.Equal("-", codex.Arguments[^1]);
        Assert.Equal(TimeSpan.FromMinutes(23), codex.Timeout);

        var codexWithoutModel = await new AgentInvocationPreparer().PrepareAsync(host,
            CreateProject(AgentCli.Codex, model: null, effort: "low"), AgentRole.Merger,
            new("ns-xci.2", "task", "stage:merge", 0), worktree, TestContext.Current.CancellationToken);
        Assert.Equal(["exec", "--dangerously-bypass-approvals-and-sandbox", "--ephemeral", "-c", "model_reasoning_effort=\"low\"", "-"], codexWithoutModel.Arguments);
    }

    [Fact]
    public async Task ResolvesCanonicalPathsAndFourEnvironmentOverridesWithoutCreatingFiles()
    {
        using var temp = new TempDirectory();
        var host = CreateHost(temp.Path);
        var project = CreateProject();
        var worktree = CanonicalWorktree(host, project, "ns-xci.2");
        var ambientTmp = Environment.GetEnvironmentVariable("TMP");
        var ambientTemp = Environment.GetEnvironmentVariable("TEMP");
        var prepared = await new AgentInvocationPreparer().PrepareAsync(host, project, AgentRole.Implementer,
            new("ns-xci.2", "task", "stage:implement", 1), worktree, TestContext.Current.CancellationToken);

        var expectedIdPrefix = "^\\d{8}T\\d{13}Z-implementer$";
        Assert.Matches(expectedIdPrefix, prepared.InvocationId);
        Assert.Equal(worktree, prepared.WorkingDirectory);
        Assert.Equal(Path.Combine(host.TmpRoot(project.Name), "ns-xci.2", prepared.InvocationId), prepared.TemporaryDirectory);
        Assert.Equal(Path.Combine(host.LogsRoot(project.Name), "ns-xci.2", prepared.InvocationId + ".log"), prepared.LogFilePath);
        Assert.Equal(4, prepared.EnvironmentOverrides.Count);
        Assert.Equal("ns-xci.2", prepared.EnvironmentOverrides["NIGHTWEAVER_BEAD_ID"]);
        Assert.Equal(prepared.TemporaryDirectory, prepared.EnvironmentOverrides["TMP"]);
        Assert.Equal(prepared.TemporaryDirectory, prepared.EnvironmentOverrides["TEMP"]);
        Assert.Equal(prepared.TemporaryDirectory, prepared.EnvironmentOverrides["TMPDIR"]);
        Assert.False(Directory.Exists(Path.Combine(host.Root, "tmp")));
        Assert.False(Directory.Exists(Path.Combine(host.Root, "logs")));

        var child = new ProcessStartInfo();
        child.Environment["TMP"] = "ambient-tmp";
        child.Environment["TEMP"] = "ambient-temp";
        child.Environment["TMPDIR"] = "ambient-tmpdir";
        child.Environment["NIGHTWEAVER_SLOT_ID"] = "ambient-slot";
        child.Environment["SYNTHETIC_AUTH_TOKEN"] = "synthetic-secret";
        foreach (var (key, value) in prepared.EnvironmentOverrides)
            child.Environment[key] = value;

        Assert.Equal(prepared.TemporaryDirectory, child.Environment["TMP"]);
        Assert.Equal(prepared.TemporaryDirectory, child.Environment["TEMP"]);
        Assert.Equal(prepared.TemporaryDirectory, child.Environment["TMPDIR"]);
        Assert.Equal("ambient-slot", child.Environment["NIGHTWEAVER_SLOT_ID"]);
        Assert.Equal("synthetic-secret", child.Environment["SYNTHETIC_AUTH_TOKEN"]);
        Assert.Equal(ambientTmp, Environment.GetEnvironmentVariable("TMP"));
        Assert.Equal(ambientTemp, Environment.GetEnvironmentVariable("TEMP"));

        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)prepared.EnvironmentOverrides).Add("EXTRA", "value"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.Arguments)[0] = "changed");
    }

    [Fact]
    public async Task UsesProcessWideMonotonicIdentifiersAcrossInstancesAndBackwardClockChanges()
    {
        using var temp = new TempDirectory();
        var host = CreateHost(temp.Path);
        var project = CreateProject();
        var worktree = CanonicalWorktree(host, project, "ns-xci.2");
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var first = await new AgentInvocationPreparer(clock).PrepareAsync(host, project, AgentRole.Implementer,
            new("ns-xci.2", "task", null, 0), worktree, TestContext.Current.CancellationToken);
        clock.Set(DateTimeOffset.UtcNow.AddDays(-1));
        var second = await new AgentInvocationPreparer(clock).PrepareAsync(host, project, AgentRole.Implementer,
            new("ns-xci.2", "task", null, 0), worktree, TestContext.Current.CancellationToken);
        Assert.True(string.CompareOrdinal(first.InvocationId, second.InvocationId) < 0);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            new AgentInvocationPreparer(new FixedTimeProvider(DateTimeOffset.UtcNow)).PrepareAsync(host, project,
                AgentRole.Implementer, new("ns-xci.2", "task", null, 0), worktree, TestContext.Current.CancellationToken)));
        Assert.Equal(concurrent.Length, concurrent.Select(item => item.InvocationId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(concurrent, item => Assert.Matches("^\\d{8}T\\d{13}Z-implementer$", item.InvocationId));
    }

    [Fact]
    public async Task RejectsUnsupportedRolesUnsafeIdsInvalidStagesAndMismatchedWorktrees()
    {
        using var temp = new TempDirectory();
        var host = CreateHost(temp.Path);
        var project = CreateProject();
        var preparer = new AgentInvocationPreparer();
        var validWorktree = CanonicalWorktree(host, project, "ns-xci.2");

        foreach (var role in new[] { AgentRole.Classifier, (AgentRole)999 })
        {
            var exception = await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
                host, project, role, new("ns-xci.2", "task", null, 0), validWorktree, TestContext.Current.CancellationToken));
            Assert.Contains(role == AgentRole.Classifier ? "not supported" : "Unknown Agent role", exception.Message, StringComparison.Ordinal);
        }

        foreach (var id in new[] { "", ".", "..", "C:bad", "a/b", "a\\b", "bad id", "bad.", "CON", "CON.txt", "Com1", "COM¹", "é" })
        {
            await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
                host, project, AgentRole.Planner, new(id, "task", null, 0), validWorktree, TestContext.Current.CancellationToken));
        }

        foreach (var stage in new[] { "", "plan", "stage:", "stage:plan challenge", "stage:plan\nextra" })
        {
            await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
                host, project, AgentRole.Planner, new("ns-xci.2", "task", stage, 0), validWorktree, TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
            host, project, AgentRole.Planner, new("ns-xci.2", "task\nBead ID: forged", null, 0), validWorktree, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
            host, CreateProject(defaultBranch: "main\nStage: forged"), AgentRole.Planner, new("ns-xci.2", "task", null, 0), validWorktree, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
            host, project, AgentRole.Planner, new("ns-xci.2", "task", null, -1), validWorktree, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
            host, CreateProject(maxRepairCycles: -1), AgentRole.Planner, new("ns-xci.2", "task", null, 0), validWorktree, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => preparer.PrepareAsync(
            host, project, AgentRole.Planner, new("ns-xci.2", "task", null, 0), Path.Combine(host.Root, "other"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingOrInvalidPromptFailsClosedWithRoleAndPathAndCancellationPropagates()
    {
        using var temp = new TempDirectory();
        var host = CreateHost(temp.Path);
        var project = CreateProject();
        var worktree = CanonicalWorktree(host, project, "ns-xci.2");
        var promptPath = Path.Combine(host.Root, "prompts", "planner.md");
        File.Delete(promptPath);
        var exception = await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => new AgentInvocationPreparer().PrepareAsync(
            host, project, AgentRole.Planner, new("ns-xci.2", "feature", null, 0), worktree, TestContext.Current.CancellationToken));
        Assert.Contains("Planner", exception.Message, StringComparison.Ordinal);
        Assert.Contains(promptPath, exception.Message, StringComparison.Ordinal);

        Directory.CreateDirectory(promptPath);
        exception = await Assert.ThrowsAsync<AgentInvocationPreparationException>(() => new AgentInvocationPreparer().PrepareAsync(
            host, project, AgentRole.Planner, new("ns-xci.2", "feature", null, 0), worktree, TestContext.Current.CancellationToken));
        Assert.Contains(promptPath, exception.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentInvocationPreparer().PrepareAsync(
            host, project, AgentRole.Implementer, new("ns-xci.2", "task", null, 0), worktree, cancellation.Token));
    }

    private static HostPaths CreateHost(string root, Func<AgentRole, string>? body = null)
    {
        var hostRoot = Path.Combine(root, "nightweaver host with spaces");
        Directory.CreateDirectory(Path.Combine(hostRoot, "prompts"));
        foreach (var (role, fileName) in PromptFiles)
        {
            File.WriteAllText(Path.Combine(hostRoot, "prompts", fileName), body?.Invoke(role) ?? $"{role} template\n", new UTF8Encoding(false));
        }
        return new HostPaths(hostRoot);
    }

    private static ProjectConfig CreateProject(
        AgentCli cli = AgentCli.Claude,
        string? model = null,
        string effort = "medium",
        int timeoutMinutes = 17,
        int maxRepairCycles = 5,
        string defaultBranch = "main")
    {
        var settings = AgentRoles.All.ToDictionary(role => role, _ => new AgentSettings(cli, model, effort, timeoutMinutes));
        return new ProjectConfig(ProjectSlug.Parse("demo"), "config.json", new("target", defaultBranch),
            new(1, 1, 1), new(maxRepairCycles, 2), new(cli, model, effort, timeoutMinutes), new RoleSettings(settings));
    }

    private static string CanonicalWorktree(HostPaths host, ProjectConfig project, string beadId) =>
        Path.Combine(host.WorktreesRoot(project.Name), beadId);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class MutableTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        private long utcTicks = instant.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref utcTicks, value.UtcTicks);
    }
}
