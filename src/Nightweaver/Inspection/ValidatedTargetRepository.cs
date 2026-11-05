using Nightweaver.Configuration;

namespace Nightweaver.Inspection;

internal sealed record ValidatedTargetRepository(string Path, string DefaultBranch)
{
    internal string GitCommonDirectory { get; init; } = Path;
    internal string? OriginUrl { get; init; }
    internal GitHubRepositoryIdentity? OriginRepository { get; init; }
}

internal sealed record GitHubRepositoryIdentity(string Host, string Owner, string Name)
{
    internal string FullName => $"{Owner}/{Name}";
    internal string CliRepository => $"{Host}/{Owner}/{Name}";

    internal bool Matches(GitHubRepositoryIdentity? other) =>
        other is not null
        && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    internal static GitHubRepositoryIdentity? ParseRemoteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string host;
        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("https" or "http" or "ssh" or "git") || string.IsNullOrWhiteSpace(uri.Host))
                return null;
            host = uri.IdnHost;
            path = uri.AbsolutePath;
        }
        else
        {
            var separator = value.IndexOf(':');
            if (separator <= 0 || value.Contains("://", StringComparison.Ordinal)) return null;
            var hostPart = value[..separator];
            var at = hostPart.LastIndexOf('@');
            host = at >= 0 ? hostPart[(at + 1)..] : hostPart;
            path = value[(separator + 1)..];
        }

        var parts = path.Trim('/').Split('/');
        if (parts.Length != 2) return null;
        var owner = Uri.UnescapeDataString(parts[0]);
        var name = Uri.UnescapeDataString(parts[1]);
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!IsSafeSegment(host) || !IsSafeSegment(owner) || !IsSafeSegment(name)) return null;
        return new(host, owner, name);
    }

    internal static GitHubPullRequestIdentity? ParsePullRequestUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 4 || !string.Equals(parts[2], "pull", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;
        var owner = Uri.UnescapeDataString(parts[0]);
        var name = Uri.UnescapeDataString(parts[1]);
        if (!IsSafeSegment(uri.IdnHost) || !IsSafeSegment(owner) || !IsSafeSegment(name)) return null;
        return new(new(uri.IdnHost, owner, name), number);
    }

    private static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..")
        && value.IndexOfAny(['/', '\\', '\r', '\n']) < 0;
}

internal sealed record GitHubPullRequestIdentity(GitHubRepositoryIdentity Repository, int Number);