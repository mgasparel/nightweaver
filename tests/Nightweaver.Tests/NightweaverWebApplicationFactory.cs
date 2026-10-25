using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace Nightweaver.Tests;

internal sealed class NightweaverWebApplicationFactory : WebApplicationFactory<Web.Program>
{
    private readonly TempDirectory directory = new();
    private readonly Action<IConfigurationBuilder>? configure;

    internal NightweaverWebApplicationFactory(Action<IConfigurationBuilder>? configure = null)
    {
        this.configure = configure;
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

    private void StageBuiltWebAssets()
    {
        var appDirectory = typeof(NightweaverWebApplicationFactory).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "NightweaverWebAppDir")?.Value
            ?? throw new InvalidOperationException("NightweaverWebAppDir assembly metadata is required to stage the built Web assets.");
        var sourceRoot = Path.Combine(appDirectory, "wwwroot");
        var requiredAssets = new[] { "index.html", Path.Combine("css", "site.css"), Path.Combine("js", "app.js") };
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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            directory.Dispose();
        }
    }
}
