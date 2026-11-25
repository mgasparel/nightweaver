using System.Text;
using System.Runtime.InteropServices;
using Nightweaver.Execution;
using Nightweaver.ActivityFeed;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class ActivityJournalTests
{
    private static readonly GitHubRepositoryIdentity Repository = new("github.com", "owner", "repo");
    private static ActivityPage Read(ActivityJournalFiles store, ActivityCursor? cursor = null, ActivityFilter? filter = null) =>
        store.ReadPage(cursor, filter, TestContext.Current.CancellationToken);
    private static ActivityEvidence Evidence(int index = 0) => new(ActivityKind.HealthChanged, ActivitySource.Health,
        ActivityPhase.Runtime, Guid.NewGuid(), "health-" + index) { Freshness = ActivityFreshness.Fresh };
    private static ActivityRecord Record(Guid journal, Guid runtime, long offset, long sequence = 0) =>
        ActivityProjector.Project(new(journal, offset, runtime, sequence == 0 ? offset : sequence),
            DateTimeOffset.UtcNow, Evidence(), Repository)!;

    private sealed class MemoryStorage : IActivityJournalStorage
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AppendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<ActivityRecord> Records = [];
        internal bool Hold;
        internal bool HoldAppend;
        internal bool Fail;
        internal bool Disposed;
        internal bool Cancelled;
        public async Task<ActivityJournalStart> StartAsync(Guid runtime, CancellationToken token)
        {
            Entered.TrySetResult();
            if (Hold) { using var registration = token.Register(() => Cancelled = true); await Release.Task; }
            return new(Guid.NewGuid(), 1, false, false);
        }
        public async Task AppendAsync(ActivityRecord record, CancellationToken token)
        {
            AppendEntered.TrySetResult();
            if (HoldAppend) await Release.Task;
            token.ThrowIfCancellationRequested();
            if (Fail) throw new UnauthorizedAccessException("SECRET_SENTINEL");
            lock (Records) Records.Add(record);
        }
        public void Dispose() { Disposed = true; Settled.TrySetResult(); }
    }

    [Fact]
    public async Task BlockedAppendDoesNotHoldSubmissionOrQueueLossReporting()
    {
        var storage = new MemoryStorage { HoldAppend = true };
        await using var writer = new ActivityJournal(storage, Repository, Guid.NewGuid());
        Assert.True(writer.TrySubmit(Evidence()));
        await storage.AppendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (var i = 0; i < ActivityLimits.ProducerQueue; i++) Assert.True(writer.TrySubmit(Evidence(i)));
        Assert.False(writer.TrySubmit(Evidence()));
        storage.Release.SetResult();
        await writer.DisposeAsync();
        Assert.Equal(1, writer.Health.Lost);
        Assert.Equal(ActivityLimits.ProducerQueue + 2, storage.Records.Count);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ClockChangesDoNotChangeJournalOrder()
    {
        var storage = new MemoryStorage { Hold = true };
        var clock = new Clock();
        await using var writer = new ActivityJournal(storage, Repository, Guid.NewGuid(), clock);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(writer.TrySubmit(Evidence()));
        clock.Now = clock.Now.AddDays(-10);
        Assert.True(writer.TrySubmit(Evidence()));
        storage.Release.SetResult();
        await writer.DisposeAsync();
        Assert.True(storage.Records[0].ObservedAt > storage.Records[1].ObservedAt);
        Assert.True(storage.Records[0].Position.Offset < storage.Records[1].Position.Offset);
    }

    [Fact]
    public async Task HeldStorageKeepsQueueFiniteAndPublishesLossBeforeProgress()
    {
        var storage = new MemoryStorage { Hold = true };
        await using var writer = new ActivityJournal(storage, Repository, Guid.NewGuid());
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (var i = 0; i < ActivityLimits.ProducerQueue; i++) Assert.True(writer.TrySubmit(Evidence(i)));
        for (var i = 0; i < 50; i++) Assert.False(writer.TrySubmit(Evidence(i)));
        Assert.Equal(50, writer.Health.Lost);
        storage.Release.SetResult();
        await writer.DisposeAsync();
        Assert.True(storage.Disposed);
        Assert.Equal(ActivityLimits.ProducerQueue + 1, storage.Records.Count);
        Assert.Equal(ActivityKind.RecordsLost, storage.Records[0].Kind);
        Assert.Equal(50, storage.Records[0].Count);
        Assert.Equal(Enumerable.Range(1, storage.Records.Count).Select(i => (long)i), storage.Records.Select(r => r.Position.Sequence));
    }

    [Fact]
    public async Task DeniedWritesExposeSafeHealthAndLoseQueuedRecords()
    {
        var storage = new MemoryStorage { Hold = true, Fail = true };
        await using var writer = new ActivityJournal(storage, Repository, Guid.NewGuid());
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (var i = 0; i < 10; i++) Assert.True(writer.TrySubmit(Evidence(i)));
        storage.Release.SetResult();
        await storage.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(writer.Health.Available);
        Assert.Equal(ActivityReason.DiskFailure, writer.Health.Reason);
        Assert.Equal(10, writer.Health.Lost);
        Assert.DoesNotContain("SECRET_SENTINEL", writer.Health.ToString());
        Assert.False(writer.TrySubmit(Evidence()));
    }

    [Fact]
    public async Task ShutdownIsBoundedAndDoesNotDisposeStorageUnderBlockedOperation()
    {
        var storage = new MemoryStorage { Hold = true };
        var writer = new ActivityJournal(storage, Repository, Guid.NewGuid());
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(writer.TrySubmit(Evidence()));
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(storage.Cancelled);
        Assert.False(storage.Disposed);
        Assert.False(writer.TrySubmit(Evidence()));
        storage.Release.SetResult();
        await storage.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, writer.Health.Lost);
    }

    [Fact]
    public async Task OversizedInputDoesNotOccupyQueueAndInvalidInputDoesNotBreakOffsets()
    {
        var storage = new MemoryStorage();
        await using var writer = new ActivityJournal(storage, Repository, Guid.NewGuid());
        Assert.False(writer.TrySubmit(Evidence() with { SourceReference = new string('x', 100000) }));
        Assert.True(writer.TrySubmit(Evidence() with { Source = ActivitySource.Invocation }));
        Assert.True(writer.TrySubmit(Evidence()));
        await writer.DisposeAsync();
        Assert.Equal(2, writer.Health.Lost);
        Assert.Equal(Enumerable.Range(1, storage.Records.Count).Select(i => (long)i), storage.Records.Select(r => r.Position.Offset));
        Assert.Equal(ActivityKind.HealthChanged, storage.Records[^1].Kind);
    }

    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "activity-" + Guid.NewGuid().ToString("N"));
        internal HostPaths Paths => new(Path.Combine(Root, "host"));
        internal string Target => Path.Combine(Root, "target");
        internal string ActivityRoot => Paths.ActivityRoot(ProjectSlug.Parse("test"));
        internal Files() { Directory.CreateDirectory(Target); }
        internal ActivityJournalFiles Store(string? target = null) => new(Paths, ProjectSlug.Parse("test"), target ?? Target, Repository);
        public void Dispose() { Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task RotationBoundsDiskFilesRecordsAndReadWorkAndPreservesOtherData()
    {
        using var fixture = new Files();
        Directory.CreateDirectory(fixture.ActivityRoot);
        var sentinels = new[] { Path.Combine(fixture.ActivityRoot, "foreign.log"), fixture.Paths.FactoryTelemetryPath(ProjectSlug.Parse("test")),
            Path.Combine(fixture.Target, "transcript.log") };
        foreach (var path in sentinels) File.WriteAllText(path, "SECRET_SENTINEL");
        using var store = fixture.Store();
        var runtime = Guid.NewGuid();
        var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken);
        for (var i = 1; i <= 2300; i++) await store.AppendAsync(Record(start.Journal, runtime, i), TestContext.Current.CancellationToken);
        var segments = Directory.GetFiles(fixture.ActivityRoot, "*.activity");
        Assert.Equal(ActivityLimits.Segments, segments.Length);
        Assert.All(segments, p => Assert.InRange(new FileInfo(p).Length, 4096, ActivityLimits.SegmentBytes));
        Assert.InRange(segments.Sum(p => new FileInfo(p).Length), 1, ActivityLimits.SegmentBytes * ActivityLimits.Segments);
        var page = Read(store, new(1, start.Journal, 1));
        Assert.True(page.State.HasFlag(ActivityPageState.Expired));
        Assert.Equal(511, page.Oldest);
        Assert.Equal(2300, page.Newest);
        var total = 0;
        ActivityCursor? cursor = null;
        do
        {
            page = Read(store, cursor);
            Assert.InRange(page.ExaminedBytes, 0, ActivityLimits.BatchBytes);
            Assert.InRange(page.Records.Count, 0, ActivityLimits.PageRecords);
            total += page.Records.Count;
            if (page.Continuation == cursor) break;
            cursor = page.Continuation;
        } while (cursor?.Offset < page.Newest);
        Assert.Equal(1790, total);
        Assert.InRange(total, 1, ActivityLimits.RetainedRecords);
        foreach (var path in sentinels) Assert.Equal("SECRET_SENTINEL", File.ReadAllText(path));
    }

    [Fact]
    public async Task EmptyFilteredPagesContinueAndRejectInvalidOrForeignCursors()
    {
        using var fixture = new Files();
        using var store = fixture.Store();
        var runtime = Guid.NewGuid(); var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken);
        for (var i = 1; i <= 150; i++) await store.AppendAsync(Record(start.Journal, runtime, i), TestContext.Current.CancellationToken);
        var page = Read(store, filter: new(Kind: ActivityKind.MergeVerified));
        Assert.Empty(page.Records); Assert.Equal(63, page.Continuation!.Offset);
        var next = Read(store, page.Continuation, new(Kind: ActivityKind.MergeVerified));
        Assert.Empty(next.Records); Assert.True(next.Continuation!.Offset > page.Continuation.Offset);
        Assert.Equal(ActivityPageState.Invalid, Read(store, new(2, start.Journal, 0)).State);
        Assert.Equal(ActivityPageState.Invalid, Read(store, filter: new(Bead: "../unsafe")).State);
        Assert.True(Read(store, new(1, Guid.NewGuid(), 50)).State.HasFlag(ActivityPageState.Expired));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.ReadPage(cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RestartRetainsCompleteRecordsAndRejectsTornTailAndForeignIdentity()
    {
        using var fixture = new Files();
        Guid journal;
        using (var store = fixture.Store())
        {
            var runtime = Guid.NewGuid(); var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken); journal = start.Journal;
            await store.AppendAsync(Record(journal, runtime, 1), TestContext.Current.CancellationToken);
            await store.AppendAsync(Record(journal, runtime, 2), TestContext.Current.CancellationToken);
        }
        using (var file = new FileStream(Path.Combine(fixture.ActivityRoot, "segment-0.activity"), FileMode.Open, FileAccess.Write))
            file.SetLength(4096 * 3 + 100);
        using (var store = fixture.Store())
        {
            var runtime = Guid.NewGuid(); var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken);
            Assert.Equal(journal, start.Journal); Assert.True(start.Gap); Assert.True(start.Replaced);
            Assert.Equal(2, Read(store).Records.Count);
            Assert.True(Read(store).State.HasFlag(ActivityPageState.Truncated));
            await store.AppendAsync(Record(journal, runtime, start.NextOffset, 1), TestContext.Current.CancellationToken);
            Assert.Equal(3, Read(store).Records.Count);
        }
        Directory.CreateDirectory(fixture.Target + "-other");
        using var foreign = fixture.Store(fixture.Target + "-other");
        await Assert.ThrowsAsync<InvalidDataException>(() => foreign.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptMiddleSegmentIsUnavailableAndOtherSegmentsRemainReadable()
    {
        using var fixture = new Files();
        using (var store = fixture.Store())
        {
            var runtime = Guid.NewGuid(); var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken);
            for (var i = 1; i <= 260; i++) await store.AppendAsync(Record(start.Journal, runtime, i), TestContext.Current.CancellationToken);
        }
        using (var file = new FileStream(Path.Combine(fixture.ActivityRoot, "segment-0.activity"), FileMode.Open, FileAccess.Write))
        { file.Position = 4096 * 3; file.WriteByte(0xff); }
        using var recovered = fixture.Store();
        var result = await recovered.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.True(result.Gap);
        var page = Read(recovered);
        Assert.True(page.State.HasFlag(ActivityPageState.Invalid));
        Assert.Equal(5, page.Records.Count);
        Assert.Equal(256, page.Oldest);
    }

    [Fact]
    public async Task SharedReadersDoNotHoldWriterAndWriterLeaseExcludesReplacement()
    {
        using var fixture = new Files();
        using var store = fixture.Store();
        var runtime = Guid.NewGuid(); var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken);
        await store.AppendAsync(Record(start.Journal, runtime, 1), TestContext.Current.CancellationToken);
        using var replacement = fixture.Store();
        await Assert.ThrowsAsync<IOException>(() => replacement.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        using var reader = Nightweaver.Execution.InvocationRuntimeFiles.OpenActivityFileForRead(Path.Combine(fixture.ActivityRoot, "segment-0.activity"));
        await store.AppendAsync(Record(start.Journal, runtime, 2), TestContext.Current.CancellationToken);
        Assert.Equal(2, Read(store).Records.Count);
        using var webReader = fixture.Store();
        var page = Read(webReader);
        Assert.Equal(2, page.Records.Count);
        await store.AppendAsync(Record(start.Journal, runtime, 3), TestContext.Current.CancellationToken);
        var next = Read(webReader, page.Continuation);
        Assert.Equal(3, Assert.Single(next.Records).Position.Offset);
        Assert.Equal(page.Records.Select(r => r.Position), Read(webReader).Records.Take(2).Select(r => r.Position));
    }

    [Fact]
    public async Task JunctionAndHardLinkCannotChangeOutsideFiles()
    {
        using var fixture = new Files();
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "manifest.json"); File.WriteAllText(sentinel, "outside-sentinel");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ActivityRoot)!);
        var linked = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", fixture.ActivityRoot, outside],
            Environment.CurrentDirectory, null, TimeSpan.FromSeconds(15));
        Assert.Equal(0, linked.ExitCode);
        using (var store = fixture.Store())
        {
            Assert.True(Read(store).State.HasFlag(ActivityPageState.Invalid));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        }
        Assert.Equal("outside-sentinel", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(outside));
        Directory.Delete(fixture.ActivityRoot); // Remove only this fixture's junction.
        Directory.CreateDirectory(fixture.ActivityRoot);
        Assert.True(CreateHardLink(Path.Combine(fixture.ActivityRoot, "manifest.json"), sentinel, IntPtr.Zero));
        using (var store = fixture.Store())
        {
            Assert.True(Read(store).State.HasFlag(ActivityPageState.Invalid));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        }
        Assert.Equal("outside-sentinel", File.ReadAllText(sentinel));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr attributes);

    [Fact]
    public async Task CorruptManifestAndReusedRuntimePreserveAllJournalFiles()
    {
        using var fixture = new Files();
        var runtime = Guid.NewGuid();
        using (var store = fixture.Store())
        {
            var start = await store.StartAsync(runtime, TestContext.Current.CancellationToken);
            await store.AppendAsync(Record(start.Journal, runtime, 1), TestContext.Current.CancellationToken);
        }
        var segment = Path.Combine(fixture.ActivityRoot, "segment-0.activity");
        var before = File.ReadAllBytes(segment);
        using (var reused = fixture.Store())
            await Assert.ThrowsAsync<InvalidDataException>(() => reused.StartAsync(runtime, TestContext.Current.CancellationToken));
        var manifest = Path.Combine(fixture.ActivityRoot, "manifest.json");
        File.WriteAllText(manifest, "{broken}");
        using (var corrupted = fixture.Store())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => corrupted.StartAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
            Assert.True(Read(corrupted).State.HasFlag(ActivityPageState.Unknown));
        }
        Assert.Equal(before, File.ReadAllBytes(segment)); Assert.Equal("{broken}", File.ReadAllText(manifest));
    }

    [Fact]
    public async Task StoredRecordsRejectAlteredStrengthMessageSchemaAndUnknownFields()
    {
        var r = Record(Guid.NewGuid(), Guid.NewGuid(), 1);
        var bytes = ActivityProjector.Serialize(r);
        Assert.Equal(r.Position, ActivityProjector.Deserialize(bytes, Repository).Position);
        var json = Encoding.UTF8.GetString(bytes);
        foreach (var bad in new[] { json.Replace("\"schema\":1", "\"schema\":2"), json.Replace("Observed", "Verified"),
            json.Replace("Runtime health changed.", "SECRET_SENTINEL"), json.Insert(1, "\"unsafe\":1,"), json.Insert(1, "\"schema\":1,") })
            Assert.ThrowsAny<Exception>(() => ActivityProjector.Deserialize(Encoding.UTF8.GetBytes(bad), Repository));
        await Task.CompletedTask;
    }
}
