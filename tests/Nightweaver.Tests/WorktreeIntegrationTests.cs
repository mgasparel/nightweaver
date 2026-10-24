using System.Reflection;
using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class WorktreeIntegrationTests
{
    [Fact]
    public async Task RealGitAndBeadsSweepIsDeterministicAndPreservesDiskAndTracker()
    {
        using var fixture = new StartupSweepFixture();
        var target = fixture.Target;
        var inventoryArgs = BeadsCommand.Create(target.Path).Arguments;
        var logicalBefore = target.Run("bd", inventoryArgs);
        var registryBefore = target.Run("git", ["worktree", "list", "--porcelain", "-z"]);
        var targetBefore = Snapshot.FileTree(target.Path);
        var hostBefore = Snapshot.FileTree(fixture.Host);
        var externalBefore = Snapshot.FileTree(fixture.External);
        var runner = new RecordingProcessRunner((request, token) =>
        {
            Assert.Equal(request.Arguments[0] == "check-ref-format" ? Path.GetTempPath() : target.Path, request.WorkingDirectory);
            if (request.FileName == "bd") Assert.Equal(inventoryArgs, request.Arguments);
            else
            {
                Assert.Equal("git", request.FileName);
                Assert.True(request.Arguments.SequenceEqual(["check-ref-format", "--branch", "main"])
                    || request.Arguments.SequenceEqual(["rev-parse", "--is-inside-work-tree", "--show-prefix", "--path-format=absolute", "--git-dir", "--git-common-dir"])
                    || request.Arguments.SequenceEqual(["worktree", "list", "--porcelain", "-z"]));
            }
            var environment = new Dictionary<string, string?>(target.Environment);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return new SystemProcessRunner().RunAsync(request with { Environment = environment }, token);
        });
        async Task<string> DryRun(string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await NightweaverApp.RunAsync(args, new(fixture.Host, fixture.Caller, output, error), runner, CancellationToken.None);
            Assert.Equal("", error.ToString());
            Assert.Equal(0, code);
            return output.ToString();
        }
        var first = await DryRun(["--project", "demo", "--dry-run"]);
        var second = await DryRun(["--config", Path.GetRelativePath(fixture.Caller, fixture.Config), "--dry-run"]);
        Assert.Equal(first, second);
        Assert.Equal(8, runner.Requests.Count);
        var runtime = RuntimeDirectoryReader.Read(new(fixture.Host), new("demo"));
        var registry = GitWorktreeReader.Parse(registryBefore);
        Assert.Contains(registry, entry => entry.Main && entry.Branch == "refs/heads/main");
        Assert.Contains(registry, entry => entry.Locked && entry.LockReason == "human inspection\nkeep this worktree");
        Assert.Contains(registry, entry => entry.Prunable);
        var plan = StartupPlanner.Plan("nightweaver:demo", BeadsReader.Parse(logicalBefore), registry, runtime.Worktrees, runtime.Tmp);
        Assert.Equal(fixture.StaleIds.Order(StringComparer.Ordinal), plan.Removals.Select(entry => entry.BeadId));
        Assert.Equal(fixture.OrphanIds.Order(StringComparer.Ordinal), plan.OrphanTmp.Select(entry => entry.BeadId));
        Assert.All(fixture.ActiveIds, id => Assert.Contains(plan.SurvivingWorktrees, entry => entry.BeadId == id && entry.Reusable));
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.MissingBead);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.UnregisteredWorktreeDirectory);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.MissingRegisteredPath);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.LockedWorktree);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.TmpCleanupDeferred);
        foreach (var action in plan.Removals) Assert.Contains($"{JsonSerializer.Serialize(action.BeadId)}: {JsonSerializer.Serialize(action.Path)}; reasons", first, StringComparison.Ordinal);
        Assert.Contains("detached HEAD", first, StringComparison.Ordinal);
        Assert.Contains("branch \"refs/heads/nightweaver/", first, StringComparison.Ordinal);
        Assert.Contains("human inspection\\nkeep this worktree", first, StringComparison.Ordinal);
        Assert.DoesNotContain("human inspection\nkeep this worktree", first, StringComparison.Ordinal);

        // Actually launch the built executable, rooted at the copied apphost,
        // from an unrelated cwd against the same real Git + bd fixture.
        var executable = Path.Combine(fixture.Host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
        var result = await new SystemProcessRunner().RunAsync(new(executable, ["--project", "demo", "--dry-run"], fixture.Caller,
            target.Environment, TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal("", result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(first, result.StandardOutput);
        Assert.Equal(logicalBefore, target.Run("bd", inventoryArgs));
        Assert.Equal(registryBefore, target.Run("git", ["worktree", "list", "--porcelain", "-z"]));
        Assert.Equal(targetBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(hostBefore, Snapshot.FileTree(fixture.Host));
        Assert.Equal(externalBefore, Snapshot.FileTree(fixture.External));

        // Late filesystem failure follows successful bd and Git observations.
        File.WriteAllText(Path.Combine(fixture.Host, "tmp", "demo", "unexpected-file"), "not a directory");
        var failureBefore = Snapshot.FileTree(fixture.Host);
        var failureOut = new StringWriter();
        var failureError = new StringWriter();
        var failure = await NightweaverApp.RunAsync(["--project", "demo", "--dry-run"], new(fixture.Host, fixture.Caller, failureOut, failureError), runner, CancellationToken.None);
        Assert.Equal(1, failure);
        Assert.Equal("", failureOut.ToString());
        Assert.Contains("runtime directory inspection", failureError.ToString(), StringComparison.Ordinal);
        Assert.Equal(failureBefore, Snapshot.FileTree(fixture.Host));
        Assert.Equal(targetBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(externalBefore, Snapshot.FileTree(fixture.External));
        Assert.Equal(logicalBefore, target.Run("bd", inventoryArgs));
    }
}

// Reusable real-tool fixture for downstream dry-run/readiness integration gates.
// All setup mutations occur before callers take their measurement snapshots.
internal sealed class StartupSweepFixture : IDisposable
{
    private readonly TempDirectory temp = new();
    internal BeadsFixture Target { get; } = new("target repo with spaces");
    internal string Host { get; }
    internal string Caller { get; }
    internal string Config { get; }
    internal string External { get; }
    internal IReadOnlyList<string> StaleIds { get; }
    internal IReadOnlyList<string> ActiveIds { get; }
    internal IReadOnlyList<string> OrphanIds { get; }

    internal StartupSweepFixture()
    {
        Host = temp.CreateDirectory("nightweaver host");
        Caller = temp.CreateDirectory("unrelated caller");
        External = temp.CreateDirectory("external");
        temp.WriteFile("external/sentinel", "never inspect or modify through a link");
        var appDirectory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "NightweaverAppDir").Value!;
        foreach (var file in Directory.EnumerateFiles(appDirectory)) File.Copy(file, Path.Combine(Host, Path.GetFileName(file)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(Host, "nightweaver"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Config = temp.WriteFile("nightweaver host/projects/demo.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = Target.Path } }));
        temp.WriteFile("nightweaver host/logs/demo/retained.log", "preserve logs");
        Target.Run("git", ["commit", "--allow-empty", "-m", "fixture base"]);
        var closed = Worktree("closed", "closed");
        var planned = Worktree("planned", label: "planned", named: true);
        var paused = Worktree("paused", label: "product-decision-required");
        var needsHuman = Worktree("needs human", label: "needs-human");
        var active = Worktree("active", "in_progress");
        var merge = Worktree("merge", label: "stage:merge", named: true);
        Target.Run("bd", ["dep", "add", active, merge]);
        var locked = Worktree("locked closed", "closed");
        Target.Run("git", ["worktree", "lock", "--reason", "human inspection\nkeep this worktree", WorktreePath(locked)]);
        var missing = Worktree("missing path", "closed");
        Directory.Delete(WorktreePath(missing), recursive: true);
        Target.Run("git", ["worktree", "add", "--detach", WorktreePath("fixture.absent"), "HEAD"]);
        temp.CreateDirectory("nightweaver host/worktrees/demo/unregistered");
        Target.Run("git", ["worktree", "add", "--detach", Path.Combine(Host, "worktrees", "elsewhere", closed), "HEAD"]);
        Target.Run("git", ["worktree", "add", "-b", "nightweaver/external", Path.Combine(External, "foreign checkout"), "HEAD"]);
        WorktreeInspectionTests.CreateDirectoryLink(WorktreePath("linked-worktree"), External);
        temp.CreateDirectory("nightweaver host/tmp/demo");
        WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(Host, "tmp", "demo", "linked-tmp"), External);
        StaleIds = Array.AsReadOnly(new[] { closed, planned, paused, needsHuman });
        ActiveIds = Array.AsReadOnly(new[] { active, merge });
        OrphanIds = Array.AsReadOnly(new[] { closed, planned, paused, needsHuman, missing, "orphan" });
        foreach (var id in new[] { closed, planned, paused, needsHuman, active, merge, locked, missing, "fixture.absent", "unregistered", "linked-worktree", "orphan" })
            temp.WriteFile($"nightweaver host/tmp/demo/{id}/invocation/output", "preserve invocation data");
    }

    private string WorktreePath(string id) => Path.Combine(Host, "worktrees", "demo", id);
    private string Worktree(string title, string status = "open", string? label = null, bool named = false)
    {
        var id = Target.Create(title, "nightweaver:demo", status);
        if (label is not null) Target.Run("bd", ["update", id, "--add-label", label]);
        if (named) Target.Run("git", ["worktree", "add", "-b", "nightweaver/" + id, WorktreePath(id), "HEAD"]);
        else Target.Run("git", ["worktree", "add", "--detach", WorktreePath(id), "HEAD"]);
        return id;
    }

    public void Dispose()
    {
        temp.Dispose();
        Target.Dispose();
    }
}
