using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class WorktreePlannerTests
{
    [Theory]
    [InlineData("closed", "")]
    [InlineData("open", "planned")]
    [InlineData("in_progress", "product-decision-required")]
    [InlineData("open", "needs-human")]
    [InlineData("custom", "planned")]
    public void StalePresentUnlockedWorktreesAreRemovedThenTheirTmpIsOrphaned(string status, string label)
    {
        using var temp = new TempDirectory();
        foreach (var detached in new[] { true, false })
        {
            var registry = Entry(temp.Path, "bead") with { Detached = detached, Branch = detached ? null : "refs/heads/nightweaver/bead" };
            var inventory = Inventory("bead", status, label.Length == 0 ? [] : [label], "foreign");
            var plan = StartupPlanner.Plan("actor", inventory, [registry], Root(temp.Path, "bead"), Root(temp.Path + "-tmp", "bead"));
            var removal = Assert.Single(plan.Removals);
            Assert.Equal("bead", removal.BeadId);
            Assert.False(removal.ExpectedLocked);
            Assert.Equal(DirectoryState.Present, removal.Presence);
            Assert.Equal(label.Length == 0 ? "closed" : label, Assert.Single(removal.Reasons));
            Assert.Empty(plan.SurvivingWorktrees);
            Assert.Equal("bead", Assert.Single(plan.OrphanTmp).BeadId);
            Assert.Equal(status, inventory.ById["bead"].Status);
            Assert.Same(inventory, plan.Inventory);
        }
    }

    [Theory]
    [InlineData("open", "stage:merge")]
    [InlineData("in_progress", "stage:implement")]
    [InlineData("open", "blocked")]
    public void ActiveWorktreesAreRetainedRegardlessOfStageOwnerOrType(string status, string label)
    {
        using var temp = new TempDirectory();
        var plan = StartupPlanner.Plan("actor", Inventory("bead", status, [label], "actor"),
            [Entry(temp.Path, "bead")], Root(temp.Path, "bead"), Root(temp.Path + "-tmp", "bead"));
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.True(Assert.Single(plan.SurvivingWorktrees).Reusable);
        Assert.Equal(status == "in_progress" ? 1 : 0, plan.Releases.Count);
    }

    [Theory]
    [InlineData("missing-bead", "MissingBead")]
    [InlineData("locked", "LockedWorktree")]
    [InlineData("prunable", "PrunableRegistration")]
    [InlineData("unsafe", "UnsafeRuntimePath")]
    [InlineData("custom", "UnsupportedBeadStatus")]
    [InlineData("deferred", "UnsupportedBeadStatus")]
    [InlineData("blocked", "UnsupportedBeadStatus")]
    public void UncertainBackingDefersTmpCleanup(string scenario, string expected)
    {
        using var temp = new TempDirectory();
        var entry = Entry(temp.Path, "bead") with { Locked = scenario == "locked", LockReason = "keep\nreason", Prunable = scenario == "prunable" };
        var inventory = scenario == "missing-bead" ? new BeadsInventory([]) : Inventory("bead", scenario is "custom" or "deferred" or "blocked" ? scenario : "closed");
        var root = Root(temp.Path, "bead");
        if (scenario == "unsafe") root = root with { Entries = [root.Entries[0] with { State = DirectoryState.UnsafeLink }] };
        var plan = StartupPlanner.Plan("actor", inventory, [entry], root, Root(temp.Path + "-tmp", "bead"));
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code.ToString() == expected);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.TmpCleanupDeferred);
    }

    [Fact]
    public void AllAnomaliesAreReportedAndMissingRegisteredPathDoesNotProtectTmp()
    {
        using var temp = new TempDirectory();
        var missing = Entry(temp.Path, "bead") with { Prunable = true };
        var plan = StartupPlanner.Plan("actor", Inventory("bead", "closed"), [missing], Root(temp.Path), Root(temp.Path + "-tmp", "bead", "orphan"));
        Assert.Empty(plan.Removals);
        Assert.Equal(2, plan.OrphanTmp.Count);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.MissingRegisteredPath);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.PrunableRegistration);
        var anomalies = StartupPlanner.Plan("actor", new BeadsInventory([]), [missing with { Locked = true }], Root(temp.Path), Root(temp.Path + "-tmp", "bead"));
        Assert.Equal(5, anomalies.Diagnostics.Count);
        Assert.Empty(anomalies.OrphanTmp);
        var absentBead = StartupPlanner.Plan("actor", new BeadsInventory([]), [missing], Root(temp.Path), Root(temp.Path + "-tmp", "bead"));
        Assert.Single(absentBead.OrphanTmp);
        Assert.Contains(absentBead.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.MissingBead);
    }

    [Fact]
    public void UnregisteredDirectoriesProtectTmpAndOtherProjectsAndBranchNamesNeverConferOwnership()
    {
        using var temp = new TempDirectory();
        var plan = StartupPlanner.Plan("actor", Inventory("bead", "closed"),
            [Entry(temp.Path + "-foreign", "bead"), Entry(temp.Path, "main") with { Main = true }, Entry(temp.Path, "bare") with { Bare = true }],
            Root(temp.Path, "bead", "main", "bare"), Root(temp.Path + "-tmp", "bead", "orphan"));
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.SurvivingWorktrees);
        Assert.Equal("orphan", Assert.Single(plan.OrphanTmp).BeadId);
        Assert.Equal(3, plan.Diagnostics.Count(diagnostic => diagnostic.Code == StartupDiagnosticCode.UnregisteredWorktreeDirectory));
    }

    [Fact]
    public void UnsafeRootsAndTmpLinksNeverGenerateDeletionProposals()
    {
        using var temp = new TempDirectory();
        var root = Root(temp.Path) with { State = DirectoryState.UnsafeLink };
        var plan = StartupPlanner.Plan("actor", new BeadsInventory([]), [], root, Root(temp.Path + "-tmp", "orphan"));
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.TmpCleanupDeferred);
        var tmp = Root(temp.Path + "-tmp", "orphan");
        tmp = tmp with { Entries = [tmp.Entries[0] with { State = DirectoryState.UnsafeLink }] };
        plan = StartupPlanner.Plan("actor", new BeadsInventory([]), [], Root(temp.Path), tmp);
        Assert.Empty(plan.OrphanTmp);
        Assert.Equal(2, plan.Diagnostics.Count);
    }

    [Fact]
    public void ReasonsAndOrderAreStableAndInputsRemainUnchanged()
    {
        using var temp = new TempDirectory();
        var inventory = Inventory("bead", "closed", ["planned", "needs-human", "product-decision-required", "planned"]);
        var entry = Entry(temp.Path, "bead");
        var root = Root(temp.Path, "z", "bead", "a");
        var tmp = Root(temp.Path + "-tmp", "z", "bead", "a", "orphan");
        var first = StartupPlanner.Plan("actor", inventory, [entry], root, tmp);
        var second = StartupPlanner.Plan("actor", inventory, [entry], root with { Entries = root.Entries.Reverse().ToArray() }, tmp with { Entries = tmp.Entries.Reverse().ToArray() });
        Assert.Equal((string[])["closed", "needs-human", "planned", "product-decision-required"], Assert.Single(first.Removals).Reasons);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(first.OrphanTmp, second.OrphanTmp);
        Assert.Equal(3, root.Entries.Count);
        Assert.Equal("closed", inventory.ById["bead"].Status);
    }

    [Fact]
    public void WindowsCaseAliasesAreAmbiguousRatherThanOrdinalBeadMatches()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var plan = StartupPlanner.Plan("actor", Inventory("bead", "closed"), [Entry(temp.Path, "bead")], Root(temp.Path, "BEAD"), Root(temp.Path + "-tmp", "bead"));
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.AmbiguousWorktreeIdentity);
    }

    [Fact]
    public void AmbiguousRuntimeIdentitiesSuppressRemovalAndTmpDeletion()
    {
        using var temp = new TempDirectory();
        var root = Root(temp.Path, "bead");
        root = root with { Entries = [root.Entries[0] with { AmbiguousIdentity = true }] };
        var tmp = Root(temp.Path + "-tmp", "bead", "orphan");
        tmp = tmp with { Entries = [tmp.Entries[0], tmp.Entries[1] with { AmbiguousIdentity = true }] };
        var plan = StartupPlanner.Plan("actor", Inventory("bead", "closed"), [Entry(temp.Path, "bead")], root, tmp);
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Equal(2, plan.Diagnostics.Count(diagnostic => diagnostic.Code == StartupDiagnosticCode.TmpCleanupDeferred));
    }

    internal static BeadsInventory Inventory(string id, string status, string[]? labels = null, string? actor = null) => BeadsReader.Parse(JsonSerializer.Serialize(new[]
    { new { id, status, issue_type = "custom", priority = 2, created_at = "2026-01-01T00:00:00Z", assignee = actor, labels = labels ?? [] } }));
    internal static GitWorktreeSnapshot Entry(string root, string id) => new(Path.Combine(root, id), Path.Combine(root, id), "abc", null, true, false, false, null, false, null);
    internal static RuntimeDirectorySnapshot Root(string root, params string[] ids) => new(root, DirectoryState.Present,
        ids.Select(id => new RuntimeDirectoryEntry(id, Path.Combine(root, id), DirectoryState.Present)).ToArray());
}
