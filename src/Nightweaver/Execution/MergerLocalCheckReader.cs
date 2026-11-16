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
    internal static string RerunMarker(string invocationId) => "NIGHTWEAVER_LOCAL_RERUN_V1 " + invocationId;

    internal static async Task<FailureLogTail> ReadAsync(HostPaths paths, ProjectSlug project,
        AgentStageResult invocation, MergerRejectionFinding finding, CancellationToken cancellationToken)
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
        var rerunMarker = Encoding.ASCII.GetBytes(RerunMarker(invocation.InvocationId));
        // The marker and timestamp-role invocation ID contain no internal 'N'.
        var buffer = new byte[4096];
        var positions = new List<long>(2);
        var matched = 0;
        var rerunMatched = 0;
        var rerunPositions = new List<long>(2);
        var passingMatched = new int[PassingMarkers.Length];
        var prefixResults = new PrefixTerminalResults();
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
                if (positions.Count == 0) prefixResults.Observe(value, position + index);
                MatchMarker(value, position + index, rerunMarker, ref rerunMatched, rerunPositions);
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
        long verifiedRerunEnd = -1;
        if (rerunPositions.Count != 0)
        {
            if (rerunPositions.Count != 2 || rerunPositions[1] + rerunMarker.Length >= start)
                throw new InvalidDataException("The current rerun evidence is incomplete or out of order.");
            var rerun = await ReadFrameAsync(stream, length, rerunPositions, rerunMarker.Length, cancellationToken)
                .ConfigureAwait(false);
            var diagnostic = MergerOutcomeVerifier.VerifyLocalCheckFailure(finding, finding.CheckedHead,
                invocation.InvocationId, new FailureLogTail(rerun, length, false, false), rerun: true);
            if (diagnostic is not null) throw new InvalidDataException("Invalid current rerun evidence: " + diagnostic);
            verifiedRerunEnd = rerunPositions[1] + rerunMarker.Length;
        }
        // Unattributed failures never erase a pass. Only a separately completed current-ID
        // rerun, checked against the same finding and candidate, can supersede it.
        if (prefixResults.HasUnclosedFrame || prefixResults.LastPassPosition(finding.Subject) > verifiedRerunEnd)
            throw new InvalidDataException("A preceding passing result or incomplete result frame contradicts the local check failure summary.");
        // A passing tool result after the reported final frame contradicts the rejection.
        // Scan the suffix too, including matches split across read buffers; do not decode or retain it.
        if (lastPassingPosition >= end)
            throw new InvalidDataException("A later passing result contradicts the local check failure summary.");
        var text = await ReadFrameAsync(stream, length, positions, marker.Length, cancellationToken).ConfigureAwait(false);
        return new FailureLogTail(text, length, false, false);
    }

    private static void MatchMarker(byte value, long position, byte[] marker, ref int matched, List<long> positions)
    {
        matched = value == marker[matched] ? matched + 1 : value == marker[0] ? 1 : 0;
        if (matched != marker.Length) return;
        positions.Add(position + 1 - marker.Length);
        matched = 0;
        if (positions.Count > 2)
            throw new InvalidDataException("Local rerun framing is ambiguous: duplicate or quoted marker mentions.");
    }

    private static async Task<string> ReadFrameAsync(Stream stream, long length, List<long> positions,
        int markerLength, CancellationToken cancellationToken)
    {
        var start = positions[0];
        var end = positions[1] + markerLength;
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
        return new UTF8Encoding(false, true).GetString(bytes, first, last - first);
    }

    private sealed class PrefixTerminalResults
    {
        // Terminal summaries and invocation marker identities are short ASCII lines. Retain
        // only a bounded prefix of each line, never arbitrary tool output or decoded log text.
        private readonly char[] line = new char[256];
        private int length;
        private bool overflow;
        private long buildPassPosition = -1;
        private long testPassPosition = -1;
        private string? foreignFrameEnd;

        internal bool HasUnclosedFrame => foreignFrameEnd is not null;

        internal long LastPassPosition(string command) => command switch
        {
            "dotnet build" => buildPassPosition,
            "dotnet test" => testPassPosition,
            _ => -1
        };

        internal void Observe(byte value, long position)
        {
            if (value != (byte)'\n')
            {
                if (length == 0 && value is (byte)' ' or (byte)'\t' or (byte)'\r') return;
                if (length < line.Length) line[length++] = (char)value;
                else if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r')) overflow = true;
                return;
            }

            var text = line.AsSpan(0, length).Trim();
            // Historical/example V2 frames are not current check observations, even when
            // they contain full terminal lines. The main scanner still counts every current
            // marker; an unterminated foreign frame cannot hide a contradictory rerun.
            const string framePrefix = "NIGHTWEAVER_LOCAL_CHECK_V2 ";
            if (foreignFrameEnd is not null)
            {
                if (!overflow && text.Equals(foreignFrameEnd, StringComparison.Ordinal)) foreignFrameEnd = null;
            }
            else if (text.StartsWith(framePrefix, StringComparison.Ordinal))
            {
                var separator = text[framePrefix.Length..].IndexOf(' ');
                if (separator > 0) foreignFrameEnd = "END_" + text[..(framePrefix.Length + separator)].ToString();
            }
            else if (!overflow)
            {
                if (text.Equals("Build succeeded.", StringComparison.OrdinalIgnoreCase)) buildPassPosition = position;
                else if (text.Equals("Test run summary: Passed!", StringComparison.OrdinalIgnoreCase)) testPassPosition = position;
            }
            length = 0;
            overflow = false;
        }
    }
}
