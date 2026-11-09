using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Tests;

public sealed class NativeOperationalRetryTimeoutIntegrationTests : AgentStageIntegrationTestSupport
{
    private static readonly TimeSpan FirstAttemptBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task HeldNativeImplementerTimesOutAndRetriesWithinOneClaimedDispatch()
    {
        Assert.True(OperatingSystem.IsWindows(), "This integration requires Win32 Job Objects and native fixture executables.");
        using var target = new AgentStageTargetFixture();
        var beadId = target.CreateBead("Native retry timeout cause proof", "task", "stage:implement");
        target.RunTarget("bd", ["update", beadId, "--claim"]);
        var claimed = target.ReadState(beadId);
        Assert.Equal("in_progress", claimed.Status);
        var actor = target.ReadBead(beadId).Assignee
            ?? throw new InvalidOperationException("The native cause-proof Bead was not claimed.");

        var gateDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "native-retry-gates")).FullName;
        var evidencePath = Path.Combine(target.RootPath, "native-retry-evidence.jsonl");
        var startedPath = Path.Combine(gateDirectory, beadId + ".started");
        var releasePath = Path.Combine(gateDirectory, beadId + ".release");
        var pidDirectory = Directory.CreateDirectory(Path.Combine(target.RootPath, "native-retry-pids")).FullName;
        var project = target.CreateProject(AgentCli.Codex) with { Limits = new LimitsConfig(3, 1) };
        var systemProcessRunner = new SystemProcessRunner();
        var processRunner = new RecordingProcessRunner((request, cancellationToken) =>
            target.RunWithFixturePullRequestAsync(systemProcessRunner, request, cancellationToken));
        var preparer = new AgentInvocationPreparer();
        var attemptNumber = 0;
        var attemptNumberForExecution = 0;
        var attempts = new ConcurrentQueue<NativeAttempt>();
        var progress = new ConcurrentQueue<OperationalRetryProgress>();

        async Task<PreparedAgentInvocation> Prepare(HostPaths hostPaths, ProjectConfig configuration, AgentRole role,
            AgentDispatchContext context, string worktreePath, CancellationToken cancellationToken)
        {
            var prepared = await preparer.PrepareAsync(hostPaths, configuration, role, context, worktreePath, cancellationToken);
            var attempt = Interlocked.Increment(ref attemptNumber);
            return attempt == 1 ? WithTimeout(prepared, FirstAttemptBudget) : prepared;
        }

        async Task<AgentExecutionResult> Execute(HostPaths hostPaths, ProjectSlug projectSlug,
            PreparedAgentInvocation invocation, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref attemptNumberForExecution);
            var pidPath = Path.Combine(pidDirectory, number.ToString(CultureInfo.InvariantCulture) + ".txt");
            var start = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            var result = await AgentInvocationExecutor.ExecuteAsync(hostPaths, projectSlug, invocation,
                target.CreateProcessLauncher(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BD_ACTOR"] = actor,
                    ["NIGHTWEAVER_FIXTURE_ACTION"] = "manual-delivery-pilot",
                    ["NIGHTWEAVER_FIXTURE_PID_FILE"] = pidPath,
                    ["NIGHTWEAVER_FIXTURE_PILOT_EVIDENCE"] = evidencePath,
                    ["NIGHTWEAVER_FIXTURE_PILOT_GATE_DIRECTORY"] = gateDirectory,
                    ["NIGHTWEAVER_FIXTURE_PILOT_GATE_BEAD"] = beadId
                }), cancellationToken);
            elapsed.Stop();
            attempts.Enqueue(new(number, invocation.InvocationId, invocation.LogFilePath, invocation.Timeout,
                start, start + invocation.Timeout, DateTimeOffset.UtcNow, elapsed.Elapsed, result, pidPath));
            return result;
        }

        var stages = new AgentStageCoordinator(processRunner, prepare: Prepare, execute: Execute);
        var retries = new OperationalRetryCoordinator(processRunner, stages: stages,
            outcomeClassifier: FixtureOutcomeClassifier.Create());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Task<OperationalRetryOutcome>? dispatch = null;
        Exception? primaryFailure = null;

        try
        {
            // One direct RunAsync call is this harness's single claimed dispatch boundary. The test intentionally
            // bypasses pool admission and proves the retry coordinator's native attempts within that one dispatch.
            dispatch = retries.RunAsync(target.Host, project, new(target.Path, "main"), beadId,
                AgentRole.Implementer, actor, timeout.Token, progress.Enqueue);

            var before = Stopwatch.StartNew();
            await WaitForFileAsync(startedPath, TimeSpan.FromSeconds(12), timeout.Token);
            var firstIdentity = ReadProcessIdentity(Path.Combine(pidDirectory, "1.txt"));
            var firstReadyElapsed = before.Elapsed;
            Assert.True(firstReadyElapsed < FirstAttemptBudget,
                $"First native attempt reached its gate after its prepared budget. ready={firstReadyElapsed}; budget={FirstAttemptBudget}.");

            var evidence = await WaitForEvidenceCountAsync(evidencePath, 2, TimeSpan.FromSeconds(60), timeout.Token);
            var capturedFirst = Assert.Single(attempts, attempt => attempt.Number == 1);
            Assert.Equal(AgentExecutionStatus.TimedOut, capturedFirst.Result.Status);
            Assert.Equal(capturedFirst.InvocationId, capturedFirst.Result.InvocationId);
            Assert.Equal(capturedFirst.LogPath, capturedFirst.Result.LogFilePath);
            Assert.Equal(FirstAttemptBudget, capturedFirst.PreparedBudget);
            Assert.True(capturedFirst.CompletedAt >= capturedFirst.Deadline,
                $"The first invocation settled before its deadline. start={capturedFirst.StartedAt:O}; deadline={capturedFirst.Deadline:O}; completed={capturedFirst.CompletedAt:O}; elapsed={capturedFirst.Elapsed}.");
            Assert.InRange(capturedFirst.Elapsed, FirstAttemptBudget - TimeSpan.FromSeconds(1),
                FirstAttemptBudget + TimeSpan.FromSeconds(10));

            Assert.Equal(2, evidence.Length); // The historical pre-signal Assert.Single expectation is red for this proven retry.
            Assert.All(evidence, row =>
            {
                Assert.Equal(beadId, row.BeadId);
                Assert.Equal("implementer", row.Role);
                Assert.Equal("stage:implement", row.Stage);
            });
            var secondIdentity = ReadProcessIdentity(Path.Combine(pidDirectory, "2.txt"));
            Assert.Equal(firstIdentity.ProcessId, evidence[0].ProcessId);
            Assert.Equal(secondIdentity.ProcessId, evidence[1].ProcessId);
            Assert.NotEqual(firstIdentity.ProcessId, secondIdentity.ProcessId);
            Assert.Equal(evidence[0].HeadBefore, evidence[1].HeadBefore);
            Assert.Equal(target.RunTarget("git", ["rev-parse", "origin/main"], target.Path).Trim(), evidence[0].HeadBefore);
            Assert.Equal(secondIdentity.ProcessId.ToString(CultureInfo.InvariantCulture), File.ReadAllText(startedPath).Trim());
            Assert.True(IsProcessRunning(secondIdentity), "The second native Agent must still be held at its gate before release.");
            AssertProcessExited(firstIdentity);

            var retryProgress = Assert.Single(progress,
                item => item.Kind == OperationalRetryProgressKind.RetryPreparing && item.Attempt == 2);
            Assert.Equal(AgentOperationalFailureKind.TimedOut, retryProgress.FailureKind);
            Assert.Equal(capturedFirst.InvocationId, retryProgress.InvocationId);
            Assert.Equal(capturedFirst.LogPath, retryProgress.LogFilePath);
            Assert.False(dispatch.IsCompleted, "The single claimed dispatch should remain held in its second native attempt.");
            var stillClaimed = target.ReadState(beadId);
            Assert.Equal("in_progress", stillClaimed.Status);
            Assert.Equal(actor, target.ReadBead(beadId).Assignee);
            Assert.Equal("stage:implement", stillClaimed.Stage);

            await File.WriteAllTextAsync(releasePath, "release", cancellationToken: timeout.Token);
            var outcome = await dispatch.WaitAsync(TimeSpan.FromSeconds(90), timeout.Token);
            Assert.True(outcome.Succeeded,
                $"Native retry did not complete: status={outcome.Status}; failure={outcome.StageResult.Failure?.Kind}: {outcome.StageResult.Failure?.Reason}; log={outcome.StageResult.LogFilePath}");
            Assert.Equal(2, outcome.AttemptCount);
            Assert.Equal(2, attempts.Count);
            Assert.Equal("stage:review", target.ReadState(beadId).Stage);
            Assert.Equal("open", target.ReadBead(beadId).Status);
            Assert.Null(target.ReadBead(beadId).Assignee);
            AssertProcessExited(secondIdentity);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            File.WriteAllText(releasePath, "release");
            if (dispatch is not null)
            {
                try { await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
                catch (Exception) when (primaryFailure is not null) { }
            }
            if (primaryFailure is not null)
                CopyDirectory(target.RootPath, Path.Combine(Path.GetTempPath(), "nightweaver-native-retry-failure-" + Guid.NewGuid().ToString("N")));
        }
    }

    private static PreparedAgentInvocation WithTimeout(PreparedAgentInvocation invocation, TimeSpan timeout) =>
        new(invocation.Executable, invocation.Arguments, invocation.WorkingDirectory, invocation.StandardInput,
            invocation.EnvironmentOverrides, timeout, invocation.InvocationId, invocation.TemporaryDirectory,
            invocation.LogFilePath, invocation.Purpose, invocation.Stage, invocation.RepairCycles,
            invocation.MaxRepairCycles, invocation.ConfiguredModel, invocation.Effort);

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed >= timeout)
                throw new TimeoutException($"Native attempt did not reach its held gate within {timeout}: {path}");
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task<NativeEvidence[]> WaitForEvidenceCountAsync(string path, int expectedCount,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = ReadEvidence(path);
            if (rows.Length >= expectedCount) return rows;
            await Task.Delay(25, cancellationToken);
        }
        throw new TimeoutException($"Native evidence remained below {expectedCount} rows within {timeout}: {path}");
    }

    private static NativeEvidence[] ReadEvidence(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var rows = new List<NativeEvidence>();
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                rows.Add(new(row.GetProperty("bead_id").GetString()!, row.GetProperty("role").GetString()!,
                    row.GetProperty("stage").GetString()!, row.GetProperty("pid").GetInt32(),
                    row.GetProperty("head_before").GetString()!));
            }
            catch (JsonException) { }
        }
        return rows.ToArray();
    }

    private static ProcessIdentity ReadProcessIdentity(string path)
    {
        var parts = File.ReadAllText(path).Split('|', StringSplitOptions.TrimEntries);
        Assert.Equal(2, parts.Length);
        return new(int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static bool IsProcessRunning(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTimeTicks;
        }
        catch (ArgumentException) { return false; }
    }

    private static void AssertProcessExited(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartTimeTicks) return;
            Assert.True(process.WaitForExit(5000), $"Native fixture process {identity.ProcessId} survived its executor outcome.");
        }
        catch (ArgumentException) { }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private sealed record NativeAttempt(int Number, string InvocationId, string LogPath, TimeSpan PreparedBudget,
        DateTimeOffset StartedAt, DateTimeOffset Deadline, DateTimeOffset CompletedAt, TimeSpan Elapsed,
        AgentExecutionResult Result, string ProcessIdentityPath);

    private sealed record ProcessIdentity(int ProcessId, long StartTimeTicks);
    private sealed record NativeEvidence(string BeadId, string Role, string Stage, int ProcessId, string HeadBefore);
}
