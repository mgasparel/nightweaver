using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

internal static class DispatchScenarioSupport
{
    internal static async Task PublishedDispatchRunsInferredAndExplicitStagesAndRefusesIneligibleWork(PublishedCliArtifactFixture published)
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var rootHead = target.Run("git", ["rev-parse", "HEAD"]).Trim();
        var rootBranch = target.Run("git", ["branch", "--show-current"]).Trim();
        var rootStatus = target.Run("git", ["status", "--porcelain"]);
        var implementer = target.Create("Dispatch implementer", type: "task");

        var inferred = await fixture.RunAsync(["dispatch", implementer, "--project=demo"], "review");
        Assert.True(inferred.ExitCode == 0,
            $"Dispatch failed (exit {inferred.ExitCode}).\\nstdout:\\n{inferred.StandardOutputText}\\nstderr:\\n{inferred.StandardErrorText}");
        Assert.True(inferred.StandardError.Length == 0,
            $"Unexpected startup diagnostic: {inferred.StandardErrorText}\nstdout:\n{inferred.StandardOutputText}");
        Assert.Contains("role 'implementer' (inferred)", inferred.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Attempt 1/1 started", inferred.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Succeeded:", inferred.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, implementer, "stage:review");
        AssertRetainedExecution(fixture, implementer);

        var explicitDispatch = await fixture.RunAsync(
            ["dispatch", implementer, "--role=reviewer", "--config=" + Path.GetRelativePath(fixture.Caller, fixture.Config)],
            "slot-review");
        Assert.True(explicitDispatch.ExitCode == 0,
            $"Explicit Reviewer dispatch failed (exit {explicitDispatch.ExitCode}).\\nstdout:\\n{explicitDispatch.StandardOutputText}\\nstderr:\\n{explicitDispatch.StandardErrorText}");
        Assert.Empty(explicitDispatch.StandardError);
        Assert.Contains("role 'reviewer' (explicit)", explicitDispatch.StandardOutputText, StringComparison.Ordinal);
        AssertSuccessfulHandoff(target, implementer, "stage:qa");

        var product = target.Create("Dispatch planner", type: "feature");
        target.Run("bd", ["update", product, "--add-label", "stage:plan"]);
        var planning = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "plan-challenge");
        Assert.True(planning.ExitCode == 0,
            $"Planner dispatch failed (exit {planning.ExitCode}).\nstdout:\n{planning.StandardOutputText}\nstderr:\n{planning.StandardErrorText}");
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

    internal static async Task PublishedDispatchMaterializesApprovedPlanAndReleasesClaimExactlyOnce(PublishedCliArtifactFixture published)
    {
        using var fixture = new LiveDispatchFixture(published);
        var product = fixture.Target.Create("Approved materialization", type: "bug");
        fixture.Target.Run("bd", ["update", product, "--add-label", "stage:plan-approved"]);
        var baseline = ReadInventory(fixture.Target).ById.Keys.ToHashSet(StringComparer.Ordinal);
        var result = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "planned-materialization");
        Assert.True(result.ExitCode == 0, result.StandardErrorText);
        AssertSuccessfulHandoff(fixture.Target, product, "planned");
        Assert.DoesNotContain("stage:plan-approved", ReadBead(fixture.Target, product).Labels);
        var inventory = BeadsReader.Parse(fixture.Target.Run("bd", BeadsCommand.Create(fixture.Target.Path).Arguments));
        var child = Assert.Single(inventory.ById.Values, bead => bead.IssueType == "task" && !baseline.Contains(bead.Id));
        Assert.Equal("open", child.Status);
        var dependencies = fixture.Target.Run("bd", ["dep", "list", product, "--direction", "up", "--json"]);
        Assert.Contains(child.Id, dependencies, StringComparison.Ordinal);
        Assert.Contains("discovered-from", dependencies, StringComparison.Ordinal);
        var repeated = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "planned-materialization");
        Assert.Equal(1, repeated.ExitCode);
        inventory = BeadsReader.Parse(fixture.Target.Run("bd", BeadsCommand.Create(fixture.Target.Path).Arguments));
        Assert.Single(inventory.ById.Values, bead => bead.IssueType == "task" && !baseline.Contains(bead.Id));
    }

    internal static async Task PlannedHandoffRefusesChangedOwnerAndPreservesEvidence(PublishedCliArtifactFixture published)
    {
        using var fixture = new LiveDispatchFixture(published);
        var product = fixture.Target.Create("Materialization owner race", type: "bug");
        fixture.Target.Run("bd", ["update", product, "--add-label", "stage:plan-approved"]);
        var result = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "planned-stolen-owner");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("claim ownership changed before handoff", result.StandardErrorText, StringComparison.Ordinal);
        var bead = ReadBead(fixture.Target, product);
        Assert.Equal("in_progress", bead.Status);
        Assert.Equal("other-owner", bead.Assignee);
        Assert.Contains("planned", bead.Labels);
        Assert.True(Directory.Exists(fixture.WorktreePath(product)));
    }

    internal static async Task FailedMaterializationRetainsEvidenceAndNeverReplays(PublishedCliArtifactFixture published, string action, string label, string failure)
    {
        using var fixture = new LiveDispatchFixture(published, retries: 2);
        var product = fixture.Target.Create("Interrupted materialization", type: "feature");
        fixture.Target.Run("bd", ["update", product, "--add-label", "stage:plan-approved"]);
        var baseline = ReadInventory(fixture.Target).ById.Keys.ToHashSet(StringComparer.Ordinal);
        var result = await fixture.RunAsync(["dispatch", product, "--project", "demo"], action);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("requires explicit recovery after attempt 1", result.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains(failure, result.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains("log '", result.StandardErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot escalate", result.StandardErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("Attempt 2/", result.StandardOutputText, StringComparison.Ordinal);
        var inventory = ReadInventory(fixture.Target);
        var bead = inventory.ById[product];
        Assert.Equal("in_progress", bead.Status);
        Assert.Equal("nightweaver:demo", bead.Assignee);
        Assert.Contains(label, bead.Labels);
        var child = Assert.Single(inventory.ById.Values, b => !baseline.Contains(b.Id));
        Assert.Equal("task", child.IssueType);
        Assert.True(Directory.Exists(fixture.WorktreePath(product)));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", product), "*.log"));
        var root = Path.GetDirectoryName(fixture.WorktreePath(product))!;
        var tmpRoot = Path.Combine(fixture.Host, "tmp", "demo");
        var startup = StartupPlanner.Plan("nightweaver:demo", inventory,
            [new GitWorktreeSnapshot(fixture.WorktreePath(product), fixture.WorktreePath(product), null, null, true, false, false, null, false, null)], RuntimeRoot(root, product),
            RuntimeRoot(tmpRoot, product));
        Assert.DoesNotContain(startup.Releases, release => release.BeadId == product);
        Assert.Empty(startup.Removals);
        Assert.Empty(startup.OrphanTmp);
        Assert.False(Assert.Single(startup.SurvivingWorktrees).Reusable);
        Assert.Contains(startup.Diagnostics, d => d.Code == StartupDiagnosticCode.PlanningMaterializationRecoveryRequired);
        // Launch an unrelated native dispatch to exercise live startup recovery and cleanup,
        // rather than relying only on the pure StartupPlan projection above.
        var tmpPath = Path.Combine(tmpRoot, product);
        Directory.CreateDirectory(tmpPath);
        var sentinel = Path.Combine(tmpPath, "recovery-evidence.txt");
        File.WriteAllText(sentinel, "retain interrupted materialization");
        var unrelated = fixture.Target.Create("Unrelated dispatch after materialization failure", type: "task");
        baseline.Add(unrelated);
        var restarted = await fixture.RunAsync(["dispatch", unrelated, "--project", "demo"], "review");
        Assert.True(restarted.ExitCode == 0, restarted.StandardErrorText);
        var afterStartup = ReadBead(fixture.Target, product);
        Assert.Equal("in_progress", afterStartup.Status);
        Assert.Equal("nightweaver:demo", afterStartup.Assignee);
        Assert.Contains(label, afterStartup.Labels);
        Assert.True(Directory.Exists(fixture.WorktreePath(product)));
        Assert.Equal("retain interrupted materialization", File.ReadAllText(sentinel));
        Assert.Single(ReadInventory(fixture.Target).ById.Values, b => !baseline.Contains(b.Id));

        if (label == "stage:plan-approved")
        {
            // Even an explicit release cannot authorize replay over existing children.
            fixture.Target.Run("bd", ["update", product, "--status", "open", "--assignee", "",
                "--if-assignee", "nightweaver:demo", "--if-status", "in_progress"]);
            var retry = await fixture.RunAsync(["dispatch", product, "--project", "demo"], "planned-materialization");
            Assert.Equal(1, retry.ExitCode);
            Assert.Contains("already has materialized children", retry.StandardErrorText, StringComparison.Ordinal);
            Assert.Single(ReadInventory(fixture.Target).ById.Values, b => !baseline.Contains(b.Id));
        }
    }

    private static RuntimeDirectorySnapshot RuntimeRoot(string root, string id) =>
        new(root, DirectoryState.Present, [new(id, Path.Combine(root, id), DirectoryState.Present)]);

    private static BeadsInventory ReadInventory(BeadsFixture target) =>
        BeadsReader.Parse(target.Run("bd", BeadsCommand.Create(target.Path).Arguments));

    internal static Task DispatchTargetCopiesKeepTrackerAndGitOriginChangesInsideTheirOwnRoot()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;

        using var first = DispatchRepositoryTemplate.CreateCopy("first dispatch target");
        using var second = DispatchRepositoryTemplate.CreateCopy("second dispatch target");
        Assert.NotEqual(first.Target.Path, second.Target.Path);
        Assert.NotEqual(first.Origin, second.Origin);
        Assert.Equal(first.Target.ControlBeadId, second.Target.ControlBeadId);
        var seedSetupCounts = DispatchRepositoryTemplate.SeedToolRunCounts;
        var firstCopySetupCounts = first.Target.ToolRunCounts;
        var secondCopySetupCounts = second.Target.ToolRunCounts;
        Assert.Equal(7, seedSetupCounts.GetValueOrDefault("git"));
        Assert.Equal(1, firstCopySetupCounts.GetValueOrDefault("git"));
        Assert.Equal(1, secondCopySetupCounts.GetValueOrDefault("git"));
        Assert.False(firstCopySetupCounts.ContainsKey("bd"));
        Assert.False(secondCopySetupCounts.ContainsKey("bd"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Dispatch template setup tools: seed git={seedSetupCounts.GetValueOrDefault("git")}; " +
            $"copy one git origin rewrite each; no per-copy bd setup.");

        var firstMainBefore = first.Target.Run("git", ["--git-dir", first.Origin, "rev-parse", "refs/heads/main"]).Trim();
        var secondMainBefore = second.Target.Run("git", ["--git-dir", second.Origin, "rev-parse", "refs/heads/main"]).Trim();
        Assert.Equal(firstMainBefore, secondMainBefore);

        var onlyFirst = first.Target.Create("Dispatch template mutation only in first copy");
        Assert.DoesNotContain(onlyFirst, ReadInventory(second.Target).ById.Keys);

        var marker = Path.Combine(first.Target.Path, "independent-dispatch-origin.txt");
        File.WriteAllText(marker, "first dispatch origin only\n", new System.Text.UTF8Encoding(false));
        first.Target.Run("git", ["add", "--", "independent-dispatch-origin.txt"]);
        first.Target.Run("git", ["commit", "-m", "verify independent dispatch origin"]);
        first.Target.Run("git", ["push", "origin", "main"]);

        var firstMainAfter = first.Target.Run("git", ["--git-dir", first.Origin, "rev-parse", "refs/heads/main"]).Trim();
        var secondMainAfter = second.Target.Run("git", ["--git-dir", second.Origin, "rev-parse", "refs/heads/main"]).Trim();
        Assert.NotEqual(firstMainBefore, firstMainAfter);
        Assert.Equal(secondMainBefore, secondMainAfter);
        return Task.CompletedTask;
    }

    internal static async Task PublishedDispatchEscalatesAnAgentThatExitsWithoutAValidTransition(PublishedCliArtifactFixture published)
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
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", beadId), "*.log").Count());
    }

    internal static async Task PublishedStartupRemovesOnlyCompletedManagedWorktreesAndKeepsBranchesAndSurvivors(PublishedCliArtifactFixture published)
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var planning = target.Create("Completed Product planning", type: "feature", labels: ["planned"]);
        var delivery = target.Create("Closed Delivery plan", type: "task");
        target.Run("bd", ["close", delivery]);
        var paused = target.Create("Paused completed Delivery plan", type: "task");
        target.Run("bd", ["close", paused]);
        target.Run("bd", ["update", paused, "--add-label", "needs-human"]);
        var active = target.Create("Unfinished Delivery plan", type: "task", labels: ["stage:review"]);
        var unrelated = target.Create("Unrelated dispatch after cleanup");

        AddDetachedWorktree(target, fixture.WorktreePath(planning));
        AddBranchWorktree(target, fixture.WorktreePath(delivery), delivery);
        AddBranchWorktree(target, fixture.WorktreePath(paused), paused);
        AddBranchWorktree(target, fixture.WorktreePath(active), active);
        var tmpPaths = new[] { planning, delivery, paused, active, "deleted-bead" }
            .ToDictionary(id => id, id => Path.Combine(fixture.Host, "tmp", "demo", id), StringComparer.Ordinal);
        foreach (var (id, path) in tmpPaths)
        {
            Directory.CreateDirectory(Path.Combine(path, "invocation"));
            File.WriteAllText(Path.Combine(path, "invocation", "sentinel.txt"), "preserve unless completed and owned");
        }
        var deletedPath = Path.Combine(fixture.Host, "worktrees", "demo", "deleted-bead");
        Directory.CreateDirectory(deletedPath);
        File.WriteAllText(Path.Combine(deletedPath, "sentinel.txt"), "deleted Bead leftover");

        var dispatch = await fixture.RunAsync(["dispatch", unrelated, "--project", "demo"], "review");

        Assert.True(dispatch.ExitCode == 0,
            $"Startup cleanup dispatch failed (exit {dispatch.ExitCode}).\nstdout:\n{dispatch.StandardOutputText}\nstderr:\n{dispatch.StandardErrorText}");
        Assert.Contains("Succeeded:", dispatch.StandardOutputText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.WorktreePath(planning)));
        Assert.False(Directory.Exists(fixture.WorktreePath(delivery)));
        Assert.False(Directory.Exists(tmpPaths[planning]));
        Assert.False(Directory.Exists(tmpPaths[delivery]));
        Assert.True(target.Run("git", ["show-ref", "--verify", "refs/heads/nightweaver/" + delivery]).Contains(delivery, StringComparison.Ordinal));
        Assert.True(Directory.Exists(fixture.WorktreePath(paused)));
        Assert.True(Directory.Exists(tmpPaths[paused]));
        Assert.True(Directory.Exists(fixture.WorktreePath(active)));
        Assert.True(Directory.Exists(tmpPaths[active]));
        Assert.True(Directory.Exists(deletedPath));
        Assert.True(Directory.Exists(tmpPaths["deleted-bead"]));
        Assert.Equal("closed", ReadBead(target, delivery).Status);
        Assert.Equal("closed", ReadBead(target, paused).Status);
        Assert.Contains("needs-human", ReadBead(target, paused).Labels);
        Assert.Equal("open", ReadBead(target, unrelated).Status);
        Assert.Contains("stage:review", ReadBead(target, unrelated).Labels);
        Assert.Contains(deletedPath, dispatch.StandardErrorText, StringComparison.Ordinal);
    }

    internal static async Task PublishedStartupRechecksPauseBeforeRemovingAPlannedWorktree(PublishedCliArtifactFixture published)
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var planning = target.Create("Pause races completed Planning cleanup", type: "feature", labels: ["planned"]);
        var unrelated = target.Create("Dispatch after cleanup pause race");
        AddDetachedWorktree(target, fixture.WorktreePath(planning));
        var tmp = Path.Combine(fixture.Host, "tmp", "demo", planning);
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "sentinel.txt"), "keep after new pause");
        var shimLog = fixture.GateFile("startup cleanup pause race bd calls.jsonl");

        var dispatch = await fixture.RunAtAsync(fixture.Host, "demo", ["dispatch", unrelated, "--project", "demo"],
            "review", "startup-cleanup-pause-race", planning, shimLog);

        Assert.True(dispatch.ExitCode == 0,
            $"Pause-race dispatch failed (exit {dispatch.ExitCode}).\nstdout:\n{dispatch.StandardOutputText}\nstderr:\n{dispatch.StandardErrorText}");
        Assert.Contains("Succeeded:", dispatch.StandardOutputText, StringComparison.Ordinal);
        Assert.True(Directory.Exists(fixture.WorktreePath(planning)));
        Assert.True(Directory.Exists(tmp));
        var bead = ReadBead(target, planning);
        Assert.Equal("open", bead.Status);
        Assert.Contains("planned", bead.Labels);
        Assert.Contains("needs-human", bead.Labels);
        Assert.Equal("open", ReadBead(target, unrelated).Status);
        Assert.Contains(fixture.WorktreePath(planning), dispatch.StandardErrorText, StringComparison.Ordinal);
    }

    internal static async Task PublishedStartupRetainsAndEscalatesOnlyFailedRemovalThenContinuesDispatch(PublishedCliArtifactFixture published)
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var deliveries = new[]
        {
            target.Create("First closed Delivery worktree", type: "task"),
            target.Create("Second closed Delivery worktree", type: "task")
        };
        foreach (var id in deliveries) target.Run("bd", ["close", id]);
        foreach (var id in deliveries) AddBranchWorktree(target, fixture.WorktreePath(id), id);
        var tmpPaths = deliveries.ToDictionary(id => id, id => Path.Combine(fixture.Host, "tmp", "demo", id), StringComparer.Ordinal);
        foreach (var path in tmpPaths.Values)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "sentinel.txt"), "retain failed cleanup tmp");
        }
        var successfulId = deliveries.Order(StringComparer.Ordinal).First();
        var failedId = deliveries.Order(StringComparer.Ordinal).Last();
        var lockedFile = Path.Combine(fixture.WorktreePath(failedId), "held-open.txt");
        File.WriteAllText(lockedFile, "Git cannot remove an open file");
        using var openHandle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var unrelated = target.Create("Unrelated dispatch after local cleanup failure");

        var dispatch = await fixture.RunAsync(["dispatch", unrelated, "--project", "demo"], "review");

        Assert.True(dispatch.ExitCode == 0,
            $"Local-cleanup-failure dispatch failed (exit {dispatch.ExitCode}).\nstdout:\n{dispatch.StandardOutputText}\nstderr:\n{dispatch.StandardErrorText}");
        Assert.Contains("Succeeded:", dispatch.StandardOutputText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.WorktreePath(successfulId)));
        Assert.False(Directory.Exists(tmpPaths[successfulId]));
        Assert.True(Directory.Exists(fixture.WorktreePath(failedId)));
        Assert.True(Directory.Exists(tmpPaths[failedId]));
        var failedBead = ReadBead(target, failedId);
        Assert.Equal("closed", failedBead.Status);
        Assert.Null(failedBead.Assignee);
        Assert.Contains("needs-human", failedBead.Labels);
        using var commentDocument = JsonDocument.Parse(target.Run("bd", ["comments", failedId, "--json"]));
        var comments = BeadsReader.ParseComments(commentDocument.RootElement, failedId);
        Assert.Contains(comments, comment => comment.Text.Contains(fixture.WorktreePath(failedId), StringComparison.Ordinal));
        Assert.Contains(comments, comment => comment.Text.Contains("Reason:", StringComparison.Ordinal));
        Assert.Contains(fixture.WorktreePath(failedId), dispatch.StandardErrorText, StringComparison.Ordinal);
        Assert.Equal("open", ReadBead(target, unrelated).Status);
        Assert.Contains("stage:review", ReadBead(target, unrelated).Labels);
    }

    internal static async Task PublishedStartupStopsWhenRequiredCleanupEscalationCannotBePersisted(PublishedCliArtifactFixture published)
    {
        Assert.True(OperatingSystem.IsWindows(), "Live dispatch integration requires Windows Job Objects and native fixture apphosts.");
        using var fixture = new LiveDispatchFixture(published);
        var target = fixture.Target;
        var delivery = target.Create("Closed Delivery worktree with escalation write failure", type: "task");
        target.Run("bd", ["close", delivery]);
        AddBranchWorktree(target, fixture.WorktreePath(delivery), delivery);
        var tmp = Path.Combine(fixture.Host, "tmp", "demo", delivery);
        Directory.CreateDirectory(tmp);
        var heldFile = Path.Combine(fixture.WorktreePath(delivery), "held-open.txt");
        File.WriteAllText(heldFile, "hold removal open");
        using var openHandle = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var unrelated = target.Create("Must not dispatch after required escalation write fails");

        var dispatch = await fixture.RunAtAsync(fixture.Host, "demo", ["dispatch", unrelated, "--project", "demo"],
            "review", "startup-cleanup-label-write-failure", delivery);

        Assert.Equal(1, dispatch.ExitCode);
        Assert.Contains("fixture startup cleanup label write failure", dispatch.StandardErrorText, StringComparison.Ordinal);
        Assert.Contains("could not persist needs-human", dispatch.StandardErrorText, StringComparison.Ordinal);
        Assert.True(Directory.Exists(fixture.WorktreePath(delivery)));
        Assert.True(Directory.Exists(tmp));
        Assert.Equal("closed", ReadBead(target, delivery).Status);
        Assert.DoesNotContain("needs-human", ReadBead(target, delivery).Labels);
        Assert.Equal("open", ReadBead(target, unrelated).Status);
        Assert.Null(ReadBead(target, unrelated).Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(unrelated)));
    }

    private static void AddDetachedWorktree(BeadsFixture target, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        target.Run("git", ["worktree", "add", "--detach", path, "HEAD"]);
    }

    private static void AddBranchWorktree(BeadsFixture target, string path, string beadId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        target.Run("git", ["worktree", "add", "-b", "nightweaver/" + beadId, path, "HEAD"]);
    }

    internal static async Task PublishedDispatchExcludesCompetingProjectsAcrossHostRootsAndRecoversAfterProcessDeath(PublishedCliArtifactFixture published)
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
        var sameHostBefore = SnapshotOtherProjectRuntime(fixture.Host, "other");
        var remoteHostBefore = Snapshot.FileTree(remoteHost);

        var startedFile = fixture.GateFile("winner started");
        var releaseFile = fixture.GateFile("winner release");
        using var winner = fixture.StartAt(fixture.Host, "demo", ["dispatch", beadId, "--project", "demo"],
            "pool-implement-gated", startedFile, releaseFile);
        await winner.WaitForFileAsync(startedFile, TimeSpan.FromSeconds(30));
        var winnerObservation = CaptureLogicalObservation(fixture.Target);
        var winnerBead = winnerObservation.Inventory.ById[beadId];
        Assert.Equal("in_progress", winnerBead.Status);
        Assert.Equal("nightweaver:demo", winnerBead.Assignee);
        var logicalBeforeLosers = winnerObservation.CanonicalState;
        RecordCompetingBoundary("winner gate", fixture, winner, beadId, winnerObservation);

        var sameHostLoser = await fixture.RunAtAsync(fixture.Host, "other",
            ["dispatch", sameHostClaim, "--project", "other"], "noop");
        Assert.Equal(1, sameHostLoser.ExitCode);
        Assert.Contains("another Nightweaver Orchestrator already owns", sameHostLoser.StandardErrorText, StringComparison.Ordinal);
        var afterSameHostLoser = CaptureLogicalObservation(fixture.Target);
        var sameHostBead = afterSameHostLoser.Inventory.ById[sameHostClaim];
        Assert.Equal("in_progress", sameHostBead.Status);
        Assert.Equal("nightweaver:other", sameHostBead.Assignee);
        RecordCompetingBoundary("same-host loser", fixture, winner, beadId, afterSameHostLoser);
        Assert.Equal(logicalBeforeLosers, afterSameHostLoser.CanonicalState);
        Assert.Equal(sameHostBefore, SnapshotOtherProjectRuntime(fixture.Host, "other"));

        if (aliasCreated.ExitCode == 0)
        {
            var aliasLoser = await fixture.RunAtAsync(fixture.Host, "alias",
                ["dispatch", aliasClaim, "--project", "alias"], "noop");
            Assert.Equal(1, aliasLoser.ExitCode);
            Assert.Contains("another Nightweaver Orchestrator already owns", aliasLoser.StandardErrorText, StringComparison.Ordinal);
            var afterAliasLoser = CaptureLogicalObservation(fixture.Target);
            var aliasBead = afterAliasLoser.Inventory.ById[aliasClaim];
            Assert.Equal("in_progress", aliasBead.Status);
            Assert.Equal("nightweaver:alias", aliasBead.Assignee);
            RecordCompetingBoundary("repository-alias loser", fixture, winner, beadId, afterAliasLoser);
            Assert.Equal(logicalBeforeLosers, afterAliasLoser.CanonicalState);
        }

        var remoteLoser = await fixture.RunAtAsync(remoteHost, "remote",
            ["dispatch", remoteClaim, "--project", "remote"], "noop");
        Assert.Equal(1, remoteLoser.ExitCode);
        Assert.Contains("another Nightweaver Orchestrator already owns", remoteLoser.StandardErrorText, StringComparison.Ordinal);
        var afterRemoteLoser = CaptureLogicalObservation(fixture.Target);
        var remoteBead = afterRemoteLoser.Inventory.ById[remoteClaim];
        Assert.Equal("in_progress", remoteBead.Status);
        Assert.Equal("nightweaver:remote", remoteBead.Assignee);
        RecordCompetingBoundary("remote-host loser", fixture, winner, beadId, afterRemoteLoser);
        Assert.Equal(logicalBeforeLosers, afterRemoteLoser.CanonicalState);
        Assert.Equal(remoteHostBefore, Snapshot.FileTree(remoteHost));

        var dryRun = await fixture.RunAtAsync(fixture.Host, "demo", ["--project", "demo", "--dry-run"], "noop");
        Assert.Equal(0, dryRun.ExitCode);
        var afterDryRun = CaptureLogicalObservation(fixture.Target);
        var dryRunBead = afterDryRun.Inventory.ById[beadId];
        Assert.Equal("in_progress", dryRunBead.Status);
        Assert.Equal("nightweaver:demo", dryRunBead.Assignee);
        RecordCompetingBoundary("dry-run", fixture, winner, beadId, afterDryRun);
        Assert.Equal(logicalBeforeLosers, afterDryRun.CanonicalState);

        winner.KillTree();
        _ = await winner.WaitAsync(TimeSpan.FromSeconds(15));

        var secondHostRecovery = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", beadId, "--project", "demo"], "review");
        Assert.True(secondHostRecovery.ExitCode == 0,
            $"Second-host recovery failed (exit {secondHostRecovery.ExitCode}).\nstdout:\n{secondHostRecovery.StandardOutputText}\nstderr:\n{secondHostRecovery.StandardErrorText}");
        Assert.Contains("role 'implementer' (inferred)", secondHostRecovery.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("Succeeded:", secondHostRecovery.StandardOutputText, StringComparison.Ordinal);
        var recoveredBead = CaptureLogicalObservation(fixture.Target).Inventory.ById[beadId];
        Assert.Equal("open", recoveredBead.Status);
        Assert.Null(recoveredBead.Assignee);
        Assert.Contains("stage:review", recoveredBead.Labels);
        Assert.True(Directory.Exists(Path.Combine(fixture.Host, "worktrees", "demo", beadId)));
    }

    internal static async Task PublishedDispatchRecoversOnlyExactProjectClaimsAndAdmitsFromFreshReadiness(PublishedCliArtifactFixture published)
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

    internal static async Task PublishedDispatchStopsOnRequiredTrackerAndSharedRootFailures(PublishedCliArtifactFixture published)
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
        Assert.Contains("fresh state is status 'in_progress'", writeFailure.StandardErrorText, StringComparison.Ordinal);
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

    internal static async Task PublishedRecoveryRereadsGuardMismatchAndUncertainWritesWithoutReplay(PublishedCliArtifactFixture published)
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
        Assert.Equal(1, uncertain.ExitCode);
        Assert.Contains("uncertain write", uncertain.StandardErrorText, StringComparison.OrdinalIgnoreCase);
        var rereadReleased = ReadBead(target, uncertainWrite);
        Assert.Equal("open", rereadReleased.Status);
        Assert.Null(rereadReleased.Assignee);
        var notAdmittedAfterUncertain = ReadBead(target, requestedAfterUncertain);
        Assert.Equal("open", notAdmittedAfterUncertain.Status);
        Assert.Null(notAdmittedAfterUncertain.Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(requestedAfterUncertain)));
        var recoveryCommandCount = File.ReadAllLines(shimLog)
            .Count(line => line.Contains(uncertainWrite, StringComparison.Ordinal)
                && line.Contains("--if-status", StringComparison.Ordinal)
                && line.Contains("in_progress", StringComparison.Ordinal));
        Assert.Equal(1, recoveryCommandCount);

        var foreignAfterUncertain = target.Create("Foreign state observed after recovery write error",
            "nightweaver:demo", "in_progress", "task");
        var requestedAfterForeignError = target.Create("Admission must stop after foreign state write error");
        var foreignShimLog = fixture.GateFile("foreign recovery bd calls.jsonl");
        var foreignError = await fixture.RunAtAsync(fixture.Host, "demo",
            ["dispatch", requestedAfterForeignError, "--project", "demo"], "review",
            "recovery-uncertain-foreign", foreignAfterUncertain, foreignShimLog);
        Assert.Equal(1, foreignError.ExitCode);
        Assert.Contains("uncertain write", foreignError.StandardErrorText, StringComparison.OrdinalIgnoreCase);
        var preservedForeign = ReadBead(target, foreignAfterUncertain);
        Assert.Equal("in_progress", preservedForeign.Status);
        Assert.Equal("person@example.invalid", preservedForeign.Assignee);
        var notAdmittedAfterForeignError = ReadBead(target, requestedAfterForeignError);
        Assert.Equal("open", notAdmittedAfterForeignError.Status);
        Assert.Null(notAdmittedAfterForeignError.Assignee);
        Assert.False(Directory.Exists(fixture.WorktreePath(requestedAfterForeignError)));
        var foreignRecoveryCommandCount = File.ReadAllLines(foreignShimLog)
            .Count(line => line.Contains(foreignAfterUncertain, StringComparison.Ordinal)
                && line.Contains("--if-status", StringComparison.Ordinal)
                && line.Contains("in_progress", StringComparison.Ordinal));
        Assert.Equal(1, foreignRecoveryCommandCount);
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

    private static DispatchLogicalObservation CaptureLogicalObservation(BeadsFixture target)
    {
        var lines = target.Run("bd", ["--readonly", "--sandbox", "export", "--all"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var document = JsonDocument.Parse("[" + string.Join(',', lines) + "]");
        return new(BeadsReader.Parse(document.RootElement.GetRawText()), CanonicalJson(document.RootElement));
    }

    private static void RecordCompetingBoundary(string boundary, LiveDispatchFixture fixture, RunningProcess winner,
        string beadId, DispatchLogicalObservation observation)
    {
        var processIdentity = "unavailable";
        var exited = "unavailable";
        try
        {
            using var process = Process.GetProcessById(winner.Id);
            processIdentity = process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            exited = process.HasExited.ToString();
        }
        catch (Exception exception)
        {
            processIdentity = $"{exception.GetType().Name}: {exception.Message}";
        }

        var telemetryPath = Path.Combine(fixture.Host, "telemetry", "demo", "factory.json");
        var runtime = "factory snapshot not published by one-shot dispatch";
        try
        {
            if (File.Exists(telemetryPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(telemetryPath));
                var root = document.RootElement;
                var runtimeId = root.TryGetProperty("runtime_id", out var runtimeIdElement) ? runtimeIdElement.ToString() : "unknown";
                var runtimeStatus = root.TryGetProperty("runtime_status", out var statusElement) ? statusElement.ToString() : "unknown";
                var invocations = new List<string>();
                foreach (var slot in root.GetProperty("delivery").GetProperty("slots").EnumerateArray())
                {
                    if (!slot.TryGetProperty("invocation", out var invocation) || invocation.ValueKind != JsonValueKind.Object
                        || !invocation.TryGetProperty("bead_id", out var idElement)
                        || !string.Equals(idElement.GetString(), beadId, StringComparison.Ordinal)) continue;
                    invocations.Add(invocation.GetRawText());
                }
                runtime = $"runtime={runtimeId}/{runtimeStatus}; bead-invocations=[{string.Join(';', invocations)}]";
            }
        }
        catch (Exception exception)
        {
            runtime = $"telemetry-error={exception.GetType().Name}: {exception.Message}";
        }

        var bead = observation.Inventory.ById.TryGetValue(beadId, out var observedBead)
            ? $"{observedBead.Status}/{observedBead.Assignee ?? "(none)"}"
            : "missing";
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"dispatch boundary={boundary}; at={DateTimeOffset.UtcNow:O}; winnerRuntimePid={winner.Id}; " +
            $"winnerProcessStart={processIdentity}; winnerExited={exited}; bead={bead}; {runtime}; " +
            $"winnerStdoutTail={Tail(winner.StandardOutputSnapshot)}; winnerStderrTail={Tail(winner.StandardErrorSnapshot)}");
    }

    private static string Tail(string value, int length = 1200) => value.Length <= length ? value : value[^length..];

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

    private sealed record DispatchLogicalObservation(BeadsInventory Inventory, string CanonicalState);

    private static void AssertRetainedExecution(LiveDispatchFixture fixture, string beadId)
    {
        Assert.True(Directory.Exists(fixture.WorktreePath(beadId)));
        Assert.Equal("nightweaver/" + beadId, fixture.Target.Run("git", ["-C", fixture.WorktreePath(beadId), "branch", "--show-current"]).Trim());
        Assert.True(Directory.EnumerateFiles(Path.Combine(fixture.Host, "logs", "demo", beadId), "*.log").Any());
    }

    private sealed class LiveDispatchFixture : IDisposable
    {
        private readonly TempDirectory temp = new();
        private readonly DispatchRepositoryCopy targetCopy;
        private readonly string fakeCliDirectory;
        private readonly string bdShimDirectory;
        private readonly string realBdPath;
        private readonly PublishedCliArtifactFixture published;

        internal BeadsFixture Target { get; }
        internal string Host { get; }
        internal string Caller { get; }
        internal string Config { get; }
        internal string AliasRoot { get; }

        internal LiveDispatchFixture(PublishedCliArtifactFixture published, int retries = 0)
        {
            this.published = published;
            targetCopy = DispatchRepositoryTemplate.CreateCopy("target repository with spaces");
            Target = targetCopy.Target;
            Host = temp.CreateDirectory("nightweaver host with spaces");
            Caller = temp.CreateDirectory("unrelated caller with spaces");
            AliasRoot = temp.CreateDirectory("repository aliases");
            published.CopyPublishedAppTo(Host);
            Config = AddProject(Host, "demo", retries: retries);

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

        internal string AddProject(string host, string project, string? targetPath = null, int retries = 0)
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
                limits = new { max_operational_retries = retries },
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
                ["OPENAI_API_KEY"] = null,
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
                File.Copy(source, Path.Combine(fakeCliDirectory, "gh.exe"), overwrite: true);
            }
        }

        public void Dispose()
        {
            temp.Dispose();
            targetCopy.Dispose();
        }
    }
}
