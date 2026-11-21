using System.Security.Cryptography;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Upgrade;

namespace Nightweaver.Tests;

public sealed class UpgradeReplacementTests
{
    [Fact]
    public async Task ReplacesOnlyInventoryAndRetainsCompleteOldPayload()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        var old = Snapshot(fixture.Root);
        var state = fixture.Sentinels();
        var result = await fixture.Replacement().ReplaceAsync(3, fixture.Candidate, fixture.Token);
        Assert.Equal(UpgradePhase.Installed, result.Phase);
        Assert.Null(result.PendingOperation);
        Assert.Equal(fixture.NextDigest, PreparedRelease.Inspect(fixture.Root, true).Digest);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "retired.txt")));
        Assert.Equal("operator", File.ReadAllText(Path.Combine(fixture.Root, "extra.txt")));
        AssertSnapshots(state, fixture.Sentinels());
        var retained = Snapshot(fixture.Journal.RetainedPackageRoot);
        foreach (var file in fixture.Old.Inventory.Files.Select(f => f.Path).Append(PreparedRelease.InventoryName))
            Assert.Equal(old[file], retained[file]);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(new(fixture.Root)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Replacement().ReplaceAsync(result.Revision, fixture.Candidate, fixture.Token));
    }

    public static TheoryData<string> Boundaries => new(Enum.GetNames<UpgradeReplacementBoundary>());

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task InterruptedStepRetainsSuppressionAndTruthfulProgress(string boundaryName)
    {
        var stop = Enum.Parse<UpgradeReplacementBoundary>(boundaryName);
        using var fixture = new Fixture();
        await fixture.Prepare();
        var state = fixture.Sentinels();
        var old = Snapshot(fixture.Root);
        var fired = false;
        var replacement = fixture.Replacement((point, _) =>
        {
            if (point == stop && !fired) { fired = true; throw new IOException("injected interruption"); }
        });
        await Assert.ThrowsAsync<IOException>(() => replacement.ReplaceAsync(3, fixture.Candidate, fixture.Token));
        Assert.True(fired);
        var progress = fixture.Journal.Read().Progress!;
        Assert.Equal(UpgradePhase.Replacing, progress.Phase);
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(new(fixture.Root)));
        AssertSnapshots(state, fixture.Sentinels());
        if (stop is UpgradeReplacementBoundary.BeforeBackup or UpgradeReplacementBoundary.AfterBackup)
        {
            foreach (var file in fixture.Old.Inventory.Files) Assert.Equal(old[file.Path], File.ReadAllBytes(Path.Combine(fixture.Root, file.Path)));
            Assert.NotNull(progress.PendingOperation);
        }
        else
        {
            Assert.Equal(fixture.Old.Digest, PreparedRelease.Inspect(fixture.Journal.RetainedPackageRoot, false).Digest);
            if (stop != UpgradeReplacementBoundary.AfterConfirm) Assert.NotNull(progress.PendingOperation);
            Assert.Contains(progress.Files, f => stop == UpgradeReplacementBoundary.AfterConfirm
                ? f.Result == UpgradeFileResult.New : f.Result == UpgradeFileResult.Pending);
        }
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("changed-old")]
    [InlineData("changed-source")]
    [InlineData("collision")]
    [InlineData("backup")]
    [InlineData("alias")]
    [InlineData("manager")]
    [InlineData("authority")]
    public async Task RefusalPreservesStateAndStopsFurtherMutation(string fault)
    {
        using var fixture = new Fixture();
        await fixture.Prepare(fault == "manager");
        var state = fixture.Sentinels();
        if (fault == "changed-old") File.WriteAllText(Path.Combine(fixture.Root, "retired.txt"), "modified");
        if (fault == "changed-source") File.WriteAllText(Path.Combine(fixture.Candidate, "added.txt"), "modified");
        if (fault == "collision") File.WriteAllText(Path.Combine(fixture.Root, "added.txt"), "operator");
        if (fault == "backup") Directory.CreateDirectory(fixture.Journal.RetainedPackageRoot);

        using var locked = fault == "locked" ? new FileStream(Path.Combine(fixture.Root, "retired.txt"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var callback = false;
        var replacement = fixture.Replacement((point, name) =>
        {
            if (point != UpgradeReplacementBoundary.BeforeWrite || callback) return;
            callback = true;
            if (fault == "authority") File.WriteAllText(fixture.Config, "changed configuration");
            if (fault == "alias")
            {
                // The file and every parent must remain pinned during a write.
                Assert.Throws<IOException>(() => Directory.Move(fixture.Candidate, fixture.Candidate + "-moved"));
                Assert.Throws<IOException>(() => File.Move(Path.Combine(fixture.Candidate, name), Path.Combine(fixture.Candidate, name + ".moved")));
                throw new IOException("injected path replacement attempt");
            }
        });
        if (fault == "authority")
        {
            // Mutation callbacks hold the config against edits. A later edit must refuse the next boundary.
            replacement = fixture.Replacement((point, _) =>
            {
                if (point == UpgradeReplacementBoundary.AfterConfirm && !callback)
                { callback = true; File.WriteAllText(fixture.Config, "changed configuration"); }
            });
        }
        await Assert.ThrowsAnyAsync<Exception>(() => replacement.ReplaceAsync(3, fixture.Candidate, fixture.Token));
        Assert.NotEqual(UpgradePhase.Installed, fixture.Journal.Read().Progress!.Phase);
        AssertSnapshots(state, fixture.Sentinels());
        if (fault is not ("alias" or "authority")) Assert.False(File.Exists(Path.Combine(fixture.Root, "added.txt")) && fault != "collision");
        Assert.Throws<InvalidOperationException>(() => UpgradeInterlock.RequireStartupAllowed(new(fixture.Root)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalGitRegistrationSurvivesReplacementAndInterruption(bool interrupt)
    {
        using var git = new GitFixture();
        git.CommitEmpty();
        using var fixture = new Fixture();
        var checkout = Path.Combine(fixture.State, "demo");
        GitFixture.RunGit(git.Path, ["worktree", "add", "--detach", checkout, "HEAD"]);
        File.WriteAllText(Path.Combine(checkout, "untracked.txt"), "operator work");
        var before = await ProcessTool.RunAsync("git", ["worktree", "list", "--porcelain"], git.Path, null, TimeSpan.FromSeconds(10));
        var files = Snapshot(checkout);
        await fixture.Prepare();
        var replacement = fixture.Replacement((point, _) =>
        {
            if (interrupt && point == UpgradeReplacementBoundary.AfterWrite) throw new IOException("injected interruption");
        });
        if (interrupt) await Assert.ThrowsAsync<IOException>(() => replacement.ReplaceAsync(3, fixture.Candidate, fixture.Token));
        else await replacement.ReplaceAsync(3, fixture.Candidate, fixture.Token);
        var after = await ProcessTool.RunAsync("git", ["worktree", "list", "--porcelain"], git.Path, null, TimeSpan.FromSeconds(10));
        Assert.Equal(0, before.ExitCode);
        Assert.Equal(0, after.ExitCode);
        Assert.Equal(before.StandardOutputText, after.StandardOutputText);
        AssertSnapshots(files, Snapshot(checkout));
    }

    [Fact]
    public async Task SharingDenialAfterBackupLeavesPendingFileAndCompleteRollback()
    {
        using var fixture = new Fixture();
        await fixture.Prepare();
        FileStream? denied = null;
        try
        {
            var replacement = fixture.Replacement((point, name) =>
            {
                if (point == UpgradeReplacementBoundary.BeforeWrite && name == "nightweaver.exe")
                    denied = new FileStream(Path.Combine(fixture.Root, name), FileMode.Open, FileAccess.Read, FileShare.Read);
            });
            await Assert.ThrowsAsync<IOException>(() => replacement.ReplaceAsync(3, fixture.Candidate, fixture.Token));
            var progress = fixture.Journal.Read().Progress!;
            Assert.Equal(UpgradePhase.Replacing, progress.Phase);
            Assert.Contains(progress.Files, f => f.Path == "nightweaver.exe" && f.Result == UpgradeFileResult.Pending);
            Assert.Equal(fixture.Old.Digest, PreparedRelease.Inspect(fixture.Journal.RetainedPackageRoot, false).Digest);
            Assert.True(File.Exists(Path.Combine(fixture.Root, "retired.txt")));
        }
        finally { denied?.Dispose(); }
    }
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), File.ReadAllBytes);
    private static void AssertSnapshots(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, bytes) in expected) Assert.Equal(bytes, actual[name]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal string Root { get; }
        internal string Candidate { get; }
        internal string State { get; }
        internal string Config { get; }
        internal UpgradeJournal Journal { get; }
        internal UpgradeLifecycle Lifecycle { get; }
        internal ReleaseInspection Old { get; }
        internal string NextDigest { get; }
        internal CancellationToken Token { get; } = TestContext.Current.CancellationToken;
        internal Fixture()
        {
            Root = temp.CreateDirectory("installation");
            Candidate = temp.CreateDirectory("candidate");
            Package(Root, false);
            Package(Candidate, true);
            Old = PreparedRelease.Inspect(Root, true);
            NextDigest = PreparedRelease.Inspect(Candidate, false).Digest;
            State = Path.Combine(Root, "worktrees");
            foreach (var directory in PreparedRelease.StateDirectories)
                Put(Root, directory + "/sentinel.txt", "preserve " + directory);
            Put(Root, "run/demo/cleanup/sentinel.txt", "old-root cleanup evidence");
            Put(Root, "run/demo/background/sentinel.txt", "historical owner identity");
            Put(Root, "run/demo/supervision/sentinel.txt", "retained history");
            Put(Root, "extra.txt", "operator");
            Config = temp.WriteFile("demo.json", "configuration");
            var common = temp.CreateDirectory("repo");
            var scope = new UpgradeScope(Root, "S-1-5-21-1001", [new("demo", Config, Digest(File.ReadAllBytes(Config)), common, common, "physical")]);
            Journal = new(scope, new('a', 32));
            Lifecycle = new(new(Root), Journal, new(_ => Task.FromResult(new UpgradeLifecycleInventory(scope, [], true)),
                (_, _) => Task.FromResult<Nightweaver.Supervision.SupervisionLifecycle?>(null),
                (_, _, _, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), _ => false));
        }
        internal Dictionary<string, byte[]> Sentinels() => Directory.GetFiles(Root, "sentinel.txt", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(Root, p), File.ReadAllBytes);
        internal async Task Prepare(bool badManager = false)
        {
            await Journal.SaveAsync(null, new(1, new('a', 32), 0, Scope(), Old.Digest, NextDigest,
                badManager ? new('0', 64) : Digest(File.ReadAllBytes(typeof(UpgradeReplacement).Assembly.Location)), UpgradePhase.Prepared, null, null,
                new('b', 32), [], [], [], []), Token);
            await Lifecycle.BeginDrainAsync(1, Token);
            await Lifecycle.ConfirmSettlementAsync(2, Token);
        }
        private UpgradeScope Scope()
        {
            var common = Path.Combine(temp.Path, "repo");
            return new(Root, "S-1-5-21-1001", [new("demo", Config, Digest(File.ReadAllBytes(Config)), common, common, "physical")]);
        }
        internal UpgradeReplacement Replacement(Action<UpgradeReplacementBoundary, string>? callback = null) => new(Journal, Lifecycle, callback);
        public void Dispose() => temp.Dispose();
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static void Package(string root, bool next)
    {
        var build = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(PreparedRelease).Assembly.Location).ProductVersion!.Split('+').Last();
        Put(root, "nightweaver.exe", next ? "new cli" : "old cli");
        Put(root, "web/Nightweaver.Web.exe", "web");
        File.Copy(typeof(PreparedRelease).Assembly.Location, Path.Combine(root, "nightweaver.dll"));
        File.Copy(typeof(PreparedRelease).Assembly.Location, Path.Combine(root, "web/nightweaver.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Nightweaver.Web.dll"), Path.Combine(root, "web/Nightweaver.Web.dll"));
        Put(root, "nightweaver.runtimeconfig.json", "{}");
        Put(root, "web/Nightweaver.Web.runtimeconfig.json", "{}");
        Put(root, "nightweaver-deployment.json", JsonSerializer.Serialize(new { schema_version = 1, build_version = build,
            cli_executable = "nightweaver.exe", web_executable = "web/Nightweaver.Web.exe" }));
        Put(root, "web/wwwroot/studio/index.html", "<script src=\"app.js\"></script>");
        Put(root, "web/wwwroot/studio/app.js", next ? "new bundle" : "old bundle");
        Put(root, next ? "added.txt" : "retired.txt", "payload");
        foreach (var role in new[] { "implementer", "merger", "plan-challenger", "planner", "qa", "reviewer" }) Put(root, "prompts/" + role + ".md", role);
        var inventory = new ReleaseInventory(1, build, [], Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => new ReleaseFile(Path.GetRelativePath(root, p).Replace('\\', '/'), new FileInfo(p).Length, Digest(File.ReadAllBytes(p)))).ToArray());
        Put(root, PreparedRelease.InventoryName, JsonSerializer.Serialize(inventory, PreparedRelease.JsonOptions));
    }
    private static void Put(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
