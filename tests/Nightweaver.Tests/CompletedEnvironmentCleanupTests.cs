using System.Text.Json;
using System.Text.Json.Nodes;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class CompletedEnvironmentCleanupTests
{
    [Theory]
    [InlineData("tool")]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("launch")]
    [InlineData("cancel")]
    public async Task CheckoutInspectionFailureRetainsOnlyItsEnvironmentAndPreservesCancellation(string failure)
    {
        using var fixture = new Fixture();
        var otherWorktree = Directory.CreateDirectory(Path.Combine(fixture.Paths.WorktreesRoot(Fixture.Project), "other")).FullName;
        var otherTmp = Directory.CreateDirectory(Path.Combine(fixture.Paths.TmpRoot(Fixture.Project), "other")).FullName;
        var otherRegistered = true;
        var runner = new RecordingProcessRunner(async (request, token) =>
        {
            if (request.FileName == "bd")
            {
                var result = await fixture.Runner.RunAsync(request, token);
                var inventory = JsonNode.Parse(result.StandardOutput)!.AsArray();
                var other = inventory[0]!.DeepClone();
                other["id"] = "other";
                inventory.Add(other);
                return result with { StandardOutput = inventory.ToJsonString() };
            }
            if (request.Arguments[0] == "status" && request.WorkingDirectory == fixture.Worktree)
                throw failure switch
                {
                    "tool" => new ToolExecutionException("injected status failure"),
                    "io" => new IOException("injected status failure"),
                    "access" => new UnauthorizedAccessException("injected status failure"),
                    "launch" => new System.ComponentModel.Win32Exception("injected status failure"),
                    _ => new OperationCanceledException(token)
                };
            if (request.Arguments.Take(2).SequenceEqual(["worktree", "remove"]) && request.Arguments[^1] == otherWorktree)
            {
                Directory.Delete(otherWorktree, true);
                otherRegistered = false;
                return new ProcessResult(0, "", "");
            }
            var original = await fixture.Runner.RunAsync(request, token);
            if (request.Arguments.Take(2).SequenceEqual(["worktree", "list"]) && otherRegistered)
                return original with { StandardOutput = original.StandardOutput
                    + $"worktree {otherWorktree}\0HEAD {fixture.Runner.Head}\0branch refs/heads/nightweaver/other\0\0" };
            if (request.WorkingDirectory == otherWorktree && request.Arguments[0] == "rev-parse")
                return original with { StandardOutput = original.StandardOutput.Replace(fixture.Worktree, otherWorktree, StringComparison.Ordinal)
                    .Replace("nightweaver/done", "nightweaver/other", StringComparison.Ordinal)
                    .Replace(Path.Combine("worktrees", "done"), Path.Combine("worktrees", "other"), StringComparison.Ordinal) };
            return original;
        });
        Task<IReadOnlyList<Nightweaver.Application.StartupCleanupDiagnostic>> Clean() =>
            new CompletedEnvironmentCleanup(new BeadsReader(runner), runner).ReconcileAsync(
                fixture.Paths, Fixture.Project, fixture.Repository, "nightweaver:demo", TestContext.Current.CancellationToken);

        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Clean);
        else
        {
            var diagnostics = await Clean();
            Assert.Contains(diagnostics, item => item.Path == fixture.Worktree && item.Reason.Contains("injected status failure", StringComparison.Ordinal));
            var records = fixture.Store.Read().Obligations;
            var retained = Assert.Single(records, item => item.BeadId == "done");
            Assert.Equal(CleanupStatus.NeedsAttention, retained.Status);
            Assert.Contains(fixture.Worktree, retained.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("injected status failure", retained.Diagnostic, StringComparison.Ordinal);
            Assert.Equal(CleanupStatus.Resolved, Assert.Single(records, item => item.BeadId == "other").Status);
            Assert.False(Directory.Exists(otherWorktree));
            Assert.False(Directory.Exists(otherTmp));
            Assert.False(otherRegistered);
        }
        Assert.True(Directory.Exists(fixture.Worktree));
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);
        Assert.All(fixture.Runner.Requests.Where(request => request.FileName == "bd"), request => Assert.Contains("list", request.Arguments));
    }

    [Fact]
    public async Task LostRemovalResponseSurvivesRestartAndResolvesFromFreshAbsenceWithoutReplayingGit()
    {
        using var fixture = new Fixture();
        fixture.Runner.LoseRemovalResponse = true;
        var first = await fixture.CleanAsync();
        Assert.NotEmpty(first);
        var pending = fixture.Read();
        Assert.Equal(CleanupStatus.NeedsAttention, pending.Status);
        Assert.True(pending.Worktree.ConfirmedAbsent);
        Assert.False(pending.Tmp.ConfirmedAbsent);
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.Equal(1, fixture.Runner.Removals);

        // Unknown/lost responses need explicit inspection; restart alone cannot rearm them.
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.Equal(pending, fixture.Read());
        await fixture.Store.WriteAsync(pending with { Status = CleanupStatus.Pending, CycleAttempts = 0 }, CancellationToken.None);
        Assert.Empty(await fixture.CleanAsync());
        var resolved = fixture.Read();
        Assert.Equal(CleanupStatus.Resolved, resolved.Status);
        Assert.True(resolved.Worktree.ConfirmedAbsent && resolved.Tmp.ConfirmedAbsent);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal(2, resolved.Attempts);
        Assert.Equal(1, fixture.Runner.Removals);
        Assert.False(Directory.Exists(fixture.Tmp));
        Assert.Empty(await fixture.CleanAsync());
        Assert.Equal(resolved, fixture.Read());
        Assert.Equal("evidence", File.ReadAllText(fixture.Log));
        Assert.True(Directory.Exists(fixture.Unrelated));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.FileName == "bd" && !request.Arguments.Contains("list"));
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("push") || request.Arguments.Contains("prune"));
    }

    [Fact]
    public async Task ClosureBeforeEnqueueRecoversCanonicalOrphanTmp()
    {
        using var fixture = new Fixture();
        fixture.RemoveWorktree();
        Assert.False(fixture.Store.Read().Exists);
        Assert.Empty(await fixture.CleanAsync());
        Assert.Equal(CleanupStatus.Resolved, fixture.Read().Status);
        Assert.False(Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("active")]
    [InlineData("foreign")]
    [InlineData("locked")]
    [InlineData("prunable")]
    [InlineData("unregistered")]
    [InlineData("missing")]
    [InlineData("dirty")]
    [InlineData("head")]
    [InlineData("common")]
    [InlineData("link")]
    public async Task UnsafeOrChangedEnvironmentsRetainResourcesAndWorkflow(string scenario)
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync(fixture.Create(), CancellationToken.None);
        switch (scenario)
        {
            case "paused": fixture.Runner.Paused = true; break;
            case "active": fixture.Runner.Status = "in_progress"; break;
            case "foreign": fixture.Runner.Assignee = "human:other"; break;
            case "locked": fixture.Runner.Locked = true; break;
            case "prunable": fixture.Runner.Prunable = true; break;
            case "unregistered": fixture.Runner.Registered = false; break;
            case "missing": Directory.Delete(fixture.Worktree, true); break;
            case "dirty": fixture.Runner.Dirty = true; break;
            case "head": fixture.Runner.Head = new string('b', 40); break;
            case "common": fixture.Runner.Common = fixture.Unrelated; break;
            case "link":
                var link = Path.Combine(fixture.Worktree, "unsafe-link");
                if (OperatingSystem.IsWindows())
                {
                    var result = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", link, fixture.Unrelated], fixture.Worktree, null, TimeSpan.FromSeconds(10));
                    Assert.Equal(0, result.ExitCode);
                }
                else Directory.CreateSymbolicLink(link, fixture.Unrelated);
                break;
        }
        if (scenario == "common")
            await Assert.ThrowsAsync<ToolExecutionException>(() => fixture.CleanAsync());
        else
        {
            Assert.NotEmpty(await fixture.CleanAsync());
            Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
        }
        Assert.Equal(scenario != "missing", Directory.Exists(fixture.Worktree));
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);
        Assert.All(fixture.Runner.Requests.Where(request => request.FileName == "bd"), request => Assert.Contains("list", request.Arguments));
    }

    [Fact]
    public async Task PolicyRefusalRetainsResourcesWithIndependentNeedsAttention()
    {
        using var fixture = new Fixture();
        fixture.Runner.RemovalDenied = true;
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
        Assert.Contains("policy denied", fixture.Read().Diagnostic, StringComparison.Ordinal);
        Assert.True(Directory.Exists(fixture.Worktree) && Directory.Exists(fixture.Tmp));
        Assert.All(fixture.Runner.Requests.Where(request => request.FileName == "bd"), request => Assert.Contains("list", request.Arguments));
    }

    [Fact]
    public async Task OrphanTmpOverlappingAnotherRegisteredCheckoutIsRetained()
    {
        using var fixture = new Fixture();
        fixture.RemoveWorktree();
        fixture.Runner.AdditionalRegistration = Directory.CreateDirectory(Path.Combine(fixture.Tmp, "foreign-checkout")).FullName;
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
        Assert.True(Directory.Exists(fixture.Runner.AdditionalRegistration));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    [Fact]
    public async Task PauseDuringFinalIdentityReadAndFalseSuccessBothRetainEvidence()
    {
        using var fixture = new Fixture();
        fixture.Runner.PauseOnHeadRead = true;
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.True(Directory.Exists(fixture.Worktree));
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);

        fixture.Runner.Paused = false;
        fixture.Runner.PauseOnHeadRead = false;
        fixture.Runner.FalseRemovalSuccess = true;
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
        Assert.False(fixture.Read().Worktree.ConfirmedAbsent);
        Assert.True(Directory.Exists(fixture.Tmp));
    }

    [Fact]
    public async Task ReappearedResolvedResourcesNeverRegainRemovalAuthority()
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.CleanAsync());
        Directory.CreateDirectory(fixture.Tmp);
        File.WriteAllText(Path.Combine(fixture.Tmp, "new-owner"), "retain");
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.NotEmpty(await fixture.CleanAsync());
        Assert.True(Directory.Exists(fixture.Tmp));
        Assert.True(fixture.Read().Tmp.ConfirmedAbsent);
        Assert.Equal(CleanupStatus.NeedsAttention, fixture.Read().Status);
    }

    [Fact]
    public async Task NewStoreValidatesPersistedIdentityAndEvidence()
    {
        using var fixture = new Fixture();
        var record = fixture.Create();
        await fixture.Store.WriteAsync(record, CancellationToken.None);
        Assert.Equal(record, fixture.Read());
        var moved = new CompletedEnvironmentStore(new HostPaths(fixture.Unrelated), Fixture.Project, fixture.Repository);
        Directory.CreateDirectory(Path.Combine(fixture.Unrelated, "run", "demo", "cleanup"));
        File.Copy(fixture.RecordPath, Path.Combine(fixture.Unrelated, "run", "demo", "cleanup", "done.json"));
        Assert.Throws<InvalidDataException>(() => moved.Read());
    }

    [Theory]
    [InlineData("Version", "99")]
    [InlineData("Root", "\"old-root\"")]
    [InlineData("Project", "\"other\"")]
    [InlineData("Repository", "\"other\"")]
    [InlineData("CommonDirectory", "\"other\"")]
    [InlineData("BeadId", "\"../escape\"")]
    [InlineData("Worktree", "null")]
    [InlineData("Attempts", "-1")]
    [InlineData("Status", "\"Resolved\"")]
    [InlineData("Status", "999")]
    [InlineData("Registration", "{}")]
    public async Task InvalidRecordsCannotAuthorizeRemovalOrBecomeAnEmptySuccess(string field, string value)
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync(fixture.Create(), CancellationToken.None);
        var json = JsonNode.Parse(File.ReadAllText(fixture.RecordPath))!;
        json[field] = JsonNode.Parse(value);
        var invalid = json.ToJsonString();
        File.WriteAllText(fixture.RecordPath, invalid);
        var exception = await Record.ExceptionAsync(() => fixture.CleanAsync());
        Assert.True(exception is InvalidDataException or JsonException, exception?.ToString());
        Assert.Equal(invalid, File.ReadAllText(fixture.RecordPath));
        Assert.True(Directory.Exists(fixture.Worktree) && Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("interrupted")]
    [InlineData("inaccessible")]
    public async Task UnavailableStoreBlocksAllRemovalAndPreservesEvidence(string scenario)
    {
        using var fixture = new Fixture();
        if (scenario == "inaccessible")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.CleanupRoot(Fixture.Project))!);
            File.WriteAllText(fixture.Paths.CleanupRoot(Fixture.Project), "not a directory");
        }
        else
        {
            Directory.CreateDirectory(fixture.Paths.CleanupRoot(Fixture.Project));
            File.WriteAllText(scenario == "corrupt" ? fixture.RecordPath : fixture.RecordPath + ".interrupted.tmp", "{");
        }
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CleanAsync()));
        Assert.True(Directory.Exists(fixture.Worktree) && Directory.Exists(fixture.Tmp));
        Assert.Equal(0, fixture.Runner.Removals);
    }

    [Fact]
    public async Task ResolvedHistoryIsBoundedWithoutEvictingUnresolvedObligations()
    {
        using var fixture = new Fixture();
        var pending = fixture.Create();
        await fixture.Store.WriteAsync(pending, CancellationToken.None);
        for (var index = 0; index < CompletedEnvironmentStore.ResolvedHistoryLimit + 2; index++)
        {
            var record = fixture.Store.Create("old-" + index, WorktreePurpose.Delivery, null);
            await fixture.Store.WriteAsync(record with
            {
                Worktree = record.Worktree with { ConfirmedAbsent = true },
                Tmp = record.Tmp with { ConfirmedAbsent = true },
                Status = CleanupStatus.Resolved, ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-index)
            }, CancellationToken.None);
        }
        fixture.Store.TrimResolvedHistory();
        var records = fixture.Store.Read().Obligations;
        Assert.Equal(CompletedEnvironmentStore.ResolvedHistoryLimit + 1, records.Count);
        Assert.Contains(pending, records);
        Assert.DoesNotContain(records, record => record.BeadId == "old-101");
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        internal static ProjectSlug Project { get; } = new("demo");
        internal HostPaths Paths { get; }
        internal ValidatedTargetRepository Repository { get; }
        internal CleanupRunner Runner { get; }
        internal string Worktree => Path.Combine(Paths.WorktreesRoot(Project), "done");
        internal string Tmp => Path.Combine(Paths.TmpRoot(Project), "done");
        internal string Log => Path.Combine(Paths.LogsRoot(Project), "done", "invocation.log");
        internal string Unrelated => Path.Combine(temp.Path, "unrelated");
        internal string RecordPath => Path.Combine(Paths.CleanupRoot(Project), "done.json");
        internal CompletedEnvironmentStore Store => new(Paths, Project, Repository);

        internal Fixture()
        {
            Paths = new(Path.Combine(temp.Path, "host"));
            var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName;
            Repository = new(target, "main") { GitCommonDirectory = Path.Combine(target, ".git") };
            Directory.CreateDirectory(Worktree);
            Directory.CreateDirectory(Tmp);
            Directory.CreateDirectory(Unrelated);
            Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
            File.WriteAllText(Log, "evidence");
            File.WriteAllText(Path.Combine(Worktree, "ignored-qa.xml"), "passed");
            File.WriteAllText(Path.Combine(Tmp, "qa-evidence"), "passed");
            Runner = new(this);
        }

        internal CompletedEnvironmentObligation Create() => Store.Create("done", WorktreePurpose.Delivery, Runner.Registration);
        internal CompletedEnvironmentObligation Read() => Assert.Single(Store.Read().Obligations);
        internal Task<IReadOnlyList<Nightweaver.Application.StartupCleanupDiagnostic>> CleanAsync() =>
            new CompletedEnvironmentCleanup(new BeadsReader(Runner), Runner).ReconcileAsync(
                Paths, Project, Repository, "nightweaver:demo", TestContext.Current.CancellationToken);
        internal void RemoveWorktree() { Directory.Delete(Worktree, true); Runner.Registered = false; }
        public void Dispose() => temp.Dispose();
    }

    internal sealed class CleanupRunner(Fixture fixture) : IProcessRunner
    {
        internal bool Registered { get; set; } = true;
        internal bool LoseRemovalResponse { get; set; }
        internal bool FalseRemovalSuccess { get; set; }
        internal int TransientFailures { get; set; }
        internal bool RemovalDenied { get; set; }
        internal string? AdditionalRegistration { get; set; }
        internal bool Locked { get; set; }
        internal bool Prunable { get; set; }
        internal bool Paused { get; set; }
        internal bool PauseOnHeadRead { get; set; }
        internal bool Dirty { get; set; }
        internal string Status { get; set; } = "closed";
        internal string? Assignee { get; set; }
        internal string Head { get; set; } = new('a', 40);
        internal string Common { get; set; } = fixture.Repository.GitCommonDirectory;
        internal int Removals { get; private set; }
        internal List<ProcessRequest> Requests { get; } = [];
        internal GitWorktreeSnapshot Registration => new(fixture.Worktree, fixture.Worktree, Head,
            "refs/heads/nightweaver/done", false, false, Locked, null, Prunable, null);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.FileName == "bd")
            {
                Assert.Contains("list", request.Arguments);
                string[] labels = Paused ? ["stage:merge", "needs-human"] : ["stage:merge"];
                return Result(JsonSerializer.Serialize(new[] { new { id = "done", status = Status, issue_type = "task",
                    priority = 1, created_at = "2026-11-13T00:00:00Z", assignee = Assignee,
                    labels } }));
            }
            var args = request.Arguments;
            if (args[0] == "worktree" && args[1] == "list")
            {
                var output = $"worktree {fixture.Repository.Path}\0HEAD {Head}\0branch refs/heads/main\0\0";
                if (Registered) output += $"worktree {fixture.Worktree}\0HEAD {Head}\0branch refs/heads/nightweaver/done\0"
                    + (Locked ? "locked\0" : "") + (Prunable ? "prunable missing\0" : "") + "\0";
                if (AdditionalRegistration is not null)
                    output += $"worktree {AdditionalRegistration}\0HEAD {Head}\0branch refs/heads/foreign\0\0";
                return Result(output);
            }
            if (args[0] == "worktree" && args[1] == "remove")
            {
                // Persisting ownership before any destructive call is part of the public seam.
                Assert.True(File.Exists(fixture.RecordPath));
                Assert.Equal(CleanupStatus.Pending, fixture.Read().Status);
                if (RemovalDenied) throw new UnauthorizedAccessException("fixture policy denied removal");
                Removals++;
                if (TransientFailures-- > 0) return Task.FromResult(new ProcessResult(1, "", "resource busy: sharing violation"));
                if (!FalseRemovalSuccess) fixture.RemoveWorktree();
                if (LoseRemovalResponse) { LoseRemovalResponse = false; return Task.FromResult(new ProcessResult(1, "", "lost removal response")); }
                return Result("");
            }
            if (args[0] == "status") return Result(Dirty ? " M source.cs\0" : "");
            if (args[0] == "rev-parse")
            {
                var value = args[^1] switch
                {
                    "--git-common-dir" => Common,
                    "--show-toplevel" => fixture.Worktree,
                    "--git-dir" => Path.Combine(Common, "worktrees", "done"),
                    "HEAD" when args.Count > 2 => "nightweaver/done",
                    "HEAD" => Head,
                    _ => throw new InvalidOperationException(string.Join(' ', args))
                };
                if (PauseOnHeadRead && args.SequenceEqual(["rev-parse", "HEAD"])) Paused = true;
                return Result(value);
            }
            throw new InvalidOperationException(string.Join(' ', args));
        }

        private static Task<ProcessResult> Result(string output) => Task.FromResult(new ProcessResult(0, output, ""));
    }
}
