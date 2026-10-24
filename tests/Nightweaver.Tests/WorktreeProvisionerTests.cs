using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class WorktreeProvisionerTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string[] FetchMainArguments = ["fetch", "origin", "main"];

    [Fact]
    public async Task FreshPlanningUsesFetchedFullyQualifiedRefAndOnlyAddsAtTargetRoot()
    {
        using var fixture = new ProvisionerScript();
        var result = await fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Destination, result.Path);
        Assert.Equal(Sha, result.HeadSha);
        Assert.Equal(FetchMainArguments, fixture.Requests.Single(request => request.Arguments[0] == "fetch").Arguments);
        Assert.Contains(fixture.Requests, request => request.Arguments.SequenceEqual(["worktree", "add", "--detach", fixture.Destination, "refs/remotes/origin/main"]));
        Assert.DoesNotContain(fixture.Requests, IsWorktreeLocalMutationAtTargetRoot);
        Assert.All(fixture.Requests.Where(request => request.Arguments[0] is "fetch" or "worktree"), request =>
            Assert.Equal(fixture.Target, request.WorkingDirectory));
        Assert.All(fixture.Requests.Where(request => request.Arguments[0] is "symbolic-ref" or "status"), request =>
            Assert.Equal(fixture.Destination, request.WorkingDirectory));
        Assert.True(IndexOf(fixture.Requests, request => request.Arguments[0] == "fetch")
            < IndexOf(fixture.Requests, request => request.Arguments[0] == "rev-parse")
            && IndexOf(fixture.Requests, request => request.Arguments[0] == "rev-parse")
            < IndexOf(fixture.Requests, request => request.Arguments[0] == "worktree" && request.Arguments[1] == "add"));
        Assert.All(fixture.Requests, request =>
        {
            Assert.Equal("git", request.FileName);
            Assert.Equal("0", request.Environment["GIT_OPTIONAL_LOCKS"]);
            Assert.Null(request.Environment["GIT_DIR"]);
            Assert.Null(request.Environment["GIT_INDEX_FILE"]);
            Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
        });
    }

    [Fact]
    public async Task FreshDeliveryUsesExactBranchAndCleansOnlyTheIsolatedCheckout()
    {
        using var fixture = new ProvisionerScript(remoteBranchExists: false);
        await fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.Requests, request => request.Arguments.SequenceEqual(
            ["worktree", "add", "--no-track", "-b", $"nightweaver/{fixture.BeadId}", fixture.Destination, "refs/remotes/origin/main"]));
        Assert.Contains(fixture.Requests, request => request.Arguments.SequenceEqual(["reset", "--hard", "HEAD"])
            && request.WorkingDirectory == fixture.Destination);
        Assert.Contains(fixture.Requests, request => request.Arguments.SequenceEqual(["clean", "-fd"])
            && request.WorkingDirectory == fixture.Destination);
        Assert.DoesNotContain(fixture.Requests, IsWorktreeLocalMutationAtTargetRoot);
        Assert.All(fixture.Requests.Where(request => request.Arguments[0] is "reset" or "clean"), request =>
            Assert.Equal(fixture.Destination, request.WorkingDirectory));
    }

    [Fact]
    public async Task FreshDeliveryTracksExactRemoteBeadBranchWhenPresent()
    {
        using var fixture = new ProvisionerScript(remoteBranchExists: true);
        await fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.Requests, request => request.Arguments.SequenceEqual(
            ["worktree", "add", "--track", "-b", $"nightweaver/{fixture.BeadId}", fixture.Destination, $"refs/remotes/origin/nightweaver/{fixture.BeadId}"]));
    }

    [Theory]
    [InlineData("bad-head")]
    [InlineData("dirty-status")]
    public async Task InvalidVerificationOutputFailsWithoutReturningAResult(string failure)
    {
        using var fixture = new ProvisionerScript(finalVerificationFailure: failure);
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.Contains(failure == "bad-head" ? "invalid full commit SHA" : "contains tracked changes", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] is "reset" or "clean");
    }

    [Fact]
    public async Task FetchFailureStopsBeforeDirectoryCreationOrWorktreeAddAndRetainsStderr()
    {
        using var fixture = new ProvisionerScript(fetchFailure: true);
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.Contains("fetch origin", exception.Message, StringComparison.Ordinal);
        Assert.Equal("fixture fetch error", exception.StandardError);
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "worktree" && request.Arguments[1] == "add");
        Assert.False(Directory.Exists(fixture.WorktreesRoot));
    }

    [Fact]
    public async Task CancellationIsPassedToGitAndStopsBeforeProvisioning()
    {
        using var fixture = new ProvisionerScript(cancelOnFetch: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provision(WorktreePurpose.Planning, cancellationToken));
        var fetch = Assert.Single(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.Equal(cancellationToken, fixture.ObservedTokens[fetch]);
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "worktree" && request.Arguments[1] == "add");
    }

    [Fact]
    public async Task CancellationDuringRegistryReadPropagatesWithoutFetching()
    {
        using var fixture = new ProvisionerScript(cancelOnRegistry: true);
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provision(WorktreePurpose.Planning, token));
        Assert.Single(fixture.Requests, request => request.Arguments[0] == "worktree");
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("CON.txt")]
    [InlineData("bad.")]
    [InlineData("bad ")]
    [InlineData("a:b")]
    public async Task InvalidBeadIdFailsBeforeAnyGitRequestOrDirectoryCreation(string id)
    {
        using var fixture = new ProvisionerScript(beadId: id);
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Requests);
        Assert.False(Directory.Exists(fixture.WorktreesRoot));
    }

    [Fact]
    public async Task TargetRootOverlapFailsBeforeAnyGitRequest()
    {
        using var fixture = new ProvisionerScript();
        var overlapping = new HostPaths(fixture.Target);
        await Assert.ThrowsAsync<ToolExecutionException>(() => new WorktreeProvisioner(fixture.Runner).ProvisionAsync(
            overlapping, new ProjectSlug("demo"), new(fixture.Target, "main"), fixture.BeadId,
            WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task BranchRegisteredAtAnotherPathFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(branchAtForeignPath: true);
        var exception = await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.Contains("registered outside the canonical destination", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.False(Directory.Exists(fixture.WorktreesRoot));
    }

    [Fact]
    public async Task OccupiedUnregisteredDestinationFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(occupiedDestination: true);
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.Destination, "sentinel")));
    }

    [Fact]
    public async Task LockedRegistrationFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(existingDestination: true, lockedDestination: true);
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.Destination, "sentinel")));
    }

    [Fact]
    public async Task WrongPurposeRegistrationFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(existingDestination: true, existingBranch: "refs/heads/main");
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
    }

    [Fact]
    public async Task PlanningRejectsAttachedRegistrationBeforeFetch()
    {
        using var fixture = new ProvisionerScript(existingDestination: true, existingBranch: "refs/heads/other");
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
    }

    [Fact]
    public async Task OrphanLocalDeliveryBranchFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(localBranchExists: true);
        var error = await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Delivery, TestContext.Current.CancellationToken));
        Assert.Contains("already exists without its canonical registered worktree", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.False(Directory.Exists(fixture.WorktreesRoot));
    }

    [Fact]
    public async Task MissingRegisteredDirectoryFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(existingDestination: true, missingRegisteredDestination: true);
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
    }

    [Fact]
    public async Task PrunableRegistrationFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript(existingDestination: true, prunableDestination: true);
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
    }

    [Fact]
    public async Task LinkedManagedAncestorFailsBeforeFetch()
    {
        using var fixture = new ProvisionerScript();
        using var external = new TempDirectory();
        File.WriteAllText(Path.Combine(external.Path, "sentinel"), "preserve");
        Directory.CreateDirectory(fixture.Host);
        WorktreeInspectionTests.CreateDirectoryLink(Path.Combine(fixture.Host, "worktrees"), external.Path);
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(external.Path, "sentinel")));
    }

    [Fact]
    public async Task ExistingCaseVariantChildIsRejectedOnCaseInsensitiveFilesystems()
    {
        using var fixture = new ProvisionerScript();
        if (!OperatingSystem.IsWindows()) return;
        var alias = Path.Combine(fixture.WorktreesRoot, fixture.BeadId.ToUpperInvariant());
        Directory.CreateDirectory(alias);
        File.WriteAllText(Path.Combine(alias, "sentinel"), "preserve case alias");
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.Equal("preserve case alias", File.ReadAllText(Path.Combine(alias, "sentinel")));
    }

    [Fact]
    public async Task NestedLinkSentinelFailsBeforeFetchWithoutFollowingLink()
    {
        using var fixture = new ProvisionerScript(existingDestination: true, existingBranch: null);
        using var external = new TempDirectory();
        File.WriteAllText(Path.Combine(external.Path, "sentinel"), "do not follow");
        var link = Path.Combine(fixture.Destination, "nested-link");
        WorktreeInspectionTests.CreateDirectoryLink(link, external.Path);
        var before = File.ReadAllText(Path.Combine(external.Path, "sentinel"));
        await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.Provision(WorktreePurpose.Planning, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, request => request.Arguments[0] == "fetch");
        Assert.Equal(before, File.ReadAllText(Path.Combine(external.Path, "sentinel")));
    }

    private static bool IsWorktreeLocalMutationAtTargetRoot(ProcessRequest request) =>
        request.Arguments[0] is "checkout" or "reset" or "clean" && request.WorkingDirectory.EndsWith("target repo", StringComparison.Ordinal);

    private static int IndexOf(IReadOnlyList<ProcessRequest> requests, Func<ProcessRequest, bool> predicate)
    {
        for (var index = 0; index < requests.Count; index++) if (predicate(requests[index])) return index;
        return int.MaxValue;
    }

    private sealed class ProvisionerScript : IDisposable
    {
        private readonly TempDirectory temp = new();
        private string registry;
        private readonly bool remoteBranchExists;
        private readonly bool fetchFailure;
        private readonly bool cancelOnFetch;
        private readonly bool cancelOnRegistry;
        private readonly bool finalBadHead;
        private readonly bool finalDirtyStatus;
        private readonly bool localBranchExists;

        internal ProvisionerScript(string beadId = "ns-xci.1", bool remoteBranchExists = false,
            bool fetchFailure = false, bool cancelOnFetch = false, string? finalVerificationFailure = null,
            bool branchAtForeignPath = false, bool occupiedDestination = false, bool lockedDestination = false,
            bool existingDestination = false, string? existingBranch = null, bool cancelOnRegistry = false,
            bool localBranchExists = false, bool missingRegisteredDestination = false, bool prunableDestination = false)
        {
            BeadId = beadId;
            Target = temp.CreateDirectory("target repo");
            Host = Path.Combine(temp.Path, "nightweaver host");
            Destination = Path.Combine(Host, "worktrees", "demo", BeadId);
            WorktreesRoot = Path.Combine(Host, "worktrees", "demo");
            this.remoteBranchExists = remoteBranchExists;
            this.fetchFailure = fetchFailure;
            this.cancelOnFetch = cancelOnFetch;
            this.cancelOnRegistry = cancelOnRegistry;
            this.localBranchExists = localBranchExists;
            finalBadHead = finalVerificationFailure == "bad-head";
            finalDirtyStatus = finalVerificationFailure == "dirty-status";
            var root = $"worktree {Target}\0HEAD {Sha}\0branch refs/heads/main\0\0";
            if (branchAtForeignPath)
            {
                var foreign = Path.Combine(temp.Path, "foreign checkout");
                Directory.CreateDirectory(foreign);
                registry = root + $"worktree {foreign}\0HEAD {Sha}\0branch refs/heads/nightweaver/{BeadId}\0\0";
            }
            else if (existingDestination)
            {
                if (!missingRegisteredDestination)
                {
                    Directory.CreateDirectory(Destination);
                    File.WriteAllText(Path.Combine(Destination, "sentinel"), "preserve");
                }
                var branch = existingBranch is null ? "detached\0" : $"branch {existingBranch}\0";
                registry = root + $"worktree {Destination}\0HEAD {Sha}\0{branch}{(lockedDestination ? "locked human\0" : string.Empty)}{(prunableDestination ? "prunable missing\0" : string.Empty)}\0";
            }
            else
            {
                registry = root;
                if (occupiedDestination)
                {
                    Directory.CreateDirectory(Destination);
                    File.WriteAllText(Path.Combine(Destination, "sentinel"), "preserve");
                }
            }
            Runner = new RecordingProcessRunner(HandleAsync);
        }

        internal string BeadId { get; }
        internal string Target { get; }
        internal string Host { get; }
        internal string Destination { get; }
        internal string WorktreesRoot { get; }
        internal RecordingProcessRunner Runner { get; }
        internal IReadOnlyList<ProcessRequest> Requests => Runner.Requests;
        internal Dictionary<ProcessRequest, CancellationToken> ObservedTokens { get; } = [];
        internal Task<ProvisionedWorktree> Provision(WorktreePurpose purpose, CancellationToken token = default) =>
            new WorktreeProvisioner(Runner).ProvisionAsync(new HostPaths(Host), new ProjectSlug("demo"),
                new ValidatedTargetRepository(Target, "main"), BeadId, purpose, token);

        private Task<ProcessResult> HandleAsync(ProcessRequest request, CancellationToken token)
        {
            ObservedTokens[request] = token;
            var args = request.Arguments;
            if (args.SequenceEqual(["worktree", "list", "--porcelain", "-z"]))
            {
                if (cancelOnRegistry) return Task.FromException<ProcessResult>(new OperationCanceledException(token));
                return Done(0, registry);
            }
            if (args[0] == "check-ref-format") return Done(0);
            if (args[0] == "show-ref")
            {
                var exists = args[^1].Contains("refs/remotes/origin/nightweaver", StringComparison.Ordinal) && remoteBranchExists
                    || args[^1].StartsWith("refs/heads/nightweaver/", StringComparison.Ordinal) && localBranchExists;
                return Done(exists ? 0 : 1);
            }
            if (args[0] == "fetch")
            {
                if (cancelOnFetch)
                {
                    return Task.FromException<ProcessResult>(new OperationCanceledException(token));
                }
                return fetchFailure ? Done(128, stderr: "fixture fetch error") : Done(0);
            }
            if (args[0] == "rev-parse")
            {
                if (finalBadHead && args[^1] == "HEAD^{commit}") return Done(0, "not-a-sha\n");
                return Done(0, Sha + "\n");
            }
            if (args.SequenceEqual(["worktree", "add", "--detach", Destination, "refs/remotes/origin/main"])
                || args[0] == "worktree" && args[1] == "add")
            {
                Directory.CreateDirectory(Destination);
                var branch = args.Contains("--detach", StringComparer.Ordinal) ? "detached\0"
                    : $"branch refs/heads/nightweaver/{BeadId}\0";
                registry = $"worktree {Target}\0HEAD {Sha}\0branch refs/heads/main\0\0"
                    + $"worktree {Destination}\0HEAD {Sha}\0{branch}\0";
                return Done(0);
            }
            if (args.SequenceEqual(["symbolic-ref", "--quiet", "HEAD"]))
                return request.WorkingDirectory == Destination && purposeIsDelivery
                    ? Done(0, $"refs/heads/nightweaver/{BeadId}\n")
                    : Done(1);
            if (args[0] == "status") return Done(0, finalDirtyStatus ? "?? stray\n" : string.Empty);
            return Done(0);
        }

        private bool purposeIsDelivery => registry.Contains($"branch refs/heads/nightweaver/{BeadId}", StringComparison.Ordinal);

        public void Dispose() => temp.Dispose();

        private static Task<ProcessResult> Done(int exitCode, string stdout = "", string stderr = "") =>
            Task.FromResult(new ProcessResult(exitCode, stdout, stderr));
    }
}
