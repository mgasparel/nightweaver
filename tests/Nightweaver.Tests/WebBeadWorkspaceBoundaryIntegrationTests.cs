using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Web;

namespace Nightweaver.Tests;

public sealed class WebBeadWorkspaceBoundaryIntegrationTests
{
    private static readonly string[] StructuredOlderIds = ["000-older", "100-diagnostic"];
    private static readonly string[] StructuredRecentIds = ["150-diagnostic", "160-unknown-schema", "170-malformed-json", "200-guidance", "300-cleanup"];
    private static readonly string[] StructuredRecentKinds = ["diagnostic", "ordinary", "ordinary", "human-guidance", "cleanup-warning"];
    private static readonly string[] EqualTimestampOlderIds = ["alpha"];
    private static readonly string[] EqualTimestampRecentIds = ["omega"];

    [Fact]
    public void ReadOnlyBdShowOnlyRefreshesTheLastTouchedTimestamp()
    {
        using var target = new BeadsFixture("readonly show timestamp probe");
        var beadId = target.Create("Readonly show timestamp probe");
        var lastTouchedPath = Path.Combine(target.Path, ".beads", "last-touched");
        Assert.True(File.Exists(lastTouchedPath));

        var initialTimestamp = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(lastTouchedPath, initialTimestamp);
        var treeBefore = Snapshot.FileTree(target.Path);
        var beforeInfo = new FileInfo(lastTouchedPath);
        var before = (beforeInfo.Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lastTouchedPath))),
            File.GetAttributes(lastTouchedPath), beforeInfo.LastWriteTimeUtc);

        target.Run("bd", ["--readonly", "--sandbox", "show", beadId, "--json"]);

        var afterInfo = new FileInfo(lastTouchedPath);
        var after = (afterInfo.Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lastTouchedPath))),
            File.GetAttributes(lastTouchedPath), afterInfo.LastWriteTimeUtc);
        var treeAfter = Snapshot.FileTree(target.Path);
        var differences = treeBefore.Split('\n').Zip(treeAfter.Split('\n'))
            .Where(pair => !StringComparer.Ordinal.Equals(pair.First, pair.Second))
            .Select(pair => (pair.First, pair.Second)).ToArray();

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.Item2, after.Item2);
        Assert.Equal(before.Item3, after.Item3);
        Assert.True(after.Item4 > before.Item4, $"Expected bd show to refresh last-touched mtime. Before={before.Item4:O}; after={after.Item4:O}.");
        Assert.Single(differences);
        Assert.Contains(".beads\\last-touched", differences[0].First, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".beads\\last-touched", differences[0].Second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceReportsVerifiedGitStateProviderMetadataAndReadOnlyObservations()
    {
        using var target = new BeadsFixture("workspace environment target");
        var beadId = target.Create("Environment observation");
        var observedAt = DateTimeOffset.Parse("2026-10-31T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var runtime = new FixedRuntimeObserver(new(4321, "running", observedAt, "fixture-provider"));
        var failStatus = 0;
        using var factory = CreateFactory(target, out var runner, runtime,
            (request, _) =>
            {
                if (Volatile.Read(ref failStatus) == 1 && IsGit(request, "status"))
                    return Task.FromResult<ProcessResult?>(new(72, "", "private status failure sentinel"));
                return Task.FromResult<ProcessResult?>(null);
            });
        using var client = factory.CreateClient();
        var path = AddManagedWorktree(target, factory, beadId, detached: true);
        var trackerBefore = TrackerProjection(target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--json"]));
        var commentsBefore = target.Run("bd", ["--readonly", "--sandbox", "comments", beadId, "--json"]);
        var targetTreeBefore = Snapshot.FileTree(target.Path, allowReadOnlyBdLastTouchedTimestamp: true);
        var hostTreeBefore = Snapshot.FileTree(factory.HostRoot);
        var targetStatusBefore = target.Run("git", ["status", "--porcelain=v1", "-z", "--untracked-files=normal"]);
        var targetHeadBefore = target.Run("git", ["rev-parse", "HEAD"]).Trim();
        var registrationsBefore = target.Run("git", ["worktree", "list", "--porcelain", "-z"]);

        using var cleanResponse = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cleanResponse.StatusCode);
        Assert.Equal("no-store", cleanResponse.Headers.CacheControl?.ToString());
        using var cleanDocument = JsonDocument.Parse(await cleanResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var clean = cleanDocument.RootElement.GetProperty("environment");
        Assert.Equal("present", clean.GetProperty("state").GetString());
        Assert.Equal(path, clean.GetProperty("worktreePath").GetString());
        Assert.Equal("clean", clean.GetProperty("gitStatus").GetString());
        Assert.True(clean.GetProperty("detached").GetBoolean());
        Assert.Equal(JsonValueKind.Null, clean.GetProperty("branch").ValueKind);
        Assert.Equal(12, clean.GetProperty("head").GetString()!.Length);
        Assert.Equal(4321, clean.GetProperty("processId").GetInt32());
        Assert.Equal("running", clean.GetProperty("processState").GetString());
        Assert.Equal("fixture-provider", clean.GetProperty("runtimeSource").GetString());
        Assert.Equal(observedAt, clean.GetProperty("runtimeObservedAt").GetDateTimeOffset());

        var targetTreeAfterCleanRead = Snapshot.FileTree(target.Path, allowReadOnlyBdLastTouchedTimestamp: true);
        Assert.True(targetTreeBefore == targetTreeAfterCleanRead,
            $"Workspace GET changed target filesystem entries. Expected-only: {string.Join(" | ", targetTreeBefore.Split('\n').Except(targetTreeAfterCleanRead.Split('\n'), StringComparer.Ordinal))}; "
            + $"actual-only: {string.Join(" | ", targetTreeAfterCleanRead.Split('\n').Except(targetTreeBefore.Split('\n'), StringComparer.Ordinal))}");

        var statusRequest = Assert.Single(runner.Requests, request => IsGit(request, "status")
            && RuntimePaths.Comparer.Equals(request.WorkingDirectory, path));
        Assert.Contains("--porcelain=v1", statusRequest.Arguments);
        Assert.Contains("-z", statusRequest.Arguments);
        Assert.Contains("--untracked-files=normal", statusRequest.Arguments);
        Assert.Contains("--ignore-submodules=all", statusRequest.Arguments);
        Assert.Equal("0", statusRequest.Environment["GIT_OPTIONAL_LOCKS"]);
        Assert.Null(statusRequest.Environment["GIT_DIR"]);
        Assert.Null(statusRequest.Environment["GIT_INDEX_FILE"]);
        Assert.Equal(hostTreeBefore, Snapshot.FileTree(factory.HostRoot));

        File.WriteAllText(Path.Combine(path, "workspace-untracked-dirty-sentinel.txt"), "dirty");
        using var dirtyResponse = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        using var dirtyDocument = JsonDocument.Parse(await dirtyResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, dirtyResponse.StatusCode);
        Assert.Equal("present", dirtyDocument.RootElement.GetProperty("environment").GetProperty("state").GetString());
        Assert.Equal("dirty", dirtyDocument.RootElement.GetProperty("environment").GetProperty("gitStatus").GetString());

        Volatile.Write(ref failStatus, 1);
        using var failedStatusResponse = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var failedStatusBody = await failedStatusResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var failedStatusDocument = JsonDocument.Parse(failedStatusBody);
        var unavailable = failedStatusDocument.RootElement.GetProperty("environment");
        Assert.Equal(HttpStatusCode.OK, failedStatusResponse.StatusCode);
        Assert.Equal("unavailable", unavailable.GetProperty("state").GetString());
        Assert.Equal("unknown", unavailable.GetProperty("gitStatus").GetString());
        Assert.Contains("Git cleanliness could not be read", unavailable.GetProperty("warning").GetString());
        Assert.DoesNotContain("private status failure sentinel", failedStatusBody, StringComparison.Ordinal);

        Assert.Equal(trackerBefore, TrackerProjection(target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--json"])));
        Assert.Equal(targetStatusBefore, target.Run("git", ["status", "--porcelain=v1", "-z", "--untracked-files=normal"]));
        Assert.Equal(targetHeadBefore, target.Run("git", ["rev-parse", "HEAD"]).Trim());
        Assert.Equal(registrationsBefore, target.Run("git", ["worktree", "list", "--porcelain", "-z"]));
        Assert.Equal(commentsBefore, target.Run("bd", ["--readonly", "--sandbox", "comments", beadId, "--json"]));
    }

    [Fact]
    public async Task WorkspaceRefusesLockedForeignAndMismatchedGitAdminDirectories()
    {
        using var target = new BeadsFixture("workspace registration target");
        using var foreign = new GitFixture("foreign workspace repository");
        var lockedId = target.Create("Locked worktree");
        var foreignId = target.Create("Foreign worktree");
        var markerId = target.Create("Mismatched admin marker");
        using var factory = CreateFactory(target, out _);
        using var client = factory.CreateClient();
        var lockedPath = ManagedPath(factory, lockedId);
        var foreignPath = ManagedPath(factory, foreignId);
        var markerPath = ManagedPath(factory, markerId);
        PrepareGitHead(target);
        target.Run("git", ["worktree", "add", "--detach", lockedPath, "HEAD"]);
        target.Run("git", ["worktree", "lock", "--reason", "workspace fixture lock", lockedPath]);
        GitFixture.RunGit(foreign.Path, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "foreign base"]);
        GitFixture.RunGit(foreign.Path, ["worktree", "add", "--detach", foreignPath, "HEAD"]);
        target.Run("git", ["worktree", "add", "--detach", markerPath, "HEAD"]);
        var pointer = File.ReadAllText(Path.Combine(markerPath, ".git")).Trim();
        Assert.StartsWith("gitdir: ", pointer, StringComparison.Ordinal);
        var adminPath = Path.GetFullPath(pointer["gitdir: ".Length..], markerPath);
        File.WriteAllText(Path.Combine(adminPath, "commondir"), Path.Combine(target.Path, ".git", "not-the-common-dir"));

        foreach (var beadId in new[] { lockedId, foreignId, markerId })
        {
            using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var environment = document.RootElement.GetProperty("environment");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("unsafe", environment.GetProperty("state").GetString());
            Assert.Equal("unknown", environment.GetProperty("gitStatus").GetString());
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("worktreePath").ValueKind);
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("processId").ValueKind);
        }
    }

    [Fact]
    public async Task WorkspaceReturnsRefreshableConflictWhenBeadChangesBetweenInventoryAndShow()
    {
        using var target = new BeadsFixture("workspace show conflict target");
        var beadId = target.Create("Workflow changes before show");
        var changed = 0;
        using var factory = CreateFactory(target, out var runner, intercept: (request, _) =>
        {
            if (request.FileName == "bd" && request.Arguments.Contains("show", StringComparer.Ordinal)
                && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                target.Run("bd", ["update", beadId, "--status", "in_progress"]);
            return Task.FromResult<ProcessResult?>(null);
        });
        using var client = factory.CreateClient();
        var targetProjectionBefore = TrackerProjection(target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"]));

        using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bead changed", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Refresh", problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Volatile.Read(ref changed));
        Assert.Single(runner.Requests, request => request.FileName == "bd" && request.Arguments.Contains("show", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Requests, request => IsGit(request, "status"));
        Assert.NotEqual(targetProjectionBefore,
            TrackerProjection(target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"])));
    }

    [Fact]
    public async Task WorkspaceReturnsRefreshableConflictWhenBeadDisappearsFromFinalInventory()
    {
        using var target = new BeadsFixture("workspace final inventory conflict target");
        var beadId = target.Create("Disappears from final inventory");
        var listArguments = new[]
        {
            "--readonly", "--sandbox", "list", "--all", "--limit", "0", "--include-ephemeral", "--include-gates", "--include-infra", "--include-templates", "--json"
        };
        var inventoryCalls = 0;
        using var factory = CreateFactory(target, out var runner, intercept: (request, _) =>
        {
            if (request.FileName == "bd" && request.Arguments.Count > 2 && request.Arguments[2] == "list"
                && Interlocked.Increment(ref inventoryCalls) == 2)
            {
                using var currentInventory = JsonDocument.Parse(target.Run("bd", listArguments));
                var remaining = currentInventory.RootElement.EnumerateArray()
                    .Where(issue => issue.GetProperty("id").GetString() != beadId)
                    .Select(issue => issue.Clone())
                    .ToArray();
                return Task.FromResult<ProcessResult?>(new(0, JsonSerializer.Serialize(remaining), string.Empty));
            }
            return Task.FromResult<ProcessResult?>(null);
        });
        using var client = factory.CreateClient();
        var targetProjectionBefore = TrackerProjection(target.Run("bd", listArguments));

        using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bead changed", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("disappeared", problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(target.Path, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Volatile.Read(ref inventoryCalls));
        Assert.Single(runner.Requests, request => request.FileName == "bd" && request.Arguments.Contains("show", StringComparer.Ordinal));
        Assert.Single(runner.Requests, request => request.FileName == "bd" && request.Arguments.Contains("comments", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Requests, request => IsGit(request, "status"));
        Assert.Equal(targetProjectionBefore, TrackerProjection(target.Run("bd", listArguments)));
        using var actualDetail = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "show", beadId, "--json"]));
        Assert.Equal(beadId, actualDetail.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task WorkspaceRejectsForeignAndReparseGitAdminPointersBeforeStatusAndRechecksBeforeStatus()
    {
        using var target = new BeadsFixture("workspace admin pointer target");
        using var foreign = new GitFixture("workspace foreign admin repository");
        var markerId = target.Create("Reparse marker");
        var adminDirectoryId = target.Create("Reparse admin directory");
        var foreignBacklinkId = target.Create("Foreign backlink");
        var backlinkId = target.Create("Reparse backlink");
        var replacementId = target.Create("Admin marker replaced at status boundary");
        string worktreeList = string.Empty;
        string? replacementMarker = null;
        string? replacementAdminDirectory = null;
        var replacementEnabled = 0;
        var replacementListCalls = 0;
        var statusRequests = new ConcurrentQueue<string?>();
        using var factory = CreateFactory(target, out var runner, intercept: (request, _) =>
        {
            if (IsGit(request, "worktree") && request.Arguments.Skip(1).FirstOrDefault() == "list"
                && worktreeList.Length > 0)
            {
                if (Volatile.Read(ref replacementEnabled) == 1
                    && Interlocked.Increment(ref replacementListCalls) == 2)
                {
                    Assert.NotNull(replacementMarker);
                    Assert.NotNull(replacementAdminDirectory);
                    File.Delete(replacementMarker!);
                    CreateDirectoryJunction(replacementMarker!, replacementAdminDirectory!);
                }
                return Task.FromResult<ProcessResult?>(new(0, worktreeList, string.Empty));
            }
            if (IsGit(request, "status"))
            {
                statusRequests.Enqueue(request.WorkingDirectory);
                return Task.FromResult<ProcessResult?>(new(0, string.Empty, string.Empty));
            }
            return Task.FromResult<ProcessResult?>(null);
        });
        using var client = factory.CreateClient();
        var markerWorktree = AddManagedWorktree(target, factory, markerId, detached: true);
        var adminDirectoryWorktree = AddManagedWorktree(target, factory, adminDirectoryId, detached: true);
        var foreignBacklinkWorktree = AddManagedWorktree(target, factory, foreignBacklinkId, detached: true);
        var backlinkWorktree = AddManagedWorktree(target, factory, backlinkId, detached: true);
        var replacementWorktree = AddManagedWorktree(target, factory, replacementId, detached: true);
        GitFixture.RunGit(foreign.Path, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "workspace foreign base"]);
        var foreignSentinel = Path.Combine(foreign.Path, "foreign-admin-sentinel.txt");
        File.WriteAllText(foreignSentinel, "foreign repository must remain unchanged");
        worktreeList = target.Run("git", ["worktree", "list", "--porcelain", "-z"]);

        var markerFile = Path.Combine(markerWorktree, ".git");
        var markerAdmin = ReadAdminDirectory(markerWorktree);
        File.Delete(markerFile);
        CreateDirectoryJunction(markerFile, markerAdmin);

        var adminDirectory = ReadAdminDirectory(adminDirectoryWorktree);
        var backupAdminDirectory = Path.Combine(target.Path, ".git", "workspace-admin-backup-" + adminDirectoryId);
        Directory.Move(adminDirectory, backupAdminDirectory);
        CreateDirectoryJunction(adminDirectory, backupAdminDirectory);

        var foreignBacklinkAdmin = ReadAdminDirectory(foreignBacklinkWorktree);
        File.WriteAllText(Path.Combine(foreignBacklinkAdmin, "gitdir"), Path.Combine(foreign.Path, ".git"));

        var backlinkAdmin = ReadAdminDirectory(backlinkWorktree);
        var backlinkFile = Path.Combine(backlinkAdmin, "gitdir");
        File.Delete(backlinkFile);
        CreateDirectoryJunction(backlinkFile, foreign.Path);

        replacementMarker = Path.Combine(replacementWorktree, ".git");
        replacementAdminDirectory = ReadAdminDirectory(replacementWorktree);
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var foreignGitBefore = Snapshot.GitStatus(foreign.Path);
        var initialTargetSentinel = Path.Combine(target.Path, "target-admin-sentinel.txt");
        File.WriteAllText(initialTargetSentinel, "target source sentinel remains unchanged");
        var targetTreeBefore = Snapshot.FileTree(target.Path, allowReadOnlyBdLastTouchedTimestamp: true);

        foreach (var (beadId, title) in new[]
        {
            (markerId, "Reparse marker"),
            (adminDirectoryId, "Reparse admin directory"),
            (foreignBacklinkId, "Foreign backlink"),
            (backlinkId, "Reparse backlink")
        })
        {
            using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(beadId, document.RootElement.GetProperty("bead").GetProperty("id").GetString());
            Assert.Equal(title, document.RootElement.GetProperty("bead").GetProperty("title").GetString());
            var environment = document.RootElement.GetProperty("environment");
            Assert.Equal("unsafe", environment.GetProperty("state").GetString());
            Assert.Equal("unknown", environment.GetProperty("gitStatus").GetString());
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("worktreePath").ValueKind);
        }
        Assert.Empty(statusRequests);

        Volatile.Write(ref replacementListCalls, 0);
        Volatile.Write(ref replacementEnabled, 1);
        using (var response = await client.GetAsync($"/api/beads/{replacementId}", TestContext.Current.CancellationToken))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(replacementId, document.RootElement.GetProperty("bead").GetProperty("id").GetString());
            Assert.Equal("unsafe", document.RootElement.GetProperty("environment").GetProperty("state").GetString());
            Assert.Equal("unknown", document.RootElement.GetProperty("environment").GetProperty("gitStatus").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("environment").GetProperty("worktreePath").ValueKind);
        }
        Assert.Equal(2, Volatile.Read(ref replacementListCalls));
        Assert.True((File.GetAttributes(replacementMarker!) & FileAttributes.ReparsePoint) != 0);
        Assert.Empty(statusRequests);
        Assert.Contains("target source sentinel remains unchanged", File.ReadAllText(initialTargetSentinel), StringComparison.Ordinal);
        Assert.Contains("foreign repository must remain unchanged", File.ReadAllText(foreignSentinel), StringComparison.Ordinal);
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(foreignGitBefore, Snapshot.GitStatus(foreign.Path));
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(target.Path, allowReadOnlyBdLastTouchedTimestamp: true));
        Assert.DoesNotContain(runner.Requests, request => IsGit(request, "status"));
    }

    [Fact]
    public async Task WorkspaceReportsReparsePointAncestryWithoutFollowingIt()
    {
        using var target = new BeadsFixture("workspace reparse target");
        var beadId = target.Create("Reparse ancestry");
        using var factory = CreateFactory(target, out _);
        using var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        var worktreesRoot = host.WorktreesRoot(project.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(worktreesRoot)!);
        CreateDirectoryJunction(worktreesRoot, target.Path);
        var targetBefore = target.Run("git", ["status", "--porcelain=v1", "-z", "--untracked-files=normal"]);

        using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var environment = document.RootElement.GetProperty("environment");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("unsafe", environment.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, environment.GetProperty("worktreePath").ValueKind);
        Assert.Equal(targetBefore, target.Run("git", ["status", "--porcelain=v1", "-z", "--untracked-files=normal"]));
    }

    [Fact]
    public async Task WorkspaceRejectsUnregisteredAndPrunablePathsAndNeverCallsADeletedCheckoutClean()
    {
        using var target = new BeadsFixture("workspace unsafe path matrix");
        var unregisteredId = target.Create("Unregistered destination");
        var prunableId = target.Create("Prunable registration");
        var disappearingId = target.Create("Checkout disappears during status");
        string? disappearingPath = null;
        string? trackerAfterRemoval = null;
        string? commentsAfterRemoval = null;
        string? targetTreeAfterRemoval = null;
        var removed = false;
        using var factory = CreateFactory(target, out var runner, intercept: (request, _) =>
        {
            if (!removed && disappearingPath is not null && IsGit(request, "status")
                && RuntimePaths.Comparer.Equals(request.WorkingDirectory, disappearingPath))
            {
                Directory.Delete(disappearingPath, recursive: true);
                removed = true;
                trackerAfterRemoval = target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--json"]);
                commentsAfterRemoval = target.Run("bd", ["--readonly", "--sandbox", "comments", disappearingId, "--json"]);
                targetTreeAfterRemoval = Snapshot.FileTree(target.Path);
                return Task.FromResult<ProcessResult?>(new(0, string.Empty, string.Empty));
            }
            return Task.FromResult<ProcessResult?>(null);
        });
        using var client = factory.CreateClient();

        var unregisteredPath = ManagedPath(factory, unregisteredId);
        Directory.CreateDirectory(unregisteredPath);
        var prunablePath = ManagedPath(factory, prunableId);
        PrepareGitHead(target);
        Directory.CreateDirectory(Path.GetDirectoryName(prunablePath)!);
        target.Run("git", ["worktree", "add", "--detach", prunablePath, "HEAD"]);
        Directory.Delete(prunablePath, recursive: true);
        Directory.CreateDirectory(prunablePath);
        var worktreeList = target.Run("git", ["worktree", "list", "--porcelain", "-z"]);
        Assert.Contains("prunable", worktreeList, StringComparison.OrdinalIgnoreCase);

        foreach (var beadId in new[] { unregisteredId, prunableId })
        {
            using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var environment = document.RootElement.GetProperty("environment");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("unsafe", environment.GetProperty("state").GetString());
            Assert.Equal("unknown", environment.GetProperty("gitStatus").GetString());
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("worktreePath").ValueKind);
        }
        Assert.DoesNotContain(runner.Requests, request => IsGit(request, "status")
            && (RuntimePaths.Comparer.Equals(request.WorkingDirectory, unregisteredPath)
                || RuntimePaths.Comparer.Equals(request.WorkingDirectory, prunablePath)));

        disappearingPath = AddManagedWorktree(target, factory, disappearingId, detached: false);
        var commentsBeforeRemoval = target.Run("bd", ["--readonly", "--sandbox", "comments", disappearingId, "--json"]);
        using var disappearingResponse = await client.GetAsync($"/api/beads/{disappearingId}", TestContext.Current.CancellationToken);
        using var disappearingDocument = JsonDocument.Parse(await disappearingResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var disappeared = disappearingDocument.RootElement.GetProperty("environment");
        Assert.Equal(HttpStatusCode.OK, disappearingResponse.StatusCode);
        Assert.Equal("unavailable", disappeared.GetProperty("state").GetString());
        Assert.Equal("unknown", disappeared.GetProperty("gitStatus").GetString());
        Assert.True(removed);
        Assert.NotNull(trackerAfterRemoval);
        Assert.Equal(trackerAfterRemoval, target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--json"]));
        Assert.Equal(commentsAfterRemoval, target.Run("bd", ["--readonly", "--sandbox", "comments", disappearingId, "--json"]));
        Assert.Equal(commentsBeforeRemoval, commentsAfterRemoval);
        Assert.Equal(targetTreeAfterRemoval, Snapshot.FileTree(target.Path));
    }

    [Fact]
    public async Task WorkspaceScrubsHostileAmbientTrackerAndGitRoutingVariables()
    {
        using var target = new BeadsFixture("workspace ambient target");
        using var foreign = new BeadsFixture("workspace ambient foreign");
        var beadId = target.Create("Configured target wins");
        var foreignId = foreign.Create("Must not route from hostile environment");
        using var factory = CreateFactory(target, out var runner);
        using var client = factory.CreateClient();
        var worktreePath = AddManagedWorktree(target, factory, beadId, detached: true);
        var targetCommentsBefore = target.Run("bd", ["--readonly", "--sandbox", "comments", beadId, "--json"]);
        var targetTreeBefore = Snapshot.FileTree(target.Path, allowReadOnlyBdLastTouchedTimestamp: true);
        var foreignTreeBefore = Snapshot.FileTree(foreign.Path);
        var ambient = (IDictionary<string, string?>)target.Environment;
        var poison = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BEADS_DIR"] = Path.Combine(foreign.Path, ".beads"),
            ["BEADS_DB"] = Path.Combine(foreign.Path, ".beads", "embedded.db"),
            ["BD_DB"] = Path.Combine(foreign.Path, ".beads", "ambient-db"),
            ["GIT_DIR"] = Path.Combine(foreign.Path, ".git"),
            ["GIT_WORK_TREE"] = foreign.Path,
            ["GIT_INDEX_FILE"] = Path.Combine(foreign.Path, ".git", "ambient-index"),
            ["GIT_COMMON_DIR"] = Path.Combine(foreign.Path, ".git")
        };
        var previous = poison.Keys.ToDictionary(name => name,
            name => ambient.TryGetValue(name, out var value) ? value : null, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pair in poison) ambient[pair.Key] = pair.Value;
            using var response = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(beadId, document.RootElement.GetProperty("bead").GetProperty("id").GetString());
            Assert.Equal("Configured target wins", document.RootElement.GetProperty("bead").GetProperty("title").GetString());
            Assert.Equal("present", document.RootElement.GetProperty("environment").GetProperty("state").GetString());
            Assert.Equal(worktreePath, document.RootElement.GetProperty("environment").GetProperty("worktreePath").GetString());

            var bdRequests = runner.Requests.Where(request => request.FileName == "bd").ToArray();
            Assert.NotEmpty(bdRequests);
            Assert.All(bdRequests, request =>
            {
                Assert.Null(request.Environment["BEADS_DIR"]);
                Assert.Null(request.Environment["BEADS_DB"]);
                Assert.Null(request.Environment["BD_DB"]);
            });
            var gitRequests = runner.Requests.Where(request => request.FileName == "git").ToArray();
            Assert.Contains(gitRequests, request => request.Arguments.Contains("status", StringComparer.Ordinal)
                && RuntimePaths.Comparer.Equals(request.WorkingDirectory, worktreePath));
            Assert.All(gitRequests, request =>
            {
                Assert.Null(request.Environment["GIT_DIR"]);
                Assert.Null(request.Environment["GIT_WORK_TREE"]);
                Assert.Null(request.Environment["GIT_INDEX_FILE"]);
                Assert.Null(request.Environment["GIT_COMMON_DIR"]);
            });
            Assert.DoesNotContain(bdRequests, request => request.Arguments.Contains(foreignId, StringComparer.Ordinal));
        }
        finally
        {
            foreach (var name in poison.Keys)
            {
                if (previous[name] is { } value) ambient[name] = value;
                else ambient.Remove(name);
            }
        }
        var targetTreeAfter = Snapshot.FileTree(target.Path, allowReadOnlyBdLastTouchedTimestamp: true);
        Assert.True(targetTreeBefore == targetTreeAfter,
            $"Workspace GET changed the ambient target filesystem. Expected-only: {string.Join(" | ", targetTreeBefore.Split('\n').Except(targetTreeAfter.Split('\n'), StringComparer.Ordinal))}; "
            + $"actual-only: {string.Join(" | ", targetTreeAfter.Split('\n').Except(targetTreeBefore.Split('\n'), StringComparer.Ordinal))}");
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(foreign.Path));
        Assert.Equal(targetCommentsBefore, target.Run("bd", ["--readonly", "--sandbox", "comments", beadId, "--json"]));
    }

    [Fact]
    public async Task WorkspaceDetailFailuresAreSafeAndRequestCancellationPropagates()
    {
        using var target = new BeadsFixture("workspace output failures");
        var beadId = target.Create("Strict workspace output");
        var mode = 0;
        CancellationTokenSource? requestCancellation = null;
        var oversizedHistory = JsonSerializer.Serialize(Enumerable.Range(0, 2049).Select(index => Comment(
            $"cap-{index:D4}", beadId, "fixture", "history cap sentinel", "2026-10-31T18:00:00Z")));
        using var factory = CreateFactory(target, out _, intercept: async (request, token) =>
        {
            if (request.FileName != "bd") return null;
            if (Volatile.Read(ref mode) == 1 && request.Arguments.Contains("show", StringComparer.Ordinal))
                return new ProcessResult(1, "", "private target path sentinel");
            if (Volatile.Read(ref mode) == 2 && request.Arguments.Contains("comments", StringComparer.Ordinal))
                return new ProcessResult(0, "[{\"id\":\"cross\",\"issue_id\":\"foreign-bead\",\"author\":\"fixture\",\"text\":\"not this bead\",\"created_at\":\"2026-01-01T00:00:00Z\"}]", "");
            if (Volatile.Read(ref mode) == 3 && request.Arguments.Contains("comments", StringComparer.Ordinal))
            {
                requestCancellation!.Cancel();
                await Task.Yield();
                throw new OperationCanceledException(token);
            }
            if (Volatile.Read(ref mode) == 4 && request.Arguments.Contains("show", StringComparer.Ordinal))
                return new ProcessResult(0, "not-json", "");
            if (Volatile.Read(ref mode) == 5 && request.Arguments.Contains("comments", StringComparer.Ordinal))
                return new ProcessResult(0, "not-json", "");
            if (Volatile.Read(ref mode) == 6 && request.Arguments.Contains("show", StringComparer.Ordinal))
                throw new ToolExecutionException("bd timed out after 30s", "private timeout path sentinel");
            if (Volatile.Read(ref mode) == 7 && request.Arguments.Contains("comments", StringComparer.Ordinal))
                return new ProcessResult(0, oversizedHistory, "");
            return null;
        });
        using var client = factory.CreateClient();

        Volatile.Write(ref mode, 1);
        using var showFailure = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var showFailureBody = await showFailure.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, showFailure.StatusCode);
        Assert.DoesNotContain("private target path sentinel", showFailureBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, showFailureBody, StringComparison.OrdinalIgnoreCase);

        Volatile.Write(ref mode, 2);
        using var commentFailure = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var commentFailureBody = await commentFailure.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, commentFailure.StatusCode);
        Assert.DoesNotContain("not this bead", commentFailureBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, commentFailureBody, StringComparison.OrdinalIgnoreCase);

        Volatile.Write(ref mode, 4);
        using var malformedShow = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var malformedShowBody = await malformedShow.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, malformedShow.StatusCode);
        Assert.DoesNotContain("not-json", malformedShowBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, malformedShowBody, StringComparison.OrdinalIgnoreCase);

        Volatile.Write(ref mode, 5);
        using var malformedComments = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var malformedCommentsBody = await malformedComments.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, malformedComments.StatusCode);
        Assert.DoesNotContain("not-json", malformedCommentsBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, malformedCommentsBody, StringComparison.OrdinalIgnoreCase);

        Volatile.Write(ref mode, 6);
        using var timedOutShow = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var timedOutShowBody = await timedOutShow.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOutShow.StatusCode);
        Assert.DoesNotContain("private timeout path sentinel", timedOutShowBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, timedOutShowBody, StringComparison.OrdinalIgnoreCase);

        Volatile.Write(ref mode, 7);
        using var overflowingHistory = await client.GetAsync($"/api/beads/{beadId}", TestContext.Current.CancellationToken);
        var overflowingHistoryBody = await overflowingHistory.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, overflowingHistory.StatusCode);
        Assert.DoesNotContain("history cap sentinel", overflowingHistoryBody, StringComparison.Ordinal);
        Assert.DoesNotContain(target.Path, overflowingHistoryBody, StringComparison.OrdinalIgnoreCase);

        Volatile.Write(ref mode, 3);
        requestCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetAsync($"/api/beads/{beadId}", requestCancellation.Token));
        Assert.True(requestCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task WorkspaceHistoryPartitionsZeroOneClosedInvalidAndStructuredCycles()
    {
        using var target = new BeadsFixture("workspace history matrix");
        var zeroId = target.Create("Zero comments");
        var oneId = target.Create("One comment");
        target.Run("bd", ["comments", "add", oneId, "Only comment"]);
        var closedId = target.Create("Closed pause", status: "closed", labels: ["product-decision-required"]);
        target.Run("bd", ["comments", "add", closedId, "Resolved context"]);
        target.Run("bd", ["comments", "add", closedId, "Latest closed note"]);
        var unattributedId = target.Create("Pause without a source", labels: ["needs-human"]);
        target.Run("bd", ["comments", "add", unattributedId, "Ordinary context is not a diagnostic"]);
        var invalidId = target.Create("Invalid workflow source", labels: ["needs-human", "stage:implement", "stage:review"]);
        target.Run("bd", ["comments", "add", invalidId, "Unattributed invalid context"]);
        var diagnosticId = target.Create("Structured multi-stage history", labels: ["needs-human", "stage:implement"]);
        var equalTimestampId = target.Create("Equal timestamp decision", labels: ["product-decision-required"]);

        var syntheticComments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [diagnosticId] = JsonSerializer.Serialize(new[]
            {
                Comment("000-older", diagnosticId, "human", "Prior cycle context", "2026-10-30T10:00:00Z"),
                Comment("100-diagnostic", diagnosticId, "implementer", Diagnostic(diagnosticId), "2026-10-30T10:00:01Z"),
                Comment("150-diagnostic", diagnosticId, "reviewer", Diagnostic(diagnosticId), "2026-10-30T10:00:02Z"),
                Comment("160-unknown-schema", diagnosticId, "future-agent-role", "{\"schema\":\"future-human-prompt-v9\",\"instruction\":\"keep literal\"}", "2026-10-30T10:00:03Z"),
                Comment("170-malformed-json", diagnosticId, "future-agent-role", "{", "2026-10-30T10:00:04Z"),
                Comment("200-guidance", diagnosticId, "local-console", Guidance("Keep the existing API."), "2026-10-30T10:00:05Z"),
                Comment("300-cleanup", diagnosticId, "system", CleanupFailure(diagnosticId), "2026-10-30T10:00:06Z")
            }),
            [equalTimestampId] = JsonSerializer.Serialize(new[]
            {
                Comment("alpha", equalTimestampId, "planner", "Prior question", "2026-10-31T10:00:00Z"),
                Comment("omega", equalTimestampId, "planner", "Which option should we choose?", "2026-10-31T10:00:00Z")
            })
        };
        using var factory = CreateFactory(target, out _, intercept: (request, _) =>
        {
            if (request.FileName == "bd" && request.Arguments.Count >= 4 && request.Arguments[2] == "comments"
                && syntheticComments.TryGetValue(request.Arguments[3], out var json))
                return Task.FromResult<ProcessResult?>(new(0, json, ""));
            return Task.FromResult<ProcessResult?>(null);
        });
        using var client = factory.CreateClient();

        using var zero = await GetWorkspaceAsync(client, zeroId);
        Assert.Empty(zero.RootElement.GetProperty("comments").EnumerateArray());
        Assert.Empty(zero.RootElement.GetProperty("history").GetProperty("olderCommentIds").EnumerateArray());
        Assert.Empty(zero.RootElement.GetProperty("history").GetProperty("recentCommentIds").EnumerateArray());
        Assert.Equal("none", zero.RootElement.GetProperty("history").GetProperty("selectionSource").GetString());

        using var one = await GetWorkspaceAsync(client, oneId);
        Assert.Empty(one.RootElement.GetProperty("history").GetProperty("olderCommentIds").EnumerateArray());
        Assert.Single(one.RootElement.GetProperty("history").GetProperty("recentCommentIds").EnumerateArray());

        using var closed = await GetWorkspaceAsync(client, closedId);
        var closedHistory = closed.RootElement.GetProperty("history");
        Assert.Equal(JsonValueKind.Null, closed.RootElement.GetProperty("attention").ValueKind);
        Assert.Equal(JsonValueKind.Null, closedHistory.GetProperty("activeCommentId").ValueKind);
        Assert.Equal("none", closedHistory.GetProperty("selectionSource").GetString());
        Assert.Single(closedHistory.GetProperty("olderCommentIds").EnumerateArray());
        Assert.Single(closedHistory.GetProperty("recentCommentIds").EnumerateArray());

        using var unattributed = await GetWorkspaceAsync(client, unattributedId);
        var unattributedHistory = unattributed.RootElement.GetProperty("history");
        Assert.Equal(JsonValueKind.Null, unattributedHistory.GetProperty("activeCommentId").ValueKind);
        Assert.Equal("none", unattributedHistory.GetProperty("selectionSource").GetString());
        Assert.Equal("needs-human", unattributed.RootElement.GetProperty("attention").GetProperty("kind").GetString());
        Assert.Single(unattributedHistory.GetProperty("recentCommentIds").EnumerateArray());

        using var invalid = await GetWorkspaceAsync(client, invalidId);
        Assert.Equal("invalid-workflow", invalid.RootElement.GetProperty("attention").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, invalid.RootElement.GetProperty("bead").GetProperty("repairCycles").ValueKind);
        Assert.Equal("none", invalid.RootElement.GetProperty("history").GetProperty("selectionSource").GetString());

        using var structured = await GetWorkspaceAsync(client, diagnosticId);
        var structuredRoot = structured.RootElement;
        var structuredHistory = structuredRoot.GetProperty("history");
        Assert.Equal("structured", structuredHistory.GetProperty("selectionSource").GetString());
        Assert.Equal("150-diagnostic", structuredHistory.GetProperty("activeCommentId").GetString());
        Assert.Equal(StructuredOlderIds, StringArray(structuredHistory.GetProperty("olderCommentIds")));
        Assert.Equal(StructuredRecentIds, StringArray(structuredHistory.GetProperty("recentCommentIds")));
        Assert.Equal(StructuredRecentKinds,
            structuredRoot.GetProperty("comments").EnumerateArray().Skip(2).Select(comment => comment.GetProperty("kind").GetString()).ToArray());
        Assert.Equal("{\"schema\":\"future-human-prompt-v9\",\"instruction\":\"keep literal\"}",
            structuredRoot.GetProperty("comments").EnumerateArray().Single(comment => comment.GetProperty("id").GetString() == "160-unknown-schema").GetProperty("body").GetString());
        Assert.Equal("{", structuredRoot.GetProperty("comments").EnumerateArray()
            .Single(comment => comment.GetProperty("id").GetString() == "170-malformed-json").GetProperty("body").GetString());
        var allIds = StringArray(structuredHistory.GetProperty("olderCommentIds")).Concat(StringArray(structuredHistory.GetProperty("recentCommentIds"))).ToArray();
        Assert.Equal(StringArray(structuredRoot.GetProperty("comments").EnumerateArray().Select(comment => comment.GetProperty("id").GetString()).ToArray()), allIds);
        Assert.Contains("QA cleanup warning", structuredRoot.GetProperty("attention").GetProperty("diagnostic").GetString());

        using var equal = await GetWorkspaceAsync(client, equalTimestampId);
        var equalHistory = equal.RootElement.GetProperty("history");
        Assert.Equal("legacy-context", equalHistory.GetProperty("selectionSource").GetString());
        Assert.Equal("omega", equalHistory.GetProperty("activeCommentId").GetString());
        Assert.Equal(EqualTimestampOlderIds, StringArray(equalHistory.GetProperty("olderCommentIds")));
        Assert.Equal(EqualTimestampRecentIds, StringArray(equalHistory.GetProperty("recentCommentIds")));
    }

    [Fact]
    public async Task WorkspaceSharesTrackerGateAcrossConcurrentReadsAndGuidanceWrites()
    {
        using var target = new BeadsFixture("workspace mixed gate");
        var beadId = target.Create("Concurrent decision", labels: ["product-decision-required"]);
        target.Run("bd", ["comments", "add", beadId, "Pick one option."]);
        using var factory = CreateFactory(target, out var runner, intercept: async (request, token) =>
        {
            if (request.FileName == "bd") await Task.Delay(15, token);
            return null;
        });
        using var client = factory.CreateClient();
        var readA = GetWorkspaceAsync(client, beadId);
        var readB = GetWorkspaceAsync(client, beadId);
        var write = SendGuidanceAsync(client, beadId, "Record this before continuing.", "note_only");
        using var resultA = await readA;
        using var resultB = await readB;
        using var writeResponse = await write;
        Assert.Equal(HttpStatusCode.OK, writeResponse.StatusCode);
        Assert.Equal(1, runner.MaximumConcurrentBdProcesses);
        Assert.All(runner.Requests.Where(request => request.FileName == "bd"
            && request.Arguments.Contains("--readonly", StringComparer.Ordinal)), request =>
        {
            Assert.Contains("--sandbox", request.Arguments);
        });
        Assert.Contains(runner.Requests, request => request.FileName == "bd"
            && request.Arguments.Contains("comments", StringComparer.Ordinal)
            && request.Arguments.Contains("add", StringComparer.Ordinal)
            && !request.Arguments.Contains("--readonly", StringComparer.Ordinal));
    }

    private static string AddManagedWorktree(BeadsFixture target, NightweaverWebApplicationFactory factory,
        string beadId, bool detached)
    {
        var destination = ManagedPath(factory, beadId);
        PrepareGitHead(target);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (detached) target.Run("git", ["worktree", "add", "--detach", destination, "HEAD"]);
        else target.Run("git", ["worktree", "add", "-b", "workspace-" + beadId, destination, "HEAD"]);
        return destination;
    }

    private static string ManagedPath(NightweaverWebApplicationFactory factory, string beadId)
    {
        var host = factory.Services.GetRequiredService<HostPaths>();
        var project = factory.Services.GetRequiredService<ProjectConfig>();
        return Path.Combine(host.WorktreesRoot(project.Name), beadId);
    }

    private static void PrepareGitHead(BeadsFixture target)
        => target.Run("git", ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "workspace base"]);

    private static string TrackerProjection(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().Select(issue =>
        {
            var labels = issue.TryGetProperty("labels", out var labelsElement) && labelsElement.ValueKind == JsonValueKind.Array
                ? labelsElement.EnumerateArray().Select(label => label.GetString() ?? "").Order(StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            return new
            {
                id = issue.GetProperty("id").GetString(),
                status = issue.GetProperty("status").GetString(),
                labels
            };
        }).OrderBy(issue => issue.id, StringComparer.Ordinal);
        return JsonSerializer.Serialize(rows);
    }

    private static async Task<JsonDocument> GetWorkspaceAsync(HttpClient client, string beadId)
    {
        using var response = await client.GetAsync($"/api/beads/{Uri.EscapeDataString(beadId)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<HttpResponseMessage> SendGuidanceAsync(HttpClient client, string beadId, string instruction, string action)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/beads/{Uri.EscapeDataString(beadId)}/decide")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { instruction, action }), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Nightweaver-Request", "console-v1");
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static bool IsGit(ProcessRequest request, string subcommand)
        => request.FileName == "git" && request.Arguments.Count > 0 && request.Arguments[0] == subcommand;

    private static string ReadAdminDirectory(string worktreePath)
    {
        var marker = File.ReadAllText(Path.Combine(worktreePath, ".git")).Trim();
        Assert.StartsWith("gitdir: ", marker, StringComparison.Ordinal);
        return Path.GetFullPath(marker["gitdir: ".Length..], worktreePath);
    }

    private static object Comment(string id, string issueId, string author, string text, string createdAt)
        => new { id, issue_id = issueId, author, text, created_at = createdAt };

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(linkPath);
        using var handle = CreateFileW(linkPath, 0x40000000, 0x00000007, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Opening junction directory failed");

        var printName = Path.GetFullPath(targetPath);
        var substituteName = @"\??\" + printName;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var dataLength = 8 + substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char);
        var buffer = new byte[8 + dataLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0xA0000003);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)dataLength));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + sizeof(char))));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer.AsSpan(16));
        printBytes.CopyTo(buffer.AsSpan(16 + substituteBytes.Length + sizeof(char)));
        if (!DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Creating directory junction failed");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint controlCode,
        byte[] inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    private static string Diagnostic(string beadId) => JsonSerializer.Serialize(new
    {
        schema = "nightweaver-needs-human-v1",
        bead_id = beadId,
        bead_type = "task",
        cause = "operational-retries-exhausted",
        failed_role = "implementer",
        reason = "verification needs a decision",
        latest_findings = "the latest run is blocked",
        cleanup_preflight = "",
        stage = "stage:implement",
        invocation_id = "invocation-fixture",
        log_directory = "logs/fixture",
        log_files = Array.Empty<string>()
    });

    private static string Guidance(string instruction) => JsonSerializer.Serialize(new
    {
        schema = "nightweaver-human-guidance-v1",
        actor = "local-console",
        action = "note_only",
        instruction
    });

    private static string CleanupFailure(string beadId) => JsonSerializer.Serialize(new
    {
        schema = "nightweaver-needs-human-cleanup-failure-v1",
        bead_id = beadId,
        failure = "QA cleanup warning"
    });

    private static string[] StringArray(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static string[] StringArray(IEnumerable<string?> values) => values.Select(value => value!).ToArray();

    private static NightweaverWebApplicationFactory CreateFactory(BeadsFixture target,
        out WorkspaceFixtureProcessRunner runner, IBeadWorkspaceRuntimeObserver? runtimeObserver = null,
        Func<ProcessRequest, CancellationToken, Task<ProcessResult?>>? intercept = null)
    {
        var createdRunner = new WorkspaceFixtureProcessRunner(target, intercept);
        runner = createdRunner;
        var factory = new NightweaverWebApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(createdRunner);
            if (runtimeObserver is not null)
            {
                services.RemoveAll<IBeadWorkspaceRuntimeObserver>();
                services.AddSingleton(runtimeObserver);
            }
        });
        File.WriteAllText(factory.ConfigPath, JsonSerializer.Serialize(new
        {
            name = "workspace-test",
            target_repository = new { path = target.Path }
        }));
        return factory;
    }

    private sealed class FixedRuntimeObserver(BeadWorkspaceRuntimeObservation value) : IBeadWorkspaceRuntimeObserver
    {
        public BeadWorkspaceRuntimeObservation Observe(string project, string beadId) => value;
    }

    private sealed class WorkspaceFixtureProcessRunner(BeadsFixture target,
        Func<ProcessRequest, CancellationToken, Task<ProcessResult?>>? intercept) : IProcessRunner
    {
        private readonly SystemProcessRunner actual = new();
        private readonly ConcurrentQueue<ProcessRequest> requests = new();
        private int activeBd;
        private int maximumConcurrentBdProcesses;

        internal IReadOnlyList<ProcessRequest> Requests => requests.ToArray();
        internal int MaximumConcurrentBdProcesses => Volatile.Read(ref maximumConcurrentBdProcesses);

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            var isBd = request.FileName == "bd";
            if (isBd)
            {
                var current = Interlocked.Increment(ref activeBd);
                UpdateMaximum(current);
            }
            try
            {
                var intercepted = intercept is null ? null : await intercept(request, cancellationToken);
                if (intercepted is not null) return intercepted;
                var environment = target.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
                return await actual.RunAsync(request with { Environment = environment }, cancellationToken);
            }
            finally
            {
                if (isBd) Interlocked.Decrement(ref activeBd);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var previous = Volatile.Read(ref maximumConcurrentBdProcesses);
                if (current <= previous || Interlocked.CompareExchange(ref maximumConcurrentBdProcesses, current, previous) == previous)
                    return;
            }
        }
    }
}
