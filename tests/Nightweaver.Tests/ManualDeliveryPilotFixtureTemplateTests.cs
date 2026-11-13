using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilotFixture = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;

namespace Nightweaver.Tests;

public sealed class ManualDeliveryPilotFixtureTemplateTests
{
    [Fact]
    public async Task CallerCancellationSettlesPilotToolBeforeReturning()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilotFixture.CreateAsync(
            seedProfile: ManualPilotSeedProfile.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var marker = Path.Combine(fixture.Root, "cancelled-tool.pid");
        var script = Path.Combine(fixture.Root, "hold-tool.ps1");
        await File.WriteAllTextAsync(script,
            "param([string]$Marker)\n[IO.File]::WriteAllText($Marker, [string]$PID)\nStart-Sleep -Seconds 60\n",
            TestContext.Current.CancellationToken);
        var run = fixture.RunToolForTestAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-File", script, marker],
            fixture.Repository, cancellation.Token);
        System.Diagnostics.Process? child = null;
        try
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
                await Task.Delay(20, ready.Token);
            child = System.Diagnostics.Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, ready.Token),
                System.Globalization.CultureInfo.InvariantCulture));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(child.HasExited, "A cancelled tracker read must settle its child before another read or fixture teardown.");
        }
        finally
        {
            cancellation.Cancel();
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
                child.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(175)]
    public async Task NestedPilotRepositoryCanRebaseWithoutLeavingInterruptedState(int rootLength)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var root = rootLength == 0 ? temp.Path
            : Path.Combine(temp.Path, new string('n', rootLength - temp.Path.Length - 1));
        await using var fixture = await ManualPilotFixture.CreateAsync(root: root,
            seedProfile: ManualPilotSeedProfile.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        var token = TestContext.Current.CancellationToken;
        Task<string> Git(params string[] arguments) => fixture.RunToolForTestAsync("git", arguments, fixture.Repository, token);

        // bd init extends the seeded .gitignore. Keep that unrelated setup
        // change out of the rebase precondition.
        await Git("add", "-u");
        await Git("commit", "--allow-empty", "-m", "snapshot tracker initialization");
        await Git("checkout", "-b", "nightweaver/qa-long-path");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "task.txt"), "task\n", token);
        await Git("add", "task.txt");
        await Git("commit", "-m", "task change");
        var taskHead = (await Git("rev-parse", "HEAD")).Trim();
        await Git("checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "default.txt"), "default\n", token);
        await Git("add", "default.txt");
        await Git("commit", "-m", "default change");
        await Git("checkout", "nightweaver/qa-long-path");

        // Rebase creates a SHA..SHA patch name below .git/rebase-merge, even
        // when every tracked path is short. The fixture must survive nested roots.
        await Git("rebase", "main");
        Assert.NotEqual(taskHead, (await Git("rev-parse", "HEAD")).Trim());
        Assert.Equal("task\n", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "task.txt"), token));
        Assert.Equal("default\n", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "default.txt"), token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Repository, ".git", "rebase-merge")));
        Assert.Empty(await Git("status", "--porcelain", "--untracked-files=no"));
    }

    [Fact]
    public async Task ManualPilotCopiesKeepTrackerAndGitOriginChangesInsideTheirOwnRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var first = await ManualPilotFixture.CreateAsync(
            seedProfile: ManualPilotSeedProfile.ParentlessOnly,
            cancellationToken: TestContext.Current.CancellationToken);
        await using var second = await ManualPilotFixture.CreateAsync(
            seedProfile: ManualPilotSeedProfile.ParentlessOnly,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Root, second.Root);
        Assert.NotEqual(first.Repository, second.Repository);
        Assert.NotEqual(first.Origin, second.Origin);
        Assert.Equal(first.ParentlessTaskId, second.ParentlessTaskId);
        Assert.NotNull(first.ParentlessTaskId);
        Assert.True(string.IsNullOrEmpty(first.ProductId));
        Assert.True(string.IsNullOrEmpty(first.ProductTaskId));
        Assert.NotNull(second.ParentlessTaskId);
        Assert.True(string.IsNullOrEmpty(second.ProductId));
        Assert.True(string.IsNullOrEmpty(second.ProductTaskId));

        var secondMainBefore = (await second.ReadOriginRefAsync("refs/heads/main", TestContext.Current.CancellationToken)).Trim();
        var pushUrl = await first.RunToolForTestAsync("git", ["config", "--get", "remote.origin.pushurl"],
            first.Repository, TestContext.Current.CancellationToken);
        Assert.Equal(first.Origin.Replace('\\', '/'), pushUrl.Trim());

        await first.RunToolForTestAsync("bd", ["update", first.ParentlessTaskId, "--status", "closed"],
            first.Repository, TestContext.Current.CancellationToken);

        var secondParentless = await second.ReadBeadAsync(second.ParentlessTaskId, TestContext.Current.CancellationToken);
        Assert.Equal("open", secondParentless.GetProperty("status").GetString());

        var marker = Path.Combine(first.Repository, "independent-origin.txt");
        await File.WriteAllTextAsync(marker, "first repository only\n", TestContext.Current.CancellationToken);
        await first.RunToolForTestAsync("git", ["add", "--", "independent-origin.txt"], first.Repository,
            TestContext.Current.CancellationToken);
        await first.RunToolForTestAsync("git", ["commit", "-m", "verify independent copied origin"], first.Repository,
            TestContext.Current.CancellationToken);
        await first.RunToolForTestAsync("git", ["push", "origin", "main"], first.Repository,
            TestContext.Current.CancellationToken);

        var firstMainAfter = (await first.ReadOriginRefAsync("refs/heads/main", TestContext.Current.CancellationToken)).Trim();
        var secondMainAfter = (await second.ReadOriginRefAsync("refs/heads/main", TestContext.Current.CancellationToken)).Trim();
        Assert.NotEqual(secondMainBefore, firstMainAfter);
        Assert.Equal(secondMainBefore, secondMainAfter);
    }
}
