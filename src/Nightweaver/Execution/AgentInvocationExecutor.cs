using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

internal interface IAgentProcessLauncher
{
    string ResolveExecutable(string name, IReadOnlyDictionary<string, string> environment);

    WindowsProcessLauncher.WindowsAgentProcess Launch(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string> environment);
}

/// <summary>Executes one already-prepared invocation and returns only after its complete process job is empty.</summary>
internal sealed class AgentInvocationExecutor
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(10);

    internal static async Task<AgentExecutionResult> ExecuteAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        PreparedAgentInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(hostPaths, project, invocation, null, null, cancellationToken).ConfigureAwait(false);
        return result.Execution;
    }

    internal static async Task<AgentExecutionResult> ExecuteAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        PreparedAgentInvocation invocation,
        IAgentProcessLauncher processLauncher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processLauncher);
        var result = await ExecuteCoreAsync(hostPaths, project, invocation, null, processLauncher, cancellationToken)
            .ConfigureAwait(false);
        return result.Execution;
    }

    internal static Task<AgentStandardOutputCaptureResult> ExecuteWithStandardOutputCaptureAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        PreparedAgentInvocation invocation,
        int maximumCapturedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCapturedBytes);
        return ExecuteCoreAsync(hostPaths, project, invocation, maximumCapturedBytes, null, cancellationToken);
    }

    internal static Task<AgentStandardOutputCaptureResult> ExecuteWithStandardOutputCaptureAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        PreparedAgentInvocation invocation,
        int maximumCapturedBytes,
        IAgentProcessLauncher processLauncher,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCapturedBytes);
        ArgumentNullException.ThrowIfNull(processLauncher);
        return ExecuteCoreAsync(hostPaths, project, invocation, maximumCapturedBytes, processLauncher, cancellationToken);
    }

    private static async Task<AgentStandardOutputCaptureResult> ExecuteCoreAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        PreparedAgentInvocation invocation,
        int? maximumCapturedBytes,
        IAgentProcessLauncher? processLauncher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();

        string beadId = invocation.EnvironmentOverrides.TryGetValue("NIGHTWEAVER_BEAD_ID", out var id) ? id : "(missing)";
        InvocationRuntimeFiles? runtimeFiles = null;
        WindowsProcessLauncher.WindowsAgentProcess? child = null;
        Task<uint>? rootExit = null;
        Task? stdinWrite = null;
        Task? stdoutPump = null;
        Task? stderrPump = null;
        SemaphoreSlim? logGate = null;
        var stdoutCapture = maximumCapturedBytes is null ? null : new BoundedStandardOutputCapture(maximumCapturedBytes.Value);
        Exception? primaryFailure = null;
        Exception? cleanupFailure = null;
        AgentExecutionStatus? status = null;
        int? exitCode = null;
        var cancelled = false;

        try
        {
            runtimeFiles = InvocationRuntimeFiles.Reserve(hostPaths, project, beadId, invocation);
            cancellationToken.ThrowIfCancellationRequested();
            var environment = BuildInheritedEnvironment(invocation.EnvironmentOverrides);
            var executable = processLauncher is null
                ? WindowsProcessLauncher.ResolveExecutable(invocation.Executable, environment)
                : processLauncher.ResolveExecutable(invocation.Executable, environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation.Timeout <= TimeSpan.Zero)
                throw new InvalidDataException($"Prepared timeout '{invocation.Timeout}' must be positive.");

            // The role wall-clock budget starts immediately before native launch and includes prompt delivery.
            var launchStarted = Stopwatch.GetTimestamp();
            child = processLauncher is null
                ? WindowsProcessLauncher.Launch(executable, invocation.Arguments, invocation.WorkingDirectory, environment)
                : processLauncher.Launch(executable, invocation.Arguments, invocation.WorkingDirectory, environment);
            rootExit = child.WaitForRootExitAsync();
            logGate = new SemaphoreSlim(1, 1);
            stdoutPump = PumpOutputAsync(child.StandardOutput, runtimeFiles.LogStream, logGate, stdoutCapture);
            stderrPump = PumpOutputAsync(child.StandardError, runtimeFiles.LogStream, logGate);
            stdinWrite = WriteInputAndCloseAsync(child.StandardInput, Encoding.UTF8.GetBytes(invocation.StandardInput));

            var outcome = await WaitForOutcomeAsync(rootExit, stdinWrite, stdoutPump, stderrPump,
                invocation.Timeout, launchStarted, cancellationToken).ConfigureAwait(false);
            status = outcome.Status;
            exitCode = outcome.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (child is not null)
        {
            try
            {
                await child.TerminateAndConfirmEmptyAsync(CleanupBudget).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = new IOException("process containment: " + exception.Message, exception);
            }
            finally
            {
                child.CloseJob();
            }

            // Termination has its own budget; slow process teardown must not starve pipe settlement.
            cleanupFailure = CombineOptional(cleanupFailure, await SettleCleanupAsync(
                [("root exit", () => rootExit ?? Task.CompletedTask),
                 ("stdin delivery", async () =>
                 {
                     if (stdinWrite is null) return;
                     try { await stdinWrite.ConfigureAwait(false); }
                     catch (IOException) when (status == AgentExecutionStatus.TimedOut || cancelled || cancellationToken.IsCancellationRequested) { }
                     catch (Exception exception) when (ReferenceEquals(exception, primaryFailure)) { }
                 }),
                 ("stdout drain", () => stdoutPump ?? Task.CompletedTask),
                 ("stderr drain", () => stderrPump ?? Task.CompletedTask)], CleanupBudget).ConfigureAwait(false));
            // Flush only after writers have settled; never race a pending pump with a log flush.
            if ((stdoutPump?.IsCompleted ?? true) && (stderrPump?.IsCompleted ?? true) && runtimeFiles is not null)
                cleanupFailure = CombineOptional(cleanupFailure, await SettleCleanupAsync(
                    [("log flush", () => runtimeFiles.FlushLogAsync(CancellationToken.None))], CleanupBudget).ConfigureAwait(false));

        }

        try { child?.Dispose(); }
        catch (Exception exception) { cleanupFailure = Combine(cleanupFailure, exception); }
        logGate?.Dispose();
        try { runtimeFiles?.Dispose(); }
        catch (Exception exception) { cleanupFailure = Combine(cleanupFailure, exception); }

        if (cleanupFailure is not null)
        {
            var inner = primaryFailure is null ? cleanupFailure : new AggregateException(primaryFailure, cleanupFailure);
            throw CreateDiagnostic(invocation, inner,
                $"cleanup failed after outcome={((cancelled || cancellationToken.IsCancellationRequested) ? "Cancelled" : status?.ToString() ?? "Failed")}, " +
                $"exit_code={exitCode?.ToString() ?? "unknown"}, timeout={invocation.Timeout}: {inner}");
        }

        if (primaryFailure is not null)
            throw CreateDiagnostic(invocation, primaryFailure, Describe(primaryFailure));

        if (cancellationToken.IsCancellationRequested) cancelled = true;
        if (cancelled) throw new OperationCanceledException("Agent invocation was cancelled after its process tree was terminated.", cancellationToken);

        if (status is null)
            throw CreateDiagnostic(invocation, new InvalidOperationException("Execution finished without a process outcome."), "no process outcome was observed");

        return new AgentStandardOutputCaptureResult(
            new AgentExecutionResult(status.Value, exitCode, invocation.InvocationId, invocation.LogFilePath),
            stdoutCapture?.Snapshot() ?? [],
            stdoutCapture?.Overflowed ?? false);
    }

    internal static IReadOnlyDictionary<string, string> BuildInheritedEnvironment(IReadOnlyDictionary<string, string> overrides)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }
        foreach (var pair in overrides)
        {
            var existing = environment.Keys.FirstOrDefault(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) environment.Remove(existing);
            environment[pair.Key] = pair.Value;
        }
        return environment;
    }

    private static async Task<(AgentExecutionStatus Status, int? ExitCode)> WaitForOutcomeAsync(
        Task<uint> rootExit,
        Task stdinWrite,
        Task stdoutPump,
        Task stderrPump,
        TimeSpan timeout,
        long started,
        CancellationToken cancellationToken)
    {
        var remainingTasks = new HashSet<Task> { rootExit, stdinWrite, stdoutPump, stderrPump };
        var cancellationTask = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rootExit.IsCompleted)
            {
                var code = await rootExit.ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(started) >= timeout)
                    return (AgentExecutionStatus.TimedOut, unchecked((int)code));
                if (!stdinWrite.IsCompleted)
                    throw new IOException("Agent CLI exited before the prepared stdin payload had been delivered.");
                await stdinWrite.ConfigureAwait(false);
                if (stdoutPump.IsCompleted) await stdoutPump.ConfigureAwait(false);
                if (stderrPump.IsCompleted) await stderrPump.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return (AgentExecutionStatus.Exited, unchecked((int)code));
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout) return (AgentExecutionStatus.TimedOut, null);
            var left = timeout - elapsed;
            var delay = left > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : left;
            var timeoutTask = Task.Delay(delay, CancellationToken.None);
            var all = remainingTasks.Append(timeoutTask).Append(cancellationTask).ToArray();
            var completed = await Task.WhenAny(all).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == cancellationTask) throw new OperationCanceledException(cancellationToken);
            if (completed == timeoutTask)
            {
                if (rootExit.IsCompleted) continue;
                return (AgentExecutionStatus.TimedOut, null);
            }

            if (completed == rootExit) continue;
            if (!remainingTasks.Remove(completed)) continue;
            // A completed input/pump task is awaited here so early stdin closure and pipe/log faults are failures.
            await completed.ConfigureAwait(false);
        }
    }

    private static async Task WriteInputAndCloseAsync(Stream input, byte[] payload)
    {
        try
        {
            await input.WriteAsync(payload).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new IOException("Agent CLI closed stdin before the prepared payload was delivered.", exception);
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task PumpOutputAsync(
        Stream source,
        Stream log,
        SemaphoreSlim logGate,
        BoundedStandardOutputCapture? capture = null)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return;
            capture?.Append(buffer.AsSpan(0, count));
            await logGate.WaitAsync().ConfigureAwait(false);
            try { await log.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false); }
            finally { logGate.Release(); }
        }
    }

    private static AgentInvocationExecutionException CreateDiagnostic(PreparedAgentInvocation invocation, Exception exception, string message)
    {
        var native = FindWin32Exception(exception);
        var detail = native is null ? message : $"{message} (Win32 error {native.NativeErrorCode}: {native.Message})";
        return new AgentInvocationExecutionException(invocation.InvocationId, invocation.LogFilePath, detail, exception);
    }

    private static string Describe(Exception exception) => exception switch
    {
        FileNotFoundException => exception.Message,
        NotSupportedException => exception.Message,
        InvalidDataException => exception.Message,
        IOException => exception.Message,
        Win32Exception => exception.Message,
        _ => $"{exception.GetType().Name}: {exception.Message}"
    };

    private static Win32Exception? FindWin32Exception(Exception exception) => exception switch
    {
        Win32Exception win32 => win32,
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.Select(FindWin32Exception).FirstOrDefault(item => item is not null),
        _ when exception.InnerException is not null => FindWin32Exception(exception.InnerException),
        _ => null
    };

    internal static async Task<Exception?> SettleCleanupAsync(
        IReadOnlyList<(string Phase, Func<Task> Run)> steps, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        Exception? failure = null;
        foreach (var (phase, run) in steps)
        {
            Task? task = null;
            try
            {
                task = run();
                var left = budget - clock.Elapsed;
                await task.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, new IOException($"{phase}: {exception.GetType().Name}: {exception.Message}", exception));
                // A bounded wait does not cancel the underlying operation. Observe any eventual fault.
                if (task is not null)
                    _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return failure;
    }

    private static Exception? CombineOptional(Exception? first, Exception? second) =>
        second is null ? first : Combine(first, second);

    private static Exception Combine(Exception? first, Exception second) => first is null ? second : new AggregateException(first, second);

    private sealed class BoundedStandardOutputCapture(int maximumBytes)
    {
        private readonly byte[] bytes = new byte[maximumBytes];
        private int length;

        internal bool Overflowed { get; private set; }

        internal void Append(ReadOnlySpan<byte> chunk)
        {
            var copied = Math.Min(bytes.Length - length, chunk.Length);
            if (copied > 0)
            {
                chunk[..copied].CopyTo(bytes.AsSpan(length));
                length += copied;
            }
            if (copied != chunk.Length) Overflowed = true;
        }

        internal byte[] Snapshot() => bytes.AsSpan(0, length).ToArray();
    }
}
