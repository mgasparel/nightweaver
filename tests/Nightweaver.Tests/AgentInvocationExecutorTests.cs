using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Tests.ExecutionCrashHost;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

[CollectionDefinition("Agent invocation executor", DisableParallelization = true)]
public sealed class AgentInvocationExecutorSerialGroup { }

[Collection("Agent invocation executor")]
public sealed class AgentInvocationExecutorTests
{
    private static readonly string[] ExpectedArguments = ["space argument", "quoted\"value", "C:\\trailing\\"];

    [Fact]
    public async Task DeliversExactUtf8ClosesStdinSetsCwdAndOverlaysOnlyRuntimeEnvironment()
    {
        using var fixture = new ExecutionFixture();
        const string payload = "héllo 雪 🌃\nsecond line\n";
        const string ambientName = "NIGHTWEAVER_FIXTURE_AMBIENT";
        const string authName = "NIGHTWEAVER_FIXTURE_AUTH";
        const string slotName = "NIGHTWEAVER_SLOT_ID";
        var oldAmbient = Environment.GetEnvironmentVariable(ambientName);
        var oldAuth = Environment.GetEnvironmentVariable(authName);
        var oldSlot = Environment.GetEnvironmentVariable(slotName);
        Environment.SetEnvironmentVariable(ambientName, "keep-this-value");
        Environment.SetEnvironmentVariable(authName, "synthetic-auth-token");
        Environment.SetEnvironmentVariable(slotName, "ambient-slot");
        var beforeEnvironment = (string?)Environment.GetEnvironmentVariable(ambientName);
        var beforeAuth = (string?)Environment.GetEnvironmentVariable(authName);
        var beforeSlot = (string?)Environment.GetEnvironmentVariable(slotName);
        try
        {
            var invocation = fixture.Invocation("inspect", payload, "space argument", "quoted\"value", "C:\\trailing\\");
            var result = await AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken);

            Assert.Equal(AgentExecutionStatus.Exited, result.Status);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(invocation.LogFilePath, result.LogFilePath);
            Assert.True(Directory.Exists(invocation.TemporaryDirectory));
            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(result.LogFilePath, TestContext.Current.CancellationToken));
            var root = report.RootElement;
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), root.GetProperty("stdinBase64").GetString());
            Assert.Equal(invocation.WorkingDirectory, root.GetProperty("cwd").GetString());
            Assert.Equal("ns-xci-fixture", root.GetProperty("beadId").GetString());
            Assert.Equal(invocation.TemporaryDirectory, root.GetProperty("tmp").GetString());
            Assert.Equal(invocation.TemporaryDirectory, root.GetProperty("temp").GetString());
            Assert.Equal(invocation.TemporaryDirectory, root.GetProperty("tmpdir").GetString());
            Assert.Equal("ambient-slot", root.GetProperty("slotId").GetString());
            Assert.Equal("keep-this-value", root.GetProperty("ambient").GetString());
            Assert.Equal("synthetic-auth-token", root.GetProperty("auth").GetString());
            Assert.Equal(ExpectedArguments,
                root.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.Equal(beforeEnvironment, Environment.GetEnvironmentVariable(ambientName));
            Assert.Equal(beforeAuth, Environment.GetEnvironmentVariable(authName));
            Assert.Equal(beforeSlot, Environment.GetEnvironmentVariable(slotName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ambientName, oldAmbient);
            Environment.SetEnvironmentVariable(authName, oldAuth);
            Environment.SetEnvironmentVariable(slotName, oldSlot);
        }
    }

    [Fact]
    public async Task DrainsLargeStdoutAndStderrAndPreservesNaturalNonzeroExit()
    {
        using var fixture = new ExecutionFixture();
        const int size = 1_200_000;
        var invocation = fixture.Invocation("flood", "payload", size.ToString(CultureInfo.InvariantCulture));
        var result = await AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken);
        var log = await File.ReadAllBytesAsync(result.LogFilePath, TestContext.Current.CancellationToken);

        Assert.Equal(AgentExecutionStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.True(log.Count(value => value == (byte)'O') >= size);
        Assert.True(log.Count(value => value == (byte)'E') >= size);

        var nonzero = fixture.Invocation("exit", "payload", "23");
        var nonzeroResult = await AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, nonzero, TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionStatus.Exited, nonzeroResult.Status);
        Assert.Equal(23, nonzeroResult.ExitCode);
        Assert.Empty(await File.ReadAllBytesAsync(nonzeroResult.LogFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BoundedStdoutCaptureDoesNotLimitCombinedLogDrain()
    {
        using var fixture = new ExecutionFixture();
        const int size = 1_200_000;
        var invocation = fixture.Invocation("flood", "payload", size.ToString(CultureInfo.InvariantCulture));

        var captured = await AgentInvocationExecutor.ExecuteWithStandardOutputCaptureAsync(
            fixture.Paths, fixture.Project, invocation, 64, TestContext.Current.CancellationToken);
        var log = await File.ReadAllBytesAsync(captured.Execution.LogFilePath, TestContext.Current.CancellationToken);

        Assert.Equal(AgentExecutionStatus.Exited, captured.Execution.Status);
        Assert.Equal(0, captured.Execution.ExitCode);
        Assert.True(captured.Overflowed);
        Assert.Equal(64, captured.StandardOutput.Length);
        Assert.All(captured.StandardOutput, value => Assert.Equal((byte)'O', value));
        Assert.True(log.Count(value => value == (byte)'O') >= size);
        Assert.True(log.Count(value => value == (byte)'E') >= size);
    }

    [Fact]
    public async Task EarlyStdinClosureFailsDeliveryAndKeepsAvailableOutput()
    {
        using var fixture = new ExecutionFixture();
        var invocation = fixture.Invocation("close-stdin", new string('x', 8 * 1024 * 1024));
        var exception = await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken));

        Assert.Contains("stdin", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stdin-closed", await File.ReadAllTextAsync(invocation.LogFilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutDuringBlockedStdinTerminatesTheJobAndRetainsLog()
    {
        using var fixture = new ExecutionFixture();
        var ready = Path.Combine(fixture.Temp.Path, "child ready.txt");
        var invocation = fixture.Invocation("never-read", new string('p', 16 * 1024 * 1024), TimeSpan.FromSeconds(3), ready);
        var execution = AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken);
        await WaitForFileAsync(ready, TimeSpan.FromSeconds(10));

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.True(File.Exists(result.LogFilePath));
        Assert.True(File.Exists(ready));
    }

    [Fact]
    public async Task CallerCancellationWaitsForProcessTerminationAndUsesCallerToken()
    {
        using var fixture = new ExecutionFixture();
        var ready = Path.Combine(fixture.Temp.Path, "cancel tree ready.txt");
        var release = Path.Combine(fixture.Temp.Path, "cancel tree release.txt");
        var invocation = fixture.Invocation("spawn-hold", "small payload", TimeSpan.FromMinutes(2), ready, release, "0");
        using var cancellation = new CancellationTokenSource();
        var execution = AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, cancellation.Token);
        await WaitForFileAsync(ready, TimeSpan.FromSeconds(10));
        await WaitForFileAsync(ready + ".child", TimeSpan.FromSeconds(10));
        using var root = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready + ".root", TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready + ".child", TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
        using var grandchild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready, TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
        Assert.False(root.HasExited);
        Assert.False(child.HasExited);
        Assert.False(grandchild.HasExited);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(root.HasExited);
        Assert.True(child.HasExited);
        Assert.True(grandchild.HasExited);
        Assert.True(File.Exists(invocation.LogFilePath));
    }

    [Fact]
    public async Task CancellingOneJobLeavesAnIndependentInvocationAlive()
    {
        using var firstFixture = new ExecutionFixture();
        using var secondFixture = new ExecutionFixture();
        var firstReady = Path.Combine(firstFixture.Temp.Path, "first ready.txt");
        var secondReady = Path.Combine(secondFixture.Temp.Path, "second ready.txt");
        var firstInvocation = firstFixture.Invocation("never-read", "first", TimeSpan.FromMinutes(2), firstReady);
        var secondInvocation = secondFixture.Invocation("never-read", "second", TimeSpan.FromMinutes(2), secondReady);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var firstExecution = AgentInvocationExecutor.ExecuteAsync(firstFixture.Paths, firstFixture.Project, firstInvocation, firstCancellation.Token);
        var secondExecution = AgentInvocationExecutor.ExecuteAsync(secondFixture.Paths, secondFixture.Project, secondInvocation, secondCancellation.Token);
        try
        {
            await WaitForFileAsync(firstReady, TimeSpan.FromSeconds(10));
            await WaitForFileAsync(secondReady, TimeSpan.FromSeconds(10));
            using var firstProcess = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(firstReady, TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
            using var secondProcess = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(secondReady, TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstExecution.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.True(firstProcess.HasExited);
            Assert.False(secondProcess.HasExited);
        }
        finally
        {
            firstCancellation.Cancel();
            secondCancellation.Cancel();
            await ObserveCancellationAsync(firstExecution);
            await ObserveCancellationAsync(secondExecution);
        }
    }

    [Fact]
    public async Task NaturalRootExitKillsAReadyGrandchildThatKeepsOutputHandlesOpen()
    {
        using var fixture = new ExecutionFixture();
        var ready = Path.Combine(fixture.Temp.Path, "grandchild ready.txt");
        var release = Path.Combine(fixture.Temp.Path, "release root.txt");
        var invocation = fixture.Invocation("spawn-hold", "payload", TimeSpan.FromSeconds(15), ready, release, "7");
        var execution = AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken);
        await WaitForFileAsync(ready, TimeSpan.FromSeconds(10));
        await WaitForFileAsync(ready + ".child", TimeSpan.FromSeconds(10));
        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready + ".child", TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
        using var grandchild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready, TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
        Assert.False(grandchild.HasExited);
        Assert.False(child.HasExited);
        await File.WriteAllTextAsync(release, "release", TestContext.Current.CancellationToken);

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionStatus.Exited, result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.True(grandchild.HasExited);
        Assert.True(child.HasExited);
        Assert.Contains("root-exit", await File.ReadAllTextAsync(result.LogFilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrchestratorProcessCrashClosesJobAndKillsAgentWithoutManagedCleanup()
    {
        using var fixture = new ExecutionFixture();
        var ready = Path.Combine(fixture.Temp.Path, "crash child ready.txt");
        var invocationId = ExecutionFixture.NewInvocationId();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CrashHostMarker).Assembly.Location);
        start.ArgumentList.Add(fixture.HostRoot);
        start.ArgumentList.Add(fixture.Project.Value);
        start.ArgumentList.Add("ns-xci-fixture");
        start.ArgumentList.Add(invocationId);
        start.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location);
        start.ArgumentList.Add(ready);
        using var orchestrator = Process.Start(start) ?? throw new InvalidOperationException("Could not start crash-host fixture.");
        var stdout = orchestrator.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = orchestrator.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Process? agent = null;
        try
        {
            await WaitForFileAsync(ready, TimeSpan.FromSeconds(15));
            agent = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready, TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
            Assert.False(agent.HasExited);

            orchestrator.Kill();
            await orchestrator.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await agent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(agent.HasExited);
            _ = await stdout;
            _ = await stderr;
        }
        finally
        {
            if (!orchestrator.HasExited)
            {
                orchestrator.Kill();
                orchestrator.WaitForExit();
            }
            if (agent is not null && !agent.HasExited)
            {
                agent.Kill();
                agent.WaitForExit();
            }
            agent?.Dispose();
        }
    }

    [Fact]
    public async Task PreCancellationDoesNotReserveRuntimePaths()
    {
        using var fixture = new ExecutionFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, fixture.Invocation("inspect", "payload"), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.TmpRoot(fixture.Project), "ns-xci-fixture")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.LogsRoot(fixture.Project), "ns-xci-fixture")));
    }

    [Fact]
    public async Task ExistingLogIsNeverTruncatedAndTmpCollisionNeverSpawns()
    {
        using var fixture = new ExecutionFixture();
        var logCollision = fixture.Invocation("inspect", "payload");
        Directory.CreateDirectory(Path.GetDirectoryName(logCollision.LogFilePath)!);
        const string sentinel = "do not overwrite";
        await File.WriteAllTextAsync(logCollision.LogFilePath, sentinel, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, logCollision, TestContext.Current.CancellationToken));
        Assert.Equal(logCollision.InvocationId, exception.InvocationId);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(logCollision.LogFilePath, TestContext.Current.CancellationToken));

        var tmpCollision = fixture.Invocation("inspect", "payload");
        Directory.CreateDirectory(tmpCollision.TemporaryDirectory);
        await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, tmpCollision, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReparsePointInRuntimePathFailsBeforeProcessLaunch()
    {
        using var fixture = new ExecutionFixture();
        var target = fixture.Temp.CreateDirectory("external tmp target");
        var tmpRoot = Path.Combine(fixture.HostRoot, "tmp");
        Directory.CreateDirectory(tmpRoot);
        var projectTmp = Path.Combine(tmpRoot, fixture.Project.Value);
        try
        {
            CreateDirectoryJunction(projectTmp, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            Assert.Skip($"This Windows filesystem cannot create a directory junction for the reparse-point integration check: {exception.Message}");
        }

        var invocation = fixture.Invocation("inspect", "payload");
        await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Directory.Delete(projectTmp);

        var logTarget = fixture.Temp.CreateDirectory("external log target");
        var logsRoot = Path.Combine(fixture.HostRoot, "logs");
        Directory.CreateDirectory(logsRoot);
        var projectLogs = Path.Combine(logsRoot, fixture.Project.Value);
        CreateDirectoryJunction(projectLogs, logTarget);
        var logInvocation = fixture.Invocation("inspect", "payload");
        await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, logInvocation, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(logTarget));
        Directory.Delete(projectLogs);
    }

    [Fact]
    public async Task ConcurrentUseOfOnePreparedIdentityLaunchesAtMostOneProcess()
    {
        using var fixture = new ExecutionFixture();
        var invocation = fixture.Invocation("inspect", "payload");
        var first = AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken);
        var second = AgentInvocationExecutor.ExecuteAsync(fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken);
        var outcomes = await Task.WhenAll(CaptureAsync(first), CaptureAsync(second));

        Assert.Single(outcomes, outcome => outcome is AgentExecutionResult);
        Assert.Single(outcomes, outcome => outcome is AgentInvocationExecutionException);
        Assert.True(File.Exists(invocation.LogFilePath));
    }

    [Fact]
    public async Task MissingExecutableRetainsContextAndReservedLog()
    {
        using var fixture = new ExecutionFixture();
        var invocation = ExecutionFixture.WithExecutable(fixture.Invocation("inspect", "payload"), "nightweaver-missing-agent-cli-77e2.exe");
        var exception = await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken));
        Assert.Contains(invocation.InvocationId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(invocation.LogFilePath, exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(invocation.LogFilePath));
    }

    [Fact]
    public async Task BatchShimIsRejectedWithoutRunningThroughAShell()
    {
        using var fixture = new ExecutionFixture();
        var shimRoot = fixture.Temp.CreateDirectory("agent shim directory");
        await File.WriteAllTextAsync(Path.Combine(shimRoot, "fixture-agent.cmd"), "@echo off", TestContext.Current.CancellationToken);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", shimRoot);
        try
        {
            var invocation = ExecutionFixture.WithExecutable(fixture.Invocation("inspect", "payload"), "fixture-agent");
            var exception = await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
                fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken));
            Assert.Contains(".cmd/.bat shim", exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(invocation.LogFilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Fact]
    public async Task PreparedWorkingDirectoryMustMatchCanonicalBeadWorktree()
    {
        using var fixture = new ExecutionFixture();
        var invocation = ExecutionFixture.WithWorkingDirectory(fixture.Invocation("inspect", "payload"), fixture.HostRoot);
        var exception = await Assert.ThrowsAsync<AgentInvocationExecutionException>(() => AgentInvocationExecutor.ExecuteAsync(
            fixture.Paths, fixture.Project, invocation, TestContext.Current.CancellationToken));

        Assert.Contains("does not match the canonical HostPaths layout", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.TmpRoot(fixture.Project), "ns-xci-fixture")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.LogsRoot(fixture.Project), "ns-xci-fixture")));
    }

    private static async Task<object> CaptureAsync(Task<AgentExecutionResult> task)
    {
        try { return await task; }
        catch (AgentInvocationExecutionException exception) { return exception; }
    }

    private static async Task ObserveCancellationAsync(Task<AgentExecutionResult> task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
        catch (OperationCanceledException) { }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException($"Fixture readiness file was not created: {path}");
            await Task.Delay(20);
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(linkPath);
        using var handle = CreateFileW(linkPath, 0x40000000, 0x00000007, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Opening junction directory failed");

        var printName = Path.GetFullPath(targetPath);
        var substituteName = @"\??\" + printName;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var dataLength = 8 + substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char);
        var buffer = new byte[8 + dataLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0xA0000003);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)dataLength));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + sizeof(char))));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer.AsSpan(16));
        printBytes.CopyTo(buffer.AsSpan(16 + substituteBytes.Length + sizeof(char)));
        if (!DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Creating directory junction failed");
    }

    private sealed class ExecutionFixture : IDisposable
    {
        private static long nextInvocation;

        internal ExecutionFixture()
        {
            Temp = new TempDirectory();
            HostRoot = Temp.CreateDirectory("host with spaces");
            Paths = new HostPaths(HostRoot);
            Project = ProjectSlug.Parse("fixture-project");
            Worktree = Directory.CreateDirectory(Path.Combine(Paths.WorktreesRoot(Project), "ns-xci-fixture")).FullName;
        }

        internal TempDirectory Temp { get; }
        internal string HostRoot { get; }
        internal HostPaths Paths { get; }
        internal ProjectSlug Project { get; }
        internal string Worktree { get; }

        internal PreparedAgentInvocation Invocation(string mode, string payload, params string[] extraArguments) =>
            CreateInvocation(mode, payload, TimeSpan.FromSeconds(20), extraArguments);

        internal PreparedAgentInvocation Invocation(string mode, string payload, TimeSpan timeout, params string[] extraArguments) =>
            CreateInvocation(mode, payload, timeout, extraArguments);

        internal static PreparedAgentInvocation WithExecutable(PreparedAgentInvocation invocation, string executable) =>
            new(executable, invocation.Arguments, invocation.WorkingDirectory, invocation.StandardInput,
                invocation.EnvironmentOverrides, invocation.Timeout, invocation.InvocationId,
                invocation.TemporaryDirectory, invocation.LogFilePath);

        internal static PreparedAgentInvocation WithWorkingDirectory(PreparedAgentInvocation invocation, string workingDirectory) =>
            new(invocation.Executable, invocation.Arguments, workingDirectory, invocation.StandardInput,
                invocation.EnvironmentOverrides, invocation.Timeout, invocation.InvocationId,
                invocation.TemporaryDirectory, invocation.LogFilePath);

        private PreparedAgentInvocation CreateInvocation(string mode, string payload, TimeSpan timeout, string[] extraArguments)
        {
            var invocationId = NewInvocationId();
            var tmp = Path.GetFullPath(Path.Combine(Paths.TmpRoot(Project), "ns-xci-fixture", invocationId));
            var log = Path.GetFullPath(Path.Combine(Paths.LogsRoot(Project), "ns-xci-fixture", invocationId + ".log"));
            var args = new[] { typeof(FixtureMarker).Assembly.Location, mode }.Concat(extraArguments).ToArray();
            return new PreparedAgentInvocation("dotnet", args, Worktree, payload,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NIGHTWEAVER_BEAD_ID"] = "ns-xci-fixture",
                    ["TMP"] = tmp,
                    ["TEMP"] = tmp,
                    ["TMPDIR"] = tmp
                }, timeout, invocationId, tmp, log);
        }

        internal static string NewInvocationId() => DateTime.UtcNow.AddTicks(Interlocked.Increment(ref nextInvocation))
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-implementer";

        public void Dispose() => Temp.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint controlCode,
        byte[] inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
