using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class ConsoleJournalReaderTests
{
    [Fact]
    public async Task SlowSuccessfulPreparationCannotConsumeTheBatchCommandDeadline()
    {
        using var fixture = new Fixture();
        var page = await fixture.Reader.ReadAsync(42, TestContext.Current.CancellationToken);
        Assert.Single(page.Hints);
        Assert.Equal(0, fixture.Client.Cursor);
        Assert.Equal(1, fixture.Client.Enables);
        Assert.True(fixture.Clock.GetTimestamp() > TimeSpan.FromSeconds(2).Ticks);
        await fixture.Reader.ReadAsync(42, TestContext.Current.CancellationToken);
        Assert.Equal(42, fixture.Client.Cursor);
        Assert.Equal(1, fixture.Client.Enables);
        fixture.Runner.Branch = "other";
        await fixture.Reader.ReadAsync(42, TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Client.Cursor);
        Assert.Equal(2, fixture.Client.Enables);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("branch")]
    [InlineData("setup")]
    [InlineData("batch")]
    public async Task EveryNativeOperationRetainsItsTwoSecondDeadline(string slow)
    {
        using var fixture = new Fixture();
        fixture.Runner.Slow = slow;
        fixture.Client.Slow = slow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Reader.ReadAsync(42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TurnBudgetFailureKeepsSuccessfulSetupButCannotLoseTheCursorReset()
    {
        using var fixture = new Fixture();
        fixture.Runner.Cost = fixture.Client.Cost = 1.4;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Reader.ReadAsync(42, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Client.Enables);
        fixture.Runner.Cost = fixture.Client.Cost = 1.1;
        await fixture.Reader.ReadAsync(42, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Client.Enables);
        Assert.Equal(0, fixture.Client.Cursor);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal InvocationDeadlineClock Clock { get; } = new();
        internal Runner Runner { get; }
        internal Client Client { get; }
        internal ConsoleJournalReader Reader { get; }
        internal Fixture()
        {
            temp.CreateDirectory(".git");
            temp.CreateDirectory(".beads");
            temp.WriteFile(".beads/metadata.json", "{}");
            var settings = new AgentSettings(AgentCli.Codex, null, "high", 30);
            var project = new ProjectConfig(ProjectSlug.Parse("test"), "fixture.json", new(temp.Path, "main"),
                new(1, 1, 1), new(3, 2), settings,
                new RoleSettings(AgentRoles.All.ToDictionary(role => role, _ => settings)));
            Runner = new Runner(Clock, temp.Path);
            Client = new Client(Clock);
            Reader = new ConsoleJournalReader(project, Runner, Clock, journalClient: Client);
        }
        public void Dispose() => temp.Dispose();
    }

    private sealed class Runner(InvocationDeadlineClock clock, string path) : IProcessRunner
    {
        internal string? Slow { get; set; }
        internal string Branch { get; set; } = "main";
        internal double Cost { get; set; } = 1.1;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            var branch = request.FileName == "bd";
            clock.Advance(TimeSpan.FromSeconds(Slow == (branch ? "branch" : "git") ? 2.01 : branch ? Cost : .1));
            token.ThrowIfCancellationRequested();
            var output = branch ? "{\"current\":\"" + Branch + "\"}"
                : request.Arguments[0] == "check-ref-format" ? "main\n"
                : request.Arguments[0] == "symbolic-ref" ? "refs/heads/main\n"
                : $"true\n\n{Path.Combine(path, ".git")}\n{Path.Combine(path, ".git")}\n";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
    }

    private sealed class Client(InvocationDeadlineClock clock) : IBeadsInvalidationClient
    {
        internal string? Slow { get; set; }
        internal double Cost { get; set; } = 1.1;
        internal int Enables { get; private set; }
        internal long Cursor { get; private set; }
        public Task EnableAsync(ValidatedTargetRepository repository, CancellationToken token)
        {
            clock.Advance(TimeSpan.FromSeconds(Slow == "setup" ? 2.01 : Cost));
            token.ThrowIfCancellationRequested();
            Enables++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<BeadsInvalidation>> ReadInvalidationsAsync(
            ValidatedTargetRepository repository, long cursor, int maxRecords, CancellationToken token)
        {
            Assert.Equal(256, maxRecords);
            clock.Advance(TimeSpan.FromSeconds(Slow == "batch" ? 2.01 : Cost));
            token.ThrowIfCancellationRequested();
            Cursor = cursor;
            return Task.FromResult<IReadOnlyList<BeadsInvalidation>>([new(cursor + 1, "comment", "task-1", null)]);
        }
    }
}
