using System.Text.Json.Nodes;
using Nightweaver.Execution;
using Nightweaver.Upgrade;

namespace Nightweaver.Tests;

public sealed class UpgradeJournalTests
{
    private static readonly string Id = new('a', 32);
    private static readonly string Digest = new('b', 64);
    private static UpgradeScope Scope(string root) => new(root, "S-1-5-21-1001",
        [new("demo", Path.Combine(root, "demo.json"), Digest, root, root, "physical-repository")]);
    private static UpgradeProgress Progress(string root) => new(1, Id, 0, Scope(root), Digest, new('c', 64),
        Digest, UpgradePhase.Replacing, Id, null, Id, [new("demo", Id, 7, 9)],
        [new("demo", 123, DateTimeOffset.Parse("2026-11-21T12:00:00Z"), Id, Id, Id, "Local\\upgrade-test")],
        [new("cli/nightweaver.dll", Id, Digest, null, UpgradeFileResult.Pending)],
        [new(Id, UpgradeEvidenceKind.Failure, "sharing-denied", DateTimeOffset.Parse("2026-11-21T12:00:00Z"))]);

    [Fact]
    public async Task RestartInspectsPendingIntentAndRetainsEveryRevision()
    {
        using var temp = new TempDirectory();
        var store = new UpgradeJournal(Scope(temp.Path), Id);
        Assert.Equal(UpgradeJournalAvailability.Missing, store.Read().Availability);
        Assert.False(Directory.Exists(store.DirectoryPath));
        var first = (await store.SaveAsync(null, Progress(temp.Path), TestContext.Current.CancellationToken)).Progress!;
        var original = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "00000001.json"));
        var next = first with { PendingOperation = null, LastConfirmedOperation = Id,
            Files = [first.Files[0] with { ObservedHash = Digest, Result = UpgradeFileResult.New }] };
        await store.SaveAsync(1, next, TestContext.Current.CancellationToken);
        var restored = new UpgradeJournal(Scope(temp.Path), Id).Read().Progress!;
        Assert.Equal(2, restored.Revision);
        Assert.Equal(UpgradePhase.Replacing, restored.Phase);
        Assert.Null(restored.PendingOperation);
        Assert.Equal(Id, restored.LastConfirmedOperation);
        Assert.Equal(first.OldPackageDigest, restored.OldPackageDigest);
        Assert.Equal(first.NewPackageDigest, restored.NewPackageDigest);
        Assert.Equal(first.Maintenance, restored.Maintenance);
        Assert.Equal(first.Owners, restored.Owners);
        Assert.Equal(first.Evidence, restored.Evidence);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(store.DirectoryPath, "00000001.json")));
        Assert.StartsWith(Path.Combine(temp.Path, "run", "upgrade", "packages"), store.RetainedPackageRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(1, next, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(2, restored with { Evidence = [] }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("foreign")]
    [InlineData("revision")]
    [InlineData("version")]
    [InlineData("enum")]
    [InlineData("missing-member")]
    [InlineData("interrupted")]
    [InlineData("sharing")]
    [InlineData("empty-directory")]
    [InlineData("oversized")]
    [InlineData("unsafe-file")]
    public async Task UnavailableEvidenceSurvivesReadAndSave(string kind)
    {
        using var temp = new TempDirectory();
        var store = new UpgradeJournal(Scope(temp.Path), Id);
        var state = (await store.SaveAsync(null, Progress(temp.Path), TestContext.Current.CancellationToken)).Progress!;
        var path = Path.Combine(store.DirectoryPath, "00000001.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        switch (kind)
        {
            case "unknown": node["authority"] = true; break;
            case "null": node["scope"] = null; break;
            case "foreign": node["scope"]!["user_sid"] = "S-1-5-21-1002"; break;
            case "revision": node["revision"] = 7; break;
            case "version": node["version"] = 2; break;
            case "enum": node["phase"] = 8; break;
            case "missing-member": node.AsObject().Remove("pending_operation"); break;
            case "unsafe-file": node["files"]![0]!["path"] = "../worktrees/foreign"; break;
        }
        var text = kind switch { "malformed" => "{", "duplicate" => node.ToJsonString().Insert(1, "\"Version\":1,"),
            "oversized" => new string(' ', UpgradeJournal.MaximumBytes + 1), _ => node.ToJsonString() };
        File.WriteAllText(path, text);
        if (kind == "interrupted") File.WriteAllText(path + ".retained.tmp", "partial");
        if (kind == "empty-directory") File.Delete(path);
        using (var locked = kind == "sharing" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            Assert.Equal(UpgradeJournalAvailability.Unavailable, store.Read().Availability);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(1, state, TestContext.Current.CancellationToken));
        }
        if (kind != "empty-directory") Assert.Equal(text, File.ReadAllText(path));
        if (kind == "interrupted") Assert.Equal("partial", File.ReadAllText(path + ".retained.tmp"));
    }

    [Fact]
    public async Task CanceledPublicationRetainsPendingSiblingAndBlocksReinitialization()
    {
        using var temp = new TempDirectory();
        var store = new UpgradeJournal(Scope(temp.Path), Id);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(null, Progress(temp.Path), canceled.Token));
        Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.tmp"));
        Assert.Equal(UpgradeJournalAvailability.Unavailable, new UpgradeJournal(Scope(temp.Path), Id).Read().Availability);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(null, Progress(temp.Path), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementRequiresDurableRead(bool afterRename)
    {
        using var temp = new TempDirectory();
        var store = new UpgradeJournal(Scope(temp.Path), Id, boundary =>
        {
            if (boundary == (afterRename ? UpgradePublicationBoundary.Renamed : UpgradePublicationBoundary.Flushed))
                throw new IOException("injected manager interruption");
        });
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(null, Progress(temp.Path), TestContext.Current.CancellationToken));
        var read = new UpgradeJournal(Scope(temp.Path), Id).Read();
        Assert.Equal(afterRename ? UpgradeJournalAvailability.Available : UpgradeJournalAvailability.Unavailable, read.Availability);
        if (afterRename)
        {
            Assert.Equal(Id, read.Progress!.PendingOperation);
            Assert.Null(read.Progress.LastConfirmedOperation);
            Assert.Equal(1, read.Progress.Revision);
        }
        else Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task JunctionStoreCannotWriteOutsideEvidence()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var outside = temp.CreateDirectory("outside");
        var sentinel = Path.Combine(outside, "00000001.json");
        File.WriteAllText(sentinel, "outside-sentinel");
        var store = new UpgradeJournal(Scope(host), Id);
        Directory.CreateDirectory(Path.GetDirectoryName(store.DirectoryPath)!);
        var result = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", store.DirectoryPath, outside],
            Environment.CurrentDirectory, null, TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(UpgradeJournalAvailability.Unavailable, store.Read().Availability);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(null, Progress(host), TestContext.Current.CancellationToken));
        Assert.Equal("outside-sentinel", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task ForeignRootCopyAndConcurrentWriterCannotReplaceEvidence()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var other = temp.CreateDirectory("other");
        var store = new UpgradeJournal(Scope(host), Id);
        var state = (await store.SaveAsync(null, Progress(host), TestContext.Current.CancellationToken)).Progress!;
        var foreign = new UpgradeJournal(Scope(other), Id);
        Directory.CreateDirectory(foreign.DirectoryPath);
        var original = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "00000001.json"));
        File.WriteAllBytes(Path.Combine(foreign.DirectoryPath, "00000001.json"), original);
        Assert.Equal(UpgradeJournalAvailability.Unavailable, foreign.Read().Availability);
        await Assert.ThrowsAsync<InvalidDataException>(() => foreign.SaveAsync(null, Progress(other), TestContext.Current.CancellationToken));
        using (var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(store.DirectoryPath, "writer.lock"), false))
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(1, state, TestContext.Current.CancellationToken));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(store.DirectoryPath, "00000001.json")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(foreign.DirectoryPath, "00000001.json")));
    }
}
