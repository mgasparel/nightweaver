using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class BackgroundRuntimeFileSafetyTests
{
    [Fact]
    public async Task OwnerStateLocksAndLogsRejectJunctionParentsWithoutTouchingTargets()
    {
        Assert.True(OperatingSystem.IsWindows(), "Background owner files use Windows no-follow handles.");
        using var temp = new TempDirectory();
        var hostRoot = temp.CreateDirectory("host");
        var paths = new HostPaths(hostRoot);
        var project = ProjectSlug.Parse("demo");
        var outside = temp.CreateDirectory("outside state");
        var outsideRecord = Path.Combine(outside, "owner.json");
        var outsideLock = Path.Combine(outside, "owner.lock");
        await File.WriteAllTextAsync(outsideRecord, "state sentinel", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(outsideLock, "lock sentinel", TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(hostRoot, "run", project.Value));
        await CreateJunctionAsync(paths.BackgroundRoot(project), outside);
        var recordPath = BackgroundOwnerRecord.PathFor(paths, project);
        var record = new BackgroundOwnerRecord(1, "starting", "fixture-run-id-123456", 1234,
            DateTimeOffset.UtcNow, hostRoot, Path.Combine(hostRoot, "projects", "demo.json"), project.Value,
            null, null, "http://127.0.0.1:5000", "starting", "starting",
            Path.Combine(hostRoot, "logs", project.Value, "background-host.log"),
            Path.Combine(hostRoot, "logs", project.Value, "background-web.log"), null);

        await Assert.ThrowsAsync<InvalidDataException>(() => BackgroundOwnerRecord.WriteAsync(
            recordPath, record, TestContext.Current.CancellationToken));
        Assert.Null(BackgroundOwnerRecord.TryRead(recordPath));
        Assert.Throws<InvalidDataException>(() => AcquireOwner(paths, project));
        Assert.Equal("state sentinel", await File.ReadAllTextAsync(outsideRecord, TestContext.Current.CancellationToken));
        Assert.Equal("lock sentinel", await File.ReadAllTextAsync(outsideLock, TestContext.Current.CancellationToken));

        var outsideLogs = temp.CreateDirectory("outside logs");
        var outsideLog = Path.Combine(outsideLogs, "background-host.log");
        await File.WriteAllTextAsync(outsideLog, "log sentinel", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(hostRoot, "logs"));
        await CreateJunctionAsync(paths.LogsRoot(project), outsideLogs);

        Assert.Throws<InvalidDataException>(() => OpenHostLog(paths, project));
        Assert.Equal("log sentinel", await File.ReadAllTextAsync(outsideLog, TestContext.Current.CancellationToken));
        Assert.Equal("background-host.log", Path.GetFileName(Assert.Single(Directory.GetFiles(outsideLogs))));
    }

    private static void AcquireOwner(HostPaths paths, ProjectSlug project)
    {
        using var ownerLock = BackgroundFileLock.AcquireOwner(paths, project);
    }

    private static void OpenHostLog(HostPaths paths, ProjectSlug project)
    {
        using var log = InvocationRuntimeFiles.OpenAppendRuntimeLog(
            Path.Combine(paths.LogsRoot(project), "background-host.log"));
    }

    private static async Task CreateJunctionAsync(string junctionPath, string targetPath)
    {
        var result = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", junctionPath, targetPath],
            Environment.CurrentDirectory, null, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0)
            Assert.Skip($"This Windows filesystem cannot create a directory junction: {result.StandardErrorText}{result.StandardOutputText}");
    }
}
