using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class ImplementerOutcomePublisherTests
{
    [Fact]
    public async Task RereadsAndAcceptsOutcomeWriteThatCommittedBeforeTheCliReportedFailure()
    {
        using var fixture = new PublisherFixture { FailAfterCommittedPublish = true };

        var result = await fixture.Publisher.PublishAsync(fixture.Session, fixture.Invocation,
            fixture.ReadyForReview, releaseClaim: true, TestContext.Current.CancellationToken);

        Assert.Equal("stage:review", fixture.State.Stage);
        Assert.Equal("open", fixture.State.Status);
        Assert.Null(fixture.State.Assignee);
        Assert.Equal(1, fixture.PublishWriteCount);
        Assert.Equal("stage:review", result.VerifiedState!.Stage);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("workflow")]
    public async Task RefusesPublicationWhenOwnerOrWorkflowChangesBeforeGuardedWrite(string changeKind)
    {
        using var fixture = new PublisherFixture { ChangeBeforePublish = changeKind };

        await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.Publisher.PublishAsync(
            fixture.Session, fixture.Invocation, fixture.ReadyForReview,
            releaseClaim: true, TestContext.Current.CancellationToken));

        Assert.Equal(0, fixture.PublishWriteCount);
        if (changeKind == "owner")
        {
            Assert.Equal("human@example.invalid", fixture.State.Assignee);
            Assert.Equal("stage:implement", fixture.State.Stage);
        }
        else
        {
            Assert.Equal("nightweaver:demo", fixture.State.Assignee);
            Assert.Equal("stage:qa", fixture.State.Stage);
        }
    }

    private sealed class PublisherFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private readonly MutableBeadsRunner runner;

        internal PublisherFixture()
        {
            var repositoryPath = temp.CreateDirectory("target");
            var host = new HostPaths(temp.CreateDirectory("host"));
            var worktreePath = Directory.CreateDirectory(Path.Combine(host.WorktreesRoot(ProjectSlug.Parse("demo")), BeadId)).FullName;
            Project = CreateProject();
            var repository = new ValidatedTargetRepository(repositoryPath, "main");
            State = new MutableBeadState();
            runner = new MutableBeadsRunner(State);
            Publisher = new ImplementerOutcomePublisher(runner);
            Session = new AgentStageSession(host, Project, repository, BeadId, AgentRole.Implementer,
                WorktreePurpose.Delivery, new ProvisionedWorktree(worktreePath, new string('a', 40)),
                InitialState, Owner, new AgentDispatchContext(BeadId, "task", "stage:implement", 0),
                PreserveInheritedChanges: false);
            var observations = new AgentStageObservations(InitialState, InitialState, Owner, Owner,
                AgentExecutionStatus.Exited, 0, $"nightweaver/{BeadId}", new string('b', 40), string.Empty, true);
            Invocation = new AgentStageResult(AgentRole.Implementer, BeadId, "invocation-1", "C:\\logs\\invocation-1.log",
                null, [], Observations: observations);
            ReadyForReview = new(AgentOutcomeChoice.ReadyForReview, "fixture", "verified evidence");
        }

        private const string BeadId = "ns-pub.1";
        private const string Owner = "nightweaver:demo";
        private static readonly BeadWorkflowState InitialState = new(BeadId, "task", "in_progress", "stage:implement",
            false, false, false, 0);

        internal ProjectConfig Project { get; }
        internal MutableBeadState State { get; }
        internal ImplementerOutcomePublisher Publisher { get; }
        internal AgentStageSession Session { get; }
        internal AgentStageResult Invocation { get; }
        internal AgentOutcomeClassificationResult ReadyForReview { get; }
        internal bool FailAfterCommittedPublish { get => runner.FailAfterCommittedPublish; init => runner.FailAfterCommittedPublish = value; }
        internal string? ChangeBeforePublish { get => runner.ChangeBeforePublish; init => runner.ChangeBeforePublish = value; }
        internal int PublishWriteCount => runner.PublishWriteCount;

        public void Dispose() => temp.Dispose();

        private static ProjectConfig CreateProject()
        {
            var settings = AgentRoles.All.ToDictionary(role => role,
                _ => new AgentSettings(AgentCli.Claude, null, "low", 1));
            return new ProjectConfig(ProjectSlug.Parse("demo"), "fixture.json", new("target", "main"),
                new(1, 1, 1), new(3, 1), new(AgentCli.Claude, null, "low", 1), new RoleSettings(settings));
        }
    }

    private sealed class MutableBeadState
    {
        internal string Status { get; set; } = "in_progress";
        internal string? Assignee { get; set; } = "nightweaver:demo";
        internal string Stage { get; set; } = "stage:implement";
        internal List<string> Comments { get; } = [];
    }

    private sealed class MutableBeadsRunner(MutableBeadState state) : IProcessRunner
    {
        internal bool FailAfterCommittedPublish { get; set; }
        internal string? ChangeBeforePublish { get; set; }
        internal int PublishWriteCount { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName != "bd") throw new InvalidOperationException($"Unexpected process {request.FileName}.");
            var args = request.Arguments;
            if (Contains(args, "comments") && Contains(args, "add"))
            {
                state.Comments.Add(args[^1]);
                if (ChangeBeforePublish == "owner")
                {
                    state.Assignee = "human@example.invalid";
                    ChangeBeforePublish = null;
                }
                else if (ChangeBeforePublish == "workflow")
                {
                    state.Stage = "stage:qa";
                    ChangeBeforePublish = null;
                }
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            if (Contains(args, "comments"))
            {
                var comments = state.Comments.Select((text, index) => new
                {
                    id = $"comment-{index}", issue_id = "ns-pub.1", text, created_at = DateTimeOffset.UnixEpoch.AddSeconds(index)
                });
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(comments), string.Empty));
            }
            if (Contains(args, "update") && Contains(args, "--add-label"))
            {
                PublishWriteCount++;
                var addIndex = IndexOf(args, "--add-label");
                if (state.Stage is { } stage && stage.StartsWith("stage:", StringComparison.Ordinal))
                    state.Stage = string.Empty;
                state.Stage = args[addIndex + 1];
                state.Status = ValueAfter(args, "--status");
                state.Assignee = ValueAfter(args, "--assignee");
                if (state.Assignee == string.Empty) state.Assignee = null;
                if (FailAfterCommittedPublish)
                {
                    FailAfterCommittedPublish = false;
                    return Task.FromResult(new ProcessResult(1, string.Empty, "simulated lost write response"));
                }
                return Task.FromResult(new ProcessResult(0, "{}", string.Empty));
            }
            if (Contains(args, "list"))
            {
                var inventory = new[]
                {
                    new
                    {
                        id = "ns-pub.1", status = state.Status, issue_type = "task", priority = 2,
                        created_at = "2026-01-01T00:00:00Z", assignee = state.Assignee,
                        labels = new[] { state.Stage }.Where(label => !string.IsNullOrEmpty(label)).ToArray(),
                        metadata = new { repair_cycles = 0 }
                    }
                };
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(inventory), string.Empty));
            }
            throw new InvalidOperationException($"Unexpected bd arguments: {string.Join(' ', args)}");
        }

        private static bool Contains(IReadOnlyList<string> args, string value) => args.Contains(value, StringComparer.Ordinal);
        private static int IndexOf(IReadOnlyList<string> args, string value)
        {
            for (var index = 0; index < args.Count; index++)
                if (string.Equals(args[index], value, StringComparison.Ordinal)) return index;
            return -1;
        }
        private static string ValueAfter(IReadOnlyList<string> args, string option) =>
            args[IndexOf(args, option) + 1];
    }
}
