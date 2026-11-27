using Nightweaver.ActivityFeed;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Execution;

/// <summary>Reconciles one immutable approved graph. All writes are followed by tracker reads;
/// the Product's final planned update is the admission barrier for the entire graph.</summary>
internal sealed class PlanningPublisher(IProcessRunner runner)
{
    internal const string ReleaseSchema = "nightweaver-plan-release-v1";
    internal const string IntentSchema = "nightweaver-plan-create-v1";
    private static readonly ConcurrentDictionary<(string Repository, string Product), byte> Active = new();
    private static readonly JsonSerializerOptions IntentJson = new(PlanningEvidence.JsonOptions)
    { RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed class Authority(string? version) { internal string? Version = version; }
    internal sealed record CreationIntent(string Schema, string Product, string Revision, string Entry, string Task, bool Resolved);
    private readonly BeadsReader reader = new(runner);
    private readonly PlanningEvidence evidence = new(runner);

    // Compatibility lookup only. New creates let Beads allocate the public ID.
    internal static string TaskId(string product, string revision, string entry) =>
        product + "-p" + PlanningEvidence.Hash(revision + "\n" + entry)[..20];

    internal static bool IsLegacyTaskId(string id, string product) =>
        id.Length == product.Length + 22 && id.StartsWith(product + "-p", StringComparison.Ordinal)
        && id[(product.Length + 2)..].All(Uri.IsHexDigit);

    internal static string Description(PlanningTask task, PlanningDocument plan) =>
        $"## Outcome\n{task.Outcome}\n\n## Relevant code and contracts\n{task.Context}\n\n"
        + $"## Sourced constraints\n{string.Join("\n", plan.Constraints!.Select(c => $"- {c.Requirement} (source: {c.Source})"))}\n\n"
        + $"## Advisory approach\n{task.Approach}\n\n## Verification\n{task.Verification}\n\n"
        + $"## Dependency ordering\n{string.Join("\n", task.DependsOn)}"
        + LocalInputDescription(task, plan);

    private static string LocalInputDescription(PlanningTask task, PlanningDocument plan) =>
        task.LocalInputIds is not { Length: > 0 } ? ""
        : "\n\n## Local inputs\n```json\n" + JsonSerializer.Serialize(
            task.LocalInputIds.Select(id => plan.LocalInputs!.Single(input => input.Id == id)),
            PlanningEvidence.JsonOptions) + "\n```";

    internal async Task<AgentStageResult> PublishAsync(AgentStageSession session, CancellationToken token,
        bool releaseRevalidationClaim = true)
    {
        // Runtime callers hold the process-lifetime physical repository mutex.
        // Also exclude overlap across publisher instances inside that process.
        var key = (ProcessLifetimeTargetRepositoryLock.ResolveIdentity(session.Repository.GitCommonDirectory), session.BeadId);
        if (!Active.TryAdd(key, 0)) throw Refuse("Another publication call owns this Product; preserve the retained evidence.");
        try
        {
            var published = await WorkflowTiming.MeasureAsync(WorkflowOperation.PlanningPublication, WorkflowPhase.Publication,
                () => PublishCoreAsync(session, token, releaseRevalidationClaim),
                result => OperationalTelemetry.Current?.Telemetry.RecordPublication(result)
                    ?? (result.PublicationReceipt is not null ? WorkflowResult.Success : WorkflowResult.Uncertain)).ConfigureAwait(false);
            ActivityRuntime.Current?.Published(published);
            return published;
        }
        finally { Active.TryRemove(key, out _); }
    }

    private async Task<AgentStageResult> PublishCoreAsync(AgentStageSession session, CancellationToken token,
        bool releaseRevalidationClaim = true)
    {
        BeadsReader.EnsureMutationFreeBackend(session.Repository);
        var authority = new Authority(session.PublicationVersion);
        var comments = await reader.ReadCommentsAsync(session.Repository, session.BeadId, token);
        var approval = ReadApproval(comments);
        var plan = PlanningEvidence.LatestCandidate(comments)?.Document
            ?? throw Refuse("The approved candidate is missing.");
        var invalid = PlanningEvidence.ValidateCandidate(plan);
        if (invalid is not null) throw Refuse(invalid);
        var tasks = plan.Tasks!;

        var initial = await evidence.ObserveAsync(session, token);
        CheckOwnership(session, initial.Product, authority);
        authority.Version = Version(initial.Product);
        await ValidateRetainedIntentsAsync(session, initial.Product, approval, tasks, token);
        var grounding = initial.Grounding;
        if (grounding != approval.Grounding && PlanningEvidence.ApprovalStillCurrent(approval, approval.Grounding, comments))
            return await RevalidateAsync(session, authority, releaseRevalidationClaim, token);

        // Unmapped children are never adopted, overwritten, or removed.
        var initialInventory = await reader.ReadSnapshotAsync(session.Repository, token);
        var ids = ResolveIds(session.BeadId, approval.PlanRevision, plan, initialInventory, comments, out var releasing);
        await CheckChildrenAsync(session, ids.Values, token);
        foreach (var task in tasks)
        {
            await CheckAuthorityAsync(session, approval, authority, token);
            var inventory = await reader.ReadSnapshotAsync(session.Repository, token);
            ids = ResolveIds(session.BeadId, approval.PlanRevision, plan, inventory, comments, out _);
            if (!ids.TryGetValue(task.Id, out var id))
            {
                if (releasing) throw Refuse($"Verified entry '{task.Id}' disappeared; human-guided replanning is required.");
                var intent = new CreationIntent(IntentSchema, session.BeadId, approval.PlanRevision, task.Id, "", false);
                var intentProduct = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
                var retainedIntent = await ReadIntentAsync(session, intentProduct, intent, token);
                if (retainedIntent is not null || Matches(inventory, intent).Any())
                    throw IntentFailure(intent, inventory, "The intended task is missing or its binding conflicts");
                await WriteIntentAsync(session, approval, authority, intent, token);
                inventory = await reader.ReadSnapshotAsync(session.Repository, token);
                if (Matches(inventory, intent).Any() || inventory.ById.ContainsKey(TaskId(session.BeadId, approval.PlanRevision, task.Id)))
                    throw IntentFailure(intent, inventory, "A task appeared after creation intent confirmation");
                // A confirmed intent does not carry authority across reads or a pause.
                intentProduct = await CheckAuthorityAsync(session, approval, authority, token);
                if (await ReadIntentAsync(session, intentProduct, intent, token) != intent)
                    throw IntentFailure(intent, inventory, "The creation intent changed before launch");
                await WriteUncertainAsync(session, ["create", "--type", "task", "--title", task.Title,
                    "--description", Description(task, plan), "--acceptance", string.Join("\n", task.AcceptanceCriteria),
                    "--labels", "plan-unpublished", "--deps", "discovered-from:" + session.BeadId,
                    "--metadata", JsonSerializer.Serialize(new { publication_product = session.BeadId,
                        publication_revision = approval.PlanRevision, publication_entry = task.Id }), "--json"], token);
                inventory = await reader.ReadSnapshotAsync(session.Repository, token);
                ids = ResolveIds(session.BeadId, approval.PlanRevision, plan, inventory, comments, out _);
                id = ids[task.Id];
            }
            // A full fresh inventory reconciles a command whose response or ID was lost.
            var binding = new CreationIntent(IntentSchema, session.BeadId, approval.PlanRevision, task.Id, id, true);
            var matches = Matches(inventory, binding).ToArray();
            if (matches.Length != 1 || matches[0].Id != id)
                throw IntentFailure(binding, inventory, "The task binding is not unique and exact");
            var bead = matches[0];
            try { CheckTask(bead, task, plan, session.BeadId, approval.PlanRevision, releasing); }
            catch (AgentStageInputException exception) { throw IntentFailure(binding, inventory, exception.Message); }
            if (!bead.Payload.TryGetProperty("dependencies", out var dependencies)
                || !BeadsReader.ParseDownDependencies(dependencies, bead.Id)
                .Contains(new BeadDependency(id, session.BeadId, "discovered-from")))
                throw IntentFailure(binding, inventory, "The native Product link is missing");
            if (BeadsReader.ParseDownDependencies(dependencies, bead.Id).Except(
                new[] { new BeadDependency(id, session.BeadId, "discovered-from") }.Concat(task.DependsOn
                    .Where(ids.ContainsKey).Select(d => new BeadDependency(id, ids[d], "blocks")))).Any())
                throw IntentFailure(binding, inventory, "The task has conflicting dependencies");
            var intentParent = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
            var existingIntent = await ReadIntentAsync(session, intentParent, binding, token);
            if (existingIntent != binding)
                await WriteIntentAsync(session, approval, authority, binding, token);
        }

        await CheckChildrenAsync(session, ids.Values, token);
        var graph = tasks.Select(t => new PublishedPlanningTask(ids[t.Id], t.Title, Description(t, plan),
            string.Join("\n", t.AcceptanceCriteria), t.DependsOn.Select(d => ids[d]).ToArray())).ToArray();
        var graphJson = JsonSerializer.Serialize(graph, PlanningEvidence.JsonOptions);
        var receipt = JsonSerializer.Serialize(new { schema = ReleaseSchema, product = session.BeadId,
            revision = approval.PlanRevision, tasks = ids }, PlanningEvidence.JsonOptions);

        foreach (var task in tasks)
        {
            var expected = Edges(session.BeadId, task, ids);
            var actual = await reader.ReadDownDependenciesAsync(session.Repository, ids[task.Id], token);
            if (actual.Except(expected).Any()) throw Refuse($"Task '{ids[task.Id]}' has conflicting dependencies.");
            foreach (var missing in expected.Except(actual))
            {
                if (releasing) throw Refuse("Released task dependencies changed; preserve executing work for human-guided replanning.");
                // dep add has no native status/assignee compare-and-swap guards.
                // Read the target last, immediately before each mutation, rather
                // than relying on its earlier creation-loop snapshot.
                await CheckAuthorityAsync(session, approval, authority, token);
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
        // Check even on resume when membership is already present: the next
        // operation may be the release receipt rather than a metadata write.
        var retained = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        if (Metadata(retained, "publication_graph") is { } existingGraph && existingGraph != graphJson
            || Metadata(retained, "publication_revision") is { } existingRevision && existingRevision != approval.PlanRevision)
            throw Refuse("Retained publication identity conflicts with the approved graph; preserve it for human reconciliation.");
        await CheckAuthorityAsync(session, approval, authority, token);
        if (Metadata(retained, "publication_graph") != graphJson || Metadata(retained, "publication_revision") != approval.PlanRevision)
        {
            var write = await WritePublicationMetadataAsync(session, approval.PlanRevision, graphJson, ["update", session.BeadId,
                "--status", "in_progress", "--assignee", session.InitialAssignee!,
                "--if-status", "in_progress", "--if-assignee", session.InitialAssignee!, "--json"], token);
            retained = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
            ConfirmMutationAuthority(session, authority, write, retained);
            if (Metadata(retained, "publication_graph") != graphJson || Metadata(retained, "publication_revision") != approval.PlanRevision)
                throw Refuse("Independent publication membership was not confirmed; all remaining holds are preserved.");
        }
        if (!releasing)
        {
            await CheckAuthorityAsync(session, approval, authority, token);
            await WriteUncertainAsync(session, ["comments", "add", session.BeadId, receipt], token);
            if (!(await reader.ReadCommentsAsync(session.Repository, session.BeadId, token)).Any(c => c.Text == receipt))
                throw Refuse("Complete graph release evidence was not confirmed.");
            // A comment response does not attest a new Product version. Never adopt
            // an unrelated claim version from the subsequent confirmation read.
            CheckOwnership(session, await reader.ReadDetailAsync(session.Repository, session.BeadId, token), authority);
        }
        foreach (var task in tasks)
        {
            await CheckAuthorityAsync(session, approval, authority, token);
            var bead = await reader.ReadDetailAsync(session.Repository, ids[task.Id], token);
            CheckTask(bead, task, plan, session.BeadId, approval.PlanRevision, allowReleased: true);
            if (!bead.Labels.Contains("plan-unpublished", StringComparer.Ordinal)) continue;
            await WriteUncertainAsync(session, ["update", bead.Id, "--remove-label", "plan-unpublished",
                "--status", "open", "--assignee", "", "--if-status", "open", "--if-assignee", "", "--json"], token);
            if ((await reader.ReadDetailAsync(session.Repository, bead.Id, token)).Labels.Contains("plan-unpublished", StringComparer.Ordinal))
                throw Refuse($"Publication hold for '{bead.Id}' was not released.");
        }
        var releasedGraph = await VerifyGraphAsync(session, approval, plan, ids, allowReleased: true, token);
        foreach (var id in ids.Values)
            if (releasedGraph.ById[id].Labels.Contains("plan-unpublished", StringComparer.Ordinal))
                throw Refuse($"Task '{id}' still has a publication hold.");
        await CheckAuthorityAsync(session, approval, authority, token);
        // All task holds are gone, but generated-task admission still requires this
        // exact parent revision and planned state. No multi-row transaction is assumed.
        await WritePublicationMetadataAsync(session, approval.PlanRevision, graphJson, ["update", session.BeadId, "--add-label", "planned",
            "--remove-label", "stage:plan-approved",
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

    private async Task<AgentStageResult> RevalidateAsync(AgentStageSession session, Authority authority, bool releaseClaim, CancellationToken token)
    {
        // Guidance after explicit Resume changes grounding, even when it leaves the
        // candidate intact. Review that same candidate again; never replay Planner
        // creation or silently treat guidance as approval of changed requirements.
        var before = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        CheckOwnership(session, before, authority);
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

    private async Task<BeadsInventory> VerifyGraphAsync(AgentStageSession session, PlanningApproval approval, PlanningDocument plan,
        Dictionary<string, string> ids, bool allowReleased, CancellationToken token)
    {
        // This phase only reads. Each subsequent mutation obtains fresh authority;
        // an extra grounding sweep here adds native processes without protecting
        // a write. Never carry authority from this phase across a mutation.
        // Native list includes complete dependency edges and task bodies. One fresh
        // inventory verifies the entire graph without repeated process/database
        // startup per node. It is never reused across a mutation.
        var inventory = await reader.ReadSnapshotAsync(session.Repository, token);
        var resolved = ResolveIds(session.BeadId, approval.PlanRevision, plan, inventory, [], out _);
        if (ids.Count != resolved.Count || ids.Any(pair => resolved.GetValueOrDefault(pair.Key) != pair.Value))
            throw Refuse("The retained task bindings changed during graph verification.");
        var intended = ids.Values.ToHashSet(StringComparer.Ordinal);
        var edges = inventory.ById.Values.SelectMany(bead => bead.Payload.TryGetProperty("dependencies", out var dependencies)
            ? BeadsReader.ParseDownDependencies(dependencies, bead.Id) : Array.Empty<BeadDependency>()).ToArray();
        if (edges.Any(e => e.Type == "discovered-from" && e.DependsOnId == session.BeadId && !intended.Contains(e.IssueId)))
            throw Refuse("Unmapped preexisting Product tasks require human-guided reconciliation; no work was removed.");
        foreach (var task in plan.Tasks!)
        {
            if (!inventory.ById.TryGetValue(ids[task.Id], out var bead)) throw Refuse("An intended task disappeared.");
            CheckTask(bead, task, plan,
                session.BeadId, approval.PlanRevision, allowReleased);
            var actual = edges.Where(e => e.IssueId == ids[task.Id]);
            var expected = Edges(session.BeadId, task, ids);
            if (!actual.ToHashSet().SetEquals(expected)) throw Refuse("The complete dependency graph was not verified.");
        }
        return inventory;
    }

    private async Task CheckChildrenAsync(AgentStageSession session, IEnumerable<string> ids, CancellationToken token)
    {
        var children = await reader.ReadUpDependenciesAsync(session.Repository, session.BeadId, token);
        var intended = ids.ToHashSet(StringComparer.Ordinal);
        if (children.Any(e => e.Type == "discovered-from" && !intended.Contains(e.IssueId)))
            throw Refuse("Unmapped preexisting Product tasks require human-guided reconciliation; no work was removed.");
    }

    private async Task<BeadSnapshot> CheckAuthorityAsync(AgentStageSession session, PlanningApproval approval, Authority authority, CancellationToken token)
    {
        var observation = await evidence.ObserveAsync(session, token);
        var parent = observation.Product;
        CheckOwnership(session, parent, authority);
        if (!PlanningEvidence.ApprovalStillCurrent(approval, observation.Grounding, observation.Comments))
            throw Refuse("Approved content or grounding changed; preserve tasks for Challenger revalidation and human-guided replanning.");
        return parent;
    }

    internal static string? Version(BeadSnapshot bead) => bead.Payload.TryGetProperty("updated_at", out var value) ? value.ToString() : null;

    private static void CheckOwnership(AgentStageSession session, BeadSnapshot parent, Authority authority)
    {
        if (BeadWorkflowState.Parse(parent) != session.InitialState || parent.Status != "in_progress"
            || parent.Assignee != session.InitialAssignee || string.IsNullOrWhiteSpace(parent.Assignee)
            || authority.Version is not null && Version(parent) != authority.Version)
            throw Refuse("Product workflow, pause, ownership or observation version changed during publication.");
    }

    private static string IntentKey(CreationIntent intent) => "publication_create_" + PlanningEvidence.Hash(intent.Revision + "\n" + intent.Entry);

    internal static CreationIntent[] ReadIntents(BeadSnapshot parent)
    {
        if (!parent.Payload.TryGetProperty("metadata", out var metadata)) return [];
        if (metadata.ValueKind != JsonValueKind.Object) throw Refuse("Product metadata is malformed.");
        return metadata.EnumerateObject().Where(p => p.Name.StartsWith("publication_create_", StringComparison.Ordinal))
            .Select(p =>
            {
                CreationIntent? intent = null;
                try
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                        intent = JsonSerializer.Deserialize<CreationIntent>(p.Value.GetString()!, IntentJson);
                }
                catch (JsonException) { }
                if (intent is null || intent.Schema != IntentSchema || intent.Product != parent.Id
                    || string.IsNullOrWhiteSpace(intent.Revision) || string.IsNullOrWhiteSpace(intent.Entry)
                    || intent.Task is null || intent.Resolved && string.IsNullOrWhiteSpace(intent.Task)
                    || p.Name != IntentKey(intent)) throw Refuse($"Creation evidence '{p.Name}' is malformed or conflicting.");
                return intent;
            }).ToArray();
    }

    // Merge independent identity evidence before adopting any row. Absence after
    // a durable intent is uncertainty, never permission for another create.
    private static Dictionary<string, string> ResolveIds(string product, string revision, PlanningDocument plan,
        BeadsInventory inventory, IReadOnlyList<BeadCommentSnapshot> comments, out bool releasing)
    {
        var parent = inventory.ById[product];
        var intents = ReadIntents(parent);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = plan.Tasks!.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var fallback = new CreationIntent(IntentSchema, product, revision, "unknown", "", false);
        foreach (var bead in inventory.ById.Values.Where(b => Metadata(b, "publication_product") == product))
            if (Metadata(bead, "publication_revision") != revision || Metadata(bead, "publication_entry") is not { } entry
                || !entries.Contains(entry))
                throw IntentFailure(fallback with { Task = bead.Id }, inventory, "Task provenance conflicts with the approved graph");
        void Bind(string entry, string id)
        {
            if (IsLegacyTaskId(id, product) && id != TaskId(product, revision, entry))
                throw IntentFailure(fallback with { Entry = entry, Task = id }, inventory, "Legacy task identity conflicts with the approved entry");
            if (!entries.Contains(entry) || string.IsNullOrWhiteSpace(id)
                || ids.TryGetValue(entry, out var previous) && previous != id
                || ids.Any(p => p.Key != entry && p.Value == id))
                throw IntentFailure(fallback with { Entry = entry, Task = id }, inventory, "Retained task bindings conflict");
            ids[entry] = id;
        }
        foreach (var intent in intents)
        {
            if (intent.Revision != revision || !entries.Contains(intent.Entry))
                throw IntentFailure(intent, inventory, "Creation evidence conflicts with the approved graph");
            if (intent.Task.Length > 0) Bind(intent.Entry, intent.Task);
        }
        releasing = false;
        foreach (var comment in comments)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(comment.Text); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.String || schema.GetString() != ReleaseSchema) continue;
                if (!root.TryGetProperty("product", out var owner) || owner.ValueKind != JsonValueKind.String || owner.GetString() != product
                    || !root.TryGetProperty("revision", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != revision
                    || !root.TryGetProperty("tasks", out var map) || map.ValueKind != JsonValueKind.Object
                    || map.EnumerateObject().Count() != entries.Count
                    || map.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                    throw IntentFailure(fallback, inventory, "Release evidence conflicts with the approved graph");
                foreach (var pair in map.EnumerateObject())
                {
                    if (pair.Value.ValueKind != JsonValueKind.String)
                        throw IntentFailure(fallback, inventory, "Release task identity is malformed");
                    Bind(pair.Name, pair.Value.GetString()!);
                }
                releasing = true;
            }
        }
        foreach (var task in plan.Tasks!)
        {
            var intent = fallback with { Entry = task.Id, Task = ids.GetValueOrDefault(task.Id) ?? "" };
            var legacy = TaskId(product, revision, task.Id);
            var matches = Matches(inventory, intent).ToArray();
            if (inventory.ById.ContainsKey(legacy)) Bind(task.Id, legacy);
            if (matches.Length > 1)
                throw IntentFailure(intent, inventory, "The task binding is not unique and exact");
            if (matches.Length == 1) Bind(task.Id, matches[0].Id);
            if (ids.TryGetValue(task.Id, out var id))
            {
                var binding = intent with { Task = id };
                if (!inventory.ById.TryGetValue(id, out var bead)
                    || Metadata(bead, "publication_product") != product || Metadata(bead, "publication_revision") != revision
                    || Metadata(bead, "publication_entry") != task.Id)
                    throw IntentFailure(binding, inventory, "The intended task is missing or its provenance conflicts");
            }
            else if (intents.Any(i => i.Entry == task.Id) || releasing)
                throw IntentFailure(intent, inventory, "The intended task is missing or its binding conflicts");
        }
        var retainedRevision = Metadata(parent, "publication_revision");
        if (parent.Payload.TryGetProperty("metadata", out var parentMetadata)
            && parentMetadata.TryGetProperty("publication_revision", out _) && retainedRevision != revision)
            throw IntentFailure(fallback, inventory, "Retained publication revision conflicts");
        if (parent.Payload.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("publication_graph", out _))
        {
            try
            {
                var graph = JsonSerializer.Deserialize<PublishedPlanningTask[]>(Metadata(parent, "publication_graph") ?? "null", PlanningEvidence.JsonOptions);
                var expected = plan.Tasks.Select(t => new PublishedPlanningTask(ids[t.Id], t.Title, Description(t, plan),
                    string.Join("\n", t.AcceptanceCriteria), t.DependsOn.Select(d => ids[d]).ToArray())).ToArray();
                if (graph is null || JsonSerializer.Serialize(graph, PlanningEvidence.JsonOptions)
                    != JsonSerializer.Serialize(expected, PlanningEvidence.JsonOptions))
                    throw IntentFailure(fallback, inventory, "Retained graph membership conflicts");
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw IntentFailure(fallback, inventory, "Retained graph membership is malformed or incomplete"); }
        }
        return ids;
    }

    private async Task ValidateRetainedIntentsAsync(AgentStageSession session, BeadSnapshot parent,
        PlanningApproval approval, PlanningTask[] tasks, CancellationToken token)
    {
        if (!parent.Payload.TryGetProperty("metadata", out var metadata)) return;
        var fallback = new CreationIntent(IntentSchema, session.BeadId, approval.PlanRevision, "unknown", "", false);
        if (metadata.ValueKind != JsonValueKind.Object)
            throw IntentFailure(fallback, await reader.ReadSnapshotAsync(session.Repository, token), "Product metadata is malformed");
        foreach (var property in metadata.EnumerateObject().Where(p => p.Name.StartsWith("publication_create_", StringComparison.Ordinal)))
        {
            CreationIntent? intent = null;
            try
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    intent = JsonSerializer.Deserialize<CreationIntent>(property.Value.GetString()!, IntentJson);
            }
            catch (JsonException) { }
            if (intent is not null && intent.Schema == IntentSchema && intent.Product == session.BeadId
                && intent.Revision == approval.PlanRevision && tasks.Any(t => t.Id == intent.Entry)
                && intent.Task is not null && (!intent.Resolved || !string.IsNullOrWhiteSpace(intent.Task))
                && property.Name == IntentKey(intent)) continue;
            var diagnostic = fallback with { Entry = intent?.Entry ?? property.Name, Task = intent?.Task ?? "" };
            throw IntentFailure(diagnostic, await reader.ReadSnapshotAsync(session.Repository, token),
                $"Retained creation evidence '{property.Name}' is malformed or conflicts with the approved graph");
        }
    }

    private static CreationIntent? ReadIntent(BeadSnapshot parent, CreationIntent expected)
    {
        if (!parent.Payload.TryGetProperty("metadata", out var metadata)) return null;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw Refuse("Product metadata is malformed");
        if (!metadata.TryGetProperty(IntentKey(expected), out var raw)) return null;
        try
        {
            var actual = raw.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<CreationIntent>(raw.GetString()!, IntentJson) : null;
            if (actual is not null && (actual with { Resolved = expected.Resolved, Task = expected.Task }) == expected
                && (actual.Task == expected.Task || !actual.Resolved && actual.Task == "")) return actual;
        }
        catch (JsonException) { }
        throw Refuse("Creation evidence is malformed or conflicting");
    }

    private async Task<CreationIntent?> ReadIntentAsync(AgentStageSession session, BeadSnapshot parent,
        CreationIntent expected, CancellationToken token)
    {
        try { return ReadIntent(parent, expected); }
        catch (AgentStageInputException exception)
        {
            throw IntentFailure(expected, await reader.ReadSnapshotAsync(session.Repository, token), exception.Message);
        }
    }

    private static IEnumerable<BeadSnapshot> Matches(BeadsInventory inventory, CreationIntent intent) =>
        inventory.ById.Values.Where(b => b.Id == intent.Task || Metadata(b, "publication_product") == intent.Product
            && Metadata(b, "publication_revision") == intent.Revision
            && Metadata(b, "publication_entry") == intent.Entry);

    private static AgentStageInputException IntentFailure(CreationIntent intent, BeadsInventory inventory, string check) =>
        Refuse($"Product '{intent.Product}', revision '{intent.Revision}', entry '{intent.Entry}', observed IDs [{string.Join(", ", Matches(inventory, intent).Select(b => b.Id))}]: {check}. Maintainer: inspect the evidence and reconcile the task binding before guarded Retry.");

    private async Task WriteIntentAsync(AgentStageSession session, PlanningApproval approval, Authority authority,
        CreationIntent intent, CancellationToken token)
    {
        await CheckAuthorityAsync(session, approval, authority, token);
        using var file = InvocationRuntimeFiles.CreatePlanningPublicationMetadataFile(session.HostPaths, session.Project.Name, session.BeadId);
        await JsonSerializer.SerializeAsync(file.Stream, new Dictionary<string, string>
        { [IntentKey(intent)] = JsonSerializer.Serialize(intent, PlanningEvidence.JsonOptions) }, PlanningEvidence.JsonOptions, token);
        await file.Stream.FlushAsync(token);
        var write = await WriteUncertainAsync(session, ["update", session.BeadId, "--status", "in_progress", "--assignee", session.InitialAssignee!,
            "--if-status", "in_progress", "--if-assignee", session.InitialAssignee!, "--metadata", "@" + file.Stream.Name, "--json"], token);
        var parent = await reader.ReadDetailAsync(session.Repository, session.BeadId, token);
        if (await ReadIntentAsync(session, parent, intent, token) != intent)
            throw IntentFailure(intent, await reader.ReadSnapshotAsync(session.Repository, token), "The creation intent was not confirmed");
        ConfirmMutationAuthority(session, authority, write, parent);
    }

    private static void ConfirmMutationAuthority(AgentStageSession session, Authority authority,
        ProcessResult? write, BeadSnapshot parent)
    {
        // Only the exact native mutation response can attribute an advanced version
        // to this invocation. Readback alone proves content, not continuing ownership.
        var expected = authority.Version;
        if (write is { ExitCode: 0 })
        {
            try
            {
                var published = BeadsReader.ParsePublicationResult(write.StandardOutput, session.BeadId);
                CheckOwnership(session, published, new Authority(null));
                expected = Version(published) ?? expected;
            }
            catch (Exception exception) when (exception is JsonException or FormatException) { }
        }
        CheckOwnership(session, parent, new Authority(expected));
        authority.Version = expected;
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

    private async Task<ProcessResult?> WritePublicationMetadataAsync(AgentStageSession session, string revision, string graph,
        string[] args, CancellationToken token)
    {
        using var file = InvocationRuntimeFiles.CreatePlanningPublicationMetadataFile(
            session.HostPaths, session.Project.Name, session.BeadId);
        // bd merges this patch with existing metadata. Do not snapshot/rewrite
        // unrelated operator fields or place the graph on the Windows command line.
        await JsonSerializer.SerializeAsync(file.Stream, new { publication_revision = revision, publication_graph = graph },
            PlanningEvidence.JsonOptions, token);
        await file.Stream.FlushAsync(token);
        return await WriteUncertainAsync(session, [.. args, "--metadata", "@" + file.Stream.Name], token);
    }

    // A failed or lost response is uncertainty, never permission to repeat a create.
    // The caller immediately checks the exact intended effect through fresh reads.
    private async Task<ProcessResult?> WriteUncertainAsync(AgentStageSession session, string[] args, CancellationToken token)
    {
        try { return await runner.RunAsync(BeadsCommand.CreateWrite(session.Repository.Path, args, session.InitialAssignee), token); }
        catch (Exception exception) when (exception is ToolExecutionException or IOException) { return null; }
    }

    private static AgentStageInputException Refuse(string reason) => new("Planning publication stopped: " + reason);
}
