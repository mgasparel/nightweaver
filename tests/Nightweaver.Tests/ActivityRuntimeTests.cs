using System.Text.Json;
using Nightweaver.ActivityFeed;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

internal sealed class ActivityCapture : IDisposable
{
    internal readonly List<(ActivityEvidence Evidence, ActivityMergeProof? Proof)> Items = [];
    private readonly ActivityRuntime runtime;
    internal ActivityCapture(TimeProvider? clock = null, int inventoryLimit = 10000) =>
        runtime = new((e, p) => Items.Add((e, p)), clock, inventoryLimit);
    internal ActivityRuntime Runtime => runtime;
    internal ActivityEvidence[] Evidence => Items.Select(i => i.Evidence).ToArray();
    public void Dispose() => runtime.Dispose();
}

public sealed class ActivityRuntimeTests
{
    private static BeadSnapshot Bead(string[]? labels = null, string status = "open") =>
        new("ns-proof", status, "task", 2, DateTimeOffset.UnixEpoch, null, labels ?? [],
            JsonSerializer.SerializeToElement(new { metadata = new { repair_cycles = 0 }, description = "PRIVATE_DESCRIPTION" }));

    [Fact]
    public async Task RuntimePersistsSafeRecordsWithoutAnExporter()
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(temp.CreateDirectory("host"));
        var target = temp.CreateDirectory("target");
        var project = ProjectSlug.Parse("test");
        var repository = new GitHubRepositoryIdentity("github.com", "owner", "repo");
        var runtime = Guid.NewGuid();
        var journal = new ActivityJournal(new ActivityJournalFiles(paths, project, target, repository), repository, runtime);
        using (var scope = new ActivityRuntime((e, p) => journal.TrySubmit(e, p)))
        {
            scope.Observe([Bead(["needs-human"])]);
            scope.Observe([Bead(["needs-human"])]);
            scope.Observe([Bead()]);
        }
        await journal.DisposeAsync();
        Assert.True(journal.Health.Available);
        using var reader = new ActivityJournalFiles(paths, project, target, repository);
        var page = reader.ReadPage(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([ActivityKind.PauseObserved, ActivityKind.PauseRemovalObserved], page.Records.Select(r => r.Kind));
        Assert.All(page.Records, r => Assert.Equal(runtime, r.Position.Runtime));
        Assert.DoesNotContain("PRIVATE", string.Join("", page.Records.Select(r => System.Text.Encoding.UTF8.GetString(ActivityProjector.Serialize(r)))), StringComparison.Ordinal);
    }

    [Fact]
    public void InitialPauseChangesAbortAndRemovalDoNotAssertResume()
    {
        using var capture = new ActivityCapture();
        var paused = Bead(["needs-human"]);
        capture.Runtime.Observe([paused]);
        capture.Runtime.Observe([paused]);
        capture.Runtime.Observe([paused with { Payload = JsonSerializer.SerializeToElement(new { note = "PRIVATE_NOTE" }) }]);
        Assert.Equal(ActivityReason.NeedsHumanReasonUnavailable, Assert.Single(capture.Evidence).Reason);
        capture.Runtime.Observe([Bead(["needs-human", "product-decision-required"])]);
        Assert.Equal(ActivityReason.CombinedPauseReasonUnavailable, capture.Evidence[^1].Reason);
        capture.Runtime.Observe([Bead(["needs-human", "product-decision-required"], "closed")]);
        Assert.Equal(ActivityKind.StateObserved, capture.Evidence[^1].Kind);
        capture.Runtime.Observe([Bead()]);
        Assert.Equal(ActivityKind.PauseRemovalObserved, capture.Evidence[^1].Kind);
        Assert.Equal(ActivityReason.MissingEvidence, capture.Evidence[^1].Reason);
        Assert.DoesNotContain(capture.Evidence, e => e.Kind == ActivityKind.ResumeConfirmed);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(capture.Items), StringComparison.Ordinal);
        Assert.All(capture.Evidence, e => Assert.Null(e.SourceTime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrStaleInventoryCannotProvePauseRemoval(bool stale)
    {
        var clock = new WorkflowTimingTests.Clock();
        using var capture = new ActivityCapture(clock);
        capture.Runtime.Observe([Bead(["needs-human"])]);
        if (stale) clock.Advance(61);
        else capture.Runtime.UnknownInventory();
        capture.Runtime.Observe([Bead()]);
        Assert.DoesNotContain(capture.Evidence, e => e.Kind == ActivityKind.PauseRemovalObserved);
        Assert.Equal(ActivityKind.StateObserved, capture.Evidence[^1].Kind);
        if (!stale) Assert.Contains(capture.Evidence, e => e.Reason == ActivityReason.SourceFailed);
    }

    [Fact]
    public void PublicationNeedsReceiptAndSuppressesExactLaterSnapshot()
    {
        using var capture = new ActivityCapture();
        var state = new BeadWorkflowState("ns-proof", "task", "open", "stage:qa", false, false, false, 0);
        var result = new AgentStageResult(AgentRole.Reviewer, "ns-proof", "invocation-1", "PRIVATE_LOG", null, []);
        capture.Runtime.Published(result);
        Assert.Empty(capture.Items);
        result = result with { PublicationReceipt = new(AgentRole.Reviewer, AgentOutcomeChoice.Approved, state, null, true, false) };
        capture.Runtime.Published(result);
        capture.Runtime.Published(result);
        capture.Runtime.Observe([Bead(["stage:qa"])]);
        var evidence = Assert.Single(capture.Evidence);
        Assert.Equal(ActivityKind.DeliveryApproved, evidence.Kind);
        Assert.Equal("invocation-1", evidence.Invocation);
        Assert.Equal(0, evidence.Count);
    }

    [Fact]
    public void InventoryThatOverlapsPublicationCannotInventPauseRemoval()
    {
        using var capture = new ActivityCapture();
        var version = capture.Runtime.InventoryVersion;
        var state = new BeadWorkflowState("ns-proof", "task", "open", "stage:implement", false, true, false, 0);
        capture.Runtime.Published(new(AgentRole.Implementer, "ns-proof", "invocation-1", "PRIVATE_LOG", null, [],
            PublicationReceipt: new(AgentRole.Implementer, AgentOutcomeChoice.ProductDecisionRequired, state, null, true, false)));
        capture.Runtime.Observe([Bead()], version);
        Assert.DoesNotContain(capture.Evidence, e => e.Kind == ActivityKind.PauseRemovalObserved);
        Assert.Equal(ActivityReason.StaleSource, capture.Evidence[^1].Reason);
        capture.Runtime.Observe([Bead(["stage:implement", "product-decision-required"])], capture.Runtime.InventoryVersion);
        Assert.Equal(2, capture.Items.Count);
    }

    [Fact]
    public void InventoryCapacityLossCannotInventHistoryForAnUnretainedBead()
    {
        using var capture = new ActivityCapture(inventoryLimit: 1);
        var other = Bead(["needs-human"]) with { Id = "ns-other" };
        capture.Runtime.Observe([Bead(), other]);
        Assert.Contains(capture.Evidence, e => e.Reason == ActivityReason.Capacity && e.Count == 1);
        capture.Runtime.Observe([]);
        capture.Runtime.Observe([other with { Labels = [] }]);
        Assert.Equal(ActivityKind.StateObserved, capture.Evidence[^1].Kind);
        Assert.DoesNotContain(capture.Evidence, e => e.Kind == ActivityKind.PauseRemovalObserved);
    }

    [Fact]
    public void FailedSinkDoesNotFailObservationAndScopeRestores()
    {
        using var capture = new ActivityCapture();
        using (var broken = new ActivityRuntime((_, _) => throw new IOException("PRIVATE_ERROR")))
            broken.Observe([Bead()]);
        Assert.Same(capture.Runtime, ActivityRuntime.Current);
        capture.Runtime.Observe([Bead()]);
        Assert.Single(capture.Items);
    }

    [Fact]
    public void DistinctRepairVisitsRemainDistinctAndBoundedProjectionRejectsNoRecord()
    {
        using var capture = new ActivityCapture();
        var state = new BeadWorkflowState("ns-proof", "task", "in_progress", "stage:implement", false, false, false, 1);
        foreach (var invocation in new[] { "invocation-1", "invocation-2" })
            capture.Runtime.Published(new(AgentRole.Reviewer, "ns-proof", invocation, "PRIVATE_LOG", null, [],
                PublicationReceipt: new(AgentRole.Reviewer, AgentOutcomeChoice.ChangesRequired, state, "owner", false, false)));
        Assert.Equal(2, capture.Items.Count);
        long sequence = 0;
        foreach (var item in capture.Items)
        {
            var record = ActivityProjector.Project(new(Guid.NewGuid(), ++sequence, Guid.NewGuid(), sequence),
                DateTimeOffset.UtcNow, item.Evidence, new("github.com", "owner", "repo"), item.Proof);
            Assert.NotNull(record);
            Assert.Equal(ActivityKind.ChangesRequired, record.Kind);
            Assert.Equal(ActivityStrength.Confirmed, record.Strength);
        }
    }
}
