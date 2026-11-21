using System.Security.Cryptography;
using System.Text.Json;
using Nightweaver.Inspection;
using Nightweaver.Application;
using Nightweaver.Upgrade;

namespace Nightweaver.Tests;

public sealed class PreparedReleaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "release-fixture-" + Guid.NewGuid().ToString("N"));
    private static readonly string Build = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(PreparedRelease).Assembly.Location)
        .ProductVersion!.Split('+').Last();

    public PreparedReleaseTests() => Directory.CreateDirectory(root);

    [Fact]
    public void CompletePairHasExactDigestAndInstalledStateIsPreserved()
    {
        var package = Package("valid");
        var original = Snapshot(package);
        var result = PreparedRelease.Inspect(package, false);
        Assert.Equal(Build, result.BuildVersion);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(package, PreparedRelease.InventoryName)))), result.Digest);
        Put(package, "run/demo/cleanup/sentinel", "preserve");
        PreparedRelease.Inspect(package, true);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(package, "run/demo/cleanup/sentinel")));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(package, file.Key)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("web\\asset.js")]
    [InlineData("file:stream")]
    [InlineData("projects/a.json")]
    [InlineData("RUN/owner")]
    [InlineData("web/CON.txt")]
    [InlineData("web/a.")]
    [InlineData("web//a")]
    [InlineData("nightweaver-release.json")]
    public void UnsafePayloadPathsRefuse(string path) => Assert.False(PreparedRelease.SafePayloadPath(path));

    [Theory]
    [InlineData("tamper")]
    [InlineData("delete")]
    [InlineData("extra")]
    [InlineData("mixed")]
    [InlineData("duplicate")]
    [InlineData("unknown-schema")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-json")]
    [InlineData("unknown-build")]
    [InlineData("null-files")]
    [InlineData("state-path")]
    [InlineData("oversize")]
    [InlineData("binary-mix")]
    [InlineData("empty-state")]
    [InlineData("studio-escape")]
    public void InvalidPackagesRefuseWithoutChangingAnyBytes(string mutation)
    {
        var package = Package(mutation);
        var inventoryPath = Path.Combine(package, PreparedRelease.InventoryName);
        var json = File.ReadAllText(inventoryPath);
        switch (mutation)
        {
            case "tamper": Put(package, "nightweaver.exe", "changed"); break;
            case "delete": File.Delete(Path.Combine(package, "nightweaver.exe")); break;
            case "extra": Put(package, "extra", "extra"); break;
            case "mixed": Put(package, "nightweaver-deployment.json", "{}"); Inventory(package); break;
            case "duplicate":
                var inventory = JsonSerializer.Deserialize<ReleaseInventory>(json, PreparedRelease.JsonOptions)!;
                WriteInventory(package, inventory with { Files = [.. inventory.Files, inventory.Files[0]] }); break;
            case "unknown-schema": File.WriteAllText(inventoryPath, json.Replace("\"schema_version\":1", "\"schema_version\":2")); break;
            case "unknown-field": File.WriteAllText(inventoryPath, json.Insert(1, "\"unknown\":true,")); break;
            case "duplicate-json": File.WriteAllText(inventoryPath, json.Insert(1, "\"schema_version\":1,")); break;
            case "unknown-build": File.WriteAllText(inventoryPath, json.Replace(Build, "unknown")); break;
            case "null-files": File.WriteAllText(inventoryPath, json[..json.IndexOf("\"files\":", StringComparison.Ordinal)] + "\"files\":null}"); break;
            case "state-path": File.WriteAllText(inventoryPath, json.Replace("nightweaver.exe", "run/owner")); break;
            case "oversize": File.WriteAllText(inventoryPath, new string(' ', 1024 * 1024 + 1)); break;
            case "binary-mix": Put(package, "web/Nightweaver.Web.dll", "foreign binary"); Inventory(package); break;
            case "empty-state": Directory.CreateDirectory(Path.Combine(package, "projects")); break;
            case "studio-escape": Put(package, "web/wwwroot/studio/index.html", "<script src=\"../../../escape.js\"></script>"); Inventory(package); break;
        }
        var before = Snapshot(package);
        var error = Record.Exception(() => PreparedRelease.Inspect(package, false));
        Assert.True(error is InvalidDataException or IOException or JsonException, error?.ToString());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(package, file.Key)));
    }

    [Theory]
    [InlineData("missing-config", "configuration-unsupported")]
    [InlineData("malformed-config", "configuration-unsupported")]
    [InlineData("missing-runtime", "runtime-unsupported")]
    [InlineData("legacy-package", "inspection-unavailable")]
    public async Task IncompleteInputsReturnBoundedRefusalsWithoutStateInitialization(string failure, string code)
    {
        var installed = Package("source");
        var candidate = Package("target");
        if (failure != "missing-config") Directory.CreateDirectory(Path.Combine(installed, "projects"));
        if (failure == "malformed-config") Put(installed, "projects/demo.json", "{private configuration invalid");
        if (failure == "legacy-package") File.Delete(Path.Combine(installed, PreparedRelease.InventoryName));
        var before = Snapshot(installed);
        var report = await UpgradePreflight.InspectAsync(installed, candidate, [], new Reader(root, failure == "missing-runtime"), CancellationToken.None);
        Assert.Contains(code, report.Refusals);
        Assert.False(report.Compatible);
        Assert.False(report.RollbackEligible);
        Assert.Equal(before.Count, Snapshot(installed).Count);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(installed, file.Key)));
    }

    [Fact]
    public async Task PreflightInventoriesAllProjectsAndRefusesUndeliveredCapabilitiesEvenIfAdvertised()
    {
        var installed = Package("installed");
        var candidate = Package("candidate");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        var common = Path.Combine(repository, ".git");
        Directory.CreateDirectory(common);
        foreach (var slug in new[] { "one", "two" })
            Put(installed, "projects/" + slug + ".json", JsonSerializer.Serialize(new
            { name = slug, target_repository = new { path = repository, default_branch = "main" } }));
        Put(installed, "run/one/supervision/state.json", "not proof of compatibility");
        var runner = new Reader(common);
        var before = Snapshot(installed);
        var report = await UpgradePreflight.InspectAsync(installed, candidate, [], runner, CancellationToken.None);
        Assert.False(report.Compatible);
        Assert.False(report.RollbackEligible);
        Assert.Equal(2, report.Projects.Length);
        Assert.All(report.Projects, p => Assert.StartsWith("windows:", p.PhysicalRepositoryIdentity));
        Assert.Contains("capability-unsupported", report.Refusals);
        Assert.Contains("state-unsupported", report.Refusals);
        Assert.Contains("rollback-incompatible", report.Refusals);
        Assert.NotNull(report.OldPackageDigest);
        Assert.NotNull(report.NewPackageDigest);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(installed, file.Key)));
        Assert.Equal(before.Count, Snapshot(installed).Count);
        Assert.Equal(3, runner.Calls);
    }

    [Fact]
    public async Task PublicCommandReturnsJsonRefusalWithoutLaunchingOwners()
    {
        var source = Package("command-source");
        var candidate = Package("command-candidate");
        Directory.CreateDirectory(Path.Combine(source, "projects"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var environment = new AppEnvironment(root, root, output, error);
        var exit = await NightweaverApp.RunAsync(["upgrade", "preflight", "--installation", source, "--candidate", candidate],
            environment, new Reader(root), CancellationToken.None);
        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("compatible").GetBoolean());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task PublicCommandRejectsUnknownOptionsBeforeReadingRoots()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new Reader(root);
        var exit = await NightweaverApp.RunAsync(["upgrade", "preflight", "--force", "true"],
            new AppEnvironment(root, root, output, error), runner, CancellationToken.None);
        Assert.Equal(2, exit);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task OverlappingRootsRefuseBeforeAnyToolCall()
    {
        var package = Package("overlap");
        var runner = new Reader(root);
        var result = await UpgradePreflight.InspectAsync(package, package, [], runner, CancellationToken.None);
        Assert.Contains("payload-collision", result.Refusals);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task UnsafeDirectoryAndHardLinkRefuse()
    {
        var package = Package("linked");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var junction = await ProcessTool.RunAsync("cmd.exe", ["/c", "mklink", "/J", Path.Combine(package, "alias"), outside],
            root, null, TimeSpan.FromSeconds(10));
        Assert.Equal(0, junction.ExitCode);
        Assert.ThrowsAny<Exception>(() => PreparedRelease.Inspect(package, false));
        Directory.Delete(Path.Combine(package, "alias"));
        var link = Path.Combine(root, "hardlink");
        Assert.True(CreateHardLink(link, Path.Combine(package, "nightweaver.exe"), IntPtr.Zero));
        Assert.ThrowsAny<Exception>(() => PreparedRelease.Inspect(package, false));
    }

    private string Package(string name)
    {
        var path = Path.Combine(root, name);
        Put(path, "nightweaver.exe", "cli");
        Put(path, "web/Nightweaver.Web.exe", "web");
        File.Copy(typeof(PreparedRelease).Assembly.Location, Path.Combine(path, "nightweaver.dll"));
        File.Copy(typeof(PreparedRelease).Assembly.Location, Path.Combine(path, "web/nightweaver.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Nightweaver.Web.dll"), Path.Combine(path, "web/Nightweaver.Web.dll"));
        Put(path, "nightweaver.runtimeconfig.json", "{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        Put(path, "web/Nightweaver.Web.runtimeconfig.json", File.ReadAllText(Path.Combine(path, "nightweaver.runtimeconfig.json")));
        Put(path, "nightweaver-deployment.json", JsonSerializer.Serialize(new { schema_version = 1, build_version = Build,
            cli_executable = "nightweaver.exe", web_executable = "web/Nightweaver.Web.exe" }));
        Put(path, "web/wwwroot/studio/index.html", "<script src=\"./assets/app.js\"></script>");
        Put(path, "web/wwwroot/studio/assets/app.js", "bundle");
        foreach (var prompt in new[] { "implementer", "merger", "plan-challenger", "planner", "qa", "reviewer" }) Put(path, "prompts/" + prompt + ".md", prompt);
        Inventory(path);
        return path;
    }

    private static void Inventory(string path) => WriteInventory(path, new(1, Build, ["paired-inventory-v1", "root-interlock-v1"],
        Directory.GetFiles(path, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != PreparedRelease.InventoryName)
            .Select(f => new ReleaseFile(Path.GetRelativePath(path, f).Replace('\\', '/'), new FileInfo(f).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))).ToArray()));
    private static void WriteInventory(string path, ReleaseInventory inventory) =>
        File.WriteAllText(Path.Combine(path, PreparedRelease.InventoryName), JsonSerializer.Serialize(inventory, PreparedRelease.JsonOptions));
    private static void Put(string path, string relative, string content)
    {
        var file = Path.Combine(path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }
    private static Dictionary<string, byte[]> Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(path, f), File.ReadAllBytes);
    private sealed class Reader(string common, bool missingRuntime = false) : IProcessRunner
    {
        private static readonly string[] GitArguments = ["rev-parse", "--path-format=absolute", "--git-common-dir"];
        internal int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.True(request.FileName == "dotnet" || request.Arguments.SequenceEqual(GitArguments));
            return Task.FromResult(new ProcessResult(missingRuntime ? 1 : 0, request.FileName == "dotnet"
                ? "Microsoft.NETCore.App 10.0.8 [runtime]\nMicrosoft.AspNetCore.App 10.0.8 [runtime]" : common, ""));
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    public void Dispose() => Directory.Delete(root, true);
}
