using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class MergerProductEligibilityTests
{
    private const string TaskId = "task-a";
    private const string ProductId = "product-a";

    [Fact]
    public void ProductClosesOnlyWhenEveryDiscoveredTaskIsClosedAndUnpaused()
    {
        var inventory = Inventory(
            Bead(ProductId, "open", "feature", "planned"),
            Bead(TaskId, "closed", "task"),
            Bead("task-b", "closed", "task"));
        var result = Evaluate(inventory,
            [Edge(TaskId, ProductId, "discovered-from")],
            [Edge(TaskId, ProductId, "discovered-from"), Edge("task-b", ProductId, "discovered-from")]);

        Assert.Equal(MergerProductCheck.Ready, result.Check);
        Assert.Equal(ProductId, result.ProductId);
    }

    [Theory]
    [InlineData("open", "task", "")]
    [InlineData("closed", "task", "needs-human")]
    [InlineData("closed", "task", "product-decision-required")]
    [InlineData("closed", "feature", "")]
    public void OpenPausedOrNonTaskSiblingPreventsProductClosure(string status, string issueType, string label)
    {
        var inventory = Inventory(Bead(ProductId, "open", "feature", "planned"),
            Bead(TaskId, "closed", "task"), Bead("task-b", status, issueType, label));
        var result = Evaluate(inventory, [Edge(TaskId, ProductId, "discovered-from")],
            [Edge(TaskId, ProductId, "discovered-from"), Edge("task-b", ProductId, "discovered-from")]);

        Assert.Equal(MergerProductCheck.IncompleteTasks, result.Check);
    }

    [Fact]
    public void MultipleDiscoveredProductsAreAmbiguousAndNeverSelected()
    {
        var inventory = Inventory(Bead(ProductId, "open", "feature", "planned"),
            Bead("product-b", "open", "bug", "planned"), Bead(TaskId, "closed", "task"));
        var result = Evaluate(inventory, [Edge(TaskId, ProductId, "discovered-from"),
            Edge(TaskId, "product-b", "discovered-from")], []);

        Assert.Equal(MergerProductCheck.MultipleCandidates, result.Check);
        Assert.Null(result.ProductId);
    }

    [Fact]
    public void MissingUnplannedAndParentlessProductsAreSafeNoOps()
    {
        Assert.Equal(MergerProductCheck.NoCandidate,
            Evaluate(Inventory(Bead(TaskId, "closed", "task")), [], []).Check);
        Assert.Equal(MergerProductCheck.MissingProduct,
            Evaluate(Inventory(Bead(TaskId, "closed", "task")), [Edge(TaskId, ProductId, "discovered-from")], []).Check);
        Assert.Equal(MergerProductCheck.NotPlannedProduct,
            Evaluate(Inventory(Bead(ProductId, "open", "feature"), Bead(TaskId, "closed", "task")),
                [Edge(TaskId, ProductId, "discovered-from")], []).Check);
        Assert.Equal(MergerProductCheck.NotPlannedProduct,
            Evaluate(Inventory(Bead(ProductId, "open", "epic", "planned"), Bead(TaskId, "closed", "task")),
                [Edge(TaskId, ProductId, "discovered-from")], []).Check);
    }

    [Fact]
    public void HierarchyAndBlockingEdgesDoNotCountAsDiscoveredProductOrLinkedTasks()
    {
        var inventory = Inventory(Bead(ProductId, "open", "feature", "planned"), Bead(TaskId, "closed", "task"));
        var noParent = Evaluate(inventory, [Edge(TaskId, ProductId, "parent-child"),
            Edge(TaskId, ProductId, "blocks")], []);
        var noTasks = Evaluate(inventory, [Edge(TaskId, ProductId, "discovered-from")],
            [Edge(TaskId, ProductId, "parent-child"), Edge(TaskId, ProductId, "blocks")]);

        Assert.Equal(MergerProductCheck.NoCandidate, noParent.Check);
        Assert.Equal(MergerProductCheck.NoLinkedTasks, noTasks.Check);
    }

    [Fact]
    public void AlreadyClosedPlannedProductIsIdempotentButClosedUnplannedProductIsNotEligible()
    {
        var taskEdges = new[] { Edge(TaskId, ProductId, "discovered-from") };
        Assert.Equal(MergerProductCheck.AlreadyClosed,
            Evaluate(Inventory(Bead(ProductId, "closed", "feature", "planned"), Bead(TaskId, "closed", "task")), taskEdges, []).Check);
        Assert.Equal(MergerProductCheck.NotPlannedProduct,
            Evaluate(Inventory(Bead(ProductId, "closed", "feature"), Bead(TaskId, "closed", "task")), taskEdges, []).Check);
    }

    [Theory]
    [InlineData("needs-human", false)]
    [InlineData("product-decision-required", true)]
    public void PausedOrContradictoryProductNeverBecomesEligibleForAutomaticClosure(
        string pauseLabel, bool contradictory)
    {
        var inventory = Inventory(Bead(ProductId, "open", "feature", "planned", pauseLabel),
            Bead(TaskId, "closed", "task"));
        var result = Evaluate(inventory, [Edge(TaskId, ProductId, "discovered-from")],
            [Edge(TaskId, ProductId, "discovered-from")]);

        Assert.Equal(contradictory ? MergerProductCheck.InconsistentWorkflowState : MergerProductCheck.PausedProduct, result.Check);
    }

    [Theory]
    [InlineData("planned", "stage:implement")]
    [InlineData("planned", "stage:implement", "stage:merge")]
    [InlineData("planned", "stage:unknown")]
    public void ContradictoryOrMalformedProductWorkflowIsNeverEligible(params string[] labels)
    {
        var inventory = Inventory(Bead(ProductId, "open", "feature", labels),
            Bead(TaskId, "closed", "task"));
        var result = Evaluate(inventory, [Edge(TaskId, ProductId, "discovered-from")],
            [Edge(TaskId, ProductId, "discovered-from")]);

        Assert.Equal(MergerProductCheck.InconsistentWorkflowState, result.Check);
    }
    private static (MergerProductCheck Check, string? ProductId) Evaluate(BeadsInventory inventory,
        IReadOnlyList<BeadDependency> taskEdges, IReadOnlyList<BeadDependency> productEdges) =>
        MergerProductEligibility.Evaluate(TaskId, taskEdges, inventory, productEdges);

    private static BeadsInventory Inventory(params object[] rows) => BeadsReader.Parse(JsonSerializer.Serialize(rows));
    private static object Bead(string id, string status, string issueType, params string[] labels) => new
    {
        id, status, issue_type = issueType, priority = 2, created_at = "2026-11-04T00:00:00Z",
        assignee = (string?)null, labels
    };
    private static BeadDependency Edge(string issueId, string dependsOnId, string type) => new(issueId, dependsOnId, type);
}
