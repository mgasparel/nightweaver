using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Nightweaver.Tests;

internal sealed class NightweaverWebApplicationFactory : WebApplicationFactory<Web.Program>
{
    private readonly TempDirectory directory = new();
    private readonly Action<IConfigurationBuilder>? configure;
    private readonly Action<IServiceCollection>? configureServices;
    private readonly List<IDisposable> payloadLeases = [];

    internal NightweaverWebApplicationFactory(
        Action<IConfigurationBuilder>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        this.configure = configure;
        this.configureServices = configureServices;
        HostRoot = directory.CreateDirectory("host");
        TargetRoot = directory.CreateDirectory("target");
        WebRoot = directory.CreateDirectory("wwwroot");
        StageBuiltWebAssets();
        ConfigPath = Path.GetFullPath(directory.WriteFile("settings/project.json", """
            {"name":"web-test","target_repository":{"path":"../target"}}
            """));
        directory.WriteFile("private.txt", "private sentinel");
    }

    internal string HostRoot { get; }
    internal string TargetRoot { get; }
    internal string WebRoot { get; }
    internal string ConfigPath { get; }

    internal string CaptureHostState() => string.Join('\n', Directory.EnumerateFileSystemEntries(HostRoot, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            var entry = $"{Path.GetRelativePath(HostRoot, path)}:{File.GetAttributes(path)}:{File.GetLastWriteTimeUtc(path):O}";
            if (Directory.Exists(path)) return entry;
            // The Web lease shares reads and writes, but excludes deletion and
            // exclusive opens. This snapshot changes no file or lease ownership.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return $"{entry}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))}";
        }));

    private void StageBuiltWebAssets()
    {
        var appDirectory = typeof(NightweaverWebApplicationFactory).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "NightweaverWebAppDir")?.Value
            ?? throw new InvalidOperationException("NightweaverWebAppDir assembly metadata is required to stage the built Web assets.");
        var sourceRoot = Path.Combine(appDirectory, "wwwroot");
        var requiredAssets = new[] { Path.Combine("studio", "index.html"), Path.Combine("vendor", "markdown-it.min.js"), Path.Combine("vendor", "purify.min.js") };
        var missingAssets = requiredAssets
            .Select(relativePath => Path.Combine(sourceRoot, relativePath))
            .Where(path => !File.Exists(path))
            .ToArray();
        if (missingAssets.Length > 0)
        {
            throw new DirectoryNotFoundException(
                $"Required built Nightweaver.Web assets are missing from '{sourceRoot}': {string.Join(", ", missingAssets)}. Build Nightweaver.Web before running the integration tests.");
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(WebRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(directory.Path);
        builder.UseWebRoot(WebRoot);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nightweaver:ConfigPath"] = ConfigPath,
                ["Nightweaver:Root"] = HostRoot
            });
            configure?.Invoke(configuration);
        });
        builder.ConfigureTestServices(services =>
        {
            // TestServer has no separate native process. Retain real admission and
            // lease checks until this simulated process has stopped.
            services.AddSingleton(new Web.WebStartupAdmission((paths, project) =>
                payloadLeases.Add(Nightweaver.Upgrade.UpgradeInterlock.AcquireWeb(paths, project))));
            configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var lease in payloadLeases) lease.Dispose();
            directory.Dispose();
        }
    }
}
