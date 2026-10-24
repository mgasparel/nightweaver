using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class TargetRepositoryValidatorTests
{
    private static readonly string[] ValidBranchNames = ["main", "release/1.0", "feature/x"];
    private static readonly string[] InvalidBranchNames = ["bad..branch", "HEAD", "-x", "a.lock", "a b"];

    private static readonly string[] RevParseArguments =
    [
        "rev-parse", "--is-inside-work-tree", "--show-prefix", "--path-format=absolute", "--git-dir", "--git-common-dir"
    ];

    [Fact]
    public async Task ValidatorRejectsMissingAndNonRepositoryPaths()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "missing");
        var missingException = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(missing));
        Assert.Contains("does not exist or is not a directory", missingException.Message, StringComparison.Ordinal);

        var plain = temp.CreateDirectory("plain directory");
        var nonRepositoryException = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(plain));
        Assert.Contains("is not a Git working tree", nonRepositoryException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatorRequiresTheRepositoryRoot()
    {
        using var fixture = new GitFixture();
        var child = Directory.CreateDirectory(Path.Combine(fixture.Path, "subdirectory")).FullName;

        var exception = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(child));

        Assert.Contains("must be the root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("subdirectory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatorRejectsLinkedAndBareWorktrees()
    {
        using var fixture = new GitFixture();
        fixture.CommitEmpty();
        var linked = Path.Combine(Directory.GetParent(fixture.Path)!.FullName, "linked worktree");
        GitFixture.RunGit(fixture.Path, ["worktree", "add", "-b", "linked", linked]);

        var linkedException = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(linked));
        Assert.Contains("linked Git worktree", linkedException.Message, StringComparison.Ordinal);

        using var temp = new TempDirectory();
        var bare = temp.CreateDirectory("bare repository");
        GitFixture.RunGit(bare, ["init", "--bare"]);
        var bareException = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(bare));
        Assert.Contains("is not a Git working tree", bareException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatorRequiresInitializedBeadsMetadataAndRejectsRedirects()
    {
        using var fixture = new GitFixture();
        Directory.Delete(Path.Combine(fixture.Path, ".beads"), recursive: true);
        var missingDirectory = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(fixture.Path));
        Assert.Contains("missing .beads directory", missingDirectory.Message, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(fixture.Path, ".beads"));
        var missingMetadata = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(fixture.Path));
        Assert.Contains("missing .beads/metadata.json", missingMetadata.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(fixture.Path, ".beads", "redirect"), "../another-tracker");
        var redirected = await Assert.ThrowsAsync<TargetRepositoryException>(() => Validate(fixture.Path));
        Assert.Contains("redirected bd trackers are not supported", redirected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatorChecksBranchFormatThenAcceptsAMainRepositoryPathWithSpaces()
    {
        using var fixture = new GitFixture("repo with spaces");
        var runner = new RecordingProcessRunner((request, token) => new SystemProcessRunner().RunAsync(request, token));

        var result = await new TargetRepositoryValidator(runner).ValidateAsync(
            new TargetRepositoryConfig(fixture.Path, "main"),
            Path.Combine(fixture.Path, "project.json"),
            CancellationToken.None);

        Assert.Equal(fixture.Path, result.Path);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Equal(2, runner.Requests.Count);
        var branchRequest = runner.Requests[0];
        Assert.Equal("git", branchRequest.FileName);
        Assert.Equal(["check-ref-format", "--branch", "main"], branchRequest.Arguments);
        Assert.Equal(Path.GetTempPath(), branchRequest.WorkingDirectory);
        Assert.Equal("0", branchRequest.Environment["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("0", branchRequest.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Null(branchRequest.Environment["GIT_DIR"]);
        Assert.Equal(RevParseArguments, runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task ValidatorUsesGitToAcceptValidAndRejectInvalidBranchNames()
    {
        using var fixture = new GitFixture();
        var runner = new RecordingProcessRunner((request, token) => new SystemProcessRunner().RunAsync(request, token));
        var configPath = Path.Combine(fixture.Path, "project.json");
        var validator = new TargetRepositoryValidator(runner);

        foreach (var branch in ValidBranchNames)
        {
            var result = await validator.ValidateAsync(
                new TargetRepositoryConfig(fixture.Path, branch),
                configPath,
                CancellationToken.None);
            Assert.Equal(branch, result.DefaultBranch);
        }

        foreach (var branch in InvalidBranchNames)
        {
            var requestCount = runner.Requests.Count;
            var exception = await Assert.ThrowsAsync<ProjectConfigurationException>(() =>
                validator.ValidateAsync(new TargetRepositoryConfig(fixture.Path, branch), configPath, CancellationToken.None));

            Assert.Equal("$.target_repository.default_branch", exception.JsonPath);
            Assert.Equal(configPath, exception.ConfigFilePath);
            Assert.Equal($"'{branch}' is not a valid Git branch name", exception.Message);
            Assert.Single(runner.Requests.Skip(requestCount));
            Assert.Equal(["check-ref-format", "--branch", branch], runner.Requests[^1].Arguments);
        }
    }

    [Fact]
    public async Task ValidatorRejectsUnexpectedGitOutputShapeAsAToolError()
    {
        using var fixture = new GitFixture();
        var runner = new RecordingProcessRunner((_, _) => Task.FromResult(new ProcessResult(0, "true\n/path\n/path\n", string.Empty)));

        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() =>
            new TargetRepositoryValidator(runner).ValidateAsync(
                new TargetRepositoryConfig(fixture.Path, "main"),
                Path.Combine(fixture.Path, "project.json"),
                CancellationToken.None));

        Assert.Equal("unexpected git rev-parse output", exception.Message);
    }

    private static Task<ValidatedTargetRepository> Validate(string path) =>
        new TargetRepositoryValidator(new SystemProcessRunner())
            .ValidateAsync(new TargetRepositoryConfig(path, "main"), Path.Combine(path, "project.json"), CancellationToken.None);
}
