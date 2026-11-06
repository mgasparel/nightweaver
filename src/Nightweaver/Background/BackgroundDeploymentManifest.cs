using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightweaver.Background;

internal sealed record BackgroundDeploymentManifest(int SchemaVersion, string BuildVersion,
    string CliExecutable, string WebExecutable)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 8
    };

    internal static string FileName => "nightweaver-deployment.json";

    internal string ResolveWebExecutable(string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var executable = ResolveContained(fullRoot, WebExecutable);
        if (!File.Exists(executable))
            throw new InvalidDataException($"deployment Web executable is missing: '{executable}'");
        return executable;
    }

    internal string ResolveCliExecutable(string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var executable = ResolveContained(fullRoot, CliExecutable);
        if (!File.Exists(executable))
            throw new InvalidDataException($"deployment CLI executable is missing: '{executable}'");
        return executable;
    }

    internal static BackgroundDeploymentManifest Load(string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.Combine(fullRoot, FileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024)
            throw new InvalidDataException($"deployment manifest is missing or has an invalid size: '{path}'");
        var manifest = JsonSerializer.Deserialize<BackgroundDeploymentManifest>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("deployment manifest is empty");
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.BuildVersion)
            || !SafeRelativePath(manifest.CliExecutable) || !SafeRelativePath(manifest.WebExecutable))
            throw new InvalidDataException("deployment manifest is incompatible or contains unsafe executable paths");
        _ = manifest.ResolveCliExecutable(fullRoot);
        _ = manifest.ResolveWebExecutable(fullRoot);
        return manifest;
    }

    private static string ResolveContained(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("deployment executable path escapes the installation root");
        return candidate;
    }

    private static bool SafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value)
            || value.IndexOfAny(['\0', '\r', '\n', ':']) >= 0 || value.Contains('\\')) return false;
        return value.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }
}
