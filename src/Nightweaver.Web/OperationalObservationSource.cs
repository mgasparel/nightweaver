using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;
using Nightweaver.Telemetry;

namespace Nightweaver.Web;

internal sealed partial class OperationalObservationSource(
    ProjectConfig project, HostPaths paths,
    IProcessRunner runner, TrackerBridge tracker, TimeProvider clock,
    CatchUpCheckpointStore checkpoint, FactoryTelemetryReader runtime,
    IBeadWorkspaceRuntimeObserver runtimeObserver, ObservationMemoryBudget memory) : IOperationalObservationSource, IScheduledTrackerObservationSource, IProgressiveTrackerObservationSource
{
    private Action<IReadOnlyDictionary<string, ObservationSample>>? publishBase;
    private SharedBaseInventory? baseInventory;
    private object? baseContentIdentity;
    private readonly object baseReservation = new();
    private ulong baseRevision;

    public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadProgressiveAsync(IReadOnlyList<OperationKey> keys,
        Action<string, ObservationSourceState> revalidated,
        Action<IReadOnlyDictionary<string, ObservationSample>> publish, CancellationToken token)
    {
        publishBase = publish;
        try { return await ReadScheduledAsync(keys, revalidated, token); }
        finally { publishBase = null; }
    }

    private ObservationSample CurrentSample(ObservationSample sample) => scheduled
        && (sample.Error is null || sample.ConfirmedAbsence || sample.Partial)
        && sample.Sources.Any(pair => (!sample.Partial || pair.Key == "inventory" || pair.Value.Error is null)
            && !schedule.IsCurrent(pair.Key, pair.Value.SourceGeneration))
            ? sample with { Pending = true, ConfirmedAbsence = false, Error = null } : sample;

    private void ProjectBases(IReadOnlyList<OperationKey> keys, BeadsInventory? inventory,
        FrozenSet<string>? ready, FrozenSet<string>? blocked, DateTimeOffset started,
        Dictionary<string, ObservationSample> result, bool inventoryOnly = false)
    {
        var selected = keys.Where(key => key.Kind is "base-explorer" or "base-workspace" or "base-attention")
            .Where(key => !inventoryOnly || key.Kind == "base-workspace" || key.Argument is not ("ready" or "blocked")).ToArray();
        if (selected.Length == 0 && !keys.Any(key => key.Kind is "section" or "base-digest" or "digest-merges")) return;
        ObservationError? projectionError = null;
        if (inventory is not null && !ReferenceEquals(baseContentIdentity, states["inventory"].ContentIdentity))
        {
            try
            {
                var next = new SharedBaseInventory(inventory, project.Limits.MaxRepairCycles,
                    checked(baseRevision + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                // Account for rows, exact-ID index and ordered references in the
                // same host budget as raw sources and resource envelopes.
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next.Ordered, ObservationJson.Options).Length
                    + next.Ordered.Sum(row => row.Id.Length * 2 + 256);
                if (!memory.TryReplace([], [(baseReservation, bytes)]))
                    throw new ToolExecutionException("base cache exceeded shared memory limit");
                baseRevision++; baseInventory = next; baseContentIdentity = states["inventory"].ContentIdentity;
            }
            catch (Exception exception) when (IsSourceFailure(exception)) { projectionError = Error(exception); }
        }
        var completed = new Dictionary<string, ObservationSample>(StringComparer.Ordinal);
        foreach (var key in selected)
        {
            var ids = key.Kind == "base-explorer" && key.Argument is "ready" or "blocked"
                ? new[] { "inventory", key.Argument } : InventorySource;
            var usable = inventory is not null && projectionError is null && baseInventory is not null
                && (key.Kind != "base-explorer" || key.Argument != "ready" || ready is not null)
                && (key.Kind != "base-explorer" || key.Argument != "blocked" || blocked is not null);
            object? data = !usable ? null : key.Kind == "base-workspace" ? baseInventory!.Workspace(key.Argument!)
                : key.Kind == "base-attention" ? baseInventory!.Attention() : baseInventory!.Explorer(key, ready, blocked);
            var sample = Sample(data, started, ids);
            if (usable && key.Kind == "base-workspace" && !baseInventory!.ById.ContainsKey(key.Argument!))
                sample = sample with { ConfirmedAbsence = true,
                    Error = new("resource-missing", "The Bead no longer exists.", false) };
            if (projectionError is not null) sample = sample with { Error = projectionError };
            else if (!usable && inventory is not null && (sample.Error is null || ids.Any(id => !states.ContainsKey(id))))
                sample = sample with { Pending = true, Error = new("budget-exceeded", "Source is awaiting its bounded observation turn.") };
            completed[key.Value] = result[key.Value] = CurrentSample(sample);
        }
        publishBase?.Invoke(completed);
    }
    private readonly TrackerSourceSchedule schedule = new(clock);
    private readonly Dictionary<string, (long Source, long Broad)> historyGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> cacheReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> cached = new(StringComparer.Ordinal);
    internal event Action<string, string>? ReadStarted;
    private bool scheduled;
    private bool continuation;
    private bool digestSectionsPending;
    private int reconciliationRequested;
    private readonly HashSet<string> outstandingKeys = new(StringComparer.Ordinal);
    private string interest = "";
    private HashSet<string> selectedIds = new(StringComparer.Ordinal);
    public void Invalidate(IReadOnlyList<BeadsInvalidation>? hints = null)
    {
        if (hints is null) schedule.InvalidateAll();
        else foreach (var hint in hints)
        {
            schedule.Invalidate(hint);
            if (hint.Operation == "comment" && hint.IssueId is { } id && selectedIds.Contains(id))
                schedule.InvalidateNames(["detail:" + id, "environment:" + id]);
        }
    }
    public double? PendingAge(IEnumerable<string> sources) => schedule.PendingAge(sources);
    public void Reconcile() => Interlocked.Exchange(ref reconciliationRequested, 1);
    public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadScheduledAsync(
        IReadOnlyList<OperationKey> keys, Action<string, ObservationSourceState> revalidated, CancellationToken token)
    {
        var signature = string.Join("\n", keys.Select(key => key.Value).Order(StringComparer.Ordinal));
        var changed = signature != interest;
        interest = signature;
        selectedIds = keys.Where(key => key.Kind is "workspace" or "section").Select(key => key.Argument!).ToHashSet(StringComparer.Ordinal);
        var reconcile = Interlocked.Exchange(ref reconciliationRequested, 0) != 0;
        if (!changed && !continuation && !reconcile && !schedule.HasDue) return new Dictionary<string, ObservationSample>();
        // One bounded turn may publish only one selected Workspace. Remember
        // completed projections for this round instead of reopening them merely
        // because another projection was published on the next turn.
        if (changed || !continuation || reconcile)
        {
            outstandingKeys.Clear();
            outstandingKeys.UnionWith(keys.Select(key => key.Value));
        }
        scheduled = true;
        try
        {
            var samples = await ReadTrackerAsync(keys, token, revalidated);
            var published = samples.ToDictionary(pair => pair.Key, pair => CurrentSample(pair.Value), StringComparer.Ordinal);
            foreach (var (key, sample) in published)
                if (sample.Pending) outstandingKeys.Add(key);
                else outstandingKeys.Remove(key);
            continuation = outstandingKeys.Count > 0 || digestSectionsPending;
            return published;
        }
        finally { scheduled = false; }
    }
    private static readonly JsonSerializerOptions RuntimeJson = ObservationJson.Create(snakeCase: true);
    private static readonly string[] InventorySource = ["inventory"];
    private readonly Dictionary<string, ObservationSourceState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (byte[] Hash, object Identity)> sourceContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> sourceReadOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SectionIdentity> sourceBeads = new(StringComparer.Ordinal);
    private long readOrder;
    private Action<string, ObservationSourceState>? revalidate;
    private readonly Dictionary<string, History> histories = new(StringComparer.Ordinal);
    private HashSet<string> pinnedHistories = new(StringComparer.Ordinal);
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
    private bool preferDigest;
    private int commentsThisSweep;
    private readonly HashSet<string> digestEvidenceIds = new(StringComparer.Ordinal);
    private static readonly string[] DigestCoreSources = ["inventory", "checkpoint"];
    private bool resumeDetails;
    private BeadsInventory? retainedInventory;
    private FrozenSet<string>? retainedReady;
    private FrozenSet<string>? retainedBlocked;
    private DateTimeOffset inventoryGeneratedAt;
    private ObservationSourceState? retainedInventoryState;
    private bool readyFirst;
    private TargetRepositoryValidator? repositoryValidator;
    private double nativeMilliseconds;
    private bool? targetedStreamsQualified;
    internal long CommandCount { get; private set; }
    internal double NativeMilliseconds => nativeMilliseconds;

    private sealed record History(IReadOnlyList<BeadsCommentSnapshot> Comments, DateTimeOffset ReadAt, int Bytes,
        ObservationSourceState Source, bool Strict);
    private sealed class WorkspaceCycle(BeadSnapshot initial, DateTimeOffset started)
    {
        internal IReadOnlyList<BeadWorkspaceRelation> Relations { get; set; } = [];
        internal BeadPresentation? Presentation { get; set; }
        internal BeadSnapshot Initial { get; } = initial;
        internal DateTimeOffset Started { get; } = started;
        internal int Step { get; set; }
        internal bool RequiresConfirmation { get; set; }
        internal BeadSnapshot? Detail { get; set; }
        internal IReadOnlyList<BeadDependency> Incoming { get; set; } = [];
        internal IReadOnlyList<BeadDependency> Outgoing { get; set; } = [];
        internal IReadOnlyList<BeadsCommentSnapshot> Comments { get; set; } = [];
        internal bool CommentsPrefetched { get; set; }
        internal ObservationSourceState? CommentPrefetchFailure { get; set; }
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

    private void RecordSuccessfulRead(string id, object? result, DateTimeOffset attemptedAt)
    {
        sourceReadOrder[id] = ++readOrder;
        if (id.Contains(':') && cached.GetValueOrDefault("inventory") is BeadsInventory inventoryAtRead
            && inventoryAtRead.ById.TryGetValue(id[(id.IndexOf(':') + 1)..], out var beadAtRead)) sourceBeads[id] = SectionIdentity.Capture(beadAtRead);
        // BeadsInventory's map is internal; serializing the wrapper would
        // produce {} and falsely treat different inventories as identical.
        object? content = result is BeadsInventory inventory ? inventory.ById : result;
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, ObservationJson.Options));
        if (!sourceContent.TryGetValue(id, out var previousContent) || !hash.AsSpan().SequenceEqual(previousContent.Hash))
            sourceContent[id] = (hash, new object());
        states[id] = new("fresh", clock.GetUtcNow(), attemptedAt, null) { ContentIdentity = sourceContent[id].Identity, ValidatedTimestamp = clock.GetTimestamp() };
        revalidate?.Invoke(id, states[id]);
    }

    private async Task<T?> ReadAsync<T>(string id, Func<Task<T>> read, CancellationToken token, string? reason = null) where T : class
    {
        if (scheduled && !schedule.Due(id) && cached.TryGetValue(id, out var held)) return (T)held;
        if (scheduled) ReadStarted?.Invoke(id, reason ?? schedule.Reason(id));
        var generation = scheduled ? schedule.Begin(id) : default;
        var attempt = clock.GetUtcNow();
        var accepted = states.GetValueOrDefault(id);
        try
        {
            var result = await read();
            RecordSuccessfulRead(id, result, attempt);
            if (scheduled)
            {
                states[id] = states[id] with { SourceGeneration = generation.Source };
                if (id.StartsWith("comments:", StringComparison.Ordinal)) historyGenerations[id] = generation;
                else
                {
                    StoreCached(id, result);
                    schedule.Complete(id, generation, true);
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            if (scheduled) schedule.Complete(id, generation, false);
            var previous = accepted;
            states[id] = (previous ?? new ObservationSourceState("unavailable", null, null, null)) with
                { Health = previous?.ObservedAt is null ? "unavailable" : "stale", LastAttemptAt = attempt, Error = Error(exception) };
            return null;
        }
    }

    private void StoreCached(string id, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value is BeadsInventory inventory ? inventory.ById : value, ObservationJson.Options).Length;
        if (!cacheReservations.TryGetValue(id, out var owner)) owner = new object();
        if (!memory.TryReplace([], [(owner, bytes)])) throw new ToolExecutionException("source cache exceeded shared memory limit");
        cacheReservations[id] = owner;
        cached[id] = value;
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

    public async Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerAsync(IReadOnlyList<OperationKey> keys, CancellationToken token,
        Action<string, ObservationSourceState>? revalidated = null)
    {
        revalidate = revalidated;
        try { return await ReadTrackerCoreAsync(keys, token); }
        finally { revalidate = null; }
    }

    private async Task<IReadOnlyDictionary<string, ObservationSample>> ReadTrackerCoreAsync(IReadOnlyList<OperationKey> keys, CancellationToken token)
    {
        sweepNativeStarted = nativeMilliseconds;
        commentsReadThisSweep.Clear();
        commentsThisSweep = 0;
        digestEvidenceIds.Clear();
        digestSectionsPending = false;
        var started = clock.GetUtcNow();
        var generatedAt = DateTimeOffset.FromUnixTimeSeconds(started.ToUnixTimeSeconds());
        var result = new Dictionary<string, ObservationSample>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            sectionCycles.Clear(); recentDetails.Clear(); pinnedHistories.Clear();
            foreach (var history in histories.Values) memory.Release(history);
            histories.Clear(); historyGenerations.Clear(); states.Clear(); sourceContent.Clear(); sourceReadOrder.Clear(); sourceBeads.Clear(); cached.Clear();
            foreach (var owner in cacheReservations.Values) memory.Release(owner);
            cacheReservations.Clear(); schedule.Reset(); workspaces.Clear(); attentionBeads = null;
            attentionPresentations.Clear(); retainedInventory = null; resumeDetails = false;
            baseInventory = null; baseContentIdentity = null; memory.Release(baseReservation);
            return result;
        }
        if (!keys.Any(key => key.Kind == "attention"))
        {
            attentionBeads = null;
            attentionPresentations.Clear();
        }
        ValidatedTargetRepository? repository;
        try
        {
            repositoryValidator ??= new TargetRepositoryValidator(CreateRunner());
            repository = await repositoryValidator.ValidateAsync(project.TargetRepository, project.ConfigFilePath, token);
        }
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
        var resumedDetails = resumeDetails && retainedInventory is not null;
        if (resumedDetails)
        {
            // Reserve one bounded detail turn when core work exhausted the last
            // turn. New core invalidations must not starve due histories. Captured
            // provenance and scheduled generation checks still guard publication;
            // a Workspace additionally confirms inventory after native detail reads.
            inventory = retainedInventory; ready = retainedReady; blocked = retainedBlocked;
            generatedAt = inventoryGeneratedAt;
            states["inventory"] = retainedInventoryState!;
        }
        else
        {
            inventory = await ReadAsync("inventory", () => reader.ReadSnapshotAsync(repository, token), token);
            ready = null; blocked = null;
            ProjectBases(keys, inventory, ready, blocked, started, result, inventoryOnly: true);
            for (var index = 0; index < 2 && HasBudget; index++)
            {
                var membership = (index == 0) == readyFirst ? "ready" : "blocked";
                if ((index == 0) == readyFirst)
                {
                    if (keys.Any(key => key.Kind is "explorer" or "base-explorer" && key.Argument == "ready"))
                        ready = await ReadAsync("ready", () => reader.ReadExplorerReadinessAsync(repository, token), token);
                }
                else if (keys.Any(key => key.Kind is "explorer" or "base-explorer" && key.Argument == "blocked"))
                    blocked = await ReadAsync("blocked", () => reader.ReadExplorerBlockedAsync(repository, token), token);
                ProjectBases(keys.Where(key => key.Kind == "base-explorer" && key.Argument == membership).ToArray(),
                    inventory, ready, blocked, started, result);
            }
            readyFirst = !readyFirst;
            // A failed core cannot erase the baseline of pending Workspace steps.
            // Keep its failure state visible and still require final confirmation.
            if (inventory is not null || workspaces.Count == 0 || keys.Any(key => key.Kind != "workspace"))
            {
                retainedInventory = inventory; retainedReady = ready; retainedBlocked = blocked;
                inventoryGeneratedAt = generatedAt;
            }
            retainedInventoryState = states["inventory"];
        }
        var inventoryState = states["inventory"];
        pinnedHistories = keys.Where(key => key.Kind is "workspace" or "section").Select(key => key.Argument!)
            .Concat(keys.Any(key => key.Kind == "attention") && inventory is not null
                ? inventory.ById.Values.Where(AttentionDeskProjector.IsPaused).Where(bead => !AttentionDeskProjector.IsTerminal(bead)).Select(bead => bead.Id) : [])
            .ToHashSet(StringComparer.Ordinal);
        ProjectBases(keys, inventory, ready, blocked, started, result);
        // Sections consume the same sources and native turn as legacy readers.
        // Alternate a reserved background turn under continuous selection.
        var sectionsFirst = HasBudget && preferSections;
        if (HasBudget && keys.Any(key => key.Kind is "section" or "digest-merges")) preferSections = !preferSections;
        if (scheduled && attentionBeads is not null && !ReferenceEquals(attentionInventoryState?.ContentIdentity, inventoryState.ContentIdentity))
        {
            // Carry successful histories, but rebuild membership/presentation from
            // the newer authoritative inventory before publishing the cohort.
            attentionBeads = null;
            attentionPresentations.Clear();
        }
        var detailCommandsBefore = CommandCount;
        var checkpointAttempt = clock.GetUtcNow();
        DateTimeOffset? caughtUp = null;
        try
        {
            if (!scheduled || schedule.Due("checkpoint"))
            {
                var generation = scheduled ? schedule.Begin("checkpoint") : default;
                caughtUp = checkpoint.ReadLastCaughtUpAt(strict: true);
                RecordSuccessfulRead("checkpoint", caughtUp, checkpointAttempt);
                if (scheduled) states["checkpoint"] = states["checkpoint"] with { SourceGeneration = generation.Source };
                cached["checkpoint"] = caughtUp is null ? Array.Empty<DateTimeOffset>() : new[] { caughtUp.Value };
                if (scheduled) schedule.Complete("checkpoint", generation, true);
            }
            else if (cached.TryGetValue("checkpoint", out var heldCheckpoint))
                caughtUp = ((DateTimeOffset[])heldCheckpoint).Cast<DateTimeOffset?>().FirstOrDefault();
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        { states["checkpoint"] = new("unavailable", null, checkpointAttempt, Error(exception)); }

        // Publish window membership before any merge history command starts.
        ProjectDigestBases(keys, inventory, caughtUp, generatedAt, started, result);
        if (sectionsFirst) await ReadSectionsAsync(keys, repository, reader, inventory, result, token);
        if (sectionsFirst) await ReadDigestSectionsAsync(keys, repository, reader, inventory, caughtUp, generatedAt, result, token);

        BeadExplorerItem[]? explorerItems = null;
        foreach (var key in keys.Where(key => key.Kind == "explorer"))
        {
            try
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
                result[key.Value] = Sample(key.Value.Count(character => character == ':') == 3 && filtered is not null
                    ? ExplorerPage.Project(filtered, key.Page) : filtered, started, ids);
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            { result[key.Value] = Sample(null, started, "inventory") with { Error = Error(exception) }; }
        }

        var selected = keys.Where(key => key.Kind == "workspace").ToArray();
        foreach (var id in workspaces.Keys.Where(id => !selected.Any(key => key.Argument == id)).ToArray()) workspaces.Remove(id);
        string? failedPrefetch = null;
        async Task ReadSelectedAsync()
        {
            if (selected.Length > 0 && inventory is not null && HasBudget)
            {
                var key = selected[workspaceCursor++ % selected.Length];
                if (key.Argument == failedPrefetch) return;
                var sample = await ReadWorkspaceAsync(repository, reader, inventory, key.Argument!, token);
                if (sample is not null) result[key.Value] = sample;
            }
            else if (inventory is null)
                foreach (var key in selected) result[key.Value] = Sample(null, started, "inventory");
        }
        // Read the selected cycle's shared history at a consistent position.
        // Alternating projection priority must not move this same read from the
        // beginning of one sweep to the end of the next and age it past budget.
        if (selected.Length > 0 && inventory is not null && HasBudget && commentsThisSweep < 4)
        {
            var id = selected[workspaceCursor % selected.Length].Argument!;
            if (!workspaces.ContainsKey(id) && inventory.ById.TryGetValue(id, out var initial))
            {
                var cycle = new WorkspaceCycle(initial, clock.GetUtcNow());
                workspaces[id] = cycle;
                var prefetchCommandsBefore = CommandCount;
                if (histories.TryGetValue(id, out var retainedHistory) && !retainedHistory.Strict)
                    schedule.InvalidateNames(["comments:" + id]);
                var combined = await TryReadColdWorkspaceAsync(repository, reader, id, token);
                commentsThisSweep++;
                var combinedComments = combined && commentsReadThisSweep.Contains(id);
                var readComments = !combinedComments && HasBudget;
                var comments = combinedComments ? histories[id].Comments
                    : readComments ? await ReadAsync("comments:" + id, () => reader.ReadDetailCommentsAsync(repository, id, token), token) : null;
                // An actual failed prefetch must remain visible at the comments
                // substep without retrying that command in the same sweep.
                if (readComments && comments is null) cycle.CommentPrefetchFailure = states["comments:" + id];
                // If the optional combined read spent the turn's budget, keep
                // the cycle for continuation without inventing a comment result.
                if (comments is not null)
                {
                    cycle.Sources["comments:" + id] = states["comments:" + id];
                    try
                    {
                        if (!combinedComments) StoreHistory(id, comments);
                        cycle.Comments = comments;
                        cycle.CommentsPrefetched = true;
                    }
                    catch (ToolExecutionException exception)
                    {
                        workspaces.Remove(id); failedPrefetch = id;
                        result["workspace:" + id] = new(null, cycle.Started, null, cycle.Sources.ToDictionary(), Error(exception));
                    }
                }
                cycle.RequiresConfirmation = CommandCount > prefetchCommandsBefore;
            }
        }
        // Alternate only on turns with detail capacity. Otherwise a slow core can
        // phase-lock the priority and continuously selected Workspaces starve history.
        var attentionFirst = HasBudget && keys.Any(key => key.Kind == "attention") && (preferAttention = !preferAttention);
        if (!attentionFirst) await ReadSelectedAsync();

        async Task ReadAttentionProjectionAsync()
        {
            if (keys.Any(key => key.Kind == "attention") && inventory is not null)
                try { await ReadAttentionAsync(repository, reader, inventory, inventoryState, result,
                    selectedIds, token); }
                catch (Exception exception) when (IsSourceFailure(exception))
                {
                    result["attention"] = Sample(null, started, "inventory") with { Error = Error(exception) };
                    attentionBeads = null;
                    attentionPresentations.Clear();
                }
            else if (inventory is null && keys.Any(key => key.Kind == "attention")) result["attention"] = Sample(null, started, "inventory");
        }
        // Digest evidence uses only budget left by the selected Workspace and
        // Attention Desk. Retain compact results, never full digest histories.
        async Task ReadDigestsAsync()
        {
            foreach (var key in keys.Where(key => key.Kind == "digest"))
            {
                try
                {
                    var digest = inventory is null ? null : CatchUpDigestProjector.Project(inventory, key.Argument!, caughtUp,
                        generatedAt, project.Limits.MaxRepairCycles);
                    var candidates = digest is null ? [] : digest.Threads.SelectMany(thread => thread.Items.Prepend(thread.Root))
                        .Concat(digest.StandaloneCompleted).Where(item => item.Status == "closed" && item.Type == "task")
                        .DistinctBy(item => item.Id).OrderByDescending(item => item.ActivityAt).ThenBy(item => item.Id, StringComparer.Ordinal)
                        .Take(128).Select(item => item.Id).ToArray();
                    var evidence = new Dictionary<string, DigestMergeEvidence>(StringComparer.Ordinal);
                    var pending = false;
                    foreach (var id in candidates)
                    {
                        digestEvidenceIds.Add(id);
                        var sourceId = "merge:" + id;
                        var due = !scheduled || schedule.Due(sourceId);
                        // A failed bootstrap has no cached value. Respect its retry
                        // backoff instead of falling through to an uncounted read.
                        if (!due && !cached.ContainsKey(sourceId)) continue;
                        var sharedHistory = scheduled && histories.ContainsKey(id) && !schedule.Due("comments:" + id)
                            && states.GetValueOrDefault("comments:" + id)?.Error is null;
                        if (due && !sharedHistory && (commentsThisSweep >= 4 || !HasBudget))
                        { pending = true; continue; }
                        var observed = await ReadAsync(sourceId, async () => DigestMergeEvidence.Read(inventory!.ById[id],
                            await ReadSharedHistoryAsync(repository, reader, id, token)
                                ?? throw new ToolExecutionException("Merge history could not be observed")), token);
                        if (observed is not null) evidence[id] = observed;
                    }
                    result[key.Value] = Sample(inventory is null ? null : CatchUpDigestProjector.Project(inventory, key.Argument!, caughtUp,
                        generatedAt, project.Limits.MaxRepairCycles, evidence), started,
                        DigestCoreSources.Concat(candidates.Select(id => "merge:" + id)).ToArray()) with { Pending = pending };
                }
                catch (Exception exception) when (IsSourceFailure(exception))
                { result[key.Value] = Sample(null, started, "inventory") with { Error = Error(exception) }; }
            }
        }
        // Due merge evidence gets a background turn as well, even when every
        // selected history and Attention cohort is repeatedly invalidated.
        var digestFirst = HasBudget && keys.Any(key => key.Kind is "digest" or "digest-merges") && (preferDigest = !preferDigest);
        if (digestFirst) { if (!sectionsFirst) await ReadDigestSectionsAsync(keys, repository, reader, inventory, caughtUp, generatedAt, result, token); await ReadDigestsAsync(); }
        await ReadAttentionProjectionAsync();
        if (attentionFirst) await ReadSelectedAsync();
        if (!sectionsFirst) await ReadSectionsAsync(keys, repository, reader, inventory, result, token);
        if (!digestFirst) { if (!sectionsFirst) await ReadDigestSectionsAsync(keys, repository, reader, inventory, caughtUp, generatedAt, result, token); await ReadDigestsAsync(); }
        // Bound reuse to one continuation turn even if another selected Workspace
        // never recovers. New interest and external creation must reach a fresh core.
        resumeDetails = !resumedDetails && ((workspaces.Count > 0 && keys.All(key => key.Kind == "workspace"))
            || (!HasBudget && CommandCount == detailCommandsBefore
                && (selected.Length > 0 || keys.Any(key => key.Kind is "attention" or "section" or "digest-merges"))));
        // A new cycle must not repeatedly compare an external edit against the
        // old core while a different Workspace remains pending indefinitely.
        if (result.Values.Any(sample => sample.Error?.Code == "source-conflict")) resumeDetails = false;
        TrimSources(selected.Select(key => key.Argument!).ToHashSet(StringComparer.Ordinal), keys);
        return result;
    }

    private async Task<bool> TryReadColdWorkspaceAsync(ValidatedTargetRepository repository, BeadsReader reader,
        string id, CancellationToken token)
    {
        var detailId = "detail:" + id;
        var commentsId = "comments:" + id;
        var incomingId = "relations-up:" + id;
        // Coalesce only already-due cold inputs. Warm comments-only turns retain
        // their small read and an incoming event cannot reuse an old generation.
        if (!scheduled || states.ContainsKey(detailId) || !schedule.Due(detailId)
            || !schedule.Due(commentsId) || !schedule.Due(incomingId)) return false;
        var generations = new[] { detailId, commentsId, incomingId }
            .ToDictionary(key => key, key => schedule.Begin(key), StringComparer.Ordinal);
        var attempted = clock.GetUtcNow();
        try
        {
            ReadStarted?.Invoke(detailId, "bootstrap-combined");
            var combined = await reader.ReadWorkspaceDetailAsync(repository, id, token);
            if (targetedStreamsQualified is null && HasBudget)
            {
                // One bounded discovery per collector, never per sweep. Only the
                // pinned producer's successfully requested iterators certify that
                // omitempty means empty. Counts and inventory play no part.
                try { targetedStreamsQualified = await reader.QualifyTargetedEmptyStreamsAsync(repository, token); }
                catch (Exception exception) when (IsSourceFailure(exception)) { targetedStreamsQualified = false; }
            }
            if (targetedStreamsQualified == true)
                combined = (combined.Bead, combined.Comments ?? [], combined.Incoming ?? []);
            else combined = (combined.Bead, null, null);
            Accept(detailId, combined.Bead);
            if (combined.Comments is not null)
            {
                Accept(commentsId, combined.Comments);
                StoreHistory(id, combined.Comments);
            }
            if (combined.Incoming is not null) Accept(incomingId, combined.Incoming);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            // Unsupported flags, missing streams, limits and failed iterations
            // establish no freshness; the ordinary bounded reads remain due.
            schedule.InvalidateNames([detailId, commentsId, incomingId]);
            states[detailId] = new("unavailable", null, attempted, Error(exception));
            return false;
        }

        void Accept<T>(string key, T value) where T : class
        {
            RecordSuccessfulRead(key, value, attempted);
            states[key] = states[key] with { SourceGeneration = generations[key].Source };
            if (key == commentsId) historyGenerations[key] = generations[key];
            else { StoreCached(key, value); schedule.Complete(key, generations[key], true); }
        }
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
                var commandsBefore = CommandCount;
                ObservationSourceState? capturedState = null;
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
                        if (cycle.CommentPrefetchFailure is { } failure)
                        {
                            capturedState = failure;
                            cycle.CommentPrefetchFailure = null;
                        }
                        else if (cycle.CommentsPrefetched)
                        {
                            capturedState = cycle.Sources[sourceId];
                            cycle.CommentsPrefetched = false;
                        }
                        else if (histories.TryGetValue(id, out var history) && commentsReadThisSweep.Contains(id)
                            && states[sourceId].Error is null)
                        {
                            cycle.Comments = history.Comments;
                            capturedState = history.Source;
                        }
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
                        if (scheduled && cycle.RequiresConfirmation) schedule.InvalidateNames(["inventory"]);
                        var final = await ReadAsync(sourceId, () => reader.ReadSnapshotAsync(repository, token), token,
                            cycle.RequiresConfirmation ? "confirmation" : null);
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
                if (cycle.Step < 4 && CommandCount > commandsBefore) cycle.RequiresConfirmation = true;
                var state = capturedState ?? states[sourceId];
                cycle.Sources[sourceId] = state;
                if (state.Error is not null && !sourceId.StartsWith("relations-", StringComparison.Ordinal)
                    && !(sourceId.StartsWith("environment:", StringComparison.Ordinal) && cycle.Environment is not null))
                {
                    // Retry only the timed-out substep on the next bounded sweep.
                    // Replaying successful reads can starve a Workspace under slow
                    // native commands. The original identity and final confirming
                    // inventory still guard the complete projection.
                    if (state.Error?.Code != "source-timeout") workspaces.Remove(id);
                    return new(null, cycle.Started, null, cycle.Sources.ToDictionary(), state.Error);
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
        var due = paused.Where(bead => !histories.TryGetValue(bead.Id, out var history) || (scheduled ? schedule.Due("comments:" + bead.Id) : history.ReadAt < attentionStarted)).ToArray();
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
            try { StoreHistory(bead.Id, comments, selected.Contains(bead.Id)); }
            catch (ToolExecutionException exception)
            {
                result["attention"] = Sample(null, attentionStarted, "inventory") with { Error = Error(exception) };
                attentionBeads = null;
                attentionPresentations.Clear();
                return;
            }
        }
        if (paused.Any(bead => !histories.TryGetValue(bead.Id, out var history) || (scheduled ? schedule.Due("comments:" + bead.Id) : history.ReadAt < attentionStarted))) return;
        var items = paused.Select(bead => AttentionDeskProjector.Project(bead, histories[bead.Id].Comments, project.Limits.MaxRepairCycles) with
        { Presentation = attentionPresentations[bead.Id] }).OrderBy(item => item.Priority)
            .ThenBy(item => item.PausedAt is null ? 1 : 0).ThenBy(item => item.PausedAt).ThenBy(item => item.BeadId, StringComparer.Ordinal).ToArray();
        var sample = Sample(items, attentionStarted, InventorySource.Concat(paused.Select(bead => "comments:" + bead.Id)).ToArray());
        var sources = sample.Sources.ToDictionary();
        sources["inventory"] = attentionInventoryState!;
        foreach (var bead in paused) sources["comments:" + bead.Id] = histories[bead.Id].Source;
        result["attention"] = sample with { Sources = sources, ObservedAt = sources.Values.Min(state => state.ObservedAt) };
        attentionBeads = null;
        attentionPresentations.Clear();
    }

    private void StoreHistory(string id, IReadOnlyList<BeadsCommentSnapshot> comments, bool strict = true)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(comments).Length;
        var previous = histories.GetValueOrDefault(id);
        foreach (var victim in histories.Where(pair => pair.Key != id && !pinnedHistories.Contains(pair.Key))
            .OrderBy(pair => pair.Value.ReadAt).ToArray())
        {
            if ((histories.ContainsKey(id) || histories.Count < 64)
                && histories.Where(pair => pair.Key != id).Sum(pair => pair.Value.Bytes) + bytes <= 16 * 1024 * 1024
                && memory.Used - (histories.GetValueOrDefault(id)?.Bytes ?? 0) + bytes <= ObservationMemoryBudget.MaximumBytes) break;
            var name = "comments:" + victim.Key;
            memory.Release(victim.Value); histories.Remove(victim.Key); cached.Remove(name);
            states.Remove(name); sourceContent.Remove(name); sourceReadOrder.Remove(name); sourceBeads.Remove(name);
            historyGenerations.Remove(name); recentDetails.Remove(name); schedule.Retire(name);
        }
        if (!histories.ContainsKey(id) && histories.Count >= 64
            || histories.Where(pair => pair.Key != id).Sum(pair => pair.Value.Bytes) + bytes > 16 * 1024 * 1024)
            RefuseHistory();
        var history = new History(comments, clock.GetUtcNow(), bytes, states["comments:" + id],
            strict || previous?.Strict == true && ReferenceEquals(previous.Comments, comments));
        if (!memory.TryReplace(previous is null ? [] : [previous], [(history, bytes)]))
            RefuseHistory();
        histories[id] = history;
        if (scheduled)
        {
            cached["comments:" + id] = comments;
            if (historyGenerations.Remove("comments:" + id, out var generation)) schedule.Complete("comments:" + id, generation, true);
        }
        commentsReadThisSweep.Add(id);

        void RefuseHistory()
        {
            if (historyGenerations.Remove("comments:" + id, out var refused)) schedule.Complete("comments:" + id, refused, false);
            states["comments:" + id] = (previous?.Source ?? new ObservationSourceState("unavailable", null, null, null)) with
                { Health = previous is null ? "unavailable" : "stale", LastAttemptAt = clock.GetUtcNow(),
                    Error = new("source-limit", "Source exceeded the supported observation limits.") };
            throw new ToolExecutionException("shared observation cache exceeded read limit");
        }
    }

    private void TrimSources(HashSet<string> selected, IReadOnlyList<OperationKey> keys)
    {
        if (!keys.Any(key => key.Kind is "base-explorer" or "base-workspace" or "base-attention" or "section" or "base-digest" or "digest-merges"))
        { baseInventory = null; baseContentIdentity = null; memory.Release(baseReservation); }
        var retainedHistories = selected.Union(keys.Any(key => key.Kind == "attention")
            ? attentionBeads?.Select(bead => bead.Id)
                ?? retainedInventory?.ById.Values.Where(AttentionDeskProjector.IsPaused).Where(bead => !AttentionDeskProjector.IsTerminal(bead)).Select(bead => bead.Id) ?? []
            : []
            , StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var selectedSources = selected.SelectMany(id => new[]
            { "detail:" + id, "comments:" + id, "relations-up:" + id, "relations-down:" + id, "environment:" + id })
            .Concat(keys.Where(key => key.Kind == "section").SelectMany(SectionSources))
            .ToHashSet(StringComparer.Ordinal);
        var active = selectedSources.Concat(retainedHistories.Select(id => "comments:" + id))
            .Concat(digestEvidenceIds.Select(id => "merge:" + id)).ToHashSet(StringComparer.Ordinal);
        foreach (var id in selectedSources) recentDetails[id] = clock.GetUtcNow();
        foreach (var id in recentDetails.Keys.Where(id => !active.Contains(id) && clock.GetUtcNow() - recentDetails[id] >= TimeSpan.FromSeconds(60)).ToArray())
            recentDetails.Remove(id);
        foreach (var id in recentDetails.Where(pair => !active.Contains(pair.Key)).OrderByDescending(pair => pair.Value).Skip(64).Select(pair => pair.Key).ToArray())
            recentDetails.Remove(id);
        foreach (var name in recentDetails.Keys.Where(name => name.StartsWith("comments:", StringComparison.Ordinal))) retainedHistories.Add(name[9..]);
        foreach (var id in histories.Keys.Where(id => !retainedHistories.Contains(id)).ToArray())
        { memory.Release(histories[id]); histories.Remove(id); }
        // Readiness sets have no colon-qualified identity. Retire them with the
        // consuming Explorer view so an unreadable due slot cannot wake every turn.
        foreach (var id in states.Keys.Where(id => id.Contains(':') && !recentDetails.ContainsKey(id) && !(id.StartsWith("comments:", StringComparison.Ordinal)
                ? retainedHistories : id.StartsWith("merge:", StringComparison.Ordinal) ? digestEvidenceIds : selected).Contains(id[(id.IndexOf(':') + 1)..])
            || id is "ready" or "blocked" && !keys.Any(key => key.Kind is "explorer" or "base-explorer" && key.Argument == id)).ToArray())
        {
            states.Remove(id); sourceContent.Remove(id); sourceReadOrder.Remove(id); sourceBeads.Remove(id); cached.Remove(id); historyGenerations.Remove(id);
            if (cacheReservations.Remove(id, out var owner)) memory.Release(owner);
        }
        if (scheduled) schedule.Retain(states.Keys.Where(id => !recentDetails.ContainsKey(id) || active.Contains(id))
            .ToHashSet(StringComparer.Ordinal));
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
