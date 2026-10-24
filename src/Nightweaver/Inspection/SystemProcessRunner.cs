using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

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
            if (!process.Start())
            {
                throw new ToolExecutionException($"required executable '{request.FileName}' was not found on PATH");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new ToolExecutionException($"required executable '{request.FileName}' was not found on PATH");
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

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
            if (timedOut)
            {
                var seconds = request.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                throw new ToolExecutionException($"'{request.FileName}' timed out after {seconds}s", stderr);
            }

            throw new OperationCanceledException(cancellationToken);
        }

        var standardOutput = await stdoutTask;
        var standardError = await stderrTask;
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }
}
