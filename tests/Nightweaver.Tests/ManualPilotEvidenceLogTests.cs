using System.Diagnostics;
using System.Text.Json;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class ManualPilotEvidenceLogTests
{
    [Fact]
    public async Task LegacyAppendCollisionIsReproducedAndCoordinatedAppendAndReadWaitForRelease()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var evidence = temp.WriteFile("agent-evidence.jsonl", "{\"seed\":true}" + Environment.NewLine);
        var legacyReady = Path.Combine(temp.Path, "legacy.ready");
        var legacyRelease = Path.Combine(temp.Path, "legacy.release");
        using var legacyHolder = StartFixture("--evidence-legacy-hold", evidence, legacyReady, legacyRelease);
        RunningChild? legacyAppender = null;
        try
        {
            await WaitForFileAsync(legacyReady, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            legacyAppender = StartFixture("--evidence-legacy-append", evidence, "{\"legacy\":true}");
            var collision = await legacyAppender.FinishAsync(TestContext.Current.CancellationToken);
            Assert.Equal(23, collision.ExitCode);
            Assert.Contains("evidence-legacy-sharing-ioexception", collision.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(legacyRelease, "release");
            await legacyHolder.FinishAsync(TestContext.Current.CancellationToken);
            legacyAppender?.Dispose();
        }

        var mutexReady = Path.Combine(temp.Path, "mutex.ready");
        var mutexRelease = Path.Combine(temp.Path, "mutex.release");
        using var mutexHolder = StartFixture("--evidence-hold-mutex", evidence, mutexReady, mutexRelease);
        RunningChild? coordinatedAppender = null;
        RunningChild? coordinatedReader = null;
        try
        {
            await WaitForFileAsync(mutexReady, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var appendStarted = Path.Combine(temp.Path, "append.started");
            var appendCompleted = Path.Combine(temp.Path, "append.completed");
            var readStarted = Path.Combine(temp.Path, "read.started");
            var readOutput = Path.Combine(temp.Path, "read.output.json");
            coordinatedAppender = StartFixture("--evidence-append", evidence, "{\"coordinated\":true}", appendStarted, appendCompleted);
            coordinatedReader = StartFixture("--evidence-read", evidence, "unused", readStarted, readOutput);

            await WaitForFileAsync(appendStarted, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await WaitForFileAsync(readStarted, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(coordinatedAppender.Process.WaitForExit(200),
                "The fixture append must remain blocked while another process owns the evidence mutex.");
            Assert.False(coordinatedReader.Process.WaitForExit(200),
                "The fixture snapshot read must remain blocked while another process owns the evidence mutex.");

            File.WriteAllText(mutexRelease, "release");
            var appendResult = await coordinatedAppender.FinishAsync(TestContext.Current.CancellationToken);
            var readResult = await coordinatedReader.FinishAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, appendResult.ExitCode);
            Assert.Equal(0, readResult.ExitCode);
            Assert.True(File.Exists(appendCompleted));

            var observedSnapshot = JsonSerializer.Deserialize<string[]>(File.ReadAllText(readOutput))
                ?? throw new InvalidOperationException("The coordinated snapshot probe returned no records.");
            foreach (var line in observedSnapshot) using (JsonDocument.Parse(line)) { }
            var finalLines = ManualPilotEvidenceLog.ReadAllLines(evidence);
            Assert.Equal(2, finalLines.Length);
            Assert.Contains(finalLines, line => line == "{\"coordinated\":true}");
            foreach (var line in finalLines) using (JsonDocument.Parse(line)) { }
        }
        finally
        {
            File.WriteAllText(mutexRelease, "release");
            await FinishIfRunningAsync(coordinatedAppender);
            await FinishIfRunningAsync(coordinatedReader);
            await FinishIfRunningAsync(mutexHolder);
        }
    }

    [Fact]
    public async Task ConcurrentProcessesAppendCompleteUniqueJsonRowsWhileSnapshotsRemainReadable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var evidence = Path.Combine(temp.Path, "agent-evidence.jsonl");
        File.WriteAllText(evidence, string.Empty);
        var release = Path.Combine(temp.Path, "workers.release");
        const int workerCount = 4;
        const int rowsPerWorker = 32;
        var workers = Enumerable.Range(0, workerCount).Select(worker =>
        {
            var ready = Path.Combine(temp.Path, $"worker-{worker}.ready");
            return (Worker: worker, Ready: ready, Child: StartFixture(
                "--evidence-batch", evidence, $"worker-{worker}", rowsPerWorker.ToString(System.Globalization.CultureInfo.InvariantCulture), ready, release));
        }).ToArray();

        var workersFinished = 0;
        var readerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotCount = 0;
        var reader = Task.Run(() =>
        {
            readerStarted.SetResult();
            do
            {
                var snapshot = ManualPilotEvidenceLog.ReadAllLines(evidence);
                foreach (var line in snapshot) using (JsonDocument.Parse(line)) { }
                Interlocked.Increment(ref snapshotCount);
                Thread.Yield();
            } while (Volatile.Read(ref workersFinished) == 0);
        }, TestContext.Current.CancellationToken);

        try
        {
            foreach (var worker in workers)
                await WaitForFileAsync(worker.Ready, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await readerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            File.WriteAllText(release, "release all workers");

            var results = await Task.WhenAll(workers.Select(worker => worker.Child.FinishAsync(TestContext.Current.CancellationToken)));
            Volatile.Write(ref workersFinished, 1);
            await reader.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.All(results, result => Assert.Equal(0, result.ExitCode));
            Assert.True(Volatile.Read(ref snapshotCount) > 0);
            var lines = ManualPilotEvidenceLog.ReadAllLines(evidence);
            Assert.Equal(workerCount * rowsPerWorker, lines.Length);

            var records = lines.Select(line => JsonDocument.Parse(line)).Select(document =>
            {
                using (document)
                {
                    return (Worker: document.RootElement.GetProperty("worker").GetString()!,
                        Sequence: document.RootElement.GetProperty("sequence").GetInt32(),
                        Pid: document.RootElement.GetProperty("pid").GetInt32(),
                        Payload: document.RootElement.GetProperty("payload").GetString()!);
                }
            }).ToArray();
            Assert.Equal(lines.Length, records.Select(record => (record.Worker, record.Sequence)).Distinct().Count());
            Assert.Equal(workerCount, records.Select(record => record.Pid).Distinct().Count());
            Assert.All(records, record => Assert.Equal(new string('界', 2048), record.Payload));
            Assert.All(Enumerable.Range(0, workerCount), worker =>
                Assert.Equal(rowsPerWorker, records.Count(record => record.Worker == $"worker-{worker}")));
        }
        finally
        {
            Volatile.Write(ref workersFinished, 1);
            File.WriteAllText(release, "release all workers");
            foreach (var worker in workers) await FinishIfRunningAsync(worker.Child);
            if (!reader.IsCompleted)
            {
                try { await reader.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
                catch (TimeoutException) { }
            }
        }
    }

    [Fact]
    public void MissingEvidenceDirectoryErrorsAreNotSuppressed()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "missing", "agent-evidence.jsonl");
        Assert.Throws<DirectoryNotFoundException>(() => ManualPilotEvidenceLog.ReadAllLines(missing));
        Assert.Throws<DirectoryNotFoundException>(() => ManualPilotEvidenceLog.AppendLine(missing, "{}"));
    }

    private static RunningChild StartFixture(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return new RunningChild(Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the AgentInvocationFixture child process."));
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for fixture probe file '{path}'.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task FinishIfRunningAsync(RunningChild? child)
    {
        if (child is null || child.IsDisposed) return;
        if (child.Process.HasExited)
        {
            try { await child.FinishAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            child.Dispose();
            return;
        }

        child.Process.Kill(entireProcessTree: true);
        try { await child.FinishAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        child.Dispose();
    }

    private sealed class RunningChild(Process process) : IDisposable
    {
        private readonly Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> standardError = process.StandardError.ReadToEndAsync();
        private bool finished;
        private bool disposed;

        internal Process Process { get; } = process;
        internal bool IsDisposed => disposed;

        internal async Task<ChildResult> FinishAsync(CancellationToken cancellationToken)
        {
            if (!finished)
            {
                try
                {
                    await Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch
                {
                    if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                    throw;
                }

                finished = true;
            }

            return new ChildResult(Process.ExitCode,
                await standardOutput.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken),
                await standardError.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            Process.Dispose();
        }
    }

    private sealed record ChildResult(int ExitCode, string StandardOutput, string StandardError);
}
