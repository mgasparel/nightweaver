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
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
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
    public async Task PublishedFirstSignalStopsMergerPreparationAndDrainsDeliveryWithoutNextLaunch()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
        var deliveryBeadId = target.CreateBead("Published graceful Delivery during Merger preparation", "task", "stage:implement");
        var mergerBeadId = target.CreateBead("Published Merger preparation must stop before launch", "task", "stage:merge");
        var hostRoot = CreatePublishedHost(target);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-merger-preparation-gates")).FullName;
        var releaseDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-releases")).FullName;
        var agentPidDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-pids")).FullName;
        var agentReadyDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-ready")).FullName;
        var classifierStarted = Path.Combine(gateDirectory, "classifier.started");
        var classifierRelease = Path.Combine(gateDirectory, "classifier.release");
        var classifierPid = Path.Combine(gateDirectory, "classifier.pid");
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var gitStarted = Path.Combine(gateDirectory, "git-preparation.started");
        var gitRelease = Path.Combine(gateDirectory, "git-preparation.release");
        var gitShimLog = Path.Combine(gateDirectory, "git-shim.log");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var mergerWorktree = Path.Combine(hostRoot, "worktrees", "demo", mergerBeadId);
        var environment = CreateGitShimEnvironment(target, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = Path.Combine(gateDirectory, "agent.started"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = Path.Combine(releaseDirectory, "default.release"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = releaseDirectory,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = agentPidDirectory,
            ["NIGHTWEAVER_FIXTURE_READY_DIRECTORY"] = agentReadyDirectory,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_STARTED_FILE"] = classifierStarted,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_RELEASE_FILE"] = classifierRelease,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_PID_FILE"] = classifierPid,
            ["NIGHTWEAVER_GIT_SHIM_MODE"] = "graceful-stop-preparation-gate",
            ["NIGHTWEAVER_GIT_SHIM_TARGET_WORKTREE"] = mergerWorktree,
            ["NIGHTWEAVER_GIT_SHIM_STARTED_FILE"] = gitStarted,
            ["NIGHTWEAVER_GIT_SHIM_RELEASE_FILE"] = gitRelease,
            ["NIGHTWEAVER_GIT_SHIM_LOG"] = gitShimLog,
            ["OPENAI_API_KEY"] = null
        });
        using var runtime = WindowsConsoleProcess.Start(
            Path.Combine(hostRoot, "nightweaver.exe"), ["--project", "demo"], target.Path, environment, runtimeLog);

        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, 1, runtime, TimeSpan.FromSeconds(90));
            var deliveryRecord = File.ReadAllLines(stageLaunchLog)
                .Select(ReadFixtureProcessFromLaunchRecord)
                .Single(record => record.BeadId == deliveryBeadId);
            using var deliveryAgent = deliveryRecord.Process;
            await WaitForFileAsync(Path.Combine(agentReadyDirectory, deliveryBeadId + ".ready"), runtime, TimeSpan.FromSeconds(30));
            await WaitForFileAsync(gitStarted, runtime, TimeSpan.FromSeconds(90));
            using var preparationGit = ReadFixtureProcess(gitStarted);
            Assert.False(deliveryAgent.HasExited);
            Assert.False(preparationGit.HasExited);

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first signal must gracefully drain Delivery while Merger preparation settles.");
            Assert.False(deliveryAgent.HasExited, "The first signal must leave the active Delivery Agent running.");
            Assert.False(preparationGit.HasExited, "The first signal must allow the in-flight Merger preparation operation to settle.");

            File.WriteAllText(gitRelease, "settle Merger preparation after stop", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(releaseDirectory, deliveryBeadId + ".release"), "finish current Delivery turn", new UTF8Encoding(false));
            await WaitForFileAsync(classifierStarted, runtime, TimeSpan.FromSeconds(90));
            using var classifier = ReadFixtureProcess(classifierPid);
            Assert.False(classifier.HasExited, "Outcome classification for the active Delivery turn must remain part of graceful drain.");
            Assert.Single(File.ReadAllLines(stageLaunchLog));

            File.WriteAllText(classifierRelease, "complete current Delivery classification", new UTF8Encoding(false));
            var exitCode = await runtime.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            Assert.True(exitCode == 0, $"Expected graceful drain to exit successfully, got {exitCode}. Runtime output: {ReadRuntimeLog(runtime)}");
            Assert.Single(File.ReadAllLines(stageLaunchLog));
            Assert.Equal("stage:review", BeadWorkflowState.Parse(target.ReadBead(deliveryBeadId)).Stage);
            Assert.Equal("open", target.ReadBead(deliveryBeadId).Status);
            Assert.Null(target.ReadBead(deliveryBeadId).Assignee);
            Assert.Equal("open", target.ReadBead(mergerBeadId).Status);
            Assert.Null(target.ReadBead(mergerBeadId).Assignee);
            Assert.Equal("stage:merge", BeadWorkflowState.Parse(target.ReadBead(mergerBeadId)).Stage);
            Assert.DoesNotContain(File.ReadAllLines(stageLaunchLog), line => line.StartsWith(mergerBeadId + "|", StringComparison.Ordinal));
            AssertFixtureProcessExited(classifierPid);
        }
        finally
        {
            File.WriteAllText(gitRelease, "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(releaseDirectory, deliveryBeadId + ".release"), "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(classifierRelease, "test cleanup", new UTF8Encoding(false));
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
    public async Task PublishedFirstSignalDrainsActiveDeliveryAndMergerWithoutLaunchingQueuedReceiver()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.ConfigureHostedFixtureOriginWithLocalRewrite();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
        var deliveryBeadId = target.CreateBead("Published graceful active Delivery", "task", "stage:implement");
        var mergerBeadId = target.CreateBead("Published graceful active Merger", "task", "stage:merge");
        var queuedMergerBeadId = target.CreateBead("Published graceful queued Merger receiver", "task", "stage:merge");
        var hostRoot = CreatePublishedHost(target);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-active-cross-pool-graceful-gates")).FullName;
        var releaseDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "delivery-releases")).FullName;
        var agentPidDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-pids")).FullName;
        var agentReadyDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-ready")).FullName;
        var classifierStarted = Path.Combine(gateDirectory, "classifier.started");
        var classifierRelease = Path.Combine(gateDirectory, "classifier.release");
        var classifierPid = Path.Combine(gateDirectory, "classifier.pid");
        var mergerStarted = Path.Combine(gateDirectory, "merger.started");
        var mergerRelease = Path.Combine(gateDirectory, "merger.release");
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var environment = target.CreateRuntimeEnvironment(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = Path.Combine(gateDirectory, "agent.started"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = Path.Combine(releaseDirectory, "default.release"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = releaseDirectory,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = agentPidDirectory,
            ["NIGHTWEAVER_FIXTURE_READY_DIRECTORY"] = agentReadyDirectory,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_STARTED_FILE"] = classifierStarted,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_RELEASE_FILE"] = classifierRelease,
            ["NIGHTWEAVER_FIXTURE_CLASSIFIER_PID_FILE"] = classifierPid,
            ["NIGHTWEAVER_FIXTURE_MERGER_BEAD_ID"] = mergerBeadId,
            ["NIGHTWEAVER_FIXTURE_MERGER_ACTION"] = "merger-verified-merge",
            ["NIGHTWEAVER_FIXTURE_MERGER_STARTED_FILE"] = mergerStarted,
            ["NIGHTWEAVER_FIXTURE_MERGER_RELEASE_FILE"] = mergerRelease,
            ["OPENAI_API_KEY"] = null
        });
        using var runtime = WindowsConsoleProcess.Start(
            Path.Combine(hostRoot, "nightweaver.exe"), ["--project", "demo"], target.Path, environment, runtimeLog);

        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, 2, runtime, TimeSpan.FromSeconds(90));
            await WaitForFileAsync(Path.Combine(agentReadyDirectory, deliveryBeadId + ".ready"), runtime, TimeSpan.FromSeconds(30));
            await WaitForFileAsync(mergerStarted, runtime, TimeSpan.FromSeconds(90));
            var activeAgents = File.ReadAllLines(stageLaunchLog).Select(ReadFixtureProcessFromLaunchRecord).ToArray();
            Assert.Equal(new[] { deliveryBeadId, mergerBeadId }.Order(StringComparer.Ordinal),
                activeAgents.Select(agent => agent.BeadId).Order(StringComparer.Ordinal));
            using var deliveryAgent = Assert.Single(activeAgents, agent => agent.BeadId == deliveryBeadId).Process;
            using var mergerAgent = Assert.Single(activeAgents, agent => agent.BeadId == mergerBeadId).Process;
            var queuedMerger = target.ReadBead(queuedMergerBeadId);
            Assert.Equal("open", queuedMerger.Status);
            Assert.Null(queuedMerger.Assignee);
            Assert.Equal("stage:merge", BeadWorkflowState.Parse(queuedMerger).Stage);
            Assert.False(deliveryAgent.HasExited);
            Assert.False(mergerAgent.HasExited);

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first signal must drain both active pools without canceling either current turn.");
            Assert.False(deliveryAgent.HasExited, "The first signal must leave the active Delivery Agent running.");
            Assert.False(mergerAgent.HasExited, "The first signal must leave the active Merger Agent running.");

            File.WriteAllText(Path.Combine(releaseDirectory, deliveryBeadId + ".release"), "finish current Delivery turn", new UTF8Encoding(false));
            File.WriteAllText(mergerRelease, "finish current Merger turn", new UTF8Encoding(false));
            await WaitForFileAsync(classifierStarted, runtime, TimeSpan.FromSeconds(90));
            using var classifier = ReadFixtureProcess(classifierPid);
            Assert.False(classifier.HasExited, "The current Delivery outcome classifier must be allowed to finish during drain.");
            Assert.Equal(2, File.ReadAllLines(stageLaunchLog).Length);

            File.WriteAllText(classifierRelease, "finish current Delivery classification", new UTF8Encoding(false));
            Assert.Equal(0, await runtime.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
            Assert.Equal(2, File.ReadAllLines(stageLaunchLog).Length);
            var delivery = target.ReadBead(deliveryBeadId);
            Assert.Equal("open", delivery.Status);
            Assert.Null(delivery.Assignee);
            Assert.Equal("stage:review", BeadWorkflowState.Parse(delivery).Stage);
            var merged = target.ReadBead(mergerBeadId);
            Assert.Equal("closed", merged.Status);
            Assert.Null(merged.Assignee);
            queuedMerger = target.ReadBead(queuedMergerBeadId);
            Assert.Equal("open", queuedMerger.Status);
            Assert.Null(queuedMerger.Assignee);
            Assert.Equal("stage:merge", BeadWorkflowState.Parse(queuedMerger).Stage);
            AssertFixtureProcessExited(classifierPid);
        }
        finally
        {
            File.WriteAllText(Path.Combine(releaseDirectory, deliveryBeadId + ".release"), "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(mergerRelease, "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(classifierRelease, "test cleanup", new UTF8Encoding(false));
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
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
        var beadIds = new[]
        {
            target.CreateBead("Published second console stop Delivery", "task", "stage:implement"),
            target.CreateBead("Published second console stop Merger", "task", "stage:merge")
        };
        var hostRoot = CreatePublishedHost(target);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-second-signal-gates")).FullName;
        var agentStarted = Path.Combine(gateDirectory, "agent.started");
        var agentRelease = Path.Combine(gateDirectory, "agent.release");
        var agentPidDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-pids")).FullName;
        var descendantPidDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "descendant-pids")).FullName;
        var worktreeMarkerDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "worktrees")).FullName;
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var environment = target.CreateRuntimeEnvironment(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = agentStarted,
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = agentRelease,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = agentPidDirectory,
            ["NIGHTWEAVER_FIXTURE_DESCENDANT_PID_DIRECTORY"] = descendantPidDirectory,
            ["NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY"] = worktreeMarkerDirectory,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["OPENAI_API_KEY"] = null
        });
        using var runtime = WindowsConsoleProcess.Start(
            Path.Combine(hostRoot, "nightweaver.exe"), ["--project", "demo"], target.Path, environment, runtimeLog);
        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, beadIds.Length, runtime, TimeSpan.FromSeconds(90));
            var agentProcesses = File.ReadAllLines(stageLaunchLog)
                .Select(ReadFixtureProcessFromLaunchRecord).ToArray();
            Assert.Equal(beadIds.Order(StringComparer.Ordinal), agentProcesses.Select(item => item.BeadId).Order(StringComparer.Ordinal));
            using var firstAgent = agentProcesses[0].Process;
            using var secondAgent = agentProcesses[1].Process;
            await WaitForFileAsync(Path.Combine(descendantPidDirectory, beadIds[0] + ".pid"), runtime, TimeSpan.FromSeconds(10));
            await WaitForFileAsync(Path.Combine(descendantPidDirectory, beadIds[1] + ".pid"), runtime, TimeSpan.FromSeconds(10));
            using var firstChild = ReadFixtureProcess(Path.Combine(descendantPidDirectory, beadIds[0] + ".pid"));
            using var secondChild = ReadFixtureProcess(Path.Combine(descendantPidDirectory, beadIds[1] + ".pid"));
            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first real console signal must start a graceful drain.");
            Assert.False(firstAgent.HasExited, "The first signal must leave the first active Agent running.");
            Assert.False(secondAgent.HasExited, "The first signal must leave the second active Agent running.");
            Assert.False(firstChild.HasExited, "The first signal must leave the first active descendant running.");
            Assert.False(secondChild.HasExited, "The first signal must leave the second active descendant running.");

            target.SendConsoleBreak(runtime.Id);
            try { target.SendConsoleBreak(runtime.Id); }
            catch (Exception) { } // The direct force exit may win before this repeated signal is delivered.
            Assert.Equal(1, await runtime.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(firstAgent.HasExited, "The second real console signal must close the first Agent Job.");
            Assert.True(secondAgent.HasExited, "The second real console signal must close every active Agent Job.");
            Assert.True(firstChild.HasExited, "Closing the first Agent Job must terminate its native descendant.");
            Assert.True(secondChild.HasExited, "Closing the second Agent Job must terminate its native descendant.");
            Assert.Equal(beadIds.Length, File.ReadAllLines(stageLaunchLog).Length);
            foreach (var beadId in beadIds)
            {
                Assert.Equal("in_progress", target.ReadBead(beadId).Status);
                var retainedWorktree = File.ReadAllText(Path.Combine(worktreeMarkerDirectory, beadId + ".path"));
                Assert.True(File.Exists(Path.Combine(retainedWorktree, "force-stop-retained.txt")),
                    $"Force stop must retain uncommitted work for '{beadId}'.");
            }
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
            Assert.True(agent.HasExited, "The working Agent must finish before its fallback classifier starts.");
            Assert.False(classifier.HasExited);

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first signal must continue draining the active classifier.");
            Assert.False(classifier.HasExited, "The first signal must leave its allowed current-turn classifier running.");

            target.SendConsoleBreak(runtime.Id);
            Assert.Equal(1, await runtime.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(agent.HasExited);
            Assert.True(classifier.HasExited, "The second signal must close the classifier's Agent Job without waiting for it.");
            Assert.Single(File.ReadAllLines(stageLaunchLog));
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

    [Fact]
    public async Task PublishedRuntimeExitsWhileGitVerificationIsBlockedAndAnotherAgentIsActive()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
        target.RunTarget("bd", ["update", target.ControlBeadId, "--add-label", "stage:merge", "--add-label", "needs-human"]);
        var beadIds = new[]
        {
            target.CreateBead("Published blocked Git and active Agent one", "task", "stage:implement"),
            target.CreateBead("Published blocked Git and active Agent two", "task", "stage:implement", "product-decision-required")
        };
        var hostRoot = CreatePublishedHost(target, deliveryCapacity: 2);
        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "published-git-force-gates")).FullName;
        var agentStarted = Path.Combine(gateDirectory, "agent.started");
        var releaseDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-releases")).FullName;
        var agentPidDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-pids")).FullName;
        var agentReadyDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-ready")).FullName;
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var gitStarted = Path.Combine(gateDirectory, "git.started");
        var gitRelease = Path.Combine(gateDirectory, "git.release");
        var gitShimLog = Path.Combine(gateDirectory, "git-shim.log");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        File.WriteAllText(Path.Combine(releaseDirectory, beadIds[1] + ".release"), "allow this Agent to finish", new UTF8Encoding(false));
        var completedWorktree = Path.Combine(hostRoot, "worktrees", "demo", beadIds[1]);
        var environment = CreateGitShimEnvironment(target, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = agentStarted,
            ["NIGHTWEAVER_FIXTURE_RELEASE_FILE"] = Path.Combine(releaseDirectory, "default.release"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = releaseDirectory,
            ["NIGHTWEAVER_FIXTURE_HUNG_BEAD_ID"] = beadIds[0],
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = agentPidDirectory,
            ["NIGHTWEAVER_FIXTURE_READY_DIRECTORY"] = agentReadyDirectory,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["NIGHTWEAVER_GIT_SHIM_MODE"] = "force-stop-status-gate",
            ["NIGHTWEAVER_GIT_SHIM_TARGET_WORKTREE"] = completedWorktree,
            ["NIGHTWEAVER_GIT_SHIM_STARTED_FILE"] = gitStarted,
            ["NIGHTWEAVER_GIT_SHIM_RELEASE_FILE"] = gitRelease,
            ["NIGHTWEAVER_GIT_SHIM_LOG"] = gitShimLog,
            ["OPENAI_API_KEY"] = null
        });
        using var runtime = WindowsConsoleProcess.Start(
            Path.Combine(hostRoot, "nightweaver.exe"), ["--project", "demo"], target.Path, environment, runtimeLog);
        Process? git = null;

        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, 1, runtime, TimeSpan.FromSeconds(90));
            var hungAgentRecord = File.ReadAllLines(stageLaunchLog)
                .Single(record => record.StartsWith(beadIds[0] + "|", StringComparison.Ordinal));
            using var hungAgent = ReadFixtureProcessFromLaunchRecord(hungAgentRecord).Process;
            await WaitForFileAsync(Path.Combine(agentReadyDirectory, beadIds[0] + ".ready"), runtime, TimeSpan.FromSeconds(30));
            Assert.False(hungAgent.HasExited, "One Agent must be active before admitting the bead that blocks Git verification.");
            target.RunTarget("bd", ["update", beadIds[1], "--remove-label", "product-decision-required"]);
            await WaitForFileAsync(gitStarted, runtime, TimeSpan.FromSeconds(90));
            try { git = ReadFixtureProcess(gitStarted); }
            catch (Exception exception)
            {
                var log = File.Exists(gitShimLog) ? File.ReadAllText(gitShimLog) : "(no shim log)";
                throw new InvalidOperationException($"Git gate process identity could not be opened; releaseExists={File.Exists(gitRelease)}; shimLog={log}", exception);
            }
            Assert.False(hungAgent.HasExited, "One Agent must remain active while another slot is blocked in Git verification.");
            Assert.False(git.HasExited);

            target.SendConsoleBreak(runtime.Id);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(runtime.HasExited, "The first signal must drain the blocked Git verification and active Agent.");
            Assert.False(hungAgent.HasExited);
            Assert.False(git.HasExited);

            target.SendConsoleBreak(runtime.Id);
            Assert.Equal(1, await runtime.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(hungAgent.HasExited, "The force signal must close the other active Agent Job.");
            Assert.False(git.HasExited, "Force exit must not wait for the blocked Git command or its gate.");
            Assert.Equal("in_progress", target.ReadBead(beadIds[0]).Status);
            Assert.Equal("in_progress", target.ReadBead(beadIds[1]).Status);
        }
        finally
        {
            File.WriteAllText(Path.Combine(releaseDirectory, beadIds[0] + ".release"), "test cleanup", new UTF8Encoding(false));
            File.WriteAllText(gitRelease, "test cleanup", new UTF8Encoding(false));
            if (!runtime.HasExited)
            {
                try { target.SendConsoleBreak(runtime.Id); }
                catch (Exception) { }
                try { await runtime.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { }
            }

            if (git is not null)
            {
                try { await git.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { try { git.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                git.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishedRuntimeForceStopsBlockedNativeBdFinalizationAndRestartRereadsActualState(bool commitAfterForce)
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration suite requires Windows console control events and native CLI fixtures.");
        using var target = new AgentStageTargetFixture();
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
        var agentReadyDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "agent-ready")).FullName;
        var markerDirectory = Directory.CreateDirectory(Path.Combine(gateDirectory, "worktrees")).FullName;
        var stageLaunchLog = Path.Combine(gateDirectory, "stage-launches.log");
        var bdStarted = Path.Combine(gateDirectory, "bd.started");
        var bdRelease = Path.Combine(gateDirectory, "bd.release");
        var runtimeLog = Path.Combine(gateDirectory, "runtime.log");
        var environment = CreateBdShimEnvironment(target, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIGHTWEAVER_FIXTURE_ACTION"] = "pool-implement-gated",
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = Path.Combine(gateDirectory, "agent.started"),
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
        Process? bdProcess = null;
        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, beadIds.Length, runtime, TimeSpan.FromSeconds(90));
            var initialAgents = File.ReadAllLines(stageLaunchLog).Select(ReadFixtureProcessFromLaunchRecord).ToArray();
            using var remainingAgent = initialAgents.Single(item => item.BeadId == beadIds[0]).Process;
            foreach (var beadId in beadIds)
                await WaitForFileAsync(Path.Combine(agentReadyDirectory, beadId + ".ready"), runtime, TimeSpan.FromSeconds(30));

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
            ["NIGHTWEAVER_FIXTURE_STARTED_FILE"] = Path.Combine(gateDirectory, "restart-agent.started"),
            ["NIGHTWEAVER_FIXTURE_RELEASE_DIRECTORY"] = releaseDirectory,
            ["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"] = Path.Combine(gateDirectory, "restart-agent-pids"),
            ["NIGHTWEAVER_FIXTURE_WORKTREE_MARKER_DIRECTORY"] = restartMarkerDirectory,
            ["NIGHTWEAVER_FIXTURE_STAGE_LAUNCH_LOG"] = stageLaunchLog,
            ["OPENAI_API_KEY"] = null
        };
        Directory.CreateDirectory(restartOverrides["NIGHTWEAVER_FIXTURE_PID_DIRECTORY"]!);
        var restartEnvironment = CreateBdShimEnvironment(target, restartOverrides);
        using var restarted = WindowsConsoleProcess.Start(executable, ["--project", "demo"], target.Path, restartEnvironment,
            Path.Combine(gateDirectory, "restart-runtime.log"));
        try
        {
            await WaitForStageLaunchCountAsync(stageLaunchLog, beadIds.Length * 2, restarted, TimeSpan.FromSeconds(90));
            foreach (var beadId in beadIds)
            {
                var observationPath = Path.Combine(restartMarkerDirectory, beadId + ".restart");
                try
                {
                    await WaitForFileAsync(observationPath, restarted, TimeSpan.FromSeconds(10));
                }
                catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
                {
                    var launches = File.Exists(stageLaunchLog) ? File.ReadAllText(stageLaunchLog) : "(no stage launch records)";
                    throw new InvalidOperationException(
                        $"Restart observer for {beadId} did not leave its marker. Stage launches: {launches}{Environment.NewLine}Runtime output: {ReadRuntimeLog(restarted)}",
                        exception);
                }
                Assert.EndsWith("|retained=True", File.ReadAllText(observationPath), StringComparison.OrdinalIgnoreCase);
            }

            target.SendConsoleBreak(restarted.Id);
            foreach (var beadId in beadIds)
                File.WriteAllText(Path.Combine(releaseDirectory, beadId + ".release"), "finish restart observation", new UTF8Encoding(false));
            _ = await restarted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            foreach (var beadId in beadIds)
                File.WriteAllText(Path.Combine(releaseDirectory, beadId + ".release"), "test cleanup", new UTF8Encoding(false));
            if (!restarted.HasExited)
            {
                try { target.SendConsoleBreak(restarted.Id); }
                catch (Exception) { }
                try { await restarted.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (TimeoutException) { }
            }
        }
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

    private static Process ReadFixtureProcess(string path)
    {
        var parts = File.ReadAllText(path).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, parts.Length);
        var process = Process.GetProcessById(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks);
        return process;
    }

    private static (string BeadId, Process Process) ReadFixtureProcessFromLaunchRecord(string record)
    {
        var parts = record.Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(3, parts.Length);
        var processId = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var startTicks = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        var process = Process.GetProcessById(processId);
        Assert.Equal(startTicks, process.StartTime.ToUniversalTime().Ticks);
        return (parts[0], process);
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

    private static async Task<string[]> ReadStageLaunchLinesAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllLines(path);
            }
            catch (IOException exception) when (OperatingSystem.IsWindows()
                && (exception.HResult & 0xFFFF) == 32
                && attempt < 5)
            {
                // Native Agents append launch records while the runtime starts the pool.
                // Retry only bounded sharing violations; persistent I/O errors still fail.
                await Task.Delay(TimeSpan.FromMilliseconds(50 << attempt), cancellationToken);
            }
        }
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
