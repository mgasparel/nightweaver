using System.Text.Json;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal sealed record PlanningHandoffInput(string Id, string Locator, string Sha256, long SizeBytes,
    string SourcePath, PlanningInputSource Source, string[] TaskEntries);
internal sealed record PlanningHandoff(string Schema, string Product, string PlanCommentId, string PlanRevision,
    PlanningInputStore Store, PlanningHandoffInput[] Inputs);
internal sealed record ResolvedPlanningInput(string Id, string Path, string Sha256, long SizeBytes);

// Comments retain metadata only. The bound store remains the authority for bytes.
internal static class PlanningInputHandoff
{
    internal const string Schema = "nightweaver-plan-input-handoff-v1";
    internal const string DescriptionPrefix = "\n\n## Local inputs\n```json\n";

    internal static IPlanningInputStore Open(AgentStageSession session) => new PlanningInputFiles(session.HostPaths,
        session.Project.Name, Binding(session).RepositoryIdentity, session.Repository.Path);

    internal static PlanningInputStore Binding(AgentStageSession session) => new(session.Project.Name.Value,
        PlanningEvidence.Hash(ProcessLifetimeTargetRepositoryLock.ResolveIdentity(session.Repository.GitCommonDirectory)));

    internal static PlanningHandoff Create(AgentStageSession session, PlanningApproval approval, PlanningDocument plan,
        IReadOnlyList<PlanningInputManifest> manifests)
    {
        var inputs = plan.LocalInputs!;
        if (manifests.Count != inputs.Length) throw Failure(session.BeadId, approval.PlanRevision, inputs[0], "Manifest count differs.");
        return new(Schema, session.BeadId, approval.PlanCommentId, approval.PlanRevision, Binding(session),
            inputs.Select((input, index) =>
            {
                CheckManifest(session.BeadId, approval.PlanRevision, Binding(session), input, manifests[index]);
                return new PlanningHandoffInput(input.Id, input.Locator, input.ExpectedSha256, manifests[index].SizeBytes,
                    input.SourcePath, input.Source, plan.Tasks!.Where(t => t.LocalInputIds?.Contains(input.Id, StringComparer.Ordinal) == true)
                        .Select(t => t.Id).ToArray());
            }).ToArray());
    }

    internal static string Encode(PlanningHandoff handoff) => JsonSerializer.Serialize(handoff, PlanningEvidence.JsonOptions);

    internal static PlanningHandoff? Read(IReadOnlyList<BeadCommentSnapshot> comments, string product, string revision)
    {
        PlanningHandoff? retained = null;
        foreach (var comment in comments)
        {
            // Other protocol evidence and old revisions remain untouched.
            if (!comment.Text.TrimStart().StartsWith('{') || !comment.Text.Contains(Schema, StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(comment.Text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("schema", out var schema) || schema.GetString() != Schema) continue;
                if (root.GetProperty("planRevision").GetString() != revision) continue;
                var value = root.Deserialize<PlanningHandoff>(PlanningEvidence.JsonOptions);
                if (value is null || value.Product != product || string.IsNullOrWhiteSpace(value.PlanCommentId)
                    || value.Store is null || value.Inputs is not { Length: > 0 and <= 64 }
                    || value.Inputs.Any(i => i is null || i.TaskEntries is not { Length: > 0 and <= 64 })
                    || value.Inputs.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != value.Inputs.Length
                    || Encode(value) != comment.Text || retained is not null && Encode(retained) != comment.Text)
                    throw new InvalidDataException("Handoff binding or content conflicts.");
                retained = value;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
            {
                throw new AgentStageInputException($"Local input check failed. Product '{product}', candidate '{revision}', input 'handoff', locator 'handoff': Invalid or conflicting handoff. The maintainer must inspect and reconcile the evidence before an authorized Retry.");
            }
        }
        return retained;
    }

    internal static void Verify(PlanningHandoff handoff, PlanningLocalInput[] inputs, IPlanningInputStore store,
        CancellationToken token)
    {
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var manifest = store.Verify(input);
                CheckManifest(handoff.Product, handoff.PlanRevision, handoff.Store, input, manifest);
                var record = handoff.Inputs.SingleOrDefault(i => i.Id == input.Id);
                if (record is null || record.Locator != manifest.Locator || record.Sha256 != manifest.Sha256
                    || record.SizeBytes != manifest.SizeBytes || record.SourcePath != manifest.SourcePath || record.Source != manifest.Source)
                    throw new InvalidDataException("Handoff and stored manifest differ.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException
                or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            { throw Failure(handoff.Product, handoff.PlanRevision, input, "Stored input verification failed: " + Diagnostic(error)); }
        }
    }

    internal static async Task<ResolvedPlanningInput[]> ResolveAsync(AgentStageSession session, BeadsReader reader,
        Func<AgentStageSession, IPlanningInputStore> open, CancellationToken token)
    {
        var inventory = await reader.ReadSnapshotAsync(session.Repository, token);
        if (!inventory.ById.TryGetValue(session.BeadId, out var task) || !PlanningPublicationGate.Allows(task, inventory))
            throw new AgentStageInputException("Delivery input check requires the unchanged published graph.");
        var product = PlanningPublisher.Metadata(task, "publication_product");
        if (product is null) return [];
        var graph = PlanningPublisher.Metadata(inventory.ById[product], "publication_graph")!;
        var published = JsonSerializer.Deserialize<PublishedPlanningTask[]>(graph, PlanningEvidence.JsonOptions)!.Single(t => t.Id == task.Id);
        // Structured graph fields declare dependencies. Never infer them from prose.
        if (published.LocalInputs is not { Length: > 0 } inputs) return [];
        var description = published.Description;
        var revision = PlanningPublisher.Metadata(task, "publication_revision")!;
        if (inputs.Length > 64 || inputs.Any(i => i is null)
            || inputs.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            throw new AgentStageInputException($"Delivery input check failed for Product '{product}', candidate '{revision}': Invalid task input declarations. The maintainer must reconcile the published evidence before an authorized Retry.");
        var comments = await reader.ReadCommentsAsync(session.Repository, product, token);
        var handoff = Read(comments, product, revision)
            ?? throw Failure(product, revision, inputs[0], "The handoff is missing.");
        if (handoff.Store != Binding(session)) throw Failure(product, revision, inputs[0], "The handoff store is foreign.");
        var entry = PlanningPublisher.Metadata(task, "publication_entry");
        var candidate = comments.SingleOrDefault(c => c.Id == handoff.PlanCommentId && PlanningEvidence.Hash(c.Text) == revision);
        var plan = candidate is null ? null : PlanningEvidence.Parse(candidate.Text);
        if (plan is null || PlanningEvidence.ValidateCandidate(plan) is not null
            || !MatchesPlan(handoff, plan)
            || plan.Tasks!.SingleOrDefault(t => t.Id == entry) is not { } approvedTask
            || PlanningPublisher.Description(approvedTask, plan) != description
            || JsonSerializer.Serialize(PlanningPublisher.TaskInputs(approvedTask, plan), PlanningEvidence.JsonOptions)
                != JsonSerializer.Serialize(inputs, PlanningEvidence.JsonOptions))
            throw Failure(product, revision, inputs[0], "The exact approved candidate or task use differs.");
        var required = handoff.Inputs.Where(i => i.TaskEntries.Contains(entry, StringComparer.Ordinal)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        if (!required.SetEquals(inputs.Select(i => i.Id))) throw Failure(product, revision, inputs[0], "Task use differs from the handoff.");
        var store = open(session);
        Verify(handoff, inputs, store, token);
        return inputs.Select(i => new ResolvedPlanningInput(i.Id, store.Location(i), i.ExpectedSha256,
            handoff.Inputs.Single(r => r.Id == i.Id).SizeBytes)).ToArray();
    }

    private static bool MatchesPlan(PlanningHandoff handoff, PlanningDocument plan) =>
        plan.LocalInputs is { Length: > 0 } declarations && declarations.Length == handoff.Inputs.Length
        && declarations.All(input => input.Store == handoff.Store
            && handoff.Inputs.SingleOrDefault(i => i.Id == input.Id) is { } record
            && record.Locator == input.Locator && record.Sha256 == input.ExpectedSha256
            && record.SourcePath == input.SourcePath && record.Source == input.Source
            && record.SizeBytes is >= 0 and <= PlanningInputFiles.MaximumInputBytes
            && record.TaskEntries.SequenceEqual(plan.Tasks!.Where(t => t.LocalInputIds?.Contains(input.Id, StringComparer.Ordinal) == true)
                .Select(t => t.Id), StringComparer.Ordinal));

    private static void CheckManifest(string product, string revision, PlanningInputStore binding,
        PlanningLocalInput input, PlanningInputManifest manifest)
    {
        if (input.Store != binding || manifest.Schema != "nightweaver-local-input-v1" || manifest.Store != binding
            || manifest.Locator != input.Locator || manifest.Sha256 != input.ExpectedSha256
            || manifest.SourcePath != input.SourcePath || manifest.Source != input.Source
            || manifest.SizeBytes is < 0 or > PlanningInputFiles.MaximumInputBytes)
            throw Failure(product, revision, input, "The manifest binding differs.");
    }

    internal static AgentStageInputException Failure(string product, string revision, PlanningLocalInput input, string check) =>
        new($"Local input check failed. Product '{product}', candidate '{revision}', input '{input.Id}', locator '{input.Locator}': {check} The maintainer must inspect and reconcile the evidence before an authorized Retry.");

    internal static string Diagnostic(Exception error) => error.GetType().Name + ": "
        + new string(error.Message.Take(240).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
