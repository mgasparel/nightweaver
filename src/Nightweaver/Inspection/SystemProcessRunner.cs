using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nightweaver.Execution;

namespace Nightweaver.Inspection;

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            bool started;
            lock (WindowsProcessLauncher.ProcessCreationGate)
            {
                started = process.Start();
            }

            if (!started)
            {
                throw new ToolExecutionException($"required executable '{request.FileName}' was not found on PATH");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new ToolExecutionException($"required executable '{request.FileName}' was not found on PATH");
        }
        catch (Win32Exception exception)
        {
            throw new ToolExecutionException($"required executable '{request.FileName}' could not be started: {exception.Message}", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            throw new ToolExecutionException($"required executable '{request.FileName}' could not be started: {exception.Message}", exception.Message);
        }

        process.StandardInput.Close();
        using var outputLimit = new CancellationTokenSource();
        var exceeded = false;
        async Task<string> ReadOutputAsync(StreamReader reader)
        {
            if (request.MaximumOutputBytes is not { } maximum)
                return await reader.ReadToEndAsync(CancellationToken.None);
            var text = new StringBuilder();
            var buffer = new char[4096];
            var bytes = 0;
            while (true)
            {
                var read = await reader.ReadAsync(buffer, CancellationToken.None);
                if (read == 0) break;
                bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
                if (bytes > maximum)
                {
                    exceeded = true;
                    outputLimit.Cancel();
                    // Drain after killing, without retaining an unbounded prefix.
                    continue;
                }
                if (!exceeded) text.Append(buffer, 0, read);
            }
            return text.ToString();
        }
        var stdoutTask = ReadOutputAsync(process.StandardOutput);
        var stderrTask = ReadOutputAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, outputLimit.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The child exited while cancellation was being handled.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            var timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            var stderr = await stderrTask;
            _ = await stdoutTask;
            if (exceeded) throw new ToolExecutionException("observer output exceeded read limit");
            if (timedOut)
            {
                var seconds = request.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                throw new ToolExecutionException($"'{request.FileName}' timed out after {seconds}s", stderr);
            }

            throw new OperationCanceledException(cancellationToken);
        }

        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;
        if (exceeded) throw new ToolExecutionException("observer output exceeded read limit");
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }
}
