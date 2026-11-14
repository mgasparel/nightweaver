using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Tests;

public sealed class WorktreePlannerTests
{
    private static readonly string[] CompletedIds = ["delivery", "product"];
    private static readonly string[] CleanupReasons = ["closed", "planned"];
    private static readonly string[] PlannedReason = ["planned"];

    [Theory]
    [InlineData("planned")]
    [InlineData("stage:plan-approved")]
    public void InterruptedMaterializationProtectsTmpEvenWithoutRegisteredWorktree(string label)
    {
        using var temp = new TempDirectory();
        var inventory = BeadsReader.Parse(JsonSerializer.Serialize(new[]
        { new { id = "product", status = "in_progress", issue_type = "bug", priority = 2,
            created_at = "2026-01-01T00:00:00Z", assignee = "actor", labels = new[] { label } } }));
        var plan = StartupPlanner.Plan("actor", inventory, [], Root(temp.Path), Root(temp.Path + "-tmp", "product"));
        if (label == "planned") Assert.Empty(plan.Releases);
        else Assert.Single(plan.Releases);
        Assert.Empty(plan.OrphanTmp);
        if (label == "planned") Assert.Contains(plan.Diagnostics, d => d.Code == StartupDiagnosticCode.PlanningMaterializationRecoveryRequired);
        else Assert.DoesNotContain(plan.Diagnostics, d => d.Code == StartupDiagnosticCode.PlanningMaterializationRecoveryRequired);
    }

    [Fact]
    public void OnlyRoleMatchedCompletedProductAndDeliveryWorktreesAreRemoved()
    {
        using var temp = new TempDirectory();
        var rootPath = temp.CreateDirectory("worktrees");
        var tmpPath = temp.CreateDirectory("tmp");
        var inventory = Inventory(
            new TestBead("product", "feature", "open", ["planned"]),
            new TestBead("delivery", "task", "closed"),
            new TestBead("wrong-role", "task", "open", ["planned"]),
            new TestBead("closed-product", "bug", "closed", ["planned"]),
            new TestBead("paused-product", "feature", "open", ["planned", "needs-human"]),
            new TestBead("active", "task", "open", ["stage:review"]));
        var registry = new[]
        {
            Entry(rootPath, "product"),
            Entry(rootPath, "delivery") with { Detached = false, Branch = "refs/heads/nightweaver/delivery" },
            Entry(rootPath, "wrong-role") with { Detached = false, Branch = "refs/heads/nightweaver/wrong-role" },
            Entry(rootPath, "closed-product"),
            Entry(rootPath, "paused-product"),
            Entry(rootPath, "active") with { Detached = false, Branch = "refs/heads/nightweaver/active" }
        };
        var worktrees = Root(rootPath, "product", "delivery", "wrong-role", "closed-product", "paused-product", "active");
        var tmp = Root(tmpPath, "product", "delivery", "wrong-role", "paused-product", "active");

        var plan = StartupPlanner.Plan("actor", inventory, registry, worktrees, tmp);

        Assert.Equal(CompletedIds, plan.Removals.Select(item => item.BeadId).Order(StringComparer.Ordinal));
        Assert.Equal(CleanupReasons, plan.Removals.SelectMany(item => item.Reasons).Order(StringComparer.Ordinal));
        Assert.Contains(plan.SurvivingWorktrees, item => item.BeadId == "paused-product" && item.Paused && item.ProtectsTmp);
        Assert.Contains(plan.SurvivingWorktrees, item => item.BeadId == "active" && item.Reusable);
        Assert.Contains(plan.Diagnostics, item => item.BeadId == "wrong-role" && item.Code == StartupDiagnosticCode.UnsupportedWorktreeIdentity);
        Assert.Equal(CompletedIds, plan.OrphanTmp.Select(item => item.BeadId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(plan.OrphanTmp, item => item.BeadId is "wrong-role" or "paused-product" or "active");
    }

    [Fact]
    public void ClaimedPlannedProductRequiresRecoveryAndRetainsItsEnvironment()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        var inventory = Inventory(new TestBead("product", "feature", "in_progress", ["planned"], "nightweaver:demo"));
        var plan = StartupPlanner.Plan("nightweaver:demo", inventory, [Entry(root, "product")],
            Root(root, "product"), Root(root + "-tmp", "product"));

        Assert.Empty(plan.Releases);
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        var retained = Assert.Single(plan.SurvivingWorktrees);
        Assert.True(retained.ProtectsTmp);
        Assert.False(retained.Reusable);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == StartupDiagnosticCode.PlanningMaterializationRecoveryRequired);
        Assert.Equal("in_progress", inventory.ById["product"].Status);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("prunable")]
    [InlineData("missing")]
    public void EveryRetainedRegistrationProtectsTmp(string scenario)
    {
        using var temp = new TempDirectory();
        var registration = Entry(temp.Path, "delivery") with
        {
            Locked = scenario == "locked",
            Prunable = scenario == "prunable"
        };
        var worktrees = scenario == "missing" ? Root(temp.Path) : Root(temp.Path, "delivery");
        var plan = StartupPlanner.Plan("actor", Inventory(new TestBead("delivery", "task", "closed")), [registration],
            worktrees, Root(temp.Path + "-tmp", "delivery"));

        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.SurvivingWorktrees, item => item.BeadId == "delivery" && item.ProtectsTmp);
        Assert.Contains(plan.Diagnostics, item => item.Code == (scenario == "locked" ? StartupDiagnosticCode.LockedWorktree
            : scenario == "prunable" ? StartupDiagnosticCode.PrunableRegistration : StartupDiagnosticCode.MissingRegisteredPath));
    }

    [Fact]
    public void UnknownAndUnregisteredLeftoversAreReportedAndProtectAssociatedTmp()
    {
        using var temp = new TempDirectory();
        var worktrees = Root(temp.Path, "deleted", "active-directory");
        var plan = StartupPlanner.Plan("actor", Inventory(new TestBead("active", "task", "open", ["stage:review"])),
            [Entry(temp.Path + "-foreign", "active")], worktrees,
            Root(temp.Path + "-tmp", "deleted", "active", "unknown"));

        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, item => item.Code == StartupDiagnosticCode.UnregisteredWorktreeDirectory && item.BeadId == "deleted");
        Assert.Contains(plan.Diagnostics, item => item.Code == StartupDiagnosticCode.UnknownTmpIdentity && item.BeadId == "unknown");
    }

    [Theory]
    [InlineData("needs-human")]
    [InlineData("product-decision-required")]
    public void PauseOverridesCompletedCleanupAndProtectsTmpEvenWithoutWorktree(string pauseLabel)
    {
        using var temp = new TempDirectory();
        var inventory = Inventory(new TestBead("product", "feature", "open", ["planned", pauseLabel]));
        var plan = StartupPlanner.Plan("actor", inventory, [Entry(temp.Path, "product")],
            Root(temp.Path, "product"), Root(temp.Path + "-tmp", "product"));

        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.True(Assert.Single(plan.SurvivingWorktrees).Paused);

        var missingBacking = StartupPlanner.Plan("actor", inventory, [], Root(temp.Path), Root(temp.Path + "-tmp", "product"));
        Assert.Empty(missingBacking.OrphanTmp);
        Assert.Contains(missingBacking.Diagnostics, item => item.Code == StartupDiagnosticCode.TmpCleanupDeferred);
    }

    [Fact]
    public void UnsafeRootsAndTmpLinksNeverGenerateDeletionProposals()
    {
        using var temp = new TempDirectory();
        var root = Root(temp.Path) with { State = DirectoryState.UnsafeLink };
        var inventory = Inventory(new TestBead("delivery", "task", "closed"));
        var plan = StartupPlanner.Plan("actor", inventory, [], root, Root(temp.Path + "-tmp", "delivery"));
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, item => item.Code == StartupDiagnosticCode.TmpCleanupDeferred);

        var tmp = Root(temp.Path + "-tmp", "delivery");
        tmp = tmp with { Entries = [tmp.Entries[0] with { State = DirectoryState.UnsafeLink }] };
        plan = StartupPlanner.Plan("actor", inventory, [], Root(temp.Path), tmp);
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, item => item.Code == StartupDiagnosticCode.UnsafeRuntimePath);
        Assert.Contains(plan.Diagnostics, item => item.Code == StartupDiagnosticCode.TmpCleanupDeferred);
    }

    [Fact]
    public void ReasonsAndOrderAreStableAndInputsRemainUnchanged()
    {
        using var temp = new TempDirectory();
        var inventory = Inventory(new TestBead("product", "feature", "open", ["planned", "planned"]));
        var entry = Entry(temp.Path, "product");
        var root = Root(temp.Path, "z", "product", "a");
        var tmp = Root(temp.Path + "-tmp", "z", "product", "a");
        var first = StartupPlanner.Plan("actor", inventory, [entry], root, tmp);
        var second = StartupPlanner.Plan("actor", inventory, [entry], root with { Entries = root.Entries.Reverse().ToArray() }, tmp with { Entries = tmp.Entries.Reverse().ToArray() });

        Assert.Equal(PlannedReason, Assert.Single(first.Removals).Reasons);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(first.OrphanTmp, second.OrphanTmp);
        Assert.Equal(3, root.Entries.Count);
        Assert.Equal("open", inventory.ById["product"].Status);
    }

    [Fact]
    public void WindowsCaseAliasesAreAmbiguousRatherThanOrdinalBeadMatches()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var root = Root(temp.Path, "DELIVERY");
        var plan = StartupPlanner.Plan("actor", Inventory(new TestBead("delivery", "task", "closed")),
            [Entry(temp.Path, "delivery") with { Detached = false, Branch = "refs/heads/nightweaver/delivery" }],
            root, Root(temp.Path + "-tmp", "delivery"));
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Contains(plan.Diagnostics, item => item.Code == StartupDiagnosticCode.AmbiguousWorktreeIdentity);
    }

    [Fact]
    public void AmbiguousRuntimeIdentitiesSuppressRemovalAndTmpDeletion()
    {
        using var temp = new TempDirectory();
        var root = Root(temp.Path, "delivery") with { Entries = [Root(temp.Path, "delivery").Entries[0] with { AmbiguousIdentity = true }] };
        var tmp = Root(temp.Path + "-tmp", "delivery") with { Entries = [Root(temp.Path + "-tmp", "delivery").Entries[0] with { AmbiguousIdentity = true }] };
        var plan = StartupPlanner.Plan("actor", Inventory(new TestBead("delivery", "task", "closed")),
            [Entry(temp.Path, "delivery") with { Detached = false, Branch = "refs/heads/nightweaver/delivery" }], root, tmp);
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.OrphanTmp);
        Assert.Equal(1, plan.Diagnostics.Count(item => item.Code == StartupDiagnosticCode.TmpCleanupDeferred));
    }

    private sealed record TestBead(string Id, string Type, string Status, string[]? Labels = null, string? Assignee = null);

    private static BeadsInventory Inventory(params TestBead[] beads) => BeadsReader.Parse(JsonSerializer.Serialize(beads.Select(bead => new
    {
        id = bead.Id,
        status = bead.Status,
        issue_type = bead.Type,
        priority = 2,
        created_at = "2026-01-01T00:00:00Z",
        assignee = bead.Assignee,
        labels = bead.Labels ?? []
    })));

    private static GitWorktreeSnapshot Entry(string root, string id) => new(Path.Combine(root, id), Path.Combine(root, id), "abc", null, true, false, false, null, false, null);
    private static RuntimeDirectorySnapshot Root(string root, params string[] ids) => new(root, DirectoryState.Present,
        ids.Select(id => new RuntimeDirectoryEntry(id, Path.Combine(root, id), DirectoryState.Present)).ToArray());
}
