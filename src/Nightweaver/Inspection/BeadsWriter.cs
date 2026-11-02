using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Nightweaver.Inspection;

internal sealed record ProductBeadIntakeSubmission(string Type, int Priority, string Title,
    string Description, string AcceptanceCriteria);

internal sealed record ProductBeadCreateResult(string Id, string Title, string Type, int Priority,
    string Status, IReadOnlyList<string> Labels, string? Assignee);

internal enum GuardedStrandedClaimReleaseResult { Released, GuardMismatch }

internal sealed class ProductBeadCreationException(string message, Exception innerException)
    : Exception(message, innerException);

/// <summary>Scoped local Beads writes for Product Bead intake, dispatch claims, and needs-human escalation.</summary>
internal sealed class BeadsWriter(IProcessRunner processRunner)
{
    internal async Task<ProductBeadCreateResult> CreateProductBeadAsync(ValidatedTargetRepository repository,
        ProductBeadIntakeSubmission submission, CancellationToken cancellationToken)
    {
        BeadsReader.EnsureMutationFreeBackend(repository);
        cancellationToken.ThrowIfCancellationRequested();

        var arguments = new List<string> { "create" };
        AddStringOption(arguments, "--title", submission.Title);
        arguments.Add("--type");
        arguments.Add(submission.Type);
        arguments.Add("--priority");
        arguments.Add(submission.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddStringOption(arguments, "--description", submission.Description);
        AddStringOption(arguments, "--acceptance", submission.AcceptanceCriteria);
        arguments.Add("--status");
        arguments.Add("open");
        arguments.Add("--json");

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path, arguments), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ToolExecutionException or IOException
            or UnauthorizedAccessException or Win32Exception)
        {
            throw new ProductBeadCreationException(
                "The Product Bead create command did not return a confirmed result.", exception);
        }

        if (result.ExitCode != 0)
        {
            throw new ProductBeadCreationException(
                "The Product Bead create command did not return a confirmed result.",
                new ToolExecutionException($"bd create exited {result.ExitCode}", result.StandardError));
        }

        try
        {
            return ParseProductBeadCreateResult(result.StandardOutput, submission);
        }
        catch (JsonException exception)
        {
            throw new ProductBeadCreationException(
                "The Product Bead create command returned an incompatible result.", exception);
        }
    }

    internal Task AcquireDispatchClaimAsync(ValidatedTargetRepository repository, string beadId, string actor,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["update", beadId, "--if-status", "open", "--if-assignee", "", "--status", "in_progress", "--assignee", actor, "--json"],
        $"acquire dispatch claim for Bead '{beadId}' as '{actor}'", cancellationToken, actor);

    internal async Task AcquireDeliveryDispatchClaimAsync(ValidatedTargetRepository repository, string beadId,
        string actor, CancellationToken cancellationToken)
    {
        var operation = $"acquire Delivery dispatch claim for Bead '{beadId}' as '{actor}'";
        BeadsReader.EnsureMutationFreeBackend(repository);
        var result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path,
            ["update", beadId, "--if-status", "open", "--if-assignee", "", "--status", "in_progress",
                "--assignee", actor, "--json"], actor), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 13)
            throw new BeadsGuardConflictException(
                $"{operation} in Target Repository '{repository.Path}' was refused because the open/unassigned guard no longer matched (bd exited 13; no write was performed).");
        if (result.ExitCode != 0)
            throw new ToolExecutionException(
                $"{operation} in Target Repository '{repository.Path}' did not return a confirmed result: bd exited {result.ExitCode}"
                + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : $": {result.StandardError.TrimEnd()}"),
                result.StandardError);
    }

    internal Task ReleaseDispatchClaimAsync(ValidatedTargetRepository repository, string beadId, string actor,
        string expectedStatus, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "update", beadId };
        if (expectedStatus is "open" or "in_progress")
        {
            arguments.Add("--status");
            arguments.Add("open");
        }
        arguments.Add("--assignee");
        arguments.Add(string.Empty);
        arguments.Add("--if-assignee");
        arguments.Add(actor);
        arguments.Add("--if-status");
        arguments.Add(expectedStatus);
        arguments.Add("--json");
        return RunAsync(repository, arguments,
            $"release dispatch claim for Bead '{beadId}' as '{actor}' guarded by its observed {expectedStatus} state",
            cancellationToken, actor);
    }

    internal async Task<ProcessResult> PublishDeliveryOutcomeAsync(
        ValidatedTargetRepository repository,
        string beadId,
        string actor,
        string expectedStatus,
        string outcomeLabel,
        string? removeStageLabel,
        int? repairCycles,
        bool releaseClaim,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "update", beadId, "--add-label", outcomeLabel };
        if (removeStageLabel is not null)
        {
            arguments.Add("--remove-label");
            arguments.Add(removeStageLabel);
        }
        if (repairCycles is { } counter)
        {
            arguments.Add("--set-metadata");
            arguments.Add($"repair_cycles={counter.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (releaseClaim)
        {
            arguments.Add("--status");
            arguments.Add("open");
            arguments.Add("--assignee");
            arguments.Add(string.Empty);
        }
        else
        {
            // Beads applies --if-status/--if-assignee guards only when the same
            // update writes at least one guarded field. Reassert the verified
            // live claim values atomically with the lifecycle-label change.
            arguments.Add("--status");
            arguments.Add(expectedStatus);
            arguments.Add("--assignee");
            arguments.Add(actor);
        }
        arguments.Add("--if-assignee");
        arguments.Add(actor);
        arguments.Add("--if-status");
        arguments.Add(expectedStatus);
        arguments.Add("--json");
        BeadsReader.EnsureMutationFreeBackend(repository);
        return await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path, arguments, actor), cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<GuardedStrandedClaimReleaseResult> ReleaseStrandedClaimAsync(
        ValidatedTargetRepository repository, string beadId, string actor, CancellationToken cancellationToken)
    {
        try
        {
            BeadsReader.EnsureMutationFreeBackend(repository);
            var result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path,
                ["update", beadId, "--status", "open", "--assignee", string.Empty,
                    "--if-status", "in_progress", "--if-assignee", actor, "--json"], actor), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 13) return GuardedStrandedClaimReleaseResult.GuardMismatch;
            if (result.ExitCode != 0)
                throw new ToolExecutionException(
                    $"guarded startup recovery for Bead '{beadId}' as '{actor}' in Target Repository '{repository.Path}' failed: bd exited {result.ExitCode}"
                    + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : $": {result.StandardError.TrimEnd()}"),
                    result.StandardError);
            return GuardedStrandedClaimReleaseResult.Released;
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException(
                $"guarded startup recovery for Bead '{beadId}' as '{actor}' in Target Repository '{repository.Path}' failed: {exception.Message}",
                exception.StandardError);
        }
    }

    internal Task ApplyNeedsHumanAsync(ValidatedTargetRepository repository, string beadId, string expectedAssignee, string expectedStatus,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["update", beadId, "--assignee", expectedAssignee, "--add-label", "needs-human", "--remove-label", "product-decision-required",
            "--if-assignee", expectedAssignee, "--if-status", expectedStatus],
        $"apply needs-human to Bead '{beadId}' guarded by its observed {expectedStatus} claim state", cancellationToken);

    internal Task AddCommentAsync(ValidatedTargetRepository repository, string beadId, string comment,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["comments", "add", beadId, comment], $"add needs-human diagnostic comment to Bead '{beadId}'", cancellationToken);

    internal Task EscalateStartupCleanupFailureAsync(ValidatedTargetRepository repository, string beadId,
        string expectedStatus, string? expectedAssignee, string actor, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "update", beadId, "--add-label", "needs-human", "--assignee",
            expectedAssignee ?? string.Empty, "--if-status", expectedStatus, "--if-assignee", expectedAssignee ?? string.Empty, "--json" };
        return RunAsync(repository, arguments,
            $"escalate startup cleanup failure for Bead '{beadId}' without changing its status or owner", cancellationToken, actor);
    }

    internal Task AddStartupCleanupCommentAsync(ValidatedTargetRepository repository, string beadId, string comment,
        string actor, CancellationToken cancellationToken) => RunAsync(repository,
        ["comments", "add", beadId, comment], $"record startup cleanup diagnostic for Bead '{beadId}'", cancellationToken, actor);

    internal async Task AddHumanGuidanceAsync(ValidatedTargetRepository repository, string beadId, string action,
        string instruction, CancellationToken cancellationToken)
    {
        BeadsReader.EnsureMutationFreeBackend(repository);
        cancellationToken.ThrowIfCancellationRequested();
        var comment = CreateHumanGuidanceComment(action, instruction);
        var commentPath = Path.Combine(Path.GetTempPath(), $"nightweaver-guidance-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(commentPath, comment, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            await RunAsync(repository, ["comments", "add", beadId, "-f", commentPath],
                $"record local human guidance for Bead '{beadId}'", cancellationToken, "local-console");
        }
        finally
        {
            try { File.Delete(commentPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A cleanup failure must not obscure whether bd recorded the comment.
            }
        }
    }

    internal Task AddHumanActionAsync(ValidatedTargetRepository repository, string beadId, string action,
        CancellationToken cancellationToken)
    {
        var comment = CreateHumanActionComment(action);
        return RunAsync(repository, ["comments", "add", beadId, comment],
            $"record local human '{action}' action for Bead '{beadId}'", cancellationToken, "local-console");
    }

    internal static string CreateHumanGuidanceComment(string action, string instruction) => JsonSerializer.Serialize(new
    {
        schema = "nightweaver-human-guidance-v1",
        action,
        instruction,
        actor = "local-console"
    });

    internal static string CreateHumanActionComment(string action) => JsonSerializer.Serialize(new
    {
        schema = "nightweaver-human-action-v1",
        action,
        actor = "local-console"
    });

    internal Task ResolveProductDecisionAsync(ValidatedTargetRepository repository, string beadId,
        CancellationToken cancellationToken) => ResumePausedBeadAsync(repository, beadId,
        "resolve product decision", cancellationToken);

    internal Task RetryNeedsHumanAsync(ValidatedTargetRepository repository, string beadId,
        CancellationToken cancellationToken) => ResumePausedBeadAsync(repository, beadId,
        "reset the repair budget and retry", cancellationToken);

    private Task ResumePausedBeadAsync(ValidatedTargetRepository repository, string beadId, string operation,
        CancellationToken cancellationToken) => RunGuardedAsync(repository,
        ["update", beadId, "--if-status", "open", "--if-assignee", "", "--remove-label", "needs-human",
            "--remove-label", "product-decision-required", "--unset-metadata", "repair_cycles", "--json"],
        $"{operation} Bead '{beadId}'", cancellationToken);

    internal Task AbortNeedsHumanAsync(ValidatedTargetRepository repository, string beadId,
        CancellationToken cancellationToken) => RunGuardedAsync(repository,
        ["update", beadId, "--status", "closed", "--if-status", "open", "--if-assignee", "", "--json"],
        $"abort and close Bead '{beadId}'", cancellationToken);

    internal Task ReleaseClaimAsync(ValidatedTargetRepository repository, string beadId, string assignee,
        CancellationToken cancellationToken) => RunAsync(repository,
        ["update", beadId, "--status", "open", "--assignee", "", "--if-assignee", assignee, "--if-status", "in_progress"],
        $"release needs-human claim for Bead '{beadId}' guarded by its live claim", cancellationToken);

    private static void AddStringOption(List<string> arguments, string option, string value)
    {
        if (value.StartsWith('-'))
        {
            arguments.Add($"{option}={value}");
            return;
        }

        arguments.Add(option);
        arguments.Add(value);
    }

    private static ProductBeadCreateResult ParseProductBeadCreateResult(string json,
        ProductBeadIntakeSubmission submission)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("created issue must be an object");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = null;
        string? title = null;
        string? type = null;
        string? status = null;
        int? priority = null;
        string? assignee = null;
        List<string> labels = [];
        var assigneeSeen = false;
        var labelsSeen = false;

        foreach (var property in root.EnumerateObject())
        {
            var propertyName = ReadPropertyName(property);
            if (!seen.Add(propertyName))
                throw new JsonException("created issue contains a duplicate property");

            switch (propertyName)
            {
                case "id":
                    id = ReadNonblankString(property.Value, "id");
                    break;
                case "title":
                    title = ReadNonblankString(property.Value, "title");
                    break;
                case "issue_type":
                    type = ReadNonblankString(property.Value, "issue_type");
                    break;
                case "priority":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedPriority))
                        throw new JsonException("created issue priority must be an integer");
                    priority = parsedPriority;
                    break;
                case "status":
                    status = ReadNonblankString(property.Value, "status");
                    break;
                case "assignee":
                    assigneeSeen = true;
                    if (property.Value.ValueKind == JsonValueKind.Null)
                        assignee = null;
                    else if (property.Value.ValueKind == JsonValueKind.String)
                        assignee = ReadStringValue(property.Value);
                    else
                        throw new JsonException("created issue assignee must be a string or null");
                    break;
                case "labels":
                    labelsSeen = true;
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new JsonException("created issue labels must be an array");
                    var parsedLabels = new List<string>();
                    foreach (var label in property.Value.EnumerateArray())
                    {
                        var parsedLabel = label.ValueKind == JsonValueKind.String ? ReadStringValue(label) : null;
                        if (string.IsNullOrWhiteSpace(parsedLabel))
                            throw new JsonException("created issue labels must contain nonblank strings");
                        parsedLabels.Add(parsedLabel);
                    }
                    labels = parsedLabels;
                    break;
            }
        }

        if (id is null || title is null || type is null || priority is null || status is null)
            throw new JsonException("created issue is missing a required field");
        if (!string.Equals(title, submission.Title, StringComparison.Ordinal)
            || !string.Equals(type, submission.Type, StringComparison.Ordinal)
            || priority.Value != submission.Priority || !string.Equals(status, "open", StringComparison.Ordinal))
            throw new JsonException("created issue does not match the requested Product Bead");
        if (assigneeSeen && !string.IsNullOrWhiteSpace(assignee))
            throw new JsonException("created Product Bead is assigned");
        if (labelsSeen && labels.Count != 0)
            throw new JsonException("created Product Bead unexpectedly has workflow labels");

        return new ProductBeadCreateResult(id, title, type, priority.Value, status, labels, assignee);
    }

    private static string ReadNonblankString(JsonElement value, string propertyName)
    {
        var decoded = value.ValueKind == JsonValueKind.String ? ReadStringValue(value) : null;
        if (string.IsNullOrWhiteSpace(decoded))
            throw new JsonException($"created issue {propertyName} must be a nonblank string");
        return decoded;
    }

    private static string ReadPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("created issue contains invalid Unicode", exception);
        }
    }

    private static string? ReadStringValue(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("created issue contains invalid Unicode", exception);
        }
    }

    private async Task RunAsync(ValidatedTargetRepository repository, IReadOnlyList<string> arguments,
        string operation, CancellationToken cancellationToken, string? actor = null)
    {
        try
        {
            BeadsReader.EnsureMutationFreeBackend(repository);
            var result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path, arguments, actor), cancellationToken);
            if (result.ExitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError.TrimEnd()
                    : string.IsNullOrWhiteSpace(result.StandardOutput) ? string.Empty
                    : result.StandardOutput.TrimEnd()[..Math.Min(result.StandardOutput.TrimEnd().Length, 1024)];
                throw new ToolExecutionException($"{operation}: bd exited {result.ExitCode}"
                    + (result.ExitCode == 13 ? " (the guarded state no longer matches; no retry was attempted)" : string.Empty)
                    + (detail.Length == 0 ? string.Empty : $": {detail}"), result.StandardError);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{operation} in Target Repository '{repository.Path}' failed: {exception.Message}", exception.StandardError);
        }
    }

    private async Task RunGuardedAsync(ValidatedTargetRepository repository, IReadOnlyList<string> arguments,
        string operation, CancellationToken cancellationToken)
    {
        try
        {
            BeadsReader.EnsureMutationFreeBackend(repository);
            var result = await processRunner.RunAsync(BeadsCommand.CreateWrite(repository.Path, arguments, "local-console"), cancellationToken);
            if (result.ExitCode == 13)
                throw new BeadsGuardConflictException($"{operation} was refused by bd because the observed status or assignee guard no longer matched.");
            if (result.ExitCode != 0)
                throw new ToolExecutionException($"{operation}: bd exited {result.ExitCode}", result.StandardError);
        }
        catch (OperationCanceledException) { throw; }
        catch (BeadsGuardConflictException) { throw; }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{operation} in Target Repository '{repository.Path}' failed: {exception.Message}", exception.StandardError);
        }
    }
}
