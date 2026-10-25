using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nightweaver.Configuration;

namespace Nightweaver.Tests;

public sealed class WebHostIntegrationTests
{
    [Fact]
    public async Task HostBootsWithPlainTargetAndHealthReturnsStableJson()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var config = factory.Services.GetRequiredService<ProjectConfig>();
        Assert.Equal("web-test", config.Name.Value);
        Assert.Equal(factory.TargetRoot, config.TargetRepository.Path);
        Assert.Equal(factory.HostRoot, factory.Services.GetRequiredService<HostPaths>().Root);
        Assert.Equal(3, config.Concurrency.MaxConcurrentDelivery);
        Assert.Equal(2, config.Limits.MaxOperationalRetries);
        Assert.False(Directory.Exists(Path.Combine(factory.TargetRoot, ".beads")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(factory.HostRoot));
    }

    [Fact]
    public void HostPreservesProjectSettingsAndRelativeBootstrapPaths()
    {
        string? configPath = null;
        string? hostRoot = null;
        using var factory = new NightweaverWebApplicationFactory(configuration =>
        {
            // These remain relative to the caller, independently of the application's content root.
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nightweaver:ConfigPath"] = Path.GetRelativePath(Environment.CurrentDirectory, configPath!),
                ["Nightweaver:Root"] = Path.GetRelativePath(Environment.CurrentDirectory, hostRoot!)
            });
        });
        configPath = factory.ConfigPath;
        hostRoot = factory.HostRoot;
        File.WriteAllText(factory.ConfigPath, """
            {"name":"web-test","target_repository":{"path":"../target"},
             "concurrency":{"max_concurrent_planning":2},"limits":{"max_repair_cycles":4},
             "agent_defaults":{"cli":"claude"},"roles":{"planner":{"effort":"high"}}}
            """);
        using var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<ProjectConfig>();
        Assert.Equal(factory.ConfigPath, config.ConfigFilePath);
        Assert.Equal(factory.TargetRoot, config.TargetRepository.Path);
        Assert.Equal(factory.HostRoot, factory.Services.GetRequiredService<HostPaths>().Root);
        Assert.Equal(2, config.Concurrency.MaxConcurrentPlanning);
        Assert.Equal(4, config.Limits.MaxRepairCycles);
        Assert.Equal(AgentCli.Claude, config.AgentDefaults.Cli);
        Assert.Equal("high", config.Roles[AgentRole.Planner].Effort);
        Assert.Equal(config.AgentDefaults.Cli, config.Roles[AgentRole.Planner].Cli);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneHostReadsEnvironmentAndCommandLineOverrides(bool useCommandLine)
    {
        using var directory = new TempDirectory();
        var host = directory.CreateDirectory("host");
        directory.CreateDirectory("target");
        var configPath = directory.WriteFile("project.json", """
            {"name":"standalone","target_repository":{"path":"target"}}
            """);
        var appDirectory = typeof(WebHostIntegrationTests).Assembly.GetCustomAttributes(
            typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NightweaverWebAppDir").Value!;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "Nightweaver.Web.exe" : "Nightweaver.Web"),
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Child-only environment: installed Git, bd, and Agent CLIs cannot be found on PATH.
        startInfo.Environment["PATH"] = "";
        startInfo.Environment["Nightweaver__ConfigPath"] = useCommandLine ? "missing.json" : "project.json";
        startInfo.Environment["Nightweaver__Root"] = useCommandLine ? directory.Path : host;
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        if (useCommandLine)
        {
            foreach (var argument in new[] { "--Nightweaver:ConfigPath", configPath, "--Nightweaver:Root", host })
                startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) continue;
                address = line[(index + marker.Length)..].Trim();
                break;
            }
            if (address is null)
                Assert.Fail($"Web host exited before listening: {await stderr.WaitAsync(timeout.Token)}");
            using var client = new HttpClient { BaseAddress = new Uri(address!) };
            await AssertDashboardPageAsync(client, "/", timeout.Token);
            await AssertDashboardPageAsync(client, "/index.html", timeout.Token);
            await AssertStaticAssetAsync(client, "/css/site.css", ["text/css"], timeout.Token);
            await AssertStaticAssetAsync(client, "/js/app.js", JavaScriptMediaTypes, timeout.Token);
            using var response = await client.GetAsync("/health", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(timeout.Token));
            using var unknown = await client.GetAsync("/unknown", timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(host));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await stderr;
        }
    }

    [Fact]
    public async Task DashboardAndAssetsAreServedFromTheWebRoot()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();

        await AssertDashboardPageAsync(client, "/", TestContext.Current.CancellationToken);
        await AssertDashboardPageAsync(client, "/index.html", TestContext.Current.CancellationToken);
        await AssertStaticAssetAsync(client, "/css/site.css", ["text/css"], TestContext.Current.CancellationToken);
        await AssertStaticAssetAsync(client, "/js/app.js", JavaScriptMediaTypes, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingConfigPathPreventsStartup(string? path)
    {
        using var factory = new NightweaverWebApplicationFactory(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Nightweaver:ConfigPath"] = path }));
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Nightweaver:ConfigPath is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OmittedHostRootDefaultsToExecutableDirectory()
    {
        using var factory = new NightweaverWebApplicationFactory(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Nightweaver:Root"] = null }));
        using var client = factory.CreateClient();
        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), factory.Services.GetRequiredService<HostPaths>().Root);
    }

    [Fact]
    public void InvalidHostRootNamesTheBootstrapSetting()
    {
        using var factory = new NightweaverWebApplicationFactory(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Nightweaver:Root"] = "" }));
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Nightweaver:Root", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "not found")]
    [InlineData("{", "malformed JSON")]
    [InlineData("{\"name\":\"web-test\",\"target_repository\":{\"path\":\"../target\"},\"concurrency\":{\"max_concurrent_planning\":0}}", "at least 1")]
    public void InvalidProjectFilePreventsStartup(string? json, string diagnostic)
    {
        using var factory = new NightweaverWebApplicationFactory();
        if (json is null) File.Delete(factory.ConfigPath);
        else File.WriteAllText(factory.ConfigPath, json);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        Assert.Contains(factory.ConfigPath, error.Message, StringComparison.Ordinal);
        Assert.IsType<ProjectConfigurationException>(error.InnerException);
    }

    [Fact]
    public async Task StaticFilesAreRestrictedToWebRootAndUnknownRoutesRemainNotFound()
    {
        using var factory = new NightweaverWebApplicationFactory();
        await File.WriteAllTextAsync(Path.Combine(factory.WebRoot, "probe.txt"), "static probe", TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/probe.txt", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("static probe", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach (var path in new[] { "/unknown", "/api/unknown", "/private.txt", "/../private.txt", "/%2e%2e/private.txt", "/css/missing.css" })
        {
            using var missing = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    private static readonly string[] JavaScriptMediaTypes =
        ["text/javascript", "application/javascript", "application/ecmascript", "text/ecmascript"];

    private static async Task AssertDashboardPageAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var title = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(title.Success, "The dashboard document must contain a title element.");
        Assert.Equal("Nightweaver UI — Unified Factory Console & Bead Workspace", WebUtility.HtmlDecode(title.Groups[1].Value));
        Assert.Contains("Nightweaver", html, StringComparison.Ordinal);
        foreach (var section in new[] { "catch-up", "attention", "factory", "beads", "workspace" })
        {
            Assert.Contains($"id=\"{section}\"", html, StringComparison.Ordinal);
            Assert.Contains($"href=\"#{section}\"", html, StringComparison.Ordinal);
        }
    }

    private static async Task AssertStaticAssetAsync(
        HttpClient client,
        string path,
        IReadOnlyCollection<string> acceptedMediaTypes,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Content.Headers.ContentType?.MediaType, acceptedMediaTypes);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync(cancellationToken)));
    }

}
