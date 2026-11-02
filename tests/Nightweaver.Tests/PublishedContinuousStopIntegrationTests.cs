using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class PublishedContinuousStopIntegrationTests(PublishedCliArtifactFixture published)
    : AgentStageIntegrationTestSupport
{
    [Fact]
    public async Task PublishedRuntimeHandlesRealFirstConsoleSignalAndDrainsNativeOutcomeFallback()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Published console stop and fallback", "task", "stage:implement");
        var hostRoot = CreatePublishedHost(target);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-console-gates")).FullName;
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
            Assert.False(agent.HasExited);

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The published CLI must remain alive after the first real console signal while its Agent is gated.");
            Assert.False(agent.HasExited, "The first console signal must not cancel the current native Agent turn.");
            Assert.Equal("in_progress", target.ReadBead(beadId).Status);

            File.WriteAllText(agentRelease, "finish the current turn", new UTF8Encoding(false));
            await WaitForFileAsync(classifierStarted, runtime, TimeSpan.FromSeconds(90));
            using var classifier = ReadFixtureProcess(classifierPid);
            Assert.False(runtime.HasExited, "The bounded native outcome classifier must remain part of the current drain.");
            Assert.False(classifier.HasExited, "The classifier gate must hold the native fallback process during drain.");
            Assert.Single(File.ReadAllLines(stageLaunchLog));

            File.WriteAllText(classifierRelease, "classification complete", new UTF8Encoding(false));
            Assert.Equal(0, await runtime.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            Assert.Single(File.ReadAllLines(stageLaunchLog));
            Assert.Equal("stage:review", BeadWorkflowState.Parse(target.ReadBead(beadId)).Stage);
            Assert.Equal("open", target.ReadBead(beadId).Status);
            Assert.Null(target.ReadBead(beadId).Assignee);
            AssertFixtureProcessExited(agentPid);
            AssertFixtureProcessExited(classifierPid);
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

    [Fact]
    public async Task PublishedRuntimeKeepsSecondConsoleSignalAvailableDuringFirstStopDrain()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge"]);
        var beadId = target.CreateBead("Published second console stop", "task", "stage:implement");
        var hostRoot = CreatePublishedHost(target);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-second-signal-gates")).FullName;
        var agentStarted = Path.Combine(gateDirectory, "agent.started");
        var agentRelease = Path.Combine(gateDirectory, "agent.release");
        var agentPid = Path.Combine(gateDirectory, "agent.pid");
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var environment = target.CreateRuntimeEnvironment(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = agentStarted,
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = agentRelease,
            ["NIGHTWEAVER_FIXTURE_PID_FILE"] = agentPid,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["OPENAI_API_KEY"] = null
        });
        using var runtime = WindowsConsoleProcess.Start(
            Path.Combine(hostRoot, "nightweaver.exe"), ["--project", "demo"], target.Path, environment, runtimeLog);

        try
        {
            await WaitForFileAsync(agentStarted, runtime, TimeSpan.FromSeconds(90));
            using var agent = ReadFixtureProcess(agentPid);
            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first real console signal must start a graceful drain.");
            Assert.False(agent.HasExited, "The first signal must leave the active Agent running.");

            target.SendConsoleBreak(runtime.Id);
            Assert.Equal(1, await runtime.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(agent.HasExited, "The second real console signal must reach the force-stop handler.");
            Assert.Single(File.ReadAllLines(stageLaunchLog));
        }
        finally
        {
            if (!File.Exists(agentRelease)) File.WriteAllText(agentRelease, "test cleanup", new UTF8Encoding(false));
            if (!runtime.HasExited)
            {
                try { target.SendConsoleBreak(runtime.Id); }
                catch (Exception) { }
                try { await runtime.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { }
            }
        }
    }

    private string CreatePublishedHost(AgentStageTargetFixture target)
    {
        var hostRoot = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-nightweaver-host")).FullName;
        published.CopyPublishedAppTo(hostRoot);
        var projects = Directory.CreateDirectory(Path.Combine(hostRoot, "projects"));
        var config = new
        {
            name = "demo",
            target_repository = new { path = target.Path, default_branch = "main" },
            concurrency = new { max_concurrent_planning = 1, max_concurrent_delivery = 1, max_active_plan_beads = 1 },
            limits = new { max_repair_cycles = 3 },
            agent_defaults = new { cli = "codex", effort = "low", timeout_minutes = 5 }
        };
        File.WriteAllText(Path.Combine(projects.FullName, "demo.json"), JsonSerializer.Serialize(config), new UTF8Encoding(false));
        return hostRoot;
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

    private static string ReadRuntimeLog(WindowsConsoleProcess runtime) =>
        runtime.OutputLogPath is { } path && File.Exists(path) ? File.ReadAllText(path) : "(no console output log)";

    private static Process ReadFixtureProcess(string path)
    {
        var parts = File.ReadAllText(path).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, parts.Length);
        var process = Process.GetProcessById(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks);
        return process;
    }

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
