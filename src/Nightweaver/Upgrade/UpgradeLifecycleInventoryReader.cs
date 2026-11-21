using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Principal;
using Nightweaver.Background;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Upgrade;

/// <summary>Fresh root-wide inventory for sources independently proven to implement this boundary.</summary>
internal static class UpgradeLifecycleInventoryReader
{
    internal static UpgradeLifecycleOperations Create(HostPaths paths, UpgradeScope expected, bool sourceBoundarySupported,
        IProcessRunner runner) => new(
        token => ReadAsync(paths, expected, sourceBoundarySupported, runner, token),
        async (name, token) =>
        {
            var item = expected.Projects.Single(p => p.Name == name);
            var config = ProjectConfigLoader.Load(item.Configuration, ProjectSlug.Parse(name), paths);
            return await Supervision.SupervisionLifecycle.OpenAsync(paths, config, token).ConfigureAwait(false);
        },
        (owner, operation, maintenance, token) => UpgradeLifecycle.RequestDrainAsync(paths, owner, operation, maintenance, token),
        BackgroundContainment.Read,
        name => BackgroundFileLock.ObserveOwner(paths, ProjectSlug.Parse(name)));

    internal static async Task<UpgradeLifecycleInventory> ReadAsync(HostPaths paths, UpgradeScope expected,
        bool sourceBoundarySupported, IProcessRunner runner, CancellationToken token)
    {
        if (!sourceBoundarySupported) throw new InvalidOperationException("Source upgrade boundary capability is unsupported.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var user = WindowsIdentity.GetCurrent();
        if (expected.Root != PreparedRelease.CanonicalRoot(paths.Root) || user.User?.Value != expected.UserSid)
            throw new InvalidDataException("Upgrade installation identity changed.");
        using var configPin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(paths.ProjectsDirectory);
        var configs = Directory.EnumerateFileSystemEntries(paths.ProjectsDirectory).Take(257).ToArray();
        if (configs.Length > 256 || configs.Any(c => !expected.Projects.Any(p => p.Configuration.Equals(c, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Upgrade configuration inventory changed.");
        var projects = new List<AffectedUpgradeProject>();
        var owners = new List<BackgroundOwnerRecord>();
        foreach (var item in expected.Projects)
        {
            var slug = ProjectSlug.Parse(item.Name);
            var bytes = PreparedRelease.Read(item.Configuration, 1024 * 1024);
            using var file = InvocationRuntimeFiles.OpenExistingPackageFileForRead(item.Configuration);
            var config = ProjectConfigLoader.Load(item.Configuration, slug, paths);
            var repository = await new TargetRepositoryValidator(runner)
                .ValidateAsync(config.TargetRepository, config.ConfigFilePath, token).ConfigureAwait(false);
            projects.Add(new(item.Name, config.ConfigFilePath, Convert.ToHexString(SHA256.HashData(bytes)),
                repository.Path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.GitCommonDirectory)),
                ProcessLifetimeTargetRepositoryLock.ResolveIdentity(repository.GitCommonDirectory)));
            var recordPath = BackgroundOwnerRecord.PathFor(paths, slug);
            BackgroundOwnerRecord? owner = null;
            try
            {
                using var record = InvocationRuntimeFiles.OpenExistingPackageFileForRead(recordPath);
                owner = BackgroundOwnerRecord.TryRead(recordPath)
                    ?? throw new InvalidDataException("Upgrade owner evidence is unavailable.");
                owners.Add(owner);
            }
            catch (IOException exception) when (Missing(exception)) { }
            var active = BackgroundFileLock.ObserveOwner(paths, slug);
            if (active is null || active == true && owner is null)
                throw new InvalidDataException("Upgrade owner inventory is unavailable.");
            // Foreground owners use this lease too. They have no background drain
            // endpoint, so this version refuses them instead of ignoring them.
            try
            {
                using var payload = InvocationRuntimeFiles.OpenExclusiveRuntimeFile(Path.Combine(paths.Root, "run", item.Name, "payload.lock"), false);
            }
            catch (IOException exception) when (Missing(exception)) { }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33
                || exception.InnerException is Win32Exception { NativeErrorCode: 32 or 33 })
            {
                if (active != true || owner is null) throw new InvalidDataException("An unsupported foreground owner uses the payload.");
            }
        }
        using var runtimePin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(Path.Combine(paths.Root, "run"));
        var entries = Directory.EnumerateFileSystemEntries(Path.Combine(paths.Root, "run")).Take(258).ToArray();
        if (entries.Length > 257 || entries.Any(p => Path.GetFileName(p) != "upgrade"
            && !expected.Projects.Any(project => project.Name == Path.GetFileName(p))))
            throw new InvalidDataException("Unknown payload consumer in runtime inventory.");
        return new(new(paths.Root, expected.UserSid, projects.ToArray()), owners.ToArray(), true);
    }

    private static bool Missing(IOException exception) => exception is FileNotFoundException or DirectoryNotFoundException
        || exception.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };
}
