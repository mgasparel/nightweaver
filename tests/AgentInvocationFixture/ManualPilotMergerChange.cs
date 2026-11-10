using System.Text;

namespace Nightweaver.Tests.AgentInvocationFixture;

public sealed record ManualPilotMergerCommit(string HeadSha, bool Reused);

/// <summary>Validates the deterministic manual-pilot Merger change before creating or reusing it.</summary>
public static class ManualPilotMergerChange
{
    public static void SynchronizeWithDefaultBranch(string worktreePath, string defaultBranch,
        Func<string, IReadOnlyList<string>, string> runGit)
    {
        foreach (var state in new[] { "rebase-merge", "rebase-apply" })
        {
            var path = runGit(worktreePath, ["rev-parse", "--git-path", state]).Trim();
            var fullPath = Path.GetFullPath(path, worktreePath);
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
                throw new InvalidOperationException(
                    $"Manual-pilot Merger found an unfinished rebase at '{fullPath}'; preserve it for inspection before retrying.");
        }

        runGit(worktreePath, ["fetch", "origin", defaultBranch]);
        runGit(worktreePath, ["rebase", $"origin/{defaultBranch}"]);
    }

    public static ManualPilotMergerCommit EnsureExpectedCommit(
        string beadId,
        string branch,
        string worktreePath,
        Func<string, IReadOnlyList<string>, string> runGit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(runGit);

        var expectedBranch = $"nightweaver/{beadId}";
        if (!string.Equals(branch, expectedBranch, StringComparison.Ordinal))
            throw new InvalidOperationException($"Manual-pilot Merger expected owned branch '{expectedBranch}', observed '{branch}'.");

        var fullWorktreePath = Path.GetFullPath(worktreePath);
        var relativeFile = $"merger-verified-{beadId}.txt";
        var expectedContent = $"fixture merge after synchronized current-head checks for {beadId}\n";
        var filePath = Path.Combine(fullWorktreePath, relativeFile);
        var status = runGit(fullWorktreePath, ["status", "--porcelain=v1", "--untracked-files=all"]);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException($"Manual-pilot Merger refused to create or reuse its expected change because the owned worktree is dirty: {status.TrimEnd()}.");

        var reused = File.Exists(filePath);
        if (!reused)
        {
            File.WriteAllText(filePath, expectedContent, new UTF8Encoding(false));
            runGit(fullWorktreePath, ["add", "--", relativeFile]);
            // Keep real commit failures visible. A retry must never turn an arbitrary failure into success.
            runGit(fullWorktreePath, ["commit", "-m", "fixture verified Merger change"]);
        }
        else
        {
            var workingContent = File.ReadAllText(filePath, Encoding.UTF8);
            if (!string.Equals(workingContent, expectedContent, StringComparison.Ordinal))
                throw new InvalidOperationException($"Manual-pilot Merger found unexpected content in '{relativeFile}'.");
        }

        ValidateCommittedChange(fullWorktreePath, relativeFile, expectedContent, runGit);
        var finalStatus = runGit(fullWorktreePath, ["status", "--porcelain=v1", "--untracked-files=all"]);
        if (!string.IsNullOrWhiteSpace(finalStatus))
            throw new InvalidOperationException($"Manual-pilot Merger expected a clean owned worktree after validating '{relativeFile}', observed: {finalStatus.TrimEnd()}.");

        return new(runGit(fullWorktreePath, ["rev-parse", "HEAD"]).Trim(), reused);
    }

    private static void ValidateCommittedChange(
        string worktreePath,
        string relativeFile,
        string expectedContent,
        Func<string, IReadOnlyList<string>, string> runGit)
    {
        var committedContent = runGit(worktreePath, ["show", $"HEAD:{relativeFile}"]);
        if (!string.Equals(committedContent, expectedContent, StringComparison.Ordinal))
            throw new InvalidOperationException($"Manual-pilot Merger HEAD does not contain the exact expected content for '{relativeFile}'.");

        var changedPaths = runGit(worktreePath, ["diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD"])
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (changedPaths.Length != 1 || !string.Equals(changedPaths[0], relativeFile, StringComparison.Ordinal))
            throw new InvalidOperationException($"Manual-pilot Merger HEAD must change only '{relativeFile}', observed: {string.Join(", ", changedPaths)}.");
    }
}
