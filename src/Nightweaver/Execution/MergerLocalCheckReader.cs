using System.Text;
using Nightweaver.Configuration;

namespace Nightweaver.Execution;

/// <summary>Finds one complete evidence frame without retaining unrelated invocation output.</summary>
internal static class MergerLocalCheckReader
{
    internal const int MaximumLogBytes = 8 * 1024 * 1024;
    internal const int MaximumEvidenceBytes = 32 * 1024;
    private static readonly byte[][] PassingMarkers =
    [
        Encoding.ASCII.GetBytes("build succeeded."),
        Encoding.ASCII.GetBytes("test run summary: passed!")
    ];

    internal static string Marker(string invocationId) => "NIGHTWEAVER_LOCAL_CHECK_V2 " + invocationId;
    internal static string Prefix(string invocationId) => Marker(invocationId) + " ";
    internal static string End(string invocationId) => "END_" + Marker(invocationId);

    internal static async Task<FailureLogTail> ReadAsync(HostPaths paths, ProjectSlug project,
        AgentStageResult invocation, CancellationToken cancellationToken)
    {
        using var pinned = InvocationRuntimeFiles.OpenExistingSettledLogForRead(
            paths, project, invocation.BeadId, invocation.InvocationId, invocation.LogFilePath);
        var stream = pinned.Stream;
        var length = stream.Length;
        if (length > MaximumLogBytes)
            throw new InvalidDataException("Local check log exceeds the bounded 8 MiB scan limit.");

        // Only the dispatch identity designates result output. Prompt examples and repository/tool
        // echoes with other identities have no authority. Count every mention of the current marker,
        // including quoted/malformed copies, across the entire bounded log.
        var marker = Encoding.ASCII.GetBytes(Marker(invocation.InvocationId));
        // The marker and timestamp-role invocation ID contain no internal 'N'.
        var buffer = new byte[4096];
        var positions = new List<long>(2);
        var matched = 0;
        var passingMatched = new int[PassingMarkers.Length];
        long lastPassingPosition = -1;
        long position = 0;
        while (position < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - position)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Local check log ended before its settled length.");
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                var folded = value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
                for (var pass = 0; pass < PassingMarkers.Length; pass++)
                {
                    var passing = PassingMarkers[pass];
                    var match = passingMatched[pass];
                    match = folded == passing[match] ? match + 1 : folded == passing[0] ? 1 : 0;
                    if (match == passing.Length)
                    {
                        lastPassingPosition = position + index + 1 - passing.Length;
                        match = 0;
                    }
                    passingMatched[pass] = match;
                }
                matched = value == marker[matched] ? matched + 1 : value == marker[0] ? 1 : 0;
                if (matched != marker.Length) continue;
                positions.Add(position + index + 1 - marker.Length);
                matched = 0;
                if (positions.Count > 2)
                    throw new InvalidDataException("Local check framing is ambiguous: duplicate or quoted marker mentions.");
            }
            position += count;
        }
        if (stream.Length != length || positions.Count != 2)
            throw new InvalidDataException("The settled log does not contain one complete local check transcript for this invocation.");

        var start = positions[0];
        var end = positions[1] + marker.Length;
        // A passing tool result after the reported final frame contradicts the rejection.
        // Scan the suffix too, including matches split across read buffers; do not decode or retain it.
        if (lastPassingPosition >= end)
            throw new InvalidDataException("A later passing result contradicts the local check failure summary.");
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
