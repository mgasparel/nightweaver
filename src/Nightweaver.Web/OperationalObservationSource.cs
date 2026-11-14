using System.Collections.Frozen;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Telemetry;

namespace Nightweaver.Web;

internal sealed class OperationalObservationSource(
    ProjectConfig project, HostPaths paths,
    IProcessRunner runner, TrackerBridge tracker, TimeProvider clock,
    CatchUpCheckpointStore checkpoint, FactoryTelemetryReader runtime,
    IBeadWorkspaceRuntimeObserver runtimeObserver, ObservationMemoryBudget memory) : IOperationalObservationSource
{
    private static readonly JsonSerializerOptions RuntimeJson = ObservationJson.Create(snakeCase: true);
    private static readonly string[] InventorySource = ["inventory"];
    private readonly Dictionary<string, ObservationSourceState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, History> histories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceCycle> workspaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> commentsReadThisSweep = new(StringComparer.Ordinal);
    private BeadSnapshot[]? attentionBeads;
    private Dictionary<string, BeadPresentation?> attentionPresentations = new(StringComparer.Ordinal);
    private ObservationSourceState? attentionInventoryState;
    private DateTimeOffset attentionStarted;
    private int workspaceCursor;
    private int commentCursor;
    private double sweepNativeStarted;
    private bool preferAttention = true;
    private int commentsThisSweep;
    private bool resumeDetails;
    private BeadsInventory? retainedInventory;
    private FrozenSet<string>? retainedReady;
    private FrozenSet<string>? retainedBlocked;
    private DateTimeOffset inventoryGeneratedAt;
    private ObservationSourceState? retainedInventoryState;
    private bool readyFirst;
    private double nativeMilliseconds;
    internal long CommandCount { get; private set; }
    internal double NativeMilliseconds => nativeMilliseconds;

    private sealed record History(IReadOnlyList<BeadsCommentSnapshot> Comments, DateTimeOffset ReadAt, int Bytes);
    private sealed class WorkspaceCycle(BeadSnapshot initial, DateTimeOffset started)
    {
        internal IReadOnlyList<BeadWorkspaceRelation> Relations { get; set; } = [];
        internal BeadPresentation? Presentation { get; set; }
        internal BeadSnapshot Initial { get; } = initial;
        internal DateTimeOffset Started { get; } = started;
        internal int Step { get; set; }
        internal BeadSnapshot? Detail { get; set; }
        internal IReadOnlyList<BeadDependency> Incoming { get; set; } = [];
        internal IReadOnlyList<BeadDependency> Outgoing { get; set; } = [];
        internal IReadOnlyList<BeadsCommentSnapshot> Comments { get; set; } = [];
        internal string IncomingState { get; set; } = "complete";
        internal string OutgoingState { get; set; } = "complete";
        internal BeadWorkspaceEnvironment? Environment { get; set; }
        internal Dictionary<string, ObservationSourceState> Sources { get; } = new(StringComparer.Ordinal);
    }

    private sealed class BoundedRunner(IProcessRunner inner, TrackerAccessGate? gate, Action<double> completed, TimeProvider clock) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (gate is not null) await gate.WaitObserverAsync(cancellationToken);
            var start = clock.GetTimestamp();
            try
            {
                var cap = request.Arguments.Contains("dep") || request.Arguments.Contains("show") || request.Arguments.Contains("comments")
                    ? 2 * 1024 * 1024 : 8 * 1024 * 1024;
                if (request.FileName == "bd" && !request.Arguments.Contains("dep"))
                {
                    var arguments = request.Arguments.ToArray();
                    var limit = Array.IndexOf(arguments, "--limit");
                    if (limit >= 0) { arguments[limit + 1] = "10001"; request = request with { Arguments = arguments }; }
                }
                var result = await inner.RunAsync(request with { Timeout = TimeSpan.FromSeconds(2), MaximumOutputBytes = cap }, cancellationToken);
                if (System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput) > cap)
                    throw new ToolExecutionException("observer output exceeded read limit");
                using var json = result.ExitCode == 0 && request.FileName == "bd" ? JsonDocument.Parse(result.StandardOutput) : null;
                if (json?.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 10000)
                    throw new ToolExecutionException("observer row count exceeded read limit");
                return result;
            }
            finally { completed(clock.GetElapsedTime(start).TotalMilliseconds); gate?.Release(); }
        }
    }

    private BoundedRunner CreateRunner(bool gated = true) => new(runner, gated ? tracker.ObservationGate : null,
        elapsed => { if (gated) { CommandCount++; nativeMilliseconds += elapsed; } }, clock);
    private BeadsReader CreateReader() => new(CreateRunner());

    private bool HasBudget => nativeMilliseconds - sweepNativeStarted < 4000;

    private async Task<T?> ReadAsync<T>(string id, Func<Task<T>> read, CancellationToken token) where T : class
    {
        var attempt = clock.GetUtcNow();
        try
        {
            var result = await read();
            states[id] = new("fresh", clock.GetUtcNow(), attempt, null);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            states.TryGetValue(id, out var previous);
            states[id] = new(previous?.ObservedAt is null ? "unavailable" : "stale", previous?.ObservedAt, attempt, Error(exception));
            return null;
        }
    }

    private static bool IsSourceFailure(Exception exception) => exception is ToolExecutionException or TargetRepositoryException
        or ProjectConfigurationException or JsonException or IOException or UnauthorizedAccessException or BeadWorkspaceException
        or InvalidDataException or ArgumentException or NotSupportedException;

    private static ObservationError Error(Exception exception) => exception switch
    {
        BeadWorkspaceException { StatusCode: 404 } => new("resource-missing", "The Bead no longer exists.", false),
        BeadWorkspaceException { StatusCode: 409 } => new("source-conflict", "The Bead changed during observation. Retrying shortly."),
        _ when exception.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase) => new("source-limit", "Source exceeded the supported observation limits."),
        _ when exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) => new("source-timeout", "Source did not respond within the observation timeout."),
        _ => new("source-failed", "Source could not be observed. Retrying shortly.")
    };

    private ObservationSample Sample(object? data, DateTimeOffset attempted, params string[] ids)
    {
        var sources = ids.Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => states.TryGetValue(id, out var state)
            ? state : new ObservationSourceState("unavailable", null, attempted, new("budget-exceeded", "Source is awaiting its bounded observation turn.")), StringComparer.Ordinal);
        var error = sources.Values.FirstOrDefault(state => state.Error is not null)?.Error;
        return new(data, attempted, sources.Values.Min(state => state.ObservedAt), sources, error);
    }

    public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
    {
        sweepNativeStarted = nativeMilliseconds;
        commentsReadThisSweep.Clear();
        commentsThisSweep = 0;
        var started = clock.GetUtcNow();
        var generatedAt = DateTimeOffset.FromUnixTimeSeconds(started.ToUnixTimeSeconds());
        var result = new Dictionary<string, ObservationSample>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            foreach (var history in histories.Values) memory.Release(history);
            histories.Clear(); states.Clear(); workspaces.Clear(); attentionBeads = null;
            attentionPresentations.Clear(); retainedInventory = null; resumeDetails = false;
            return result;
        }
        if (!keys.Any(key => key.Kind == "attention"))
        {
            attentionBeads = null;
            attentionPresentations.Clear();
        }
        ValidatedTargetRepository? repository;
        try { repository = await new TargetRepositoryValidator(CreateRunner()).ValidateAsync(project.TargetRepository, project.ConfigFilePath, token); }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            states["inventory"] = new(states.GetValueOrDefault("inventory")?.ObservedAt is null ? "unavailable" : "stale",
                states.GetValueOrDefault("inventory")?.ObservedAt, started, Error(exception));
            repository = null;
        }
        if (repository is null)
        {
            foreach (var key in keys) result[key.Value] = Sample(null, started, "inventory");
            return result;
        }
        var reader = CreateReader();
        BeadsInventory? inventory;
        FrozenSet<string>? ready, blocked;
        if (resumeDetails && retainedInventory is not null)
        {
            // When the previous core exhausted the sweep, give pending detail work
            // one bounded turn before starting another core. Source age stays visible.
            inventory = retainedInventory; ready = retainedReady; blocked = retainedBlocked;
            generatedAt = inventoryGeneratedAt;
            states["inventory"] = retainedInventoryState!;
        }
        else
        {
            inventory = await ReadAsync("inventory", () => reader.ReadSnapshotAsync(repository, token), token);
            ready = null; blocked = null;
            for (var index = 0; index < 2 && HasBudget; index++)
            {
                if ((index == 0) == readyFirst) ready = await ReadAsync("ready", () => reader.ReadExplorerReadinessAsync(repository, token), token);
                else blocked = await ReadAsync("blocked", () => reader.ReadExplorerBlockedAsync(repository, token), token);
            }
            readyFirst = !readyFirst;
            retainedInventory = inventory; retainedReady = ready; retainedBlocked = blocked;
            inventoryGeneratedAt = generatedAt;
            retainedInventoryState = states["inventory"];
        }
        var inventoryState = states["inventory"];
        var detailCommandsBefore = CommandCount;
        var checkpointAttempt = clock.GetUtcNow();
        DateTimeOffset? caughtUp = null;
        try
        {
            caughtUp = checkpoint.ReadLastCaughtUpAt(strict: true);
            states["checkpoint"] = new("fresh", checkpointAttempt, checkpointAttempt, null);
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        { states["checkpoint"] = new("unavailable", null, checkpointAttempt, Error(exception)); }

        BeadExplorerItem[]? explorerItems = null;
        foreach (var key in keys.Where(key => key.Kind is "explorer" or "digest"))
        {
            try
            {
                if (key.Kind == "digest")
                    result[key.Value] = Sample(inventory is null ? null : CatchUpDigestProjector.Project(inventory, key.Argument!, caughtUp,
                        generatedAt, project.Limits.MaxRepairCycles), started, "inventory", "checkpoint");
                else
                {
                    var ids = key.Argument == "ready" ? new[] { "inventory", "ready" }
                        : key.Argument == "blocked" ? ["inventory", "blocked"] : ["inventory"];
                    // A skipped due source is a failed projection, never a fresh blend.
                    if (key.Argument == "ready" && ready is null || key.Argument == "blocked" && blocked is null)
                    {
                        var id = key.Argument!;
                        states[id] = new("stale", states.GetValueOrDefault(id)?.ObservedAt, started,
                            states.GetValueOrDefault(id)?.Error ?? new("budget-exceeded", "Source is awaiting its bounded observation turn."));
                    }
                    var items = explorerItems ??= inventory?.ById.Values.Select(bead => BeadExplorerItem.FromSnapshot(bead) with
                    { Presentation = BeadPresenter.Project(bead, project.Limits.MaxRepairCycles, inventory) }).ToArray();
                    var filtered = items?.Where(item => key.Argument switch
                    {
                        "active" => item.Status == "in_progress", "closed" => item.Status == "closed",
                        "ready" => item.Status == "open" && ready?.Contains(item.Id) == true,
                        "blocked" => item.Status != "closed" && (item.Status == "blocked" || blocked?.Contains(item.Id) == true),
                        _ => true
                    }).Where(item => string.IsNullOrEmpty(key.Query) || item.Id.Contains(key.Query, StringComparison.OrdinalIgnoreCase)
                        || item.Title.Contains(key.Query, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Priority)
                        .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
                    result[key.Value] = Sample(filtered, started, ids);
                }
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            { result[key.Value] = Sample(null, started, "inventory") with { Error = Error(exception) }; }
        }

        var selected = keys.Where(key => key.Kind == "workspace").ToArray();
        foreach (var id in workspaces.Keys.Where(id => !selected.Any(key => key.Argument == id)).ToArray()) workspaces.Remove(id);
        async Task ReadSelectedAsync()
        {
            if (selected.Length > 0 && inventory is not null && HasBudget)
            {
                var key = selected[workspaceCursor++ % selected.Length];
                var sample = await ReadWorkspaceAsync(repository, reader, inventory, key.Argument!, token);
                if (sample is not null) result[key.Value] = sample;
            }
            else if (inventory is null)
                foreach (var key in selected) result[key.Value] = Sample(null, started, "inventory");
        }
        // Alternate only on turns with detail capacity. Otherwise a slow core can
        // phase-lock the priority and continuously selected Workspaces starve history.
        var attentionFirst = HasBudget && keys.Any(key => key.Kind == "attention") && (preferAttention = !preferAttention);
        if (!attentionFirst) await ReadSelectedAsync();

        if (keys.Any(key => key.Kind == "attention") && inventory is not null)
            try { await ReadAttentionAsync(repository, reader, inventory, inventoryState, result,
                selected.Select(key => key.Argument!).ToHashSet(StringComparer.Ordinal), token); }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                result["attention"] = Sample(null, started, "inventory") with { Error = Error(exception) };
                attentionBeads = null;
                attentionPresentations.Clear();
            }
        else if (inventory is null && keys.Any(key => key.Kind == "attention")) result["attention"] = Sample(null, started, "inventory");
        if (attentionFirst) await ReadSelectedAsync();
        resumeDetails = !HasBudget && CommandCount == detailCommandsBefore
            && (selected.Length > 0 || keys.Any(key => key.Kind == "attention"));
        TrimSources(selected.Select(key => key.Argument!).ToHashSet(StringComparer.Ordinal));
        return result;
    }

    private async Task<ObservationSample?> ReadWorkspaceAsync(ValidatedTargetRepository repository, BeadsReader reader,
        BeadsInventory inventory, string id, CancellationToken token)
    {
        var started = clock.GetUtcNow();
        if (!inventory.ById.TryGetValue(id, out var initial))
        {
            workspaces.Remove(id);
            return Sample(null, started, "inventory") with { Error = new("resource-missing", "The Bead no longer exists.", false) };
        }
        if (!workspaces.TryGetValue(id, out var cycle)) workspaces[id] = cycle = new(initial, started);
        try
        {
            while (HasBudget)
            {
                string sourceId;
                switch (cycle.Step)
                {
                    case 0:
                        sourceId = "detail:" + id;
                        cycle.Detail = await ReadAsync(sourceId, () => reader.ReadDetailAsync(repository, id, token), token);
                        break;
                    case 1:
                        sourceId = "relations-up:" + id;
                        cycle.Incoming = await ReadAsync(sourceId, () => reader.ReadUpDependenciesAsync(repository, id, token), token) ?? [];
                        cycle.IncomingState = states[sourceId].Error is null ? "complete" : "unavailable";
                        if (cycle.Incoming.Any(edge => edge.DependsOnId != id))
                        {
                            cycle.Incoming = []; cycle.IncomingState = "malformed";
                            states[sourceId] = states[sourceId] with { Health = "unavailable", Error = new("source-failed", "Relation identity could not be validated.") };
                        }
                        break;
                    case 2:
                        sourceId = "relations-down:" + id;
                        cycle.Outgoing = await ReadAsync(sourceId, () => reader.ReadDownDependenciesAsync(repository, id, token), token) ?? [];
                        cycle.OutgoingState = states[sourceId].Error is null ? "complete" : "unavailable";
                        if (cycle.Outgoing.Any(edge => edge.IssueId != id))
                        {
                            cycle.Outgoing = []; cycle.OutgoingState = "malformed";
                            states[sourceId] = states[sourceId] with { Health = "unavailable", Error = new("source-failed", "Relation identity could not be validated.") };
                        }
                        break;
                    case 3:
                        sourceId = "comments:" + id;
                        if (histories.TryGetValue(id, out var history) && commentsReadThisSweep.Contains(id)
                            && states[sourceId].Error is null)
                            cycle.Comments = history.Comments;
                        else
                        {
                            if (commentsThisSweep >= 4)
                                return new(null, cycle.Started, null, cycle.Sources.ToDictionary(), Pending: true);
                            commentsThisSweep++;
                            var comments = await ReadAsync(sourceId, () => reader.ReadDetailCommentsAsync(repository, id, token), token);
                            if (comments is not null) { StoreHistory(id, comments); cycle.Comments = comments; }
                        }
                        break;
                    case 4:
                        sourceId = "inventory";
                        var final = await ReadAsync(sourceId, () => reader.ReadSnapshotAsync(repository, token), token);
                        if (final is not null)
                        {

                            if (!final.ById.TryGetValue(id, out var last) || cycle.Detail is null
                                || !AttentionMutationIdentity.Capture(cycle.Initial).SameObservedIdentity(AttentionMutationIdentity.Capture(last))
                                || !AttentionMutationIdentity.Capture(cycle.Initial).SameObservedIdentity(AttentionMutationIdentity.Capture(cycle.Detail)))
                                throw new BeadWorkspaceException(409, "Bead changed", "Identity changed");
                            cycle.Relations = BeadWorkspaceProjector.ProjectRelations(cycle.Incoming, cycle.Outgoing, final.ById);
                            cycle.Presentation = BeadPresenter.Project(cycle.Detail!, project.Limits.MaxRepairCycles, final);
                        }
                        break;
                    case 5:
                        sourceId = "environment:" + id;
                        var bounded = new BoundedRunner(runner, tracker.ObservationGate, elapsed => { CommandCount++; nativeMilliseconds += elapsed; }, clock);
                        var environment = new BeadWorkspaceEnvironmentObserver(paths, project, new GitWorktreeReader(bounded), bounded, runtimeObserver, clock);
                        async Task<BeadWorkspaceEnvironment> ObserveEnvironmentAsync()
                        {
                            var remaining = TimeSpan.FromMilliseconds(4000 - (nativeMilliseconds - sweepNativeStarted));
                            using var deadline = new CancellationTokenSource(remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), clock);
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                            try { return await environment.ObserveAsync(repository, id, linked.Token); }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            { throw new ToolExecutionException("Environment observation timed out within its source budget"); }
                        }
                        cycle.Environment = await ReadAsync(sourceId, ObserveEnvironmentAsync, token);
                        if (cycle.Environment?.State is "unavailable" or "unsafe")
                            states[sourceId] = states[sourceId] with { Health = "unavailable", Error = new("source-failed", "Environment identity or access could not be validated.") };
                        break;
                    default:
                        workspaces.Remove(id);
                        var sample = WorkspaceSample(cycle, id);
                        return sample;
                }
                cycle.Sources[sourceId] = states[sourceId];
                if (states[sourceId].Error is not null && !sourceId.StartsWith("relations-", StringComparison.Ordinal)
                    && !(sourceId.StartsWith("environment:", StringComparison.Ordinal) && cycle.Environment is not null))
                {
                    workspaces.Remove(id);
                    return new(null, cycle.Started, null, cycle.Sources.ToDictionary(), states[sourceId].Error);
                }
                cycle.Step++;
            }
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            workspaces.Remove(id);
            return new(null, cycle.Started, null, cycle.Sources.ToDictionary(), Error(exception));
        }
        return new(null, cycle.Started, null, cycle.Sources.ToDictionary(), Pending: true);
    }

    private ObservationSample WorkspaceSample(WorkspaceCycle cycle, string id)
    {
        var detail = cycle.Detail!;
        var attention = AttentionDeskProjector.IsPaused(detail) && !AttentionDeskProjector.IsTerminal(detail)
            ? AttentionDeskProjector.ProjectWithSelection(detail, cycle.Comments, project.Limits.MaxRepairCycles) : null;
        var presentation = cycle.Presentation;
        var data = BeadWorkspaceProjector.Project(detail, cycle.Comments, attention, cycle.Environment!) with
        {
            Presentation = presentation, Attention = attention is null ? null : attention.Item with { Presentation = presentation },
            Relations = cycle.Relations,
            RelationObservation = new(cycle.IncomingState, cycle.OutgoingState)
        };
        var error = cycle.Sources.Values.FirstOrDefault(state => state.Error is not null)?.Error;
        return new(data, cycle.Started, clock.GetUtcNow(), cycle.Sources.ToDictionary(), error, Partial: error is not null);
    }

    private async Task ReadAttentionAsync(ValidatedTargetRepository repository, BeadsReader reader, BeadsInventory inventory,
        ObservationSourceState inventoryState, Dictionary<string, ObservationSample> result, HashSet<string> selected, CancellationToken token)
    {
        if (attentionBeads is null)
        {
            attentionBeads = inventory.ById.Values.Where(AttentionDeskProjector.IsPaused).Where(bead => !AttentionDeskProjector.IsTerminal(bead))
                .OrderBy(bead => bead.Id, StringComparer.Ordinal).ToArray();
            attentionStarted = inventoryState.LastAttemptAt!.Value;
            attentionInventoryState = inventoryState;
            attentionPresentations = attentionBeads.Take(64).ToDictionary(bead => bead.Id,
                bead => (BeadPresentation?)BeadPresenter.Project(bead, project.Limits.MaxRepairCycles, inventory), StringComparer.Ordinal);
        }
        var paused = attentionBeads;
        if (paused.Length > 64)
        {
            result["attention"] = Sample(null, attentionStarted, "inventory") with { Error = new("source-limit", "Attention requires more than 64 comment histories.") };
            attentionBeads = null;
            attentionPresentations.Clear();
            return;
        }
        var due = paused.Where(bead => !histories.TryGetValue(bead.Id, out var history) || history.ReadAt < attentionStarted).ToArray();
        var allowance = Math.Min(4 - commentsThisSweep, due.Length);
        for (var count = 0; count < allowance && HasBudget; count++)
        {
            var bead = due[commentCursor++ % due.Length];
            commentsThisSweep++;
            var comments = await ReadAsync("comments:" + bead.Id, () => selected.Contains(bead.Id)
                ? reader.ReadDetailCommentsAsync(repository, bead.Id, token)
                : reader.ReadAttentionCommentsAsync(repository, bead.Id, token), token);
            if (comments is null)
            {
                result["attention"] = Sample(null, attentionStarted, "inventory", "comments:" + bead.Id);
                // Keep successful histories in this bounded cohort. Restarting it on
                // every transient timeout can prevent a large cohort ever completing.
                return;
            }
            try { StoreHistory(bead.Id, comments); }
            catch (ToolExecutionException exception)
            {
                result["attention"] = Sample(null, attentionStarted, "inventory") with { Error = Error(exception) };
                attentionBeads = null;
                attentionPresentations.Clear();
                return;
            }
        }
        if (paused.Any(bead => !histories.TryGetValue(bead.Id, out var history) || history.ReadAt < attentionStarted)) return;
        var items = paused.Select(bead => AttentionDeskProjector.Project(bead, histories[bead.Id].Comments, project.Limits.MaxRepairCycles) with
        { Presentation = attentionPresentations[bead.Id] }).OrderBy(item => item.Priority)
            .ThenBy(item => item.PausedAt is null ? 1 : 0).ThenBy(item => item.PausedAt).ThenBy(item => item.BeadId, StringComparer.Ordinal).ToArray();
        var sample = Sample(items, attentionStarted, InventorySource.Concat(paused.Select(bead => "comments:" + bead.Id)).ToArray());
        var sources = sample.Sources.ToDictionary();
        sources["inventory"] = attentionInventoryState!;
        result["attention"] = sample with { Sources = sources, ObservedAt = attentionStarted };
        attentionBeads = null;
        attentionPresentations.Clear();
    }

    private void StoreHistory(string id, IReadOnlyList<BeadsCommentSnapshot> comments)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(comments).Length;
        if (!histories.ContainsKey(id) && histories.Count >= 64
            || histories.Where(pair => pair.Key != id).Sum(pair => pair.Value.Bytes) + bytes > 16 * 1024 * 1024)
            throw new ToolExecutionException("comment cache exceeded read limit");
        var history = new History(comments, clock.GetUtcNow(), bytes);
        var previous = histories.GetValueOrDefault(id);
        if (!memory.TryReplace(previous is null ? [] : [previous], [(history, bytes)]))
            throw new ToolExecutionException("shared observation cache exceeded read limit");
        histories[id] = history;
        commentsReadThisSweep.Add(id);
    }

    private void TrimSources(HashSet<string> selected)
    {
        var retained = selected.Union(attentionBeads?.Select(bead => bead.Id) ?? []
            , StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var id in histories.Keys.Where(id => !retained.Contains(id)).ToArray())
        { memory.Release(histories[id]); histories.Remove(id); }
        foreach (var id in states.Keys.Where(id => id.Contains(':') && !retained.Contains(id[(id.IndexOf(':') + 1)..])).ToArray()) states.Remove(id);
    }

    public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadRuntimeAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
    {
        // Runtime and file observations never wait for the tracker gate.
        var result = new Dictionary<string, ObservationSample>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var started = clock.GetUtcNow();
            try
            {
                object data;
                ObservationError? observationError = null;
                if (key.Kind == "factory")
                {
                    var read = runtime.Read();
                    if (read.Availability is FactoryTelemetryAvailability.Stale or FactoryTelemetryAvailability.Unavailable)
                        observationError = new("source-failed", "Runtime telemetry is stale or unavailable.");
                    data = JsonSerializer.SerializeToElement(new { availability = read.Availability.ToString().ToLowerInvariant(), reason = read.Reason, snapshot = read.Snapshot }, RuntimeJson);
                }
                else
                {
                    var repository = await new TargetRepositoryValidator(CreateRunner(false)).ValidateAsync(project.TargetRepository, project.ConfigFilePath, token);
                    var page = new CleanupStatusReader(paths, project, repository, clock).Read(key.Page, key.Argument == "all" ? null : key.Argument, key.Query);
                    if (page.Counts is null) observationError = new("source-failed", "Cleanup evidence is unavailable.");
                    data = page;
                }
                var id = key.Kind == "factory" ? "runtime" : "cleanup";
                result[key.Value] = new(data, started, clock.GetUtcNow(), new Dictionary<string, ObservationSourceState>
                { [id] = new(observationError is null ? "fresh" : "unavailable", observationError is null ? clock.GetUtcNow() : null, started, observationError) }, observationError);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                result[key.Value] = new(null, started, null, new Dictionary<string, ObservationSourceState>
                { [key.Kind == "factory" ? "runtime" : "cleanup"] = new("unavailable", null, started, Error(exception)) }, Error(exception));
            }
        }
        return result;
    }
}
