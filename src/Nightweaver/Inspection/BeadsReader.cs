using System.Collections.Frozen;
using System.Text.Json;

namespace Nightweaver.Inspection;

internal sealed class BeadsReader(IProcessRunner processRunner)
{
    internal async Task<BeadsInventory> ReadSnapshotAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        var context = $"bd --readonly --sandbox list inventory in Target Repository '{repository.Path}'";
        var json = await RunQueryAsync(repository, BeadsCommand.Create(repository.Path), context, cancellationToken);
        try
        {
            return Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd JSON: {exception.Message}");
        }
    }

    internal async Task<BeadsReadiness> ReadReadinessAsync(ValidatedTargetRepository repository, BeadsInventory inventory,
        string recoveryActor, CancellationToken cancellationToken)
    {
        var nativeContext = $"bd --readonly --sandbox ready --unassigned --limit 0 --json in Target Repository '{repository.Path}'";
        var nativeJson = await RunQueryAsync(repository, BeadsCommand.CreateUnassignedReadiness(repository.Path), nativeContext, cancellationToken);
        var native = ParseReadiness(nativeJson, inventory, nativeContext, bead => bead.Status == "open" && bead.Assignee is null);

        var recoverableContext = $"bd --readonly --sandbox list --ready --status in_progress --assignee {recoveryActor} --limit 0 --json in Target Repository '{repository.Path}'";
        var recoverableJson = await RunQueryAsync(repository, BeadsCommand.CreateRecoverableReadiness(repository.Path, recoveryActor), recoverableContext, cancellationToken);
        var recoverable = ParseReadiness(recoverableJson, inventory, recoverableContext,
            bead => bead.Status == "in_progress" && string.Equals(bead.Assignee, recoveryActor, StringComparison.Ordinal));
        return BeadsReadiness.Create(native, recoverable);
    }

    internal Task<FrozenSet<string>> ReadExplorerReadinessAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
        => ReadMembershipAsync(repository, BeadsCommand.CreateExplorerReadiness(repository.Path),
            "bd --readonly --sandbox ready --limit 0 --json", requireBlockerMetadata: false, cancellationToken);

    internal Task<FrozenSet<string>> ReadExplorerBlockedAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
        => ReadMembershipAsync(repository, BeadsCommand.CreateExplorerBlocked(repository.Path),
            "bd --readonly --sandbox blocked --json", requireBlockerMetadata: true, cancellationToken);

    private async Task<FrozenSet<string>> ReadMembershipAsync(ValidatedTargetRepository repository, ProcessRequest request,
        string commandDescription, bool requireBlockerMetadata, CancellationToken cancellationToken)
    {
        var context = $"{commandDescription} in Target Repository '{repository.Path}'";
        var json = await RunQueryAsync(repository, request, context, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("$: expected an array");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var row = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var path = $"$[{row++}]";
                if (item.ValueKind != JsonValueKind.Object)
                    throw new JsonException($"{path}: expected an object");

                string? id = null;
                int? blockedByCount = null;
                var blockedBySeen = false;
                var blockedByIds = new List<string>();
                foreach (var property in item.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "id":
                            if (id is not null) throw new JsonException($"{path}.id: duplicate property");
                            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                                throw new JsonException($"{path}.id: expected a nonblank string");
                            id = property.Value.GetString();
                            break;
                        case "blocked_by_count":
                            if (blockedByCount is not null) throw new JsonException($"{path}.blocked_by_count: duplicate property");
                            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var count) || count < 0)
                                throw new JsonException($"{path}.blocked_by_count: expected a nonnegative integer");
                            blockedByCount = count;
                            break;
                        case "blocked_by":
                            if (blockedBySeen) throw new JsonException($"{path}.blocked_by: duplicate property");
                            blockedBySeen = true;
                            if (property.Value.ValueKind != JsonValueKind.Array)
                                throw new JsonException($"{path}.blocked_by: expected a string array");
                            foreach (var blocker in property.Value.EnumerateArray())
                            {
                                if (blocker.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(blocker.GetString()))
                                    throw new JsonException($"{path}.blocked_by: expected a string array");
                                blockedByIds.Add(blocker.GetString()!);
                            }
                            break;
                    }
                }

                if (id is null) throw new JsonException($"{path}.id: required property is missing");
                if (requireBlockerMetadata && (blockedByCount is null || !blockedBySeen))
                    throw new JsonException($"{path}: blocked rows must include blocked_by_count and blocked_by metadata");
                if (requireBlockerMetadata && blockedByCount > 0 && blockedByIds.Count == 0)
                    throw new JsonException($"{path}.blocked_by: expected blocker IDs when blocked_by_count is positive");
                if (!ids.Add(id)) throw new JsonException($"{path}.id: duplicate ID '{id}'");
            }

            return ids.ToFrozenSet(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd JSON: {exception.Message}");
        }
    }

    private async Task<string> RunQueryAsync(ValidatedTargetRepository repository, ProcessRequest request, string context,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureMutationFreeBackend(repository);
            var result = await processRunner.RunAsync(request, cancellationToken);
            if (result.ExitCode != 0)
                throw new ToolExecutionException($"exited {result.ExitCode}; verify bd compatibility and initialized backend (no bootstrap is performed)", result.StandardError);
            return result.StandardOutput;
        }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{context}: {exception.Message}", exception.StandardError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ToolExecutionException($"{context}: cancelled");
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd metadata: {exception.Message}");
        }
    }

    internal static void EnsureMutationFreeBackend(ValidatedTargetRepository repository)
    {
        // bd 1.3.1 creates persistent gate files before opening storage, even
        // under --readonly. Inspect only a prepared embedded backend.
        var beadsDirectory = Path.Combine(repository.Path, ".beads");
        if (!Directory.Exists(beadsDirectory)) return;
        var envPath = Path.Combine(beadsDirectory, ".env");
        if (File.Exists(envPath))
        {
            foreach (var line in File.ReadLines(envPath))
            {
                var assignment = line.Trim();
                if (assignment.StartsWith("export ", StringComparison.Ordinal)) assignment = assignment[7..].TrimStart();
                var separator = assignment.IndexOf('=');
                if (separator >= 0 && BeadsCommand.ScrubbedVariables.Contains(assignment[..separator].Trim(), StringComparer.Ordinal))
                    throw new ToolExecutionException("target .beads/.env contains routing or output-file overrides unsupported for mutation-free inspection");
            }
        }

        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(beadsDirectory, "metadata.json")));
        var metadataRoot = metadata.RootElement;
        if (metadataRoot.ValueKind != JsonValueKind.Object)
            throw new JsonException("metadata root must be an object");

        string? doltMode = null;
        if (metadataRoot.TryGetProperty("dolt_mode", out var mode))
        {
            if (mode.ValueKind != JsonValueKind.String)
                throw new JsonException("metadata.dolt_mode must be a string");
            doltMode = mode.GetString();
        }

        if (File.Exists(Path.Combine(beadsDirectory, "redirect")) || doltMode is not null && doltMode != "embedded")
            throw new ToolExecutionException("this bd backend layout is unsupported for mutation-free inspection; a local embedded tracker is required");
        if (!Directory.Exists(Path.Combine(beadsDirectory, "embeddeddolt")) ||
            !File.Exists(Path.Combine(repository.Path, ".beads.gate.lock")) ||
            !File.Exists(Path.Combine(beadsDirectory, "embeddeddolt.gate.lock")))
            throw new ToolExecutionException("embedded bd backend is not prepared for mutation-free inspection (missing gate-lock files or database); complete tracker setup with bd outside dry-run");
    }

    private static FrozenSet<string> ParseReadiness(string json, BeadsInventory inventory, string context,
        Func<BeadSnapshot, bool> queryPredicate)
    {
        try
        {
            var response = Parse(json);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bead in response.ById.Values.OrderBy(bead => bead.Id, StringComparer.Ordinal))
            {
                var path = $"readiness row '{bead.Id}'";
                if (!inventory.ById.TryGetValue(bead.Id, out var authoritative))
                    throw new JsonException($"{path}: ID is absent from the complete inventory");
                if (bead.Status != authoritative.Status || bead.IssueType != authoritative.IssueType || bead.Assignee != authoritative.Assignee ||
                    bead.Priority != authoritative.Priority || bead.CreatedAt != authoritative.CreatedAt ||
                    !PolicyLabels(bead.Labels).SetEquals(PolicyLabels(authoritative.Labels)))
                    throw new JsonException($"{path}: readiness fields disagree with the complete inventory");
                if (!queryPredicate(bead))
                    throw new JsonException($"{path}: row does not match the requested readiness query");
                if (!ids.Add(bead.Id)) throw new JsonException($"{path}: duplicate ID '{bead.Id}'");
            }
            return ids.ToFrozenSet(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd JSON: {exception.Message}");
        }
    }

    private static HashSet<string> PolicyLabels(IReadOnlyList<string> labels) => labels
        .Where(label => label is "planned" or "product-decision-required" or "needs-human" || label.StartsWith("stage:", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);

    internal static BeadsInventory Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("$: expected an array");
        }
        var beads = new List<BeadSnapshot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var row = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var path = $"$[{row++}]";
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException($"{path}: expected an object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is "id" or "status" or "issue_type" or "priority" or "created_at" or "assignee" or "labels"
                    && !seen.Add(property.Name)) throw new JsonException($"{path}.{property.Name}: duplicate property");
            }
            string RequiredString(string name)
            {
                if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new JsonException($"{path}.{name}: expected a nonblank string");
                return value.GetString()!;
            }
            var id = RequiredString("id");
            if (!ids.Add(id)) throw new JsonException($"{path}.id: duplicate ID '{id}'");
            var status = RequiredString("status");
            var type = RequiredString("issue_type");
            if (!item.TryGetProperty("priority", out var priority) || priority.ValueKind != JsonValueKind.Number || !priority.TryGetInt32(out var number) || number is < 0 or > 4)
                throw new JsonException($"{path}.priority: expected an integer in 0..4");
            _ = RequiredString("created_at");
            if (!item.GetProperty("created_at").TryGetDateTimeOffset(out var createdAt))
                throw new JsonException($"{path}.created_at: expected an ISO timestamp");
            string? assignee = null;
            if (item.TryGetProperty("assignee", out var actor) && actor.ValueKind != JsonValueKind.Null)
            {
                if (actor.ValueKind != JsonValueKind.String) throw new JsonException($"{path}.assignee: expected string or null");
                assignee = actor.GetString();
                if (assignee == "") assignee = null;
            }
            var labels = new List<string>();
            if (item.TryGetProperty("labels", out var labelArray))
            {
                if (labelArray.ValueKind != JsonValueKind.Array) throw new JsonException($"{path}.labels: expected string array");
                foreach (var label in labelArray.EnumerateArray())
                {
                    if (label.ValueKind != JsonValueKind.String) throw new JsonException($"{path}.labels: expected string array");
                    labels.Add(label.GetString()!);
                }
            }
            beads.Add(new BeadSnapshot(id, status, type, number, createdAt, assignee, labels.AsReadOnly(), item.Clone()));
        }
        return new BeadsInventory(beads);
    }

    internal async Task<IReadOnlyList<Execution.BeadDependency>> ReadUpDependenciesAsync(
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken)
    {
        var context = $"bd --readonly --sandbox dep list '{beadId}' --direction up in Target Repository '{repository.Path}'";
        var json = await RunQueryAsync(repository, BeadsCommand.CreateUpDependencies(repository.Path, beadId), context, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("$: expected an array");

            return ParseUpDependencies(document.RootElement, beadId);
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd dependency JSON: {exception.Message}");
        }
    }

    internal async Task<IReadOnlyList<BeadCommentSnapshot>> ReadCommentsAsync(
        ValidatedTargetRepository repository,
        string beadId,
        CancellationToken cancellationToken)
    {
        var context = $"bd --readonly --sandbox comments '{beadId}' --json in Target Repository '{repository.Path}'";
        var json = await RunQueryAsync(repository, BeadsCommand.CreateComments(repository.Path, beadId), context, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseComments(document.RootElement, beadId);
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd JSON: {exception.Message}");
        }
    }

    internal static IReadOnlyList<BeadCommentSnapshot> ParseComments(JsonElement root, string beadId)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("$: expected an array");
        var comments = new List<(int Index, BeadCommentSnapshot Comment)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var row = 0;
        foreach (var item in root.EnumerateArray())
        {
            var path = $"$[{row}]";
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException($"{path}: expected an object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if ((property.Name is "id" or "issue_id" or "text" or "created_at") && !seen.Add(property.Name))
                    throw new JsonException($"{path}.{property.Name}: duplicate property");
            }

            string RequiredString(string name)
            {
                if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new JsonException($"{path}.{name}: expected a nonblank string");
                return value.GetString()!;
            }

            var id = RequiredString("id");
            if (!ids.Add(id)) throw new JsonException($"{path}.id: duplicate comment ID '{id}'");
            if (!string.Equals(RequiredString("issue_id"), beadId, StringComparison.Ordinal))
                throw new JsonException($"{path}.issue_id: comment does not belong to Bead '{beadId}'");
            var text = RequiredString("text");
            var createdAtText = RequiredString("created_at");
            if (!DateTimeOffset.TryParse(createdAtText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var createdAt))
                throw new JsonException($"{path}.created_at: expected an ISO timestamp");
            comments.Add((row++, new BeadCommentSnapshot(id, text, createdAt)));
        }

        return comments.OrderBy(entry => entry.Comment.CreatedAt).ThenBy(entry => entry.Index)
            .Select(entry => entry.Comment).ToArray();
    }

    internal static IReadOnlyList<Execution.BeadDependency> ParseUpDependencies(JsonElement root, string beadId)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("$: expected an array");
        var dependencies = new List<Execution.BeadDependency>();
        var row = 0;
        foreach (var item in root.EnumerateArray())
        {
            var path = $"$[{row++}]";
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException($"{path}: expected an object");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is not ("issue_id" or "depends_on_id" or "type" or "id" or "dependency_type")) continue;
                if (!values.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()! : throw new JsonException($"{path}.{property.Name}: expected a string")))
                    throw new JsonException($"{path}.{property.Name}: duplicate property");
            }

            if (values.TryGetValue("issue_id", out var issueId))
            {
                if (!values.TryGetValue("depends_on_id", out var dependsOnId) || !values.TryGetValue("type", out var type))
                    throw new JsonException($"{path}: edge row must include depends_on_id and type");
                dependencies.Add(new Execution.BeadDependency(issueId, dependsOnId, type));
                continue;
            }

            if (!values.TryGetValue("id", out var dependentId) || !values.TryGetValue("dependency_type", out var dependencyType))
                throw new JsonException($"{path}: bd dep list row must include id and dependency_type");
            dependencies.Add(new Execution.BeadDependency(dependentId, beadId, dependencyType));
        }
        if (dependencies.Any(dependency => string.IsNullOrWhiteSpace(dependency.IssueId)
            || string.IsNullOrWhiteSpace(dependency.DependsOnId) || string.IsNullOrWhiteSpace(dependency.Type)))
            throw new JsonException("$: dependency fields must be nonblank strings");
        return dependencies.AsReadOnly();
    }

}
