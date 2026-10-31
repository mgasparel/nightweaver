using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class DispatchCliIntegrationTests(PublishedCliArtifactFixture published)
{
    [Fact]
    public async Task PublishedDispatchRunsInferredAndExplicitStagesAndRefusesIneligibleWork()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var rootHead = target.Run("git", ["rev-parse", "HEAD"]).Trim();
        var rootBranch = target.Run("git", ["branch", "--show-current"]).Trim();
        var rootStatus = target.Run("git", ["status", "--porcelain"]);
        var implementer = target.Create("Dispatch implementer", type: "task");

        var inferred = await fixture.RunAsync(["dispatch", implementer, "--project=demo"], "review");
        Assert.Equal(0, inferred.ExitCode);
        Assert.Empty(inferred.StandardError);
        Assert.Contains("role 'implementer' (inferred)", inferred.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Attempt 1/1 started", inferred.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Succeeded:", inferred.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, implementer, "stage:review");
        AssertRetainedExecution(fixture, implementer);

        var explicitDispatch = await fixture.RunAsync(
            ["dispatch", implementer, "--role=reviewer", "--config=" + Path.GetRelativePath(fixture.Caller, fixture.Config)],
            "review-qa");
        Assert.Equal(0, explicitDispatch.ExitCode);
        Assert.Empty(explicitDispatch.StandardError);
        Assert.Contains("role 'reviewer' (explicit)", explicitDispatch.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, implementer, "stage:qa");

        var product = target.Create("Dispatch planner", type: "feature");
        target.Run("bd", ["update", product, "--add-label", "stage:plan"]);
        var planning = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "plan-challenge");
        Assert.Equal(0, planning.ExitCode);
        Assert.Empty(planning.StandardError);
        Assert.Contains("role 'planner' (inferred)", planning.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, product, "stage:plan-challenge");

        var blocker = target.Create("Dispatch blocker", type: "task");
        var blocked = target.Create("Dispatch blocked", type: "task");
        target.Run("bd", ["dep", "add", blocked, blocker]);
        var refused = await fixture.RunAsync(["dispatch", blocked, "--project", "demo"], "review");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("native unassigned ready set", refused.StandardErrorText, StringComparison.Ordinal);
        var blockedAfter = ReadBead(target, blocked);
        Assert.Equal("open", blockedAfter.Status);
        Assert.Null(blockedAfter.Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(blocked)));
        Assert.Equal(rootHead, target.Run("git", ["rev-parse", "HEAD"]).Trim());
        Assert.Equal(rootBranch, target.Run("git", ["branch", "--show-current"]).Trim());
        Assert.Equal(rootStatus, target.Run("git", ["status", "--porcelain"]));
    }

    [Fact]
    public async Task PublishedDispatchEscalatesAnAgentThatExitsWithoutAValidTransition()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var beadId = fixture.Target.Create("Dispatch invalid transition", type: "task");

        var result = await fixture.RunAsync(["dispatch", beadId, "--project", "demo"], "noop");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Escalated to needs-human", result.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains("Attempt 1/1 started", result.StandardOutputText, StringComparison.Ordinal);
        var bead = ReadBead(fixture.Target, beadId);
        Assert.Equal("open", bead.Status);
        Assert.Null(bead.Assignee);
        Assert.Contains("needs-human", bead.Labels);
        Assert.True(Directory.Exists(fixture.WorktreePath(beadId)));
        Assert.Equal("nightweaver/" + beadId,
            fixture.Target.Run("git", ["-C", fixture.WorktreePath(beadId), "branch", "--show-current"]).Trim());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", beadId), "*.log"));
    }

    [Fact]
    public async Task PublishedDispatchExcludesCompetingProjectsAcrossHostRootsAndRecoversAfterProcessDeath()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var beadId = fixture.Target.Create("Process-owned dispatch", type: "task");
        var sameHostClaim = fixture.Target.Create("Other Project stranded claim", actor: "nightweaver:other",
            status: "in_progress", type: "task");
        var remoteClaim = fixture.Target.Create("Other host stranded claim", actor: "nightweaver:remote",
            status: "in_progress", type: "task");
        _ = fixture.AddProject(fixture.Host, "other");
        var aliasPath = Path.Combine(fixture.AliasRoot, "target alias");
        var aliasCreated = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", aliasPath, fixture.Target.Path],
            fixture.Caller, null, TimeSpan.FromSeconds(15));
        Assert.True(aliasCreated.ExitCode == 0,
            $"Could not create the Windows directory-junction identity fixture: {aliasCreated.StandardErrorText}{aliasCreated.StandardOutputText}");
        var aliasClaim = fixture.Target.Create("Physical repository alias claim", actor: "nightweaver:alias",
            status: "in_progress", type: "task");
        if (aliasCreated.ExitCode == 0) _ = fixture.AddProject(fixture.Host, "alias", aliasPath);
        var remoteHost = fixture.AddHost("second published host", "remote", "demo");

        var startedFile = fixture.GateFile("winner started");
        var releaseFile = fixture.GateFile("winner release");
        using var winner = fixture.StartAt(fixture.Host, "demo", ["dispatch", beadId, "--project", "demo"],
            "pool-implement-gated", startedFile, releaseFile);
        await winner.WaitForFileAsync(startedFile, TimeSpan.FromSeconds(30));
        Assert.Equal("in_progress", ReadBead(fixture.Target, beadId).Status);
        Assert.Equal("nightweaver:demo", ReadBead(fixture.Target, beadId).Assignee);

        var logicalBeforeLosers = CaptureLogicalState(fixture.Target);
        var sameHostBefore = SnapshotOtherProjectRuntime(fixture.Host, "other");
        var remoteHostBefore = Snapshot.FileTree(remoteHost);
        var sameHostLoser = await fixture.RunAtAsync(fixture.Host, "other",
            ["dispatch", sameHostClaim, "--project", "other"], "noop");
        Assert.Equal(1, sameHostLoser.ExitCode);
        Assert.Contains("another Nightweaver Orchestrator already owns", sameHostLoser.StandardErrorText, StringComparison.Ordinal);
        Assert.Equal("in_progress", ReadBead(fixture.Target, sameHostClaim).Status);
        Assert.Equal("nightweaver:other", ReadBead(fixture.Target, sameHostClaim).Assignee);
        Assert.Equal(logicalBeforeLosers, CaptureLogicalState(fixture.Target));
        Assert.Equal(sameHostBefore, SnapshotOtherProjectRuntime(fixture.Host, "other"));

        if (aliasCreated.ExitCode == 0)
        {
            var aliasLoser = await fixture.RunAtAsync(fixture.Host, "alias",
                ["dispatch", aliasClaim, "--project", "alias"], "noop");
            Assert.Equal(1, aliasLoser.ExitCode);
            Assert.Contains("another Nightweaver Orchestrator already owns", aliasLoser.StandardErrorText, StringComparison.Ordinal);
            Assert.Equal("in_progress", ReadBead(fixture.Target, aliasClaim).Status);
            Assert.Equal("nightweaver:alias", ReadBead(fixture.Target, aliasClaim).Assignee);
            Assert.Equal(logicalBeforeLosers, CaptureLogicalState(fixture.Target));
        }

        var remoteLoser = await fixture.RunAtAsync(remoteHost, "remote",
            ["dispatch", remoteClaim, "--project", "remote"], "noop");
        Assert.Equal(1, remoteLoser.ExitCode);
        Assert.Contains("another Nightweaver Orchestrator already owns", remoteLoser.StandardErrorText, StringComparison.Ordinal);
        Assert.Equal("in_progress", ReadBead(fixture.Target, remoteClaim).Status);
        Assert.Equal("nightweaver:remote", ReadBead(fixture.Target, remoteClaim).Assignee);
        Assert.Equal(logicalBeforeLosers, CaptureLogicalState(fixture.Target));
        Assert.Equal(remoteHostBefore, Snapshot.FileTree(remoteHost));

        var dryRun = await fixture.RunAtAsync(fixture.Host, "demo", ["--project", "demo", "--dry-run"], "noop");
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(logicalBeforeLosers, CaptureLogicalState(fixture.Target));
        Assert.Equal("in_progress", ReadBead(fixture.Target, beadId).Status);
        Assert.Equal("nightweaver:demo", ReadBead(fixture.Target, beadId).Assignee);

        winner.KillTree();
        _ = await winner.WaitAsync(TimeSpan.FromSeconds(15));

        var secondHostRecovery = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", beadId, "--project", "demo"], "review");
        Assert.True(secondHostRecovery.ExitCode == 0,
            $"Second-host recovery failed (exit {secondHostRecovery.ExitCode}).\nstdout:\n{secondHostRecovery.StandardOutputText}\nstderr:\n{secondHostRecovery.StandardErrorText}");
        Assert.Contains("role 'implementer' (inferred)", secondHostRecovery.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Succeeded:", secondHostRecovery.StandardOutputText, StringComparison.Ordinal);
        Assert.Equal("open", ReadBead(fixture.Target, beadId).Status);
        Assert.Null(ReadBead(fixture.Target, beadId).Assignee);
        Assert.Contains("stage:review", ReadBead(fixture.Target, beadId).Labels);
        Assert.True(Directory.Exists(Path.Combine(fixture.Host, "worktrees", "demo", beadId)));
    }

    [Fact]
    public async Task PublishedDispatchRecoversOnlyExactProjectClaimsAndAdmitsFromFreshReadiness()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        const string actor = "nightweaver:demo";
        var requested = target.Create("Recovered dispatch candidate", actor, "in_progress", "task", labels: ["stage:implement"]);
        var paused = target.Create("Paused stranded claim", actor, "in_progress", "task",
            labels: ["stage:qa", "needs-human", "product-decision-required"]);
        target.Run("bd", ["update", paused, "--set-metadata", "repair_cycles=3", "--set-metadata", "preserved=metadata"]);
        target.Run("bd", ["comments", "add", paused, "Retain this recovery evidence."]);

        var blocker = target.Create("Open blocker");
        var blocked = target.Create("Blocked stranded claim", actor, "in_progress", "task", labels: ["stage:review"]);
        target.Run("bd", ["update", blocked, "--set-metadata", "repair_cycles=1"]);
        target.Run("bd", ["dep", "add", blocked, blocker]);

        var foreign = target.Create("Foreign stranded claim", "person@example.invalid", "in_progress", "task");
        var unassigned = target.Create("Unassigned in progress", status: "in_progress", type: "task");
        var lookalike = target.Create("Actor prefix lookalike", "nightweaver:demo-other", "in_progress", "task");
        var casing = target.Create("Actor case difference", "Nightweaver:demo", "in_progress", "task");
        var otherProject = target.Create("Other Project stranded claim", "nightweaver:other", "in_progress", "task");
        _ = fixture.AddProject(fixture.Host, "other");

        var otherWorktree = Path.Combine(fixture.Host, "worktrees", "other", otherProject);
        var otherTmp = Path.Combine(fixture.Host, "tmp", "other", otherProject);
        Directory.CreateDirectory(otherWorktree);
        Directory.CreateDirectory(otherTmp);
        File.WriteAllText(Path.Combine(otherWorktree, "retained.txt"), "keep the other Project environment\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(otherTmp, "retained.tmp"), "keep the other Project tmp\n", new UTF8Encoding(false));
        var otherProjectEnvironment = string.Join("\n", Snapshot.FileTree(otherWorktree), Snapshot.FileTree(otherTmp));

        var before = new Dictionary<string, BeadSnapshot>(StringComparer.Ordinal)
        {
            [paused] = ReadBead(target, paused),
            [blocked] = ReadBead(target, blocked),
            [foreign] = ReadBead(target, foreign),
            [unassigned] = ReadBead(target, unassigned),
            [lookalike] = ReadBead(target, lookalike),
            [casing] = ReadBead(target, casing),
            [otherProject] = ReadBead(target, otherProject)
        };

        var result = await fixture.RunAsync(["dispatch", requested, "--project", "demo"], "review");
        Assert.True(result.ExitCode == 0,
            $"Startup recovery dispatch failed (exit {result.ExitCode}).\nstdout:\n{result.StandardOutputText}\nstderr:\n{result.StandardErrorText}");
        Assert.Contains("role 'implementer' (inferred)", result.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Succeeded:", result.StandardOutputText, StringComparison.Ordinal);

        AssertRecoveredWithFieldsPreserved(before[paused], ReadBead(target, paused), actor);
        Assert.Contains("stage:qa", ReadBead(target, paused).Labels);
        Assert.Contains("needs-human", ReadBead(target, paused).Labels);
        Assert.Contains("product-decision-required", ReadBead(target, paused).Labels);
        Assert.Equal(3, ReadBead(target, paused).Payload.GetProperty("metadata").GetProperty("repair_cycles").GetInt32());
        AssertRecoveredWithFieldsPreserved(before[blocked], ReadBead(target, blocked), actor);
        AssertRecoveredWithFieldsPreserved(before[foreign], ReadBead(target, foreign), "person@example.invalid", recovered: false);
        AssertRecoveredWithFieldsPreserved(before[unassigned], ReadBead(target, unassigned), null, recovered: false);
        AssertRecoveredWithFieldsPreserved(before[lookalike], ReadBead(target, lookalike), "nightweaver:demo-other", recovered: false);
        AssertRecoveredWithFieldsPreserved(before[casing], ReadBead(target, casing), "Nightweaver:demo", recovered: false);
        AssertRecoveredWithFieldsPreserved(before[otherProject], ReadBead(target, otherProject), "nightweaver:other", recovered: false);

        var completedRequested = ReadBead(target, requested);
        Assert.Equal("open", completedRequested.Status);
        Assert.Null(completedRequested.Assignee);
        Assert.Contains("stage:review", completedRequested.Labels);
        Assert.Equal(otherProjectEnvironment,
            string.Join("\n", Snapshot.FileTree(otherWorktree), Snapshot.FileTree(otherTmp)));
    }

    [Fact]
    public async Task PublishedDispatchStopsOnRequiredTrackerAndSharedRootFailures()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;

        var readFailureBead = target.Create("Tracker read failure must stop startup");
        var readFailure = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", readFailureBead, "--project", "demo"], "review", "read-failure");
        Assert.Equal(1, readFailure.ExitCode);
        Assert.Contains("fixture tracker read failure", readFailure.StandardErrorText, StringComparison.Ordinal);
        Assert.Equal("open", ReadBead(target, readFailureBead).Status);
        Assert.Null(ReadBead(target, readFailureBead).Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(readFailureBead)));

        var writeFailureBead = target.Create("Tracker recovery write failure must stop startup",
            "nightweaver:demo", "in_progress", "task", labels: ["stage:implement"]);
        var writeFailure = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", writeFailureBead, "--project", "demo"], "review", "recovery-write-failure", writeFailureBead);
        Assert.Equal(1, writeFailure.ExitCode);
        Assert.Contains("fixture recovery write failure", writeFailure.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains("fresh state remains status 'in_progress'", writeFailure.StandardErrorText, StringComparison.Ordinal);
        Assert.Equal("in_progress", ReadBead(target, writeFailureBead).Status);
        Assert.Equal("nightweaver:demo", ReadBead(target, writeFailureBead).Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(writeFailureBead)));

        _ = fixture.AddProject(fixture.Host, "broken");
        File.WriteAllText(Path.Combine(fixture.Host, "worktrees", "broken"), "not a directory", new UTF8Encoding(false));
        var rootFailureBead = target.Create("Unusable shared root must stop startup");
        var logicalBeforeRootFailure = CaptureLogicalState(target);
        var rootFailure = await fixture.RunAtAsync(fixture.Host, "broken",
            ["dispatch", rootFailureBead, "--project", "broken"], "review");
        Assert.Equal(1, rootFailure.ExitCode);
        Assert.Contains("shared runtime root", rootFailure.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(fixture.Host, "worktrees", "broken"), rootFailure.StandardErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(logicalBeforeRootFailure, CaptureLogicalState(target));
        Assert.Equal("open", ReadBead(target, rootFailureBead).Status);
        Assert.Null(ReadBead(target, rootFailureBead).Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(rootFailureBead)));
    }

    [Fact]
    public async Task PublishedRecoveryRereadsGuardMismatchAndUncertainWritesWithoutReplay()
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;

        var changedByForeignWriter = target.Create("Guard mismatch claim", "nightweaver:demo", "in_progress", "task");
        var requestedAfterMismatch = target.Create("Ordinary admission after guard mismatch");
        var mismatch = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", requestedAfterMismatch, "--project", "demo"], "review", "recovery-guard-mismatch", changedByForeignWriter);
        Assert.True(mismatch.ExitCode == 0,
            $"Guard mismatch run failed (exit {mismatch.ExitCode}).\nstdout:\n{mismatch.StandardOutputText}\nstderr:\n{mismatch.StandardErrorText}");
        var changedState = ReadBead(target, changedByForeignWriter);
        Assert.Equal("closed", changedState.Status);
        Assert.Equal("nightweaver:demo", changedState.Assignee);

        var changedAssignee = target.Create("Assignee changed before recovery write", "nightweaver:demo", "in_progress", "task");
        var requestedAfterAssigneeChange = target.Create("Ordinary admission after assignee guard mismatch");
        var assigneeMismatch = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", requestedAfterAssigneeChange, "--project", "demo"], "review", "recovery-assignee-mismatch", changedAssignee);
        Assert.True(assigneeMismatch.ExitCode == 0,
            $"Assignee mismatch run failed (exit {assigneeMismatch.ExitCode}).\nstdout:\n{assigneeMismatch.StandardOutputText}\nstderr:\n{assigneeMismatch.StandardErrorText}");
        var changedOwner = ReadBead(target, changedAssignee);
        Assert.Equal("in_progress", changedOwner.Status);
        Assert.Equal("person@example.invalid", changedOwner.Assignee);

        var pausedByLabelRace = target.Create("Pause label added during recovery", "nightweaver:demo", "in_progress", "task",
            labels: ["stage:qa"]);
        target.Run("bd", ["update", pausedByLabelRace, "--set-metadata", "repair_cycles=2"]);
        var requestedAfterLabelRace = target.Create("Ordinary admission after label-only race");
        var labelRace = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", requestedAfterLabelRace, "--project", "demo"], "review", "recovery-label-race", pausedByLabelRace);
        Assert.True(labelRace.ExitCode == 0,
            $"Label-only race run failed (exit {labelRace.ExitCode}).\nstdout:\n{labelRace.StandardOutputText}\nstderr:\n{labelRace.StandardErrorText}");
        var releasedPaused = ReadBead(target, pausedByLabelRace);
        Assert.Equal("open", releasedPaused.Status);
        Assert.Null(releasedPaused.Assignee);
        Assert.Contains("stage:qa", releasedPaused.Labels);
        Assert.Contains("needs-human", releasedPaused.Labels);
        Assert.Equal(2, releasedPaused.Payload.GetProperty("metadata").GetProperty("repair_cycles").GetInt32());

        var uncertainWrite = target.Create("Write committed before lost result", "nightweaver:demo", "in_progress", "task",
            labels: ["stage:review"]);
        var requestedAfterUncertain = target.Create("Ordinary admission after uncertain write");
        var shimLog = fixture.GateFile("uncertain recovery bd calls.jsonl");
        var uncertain = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", requestedAfterUncertain, "--project", "demo"], "review", "recovery-uncertain-write", uncertainWrite, shimLog);
        Assert.True(uncertain.ExitCode == 0,
            $"Uncertain-write run failed (exit {uncertain.ExitCode}).\nstdout:\n{uncertain.StandardOutputText}\nstderr:\n{uncertain.StandardErrorText}");
        var rereadReleased = ReadBead(target, uncertainWrite);
        Assert.Equal("open", rereadReleased.Status);
        Assert.Null(rereadReleased.Assignee);
        var recoveryCommandCount = File.ReadAllLines(shimLog)
            .Count(line => line.Contains(uncertainWrite, StringComparison.Ordinal)
                && line.Contains("--if-status", StringComparison.Ordinal)
                && line.Contains("in_progress", StringComparison.Ordinal));
        Assert.Equal(1, recoveryCommandCount);
    }

    private static void AssertSuccessfulHandoff(BeadsFixture target, string beadId, string expectedStage)
    {
        var bead = ReadBead(target, beadId);
        Assert.Equal("open", bead.Status);
        Assert.Null(bead.Assignee);
        Assert.Contains(expectedStage, bead.Labels);
    }

    private static BeadSnapshot ReadBead(BeadsFixture target, string beadId) =>
        BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments)).ById[beadId];

    private static string CaptureLogicalState(BeadsFixture target)
    {
        var lines = target.Run("bd", ["--readonly", "--sandbox", "export", "--all"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var document = JsonDocument.Parse("[" + string.Join(',', lines) + "]");
        return CanonicalJson(document.RootElement);
    }

    private static string SnapshotOtherProjectRuntime(string host, string project) => string.Join("\n",
        Snapshot.Location(Path.Combine(host, "worktrees", project)),
        Snapshot.Location(Path.Combine(host, "tmp", project)),
        Snapshot.Location(Path.Combine(host, "logs", project)));

    private static void AssertRecoveredWithFieldsPreserved(BeadSnapshot before, BeadSnapshot after,
        string? expectedAssignee, bool recovered = true)
    {
        Assert.Equal(recovered ? "open" : before.Status, after.Status);
        Assert.Equal(recovered ? null : expectedAssignee, after.Assignee);
        Assert.Equal(CanonicalPayloadExceptMutable(before.Payload), CanonicalPayloadExceptMutable(after.Payload));
    }

    private static string CanonicalPayloadExceptMutable(JsonElement payload)
    {
        var properties = payload.EnumerateObject()
            .Where(property => property.Name is not ("status" or "assignee" or "updated_at"))
            .OrderBy(property => property.Name, StringComparer.Ordinal);
        return "{" + string.Join(',', properties.Select(property =>
            JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}";
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', element.EnumerateArray().Select(CanonicalJson).Order(StringComparer.Ordinal)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => throw new InvalidOperationException($"Unsupported JSON value kind {element.ValueKind}")
    };

    private static void AssertRetainedExecution(LiveDispatchFixture fixture, string beadId)
    {
        Assert.True(Directory.Exists(fixture.WorktreePath(beadId)));
        Assert.Equal("nightweaver/" + beadId, fixture.Target.Run("git", ["-C", fixture.WorktreePath(beadId), "branch", "--show-current"]).Trim());
        Assert.True(Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", beadId), "*.log").Any());
    }

    private sealed class LiveDispatchFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private readonly string fakeCliDirectory;
        private readonly string bdShimDirectory;
        private readonly string realBdPath;
        private readonly PublishedCliArtifactFixture published;
        private readonly string origin;

        internal BeadsFixture Target { get; } = new("target repository with spaces");
        internal string Host { get; }
        internal string Caller { get; }
        internal string Config { get; }
        internal string AliasRoot { get; }

        internal LiveDispatchFixture(PublishedCliArtifactFixture published)
        {
            this.published = published;
            Host = temp.CreateDirectory("nightweaver host with spaces");
            Caller = temp.CreateDirectory("unrelated caller with spaces");
            AliasRoot = temp.CreateDirectory("repository aliases");
            published.CopyPublishedAppTo(Host);
            Config = AddProject(Host, "demo");
            Target.Run("git", ["config", "user.name", "Nightweaver Dispatch Fixture"]);
            Target.Run("git", ["config", "user.email", "fixture@example.invalid"]);
            File.WriteAllText(Path.Combine(Target.Path, "README.md"), "dispatch base\n", new UTF8Encoding(false));
            Target.Run("git", ["add", "README.md"]);
            Target.Run("git", ["commit", "-m", "dispatch fixture base"]);
            origin = Path.Combine(Target.Root, "bare origin.git");
            Directory.CreateDirectory(origin);
            Target.Run("git", ["init", "--bare", origin]);
            Target.Run("git", ["remote", "add", "origin", origin]);
            Target.Run("git", ["push", "--set-upstream", "origin", "main"]);

            fakeCliDirectory = temp.CreateDirectory("native fake agent cli");
            CopyFixtureCli("AgentInvocationFixture.exe");
            CopyFixtureCli("AgentInvocationFixture.dll");
            CopyFixtureCli("AgentInvocationFixture.deps.json");
            CopyFixtureCli("AgentInvocationFixture.runtimeconfig.json");

            bdShimDirectory = temp.CreateDirectory("native bd shim");
            foreach (var name in new[] { "AgentInvocationFixture.exe", "AgentInvocationFixture.dll",
                         "AgentInvocationFixture.deps.json", "AgentInvocationFixture.runtimeconfig.json" })
                File.Copy(Path.Combine(fakeCliDirectory, name), Path.Combine(bdShimDirectory, name), overwrite: true);
            File.Copy(Path.Combine(fakeCliDirectory, "AgentInvocationFixture.exe"), Path.Combine(bdShimDirectory, "bd.exe"), overwrite: true);
            realBdPath = FindExecutable("bd");
        }

        internal string WorktreePath(string beadId) => Path.Combine(Host, "worktrees", "demo", beadId);

        internal async Task<RawProcessResult> RunAsync(IReadOnlyList<string> arguments, string action)
            => await RunAtAsync(Host, "demo", arguments, action);

        internal Task<RawProcessResult> RunAtAsync(string host, string project, IReadOnlyList<string> arguments,
            string action, string? bdShimMode = null, string? bdShimTarget = null, string? bdShimLog = null)
        {
            var environment = LaunchEnvironment(project, action, bdShimMode, bdShimTarget, bdShimLog);
            var executable = Path.Combine(host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
            return ProcessTool.RunAsync(executable, arguments, Caller, environment, TimeSpan.FromMinutes(2));
        }

        internal RunningProcess StartAt(string host, string project, IReadOnlyList<string> arguments, string action,
            string startedFile, string releaseFile)
        {
            var environment = LaunchEnvironment(project, action, null, null, null);
            environment["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = startedFile;
            environment["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = releaseFile;
            var executable = Path.Combine(host, OperatingSystem.IsWindows() ? "nightweaver.exe" : "nightweaver");
            return ProcessTool.Start(executable, arguments, Caller, environment);
        }

        internal string AddProject(string host, string project, string? targetPath = null)
        {
            Directory.CreateDirectory(Path.Combine(host, "projects"));
            var prompts = Path.Combine(host, "prompts");
            Directory.CreateDirectory(prompts);
            foreach (var role in new[] { "implementer", "reviewer", "planner", "plan-challenger", "qa", "merger" })
                if (!File.Exists(Path.Combine(prompts, role + ".md")))
                    File.WriteAllText(Path.Combine(prompts, role + ".md"), $"Fixture prompt for {role}.\n", new UTF8Encoding(false));
            var config = Path.Combine(host, "projects", project + ".json");
            var json = JsonSerializer.Serialize(new
            {
                name = project,
                target_repository = new { path = targetPath ?? Target.Path, default_branch = "main" },
                limits = new { max_operational_retries = 0 },
                agent_defaults = new { cli = "claude", effort = "low", timeout_minutes = 1 }
            });
            File.WriteAllText(config, json, new UTF8Encoding(false));
            return config;
        }

        internal string AddHost(string name, params string[] projects)
        {
            var host = temp.CreateDirectory(name);
            published.CopyPublishedAppTo(host);
            foreach (var project in projects) AddProject(host, project);
            return host;
        }

        internal string GateFile(string name) => Path.Combine(temp.CreateDirectory("process gates"), name);

        private Dictionary<string, string?> LaunchEnvironment(string project, string action,
            string? bdShimMode, string? bdShimTarget, string? bdShimLog)
        {
            var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var environment = new Dictionary<string, string?>(Target.Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["BD_ACTOR"] = "nightweaver:" + project,
                ["NIGHTWEAVER_FIXTURE_ACTION"] = action,
                ["PATH"] = fakeCliDirectory + Path.PathSeparator + inheritedPath
            };
            if (bdShimMode is not null)
            {
                environment["PATH"] = bdShimDirectory + Path.PathSeparator + fakeCliDirectory + Path.PathSeparator + inheritedPath;
                environment["NIGHTWEAVER_BD_REAL_PATH"] = realBdPath;
                environment["NIGHTWEAVER_BD_SHIM_MODE"] = bdShimMode;
                environment["NIGHTWEAVER_BD_SHIM_TARGET"] = bdShimTarget;
                environment["NIGHTWEAVER_BD_SHIM_LOG"] = bdShimLog;
            }
            return environment;
        }

        private static string FindExecutable(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim('"'), name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new FileNotFoundException($"Required native executable '{name}' was not found on PATH.");
        }

        private void CopyFixtureCli(string name)
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException($"Native Agent fixture component '{name}' was not built.", source);
            File.Copy(source, Path.Combine(fakeCliDirectory, name), overwrite: true);
            if (name == "AgentInvocationFixture.exe")
            {
                File.Copy(source, Path.Combine(fakeCliDirectory, "claude.exe"), overwrite: true);
                File.Copy(source, Path.Combine(fakeCliDirectory, "codex.exe"), overwrite: true);
            }
        }

        public void Dispose()
        {
            temp.Dispose();
            Target.Dispose();
        }
    }
}
