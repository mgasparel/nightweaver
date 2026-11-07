using System.Reflection;
using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class WorktreeIntegrationTests
{
    [Fact]
    public async Task DryRunRetainsCompletedWorktreeWithNestedJunctionAndProtectsItsTmp()
    {
        using var fixture = new StartupSweepFixture();
        var id = fixture.PlannedBeadId;
        var worktree = fixture.WorktreePath(id);
        WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(worktree, "nested-link"), fixture.External);
        var hostBefore = Snapshot.FileTree(fixture.Host);
        var targetBefore = Snapshot.FileTree(fixture.Target.Path);
        var externalBefore = Snapshot.FileTree(fixture.External);
        var runner = new RecordingProcessRunner((request, token) =>
        {
            var environment = new Dictionary<string, string?>(fixture.Target.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return new SystemProcessRunner().RunAsync(request with { Environment = environment }, token);
        });
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await NightweaverApp.RunAsync(["--project", "demo", "--dry-run"],
            new(fixture.Host, fixture.Caller, output, error), runner, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        var report = output.ToString();
        var retained = Section(report, "Retained worktrees\n", "\nOrphan tmp deletions (hypothetical)");
        var orphanTmp = Section(report, "Orphan tmp deletions (hypothetical)\n", "\nStartup diagnostics");
        Assert.Contains($"{JsonSerializer.Serialize(id)}:", retained, StringComparison.Ordinal);
        Assert.DoesNotContain($"{JsonSerializer.Serialize(id)}:", orphanTmp, StringComparison.Ordinal);
        Assert.Contains("unsafe_runtime_path", report, StringComparison.Ordinal);
        Assert.Contains("tmp_cleanup_deferred", report, StringComparison.Ordinal);
        Assert.Contains("nested reparse point", report, StringComparison.Ordinal);
        Assert.Contains("nested-link", report, StringComparison.Ordinal);
        Assert.Equal(hostBefore, Snapshot.FileTree(fixture.Host));
        Assert.Equal(targetBefore, Snapshot.FileTree(fixture.Target.Path));
        Assert.Equal(externalBefore, Snapshot.FileTree(fixture.External));
    }

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
            if (request.FileName == "bd")
            {
                Assert.Equal(target.Path, request.WorkingDirectory);
                Assert.True(request.Arguments.SequenceEqual(inventoryArgs)
                    || request.Arguments.SequenceEqual(BeadsCommand.CreateUnassignedReadiness(target.Path).Arguments)
                    || request.Arguments.SequenceEqual(BeadsCommand.CreateRecoverableReadiness(target.Path, "nightweaver:demo").Arguments));
            }
            else
            {
                Assert.Equal("git", request.FileName);
                var branchCheck = request.Arguments.SequenceEqual(["check-ref-format", "--branch", "main"]);
                var checkoutIdentityRead = IsCheckoutIdentityRead(request.Arguments);
                if (branchCheck) Assert.Equal(Path.GetTempPath(), request.WorkingDirectory);
                else if (checkoutIdentityRead)
                    Assert.Contains(fixture.StaleIds, id => RuntimePaths.Comparer.Equals(request.WorkingDirectory, fixture.WorktreePath(id)));
                else Assert.Equal(target.Path, request.WorkingDirectory);
                Assert.True(branchCheck
                    || request.Arguments.SequenceEqual(["rev-parse", "--is-inside-work-tree", "--show-prefix", "--path-format=absolute", "--git-dir", "--git-common-dir"])
                    || request.Arguments.SequenceEqual(["worktree", "list", "--porcelain", "-z"])
                    || checkoutIdentityRead);
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
        Assert.Equal(32, runner.Requests.Count);
        var runtime = RuntimeDirectoryReader.Read(new(fixture.Host), new("demo"));
        var registry = GitWorktreeReader.Parse(registryBefore);
        Assert.Contains(registry, entry => entry.Main && entry.Branch == "refs/heads/main");
        Assert.Contains(registry, entry => entry.Locked && entry.LockReason == "human inspection\nkeep this worktree");
        Assert.Contains(registry, entry => entry.Prunable);
        var plan = StartupPlanner.Plan("nightweaver:demo", BeadsReader.Parse(logicalBefore), registry, runtime.Worktrees, runtime.Tmp);
        Assert.Equal(fixture.StaleIds.Order(StringComparer.Ordinal), plan.Removals.Select(entry => entry.BeadId));
        Assert.Equal(fixture.OrphanIds.Order(StringComparer.Ordinal), plan.OrphanTmp.Select(entry => entry.BeadId));
        Assert.All(fixture.ActiveIds, id => Assert.Contains(plan.SurvivingWorktrees, entry => entry.BeadId == id && entry.Reusable));
        Assert.All(fixture.PausedIds, id => Assert.Contains(plan.SurvivingWorktrees, entry => entry.BeadId == id && entry.Paused && entry.ProtectsTmp));
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.MissingBead);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.UnregisteredWorktreeDirectory);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.MissingRegisteredPath);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.LockedWorktree);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.TmpCleanupDeferred);
        foreach (var action in plan.Removals) Assert.Contains($"{JsonSerializer.Serialize(action.BeadId)}: {JsonSerializer.Serialize(action.Path)}; reasons", first, StringComparison.Ordinal);
        foreach (var id in fixture.PausedIds)
        {
            Assert.Contains($"{JsonSerializer.Serialize(id)}:", Section(first, "Retained worktrees\n", "\nOrphan tmp deletions (hypothetical)"), StringComparison.Ordinal);
            Assert.DoesNotContain($"{JsonSerializer.Serialize(id)}:", Section(first, "Orphan tmp deletions (hypothetical)\n", "\nStartup diagnostics"), StringComparison.Ordinal);
        }
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
        Assert.Equal(0, failure);
        Assert.Equal("", failureError.ToString());
        Assert.Contains("unexpected-file", failureOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("unusable_runtime_path", failureOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("tmp_cleanup_deferred", failureOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(failureBefore, Snapshot.FileTree(fixture.Host));
        Assert.Equal(targetBefore, Snapshot.FileTree(target.Path));
        Assert.Equal(externalBefore, Snapshot.FileTree(fixture.External));
        Assert.Equal(logicalBefore, target.Run("bd", inventoryArgs));
    }

    private static string Section(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start < 0 ? -1 : value.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= 0, $"Could not find report section '{startMarker}'.");
        return value[start..end];
    }

    private static bool IsCheckoutIdentityRead(IReadOnlyList<string> arguments) =>
        arguments.SequenceEqual(["rev-parse", "--show-toplevel"])
        || arguments.SequenceEqual(["rev-parse", "--path-format=absolute", "--git-dir"])
        || arguments.SequenceEqual(["rev-parse", "--path-format=absolute", "--git-common-dir"])
        || arguments.SequenceEqual(["rev-parse", "--abbrev-ref", "HEAD"])
        || arguments.SequenceEqual(["rev-parse", "HEAD"]);
}

// Reusable real-tool fixture for downstream dry-run/readiness integration gates.
// All setup mutations occur before callers take their measurement snapshots.
internal sealed class StartupSweepFixture : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly Dictionary<string, string> seededIds;
    internal BeadsFixture Target { get; } = new("target repo with spaces");
    internal string Host { get; }
    internal string Caller { get; }
    internal string Config { get; }
    internal string External { get; }
    internal IReadOnlyList<string> StaleIds { get; }
    internal string PlannedBeadId => seededIds["planned"];
    internal IReadOnlyList<string> ActiveIds { get; }
    internal IReadOnlyList<string> PausedIds { get; }
    internal IReadOnlyList<string> OrphanIds { get; }

    internal StartupSweepFixture(string? publishedArtifactDirectory = null)
    {
        Host = temp.CreateDirectory("nightweaver host");
        Caller = temp.CreateDirectory("unrelated caller");
        External = temp.CreateDirectory("external");
        temp.WriteFile("external/sentinel", "never inspect or modify through a link");
        var appDirectory = publishedArtifactDirectory ?? Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "NightweaverAppDir").Value!;
        foreach (var directory in Directory.EnumerateDirectories(appDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(Host, Path.GetRelativePath(appDirectory, directory)));
        foreach (var file in Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(Host, Path.GetRelativePath(appDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(destination, File.GetUnixFileMode(file)); }
                catch (PlatformNotSupportedException) { }
            }
        }
        Config = temp.WriteFile("nightweaver host/projects/demo.json", JsonSerializer.Serialize(new { name = "demo", target_repository = new { path = Target.Path } }));
        temp.WriteFile("nightweaver host/logs/demo/retained.log", "preserve logs");
        Target.Run("git", ["commit", "--allow-empty", "-m", "fixture base"]);
        // These rows describe a scenario, not create/update command contracts.
        // Import them in one transaction before constructing real Git worktrees.
        var seeds = new[]
        {
            new { title = "closed", status = "closed", labels = Array.Empty<string>() },
            // Completed Planning has passed verification and guarded claim handoff.
            // A still-claimed planned Product instead requires explicit recovery.
            new { title = "planned", status = "open", labels = new[] { "planned" } },
            new { title = "paused", status = "in_progress", labels = new[] { "product-decision-required" } },
            new { title = "needs human", status = "in_progress", labels = new[] { "needs-human" } },
            new { title = "active", status = "in_progress", labels = Array.Empty<string>() },
            new { title = "merge", status = "open", labels = new[] { "stage:merge" } },
            new { title = "locked closed", status = "closed", labels = Array.Empty<string>() },
            new { title = "missing path", status = "closed", labels = Array.Empty<string>() },
            new { title = "orphan tmp", status = "closed", labels = Array.Empty<string>() }
        };
        var seedFile = Path.Combine(temp.Path, "startup-seed.jsonl");
        File.WriteAllText(seedFile, string.Join('\n', seeds.Select(seed => JsonSerializer.Serialize(new
        {
            seed.title, seed.status, seed.labels,
            issue_type = seed.title == "planned" ? "feature" : "task",
            priority = 2,
            assignee = seed.status == "closed" || seed.title == "planned" ? null : "nightweaver:demo"
        }))));
        Target.Run("bd", ["import", seedFile, "--json"]);
        seededIds = BeadsReader.Parse(Target.Run("bd", BeadsCommand.Create(Target.Path).Arguments)).ById.Values
            .ToDictionary(bead => bead.Payload.GetProperty("title").GetString()!, bead => bead.Id, StringComparer.Ordinal);
        File.Delete(seedFile);
        var closed = Worktree("closed", named: true);
        var planned = Worktree("planned");
        var paused = Worktree("paused");
        var needsHuman = Worktree("needs human");
        var active = Worktree("active");
        var merge = Worktree("merge", named: true);
        Target.Run("bd", ["dep", "add", active, merge]);
        var locked = Worktree("locked closed");
        Target.Run("git", ["worktree", "lock", "--reason", "human inspection\nkeep this worktree", WorktreePath(locked)]);
        var missing = Worktree("missing path");
        Directory.Delete(WorktreePath(missing), recursive: true);
        Target.Run("git", ["worktree", "add", "--detach", WorktreePath("fixture.absent"), "HEAD"]);
        temp.CreateDirectory("nightweaver host/worktrees/demo/unregistered");
        Target.Run("git", ["worktree", "add", "--detach", Path.Combine(Host, "worktrees", "elsewhere", closed), "HEAD"]);
        Target.Run("git", ["worktree", "add", "-b", "nightweaver/external", Path.Combine(External, "foreign checkout"), "HEAD"]);
        WorktreeInspectionTests.CreateDirectoryLink(WorktreePath("linked-worktree"), External);
        temp.CreateDirectory("nightweaver host/tmp/demo");
        WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(Host, "tmp", "demo", "linked-tmp"), External);
        StaleIds = Array.AsReadOnly(new[] { closed, planned });
        ActiveIds = Array.AsReadOnly(new[] { active, merge });
        PausedIds = Array.AsReadOnly(new[] { paused, needsHuman });
        OrphanIds = Array.AsReadOnly(new[] { closed, planned, seededIds["orphan tmp"] });
        foreach (var id in new[] { closed, planned, paused, needsHuman, active, merge, locked, missing, seededIds["orphan tmp"], "fixture.absent", "unregistered", "linked-worktree" })
            temp.WriteFile($"nightweaver host/tmp/demo/{id}/invocation/output", "preserve invocation data");
    }

    internal string WorktreePath(string id) => Path.Combine(Host, "worktrees", "demo", id);
    private string Worktree(string title, bool named = false)
    {
        var id = seededIds[title];
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
