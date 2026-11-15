using System.Diagnostics;

namespace Nightweaver.Tests;

public sealed class WindowsConsoleDescendantSettlementTests : AgentStageIntegrationTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalSettlesNativeDoltLockHolderAfterRootExit(bool logOutput)
    {
        using var target = new AgentStageTargetFixture();
        var identity = Path.Combine(target.RootPath, "dolt-lock-holder.pid");
        var lockReady = identity + ".lock-ready";
        var rootRelease = identity + ".release-root";
        var lockPath = Path.Combine(target.Path, ".beads", "embeddeddolt", "stage", ".dolt", "noms", "LOCK");
        var lockBeforeSpawn = InspectLockBeforeSpawn(lockPath);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Pre-spawn Dolt LOCK: {lockBeforeSpawn.State}; existing dolt processes: {string.Join(", ", DescribeDoltProcesses())}");
        Assert.True(lockBeforeSpawn.IsAvailable,
            $"The fixture Dolt LOCK must not already have a holder before the child starts. "
            + $"preflight={lockBeforeSpawn.State}; existing dolt processes={string.Join(", ", DescribeDoltProcesses())}");
        var rootTimer = Stopwatch.StartNew();
        using var runtime = WindowsConsoleProcess.Start(target.AgentFixtureExecutablePath,
            ["spawn-dolt-lock-exit", target.Path, identity, lockReady, rootRelease], target.Path, target.CreateRuntimeEnvironment(),
            logOutput ? Path.Combine(target.RootPath, "runtime.log") : null);
        Process? child = null;
        try
        {
            child = await WaitForNativeChildAsync(identity, runtime, rootTimer, TestContext.Current.CancellationToken);
            var childStart = child.StartTime.ToUniversalTime();
            await WaitForFileAsync(lockReady, identity, runtime, rootTimer, TestContext.Current.CancellationToken);
            Assert.False(child.HasExited,
                $"Native Dolt child must remain alive while the fixture root waits for release. child={DescribeChild(child, childStart)}");
            var lockBeforeRelease = InspectLockBeforeSpawn(lockPath);
            Assert.False(lockBeforeRelease.IsAvailable,
                $"Native Dolt child must hold LOCK before the fixture root is released. child={DescribeChild(child, childStart)}; lock={lockBeforeRelease.State}");
            Assert.Throws<IOException>(() => File.Delete(lockPath));
            File.WriteAllText(rootRelease, "test retained the native child handle");

            var remainingRootWait = TimeSpan.FromSeconds(15) - rootTimer.Elapsed;
            Assert.True(remainingRootWait > TimeSpan.Zero,
                $"Fixture root did not reach the release handshake within 15 seconds. child={DescribeChild(child, childStart)}; diagnostics={ReadChildFailure(identity)}");
            var rootExitCode = await runtime.WaitAsync(remainingRootWait, TestContext.Current.CancellationToken);
            Assert.True(rootExitCode == 0,
                $"Fixture root exit should be successful; child={DescribeChild(child, childStart)}; "
                + $"child diagnostics={ReadChildFailure(identity)}; log={runtime.OutputLogPath ?? "(no runtime log)"}.");
            var lockAfterRoot = InspectLockBeforeSpawn(lockPath);
            Assert.False(child.HasExited,
                $"Runtime root exit must not imply settlement of native Dolt operations. child={DescribeChild(child, childStart)}; lock={lockAfterRoot.State}");
            Assert.False(lockAfterRoot.IsAvailable,
                $"Native Dolt child must hold LOCK after root exit. child={DescribeChild(child, childStart)}; lock={lockAfterRoot.State}");
            Assert.Throws<IOException>(() => File.Delete(lockPath));

            runtime.Dispose();

            Assert.True(child.WaitForExit(0),
                $"Console fixture disposal must settle its native Dolt descendants before repository deletion. child={DescribeChild(child, childStart)}");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Native Dolt lock-holder settled: {DescribeChild(child, childStart)}; lock={lockPath}; preflight={lockBeforeSpawn.State}; log={runtime.OutputLogPath ?? "(no runtime log)"}.");
            File.Delete(lockPath);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
                child.Dispose();
            }
        }
    }

    private static async Task<Process> WaitForNativeChildAsync(string identityPath, WindowsConsoleProcess runtime,
        Stopwatch rootTimer, CancellationToken cancellationToken)
    {
        while (!File.Exists(identityPath))
        {
            if (runtime.HasExited)
                throw new InvalidOperationException($"Fixture root exited before publishing the Dolt child identity (exit={runtime.ExitCode}, diagnostics={ReadChildFailure(identityPath)}, log={runtime.OutputLogPath ?? "(no runtime log)"}).");
            if (rootTimer.Elapsed >= TimeSpan.FromSeconds(15))
                throw new TimeoutException($"Fixture root did not publish the Dolt child identity within 15 seconds (log={runtime.OutputLogPath ?? "(no runtime log)"}).");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        var identity = File.ReadAllText(identityPath).Split('|');
        if (identity.Length != 2
            || !int.TryParse(identity[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId)
            || !long.TryParse(identity[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var startTimeTicks))
            throw new InvalidDataException($"Invalid native Dolt child identity '{File.ReadAllText(identityPath)}'.");

        Process child;
        try { child = Process.GetProcessById(processId); }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Native Dolt child exited before the test could retain its process handle (pid={processId}, startTicks={startTimeTicks}, rootExited={runtime.HasExited}, rootExit={(runtime.HasExited ? runtime.ExitCode : null)}, diagnostics={ReadChildFailure(identityPath)}, log={runtime.OutputLogPath ?? "(no runtime log)"}).", exception);
        }

        if (child.StartTime.ToUniversalTime().Ticks != startTimeTicks)
        {
            var actualStart = child.StartTime.ToUniversalTime().Ticks;
            child.Dispose();
            throw new InvalidOperationException($"Native Dolt PID {processId} was reused before its handle was retained (expectedStartTicks={startTimeTicks}, actualStartTicks={actualStart}).");
        }
        _ = child.SafeHandle;
        return child;
    }

    private static async Task WaitForFileAsync(string path, string identityPath, WindowsConsoleProcess runtime,
        Stopwatch rootTimer, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            if (runtime.HasExited)
                throw new InvalidOperationException(
                    $"Fixture root exited before publishing the lock-ready handshake (exit={runtime.ExitCode}, diagnostics={ReadChildFailure(identityPath)}, log={runtime.OutputLogPath ?? "(no runtime log)"}).");
            if (rootTimer.Elapsed >= TimeSpan.FromSeconds(15))
                throw new TimeoutException($"Fixture root did not publish the lock-ready handshake within 15 seconds (diagnostics={ReadChildFailure(identityPath)}, log={runtime.OutputLogPath ?? "(no runtime log)"}).");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static (bool IsAvailable, string State) InspectLockBeforeSpawn(string lockPath)
    {
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return (true, "present and exclusively openable");
        }
        catch (FileNotFoundException) { return (true, "absent before child start"); }
        catch (DirectoryNotFoundException) { return (true, "absent before child start"); }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) == 32)
        { return (false, $"sharing violation before child start (HResult=0x{exception.HResult:X8})"); }
    }

    private static string[] DescribeDoltProcesses()
    {
        var rows = new List<string>();
        foreach (var process in Process.GetProcessesByName("dolt"))
        {
            using (process)
            {
                try { rows.Add($"pid={process.Id},start={process.StartTime.ToUniversalTime():O}"); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                { rows.Add($"pid={process.Id},identity-unavailable={exception.GetType().Name}"); }
            }
        }
        return rows.ToArray();
    }

    private static string DescribeChild(Process child, DateTime childStart)
    {
        var exited = child.HasExited;
        return $"pid={child.Id},start={childStart:O},exited={exited},exitCode={(exited ? child.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(alive)")}";
    }

    private static string ReadChildFailure(string identityPath)
    {
        var failurePath = identityPath + ".failure.json";
        return File.Exists(failurePath) ? File.ReadAllText(failurePath) : "(none)";
    }
}
