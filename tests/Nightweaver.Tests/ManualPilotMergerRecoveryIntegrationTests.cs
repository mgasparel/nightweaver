using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Tests.ManualDeliveryPilotFixture;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;
using MergerChange = Nightweaver.Tests.AgentInvocationFixture.ManualPilotMergerChange;

namespace Nightweaver.Tests;

public sealed class ManualPilotMergerRecoveryIntegrationTests : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task AlreadyMergedRecoveryRejectsFailedChecksThenAcceptsFreshChecksWithoutRecreatingTheBranch()
    {
        await using var fixture = await ManualPilot.CreateAsync(seedProfile: ManualPilotSeedProfile.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        var token = TestContext.Current.CancellationToken;
        var paths = new HostPaths(fixture.NightweaverRoot);
        var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
        var bead = await fixture.CreateMergeGateTaskAsync("Recover already merged native fixture", token);
        var beadsEnvironment = new Dictionary<string, string?>(fixture.CreateEnvironment(), StringComparer.OrdinalIgnoreCase)
        {
            ["BD_ACTOR"] = project.RecoveryActor
        };
        var system = new SystemProcessRunner();
        var runner = new RecordingProcessRunner((request, cancellation) =>
        {
            var environment = new Dictionary<string, string?>(request.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in beadsEnvironment) environment[pair.Key] = pair.Value;
            return system.RunAsync(request with { Environment = environment }, cancellation);
        });
        var repository = new ValidatedTargetRepository(fixture.Repository, "main");
        try
        {
            var worktree = await new WorktreeProvisioner(runner).ProvisionAsync(paths, project.Name, repository,
                bead, WorktreePurpose.Delivery, token);
            Task<string> Git(params string[] arguments) => fixture.RunToolForTestAsync("git", arguments, worktree.Path, token);
            var expected = MergerChange.EnsureExpectedCommit(bead, $"nightweaver/{bead}", worktree.Path,
                (directory, arguments) => fixture.RunToolForTestAsync("git", arguments, directory, token).GetAwaiter().GetResult());
            await Git("push", "origin", $"HEAD:refs/heads/nightweaver/{bead}");
            await Git("push", "origin", "HEAD:refs/heads/main");
            await Git("push", "origin", "--delete", $"nightweaver/{bead}");
            using var existing = JsonDocument.Parse(await fixture.RunToolForTestAsync(
                Path.Combine(fixture.FakeCliRoot, "gh.exe"), ["pr", "view", AgentStageTargetFixture.FixturePullRequestUrl], worktree.Path, token));
            Assert.Equal("MERGED", existing.RootElement.GetProperty("state").GetString());
            Assert.Equal(expected.HeadSha, existing.RootElement.GetProperty("headRefOid").GetString());
            await new BeadsWriter(runner).AcquireDispatchClaimAsync(repository, bead, project.RecoveryActor, token);

            var beforeState = await fixture.ReadBeadAsync(bead, token);
            Assert.Equal("in_progress", beforeState.GetProperty("status").GetString());
            Assert.Equal(project.RecoveryActor, beforeState.GetProperty("assignee").GetString());
            var beforeLabels = ReadLabels(beforeState);
            var beforeComments = await ReadCommentTextsAsync(fixture, bead, token);
            var mainHead = (await fixture.ReadOriginRefAsync("refs/heads/main", token)).Trim();
            Assert.Equal(expected.HeadSha, mainHead);
            var remoteBranch = $"refs/heads/nightweaver/{bead}";
            Assert.Empty(await Git("ls-remote", "origin", remoteBranch));

            var failedGhLog = Path.Combine(fixture.Root, "recovery-failed-gh.jsonl");
            var failedEnvironment = CreateInvocationEnvironment(fixture, project.RecoveryActor, "fail", failedGhLog);
            Assert.False(File.Exists(failedGhLog));
            var preparer = new AgentInvocationPreparer();
            var failedInvocation = await preparer.PrepareAsync(paths, project, AgentRole.Merger,
                new AgentDispatchContext(bead, "task", "stage:merge", 0), worktree.Path, token);
            var failedResult = await AgentInvocationExecutor.ExecuteAsync(paths, project.Name, failedInvocation,
                new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(failedEnvironment), token);
            Assert.Equal(AgentExecutionStatus.Exited, failedResult.Status);
            Assert.NotEqual(0, failedResult.ExitCode);
            Assert.Equal(failedInvocation.InvocationId, failedResult.InvocationId);
            Assert.Equal(failedInvocation.LogFilePath, failedResult.LogFilePath);
            var failedInvocationLog = await File.ReadAllTextAsync(failedResult.LogFilePath, token);
            Assert.Contains($"gh pr checks {AgentStageTargetFixture.FixturePullRequestUrl} --required --json name,state,bucket exited 1",
                failedInvocationLog, StringComparison.Ordinal);
            Assert.Contains("\"state\":\"FAILURE\"", failedInvocationLog, StringComparison.Ordinal);

            var failedCommands = ReadGhCommands(failedGhLog);
            AssertFreshRecoveryRequests(failedCommands);
            Assert.Empty(await Git("ls-remote", "origin", remoteBranch));
            Assert.Equal(mainHead, (await fixture.ReadOriginRefAsync("refs/heads/main", token)).Trim());
            Assert.Equal(beforeComments, await ReadCommentTextsAsync(fixture, bead, token));
            Assert.DoesNotContain(beforeComments, comment => comment.Contains("nightweaver-merge-v1", StringComparison.Ordinal));
            AssertBeadWorkflowUnchanged(beforeState, await fixture.ReadBeadAsync(bead, token),
                beforeLabels, project.RecoveryActor);

            var successfulGhLog = Path.Combine(fixture.Root, "recovery-success-gh.jsonl");
            var successfulEnvironment = CreateInvocationEnvironment(fixture, project.RecoveryActor, "success", successfulGhLog);
            Assert.False(File.Exists(successfulGhLog));
            Assert.NotSame(failedEnvironment, successfulEnvironment);
            var successfulInvocation = await preparer.PrepareAsync(paths, project, AgentRole.Merger,
                new AgentDispatchContext(bead, "task", "stage:merge", 0), worktree.Path, token);
            var successfulResult = await AgentInvocationExecutor.ExecuteAsync(paths, project.Name, successfulInvocation,
                new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(successfulEnvironment), token);
            Assert.Equal(AgentExecutionStatus.Exited, successfulResult.Status);
            Assert.Equal(0, successfulResult.ExitCode);
            Assert.Equal(successfulInvocation.InvocationId, successfulResult.InvocationId);
            Assert.Equal(successfulInvocation.LogFilePath, successfulResult.LogFilePath);
            Assert.NotEqual(failedInvocation.InvocationId, successfulInvocation.InvocationId);
            Assert.NotEqual(failedInvocation.LogFilePath, successfulInvocation.LogFilePath);
            Assert.NotEqual(failedGhLog, successfulGhLog);
            Assert.Equal("fail", failedEnvironment["NIGHTWEAVER_FIXTURE_CI_RESULT"]);
            Assert.Equal("success", successfulEnvironment["NIGHTWEAVER_FIXTURE_CI_RESULT"]);
            var successfulInvocationLog = await File.ReadAllTextAsync(successfulResult.LogFilePath, token);
            Assert.Contains("fixture action completed: merger-verified-merge", successfulInvocationLog, StringComparison.Ordinal);

            var successfulCommands = ReadGhCommands(successfulGhLog);
            AssertFreshRecoveryRequests(successfulCommands);
            Assert.Empty(await Git("ls-remote", "origin", remoteBranch));
            Assert.Equal(mainHead, (await fixture.ReadOriginRefAsync("refs/heads/main", token)).Trim());
            var successfulComments = await ReadCommentTextsAsync(fixture, bead, token);
            Assert.Equal(beforeComments.Length + 1, successfulComments.Length);
            Assert.Contains(successfulComments,
                comment => comment.Contains($"checked_head={expected.HeadSha} required_checks=passed merge_method=squash merge_commit={expected.HeadSha}", StringComparison.Ordinal));
            AssertBeadWorkflowUnchanged(beforeState, await fixture.ReadBeadAsync(bead, token),
                beforeLabels, project.RecoveryActor);
        }
        catch
        {
            fixture.PreserveForFailure();
            TestContext.Current.TestOutputHelper?.WriteLine($"Already-merged native fixture preserved at {fixture.Root}");
            throw;
        }
    }

    private static Dictionary<string, string?> CreateInvocationEnvironment(ManualPilot fixture,
        string recoveryActor, string checksResult, string ghLogPath)
    {
        var environment = new Dictionary<string, string?>(fixture.CreateEnvironment(), StringComparer.OrdinalIgnoreCase)
        {
            ["BD_ACTOR"] = recoveryActor,
            ["NIGHTWEAVER_FIXTURE_CI_RESULT"] = checksResult,
            ["NIGHTWEAVER_FIXTURE_GH_LOG"] = ghLogPath
        };
        return environment;
    }

    private static string[] ReadCommentTexts(string commentsJson)
    {
        using var document = JsonDocument.Parse(commentsJson);
        var root = document.RootElement;
        var comments = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        return comments.Select(comment => comment.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : string.Empty).ToArray();
    }

    private static async Task<string[]> ReadCommentTextsAsync(ManualPilot fixture, string beadId, CancellationToken token) =>
        ReadCommentTexts(await fixture.ReadBeadCommentsAsync(beadId, token));

    private static string[] ReadLabels(JsonElement bead) => bead.GetProperty("labels").EnumerateArray()
        .Select(label => label.GetString()!).ToArray();

    private static void AssertBeadWorkflowUnchanged(JsonElement before, JsonElement after,
        IReadOnlyList<string> beforeLabels, string recoveryActor)
    {
        Assert.Equal("in_progress", after.GetProperty("status").GetString());
        Assert.Equal(recoveryActor, after.GetProperty("assignee").GetString());
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("assignee").GetString(), after.GetProperty("assignee").GetString());
        Assert.Equal(beforeLabels, ReadLabels(after));
    }

    private static string[][] ReadGhCommands(string path) => File.ReadAllLines(path)
        .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();

    private static void AssertFreshRecoveryRequests(IReadOnlyList<string[]> commands)
    {
        Assert.Contains(commands, command => command.SequenceEqual(
            ["pr", "view", AgentStageTargetFixture.FixturePullRequestUrl, "--json", "state,baseRefName,headRefName,headRefOid,url"],
            StringComparer.Ordinal));
        Assert.Contains(commands, command => command.SequenceEqual(
            ["pr", "checks", AgentStageTargetFixture.FixturePullRequestUrl, "--required", "--json", "name,state,bucket"],
            StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command => command.Length > 1 && command[0] == "pr" && command[1] == "merge");
        Assert.DoesNotContain(commands, command => command.Contains("--watch", StringComparer.Ordinal));
    }
}
