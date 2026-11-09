using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilotFixture = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;

namespace Nightweaver.Tests;

public sealed class ManualDeliveryPilotFixtureTemplateTests
{
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
