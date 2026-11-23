using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed partial class OperationalObservationSource
{
    private void ProjectDigestBases(IReadOnlyList<OperationKey> keys, BeadsInventory? inventory, DateTimeOffset? caughtUp,
        DateTimeOffset generatedAt, DateTimeOffset started, Dictionary<string, ObservationSample> result)
    {
        foreach (var key in keys.Where(key => key.Kind == "base-digest"))
        {
            var digest = inventory is null ? null : CatchUpDigestProjector.Project(inventory, key.Argument!, caughtUp,
                generatedAt, project.Limits.MaxRepairCycles, baseOnly: true);
            var usable = digest is not null && baseInventory is not null
                && ReferenceEquals(baseContentIdentity, states["inventory"].ContentIdentity);
            var sample = Sample(usable ? baseInventory!.Digest(digest!) : null, started, DigestCoreSources);
            if (!usable && inventory is not null) sample = sample with
                { Error = new("source-limit", "Shared base data could not fit the observation cache.") };
            result[key.Value] = CurrentSample(sample);
            publishBase?.Invoke(new Dictionary<string, ObservationSample> { [key.Value] = result[key.Value] });
        }
    }

    private async Task ReadDigestSectionsAsync(IReadOnlyList<OperationKey> keys, ValidatedTargetRepository repository,
        BeadsReader reader, BeadsInventory? inventory, DateTimeOffset? caughtUp, DateTimeOffset generatedAt,
        Dictionary<string, ObservationSample> result, CancellationToken token)
    {
        foreach (var key in keys.Where(key => key.Kind == "digest-merges"))
        {
            if (inventory is null) { result[key.Value] = Sample(null, generatedAt, DigestCoreSources); continue; }
            CatchUpDigestResponse digest;
            try { digest = CatchUpDigestProjector.Project(inventory, key.Argument!, caughtUp, generatedAt, project.Limits.MaxRepairCycles); }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                result[key.Value] = Sample(null, generatedAt, DigestCoreSources) with { Error = Error(exception) };
                continue;
            }
            var candidates = digest.Threads.SelectMany(thread => thread.Items.Prepend(thread.Root)).Concat(digest.StandaloneCompleted)
                .Where(item => item.Status == "closed" && item.Type == "task").DistinctBy(item => item.Id)
                .OrderByDescending(item => item.ActivityAt).ThenBy(item => item.Id, StringComparer.Ordinal).Take(128).ToArray();
            var initial = inventory;
            var before = CommandCount;
            foreach (var item in candidates)
            {
                var id = item.Id;
                digestEvidenceIds.Add(id);
                var name = "merge:" + id;
                if (sourceBeads.TryGetValue(name, out var identity) && !SameSectionIdentity(identity, initial.ById[id]))
                    schedule.InvalidateNames([name, "comments:" + id]);
                if (sourceBeads.TryGetValue("comments:" + id, out var historyIdentity) && !SameSectionIdentity(historyIdentity, initial.ById[id]))
                    schedule.InvalidateNames(["comments:" + id]);
                var due = !scheduled || schedule.Due(name);
                if (!due || !HasBudget || commentsThisSweep >= 4
                    && (!histories.ContainsKey(id) || !scheduled || schedule.Due("comments:" + id))) continue;
                var observed = await ReadAsync(name, async () => DigestMergeEvidence.Read(initial.ById[id],
                    await ReadSharedHistoryAsync(repository, reader, id, token)
                        ?? throw new ToolExecutionException("Merge history could not be observed")), token);
                if (observed is not null)
                {
                    sourceReadOrder[name] = sourceReadOrder.GetValueOrDefault("comments:" + id);
                    var historySource = states["comments:" + id];
                    states[name] = states[name] with { ObservedAt = historySource.ObservedAt,
                        LastAttemptAt = historySource.LastAttemptAt, ValidatedTimestamp = historySource.ValidatedTimestamp };
                }
            }
            // Native detail must be confirmed by the complete shared inventory.
            // A spent turn keeps each value loading until a later core confirms it.
            if (CommandCount > before)
            {
                if (scheduled) schedule.InvalidateNames(["inventory"]);
                if (HasBudget) inventory = await ReadAsync("inventory", () => reader.ReadSnapshotAsync(repository, token), token, "confirmation");
                ProjectBases(keys, inventory, retainedReady, retainedBlocked, generatedAt, result, inventoryOnly: true);
                ProjectDigestBases(keys, inventory, caughtUp, generatedAt, generatedAt, result);
            }
            var sections = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var item in candidates)
            {
                var name = "merge:" + item.Id;
                var source = states.GetValueOrDefault(name);
                var confirmed = inventory is not null && inventory.ById.TryGetValue(item.Id, out var bead)
                    && SameSectionIdentity(initial.ById[item.Id], bead)
                    && sourceBeads.TryGetValue(name, out var identity) && SameSectionIdentity(identity, bead)
                    && sourceReadOrder.GetValueOrDefault(name) < sourceReadOrder.GetValueOrDefault("inventory")
                    && (!scheduled || schedule.IsCurrent("inventory", states["inventory"].SourceGeneration));
                var current = confirmed && source?.Error is null && (!scheduled || !schedule.Due(name));
                var evidence = confirmed ? cached.GetValueOrDefault(name) as DigestMergeEvidence : null;
                var state = current && evidence is not null ? evidence.State == "verified" ? "available" : "unavailable"
                    : evidence is not null ? "stale" : source?.Error is not null ? "failed" : "loading";
                digestSectionsPending |= state == "loading";
                sections[item.Id] = new
                {
                    beadId = item.Id, updatedAt = initial.ById[item.Id].Payload.GetProperty("updated_at"),
                    state, value = evidence, source, pending = !current,
                    pendingAgeMs = scheduled ? schedule.PendingAge([name]) : null,
                    validatedAgeMs = source?.ValidatedTimestamp is { } validated ? clock.GetElapsedTime(validated).TotalMilliseconds : (double?)null,
                    item.OutcomeLabel, item.Summary, item.Commits, item.PullRequests,
                    reason = state == "unavailable" ? "No matching merge verification is available." : null
                };
            }
            // The aggregate is a set of independent sections. Its clocks cover
            // membership only; each section retains its own source provenance.
            result[key.Value] = CurrentSample(Sample(new { schema = "nightweaver-digest-merges-v1", sections }, generatedAt, DigestCoreSources));
        }
    }
}
