using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Reconciles one immutable approved graph. All writes are followed by tracker reads;
/// the Product's final planned update is the admission barrier for the entire graph.</summary>
internal sealed class PlanningPublisher(IProcessRunner runner)
{
    internal const string ReleaseSchema = "nightweaver-plan-release-v1";
    private readonly BeadsReader reader = new(runner);
    private readonly PlanningEvidence evidence = new(runner);

    internal static string TaskId(string product, string revision, string entry) =>
        product + "-p" + PlanningEvidence.Hash(revision + "\n" + entry)[..20];

    internal static string Description(PlanningTask task, PlanningDocument plan) =>
        $"## Outcome\n{task.Outcome}\n\n## Relevant code and contracts\n{task.Context}\n\n"
        + $"## Sourced constraints\n{string.Join("\n", plan.Constraints!.Select(c => $"- {c.Requirement} (source: {c.Source})"))}\n\n"
        + $"## Advisory approach\n{task.Approach}\n\n## Verification\n{task.Verification}\n\n"
        + $"## Dependency ordering\n{string.Join("\n", task.DependsOn)}";

    internal async Task<AgentStageResult> PublishAsync(AgentStageSession session, CancellationToken token,
        bool releaseRevalidationClaim = true)
    {
        BeadsReader.EnsureMutationFreeBackend(session.Repository);
        var comments = await reader.ReadCommentsAsync(session.Repository, session.BeadId, token);
        var approval = ReadApproval(comments);
        var plan = PlanningEvidence.LatestCandidate(comments)?.Document
            ?? throw Refuse("The approved candidate is missing.");
        var invalid = PlanningEvidence.ValidateCandidate(plan);
        if (invalid is not null) throw Refuse(invalid);
        var tasks = plan.Tasks!;
        var ids = tasks.ToDictionary(t => t.Id, t => TaskId(session.BeadId, approval.PlanRevision, t.Id), StringComparer.Ordinal);
        var graph = tasks.Select(t => new PublishedPlanningTask(ids[t.Id], t.Title, Description(t, plan),
            string.Join("\n", t.AcceptanceCriteria), t.DependsOn.Select(d => ids[d]).ToArray())).ToArray();
        var graphJson = JsonSerializer.Serialize(graph, PlanningEvidence.JsonOptions);
        var receipt = JsonSerializer.Serialize(new { schema = ReleaseSchema, product = session.BeadId,
            revision = approval.PlanRevision, tasks = ids }, PlanningEvidence.JsonOptions);
        var releasing = comments.Any(c => c.Text == receipt);

        var grounding = await evidence.CaptureAsync(session, token);
        if (grounding != approval.Grounding && PlanningEvidence.ApprovalStillCurrent(approval, approval.Grounding, comments))
            return await RevalidateAsync(session, releaseRevalidationClaim, token);

        await CheckAuthorityAsync(session, approval, token);
        // Unmapped children are never adopted, overwritten, or removed.
        await CheckChildrenAsync(session, ids.Values, token);
        foreach (var task in tasks)
        {
            await CheckAuthorityAsync(session, approval, token);
            var id = ids[task.Id];
            var inventory = await reader.ReadSnapshotAsync(session.Repository, token);
            if (!inventory.ById.ContainsKey(id))
            {
                if (releasing) throw Refuse($"Verified task '{id}' disappeared; human-guided replanning is required.");
                await WriteUncertainAsync(session, ["create", "--id", id, "--type", "task", "--title", task.Title,
                    "--description", Description(task, plan), "--acceptance", string.Join("\n", task.AcceptanceCriteria),
                    "--labels", "plan-unpublished", "--deps", "discovered-from:" + session.BeadId,
                    "--metadata", JsonSerializer.Serialize(new { publication_product = session.BeadId,
                        publication_revision = approval.PlanRevision, publication_entry = task.Id }), "--json"], token);
            }
            var bead = await reader.ReadDetailAsync(session.Repository, id, token);
            CheckTask(bead, task, plan, session.BeadId, approval.PlanRevision, releasing);
        }

        foreach (var task in tasks)
        {
            await CheckAuthorityAsync(session, approval, token);
            var expected = Edges(session.BeadId, task, ids);
            var actual = await reader.ReadDownDependenciesAsync(session.Repository, ids[task.Id], token);
            if (actual.Except(expected).Any()) throw Refuse($"Task '{ids[task.Id]}' has conflicting dependencies.");
            foreach (var missing in expected.Except(actual))
            {
                if (releasing) throw Refuse("Released task dependencies changed; preserve executing work for human-guided replanning.");
                // dep add has no native status/assignee compare-and-swap guards.
                // Read the target last, immediately before each mutation, rather
                // than relying on its earlier creation-loop snapshot.
                CheckTask(await reader.ReadDetailAsync(session.Repository, missing.IssueId, token), task, plan,
                    session.BeadId, approval.PlanRevision, allowReleased: false);
                await WriteUncertainAsync(session, ["dep", "add", missing.IssueId, missing.DependsOnId, "--type", missing.Type], token);
                if (!(await reader.ReadDownDependenciesAsync(session.Repository, missing.IssueId, token)).Contains(missing))
                    throw Refuse($"Dependency '{missing.IssueId}' -> '{missing.DependsOnId}' was not confirmed.");
            }
        }
        await VerifyGraphAsync(session, approval, plan, ids, releasing, token);
        // Persist independent membership before any hold removal. Otherwise loss of
        // child metadata during partial release could disguise generated work as manual.
        var retained = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        if (Metadata(retained, "publication_graph") is { } existingGraph && existingGraph != graphJson
            || Metadata(retained, "publication_revision") is { } existingRevision && existingRevision != approval.PlanRevision)
            throw Refuse("Retained publication identity conflicts with the approved graph; preserve it for human reconciliation.");
        if (Metadata(retained, "publication_graph") != graphJson || Metadata(retained, "publication_revision") != approval.PlanRevision)
        {
            await CheckAuthorityAsync(session, approval, token);
            await WriteUncertainAsync(session, ["update", session.BeadId,
                "--set-metadata", "publication_revision=" + approval.PlanRevision,
                "--set-metadata", "publication_graph=" + graphJson,
                "--status", "in_progress", "--assignee", session.InitialAssignee!,
                "--if-status", "in_progress", "--if-assignee", session.InitialAssignee!, "--json"], token);
            retained = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
            if (Metadata(retained, "publication_graph") != graphJson || Metadata(retained, "publication_revision") != approval.PlanRevision)
                throw Refuse("Independent publication membership was not confirmed; all remaining holds are preserved.");
        }
        if (!releasing)
        {
            await WriteUncertainAsync(session, ["comments", "add", session.BeadId, receipt], token);
            if (!(await reader.ReadCommentsAsync(session.Repository, session.BeadId, token)).Any(c => c.Text == receipt))
                throw Refuse("Complete graph release evidence was not confirmed.");
        }
        foreach (var task in tasks)
        {
            await CheckAuthorityAsync(session, approval, token);
            var bead = await reader.ReadDetailAsync(session.Repository, ids[task.Id], token);
            CheckTask(bead, task, plan, session.BeadId, approval.PlanRevision, allowReleased: true);
            if (!bead.Labels.Contains("plan-unpublished", StringComparer.Ordinal)) continue;
            await WriteUncertainAsync(session, ["update", bead.Id, "--remove-label", "plan-unpublished",
                "--status", "open", "--assignee", "", "--if-status", "open", "--if-assignee", "", "--json"], token);
            if ((await reader.ReadDetailAsync(session.Repository, bead.Id, token)).Labels.Contains("plan-unpublished", StringComparer.Ordinal))
                throw Refuse($"Publication hold for '{bead.Id}' was not released.");
        }
        await VerifyGraphAsync(session, approval, plan, ids, allowReleased: true, token);
        foreach (var id in ids.Values)
            if ((await reader.ReadDetailAsync(session.Repository, id, token)).Labels.Contains("plan-unpublished", StringComparer.Ordinal))
                throw Refuse($"Task '{id}' still has a publication hold.");
        await CheckAuthorityAsync(session, approval, token);
        // All task holds are gone, but generated-task admission still requires this
        // exact parent revision and planned state. No multi-row transaction is assumed.
        await WriteUncertainAsync(session, ["update", session.BeadId, "--add-label", "planned",
            "--remove-label", "stage:plan-approved", "--set-metadata", "publication_revision=" + approval.PlanRevision,
            "--set-metadata", "publication_graph=" + graphJson,
            "--status", "open", "--assignee", "", "--if-status", "in_progress",
            "--if-assignee", session.InitialAssignee!, "--json"], token);
        var parent = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        var desired = session.InitialState with { Stage = null, Planned = true, Status = "open" };
        if (BeadWorkflowState.Parse(parent) != desired || parent.Assignee is not null
            || Metadata(parent, "publication_revision") != approval.PlanRevision
            || Metadata(parent, "publication_graph") != graphJson)
            throw Refuse("Final planned publication was not confirmed; preserve the graph and claim for inspection.");
        return new(AgentRole.Planner, session.BeadId, "planning-publication", "", null, [], desired,
            PublicationReceipt: new(AgentRole.Planner, AgentOutcomeChoice.Approved, desired, null, true, false),
            PlanningApproval: approval);
    }

    private async Task<AgentStageResult> RevalidateAsync(AgentStageSession session, bool releaseClaim, CancellationToken token)
    {
        // Guidance after explicit Resume changes grounding, even when it leaves the
        // candidate intact. Review that same candidate again; never replay Planner
        // creation or silently treat guidance as approval of changed requirements.
        var before = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        if (BeadWorkflowState.Parse(before) != session.InitialState || before.Assignee != session.InitialAssignee)
            throw Refuse("Workflow or ownership changed before targeted Challenger revalidation.");
        var args = new[] { "update", session.BeadId, "--add-label", "stage:plan-challenge", "--remove-label", "stage:plan-approved",
            "--status", releaseClaim ? "open" : "in_progress", "--assignee", releaseClaim ? "" : session.InitialAssignee!,
            "--if-status", "in_progress", "--if-assignee", session.InitialAssignee!, "--json" };
        await WriteUncertainAsync(session, args, token);
        var parent = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        var desired = session.InitialState with { Stage = "stage:plan-challenge", Status = releaseClaim ? "open" : "in_progress" };
        if (BeadWorkflowState.Parse(parent) != desired || parent.Assignee != (releaseClaim ? null : session.InitialAssignee))
            throw Refuse("Targeted Challenger revalidation handoff was not confirmed.");
        return new(AgentRole.Planner, session.BeadId, "planning-revalidation", "", null,
            ["Grounding changed; revalidate the retained exact candidate before resuming publication."], desired,
            PublicationReceipt: new(AgentRole.Planner, AgentOutcomeChoice.ReadyForReview, desired, parent.Assignee, releaseClaim, false));
    }

    private async Task VerifyGraphAsync(AgentStageSession session, PlanningApproval approval, PlanningDocument plan,
        Dictionary<string, string> ids, bool allowReleased, CancellationToken token)
    {
        await CheckAuthorityAsync(session, approval, token);
        await CheckChildrenAsync(session, ids.Values, token);
        foreach (var task in plan.Tasks!)
        {
            CheckTask(await reader.ReadDetailAsync(session.Repository, ids[task.Id], token), task, plan,
                session.BeadId, approval.PlanRevision, allowReleased);
            var actual = await reader.ReadDownDependenciesAsync(session.Repository, ids[task.Id], token);
            var expected = Edges(session.BeadId, task, ids);
            if (!actual.ToHashSet().SetEquals(expected)) throw Refuse("The complete dependency graph was not verified.");
        }
    }

    private async Task CheckChildrenAsync(AgentStageSession session, IEnumerable<string> ids, CancellationToken token)
    {
        var children = await reader.ReadUpDependenciesAsync(session.Repository, session.BeadId, token);
        var intended = ids.ToHashSet(StringComparer.Ordinal);
        if (children.Any(e => e.Type == "discovered-from" && !intended.Contains(e.IssueId)))
            throw Refuse("Unmapped preexisting Product tasks require human-guided reconciliation; no work was removed.");
    }

    private async Task CheckAuthorityAsync(AgentStageSession session, PlanningApproval approval, CancellationToken token)
    {
        var parent = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        if (BeadWorkflowState.Parse(parent) != session.InitialState || parent.Status != "in_progress"
            || parent.Assignee != session.InitialAssignee || string.IsNullOrWhiteSpace(parent.Assignee))
            throw Refuse("Product workflow, pause or ownership changed during publication.");
        var grounding = await evidence.CaptureAsync(session, token);
        if (!PlanningEvidence.ApprovalStillCurrent(approval, grounding,
            await reader.ReadCommentsAsync(session.Repository, session.BeadId, token)))
            throw Refuse("Approved content or grounding changed; preserve tasks for Challenger revalidation and human-guided replanning.");
    }

    internal static PlanningApproval ReadApproval(IReadOnlyList<BeadCommentSnapshot> comments)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        foreach (var comment in comments.OrderByDescending(c => c.CreatedAt))
        {
            try
            {
                using var doc = JsonDocument.Parse(comment.Text);
                var root = doc.RootElement;
                if (root.TryGetProperty("schema", out var schema) && schema.GetString() == "nightweaver-planning-outcome-v1"
                    && root.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "Approved"
                    && root.TryGetProperty("planning_approval", out var value)
                    && value.Deserialize<PlanningApproval>(options) is { } approval) return approval;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
        }
        throw Refuse("Exact Orchestrator-verified Planning approval was not found.");
    }

    private static BeadDependency[] Edges(string product, PlanningTask task, Dictionary<string, string> ids) =>
        [new(ids[task.Id], product, "discovered-from"), .. task.DependsOn.Select(d => new BeadDependency(ids[task.Id], ids[d], "blocks"))];

    private static void CheckTask(BeadSnapshot bead, PlanningTask task, PlanningDocument plan,
        string product, string revision, bool allowReleased)
    {
        if (bead.IssueType != "task" || bead.Status != "open" || bead.Assignee is not null
            || bead.Labels.Any(l => l != "plan-unpublished")
            || !allowReleased && !bead.Labels.Contains("plan-unpublished", StringComparer.Ordinal)
            || Metadata(bead, "publication_product") != product || Metadata(bead, "publication_revision") != revision
            || Metadata(bead, "publication_entry") != task.Id || Field(bead, "title") != task.Title
            || Field(bead, "description") != Description(task, plan)
            || Field(bead, "acceptance_criteria") != string.Join("\n", task.AcceptanceCriteria))
            throw Refuse($"Task '{bead.Id}' has conflicting content, publication hold, lifecycle or foreign ownership; preserve it for human-guided reconciliation.");
    }

    internal static string? Metadata(BeadSnapshot bead, string key) =>
        bead.Payload.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Field(BeadSnapshot bead, string key) => bead.Payload.TryGetProperty(key, out var value) ? value.GetString() : null;

    // A failed or lost response is uncertainty, never permission to repeat a create.
    // The caller immediately checks the exact intended effect through fresh reads.
    private async Task WriteUncertainAsync(AgentStageSession session, string[] args, CancellationToken token)
    {
        try { await runner.RunAsync(BeadsCommand.CreateWrite(session.Repository.Path, args, session.InitialAssignee), token); }
        catch (Exception exception) when (exception is ToolExecutionException or IOException) { }
    }

    private static AgentStageInputException Refuse(string reason) => new("Planning publication stopped: " + reason);
}
