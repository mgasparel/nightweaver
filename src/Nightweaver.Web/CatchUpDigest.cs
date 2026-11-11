using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record CatchUpDigestResponse(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("windowStart")] DateTimeOffset? WindowStart,
    [property: JsonPropertyName("lastCaughtUpAt")] DateTimeOffset? LastCaughtUpAt,
    [property: JsonPropertyName("isCaughtUp")] bool IsCaughtUp,
    [property: JsonPropertyName("metrics")] CatchUpDigestMetrics Metrics,
    [property: JsonPropertyName("threads")] IReadOnlyList<CatchUpDigestThread> Threads,
    [property: JsonPropertyName("standaloneCompleted")] IReadOnlyList<CatchUpDigestItem> StandaloneCompleted,
    [property: JsonPropertyName("standaloneOpen")] IReadOnlyList<CatchUpDigestItem> StandaloneOpen);

internal sealed record CatchUpDigestMetrics(
    [property: JsonPropertyName("completedTotal")] int CompletedTotal,
    [property: JsonPropertyName("completedFeatures")] int CompletedFeatures,
    [property: JsonPropertyName("completedTasks")] int CompletedTasks,
    [property: JsonPropertyName("completedBugs")] int CompletedBugs,
    [property: JsonPropertyName("activeThreads")] int ActiveThreads,
    [property: JsonPropertyName("openOrUpdatedTotal")] int OpenOrUpdatedTotal,
    [property: JsonPropertyName("waitingForGuidance")] int WaitingForGuidance);

internal sealed record CatchUpDigestThread(
    [property: JsonPropertyName("root")] CatchUpDigestItem Root,
    [property: JsonPropertyName("completedCount")] int CompletedCount,
    [property: JsonPropertyName("openCount")] int OpenCount,
    [property: JsonPropertyName("items")] IReadOnlyList<CatchUpDigestItem> Items);

internal sealed record CatchUpDigestItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("pauseLabels")] IReadOnlyList<string> PauseLabels,
    [property: JsonPropertyName("activityKind")] string ActivityKind,
    [property: JsonPropertyName("activityAt")] DateTimeOffset ActivityAt,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("outcomeLabel")] string OutcomeLabel,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("commits")] IReadOnlyList<string> Commits,
    [property: JsonPropertyName("pullRequests")] IReadOnlyList<string> PullRequests,
    [property: JsonPropertyName("fromId")] string? FromId,
    [property: JsonPropertyName("fromRelation")] string? FromRelation);

internal sealed record CatchUpCheckpointResult(
    [property: JsonPropertyName("lastCaughtUpAt")] DateTimeOffset LastCaughtUpAt);

internal sealed record CatchUpCheckpointFile(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("last_caught_up_at")] DateTimeOffset LastCaughtUpAt);

internal sealed class CatchUpRequestValidationException(string detail) : Exception(detail);

internal sealed class CatchUpCheckpointStore(HostPaths paths, ProjectConfig project) : IDisposable
{
    private const int MaximumCheckpointBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly SemaphoreSlim writeGate = new(1, 1);

    public void Dispose() => writeGate.Dispose();

    internal DateTimeOffset? ReadLastCaughtUpAt()
    {
        var path = paths.CatchUpStatePath(project.Name);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = InvocationRuntimeFiles.ReadRuntimeFileBounded(path, MaximumCheckpointBytes);
            var stored = JsonSerializer.Deserialize<CatchUpCheckpointFile>(bytes, JsonOptions);
            if (stored is null || stored.SchemaVersion != 1
                || !string.Equals(stored.Project, project.Name.Value, StringComparison.Ordinal))
                return null;
            return stored.LastCaughtUpAt.ToUniversalTime();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal async Task<CatchUpCheckpointResult> WriteLastCaughtUpAtAsync(DateTimeOffset caughtUpAt,
        CancellationToken cancellationToken)
    {
        var normalized = caughtUpAt.ToUniversalTime();
        var payload = new CatchUpCheckpointFile(1, project.Name.Value, normalized);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(
                paths.CatchUpStatePath(project.Name), bytes, cancellationToken).ConfigureAwait(false);
            return new CatchUpCheckpointResult(normalized);
        }
        finally
        {
            writeGate.Release();
        }
    }

    internal static DateTimeOffset ParseCatchUpRequest(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
            throw new CatchUpRequestValidationException("A JSON object with caughtUpAt is required.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new CatchUpRequestValidationException("The request body must be valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CatchUpRequestValidationException("The request body must be a JSON object.");

            string? rawTimestamp = null;
            var seen = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                string name;
                try
                {
                    name = property.Name;
                }
                catch (InvalidOperationException)
                {
                    throw new CatchUpRequestValidationException("Request property names must contain valid Unicode.");
                }

                if (name != "caughtUpAt")
                    throw new CatchUpRequestValidationException("Only the caughtUpAt property is allowed.");
                if (seen)
                    throw new CatchUpRequestValidationException("The caughtUpAt property may be supplied only once.");
                seen = true;
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new CatchUpRequestValidationException("caughtUpAt must be an ISO-8601 timestamp string.");
                try
                {
                    rawTimestamp = property.Value.GetString();
                }
                catch (InvalidOperationException)
                {
                    throw new CatchUpRequestValidationException("caughtUpAt must contain valid Unicode.");
                }
            }

            if (!seen || string.IsNullOrWhiteSpace(rawTimestamp))
                throw new CatchUpRequestValidationException("caughtUpAt is required.");
            if (!DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                throw new CatchUpRequestValidationException("caughtUpAt must be a valid ISO-8601 timestamp.");
            if (parsed > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new CatchUpRequestValidationException("caughtUpAt cannot be in the future.");
            return parsed.ToUniversalTime();
        }
    }
}

internal static partial class CatchUpDigestProjector
{
    private const int MaxSummaryLength = 600;

    internal static bool IsSupportedWindow(string window)
        => window is "auto" or "since_caught_up" or "24h" or "7d" or "30d" or "all";

    internal static CatchUpDigestResponse Project(
        BeadsInventory inventory,
        string requestedWindow,
        DateTimeOffset? lastCaughtUpAt,
        DateTimeOffset generatedAt)
    {
        var normalizedNow = generatedAt.ToUniversalTime();
        var (effectiveWindow, windowStart) = ResolveWindow(requestedWindow, lastCaughtUpAt, normalizedNow);

        var parsedBeads = new Dictionary<string, ParsedBead>(inventory.ById.Count, StringComparer.Ordinal);
        foreach (var bead in inventory.ById.Values)
            parsedBeads.Add(bead.Id, ParseBead(bead));

        // Resolve primary upward lineage links within the inventory.
        var immediateParents = new Dictionary<string, (string ParentId, string Relation)>(StringComparer.Ordinal);
        foreach (var parsed in parsedBeads.Values)
        {
            var resolved = ResolveImmediateParent(parsed, parsedBeads);
            if (resolved is not null)
                immediateParents[parsed.Id] = resolved.Value;
        }

        // Determine window activity for each bead.
        var windowActivities = new Dictionary<string, (string Kind, DateTimeOffset At)>(StringComparer.Ordinal);
        foreach (var parsed in parsedBeads.Values)
        {
            var activity = EvaluateWindowActivity(parsed, windowStart);
            if (activity is not null)
                windowActivities[parsed.Id] = activity.Value;
        }

        // Group window-active beads by their connected root ancestor in the inventory.
        var groupsByRoot = new Dictionary<string, List<ParsedBead>>(StringComparer.Ordinal);
        foreach (var (beadId, _) in windowActivities)
        {
            var parsed = parsedBeads[beadId];
            var rootId = FindRootAncestorId(parsed.Id, immediateParents);
            if (!groupsByRoot.TryGetValue(rootId, out var members))
            {
                members = [];
                groupsByRoot[rootId] = members;
            }
            members.Add(parsed);
        }

        var threads = new List<(DateTimeOffset LatestAt, CatchUpDigestThread Thread)>();
        var standaloneCompleted = new List<CatchUpDigestItem>();
        var standaloneOpen = new List<CatchUpDigestItem>();

        foreach (var (rootId, activeMembers) in groupsByRoot)
        {
            var rootBead = parsedBeads[rootId];
            var hasRollupContainer = activeMembers.Any(member =>
                member.Id != rootId && IsRollupContainer(rootBead)
                || immediateParents.TryGetValue(member.Id, out var edge)
                    && parsedBeads.TryGetValue(edge.ParentId, out var directParent)
                    && IsRollupContainer(directParent));

            if (activeMembers.Count >= 2 || hasRollupContainer)
            {
                var rootActivity = windowActivities.TryGetValue(rootId, out var activeRoot)
                    ? activeRoot
                    : ("context", rootBead.ClosedAt ?? rootBead.UpdatedAt);
                var rootParent = immediateParents.TryGetValue(rootId, out var rootEdge) ? rootEdge : ((string, string)?)null;
                var projectedRoot = ProjectItem(rootBead, rootActivity.Item1, rootActivity.Item2, rootParent);

                var childItems = activeMembers
                    .Where(member => !string.Equals(member.Id, rootId, StringComparison.Ordinal))
                    .Select(member =>
                    {
                        var activity = windowActivities[member.Id];
                        var parentEdge = immediateParents.TryGetValue(member.Id, out var edge) ? edge : ((string, string)?)null;
                        return ProjectItem(member, activity.Kind, activity.At, parentEdge);
                    })
                    .OrderByDescending(item => item.ActivityAt)
                    .ThenBy(item => item.Priority)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();

                var completedCount = activeMembers.Count(member => member.Status == "closed");
                var openCount = activeMembers.Count(member => member.Status != "closed");
                var latestAt = activeMembers.Max(member => windowActivities[member.Id].At);
                threads.Add((latestAt, new CatchUpDigestThread(projectedRoot, completedCount, openCount, childItems)));
            }
            else
            {
                var single = activeMembers[0];
                var activity = windowActivities[single.Id];
                var parentEdge = immediateParents.TryGetValue(single.Id, out var edge) ? edge : ((string, string)?)null;
                var projected = ProjectItem(single, activity.Kind, activity.At, parentEdge);
                if (single.Status == "closed")
                    standaloneCompleted.Add(projected);
                else
                    standaloneOpen.Add(projected);
            }
        }

        var orderedThreads = threads
            .OrderByDescending(entry => entry.LatestAt)
            .ThenBy(entry => entry.Thread.Root.Priority)
            .ThenBy(entry => entry.Thread.Root.Id, StringComparer.Ordinal)
            .Select(entry => entry.Thread)
            .ToArray();

        var orderedStandaloneCompleted = standaloneCompleted
            .OrderByDescending(item => item.ActivityAt)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var orderedStandaloneOpen = standaloneOpen
            .OrderByDescending(item => item.ActivityAt)
            .ThenBy(item => item.Priority)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var completedBeads = windowActivities
            .Select(kvp => parsedBeads[kvp.Key])
            .Where(bead => bead.Status == "closed")
            .ToArray();
        var completedTotal = completedBeads.Length;
        var completedFeatures = completedBeads.Count(bead => bead.Type is "feature" or "epic");
        var completedTasks = completedBeads.Count(bead => bead.Type == "task");
        var completedBugs = completedBeads.Count(bead => bead.Type == "bug");
        var openOrUpdatedTotal = windowActivities.Count - completedTotal;
        var waitingForGuidance = inventory.ById.Values
            .Count(bead => AttentionDeskProjector.IsPaused(bead) && !AttentionDeskProjector.IsTerminal(bead));

        var isCaughtUp = effectiveWindow == "since_caught_up"
            && lastCaughtUpAt is not null
            && completedTotal == 0
            && openOrUpdatedTotal == 0;

        var metrics = new CatchUpDigestMetrics(
            completedTotal,
            completedFeatures,
            completedTasks,
            completedBugs,
            orderedThreads.Length,
            openOrUpdatedTotal,
            waitingForGuidance);

        return new CatchUpDigestResponse(
            normalizedNow,
            effectiveWindow,
            windowStart,
            lastCaughtUpAt?.ToUniversalTime(),
            isCaughtUp,
            metrics,
            orderedThreads,
            orderedStandaloneCompleted,
            orderedStandaloneOpen);
    }

    private static (string Window, DateTimeOffset? WindowStart) ResolveWindow(
        string requestedWindow,
        DateTimeOffset? lastCaughtUpAt,
        DateTimeOffset now)
    {
        return requestedWindow switch
        {
            "auto" when lastCaughtUpAt is not null => ("since_caught_up", lastCaughtUpAt.Value.ToUniversalTime()),
            "auto" => ("24h", now.AddHours(-24)),
            "since_caught_up" when lastCaughtUpAt is not null => ("since_caught_up", lastCaughtUpAt.Value.ToUniversalTime()),
            "since_caught_up" => ("since_caught_up", now.AddHours(-24)),
            "24h" => ("24h", now.AddHours(-24)),
            "7d" => ("7d", now.AddDays(-7)),
            "30d" => ("30d", now.AddDays(-30)),
            "all" => ("all", null),
            _ => throw new ArgumentOutOfRangeException(nameof(requestedWindow), requestedWindow, "Unsupported digest window.")
        };
    }

    private static (string Kind, DateTimeOffset At)? EvaluateWindowActivity(ParsedBead bead, DateTimeOffset? windowStart)
    {
        if (bead.Status == "closed")
        {
            var closedAt = bead.ClosedAt ?? bead.UpdatedAt;
            if (windowStart is null || closedAt >= windowStart.Value)
                return ("closed", closedAt);
            return null;
        }

        if (windowStart is null || bead.CreatedAt >= windowStart.Value)
            return ("opened", bead.CreatedAt);
        if (bead.UpdatedAt >= windowStart.Value)
            return ("updated", bead.UpdatedAt);
        return null;
    }

    private static bool IsRollupContainer(ParsedBead bead)
        => bead.Type is "feature" or "epic"
            || bead.Labels.Contains("rollup", StringComparer.Ordinal)
            || bead.Labels.Contains("planned", StringComparer.Ordinal);

    private static (string ParentId, string Relation)? ResolveImmediateParent(
        ParsedBead bead,
        Dictionary<string, ParsedBead> byId)
    {
        if (bead.ParentId is not null
            && !string.Equals(bead.ParentId, bead.Id, StringComparison.Ordinal)
            && byId.ContainsKey(bead.ParentId))
            return (bead.ParentId, "parent-child");

        var parentChild = bead.UpwardLinks
            .Where(link => link.Relation == "parent-child"
                && !string.Equals(link.TargetId, bead.Id, StringComparison.Ordinal)
                && byId.ContainsKey(link.TargetId))
            .OrderBy(link => link.TargetId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (parentChild.TargetId is not null)
            return (parentChild.TargetId, "parent-child");

        var discoveredFrom = bead.UpwardLinks
            .Where(link => link.Relation == "discovered-from"
                && !string.Equals(link.TargetId, bead.Id, StringComparison.Ordinal)
                && byId.ContainsKey(link.TargetId))
            .OrderBy(link => link.TargetId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (discoveredFrom.TargetId is not null)
            return (discoveredFrom.TargetId, "discovered-from");

        return null;
    }

    private static string FindRootAncestorId(
        string startId,
        Dictionary<string, (string ParentId, string Relation)> immediateParents)
    {
        var current = startId;
        var visited = new HashSet<string>(StringComparer.Ordinal) { current };
        while (immediateParents.TryGetValue(current, out var parent) && visited.Add(parent.ParentId))
            current = parent.ParentId;
        return current;
    }

    private static CatchUpDigestItem ProjectItem(
        ParsedBead bead,
        string activityKind,
        DateTimeOffset activityAt,
        (string ParentId, string Relation)? parentEdge)
    {
        var pauseLabels = bead.Labels
            .Where(label => label is "product-decision-required" or "needs-human")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var stage = AttentionDeskProjector.Stage(bead.Snapshot);

        if (bead.Status == "closed")
        {
            var rawText = !string.IsNullOrWhiteSpace(bead.CloseReason)
                ? bead.CloseReason!
                : FirstParagraph(bead.Notes);
            var commits = ExtractCommits(bead.CloseReason, bead.Notes);
            var prs = ExtractPullRequests(bead.CloseReason, bead.Notes);
            var (outcome, outcomeLabel) = ClassifyClosedOutcome(bead, rawText, commits);
            var summary = NormalizeSummary(rawText, "Closed in tracker.");
            return new CatchUpDigestItem(
                bead.Id,
                bead.Title,
                bead.Type,
                bead.Priority,
                bead.Status,
                stage,
                pauseLabels,
                activityKind,
                activityAt,
                outcome,
                outcomeLabel,
                summary,
                commits,
                prs,
                parentEdge?.ParentId,
                parentEdge?.Relation);
        }
        else
        {
            var (outcome, outcomeLabel) = ClassifyOpenOutcome(bead.Status, stage, pauseLabels);
            var rawText = !string.IsNullOrWhiteSpace(bead.Notes)
                ? FirstParagraph(bead.Notes)
                : FirstParagraph(bead.Description);
            var commits = ExtractCommits(bead.Notes, null);
            var prs = ExtractPullRequests(bead.Notes, null);
            var summary = NormalizeSummary(rawText, $"Currently {outcomeLabel.ToLowerInvariant()} in tracker.");
            return new CatchUpDigestItem(
                bead.Id,
                bead.Title,
                bead.Type,
                bead.Priority,
                bead.Status,
                stage,
                pauseLabels,
                activityKind,
                activityAt,
                outcome,
                outcomeLabel,
                summary,
                commits,
                prs,
                parentEdge?.ParentId,
                parentEdge?.Relation);
        }
    }

    private static (string Outcome, string Label) ClassifyClosedOutcome(
        ParsedBead bead,
        string? rawText,
        IReadOnlyList<string> commits)
    {
        var hasNeedsHuman = bead.Labels.Contains("needs-human", StringComparer.Ordinal);
        if (hasNeedsHuman && (string.IsNullOrWhiteSpace(rawText)
            || rawText.Contains("abort", StringComparison.OrdinalIgnoreCase)))
            return ("aborted", "Aborted");

        if (commits.Count == 0 && !string.IsNullOrWhiteSpace(rawText) && NoOpPattern().IsMatch(rawText))
            return ("no_op", "Closed (No-Op / Duplicate)");

        if (commits.Count > 0)
            return ("resolved", "Merged / Resolved");

        return ("resolved", "Resolved");
    }

    private static (string Outcome, string Label) ClassifyOpenOutcome(
        string status,
        string? stage,
        string[] pauseLabels)
    {
        if (pauseLabels.Length > 0)
            return ("paused", string.Join(" · ", pauseLabels));
        if (status == "in_progress")
            return ("in_progress", stage is not null ? $"In progress ({stage})" : "In progress");
        if (status == "blocked")
            return ("blocked", "Blocked");
        return ("open", stage is not null ? $"Open ({stage})" : "Open");
    }

    internal static IReadOnlyList<string> ExtractCommits(string? primary, string? secondary)
    {
        var combined = string.Join("\n", new[] { primary, secondary }.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(combined)) return [];

        var commits = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ContextualCommitPattern().Matches(combined))
        {
            var sha = match.Groups["sha"].Value;
            AddCandidateSha(sha, commits, seen);
            if (commits.Count >= 4) return commits;
        }

        foreach (Match match in StandaloneShaPattern().Matches(combined))
        {
            var sha = match.Groups["sha"].Value;
            if (!sha.Any(char.IsAsciiDigit) || !sha.Any(ch => ch is (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                continue;
            AddCandidateSha(sha, commits, seen);
            if (commits.Count >= 4) break;
        }

        return commits;

        static void AddCandidateSha(string raw, List<string> list, HashSet<string> existing)
        {
            var shortSha = raw.Length > 7 ? raw[..7].ToLowerInvariant() : raw.ToLowerInvariant();
            if (existing.Add(shortSha))
                list.Add(shortSha);
        }
    }

    internal static IReadOnlyList<string> ExtractPullRequests(string? primary, string? secondary)
    {
        var combined = string.Join("\n", new[] { primary, secondary }.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(combined)) return [];

        var prs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PullRequestPattern().Matches(combined))
        {
            var tag = "#" + match.Groups["num"].Value;
            if (seen.Add(tag))
            {
                prs.Add(tag);
                if (prs.Count >= 4) break;
            }
        }
        return prs;
    }

    private static string? FirstParagraph(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = paragraph.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            // Skip pure markdown headings if a prose line follows
            var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .ToArray();
            if (lines.Length > 0)
                return string.Join(" ", lines);
        }
        return text.Trim();
    }

    private static string NormalizeSummary(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var collapsed = WhitespacePattern().Replace(raw.Trim(), " ");
        if (collapsed.Length <= MaxSummaryLength) return collapsed;
        return collapsed[..MaxSummaryLength].TrimEnd() + "…";
    }

    private static ParsedBead ParseBead(BeadSnapshot bead)
    {
        var item = BeadExplorerItem.FromSnapshot(bead);
        var payload = bead.Payload;
        var updatedAt = ReadOptionalTimestamp(payload, "updated_at", bead.Id) ?? bead.CreatedAt.ToUniversalTime();
        var closedAt = ReadOptionalTimestamp(payload, "closed_at", bead.Id);
        var closeReason = ReadOptionalString(payload, "close_reason", bead.Id);
        var notes = ReadOptionalString(payload, "notes", bead.Id);
        var description = ReadOptionalString(payload, "description", bead.Id);
        var parentId = ReadOptionalString(payload, "parent", bead.Id);
        var upwardLinks = ReadUpwardLinks(payload, bead.Id);

        return new ParsedBead(
            bead,
            item.Id,
            item.Title,
            item.Type,
            item.Priority,
            item.Status,
            item.Labels,
            bead.CreatedAt.ToUniversalTime(),
            updatedAt,
            closedAt,
            closeReason,
            notes,
            description,
            string.IsNullOrWhiteSpace(parentId) ? null : parentId.Trim(),
            upwardLinks);
    }

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement payload, string propertyName, string beadId)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new JsonException($"Bead '{beadId}' has an invalid '{propertyName}' timestamp.");
        return parsed.ToUniversalTime();
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName, string beadId)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Bead '{beadId}' has a non-string '{propertyName}' property.");
        return value.GetString();
    }

    private static List<(string TargetId, string Relation)> ReadUpwardLinks(JsonElement payload, string beadId)
    {
        var links = new List<(string TargetId, string Relation)>();
        if (!payload.TryGetProperty("dependencies", out var deps) || deps.ValueKind == JsonValueKind.Null)
            return links;
        if (deps.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Bead '{beadId}' has a non-array 'dependencies' property.");

        foreach (var entry in deps.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Bead '{beadId}' has a non-object dependency entry.");

            var relation = entry.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : entry.TryGetProperty("dependency_type", out var depTypeProp) && depTypeProp.ValueKind == JsonValueKind.String
                    ? depTypeProp.GetString()
                    : null;
            if (relation is not ("parent-child" or "discovered-from"))
                continue;

            var targetId = entry.TryGetProperty("depends_on_id", out var dependsOnProp) && dependsOnProp.ValueKind == JsonValueKind.String
                ? dependsOnProp.GetString()
                : entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(targetId))
                links.Add((targetId.Trim(), relation));
        }

        return links;
    }

    private sealed record ParsedBead(
        BeadSnapshot Snapshot,
        string Id,
        string Title,
        string Type,
        int Priority,
        string Status,
        IReadOnlyList<string> Labels,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ClosedAt,
        string? CloseReason,
        string? Notes,
        string? Description,
        string? ParentId,
        IReadOnlyList<(string TargetId, string Relation)> UpwardLinks);

    [GeneratedRegex(@"\b(?:duplicate|no\s+fix\s+claimed|no-op|superseded|wontfix)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoOpPattern();

    [GeneratedRegex(@"\b(?:commit(?:ted)?|merged(?:\s+into\s+(?:local\s+)?main)?(?:\s+(?:as|at))?|at|as)\s*(?<sha>[0-9a-f]{7,40})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextualCommitPattern();

    [GeneratedRegex(@"(?<![0-9a-zA-Z_-])(?<sha>[0-9a-f]{7,40})(?![0-9a-zA-Z_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneShaPattern();

    [GeneratedRegex(@"(?:PR\s*#|(?<!\w)#)(?<num>[1-9]\d{0,5})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
