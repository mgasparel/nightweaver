using System.Diagnostics;
using System.Text.Json;
using Nightweaver.Configuration;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;
using ManualPilotMergerChange = Nightweaver.Tests.AgentInvocationFixture.ManualPilotMergerChange;

namespace Nightweaver.Tests;

public sealed class ManualPilotMergerRetryTests
{
    private const string ControlledInterruption =
        "Controlled one-shot interruption after expected commit and branch push, before PR checks.";

    [Fact]
    public async Task RealRuntimeRetryReusesPushedCommitThenChecksMergesAndPublishes()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var fixture = await ManualPilot.CreateAsync(
            agentInvocationFixturePath: typeof(Nightweaver.Tests.AgentInvocationFixture.FixtureMarker).Assembly.Location,
            cancellationToken: TestContext.Current.CancellationToken,
            seedProfile: Nightweaver.Tests.ManualDeliveryPilotFixture.ManualPilotSeedProfile.Empty);
        var beadId = await fixture.CreateMergeGateTaskAsync("QA manual pilot retry after pushed Merger commit",
            TestContext.Current.CancellationToken);
        fixture.MergerInterruptBeadId = beadId;
        fixture.MergerRetryGateBeadId = beadId;
        fixture.MergerDiagnosticsBeadId = beadId;
        var initialMain = (await fixture.ReadOriginRefAsync("refs/heads/main", TestContext.Current.CancellationToken)).Trim();
        var runtime = fixture.StartRuntime(typeof(ProjectConfigLoader).Assembly.Location);
        Exception? primaryFailure = null;
        try
        {
            await fixture.WaitForMergerRetryGateAsync(beadId, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var beforeRetryRelease = ReadDiagnostics(fixture.MergerDiagnosticsPath(beadId), beadId);
            var created = Assert.Single(beforeRetryRelease, row =>
                row.GetProperty("phase").GetString() == "expected-commit-created"
                && row.GetProperty("status").GetString() == "completed");
            var reused = Assert.Single(beforeRetryRelease, row =>
                row.GetProperty("phase").GetString() == "expected-commit-reused"
                && row.GetProperty("status").GetString() == "completed");
            var createdHead = created.GetProperty("current_head").GetString();
            Assert.False(string.IsNullOrWhiteSpace(createdHead));
            Assert.Equal(createdHead, reused.GetProperty("current_head").GetString());
            Assert.NotEqual(created.GetProperty("invocation_identity").GetString(),
                reused.GetProperty("invocation_identity").GetString());

            var interrupted = Assert.Single(beforeRetryRelease, row =>
                row.GetProperty("phase").GetString() == "controlled-post-push-interruption"
                && row.GetProperty("status").GetString() == "failed");
            Assert.Contains(ControlledInterruption, interrupted.GetProperty("failure").GetString(), StringComparison.Ordinal);
            Assert.Equal(created.GetProperty("invocation_identity").GetString(),
                interrupted.GetProperty("invocation_identity").GetString());

            Assert.Equal(initialMain,
                (await fixture.ReadOriginRefAsync("refs/heads/main", TestContext.Current.CancellationToken)).Trim());
            Assert.Equal(createdHead,
                (await fixture.ReadOriginRefAsync($"refs/heads/nightweaver/{beadId}", TestContext.Current.CancellationToken)).Trim());
            using (var pullRequest = JsonDocument.Parse(await fixture.ReadPullRequestAsync(beadId,
                       TestContext.Current.CancellationToken)))
            {
                Assert.Equal("OPEN", pullRequest.RootElement.GetProperty("state").GetString());
                Assert.Equal(createdHead, pullRequest.RootElement.GetProperty("headRefOid").GetString());
            }

            var heldBead = await fixture.ReadBeadAsync(beadId, TestContext.Current.CancellationToken);
            Assert.Equal("in_progress", heldBead.GetProperty("status").GetString());
            Assert.Contains("stage:merge", heldBead.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            Assert.DoesNotContain(await ReadCommentTextsAsync(fixture, beadId),
                comment => comment.Contains("nightweaver-merge-v1", StringComparison.Ordinal));
            Assert.DoesNotContain(beforeRetryRelease, row =>
                row.GetProperty("phase").GetString() == "run-current-head-required-checks"
                && row.GetProperty("status").GetString() == "completed");

            fixture.ReleaseMergerRetryGate(beadId);
            var closed = await WaitForClosedAsync(fixture, runtime, beadId, TimeSpan.FromMinutes(3),
                TestContext.Current.CancellationToken);
            Assert.Equal("closed", closed.GetProperty("status").GetString());

            var expectedFile = $"merger-verified-{beadId}.txt";
            Assert.Equal($"fixture merge after synchronized current-head checks for {beadId}\n",
                await fixture.ReadRemoteMainFileAsync(expectedFile, TestContext.Current.CancellationToken));
            var history = (await fixture.ReadOriginFileCommitHistoryAsync(expectedFile,
                TestContext.Current.CancellationToken)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[] { createdHead }, history);

            var afterRetry = ReadDiagnostics(fixture.MergerDiagnosticsPath(beadId), beadId);
            Assert.Contains(afterRetry, row => row.GetProperty("phase").GetString() == "run-current-head-required-checks"
                && row.GetProperty("status").GetString() == "completed"
                && row.GetProperty("current_head").GetString() == createdHead
                && row.GetProperty("invocation_identity").GetString() == reused.GetProperty("invocation_identity").GetString());
            Assert.Contains(afterRetry, row => row.GetProperty("phase").GetString() == "verify-merged-pull-request"
                && row.GetProperty("status").GetString() == "completed");
            Assert.Contains(afterRetry, row => row.GetProperty("phase").GetString() == "publish-verified-merge-evidence"
                && row.GetProperty("status").GetString() == "completed");
            Assert.Contains(await ReadCommentTextsAsync(fixture, beadId),
                comment => comment.Contains($"nightweaver-merge-v1 pull_request=https://github.com/nightweaver-fixture/target/pull/123 checked_head={createdHead} required_checks=passed merge_method=squash merge_commit={createdHead}",
                    StringComparison.Ordinal));

            var invocationLogs = Directory.GetFiles(Path.Combine(fixture.NightweaverRoot, "logs", "manualqa", beadId), "*.log");
            Assert.Contains(invocationLogs, path => File.ReadAllText(path).Contains(ControlledInterruption, StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            fixture.PreserveForFailure();
            primaryFailure = exception;
            throw;
        }
        finally
        {
            fixture.ReleaseMergerRetryGate(beadId);
            fixture.ReleaseMergerGate(beadId);
            fixture.ReleaseGate(fixture.ParentlessTaskId);
            try { await fixture.StopRuntimeAsync(force: false, TimeSpan.FromMinutes(2)); }
            catch (Exception cleanupException) when (primaryFailure is not null)
            {
                Console.Error.WriteLine($"Merger retry cleanup after test failure also failed: {cleanupException}");
            }
        }
    }

    [Fact]
    public void ExpectedCommitReuseRequiresExactOwnedBranchCleanContentAndHeadScope()
    {
        WithGitRepository(repository =>
        {
            var beadId = "qa-safe-reuse";
            var expectedBranch = $"nightweaver/{beadId}";
            RunGit(repository, ["checkout", "-b", expectedBranch]);
            var first = ManualPilotMergerChange.EnsureExpectedCommit(beadId, expectedBranch, repository, RunGit);
            var second = ManualPilotMergerChange.EnsureExpectedCommit(beadId, expectedBranch, repository, RunGit);
            Assert.False(first.Reused);
            Assert.True(second.Reused);
            Assert.Equal(first.HeadSha, second.HeadSha);
            Assert.Equal(first.HeadSha, RunGit(repository, ["rev-parse", "HEAD"]).Trim());
            Assert.Equal($"fixture merge after synchronized current-head checks for {beadId}\n",
                RunGit(repository, ["show", $"HEAD:merger-verified-{beadId}.txt"]));
            Assert.Equal(string.Empty, RunGit(repository, ["status", "--porcelain=v1", "--untracked-files=all"]));
        });
    }

    [Fact]
    public void ExpectedCommitRejectsWrongBranchDirtyWorktreeAndUnexpectedCommittedState()
    {
        WithGitRepository(repository =>
        {
            var beadId = "qa-wrong-branch";
            Assert.Contains("expected owned branch", Assert.Throws<InvalidOperationException>(() =>
                ManualPilotMergerChange.EnsureExpectedCommit(beadId, "other/branch", repository, RunGit)).Message,
                StringComparison.Ordinal);
        });

        WithGitRepository(repository =>
        {
            var beadId = "qa-dirty-worktree";
            var branch = $"nightweaver/{beadId}";
            RunGit(repository, ["checkout", "-b", branch]);
            File.WriteAllText(Path.Combine(repository, "unrelated.txt"), "unrelated\n");
            Assert.Contains("worktree is dirty", Assert.Throws<InvalidOperationException>(() =>
                ManualPilotMergerChange.EnsureExpectedCommit(beadId, branch, repository, RunGit)).Message,
                StringComparison.Ordinal);
        });

        WithGitRepository(repository =>
        {
            var beadId = "qa-unexpected-content";
            var branch = $"nightweaver/{beadId}";
            RunGit(repository, ["checkout", "-b", branch]);
            File.WriteAllText(Path.Combine(repository, $"merger-verified-{beadId}.txt"), "different committed content\n");
            RunGit(repository, ["add", "--", $"merger-verified-{beadId}.txt"]);
            RunGit(repository, ["commit", "-m", "untrusted fixture content"]);
            Assert.Contains("unexpected content", Assert.Throws<InvalidOperationException>(() =>
                ManualPilotMergerChange.EnsureExpectedCommit(beadId, branch, repository, RunGit)).Message,
                StringComparison.Ordinal);
        });

        WithGitRepository(repository =>
        {
            var beadId = "qa-unexpected-head";
            var branch = $"nightweaver/{beadId}";
            RunGit(repository, ["checkout", "-b", branch]);
            ManualPilotMergerChange.EnsureExpectedCommit(beadId, branch, repository, RunGit);
            File.WriteAllText(Path.Combine(repository, "unexpected-history.txt"), "unexpected\n");
            RunGit(repository, ["add", "--", "unexpected-history.txt"]);
            RunGit(repository, ["commit", "-m", "unrelated later commit"]);
            Assert.Contains("must change only", Assert.Throws<InvalidOperationException>(() =>
                ManualPilotMergerChange.EnsureExpectedCommit(beadId, branch, repository, RunGit)).Message,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void GenuineCommitFailureIsNotSwallowedOrConvertedIntoSuccessfulReuse()
    {
        WithGitRepository(repository =>
        {
            var beadId = "qa-commit-failure";
            var branch = $"nightweaver/{beadId}";
            RunGit(repository, ["checkout", "-b", branch]);
            RunGit(repository, ["config", "commit.gpgsign", "true"]);
            RunGit(repository, ["config", "gpg.program", "nightweaver-missing-gpg-for-controlled-commit-failure"]);
            var originalHead = RunGit(repository, ["rev-parse", "HEAD"]).Trim();
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ManualPilotMergerChange.EnsureExpectedCommit(beadId, branch, repository, RunGit));
            Assert.Contains("git commit", exception.Message, StringComparison.Ordinal);
            Assert.Contains("exited", exception.Message, StringComparison.Ordinal);
            Assert.Equal(originalHead, RunGit(repository, ["rev-parse", "HEAD"]).Trim());
            Assert.Contains($"merger-verified-{beadId}.txt", RunGit(repository, ["status", "--porcelain=v1"]),
                StringComparison.Ordinal);
        });
    }

    private static JsonElement[] ReadDiagnostics(string path, string beadId) =>
        File.ReadAllLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(row => row.GetProperty("bead_id").GetString() == beadId).ToArray();

    private static async Task<string[]> ReadCommentTextsAsync(ManualPilot fixture, string beadId)
    {
        using var document = JsonDocument.Parse(await fixture.ReadBeadCommentsAsync(beadId, TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var comments = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };
        return comments.Select(comment =>
                comment.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty)
            .ToArray();
    }

    private static async Task<JsonElement> WaitForClosedAsync(ManualPilot fixture, WindowsConsoleProcess runtime,
        string beadId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bead = await fixture.ReadBeadAsync(beadId, cancellationToken);
            if (bead.GetProperty("status").GetString() == "closed") return bead;
            if (runtime.HasExited)
                throw new InvalidOperationException($"Runtime exited ({runtime.ExitCode}) before Merger task '{beadId}' closed: {fixture.ReadRuntimeLog()}");
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException($"Merger task '{beadId}' did not close within {timeout}: {fixture.ReadRuntimeLog()}");
    }

    private static void WithGitRepository(Action<string> action)
    {
        var repository = Path.Combine(Path.GetTempPath(), "nightweaver-merger-safety-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repository);
            RunGit(repository, ["init", "-b", "main"]);
            RunGit(repository, ["config", "user.name", "Nightweaver Fixture Test"]);
            RunGit(repository, ["config", "user.email", "fixture@example.invalid"]);
            File.WriteAllText(Path.Combine(repository, "README.md"), "seed\n");
            RunGit(repository, ["add", "--", "README.md"]);
            RunGit(repository, ["commit", "-m", "seed"]);
            action(repository);
        }
        finally
        {
            if (Directory.Exists(repository))
            {
                foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                foreach (var directory in Directory.EnumerateDirectories(repository, "*", SearchOption.AllDirectories))
                    File.SetAttributes(directory, FileAttributes.Normal);
                Directory.Delete(repository, recursive: true);
            }
        }
    }

    private static string RunGit(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_AUTHOR_NAME"] = "Nightweaver Fixture Test";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "Nightweaver Fixture Test";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid";
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git for Merger safety test.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} exited {process.ExitCode}: {stderr}{stdout}");
        return stdout;
    }
}
