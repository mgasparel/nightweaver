using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightweaver.Execution;

namespace Nightweaver.Upgrade;

internal sealed record ReleaseFile(string Path, long Length, string Sha256);
internal sealed record ReleaseInventory(int SchemaVersion, string BuildVersion, string[] Capabilities, ReleaseFile[] Files);
internal sealed record ReleaseInspection(string Root, string BuildVersion, string Digest, ReleaseInventory Inventory);

/// <summary>Read-only validation. The returned digest is evidence, never a permit to replace files.</summary>
internal static class PreparedRelease
{
    internal const string InventoryName = "nightweaver-release.json";
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly string[] StateDirectories = ["projects", "worktrees", "tmp", "logs", "telemetry", "run"];
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    internal static string CanonicalRoot(string root)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)
            || root[2..].Contains(':')) throw new InvalidDataException("identity-unknown");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("identity-unknown");
        using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(full);
        if (!full.Equals(InvocationRuntimeFiles.ResolveExistingDirectoryPath(full), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("identity-unknown");
        return full;
    }

    internal static bool Contains(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.GetFullPath(child);
        return parent.Equals(child, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SafePayloadPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':')) return false;
        var segments = path.Split('/');
        if (StateDirectories.Contains(segments[0], StringComparer.OrdinalIgnoreCase)
            || path.Equals(InventoryName, StringComparison.OrdinalIgnoreCase)) return false;
        return segments.All(segment => segment.Length > 0 && segment is not ("." or "..")
            && !segment.EndsWith('.') && !segment.EndsWith(' ')
            && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ')
            && !IsDevice(segment.Split('.')[0]));
    }

    private static bool IsDevice(string segment) => segment.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (segment.Length == 4 && (segment.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(segment[3]));

    internal static ReleaseInspection Inspect(string root, bool installed)
    {
        root = CanonicalRoot(root);
        using var rootPin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(root);
        var bytes = Read(Path.Combine(root, InventoryName), 1024 * 1024);
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(document.RootElement);
        var inventory = JsonSerializer.Deserialize<ReleaseInventory>(bytes, JsonOptions)
            ?? throw new InvalidDataException("inventory-invalid");
        if (inventory.SchemaVersion != 1 || inventory.BuildVersion is null || inventory.BuildVersion.Length != 40
            || !inventory.BuildVersion.All(char.IsAsciiHexDigit) || inventory.Files is null
            || inventory.Files.Length is < 4 or > 4096 || inventory.Capabilities is null
            || inventory.Capabilities.Length > 32 || inventory.Capabilities.Any(c => string.IsNullOrEmpty(c) || c.Length > 80)
            || inventory.Capabilities.Distinct(StringComparer.Ordinal).Count() != inventory.Capabilities.Length)
            throw new InvalidDataException("inventory-invalid");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in inventory.Files)
        {
            if (file is null || !SafePayloadPath(file.Path) || !names.Add(file.Path) || file.Length < 0
                || file.Length > MaximumBytes || file.Sha256 is null || file.Sha256.Length != 64
                || !file.Sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("inventory-invalid");
            total += file.Length;
            if (total > MaximumBytes) throw new InvalidDataException("inventory-invalid");
        }
        foreach (var name in names)
            if (name.Split('/').Select((_, i) => string.Join('/', name.Split('/').Take(i + 1)))
                .SkipLast(1).Any(names.Contains)) throw new InvalidDataException("payload-collision");
        foreach (var file in inventory.Files)
        {
            using var pinned = InvocationRuntimeFiles.OpenExistingPackageFileForRead(Path.Combine(root, file.Path));
            if (pinned.Stream.Length != file.Length || !Convert.ToHexString(SHA256.HashData(pinned.Stream))
                    .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("payload-mismatch");
        }
        if (!installed)
        {
            var actual = Enumerate(root).Where(p => p != InventoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(names)) throw new InvalidDataException("inventory-invalid");
        }
        RequirePair(root, inventory, names);
        return new ReleaseInspection(root, inventory.BuildVersion, Convert.ToHexString(SHA256.HashData(bytes)), inventory);
    }

    private static void RequirePair(string root, ReleaseInventory inventory, HashSet<string> names)
    {
        foreach (var required in new[] { "nightweaver-deployment.json", "nightweaver.exe", "web/Nightweaver.Web.exe",
                     "nightweaver.dll", "web/nightweaver.dll", "web/Nightweaver.Web.dll",
                     "nightweaver.runtimeconfig.json", "web/Nightweaver.Web.runtimeconfig.json", "web/wwwroot/studio/index.html" })
            if (!names.Contains(required)) throw new InvalidDataException("pair-mismatch");
        using var manifest = JsonDocument.Parse(Read(Path.Combine(root, "nightweaver-deployment.json"), 16384));
        RejectDuplicateProperties(manifest.RootElement);
        var m = manifest.RootElement;
        if (m.EnumerateObject().Count() != 4 || m.GetProperty("schema_version").GetInt32() != 1
            || m.GetProperty("build_version").GetString() != inventory.BuildVersion
            || m.GetProperty("cli_executable").GetString() != "nightweaver.exe"
            || m.GetProperty("web_executable").GetString() != "web/Nightweaver.Web.exe") throw new InvalidDataException("pair-mismatch");
        foreach (var binary in new[] { "nightweaver.dll", "web/nightweaver.dll", "web/Nightweaver.Web.dll" })
        {
            var path = Path.Combine(root, binary);
            using var pin = InvocationRuntimeFiles.OpenExistingPackageFileForRead(path);
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (version != inventory.BuildVersion && version?.Split('+').Last() != inventory.BuildVersion)
                throw new InvalidDataException("pair-mismatch");
        }
        var cli = inventory.Files.Single(f => f.Path.Equals("nightweaver.dll", StringComparison.OrdinalIgnoreCase));
        var webCli = inventory.Files.Single(f => f.Path.Equals("web/nightweaver.dll", StringComparison.OrdinalIgnoreCase));
        if (cli.Length != webCli.Length || !cli.Sha256.Equals(webCli.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("pair-mismatch");
        foreach (var prompt in new[] { "implementer.md", "merger.md", "plan-challenger.md", "planner.md", "qa.md", "reviewer.md" })
            if (!names.Contains("prompts/" + prompt)) throw new InvalidDataException("pair-mismatch");
        var html = System.Text.Encoding.UTF8.GetString(Read(Path.Combine(root, "web/wwwroot/studio/index.html"), 1024 * 1024));
        var references = System.Text.RegularExpressions.Regex.Matches(html,
            "(?i)\\b(?:src|href)\\s*=\\s*[\"'](?<url>[^\"']+\\.(?:js|css)(?:[?#][^\"']*)?)[\"']");
        var local = 0;
        foreach (System.Text.RegularExpressions.Match reference in references)
        {
            var url = System.Net.WebUtility.HtmlDecode(reference.Groups["url"].Value);
            if (Uri.TryCreate(url, UriKind.Absolute, out _) || url.StartsWith("//", StringComparison.Ordinal)) continue;
            var asset = Uri.UnescapeDataString(url.Split('?', '#')[0]);
            var path = Path.GetFullPath(Path.Combine(root, "web/wwwroot", asset.StartsWith('/') ? asset.TrimStart('/') : "studio/" + asset));
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!Contains(Path.Combine(root, "web/wwwroot"), path) || !names.Contains(relative))
                throw new InvalidDataException("pair-mismatch");
            local++;
        }
        if (local == 0) throw new InvalidDataException("pair-mismatch");
    }

    internal static IEnumerable<string> Enumerate(string root)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            using var pin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 8192) throw new InvalidDataException("inventory-invalid");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("inventory-invalid");
                if (directory == root && StateDirectories.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("payload-collision");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return Path.GetRelativePath(root, path).Replace('\\', '/');
            }
        }
    }

    internal static byte[] Read(string path, int limit)
    {
        using var pinned = InvocationRuntimeFiles.OpenExistingPackageFileForRead(path);
        if (pinned.Stream.Length > limit) throw new InvalidDataException("inventory-invalid");
        using var buffer = new MemoryStream();
        pinned.Stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("inventory-invalid");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
