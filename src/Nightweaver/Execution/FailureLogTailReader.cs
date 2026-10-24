using System.Text;
using Nightweaver.Configuration;
using Nightweaver.Inspection;

namespace Nightweaver.Execution;

internal static class DuplicateFailureClassifierLimits
{
    internal const int MaximumLogTailBytes = 32 * 1024;
    internal const int MaximumReasonBytes = 4 * 1024;
    internal const int MaximumDiagnosticsBytes = 16 * 1024;
    internal const int MaximumDiagnosticCount = 256;
    internal const int MaximumPayloadBytes = 256 * 1024;
    internal const int MaximumStandardOutputBytes = 16 * 1024;
}

internal sealed record FailureLogTail(
    string Text,
    long OriginalByteLength,
    bool Truncated,
    bool HadInvalidUtf8);

internal static class FailureLogTailReader
{
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static void ValidateReference(
        HostPaths hostPaths,
        ProjectSlug project,
        string beadId,
        AgentRole role,
        AgentStageResult result)
    {
        if (!IsSafeBeadId(beadId))
            throw new DuplicateFailureClassifierInputException($"Bead ID '{beadId}' is not a safe Windows path segment.");
        var expectedSuffix = RoleSuffix(role);
        if (result.Role != role)
            throw new DuplicateFailureClassifierInputException($"Failed invocation '{result.InvocationId}' belongs to role '{result.Role}', not '{role}'.");
        if (!IsLifecycleInvocationId(result.InvocationId, expectedSuffix))
            throw new DuplicateFailureClassifierInputException($"Failed invocation ID '{result.InvocationId}' does not identify a settled '{role}' invocation.");
        var expectedPath = RuntimePaths.Normalize(Path.Combine(hostPaths.LogsRoot(project), beadId, result.InvocationId + ".log"));
        if (!Path.IsPathFullyQualified(result.LogFilePath)
            || !string.Equals(result.LogFilePath, expectedPath, StringComparison.Ordinal))
            throw new DuplicateFailureClassifierInputException(
                $"Failed invocation log path '{result.LogFilePath}' does not match the canonical log for '{result.InvocationId}'.");
    }

    internal static async Task<FailureLogTail> ReadAsync(
        HostPaths hostPaths,
        ProjectSlug project,
        AgentStageResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pinned = InvocationRuntimeFiles.OpenExistingSettledLogForRead(
            hostPaths, project, result.BeadId, result.InvocationId, result.LogFilePath);
        var stream = pinned.Stream;
        var originalLength = stream.Length;
        var retainedLength = (int)Math.Min(originalLength, DuplicateFailureClassifierLimits.MaximumLogTailBytes);
        var offset = originalLength - retainedLength;
        stream.Position = offset;

        var bytes = new byte[retainedLength];
        var read = 0;
        while (read < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }

        if (read != bytes.Length) Array.Resize(ref bytes, read);
        var hadInvalidUtf8 = false;
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            hadInvalidUtf8 = true;
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
        }

        return new FailureLogTail(text, originalLength,
            originalLength > DuplicateFailureClassifierLimits.MaximumLogTailBytes || read != retainedLength,
            hadInvalidUtf8);
    }

    internal static string RoleSuffix(AgentRole role) => role switch
    {
        AgentRole.Planner => "planner",
        AgentRole.PlanChallenger => "plan-challenger",
        AgentRole.Implementer => "implementer",
        AgentRole.Reviewer => "reviewer",
        AgentRole.Qa => "qa",
        AgentRole.Merger => "merger",
        _ => throw new DuplicateFailureClassifierInputException($"Role '{role}' is not a lifecycle role whose failures can be classified.")
    };

    private static bool IsLifecycleInvocationId(string value, string suffix)
    {
        if (value.Length != 24 + suffix.Length || value[8] != 'T' || value[22] != 'Z' || value[23] != '-') return false;
        if (!value.AsSpan(0, 8).ToString().All(char.IsAsciiDigit)
            || !value.AsSpan(9, 13).ToString().All(char.IsAsciiDigit)
            || !value.EndsWith(suffix, StringComparison.Ordinal)) return false;
        return DateTime.TryParseExact(value[..23], "yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out _);
    }

    private static bool IsSafeBeadId(string beadId)
    {
        if (string.IsNullOrEmpty(beadId) || beadId.Length > 128 || beadId is "." or ".." || beadId.EndsWith('.')
            || !char.IsAsciiLetterOrDigit(beadId[0])
            || beadId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))) return false;
        var stem = beadId.Split('.', 2)[0];
        return !WindowsDeviceNames.Contains(stem)
            && !(stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '0' and <= '9' || stem[3] is '¹' or '²' or '³'));
    }
}

internal sealed class DuplicateFailureClassifierInputException(string message) : Exception(message);
