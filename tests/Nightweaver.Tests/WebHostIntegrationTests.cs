using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nightweaver.Configuration;
using Nightweaver.Background;

namespace Nightweaver.Tests;

public sealed class WebHostIntegrationTests
{
    [Fact]
    public async Task ReservedUpgradePreventsStandaloneHostStartup()
    {
        using var factory = new NightweaverWebApplicationFactory();
        var paths = new HostPaths(factory.HostRoot);
        await Nightweaver.Upgrade.UpgradeInterlock.ReserveAsync(paths,
            new(1, paths.Root, new('a', 32), new('b', 64)), TestContext.Current.CancellationToken);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("suppresses startup", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Nightweaver.Upgrade.UpgradeInterlock.WebPayloadPath(paths, ProjectSlug.Parse("web-test"))));
    }

    [Fact]
    public async Task HostBootsWithPlainTargetAndHealthReturnsStableJson()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var config = factory.Services.GetRequiredService<ProjectConfig>();
        Assert.Equal("web-test", config.Name.Value);
        Assert.Equal(factory.TargetRoot, config.TargetRepository.Path);
        Assert.Equal(factory.HostRoot, factory.Services.GetRequiredService<HostPaths>().Root);
        Assert.Equal(3, config.Concurrency.MaxConcurrentDelivery);
        Assert.Equal(2, config.Limits.MaxOperationalRetries);
        Assert.False(Directory.Exists(Path.Combine(factory.TargetRoot, ".beads")));
        Assert.True(File.Exists(Nightweaver.Upgrade.UpgradeInterlock.WebPayloadPath(new HostPaths(factory.HostRoot), config.Name)));
        Assert.False(Directory.Exists(new HostPaths(factory.HostRoot).SupervisionRoot(config.Name)));

        using var unavailable = await client.GetAsync("/api/beads", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("application/problem+json", unavailable.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
        using var failedWrite = await client.PostAsync("/api/beads",
            new StringContent("{\"type\":\"feature\",\"priority\":2,\"title\":\"Unavailable target\",\"description\":\"Context\",\"acceptanceCriteria\":\"Criteria\"}",
                System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedWrite.StatusCode);
        Assert.Equal("application/problem+json", failedWrite.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", failedWrite.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(factory.TargetRoot,
            await failedWrite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        using var stillHealthy = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillHealthy.StatusCode);
        using var shell = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.DoesNotContain(factory.TargetRoot,
            await unavailable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
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
        await using var receiver = await OperationalTelemetryTests.Receiver.StartAsync();
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
        startInfo.Environment["NIGHTWEAVER_TELEMETRY_ENABLED"] = "true";
        startInfo.Environment["NIGHTWEAVER_TELEMETRY_ISOLATED_QA"] = "true";
        startInfo.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = receiver.Endpoint.AbsoluteUri;
        startInfo.Environment["OTEL_EXPORTER_OTLP_HEADERS"] = "";
        startInfo.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        if (useCommandLine)
        {
            foreach (var argument in new[] { "--Nightweaver:ConfigPath", configPath, "--Nightweaver:Root", host })
                startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(FixtureProcess.Start(process));
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
            using var studio = await client.GetAsync("/projects/standalone/overview", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, studio.StatusCode);
            Assert.Contains("Nightweaver · Studio", await studio.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
            await AssertStaticAssetAsync(client, "/vendor/purify.min.js", JavaScriptMediaTypes, timeout.Token);
            await AssertStaticAssetAsync(client, "/vendor/markdown-it.min.js", JavaScriptMediaTypes, timeout.Token);
            using var response = await client.GetAsync("/health", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync(timeout.Token));
            using var unknown = await client.GetAsync("/unknown", timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Throws<IOException>(() => Nightweaver.Execution.InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
                Nightweaver.Upgrade.UpgradeInterlock.WebPayloadPath(new HostPaths(host), ProjectSlug.Parse("standalone")), false));
            Assert.False(Directory.Exists(new HostPaths(host).SupervisionRoot(ProjectSlug.Parse("standalone"))));
            using var observation = await client.GetAsync("/api/operations/v1/projects/standalone/resources?key=factory", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, observation.StatusCode);
            await receiver.WaitForSignalAsync("traces");
            await receiver.WaitForSignalAsync("logs");
            while (!receiver.Captures.Any(capture => Encoding.UTF8.GetString(capture.Bytes).Contains("observation", StringComparison.Ordinal)))
                await Task.Delay(20, timeout.Token);
            ObservationTelemetryAssertions.AssertSanitized(receiver, configPath);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await stderr;
        }
    }

    [Fact]
    public async Task BuiltWebExecutableServesConfiguredBeadsExplorerOutsideTestHost()
    {
        using var target = new BeadsFixture("configured target");
        var expectedId = target.Create("Executable smoke needle", status: "in_progress", priority: 0);
        var config = JsonSerializer.Serialize(new { name = "web-smoke", target_repository = new { path = target.Path } });
        using var directory = new TempDirectory();
        var host = directory.CreateDirectory("host");
        var caller = directory.CreateDirectory("unrelated caller");
        var configPath = directory.WriteFile("settings/project.json", config);
        var appDirectory = typeof(WebHostIntegrationTests).Assembly.GetCustomAttributes(
            typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NightweaverWebAppDir").Value!;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "Nightweaver.Web.exe" : "Nightweaver.Web"),
            WorkingDirectory = caller,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["Nightweaver__ConfigPath"] = configPath;
        startInfo.Environment["Nightweaver__Root"] = host;
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["HOME"] = target.UserHome;
        startInfo.Environment["USERPROFILE"] = target.UserHome;
        startInfo.Environment["APPDATA"] = target.UserHome;
        startInfo.Environment["XDG_CONFIG_HOME"] = target.UserHome;
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(target.UserHome, "gitconfig");
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        using var process = new Process { StartInfo = startInfo };
        Assert.True(FixtureProcess.Start(process));
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                {
                    address = line[(index + marker.Length)..].Trim();
                    break;
                }
            }
            if (address is null)
                Assert.Fail($"Built Web executable exited before listening: {await stderr.WaitAsync(timeout.Token)}");

            using var client = new HttpClient { BaseAddress = new Uri(address!) };
            using var health = await client.GetAsync("/health", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var beads = await client.GetAsync("/api/beads?view=ACTIVE&q=SMOKE%20NEEDLE", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, beads.StatusCode);
            Assert.Equal("no-store", beads.Headers.CacheControl?.ToString());
            using var document = JsonDocument.Parse(await beads.Content.ReadAsStringAsync(timeout.Token));
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(expectedId, row.GetProperty("id").GetString());

            const string productTitle = "Executable Product Bead smoke";
            const string productDescription = "The shipped Web executable must create in its configured disposable tracker.";
            const string acceptanceCriteria = "The created feature is open, unassigned, and visible to real bd.";
            using var create = await client.PostAsync("/api/beads", new StringContent(JsonSerializer.Serialize(new
            {
                type = "feature",
                priority = 1,
                title = productTitle,
                description = productDescription,
                acceptanceCriteria
            }), Encoding.UTF8, "application/json"), timeout.Token);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            Assert.Equal("no-store", create.Headers.CacheControl?.ToString());
            using var createdDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync(timeout.Token));
            var createdId = createdDocument.RootElement.GetProperty("id").GetString()!;
            using var independentShow = JsonDocument.Parse(target.Run("bd", ["show", createdId, "--json"]));
            var shown = independentShow.RootElement.ValueKind == JsonValueKind.Array
                ? Assert.Single(independentShow.RootElement.EnumerateArray())
                : independentShow.RootElement;
            Assert.Equal(createdId, shown.GetProperty("id").GetString());
            Assert.Equal(productTitle, shown.GetProperty("title").GetString());
            Assert.Equal(productDescription, shown.GetProperty("description").GetString());
            Assert.Equal(acceptanceCriteria, shown.GetProperty("acceptance_criteria").GetString());
            Assert.Equal("feature", shown.GetProperty("issue_type").GetString());
            Assert.Equal(1, shown.GetProperty("priority").GetInt32());
            Assert.Equal("open", shown.GetProperty("status").GetString());
            using var ready = JsonDocument.Parse(target.Run("bd", ["--readonly", "--sandbox", "ready", "--limit", "0", "--json"]));
            Assert.Contains(ready.RootElement.EnumerateArray(), bead => bead.GetProperty("id").GetString() == createdId);

            await AssertDashboardPageAsync(client, "/", timeout.Token);
            Assert.Throws<IOException>(() => Nightweaver.Execution.InvocationRuntimeFiles.OpenExclusiveRuntimeFile(
                Nightweaver.Upgrade.UpgradeInterlock.WebPayloadPath(new HostPaths(host), ProjectSlug.Parse("web-smoke")), false));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await stderr;
        }
    }

    [Fact]
    public async Task PublishedStudioServesStandaloneAndBackgroundHosts()
    {
        using var directory = new TempDirectory();
        var root = directory.CreateDirectory("host");
        directory.CreateDirectory("target");
        var config = directory.WriteFile("project.json", """
            {"name":"embedded","target_repository":{"path":"target"}}
            """);
        var appDirectory = typeof(WebHostIntegrationTests).Assembly.GetCustomAttributes(
            typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NightweaverWebAppDir").Value!;
        var webProject = Path.GetFullPath(Path.Combine(appDirectory, "../../../Nightweaver.Web.csproj"));
        var published = directory.CreateDirectory("published");
        await TestProjectBuildGate.NightweaverProject.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var publish = await ProcessTool.RunAsync("dotnet",
                ["publish", webProject, "-c", "Release", "-o", published],
                Path.GetDirectoryName(webProject)!, null, TimeSpan.FromMinutes(2));
            Assert.True(publish.ExitCode == 0, publish.StandardOutputText + publish.StandardErrorText);
        }
        finally { TestProjectBuildGate.NightweaverProject.Release(); }
        Assert.False(Directory.Exists(Path.Combine(published, "Studio")));
        foreach (var dependency in new[] { "OpenTelemetry.dll", "OpenTelemetry.Api.dll", "OpenTelemetry.Exporter.OpenTelemetryProtocol.dll" })
            Assert.True(File.Exists(Path.Combine(published, dependency)), dependency);
        Assert.False(File.Exists(Path.Combine(published, "wwwroot", "js", "app.js")));
        Assert.False(File.Exists(Path.Combine(published, "wwwroot", "css", "site.css")));
        var log = Path.Combine(root, "web.log");
        await using var host = BackgroundWebHost.Start(
            Path.Combine(published, "Nightweaver.Web.exe"), root, config, "http://127.0.0.1:0", log);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string address;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.False(host.HasExited, "Embedded Web host exited before listening.");
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var match = Regex.Match(await reader.ReadToEndAsync(deadline.Token), @"Now listening on: (http://127\.0\.0\.1:\d+)");
            if (match.Success) { address = match.Groups[1].Value; break; }
            await Task.Delay(50, deadline.Token);
        }
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        await AssertDashboardPageAsync(client, "/", deadline.Token);
        await AssertDashboardPageAsync(client, "/projects/embedded/beads/ns-mbd.7?tab=history", deadline.Token);
        using var missing = await client.GetAsync("/api/missing", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var start = new ProcessStartInfo(Path.Combine(published, "Nightweaver.Web.exe"))
        {
            WorkingDirectory = directory.Path, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment["PATH"] = ""; // Published Studio has no Node runtime dependency.
        foreach (var argument in new[] { "--urls", "http://127.0.0.1:0", "--Nightweaver:Root", root, "--Nightweaver:ConfigPath", config })
            start.ArgumentList.Add(argument);
        using var standalone = FixtureProcess.Start(start) ?? throw new InvalidOperationException("Standalone Web host did not start.");
        var errors = standalone.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            string? standaloneAddress = null;
            while (await standalone.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                var match = Regex.Match(line, @"Now listening on: (http://127\.0\.0\.1:\d+)");
                if (match.Success) { standaloneAddress = match.Groups[1].Value; break; }
            }
            Assert.NotNull(standaloneAddress);
            using var publishedClient = new HttpClient { BaseAddress = new Uri(standaloneAddress) };
            await AssertDashboardPageAsync(publishedClient, "/", deadline.Token);
            await AssertDashboardPageAsync(publishedClient, "/projects/embedded/beads/ns-mbd.7?tab=output", deadline.Token);
        }
        finally
        {
            if (!standalone.HasExited) standalone.Kill(entireProcessTree: true);
            await standalone.WaitForExitAsync(TestContext.Current.CancellationToken);
            await errors;
        }
    }

    [Fact]
    public async Task DashboardAndAssetsAreServedFromTheWebRoot()
    {
        using var factory = new NightweaverWebApplicationFactory();
        using var client = factory.CreateClient();

        await AssertDashboardPageAsync(client, "/", TestContext.Current.CancellationToken);
        await AssertDashboardPageAsync(client, "/index.html", TestContext.Current.CancellationToken);
        await AssertStaticAssetAsync(client, "/vendor/purify.min.js", JavaScriptMediaTypes, TestContext.Current.CancellationToken);
        await AssertStaticAssetAsync(client, "/vendor/markdown-it.min.js", JavaScriptMediaTypes, TestContext.Current.CancellationToken);
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
        Assert.Equal("Nightweaver · Studio", WebUtility.HtmlDecode(title.Groups[1].Value));
        Assert.Contains("Nightweaver", html, StringComparison.Ordinal);
        Assert.Contains("id=\"root\"", html, StringComparison.Ordinal);
        var assets = Regex.Matches(html, "/studio/assets/[^\" ]+\\.(?:js|css)");
        Assert.Equal(2, assets.Count);
        foreach (Match asset in assets)
        {
            await AssertStaticAssetAsync(client, asset.Value,
                asset.Value.EndsWith(".css", StringComparison.Ordinal) ? ["text/css"] : JavaScriptMediaTypes,
                cancellationToken);
        }
        // Interactive behavior is covered by Studio component and isolated browser tests.

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
