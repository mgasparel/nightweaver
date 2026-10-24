using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DispatchClaimTests
{
    [Fact]
    public async Task RealBdGuardAllowsOnlyOneSameActorContenderAndReleaseUsesEmptyAssignee()
    {
        using var target = new BeadsFixture("claim target with spaces");
        var beadId = target.Create("Claim race");
        const string actor = "nightweaver:claim-test";
        var repository = new ValidatedTargetRepository(target.Path, "main");
        var writer = new BeadsWriter(new SystemProcessRunner());

        var claimRequest = BeadsCommand.CreateWrite(target.Path,
            ["update", beadId, "--if-status", "open", "--if-assignee", "", "--status", "in_progress", "--assignee", actor, "--json"], actor);
        Assert.Equal(["--sandbox", "--actor", actor, "--dolt-auto-commit", "on", "update", beadId,
            "--if-status", "open", "--if-assignee", "", "--status", "in_progress", "--assignee", actor, "--json"], claimRequest.Arguments);
        Assert.Equal(target.Path, claimRequest.WorkingDirectory);
        Assert.Null(claimRequest.Environment["BEADS_DIR"]);
        Assert.Equal("false", claimRequest.Environment["BD_DOLT_AUTO_PUSH"]);

        async Task<string?> TryAcquireAsync()
        {
            try
            {
                await writer.AcquireDispatchClaimAsync(repository, beadId, actor, CancellationToken.None);
                return null;
            }
            catch (ToolExecutionException exception)
            {
                return exception.Message;
            }
        }

        var contenders = await Task.WhenAll(Task.Run(TryAcquireAsync), Task.Run(TryAcquireAsync));
        Assert.Single(contenders, error => error is null);
        var refusal = Assert.Single(contenders, error => error is not null);
        Assert.Contains("bd exited 13", refusal, StringComparison.Ordinal);
        var claimed = BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments)).ById[beadId];
        Assert.Equal("in_progress", claimed.Status);
        Assert.Equal(actor, claimed.Assignee);

        await writer.ReleaseDispatchClaimAsync(repository, beadId, actor, claimed.Status, CancellationToken.None);
        var released = BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments)).ById[beadId];
        Assert.Equal("open", released.Status);
        Assert.Null(released.Assignee);
    }
}
