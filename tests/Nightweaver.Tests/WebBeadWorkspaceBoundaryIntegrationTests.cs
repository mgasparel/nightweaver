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
    [Fact]
    public async Task WorkspaceReadsNativeChildrenAndParentWithoutTrackerMutation()
    {
        using var target = new BeadsFixture("workspace native relations");
        var epic = target.Create("Native epic", type: "epic");
        var child = target.Create("Native child", type: "feature");
        var nested = target.Create("Nested child", type: "bug");
        target.Run("bd", ["dep", "add", child, epic, "--type", "parent-child"]);
        target.Run("bd", ["dep", "add", nested, child, "--type", "parent-child"]);
        var up = target.Run("bd", ["--readonly", "--sandbox", "dep", "list", epic, "--direction", "up", "--json"]);
        var down = target.Run("bd", ["--readonly", "--sandbox", "dep", "list", child, "--direction", "down", "--json"]);
        using var nativeUp = JsonDocument.Parse(up);
        using var nativeDown = JsonDocument.Parse(down);
        Assert.Equal(child, nativeUp.RootElement[0].GetProperty("id").GetString());
        Assert.Equal(epic, nativeDown.RootElement[0].GetProperty("id").GetString());
        var before = target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--json"]);
        using var factory = CreateFactory(target, out var runner);
        using var client = factory.CreateClient();
        using var parentWorkspace = await GetWorkspaceAsync(client, epic);
        var children = parentWorkspace.RootElement.GetProperty("relations").EnumerateArray().ToArray();
        Assert.Single(children);
        Assert.Equal(child, children[0].GetProperty("id").GetString());
        Assert.Equal("incoming", children[0].GetProperty("direction").GetString());
        Assert.Equal("feature", children[0].GetProperty("issueType").GetString());
        using var childWorkspace = await GetWorkspaceAsync(client, child);
        Assert.Contains(childWorkspace.RootElement.GetProperty("relations").EnumerateArray(), row =>
            row.GetProperty("id").GetString() == epic && row.GetProperty("direction").GetString() == "outgoing");
        Assert.Contains(childWorkspace.RootElement.GetProperty("relations").EnumerateArray(), row =>
            row.GetProperty("id").GetString() == nested && row.GetProperty("direction").GetString() == "incoming");
        Assert.Equal(before, target.Run("bd", ["--readonly", "--sandbox", "list", "--all", "--limit", "0", "--json"]));
        Assert.All(runner.Requests.Where(request => request.FileName == "bd"), request => Assert.Contains("--readonly", request.Arguments));
    }

    private static readonly string[] StructuredOlderIds = ["000-older", "100-diagnostic"];
    private static readonly string[] StructuredRecentIds = ["150-diagnostic", "160-unknown-schema", "170-malformed-json", "200-guidance", "300-cleanup"];
    private static readonly string[] StructuredRecentKinds = ["diagnostic", "ordinary", "ordinary", "human-guidance", "cleanup-warning"];
    private static readonly string[] EqualTimestampOlderIds = ["alpha"];
    private static readonly string[] EqualTimestampRecentIds = ["omega"];

    [Fact]
    public async Task WorkspaceRejectsForeignAndReparseGitAdminPointersBeforeStatusAndRechecksBeforeStatus()
    {
        using var fixture = new WorkspaceBoundaryFixture("workspace admin pointers");
        var markerId = "marker-reparse";
        var adminDirectoryId = "admin-reparse";
        var foreignBacklinkId = "foreign-backlink";
        var backlinkId = "backlink-reparse";
        var replacementId = "marker-replaced-at-status";
        var markerWorktree = fixture.AddManagedWorktree(markerId);
        var adminDirectoryWorktree = fixture.AddManagedWorktree(adminDirectoryId);
        var foreignBacklinkWorktree = fixture.AddManagedWorktree(foreignBacklinkId);
        var backlinkWorktree = fixture.AddManagedWorktree(backlinkId);
        var replacementWorktree = fixture.AddManagedWorktree(replacementId);
        fixture.WriteSentinels();

        var markerFile = Path.Combine(markerWorktree, ".git");
        var markerAdmin = ReadAdminDirectory(markerWorktree);
        File.Delete(markerFile);
        CreateDirectoryJunction(markerFile, markerAdmin);

        var adminDirectory = ReadAdminDirectory(adminDirectoryWorktree);
        var backupAdminDirectory = Path.Combine(fixture.CommonDirectory, "worktrees", "admin-backup");
        Directory.Move(adminDirectory, backupAdminDirectory);
        CreateDirectoryJunction(adminDirectory, backupAdminDirectory);

        var foreignBacklinkAdmin = ReadAdminDirectory(foreignBacklinkWorktree);
        File.WriteAllText(Path.Combine(foreignBacklinkAdmin, "gitdir"), Path.Combine(fixture.ForeignRepository, ".git"));

        var backlinkAdmin = ReadAdminDirectory(backlinkWorktree);
        var backlinkFile = Path.Combine(backlinkAdmin, "gitdir");
        File.Delete(backlinkFile);
        CreateDirectoryJunction(backlinkFile, fixture.ForeignRepository);

        var replacementMarker = Path.Combine(replacementWorktree, ".git");
        var replacementAdminDirectory = ReadAdminDirectory(replacementWorktree);
        var targetTreeBefore = Snapshot.FileTree(fixture.TargetRepository);
        var foreignTreeBefore = Snapshot.FileTree(fixture.ForeignRepository);

        var runner = fixture.CreateRunner();
        var observer = fixture.CreateObserver(runner);
        foreach (var beadId in new[] { markerId, adminDirectoryId, foreignBacklinkId, backlinkId })
        {
            var environment = await observer.ObserveAsync(fixture.Target, beadId, TestContext.Current.CancellationToken);
            Assert.Equal("unsafe", environment.State);
            Assert.Equal("unknown", environment.GitStatus);
            Assert.Null(environment.WorktreePath);
            Assert.Empty(runner.StatusRequests);
        }
        Assert.Equal(4, runner.CommonDirectoryRequests);

        var raceRunner = fixture.CreateRunner(() =>
        {
            File.Delete(replacementMarker);
            CreateDirectoryJunction(replacementMarker, replacementAdminDirectory);
        });
        var replacement = await fixture.CreateObserver(raceRunner).ObserveAsync(
            fixture.Target, replacementId, TestContext.Current.CancellationToken);
        Assert.Equal("unsafe", replacement.State);
        Assert.Equal("unknown", replacement.GitStatus);
        Assert.Null(replacement.WorktreePath);
        Assert.True((File.GetAttributes(replacementMarker) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(2, raceRunner.WorktreeListRequests);
        Assert.Equal(1, raceRunner.CommonDirectoryRequests);
        Assert.Empty(runner.StatusRequests);
        Assert.Empty(raceRunner.StatusRequests);
        Assert.Equal("target source sentinel remains unchanged", File.ReadAllText(fixture.TargetSentinel));
        Assert.Equal("foreign repository must remain unchanged", File.ReadAllText(fixture.ForeignSentinel));
        Assert.Equal(targetTreeBefore, Snapshot.FileTree(fixture.TargetRepository));
        Assert.Equal(foreignTreeBefore, Snapshot.FileTree(fixture.ForeignRepository));
    }

    [Fact]
    public async Task WorkspaceReportsReparsePointAncestryWithoutFollowingIt()
    {
        using var fixture = new WorkspaceBoundaryFixture("workspace reparse ancestry");
        fixture.WriteSentinels();
        var targetBefore = Snapshot.FileTree(fixture.TargetRepository);
        var worktreesRoot = fixture.HostPaths.WorktreesRoot(fixture.Project.Name);
        CreateDirectoryJunction(worktreesRoot, fixture.TargetRepository);
        var runner = fixture.CreateRunner();

        var environment = await fixture.CreateObserver(runner).ObserveAsync(
            fixture.Target, "reparse-ancestry", TestContext.Current.CancellationToken);

        Assert.Equal("unsafe", environment.State);
        Assert.Equal("unknown", environment.GitStatus);
        Assert.Null(environment.WorktreePath);
        Assert.Empty(runner.Requests);
        Assert.Equal(targetBefore, Snapshot.FileTree(fixture.TargetRepository));
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

    private sealed class WorkspaceBoundaryFixture : IDisposable
    {
        private readonly TempDirectory temp;
        private readonly List<string> managedWorktrees = [];

        internal WorkspaceBoundaryFixture(string name)
        {
            temp = new TempDirectory();
            TargetRepository = Directory.CreateDirectory(Path.Combine(temp.Path, name, "target")).FullName;
            CommonDirectory = Directory.CreateDirectory(Path.Combine(TargetRepository, ".git")).FullName;
            Directory.CreateDirectory(Path.Combine(CommonDirectory, "worktrees"));
            Directory.CreateDirectory(Path.Combine(CommonDirectory, "objects"));
            Directory.CreateDirectory(Path.Combine(CommonDirectory, "refs", "heads"));
            File.WriteAllText(Path.Combine(CommonDirectory, "HEAD"), "ref: refs/heads/main\n");
            ForeignRepository = Directory.CreateDirectory(Path.Combine(temp.Path, name, "foreign")).FullName;
            Directory.CreateDirectory(Path.Combine(ForeignRepository, ".git"));
            HostPaths = new(Path.Combine(temp.Path, name, "host"));
            Directory.CreateDirectory(HostPaths.Root);
            Project = CreateProject(TargetRepository);
        }

        internal string TargetRepository { get; }
        internal string CommonDirectory { get; }
        internal string ForeignRepository { get; }
        internal string TargetSentinel => Path.Combine(TargetRepository, "target-admin-sentinel.txt");
        internal string ForeignSentinel => Path.Combine(ForeignRepository, "foreign-admin-sentinel.txt");
        internal HostPaths HostPaths { get; }
        internal ProjectConfig Project { get; }
        internal ValidatedTargetRepository Target => new(TargetRepository, "main");

        internal string AddManagedWorktree(string beadId)
        {
            var worktree = Path.Combine(HostPaths.WorktreesRoot(Project.Name), beadId);
            var admin = Path.Combine(CommonDirectory, "worktrees", beadId);
            Directory.CreateDirectory(worktree);
            Directory.CreateDirectory(admin);
            Directory.CreateDirectory(Path.Combine(admin, "logs"));
            File.WriteAllText(Path.Combine(admin, "HEAD"), new string('b', 40) + "\n");
            File.WriteAllText(Path.Combine(admin, "commondir"), Path.GetRelativePath(admin, CommonDirectory) + "\n");
            File.WriteAllText(Path.Combine(admin, "gitdir"), Path.Combine(worktree, ".git") + "\n");
            File.WriteAllText(Path.Combine(admin, "logs", "HEAD"), string.Empty);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + admin + "\n");
            managedWorktrees.Add(worktree);
            return worktree;
        }

        internal void WriteSentinels()
        {
            File.WriteAllText(TargetSentinel, "target source sentinel remains unchanged");
            File.WriteAllText(ForeignSentinel, "foreign repository must remain unchanged");
        }

        internal WorkspaceBoundaryProcessRunner CreateRunner(Action? onSecondWorktreeList = null)
            => new(TargetRepository, CommonDirectory, managedWorktrees, onSecondWorktreeList);

        internal BeadWorkspaceEnvironmentObserver CreateObserver(IProcessRunner runner)
            => new(HostPaths, Project, new GitWorktreeReader(runner), runner, new UnavailableBeadWorkspaceRuntimeObserver());

        private static ProjectConfig CreateProject(string target)
        {
            var settings = AgentRoles.All.ToDictionary(role => role,
                _ => new AgentSettings(AgentCli.Claude, null, "low", 1));
            return new(ProjectSlug.Parse("workspace-test"), "workspace-test.json", new(target, "main"),
                new(1, 1, 1), new(3, 1), new(AgentCli.Claude, null, "low", 1), new RoleSettings(settings));
        }

        public void Dispose() => temp.Dispose();
    }

    private sealed class WorkspaceBoundaryProcessRunner : IProcessRunner
    {
        private static readonly string[] WorktreeArguments = ["worktree", "list", "--porcelain", "-z"];
        private static readonly string[] CommonDirectoryArguments = ["rev-parse", "--path-format=absolute", "--git-common-dir"];
        private static readonly string[] StatusArguments = ["status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=all"];
        private readonly string targetRepository;
        private readonly string commonDirectory;
        private readonly string worktreeOutput;
        private readonly Action? onSecondWorktreeList;
        private readonly ConcurrentQueue<ProcessRequest> requests = new();
        private int worktreeListRequests;
        private int commonDirectoryRequests;

        internal WorkspaceBoundaryProcessRunner(string targetRepository, string commonDirectory,
            IReadOnlyList<string> managedWorktrees, Action? onSecondWorktreeList)
        {
            this.targetRepository = targetRepository;
            this.commonDirectory = commonDirectory;
            this.onSecondWorktreeList = onSecondWorktreeList;
            var rows = new List<string>
            {
                $"worktree {targetRepository}\0HEAD {new string('a', 40)}\0branch refs/heads/main\0\0"
            };
            rows.AddRange(managedWorktrees.Select(path =>
                $"worktree {path}\0HEAD {new string('b', 40)}\0detached\0\0"));
            worktreeOutput = string.Concat(rows);
        }

        internal IReadOnlyList<ProcessRequest> Requests => requests.ToArray();
        internal IReadOnlyList<ProcessRequest> StatusRequests => requests.ToArray()
            .Where(request => request.Arguments.SequenceEqual(StatusArguments, StringComparer.Ordinal)).ToArray();
        internal int WorktreeListRequests => Volatile.Read(ref worktreeListRequests);
        internal int CommonDirectoryRequests => Volatile.Read(ref commonDirectoryRequests);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(request);
            if (request.FileName != "git" || !RuntimePaths.Comparer.Equals(request.WorkingDirectory, targetRepository))
                throw new InvalidOperationException($"Unexpected process request: {request.FileName} {string.Join(' ', request.Arguments)} in {request.WorkingDirectory}");
            if (request.Arguments.SequenceEqual(WorktreeArguments, StringComparer.Ordinal))
            {
                if (Interlocked.Increment(ref worktreeListRequests) == 2) onSecondWorktreeList?.Invoke();
                return Task.FromResult(new ProcessResult(0, worktreeOutput, string.Empty));
            }
            if (request.Arguments.SequenceEqual(CommonDirectoryArguments, StringComparer.Ordinal))
            {
                Interlocked.Increment(ref commonDirectoryRequests);
                return Task.FromResult(new ProcessResult(0, commonDirectory + "\r\n", string.Empty));
            }
            if (request.Arguments.SequenceEqual(StatusArguments, StringComparer.Ordinal))
                throw new InvalidOperationException("Git status must not run for a rejected worktree path.");
            throw new InvalidOperationException($"Unexpected Git request: {string.Join(' ', request.Arguments)}");
        }
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
