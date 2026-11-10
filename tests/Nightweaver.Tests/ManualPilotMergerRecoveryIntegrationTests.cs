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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlreadyMergedRecoveryRequiresFreshChecksWithoutRecreatingTheBranch(bool checksPass)
    {
        await using var fixture = await ManualPilot.CreateAsync(seedProfile: ManualPilotSeedProfile.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        var token = TestContext.Current.CancellationToken;
        var paths = new HostPaths(fixture.NightweaverRoot);
        var project = ProjectConfigLoader.Load(fixture.ConfigPath, ProjectSlug.Parse("manualqa"), paths);
        var bead = await fixture.CreateMergeGateTaskAsync("Recover already merged native fixture", token);
        var environment = new Dictionary<string, string?>(fixture.CreateEnvironment(), StringComparer.OrdinalIgnoreCase)
        {
            ["BD_ACTOR"] = project.RecoveryActor,
            ["NIGHTWEAVER_FIXTURE_CI_RESULT"] = checksPass ? "success" : "fail",
            ["NIGHTWEAVER_FIXTURE_GH_LOG"] = Path.Combine(fixture.Root, "recovery-gh.jsonl")
        };
        var system = new SystemProcessRunner();
        var runner = new RecordingProcessRunner((request, cancellation) =>
        {
            var merged = new Dictionary<string, string?>(request.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in environment) merged[pair.Key] = pair.Value;
            return system.RunAsync(request with { Environment = merged }, cancellation);
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
            var invocation = await new AgentInvocationPreparer().PrepareAsync(paths, project, AgentRole.Merger,
                new AgentDispatchContext(bead, "task", "stage:merge", 0), worktree.Path, token);
            var result = await AgentInvocationExecutor.ExecuteAsync(paths, project.Name, invocation,
                new AgentStageTargetFixture.FixtureEnvironmentOverlayProcessLauncher(environment), token);
            Assert.Equal(AgentExecutionStatus.Exited, result.Status);
            if (checksPass)
                Assert.True(result.ExitCode == 0, $"{result.ExitCode}: {File.ReadAllText(invocation.LogFilePath)}");
            else Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(await Git("ls-remote", "origin", $"refs/heads/nightweaver/{bead}"));
            Assert.Equal(expected.HeadSha, (await fixture.ReadOriginRefAsync("refs/heads/main", token)).Trim());
            var comments = await fixture.ReadBeadCommentsAsync(bead, token);
            if (checksPass)
                Assert.Contains($"checked_head={expected.HeadSha} required_checks=passed merge_method=squash merge_commit={expected.HeadSha}", comments, StringComparison.Ordinal);
            else Assert.DoesNotContain("nightweaver-merge-v1", comments, StringComparison.Ordinal);
            var state = await fixture.ReadBeadAsync(bead, token);
            Assert.Equal("in_progress", state.GetProperty("status").GetString());
            Assert.Equal(project.RecoveryActor, state.GetProperty("assignee").GetString());
            var commands = File.ReadAllLines(environment["NIGHTWEAVER_FIXTURE_GH_LOG"]!)
                .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            Assert.NotEmpty(commands);
            Assert.DoesNotContain(commands, command => command.Length > 1 && command[0] == "pr" && command[1] == "merge");
            Assert.DoesNotContain(commands, command => command.Contains("--watch", StringComparer.Ordinal));
        }
        catch
        {
            fixture.PreserveForFailure();
            TestContext.Current.TestOutputHelper?.WriteLine($"Already-merged native fixture preserved at {fixture.Root}");
            throw;
        }
    }
}
