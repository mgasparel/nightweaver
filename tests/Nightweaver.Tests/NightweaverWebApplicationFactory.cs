using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        ConfigPath = Path.GetFullPath(directory.WriteFile("settings/project.json", """
            {"name":"web-test","target_repository":{"path":"../target"}}
            """));
        directory.WriteFile("private.txt", "private sentinel");
    }

    internal string HostRoot { get; }
    internal string TargetRoot { get; }
    internal string WebRoot { get; }
    internal string ConfigPath { get; }

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
