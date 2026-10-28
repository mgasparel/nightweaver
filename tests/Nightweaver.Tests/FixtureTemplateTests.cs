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
    public void ProvisioningCopiesHaveIndependentOriginsAndSeedHistories()
    {
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
