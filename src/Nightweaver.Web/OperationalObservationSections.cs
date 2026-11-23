using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed partial class OperationalObservationSource
{
    private readonly Dictionary<string, SectionIdentity> sectionCycles = new(StringComparer.Ordinal);
    private sealed record SectionIdentity(string Id, DateTimeOffset UpdatedAt, string Workflow)
    {
        internal static SectionIdentity Capture(BeadSnapshot bead) => new(bead.Id,
            bead.Payload.GetProperty("updated_at").GetDateTimeOffset(),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                AttentionMutationIdentity.Capture(bead), ObservationJson.Options))));
    }
    private readonly Dictionary<string, DateTimeOffset> recentDetails = new(StringComparer.Ordinal);
    private bool preferSections = true;
    private int sectionCursor;

    internal static string[] SectionSources(OperationKey key) => key.Query switch
    {
        "text" => ["detail:" + key.Argument],
        "history" or "merge" => ["comments:" + key.Argument],
        "relations" => ["relations-up:" + key.Argument, "relations-down:" + key.Argument],
        "environment" => ["environment:" + key.Argument],
        _ => throw new ObservationRequestException(400)
    };

    private async Task<IReadOnlyList<BeadsCommentSnapshot>?> ReadSharedHistoryAsync(
        ValidatedTargetRepository repository, BeadsReader reader, string id, CancellationToken token)
    {
        var name = "comments:" + id;
        if (scheduled && !schedule.Due(name))
        {
            if (states.GetValueOrDefault(name)?.Error is null && histories.TryGetValue(id, out var history) && !history.Strict)
                schedule.InvalidateNames([name]);
            else return states.GetValueOrDefault(name)?.Error is null ? histories.GetValueOrDefault(id)?.Comments : null;
        }
        if (!HasBudget || commentsThisSweep >= 4) return null;
        commentsThisSweep++;
        var comments = await ReadAsync(name, () => reader.ReadDetailCommentsAsync(repository, id, token), token);
        if (comments is not null) StoreHistory(id, comments);
        return comments;
    }

    private static bool SameSectionIdentity(BeadSnapshot left, BeadSnapshot right)
        => SameSectionIdentity(SectionIdentity.Capture(left), right);
    private static bool SameSectionIdentity(SectionIdentity left, BeadSnapshot right) => left == SectionIdentity.Capture(right);

    private async Task ReadSectionsAsync(IReadOnlyList<OperationKey> keys, ValidatedTargetRepository repository,
        BeadsReader reader, BeadsInventory? inventory, Dictionary<string, ObservationSample> result, CancellationToken token)
    {
        var sections = keys.Where(key => key.Kind == "section").ToArray();
        foreach (var key in sectionCycles.Keys.Where(key => !sections.Any(item => item.Value == key)).ToArray()) sectionCycles.Remove(key);
        if (sections.Length == 0) return;
        var now = clock.GetUtcNow();
        if (inventory is not null && (baseInventory is null || !ReferenceEquals(baseContentIdentity, states["inventory"].ContentIdentity)))
        {
            foreach (var key in sections) result[key.Value] = Sample(null, now, "inventory") with
                { Error = new("source-limit", "Shared base data could not fit the observation cache.") };
            return;
        }
        var ids = sections.Select(key => key.Argument!).Distinct(StringComparer.Ordinal).ToArray();
        var selected = ids[sectionCursor++ % ids.Length];
        var completed = new List<OperationKey>();
        foreach (var key in sections)
        {
            var sources = InventorySource.Concat(SectionSources(key)).ToArray();
            result[key.Value] = Sample(null, now, sources) with { Pending = true };
            if (inventory is null) continue;
            if (!inventory.ById.TryGetValue(key.Argument!, out var initial))
            {
                sectionCycles.Remove(key.Value);
                if (scheduled && !schedule.IsCurrent("inventory", states["inventory"].SourceGeneration)) continue;
                result[key.Value] = Sample(null, now, "inventory") with
                { Error = new("resource-missing", "The Bead no longer exists.", false) };
                continue;
            }
            if (SectionSources(key).Any(name => sourceBeads.TryGetValue(name, out var prior) && !SameSectionIdentity(prior, initial)))
            {
                schedule.InvalidateNames(SectionSources(key));
                result[key.Value] = Sample(null, now, sources) with
                    { Error = new("source-conflict", "The Bead changed during observation. Retrying shortly.") };
            }
            if (key.Argument != selected && SectionSources(key).Any(name => !states.TryGetValue(name, out var state)
                || state.Error is not null || scheduled && schedule.Due(name))) continue;
            if (!sectionCycles.TryGetValue(key.Value, out var captured)) sectionCycles[key.Value] = captured = SectionIdentity.Capture(initial);
            if (!SameSectionIdentity(captured, initial))
            {
                schedule.InvalidateNames(SectionSources(key));
                sectionCycles[key.Value] = captured = SectionIdentity.Capture(initial);
            }
            try
            {
                foreach (var name in SectionSources(key))
                {
                    if (!HasBudget) break;
                    if (sourceBeads.TryGetValue(name, out var sourceBead) && !SameSectionIdentity(sourceBead, initial))
                        schedule.InvalidateNames([name]);
                    if (name.StartsWith("comments:", StringComparison.Ordinal))
                        await ReadSharedHistoryAsync(repository, reader, key.Argument!, token);
                    else if (name.StartsWith("detail:", StringComparison.Ordinal))
                        await ReadAsync(name, () => reader.ReadDetailAsync(repository, key.Argument!, token), token);
                    else if (name.StartsWith("relations-up:", StringComparison.Ordinal))
                        await ReadAsync(name, () => reader.ReadUpDependenciesAsync(repository, key.Argument!, token), token);
                    else if (name.StartsWith("relations-down:", StringComparison.Ordinal))
                        await ReadAsync(name, () => reader.ReadDownDependenciesAsync(repository, key.Argument!, token), token);
                    else await ReadAsync(name, () => ReadSectionEnvironmentAsync(repository, key.Argument!, token), token);
                }
                var sample = Sample(null, now, sources);
                if (key.Query == "relations" && SectionSources(key).All(states.ContainsKey)
                    && SectionSources(key).Any(name => states[name].Error is null)) completed.Add(key);
                else if (sample.Error is not null && sample.Error.Code != "budget-exceeded")
                    result[key.Value] = sample;
                else if (SectionSources(key).All(name => states.TryGetValue(name, out var state) && state.Error is null
                    && (!scheduled || schedule.IsCurrent(name, state.SourceGeneration)))
                    && (key.Query is not ("history" or "merge") || histories.GetValueOrDefault(key.Argument!)?.Strict == true)) completed.Add(key);
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            { result[key.Value] = Sample(null, now, sources) with { Error = Error(exception) }; }
        }
        // One complete shared confirmation serves all compatible sections. A
        // spent turn leaves them pending; the next core read can confirm them.
        if (completed.Count == 0) return;
        var requiresConfirmation = completed.SelectMany(SectionSources).Any(name =>
            sourceReadOrder.GetValueOrDefault(name) > sourceReadOrder.GetValueOrDefault("inventory"));
        if (requiresConfirmation)
        {
            if (!HasBudget) return;
            if (scheduled) schedule.InvalidateNames(["inventory"]);
            inventory = await ReadAsync("inventory", () => reader.ReadSnapshotAsync(repository, token), token, "confirmation");
            ProjectBases(keys, inventory, retainedReady, retainedBlocked, now, result, inventoryOnly: true);
        }
        if (inventory is null)
        {
            foreach (var key in completed) result[key.Value] = Sample(null, now, InventorySource.Concat(SectionSources(key)).ToArray());
            return;
        }
        if (inventory is not null && !ReferenceEquals(baseContentIdentity, states["inventory"].ContentIdentity))
        {
            foreach (var key in completed) result[key.Value] = Sample(null, now, "inventory") with
                { Error = new("source-limit", "Shared base data could not fit the observation cache.") };
            return;
        }
        foreach (var key in completed)
        {
            var names = InventorySource.Concat(SectionSources(key)).ToArray();
            var captured = sectionCycles[key.Value];
            if (inventory is null || !inventory.ById.TryGetValue(key.Argument!, out var last)
                || !SameSectionIdentity(captured, last)
                || key.Query == "text" && cached.GetValueOrDefault("detail:" + key.Argument) is BeadSnapshot detail
                    && !SameSectionIdentity(detail, last))
            {
                result[key.Value] = Sample(null, now, names) with
                { Error = new("source-conflict", "The Bead changed during observation. Retrying shortly.") };
                schedule.InvalidateNames(SectionSources(key));
                sectionCycles.Remove(key.Value);
                continue;
            }
            var sample = Sample(null, now, names);
            if (sample.Error is not null && key.Query != "relations") { result[key.Value] = sample; continue; }
            var row = BaseBeadRow.Project(last, inventory, project.Limits.MaxRepairCycles);
            (object? value, bool empty, bool unavailable) projection;
            try { projection = ProjectSection(key, last, inventory); }
            catch (Exception exception) when (IsSourceFailure(exception))
            { result[key.Value] = sample with { Error = Error(exception) }; continue; }
            var (value, empty, unavailable) = projection;
            var data = new
            {
                schema = "nightweaver-section-v1", beadId = key.Argument, name = key.Query,
                baseIdentity = new { inventoryRevision = baseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    updatedAt = row.UpdatedAt, expectedWorkflow = row.Actions.ExpectedWorkflow },
                state = sample.Error is not null ? "stale" : unavailable ? "unavailable" : empty ? "empty" : "available", value
            };
            result[key.Value] = CurrentSample(sample with { Data = data, Partial = sample.Error is not null });
            sectionCycles.Remove(key.Value);
            publishBase?.Invoke(new Dictionary<string, ObservationSample> { [key.Value] = result[key.Value] });
        }
    }

    private async Task<BeadWorkspaceEnvironment> ReadSectionEnvironmentAsync(ValidatedTargetRepository repository, string id, CancellationToken token)
    {
        var bounded = CreateRunner();
        var environment = new BeadWorkspaceEnvironmentObserver(paths, project, new GitWorktreeReader(bounded), bounded, runtimeObserver, clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Min(2000, 4000 - (nativeMilliseconds - sweepNativeStarted))), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var value = await environment.ObserveAsync(repository, id, linked.Token);
            if (value.State is "unavailable" or "unsafe") throw new ToolExecutionException("Environment could not be observed");
            return value;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ToolExecutionException("Environment observation timed out within its source budget"); }
    }

    private (object? Value, bool Empty, bool Unavailable) ProjectSection(OperationKey key, BeadSnapshot bead, BeadsInventory inventory)
    {
        var id = key.Argument!;
        switch (key.Query)
        {
            case "text":
                var text = BeadWorkspaceProjector.Project((BeadSnapshot)cached["detail:" + id], [], null,
                    BeadWorkspaceProjector.UnavailableEnvironment("unavailable"));
                return (new { text.Bead.Description, text.Bead.Design, text.Bead.AcceptanceCriteria, text.Bead.Notes, text.SourceMetadata },
                    string.IsNullOrEmpty(text.Bead.Description + text.Bead.Design + text.Bead.AcceptanceCriteria + text.Bead.Notes + text.SourceMetadata), false);
            case "history":
                var comments = histories[id].Comments;
                var attention = AttentionDeskProjector.IsPaused(bead) && !AttentionDeskProjector.IsTerminal(bead)
                    ? AttentionDeskProjector.ProjectWithSelection(bead, comments, project.Limits.MaxRepairCycles) : null;
                var history = BeadWorkspaceProjector.Project(bead, comments, attention, BeadWorkspaceProjector.UnavailableEnvironment("unavailable"));
                return (new { history.Comments, history.History, history.Attention }, comments.Count == 0, false);
            case "merge":
                var evidence = DigestMergeEvidence.Read(bead, histories[id].Comments);
                return (evidence.State == "verified" ? evidence : null, false, evidence.State != "verified");
            case "environment": return (cached["environment:" + id], false, false);
            default:
                var up = states["relations-up:" + id].Error is null ? (IReadOnlyList<BeadDependency>)cached["relations-up:" + id] : [];
                var down = states["relations-down:" + id].Error is null ? (IReadOnlyList<BeadDependency>)cached["relations-down:" + id] : [];
                if (up.Any(edge => edge.DependsOnId != id) || down.Any(edge => edge.IssueId != id))
                    throw new ToolExecutionException("Relation identity could not be validated");
                var relations = BeadWorkspaceProjector.ProjectRelations(up, down, inventory.ById);
                return (new { relations, relationObservation = new BeadWorkspaceRelationObservation(
                    states["relations-up:" + id].Error is null ? "complete" : "unavailable",
                    states["relations-down:" + id].Error is null ? "complete" : "unavailable") }, relations.Length == 0, false);
        }
    }
}
