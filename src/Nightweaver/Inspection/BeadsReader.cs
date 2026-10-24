using System.Text.Json;

namespace Nightweaver.Inspection;

internal sealed class BeadsReader(IProcessRunner processRunner)
{
    internal async Task<BeadsInventory> ReadSnapshotAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        var context = $"bd --readonly --sandbox list inventory in Target Repository '{repository.Path}'";
        try
        {
            // bd 1.3.1 creates persistent gate files before opening storage, even
            // under --readonly. Inspect only the backend whose physical-root
            // layout has been verified; never bootstrap it or warm up a first read.
            var beadsDirectory = Path.Combine(repository.Path, ".beads");
            if (Directory.Exists(beadsDirectory))
            {
                // bd loads target .env after discovery. Do not let selectors or
                // output-file overrides reintroduce a scrubbed ambient setting.
                var envPath = Path.Combine(beadsDirectory, ".env");
                if (File.Exists(envPath))
                {
                    foreach (var line in File.ReadLines(envPath))
                    {
                        var assignment = line.Trim();
                        if (assignment.StartsWith("export ", StringComparison.Ordinal)) assignment = assignment[7..].TrimStart();
                        var separator = assignment.IndexOf('=');
                        if (separator >= 0 && BeadsCommand.ScrubbedVariables.Contains(assignment[..separator].Trim(), StringComparer.Ordinal))
                        {
                            throw new ToolExecutionException("target .beads/.env contains routing or output-file overrides unsupported for mutation-free inspection");
                        }
                    }
                }
                using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(beadsDirectory, "metadata.json")));
                if (File.Exists(Path.Combine(beadsDirectory, "redirect")) ||
                    (metadata.RootElement.TryGetProperty("dolt_mode", out var mode) && mode.GetString() != "embedded"))
                {
                    throw new ToolExecutionException("this bd backend layout is unsupported for mutation-free inspection; a local embedded tracker is required");
                }
                if (!Directory.Exists(Path.Combine(beadsDirectory, "embeddeddolt")) ||
                    !File.Exists(Path.Combine(repository.Path, ".beads.gate.lock")) ||
                    !File.Exists(Path.Combine(beadsDirectory, "embeddeddolt.gate.lock")))
                {
                    throw new ToolExecutionException("embedded bd backend is not prepared for mutation-free inspection (missing gate-lock files or database); complete tracker setup with bd outside dry-run");
                }
            }
            var result = await processRunner.RunAsync(BeadsCommand.Create(repository.Path), cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new ToolExecutionException($"exited {result.ExitCode}; verify bd compatibility and initialized backend (no bootstrap is performed)", result.StandardError);
            }
            return Parse(result.StandardOutput);
        }
        catch (ToolExecutionException exception)
        {
            throw new ToolExecutionException($"{context}: {exception.Message}", exception.StandardError);
        }
        catch (OperationCanceledException)
        {
            throw new ToolExecutionException($"{context}: cancelled");
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException($"{context}: incompatible or malformed bd JSON: {exception.Message}");
        }
    }

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
}
