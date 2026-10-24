using Nightweaver.Inspection;

namespace Nightweaver.Execution;

/// <summary>Shared literal Bead ID validation for managed paths and Agent invocation contexts.</summary>
internal static class BeadIdSafety
{
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static bool IsSafe(string beadId)
    {
        if (string.IsNullOrEmpty(beadId) || beadId.Length > 128 || beadId is "." or ".."
            || Path.IsPathRooted(beadId) || beadId.EndsWith(' ') || beadId.EndsWith('.')
            || !char.IsAsciiLetterOrDigit(beadId[0])
            || beadId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            return false;

        var stem = beadId.Split('.', 2)[0];
        if (WindowsDeviceNames.Contains(stem)) return false;
        if (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is '¹' or '²' or '³') return false;

        // Keep the existing RuntimePaths checks as a final platform-specific guard.
        return RuntimePaths.SafeId(beadId);
    }
}
