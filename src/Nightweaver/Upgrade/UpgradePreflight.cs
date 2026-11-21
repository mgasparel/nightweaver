using System.Security.Cryptography;
using System.Text.Json;
using Nightweaver.Application;
using Nightweaver.Cli;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;

namespace Nightweaver.Upgrade;

internal sealed record AffectedUpgradeProject(string Name, string Configuration, string ConfigurationDigest,
    string Repository, string GitCommonDirectory, string PhysicalRepositoryIdentity);
internal sealed record UpgradePreflightReport(string Schema, string InstallationRoot, string CandidateRoot,
    string? OldPackageDigest, string? NewPackageDigest, AffectedUpgradeProject[] Projects,
    bool Compatible, bool RollbackEligible, string[] Refusals);

/// <summary>Collects bounded read-only evidence; no lifecycle, store initialization or staging writes.</summary>
internal static class UpgradePreflight
{
    internal const string Usage = "Usage: nightweaver upgrade preflight --installation <absolute-root> --candidate <absolute-prepared-root> [--config <absolute-external-config>]...\n";

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, AppEnvironment environment,
        IProcessRunner runner, CancellationToken cancellationToken)
    {
        if (args.Count == 2 && args[1] is "--help" or "-h") { environment.Out.Write(Usage); return 0; }
        if (args.Count < 2 || args[1] != "preflight") throw new UsageException(Usage.Trim());
        string? installation = null, candidate = null;
        var configs = new List<string>();
        for (var i = 2; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count) throw new UsageException(Usage.Trim());
            switch (args[i])
            {
                case "--installation" when installation is null: installation = args[i + 1]; break;
                case "--candidate" when candidate is null: candidate = args[i + 1]; break;
                case "--config" when configs.Count < 256: configs.Add(args[i + 1]); break;
                default: throw new UsageException(Usage.Trim());
            }
        }
        if (installation is null || candidate is null) throw new UsageException(Usage.Trim());
        var report = await InspectAsync(installation, candidate, configs, runner, cancellationToken);
        environment.Out.WriteLine(JsonSerializer.Serialize(report, PreparedRelease.JsonOptions));
        return report.Compatible ? 0 : 1;
    }

    internal static async Task<UpgradePreflightReport> InspectAsync(string installation, string candidate,
        IReadOnlyList<string> externalConfigs, IProcessRunner runner, CancellationToken cancellationToken)
    {
        var refusals = new List<string>();
        var projects = new List<AffectedUpgradeProject>();
        ReleaseInspection? old = null, next = null;
        try
        {
            installation = PreparedRelease.CanonicalRoot(installation);
            candidate = PreparedRelease.CanonicalRoot(candidate);
            using var installationPin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(installation);
            using var candidatePin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(candidate);
            if (PreparedRelease.Contains(installation, candidate) || PreparedRelease.Contains(candidate, installation))
                throw new InvalidDataException("payload-collision");
            old = InspectPackage(installation, true, refusals);
            next = InspectPackage(candidate, false, refusals);
            if (old is not null && next is not null)
            {
                var oldNames = old.Inventory.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in next.Inventory.Files)
                    if (!oldNames.Contains(file.Path) && (File.Exists(Path.Combine(installation, file.Path))
                            || Directory.Exists(Path.Combine(installation, file.Path)))) refusals.Add("payload-collision");
            }
            var runtimes = await runner.RunAsync(new ProcessRequest("dotnet", ["--list-runtimes"], installation,
                new Dictionary<string, string?>(), TimeSpan.FromSeconds(10)) { MaximumOutputBytes = 16384 }, cancellationToken);
            if (runtimes.ExitCode != 0 || old is null || next is null
                || !SupportsRuntime(installation, runtimes.StandardOutput) || !SupportsRuntime(candidate, runtimes.StandardOutput))
                refusals.Add("runtime-unsupported");
            var paths = new HostPaths(installation);
            if (!Directory.Exists(paths.ProjectsDirectory)) throw new InvalidDataException("configuration-unsupported");
            using var configPin = InvocationRuntimeFiles.PinExistingRuntimeDirectory(paths.ProjectsDirectory);
            var configurations = Directory.EnumerateFileSystemEntries(paths.ProjectsDirectory).Take(257).ToArray();
            if (configurations.Length > 256) throw new InvalidDataException("configuration-unsupported");
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var configPath in configurations.Concat(externalConfigs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Path.IsPathFullyQualified(configPath) || !selected.Add(Path.GetFullPath(configPath))
                    || !configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("configuration-unsupported");
                var bytes = PreparedRelease.Read(configPath, 1024 * 1024);
                // Hold the exact config against write/delete while the existing strict reader parses it.
                using var configFile = InvocationRuntimeFiles.OpenExistingPackageFileForRead(configPath);
                ProjectSlug? expected = PreparedRelease.Contains(paths.ProjectsDirectory, configPath)
                    ? ProjectSlug.Parse(Path.GetFileNameWithoutExtension(configPath)) : null;
                var config = ProjectConfigLoader.Load(configPath, expected, paths);
                if (!slugs.Add(config.Name.Value)) throw new InvalidDataException("identity-unknown");
                var repository = PreparedRelease.CanonicalRoot(config.TargetRepository.Path);
                if (PreparedRelease.Contains(repository, candidate) || PreparedRelease.Contains(candidate, repository)
                    || PreparedRelease.Contains(repository, installation) || PreparedRelease.Contains(installation, repository))
                    throw new InvalidDataException("payload-collision");
                var result = await runner.RunAsync(GitCommand.Create(
                    ["rev-parse", "--path-format=absolute", "--git-common-dir"], repository) with
                    { MaximumOutputBytes = 4096, Timeout = TimeSpan.FromSeconds(10) }, cancellationToken);
                if (result.ExitCode != 0) throw new InvalidDataException("identity-unknown");
                var common = PreparedRelease.CanonicalRoot(result.StandardOutput.Trim());
                var physical = ProcessLifetimeTargetRepositoryLock.ResolveIdentity(common);
                // Re-read under the retained handle: a concurrent edit invalidates the whole observation.
                var confirmed = PreparedRelease.Read(configPath, 1024 * 1024);
                if (!bytes.AsSpan().SequenceEqual(confirmed)) throw new InvalidDataException("configuration-unsupported");
                projects.Add(new(config.Name.Value, Path.GetFullPath(configPath), Convert.ToHexString(SHA256.HashData(bytes)),
                    repository, common, physical));
            }
            if (projects.Count == 0) refusals.Add("identity-unknown");
            // Current binaries lack the root interlock, recovery manager and cross-version state readers.
            // An inventory's untrusted capability strings cannot establish those implementation facts.
            // Until delivered adapters prove them, even a stopped legacy installation is inspection-only.
            refusals.Add("capability-unsupported");
            refusals.Add("state-unsupported");
            refusals.Add("rollback-incompatible");
            refusals.Add("identity-unknown");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            refusals.Add(SafeCode(exception));
        }
        return new("nightweaver-upgrade-preflight-v1", installation, candidate, old?.Digest, next?.Digest,
            projects.ToArray(), false, false, refusals.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool SupportsRuntime(string root, string available)
    {
        foreach (var relative in new[] { "nightweaver.runtimeconfig.json", "web/Nightweaver.Web.runtimeconfig.json" })
        {
            using var document = JsonDocument.Parse(PreparedRelease.Read(Path.Combine(root, relative), 65536));
            PreparedRelease.RejectDuplicateProperties(document.RootElement);
            var options = document.RootElement.GetProperty("runtimeOptions");
            var frameworks = options.TryGetProperty("frameworks", out var many) ? many.EnumerateArray().ToArray()
                : new[] { options.GetProperty("framework") };
            if (frameworks.Length is < 1 or > 4) return false;
            foreach (var framework in frameworks)
            {
                var name = framework.GetProperty("name").GetString();
                if (name is not ("Microsoft.NETCore.App" or "Microsoft.AspNetCore.App")
                    || !Version.TryParse(framework.GetProperty("version").GetString(), out var required)) return false;
                if (!available.Split('\n').Any(line =>
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        return parts.Length >= 2 && parts[0] == name && Version.TryParse(parts[1], out var found)
                            && found.Major == required.Major && found.Minor == required.Minor && found >= required;
                    })) return false;
            }
        }
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64;
    }

    private static ReleaseInspection? InspectPackage(string root, bool installed, List<string> refusals)
    {
        try { return PreparedRelease.Inspect(root, installed); }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            refusals.Add(SafeCode(exception));
            return null;
        }
    }

    private static bool IsInspectionFailure(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException
        or FormatException or OverflowException
        or ProjectConfigurationException or ToolExecutionException or System.ComponentModel.Win32Exception;

    private static string SafeCode(Exception exception) => exception.Message is "inventory-invalid" or "pair-mismatch"
        or "payload-collision" or "payload-mismatch" or "identity-unknown" or "configuration-unsupported"
        ? exception.Message : exception is ProjectConfigurationException ? "configuration-unsupported" : "inspection-unavailable";
}
