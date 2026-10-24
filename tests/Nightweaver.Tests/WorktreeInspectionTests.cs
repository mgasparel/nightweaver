using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WorktreeInspectionTests
{
    [Fact]
    public async Task ReaderUsesExactReadOnlyContractAndPreservesPorcelainValues()
    {
        using var temp = new TempDirectory();
        var raw = Path.Combine(temp.Path, "space unicode é\npath");
        var output = $"worktree {temp.Path}\0bare\0\0worktree {raw}\0HEAD {new string('a', 40)}\0detached\0locked reason\nwith spaces\0prunable\0future value\0\0";
        using var cancellation = new CancellationTokenSource();
        var runner = new RecordingProcessRunner((request, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Equal("git", request.FileName);
            Assert.Equal((string[])["worktree", "list", "--porcelain", "-z"], request.Arguments);
            Assert.Equal(temp.Path, request.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
            Assert.Equal("0", request.Environment["GIT_OPTIONAL_LOCKS"]);
            Assert.Null(request.Environment["GIT_DIR"]);
            Assert.Null(request.Environment["GIT_INDEX_FILE"]);
            return Task.FromResult(new ProcessResult(0, output, ""));
        });
        var records = await new GitWorktreeReader(runner).ReadAsync(new(temp.Path, "main"), cancellation.Token);
        Assert.True(records[0].Main);
        Assert.True(records[0].Bare);
        Assert.Equal(raw, records[1].RawPath);
        Assert.True(records[1].Detached);
        Assert.True(records[1].Locked);
        Assert.Equal("reason\nwith spaces", records[1].LockReason);
        Assert.True(records[1].Prunable);
        Assert.Null(records[1].PruneReason);
        Assert.Single(runner.Requests);
        var branch = Assert.Single(GitWorktreeReader.Parse($"worktree {temp.Path}\0HEAD {new string('a', 64)}\0branch refs/heads/main\0locked\0prunable reason\0\0"));
        Assert.Equal("refs/heads/main", branch.Branch);
        Assert.True(branch.Locked);
        Assert.Null(branch.LockReason);
        Assert.Equal("reason", branch.PruneReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("worktree /path\0HEAD abc\0detached\0")]
    [InlineData("HEAD abc\0detached\0\0")]
    [InlineData("worktree relative\0bare\0\0")]
    [InlineData("worktree /path\0HEAD abc\0HEAD def\0detached\0\0")]
    [InlineData("worktree /path\0HEAD abc\0detached\0branch refs/heads/main\0\0")]
    [InlineData("worktree /path\0bare\0detached\0\0")]
    [InlineData("worktree /path\0HEAD abc\0\0")]
    public void MalformedOutputFailsAtomically(string output) => Assert.Throws<FormatException>(() => GitWorktreeReader.Parse(output));

    [Fact]
    public void ConflictingNormalizedRegistrationsAreRejected()
    {
        using var temp = new TempDirectory();
        Assert.Throws<FormatException>(() => GitWorktreeReader.Parse($"worktree {temp.Path}\0bare\0\0worktree {Path.Combine(temp.Path, ".")}\0bare\0\0"));
    }

    [Fact]
    public async Task RealUnbornAndBareRegistrationsAreAccepted()
    {
        using var repository = new GitFixture("unborn repo");
        var reader = new GitWorktreeReader(new SystemProcessRunner());
        var unborn = Assert.Single(await reader.ReadAsync(new(repository.Path, "main"), CancellationToken.None));
        Assert.True(unborn.Main);
        Assert.Equal(new string('0', 40), unborn.Head);
        Assert.Equal("refs/heads/main", unborn.Branch);
        using var temp = new TempDirectory();
        GitFixture.RunGit(temp.Path, ["init", "--bare", "bare repo"]);
        var bare = Assert.Single(await reader.ReadAsync(new(Path.Combine(temp.Path, "bare repo"), "main"), CancellationToken.None));
        Assert.True(bare.Bare);
        Assert.Null(bare.Head);
        Assert.Null(bare.Branch);
    }

    [Fact]
    public void KnownFieldDuplicatesAndConflictsFailOnNativeAbsolutePaths()
    {
        using var temp = new TempDirectory();
        var prefix = $"worktree {temp.Path}\0HEAD {new string('a', 40)}\0detached\0";
        foreach (var suffix in new[] { "locked\0locked\0", "prunable\0prunable why\0", "branch refs/heads/main\0", "bare\0", $"HEAD {new string('a', 40)}\0", $"worktree {temp.Path}\0" })
            Assert.Throws<FormatException>(() => GitWorktreeReader.Parse(prefix + suffix + "\0"));
        Assert.Throws<FormatException>(() => GitWorktreeReader.Parse($"worktree {temp.Path}\0HEAD invalid\0detached\0\0"));
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("missing")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("invalid")]
    public async Task ReaderFailuresHaveRepositoryAndCommandContext(string failure)
    {
        using var temp = new TempDirectory();
        var runner = new RecordingProcessRunner((_, token) => failure switch
        {
            "missing" => throw new ToolExecutionException("executable missing"),
            "timeout" => throw new ToolExecutionException("timed out"),
            "cancelled" => throw new OperationCanceledException(token),
            "exit" => Task.FromResult(new ProcessResult(1, "partial", "fatal")),
            _ => Task.FromResult(new ProcessResult(0, "invalid", ""))
        });
        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => new GitWorktreeReader(runner).ReadAsync(new(temp.Path, "main"), CancellationToken.None));
        Assert.Contains("git worktree list --porcelain -z", error.Message, StringComparison.Ordinal);
        Assert.Contains(temp.Path, error.Message, StringComparison.Ordinal);
        if (failure == "exit") Assert.Equal("fatal", error.StandardError);
    }

    [Fact]
    public void MissingAndPopulatedRuntimeRootsAreShallowAndReadOnly()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(Path.Combine(temp.Path, "host"));
        var before = Snapshot.FileTree(temp.Path);
        var missing = RuntimeDirectoryReader.Read(paths, new ProjectSlug("demo"));
        Assert.Equal(DirectoryState.Missing, missing.Worktrees.State);
        Assert.Empty(missing.Tmp.Entries);
        Assert.Equal(before, Snapshot.FileTree(temp.Path));
        temp.WriteFile("host/worktrees/demo/bead/deep/sentinel", "keep");
        temp.CreateDirectory("host/worktrees/other/ignored");
        temp.CreateDirectory("host/tmp/demo/bead/invocation");
        before = Snapshot.FileTree(temp.Path);
        var runtime = RuntimeDirectoryReader.Read(paths, new ProjectSlug("demo"));
        Assert.Equal("bead", Assert.Single(runtime.Worktrees.Entries).Id);
        Assert.Equal("bead", Assert.Single(runtime.Tmp.Entries).Id);
        Assert.Equal(before, Snapshot.FileTree(temp.Path));
        temp.WriteFile("host/tmp/demo/file", "unexpected");
        Assert.Throws<ToolExecutionException>(() => RuntimeDirectoryReader.Read(paths, new ProjectSlug("demo")));
    }

    [Fact]
    public void FileInPlaceOfRootFailsAndMissingTmpRootStaysMissing()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("host/worktrees", "not a root directory");
        var exception = Assert.Throws<ToolExecutionException>(() => RuntimeDirectoryReader.ReadRoot(Path.Combine(file, "demo")));
        Assert.Contains("expected a directory", exception.Message, StringComparison.Ordinal);
        Assert.Equal(DirectoryState.Missing, RuntimeDirectoryReader.ReadRoot(Path.Combine(temp.Path, "host/tmp/demo")).State);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "host/tmp")));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("host/worktrees")]
    [InlineData("host/worktrees/demo")]
    [InlineData("host/worktrees/demo/bead")]
    public void JunctionsAtEveryLevelAreNeverFollowed(string linkPath)
    {
        using var temp = new TempDirectory();
        using var external = new TempDirectory();
        external.WriteFile("sentinel", "must remain unchanged");
        var link = Path.Combine(temp.Path, linkPath);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateDirectoryLink(link, external.Path);
        var before = Snapshot.FileTree(temp.Path);
        var externalBefore = Snapshot.FileTree(external.Path);
        var snapshot = RuntimeDirectoryReader.ReadRoot(Path.Combine(temp.Path, "host/worktrees/demo"));
        if (linkPath.EndsWith("bead", StringComparison.Ordinal)) Assert.Equal(DirectoryState.UnsafeLink, Assert.Single(snapshot.Entries).State);
        else { Assert.Equal(DirectoryState.UnsafeLink, snapshot.State); Assert.Empty(snapshot.Entries); }
        Assert.Equal(before, Snapshot.FileTree(temp.Path));
        Assert.Equal(externalBefore, Snapshot.FileTree(external.Path));
    }

    internal static void CreateDirectoryLink(string path, string target)
    {
        path = Path.GetFullPath(path);
        target = Path.GetFullPath(target);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(path); start.ArgumentList.Add(target);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(path, target);
    }

    [Theory]
    [InlineData("../bead")]
    [InlineData("..\\bead")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/root")]
    [InlineData("C:bead")]
    public void UnsafeIdsAreNotOwned(string id) => Assert.False(RuntimePaths.SafeId(id));

    [Fact]
    public void SiblingPrefixAndNestedPathsAreNotOwned()
    {
        using var temp = new TempDirectory();
        Assert.Null(RuntimePaths.ChildId(temp.Path, temp.Path + "-other/bead"));
        Assert.Null(RuntimePaths.ChildId(temp.Path, Path.Combine(temp.Path, "bead", "nested")));
    }
}
