using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

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
            var executable = WindowsProcessLauncher.ResolveExecutable(invocation.Executable, environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation.Timeout <= TimeSpan.Zero)
                throw new InvalidDataException($"Prepared timeout '{invocation.Timeout}' must be positive.");

            // The role wall-clock budget starts immediately before native launch and includes prompt delivery.
            var launchStarted = Stopwatch.GetTimestamp();
            child = WindowsProcessLauncher.Launch(executable, invocation.Arguments, invocation.WorkingDirectory, environment);
            rootExit = child.WaitForRootExitAsync();
            logGate = new SemaphoreSlim(1, 1);
            stdoutPump = PumpOutputAsync(child.StandardOutput, runtimeFiles.LogStream, logGate);
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
            var cleanupClock = Stopwatch.StartNew();
            try
            {
                await child.TerminateAndConfirmEmptyAsync(CleanupBudget).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            finally
            {
                child.CloseJob();
            }

            try
            {
                if (rootExit is not null)
                    await rootExit.WaitAsync(GetCleanupTimeLeft(cleanupClock), CancellationToken.None).ConfigureAwait(false);
                if (stdinWrite is not null)
                {
                    try { await stdinWrite.WaitAsync(GetCleanupTimeLeft(cleanupClock), CancellationToken.None).ConfigureAwait(false); }
                    catch (IOException) when (stdinWrite.IsCompleted
                        && (status == AgentExecutionStatus.TimedOut || cancelled || cancellationToken.IsCancellationRequested)) { }
                    catch (Exception exception) when (ReferenceEquals(exception, primaryFailure) && stdinWrite.IsCompleted) { }
                }
                if (stdoutPump is not null)
                {
                    try { await stdoutPump.WaitAsync(GetCleanupTimeLeft(cleanupClock), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception) when (ReferenceEquals(exception, primaryFailure) && stdoutPump.IsCompleted) { }
                }
                if (stderrPump is not null)
                {
                    try { await stderrPump.WaitAsync(GetCleanupTimeLeft(cleanupClock), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception) when (ReferenceEquals(exception, primaryFailure) && stderrPump.IsCompleted) { }
                }
                if (runtimeFiles is not null)
                {
                    await runtimeFiles.FlushLogAsync(CancellationToken.None)
                        .WaitAsync(GetCleanupTimeLeft(cleanupClock), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (primaryFailure is null || !ReferenceEquals(exception, primaryFailure))
                    cleanupFailure = Combine(cleanupFailure, exception);
            }
        }

        try { child?.Dispose(); }
        catch (Exception exception) { cleanupFailure = Combine(cleanupFailure, exception); }
        logGate?.Dispose();
        try { runtimeFiles?.Dispose(); }
        catch (Exception exception) { cleanupFailure = Combine(cleanupFailure, exception); }

        if (cleanupFailure is not null)
        {
            var inner = primaryFailure is null ? cleanupFailure : new AggregateException(primaryFailure, cleanupFailure);
            throw CreateDiagnostic(invocation, inner, "containment, pipe settlement or log cleanup did not complete successfully");
        }

        if (primaryFailure is not null)
            throw CreateDiagnostic(invocation, primaryFailure, Describe(primaryFailure));

        if (cancellationToken.IsCancellationRequested) cancelled = true;
        if (cancelled) throw new OperationCanceledException("Agent invocation was cancelled after its process tree was terminated.", cancellationToken);

        if (status is null)
            throw CreateDiagnostic(invocation, new InvalidOperationException("Execution finished without a process outcome."), "no process outcome was observed");

        return new AgentExecutionResult(status.Value, exitCode, invocation.InvocationId, invocation.LogFilePath);
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

    private static async Task PumpOutputAsync(Stream source, Stream log, SemaphoreSlim logGate)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return;
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

    private static Exception Combine(Exception? first, Exception second) => first is null ? second : new AggregateException(first, second);

    private static TimeSpan GetCleanupTimeLeft(Stopwatch cleanupClock)
    {
        var remaining = CleanupBudget - cleanupClock.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
