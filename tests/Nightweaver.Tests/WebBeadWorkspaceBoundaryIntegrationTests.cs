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
