using System.Text;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

/// <summary>Finds one complete evidence frame without retaining unrelated invocation output.</summary>
internal static class MergerLocalCheckReader
{
    internal const int MaximumLogBytes = 8 * 1024 * 1024;
    internal const int MaximumEvidenceBytes = 32 * 1024;
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("NIGHTWEAVER_LOCAL_CHECK_V1");

    internal static async Task<FailureLogTail> ReadAsync(HostPaths paths, ProjectSlug project,
        AgentStageResult invocation, CancellationToken cancellationToken)
    {
        using var pinned = InvocationRuntimeFiles.OpenExistingSettledLogForRead(
            paths, project, invocation.BeadId, invocation.InvocationId, invocation.LogFilePath);
        var stream = pinned.Stream;
        var length = stream.Length;
        if (length > MaximumLogBytes)
            throw new InvalidDataException("Local check log exceeds the bounded 8 MiB scan limit.");

        // Scan every byte for marker mentions, including quoted or malformed frames.
        // Marker has no internal 'N', so a mismatch can restart at the current byte.
        var buffer = new byte[4096];
        var positions = new List<long>(2);
        var matched = 0;
        long position = 0;
        while (position < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - position)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Local check log ended before its settled length.");
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                matched = value == Marker[matched] ? matched + 1 : value == Marker[0] ? 1 : 0;
                if (matched != Marker.Length) continue;
                positions.Add(position + index + 1 - Marker.Length);
                matched = 0;
                if (positions.Count > 2)
                    throw new InvalidDataException("Local check framing is ambiguous: duplicate or quoted marker mentions.");
            }
            position += count;
        }
        if (stream.Length != length || positions.Count != 2)
            throw new InvalidDataException("The settled log does not contain one complete local check transcript for this invocation.");

        var start = positions[0];
        var end = positions[1] + Marker.Length;
        if (end - start > MaximumEvidenceBytes)
            throw new InvalidDataException("Local check evidence exceeds the bounded 32 KiB frame limit.");
        var offset = Math.Max(0, start - 1);
        var bytes = new byte[(int)(Math.Min(length, end + 2) - offset)];
        stream.Position = offset;
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var first = (int)(start - offset);
        var last = (int)(end - offset);
        if (first > 0 && bytes[0] != (byte)'\n'
            || last < bytes.Length && bytes[last] != (byte)'\n'
                && !(bytes[last] == (byte)'\r' && last + 1 < bytes.Length && bytes[last + 1] == (byte)'\n'))
            throw new InvalidDataException("Local check framing must occupy complete unquoted lines.");

        // Decode only the evidence. Its first byte is ASCII at a proven line boundary;
        // no truncated UTF-8 character from unrelated prefix can enter the frame.
        var text = new UTF8Encoding(false, true).GetString(bytes, first, last - first);
        return new FailureLogTail(text, length, false, false);
    }
}
