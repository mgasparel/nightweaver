using System.Diagnostics;
using System.Security.Cryptography;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WorktreeProvisionerIntegrationTests
{
    [Fact]
    public async Task PlanningFetchesDetachedDefaultAndWarmReuseAdvancesToFetchedCommit()
    {
        using var fixture = new ProvisioningGitFixture();
        var before = fixture.TargetCheckout();
        var provisioner = fixture.Provisioner();
        var first = await provisioner.ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository, fixture.BeadId,
            WorktreePurpose.Planning, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.InitialSha, first.HeadSha);
        Assert.Equal("", fixture.Git(first.Path, ["status", "--porcelain=v1", "--untracked-files=all"]).Output);
        Assert.Equal(1, fixture.Git(first.Path, ["symbolic-ref", "--quiet", "HEAD"], allowFailure: true).ExitCode);

        fixture.AdvanceSeedMain();
        File.WriteAllText(Path.Combine(first.Path, "README.md"), "dirty planning tracked edit");
        File.WriteAllText(Path.Combine(first.Path, "planning-untracked.txt"), "remove me");
        Directory.CreateDirectory(Path.Combine(first.Path, "ignored-cache"));
        File.WriteAllText(Path.Combine(first.Path, "ignored-cache", "cache.bin"), "keep me");
        var second = await provisioner.ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository, fixture.BeadId,
            WorktreePurpose.Planning, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.AdvancedSha, second.HeadSha);
        Assert.False(File.Exists(Path.Combine(first.Path, "planning-untracked.txt")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(first.Path, "ignored-cache", "cache.bin")));
        Assert.Equal(before, fixture.TargetCheckout());
    }

    [Fact]
    public async Task FreshDeliveryBranchesFromDefaultWithoutAccidentalUpstream()
    {
        using var fixture = new ProvisioningGitFixture();
        var before = fixture.TargetCheckout();
        var result = await fixture.Provisioner().ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository,
            fixture.BeadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.InitialSha, result.HeadSha);
        Assert.Equal($"refs/heads/nightweaver/{fixture.BeadId}", fixture.Git(result.Path, ["symbolic-ref", "--quiet", "HEAD"]).Output.Trim());
        Assert.NotEqual(0, fixture.Git(result.Path, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], allowFailure: true).ExitCode);
        Assert.Equal(before, fixture.TargetCheckout());
    }

    [Fact]
    public async Task FreshDeliveryTracksExistingRemoteBeadBranch()
    {
        using var fixture = new ProvisioningGitFixture();
        var remoteSha = fixture.CreateAndPushRemoteBeadBranch();
        var before = fixture.TargetCheckout();
        var result = await fixture.Provisioner().ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository,
            fixture.BeadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        Assert.Equal(remoteSha, result.HeadSha);
        Assert.Equal($"origin/nightweaver/{fixture.BeadId}", fixture.Git(result.Path,
            ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"]).Output.Trim());
        Assert.Equal(before, fixture.TargetCheckout());
    }

    [Fact]
    public async Task WarmDeliveryCleansDirtyFilesPreservesIgnoredCacheAndKeepsLocalHead()
    {
        using var fixture = new ProvisioningGitFixture();
        var provisioner = fixture.Provisioner();
        var first = await provisioner.ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository,
            fixture.BeadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(first.Path, "delivery-commit.txt"), "committed local progress");
        fixture.Git(first.Path, ["add", "delivery-commit.txt"]);
        fixture.Git(first.Path, ["-c", "user.name=Nightweaver Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "local delivery progress"]);
        var committedHead = fixture.Git(first.Path, ["rev-parse", "HEAD"]).Output.Trim();
        fixture.AdvanceSeedMain();

        File.AppendAllText(Path.Combine(first.Path, "README.md"), "unstaged dirt");
        fixture.Git(first.Path, ["add", "README.md"]);
        File.WriteAllText(Path.Combine(first.Path, "README.md"), "staged and unstaged dirt");
        File.WriteAllText(Path.Combine(first.Path, "untracked.txt"), "remove");
        Directory.CreateDirectory(Path.Combine(first.Path, "untracked-directory"));
        File.WriteAllText(Path.Combine(first.Path, "untracked-directory", "nested.txt"), "remove");
        Directory.CreateDirectory(Path.Combine(first.Path, "ignored-cache"));
        File.WriteAllText(Path.Combine(first.Path, "ignored-cache", "cache.bin"), "keep");
        var rootBefore = fixture.TargetCheckout();

        var second = await provisioner.ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository,
            fixture.BeadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        Assert.Equal(committedHead, second.HeadSha);
        Assert.Equal("committed local progress", File.ReadAllText(Path.Combine(second.Path, "delivery-commit.txt")));
        Assert.False(File.Exists(Path.Combine(second.Path, "untracked.txt")));
        Assert.False(Directory.Exists(Path.Combine(second.Path, "untracked-directory")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(second.Path, "ignored-cache", "cache.bin")));
        Assert.Equal("", fixture.Git(second.Path, ["status", "--porcelain=v1", "--untracked-files=all"]).Output);

        var repeated = await provisioner.ProvisionAsync(fixture.Paths, fixture.Project, fixture.Repository,
            fixture.BeadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        Assert.Equal(committedHead, repeated.HeadSha);
        Assert.Equal(rootBefore, fixture.TargetCheckout());
    }

    [Fact]
    public async Task MissingDefaultFetchCannotReturnResultOrChangeRootCheckout()
    {
        using var fixture = new ProvisioningGitFixture();
        var before = fixture.TargetCheckout();
        var repository = new ValidatedTargetRepository(fixture.Target, "missing-default");
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provisioner().ProvisionAsync(
            fixture.Paths, fixture.Project, repository, fixture.BeadId, WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.Contains("fetch origin", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, fixture.TargetCheckout());
        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreesRoot, fixture.BeadId)));
    }

    [Fact]
    public async Task ConflictingRegisteredBranchFailsWithoutChangingTargetCheckout()
    {
        using var fixture = new ProvisioningGitFixture();
        var foreignPath = Path.Combine(fixture.TempRoot, "foreign branch checkout");
        fixture.Git(fixture.Target, ["worktree", "add", "-b", $"nightweaver/{fixture.BeadId}", foreignPath, "HEAD"]);
        var before = fixture.TargetCheckout();
        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provisioner().ProvisionAsync(
            fixture.Paths, fixture.Project, fixture.Repository, fixture.BeadId, WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.Contains("registered outside the canonical destination", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, fixture.TargetCheckout());
        Assert.Equal("", fixture.Git(foreignPath, ["status", "--porcelain=v1", "--untracked-files=all"]).Output);
    }

    [Theory]
    [InlineData("wrong-branch")]
    [InlineData("detached")]
    [InlineData("occupied")]
    [InlineData("orphan-local-branch")]
    [InlineData("locked")]
    [InlineData("missing-registration-path")]
    [InlineData("nested-link")]
    public async Task UnsafeOrConflictingDeliveryStateFailsWithoutChangingTargetCheckout(string scenario)
    {
        using var fixture = new ProvisioningGitFixture();
        var destination = Path.Combine(fixture.WorktreesRoot, fixture.BeadId);
        string? externalSentinel = null;
        switch (scenario)
        {
            case "wrong-branch":
                fixture.CreateDestinationParent();
                fixture.Git(fixture.Target, ["worktree", "add", "-b", "somebody-elses-branch", destination, "HEAD"]);
                break;
            case "detached":
                fixture.CreateDestinationParent();
                fixture.Git(fixture.Target, ["worktree", "add", "--detach", destination, "HEAD"]);
                break;
            case "occupied":
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "sentinel"), "preserve occupied destination");
                break;
            case "orphan-local-branch":
                fixture.Git(fixture.Target, ["branch", $"nightweaver/{fixture.BeadId}", "HEAD"]);
                break;
            case "locked":
                fixture.CreateDestinationParent();
                fixture.Git(fixture.Target, ["worktree", "add", "-b", $"nightweaver/{fixture.BeadId}", destination, "HEAD"]);
                fixture.Git(fixture.Target, ["worktree", "lock", "--reason", "fixture lock", destination]);
                break;
            case "missing-registration-path":
                fixture.CreateDestinationParent();
                fixture.Git(fixture.Target, ["worktree", "add", "-b", $"nightweaver/{fixture.BeadId}", destination, "HEAD"]);
                Directory.Delete(destination, recursive: true);
                break;
            case "nested-link":
                fixture.CreateDestinationParent();
                fixture.Git(fixture.Target, ["worktree", "add", "-b", $"nightweaver/{fixture.BeadId}", destination, "HEAD"]);
                var external = Path.Combine(fixture.TempRoot, "external sentinel");
                Directory.CreateDirectory(external);
                externalSentinel = Path.Combine(external, "sentinel.txt");
                File.WriteAllText(externalSentinel, "never follow or delete");
                WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(destination, "nested-link"), external);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var sentinelBefore = externalSentinel is null ? null : File.ReadAllText(externalSentinel);
        var rootBefore = fixture.TargetCheckout();
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provisioner().ProvisionAsync(
            fixture.Paths, fixture.Project, fixture.Repository, fixture.BeadId,
            WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.Equal(rootBefore, fixture.TargetCheckout());
        if (File.Exists(Path.Combine(destination, "sentinel")))
            Assert.Equal("preserve occupied destination", File.ReadAllText(Path.Combine(destination, "sentinel")));
        if (externalSentinel is not null) Assert.Equal(sentinelBefore, File.ReadAllText(externalSentinel));
    }
}

internal sealed class ProvisioningGitFixture : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly string seed;
    private readonly string bare;
    private readonly string globalConfig;
    private readonly string beadId = "ns-xci.1";
    private readonly ProjectSlug project = new("demo");

    internal ProvisioningGitFixture()
    {
        TempRoot = temp.Path;
        seed = temp.CreateDirectory("seed repo");
        bare = Path.Combine(temp.Path, "bare origin.git");
        Target = Path.Combine(temp.Path, "target repository");
        Host = Path.Combine(temp.Path, "nightweaver host with spaces");
        Directory.CreateDirectory(Host);
        globalConfig = Path.Combine(temp.Path, "no global git config");

        Git(seed, ["init", "-b", "main"]);
        File.WriteAllText(Path.Combine(seed, "README.md"), "base\n");
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "ignored-cache/\n");
        Commit(seed, "initial base");
        Git(temp.Path, ["init", "--bare", bare]);
        Git(seed, ["remote", "add", "origin", bare]);
        Git(seed, ["push", "-u", "origin", "main"]);
        Git(bare, ["symbolic-ref", "HEAD", "refs/heads/main"]);
        Git(temp.Path, ["clone", bare, Target]);
        InitialSha = Git(seed, ["rev-parse", "HEAD"]).Output.Trim();

        // Sentinels include staged, unstaged, untracked, and ignored root data.
        File.AppendAllText(Path.Combine(Target, "README.md"), "unstaged root sentinel\n");
        File.WriteAllText(Path.Combine(Target, "staged-root.txt"), "staged root sentinel");
        Git(Target, ["add", "staged-root.txt"]);
        File.WriteAllText(Path.Combine(Target, "untracked-root.txt"), "untracked root sentinel");
        Directory.CreateDirectory(Path.Combine(Target, "untracked-root-directory"));
        File.WriteAllText(Path.Combine(Target, "untracked-root-directory", "nested.txt"), "nested root sentinel");
        Directory.CreateDirectory(Path.Combine(Target, "ignored-cache"));
        File.WriteAllText(Path.Combine(Target, "ignored-cache", "cache.bin"), "ignored root sentinel");
    }

    internal string TempRoot { get; }
    internal string BareOrigin => bare;
    internal string Target { get; }
    internal string Host { get; }
    internal string WorktreesRoot => Path.Combine(Host, "worktrees", "demo");
    internal string BeadId => beadId;
    internal ProjectSlug Project => project;
    internal HostPaths Paths => new(Host);
    internal ValidatedTargetRepository Repository => new(Target, "main");
    internal string InitialSha { get; }
    internal string? AdvancedSha { get; private set; }

    internal WorktreeProvisioner Provisioner() => new(new IsolatedGitRunner(globalConfig));

    internal void CreateDestinationParent() => Directory.CreateDirectory(WorktreesRoot);

    internal void AdvanceSeedMain()
    {
        File.AppendAllText(Path.Combine(seed, "README.md"), "advance origin main\n");
        Commit(seed, "advance default branch");
        Git(seed, ["push", "origin", "main"]);
        AdvancedSha = Git(seed, ["rev-parse", "HEAD"]).Output.Trim();
    }

    internal string CreateAndPushRemoteBeadBranch(string? beadId = null)
    {
        var branch = $"nightweaver/{beadId ?? BeadId}";
        Git(seed, ["switch", "-c", branch]);
        File.WriteAllText(Path.Combine(seed, "remote-bead.txt"), "remote bead work");
        Commit(seed, "remote bead work");
        Git(seed, ["push", "-u", "origin", branch]);
        Git(seed, ["switch", "main"]);
        return Git(seed, ["rev-parse", branch]).Output.Trim();
    }

    internal RootCheckoutState TargetCheckout()
    {
        var index = File.ReadAllBytes(Path.Combine(Target, ".git", "index"));
        var content = Directory.EnumerateFileSystemEntries(Target)
            .Where(path => !string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Directory.Exists(path) ? Snapshot.SafeEntries(path).Prepend(path) : [path])
            .Select(path =>
            {
                var relative = Path.GetRelativePath(Target, path).Replace('\\', '/');
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return $"L {relative} {new FileInfo(path).LinkTarget}";
                if ((attributes & FileAttributes.Directory) != 0) return $"D {relative}";
                using var stream = File.OpenRead(path);
                return $"F {relative} {Convert.ToHexString(SHA256.HashData(stream))}";
            })
            .Order(StringComparer.Ordinal).ToArray();
        return new(Convert.ToHexString(SHA256.HashData(index)), string.Join("\n", content),
            Git(Target, ["rev-parse", "HEAD"]).Output.Trim(),
            Git(Target, ["symbolic-ref", "--quiet", "HEAD"]).Output.Trim(),
            Git(Target, ["status", "--porcelain=v1", "--untracked-files=all"]).Output);
    }

    internal GitResult Git(string cwd, IReadOnlyList<string> arguments, bool allowFailure = false) => RunGit(cwd, arguments, allowFailure);

    private GitResult RunGit(string cwd, IReadOnlyList<string> arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git fixture process");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {stderr}");
        return new(process.ExitCode, stdout, stderr);
    }

    private void Commit(string cwd, string message)
    {
        Git(cwd, ["add", "-A"]);
        Git(cwd, ["-c", "user.name=Nightweaver Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", message]);
    }

    public void Dispose() => temp.Dispose();

    internal sealed record GitResult(int ExitCode, string Output, string Error)
    {
        internal void EnsureCommitted(string message, string cwd)
        {
            if (ExitCode != 0) throw new InvalidOperationException($"Could not commit '{message}' in '{cwd}': {Error}");
        }
    }

    internal sealed record RootCheckoutState(string IndexSha, string WorkingFiles, string Head, string Branch, string Status);

    private sealed class IsolatedGitRunner(string globalConfig) : IProcessRunner
    {
        private readonly SystemProcessRunner runner = new();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_CONFIG_GLOBAL"] = globalConfig,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0"
            };
            foreach (var (name, value) in request.Environment) environment[name] = value;
            return runner.RunAsync(request with { Environment = environment }, cancellationToken);
        }
    }
}
