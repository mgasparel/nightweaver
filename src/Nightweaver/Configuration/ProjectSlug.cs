using System.Text.RegularExpressions;

namespace Nightweaver.Configuration;

internal readonly partial record struct ProjectSlug(string Value)
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSlugRegex();

    internal static bool TryParse(string? value, out ProjectSlug slug, out string reason)
    {
        slug = default;
        if (string.IsNullOrEmpty(value) || !char.IsAsciiLetterOrDigit(value[0]))
        {
            reason = "must start with a letter or digit";
            return false;
        }

        if (value.Length > 64)
        {
            reason = "must be 64 characters or fewer";
            return false;
        }

        if (!SafeSlugRegex().IsMatch(value))
        {
            reason = "must contain only ASCII letters, digits, underscores, and hyphens";
            return false;
        }

        if (ReservedNames.Contains(value))
        {
            reason = "is a reserved Windows device name";
            return false;
        }

        slug = new ProjectSlug(value);
        reason = string.Empty;
        return true;
    }

    internal static ProjectSlug Parse(string value) =>
        TryParse(value, out var slug, out var reason) ? slug : throw new ArgumentException(reason, nameof(value));

    public override string ToString() => Value;
}
