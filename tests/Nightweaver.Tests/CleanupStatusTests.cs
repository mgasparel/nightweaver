using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Fixture = Nightweaver.Tests.CompletedEnvironmentCleanupTests.Fixture;

namespace Nightweaver.Tests;

public sealed class CleanupStatusTests
{
    [Fact]
    public async Task MissingCorruptAndStoppedEvidenceNeverLooksLikeLiveEmptySuccess()
    {
        using var fixture = new Fixture();
        var reader = new CleanupStatusReader(fixture.Paths, Config(fixture), fixture.Repository);
        Assert.Null(reader.Read().Counts);
        Assert.Equal("unavailable", reader.Read().Availability);
        var record = fixture.Create() with { Status = CleanupStatus.NeedsAttention, Diagnostic = "policy denied" };
        await fixture.Store.WriteAsync(record, TestContext.Current.CancellationToken);
        var state = reader.Read();
        Assert.False(state.CanRetry);
        Assert.NotEqual("live", state.Availability);
        Assert.Equal(1, state.Counts!["needs-attention"]);
        Assert.Equal(CleanupRetryInbox.Revision(record), Assert.Single(state.Items).Revision);
        File.WriteAllText(fixture.RecordPath, "{");
        Assert.Null(reader.Read().Counts);
        Assert.Equal("{", File.ReadAllText(fixture.RecordPath));
        Assert.True(Directory.Exists(fixture.Worktree));
    }

    [Fact]
    public async Task FullInventorySearchPaginationAndRetentionPreserveAllUnresolvedEvidence()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 19; index++)
            await fixture.Store.WriteAsync(fixture.Store.Create($"pending-{index:D2}", WorktreePurpose.Delivery, null), TestContext.Current.CancellationToken);
        var old = fixture.Store.Create("old-resolved", WorktreePurpose.Delivery, null);
        await fixture.Store.WriteAsync(old with { Status = CleanupStatus.Resolved, ResolvedAt = now.AddDays(-31),
            Worktree = old.Worktree with { ConfirmedAbsent = true }, Tmp = old.Tmp with { ConfirmedAbsent = true } }, TestContext.Current.CancellationToken);
        var reader = new CleanupStatusReader(fixture.Paths, Config(fixture), fixture.Repository);
        Assert.Equal(8, reader.Read().Items.Count);
        Assert.Equal(3, reader.Read().Pages);
        Assert.Equal(20, reader.Read().Total);
        Assert.Equal("pending-18", Assert.Single(reader.Read(search: "pending-18").Items).BeadId);
        Assert.Single(reader.Read(status: "resolved").Items);
        Assert.Equal(3, reader.Read(int.MaxValue).Page);
        fixture.Store.TrimResolvedHistory(now);
        Assert.Equal(19, fixture.Store.Read().Obligations.Count);
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.True(File.Exists(fixture.Log));
    }

    [Fact]
    public async Task IdleOwnerExpiresOnlyOldResolvedBookkeepingWithoutDisposal()
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var retained = fixture.Create() with { Status = CleanupStatus.NeedsAttention };
        await fixture.Store.WriteAsync(retained, token);
        var old = fixture.Store.Create("old", WorktreePurpose.Delivery, null);
        await fixture.Store.WriteAsync(old with { Status = CleanupStatus.Resolved, ResolvedAt = DateTimeOffset.UtcNow.AddDays(-31),
            Worktree = old.Worktree with { ConfirmedAbsent = true }, Tmp = old.Tmp with { ConfirmedAbsent = true } }, token);
        using var worker = new CompletedEnvironmentWorker(fixture.Paths, Config(fixture), fixture.Repository,
            fixture.Runner, new(), new(), "idle-owner-0000000001", _ => { });
        await worker.RunBatchAsync(token);
        Assert.Equal(retained, Assert.Single(fixture.Store.Read().Obligations));
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.True(Directory.Exists(fixture.Worktree));
        Assert.True(File.Exists(fixture.Log));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    internal static ProjectConfig Config(Fixture fixture)
    {
        var defaults = new AgentSettings(AgentCli.Codex, null, "low", 1);
        return new(Fixture.Project, "test.json", new(fixture.Repository.Path, "main"), new(1, 2, 10), new(3, 3), defaults,
            new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => defaults)));
    }
}
