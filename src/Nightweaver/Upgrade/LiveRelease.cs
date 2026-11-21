using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Nightweaver.Execution;

namespace Nightweaver.Upgrade;

internal sealed record LiveRelease(int Version, string PackageDigest, string Build, int ProcessId,
    DateTimeOffset CreatedUtc, string Root, string Configuration, string Project);

// Capture once before serving requests. A later manifest change cannot change this identity.
internal static class LiveReleaseIdentity
{
    internal static LiveRelease? Capture(string root, string configuration, string project, Assembly assembly, string binary)
    {
        // Legacy/dev installations remain usable but cannot establish upgrade acceptance.
        if (!File.Exists(Path.Combine(root, PreparedRelease.InventoryName))) return null;
        var package = PreparedRelease.Inspect(root, true);
        RequireAssembly(root, package.BuildVersion, assembly, binary);
        if (assembly != typeof(LiveReleaseIdentity).Assembly)
            RequireAssembly(root, package.BuildVersion, typeof(LiveReleaseIdentity).Assembly, "web/nightweaver.dll");
        using var process = Process.GetCurrentProcess();
        return new(1, package.Digest, package.BuildVersion, process.Id, process.StartTime.ToUniversalTime(), root, configuration, project);
    }

    private static void RequireAssembly(string root, string build, Assembly assembly, string binary)
    {
        using var pin = InvocationRuntimeFiles.OpenExistingPackageFileForRead(assembly.Location);
        using var image = new PEReader(pin.Stream, PEStreamOptions.LeaveOpen);
        var metadata = image.GetMetadataReader();
        var loadedBuild = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').Last();
        if (!Path.GetFullPath(assembly.Location).Equals(Path.GetFullPath(Path.Combine(root, binary)), StringComparison.OrdinalIgnoreCase)
            || loadedBuild != build
            || assembly.ManifestModule.ModuleVersionId != metadata.GetGuid(metadata.GetModuleDefinition().Mvid))
            throw new InvalidDataException("Loaded release does not match the installed package.");
    }

    internal static bool Matches(LiveRelease? release, string digest, string build, int pid, DateTimeOffset created,
        string root, string configuration, string project) => release is { Version: 1 }
        && release.PackageDigest == digest && release.Build == build && release.ProcessId == pid
        && release.CreatedUtc == created && release.Root == root && release.Configuration == configuration && release.Project == project;

    internal static bool MatchesProcess(LiveRelease release, int processId, DateTimeOffset createdUtc) =>
        release.Version == 1 && processId > 0 && release.ProcessId == processId
        && release.CreatedUtc == createdUtc && createdUtc != default && createdUtc.Offset == TimeSpan.Zero;
}
