using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class PublishedContinuousStopIntegrationTests(PublishedCliArtifactFixture published)
    : AgentStageIntegrationTestSupport
{

    [Fact]
    public async Task PublishedRuntimeForceStopsAnActiveOutcomeClassifier()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
        var beadId = target.CreateBead("Published force during classification", "task", "stage:implement");
        var hostRoot = CreatePublishedHost(target);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-classifier-force-gates")).FullName;
        var agentStarted = Path.Combine(gateDirectory, "agent.started");
        var agentRelease = Path.Combine(gateDirectory, "agent.release");
        var agentPid = Path.Combine(gateDirectory, "agent.pid");
        var classifierStarted = Path.Combine(gateDirectory, "classifier.started");
        var classifierRelease = Path.Combine(gateDirectory, "classifier.release");
        var classifierPid = Path.Combine(gateDirectory, "classifier.pid");
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var environment = target.CreateRuntimeEnvironment(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = agentStarted,
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = agentRelease,
            ["NIGHTWEAVER_FIXTURE_PID_FILE"] = agentPid,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_STARTED_FILE"] = classifierStarted,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_RELEASE_FILE"] = classifierRelease,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_PID_FILE"] = classifierPid,
            ["OPENAI_API_KEY"] = null
        });
        using var runtime = WindowsConsoleProcess.Start(
            Path.Combine(hostRoot, "nightweaver.exe"), ["--project", "demo"], target.Path, environment, runtimeLog);

        try
        {
            await WaitForFileAsync(agentStarted, runtime, TimeSpan.FromSeconds(90));
            using var agent = ReadFixtureProcess(agentPid);
            File.WriteAllText(agentRelease, "finish before classifier force stop", new UTF8Encoding(false));
            await WaitForFileAsync(classifierStarted, runtime, TimeSpan.FromSeconds(90));
            using var classifier = ReadFixtureProcess(classifierPid);
            Assert.True(agent.WaitForExit(0), "The working Agent must finish before its fallback classifier starts.");
            Assert.False(classifier.HasExited);

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first signal must continue draining the active classifier.");
            Assert.False(classifier.HasExited, "The first signal must leave its allowed current-turn classifier running.");

            target.SendConsoleBreak(runtime.Id);
            Assert.Equal(1, await runtime.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(agent.HasExited);
            Assert.True(classifier.HasExited, "The second signal must close the classifier's Agent Job without waiting for it.");
            Assert.Single(ReadStageLaunchLines(stageLaunchLog));
            Assert.Equal("in_progress", target.ReadBead(beadId).Status);
        }
        finally
        {
            if (!File.Exists(agentRelease)) File.WriteAllText(agentRelease, "test cleanup", new UTF8Encoding(false));
            if (!File.Exists(classifierRelease)) File.WriteAllText(classifierRelease, "test cleanup", new UTF8Encoding(false));
            if (!runtime.HasExited)
            {
                try { target.SendConsoleBreak(runtime.Id); }
                catch (Exception) { }
                try { await runtime.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { }
            }
        }
    }

    private async Task RunPublishedRuntimeForceStopsBlockedNativeBdFinalizationAndRestartRereadsActualStateAsync(
        AgentStageTargetFixture target,
        bool commitAfterForce)
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
        var beadIds = new[]
        {
            target.CreateBead("Published bd finalization and active Agent one", "task", "stage:implement"),
            target.CreateBead("Published bd finalization and active Agent two", "task", "stage:implement")
        };
        var hostRoot = CreatePublishedHost(target, deliveryCapacity: 2);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-bd-force-gates")).FullName;
        var releaseDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-releases")).FullName;
        var agentPidDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-pids")).FullName;
        var agentStartedDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-started")).FullName;
        var agentReadyDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-ready")).FullName;
        var markerDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "worktrees")).FullName;
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var bdStarted = Path.Combine(gateDirectory, "bd.started");
        var bdRelease = Path.Combine(gateDirectory, "bd.release");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var environment = CreateBdShimEnvironment(target, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_DIRECTORY"] = agentStartedDirectory,
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = Path.Combine(releaseDirectory, "default.release"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = releaseDirectory,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = agentPidDirectory,
            ["NIGHTWEAVER_FIXTURE_READY_DIRECTORY"] = agentReadyDirectory,
            ["NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY"] = markerDirectory,
            ["NIGHTWEAVER_FIXTURE_COMMIT_WORKTREE_MARKER"] = "true",
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["NIGHTWEAVER_BD_SHIM_MODE"] = "force-stop-finalization-gate",
            ["NIGHTWEAVER_BD_SHIM_TARGET"] = beadIds[1],
            ["NIGHTWEAVER_BD_SHIM_STARTED_FILE"] = bdStarted,
            ["NIGHTWEAVER_BD_SHIM_RELEASE_FILE"] = bdRelease,
            ["NIGHTWEAVER_BD_SHIM_COMMIT_AFTER_RELEASE"] = commitAfterForce ? "true" : "false",
            ["OPENAI_API_KEY"] = null
        });
        var executable = Path.Combine(hostRoot, "nightweaver.exe");
        var restartMarkerDirectory = Path.Combine(gateDirectory, "restart-observations");
        Directory.CreateDirectory(restartMarkerDirectory);

        using var runtime = WindowsConsoleProcess.Start(executable, ["--project", "demo"], target.Path, environment, runtimeLog);
        RecordRuntimeIdentity(Path.Combine(gateDirectory, "runtime.identity"), runtime);
        Process? bdProcess = null;
        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, beadIds.Length, runtime, TimeSpan.FromSeconds(90));
            var initialAgents = ReadStageLaunchLines(stageLaunchLog).Select(ReadFixtureProcessFromLaunchRecord).ToArray();
            using var remainingAgent = initialAgents.Single(item => item.BeadId == beadIds[0]).Process;
            using var completingAgent = initialAgents.Single(item => item.BeadId == beadIds[1]).Process;
            foreach (var beadId in beadIds)
            {
                var startedPath = Path.Combine(agentStartedDirectory, beadId + ".started");
                await WaitForFileAsync(startedPath, runtime, TimeSpan.FromSeconds(30));
                Assert.Equal(beadId, File.ReadAllText(startedPath));
                await WaitForFileAsync(Path.Combine(agentReadyDirectory, beadId + ".ready"), runtime, TimeSpan.FromSeconds(30));
            }

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited);
            var remainingAgentExited = remainingAgent.HasExited;
            Assert.False(remainingAgentExited,
                remainingAgentExited
                    ? $"The first signal must keep the other Agent running during finalization (exit {remainingAgent.ExitCode}). Runtime output: {ReadRuntimeLog(runtime)}"
                    : "The first signal must keep the other Agent running during finalization.");
            File.WriteAllText(Path.Combine(releaseDirectory, beadIds[1] + ".release"), "complete current turn", new UTF8Encoding(false));

            await WaitForFileAsync(bdStarted, runtime, TimeSpan.FromSeconds(90));
            bdProcess = ReadFixtureProcess(bdStarted);
            Assert.False(remainingAgent.HasExited);
            Assert.False(bdProcess.HasExited);

            target.SendConsoleBreak(runtime.Id);
            Assert.Equal(1, await runtime.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(remainingAgent.HasExited, "Force stop must close the other active Agent Job while bd finalization is blocked.");
            Assert.False(bdProcess.HasExited, "Force exit must not wait for native bd or release its gate.");
            Assert.Equal("in_progress", target.ReadBead(beadIds[1]).Status);
        }
        finally
        {
            File.WriteAllText(Path.Combine(releaseDirectory, beadIds[0] + ".release"), "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(releaseDirectory, beadIds[1] + ".release"), "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(bdRelease, "test cleanup", new UTF8Encoding(false));
            if (!runtime.HasExited)
            {
                try { target.SendConsoleBreak(runtime.Id); }
                catch (Exception) { }
                try { await runtime.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { }
            }

            if (bdProcess is not null)
            {
                try { await bdProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); }
                catch (TimeoutException) { try { bdProcess.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                bdProcess.Dispose();
            }
        }

        var actualBead = target.ReadBead(beadIds[1]);
        if (commitAfterForce)
        {
            Assert.Equal("open", actualBead.Status);
            Assert.Null(actualBead.Assignee);
        }
        else
        {
            Assert.Equal("in_progress", actualBead.Status);
            Assert.Equal("nightweaver:demo", actualBead.Assignee);
        }

        foreach (var beadId in beadIds)
            File.Delete(Path.Combine(releaseDirectory, beadId + ".release"));
        var restartOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-restart-observe",
            ["NIGHTWEAVER_FIXTURE_STARTED_DIRECTORY"] = Directory.CreateDirectory(Path.Combine(gateDirectory, "restart-agent-started")).FullName,
            ["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = releaseDirectory,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = Path.Combine(gateDirectory, "restart-agent-pids"),
            ["NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY"] = restartMarkerDirectory,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["OPENAI_API_KEY"] = null
        };
        Directory.CreateDirectory(restartOverrides["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"]!);
        var restartEnvironment = CreateBdShimEnvironment(target, restartOverrides);
        var restartRuntimeLogPath = Path.Combine(gateDirectory, "restart-runtime.log");
        using var restarted = WindowsConsoleProcess.Start(executable, ["--project", "demo"], target.Path, restartEnvironment,
            restartRuntimeLogPath);
        var restartedStartTicks = RecordRuntimeIdentity(Path.Combine(gateDirectory, "restart-runtime.identity"), restarted);
        var stopLogOffset = 0L;
        (string BeadId, Process Process)[] restartedAgents = [];
        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, beadIds.Length * 2, restarted, TimeSpan.FromSeconds(90));
            restartedAgents = ReadStageLaunchLines(stageLaunchLog).Skip(beadIds.Length)
                .Select(ReadFixtureProcessFromLaunchRecord).ToArray();
            Assert.Equal(beadIds.Length, restartedAgents.Length);
            Assert.Equal(beadIds.OrderBy(id => id, StringComparer.Ordinal),
                restartedAgents.Select(agent => agent.BeadId).OrderBy(id => id, StringComparer.Ordinal));
            foreach (var beadId in beadIds)
            {
                var observationPath = Path.Combine(restartMarkerDirectory, beadId + ".restart");
                try
                {
                    await WaitForFileAsync(observationPath, restarted, TimeSpan.FromSeconds(10));
                }
                catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
                {
                    var launches = File.Exists(stageLaunchLog) ? string.Join(Environment.NewLine, ReadStageLaunchLines(stageLaunchLog)) : "(no stage launch records)";
                    throw new InvalidOperationException(
                        $"Restart observer for {beadId} did not leave its marker. Stage launches: {launches}{Environment.NewLine}Runtime output: {ReadRuntimeLog(restarted)}",
                        exception);
                }
                Assert.EndsWith("|retained=True", File.ReadAllText(observationPath), StringComparison.OrdinalIgnoreCase);
            }

            stopLogOffset = File.Exists(restartRuntimeLogPath) ? new FileInfo(restartRuntimeLogPath).Length : 0;
            var stopDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            target.SendConsoleBreak(restarted.Id);
            await WaitForGracefulStopAcceptedAsync(restarted, restartRuntimeLogPath, stopLogOffset, restartedStartTicks,
                stopDeadline, TestContext.Current.CancellationToken);
            var forceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            if (forceDeadline > stopDeadline)
                forceDeadline = stopDeadline;
            var forceSignalSent = false;
            if (!restarted.HasExited)
            {
                try
                {
                    target.SendConsoleBreak(restarted.Id);
                    forceSignalSent = true;
                }
                catch (InvalidOperationException) when (IsSameOwnedRuntimeExited(restarted, restartedStartTicks))
                {
                }
            }

            var remainingForceBudget = forceDeadline - DateTime.UtcNow;
            if (remainingForceBudget <= TimeSpan.Zero)
                throw new TimeoutException("Restarted runtime did not exit within the five-second force-stop budget.");
            var restartedExitCode = await restarted.WaitAsync(remainingForceBudget, TestContext.Current.CancellationToken);
            Assert.Equal(forceSignalSent ? 1 : 0, restartedExitCode);

            foreach (var agent in restartedAgents)
            {
                remainingForceBudget = forceDeadline - DateTime.UtcNow;
                if (remainingForceBudget <= TimeSpan.Zero)
                    throw new TimeoutException("Restarted runtime's owned Agents did not exit within the five-second force-stop budget.");
                await agent.Process.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(remainingForceBudget, TestContext.Current.CancellationToken);
                Assert.True(agent.Process.HasExited,
                    $"Restarted runtime force stop must close owned Agent {agent.BeadId} (PID {agent.Process.Id}).");
            }
        }
        finally
        {
            foreach (var beadId in beadIds)
                File.WriteAllText(Path.Combine(releaseDirectory, beadId + ".release"), "test cleanup", new UTF8Encoding(false));
            foreach (var agent in restartedAgents)
                agent.Process.Dispose();
            if (!restarted.HasExited)
            {
                try { target.SendConsoleBreak(restarted.Id); }
                catch (Exception) { }
                try { await restarted.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { }
            }
        }
    }

    internal static async Task WaitForGracefulStopAcceptedAsync(
        WindowsConsoleProcess runtime,
        string runtimeLogPath,
        long logOffset,
        long expectedStartTicks,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        const string marker = "nightweaver: graceful stop accepted; draining current turns.";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadRuntimeLogFromOffset(runtimeLogPath, logOffset).Contains(marker, StringComparison.Ordinal))
                return;

            if (runtime.HasExited)
            {
                var remainingExitBudget = deadline - DateTime.UtcNow;
                var exitCode = await runtime.WaitAsync(
                    remainingExitBudget > TimeSpan.Zero ? remainingExitBudget : TimeSpan.Zero, cancellationToken);
                if (ReadRuntimeLogFromOffset(runtimeLogPath, logOffset).Contains(marker, StringComparison.Ordinal))
                    return;
                throw new InvalidOperationException(
                    $"Restarted runtime {runtime.Id} exited before acknowledging graceful stop (exit {exitCode}). " +
                    $"Output: {ReadRuntimeLog(runtime)}");
            }

            var actualStartTicks = ReadProcessStartTicks(runtime.Id);
            if (actualStartTicks != expectedStartTicks)
                throw new InvalidOperationException(
                    $"Restarted runtime PID {runtime.Id} changed identity before graceful stop acceptance " +
                    $"(expected startUtcTicks={expectedStartTicks}, actual={actualStartTicks}).");

            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException(
            $"Restarted runtime {runtime.Id} did not acknowledge graceful stop within the shared 30-second shutdown budget. " +
            $"Output: {ReadRuntimeLog(runtime)}");
    }

    private static long ReadProcessStartTicks(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static long RecordRuntimeIdentity(string path, WindowsConsoleProcess runtime)
    {
        var startTicks = ReadProcessStartTicks(runtime.Id);
        File.WriteAllText(path,
            $"pid={runtime.Id}|startUtcTicks={startTicks}|runtimeLog={runtime.OutputLogPath}", new UTF8Encoding(false));
        return startTicks;
    }

    private static bool IsSameOwnedRuntimeExited(WindowsConsoleProcess runtime, long expectedStartTicks)
    {
        if (!runtime.HasExited)
            return false;

        try
        {
            using var current = Process.GetProcessById(runtime.Id);
            return current.StartTime.ToUniversalTime().Ticks == expectedStartTicks;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string ReadRuntimeLogFromOffset(string path, long offset)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = stream.Length < offset ? 0 : offset;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string CreatePublishedHost(AgentStageTargetFixture target, int deliveryCapacity = 1)
    {
        var hostRoot = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-nightweaver-host")).FullName;
        published.CopyPublishedAppTo(hostRoot);
        var projects = Directory.CreateDirectory(Path.Combine(hostRoot, "projects"));
        var config = new
        {
            name = "demo",
            target_repository = new { path = target.Path, default_branch = "main" },
            concurrency = new { max_concurrent_planning = 1, max_concurrent_delivery = deliveryCapacity, max_active_plan_beads = 8 },
            limits = new { max_repair_cycles = 3 },
            agent_defaults = new { cli = "codex", effort = "low", timeout_minutes = 5 }
        };
        File.WriteAllText(Path.Combine(projects.FullName, "demo.json"), JsonSerializer.Serialize(config), new UTF8Encoding(false));
        return hostRoot;
    }

    private static Dictionary<string, string?> CreateGitShimEnvironment(
        AgentStageTargetFixture target,
        IReadOnlyDictionary<string, string?> overrides)
    {
        var environment = new Dictionary<string, string?>(target.CreateRuntimeEnvironment(overrides), StringComparer.OrdinalIgnoreCase);
        var shimDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "native-git-shim")).FullName;
        var fixtureDirectory = Path.GetDirectoryName(target.AgentFixtureExecutablePath)!;
        foreach (var file in new[] { "AgentInvocationFixture.exe", "AgentInvocationFixture.dll",
                     "AgentInvocationFixture.deps.json", "AgentInvocationFixture.runtimeconfig.json" })
            File.Copy(Path.Combine(fixtureDirectory, file), Path.Combine(shimDirectory, file), overwrite: true);
        File.Copy(target.AgentFixtureExecutablePath, Path.Combine(shimDirectory, "git.exe"), overwrite: true);
        var inheritedPath = environment["PATH"] ?? string.Empty;
        environment["PATH"] = shimDirectory + Path.PathSeparator + inheritedPath;
        environment["NIGHTWEAVER_GIT_REAL_PATH"] = FindExecutable("git");
        return environment;
    }

    private static Dictionary<string, string?> CreateBdShimEnvironment(
        AgentStageTargetFixture target,
        IReadOnlyDictionary<string, string?> overrides)
    {
        var environment = new Dictionary<string, string?>(target.CreateRuntimeEnvironment(overrides), StringComparer.OrdinalIgnoreCase);
        var shimDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "native-bd-shim")).FullName;
        var fixtureDirectory = Path.GetDirectoryName(target.AgentFixtureExecutablePath)!;
        foreach (var file in new[] { "AgentInvocationFixture.exe", "AgentInvocationFixture.dll",
                     "AgentInvocationFixture.deps.json", "AgentInvocationFixture.runtimeconfig.json" })
            File.Copy(Path.Combine(fixtureDirectory, file), Path.Combine(shimDirectory, file), overwrite: true);
        File.Copy(target.AgentFixtureExecutablePath, Path.Combine(shimDirectory, "bd.exe"), overwrite: true);
        var inheritedPath = environment["PATH"] ?? string.Empty;
        environment["PATH"] = shimDirectory + Path.PathSeparator + inheritedPath;
        environment["NIGHTWEAVER_BD_REAL_PATH"] = FindExecutable("bd");
        return environment;
    }

    private static string FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"Required native executable '{name}' was not found on PATH.");
    }

    private static async Task WaitForFileAsync(string path, WindowsConsoleProcess runtime, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            if (runtime.HasExited)
                throw new InvalidOperationException($"Published Nightweaver exited before fixture handshake '{path}' appeared (exit {await runtime.WaitAsync(TimeSpan.FromSeconds(1))}). Output: {ReadRuntimeLog(runtime)}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Published Nightweaver did not create fixture handshake '{path}' within {timeout}.");
    }

    private static string ReadRuntimeLog(WindowsConsoleProcess runtime)
    {
        if (runtime.OutputLogPath is not { } path || !File.Exists(path)) return "(no console output log)";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static Process ReadFixtureProcess(string path)
    {
        var parts = NativeProcessIdentityFile.Read(path).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, parts.Length);
        return OpenVerifiedProcess(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static (string BeadId, Process Process) ReadFixtureProcessFromLaunchRecord(string record)
    {
        var parts = record.Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(3, parts.Length);
        var processId = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var startTicks = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        return (parts[0], OpenVerifiedProcess(processId, startTicks));
    }

    private static Process OpenVerifiedProcess(int processId, long startTicks)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            // Bind later exit checks to this process generation before verifying its identity.
            _ = process.SafeHandle;
            Assert.Equal(startTicks, process.StartTime.ToUniversalTime().Ticks);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForStageLaunchCountAsync(
        string path,
        int expectedCount,
        WindowsConsoleProcess runtime,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)
                && (await ReadStageLaunchLinesAsync(path, TestContext.Current.CancellationToken)).Length >= expectedCount)
                return;
            if (runtime.HasExited)
                throw new InvalidOperationException($"Published Nightweaver exited before {expectedCount} Agents launched (exit {await runtime.WaitAsync(TimeSpan.FromSeconds(1))}). Output: {ReadRuntimeLog(runtime)}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Published Nightweaver did not launch {expectedCount} Agents within {timeout}.");
    }

    private static Task<string[]> ReadStageLaunchLinesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadStageLaunchLines(path));
    }

    private static string[] ReadStageLaunchLines(string path) => ManualPilotEvidenceLog.ReadAllLines(path);

    private static void AssertFixtureProcessExited(string path)
    {
        var parts = File.ReadAllText(path).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, parts.Length);
        var processId = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks == long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
                Assert.True(process.WaitForExit(5000), $"Fixture process {processId} survived runtime shutdown.");
        }
        catch (ArgumentException) { }
    }
}
