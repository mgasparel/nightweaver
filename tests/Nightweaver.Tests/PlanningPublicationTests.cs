using System.Text.Json;
using System.Text.Json.Nodes;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Telemetry;

namespace Nightweaver.Tests;

public sealed class PlanningPublicationTests
{
    [Theory]
    [InlineData("intent")]
    [InlineData("graph")]
    [InlineData("receipt")]
    public async Task ConfirmationCannotAdoptANewClaimWithTheSameAssignee(string boundary)
    {
        using var fixture = new Fixture(singleTask: true);
        var changed = false;
        fixture.BeforeDetailRead = id =>
        {
            if (id != "product" || changed) return;
            var parent = fixture.Rows["product"];
            var ready = boundary switch
            {
                "intent" => fixture.IntentWriteCount == 1,
                "graph" => parent["metadata"]!["publication_graph"] is not null,
                _ => fixture.Comments.Any(c => c.Text.Contains(PlanningPublisher.ReleaseSchema, StringComparison.Ordinal))
            };
            if (!ready) return;
            changed = true;
            parent["updated_at"] = "new claim with the same assignee";
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.True(changed);
        Assert.Equal(boundary == "intent" ? 0 : 1, fixture.CreateAttemptCount);
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("other-revision")]
    [InlineData("missing-field")]
    public async Task InvalidRetainedCreationEvidenceStopsBeforeAnyWrite(string change)
    {
        using var fixture = new Fixture(singleTask: true) { CrashAfterIntentAt = 1 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterIntentAt = null;
        var metadata = fixture.Rows["product"]["metadata"]!.AsObject();
        var stored = Assert.Single(metadata);
        var value = JsonNode.Parse(stored.Value!.GetValue<string>())!.AsObject();
        metadata.Remove(stored.Key);
        if (change == "other-revision") value["revision"] = "other approved revision";
        if (change == "missing-field") value.Remove("resolved");
        var key = change == "missing-field" ? stored.Key : "publication_create_foreign";
        metadata[key] = change == "malformed" ? "{" : value.ToJsonString();
        var before = JsonSerializer.Serialize(fixture.Rows);
        var failure = await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
        Assert.Contains(key, failure.Message);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.Equal(1, fixture.IntentWriteCount);
        Assert.Equal(0, fixture.CreateAttemptCount);
    }

    [Fact]
    public async Task RevalidationCannotUseAReusedOwnerAfterInitialObservation()
    {
        using var fixture = new Fixture(singleTask: true);
        fixture.Rows["product"]["notes"] = "Guidance requires revalidation";
        var reads = 0;
        fixture.BeforeDetailRead = id =>
        {
            if (id == "product" && ++reads == 2) fixture.Rows["product"]["updated_at"] = "new claim";
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(0, fixture.WriteCount);
        Assert.Equal(0, fixture.IntentWriteCount);
    }

    [Fact]
    public async Task InterruptedIntentBeforeLaunchStaysUnresolvedAcrossPublisherRestart()
    {
        using var fixture = new Fixture(singleTask: true) { CrashAfterIntentAt = 1 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterIntentAt = null;
        var before = JsonSerializer.Serialize(fixture.Rows);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.Equal(0, fixture.CreateAttemptCount);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
    }

    [Fact]
    public async Task ValidLegacyTaskWithoutIntentRetainsItsIdentity()
    {
        using var fixture = new Fixture(singleTask: true) { CrashAfterWrite = 1 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.Rows["product"]["metadata"] = new JsonObject { ["operator"] = "keep" };
        fixture.CrashAfterWrite = null;
        var id = Assert.Single(fixture.Tasks).Id;
        Assert.True((await fixture.PublishWithNewPublisher()).Succeeded);
        Assert.Equal(id, Assert.Single(fixture.Tasks).Id);
        Assert.Equal(1, fixture.CreateAttemptCount);
        Assert.Equal("keep", PlanningPublisher.Metadata(fixture.Parent, "operator"));
    }

    [Fact]
    public async Task StaleSessionCannotUseAReusedAssignee()
    {
        using var fixture = new Fixture(singleTask: true);
        fixture.Rows["product"]["updated_at"] = "new claim";
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithStaleSession);
        Assert.Equal(0, fixture.IntentWriteCount);
        Assert.Equal(0, fixture.CreateAttemptCount);
    }
    [Theory]
    [InlineData("confirmation")]
    [InlineData("launch")]
    [InlineData("commit")]
    public async Task OverlappingPublisherInstancesCannotLaunchAnotherCreate(string boundary)
    {
        using var fixture = new Fixture(singleTask: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeCommand = async request =>
        {
            var args = request.Arguments;
            var stop = boundary switch
            {
                "confirmation" => fixture.IntentWriteCount == 1 && args.Contains("show"),
                "launch" => args.Contains("create"),
                _ => fixture.CreateCount == 1 && args.Contains("list")
            };
            if (!stop) return;
            fixture.BeforeCommand = null;
            entered.SetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        var first = fixture.Publish();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
            Assert.DoesNotContain("planned", fixture.Parent.Labels);
        }
        finally { release.SetResult(); }
        Assert.True((await first).Succeeded);
        Assert.Equal(1, fixture.CreateAttemptCount);
    }

    [Fact]
    public async Task PendingCreateCanFinishAfterRecoveryRefusesASecondLaunch()
    {
        using var fixture = new Fixture(singleTask: true) { DeferCreate = true };
        var failure = await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Contains("entry 'first', observed IDs []", failure.Message);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
        Assert.Equal(1, fixture.CreateAttemptCount);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        await fixture.FinishDeferredCreate();
        Assert.True((await fixture.PublishWithNewPublisher()).Succeeded);
        Assert.Equal(1, fixture.CreateCount);
        Assert.Equal(1, fixture.CreateAttemptCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UncertainIntentWritesPreserveEvidenceWithoutAdoptingReadbackAuthority(int lost)
    {
        using var fixture = new Fixture(singleTask: true) { LoseIntentResponseAt = lost };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(lost == 1 ? 0 : 1, fixture.CreateAttemptCount);
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
        fixture.LoseIntentResponseAt = null;
        if (lost == 1)
            await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
        else Assert.True((await fixture.PublishWithNewPublisher()).Succeeded);
        Assert.Equal(lost == 1 ? 0 : 1, fixture.CreateAttemptCount);
    }

    [Fact]
    public async Task UnconfirmedIntentNeverLaunchesACreate()
    {
        using var fixture = new Fixture(singleTask: true) { RejectIntentAt = 1 };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(0, fixture.CreateAttemptCount);
        Assert.Empty(fixture.Tasks);
        fixture.RejectIntentAt = null;
        Assert.True((await fixture.PublishWithNewPublisher()).Succeeded);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("owner")]
    [InlineData("status")]
    [InlineData("approval")]
    [InlineData("reused-owner")]
    public async Task ChangedAuthorityAfterIntentConfirmationStopsBeforeCreate(string change)
    {
        using var fixture = new Fixture(singleTask: true);
        Action action = () =>
        {
            var parent = fixture.Rows["product"];
            if (change == "pause") parent["labels"]!.AsArray().Add("needs-human");
            else if (change == "owner") parent["assignee"] = "human";
            else if (change == "status") parent["status"] = "open";
            else if (change == "approval") fixture.Comments[0] = fixture.Comments[0] with { Text = fixture.Comments[0].Text + " " };
            else parent["updated_at"] = "new claim with the same assignee";
        };
        // Change on the authority read after the intent confirmation read.
        fixture.BeforeCommand = request =>
        {
            if (fixture.IntentWriteCount == 1 && request.Arguments.Contains("--include-comments"))
            {
                fixture.BeforeCommand = null;
                action();
            }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(0, fixture.CreateAttemptCount);
        Assert.Equal(1, fixture.IntentWriteCount);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("conflict")]
    [InlineData("duplicate")]
    public async Task ConflictingIntentOrTaskEvidencePreservesHolds(string change)
    {
        using var fixture = new Fixture(singleTask: true) { CrashAfterWrite = 1 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        var metadata = fixture.Rows["product"]["metadata"]!.AsObject();
        var key = Assert.Single(metadata).Key;
        if (change == "malformed") metadata[key] = "{";
        else if (change == "conflict") metadata[key] = metadata[key]!.GetValue<string>().Replace("first", "foreign", StringComparison.Ordinal);
        else
        {
            var task = fixture.Rows.Values.Last().DeepClone().AsObject();
            task["id"] = "duplicate";
            fixture.Rows["duplicate"] = task;
            fixture.Edges.Add(new("duplicate", "product", "discovered-from"));
        }
        var before = JsonSerializer.Serialize(fixture.Rows);
        var failure = await Assert.ThrowsAsync<AgentStageInputException>(fixture.PublishWithNewPublisher);
        Assert.Contains("Maintainer: inspect the evidence and reconcile the task binding before guarded Retry.", failure.Message);
        if (change == "duplicate")
        {
            Assert.Contains("Product 'product'", failure.Message);
            Assert.Contains("revision '" + PlanningPublisher.Metadata(fixture.Tasks.First(), "publication_revision") + "'", failure.Message);
            Assert.Contains("entry 'first'", failure.Message);
            Assert.Contains("duplicate", failure.Message);
            Assert.Contains(fixture.Tasks.First().Id, failure.Message);
            Assert.Contains("The task binding is not unique and exact", failure.Message);
        }
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
        Assert.Equal(1, fixture.CreateAttemptCount);
    }

    [Fact]
    public async Task ConfirmedGraphPublicationExportsMilestoneWithoutCountingASecondApproval()
    {
        using var fixture = new Fixture(largeGraph: true) { LoseResponseAt = 9 };
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
        await using var telemetry = new OperationalTelemetry(OperationalHost.Runtime, new(receiver.Endpoint, "", 1),
            _ => { }, metricIntervalMilliseconds: 100);
        using var runtime = telemetry.EnterRuntime("0123456789abcdef0123456789abcdef");
        Assert.True((await fixture.Publish()).VerifiedState!.Planned);
        await receiver.WaitForSignalsAsync();
        await telemetry.StopAsync(false);
        var spans = receiver.Captures.Where(c => c.Signal == "traces").SelectMany(FirstPathTelemetryTests.DecodeRecords)
            .Select(span => OperationalTelemetryTests.Attributes(span, 9)).ToArray();
        Assert.Contains(spans, tags => tags.GetValueOrDefault("operation") == "lifecycle"
            && tags.GetValueOrDefault("outcome") == "planning_published"
            && tags.GetValueOrDefault("source") == "confirmed_publication"
            && tags.ContainsKey("observed.utc"));
        var names = receiver.Captures.Where(c => c.Signal == "metrics").SelectMany(FirstPathTelemetryTests.DecodeRecords)
            .Select(m => m.Only(1).String).ToArray();
        Assert.Contains("nightweaver.published.outcomes", names);
        Assert.DoesNotContain("nightweaver.approvals", names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8)]
    [InlineData(17)]
    public async Task LargeSevenTaskGraphPublishesWithBoundedArgumentsAndPreservesMetadata(int? lostResponse)
    {
        using var fixture = new Fixture(largeGraph: true) { LoseResponseAt = lostResponse };
        fixture.Rows["product"]["metadata"]!["operator"] = "keep me";
        if (lostResponse == 8)
        {
            await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
            Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
            fixture.LoseResponseAt = null;
        }
        Assert.True((await fixture.Publish()).VerifiedState!.Planned);
        Assert.Equal(7, fixture.CreateCount);
        Assert.Equal("keep me", fixture.Rows["product"]["metadata"]!["operator"]!.GetValue<string>());
        Assert.True(PlanningPublisher.Metadata(fixture.Parent, "publication_graph")!.Length > 32767);
        Assert.All(fixture.Tasks, task => Assert.True(PlanningPublicationGate.Allows(task, fixture.Inventory)));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(17)]
    public async Task FailedLargeMetadataWritesStayHeldAndResumeWithoutDuplicateTasks(int rejected)
    {
        using var fixture = new Fixture(largeGraph: true) { RejectWriteAt = rejected };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
        fixture.RejectWriteAt = null;
        Assert.True((await fixture.Publish()).VerifiedState!.Planned);
        Assert.Equal(7, fixture.CreateCount);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("notes")]
    public async Task AuthorityChangesDuringRetainedParentReadStopMembershipWrites(string change)
    {
        using var fixture = new Fixture();
        fixture.BeforeDetailRead = id =>
        {
            if (id != "product" || fixture.WriteCount != 3) return;
            fixture.BeforeDetailRead = null;
            if (change == "pause") fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
            else fixture.Rows["product"]["notes"] = "Updated requirement";
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(3, fixture.WriteCount);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
    }

    [Fact]
    public async Task TwoTaskPublicationBoundsNativeGroundingProcessStarts()
    {
        using var fixture = new Fixture();
        var before = fixture.GroundingReadCount;
        Assert.True((await fixture.Publish()).Succeeded);
        // One initial revalidation observation and fresh authority before each of
        // graph mutations and creation-intent boundaries. Read-only graph checks need no extra sweep.
        Assert.InRange(fixture.GroundingReadCount - before, 1, 15);
        Assert.Equal(8, fixture.WriteCount);
        Assert.All(fixture.Tasks, task => Assert.True(PlanningPublicationGate.Allows(task, fixture.Inventory)));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("pause")]
    public async Task AuthorityChangesDuringDependencyReadStopBeforeLinking(string change)
    {
        using var fixture = new Fixture();
        fixture.BeforeDependencyRead = id =>
        {
            if (fixture.Rows[id]["metadata"]!["publication_entry"]!.GetValue<string>() != "second") return;
            if (change == "owner") fixture.Rows["product"]["assignee"] = "human";
            else fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(2, fixture.WriteCount);
        Assert.DoesNotContain(fixture.Edges, e => e.Type == "blocks");
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
    }

    [Theory]
    [InlineData(3, "owner")]
    [InlineData(3, "pause")]
    [InlineData(7, "owner")]
    [InlineData(7, "pause")]
    public async Task AuthorityChangesDuringGraphVerificationStopTheNextPublicationWrite(int writes, string change)
    {
        using var fixture = new Fixture();
        fixture.BeforeInventoryRead = () =>
        {
            if (fixture.WriteCount != writes) return;
            fixture.BeforeInventoryRead = null;
            if (change == "owner") fixture.Rows["product"]["assignee"] = "human";
            else fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(writes, fixture.WriteCount);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
    }

    [Theory]
    [InlineData("feature", true)]
    [InlineData("bug", true)]
    [InlineData("task", false)]
    public async Task FollowupProductProvenanceDoesNotExpandPublishedTaskMembership(string issueType, bool allowed)
    {
        using var fixture = new Fixture();
        await fixture.Publish();
        var edge = new BeadDependency("followup", "product", "discovered-from");
        fixture.Edges.Add(edge);
        fixture.Rows["followup"] = new JsonObject { ["id"] = "followup", ["title"] = "Follow-up work",
            ["issue_type"] = issueType, ["status"] = "open", ["priority"] = 2,
            ["labels"] = new JsonArray(), ["created_at"] = "2026-11-13T00:00:00Z",
            ["dependencies"] = JsonSerializer.SerializeToNode(new[]
                { new { issue_id = edge.IssueId, depends_on_id = edge.DependsOnId, type = edge.Type } }) };
        var inventory = fixture.Inventory;
        var published = inventory.ById.Values.Where(b => b.Id != "followup" && b.IssueType == "task").ToArray();
        Assert.All(published, task => Assert.Equal(allowed, PlanningPublicationGate.Allows(task, inventory)));
        Assert.Equal(!allowed, PlanningPublicationGate.HasProductGraphConflict(fixture.Parent, inventory));
        var before = JsonSerializer.Serialize(fixture.Rows);
        var writes = fixture.WriteCount;
        Assert.Equal(!allowed, await fixture.Reconcile());
        if (allowed)
        {
            Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
            Assert.Equal(writes, fixture.WriteCount);
        }
        else
            Assert.All(published, task => Assert.Contains("needs-human", fixture.Inventory.ById[task.Id].Labels));
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("bug")]
    public async Task PublishedTaskCannotEscapeGraphValidationByChangingItsType(string issueType)
    {
        using var fixture = new Fixture();
        await fixture.Publish();
        fixture.Rows.Values.ElementAt(1)["issue_type"] = issueType;
        Assert.True(PlanningPublicationGate.HasProductGraphConflict(fixture.Parent, fixture.Inventory));
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
        Assert.True(await fixture.Reconcile());
    }

    [Theory]
    [InlineData("content")]
    [InlineData("edge")]
    [InlineData("child")]
    public async Task FinalGraphInventoryRejectsChangesAfterHoldRelease(string change)
    {
        using var fixture = new Fixture();
        fixture.BeforeInventoryRead = () =>
        {
            if (fixture.WriteCount != 7) return;
            fixture.BeforeInventoryRead = null;
            var task = fixture.Rows.Values.Last();
            var id = task["id"]!.GetValue<string>();
            if (change == "content") task["acceptance_criteria"] = "human edit after release";
            else if (change == "edge") task["dependencies"]!.AsArray().Add(new JsonObject
            { ["issue_id"] = id, ["depends_on_id"] = "other", ["type"] = "blocks" });
            else
            {
                var extra = task.DeepClone().AsObject();
                extra["id"] = "unmapped";
                extra["dependencies"] = JsonSerializer.SerializeToNode(new[]
                { new { issue_id = "unmapped", depends_on_id = "product", type = "discovered-from" } });
                fixture.Rows["unmapped"] = extra;
            }
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Null(fixture.BeforeInventoryRead);
        Assert.Equal(7, fixture.WriteCount);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("pause")]
    [InlineData("status")]
    public async Task AuthorityChangesDuringGroundingStopBeforeTheNextTaskWrite(string change)
    {
        using var fixture = new Fixture();
        fixture.BeforeGitRead = () =>
        {
            if (fixture.CreateCount != 1) return;
            fixture.BeforeGitRead = null;
            var product = fixture.Rows["product"];
            if (change == "owner") product["assignee"] = "human";
            else if (change == "pause") product["labels"]!.AsArray().Add("needs-human");
            else product["status"] = "closed";
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(1, fixture.CreateCount);
        Assert.Equal(1, fixture.WriteCount);
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CrashAfterEveryCreateLinkReceiptOrReleaseResumesWithoutDuplicates(int stop)
    {
        using var fixture = new Fixture { CrashAfterWrite = stop };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
        Assert.False(await fixture.Reconcile()); // A valid incomplete publication is not a graph conflict.
        fixture.CrashAfterWrite = null;
        var result = await fixture.Publish();
        Assert.True(result.Succeeded);
        Assert.Equal(2, fixture.CreateCount);
        Assert.Equal(2, fixture.Tasks.Count());
        Assert.Equal(3, fixture.Edges.Count);
        Assert.All(fixture.Tasks, task => Assert.True(PlanningPublicationGate.Allows(task, fixture.Inventory)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task LostResponsesReconcileActualEffectsWithoutRepeatingWrites(int lost)
    {
        using var fixture = new Fixture { LoseResponseAt = lost };
        if (lost == 4)
        {
            await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
            Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
            fixture.LoseResponseAt = null;
        }
        var result = await fixture.Publish();
        Assert.True(result.Succeeded);
        Assert.True(result.VerifiedState!.Planned);
        Assert.Equal(2, fixture.CreateCount);
        Assert.Equal(8, fixture.WriteCount);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("foreign")]
    [InlineData("pause")]
    [InlineData("extra-child")]
    [InlineData("extra-edge")]
    [InlineData("approval-edit")]
    [InlineData("hold")]
    public async Task ConflictsPreserveEvidenceAndNeverPublish(string conflict)
    {
        using var fixture = new Fixture { CrashAfterWrite = 2 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        var child = fixture.Rows.Values.Last();
        switch (conflict)
        {
            case "content": child["description"] = "human-edited contract"; break;
            case "foreign": child["assignee"] = "another-owner"; break;
            case "pause": fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human"); break;
            case "extra-child": fixture.Edges.Add(new("manual", "product", "discovered-from")); break;
            case "extra-edge":
                var childId = child["id"]!.GetValue<string>();
                fixture.Edges.Add(new(childId, "other", "blocks"));
                child["dependencies"] = JsonSerializer.SerializeToNode(fixture.Edges.Where(e => e.IssueId == childId)
                    .Select(e => new { issue_id = e.IssueId, depends_on_id = e.DependsOnId, type = e.Type }));
                break;
            case "approval-edit": fixture.Comments[0] = fixture.Comments[0] with { Text = fixture.Comments[0].Text + " " }; break;
            case "hold": child["labels"] = new JsonArray(); break;
        }
        var before = JsonSerializer.Serialize(fixture.Rows);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        Assert.Equal(2, fixture.CreateCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task FailedWritesRemainNonexecutableAndCanBeReconciled(int rejected)
    {
        using var fixture = new Fixture { RejectWriteAt = rejected };
        await Assert.ThrowsAnyAsync<NightweaverException>(fixture.Publish);
        Assert.All(fixture.Tasks, task => Assert.False(PlanningPublicationGate.Allows(task, fixture.Inventory)));
        fixture.RejectWriteAt = null;
        if (rejected == 1)
        {
            await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
            Assert.Equal(0, fixture.CreateCount);
        }
        else
        {
            Assert.True((await fixture.Publish()).VerifiedState!.Planned);
            Assert.Equal(2, fixture.CreateCount);
        }
    }

    [Fact]
    public async Task ChangedReleasedGraphRequiresReplanningAndManualTasksRemainIndependent()
    {
        using var fixture = new Fixture { CrashAfterWrite = 6 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        var child = fixture.Rows.Values.Last();
        child["status"] = "in_progress";
        child["assignee"] = "nightweaver:demo";
        var before = JsonSerializer.Serialize(fixture.Rows);
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        var manual = fixture.Parent with { Id = "manual", IssueType = "task", Payload = JsonSerializer.SerializeToElement(new { }) };
        Assert.True(PlanningPublicationGate.Allows(manual, fixture.Inventory));
    }

    private static readonly JsonSerializerOptions HostJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task GuidanceDoesNotResumePauseAndExplicitResumeRevalidatesThenReusesExistingTasks()
    {
        using var fixture = new Fixture { CrashAfterWrite = 2 };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        fixture.CrashAfterWrite = null;
        fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
        fixture.AddComment(BeadsWriter.CreateHumanGuidanceComment("resume", "Continue the same approved tasks; retain all evidence."));
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.Equal(2, fixture.WriteCount);
        fixture.Rows["product"]["labels"] = new JsonArray("ready-for-planning", "stage:plan-approved");
        var revalidation = await fixture.Publish();
        Assert.Equal("stage:plan-challenge", revalidation.VerifiedState!.Stage);
        Assert.True(revalidation.PublicationReceipt!.ClaimReleased);
        Assert.All(fixture.Tasks, task => Assert.Contains("plan-unpublished", task.Labels));
        await fixture.Reapprove();
        Assert.True((await fixture.Publish()).VerifiedState!.Planned);
        Assert.Equal(2, fixture.CreateCount);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("dependencies")]
    [InlineData("missing")]
    [InlineData("pause")]
    public async Task ExecutingGraphChangesStopFurtherDeliveryAndPreserveCompletedWork(string change)
    {
        using var fixture = new Fixture();
        await fixture.Publish();
        var first = fixture.Rows.Values.ElementAt(1);
        var second = fixture.Rows.Values.ElementAt(2);
        first["status"] = "closed";
        second["status"] = "in_progress";
        second["assignee"] = "nightweaver:demo";
        Assert.All(fixture.Tasks, t => Assert.True(PlanningPublicationGate.Allows(t, fixture.Inventory)));
        switch (change)
        {
            case "content": second["acceptance_criteria"] = "new executing contract"; break;
            case "dependencies": second["dependencies"] = new JsonArray(); break;
            case "missing": fixture.Rows.Remove(first["id"]!.GetValue<string>()); break;
            case "pause": fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human"); break;
        }
        var before = JsonSerializer.Serialize(fixture.Rows);
        Assert.All(fixture.Tasks, t => Assert.False(PlanningPublicationGate.Allows(t, fixture.Inventory)));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Rows));
        Assert.Equal("closed", first["status"]!.GetValue<string>());
        Assert.Equal("in_progress", second["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("owned")]
    [InlineData("foreign")]
    [InlineData("closed")]
    public async Task DiscoveryPublishesGraphAttentionWhilePreservingClaimsAndCompletedWork(string ownership)
    {
        using var fixture = new Fixture();
        await fixture.Publish();
        var first = fixture.Rows.Values.ElementAt(1);
        var second = fixture.Rows.Values.ElementAt(2);
        first["status"] = "closed";
        second["acceptance_criteria"] = "changed executing contract";
        second["status"] = ownership switch { "closed" => "closed", "unassigned" => "open", _ => "in_progress" };
        second["assignee"] = ownership switch { "owned" => "nightweaver:demo", "foreign" => "other-owner", _ => null };
        var expectedStatus = second["status"]!.GetValue<string>();
        var expectedOwner = second["assignee"]?.GetValue<string>();
        var changed = await fixture.Reconcile();
        Assert.Equal(ownership is "unassigned" or "owned", changed);
        var task = fixture.Inventory.ById[second["id"]!.GetValue<string>()];
        Assert.Equal(changed, task.Labels.Contains("needs-human"));
        Assert.Equal(expectedStatus, task.Status);
        Assert.Equal(expectedOwner, task.Assignee);
        Assert.Equal("closed", first["status"]!.GetValue<string>());
        var writes = fixture.WriteCount;
        Assert.False(await fixture.Reconcile());
        Assert.Equal(writes, fixture.WriteCount);
    }

    [Theory]
    [InlineData("missing", "content")]
    [InlineData("invalid", "pause")]
    [InlineData("all", "content")]
    [InlineData("all", "native-only")]
    [InlineData("all", "snapshot-only")]
    [InlineData("conflicting", "content")]
    public async Task LostProvenanceRefusesDiscoveryAndExplicitDispatchAndSurfacesAttention(string provenance, string change)
    {
        using var fixture = new Fixture(singleTask: true);
        await fixture.Publish();
        var task = fixture.Rows.Values.Last();
        var id = task["id"]!.GetValue<string>();
        if (provenance == "all") task["metadata"] = new JsonObject();
        else if (provenance == "missing") task["metadata"]!.AsObject().Remove("publication_product");
        else task["metadata"]!["publication_product"] = provenance == "invalid" ? JsonValue.Create(42) : JsonValue.Create("other");
        if (change == "pause") fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
        else task["acceptance_criteria"] = "edited contract";
        if (change == "native-only") fixture.Rows["product"]["metadata"]!.AsObject().Remove("publication_graph");
        if (change == "snapshot-only")
        {
            task["dependencies"] = new JsonArray();
            fixture.Edges.RemoveAll(e => e.IssueId == id);
        }
        var before = task.ToJsonString();
        var inventory = fixture.Inventory;
        var ready = new HashSet<string>([id], StringComparer.Ordinal);
        var discovery = DeliveryDiscoveryPlanner.Plan(new(inventory,
            new(ready, new HashSet<string>(StringComparer.Ordinal), ready, new HashSet<string>(StringComparer.Ordinal))), 1);
        Assert.Empty(discovery.DeliverySelected);
        await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.ReadRequestedState(id));
        Assert.Equal(before, task.ToJsonString());
        Assert.True(await fixture.Reconcile());
        Assert.Contains("needs-human", fixture.Inventory.ById[id].Labels);
        var paused = task.DeepClone().AsObject();
        paused["labels"] = new JsonArray();
        Assert.Equal(before, paused.ToJsonString());
        Assert.Equal("open", task["status"]!.GetValue<string>());
        Assert.Null(task["assignee"]);
    }

    [Fact]
    public async Task NativeManualTaskWithoutPublicationEvidenceRetainsDiscoveryAndDispatch()
    {
        using var fixture = new Fixture();
        fixture.Rows["manual"] = new JsonObject { ["id"] = "manual", ["title"] = "Manual work",
            ["issue_type"] = "task", ["status"] = "open", ["priority"] = 2,
            ["labels"] = new JsonArray(), ["created_at"] = "2026-11-13T00:00:00Z",
            ["dependencies"] = JsonSerializer.SerializeToNode(new[] { new { issue_id = "manual", depends_on_id = "product", type = "discovered-from" } }) };
        var inventory = fixture.Inventory;
        var ready = new HashSet<string>(["manual"], StringComparer.Ordinal);
        var discovery = DeliveryDiscoveryPlanner.Plan(new(inventory,
            new(ready, new HashSet<string>(StringComparer.Ordinal), ready, new HashSet<string>(StringComparer.Ordinal))), 1);
        Assert.Equal("manual", Assert.Single(discovery.DeliverySelected).Bead.Id);
        Assert.Equal("manual", (await fixture.ReadRequestedState("manual")).Bead.Id);
        Assert.False(await fixture.Reconcile());
    }

    [Theory]
    [InlineData(5, "content")]
    [InlineData(5, "pause")]
    [InlineData(6, "content")]
    [InlineData(6, "pause")]
    [InlineData(7, "content")]
    [InlineData(7, "pause")]
    public async Task LostProvenanceDuringPartialReleaseCannotBecomeManualWork(int stop, string change)
    {
        using var fixture = new Fixture { CrashAfterWrite = stop };
        await Assert.ThrowsAsync<OperationCanceledException>(fixture.Publish);
        var task = fixture.Rows.Values.ElementAt(1);
        var id = task["id"]!.GetValue<string>();
        Assert.Equal(stop == 5, fixture.Inventory.ById[id].Labels.Contains("plan-unpublished"));
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
        task["metadata"] = new JsonObject();
        if (change == "content") task["acceptance_criteria"] = "edited during interrupted publication";
        else fixture.Rows["product"]["labels"]!.AsArray().Add("needs-human");
        task["assignee"] = null;
        var before = task.ToJsonString();
        var ready = new HashSet<string>([id], StringComparer.Ordinal);
        var discovery = DeliveryDiscoveryPlanner.Plan(new(fixture.Inventory,
            new(ready, new HashSet<string>(StringComparer.Ordinal), ready, new HashSet<string>(StringComparer.Ordinal))), 1);
        Assert.Empty(discovery.DeliverySelected);
        await Assert.ThrowsAsync<AgentStageInputException>(() => fixture.ReadRequestedState(id));
        Assert.Equal(before, task.ToJsonString());
        Assert.True(await fixture.Reconcile());
        Assert.Contains("needs-human", fixture.Inventory.ById[id].Labels);
        var paused = task.DeepClone().AsObject();
        paused["labels"] = JsonNode.Parse(before)!["labels"]!.DeepClone();
        Assert.Equal(before, paused.ToJsonString());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("content")]
    [InlineData("hold")]
    public async Task DependencyReconciliationRefreshesTargetAfterInitialValidation(string change)
    {
        using var fixture = new Fixture();
        string? preserved = null;
        fixture.BeforeDependencyRead = id =>
        {
            var task = fixture.Rows[id];
            if (task["metadata"]!["publication_entry"]!.GetValue<string>() != "second") return;
            if (change == "foreign") task["assignee"] = "another-owner";
            else if (change == "content") task["description"] = "human edited contract";
            else task["labels"] = new JsonArray();
            preserved = task.ToJsonString();
        };
        await Assert.ThrowsAsync<AgentStageInputException>(fixture.Publish);
        Assert.NotNull(preserved);
        Assert.Equal(preserved, fixture.Rows.Values.Last().ToJsonString());
        Assert.DoesNotContain(fixture.Edges, e => e.Type == "blocks");
        Assert.Equal(2, fixture.WriteCount);
        Assert.DoesNotContain("planned", fixture.Parent.Labels);
    }

    private sealed class Fixture : IDisposable
    {
        internal Action? BeforeGitRead { get; set; }
        internal Action? BeforeInventoryRead { get; set; }
        private readonly TempDirectory temp = new();
        internal Dictionary<string, JsonObject> Rows { get; } = new(StringComparer.Ordinal);
        internal List<BeadDependency> Edges { get; } = [];
        internal List<BeadCommentSnapshot> Comments { get; } = [];
        internal int? CrashAfterWrite { get; set; }
        internal int? LoseResponseAt { get; set; }
        internal int? RejectWriteAt { get; set; }
        internal int WriteCount { get; private set; }
        internal int CreateCount { get; private set; }
        internal int CreateAttemptCount { get; private set; }
        internal int IntentWriteCount { get; private set; }
        internal int? RejectIntentAt { get; set; }
        internal int? LoseIntentResponseAt { get; set; }
        internal int? CrashAfterIntentAt { get; set; }
        internal Action? BeforeCreate { get; set; }
        internal Func<ProcessRequest, Task>? BeforeCommand { get; set; }
        internal bool DeferCreate { get; set; }
        private ProcessRequest? deferredCreate;
        internal Task<ProcessResult> FinishDeferredCreate()
        {
            DeferCreate = false;
            return Respond(deferredCreate!, TestContext.Current.CancellationToken);
        }
        internal Task<AgentStageResult> PublishWithNewPublisher() => new PlanningPublisher(runner).PublishAsync(session, TestContext.Current.CancellationToken);
        internal Task<AgentStageResult> PublishWithStaleSession() => new PlanningPublisher(runner)
            .PublishAsync(session with { PublicationVersion = "initial claim" }, TestContext.Current.CancellationToken);
        internal int GroundingReadCount => runner.Requests.Count(r => r.FileName == "git" && r.Arguments.Contains("status"));
        internal Action<string>? BeforeDependencyRead { get; set; }
        internal Action<string>? BeforeDetailRead { get; set; }
        private readonly AgentStageSession session;
        private readonly PlanningPublisher publisher;
        private readonly RecordingProcessRunner runner;
        internal BeadsInventory Inventory => BeadsReader.Parse(JsonSerializer.Serialize(Rows.Values));
        internal BeadSnapshot Parent => Inventory.ById["product"];
        internal IEnumerable<BeadSnapshot> Tasks => Inventory.ById.Values.Where(b => b.IssueType == "task");

        internal Fixture(bool singleTask = false, bool largeGraph = false)
        {
            Rows["product"] = new JsonObject { ["id"] = "product", ["title"] = "Requested behavior", ["issue_type"] = "feature",
                ["description"] = "Contract", ["status"] = "in_progress", ["assignee"] = "nightweaver:demo",
                ["priority"] = 2, ["created_at"] = "2026-11-13T00:00:00Z",
                ["updated_at"] = "initial claim",
                ["labels"] = new JsonArray("ready-for-planning", "stage:plan-approved"), ["metadata"] = new JsonObject() };
            var settings = new AgentSettings(AgentCli.Codex, null, "low", 1);
            var project = new ProjectConfig(ProjectSlug.Parse("demo"), "config.json", new(temp.Path, "main"), new(1, 1, 1), new(3, 2),
                settings, new(AgentRoles.All.ToDictionary(r => r, _ => settings)));
            session = new(new(temp.Path), project, new(temp.Path, "main"), "product", AgentRole.Planner,
                WorktreePurpose.Planning, new(temp.Path, new string('a', 40)), BeadWorkflowState.Parse(Parent), "nightweaver:demo",
                new("product", "feature", "stage:plan-approved", 0), false);
            runner = new RecordingProcessRunner(async (request, token) =>
            {
                if (BeforeCommand is { } before) await before(request);
                return await Respond(request, token);
            });
            publisher = new(runner);
            var grounding = new PlanningEvidence(runner).CaptureAsync(session, CancellationToken.None).GetAwaiter().GetResult();
            var plan = new PlanningDocument(PlanningEvidence.CandidateSchema, grounding,
                Tasks: [new("first", "First behavior", "Observable first", "Api.cs", "Advisory", ["First works"], "Check first", []),
                    new("second", "Second behavior", "Observable second", "Contract.cs", "Advisory", ["Second works"], "Check second", ["first"])],
                Constraints: [new("Preserve data", "Product contract")], Coverage: [new("Behavior", ["first", "second"], null, null)]);
            if (singleTask) plan = plan with { Tasks = [plan.Tasks![0]], Coverage = [new("Behavior", ["first"], null, null)] };
            if (largeGraph)
            {
                var tasks = Enumerable.Range(0, 7).Select(i => new PlanningTask("entry" + i, "Behavior " + i,
                    "Observable behavior", "Api.cs", new string('x', 8000), ["Behavior works"], "Component checks", [])).ToArray();
                plan = plan with { Tasks = tasks, Coverage = [new("Behavior", tasks.Select(t => t.Id).ToArray(), null, null)] };
            }
            AddComment(JsonSerializer.Serialize(plan, PlanningEvidence.JsonOptions));
            var review = new PlanningDocument(PlanningEvidence.ReviewSchema, grounding, PlanCommentId: "1",
                PlanRevision: PlanningEvidence.Hash(Comments[0].Text), Assessment: "Complete", Findings: [], Questions: []);
            AddComment(JsonSerializer.Serialize(review, PlanningEvidence.JsonOptions));
            var approval = new PlanningApproval("1", PlanningEvidence.Hash(Comments[0].Text), "2", PlanningEvidence.Hash(Comments[1].Text), grounding);
            AddComment(JsonSerializer.Serialize(new { schema = "nightweaver-planning-outcome-v1", outcome = "Approved", planning_approval = approval },
                HostJsonOptions));
        }

        internal Task<AgentStageResult> Publish() => publisher.PublishAsync(session, TestContext.Current.CancellationToken);
        internal Task<(BeadSnapshot Bead, BeadWorkflowState State)> ReadRequestedState(string id) =>
            new AgentStageCoordinator(runner).ReadRequestedStateAsync(session.Repository, id, TestContext.Current.CancellationToken);
        internal Task<bool> Reconcile() => new PlanningGraphReconciler(runner).PauseChangedGraphsAsync(session.Repository,
            Inventory, "nightweaver:demo", TestContext.Current.CancellationToken);
        internal void AddComment(string text) => Comments.Add(new((Comments.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), text,
            DateTimeOffset.UnixEpoch.AddSeconds(Comments.Count)));

        internal async Task Reapprove()
        {
            Rows["product"]["status"] = "in_progress";
            Rows["product"]["assignee"] = "nightweaver:demo";
            Rows["product"]["labels"] = new JsonArray("ready-for-planning", "stage:plan-approved");
            var grounding = await new PlanningEvidence(runner).CaptureAsync(session, TestContext.Current.CancellationToken);
            AddComment(JsonSerializer.Serialize(new PlanningDocument(PlanningEvidence.ReviewSchema, grounding,
                PlanCommentId: "1", PlanRevision: PlanningEvidence.Hash(Comments[0].Text), Assessment: "Revalidated guidance",
                Revalidation: "Guidance preserves candidate contracts and task boundaries.", Findings: [], Questions: []), PlanningEvidence.JsonOptions));
            var review = Comments[^1];
            var approval = new PlanningApproval("1", PlanningEvidence.Hash(Comments[0].Text), review.Id, PlanningEvidence.Hash(review.Text), grounding);
            AddComment(JsonSerializer.Serialize(new { schema = "nightweaver-planning-outcome-v1", outcome = "Approved", planning_approval = approval }, HostJsonOptions));
        }

        private Task<ProcessResult> Respond(ProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var args = request.Arguments.ToList();
            if (args.Sum(a => a.Length + 3) > 30000)
                throw new IOException("Native command line exceeds the Windows process limit.");
            string Value(string flag) => args[args.IndexOf(flag) + 1];
            string output;
            var write = args.Contains("--dolt-auto-commit");
            string MetadataText()
            {
                using var stream = new FileStream(Value("--metadata")[1..], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var text = new StreamReader(stream);
                return text.ReadToEnd();
            }
            var intentWrite = write && args.Contains("--metadata") && Value("--metadata").StartsWith('@')
                && MetadataText().Contains("publication_create_", StringComparison.Ordinal);
            if (intentWrite && IntentWriteCount + 1 == RejectIntentAt)
            {
                IntentWriteCount++;
                return Task.FromResult(new ProcessResult(1, "", "Intent write failed before commit"));
            }
            if (write && !intentWrite && WriteCount + 1 == RejectWriteAt)
            {
                WriteCount++;
                return Task.FromResult(new ProcessResult(1, "", "Write failed before commit"));
            }
            if (request.FileName == "git")
            {
                BeforeGitRead?.Invoke();
                output = args.Contains("status") ? "" : new string('a', 40);
            }
            else if (args.Contains("comments"))
            {
                if (args.Contains("add")) { AddComment(args[^1]); output = ""; }
                else output = JsonSerializer.Serialize(Comments.Select(c => new { id = c.Id, issue_id = "product", text = c.Text, created_at = c.CreatedAt }));
            }
            else if (args.Contains("create"))
            {
                if (deferredCreate is null) CreateAttemptCount++;
                if (DeferCreate)
                {
                    deferredCreate = request;
                    throw new IOException("The response was lost while the create can still finish later.");
                }
                BeforeCreate?.Invoke();
                var id = Value("--id");
                Assert.False(Rows.ContainsKey(id), "A create must never be replayed after an uncertain write.");
                CreateCount++;
                Rows[id] = new JsonObject { ["id"] = id, ["title"] = Value("--title"), ["issue_type"] = "task",
                    ["description"] = Value("--description"), ["acceptance_criteria"] = Value("--acceptance"),
                    ["status"] = "open", ["priority"] = 2, ["created_at"] = "2026-11-13T00:00:00Z",
                    ["labels"] = new JsonArray("plan-unpublished"), ["metadata"] = JsonNode.Parse(Value("--metadata")) };
                Edges.Add(new(id, "product", "discovered-from")); output = "";
            }
            else if (args.Contains("dep"))
            {
                if (args.Contains("add"))
                {
                    var i = args.IndexOf("add"); Edges.Add(new(args[i + 1], args[i + 2], Value("--type"))); output = "";
                }
                else
                {
                    var id = args[args.IndexOf("list") + 1];
                    if (Value("--direction") == "down") BeforeDependencyRead?.Invoke(id);
                    output = JsonSerializer.Serialize(Edges.Where(e => Value("--direction") == "up" ? e.DependsOnId == id : e.IssueId == id)
                        .Select(e => new { issue_id = e.IssueId, depends_on_id = e.DependsOnId, type = e.Type }));
                }
            }
            else if (args.Contains("update"))
            {
                var row = Rows[args[args.IndexOf("update") + 1]];
                Assert.Equal(row["status"]!.GetValue<string>(), Value("--if-status"));
                Assert.Equal(row["assignee"]?.GetValue<string>() ?? "", Value("--if-assignee"));
                var labels = row["labels"]!.AsArray();
                if (args.Contains("--remove-label"))
                {
                    var removed = labels.FirstOrDefault(l => l!.GetValue<string>() == Value("--remove-label"));
                    if (removed is not null) labels.Remove(removed);
                }
                if (args.Contains("--add-label")) labels.Add(Value("--add-label"));
                for (var i = 0; i < args.Count - 1; i++)
                    if (args[i] == "--set-metadata")
                    {
                        var pair = args[i + 1].Split('=', 2);
                        row["metadata"]![pair[0]] = JsonValue.Create(pair[1]);
                    }
                if (args.Contains("--metadata"))
                {
                    var path = Value("--metadata");
                    Assert.StartsWith("@", path);
                    using var stream = new FileStream(path[1..], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var patch = JsonNode.Parse(stream)!.AsObject();
                    foreach (var pair in patch) row["metadata"]![pair.Key] = pair.Value?.DeepClone();
                }
                row["status"] = Value("--status"); row["assignee"] = Value("--assignee") is "" ? null : Value("--assignee");
                if (row["id"]!.GetValue<string>() == "product")
                    row["updated_at"] = "mutation " + (IntentWriteCount + WriteCount + 1);
                output = JsonSerializer.Serialize(new[] { row });
            }
            else if (args.Contains("show"))
            {
                var id = args[args.IndexOf("show") + 1];
                BeforeDetailRead?.Invoke(id);
                output = Rows.TryGetValue(id, out var shown)
                    ? JsonSerializer.Serialize(new[] { Detail(shown, args.Contains("--include-comments")) }) : "[]";
            }
            else
            {
                BeforeInventoryRead?.Invoke();
                output = JsonSerializer.Serialize(Rows.Values);
            }
            if (write)
            {
                foreach (var row in Rows.Values)
                    row["dependencies"] = JsonSerializer.SerializeToNode(Edges.Where(e => e.IssueId == row["id"]!.GetValue<string>())
                        .Select(e => new { issue_id = e.IssueId, depends_on_id = e.DependsOnId, type = e.Type }));
                if (intentWrite)
                {
                    IntentWriteCount++;
                    if (IntentWriteCount == CrashAfterIntentAt) throw new OperationCanceledException("Interrupted after intent commit");
                    if (IntentWriteCount == LoseIntentResponseAt) throw new IOException("Lost intent write response");
                }
                else WriteCount++;
                if (!intentWrite && WriteCount == CrashAfterWrite) throw new OperationCanceledException("Simulated crash after durable write");
                if (!intentWrite && WriteCount == LoseResponseAt) throw new IOException("Lost response after durable write");
            }
            return Task.FromResult(new ProcessResult(0, output, ""));
        }

        private JsonObject Detail(JsonObject row, bool includeComments)
        {
            var detail = row.DeepClone().AsObject();
            if (includeComments)
                detail["comments"] = JsonSerializer.SerializeToNode(Comments.Select(c => new
                { id = c.Id, issue_id = "product", text = c.Text, created_at = c.CreatedAt }));
            return detail;
        }
        public void Dispose() => temp.Dispose();
    }
}
