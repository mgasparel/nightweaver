using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.ActivityFeed;

[Flags]
internal enum ActivityPageState { Ready = 0, Unknown = 1, Invalid = 2, Truncated = 4, Expired = 8, Gap = 16 }
internal sealed record ActivityCursor(int Version, Guid Journal, long Offset);
internal sealed record ActivityFilter(ActivityKind? Kind = null, ActivityPhase? Phase = null,
    ActivityStrength? Strength = null, string? Bead = null)
{
    internal bool Valid => (Kind is null || Enum.IsDefined(Kind.Value)) && (Phase is null || Enum.IsDefined(Phase.Value))
        && (Strength is null || Enum.IsDefined(Strength.Value)) && (Bead is null || ActivityProjector.SafeIdentifier(Bead));
    internal bool Matches(ActivityRecord r) => (Kind is null || Kind == r.Kind) && (Phase is null || Phase == r.Phase)
        && (Strength is null || Strength == r.Strength) && (Bead is null || Bead == r.Bead);
}
internal sealed record ActivityPage(IReadOnlyList<ActivityRecord> Records, ActivityCursor? Continuation,
    long? Oldest, long? Newest, ActivityPageState State, int ExaminedBytes);

// One shared store per runtime. Fixed-size slots permit a direct bounded seek.
// Padding counts against the disk and read budgets. No viewer owns a scan cache.
internal sealed class ActivityJournalFiles : IActivityJournalStorage
{
    private const int SlotBytes = ActivityLimits.RecordBytes;
    private const int SlotsPerSegment = ActivityLimits.SegmentBytes / SlotBytes - 1;
    private sealed record Identity(int Schema, string Project, string Target, string Repository, Guid Journal);
    private sealed record Header(Identity Identity, Guid Runtime, long First);
    private sealed record Segment(int Slot, Header Header, ActivityPosition[] Positions, bool Sealed);
    private sealed record Snapshot(Identity Identity, Segment[] Segments, ActivityPageState State);
    private readonly string root;
    private readonly string project;
    private readonly string target;
    private readonly GitHubRepositoryIdentity repository;
    private InvocationRuntimeFiles.PinnedRuntimeFile? lease;
    private Snapshot? snapshot;
    private Guid runtime;

    // Web can share one instance without taking the writer lease. Each turn
    // reads only the manifest and eight fixed headers, then seeks to its cursor.
    private Snapshot ReadView(ref int examined)
    {
        using var manifest = InvocationRuntimeFiles.OpenActivityFileForRead(Path.Combine(root, "manifest.json"));
        if (manifest.Stream.Length is <= 0 or > 16384) throw new InvalidDataException("Invalid Activity manifest size.");
        var bytes = new byte[(int)manifest.Stream.Length]; manifest.Stream.ReadExactly(bytes); examined += bytes.Length;
        var identity = JsonSerializer.Deserialize<Identity>(bytes) ?? throw new InvalidDataException("Missing Activity identity.");
        if (identity.Schema != 1 || identity.Journal == Guid.Empty || identity.Project != project || identity.Target != target
            || identity.Repository != repository.CliRepository || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(identity)))
            throw new InvalidDataException("Invalid or foreign Activity identity.");
        var segments = new List<Segment>();
        var state = ActivityPageState.Ready;
        for (var slot = 0; slot < ActivityLimits.Segments; slot++)
        {
            try
            {
                using var file = InvocationRuntimeFiles.OpenActivityFileForRead(SegmentPath(slot));
                if (file.Stream.Length < SlotBytes || file.Stream.Length > ActivityLimits.SegmentBytes)
                    throw new InvalidDataException("Invalid Activity segment size.");
                examined += SlotBytes;
                var header = ReadHeader(file.Stream);
                if (header.Identity != identity || header.Runtime == Guid.Empty || header.First <= 0
                    || header.First > long.MaxValue - SlotsPerSegment)
                    throw new InvalidDataException("Invalid or foreign Activity segment identity.");
                var count = (int)((file.Stream.Length - SlotBytes) / SlotBytes);
                if (file.Stream.Length % SlotBytes != 0) state |= ActivityPageState.Truncated | ActivityPageState.Gap;
                // Sequence is validated from each record, not inferred from offset.
                var positions = Enumerable.Range(0, count).Select(i => new ActivityPosition(identity.Journal,
                    header.First + i, header.Runtime, 0)).ToArray();
                segments.Add(new(slot, header, positions, true));
            }
            catch (IOException e) when (Missing(e)) { }
            catch (Exception e) when (ActivityJournal.StorageFailure(e))
            { state |= ActivityPageState.Invalid | ActivityPageState.Gap; }
        }
        var ordered = segments.OrderBy(s => s.Header.First).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Header.First < ordered[i - 1].Header.First + SlotsPerSegment)
                throw new InvalidDataException("Activity segments overlap.");
        return new(identity, ordered, state);
    }

    internal ActivityJournalFiles(HostPaths paths, ProjectSlug project, string targetRepository, GitHubRepositoryIdentity repository)
    {
        this.project = ProjectSlug.Parse(project.Value).Value;
        root = paths.ActivityRoot(project);
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRepository));
        if (Path.GetRelativePath(target, root) is var relative && (!relative.StartsWith(".." + Path.DirectorySeparatorChar)
            && relative != ".." && !Path.IsPathFullyQualified(relative)))
            throw new ArgumentException("Activity storage must be outside the Target Repository.");
        this.repository = repository;
    }

    private string SegmentPath(int slot) => Path.Combine(root, $"segment-{slot}.activity");

    public Task<ActivityJournalStart> StartAsync(Guid runtime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Activity storage requires safe Windows handles.");
        if (runtime == Guid.Empty || lease is not null) throw new InvalidOperationException("Activity writer already started or has no identity.");
        this.runtime = runtime;
        var physicalTarget = InvocationRuntimeFiles.ResolveExistingDirectoryPath(target);
        // Check the existing ancestry before creating any observation file.
        var ancestor = root;
        while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor)
            ?? throw new InvalidDataException("Activity storage has no existing ancestry.");
        var physicalRoot = Path.GetFullPath(Path.Combine(InvocationRuntimeFiles.ResolveExistingDirectoryPath(ancestor),
            Path.GetRelativePath(ancestor, root)));
        var physicalRelative = Path.GetRelativePath(physicalTarget, physicalRoot);
        if (physicalRelative != ".." && !physicalRelative.StartsWith(".." + Path.DirectorySeparatorChar)
            && !Path.IsPathFullyQualified(physicalRelative))
            throw new InvalidDataException("Activity storage resolves inside the Target Repository.");
        lease = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(root, "writer.lock"), true);
        if (lease.Stream.Length != 0) throw new InvalidDataException("Activity lock contains unknown data.");
        if (!string.Equals(physicalRoot, InvocationRuntimeFiles.ResolveExistingDirectoryPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Activity storage ancestry changed.");
        var manifest = Path.Combine(root, "manifest.json");
        Identity identity;
        try
        {
            using var file = InvocationRuntimeFiles.OpenActivityFileForRead(manifest);
            if (file.Stream.Length is <= 0 or > 16384) throw new InvalidDataException("Invalid Activity manifest size.");
            var bytes = new byte[(int)file.Stream.Length]; file.Stream.ReadExactly(bytes);
            identity = JsonSerializer.Deserialize<Identity>(bytes) ?? throw new InvalidDataException("Missing Activity identity.");
            if (identity.Schema != 1 || identity.Journal == Guid.Empty || identity.Project != project
                || identity.Target != target || identity.Repository != repository.CliRepository
                || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(identity)))
                throw new InvalidDataException("Invalid or foreign Activity identity.");
        }
        catch (IOException e) when (Missing(e))
        {
            // Do not adopt orphan segments into a new identity.
            for (var i = 0; i < ActivityLimits.Segments; i++)
            {
                try { using var existing = InvocationRuntimeFiles.OpenActivityFileForRead(SegmentPath(i));
                    throw new InvalidDataException("Activity manifest is missing beside retained data."); }
                catch (IOException absent) when (Missing(absent)) { }
            }
            identity = new(1, project, target, repository.CliRepository, Guid.NewGuid());
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(identity);
            if (manifestBytes.Length > 16384) throw new InvalidDataException("Activity manifest exceeds its limit.");
            using var created = InvocationRuntimeFiles.OpenActivityFileForWrite(manifest, true);
            created.Stream.Write(manifestBytes); created.Stream.Flush(true);
        }
        var segments = new List<Segment>();
        var state = ActivityPageState.Ready;
        long next = 1;
        for (var slot = 0; slot < ActivityLimits.Segments; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var file = InvocationRuntimeFiles.OpenActivityFileForRead(SegmentPath(slot));
                var stream = file.Stream;
                if (stream.Length < SlotBytes || stream.Length > ActivityLimits.SegmentBytes)
                    throw new InvalidDataException("Invalid Activity segment size.");
                var header = ReadHeader(stream);
                if (header.Identity != identity || header.Runtime == Guid.Empty || header.First <= 0
                    || header.First > long.MaxValue - SlotsPerSegment)
                    throw new InvalidDataException("Invalid or foreign Activity segment identity.");
                if (header.Runtime == runtime) throw new InvalidDataException("Activity runtime identity was reused.");
                next = Math.Max(next, checked(header.First + SlotsPerSegment));
                var positions = new List<ActivityPosition>();
                var corrupt = false;
                for (var index = 0; index < (stream.Length - SlotBytes) / SlotBytes; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ActivityRecord r;
                    try { r = ReadRecord(stream); }
                    catch (Exception e) when (ActivityJournal.StorageFailure(e)) { corrupt = true; break; }
                    if (r.Position.Journal != identity.Journal || r.Position.Runtime != header.Runtime
                        || r.Position.Offset != header.First + index || positions.Count > 0 && r.Position.Sequence <= positions[^1].Sequence)
                    { corrupt = true; break; }
                    positions.Add(r.Position);
                }
                var partial = stream.Length % SlotBytes != 0;
                if (partial) state |= ActivityPageState.Truncated | ActivityPageState.Gap;
                if (corrupt) { positions.Clear(); state |= ActivityPageState.Invalid | ActivityPageState.Gap; }
                segments.Add(new(slot, header, positions.ToArray(), true));
            }
            catch (IOException e) when (Missing(e)) { }
            // A foreign header cannot be removed or silently adopted. Record
            // corruption fails closed too; the next runtime preserves the file.
        }
        var ordered = segments.OrderBy(s => s.Header.First).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Header.First <= ordered[i - 1].Header.First + SlotsPerSegment - 1)
                throw new InvalidDataException("Activity segments overlap.");
        Volatile.Write(ref snapshot, new(identity, ordered, state));
        return Task.FromResult(new ActivityJournalStart(identity.Journal, next, state != ActivityPageState.Ready, ordered.Length > 0,
            state.HasFlag(ActivityPageState.Truncated) ? ActivityReason.PartialTail : ActivityReason.SourceFailed));
    }

    private static bool Missing(IOException e) => e.InnerException is System.ComponentModel.Win32Exception w
        && w.NativeErrorCode is 2 or 3;

    public Task AppendAsync(ActivityRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = Volatile.Read(ref snapshot) ?? throw new InvalidOperationException("Activity storage is not ready.");
        if (record.Position.Journal != view.Identity.Journal || record.Position.Runtime != runtime)
            throw new InvalidDataException("Foreign Activity append.");
        var segments = view.Segments.ToList();
        var active = segments.LastOrDefault();
        if (active is null || active.Sealed || active.Positions.Length == SlotsPerSegment)
        {
            if (segments.Count == ActivityLimits.Segments)
            {
                var oldest = segments[0];
                using var retired = InvocationRuntimeFiles.OpenActivityFileForWrite(SegmentPath(oldest.Slot), false);
                if (ReadHeader(retired.Stream) != oldest.Header) throw new InvalidDataException("Activity segment changed before retirement.");
                InvocationRuntimeFiles.RetirePackageFile(retired.Stream);
                segments.RemoveAt(0);
                view = view with { State = view.State | ActivityPageState.Gap };
            }
            var slot = Enumerable.Range(0, ActivityLimits.Segments).First(i => segments.All(s => s.Slot != i));
            active = new(slot, new(view.Identity, runtime, record.Position.Offset), [], false);
            using var created = InvocationRuntimeFiles.OpenActivityFileForWrite(SegmentPath(slot), true);
            created.Stream.Write(Block(JsonSerializer.SerializeToUtf8Bytes(active.Header)));
            created.Stream.Flush(true);
            segments.Add(active);
        }
        if (record.Position.Offset != active.Header.First + active.Positions.Length
            || active.Positions.Length > 0 && record.Position.Sequence <= active.Positions[^1].Sequence)
            throw new InvalidDataException("Activity append is out of order.");
        using (var file = InvocationRuntimeFiles.OpenActivityFileForWrite(SegmentPath(active.Slot), false))
        {
            if (ReadHeader(file.Stream) != active.Header || file.Stream.Length != (active.Positions.Length + 1) * SlotBytes)
                throw new InvalidDataException("Activity append tail changed.");
            file.Stream.Position = file.Stream.Length;
            file.Stream.Write(Block(ActivityProjector.Serialize(record)));
            // Display evidence needs complete framing, not a physical disk barrier
            // per observation. Startup rejects any incomplete slot after a crash.
            file.Stream.Flush();
        }
        segments[^1] = active with { Positions = [.. active.Positions, record.Position] };
        Volatile.Write(ref snapshot, new(view.Identity, segments.ToArray(), view.State));
        return Task.CompletedTask;
    }

    internal ActivityPage ReadPage(ActivityCursor? cursor = null, ActivityFilter? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var examined = 0;
        var view = Volatile.Read(ref snapshot);
        if (lease is null)
        {
            try { view = ReadView(ref examined); }
            catch (Exception e) when (ActivityJournal.StorageFailure(e))
            { return new([], null, null, null, ActivityPageState.Unknown | ActivityPageState.Invalid, examined); }
        }
        if (view is null) return new([], null, null, null, ActivityPageState.Unknown, 0);
        var positions = view.Segments.SelectMany(s => s.Positions).ToArray();
        long? oldest = positions.Length == 0 ? null : positions[0].Offset;
        long? newest = positions.Length == 0 ? null : positions[^1].Offset;
        filter ??= new();
        if (!filter.Valid || cursor is { Version: not 1 } || cursor is { Offset: < 0 })
            return new([], null, oldest, newest, ActivityPageState.Invalid, examined);
        var state = view.State;
        var after = cursor?.Offset ?? 0;
        if (cursor is not null && (cursor.Journal != view.Identity.Journal || oldest is { } first && after < first - 1))
        { state |= ActivityPageState.Expired | ActivityPageState.Gap; after = (oldest ?? 1) - 1; }
        if (newest is { } last && after > last) return new([], null, oldest, newest, ActivityPageState.Invalid, examined);
        var rows = new List<ActivityRecord>();
        var progress = after;
        var failedSegmentEnd = after;
        var segmentRowStart = 0;
        try
        {
            foreach (var segment in view.Segments)
            {
                var index = Array.FindIndex(segment.Positions, p => p.Offset > after);
                if (index < 0) continue;
                if (examined + SlotBytes * (index == 0 ? 2 : 3) > ActivityLimits.BatchBytes) break;
                failedSegmentEnd = segment.Positions[^1].Offset;
                segmentRowStart = rows.Count;
                using var file = InvocationRuntimeFiles.OpenActivityFileForRead(SegmentPath(segment.Slot));
                examined += SlotBytes;
                if (ReadHeader(file.Stream) != segment.Header) { state |= ActivityPageState.Expired | ActivityPageState.Gap; break; }
                long previousSequence = 0;
                if (index > 0)
                {
                    file.Stream.Position = index * SlotBytes;
                    examined += SlotBytes;
                    var previous = ReadRecord(file.Stream);
                    if (previous.Position.Journal != view.Identity.Journal || previous.Position.Runtime != segment.Header.Runtime
                        || previous.Position.Offset != segment.Positions[index - 1].Offset)
                        throw new InvalidDataException("Activity predecessor changed.");
                    previousSequence = previous.Position.Sequence;
                }
                for (; index < segment.Positions.Length && rows.Count < ActivityLimits.PageRecords
                    && examined + SlotBytes <= ActivityLimits.BatchBytes; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    file.Stream.Position = (index + 1) * SlotBytes;
                    examined += SlotBytes;
                    var record = ReadRecord(file.Stream);
                    var expected = segment.Positions[index];
                    if (record.Position.Journal != expected.Journal || record.Position.Runtime != expected.Runtime
                        || record.Position.Offset != expected.Offset || record.Position.Sequence <= previousSequence
                        || expected.Sequence != 0 && record.Position.Sequence != expected.Sequence)
                        throw new InvalidDataException("Activity position changed.");
                    previousSequence = record.Position.Sequence;
                    progress = record.Position.Offset;
                    if (filter.Matches(record)) rows.Add(record);
                }
                if (rows.Count == ActivityLimits.PageRecords) break;
            }
        }
        catch (Exception e) when (ActivityJournal.StorageFailure(e))
        {
            rows.RemoveRange(segmentRowStart, rows.Count - segmentRowStart);
            progress = failedSegmentEnd;
            state |= ActivityPageState.Invalid | ActivityPageState.Gap;
        }
        return new(rows, new(1, view.Identity.Journal, progress), oldest, newest, state, examined);
    }

    private static byte[] Block(byte[] content)
    {
        if (content.Length + 1 > SlotBytes) throw new InvalidDataException("Activity slot exceeds its limit.");
        var block = new byte[SlotBytes]; content.CopyTo(block, 0); block[content.Length] = (byte)'\n'; return block;
    }
    private static byte[] Content(Stream stream)
    {
        var block = new byte[SlotBytes]; stream.ReadExactly(block);
        var end = Array.IndexOf(block, (byte)'\n');
        if (end < 1 || block.AsSpan(end + 1).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Invalid Activity slot.");
        return block[..end];
    }
    private static Header ReadHeader(Stream stream)
    {
        stream.Position = 0; var bytes = Content(stream);
        var header = JsonSerializer.Deserialize<Header>(bytes) ?? throw new InvalidDataException("Missing Activity header.");
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(header))) throw new InvalidDataException("Invalid Activity header.");
        return header;
    }
    private ActivityRecord ReadRecord(Stream stream) => ActivityProjector.Deserialize(Content(stream), repository);

    public void Dispose() { lease?.Dispose(); lease = null; }
}
