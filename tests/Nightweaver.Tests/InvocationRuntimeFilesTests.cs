using System.Text;
using Nightweaver;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class InvocationRuntimeFilesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(170)]
    [InlineData(290)]
    public async Task ReservesPinsAndReadsTheSameLogAcrossLegacyPathLimits(int minimumHostLength)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "host with spaces");
        while (root.Length < minimumHostLength) root = Path.Combine(root, "nested host");
        var paths = new HostPaths(root);
        var project = ProjectSlug.Parse("runtime-paths");
        const string beadId = "ns-135";
        var invocation = CreateInvocation(paths, project, beadId);
        if (minimumHostLength >= 290)
            Assert.True(invocation.LogFilePath.Length > 260, $"Expected a legacy-limit-crossing log path, got {invocation.LogFilePath.Length} characters.");
        Directory.CreateDirectory(invocation.WorkingDirectory);

        using (var files = InvocationRuntimeFiles.Reserve(paths, project, beadId, invocation))
        {
            Assert.Equal(invocation.TemporaryDirectory, files.TemporaryDirectory);
            Assert.Equal(invocation.LogFilePath, files.LogFilePath);
            Assert.True(Directory.Exists(files.TemporaryDirectory));
            await files.LogStream.WriteAsync(Encoding.UTF8.GetBytes("settled log sentinel"), TestContext.Current.CancellationToken);
            await files.FlushLogAsync(TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
            Assert.Throws<BeadEnvironmentException>(() => InvocationRuntimeFiles.Reserve(paths, project, beadId, invocation));
        }

        using (var settled = InvocationRuntimeFiles.OpenExistingSettledLogForRead(
            paths, project, beadId, invocation.InvocationId, invocation.LogFilePath))
        using (var reader = new StreamReader(settled.Stream))
            Assert.Equal("settled log sentinel", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        // Removing the temp collision must still leave CREATE_NEW protection for the existing log.
        Directory.Delete(invocation.TemporaryDirectory);
        Assert.Throws<BeadEnvironmentException>(() => InvocationRuntimeFiles.Reserve(paths, project, beadId, invocation));
        Assert.Equal("settled log sentinel", await File.ReadAllTextAsync(invocation.LogFilePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(290)]
    public async Task PinsSharedRuntimeFileBoundariesAcrossShortAndDeepRoots(int minimumHostLength)
    {
        Assert.True(OperatingSystem.IsWindows(), "Runtime native file boundaries use Windows no-follow handles.");
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "host with spaces");
        while (root.Length < minimumHostLength) root = Path.Combine(root, "nested host");
        var paths = new HostPaths(root);
        var project = ProjectSlug.Parse("runtime-paths");
        var payload = Encoding.UTF8.GetBytes("runtime boundary sentinel");
        const string runId = "ns135-runtime-boundary";

        var ownerPath = Path.Combine(paths.BackgroundRoot(project), "owner.json");
        await InvocationRuntimeFiles.WriteRuntimeFileAtomicallyAsync(ownerPath, payload, TestContext.Current.CancellationToken);
        Assert.Equal(payload, InvocationRuntimeFiles.ReadRuntimeFileBounded(ownerPath, 128));

        var lockPath = Path.Combine(paths.BackgroundRoot(project), "owner.lock");
        using (InvocationRuntimeFiles.OpenExclusiveRuntimeFile(lockPath, createMissingDirectories: true))
        {
            Assert.True(File.Exists(lockPath));
            Assert.Throws<IOException>(() => InvocationRuntimeFiles.OpenExclusiveRuntimeFile(lockPath, createMissingDirectories: false));
        }

        var diagnosticLogPath = Path.Combine(paths.LogsRoot(project), "background-host.log");
        using (var log = InvocationRuntimeFiles.OpenAppendRuntimeLog(diagnosticLogPath))
        {
            log.Stream.Write(payload);
            log.Stream.Flush();
        }
        Assert.Equal(payload, await File.ReadAllBytesAsync(diagnosticLogPath, TestContext.Current.CancellationToken));

        var snapshotPath = paths.FactoryTelemetryPath(project);
        if (minimumHostLength >= 290)
            Assert.True(snapshotPath.Length > 260, $"Expected a legacy-limit-crossing snapshot path, got {snapshotPath.Length} characters.");
        await InvocationRuntimeFiles.WriteFactorySnapshotAtomicallyAsync(
            paths, project, runId, payload, TestContext.Current.CancellationToken);
        using var snapshot = InvocationRuntimeFiles.OpenExistingFactorySnapshotForRead(paths, project);
        using var contents = new MemoryStream();
        await snapshot.Stream.CopyToAsync(contents, TestContext.Current.CancellationToken);
        Assert.Equal(payload, contents.ToArray());
    }

    [Theory]
    [InlineData("trailing ")]
    [InlineData("NUL")]
    [InlineData("COM¹")]
    public void ExtendedNativePathsDoNotAdmitUnsafeWindowsSegments(string segment)
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(Path.Combine(temp.Path, segment, "host"));
        var project = ProjectSlug.Parse("runtime-paths");
        var invocation = CreateInvocation(paths, project, "ns-135");

        Assert.Throws<InvalidDataException>(() => InvocationRuntimeFiles.Reserve(paths, project, "ns-135", invocation));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    [InlineData("//?/")]
    public void RejectsCallerSuppliedDeviceNamespaces(string prefix)
    {
        using var temp = new TempDirectory();
        var paths = new HostPaths(prefix + temp.Path);
        var project = ProjectSlug.Parse("runtime-paths");
        var invocation = CreateInvocation(paths, project, "ns-135");

        Assert.Throws<InvalidDataException>(() => InvocationRuntimeFiles.Reserve(paths, project, "ns-135", invocation));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    private static PreparedAgentInvocation CreateInvocation(HostPaths paths, ProjectSlug project, string beadId)
    {
        const string invocationId = "20261108T1513389140204Z-implementer";
        var tmp = Path.Combine(paths.TmpRoot(project), beadId, invocationId);
        var log = Path.Combine(paths.LogsRoot(project), beadId, invocationId + ".log");
        return new PreparedAgentInvocation("unused.exe", [], Path.Combine(paths.WorktreesRoot(project), beadId),
            "payload", new Dictionary<string, string>
            {
                ["NIGHTWEAVER_BEAD_ID"] = beadId, ["TMP"] = tmp, ["TEMP"] = tmp, ["TMPDIR"] = tmp
            }, TimeSpan.FromSeconds(10), invocationId, tmp, log);
    }
}
