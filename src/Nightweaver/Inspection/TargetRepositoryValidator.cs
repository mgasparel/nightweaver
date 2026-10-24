using Nightweaver.Configuration;

namespace Nightweaver.Inspection;

internal sealed class TargetRepositoryValidator(IProcessRunner processRunner)
{
    internal async Task<ValidatedTargetRepository> ValidateAsync(
        TargetRepositoryConfig targetRepository,
        string configFilePath,
        CancellationToken cancellationToken)
    {
        var path = targetRepository.Path;
        var branchCheck = GitCommand.Create(
            ["check-ref-format", "--branch", targetRepository.DefaultBranch],
            Path.GetTempPath());
        var branchResult = await processRunner.RunAsync(branchCheck, cancellationToken);
        if (branchResult.ExitCode != 0)
        {
            throw new ProjectConfigurationException(
                $"'{targetRepository.DefaultBranch}' is not a valid Git branch name",
                "$.target_repository.default_branch",
                configFilePath);
        }

        if (!Directory.Exists(path))
        {
            throw new TargetRepositoryException($"Target Repository path does not exist or is not a directory: {path}");
        }

        var request = GitCommand.Create(
            ["rev-parse", "--is-inside-work-tree", "--show-prefix", "--path-format=absolute", "--git-dir", "--git-common-dir"],
            path);
        var result = await processRunner.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? string.Empty
                : Environment.NewLine + result.StandardError.TrimEnd();
            throw new TargetRepositoryException($"Target Repository is not a Git working tree: {path}{detail}");
        }

        var lines = SplitLinesPreservingEmpty(result.StandardOutput);
        if (lines.Length != 4)
        {
            throw new ToolExecutionException("unexpected git rev-parse output", result.StandardError);
        }

        if (!string.Equals(lines[0], "true", StringComparison.Ordinal))
        {
            throw new TargetRepositoryException($"Target Repository is not a Git working tree: {path}");
        }

        if (lines[1].Length != 0)
        {
            throw new TargetRepositoryException(
                $"Target Repository path must be the root of its Git working tree; '{path}' is inside it at '{lines[1]}'");
        }

        var gitDirectory = Path.GetFullPath(lines[2], path);
        var commonDirectory = Path.GetFullPath(lines[3], path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(gitDirectory, commonDirectory, comparison))
        {
            throw new TargetRepositoryException(
                $"Target Repository path is a linked Git worktree; configure the main working tree instead (SPEC 5.3.2): {path}");
        }

        var beadsDirectory = Path.Combine(path, ".beads");
        if (!Directory.Exists(beadsDirectory))
        {
            throw new TargetRepositoryException(
                $"Target Repository has no initialized bd tracker (missing .beads directory). Run 'bd init' in the Target Repository; Nightweaver never initializes trackers: {path}");
        }

        var metadataPath = Path.Combine(beadsDirectory, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            var redirectPath = Path.Combine(beadsDirectory, "redirect");
            var detail = File.Exists(redirectPath) || Directory.Exists(redirectPath)
                ? "redirected bd trackers are not supported"
                : "missing .beads/metadata.json";
            throw new TargetRepositoryException(
                $"Target Repository has no initialized bd tracker ({detail}). Run 'bd init' in the Target Repository; Nightweaver never initializes trackers: {path}");
        }

        return new ValidatedTargetRepository(path, targetRepository.DefaultBranch);
    }

    private static string[] SplitLinesPreservingEmpty(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }
}
