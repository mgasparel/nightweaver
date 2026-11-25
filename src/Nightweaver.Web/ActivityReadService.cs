using Nightweaver.ActivityFeed;
using Nightweaver.Configuration;
using Nightweaver.Inspection;
using Nightweaver.Telemetry;

namespace Nightweaver.Web;

internal interface IActivityReadSource
{
    Task<ActivityPage> ReadAsync(ActivityCursor? cursor, CancellationToken cancellationToken);
    FactoryTelemetryReadResult ReadRuntime();
}

internal sealed class ActivityReadSource(HostPaths paths, ProjectConfig project,
    IProcessRunner runner, FactoryTelemetryReader runtime) : IActivityReadSource, IDisposable
{
    private ActivityJournalFiles? files;
    private bool initialized;

    public async Task<ActivityPage> ReadAsync(ActivityCursor? cursor, CancellationToken cancellationToken)
    {
        // Resolve the configured origin once. Never query a tracker for Activity.
        if (!initialized)
        {
            initialized = true;
            var request = GitCommand.Create(["config", "--get", "remote.origin.url"], project.TargetRepository.Path)
                with { Timeout = ActivityLimits.Refresh, MaximumOutputBytes = 2048 };
            var result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && GitHubRepositoryIdentity.ParseRemoteUrl(result.StandardOutput.Trim()) is { } repository)
                files = new(paths, project.Name, project.TargetRepository.Path, repository);
        }
        return files?.ReadPage(cursor, cancellationToken: cancellationToken)
            ?? new([], null, null, null, ActivityPageState.Unknown, 0);
    }

    public FactoryTelemetryReadResult ReadRuntime() => runtime.Read();
    public void Dispose() => files?.Dispose();
}

internal sealed record ActivitySelection(ActivityKind[] Kinds, ActivityPhase[] Phases,
    ActivityStrength[] Strengths, string? Bead, string? Level)
{
    internal bool Matches(ActivityRecord row) => (Kinds.Length == 0 || Kinds.Contains(row.Kind))
        && (Phases.Length == 0 || Phases.Contains(row.Phase))
        && (Strengths.Length == 0 || Strengths.Contains(row.Strength))
        && (Bead is null || row.Bead == Bead) && (Level is null || Level == LevelOf(row));

    private static string LevelOf(ActivityRecord row) => row.Kind switch
    {
        ActivityKind.RecoveryFailed or ActivityKind.CleanupFailed => "error",
        ActivityKind.ChangesRequired or ActivityKind.PauseObserved or ActivityKind.EvidenceUnavailable
            or ActivityKind.RecordsLost or ActivityKind.RuntimeReplaced or ActivityKind.HistoryGap => "warning",
        ActivityKind.HealthChanged when row.Reason is ActivityReason.RuntimeFailed or ActivityReason.Unhealthy => "error",
        _ => "work"
    };
}

internal sealed record ActivityReadPage(IReadOnlyList<ActivityRecord> Records, string? Cursor,
    long? Oldest, long? Newest, long? LoadedThrough, bool Loading, ActivityPageState State,
    ActivityFreshness JournalHealth, DateTimeOffset? AttemptedAt, DateTimeOffset? ObservedAt,
    FactoryTelemetryAvailability RuntimeHealth, string? RuntimeId, DateTimeOffset? RuntimeObservedAt,
    bool RuntimeReplaced, int ExaminedBytes, IReadOnlyList<ActivityRecordDetail> Details);

internal sealed record ActivityRecordDetail(string Id, string Availability, string? Workspace, string? Invocation, string? PullRequest);

// One serialized collector owns all file reads. HTTP readers only inspect memory.
// Loading is progressive: each turn keeps the journal's scan and record limits.
internal sealed class ActivityReadService : BackgroundService
{
    private readonly IActivityReadSource source;
    private readonly TimeProvider clock;
    private readonly ObservationMemoryBudget budget;
    private readonly string projectId;
    private readonly object sync = new();
    private readonly List<ActivityRecord> rows = [];
    private ActivityCursor? loaded;
    private ActivityPageState state = ActivityPageState.Unknown;
    private long? oldest, newest;
    private DateTimeOffset? attempted, observed;
    private long? receipt;
    private long? runtimeReceipt;
    private bool available;
    private ActivityPageState retainedGaps;
    private FactoryTelemetryReadResult runtime = new(FactoryTelemetryAvailability.Unavailable, null, null);
    private int streams;
    private long? interest;

    internal ActivityReadService(IActivityReadSource source, TimeProvider clock, ObservationMemoryBudget budget, string projectId)
    { this.source = source; this.clock = clock; this.budget = budget; this.projectId = projectId; }

    internal bool TryEnterStream()
    {
        lock (sync) { if (streams == 16) return false; streams++; return true; }
    }
    internal void ExitStream() { lock (sync) streams--; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool requested;
            lock (sync) requested = streams > 0 || interest is { } stamp && clock.GetElapsedTime(stamp) <= ActivityLimits.FreshFor;
            if (requested) await RefreshAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(ActivityLimits.Refresh, clock, stoppingToken).ConfigureAwait(false);
        }
    }, stoppingToken);

    // Only the collector calls this method. Tests call it without starting the host.
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ActivityCursor? cursor;
        lock (sync) { cursor = loaded; attempted = clock.GetUtcNow(); }
        try
        {
            var currentRuntime = source.ReadRuntime();
            lock (sync) { runtime = currentRuntime; runtimeReceipt = clock.GetTimestamp(); }
        }
        catch (Exception exception) when (SourceFailure(exception))
        { lock (sync) runtime = new(FactoryTelemetryAvailability.Unavailable, null, null); }
        try
        {
            var page = await source.ReadAsync(cursor, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                state = page.State;
                available = !page.State.HasFlag(ActivityPageState.Unknown);
                if (!available) return;
                oldest = page.Oldest; newest = page.Newest;
                if (loaded?.Journal != page.Continuation?.Journal || page.State.HasFlag(ActivityPageState.Expired))
                { rows.Clear(); retainedGaps = ActivityPageState.Ready; }
                retainedGaps |= page.State & (ActivityPageState.Gap | ActivityPageState.Truncated | ActivityPageState.Invalid);
                if (oldest is { } first) rows.RemoveAll(r => r.Position.Offset < first);
                foreach (var row in page.Records)
                    if (loaded is null || loaded.Journal != row.Position.Journal || row.Position.Offset > loaded.Offset)
                    {
                        rows.Add(row);
                        if (row.Kind is ActivityKind.RecordsLost or ActivityKind.HistoryGap or ActivityKind.RuntimeReplaced)
                            retainedGaps |= ActivityPageState.Gap;
                    }
                loaded = page.Continuation;
                while (rows.Count > ActivityLimits.RetainedRecords) { rows.RemoveAt(0); retainedGaps |= ActivityPageState.Gap; }
                // Account for canonical bytes and bounded object/identity overhead.
                while (!budget.TryReplace([], [(this, rows.Count * (ActivityLimits.RecordBytes + 512))]) && rows.Count > 0)
                { rows.RemoveAt(0); retainedGaps |= ActivityPageState.Gap; }
                observed = clock.GetUtcNow(); receipt = clock.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (SourceFailure(exception))
        { lock (sync) { available = false; state = ActivityPageState.Unknown; } }
    }

    internal ActivityReadPage Read(ActivityCursor? cursor, ActivitySelection filter)
    {
        lock (sync)
        {
            interest = clock.GetTimestamp();
            var health = !available ? attempted is null ? ActivityFreshness.Unknown : ActivityFreshness.Unavailable
                : receipt is { } stamp && clock.GetElapsedTime(stamp) <= ActivityLimits.FreshFor ? ActivityFreshness.Fresh : ActivityFreshness.Stale;
            var flags = state | retainedGaps;
            var after = cursor?.Offset ?? 0;
            var cacheFirst = rows.Count == 0 ? oldest : rows[0].Position.Offset;
            if (cursor is not null && loaded is not null && (cursor.Journal != loaded.Journal
                || cacheFirst is { } first && after < first - 1))
            { flags |= ActivityPageState.Expired | ActivityPageState.Gap; after = (cacheFirst ?? 1) - 1; }
            if (cursor is not null && loaded is not null && cursor.Journal == loaded.Journal && cursor.Offset > (newest ?? 0))
                throw new ArgumentException("Future Activity cursor.");
            var result = new List<ActivityRecord>();
            var bytes = 0;
            foreach (var row in rows)
            {
                if (row.Position.Offset <= after) continue;
                var size = ActivityProjector.Serialize(row).Length;
                if (bytes + size > ActivityLimits.BatchBytes - 65536 || result.Count == ActivityLimits.PageRecords) break;
                bytes += size; after = row.Position.Offset;
                if (filter.Matches(row)) result.Add(row);
            }
            // A scan can cross corrupt or filtered records without displaying a row.
            if (result.Count < ActivityLimits.PageRecords && bytes < ActivityLimits.BatchBytes
                && (rows.Count == 0 || after >= rows[^1].Position.Offset)) after = Math.Max(after, loaded?.Offset ?? 0);
            var id = loaded is null ? null : ActivityEndpoints.FormatCursor(new(1, loaded.Journal, after));
            var runtimeHealth = runtime.Availability == FactoryTelemetryAvailability.Live && runtimeReceipt is { } lastRead
                && clock.GetElapsedTime(lastRead) > TimeSpan.FromSeconds(10) ? FactoryTelemetryAvailability.Stale : runtime.Availability;
            var priorRuntime = cursor is null ? null : rows.LastOrDefault(r => r.Position.Offset <= cursor.Offset)?.Position.Runtime;
            var replaced = priorRuntime is { } prior && Guid.TryParse(runtime.Snapshot?.RuntimeId, out var current) && current != prior;
            return new(result, id, oldest, newest, loaded?.Offset, newest > loaded?.Offset, flags, health,
                attempted, observed, runtimeHealth, runtime.Snapshot?.RuntimeId, runtime.Snapshot?.ObservedAt, replaced, bytes,
                result.Select(r => ResolveDetail(r, runtimeHealth)).ToArray());
        }
    }

    private static bool SourceFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or InvalidDataException or System.Text.Json.JsonException or ArgumentException or NotSupportedException
        or ToolExecutionException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException;

    private ActivityRecordDetail ResolveDetail(ActivityRecord row, FactoryTelemetryAvailability runtimeHealth)
    {
        var id = ActivityEndpoints.FormatCursor(new(1, row.Position.Journal, row.Position.Offset));
        // Do not synthesize a file path or reconstruct an expired invocation.
        string? invocation = null;
        if (row.Detail == ActivityDetail.Invocation && runtimeHealth == FactoryTelemetryAvailability.Live
            && Guid.TryParse(runtime.Snapshot?.RuntimeId, out var current) && current == row.Position.Runtime)
        {
            var snapshot = runtime.Snapshot!;
            var slot = new[] { snapshot.Planning, snapshot.Delivery, snapshot.Merger }.Where(p => p is not null)
                .SelectMany(p => p!.Slots).FirstOrDefault(s => s.BeadId == row.Bead
                    && s.Invocation is { } selected && selected.InvocationId == row.Invocation && selected.BeadId == row.Bead);
            if (slot is not null) invocation = "/api/telemetry/stream?runtime_id=" + Uri.EscapeDataString(snapshot.RuntimeId)
                + "&slot_id=" + Uri.EscapeDataString(slot.SlotId) + "&invocation_id=" + Uri.EscapeDataString(row.Invocation!);
        }
        var workspace = row.Bead is not null && row.Detail is ActivityDetail.Bead or ActivityDetail.Comment
            ? "/projects/" + Uri.EscapeDataString(projectId) + "/beads/" + row.Bead
                + (row.Detail == ActivityDetail.Comment ? "?tab=history" : "") : null;
        return new(id, invocation is not null || workspace is not null || row.Merge is not null ? "available" : "unavailable",
            workspace, invocation, row.Merge?.PullRequest);
    }

    public override void Dispose() { base.Dispose(); budget.Release(this); }
}
