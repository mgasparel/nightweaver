using System.Security.Cryptography;
using System.Text;
using Nightweaver.Configuration;

namespace Nightweaver.Background;

internal static class BackgroundIdentity
{
    internal static string CurrentUserIdentity => Environment.UserDomainName + "\\" + Environment.UserName;

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string PipeName(HostPaths paths, ProjectSlug project)
    {
        var root = NormalizeRoot(paths.Root);
        return "nightweaver_background_" + Hash(root + "\n" + project.Value + "\n" + CurrentUserIdentity)[..40];
    }

    internal static string InstanceMutexName(HostPaths paths, ProjectSlug project)
    {
        var identity = Hash(NormalizeRoot(paths.Root) + "\n" + project.Value + "\n" + CurrentUserIdentity);
        return "Local\\Nightweaver_Background_" + identity;
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
