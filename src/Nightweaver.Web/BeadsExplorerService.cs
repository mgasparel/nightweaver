using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Execution;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Web;

internal sealed record BeadExplorerItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("assignee")] string? Assignee)
{
    internal static BeadExplorerItem FromSnapshot(BeadSnapshot bead)
    {
        var titles = bead.Payload.EnumerateObject().Where(property => property.NameEquals("title")).ToArray();
        if (titles.Length != 1 || titles[0].Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(titles[0].Value.GetString()))
            throw new JsonException($"Bead '{bead.Id}' has no valid title string.");

        return Map(bead.Id, titles[0].Value.GetString()!, bead.IssueType, bead.Priority,
            bead.Status, bead.Labels, bead.Assignee);
    }

    internal static BeadExplorerItem FromCreated(ProductBeadCreateResult bead)
        => Map(bead.Id, bead.Title, bead.Type, bead.Priority, bead.Status, bead.Labels, bead.Assignee);

    private static BeadExplorerItem Map(string id, string title, string type, int priority, string status,
        IReadOnlyList<string> labels, string? assignee)
        => new(id, title, type, priority, status, labels,
            string.IsNullOrWhiteSpace(assignee) ? null : assignee);
}

// The shared gate serializes embedded-Dolt reads and writes within this host.
internal sealed class TrackerBridge(
    ProjectConfig project,
    TargetRepositoryValidator targetRepositoryValidator,
    BeadsReader beadsReader,
    BeadsWriter beadsWriter) : IDisposable
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public void Dispose() => operationGate.Dispose();

    internal async Task<IReadOnlyList<BeadExplorerItem>> ReadAsync(string view, string? query, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var target = await targetRepositoryValidator.ValidateAsync(
                project.TargetRepository, project.ConfigFilePath, cancellationToken);
            var inventory = await beadsReader.ReadSnapshotAsync(target, cancellationToken);
            FrozenSet<string>? readyIds = null;
            FrozenSet<string>? blockedIds = null;
            if (view == "ready")
                readyIds = await beadsReader.ReadExplorerReadinessAsync(target, cancellationToken);
            else if (view == "blocked")
                blockedIds = await beadsReader.ReadExplorerBlockedAsync(target, cancellationToken);

            // Project the complete inventory first so malformed required display data cannot
            // produce a partial response whose completeness depends on the selected filter.
            var items = inventory.ById.Values.Select(BeadExplorerItem.FromSnapshot).ToArray();
            return items
                .Where(item => IsInView(item, view, readyIds, blockedIds))
                .Where(item => query is null
                    || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<IReadOnlyList<AttentionDeskItem>> ReadAttentionAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var target = await targetRepositoryValidator.ValidateAsync(
                project.TargetRepository, project.ConfigFilePath, cancellationToken);
            var inventory = await beadsReader.ReadSnapshotAsync(target, cancellationToken);
            var items = new List<AttentionDeskItem>();
            foreach (var bead in inventory.ById.Values
                         .Where(AttentionDeskProjector.IsPaused)
                         .Where(bead => !AttentionDeskProjector.IsTerminal(bead)))
            {
                var comments = await beadsReader.ReadAttentionCommentsAsync(target, bead.Id, cancellationToken);
                items.Add(AttentionDeskProjector.Project(bead, comments, project.Limits.MaxRepairCycles));
            }

            return items.OrderBy(item => item.Priority)
                .ThenBy(item => item.PausedAt is null ? 1 : 0)
                .ThenBy(item => item.PausedAt)
                .ThenBy(item => item.BeadId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task<AttentionMutationResult> DecideAsync(string beadId, string instruction, string action,
        CancellationToken cancellationToken)
    {
        if (action is not ("resume" or "note_only"))
            throw new AttentionActionException(400, "Invalid action", "Action must be resume or note_only.");
        return MutateAsync(beadId, action, "product-decision-required",
            BeadsWriter.CreateHumanGuidanceComment(action, instruction), cancellationToken);
    }

    internal Task<AttentionMutationResult> RetryAsync(string beadId, CancellationToken cancellationToken)
        => MutateAsync(beadId, "retry", "needs-human",
            BeadsWriter.CreateHumanActionComment("retry"), cancellationToken);

    internal Task<AttentionMutationResult> AbortAsync(string beadId, CancellationToken cancellationToken)
        => MutateAsync(beadId, "abort", "needs-human",
            BeadsWriter.CreateHumanActionComment("abort"), cancellationToken);

    internal async Task<BeadExplorerItem> CreateProductBeadAsync(ProductBeadIntakeSubmission submission,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var target = await targetRepositoryValidator.ValidateAsync(
                project.TargetRepository, project.ConfigFilePath, cancellationToken);
            var created = await beadsWriter.CreateProductBeadAsync(target, submission, cancellationToken);
            return BeadExplorerItem.FromCreated(created);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<AttentionMutationResult> MutateAsync(string beadId, string action, string pauseLabel,
        string expectedComment, CancellationToken cancellationToken)
    {
        if (!BeadIdSafety.IsSafe(beadId))
            throw new AttentionActionException(400, "Invalid Bead ID", "The Bead ID is not a safe exact tracker ID.");

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var target = await targetRepositoryValidator.ValidateAsync(
                project.TargetRepository, project.ConfigFilePath, cancellationToken);
            var initial = await ReadExactBeadAsync(target, beadId, cancellationToken);
            EnsureActionable(initial, beadId, action, pauseLabel);
            var initialIdentity = AttentionMutationIdentity.Capture(initial!);

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (action is "resume" or "note_only")
                    await beadsWriter.AddHumanGuidanceAsync(target, beadId, action,
                        ExtractInstruction(expectedComment), cancellationToken);
                else
                    await beadsWriter.AddHumanActionAsync(target, beadId, action, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _ = await TryVerifyCommentAsync(target, beadId, expectedComment);
                throw;
            }
            catch (Exception exception) when (IsExpectedTrackerFailure(exception))
            {
                var recorded = await TryVerifyCommentAsync(target, beadId, expectedComment);
                throw new AttentionActionException(503, "Beads tracker unavailable",
                    "The human comment could not be confirmed. Refresh the Attention Desk before retrying.",
                    recorded, false, exception, includeProgress: true);
            }

            var afterComment = await ReadAfterWriteAsync(target, beadId, action, expectedComment, cancellationToken);
            if (afterComment is null)
                throw new AttentionActionException(409, "Bead changed", "The Bead disappeared after the comment was recorded. Refresh before taking another action.",
                    true, true, includeProgress: true);
            var afterCommentIdentity = AttentionMutationIdentity.Capture(afterComment);
            if (!initialIdentity.SameObservedIdentity(afterCommentIdentity))
                throw new AttentionActionException(409, "Bead changed", "The Bead workflow changed after the comment was recorded. Refresh before taking another action.",
                    true, true, includeProgress: true);

            if (action == "note_only")
                return new AttentionMutationResult(beadId, action, false, true, false);

            cancellationToken.ThrowIfCancellationRequested();
            Exception? updateFailure = null;
            try
            {
                switch (action)
                {
                    case "resume":
                        await beadsWriter.ResolveProductDecisionAsync(target, beadId, cancellationToken);
                        break;
                    case "retry":
                        await beadsWriter.RetryNeedsHumanAsync(target, beadId, cancellationToken);
                        break;
                    case "abort":
                        await beadsWriter.AbortNeedsHumanAsync(target, beadId, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected Attention Desk action '{action}'.");
                }
            }
            catch (OperationCanceledException)
            {
                _ = await TryReadCurrentAsync(target, beadId);
                throw;
            }
            catch (Exception exception) when (IsExpectedTrackerFailure(exception))
            {
                updateFailure = exception;
            }

            var finalRead = await TryReadCurrentAsync(target, beadId);
            if (!finalRead.Readable)
            {
                var statusCode = updateFailure is BeadsGuardConflictException ? 409 : 503;
                var title = statusCode == 409 ? "Bead changed" : "Beads tracker unavailable";
                throw new AttentionActionException(statusCode, title,
                    "The action outcome could not be verified. Refresh the Attention Desk before retrying.",
                    true, null, updateFailure, includeProgress: true);
            }

            var finalIdentity = finalRead.Bead is null ? null : AttentionMutationIdentity.Capture(finalRead.Bead);
            var verified = finalIdentity is not null && initialIdentity.MatchesResolution(finalIdentity, action, pauseLabel);
            if (updateFailure is not null)
            {
                var statusCode = updateFailure is BeadsGuardConflictException ? 409 : 503;
                throw new AttentionActionException(statusCode,
                    statusCode == 409 ? "Bead changed" : "Beads tracker unavailable",
                    "The state update was not confirmed. Refresh the Attention Desk before retrying.",
                    true, finalIdentity is null ? true : !initialIdentity.SameObservedIdentity(finalIdentity),
                    updateFailure, includeProgress: true);
            }

            if (!verified)
                throw new AttentionActionException(409, "Bead changed",
                    "The Bead did not reach the requested workflow state. Refresh the Attention Desk before retrying.",
                    true, finalIdentity is null ? true : !initialIdentity.SameObservedIdentity(finalIdentity),
                    includeProgress: true);

            return new AttentionMutationResult(beadId, action, true, true, true);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<BeadSnapshot?> ReadExactBeadAsync(ValidatedTargetRepository target, string beadId,
        CancellationToken cancellationToken)
    {
        var inventory = await beadsReader.ReadSnapshotAsync(target, cancellationToken);
        return inventory.ById.TryGetValue(beadId, out var bead) ? bead : null;
    }

    private async Task<BeadSnapshot?> ReadAfterWriteAsync(ValidatedTargetRepository target, string beadId,
        string action, string expectedComment, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadExactBeadAsync(target, beadId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = await TryVerifyCommentAsync(target, beadId, expectedComment);
            throw;
        }
        catch (Exception exception) when (IsExpectedTrackerFailure(exception))
        {
            throw new AttentionActionException(503, "Beads tracker unavailable",
                "The comment was recorded, but the Bead could not be re-read before the state update. Refresh before retrying.",
                true, null, exception, includeProgress: true);
        }
    }

    private async Task<bool?> TryVerifyCommentAsync(ValidatedTargetRepository target, string beadId, string expectedComment)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try
        {
            var comments = await beadsReader.ReadAttentionCommentsAsync(target, beadId, timeout.Token);
            return comments.Any(comment => comment.Text == expectedComment && comment.Author == "local-console");
        }
        catch (Exception exception) when (IsExpectedTrackerFailure(exception) || exception is OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<(bool Readable, BeadSnapshot? Bead)> TryReadCurrentAsync(ValidatedTargetRepository target, string beadId)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try
        {
            return (true, await ReadExactBeadAsync(target, beadId, timeout.Token));
        }
        catch (Exception exception) when (IsExpectedTrackerFailure(exception) || exception is OperationCanceledException)
        {
            return (false, null);
        }
    }

    private static void EnsureActionable(BeadSnapshot? bead, string beadId, string action, string pauseLabel)
    {
        if (bead is null)
            throw new AttentionActionException(404, "Bead not found", "No exact Bead ID matched the configured target inventory.");
        if (AttentionDeskProjector.InvalidWorkflowReason(bead) is not null)
            throw new AttentionActionException(409, "Invalid workflow", "This Bead has contradictory or malformed workflow state and cannot be changed from the Attention Desk.");
        if (AttentionDeskProjector.IsTerminal(bead) || bead.Status != "open" || bead.Assignee is not null)
            throw new AttentionActionException(409, "Bead is unavailable", "Only open, unassigned, nonterminal paused Beads can be changed from the Attention Desk.");
        if (!bead.Labels.Contains(pauseLabel, StringComparer.Ordinal))
            throw new AttentionActionException(409, "Pause is no longer active", "The requested pause label is no longer present. Refresh the Attention Desk before retrying.");
        if (action is "resume" or "note_only" && pauseLabel != "product-decision-required"
            || action is "retry" or "abort" && pauseLabel != "needs-human")
            throw new AttentionActionException(409, "Action does not match pause", "This action is not available for the current pause type.");
    }

    private static bool IsExpectedTrackerFailure(Exception exception)
        => exception is ToolExecutionException or TargetRepositoryException or ProjectConfigurationException
            or BeadsGuardConflictException or JsonException or IOException or UnauthorizedAccessException;

    private static string ExtractInstruction(string comment)
    {
        using var document = JsonDocument.Parse(comment);
        return document.RootElement.GetProperty("instruction").GetString()!;
    }

    private static bool IsInView(BeadExplorerItem item, string view, FrozenSet<string>? readyIds, FrozenSet<string>? blockedIds)
        => view switch
        {
            "all" => true,
            "active" => item.Status == "in_progress",
            "ready" => item.Status == "open" && readyIds!.Contains(item.Id),
            "blocked" => item.Status != "closed" && (item.Status == "blocked" || blockedIds!.Contains(item.Id)),
            "closed" => item.Status == "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unknown Beads view")
        };
}
