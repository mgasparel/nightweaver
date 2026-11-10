using System.Diagnostics;
using System.Text.Json;
using Nightweaver.Configuration;
using ManualPilot = Nightweaver.Tests.ManualDeliveryPilotFixture.ManualDeliveryPilotFixture;
using ManualPilotMergerChange = Nightweaver.Tests.AgentInvocationFixture.ManualPilotMergerChange;

namespace Nightweaver.Tests;

public sealed class ManualPilotMergerRetryTests
{
    [Theory]
    [InlineData("merge", "rebase-merge")]
    [InlineData("apply", "rebase-apply")]
    public void InterruptedRebaseIsPreservedAndRejectedBeforeAnotherFetchOrRebase(string backend, string stateDirectory)
    {
        WithGitRepository(repository =>
        {
            RunGit(repository, ["checkout", "-b", "nightweaver/qa-interrupted"]);
            File.WriteAllText(Path.Combine(repository, "README.md"), "task change\n");
            RunGit(repository, ["commit", "-am", "task change"]);
            RunGit(repository, ["checkout", "main"]);
            File.WriteAllText(Path.Combine(repository, "README.md"), "default change\n");
            RunGit(repository, ["commit", "-am", "default change"]);
            RunGit(repository, ["checkout", "nightweaver/qa-interrupted"]);
            Assert.Throws<InvalidOperationException>(() => RunGit(repository, ["-c", $"rebase.backend={backend}", "rebase", "main"]));
            var rebasePath = Path.Combine(repository, ".git", stateDirectory);
            Assert.True(Directory.Exists(rebasePath));
            File.WriteAllText(Path.Combine(repository, "README.md"), "human resolution in progress\n");
            var head = RunGit(repository, ["rev-parse", "HEAD"]);
            var status = RunGit(repository, ["status", "--porcelain"]);
            var state = Directory.GetFiles(rebasePath)
                .Select(path => (Name: Path.GetFileName(path), Content: File.ReadAllText(path)))
                .OrderBy(file => file.Name).ToArray();
            var commands = new List<string[]>();

            var failure = Assert.Throws<InvalidOperationException>(() =>
                ManualPilotMergerChange.SynchronizeWithDefaultBranch(repository, "main", (path, arguments) =>
                {
                    commands.Add(arguments.ToArray());
                    return RunGit(path, arguments);
                }));

            Assert.Contains("unfinished rebase", failure.Message, StringComparison.Ordinal);
            Assert.All(commands, command => Assert.Equal("rev-parse", command[0]));
            Assert.Equal(head, RunGit(repository, ["rev-parse", "HEAD"]));
            Assert.Equal(status, RunGit(repository, ["status", "--porcelain"]));
            Assert.Equal("human resolution in progress\n", File.ReadAllText(Path.Combine(repository, "README.md")));
            Assert.Equal(state, Directory.GetFiles(rebasePath)
                .Select(path => (Name: Path.GetFileName(path), Content: File.ReadAllText(path)))
                .OrderBy(file => file.Name).ToArray());
        });
    }

    private const string ControlledInterruption =
        "Controlled one-shot interruption after expected commit and branch push, before PR checks.";

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
