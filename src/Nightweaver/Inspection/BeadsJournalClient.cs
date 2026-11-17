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
    internal const int MaximumBatchBytes = 8 * 1024 * 1024;
    private const int MaximumRecordBytes = 1024 * 1024;
    private readonly IProcessRunner processRunner;
    private readonly Func<ProcessRequest, Process> startProcess;
    private readonly TimeProvider timeProvider;

    internal BeadsJournalClient(IProcessRunner processRunner, Func<ProcessRequest, Process>? startProcess = null, TimeProvider? timeProvider = null)
    {
        this.processRunner = processRunner;
        this.timeProvider = timeProvider ?? TimeProvider.System;
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

    public async Task<IReadOnlyList<long>> ReadBatchAsync(
        ValidatedTargetRepository repository,
        long afterSequence,
        int maxRecords,
        CancellationToken cancellationToken)
    {
        var request = BeadsCommand.CreateJournalBatch(repository.Path, afterSequence, maxRecords);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(request.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = startProcess(request);
        var standardError = ReadBoundedAsync(process.StandardError, MaximumStandardErrorCharacters, linked.Token);

        try
        {
            IReadOnlyList<long> sequences = [];
            long? truncatedHead = null;
            try { sequences = await ParseBatchAsync(process.StandardOutput, maxRecords, linked.Token).ConfigureAwait(false); }
            catch (BeadsJournalTruncatedException exception) { truncatedHead = exception.Head; }
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var diagnostic = await standardError.ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (truncatedHead is { } reportedHead) throw new BeadsJournalTruncatedException(reportedHead);
            if (process.ExitCode != 0)
            {
                if (TryReadTruncation(diagnostic, out var head))
                    throw new BeadsJournalTruncatedException(head);
                throw new InvalidOperationException(BoundDiagnostic(
                    $"bd events tail exited with code {process.ExitCode}", diagnostic));
            }
            return sequences;
        }
        catch
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            try { _ = await standardError.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException exception)
            {
                throw new BeadsJournalSettlementException("Beads journal stderr did not settle; watcher stopped.", exception);
            }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("bd events batch exceeded its 30-second command budget.");
            throw;
        }
    }

    internal static async Task<IReadOnlyList<long>> ParseBatchAsync(
        StreamReader reader, int maxRecords, CancellationToken cancellationToken)
    {
        var sequences = new List<long>();
        long? truncatedHead = null;
        var count = 0;
        await foreach (var record in ReadJsonRecordsAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (++count > maxRecords)
                throw new InvalidDataException("bd events batch exceeded its record limit.");
            using var document = JsonDocument.Parse(record);
            var root = document.RootElement;
            if (TryReadTruncation(root, out var head))
            {
                truncatedHead = head;
                continue;
            }
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("seq", out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var sequence) || sequence < 0)
                throw new InvalidDataException("bd events tail emitted a record without a nonnegative int64 seq.");
            // Payloads never establish eligibility.
            sequences.Add(sequence);
        }
        if (truncatedHead is { } reportedHead) throw new BeadsJournalTruncatedException(reportedHead);
        return sequences;
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
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
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
        var totalBytes = 0;
        while (true)
        {
            var count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
                yield break;
            }

            totalBytes += Encoding.UTF8.GetByteCount(chunk.AsSpan(0, count));
            if (totalBytes > MaximumBatchBytes)
                throw new InvalidDataException("bd events batch exceeded its 8 MiB output limit.");
            for (var index = 0; index < count; index++)
            {
                if (chunk[index] == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                    continue;
                }

                if (line.Length >= MaximumRecordBytes)
                    throw new InvalidDataException($"bd events tail record exceeded {MaximumRecordBytes} bytes.");
                line.Append(chunk[index]);
            }
        }
    }

    private static async IAsyncEnumerable<string> ReadJsonRecordsAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var record = new StringBuilder();
        var recordBytes = 0;
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
                recordBytes++;
            }

            recordBytes += Encoding.UTF8.GetByteCount(line);
            if (recordBytes > MaximumRecordBytes)
                throw new InvalidDataException($"bd events tail record exceeded {MaximumRecordBytes} bytes.");
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

            if (depth == 0 && !inString)
            {
                yield return record.ToString();
                record.Clear();
                recordBytes = 0;
                escaped = false;
            }
        }

        if (record.Length > 0)
            throw new InvalidDataException("bd events tail ended with an incomplete JSON record.");
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var chunk = new char[2048];
        var retained = new StringBuilder(maximumCharacters);
        while (true)
        {
            var count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
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
        catch (Win32Exception exception)
        {
            throw new BeadsJournalSettlementException("Could not terminate Beads journal child; watcher stopped.", exception);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            throw new BeadsJournalSettlementException("Beads journal child settlement is uncertain; watcher stopped without replacement.", exception);
        }
    }
}
