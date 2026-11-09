using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Application;
using Nightweaver.Execution;

namespace Nightweaver.Inspection;

internal sealed class BeadsJournalClient : IBeadsJournalClient
{
    private const int MaximumStandardErrorCharacters = 8 * 1024;
    private const int MaximumRecordCharacters = 1024 * 1024;
    private readonly IProcessRunner processRunner;
    private readonly Func<ProcessRequest, Process> startProcess;

    internal BeadsJournalClient(IProcessRunner processRunner, Func<ProcessRequest, Process>? startProcess = null)
    {
        this.processRunner = processRunner;
        this.startProcess = startProcess ?? Start;
    }

    public async Task EnableAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        var request = BeadsCommand.CreateJournalSetup(repository.Path);
        var result = await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BoundDiagnostic(
                "bd config set events-journal true failed", result.StandardError, result.StandardOutput));
        }
    }

    public async Task FollowAsync(
        ValidatedTargetRepository repository,
        long afterSequence,
        Action<long> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var request = BeadsCommand.CreateJournalTail(repository.Path, afterSequence);
        using var process = startProcess(request);
        var standardError = ReadBoundedAsync(process.StandardError, MaximumStandardErrorCharacters);

        try
        {
            await foreach (var record in ReadJsonRecordsAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false))
            {
                using var document = JsonDocument.Parse(record);
                var root = document.RootElement;
                if (TryReadTruncation(root, out var head))
                    throw new BeadsJournalTruncatedException(head);
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("seq", out var sequenceValue)
                    || !sequenceValue.TryGetInt64(out var sequence)
                    || sequence < 0)
                {
                    throw new InvalidDataException("bd events tail emitted a record without a nonnegative int64 seq.");
                }

                // Event payloads are intentionally ignored. Current tracker reads still decide eligibility.
                onEvent(sequence);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var diagnostic = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                if (TryReadTruncation(diagnostic, out var head))
                    throw new BeadsJournalTruncatedException(head);
                throw new InvalidOperationException(BoundDiagnostic(
                    $"bd events tail exited with code {process.ExitCode}", diagnostic));
            }
        }
        catch
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            _ = await standardError.ConfigureAwait(false);
            throw;
        }
    }

    private static Process Start(ProcessRequest request)
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
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Environment)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            bool started;
            lock (WindowsProcessLauncher.ProcessCreationGate)
            {
                started = process.Start();
            }

            if (!started) throw new InvalidOperationException("Could not start bd events tail.");
            process.StandardInput.Close();
            return process;
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start bd events tail: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start bd events tail: {exception.Message}", exception);
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunk = new char[4096];
        var line = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
                yield break;
            }

            for (var index = 0; index < count; index++)
            {
                if (chunk[index] == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                    continue;
                }

                if (line.Length >= MaximumRecordCharacters)
                    throw new InvalidDataException($"bd events tail record exceeded {MaximumRecordCharacters} characters.");
                line.Append(chunk[index]);
            }
        }
    }

    private static async IAsyncEnumerable<string> ReadJsonRecordsAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var record = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;

        await foreach (var line in ReadLinesAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (record.Length == 0)
            {
                var first = line.AsSpan().TrimStart();
                if (first.IsEmpty || first[0] is not ('{' or '['))
                    throw new InvalidDataException("bd events tail emitted a non-JSON record.");
            }
            else
            {
                record.Append('\n');
            }

            record.Append(line);
            foreach (var character in line)
            {
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }

                if (character == '"') inString = true;
                else if (character is '{' or '[') depth++;
                else if (character is '}' or ']') depth--;

                if (depth < 0)
                    throw new InvalidDataException("bd events tail emitted unbalanced JSON delimiters.");
            }

            if (record.Length > MaximumRecordCharacters)
                throw new InvalidDataException($"bd events tail record exceeded {MaximumRecordCharacters} characters.");
            if (depth == 0 && !inString)
            {
                yield return record.ToString();
                record.Clear();
                escaped = false;
            }
        }

        if (record.Length > 0)
            throw new InvalidDataException("bd events tail ended with an incomplete JSON record.");
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, int maximumCharacters)
    {
        var chunk = new char[2048];
        var retained = new StringBuilder(maximumCharacters);
        while (true)
        {
            var count = await reader.ReadAsync(chunk.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (count == 0) return retained.ToString();

            if (count >= maximumCharacters)
            {
                retained.Clear();
                retained.Append(chunk, count - maximumCharacters, maximumCharacters);
                continue;
            }

            var excess = retained.Length + count - maximumCharacters;
            if (excess > 0) retained.Remove(0, excess);
            retained.Append(chunk, 0, count);
        }
    }

    private static bool TryReadTruncation(string text, out long head)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (TryReadTruncation(document.RootElement, out head)) return true;
            }
            catch (JsonException)
            {
                // Diagnostic text can surround the structured CLI error.
            }
        }

        head = 0;
        return false;
    }

    private static bool TryReadTruncation(JsonElement root, out long head)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
            && code.GetString() == "events_journal_truncated"
            && root.TryGetProperty("head", out var headValue)
            && headValue.TryGetInt64(out head)
            && head >= 0)
        {
            return true;
        }

        head = 0;
        return false;
    }

    private static string BoundDiagnostic(string message, params string[] diagnostics)
    {
        var details = string.Join(" ", diagnostics.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (details.Length > MaximumStandardErrorCharacters)
            details = details[^MaximumStandardErrorCharacters..];
        return string.IsNullOrWhiteSpace(details) ? message : $"{message}: {details.Trim()}";
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The child exited while cancellation or parser failure was being handled.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // The process has either exited already or will be retried by the next connection cycle.
        }
    }
}
