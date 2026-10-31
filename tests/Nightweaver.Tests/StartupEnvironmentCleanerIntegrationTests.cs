using Nightweaver.Application;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class StartupEnvironmentCleanerIntegrationTests
{
    [Theory]
    [InlineData("tracker-read")]
    [InlineData("shared-root")]
    public async Task LateRequiredTmpValidationFailuresAbortStartupInsteadOfEscalatingLocally(string failure)
    {
        using var temp = new TempDirectory();
        using var target = new BeadsFixture("target repository");
        var paths = CreatePaths(temp.Path);
        var project = new ProjectSlug("demo");
        var beadId = target.Create("Completed Delivery with orphan tmp", status: "closed", type: "task");
        var tmp = Directory.CreateDirectory(Path.Combine(paths.TmpRoot(project), beadId));
        File.WriteAllText(Path.Combine(tmp.FullName, "sentinel"), "retain when required validation fails");
        var worktreesRoot = paths.WorktreesRoot(project);
        var runner = new StartupCleanupRunner(target, paths, project, failure, null, null);
        var repository = Repository(target);
        var cleaner = Cleaner(runner);

        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => cleaner.CleanAsync(
            paths, project, repository, "nightweaver:demo", CancellationToken.None));

        Assert.True(runner.Injected, "The fault must occur at the final required cleanup validation read.");
        Assert.Contains(failure == "tracker-read" ? "fixture late tracker read failure" : "runtime directory inspection",
            exception.Message + exception.StandardError, StringComparison.Ordinal);
        Assert.True(Directory.Exists(tmp.FullName));
        Assert.Equal("closed", ReadBead(target, beadId).Status);
        Assert.DoesNotContain("needs-human", ReadBead(target, beadId).Labels);
        if (failure == "shared-root")
        {
            Assert.True(File.Exists(worktreesRoot));
            Assert.False(Directory.Exists(worktreesRoot));
        }
    }

    [Fact]
    public async Task PauseAddedDuringFinalGitIdentityReadRetainsWorktreeAndTmp()
    {
        using var temp = new TempDirectory();
        using var target = new BeadsFixture("target repository");
        var paths = CreatePaths(temp.Path);
        var project = new ProjectSlug("demo");
        var beadId = target.Create("Closed Delivery paused during validation", status: "closed", type: "task");
        var worktree = Path.Combine(paths.WorktreesRoot(project), beadId);
        target.Run("git", ["worktree", "add", "-b", "nightweaver/" + beadId, worktree, "HEAD"]);
        var tmp = Directory.CreateDirectory(Path.Combine(paths.TmpRoot(project), beadId));
        File.WriteAllText(Path.Combine(tmp.FullName, "sentinel"), "retain after late pause");
        var runner = new StartupCleanupRunner(target, paths, project, null, worktree, beadId);
        var result = await Cleaner(runner).CleanAsync(paths, project, Repository(target), "nightweaver:demo", CancellationToken.None);

        Assert.True(runner.PauseInjected,
            $"The pause must be added during the final Git identity read. Observed: {string.Join(" | ", runner.GitIdentityRequests)}");
        Assert.True(Directory.Exists(worktree));
        Assert.True(Directory.Exists(tmp.FullName));
        var bead = ReadBead(target, beadId);
        Assert.Equal("closed", bead.Status);
        Assert.Contains("needs-human", bead.Labels);
        Assert.Contains(result, diagnostic => diagnostic.BeadId == beadId
            && diagnostic.Reason.Contains("changed during final Git identity validation", StringComparison.Ordinal));
    }

    private static HostPaths CreatePaths(string root)
    {
        var paths = new HostPaths(Path.Combine(root, "host"));
        var project = new ProjectSlug("demo");
        Directory.CreateDirectory(paths.WorktreesRoot(project));
        Directory.CreateDirectory(paths.TmpRoot(project));
        return paths;
    }

    private static ValidatedTargetRepository Repository(BeadsFixture target) =>
        new(target.Path, "main") { GitCommonDirectory = Path.Combine(target.Path, ".git") };

    private static StartupEnvironmentCleaner Cleaner(IProcessRunner runner) =>
        new(new BeadsReader(runner), new BeadsWriter(runner), runner);

    private static BeadSnapshot ReadBead(BeadsFixture target, string id) =>
        BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments)).ById[id];

    private sealed class StartupCleanupRunner(
        BeadsFixture target,
        HostPaths paths,
        ProjectSlug project,
        string? failure,
        string? pauseDuringGitPath,
        string? pauseBeadId) : IProcessRunner
    {
        private readonly SystemProcessRunner system = new();
        private int inventoryReads;

        internal bool Injected { get; private set; }
        internal bool PauseInjected { get; private set; }
        internal List<string> GitIdentityRequests { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            var isInventory = request.FileName == "bd"
                && request.Arguments.SequenceEqual(BeadsCommand.Create(target.Path).Arguments);
            if (isInventory)
            {
                inventoryReads++;
                if (failure == "tracker-read" && inventoryReads == 4)
                {
                    Injected = true;
                    return new ProcessResult(73, string.Empty, "fixture late tracker read failure");
                }

                var result = await RunRealAsync(request, cancellationToken).ConfigureAwait(false);
                if (failure == "shared-root" && inventoryReads == 4)
                {
                    var root = paths.WorktreesRoot(project);
                    Directory.Delete(root, recursive: true);
                    File.WriteAllText(root, "shared root replaced during final validation");
                    Injected = true;
                }
                return result;
            }

            if (request.Arguments.Count > 0 && request.Arguments[0] == "rev-parse")
                GitIdentityRequests.Add($"{request.FileName} {string.Join(' ', request.Arguments)} @ {request.WorkingDirectory}");
            var actual = await RunRealAsync(request, cancellationToken).ConfigureAwait(false);
            if (pauseDuringGitPath is not null && !PauseInjected
                && string.Equals(Path.GetFileNameWithoutExtension(request.FileName), "git", StringComparison.OrdinalIgnoreCase)
                && RuntimePaths.Comparer.Equals(request.WorkingDirectory, pauseDuringGitPath)
                && request.Arguments.SequenceEqual(["rev-parse", "HEAD"]))
            {
                target.Run("bd", ["update", pauseBeadId!, "--add-label", "needs-human"]);
                PauseInjected = true;
            }
            return actual;
        }

        private Task<ProcessResult> RunRealAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            var environment = new Dictionary<string, string?>(target.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return system.RunAsync(request with { Environment = environment }, cancellationToken);
        }
    }
}
