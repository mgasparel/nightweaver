using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests.ExecutionCrashHost;

internal static class AgentStreamCleanupCoordinator
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(16);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8) return 64;

        try
        {
            var report = await RunScenarioAsync(args).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new CoordinatorReport(
                args.Length == 8 ? args[7] : "invalid",
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                ContinuousAgentLaunchContext.IsActive,
                false,
                null,
                null,
                null,
                false,
                false,
                false,
                null,
                CaptureException(exception),
                null,
                null,
                new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal),
                [],
                [])));
            return 0;
        }
    }

    private static async Task<CoordinatorReport> RunScenarioAsync(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var project = ProjectSlug.Parse(args[1]);
        var beadId = args[2];
        var invocationId = args[3];
        var fixtureAssembly = Path.GetFullPath(args[4]);
        var workingDirectory = Path.GetFullPath(args[5]);
        var scenarioDirectory = Path.GetFullPath(args[6]);
        var scenario = args[7];
        if (scenario is "launch-race" or "prelaunch-holder")
            return await RunWindowScenarioAsync(args).ConfigureAwait(false);
        if (scenario is not ("red" or "release-before-root" or "noninheritable"))
            throw new ArgumentException($"Unknown stream-cleanup scenario '{scenario}'.", nameof(args));

        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(scenarioDirectory);
        var hostPaths = new HostPaths(root);
        var temporaryDirectory = Path.GetFullPath(Path.Combine(hostPaths.TmpRoot(project), beadId, invocationId));
        var logFilePath = Path.GetFullPath(Path.Combine(hostPaths.LogsRoot(project), beadId, invocationId + ".log"));
        var producerReportPath = Path.Combine(scenarioDirectory, "producer.json");
        var producerReleasePath = Path.Combine(scenarioDirectory, "producer.release");
        var holderReadyPath = Path.Combine(scenarioDirectory, "holder.json");
        var holderReleasePath = Path.Combine(scenarioDirectory, "holder.release");
        var producerInheritable = scenario != "noninheritable";
        var startedAt = DateTimeOffset.UtcNow;
        var coordinator = Process.GetCurrentProcess();
        var coordinatorId = Environment.ProcessId;
        var coordinatorStart = coordinator.StartTime.ToUniversalTime().Ticks;

        var invocation = new PreparedAgentInvocation(
            "dotnet",
            [fixtureAssembly, "stream-cleanup-producer", coordinatorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                producerReportPath, producerReleasePath, producerInheritable.ToString()],
            workingDirectory,
            "",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NIGHTWEAVER_BEAD_ID"] = beadId,
                ["TMP"] = temporaryDirectory,
                ["TEMP"] = temporaryDirectory,
                ["TMPDIR"] = temporaryDirectory
            },
            TimeSpan.FromSeconds(25),
            invocationId,
            temporaryDirectory,
            logFilePath);

        var eventTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["coordinatorStarted"] = startedAt
        };
        using var runnerCancellation = new CancellationTokenSource();
        Task<ProcessResult>? holderTask = null;
        IntPtr processHandle = IntPtr.Zero;
        ProducerReport? producer = null;
        HolderReport? holder = null;
        var duplicateHandlesClosed = false;
        var holderAliveAtReadiness = false;
        var holderAliveAtVerdict = false;
        uint? holderParentProcessId = null;
        int? holderExitCode = null;
        AgentExecutionResult? execution = null;
        ExceptionNode? executorFailure = null;
        var runnerPendingAtVerdict = false;
        string? logBytesBase64 = null;
        var cleanupErrors = new List<string>();

        var executorTask = AgentInvocationExecutor.ExecuteAsync(hostPaths, project, invocation, CancellationToken.None);
        try
        {
            producer = await WaitForJsonAsync<ProducerReport>(producerReportPath, GateTimeout, executorTask, logFilePath).ConfigureAwait(false);
            eventTimes["producerReady"] = producer.DuplicatesCreatedAtUtc;
            if (producer.ProcessId <= 0 || producer.StdoutHandle == 0 || producer.StderrHandle == 0)
                throw new InvalidDataException("Producer did not report valid process and duplicate-handle identities.");
            if (producer.Inheritable != producerInheritable)
                throw new InvalidDataException("Producer reported a different duplicate-handle inheritance mode.");
            processHandle = OpenProcessForDuplicateHandles(coordinatorId);

            var runner = new SystemProcessRunner();
            var holderRequest = new ProcessRequest(
                "dotnet",
                [fixtureAssembly, "stream-cleanup-holder", holderReadyPath, holderReleasePath],
                workingDirectory,
                new Dictionary<string, string?>(StringComparer.Ordinal),
                TimeSpan.FromSeconds(35));
            eventTimes["managedRunnerStarted"] = DateTimeOffset.UtcNow;
            holderTask = runner.RunAsync(holderRequest, runnerCancellation.Token);
            holder = await WaitForJsonAsync<HolderReport>(holderReadyPath, GateTimeout).ConfigureAwait(false);
            eventTimes["holderReady"] = holder.ReadyAtUtc;
            holderParentProcessId = ReadParentProcessId(holder.ProcessId);
            holderAliveAtReadiness = IsLiveIdentity(holder.ProcessId, holder.StartTimeUtcTicks);
            if (!holderAliveAtReadiness || holder.ProcessId == producer.ProcessId || holderParentProcessId != coordinatorId)
                throw new InvalidDataException("Managed holder identity/parent did not prove it is a live coordinator child outside the producer process tree.");
            runnerPendingAtVerdict = !holderTask.IsCompleted;

            if (scenario is "red" or "release-before-root")
            {
                CloseDuplicateCopies(producer);
                duplicateHandlesClosed = true;
                eventTimes["coordinatorCopiesClosed"] = DateTimeOffset.UtcNow;
            }

            if (scenario == "red")
            {
                WriteGate(producerReleasePath);
                eventTimes["producerReleased"] = DateTimeOffset.UtcNow;
                try
                {
                    execution = await executorTask.WaitAsync(ResultTimeout).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    executorFailure = CaptureException(exception);
                }

                eventTimes["executorVerdict"] = DateTimeOffset.UtcNow;
                holderAliveAtVerdict = IsLiveIdentity(holder.ProcessId, holder.StartTimeUtcTicks);
                runnerPendingAtVerdict = !holderTask.IsCompleted;
                if (!holderAliveAtVerdict || !runnerPendingAtVerdict)
                    throw new InvalidDataException("Foreign holder was not positively alive with its managed runner pending at the executor verdict.");

                WriteGate(holderReleasePath);
                eventTimes["holderReleased"] = DateTimeOffset.UtcNow;
                var holderResult = await holderTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                holderExitCode = holderResult.ExitCode;
            }
            else
            {
                if (scenario == "noninheritable")
                {
                    CloseDuplicateCopies(producer);
                    duplicateHandlesClosed = true;
                    eventTimes["coordinatorCopiesClosed"] = DateTimeOffset.UtcNow;
                }

                WriteGate(holderReleasePath);
                eventTimes["holderReleased"] = DateTimeOffset.UtcNow;
                var holderResult = await holderTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                holderExitCode = holderResult.ExitCode;
                WriteGate(producerReleasePath);
                eventTimes["producerReleased"] = DateTimeOffset.UtcNow;
                execution = await executorTask.WaitAsync(ResultTimeout).ConfigureAwait(false);
                eventTimes["executorVerdict"] = DateTimeOffset.UtcNow;
            }

            if (File.Exists(logFilePath))
                logBytesBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(logFilePath).ConfigureAwait(false));
        }
        finally
        {
            if (processHandle != IntPtr.Zero && producer is not null && !duplicateHandlesClosed)
            {
                try { CloseDuplicateCopies(producer); duplicateHandlesClosed = true; }
                catch (Exception exception) { cleanupErrors.Add("close duplicates: " + exception); }
            }

            try { WriteGate(producerReleasePath); }
            catch (Exception exception) { cleanupErrors.Add("release producer: " + exception); }
            try { WriteGate(holderReleasePath); }
            catch (Exception exception) { cleanupErrors.Add("release holder: " + exception); }
            if (holderTask is not null)
            {
                try
                {
                    var holderResult = await holderTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    holderExitCode ??= holderResult.ExitCode;
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add("settle holder: " + exception);
                    runnerCancellation.Cancel();
                }
            }

            if (!executorTask.IsCompleted)
            {
                try { execution = await executorTask.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (executorFailure is null) executorFailure = CaptureException(exception);
                }
            }
            if (processHandle != IntPtr.Zero && !CloseHandle(processHandle))
                cleanupErrors.Add("CloseHandle(coordinator): " + Marshal.GetLastWin32Error());
        }

        eventTimes["coordinatorFinished"] = DateTimeOffset.UtcNow;
        return new CoordinatorReport(
            scenario,
            coordinatorId,
            coordinatorStart,
            ContinuousAgentLaunchContext.IsActive,
            ContinuousAgentLaunchContext.StopRequested,
            producer,
            holder,
            holderParentProcessId,
            holderAliveAtReadiness,
            holderAliveAtVerdict,
            runnerPendingAtVerdict,
            execution is null ? null : new ExecutionSummary(execution.Status.ToString(), execution.ExitCode),
            executorFailure,
            holderExitCode,
            logBytesBase64,
            eventTimes,
            holder is null ? [] : [new ForeignHolderObservation(0,
                eventTimes.TryGetValue("managedRunnerStarted", out var runnerStarted) ? runnerStarted : holder.ReadyAtUtc,
                holder, holderParentProcessId ?? 0, holderAliveAtReadiness, holderAliveAtVerdict,
                runnerPendingAtVerdict, holderExitCode)],
            cleanupErrors);
    }

    private static async Task<CoordinatorReport> RunWindowScenarioAsync(string[] args)
    {
        const int burstSize = 8;
        var root = Path.GetFullPath(args[0]);
        var project = ProjectSlug.Parse(args[1]);
        var beadId = args[2];
        var invocationId = args[3];
        var fixtureAssembly = Path.GetFullPath(args[4]);
        var workingDirectory = Path.GetFullPath(args[5]);
        var scenarioDirectory = Path.GetFullPath(args[6]);
        var scenario = args[7];
        var coordinatorId = Environment.ProcessId;
        var coordinatorStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(scenarioDirectory);

        var hostPaths = new HostPaths(root);
        var temporaryDirectory = Path.GetFullPath(Path.Combine(hostPaths.TmpRoot(project), beadId, invocationId));
        var logFilePath = Path.GetFullPath(Path.Combine(hostPaths.LogsRoot(project), beadId, invocationId + ".log"));
        var producerReportPath = Path.Combine(scenarioDirectory, "producer.json");
        var producerReleasePath = Path.Combine(scenarioDirectory, "producer.release");
        var invocation = new PreparedAgentInvocation(
            "dotnet",
            [fixtureAssembly, "stream-cleanup-race-producer", producerReportPath, producerReleasePath],
            workingDirectory,
            "",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NIGHTWEAVER_BEAD_ID"] = beadId,
                ["TMP"] = temporaryDirectory,
                ["TEMP"] = temporaryDirectory,
                ["TMPDIR"] = temporaryDirectory
            },
            TimeSpan.FromSeconds(25),
            invocationId,
            temporaryDirectory,
            logFilePath);

        var eventTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["coordinatorStarted"] = DateTimeOffset.UtcNow
        };
        var runner = new SystemProcessRunner();
        using var runnerCancellation = new CancellationTokenSource();
        var runnerStarts = new List<RunnerStart>();
        var holderReports = new List<HolderReport>();
        var holderParentIds = new List<uint>();
        var cleanupErrors = new List<string>();
        AgentExecutionResult? execution = null;
        ExceptionNode? executorFailure = null;
        ProducerReport? producer = null;
        DateTimeOffset executorCallStartedAt = default;
        var holdersAliveAtReadiness = false;
        var holdersAliveAtVerdict = false;
        var runnerTasksPendingAtVerdict = false;
        var holderExitCodes = new List<int>();
        string? logBytesBase64 = null;

        Task<AgentExecutionResult>? executorTask = null;
        try
        {
            if (scenario == "prelaunch-holder")
            {
                runnerStarts.Add(StartHolder(runner, fixtureAssembly, workingDirectory, scenarioDirectory,
                    0, runnerCancellation.Token));
                eventTimes["managedRunnerCallStarted-0"] = runnerStarts[0].CallStartedAtUtc;
                holderReports.Add(await WaitForJsonAsync<HolderReport>(runnerStarts[0].ReadyPath, GateTimeout).ConfigureAwait(false));
                holderParentIds.Add(ReadParentProcessId(holderReports[0].ProcessId));
                eventTimes["holderReady-0"] = holderReports[0].ReadyAtUtc;
                if (holderParentIds[0] != coordinatorId || !IsLiveIdentity(holderReports[0].ProcessId, holderReports[0].StartTimeUtcTicks))
                    throw new InvalidDataException("Prelaunch managed holder identity/parent was not confirmed.");
                holdersAliveAtReadiness = true;

                executorCallStartedAt = DateTimeOffset.UtcNow;
                eventTimes["executorCallStarted"] = executorCallStartedAt;
                executorTask = AgentInvocationExecutor.ExecuteAsync(hostPaths, project, invocation, CancellationToken.None);
            }
            else
            {
                // This private coordinator must produce the burst even when its
                // shared workers are scarce; it does not change xUnit parallelism.
                ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
                ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
                // The eight deliberately held managed children retain stdout/stderr
                // readers. Reserve callbacks as well as the native exit wait after
                // testing burst admission with only two shared workers available.
                var readerWorkers = Math.Max(minimumWorkers, burstSize * 2 + 4);
                if (!ThreadPool.SetMinThreads(Math.Min(minimumWorkers, 2), minimumIo)
                    || !ThreadPool.SetMaxThreads(2, maximumIo))
                    throw new InvalidOperationException("Could not constrain the launch probe's private ThreadPool.");
                try
                {
                    using var barrier = new Barrier(burstSize + 1, _ =>
                    {
                        // Release the worker constraint before any real launch I/O.
                        ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
                        ThreadPool.SetMinThreads(readerWorkers, minimumIo);
                    });
                    var starts = Enumerable.Range(0, burstSize).Select(index => Task.Factory.StartNew(() =>
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("Managed runner did not reach the native launch overlap barrier.");
                        return StartHolder(runner, fixtureAssembly, workingDirectory, scenarioDirectory, index,
                            runnerCancellation.Token);
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                    var executorStart = Task.Factory.StartNew(() =>
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("Native executor did not reach the managed launch overlap barrier.");
                        var calledAt = DateTimeOffset.UtcNow;
                        return new ExecutorStart(calledAt,
                            AgentInvocationExecutor.ExecuteAsync(hostPaths, project, invocation, CancellationToken.None));
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    runnerStarts.AddRange(await Task.WhenAll(starts).ConfigureAwait(false));
                    var started = await executorStart.ConfigureAwait(false);
                    executorCallStartedAt = started.CalledAtUtc;
                    executorTask = started.Task;
                    eventTimes["executorCallStarted"] = executorCallStartedAt;
                    foreach (var holderStart in runnerStarts)
                        eventTimes[$"managedRunnerCallStarted-{holderStart.Index}"] = holderStart.CallStartedAtUtc;
                }
                finally
                {
                    // Constrain only burst admission, not later stream/exit callbacks
                    // in this single-scenario, disposable coordinator process.
                    ThreadPool.SetMaxThreads(maximumWorkers, maximumIo);
                    ThreadPool.SetMinThreads(readerWorkers, minimumIo);
                }
            }

            producer = await WaitForJsonAsync<ProducerReport>(producerReportPath, GateTimeout,
                executorTask, logFilePath).ConfigureAwait(false);
            eventTimes["producerReady"] = producer.DuplicatesCreatedAtUtc;
            if (producer.ProcessId <= 0 || producer.StdoutHandle != 0 || producer.StderrHandle != 0 || producer.Inheritable)
                throw new InvalidDataException("Race producer unexpectedly duplicated writer handles or reported an invalid process.");

            if (scenario == "launch-race")
            {
                var ready = await Task.WhenAll(runnerStarts.Select(start =>
                    WaitForJsonAsync<HolderReport>(start.ReadyPath, GateTimeout))).ConfigureAwait(false);
                holderReports.AddRange(ready);
                foreach (var holder in holderReports)
                    holderParentIds.Add(ReadParentProcessId(holder.ProcessId));
                for (var index = 0; index < holderReports.Count; index++)
                {
                    eventTimes[$"holderReady-{index}"] = holderReports[index].ReadyAtUtc;
                    if (holderParentIds[index] != coordinatorId
                        || holderReports[index].ProcessId == producer.ProcessId
                        || !IsLiveIdentity(holderReports[index].ProcessId, holderReports[index].StartTimeUtcTicks))
                        throw new InvalidDataException($"Racing managed holder {index} identity/parent was not confirmed.");
                }
                holdersAliveAtReadiness = true;
            }

            WriteGate(producerReleasePath);
            eventTimes["producerReleased"] = DateTimeOffset.UtcNow;
            try { execution = await executorTask!.WaitAsync(ResultTimeout).ConfigureAwait(false); }
            catch (Exception exception) { executorFailure = CaptureException(exception); }
            eventTimes["executorVerdict"] = DateTimeOffset.UtcNow;

            holdersAliveAtVerdict = holderReports.Count > 0 && holderReports.All(holder =>
                IsLiveIdentity(holder.ProcessId, holder.StartTimeUtcTicks));
            runnerTasksPendingAtVerdict = runnerStarts.Count > 0 && runnerStarts.All(start => !start.Task.IsCompleted);
            if (!holdersAliveAtVerdict || !runnerTasksPendingAtVerdict)
                throw new InvalidDataException("A foreign holder or its actual managed runner was not alive/pending at the executor verdict.");

            if (File.Exists(logFilePath))
                logBytesBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(logFilePath).ConfigureAwait(false));
        }
        finally
        {
            try { WriteGate(producerReleasePath); }
            catch (Exception exception) { cleanupErrors.Add("release producer: " + exception); }
            foreach (var holderStart in runnerStarts)
            {
                try { WriteGate(holderStart.ReleasePath); }
                catch (Exception exception) { cleanupErrors.Add($"release holder {holderStart.Index}: {exception}"); }
            }
            foreach (var holderStart in runnerStarts)
            {
                try
                {
                    var result = await holderStart.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    holderExitCodes.Add(result.ExitCode);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add($"settle holder {holderStart.Index}: {exception}");
                    runnerCancellation.Cancel();
                }
            }
            if (executorTask is not null && !executorTask.IsCompleted)
            {
                try { execution = await executorTask.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (executorFailure is null) executorFailure = CaptureException(exception);
                }
            }
        }

        eventTimes["coordinatorFinished"] = DateTimeOffset.UtcNow;
        var observations = holderReports.Select((holder, index) => new ForeignHolderObservation(
            index,
            runnerStarts[index].CallStartedAtUtc,
            holder,
            holderParentIds[index],
            true,
            holdersAliveAtVerdict,
            runnerTasksPendingAtVerdict,
            index < holderExitCodes.Count ? holderExitCodes[index] : null)).ToArray();
        return new CoordinatorReport(
            scenario,
            coordinatorId,
            coordinatorStart,
            ContinuousAgentLaunchContext.IsActive,
            ContinuousAgentLaunchContext.StopRequested,
            producer,
            holderReports.FirstOrDefault(),
            holderParentIds.FirstOrDefault() is var parent && parent != 0 ? parent : null,
            holdersAliveAtReadiness,
            holdersAliveAtVerdict,
            runnerTasksPendingAtVerdict,
            execution is null ? null : new ExecutionSummary(execution.Status.ToString(), execution.ExitCode),
            executorFailure,
            holderExitCodes.FirstOrDefault() is var holderExit && holderReports.Count > 0 ? holderExit : null,
            logBytesBase64,
            eventTimes,
            observations,
            cleanupErrors);
    }

    private static RunnerStart StartHolder(SystemProcessRunner runner, string fixtureAssembly,
        string workingDirectory, string scenarioDirectory, int index, CancellationToken cancellationToken)
    {
        var readyPath = Path.Combine(scenarioDirectory, $"holder-{index}.json");
        var releasePath = Path.Combine(scenarioDirectory, $"holder-{index}.release");
        var request = new ProcessRequest(
            "dotnet",
            [fixtureAssembly, "stream-cleanup-holder", readyPath, releasePath],
            workingDirectory,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            TimeSpan.FromSeconds(35));
        var calledAt = DateTimeOffset.UtcNow;
        return new RunnerStart(index, calledAt, readyPath, releasePath, runner.RunAsync(request, cancellationToken));
    }

    private static async Task<T> WaitForJsonAsync<T>(string path, TimeSpan timeout,
        Task<AgentExecutionResult>? executorTask = null, string? logFilePath = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    var value = JsonSerializer.Deserialize<T>(json);
                    if (value is not null) return value;
                }
                catch (JsonException) { }
            }

            if (executorTask?.IsCompleted == true)
            {
                Exception? failure = null;
                try { await executorTask.ConfigureAwait(false); }
                catch (Exception exception) { failure = exception; }
                var logText = logFilePath is not null && File.Exists(logFilePath)
                    ? Convert.ToBase64String(await ReadActiveLogAsync(logFilePath).ConfigureAwait(false))
                    : "missing";
                throw new InvalidOperationException(
                    $"Actual executor completed before producer report. failure={failure}; logBase64={logText}", failure);
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        var logAtTimeout = logFilePath is not null && File.Exists(logFilePath)
            ? Convert.ToBase64String(await ReadActiveLogAsync(logFilePath).ConfigureAwait(false))
            : "missing";
        throw new TimeoutException(
            $"Coordinator gate did not become ready: {path}; executorTask={executorTask?.Status.ToString() ?? "not observed"}; logBase64={logAtTimeout}");
    }

    private static async Task<byte[]> ReadActiveLogAsync(string path)
    {
        await using var log = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        using var bytes = new MemoryStream();
        await log.CopyToAsync(bytes).ConfigureAwait(false);
        return bytes.ToArray();
    }

    private static void CloseDuplicateCopies(ProducerReport producer)
    {
        var failures = new List<Exception>();
        if (!CloseHandle(new IntPtr(producer.StdoutHandle)))
            failures.Add(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CloseHandle(stdout duplicate)"));
        if (!CloseHandle(new IntPtr(producer.StderrHandle)))
            failures.Add(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CloseHandle(stderr duplicate)"));
        if (failures.Count > 0) throw new AggregateException("Could not close every coordinator pipe copy.", failures);
    }

    private static IntPtr OpenProcessForDuplicateHandles(int processId)
    {
        const uint processDuplicateHandle = 0x0040;
        var process = OpenProcess(processDuplicateHandle, inheritHandle: false, checked((uint)processId));
        if (process == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(duplicate handles)");
        return process;
    }

    private static void WriteGate(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(bytes);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path)) { }
    }

    private static bool IsLiveIdentity(int processId, long startTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static uint ReadParentProcessId(int processId)
    {
        const uint SnapshotProcess = 0x00000002;
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot");

        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snapshot, ref entry))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Process32First");
        do
        {
            if (entry.ProcessId == (uint)processId) return entry.ParentProcessId;
        } while (Process32Next(snapshot, ref entry));
        throw new InvalidOperationException($"Could not find foreign PID {processId} in the process snapshot.");
    }

    private static ExceptionNode CaptureException(Exception exception)
    {
        var inners = exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Select(CaptureException).ToArray()
            : exception.InnerException is null ? [] : [CaptureException(exception.InnerException)];
        return new ExceptionNode(exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message, exception.StackTrace, inners);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string? ExecutableFile;
    }

    private sealed record ProducerReport(int ProcessId, long StartTimeUtcTicks, long StdoutHandle,
        long StderrHandle, bool Inheritable, DateTimeOffset DuplicatesCreatedAtUtc);
    private sealed record HolderReport(int ProcessId, long StartTimeUtcTicks, DateTimeOffset ReadyAtUtc);
    private sealed record ExecutionSummary(string Status, int? ExitCode);
    private sealed record ExceptionNode(string Type, string Message, string? StackTrace, ExceptionNode[] InnerExceptions);
    private sealed record RunnerStart(int Index, DateTimeOffset CallStartedAtUtc, string ReadyPath, string ReleasePath,
        Task<ProcessResult> Task);
    private sealed record ExecutorStart(DateTimeOffset CalledAtUtc, Task<AgentExecutionResult> Task);
    private sealed record ForeignHolderObservation(int Index, DateTimeOffset RunnerCallStartedAtUtc,
        HolderReport Holder, uint ParentProcessId, bool AliveAtReadiness, bool AliveAtVerdict,
        bool RunnerPendingAtVerdict, int? ExitCode);
    private sealed record CoordinatorReport(string Scenario, int CoordinatorProcessId, long CoordinatorStartTimeUtcTicks,
        bool LaunchContextActive, bool StopRequested, ProducerReport? Producer, HolderReport? Holder,
        uint? HolderParentProcessId, bool HolderAliveAtReadiness, bool HolderAliveAtVerdict,
        bool RunnerPendingAtVerdict, ExecutionSummary? Execution, ExceptionNode? ExecutorFailure,
        int? HolderExitCode, string? LogBytesBase64, IReadOnlyDictionary<string, DateTimeOffset> EventTimes,
        IReadOnlyList<ForeignHolderObservation> ForeignHolders,
        IReadOnlyList<string> CleanupErrors);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry32 entry);
}
