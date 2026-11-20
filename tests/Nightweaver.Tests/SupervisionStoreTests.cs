using System.Text.Json.Nodes;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Supervision;

namespace Nightweaver.Tests;

public sealed class SupervisionStoreTests
{
    private static SupervisionStore Store(string root) => new(new HostPaths(root), ProjectSlug.Parse("demo"), SupervisionPolicyTests.Identity(root));

    [Fact]
    public async Task MissingStoreIsReadOnlyManualAndReservationSurvivesReloadAndCommands()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "host");
        var store = Store(root);
        Assert.Equal(SupervisionAvailability.Manual, store.Read().Availability);
        Assert.False(Directory.Exists(root));
        var state = await store.SaveAsync(null, SupervisionState.Manual(SupervisionPolicyTests.Identity(root)), TestContext.Current.CancellationToken);
        Assert.False(state.Enabled);
        Assert.False(state.DesiredRunning);
        Assert.Equal(1, state.Revision);
        state = SupervisionPolicy.SetIntent(state, true, true, null);
        var epoch = Guid.NewGuid().ToString("N");
        for (var i = 0; i <= 2; i++)
            state = SupervisionPolicy.Observe(state, new(epoch, TimeSpan.FromMinutes(i).Ticks,
                SupervisionPolicyTests.Start.AddMinutes(i), SupervisionHealth.Failed, null));
        state = await store.SaveAsync(1, state, TestContext.Current.CancellationToken);
        var reloaded = Store(root).Read().State!;
        Assert.Equal(state.NextDueUtc, reloaded.NextDueUtc);
        var reservation = SupervisionPolicy.Reserve(reloaded, reloaded.LastObservation!, Guid.NewGuid().ToString("N"));
        state = await store.SaveAsync(2, reservation, TestContext.Current.CancellationToken);
        reloaded = Store(root).Read().State!;
        Assert.Equal(state.PendingAttempt, reloaded.PendingAttempt);
        Assert.Equal(1, reloaded.Attempts);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reserve(reloaded, reloaded.LastObservation!, Guid.NewGuid().ToString("N")));
        var stopped = SupervisionPolicy.SetIntent(reloaded, false, false, Guid.NewGuid().ToString("N"));
        await store.SaveAsync(3, stopped, TestContext.Current.CancellationToken);
        Assert.Equal(state.PendingAttempt, Store(root).Read().State!.PendingAttempt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(3, stopped, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(new HostPaths(root).SupervisionRoot(ProjectSlug.Parse("demo")), "*.json"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"enabled\":true")]
    public async Task MalformedStateNeverBecomesFreshOptInOrGetsOverwritten(string contents)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        await File.WriteAllTextAsync(store.Path, contents, TestContext.Current.CancellationToken);
        Assert.Equal(SupervisionAvailability.Unavailable, store.Read().Availability);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(null,
            SupervisionState.Manual(SupervisionPolicyTests.Identity(temp.Path)), TestContext.Current.CancellationToken));
        Assert.Equal(contents, await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("foreign-root")]
    [InlineData("foreign-config")]
    [InlineData("foreign-repository")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("attempts")]
    [InlineData("enum")]
    [InlineData("oversized")]
    [InlineData("interrupted")]
    [InlineData("locked")]
    public async Task ForeignIncompleteUnsafeAndInaccessibleEvidenceFailsClosed(string kind)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.SaveAsync(null, SupervisionState.Manual(SupervisionPolicyTests.Identity(temp.Path)), TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(store.Path, TestContext.Current.CancellationToken);
        var node = JsonNode.Parse(original)!;
        string contents = original;
        switch (kind)
        {
            case "foreign": node["identity"]!["userSid"] = "S-1-5-21-1002"; break;
            case "foreign-root": node["identity"]!["root"] = Path.Combine(temp.Path, "other"); break;
            case "foreign-config": node["identity"]!["configPath"] = Path.Combine(temp.Path, "other.json"); break;
            case "foreign-repository": node["identity"]!["gitCommonDirectory"] = Path.Combine(temp.Path, "other", ".git"); break;
            case "missing": node.AsObject().Remove("attempts"); break;
            case "duplicate": contents = original.Insert(1, "\"Enabled\":true,"); break;
            case "unknown": node["beadStage"] = "implement"; break;
            case "attempts": node["attempts"] = -1; break;
            case "enum": node["lastObservation"] = JsonNode.Parse("{\"epoch\":\"00000000000000000000000000000000\",\"monotonicTicks\":0,\"utc\":\"2026-11-20T12:00:00Z\",\"health\":99,\"owner\":null}"); break;
            case "oversized": contents = new string(' ', SupervisionStore.MaximumBytes + 1); break;
            case "interrupted": await File.WriteAllTextAsync(store.Path + ".retained.tmp", "partial", TestContext.Current.CancellationToken); break;
            case "locked": break;
        }
        if (contents == original) contents = node.ToJsonString();
        await File.WriteAllTextAsync(store.Path, contents, TestContext.Current.CancellationToken);
        using var locked = kind == "locked" ? new FileStream(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var read = store.Read();
        Assert.Equal(SupervisionAvailability.Unavailable, read.Availability);
        Assert.Null(read.State);
        Assert.Equal(store.Path, read.Path);
        Assert.NotEmpty(read.Diagnostic!);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(1,
            SupervisionState.Manual(SupervisionPolicyTests.Identity(temp.Path)) with { Revision = 1 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExhaustionAndUnresolvedEvidenceReloadWithinTheSerializationBudget()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        var now = SupervisionPolicyTests.Start;
        var incident = new SupervisionIncident(Guid.NewGuid().ToString("N"), now, "settlement-unknown", Guid.NewGuid().ToString("N"));
        var state = SupervisionPolicy.SetIntent(SupervisionState.Manual(SupervisionPolicyTests.Identity(temp.Path)), true, true, null) with
        {
            Attempts = 3,
            PendingAttempt = new(Guid.NewGuid().ToString("N"), 1, 3, now, Guid.NewGuid().ToString("N"), null)
        };
        for (var i = 0; i < 40; i++)
            state = SupervisionPolicy.Settle(state, state.PendingAttempt!.Id, now, false, incident with { Id = Guid.NewGuid().ToString("N") });
        await store.SaveAsync(null, state, TestContext.Current.CancellationToken);
        var reload = Store(temp.Path).Read().State!;
        Assert.True(reload.Exhausted);
        Assert.Equal(state.PendingAttempt, reload.PendingAttempt);
        Assert.Equal(state.UnresolvedIncident, reload.UnresolvedIncident);
        Assert.Equal(state.History, reload.History);
        Assert.Equal(8, reload.DiscardedHistory);
        Assert.True(new FileInfo(store.Path).Length < SupervisionStore.MaximumBytes);
        Assert.Throws<InvalidOperationException>(() => SupervisionPolicy.Reset(reload));
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("stream")]
    [InlineData("network")]
    [InlineData("trailing")]
    public void NoncanonicalInstallationPathsAreRejectedBeforeCreatingStorage(string kind)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "absent");
        var identity = SupervisionPolicyTests.Identity(root);
        identity = identity with { ConfigPath = kind switch
        {
            "traversal" => Path.Combine(root, "projects", "..", "demo.json"),
            "stream" => Path.Combine(root, "demo.json:stream"),
            "network" => @"\\server\share\demo.json",
            _ => Path.Combine(root, "unsafe. ", "demo.json")
        } };
        Assert.Throws<InvalidDataException>(() => new SupervisionStore(new HostPaths(root), ProjectSlug.Parse("demo"), identity));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CanceledPublicationPreservesCommittedStateAndExclusiveWriterBlocksCompetitors()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        var state = await store.SaveAsync(null, SupervisionState.Manual(SupervisionPolicyTests.Identity(temp.Path)), TestContext.Current.CancellationToken);
        using (var cancellation = new CancellationTokenSource())
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(1,
                SupervisionPolicy.SetIntent(state, true, true, null), cancellation.Token));
        }
        Assert.False(Store(temp.Path).Read().State!.Enabled);
        using var writer = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(Path.GetDirectoryName(store.Path)!, "writer.lock"), false);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(1, state, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JunctionParentAndDirectoryLeafRejectWritesWithoutChangingOutsideSentinel()
    {
        using var temp = new TempDirectory();
        var host = temp.CreateDirectory("host");
        var outside = temp.CreateDirectory("outside");
        var sentinel = Path.Combine(outside, "state.json");
        await File.WriteAllTextAsync(sentinel, "sentinel", TestContext.Current.CancellationToken);
        var paths = new HostPaths(host);
        var project = ProjectSlug.Parse("demo");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SupervisionRoot(project))!);
        var result = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", paths.SupervisionRoot(project), outside],
            Environment.CurrentDirectory, null, TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        var store = Store(host);
        Assert.Equal(SupervisionAvailability.Unavailable, store.Read().Availability);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(null,
            SupervisionState.Manual(SupervisionPolicyTests.Identity(host)), TestContext.Current.CancellationToken));
        Assert.Equal("sentinel", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFileSystemEntries(outside));

        var otherStore = Store(temp.CreateDirectory("directory-leaf"));
        Directory.CreateDirectory(otherStore.Path);
        Assert.Equal(SupervisionAvailability.Unavailable, otherStore.Read().Availability);
    }
}
