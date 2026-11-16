using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class FixtureTemplateTests
{
    [Fact]
    public void BeadsCopiesHaveIndependentDatabasesAndLeaveTheTemplatePristine()
    {
        using var first = new BeadsFixture("first tracker");
        using var second = new BeadsFixture("second tracker");
        var created = first.Create("Only in the first copy");
        first.Run("bd", ["update", first.ControlBeadId, "--status", "closed"]);

        var secondInventory = BeadsReader.Parse(second.Run("bd", BeadsCommand.Create(second.Path).Arguments));
        Assert.DoesNotContain(created, secondInventory.ById.Keys);
        Assert.Equal("open", secondInventory.ById[second.ControlBeadId].Status);

        // A copy made after mutation must also inherit only the pristine baseline.
        using var later = new BeadsFixture("later tracker");
        var laterInventory = BeadsReader.Parse(later.Run("bd", BeadsCommand.Create(later.Path).Arguments));
        Assert.DoesNotContain(created, laterInventory.ById.Keys);
        Assert.Equal("open", laterInventory.ById[later.ControlBeadId].Status);
    }

    [Fact]
    public void GitCopiesHaveIndependentConfigurationOriginsAndSeedHistories()
    {
        using var firstGit = new GitFixture();
        using var secondGit = new GitFixture();
        var firstConfig = Path.Combine(firstGit.Path, ".git", "config");
        var originalConfig = File.ReadAllText(firstConfig);
        File.AppendAllText(firstConfig, "\n[fixture]\n\tcopy = first\n");
        File.WriteAllText(Path.Combine(firstGit.Path, ".beads", "metadata.json"), "first copy only");
        Assert.Equal(originalConfig, File.ReadAllText(Path.Combine(secondGit.Path, ".git", "config")));
        using var laterGit = new GitFixture();
        Assert.Equal(originalConfig, File.ReadAllText(Path.Combine(laterGit.Path, ".git", "config")));
        Assert.Equal("{\"backend\":\"dolt\"}", File.ReadAllText(Path.Combine(laterGit.Path, ".beads", "metadata.json")));

        using var first = new ProvisioningGitFixture();
        using var second = new ProvisioningGitFixture();
        first.AdvanceSeedMain();

        Assert.NotEqual(first.InitialSha, first.AdvancedSha);
        Assert.Equal(first.AdvancedSha, first.Git(first.BareOrigin, ["rev-parse", "main"]).Output.Trim());
        Assert.Equal(second.InitialSha, second.Git(second.BareOrigin, ["rev-parse", "main"]).Output.Trim());
        using var later = new ProvisioningGitFixture();
        Assert.Equal(later.InitialSha, later.Git(later.BareOrigin, ["rev-parse", "main"]).Output.Trim());
    }
}
