using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Execution;
using Nightweaver.Tests.AgentInvocationFixture;
using Nightweaver.Tests.ExecutionCrashHost;

namespace Nightweaver.Tests;

public sealed class AgentStreamCleanupIntegrationTests
{
    private static readonly byte[] ExpectedOutput = Encoding.UTF8.GetBytes("prefix-€-suffix\n");
    private static long lastInvocationTimestampTicks;

    [Fact]
    public async Task NativeExecutorAndManagedRunnerExposeBothBlockedDrainsWithReleaseControls()
    {
        Assert.True(OperatingSystem.IsWindows(), "This loop requires the native Windows Job Object launcher.");

        var red = await RunCoordinatorAsync("red");
        Assert.Equal("red", red.Root.GetProperty("Scenario").GetString());
        Assert.False(red.Root.GetProperty("LaunchContextActive").GetBoolean());
        Assert.False(red.Root.GetProperty("StopRequested").GetBoolean());
        Assert.True(red.Root.GetProperty("HolderAliveAtReadiness").GetBoolean(), red.StandardOutput + red.StandardError);
        Assert.True(red.Root.GetProperty("HolderAliveAtVerdict").GetBoolean());
        Assert.True(red.Root.GetProperty("RunnerPendingAtVerdict").GetBoolean());
        Assert.Equal(0, red.Root.GetProperty("HolderExitCode").GetInt32());

        var producer = red.Root.GetProperty("Producer");
        var holder = red.Root.GetProperty("Holder");
        var coordinatorPid = red.Root.GetProperty("CoordinatorProcessId").GetInt32();
        Assert.NotEqual(producer.GetProperty("ProcessId").GetInt32(), holder.GetProperty("ProcessId").GetInt32());
        Assert.Equal((uint)coordinatorPid, red.Root.GetProperty("HolderParentProcessId").GetUInt32());
        Assert.True(producer.GetProperty("Inheritable").GetBoolean());

        var failure = red.Root.GetProperty("ExecutorFailure");
        Assert.NotEqual(JsonValueKind.Null, failure.ValueKind);
        var errorNodes = EnumerateErrorNodes(failure).ToArray();
        var errorText = string.Join("\n", errorNodes.Select(node => node.GetProperty("Message").GetString()));
        Assert.Contains("outcome=Exited, exit_code=0", errorText, StringComparison.Ordinal);
        Assert.Contains("timeout=00:00:25", errorText, StringComparison.Ordinal);
        Assert.Contains("stdout drain", errorText, StringComparison.Ordinal);
        Assert.Contains("stderr drain", errorText, StringComparison.Ordinal);
        Assert.Contains(errorNodes, node => node.GetProperty("Type").GetString() == "System.TimeoutException");
        Assert.DoesNotContain(errorNodes, node => node.GetProperty("Message").GetString()!.Contains(
            "process containment:", StringComparison.Ordinal));
        Assert.Empty(red.Root.GetProperty("CleanupErrors").EnumerateArray());
        Assert.Contains("coordinatorCopiesClosed", red.Root.GetProperty("EventTimes").EnumerateObject().Select(item => item.Name));
        Assert.NotNull(red.Root.GetProperty("LogBytesBase64").GetString());

        var releaseControl = await RunCoordinatorAsync("release-before-root");
        AssertCleanControl(releaseControl.Root, "release-before-root");

        var noninheritableControl = await RunCoordinatorAsync("noninheritable");
        AssertCleanControl(noninheritableControl.Root, "noninheritable");

        var prelaunchControl = await RunCoordinatorAsync("prelaunch-holder");
        AssertWindowExperiment(prelaunchControl.Root, "prelaunch-holder", expectedHolderCount: 1);
        Assert.Equal(JsonValueKind.Null, prelaunchControl.Root.GetProperty("ExecutorFailure").ValueKind);
        Assert.Equal("Exited", prelaunchControl.Root.GetProperty("Execution").GetProperty("Status").GetString());
        Assert.Equal(0, prelaunchControl.Root.GetProperty("Execution").GetProperty("ExitCode").GetInt32());
        Assert.Equal(Convert.ToBase64String(ExpectedOutput), prelaunchControl.Root.GetProperty("LogBytesBase64").GetString());
        var prelaunchEvents = prelaunchControl.Root.GetProperty("EventTimes");
        Assert.True(prelaunchEvents.GetProperty("holderReady-0").GetDateTimeOffset()
            < prelaunchEvents.GetProperty("executorCallStarted").GetDateTimeOffset());
    }

    [Fact]
    public async Task NativeLaunchBurstDoesNotDependOnAvailableThreadPoolWorkers()
    {
        var creationWindow = await RunCoordinatorAsync("launch-race");
        AssertWindowExperiment(creationWindow.Root, "launch-race", expectedHolderCount: 8);
    }

    private static void AssertWindowExperiment(JsonElement result, string scenario, int expectedHolderCount)
    {
        Assert.Equal(scenario, result.GetProperty("Scenario").GetString());
        Assert.False(result.GetProperty("LaunchContextActive").GetBoolean());
        Assert.False(result.GetProperty("StopRequested").GetBoolean());
        Assert.True(result.GetProperty("Producer").ValueKind == JsonValueKind.Object, result.GetRawText());
        Assert.Equal(0, result.GetProperty("Producer").GetProperty("StdoutHandle").GetInt64());
        Assert.Equal(0, result.GetProperty("Producer").GetProperty("StderrHandle").GetInt64());
        Assert.False(result.GetProperty("Producer").GetProperty("Inheritable").GetBoolean());
        var holders = result.GetProperty("ForeignHolders").EnumerateArray().ToArray();
        Assert.Equal(expectedHolderCount, holders.Length);
        var coordinatorPid = (uint)result.GetProperty("CoordinatorProcessId").GetInt32();
        var producerPid = result.GetProperty("Producer").GetProperty("ProcessId").GetInt32();
        Assert.All(holders, holder =>
        {
            Assert.Equal(coordinatorPid, holder.GetProperty("ParentProcessId").GetUInt32());
            Assert.NotEqual(producerPid, holder.GetProperty("Holder").GetProperty("ProcessId").GetInt32());
            Assert.True(holder.GetProperty("AliveAtReadiness").GetBoolean());
            Assert.True(holder.GetProperty("AliveAtVerdict").GetBoolean());
            Assert.True(holder.GetProperty("RunnerPendingAtVerdict").GetBoolean());
            Assert.Equal(0, holder.GetProperty("ExitCode").GetInt32());
        });
        var events = result.GetProperty("EventTimes");
        var executorStart = events.GetProperty("executorCallStarted").GetDateTimeOffset();
        foreach (var item in events.EnumerateObject().Where(item => item.Name.StartsWith("managedRunnerCallStarted-", StringComparison.Ordinal)))
        {
            var runnerStart = item.Value.GetDateTimeOffset();
            if (scenario == "launch-race")
                Assert.True(Math.Abs((runnerStart - executorStart).TotalMilliseconds) < 100,
                    $"Runner start {item.Name} was not in the bounded native launch burst. {result}");
            else
                Assert.True(runnerStart < executorStart,
                    $"Prelaunch holder {item.Name} was not started before the native executor. {result}");
        }

        var failure = result.GetProperty("ExecutorFailure");
        Assert.Equal(JsonValueKind.Null, failure.ValueKind);
        Assert.Equal("Exited", result.GetProperty("Execution").GetProperty("Status").GetString());
        Assert.Equal(0, result.GetProperty("Execution").GetProperty("ExitCode").GetInt32());
        Assert.Equal(Convert.ToBase64String(ExpectedOutput), result.GetProperty("LogBytesBase64").GetString());
        Assert.Empty(result.GetProperty("CleanupErrors").EnumerateArray());
    }

    private static void AssertCleanControl(JsonElement result, string scenario)
    {
        Assert.Equal(scenario, result.GetProperty("Scenario").GetString());
        Assert.False(result.GetProperty("LaunchContextActive").GetBoolean());
        Assert.False(result.GetProperty("StopRequested").GetBoolean());
        Assert.True(result.GetProperty("HolderAliveAtReadiness").GetBoolean());
        Assert.Equal(0, result.GetProperty("HolderExitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("ExecutorFailure").ValueKind);
        Assert.Equal("Exited", result.GetProperty("Execution").GetProperty("Status").GetString());
        Assert.Equal(0, result.GetProperty("Execution").GetProperty("ExitCode").GetInt32());
        Assert.Equal(Convert.ToBase64String(ExpectedOutput), result.GetProperty("LogBytesBase64").GetString());
        Assert.Empty(result.GetProperty("CleanupErrors").EnumerateArray());
    }

    private static IEnumerable<JsonElement> EnumerateErrorNodes(JsonElement node)
    {
        yield return node;
        foreach (var inner in node.GetProperty("InnerExceptions").EnumerateArray())
        foreach (var descendant in EnumerateErrorNodes(inner))
            yield return descendant;
    }

    private static async Task<CoordinatorRun> RunCoordinatorAsync(string scenario)
    {
        using var temporary = new TempDirectory();
        var runtimeRoot = Path.Combine(temporary.Path, "runtime");
        var worktree = Path.Combine(runtimeRoot, "worktrees", "ns-8rr-loop", "ns-8rr-" + Guid.NewGuid().ToString("N"));
        var scenarioDirectory = temporary.CreateDirectory("scenario");
        var beadId = Path.GetFileName(worktree);
        var now = DateTime.UtcNow.Ticks;
        long uniqueTicks;
        while (true)
        {
            var previous = Interlocked.Read(ref lastInvocationTimestampTicks);
            uniqueTicks = Math.Max(now, previous + 1);
            if (Interlocked.CompareExchange(ref lastInvocationTimestampTicks, uniqueTicks, previous) == previous) break;
        }
        var invocationId = new DateTime(uniqueTicks, DateTimeKind.Utc)
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-qa";
        var environment = GetInheritedEnvironment();
        if (scenario == "launch-race") environment["DOTNET_PROCESSOR_COUNT"] = "2";
        var dotnet = WindowsProcessLauncher.ResolveExecutable("dotnet", environment);
        var arguments = new[]
        {
            typeof(CrashHostMarker).Assembly.Location,
            "--stream-cleanup-coordinator",
            runtimeRoot,
            "ns-8rr-loop",
            beadId,
            invocationId,
            typeof(FixtureMarker).Assembly.Location,
            worktree,
            scenarioDirectory,
            scenario
        };

        using var coordinator = WindowsProcessLauncher.Launch(dotnet, arguments, AppContext.BaseDirectory, environment);
        var stdout = ReadAllAsync(coordinator.StandardOutput);
        var stderr = ReadAllAsync(coordinator.StandardError);
        var rootExitTask = coordinator.WaitForRootExitAsync();
        uint rootExitCode;
        try
        {
            rootExitCode = await rootExitTask.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
        }
        catch
        {
            try { await coordinator.TerminateAndConfirmEmptyAsync(TimeSpan.FromSeconds(10)); }
            catch { }
            coordinator.CloseJob();
            throw;
        }

        await coordinator.TerminateAndConfirmEmptyAsync(TimeSpan.FromSeconds(5));
        coordinator.CloseJob();
        var standardOutput = await stdout.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var standardError = await stderr.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var artifactFolder = scenario is "launch-race" or "prelaunch-holder" ? "ns-8rr-window" : "ns-8rr-loop";
        var evidenceDirectory = Path.Combine(FindRepositoryRoot(), "TestResults", artifactFolder);
        Directory.CreateDirectory(evidenceDirectory);
        var evidenceName = scenario + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, evidenceName + ".json"), standardOutput,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, evidenceName + ".stderr.txt"), standardError,
            TestContext.Current.CancellationToken);
        Assert.Equal(0u, rootExitCode);
        Assert.True(string.IsNullOrWhiteSpace(standardError), standardError);

        var jsonLine = standardOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Single();
        return new CoordinatorRun(JsonDocument.Parse(jsonLine), rootExitCode, standardOutput, standardError);
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> GetInheritedEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value) environment[name] = value;
        }
        return environment;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nightweaver.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Nightweaver.slnx from the test output directory.");
    }

    private sealed record CoordinatorRun(JsonDocument Document, uint RootExitCode, string StandardOutput, string StandardError)
    {
        internal JsonElement Root => Document.RootElement;
    }
}
